using System.Diagnostics;
using BuilderEngine.Script;

/// <summary>The code editor's colours (SyntaxColors / Lexer.ScanLine). Called from Program.cs with its Test runner.</summary>
static class SyntaxTests
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

    /// <summary>"text:Class text:Class …" for one line.</summary>
    static string Show(string line, SyntaxContext? ctx = null) =>
        string.Join(" ", SyntaxColors.Classify(line, ctx).Select(s => line.Substring(s.Start, s.Length) + ":" + s.Class));

    /// <summary>The class of the first piece whose text is <paramref name="piece"/> (after <paramref name="skip"/> earlier ones).</summary>
    static CodeClass ClassOf(string line, string piece, SyntaxContext? ctx = null, int skip = 0)
    {
        foreach (var s in SyntaxColors.Classify(line, ctx))
            if (line.Substring(s.Start, s.Length) == piece && skip-- == 0) return s.Class;
        throw new Exception($"no piece '{piece}' in: {Show(line, ctx)}");
    }

    static void Is(CodeClass expected, string line, string piece, SyntaxContext? ctx = null, int skip = 0) =>
        Eq(expected, ClassOf(line, piece, ctx, skip), $"'{piece}' in \"{line.Trim()}\"");

    /// <summary>Every embedded .bs file: the starter kit, the systems and the sample games.</summary>
    static List<(string Name, string Source)> Corpus()
    {
        var asm = typeof(Parser).Assembly;
        var list = new List<(string, string)>();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".bs", StringComparison.Ordinal)).Order())
        {
            using var r = new StreamReader(asm.GetManifestResourceStream(res)!);
            list.Add((res, r.ReadToEnd()));
        }
        return list;
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Code colours");

        test("the colour pieces are exactly the lexer's tokens on every starter-kit / system / sample file", () =>
        {
            var corpus = Corpus();
            True(corpus.Count >= 15, $"only {corpus.Count} .bs files found");
            int files = 0;
            foreach (var (name, src) in corpus)
            {
                var comments = new List<Comment>();
                var tokens = Lexer.Tokenize(src, comments).Where(t => t.Kind is Tok.Ident or Tok.Number or Tok.String or Tok.Op).ToList();
                var pieces = new List<(int Line, Tok Kind, string Text)>();
                var found = new List<(int Line, string Text)>();
                var lines = src.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                var ctx = SyntaxContext.For(src);
                for (int i = 0; i < lines.Length; i++)
                    foreach (var s in SyntaxColors.Classify(lines[i], ctx))
                    {
                        string text = lines[i].Substring(s.Start, s.Length);
                        if (s.Class == CodeClass.Comment) { found.Add((i + 1, text.TrimEnd())); continue; }
                        True(s.Class != CodeClass.Plain, $"{name} line {i + 1}: '{text}' has no colour");
                        if (s.Class == CodeClass.String) pieces.Add((i + 1, Tok.String, ""));
                        else if (s.Class == CodeClass.Number) pieces.Add((i + 1, Tok.Number, text));
                        else if (s.Class == CodeClass.Operator) pieces.Add((i + 1, Tok.Op, text));
                        else pieces.Add((i + 1, Tok.Ident, text));
                    }
                Eq(tokens.Count, pieces.Count, $"{name}: number of pieces");
                for (int k = 0; k < tokens.Count; k++)
                {
                    var t = tokens[k];
                    var p = pieces[k];
                    string want = t.Kind == Tok.String ? "" : t.Text;
                    Eq($"{t.Line} {t.Kind} {want}", $"{p.Line} {p.Kind} {p.Text}", $"{name}: piece {k}");
                }
                Eq(string.Join("\n", comments.Select(c => $"{c.Line} {c.Text}")), string.Join("\n", found.Select(c => $"{c.Line} {c.Text}")), $"{name}: comments");
                files++;
            }
            True(files == corpus.Count, "every file checked");
        });

        test("keywords: control flow purple, declarations and connectors blue, constants blue", () =>
        {
            foreach (var w in new[] { "when", "if", "else", "repeat", "while", "together", "run", "wait", "return", "stop", "emit", "tell" })
                Is(CodeClass.Control, $"    {w} x", w);
            Is(CodeClass.Control, "        for each e in all(\"enemy\")", "for");
            Is(CodeClass.Control, "        for each e in all(\"enemy\")", "each");
            Is(CodeClass.Control, "        for each e in all(\"enemy\")", "in");
            Is(CodeClass.Function, "        for each e in all(\"enemy\")", "all");
            Is(CodeClass.Variable, "        for each e in all(\"enemy\")", "e");
            Is(CodeClass.Control, "        wait until hp <= 0", "until");
            Is(CodeClass.Control, "        repeat forever", "forever");
            Is(CodeClass.Control, "        wait 2 seconds", "seconds");
            Is(CodeClass.Control, "        repeat 3 times", "times");
            Is(CodeClass.Variable, "        wait seconds", "seconds"); // a param called seconds is a name, not the unit
            Is(CodeClass.Declaration, "mechanism Coin", "mechanism");
            Is(CodeClass.Declaration, "system Inventory", "system");
            Is(CodeClass.Declaration, "    param value: number = 1", "param");
            Is(CodeClass.Declaration, "    uses Health max 2", "uses");
            Is(CodeClass.Declaration, "    includes Health", "includes");
            Is(CodeClass.Declaration, "    to attack target", "to");
            Is(CodeClass.Declaration, "mechanism RedSlime from Slime", "from");
            Is(CodeClass.Declaration, "        move Boss to 400, 300 in 2 seconds ease out", "to");
            Is(CodeClass.Declaration, "        move Boss to 400, 300 in 2 seconds ease out", "in");
            Is(CodeClass.Declaration, "        move Boss to 400, 300 in 2 seconds ease out", "ease");
            Is(CodeClass.Declaration, "    when touched by \"player\"", "by");
            Is(CodeClass.Declaration, "        if not ready and hp > 0 or done", "not");
            Is(CodeClass.Declaration, "        if not ready and hp > 0 or done", "and");
            Is(CodeClass.Declaration, "        global power, night", "global");
            foreach (var w in new[] { "true", "false", "nothing", "none", "self", "other" })
                Is(CodeClass.Constant, $"        x = {w}", w);
        });

        test("strings, numbers, comments: a # inside a text is not a comment, a comment's quotes are not a text", () =>
        {
            Eq("print:Function \"score # not a comment\":String", Show("print \"score # not a comment\""));
            Eq("say:Function 'it\\'s # fine':String", Show("say 'it\\'s # fine'"));
            Eq("x:Variable =:Operator \"a \\\" b # c\":String", Show("x = \"a \\\" b # c\""));
            Eq("when:Control touched:Variable by:Declaration \"player\":String # a \"quoted\" # note:Comment", Show("when touched by \"player\"  # a \"quoted\" # note"));
            Eq("# just a comment, with \"quotes\" and 'more':Comment", Show("    # just a comment, with \"quotes\" and 'more'"));
            Eq("x:Variable =:Operator 1e3:Number +:Operator 2.5:Number *:Operator .5:Number -:Operator 1E-4:Number", Show("x = 1e3 + 2.5 * .5 - 1E-4"));
            Eq("print:Function \"\":String", Show("print \"\""));
            Is(CodeClass.Number, "move 10, -3", "3");
            Is(CodeClass.Variable, "x = 3 + times", "times"); // the unit only right after a number
        });

        test("while typing: an unterminated text runs to the end of the line, odd symbols never throw", () =>
        {
            var spans = SyntaxColors.Classify("    print \"hello wor");
            Eq(CodeClass.String, spans[^1].Class);
            Eq("    print \"hello wor".Length, spans[^1].Start + spans[^1].Length, "the text reaches the end");
            var raw = new List<RawSpan>();
            Lexer.ScanLine("say 'half # still text", raw);
            True(raw[^1].Kind == RawKind.String && raw[^1].Open, "an open text is marked");
            Eq("x:Variable =:Operator @:Plain 1:Number", Show("x = @ 1"));
            Eq("x:Variable =:Operator 😀:Plain", Show("x = 😀"), "an emoji is one piece");
            Eq("print:Function \"a\\\\\":String", Show("print \"a\\\\\""), "an escaped backslash before the closing quote");
            Eq("", Show(""));
            Eq("", Show("        "));
            Eq("print:Function 1:Number", Show("\tprint 1"));
            Eq("x:Variable =:Operator \"\\:String", Show("x = \"\\"), "a lone backslash at the end stays in the open text");
            foreach (var junk in new[] { "\"", "'", "\\", "(((", "]]]", "#", "1.2.3.4", "1e", "1e+", "_", "é = 1", "x = \"\t\"" })
                SyntaxColors.Classify(junk); // never throws
        });

        test("names: mechanisms and types teal, commands and calls yellow, variables and members light blue", () =>
        {
            Is(CodeClass.Type, "mechanism RedSlime from Slime", "RedSlime");
            Is(CodeClass.Type, "mechanism RedSlime from Slime", "Slime");
            Is(CodeClass.Type, "system Inventory", "Inventory");
            Is(CodeClass.Type, "    uses Health max 2", "Health");
            Is(CodeClass.Variable, "    uses Health max 2", "max");
            Is(CodeClass.Variable, "    param loot: List of image = [\"a.png\"]", "loot");
            foreach (var w in new[] { "List", "of", "image" }) Is(CodeClass.Type, "    param loot: List of image = [\"a.png\"]", w);
            Is(CodeClass.Type, "    param target: Object", "Object");
            Is(CodeClass.Type, "    param speed: number = 100", "number");
            Is(CodeClass.Type, "    param name: text = \"Imp\"  # a comment", "text");
            var ctx = SyntaxContext.For("", ["Health", "Tools"]);
            Is(CodeClass.Type, "        wait for other.Health.died", "Health", ctx);
            Is(CodeClass.Variable, "        wait for other.Health.died", "died", ctx);
            Is(CodeClass.Constant, "        wait for other.Health.died", "other", ctx);
            Is(CodeClass.Type, "    when Health.died", "Health", ctx);
            Is(CodeClass.Variable, "        x = find(\"Turns\").TurnManager.turn", "TurnManager");
            Is(CodeClass.Function, "        x = find(\"Turns\").TurnManager.turn", "find");
            Is(CodeClass.Variable, "        self.vx = 0", "vx");
            Is(CodeClass.Variable, "        score += value", "score");
            Is(CodeClass.Variable, "        score += value", "value");
            Is(CodeClass.Variable, "        play = 1", "play", null); // a variable called like a command
            foreach (var c in new[] { "print \"hi\"", "destroy self", "spawn \"Coin\"", "play \"coin.wav\"", "notify \"Saved\"", "say Boss \"Hi\"", "goto_scene \"Two\"" })
                Is(CodeClass.Function, "        " + c, c.Split(' ')[0]);
            Is(CodeClass.Function, "        camera follow Boss", "camera");
            Is(CodeClass.Function, "        camera follow Boss", "follow");
            Is(CodeClass.Variable, "        camera follow Boss", "Boss");
            Is(CodeClass.Function, "        stop sound \"rain.wav\"", "stop");
            Is(CodeClass.Function, "        stop sound \"rain.wav\"", "sound");
            Is(CodeClass.Control, "        stop", "stop");
            Is(CodeClass.Function, "        n = max(1, round(d / 2))", "max");
            Is(CodeClass.Function, "        n = max(1, round(d / 2))", "round");
            Is(CodeClass.Control, "        if hp <= 0: stop", "stop");
            Is(CodeClass.Function, "        if not find(copy_of): ground = \"Solid\"", "find");
            Is(CodeClass.Variable, "        if not find(copy_of): ground = \"Solid\"", "ground");
            Is(CodeClass.Function, "        if hit: print \"ouch\"", "print");
            Is(CodeClass.Variable, "        card = {name: \"Imp\", cost: 1}", "name");
        });

        test("the mechanism's own commands ('to') are yellow where they are made and where they are used", () =>
        {
            const string src = "mechanism Boss\n    when start\n        slide_to 400, 1\n        run taunt\n        x = damage_for(3)\n    to slide_to x, seconds\n        wait seconds\n    to taunt\n        say \"Ha\"\n    to damage_for level\n        return level * 2\n";
            var ctx = SyntaxContext.For(src);
            True(ctx.Procs.SetEquals(["slide_to", "taunt", "damage_for"]), "procs: " + string.Join(",", ctx.Procs));
            True(ctx.Mechanisms.Contains("Boss"), "the mechanism in the text is known");
            Is(CodeClass.Function, "        slide_to 400, 1", "slide_to", ctx);
            Is(CodeClass.Function, "        run taunt", "taunt", ctx);
            Is(CodeClass.Function, "        x = damage_for(3)", "damage_for", ctx);
            Is(CodeClass.Function, "    to slide_to x, seconds", "slide_to", ctx);
            Is(CodeClass.Variable, "    to slide_to x, seconds", "x", ctx);
            Is(CodeClass.Variable, "    to slide_to x, seconds", "seconds", ctx);
            Is(CodeClass.Variable, "        slide_to 400, 1", "slide_to", SyntaxContext.Empty); // not made here: just a name
            Is(CodeClass.Function, "        Tools.flash 2", "flash", SyntaxContext.For("    to flash n\n"));
            True(SyntaxContext.For(src).SameAs(ctx), "same text, same context");
            True(!SyntaxContext.For(src + "    to more\n").SameAs(ctx), "a new command changes the context");
        });

        test("a 5,000-line mechanism colours fast (only visible lines are coloured, but even all of them is quick)", () =>
        {
            var sb = new System.Text.StringBuilder("mechanism Big\n    param speed: number = 100\n");
            for (int i = 0; i < 1250; i++)
                sb.Append($"    when key \"k{i}\"  # key {i}\n        if hp > {i}: print \"hit # {i}\"\n        camera shake 4 for 0.5\n        do_{i % 7} self.x + 1e3, other.Health.max\n");
            string src = sb.ToString();
            var watch = Stopwatch.StartNew();
            var ctx = SyntaxContext.For(src, ["Health"]);
            int pieces = 0;
            foreach (var (_, _, spans) in SyntaxColors.ClassifyLines(src, ctx)) pieces += spans.Count;
            watch.Stop();
            True(pieces > 30000, $"{pieces} pieces");
            True(watch.ElapsedMilliseconds < 1500, $"took {watch.ElapsedMilliseconds} ms");
        });
    }
}
