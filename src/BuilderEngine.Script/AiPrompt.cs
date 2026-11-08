using System.Text;

namespace BuilderEngine.Script;

/// <summary>What the AI is asked to do. Make and Edit (and Fix) write Builder Script; Explain only talks.</summary>
public enum AiMode { Make, Edit, Explain, Fix }

/// <summary>One object of the open scene, as the prompt describes it.</summary>
public sealed record AiSceneObject(string Name, IReadOnlyList<string> Tags, IReadOnlyList<string> Mechanisms);

/// <summary>
/// What the editor knows when a request is sent: every mechanism (starter kit + the project's own), which ones belong to
/// the project, the open one, the scene. Built by the window; plain data so the assistant runs headless.
/// </summary>
public sealed class AiContext
{
    /// <summary>Every mechanism the game can use: name → Builder Script.</summary>
    public Dictionary<string, string> Sources { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The project's own mechanisms (new ones and edited starter ones).</summary>
    public List<string> ProjectMechanisms { get; init; } = [];
    public string? OpenName { get; init; }
    public string? OpenSource => OpenName != null && Sources.TryGetValue(OpenName, out var s) ? s : null;
    /// <summary>Lines picked in the code view (Explain explains just these).</summary>
    public string? Selection { get; init; }
    /// <summary>The error to fix (Fix mode): a console line or the code check's message.</summary>
    public string? Error { get; init; }
    public string SceneName { get; init; } = "";
    public List<string> SceneNames { get; init; } = [];
    public List<AiSceneObject> SceneObjects { get; init; } = [];
    /// <summary>The open scene, to test a new mechanism inside it (null = a tiny built-in test scene).</summary>
    public Scene? Scene { get; init; }
    /// <summary>"English", or null = the language of the question (explanations).</summary>
    public string? AnswerLanguage { get; init; }
}

/// <summary>
/// Builds a small prompt per request (small local models are the worst case): the language rules and vocabulary, the
/// starter mechanisms closest to the request (at most 2), the open mechanism, the project's mechanism names with their
/// commands and the scene's objects — nothing else. The request itself goes last.
/// </summary>
public static class AiPrompt
{
    /// <summary>System + user prompt stay under this many characters (≈ 3k tokens), whatever the project's size.</summary>
    public const int MaxChars = 9000;
    const int MaxOpenChars = 3500, MaxMechanismNames = 30, MaxObjects = 25;

    public static (string System, string User) Build(AiMode mode, string request, AiContext ctx)
    {
        var system = new StringBuilder();
        if (mode == AiMode.Explain)
        {
            system.AppendLine("You are a friendly teacher inside BuilderEngine, a 2D game engine for people who don't like programming. " +
                "Objects get behavior from mechanisms written in Builder Script (each line is also shown as a block). " +
                "Explain in plain words, short sentences, no jargon; at most about 150 words unless asked for more. " +
                "When you show code, write Builder Script in a ``` block and use only the words and commands listed below — never invent commands. " +
                "You never change the project: tell the person what to write or which block to add.");
            system.AppendLine(LanguageRule(request, ctx));
        }
        else
        {
            system.AppendLine("You write mechanisms in Builder Script, the small language of the BuilderEngine 2D game engine. " +
                "Answer with ONE complete mechanism inside one ``` code block, then one short sentence saying what it does. " +
                "Only Builder Script: no Python, no JavaScript, no braces. Use only the words, commands and functions listed below.");
        }
        system.AppendLine();
        system.Append(AiKnowledge.Rules);
        system.AppendLine();
        system.Append(AiKnowledge.Vocabulary());
        system.AppendLine("ENGINE COMMANDS, as written:");
        system.Append(AiKnowledge.CommandExamples);
        system.AppendLine(AiKnowledge.KitSummary());

        var project = new StringBuilder();
        AppendProject(project, ctx);
        if (request.Length > 1500) request = request[..1500];
        // The engine's word lists grow with every feature: a huge project's list gives way so the whole stays in budget.
        int projectRoom = Math.Max(400, MaxChars - system.Length - request.Length - 1500);
        if (project.Length > projectRoom) { string cut = Cut(project.ToString(), projectRoom); project.Clear().AppendLine(cut); }

        // The mode's part (open mechanism + request) gets what's left after the fixed parts; the examples get the rest.
        string? open = ctx.OpenSource;
        int openMax = Math.Clamp(MaxChars - system.Length - project.Length - request.Length - (ctx.Error?.Length ?? 0) - 900, 300, MaxOpenChars);
        var tail = new StringBuilder();
        switch (mode)
        {
            case AiMode.Make:
                if (open != null) AppendOpen(tail, open, "THE MECHANISM OPEN NOW (for reference; make a NEW one unless asked to change it)", Math.Min(openMax, 1500));
                string name = AiKnowledge.SuggestName(request) ?? "MyMechanism";
                tail.AppendLine($"REQUEST: {request}");
                tail.AppendLine($"Write a new mechanism (call it {name} unless the request names it). Answer with the whole mechanism in one ``` block.");
                break;
            case AiMode.Edit:
                if (open != null) AppendOpen(tail, open, $"THE MECHANISM TO CHANGE (keep its name {ctx.OpenName})", openMax);
                tail.AppendLine($"REQUEST: {request}");
                tail.AppendLine("Write the whole changed mechanism in one ``` block. Keep everything the request doesn't ask to change.");
                break;
            case AiMode.Fix:
                if (open != null) AppendOpen(tail, open, $"THE MECHANISM WITH THE PROBLEM (keep its name {ctx.OpenName})", openMax);
                tail.AppendLine($"ERROR: {Cut(ctx.Error ?? "", 600)}");
                if (!string.IsNullOrWhiteSpace(request)) tail.AppendLine($"NOTE: {request}");
                tail.AppendLine("Fix the mechanism so this error goes away, changing as little as possible. Write the whole fixed mechanism in one ``` block, then one sentence saying what was wrong.");
                break;
            case AiMode.Explain:
                if (!string.IsNullOrWhiteSpace(ctx.Selection))
                {
                    tail.AppendLine($"THESE LINES (from {ctx.OpenName ?? "a mechanism"}):\n```\n{Cut(ctx.Selection!.TrimEnd(), 1500)}\n```");
                    if (open != null && open.Length <= Math.Min(openMax, 1500)) AppendOpen(tail, open, "THE WHOLE MECHANISM, for context", 1500);
                }
                else if (open != null) AppendOpen(tail, open, "THE MECHANISM OPEN NOW", openMax);
                tail.AppendLine($"QUESTION: {(string.IsNullOrWhiteSpace(request) ? (ctx.Selection != null ? "What do these lines do?" : "What does this mechanism do, line by line?") : request)}");
                break;
        }

        int exampleCount = mode switch { AiMode.Make => 2, AiMode.Explain => ctx.OpenName != null ? 1 : 2, _ => 1 };
        var examples = AiKnowledge.PickExamples(request + " " + (ctx.Error ?? ""), exampleCount, exclude: ctx.OpenName != null ? [ctx.OpenName] : null);
        int budget = MaxChars - system.Length - project.Length - tail.Length - 120;
        var user = new StringBuilder();
        user.Append(project);
        var shown = examples.Where(e => (budget -= e.Source.Length + 12) >= 0).ToList();
        if (shown.Count > 0)
        {
            user.AppendLine("EXAMPLES (starter kit mechanisms that work):");
            foreach (var e in shown) user.Append("```\n").Append(e.Source.TrimEnd()).Append("\n```\n");
            user.AppendLine();
        }
        user.Append(tail);
        return (system.ToString(), user.ToString());
    }

