# Trace example with code (`--with-bodies`)

The same trace, but between each hop CodeTracer inserts the method's code **from its start
down to the exact line where it calls the next hop** — so you read the actual flow, not just
method names. With `--repo-url` every location and call site is a clickable link to the file
in the repo. Deterministic, **zero model calls**. Reproducible:

```bash
dotnet run -- trace -s CodeTracer.sln -f RoslynIndex.cs -e Agent.cs --no-llm --with-bodies \
  --repo-url https://github.com/janjanusek/code_tracer/blob/main
```

PATH FOUND (4 nodes):

**1. Agent.RunAsync(String, String, String)**   [Agent.cs:116](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L116)

```csharp
  116      public async Task RunAsync(string solutionPath, string targetFile, string endpoint)
  117      {
  118          var seed = Bootstrap(targetFile, endpoint);
  119
  120          // Deterministicky pre-flight: skus kandidatske find_path dvojice HNED. Na CPU je to
  121          // rychlejsie a spolahlivejsie nez cakat na (casto podvyplnene) volania modelu. Roslyn
  122          // je zdroj pravdy; model je tu len na navigaciu tazsich pripadov (interface/DI/eventy).
  123          // --all-paths/--brute: enumeruj VSETKY cesty (deep), nie len prvu najkratsiu.
  124          var mode = _allPaths ? "brute-force (all paths)" : "first path";
  125          Console.WriteLine($"[pre-flight] deterministic find_path over {_pairs.Count} candidate pairs [{mode}]...");
  126          var deterministic = _allPaths ? await TryAllPaths() : await TryAutoPath();
      // … (some lines omitted) …
  192
  193              string observation;
  194              try { observation = await Dispatch(tool, args); }
```
_call site: [Agent.cs:194](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L194)_

↓ calls **Agent.Dispatch(String, JsonElement)**

**2. Agent.Dispatch(String, JsonElement)**   [Agent.cs:453](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L453)

```csharp
  453      private async Task<string> Dispatch(string tool, JsonElement a)
  454      {
  455          string S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
  456              ? (v.GetString() ?? "") : "";
  457          int I(string k, int def) => a.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : def;
  458
  459          return tool switch
  460          {
  461              "find_symbol"     => await _index.FindSymbol(S("name")),
```
_call site: [Agent.cs:461](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L461)_

↓ calls **RoslynIndex.FindSymbol(String)**

**3. RoslynIndex.FindSymbol(String)**   [RoslynIndex.cs:126](https://github.com/janjanusek/code_tracer/blob/main/RoslynIndex.cs#L126)

```csharp
  126      public async Task<string> FindSymbol(string name)
  127      {
  128          var decls = await FindDeclarations(name);
  129          if (decls.Count == 0) return $"no declaration '{name}'";
  130          var sb = new System.Text.StringBuilder();
  131          foreach (var s in decls.Take(40))
  132          {
  133              var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
  134              var where = loc != null ? Rel(loc) : "?";
```
_call site: [RoslynIndex.cs:134](https://github.com/janjanusek/code_tracer/blob/main/RoslynIndex.cs#L134)_

↓ calls **RoslynIndex.Rel(Location)**

**4. RoslynIndex.Rel(Location)**   [RoslynIndex.cs:38](https://github.com/janjanusek/code_tracer/blob/main/RoslynIndex.cs#L38)  (target)
