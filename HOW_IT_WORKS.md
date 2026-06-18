# CodeTracer — how it works

A guide to the internals: what happens between your command and the output, and why it's
built this way. For usage see `README.md`; for the command-prep agent see `AGENT.md`.

---

## Guiding principle: Roslyn is the source of truth, the model is secondary

The codebase is large, old, and runs on a GPU-less VDI with a small local model
(`gemma4:latest`). Small models on CPU are **weak at autonomous navigation** but
**good at explaining code they are handed**. So the design splits responsibilities:

- **Roslyn** (`MSBuildWorkspace`) does everything that must be *correct*: resolving symbols,
  finding callers/callees, computing the call graph, the shortest path. These are exact,
  not guessed.
- **The model** only does what it's good at: turning a precisely-prepared chunk of code into
  a readable explanation, and (in trace) picking which tool to call next.

Every place where the model could produce something wrong is backed by a deterministic
Roslyn result. That's the whole architecture in one sentence.

```
            ┌─────────────┐     prepares exact context / runs exact graph ops
   you ───► │  CodeTracer │ ──────────────────────────────► ┌──────────┐
            │  (Program)  │                                  │  Roslyn  │
            └──────┬──────┘ ◄────────────────────────────── └──────────┘
                   │  hands a compact, correct chunk to…
                   ▼
            ┌─────────────┐   explains / picks a tool (never the source of truth)
            │ local model │
            │  (Ollama)   │
            └─────────────┘
```

---

## Components

| file | responsibility |
|---|---|
| `Program.cs` | parse args, pick the command (`explain`/`trace`), load the solution, wire things up |
| `RoslynIndex.cs` | all Roslyn analysis: symbols, callers, callees, `find_path`, `BuildMethodContext` |
| `LlmClient.cs` | HTTP to Ollama `/api/chat` (structured outputs) or an OpenAI-compatible server |
| `Agent.cs` | trace mode: deterministic pre-flight, the model loop, JSON enforcement, escalation |
| `Explainer.cs` | explain mode: per-method prompts, deep call-chain walk + end-to-end synthesis |

Startup detail: `MSBuildLocator.RegisterDefaults()` must run **before** any Roslyn/MSBuild
type is touched, so all real work lives in methods, not inline in `Program.cs`.

---

## `explain` mode — pipeline

```
--method "C.M"  ─┐
                 ├─► RoslynIndex.BuildMethodContext ──► MethodContext ──► Explainer ──► Ollama ──► text
--file + --line ─┘        (extract ONLY the method)        (compact)       (prompt)    (1 call)
```

