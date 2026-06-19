using System.Text;

namespace CodeTracer;

/// <summary>
/// "Explain method" mode: Roslyn prepares a compact context for a single method and the model
/// explains it step by step in ONE unlimited call (+ a change proposal if a goal is given).
/// This plays to the strength of a small model (explaining supplied code), not its weakness (navigation).
/// </summary>
public class Explainer
{
    private readonly LlmClient _llm;
    private readonly int _numPredict;
    private readonly double _temperature;
    private readonly int _blockThreshold;
    private readonly string? _repoUrl;
    private readonly bool _showCode;
    private readonly CancellationToken _ct;

    public Explainer(LlmClient llm, int numPredict = 2048, double temperature = 0.2,
                     string? repoUrl = null, bool showCode = true, int blockThreshold = 400,
                     CancellationToken ct = default)
    {
        _llm = llm;
        _numPredict = numPredict;
        _temperature = temperature;
        _repoUrl = repoUrl;
        _showCode = showCode;
        _blockThreshold = blockThreshold;
        _ct = ct;
    }

    /// The method's real source as a fenced block (clipped if very long), so the reader sees the
    /// CODE next to the explanation, not just prose. Deterministic - straight from Roslyn.
    /// `level` shifts the code right (one notch per call-depth) so the nesting is visible at a
    /// glance, like the ASCII Call-flow tree. Indenting *content* inside a fence is valid Markdown
    /// (leading spaces are preserved); only indenting the ``` fence itself would break it.
    private string CodeBlock(MethodContext ctx, int level = 0, int maxLines = 160)
    {
        var pad = new string(' ', level * 3);
        var lines = ctx.Source.Replace("\r\n", "\n").Split('\n');
        bool clip = lines.Length > maxLines;
        var shown = clip ? lines.Take(maxLines) : lines.AsEnumerable();
        var sb = new StringBuilder();
        sb.AppendLine("```csharp");
        foreach (var ln in shown) sb.AppendLine(pad + ln);
        if (clip) sb.AppendLine(pad + $"// … (+{lines.Length - maxLines} more lines — full method at {ctx.Location})");
        sb.AppendLine("```");
        return sb.ToString();
    }

    /// "relpath:line" -> clickable markdown link to the repo (if repoUrl is set), otherwise plain text.
    public static string LinkLoc(string location, string? repoUrl) => RoslynIndex.RepoLink(location, repoUrl);

    /// First N lines of the body (peek). N<=0 => entire body.
    private static string Peek(string src, int n)
    {
        if (n <= 0) return src;
        var lines = src.Split('\n');
        if (lines.Length <= n) return src;
        return string.Join("\n", lines.Take(n)) + $"\n    // … (+{lines.Length - n} more lines)";
    }

    private const string SystemPrompt =
        "You are a senior C# developer. You are given ONE method and its context from a large, " +
        "20-year-old system. Explain in clear, plain English. Do not invent anything - stick to " +
        "the code you are given.\n" +
        "Be concise and skip filler: if a section (side effects, risks, trade-offs, edge cases, " +
        "exceptions, etc.) has nothing real to report, OMIT it entirely - do NOT write a sentence " +
        "saying there are none. Only include sections that add information.";

