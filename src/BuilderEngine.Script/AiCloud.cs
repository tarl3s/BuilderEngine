using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BuilderEngine.Script;

/// <summary>A cloud AI. Free = a free tier (used by Auto). Anthropic = the native Claude API; the rest are OpenAI-compatible.</summary>
public sealed record CloudProvider(string Id, string Name, string BaseUrl, string Model, bool JsonMode, string KeyUrl, bool Free = true, bool Anthropic = false);

/// <summary>A cloud error: busy / out of quota (another provider may answer), or the model id is gone.</summary>
public sealed class CloudException(string message) : AiException(message)
{
    public bool RateLimited { get; init; }
    public double RetryAfter { get; init; }
    public bool ModelMissing { get; init; }
}

/// <summary>
/// One cloud provider with the person's key (ported from EasyEngine's CloudClient): OpenAI-compatible endpoints (Groq,
/// Gemini, OpenRouter, Mistral, OpenAI, DeepSeek, custom) and the native Anthropic API. A built-in free provider whose
/// model was retired asks the provider for its models and picks one; a model used up for the day hands over to the
/// same provider's next model.
/// </summary>
public sealed class CloudClient(CloudProvider provider, string apiKey, string customUrl = "", string customModel = "") : IAiClient
{
    public static readonly CloudProvider[] Providers =
    [
        new("groq", "Groq", "https://api.groq.com/openai/v1", "openai/gpt-oss-120b", true, "console.groq.com/keys"),
        new("gemini", "Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.8-flash", true, "aistudio.google.com/apikey"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "nvidia/nemotron-3-super-120b-a12b:free", true, "openrouter.ai/keys"),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral-small-latest", true, "console.mistral.ai/api-keys"),
        new("openai", "OpenAI (ChatGPT)", "https://api.openai.com/v1", "gpt-4o-mini", true, "platform.openai.com/api-keys", Free: false),
        new("anthropic", "Claude (Anthropic)", "https://api.anthropic.com/v1", "claude-haiku-5-5", false, "console.anthropic.com/settings/keys", Free: false, Anthropic: true),
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-chat", true, "platform.deepseek.com/api_keys", Free: false),
        new("custom", "Custom (OpenAI-compatible)", "", "", true, "any OpenAI-compatible URL", Free: false),
    ];

    public static CloudProvider? Provider(string id) => Array.Find(Providers, p => p.Id == id);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    static readonly Dictionary<string, string> Discovered = new();
    static readonly Dictionary<string, string[]> DailyFallbacks = new()
    {
        ["groq"] = ["qwen/qwen3.8-27b", "openai/gpt-oss-20b"],
        ["gemini"] = ["gemini-3.7-flash", "gemini-3.5-flash", "gemini-3.1-flash-lite"],
    };
    static readonly Dictionary<string, DateTime> Resting = new();

    public CloudProvider ProviderInfo { get; } = provider;
    public string Name => ProviderInfo.Id == "custom" ? $"Cloud · {customModel}" : $"Cloud · {ProviderInfo.Name}";

    static bool IsDaily(CloudException ex) =>
        !Regex.IsMatch(ex.Message, @"per minute|PerMinute|\bTPM\b|\bRPM\b", RegexOptions.IgnoreCase)
        && Regex.IsMatch(ex.Message, @"per day|per-day|\bTPD\b|\bRPD\b|daily|free_tier_requests|free-models-per-day|exceeded your current quota", RegexOptions.IgnoreCase);

    public async Task<string> GenerateAsync(string system, string prompt, bool json = false, CancellationToken ct = default)
    {
        var p = ProviderInfo;
        bool builtIn = Provider(p.Id) is { } def && def.Model == p.Model && p.Id != "custom";
        if (!builtIn) return await GenerateWith(p, prompt, system, json, ct);

        string first = Discovered.TryGetValue(p.Id, out var known) ? known : p.Model;
        var chain = new[] { first }.Concat(DailyFallbacks.TryGetValue(p.Id, out var more) ? more : []).Distinct().ToList();
        var awake = chain.Where(m => !(Resting.TryGetValue(p.Id + "/" + m, out var until) && until > DateTime.UtcNow)).ToList();
        if (awake.Count == 0) awake = chain;
        CloudException? last = null;
        foreach (var model in awake)
        {
            try { return await GenerateWith(p with { Model = model }, prompt, system, json, ct); }
            catch (CloudException ex) when (ex.ModelMissing && model == first && !Discovered.ContainsKey(p.Id))
            {
                var alt = await DiscoverModel(p, ct);
                if (alt == null || alt == model) { last = ex; continue; }
                Discovered[p.Id] = alt;
                try { return await GenerateWith(p with { Model = alt }, prompt, system, json, ct); }
                catch (CloudException ex2) when (ex2.RateLimited && IsDaily(ex2)) { Resting[p.Id + "/" + alt] = DateTime.UtcNow.AddSeconds(Math.Max(ex2.RetryAfter, 3600)); last = ex2; }
            }
            catch (CloudException ex) when (ex.RateLimited && IsDaily(ex)) { Resting[p.Id + "/" + model] = DateTime.UtcNow.AddSeconds(Math.Max(ex.RetryAfter, 3600)); last = ex; }
            catch (CloudException ex) when (ex.ModelMissing) { last = ex; if (model == first) Discovered.Remove(p.Id); }
        }
        throw last ?? new CloudException($"{p.Name} limit reached for now.") { RateLimited = true };
    }

