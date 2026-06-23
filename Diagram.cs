using System.Text;
using System.Text.RegularExpressions;

namespace CodeTracer;

/// <summary>
/// Builds a high-level "what the analysis found" diagram of the discovered call-flow and renders it
/// TWO ways into the result: an ASCII block (renders identically in ANY viewer — corporate wiki,
/// Notepad, GitHub) followed by a Mermaid block (renders as real graphics on GitHub / VS Code).
/// Fully deterministic — built from the Roslyn result, NO model calls. Used by both explain and trace.
/// </summary>
public static class Diagram
{
    /// A node in the discovered flow. Role: "entry" (start), "target" (destination), or "" (in between).
    public sealed class Node
    {
        public required string Id;       // stable key (e.g. "Type.Method")
        public required string Label;    // shown text
        public string Where = "";        // file:line, if known
        public string Role = "";         // "entry" | "target" | ""
        public string Note = "";         // one-line "why" (reused from --annotate; never generated here)
    }

    /// A tiny directed graph of the finding (entry -> ... -> target), shared by both renderers.
    public sealed class Graph
    {
        private readonly Dictionary<string, Node> _byId = new(StringComparer.Ordinal);
        public readonly List<Node> Nodes = new();
        public readonly List<(string from, string to)> Edges = new();

        public Node Add(string id, string label, string where = "", string role = "", string note = "")
        {
            if (_byId.TryGetValue(id, out var n))
            {
                if (n.Where.Length == 0 && where.Length > 0) n.Where = where;
                if (n.Role.Length == 0 && role.Length > 0) n.Role = role;     // never downgrade
                if (n.Note.Length == 0 && note.Length > 0) n.Note = note;     // first note wins
                return n;
            }
            var node = new Node { Id = id, Label = label, Where = where, Role = role, Note = note };
            _byId[id] = node; Nodes.Add(node);
            return node;
        }

        public void Edge(string from, string to)
        {
            if (from == to) return;
            if (!Edges.Contains((from, to))) Edges.Add((from, to));
        }

        public Node? ById(string id) => _byId.TryGetValue(id, out var n) ? n : null;
        public List<string> Children(string id) => Edges.Where(e => e.from == id).Select(e => e.to).ToList();
        public bool HasIncoming(string id) => Edges.Any(e => e.to == id);

        /// Best entry points: explicit "entry" nodes, else nodes nothing points to, else the first node.
        public List<string> Roots()
        {
            var entries = Nodes.Where(n => n.Role == "entry").Select(n => n.Id).ToList();
            if (entries.Count > 0) return entries;
            var sources = Nodes.Where(n => !HasIncoming(n.Id)).Select(n => n.Id).ToList();
            if (sources.Count > 0) return sources;
            return Nodes.Count > 0 ? new List<string> { Nodes[0].Id } : new List<string>();
        }
    }

    // ---- public entry points -------------------------------------------------------------------

