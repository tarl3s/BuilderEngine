using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuilderEngine.Script;

/// <summary>
/// The local Ollama server (http://localhost:11434), ported from EasyEngine: no packages, HttpClient + System.Text.Json.
/// Asks for a context window that fits the whole prompt (Ollama's default 4096 tokens silently cuts long prompts) and caps
/// the answer (a small model can loop forever).
/// </summary>
public sealed class OllamaClient(string model, string baseUrl = OllamaClient.DefaultUrl) : IAiClient
{
    public const string DefaultUrl = "http://localhost:11434";
    /// <summary>The model the panel suggests when none is picked (small, good at code).</summary>
    public const string SuggestedModel = "qwen2.5-coder:3b";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) }; // the first answer loads the model

    public string Model { get; } = model;
    public string BaseUrl { get; } = baseUrl.TrimEnd('/');
    public string Name => $"Local · {Model}";

    public async Task<bool> IsRunningAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var resp = await Http.GetAsync($"{BaseUrl}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>The downloaded models ("qwen2.5-coder:3b"...); empty when the server isn't running.</summary>
    public async Task<List<string>> ListModelsAsync()
    {
        var result = new List<string>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string json = await Http.GetStringAsync($"{BaseUrl}/api/tags", cts.Token);
            result.AddRange(ParseModels(json));
        }
        catch { }
        return result;
    }

    /// <summary>The model names in an /api/tags answer.</summary>
    public static List<string> ParseModels(string json)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s) list.Add(s);
        }
        catch (JsonException) { }
        return list;
    }

    public async Task<string> GenerateAsync(string system, string prompt, bool json = false, CancellationToken ct = default)
    {
        var body = new GenerateRequest
        {
            Model = Model, Prompt = prompt, System = system, Stream = false,
            Format = json ? "json" : null,
            Options = new() { Temperature = 0.2, NumCtx = ContextFor(system.Length + prompt.Length), NumPredict = json ? 300 : 1500 },
        };
        using var content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try { resp = await Http.PostAsync($"{BaseUrl}/api/generate", content, ct); }
        catch (HttpRequestException) { throw new AiException("Couldn't talk to Ollama. Is it installed and running? (localhost:11434)"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AiException("The answer took too long and was cancelled."); }
        using (resp)
        {
            string text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (text.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    throw new AiException($"The model \"{Model}\" isn't downloaded. Run this in a terminal:\n    ollama pull {Model}");
                throw new AiException($"Ollama answered with an error ({(int)resp.StatusCode}): {text}");
            }
            try { return JsonSerializer.Deserialize<GenerateResponse>(text)?.Response?.Trim() ?? ""; }
            catch (JsonException) { return ""; }
        }
    }

    /// <summary>Room for the whole prompt (~3 characters a token) plus the answer, in steps of 2048 so the model isn't reloaded for small changes.</summary>
    public static int ContextFor(int promptChars) => Math.Clamp((promptChars / 3 + 2000 + 2047) / 2048 * 2048, 4096, 32768);

    static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    sealed class GenerateRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
        [JsonPropertyName("system")] public string System { get; set; } = "";
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("options")] public GenerateOptions? Options { get; set; }
    }

    sealed class GenerateOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("num_ctx")] public int NumCtx { get; set; }
        [JsonPropertyName("num_predict")] public int NumPredict { get; set; }
    }

    sealed class GenerateResponse
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
    }
}

/// <summary>Starts "ollama serve" hidden when the server isn't up (only when the person asks, from the AI settings).</summary>
public static class OllamaManager
{
    static Process? _server;

    public enum State { Running, Started, NotInstalled, Failed }

    public static string? FindExe()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(local, "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
            Path.Combine(local, "Ollama", "ollama.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Checks the server and starts it if needed; waits up to ~10 s for it to answer.</summary>
    public static async Task<State> EnsureRunningAsync(string baseUrl = OllamaClient.DefaultUrl)
    {
        var client = new OllamaClient("", baseUrl);
        if (await client.IsRunningAsync()) return State.Running;
        try
        {
            _server = Process.Start(new ProcessStartInfo
            {
                FileName = FindExe() ?? "ollama", Arguments = "serve",
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { return State.NotInstalled; }
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            if (await client.IsRunningAsync()) return State.Started;
        }
        return State.Failed;
    }

    /// <summary>Stops the server only if BuilderEngine started it.</summary>
    public static void Stop()
    {
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); }
        catch { }
    }
}
