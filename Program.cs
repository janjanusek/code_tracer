using CodeTracer;
using Microsoft.Build.Locator;

// DOLEZITE: MSBuildLocator musi byt zaregistrovany skor nez sa dotkneme
// akehokolvek Roslyn/MSBuild typu. Preto je vsetka praca v Run*() metodach,
// nie inline tu - aby JIT nenacital tie typy predcasne.
if (!MSBuildLocator.IsRegistered)
    MSBuildLocator.RegisterDefaults();

return await RunApp(args);

static async Task<int> RunApp(string[] args)
{
    var opts = ParseArgs(args);
    return opts.Command switch
    {
        "explain" => await RunExplain(opts),
        _         => await RunTrace(opts),
    };
}

// ---- trace: autonomny agent na dohladanie call-chain -------------------------

static async Task<int> RunTrace(Options opts)
{
    opts.Solution   ??= Ask("Path to solution (.sln)");
    opts.TargetFile ??= Ask("Path to file of class A (where the call we look for lives)");
    opts.Endpoint   ??= Ask("Endpoint / starting point B (e.g. 'POST /orders' or 'OrdersController.Create')");

    Console.Error.WriteLine($"[cfg] mode=trace  api={opts.Api}  style={opts.ApiStyle}  model={opts.Model}  " +
                            $"numCtx={opts.NumCtx}  maxSteps={opts.MaxSteps}");

    var index = new RoslynIndex();
    if (!await TryLoad(index, opts.Solution!)) return 1;

    var llm = new LlmClient(opts.Api, opts.Model, opts.ApiStyle, opts.NumCtx);
    await ReportVersion(llm);

    var agent = new Agent(llm, index, opts.MaxSteps, opts.Summary, opts.UseLlm, opts.AllPaths);
    await agent.RunAsync(opts.Solution!, opts.TargetFile!, opts.Endpoint!);
    return 0;
}

// ---- explain: krok-za-krokom vysvetlenie jednej metody -----------------------

static async Task<int> RunExplain(Options opts)
{
    opts.Solution ??= Ask("Path to solution (.sln)");
    if (string.IsNullOrWhiteSpace(opts.Method) && string.IsNullOrWhiteSpace(opts.File))
        opts.Method = Ask("Method (Class.Method)");

    Console.Error.WriteLine($"[cfg] mode=explain  api={opts.Api}  style={opts.ApiStyle}  model={opts.Model}  " +
                            $"numCtx={opts.NumCtx}  numPredict={opts.NumPredict}");

    var index = new RoslynIndex();
    if (!await TryLoad(index, opts.Solution!)) return 1;

    var llm = new LlmClient(opts.Api, opts.Model, opts.ApiStyle, opts.NumCtx);
    await ReportVersion(llm);

    // rozlis ciel: --method "Class.Method" alebo --file + --line
    string? cls = null, method = null;
    if (!string.IsNullOrWhiteSpace(opts.Method))
    {
        var parts = opts.Method!.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            Console.Error.WriteLine("[error] --method expects the format \"Class.Method\".");
            return 1;
        }
        method = parts[^1];
        cls = parts[^2];
    }
    else if (opts.Line <= 0)
    {
        Console.Error.WriteLine("[error] with --file you must also pass --line <N>.");
        return 1;
    }

    var explainer = new Explainer(llm, opts.NumPredict, opts.Temperature ?? 0.2);
    string text;

    if (opts.Depth <= 0)
    {
        // jedna metoda - najrychlejsie (jeden call)
        var where = cls != null ? $"{cls}.{method}" : $"{opts.File}:{opts.Line}";
        Console.Error.WriteLine($"[explain] looking for {where} (depth=0) ...");
        var ctx = cls != null
            ? await index.BuildMethodContext(cls, method!, 0)
            : await index.BuildMethodContextByLocation(opts.File!, opts.Line, 0);
        if (ctx == null)
        {
            Console.Error.WriteLine("[error] method not found or has no source available (possibly from metadata).");
            return 1;
        }
        Console.Error.WriteLine($"[explain] {ctx.Display}  {ctx.Location}  (~{ctx.BodyLineCount} body lines, " +
                                $"{ctx.Callees.Count} callees)");
        text = await explainer.ExplainAsync(ctx, opts.Goal, opts.Question);
    }
    else
    {
        // deep: vysvetli CELU logiku po call-chain (kazda metoda samostatne + synteza)
        Console.Error.WriteLine($"[explain] building call chain (depth={opts.Depth}, max-methods={opts.MaxMethods}) ...");
        var chain = cls != null
            ? await index.BuildExplainChain(cls, method!, opts.Depth, opts.MaxMethods)
            : await index.BuildExplainChainByLocation(opts.File!, opts.Line, opts.Depth, opts.MaxMethods);
        if (chain == null || chain.Count == 0)
        {
            Console.Error.WriteLine("[error] method not found or has no source available (possibly from metadata).");
            return 1;
        }
        Console.Error.WriteLine($"[explain] chain has {chain.Count} methods - explaining each + end-to-end synthesis " +
                                $"({chain.Count + 1} model calls) ...");
        text = await explainer.ExplainChainAsync(chain, opts.Goal, opts.Question);
    }

    Console.WriteLine();
    Console.WriteLine(text.Trim());

    if (!string.IsNullOrWhiteSpace(opts.Out))
    {
        try
        {
            await File.WriteAllTextAsync(opts.Out!, text);
            Console.Error.WriteLine($"[explain] saved to {opts.Out}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[write error] {ex.Message}"); }
    }
    return 0;
}