    /// <summary>Which language the explanation is in: always English, or the language of the question.</summary>
    static string LanguageRule(string request, AiContext ctx) =>
        ctx.AnswerLanguage is { Length: > 0 } lang ? $"Answer in {lang}."
        : AiTranslate.LooksEnglish(request) ? "Answer in English."
        : "Answer in the same language as the question (for example Portuguese if it is in Portuguese); keep the code words in English.";

    static void AppendOpen(StringBuilder sb, string source, string title, int max)
    {
        sb.AppendLine($"{title}:");
        sb.Append("```\n").Append(Cut(source.TrimEnd(), max)).Append("\n```\n\n");
    }

    static void AppendProject(StringBuilder sb, AiContext ctx)
    {
        var own = ctx.ProjectMechanisms.Where(ctx.Sources.ContainsKey).Take(MaxMechanismNames).ToList();
        if (own.Count > 0)
        {
            sb.Append("THIS PROJECT'S MECHANISMS: ");
            var parts = new List<string>();
            foreach (var n in own)
            {
                try { parts.Add(AiKnowledge.Describe(Parser.Parse(ctx.Sources[n])[0])); }
                catch (ScriptError) { parts.Add(n + " (has an error)"); }
                catch (ArgumentOutOfRangeException) { parts.Add(n); }
            }
            sb.AppendLine(Cut(string.Join(" · ", parts), 1200));
        }
        if (ctx.SceneObjects.Count > 0)
        {
            sb.Append($"SCENE \"{ctx.SceneName}\" OBJECTS: ");
            sb.AppendLine(Cut(string.Join(" · ", ctx.SceneObjects.Take(MaxObjects).Select(o =>
                o.Name + (o.Tags.Count > 0 ? $" [tags {string.Join(", ", o.Tags)}]" : "") + (o.Mechanisms.Count > 0 ? $" ({string.Join(", ", o.Mechanisms)})" : ""))), 1000));
            if (ctx.SceneObjects.Count > MaxObjects) sb.AppendLine($"(and {ctx.SceneObjects.Count - MaxObjects} more)");
        }
        if (ctx.SceneNames.Count > 1) sb.AppendLine("SCENES: " + string.Join(", ", ctx.SceneNames.Select(s => $"\"{s}\"")));
        if (sb.Length > 0) sb.AppendLine();
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "\n# … (cut)";

    /// <summary>The follow-up prompt after a reply that didn't work: the same request plus the reply and its problems.</summary>
    public static string Retry(string firstUser, string reply, IReadOnlyList<string> problems) =>
        firstUser + "\n\nYOUR LAST ANSWER:\n```\n" + Cut(reply.TrimEnd(), 3000) + "\n```\nIT DOESN'T WORK: " + string.Join(" / ", problems.Take(3)) +
        "\nWrite the whole mechanism again, fixed, in one ``` block. Use only the commands listed.";
}
