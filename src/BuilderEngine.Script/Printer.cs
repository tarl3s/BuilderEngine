using System.Globalization;
using System.Text;

namespace BuilderEngine.Script;

/// <summary>
/// Turns the AST back into canonical Builder Script: 4-space indents, a blank line before each <c>when</c>,
/// only the parentheses the precedence needs. <c>Parser.Parse(Printer.Print(defs))</c> gives the same tree back
/// (the original spacing and blank lines are not kept). Comments are printed where the parser hung them: comment lines
/// just above their code, an end-of-line comment at the end of its code's first line. Empty blocks print as <c>when start:</c>.
/// </summary>
public static class Printer
{
    const string Indent = "    ";

    // ---------------- Mechanisms ----------------

    public static string Print(IEnumerable<MechanismDef> defs)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var m in defs)
        {
            if (!first) sb.Append('\n');
            first = false;
            Print(sb, m);
        }
        return sb.ToString();
    }

    public static string Print(MechanismDef m)
    {
        var sb = new StringBuilder();
        Print(sb, m);
        return sb.ToString();
    }

    static void Print(StringBuilder sb, MechanismDef m)
    {
        Before(sb, m.Comments, 0);
        sb.Append(m.IsSystem ? "system " : "mechanism ").Append(m.Name);
        if (m.Base != null) sb.Append(" from ").Append(m.Base);
        EndLine(sb, m.Comments?.After);
        foreach (var u in m.Includes) Line(sb, u, PrintIncludes(u));
        foreach (var p in m.Params) Line(sb, p, Print(p));
        foreach (var u in m.Uses) Line(sb, u, Print(u));
        bool gap = m.Params.Count + m.Uses.Count + m.Includes.Count > 0;
        foreach (var w in m.Whens)
        {
            if (gap) sb.Append('\n');
            gap = true;
            Before(sb, w.Comments, 1);
            sb.Append(Indent).Append(WhenHead(w.Event));
            PrintBody(sb, w.Body, 2, w.Comments?.After);
        }
        foreach (var p in m.Procs)
        {
            if (gap) sb.Append('\n');
            gap = true;
            Before(sb, p.Comments, 1);
            sb.Append(Indent).Append(ProcHead(p));
            PrintBody(sb, p.Body, 2, p.Comments?.After);
        }
        if (m.EndComments != null)
            foreach (var c in m.EndComments) sb.Append(Indent).Append(c).Append('\n');
    }

    /// <summary>A one-line part of a mechanism (param, uses, includes) with its comments.</summary>
    static void Line(StringBuilder sb, Commented part, string text)
    {
        Before(sb, part.Comments, 1);
        sb.Append(Indent).Append(text);
        EndLine(sb, part.Comments?.After);
    }

    /// <summary>The comment lines written above a piece of code, at its indentation.</summary>
    static void Before(StringBuilder sb, CodeComments? c, int indent)
    {
        if (c == null) return;
        foreach (var line in c.Before)
        {
            Pad(sb, indent);
            sb.Append(line).Append('\n');
        }
    }

    /// <summary>Ends a line, with its end-of-line comment when there is one.</summary>
    static void EndLine(StringBuilder sb, string? after)
    {
        if (after != null) sb.Append("  ").Append(after);
        sb.Append('\n');
    }

    static string? Join(string? a, string? b) => a == null ? b : b == null ? a : a + " " + b;

    /// <summary><c>to attack target, power</c></summary>
    public static string ProcHead(ProcDef p) => "to " + p.Name + (p.Params.Count == 0 ? "" : " " + string.Join(", ", p.Params));

    public static string Print(ParamDef p)
    {
        var s = "param " + p.Name;
        if (p.Type != "any") s += ": " + ParamType(p);
        if (p.Default != null) s += " = " + Print(p.Default);
        return s;
    }

    /// <summary>A param's type as written after the colon: <c>number</c>, <c>List of Object</c>, <c>Choice "easy", "hard"</c>.</summary>
    public static string ParamType(ParamDef p)
    {
        if (p.Type == "any") return "";
        if (p.Options is not { Count: > 0 } opts) return p.Type;
        return p.Type + " " + string.Join(", ", opts.Select(Quote));
    }

    public static string Print(UsesDef u)
    {
        string tail = UsesTail(u);
        return tail.Length == 0 ? "uses " + u.Name : "uses " + u.Name + " " + tail;
    }

    /// <summary>A system's <c>includes Pickup sound pickup_sound</c> line (the same settings as <c>uses</c>).</summary>
    public static string PrintIncludes(UsesDef u)
    {
        string tail = UsesTail(u);
        return tail.Length == 0 ? "includes " + u.Name : "includes " + u.Name + " " + tail;
    }

    /// <summary>What follows <c>uses Name</c>: <c>"slime.png"</c>, <c>max 2, speed 3</c>.</summary>
    public static string UsesTail(UsesDef u)
    {
        var parts = new List<string>();
        if (u.Positional != null)
        {
            string p = Print(u.Positional);
            // A value starting with a name would be read as a param name.
            parts.Add(StartsWithName(p) ? "(" + p + ")" : p);
        }
        foreach (var (key, value) in u.Args) parts.Add(key + " " + Print(value));
        return string.Join(", ", parts);
    }

    public static string WhenHead(EventSpec e)
    {
        string tail = EventTail(e);
        string head = EventWord(e.Kind);
        return "when " + (head.Length == 0 ? tail : tail.Length == 0 ? head : head + " " + tail);
    }

    /// <summary>The word(s) after <c>when</c> that name the event kind (empty for a signal).</summary>
    public static string EventWord(EventKind k) => k switch
    {
        EventKind.Start => "start",
        EventKind.Tick => "tick",
        EventKind.Touched => "touched by",
        EventKind.Told => "told",
        EventKind.Key => "key",
        EventKind.Tool => "tool",
        _ => ""
    };

    /// <summary>What follows the event word: <c>"player"</c>, <c>"damage" amount</c>, <c>Health.died</c>.</summary>
    public static string EventTail(EventSpec e) => e.Kind switch
    {
        EventKind.Touched => e.Filter != null ? Quote(e.Filter) : e.Bind ?? "",
        EventKind.Told => Quote(e.Filter ?? "") + (e.Bind != null ? " " + e.Bind : ""),
        EventKind.Key => Quote(e.Filter ?? ""),
        EventKind.Signal => e.Source == null ? e.Name ?? "" : e.Source + "." + e.Name,
        EventKind.Tool => EditorTools.EventTail(e),
        EventKind.Game => GameEvents.Tail(e),
        _ => ""
    };

    // ---------------- Statements ----------------

    /// <summary>One statement (and its nested blocks) as source lines, each ending with a newline.</summary>
    public static string Print(Stmt s, int indent)
    {
        var sb = new StringBuilder();
        PrintStmt(sb, s, indent);
        return sb.ToString();
    }

    /// <summary>The end of a block's head line (a ':' when the block is empty, the head's comment), then its lines.</summary>
    static void PrintBody(StringBuilder sb, List<Stmt> body, int indent, string? after = null)
    {
        if (body.Count == 0) sb.Append(':');
        EndLine(sb, after);
        foreach (var s in body) PrintStmt(sb, s, indent);
    }

    static void Pad(StringBuilder sb, int indent)
    {
        for (int i = 0; i < indent; i++) sb.Append(Indent);
    }

    /// <summary>One statement with its comments; <paramref name="after"/> = an extra end-of-line comment for its first line.</summary>
    static void PrintStmt(StringBuilder sb, Stmt s, int indent, string? after = null)
    {
        Before(sb, s.Comments, indent);
        after = Join(s.Comments?.After, after);
        switch (s)
        {
            case IfStmt i: PrintIf(sb, i, indent, "if ", after); break;
            case RepeatStmt r:
                Pad(sb, indent);
                sb.Append(r.Count == null ? "repeat forever" : "repeat " + RepeatCount(r.Count));
                PrintBody(sb, r.Body, indent + 1, after);
                break;
            case TogetherStmt t:
                Pad(sb, indent);
                sb.Append("together");
                PrintBody(sb, t.Body, indent + 1, after);
                break;
            case WhileStmt w:
                Pad(sb, indent);
                sb.Append("while ").Append(Print(w.Cond));
                PrintBody(sb, w.Body, indent + 1, after);
                break;
            case ForEachStmt f:
                Pad(sb, indent);
                sb.Append("for each ").Append(f.Var).Append(" in ").Append(Print(f.List));
                PrintBody(sb, f.Body, indent + 1, after);
                break;
            case RunStmt { Body.Count: 1 } r:
            {
                // 'run patrol', 'run repeat forever' + its block: the one statement printed with "run " in front of its
                // first line. Its comment lines go above that line, its end-of-line comment at its end.
                var one = WithoutRunComments(r.Body[0], sb, indent, ref after);
                var inner = new StringBuilder();
                PrintStmt(inner, one, indent, after);
                int at = indent * Indent.Length;
                sb.Append(inner, 0, at).Append("run ").Append(inner, at, inner.Length - at);
                break;
            }
            case RunStmt r:
                Pad(sb, indent);
                sb.Append("run");
                PrintBody(sb, r.Body, indent + 1, after);
                break;
            default:
                Pad(sb, indent);
                sb.Append(Simple(s));
                EndLine(sb, after);
                break;
        }
    }

    /// <summary>
    /// The statement a one-statement 'run' starts, without its comments (nor those of a 'run run x' chain inside it, which
    /// would land in front of the shared line): their comment lines are printed now, their end-of-line comments join
    /// <paramref name="after"/>.
    /// </summary>
    static Stmt WithoutRunComments(Stmt s, StringBuilder sb, int indent, ref string? after)
    {
        Before(sb, s.Comments, indent);
        after = Join(after, s.Comments?.After);
        if (s is RunStmt { Body.Count: 1 } r) return r with { Comments = null, Body = [WithoutRunComments(r.Body[0], sb, indent, ref after)] };
        return s.Comments == null ? s : s with { Comments = null };
    }

    /// <summary>True when the statement (or the one a one-statement 'run' starts) carries comments.</summary>
    static bool HasComments(Stmt s) => s.Comments != null || s is RunStmt { Body.Count: 1 } r && HasComments(r.Body[0]);

    static string RepeatCount(Expr count)
    {
        string c = Print(count);
        // 'repeat forever + 1' would read as 'repeat forever' (a count starting with the word, not only the word itself).
        return StartsWithWord(c, "forever") || c == "times" ? "(" + c + ")" : c;
    }

    /// <summary>'wait until' / 'wait for' are other statements: a value starting with those words gets parentheses.</summary>
    static string WaitSeconds(Expr seconds)
    {
        string c = Print(seconds);
        return StartsWithWord(c, "until") || StartsWithWord(c, "for") ? "(" + c + ")" : c;
    }

    static bool StartsWithWord(string s, string word) =>
        s.StartsWith(word, StringComparison.Ordinal) && (s.Length == word.Length || !(char.IsLetterOrDigit(s[word.Length]) || s[word.Length] == '_'));

    static void PrintIf(StringBuilder sb, IfStmt s, int indent, string word, string? after)
    {
        Pad(sb, indent);
        sb.Append(word).Append(Print(s.Cond));
        // One simple statement (without comments of its own) and no else: the one-line form "if hp <= 0: stop".
        if (s.Else == null && s.Then.Count == 1 && IsSimple(s.Then[0]) && !HasComments(s.Then[0]))
        {
            sb.Append(": ").Append(Simple(s.Then[0]));
            EndLine(sb, after);
            return;
        }
        PrintBody(sb, s.Then, indent + 1, after);
        if (s.Else == null) return;
        if (s.Else.Count == 1 && s.Else[0] is IfStmt elseIf)
        {
            Before(sb, elseIf.Comments, indent);
            PrintIf(sb, elseIf, indent, "else if ", elseIf.Comments?.After);
            return;
        }
        Pad(sb, indent);
        sb.Append("else");
        PrintBody(sb, s.Else, indent + 1);
    }

    public static bool IsSimple(Stmt s) => s switch
    {
        IfStmt or RepeatStmt or TogetherStmt or WhileStmt or ForEachStmt => false,
        RunStmt r => r.Body.Count == 1 && IsSimple(r.Body[0]),
        _ => true
    };

    /// <summary>A one-line statement (not if/repeat) without indentation or newline.</summary>
    public static string Simple(Stmt s) => s switch
    {
        AssignStmt a => string.Join('.', a.Target) + " " + a.Op + " " + Print(a.Value),
        WaitStmt w => "wait " + WaitSeconds(w.Seconds),
        WaitUntilStmt u => "wait until " + Print(u.Cond),
        WaitSignalStmt f => "wait for " + f.Full,
        WaitKeyStmt k => "wait for key " + Print(k.Key),
        ReturnStmt r => r.Value == null ? "return" : "return " + Print(r.Value),
        RunStmt { Body.Count: 1 } r when IsSimple(r.Body[0]) => "run " + Simple(r.Body[0]),
        StopStmt => "stop",
        EmitStmt e => "emit " + e.Signal + ArgsTail(e.Args),
        TellStmt t => Tell(t),
        CommandStmt c => Command(c),
        _ => throw new ArgumentException($"'{s.GetType().Name}' is not a one-line statement.")
    };

    /// <summary>
    /// <c>move Boss to 400, 300 in 2</c>. The parser reads a connector word (to, in, at...) that has something after it
    /// as the start of a named part, and skips 'seconds' after a named number: a value starting with such a word gets
    /// parentheses (<c>say (to) + 1</c>), except a lone word at the very end, which already reads as a value (<c>say to</c>).
    /// </summary>
    static string Command(CommandStmt c)
    {
        var sb = new StringBuilder(c.Name);
        int start = 0;
        // 'stop sound "rain.wav"' (World.Audio.cs): the word 'sound' reads as part of the command, not as a value.
        if (c.Name == "stop" && c.Args.Count > 0 && c.Args[0] is NameExpr { Path: [("sound" or "sounds") and var word] })
        {
            sb.Append(' ').Append(word);
            start = 1;
        }
        var parts = new List<(string? Connector, List<Expr> Values, int From)> { (null, c.Args, start) };
        if (c.Named != null) parts.AddRange(c.Named.Select(kv => ((string?)kv.Key, kv.Value, 0)));
        int total = parts.Sum(p => p.Values.Count - p.From), n = 0;
        for (int k = 0; k < parts.Count; k++)
        {
            var (connector, list, from) = parts[k];
            if (connector != null) sb.Append(' ').Append(connector);
            for (int i = from; i < list.Count; i++)
            {
                string v = Print(list[i]);
                bool last = ++n == total;
                bool connectorStart = Parser.Connectors.Any(w => StartsWithWord(v, w)) && !(last && Parser.Connectors.Contains(v));
                bool unitStart = connector != null && i > 0 && (StartsWithWord(v, "seconds") || StartsWithWord(v, "second"));
                if (connectorStart || unitStart) v = "(" + v + ")";
                // The values of one part are separated by commas; the first one follows a space (a "-1" right after
                // 'stop sound' would read as "sound - 1", so it takes a comma there too).
                bool joinsWord = i == start && start > 0 && (v.StartsWith('-') || v.StartsWith('(') || v.StartsWith('['));
                sb.Append(i == from && !joinsWord ? " " : ", ").Append(v);
            }
        }
        return sb.ToString();
    }

    /// <summary>Values after a command or signal: a space, then the values separated by commas.</summary>
    static string ArgsTail(List<Expr> args) => args.Count == 0 ? "" : " " + Args(args);

    public static string Args(List<Expr> args) => string.Join(", ", args.Select(Print));

    static string Tell(TellStmt t)
    {
        string target = Print(t.Target), msg = Print(t.Message);
        // "tell a -x" would read as "a - x" and "tell a (x)" as a call: parenthesize when needed.
        if (msg.StartsWith('-') || msg.StartsWith('[')) msg = "(" + msg + ")";
        if (msg.StartsWith('(')) target = "(" + target + ")";
        var sb = new StringBuilder("tell ").Append(target).Append(' ').Append(msg);
        for (int i = 0; i < t.Args.Count; i++)
        {
            string a = Print(t.Args[i]);
            // The first value may follow the message with a space unless it could join it ("-1", "(x)").
            sb.Append(i == 0 && !a.StartsWith('-') && !a.StartsWith('(') && !a.StartsWith('[') ? " " : ", ").Append(a);
        }
        return sb.ToString();
    }

    // ---------------- Expressions ----------------

    public static string Print(Expr e) => e switch
    {
        NumberExpr n => Number(n.Value),
        StringExpr s => Quote(s.Value),
        BoolExpr b => b.Value ? "true" : "false",
        NameExpr n => n.Full,
        UnaryExpr { Op: "not" } u => "not " + Wrap(u.Operand, Prec(u.Operand) < 3),
        UnaryExpr u => u.Op + Wrap(u.Operand, Prec(u.Operand) < 7),
        BinaryExpr b => Wrap(b.Left, Prec(b.Left) < BinaryPrec(b.Op)) + " " + b.Op + " " + Wrap(b.Right, Prec(b.Right) <= BinaryPrec(b.Op)),
        CallExpr c => c.Name + "(" + Args(c.Args) + ")",
        ListExpr l => "[" + Args(l.Items) + "]",
        RecordExpr r => Record.Print(r), // World.Records.cs
        MemberExpr m => (m.Target is CallExpr or IndexExpr or MemberExpr ? Print(m.Target) : "(" + Print(m.Target) + ")") + "." + m.Name,
        // Only names, calls, lists, items and parenthesized values can be followed by [ ].
        IndexExpr x => (x.Target is NameExpr or CallExpr or ListExpr or IndexExpr or MemberExpr ? Print(x.Target) : "(" + Print(x.Target) + ")") + "[" + Print(x.Index) + "]",
        _ => throw new ArgumentException($"Unknown expression '{e.GetType().Name}'.")
    };

    static string Wrap(Expr e, bool parens) => parens ? "(" + Print(e) + ")" : Print(e);

    /// <summary>Binding strength, matching the parser: or 1, and 2, not 3, compare 4, + - 5, * / % 6, unary minus 7, values 8.</summary>
    static int Prec(Expr e) => e switch
    {
        BinaryExpr b => BinaryPrec(b.Op),
        UnaryExpr u => u.Op == "not" ? 3 : 7,
        NumberExpr n when n.Value < 0 => 7,
        _ => 8
    };

    static int BinaryPrec(string op) => op switch
    {
        "or" => 1,
        "and" => 2,
        "==" or "!=" or "<>" or "<" or ">" or "<=" or ">=" => 4,
        "+" or "-" => 5,
        _ => 6
    };

    /// <summary>
    /// The shortest text that reads back as exactly this number, written without an exponent (the lexer reads none):
    /// <c>1E+21</c> → 1000000000000000000000, <c>1E-30</c> → 0.000…001. (A custom format kept only 15 digits and wrote 0 for 1E-30.)
    /// </summary>
    public static string Number(double v)
    {
        string s = v.ToString("R", CultureInfo.InvariantCulture);
        int e = s.IndexOf('E');
        if (e < 0) return s;
        bool negative = s[0] == '-';
        string mantissa = s[(negative ? 1 : 0)..e];
        int exponent = int.Parse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int dot = mantissa.IndexOf('.');
        string digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        int point = (dot < 0 ? mantissa.Length : dot) + exponent; // where the decimal point goes in the digits
        string plain = point <= 0 ? "0." + new string('0', -point) + digits
            : point >= digits.Length ? digits + new string('0', point - digits.Length)
            : digits[..point] + "." + digits[point..];
        return negative ? "-" + plain : plain;
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    static bool StartsWithName(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_');
}
