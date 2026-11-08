using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// Draws tile layers (in scene coordinates, inside the caller's transform) — only the cells inside the visible
/// rectangle, and runs of same-coloured cells as one rectangle, so a 400 × 200 map costs what the screen shows.
/// Used by the game (<see cref="PlaySurface"/>) and the scene editor.
/// </summary>
static class TileDraw
{
    static readonly IPen CrackPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 10, 10, 10)), 1.5).ToImmutable();
    static readonly IBrush CrackShade = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)).ToImmutable();

    /// <summary>Draws the layers whose <see cref="TileLayer.Front"/> equals <paramref name="front"/>.</summary>
    public static void Draw(DrawingContext ctx, IReadOnlyList<TileLayer> layers, IReadOnlyList<TileKind> kinds, Rect visible, ImageCache? images, bool front)
    {
        foreach (var l in layers)
            if (l.Front == front) DrawLayer(ctx, l, kinds, visible, images);
    }

    public static void DrawLayer(DrawingContext ctx, TileLayer l, IReadOnlyList<TileKind> kinds, Rect visible, ImageCache? images)
    {
        double cs = l.CellSize;
        int c0 = Math.Max(0, (int)Math.Floor((visible.X - l.X) / cs)), c1 = Math.Min(l.Columns - 1, (int)Math.Floor((visible.Right - l.X) / cs));
        int r0 = Math.Max(0, (int)Math.Floor((visible.Y - l.Y) / cs)), r1 = Math.Min(l.Rows - 1, (int)Math.Floor((visible.Bottom - l.Y) / cs));
        if (c0 > c1 || r0 > r1) return;
        var cells = l.Cells;
        for (int r = r0; r <= r1; r++)
        {
            int row = r * l.Columns;
            double y = l.Y + r * cs;
            int c = c0;
            while (c <= c1)
            {
                int id = cells[row + c];
                if (id == 0 || id > kinds.Count) { c++; continue; }
                var k = kinds[id - 1];
                if (k.Image.Length > 0 && images?.Get(k.Image) is { } bmp)
                {
                    ctx.DrawImage(bmp, new Rect(bmp.Size), new Rect(l.X + c * cs, y, cs, cs));
                    c++;
                    continue;
                }
                int start = c;
                while (c + 1 <= c1 && cells[row + c + 1] == id) c++;
                // A hair wider than the cells so neighbours never show a seam when scaled.
                ctx.FillRectangle(SceneCanvas.BrushOf(k.Color, Palette.Accent), new Rect(l.X + start * cs, y, (c - start + 1) * cs + 0.05, cs + 0.05));
                c++;
            }
        }
    }

    /// <summary>Cracks on the cell being dug: more of them the further it has gone.</summary>
    public static void DrawDig(DrawingContext ctx, TileMapState t)
    {
        if (t.DigLayer is not { } l || t.DigFraction <= 0 || t.DigFraction >= 1) return;
        double cs = l.CellSize, x = l.X + t.DigCol * cs, y = l.Y + t.DigRow * cs;
        ctx.FillRectangle(CrackShade, new Rect(x, y, cs, cs));
        int lines = 1 + (int)(t.DigFraction * 5);
        double cx = x + cs / 2, cy = y + cs / 2;
        for (int i = 0; i < lines; i++)
        {
            double a = i * 2.39996; // golden angle: spread around the middle
            double len = cs * (0.25 + 0.25 * t.DigFraction);
            ctx.DrawLine(CrackPen, new Point(cx, cy), new Point(cx + Math.Cos(a) * len, cy + Math.Sin(a) * len));
        }
    }
}
