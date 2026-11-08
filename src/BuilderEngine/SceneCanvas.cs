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
/// The scene editing surface: draws the scene (bounds, grid, objects, selection) and handles select (click, Shift/Ctrl
/// click, selection box) / move (several at once) / resize (Shift keeps the proportions) / pan / zoom and mechanisms
/// dropped from the library. Locked objects can't be clicked or boxed here. It only edits the <see cref="Scene"/> it is
/// given and reports through the callbacks; the window owns undo, the inspector and the keyboard commands.
/// </summary>
sealed class SceneCanvas : Control, ICustomHitTest
{
    public const double GridStep = SceneEdit.Grid;
    public const string DragPrefix = "be-mechanism:";

    public Scene Scene = new();
    readonly HashSet<int> _sel = new();
    int _primary;
    /// <summary>The main selected object (the inspector shows it; 0 = nothing). Setting it selects only that object.</summary>
    public int SelectedId
    {
        get => _primary;
        set { _primary = value; _sel.Clear(); if (value != 0) _sel.Add(value); }
    }
    /// <summary>Every selected object (the main one too).</summary>
    public IReadOnlyCollection<int> SelectedIds => _sel;
    public bool IsSelected(int id) => _sel.Contains(id);

    /// <summary>Selects these objects; the main one is <paramref name="primary"/> when it is among them, else the last.</summary>
    public void SetSelection(IEnumerable<int> ids, int primary = 0)
    {
        var list = ids.ToList();
        _sel.Clear();
        int last = 0;
        foreach (var id in list) if (id != 0 && _sel.Add(id)) last = id;
        _primary = primary != 0 && _sel.Contains(primary) ? primary : last;
        InvalidateVisual();
    }

    public bool ShowGrid = true;
    /// <summary>The project's pictures (objects with a picture are drawn with it).</summary>
    public ImageCache? Images;
    /// <summary>The picture an object shows (its own, or one a mechanism param picks); default: its Picture.</summary>
    public Func<SceneObject, string?>? PictureOf;
    /// <summary>The width/height of the picture an object shows (null = no picture): what Shift keeps while resizing.</summary>
    public Func<SceneObject, double?>? PictureAspect;
    static readonly RenderOptions Crisp = new() { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.None };
    public double Zoom { get; private set; } = 1;
    Vector _pan;

    public Action? SelectionChanged;
    /// <summary>Called once before an edit starts changing the scene (undo checkpoint).</summary>
    public Action? BeforeEdit;
    /// <summary>The selected objects moved or resized (live, while dragging).</summary>
    public Action? Edited;
    public Action? ViewChanged;
    public Action<string, Point, SceneObject?>? MechanismDropped;
    /// <summary>A double-click on an (unlocked) object.</summary>
    public Action<SceneObject>? DoubleClicked;
    /// <summary>A right-click the overlay didn't take: the scene point and the object under it (locked ones too).</summary>
    public Action<Point, SceneObject?>? MenuRequested;
    /// <summary>The pointer moved over the scene (scene coordinates), or left it (null).</summary>
    public Action<Point?>? PointerWorldChanged;
    /// <summary>A tool drawn over the scene that gets the pointer first (the path editor, picking an object for a param).</summary>
    public ICanvasOverlay? Overlay;
    /// <summary>Where the pointer is over the scene (scene coordinates), or null when it is outside the canvas.</summary>
    public Point? PointerWorld { get; private set; }

    /// <summary>Scene coordinates → this control's pixels.</summary>
    public Point WorldToScreen(Point world) => ToScreen(world.X, world.Y);

    /// <summary>An editor tool is active: left presses go to <see cref="ToolPointer"/> instead of selecting and moving.</summary>
    public bool ToolActive;
    /// <summary>The active tool reacts to the release: while dragging, show the box from the press to the pointer.</summary>
    public bool ToolShowsBox;
    /// <summary>The active tool's pointer events: "click" (press), "drag" (moved while pressed), "release", in scene coordinates.</summary>
    public Action<string, Point>? ToolPointer;

