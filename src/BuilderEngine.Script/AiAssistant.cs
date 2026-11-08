using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BuilderEngine.Script;

/// <summary>What came back from one request.</summary>
public sealed class AiResult
{
    public AiMode Mode { get; init; }
    /// <summary>A friendly error (no AI, no answer...); null when the AI answered.</summary>
    public string? Error { get; set; }
    /// <summary>Explain: the answer. Make / Edit / Fix: what the model said around its code.</summary>
    public string Text { get; set; } = "";
    /// <summary>The mechanisms to preview and accept (Make / Edit / Fix).</summary>
    public List<AiMechanism> Mechanisms { get; } = [];
    /// <summary>Problems still left after the retry (the preview shows them; accepting is still possible).</summary>
    public List<string> Problems { get; } = [];
    /// <summary>The first answer had problems and the AI was asked once more.</summary>
    public bool Retried { get; set; }
    /// <summary>The request as the AI got it (translated to English when it was in another language).</summary>
    public string Request { get; set; } = "";
    public bool Translated { get; set; }
    public bool HasCode => Mechanisms.Count > 0;
}

/// <summary>
/// The AI assistant without any UI: translate (when needed) → build a small prompt → ask → pull the Builder Script out
/// → test it → once more with the problems if it fails → mechanisms to preview. Explain only answers. Every line it logs
/// goes through <see cref="AiSecrets.Redact"/>, so keys never reach the console or the log file.
/// </summary>
public sealed class AiAssistant(IAiClient client, Action<string>? log = null, IEnumerable<string>? secrets = null)
{
    readonly List<string> _secrets = (secrets ?? []).ToList();

    public IAiClient Client { get; } = client;
    /// <summary>A file that gets the last request's prompts and answers (keys hidden); null = none.</summary>
    public string? LogFile { get; set; }
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(5);

    void Log(string line) => log?.Invoke(AiSecrets.Redact(line, _secrets));

    readonly StringBuilder _file = new();
    void FileLog(string title, string text)
    {
        if (LogFile == null) return;
        _file.Append("==== ").Append(title).Append(" ====\n").Append(AiSecrets.Redact(text, _secrets)).Append("\n\n");
    }

    void FlushFile()
    {
        if (LogFile == null) return;
        try { File.WriteAllText(LogFile, _file.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _file.Clear();
    }

    public async Task<AiResult> RunAsync(AiMode mode, string request, AiContext ctx, CancellationToken ct = default)
    {
        var result = new AiResult { Mode = mode, Request = request };
        var clock = Stopwatch.StartNew();
        try
        {
            if ((mode is AiMode.Edit or AiMode.Fix) && ctx.OpenName == null)
            {
                result.Error = mode == AiMode.Fix ? "Open the mechanism with the error first." : "Open a mechanism first (click one in the library), or use Make for a new one.";
                return result;
            }
            Log($"✨ Asking {Client.Name} ({mode})…");
            string english = request;
            if (mode != AiMode.Explain && !string.IsNullOrWhiteSpace(request))
            {
                english = await AiTranslate.ToEnglishAsync(request, Client, ct);
                if (english != request)
                {
                    result.Translated = true;
                    Log($"✨ In English: {english}");
                }
            }
            result.Request = english;
            var (system, user) = AiPrompt.Build(mode, english, ctx);
            FileLog("SYSTEM", system);
            FileLog("USER", user);
            string reply = await Client.GenerateAsync(system, user, false, ct);
            FileLog("REPLY", reply);

            if (mode == AiMode.Explain)
            {
                result.Text = reply.Trim();
                if (result.Text.Length == 0) result.Error = "The AI gave an empty answer. Try again, or pick another AI in the settings.";
                Log($"✨ Answered in {Seconds(clock)} s.");
                return result;
            }

            string fallback = AiKnowledge.SuggestName(english) ?? "MyMechanism";
            string? forced = mode is AiMode.Edit or AiMode.Fix ? ctx.OpenName : null;
            string? script = AiReply.ExtractScript(reply, forced, fallback);
            var check = script == null ? NoCode() : await AiCheck.ValidateAsync(script, ctx, CheckTimeout);
            if (!check.Ok)
            {
                Log("✨ The answer has a problem (" + check.Problems[0] + "); asking once more…");
                result.Retried = true;
                string retryUser = AiPrompt.Retry(user, script ?? reply, check.Problems);
                FileLog("RETRY", retryUser);
                string reply2 = await Client.GenerateAsync(system, retryUser, false, ct);
                FileLog("REPLY 2", reply2);
                string? script2 = AiReply.ExtractScript(reply2, forced, fallback);
                var check2 = script2 == null ? NoCode() : await AiCheck.ValidateAsync(script2, ctx, CheckTimeout);
                if (script2 != null && (check2.Ok || script == null || check2.Problems.Count <= check.Problems.Count))
                {
                    script = script2; check = check2; reply = reply2;
                }
            }
            result.Text = AiReply.Prose(reply);
            if (script == null)
            {
                result.Error = "The AI didn't write any Builder Script. Try saying it differently, or pick a bigger model in the settings.";
                return result;
            }
            result.Problems.AddRange(check.Problems);
            result.Mechanisms.AddRange(AiMerge.Propose(script, mode, ctx));
            if (result.Mechanisms.Count == 0) result.Error = "The AI's answer is the same as what you already have.";
            Log(check.Ok ? $"✨ Ready in {Seconds(clock)} s — tested, no errors. Look at the preview, then Accept or Discard."
                         : $"✨ Ready in {Seconds(clock)} s, but it still has a problem: {check.Problems[0]}");
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Cancelled.";
            return result;
        }
        catch (AiException e)
        {
            result.Error = AiSecrets.Redact(e.Message, _secrets);
            Log("⚠ AI: " + result.Error);
            return result;
        }
        finally { FlushFile(); }
    }

    /// <summary>"2.4" whatever the computer's language (the editor is in English).</summary>
    public static string Seconds(Stopwatch clock) => clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    static AiCheckResult NoCode()
    {
        var r = new AiCheckResult();
        r.Problems.Add("There is no Builder Script in the answer: write the mechanism in a ``` block, starting with 'mechanism Name'.");
        return r;
    }

    /// <summary>Which mechanism a console error is about: "Coin (line 4): …" → Coin; else the first known name it quotes.</summary>
    public static string? MechanismInError(string error, IEnumerable<string> names)
    {
        var list = names.ToList();
        var m = Regex.Match(error ?? "", @"^\W*([A-Za-z_][A-Za-z0-9_]*) \(line \d+\)");
        if (m.Success && list.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) return list.First(n => n.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        foreach (Match q in Regex.Matches(error ?? "", @"'([A-Za-z_][A-Za-z0-9_]*)'"))
            if (list.FirstOrDefault(n => n.Equals(q.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        return null;
    }
}
