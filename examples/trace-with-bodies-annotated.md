# Trace example with code + LLM notes (`--with-bodies --annotate`)

The same code-walk as [`trace-with-bodies.md`](trace-with-bodies.md), but `--annotate` adds a
short LLM **"why" note** per hop (the `> _…_` lines) explaining what that step achieves in the
overall chain. The model sees the prior steps, so the notes are depth-aware; trivial hops get
no note (just the self-describing code). Signatures show **parameter names**, and each call
site shows the **argument → parameter** mapping. Reproducible (needs the model running):

```bash
dotnet run -- trace -s CodeTracer.sln -f RoslynIndex.cs -e Agent.cs --no-llm --annotate \
  --repo-url https://github.com/janjanusek/code_tracer/blob/main
```

PATH FOUND (4 nodes):

**1. Agent.RunAsync(String solutionPath, String targetFile, String endpoint)**   [Agent.cs:118](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L118)
> _Executes model-requested external tool call and processes observation_

```csharp
  118      public async Task RunAsync(string solutionPath, string targetFile, string endpoint)
  119      {
  120          var seed = Bootstrap(targetFile, endpoint);
  ...
  195              string observation;
  196              try { observation = await Dispatch(tool, args); }
```
_call site: [Agent.cs:196](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L196)  ·  args: tool → tool, args → a_

↓ calls **Agent.Dispatch(String tool, JsonElement a)**

**2. Agent.Dispatch(String tool, JsonElement a)**   [Agent.cs:488](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L488)
> _Extracts symbol name from JSON and queries the Roslyn index for type information_

```csharp
  488      private async Task<string> Dispatch(string tool, JsonElement a)
  489      {
  490          string S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
  491              ? (v.GetString() ?? "") : "";
  492          int I(string k, int def) => a.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : def;
  493
  494          return tool switch
  495          {
  496              "find_symbol"     => await _index.FindSymbol(S("name")),
```
_call site: [Agent.cs:496](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L496)  ·  args: S("name") → name_

↓ calls **RoslynIndex.FindSymbol(String name)**

**3. RoslynIndex.FindSymbol(String name)**   [RoslynIndex.cs:130](https://github.com/janjanusek/code_tracer/blob/main/RoslynIndex.cs#L130)
> _Converts location object into a readable source code snippet for output_

```csharp
  130      public async Task<string> FindSymbol(string name)
  131      {
  132          var decls = await FindDeclarations(name);
  133          if (decls.Count == 0) return $"no declaration '{name}'";
  134          var sb = new System.Text.StringBuilder();
  135          foreach (var s in decls.Take(40))
  136          {
  137              var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
  138              var where = loc != null ? Rel(loc) : "?";
```
_call site: [RoslynIndex.cs:138](https://github.com/janjanusek/code_tracer/blob/main/RoslynIndex.cs#L138)  ·  args: loc → loc_

↓ calls **RoslynIndex.Rel(Location loc)**

**4. RoslynIndex.Rel(Location loc)**   [RoslynIndex.cs:38](https://github.com/janjanusek/code_tracer/blob/main/RoslynIndex.cs#L38)  (target)
