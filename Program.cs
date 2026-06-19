using System.Diagnostics;
using CodeTracer;
using Microsoft.Build.Locator;

// IMPORTANT: MSBuildLocator must be registered before we touch
// any Roslyn/MSBuild type. That is why all the work is in Run*() methods,
// not inline here — so the JIT does not load those types prematurely.
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

// ---- trace: autonomous agent for tracing the call chain ----------------------

static async Task<int> RunTrace(Options opts)
{
    opts.Solution ??= Ask("Path to solution (.sln)");
    // --from/--to: direct method-to-method find_path. Otherwise the endpoint/target-file flow.
    bool direct = !string.IsNullOrWhiteSpace(opts.From) && !string.IsNullOrWhiteSpace(opts.To);
    if (!direct)
    {
        opts.TargetFile ??= Ask("Path to file of class A (where the call we look for lives)");
        opts.Endpoint   ??= Ask("Endpoint / starting point B (e.g. 'POST /orders' or 'OrdersController.Create')");
    }

    Console.Error.WriteLine($"[cfg] mode=trace  api={opts.Api}  style={opts.ApiStyle}  model={opts.Model}  " +
                            $"numCtx={opts.NumCtx}  maxSteps={opts.MaxSteps}");

    var sw = Stopwatch.StartNew();
    var index = new RoslynIndex();
    if (!await TryLoad(index, opts.Solution!)) return 1;

    var llm = new LlmClient(opts.Api, opts.Model, opts.ApiStyle, opts.NumCtx);
    await ReportVersion(llm);

    // Auto-save the result if --out wasn't given (default ON), so a trace is never lost for lack of a flag.
    bool autoOut = string.IsNullOrWhiteSpace(opts.Out);
    var traceLabel = direct ? $"{opts.From}-to-{opts.To}"
                            : $"{opts.Endpoint}-to-{Path.GetFileNameWithoutExtension(opts.TargetFile)}";
    var outPath = autoOut ? AutoOutPath("trace", traceLabel) : opts.Out!;
    if (autoOut)
        Console.Error.WriteLine($"[trace] auto-saving result to {outPath}  (pass --out <file> to choose the path)");

    var agent = new Agent(llm, index, opts.MaxSteps, opts.Summary, opts.UseLlm, opts.AllPaths,
                          opts.WithBodies || opts.Annotate, opts.Annotate, opts.RepoUrl, outPath);

    if (direct)
    {
        if (!TrySplit(opts.From!, out var fc, out var fm) || !TrySplit(opts.To!, out var tc, out var tm))
        {
            Console.Error.WriteLine("[error] --from / --to expect the format \"Class.Method\".");
            return 1;
        }
        await agent.RunDirectAsync(fc, fm, tc, tm);
    }
    else
    {
        await agent.RunAsync(opts.Solution!, opts.TargetFile!, opts.Endpoint!);
    }

    ReportPerf(llm, sw);
    return 0;
}

// A discoverable default output path, used when --out is not given so a run is never lost.
// e.g. ("explain", "Agent.GetAction") -> "codetracer-explain-Agent.GetAction.md" in the current dir.
static string AutoOutPath(string kind, string? label)
{
    var safe = new string((label ?? "out")
        .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
    if (string.IsNullOrWhiteSpace(safe)) safe = "out";
    if (safe.Length > 80) safe = safe[..80];
    return $"codetracer-{kind}-{safe}.md";
}

// "Namespace.Class.Method" / "Class.Method" -> (Class, Method) using the last two segments.
static bool TrySplit(string classDotMethod, out string cls, out string method)
{
    cls = method = "";
    var parts = classDotMethod.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length < 2) return false;
    method = parts[^1];
    cls = parts[^2];
    return true;
}

// ---- explain: step-by-step explanation of a single method -------------------

