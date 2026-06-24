using System.Text;
using System.Text.Json;

namespace CodeTracer;

public class Agent
{
    private readonly LlmClient _llm;
    private readonly RoslynIndex _index;
    private readonly int _maxSteps;
    private readonly bool _summarize;
    private readonly bool _useLlm;
    private readonly bool _allPaths;
    private readonly bool _withBodies;
    private readonly bool _annotate;
    private readonly string? _repoUrl;
    private readonly string? _outPath;
    private readonly int _actionNumPredict;

    // Deterministically pre-built candidate pairs for find_path.
    private readonly List<(string fc, string fm, string tc, string tm)> _pairs = new();

    // Last successful find_path - used to assemble the final path deterministically on `finish`.
    private string? _lastPath;

    public Agent(LlmClient llm, RoslynIndex index, int maxSteps = 25,
                 bool summarize = false, bool useLlm = true, bool allPaths = false,
                 bool withBodies = false, bool annotate = false, string? repoUrl = null,
                 string? outPath = null, int actionNumPredict = 512)
    {
        _llm = llm;
        _index = index;
        _maxSteps = maxSteps;
        _summarize = summarize;
        _useLlm = useLlm;
        _allPaths = allPaths;
        _withBodies = withBodies;
        _annotate = annotate;
        _repoUrl = repoUrl;
        _outPath = outPath;
        _actionNumPredict = actionNumPredict;
    }

    // Tools the model is allowed to choose. find_references is dispatchable but intentionally
    // NOT in the schema enum (keeping the set small and flat for grammar reasons).
    private static readonly string[] AllowedTools =
        { "find_path", "find_callers", "find_callees", "get_method",
          "find_symbol", "outline", "read_file", "grep", "finish" };

    // Flat JSON schema for an action: one `tool` enum + one flat `args` object with
    // all possible optional fields. No anyOf/oneOf, no free-text fields (`thought` is
    // intentionally absent - inside grammar-constrained JSON it triggers a repetition-loop
    // in small models).
    private const string ActionSchemaJson = """
    {
      "type": "object",
      "properties": {
        "tool": {
          "type": "string",
          "enum": ["find_path","find_callers","find_callees","get_method",
                   "find_symbol","outline","read_file","grep","finish"]
        },
        "args": {
          "type": "object",
          "properties": {
            "fromClass": {"type":"string"}, "fromMethod": {"type":"string"},
            "toClass":   {"type":"string"}, "toMethod":   {"type":"string"},
            "class":     {"type":"string"}, "method":     {"type":"string"},
            "file":      {"type":"string"}, "name":       {"type":"string"},
            "pattern":   {"type":"string"},
            "start":     {"type":"integer"}, "end":       {"type":"integer"}
          },
          "additionalProperties": false
        }
      },
      "required": ["tool","args"],
      "additionalProperties": false
    }
    """;

    private static readonly JsonElement ActionSchema =
        JsonDocument.Parse(ActionSchemaJson).RootElement.Clone();

    private static readonly JsonElement EmptyArgs =
        JsonDocument.Parse("{}").RootElement.Clone();

    private const string SystemPrompt = """
You are a C# code-tracing agent over a Roslyn-indexed solution. You find how execution
reaches a target call. You reason ONLY on tool output, never from memory.

Respond with ONLY a JSON object of the form {"tool": <name>, "args": { ... }}.
No prose, no markdown, no explanations - just the JSON object.

TOOLS and their args:
  find_path     {"fromClass":"C","fromMethod":"M","toClass":"C2","toMethod":"M2"}
  find_callers  {"class":"C","method":"M"}
  find_callees  {"class":"C","method":"M"}
  get_method    {"class":"C","method":"M"}
  find_symbol   {"name":"X"}
  outline       {"file":"path"}
  read_file     {"file":"path","start":10,"end":40}
  grep          {"pattern":"text"}
  finish        {}            -- use when a connected chain is found; the program prints the path

RULES:
  * Prefer find_path FIRST. It is deterministic and usually solves the task in one call.
  * NEVER call the same tool with the same args twice. If an observation did not help,
    CHANGE approach - do not repeat it.
  * For a Razor endpoint (.cshtml), the handler lives in the .cshtml.cs page model and is
    named OnGet / OnGetAsync / OnPost / OnPostAsync - NOT a method named after the page.
  * As soon as you have a connected chain (e.g. find_path returned PATH FOUND), return
    {"tool":"finish","args":{}}. Do not narrate the answer - the program assembles it.

STRATEGY: try find_path from each endpoint handler to each target method. If it fails,
fall back to find_callers from the target going UP one hop at a time, then finish.
""";

