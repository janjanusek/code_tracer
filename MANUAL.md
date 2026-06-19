# CodeTracer — manual

The complete reference. For a quick overview see `README.md`; for the internals see
`HOW_IT_WORKS.md`; for turning natural language into commands see `AGENT.md`.

CodeTracer is a **local, offline** C# code navigator: **Roslyn** does the exact analysis,
a **small local model** (Ollama, default `gemma4:latest`, CPU-only) only explains the code it
is handed. No GPU, no cloud.

---

## 1. Install & run

1. **.NET 8 SDK or newer** (`dotnet --version`). The project targets `net8.0`, so it builds on a
   .NET 8 SDK (e.g. a VDI) and a .NET 10 SDK; `RollForward=Major` lets the net8 binary run on a
   newer runtime.
2. **Ollama ≥ 0.5** (`ollama --version`) and a model: `ollama pull gemma4:latest`
   (or use the bundled `docker-compose.yml`).

```bash
dotnet build
dotnet run -- <command> [options]      # command = explain | trace
dotnet run -- --help
```

The default API is `http://localhost:11434` (native Ollama). For LM Studio:
`-a http://localhost:1234 --api-style openai`.

---

## 2. Commands at a glance

| command | purpose | core inputs |
|---|---|---|
| `explain` | understand a method (or property) + its call chain | `--method "Class.Method"` or `--file --line` |
| `trace` (endpoint) | find the call chain from an endpoint B to a target class A | `-f <target.cs> -e "<B>"` |
| `trace` (direct) | find the path between two concrete methods | `--from "C.M" --to "C2.M2"` |

---

## 3. `explain`

Roslyn extracts **only** the target method (signature, full body, parameters, fields it
reads/writes, callees, doc-comment) — never the whole file — and the model explains it.

```bash
# one method, fast (single model call)
dotnet run -- explain -s App.sln --method "TaxEngine.Calculate" --depth 0

# the WHOLE logic, several levels deep (each method explained + an end-to-end synthesis)
dotnet run -- explain -s App.sln --method "TaxEngine.Calculate" --depth 3 --max-methods 15

# by file + line instead of a name (any line inside the method)
dotnet run -- explain -s App.sln --file Tax/TaxEngine.cs --line 1234 --depth 2

# point at code and ask a specific question (answered first)
dotnet run -- explain -s App.sln --method "TaxEngine.Calculate" --ask "where does the VAT rate come from?"

# propose a change (optional; a separate model call)
dotnet run -- explain -s App.sln --method "TaxEngine.Calculate" --goal "add a VIP discount"

# save to a file
dotnet run -- explain -s App.sln --method "TaxEngine.Calculate" --depth 3 --out tax.md
```

- **`--depth 0`** → one unconstrained call: a numbered explanation of that one method.
- **`--depth N ≥ 1`** → follows the call chain N levels down; **each method is explained on its
  own**, then a final **end-to-end synthesis**. At depth ≥ 1 it also lists the method's **callers**.
- Long methods (> 400 lines) are explained **block by block**, then summarized.
- Every explanation ends with an **"In plain words"** recap — a short, jargon-free "explain
  like I'm 10" version of what the code is for and what the point of it is.

### Properties

`explain` also works on **properties** (and indexers): `--method "Class.PropertyName"`. It
explains the get/set accessor bodies (their reads/writes, callees, callers). Auto-properties
(`{ get; set; }` with no body) are still shown, just with little to explain. (`--file --line`
currently resolves **methods** only.)

---

## 4. `trace`

### 4a. Endpoint → target class
```bash
dotnet run -- trace -s App.sln -f Pricing/PricingEngine.cs -e "POST /orders"
dotnet run -- trace -s App.sln -f Pricing/PricingEngine.cs -e "OrdersController.Create"
```
`-e` is the start B (a route or `Class.Method`); `-f` is the file of the target class A. For a
Razor `.cshtml` endpoint the handler lives in `.cshtml.cs` (`OnGet/OnPost…`).

### 4b. Direct: method → method
```bash
dotnet run -- trace -s App.sln --from "OrdersController.Create" --to "PricingEngine.Compute"
```
*"From this method, how do I reach that method?"* — runs `find_path` between the two concrete
methods, skipping `-f`/`-e`. Works with all the rendering options below.

### How it finds the path
Trace runs a **deterministic pre-flight**: `find_path` is a BFS over callers in Roslyn — exact,
not guessed. In the common case it prints the path with **no model calls at all**. The model
loop is a fallback for the rare purely-dynamic cases. The output is the path, hop by hop, as
`Class.Method  file:line`.

