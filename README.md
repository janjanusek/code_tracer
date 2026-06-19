# CodeTracer

A local C# tool built on **Roslyn** (`MSBuildWorkspace`) + a **small local model**
(Ollama, default `gemma4:latest`, **CPU-only**, no GPU). It was built to understand a
large, **15+ year old C# spaghetti** codebase on a **GPU-less VDI**, where a cloud model
**cannot** be used for privacy reasons.

> **Runs on modest hardware.** Built and used daily on a GPU-less corporate VDI
> (8 cores / 32 GB RAM, CPU-only) with a **Q4-quantized local model** — no GPU,
> no cloud, fully offline.

Roslyn does the **precise** analysis (symbols, references, call graph) — nothing is guessed
by the model. The model only **explains the code it is given** and (in trace mode) decides
which tool to call.

It has **two modes**:

| mode | what it does | typical question |
|---|---|---|
| **`explain`** | explains one method step by step and **pulls in its dependencies** ("how it all fits together") | *"tax is calculated here — explain it and how it works together"* |
| **`trace`** | finds the **whole call chain** from an endpoint (B) down to a target class (A) and prints it | *"how does execution reach this point?"* |

> It does not modify code — that's the engineer's job. The tool is for **understanding** and **mapping**.

**Docs:** [full manual](MANUAL.md) · [how it works](HOW_IT_WORKS.md) · [command-prep agent guide](AGENT.md)

---

## Requirements

1. **.NET 8 SDK or newer** — `dotnet --version`. The project targets **`net8.0`**, so it builds
   on a .NET 8 SDK (e.g. a VDI) *and* a .NET 10 SDK. `RollForward=Major` lets the net8 binary
   run on a newer runtime if no .NET 8 runtime is installed.
2. **Ollama ≥ 0.5** (structured outputs) — `ollama --version`
3. Model: `ollama pull gemma4:latest`

On a CPU-only machine with 32 GB RAM, a `--num-ctx` of 8–16K is safe; higher risks OOM.
Smaller / more quantized models (e.g. a Q4 build) run faster but may fill the trace schema
less reliably — the deterministic `find_path` fallback covers that.

### Server (Ollama)
```bash
ollama serve            # http://localhost:11434
```
or via the bundled `docker-compose.yml` (starts Ollama + pulls the model):
```bash
docker compose up -d
```

---

## Build & run

```bash
dotnet build
dotnet run -- <command> [options]      # command = explain | trace
dotnet run -- --help
```

The default API is `http://localhost:11434` (native Ollama `/api/chat`). It also accepts
the `.../v1` form — it gets normalized. For **LM Studio**, add `--api-style openai`
(e.g. `-a http://localhost:1234 --api-style openai`).

---

## `explain` mode — understanding code (the main value)

```bash
# explain the WHOLE logic deeply - follow the call chain down and explain every layer
dotnet run -- explain -s ./Big.sln --method "TaxEngine.Calculate" --depth 3 --max-methods 15

# alternatively by file + line (any line inside the method works)
dotnet run -- explain -s ./Big.sln --file ./Tax/TaxEngine.cs --line 1234 --depth 3

# just this method, fast (one call)
dotnet run -- explain -s ./Big.sln --method "TaxEngine.Calculate" --depth 0

# save the explanation to a .md file
dotnet run -- explain -s ./Big.sln --method "TaxEngine.Calculate" --depth 3 --out tax.md
```

### How it works (and why it handles even a 5000-line class)
Roslyn extracts **only** the target method + relevant context — the model **never receives
the whole file**. Per method it provides: signature + full body, parameters and types,
fields/properties **read** / **written**, **called methods** (callees) with origin, and the
doc comment.

**`--depth N` follows the call chain and explains the whole logic** — not one shallow pass.
Roslyn walks from the entry method down through its in-solution callees (BFS to depth `N`,
capped by `--max-methods`, default 8) and **each method is explained on its own** (its full
body, full attention), then a final **end-to-end synthesis** ties the layers together. So on
non-trivial code you get real depth, layer by layer. `--depth 0` = just the method, one fast
call. To go deeper/wider on spaghetti, **raise `--max-methods`** (each method = one model call).

