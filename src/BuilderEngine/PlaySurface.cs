using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// Runs a <see cref="World"/> at a fixed 60 steps per second and draws it scaled to fit: objects as coloured
/// rectangles, global variables top-right, a red badge with the runtime error count. Takes the keyboard while playing.
/// </summary>
sealed class PlaySurface : Control, ICustomHitTest
{
    const double Step = 1.0 / 60;
    readonly World _world;
    public World World => _world;
    readonly Scene _scene;
    readonly Dictionary<GameObject, IBrush> _brushes = new();
    /// <summary>Colour for objects created while playing (by 'spawn'): the colour of a scene object with that mechanism.</summary>
    readonly Dictionary<string, IBrush> _byMechanism = new(StringComparer.OrdinalIgnoreCase);
    readonly StringBuilder _hud = new();
    string _hudText = "";
    FormattedText? _hudFt;
    int _errorsShown = -1;
    FormattedText? _errorFt;
    TimeSpan? _last;
    double _acc;
    bool _running;

    public Action? StopRequested;
    public Action<string>? Crashed;

    public PlaySurface(World world, Scene scene, Dictionary<GameObject, SceneObject> spawned)
    {
        _world = world;
        _scene = scene;
        Focusable = true;
        ClipToBounds = true;
        foreach (var (g, so) in spawned)
        {
            var b = SceneCanvas.BrushOf(so.Color, Palette.Accent);
            _brushes[g] = b;
            foreach (var m in so.Mechanisms) _byMechanism.TryAdd(m.Name, b);
        }
    }

    public bool HitTest(Point point) => true;

    public void Start()
    {
        _running = true;
        _last = null;
        RequestFrame();
        Focus();
    }

    bool _framePending;

    void RequestFrame()
    {
        if (_framePending || TopLevel.GetTopLevel(this) is not { } top) return;
        _framePending = true;
        top.RequestAnimationFrame(OnFrame);
    }

    /// <summary>Shown again after the person looked at another tab: the game goes on.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_running) { _last = null; RequestFrame(); }
    }

    public void Halt() => _running = false;

    void OnFrame(TimeSpan now)
    {
        _framePending = false;
        if (!_running || TopLevel.GetTopLevel(this) == null) return;
        double dt = _last == null ? Step : (now - _last.Value).TotalSeconds;
        _last = now;
        _acc += Math.Clamp(dt, 0, 0.25);
        int steps = 0;
        try
        {
            while (_acc >= Step && steps < 5)
            {
                _world.Step(Step);
                _acc -= Step;
                steps++;
            }
        }
        catch (Exception ex)
        {
            _running = false;
            Crashed?.Invoke(ex.Message);
            return;
        }
        if (steps == 5) _acc = 0; // too slow: drop the backlog instead of spiralling
        InvalidateVisual();
        RequestFrame();
    }

    // ---------------- Drawing ----------------

    static readonly Typeface HudFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    static readonly IBrush HudBg = new SolidColorBrush(Color.FromArgb(200, 11, 11, 11)).ToImmutable();
    static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#A3A3A3")).ToImmutable();

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(Palette.Bg0, new Rect(size));
        double scale = Math.Min(size.Width / _scene.Width, size.Height / _scene.Height);
        if (scale <= 0 || !double.IsFinite(scale)) return;
        double ox = (size.Width - _scene.Width * scale) / 2, oy = (size.Height - _scene.Height * scale) / 2;
        var area = new Rect(ox, oy, _scene.Width * scale, _scene.Height * scale);
        ctx.FillRectangle(SceneCanvas.BrushOf(_scene.Background, Palette.Bg1), area);

        using (ctx.PushClip(area))
        {
            var objs = _world.Objects;
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                if (o.Destroyed || !Shown(o)) continue;
                var r = new Rect(ox + o.X * scale, oy + o.Y * scale, Math.Max(0, o.Width * scale), Math.Max(0, o.Height * scale));
                double rad = Math.Min(6, Math.Min(r.Width, r.Height) / 4);
                ctx.DrawRectangle(BrushFor(o), null, r, rad, rad);
            }
        }

        DrawHud(ctx, size);
        DrawErrors(ctx);
    }

    IBrush BrushFor(GameObject o)
    {
        if (_brushes.TryGetValue(o, out var b)) return b;
        b = o.Mechanisms.Count > 0 && _byMechanism.TryGetValue(o.Mechanisms[0].Def.Name, out var mb) ? mb : Fallback;
        _brushes[o] = b;
        return b;
    }

    /// <summary>Hidden when any of its mechanisms has <c>visible</c> set to false.</summary>
    static bool Shown(GameObject o)
    {
        foreach (var m in o.Mechanisms)
            if (!Shown(m)) return false;
        return true;
    }

    static bool Shown(MechanismInstance m)
    {
        if (m.Vars.TryGetValue("visible", out var v) && v is bool b && !b) return false;
        foreach (var c in m.Children.Values)
            if (!Shown(c)) return false;
        return true;
    }

    void DrawHud(DrawingContext ctx, Size size)
    {
        if (_world.Globals.Count == 0) return;
        _hud.Clear();
        foreach (var (k, v) in _world.Globals)
        {
            if (_hud.Length > 0) _hud.Append("    ");
            _hud.Append(k).Append("  ").Append(v is double d ? d.ToString("0.##", CultureInfo.InvariantCulture) : World.ToText(v));
        }
        if (_hudFt == null || !_hud.Equals(_hudText.AsSpan()))
        {
            _hudText = _hud.ToString();
            _hudFt = new FormattedText(_hudText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HudFont, 15, Palette.Text);
        }
        var at = new Point(size.Width - _hudFt.Width - 20, 14);
        ctx.DrawRectangle(HudBg, null, new Rect(at.X - 10, at.Y - 5, _hudFt.Width + 20, _hudFt.Height + 10), 8, 8);
        ctx.DrawText(_hudFt, at);
    }

    void DrawErrors(DrawingContext ctx)
    {
        int n = _world.Errors.Count;
        if (n == 0) return;
        if (n != _errorsShown)
        {
            _errorsShown = n;
            _errorFt = new FormattedText(n == 1 ? "1 error — see the console" : $"{n} errors — see the console", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, HudFont, 12, Palette.AccentText);
        }
        var at = new Point(24, 16);
        ctx.DrawRectangle(Palette.Error, null, new Rect(at.X - 10, at.Y - 4, _errorFt!.Width + 20, _errorFt.Height + 8), 10, 10);
        ctx.DrawText(_errorFt, at);
    }

    // ---------------- Keyboard ----------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = true;
        if (e.Key == Key.Escape) { StopRequested?.Invoke(); return; }
        string k = MainWindow.KeyName(e.Key);
        if (_world.KeysDown.Add(k)) _world.PressKey(k); // auto-repeat doesn't press again
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _world.KeysDown.Remove(MainWindow.KeyName(e.Key));
        e.Handled = true;
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _world.KeysDown.Clear();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }
}