### Trace rendering options

| option | effect |
|---|---|
| `--all-paths` (`--brute`) | enumerate **all** distinct paths, not just the first/shortest. With `--from/--to`: every path between the two methods — one per interface implementation / decorator chain |
| `--with-bodies` (`--code`) | between hops, show each method's code from its start down to the call to the next hop; **param names** in signatures; **arg → param** mapping at each call site; the **target** node shows its full body |
| `--annotate` (`--why`) | a short LLM **"why" note per hop** (depth-aware: it sees the prior steps); trivial hops get none. Implies `--with-bodies` |
| `--summary` | a final **Summary** (purpose, dependencies, "good to know") **followed by an "In plain words" recap** — a 2-4 sentence "explain like I'm 10" version. Both go to the output / saved file |
| `--repo-url <base>` | turn every `file:line` into a clickable link, e.g. `https://github.com/you/repo/blob/main` |
| `--no-llm` | deterministic only — no model (works even if Ollama is down) |
| `--out <file>` | save the result to a file |

The full experience:
```bash
dotnet run -- trace -s App.sln --from "OrdersController.Create" --to "PricingEngine.Compute" \
  --with-bodies --annotate --summary --repo-url https://github.com/you/repo/blob/main --out walk.md
```
See `examples/` for real output: `trace-with-bodies.md`, `trace-with-bodies-annotated.md`,
`trace-agent-to-roslynindex.md` (all paths).

### DI, interfaces & multiple implementations