    /// Renders the full "## Call-flow" section (ASCII + Mermaid) for a graph, or "" if there's nothing
    /// worth drawing (fewer than 2 nodes — a lone box helps no one).
    public static string Section(Graph g, string caption, bool fullAscii = false)
    {
        if (g.Nodes.Count < 2) return "";
        // The diagram itself makes no model call. If --annotate already produced per-hop notes we
        // reuse them inline (one line explains each node) - free, no extra calls.
        bool hasNotes = g.Nodes.Any(n => n.Note.Length > 0);
        var origin = hasNotes
            ? "flow from Roslyn; the one-line note on each node is reused from --annotate (no extra calls)"
            : "deterministic, straight from Roslyn (no model)";
        var sb = new StringBuilder();
        sb.AppendLine("## Call-flow");
        sb.AppendLine($"_{caption} — {origin}._");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.AppendLine(Ascii(g, fullAscii).TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(Mermaid(g));
        return sb.ToString().TrimEnd();
    }

    /// explain: the call-tree the analysis walked. Edges stay WITHIN the explained chain (clean tree);
    /// for a single method (--depth 0) its in-solution callees are shown as leaves so it isn't a lone box.
    public static Graph FromChain(IReadOnlyList<(int level, MethodContext ctx)> chain)
    {
        var g = new Graph();
        var inChain = new HashSet<string>(chain.Select(c => c.ctx.Display), StringComparer.Ordinal);
        bool single = chain.Count == 1;

        foreach (var (level, ctx) in chain)
            g.Add(ctx.Display, ctx.Display, ctx.Location, level == 0 ? "entry" : "");

        foreach (var (_, ctx) in chain)
        {
            foreach (var callee in ctx.Callees)
            {
                if (callee.EndsWith("(extern/metadata)", StringComparison.Ordinal)) continue; // skip framework
                var name = CalleeName(callee);
                if (name == null || name == ctx.Display) continue;
                if (inChain.Contains(name))
                {
                    g.Edge(ctx.Display, name);                       // real call between explained methods
                }
                else if (single && g.Nodes.Count < 24)
                {
                    g.Add(name, name, CalleeWhere(callee));          // depth-0 bonus: show what it calls
                    g.Edge(ctx.Display, name);
                }
            }
        }
        return g;
    }

    /// trace: parse the rendered path text (compact OR --with-bodies, single OR --all-paths) into the
    /// discovered graph. Robust to repo-url links. Built from the path string so it works for every mode.
    public static Graph FromTraceText(string pathText)
    {
        var g = new Graph();
        var ends = new HashSet<string>(StringComparer.Ordinal);   // last node of each path
        foreach (var seg in SplitPaths(pathText))
        {
            var toks = ExtractNodes(seg);
            if (toks.Count == 0) continue;
            for (int i = 0; i < toks.Count; i++)
            {
                g.Add(toks[i].name, toks[i].name, toks[i].where, i == 0 ? "entry" : "", toks[i].note);
                if (i > 0) g.Edge(toks[i - 1].name, toks[i].name);
            }
            ends.Add(toks[^1].name);
        }
        // Mark "target" only where chains actually bottom out (a path end with no further calls).
        // When --all-paths targets a whole class, many methods are path-ends; tagging only the
        // true leaves keeps the diagram readable instead of a sea of stars.
        foreach (var n in g.Nodes)
            if (n.Role.Length == 0 && ends.Contains(n.Id) && g.Children(n.Id).Count == 0)
                n.Role = "target";
        return g;
    }

    // ---- ASCII renderer ------------------------------------------------------------------------

    private static string Ascii(Graph g, bool fullAscii)
    {
        var roots = g.Roots();
        return IsLinearChain(g, roots) ? AsciiBoxes(g, roots[0]) : AsciiTree(g, roots, fullAscii);
    }

    /// A single straight path A -> B -> C: pretty vertical boxes (the README look).
    private static string AsciiBoxes(Graph g, string rootId)
    {
        // follow the single chain
        var order = new List<Node>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var id = rootId;
        while (id != null && seen.Add(id))
        {
            var n = g.ById(id); if (n == null) break;
            order.Add(n);
            id = g.Children(id).FirstOrDefault();
        }

        var labels = order.Select(LabelWithTag).ToList();
        int w = labels.Max(s => s.Length);
        var sb = new StringBuilder();
        var bar = new string('─', w + 2);
        for (int i = 0; i < order.Count; i++)
        {
            sb.AppendLine("┌" + bar + "┐");
            var trail = order[i].Where.Length > 0 ? "   " + order[i].Where : "";
            if (order[i].Note.Length > 0) trail += "   — " + order[i].Note;   // reused --annotate one-liner
            sb.AppendLine("│ " + labels[i].PadRight(w) + " │" + trail);
            if (i < order.Count - 1)
            {
                int center = (w + 2) / 2;
                sb.AppendLine("└" + new string('─', center) + "┬" + new string('─', w + 1 - center) + "┘");
                sb.AppendLine(new string(' ', 1 + center) + "▼  calls");
            }
            else sb.AppendLine("└" + bar + "┘");
        }
        return sb.ToString();
    }

    /// Branching flow (a call-tree, or several paths that fan out / converge — e.g. DI implementations):
    /// an indented tree with box-drawing connectors. Locations are aligned into a column.
    // For trace/explain the ASCII tree is an at-a-glance view (the Mermaid block below is always complete),
    // so it's row-capped to avoid a wall of text. `map` is the deliberate full-reachability view: there we
    // render the WHOLE tree (fullAscii) and never truncate it - we only collapse a subtree that was already
    // drawn elsewhere into a one-line "↑ shown above" reference, so a re-converging (DAG) graph stays
    // bounded by node count instead of expanding every path.
    private const int MaxAsciiRows = 200;

    private static string AsciiTree(Graph g, List<string> roots, bool fullAscii)
    {
        var rows = new List<(string text, string where, string note)>();
        var stack = new HashSet<string>(StringComparer.Ordinal);    // nodes on the current path (cycle guard)
        var expanded = new HashSet<string>(StringComparer.Ordinal); // nodes whose subtree was already drawn
        int maxRows = fullAscii ? int.MaxValue : MaxAsciiRows;       // map: show it all, never truncate
        bool capped = false;

        void Walk(string nodeId, string prefix, bool isLast, bool isRoot)
        {
            if (capped) return;
            if (rows.Count >= maxRows)
            {
                rows.Add(("… (tree truncated here — the Mermaid graph below shows the full set)", "", ""));
                capped = true;
                return;
            }
            var n = g.ById(nodeId);
            if (n == null) return;
            var connector = isRoot ? "" : isLast ? "└─► " : "├─► ";

            // Already drawn this node's subtree (fullAscii/map only)? Reference it instead of re-expanding,
            // so the tree can't blow up exponentially on a dense graph while still showing every node.
            bool repeat = fullAscii && expanded.Contains(nodeId) && g.Children(nodeId).Count > 0;
            rows.Add((prefix + connector + LabelWithTag(n) + (repeat ? "  ↑ (shown above)" : ""), n.Where, n.Note));
            if (repeat) return;

            if (stack.Contains(nodeId)) { rows.Add((prefix + (isRoot ? "" : "    ") + "↩ (cycle)", "", "")); return; }
            stack.Add(nodeId);
            expanded.Add(nodeId);
            var kids = g.Children(nodeId);
            var childPrefix = isRoot ? "" : prefix + (isLast ? "    " : "│   ");
            for (int i = 0; i < kids.Count; i++)
                Walk(kids[i], childPrefix, i == kids.Count - 1, false);
            stack.Remove(nodeId);
        }

        for (int i = 0; i < roots.Count; i++) Walk(roots[i], "", true, true);

        // align file:line into a column; the reused --annotate one-liner (if any) trails after it.
        int col = rows.Where(r => r.where.Length > 0).Select(r => r.text.Length).DefaultIfEmpty(0).Max();
        var sb = new StringBuilder();
        foreach (var (text, where, note) in rows)
        {
            var line = where.Length > 0 ? text.PadRight(col + 2) + where : text;
            if (note.Length > 0) line += "   — " + note;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    // ---- Mermaid renderer ----------------------------------------------------------------------

    private static string Mermaid(Graph g)
    {
        // map graph ids -> safe mermaid ids (n0, n1, ...)
        var mid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in g.Nodes) mid[n.Id] = "n" + mid.Count;

        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("flowchart TD");
        foreach (var n in g.Nodes)
            sb.AppendLine($"    {mid[n.Id]}[\"{Esc(n.Label)}\"]");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (from, to) in g.Edges)
            if (seen.Add(from + "" + to))
                sb.AppendLine($"    {mid[from]} --> {mid[to]}");
        sb.AppendLine("    classDef entry fill:#dbeafe,stroke:#3b82f6,color:#1e3a8a,stroke-width:2px;");
        sb.AppendLine("    classDef target fill:#dcfce7,stroke:#16a34a,color:#14532d,stroke-width:2px;");
        foreach (var n in g.Nodes)
            if (n.Role.Length > 0) sb.AppendLine($"    class {mid[n.Id]} {n.Role};");
        sb.Append("```");
        return sb.ToString();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string LabelWithTag(Node n) =>
        n.Role == "entry" ? n.Label + "   ◆ start"
        : n.Role == "target" ? n.Label + "   ★ target"
        : n.Label;

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// A single root, every node with <=1 child and <=1 parent: a straight A->B->C chain.
    private static bool IsLinearChain(Graph g, List<string> roots)
    {
        if (roots.Count != 1) return false;
        foreach (var n in g.Nodes)
        {
            if (g.Edges.Count(e => e.from == n.Id) > 1) return false;
            if (g.Edges.Count(e => e.to == n.Id) > 1) return false;
        }
        return true;
    }

    /// "Type.Method(params)  - Type (file:line)" -> "Type.Method"
    private static string? CalleeName(string callee)
    {
        var dash = callee.IndexOf("  - ", StringComparison.Ordinal);
        var sig = dash > 0 ? callee[..dash] : callee;
        var paren = sig.IndexOf('(');
        var name = (paren > 0 ? sig[..paren] : sig).Trim();
        return name.Contains('.') ? name : null;
    }

    /// "...  - Type (Audit.cs:8)" -> "Audit.cs:8"
    private static string CalleeWhere(string callee)
    {
        var m = Regex.Match(callee, @"\(([^()]+\.cs:\d+)\)\s*$");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// Split an --all-paths result ("### Path N:" headers) into one segment per path; else one segment.
    private static IEnumerable<string> SplitPaths(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (!lines.Any(l => l.StartsWith("### Path ", StringComparison.Ordinal)))
        {
            yield return text;
            yield break;
        }
        var cur = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("### Path ", StringComparison.Ordinal) && cur.Length > 0)
            {
                yield return cur.ToString(); cur.Clear();
            }
            cur.Append(line).Append('\n');
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    // node line, compact:      "  1. Type.Method(args)   file.cs:NN  -->"
    //           with-bodies:   "**1. Type.Method(args)**   file.cs:NN"
    private static readonly Regex NodeRe =
        new(@"^\s*\*{0,2}(\d+)\.\s+([A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex WhereRe =
        new(@"([A-Za-z0-9_./\\-]+\.cs:\d+)", RegexOptions.Compiled);
    // the --annotate "why" note that follows a hop: "> _Executes the model-requested tool call_"
    private static readonly Regex NoteRe =
        new(@"^\s*>\s*(.+?)\s*$", RegexOptions.Compiled);

    /// Pulls the ordered (Type.Method, file:line, why-note) nodes out of one rendered path segment.
    /// The note is only present when --annotate ran (we reuse it; we never generate one here).
    private static List<(string name, string where, string note)> ExtractNodes(string segment)
    {
        var result = new List<(string name, string where, string note)>();
        foreach (var line in segment.Replace("\r\n", "\n").Split('\n'))
        {
            var m = NodeRe.Match(line);
            if (m.Success)
            {
                var name = m.Groups[2].Value;
                // location = first file.cs:line on the line AFTER the matched name (skip params/sig)
                var rest = line.Substring(Math.Min(m.Index + m.Length, line.Length));
                var w = WhereRe.Match(rest);
                result.Add((name, w.Success ? w.Groups[1].Value : "", ""));
                continue;
            }
            // a "> _note_" line attaches to the hop just above it (if it doesn't have one yet)
            var nm = NoteRe.Match(line);
            if (nm.Success && result.Count > 0 && result[^1].note.Length == 0)
            {
                var note = nm.Groups[1].Value.Trim().Trim('_', '*', '`', ' ');
                if (note.Length > 0) result[^1] = (result[^1].name, result[^1].where, note);
            }
        }
        return result;
    }
}
