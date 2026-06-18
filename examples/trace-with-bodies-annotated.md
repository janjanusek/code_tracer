# Trace example with code + LLM notes (`--with-bodies --annotate`)

The same code-walk as [`trace-with-bodies.md`](trace-with-bodies.md), but `--annotate` adds a
short LLM **"why" note** per hop (the `> _…_` lines) explaining what that step achieves in the
overall chain. The model sees the prior steps, so the notes are depth-aware; trivial hops get
no note (just the self-describing code). Signatures show **parameter names**, each call site
shows the **argument → parameter** mapping, and the **target** node shows its full body (where
the chain ends). `--summary` adds a final LLM **Summary** section (purpose, dependencies,
good-to-know). Reproducible (needs the model running):

```bash
dotnet run -- trace -s CodeTracer.sln -f RoslynIndex.cs -e Agent.cs --no-llm --annotate --summary \
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

**2. Agent.Dispatch(String tool, JsonElement a)**   [Agent.cs:494](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L494)
> _Extracts symbol name from JSON and queries code index for definition location_

```csharp
  494      private async Task<string> Dispatch(string tool, JsonElement a)
  495      {
  496          string S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
  497              ? (v.GetString() ?? "") : "";
  498          int I(string k, int def) => a.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : def;
  499
  500          return tool switch
  501          {
  502              "find_symbol"     => await _index.FindSymbol(S("name")),
```
_call site: [Agent.cs:502](https://github.com/janjanusek/code_tracer/blob/main/Agent.cs#L502)  ·  args: S("name") → name_

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
> _Formats location object into file path and line number string_

```csharp
   38      private string Rel(Location loc)
   39      {
   40          var span = loc.GetLineSpan();
   41          var path = span.Path;
   42          try { path = Path.GetRelativePath(SolutionDir, path); } catch { /* keep absolute */ }
   43          return $"{path}:{span.StartLinePosition.Line + 1}";
   44      }
```

## Summary

This call chain executes the final step of an agent-based system designed to resolve and
report specific locations (symbols) within a large codebase. It translates internal code
location objects into human-readable file paths and line numbers.

**1. What it does / purpose**
The primary goal is to determine the exact source code reference for a given symbol name. The
chain starts with an agent attempting to find a path (`Agent.RunAsync`), which delegates the
task of finding declarations to `RoslynIndex`. Finally, when a declaration object is found,
`RoslynIndex.Rel` extracts the file system path and line number from the internal location data.

**2. Dependencies**
* **Roslyn Indexing:** relies on the Roslyn compiler services (`RoslynIndex`) for static
  analysis (finding declarations / symbols).
* **File System Operations:** uses `System.IO.Path` (`GetRelativePath`) to normalize file paths
  relative to the solution directory.
* **Agent State:** the process is embedded within an LLM agent loop, so it relies on the agent
  receiving tool calls (e.g. `"find_symbol"`) from a language model.

**3. Good to know**
* **Deterministic Fallback:** the system prioritizes deterministic path finding (Roslyn) before
  engaging the LLM — reliability and speed for common cases.
* **Output Format:** `RoslynIndex.Rel` guarantees the format `[relative_path]:[line_number]`.
* **Scope Limitation:** path resolution is limited to declarations Roslyn finds within the
  provided solution directory; if the location is not in source, it defaults to `"?"`.
