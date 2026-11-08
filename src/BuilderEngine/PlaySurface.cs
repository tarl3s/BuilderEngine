using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// Runs a <see cref="World"/> at a fixed 60 frames per second and draws it through its camera, scaled to fit: objects
/// as their pictures (or coloured boxes), then the HUD (texts and bars), screen fades and flashes, the dialogue panel,
/// messages, the global variables top-right (when the game shows no texts of its own) and a red badge with the error
/// count. Takes the keyboard while playing; keys go through the world, which handles locked controls and dialogues.
/// </summary>
sealed partial class PlaySurface : Control, ICustomHitTest
{
    const double Step = 1.0 / 60;
    readonly World _world;
    public World World => _world;
    readonly Scene _scene;
    readonly ImageCache _images;
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
    // Reused every frame (no allocations while nothing changes).
    readonly List<GameObject> _order = [];
    readonly Dictionary<HudItem, (string Text, double Size, string Color, FormattedText Ft)> _hudTexts = new();
    readonly List<Rect> _optionRects = [];
    readonly SolidColorBrush _fadeBrush = new(Colors.Black), _flashBrush = new(Colors.White), _barBrush = new(Colors.Red);
    readonly Dictionary<string, Color> _colors = new(StringComparer.OrdinalIgnoreCase);

    public Action? StopRequested;
    public Action<string>? Crashed;
    /// <summary>The debugger watching this game (draws "paused", F5 pauses / resumes, F10 steps).</summary>
    public Debugger? Debugger;
    /// <summary>The object the Debug panel looks inside (outlined).</summary>
    public GameObject? Inspected;
    /// <summary>A click on the game that isn't for a dialogue: the object under the pointer, or null.</summary>
    public Action<GameObject?>? ObjectClicked;

    public PlaySurface(World world, Scene scene, Dictionary<GameObject, SceneObject> spawned, ImageCache? images = null)
    {
        _world = world;
        _scene = scene;
        _images = images ?? new ImageCache(world.ProjectFolder ?? "");
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
                _world.Frame(Step);
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
        UpdateCursor(); // PlaySurface.GameKit.cs
        InvalidateVisual();
        RequestFrame();
    }

    // ---------------- Drawing ----------------

