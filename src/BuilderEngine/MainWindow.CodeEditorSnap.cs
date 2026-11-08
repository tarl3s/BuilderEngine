using System.Diagnostics;
using Avalonia.Input;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// CODE EDITOR, dev tool (`--snap`): the coloured code view (AvaloniaEdit) checked on the real window — the colours,
// auto-indent, Tab, its own text undo next to the blocks' undo, live error check, blocks ⇄ code, the gutter, a long file.
public sealed partial class MainWindow
{
    async Task SnapCodeEditor(string dir, Action<string, bool> check)
    {
        const string src = "mechanism ColorTest\n    uses Health max 2\n    param speed: number = 100  # how fast\n\n    when start\n        say \"Hi # there\"\n        if speed > 0: stop\n";
        var sb = new System.Text.StringBuilder("mechanism LongOne\n    param step: number = 1\n\n    when start\n        x = 0\n");
        for (int i = 0; i < 1200; i++) sb.Append($"        x = x + step  # step {i}\n");
        string longSrc = sb.ToString();
        string? previous = _current;
        string mode = _mode;
        _sources["ColorTest"] = src;
        _sources["LongOne"] = longSrc;
        RefreshLibrary();
        try
        {
            SetMode("both");
            OpenMechanism("ColorTest");
            await Task.Delay(600);
            Snap(dir, "7b_code_colors");

            // ---- Colours: every piece gets its VS Dark+ colour ----
            var tv = _code.TextArea.TextView;
            (IBrush? Brush, FontStyle Style) At(int line, string piece)
            {
                var dl = _code.Document.GetLineByNumber(line);
                int col = _code.Document.GetText(dl).IndexOf(piece, StringComparison.Ordinal);
                if (col < 0) return (null, FontStyle.Normal);
                foreach (var el in tv.GetOrConstructVisualLine(dl).Elements)
                    if (el.RelativeTextOffset <= col && col < el.RelativeTextOffset + el.DocumentLength)
                        return (el.TextRunProperties.ForegroundBrush, el.TextRunProperties.Typeface.Style);
                return (null, FontStyle.Normal);
            }
            bool Is(int line, string piece, IBrush brush) => ReferenceEquals(At(line, piece).Brush, brush);
            check("code colours: 'mechanism' and 'uses' blue, the names teal", Is(1, "mechanism", CodeColors.Declaration) && Is(1, "ColorTest", CodeColors.Type) && Is(2, "uses", CodeColors.Declaration) && Is(2, "Health", CodeColors.Type));
            check("code colours: a param's name light blue, its type teal, its number green", Is(3, "speed", CodeColors.Variable) && Is(3, "number", CodeColors.Type) && Is(3, "100", CodeColors.Number));
            check("code colours: a comment green and in italics", Is(3, "# how fast", CodeColors.Comment) && At(3, "# how fast").Style == FontStyle.Italic);
            check("code colours: 'when' / 'if' / 'stop' purple, a command yellow", Is(5, "when", CodeColors.Control) && Is(7, "if", CodeColors.Control) && Is(7, "stop", CodeColors.Control) && Is(6, "say", CodeColors.Function));
            check("code colours: a text is orange, even with a # inside", Is(6, "\"Hi", CodeColors.String) && Is(6, "# there", CodeColors.String));
            check("the code font is the monospace one, the caret orange", _code.FontFamily == Palette.Mono && ReferenceEquals(_code.TextArea.CaretBrush, Palette.Accent));

            // ---- Typing: Enter keeps the indentation and adds a level after a block head; Tab = 4 spaces ----
            _code.TextArea.Focus();
            await Task.Delay(100);
            bool focused = FocusManager?.GetFocusedElement() is AvaloniaEdit.Editing.TextArea;
            check("the code editor takes the keyboard", focused);
            void Key(Key key, KeyModifiers mods = KeyModifiers.None) =>
                _code.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = _code.TextArea });
            var head = _code.Document.GetLineByNumber(5);
            _code.Select(head.EndOffset, 0);
            Key(Avalonia.Input.Key.Enter);
            await Task.Delay(100);
            check("Enter after 'when start' indents the new line one level more", _code.Document.GetText(_code.Document.GetLineByNumber(6)) == "        " && _code.CaretOffset == _code.Document.GetLineByNumber(6).EndOffset);
            check("typing reaches the mechanism (live check)", _sources["ColorTest"] == _code.Text && _status.Text?.StartsWith("No problems") == true);
            Key(Avalonia.Input.Key.Tab);
            await Task.Delay(100);
            check("Tab types 4 spaces", _code.Document.GetText(_code.Document.GetLineByNumber(6)) == "            ");

            // ---- Ctrl+Z / Ctrl+Y inside the code: the code's own text undo (not the blocks') ----
            Key(Avalonia.Input.Key.Z, KeyModifiers.Control);
            await Task.Delay(150);
            check("Ctrl+Z in the code undoes the typing (its own text undo)", _code.Document.GetText(_code.Document.GetLineByNumber(6)) == "        " && _code.CanRedo);
            Key(Avalonia.Input.Key.Y, KeyModifiers.Control);
            await Task.Delay(150);
            check("Ctrl+Y in the code redoes it", _code.Document.GetText(_code.Document.GetLineByNumber(6)) == "            ");
            while (_code.CanUndo) _code.Undo();
            await Task.Delay(300);
            check("undoing everything gives the opened code back", _code.Text == src && _sources["ColorTest"] == src);

