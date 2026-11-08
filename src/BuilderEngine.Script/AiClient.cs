using System.Text.RegularExpressions;

namespace BuilderEngine.Script;

/// <summary>
/// Something that answers a prompt: a local model (<see cref="OllamaClient"/>), a cloud one (<see cref="CloudClient"/>,
/// <see cref="CloudAuto"/>) or a scripted fake for tests (<see cref="ScriptedAiClient"/>). The assistant only talks to this.
/// </summary>
public interface IAiClient
{
    /// <summary>Who answers, for the panel ("Local · qwen2.5-coder:3b").</summary>
    string Name { get; }

    /// <summary>One answer, no streaming. <paramref name="json"/> asks for a JSON reply where the AI supports it.
    /// Throws <see cref="AiException"/> with a friendly message when it can't answer.</summary>
    Task<string> GenerateAsync(string system, string prompt, bool json = false, CancellationToken ct = default);
}

/// <summary>An AI error the panel can show as it is (local or cloud).</summary>
public class AiException(string message) : Exception(message);

/// <summary>A fake AI for tests and dev snapshots: answers from a queue (the last answer repeats) and records every prompt.</summary>
public sealed class ScriptedAiClient(params string[] replies) : IAiClient
{
    readonly Queue<string> _replies = new(replies);
    string _last = replies.Length > 0 ? replies[^1] : "";

    public string Name { get; init; } = "Test AI";
    /// <summary>Every call: (system, prompt, json).</summary>
    public List<(string System, string Prompt, bool Json)> Calls { get; } = [];
    /// <summary>Answers for the translation step (JSON calls); when empty, JSON calls take from the main queue.</summary>
    public Queue<string> JsonReplies { get; } = new();
    /// <summary>Thrown instead of answering (tests of errors).</summary>
    public Exception? Throw { get; set; }

    public void Enqueue(string reply) => _replies.Enqueue(reply);

    public Task<string> GenerateAsync(string system, string prompt, bool json = false, CancellationToken ct = default)
    {
        Calls.Add((system, prompt, json));
        if (Throw != null) throw Throw;
        if (json && JsonReplies.Count > 0) return Task.FromResult(JsonReplies.Dequeue());
        if (_replies.Count > 0) _last = _replies.Dequeue();
        return Task.FromResult(_last);
    }
}

/// <summary>Keeps API keys out of every text the assistant shows or writes (console, log file, error messages).</summary>
public static class AiSecrets
{
    /// <summary>The text with every known key (and anything that looks like a key after "key=" / "Bearer ") hidden.</summary>
    public static string Redact(string? text, IEnumerable<string>? secrets)
    {
        string s = text ?? "";
        foreach (var k in secrets ?? [])
            if (!string.IsNullOrWhiteSpace(k) && k.Trim().Length >= 6) s = s.Replace(k.Trim(), "•••", StringComparison.Ordinal);
        s = Regex.Replace(s, @"(?i)(bearer\s+|x-api-key[:=]\s*|[?&]key=|api[_-]?key[""']?\s*[:=]\s*[""']?)[A-Za-z0-9_\-\.]{12,}", "$1•••");
        s = Regex.Replace(s, @"\b(sk-[A-Za-z0-9_\-]{10,}|gsk_[A-Za-z0-9]{10,}|AIza[A-Za-z0-9_\-]{20,}|sk-or-[A-Za-z0-9_\-]{10,})", "•••");
        return s;
    }
}
