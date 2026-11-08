using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// The scene editing surface: draws the scene (bounds, grid, objects, selection) and handles select / move / resize /
/// pan / zoom and mechanisms dropped from the library. It only edits the <see cref="Scene"/> it is given and reports
/// through the callbacks; the window owns undo, the inspector and the keyboard commands.
/// </summary>
sealed class SceneCanvas : Control, ICustomHitTest
{
    public const double GridStep = 8;
    public const string DragPrefix = "be-mechanism:";

    public Scene Scene = new();
    /// <summary>Selected object id (0 = nothing).</summary>
    public int SelectedId;
    public bool ShowGrid = true;
    /// <summary>The project's pictures (objects with a picture are drawn with it).</summary>
    public ImageCache? Images;
    /// <summary>The picture an object shows (its own, or one a mechanism param picks); default: its Picture.</summary>
    public Func<SceneObject, string?>? PictureOf;
    static readonly RenderOptions Crisp = new() { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.None };
    public double Zoom { get; private set; } = 1;
    Vector _pan;

    public Action? SelectionChanged;
    /// <summary>Called once before an edit starts changing the scene (undo checkpoint).</summary>
    public Action? BeforeEdit;
    /// <summary>The selected object moved or resized (live, while dragging).</summary>
    public Action? Edited;
    public Action? ViewChanged;
    public Action<string, Point, SceneObject?>? MechanismDropped;
    /// <summary>Keys the canvas doesn't use itself (Delete, Ctrl+D, arrows...). Return true when handled.</summary>
    public Func<KeyEventArgs, bool>? KeyCommand;
    /// <summary>A tool drawn over the scene that gets the pointer first (the path editor, picking an object for a param).</summary>
    public ICanvasOverlay? Overlay;

    /// <summary>Scene coordinates → this control's pixels.</summary>
    public Point WorldToScreen(Point world) => ToScreen(world.X, world.Y);

    /// <summary>An editor tool is active: left presses go to <see cref="ToolPointer"/> instead of selecting and moving.</summary>
    public bool ToolActive;
    /// <summary>The active tool reacts to the release: while dragging, show the box from the press to the pointer.</summary>
    public bool ToolShowsBox;
    /// <summary>The active tool's pointer events: "click" (press), "drag" (moved while pressed), "release", in scene coordinates.</summary>
    public Action<string, Point>? ToolPointer;

    enum Mode { None, Pan, Move, Resize, Overlay, Tool }
    Mode _mode;
    Point _pressScreen;
    Point _pressWorld;
    Rect _origRect;
    int _handle;
    Point _toolLast;
    bool _editStarted, _spaceDown, _fitted;
    int _dropHoverId;

