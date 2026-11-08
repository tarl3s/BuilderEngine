namespace BuilderEngine.Script;

// GAME KIT helpers shared by tiles, lights, mouse, UI, items and cards: reading "word value" options written after a
// command (tile_kind "ore" with "#C0C0C0" hardness 2 drop "ore"), engine values (mouse_x), functions that know which
// object calls them, and firing the game events ('when clicked', 'when tile broken'…).
public sealed partial class World
{
    /// <summary>Values read by a bare name in scripts: <c>mouse_x</c>, <c>mouse_y</c>… (after variables of the same name).</summary>
    internal readonly Dictionary<string, Func<World, object?>> EngineValues = new(StringComparer.Ordinal);

    /// <summary>The mechanism calling a function from <see cref="Functions"/> right now (for "this object" functions).</summary>
    internal MechanismInstance? CallingInst;

    /// <summary>One value of a command line as written and as worked out.</summary>
    internal readonly record struct Word(Expr Raw, object? Value)
    {
        /// <summary>The bare name written here (<c>solid</c>, <c>hardness</c>), lower case — only when it isn't a set variable.</summary>
        public string? Name => Raw is NameExpr { Path.Length: 1 } n && (Value is null || (Value is string s && (s.Length == 0 || string.Equals(s, n.Path[0], StringComparison.OrdinalIgnoreCase))))
            ? n.Path[0].ToLowerInvariant() : null; // unset, or a mechanism's name (Solid, OneWay)
    }

    /// <summary>The positional values and then the named ones (in the order given), as one list.</summary>
    internal static List<Word> Words(CommandContext c, params string[] connectors)
    {
        var list = new List<Word>();
        for (int i = 0; i < c.Raw.Count && i < c.Args.Count; i++) list.Add(new Word(c.Raw[i], c.Args[i]));
        foreach (var k in connectors)
            if (c.RawNamed.TryGetValue(k, out var raw) && c.Named.TryGetValue(k, out var vals))
                for (int i = 0; i < raw.Count && i < vals.Count; i++) list.Add(new Word(raw[i], vals[i]));
        return list;
    }

    /// <summary>The value after a bare word (<c>gap 10</c> → 10) anywhere in the line, or null.</summary>
    internal static object? ValueAfter(List<Word> words, string word)
    {
        for (int i = 0; i + 1 < words.Count; i++)
            if (words[i].Name == word) return words[i + 1].Value;
        return null;
    }

    internal static bool HasWord(List<Word> words, string word)
    {
        foreach (var w in words) if (w.Name == word) return true;
        return false;
    }

    /// <summary>"at x, y" names of an event: x and y as two locals, or one name holding [x, y].</summary>
    static void BindXY(Dictionary<string, object?> locals, string? bind, double x, double y)
    {
        if (bind == null) return;
        int comma = bind.IndexOf(',');
        if (comma < 0) { locals[bind.Trim()] = new List<object?> { x, y }; return; }
        locals[bind[..comma].Trim()] = x;
        locals[bind[(comma + 1)..].Trim()] = y;
    }

    /// <summary>
    /// Fires a game event: on <paramref name="only"/> when given (clicked, hovered…), else on every object that has it
    /// (mouse pressed, tile broken). <paramref name="filter"/> is what a "when … \"x\"" must match (a kind, a button,
    /// or — with <paramref name="filterObject"/> — a tag or name of that object). Locals: x, y (by the event's
    /// 'at' names) and <paramref name="extra"/>.
    /// </summary>
    internal void FireGame(string name, GameObject? only = null, string? filter = null, GameObject? filterObject = null,
        double x = double.NaN, double y = double.NaN, GameObject? other = null, (string, object?)[]? extra = null)
    {
        if (only != null) { FireGameOn(only, name, filter, filterObject, x, y, other, extra); return; }
        for (int i = 0, n = _objects.Count; i < n && i < _objects.Count; i++)
            if (!_objects[i].Destroyed) FireGameOn(_objects[i], name, filter, filterObject, x, y, other, extra);
    }

    void FireGameOn(GameObject o, string name, string? filter, GameObject? filterObject, double x, double y, GameObject? other, (string, object?)[]? extra)
    {
        if (o.Destroyed) return;
        foreach (var m in o.Flat)
            foreach (var w in m.Ix.Game)
            {
                if (w.Event.Name != name) continue;
                if (w.Event.Filter != null)
                {
                    bool ok = filterObject != null ? filterObject.Is(w.Event.Filter)
                        : filter != null && string.Equals(w.Event.Filter, filter, StringComparison.OrdinalIgnoreCase);
                    if (!ok) continue;
                }
                var locals = new Dictionary<string, object?>();
                if (!double.IsNaN(x)) BindXY(locals, w.Event.Bind, x, y);
                if (extra != null) foreach (var (k, v) in extra) locals[k] = v;
                StartWhen(m, w, locals, other);
            }
    }

    /// <summary>Does any object have this game event? (cheap check before working out its values)</summary>
    internal bool AnyListens(string name)
    {
        foreach (var o in _objects)
            if (!o.Destroyed)
                foreach (var m in o.Flat)
                    foreach (var w in m.Ix.Game)
                        if (w.Event.Name == name) return true;
        return false;
    }

    /// <summary>A list value (or one item) as a list of objects: <c>hide all("ghost")</c>, <c>hide Boss</c>.</summary>
    internal static IEnumerable<GameObject> ObjectsIn(CommandContext c, int i)
    {
        if (i >= c.Args.Count) yield break;
        switch (c.Args[i])
        {
            case List<object?> list:
                foreach (var v in list)
                    if (v is GameObject { Destroyed: false } g) yield return g;
                    else if (v is string s && c.World.FindObject(s) is { } f) yield return f;
                break;
            default:
                if (ObjectAt(c, i) is { } one) yield return one;
                break;
        }
    }
}
