namespace BuilderEngine.Script;

/// <summary>The colour class of a piece of code (the editor maps each to a colour: BuilderEngine's CodeColors).</summary>
public enum CodeClass : byte
{
    Plain,
    /// <summary>when if else repeat while for each together run wait until return stop forever emit tell</summary>
    Control,
    /// <summary>mechanism system param uses includes global to from, the connectors (in at by with…), and or not</summary>
    Declaration,
    /// <summary>true false nothing none self other</summary>
    Constant,
    String,
    Number,
    Comment,
    /// <summary>a command (built-in, engine, plugin or the mechanism's own 'to'), or any call: <c>name(…)</c></summary>
    Function,
    /// <summary>a mechanism name (after mechanism / system / uses / from / includes, or a known one) or a param's type</summary>
    Type,
    /// <summary>variables, params, members</summary>
    Variable,
    Operator,
}

/// <summary>A coloured piece of one line.</summary>
public readonly record struct CodeSpan(int Start, int Length, CodeClass Class);

/// <summary>
/// What the colours need to know beyond one line: the mechanism's own commands (<c>to name</c>) and the mechanism
/// names in the project. Build one per text change with <see cref="For"/> (cheap: one pass over the line starts).
/// </summary>
public sealed class SyntaxContext
{
    public static readonly SyntaxContext Empty = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlySet<string> Procs { get; }
    public IReadOnlySet<string> Mechanisms { get; }

    SyntaxContext(HashSet<string> procs, HashSet<string> mechanisms) { Procs = procs; Mechanisms = mechanisms; }

    /// <summary>The commands this source makes with 'to' and the given mechanism names (null = none known).</summary>
    public static SyntaxContext For(string? source, IEnumerable<string>? mechanisms = null)
    {
        var procs = new HashSet<string>(StringComparer.Ordinal);
        var mechs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mechanisms != null) foreach (var m in mechanisms) mechs.Add(m);
        source ??= "";
        int pos = 0;
        while (pos <= source.Length)
        {
            int end = source.IndexOf('\n', pos);
            if (end < 0) end = source.Length;
            int i = pos;
            while (i < end && source[i] is ' ' or '\t') i++;
            if (end - i > 3 && string.CompareOrdinal(source, i, "to", 0, 2) == 0 && source[i + 2] is ' ' or '\t')
            {
                i += 3;
                while (i < end && source[i] is ' ' or '\t') i++;
                int s = i;
                while (i < end && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                if (i > s && !char.IsDigit(source[s])) procs.Add(source[s..i]);
            }
            // A mechanism in this text (several in one file) counts as known too.
            i = pos;
            while (i < end && source[i] is ' ' or '\t') i++;
            foreach (var head in (string[])["mechanism ", "system "])
                if (end - i > head.Length && string.CompareOrdinal(source, i, head, 0, head.Length) == 0)
                {
                    int s = i + head.Length;
                    int e = s;
                    while (e < end && (char.IsLetterOrDigit(source[e]) || source[e] == '_')) e++;
                    if (e > s) mechs.Add(source[s..e]);
                }
            pos = end + 1;
        }
        return new SyntaxContext(procs, mechs);
    }

    /// <summary>Same commands and mechanisms (nothing to recolour when a change keeps them).</summary>
    public bool SameAs(SyntaxContext other) => Procs.SetEquals(other.Procs) && Mechanisms.SetEquals(other.Mechanisms);
}

/// <summary>
/// Colours Builder Script one line at a time (lines never depend on each other: no text or comment spans lines).
/// The pieces come from <see cref="Lexer.ScanLine"/>, so they are exactly what the language reads; the word lists come
/// from the language itself (<see cref="Parser.Keywords"/>, <see cref="World.BuiltInCommands"/>, the engine's and the
/// plugins' commands). The editor (CodeColorizer) and every code preview use this, so colours agree everywhere.
/// </summary>
public static class SyntaxColors
{
    public static readonly HashSet<string> ControlWords = new(StringComparer.Ordinal)
    {
        "when", "if", "else", "repeat", "while", "for", "each", "together", "run", "wait", "until", "return", "stop", "forever", "emit", "tell",
    };

    public static readonly HashSet<string> DeclarationWords = new(StringComparer.Ordinal)
    {
        "mechanism", "system", "param", "uses", "includes", "global", "and", "or", "not",
    };

    public static readonly HashSet<string> ConstantWords = new(StringComparer.Ordinal) { "true", "false", "nothing", "none", "self", "other" };

    static HashSet<string>? s_commands;

    /// <summary>The engine's commands (one and two words: "camera follow"), the built-in ones, the editor tools' ones.</summary>
    static HashSet<string> EngineCommands
    {
        get
        {
            if (s_commands != null) return s_commands;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { set.UnionWith(PluginManager.EngineNames().Commands); }
            catch { /* no engine world (should not happen): the fixed lists below still colour the common ones */ }
            set.UnionWith(World.BuiltInCommands);
            set.UnionWith(World.SceneCommands);
            set.UnionWith(EditorTools.Commands);
            return s_commands = set;
        }
    }