    public async Task<string> ExplainAsync(MethodContext ctx, string? goal, string? question = null,
                                            string? outPath = null)
    {
        // Self-describing header - so that a saved .md makes sense on its own (for colleagues).
        var sb = new StringBuilder();
        sb.AppendLine($"# {ctx.Display}  ({LinkLoc(ctx.Location, _repoUrl)})");
        sb.AppendLine($"`{ctx.Signature}`");
        if (!string.IsNullOrWhiteSpace(question))
            sb.AppendLine($"> **Question:** {question!.Trim()}");
        sb.AppendLine();

        // Show the actual source first, so the reader sees the code next to the explanation.
        if (_showCode) sb.AppendLine(CodeBlock(ctx));

        // Explanation and change proposal are TWO separate calls - each gets its full
        // token budget, so a verbose explanation never "eats into" the change proposal.
        var explanation = ctx.BodyLineCount > _blockThreshold
            ? await ExplainByBlocks(ctx, question)     // long methods: block by block + summary
            : await Ask(BuildUserPrompt(ctx, ctx.Source, question));
        sb.AppendLine(explanation.Trim());
        await FlushPartial(sb, outPath);

        var simple = await SimplifyForKid(explanation);   // a second, plain-words pass
        if (!string.IsNullOrWhiteSpace(simple))
        {
            sb.AppendLine();
            sb.AppendLine("## In plain words");
            sb.AppendLine(simple.Trim());
        }

        // A high-level map: this method and the in-solution methods it calls.
        var flow = Diagram.Section(Diagram.FromChain(new (int, MethodContext)[] { (0, ctx) }),
            "What this method calls");
        if (!string.IsNullOrWhiteSpace(flow))
        {
            sb.AppendLine();
            sb.AppendLine(flow);
        }
        await FlushPartial(sb, outPath);

        if (!string.IsNullOrWhiteSpace(goal))
        {
            var proposal = await ProposeChange(ctx, goal!);
            sb.AppendLine();
            sb.AppendLine("## Change proposal");
            sb.AppendLine(proposal.Trim());
            await FlushPartial(sb, outPath);
        }
        return sb.ToString();
    }

    private async Task<string> ExplainByBlocks(MethodContext ctx, string? question)
    {
        var blocks = RoslynIndex.SplitLongMethod(ctx);
        var sb = new StringBuilder();
        sb.AppendLine($"_Method is ~{ctx.BodyLineCount} lines - explaining block by block._");
        sb.AppendLine();

        var partial = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int b = 0;
        foreach (var (label, code) in blocks)
        {
            b++;
            // +1 remaining unit for the final stitch-together summary pass.
            Console.Error.WriteLine($"[explain] block {b}/{blocks.Count} ({label}) ...{Eta(sw.Elapsed, b - 1, blocks.Count - (b - 1) + 1)}");
            var prompt = new StringBuilder();
            prompt.AppendLine(HeaderBlock(ctx));
            prompt.AppendLine($"Below is ONE BLOCK of the method ({label}). Explain in numbered steps what this block does.");
            prompt.AppendLine("Focus only on this block; do not quote the rest of the method.");
            if (!string.IsNullOrWhiteSpace(question))
                prompt.AppendLine($"Keep in mind the user's question: {question!.Trim()}");
            prompt.AppendLine();
            prompt.AppendLine("```csharp");
            prompt.AppendLine(code);
            prompt.AppendLine("```");

            var part = await Ask(prompt.ToString());
            sb.AppendLine($"## {label}");
            sb.AppendLine(part.Trim());
            sb.AppendLine();
            partial.Add($"{label}: {Shorten(part, 600)}");
        }

        // final summary across the block results
        var summaryPrompt = new StringBuilder();
        summaryPrompt.AppendLine(HeaderBlock(ctx));
        summaryPrompt.AppendLine("You have partial explanations of the individual blocks:");
        foreach (var p in partial) summaryPrompt.AppendLine($"- {p}");
        summaryPrompt.AppendLine();
        summaryPrompt.AppendLine("Write a short, coherent SUMMARY (3-6 sentences) of what the method does as a " +
                                 "whole, its inputs/outputs and side effects.");
        if (!string.IsNullOrWhiteSpace(question))
            summaryPrompt.AppendLine($"Make sure the summary answers: {question!.Trim()}");

        var summary = await Ask(summaryPrompt.ToString());
        sb.AppendLine("## Summary");
        sb.AppendLine(summary.Trim());
        return sb.ToString();
    }

