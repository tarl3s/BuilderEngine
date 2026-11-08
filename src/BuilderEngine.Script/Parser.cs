using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>Reads Builder Script tokens into mechanisms. One file may hold several mechanisms.</summary>
public sealed partial class Parser
{
    readonly List<Token> _t;
    int _p;
    /// <summary>The source's comments in order; <see cref="TakeComments"/> hands each one to the code next to it.</summary>
    readonly List<Comment> _comments;
    int _nextComment;

    Parser(List<Token> tokens, List<Comment>? comments = null) { _t = tokens; _comments = comments ?? []; }

    public static List<MechanismDef> Parse(string source)
    {
        var comments = new List<Comment>();
        return new Parser(Lexer.Tokenize(source, comments), comments).ParseFile();
    }

    /// <summary>
    /// The comments no code has taken yet that are above <paramref name="line"/>, and the one at the end of that line:
    /// they belong to the code that starts on that line (null when there are none).
    /// </summary>
    CodeComments? TakeComments(int line)
    {
        List<string>? before = null;
        while (_nextComment < _comments.Count && _comments[_nextComment].Line < line)
            (before ??= []).Add(_comments[_nextComment++].Text);
        string? after = null;
        if (_nextComment < _comments.Count && _comments[_nextComment].Line == line && _comments[_nextComment].Trailing)
            after = _comments[_nextComment++].Text;
        return before == null && after == null ? null : new CodeComments(before ?? [], after);
    }

    /// <summary>Reads one whole expression (<c>hp + 1</c>); anything left over is an error.</summary>
    public static Expr ParseExpression(string text)
    {
        var p = new Parser(Lexer.Tokenize(text.Trim()));
        if (p.AtLineEnd) throw new ScriptError("Write a value here.", 0);
        var e = p.ParseExpr();
        p.ExpectEnd();
        return e;
    }

    /// <summary>Reads values separated by commas or spaces (<c>10, 0</c>); empty text = no values.</summary>
    public static List<Expr> ParseExpressionList(string text)
    {
        var p = new Parser(Lexer.Tokenize(text.Trim()));
        var list = p.ParseArgs();
        p.ExpectEnd();
        return list;
    }

    void ExpectEnd()
    {
        EndLine();
        SkipNewlines();
        if (Peek().Kind != Tok.Eof) throw new ScriptError($"I didn't expect {Peek()} here.", Peek().Line);
    }

    Token Peek(int ahead = 0) => _t[Math.Min(_p + ahead, _t.Count - 1)];
    Token Next() => _t[Math.Min(_p++, _t.Count - 1)];
    bool IsOp(string op, int ahead = 0) => Peek(ahead).Kind == Tok.Op && Peek(ahead).Text == op;
    bool IsWord(string w, int ahead = 0) => Peek(ahead).Kind == Tok.Ident && Peek(ahead).Text == w;
    bool AtLineEnd => Peek().Kind is Tok.Newline or Tok.Eof or Tok.Dedent;

    void SkipNewlines() { while (Peek().Kind == Tok.Newline) _p++; }

    Token Expect(Tok kind, string what)
    {
        var t = Peek();
        if (t.Kind != kind) throw new ScriptError($"Expected {what} but found {t}.", t.Line);
        return Next();
    }

    void ExpectOp(string op)
    {
        if (!IsOp(op)) throw new ScriptError($"Expected '{op}' but found {Peek()}.", Peek().Line);
        _p++;
    }

    void EndLine()
    {
        if (Peek().Kind == Tok.Newline) { _p++; return; }
        if (Peek().Kind is Tok.Eof or Tok.Dedent) return;
        throw new ScriptError($"I didn't expect {Peek()} here; the line should end.", Peek().Line);
    }

