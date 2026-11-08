using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

public sealed partial class GameObject
{
    /// <summary>
    /// Drawn on the screen (like the HUD) instead of in the scene: the camera doesn't move it, and the mouse clicks it in
    /// screen pixels. 'ui' / 'ui false' in a script, or the tag "ui".
    /// </summary>
    public bool Ui;
    /// <summary>'hide' / 'show': not drawn and not clicked (it still moves and touches).</summary>
    public bool Hidden;
    /// <summary>The mouse can pick it up and drop it ('draggable').</summary>
    public bool Draggable;
    /// <summary>The tag of the places it may be dropped on (null = anywhere): elsewhere it glides back.</summary>
    public string? DropZone;

    /// <summary>Drawn now: not hidden, and no mechanism of it has <c>visible</c> set to false.</summary>
    public bool Shown
    {
        get
        {
            if (Hidden || Destroyed) return false;
            foreach (var m in Flat)
                if (m.Vars.TryGetValue("visible", out var v) && v is false) return false;
            return true;
        }
    }
}

/// <summary>
/// The mouse in the game:
/// <code>
/// mouse_x, mouse_y               (scene pixels under the pointer, camera included)   mouse_screen_x, mouse_screen_y
/// mouse_down()  mouse_down("right")   mouse_over()  mouse_over(Boss)   mouse_over_ui()   under_mouse()
/// when clicked                   (this object, the top one under the pointer)
/// when hovered / when unhovered  (the pointer came over it / left it)
/// when mouse pressed at x, y     when mouse released at x, y      when mouse pressed "right"   (anywhere)
/// draggable                      draggable to "slot"   (only onto things tagged slot: it snaps into them, elsewhere it glides back)   draggable off
/// when picked up                 when dropped          when dropped on "slot"   (other = the slot)   when received "card"  (on the slot)
/// cursor "hand"                  cursor "cross" · "none" · "move" · "text" · "wait" · cursor default
/// </code>
/// Objects drawn on the screen ('ui' or the tag "ui") are on top and clicked in screen pixels.
/// </summary>
public sealed partial class World
{
    /// <summary>The pointer in view pixels (0..the camera's base width/height).</summary>
    public double MouseViewX { get; private set; }
    public double MouseViewY { get; private set; }
    public bool MouseInView { get; private set; }
    readonly HashSet<string> _buttons = new(StringComparer.OrdinalIgnoreCase);
    public GameObject? Hovered { get; private set; }
    public GameObject? Dragging { get; private set; }
    double _dragOffX, _dragOffY, _dragStartX, _dragStartY;
    /// <summary>The pointer's look asked by 'cursor' ("default", "hand", "cross", "none", "move", "text", "wait").</summary>
    public string Cursor { get; set; } = "default";

    /// <summary>The look to show now: the asked one, or a hand over something clickable.</summary>
    public string CursorNow => Cursor == "default" && (Hovered != null || Dragging != null) ? (Dragging != null ? "move" : "hand") : Cursor;

    /// <summary>The pointer in scene pixels (camera position and zoom included).</summary>
    public double MouseX => Camera.Left + MouseViewX / Camera.Zoom;
    public double MouseY => Camera.Top + MouseViewY / Camera.Zoom;

    sealed class Glide
    {
        public GameObject O = null!;
        public double X0, Y0, X1, Y1, T0, Seconds;
        /// <summary>The object's move tween when the glide started: a script's move or arrange after that takes over.</summary>
        public int Tween;
    }
    readonly List<Glide> _glides = [];