    enum Mode { None, Pan, Move, Resize, Overlay, Tool, Box, Right }
    Mode _mode;
    Point _pressScreen;
    Point _pressWorld;
    Rect _origRect;
    int _handle;
    Point _toolLast;
    bool _editStarted, _spaceDown, _fitted;
    int _dropHoverId;
    // Moving several objects: where each one started; the grabbed one snaps and the others follow.
    readonly Dictionary<int, Point> _origPos = new();
    int _grabId;
    bool _moved, _collapseOnRelease;
    // The selection box.
    Point _boxEnd;
    bool _boxShown, _boxAdd;
    readonly HashSet<int> _boxBase = new();

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

    public void ZoomBy(double factor, Point? around = null) => ZoomTo(Zoom * factor, around);

    /// <summary>Sets the zoom keeping the point <paramref name="around"/> (screen pixels; default the middle) where it is.</summary>
    public void ZoomTo(double zoom, Point? around = null)
    {
        var p = around ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        double nz = Math.Clamp(zoom, 0.1, 6);
        _pan = new Vector(p.X - (p.X - _pan.X) * nz / Zoom, p.Y - (p.Y - _pan.Y) * nz / Zoom);
        Zoom = nz;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Scrolls the view by screen pixels (the wheel without Ctrl).</summary>
    public void PanBy(double dx, double dy)
    {
        _pan += new Vector(dx, dy);
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
    static readonly IPen LockedPen = new ImmutablePen((IImmutableBrush)Palette.TextDim, 1.5, new ImmutableDashStyle([3, 3], 0));
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
        if (ShowGrid) DrawGrid(ctx, sr);
        ctx.DrawRectangle(null, BoundsPen, sr.Inflate(0.5));

        SceneObject? primary = null;
        foreach (var o in Scene.Objects)
        {
            var r = ToScreen(o.X, o.Y, o.Width, o.Height);
            double rad = Math.Min(6, Math.Min(r.Width, r.Height) / 4);
            if (Images?.Get(PictureOf?.Invoke(o) ?? o.Image) is { } bmp)
                using (ctx.PushRenderOptions(Crisp)) PlaySurface.DrawPicture(ctx, bmp, r, 1, 0, false);
            else ctx.DrawRectangle(BrushOf(o.Color, Palette.Accent), ObjectEdge, r, rad, rad);
            if (o.Id == _primary) primary = o;
            if (o.Id == _dropHoverId) ctx.DrawRectangle(null, DropPen, r.Inflate(3), rad + 2, rad + 2);
        }

        // Every selected object gets an outline (dashed when locked); handles only on one unlocked object.
        if (_sel.Count > 0)
            foreach (var o in Scene.Objects)
            {
                if (!_sel.Contains(o.Id)) continue;
                var r = ToScreen(o.X, o.Y, o.Width, o.Height);
                double rad = Math.Min(6, Math.Min(r.Width, r.Height) / 4) + 2;
                ctx.DrawRectangle(null, o.Locked ? LockedPen : SelectPen, r.Inflate(2), rad, rad);
            }
        if (primary != null)
        {
            var r = ToScreen(primary.X, primary.Y, primary.Width, primary.Height);
            if (CanResize(primary))
                foreach (var h in HandleRects(r)) ctx.DrawRectangle(Palette.Accent, HandleEdge, h);
            string text = (primary.Locked ? "🔒 " : "") + primary.Name + (_sel.Count > 1 ? $"   +{_sel.Count - 1}" : "");
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, UiFont, 12, Palette.Text);
            var at = new Point(r.X + (r.Width - ft.Width) / 2, r.Bottom + 9);
            ctx.DrawRectangle(LabelBg, null, new Rect(at.X - 6, at.Y - 2, ft.Width + 12, ft.Height + 4), 4, 4);
            ctx.DrawText(ft, at);
        }

        Overlay?.Draw(ctx, this);
        if ((_mode == Mode.Tool && ToolShowsBox) || (_mode == Mode.Box && _boxShown))
        {
            var end = _mode == Mode.Box ? _boxEnd : _toolLast;
            var a = ToScreen(_pressWorld.X, _pressWorld.Y);
            var b = ToScreen(end.X, end.Y);
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

    /// <summary>Handles show on one selected object that isn't locked.</summary>
    bool CanResize(SceneObject o) => _sel.Count == 1 && !o.Locked;

    // ---------------- Hit testing ----------------

    /// <summary>The top object under a scene point (locked ones too unless <paramref name="includeLocked"/> is false).</summary>
    public SceneObject? ObjectAt(Point world, bool includeLocked = true) => SceneEdit.At(Scene, world.X, world.Y, includeLocked);

    int HandleAt(Point screen)
    {
        var sel = Scene.Find(_primary);
        if (sel == null || !CanResize(sel)) return -1;
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
        if (_primary == id && _sel.Count == (id == 0 ? 0 : 1)) return;
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
        _moved = false;
        _collapseOnRelease = false;
        var mods = e.KeyModifiers;
        if (pt.Properties.IsMiddleButtonPressed || (pt.Properties.IsLeftButtonPressed && _spaceDown))
        {
            _mode = Mode.Pan;
            Cursor = HandCursor;
        }
        else if ((pt.Properties.IsLeftButtonPressed || pt.Properties.IsRightButtonPressed) && Overlay != null && Overlay.Press(_pressWorld, pt.Properties.IsRightButtonPressed, mods))
        {
            _mode = Mode.Overlay;
            InvalidateVisual();
        }
        else if (pt.Properties.IsRightButtonPressed)
        {
            _mode = Mode.Right; // the menu opens on the release
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
            bool toggle = (mods & (KeyModifiers.Shift | KeyModifiers.Control)) != 0;
            int h = toggle ? -1 : HandleAt(pt.Position);
            if (h >= 0)
            {
                var sel = Scene.Find(_primary)!;
                _mode = Mode.Resize;
                _handle = h;
                _origRect = new Rect(sel.X, sel.Y, sel.Width, sel.Height);
            }
            else if (ObjectAt(_pressWorld, includeLocked: false) is { } o)
            {
                if (e.ClickCount >= 2 && !toggle)
                {
                    _mode = Mode.None;
                    DoubleClicked?.Invoke(o);
                    e.Handled = true;
                    return;
                }
                if (toggle && _sel.Contains(o.Id))
                {
                    // Shift/Ctrl+click on a selected object takes it out of the selection.
                    _sel.Remove(o.Id);
                    if (_primary == o.Id) _primary = Scene.Objects.LastOrDefault(x => _sel.Contains(x.Id))?.Id ?? 0;
                    SelectionChanged?.Invoke();
                    InvalidateVisual();
                    _mode = Mode.None;
                    e.Handled = true;
                    return;
                }
                if (toggle) { _sel.Add(o.Id); _primary = o.Id; SelectionChanged?.Invoke(); }
                else if (!_sel.Contains(o.Id)) Select(o.Id);
                else
                {
                    // Grabbing one of several selected objects moves them all; a click without a drag keeps only it.
                    _collapseOnRelease = _sel.Count > 1;
                    if (_primary != o.Id) { _primary = o.Id; SelectionChanged?.Invoke(); }
                }
                StartMove(o);
                InvalidateVisual();
            }
            else
            {
                // Empty space: a selection box (Shift/Ctrl keeps what was selected).
                _boxAdd = toggle;
                _boxBase.Clear();
                if (_boxAdd) _boxBase.UnionWith(_sel);
                else if (_sel.Count > 0) { SelectedId = 0; SelectionChanged?.Invoke(); }
                _mode = Mode.Box;
                _boxShown = false;
                _boxEnd = _pressWorld;
                InvalidateVisual();
            }
        }
        else return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    void StartMove(SceneObject grabbed)
    {
        _mode = Mode.Move;
        _grabId = grabbed.Id;
        _origPos.Clear();
        foreach (var o in Scene.Objects)
            if (_sel.Contains(o.Id) && !o.Locked) _origPos[o.Id] = new Point(o.X, o.Y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        PointerWorld = ToWorld(p);
        PointerWorldChanged?.Invoke(PointerWorld);
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
            case Mode.Right:
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
            case Mode.Box:
            {
                _boxEnd = ToWorld(p);
                if (!_boxShown && Math.Abs(p.X - _pressScreen.X) < 4 && Math.Abs(p.Y - _pressScreen.Y) < 4) return; // a click, not a box (yet)
                _boxShown = true;
                var hits = SceneEdit.InBox(Scene, _pressWorld.X, _pressWorld.Y, _boxEnd.X, _boxEnd.Y);
                SetSelection(_boxBase.Concat(hits), hits.Count > 0 ? hits[^1] : _primary);
                return;
            }
            case Mode.Move:
            {
                if (!_moved && Math.Abs(p.X - _pressScreen.X) < 3 && Math.Abs(p.Y - _pressScreen.Y) < 3) return;
                if (!_origPos.TryGetValue(_grabId, out var grab)) return; // nothing that can move
                _moved = true;
                var w = ToWorld(p);
                bool axis = (e.KeyModifiers & KeyModifiers.Shift) != 0;
                var (dx, dy) = SceneEdit.DragDelta(grab.X, grab.Y, w.X - _pressWorld.X, w.Y - _pressWorld.Y, snap, axis);
                var g = Scene.Find(_grabId);
                if (g == null || (g.X == grab.X + dx && g.Y == grab.Y + dy)) return;
                StartEdit();
                foreach (var (id, start) in _origPos)
                    if (Scene.Find(id) is { } o) { o.X = start.X + dx; o.Y = start.Y + dy; }
                Edited?.Invoke();
                InvalidateVisual();
                return;
            }
            case Mode.Resize:
            {
                var o = Scene.Find(_primary);
                if (o == null) return;
                var w = ToWorld(p);
                double ex = snap ? SceneEdit.Snap(w.X) : w.X, ey = snap ? SceneEdit.Snap(w.Y) : w.Y;
                double? ratio = null;
                if ((e.KeyModifiers & KeyModifiers.Shift) != 0 || o.KeepAspect)
                    ratio = PictureAspect?.Invoke(o) ?? (_origRect.Height > 0 ? _origRect.Width / _origRect.Height : null);
                var (l, t, rw, rh) = SceneEdit.Resize(_origRect.X, _origRect.Y, _origRect.Width, _origRect.Height, _handle, ex, ey, ratio);
                if (l == o.X && t == o.Y && rw == o.Width && rh == o.Height) return;
                StartEdit();
                o.X = l; o.Y = t; o.Width = rw; o.Height = rh;
                Edited?.Invoke();
                InvalidateVisual();
                return;
            }
        }
        // Hover feedback.
        if (ToolActive) { Cursor = _spaceDown ? HandCursor : CrossCursor; return; }
        int h = HandleAt(p);
        Cursor = h >= 0 ? HandleCursors[h] : _spaceDown ? HandCursor : ObjectAt(ToWorld(p), includeLocked: false) != null ? MoveCursor : Cursor.Default;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_mode != Mode.None) return; // still dragging (captured)
        PointerWorld = null;
        PointerWorldChanged?.Invoke(null);
    }

    void StartEdit()
    {
        if (_editStarted) return;
        _editStarted = true;
        BeforeEdit?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mode == Mode.None) return;
        var mode = _mode;
        if (mode == Mode.Overlay) { Overlay?.Release(); InvalidateVisual(); }
        _mode = Mode.None;
        e.Pointer.Capture(null);
        switch (mode)
        {
            case Mode.Tool: EndToolUse(); return;
            case Mode.Right:
            {
                var w = ToWorld(e.GetPosition(this));
                var hit = ObjectAt(w, includeLocked: true);
                // Right-clicking an unlocked object that isn't selected selects it, so the menu acts on it.
                if (hit is { Locked: false } && !_sel.Contains(hit.Id)) Select(hit.Id);
                MenuRequested?.Invoke(w, hit);
                return;
            }
            case Mode.Box:
                _boxShown = false;
                SelectionChanged?.Invoke();
                InvalidateVisual();
                break;
            case Mode.Move when !_moved && _collapseOnRelease:
                Select(_grabId);
                break;
        }
        Cursor = Cursor.Default;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        var mode = _mode;
        _mode = Mode.None;
        if (mode == Mode.Tool) EndToolUse(); // the use still ends (one undo step) when the pointer is taken away
        if (mode == Mode.Box) { _boxShown = false; SelectionChanged?.Invoke(); InvalidateVisual(); }
    }

    /// <summary>Esc while dragging: objects go back where they were, a selection box is dropped. False when nothing was being dragged.</summary>
    public bool CancelDrag()
    {
        var mode = _mode;
        switch (mode)
        {
            case Mode.Move:
                foreach (var (id, start) in _origPos)
                    if (Scene.Find(id) is { } o) { o.X = start.X; o.Y = start.Y; }
                break;
            case Mode.Resize:
                if (Scene.Find(_primary) is { } r) { r.X = _origRect.X; r.Y = _origRect.Y; r.Width = _origRect.Width; r.Height = _origRect.Height; }
                break;
            case Mode.Box:
                SetSelection(_boxBase, _primary);
                _boxShown = false;
                break;
            default:
                return false;
        }
        _mode = Mode.None;
        if (mode == Mode.Box) SelectionChanged?.Invoke(); else Edited?.Invoke();
        InvalidateVisual();
        return true;
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

    /// <summary>Snap/test helper: drags a selection box from one scene point to another (null hides it) and selects what it touches.</summary>
    public void ShowSelectionBox(Point? from, Point to)
    {
        if (from is not { } f) { _mode = Mode.None; _boxShown = false; InvalidateVisual(); return; }
        _mode = Mode.Box;
        _pressWorld = f;
        _boxEnd = to;
        _boxShown = true;
        _boxBase.Clear();
        var hits = SceneEdit.InBox(Scene, f.X, f.Y, to.X, to.Y);
        SetSelection(hits);
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
        var d = e.Delta;
        if (d.X == 0 && d.Y == 0) return;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            double dy = d.Y != 0 ? d.Y : d.X;
            ZoomBy(dy > 0 ? 1.15 : 1 / 1.15, e.GetPosition(this));
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && d.X == 0) PanBy(d.Y * 48, 0); // Shift+wheel: sideways
        else PanBy(d.X * 48, d.Y * 48); // the wheel scrolls (a touchpad scrolls both ways)
        e.Handled = true;
    }

    // ---------------- Keyboard ----------------
    // The window handles the editing keys (Delete, arrows, Ctrl+C...); the canvas only keeps Space for panning.

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space) { _spaceDown = true; Cursor = HandCursor; e.Handled = true; }
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
        int id = ObjectAt(ToWorld(e.GetPosition(this)), includeLocked: false)?.Id ?? 0;
        if (id != _dropHoverId) { _dropHoverId = id; InvalidateVisual(); }
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        _dropHoverId = 0;
        var name = DroppedMechanism(e);
        if (name == null) return;
        var w = ToWorld(e.GetPosition(this));
        MechanismDropped?.Invoke(name, w, ObjectAt(w, includeLocked: false));
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
