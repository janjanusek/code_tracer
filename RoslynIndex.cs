using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeTracer;

/// <summary>
/// Obal nad Roslyn workspace. Drzi nacitanu solution a poskytuje
/// presnu analyzu: definicie, referencie, callerov, callees a najkratsiu
/// cestu v call-grafe. Vsetko deterministicky - LLM len rozhoduje co volat.
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
            // Warningy pri loade su bezne (chybajuce analyzery atd.) - len logujeme.
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

    /// Najde vsetky deklaracie ktorych meno sa zhoduje (case-insensitive).
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
        // deduplikacia podla definicie
        return result
            .GroupBy(s => s, SymbolEqualityComparer.Default)
            .Select(g => g.First())
            .ToList();
    }

    /// Rozlisi metodu z "Trieda" + "Metoda". Vrati prvu zhodu (poznamena ambiguitu).
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

    // ---- tools (vracaju kompaktny text pre LLM) ----------------------------

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
    /// Deterministicka najkratsia cesta v call-grafe od (fromClass.fromMethod)
    /// k (toClass.toMethod). BFS smerom NAHOR cez callerov ciela az kym
    /// nenarazime na zdroj. Toto je najspolahlivejsia operacia - LLM ju len
    /// vola s vysledkom find_symbol/outline a potom interpretuje vystup.
    /// </summary>
    public async Task<string> FindPath(string fromClass, string fromMethod, string toClass, string toMethod,
                                       int maxNodes = 3000)
    {
        var start = await ResolveMethod(fromClass, fromMethod);
        var target = await ResolveMethod(toClass, toMethod);
        if (start == null) return $"source method {fromClass}.{fromMethod} not found";
        if (target == null) return $"target method {toClass}.{toMethod} not found";

        var cmp = SymbolEqualityComparer.Default;
        if (cmp.Equals(start, target)) return "source == target (same method)";

        var queue = new Queue<IMethodSymbol>();
        var visited = new HashSet<ISymbol>(cmp) { target };
        var calledBy = new Dictionary<ISymbol, IMethodSymbol>(cmp); // caller -> co volal (smerom k cielu)

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
                calledBy[caller] = current; // caller volá 'current' (smer k cielu)

                if (cmp.Equals(caller, start))
                {
                    // rekonstrukcia start -> ... -> target
                    var path = new List<IMethodSymbol> { start };
                    var node = (IMethodSymbol)start;
                    while (!cmp.Equals(node, target))
                    {
                        node = calledBy[node];
                        path.Add(node);
                    }
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"PATH FOUND ({path.Count} nodes):");
                    for (int i = 0; i < path.Count; i++)
                    {
                        var loc = path[i].Locations.FirstOrDefault(l => l.IsInSource);
                        var where = loc != null ? Rel(loc) : "?";
                        var arrow = i < path.Count - 1 ? "  -->" : "";
                        sb.AppendLine($"  {i + 1}. {Sig(path[i])}   {where}{arrow}");
                    }
                    return sb.ToString();
                }
                queue.Enqueue(caller);
            }
        }
        return $"path not found (explored {explored} nodes). " +
               "The call may go through interface/DI/reflection/events - try find_callers manually " +
               "or find_callees from the source going down.";
    }

    /// Strukturovany zoznam metod v subore: (trieda, metoda, riadok).
    /// Pouziva sa na deterministicky bootstrap kandidatov pre find_path.
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
    //  explain mode: kompaktny, struktrurovany kontext jednej metody.
    //  Roslyn vytiahne IBA cielovu metodu + relevantny kontext - model
    //  nikdy nedostane cely (5000-riadkovy) subor.
    // ====================================================================

    private static readonly SymbolDisplayFormat ShortType =
        SymbolDisplayFormat.MinimallyQualifiedFormat;

    /// Kontext metody z "Trieda" + "Metoda". depth = do akej hlbky natiahnut tela callees.
    public async Task<MethodContext?> BuildMethodContext(string className, string methodName, int depth = 0)
    {
        var m = await ResolveMethod(className, methodName);
        if (m == null) return null;
        return await BuildMethodContextFor(m, depth);
    }

    /// Kontext metody, ktorej telo obsahuje dany riadok v danom subore.
    public async Task<MethodContext?> BuildMethodContextByLocation(string filePath, int line, int depth = 0)
    {
        var sym = await ResolveByLocation(filePath, line);
        return sym == null ? null : await BuildMethodContextFor(sym, depth);
    }

    /// Najde IMethodSymbol metody, ktorej telo obsahuje dany riadok (najtesnejsia).
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
    //  deep explain: cela logika po call-chain. Namiesto nahltania N skratenych
    //  tiel do JEDNEHO plytkeho callu prejde retazec metod (BFS po in-solution
    //  callees do hlbky `depth`, cap `maxMethods`) a kazdu vysvetli SAMOSTATNE.
    // ====================================================================

    /// In-solution metody (so zdrojakom), ktore dana metoda vola. Deduplikovane.
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
            if (!callee.Locations.Any(l => l.IsInSource)) continue;     // preskoc framework/extern
            if (seen.Add(Sig(callee))) result.Add(callee);
        }
        return result;
    }

    /// Vrati retazec metod (root + in-solution callees do hlbky `depth`, cap `maxMethods`),
    /// kazdu ako samostatny depth-0 kontext. Poradie BFS (root prvy). null = root nenajdeny.
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
        if (body == null) return null;                  // bez zdrojaku (metadata) - nic nevysvetlime
        var (model, node) = body.Value;
        if (node is not BaseMethodDeclarationSyntax decl) return null;

        var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
        var location = loc != null ? Rel(loc) : "?";
        var type = m.ContainingType;

        // scanRoot = telo metody (block alebo expression body); nie signatura
        SyntaxNode scanRoot = (SyntaxNode?)decl.Body ?? (SyntaxNode?)decl.ExpressionBody ?? decl;

        var parameters = m.Parameters
            .Select(p => $"{p.Type.ToDisplayString(ShortType)} {p.Name}")
            .ToList();

        // ---- citane / zapisovane polia a property triedy ----
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

        // ---- callees (volane metody) so signaturou a 1-riadkovym povodom ----
        var callees = new List<string>();
        var seenCallee = new HashSet<string>();
        var calleeSymbols = new List<IMethodSymbol>();   // in-solution callees na expanziu
        foreach (var inv in scanRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol callee) continue;
            var sig = Sig(callee);
            if (!seenCallee.Add(sig)) continue;
            var cloc = callee.Locations.FirstOrDefault(l => l.IsInSource);
            var origin = cloc != null ? Rel(cloc) : "extern/metadata";
            callees.Add($"{sig}  - {callee.ContainingType?.Name} ({origin})");
            if (cloc != null) calleeSymbols.Add(callee);     // ma zdrojak -> da sa expandovat
            if (callees.Count >= 30) break;
        }

        var src = decl.ToString();
        int bodyLines = src.Count(ch => ch == '\n') + 1;
        var doc = CleanDoc(m.GetDocumentationCommentXml());

        // ---- expanzia zavislosti do hlbky `depth` (in-solution, tvrdo zastropovane) ----
        var deps = depth > 0
            ? await ExpandDependencies(m, calleeSymbols, depth)
            : new List<(string, string)>();

        // ---- calleri (smer NAHOR) - "ako sa sem da dostat"; len pri depth>0, best-effort ----
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
            catch { /* na velkej solution moze byt drahe/zlyhat - nie je kriticke */ }
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

    // Tvrde stropy pre expanziu zavislosti - aby spagetti kod nezahltil kontext / CPU.
    private const int MaxDepMethods = 8;
    private const int MaxDepLines = 40;

    /// BFS cez in-solution callees do hlbky `depth`. Vrati skratene tela (cap na pocet
    /// metod aj riadkov). Preskakuje rekurziu, externe symboly a uz videne metody.
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

            if (d < depth)   // este o uroven nizsie
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

    /// Rozbije dlhe telo na logicke bloky (po vrchnych statementoch, ~120 riadkov/blok)
    /// pre vysvetlenie blok-po-bloku pri extremne dlhych metodach.
    public static List<(string label, string code)> SplitLongMethod(MethodContext ctx)
    {
        var result = new List<(string, string)>();
        if (ctx.Declaration is not BaseMethodDeclarationSyntax decl || decl.Body is not { } block)
        {
            result.Add(("Cele telo", ctx.Source));
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
            result.Add(($"Blok {idx++} (riadky {s}-{e})", string.Join("\n", cur.Select(st => st.ToString()))));
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
        if (result.Count == 0) result.Add(("Cele telo", ctx.Source));
        return result;
    }

    // ---- pomocne pre explain ----

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

    /// Vystupi z member-access retazca (this.a.b -> najvyssie b je zaver) na vyraz,
    /// ktoreho zapis/citanie ma zmysel skumat.
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
                alsoReads = !asg.IsKind(SyntaxKind.SimpleAssignmentExpression);   // += -= ... aj citaju
                return true;
            case PostfixUnaryExpressionSyntax:
                alsoReads = true;
                return true;
            case PrefixUnaryExpressionSyntax pre
                when pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression):
                alsoReads = true;
                return true;
            case ArgumentSyntax arg when !arg.RefKindKeyword.IsKind(SyntaxKind.None):
                alsoReads = arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword);     // ref aj cita, out len pise
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

/// <summary>Kompaktny strukturovany kontext jednej metody pre explain mode.</summary>
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
    // Skratene tela in-solution zavislosti (callees) do hlbky --depth - aby model
    // vedel vysvetlit, ako metoda a jej zavislosti spolu hraju. (display, code)
    IReadOnlyList<(string display, string code)> Dependencies,
    // Kto metodu vola (smer NAHOR) - "ako sa sem da dostat". Vypln sa len pri depth>0.
    IReadOnlyList<string> Callers);