# CodeTracer

**Drowning in a huge, 15-year-old C# codebase — on a locked-down corporate machine with no GPU
and no cloud AI allowed?** CodeTracer maps how the code actually connects and explains it for
you, **fully offline**. Roslyn does the exact analysis; a small local model (Ollama, CPU-only)
does the explaining. No GPU, no cloud — nothing leaves your machine.

It answers the two questions you keep asking in legacy code:
- **"How does execution get from here to there?"** → **`trace`** prints the call chain hop by
  hop — *through interfaces and DI too* (it bridges interface calls to their implementations,
  and can list every implementation that reaches your target).
- **"What does this code actually do?"** → **`explain`** walks a method (and, as deep as you
  ask, its call chain) and explains it step by step, ending with a plain-words recap.

Every result also ends with an **auto-generated `## Call-flow` diagram** — a high-level map of
what the analysis found (the path / call-tree), drawn as **ASCII** (readable in any viewer, even
a locked-down wiki) **and** **Mermaid** (renders as graphics on GitHub / VS Code). Deterministic,
no extra model call — so you grasp the shape before reading a word of prose.

Roslyn does the **precise** analysis (symbols, references, call graph) — nothing is guessed by
the model; the model only **explains the code it is given**.

> **Runs on modest hardware.** Built and used daily on a GPU-less corporate VDI
> (8 cores / 32 GB RAM, CPU-only) with a **Q4-quantized local model** — no GPU, no cloud, fully offline.

It has **three modes**:

| mode | what it does | typical question |
|---|---|---|
| **`explain`** | explains one method step by step and **pulls in its dependencies** ("how it all fits together") | *"tax is calculated here — explain it and how it works together"* |
| **`trace`** | finds the **whole call chain** from an endpoint (B) down to a target class (A) and prints it | *"how does execution reach this point?"* |
| **`map`** | from one method, **maps everything reachable** both ways (callees + callers) — deterministic, no model | *"what does this touch, and who depends on it?"* |

> It does not modify code — that's the engineer's job. The tool is for **understanding** and **mapping**.

**Docs:** [full manual](MANUAL.md) · [how it works](HOW_IT_WORKS.md) · [command-prep agent guide](AGENT.md)

---

## Requirements

1. **.NET 8 SDK or newer** — `dotnet --version`. CodeTracer multi-targets **`net8.0;net472`**, so
   `dotnet build` produces **both** builds: **net8.0** for SDK-style / modern .NET solutions (runs on
   a .NET 8 SDK — e.g. a VDI — *and* a .NET 10 SDK; `RollForward=Major`), and **net472** for **classic
   .NET Framework / mixed** solutions. Building net472 needs nothing extra (reference assemblies come
   via NuGet).