    List<MechanismDef> ParseFile()
    {
        var list = new List<MechanismDef>();
        SkipNewlines();
        while (Peek().Kind != Tok.Eof)
        {
            if (!IsWord("mechanism") && !IsWord("system"))
                throw new ScriptError($"A file starts with 'mechanism Name' (or 'system Name'), but found {Peek()}.", Peek().Line);
            var comments = TakeComments(Peek().Line);
            list.Add(ParseMechanism() with { Comments = comments });
            SkipNewlines();
        }
        // The comments after the last line of code stay at the end of the file.
        if (list.Count > 0 && _nextComment < _comments.Count)
            list[^1] = list[^1] with { EndComments = _comments.Skip(_nextComment).Select(c => c.Text).ToList() };
        var dup = list.GroupBy(m => m.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new ScriptError($"There are two mechanisms called '{dup.Key}'.", dup.Last().Line);
        return list;
    }

    MechanismDef ParseMechanism()
    {
        bool isSystem = IsWord("system"); // 'system Inventory': a mechanism that brings others with it (Systems.cs)
        int line = Next().Line; // 'mechanism' / 'system'
        string name = Expect(Tok.Ident, isSystem ? "the system's name" : "the mechanism's name").Text;
        // 'mechanism RedSlime from Slime': a variant (see Variants.cs).
        string? baseName = null;
        if (IsWord("from"))
        {
            _p++;
            var b = Peek();
            if (b.Kind != Tok.Ident) throw new ScriptError($"After 'from' write the mechanism '{name}' is a variant of, like 'mechanism RedSlime from Slime' — found {b}.", b.Line);
            _p++;
            if (string.Equals(b.Text, name, StringComparison.OrdinalIgnoreCase))
                throw new ScriptError($"'{name}' can't come from itself: write the mechanism it's a variant of after 'from'.", b.Line);
            baseName = b.Text;
        }
        EndLine();
        var ps = new List<ParamDef>();
        var uses = new List<UsesDef>();
        var whens = new List<WhenDef>();
        var procs = new List<ProcDef>();
        var includes = new List<UsesDef>();
        if (Peek().Kind != Tok.Indent)
            return new MechanismDef(name, ps, uses, whens, line) { Base = baseName, IsSystem = isSystem };
        _p++;
        while (true)
        {
            SkipNewlines();
            if (Peek().Kind == Tok.Dedent) { _p++; break; }
            if (Peek().Kind == Tok.Eof) break;
            var comments = TakeComments(Peek().Line);
            if (IsWord("param")) ps.Add(ParseParam() with { Comments = comments });
            else if (IsWord("uses")) uses.Add(ParseUses() with { Comments = comments });
            else if (IsWord("includes"))
            {
                if (!isSystem) throw new ScriptError($"'includes' is for systems: start the file with 'system {name}' instead of 'mechanism {name}' (or use 'uses').", Peek().Line);
                includes.Add(ParseUses() with { Comments = comments });
            }
            else if (IsWord("when")) whens.Add(ParseWhen() with { Comments = comments });
            else if (IsWord("to")) procs.Add(ParseProc() with { Comments = comments });
            else throw new ScriptError($"Inside a mechanism, lines start with 'param', 'uses' or 'when' (or 'to' to make a command) — found {Peek()}.", Peek().Line);
        }
        var dupParam = ps.GroupBy(x => x.Name).FirstOrDefault(g => g.Count() > 1);
        if (dupParam != null) throw new ScriptError($"The param '{dupParam.Key}' is declared twice.", dupParam.Last().Line);
        var dupProc = procs.GroupBy(x => x.Name).FirstOrDefault(g => g.Count() > 1);
        if (dupProc != null) throw new ScriptError($"There are two commands called '{dupProc.Key}' in '{name}'.", dupProc.Last().Line);
        var dupInc = includes.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupInc != null) throw new ScriptError($"'{name}' includes '{dupInc.Key}' twice: put its settings on one line.", dupInc.Last().Line);
        return new MechanismDef(name, ps, uses, whens, line) { Procs = procs, Base = baseName, IsSystem = isSystem, Includes = includes };
    }

