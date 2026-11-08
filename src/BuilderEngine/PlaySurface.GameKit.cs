using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BuilderEngine.Script;

namespace BuilderEngine;

// GAME KIT drawing and input: darkness and lights (a small light map drawn smoothly over the view), screen static and
// tint, the hotbar, captions in objects, objects on the screen, and the mouse (moves, buttons, wheel, cursor).
sealed partial class PlaySurface
{
    readonly List<GameObject> _uiOrder = [];
    readonly SolidColorBrush _tintBrush = new(Colors.Black);
    static readonly RenderOptions Smooth = new() { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality };

    // ---------------- Darkness and lights ----------------

    /// <summary>View pixels per light-map cell: 800 × 450 → 200 × 113 cells, drawn smoothly.</summary>
    const double LightCell = 4;
    readonly LightBuffer _light = new();
    WriteableBitmap? _lightBmp;
    int[] _lightInts = [];

    void DrawLighting(DrawingContext ctx, Rect area)
    {
        if (!_world.LightingOn) return;
        var cam = _world.Camera;
        int w = Math.Max(1, (int)Math.Ceiling(cam.BaseWidth / LightCell)), h = Math.Max(1, (int)Math.Ceiling(cam.BaseHeight / LightCell));
        _light.Resize(w, h);
        _world.ComputeLighting(_light, cam.Left, cam.Top, cam.ViewWidth, cam.ViewHeight);
        if (_lightBmp == null || _lightBmp.PixelSize.Width != w || _lightBmp.PixelSize.Height != h)
        {
            _lightBmp?.Dispose();
            _lightBmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            _lightInts = new int[w * h];
        }
        CopyPixels(_light.Pixels, _lightInts, _lightBmp, w, h);
        using (ctx.PushRenderOptions(Smooth))
            ctx.DrawImage(_lightBmp, new Rect(0, 0, w, h), area);
    }

    static void CopyPixels(uint[] src, int[] ints, WriteableBitmap bmp, int w, int h)
    {
        Buffer.BlockCopy(src, 0, ints, 0, w * h * 4);
        using var fb = bmp.Lock();
        for (int y = 0; y < h; y++) Marshal.Copy(ints, y * w, fb.Address + y * fb.RowBytes, w);
    }

    // ---------------- Screen static ----------------

    WriteableBitmap? _noiseBmp;
    readonly uint[] _noisePx = new uint[160 * 90];
    readonly int[] _noiseInts = new int[160 * 90];
    readonly Random _noiseRng = new();