    static bool IsCommand(string name, SyntaxContext ctx)
    {
        if (ctx.Procs.Contains(name) || EngineCommands.Contains(name)) return true;
        foreach (var c in PluginRegistry.Commands) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    [ThreadStatic] static List<RawSpan>? t_raw;

    /// <summary>The coloured pieces of one line (no line break in it), in order; spaces are left out.</summary>
    public static List<CodeSpan> Classify(string line, SyntaxContext? ctx = null)
    {
        var result = new List<CodeSpan>();
        Classify(line, ctx ?? SyntaxContext.Empty, result);
        return result;
    }

    /// <summary>Same, filling <paramref name="result"/> (cleared first).</summary>
    public static void Classify(string line, SyntaxContext ctx, List<CodeSpan> result)
    {
        result.Clear();
        var raw = t_raw ??= [];
        Lexer.ScanLine(line, raw);
        string Text(int k) => line.Substring(raw[k].Start, raw[k].Length);
        bool IsOp(int k, string op) => k >= 0 && k < raw.Count && raw[k].Kind == RawKind.Op && raw[k].Length == op.Length && string.CompareOrdinal(line, raw[k].Start, op, 0, op.Length) == 0;
        bool IsIdent(int k) => k >= 0 && k < raw.Count && raw[k].Kind == RawKind.Ident;

        // What kind of line this is, from its first word.
        string head = IsIdent(0) ? Text(0) : "";
        bool paramLine = head == "param";
        int typeFrom = -1, typeTo = -1; // param name: type = default → the type's pieces
        if (paramLine)
        {
            for (int k = 1; k < raw.Count; k++)
                if (IsOp(k, ":")) { typeFrom = k + 1; break; }
            if (typeFrom > 0)
            {
                typeTo = raw.Count;
                for (int k = typeFrom; k < raw.Count; k++)
                    if (IsOp(k, "=") || raw[k].Kind == RawKind.Comment) { typeTo = k; break; }
            }
        }

        int depth = 0;
        int stmt = 0; // where the current statement starts (0, or right after the ':' of a one-line 'if x: stop')
        for (int k = 0; k < raw.Count; k++)
        {
            var r = raw[k];
            CodeClass cls;
            switch (r.Kind)
            {
                case RawKind.Comment: cls = CodeClass.Comment; break;
                case RawKind.String: cls = CodeClass.String; break;
                case RawKind.Number: cls = CodeClass.Number; break;
                case RawKind.Unknown: cls = CodeClass.Plain; break;
                case RawKind.Op:
                    cls = CodeClass.Operator;
                    if (IsOp(k, "(") || IsOp(k, "[") || IsOp(k, "{")) depth++;
                    else if (IsOp(k, ")") || IsOp(k, "]") || IsOp(k, "}")) depth = Math.Max(0, depth - 1);
                    else if (IsOp(k, ":") && depth == 0 && !paramLine && ControlWords.Contains(head)) stmt = k + 1;
                    break;
                default:
                    cls = Ident(k);
                    break;
            }
            result.Add(new CodeSpan(r.Start, r.Length, cls));
        }

        CodeClass Ident(int k)
        {
            string w = Text(k);
            bool afterDot = IsOp(k - 1, ".");
            bool call = IsOp(k + 1, "(");
            if (afterDot) return call || ctx.Procs.Contains(w) ? CodeClass.Function : ctx.Mechanisms.Contains(w) ? CodeClass.Type : CodeClass.Variable;
            if (paramLine && k >= typeFrom && k < typeTo) return CodeClass.Type;
            if (paramLine && k == 1) return CodeClass.Variable;

            // The names a line declares or uses: mechanism Name from Base / uses Name / includes Name.
            if (k > 0 && IsIdent(k - 1))
            {
                string prev = Text(k - 1);
                if (prev is "mechanism" or "system" or "uses" or "includes" && k - 1 == 0) return CodeClass.Type;
                if (prev == "from" && head is "mechanism" or "system") return CodeClass.Type;
                if (prev == "to" && k - 1 == stmt) return CodeClass.Function; // to name a, b: the new command
            }

            // A command at the start of a statement: one word (print) or two (camera follow, stop sound).
            if (k == stmt)
            {
                if (IsIdent(k + 1) && EngineCommands.Contains(w + " " + Text(k + 1))) return CodeClass.Function;
                if (!ControlWords.Contains(w) && !DeclarationWords.Contains(w) && w != "to" && !IsAssignment(k + 1) && IsCommand(w, ctx)) return CodeClass.Function;
            }
            if (k == stmt + 1 && IsIdent(stmt) && EngineCommands.Contains(Text(stmt) + " " + w)) return CodeClass.Function;

            if (ControlWords.Contains(w)) return CodeClass.Control;
            if (w == "in" && head == "for") return CodeClass.Control;
            if (ConstantWords.Contains(w)) return CodeClass.Constant;
            if (DeclarationWords.Contains(w) || Parser.Connectors.Contains(w)) return CodeClass.Declaration;
            if (w is "seconds" or "second" or "times" && k > 0 && raw[k - 1].Kind == RawKind.Number) return CodeClass.Control;
            if (call || (ctx.Procs.Contains(w) && !IsAssignment(k + 1))) return CodeClass.Function; // run taunt
            if (ctx.Mechanisms.Contains(w)) return CodeClass.Type;
            return CodeClass.Variable;
        }

        bool IsAssignment(int k) => IsOp(k, "=") || IsOp(k, "+=") || IsOp(k, "-=") || IsOp(k, "*=") || IsOp(k, "/=") || IsOp(k, ".") || IsOp(k, "[");
    }

    /// <summary>The whole text, line by line: (line index from 0, its pieces). For previews.</summary>
    public static IEnumerable<(int Line, string Text, List<CodeSpan> Spans)> ClassifyLines(string source, SyntaxContext? ctx = null)
    {
        ctx ??= SyntaxContext.For(source);
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++) yield return (i, lines[i], Classify(lines[i], ctx));
    }
}
