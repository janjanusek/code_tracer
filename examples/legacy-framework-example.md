# Legacy / mixed .NET Framework solution — a full walkthrough

> **This file is a *workflow* example, not a generated tool dump.** The other examples in this folder
> are real CodeTracer output produced against CodeTracer's own (modern) source. This one shows how to
> point CodeTracer at an **old, 15-year-old, mixed .NET Framework + .NET (Core)** solution behind a
> **private NuGet feed** — the exact case where "it builds in Visual Studio but won't load from the
> terminal". The console blocks below are **illustrative** (your names/counts differ); the point is the
> sequence of steps and what each line means.

---

## The situation

A real legacy solution, grown over ~15 years (hundreds of projects):

```
All.Monster.sln
├─ …Web              classic non-SDK csproj, packages.config, <TargetFrameworkVersion>v4.7.1</…>
├─ …Services         classic non-SDK csproj, packages.config, net472
├─ …Provisioning.*   modern SDK-style csproj, net6.0, PackageReference
└─ …Public.MyAccount  ⇄  …Public.MyAccount.Data   (a circular project reference)
```

- NuGet packages come from a **private feed** (Artifactory / Nexus / Azure DevOps). The credentials
  live **only inside Visual Studio** — the `dotnet` / `nuget` CLI can't authenticate out of the box.
- The machine is a **locked-down corporate box** (no cloud AI). It **does** have Visual Studio
  (or *Build Tools for Visual Studio*) installed.

**Why the terminal "won't build" while VS does:** Roslyn's `MSBuildWorkspace` hosts exactly one
MSBuild, fixed by the process runtime. The **net8.0** CodeTracer build gets the **.NET SDK MSBuild**,
which **cannot evaluate classic non-SDK projects** — so the `…Web` / `…Services` projects silently
drop out. The **net472** build gets the **full Visual Studio MSBuild**, which loads **both** the
classic *and* the modern projects. CodeTracer picks the right one for you.

---

## Step 1 — restore the target the painless way: let Visual Studio do it

CodeTracer **never restores** packages itself — it only *reads* restore output. So restore the way that
already works on this box: **open `All.Monster.sln` in Visual Studio and Build (or Rebuild) it once.** VS
authenticates to the private feed with its stored credentials and fills the local NuGet caches:

- PackageReference / SDK-style projects → `%USERPROFILE%\.nuget\packages` + each project's `obj\project.assets.json`
- packages.config (classic) projects → the solution's `packages\` folder

You do **not** need to touch `nuget.config` or run a CLI restore.

## Step 2 — run it with the launcher — one command, no framework flag

`codetracer.cmd` builds CodeTracer for you (incrementally — fresh after every `git pull`) and runs it.
`--offline` tells CodeTracer to resolve packages **only** from the caches VS just filled, contacting
**no** feed. You pass **no** `--framework` — the launcher runs the net8.0 build, which **auto-switches**
to the net472 build because this solution has classic projects:

```powershell
.\codetracer.cmd map -s "C:\dev\monster\src\All.Monster.sln" `
  --method "OrderBuilder.CreateOrderFromCartAndSave" --up --offline
```

> Don't use `dotnet run` here — on a multi-target project it errors asking for `--framework`. And don't
> put a `--` after `codetracer.cmd` (habit from `dotnet run --`); it's not needed.

Console (illustrative — close to a real run):

```text
[codetracer] first run - building (net8.0 + net472)...        <- only the first time; fast afterwards
[auto] classic (non-SDK) .NET Framework project(s) detected -> switching to the .NET Framework build for full MSBuild:
       C:\Users\you\source\repos\code_tracer\bin\Debug\net472\CodeTracer.exe
[cfg] mode=map  directions=up  depth=64  max-nodes=400  (deterministic, no model)
[index] offline: reusing the local NuGet cache only, no feed (config: ...\Temp\codetracer-offline.nuget.config)
[index] loading solution: C:\dev\monster\src\All.Monster.sln
[workspace] Msbuild failed when processing the file '...CCI.Carrier.Provision.csproj' ... was restored
            using '.NETFramework,Version=v4.6.1 … v4.8.1' instead of … 'net6.0'.   <- NORMAL warnings, load continues