CodeTracer traces **through dependency injection and interfaces** out of the box. When a method
calls `service.DoWork()` and `service` is an interface, Roslyn's caller search **cascades through
the interface member to its implementations**, so the path continues into the concrete code —
you don't have to start from the implementation. Direct `new` instantiation and factories work
too (they're static calls).

When an interface has **several implementations that each reach the target**, enumerate them all
with **direct mode + `--all-paths`**:

```bash
dotnet run -- trace -s App.sln --from "NotificationService.Notify" --to "Audit.Record" --all-paths
```

It lists **every distinct path** — one per implementation, including decorator chains (an impl
that wraps another impl). See [`examples/trace-di-multiple-impls.md`](examples/trace-di-multiple-impls.md),
produced from the `samples/di-playground` project.

Tracing only stops at **purely dynamic** links that don't exist statically: reflection
(`Activator.CreateInstance` + `MethodInfo.Invoke`), `dynamic`, or handlers wired up at runtime.
For those, start from a concrete implementation method (`--from` / `-e`).

---

## 5. `--depth` vs `--max-methods` (easy to use once you get this)

These two knobs control how much of the call chain `explain` covers. You set them yourself.

- **`--depth N`** = how many *levels* down to follow. `0` = just the method; `1` = the method
  plus what it directly calls; `N` = N hops deep. In the output, levels are labelled `L0, L1, …`.
- **`--max-methods M`** = the *total* number of methods to explain (the budget). The walk is
  **breadth-first**: it fills all of L0, then all of L1, and so on. If `M` runs out at a shallow
  level, the deeper levels are never reached — **even with a big `--depth`**.

> **In one line:** `--depth` says how deep you're *allowed* to go; `--max-methods` says how many
> methods you can *afford*. To reach level N, `M` must hold everything at levels 0…N-1 first.

Example: a method that calls 8 others. `--depth 5 --max-methods 8` only ever shows L0 + L1 (the
8 direct callees use up the whole budget). Raise `--max-methods` to actually go deeper.

| you want | command |
|---|---|
| just this method (fast) | `--depth 0` |
| method + its direct calls | `--depth 1 --max-methods 8` |
| a few levels of real logic | `--depth 3 --max-methods 12` |
| deep prod logic, brute depth | `--depth 10 --max-methods 40` |

When the budget runs out you'll see, on stderr:
`[explain] call chain capped at M methods - raise --max-methods to go wider/deeper.`
That's your cue to raise `--max-methods` (and/or `--depth`).

---

## 6. How names are resolved — and conflicts

A `"Class.Method"` is resolved by the **simple class name** (case-insensitive) + the member name.
There is **no namespace or overload disambiguation** — if several declarations match, CodeTracer
**warns and lists them**, then uses the **first**:

```
[warn] 'OrderService.Process' is ambiguous - 3 matches; using the first:
[warn]   Namespace.A.OrderService.Process(Order)  A/OrderService.cs:42
[warn]   Namespace.B.OrderService.Process(Order, bool)  B/OrderService.cs:88
[warn]   ...
```

When that happens:
- **Overloads** (same `Class.Method`, different parameters): it picks one arbitrarily — narrow
  the target by tracing a caller/callee instead, or use `--file --line`.
- **Same class name in different namespaces**: the simple name can't distinguish them; pick by
  using `--file --line`, or by choosing a more unique entry point.

Roslyn is still the source of truth — the warning just makes the choice visible.

---

## 7. Performance (the `[perf]` line)

Every run prints a `[perf]` line to stderr with total wall-clock, model-call count, and token
usage. With more than one model call it also breaks it down **per call** — label, duration, and
**in / out** tokens (input/prompt vs generated):

```
[perf] 146.4s total · 5 model call(s) · in 9210 / out 1840 tokens (gemma4:latest)
[perf]   call 1 [annotate]: 4.1s · in 820 / out 22
[perf]   call 2 [annotate]: 3.8s · in 910 / out 31
[perf]   call 5 [summary]: 121.0s · in 4194 / out 732
```

A deterministic run (`--no-llm`, no `--summary`/`--annotate`) shows `0 model calls`. Most of the
time on CPU is the model generating/processing tokens — the Roslyn path-finding itself is fast.

---

## 8. All options

| option | command | default | meaning |
|---|---|---|---|
| `-s, --solution` | both | — | path to the `.sln` (required) |
| `--method` | explain | — | `"Class.Method"` (or `"Class.Property"`) to explain |
| `--file` + `--line` | explain | — | alternative to `--method` (methods only) |
| `--depth` | explain | `1` | call-chain depth (0 = the method alone) |
| `--max-methods` | explain | `8` | total methods explained in the chain |
| `--question` / `--ask` | explain | — | a specific question, answered first |
| `--goal` | explain | — | (optional) change proposal with a code sample |
| `-f, --target-file` | trace | — | file of the target class A |
| `-e, --endpoint` | trace | — | starting point B (route / `Class.Method`) |
| `--from` / `--to` | trace | — | direct mode: path from `Class.Method` to `Class.Method` |
| `--all-paths` / `--brute` | trace | off | enumerate all distinct paths |
| `--with-bodies` / `--code` | trace | off | show code between hops (+ param names + arg→param) |
| `--annotate` / `--why` | trace | off | per-hop LLM "why" note (implies `--with-bodies`) |
| `--summary` | trace | off | final summary section (purpose / deps / good-to-know) |
| `--max-steps` | trace | `25` | model-loop step limit |
| `--no-llm` | both | off | trace: deterministic only · explain: dump Roslyn context, no model |
| `--peek` | explain | — | in the `--no-llm` dump, first N lines per method instead of full body |
| `--repo-url` | both | — | clickable links to the repo (`.../blob/<branch>`) |
| `--out` | both | — | save the result to a file |
| `-m, --model` | both | `gemma4:latest` | model name |
| `-a, --api` | both | `http://localhost:11434` | server base URL |
| `--api-style` | both | `ollama` | `ollama` \| `openai` (LM Studio) |
| `--num-ctx` | both | `16384` | context window |
| `--num-predict` | both | `4096` | output token cap for explain |
| `--temperature` | both | `0` / `0.2` | 0 for decisions, 0.2 for explanation |

---

## 9. Model-free / offline

Roslyn does the exact work; the model is optional. Fully model-free workflows (work even if
Ollama is not running):

```bash
# all paths, pure Roslyn, no model:
dotnet run -- trace -s App.sln --from "A.M1" --to "B.M2" --all-paths --no-llm --out paths.md

# dump a method + its whole call chain (source + structure) for a bigger model:
dotnet run -- explain -s App.sln --method "Some.Method" --depth 10 --max-methods 40 \
  --no-llm --peek 15 --repo-url https://github.com/you/repo/blob/main --out context.md
```

---

## 10. Troubleshooting

- **Solution won't load** → run `dotnet restore` + `dotnet build` on the *target* solution first.
  `[workspace] …` warnings during load are normal.
- **`.slnx`** (new SDK format) may not open in older Roslyn — use a classic `.sln`.
- **Ambiguous match warning** → see §6; use `--file --line` or a more unique entry point.
- **`find_path` finds nothing** → interface/DI calls are followed; a true miss is a purely-dynamic link (reflection / `dynamic` / runtime-wired handler). Try the
  model loop (omit `--no-llm`) or `--from/--to` between concrete methods.
- **Slow on CPU** → `explain --depth 0`, lower `--num-predict`, drop `--summary`/`--annotate`,
  or use `--no-llm` and hand the dump to a bigger model.
