using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// The code colours (Visual Studio Dark+), in one place so they can be themed later. Which piece gets which class is
/// decided by <see cref="SyntaxColors"/> (driven by the language's own lexer); this only maps a class to a brush.
/// </summary>
public static class CodeColors
{
    public static readonly IBrush Text = Solid("#D4D4D4");         // plain text, operators and punctuation
    public static readonly IBrush Control = Solid("#C586C0");      // when if else repeat while for each together run wait …
    public static readonly IBrush Declaration = Solid("#569CD6");  // mechanism system param uses includes to from …
    public static readonly IBrush Constant = Solid("#569CD6");     // true false nothing none self other
    public static readonly IBrush String = Solid("#CE9178");
    public static readonly IBrush Number = Solid("#B5CEA8");
    public static readonly IBrush Comment = Solid("#6A9955");      // drawn in italics
    public static readonly IBrush Function = Solid("#DCDCAA");     // commands, calls, the mechanism's own 'to' commands
    public static readonly IBrush Type = Solid("#4EC9B0");         // mechanism names, param types
    public static readonly IBrush Variable = Solid("#9CDCFE");     // variables, params, members
    public static readonly IBrush Operator = Solid("#D4D4D4");

    /// <summary>The selected text: a subtle grey-blue like VS (the caret stays the app's orange accent).</summary>
    public static readonly IBrush Selection = Solid("#3A3D41");
    /// <summary>The line the caret is on.</summary>
    public static readonly IBrush CurrentLine = Solid("#161616");
    /// <summary>The line opened from the debugger / an error (a faded orange, like the gutter arrow).</summary>
    public static readonly IBrush MarkedLine = new SolidColorBrush(Color.FromArgb(48, 217, 119, 87)).ToImmutable();

    public static IBrush For(CodeClass c) => c switch
    {
        CodeClass.Control => Control,
        CodeClass.Declaration => Declaration,
        CodeClass.Constant => Constant,
        CodeClass.String => String,
        CodeClass.Number => Number,
        CodeClass.Comment => Comment,
        CodeClass.Function => Function,
        CodeClass.Type => Type,
        CodeClass.Variable => Variable,
        CodeClass.Operator => Operator,
        _ => Text,
    };

    /// <summary>Mechanism names the previews colour as types (the editor sets it to the project's mechanisms).</summary>
    public static Func<IEnumerable<string>>? KnownMechanisms;

    /// <summary>
    /// Read-only coloured code (the AI panel's previews and answers, the plugins' usage lines): one text block of
    /// coloured runs, selectable so it can still be copied.
    /// </summary>
    public static SelectableTextBlock Preview(string code, double fontSize = 11.5)
    {
        var tb = new SelectableTextBlock { FontFamily = Palette.Mono, FontSize = fontSize, Foreground = Text };
        AddRuns(tb.Inlines!, code, SyntaxContext.For(code, KnownMechanisms?.Invoke()));
        return tb;
    }

    /// <summary>Adds the coloured runs of <paramref name="code"/> (several lines are joined with line breaks).</summary>
    public static void AddRuns(InlineCollection inlines, string code, SyntaxContext? ctx = null, IBrush? dim = null)
    {
        ctx ??= SyntaxContext.For(code, KnownMechanisms?.Invoke());
        bool first = true;
        foreach (var (_, line, spans) in SyntaxColors.ClassifyLines(code, ctx))
        {
            if (!first) inlines.Add(new LineBreak());
            first = false;
            int at = 0;
            foreach (var s in spans)
            {
                if (s.Start > at) inlines.Add(new Run(line[at..s.Start]));
                var run = new Run(line.Substring(s.Start, s.Length)) { Foreground = dim ?? For(s.Class) };
                if (s.Class == CodeClass.Comment) run.FontStyle = FontStyle.Italic;
                inlines.Add(run);
                at = s.Start + s.Length;
            }
            if (at < line.Length) inlines.Add(new Run(line[at..]));
        }
    }

    static IBrush Solid(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}

/// <summary>
/// Colours the code editor (AvaloniaEdit) line by line with <see cref="SyntaxColors"/>. AvaloniaEdit only asks for the
/// lines on screen and re-asks for a line when it changes, so a 1,000-line mechanism stays as fast as a short one.
/// The few things a line can't know (the mechanism's own 'to' commands, the project's mechanism names) live in
/// <see cref="Context"/>; when they change every visible line is recoloured.
/// </summary>
sealed class CodeColorizer : DocumentColorizingTransformer
{
    readonly List<CodeSpan> _spans = new();
    Typeface? _italic;

    public SyntaxContext Context { get; private set; } = SyntaxContext.Empty;

    /// <summary>Re-reads the text's commands and the mechanism names; true when that changed the colours (redraw).</summary>
    public bool Update(string text, IEnumerable<string>? mechanisms)
    {
        var next = SyntaxContext.For(text, mechanisms);
        if (next.SameAs(Context)) return false;
        Context = next;
        return true;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) return;
        string text = CurrentContext.Document.GetText(line);
        SyntaxColors.Classify(text, Context, _spans);
        foreach (var s in _spans)
        {
            var brush = CodeColors.For(s.Class);
            bool italic = s.Class == CodeClass.Comment;
            ChangeLinePart(line.Offset + s.Start, line.Offset + s.Start + s.Length, el =>
            {
                el.TextRunProperties.SetForegroundBrush(brush);
                if (italic)
                {
                    var tf = el.TextRunProperties.Typeface;
                    _italic ??= new Typeface(tf.FontFamily, FontStyle.Italic, tf.Weight, tf.Stretch);
                    el.TextRunProperties.SetTypeface(_italic.Value);
                }
            });
        }
    }
}
