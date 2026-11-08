using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BuilderEngine.Script;

/// <summary>
/// Audit of the language front-end and the blocks ⇄ code conversion (docs/audit/language-and-blocks.md): lexer and
/// parser edge cases, printing that keeps comments, round-trips of every Builder Script source in the repository, a
/// random-program fuzzer and a block-edit fuzzer. Called from Program.cs.
/// </summary>
static class AuditLangTests
{
    static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected <{expected}> but got <{actual}>");
    }

    static void True(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    static void Throws(string fragment, Action body)
    {
        try { body(); }
        catch (ScriptError e)
        {
            if (!e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"error was \"{e.Message}\", expected it to mention \"{fragment}\"");
            return;
        }
        throw new Exception($"expected an error mentioning \"{fragment}\"");
    }

    static string M(string body) => "mechanism T\n    when start\n" + string.Join("", body.Split('\n').Select(l => "        " + l + "\n"));

    // ---------------- A complete dump of the tree (everything but line numbers) ----------------

    static string D(Expr? e) => e switch
    {
        null => "_",
        NumberExpr n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        StringExpr s => "'" + s.Value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n") + "'",
        BoolExpr b => b.Value ? "T" : "F",
        NameExpr n => "$" + n.Full,
        UnaryExpr u => $"({u.Op} {D(u.Operand)})",
        BinaryExpr b => $"({b.Op} {D(b.Left)} {D(b.Right)})",
        CallExpr c => $"{c.Name}[{L(c.Args)}]",
        ListExpr l => $"<{L(l.Items)}>",
        IndexExpr x => $"(at {D(x.Target)} {D(x.Index)})",
        RecordExpr r => "{" + string.Join(" ", r.Fields.Select(f => f.Key + ":" + D(f.Value))) + "}",
        MemberExpr m => $"(. {D(m.Target)} {m.Name})",
        _ => throw new Exception("unknown expression " + e.GetType().Name)
    };

    static string L(IEnumerable<Expr> list) => string.Join(" ", list.Select(D));

    static string C(Commented c) => C(c.Comments);

    static string C(CodeComments? c) => c == null ? "" : $"«{string.Join("|", c.Before)}»«{c.After}»";

    static CodeComments? Merge(CodeComments? a, CodeComments? b) =>
        a == null ? b : b == null ? a : new CodeComments([.. a.Before, .. b.Before], a.After == null ? b.After : b.After == null ? a.After : a.After + " " + b.After);

    static string Body(List<Stmt>? list) => list == null ? "null" : "{" + string.Join("; ", list.Select(D)) + "}";

    static string D(Stmt s)
    {
        // A 'run' and its one statement are one line of code: comments on either of them are the same in the code.
        if (s is RunStmt { Body.Count: 1 })
        {
            CodeComments? merged = null;
            int runs = 0;
            while (s is RunStmt { Body.Count: 1 } run) { merged = Merge(merged, run.Comments); s = run.Body[0]; runs++; }
            return C(Merge(merged, s.Comments)) + string.Concat(Enumerable.Repeat("run1 ", runs)) + D(s with { Comments = null });
        }
        return C(s) + Plain(s);
    }

    static string Plain(Stmt s) => s switch
    {
        AssignStmt a => $"{string.Join('.', a.Target)} {a.Op} {D(a.Value)}",
        IfStmt i => $"if {D(i.Cond)} {Body(i.Then)} else {Body(i.Else)}",
        RepeatStmt r => $"repeat {D(r.Count)} {Body(r.Body)}",
        WaitStmt w => $"wait {D(w.Seconds)}",
        EmitStmt e => $"emit {e.Signal} [{L(e.Args)}]",
        TellStmt t => $"tell {D(t.Target)} {D(t.Message)} [{L(t.Args)}]",
        StopStmt => "stop",
        CommandStmt c => $"cmd {c.Name} [{L(c.Args)}]" + (c.Named == null ? "" : string.Concat(c.Named.Select(kv => $" {kv.Key}:[{L(kv.Value)}]"))),
        WaitUntilStmt u => $"waituntil {D(u.Cond)}",
        WaitSignalStmt f => $"waitfor {f.Full}",
        WaitKeyStmt k => $"waitkey {D(k.Key)}",
        TogetherStmt t => $"together {Body(t.Body)}",
        RunStmt r => $"run {Body(r.Body)}",
        ForEachStmt f => $"foreach {f.Var} {D(f.List)} {Body(f.Body)}",
        WhileStmt w => $"while {D(w.Cond)} {Body(w.Body)}",
        ReturnStmt r => $"return {D(r.Value)}",
        _ => throw new Exception("unknown statement " + s.GetType().Name)
    };

    static string Uses(UsesDef u) => $"{C(u)}{u.Name} {D(u.Positional)} {string.Join(",", u.Args.Select(kv => kv.Key + "=" + D(kv.Value)))}";

    static string Dump(IEnumerable<MechanismDef> defs) => string.Join("\n", defs.Select(m =>
        $"{C(m)}{(m.IsSystem ? "S" : "M")} {m.Name} from {m.Base} end«{string.Join("|", m.EndComments ?? [])}»\n" +
        string.Concat(m.Includes.Select(u => $" I {Uses(u)}\n")) +
        string.Concat(m.Params.Select(p => $" P {C(p)}{p.Name}:{p.Type}[{string.Join(",", p.Options ?? [])}]={D(p.Default)}\n")) +
        string.Concat(m.Uses.Select(u => $" U {Uses(u)}\n")) +
        string.Concat(m.Whens.Select(w => $" W {C(w)}{w.Event.Kind}|{w.Event.Filter}|{w.Event.Bind}|{w.Event.Source}|{w.Event.Name} {Body(w.Body)}\n")) +
        string.Concat(m.Procs.Select(p => $" TO {C(p)}{p.Name}({string.Join(",", p.Params)}) {Body(p.Body)}\n"))));

    /// <summary>Null when printing gives the same tree back and printing again gives the same text; else why not.</summary>
    static string? RoundTripProblem(List<MechanismDef> defs)
    {
        string printed = Printer.Print(defs);
        List<MechanismDef> again;
        try { again = Parser.Parse(printed); }
        catch (ScriptError e) { return $"the printed code doesn't parse ({e}):\n{printed}"; }
        string a = Dump(defs), b = Dump(again);
        if (a != b) return $"the tree changed after printing:\n{printed}\n--- before\n{a}\n--- after\n{b}";
        string printed2 = Printer.Print(again);
        if (printed2 != printed) return $"printing is not stable:\n{printed}\n--- then\n{printed2}";
        return null;
    }

    static void RoundTrip(string src, string label = "")
    {
        var defs = Parser.Parse(src);
        if (RoundTripProblem(defs) is { } why) throw new Exception(label + ": " + why);
        // Every comment of the source is in the printed code, with the same text.
        var before = new List<Comment>();
        Lexer.Tokenize(src, before);
        var after = new List<Comment>();
        Lexer.Tokenize(Printer.Print(defs), after);
        string Texts(List<Comment> l) => string.Join("\n", l.Select(c => c.Text).Order(StringComparer.Ordinal));
        if (Texts(before) != Texts(after)) throw new Exception($"{label}: comments changed\n--- before\n{Texts(before)}\n--- after\n{Texts(after)}");
    }

    // ---------------- The corpus: every Builder Script source in the repository ----------------

    static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "BuilderEngine.sln"))) return d.FullName;
        return null;
    }

    /// <summary>Embedded .bs files (starter kit, systems, samples), .bs files on disk, and the test sources written in C#.</summary>
    static List<(string Name, string Source)> Corpus()
    {
        var list = new List<(string, string)>();
        var asm = typeof(Parser).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".bs", StringComparison.Ordinal)).Order())
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            list.Add(("resource " + res, r.ReadToEnd()));
        }
        string? root = RepoRoot();
        if (root == null) return list;
        foreach (var f in Directory.EnumerateFiles(root, "*.bs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)).Order())
            list.Add(("file " + Path.GetRelativePath(root, f), File.ReadAllText(f)));
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("obj") && !f.EndsWith("AuditLangTests.cs")).Order())
        {
            int n = 0;
            foreach (var src in SourceLiterals(File.ReadAllText(f)))
            {
                try { Parser.Parse(src); }
                catch (ScriptError) { continue; } // an intentionally broken source (an error test)
                list.Add(($"test {Path.GetFileName(f)} #{++n}", src));
            }
        }
        return list;
    }

    static readonly Regex Literal = new("\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"", RegexOptions.Compiled);

    /// <summary>The C# string literals (joined with +) that start with "mechanism " or "system ".</summary>
    static IEnumerable<string> SourceLiterals(string cs)
    {
        for (int i = cs.IndexOf('"'); i >= 0 && i < cs.Length; i = cs.IndexOf('"', i + 1))
        {
            if (i > 0 && (cs[i - 1] is '$' or '@' or '\\')) continue;
            if (string.CompareOrdinal(cs, i + 1, "mechanism ", 0, 10) != 0 && string.CompareOrdinal(cs, i + 1, "system ", 0, 7) != 0) continue;
            var sb = new StringBuilder();
            int at = i;
            while (true)
            {
                var m = Literal.Match(cs, at);
                if (!m.Success || m.Index != at) break;
                sb.Append(Unescape(m.Groups[1].Value));
                at = m.Index + m.Length;
                int k = at;
                while (k < cs.Length && char.IsWhiteSpace(cs[k])) k++;
                if (k >= cs.Length || cs[k] != '+') break;
                k++;
                while (k < cs.Length && char.IsWhiteSpace(cs[k])) k++;
                if (k >= cs.Length || cs[k] != '"') break;
                at = k;
            }
            yield return sb.ToString();
            i = at - 1;
        }
    }

    static string Unescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char c = s[++i];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'u' when i + 4 < s.Length: sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // ---------------- Random programs (from the grammar) ----------------

    sealed class Gen(int seed)
    {
        readonly Random _r = new(seed);
        T Pick<T>(params T[] items) => items[_r.Next(items.Length)];
        bool Chance(double p) => _r.NextDouble() < p;

        // Names a block field accepts (BlockEdits.CheckName): no Builder Script word first. Connector words, 'seconds',
        // 'each', 'key', 'sound' are allowed and are exactly the ones the printer must be careful with.
        static readonly string[] Names = ["x", "hp", "speed", "items", "self", "other", "to", "in", "at", "by", "with", "from", "ease", "over", "as", "then", "seconds", "second", "each", "key", "sound", "until", "times", "é_ç"];
        static readonly string[] Members = ["x", "Health", "hp", "if", "to", "when", "forever"];
        static readonly string[] Words = ["mechanism", "param", "uses", "when", "if", "else", "repeat", "forever", "wait", "emit", "tell", "stop", "to", "together", "run", "for", "while", "return", "system", "includes"];

        public string Name() => Pick(Names);

        /// <summary>A name a block field takes for a variable, a signal or a loop item (BlockEdits.CheckName: not a Builder Script word).</summary>
        string BlockName() { string n; do n = Name(); while (Parser.Keywords.Contains(n)); return n; }

        string[] BlockPath() => Chance(0.7) ? [BlockName()] : [BlockName(), .. Enumerable.Range(0, _r.Next(1, 3)).Select(_ => Pick(Members))];

        string[] Path() => Chance(0.7) ? [Name()] : [Name(), .. Enumerable.Range(0, _r.Next(1, 3)).Select(_ => Pick(Members))];

        /// <summary>A value as the parser can produce it (what a block's value field holds).</summary>
        public Expr Expr(int depth)
        {
            int k = depth <= 0 ? _r.Next(4) : _r.Next(12);
            switch (k)
            {
                case 0: return new NumberExpr(Pick(0, 1, 2.5, 100, 0.001, 1e21, 1.2345678901234567e22, 1e-7, 3e-30, 123456789.125), 0);
                case 1: return new StringExpr(Pick("", "a b", "say \"hi\"", "back\\slash", "a # b", "two\nlines", "çã 😀", "it's"), 0);
                case 2: return new BoolExpr(Chance(0.5), 0);
                case 3: return new NameExpr(Path(), 0);
                case 4: return new UnaryExpr(Pick("-", "not"), Expr(depth - 1), 0);
                case 5: return new BinaryExpr(Pick("or", "and", "==", "!=", "<", ">", "<=", ">=", "+", "-", "*", "/", "%"), Expr(depth - 1), Expr(depth - 1), 0);
                case 6: return new CallExpr(Pick("min", "all", "find", "Health.heal_amount", "to_text"), Enumerable.Range(0, _r.Next(3)).Select(_ => Expr(depth - 1)).ToList(), 0);
                case 7: return new ListExpr(Enumerable.Range(0, _r.Next(4)).Select(_ => Expr(depth - 1)).ToList(), 0);
                case 8: return new IndexExpr(Expr(depth - 1), Expr(depth - 1), 0);
                case 9: return new RecordExpr(Enumerable.Range(0, _r.Next(3)).Select(i => (i == 0 ? Pick("if", "to", "b c", "x") : Pick("a", "x") + i, Expr(depth - 1))).ToList(), 0);
                case 10:
                {
                    // Member access only after a call, an item or another member (the parser reads 'a.b' as one name).
                    Expr target = Pick(0, 1, 2) switch
                    {
                        0 => new CallExpr("find", [new StringExpr("Boss", 0)], 0),
                        1 => new IndexExpr(new NameExpr(Path(), 0), Expr(depth - 1), 0),
                        _ => new MemberExpr(new CallExpr("find", [], 0), Pick(Members), 0)
                    };
                    return new MemberExpr(target, Pick(Members), 0);
                }
                default: return new NameExpr(Path(), 0);
            }
        }

        List<Expr> Exprs(int max, int depth) => Enumerable.Range(0, _r.Next(max + 1)).Select(_ => Expr(depth)).ToList();

        CodeComments? Comments()
        {
            if (!Chance(0.25)) return null;
            var before = Enumerable.Range(0, _r.Next(3)).Select(i => Pick("# note", "#", "# ## twice", "# a \"quote\" and 'one'")).ToList();
            string? after = Chance(0.5) ? "# end " + _r.Next(9) : null;
            return before.Count == 0 && after == null ? null : new CodeComments(before, after); // as the parser makes them
        }

        public List<Stmt> Body(int depth, bool inProc) => Enumerable.Range(0, _r.Next(depth <= 0 ? 2 : 4)).Select(_ => Stmt(depth, inProc)).ToList();

        public Stmt Stmt(int depth, bool inProc)
        {
            int k = depth <= 0 ? _r.Next(10) : _r.Next(18);
            Stmt s = k switch
            {
                0 => new AssignStmt(BlockPath(), Pick("=", "+=", "-=", "*=", "/="), Expr(2), 0),
                1 => new WaitStmt(Expr(2), 0),
                2 => new WaitUntilStmt(Expr(2), 0),
                3 => new WaitSignalStmt(BlockPath(), 0),
                4 => new WaitKeyStmt(Expr(1), 0),
                5 => new EmitStmt(BlockName(), Exprs(2, 1), 0),
                6 => new TellStmt(Expr(1), Expr(1), Exprs(2, 1), 0),
                7 => new StopStmt(0),
                8 => Command(),
                9 => inProc ? new ReturnStmt(Chance(0.5) ? Expr(2) : null, 0) : Command(),
                10 => new IfStmt(Expr(2), Body(depth - 1, inProc), Pick(0, 1, 2) switch { 0 => null, 1 => Body(depth - 1, inProc), _ => [new IfStmt(Expr(1), Body(depth - 1, inProc), null, 0) { Comments = Comments() }] }, 0),
                11 => new RepeatStmt(Chance(0.3) ? null : Expr(2), Body(depth - 1, inProc), 0),
                12 => new TogetherStmt(Body(depth - 1, inProc), 0),
                13 => new RunStmt(Body(depth - 1, inProc), 0),
                14 => new RunStmt([Stmt(depth - 1, inProc)], 0),
                15 => new ForEachStmt(BlockName(), Expr(2), Body(depth - 1, inProc), 0),
                16 => new WhileStmt(Expr(2), Body(depth - 1, inProc), 0),
                _ => Command()
            };
            return s with { Comments = Comments() };
        }

        CommandStmt Command()
        {
            string name = Pick("print", "move", "spawn", "say", "attack", "Health.heal", "add", "remove", "stop");
            var args = Exprs(3, 1);
            if (name == "stop") args.Insert(0, new NameExpr([Pick("sound", "sounds")], 0)); // 'stop sound "x"' (World.Audio.cs)
            Dictionary<string, List<Expr>>? named = null;
            if (name != "stop" && Chance(0.4))
            {
                named = new();
                foreach (var c in Parser.Connectors.OrderBy(_ => _r.Next()).Take(_r.Next(1, 3)))
                    named[c] = Enumerable.Range(0, _r.Next(1, 3)).Select(_ => Expr(1)).ToList();
            }
            return new CommandStmt(name, args, 0, named);
        }

        static readonly EventSpec[] Events =
        [
            new(EventKind.Start), new(EventKind.Tick), new(EventKind.Touched, Filter: "player"), new(EventKind.Touched, Bind: "other"),
            new(EventKind.Touched), new(EventKind.Told, Filter: "damage", Bind: "amount"), new(EventKind.Told, Filter: "a \"b\""),
            new(EventKind.Key, Filter: "space"), new(EventKind.Signal, Name: "died"), new(EventKind.Signal, Source: "Health", Name: "died"),
            new(EventKind.Signal, Name: "tool"), new(EventKind.Tool, Name: "click", Bind: "x, y"), new(EventKind.Tool, Name: "used"),
            new(EventKind.Tool, Name: "key", Filter: "r"), new(EventKind.Game, Name: "clicked"),
            new(EventKind.Game, Filter: "right", Bind: "x, y", Name: "mouse pressed"), new(EventKind.Game, Filter: "slot", Name: "dropped"),
            new(EventKind.Game, Filter: "stone", Bind: "px", Name: "tile broken"), new(EventKind.Game, Name: "picked up"), new(EventKind.Game, Name: "received"),
        ];

        public MechanismDef Mechanism(string name, bool system)
        {
            var ps = new List<ParamDef>();
            for (int i = 0, n = _r.Next(3); i < n; i++)
            {
                var (type, options) = Pick<(string, List<string>?)>(("any", null), ("number", null), ("List of number", null), ("Choice", ["easy", "a \"b\""]), ("Sound", null));
                ps.Add(new ParamDef("p" + i, type, Chance(0.6) ? Expr(1) : null, 0) { Options = options, Comments = Comments() });
            }
            UsesDef Use(string n) => new(n, Chance(0.4) ? Expr(1) : null, Enumerable.Range(0, _r.Next(3)).ToDictionary(i => Pick("max", "speed", "to", "seconds") + i, _ => Expr(1)), 0) { Comments = Comments() };
            var uses = Enumerable.Range(0, _r.Next(3)).Select(i => Use("Health" + i)).ToList();
            var whens = Enumerable.Range(0, _r.Next(1, 4)).Select(_ => new WhenDef(Pick(Events), Body(3, false), 0) { Comments = Comments() }).ToList();
            var procs = Enumerable.Range(0, _r.Next(3)).Select(i => new ProcDef("do_it" + i, Enumerable.Range(0, _r.Next(3)).Select(j => "a" + j).ToList(), Body(3, true), 0) { Comments = Comments() }).ToList();
            return new MechanismDef(name, ps, uses, whens, 0)
            {
                Procs = procs, IsSystem = system, Base = !system && Chance(0.2) ? "Base" : null,
                Includes = system ? Enumerable.Range(0, _r.Next(3)).Select(i => Use("Coin" + i)).ToList() : [],
                Comments = Comments(),
            };
        }

        public List<MechanismDef> File()
        {
            var defs = Enumerable.Range(0, _r.Next(1, 3)).Select(i => Mechanism("Mech" + i, Chance(0.2))).ToList();
            if (Chance(0.3)) defs[^1] = defs[^1] with { EndComments = ["# the end", "#"] };
            return defs;
        }
    }

    // ---------------- Block edits on real programs ----------------

    /// <summary>Every statement list of a mechanism (when and 'to' bodies, and the bodies inside them), outermost first.</summary>
    static List<List<Stmt>> AllBodies(MechanismDef m)
    {
        var all = new List<List<Stmt>>();
        void Walk(List<Stmt> l)
        {
            all.Add(l);
            foreach (var s in l) foreach (var b in BlockEdits.Bodies(s)) Walk(b);
        }
        for (int k = 0; BlockEdits.TopBody(m, k) is { } top; k++) Walk(top);
        return all;
    }

    /// <summary>One random edit, done the way the blocks view does it (BlocksView*.cs): false when nothing could be done.</summary>
    static bool RandomEdit(Random r, MechanismDef m)
    {
        var bodies = AllBodies(m);
        var nonEmpty = bodies.Where(b => b.Count > 0).ToList();
        switch (r.Next(12))
        {
            case 0: // palette click / drop
            {
                if (bodies.Count == 0) return false;
                var (id, _) = BlockEdits.StatementKinds[r.Next(BlockEdits.StatementKinds.Length)];
                var body = bodies[r.Next(bodies.Count)];
                BlockEdits.Insert(body, r.Next(body.Count + 1), BlockEdits.NewStatement(id));
                return true;
            }
            case 1: // 'return' from MY COMMANDS: only inside a 'to'
            {
                var inProc = bodies.Where(b => BlockEdits.InProc(m, b)).ToList();
                if (inProc.Count == 0) return false;
                inProc[r.Next(inProc.Count)].Add(BlockEdits.NewStatement("return"));
                return true;
            }
            case 2: // drag a block somewhere (the drop targets StatementTarget offers)
            {
                if (nonEmpty.Count == 0) return false;
                var from = nonEmpty[r.Next(nonEmpty.Count)];
                int i = r.Next(from.Count);
                var to = bodies[r.Next(bodies.Count)];
                if (!BlockEdits.CanPlace(m, from[i], to)) return false;
                return BlockEdits.MoveStatement(from, i, to, r.Next(to.Count + 1));
            }
            case 3: // ✕ / drop on the palette
            {
                if (nonEmpty.Count == 0) return false;
                var b = nonEmpty[r.Next(nonEmpty.Count)];
                return BlockEdits.Delete(b, r.Next(b.Count));
            }
            case 4: // ⧉
            {
                if (nonEmpty.Count == 0) return false;
                var b = nonEmpty[r.Next(nonEmpty.Count)];
                BlockEdits.Duplicate(b, r.Next(b.Count));
                return true;
            }
            case 5: // ↑ ↓
            {
                if (nonEmpty.Count == 0) return false;
                var b = nonEmpty[r.Next(nonEmpty.Count)];
                return BlockEdits.Move(b, r.Next(b.Count), r.Next(2) == 0 ? -1 : 1);
            }
            case 6: // + else / − else
            {
                foreach (var b in bodies.OrderBy(_ => r.Next()))
                    for (int j = 0; j < b.Count; j++)
                        if (b[j] is IfStmt s) { b[j] = s.Else == null ? s with { Else = [] } : s.Else.Count == 0 ? s with { Else = null } : s; return true; }
                return false;
            }
            case 7: // a command's values typed in its field (ListField → ParseExpressionList)
            {
                foreach (var b in bodies.OrderBy(_ => r.Next()))
                    for (int j = 0; j < b.Count; j++)
                        if (b[j] is CommandStmt c)
                        {
                            string text = new[] { "to", "x, to", "to + 1, 2", "in, seconds", "at(1)", "seconds", "-1, (x)", "from.x", "[to]" }[r.Next(9)];
                            var values = Parser.ParseExpressionList(text);
                            if (c.Named != null && r.Next(2) == 0)
                            {
                                var key = c.Named.Keys.First();
                                var named = c.Named.ToDictionary(kv => kv.Key, kv => kv.Value);
                                named[key] = values;
                                b[j] = c with { Named = named };
                            }
                            else if (BlockEdits.CheckCommandValues(c.Name, values) == null) b[j] = c with { Args = values };
                            else return false; // the field refuses it (red, with the reason)
                            return true;
                        }
                return false;
            }
            case 8: // a value field (ExprField → ParseExpression)
            {
                string text = new[] { "forever + 1", "until", "for", "-x", "not a == b", "a == (not b)", "{a: 1}", "find(\"x\").hp", "seconds" }[r.Next(9)];
                var e = Parser.ParseExpression(text);
                foreach (var b in bodies.OrderBy(_ => r.Next()))
                    for (int j = 0; j < b.Count; j++)
                        switch (b[j])
                        {
                            case RepeatStmt { Count: not null } rep: b[j] = rep with { Count = e }; return true;
                            case WaitStmt w: b[j] = w with { Seconds = e }; return true;
                            case TellStmt t: b[j] = t with { Message = e }; return true;
                            case IfStmt i: b[j] = i with { Cond = e }; return true;
                        }
                return false;
            }
            case 9: // new when / to / param / uses
                switch (r.Next(4))
                {
                    case 0: m.Whens.Add(BlockEdits.NewWhen(BlockEdits.EventKinds[r.Next(BlockEdits.EventKinds.Length)])); break;
                    case 1: m.Procs.Add(BlockEdits.NewProc(m)); break;
                    case 2: m.Params.Add(BlockEdits.NewParam(m)); break;
                    default: m.Uses.Add(BlockEdits.NewUses()); break;
                }
                return true;
            case 10: // ⧉ on a when or a 'to'
                if (m.Whens.Count > 0 && r.Next(2) == 0) { BlockEdits.Duplicate(m.Whens, r.Next(m.Whens.Count)); return true; }
                if (m.Procs.Count > 0) { BlockEdits.Duplicate(m.Procs, r.Next(m.Procs.Count)); return true; }
                return false;
            default: // an event typed in the when's field
            {
                if (m.Whens.Count == 0) return false;
                int w = r.Next(m.Whens.Count);
                var kind = m.Whens[w].Event.Kind;
                string tail = kind switch
                {
                    EventKind.Touched => "other", EventKind.Told => "\"hit\" n", EventKind.Key => "\"left\"", EventKind.Signal => "Health.died",
                    EventKind.Tool => "drag at x, y", EventKind.Game => "dropped on \"slot\"", _ => ""
                };
                m.Whens[w] = m.Whens[w] with { Event = BlockEdits.ParseEvent(kind, tail) };
                return true;
            }
        }
    }

    /// <summary>The statements of a mechanism in printing order.</summary>
    static IEnumerable<Stmt> Statements(MechanismDef m)
    {
        IEnumerable<Stmt> Walk(List<Stmt> l)
        {
            foreach (var s in l)
            {
                yield return s;
                foreach (var b in BlockEdits.Bodies(s)) foreach (var x in Walk(b)) yield return x;
            }
        }
        for (int k = 0; BlockEdits.TopBody(m, k) is { } top; k++) foreach (var s in Walk(top)) yield return s;
    }

    // ---------------- The tests ----------------

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine();
        Console.WriteLine("Audit: language front-end and blocks ⇄ code (AuditLangTests.cs)");

        // ----- Lexer -----
        test("lexer: a byte order mark at the start is ignored", () =>
        {
            Eq("A", Parser.Parse("\uFEFFmechanism A\n    when start\n        x = 1\n")[0].Name);
        });
        test("lexer: a number too big to write back is an error (it was read as infinity, printed as the name 'Infinity')", () =>
        {
            Throws("too big", () => Parser.Parse(M("x = " + new string('9', 400))));
            Throws("too big", () => Parser.Parse(M("x = 1e400")));
            Eq(1e300, ((NumberExpr)Parser.ParseExpression("1" + new string('0', 300))).Value);
        });
        test("lexer: numbers with an exponent (1e308, 2.5E-4, 1e+3); a name after a number still reads as before", () =>
        {
            Eq(1e308, ((NumberExpr)Parser.ParseExpression("1e308")).Value);
            Eq(2.5e-4, ((NumberExpr)Parser.ParseExpression("2.5E-4")).Value);
            Eq(1000.0, ((NumberExpr)Parser.ParseExpression("1e+3")).Value);
            Eq("(- 1000 $e)", D(Parser.ParseExpression("1e3 - e")));
            // 'e' without digits after it is not an exponent: '3times' / '2each' stay a number and a name.
            Eq("repeat 3 {stop}", D(Parser.Parse(M("repeat 3times: stop"))[0].Whens[0].Body[0]));
            Eq("cmd say [2 $e $ex]", D(Parser.Parse(M("say 2e, ex"))[0].Whens[0].Body[0]));
            // Printing writes plain digits (older versions read them too).
            Eq("        x = 100000000000000000000\n", Printer.Print(Parser.Parse(M("x = 1e20"))[0].Whens[0].Body[0], 2));
        });
        test("parser: code nested thousands deep is an error, not a crash of the whole editor", () =>
        {
            foreach (var (open, close) in new[] { ("(", ")"), ("[", "]"), ("f(", ")"), ("{a: ", "}") })
                Throws("too deep", () => Parser.Parse(M("x = " + string.Concat(Enumerable.Repeat(open, 3000)) + "1" + string.Concat(Enumerable.Repeat(close, 3000)))));
            Throws("too deep", () => Parser.Parse(M("x = " + string.Concat(Enumerable.Repeat("- ", 5000)) + "1")));
            Throws("too deep", () => Parser.Parse(M("x = " + string.Concat(Enumerable.Repeat("not ", 5000)) + "a")));
            Throws("too deep", () => Parser.Parse(M("x = 1" + string.Concat(Enumerable.Repeat(" + 1", 5000)))));
            Throws("too deep", () => Parser.Parse(M("x = l" + string.Concat(Enumerable.Repeat("[0]", 5000)))));
            Throws("too deep", () => Parser.Parse(M("run " + string.Concat(Enumerable.Repeat("run ", 5000)) + "stop")));
            Throws("too deep", () => Parser.Parse("mechanism T\n    when start: " + string.Concat(Enumerable.Repeat("run ", 5000)) + "stop\n"));
            var deep = new StringBuilder("mechanism T\n    when start\n");
            for (int i = 0; i < 3000; i++) deep.Append(new string(' ', 8 + 4 * i)).Append("if a\n");
            Throws("too deep", () => Parser.Parse(deep.ToString()));
            var elseIfs = new StringBuilder("mechanism T\n    when start\n        if a == 0: x = 0\n");
            for (int i = 1; i < 3000; i++) elseIfs.Append($"        else if a == {i}: x = {i}\n");
            Throws("too deep", () => Parser.Parse(elseIfs.ToString()));
            Throws("too deep", () => Parser.ParseExpression(string.Concat(Enumerable.Repeat("(", 3000)) + "1" + string.Concat(Enumerable.Repeat(")", 3000))));
        });
        test("parser: the deepest code accepted still prints, round-trips and plays without running out of stack", () =>
        {
            // Every precedence level as a long chain along the same left spine: the deepest tree the limits allow.
            int n = Parser.MaxDepth - 20, chain = (Parser.MaxSteps - 20) / 5;
            string Chain(string first, string op, string item) => first + string.Concat(Enumerable.Repeat($" {op} {item}", chain));
            string e = Chain(Chain(Chain(Chain(Chain("1", "*", "1"), "+", "1"), "<", "1"), "and", "true"), "or", "false");
            string Nest(string inner) => string.Concat(Enumerable.Repeat("(", n)) + inner + string.Concat(Enumerable.Repeat(")", n));
            string longSum = string.Join(" + ", Enumerable.Repeat("1", Parser.MaxSteps - 20 + 1));
            string src = M($"x = {Nest(e)}\ny = {Nest(longSum)}\nz = [{string.Concat(Enumerable.Repeat("[", n))}1{string.Concat(Enumerable.Repeat("]", n))}]\nw = {string.Join(" + ", Enumerable.Repeat("1", 300))}");
            RoundTrip(src, "deep");
            var defs = Parser.Parse(src);
            BlockEdits.Duplicate(defs[0].Whens, 0);
            if (RoundTripProblem(defs) is { } why) throw new Exception(why);
            var w = new World();
            w.Load(src);
            w.Spawn("o", 0, 0, ["T"]);
            w.Step(1.0 / 60);
            True(w.Errors.Count == 0 || w.Errors.All(x => x.Contains("too deep")), string.Join(" | ", w.Errors));
            Throws("too deep", () => Parser.Parse(M("x = " + string.Join(" + ", Enumerable.Repeat("1", Parser.MaxSteps + 2)))));
        });
        test("lexer: an emoji outside quotes is named whole in the error", () =>
        {
            Throws("'😀'", () => Parser.Parse(M("😀 = 1")));
        });
        test("lexer: every line ending and tabs read the same", () =>
        {
            string lf = "mechanism A\n    when start\n        x = 1\n";
            string want = Dump(Parser.Parse(lf));
            Eq(want, Dump(Parser.Parse(lf.Replace("\n", "\r\n"))), "CRLF");
            Eq(want, Dump(Parser.Parse(lf.Replace("\n", "\r"))), "CR");
            Eq(want, Dump(Parser.Parse(lf.TrimEnd('\n'))), "no newline at the end");
            Eq(want, Dump(Parser.Parse("mechanism A\n\twhen start\n\t\tx = 1\n")), "tabs");
            Eq(want, Dump(Parser.Parse("mechanism A   \n    when start   \n        x = 1   \n   \n")), "trailing spaces");
        });
        test("lexer: comments are collected with their line and whether code comes before them", () =>
        {
            var comments = new List<Comment>();
            Lexer.Tokenize("# top\nmechanism A  # head\n    when start\n        print \"a # not a comment\"  # end\n", comments);
            Eq("1:# top:False|2:# head:True|4:# end:True", string.Join("|", comments.Select(c => $"{c.Line}:{c.Text}:{c.Trailing}")));
        });

        // ----- Parser -----
        test("parser: 'wait for key' takes any value; 'wait for key.x' is still a signal", () =>
        {
            var body = Parser.Parse(M("wait for key (k)\nwait for key -1\nwait for key [\"a\"][0]\nwait for key.pressed\nwait for key"))[0].Whens[0].Body;
            Eq("waitkey $k|waitkey (- 1)|waitkey (at <'a'> 0)|waitfor key.pressed|waitfor key", string.Join("|", body.Select(D)));
            RoundTrip(M("wait for key (k)\nwait for key -1\nwait for key.pressed\nwait for key"));
        });
        test("parser: 'if' / 'repeat' after a ':' are errors (they were read as commands and failed only when played)", () =>
        {
            Throws("needs its own lines", () => Parser.Parse(M("if a: repeat 3")));
            Throws("needs its own lines", () => Parser.Parse(M("if a: if b: c = 1")));
            Throws("needs its own lines", () => Parser.Parse("mechanism T\n    when start: repeat forever\n"));
            Throws("needs its own lines", () => Parser.Parse(M("if a: run repeat 2")));
        });
        test("parser: a stray 'else' is an error (it was read as a command called 'else')", () =>
        {
            Throws("right after the lines of an 'if'", () => Parser.Parse(M("x = 1\nelse")));
            Throws("right after the lines of an 'if'", () => Parser.Parse(M("x = 1\nelse:\n    y = 1")));
        });
        test("parser: 'a == not b' is an error (it read 'not' as a name and 'b' as one more value)", () =>
        {
            Throws("parentheses", () => Parser.Parse(M("print a == not b")));
            Throws("parentheses", () => Parser.Parse(M("x = 1 + not b")));
            var e = (BinaryExpr)Parser.ParseExpression("a == (not b)");
            Eq("(== $a (not $b))", D(e));
            Eq("(not (== $a $b))", D(Parser.ParseExpression("not a == b")), "'not' before a comparison still covers it");
        });
        test("parser: words used as variables still work (assignments, values)", () =>
        {
            var body = Parser.Parse(M("else = 1\nrepeat = 2\nif = 3\nnot = 4\nprint to\nx = forever"))[0].Whens[0].Body;
            Eq(6, body.Count);
            Eq("else = 1", Printer.Print(body[0], 0).Trim());
            Eq("cmd print [$to]", D(body[4]));
        });
        test("parser: precedence and associativity", () =>
        {
            Eq("(- (- $a $b) $c)", D(Parser.ParseExpression("a - b - c")));
            Eq("(* (/ $a $b) $c)", D(Parser.ParseExpression("a / b * c")));
            Eq("(* (- 2) 3)", D(Parser.ParseExpression("-2 * 3")));
            Eq("(or (and $a $b) $c)", D(Parser.ParseExpression("a and b or c")));
            Eq("(< (< 1 $x) 3)", D(Parser.ParseExpression("1 < x < 3")), "comparisons chain to the left (documented, not Python's)");
            Eq("(!= $a $b)", D(Parser.ParseExpression("a <> b")));
            Eq("(. (at $l 0) Health)", D(Parser.ParseExpression("l[0].Health")));
            Eq("(. (. find['x'] y) z)", D(Parser.ParseExpression("find(\"x\").y.z")));
        });
        test("parser: brackets, records and calls can span lines", () =>
        {
            var body = Parser.Parse(M("x = (1 +\n2)\ny = [1,\n  2]\nz = {a: 1,\n b: 2}\nw = min(1,\n 2)"))[0].Whens[0].Body;
            Eq(4, body.Count);
            Throws("missing its '}'", () => Parser.Parse(M("x = {a: 1")));
            Throws("Expected ')'", () => Parser.Parse(M("x = (1 + 2")));
        });
        test("parser: error lines point at the problem", () =>
        {
            try { Parser.Parse("mechanism T\n    when start\n        x = 1\n        y = = 2\n"); throw new Exception("no error"); }
            catch (ScriptError e) { Eq(4, e.Line); }
            try { Parser.Parse("mechanism T\n    when start\n        print \"abc\n"); throw new Exception("no error"); }
            catch (ScriptError e) { Eq(3, e.Line); }
        });

        // ----- Printer -----
        test("printer: numbers come back exactly, without an exponent", () =>
        {
            foreach (var v in new[] { 1e21, 1.2345678901234567e22, 1e-7, 3e-30, 123456789.125, 0.1, 1e15, 1e16, -2.5e-8, 5e-324, 1.7976931348623157e308 })
            {
                string s = Printer.Number(v);
                True(!s.Contains('E') && !s.Contains('e'), $"{v} printed with an exponent: {s}");
                Eq(v, ((NumberExpr)Parser.ParseExpression(s.TrimStart('-'))).Value * (s.StartsWith('-') ? -1 : 1), $"{v} → {s}");
            }
            Eq("100000000000000000000", Printer.Number(1e20));
            Eq("0.00001", Printer.Number(1e-5));
        });
        test("printer: command values starting with a connector word get parentheses", () =>
        {
            var c = new CommandStmt("move", [new NameExpr(["to"], 0), new BinaryExpr("+", new NameExpr(["in"], 0), new NumberExpr(1, 0), 0)], 0,
                new() { ["by"] = [new NumberExpr(2, 0), new NameExpr(["seconds"], 0)] });
            Eq("move (to), (in + 1) by 2, (seconds)", Printer.Simple(c));
            Eq("say to", Printer.Simple(new CommandStmt("say", [new NameExpr(["to"], 0)], 0)), "a lone word at the end needs none");
            RoundTrip(M("say to\nsay x, to\nmove (to) by 3"));
        });
        test("printer: 'stop sound \"x\"' prints as written", () =>
        {
            Eq("        stop sound \"rain.wav\"\n", Printer.Print(Parser.Parse(M("stop sound \"rain.wav\""))[0].Whens[0].Body[0], 2));
            RoundTrip(M("stop sound \"rain.wav\"\nstop sounds\nstop sound -1\nstop sound (x)"));
        });
        test("printer: a repeat count starting with 'forever' gets parentheses", () =>
        {
            var m = Parser.Parse(M("x = 1"))[0];
            m.Whens[0].Body.Add(new RepeatStmt(Parser.ParseExpression("forever + 1"), [], 0));
            if (RoundTripProblem([m]) is { } why) throw new Exception(why);
        });
        test("printer: comments stay where they were written", () =>
        {
            string src =
                "# Coin: gives points\nmechanism Coin  # the coin\n    # how much\n    param value: number = 1  # points\n    uses Sprite \"coin.png\"\n\n" +
                "    # when the player takes it\n    when touched by \"player\"  # touch\n        # add\n        score += value  # more\n        if score > 9  # won?\n" +
                "            emit won\n        # before else-if\n        else if score > 5: print \"half\"  # half\n        else\n            # nothing\n            stop\n" +
                "        run patrol  # in the background\n\n    to patrol\n        # walk\n        move 1, 0\n# end of file\n";
            string want =
                "# Coin: gives points\nmechanism Coin  # the coin\n    # how much\n    param value: number = 1  # points\n    uses Sprite \"coin.png\"\n\n" +
                "    # when the player takes it\n    when touched by \"player\"  # touch\n        # add\n        score += value  # more\n        if score > 9  # won?\n" +
                "            emit won\n        # before else-if\n        else if score > 5  # half\n            print \"half\"\n        else\n            # nothing\n            stop\n" +
                "        run patrol  # in the background\n\n    to patrol\n        # walk\n        move 1, 0\n    # end of file\n";
            Eq(want, Printer.Print(Parser.Parse(src)));
            RoundTrip(src, "comments");
        });
        test("printer: a commented block moved into a 'run' keeps its comments above the run line", () =>
        {
            var m = Parser.Parse(M("run:\n# walk\nrepeat 2  # twice\n    x += 1"))[0];
            var body = m.Whens[0].Body;
            True(BlockEdits.MoveStatement(body, 1, ((RunStmt)body[0]).Body, 0), "moved");
            string printed = Printer.Print([m]);
            True(printed.Contains("        # walk\n        run repeat 2  # twice\n"), printed);
            if (RoundTripProblem([m]) is { } why) throw new Exception(why);
        });

        // ----- Round-trips of every source -----
        test("round-trip: every Builder Script source in the repository (resources, .bs files, test sources)", () =>
        {
            var corpus = Corpus();
            True(corpus.Count(c => c.Name.StartsWith("resource")) >= 60, $"embedded sources found: {corpus.Count(c => c.Name.StartsWith("resource"))}");
            True(corpus.Count(c => c.Name.StartsWith("test")) >= 150, $"test sources found: {corpus.Count(c => c.Name.StartsWith("test"))}");
            var problems = new List<string>();
            foreach (var (name, src) in corpus)
            {
                try { RoundTrip(src, name); }
                catch (Exception e) { problems.Add(e.Message); }
            }
            True(problems.Count == 0, $"{problems.Count} of {corpus.Count} sources don't round-trip; first:\n{problems.FirstOrDefault()}");
            Console.WriteLine($"         ({corpus.Count} sources)");
        });
        test("fuzz: 3000 random programs print and parse back to the same tree (comments included)", () =>
        {
            var problems = new List<string>();
            for (int seed = 1; seed <= 3000; seed++)
            {
                var defs = new Gen(seed).File();
                if (RoundTripProblem(defs) is { } why) problems.Add($"seed {seed}: {why}");
            }
            True(problems.Count == 0, $"{problems.Count} programs failed; first:\n{problems.FirstOrDefault()}");
        });
        test("fuzz: random value texts that parse print back to the same value", () =>
        {
            int checkedCount = 0;
            for (int seed = 1; seed <= 4000; seed++)
            {
                var e = new Gen(seed).Expr(3);
                string text = Printer.Print(e);
                Eq(D(e), D(Parser.ParseExpression(text)), $"seed {seed}: {text}");
                checkedCount++;
            }
            Eq(4000, checkedCount);
        });

        // ----- Blocks -----
        test("blocks: a block holding a 'return' can only go inside a 'to' (elsewhere the code wouldn't parse)", () =>
        {
            var m = Parser.Parse("mechanism T\n    when start\n        x = 1\n\n    to f a\n        if a: return 1\n        return 2\n")[0];
            var when = m.Whens[0].Body;
            var proc = m.Procs[0].Body;
            True(BlockEdits.HasReturn(proc[0]), "an if with a return inside");
            Eq(false, BlockEdits.CanPlace(m, proc[0], when), "into a when");
            Eq(true, BlockEdits.CanPlace(m, proc[0], proc), "inside the 'to'");
            Eq(true, BlockEdits.CanPlace(m, when[0], proc), "a block without return goes anywhere");
            // What the old check let through: the drop printed code that didn't parse.
            BlockEdits.MoveStatement(proc, 0, when, 0);
            Throws("'return' only works inside", () => Parser.Parse(Printer.Print([m])));
        });
        test("blocks: after an edit each block knows its new line (breakpoints from the blocks)", () =>
        {
            string src = "mechanism T\n    # comment\n\n\n    when start\n        x = 1\n        y = 2\n";
            var defs = Parser.Parse(src);
            var body = defs[0].Whens[0].Body;
            var y = body[1];
            Eq(7, y.Line);
            BlockEdits.Insert(body, 0, BlockEdits.NewStatement("stop"));
            string printed = Printer.Print(defs);
            var lines = BlockEdits.LinesAfterPrint(defs, printed)!;
            int line = lines[y];
            Eq("        y = 2", printed.Split('\n')[line - 1], $"line {line}");
            Eq(true, lines[body[0]] > 0, "the new block has a line too");
            Eq(null, BlockEdits.LinesAfterPrint(defs, "mechanism"), "code that doesn't parse: no lines");
        });
        test("blocks: a 'stop sound' block's values must start with 'sound' (else the code read a plain 'stop' and didn't parse)", () =>
        {
            Eq(null, BlockEdits.CheckCommandValues("stop", Parser.ParseExpressionList("sound \"rain.wav\"")));
            Eq(null, BlockEdits.CheckCommandValues("stop", Parser.ParseExpressionList("sounds")));
            True(BlockEdits.CheckCommandValues("stop", Parser.ParseExpressionList("\"rain.wav\"")) != null, "no 'sound' word");
            True(BlockEdits.CheckCommandValues("stop", []) != null, "no values");
            Eq(null, BlockEdits.CheckCommandValues("print", Parser.ParseExpressionList("x, y")));
            // What the old field allowed:
            Throws("didn't expect", () => Parser.Parse(Printer.Print([new MechanismDef("T", [], [], [new WhenDef(new EventSpec(EventKind.Start), [new CommandStmt("stop", [new StringExpr("rain.wav", 0)], 0)], 0)], 0)])));
        });
        test("blocks: a duplicated block is a new block (the view tells blocks apart by reference)", () =>
        {
            var body = Parser.Parse(M("x = 1"))[0].Whens[0].Body;
            BlockEdits.Duplicate(body, 0);
            True(!ReferenceEquals(body[0], body[1]), "the copy is the same object");
        });
        test("blocks: renaming a command also renames it inside 'wait for key'", () =>
        {
            var m = Parser.Parse("mechanism T\n    when start\n        wait for key key_of(1)\n\n    to key_of n\n        return \"space\"\n")[0];
            Eq(1, BlockEdits.RenameCalls(m, "key_of", "pick_key"));
            True(Printer.Print([m]).Contains("wait for key pick_key(1)"), Printer.Print([m]));
        });
        test("blocks: undo steps through code typed between block edits (it used to jump over it)", () =>
        {
            var u = new BlockUndo();
            u.Record("s0", "s1"); // a block edit
            u.Record("s1", "s2", "code"); // typing in the code view (BlocksView.Show)
            u.Record("s2", "s3", "code"); // more typing: the same step
            Eq("s1", u.Undo("s3"), "undo the typing");
            Eq("s0", u.Undo("s1"), "undo the block edit");
            Eq("s1", u.Redo("s0"));
            Eq("s3", u.Redo("s1"), "the typed code comes back");
        });
        test("fuzz: 4000 random block edits on every sample program always print code that parses to the same blocks", () =>
        {
            var corpus = Corpus().Where(c => !c.Name.StartsWith("test")).ToList();
            var r = new Random(7);
            int edits = 0;
            var problems = new List<string>();
            for (int round = 0; round < 4000 && problems.Count == 0; round++)
            {
                var (name, src) = corpus[r.Next(corpus.Count)];
                var defs = Parser.Parse(src);
                var m = defs[r.Next(defs.Count)];
                for (int k = 0; k < 4; k++)
                {
                    if (!RandomEdit(r, m)) continue;
                    edits++;
                    string printed = Printer.Print(defs);
                    if (RoundTripProblem(defs) is { } why) { problems.Add($"{name}, round {round}: {why}"); break; }
                    var lines = BlockEdits.LinesAfterPrint(defs, printed);
                    if (lines == null) { problems.Add($"{name}: no lines"); break; }
                    // Every block gets a line, in the order the blocks are printed.
                    int last = 0;
                    foreach (var mech in defs)
                        foreach (var s in Statements(mech))
                        {
                            if (!lines.TryGetValue(s, out int line) || line <= 0 || line < last) { problems.Add($"{name}, round {round}: a block has no line or a wrong one ({line} after {last}; a one-line run or if shares its line):\n{printed}"); break; }
                            last = line;
                        }
                }
            }
            True(problems.Count == 0, problems.FirstOrDefault() ?? "");
            True(edits > 8000, $"only {edits} edits were made");
        });
    }
}
