namespace BuilderEngine.Script;

/// <summary>What a piece of one source line is, for the code editor's colours (<see cref="Lexer.ScanLine"/>).</summary>
public enum RawKind : byte { Ident, Number, String, Op, Comment, Unknown }

/// <summary>A piece of one line: where it starts, how long it is, what it is. <c>Open</c> = a text still missing its closing quote.</summary>
public readonly record struct RawSpan(int Start, int Length, RawKind Kind, bool Open = false);

public static partial class Lexer
{
    /// <summary>The operators and punctuation, longest first (what <see cref="Tokenize(string)"/> reads).</summary>
    public static IReadOnlyList<string> Operators => Ops;

    /// <summary>
    /// Splits one line into pieces with the same rules as <see cref="Tokenize(string)"/> (names, numbers with exponents,
    /// texts in either quote with backslash escapes, the operators, '#' comments), keeping where each piece is. It never
    /// throws: while typing, a text missing its closing quote runs to the end of the line and a symbol the language
    /// doesn't know is one <see cref="RawKind.Unknown"/> piece. Used by the code editor's colours, so they always agree
    /// with what the language reads (a test compares the two on every starter-kit file).
    /// </summary>
    public static void ScanLine(string line, List<RawSpan> spans)
    {
        spans.Clear();
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c is ' ' or '\t' or '\r' or '﻿') { i++; continue; }
            if (c == '#') { spans.Add(new RawSpan(i, line.Length - i, RawKind.Comment)); break; }
            int s = i;
            if (char.IsDigit(c) || (c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
            {
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.')) i++;
                if (i + 1 < line.Length && line[i] is 'e' or 'E')
                {
                    int d = line[i + 1] is '+' or '-' ? i + 2 : i + 1;
                    if (d < line.Length && char.IsAsciiDigit(line[d]))
                    {
                        i = d;
                        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
                    }
                }
                spans.Add(new RawSpan(s, i - s, RawKind.Number));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                spans.Add(new RawSpan(s, i - s, RawKind.Ident));
                continue;
            }
            if (c is '"' or '\'')
            {
                i++;
                while (i < line.Length && line[i] != c)
                    i += line[i] == '\\' && i + 1 < line.Length ? 2 : 1;
                bool open = i >= line.Length;
                if (!open) i++;
                spans.Add(new RawSpan(s, i - s, RawKind.String, open));
                continue;
            }
            string? op = null;
            foreach (var o in Ops)
                if (string.CompareOrdinal(line, i, o, 0, o.Length) == 0) { op = o; break; }
            if (op != null)
            {
                spans.Add(new RawSpan(s, op.Length, RawKind.Op));
                i += op.Length;
                continue;
            }
            i += char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]) ? 2 : 1;
            spans.Add(new RawSpan(s, i - s, RawKind.Unknown));
        }
    }
}
