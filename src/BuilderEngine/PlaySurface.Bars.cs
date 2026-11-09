using Avalonia;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// Cinema bars ('bars on' / 'bars off', World.View.cs): two black bands that slide in from the top and the bottom of the
// view, over the game and the HUD, under fades and the dialogue panel.
sealed partial class PlaySurface
{
    void DrawBars(DrawingContext ctx, Rect area)
    {
        double k = _world.BarsAmount;
        if (k <= 0.001) return;
        double eased = World.Ease("inout", k);
        double h = area.Height * World.BarsSize * eased;
        ctx.FillRectangle(Brushes.Black, new Rect(area.X, area.Y, area.Width, h));
        ctx.FillRectangle(Brushes.Black, new Rect(area.X, area.Bottom - h, area.Width, h));
    }
}