    async Task<string?> DiscoverModel(CloudProvider p, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, p.BaseUrl.TrimEnd('/') + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s) ids.Add(s.StartsWith("models/") ? s[7..] : s);
            return PickModel(p.Id, ids);
        }
        catch { return null; }
    }

    /// <summary>The best chat model among a provider's current ids (null = none fits).</summary>
    public static string? PickModel(string providerId, IReadOnlyList<string> ids)
    {
        static bool Chat(string id) => !Regex.IsMatch(id, "tts|whisper|guard|safety|image|embed|audio|transcribe|orpheus|robotics|computer-use|lyria|omni|vision", RegexOptions.IgnoreCase);
        var chat = ids.Where(Chat).ToList();
        if (providerId == "gemini")
        {
            var best = chat.Select(id => (id, m: Regex.Match(id, @"^gemini-(\d+(?:\.\d+)?)-flash$")))
                .Where(x => x.m.Success).OrderByDescending(x => double.Parse(x.m.Groups[1].Value, CultureInfo.InvariantCulture)).Select(x => x.id).FirstOrDefault();
            return best ?? chat.FirstOrDefault(id => id == "gemini-flash-latest");
        }
        string[] prefer = providerId switch
        {
            "openrouter" => ["nemotron-3-super", "gpt-oss-120b", "llama-3.3-70b", "gemma-4-31b", "qwen", "deepseek", "gemma"],
            "groq" => ["gpt-oss-120b", "llama-3.3-70b", "qwen", "gpt-oss-20b", "llama"],
            "mistral" => ["mistral-small-latest", "mistral-medium-latest", "mistral-large-latest"],
            _ => [],
        };
        if (providerId == "openrouter") chat = chat.Where(id => id.EndsWith(":free")).ToList(); // Auto never spends money
        foreach (var want in prefer)
            if (chat.FirstOrDefault(id => id.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        return prefer.Length > 0 ? chat.FirstOrDefault() : null;
    }

    async Task<string> GenerateWith(CloudProvider p, string prompt, string system, bool json, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new CloudException($"No API key for {p.Name}. Paste your key in the AI settings (get one at {p.KeyUrl}).");
        string baseUrl = p.BaseUrl, model = p.Model;
        if (p.Id == "custom")
        {
            baseUrl = customUrl; model = customModel;
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
                throw new CloudException("Set the Custom endpoint URL and model name in the AI settings.");
        }
        if (p.Anthropic) return await AnthropicGenerate(baseUrl, model, prompt, system, p, ct);

        var body = new ChatRequest
        {
            Model = model, Temperature = 0.2, Stream = false,
            Messages = [new() { Role = "system", Content = system }, new() { Role = "user", Content = prompt }],
            ResponseFormat = json && p.JsonMode ? new ResponseFormat { Type = "json_object" } : null,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        string text = await Send(req, p, model, ct);
        try
        {
            var parsed = JsonSerializer.Deserialize<ChatResponse>(text);
            return parsed?.Choices is { Count: > 0 } ? parsed.Choices[0].Message?.Content?.Trim() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    async Task<string> AnthropicGenerate(string baseUrl, string model, string prompt, string system, CloudProvider p, CancellationToken ct)
    {
        var body = new { model, max_tokens = 4096, system, messages = new[] { new { role = "user", content = prompt } } };
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        string text = await Send(req, p, model, ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var tx))
                        return (tx.GetString() ?? "").Trim();
        }
        catch (JsonException) { }
        return "";
    }

    /// <summary>Sends a request; a failed answer becomes a friendly <see cref="CloudException"/> (never with the key in it).</summary>
    static async Task<string> Send(HttpRequestMessage req, CloudProvider p, string model, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (HttpRequestException) { throw new CloudException($"Couldn't reach {p.Name}. Check your internet connection."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new CloudException($"{p.Name} took too long and was cancelled."); }
        using (resp)
        {
            string text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return text;
            string? detail = TryGetErrorMessage(text);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CloudException($"The {p.Name} API key is invalid. Check it in the AI settings ({p.KeyUrl}).");
            if (resp.StatusCode is (HttpStatusCode)429 or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or (HttpStatusCode)529)
                throw new CloudException($"{p.Name} limit reached for now." + (detail != null ? $" ({detail})" : "")) { RateLimited = true, RetryAfter = RetryAfterSeconds(resp, detail) };
            if (resp.StatusCode == HttpStatusCode.NotFound || ModelGone(detail))
                throw new CloudException($"Model \"{model}\" isn't available on {p.Name}." + (detail != null ? $"\n({detail})" : "")) { ModelMissing = true };
            throw new CloudException($"{p.Name} error ({(int)resp.StatusCode}): {detail ?? text}");
        }
    }

    static double RetryAfterSeconds(HttpResponseMessage resp, string? detail) =>
        resp.Headers.RetryAfter?.Delta is TimeSpan d ? d.TotalSeconds : RetryAfterSeconds(detail);

    /// <summary>"try again in 7.1s" / "in 1m3s" / "in 250ms" in a provider's message, in seconds (0 = none).</summary>
    public static double RetryAfterSeconds(string? detail)
    {
        var m = Regex.Match(detail ?? "", @"(?:try again|retry) in (?:(\d+)m)?(\d+(?:\.\d+)?)(ms|s)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
        return (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) * 60 : 0) + (m.Groups[3].Value.Equals("ms", StringComparison.OrdinalIgnoreCase) ? v / 1000 : v);
    }

    static bool ModelGone(string? detail) => detail != null
        && Regex.IsMatch(detail, @"not found|does not exist|no longer available|decommissioned|deprecated|not supported|invalid model|unknown model|model_not_found|isn't available|is not available", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(detail, @"too large|rate limit|quota|tokens per", RegexOptions.IgnoreCase);

    static string? TryGetErrorMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0) // Gemini sometimes wraps it
                return TryGetErrorMessage(doc.RootElement[0].GetRawText());
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString();
                if (err.TryGetProperty("message", out var msg)) return msg.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = [];
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("response_format")] public ResponseFormat? ResponseFormat { get; set; }
    }
    sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }
    sealed class ResponseFormat { [JsonPropertyName("type")] public string Type { get; set; } = ""; }
    sealed class ChatResponse { [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; } }
    sealed class Choice { [JsonPropertyName("message")] public ChatMessage? Message { get; set; } }
}

