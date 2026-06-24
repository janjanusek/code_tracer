using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeTracer;

/// <summary>
/// Wrapper around the Roslyn workspace. Holds the loaded solution and provides
/// precise analysis: definitions, references, callers, callees, and the shortest
/// path in the call graph. Everything is deterministic - the LLM only decides what to call.
/// </summary>
public class RoslynIndex
{
    private Solution _solution = null!;
    public string SolutionDir { get; private set; } = "";

    public async Task LoadAsync(string solutionPath, bool offline = false)
    {
        SolutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? "";
        Console.Error.WriteLine($"[index] loading solution: {solutionPath}");

        // Offline: reuse what Visual Studio already restored - resolve packages ONLY from the local
        // caches, with NO remote source, so loading never contacts a (private/auth) feed.
        ImmutableDictionary<string, string>? props = null;
        if (offline)
        {
            var cfg = WriteOfflineNuGetConfig();
            Console.Error.WriteLine($"[index] offline: reusing the local NuGet cache only, no feed (config: {cfg})");
            props = ImmutableDictionary<string, string>.Empty.Add("RestoreConfigFile", cfg);
        }

        var workspace = CreateWorkspace(props);
        try
        {
            _solution = await workspace.OpenSolutionAsync(solutionPath);
        }
        catch (Exception ex)
        {
            // OpenSolutionAsync is all-or-nothing: a single illegal edge (e.g. a circular project
            // reference - VS allows it, Roslyn's immutable Solution model does not) drops the WHOLE
            // solution. Fall back to rebuilding the graph from project infos, keeping every project
            // and dropping ONLY the offending references.
            Console.Error.WriteLine($"[index] whole-solution load failed: {FirstLine(ex.Message)}");
            Console.Error.WriteLine("[index] rebuilding graph, dropping only the bad (cyclic/duplicate) project references...");
            workspace.Dispose();
            _solution = await LoadResilient(props, solutionPath);
        }

        var projCount = _solution.Projects.Count();
        Console.Error.WriteLine($"[index] projects loaded: {projCount}");
    }

    private static MSBuildWorkspace CreateWorkspace(ImmutableDictionary<string, string>? props)
    {
        var ws = props != null ? MSBuildWorkspace.Create(props) : MSBuildWorkspace.Create();
        ws.WorkspaceFailed += (_, e) =>
        {
            // Warnings during load are common (missing analyzers, package TFM mismatches, etc.) - log only.
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                Console.Error.WriteLine($"[workspace] {e.Diagnostic.Message}");
        };
        return ws;
    }

    /// Resilient load: evaluate every project, then re-assemble the Solution graph by hand, skipping
    /// any project reference that Roslyn rejects (a cycle / duplicate). Every project still loads, so
    /// callers/callees analysis works across the whole solution except the few dropped edges.
    private async Task<Solution> LoadResilient(ImmutableDictionary<string, string>? props, string solutionPath)
    {
        var workspace = CreateWorkspace(props);
        var loader = props != null ? new MSBuildProjectLoader(workspace, props) : new MSBuildProjectLoader(workspace);
        var info = await loader.LoadSolutionInfoAsync(solutionPath);

        var sol = workspace.CurrentSolution;
        foreach (var pi in info.Projects)                               // add projects WITHOUT refs first
            sol = sol.AddProject(pi.WithProjectReferences(Array.Empty<ProjectReference>()));

        int dropped = 0;
        foreach (var pi in info.Projects)                               // then add refs, skipping bad ones
            foreach (var pref in pi.ProjectReferences)
            {
                try { sol = sol.AddProjectReference(pi.Id, pref); }
                catch { dropped++; }                                    // cyclic/duplicate -> keep both projects, drop the edge
            }
        if (dropped > 0)
            Console.Error.WriteLine($"[index] dropped {dropped} cyclic/duplicate project reference(s) - analysis works across the rest.");
        return sol;
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s : s.Substring(0, i);
    }

    /// Writes a throwaway nuget.config that resolves packages ONLY from the machine's existing caches
    /// (the global packages folder Visual Studio already populated) with NO remote source - so loading
    /// a solution never contacts a private/auth feed. The user's real nuget.config is left untouched.
    private static string WriteOfflineNuGetConfig()
    {
        var globalPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(globalPackages))
            globalPackages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        // <packageSources><clear/></packageSources> = no feeds at all; fallbackPackageFolders points at
        // the cache VS filled, so PackageReference resolves fully offline. (packages.config projects
        // resolve via their <HintPath> into the solution's packages/ folder - also offline.)
        var xml =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
  <fallbackPackageFolders>
    <clear />
    <add key=""vs-global-cache"" value=""{globalPackages}"" />
  </fallbackPackageFolders>
</configuration>";