    [ModuleInitializer]
    internal static void InstallMouse() => Installers.Add(w =>
    {
        w.EngineValues["mouse_x"] = x => x.MouseX;
        w.EngineValues["mouse_y"] = x => x.MouseY;
        w.EngineValues["mouse_screen_x"] = x => x.MouseViewX;
        w.EngineValues["mouse_screen_y"] = x => x.MouseViewY;
        w.Functions["mouse_down"] = a => !w.ControlsLocked && w._buttons.Contains(a.Count > 0 ? ToText(a[0]) : "left");
        w.Functions["mouse_over"] = a =>
        {
            var o = a.Count > 0 ? a[0] as GameObject ?? (a[0] is string s ? w.FindObject(s) : null) : w.CallingInst?.Owner;
            return o != null && w.MouseInView && o.Shown && w.PointerIn(o);
        };
        w.Functions["mouse_over_ui"] = _ =>
        {
            if (!w.MouseInView) return false;
            foreach (var o in w._objects)
                if (o.Ui && o.Shown && w.PointerIn(o)) return true;
            return false;
        };
        w.Functions["under_mouse"] = _ => w.ObjectAtPointer(onlyMouseAware: false);
        w.Commands["cursor"] = c =>
        {
            string look = c.Args.Count > 0 && c.Args[0] is string s && s.Length > 0 ? s : WordAt(c, 0) ?? "default";
            c.World.Cursor = look.ToLowerInvariant() switch
            {
                "pointer" or "hand" or "finger" => "hand",
                "cross" or "crosshair" or "aim" => "cross",
                "none" or "off" or "hidden" => "none",
                "move" or "grab" or "drag" => "move",
                "text" or "ibeam" => "text",
                "wait" or "busy" => "wait",
                _ => "default"
            };
        };
        w.Commands["draggable"] = c =>
        {
            var (o, i) = TargetAndRest(c, 1);
            if (WordAt(c, i) is "off" or "false" || (i < c.Args.Count && c.Args[i] is false))
            {
                o.Draggable = false;
                if (c.World.Dragging == o) c.World.Dragging = null;
                return;
            }
            o.Draggable = true;
            o.DropZone = c.Has("to") && c.NamedArg("to") is { } z && ToText(z).Length > 0 ? ToText(z) : null;
        };
    });

    // ---------------- From the host ----------------

    /// <summary>The pointer moved (view pixels from the top-left of the game's view). <paramref name="inside"/> false = it left the game.</summary>
    public void MouseMove(double viewX, double viewY, bool inside = true)
    {
        MouseViewX = viewX;
        MouseViewY = viewY;
        MouseInView = inside;
    }

    /// <summary>A mouse button went down ("left", "right", "middle").</summary>
    public void MouseDown(string button = "left")
    {
        button = button.ToLowerInvariant();
        if (!_buttons.Add(button) || ControlsLocked || !MouseInView) return;
        var target = ObjectAtPointer(onlyMouseAware: true);
        if (button == "left" && target != null)
        {
            FireGame("clicked", only: target);
            if (target.Draggable && !target.Destroyed) StartDrag(target);
        }
        FireGame("mouse pressed", filter: button, x: MouseX, y: MouseY);
    }

    public void MouseUp(string button = "left")
    {
        button = button.ToLowerInvariant();
        if (!_buttons.Remove(button)) return;
        if (button == "left" && Dragging != null) Drop();
        if (!ControlsLocked) FireGame("mouse released", filter: button, x: MouseX, y: MouseY);
    }

    /// <summary>The wheel turned (+ = away from you). With a hotbar on screen it picks the slot.</summary>
    public void MouseWheel(double delta)
    {
        if (delta == 0 || ControlsLocked) return;
        if (Hotbar.Visible && Hotbar.Slots > 0)
            Hotbar.Selected = ((Hotbar.Selected + (delta > 0 ? -1 : 1)) % Hotbar.Slots + Hotbar.Slots) % Hotbar.Slots;
    }

    /// <summary>Every button let go (the window lost the mouse).</summary>
    public void ReleaseMouse()
    {
        if (Dragging != null) Drop();
        _buttons.Clear();
    }

    // ---------------- Finding what is under the pointer ----------------

    /// <summary>Is the pointer inside the object's box (screen pixels for objects on the screen, scene pixels for the others)?</summary>
    internal bool PointerIn(GameObject o)
    {
        double x = o.Ui ? MouseViewX : MouseX, y = o.Ui ? MouseViewY : MouseY;
        return x >= o.X && x <= o.X + o.Width && y >= o.Y && y <= o.Y + o.Height;
    }

    /// <summary>The object drawn on top under the pointer: screen objects first, then the higher layer, then the later one.</summary>
    public GameObject? ObjectAtPointer(bool onlyMouseAware)
    {
        if (!MouseInView) return null;
        GameObject? best = null;
        int bestIndex = -1;
        for (int i = 0; i < _objects.Count; i++)
        {
            var o = _objects[i];
            if (o.Destroyed || o == Dragging || (onlyMouseAware && !o.MouseAware) || !PointerIn(o) || !o.Shown) continue;
            if (best == null || Above(o, i, best, bestIndex)) { best = o; bestIndex = i; }
        }
        return best;
    }