    /// <summary>Words a user command can't be called: Builder Script words, built-in commands and functions.</summary>
    public static readonly HashSet<string> Keywords =
    [
        "mechanism", "param", "uses", "when", "if", "else", "repeat", "forever", "wait", "emit", "tell", "stop",
        "and", "or", "not", "true", "false", "to", "together", "run", "for", "while", "return", "system", "includes"
    ];

    /// <summary>Why a user command can't have this name, or null when it can.</summary>
    public static string? CheckCommandName(string name)
    {
        if (Keywords.Contains(name)) return $"'{name}' is a Builder Script word; pick another name for the command.";
        if (World.BuiltInCommands.Contains(name)) return $"'{name}' is already a command; pick another name (like 'my_{name}').";
        if (World.BuiltInFunctions.Contains(name)) return $"'{name}' is already a function; pick another name (like 'my_{name}').";
        return null;
    }

    bool _inProc;

    ProcDef ParseProc()
    {
        int line = Next().Line; // 'to'
        var nameTok = Peek();
        if (nameTok.Kind != Tok.Ident) throw new ScriptError($"After 'to' write the new command's name, like 'to attack target' — found {nameTok}.", nameTok.Line);
        _p++;
        if (CheckCommandName(nameTok.Text) is { } bad) throw new ScriptError(bad, nameTok.Line);
        var ps = new List<string>();
        while (!AtLineEnd && !IsOp(":"))
        {
            if (IsOp(",")) { _p++; continue; }
            var t = Peek();
            if (t.Kind != Tok.Ident || Keywords.Contains(t.Text))
                throw new ScriptError($"The inputs of '{nameTok.Text}' must be names separated by commas, like 'to attack target, power' — found {t}.", t.Line);
            if (ps.Contains(t.Text)) throw new ScriptError($"'{nameTok.Text}' has two inputs called '{t.Text}'.", t.Line);
            ps.Add(t.Text);
            _p++;
        }
        bool was = _inProc;
        _inProc = true;
        try { return new ProcDef(nameTok.Text, ps, ParseBlockOrInline(), line); }
        finally { _inProc = was; }
    }

    /// <summary>True when the token after this one is an assignment: then a word like 'run' is just a variable.</summary>
    bool AssignNext => Peek(1).Kind == Tok.Op && Peek(1).Text is "=" or "+=" or "-=" or "*=" or "/=";

    ParamDef ParseParam()
    {
        int line = Next().Line;
        string name = Expect(Tok.Ident, "the param's name").Text;
        string type = "any";
        List<string>? options = null;
        Expr? def = null;
        if (IsOp(":")) { _p++; (type, options) = ParseParamType(); }
        if (IsOp("=")) { _p++; def = ParseExpr(); }
        EndLine();
        return new ParamDef(name, type, def, line) { Options = options };
    }

    /// <summary>
    /// A param's type after the colon: a name (<c>number</c>, <c>Object</c>, <c>Path</c>...), <c>List of number</c>,
    /// or <c>Choice "easy", "normal", "hard"</c> (the options in quotes).
    /// </summary>
    (string Type, List<string>? Options) ParseParamType()
    {
        string type = Expect(Tok.Ident, "a type (number, text, Sound, Object, Path, List, Color, Key, Choice...)").Text;
        if (string.Equals(type, "List", StringComparison.OrdinalIgnoreCase) && IsWord("of"))
        {
            _p++;
            type += " of " + Expect(Tok.Ident, "the type of the items, like 'List of number' or 'List of Object'").Text;
        }
        if (!string.Equals(type, "Choice", StringComparison.OrdinalIgnoreCase)) return (type, null);
        var options = new List<string>();
        while (Peek().Kind == Tok.String)
        {
            options.Add(Next().Text);
            if (IsOp(",")) _p++;
        }
        if (options.Count == 0)
            throw new ScriptError("A Choice needs its options in quotes, like: param level: Choice \"easy\", \"normal\", \"hard\"", Peek().Line);
        return (type, options);
    }

