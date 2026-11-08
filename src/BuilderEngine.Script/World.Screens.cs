using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

public sealed partial class GameObject
{
    /// <summary>The box's colour while playing ("#RRGGBB"); null = the colour it had in the scene.</summary>
    public string? Color;
    /// <summary>A text drawn in the middle of the box (a button's label, a card's number). {name} parts update every frame.</summary>
    public string? Caption;
    public string CaptionColor = "#ECECEC";
    public double CaptionSize = 16;
    internal string? CaptionTemplate;
    internal MechanismInstance? CaptionOwner;
    /// <summary>The screen (a group of objects shown and hidden together) it belongs to ('join screen "Menu"').</summary>
    public string? ScreenName;
}

/// <summary>
/// Screens, buttons and showing things:
/// <code>
/// ui                         (this object is drawn on the screen, not in the scene)        ui false
/// color "#E05D5D"            color Door to "#333333"
/// caption "Play" with "#111111", 20      caption "{hp}"     caption none
/// hide · hide Door · hide all("ghost")   show · show Door
/// tag self "in_hand"         untag self "in_hand"
/// broadcast "lights out"     (every object's 'when told "lights out"')
/// make Button at 300, 200 with 200, 48 as "Play"    (a new object; then 'made' is it: made.Button.text = "Play")
/// join screen "Menu"         show screen "Menu"   hide screen "Menu"   toggle screen "Menu"   hide screen   screen_shown("Menu")
/// camera view "Cam 3"        (the camera jumps to that object and fits its box)   camera view off   camera_view()
/// screen noise 0.4           screen noise 0.8 for 0.3   screen noise off     screen tint "#103018", 0.35   screen tint off
/// on_screen() · on_screen(Ghost)   (is it inside the camera's view?)
/// </code>
/// </summary>
public sealed partial class World
{
    /// <summary>Static over the whole view, 0..1 ('screen noise').</summary>
    public double NoiseAmount { get; private set; }
    double _noiseLeft, _noiseBase;
    public string TintColor { get; private set; } = "#000000";
    public double TintAmount { get; private set; }
    /// <summary>The object 'camera view' jumped to (null = the camera follows as usual).</summary>
    public GameObject? CameraView { get; private set; }
    readonly List<string> _screensShown = [];

    [ModuleInitializer]
    internal static void InstallScreens() => Installers.Add(w =>
    {
        w.Commands["ui"] = c =>
        {
            var (o, i) = TargetAndRest(c, 1);
            o.Ui = i >= c.Args.Count || (WordAt(c, i) is not ("off" or "false") && Truthy(c.Args[i]));
        };
        w.Commands["color"] = c =>
        {
            var (o, i) = TargetAndRest(c, 1);
            object? v = c.Has("to") ? c.NamedArg("to") : i < c.Args.Count ? c.Args[i] : null;
            o.Color = v is string s && s.Length > 0 ? s : null;
        };
        w.Commands["caption"] = CaptionCommand;
        w.Commands["hide"] = c => { foreach (var o in Targets(c)) o.Hidden = true; };
        w.Commands["show"] = c => { foreach (var o in Targets(c)) o.Hidden = false; };
        w.Commands["tag"] = c => TagCommand(c, add: true);
        w.Commands["untag"] = c => TagCommand(c, add: false);
        w.Commands["broadcast"] = c =>
        {
            if (c.Args.Count == 0) throw c.Error("'broadcast' needs a message: broadcast \"lights out\" (every object's 'when told \"lights out\"' runs).");
            c.World.Broadcast(ToText(c.Args[0]), c.Args.Skip(1).ToList());
        };
        w.Commands["make"] = MakeCommand;
        w.Commands["join screen"] = c =>
        {
            var (o, i) = TargetAndRest(c, 1);
            if (i >= c.Args.Count) throw c.Error("'join screen' needs the screen's name: join screen \"Menu\".");
            o.ScreenName = ToText(c.Args[i]);
            o.Hidden = !c.World.ScreenShown(o.ScreenName); // a screen's objects show when the screen does
        };
        w.Commands["show screen"] = c => c.World.ShowScreen(ScreenArg(c, "show screen"), true);
        w.Commands["hide screen"] = c =>
        {
            var world = c.World;
            if (c.Args.Count > 0) { world.ShowScreen(ToText(c.Args[0]), false); return; }
            if (world._screensShown.Count > 0) world.ShowScreen(world._screensShown[^1], false); // the last one shown
        };
        w.Commands["toggle screen"] = c =>
        {
            string name = ScreenArg(c, "toggle screen");
            c.World.ShowScreen(name, !c.World.ScreenShown(name));
        };
        w.Functions["screen_shown"] = a => a.Count > 0 && w.ScreenShown(ToText(a[0]));
        w.Commands["camera view"] = c =>
        {
            var world = c.World;
            if (WordAt(c, 0) is "off" or "none" || c.Args.Count == 0)
            {
                world.CameraView = null;
                world.ResetCamera();
                world.Camera.Placed = false; // jump back (to the player, or where the game started) instead of staying at the camera
                return;
            }
            var o = ObjectAt(c, 0) ?? throw c.Error($"There's no object called '{DescribeArg(c, 0)}' to look through. Put an object where the camera should look (its box = what it shows).");
            world.ViewThrough(o);
        };
        w.Functions["camera_view"] = _ => w.CameraView?.Name ?? "";
        w.Commands["screen noise"] = c =>
        {
            var world = c.World;
            if (WordAt(c, 0) is "off" or "none") { world.NoiseAmount = world._noiseBase = 0; world._noiseLeft = 0; return; }
            double v = c.Args.Count > 0 ? Math.Clamp(c.Number(c.Args[0]), 0, 1) : 0.3;
            double? secs = NumberAfterWord(c, "for");
            if (secs is > 0) { world._noiseLeft = secs.Value; world.NoiseAmount = v; }
            else { world._noiseBase = world.NoiseAmount = v; world._noiseLeft = 0; }
        };
        w.Commands["screen tint"] = c =>
        {
            var world = c.World;
            if (WordAt(c, 0) is "off" or "none" || c.Args.Count == 0) { world.TintAmount = 0; return; }
            world.TintColor = c.Args[0] is string s && s.Length > 0 ? s : "#000000";
            world.TintAmount = c.Args.Count > 1 ? Math.Clamp(c.Number(c.Args[1]), 0, 1) : 0.3;
        };
        w.Functions["on_screen"] = a =>
        {
            var o = a.Count > 0 ? a[0] as GameObject ?? (a[0] is string s ? w.FindObject(s) : null) : w.CallingInst?.Owner;
            return o != null && w.OnScreen(o);
        };
    });

