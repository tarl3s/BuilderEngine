using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace BuilderEngine;

// GAME KIT: the minimap ('show minimap') — the world's one-pixel-per-cell picture (World.MinimapImage) scaled into a
// framed box in a corner of the screen, with a dot per marked object and a white one for the player.
sealed partial class PlaySurface
{
    WriteableBitmap? _miniBmp;
    int[] _miniInts = [];
    uint[]? _miniLast;
    int _miniVersion = int.MinValue;
    static readonly IBrush MiniBack = new SolidColorBrush(Color.FromArgb(210, 8, 8, 8)).ToImmutable();
    static readonly IPen MiniFrame = new Pen(new SolidColorBrush(Color.FromArgb(200, 120, 110, 100)), 1.5).ToImmutable();
    static readonly IPen DotPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 1).ToImmutable();

    void DrawMinimap(DrawingContext ctx, Rect area, double scale)
    {
        var m = _world.Minimap;
        if (!m.Visible) return;
        var px = _world.MinimapImage(out int cols, out int rows);
        if (px == null || cols <= 0 || rows <= 0) return;
        if (_miniBmp == null || _miniBmp.PixelSize.Width != cols || _miniBmp.PixelSize.Height != rows)
        {
            _miniBmp?.Dispose();
            _miniBmp = new WriteableBitmap(new PixelSize(cols, rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            _miniInts = new int[cols * rows];
            _miniLast = null;
        }
        // Copy only when the world made a new picture (it keeps the same array and bumps its version).
        int ver = m.ImageVersion;
        if (!ReferenceEquals(_miniLast, px) || ver != _miniVersion)
        {
            Buffer.BlockCopy(px, 0, _miniInts, 0, cols * rows * 4);
            using (var fb = _miniBmp.Lock())
                for (int y = 0; y < rows; y++) Marshal.Copy(_miniInts, y * cols, fb.Address + y * fb.RowBytes, cols);
            _miniLast = px;
            _miniVersion = ver;
        }
        // Keep the map's shape inside the box.
        double bw = m.Width * scale, bh = m.Height * scale, k = Math.Min(bw / cols, bh / rows);
        double w = cols * k, h = rows * k;
        var box = new Rect(area.X + m.X * scale, area.Y + m.Y * scale, bw, bh);
        var map = new Rect(box.X + (bw - w) / 2, box.Y + (bh - h) / 2, w, h);
        ctx.DrawRectangle(MiniBack, MiniFrame, box.Inflate(3 * scale), 4 * scale, 4 * scale);
        using (ctx.PushRenderOptions(Crisp)) ctx.DrawImage(_miniBmp, new Rect(0, 0, cols, rows), map);
        double dot = Math.Max(2.5, 3.2 * scale);
        foreach (var (fx, fy, color) in _world.MinimapDots())
        {
            if (fx < 0 || fy < 0 || fx > 1 || fy > 1) continue;
            _barBrush.Color = ColorOf(color, Colors.White);
            var c = new Point(map.X + fx * w, map.Y + fy * h);
            ctx.DrawRectangle(_barBrush, DotPen, new Rect(c.X - dot / 2, c.Y - dot / 2, dot, dot));
        }
    }
}
