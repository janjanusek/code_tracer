using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CodeTracer;

public record ChatMsg(string Role, string Content);

/// <summary>Which API family to use for chat calls.</summary>
public enum ApiStyle
{
    /// Native Ollama endpoint /api/chat - supports `format` (JSON schema object)
    /// and `options` (num_ctx, num_predict, repeat_penalty). Default.
    Ollama,
    /// OpenAI-compatible /v1/chat/completions (e.g. LM Studio). Structured output
    /// goes via `response_format` = json_schema; `num_ctx` cannot be set here.
    OpenAI
}

/// <summary>Per-call settings. Defaults are CPU-friendly and deterministic.</summary>
public sealed class ChatOptions
{
    /// 0 = greedy/deterministic. Keep at 0 for decision-making and structured output.
    public double Temperature { get; init; } = 0.0;
    /// Context window size. null => the client default is used (--num-ctx).
    public int? NumCtx { get; init; }
    /// Cap on the number of generated tokens (Ollama num_predict / OpenAI max_tokens).
    public int? NumPredict { get; init; }
    /// Repetition penalty - gently suppresses repetition loops in smaller models.
    public double RepeatPenalty { get; init; } = 1.1;
    /// JSON schema (as a JsonElement). When provided, the output is grammar-constrained
    /// to structurally valid JSON. null => unconstrained text.
    public JsonElement? Format { get; init; }
    /// For reasoning models (Ollama): false disables "thinking" so the whole token budget
    /// goes to the actual answer (e.g. for short direct outputs). null => server default.
    public bool? Think { get; init; }
}

/// <summary>
/// Client for a local LLM server. Default is the native Ollama /api/chat, which
/// supports token-level structured outputs (`format` = JSON schema) - the server
/// builds a grammar from it and masks invalid tokens during sampling, so the
/// output is guaranteed to be valid JSON. OpenAI-compatible mode (LM Studio)
/// achieves the same via `response_format`.
/// </summary>
public class LlmClient
{
    private readonly HttpClient _http;
    private readonly string _root;       // e.g. http://localhost:11434  (no /v1, no trailing /)
    private readonly string _model;
    private readonly ApiStyle _style;
    private readonly int _defaultNumCtx;

    public LlmClient(string api, string model, ApiStyle style = ApiStyle.Ollama, int defaultNumCtx = 16384)
    {
        _root = NormalizeRoot(api);
        _model = model;
        _style = style;
        _defaultNumCtx = defaultNumCtx;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    }

    public ApiStyle Style => _style;
    public string Model => _model;

    // Cumulative usage across the whole run (for the [perf] line).
    public int Calls { get; private set; }
    public long PromptTokens { get; private set; }
    public long EvalTokens { get; private set; }
    public long TotalTokens => PromptTokens + EvalTokens;

    // Per-call breakdown: wall-clock seconds, input (prompt) tokens, output (generated) tokens.
    public readonly record struct CallStat(double Seconds, long In, long Out, string Label);
    private readonly List<CallStat> _callLog = new();
    public IReadOnlyList<CallStat> CallLog => _callLog;

    /// Accepts anything (http://host:port, .../v1, with/without trailing slash) and returns a clean root.
    private static string NormalizeRoot(string api)
    {
        var r = api.Trim().TrimEnd('/');
        if (r.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            r = r[..^3].TrimEnd('/');
        return r;
    }

    public async Task<string> ChatAsync(IEnumerable<ChatMsg> messages, ChatOptions? options = null,
                                        string label = "", CancellationToken ct = default)
    {
        options ??= new ChatOptions();
        var (url, json) = _style == ApiStyle.Ollama
            ? BuildOllama(messages, options)
            : BuildOpenAI(messages, options);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"LLM HTTP {(int)resp.StatusCode} ({url}): {Truncate(body, 800)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // Ollama native: { "message": { "content": "..." } }
        // OpenAI:        { "choices": [ { "message": { "content": "..." } } ] }
        string? content = _style == ApiStyle.Ollama
            ? root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c1) ? c1.GetString() : null
            : root.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var m2) && m2.TryGetProperty("content", out var c2) ? c2.GetString() : null;

        // token usage for this call (Ollama native: *_eval_count; OpenAI: usage.*_tokens)
        long inTok = 0, outTok = 0;
        if (_style == ApiStyle.Ollama)
        {
            if (root.TryGetProperty("prompt_eval_count", out var pe) && pe.TryGetInt64(out var pv)) inTok = pv;
            if (root.TryGetProperty("eval_count", out var ec) && ec.TryGetInt64(out var ev)) outTok = ev;
        }
        else if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var pv)) inTok = pv;
            if (u.TryGetProperty("completion_tokens", out var cc) && cc.TryGetInt64(out var cv)) outTok = cv;
        }
        Calls++;
        PromptTokens += inTok;
        EvalTokens += outTok;
        _callLog.Add(new CallStat(sw.Elapsed.TotalSeconds, inTok, outTok, label));

        return Sanitize(content ?? "");
    }

    /// Some SentencePiece-tokenizer models (notably small Gemma variants) occasionally LEAK the raw
    /// metaspace marker '▁' (U+2581) into their output where a normal space belongs - e.g. rendering
    /// "Detection:▁▁It iterates" instead of "Detection: It iterates". It's never a legitimate output
    /// character, so map it back to a space. Guarded so the common (clean) case allocates nothing.
    private static string Sanitize(string s) => s.IndexOf('▁') < 0 ? s : s.Replace('▁', ' ');

    private (string url, string json) BuildOllama(IEnumerable<ChatMsg> messages, ChatOptions o)
    {
        var opts = new Dictionary<string, object?>
        {
            ["temperature"] = o.Temperature,
            ["num_ctx"] = o.NumCtx ?? _defaultNumCtx,
            ["repeat_penalty"] = o.RepeatPenalty,
        };
        if (o.NumPredict is int np) opts["num_predict"] = np;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["stream"] = false,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["options"] = opts,
        };
        if (o.Format is JsonElement fmt) payload["format"] = fmt;
        if (o.Think is bool think) payload["think"] = think;   // reasoning models: false = direct answer

        return ($"{_root}/api/chat", JsonSerializer.Serialize(payload));
    }

    private (string url, string json) BuildOpenAI(IEnumerable<ChatMsg> messages, ChatOptions o)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["stream"] = false,
            ["temperature"] = o.Temperature,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        if (o.NumPredict is int np) payload["max_tokens"] = np;
        if (o.Format is JsonElement fmt)
        {
            payload["response_format"] = new Dictionary<string, object?>
            {
                ["type"] = "json_schema",
                ["json_schema"] = new Dictionary<string, object?>
                {
                    ["name"] = "action",
                    ["strict"] = true,
                    ["schema"] = fmt,
                }
            };
        }
        // num_ctx cannot be set via the OpenAI-compatible endpoint - the server handles it.
        return ($"{_root}/v1/chat/completions", JsonSerializer.Serialize(payload));
    }

    /// Best-effort: retrieves the Ollama server version (to verify it supports structured outputs >= 0.5).
    /// Returns null for OpenAI style or on error.
    public async Task<string?> TryGetVersionAsync(CancellationToken ct = default)
    {
        if (_style != ApiStyle.Ollama) return null;
        try
        {
            using var resp = await _http.GetAsync($"{_root}/api/version", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
