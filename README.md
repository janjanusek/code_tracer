# CodeTracer

A local C# tool built on **Roslyn** (`MSBuildWorkspace`) + a **small local model**
(Ollama, default `gemma4:latest`, **CPU-only**, no GPU). It was built to understand a
large, **15+ year old C# spaghetti** codebase on a **GPU-less VDI**, where a cloud model
**cannot** be used for privacy reasons.

Roslyn does the **precise** analysis (symbols, references, call graph) — nothing is guessed
by the model. The model only **explains the code it is given** and (in trace mode) decides
which tool to call.

It has **two modes**:

| mode | what it does | typical question |
|---|---|---|
| **`explain`** | explains one method step by step and **pulls in its dependencies** ("how it all fits together") | *"tax is calculated here — explain it and how it works together"* |
| **`trace`** | finds the **whole call chain** from an endpoint (B) down to a target class (A) and prints it | *"how does execution reach this point?"* |

> It does not modify code — that's the engineer's job. The tool is for **understanding** and **mapping**.

---

## Requirements

1. **.NET 10 SDK** — `dotnet --version`
2. **Ollama ≥ 0.5** (structured outputs) — `ollama --version`
3. Model: `ollama pull gemma4:latest`

With 32 GB RAM and a 7B model, `--num-ctx` of 8–16K is safe; higher risks OOM.

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
# "tax is calculated here - explain it and pull in the dependencies"
dotnet run -- explain -s ./Big.sln --method "TaxEngine.Calculate" --depth 1

# alternatively by file + line (any line inside the method works)
dotnet run -- explain -s ./Big.sln --file ./Tax/TaxEngine.cs --line 1234 --depth 1

# save the explanation to a .md file
dotnet run -- explain -s ./Big.sln --method "TaxEngine.Calculate" --out tax.md
```

### How it works (and why it handles even a 5000-line class)
Roslyn extracts **only** the target method + relevant context — the model **never receives
the whole file**:
- method signature + full body
- parameters and their types
- fields/properties the method **reads** / **writes**
- list of **called methods** (callees) with signature and origin
- doc comment, if present
- **`--depth N`**: additionally pulls in **truncated bodies of called methods** within the
  solution (BFS to depth N, hard-capped at 8 methods × 40 lines) — so the model can explain
  *how the method and its dependencies fit together*. Default is `--depth 1`; `--depth 0`
  = the method alone (fastest). At `--depth ≥ 1` it also lists the method's **callers**
  ("REACHED FROM"), answering *how is this reached*.

Add **`--question "…"`** (alias `--ask`) to point at the code and ask something specific —
it's answered first, before the general walkthrough.

The explanation is produced in a **single unconstrained call** (the model's strength =
explaining code it is handed). If the method is extremely long (> 400 lines) it is split
into **logical blocks**, explained block by block, with a final summary.

`--goal "..."` (optional) adds a change proposal with a code snippet — done as a **separate
call** so a verbose explanation doesn't eat its token budget. Not needed for plain understanding.

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

## All parameters

| parameter | mode | default | description |
|---|---|---|---|
| `-s, --solution` | both | — | path to the `.sln` (required) |
| `--method` | explain | — | `"Class.Method"` to explain |
| `--file` + `--line` | explain | — | alternative to `--method` |
| `--depth` | explain | `1` | depth of pulled-in dependency bodies (0 = method only; ≥1 also shows callers) |
| `--question` / `--ask` | explain | — | a specific question to focus the explanation on (answered first) |
| `--goal` | explain | — | (optional) change proposal with a code snippet |
| `--out` | explain | — | save the output to a file |
| `-f, --target-file` | trace | — | file of the target class A |
| `-e, --endpoint` | trace | — | starting point B (route / `Class.Method`) |
| `--max-steps` | trace | `25` | agent step limit |
| `--no-llm` | trace | off | deterministic only — `find_path` over candidate pairs, no model |
| `--summary` | trace | off | short free-text summary of the path at the end |
| `-m, --model` | both | `gemma4:latest` | model name |
| `-a, --api` | both | `http://localhost:11434` | server base URL |
| `--api-style` | both | `ollama` | `ollama` \| `openai` (LM Studio) |
| `--num-ctx` | both | `8192` | context window size |
| `--num-predict` | both | `2048` | token cap for explain |
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