    static readonly Typeface HudFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    static readonly Typeface TextFont = new(FontFamily.Default);
    static readonly IBrush HudBg = new SolidColorBrush(Color.FromArgb(200, 11, 11, 11)).ToImmutable();
    static readonly IBrush PanelBg = new SolidColorBrush(Color.FromArgb(240, 11, 11, 11)).ToImmutable();
    static readonly IBrush BarBack = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)).ToImmutable();
    static readonly IBrush Outside = new SolidColorBrush(Color.Parse("#070707")).ToImmutable();
    static readonly IPen PanelPen = new Pen(Palette.AccentBorder, 1).ToImmutable();
    static readonly IPen ChoicePen = new Pen(Palette.Accent, 1).ToImmutable();
    static readonly IPen BarPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1).ToImmutable();
    static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#A3A3A3")).ToImmutable();
    static readonly RenderOptions Crisp = new() { BitmapInterpolationMode = BitmapInterpolationMode.None };

    /// <summary>Where the game's view is drawn and how many screen pixels one view pixel takes.</summary>
    (Rect Area, double Scale) Viewport()
    {
        var cam = _world.Camera;
        var size = Bounds.Size;
        double scale = Math.Min(size.Width / cam.BaseWidth, size.Height / cam.BaseHeight);
        if (scale <= 0 || !double.IsFinite(scale)) return (default, 0);
        double w = cam.BaseWidth * scale, h = cam.BaseHeight * scale;
        return (new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h), scale);
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(Palette.Bg0, new Rect(size));
        var (area, scale) = Viewport();
        if (scale <= 0) return;
        var cam = _world.Camera;
        double s = scale * cam.Zoom;

        using (ctx.PushClip(area))
        {
            ctx.FillRectangle(Outside, area);
            var world = Matrix.CreateTranslation(-cam.Left, -cam.Top) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(area.X, area.Y);
            var visible = new Rect(cam.Left, cam.Top, cam.ViewWidth, cam.ViewHeight);
            SortObjects();
            using (ctx.PushTransform(world))
            using (ctx.PushRenderOptions(Crisp))
            {
                ctx.FillRectangle(SceneCanvas.BrushOf(_scene.Background, Palette.Bg1), new Rect(0, 0, _world.SceneWidth, _world.SceneHeight));
                if (_world.HasTiles) TileDraw.Draw(ctx, _world.Tiles.Layers, _world.Tiles.Kinds, visible, _images, front: false); // GameKit: tiles behind
                DrawObjects(ctx, _order, visible);
                if (_world.HasTiles)
                {
                    TileDraw.Draw(ctx, _world.Tiles.Layers, _world.Tiles.Kinds, visible, _images, front: true);
                    TileDraw.DrawDig(ctx, _world.Tiles);
                }
                DrawHudItems(ctx, anchored: true, area, scale);
                DrawDebugMarks(ctx, s);
            }
            // GameKit: darkness and lights, a tint and static over the scene — under the screen objects and the HUD.
            DrawLighting(ctx, area);
            DrawOverlay(ctx, area, _world.TintAmount, _world.TintColor, _tintBrush);
            DrawNoise(ctx, area);
            if (_uiOrder.Count > 0)
                using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(area.X, area.Y)))
                using (ctx.PushRenderOptions(Crisp))
                    DrawObjects(ctx, _uiOrder, new Rect(0, 0, cam.BaseWidth, cam.BaseHeight));
            DrawHudItems(ctx, anchored: false, area, scale);
            DrawHotbar(ctx, area, scale);
            DrawOverlay(ctx, area, _world.FadeAmount, _world.FadeColor, _fadeBrush);
            DrawOverlay(ctx, area, _world.FlashAmount, _world.FlashColor, _flashBrush);
            DrawDialogue(ctx, area, scale);
            DrawToasts(ctx, area, scale);
            if (_world.Paused) DrawCentered(ctx, area, "PAUSED", 28 * scale);
            if (Debugger?.Paused == true) DrawDebugBanner(ctx, area);
        }

        DrawGlobals(ctx, size);
        DrawErrors(ctx);
    }

    /// <summary>The objects to draw this frame, by layer (scene order inside a layer): scene ones, then screen ones.</summary>
    void SortObjects()
    {
        var objs = _world.Objects;
        _order.Clear();
        _uiOrder.Clear();
        bool layered = false;
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            if (o.Destroyed || o.Opacity <= 0.001 || !o.Shown) continue;
            if (o.Z != 0) layered = true;
            (o.Ui ? _uiOrder : _order).Add(o);
        }
        if (layered) { ByLayer(_order); ByLayer(_uiOrder); }
    }

    static void ByLayer(List<GameObject> list) // stable insertion sort by layer
    {
        for (int i = 1; i < list.Count; i++)
        {
            var o = list[i];
            int j = i - 1;
            while (j >= 0 && list[j].Z > o.Z) { list[j + 1] = list[j]; j--; }
            list[j + 1] = o;
        }
    }

    void DrawObjects(DrawingContext ctx, List<GameObject> order, Rect visible)
    {
        foreach (var o in order)
        {
            // Off the view: skip (big worlds have thousands of objects).
            if (o.X > visible.Right || o.Y > visible.Bottom || o.X + o.Width < visible.X || o.Y + o.Height < visible.Y) continue;
            if (o.Opacity < 0.999)
                using (ctx.PushOpacity(o.Opacity)) DrawObject(ctx, o);
            else DrawObject(ctx, o);
        }
    }

    void DrawObject(DrawingContext ctx, GameObject o)
    {
        var r = new Rect(o.X, o.Y, Math.Max(0, o.Width), Math.Max(0, o.Height));
        if (_images.Get(o.Image) is { } bmp) DrawPicture(ctx, bmp, r, o.SheetFrames, o.SheetFrame, o.FlipX);
        else
        {
            double rad = Math.Min(6, Math.Min(r.Width, r.Height) / 4);
            ctx.DrawRectangle(BrushFor(o), null, r, rad, rad);
        }
        if (!string.IsNullOrEmpty(o.Caption)) DrawCaption(ctx, o, r);
    }

    /// <summary>A picture (or one frame of a strip) stretched to the box, mirrored when facing left.</summary>
    internal static void DrawPicture(DrawingContext ctx, Bitmap bmp, Rect r, int frames, int frame, bool flip)
    {
        var px = bmp.Size;
        frames = Math.Max(1, frames);
        double fw = px.Width / frames;
        var src = new Rect(Math.Clamp(frame, 0, frames - 1) * fw, 0, fw, px.Height);
        if (!flip) { ctx.DrawImage(bmp, src, r); return; }
        double cx = r.X + r.Width / 2;
        using (ctx.PushTransform(Matrix.CreateTranslation(-cx, 0) * Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(cx, 0)))
            ctx.DrawImage(bmp, src, r);
    }

    IBrush BrushFor(GameObject o)
    {
        if (o.Color != null) return SceneCanvas.BrushOf(o.Color, Fallback); // its scene colour, or one a script set ('color')
        if (_brushes.TryGetValue(o, out var b)) return b;
        b = o.Mechanisms.Count > 0 && _byMechanism.TryGetValue(o.Mechanisms[0].Def.Name, out var mb) ? mb : Fallback;
        _brushes[o] = b;
        return b;
    }

    Color ColorOf(string text, Color fallback)
    {
        if (_colors.TryGetValue(text, out var c)) return c;
        c = Color.TryParse(text, out var parsed) ? parsed : fallback;
        _colors[text] = c;
        return c;
    }

    FormattedText HudText(HudItem h, double size)
    {
        if (_hudTexts.TryGetValue(h, out var c) && c.Text == h.Text && c.Size == size && c.Color == h.Color) return c.Ft;
        var ft = new FormattedText(h.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HudFont, Math.Max(1, size),
            SceneCanvas.BrushOf(h.Color, Palette.Text));
        _hudTexts[h] = (h.Text, size, h.Color, ft);
        return ft;
    }

    /// <summary>
    /// Anchored items are drawn in scene coordinates (inside the world transform): texts centred on their object, bars
    /// just above it. The others are in view pixels from the top-left of the game area.
    /// </summary>
    void DrawHudItems(DrawingContext ctx, bool anchored, Rect area, double scale)
    {
        var hud = _world.Hud;
        for (int i = 0; i < hud.Count; i++)
        {
            var h = hud[i];
            if ((h.Anchor != null) != anchored) continue;
            if (anchored && h.Anchor!.Destroyed) continue;
            double k = anchored ? 1 : scale;
            double x, y;
            if (anchored)
            {
                var a = h.Anchor!;
                x = a.X + a.Width / 2 + h.X;
                y = h.IsBar ? a.Y - h.Height - 6 + h.Y : a.Y + a.Height / 2 + h.Y;
            }
            else { x = area.X + h.X * scale; y = area.Y + h.Y * scale; }

            if (h.IsBar)
            {
                double w = h.Width * k, hh = h.Height * k;
                var r = anchored ? new Rect(x - w / 2, y, w, hh) : new Rect(x, y, w, hh);
                ctx.DrawRectangle(BarBack, BarPen, r, hh / 2, hh / 2);
                _barBrush.Color = ColorOf(h.Color, Colors.IndianRed);
                if (h.Fraction > 0) ctx.DrawRectangle(_barBrush, null, new Rect(r.X, r.Y, r.Width * h.Fraction, r.Height), hh / 2, hh / 2);
            }
            else if (h.Text.Length > 0)
            {
                var ft = HudText(h, h.Size * k);
                var at = anchored ? new Point(x - ft.Width / 2, y - ft.Height / 2) : new Point(x, y);
                ctx.DrawText(ft, at);
            }
        }
    }

    void DrawOverlay(DrawingContext ctx, Rect area, double amount, string color, SolidColorBrush brush)
    {
        if (amount <= 0.001) return;
        var c = ColorOf(color, Colors.Black);
        brush.Color = Color.FromArgb((byte)Math.Round(Math.Clamp(amount, 0, 1) * c.A), c.R, c.G, c.B);
        ctx.FillRectangle(brush, area);
    }

    // The dialogue panel: the last text and its layout, reused while nothing changes.
    string? _dlgShown, _nameShown;
    double _dlgSize, _dlgWidth, _nameSize;
    FormattedText? _dlgFt, _nameFt;
    readonly List<FormattedText> _optionFts = [];
    List<string>? _optionsShown;
    double _optionsSize;
    StreamGeometry? _marker;
    double _markerScale;

    void DrawDialogue(DrawingContext ctx, Rect area, double scale)
    {
        _optionRects.Clear();
        var d = _world.Dialogue;
        if (d == null || d.Closed) return;
        double pad = 16 * scale, margin = 14 * scale;
        double textSize = 17 * scale;
        // A speaker on screen gets a speech box above them; otherwise (a narrator, off screen) a panel at the bottom.
        var cam = _world.Camera;
        double zs = scale * cam.Zoom;
        Rect? speaker = null;
        if (d.SpeakerObject is { Destroyed: false } so && so.Shown && !so.Ui)
        {
            var r = new Rect(area.X + (so.X - cam.Left) * zs, area.Y + (so.Y - cam.Top) * zs, so.Width * zs, so.Height * zs);
            if (r.Intersects(area)) speaker = r;
        }
        double width = speaker != null ? Math.Min(area.Width - margin * 2, 440 * scale) : area.Width - margin * 2;
        string visible = d.Visible;
        if (_dlgFt == null || visible != _dlgShown || textSize != _dlgSize || width != _dlgWidth)
        {
            _dlgWidth = width;
            _dlgShown = visible;
            _dlgSize = textSize;
            _dlgFt = new FormattedText(visible, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextFont, textSize, Palette.Text)
            { MaxTextWidth = Math.Max(10, width - pad * 2) };
        }
        if (_nameFt == null || _nameShown != d.Speaker || _nameSize != scale)
        {
            _nameShown = d.Speaker;
            _nameSize = scale;
            _nameFt = new FormattedText(d.Speaker, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HudFont, 15 * scale, Palette.Accent);
        }
        if (d.Options != null && (!ReferenceEquals(_optionsShown, d.Options) || _optionsSize != scale))
        {
            _optionsShown = d.Options;
            _optionsSize = scale;
            _optionFts.Clear();
            for (int i = 0; i < d.Options.Count; i++)
                _optionFts.Add(new FormattedText($"{i + 1}.  {d.Options[i]}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HudFont, 16 * scale, Palette.Text));
        }

        bool choices = d.Options != null && d.FullyTyped;
        double nameH = d.Speaker.Length > 0 ? _nameFt.Height + 4 * scale : 0;
        double optH = 0;
        if (choices) foreach (var f in _optionFts) optH += f.Height + 10 * scale;
        double textH = Math.Max(_dlgFt.Height, (speaker != null ? 1 : 2) * textSize * 1.3);
        double height = pad * 2 + nameH + textH + optH;
        var panel = new Rect(area.X + margin, area.Bottom - margin - height, width, height);
        if (speaker is { } sp)
        {
            double px = Math.Clamp(sp.Center.X - width / 2, area.X + margin, area.Right - margin - width);
            double tail = 10 * scale, py = sp.Top - tail - 4 * scale - height;
            bool above = py >= area.Y + margin;
            if (!above) py = Math.Min(sp.Bottom + tail + 4 * scale, area.Bottom - margin - height);
            panel = new Rect(px, py, width, height);
            double tx = Math.Clamp(sp.Center.X, panel.X + 16 * scale, panel.Right - 16 * scale);
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                double edge = above ? panel.Bottom - 1 : panel.Top + 1, tip = above ? panel.Bottom + tail : panel.Top - tail;
                gc.BeginFigure(new Point(tx - tail, edge), true);
                gc.LineTo(new Point(tx, tip));
                gc.LineTo(new Point(tx + tail * 0.4, edge));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(PanelBg, null, g);
        }
        ctx.DrawRectangle(PanelBg, PanelPen, panel, 10 * scale, 10 * scale);
        ctx.FillRectangle(Palette.Accent, new Rect(panel.X, panel.Y + 10 * scale, 3 * scale, Math.Max(0, panel.Height - 20 * scale)));
        double y = panel.Y + pad;
        if (d.Speaker.Length > 0) { ctx.DrawText(_nameFt, new Point(panel.X + pad, y)); y += nameH; }
        ctx.DrawText(_dlgFt, new Point(panel.X + pad, y));
        y += textH + 4 * scale;
        if (choices)
            for (int i = 0; i < _optionFts.Count; i++)
            {
                var ft = _optionFts[i];
                var r = new Rect(panel.X + pad, y, Math.Min(panel.Width - pad * 2, ft.Width + 28 * scale), ft.Height + 6 * scale);
                if (i == d.Selected) ctx.DrawRectangle(Palette.AccentSoft, ChoicePen, r, 6 * scale, 6 * scale);
                ctx.DrawText(ft, new Point(r.X + 12 * scale, r.Y + 3 * scale));
                _optionRects.Add(r);
                y += ft.Height + 10 * scale;
            }
        else if (d.FullyTyped && d.Options == null && (int)(_world.RealTime * 2) % 2 == 0)
        {
            // The "go on" marker, blinking.
            if (_marker == null || _markerScale != scale)
            {
                double m = 7 * scale;
                _markerScale = scale;
                _marker = new StreamGeometry();
                using var gc = _marker.Open();
                gc.BeginFigure(new Point(-m, -m / 2), true);
                gc.LineTo(new Point(0, -m / 2));
                gc.LineTo(new Point(-m / 2, m / 3));
                gc.EndFigure(true);
            }
            using (ctx.PushTransform(Matrix.CreateTranslation(panel.Right - pad, panel.Bottom - pad)))
                ctx.DrawGeometry(Palette.Accent, null, _marker);
        }
    }

    readonly Dictionary<Toast, FormattedText> _toastFts = new();

    void DrawToasts(DrawingContext ctx, Rect area, double scale)
    {
        var toasts = _world.Toasts;
        if (toasts.Count == 0) { if (_toastFts.Count > 0) _toastFts.Clear(); return; }
        double y = area.Y + 14 * scale;
        foreach (var t in toasts)
        {
            if (!_toastFts.TryGetValue(t, out var ft))
                _toastFts[t] = ft = new FormattedText(t.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HudFont, 15 * scale, Palette.Text);
            double alpha = Math.Clamp(t.Left / 0.4, 0, 1);
            var r = new Rect(area.X + (area.Width - ft.Width) / 2 - 14 * scale, y, ft.Width + 28 * scale, ft.Height + 12 * scale);
            using (ctx.PushOpacity(alpha))
            {
                ctx.DrawRectangle(PanelBg, PanelPen, r, r.Height / 2, r.Height / 2);
                ctx.DrawText(ft, new Point(r.X + 14 * scale, r.Y + 6 * scale));
            }
            y += r.Height + 6 * scale;
        }
    }

    FormattedText? _centerFt;
    string? _centerText;
    double _centerSize;

    void DrawCentered(DrawingContext ctx, Rect area, string text, double size)
    {
        if (_centerFt == null || _centerText != text || _centerSize != size)
        {
            _centerSize = size;
            _centerText = text;
            _centerFt = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HudFont, size, Palette.Text);
        }
        var r = new Rect(area.Center.X - _centerFt.Width / 2 - 20, area.Center.Y - _centerFt.Height / 2 - 10, _centerFt.Width + 40, _centerFt.Height + 20);
        ctx.DrawRectangle(PanelBg, PanelPen, r, 10, 10);
        ctx.DrawText(_centerFt, new Point(r.X + 20, r.Y + 10));
    }

    /// <summary>The global variables top-right — only while the game shows no texts of its own (a Label, 'show text').</summary>
    void DrawGlobals(DrawingContext ctx, Size size)
    {
        if (_world.Globals.Count == 0) return;
        foreach (var h in _world.Hud) if (!h.IsBar) return;
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

    // ---------------- Keyboard and mouse ----------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = true;
        if (e.Key == Key.Escape) { StopRequested?.Invoke(); return; }
        if (Debugger != null && e.Key is Key.F5 or Key.F10)
        {
            if (e.Key == Key.F10) Debugger.StepFrame();
            else if (Debugger.Paused) Debugger.Resume();
            else Debugger.Pause();
            InvalidateVisual();
            return;
        }
        _world.KeyDown(MainWindow.KeyName(e.Key)); // the world ignores auto-repeat
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _world.KeyUp(MainWindow.KeyName(e.Key));
        e.Handled = true;
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _world.ReleaseKeys();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        // A dialogue takes the click (unless the debugger paused the game: then a click looks inside an object).
        if (_world.Dialogue is { Closed: false } && Debugger?.Paused != true)
        {
            int hit = _optionRects.FindIndex(r => r.Contains(p));
            _world.Click(hit >= 0 ? hit : null);
            e.Handled = true;
            return;
        }
        GameMouseDown(e, p); // the game gets the click too: when clicked, dragging… (PlaySurface.GameKit.cs)
        e.Handled = true;
        if (ObjectClicked == null) return;
        ObjectClicked(ObjectAtScreen(p));
        InvalidateVisual();
    }

    // ---------------- Debugger ----------------

    static readonly IPen InspectPen = new Pen(Palette.Accent, 2).ToImmutable();
    static readonly IPen BreakPen = new Pen(new SolidColorBrush(Color.Parse("#E05D5D")), 2, new DashStyle([3, 2], 0)).ToImmutable();
    FormattedText? _bannerFt;
    string? _bannerText;

    /// <summary>The object drawn on top under a point of the control (what a click inspects), or null.</summary>
    public GameObject? ObjectAtScreen(Point p)
    {
        var (area, scale) = Viewport();
        if (scale <= 0 || !area.Contains(p)) return null;
        var cam = _world.Camera;
        double s = scale * cam.Zoom;
        double ux = (p.X - area.X) / scale, uy = (p.Y - area.Y) / scale; // screen objects first
        for (int i = _uiOrder.Count - 1; i >= 0; i--)
        {
            var o = _uiOrder[i];
            if (!o.Destroyed && ux >= o.X && ux <= o.X + o.Width && uy >= o.Y && uy <= o.Y + o.Height) return o;
        }
        double x = cam.Left + (p.X - area.X) / s, y = cam.Top + (p.Y - area.Y) / s;
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            var o = _order[i];
            if (!o.Destroyed && x >= o.X && x <= o.X + o.Width && y >= o.Y && y <= o.Y + o.Height) return o;
        }
        return null;
    }

    /// <summary>The inspected object outlined; the object stopped at a breakpoint dashed red. Inside the world transform.</summary>
    void DrawDebugMarks(DrawingContext ctx, double s)
    {
        if (Inspected is { Destroyed: false } i)
            ctx.DrawRectangle(null, InspectPen, new Rect(i.X - 3 / s, i.Y - 3 / s, i.Width + 6 / s, i.Height + 6 / s).Normalize(), 4 / s, 4 / s);
        if (Debugger?.PausedAt?.Object is { Destroyed: false } b && b != Inspected)
            ctx.DrawRectangle(null, BreakPen, new Rect(b.X - 3 / s, b.Y - 3 / s, b.Width + 6 / s, b.Height + 6 / s).Normalize(), 4 / s, 4 / s);
    }

    /// <summary>"⏸ Paused by the debugger" at the top of the game (with where, when a breakpoint stopped it).</summary>
    void DrawDebugBanner(DrawingContext ctx, Rect area)
    {
        string text = Debugger?.PausedAt is { } at ? $"⏸  Paused at {at}  ·  F5 resume · F10 step" : "⏸  Paused  ·  F5 resume · F10 step";
        if (_bannerFt == null || _bannerText != text)
        {
            _bannerText = text;
            _bannerFt = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HudFont, 13, Palette.Text);
        }
        var r = new Rect(area.X + (area.Width - _bannerFt.Width) / 2 - 14, area.Y + 10, _bannerFt.Width + 28, _bannerFt.Height + 10);
        ctx.DrawRectangle(PanelBg, PanelPen, r, r.Height / 2, r.Height / 2);
        ctx.DrawText(_bannerFt, new Point(r.X + 14, r.Y + 5));
    }
}
