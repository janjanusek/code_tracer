# CodeTracer — manual

The complete reference. For a quick overview see `README.md`; for the internals see
`HOW_IT_WORKS.md`; for turning natural language into commands see `AGENT.md`.

CodeTracer is a **local, offline** C# code navigator: **Roslyn** does the exact analysis,
a **small local model** (Ollama, default `gemma4:latest`, CPU-only) only explains the code it
is handed. No GPU, no cloud.

---

## 1. Install & run

1. **.NET 8 SDK or newer** (`dotnet --version`). CodeTracer multi-targets `net8.0;net472`, so
   `dotnet build` produces both: the **net8.0** build for SDK-style / modern .NET solutions (builds on
   a .NET 8 or 10 SDK; `RollForward=Major`) and the **net472** build for classic .NET Framework / mixed
   solutions.
2. **For classic .NET Framework or mixed solutions:** **Visual Studio** or **Build Tools for Visual
   Studio** (the full MSBuild). You never pass a framework flag — CodeTracer auto-detects the solution
   type and switches to the net472 build itself (§ *Legacy / mixed solutions* below).
3. **Ollama ≥ 0.5** (`ollama --version`) and a model: `ollama pull gemma4:latest`
   (or use the bundled `docker-compose.yml`).

```bash
dotnet build                                   # builds net8.0 + net472
bin\Debug\net472\CodeTracer.exe <command> [options]    # Windows+VS: loads any solution (recommended)
dotnet bin/Debug/net8.0/CodeTracer.dll <command>       # no-VS box: SDK solutions (auto-routes if legacy)
dotnet run -f net8.0 -- --help                 # quick dev (multi-target needs -f)
```

The default API is `http://localhost:11434` (native Ollama). For LM Studio:
`-a http://localhost:1234 --api-style openai`.

### Legacy / mixed solutions

`MSBuildWorkspace` hosts one MSBuild family per process: net8.0 → SDK MSBuild (SDK-style projects
only); net472 → full VS MSBuild (classic non-SDK / `packages.config` **and** SDK-style). CodeTracer
reads the `.sln` and, if it sees a classic project while on .NET (Core), re-launches its **net472**
build automatically (`[auto] … switching …`); needs VS / Build Tools installed. Override the located
exe with the `CODETRACER_NET472` env var.

CodeTracer never restores — it reads restore output. If VS already built the solution, **don't restore
from the CLI at all**: run with **`--offline`** to reuse VS's NuGet cache and contact no feed (your
`nuget.config` is untouched). Only if nothing is cached, add the feed's credentials to `nuget.config`
(`dotnet nuget add source … -u … -p … --store-password-in-clear-text`), since the CLI doesn't share
Visual Studio's stored credentials. A full walkthrough is in
[`examples/legacy-framework-example.md`](examples/legacy-framework-example.md).

---

## 2. Commands at a glance