// ---- spolocne pomocky --------------------------------------------------------

static async Task<bool> TryLoad(RoslynIndex index, string solution)
{
    try { await index.LoadAsync(solution); return true; }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[error] failed to load solution: {ex.Message}");
        Console.Error.WriteLine("Tip: run 'dotnet restore' and 'dotnet build' on the target solution first, " +
                                "so MSBuild can open it.");
        return false;
    }
}

static async Task ReportVersion(LlmClient llm)
{
    var v = await llm.TryGetVersionAsync();
    if (v != null) Console.Error.WriteLine($"[cfg] Ollama version: {v}");
    else if (llm.Style == ApiStyle.Ollama)
        Console.Error.WriteLine("[warning] could not verify the Ollama version. Structured outputs " +
                                "require Ollama >= 0.5 (check: 'ollama --version').");
}

static string Ask(string prompt)
{
    Console.Write($"{prompt}: ");
    return Console.ReadLine()?.Trim() ?? "";
}

static Options ParseArgs(string[] args)
{
    var o = new Options();
    int start = 0;
    if (args.Length > 0 && (args[0] == "explain" || args[0] == "trace"))
    {
        o.Command = args[0];
        start = 1;
    }

    for (int i = start; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : "";
        switch (args[i])
        {
            case "-s": case "--solution":     o.Solution = Next(); break;
            case "-f": case "--target-file":  o.TargetFile = Next(); break;
            case "-e": case "--endpoint":     o.Endpoint = Next(); break;
            case "--method":                  o.Method = Next(); break;
            case "--file":                    o.File = Next(); break;
            case "--line":                    if (int.TryParse(Next(), out var ln)) o.Line = ln; break;
            case "--depth":                   if (int.TryParse(Next(), out var dp)) o.Depth = dp; break;
            case "--max-methods":             if (int.TryParse(Next(), out var mm)) o.MaxMethods = mm; break;
            case "--question": case "--ask":  o.Question = Next(); break;
            case "--goal":                    o.Goal = Next(); break;
            case "--out":                     o.Out = Next(); break;
            case "--no-llm":                  o.UseLlm = false; break;
            case "--all-paths": case "--brute": case "--deep":  o.AllPaths = true; break;
            case "-m": case "--model":        o.Model = Next(); break;
            case "-a": case "--api":          o.Api = Next(); break;
            case "--api-style":               o.ApiStyle = Next().Equals("openai", StringComparison.OrdinalIgnoreCase)
                                                  ? ApiStyle.OpenAI : ApiStyle.Ollama; break;
            case "--num-ctx":                 if (int.TryParse(Next(), out var nc)) o.NumCtx = nc; break;
            case "--num-predict":             if (int.TryParse(Next(), out var npr)) o.NumPredict = npr; break;
            case "--temperature":             if (double.TryParse(Next(), System.Globalization.CultureInfo.InvariantCulture, out var t)) o.Temperature = t; break;
            case "--max-steps":               if (int.TryParse(Next(), out var ms)) o.MaxSteps = ms; break;
            case "--summary":                 o.Summary = true; break;
            case "-h": case "--help":         PrintHelp(); Environment.Exit(0); break;
        }
    }
    return o;
}

