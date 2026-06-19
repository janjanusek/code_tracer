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

    public Explainer(LlmClient llm, int numPredict = 2048, double temperature = 0.2,
                     string? repoUrl = null, int blockThreshold = 400)
    {
        _llm = llm;
        _numPredict = numPredict;
        _temperature = temperature;
        _repoUrl = repoUrl;
        _blockThreshold = blockThreshold;
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
        "the code you are given.";

    public async Task<string> ExplainAsync(MethodContext ctx, string? goal, string? question = null)
    {
        // Self-describing header - so that a saved .md makes sense on its own (for colleagues).
        var sb = new StringBuilder();
        sb.AppendLine($"# {ctx.Display}  ({LinkLoc(ctx.Location, _repoUrl)})");
        sb.AppendLine($"`{ctx.Signature}`");
        if (!string.IsNullOrWhiteSpace(question))
            sb.AppendLine($"> **Question:** {question!.Trim()}");
        sb.AppendLine();

        // Explanation and change proposal are TWO separate calls - each gets its full
        // token budget, so a verbose explanation never "eats into" the change proposal.
        var explanation = ctx.BodyLineCount > _blockThreshold
            ? await ExplainByBlocks(ctx, question)     // long methods: block by block + summary
            : await Ask(BuildUserPrompt(ctx, ctx.Source, question));
        sb.AppendLine(explanation.Trim());

        var simple = await SimplifyForKid(explanation);   // a second, plain-words pass
        if (!string.IsNullOrWhiteSpace(simple))
        {
            sb.AppendLine();
            sb.AppendLine("## In plain words");
            sb.AppendLine(simple.Trim());
        }

        if (!string.IsNullOrWhiteSpace(goal))
        {
            var proposal = await ProposeChange(ctx, goal!);
            sb.AppendLine();
            sb.AppendLine("## Change proposal");
            sb.AppendLine(proposal.Trim());
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
        foreach (var (label, code) in blocks)
        {
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
        sb.AppendLine("3. State side effects: fields written, services called, exceptions.");
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
        sb.AppendLine("3. Risks and side effects of the change.");
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
        }, label);
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
            }, new ChatOptions { Temperature = 0.3, NumPredict = 300 }, "eli10")).Trim();
        }
        catch { return ""; }
    }

    /// Deep explain: explains the ENTIRE logic along the call chain. Each method in the chain is explained
    /// with a SEPARATE call (full attention, not a truncated snippet), followed by an end-to-end synthesis.
    public async Task<string> ExplainChainAsync(
        IReadOnlyList<(int level, MethodContext ctx)> chain, string? goal, string? question)
    {
        var root = chain[0].ctx;
        var sb = new StringBuilder();
        sb.AppendLine($"# {root.Display}  ({LinkLoc(root.Location, _repoUrl)})");
        sb.AppendLine($"`{root.Signature}`");
        if (!string.IsNullOrWhiteSpace(question))
            sb.AppendLine($"> **Question:** {question!.Trim()}");
        sb.AppendLine($"_Deep explanation following the call chain ({chain.Count} methods)._");
        sb.AppendLine();

        // Each node is kept shorter (focused on its own method); synthesis gets the full budget.
        int nodeBudget = Math.Min(_numPredict, 1100);
        var notes = new List<string>();
        int i = 0;
        foreach (var (level, ctx) in chain)
        {
            i++;
            Console.Error.WriteLine($"[explain] ({i}/{chain.Count}) L{level}  {ctx.Display} ...");
            var part = await Ask(BuildNodePrompt(ctx), nodeBudget);
            sb.AppendLine($"## L{level} · {ctx.Display}  ({LinkLoc(ctx.Location, _repoUrl)})");
            sb.AppendLine(part.Trim());
            sb.AppendLine();
            notes.Add($"L{level} {ctx.Display}: {Shorten(part, 500)}");
        }

        // synthesis: how it all fits together from input to output
        Console.Error.WriteLine("[explain] synthesizing end-to-end logic ...");
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

        var simple = await SimplifyForKid(synthText);   // a second, plain-words pass
        if (!string.IsNullOrWhiteSpace(simple))
        {
            sb.AppendLine();
            sb.AppendLine("## In plain words");
            sb.AppendLine(simple.Trim());
        }

        if (!string.IsNullOrWhiteSpace(goal))
        {
            var proposal = await ProposeChange(root, goal!);
            sb.AppendLine();
            sb.AppendLine("## Change proposal");
            sb.AppendLine(proposal.Trim());
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
        sb.AppendLine("Explain concisely (numbered steps) what THIS method does: its inputs/outputs, side " +
                      "effects, and what it delegates to the methods it calls. Stay focused on this method.");
        return sb.ToString();
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s.Trim() : s[..max].Trim() + " ...";

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
        return sb.ToString();
    }
}
