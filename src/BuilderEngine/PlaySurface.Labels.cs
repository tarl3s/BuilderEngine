using System.Globalization;
using Avalonia;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// LABELS drawing: texts and badges on objects ('label "cost" "3" at top left with "#FFFFFF", 14, "#2F6FD0"') and
// frames around them ('outline "#FFD54A", 3'). Labels scale with the object's width (a zoomed card keeps its layout).
sealed partial class PlaySurface
{
    static readonly Typeface BadgeFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    readonly Dictionary<ObjectLabel, (string Text, double Size, string Color, double Width, FormattedText Ft)> _labelFts = new();
    readonly Dictionary<(string, double), IPen> _labelPens = new();
    static readonly IPen BadgeRim = new Pen(new SolidColorBrush(Color.FromArgb(200, 12, 8, 6)), 1).ToImmutable();

    IPen LabelPen(string color, double width)
    {
        var key = (color, Math.Round(width, 2));
        if (_labelPens.TryGetValue(key, out var p)) return p;
        if (_labelPens.Count > 200) _labelPens.Clear();
        return _labelPens[key] = new Pen(GameDraw.BrushOf(color, Palette.Accent), width).ToImmutable();
    }

    void DrawLabels(DrawingContext ctx, GameObject o, Rect r)
    {
        if (o.Outline != null)
        {
            double w = o.OutlineWidth;
            double rad = Math.Min(8, Math.Min(r.Width, r.Height) / 4) + w / 2;
            ctx.DrawRectangle(null, LabelPen(o.Outline, w), r.Inflate(w / 2 + 1), rad, rad);
        }
        if (o.Labels is not { Count: > 0 } labels) return;
        for (int i = 0; i < labels.Count; i++)
        {
            var l = labels[i];
            string text = l.TextNow();
            if (text.Length == 0) continue;
            double k = r.Width / Math.Max(1, l.BaseWidth);
            if (k <= 0.01) continue;
            double size = Math.Max(1, l.Size * k), pad = 3 * k;
            bool badge = l.Back != null;
            double maxW = badge ? 0 : Math.Max(8, r.Width - pad * 2);
            if (!_labelFts.TryGetValue(l, out var c) || c.Text != text || c.Size != size || c.Color != l.Color || c.Width != maxW)
            {
                var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, badge ? BadgeFont : HudFont, size,
                    GameDraw.BrushOf(l.Color, Palette.Text));
                if (!badge)
                {
                    ft.MaxTextWidth = maxW;
                    ft.TextAlignment = l.AnchorLeft ? TextAlignment.Left : l.AnchorRight ? TextAlignment.Right : TextAlignment.Center;
                }
                if (_labelFts.Count > 4000) _labelFts.Clear();
                _labelFts[l] = c = (text, size, l.Color, maxW, ft);
            }
            var f = c.Ft;
            double bw = badge ? Math.Max(f.Width + size * 0.7, f.Height * 1.05) : maxW, bh = badge ? f.Height * 1.05 : f.Height;
            double x = l.AnchorLeft ? r.X + pad : l.AnchorRight ? r.Right - pad - bw : r.X + (r.Width - bw) / 2;
            double y = l.AnchorTop ? r.Y + pad : l.AnchorBottom ? r.Bottom - pad - bh : r.Y + (r.Height - bh) / 2;
            x += l.Dx * k;
            y += l.Dy * k;
            if (badge)
            {
                var box = new Rect(x, y, bw, bh);
                ctx.DrawRectangle(GameDraw.BrushOf(l.Back!, Palette.Accent), BadgeRim, box, bh / 2, bh / 2);
                ctx.DrawText(f, new Point(x + (bw - f.Width) / 2, y + (bh - f.Height) / 2));
            }
            else ctx.DrawText(f, new Point(x, y));
        }
    }
}
