using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// GAME KIT, editor: painting tiles. The TILES row (under the tools) makes a tile map for the scene, lists its kinds
// (click one), and switches the canvas to Brush / Erase / Fill: left button paints, right button erases. Each stroke is
// one Ctrl+Z. The map itself is drawn by TileDraw (also in the game).
public sealed partial class MainWindow
{
    readonly StackPanel _tileRow = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    TileOverlay? _tileOverlay;
    /// <summary>The kind painted (1 = the scene's first kind).</summary>
    int _tileKind = 1;
    string _tileMode = "brush";
    Scene? _tileScene;

    static readonly (string Name, string Color, bool Solid)[] StarterTiles =
    [
        ("dirt", "#8B5A2B", true), ("grass", "#5FA84A", true), ("stone", "#6E6E73", true), ("brick", "#A0522D", true),
        ("wood", "#C08040", true), ("water", "#3A78C2", false), ("wall", "#3A3330", false),
    ];

    /// <summary>The TILES row (put under the TOOLS row).</summary>
    Control TileBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 12, 6), VerticalAlignment = VerticalAlignment.Center };
        var label = Label("TILES");
        ToolTip.SetTip(label, "A tile map: a grid of cells you paint. Solid tiles are ground bodies stand on; scripts dig and place them (break_tile, place_tile).");
        row.Children.Add(label);
        row.Children.Add(_tileRow);
        RefreshTileBar();
        return row;
    }

    void RefreshTileBar()
    {
        _tileScene = _scene;
        _tileRow.Children.Clear();
        var s = _scene;
        if (s.TileLayers is not { Count: > 0 })
        {
            var make = ToolButton("＋ Tile map", "Make a tile map the size of the scene (32-pixel cells) with a few kinds of tile to paint.", false);
            make.Click += (_, _) => MakeTileMap();
            _tileRow.Children.Add(make);
            if (_tileOverlay != null) { _tileOverlay = null; UpdateCanvasOverlay(); }
            return;
        }
        var kinds = s.TileKinds ??= [];
        if (_tileKind > kinds.Count) _tileKind = Math.Max(1, kinds.Count);
        foreach (var (mode, text, tip) in new[] { ("brush", "🖌 Brush", "Click or drag to paint the selected tile (right button erases)."), ("erase", "⌫ Erase", "Click or drag to empty cells."), ("fill", "▦ Fill", "Fill the connected area of the same tile.") })
        {
            var b = ToolButton(text, tip, _tileOverlay != null && _tileMode == mode);
            b.Click += (_, _) => SetTileMode(_tileOverlay != null && _tileMode == mode ? null : mode);
            _tileRow.Children.Add(b);
        }
        for (int i = 0; i < kinds.Count; i++)
        {
            int id = i + 1;
            var k = kinds[i];
            var sw = new Button
            {
                Width = 26, Height = 26, Padding = new Thickness(0), CornerRadius = new CornerRadius(5),
                Background = SceneCanvas.BrushOf(k.Color, Palette.Accent),
                BorderBrush = id == _tileKind ? Palette.Accent : Palette.Border, BorderThickness = new Thickness(id == _tileKind ? 2.5 : 1),
            };
            ToolTip.SetTip(sw, $"{k.Name}{(k.Solid ? "" : " (background)")}{(k.Breakable ? "" : " — can't be broken")}");
            sw.Click += (_, _) => { _tileKind = id; if (_tileOverlay == null || _tileMode == "erase") SetTileMode("brush"); else RefreshTileBar(); };
            _tileRow.Children.Add(sw);
        }
        var add = ToolButton("＋ Kind", "Add a kind of tile (a name and a colour; edit pictures, hardness and drops in scripts with tile_kind).", false);
        add.Click += async (_, _) => await AddTileKind();
        _tileRow.Children.Add(add);
        var l = s.TileLayers[0];
        var size = ToolButton($"{l.Columns} × {l.Rows}", "The map's size in cells: click to change it.", false);
        size.Click += async (_, _) => await ResizeTileMap();
        _tileRow.Children.Add(size);
    }

    void SetTileMode(string? mode)
    {
        if (mode == null) _tileOverlay = null;
        else
        {
            _tileMode = mode;
            _tileOverlay ??= new TileOverlay(this);
            SetActiveTool(null, quiet: true);
        }
        UpdateCanvasOverlay();
        RefreshTileBar();
        _canvas.InvalidateVisual();
        if (mode != null) SetStatus(true, mode == "fill" ? "Fill: click an area to fill it." : "Paint tiles: left button paints, right button erases. Click the mode again to stop.");
    }

    void MakeTileMap()
    {
        Checkpoint();
        var s = _scene;
        s.TileKinds = StarterTiles.Select(t => new TileKind { Name = t.Name, Color = t.Color, Solid = t.Solid }).ToList();
        s.TileLayers = [new TileLayer((int)Math.Ceiling(s.Width / 32), (int)Math.Ceiling(s.Height / 32), 32)];
        _tileKind = 1;
        SetTileMode("brush");
    }

    async Task AddTileKind()
    {
        string? name = await Dialogs.Input(this, "New kind of tile", "Its name (scripts use it: place_tile \"name\" at x, y):", "tile");
        if (string.IsNullOrWhiteSpace(name)) return;
        var kinds = _scene.TileKinds ??= [];
        name = name.Trim();
        if (kinds.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))) { SetStatus(false, $"There's already a tile called “{name}”."); return; }
        Checkpoint();
        kinds.Add(new TileKind { Name = name, Color = Swatches[kinds.Count % Swatches.Length] });
        _tileKind = kinds.Count;
        SetTileMode("brush");
    }

    async Task ResizeTileMap()
    {
        var l = _scene.TileLayers![0];
        string? text = await Dialogs.Input(this, "Tile map size", "Columns × rows (cells of " + l.CellSize + " px):", $"{l.Columns} x {l.Rows}");
        if (text == null) return;
        var parts = text.Split(['x', 'X', '×', ',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int c) || !int.TryParse(parts[1], out int r) || c < 1 || r < 1) { SetStatus(false, "Write it like 100 x 40."); return; }
        Checkpoint();
        foreach (var layer in _scene.TileLayers) layer.Resize(c, r);
        _scene.Width = Math.Max(_scene.Width, c * l.CellSize);
        _scene.Height = Math.Max(_scene.Height, r * l.CellSize);
        RefreshTileBar();
        _canvas.InvalidateVisual();
    }

    /// <summary>The scene's tiles in the editor canvas (behind the objects).</summary>
    internal static void DrawSceneTiles(DrawingContext ctx, Scene scene, Rect visible, ImageCache? images)
    {
        if (scene.TileLayers is not { Count: > 0 } layers) return;
        var kinds = scene.TileKinds ?? [];
        TileDraw.Draw(ctx, layers, kinds, visible, images, front: false);
        TileDraw.Draw(ctx, layers, kinds, visible, images, front: true);
    }

    /// <summary>Gets the canvas's presses while a tile mode is on.</summary>
    sealed class TileOverlay(MainWindow w) : ICanvasOverlay
    {
        static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1).ToImmutable();
        bool _erasing;

        public void Draw(DrawingContext ctx, SceneCanvas canvas)
        {
            if (w._tileScene != w._scene) Dispatcher.UIThread.Post(w.RefreshTileBar); // another scene, or undo: its kinds
            if (w._scene.TileLayers is not { Count: > 0 } layers) return;
            var l = layers[0];
            double cs = l.CellSize * canvas.Zoom;
            if (cs < 6) return;
            var a = canvas.WorldToScreen(new Point(l.X, l.Y));
            var b = canvas.WorldToScreen(new Point(l.X + l.Width, l.Y + l.Height));
            for (int c = 0; c <= l.Columns; c++)
            {
                double x = Math.Round(a.X + c * cs) + 0.5;
                if (x >= 0 && x <= canvas.Bounds.Width) ctx.DrawLine(GridPen, new Point(x, Math.Max(0, a.Y)), new Point(x, Math.Min(canvas.Bounds.Height, b.Y)));
            }
            for (int r = 0; r <= l.Rows; r++)
            {
                double y = Math.Round(a.Y + r * cs) + 0.5;
                if (y >= 0 && y <= canvas.Bounds.Height) ctx.DrawLine(GridPen, new Point(Math.Max(0, a.X), y), new Point(Math.Min(canvas.Bounds.Width, b.X), y));
            }
        }

        public bool Press(Point world, bool right, KeyModifiers mods)
        {
            if (w._scene.TileLayers is not { Count: > 0 }) return false;
            if (w._activeTool != null) // a tool was picked since: it gets the canvas
            {
                w._tileOverlay = null;
                Dispatcher.UIThread.Post(() => { w.UpdateCanvasOverlay(); w.RefreshTileBar(); });
                return false;
            }
            w.Checkpoint();
            _erasing = right || w._tileMode == "erase";
            if (w._tileMode == "fill" && !right) Fill(world);
            else Paint(world);
            return true;
        }

        public void Drag(Point world, KeyModifiers mods)
        {
            if (w._tileMode != "fill" || _erasing) Paint(world);
        }

        public void Release() { }

        TileLayer Layer => w._scene.TileLayers![0];

        void Paint(Point world)
        {
            var l = Layer;
            var (c, r) = l.CellAt(world.X, world.Y);
            if (l.Set(c, r, (ushort)(_erasing ? 0 : w._tileKind))) w._canvas.InvalidateVisual();
        }

        void Fill(Point world)
        {
            var l = Layer;
            var (c0, r0) = l.CellAt(world.X, world.Y);
            if (!l.Inside(c0, r0)) return;
            ushort from = l.Get(c0, r0), to = (ushort)w._tileKind;
            if (from == to) return;
            var stack = new Stack<(int, int)>();
            stack.Push((c0, r0));
            int guard = 0;
            while (stack.Count > 0 && guard++ < 2_000_000)
            {
                var (c, r) = stack.Pop();
                if (!l.Inside(c, r) || l.Get(c, r) != from) continue;
                l.Set(c, r, to);
                stack.Push((c + 1, r)); stack.Push((c - 1, r)); stack.Push((c, r + 1)); stack.Push((c, r - 1));
            }
            w._canvas.InvalidateVisual();
        }
    }
}