    void DrawNoise(DrawingContext ctx, Rect area)
    {
        double amount = _world.NoiseAmount;
        if (amount <= 0.001) return;
        _noiseBmp ??= new WriteableBitmap(new PixelSize(160, 90), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        for (int i = 0; i < _noisePx.Length; i++)
        {
            uint v = (uint)_noiseRng.Next(256);
            uint a = (uint)(Math.Clamp(amount, 0, 1) * (70 + _noiseRng.Next(150)));
            _noisePx[i] = (a << 24) | (v << 16) | (v << 8) | v;
        }
        // A few brighter scan lines rolling down.
        int band = (int)(_world.RealTime * 40) % 90;
        for (int x = 0; x < 160; x++) _noisePx[band * 160 + x] |= 0x00303030;
        CopyPixels(_noisePx, _noiseInts, _noiseBmp, 160, 90);
        using (ctx.PushRenderOptions(Crisp))
            ctx.DrawImage(_noiseBmp, new Rect(0, 0, 160, 90), area);
    }

    // ---------------- Captions ----------------

    readonly Dictionary<GameObject, (string Text, double Size, string Color, FormattedText Ft)> _captions = new();

    void DrawCaption(DrawingContext ctx, GameObject o, Rect r)
    {
        string text = o.Caption!;
        if (!_captions.TryGetValue(o, out var c) || c.Text != text || c.Size != o.CaptionSize || c.Color != o.CaptionColor)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HudFont, Math.Max(1, o.CaptionSize),
                SceneCanvas.BrushOf(o.CaptionColor, Palette.Text)) { TextAlignment = TextAlignment.Center, MaxTextWidth = Math.Max(8, r.Width - 4) };
            if (_captions.Count > 4000) _captions.Clear();
            _captions[o] = c = (text, o.CaptionSize, o.CaptionColor, ft);
        }
        c.Ft.MaxTextWidth = Math.Max(8, r.Width - 4);
        ctx.DrawText(c.Ft, new Point(r.X + 2, r.Y + (r.Height - c.Ft.Height) / 2));
    }

    // ---------------- Hotbar ----------------

    static readonly IBrush SlotBg = new SolidColorBrush(Color.FromArgb(200, 18, 18, 18)).ToImmutable();
    static readonly IPen SlotPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 90, 90, 90)), 1).ToImmutable();
    static readonly IPen SlotSelected = new Pen(Palette.Accent, 2.5).ToImmutable();
    readonly Dictionary<string, FormattedText> _countFts = new();

    void DrawHotbar(DrawingContext ctx, Rect area, double scale)
    {
        var h = _world.Hotbar;
        if (!h.Visible || h.Slots <= 0) return;
        var cam = _world.Camera;
        double size = h.SlotSize, gap = 6;
        double total = h.Slots * size + (h.Slots - 1) * gap;
        double x0 = double.IsNaN(h.X) ? (cam.BaseWidth - total) / 2 : h.X, y0 = double.IsNaN(h.Y) ? cam.BaseHeight - size - 10 : h.Y;
        var inv = _world.Inventory;
        for (int i = 0; i < h.Slots; i++)
        {
            var r = new Rect(area.X + (x0 + i * (size + gap)) * scale, area.Y + y0 * scale, size * scale, size * scale);
            ctx.DrawRectangle(SlotBg, i == h.Selected ? SlotSelected : SlotPen, r, 6 * scale, 6 * scale);
            if (i >= inv.Count) continue;
            var stack = inv[i];
            var icon = r.Deflate(7 * scale);
            var look = _world.ItemLooks.GetValueOrDefault(stack.Name);
            if (look != null && look.Image.Length > 0 && _images.Get(look.Image) is { } bmp)
                using (ctx.PushRenderOptions(Crisp)) ctx.DrawImage(bmp, new Rect(bmp.Size), icon);
            else ctx.DrawRectangle(SceneCanvas.BrushOf(look?.Color ?? "#A3A3A3", Fallback), null, icon, 3 * scale, 3 * scale);
            if (stack.Count > 1)
            {
                string key = stack.Count + "|" + scale.ToString("0.###", CultureInfo.InvariantCulture);
                if (!_countFts.TryGetValue(key, out var ft))
                {
                    if (_countFts.Count > 500) _countFts.Clear();
                    _countFts[key] = ft = new FormattedText(stack.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, HudFont, 12 * scale, Palette.Text);
                }
                ctx.DrawText(ft, new Point(r.Right - ft.Width - 4 * scale, r.Bottom - ft.Height - 2 * scale));
            }
        }
    }

    // ---------------- Mouse ----------------

    /// <summary>A point of this control in the game's view pixels, and whether it is inside the view.</summary>
    (double X, double Y, bool Inside) ViewPoint(Point p)
    {
        var (area, scale) = Viewport();
        if (scale <= 0) return (0, 0, false);
        return ((p.X - area.X) / scale, (p.Y - area.Y) / scale, area.Contains(p));
    }

    void GameMouseDown(PointerPressedEventArgs e, Point p)
    {
        if (Debugger?.Paused == true) return; // a paused game: the click only looks inside an object
        var (x, y, inside) = ViewPoint(p);
        _world.MouseMove(x, y, inside);
        var props = e.GetCurrentPoint(this).Properties;
        string button = props.IsRightButtonPressed ? "right" : props.IsMiddleButtonPressed ? "middle" : "left";
        e.Pointer.Capture(this);
        _world.MouseDown(button);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var (x, y, inside) = ViewPoint(e.GetPosition(this));
        _world.MouseMove(x, y, inside || _world.Dragging != null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var (x, y, inside) = ViewPoint(e.GetPosition(this));
        _world.MouseMove(x, y, inside);
        string button = e.InitialPressMouseButton switch { MouseButton.Right => "right", MouseButton.Middle => "middle", _ => "left" };
        _world.MouseUp(button);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _world.MouseWheel(e.Delta.Y);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_world.Dragging == null) _world.MouseMove(_world.MouseViewX, _world.MouseViewY, inside: false);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _world.ReleaseMouse();
    }

    string _cursorShown = "default";
    static readonly Dictionary<string, Cursor> Cursors = new()
    {
        ["default"] = new Cursor(StandardCursorType.Arrow), ["hand"] = new Cursor(StandardCursorType.Hand),
        ["cross"] = new Cursor(StandardCursorType.Cross), ["none"] = new Cursor(StandardCursorType.None),
        ["move"] = new Cursor(StandardCursorType.SizeAll), ["text"] = new Cursor(StandardCursorType.Ibeam),
        ["wait"] = new Cursor(StandardCursorType.Wait),
    };

    /// <summary>The pointer's look follows the game ('cursor "hand"', a hand over something clickable).</summary>
    void UpdateCursor()
    {
        string want = _world.CursorNow;
        if (want == _cursorShown) return;
        _cursorShown = want;
        Cursor = Cursors.GetValueOrDefault(want) ?? Cursors["default"];
    }
}
