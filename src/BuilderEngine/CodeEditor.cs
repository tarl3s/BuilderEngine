using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;

namespace BuilderEngine;

/// <summary>
/// The Mechanism tab's code view: AvaloniaEdit (the standard Avalonia code editor) with Builder Script colours
/// (<see cref="CodeColorizer"/>), its own text undo, and the debugger gutter as a left margin. A plain TextBox can't
/// colour its text; AvaloniaEdit draws only the lines on screen, so very long mechanisms stay responsive.
/// Ctrl+F opens AvaloniaEdit's find bar (F3 / Shift+F3 next / previous).
/// </summary>
sealed class CodeEditor : TextEditor
{
    protected override Type StyleKeyOverride => typeof(TextEditor); // use AvaloniaEdit's own template

    public CodeColorizer Colors { get; } = new();

    public CodeEditor()
    {
        FontFamily = Palette.Mono;
        FontSize = 14;
        Background = Palette.Bg0;
        Foreground = CodeColors.Text;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0, 10, 0, 0);
        WordWrap = false;
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

        Options.EnableHyperlinks = false;        // a URL in a text is just text
        Options.EnableEmailHyperlinks = false;
        Options.CutCopyWholeLine = false;        // Ctrl+C with nothing selected copies nothing, as before
        Options.HighlightCurrentLine = true;
        Options.IndentationSize = 4;
        Options.ConvertTabsToSpaces = true;

        var area = TextArea;
        area.CaretBrush = Palette.Accent;        // the app's orange caret
        area.SelectionBrush = CodeColors.Selection;
        area.SelectionForeground = null;         // selected code keeps its colours
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 0;
        area.TextView.Margin = new Thickness(14, 0, 0, 0); // room between the line numbers and the code
        area.TextView.CurrentLineBackground = CodeColors.CurrentLine;
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        area.TextView.LineTransformers.Add(Colors);

        // The right-click menu a text box has.
        var menu = new ContextMenu();
        void Item(string header, string keys, Action act, Func<bool> can)
        {
            var mi = new MenuItem { Header = header, InputGesture = Avalonia.Input.KeyGesture.Parse(keys) };
            mi.Click += (_, _) => act();
            menu.Opening += (_, _) => mi.IsEnabled = can();
            menu.Items.Add(mi);
        }
        Item("Cut", "Ctrl+X", Cut, () => CanCut);
        Item("Copy", "Ctrl+C", Copy, () => CanCopy);
        Item("Paste", "Ctrl+V", Paste, () => CanPaste);
        menu.Items.Add(new Separator());
        Item("Undo", "Ctrl+Z", () => Undo(), () => CanUndo);
        Item("Redo", "Ctrl+Y", () => Redo(), () => CanRedo);
        menu.Items.Add(new Separator());
        Item("Select all", "Ctrl+A", SelectAll, () => Document.TextLength > 0);
        ContextMenu = menu;
    }

    protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // AvaloniaEdit sets its own current-line colours when the template loads: put the subtle ones back (no border).
        TextArea.TextView.CurrentLineBackground = CodeColors.CurrentLine;
        TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
    }

    // ---- Editing helpers (auto-indent, the debugger's marked line, the blocks) ----

    /// <summary>
    /// Replaces the text with the smallest edit (only the part that differs), so the scroll position and the text undo
    /// stay (used when the blocks or a file reload change the code under the person's eyes). The caret stays on its
    /// line: when that line was inside the changed part (the blocks reprint a mechanism, moving a 'param' above a
    /// 'uses'…), it goes to the nearest line with the same text, else to the same line number.
    /// </summary>
    public void SetTextKeepView(string text)
    {
        string old = Document.Text;
        if (old == text) return;
        int pre = 0, max = Math.Min(old.Length, text.Length);
        while (pre < max && old[pre] == text[pre]) pre++;
        int suf = 0;
        while (suf < max - pre && old[old.Length - 1 - suf] == text[text.Length - 1 - suf]) suf++;

        int caret = CaretOffset;
        bool inside = caret > pre && caret < old.Length - suf; // the anchors can't keep it: put it back by its line
        string caretLine = "";
        int lineNo = 1, column = 0;
        if (inside)
        {
            var dl = Document.GetLineByOffset(caret);
            caretLine = Document.GetText(dl);
            lineNo = dl.LineNumber;
            column = caret - dl.Offset;
        }
        Document.Replace(pre, old.Length - pre - suf, text.Substring(pre, text.Length - pre - suf));
        if (!inside) return;
        int count = Document.LineCount, best = Math.Min(lineNo, count);
        for (int d = 0; d < count; d++)
        {
            if (lineNo - d >= 1 && lineNo - d <= count && Document.GetText(Document.GetLineByNumber(lineNo - d)) == caretLine) { best = lineNo - d; break; }
            if (lineNo + d <= count && Document.GetText(Document.GetLineByNumber(lineNo + d)) == caretLine) { best = lineNo + d; break; }
            if (lineNo - d < 1 && lineNo + d > count) break;
        }
        var target = Document.GetLineByNumber(best);
        Select(target.Offset + Math.Min(column, target.Length), 0);
    }

    /// <summary>Types <paramref name="s"/> over the selection (or at the caret), as one undo step.</summary>
    public void InsertAtCaret(string s)
    {
        int start = SelectionStart, length = SelectionLength;
        Document.Replace(start, length, s);
        Select(start + s.Length, 0);
    }

    /// <summary>The text of the caret's line up to the caret.</summary>
    public string LineBeforeCaret()
    {
        var line = Document.GetLineByOffset(CaretOffset);
        return Document.GetText(line.Offset, CaretOffset - line.Offset);
    }

    /// <summary>Selects a whole line (1 = first), without its line break.</summary>
    public void SelectLine(int line)
    {
        if (line < 1 || line > Document.LineCount) return;
        var l = Document.GetLineByNumber(line);
        Select(l.Offset, l.Length);
    }

    /// <summary>
    /// The project's mechanism names changed (or the text did): recolour when that changes any colour. A caller that
    /// already has the editor's text passes it (<paramref name="text"/>), so it isn't built again from the document.
    /// </summary>
    public void RefreshColors(IEnumerable<string>? mechanisms, string? text = null)
    {
        if (Colors.Update(text ?? Document.Text, mechanisms)) TextArea.TextView.Redraw();
    }
}
