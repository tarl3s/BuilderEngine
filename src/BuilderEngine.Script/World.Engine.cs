using System.Globalization;
using System.Text;

namespace BuilderEngine.Script;

/// <summary>
/// The engine loop around <see cref="Step"/>: a host calls <see cref="Frame"/> with real seconds; the game advances by
/// them times <see cref="TimeScale"/> (slow motion), not at all while <see cref="Paused"/>, and the camera, dialogue,
/// HUD, screen effects and animations update. Also the helpers the engine commands share (finding the object a
/// command names, reading words like <c>for</c> or <c>off</c>, <c>{name}</c> in texts).
/// </summary>
public sealed partial class World
{
    /// <summary>Game speed: 1 normal, 0.3 slow motion, 2 fast. Real-time things (dialogue typing, toasts) ignore it.</summary>
    public double TimeScale { get; set; } = 1;
    /// <summary>'pause game': the game clock stands still (keys still reach dialogues and 'when key').</summary>
    public bool Paused { get; set; }
    /// <summary>Real seconds since the world started (dialogue typing, toasts, flashes).</summary>
    public double RealTime { get; private set; }
    /// <summary>The project folder (pictures in images/, saves). Null in tests that don't need files.</summary>
    public string? ProjectFolder { get; set; }
    /// <summary>The playing scene's size (camera limits); 800×450 until a scene is loaded.</summary>
    public double SceneWidth { get; private set; } = 800;
    public double SceneHeight { get; private set; } = 450;

    /// <summary>Extra functions for expressions, like <c>saved("coins")</c>. Values in, value out.</summary>
    public Dictionary<string, Func<List<object?>, object?>> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One frame of the game: <paramref name="realDt"/> real seconds passed.</summary>
    public void Frame(double realDt)
    {
        if (Debug != null && !Debug.AllowFrame()) return; // held by the debugger (paused, or between steps)
        if (!double.IsFinite(realDt) || realDt < 0) realDt = 0;
        RealTime += realDt;
        UpdateScreen(realDt);
        UpdateUi(realDt);
        Step(Paused ? 0 : realDt * TimeScale);
        UpdateCamera(realDt);
        UpdateAnimations();
        UpdateHud();
    }

    /// <summary>Runs frames for <paramref name="seconds"/> real seconds (tests and headless tools).</summary>
    public void RunFor(double seconds, double dt = 1.0 / 60)
    {
        for (double t = 0; t < seconds - 1e-9; t += dt) Frame(dt);
    }

    // ---------------- Helpers for engine commands ----------------

    /// <summary>The object a command's value at <paramref name="i"/> names: an object, a name or tag in quotes, or a bare name.</summary>
    internal static GameObject? ObjectAt(CommandContext c, int i)
    {
        if (i >= c.Args.Count) return null;
        switch (c.Args[i])
        {
            case GameObject g: return g.Destroyed ? null : g;
            case string s when s.Length > 0: return c.World.FindObject(s);
        }
        if (c.Args[i] == null && i < c.Raw.Count && c.Raw[i] is NameExpr { Path.Length: 1 } n) return c.World.FindObject(n.Path[0]);
        return null;
    }

    /// <summary>The bare word written at <paramref name="i"/> (<c>off</c>, <c>left</c>, <c>none</c>), or null.</summary>
    internal static string? WordAt(CommandContext c, int i) =>
        i < c.Raw.Count && c.Raw[i] is NameExpr { Path.Length: 1 } n && (i >= c.Args.Count || c.Args[i] is null or string)
            ? n.Path[0].ToLowerInvariant() : null;

    internal static int IndexOfWord(CommandContext c, string word, int from = 0)
    {
        for (int i = from; i < c.Raw.Count; i++)
            if (c.Raw[i] is NameExpr { Path.Length: 1 } n && string.Equals(n.Path[0], word, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The number after a bare word: <c>camera shake 6 for 0.5</c> → 0.5 for "for".</summary>
    internal static double? NumberAfterWord(CommandContext c, string word)
    {
        int i = IndexOfWord(c, word);
        return i >= 0 && i + 1 < c.Args.Count ? c.Number(c.Args[i + 1]) : null;
    }

    /// <summary>A named number: <c>in 2</c> → 2. Null when it isn't there.</summary>
    internal static double? NamedNumber(CommandContext c, string connector) =>
        c.Named.TryGetValue(connector, out var l) && l.Count > 0 ? c.Number(l[0]) : null;

    /// <summary>The word after a connector: <c>ease out</c> → "out"; a text in quotes works too.</summary>
    internal static string? NamedWord(CommandContext c, string connector)
    {
        if (c.RawNamed.TryGetValue(connector, out var raw) && raw.Count > 0 && raw[0] is NameExpr { Path.Length: 1 } n
            && (!c.Named.TryGetValue(connector, out var v) || v.Count == 0 || v[0] is null or string))
            return n.Path[0].ToLowerInvariant();
        return c.Named.TryGetValue(connector, out var l) && l.Count > 0 && l[0] is string s ? s.ToLowerInvariant() : null;
    }

    internal Ctx ContextFor(CommandContext c) => new(this, c.Mechanism) { Locals = c.Locals, Other = c.Other };

    readonly Dictionary<string, List<object>> _templates = new();

    /// <summary>Fills <c>{name}</c> (any value or expression) in a text: "Score: {score}" → "Score: 3".</summary>
    internal string Interpolate(string template, MechanismInstance inst, Dictionary<string, object?>? locals = null)
    {
        if (template.IndexOf('{') < 0) return template;
        var parts = TemplateParts(template);
        var ctx = new Ctx(this, inst) { Locals = locals ?? new() };
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p is string s) { sb.Append(s); continue; }
            object? v;
            try { v = Eval((Expr)p, ctx); }
            catch (ScriptError) { v = "?"; }
            sb.Append(v is double d ? d.ToString("0.##", CultureInfo.InvariantCulture) : ToText(v));
        }
        return sb.ToString();
    }

    List<object> TemplateParts(string template)
    {
        if (_templates.TryGetValue(template, out var parts)) return parts;
        parts = [];
        int i = 0;
        while (i < template.Length)
        {
            int open = template.IndexOf('{', i);
            int close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (open < 0 || close < 0) { parts.Add(template[i..]); break; }
            if (open > i) parts.Add(template[i..open]);
            string inner = template[(open + 1)..close];
            try { parts.Add(Parser.ParseExpression(inner)); }
            catch (ScriptError) { parts.Add(template[open..(close + 1)]); }
            i = close + 1;
        }
        _templates[template] = parts;
        return parts;
    }

    /// <summary>A warning in the console that doesn't stop the script (a missing picture).</summary>
    readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    internal void WarnOnce(string msg)
    {
        if (!_warned.Add(msg)) return;
        Output.Add("⚠ " + msg);
        RaisePrinted("⚠ " + msg);
    }

    void RaisePrinted(string text) => Printed?.Invoke(text);
}
