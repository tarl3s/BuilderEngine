using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// The pixel-art editor ("✎ Draw…" in a picture dropdown, ✎ on an animation frame): pencil, eraser, fill, colour
/// picker, a palette (+ lighter / darker for new colours), brush size, zoom, grid, undo/redo. Saves a PNG in the
/// project's images folder. The picture and its tools are <see cref="PixelCanvas"/> (headless, tested).
/// </summary>
sealed class SpriteEditorWindow : Window
{
    enum Tool { Pencil, Eraser, Fill, Picker }

    readonly string _projectDir;
    readonly string? _editing; // the picture opened for editing (its name), or null for a new one
    PixelCanvas _canvas;
    Tool _tool = Tool.Pencil;
    uint _color = 0xFF000000;
    int _brush = 1;
    readonly PixelView _view;
    readonly TextBox _name = new();
    readonly TextBlock _info = new() { Foreground = Palette.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _current = new() { Width = 44, Height = 44, CornerRadius = new CornerRadius(6), BorderBrush = Palette.Border, BorderThickness = new Thickness(1) };
    readonly WrapPanel _palette = new() { Width = 168 };
    readonly Dictionary<Tool, Button> _toolButtons = new();
    readonly List<Button> _brushButtons = new();
    readonly List<uint> _colors = new(PixelCanvas.DefaultPalette);

    /// <summary>The saved picture's name in the images folder (null = closed without saving).</summary>
    public string? SavedName { get; private set; }

    /// <summary>Opens <paramref name="picture"/> (a name in the images folder) for editing, or a new empty <paramref name="size"/>² picture.</summary>
    public SpriteEditorWindow(string projectDir, string? picture = null, int size = 32)
    {
        _projectDir = projectDir;
        Title = "Sprite editor";
        Width = 900; Height = 640; MinWidth = 720; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Bg1; Foreground = Palette.Text;
        WindowLook.Apply(Resources);

        _canvas = new PixelCanvas(size, size);
        string? loaded = null;
        if (!string.IsNullOrWhiteSpace(picture) && Images.Resolve(projectDir, picture) is { } path && PictureDecode.Load(path) is { } pic)
        {
            _canvas = PixelCanvas.From(pic.W, pic.H, pic.Px);
            loaded = Path.GetFileName(path);
            foreach (var c in _canvas.UsedColors(16)) if (!_colors.Contains(c)) _colors.Add(c);
        }
        _editing = loaded;
        _name.Text = loaded != null ? Path.ChangeExtension(loaded, ".png") : SpriteSheet.FreePictureName(projectDir, "sprite");

        _view = new PixelView(this);
        Content = Build();
        UpdateTools();
        RefreshPalette();
        PickColor(_canvas.UsedColors(1).FirstOrDefault() is var first && first != 0 ? first : 0xFF000000);
        KeyDown += OnKey;
        _view.Refresh();
    }

    // ---------------- Layout ----------------

    Control Build()
    {
        var root = new DockPanel { Margin = new Thickness(14) };

        // Top: new size, undo/redo, flip, clear, zoom, grid.
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };
        var sizes = new ComboBox { ItemsSource = new[] { "8 × 8", "16 × 16", "24 × 24", "32 × 32", "48 × 48", "64 × 64", "128 × 128" }, PlaceholderText = "New picture…", MinWidth = 140, Background = Palette.Bg2 };
        ToolTip.SetTip(sizes, "Start a new empty picture of this size (Ctrl+Z brings the old one back)");
        sizes.SelectionChanged += (_, _) =>
        {
            if (sizes.SelectedItem is not string s) return;
            int n = int.Parse(s.Split(' ')[0]);
            _canvas.BeginStroke();
            _canvas.Resize(n, n);
            _canvas.Clear();
            sizes.SelectedItem = null;
            _view.Changed(fit: true);
        };
        top.Children.Add(sizes);
        top.Children.Add(Sep());
        top.Children.Add(Small("↶ Undo", "Undo (Ctrl+Z)", () => { if (_canvas.Undo()) _view.Changed(fit: false); }));
        top.Children.Add(Small("↷ Redo", "Redo (Ctrl+Y)", () => { if (_canvas.Redo()) _view.Changed(fit: false); }));
        top.Children.Add(Sep());
        top.Children.Add(Small("⇋ Flip", "Mirror the picture left to right", () => { _canvas.BeginStroke(); _canvas.FlipHorizontal(); _view.Changed(fit: false); }));
        top.Children.Add(Small("Clear", "Erase everything", () => { _canvas.BeginStroke(); _canvas.Clear(); _view.Changed(fit: false); }));
        top.Children.Add(Sep());
        top.Children.Add(Small("−", "Zoom out (wheel)", () => _view.ZoomBy(-1)));
        top.Children.Add(Small("+", "Zoom in (wheel)", () => _view.ZoomBy(+1)));
        var grid = Small("Grid", "Show or hide the pixel grid", () => { });
        grid.Click += (_, _) => { _view.ShowGrid = !_view.ShowGrid; _view.InvalidateVisual(); };
        top.Children.Add(grid);
        top.Children.Add(_info);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // Bottom: name + save.
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var save = MainWindow.AccentButton("Save");
        save.Click += async (_, _) => await SaveAsync();
        var cancel = MainWindow.PlainButton("Cancel");
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        var nameLabel = new TextBlock { Text = "Save as", Foreground = Palette.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(nameLabel, Dock.Left);
        bottom.Children.Add(nameLabel);
        _name.Background = Palette.Bg2; _name.Foreground = Palette.Text; _name.BorderBrush = Palette.Border;
        _name.Margin = new Thickness(0, 0, 12, 0); _name.MinHeight = 32;
        ToolTip.SetTip(_name, "The picture's name in the project's images folder");
        bottom.Children.Add(_name);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // Left: tools, brush size, colours.
        var left = new StackPanel { Spacing = 8, Width = 180, Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(MainWindow.Label("TOOLS"));
        var tools = new WrapPanel();
        foreach (var (t, text, tip) in new[]
                 {
                     (Tool.Pencil, "✎ Pencil", "Draw (B)"), (Tool.Eraser, "⌫ Eraser", "Erase (E); the right button erases too"),
                     (Tool.Fill, "▨ Fill", "Fill an area of one colour (F)"), (Tool.Picker, "⦿ Picker", "Take a colour from the picture (I, or Alt+click)")
                 })
        {
            var b = Small(text, tip, () => { _tool = t; UpdateTools(); });
            b.Margin = new Thickness(0, 0, 6, 6);
            b.MinWidth = 82;
            _toolButtons[t] = b;
            tools.Children.Add(b);
        }
        left.Children.Add(tools);
        left.Children.Add(MainWindow.Label("BRUSH"));
        var brushes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (int n in new[] { 1, 2, 3, 4 })
        {
            var b = Small($"{n} px", $"A {n}×{n} brush", () => { _brush = n; UpdateTools(); });
            b.Tag = n;
            _brushButtons.Add(b);
            brushes.Children.Add(b);
        }
        left.Children.Add(brushes);
        left.Children.Add(MainWindow.Label("COLOUR"));
        var curRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        curRow.Children.Add(_current);
        var shades = new StackPanel { Spacing = 4 };
        shades.Children.Add(Small("Lighter", "A lighter version of this colour (added to the palette)", () => AddShade(0.25)));
        shades.Children.Add(Small("Darker", "A darker version of this colour (added to the palette)", () => AddShade(-0.25)));
        curRow.Children.Add(shades);
        left.Children.Add(curRow);
        left.Children.Add(_palette);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);

        root.Children.Add(new Border { Background = Palette.Bg0, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _view });
        return root;
    }

    static Border Sep() => new() { Width = 1, Height = 20, Background = Palette.Border, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };

    static Button Small(string text, string tip, Action click)
    {
        var b = MainWindow.PlainButton(text);
        b.Padding = new Thickness(10, 5);
        b.FontSize = 12;
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    void UpdateTools()
    {
        foreach (var (t, b) in _toolButtons) Highlight(b, t == _tool);
        foreach (var b in _brushButtons) Highlight(b, (int)b.Tag! == _brush);
        _info.Text = $"   {_canvas.Width} × {_canvas.Height} px · {_tool}";
    }

    static void Highlight(Button b, bool on)
    {
        b.Background = on ? Palette.AccentSoft : Palette.Bg2;
        b.BorderBrush = on ? Palette.Accent : Palette.Border;
    }

    void RefreshPalette()
    {
        _palette.Children.Clear();
        _palette.Children.Add(Swatch(0, "Transparent (an eraser colour)"));
        foreach (var c in _colors) _palette.Children.Add(Swatch(c, PixelCanvas.Hex(c)));
    }

    Control Swatch(uint c, string tip)
    {
        bool on = c == _color;
        var b = new Border
        {
            Width = 22, Height = 22, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(4),
            Background = c == 0 ? Checker(6) : new SolidColorBrush(Color.FromUInt32(c)),
            BorderBrush = on ? Palette.Text : Palette.Border, BorderThickness = new Thickness(on ? 2 : 1), Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(b, tip);
        b.PointerPressed += (_, e) => { e.Handled = true; PickColor(c); };
        return b;
    }

    internal void PickColor(uint c)
    {
        _color = c;
        if (c != 0 && !_colors.Contains(c)) _colors.Add(c);
        _current.Background = c == 0 ? Checker(8) : new SolidColorBrush(Color.FromUInt32(c));
        ToolTip.SetTip(_current, c == 0 ? "Transparent" : PixelCanvas.Hex(c));
        RefreshPalette();
    }

    void AddShade(double amount)
    {
        if (_color == 0) return;
        PickColor(PixelArt.Shade(_color, amount));
    }

    internal static IBrush Checker(double cell)
    {
        var light = new SolidColorBrush(Color.Parse("#3A3A3A"));
        var dark = new SolidColorBrush(Color.Parse("#262626"));
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing { Brush = dark, Geometry = new RectangleGeometry(new Rect(0, 0, cell * 2, cell * 2)) });
        g.Children.Add(new GeometryDrawing { Brush = light, Geometry = new RectangleGeometry(new Rect(0, 0, cell, cell)) });
        g.Children.Add(new GeometryDrawing { Brush = light, Geometry = new RectangleGeometry(new Rect(cell, cell, cell, cell)) });
        return new DrawingBrush(g) { TileMode = TileMode.Tile, DestinationRect = new RelativeRect(0, 0, cell * 2, cell * 2, RelativeUnit.Absolute) };
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Z when ctrl: if (_canvas.Undo()) _view.Changed(false); break;
            case Key.Y when ctrl: if (_canvas.Redo()) _view.Changed(false); break;
            case Key.B: _tool = Tool.Pencil; UpdateTools(); break;
            case Key.E: _tool = Tool.Eraser; UpdateTools(); break;
            case Key.F: _tool = Tool.Fill; UpdateTools(); break;
            case Key.I: _tool = Tool.Picker; UpdateTools(); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------- Drawing (called by the view) ----------------

    int _lastX = int.MinValue, _lastY;
    bool _erasing;

    internal void StrokeStart(int x, int y, bool right, bool alt)
    {
        if (alt || _tool == Tool.Picker) { if (_canvas.Inside(x, y)) PickColor(_canvas.Pick(x, y)); return; }
        _erasing = right || _tool == Tool.Eraser;
        if (_tool == Tool.Fill && !right)
        {
            if (!_canvas.Inside(x, y) || _canvas.Pick(x, y) == _color) return; // nothing would change: no empty undo step
            _canvas.BeginStroke();
            _canvas.Fill(x, y, _color);
            _view.Changed(false);
            return;
        }
        _canvas.BeginStroke();
        _lastX = x; _lastY = y;
        Paint(x, y, x, y);
    }

    internal void StrokeMove(int x, int y)
    {
        if (_lastX == int.MinValue) return;
        Paint(_lastX, _lastY, x, y);
        _lastX = x; _lastY = y;
    }

    internal void StrokeEnd() => _lastX = int.MinValue;

    void Paint(int x0, int y0, int x1, int y1)
    {
        _canvas.Line(x0, y0, x1, y1, _erasing ? 0 : _color, _brush);
        _view.Changed(false);
    }

    internal PixelCanvas Canvas => _canvas;

    // ---------------- Saving ----------------

    async Task SaveAsync()
    {
        string name = (_name.Text ?? "").Trim();
        if (name.Length == 0) name = "sprite";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = Path.ChangeExtension(name, ".png");
        string path = Path.Combine(Images.Folder(_projectDir), name);
        bool same = _editing != null && string.Equals(Path.ChangeExtension(_editing, ".png"), name, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(path) && !same)
        {
            var r = await Dialogs.Ask(this, "Replace picture?", $"There's already a picture called “{name}”. Replace it? (Everything that shows it will show the new one.)", "Replace", "Cancel");
            if (r != "Replace") return;
        }
        if (!SaveTo(name)) return;
        Close(name);
    }

    /// <summary>Writes the picture as a PNG in the images folder. False (and a message) when it can't.</summary>
    internal bool SaveTo(string name)
    {
        try
        {
            PixelArt.Save(Path.Combine(Images.Folder(_projectDir), name), _canvas.ToPng());
            SavedName = name;
            return true;
        }
        catch (Exception e)
        {
            _info.Text = $"   Couldn't save: {e.Message}";
            _info.Foreground = Palette.Error;
            return false;
        }
    }

    // ---------------- The drawing area ----------------

    /// <summary>The picture, zoomed, on a checkerboard with an optional pixel grid; draws with the mouse.</summary>
    sealed class PixelView : Control
    {
        readonly SpriteEditorWindow _owner;
        WriteableBitmap? _bmp;
        int _bw, _bh;
        double _zoom = 12;
        bool _fitted;
        public bool ShowGrid = true;
        static readonly IBrush CheckerBrush = Checker(8);

        public PixelView(SpriteEditorWindow owner)
        {
            _owner = owner;
            ClipToBounds = true;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Cross);
        }

        PixelCanvas C => _owner.Canvas;

        public void Refresh()
        {
            if (_bmp == null || _bw != C.Width || _bh != C.Height)
            {
                _bmp = PictureDecode.ToBitmap(C.Width, C.Height, C.Pixels);
                _bw = C.Width; _bh = C.Height;
            }
            else PictureDecode.Write(_bmp, C.Width, C.Height, C.Pixels);
            InvalidateVisual();
        }

        public void Changed(bool fit)
        {
            if (fit) _fitted = false;
            Refresh();
            _owner.UpdateTools();
        }

        public void ZoomBy(int dir)
        {
            _zoom = Math.Clamp(dir > 0 ? _zoom * 1.25 : _zoom / 1.25, 1, 64);
            InvalidateVisual();
        }

        Rect Area()
        {
            if (!_fitted && Bounds.Width > 0)
            {
                _zoom = Math.Clamp(Math.Floor(Math.Min((Bounds.Width - 40) / C.Width, (Bounds.Height - 40) / C.Height)), 1, 64);
                _fitted = true;
            }
            double w = C.Width * _zoom, h = C.Height * _zoom;
            return new Rect(Math.Round((Bounds.Width - w) / 2), Math.Round((Bounds.Height - h) / 2), w, h);
        }

        public override void Render(DrawingContext ctx)
        {
            var r = Area();
            ctx.FillRectangle(CheckerBrush, r);
            if (_bmp != null)
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                    ctx.DrawImage(_bmp, new Rect(0, 0, _bw, _bh), r);
            if (ShowGrid && _zoom >= 6)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
                for (int x = 1; x < C.Width; x++) ctx.DrawLine(pen, new Point(r.X + x * _zoom + 0.5, r.Y), new Point(r.X + x * _zoom + 0.5, r.Bottom));
                for (int y = 1; y < C.Height; y++) ctx.DrawLine(pen, new Point(r.X, r.Y + y * _zoom + 0.5), new Point(r.Right, r.Y + y * _zoom + 0.5));
            }
            ctx.DrawRectangle(new Pen(Palette.Border, 1), r);
        }

        (int X, int Y) PixelAt(Point p)
        {
            var r = Area();
            return ((int)Math.Floor((p.X - r.X) / _zoom), (int)Math.Floor((p.Y - r.Y) / _zoom));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            var pt = e.GetCurrentPoint(this);
            var (x, y) = PixelAt(pt.Position);
            _owner.StrokeStart(x, y, pt.Properties.IsRightButtonPressed, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var (x, y) = PixelAt(e.GetPosition(this));
            _owner.StrokeMove(x, y);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            _owner.StrokeEnd();
            e.Pointer.Capture(null);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            ZoomBy(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }
    }
}