    // ---------------- Commands ----------------

    /// <summary>The objects a hide/show names: none = this object, an object, a name, or a list.</summary>
    static IEnumerable<GameObject> Targets(CommandContext c) => c.Args.Count == 0 ? [c.Self] : ObjectsIn(c, 0).ToList();

    static string ScreenArg(CommandContext c, string what) =>
        c.Args.Count > 0 ? ToText(c.Args[0]) : throw c.Error($"'{what}' needs the screen's name, like {what} \"Menu\".");

    static void CaptionCommand(CommandContext c)
    {
        var (o, i) = TargetAndRest(c, 1);
        if (i >= c.Args.Count || WordAt(c, i) is "none" or "off")
        {
            o.Caption = o.CaptionTemplate = null;
            return;
        }
        string text = ToText(c.Args[i]);
        if (c.Named.TryGetValue("with", out var with))
            foreach (var v in with)
                if (v is string col && col.Length > 0) o.CaptionColor = col;
                else if (v is double size) o.CaptionSize = Math.Clamp(size, 4, 200);
        o.CaptionTemplate = text.IndexOf('{') >= 0 ? text : null;
        o.CaptionOwner = c.Mechanism;
        o.Caption = c.World.Interpolate(text, c.Mechanism, c.Locals);
    }

    static void TagCommand(CommandContext c, bool add)
    {
        string word = add ? "tag" : "untag";
        if (c.Args.Count == 0) throw c.Error($"'{word}' needs a tag: {word} self \"in_hand\".");
        var (o, i) = c.Args.Count >= 2 ? (ObjectAt(c, 0) ?? throw c.Error($"There's no object called '{DescribeArg(c, 0)}'."), 1) : (c.Self, 0);
        for (; i < c.Args.Count; i++)
        {
            string t = ToText(c.Args[i]).Trim();
            if (t.Length == 0) continue;
            if (add) o.Tags.Add(t); else o.Tags.Remove(t);
            if (string.Equals(t, "ui", StringComparison.OrdinalIgnoreCase)) o.Ui = add;
        }
    }

    /// <summary>Tells every object (in the world's order) the message — their 'when told' runs.</summary>
    public void Broadcast(string message, List<object?> args)
    {
        for (int i = 0, n = _objects.Count; i < n && i < _objects.Count; i++)
            if (!_objects[i].Destroyed) Tell(_objects[i], message, args);
        foreach (var o in _pendingAdd.ToArray())
            if (!o.Destroyed) Tell(o, message, args);
    }

    static void MakeCommand(CommandContext c)
    {
        if (c.Args.Count == 0) throw c.Error("'make' needs a mechanism: make Button at 300, 200 with 200, 48 (then 'made' is the new object).");
        var w = c.World;
        string mech = ToText(c.Args[0]);
        if (w.TryDef(mech, c.Line) == null) throw c.Error($"There's no mechanism called '{mech}'.{Suggest(mech, w.Library.Keys)}");
        double x = c.Self.X, y = c.Self.Y;
        if (c.Named.TryGetValue("at", out var at))
        {
            if (at.Count == 1 && at[0] is GameObject g) { x = g.X; y = g.Y; }
            else if (at.Count >= 2) { x = c.Number(at[0]); y = c.Number(at[1]); }
        }
        string name = c.Named.TryGetValue("as", out var nm) && nm.Count > 0 ? ToText(nm[0]) : mech;
        var o = w.Spawn(name, x, y, [mech]);
        if (c.Named.TryGetValue("with", out var size) && size.Count >= 2) { o.Width = Math.Max(1, c.Number(size[0])); o.Height = Math.Max(1, c.Number(size[1])); }
        if (c.Self.Ui) o.Ui = true; // made by something on the screen: on the screen too
        if (c.Self.ScreenName != null) { o.ScreenName = c.Self.ScreenName; o.Hidden = !w.ScreenShown(o.ScreenName); }
        c.Locals["made"] = o;
    }