Add **`--question "…"`** (alias `--ask`) to point at the code and ask something specific —
it's answered first, before the general walkthrough. If a single method is extremely long
(> 400 lines) it's split into **logical blocks**, explained block by block, with a summary.

`--goal "..."` (optional) adds a change proposal with a code snippet — done as a **separate
call** so a verbose explanation doesn't eat its token budget. Not needed for plain understanding.

### Example output (deep, multi-level)

A **real** run — CodeTracer explaining **its own** structured-output flow, **3 levels deep**,
`gemma4:latest` on CPU. Each method on the chain is explained in its own pass, then an
end-to-end synthesis ties them together:

```bash
dotnet run -- explain -s CodeTracer.sln --method "Agent.GetAction" --depth 3 --max-methods 9 \
  --repo-url https://github.com/janjanusek/code_tracer/blob/main
```

```markdown
# Agent.GetAction  (Agent.cs:214)
_Deep explanation following the call chain (6 methods)._

## L0 · Agent.GetAction
This method attempts to extract a structured action (a tool name and its arguments) from a
large language model's output, ensuring the action is valid… It handles failures by
iteratively prompting the model for corrections (1 initial + 2 correction attempts).

## L1 · LlmClient.ChatAsync      → the actual HTTP call to Ollama (/api/chat)
## L1 · Agent.ValidateArgs       → per-tool argument validation
## L2 · LlmClient.BuildOllama    → builds the request body with the JSON schema (format)
## L2 · LlmClient.BuildOpenAI    → the OpenAI-compatible variant (response_format)
## L2 · LlmClient.Truncate       → trims long error bodies

## End-to-end logic
(how a request flows L0 → L1 → L2 and back: the schema-constrained call, validation,
 the correction loop, and the deterministic escalation when the model can't comply)
```

**→ Full examples in the repo:**
[`explain-agent-getaction.md`](examples/explain-agent-getaction.md) (3 levels) ·
[`explain-deep-runasync.md`](examples/explain-deep-runasync.md) (12 methods over 3 levels — the
whole agent loop, each method explained, then an end-to-end synthesis).

---

## `trace` mode — the whole call chain (A ↔ B)

```bash
dotnet run -- trace -s ./Big.sln -f ./Pricing/PricingEngine.cs -e "POST /orders"
dotnet run -- trace -s ./Big.sln -f ./Pricing/PricingEngine.cs -e "OrdersController.Create"
```

`-e/--endpoint` is the starting point B — a route (`POST /orders`) or `Class.Method`
(`OrdersController.Create`). Direction doesn't matter: the output is the **whole path
printed hop by hop** (`Class.Method  file:line`), so you can follow it by eye. For a
`.cshtml` endpoint the handler lives in `.cshtml.cs` (`OnGet/OnPost…`).

Trace runs a **deterministic pre-flight first**: it tries `find_path` over the bootstrapped
candidate pairs immediately. If they connect (the common case), it prints the path with **no
model calls at all** — fast and fully reliable on CPU. Only when the pairs don't connect does
it hand over to the model loop (for interface/DI/reflection cases that need navigation). Use
**`--no-llm`** to skip the model entirely and stay purely deterministic.

For non-trivial code where one shortest path isn't enough, **`--all-paths`** (aliases
`--brute` / `--deep`) brute-forces **every** candidate pair and prints **all distinct paths**
(with a wider graph budget), not just the first. Combine with `--no-llm` for a fast, complete,
deterministic map.

### Example output

A **real** brute-force run over CodeTracer's own code — **all 15 paths** from `Agent.cs` to
`RoslynIndex.cs`, deterministic, **zero model calls**:

```bash
dotnet run -- trace -s CodeTracer.sln -f RoslynIndex.cs -e Agent.cs --all-paths --no-llm
```

```
FOUND 15 distinct path(s) [brute-force]:

### Path 1:  Agent.RunAsync  ->  RoslynIndex.Rel
PATH FOUND (4 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)       Agent.cs:447  -->
  3. RoslynIndex.FindSymbol(String)            RoslynIndex.cs:126  -->
  4. RoslynIndex.Rel(Location)                 RoslynIndex.cs:38
...
### Path 9:  Agent.RunAsync  ->  RoslynIndex.FindCallers
  1. Agent.RunAsync   →   2. Agent.TryAutoPath   →   3. RoslynIndex.FindCallers
```