/// <summary>
/// "Cloud — free tiers": tries every free provider the person has a key for, in order; any failure (busy, quota, a bad
/// key, no internet) moves to the next one. When every one is only busy for a short while, it waits once and tries again.
/// </summary>
public sealed class CloudAuto(IReadOnlyList<IAiClient> clients) : IAiClient
{
    public string Name => "Cloud · free tiers";
    public IReadOnlyList<IAiClient> Clients { get; } = clients;

    public async Task<string> GenerateAsync(string system, string prompt, bool json = false, CancellationToken ct = default)
    {
        if (Clients.Count == 0)
            throw new AiException("No cloud key yet. Open the AI settings and paste a free key (Groq: console.groq.com/keys, Gemini: aistudio.google.com/apikey).");
        var errors = new List<string>();
        double wait = double.MaxValue;
        bool allBusy = true;
        for (int round = 0; round < 2; round++)
        {
            foreach (var c in Clients)
            {
                ct.ThrowIfCancellationRequested();
                try { return await c.GenerateAsync(system, prompt, json, ct); }
                catch (AiException ex)
                {
                    errors.Add(ex.Message);
                    if (ex is CloudException { RateLimited: true } ce) wait = Math.Min(wait, ce.RetryAfter > 0 ? ce.RetryAfter : 20);
                    else allBusy = false;
                }
            }
            if (!allBusy || wait > 60) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, wait)), ct);
            errors.Clear();
        }
        throw new AiException("No free cloud AI could answer right now:\n• " + string.Join("\n• ", errors.Distinct()));
    }
}