    // ---------------- Screens ----------------

    public bool ScreenShown(string name) => _screensShown.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Shows or hides every object of a screen (joined with 'join screen', or tagged with its name).</summary>
    public void ShowScreen(string name, bool show)
    {
        foreach (var o in LiveObjects())
            if (string.Equals(o.ScreenName, name, StringComparison.OrdinalIgnoreCase) || o.Tags.Contains(name)) o.Hidden = !show;
        _screensShown.RemoveAll(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
        if (show) _screensShown.Add(name);
    }

    // ---------------- Camera views and screen effects ----------------

    /// <summary>The camera jumps to an object's middle and zooms so its box fills the view (a security camera).</summary>
    public void ViewThrough(GameObject o)
    {
        var cam = Camera;
        CameraView = o;
        cam.Auto = false;
        cam.Target = null;
        cam.MoveTween++;
        cam.ZoomTween++;
        cam.Limited = false;
        if (o.Width > 1 && o.Height > 1) cam.Zoom = Math.Clamp(Math.Min(cam.BaseWidth / o.Width, cam.BaseHeight / o.Height), 0.1, 10);
        foreach (var m in o.Flat)
            if (m.Vars.TryGetValue("zoom", out var z) && z is double dz && dz > 0) cam.Zoom = Math.Clamp(dz, 0.1, 10);
        cam.X = o.X + o.Width / 2;
        cam.Y = o.Y + o.Height / 2;
        cam.Placed = true;
    }

    /// <summary>Is the object inside what the camera shows now? (objects on the screen: when shown)</summary>
    public bool OnScreen(GameObject o)
    {
        if (o.Destroyed) return false;
        if (o.Ui) return o.Shown;
        var cam = Camera;
        return o.X < cam.Left + cam.ViewWidth && o.X + o.Width > cam.Left && o.Y < cam.Top + cam.ViewHeight && o.Y + o.Height > cam.Top;
    }

    void UpdateEffects(double realDt)
    {
        if (_noiseLeft > 0 && (_noiseLeft -= realDt) <= 0) { _noiseLeft = 0; NoiseAmount = _noiseBase; }
        for (int i = 0; i < _objects.Count; i++)
        {
            var o = _objects[i];
            if (o.CaptionTemplate != null && o.CaptionOwner != null && !o.Destroyed) o.Caption = Interpolate(o.CaptionTemplate, o.CaptionOwner);
        }
    }

    // ---------------- self.hidden, self.color… ----------------

    /// <summary>Extra things a script can read on an object: hidden, ui, color, caption, layer, opacity, picture, light, hovered…</summary>
    internal bool TryGameMember(GameObject g, string member, out object? value)
    {
        value = member switch
        {
            "hidden" => g.Hidden,
            "shown" or "visible" => g.Shown,
            "ui" => g.Ui,
            "color" => g.Color ?? "",
            "caption" => g.Caption ?? "",
            "layer" or "z" => g.Z,
            "opacity" => g.Opacity,
            "picture" or "image" => g.Image ?? "",
            "light" => g.LightRadius,
            "draggable" => g.Draggable,
            "hovered" => Hovered == g,
            "dragging" => Dragging == g,
            "screen" => g.ScreenName ?? "",
            "tags" => g.Tags.Cast<object?>().ToList(),
            _ => Missing,
        };
        if (ReferenceEquals(value, Missing)) { value = null; return false; }
        return true;
    }

    static readonly object Missing = new();

    /// <summary>Changes one of those (true when the member is one of them).</summary>
    internal bool SetGameMember(GameObject g, string member, string op, object? value, int line)
    {
        if (member is "x" or "y" or "width" or "height" or "vx" or "vy") return false;
        if (!TryGameMember(g, member, out var cur)) return false;
        var v = Combine(op, cur, value, line);
        switch (member)
        {
            case "hidden": g.Hidden = Truthy(v); break;
            case "shown" or "visible": g.Hidden = !Truthy(v); break;
            case "ui": g.Ui = Truthy(v); break;
            case "color": g.Color = v is string s && s.Length > 0 ? s : null; break;
            case "caption": g.Caption = ToText(v); g.CaptionTemplate = null; break;
            case "layer" or "z": g.Z = ToNumber(v, line); break;
            case "opacity": g.Opacity = Math.Clamp(ToNumber(v, line), 0, 1); break;
            case "picture" or "image": g.Image = ToText(v) is { Length: > 0 } p ? p : null; break;
            case "light": g.LightRadius = Math.Max(0, ToNumber(v, line)); break;
            case "draggable": g.Draggable = Truthy(v); break;
            default: throw new ScriptError($"'{member}' of an object can only be read, not changed.", line);
        }
        return true;
    }
}