    /// <summary>Reads a param type written on its own (the blocks' type field): <c>List of number</c>, <c>Choice "a", "b"</c>.</summary>
    public static (string Type, List<string>? Options) ParseTypeText(string text)
    {
        var p = new Parser(Lexer.Tokenize(text.Trim()));
        var r = p.ParseParamType();
        p.ExpectEnd();
        return r;
    }

    UsesDef ParseUses()
    {
        int line = Next().Line;
        string name = Expect(Tok.Ident, "the name of the mechanism to use").Text;
        Expr? positional = null;
        var args = new Dictionary<string, Expr>();
        if (!AtLineEnd && Peek().Kind != Tok.Ident) positional = ParseExpr();
        while (!AtLineEnd)
        {
            if (IsOp(",")) { _p++; continue; }
            var key = Expect(Tok.Ident, "a param name");
            if (IsOp(":") || IsOp("=")) _p++;
            args[key.Text] = ParseExpr();
        }
        EndLine();
        return new UsesDef(name, positional, args, line);
    }

    WhenDef ParseWhen()
    {
        int line = Next().Line;
        var ev = ParseEvent();
        var body = ParseBlockOrInline();
        return new WhenDef(ev, body, line);
    }

    EventSpec ParseEvent()
    {
        var t = Peek();
        if (t.Kind != Tok.Ident) throw new ScriptError($"After 'when' write what happens (start, tick, touched by...) — found {t}.", t.Line);
        string w = t.Text;
        _p++;
        switch (w)
        {
            case "start": return new EventSpec(EventKind.Start);
            case "tick": return new EventSpec(EventKind.Tick);
            case "tool" when Peek().Kind == Tok.Ident: return ParseToolEvent(); // EditorTools.Parse.cs
            case "touched":
            {
                if (IsWord("by")) _p++;
                if (Peek().Kind == Tok.String) return new EventSpec(EventKind.Touched, Filter: Next().Text);
                if (Peek().Kind == Tok.Ident) return new EventSpec(EventKind.Touched, Bind: Next().Text);
                return new EventSpec(EventKind.Touched);
            }
            case "told":
            {
                string msg = Expect(Tok.String, "the message in quotes, like \"damage\"").Text;
                string? bind = Peek().Kind == Tok.Ident ? Next().Text : null;
                return new EventSpec(EventKind.Told, Filter: msg, Bind: bind);
            }
            case "key":
            {
                string key = Peek().Kind == Tok.String ? Next().Text : Expect(Tok.Ident, "a key, like \"space\"").Text;
                return new EventSpec(EventKind.Key, Filter: key.ToLowerInvariant());
            }
            default:
                if (TryParseGameEvent(w) is { } game) return game; // GameEvents.Parse.cs
                if (IsOp("."))
                {
                    _p++;
                    string sig = Expect(Tok.Ident, "the signal's name").Text;
                    return new EventSpec(EventKind.Signal, Source: w, Name: sig);
                }
                return new EventSpec(EventKind.Signal, Name: w);
        }
    }

    /// <summary>
    /// Either "<c>: statement</c>" on the same line, or a newline and an indented block.
    /// A colon with nothing after it and nothing indented under it (<c>when start:</c>) is an empty block.
    /// </summary>
    List<Stmt> ParseBlockOrInline()
    {
        bool colon = false;
        if (IsOp(":"))
        {
            _p++;
            colon = true;
            if (!AtLineEnd) return [ParseSimpleStmt(endLine: true)];
        }
        int line = Peek().Line;
        EndLine();
        SkipNewlines();
        if (Peek().Kind != Tok.Indent && colon) return [];
        if (Peek().Kind != Tok.Indent) throw new ScriptError("The lines inside this block must be indented.", line);
        _p++;
        var list = new List<Stmt>();
        while (true)
        {
            SkipNewlines();
            if (Peek().Kind == Tok.Dedent) { _p++; break; }
            if (Peek().Kind == Tok.Eof) break;
            var comments = TakeComments(Peek().Line);
            var s = ParseStmt();
            list.Add(comments == null ? s : s with { Comments = comments });
        }
        return list;
    }

    Stmt ParseStmt() => Deeper(ParseStmtHere);

