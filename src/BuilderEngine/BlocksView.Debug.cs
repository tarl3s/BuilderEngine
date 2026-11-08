using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// DEBUGGER in the blocks: a red dot on blocks with a breakpoint, the ⋯ menu's "Pause here", and the block of a line
// opened from the Debug panel (a running 'when', an error, the breakpoint the game stopped at) outlined in the accent.
sealed partial class BlocksView
{
    /// <summary>Is there a breakpoint on this line of the open mechanism? (set by the window)</summary>
    public Func<int, bool>? BreakpointAt { get; set; }
    /// <summary>The ⋯ menu's "Pause here" / "Don't pause here" on a block's line.</summary>
    public Action<int>? ToggleBreakpoint { get; set; }

    int _debugLine;
    string? _debugMech;
    Stmt? _debugStmt;
    /// <summary>The statement whose head row is being built (so that row gets the ⋯ menu).</summary>
    Stmt? _rowStmt;

    static readonly IBrush BreakDot = new SolidColorBrush(Color.Parse("#E05D5D")).ToImmutable();

    /// <summary>Outlines the block on this line of that mechanism (0 = none) and scrolls to it.</summary>
    public void HighlightLine(int line, string? mechanism = null)
    {
        if (line == _debugLine && line == 0) return;
        _debugLine = line;
        _debugMech = mechanism;
        _reveal = line > 0 ? FindStmtAt(line) : null;
        Rebuild();
    }

    public int HighlightedLine => _debugLine;

    /// <summary>Breakpoints changed: redraw the dots.</summary>
    public void RefreshDebugMarks() => Rebuild();

    /// <summary>The outermost statement on a line, in any mechanism shown.</summary>
    Stmt? FindStmtAt(int line)
    {
        foreach (var m in _defs)
        {
            if (_debugMech != null && !string.Equals(m.Name, _debugMech, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var w in m.Whens) if (Find(w.Body) is { } s) return s;
            foreach (var p in m.Procs) if (Find(p.Body) is { } s) return s;
        }
        return null;

        Stmt? Find(List<Stmt> body)
        {
            foreach (var s in body)
            {
                if (LineOf(s) == line) return s;
                foreach (var inner in BlockEdits.Bodies(s))
                    if (Find(inner) is { } found) return found;
            }
            return null;
        }
    }

    /// <summary>A statement's block with its breakpoint dot and, when it is the opened line, an accent outline.</summary>
    Control DebugDecorate(Stmt s, Control block)
    {
        bool highlighted = ReferenceEquals(s, _debugStmt);
        bool breakpoint = BreakpointAt?.Invoke(LineOf(s)) == true;
        if (!highlighted && !breakpoint) return block;
        var margin = block.Margin;
        block.Margin = default;
        Control result = block;
        if (highlighted)
            result = new Border
            {
                Child = block, BorderBrush = Palette.Accent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(1), Background = Palette.AccentSoft
            };
        if (breakpoint)
        {
            var grid = new Grid();
            grid.Children.Add(result);
            var dot = new Ellipse
            {
                Width = 10, Height = 10, Fill = BreakDot, Stroke = Palette.Bg0, StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-5, 7, 0, 0), IsHitTestVisible = false
            };
            ToolTip.SetTip(grid, BlockText($"The game pauses here (line {LineOf(s)}) while playing"));
            grid.Children.Add(dot);
            result = grid;
        }
        result.Margin = margin;
        return result;
    }

    /// <summary>
    /// Dev check (--snap): the line each block says it is on holds that block's first word (a one-line 'run x' or
    /// 'if c: x' holds two blocks).
    /// </summary>
    internal bool DebugLinesMatchCode()
    {
        var code = _shown.Replace("\r\n", "\n").Split('\n');
        foreach (var m in _defs)
            for (int k = 0; BlockEdits.TopBody(m, k) is { } top; k++)
                if (!Match(top)) return false;
        return true;

        bool Match(List<Stmt> body)
        {
            foreach (var s in body)
            {
                int line = LineOf(s);
                string head = Printer.Print(s, 0).Split('\n').First(l => !l.TrimStart().StartsWith('#')).Trim();
                if (line <= 0 || line > code.Length || !code[line - 1].Contains(head.Split(' ')[0], StringComparison.Ordinal)) return false;
                if (BlockEdits.Bodies(s).Any(b => !Match(b))) return false;
            }
            return true;
        }
    }

    /// <summary>The ⋯ tool of a statement's row: a menu with "Pause here" (a breakpoint on its line).</summary>
    void AddMoreTool(StackPanel tools, BlockLook look, Stmt s)
    {
        if (ToggleBreakpoint == null || LineOf(s) <= 0) return;
        var b = new Button
        {
            Content = "⋯", FontSize = 12, Padding = new Thickness(6, 0), Height = 22, MinHeight = 0,
            Background = Brushes.Transparent, Foreground = look.Fg, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4), VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(b, BlockText("More"));
        var menu = new MenuFlyout();
        var pause = new MenuItem();
        // The line is read when the menu opens: a block edit since this row was drawn may have moved it.
        void Label()
        {
            int line = LineOf(s);
            bool on = BreakpointAt?.Invoke(line) == true;
            pause.Header = BlockText(on ? $"Don't pause here (line {line})" : $"Pause here (line {line})");
        }
        Label();
        menu.Opening += (_, _) => Label();
        pause.Click += (_, _) => ToggleBreakpoint(LineOf(s));
        menu.Items.Add(pause);
        b.Flyout = menu;
        tools.Children.Add(b);
    }
}