    static bool Above(GameObject a, int ai, GameObject b, int bi) =>
        a.Ui != b.Ui ? a.Ui : a.Z != b.Z ? a.Z > b.Z : ai > bi;

    // ---------------- Hover, drag, glide (every frame) ----------------

    void UpdateMouse(double realDt)
    {
        if (Dragging is { } d)
        {
            if (d.Destroyed || !d.Draggable) Dragging = null;
            else
            {
                d.X = (d.Ui ? MouseViewX : MouseX) + _dragOffX;
                d.Y = (d.Ui ? MouseViewY : MouseY) + _dragOffY;
                d.Vx = d.Vy = 0;
            }
        }
        var now = ControlsLocked ? null : ObjectAtPointer(onlyMouseAware: true);
        if (Dragging != null) now = Dragging;
        if (now != Hovered)
        {
            var old = Hovered;
            Hovered = now;
            if (old != null && !old.Destroyed) FireGame("unhovered", only: old);
            if (now != null) FireGame("hovered", only: now);
        }
        for (int i = _glides.Count - 1; i >= 0; i--)
        {
            var g = _glides[i];
            double k = g.Seconds <= 0 ? 1 : Math.Clamp((RealTime - g.T0) / g.Seconds, 0, 1);
            double e = Ease("out", k);
            if (g.O.Destroyed || g.O == Dragging || g.O.MoveTween != g.Tween) { _glides.RemoveAt(i); continue; }
            g.O.X = g.X0 + (g.X1 - g.X0) * e;
            g.O.Y = g.Y0 + (g.Y1 - g.Y0) * e;
            if (k >= 1) _glides.RemoveAt(i);
        }
    }

    void StartDrag(GameObject o)
    {
        _glides.RemoveAll(g => g.O == o);
        BringToFront(o);
        Dragging = o;
        _dragStartX = o.X; _dragStartY = o.Y;
        _dragOffX = o.X - (o.Ui ? MouseViewX : MouseX);
        _dragOffY = o.Y - (o.Ui ? MouseViewY : MouseY);
        o.Vx = o.Vy = 0;
        FireGame("picked up", only: o);
    }

    void Drop()
    {
        var d = Dragging!;
        Dragging = null;
        if (d.Destroyed) return;
        double cx = d.X + d.Width / 2, cy = d.Y + d.Height / 2;
        GameObject? zone = null;
        int zi = -1;
        for (int i = 0; i < _objects.Count; i++)
        {
            var o = _objects[i];
            if (o == d || o.Destroyed || o.Ui != d.Ui || !o.Shown) continue;
            if (cx < o.X || cx > o.X + o.Width || cy < o.Y || cy > o.Y + o.Height) continue;
            if (d.DropZone != null && !o.Is(d.DropZone)) continue;
            if (zone == null || Above(o, i, zone, zi)) { zone = o; zi = i; }
        }
        if (d.DropZone != null)
        {
            if (zone != null) GlideTo(d, zone.X + (zone.Width - d.Width) / 2, zone.Y + (zone.Height - d.Height) / 2, 0.12);
            else GlideTo(d, _dragStartX, _dragStartY, 0.25);
        }
        FireGame("dropped", only: d, filterObject: zone, other: zone);
        if (zone != null) FireGame("received", only: zone, filterObject: d, other: d);
    }

    /// <summary>Drawn (and clicked) above the others of its layer: the card in your hand is on top.</summary>
    void BringToFront(GameObject o)
    {
        int i = _objects.IndexOf(o);
        if (i < 0 || i == _objects.Count - 1) return;
        _objects.RemoveAt(i);
        _objects.Add(o);
    }

    /// <summary>Moves an object smoothly to a place (real time; doesn't wait).</summary>
    internal void GlideTo(GameObject o, double x, double y, double seconds)
    {
        _glides.RemoveAll(g => g.O == o);
        if (seconds <= 0) { o.X = x; o.Y = y; return; }
        _glides.Add(new Glide { O = o, X0 = o.X, Y0 = o.Y, X1 = x, Y1 = y, T0 = RealTime, Seconds = seconds, Tween = o.MoveTween });
    }
}
