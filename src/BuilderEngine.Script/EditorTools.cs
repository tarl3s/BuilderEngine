namespace BuilderEngine.Script;

/// <summary>
/// Editor tools: a mechanism with <c>when tool …</c> events is a tool. The editor shows it in the scene's TOOLS bar;
/// using it runs its events on the scene being edited (not on a game), with commands that edit the scene:
/// <code>
/// mechanism CoinLine
///     param coin: text = "Coin"
///     when tool click at x, y
///         place coin at x, y
/// </code>
/// Events: <c>click at x, y</c> (press), <c>drag at x, y</c> (moving while pressed), <c>release at x, y</c>,
/// <c>used</c> (its button was pressed) and <c>key "r"</c> (a key while it is the active tool).
/// Commands: <c>place</c>, <c>erase</c>, <c>fill_row</c>, <c>select</c>; functions <c>objects_at</c>, <c>object_at</c>,
/// <c>snap</c>; and every ordinary command and function (<c>all("coin")</c>, <c>obj.x = 10</c>, <c>destroy obj</c>…).
/// </summary>
public static class EditorTools
{
    public static readonly string[] Events = ["click", "drag", "release", "used", "key"];
    /// <summary>Commands that only exist in tools (the blocks palette and did-you-mean can list them).</summary>
    public static readonly string[] Commands = ["place", "erase", "fill_row", "select"];
    public static readonly string[] Functions = ["objects_at", "object_at", "snap"];
    /// <summary>The most objects one use of a tool may place (a runaway loop stops here).</summary>
    public const int MaxPlacedPerUse = 5000;

    public static bool IsTool(MechanismDef d) => d.Whens.Any(w => w.Event.Kind == EventKind.Tool);

    /// <summary>The tools among these mechanisms, in the order given.</summary>
    public static List<EditorTool> Find(IEnumerable<MechanismDef> defs) => defs.Where(IsTool).Select(d => new EditorTool(d)).ToList();

    /// <summary>What follows <c>when tool</c>: <c>click at x, y</c>, <c>used</c>, <c>key "r"</c>.</summary>
    public static string EventTail(EventSpec e) =>
        (e.Name ?? "") + (e.Filter != null ? " " + Printer.Quote(e.Filter) : "") + (e.Bind != null ? " at " + e.Bind : "");

    /// <summary>The comment right under <c>mechanism Name</c> (<c># A tool: drag to paint ground</c>), or "".</summary>
    public static string Description(string source, string mechanism)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var parts = lines[i].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "mechanism" || !string.Equals(parts[1], mechanism, StringComparison.OrdinalIgnoreCase)) continue;
            var text = new List<string>();
            for (int k = i + 1; k < lines.Length && lines[k].Trim().StartsWith('#'); k++) text.Add(lines[k].Trim().TrimStart('#').Trim());
            return string.Join(" ", text);
        }
        return "";
    }

    static readonly string[] Swatches = ["#5B8DEF", "#4FC3F7", "#6FBF73", "#A3D977", "#E8B04B", "#F2994A", "#D97757", "#E05D5D", "#E07AB5", "#9B7BE0", "#6B5B4B", "#ECECEC"];

    /// <summary>The colour a new object with this mechanism gets (the same rule as dropping it from the library).</summary>
    public static string ColorFor(string mechanism)
    {
        switch (mechanism.ToLowerInvariant())
        {
            case "coin": return "#E8B04B";
            case "playermove": return "#5B8DEF";
            case "patrol": return "#6FBF73";
            case "health": return "#E05D5D";
            case "blink": return "#9B7BE0";
            case "solid": return "#6B5B4B";
        }
        int h = 17;
        foreach (var c in mechanism) h = h * 31 + char.ToLowerInvariant(c);
        return Swatches[(int)((uint)h % Swatches.Length)];
    }

    /// <summary>"Coin 3" → "Coin" (copies are numbered from the base name).</summary>
    public static string BaseName(string name)
    {
        int sp = name.LastIndexOf(' ');
        return sp > 0 && int.TryParse(name[(sp + 1)..], out _) ? name[..sp] : name;
    }
}

