using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BuilderEngine.Script;

/// <summary>
/// One-click local AI: downloads Ollama's official Windows installer, installs it silently (per user, no admin),
/// starts the server and downloads a small model — so the AI works without the person ever opening a terminal.
/// </summary>
public static class OllamaSetup
{
    /// <summary>Ollama's official installer (always the latest version).</summary>
    public const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    public const string Website = "https://ollama.com/download";

    /// <summary>The model the one-click setup downloads: small (≈1 GB), fast on most PCs, and the one that did best on
    /// EasyEngine's AI tests among the small local models.</summary>
    public const string StarterModel = "qwen2.5-coder:1.5b";

    public static bool IsInstalled => OllamaManager.FindExe() != null;

    static string WorkDir => Path.Combine(Path.GetTempPath(), "BuilderEngine_ollama");

    /// <summary>Downloads the installer (progress 0..1); returns its path.</summary>
    public static async Task<string> DownloadInstallerAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(WorkDir);
        string file = Path.Combine(WorkDir, "OllamaSetup.exe");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BuilderEngine");
        using var resp = await http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file))
        {
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        return file;
    }

    /// <summary>Runs the installer silently and waits for it; true when Ollama is installed afterwards.</summary>
    public static async Task<bool> InstallAsync(string installer, CancellationToken ct = default)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = installer,
            // Inno Setup switches: no wizard, no message boxes, no restart. Ollama installs per user (no admin).
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("The Ollama installer didn't start.");
        await p.WaitForExitAsync(ct);
        for (int i = 0; i < 20 && !IsInstalled; i++) await Task.Delay(500, ct);
        return IsInstalled;
    }

    /// <summary>One line of Ollama's /api/pull progress: what it is doing and how far (0..1, or -1 when unknown).</summary>
    public readonly record struct PullProgress(string Status, double Fraction);

    /// <summary>Reads one NDJSON line of /api/pull; null when it isn't one. Throws when Ollama reports an error.</summary>
    public static PullProgress? ParsePullLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var err)) throw new AiException("Ollama: " + err.GetString());
            string status = r.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            double frac = -1;
            if (r.TryGetProperty("total", out var t) && t.TryGetInt64(out long total) && total > 0
                && r.TryGetProperty("completed", out var c) && c.TryGetInt64(out long completed))
                frac = Math.Clamp((double)completed / total, 0, 1);
            return new PullProgress(status, frac);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Downloads a model through the running Ollama server, reporting progress; true when it says "success".</summary>
    public static async Task<bool> PullAsync(string model, IProgress<PullProgress>? progress = null,
        string baseUrl = OllamaClient.DefaultUrl, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var body = new StringContent(JsonSerializer.Serialize(new { model, stream = true }), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/pull") { Content = body };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        bool ok = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (ParsePullLine(line) is not { } p) continue;
            progress?.Report(p);
            if (p.Status == "success") ok = true;
        }
        return ok;
    }

    /// <summary>
    /// When BuilderEngine opens: if the local AI is chosen and Ollama is installed, start its server hidden (so the first
    /// request works) and say whether a model is missing. Never downloads anything by itself.
    /// </summary>
    public static async Task<string?> PrepareOnOpenAsync(AiSettings settings)
    {
        if (settings.Provider != AiSettings.Local || !IsInstalled) return null;
        try
        {
            var state = await OllamaManager.EnsureRunningAsync(settings.OllamaUrl);
            if (state is OllamaManager.State.Failed or OllamaManager.State.NotInstalled) return null;
            var models = await new OllamaClient("", settings.OllamaUrl).ListModelsAsync();
            return models.Count == 0
                ? "Ollama is installed but has no AI model yet: open ✨ AI → ⚙ and press “Download the AI model”."
                : null;
        }
        catch { return null; }
    }
}