    Stmt ParseStmtHere()
    {
        var t = Peek();
        if (t.Kind == Tok.Ident && !AssignNext)
        {
            switch (t.Text)
            {
                case "together":
                    _p++;
                    return new TogetherStmt(ParseBlockOrInline(), t.Line);
                case "run":
                    _p++;
                    if (IsOp(":") || AtLineEnd) return new RunStmt(ParseBlockOrInline(), t.Line);
                    return new RunStmt([ParseStmt()], t.Line);
                case "while":
                {
                    _p++;
                    if (AtLineEnd || IsOp(":")) throw new ScriptError("After 'while' write a condition, like 'while hp > 0'.", t.Line);
                    var cond = ParseExpr();
                    return new WhileStmt(cond, ParseBlockOrInline(), t.Line);
                }
                case "for":
                {
                    _p++;
                    if (IsWord("each")) _p++;
                    var v = Peek();
                    if (v.Kind != Tok.Ident || Keywords.Contains(v.Text))
                        throw new ScriptError($"Write it like 'for each enemy in all(\"enemy\")' — found {v} after 'for each'.", v.Line);
                    _p++;
                    if (!IsWord("in")) throw new ScriptError($"Write it like 'for each {v.Text} in list' — the word 'in' is missing.", v.Line);
                    _p++;
                    if (AtLineEnd || IsOp(":")) throw new ScriptError($"After 'in' write the list to go through, like range(1, 10) or all(\"coin\").", v.Line);
                    var list = ParseExpr();
                    return new ForEachStmt(v.Text, list, ParseBlockOrInline(), t.Line);
                }
                case "if": return ParseIf();
                case "repeat":
                {
                    _p++;
                    Expr? count = null;
                    if (IsWord("forever")) _p++;
                    else count = ParseExpr();
                    if (IsWord("times")) _p++;
                    return new RepeatStmt(count, ParseBlockOrInline(), t.Line);
                }
            }
        }
        return ParseSimpleStmt(endLine: true);
    }

    Stmt ParseIf()
    {
        int line = Next().Line;
        var cond = ParseExpr();
        var then = ParseBlockOrInline();
        List<Stmt>? els = null;
        SkipNewlines();
        if (IsWord("else"))
        {
            // Comments above an 'else if' stay above it; above a plain 'else' they go to its first line.
            var comments = IsWord("if", 1) ? TakeComments(Peek().Line) : null;
            _p++;
            els = IsWord("if") ? [Deeper(ParseIf) with { Comments = comments }] : ParseBlockOrInline();
        }
        return new IfStmt(cond, then, els, line);
    }

    Stmt ParseSimpleStmt(bool endLine)
    {
        var t = Peek();
        if (t.Kind != Tok.Ident) throw new ScriptError($"A line can't start with {t}.", t.Line);
        Stmt s;
        switch (AssignNext ? "" : t.Text)
        {
            case "when": throw new ScriptError("'when' goes directly inside the mechanism, not inside another block.", t.Line);
            case "param":
            case "uses": throw new ScriptError($"'{t.Text}' goes at the top of the mechanism, not inside a 'when'.", t.Line);
            case "to": throw new ScriptError("'to' makes a new command: it goes directly inside the mechanism, not inside a 'when'.", t.Line);
            // A block can't follow another line's ':' (that one-line form holds one simple line). Without this,
            // 'if a: repeat 3' read 'repeat' as a command: no error in the editor, "no command called 'repeat'" when played.
            case "together" or "while" or "for" or "if" or "repeat":
                throw new ScriptError($"'{t.Text}' needs its own lines: put it on a new line with its block indented under it.", t.Line);
            case "else": throw new ScriptError("'else' goes right after the lines of an 'if', lined up with that 'if'.", t.Line);
            case "wait": _p++; s = ParseWait(t.Line); break;
            case "stop" when Peek(1).Kind == Tok.Ident && Peek(1).Text is "sound" or "sounds": s = ParseCommand(); break; // stop sound "rain.wav" (World.Audio.cs)
            case "stop": _p++; s = new StopStmt(t.Line); break;
            case "return":
                _p++;
                if (!_inProc) throw new ScriptError("'return' only works inside a command made with 'to' (it ends the command). To end a 'when', use 'stop'.", t.Line);
                s = new ReturnStmt(AtLineEnd ? null : ParseExpr(), t.Line);
                break;
            case "run":
                _p++;
                if (AtLineEnd) throw new ScriptError("After 'run' write what to start in the background, like 'run patrol'.", t.Line);
                s = new RunStmt([Deeper(() => ParseSimpleStmt(endLine: false))], t.Line);
                break;
            case "emit":
            {
                _p++;
                string sig = Expect(Tok.Ident, "the signal's name").Text;
                s = new EmitStmt(sig, ParseArgs(), t.Line);
                break;
            }
            case "tell":
            {
                _p++;
                var target = ParseExpr();
                var msg = ParseExpr();
                s = new TellStmt(target, msg, ParseArgs(), t.Line);
                break;
            }
            default:
                s = TryAssign() ?? ParseCommand();
                break;
        }
        if (endLine) EndLine();
        return s;
    }