    public async Task RunAsync(string solutionPath, string targetFile, string endpoint)
    {
        var seed = Bootstrap(targetFile, endpoint);

        // Deterministic pre-flight: try candidate find_path pairs IMMEDIATELY. On CPU this is
        // faster and more reliable than waiting for (often under-filled) model calls. Roslyn
        // is the source of truth; the model is here only to navigate harder cases (interface/DI/events).
        // --all-paths/--brute: enumerate ALL paths (deep), not just the first shortest one.
        var mode = _allPaths ? "brute-force (all paths)" : "first path";
        Console.WriteLine($"[pre-flight] deterministic find_path over {_pairs.Count} candidate pairs [{mode}]...");
        var deterministic = _allPaths ? await TryAllPaths() : await TryAutoPath();
        if (deterministic.Contains("PATH FOUND"))
        {
            await Finish(deterministic, _allPaths ? "brute-force" : "pre-flight");
            return;
        }
        if (!_useLlm)
        {
            Console.WriteLine("[pre-flight] no direct path and --no-llm set - stopping.");
            await Finish(deterministic, "deterministic");
            return;
        }
        Console.WriteLine("[pre-flight] no direct path - handing over to the model loop...");

        var messages = new List<ChatMsg>
        {
            new("system", SystemPrompt),
            new("user", seed)
        };

        var seen = new HashSet<string>();
        int escalations = 0;

        for (int step = 1; step <= _maxSteps; step++)
        {
            var act = await GetAction(messages);
            if (act == null)
            {
                // model could not produce a valid action even after corrections -> deterministic escalation
                Console.WriteLine("\n[auto] model gave no valid action - using deterministic result...");
                await Finish(_lastPath ?? deterministic, "auto");
                return;
            }

            var (tool, args, raw) = act.Value;
            Console.WriteLine($"\n===== STEP {step} =====\n{raw}");

            if (tool == "finish")
            {
                var pathText = _lastPath ?? await TryAutoPath();
                await Finish(pathText, "finish");
                return;
            }

            // --- loop detection -------------------------------------------------
            var key = $"{tool}|{Canonical(args)}";
            if (!seen.Add(key))
            {
                escalations++;
                Console.WriteLine($"[!] repeated step ({escalations}) - escalating");

                if (escalations == 1)
                {
                    // one more chance: explicitly dictate the find_path calls to it
                    messages.Add(new("assistant", raw));
                    messages.Add(new("user",
                        "STOP. You already ran this exact tool+args. Do NOT repeat it.\n" +
                        "Call find_path now (return the JSON), e.g.:\n" + SuggestPairs()));
                    continue;
                }

                // model is looping -> use deterministic result (within 2 steps)
                Console.WriteLine("[auto] loop detected - using deterministic result...");
                await Finish(_lastPath ?? deterministic, "auto");
                return;
            }

            string observation;
            try { observation = await Dispatch(tool, args); }
            catch (Exception ex) { observation = $"TOOL ERROR: {ex.Message}"; }

            if (tool == "find_path" && observation.Contains("PATH FOUND"))
                _lastPath = observation;

            if (observation.Length > 3000)
                observation = observation[..3000] + "\n... (truncated)";

            Console.WriteLine($"--- OBSERVATION ---\n{observation.Trim()}");

            messages.Add(new("assistant", raw));
            messages.Add(new("user", $"OBSERVATION:\n{observation}"));
        }

        // step limit exhausted -> use the best available result
        Console.WriteLine($"\n[!] step limit {_maxSteps} reached - using deterministic result");
        await Finish(_lastPath ?? deterministic, "limit");
    }