### 1. Roslyn builds a *compact* context (never the whole file)
`BuildMethodContext` resolves the method to an `IMethodSymbol` and extracts only:
- the **signature** + **full body** of that method,
- **parameters** and their types,
- **fields/properties read vs. written** (it walks the body, classifies each member access
  as a write if it's the left side of an assignment, `ref`/`out` arg, or `++`/`--`),
- **callees** — methods it calls, with signature + where they're declared,
- the **doc comment**, if any.

This is why a 5000-line class is no problem: the model sees one method, never the file.

### 2. The model explains it
- **`--depth 0`** → one unconstrained call (`format` = null), free text. Fast.
- **`--depth N ≥ 1`** → **deep, layer by layer**. Roslyn walks the **call chain** from the
  entry method down through its in-solution callees (BFS to depth `N`, capped by
  `--max-methods`, default 8). **Each method is explained in its own call** with its full
  body (not a truncated snippet), then a final **end-to-end synthesis** describes how it all
  flows together. So `N+1` model calls — deeper depth/`--max-methods` = more thorough on
  non-trivial code. This is the fix for "depth went shallow": depth now drives a real
  per-method walk, not one summarising pass.

Knobs: `--num-predict` (output cap, default 4096; chain nodes get a smaller per-node budget,
the synthesis gets the full cap), `--temperature` (default 0.2).

### 3. Special cases
- **`--question "…"`** is placed *first* in the task, so it's answered even if the output is
  truncated by `num_predict`.
- **`--goal "…"`** (change proposal) is a **separate** call, so a long explanation can't eat
  its token budget. Output gets a `## Change proposal` section.
- **Long methods (> 400 lines)** are split into top-level **blocks** (~120 lines each),
  explained block-by-block, then a final summary call stitches it together.
- The output always starts with a self-describing header (method, location, signature) so a
  saved `.md` makes sense on its own.

---

## `trace` mode — pipeline

```
-f target.cs ─┐                                  ┌─ pre-flight: find_path over pairs ─► PATH FOUND? ─► print, done
-e endpoint ──┤─► Bootstrap (candidate pairs) ──►┤
              │                                   └─ else (and not --no-llm): model loop ─► find_path/… ─► finish
              ▼
        RoslynIndex.find_path = BFS over CALLERS (deterministic shortest path)
```

### 1. Bootstrap — deterministic candidate pairs
From the endpoint and target file, `Bootstrap` builds `(fromClass, fromMethod) → (toClass,
toMethod)` candidate pairs. For a Razor `.cshtml` endpoint it knows the handler lives in the
`.cshtml.cs` page model (`OnGet/OnPost…`), and it prioritises target methods that look like
entry points (`*Async`, `Build*`, `Generate*`). Up to 24 pairs.

### 2. Pre-flight — try the deterministic answer first
Before involving the model, trace runs `find_path` over those pairs. `find_path` is a **BFS
upward over callers** in Roslyn (`SymbolFinder.FindCallersAsync`) from the target until it
reaches the source — an exact shortest path. If a pair connects (the common case), the path
is printed with **zero model calls**. `--no-llm` stops here regardless.

**Brute force (`--all-paths` / `--brute` / `--deep`)**: instead of returning the first
connecting pair, it runs `find_path` over **every** candidate pair (with a wider graph
budget, 20000 nodes) and prints **all distinct paths**. For non-trivial code where one
shortest path isn't the whole picture.

### 3. The model loop — only for the hard cases
If no pair connects (calls through interface/DI/reflection/events that BFS-over-callers can't
bridge), the model loop runs. Each step the model picks one tool. The deterministic pre-flight
result is cached and used as the final answer if the model doesn't do better.

### 4. Token-level JSON enforcement
This is the core of why a small model can't break the format. Each action request sends a
flat **JSON schema** as Ollama's `format`. Ollama compiles it to a grammar and **masks
invalid tokens during sampling**, so the reply is **structurally guaranteed valid JSON** —
`Agent` parses a clean `{"tool":…,"args":{…}}` object with **no text-parsing fallback**.

The schema is deliberately **flat** (one `tool` enum + one `args` object with all optional
fields, no `anyOf`/`oneOf`, no free-text `thought` field) because small models and grammars
handle nesting and free text inside constrained JSON poorly (the latter can trigger a
repetition loop).

### 5. The schema guarantees *structure*, not *values*
A grammar can't make the values sensible — a small model may under-fill `find_path`'s args
(some models more than others). So on top of the grammar:
1. **Validate** the args make sense for the tool; if not, send a short **correction turn** and
   retry (max 2×).
2. **Loop detection**: if the model repeats an identical call, escalate within **2 steps**.
3. **Deterministic escalation**: when the model can't produce a usable call, fall back to the
   cached pre-flight `find_path`. It never runs 25 wasted iterations.

This is why the model "driving badly" is fine — Roslyn delivers the path either way.

---

## `LlmClient` — talking to the model

- Default is **native Ollama** `POST /api/chat`, which supports both `format` (a JSON-schema
  object) and `options` (`temperature`, `num_ctx`, `num_predict`, `repeat_penalty`).
- The base URL is normalised: `http://host:11434`, `…/v1`, trailing slash — all accepted.
- **`--api-style openai`** switches to `POST /v1/chat/completions` with `response_format`
  (for LM Studio). Note: `num_ctx` can't be set over the OpenAI endpoint.
- On startup it best-effort reads `/api/version` and warns if it can't confirm Ollama ≥ 0.5
  (structured outputs need it).

---

## Tuning knobs (CPU-friendly defaults)

| knob | default | effect |
|---|---|---|
| `--num-ctx` | 16384 | context window. 8–16K is safe with 32 GB RAM + 7B; higher risks OOM |
| `--num-predict` | 4096 | explain output cap. Action selection uses a fixed small cap (512) |
| `--temperature` | 0 / 0.2 | **0** for decisions/structure (deterministic); 0.2 for explanation prose |
| `--depth` | 1 | explain call-chain depth; `0` = fastest (just the method), higher = deeper logic |
| `--max-methods` | 8 | cap on methods explained in the chain — raise to go deeper/wider |
| `--all-paths` | off | trace: enumerate ALL paths (brute force), not just the first |
| `--no-llm` | off | trace without the model — fastest, fully deterministic |
| `-m, --model` | gemma4:latest | swap models; smaller ones are faster but may fill the trace schema worse |

Everything is bounded (tool outputs truncated, explain chain ≤ `--max-methods`, BFS node
budget capped) so a giant spaghetti codebase can't blow up context or memory.

---

## End-to-end, in one breath

`explain`: Roslyn carves out the entry method and walks the call chain down → each method is
explained on its own, then synthesised into the end-to-end logic. `trace`: Roslyn bootstraps
candidate pairs → a deterministic BFS finds the exact path(s) (usually with no model at all;
`--all-paths` enumerates every one) → the model only helps navigate the cases pure call-graph
BFS can't reach, and even then a deterministic result backs it up.
