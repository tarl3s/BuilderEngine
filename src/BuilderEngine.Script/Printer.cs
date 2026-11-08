using System.Globalization;
using System.Text;

namespace BuilderEngine.Script;

/// <summary>
/// Turns the AST back into canonical Builder Script: 4-space indents, a blank line before each <c>when</c>,
/// only the parentheses the precedence needs. <c>Parser.Parse(Printer.Print(defs))</c> gives the same tree back
/// (comments and the original spacing are not kept). Empty blocks print as <c>when start:</c>.
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
        sb.Append("mechanism ").Append(m.Name).Append('\n');
        foreach (var p in m.Params) sb.Append(Indent).Append(Print(p)).Append('\n');
        foreach (var u in m.Uses) sb.Append(Indent).Append(Print(u)).Append('\n');
        bool gap = m.Params.Count + m.Uses.Count > 0;
        foreach (var w in m.Whens)
        {
            if (gap) sb.Append('\n');
            gap = true;
            sb.Append(Indent).Append(WhenHead(w.Event));
            PrintBody(sb, w.Body, 2);
        }
    }

    public static string Print(ParamDef p)
    {
        var s = "param " + p.Name;
        if (p.Type != "any") s += ": " + p.Type;
        if (p.Default != null) s += " = " + Print(p.Default);
        return s;
    }

    public static string Print(UsesDef u)
    {
        string tail = UsesTail(u);
        return tail.Length == 0 ? "uses " + u.Name : "uses " + u.Name + " " + tail;
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
        _ => ""
    };

    /// <summary>What follows the event word: <c>"player"</c>, <c>"damage" amount</c>, <c>Health.died</c>.</summary>
    public static string EventTail(EventSpec e) => e.Kind switch
    {
        EventKind.Touched => e.Filter != null ? Quote(e.Filter) : e.Bind ?? "",
        EventKind.Told => Quote(e.Filter ?? "") + (e.Bind != null ? " " + e.Bind : ""),
        EventKind.Key => Quote(e.Filter ?? ""),
        EventKind.Signal => e.Source == null ? e.Name ?? "" : e.Source + "." + e.Name,
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

    static void PrintBody(StringBuilder sb, List<Stmt> body, int indent)
    {
        if (body.Count == 0) { sb.Append(":\n"); return; }
        sb.Append('\n');
        foreach (var s in body) PrintStmt(sb, s, indent);
    }

    static void Pad(StringBuilder sb, int indent)
    {
        for (int i = 0; i < indent; i++) sb.Append(Indent);
    }

    static void PrintStmt(StringBuilder sb, Stmt s, int indent)
    {
        switch (s)
        {
            case IfStmt i: PrintIf(sb, i, indent, "if "); break;
            case RepeatStmt r:
                Pad(sb, indent);
                sb.Append(r.Count == null ? "repeat forever" : "repeat " + RepeatCount(r.Count));
                PrintBody(sb, r.Body, indent + 1);
                break;
            default:
                Pad(sb, indent);
                sb.Append(Simple(s)).Append('\n');
                break;
        }
    }

    static string RepeatCount(Expr count)
    {
        string c = Print(count);
        return c is "forever" or "times" ? "(" + c + ")" : c;
    }

    static void PrintIf(StringBuilder sb, IfStmt s, int indent, string word)
    {
        Pad(sb, indent);
        sb.Append(word).Append(Print(s.Cond));
        // One simple statement and no else: the one-line form "if hp <= 0: stop".
        if (s.Else == null && s.Then.Count == 1 && IsSimple(s.Then[0]))
        {
            sb.Append(": ").Append(Simple(s.Then[0])).Append('\n');
            return;
        }
        PrintBody(sb, s.Then, indent + 1);
        if (s.Else == null) return;
        if (s.Else.Count == 1 && s.Else[0] is IfStmt elseIf)
        {
            PrintIf(sb, elseIf, indent, "else if ");
            return;
        }
        Pad(sb, indent);
        sb.Append("else");
        PrintBody(sb, s.Else, indent + 1);
    }

    public static bool IsSimple(Stmt s) => s is not (IfStmt or RepeatStmt);

    /// <summary>A one-line statement (not if/repeat) without indentation or newline.</summary>
    public static string Simple(Stmt s) => s switch
    {
        AssignStmt a => string.Join('.', a.Target) + " " + a.Op + " " + Print(a.Value),
        WaitStmt w => "wait " + Print(w.Seconds),
        StopStmt => "stop",
        EmitStmt e => "emit " + e.Signal + ArgsTail(e.Args),
        TellStmt t => Tell(t),
        CommandStmt c => c.Name + ArgsTail(c.Args) + NamedTail(c.Named),
        _ => throw new ArgumentException($"'{s.GetType().Name}' is not a one-line statement.")
    };

    static string NamedTail(Dictionary<string, List<Expr>>? named) =>
        named == null ? "" : string.Concat(named.Select(kv => " " + kv.Key + (kv.Value.Count == 0 ? "" : " " + Args(kv.Value))));

    /// <summary>Values after a command or signal: a space, then the values separated by commas.</summary>
    static string ArgsTail(List<Expr> args) => args.Count == 0 ? "" : " " + Args(args);

    public static string Args(List<Expr> args) => string.Join(", ", args.Select(Print));

    static string Tell(TellStmt t)
    {
        string target = Print(t.Target), msg = Print(t.Message);
        // "tell a -x" would read as "a - x" and "tell a (x)" as a call: parenthesize when needed.
        if (msg.StartsWith('-')) msg = "(" + msg + ")";
        if (msg.StartsWith('(')) target = "(" + target + ")";
        var sb = new StringBuilder("tell ").Append(target).Append(' ').Append(msg);
        for (int i = 0; i < t.Args.Count; i++)
        {
            string a = Print(t.Args[i]);
            // The first value may follow the message with a space unless it could join it ("-1", "(x)").
            sb.Append(i == 0 && !a.StartsWith('-') && !a.StartsWith('(') ? " " : ", ").Append(a);
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

    public static string Number(double v)
    {
        string s = v.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('E')) s = v.ToString("0.############################", CultureInfo.InvariantCulture);
        return s;
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
