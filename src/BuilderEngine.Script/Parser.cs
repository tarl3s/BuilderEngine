namespace BuilderEngine.Script;

/// <summary>Reads Builder Script tokens into mechanisms. One file may hold several mechanisms.</summary>
public sealed class Parser
{
    readonly List<Token> _t;
    int _p;

    Parser(List<Token> tokens) { _t = tokens; }

    public static List<MechanismDef> Parse(string source) => new Parser(Lexer.Tokenize(source)).ParseFile();

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
            if (!IsWord("mechanism"))
                throw new ScriptError($"A file starts with 'mechanism Name', but found {Peek()}.", Peek().Line);
            list.Add(ParseMechanism());
            SkipNewlines();
        }
        var dup = list.GroupBy(m => m.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new ScriptError($"There are two mechanisms called '{dup.Key}'.", dup.Last().Line);
        return list;
    }

    MechanismDef ParseMechanism()
    {
        int line = Next().Line; // 'mechanism'
        string name = Expect(Tok.Ident, "the mechanism's name").Text;
        EndLine();
        var ps = new List<ParamDef>();
        var uses = new List<UsesDef>();
        var whens = new List<WhenDef>();
        if (Peek().Kind != Tok.Indent)
            return new MechanismDef(name, ps, uses, whens, line);
        _p++;
        while (true)
        {
            SkipNewlines();
            if (Peek().Kind == Tok.Dedent) { _p++; break; }
            if (Peek().Kind == Tok.Eof) break;
            if (IsWord("param")) ps.Add(ParseParam());
            else if (IsWord("uses")) uses.Add(ParseUses());
            else if (IsWord("when")) whens.Add(ParseWhen());
            else throw new ScriptError($"Inside a mechanism, lines start with 'param', 'uses' or 'when' — found {Peek()}.", Peek().Line);
        }
        var dupParam = ps.GroupBy(x => x.Name).FirstOrDefault(g => g.Count() > 1);
        if (dupParam != null) throw new ScriptError($"The param '{dupParam.Key}' is declared twice.", dupParam.Last().Line);
        return new MechanismDef(name, ps, uses, whens, line);
    }

    ParamDef ParseParam()
    {
        int line = Next().Line;
        string name = Expect(Tok.Ident, "the param's name").Text;
        string type = "any";
        Expr? def = null;
        if (IsOp(":")) { _p++; type = Expect(Tok.Ident, "a type (number, text, Sound...)").Text; }
        if (IsOp("=")) { _p++; def = ParseExpr(); }
        EndLine();
        return new ParamDef(name, type, def, line);
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
                if (IsOp("."))
                {
                    _p++;
                    string sig = Expect(Tok.Ident, "the signal's name").Text;
                    return new EventSpec(EventKind.Signal, Source: w, Name: sig);
                }
                return new EventSpec(EventKind.Signal, Name: w);
        }
    }

    /// <summary>Either "<c>: statement</c>" on the same line, or a newline and an indented block.</summary>
    List<Stmt> ParseBlockOrInline()
    {
        if (IsOp(":"))
        {
            _p++;
            if (!AtLineEnd) return [ParseSimpleStmt(endLine: true)];
        }
        int line = Peek().Line;
        EndLine();
        SkipNewlines();
        if (Peek().Kind != Tok.Indent) throw new ScriptError("The lines inside this block must be indented.", line);
        _p++;
        var list = new List<Stmt>();
        while (true)
        {
            SkipNewlines();
            if (Peek().Kind == Tok.Dedent) { _p++; break; }
            if (Peek().Kind == Tok.Eof) break;
            list.Add(ParseStmt());
        }
        return list;
    }

    Stmt ParseStmt()
    {
        var t = Peek();
        if (t.Kind == Tok.Ident)
        {
            switch (t.Text)
            {
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
            _p++;
            els = IsWord("if") ? [ParseIf()] : ParseBlockOrInline();
        }
        return new IfStmt(cond, then, els, line);
    }

    Stmt ParseSimpleStmt(bool endLine)
    {
        var t = Peek();
        if (t.Kind != Tok.Ident) throw new ScriptError($"A line can't start with {t}.", t.Line);
        Stmt s;
        switch (t.Text)
        {
            case "when": throw new ScriptError("'when' goes directly inside the mechanism, not inside another block.", t.Line);
            case "param":
            case "uses": throw new ScriptError($"'{t.Text}' goes at the top of the mechanism, not inside a 'when'.", t.Line);
            case "wait": _p++; s = new WaitStmt(ParseExpr(), t.Line); if (IsWord("seconds") || IsWord("second")) _p++; break;
            case "stop": _p++; s = new StopStmt(t.Line); break;
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
                s = TryAssign() ?? new CommandStmt(Next().Text, ParseArgs(), t.Line);
                break;
        }
        if (endLine) EndLine();
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
    public Expr ParseExpr() => ParseOr();

    Expr ParseOr()
    {
        var e = ParseAnd();
        while (IsWord("or")) { int l = Next().Line; e = new BinaryExpr("or", e, ParseAnd(), l); }
        return e;
    }

    Expr ParseAnd()
    {
        var e = ParseNot();
        while (IsWord("and")) { int l = Next().Line; e = new BinaryExpr("and", e, ParseNot(), l); }
        return e;
    }

    Expr ParseNot()
    {
        if (IsWord("not")) { int l = Next().Line; return new UnaryExpr("not", ParseNot(), l); }
        return ParseCompare();
    }

    Expr ParseCompare()
    {
        var e = ParseAdd();
        while (Peek().Kind == Tok.Op && Peek().Text is "==" or "!=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            var op = Next();
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
            e = new BinaryExpr(op.Text, e, ParseUnary(), op.Line);
        }
        return e;
    }

    Expr ParseUnary()
    {
        if (IsOp("-")) { int l = Next().Line; return new UnaryExpr("-", ParseUnary(), l); }
        return ParsePrimary();
    }

    Expr ParsePrimary()
    {
        var t = Next();
        switch (t.Kind)
        {
            case Tok.Number: return new NumberExpr(t.Number, t.Line);
            case Tok.String: return new StringExpr(t.Text, t.Line);
            case Tok.Op when t.Text == "(":
            {
                var e = ParseExpr();
                ExpectOp(")");
                return e;
            }
            case Tok.Ident:
            {
                if (t.Text == "true") return new BoolExpr(true, t.Line);
                if (t.Text == "false") return new BoolExpr(false, t.Line);
                if (IsOp("("))
                {
                    _p++;
                    var args = new List<Expr>();
                    while (!IsOp(")"))
                    {
                        if (Peek().Kind == Tok.Eof) throw new ScriptError($"'{t.Text}(' is missing its ')'.", t.Line);
                        if (IsOp(",")) { _p++; continue; }
                        args.Add(ParseExpr());
                    }
                    _p++;
                    return new CallExpr(t.Text, args, t.Line);
                }
                var path = new List<string> { t.Text };
                while (IsOp(".") && Peek(1).Kind == Tok.Ident) { _p++; path.Add(Next().Text); }
                return new NameExpr(path.ToArray(), t.Line);
            }
        }
        throw new ScriptError($"Expected a value (a number, a text or a name) but found {t}.", t.Line);
    }
}
