# Legacy / mixed .NET Framework solution — a full walkthrough

> **This file is a *workflow* example, not a generated tool dump.** The other examples in this folder
> are real CodeTracer output produced against CodeTracer's own (modern) source. This one shows how to
> point CodeTracer at an **old, 15-year-old, mixed .NET Framework + .NET (Core)** solution behind a
> **private NuGet feed** — the exact case where "it builds in Visual Studio but won't load from the
> terminal". The console blocks below are **illustrative** (your names/counts differ); the point is the
> sequence of steps and what each line means.

---

## The situation

A real legacy solution, grown over ~15 years:

```
BigShop.sln
├─ BigShop.Web        classic non-SDK csproj, packages.config, <TargetFrameworkVersion>v4.7.1</…>
├─ BigShop.Services   classic non-SDK csproj, packages.config, net471
└─ BigShop.Core       modern SDK-style csproj, <TargetFramework>net8.0</TargetFramework>, PackageReference
```

- NuGet packages come from a **private feed** (Artifactory / Nexus / Azure DevOps). The credentials
  live **only inside Visual Studio** — the `dotnet` / `nuget` CLI can't authenticate out of the box.
- The machine is a **locked-down corporate box** (no cloud AI). It **does** have Visual Studio
  (or *Build Tools for Visual Studio*) installed.

**Why the terminal "won't build" while VS does:** Roslyn's `MSBuildWorkspace` hosts exactly one
MSBuild, fixed by the process runtime. The **net8.0** CodeTracer build gets the **.NET SDK MSBuild**,
which **cannot evaluate classic non-SDK projects** — so `BigShop.Web` / `BigShop.Services` silently
drop out. The **net472** build gets the **full Visual Studio MSBuild**, which loads **both** the
classic *and* the modern projects. CodeTracer picks the right one for you.

---

## Step 1 — build CodeTracer once (produces both runtimes)

```bash
dotnet build
#   -> bin\Debug\net8.0\CodeTracer.dll   (SDK MSBuild — modern solutions)
#   -> bin\Debug\net472\CodeTracer.exe   (full VS MSBuild — classic / mixed solutions)
```

Nothing extra to install to *build* net472 — the .NET Framework reference assemblies come via NuGet.
To *run* net472 against a real Framework solution you need VS / Build Tools on the machine (you have it).

## Step 2 — restore the target the painless way: let Visual Studio do it

CodeTracer **never restores** packages itself — it only *reads* restore output. So restore the way that
already works on this box: **open `BigShop.sln` in Visual Studio and Build (or Rebuild) it once.** VS
authenticates to the private feed with its stored credentials and fills the local NuGet caches:

- PackageReference projects (`BigShop.Core`) → `%USERPROFILE%\.nuget\packages` + `obj\project.assets.json`
- packages.config projects (`BigShop.Web`, `BigShop.Services`) → the solution's `packages\` folder

You do **not** need to touch `nuget.config` or run a CLI restore.

## Step 3 — run CodeTracer with `--offline` — it auto-routes **and** reuses the cache

Run the same one command you'd use for any solution. `--offline` tells CodeTracer to resolve packages
**only** from the caches VS just filled and to contact **no** feed:

```bash
# the launcher runs with NO framework flag and auto-switches to net472 for this legacy solution:
.\codetracer map -s BigShop.sln --method "OrderService.PlaceOrder" --down --offline
# equivalent without the launcher (one build that loads everything on a VS box):
bin\Debug\net472\CodeTracer.exe map -s BigShop.sln --method "OrderService.PlaceOrder" --down --offline
```

Console (illustrative):

```text
[auto] classic (non-SDK) .NET Framework project(s) detected -> switching to the .NET Framework build for full MSBuild:
       C:\tools\code_tracer\bin\Debug\net472\CodeTracer.exe
[cfg] mode=map  directions=down  depth=64  max-nodes=400  (deterministic, no model)
[index] offline: reusing the local NuGet cache only, no feed (config: C:\Users\you\AppData\Local\Temp\codetracer-offline.nuget.config)
[index] loading solution: BigShop.sln
[workspace] Msbuild ... skipping analyzer ...        <- these warnings are normal, load continues
[index] projects loaded: 3                            <- all three, incl. the two classic net471 projects
[map] building downstream (callees) map ...
[map] wrote codetracer-map-down-OrderService.PlaceOrder.md
```

