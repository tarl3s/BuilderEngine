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

/// <summary>
/// A <c># comment</c> in the source: its line, its text from the '#' on, and whether code comes before it on that line
/// (<c>x = 1  # note</c>). The parser hangs comments on the code next to them so printing the code keeps them.
/// </summary>
public readonly record struct Comment(int Line, string Text, bool Trailing);

/// <summary>Turns source text into tokens; indentation becomes Indent/Dedent tokens (like Python).</summary>
public static partial class Lexer
{
    static readonly string[] Ops = ["+=", "-=", "*=", "/=", "==", "!=", "<>", "<=", ">=", "+", "-", "*", "/", "%", "=", "<", ">", "(", ")", "[", "]", "{", "}", ",", ".", ":"];

    public static List<Token> Tokenize(string source) => Tokenize(source, null);

    /// <summary>Tokenizes, and adds every comment (in source order) to <paramref name="comments"/> when it is given.</summary>
    public static List<Token> Tokenize(string source, List<Comment>? comments)
    {
        var tokens = new List<Token>();
        var indents = new Stack<int>();
        indents.Push(0);
        // A byte order mark at the start (text saved by some editors or pasted from them) is not part of the code.
        if (source.Length > 0 && source[0] == '﻿') source = source[1..];
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
            if (i >= line.Length) continue; // blank line
            if (line[i] == '#') // comment line
            {
                comments?.Add(new Comment(lineNo, line[i..].TrimEnd(), Trailing: false));
                continue;
            }

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
                if (c == '#')
                {
                    comments?.Add(new Comment(lineNo, line[i..].TrimEnd(), Trailing: true));
                    break;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
                {
                    int s = i;
                    while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.')) i++;
                    // An exponent: 1e3, 2.5E-4, 1e+308. Only an 'e' with digits right after it (a sign may come first),
                    // so '3times' and 'x = 2' followed by a name still read as before.
                    if (i + 1 < line.Length && line[i] is 'e' or 'E')
                    {
                        int d = line[i + 1] is '+' or '-' ? i + 2 : i + 1;
                        if (d < line.Length && char.IsAsciiDigit(line[d]))
                        {
                            i = d;
                            while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
                        }
                    }
                    string text = line[s..i];
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                        throw new ScriptError($"'{text}' is not a valid number.", lineNo);
                    // 400 digits parse as infinity, which can't be written back as a number.
                    if (double.IsInfinity(n)) throw new ScriptError($"The number '{text[..Math.Min(text.Length, 12)]}...' is too big.", lineNo);
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
                if (op == null)
                {
                    // An emoji is two chars (a surrogate pair): show the whole symbol, not half of it.
                    string symbol = char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]) ? line.Substring(i, 2) : c.ToString();
                    throw new ScriptError($"I don't understand the symbol '{symbol}'.", lineNo);
                }
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