    /// Direct mode (--from/--to): find_path between two concrete methods, with the same
    /// rendering options (with-bodies / annotate) and final --summary as the normal trace.
    public async Task RunDirectAsync(string fromClass, string fromMethod, string toClass, string toMethod)
    {
        var ann = Annotator();
        if (_allPaths)
        {
            // enumerate EVERY path (e.g. one per interface implementation that reaches the target)
            Console.WriteLine($"[direct] all paths {fromClass}.{fromMethod} -> {toClass}.{toMethod}");
            var all = await _index.FindAllPaths(fromClass, fromMethod, toClass, toMethod,
                withBodies: _withBodies, repoUrl: _repoUrl, annotate: ann);
            await Finish(all, "direct-all");
            return;
        }
        Console.WriteLine($"[direct] find_path {fromClass}.{fromMethod} -> {toClass}.{toMethod}");
        var res = await _index.FindPath(fromClass, fromMethod, toClass, toMethod,
            maxNodes: 20000, withBodies: _withBodies, repoUrl: _repoUrl, annotate: ann);
        await Finish(res, "direct");
    }

    // ---- action selection: token-level enforced JSON + validation + retry ---------

    /// Requests one action from the model as grammar-constrained JSON. The grammar guarantees
    /// STRUCTURE; here we validate VALUES (tool is in the enum, args make sense for the given tool).
    /// On a bad value sends a correction turn and retries (max 2x), otherwise returns null.
    private async Task<(string tool, JsonElement args, string raw)?> GetAction(List<ChatMsg> messages)
    {
        var opts = new ChatOptions { Temperature = 0, NumPredict = _actionNumPredict, Format = ActionSchema };

        for (int attempt = 0; attempt < 3; attempt++)   // 1 attempt + 2 corrections
        {
            var raw = (await _llm.ChatAsync(messages, opts, "action")).Trim();

            // the grammar should guarantee valid JSON; on interruption (num_predict) it may
            // return unclosed JSON - handle that.
            JsonElement root;
            try { root = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch
            {
                messages.Add(new("assistant", raw));
                messages.Add(new("user", "Your output was not a valid JSON object. Return ONLY {\"tool\":...,\"args\":{...}}."));
                continue;
            }

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool", out var toolEl)
                || toolEl.ValueKind != JsonValueKind.String)
            {
                messages.Add(new("assistant", raw));
                messages.Add(new("user", "Error: the object must have a string field \"tool\" and an object \"args\". Try again."));
                continue;
            }

            var tool = toolEl.GetString()!.Trim().ToLowerInvariant();
            var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : EmptyArgs;

            if (!AllowedTools.Contains(tool))
            {
                messages.Add(new("assistant", raw));
                messages.Add(new("user", $"Unknown tool '{tool}'. Allowed: {string.Join(", ", AllowedTools)}."));
                continue;
            }

            var err = ValidateArgs(tool, args);
            if (err != null)
            {
                messages.Add(new("assistant", raw));
                messages.Add(new("user", $"Invalid args for '{tool}': {err} Return corrected JSON."));
                continue;
            }

            return (tool, args, raw);
        }
        return null;
    }

    /// Returns null if args are ok, otherwise a description of the missing fields for the given tool.
    private static string? ValidateArgs(string tool, JsonElement args)
    {
        string Get(string k) => args.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";
        bool Has(string k) => !string.IsNullOrWhiteSpace(Get(k));
        string Need(params string[] keys)
        {
            var missing = keys.Where(k => !Has(k)).ToArray();
            return missing.Length == 0 ? "" : "missing fields: " + string.Join(", ", missing) + ".";
        }

        return tool switch
        {
            "find_path"    => Empty(Need("fromClass", "fromMethod", "toClass", "toMethod")),
            "find_callers" => Empty(Need("class", "method")),
            "find_callees" => Empty(Need("class", "method")),
            "get_method"   => Empty(Need("class", "method")),
            "find_symbol"  => Empty(Need("name")),
            "outline"      => Empty(Need("file")),
            "read_file"    => Empty(Need("file")),
            "grep"         => Empty(Need("pattern")),
            "finish"       => null,
            _              => null,
        };
        static string? Empty(string s) => s.Length == 0 ? null : s;
    }