What those lines mean:

| line | meaning |
|---|---|
| `[auto] … switching to the .NET Framework build` | you started the net8.0 build, it saw a classic project, and **re-launched the net472 build** for you — no `-f`, no version-hunting |
| `[index] offline: reusing the local NuGet cache only` | `--offline` wrote a throwaway config (cleared sources + fallback to VS's cache); the private feed is **never** contacted, your `nuget.config` is untouched |
| `[index] projects loaded: 3` | the classic **and** modern projects all loaded — the whole-solution call graph is available |

If you instead see `projects loaded: 1` (only the modern one), you're on the net8.0 build **without**
the net472 build present, or VS / Build Tools isn't installed — see *Troubleshooting* below.

## Step 4 — use it normally

From here every mode works across the mixed solution. A couple of legacy-friendly, **model-free**
(deterministic, instant) commands:

```bash
# Impact analysis on a classic net471 service: who (transitively) reaches this method?
bin\Debug\net472\CodeTracer.exe map -s BigShop.sln --method "PricingRepository.Save" --up --offline

# The whole call chain from an MVC endpoint down to a target class, all paths, no model:
bin\Debug\net472\CodeTracer.exe trace -s BigShop.sln -f Pricing\PricingEngine.cs \
  -e "OrdersController.Create" --all-paths --no-llm --offline
```

The result ends with the auto `## Call-flow` map (ASCII + Mermaid), e.g.:

```text
OrdersController.Create   ◆ start        BigShop.Web\Controllers\OrdersController.cs:42
└─► OrderService.PlaceOrder              BigShop.Services\OrderService.cs:88
    ├─► PricingEngine.Quote   ★ target   BigShop.Services\Pricing\PricingEngine.cs:15
    └─► PricingRepository.Save           BigShop.Services\Data\PricingRepository.cs:120
```

---

## Fallback — if nothing is cached and you must hit the feed

Only needed when VS hasn't restored (fresh clone, cleared cache). The CLI doesn't share VS's stored
credentials, so add them once (basic auth = username + API token), then restore:

```bash
# Nexus (NuGet v3 hosted repo). Token: Nexus UI -> your profile -> "NuGet API Key".
dotnet nuget add source "https://nexus.example.com/repository/nuget-hosted/index.json" \
  -n NexusFeed -u <user> -p <nuget-api-token> --store-password-in-clear-text

# Artifactory: source URL https://artifactory.example.com/artifactory/api/nuget/v3/<repo>
#   token from the repo's "Set Me Up" -> NuGet panel.

# Azure DevOps Artifacts: dotnet restore BigShop.sln --interactive   (Azure Artifacts Credential Provider)

nuget restore BigShop.sln     # nuget.exe for packages.config projects; `dotnet restore` for PackageReference
```

Then drop `--offline` (or keep it — once restored, offline still works and is faster).

---

## Troubleshooting

| symptom | cause | fix |
|---|---|---|
| `projects loaded:` lower than expected; classic projects missing | on the net8.0 build, no auto-route happened | ensure `bin\Debug\net472\CodeTracer.exe` exists (`dotnet build`) **and** VS / Build Tools for VS is installed; CodeTracer then switches automatically |
| `[fatal] no MSBuild could be located` (net472 build) | no full MSBuild on the machine | install Visual Studio or *Build Tools for Visual Studio* |
| restore / load tries to reach the feed and fails on auth | not using the cache | build once in VS, then run with **`--offline`** |
| `[auto]` can't find the net472 exe (non-standard layout) | sibling path not discoverable | set `CODETRACER_NET472=<path>\CodeTracer.exe` |
| `.slnx` solution won't open | new XML solution format, older Roslyn | use a classic `.sln` |

See [`../MANUAL.md`](../MANUAL.md) → *Legacy / mixed solutions* and the
[README](../README.md#legacy--mixed-net-framework-solutions) for the reference.