| command | purpose | core inputs |
|---|---|---|
| `explain` | understand a method (or property) + its call chain | `--method "Class.Method"` or `--file --line` |
| `trace` (endpoint) | find the call chain from an endpoint B to a target class A | `-f <target.cs> -e "<B>"` |
| `trace` (direct) | find the path between two concrete methods | `--from "C.M" --to "C2.M2"` |
| `map` | from one method, map everything reachable both ways (callees + callers) — deterministic | `--method "Class.Method"` (`--up` / `--down`) |

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
- **The real source is shown under each method** (a ```csharp block), so you read the code next
  to the explanation — not just prose. In a deep chain the **whole section is indented by call-depth**
  via nested blockquotes (the code itself keeps its natural indent), so the nesting is visible at a
  glance (like the Call-flow tree). The source is a **foldable `<details>`** (open by default) — so
  you can collapse a method's code in the VS Code preview / on GitHub, IDE-style, and still read the
  explanation. Pass **`--no-code`** for prose only, or **`--no-collapse`** to keep the code expanded.
- Every explanation ends with an **"In plain words"** recap — a short, jargon-free "explain
  like I'm 10" version of what the code is for and what the point of it is.
- …and then a **`## Call-flow`** diagram (ASCII + Mermaid) of the call-tree it walked — see
  [§ Call-flow diagram](#7-call-flow-diagram-automatic). Deterministic, no extra model call.

The goal of all this: a developer who has **never seen the code** can grasp it fast — the code,
what each piece does, how the calls nest, and a map of the whole flow, in one read.

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

Every trace result also **ends with a `## Call-flow` diagram** (ASCII + Mermaid) of the path it
found — automatic, deterministic, no flag needed. See
[§ Call-flow diagram](#7-call-flow-diagram-automatic).

The full experience:
```bash
dotnet run -- trace -s App.sln --from "OrdersController.Create" --to "PricingEngine.Compute" \
  --with-bodies --annotate --summary --repo-url https://github.com/you/repo/blob/main --out walk.md
```
…gives you, in one file: the code between every hop, a "why" note per hop, a Summary, an "In
plain words" recap, **and** the Call-flow diagram. See `examples/` for real output:
`trace-full-example.md` (all of the above) and `trace-di-multiple-impls.md` (DI fan-out drawn as a
branching tree).

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

## 5. `map`

`trace` is point-to-point ("how do I get from A to B"). **`map` has no destination** — you give it
one method as the **root** and it expands the call graph until it runs out. **Fully deterministic
(0 model calls)**, so it's fast and sweeps the whole solution.

```bash
# default: BOTH directions, written to TWO files
dotnet run -- map -s App.sln --method "Agent.RunAsync"
#   -> codetracer-map-down-Agent.RunAsync.md   (what it calls — downstream)
#   -> codetracer-map-up-Agent.RunAsync.md     (what reaches it — callers / impact)

# one direction (then --out applies):
dotnet run -- map -s App.sln --method "LlmClient.ChatAsync" --up --out who-calls-llm.md

# by file + line instead of a name:
dotnet run -- map -s App.sln --file LlmClient.cs --line 87 --down
```

| flag | meaning |
|---|---|
| `--down` / `--callees` | only downstream — what the root calls, transitively ("what does this touch?") |
| `--up` / `--callers` | only upstream — what reaches the root ("impact: who breaks if I change this?") |
| *(neither)* | **both directions**, written to two files — the default; that's why `map` is deterministic |
| `--depth N` | how deep to follow (default: very deep) |
| `--max-nodes N` | hard cap on the graph (default 400); announced on stderr when hit — never a silent cut |

Each result is an **ASCII tree** (readable anywhere) **and** a **Mermaid graph** (graphics on
GitHub / VS Code). `map` is the overview; for the detail on any node it surfaces, run `explain` or
`trace` on that node. See [`examples/map-full-example.md`](examples/map-full-example.md).

---

## 6. `--depth` vs `--max-methods` (easy to use once you get this)

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

## 7. How names are resolved — and conflicts

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

## 8. Call-flow diagram (automatic)

Both `explain` and `trace` finish their result with a **`## Call-flow`** section: a high-level map
of what the analysis found, so you grasp the *shape* before reading the prose. It's **automatic**
(no flag), **deterministic** (built straight from Roslyn — no extra model call), and rendered
**two ways** so it's useful everywhere:

- an **ASCII** block (in a ` ```text ` fence) — renders identically in any viewer, even a
  locked-down corporate wiki, Notepad, or an offline `.md`;
- a **Mermaid** block (` ```mermaid `) — renders as real graphics on GitHub / VS Code.

The **start** node is tagged `◆ start`, the **target / leaf** `★ target`, and every node carries
its `file:line`. If you ran `trace --annotate`, the per-hop **"why" one-liner is reused** on each
node (`— …`), so the chart explains the whole path at a glance — for free, no extra model calls.
The layout adapts to what was found:

- **`trace`, one straight path** → vertical boxes:

  ```text
  ┌────────────────────────────┐
  │ Agent.RunAsync   ◆ start   │   Agent.cs:118
  └──────────────┬─────────────┘
                 ▼  calls
  ┌────────────────────────────┐
  │ RoslynIndex.Rel   ★ target │   RoslynIndex.cs:38
  └────────────────────────────┘
  ```

- **`trace --all-paths`** (e.g. a DI interface with several implementations) or **`explain`'s
  call-tree** → an indented tree that shows the fan-out and where branches converge:

  ```text
  NotificationService.Notify   ◆ start  Services.cs:10
  ├─► EmailNotifier.Send                Notifications.cs:13
  │   └─► Audit.Record   ★ target       Audit.cs:8
  ├─► SmsNotifier.Send                  Notifications.cs:23
  │   └─► Audit.Record   ★ target       Audit.cs:8
  └─► LoggingNotifier.Send              Notifications.cs:36
      └─► Audit.Record   ★ target       Audit.cs:8
  ```

See `examples/trace-di-multiple-impls.md` and `examples/explain-full-example.md` for the full
ASCII + Mermaid output.

---

## 9. Reading the console output (logs, pre-flight, `[perf]`)

Everything CodeTracer prints **while it works** goes to **stderr** as short tagged lines (so it
never pollutes the `--out` file, which is stdout). Here's what each tag means.

### What "pre-flight" is

In `trace`, **pre-flight** is the deterministic attempt that runs **before any model is involved**.
From your endpoint and target, CodeTracer builds a list of **candidate pairs** — `(fromClass,
fromMethod) → (toClass, toMethod)` guesses for where the path might start and end (for a Razor
page it knows the handler is `OnGet/OnPost…`, etc.) — and runs the exact `find_path` (a breadth-
first search up the call graph in Roslyn) over them. If any pair connects, you get the real path
with **zero model calls**. Only if none connect does it "hand over to the model loop". So:

```
[pre-flight] deterministic find_path over 24 candidate pairs [first path]...
[pre-flight] no direct path - handing over to the model loop...
```
means: it tried 24 start/end guesses deterministically, none linked up, so the model will now
navigate. `[first path]` = stop at the first connecting pair; `[brute-force (all paths)]` =
(`--all-paths`) enumerate every path instead.

### The log tags

| tag | when | what it means |
|---|---|---|
| `[cfg]` | startup | the resolved config — mode, API URL, api-style, model, key options. Also `[cfg] Ollama version: …` |
| `[pre-flight]` | trace | the deterministic `find_path` over candidate pairs (see above), before the model |
| `[direct]` | trace | `--from/--to` mode: the single `find_path` (or all-paths) between the two methods you named |
| `===== STEP n =====` | trace | the model loop: step *n*, printing the model's raw JSON action (`{"tool":…}`) |
| `--- OBSERVATION ---` | trace | the tool output that step produced — what the model "sees" next |
| `[!]` | trace | a guard fired: a repeated step, or the step limit was hit — it's about to fall back |
| `[auto]` | trace | deterministic escalation: the model stalled/looped, so the cached pre-flight result is used |
| `[summary]` | trace | the optional `--summary` pass is running (one model call) |
| `========== DONE (reason) ==========` | trace | the final result; `reason` = how it was obtained (`pre-flight` / `brute-force` / `finish` / `auto` / `direct` …) |
| `[explain] building call chain …` | explain | Roslyn is assembling the method chain (`depth=…, max-methods=…`) before any explaining |
| `[explain] (i/N) Lx Method … · ~Xm left` | explain | explaining method *i* of *N*; the **ETA** is averaged from the methods already done |
| `[explain] synthesizing end-to-end logic …` | explain | the final pass that ties the per-method explanations together |
| `[explain] block b/B …` | explain | a >400-line method is being explained block by block |
| `[warn]` | both | a non-fatal note — e.g. an **ambiguous** `Class.Method` (it lists the matches and uses the first) |
| `[perf]` | both | the performance summary (below) |
| `[error]` / `[write error]` | both | a fatal problem (bad args, solution failed to load, couldn't write `--out`) |

### The `[perf]` line

Every run ends with a `[perf]` summary: total wall-clock, model-call count, and token usage. With
more than one model call it also breaks it down **per call** — label, duration, and **in / out**
tokens (input/prompt vs generated):

```
[perf] 146.4s total · 5 model call(s) · in 9210 / out 1840 tokens (gemma4:latest)
[perf]   call 1 [annotate]: 4.1s · in 820 / out 22
[perf]   call 2 [annotate]: 3.8s · in 910 / out 31
[perf]   call 5 [summary]: 121.0s · in 4194 / out 732
```

The per-call **label** matches the work: `[action]` (trace's tool picks), `[annotate]`,
`[summary]`, `[explain]`, `[eli10]` (the "In plain words" pass). A deterministic run (`--no-llm`,
no `--summary`/`--annotate`) shows `0 model calls`. Most of the time on CPU is the model
generating/processing tokens — the Roslyn analysis itself is fast.

### Auto-save, live progress & stopping early

A long deep run is **never lost**, even if you forget `--out`:

- **Auto-save (default on, no flag).** If you don't pass `--out`, the result is still saved — to a
  discoverable file in the current directory: `codetracer-explain-<Class.Method>.md` /
  `codetracer-trace-<from>-to-<to>.md`. Pass `--out <file>` to choose the path instead.
- **Incremental save.** In `explain`, the file is flushed **after every method** (and after the
  synthesis / plain-words / call-flow). So at any moment the file holds everything finished so far.
- **Watch it live.** Because it's written progressively, you can open the file **read-only** and
  watch the knowledge accumulate — e.g. `Get-Content codetracer-explain-*.md -Wait` (PowerShell)
  or `tail -f` (bash).
- **Live ETA.** Each `[explain] (i/N) …` line shows `· ~Xm left`, averaged from the methods
  already done (remaining work = methods left + the synthesis pass).
- **Stop early with `Ctrl+C`.** "I'm out of time — give me what you have": it cancels the in-flight
  model call, keeps the partial file (everything finished before the stop), and exits cleanly.

---

## 10. All options

| option | command | default | meaning |
|---|---|---|---|
| `-s, --solution` | both | — | path to the `.sln` (required) |
| `--method` | explain | — | `"Class.Method"` (or `"Class.Property"`) to explain |
| `--file` + `--line` | explain | — | alternative to `--method` (methods only) |
| `--depth` | explain | `1` | call-chain depth (0 = the method alone) |
| `--max-methods` | explain | `8` | total methods explained in the chain |
| `--question` / `--ask` | explain | — | a specific question, answered first |
| `--goal` | explain | — | (optional) change proposal with a code sample |
| `--no-code` | explain | off | prose only — don't show each method's source |
| `--no-collapse` | explain | off | keep each method's source expanded (default: foldable `<details>`, open) |
| `-f, --target-file` | trace | — | file of the target class A |
| `-e, --endpoint` | trace | — | starting point B (route / `Class.Method`) |
| `--from` / `--to` | trace | — | direct mode: path from `Class.Method` to `Class.Method` |
| `--all-paths` / `--brute` | trace | off | enumerate all distinct paths |
| `--with-bodies` / `--code` | trace | off | show code between hops (+ param names + arg→param) |
| `--annotate` / `--why` | trace | off | per-hop LLM "why" note (implies `--with-bodies`) |
| `--summary` | trace | off | final summary section (purpose / deps / good-to-know) |
| `--max-steps` | trace | `25` | model-loop step limit |
| `--down` / `--callees` | map | — | map only downstream (what the root calls) |
| `--up` / `--callers` | map | — | map only upstream (callers / impact); no flag = BOTH (two files) |
| `--depth` | map | very deep | how far to expand the graph (the `explain` default of 1 does not apply to map) |
| `--max-nodes` | map | `400` | hard cap on map size (announced when hit, never silent) |
| `--no-llm` | both | off | trace: deterministic only · explain: dump Roslyn context, no model (map is always model-free) |
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

## 11. Model-free / offline

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

## 12. Troubleshooting

- **Solution won't load** → run `dotnet restore` + `dotnet build` on the *target* solution first.
  `[workspace] …` warnings during load are normal.
- **`.slnx`** (new SDK format) may not open in older Roslyn — use a classic `.sln`.
- **Ambiguous match warning** → see §7; use `--file --line` or a more unique entry point.
- **`find_path` finds nothing** → interface/DI calls are followed; a true miss is a purely-dynamic link (reflection / `dynamic` / runtime-wired handler). Try the
  model loop (omit `--no-llm`) or `--from/--to` between concrete methods.
- **Slow on CPU** → `explain --depth 0`, lower `--num-predict`, drop `--summary`/`--annotate`,
  or use `--no-llm` and hand the dump to a bigger model.