    private string BuildUserPrompt(MethodContext ctx, string code, string? question)
    {
        var sb = new StringBuilder();
        sb.AppendLine(HeaderBlock(ctx));
        sb.AppendLine("Method source:");
        sb.AppendLine("```csharp");
        sb.AppendLine(code);
        sb.AppendLine("```");
        AppendDependencies(sb, ctx);
        sb.AppendLine();
        sb.AppendLine("Task:");
        if (!string.IsNullOrWhiteSpace(question))
        {
            // The question goes FIRST - so the model answers it even if num_predict cuts it short.
            sb.AppendLine($"PRIMARY: answer this question first, directly and concretely: {question!.Trim()}");
            sb.AppendLine("Use the method body, its dependencies, and its callers (REACHED FROM) above as needed.");
            sb.AppendLine("Then, more briefly:");
        }
        sb.AppendLine("1. Explain in NUMBERED steps what the method does.");
        sb.AppendLine("2. State the inputs (parameters) and the output (return value).");
        sb.AppendLine("3. Side effects (fields written, services called, exceptions) - ONLY if there are any; skip otherwise.");
        if (ctx.Dependencies.Count > 0)
            sb.AppendLine("4. Explain how the method and its dependencies (below) WORK TOGETHER - " +
                          "who computes what, what is passed to whom, where the result is produced.");
        return sb.ToString();
    }

