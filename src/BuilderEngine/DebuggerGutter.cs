using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace BuilderEngine;

/// <summary>
/// The line numbers left of the code view (a margin of the code editor). Click a number to make the game pause there
/// (a breakpoint, a red dot); the line opened from the Debug panel gets an accent arrow, and that line is tinted in
/// the code too. It reads the editor's own visual lines, so the numbers stay next to their lines while scrolling.
/// </summary>
sealed class DebuggerGutter : AbstractMargin
{
    readonly CodeEditor _code;
    readonly MarkedLineRenderer _marked;

    /// <summary>Is there a breakpoint on this line (1 = first)?</summary>
    public Func<int, bool>? HasBreakpoint;
    public Action<int>? LineClicked;
    /// <summary>The highlighted line (the one the game stopped at, or a running 'when' opened from the panel); 0 = none.</summary>
    public Func<int>? CurrentLine;

    const double GutterWidth = 48;
    static readonly Typeface Font = new(Palette.Mono);
    static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#E05D5D")).ToImmutable();
    static readonly IBrush CurrentBg = new SolidColorBrush(Color.FromArgb(60, 217, 119, 87)).ToImmutable();
    static readonly IBrush NumberDim = new SolidColorBrush(Color.Parse("#6E7681")).ToImmutable();   // VS's line numbers
    static readonly IBrush NumberCaret = new SolidColorBrush(Color.Parse("#C6C6C6")).ToImmutable(); // the caret's line
    readonly Dictionary<(int, bool), FormattedText> _numbers = new();

    public DebuggerGutter(CodeEditor code)
    {
        _code = code;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Click a line number: the game pauses there while playing (click again to remove)");
        _marked = new MarkedLineRenderer(this);
        code.TextArea.LeftMargins.Insert(0, this);
        code.TextArea.TextView.BackgroundRenderers.Add(_marked);
        code.TextArea.Caret.PositionChanged += (_, _) => InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(GutterWidth, 0);

    protected override void OnTextViewVisualLinesChanged() => InvalidateVisual();

    /// <summary>Repaints the numbers, the dots, the arrow and the tinted line.</summary>
    public void Refresh()
    {
        InvalidateVisual();
        TextView?.InvalidateLayer(_marked.Layer);
    }

    /// <summary>Each visible line: its number and its top and height in this control's pixels.</summary>
    IEnumerable<(int Line, double Top, double Height)> Lines()
    {
        var tv = TextView;
        if (tv == null || !tv.VisualLinesValid) yield break;
        foreach (var vl in tv.VisualLines)
            yield return (vl.FirstDocumentLine.LineNumber, vl.VisualTop - tv.VerticalOffset, vl.Height);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Palette.Bg0, new Rect(Bounds.Size));
        int current = CurrentLine?.Invoke() ?? 0;
        int caretLine = _code.TextArea.Caret.Line;
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
            bool atCaret = line == caretLine;
            if (!_numbers.TryGetValue((line, atCaret), out var ft))
                _numbers[(line, atCaret)] = ft = new FormattedText(line.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 11.5, atCaret ? NumberCaret : NumberDim);
            ctx.DrawText(ft, new Point(Bounds.Width - 12 - ft.Width, top + (h - ft.Height) / 2));
        }
        ctx.FillRectangle(Palette.Border, new Rect(Bounds.Width - 1, 0, 1, Bounds.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (LineAt(e.GetPosition(this).Y) is int line and > 0)
        {
            LineClicked?.Invoke(line);
            Refresh();
        }
        e.Handled = true;
    }

    /// <summary>The line drawn at this height of the gutter (0 = none): what a click there toggles.</summary>
    internal int LineAt(double y)
    {
        foreach (var (line, top, h) in Lines())
            if (y >= top && y < top + h) return line;
        return 0;
    }

    /// <summary>Where a line's number is drawn (its middle, in gutter pixels), or null when it is off screen (snap checks).</summary>
    internal double? MiddleOf(int line)
    {
        foreach (var (n, top, h) in Lines())
            if (n == line) return top + h / 2;
        return null;
    }

    /// <summary>Scrolls the code so this line is in view (about a third from the top).</summary>
    public void ScrollToLine(int line)
    {
        if (line < 1 || line > _code.Document.LineCount) return;
        // (TextEditor.ScrollToVerticalOffset does nothing in AvaloniaEdit 11; ScrollTo places the line itself.)
        _code.ScrollTo(line, 1, VisualYPosition.LineTop, _code.ViewportHeight / 3, 0);
        Refresh();
    }

    /// <summary>Tints the marked line (<see cref="CurrentLine"/>) behind the code, across the whole width.</summary>
    sealed class MarkedLineRenderer(DebuggerGutter gutter) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext ctx)
        {
            int line = gutter.CurrentLine?.Invoke() ?? 0;
            if (line < 1 || !textView.VisualLinesValid) return;
            foreach (var vl in textView.VisualLines)
                if (vl.FirstDocumentLine.LineNumber == line)
                {
                    ctx.FillRectangle(CodeColors.MarkedLine, new Rect(0, vl.VisualTop - textView.VerticalOffset, textView.Bounds.Width, vl.Height));
                    break;
                }
        }
    }
}