**→ Full example in the repo:** [`examples/trace-agent-to-roslynindex.md`](examples/trace-agent-to-roslynindex.md)
— all 15 distinct paths, each hop with its `file:line`.

**See the actual code between the hops:** add **`--with-bodies`** (`--code`) and each method's
source is shown **from its start down to the line where it calls the next hop** — so you read
the real flow, not just names. Signatures include **parameter names** and each call site shows
the **argument → parameter** mapping. With `--repo-url`, every location and call site is a
clickable link to the file in the repo:

```bash
dotnet run -- trace -s CodeTracer.sln -f RoslynIndex.cs -e Agent.cs --no-llm --with-bodies \
  --repo-url https://github.com/janjanusek/code_tracer/blob/main
```

Add **`--annotate`** (`--why`) for a short LLM **"why" note per hop** — it sees the prior steps
(so the notes are depth-aware) and stays silent on trivial hops. Add **`--summary`** for a final
**Summary** section (purpose, dependencies, good-to-know) included in the saved file. **→ Examples:**
[`trace-with-bodies.md`](examples/trace-with-bodies.md) ·
[`trace-with-bodies-annotated.md`](examples/trace-with-bodies-annotated.md).

### Token-level JSON enforcement (why a small model can't "break" the format)
The model's action selection uses **Ollama structured outputs**: the request carries a
`format` = flat **JSON schema**, from which Ollama builds a grammar and **masks invalid
tokens** during sampling. The output is therefore **structurally guaranteed valid JSON** —
the parser has no text fallback, it receives a clean `{"tool":…,"args":{…}}` object.

