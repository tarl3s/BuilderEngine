using System.Text;
using System.Text.RegularExpressions;

namespace BuilderEngine.Script;

/// <summary>
/// Reads Builder Script out of a model's answer, however messy: the code may come in a ``` block (any language tag), in
/// several blocks (before/after), or bare between sentences; small models add tabs, semicolons, braces, <c>//</c>
/// comments, <c>&amp;&amp;</c>, <c>on start</c>, <c>function jump()</c>… Each is turned into the Builder Script it meant.
/// Nothing here throws; the parser and <see cref="AiCheck"/> judge the result.
/// </summary>
public static class AiReply
{
    static readonly Regex Fence = new(@"```[ \t]*([\w+\-#.]*)[ \t]*\r?\n(.*?)(?:```|\z)", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex MechLine = new(@"^\s*mechanism\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex CodeStart = new(@"^\s*(mechanism|when|param|uses|to|on)\s", RegexOptions.Compiled);

    /// <summary>
    /// The Builder Script in a reply (null when there is none). <paramref name="forcedName"/> renames the first mechanism
    /// (Edit / Fix keep the open one's name); <paramref name="fallbackName"/> names code that came without a
    /// <c>mechanism</c> line.
    /// </summary>
    public static string? ExtractScript(string? reply, string? forcedName = null, string fallbackName = "MyMechanism")
    {
        string text = (reply ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Trim().Length == 0) return null;

        var blocks = Fence.Matches(text).Select(m => m.Groups[2].Value).Where(b => b.Trim().Length > 0).ToList();
        string? code = null;
        if (blocks.Count > 0)
        {
            var withMech = blocks.Where(b => MechLine.IsMatch(b)).ToList();
            if (withMech.Count > 0)
            {
                // Several blocks: different mechanisms are kept together; the same one written twice (before / after) → the last.
                var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();
                foreach (var b in withMech)
                    foreach (var (name, src) in AiMerge.Split(Clean(b)))
                    {
                        if (!byName.ContainsKey(name)) order.Add(name);
                        byName[name] = src;
                    }
                code = string.Join("\n", order.Select(n => byName[n].TrimEnd() + "\n"));
            }
            else code = blocks.Where(b => b.Split('\n').Any(l => CodeStart.IsMatch(l))).OrderByDescending(b => b.Length).FirstOrDefault()
                        ?? blocks.OrderByDescending(b => b.Length).First();
        }
        else code = BareCode(text);
        if (code == null) return null;

        code = Clean(code);
        if (!MechLine.IsMatch(code)) code = Wrap(code, forcedName ?? fallbackName);
        if (forcedName != null) code = MechLine.Replace(code, m => m.Value[..(m.Groups[1].Index - m.Index)] + forcedName, 1);
        code = code.Trim('\n');
        return code.Length == 0 ? null : code + "\n";
    }

    /// <summary>Code written without a fence: from the first code-looking line to the first sentence at the left margin after it.</summary>
    static string? BareCode(string text)
    {
        var lines = text.Split('\n');
        int start = Array.FindIndex(lines, l => CodeStart.IsMatch(l.Replace('\t', ' ')));
        if (start < 0) return null;
        var sb = new StringBuilder();
        for (int i = start; i < lines.Length; i++)
        {
            string l = lines[i];
            bool atMargin = l.Length > 0 && !char.IsWhiteSpace(l[0]);
            if (i > start && atMargin && !CodeStart.IsMatch(l) && !l.StartsWith('#') && !l.StartsWith('}') && !l.StartsWith("//")) break;
            sb.Append(l).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Code that came without its <c>mechanism Name</c> line: one is added and everything moves in one level.</summary>
    static string Wrap(string code, string name)
    {
        var lines = code.Split('\n');
        var sb = new StringBuilder($"mechanism {name}\n");
        foreach (var l in lines) sb.Append(l.Trim().Length == 0 ? "" : "    " + l).Append('\n');
        return sb.ToString();
    }

    /// <summary>The usual mistakes of models (and of people coming from other languages), fixed line by line.</summary>
    public static string Clean(string code)
    {
        var outLines = new List<string>();
        foreach (var raw in code.Replace("\r\n", "\n").Split('\n'))
        {
            // Leading tabs → 4 spaces.
            int n = 0, col = 0;
            while (n < raw.Length && (raw[n] == ' ' || raw[n] == '\t')) { col += raw[n] == '\t' ? 4 : 1; n++; }
            string body = raw[n..].TrimEnd();

            body = CodeOnly(body, s =>
            {
                s = Regex.Replace(s, @"\s*//(.*)$", m => "  #" + m.Groups[1].Value); // // comment
                s = Regex.Replace(s, @"\s*&&\s*", " and ");
                s = Regex.Replace(s, @"\s*\|\|\s*", " or ");
                s = Regex.Replace(s, @"!(?!=)\s*", "not ");
                s = Regex.Replace(s, @"\bthis\.", "self.");
                s = Regex.Replace(s, @"\bTrue\b", "true");
                s = Regex.Replace(s, @"\bFalse\b", "false");
                s = Regex.Replace(s, @"\bnull\b|\bNone\b", "0");
                s = Regex.Replace(s, @"\s*;\s*$", "");
                s = Regex.Replace(s, @"\s*\{\s*$", ""); // a block opened with a brace: indentation already says it
                return s;
            });
            string trimmed = body.Trim();
            if (trimmed is "}" or "};" or "{" or "end" or "end;" or "endif" or "pass") continue;
            if (trimmed.StartsWith("} else", StringComparison.Ordinal)) body = trimmed[1..].TrimStart();

            body = FixHead(body);
            outLines.Add(body.Length == 0 ? "" : new string(' ', col) + body);
        }
        // Two-space indentation (common in small models) → four.
        var indents = outLines.Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).Select(l => l.Length - l.TrimStart().Length).Where(i => i > 0).ToList();
        if (indents.Count > 0 && indents.Min() == 2 && indents.All(i => i % 2 == 0))
            outLines = outLines.Select(l => l.Trim().Length == 0 ? l : new string(' ', (l.Length - l.TrimStart().Length) * 2) + l.TrimStart()).ToList();
        return string.Join("\n", outLines);
    }

    /// <summary>Applies a fix to the parts of a line outside quotes and comments.</summary>
    static string CodeOnly(string line, Func<string, string> fix)
    {
        var sb = new StringBuilder();
        var part = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote == '\0' && c == '#') { sb.Append(fix(part.ToString())); part.Clear(); sb.Append(line[i..]); return sb.ToString(); }
            if (quote == '\0' && (c == '"' || c == '\''))
            {
                sb.Append(fix(part.ToString())); part.Clear();
                quote = c; sb.Append(c); continue;
            }
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < line.Length) { sb.Append(line[++i]); continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            part.Append(c);
        }
        sb.Append(fix(part.ToString()));
        return sb.ToString();
    }

    /// <summary>Block heads written the way other languages do: on start → when start, function jump() → to jump, elif → else if...</summary>
    static string FixHead(string line)
    {
        string s = line;
        s = Regex.Replace(s, @"^(?:function|def|func|fn|command|procedure|method)\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*:?\s*$", m =>
            "to " + m.Groups[1].Value + (m.Groups[2].Value.Trim().Length > 0 ? " " + m.Groups[2].Value.Trim() : ""));
        s = Regex.Replace(s, @"^(?:function|def|func|fn|command)\s+", "to ");
        s = Regex.Replace(s, @"^on\s+(?=start\b|tick\b|touched\b|told\b|key\b|[A-Z]\w*\.)", "when ");
        s = Regex.Replace(s, @"^when\s+(?:update|frame|every\s+frame|each\s+frame|loop|always)\s*:?\s*$", "when tick");
        s = Regex.Replace(s, @"^when\s+(?:begin|created|spawned|ready|init|game\s+starts|starts)\s*:?\s*$", "when start");
        s = Regex.Replace(s, @"^when\s+(?:collides?\s+with|collision\s+with|touch(?:es)?|hit\s+by|touching)\s+", "when touched by ");
        s = Regex.Replace(s, @"^when\s+(?:key\s*pressed|pressed|key_pressed|keypress|press)\s+", "when key ");
        s = Regex.Replace(s, @"^elif\b", "else if");
        s = Regex.Replace(s, @"^else\s*if\s*\((.*)\)\s*:?$", "else if $1");
        s = Regex.Replace(s, @"^if\s*\((.*)\)\s*:?$", "if $1");
        s = Regex.Replace(s, @"^while\s*\((.*)\)\s*:?$", "while $1");
        s = Regex.Replace(s, @"^while\s+true\s*:?\s*$", "repeat forever");
        s = Regex.Replace(s, @"^(?:forever|loop)\s*:?\s*$", "repeat forever");
        s = Regex.Replace(s, @"^(?:var|let|const|local|set)\s+([A-Za-z_][\w.]*\s*[-+*/]?=)", "$1");
        s = Regex.Replace(s, @"^(mechanism\s+[A-Za-z_]\w*)\s*:\s*$", "$1");
        s = Regex.Replace(s, @"^wait\s*\(\s*([^()]*)\s*\)\s*$", "wait $1");
        s = Regex.Replace(s, @"^(?:sleep|delay)\s+", "wait ");
        return s;
    }

    /// <summary>The words around the code (what the model says it did), short.</summary>
    public static string Prose(string? reply, int max = 600)
    {
        string text = Fence.Replace((reply ?? "").Replace("\r\n", "\n"), "").Trim();
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }
}