    /// Canonical (stable) representation of args for repeat-detection.
    private static string Canonical(JsonElement args) => JsonSerializer.Serialize(args).ToLowerInvariant();

    // ---- finalization -------------------------------------------------------

    /// Prints the final path (assembled deterministically) and, when --summary is on, an LLM
    /// summary section (purpose, dependencies, good-to-know) included in BOTH console and --out.
    private async Task Finish(string pathText, string reason)
    {
        var output = pathText.Trim();

        // Built from the CLEAN path text (before any summary prose is appended), so the diagram
        // reflects only the discovered call-path. Appended at the very end of the result.
        var flow = Diagram.Section(Diagram.FromTraceText(output), "The path the analysis found");

        if (_summarize && output.Contains("PATH FOUND"))
        {
            Console.Error.WriteLine("[summary] summarizing the chain...");
            var summary = await SummarizeChain(pathText);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                output += "\n\n## Summary\n" + summary.Trim();
                var simple = await SimplifyForKid(summary);     // a second, plain-words pass
                if (!string.IsNullOrWhiteSpace(simple))
                    output += "\n\n## In plain words\n" + simple.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(flow))
            output += "\n\n" + flow;

        Console.WriteLine($"\n========== DONE ({reason}) ==========");
        Console.WriteLine(output);

        if (!string.IsNullOrWhiteSpace(_outPath))
        {
            try
            {
                var saved = output + "\n";
                await Compat.WriteAllTextAsync(_outPath!, saved);
                Console.Error.WriteLine($"[trace] saved to {_outPath}");
                var html = await HtmlViewer.WriteSiblingAsync(_outPath!, saved);
                if (html != null)
                    Console.Error.WriteLine($"[viewer] interactive graph (fit-to-window, zoom/pan) -> {html}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"[write error] {ex.Message}"); }
        }
    }

    /// A final SUMMARY of the whole chain: what it does, its dependencies, and "good to know"
    /// (important / non-obvious things the user did not ask about). One unconstrained call.
    private async Task<string> SummarizeChain(string pathText)
    {
        try
        {
            var prompt =
                "Below is a traced call chain (with code) from a large C# system.\n\n" +
                pathText + "\n\n" +
                "Write a brief SUMMARY for a developer seeing this for the first time:\n" +
                "1. **What it does / purpose** - what this chain achieves, factually.\n" +
                "2. **Dependencies** - the key services, types or external calls it relies on.\n" +
                "3. **Good to know** - anything important or non-obvious they did NOT ask about " +
                "(side effects, assumptions, edge cases, gotchas).\n" +
                "Be concise and concrete; short paragraphs or bullets. OMIT any of the three that has " +
                "nothing real to report - do NOT write a sentence saying there are none / no risks / " +
                "no trade-offs. Only include what adds information.";
            return (await _llm.ChatAsync(new[]
            {
                new ChatMsg("system", "You summarize a code call-chain for a developer. Concise, factual, plain English. " +
                    "Skip empty sections instead of writing that something is absent."),
                new ChatMsg("user", prompt)
            }, new ChatOptions { Temperature = 0.2, NumPredict = 2048 }, "summary")).Trim();
        }
        catch (Exception ex) { return $"_(summary unavailable: {ex.Message})_"; }
    }

    /// A second, very simple "explain like I'm 10" pass over the full summary - what the path is
    /// for and why it goes from start to end, in plain words. Cheap (short input/output).
    private async Task<string> SimplifyForKid(string fullSummary)
    {
        try
        {
            var prompt =
                "Here is a technical summary of a code path:\n\n" + fullSummary + "\n\n" +
                "Now say it again VERY simply - as if to a smart 10-year-old. In 2-4 short sentences, " +
                "plain words, no jargon: what is this code for, and why does it go from the start to " +
                "the end (what's the point)?";
            return (await _llm.ChatAsync(new[]
            {
                new ChatMsg("system", "You explain technical things in very simple, plain language. No jargon."),
                new ChatMsg("user", prompt)
            }, new ChatOptions { Temperature = 0.3, NumPredict = 800, Think = false }, "eli10")).Trim();
        }
        catch { return ""; }
    }

    // ---- deterministic bootstrap -----------------------------------------

    private string Bootstrap(string targetFile, string endpoint)
    {
        // endpoint: if it is a .cshtml, the handler lives in .cshtml.cs
        var endpointCs = endpoint.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
            ? endpoint + ".cs" : endpoint;

        var sb = new StringBuilder();
        sb.AppendLine("Goal: find the call chain from the ENDPOINT down to the call in the TARGET FILE.");
        sb.AppendLine();

        var fromMethods = new List<(string cls, string method, int line)>();
        if (File.Exists(endpointCs))
        {
            fromMethods = _index.MethodsInFile(endpointCs);
            sb.AppendLine($"ENDPOINT page model ({Path.GetFileName(endpointCs)}):");
            foreach (var m in fromMethods)
                sb.AppendLine($"  {m.cls}.{m.method}  :{m.line}");
        }
        else
        {
            sb.AppendLine($"ENDPOINT: {endpoint}  (resolve it with find_symbol/grep)");
        }
        sb.AppendLine();

        var toMethods = _index.MethodsInFile(targetFile);
        sb.AppendLine($"TARGET FILE ({Path.GetFileName(targetFile)}) methods:");
        foreach (var m in toMethods)
            sb.AppendLine($"  {m.cls}.{m.method}  :{m.line}");
        sb.AppendLine();

        // select candidates: handlers (On*) as source, all target methods as destination
        var handlers = fromMethods.Where(m => m.method.StartsWith("On", StringComparison.Ordinal)).ToList();
        if (handlers.Count == 0) handlers = fromMethods;                 // fallback: all
        var targets = toMethods
            .Where(m => !m.method.Equals(".ctor"))
            .OrderByDescending(m => m.method.EndsWith("Async") ||
                                    m.method.StartsWith("Build") || m.method.StartsWith("Generate"))
            .ToList();

        foreach (var h in handlers)
            foreach (var t in targets)
            {
                if (_pairs.Count >= 24) break;
                _pairs.Add((h.cls, h.method, t.cls, t.method));
            }

        sb.AppendLine("Start by calling find_path. Suggested first call:");
        sb.AppendLine(SuggestPairs());
        return sb.ToString();
    }

    private string SuggestPairs()
    {
        var sb = new StringBuilder();
        foreach (var p in _pairs.Take(3))
            sb.AppendLine($"  {{\"tool\":\"find_path\",\"args\":{{\"fromClass\":\"{p.fc}\",\"fromMethod\":\"{p.fm}\",\"toClass\":\"{p.tc}\",\"toMethod\":\"{p.tm}\"}}}}");
        return sb.Length == 0 ? "  (no candidates - use find_symbol to resolve the endpoint)" : sb.ToString();
    }

    /// Deterministically iterates all candidate pairs and returns the first path found.
    private async Task<string> TryAutoPath()
    {
        var ann = Annotator();
        foreach (var p in _pairs)
        {
            var res = await _index.FindPath(p.fc, p.fm, p.tc, p.tm,
                                            withBodies: _withBodies, repoUrl: _repoUrl, annotate: ann);
            if (res.Contains("PATH FOUND"))
                return $"(find_path {p.fc}.{p.fm} -> {p.tc}.{p.tm})\n{res}";
        }
        // no direct path -> at least show who calls the target methods (callers going up)
        var sb = new StringBuilder("No direct path found. Callers of the target methods (going up):\n");
        foreach (var t in _pairs.Select(p => (p.tc, p.tm)).Distinct().Take(3))
        {
            sb.AppendLine($"\n# {t.tc}.{t.tm}");
            sb.AppendLine(await _index.FindCallers(t.tc, t.tm));
        }
        return sb.ToString();
    }

    /// Brute-force (--all-paths): iterates ALL candidate pairs and returns ALL distinct
    /// found paths. For non-trivial code where multiple paths exist and the first/shortest is not enough.
    private async Task<string> TryAllPaths()
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>();
        var ann = Annotator();
        int found = 0;
        foreach (var p in _pairs)
        {
            var res = await _index.FindPath(p.fc, p.fm, p.tc, p.tm, maxNodes: 20000,
                                            withBodies: _withBodies, repoUrl: _repoUrl, annotate: ann);
            if (!res.Contains("PATH FOUND")) continue;
            if (!seen.Add(res)) continue;       // dedup identical paths
            found++;
            if (found > 1) { sb.AppendLine(); sb.AppendLine("---"); }   // clear separator between paths
            sb.AppendLine();
            sb.AppendLine($"### Path {found}:  {p.fc}.{p.fm}  ->  {p.tc}.{p.tm}");
            sb.AppendLine(res.Trim());
        }
        if (found == 0)
        {
            var fb = new StringBuilder("No direct path found over candidate pairs. " +
                                       "Callers of the target methods (going up):\n");
            foreach (var t in _pairs.Select(p => (p.tc, p.tm)).Distinct().Take(3))
            {
                fb.AppendLine($"\n# {t.tc}.{t.tm}");
                fb.AppendLine(await _index.FindCallers(t.tc, t.tm));
            }
            return fb.ToString();
        }
        return $"FOUND {found} distinct path(s) [brute-force]:\n\n" + sb.ToString();
    }

    /// Builds the per-hop annotator (or null if --annotate is off). The callback gets the prior
    /// context (the whole chain + earlier steps/notes, so it understands the depth), the caller and
    /// callee signatures (with param names) and the caller's code up to the call. It returns a very
    /// short "why" note, or null when the step is trivial (then only the self-describing code shows).
    private Func<string, string, string, string, Task<string?>>? Annotator()
    {
        if (!_annotate) return null;
        return async (context, callerSig, calleeSig, code) =>
        {
            try
            {
                // Empty calleeSig => this is the target/destination node (end of the chain).
                var prompt = string.IsNullOrEmpty(calleeSig)
                    ? $"{context}\n\n" +
                      $"This is the FINAL method of the chain: `{callerSig}`.\n\n" +
                      $"```csharp\n{code}\n```\n\n" +
                      "In ONE short phrase (max ~14 words) say what this final method does / why the chain " +
                      "ends here. If trivial, reply with exactly: null"
                    : $"{context}\n\n" +
                      $"Current step: `{callerSig}` runs and, at the end of the snippet below, calls `{calleeSig}`.\n\n" +
                      $"```csharp\n{code}\n```\n\n" +
                      $"In ONE short phrase (max ~14 words) say WHY it calls `{calleeSig}` here / what this step " +
                      "achieves in the overall chain. Be proportional to the context. If it is a trivial or obvious " +
                      "delegation with nothing meaningful to add, reply with exactly: null";
                var reply = (await _llm.ChatAsync(new[]
                {
                    new ChatMsg("system", "You annotate one step of a code call-chain in a single terse phrase. No markdown, no quotes."),
                    new ChatMsg("user", prompt)
                }, new ChatOptions { Temperature = 0.2, NumPredict = 64 }, "annotate")).Trim();
                if (reply.Length == 0 || reply.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
                return reply.Trim('"', '`', ' ', '.');
            }
            catch { return null; }   // model down / error -> just omit the annotation
        };
    }

    // ---- dispatch ----------------------------------------------------------

    private async Task<string> Dispatch(string tool, JsonElement a)
    {
        string S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";
        int I(string k, int def) => a.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : def;

        return tool switch
        {
            "find_symbol"     => await _index.FindSymbol(S("name")),
            "outline"         => _index.Outline(S("file")),
            "get_method"      => await _index.GetMethod(S("class"), S("method")),
            "find_callers"    => await _index.FindCallers(S("class"), S("method")),
            "find_callees"    => await _index.FindCallees(S("class"), S("method")),
            "find_references" => await _index.FindReferences(S("class"), S("method")),
            "find_path"       => await _index.FindPath(S("fromClass"), S("fromMethod"), S("toClass"), S("toMethod")),
            "read_file"       => _index.ReadFile(S("file"), I("start", 1), I("end", 0)),
            "grep"            => _index.Grep(S("pattern")),
            _                 => $"unknown tool '{tool}'"
        };
    }
}