[index] whole-solution load failed: Adding project reference from '...MyAccount.csproj' to '...MyAccount.Data.csproj' will cause a circular reference.
[index] rebuilding graph, dropping only the bad (cyclic/duplicate) project references...
[index] dropped 2 cyclic/duplicate project reference(s) - analysis works across the rest.
[index] projects loaded: 469
[map] building upstream (callers) map ...
[map] saved upstream map to codetracer-map-up-OrderBuilder.CreateOrderFromCartAndSave.md
```

What those lines mean:

| line | meaning |
|---|---|
| `[auto] … switching to the .NET Framework build` | you ran the launcher (net8.0); it saw a classic project and **re-launched the net472 build** for you — no `-f`, no version-hunting |
| `[index] offline: reusing the local NuGet cache only` | `--offline` wrote a throwaway config (cleared sources + fallback to VS's cache); the private feed is **never** contacted, your `nuget.config` is untouched |
| `[workspace] Msbuild failed … restored using .NETFramework v4.x instead of net6.0` | a **warning**, not an error — a package was restored for a different TFM; load continues |
| `[index] whole-solution load failed: … circular reference` + `rebuilding graph` | `OpenSolutionAsync` is all-or-nothing; one illegal edge (a cycle VS tolerates but Roslyn doesn't) would drop the whole solution, so CodeTracer **rebuilds the graph and drops only the bad edges** |
| `[index] projects loaded: 469` | the classic **and** modern projects all loaded — the whole-solution call graph is available |

If you instead see `projects loaded: 1` (only the modern one), the launcher didn't switch — the net472
build isn't present or VS / Build Tools isn't installed — see *Troubleshooting* below.

## Step 3 — see the actual code, not just names

Add **`--with-bodies`** to dump every method's real source under the diagram (deterministic, no model).
On a big map, cap each body with **`--peek`** so the file stays readable:

```powershell
.\codetracer.cmd map -s "C:\dev\monster\src\All.Monster.sln" `
  --method "OrderBuilder.CreateOrderFromCartAndSave" --up --with-bodies --peek 20 --offline
```

The saved `.md` then has: a **foldable, full ASCII tree** (never truncated for `map`) + a **Mermaid**
graph + a **`## Method bodies`** section with the source of every method in the map.

## Step 4 — the other modes

Everything works across the mixed solution. A couple of legacy-friendly, **model-free** (deterministic,
instant) commands:

```powershell
# Impact analysis on a classic net472 service: who (transitively) reaches this method?
.\codetracer.cmd map -s "C:\dev\monster\src\All.Monster.sln" --method "PricingRepository.Save" --up --offline

# The whole call chain from an MVC endpoint down to a target class, all paths, no model:
.\codetracer.cmd trace -s "C:\dev\monster\src\All.Monster.sln" `
  -f "Pricing\PricingEngine.cs" -e "OrdersController.Create" --all-paths --no-llm --offline
```

A `trace` result ends with the auto `## Call-flow` map (ASCII + Mermaid), e.g.:

```text
OrdersController.Create   ◆ start        ...\Web\Controllers\OrdersController.cs:42
└─► OrderService.PlaceOrder              ...\Services\OrderService.cs:88
    ├─► PricingEngine.Quote   ★ target   ...\Services\Pricing\PricingEngine.cs:15
    └─► PricingRepository.Save           ...\Services\Data\PricingRepository.cs:120
```

---

## Fallback — if nothing is cached and you must hit the feed

Only needed when VS hasn't restored (fresh clone, cleared cache). The CLI doesn't share VS's stored
credentials, so add them once (basic auth = username + API token), then restore:

```powershell
# Nexus (NuGet v3 hosted repo). Token: Nexus UI -> your profile -> "NuGet API Key".
dotnet nuget add source "https://nexus.example.com/repository/nuget-hosted/index.json" `
  -n NexusFeed -u <user> -p <nuget-api-token> --store-password-in-clear-text

# Artifactory: source URL https://artifactory.example.com/artifactory/api/nuget/v3/<repo>
#   token from the repo's "Set Me Up" -> NuGet panel.

# Azure DevOps Artifacts: dotnet restore All.Monster.sln --interactive   (Azure Artifacts Credential Provider)

nuget restore All.Monster.sln     # nuget.exe for packages.config projects; `dotnet restore` for PackageReference
```

Then drop `--offline` (or keep it — once restored, offline still works and is faster).

---

## Troubleshooting

| symptom | cause | fix |
|---|---|---|
| `projects loaded:` lower than expected; classic projects missing | the launcher didn't switch to net472 | ensure VS / Build Tools for VS is installed and `bin\Debug\net472\CodeTracer.exe` exists (the launcher builds it) |
| `Path to file of class A …` prompt for a `map` command | a stray `--` after `.\codetracer.cmd` became the command and fell back to `trace` | drop the `--` (recent builds ignore it anyway): `.\codetracer.cmd map …` |
| `[fatal] no MSBuild could be located` (net472 build) | no full MSBuild on the machine | install Visual Studio or *Build Tools for Visual Studio* |
| restore / load tries to reach the feed and fails on auth | not using the cache | build once in VS, then run with **`--offline`** |
| `Your project targets multiple frameworks … --framework` | you ran `dotnet run` instead of the launcher | use `.\codetracer.cmd …` (or `dotnet run -f net8.0 -- …`) |
| `[index] whole-solution load failed … circular reference` | a project-reference cycle VS tolerates but Roslyn doesn't | nothing to do — CodeTracer rebuilds the graph and drops only the bad edges (`dropped N …`), then continues |
| `[auto]` can't find the net472 exe (non-standard layout) | sibling path not discoverable | set `CODETRACER_NET472=<path>\CodeTracer.exe` |
| `.slnx` solution won't open | new XML solution format, older Roslyn | use a classic `.sln` |

See [`../MANUAL.md`](../MANUAL.md) → *Legacy / mixed solutions* and the
[README](../README.md#legacy--mixed-net-framework-solutions) for the reference.