static void PrintHelp()
{
    Console.WriteLine("""
CodeTracer - C# tool on top of Roslyn + a local model (Ollama / LM Studio).

COMMANDS:
  trace    (default) autonomous agent finds the call chain from an endpoint to a target class
  explain            step-by-step explanation of a specific method (+ optional change proposal)

USAGE:
  codetracer trace   -s <sln> -f <target.cs> -e "<endpoint>" [options]
  codetracer explain -s <sln> --method "Class.Method" [--goal "..."] [options]
  codetracer explain -s <sln> --file <path.cs> --line <N>  [--goal "..."] [options]

TRACE options:
  -s, --solution      path to the .sln
  -f, --target-file   path to the file of class A (where the call we look for lives)
  -e, --endpoint      starting point B (route or Class.Method)
      --all-paths     (--brute / --deep) enumerate ALL distinct paths, not just the first.
                      For non-trivial code where one shortest path isn't enough.
      --no-llm        deterministic only: run find_path over candidate pairs, no model
                      (fastest + fully reliable when the endpoint resolves to a file)
      --max-steps     agent step limit (default 25)
      --summary       append a short free-text summary of the path at the end

EXPLAIN options:
      --method        "Class.Method" - which method to explain
      --file + --line alternative: file and a line inside the method
      --depth         how deep to follow the CALL CHAIN (default 1; 0 = the method alone).
                      Each method in the chain is explained on its own, then an end-to-end
                      synthesis ties it together. Higher = deeper logic, more model calls.
      --max-methods   cap on methods explained in the chain (default 8). RAISE THIS to go
                      wider/deeper on non-trivial code (each method = one model call).
      --question      (--ask) a specific question to focus the explanation on
      --goal          (optional) "what to change" - adds a change proposal with a code sample.
                      Not needed for plain code understanding.
      --out           save the result to a .md file

SHARED options:
  -m, --model         model name (default: gemma4:latest)
  -a, --api           server base URL (default: http://localhost:11434)
                      LM Studio: http://localhost:1234  (add --api-style openai)
      --api-style     ollama (default) | openai
      --num-ctx       context window size (default 16384; with 32 GB RAM and 7B, 8-16K is safe)
      --num-predict   token cap for explain output (default 4096)
      --temperature   default 0 (decisions) / 0.2 (explain)

EXAMPLES:
  # explain the WHOLE logic deeply - follow the call chain down and explain every layer
  codetracer explain -s ./Big.sln --method "TaxEngine.Calculate" --depth 3 --max-methods 15

  # point at code and ask a specific question
  codetracer explain -s ./Big.sln --method "TaxEngine.Calculate" --ask "where does the VAT rate come from?"

  # brute-force trace: ALL paths from an endpoint to the target class (non-trivial code)
  codetracer trace -s ./Big.sln -f ./Pricing/PricingEngine.cs -e "POST /orders" --all-paths --no-llm

  # fastest single deterministic path (no model round-trips)
  codetracer trace -s ./Big.sln -f ./Pricing/PricingEngine.cs -e "POST /orders" --no-llm

  # save the explanation to .md
  codetracer explain -s ./Big.sln --method "TaxEngine.Calculate" --depth 3 --out tax.md
""");
}

class Options
{
    public string Command = "trace";
    public string? Solution;

    // trace
    public string? TargetFile;
    public string? Endpoint;
    public int MaxSteps = 25;
    public bool Summary = false;
    public bool UseLlm = true;     // --no-llm: cisto deterministicky find_path, bez modelu
    public bool AllPaths = false;  // --all-paths/--brute: enumeruj VSETKY cesty, nie len prvu

    // explain
    public string? Method;
    public string? File;
    public int Line = 0;
    public int Depth = 1;          // hlbka call-chain pre deep explain (0 = len metoda)
    public int MaxMethods = 8;     // strop poctu metod v deep-explain retazci
    public string? Question;       // --question/--ask: konkretna otazka, na ktoru sa ma sustredit
    public string? Goal;
    public string? Out;

    // spolocne
    public string Model = "gemma4:latest";
    public string Api = "http://localhost:11434";
    public ApiStyle ApiStyle = ApiStyle.Ollama;
    public int NumCtx = 16384;
    public int NumPredict = 4096;
    public double? Temperature = null;
}