static async Task<int> RunExplain(Options opts)
{
    opts.Solution ??= Ask("Path to solution (.sln)");
    if (string.IsNullOrWhiteSpace(opts.Method) && string.IsNullOrWhiteSpace(opts.File))
        opts.Method = Ask("Method (Class.Method)");

    Console.Error.WriteLine($"[cfg] mode=explain  api={opts.Api}  style={opts.ApiStyle}  model={opts.Model}  " +
                            $"numCtx={opts.NumCtx}  numPredict={opts.NumPredict}");

    var sw = Stopwatch.StartNew();
    var index = new RoslynIndex();
    if (!await TryLoad(index, opts.Solution!)) return 1;

    var llm = new LlmClient(opts.Api, opts.Model, opts.ApiStyle, opts.NumCtx);
    await ReportVersion(llm);

    // distinguish target: --method "Class.Method" or --file + --line
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

    // build the call chain (depth 0 = the method alone) — used both for the model and for the --no-llm dump
    List<(int level, MethodContext ctx)>? chain;
    if (opts.Depth <= 0)
    {
        var where = cls != null ? $"{cls}.{method}" : $"{opts.File}:{opts.Line}";
        Console.Error.WriteLine($"[explain] looking for {where} (depth=0) ...");
        var ctx = cls != null
            ? await index.BuildMethodContext(cls, method!, 0)
            : await index.BuildMethodContextByLocation(opts.File!, opts.Line, 0);
        chain = ctx == null ? null : new List<(int, MethodContext)> { (0, ctx) };
    }
    else
    {
        Console.Error.WriteLine($"[explain] building call chain (depth={opts.Depth}, max-methods={opts.MaxMethods}) ...");
        chain = cls != null
            ? await index.BuildExplainChain(cls, method!, opts.Depth, opts.MaxMethods)
            : await index.BuildExplainChainByLocation(opts.File!, opts.Line, opts.Depth, opts.MaxMethods);

        // property fallback: a property is not a call-chain root - explain it as a single node.
        if ((chain == null || chain.Count == 0) && cls != null)
        {
            var ctx = await index.BuildMethodContext(cls, method!, opts.Depth);
            if (ctx != null) chain = new List<(int, MethodContext)> { (0, ctx) };
        }
    }

    if (chain == null || chain.Count == 0)
    {
        Console.Error.WriteLine("[error] method not found or has no source available (possibly from metadata).");
        return 1;
    }

    // Where to save. If the user didn't pass --out, auto-pick a discoverable path so a long run is
    // NEVER lost just because a flag was forgotten — partial results are flushed as it goes (default ON).
    bool autoOut = string.IsNullOrWhiteSpace(opts.Out);
    var label = cls != null ? $"{cls}.{method}" : $"{Path.GetFileNameWithoutExtension(opts.File)}-L{opts.Line}";
    var outPath = autoOut ? AutoOutPath("explain", label) : opts.Out!;

    string text;
    if (!opts.UseLlm)
    {
        // --no-llm: do NOT explain locally — just emit the Roslyn-extracted context (for a bigger model).
        Console.Error.WriteLine($"[explain] --no-llm: dumping Roslyn context for {chain.Count} method(s), no model call.");
        text = Explainer.RenderContext(chain, opts.RepoUrl, opts.Peek);
    }
    else
    {
        // Ctrl+C = "I'm out of time — give me what you have": cancel the in-flight call and keep the
        // partial file (flushed after every method). Open it read-only to watch the knowledge fill in.
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;          // don't hard-kill — let us stop cleanly and keep the partial file
            cts.Cancel();
            Console.Error.WriteLine("\n[cancel] stopping — your partial result is already saved ...");
        };
        Console.CancelKeyPress += onCancel;
        if (autoOut)
            Console.Error.WriteLine($"[explain] auto-saving to {outPath}  (open it read-only to watch progress; " +
                                    "Ctrl+C stops and keeps what's done)");
        try
        {
            var explainer = new Explainer(llm, opts.NumPredict, opts.Temperature ?? 0.2, opts.RepoUrl, opts.ShowCode, ct: cts.Token);
            text = opts.Depth <= 0
                ? await explainer.ExplainAsync(chain[0].ctx, opts.Goal, opts.Question, outPath)
                : await explainer.ExplainChainAsync(chain, opts.Goal, opts.Question, outPath);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"[explain] stopped early — partial result saved to {outPath} " +
                                    "(every method that finished before you stopped is in it).");
            ReportPerf(llm, sw);
            return 0;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    Console.WriteLine();
    Console.WriteLine(text.Trim());

    try
    {
        await File.WriteAllTextAsync(outPath, text);
        Console.Error.WriteLine(autoOut
            ? $"[explain] saved to {outPath}  (pass --out <file> to choose the path)"
            : $"[explain] saved to {outPath}");
    }
    catch (Exception ex) { Console.Error.WriteLine($"[write error] {ex.Message}"); }

    ReportPerf(llm, sw);
    return 0;
}

// ---- shared helpers ----------------------------------------------------------

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

