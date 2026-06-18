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

    public async Task LoadAsync(string solutionPath)
    {
        SolutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? "";
        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            // Warnings during load are common (missing analyzers, etc.) - just log them.
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                Console.Error.WriteLine($"[workspace] {e.Diagnostic.Message}");
        };

        Console.Error.WriteLine($"[index] loading solution: {solutionPath}");
        _solution = await workspace.OpenSolutionAsync(solutionPath);
        var projCount = _solution.Projects.Count();
        Console.Error.WriteLine($"[index] projects loaded: {projCount}");
    }

    private string Rel(Location loc)
    {
        var span = loc.GetLineSpan();
        var path = span.Path;
        try { path = Path.GetRelativePath(SolutionDir, path); } catch { /* keep absolute */ }
        return $"{path}:{span.StartLinePosition.Line + 1}";
    }

    private static string Sig(IMethodSymbol m) =>
        $"{m.ContainingType?.Name}.{m.Name}({string.Join(", ", m.Parameters.Select(p => p.Type.Name))})";

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

    /// Resolves a method from "Class" + "Method". Returns the first match (notes ambiguity).
    public async Task<IMethodSymbol?> ResolveMethod(string className, string methodName)
    {
        var decls = await FindDeclarations(methodName);
        var methods = decls.OfType<IMethodSymbol>()
            .Where(m => m.ContainingType != null &&
                        string.Equals(m.ContainingType.Name, className, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return methods.FirstOrDefault();
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
                if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(SolutionDir, doc.FilePath);
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
                                       int maxNodes = 3000, bool withBodies = false, string? repoUrl = null)
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
                    return await RenderPath(path, withBodies, repoUrl);
                }
                queue.Enqueue(caller);
            }
        }
        return $"path not found (explored {explored} nodes). " +
               "The call may go through interface/DI/reflection/events - try find_callers manually " +
               "or find_callees from the source going down.";
    }

    /// Renders the found path. withBodies=false -> compact list (for the model and default trace).
    /// withBodies=true -> inserts the method body FROM its beginning UP TO the call site of the next step.
    private async Task<string> RenderPath(List<IMethodSymbol> path, bool withBodies, string? repoUrl)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PATH FOUND ({path.Count} nodes):");
        if (!withBodies)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var loc = path[i].Locations.FirstOrDefault(l => l.IsInSource);
                var where = loc != null ? Rel(loc) : "?";
                var arrow = i < path.Count - 1 ? "  -->" : "";
                sb.AppendLine($"  {i + 1}. {Sig(path[i])}   {where}{arrow}");
            }
            return sb.ToString();
        }

        for (int i = 0; i < path.Count; i++)
        {
            var loc = path[i].Locations.FirstOrDefault(l => l.IsInSource);
            var where = loc != null ? Rel(loc) : "?";
            var tag = i == path.Count - 1 ? "  (target)" : "";
            sb.AppendLine();
            sb.AppendLine($"**{i + 1}. {Sig(path[i])}**   {RepoLink(where, repoUrl)}{tag}");
            if (i < path.Count - 1)
            {
                sb.AppendLine();
                sb.AppendLine(await SnippetUpToCall(path[i], path[i + 1], repoUrl));
                sb.AppendLine($"↓ calls **{Sig(path[i + 1])}**");
            }
        }
        return sb.ToString();
    }

    /// Body of `caller` from its beginning to the FIRST call to `callee` (inclusive), with line numbers
    /// clipped to a reasonable length. Plus a link to the call site if repoUrl is provided.
    private async Task<string> SnippetUpToCall(IMethodSymbol caller, IMethodSymbol callee, string? repoUrl)
    {
        var body = await GetBody(caller);
        if (body == null || body.Value.node is not BaseMethodDeclarationSyntax decl)
            return "_(no source available)_";
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
            sb.AppendLine($"_call site: {RepoLink(Rel(callSite.GetLocation()), repoUrl)}_");
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

    /// Context for a method from "Class" + "Method". depth = how deep to pull in callee bodies.
    public async Task<MethodContext?> BuildMethodContext(string className, string methodName, int depth = 0)
    {
        var m = await ResolveMethod(className, methodName);
        if (m == null) return null;
        return await BuildMethodContextFor(m, depth);
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
        IMethodSymbol root, List<IMethodSymbol> firstLevel, int depth)
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