    /// <summary>After 'wait': seconds, <c>until cond</c>, <c>for key "space"</c> or <c>for Health.died</c>.</summary>
    Stmt ParseWait(int line)
    {
        if (AtLineEnd) throw new ScriptError("After 'wait' write how long (wait 1), or 'wait until ...' / 'wait for ...'.", line);
        if (IsWord("until") && Peek(1).Kind is not (Tok.Newline or Tok.Eof or Tok.Dedent))
        {
            _p++;
            return new WaitUntilStmt(ParseExpr(), line);
        }
        if (IsWord("for") && Peek(1).Kind is not (Tok.Newline or Tok.Eof or Tok.Dedent))
        {
            _p++;
            // 'wait for key.pressed' is a signal; 'wait for key (k)' / 'wait for key -1' wait for a key (any other symbol
            // after 'key' used to send them down the signal path, an error, though the printer writes them that way).
            if (IsWord("key") && Peek(1).Kind is not (Tok.Newline or Tok.Eof or Tok.Dedent) && !IsOp(".", 1))
            {
                _p++;
                return new WaitKeyStmt(ParseExpr(), line);
            }
            var first = Peek();
            if (first.Kind != Tok.Ident)
                throw new ScriptError($"After 'wait for' write a signal (wait for died, wait for Health.died) or a key (wait for key \"space\") — found {first}.", first.Line);
            var path = new List<string> { Next().Text };
            while (IsOp(".") && Peek(1).Kind == Tok.Ident) { _p++; path.Add(Next().Text); }
            return new WaitSignalStmt(path.ToArray(), line);
        }
        var s = new WaitStmt(ParseExpr(), line);
        if (IsWord("seconds") || IsWord("second")) _p++;
        return s;
    }

    Stmt? TryAssign()
    {
        int save = _p;
        var path = new List<string> { Next().Text };
        while (IsOp(".") && Peek(1).Kind == Tok.Ident) { _p++; path.Add(Next().Text); }
        if (Peek().Kind == Tok.Op && Peek().Text is "=" or "+=" or "-=" or "*=" or "/=")
        {
            var op = Next();
            return new AssignStmt(path.ToArray(), op.Text, ParseExpr(), op.Line);
        }
        _p = save;
        return null;
    }

    /// <summary>Words that start a named value in a command: <c>move Boss to 400, 300 in 2 seconds</c>.</summary>
    public static readonly HashSet<string> Connectors = ["to", "in", "at", "by", "with", "from", "ease", "over", "as", "then"];