static void ReportPerf(LlmClient llm, Stopwatch sw)
{
    sw.Stop();
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var t = sw.Elapsed.TotalSeconds.ToString("F1", inv);
    if (llm.Calls == 0)
    {
        Console.Error.WriteLine($"[perf] {t}s · 0 model calls (deterministic)");
        return;
    }
    Console.Error.WriteLine($"[perf] {t}s total · {llm.Calls} model call(s) · " +
                            $"in {llm.PromptTokens} / out {llm.EvalTokens} tokens ({llm.Model})");
    int n = 1;
    foreach (var c in llm.CallLog)
    {
        var lbl = string.IsNullOrEmpty(c.Label) ? "" : $" [{c.Label}]";
        Console.Error.WriteLine($"[perf]   call {n++}{lbl}: {c.Seconds.ToString("F1", inv)}s · in {c.In} / out {c.Out}");
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
            case "--from":                    o.From = Next(); break;
            case "--to":                      o.To = Next(); break;
            case "--method":                  o.Method = Next(); break;
            case "--file":                    o.File = Next(); break;
            case "--line":                    if (int.TryParse(Next(), out var ln)) o.Line = ln; break;
            case "--depth":                   if (int.TryParse(Next(), out var dp)) o.Depth = dp; break;
            case "--max-methods":             if (int.TryParse(Next(), out var mm)) o.MaxMethods = mm; break;
            case "--question": case "--ask":  o.Question = Next(); break;
            case "--goal":                    o.Goal = Next(); break;
            case "--out":                     o.Out = Next(); break;
            case "--repo-url":                o.RepoUrl = Next(); break;
            case "--peek":                    if (int.TryParse(Next(), out var pk)) o.Peek = pk; break;
            case "--no-code":                 o.ShowCode = false; break;
            case "--with-code":               o.ShowCode = true; break;
            case "--no-llm":                  o.UseLlm = false; break;
            case "--all-paths": case "--brute": case "--deep":  o.AllPaths = true; break;
            case "--with-bodies": case "--code":  o.WithBodies = true; break;
            case "--annotate": case "--why":      o.Annotate = true; break;
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
      --from "C.M"    direct mode: find the path FROM this method ...
      --to "C.M"      ... TO this method (skips -f/-e; "how do I get from C.M to C2.M2")
      --all-paths     (--brute / --deep) enumerate ALL distinct paths, not just the first. With
                      --from/--to it lists every path between the two methods - e.g. one per
                      interface implementation that reaches the target (DI/decorator chains).
      --with-bodies   (--code) between hops, show each method's code from its start down to
                      the call to the next hop. With --repo-url, locations become repo links.
      --annotate      (--why) add a short LLM note per hop explaining WHY it calls the next
                      method (or nothing if trivial). Sees the prior chain for depth. Implies
                      --with-bodies; needs the model (gemma4) reachable.
      --repo-url URL  render file locations as clickable links to the repo, e.g.
                      https://github.com/you/repo/blob/main  (path relative to the .sln dir)
      --no-llm        deterministic only: run find_path over candidate pairs, no model
                      (fastest + fully reliable when the endpoint resolves to a file)
      --max-steps     agent step limit (default 25)
      --summary       append an LLM SUMMARY section at the end (purpose, dependencies,
                      "good to know") - included in the output / saved file. Needs the model.
      --out           save the result (the path / all paths) to a file

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
      --no-code       prose only: DON'T show each method's source under its heading. By default
                      the real code IS shown (indented by call-depth, so the nesting is visible).
      --no-llm        DON'T explain locally - just dump the Roslyn-extracted context (method +
                      call-chain source) to feed into a bigger model yourself.
      --peek N        in the --no-llm dump, show only the first N lines of each method body
                      (a peek) instead of the full source. Use with --repo-url for the full file.
      --repo-url URL  render file locations as clickable links to the repo, e.g.
                      https://github.com/you/repo/blob/main  (path is relative to the .sln dir)
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

  # fastest single deterministic path (no model round-trips), saved to a file
  codetracer trace -s ./Big.sln -f ./Pricing/PricingEngine.cs -e "POST /orders" --no-llm --out path.md

  # NO local model: dump the method + call-chain (peek + clickable repo links) to a file
  codetracer explain -s ./Big.sln --method "TaxEngine.Calculate" --depth 3 --no-llm \
             --peek 15 --repo-url https://github.com/you/repo/blob/main --out context.md

  # save the local explanation to .md
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
    public string? From;           // --from "Class.Method": direct find_path source
    public string? To;             // --to   "Class.Method": direct find_path target
    public int MaxSteps = 25;
    public bool Summary = false;
    public bool UseLlm = true;     // --no-llm: purely deterministic find_path, no model
    public bool AllPaths = false;  // --all-paths/--brute: enumerate ALL paths, not just the first
    public bool WithBodies = false;// --with-bodies/--code: between hops inserts the method body up to the call site
    public bool Annotate = false;  // --annotate/--why: short LLM "why" note per hop (implies --with-bodies)

    // explain
    public string? Method;
    public string? File;
    public int Line = 0;
    public int Depth = 1;          // call chain depth for deep explain (0 = the method alone)
    public int MaxMethods = 8;     // cap on the number of methods in the deep-explain chain
    public string? Question;       // --question/--ask: a specific question to focus the explanation on
    public string? Goal;
    public string? Out;
    public string? RepoUrl;        // --repo-url: base URL for clickable links (e.g. .../blob/main)
    public int Peek = 0;           // --peek N: in the --no-llm dump show only the first N lines of each method body
    public bool ShowCode = true;   // --no-code: explain WITHOUT the source snippet under each method (prose only)

    // shared
    public string Model = "gemma4:latest";
    public string Api = "http://localhost:11434";
    public ApiStyle ApiStyle = ApiStyle.Ollama;
    public int NumCtx = 16384;
    public int NumPredict = 4096;
    public double? Temperature = null;
}
