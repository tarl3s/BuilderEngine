namespace BuilderEngine.Script;

public sealed partial class GameObject
{
    /// <summary>The object's named animations (from the scene object or prefab it came from), or null.</summary>
    public List<SpriteAnimation>? Animations;

    /// <summary>The prefab this object was made from (placed in the scene or spawned), or null.</summary>
    public string? Prefab;

    /// <summary>The playing animation's frames per second.</summary>
    public double AnimationFps => AnimFps;

    public SpriteAnimation? FindAnimation(string name)
    {
        if (Animations == null) return null;
        foreach (var a in Animations)
            if (string.Equals(a.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }
}

/// <summary>
/// Prefabs while playing: <c>spawn "Slime"</c> makes a copy of the prefab Slime (its mechanisms with their params,
/// picture, size, colour, tags and animations) — a prefab is found before a mechanism with the same name; with no
/// prefab, <c>spawn Coin</c> still makes an object with the mechanism Coin. Also: each object's named animations
/// start their default one when the object appears (<see cref="StartDefaultAnimation"/>).
/// The host gives the world the project's prefabs with <see cref="UsePrefabs"/>.
/// </summary>
public sealed partial class World
{
    /// <summary>Prefab name → what a copy looks like (the prefab's template). Kept by <see cref="ForNextScene"/>.</summary>
    public Dictionary<string, SceneObject> Prefabs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lets scripts spawn these prefabs by name.</summary>
    public void UsePrefabs(IEnumerable<Prefab> prefabs)
    {
        Prefabs.Clear();
        foreach (var p in prefabs)
            if (!string.IsNullOrWhiteSpace(p.Name)) Prefabs[p.Name.Trim()] = p.Template;
    }

    /// <summary>
    /// A copy of the prefab <paramref name="name"/> at x, y, or null when there's no such prefab. Its mechanisms run
    /// 'when start' on the next step like any spawned object. A mechanism it can't use is an error.
    /// </summary>
    public GameObject? SpawnPrefab(string name, double x, double y, int line = 0)
    {
        if (!Prefabs.TryGetValue(name.Trim(), out var t)) return null;
        var names = new List<string>();
        var overrides = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in t.Mechanisms)
        {
            var def = TryDef(m.Name, line)
                ?? throw new ScriptError($"The prefab '{name}' uses '{m.Name}', but there's no mechanism with that name.{Suggest(m.Name, Library.Keys)}", line);
            if (names.Contains(def.Name, StringComparer.OrdinalIgnoreCase)) continue;
            names.Add(def.Name);
            var ov = new Dictionary<string, object?>();
            foreach (var (k, v) in m.Params)
                if (def.Params.FirstOrDefault(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase)) is { } p) ov[p.Name] = ConvertParam(v, p.Type);
            overrides[def.Name] = ov;
        }
        var o = Spawn(name.Trim(), x, y, names, overrides, t.Tags.ToArray());
        o.Width = t.Width;
        o.Height = t.Height;
        o.Color = t.Color;
        if (!string.IsNullOrWhiteSpace(t.Image)) o.Image = t.Image.Trim();
        if (o.Tags.Contains("ui")) o.Ui = true;
        o.Prefab = name.Trim();
        LoadAnimations(o, t);
        foreach (var inst in o.AllMechanisms()) ResolveObjectParams(inst);
        return o;
    }

    /// <summary>A scene object's (or prefab's) named animations go to its runtime object, and the default one starts.</summary>
    internal void LoadAnimations(GameObject o, SceneObject so)
    {
        if (!string.IsNullOrEmpty(so.Prefab)) o.Prefab = so.Prefab;
        if (so.Animations.Count == 0) return;
        o.Animations = so.Animations.Select(a => a.Clone()).ToList();
        StartDefaultAnimation(o, so.ResolveDefaultAnimation()?.Name);
    }

    /// <summary>Plays an object's default animation (by name, else its first) from the start.</summary>
    public void StartDefaultAnimation(GameObject o, string? defaultName = null)
    {
        if (o.Animations is not { Count: > 0 } list) return;
        var a = (defaultName != null ? o.FindAnimation(defaultName) : null) ?? list[0];
        if (a.Frames.Count > 0) PlayNamed(o, a, null, once: !a.Loop, restart: true);
    }

    /// <summary>
    /// Starts a named animation (its frames, its speed unless <paramref name="fps"/> is given, once when it doesn't loop).
    /// Asked again while it plays (a 'when tick'), it keeps going — a finished one-time animation stays on its last
    /// frame — unless <paramref name="restart"/> ('animate "attack" once' always plays it from the start).
    /// Returns the key the animation runs under (to tell later whether it is still the one playing).
    /// </summary>
    internal string PlayNamed(GameObject o, SpriteAnimation a, double? fps, bool once, bool restart)
    {
        string key = $"@{a.Name}|{once}";
        double speed = fps is > 0 ? fps.Value : Math.Max(0.1, a.Fps);
        if (o.AnimName == key && !restart) { o.AnimFps = speed; return key; } // already playing: keep going
        foreach (var f in a.Frames) CheckPicture(f);
        o.AnimName = key;
        o.AnimFrames = a.Frames;
        o.AnimCount = a.Frames.Count;
        o.AnimFps = speed;
        o.AnimStart = Time;
        o.AnimOnce = once;
        o.AnimDone = false;
        ApplyFrame(o, 0);
        return key;
    }
}