    public SceneCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { _dropHoverId = 0; InvalidateVisual(); });
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public bool HitTest(Point point) => true;

    // ---------------- View ----------------

    public Point ToWorld(Point screen) => new((screen.X - _pan.X) / Zoom, (screen.Y - _pan.Y) / Zoom);
    Point ToScreen(double x, double y) => new(x * Zoom + _pan.X, y * Zoom + _pan.Y);
    Rect ToScreen(double x, double y, double w, double h) => new(x * Zoom + _pan.X, y * Zoom + _pan.Y, w * Zoom, h * Zoom);

    /// <summary>The middle of what is visible now, in scene coordinates.</summary>
    public Point ViewCenter => ToWorld(new Point(Bounds.Width / 2, Bounds.Height / 2));

    public void Fit()
    {
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        double z = Math.Min((Bounds.Width - 48) / Scene.Width, (Bounds.Height - 48) / Scene.Height);
        Zoom = Math.Clamp(z, 0.1, 4);
        _pan = new Vector((Bounds.Width - Scene.Width * Zoom) / 2, (Bounds.Height - Scene.Height * Zoom) / 2);
        _fitted = true;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void ZoomBy(double factor, Point? around = null)
    {
        var p = around ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        double nz = Math.Clamp(Zoom * factor, 0.1, 6);
        _pan = new Vector(p.X - (p.X - _pan.X) * nz / Zoom, p.Y - (p.Y - _pan.Y) * nz / Zoom);
        Zoom = nz;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_fitted) Fit();
    }

    // ---------------- Drawing ----------------

    static readonly Dictionary<string, IBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
    public static IBrush BrushOf(string? hex, IBrush fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        if (BrushCache.TryGetValue(hex, out var b)) return b;
        b = Color.TryParse(hex, out var c) ? new SolidColorBrush(c).ToImmutable() : fallback;
        BrushCache[hex] = b;
        return b;
    }

    static readonly IPen GridMajor = new ImmutablePen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)).ToImmutable(), 1);
    static readonly IPen GridMinor = new ImmutablePen(new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)).ToImmutable(), 1);
    static readonly IPen BoundsPen = new ImmutablePen(new SolidColorBrush(Color.FromArgb(70, 217, 119, 87)).ToImmutable(), 1);
    static readonly IPen ObjectEdge = new ImmutablePen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)).ToImmutable(), 1);
    static readonly IPen SelectPen = new ImmutablePen((IImmutableBrush)Palette.Accent, 2);
    static readonly IPen DropPen = new ImmutablePen((IImmutableBrush)Palette.Accent, 2, new ImmutableDashStyle([3, 2], 0));
    static readonly IPen HandleEdge = new ImmutablePen((IImmutableBrush)Palette.Bg0, 1);
    static readonly IBrush LabelBg = new SolidColorBrush(Color.FromArgb(225, 11, 11, 11)).ToImmutable();
    static readonly Typeface UiFont = new(FontFamily.Default);
    static readonly IBrush ToolBoxFill = new SolidColorBrush(Color.FromArgb(28, 217, 119, 87)).ToImmutable();

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(Palette.Bg0, new Rect(size));

        var sr = ToScreen(0, 0, Scene.Width, Scene.Height);
        ctx.FillRectangle(BrushOf(Scene.Background, Palette.Bg1), sr);
        using (ctx.PushTransform(Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y))) // tile maps (MainWindow.TilePaint.cs)
            MainWindow.DrawSceneTiles(ctx, Scene, new Rect(ToWorld(default), ToWorld(new Point(size.Width, size.Height))), Images);
        if (ShowGrid) DrawGrid(ctx, sr);
        ctx.DrawRectangle(null, BoundsPen, sr.Inflate(0.5));

        SceneObject? sel = null;
        foreach (var o in Scene.Objects)
        {
            var r = ToScreen(o.X, o.Y, o.Width, o.Height);
            double rad = Math.Min(6, Math.Min(r.Width, r.Height) / 4);
            if (Images?.Get(PictureOf?.Invoke(o) ?? o.Image) is { } bmp)
                using (ctx.PushRenderOptions(Crisp)) PlaySurface.DrawPicture(ctx, bmp, r, 1, 0, false);
            else ctx.DrawRectangle(BrushOf(o.Color, Palette.Accent), ObjectEdge, r, rad, rad);
            if (o.Id == SelectedId) sel = o;
            if (o.Id == _dropHoverId) ctx.DrawRectangle(null, DropPen, r.Inflate(3), rad + 2, rad + 2);
        }

        if (sel != null)
        {
            var r = ToScreen(sel.X, sel.Y, sel.Width, sel.Height);
            double rad = Math.Min(6, Math.Min(r.Width, r.Height) / 4) + 2;
            ctx.DrawRectangle(null, SelectPen, r.Inflate(2), rad, rad);
            foreach (var h in HandleRects(r)) ctx.DrawRectangle(Palette.Accent, HandleEdge, h);
            var ft = new FormattedText(sel.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, UiFont, 12, Palette.Text);
            var at = new Point(r.X + (r.Width - ft.Width) / 2, r.Bottom + 9);
            ctx.DrawRectangle(LabelBg, null, new Rect(at.X - 6, at.Y - 2, ft.Width + 12, ft.Height + 4), 4, 4);
            ctx.DrawText(ft, at);
        }

        Overlay?.Draw(ctx, this);
        if (_mode == Mode.Tool && ToolShowsBox)
        {
            var a = ToScreen(_pressWorld.X, _pressWorld.Y);
            var b = ToScreen(_toolLast.X, _toolLast.Y);
            var box = new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
            ctx.DrawRectangle(ToolBoxFill, DropPen, box);
        }

        if (Scene.Objects.Count == 0)
        {
            var ft = new FormattedText("Drag a mechanism here, or click  + Object", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, UiFont, 14, Palette.TextDim);
            ctx.DrawText(ft, new Point(sr.X + (sr.Width - ft.Width) / 2, sr.Y + (sr.Height - ft.Height) / 2));
        }
    }

    void DrawGrid(DrawingContext ctx, Rect sr)
    {
        double step = GridStep * 4;
        if (step * Zoom < 6) return;
        bool minor = GridStep * Zoom >= 10;
        double s = minor ? GridStep : step;
        for (double x = s; x < Scene.Width; x += s)
        {
            double sx = Math.Round(x * Zoom + _pan.X) + 0.5;
            ctx.DrawLine(x % step == 0 ? GridMajor : GridMinor, new Point(sx, sr.Top), new Point(sx, sr.Bottom));
        }
        for (double y = s; y < Scene.Height; y += s)
        {
            double sy = Math.Round(y * Zoom + _pan.Y) + 0.5;
            ctx.DrawLine(y % step == 0 ? GridMajor : GridMinor, new Point(sr.Left, sy), new Point(sr.Right, sy));
        }
    }

    // Handles: 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L
    static Rect[] HandleRects(Rect r)
    {
        const double s = 8;
        double l = r.Left - 2, t = r.Top - 2, rr = r.Right + 2, b = r.Bottom + 2, cx = r.Center.X, cy = r.Center.Y;
        Rect At(double x, double y) => new(x - s / 2, y - s / 2, s, s);
        return [At(l, t), At(cx, t), At(rr, t), At(rr, cy), At(rr, b), At(cx, b), At(l, b), At(l, cy)];
    }

    // ---------------- Hit testing ----------------

    public SceneObject? ObjectAt(Point world)
    {
        for (int i = Scene.Objects.Count - 1; i >= 0; i--)
        {
            var o = Scene.Objects[i];
            if (world.X >= o.X && world.X <= o.X + o.Width && world.Y >= o.Y && world.Y <= o.Y + o.Height) return o;
        }
        return null;
    }

    int HandleAt(Point screen)
    {
        var sel = Scene.Find(SelectedId);
        if (sel == null) return -1;
        var hs = HandleRects(ToScreen(sel.X, sel.Y, sel.Width, sel.Height));
        for (int i = 0; i < hs.Length; i++)
            if (hs[i].Inflate(3).Contains(screen)) return i;
        return -1;
    }

    static readonly Cursor[] HandleCursors =
    [
        new(StandardCursorType.TopLeftCorner), new(StandardCursorType.TopSide), new(StandardCursorType.TopRightCorner), new(StandardCursorType.RightSide),
        new(StandardCursorType.BottomRightCorner), new(StandardCursorType.BottomSide), new(StandardCursorType.BottomLeftCorner), new(StandardCursorType.LeftSide)
    ];
    static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    static readonly Cursor CrossCursor = new(StandardCursorType.Cross);

    // ---------------- Pointer ----------------

    void Select(int id)
    {
        if (SelectedId == id) return;
        SelectedId = id;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        _pressScreen = pt.Position;
        _pressWorld = ToWorld(pt.Position);
        _editStarted = false;
        if (pt.Properties.IsMiddleButtonPressed || (pt.Properties.IsLeftButtonPressed && _spaceDown))
        {
            _mode = Mode.Pan;
            Cursor = HandCursor;
        }
        else if ((pt.Properties.IsLeftButtonPressed || pt.Properties.IsRightButtonPressed) && Overlay != null && Overlay.Press(_pressWorld, pt.Properties.IsRightButtonPressed, e.KeyModifiers))
        {
            _mode = Mode.Overlay;
            InvalidateVisual();
        }
        else if (pt.Properties.IsLeftButtonPressed && ToolActive)
        {
            _mode = Mode.Tool;
            _toolLast = _pressWorld;
            ToolPointer?.Invoke("click", _pressWorld);
            InvalidateVisual();
        }
        else if (pt.Properties.IsLeftButtonPressed)
        {
            int h = HandleAt(pt.Position);
            if (h >= 0)
            {
                var sel = Scene.Find(SelectedId)!;
                _mode = Mode.Resize;
                _handle = h;
                _origRect = new Rect(sel.X, sel.Y, sel.Width, sel.Height);
            }
            else if (ObjectAt(_pressWorld) is { } o)
            {
                Select(o.Id);
                _mode = Mode.Move;
                _origRect = new Rect(o.X, o.Y, o.Width, o.Height);
            }
            else
            {
                Select(0);
                _mode = Mode.None;
                return;
            }
        }
        else return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        bool snap = (e.KeyModifiers & KeyModifiers.Alt) == 0;
        switch (_mode)
        {
            case Mode.Pan:
                _pan += p - _pressScreen;
                _pressScreen = p;
                InvalidateVisual();
                return;
            case Mode.Overlay:
                Overlay?.Drag(ToWorld(p), e.KeyModifiers);
                InvalidateVisual();
                return;
            case Mode.Tool:
            {
                var w = ToWorld(p);
                if (Math.Abs(w.X - _toolLast.X) < 0.5 && Math.Abs(w.Y - _toolLast.Y) < 0.5) return;
                _toolLast = w;
                ToolPointer?.Invoke("drag", w);
                InvalidateVisual();
                return;
            }
            case Mode.Move:
            {
                var o = Scene.Find(SelectedId);
                if (o == null) return;
                var w = ToWorld(p);
                double nx = _origRect.X + (w.X - _pressWorld.X), ny = _origRect.Y + (w.Y - _pressWorld.Y);
                if (snap) { nx = Snap(nx); ny = Snap(ny); }
                if (nx == o.X && ny == o.Y) return;
                StartEdit();
                o.X = nx; o.Y = ny;
                Edited?.Invoke();
                InvalidateVisual();
                return;
            }
            case Mode.Resize:
            {
                var o = Scene.Find(SelectedId);
                if (o == null) return;
                var w = ToWorld(p);
                double ex = snap ? Snap(w.X) : w.X, ey = snap ? Snap(w.Y) : w.Y;
                double l = _origRect.Left, t = _origRect.Top, r = _origRect.Right, b = _origRect.Bottom;
                const double min = 4;
                if (_handle is 0 or 6 or 7) l = Math.Min(ex, r - min);
                if (_handle is 2 or 3 or 4) r = Math.Max(ex, l + min);
                if (_handle is 0 or 1 or 2) t = Math.Min(ey, b - min);
                if (_handle is 4 or 5 or 6) b = Math.Max(ey, t + min);
                if (l == o.X && t == o.Y && r - l == o.Width && b - t == o.Height) return;
                StartEdit();
                o.X = l; o.Y = t; o.Width = r - l; o.Height = b - t;
                Edited?.Invoke();
                InvalidateVisual();
                return;
            }
        }
        // Hover feedback.
        if (ToolActive) { Cursor = _spaceDown ? HandCursor : CrossCursor; return; }
        int h = HandleAt(p);
        Cursor = h >= 0 ? HandleCursors[h] : _spaceDown ? HandCursor : ObjectAt(ToWorld(p)) != null ? MoveCursor : Cursor.Default;
    }

    void StartEdit()
    {
        if (_editStarted) return;
        _editStarted = true;
        BeforeEdit?.Invoke();
    }

    static double Snap(double v) => Math.Round(v / GridStep) * GridStep;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mode == Mode.None) return;
        if (_mode == Mode.Overlay) { Overlay?.Release(); InvalidateVisual(); }
        bool tool = _mode == Mode.Tool;
        _mode = Mode.None;
        e.Pointer.Capture(null);
        if (tool) { EndToolUse(); return; }
        Cursor = Cursor.Default;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        bool tool = _mode == Mode.Tool;
        _mode = Mode.None;
        if (tool) EndToolUse(); // the use still ends (one undo step) when the pointer is taken away
    }

    void EndToolUse()
    {
        ToolPointer?.Invoke("release", _toolLast);
        InvalidateVisual();
    }

    /// <summary>Snap helper: shows a tool's drag box without a mouse (null hides it again, without a release).</summary>
    public void ShowToolBox(Point? from, Point to)
    {
        _mode = from == null ? Mode.None : Mode.Tool;
        if (from is { } f) _pressWorld = f;
        _toolLast = to;
        InvalidateVisual();
    }

    /// <summary>Ends a tool drag that is in progress (the tool was switched off while the mouse was down).</summary>
    public void CancelToolDrag()
    {
        if (_mode != Mode.Tool) return;
        _mode = Mode.None;
        EndToolUse();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        ZoomBy(e.Delta.Y > 0 ? 1.15 : 1 / 1.15, e.GetPosition(this));
        e.Handled = true;
    }

    // ---------------- Keyboard ----------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space) { _spaceDown = true; Cursor = HandCursor; e.Handled = true; return; }
        if (KeyCommand?.Invoke(e) == true) e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) { _spaceDown = false; Cursor = Cursor.Default; e.Handled = true; }
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _spaceDown = false;
    }

    // ---------------- Drop from the library ----------------
    // (ICanvasOverlay is declared at the end of this file.)

    static string? DroppedMechanism(DragEventArgs e)
    {
        string? text = e.DataTransfer.TryGetText();
        return text != null && text.StartsWith(DragPrefix) ? text[DragPrefix.Length..] : null;
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        if (DroppedMechanism(e) == null) { e.DragEffects = DragDropEffects.None; return; }
        e.DragEffects = DragDropEffects.Copy;
        int id = ObjectAt(ToWorld(e.GetPosition(this)))?.Id ?? 0;
        if (id != _dropHoverId) { _dropHoverId = id; InvalidateVisual(); }
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        _dropHoverId = 0;
        var name = DroppedMechanism(e);
        if (name == null) return;
        var w = ToWorld(e.GetPosition(this));
        MechanismDropped?.Invoke(name, w, ObjectAt(w));
        e.Handled = true;
        InvalidateVisual();
    }
}

/// <summary>
/// An editor tool drawn over the scene canvas that gets the pointer before the canvas does (the path editor, picking an
/// object for an Object param). Positions are in scene coordinates.
/// </summary>
interface ICanvasOverlay
{
    void Draw(DrawingContext ctx, SceneCanvas canvas);
    /// <summary>A press (left, or right when <paramref name="right"/>). True = the overlay takes it, and gets the drag and release.</summary>
    bool Press(Point world, bool right, KeyModifiers mods);
    void Drag(Point world, KeyModifiers mods);
    void Release();
}