The schema guarantees **format**, not **value quality** — so additionally:
1. **Validation + correction retry** (max 2×): if the values for the chosen tool don't make sense.
2. **Deterministic candidate bootstrap** (from `.cshtml`→`.cshtml.cs` handlers and the
   target file's methods) and **loop detection**: when the model repeats an action or can't
   produce a usable call, the agent escalates within **2 steps** to a deterministic
   `find_path` (BFS over callers in Roslyn). It never runs 25 wasted repetitions.

> **Note on small models:** small models can under-fill `args` on this flat schema (the grammar
> permits it; some models do it more than others). That's why the deterministic `find_path`
> fallback is key — **it** produces the correct path; the model only navigates when it can.
> Roslyn is the source of truth.

Agent tools: `find_path`, `find_callers`, `find_callees`, `get_method`, `find_symbol`,
`outline`, `read_file`, `grep`, `finish`.

---

## Choosing `--depth` and `--max-methods`

These two knobs decide how much of the call chain you get — you set them yourself, no AI needed
to work it out:

- **`--depth N`** — how many *levels* down to follow. `0` = just the method; `1` = the method
  plus what it directly calls; `N` = N hops deep. In the output, levels are labelled `L0, L1, …`.
- **`--max-methods M`** — the *total* number of methods to explain (the budget). The walk is
  **breadth-first**, so it fills level by level (all of L0, then all of L1, …). If `M` runs out
  at a shallow level, the deeper levels are never reached — even with a big `--depth`.

So **`--depth` says how deep you're *allowed* to go; `--max-methods` says how many methods you
can *afford*.** To actually reach level N, `M` must be big enough to hold everything at levels
0…N-1 first. For a method that calls 8 others, `--depth 5 --max-methods 8` only ever shows L0+L1
(8 methods used up) — raise `--max-methods` to go deeper.

| you want | command |
|---|---|
| just this one method (fast) | `--depth 0` |
| the method + its direct calls | `--depth 1 --max-methods 8` |
| a few levels of real logic | `--depth 3 --max-methods 12` |
| deep prod logic, brute depth | `--depth 10 --max-methods 40` |

When the budget runs out you'll see `call chain capped at M methods - raise --max-methods` on
stderr — that's your cue to bump `--max-methods` (and/or `--depth`).

## Use it without any model (deterministic, offline)

Roslyn does the exact analysis; the local model is **optional**. Two fully model-free workflows
(they work even if Ollama isn't running):

```bash
# 1) Map the call paths - pure Roslyn, no model, instant:
dotnet run -- trace -s Your.sln -f Target.cs -e "POST /orders" --all-paths --no-llm --out paths.md

# 2) Dump the method + its WHOLE call chain (source + structure) to a file - with a short peek
#    per method and clickable repo links - then read it yourself or paste into a bigger model:
dotnet run -- explain -s Your.sln --method "Some.Method" --depth 10 --max-methods 40 \
  --no-llm --peek 15 --repo-url https://github.com/you/repo/blob/main --out context.md
```

`--peek N` keeps each method body to N lines; `--repo-url` turns every `file:line` into a link
to the full file in your repo (path is taken relative to the `.sln` directory).

---

## All parameters

| parameter | mode | default | description |
|---|---|---|---|
| `-s, --solution` | both | — | path to the `.sln` (required) |
| `--method` | explain | — | `"Class.Method"` to explain |
| `--file` + `--line` | explain | — | alternative to `--method` |
| `--depth` | explain | `1` | how deep to follow the call chain; each method explained + end-to-end synthesis (0 = method only) |
| `--max-methods` | explain | `8` | cap on methods explained in the chain — raise for deeper/wider coverage |
| `--question` / `--ask` | explain | — | a specific question to focus the explanation on (answered first) |
| `--goal` | explain | — | (optional) change proposal with a code snippet |
| `--out` | explain | — | save the output to a file |
| `-f, --target-file` | trace | — | file of the target class A |
| `-e, --endpoint` | trace | — | starting point B (route / `Class.Method`) |
| `--from` / `--to` | trace | — | direct mode: path from `Class.Method` to `Class.Method` |
| `--all-paths` / `--brute` | trace | off | enumerate ALL distinct paths, not just the first |
| `--with-bodies` / `--code` | trace | off | insert each method's code (start → call to next hop) between steps; show param names + arg→param mapping |
| `--annotate` / `--why` | trace | off | short LLM "why" note per hop (depth-aware); implies `--with-bodies` |
| `--no-llm` | trace+explain | off | trace: deterministic only; explain: dump Roslyn context, no model |
| `--max-steps` | trace | `25` | agent step limit |
| `--summary` | trace | off | LLM summary (purpose, dependencies, good-to-know) + an "In plain words" (ELI10) recap; in the saved file |
| `--repo-url` | trace+explain | — | render locations as clickable links to the repo |
| `--peek` | explain | — | in the `--no-llm` dump, show first N lines per method instead of the full body |
| `-m, --model` | both | `gemma4:latest` | model name |
| `-a, --api` | both | `http://localhost:11434` | server base URL |
| `--api-style` | both | `ollama` | `ollama` \| `openai` (LM Studio) |
| `--num-ctx` | both | `16384` | context window size |
| `--num-predict` | both | `4096` | token cap for explain output |
| `--temperature` | both | `0` / `0.2` | 0 for decisions, 0.2 for explain |

---

## Common issues

- **Solution won't load** → run `dotnet restore` + `dotnet build` on the *target* solution
  first. `MSBuildWorkspace` must be able to open it. `[workspace] …` warnings during load are
  normal (missing analyzers / NuGet advisories) — it continues.
- **`.slnx`** (the new SDK format) may not open in older Roslyn — use a classic `.sln`.
- **`find_path` finds nothing** → the call goes through interface/DI/reflection/events. Pass
  `--endpoint` as a concrete implementation method.
- **CPU is slow** → `explain --depth 0`, lower `--num-predict`, or a smaller model.

---

## Output language

All output (explanations, the traced path, console messages) is in **English**. The
model-facing prompts live in `Explainer.cs` and `Agent.cs`; the logic is language-agnostic,
so you can switch the output language by translating those prompt strings. (Internal code
comments are in Slovak.)

---

## Layout

```
Program.cs       arg parsing, command dispatch (explain/trace), MSBuildLocator, wiring
LlmClient.cs     HTTP client: Ollama /api/chat (structured outputs) + OpenAI-compatible
RoslynIndex.cs   Roslyn analysis: symbols, call graph, find_path, BuildMethodContext (+deps)
Agent.cs         trace: ReAct loop, token-level JSON, bootstrap + auto-escalation
Explainer.cs     explain: compact method context -> explanation (+ block-splitting long methods)
```