    Stmt ParseCommand()
    {
        var name = Next();
        // 'Health.heal_all 3': a user command of one particular mechanism.
        if (IsOp(".") && Peek(1).Kind == Tok.Ident)
        {
            _p++;
            name = name with { Text = name.Text + "." + Next().Text };
        }
        var args = new List<Expr>();
        Dictionary<string, List<Expr>>? named = null;
        List<Expr> target = args;
        while (!AtLineEnd)
        {
            if (IsOp(",")) { _p++; continue; }
            if (Peek().Kind == Tok.Ident && Connectors.Contains(Peek().Text) && Peek(1).Kind is not (Tok.Newline or Tok.Eof or Tok.Dedent))
            {
                named ??= new();
                target = named[Next().Text] = [];
                continue;
            }
            // "in 2 seconds": the unit word after a named number is just for reading.
            if (target != args && target.Count > 0 && (IsWord("seconds") || IsWord("second"))) { _p++; continue; }
            target.Add(ParseExpr());
        }
        return new CommandStmt(name.Text, args, name.Line, named);
    }

    List<Expr> ParseArgs()
    {
        var args = new List<Expr>();
        while (!AtLineEnd)
        {
            if (IsOp(",")) { _p++; continue; }
            args.Add(ParseExpr());
        }
        return args;
    }

    // ---- Expressions (lowest to highest precedence) ----
    public Expr ParseExpr()
    {
        // A whole value (not one inside another) counts its steps from zero.
        if (_valueDepth++ == 0) _steps = 0;
        try { return Deeper(ParseOr); }
        finally { _valueDepth--; }
    }

    // ---- How deep code may nest ----
    // Values inside values (parentheses, lists, '-' / 'not' in a row), blocks inside blocks and long chains of one
    // operator (a + b + c..., which nest to the left) all make trees that the parser, the printer, the blocks and the game
    // walk by recursion. Thousands of levels ran the stack out and killed the whole editor; now they are an error.

    /// <summary>The deepest nesting accepted (nobody writes this much; generated or pasted text can).</summary>
    public const int MaxDepth = 200;
    /// <summary>
    /// The most steps (operators, items, names after a dot) one whole value may have: a chain like <c>1 + 1 + ... + 1</c> nests
    /// to the left, as deep as it is long, and chains at every precedence level can stack up along one side of the tree.
    /// </summary>
    public const int MaxSteps = 500;
    int _steps, _valueDepth;
    int _depth;

