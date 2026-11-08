using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace BuilderEngine;

/// <summary>
/// The line numbers left of the code view. Click a number to make the game pause there (a breakpoint, a red dot);
/// the line opened from the Debug panel gets an accent arrow. It reads the code box's own text layout, so the numbers
/// stay next to their lines while scrolling.
/// </summary>
sealed class DebuggerGutter : Control
{
    readonly TextBox _code;
    TextPresenter? _presenter;
    ScrollViewer? _scroll;

    /// <summary>Is there a breakpoint on this line (1 = first)?</summary>
    public Func<int, bool>? HasBreakpoint;
    public Action<int>? LineClicked;
    /// <summary>The highlighted line (the one the game stopped at, or a running 'when' opened from the panel); 0 = none.</summary>
    public Func<int>? CurrentLine;

    static readonly Typeface Font = new(Palette.Mono);
    static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#E05D5D")).ToImmutable();
    static readonly IBrush CurrentBg = new SolidColorBrush(Color.FromArgb(60, 217, 119, 87)).ToImmutable();
    readonly Dictionary<int, FormattedText> _numbers = new();

    public DebuggerGutter(TextBox code)
    {
        _code = code;
        Width = 48;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Click a line number: the game pauses there while playing (click again to remove)");
        code.TemplateApplied += (_, e) =>
        {
            _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
            _scroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_scroll != null) _scroll.ScrollChanged += (_, _) => InvalidateVisual();
        };
        code.TextChanged += (_, _) => InvalidateVisual();
        code.LayoutUpdated += (_, _) => InvalidateVisual();
    }

    TextPresenter? Presenter => _presenter ??= _code.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();

    /// <summary>Each visible line: its number and its top and height in this control's pixels.</summary>
    IEnumerable<(int Line, double Top, double Height)> Lines()
    {
        var p = Presenter;
        if (p == null || p.TextLayout is not { } layout) yield break;
        var origin = p.TranslatePoint(new Point(0, 0), this);
        if (origin == null) yield break;
        double y = origin.Value.Y;
        int n = 1;
        foreach (var line in layout.TextLines)
        {
            if (y > Bounds.Height) yield break;
            if (y + line.Height >= 0) yield return (n, y, line.Height);
            y += line.Height;
            n++;
        }
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Palette.Bg1, new Rect(Bounds.Size));
        int current = CurrentLine?.Invoke() ?? 0;
        foreach (var (line, top, h) in Lines())
        {
            if (line == current)
            {
                ctx.FillRectangle(CurrentBg, new Rect(0, top, Bounds.Width, h));
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    double cy = top + h / 2;
                    g.BeginFigure(new Point(Bounds.Width - 9, cy - 5), true);
                    g.LineTo(new Point(Bounds.Width - 3, cy));
                    g.LineTo(new Point(Bounds.Width - 9, cy + 5));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(Palette.Accent, null, tri);
            }
            if (HasBreakpoint?.Invoke(line) == true)
                ctx.DrawEllipse(Dot, null, new Point(9, top + h / 2), 5, 5);
            if (!_numbers.TryGetValue(line, out var ft))
                _numbers[line] = ft = new FormattedText(line.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 11.5, Palette.TextDim);
            ctx.DrawText(ft, new Point(Bounds.Width - 12 - ft.Width, top + (h - ft.Height) / 2));
        }
        ctx.FillRectangle(Palette.Border, new Rect(Bounds.Width - 1, 0, 1, Bounds.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        double y = e.GetPosition(this).Y;
        foreach (var (line, top, h) in Lines())
            if (y >= top && y < top + h)
            {
                LineClicked?.Invoke(line);
                InvalidateVisual();
                break;
            }
        e.Handled = true;
    }

    /// <summary>Scrolls the code so this line is in view (about a third from the top).</summary>
    public void ScrollToLine(int line)
    {
        var p = Presenter;
        _scroll ??= _code.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (p?.TextLayout is not { } layout || _scroll == null) return;
        double y = 0;
        int n = 1;
        foreach (var l in layout.TextLines)
        {
            if (n == line) break;
            y += l.Height;
            n++;
        }
        _scroll.Offset = new Vector(_scroll.Offset.X, Math.Max(0, y - _scroll.Viewport.Height / 3));
        InvalidateVisual();
    }
}
