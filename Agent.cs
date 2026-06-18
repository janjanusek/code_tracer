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
    private readonly int _actionNumPredict;

    // Deterministicky predpripravene kandidatske dvojice pre find_path.
    private readonly List<(string fc, string fm, string tc, string tm)> _pairs = new();

    // Posledny uspesny find_path - z neho sa pri `finish` zostavi finalna cesta (deterministicky).
    private string? _lastPath;

    public Agent(LlmClient llm, RoslynIndex index, int maxSteps = 25,
                 bool summarize = false, bool useLlm = true, bool allPaths = false,
                 int actionNumPredict = 512)
    {
        _llm = llm;
        _index = index;
        _maxSteps = maxSteps;
        _summarize = summarize;
        _useLlm = useLlm;
        _allPaths = allPaths;
        _actionNumPredict = actionNumPredict;
    }

    // Tools, ktore model smie zvolit. find_references je dispatchovatelny ale zamerne
    // NIE je v schema enume (drzime mnozinu malu a plochu kvoli gramatike).
    private static readonly string[] AllowedTools =
        { "find_path", "find_callers", "find_callees", "get_method",
          "find_symbol", "outline", "read_file", "grep", "finish" };

    // Plocha JSON schema akcie: jeden `tool` enum + jeden plochy `args` objekt so
    // vsetkymi moznymi volitelnymi polami. Ziadne anyOf/oneOf, ziadne volnotextove
    // polia (`thought` zamerne chyba - vo vnutri grammar-constrained JSON spustaju
    // u malych modelov repetition-loop).
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

        // Deterministicky pre-flight: skus kandidatske find_path dvojice HNED. Na CPU je to
        // rychlejsie a spolahlivejsie nez cakat na (casto podvyplnene) volania modelu. Roslyn
        // je zdroj pravdy; model je tu len na navigaciu tazsich pripadov (interface/DI/eventy).
        // --all-paths/--brute: enumeruj VSETKY cesty (deep), nie len prvu najkratsiu.
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
                // model nedokazal dat platnu akciu ani po opravach -> deterministicka eskalacia
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

            // --- detekcia zacyklenia -------------------------------------------------
            var key = $"{tool}|{Canonical(args)}";
            if (!seen.Add(key))
            {
                escalations++;
                Console.WriteLine($"[!] repeated step ({escalations}) - escalating");

                if (escalations == 1)
                {
                    // este jedna sanca: explicitne mu nadiktujem find_path volania
                    messages.Add(new("assistant", raw));
                    messages.Add(new("user",
                        "STOP. You already ran this exact tool+args. Do NOT repeat it.\n" +
                        "Call find_path now (return the JSON), e.g.:\n" + SuggestPairs()));
                    continue;
                }

                // model je zacykleny -> pouzijem deterministicky vysledok (do 2 krokov)
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

        // limit krokov vycerpany -> pouzi najlepsi dostupny vysledok
        Console.WriteLine($"\n[!] step limit {_maxSteps} reached - using deterministic result");
        await Finish(_lastPath ?? deterministic, "limit");
    }

    // ---- vyber akcie: token-level vynuteny JSON + validacia + retry ---------

    /// Pozaduje od modelu jednu akciu ako grammar-constrained JSON. Gramatika garantuje
    /// STRUKTURU; tu validujeme HODNOTY (tool v enume, args davaju zmysel pre dany tool).
    /// Pri zlej hodnote posle correction turn a skusi znova (max 2x), inak vrati null.
    private async Task<(string tool, JsonElement args, string raw)?> GetAction(List<ChatMsg> messages)
    {
        var opts = new ChatOptions { Temperature = 0, NumPredict = _actionNumPredict, Format = ActionSchema };

        for (int attempt = 0; attempt < 3; attempt++)   // 1 pokus + 2 opravy
        {
            var raw = (await _llm.ChatAsync(messages, opts)).Trim();

            // gramatika by mala garantovat platny JSON; pri preruseni (num_predict) sa
            // moze vratit neuzavrety - osetrime.
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

    /// Vrati null ak su args ok, inak popis chybajucich poli pre dany tool.
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

    /// Kanonicka (stabilna) reprezentacia args pre detekciu opakovania.
    private static string Canonical(JsonElement args) => JsonSerializer.Serialize(args).ToLowerInvariant();

    // ---- finalizacia -------------------------------------------------------

    /// Vypise finalnu cestu (zostavenu deterministicky) a volitelne neobmedzene zhrnutie.
    private async Task Finish(string pathText, string reason)
    {
        Console.WriteLine($"\n========== DONE ({reason}) ==========");
        Console.WriteLine(pathText.Trim());

        if (!_summarize) return;

        // Volnotextove zhrnutie ako samostatny NEOBMEDZENY call (bez format).
        try
        {
            var summary = await _llm.ChatAsync(new[]
            {
                new ChatMsg("system", "You are a senior C# developer. Summarize the discovered call chain in 2-3 plain-English sentences."),
                new ChatMsg("user", "Discovered path:\n" + pathText)
            }, new ChatOptions { Temperature = 0.2, NumPredict = 256 });

            if (!string.IsNullOrWhiteSpace(summary))
            {
                Console.WriteLine("\n--- SUMMARY ---");
                Console.WriteLine(summary.Trim());
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[summary failed] {ex.Message}"); }
    }

    // ---- deterministicky bootstrap -----------------------------------------

    private string Bootstrap(string targetFile, string endpoint)
    {
        // endpoint: ak je to .cshtml, handler je v .cshtml.cs
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

        // vyber kandidatov: handlery (On*) ako zdroj, vsetky cielove metody ako ciel
        var handlers = fromMethods.Where(m => m.method.StartsWith("On", StringComparison.Ordinal)).ToList();
        if (handlers.Count == 0) handlers = fromMethods;                 // fallback: vsetky
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

    /// Deterministicky preide vsetky kandidatske dvojice a vrati prvu najdenu cestu.
    private async Task<string> TryAutoPath()
    {
        foreach (var p in _pairs)
        {
            var res = await _index.FindPath(p.fc, p.fm, p.tc, p.tm);
            if (res.Contains("PATH FOUND"))
                return $"(find_path {p.fc}.{p.fm} -> {p.tc}.{p.tm})\n{res}";
        }
        // ziadna priama cesta -> aspon ukaz kto vola cielove metody (callers nahor)
        var sb = new StringBuilder("No direct path found. Callers of the target methods (going up):\n");
        foreach (var t in _pairs.Select(p => (p.tc, p.tm)).Distinct().Take(3))
        {
            sb.AppendLine($"\n# {t.tc}.{t.tm}");
            sb.AppendLine(await _index.FindCallers(t.tc, t.tm));
        }
        return sb.ToString();
    }

    /// Brute-force (--all-paths): prejde VSETKY kandidatske dvojice a vrati VSETKY distinct
    /// najdene cesty. Pre netrivialny kod, kde existuje viac ciest a prva/najkratsia nestaci.
    private async Task<string> TryAllPaths()
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>();
        int found = 0;
        foreach (var p in _pairs)
        {
            var res = await _index.FindPath(p.fc, p.fm, p.tc, p.tm, maxNodes: 20000);
            if (!res.Contains("PATH FOUND")) continue;
            if (!seen.Add(res)) continue;       // dedup identicke cesty
            found++;
            sb.AppendLine($"### Path {found}:  {p.fc}.{p.fm}  ->  {p.tc}.{p.tm}");
            sb.AppendLine(res.Trim());
            sb.AppendLine();
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