    T Deeper<T>(Func<T> parse)
    {
        if (++_depth > MaxDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw TooDeep(Peek().Line);
        try { return parse(); }
        finally { _depth--; }
    }

    /// <summary>One more step in the value being read (<c>a + b</c>, <c>l[0]</c>, <c>find("x").y</c>).</summary>
    void Longer(int line)
    {
        if (++_steps > MaxSteps) throw TooDeep(line);
    }

    static ScriptError TooDeep(int line) => new(
        $"This goes too deep: more than {MaxDepth} values or blocks inside each other, or one value of more than {MaxSteps} steps. Split it into smaller steps with variables.", line);

    Expr ParseOr()
    {
        var e = ParseAnd();
        while (IsWord("or")) { int l = Next().Line; Longer(l); e = new BinaryExpr("or", e, ParseAnd(), l); }
        return e;
    }

    Expr ParseAnd()
    {
        var e = ParseNot();
        while (IsWord("and")) { int l = Next().Line; Longer(l); e = new BinaryExpr("and", e, ParseNot(), l); }
        return e;
    }

    Expr ParseNot()
    {
        if (IsWord("not")) { int l = Next().Line; return new UnaryExpr("not", Deeper(ParseNot), l); }
        return ParseCompare();
    }

    Expr ParseCompare()
    {
        var e = ParseAdd();
        while (Peek().Kind == Tok.Op && Peek().Text is "==" or "!=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            var op = Next();
            Longer(op.Line);
            string o = op.Text == "<>" ? "!=" : op.Text;
            e = new BinaryExpr(o, e, ParseAdd(), op.Line);
        }
        return e;
    }

    Expr ParseAdd()
    {
        var e = ParseMul();
        while (Peek().Kind == Tok.Op && Peek().Text is "+" or "-")
        {
            var op = Next();
            Longer(op.Line);
            e = new BinaryExpr(op.Text, e, ParseMul(), op.Line);
        }
        return e;
    }

    Expr ParseMul()
    {
        var e = ParseUnary();
        while (Peek().Kind == Tok.Op && Peek().Text is "*" or "/" or "%")
        {
            var op = Next();
            Longer(op.Line);
            e = new BinaryExpr(op.Text, e, ParseUnary(), op.Line);
        }
        return e;
    }

    Expr ParseUnary()
    {
        if (IsOp("-")) { int l = Next().Line; return new UnaryExpr("-", Deeper(ParseUnary), l); }
        return ParsePrimary();
    }

    Expr ParsePrimary()
    {
        var e = ParseAtom(out bool indexable);
        // items[0], all("coin")[0], [1, 2][1]: an item of a list (not after a number or a text).
        // find("Turns").TurnManager.turn, hand[0].CardView.card: names after a call or an item (MemberExpr).
        while (indexable)
        {
            if (IsOp("["))
            {
                int l = Next().Line;
                Longer(l);
                if (IsOp("]")) throw new ScriptError("Write which item between the brackets, like items[0] (0 is the first).", l);
                var index = ParseExpr();
                if (!IsOp("]")) throw new ScriptError($"The '[' is missing its ']' (found {Peek()}).", l);
                _p++;
                e = new IndexExpr(e, index, l);
            }
            else if (e is CallExpr or IndexExpr or MemberExpr && IsOp(".") && Peek(1).Kind == Tok.Ident)
            {
                _p++;
                Longer(e.Line);
                e = new MemberExpr(e, Next().Text, e.Line);
            }
            else break;
        }
        return e;
    }

    Expr ParseAtom(out bool indexable)
    {
        indexable = true;
        var t = Next();
        switch (t.Kind)
        {
            case Tok.Number: indexable = false; return new NumberExpr(t.Number, t.Line);
            case Tok.String: indexable = false; return new StringExpr(t.Text, t.Line);
            case Tok.Op when t.Text == "(":
            {
                var e = ParseExpr();
                ExpectOp(")");
                return e;
            }
            case Tok.Op when t.Text == "[":
            {
                var items = new List<Expr>();
                while (!IsOp("]"))
                {
                    if (Peek().Kind == Tok.Eof) throw new ScriptError("A list '[' is missing its ']'.", t.Line);
                    if (IsOp(",")) { _p++; continue; }
                    items.Add(ParseExpr());
                }
                _p++;
                return new ListExpr(items, t.Line);
            }
            case Tok.Op when t.Text == "{": return ParseRecord(t.Line); // World.Records.cs
            case Tok.Ident:
            {
                if (t.Text == "true") { indexable = false; return new BoolExpr(true, t.Line); }
                if (t.Text == "false") { indexable = false; return new BoolExpr(false, t.Line); }
                // After an operator, 'a == not b' read 'not' as a name and 'b' as one more value of a command.
                if (t.Text == "not") throw new ScriptError("Put 'not ...' in parentheses here, like: a == (not b).", t.Line);
                if (IsOp("(")) return ParseCall(t.Text, t.Line);
                var path = new List<string> { t.Text };
                while (IsOp(".") && Peek(1).Kind == Tok.Ident) { _p++; path.Add(Next().Text); }
                // Health.heal_amount(2): a user command of one mechanism, used as a value.
                if (path.Count == 2 && IsOp("(")) return ParseCall(path[0] + "." + path[1], t.Line);
                return new NameExpr(path.ToArray(), t.Line);
            }
        }
        throw new ScriptError($"Expected a value (a number, a text or a name) but found {t}.", t.Line);
    }

    CallExpr ParseCall(string name, int line)
    {
        _p++; // '('
        var args = new List<Expr>();
        while (!IsOp(")"))
        {
            if (Peek().Kind == Tok.Eof) throw new ScriptError($"'{name}(' is missing its ')'.", line);
            if (IsOp(",")) { _p++; continue; }
            args.Add(ParseExpr());
        }
        _p++;
        return new CallExpr(name, args, line);
    }
}