    /// Proposal for a concrete change as a SEPARATE call (full token budget, independent of the explanation).
    private async Task<string> ProposeChange(MethodContext ctx, string goal)
    {
        var sb = new StringBuilder();
        sb.AppendLine(HeaderBlock(ctx));
        sb.AppendLine("Method source:");
        sb.AppendLine("```csharp");
        sb.AppendLine(ctx.Source);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"CHANGE GOAL: {goal}");
        sb.AppendLine("Propose a concrete change:");
        sb.AppendLine("1. What exactly to change (which lines / which logic).");
        sb.AppendLine("2. Code sample - the modified block or a diff.");
        sb.AppendLine("3. Risks or side effects of the change - only if there are real ones; skip otherwise.");
        return await Ask(sb.ToString());
    }

    /// Appends (truncated) bodies of in-solution dependencies so the model can explain the interplay.
    private static void AppendDependencies(StringBuilder sb, MethodContext ctx)
    {
        if (ctx.Dependencies.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"DEPENDENCIES (bodies of called methods within the solution, truncated - {ctx.Dependencies.Count}):");
        foreach (var (display, code) in ctx.Dependencies)
        {
            sb.AppendLine($"--- {display} ---");
            sb.AppendLine("```csharp");
            sb.AppendLine(code);
            sb.AppendLine("```");
        }
    }

    /// Structured context header - compact, never the entire file.
    private static string HeaderBlock(MethodContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"METHOD: {ctx.Display}");
        sb.AppendLine($"LOCATION: {ctx.Location}");
        sb.AppendLine($"SIGNATURE: {ctx.Signature}");
        if (ctx.DocComment != null)
            sb.AppendLine($"DOC: {ctx.DocComment}");
        if (ctx.Parameters.Count > 0)
            sb.AppendLine($"PARAMETERS: {string.Join(", ", ctx.Parameters)}");
        if (ctx.Reads.Count > 0)
            sb.AppendLine($"READS FIELDS/PROPERTIES: {string.Join(", ", ctx.Reads)}");
        if (ctx.Writes.Count > 0)
            sb.AppendLine($"WRITES FIELDS/PROPERTIES: {string.Join(", ", ctx.Writes)}");
        if (ctx.Callees.Count > 0)
        {
            sb.AppendLine("CALLS METHODS:");
            foreach (var c in ctx.Callees) sb.AppendLine($"  - {c}");
        }
        if (ctx.Callers.Count > 0)
        {
            sb.AppendLine("REACHED FROM (callers):");
            foreach (var c in ctx.Callers) sb.AppendLine($"  - {c}");
        }
        return sb.ToString();
    }

    private async Task<string> Ask(string userContent, int? numPredict = null, string label = "explain")
    {
        var msgs = new[]
        {
            new ChatMsg("system", SystemPrompt),
            new ChatMsg("user", userContent)
        };
        return await _llm.ChatAsync(msgs, new ChatOptions
        {
            Temperature = _temperature,
            NumPredict = numPredict ?? _numPredict,
            // Format intentionally null - explain is free text (the model's strength), no grammar constraint.
        }, label, _ct);
    }

    /// A second, very simple "explain like I'm 10" pass over the explanation - in plain words,
    /// what the code is for and what the point of it is. Cheap (short input/output).
    private async Task<string> SimplifyForKid(string detailed)
    {
        try
        {
            var prompt =
                "Here is a detailed explanation of some code:\n\n" + detailed + "\n\n" +
                "Now say it again VERY simply - as if to a smart 10-year-old. In 2-4 short sentences, " +
                "plain words, no jargon: what is this code for and what is the point of it?";
            return (await _llm.ChatAsync(new[]
            {
                new ChatMsg("system", "You explain technical things in very simple, plain language. No jargon."),
                new ChatMsg("user", prompt)
            }, new ChatOptions { Temperature = 0.3, NumPredict = 800, Think = false }, "eli10", _ct)).Trim();
        }
        catch (OperationCanceledException) { throw; }   // a user Ctrl+C must stop, not be swallowed
        catch { return ""; }
    }

    /// Deep explain: explains the ENTIRE logic along the call chain. Each method in the chain is explained
    /// with a SEPARATE call (full attention, not a truncated snippet), followed by an end-to-end synthesis.
    public async Task<string> ExplainChainAsync(
        IReadOnlyList<(int level, MethodContext ctx)> chain, string? goal, string? question,
        string? outPath = null)
    {
        var root = chain[0].ctx;
        var sb = new StringBuilder();
        sb.AppendLine($"# {root.Display}  ({LinkLoc(root.Location, _repoUrl)})");
        sb.AppendLine($"`{root.Signature}`");
        if (!string.IsNullOrWhiteSpace(question))
            sb.AppendLine($"> **Question:** {question!.Trim()}");
        sb.AppendLine($"_Deep explanation following the call chain ({chain.Count} methods)._");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(outPath))
            Console.Error.WriteLine($"[explain] writing partial results to {outPath} after each method " +
                                    "(an interrupted run won't lose finished work).");

        // Each node is kept shorter (focused on its own method); synthesis gets the full budget.
        int nodeBudget = Math.Min(_numPredict, 1100);
        var notes = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int total = chain.Count;
        int i = 0;
        foreach (var (level, ctx) in chain)
        {
            i++;
            // Live ETA: average the methods already done; remaining work = methods left + the synthesis pass.
            var eta = Eta(sw.Elapsed, i - 1, total - (i - 1) + 1);
            Console.Error.WriteLine($"[explain] ({i}/{total}) L{level}  {ctx.Display} ...{eta}");
            var part = await Ask(BuildNodePrompt(ctx), nodeBudget);
            sb.AppendLine($"## L{level} · {ctx.Display}  ({LinkLoc(ctx.Location, _repoUrl)})");
            if (_showCode) sb.AppendLine(CodeBlock(ctx, level));
            sb.AppendLine(part.Trim());
            sb.AppendLine();
            notes.Add($"L{level} {ctx.Display}: {Shorten(part, 500)}");
            await FlushPartial(sb, outPath);              // persist after every method
        }

        // synthesis: how it all fits together from input to output
        Console.Error.WriteLine($"[explain] synthesizing end-to-end logic ...{Eta(sw.Elapsed, total, 1)}");
        var synth = new StringBuilder();
        synth.AppendLine($"Entry method: {root.Display}  ({root.Signature}).");
        synth.AppendLine("Per-method explanations of the call chain (entry first):");
        foreach (var n in notes) synth.AppendLine($"- {n}");
        synth.AppendLine();
        synth.AppendLine("Now describe the COMPLETE END-TO-END logic: how execution flows from the entry " +
                         "method down through these methods, where the real work happens, the key branches/" +
                         "decisions, what data gets transformed, and the final outcome. Be concrete.");
        if (!string.IsNullOrWhiteSpace(question))
            synth.AppendLine($"Above all, answer: {question!.Trim()}");

        var synthText = await Ask(synth.ToString());
        sb.AppendLine("## End-to-end logic");
        sb.AppendLine(synthText.Trim());
        await FlushPartial(sb, outPath);

        var simple = await SimplifyForKid(synthText);   // a second, plain-words pass
        if (!string.IsNullOrWhiteSpace(simple))
        {
            sb.AppendLine();
            sb.AppendLine("## In plain words");
            sb.AppendLine(simple.Trim());
        }

        // A high-level map of what the analysis walked, so the reader sees the whole shape at a glance.
        var flow = Diagram.Section(Diagram.FromChain(chain),
            "How execution flows through the methods explained above");
        if (!string.IsNullOrWhiteSpace(flow))
        {
            sb.AppendLine();
            sb.AppendLine(flow);
        }
        await FlushPartial(sb, outPath);

        if (!string.IsNullOrWhiteSpace(goal))
        {
            var proposal = await ProposeChange(root, goal!);
            sb.AppendLine();
            sb.AppendLine("## Change proposal");
            sb.AppendLine(proposal.Trim());
            await FlushPartial(sb, outPath);
        }
        return sb.ToString();
    }

    private string BuildNodePrompt(MethodContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine(HeaderBlock(ctx));
        sb.AppendLine("Method source:");
        sb.AppendLine("```csharp");
        sb.AppendLine(ctx.Source);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Explain concisely (numbered steps) what THIS method does: its inputs/outputs, any side " +
                      "effects, and what it delegates to the methods it calls. Stay focused on this method. " +
                      "Skip any aspect that has nothing to report - don't pad with 'there are none'.");
        return sb.ToString();
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s.Trim() : s[..max].Trim() + " ...";

    /// "  · ~Xm left" estimated from the average time of the work already done. Empty until we have
    /// at least one timed step (no point guessing from nothing). `remainingUnits` = passes still to run.
    private static string Eta(TimeSpan elapsed, int done, int remainingUnits)
    {
        if (done <= 0 || remainingUnits <= 0) return "";
        var perUnit = elapsed.TotalSeconds / done;
        return $"  · ~{FmtDuration(TimeSpan.FromSeconds(perUnit * remainingUnits))} left";
    }

    private static string FmtDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{Math.Ceiling(t.TotalSeconds)}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalHours}h {t.Minutes}m";
    }

    /// Writes the result-so-far to disk. Best-effort: a transient write failure (file locked, etc.)
    /// must never abort a long run, so it's swallowed - the next flush, or the final write, will retry.
    private static async Task FlushPartial(StringBuilder sb, string? outPath)
    {
        if (string.IsNullOrWhiteSpace(outPath)) return;
        try { await File.WriteAllTextAsync(outPath!, sb.ToString()); }
        catch { /* ignore - partial-save is a safety net, not a hard requirement */ }
    }

    /// --no-llm: renders the Roslyn-extracted context (method + call-chain + sources)
    /// to markdown WITHOUT calling the model. Output is ready to paste into a larger model.
    /// repoUrl => clickable links to the full file in the repo; peek>0 => only the first N lines of the body.
    public static string RenderContext(IReadOnlyList<(int level, MethodContext ctx)> chain,
                                       string? repoUrl = null, int peek = 0)
    {
        var root = chain[0].ctx;
        var sb = new StringBuilder();
        sb.AppendLine($"# Context for {root.Display}  ({LinkLoc(root.Location, repoUrl)})");
        sb.AppendLine($"_Roslyn-extracted, {chain.Count} method(s). No model was run — paste this into a " +
                      "bigger model and ask it to explain the logic step by step._");
        sb.AppendLine();
        foreach (var (level, ctx) in chain)
        {
            sb.AppendLine($"## L{level} · {ctx.Display}  ({LinkLoc(ctx.Location, repoUrl)})");
            sb.AppendLine(HeaderBlock(ctx).Trim());
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(Peek(ctx.Source, peek));
            sb.AppendLine("```");
            if (peek > 0)
                sb.AppendLine($"↳ full method / file: {LinkLoc(ctx.Location, repoUrl)}");
            sb.AppendLine();
        }

        var flow = Diagram.Section(Diagram.FromChain(chain), "How these methods connect");
        if (!string.IsNullOrWhiteSpace(flow))
            sb.AppendLine(flow);
        return sb.ToString();
    }
}
