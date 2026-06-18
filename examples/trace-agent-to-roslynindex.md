# Trace example — `Agent.cs` → `RoslynIndex.cs` (brute force, all paths)

Real output from running CodeTracer on **its own** solution. Deterministic, **zero model
calls** — pure Roslyn call-graph analysis. Reproducible from this repo:

```bash
dotnet run -- trace -s CodeTracer.sln -f RoslynIndex.cs -e Agent.cs --all-paths --no-llm
```

`--all-paths` enumerates **every** distinct path from the entry (`Agent.cs`) to the target
class (`RoslynIndex.cs`), not just the first/shortest. Each hop is `Class.Method  file:line`,
so you can follow it by eye or open the location in your IDE.

```
FOUND 15 distinct path(s) [brute-force]:

### Path 1:  Agent.RunAsync  ->  RoslynIndex.Rel
PATH FOUND (4 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.FindSymbol(String)   RoslynIndex.cs:126  -->
  4. RoslynIndex.Rel(Location)   RoslynIndex.cs:38

### Path 2:  Agent.RunAsync  ->  RoslynIndex.Sig
PATH FOUND (4 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.GetMethod(String, String)   RoslynIndex.cs:140  -->
  4. RoslynIndex.Sig(IMethodSymbol)   RoslynIndex.cs:46

### Path 3:  Agent.RunAsync  ->  RoslynIndex.FindDeclarations
PATH FOUND (4 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.FindSymbol(String)   RoslynIndex.cs:126  -->
  4. RoslynIndex.FindDeclarations(String)   RoslynIndex.cs:52

### Path 4:  Agent.RunAsync  ->  RoslynIndex.ResolveMethod
PATH FOUND (4 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.GetMethod(String, String)   RoslynIndex.cs:140  -->
  4. RoslynIndex.ResolveMethod(String, String)   RoslynIndex.cs:70

### Path 5:  Agent.RunAsync  ->  RoslynIndex.GetBody
PATH FOUND (4 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.GetMethod(String, String)   RoslynIndex.cs:140  -->
  4. RoslynIndex.GetBody(IMethodSymbol)   RoslynIndex.cs:80

### Path 6:  Agent.RunAsync  ->  RoslynIndex.Outline
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.Outline(String)   RoslynIndex.cs:94

### Path 7:  Agent.RunAsync  ->  RoslynIndex.FindSymbol
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.FindSymbol(String)   RoslynIndex.cs:126

### Path 8:  Agent.RunAsync  ->  RoslynIndex.GetMethod
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.GetMethod(String, String)   RoslynIndex.cs:140

### Path 9:  Agent.RunAsync  ->  RoslynIndex.FindCallers
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.TryAutoPath()   Agent.cs:396  -->
  3. RoslynIndex.FindCallers(String, String)   RoslynIndex.cs:152

### Path 10:  Agent.RunAsync  ->  RoslynIndex.FindCallees
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.FindCallees(String, String)   RoslynIndex.cs:171

### Path 11:  Agent.RunAsync  ->  RoslynIndex.FindReferences
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.FindReferences(String, String)   RoslynIndex.cs:194

### Path 12:  Agent.RunAsync  ->  RoslynIndex.ReadFile
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.ReadFile(String, Int32, Int32)   RoslynIndex.cs:210

### Path 13:  Agent.RunAsync  ->  RoslynIndex.Grep
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Dispatch(String, JsonElement)   Agent.cs:447  -->
  3. RoslynIndex.Grep(String)   RoslynIndex.cs:223

### Path 14:  Agent.RunAsync  ->  RoslynIndex.FindPath
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.TryAutoPath()   Agent.cs:396  -->
  3. RoslynIndex.FindPath(String, String, String, String, Int32)   RoslynIndex.cs:250

### Path 15:  Agent.RunAsync  ->  RoslynIndex.MethodsInFile
PATH FOUND (3 nodes):
  1. Agent.RunAsync(String, String, String)   Agent.cs:111  -->
  2. Agent.Bootstrap(String, String)   Agent.cs:336  -->
  3. RoslynIndex.MethodsInFile(String)   RoslynIndex.cs:312
```