2. **To analyze classic .NET Framework or mixed (Framework + Core) solutions** you also need the
   **full MSBuild** — **Visual Studio** or **Build Tools for Visual Studio** installed. You don't pick
   a framework: CodeTracer **auto-detects** the solution type and uses (or switches to) the right build
   — see [Legacy / mixed solutions](#legacy--mixed-net-framework-solutions).
3. **Ollama ≥ 0.5** (structured outputs) — `ollama --version`
4. Model: `ollama pull gemma4:latest`

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
dotnet build                                        # builds BOTH net8.0 and net472
.\codetracer map -s Big.sln --method "Foo.Bar"      # run with NO framework flag (Windows .cmd)
./codetracer.ps1 map -s Big.sln --method "Foo.Bar"  # PowerShell / Linux / macOS
```

`codetracer` (`.cmd` for Windows, `.ps1` cross-platform) is a thin launcher so you **never pass
`--framework`**: it does a fast incremental **build** (so it's always fresh after a `git pull` — no
separate `dotnet build` needed) and runs the net8.0 build, which **auto-switches** to the net472 build
for classic / .NET Framework / mixed solutions (see [Legacy / mixed
solutions](#legacy--mixed-net-framework-solutions)). (`dotnet run` can't choose a framework on a
multi-target project — that's why the launcher exists.)

> The `[cfg]` / `[index]` / `[map]` lines are progress on **stderr**; Windows PowerShell colours them
> red — that's normal, not an error (the exit code is `0` on success).

Prefer to skip the launcher? Run a build directly:
- `bin\Debug\net472\CodeTracer.exe <cmd>` — Windows + VS: this one build loads legacy, modern, and mixed.
- `dotnet bin/Debug/net8.0/CodeTracer.dll <cmd>` — SDK-only boxes; auto-switches to net472 for legacy.
- `dotnet run -f net8.0 -- <cmd>` — quick dev (`dotnet run` requires the `-f`). `--help` lists all options.

The default API is `http://localhost:11434` (native Ollama `/api/chat`). It also accepts
the `.../v1` form — it gets normalized. For **LM Studio**, add `--api-style openai`
(e.g. `-a http://localhost:1234 --api-style openai`).

### Legacy / mixed (.NET Framework) solutions

`MSBuildWorkspace` can host only **one** MSBuild family, fixed by the running process: the **net8.0**
build gets the **.NET SDK MSBuild** (SDK-style projects only); the **net472** build gets the **full
Visual Studio MSBuild** (loads **both** classic non-SDK / `packages.config` **and** modern SDK-style
projects). A 15-year-old solution mixing .NET Framework and .NET (Core) therefore needs the net472 build.

**You don't manage this.** CodeTracer reads the `.sln`, and if it finds a classic (non-SDK) project
while running on .NET (Core), it **re-launches its net472 build** with the same arguments and prints
`[auto] … switching to the .NET Framework build`. Needs VS / Build Tools on the machine. Set the
`CODETRACER_NET472` env var to the `CodeTracer.exe` path if your layout is non-standard.

**Reuse Visual Studio's packages — no feed, no `nuget.config` edits.** CodeTracer does *not* restore;
it only reads restore output. So **build the solution once in VS** (it restores), then run CodeTracer
with **`--offline`**: it resolves packages **only** from the local NuGet cache VS already filled and
contacts **no** feed. Your `nuget.config` is left untouched.

```bash
bin\Debug\net472\CodeTracer.exe map -s Big.sln --method "Foo.Bar" --offline
```

**Only if nothing is cached and you must hit the feed — connect Nexus / Artifactory / Azure DevOps.**
The `dotnet`/`nuget` CLI does **not** share Visual Studio's stored credentials, so add them once
(basic auth = username + API token), then restore:

```bash
# Nexus example (NuGet v3 hosted repo). Token: Nexus UI -> your profile -> "NuGet API Key".
dotnet nuget add source "https://nexus.example.com/repository/nuget-hosted/index.json" \
  -n NexusFeed -u <user> -p <nuget-api-token> --store-password-in-clear-text
# Artifactory: URL like https://artifactory.example.com/artifactory/api/nuget/v3/<repo> ; token from "Set Me Up".

nuget restore Big.sln      # nuget.exe for packages.config projects; `dotnet restore` for PackageReference
```

**→ Full walkthrough:** [`examples/legacy-framework-example.md`](examples/legacy-framework-example.md)
— a mixed Framework + Core solution behind a private feed, end to end (the auto-switch to net472,
`--offline` cache reuse, and the Nexus / Artifactory connect steps).

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

**The real source is shown with the explanation.** Each method's code appears (a ```csharp
block) right under its heading. In a deep chain the **whole section is indented by call-depth**
using nested Markdown blockquotes (`>`), so the nesting reads like the Call-flow tree — while the
**code itself stays at its natural indentation** (indenting the code would just make it look
mis-indented). The source is wrapped in a **foldable `<details>`** (open by default), so you can
**collapse a method's code** like in an IDE while keeping the explanation — it folds in the VS Code
preview / on GitHub. You see the code, what it does, and how the calls nest, together. The whole
point: a dev who has **never seen the codebase** gets, in one read, what would otherwise take days
of cold-reading. Pass **`--no-code`** for prose only, or **`--no-collapse`** to keep the code always
expanded (plain `csharp` fences, for renderers that don't do HTML).

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

````markdown
# Agent.GetAction  (Agent.cs:241)
_Deep explanation following the call chain (6 methods)._

## L0 · Agent.GetAction
<details open>
<summary>source</summary>

```csharp
private async Task<(string,...)?> GetAction(List<ChatMsg> messages)   // the method's REAL source,
{ for (int attempt = 0; attempt < 3; attempt++) { ... } }             // at its natural indentation
```

</details>
This method extracts a structured action (tool + args) from the model's output; it retries with
corrections on bad output (1 attempt + 2 corrections).  ← source is foldable (`<details>`), IDE-style

> ## L1 · LlmClient.ChatAsync      → the HTTP call to Ollama (/api/chat)
> <details open>
> <summary>source</summary>
>
> ```csharp
> public async Task<string> ChatAsync(...)
> { ... BuildOllama / BuildOpenAI / SendAsync ... }
> ```
>
> </details>
> The whole L1 section sits one blockquote in — a level deeper than L0; the code keeps its
> natural indentation.

> > ## L2 · LlmClient.BuildOllama / BuildOpenAI / Truncate
> > <details open>
> > <summary>source</summary>
> >
> > ```csharp
> > ...request body, OpenAI variant, error trim...
> > ```
> >
> > </details>
> > Nested another blockquote in — the deeper the call, the further the whole section is indented.

## End-to-end logic
(how a request flows L0 → L1 → L2 and back: the schema-constrained call, validation,
 the correction loop, and the deterministic escalation when the model can't comply)

## In plain words
(the same, re-stated for a 10-year-old — plain words, no jargon)

## Call-flow        ← auto-generated, deterministic (also emitted as a Mermaid graph)
Agent.GetAction   ◆ start      Agent.cs:241
├─► LlmClient.ChatAsync        LlmClient.cs:87
│   ├─► LlmClient.BuildOllama  LlmClient.cs:136
│   ├─► LlmClient.BuildOpenAI  LlmClient.cs:159
│   └─► LlmClient.Truncate     LlmClient.cs:201
└─► Agent.ValidateArgs         Agent.cs:293
````

**→ Full example in the repo:**
[`examples/explain-full-example.md`](examples/explain-full-example.md) — a real deep + wide run
(12 methods over 3 levels): each method's source + explanation, an end-to-end synthesis, an "In
plain words" recap, and the Call-flow diagram.

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
it hand over to the model loop (rare, for purely-dynamic links that need navigation). Use
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

…and the result ends with a **`## Call-flow`** map of all those paths merged — the fan-out drawn
as a branching tree (ASCII) plus a Mermaid graph. For a DI interface with several implementations
that each reach the target, this is the "from the side" view of every candidate at once (see
[`examples/trace-di-multiple-impls.md`](examples/trace-di-multiple-impls.md)):

```text
NotificationService.Notify   ◆ start  Services.cs:10
├─► EmailNotifier.Send                Notifications.cs:13
│   └─► Audit.Record   ★ target       Audit.cs:8
├─► SmsNotifier.Send                  Notifications.cs:23
│   └─► Audit.Record   ★ target       Audit.cs:8
└─► LoggingNotifier.Send  (decorator) Notifications.cs:36
    └─► Audit.Record   ★ target       Audit.cs:8
```

**→ Full DI example in the repo:** [`examples/trace-di-multiple-impls.md`](examples/trace-di-multiple-impls.md)
— every implementation path through an interface, drawn as a branching Call-flow tree.

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
**Summary** section (purpose, dependencies, good-to-know) included in the saved file. **→ Full
example:** [`examples/trace-full-example.md`](examples/trace-full-example.md) — bodies + a "why"
note per hop + Summary + "In plain words" + the Call-flow diagram, all in one file.

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

## `map` mode — reachability from one point (deterministic, no model)

Where `trace` answers *"how do I get from A to B"* (point-to-point), **`map` answers *"what can I
reach from here"*** — it picks one method as the root and expands the call graph until it runs out,
with **no fixed destination**. It's **fully deterministic** (pure Roslyn, **0 model calls**), so it's
fast and runs over the whole solution.

```bash
# default: BOTH directions, written to TWO files
codetracer map -s CodeTracer.sln --method "Agent.RunAsync"
#   -> codetracer-map-down-Agent.RunAsync.md   (what it calls — downstream)
#   -> codetracer-map-up-Agent.RunAsync.md     (what reaches it — callers / impact)

# one direction only (then you can use --out):
codetracer map -s CodeTracer.sln --method "LlmClient.ChatAsync" --up --out who-calls-llm.md
```

- **`--down`** (`--callees`) — what the root calls, transitively. *"What does this touch?"*
- **`--up`** (`--callers`) — what reaches the root, transitively. **Impact analysis:** *"if I change
  this, who breaks?"*
- **default (neither flag)** — both, as two files. Because both directions over a busy method is
  **heavy**, `map` is deterministic by design — for a deep dive on any node, run `explain`/`trace` on it.
- **`--depth N`** — how deep to follow (default: very deep); **`--max-nodes N`** is the real guard
  (default 400, and it says so on stderr when it caps — never a silent truncation).
- **`--with-bodies`** (`--code`) — also append **every method's real source** under the diagram, in
  the order reached (deterministic, no model). Add **`--peek N`** to cap each body to N lines.

Each result is an **ASCII tree** (readable anywhere) **and** a **Mermaid graph** (renders as graphics
on GitHub / VS Code). **→ Full example:** [`examples/map-full-example.md`](examples/map-full-example.md)
— both directions from `Agent.RunAsync`, the entire app graph in two diagrams.

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
| `--depth` | explain+map | explain `1` / map very deep | how deep to follow the call chain (explain: each method explained + synthesis, 0 = method only; map: how far to expand the graph) |
| `--max-methods` | explain | `8` | cap on methods explained in the chain — raise for deeper/wider coverage |
| `--question` / `--ask` | explain | — | a specific question to focus the explanation on (answered first) |
| `--goal` | explain | — | (optional) change proposal with a code snippet |
| `--no-code` | explain | off | prose only — don't show each method's source (shown by default; whole section indented by call-depth via blockquotes) |
| `--no-collapse` | explain | off | keep each method's source always expanded (by default it's a foldable `<details>`, open) |
| `--out` | explain | — | save the output to a file (auto-saves if omitted) |
| `-f, --target-file` | trace | — | file of the target class A |
| `-e, --endpoint` | trace | — | starting point B (route / `Class.Method`) |
| `--from` / `--to` | trace | — | direct mode: path from `Class.Method` to `Class.Method` |
| `--all-paths` / `--brute` | trace | off | enumerate ALL distinct paths, not just the first |
| `--with-bodies` / `--code` | trace+map | off | trace: insert each method's code (start → call to next hop) between steps; map: dump every method's real source under the diagram (deterministic) |
| `--annotate` / `--why` | trace | off | short LLM "why" note per hop (depth-aware); implies `--with-bodies` |
| `--no-llm` | trace+explain | off | trace: deterministic only; explain: dump Roslyn context, no model |
| `--max-steps` | trace | `25` | agent step limit |
| `--summary` | trace | off | LLM summary (purpose, dependencies, good-to-know) + an "In plain words" (ELI10) recap; in the saved file |
| `--method` / `--file`+`--line` | map | — | the root method of the map |
| `--down` / `--callees` | map | — | map only downstream (what the root calls) |
| `--up` / `--callers` | map | — | map only upstream (callers / impact); default with no flag = BOTH, as two files |
| `--max-nodes` | map | `400` | hard cap on map size (said out loud when hit, never silent) |
| `--repo-url` | trace+explain | — | render locations as clickable links to the repo |
| `--peek` | explain+map | — | explain (`--no-llm` dump) / map (`--with-bodies`): show first N lines per method instead of the full body |
| `-m, --model` | both | `gemma4:latest` | model name |
| `-a, --api` | both | `http://localhost:11434` | server base URL |
| `--api-style` | both | `ollama` | `ollama` \| `openai` (LM Studio) |
| `--num-ctx` | both | `16384` | context window size |
| `--num-predict` | both | `4096` | token cap for explain output |
| `--temperature` | both | `0` / `0.2` | 0 for decisions, 0.2 for explain |
| `--offline` / `--no-restore` | both | off | load using only the local NuGet cache (VS's restore); never contact a feed, `nuget.config` untouched |

---

## Common issues

- **Solution won't load** → run restore on the *target* solution first (`dotnet restore`, or
  `nuget restore` for `packages.config`). `MSBuildWorkspace` must be able to open it. `[workspace] …`
  warnings during load are normal (missing analyzers / NuGet advisories) — it continues.
- **`projects loaded:` is lower than expected / classic projects missing** → those are non-SDK .NET
  Framework projects and you're on the net8.0 build. CodeTracer normally auto-switches to the net472
  build; if it can't (no VS / Build Tools, or the net472 build isn't built), it says so — build it
  with `dotnet build` and ensure Visual Studio or Build Tools for VS is installed. See
  [Legacy / mixed solutions](#legacy--mixed-net-framework-solutions).
- **Restore fails: "cannot connect to NuGet" on a private feed** → VS has the credentials, the CLI
  doesn't. Easiest: build once in VS, then run with **`--offline`** (reuses VS's cache, contacts no
  feed). Otherwise add the feed credentials to `nuget.config` — see
  [Legacy / mixed solutions](#legacy--mixed-net-framework-solutions).
- **`.slnx`** (the new SDK format) may not open in older Roslyn — use a classic `.sln`.
- **`find_path` finds nothing** → interface & DI calls *are* followed automatically (Roslyn
  bridges interface members to implementations); a true miss means a **purely dynamic** link —
  reflection (`Activator`/`MethodInfo.Invoke`), `dynamic`, or a handler wired up at runtime. Pass
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
AutoRouter.cs    picks the runtime: re-launches the net472 build for classic/mixed solutions
Compat/Shims.cs  net8.0 ↔ net472 compatibility helpers (BCL methods missing on .NET Framework)
LlmClient.cs     HTTP client: Ollama /api/chat (structured outputs) + OpenAI-compatible
RoslynIndex.cs   Roslyn analysis: symbols, call graph, find_path, BuildMethodContext (+deps)
Agent.cs         trace: ReAct loop, token-level JSON, bootstrap + auto-escalation
Explainer.cs     explain: compact method context -> explanation (+ block-splitting long methods)
Diagram.cs       the auto "## Call-flow" diagram (ASCII + Mermaid) of what the analysis found
```

---

## See a complete run

Want the full picture before installing anything? These example files are **real, unedited runs
against CodeTracer's own source** — open them and you see exactly what you get:

- **[`examples/explain-full-example.md`](examples/explain-full-example.md)** — a deep + wide
  `explain` (12 methods, 3 levels): each method's **source + explanation**, an end-to-end
  synthesis, an "In plain words" recap, and the `## Call-flow` diagram. The whole codebase logic,
  in one file.
- **[`examples/trace-full-example.md`](examples/trace-full-example.md)** — a `trace` with the code
  between every hop, a "why" note per hop, a Summary, an "In plain words" recap, and the Call-flow.
- **[`examples/trace-di-multiple-impls.md`](examples/trace-di-multiple-impls.md)** — DI through an
  interface with 3 implementations + a decorator: every path enumerated, drawn as a branching
  Call-flow.
- **[`examples/map-full-example.md`](examples/map-full-example.md)** — a `map` from one method, both
  directions (downstream + upstream/impact): the whole app's call graph in two ASCII + Mermaid
  diagrams, **0 model calls**.
- **[`examples/explain-gemma3n-e2b-example.md`](examples/explain-gemma3n-e2b-example.md)** — the
  **same `explain`** on a **micro model** (`gemma3n:e2b`): ~3.4 min vs ~27 min (**≈7.8× faster**),
  terser but coherent — compare a tiny local model against `gemma4:latest` side by side.
- **[`examples/trace-gemma3n-e2b-example.md`](examples/trace-gemma3n-e2b-example.md)** — the **same
  `trace`** on `gemma3n:e2b`: barely slower than `gemma4:latest` here (path-finding is deterministic;
  the model only writes the notes + summary), so `trace` is a great fit for a small local model.

Every run also **auto-saves** (no `--out` needed) and **saves incrementally**, so a long deep run
is never lost — open the file read-only to watch it fill in, or `Ctrl+C` to stop and keep what's done.

---

## Need help with this — or want it inside your enterprise?

CodeTracer is one example of a bigger specialty: **getting real, production work out of local LLMs on
infrastructure you already own** — plain CPU boxes or **consumer-grade GPU clusters**. No cloud, no
enterprise-GPU budget, nothing leaving your network.

That's not a lab demo. **Reality Radar** runs a **local model on custom infrastructure** to process
**500,000 real-estate listings every single day** — at a total running cost of **under \$100/month**.
The same playbook — a small local model wrapped in exact, deterministic tooling — is what powers
CodeTracer.

If you want help **applying CodeTracer to your codebase**, or **designing a private, low-cost
local-LLM pipeline** for your own enterprise — on the hardware you already have — this is exactly
what I consult on.

<img src="docs/jan.janusek.png" alt="Ján Janušek" width="128" align="left" hspace="16" vspace="4" />

**Ing. Ján Janušek** — CEO & Founder

Privacy-first AI that runs where the data lives, on commodity hardware:

- **Reality Radar** — monitors 90% of the real-estate market; a **local model on custom infra**, **500K listings/day for < \$100/month** · AI Unlimited Ltd — [realityradar.eu](https://www.realityradar.eu)
- **Meeting Buddy** — a **100% private** meeting assistant — [meeting-buddy.com](https://meeting-buddy.com)
- 💼 LinkedIn: [linkedin.com/in/janjanusek](https://www.linkedin.com/in/janjanusek)

<br clear="left" />

---

## License

[MIT](LICENSE) © 2026 Ján Janušek (AI Unlimited Ltd). Use it, fork it, ship it — no GPU, no cloud,
no strings.