            // ---- A text missing its quote: the error in the status bar, the rest of the line orange ----
            _code.Document.Insert(_code.Document.TextLength, "        print \"oops");
            await Task.Delay(300);
            check("an error shows in the status bar with its line", _status.Foreground == Palette.Error && _status.Text?.Contains("Line 8") == true);
            check("while typing a text, the rest of the line is orange", Is(8, "\"oops", CodeColors.String) && Is(8, "print", CodeColors.Function));
            _code.Undo();
            await Task.Delay(400);
            check("undo fixes it again", _sources["ColorTest"] == src && _status.Foreground != Palette.Error);

            // ---- Blocks → code: the caret and the text undo stay ----
            var caretLine = _code.Document.GetLineByNumber(6);
            _code.Select(caretLine.Offset + 8, 0);
            bool edited = _blocksView!.DebugPointer("stop", drag: false);
            await Task.Delay(400);
            check("a block edit changes the code", edited && _code.Text == _sources["ColorTest"] && _code.Text != src);
            string caretText = _code.Document.GetText(_code.Document.GetLineByNumber(_code.TextArea.Caret.Line));
            check($"… and the caret stays on its line (line {_code.TextArea.Caret.Line}: '{caretText.Trim()}')", caretText.Contains("say \"Hi # there\""));
            _blocksView.Undo();
            await Task.Delay(400);
            check("the blocks' undo puts the code back", _code.Text == src);

            // ---- Selected text (the AI's Explain), the line opened from an error, the gutter ----
            var sayLine = _code.Document.GetLineByNumber(6);
            _code.Select(sayLine.Offset + 8, "say \"Hi # there\"".Length);
            check("the selected code is what Explain reads", _code.SelectedText == "say \"Hi # there\"");
            check("copy / cut / paste are there (keys and right-click menu)", _code.CanCopy && _code.CanCut && _code.ContextMenu?.Items.Count >= 7);
            OpenMechanismAt("ColorTest", 7);
            await Task.Delay(400);
            check("opening a line (an error, the debugger) selects it", _code.SelectedText == "        if speed > 0: stop");
            check("a click on a line number hits that line", _gutter?.MiddleOf(7) is double y7 && _gutter.LineAt(y7) == 7 && _gutter.MiddleOf(2) is double y2 && _gutter.LineAt(y2) == 2);
            Snap(dir, "7c_code_line_marked");

            // ---- A 1,200-line mechanism stays responsive ----
            var watch = Stopwatch.StartNew();
            OpenMechanism("LongOne");
            long openMs = watch.ElapsedMilliseconds;
            await Task.Delay(500);
            watch.Restart();
            _gutter?.ScrollToLine(1100);
            tv.EnsureVisualLines();
            long scrollMs = watch.ElapsedMilliseconds;
            await Task.Delay(200);
            // The editor alone (text change, colours, the visible lines redrawn), then a key with the live check.
            _loading = true;
            watch.Restart();
            _code.Select(_code.Document.GetLineByNumber(1100).EndOffset, 0);
            _code.InsertAtCaret(" ");
            tv.EnsureVisualLines();
            long editorMs = watch.ElapsedMilliseconds;
            _code.Undo();
            _loading = false;
            await Task.Delay(100);
            watch.Restart();
            _code.Select(_code.Document.GetLineByNumber(1100).EndOffset, 0);
            _code.InsertAtCaret(" ");
            tv.EnsureVisualLines();
            long typeMs = watch.ElapsedMilliseconds;
            await Task.Delay(300);
            Snap(dir, "7d_code_long");
            check($"a 1,200-line mechanism: opens in {openMs} ms, scrolls in {scrollMs} ms, a key costs the editor {editorMs} ms ({typeMs} ms with the live check)",
                openMs < 3000 && scrollMs < 300 && editorMs < 100 && typeMs < 1500);
            check($"… only the lines on screen are drawn ({tv.VisualLines.Count}) and line 1100 is in view (offset {_code.VerticalOffset:0}, extent {_code.ExtentHeight:0})",
                tv.VisualLines.Count < 120 && _gutter?.MiddleOf(1100) != null);
            check("the colours follow the scroll", Is(1100, "# step", CodeColors.Comment) && Is(1100, "step", CodeColors.Variable));
            _code.Undo();

            // ---- Ctrl+F: the find bar ----
            _code.TextArea.Focus();
            await Task.Delay(100);
            Key(Avalonia.Input.Key.F, KeyModifiers.Control);
            await Task.Delay(200);
            bool opened = _code.SearchPanel?.IsOpened == true;
            if (opened) _code.SearchPanel!.SearchPattern = "step 1150";
            await Task.Delay(300);
            Snap(dir, "7e_code_find");
            check("Ctrl+F in the code opens the find bar", opened);
            _code.SearchPanel?.Close();

            // ---- Previews elsewhere use the same colours ----
            var preview = CodeColors.Preview("mechanism X\n    when start\n        print 1  # hi\n");
            check("the AI panel's code previews are coloured too", preview.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Any(r => ReferenceEquals(r.Foreground, CodeColors.Function) && r.Text == "print"));
        }
        catch (Exception e) { check("code editor: no exception: " + e.Message, false); }
        finally
        {
            _sources.Remove("ColorTest");
            _sources.Remove("LongOne");
            RefreshLibrary();
            if (previous != null && _sources.ContainsKey(previous)) OpenMechanism(previous);
            SetMode(mode);
            await Task.Delay(200);
        }
    }
}