/// <summary>A mechanism that is a tool, and what it reacts to.</summary>
public sealed record EditorTool(MechanismDef Def)
{
    public string Name => Def.Name;
    public bool Has(string toolEvent) => Def.Whens.Any(w => w.Event.Kind == EventKind.Tool && w.Event.Name == toolEvent);
    /// <summary>Clicks, drags or releases on the scene: the tool stays active until another one (or Select) is picked.</summary>
    public bool UsesPointer => Has("click") || Has("drag") || Has("release");
    /// <summary>It reacts to the release: while dragging, the editor shows the box from the press to the pointer.</summary>
    public bool ShowsBox => Has("release");
    public IEnumerable<string> Keys => Def.Whens.Where(w => w.Event is { Kind: EventKind.Tool, Name: "key", Filter: not null }).Select(w => w.Event.Filter!).Distinct();
}

/// <summary>
/// Runs one tool on a scene. The tool's mechanism lives on a hidden object of an editor world (so its variables last
/// from one use to the next: a first click can remember where a line starts). Before each use the world gets a copy
/// of the scene's objects; after each event the changes go back to the scene. Everything between
/// <see cref="BeginUse"/> and <see cref="EndUse"/> is one undo step.
/// </summary>
public sealed class ToolRunner
{
    public World World { get; }
    public EditorTool Tool { get; }
    /// <summary>The tool's params as the options bar sets them (raw values, like a scene mechanism's); the rest keep their defaults.</summary>
    public Dictionary<string, object?> Params { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The object selected in the editor (a tool reads it as <c>selected</c>); <c>select obj</c> changes it.</summary>
    public int SelectedId { get; set; }
    /// <summary>Prints, notifies and errors of the tool (for the console and the status bar).</summary>
    public event Action<string>? Message;
    public List<string> Errors { get; } = [];

    readonly MechanismInstance _inst;
    readonly GameObject _toolObject;
    readonly Dictionary<GameObject, SceneObject> _map = new();
    readonly Dictionary<SceneObject, GameObject> _back = new(ReferenceEqualityComparer.Instance);
    Scene? _scene;
    string? _before;
    int _placed;

    /// <summary>Loads <paramref name="sources"/> (the editor's mechanisms; ones with errors are skipped) and makes the tool.</summary>
    public ToolRunner(string toolName, IEnumerable<string> sources, string? projectFolder = null)
    {
        World = World.NewEditor();
        World.ProjectFolder = projectFolder;
        foreach (var src in sources)
        {
            try { World.Load(src); }
            catch (ScriptError) { /* the editor shows a broken mechanism's error where it is edited */ }
        }
        if (!World.Library.TryGetValue(toolName, out var def))
            throw new ScriptError($"There's no mechanism called '{toolName}'.{World.Suggest(toolName, World.Library.Keys)}", 0);
        if (!EditorTools.IsTool(def)) throw new ScriptError($"'{def.Name}' has no 'when tool …', so it isn't a tool.", def.Line);
        Tool = new EditorTool(def);
        World.Printed += m => Message?.Invoke(m);
        InstallCommands();
        _toolObject = World.NewDetached(def.Name);
        _inst = World.InstantiateDetached(def, _toolObject, null);
    }

    // ---------------- Uses ----------------

    /// <summary>Starts a use (a press, a button, a key): copies the scene's objects into the tool's world.</summary>
    public void BeginUse(Scene scene)
    {
        if (_scene != null) EndUse();
        _scene = scene;
        _before = scene.ToJson();
        _placed = 0;
        World.ClearObjects();
        _map.Clear();
        _back.Clear();
        foreach (var so in scene.Objects)
        {
            var go = World.AddStill(so.Name, so.X, so.Y, so.Width, so.Height, so.Tags);
            _map[go] = so;
            _back[so] = go;
        }
        World.Globals["scene_width"] = scene.Width;
        World.Globals["scene_height"] = scene.Height;
    }

    /// <summary>
    /// Runs the tool's <c>when tool &lt;event&gt;</c> (click, drag, release, used, key) and writes its changes to the scene.
    /// Returns false when the tool has no such event.
    /// </summary>
    public bool Fire(string toolEvent, double x = 0, double y = 0, string? key = null)
    {
        if (_scene == null) throw new InvalidOperationException("BeginUse first.");
        var whens = Tool.Def.Whens.Where(w => w.Event.Kind == EventKind.Tool && w.Event.Name == toolEvent
            && (toolEvent != "key" || string.Equals(w.Event.Filter, key, StringComparison.OrdinalIgnoreCase))).ToList();
        if (whens.Count == 0) return false;
        RefreshParams();
        _toolObject.X = x;
        _toolObject.Y = y;
        int errors = World.Errors.Count;
        foreach (var w in whens)
        {
            var locals = new Dictionary<string, object?>();
            if (w.Event.Bind is { } bind)
            {
                var names = bind.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (names.Length == 1) locals[names[0]] = new List<object?> { x, y };
                else { locals[names[0]] = x; locals[names[1]] = y; }
            }
            locals["selected"] = _map.FirstOrDefault(kv => kv.Value.Id == SelectedId && !kv.Key.Destroyed).Key;
            World.StartWhenWith(_inst, w, locals);
            // A tool that waits gets a few quick frames to finish; tools are meant to act at once.
            for (int i = 0; i < 300 && World.IsBusy(_inst); i++) World.Step(1.0 / 60);
            if (World.IsBusy(_inst))
            {
                World.CancelRunning(_inst);
                World.Errors.Add($"{Tool.Name}: a tool can't keep waiting — it was stopped. Tools should do their work at once.");
            }
            Sync();
        }
        for (int i = errors; i < World.Errors.Count; i++)
        {
            Errors.Add(World.Errors[i]);
            if (!World.Errors[i].StartsWith(Tool.Name + " (line")) Message?.Invoke("⚠ " + World.Errors[i]); // runtime errors were printed already
        }
        return true;
    }

    /// <summary>Ends the use. Returns the scene as it was before it (the one undo step) when the tool changed something, else null.</summary>
    public string? EndUse()
    {
        if (_scene == null) return null;
        string? before = _scene.ToJson() != _before ? _before : null;
        _scene = null;
        _before = null;
        World.ClearObjects();
        _map.Clear();
        _back.Clear();
        return before;
    }

    /// <summary>One whole use with one event (a button, a key, or a click in tests). Returns the undo snapshot or null.</summary>
    public string? Use(Scene scene, string toolEvent, double x = 0, double y = 0, string? key = null)
    {
        BeginUse(scene);
        Fire(toolEvent, x, y, key);
        return EndUse();
    }

    void RefreshParams()
    {
        foreach (var p in Tool.Def.Params)
            _inst.Vars[p.Name] = Params.TryGetValue(p.Name, out var v) ? World.ConvertParam(v, p.Type) : World.ParamDefaultFor(_inst, p);
    }

    /// <summary>The tool's world → the scene: moved, resized and erased objects (placed ones are added when placed).</summary>
    void Sync()
    {
        foreach (var (go, so) in _map.ToList())
        {
            if (go.Destroyed)
            {
                _scene!.Objects.Remove(so);
                _map.Remove(go);
                _back.Remove(so);
                if (so.Id == SelectedId) SelectedId = 0;
                continue;
            }
            if (double.IsFinite(go.X)) so.X = go.X;
            if (double.IsFinite(go.Y)) so.Y = go.Y;
            if (double.IsFinite(go.Width)) so.Width = Math.Max(1, go.Width);
            if (double.IsFinite(go.Height)) so.Height = Math.Max(1, go.Height);
        }
        World.DropDestroyed();
    }

    // ---------------- Commands that edit the scene ----------------

    void InstallCommands()
    {
        var c = World.Commands;
        c["place"] = Place;
        c["erase"] = Erase;
        c["fill_row"] = FillRow;
        c["select"] = cc =>
        {
            if (cc.Args.Count == 0 || World.WordAt(cc, 0) is "none" or "nothing") { SelectedId = 0; return; }
            if (cc.Arg(0) is GameObject g && _map.TryGetValue(g, out var so)) SelectedId = so.Id;
            else if (cc.Arg(0) is string s && Live().FirstOrDefault(o => o.Is(s)) is { } found) SelectedId = _map[found].Id;
            else throw cc.Error("'select' needs an object of the scene, like 'select placed' or 'select object_at(x, y)'.");
        };
        c["notify"] = cc => Message?.Invoke(string.Join(" ", cc.Args.Select(World.ToText)));

        var f = World.Functions;
        f["objects_at"] = a => ObjectsAt(Num(a, 0, "objects_at(x, y)"), Num(a, 1, "objects_at(x, y)")).Cast<object?>().ToList();
        f["object_at"] = a => ObjectsAt(Num(a, 0, "object_at(x, y)"), Num(a, 1, "object_at(x, y)")).FirstOrDefault();
        f["snap"] = a =>
        {
            double v = Num(a, 0, "snap(x, 32)"), step = a.Count > 1 ? Num(a, 1, "snap(x, 32)") : 8;
            if (step <= 0) throw new ScriptError("'snap' needs a grid size above 0, like snap(x, 32).", 0);
            return Math.Floor(v / step + 1e-9) * step;
        };
    }

    static double Num(List<object?> a, int i, string usage)
    {
        if (i >= a.Count) throw new ScriptError($"Write it like {usage}.", 0);
        return World.ToNumber(a[i], 0);
    }

    IEnumerable<GameObject> Live() => _map.Keys.Where(o => !o.Destroyed);

    /// <summary>The objects under a point, the top one (drawn last) first.</summary>
    List<GameObject> ObjectsAt(double x, double y)
    {
        var list = new List<GameObject>();
        foreach (var so in _scene!.Objects.AsEnumerable().Reverse())
        {
            if (!_back.TryGetValue(so, out var go) || go.Destroyed) continue;
            if (x >= go.X && x < go.X + go.Width && y >= go.Y && y < go.Y + go.Height) list.Add(go);
        }
        return list;
    }

    /// <summary>
    /// <c>place "Coin" at x, y</c> — a copy of the scene object called Coin (or "Coin 3", or tagged coin: a "prefab"
    /// with its mechanisms, params, size, colour, picture and tags) or, when there's none, a new object with the mechanism Coin. <c>with w, h</c> sets its
    /// size. Afterwards <c>placed</c> is the new object.
    /// </summary>
    void Place(CommandContext c)
    {
        if (c.Args.Count == 0) throw c.Error("Write what to place and where, like 'place \"Coin\" at x, y'.");
        var (x, y) = Where(c, "place \"Coin\" at x, y");
        var go = PlaceOne(c, c.Args[0], x, y, Size(c));
        c.Locals["placed"] = go;
    }

    GameObject PlaceOne(CommandContext c, object? what, double x, double y, (double W, double H)? size)
    {
        if (++_placed > EditorTools.MaxPlacedPerUse)
            throw c.Error($"This tool placed more than {EditorTools.MaxPlacedPerUse} objects at once — it stopped there.");
        var so = MakeObject(c, what);
        so.X = x;
        so.Y = y;
        if (size is { } s) { so.Width = Math.Max(1, s.W); so.Height = Math.Max(1, s.H); }
        so.Name = UniqueName(EditorTools.BaseName(so.Name));
        _scene!.Add(so);
        var go = World.AddStill(so.Name, so.X, so.Y, so.Width, so.Height, so.Tags);
        _map[go] = so;
        _back[so] = go;
        return go;
    }

    SceneObject MakeObject(CommandContext c, object? what)
    {
        if (what is GameObject g)
            return _map.TryGetValue(g, out var own) && !g.Destroyed ? own.Clone()
                : throw c.Error($"'{g.Name}' isn't in the scene, so it can't be copied.");
        string name = World.ToText(what).Trim();
        if (name.Length == 0) throw c.Error("Write what to place, like 'place \"Coin\" at x, y'.");
        var live = _scene!.Objects.Where(so => _back.TryGetValue(so, out var g) && !g.Destroyed).ToList();
        var prefab = live.FirstOrDefault(so => string.Equals(so.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? live.FirstOrDefault(so => string.Equals(EditorTools.BaseName(so.Name), name, StringComparison.OrdinalIgnoreCase))
            ?? live.FirstOrDefault(so => so.Tags.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (prefab != null) return prefab.Clone();
        if (World.Library.TryGetValue(name, out var def))
            return new SceneObject { Name = def.Name, Color = EditorTools.ColorFor(def.Name), Mechanisms = [new SceneMechanism { Name = def.Name }] };
        throw c.Error($"There's no object or mechanism called '{name}' to place.{World.Suggest(name, live.Select(o => o.Name).Concat(World.Library.Keys))}");
    }

    string UniqueName(string baseName)
    {
        bool Taken(string n) => _scene!.Objects.Any(o => string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(baseName)) return baseName;
        int i = 2;
        while (Taken($"{baseName} {i}")) i++;
        return $"{baseName} {i}";
    }

    static (double X, double Y) Where(CommandContext c, string usage)
    {
        if (c.Named.TryGetValue("at", out var at) && at.Count >= 2) return (c.Number(at[0]), c.Number(at[1]));
        if (c.Named.TryGetValue("at", out at) && at.Count == 1 && at[0] is List<object?> { Count: >= 2 } pos) return (c.Number(pos[0]), c.Number(pos[1]));
        throw c.Error($"Write where, like '{usage}'.");
    }

    static (double, double)? Size(CommandContext c)
    {
        if (!c.Named.TryGetValue("with", out var with)) return null;
        if (with.Count >= 2) return (c.Number(with[0]), c.Number(with[1]));
        if (with.Count == 1 && with[0] is List<object?> { Count: >= 2 } l) return (c.Number(l[0]), c.Number(l[1]));
        throw c.Error("After 'with' write the size: width, height (like 'with 32, 32').");
    }

    /// <summary><c>erase at x, y</c> (the top object there), <c>erase obj</c>, <c>erase all("coin")</c> or <c>erase "Coin 3"</c>.</summary>
    void Erase(CommandContext c)
    {
        if (c.Has("at"))
        {
            var (x, y) = Where(c, "erase at x, y");
            if (ObjectsAt(x, y).FirstOrDefault() is { } top) top.Destroyed = true;
            return;
        }
        if (c.Args.Count == 0) throw c.Error("Write what to erase: 'erase at x, y', 'erase placed' or 'erase all(\"coin\")'.");
        foreach (var a in c.Args)
            switch (a)
            {
                case GameObject g: if (_map.ContainsKey(g)) g.Destroyed = true; break;
                case List<object?> l: foreach (var g in l.OfType<GameObject>()) if (_map.ContainsKey(g)) g.Destroyed = true; break;
                case string s: foreach (var g in Live().Where(o => string.Equals(o.Name, s, StringComparison.OrdinalIgnoreCase)).ToList()) g.Destroyed = true; break;
                case null: break; // object_at(...) found nothing: nothing to erase
                default: throw c.Error("'erase' needs an object, a list of objects or 'at x, y'.");
            }
    }

    /// <summary><c>fill_row "Ground" from x1 to x2 at y</c>: copies side by side (each as wide as the first, or <c>with w, h</c>).</summary>
    void FillRow(CommandContext c)
    {
        if (c.Args.Count == 0 || !c.Has("from") || !c.Has("to") || !c.Has("at"))
            throw c.Error("Write it like 'fill_row \"Ground\" from x1 to x2 at y'.");
        double x1 = c.Number(c.NamedArg("from")), x2 = c.Number(c.NamedArg("to")), y = c.Number(c.NamedArg("at"));
        var size = Size(c);
        double left = Math.Min(x1, x2), right = Math.Max(x1, x2);
        var first = PlaceOne(c, c.Args[0], left, y, size);
        double step = Math.Max(1, first.Width);
        int n = Math.Max(1, (int)Math.Ceiling((right - left) / step - 1e-9));
        if (n > EditorTools.MaxPlacedPerUse) throw c.Error($"That row needs {n} objects — more than {EditorTools.MaxPlacedPerUse}.");
        var sz = (first.Width, first.Height);
        c.Locals["placed"] = first;
        for (int i = 1; i < n; i++) c.Locals["placed"] = PlaceOne(c, c.Args[0], left + i * step, y, sz);
    }
}