        var path = Path.Combine(Path.GetTempPath(), "codetracer-offline.nuget.config");
        File.WriteAllText(path, xml);
        return path;
    }

    private string Rel(Location loc)
    {
        var span = loc.GetLineSpan();
        var path = span.Path;
        try { path = Compat.GetRelativePath(SolutionDir, path); } catch { /* keep absolute */ }
        return $"{path}:{span.StartLinePosition.Line + 1}";
    }

    private static string Sig(IMethodSymbol m) =>
        $"{m.ContainingType?.Name}.{m.Name}({string.Join(", ", m.Parameters.Select(p => p.Type.Name))})";

    // Like Sig but includes parameter NAMES: Class.Method(Type name, Type name).
    private static string SigNamed(IMethodSymbol m) =>
        $"{m.ContainingType?.Name}.{m.Name}({string.Join(", ", m.Parameters.Select(p => $"{p.Type.Name} {p.Name}"))})";

    // Readable "Type.Member" for the graph: a ctor's metadata name is ".ctor" and an accessor's is
    // "get_X" - render them as Type.ctor / Type.Prop.get|set instead of the raw, double-dotted names.
    private static string DisplayName(IMethodSymbol m) => m.MethodKind switch
    {
        MethodKind.Constructor or MethodKind.StaticConstructor => $"{m.ContainingType?.Name}.ctor",
        MethodKind.PropertyGet => $"{m.ContainingType?.Name}.{m.AssociatedSymbol?.Name}.get",
        MethodKind.PropertySet => $"{m.ContainingType?.Name}.{m.AssociatedSymbol?.Name}.set",
        _ => $"{m.ContainingType?.Name}.{m.Name}",
    };

    // ---- symbol resolution -------------------------------------------------

    /// Finds all declarations whose name matches (case-insensitive).
    public async Task<List<ISymbol>> FindDeclarations(string name)
    {
        var result = new List<ISymbol>();
        foreach (var project in _solution.Projects)
        {
            var found = await SymbolFinder.FindDeclarationsAsync(
                project, name, ignoreCase: true,
                filter: SymbolFilter.TypeAndMember);
            result.AddRange(found);
        }
        // deduplicate by definition
        return result
            .GroupBy(s => s, SymbolEqualityComparer.Default)
            .Select(g => g.First())
            .ToList();
    }

    /// Resolves a method from "Class" + "Method". If several match (overloads, or same class
    /// name in different namespaces), it WARNS and lists them, then uses the first - there is no
    /// overload/namespace disambiguation, so make the class name as specific as the codebase allows.
    /// All method-like symbols for "Class.Member": ordinary methods, plus the type's source constructors
    /// when the name denotes one ("Class.Class" / "Class.ctor" / "Class..ctor" / "Class.new").
    private async Task<List<IMethodSymbol>> GatherMethods(string className, string methodName)
    {
        var methods = (await FindDeclarations(methodName)).OfType<IMethodSymbol>()
            .Where(m => m.ContainingType != null &&
                        string.Equals(m.ContainingType.Name, className, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (methods.Count == 0 && IsCtorRef(className, methodName))
            foreach (var type in (await FindDeclarations(className)).OfType<INamedTypeSymbol>()
                         .Where(t => string.Equals(t.Name, className, StringComparison.OrdinalIgnoreCase)))
                methods.AddRange(type.InstanceConstructors.Where(c => c.Locations.Any(l => l.IsInSource)));
        return methods;
    }

    /// Resolves a method/constructor from "Class" + "Member". On a collision (overloads, or the same class
    /// name in different namespaces) it ASKS which one (listing full namespace + signature + location).
    public async Task<IMethodSymbol?> ResolveMethod(string className, string methodName) =>
        PickSymbols($"{className}.{methodName}", await GatherMethods(className, methodName), allowAll: false).FirstOrDefault();

    /// Resolve map root(s): a method/ctor, else a property's get/set accessor. Interactive on a collision,
    /// with an "all" option to map every match at once.
    public async Task<List<IMethodSymbol>> ResolveMapRoots(string? className, string? methodName, string? filePath, int line)
    {
        if (className == null)
        {
            var byLoc = await ResolveByLocation(filePath!, line);
            return byLoc != null ? new List<IMethodSymbol> { byLoc } : new List<IMethodSymbol>();
        }
        var methods = await GatherMethods(className, methodName!);
        if (methods.Count > 0)
            return PickSymbols($"{className}.{methodName}", methods, allowAll: true);

        // not a method/ctor -> a property: map the get (else set) accessor of the chosen property
        var props = (await FindDeclarations(methodName!)).OfType<IPropertySymbol>()
            .Where(p => p.ContainingType != null &&
                        string.Equals(p.ContainingType.Name, className, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var chosen = PickSymbols($"{className}.{methodName}", props, allowAll: true);
        if (chosen.Count > 0)
            Console.Error.WriteLine($"[map] '{className}.{methodName}' is a property - mapping its accessor.");
        return chosen.Select(p => p.GetMethod ?? p.SetMethod).Where(a => a != null).Cast<IMethodSymbol>().ToList();
    }

    private static bool IsCtorRef(string className, string methodName) =>
        string.Equals(methodName, className, StringComparison.OrdinalIgnoreCase)
        || string.Equals(methodName, "ctor", StringComparison.OrdinalIgnoreCase)
        || string.Equals(methodName, ".ctor", StringComparison.OrdinalIgnoreCase)
        || string.Equals(methodName, "new", StringComparison.OrdinalIgnoreCase);

    /// When several symbols match, list them (full namespace + signature + location) and let the user pick
    /// one - or, when allowAll, 'a' for all. Falls back to the first when input isn't available (piped/empty).
    private List<T> PickSymbols<T>(string what, List<T> candidates, bool allowAll) where T : ISymbol
    {
        candidates = candidates.GroupBy(s => s, SymbolEqualityComparer.Default).Select(g => g.First()).ToList();
        if (candidates.Count <= 1) return candidates;

        Console.Error.WriteLine($"[ambiguous] {candidates.Count} matches for \"{what}\":");
        for (int i = 0; i < candidates.Count; i++)
            Console.Error.WriteLine($"   [{i + 1}] {candidates[i].ToDisplayString(FullSig)}   {WhereOf(candidates[i])}");
        Console.Error.Write(allowAll ? "   pick a number, or 'a' for all: " : "   pick a number: ");

        // keep only letters/digits - strips a BOM / stray whitespace that piped input can prepend
        var input = new string((Console.ReadLine() ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (allowAll && (string.Equals(input, "a", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(input, "all", StringComparison.OrdinalIgnoreCase)))
            return candidates;
        if (int.TryParse(input, out var n) && n >= 1 && n <= candidates.Count)
            return new List<T> { candidates[n - 1] };

        Console.Error.WriteLine($"[ambiguous] no valid choice ('{input}') - using [1].");
        return new List<T> { candidates[0] };
    }

    private string WhereOf(ISymbol s)
    {
        var l = s.Locations.FirstOrDefault(x => x.IsInSource);
        return l != null ? Rel(l) : "";
    }

    private static readonly SymbolDisplayFormat FullSig = new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// Full, unambiguous label for a symbol: namespace + type + signature (used to disambiguate "all").
    public string FullLabel(IMethodSymbol m) => m.ToDisplayString(FullSig);

    /// Resolves a property (or indexer) from "Class" + "Name". Same ambiguity handling as methods.
    public async Task<IPropertySymbol?> ResolveProperty(string className, string propName)
    {
        var decls = await FindDeclarations(propName);
        var props = decls.OfType<IPropertySymbol>()
            .Where(p => p.ContainingType != null &&
                        string.Equals(p.ContainingType.Name, className, StringComparison.OrdinalIgnoreCase))
            .ToList();
        WarnIfAmbiguous($"{className}.{propName}", props.Cast<ISymbol>().ToList());
        return props.FirstOrDefault();
    }

    /// Surfaces ambiguity (more than one match) to stderr so the user knows which one was picked.
    private void WarnIfAmbiguous(string what, List<ISymbol> matches)
    {
        if (matches.Count <= 1) return;
        Console.Error.WriteLine($"[warn] '{what}' is ambiguous - {matches.Count} matches; using the first:");
        foreach (var s in matches.Take(6))
        {
            var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
            Console.Error.WriteLine($"[warn]   {s.ToDisplayString(ShortType)}  {(loc != null ? Rel(loc) : "?")}");
        }
    }

    private async Task<(SemanticModel model, SyntaxNode node)?> GetBody(IMethodSymbol method)
    {
        var sref = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (sref == null) return null;
        var node = await sref.GetSyntaxAsync();
        var doc = _solution.GetDocument(node.SyntaxTree);
        if (doc == null) return null;
        var model = await doc.GetSemanticModelAsync();
        if (model == null) return null;
        return (model, node);
    }

    // ---- tools (return compact text for the LLM) ----------------------------

    public string Outline(string filePath)
    {
        var full = Path.IsPathRooted(filePath) ? filePath : Path.Combine(SolutionDir, filePath);
        var doc = _solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath ?? ""), Path.GetFullPath(full),
                                               StringComparison.OrdinalIgnoreCase));
        if (doc == null) return $"file is not part of the solution: {filePath}";

        var tree = doc.GetSyntaxTreeAsync().Result;
        var root = tree!.GetRoot();
        var sb = new System.Text.StringBuilder();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            sb.AppendLine($"{type.Keyword.ValueText} {type.Identifier.ValueText}");
            foreach (var member in type.Members)
            {
                if (member is MethodDeclarationSyntax md)
                {
                    var line = md.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    sb.AppendLine($"    method {md.Identifier.ValueText}(...)  :{line}");
                }
                else if (member is PropertyDeclarationSyntax pd)
                {
                    var line = pd.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    sb.AppendLine($"    prop   {pd.Identifier.ValueText}  :{line}");
                }
            }
        }
        return sb.Length == 0 ? "no types in file" : sb.ToString();
    }

    public async Task<string> FindSymbol(string name)
    {
        var decls = await FindDeclarations(name);
        if (decls.Count == 0) return $"no declaration '{name}'";
        var sb = new System.Text.StringBuilder();
        foreach (var s in decls.Take(40))
        {
            var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
            var where = loc != null ? Rel(loc) : "?";
            sb.AppendLine($"{s.Kind} {s.ContainingType?.Name}{(s.ContainingType != null ? "." : "")}{s.Name}  {where}");
        }
        return sb.ToString();
    }

    public async Task<string> GetMethod(string className, string methodName)
    {
        var m = await ResolveMethod(className, methodName);
        if (m == null) return $"method {className}.{methodName} not found";
        var body = await GetBody(m);
        if (body == null) return $"{Sig(m)} - no source available (possibly from metadata)";
        var loc = m.Locations.First(l => l.IsInSource);
        var src = body.Value.node.ToString();
        if (src.Length > 4000) src = src[..4000] + "\n... (truncated)";
        return $"{Rel(loc)}\n{src}";
    }

    public async Task<string> FindCallers(string className, string methodName)
    {
        var m = await ResolveMethod(className, methodName);
        if (m == null) return $"method {className}.{methodName} not found";
        var callers = await SymbolFinder.FindCallersAsync(m, _solution);
        var sb = new System.Text.StringBuilder();
        int n = 0;
        foreach (var c in callers)
        {
            if (c.CallingSymbol is not IMethodSymbol cm) continue;
            foreach (var loc in c.Locations)
            {
                sb.AppendLine($"{Sig(cm)}  ->called at {Rel(loc)}  {(c.IsDirect ? "" : "(indirect)")}");
                if (++n >= 40) { sb.AppendLine("... (truncated)"); return sb.ToString(); }
            }
        }
        return sb.Length == 0 ? "no callers (possibly entry-point / DI / reflection)" : sb.ToString();
    }

    public async Task<string> FindCallees(string className, string methodName)
    {
        var m = await ResolveMethod(className, methodName);
        if (m == null) return $"method {className}.{methodName} not found";
        var body = await GetBody(m);
        if (body == null) return "method source unavailable";

        var (model, node) = body.Value;
        var sb = new System.Text.StringBuilder();
        var seen = new HashSet<string>();
        foreach (var inv in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol callee)
            {
                var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var key = $"{Sig(callee)}@{line}";
                if (seen.Add(key))
                    sb.AppendLine($"{Sig(callee)}  :{line}  [declared in {callee.ContainingType?.Name}]");
            }
        }
        return sb.Length == 0 ? "method calls no (resolved) methods" : sb.ToString();
    }

    public async Task<string> FindReferences(string className, string methodName)
    {
        var m = await ResolveMethod(className, methodName);
        if (m == null) return $"symbol {className}.{methodName} not found";
        var refs = await SymbolFinder.FindReferencesAsync(m, _solution);
        var sb = new System.Text.StringBuilder();
        int n = 0;
        foreach (var r in refs)
            foreach (var loc in r.Locations)
            {
                sb.AppendLine(Rel(loc.Location));
                if (++n >= 50) { sb.AppendLine("... (truncated)"); return sb.ToString(); }
            }
        return sb.Length == 0 ? "no references" : sb.ToString();
    }

    public string ReadFile(string filePath, int start, int end)
    {
        var full = Path.IsPathRooted(filePath) ? filePath : Path.Combine(SolutionDir, filePath);
        if (!File.Exists(full)) return $"file does not exist: {filePath}";
        var lines = File.ReadAllLines(full);
        start = Math.Max(1, start);
        end = Math.Min(lines.Length, end <= 0 ? start + 40 : end);
        var sb = new System.Text.StringBuilder();
        for (int i = start; i <= end; i++)
            sb.AppendLine($"{i,5}  {lines[i - 1]}");
        return sb.ToString();
    }

    public string Grep(string pattern)
    {
        var sb = new System.Text.StringBuilder();
        int n = 0;
        foreach (var doc in _solution.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath == null || !File.Exists(doc.FilePath)) continue;
            var lines = File.ReadAllLines(doc.FilePath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var rel = Compat.GetRelativePath(SolutionDir, doc.FilePath);
                    sb.AppendLine($"{rel}:{i + 1}:  {lines[i].Trim()}");
                    if (++n >= 40) { sb.AppendLine("... (truncated)"); return sb.ToString(); }
                }
            }
        }
        return sb.Length == 0 ? "no match" : sb.ToString();
    }

    /// <summary>
    /// Deterministic shortest path in the call graph from (fromClass.fromMethod)
    /// to (toClass.toMethod). BFS going UPWARD through the target's callers until
    /// we reach the source. This is the most reliable operation - the LLM just
    /// calls it with the result of find_symbol/outline and then interprets the output.
    /// </summary>
    public async Task<string> FindPath(string fromClass, string fromMethod, string toClass, string toMethod,
                                       int maxNodes = 3000, bool withBodies = false, string? repoUrl = null,
                                       Func<string, string, string, string, Task<string?>>? annotate = null)
    {
        var start = await ResolveMethod(fromClass, fromMethod);
        var target = await ResolveMethod(toClass, toMethod);
        if (start == null) return $"source method {fromClass}.{fromMethod} not found";
        if (target == null) return $"target method {toClass}.{toMethod} not found";

        var cmp = SymbolEqualityComparer.Default;
        if (cmp.Equals(start, target)) return "source == target (same method)";

        var queue = new Queue<IMethodSymbol>();
        var visited = new HashSet<ISymbol>(cmp) { target };
        var calledBy = new Dictionary<ISymbol, IMethodSymbol>(cmp); // caller -> what it called (toward the target)

        queue.Enqueue(target);
        int explored = 0;

        while (queue.Count > 0 && explored < maxNodes)
        {
            var current = queue.Dequeue();
            explored++;

            var callers = await SymbolFinder.FindCallersAsync(current, _solution);
            foreach (var c in callers)
            {
                if (c.CallingSymbol is not IMethodSymbol caller) continue;
                if (visited.Contains(caller)) continue;
                visited.Add(caller);
                calledBy[caller] = current; // caller calls 'current' (direction toward the target)

                if (cmp.Equals(caller, start))
                {
                    // reconstruct start -> ... -> target
                    var path = new List<IMethodSymbol> { start };
                    var node = (IMethodSymbol)start;
                    while (!cmp.Equals(node, target))
                    {
                        node = calledBy[node];
                        path.Add(node);
                    }
                    return await RenderPath(path, withBodies, repoUrl, annotate);
                }
                queue.Enqueue(caller);
            }
        }
        return $"path not found (explored {explored} nodes). " +
               "Interface/DI calls ARE followed (Roslyn bridges interface members to their " +
               "implementations), so this usually means a purely dynamic link: reflection " +
               "(Activator.CreateInstance / MethodInfo.Invoke), `dynamic`, or a handler wired up at " +
               "runtime. Try find_callers manually, or find_callees from the source going down.";
    }

    /// Enumerates ALL distinct paths from (fromClass.fromMethod) to (toClass.toMethod), not just the
    /// shortest. This surfaces the case the matters for DI: an interface with MULTIPLE implementations
    /// where more than one implementation reaches the target - each becomes its own path. Bounded by
    /// maxPaths / maxDepth / maxNodes so it can't explode on a large graph.
    public async Task<string> FindAllPaths(string fromClass, string fromMethod, string toClass, string toMethod,
                                           int maxPaths = 12, int maxDepth = 15, int maxNodes = 20000,
                                           bool withBodies = false, string? repoUrl = null,
                                           Func<string, string, string, string, Task<string?>>? annotate = null)
    {
        var start = await ResolveMethod(fromClass, fromMethod);
        var target = await ResolveMethod(toClass, toMethod);
        if (start == null) return $"source method {fromClass}.{fromMethod} not found";
        if (target == null) return $"target method {toClass}.{toMethod} not found";
        var cmp = SymbolEqualityComparer.Default;
        if (cmp.Equals(start, target)) return "source == target (same method)";

        var callerCache = new Dictionary<ISymbol, List<IMethodSymbol>>(cmp);
        async Task<List<IMethodSymbol>> Callers(IMethodSymbol node)
        {
            if (callerCache.TryGetValue(node, out var cached)) return cached;
            var list = new List<IMethodSymbol>();
            foreach (var c in await SymbolFinder.FindCallersAsync(node, _solution))
                if (c.CallingSymbol is IMethodSymbol m) list.Add(m);
            callerCache[node] = list;
            return list;
        }

        var found = new List<List<IMethodSymbol>>();
        var onStack = new HashSet<ISymbol>(cmp) { target };
        var path = new List<IMethodSymbol> { target };   // target-first, going UP
        int explored = 0;

        async Task Dfs(IMethodSymbol node)
        {
            if (found.Count >= maxPaths || path.Count > maxDepth || explored >= maxNodes) return;
            explored++;
            foreach (var caller in await Callers(node))
            {
                if (cmp.Equals(caller, start))
                {
                    var full = new List<IMethodSymbol>(path) { start };
                    full.Reverse();                       // start -> ... -> target
                    found.Add(full);
                    if (found.Count >= maxPaths) return;
                    continue;
                }
                if (onStack.Contains(caller)) continue;   // no cycles within one path
                onStack.Add(caller); path.Add(caller);
                await Dfs(caller);
                path.RemoveAt(path.Count - 1); onStack.Remove(caller);
                if (found.Count >= maxPaths) return;
            }
        }
        await Dfs(target);

        // dedup by signature sequence, shortest first
        var seen = new HashSet<string>();
        var distinct = found.OrderBy(p => p.Count)
                            .Where(p => seen.Add(string.Join("|", p.Select(Sig))))
                            .ToList();
        if (distinct.Count == 0)
            return $"path not found from {fromClass}.{fromMethod} to {toClass}.{toMethod} (explored {explored} nodes).";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"FOUND {distinct.Count} distinct path(s) [all]" +
                      (found.Count >= maxPaths ? $" (capped at {maxPaths})" : "") + ":");
        int n = 0;
        foreach (var p in distinct)
        {
            n++;
            if (n > 1) { sb.AppendLine(); sb.AppendLine("---"); }
            sb.AppendLine();
            sb.AppendLine($"### Path {n}:  {fromClass}.{fromMethod}  ->  {toClass}.{toMethod}");
            sb.AppendLine((await RenderPath(p, withBodies, repoUrl, annotate)).Trim());
        }
        return sb.ToString();
    }

    /// Renders the found path. withBodies=false -> compact list (for the model and default trace).
    /// withBodies=true -> inserts the method body FROM its beginning UP TO the call site of the next step.
    /// annotate (optional) -> per-hop callback (LLM) returning a short "why" note, or null to omit it.
    private async Task<string> RenderPath(List<IMethodSymbol> path, bool withBodies, string? repoUrl,
                                          Func<string, string, string, string, Task<string?>>? annotate)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PATH FOUND ({path.Count} nodes):");
        if (!withBodies)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var loc0 = path[i].Locations.FirstOrDefault(l => l.IsInSource);
                var where0 = loc0 != null ? Rel(loc0) : "?";
                var arrow = i < path.Count - 1 ? "  -->" : "";
                sb.AppendLine($"  {i + 1}. {Sig(path[i])}   {RepoLink(where0, repoUrl)}{arrow}");
            }
            return sb.ToString();
        }

        // Overview of the whole chain + a running trail of prior steps/notes, so the annotator
        // understands the depth and where the current hop sits.
        var pathOverview = "Full call chain: " + string.Join("  →  ", path.Select(Sig));
        var trail = new System.Text.StringBuilder();

        for (int i = 0; i < path.Count; i++)
        {
            var loc = path[i].Locations.FirstOrDefault(l => l.IsInSource);
            var where = loc != null ? Rel(loc) : "?";
            bool isTarget = i == path.Count - 1;
            var tag = isTarget ? "  (target)" : "";
            sb.AppendLine();
            sb.AppendLine($"**{i + 1}. {SigNamed(path[i])}**   {RepoLink(where, repoUrl)}{tag}");

            // Non-target nodes: body up to the call of the next hop. Target node: its FULL body
            // (the destination - where the chain ends), so the reader gets the full picture.
            var calleeSig = isTarget ? "" : SigNamed(path[i + 1]);
            var (rendered, rawCode) = isTarget
                ? await MethodBodyWithRaw(path[i], repoUrl)
                : await SnippetUpToCall(path[i], path[i + 1], repoUrl);

            if (annotate != null)
            {
                var context = pathOverview + (trail.Length > 0 ? "\nSteps so far:\n" + trail : "");
                var note = await annotate(context, SigNamed(path[i]), calleeSig, rawCode);
                if (!string.IsNullOrWhiteSpace(note))
                {
                    sb.AppendLine($"> _{note!.Trim()}_");
                    trail.AppendLine(isTarget
                        ? $"  {i + 1}. {Sig(path[i])} (target): {note.Trim()}"
                        : $"  {i + 1}. {Sig(path[i])} → {Sig(path[i + 1])}: {note.Trim()}");
                }
                else if (!isTarget)
                    trail.AppendLine($"  {i + 1}. {Sig(path[i])} → {Sig(path[i + 1])}");
            }
            sb.AppendLine();
            sb.AppendLine(rendered);
            if (!isTarget)
                sb.AppendLine($"↓ calls **{SigNamed(path[i + 1])}**");
        }
        return sb.ToString();
    }

    /// Body of `caller` from its beginning to the FIRST call to `callee` (inclusive). Returns the
    /// rendered markdown (fenced, line-numbered, clipped, with a call-site link + arg mapping) AND the
    /// raw code (no line numbers) used as context for the optional LLM annotation.
    private async Task<(string rendered, string rawCode)> SnippetUpToCall(
        IMethodSymbol caller, IMethodSymbol callee, string? repoUrl)
    {
        var body = await GetBody(caller);
        if (body == null || body.Value.node is not BaseMethodDeclarationSyntax decl)
            return ("_(no source available)_", "");
        var (model, _) = body.Value;

        InvocationExpressionSyntax? callSite = null;
        foreach (var inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol s &&
                SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, callee.OriginalDefinition))
            { callSite = inv; break; }
        }

        var text = decl.SyntaxTree.GetText();
        int from = decl.GetLocation().GetLineSpan().StartLinePosition.Line;          // 0-based
        int to = callSite != null
            ? callSite.GetLocation().GetLineSpan().StartLinePosition.Line
            : decl.GetLocation().GetLineSpan().EndLinePosition.Line;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```csharp");
        sb.Append(ClipRange(text, from, to, 60));
        sb.AppendLine("```");
        if (callSite != null)
        {
            var cs = $"_call site: {RepoLink(Rel(callSite.GetLocation()), repoUrl)}";
            var argMap = ArgMapping(callSite, callee);
            if (argMap.Length > 0) cs += $"  ·  args: {argMap}";
            sb.AppendLine(cs + "_");
        }
        return (sb.ToString(), RawRange(text, from, to, 80));
    }

    /// Full body of a method (e.g. the target/destination node), rendered + raw (for annotation).
    private async Task<(string rendered, string rawCode)> MethodBodyWithRaw(IMethodSymbol m, string? repoUrl)
    {
        var body = await GetBody(m);
        if (body == null || body.Value.node is not BaseMethodDeclarationSyntax decl)
            return ("_(no source available)_", "");
        var text = decl.SyntaxTree.GetText();
        int from = decl.GetLocation().GetLineSpan().StartLinePosition.Line;
        int to = decl.GetLocation().GetLineSpan().EndLinePosition.Line;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```csharp");
        sb.Append(ClipRange(text, from, to, 60));
        sb.AppendLine("```");
        return (sb.ToString(), RawRange(text, from, to, 80));
    }

    /// Positional "argExpr → paramName" mapping for a call site (best-effort, truncated).
    private static string ArgMapping(InvocationExpressionSyntax inv, IMethodSymbol callee)
    {
        var args = inv.ArgumentList.Arguments;
        var ps = callee.Parameters;
        var parts = new List<string>();
        for (int i = 0; i < args.Count && i < ps.Length; i++)
        {
            var a = args[i].Expression.ToString().Replace('\n', ' ').Trim();
            if (a.Length > 28) a = a[..28] + "…";
            parts.Add($"{a} → {ps[i].Name}");
        }
        return string.Join(", ", parts);
    }

    /// Raw source [from..to] (0-based, inclusive), no line numbers, clipped to the LAST maxLines
    /// (keeps the tail, i.e. the call site) - context for the LLM annotation prompt.
    private static string RawRange(Microsoft.CodeAnalysis.Text.SourceText text, int from, int to, int maxLines)
    {
        if (to < from) to = from;
        to = Math.Min(to, text.Lines.Count - 1);
        if (to - from + 1 > maxLines) from = to - maxLines + 1;
        var sb = new System.Text.StringBuilder();
        for (int ln = Math.Max(0, from); ln <= to; ln++) sb.AppendLine(text.Lines[ln].ToString());
        return sb.ToString();
    }

    /// Lines [from..to] (0-based, inclusive) from SourceText, with 1-based numbers, clipped to maxLines.
    private static string ClipRange(Microsoft.CodeAnalysis.Text.SourceText text, int from, int to, int maxLines)
    {
        if (to < from) to = from;
        to = Math.Min(to, text.Lines.Count - 1);
        int total = to - from + 1;
        var sb = new System.Text.StringBuilder();
        if (total <= maxLines)
        {
            for (int ln = from; ln <= to; ln++) sb.AppendLine($"{ln + 1,5}  {text.Lines[ln]}");
        }
        else
        {
            int head = maxLines - 10;
            for (int ln = from; ln < from + head; ln++) sb.AppendLine($"{ln + 1,5}  {text.Lines[ln]}");
            sb.AppendLine($"      // … ({total - maxLines} lines omitted) …");
            for (int ln = to - 9; ln <= to; ln++) sb.AppendLine($"{ln + 1,5}  {text.Lines[ln]}");
        }
        return sb.ToString();
    }

    /// "relpath:line" -> markdown link to the repo (if repoUrl is provided), otherwise plain text.
    public static string RepoLink(string location, string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return location;
        var i = location.LastIndexOf(':');
        if (i <= 0) return location;
        var path = location[..i].Replace('\\', '/');
        var line = location[(i + 1)..];
        return $"[{location}]({repoUrl!.TrimEnd('/')}/{path}#L{line})";
    }

    /// Structured list of methods in a file: (class, method, line).
    /// Used for deterministic bootstrapping of candidates for find_path.
    public List<(string cls, string method, int line)> MethodsInFile(string filePath)
    {
        var result = new List<(string, string, int)>();
        var full = Path.IsPathRooted(filePath) ? filePath : Path.Combine(SolutionDir, filePath);
        var doc = _solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath ?? ""),
                                               Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase));
        if (doc == null) return result;
        var root = doc.GetSyntaxTreeAsync().Result!.GetRoot();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            foreach (var md in type.Members.OfType<MethodDeclarationSyntax>())
                result.Add((type.Identifier.ValueText, md.Identifier.ValueText,
                            md.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        return result;
    }

    // ====================================================================
    //  explain mode: compact, structured context for a single method.
    //  Roslyn extracts ONLY the target method + relevant context - the model
    //  never receives the entire (5000-line) file.
    // ====================================================================

    private static readonly SymbolDisplayFormat ShortType =
        SymbolDisplayFormat.MinimallyQualifiedFormat;

    /// Context for a method (or property) from "Class" + "Name". depth = how deep to pull in callee bodies.
    public async Task<MethodContext?> BuildMethodContext(string className, string methodName, int depth = 0)
    {
        var m = await ResolveMethod(className, methodName);
        if (m != null) return await BuildMethodContextFor(m, depth);
        // fall back to a property/indexer with that name (explains its get/set accessor bodies)
        var p = await ResolveProperty(className, methodName);
        if (p != null) return await BuildPropertyContext(p, depth);
        return null;
    }

    /// Context for a property: explains its accessor bodies (get/set), reads/writes, callees, callers.
    private async Task<MethodContext?> BuildPropertyContext(IPropertySymbol p, int depth)
    {
        var sref = p.DeclaringSyntaxReferences.FirstOrDefault();
        if (sref == null) return null;
        var node = await sref.GetSyntaxAsync();
        var doc = _solution.GetDocument(node.SyntaxTree);
        if (doc == null) return null;
        var model = await doc.GetSemanticModelAsync();
        if (model == null) return null;

        var loc = p.Locations.FirstOrDefault(l => l.IsInSource);
        var location = loc != null ? Rel(loc) : "?";
        var type = p.ContainingType;

        var reads = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var writes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in node.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (model.GetSymbolInfo(name).Symbol is not { } sym) continue;
            if (SymbolEqualityComparer.Default.Equals(sym, p)) continue;   // skip the property itself
            if (!IsSelfMember(sym, type)) continue;
            var expr = OutermostAccess(name);
            var label = MemberLabel(sym);
            if (IsWriteContext(expr, out bool alsoReads)) { writes[sym.Name] = label; if (alsoReads) reads.TryAdd(sym.Name, label); }
            else reads[sym.Name] = label;
        }

        var callees = new List<string>();
        var seenCallee = new HashSet<string>();
        var calleeSymbols = new List<IMethodSymbol>();
        foreach (var inv in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol callee) continue;
            var sig = Sig(callee);
            if (!seenCallee.Add(sig)) continue;
            var cloc = callee.Locations.FirstOrDefault(l => l.IsInSource);
            callees.Add($"{sig}  - {callee.ContainingType?.Name} ({(cloc != null ? Rel(cloc) : "extern/metadata")})");
            if (cloc != null) calleeSymbols.Add(callee);
            if (callees.Count >= 30) break;
        }

        var src = node.ToString();
        int bodyLines = src.Count(ch => ch == '\n') + 1;
        var deps = depth > 0 ? await ExpandDependencies(p, calleeSymbols, depth) : new List<(string, string)>();

        var callers = new List<string>();
        if (depth > 0)
        {
            try
            {
                foreach (var c in await SymbolFinder.FindCallersAsync(p, _solution))
                {
                    if (c.CallingSymbol is IMethodSymbol cm) { callers.Add(Sig(cm)); if (callers.Count >= 6) break; }
                }
            }
            catch { }
        }

        var accessors = (p.GetMethod != null ? "get; " : "") + (p.SetMethod != null ? "set; " : "");
        return new MethodContext(
            Display: $"{type?.Name}.{p.Name}",
            Location: location,
            Signature: $"{p.Type.ToDisplayString(ShortType)} {p.Name} {{ {accessors}}}".Trim(),
            Source: src,
            BodyLineCount: bodyLines,
            Parameters: new List<string>(),
            Reads: reads.Values.ToList(),
            Writes: writes.Values.ToList(),
            Callees: callees,
            DocComment: CleanDoc(p.GetDocumentationCommentXml()),
            Declaration: node,
            Dependencies: deps,
            Callers: callers);
    }

    /// Context for the method whose body contains the given line in the given file.
    public async Task<MethodContext?> BuildMethodContextByLocation(string filePath, int line, int depth = 0)
    {
        var sym = await ResolveByLocation(filePath, line);
        return sym == null ? null : await BuildMethodContextFor(sym, depth);
    }

    /// Finds the IMethodSymbol of the method whose body contains the given line (innermost match).
    private async Task<IMethodSymbol?> ResolveByLocation(string filePath, int line)
    {
        var full = Path.IsPathRooted(filePath) ? filePath : Path.Combine(SolutionDir, filePath);
        var doc = _solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath ?? ""),
                                               Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase));
        if (doc == null) return null;

        var root = await doc.GetSyntaxRootAsync();
        if (root == null) return null;

        var decl = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
            .Where(d =>
            {
                var sp = d.GetLocation().GetLineSpan();
                return line >= sp.StartLinePosition.Line + 1 && line <= sp.EndLinePosition.Line + 1;
            })
            .OrderBy(d =>
            {
                var sp = d.GetLocation().GetLineSpan();
                return sp.EndLinePosition.Line - sp.StartLinePosition.Line;
            })
            .FirstOrDefault();
        if (decl == null) return null;

        var model = await doc.GetSemanticModelAsync();
        return model?.GetDeclaredSymbol(decl) as IMethodSymbol;
    }

    // ====================================================================
    //  deep explain: full logic along the call chain. Instead of stuffing N truncated
    //  bodies into ONE shallow call, it walks the chain of methods (BFS over in-solution
    //  callees up to depth `depth`, cap `maxMethods`) and explains each one SEPARATELY.
    // ====================================================================

    /// In-solution methods (with source), called by the given method. Deduplicated.
    private async Task<List<IMethodSymbol>> InSolutionCallees(IMethodSymbol m)
    {
        var result = new List<IMethodSymbol>();
        var body = await GetBody(m);
        if (body == null) return result;
        var (model, node) = body.Value;
        SyntaxNode scanRoot = node is BaseMethodDeclarationSyntax d
            ? ((SyntaxNode?)d.Body ?? (SyntaxNode?)d.ExpressionBody ?? d) : node;

        var seen = new HashSet<string>();
        foreach (var inv in scanRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol callee) continue;
            if (!callee.Locations.Any(l => l.IsInSource)) continue;     // skip framework/extern
            if (seen.Add(Sig(callee))) result.Add(callee);
        }
        // `new Foo(...)` (and target-typed `new()`) invokes Foo's constructor - track it like any other
        // callee, so the call graph follows object construction. Framework ctors (new List<>()) are skipped.
        foreach (var oce in scanRoot.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(oce).Symbol is not IMethodSymbol ctor) continue;
            if (!ctor.Locations.Any(l => l.IsInSource)) continue;
            if (seen.Add(Sig(ctor))) result.Add(ctor);
        }
        return result;
    }

    /// Returns the chain of methods (root + in-solution callees up to depth `depth`, cap `maxMethods`),
    /// each as an independent depth-0 context. BFS order (root first). null = root not found.
    public async Task<List<(int level, MethodContext ctx)>?> BuildExplainChain(
        string className, string methodName, int depth, int maxMethods)
    {
        var root = await ResolveMethod(className, methodName);
        return root == null ? null : await BuildExplainChainFor(root, depth, maxMethods);
    }

    public async Task<List<(int level, MethodContext ctx)>?> BuildExplainChainByLocation(
        string filePath, int line, int depth, int maxMethods)
    {
        var root = await ResolveByLocation(filePath, line);
        return root == null ? null : await BuildExplainChainFor(root, depth, maxMethods);
    }

    private async Task<List<(int level, MethodContext ctx)>> BuildExplainChainFor(
        IMethodSymbol root, int depth, int maxMethods)
    {
        var cmp = SymbolEqualityComparer.Default;
        var visited = new HashSet<ISymbol>(cmp) { root };
        var order = new List<(IMethodSymbol sym, int lvl)>();
        var queue = new Queue<(IMethodSymbol sym, int lvl)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && order.Count < maxMethods)
        {
            var (sym, lvl) = queue.Dequeue();
            order.Add((sym, lvl));
            if (lvl >= depth) continue;
            foreach (var c in await InSolutionCallees(sym))
                if (visited.Add(c)) queue.Enqueue((c, lvl + 1));
        }
        if (queue.Count > 0)
            Console.Error.WriteLine($"[explain] call chain capped at {maxMethods} methods " +
                                    "- raise --max-methods to go wider/deeper.");

        var result = new List<(int, MethodContext)>();
        foreach (var (sym, lvl) in order)
        {
            var ctx = await BuildMethodContextFor(sym, 0);
            if (ctx != null) result.Add((lvl, ctx));
        }
        return result;
    }

    // ====================================================================
    //  map: deterministic reachability from one point. UNLIKE trace (point-to-point) there is no
    //  target - we just expand the call graph in ONE direction (callees = downstream, callers =
    //  upstream/impact) breadth-first until a depth / node cap. No model: it's heavy, so it's a
    //  fast structural overview - pick an interesting node and run trace/explain on it for detail.
    // ====================================================================

    /// In-solution methods (with source) that CALL the given method - the upward/impact direction.
    /// Mirror of InSolutionCallees. Best-effort: FindCallersAsync can be heavy/fail on a big solution.
    private async Task<List<IMethodSymbol>> InSolutionCallers(IMethodSymbol m)
    {
        var result = new List<IMethodSymbol>();
        var cmp = SymbolEqualityComparer.Default;
        var seen = new HashSet<ISymbol>(cmp);
        try
        {
            foreach (var c in await SymbolFinder.FindCallersAsync(m, _solution))
            {
                if (c.CallingSymbol is not IMethodSymbol cm) continue;
                if (!cm.Locations.Any(l => l.IsInSource)) continue;     // skip framework/extern callers
                if (seen.Add(cm)) result.Add(cm);
            }
        }
        catch { /* not critical - an empty caller set just yields a smaller map */ }
        return result;
    }

    /// Builds a reachability graph from a root method, following callees (down=true) or callers
    /// (down=false) breadth-first up to `maxDepth`, hard-capped at `maxNodes`. Deterministic (no model).
    /// `down` decides edge direction so the tree always reads top→bottom in calling order. Returns null
    /// if the root can't be resolved; the out `truncated` flag reports whether the node cap was hit.
    public async Task<(Diagram.Graph graph, bool truncated, List<(string display, string where, string code)> bodies)> BuildMap(
        IMethodSymbol root, bool down, int maxDepth, int maxNodes, bool withBodies = false, int peek = 0)
    {
        string Display(IMethodSymbol m) => DisplayName(m);
        string Where(IMethodSymbol m)
        {
            var l = m.Locations.FirstOrDefault(x => x.IsInSource);
            return l != null ? Rel(l) : "";
        }

        var g = new Diagram.Graph();
        var cmp = SymbolEqualityComparer.Default;
        var visited = new HashSet<ISymbol>(cmp) { root };
        var queue = new Queue<(IMethodSymbol sym, int lvl)>();
        queue.Enqueue((root, 0));
        // down: root is where you START (◆); up: root is what everything REACHES (★ target at the bottom).
        g.Add(Display(root), Display(root), Where(root), down ? "entry" : "target");

        var nodeSyms = new List<IMethodSymbol> { root };   // unique methods in BFS order (for --with-bodies)
        bool truncated = false;
        while (queue.Count > 0 && !truncated)
        {
            var (sym, lvl) = queue.Dequeue();
            if (lvl >= maxDepth) continue;
            var neighbors = down ? await InSolutionCallees(sym) : await InSolutionCallers(sym);
            foreach (var nb in neighbors)
            {
                // edge always points caller→callee, regardless of which way we walked
                var from = down ? Display(sym) : Display(nb);
                var to   = down ? Display(nb)  : Display(sym);
                g.Add(Display(nb), Display(nb), Where(nb));
                g.Edge(from, to);
                if (!visited.Add(nb)) continue;                 // already mapped (cycle / shared callee)
                nodeSyms.Add(nb);
                if (g.Nodes.Count >= maxNodes) { truncated = true; break; }
                queue.Enqueue((nb, lvl + 1));
            }
        }
        if (truncated)
            Console.Error.WriteLine($"[map] hit the {maxNodes}-node cap - graph truncated here. " +
                                    "Raise --max-nodes (or lower --depth) for a different cut.");

        // --with-bodies: pull each unique method's real source (deterministic), peek-limited if asked.
        var bodies = new List<(string display, string where, string code)>();
        if (withBodies)
            foreach (var m in nodeSyms)
            {
                var gb = await GetBody(m);
                var code = gb?.node.ToString() ?? "// (no source available)";
                if (peek > 0) code = PeekLines(code, peek);
                bodies.Add((Display(m), Where(m), code));
            }
        return (g, truncated, bodies);
    }

    /// First N lines of a code block, with a note when clipped (used by map --with-bodies --peek).
    private static string PeekLines(string code, int n)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= n) return code;
        return string.Join("\n", lines.Take(n)) + $"\n// … (+{lines.Length - n} more lines — raise --peek)";
    }

    private async Task<MethodContext?> BuildMethodContextFor(IMethodSymbol m, int depth = 0)
    {
        var body = await GetBody(m);
        if (body == null) return null;                  // no source (metadata) - nothing to explain
        var (model, node) = body.Value;
        if (node is not BaseMethodDeclarationSyntax decl) return null;

        var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
        var location = loc != null ? Rel(loc) : "?";
        var type = m.ContainingType;

        // scanRoot = method body (block or expression body); not the signature
        SyntaxNode scanRoot = (SyntaxNode?)decl.Body ?? (SyntaxNode?)decl.ExpressionBody ?? decl;

        var parameters = m.Parameters
            .Select(p => $"{p.Type.ToDisplayString(ShortType)} {p.Name}")
            .ToList();

        // ---- fields and properties of the class that are read / written ----
        var reads = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var writes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in scanRoot.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (model.GetSymbolInfo(name).Symbol is not { } sym) continue;
            if (!IsSelfMember(sym, type)) continue;

            var expr = OutermostAccess(name);
            var label = MemberLabel(sym);
            if (IsWriteContext(expr, out bool alsoReads))
            {
                writes[sym.Name] = label;
                if (alsoReads) reads.TryAdd(sym.Name, label);
            }
            else
            {
                reads[sym.Name] = label;
            }
        }

        // ---- callees (called methods) with signature and 1-line origin ----
        var callees = new List<string>();
        var seenCallee = new HashSet<string>();
        var calleeSymbols = new List<IMethodSymbol>();   // in-solution callees for expansion
        foreach (var inv in scanRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol callee) continue;
            var sig = Sig(callee);
            if (!seenCallee.Add(sig)) continue;
            var cloc = callee.Locations.FirstOrDefault(l => l.IsInSource);
            var origin = cloc != null ? Rel(cloc) : "extern/metadata";
            callees.Add($"{sig}  - {callee.ContainingType?.Name} ({origin})");
            if (cloc != null) calleeSymbols.Add(callee);     // has source -> can be expanded
            if (callees.Count >= 30) break;
        }

        var src = decl.ToString();
        int bodyLines = src.Count(ch => ch == '\n') + 1;
        var doc = CleanDoc(m.GetDocumentationCommentXml());

        // ---- expand dependencies up to depth `depth` (in-solution, hard-capped) ----
        var deps = depth > 0
            ? await ExpandDependencies(m, calleeSymbols, depth)
            : new List<(string, string)>();

        // ---- callers (direction UPWARD) - "how to get here"; only when depth>0, best-effort ----
        var callers = new List<string>();
        if (depth > 0)
        {
            try
            {
                foreach (var c in await SymbolFinder.FindCallersAsync(m, _solution))
                {
                    if (c.CallingSymbol is not IMethodSymbol cm) continue;
                    callers.Add(Sig(cm));
                    if (callers.Count >= 6) break;
                }
            }
            catch { /* can be expensive/fail on a large solution - not critical */ }
        }

        return new MethodContext(
            Display: $"{type?.Name}.{m.Name}",
            Location: location,
            Signature: m.ToDisplayString(ShortType),
            Source: src,
            BodyLineCount: bodyLines,
            Parameters: parameters,
            Reads: reads.Values.ToList(),
            Writes: writes.Values.ToList(),
            Callees: callees,
            DocComment: doc,
            Declaration: decl,
            Dependencies: deps,
            Callers: callers);
    }

    // Hard caps for dependency expansion - to prevent spaghetti code from flooding the context / CPU.
    private const int MaxDepMethods = 8;
    private const int MaxDepLines = 40;

    /// BFS over in-solution callees up to depth `depth`. Returns truncated bodies (capped on method count
    /// and line count). Skips recursion, external symbols, and already-visited methods.
    private async Task<List<(string display, string code)>> ExpandDependencies(
        ISymbol root, List<IMethodSymbol> firstLevel, int depth)
    {
        var result = new List<(string, string)>();
        var cmp = SymbolEqualityComparer.Default;
        var visited = new HashSet<ISymbol>(cmp) { root };
        var queue = new Queue<(IMethodSymbol sym, int d)>();
        foreach (var c in firstLevel) queue.Enqueue((c, 1));

        while (queue.Count > 0 && result.Count < MaxDepMethods)
        {
            var (sym, d) = queue.Dequeue();
            if (!visited.Add(sym)) continue;

            var body = await GetBody(sym);
            if (body == null) continue;
            var node = body.Value.node;

            var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
            var where = loc != null ? Rel(loc) : "?";
            result.Add(($"{Sig(sym)}  {where}", ClipLines(node.ToString(), MaxDepLines)));

            if (d < depth)   // go one level deeper
            {
                var (cmodel, cnode) = body.Value;
                foreach (var inv in cnode.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (cmodel.GetSymbolInfo(inv).Symbol is IMethodSymbol next
                        && next.Locations.Any(l => l.IsInSource)
                        && !visited.Contains(next))
                        queue.Enqueue((next, d + 1));
                }
            }
        }
        return result;
    }

    private static string ClipLines(string src, int maxLines)
    {
        var lines = src.Split('\n');
        if (lines.Length <= maxLines) return src;
        return string.Join("\n", lines.Take(maxLines)) + "\n    // ... (truncated)";
    }

    /// Splits a long method body into logical blocks (by top-level statements, ~120 lines/block)
    /// for block-by-block explanation of extremely long methods.
    public static List<(string label, string code)> SplitLongMethod(MethodContext ctx)
    {
        var result = new List<(string, string)>();
        if (ctx.Declaration is not BaseMethodDeclarationSyntax decl || decl.Body is not { } block)
        {
            result.Add(("Full body", ctx.Source));
            return result;
        }

        const int budget = 120;
        var cur = new List<StatementSyntax>();
        int curLines = 0, idx = 1;

        void Flush()
        {
            if (cur.Count == 0) return;
            int s = cur[0].GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            int e = cur[^1].GetLocation().GetLineSpan().EndLinePosition.Line + 1;
            result.Add(($"Block {idx++} (lines {s}-{e})", string.Join("\n", cur.Select(st => st.ToString()))));
            cur.Clear();
            curLines = 0;
        }

        foreach (var st in block.Statements)
        {
            var sp = st.GetLocation().GetLineSpan();
            int lines = sp.EndLinePosition.Line - sp.StartLinePosition.Line + 1;
            if (curLines > 0 && curLines + lines > budget) Flush();
            cur.Add(st);
            curLines += lines;
            if (curLines >= budget) Flush();
        }
        Flush();
        if (result.Count == 0) result.Add(("Full body", ctx.Source));
        return result;
    }

    // ---- helpers for explain ----

    private static bool IsSelfMember(ISymbol sym, INamedTypeSymbol? type)
    {
        if (sym is not (IFieldSymbol or IPropertySymbol)) return false;
        var ct = sym.ContainingType;
        for (var t = type; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(ct, t)) return true;
        return false;
    }

    private static string MemberLabel(ISymbol sym) => sym switch
    {
        IFieldSymbol f => $"{f.Type.ToDisplayString(ShortType)} {f.Name}",
        IPropertySymbol p => $"{p.Type.ToDisplayString(ShortType)} {p.Name}",
        _ => sym.Name
    };

    /// Walks up a member-access chain (this.a.b -> the outermost b is the end) to the expression
    /// whose write/read context is meaningful to examine.
    private static ExpressionSyntax OutermostAccess(SimpleNameSyntax name)
    {
        ExpressionSyntax expr = name;
        while (expr.Parent is MemberAccessExpressionSyntax ma && ma.Name == expr)
            expr = ma;
        return expr;
    }

    private static bool IsWriteContext(ExpressionSyntax expr, out bool alsoReads)
    {
        alsoReads = false;
        switch (expr.Parent)
        {
            case AssignmentExpressionSyntax asg when asg.Left == expr:
                alsoReads = !asg.IsKind(SyntaxKind.SimpleAssignmentExpression);   // += -= ... also read
                return true;
            case PostfixUnaryExpressionSyntax:
                alsoReads = true;
                return true;
            case PrefixUnaryExpressionSyntax pre
                when pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression):
                alsoReads = true;
                return true;
            case ArgumentSyntax arg when !arg.RefKindKeyword.IsKind(SyntaxKind.None):
                alsoReads = arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword);     // ref also reads, out only writes
                return true;
            default:
                return false;
        }
    }

    private static string? CleanDoc(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var text = Regex.Replace(xml, "<.*?>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }
}

/// <summary>Compact structured context for a single method for explain mode.</summary>
public sealed record MethodContext(
    string Display,
    string Location,
    string Signature,
    string Source,
    int BodyLineCount,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string> Reads,
    IReadOnlyList<string> Writes,
    IReadOnlyList<string> Callees,
    string? DocComment,
    SyntaxNode Declaration,
    // Truncated bodies of in-solution dependencies (callees) up to depth --depth - so the model
    // can explain how the method and its dependencies work together. (display, code)
    IReadOnlyList<(string display, string code)> Dependencies,
    // Who calls this method (direction UPWARD) - "how to get here". Populated only when depth>0.
    IReadOnlyList<string> Callers);