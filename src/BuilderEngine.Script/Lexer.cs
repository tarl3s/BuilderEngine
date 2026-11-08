using System.Globalization;
using System.Text;

namespace BuilderEngine.Script;

public enum Tok { Ident, Number, String, Op, Newline, Indent, Dedent, Eof }

public readonly record struct Token(Tok Kind, string Text, int Line, double Number = 0)
{
    public override string ToString() => Kind switch
    {
        Tok.Newline => "end of line",
        Tok.Indent => "indentation",
        Tok.Dedent => "end of block",
        Tok.Eof => "end of file",
        Tok.String => $"\"{Text}\"",
        _ => $"'{Text}'"
    };
}

/// <summary>A friendly error with the line it happened on.</summary>
public sealed class ScriptError(string message, int line) : Exception(message)
{
    public int Line { get; } = line;
    public override string ToString() => Line > 0 ? $"Line {Line}: {Message}" : Message;
}

/// <summary>Turns source text into tokens; indentation becomes Indent/Dedent tokens (like Python).</summary>
public static class Lexer
{
    static readonly string[] Ops = ["+=", "-=", "*=", "/=", "==", "!=", "<>", "<=", ">=", "+", "-", "*", "/", "%", "=", "<", ">", "(", ")", "[", "]", "{", "}", ",", ".", ":"];

    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var indents = new Stack<int>();
        indents.Push(0);
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int depth = 0; // open parentheses / brackets: newlines inside are ignored

        for (int ln = 0; ln < lines.Length; ln++)
        {
            string line = lines[ln];
            int lineNo = ln + 1;
            int i = 0, col = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            {
                col += line[i] == '\t' ? 4 : 1;
                i++;
            }
            if (i >= line.Length || line[i] == '#') continue; // blank or comment line

            if (depth == 0)
            {
                if (col > indents.Peek())
                {
                    indents.Push(col);
                    tokens.Add(new Token(Tok.Indent, "", lineNo));
                }
                else
                {
                    while (col < indents.Peek())
                    {
                        indents.Pop();
                        tokens.Add(new Token(Tok.Dedent, "", lineNo));
                    }
                    if (col != indents.Peek())
                        throw new ScriptError("This line's indentation doesn't match any line above it.", lineNo);
                }
            }

            while (i < line.Length)
            {
                char c = line[i];
                if (c == ' ' || c == '\t') { i++; continue; }
                if (c == '#') break;

                if (char.IsDigit(c) || (c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
                {
                    int s = i;
                    while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.')) i++;
                    string text = line[s..i];
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                        throw new ScriptError($"'{text}' is not a valid number.", lineNo);
                    tokens.Add(new Token(Tok.Number, text, lineNo, n));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    tokens.Add(new Token(Tok.Ident, line[s..i], lineNo));
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    char q = c;
                    var sb = new StringBuilder();
                    i++;
                    while (i < line.Length && line[i] != q)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length) { i++; sb.Append(line[i] == 'n' ? '\n' : line[i]); }
                        else sb.Append(line[i]);
                        i++;
                    }
                    if (i >= line.Length) throw new ScriptError("A text is missing its closing quote.", lineNo);
                    i++;
                    tokens.Add(new Token(Tok.String, sb.ToString(), lineNo));
                    continue;
                }
                string? op = null;
                foreach (var o in Ops)
                    if (string.CompareOrdinal(line, i, o, 0, o.Length) == 0) { op = o; break; }
                if (op == null) throw new ScriptError($"I don't understand the symbol '{c}'.", lineNo);
                if (op is "(" or "[" or "{") depth++;
                if (op is ")" or "]" or "}") depth = Math.Max(0, depth - 1);
                tokens.Add(new Token(Tok.Op, op, lineNo));
                i += op.Length;
            }
            if (depth == 0) tokens.Add(new Token(Tok.Newline, "", lineNo));
        }
        int last = lines.Length;
        while (indents.Count > 1) { indents.Pop(); tokens.Add(new Token(Tok.Dedent, "", last)); }
        tokens.Add(new Token(Tok.Eof, "", last));
        return tokens;
    }
}
