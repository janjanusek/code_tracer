using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CodeTracer;

public record ChatMsg(string Role, string Content);

/// <summary>Ktoru API rodinu pouzit na chat volania.</summary>
public enum ApiStyle
{
    /// Natívny Ollama endpoint /api/chat - podporuje `format` (JSON schema objekt)
    /// aj `options` (num_ctx, num_predict, repeat_penalty). Default.
    Ollama,
    /// OpenAI-kompatibilný /v1/chat/completions (napr. LM Studio). Structured output
    /// ide cez `response_format` = json_schema; `num_ctx` sa tu nedá nastaviť.
    OpenAI
}

/// <summary>Per-volanie nastavenia. Defaulty su CPU-friendly a deterministicke.</summary>
public sealed class ChatOptions
{
    /// 0 = greedy/deterministicke. Pre rozhodovanie a štruktúru drž 0.
    public double Temperature { get; init; } = 0.0;
    /// Veľkosť kontextu. null => použije sa default klienta (--num-ctx).
    public int? NumCtx { get; init; }
    /// Strop na počet vygenerovaných tokenov (Ollama num_predict / OpenAI max_tokens).
    public int? NumPredict { get; init; }
    /// Penalizácia opakovania - mierne tlmí repetition-loop u malých modelov.
    public double RepeatPenalty { get; init; } = 1.1;
    /// JSON schema (ako JsonElement). Keď je zadaná, výstup je gramaticky vynútený
    /// na štrukturálne platný JSON. null => neobmedzený text.
    public JsonElement? Format { get; init; }
}

/// <summary>
/// Klient na lokálny LLM server. Default je natívne Ollama /api/chat, ktoré
/// podporuje token-level structured outputs (`format` = JSON schema) - server
/// z nej vyrobí gramatiku a počas samplingu maskuje nevalidné tokeny, takže
/// výstup je garantovane platný JSON. OpenAI-kompatibilný režim (LM Studio)
/// rieši to isté cez `response_format`.
/// </summary>
public class LlmClient
{
    private readonly HttpClient _http;
    private readonly string _root;       // napr. http://localhost:11434  (bez /v1, bez trailing /)
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

    /// Zoberie čokoľvek (http://host:port, .../v1, s/bez trailing slash) a vráti čistý root.
    private static string NormalizeRoot(string api)
    {
        var r = api.Trim().TrimEnd('/');
        if (r.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            r = r[..^3].TrimEnd('/');
        return r;
    }

    public async Task<string> ChatAsync(IEnumerable<ChatMsg> messages, ChatOptions? options = null,
                                        CancellationToken ct = default)
    {
        options ??= new ChatOptions();
        var (url, json) = _style == ApiStyle.Ollama
            ? BuildOllama(messages, options)
            : BuildOpenAI(messages, options);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"LLM HTTP {(int)resp.StatusCode} ({url}): {Truncate(body, 800)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // Ollama natívne: { "message": { "content": "..." } }
        // OpenAI:         { "choices": [ { "message": { "content": "..." } } ] }
        string? content = _style == ApiStyle.Ollama
            ? root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c1) ? c1.GetString() : null
            : root.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var m2) && m2.TryGetProperty("content", out var c2) ? c2.GetString() : null;

        return content ?? "";
    }

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
        // num_ctx sa cez OpenAI-kompatibilny endpoint nedá nastaviť - rieši ho server.
        return ($"{_root}/v1/chat/completions", JsonSerializer.Serialize(payload));
    }

    /// Best-effort: zistí verziu Ollama servera (na overenie že podporuje structured outputs >= 0.5).
    /// Pri OpenAI štýle / chybe vráti null.
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
