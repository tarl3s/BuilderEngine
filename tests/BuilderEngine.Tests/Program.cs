using BuilderEngine.Script;

// Tiny test runner: no NuGet packages. Exit code = number of failures.
int pass = 0, fail = 0;
void Test(string name, Action body)
{
    try { body(); pass++; Console.WriteLine($"  ok   {name}"); }
    catch (Exception e) { fail++; Console.WriteLine($"  FAIL {name}: {e.Message}"); }
}
void Eq<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what} expected <{expected}> but got <{actual}>");
}
void Throws(string fragment, Action body)
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
World NewWorld(string src)
{
    var w = new World();
    StarterKit.LoadInto(w);
    if (src.Length > 0) w.Load(src);
    return w;
}
void Run(World w, double seconds, double dt = 1.0 / 60)
{
    for (double t = 0; t < seconds - 1e-9; t += dt) w.Step(dt);
}

Console.WriteLine("Lexer / parser");
Test("indentation becomes blocks", () =>
{
    var m = Parser.Parse("mechanism A\n    when start\n        x = 1\n        y = 2\n")[0];
    Eq(1, m.Whens.Count); Eq(2, m.Whens[0].Body.Count);
});
Test("params, types and defaults", () =>
{
    var m = Parser.Parse("mechanism A\n    param speed: number = 100\n    param s: Sound = \"a.wav\"\n    param free\n")[0];
    Eq(3, m.Params.Count); Eq("number", m.Params[0].Type); Eq("any", m.Params[2].Type);
});
Test("uses with positional and named args", () =>
{
    var m = Parser.Parse("mechanism A\n    uses Sprite \"s.png\"\n    uses Health max 2\n")[0];
    Eq(2, m.Uses.Count); Eq(true, m.Uses[0].Positional is StringExpr); Eq(true, m.Uses[1].Args.ContainsKey("max"));
});
Test("event shapes", () =>
{
    var m = Parser.Parse("mechanism A\n    when touched by \"player\": x = 1\n    when touched by other: x = 1\n    when told \"damage\" n: x = 1\n    when key \"space\": x = 1\n    when Health.died: x = 1\n    when died: x = 1\n")[0];
    Eq("player", m.Whens[0].Event.Filter); Eq("other", m.Whens[1].Event.Bind);
    Eq("damage", m.Whens[2].Event.Filter); Eq("n", m.Whens[2].Event.Bind);
    Eq("space", m.Whens[3].Event.Filter);
    Eq("Health", m.Whens[4].Event.Source); Eq(null, m.Whens[5].Event.Source);
});
Test("one-line if and else if", () =>
{
    var m = Parser.Parse("mechanism A\n    when start\n        if a: stop\n        if b\n            x = 1\n        else if c\n            x = 2\n        else\n            x = 3\n")[0];
    Eq(2, m.Whens[0].Body.Count);
});
Test("operator precedence", () =>
{
    var w = NewWorld("mechanism A\n    when start\n        r = 2 + 3 * 4 - (1 + 1)\n        b = not 1 > 2 and 3 >= 3\n");
    var o = w.Spawn("o", 0, 0, ["A"]); w.Step(0.016);
    Eq(12.0, o.Mechanisms[0].Vars["r"]); Eq(true, o.Mechanisms[0].Vars["b"]);
});
Test("comments and blank lines are ignored", () =>
{
    Eq(1, Parser.Parse("# hi\n\nmechanism A  # trailing\n\n    # inside\n    when start\n        x = 1\n").Count);
});
Test("friendly error: bad indentation", () => Throws("indentation", () => Parser.Parse("mechanism A\n    when start\n        x = 1\n      y = 2\n")));
Test("friendly error: missing quote", () => Throws("closing quote", () => Parser.Parse("mechanism A\n    when start\n        play \"x\n")));
Test("friendly error: stray line in mechanism", () => Throws("'param', 'uses' or 'when'", () => Parser.Parse("mechanism A\n    x = 1\n")));
Test("friendly error: has the line number", () =>
{
    try { Parser.Parse("mechanism A\n    when start\n        x = )\n"); throw new Exception("no error"); }
    catch (ScriptError e) { Eq(3, e.Line); }
});
Test("friendly error: duplicate mechanism", () => Throws("two mechanisms", () => Parser.Parse("mechanism A\nmechanism A\n")));

Console.WriteLine("Runtime");
Test("coin: touched by player adds score, plays sound, disappears", () =>
{
    var w = NewWorld("");
    w.Globals["score"] = 0.0;
    var coin = w.Spawn("Coin", 100, 0, ["Coin"]);
    var player = w.Spawn("Player", 0, 0, null, null, "player");
    w.Step(0.016);
    Eq(0.0, w.Globals["score"]);
    player.X = 100; w.Step(0.016);
    Eq(1.0, w.Globals["score"]); Eq(true, coin.Destroyed); Eq(true, w.Output.Contains("♪ coin.wav"));
    Eq(false, w.Objects.Contains(coin));
});
Test("touch fires once per contact, not every frame", () =>
{
    var w = NewWorld("mechanism Count\n    when touched by other\n        n += 1\n");
    var a = w.Spawn("a", 0, 0, ["Count"]); w.Spawn("b", 0, 0);
    Run(w, 1);
    Eq(1.0, a.Mechanisms[0].Vars["n"]);
});
Test("param overrides", () =>
{
    var w = NewWorld(""); w.Globals["score"] = 0.0;
    w.Spawn("Coin", 0, 0, ["Coin"], new() { ["Coin"] = new() { ["value"] = 5.0 } });
    w.Spawn("P", 0, 0, null, null, "player");
    w.Step(0.016);
    Eq(5.0, w.Globals["score"]);
});
Test("wait pauses only that when", () =>
{
    var w = NewWorld("mechanism T\n    when start\n        a = 1\n        wait 1\n        a = 2\n    when start\n        b = 1\n");
    var o = w.Spawn("o", 0, 0, ["T"]);
    w.Step(0.1);
    Eq(1.0, o.Mechanisms[0].Vars["a"]); Eq(1.0, o.Mechanisms[0].Vars["b"]);
    Run(w, 0.5); Eq(1.0, o.Mechanisms[0].Vars["a"]);
    Run(w, 0.6); Eq(2.0, o.Mechanisms[0].Vars["a"]);
});
Test("repeat N and repeat forever with wait", () =>
{
    var w = NewWorld("mechanism R\n    when start\n        repeat 3\n            n += 1\n        repeat forever\n            m += 1\n            wait 0.5\n");
    var o = w.Spawn("o", 0, 0, ["R"]);
    Run(w, 2.1, 0.05);
    Eq(3.0, o.Mechanisms[0].Vars["n"]);
    Eq(5.0, o.Mechanisms[0].Vars["m"]);
});
Test("repeat forever without wait runs once per frame (no freeze)", () =>
{
    var w = NewWorld("mechanism F\n    when start\n        repeat forever\n            n += 1\n");
    var o = w.Spawn("o", 0, 0, ["F"]);
    Run(w, 10.0 / 60);
    Eq(true, (double)o.Mechanisms[0].Vars["n"]! is >= 9 and <= 11, "frames");
});
Test("health: damage, invincibility, died signal reaches the user", () =>
{
    var w = NewWorld("mechanism Slime\n    uses Health max 2\n    when Health.died\n        print \"slime died\"\n        destroy self\n    when touched by other\n        tell other \"damage\" 1\n");
    var slime = w.Spawn("Slime", 0, 0, ["Slime"]);
    w.Step(0.016);
    var hp = slime.Mechanisms[0].Children["Health"];
    Eq(2.0, hp.Vars["hp"]);
    w.Spawn("Hero", 500, 0); w.Step(0.016);
    var hero = w.FindObject("Hero")!;
    w.Spawn("Sword", 0, 0); // a sword appears on the slime
    w.Step(0.016);
    w.Tell(slime, "damage", [1.0]); w.Step(0.016);
    Eq(1.0, hp.Vars["hp"]);
    w.Tell(slime, "damage", [1.0]); w.Step(0.016);
    Eq(1.0, hp.Vars["hp"], "invincible");
    Run(w, 1.1);
    w.Tell(slime, "damage", [1.0]); w.Step(0.016);
    Eq(true, slime.Destroyed); Eq(true, w.Output.Contains("slime died"));
    Eq(false, hero.Destroyed);
});
Test("touched-by binding: slime damages whoever touches it", () =>
{
    var w = NewWorld("mechanism Slime\n    when touched by other\n        tell other \"damage\" 1\nmechanism Hero\n    uses Health max 3\n");
    w.Spawn("S", 0, 0, ["Slime"]);
    var hero = w.Spawn("H", 0, 0, ["Hero"]);
    w.Step(0.016);
    Eq(2.0, hero.Mechanisms[0].Children["Health"].Vars["hp"]);
});
Test("emit own signal", () =>
{
    var w = NewWorld("mechanism E\n    when start\n        emit ping\n    when ping\n        got = true\n");
    var o = w.Spawn("o", 0, 0, ["E"]); w.Step(0.016);
    Eq(true, o.Mechanisms[0].Vars["got"]);
});
Test("spawn creates a new object with that mechanism", () =>
{
    var w = NewWorld("mechanism Drop\n    when start\n        spawn Coin here\n");
    w.Spawn("d", 40, 50, ["Drop"]); w.Step(0.016); w.Step(0.016);
    var c = w.FindObject("Coin")!;
    Eq(40.0, c.X); Eq("Coin", c.Mechanisms[0].Def.Name);
});
Test("key event", () =>
{
    var w = NewWorld("mechanism J\n    when key \"space\"\n        jumps += 1\n");
    var o = w.Spawn("o", 0, 0, ["J"]); w.Step(0.016);
    w.PressKey("Space"); w.PressKey("a");
    Eq(1.0, o.Mechanisms[0].Vars["jumps"]);
});
Test("tick runs each step; object position is writable", () =>
{
    var w = NewWorld("mechanism Mover\n    when tick\n        self.x += 1\n");
    var o = w.Spawn("o", 0, 0, ["Mover"]);
    Run(w, 10.0 / 60);
    Eq(true, o.X is >= 9 and <= 11, "x");
});
Test("patrol from the starter kit moves back and forth", () =>
{
    var w = NewWorld("");
    var o = w.Spawn("p", 0, 0, ["Patrol"]);
    double maxX = 0;
    for (int i = 0; i < 60 * 4; i++) { w.Step(1.0 / 60); maxX = Math.Max(maxX, o.X); }
    Eq(true, maxX > 90 && maxX < 110, $"max x {maxX}");
    Eq(true, o.X < maxX, "came back");
});
Test("text joins with +", () =>
{
    var w = NewWorld("mechanism T\n    when start\n        print \"hp: \" + 3\n");
    w.Spawn("o", 0, 0, ["T"]); w.Step(0.016);
    Eq("hp: 3", w.Output[0]);
});
Test("functions", () =>
{
    var w = NewWorld("mechanism F\n    when start\n        a = max(2, 7)\n        r = random(1, 6)\n");
    var o = w.Spawn("o", 0, 0, ["F"]); w.Step(0.016);
    Eq(7.0, o.Mechanisms[0].Vars["a"]);
    Eq(true, (double)o.Mechanisms[0].Vars["r"]! is >= 1 and <= 6);
});
Test("runtime error is reported, game keeps going", () =>
{
    var w = NewWorld("mechanism Bad\n    when start\n        jmup 3\n    when start\n        ok = true\n");
    var o = w.Spawn("o", 0, 0, ["Bad"]); w.Step(0.016);
    Eq(1, w.Errors.Count);
    Eq(true, w.Errors[0].Contains("line 3") && w.Errors[0].Contains("no command"), w.Errors[0]);
    Eq(true, o.Mechanisms[0].Vars["ok"]);
});
Test("did-you-mean for unknown command", () =>
{
    var w = NewWorld("mechanism Bad\n    when start\n        destory self\n");
    w.Spawn("o", 0, 0, ["Bad"]); w.Step(0.016);
    Eq(true, w.Errors[0].Contains("Did you mean 'destroy'"), w.Errors[0]);
});
Test("unknown mechanism in uses", () =>
{
    var w = NewWorld("mechanism A\n    uses Helth\n");
    Throws("Did you mean 'Health'", () => w.Spawn("o", 0, 0, ["A"]));
});
Test("unknown param in uses", () =>
{
    var w = NewWorld("mechanism A\n    uses Health maxx 2\n");
    Throws("no param called 'maxx'", () => w.Spawn("o", 0, 0, ["A"]));
});
Test("division by zero is a friendly error", () =>
{
    var w = NewWorld("mechanism D\n    when start\n        x = 1 / 0\n");
    w.Spawn("o", 0, 0, ["D"]); w.Step(0.016);
    Eq(true, w.Errors[0].Contains("Division by zero"));
});
Test("starter kit parses", () =>
{
    foreach (var (file, src) in StarterKit.Sources()) Parser.Parse(src);
});

// ---------------- Printer and block edits ----------------

// A dump of the tree that ignores line numbers, written independently of the printer.
string DumpE(Expr? e) => e switch
{
    null => "_",
    NumberExpr n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    StringExpr s => "'" + s.Value + "'",
    BoolExpr b => b.Value ? "T" : "F",
    NameExpr n => n.Full,
    UnaryExpr u => $"({u.Op} {DumpE(u.Operand)})",
    BinaryExpr b => $"({b.Op} {DumpE(b.Left)} {DumpE(b.Right)})",
    CallExpr c => $"{c.Name}[{string.Join(" ", c.Args.Select(DumpE))}]",
    ListExpr l => $"<list {string.Join(" ", l.Items.Select(DumpE))}>",
    IndexExpr x => $"(at {DumpE(x.Target)} {DumpE(x.Index)})",
    _ => "?"
};
string DumpL(List<Stmt>? list) => list == null ? "null" : "{" + string.Join("; ", list.Select(DumpS)) + "}";
string DumpS(Stmt s) => s switch
{
    AssignStmt a => $"{string.Join('.', a.Target)} {a.Op} {DumpE(a.Value)}",
    IfStmt i => $"if {DumpE(i.Cond)} {DumpL(i.Then)} else {DumpL(i.Else)}",
    RepeatStmt r => $"repeat {DumpE(r.Count)} {DumpL(r.Body)}",
    WaitStmt w => $"wait {DumpE(w.Seconds)}",
    EmitStmt e => $"emit {e.Signal} [{string.Join(" ", e.Args.Select(DumpE))}]",
    TellStmt t => $"tell {DumpE(t.Target)} {DumpE(t.Message)} [{string.Join(" ", t.Args.Select(DumpE))}]",
    StopStmt => "stop",
    CommandStmt c => $"cmd {c.Name} [{string.Join(" ", c.Args.Select(DumpE))}]" +
        (c.Named == null ? "" : string.Concat(c.Named.Select(kv => $" {kv.Key}:[{string.Join(" ", kv.Value.Select(DumpE))}]"))),
    WaitUntilStmt u => $"waituntil {DumpE(u.Cond)}",
    WaitSignalStmt f => $"waitfor {f.Full}",
    WaitKeyStmt k => $"waitkey {DumpE(k.Key)}",
    TogetherStmt t => $"together {DumpL(t.Body)}",
    RunStmt r => $"run {DumpL(r.Body)}",
    ForEachStmt f => $"foreach {f.Var} {DumpE(f.List)} {DumpL(f.Body)}",
    WhileStmt w => $"while {DumpE(w.Cond)} {DumpL(w.Body)}",
    ReturnStmt r => $"return {DumpE(r.Value)}",
    _ => "?"
};
string Dump(List<MechanismDef> defs) => string.Join("\n", defs.Select(m =>
    $"M {m.Name}\n" +
    string.Concat(m.Params.Select(p => $" P {p.Name}:{p.Type}={DumpE(p.Default)}\n")) +
    string.Concat(m.Uses.Select(u => $" U {u.Name} {DumpE(u.Positional)} {string.Join(",", u.Args.Select(kv => kv.Key + "=" + DumpE(kv.Value)))}\n")) +
    string.Concat(m.Whens.Select(w => $" W {w.Event.Kind}|{w.Event.Filter}|{w.Event.Bind}|{w.Event.Source}|{w.Event.Name} {DumpL(w.Body)}\n")) +
    string.Concat(m.Procs.Select(p => $" TO {p.Name}({string.Join(",", p.Params)}) {DumpL(p.Body)}\n"))));
void RoundTrip(string src)
{
    var first = Parser.Parse(src);
    string printed = Printer.Print(first);
    List<MechanismDef> second;
    try { second = Parser.Parse(printed); }
    catch (ScriptError e) { throw new Exception($"printed code doesn't parse ({e}):\n{printed}"); }
    if (Dump(first) != Dump(second)) throw new Exception($"tree changed after printing:\n{printed}\n--- before\n{Dump(first)}\n--- after\n{Dump(second)}");
    Eq(printed, Printer.Print(second), "printing is stable");
}
string M(string body) => "mechanism T\n    when start\n" + string.Join("", body.Split('\n').Select(l => "        " + l + "\n"));

Console.WriteLine();
Console.WriteLine("Printer / blocks");
Test("round-trip: starter kit", () =>
{
    foreach (var (_, src) in StarterKit.Sources()) RoundTrip(src);
});
Test("round-trip: nested if / else if / else", () => RoundTrip(M("if a\n    if b: x = 1\n    else\n        x = 2\nelse if c\n    repeat 2\n        y += 1\nelse if d: stop\nelse\n    z = 3")));
Test("round-trip: repeat forever and repeat n times", () => RoundTrip(M("repeat forever\n    repeat 3 times\n        wait 0.5 seconds\n    wait 1")));
Test("round-trip: precedence", () => RoundTrip(M(
    "a = -(a + b) * c\nb = not (a and b)\nc = (not a) == b\nd = a - (b - c)\ne = (a - b) - c\nf = a / (b * c)\ng = -x % 3\n" +
    "h = a or b and c\ni = (a or b) and c\nj = - -x\nk = a + (not b)\nl = (a == b) == c\nm = a == (b == c)\nn = min(a + 1, -b) * 2")));
Test("round-trip: strings with quotes and backslashes", () => RoundTrip(M("print \"say \\\"hi\\\"\"\nprint 'it\\'s'\nprint \"back\\\\slash\"\nprint \"two\\nlines\"")));
Test("round-trip: uses with positional and named args", () => RoundTrip(
    "mechanism T\n    uses Sprite \"s.png\"\n    uses Health max 2\n    uses Body gravity: 300, solid = true\n    uses Sprite \"a.png\" size 2\n    uses Plain\n    uses Weird (hp) max -1\n"));
Test("round-trip: every event shape", () => RoundTrip(
    "mechanism T\n    when start: x = 1\n    when tick: x = 1\n    when touched by \"player\": x = 1\n    when touched by other: x = 1\n    when touched: x = 1\n" +
    "    when told \"damage\" n: x = 1\n    when told \"hi\": x = 1\n    when key \"space\": x = 1\n    when key left: x = 1\n    when Health.died: x = 1\n    when died: x = 1\n"));
Test("round-trip: commands, emit and tell", () => RoundTrip(M(
    "move -speed / 60 * 10, 0\nmove 1 2\nspawn Coin here\ndestroy\nemit hurt\nemit hit 2, -3\ntell other \"damage\" 1\ntell other \"damage\", -1\ntell (a) (-x)\ntell (a) (b + c) * 2\ntell other msg, (x), y\nprint (a + b) * 2, -1")));
Test("round-trip: several mechanisms and empty ones", () => RoundTrip("mechanism A\nmechanism B\n    param p\n    param q: text = \"x\"\nmechanism C\n    when start\n        stop\n"));
Test("empty blocks print with a colon and parse back", () =>
{
    var m = Parser.Parse("mechanism T\n    when start:\n    when tick\n        if a:\n        else:\n        repeat forever:\n")[0];
    Eq(0, m.Whens[0].Body.Count);
    var i = (IfStmt)m.Whens[1].Body[0];
    Eq(0, i.Then.Count); Eq(0, i.Else!.Count);
    Eq(0, ((RepeatStmt)m.Whens[1].Body[1]).Body.Count);
    RoundTrip("mechanism T\n    when start:\n    when tick\n        if a:\n        else:\n        repeat forever:\n");
});
Test("a missing indent is still an error without the colon", () => Throws("indented", () => Parser.Parse("mechanism T\n    when start\n    when tick\n        x = 1\n")));
Test("printer: exact canonical form", () =>
{
    string src = "mechanism   Coin\n  param value : number=1\n  uses Sprite 'coin.png'\n  when touched by 'player'   # comment\n    score+=value\n    if score>=10 : emit won\n    play  \"coin.wav\"\n    destroy self\n  when told \"x\" v\n    repeat 2 times\n      wait (1)\n";
    Eq("mechanism Coin\n    param value: number = 1\n    uses Sprite \"coin.png\"\n\n    when touched by \"player\"\n        score += value\n        if score >= 10: emit won\n        play \"coin.wav\"\n        destroy self\n\n    when told \"x\" v\n        repeat 2\n            wait 1\n",
        Printer.Print(Parser.Parse(src)));
});
Test("printer: tricky values get the parentheses they need", () =>
{
    var m = Parser.Parse("mechanism T\n    when start\n        x = 1\n")[0];
    var body = m.Whens[0].Body;
    body.Clear();
    body.Add(new TellStmt(new NameExpr(["a"], 0), new UnaryExpr("-", new NameExpr(["x"], 0), 0), [], 0));
    body.Add(new TellStmt(new NameExpr(["other"], 0), new StringExpr("d", 0), [new UnaryExpr("-", new NumberExpr(1, 0), 0), new NameExpr(["y"], 0)], 0));
    body.Add(new RepeatStmt(new NameExpr(["forever"], 0), [], 0));
    m.Uses.Add(new UsesDef("U", new NameExpr(["hp"], 0), new Dictionary<string, Expr> { ["max"] = new NumberExpr(2, 0) }, 0));
    RoundTrip(Printer.Print([m]));
});
Test("printer: numbers in invariant culture without trailing .0", () =>
{
    var old = System.Globalization.CultureInfo.CurrentCulture;
    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
    try
    {
        Eq("0.5", Printer.Print(new NumberExpr(0.5, 0)));
        Eq("3", Printer.Print(new NumberExpr(3.0, 0)));
        Eq("100000000000000000000", Printer.Print(new NumberExpr(1e20, 0)));
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = old; }
});
Test("parse expression and value lists", () =>
{
    Eq("(+ a (* 2 b))", DumpE(Parser.ParseExpression(" a + 2*b ")));
    Throws("didn't expect", () => Parser.ParseExpression("a b"));
    Throws("value", () => Parser.ParseExpression("  "));
    Throws("expected", () => Parser.ParseExpression("(a"));
    Eq(2, Parser.ParseExpressionList("10, -2").Count);
    Eq(0, Parser.ParseExpressionList("").Count);
});
Test("block edits: insert, delete, move", () =>
{
    var list = new List<Stmt> { new StopStmt(0), BlockEdits.NewStatement("wait") };
    BlockEdits.Insert(list, 99, BlockEdits.NewStatement("print"));
    Eq("{stop; wait 1; cmd print ['Hello!']}", DumpL(list));
    Eq(true, BlockEdits.Move(list, 2, -1));
    Eq("{stop; cmd print ['Hello!']; wait 1}", DumpL(list));
    Eq(false, BlockEdits.Move(list, 0, -1));
    Eq(false, BlockEdits.Move(list, 2, 1));
    Eq(true, BlockEdits.Delete(list, 0));
    Eq(false, BlockEdits.Delete(list, 5));
    Eq("{cmd print ['Hello!']; wait 1}", DumpL(list));
});
Test("block edits: duplicate is a deep copy", () =>
{
    var m = Parser.Parse(M("if a\n    x = 1\nelse\n    repeat 2\n        y = 1"))[0];
    var body = m.Whens[0].Body;
    BlockEdits.Duplicate(body, 0);
    Eq(2, body.Count);
    ((RepeatStmt)((IfStmt)body[1]).Else![0]).Body.Add(new StopStmt(0));
    Eq(1, ((RepeatStmt)((IfStmt)body[0]).Else![0]).Body.Count, "original untouched");
    BlockEdits.Duplicate(m.Whens, 0);
    m.Whens[1].Body.Clear();
    Eq(2, m.Whens[0].Body.Count, "duplicated when has its own body");
});
Test("block edits: every palette block prints and parses", () =>
{
    var m = Parser.Parse("mechanism T\n")[0];
    var w = BlockEdits.NewWhen(EventKind.Start);
    m.Whens.Add(w);
    foreach (var (id, _) in BlockEdits.StatementKinds) w.Body.Add(BlockEdits.NewStatement(id));
    foreach (var k in BlockEdits.EventKinds) m.Whens.Add(BlockEdits.NewWhen(k));
    m.Params.Add(BlockEdits.NewParam(m));
    m.Params.Add(BlockEdits.NewParam(m));
    Eq("speed2", m.Params[1].Name);
    m.Uses.Add(BlockEdits.NewUses());
    RoundTrip(Printer.Print([m]));
    Eq(BlockEdits.StatementKinds.Length, Parser.Parse(Printer.Print([m]))[0].Whens[0].Body.Count);
});
Test("block edits: names and fields", () =>
{
    Eq(null, BlockEdits.CheckName("hp"));
    Eq(null, BlockEdits.CheckName("self.x", dotted: true));
    Eq(true, BlockEdits.CheckName("self.x") != null);
    Eq(true, BlockEdits.CheckName("2x") != null);
    Eq(true, BlockEdits.CheckName("wait") != null);
    Eq(true, BlockEdits.CheckName("") != null);
    var told = BlockEdits.ParseEvent(EventKind.Told, "\"damage\" amount");
    Eq("damage", told.Filter); Eq("amount", told.Bind);
    Eq("Health", BlockEdits.ParseEvent(EventKind.Signal, "Health.died").Source);
    Eq("player", BlockEdits.ParseEvent(EventKind.Touched, "\"player\"").Filter);
    Eq(null, BlockEdits.ParseEvent(EventKind.Touched, "").Filter);
    Throws("not a signal", () => BlockEdits.ParseEvent(EventKind.Signal, "start"));
    Throws("quotes", () => BlockEdits.ParseEvent(EventKind.Told, ""));
    var (pos, args) = BlockEdits.ParseUsesTail("\"a.png\" size 2");
    Eq(true, pos is StringExpr); Eq(true, args.ContainsKey("size"));
    Eq("\"a.png\", size 2", Printer.UsesTail(new UsesDef("Sprite", pos, args, 0)));
    Eq("touched by", BlockEdits.EventLabel(EventKind.Touched));
});

// ---- Blocks: drag and drop, selection paths, undo ----
Test("block drag: move into a nested body and out again", () =>
{
    var m = Parser.Parse(M("print 1\nif a\n    x = 1\nprint 2"))[0];
    var body = m.Whens[0].Body;
    var then = ((IfStmt)body[1]).Then;
    Eq(true, BlockEdits.MoveStatement(body, 0, then, 1));
    Eq("{if a {x = 1; cmd print [1]} else null; cmd print [2]}", DumpL(body));
    Eq(true, BlockEdits.MoveStatement(then, 0, body, 2));
    Eq("{if a {cmd print [1]} else null; cmd print [2]; x = 1}", DumpL(body));
    Eq(true, BlockEdits.MoveStatement(body, 2, body, 0), "up inside one list");
    Eq("{x = 1; if a {cmd print [1]} else null; cmd print [2]}", DumpL(body));
    Eq(true, BlockEdits.MoveStatement(body, 0, body, 2), "down inside one list (lands before the old index 2)");
    Eq("{if a {cmd print [1]} else null; x = 1; cmd print [2]}", DumpL(body));
    Eq(false, BlockEdits.MoveStatement(body, 1, body, 1), "same place");
    Eq(false, BlockEdits.MoveStatement(body, 1, body, 2), "right after itself is the same place");
    Eq(false, BlockEdits.MoveStatement(body, 9, body, 0), "bad index");
    RoundTrip(Printer.Print([m]));
});
Test("block drag: across whens and mechanism-level moves", () =>
{
    var m = Parser.Parse("mechanism T\n    param a = 1\n    param b = 2\n    when start\n        print 1\n        repeat 2\n            wait 1\n    when tick\n        stop\n")[0];
    var start = m.Whens[0].Body; var tick = m.Whens[1].Body;
    Eq(true, BlockEdits.MoveStatement(start, 1, tick, 0));
    Eq("{cmd print [1]}", DumpL(start));
    Eq("{repeat 2 {wait 1}; stop}", DumpL(tick));
    Eq(true, BlockEdits.MoveItem(m.Whens, 1, m.Whens, 0));
    Eq(EventKind.Tick, m.Whens[0].Event.Kind);
    Eq(true, BlockEdits.MoveItem(m.Params, 1, m.Params, 0));
    Eq("b", m.Params[0].Name);
    var other = Parser.Parse("mechanism U\n")[0];
    Eq(true, BlockEdits.MoveItem(m.Whens, 0, other.Whens, 0), "a when into another mechanism");
    Eq(1, m.Whens.Count); Eq(1, other.Whens.Count);
    Eq(true, BlockEdits.IsMechanismLevel("when") && BlockEdits.IsMechanismLevel("param") && BlockEdits.IsMechanismLevel("uses"));
    Eq(false, BlockEdits.IsMechanismLevel("if"));
});
Test("block drag: a block can't go inside itself", () =>
{
    var m = Parser.Parse(M("if a\n    repeat 2\n        wait 1\nelse\n    stop"))[0];
    var body = m.Whens[0].Body;
    var outer = (IfStmt)body[0];
    var inner = ((RepeatStmt)outer.Then[0]).Body;
    Eq(true, BlockEdits.Contains(outer, inner));
    Eq(true, BlockEdits.Contains(outer, outer.Else!));
    Eq(false, BlockEdits.Contains(outer, body));
    Eq(false, BlockEdits.MoveStatement(body, 0, inner, 0), "into a grandchild body");
    Eq(false, BlockEdits.MoveStatement(body, 0, outer.Then, 1), "into its own body");
    Eq(1, body.Count, "nothing changed");
    Eq(true, BlockEdits.MoveStatement(outer.Then, 0, outer.Else!, 0), "a child into a sibling branch is fine");
    Eq("{if a {} else {repeat 2 {wait 1}; stop}}", DumpL(body));
});
Test("block drag: drop index from block positions", () =>
{
    var rows = new List<(double, double)> { (0, 20), (24, 44), (48, 108) };
    Eq(0, BlockEdits.DropIndex(rows, -5));
    Eq(0, BlockEdits.DropIndex(rows, 9));
    Eq(1, BlockEdits.DropIndex(rows, 11));
    Eq(2, BlockEdits.DropIndex(rows, 40));
    Eq(2, BlockEdits.DropIndex(rows, 70));
    Eq(3, BlockEdits.DropIndex(rows, 90));
    Eq(0, BlockEdits.DropIndex(new List<(double, double)>(), 50));
    var list = new List<Stmt> { new StopStmt(0) };
    BlockEdits.Insert(list, 0, BlockEdits.NewStatement("wait"));
    Eq("{wait 1; stop}", DumpL(list));
});
Test("block selection: path survives a reparse", () =>
{
    string src = "mechanism T\n    when start\n        stop\n    when tick\n        print 1\n        if a\n            x = 1\n        else\n            repeat 2\n                wait 1\n";
    var m = Parser.Parse(src)[0];
    var target = ((RepeatStmt)((IfStmt)m.Whens[1].Body[1]).Else![0]).Body;
    var path = BlockEdits.PathOf(m, target)!;
    Eq("1,1,1,0,0", string.Join(",", path));
    var again = Parser.Parse(Printer.Print([m]))[0];
    Eq("{wait 1}", DumpL(BlockEdits.BodyAt(again, path)));
    Eq("{stop}", DumpL(BlockEdits.BodyAt(again, BlockEdits.PathOf(m, m.Whens[0].Body)!)));
    Eq(null, BlockEdits.PathOf(m, new List<Stmt>()));
    Eq(null, BlockEdits.BodyAt(again, [5]));
    Eq(null, BlockEdits.BodyAt(again, [1, 0, 0]), "print has no body");
    Eq(null, BlockEdits.BodyAt(again, [1, 1, 2]), "no third branch");
});
Test("block undo: steps, merging and redo", () =>
{
    var u = new BlockUndo();
    Eq(false, u.CanUndo);
    u.Record("a", "a");
    Eq(false, u.CanUndo, "no change, no step");
    u.Record("a", "b");
    u.Record("b", "c", "field1");
    u.Record("c", "d", "field1");
    u.Record("d", "e", "field1");
    Eq(2, u.UndoCount, "the same field typed three times is one step");
    u.Record("e", "f", "field2");
    Eq(3, u.UndoCount);
    Eq("e", u.Undo("f"));
    Eq("b", u.Undo("e"));
    Eq(true, u.CanRedo);
    Eq("e", u.Redo("b"));
    u.Record("e", "g", "field2");
    Eq(false, u.CanRedo, "a new edit drops the redo steps");
    Eq(3, u.UndoCount, "and doesn't merge with the step before the undo");
    u.BreakMerge();
    u.Record("g", "h", "field2");
    Eq(4, u.UndoCount);
    Eq("g", u.Undo("h"));
    Eq("e", u.Undo("g"));
    Eq("b", u.Undo("e"));
    Eq("a", u.Undo("b"));
    Eq(null, u.Undo("a"));
    u.Reset();
    Eq(false, u.CanUndo || u.CanRedo);
    var small = new BlockUndo { Limit = 2 };
    small.Record("1", "2"); small.Record("2", "3"); small.Record("3", "4");
    Eq(2, small.UndoCount);
    Eq("3", small.Undo("4")); Eq("2", small.Undo("3")); Eq(null, small.Undo("2"));
});

Console.WriteLine("Project / scene");
string TempDir()
{
    string d = Path.Combine(Path.GetTempPath(), "BE_Test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(d);
    return d;
}
Test("project save -> load round-trip", () =>
{
    string dir = TempDir();
    try
    {
        var p = Project.CreateDemo("Round Trip");
        p.Mechanisms["Spinner"] = "mechanism Spinner\n    param turns: number = 2\n";
        var player = p.Scenes[0].Objects.First(o => o.Name == "Player");
        player.Mechanisms.Add(new SceneMechanism { Name = "Coin", Params = { ["value"] = 5.0, ["sound"] = "ding.wav" } });
        p.Scenes[0].Background = "#102030";
        p.Save(dir);
        Eq(true, File.Exists(Path.Combine(dir, "project.json")));
        Eq(true, File.Exists(Path.Combine(dir, "mechanisms", "Spinner.bs")));

        var q = Project.Load(dir);
        Eq("Round Trip", q.Name);
        Eq(p.Scenes[0].Objects.Count, q.Scenes[0].Objects.Count);
        Eq("#102030", q.Scenes[0].Background);
        var qp = q.Scenes[0].Objects.First(o => o.Name == "Player");
        Eq(player.Id, qp.Id); Eq(80.0, qp.X); Eq(48.0, qp.Height); Eq("#5B8DEF", qp.Color);
        Eq("player", qp.Tags[0]);
        Eq(2, qp.Mechanisms.Count);
        Eq(5.0, qp.Mechanisms[1].Params["value"]); Eq("ding.wav", qp.Mechanisms[1].Params["sound"]);
        Eq(true, q.Mechanisms["Spinner"].Contains("param turns"));

        // A mechanism removed from the project disappears from the folder on the next save.
        q.Mechanisms.Remove("Spinner");
        q.Save(dir);
        Eq(false, File.Exists(Path.Combine(dir, "mechanisms", "Spinner.bs")));
    }
    finally { Directory.Delete(dir, true); }
});
Test("tolerant load: missing and unknown fields, numbers as text", () =>
{
    var p = Project.FromJson("""
        {
          // comments are fine
          "Name": "Loose",
          "extra": { "whatever": 1 },
          "scenes": [ { "objects": [
            { "name": "A", "x": "12.5", "y": "3,5", "tags": "player, hero", "mechanisms": [ "Coin", { "name": "Patrol", "params": { "speed": "90" } } ] },
            { "id": 7, "mystery": true },
            { "id": 7, "width": "abc" },
          ] } ],
        }
        """);
    Eq("Loose", p.Name);
    var s = p.Scenes[0];
    Eq(800.0, s.Width); Eq("#1A1410", s.Background);
    Eq(3, s.Objects.Count);
    var a = s.Objects[0];
    Eq(12.5, a.X); Eq(3.5, a.Y); Eq(32.0, a.Width);
    Eq(2, a.Tags.Count); Eq("hero", a.Tags[1]);
    Eq("Coin", a.Mechanisms[0].Name); Eq("90", a.Mechanisms[1].Params["speed"]);
    Eq(3, s.Objects.Select(o => o.Id).Distinct().Count(), "unique ids");
    Eq(true, s.Objects.All(o => o.Id > 0), "positive ids");
    Eq(32.0, s.Objects[2].Width, "bad number -> default");
    Eq(1, Project.FromJson("{}").Scenes.Count, "an empty project still has a scene");
});
Test("scene spawns into a world: player collects a coin", () =>
{
    var p = Project.CreateDemo();
    var w = NewWorld("");
    w.Globals["score"] = 0.0;
    var map = w.LoadScene(p.Scenes[0]);
    Eq(p.Scenes[0].Objects.Count, map.Count);
    w.Step(1.0 / 60);
    var player = w.FindObject("player")!;
    Eq(48.0, player.Height, "size from the scene");
    var coin = map.First(kv => kv.Value.Name == "Coin 1").Key;
    w.KeysDown.Add("right");
    for (int i = 0; i < 60 * 3 && !coin.Destroyed; i++) w.Step(1.0 / 60);
    Eq(true, coin.Destroyed, "coin collected");
    Eq(1.0, w.Globals["score"]);
});
Test("scene param overrides reach the runtime (text numbers converted)", () =>
{
    var s = new Scene();
    s.Add(new SceneObject { Name = "Big coin", Mechanisms = [new SceneMechanism { Name = "Coin", Params = { ["value"] = "7", ["sound"] = "big.wav", ["nope"] = 1.0 } }] });
    s.Add(new SceneObject { Name = "P", Tags = ["player"] });
    s.Add(new SceneObject { Name = "Blinker", X = 500, Mechanisms = [new SceneMechanism { Name = "Blink", Params = { ["every"] = 0.25 } }] });
    var w = NewWorld(""); w.Globals["score"] = 0.0;
    var map = w.LoadScene(s);
    var coin = map.First(kv => kv.Value.Name == "Big coin").Key;
    Eq(7.0, coin.Mechanisms[0].Vars["value"]);
    Eq("big.wav", coin.Mechanisms[0].Vars["sound"]);
    Eq(0.25, map.First(kv => kv.Value.Name == "Blinker").Key.Mechanisms[0].Vars["every"]);
    w.Step(1.0 / 60);
    Eq(7.0, w.Globals["score"]);
    Eq(true, w.Output.Contains("♪ big.wav"));
});
Test("param values convert to the param's type", () =>
{
    Eq(2.5, World.ConvertParam("2,5", "number"));
    Eq(1.0, World.ConvertParam(true, "seconds"));
    Eq(true, World.ConvertParam("yes", "bool"));
    Eq(false, World.ConvertParam(0.0, "yesno"));
    Eq("3", World.ConvertParam(3.0, "text"));
    Eq(4.0, World.ConvertParam("4", "any"));
    Eq("hi", World.ConvertParam("hi", "any"));
});
Test("unknown mechanism in a scene is reported, object still appears", () =>
{
    var s = new Scene();
    s.Add(new SceneObject { Name = "Odd", Mechanisms = [new SceneMechanism { Name = "Coyn" }, new SceneMechanism { Name = "Blink" }] });
    var w = NewWorld("");
    var map = w.LoadScene(s);
    Eq(1, map.Count);
    Eq(1, map.Keys.First().Mechanisms.Count);
    Eq(true, w.Errors[0].Contains("Did you mean 'Coin'"), w.Errors[0]);
});
Test("renaming a mechanism updates scene references", () =>
{
    var p = Project.CreateDemo();
    p.Mechanisms["Patrol"] = "mechanism Patrol\n";
    int n = p.RenameMechanism("Patrol", "Walker");
    Eq(1, n);
    Eq(true, p.Scenes[0].Objects.Any(o => o.Has("Walker")));
    Eq(false, p.Scenes[0].Objects.Any(o => o.Has("Patrol")));
    Eq(true, p.Mechanisms.ContainsKey("Walker") && !p.Mechanisms.ContainsKey("Patrol"));
});
Test("scene clone is deep (undo snapshots)", () =>
{
    var s = Project.CreateDemo().Scenes[0];
    var c = s.Clone();
    c.Objects[1].X = 999; c.Objects[1].Mechanisms[0].Params["speed"] = 1.0; c.Objects[1].Tags.Add("x");
    Eq(80.0, s.Objects[1].X); Eq(false, s.Objects[1].Mechanisms[0].Params.ContainsKey("speed")); Eq(1, s.Objects[1].Tags.Count);
    var d = s.Objects[1].Clone();
    Eq("Platformer", d.Mechanisms[0].Name);
});

Console.WriteLine("Physics");
GameObject Box(World w, string name, double x, double y, double width, double height, string[] mechs, params string[] tags)
{
    var o = w.Spawn(name, x, y, mechs, null, tags);
    o.Width = width; o.Height = height;
    return o;
}
GameObject Floor(World w, double y = 400, double thick = 50) => Box(w, "Ground", 0, y, 800, thick, ["Solid"], "ground");
void Near(double expected, double actual, double tol, string what) =>
    Eq(true, Math.Abs(expected - actual) <= tol, $"{what}: {actual} (expected {expected} ± {tol})");
Test("a body falls and lands on the ground", () =>
{
    var w = NewWorld("");
    Floor(w);
    var b = Box(w, "B", 100, 100, 32, 32, ["Body"]);
    w.Step(1.0 / 60);
    Eq(false, b.OnGround, "in the air");
    Run(w, 2);
    Near(400, b.Y + b.Height, 0.01, "feet on the ground");
    Eq(true, b.OnGround, "on_ground");
    Eq(0.0, b.Vy, "vy");
});
Test("scripts read self.on_ground and self.vy", () =>
{
    var w = NewWorld("mechanism Probe\n    uses Body\n    when tick\n        down = self.on_ground\n        fall = self.vy\n");
    Floor(w);
    var b = Box(w, "B", 100, 300, 32, 32, ["Probe"]);
    Run(w, 0.1);
    Eq(false, b.Mechanisms[0].Vars["down"]);
    Eq(true, (double)b.Mechanisms[0].Vars["fall"]! > 0, "falling");
    Run(w, 1);
    Eq(true, b.Mechanisms[0].Vars["down"]);
});
Test("a body can't walk through a wall", () =>
{
    var w = NewWorld("mechanism Runner\n    uses Body\n    when tick\n        self.vx = 300\n");
    Floor(w);
    Box(w, "Wall", 200, 300, 16, 100, ["Solid"]);
    var b = Box(w, "B", 100, 368, 32, 32, ["Runner"]);
    Run(w, 2);
    Near(200, b.X + b.Width, 0.01, "stopped at the wall");
    Near(400, b.Y + b.Height, 0.01, "still on the ground");
    Eq(true, b.OnGround);
});
Test("platformer: jump reaches its height and lands", () =>
{
    var w = NewWorld("");
    Floor(w);
    var p = Box(w, "P", 100, 352, 32, 48, ["Platformer"], "player");
    Run(w, 0.2);
    Eq(true, p.OnGround, "starts on the ground");
    w.KeysDown.Add("space"); w.PressKey("space");
    double top = p.Y;
    for (int i = 0; i < 90; i++) { w.Step(1.0 / 60); top = Math.Min(top, p.Y); }
    Near(110, 352 - top, 4, "jump height");
    Eq(true, p.OnGround, "landed");
    Near(400, p.Y + p.Height, 0.01, "back on the ground");
});
Test("platformer: letting go early makes a small hop", () =>
{
    var w = NewWorld("");
    Floor(w);
    var p = Box(w, "P", 100, 352, 32, 48, ["Platformer"], "player");
    Run(w, 0.2);
    w.KeysDown.Add("space"); w.PressKey("space");
    w.Step(1.0 / 60); w.Step(1.0 / 60);
    w.KeysDown.Remove("space");
    double top = p.Y;
    for (int i = 0; i < 90; i++) { w.Step(1.0 / 60); top = Math.Min(top, p.Y); }
    Eq(true, 352 - top is > 5 and < 60, $"hop height {352 - top}");
});
Test("platformer: walks with the arrow keys", () =>
{
    var w = NewWorld("");
    Floor(w);
    var p = Box(w, "P", 100, 352, 32, 48, ["Platformer"], "player");
    w.KeysDown.Add("right");
    Run(w, 1);
    Near(320, p.X, 8, "x after 1 s at 220 px/s");
    w.KeysDown.Clear(); w.KeysDown.Add("a");
    Run(w, 0.5);
    Near(210, p.X, 8, "back left");
});
Test("no tunnelling: a very fast fall stops on a thin floor", () =>
{
    var w = NewWorld("mechanism Rock\n    when start\n        body 50000, 20000\n");
    Floor(w, 400, 2);
    var b = Box(w, "B", 100, -3000, 16, 16, ["Rock"]);
    Run(w, 1);
    Near(400, b.Y + b.Height, 0.01, "landed on the 2 px floor");
    var w2 = NewWorld("mechanism Bullet\n    uses Body gravity: 0\n    when start\n        self.vx = 30000\n");
    Box(w2, "Wall", 500, 0, 2, 800, ["Solid"]);
    var s = Box(w2, "S", 0, 100, 8, 8, ["Bullet"]);
    Run(w2, 0.5);
    Near(500, s.X + s.Width, 0.01, "stopped by the 2 px wall");
});
Test("two bodies stack (in either spawn order)", () =>
{
    foreach (bool upperFirst in new[] { false, true })
    {
        var w = NewWorld("mechanism Crate\n    uses Body\n    uses Solid\n");
        Floor(w);
        GameObject lower, upper;
        if (upperFirst) { upper = Box(w, "Up", 105, 100, 32, 32, ["Crate"]); lower = Box(w, "Low", 100, 250, 32, 32, ["Crate"]); }
        else { lower = Box(w, "Low", 100, 250, 32, 32, ["Crate"]); upper = Box(w, "Up", 105, 100, 32, 32, ["Crate"]); }
        Run(w, 2);
        Near(400, lower.Y + lower.Height, 0.01, "lower on the ground");
        Near(lower.Y, upper.Y + upper.Height, 0.01, "upper on the lower");
        Eq(true, upper.OnGround && lower.OnGround, "both on something");
        Eq(lower, upper.Ground, "upper stands on the lower");
    }
});
Test("touched by fires when a body lands on something (once)", () =>
{
    var w = NewWorld("mechanism Lander\n    uses Body\n    when touched by \"ground\"\n        landed += 1\nmechanism Pad\n    when start\n        solid\n    when touched by other\n        hits += 1\n");
    var pad = Box(w, "Pad", 0, 400, 800, 50, ["Pad"], "ground");
    var b = Box(w, "B", 100, 200, 32, 32, ["Lander"]);
    Run(w, 0.3);
    Eq(null, b.Mechanisms[0].Vars.GetValueOrDefault("landed"), "not yet");
    Run(w, 2);
    Eq(1.0, b.Mechanisms[0].Vars["landed"], "landed once");
    Eq(1.0, pad.Mechanisms[0].Vars["hits"], "the ground saw it too");
});
Test("objects without a Body aren't affected by physics", () =>
{
    var w = NewWorld("");
    Floor(w);
    var plain = Box(w, "Plain", 100, 100, 32, 32, []);
    var coin = Box(w, "Coin", 200, 380, 16, 16, ["Coin"]);
    Run(w, 1);
    Eq(100.0, plain.Y); Eq(380.0, coin.Y, "a coin overlapping the ground stays put");
    Eq(false, plain.OnGround);
});
Test("an object without a Body still moves by its speed (no collisions)", () =>
{
    var w = NewWorld("mechanism Shot\n    when start\n        self.vx = 120\n");
    Box(w, "Wall", 50, 0, 10, 400, ["Solid"]);
    var s = Box(w, "S", 0, 100, 8, 8, ["Shot"]);
    Run(w, 1);
    Near(120, s.X, 2.5, "flew through");
});
Test("patrol still works, alone and with a Body on the ground", () =>
{
    var w = NewWorld("");
    Floor(w);
    var alone = Box(w, "A", 0, 100, 32, 32, ["Patrol"]);
    var walker = Box(w, "W", 300, 360, 40, 32, ["Patrol", "Body"]);
    double maxA = 0, maxW = 0;
    for (int i = 0; i < 60 * 4; i++) { w.Step(1.0 / 60); maxA = Math.Max(maxA, alone.X); maxW = Math.Max(maxW, walker.X); }
    Eq(100.0, alone.Y, "no gravity without a Body");
    Eq(true, maxA > 90 && maxA < 110 && alone.X < maxA, $"alone: max x {maxA}");
    Eq(true, maxW > 390 && maxW < 410 && walker.X < maxW, $"walker: max x {maxW}");
    Near(400, walker.Y + walker.Height, 0.01, "walker on the ground");
});
Test("one-way platform: jump up through it, land on top", () =>
{
    var w = NewWorld("mechanism Up\n    uses Body\n    when start\n        push 0, -900\n");
    Floor(w);
    Box(w, "Ledge", 0, 300, 800, 10, ["OneWay"]);
    var b = Box(w, "B", 100, 368, 32, 32, ["Up"]);
    Run(w, 2);
    Near(300, b.Y + b.Height, 0.01, "standing on the ledge");
    Eq(true, b.OnGround);
});
Test("a moving solid carries what stands on it", () =>
{
    var w = NewWorld("mechanism Lift\n    uses Solid\n    when tick\n        self.x += 60 * dt\n");
    var lift = Box(w, "Lift", 100, 300, 100, 16, ["Lift"]);
    var b = Box(w, "B", 130, 268, 32, 32, ["Body"]);
    Run(w, 0.5);
    double startB = b.X, startL = lift.X;
    Run(w, 1);
    Near(lift.X - startL, b.X - startB, 1.5, "rider moved with the lift");
    Near(300, b.Y + b.Height, 0.01, "still on the lift");
});
Test("coyote time and jump buffer", () =>
{
    // Coyote: jump just after walking off a ledge.
    var w = NewWorld("mechanism J\n    uses Body\n    when told \"jump\"\n        jump 80\n");
    Box(w, "Ledge", 0, 400, 100, 20, ["Solid"]);
    var b = Box(w, "B", 60, 368, 32, 32, ["J"]);
    Run(w, 0.2);
    Eq(true, b.OnGround);
    b.X = 101; // just off the edge
    w.Step(1.0 / 60); w.Step(1.0 / 60);
    Eq(false, b.OnGround, "off the ledge");
    w.Tell(b, "jump", []);
    Eq(true, b.Vy < -300, $"still jumped (vy {b.Vy})");
    // Buffer: pressed just before landing, jumps when it lands.
    var w2 = NewWorld("mechanism J\n    uses Body\n    when told \"jump\"\n        jump 80\n");
    Floor(w2);
    var c = Box(w2, "C", 100, 300, 32, 32, ["J"]);
    while (c.Y + c.Height < 392) w2.Step(1.0 / 60);
    w2.Tell(c, "jump", []);
    double top = c.Y;
    for (int i = 0; i < 20; i++) { w2.Step(1.0 / 60); top = Math.Min(top, c.Y); }
    Eq(true, 368 - top > 40, $"buffered jump went up {368 - top} px");
});
Test("jump without a Body is a friendly error", () =>
{
    var w = NewWorld("mechanism Hop\n    when start\n        jump 50\n");
    w.Spawn("o", 0, 0, ["Hop"]); w.Step(1.0 / 60);
    Eq(true, w.Errors.Count == 1 && w.Errors[0].Contains("needs a Body"), string.Join(" | ", w.Errors));
});
Test("physics demo: player lands, walks, collects a coin; crate lands", () =>
{
    var p = Project.CreateDemo();
    PhysicsDemo.Apply(p.Scenes[0]);
    PhysicsDemo.Apply(p.Scenes[0]); // twice changes nothing
    var s = p.Scenes[0];
    Eq(false, s.Objects.First(o => o.Name == "Player").Has("PlayerMove"));
    Eq(1, s.Objects.Count(o => o.Name == "Crate"));
    var w = NewWorld(""); w.Globals["score"] = 0.0;
    var map = w.LoadScene(s);
    Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
    Run(w, 1);
    var player = w.FindObject("Player")!;
    Near(400, player.Y + player.Height, 0.01, "player on the ground");
    var crate = map.First(kv => kv.Value.Name == "Crate").Key;
    Near(400, crate.Y + crate.Height, 0.01, "crate on the ground");
    var slime = map.First(kv => kv.Value.Name == "Slime").Key;
    Near(400, slime.Y + slime.Height, 0.01, "slime on the ground");
    w.KeysDown.Add("right");
    Run(w, 0.7); // coin 1 at x 220, coin 2 at 300
    Eq(1.0, w.Globals["score"], "coin 1 collected");
});
ProjectTests.Run(Test); // scenes, sounds, My Library, settings (ProjectTests.cs)
EngineTests.Run(Test); // tweens, camera, dialogue, HUD, pictures, saves, the demo cutscene (EngineTests.cs)

Console.WriteLine("Command base (named values, waiting commands, phrases)");
Test("named values parse and print back", () =>
{
    var src = "mechanism A\n    when start\n        move Boss to 400, 300 in 2 seconds\n";
    var c = (CommandStmt)Parser.Parse(src)[0].Whens[0].Body[0];
    Eq(1, c.Args.Count); Eq(2, c.Named!["to"].Count); Eq(1, c.Named["in"].Count);
    Eq(true, Printer.Print(Parser.Parse(src)).Contains("move Boss to 400, 300 in 2"));
});
Test("a command can wait, multi-word names match", () =>
{
    var w = NewWorld("mechanism A\n    when start\n        hold door 0.5\n        done = true\n");
    w.Commands["hold door"] = c => { double until = c.World.Time + c.Number(c.Arg(0)); c.WaitUntil(() => c.World.Time >= until); };
    var o = w.Spawn("o", 0, 0, ["A"]);
    Run(w, 0.3); Eq(null, o.Mechanisms[0].Vars.GetValueOrDefault("done"));
    Run(w, 0.4); Eq(true, o.Mechanisms[0].Vars["done"]);
});

// ---------------- Builder Script 2: user commands, waiting, parallel, loops, lists, finding objects ----------------
Console.WriteLine("Language: user commands");
object? V(GameObject o, string name) => o.Mechanisms[0].Vars.GetValueOrDefault(name);
(World, GameObject) One(string src, double run = 1.0 / 60)
{
    var w = NewWorld(src);
    var o = w.Spawn("o", 0, 0, [Parser.Parse(src)[0].Name]);
    Run(w, run);
    return (w, o);
}
Test("to: parses a command with inputs, 'to' still works as a connector inside", () =>
{
    var m = Parser.Parse("mechanism A\n    to slide who, x\n        move who to x, 0 in 1\n    when start: slide self, 3\n")[0];
    Eq(1, m.Procs.Count); Eq("slide", m.Procs[0].Name); Eq("who,x", string.Join(",", m.Procs[0].Params));
    var c = (CommandStmt)m.Procs[0].Body[0];
    Eq("move", c.Name); Eq(2, c.Named!["to"].Count);
});
Test("a user command runs with its inputs as locals", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        hits = 0\n        hit 2\n        hit 3\n    to hit amount\n        temp = amount * 10\n        hits += amount\n");
    Eq(5.0, V(o, "hits"));
    Eq(null, V(o, "temp"), "new names inside 'to' are locals");
    Eq(null, V(o, "amount"), "inputs are locals");
});
Test("a user command can return a value and be used in an expression", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        a = double(4) + 1\n        b = fact(5)\n        c = sign(-3)\n    to double x\n        return x * 2\n    to fact n\n        if n <= 1: return 1\n        return n * fact(n - 1)\n    to sign x\n        if x < 0\n            return -1\n        return 1\n");
    Eq(9.0, V(o, "a")); Eq(120.0, V(o, "b")); Eq(-1.0, V(o, "c"), "return ends the command early");
});
Test("a waiting command: the caller waits for it to finish", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        step = 1\n        pause_then 0.5\n        step = 3\n    to pause_then t\n        wait t\n        step = 2\n");
    Eq(1.0, V(o, "step"));
    Run(w, 0.3); Eq(1.0, V(o, "step"));
    Run(w, 0.3); Eq(3.0, V(o, "step"));
});
Test("commands of used mechanisms, of any mechanism by name, and Mechanism.name", () =>
{
    var w = NewWorld("mechanism Tools\n    param flashes: number = 0\n    to zap times\n        flashes += times\n    to twice x\n        return x * 2\nmechanism Hero\n    uses Tools\n    when start\n        zap 2\n        n = twice(5)\nmechanism Other\n    when start\n        zap 1\n        Tools.zap 4\n        m = Tools.twice(3)\n");
    var h = w.Spawn("h", 0, 0, ["Hero"]);
    var x = w.Spawn("x", 0, 0, ["Other"]);
    w.Step(1.0 / 60);
    Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
    Eq(2.0, h.Mechanisms[0].Children["Tools"].Vars["flashes"], "runs in the used mechanism's own variables");
    Eq(10.0, V(h, "n"));
    Eq(6.0, V(x, "m"));
    Eq(5.0, x.Helpers!["Tools"].Vars["flashes"], "a mechanism the object doesn't have runs in a quiet copy");
});
Test("an existing mechanism variable is changed from inside a command", () =>
{
    var (w, o) = One("mechanism A\n    param lives: number = 3\n    when start: lose\n    to lose\n        lives -= 1\n");
    Eq(2.0, V(o, "lives"));
});
Test("errors: ambiguous command, wrong number of values, recursion, waits in a value", () =>
{
    var w = NewWorld("mechanism P\n    to boom\n        print 1\nmechanism Q\n    to boom\n        print 2\nmechanism A\n    when start: boom\n");
    w.Spawn("a", 0, 0, ["A"]); w.Step(1.0 / 60);
    Eq(true, w.Errors.Count == 1 && w.Errors[0].Contains("P.boom"), string.Join(" | ", w.Errors));
    var (w2, _) = One("mechanism A\n    when start: hit 1, 2\n    to hit amount\n        x = amount\n");
    Eq(true, w2.Errors[0].Contains("needs 1 value(s) (hit amount), but got 2"), w2.Errors[0]);
    var (w3, _) = One("mechanism A\n    when start: down 1\n    to down n\n        down n + 1\n");
    Eq(true, w3.Errors[0].Contains("more than 100 times"), w3.Errors[0]);
    var (w4, _) = One("mechanism A\n    when start: x = slow(1)\n    to slow n\n        wait 1\n        return n\n");
    Eq(true, w4.Errors[0].Contains("can't be used inside a value"), w4.Errors[0]);
    var (w5, _) = One("mechanism A\n    when start: atack\n    to attack\n        x = 1\n");
    Eq(true, w5.Errors[0].Contains("Did you mean 'attack'"), w5.Errors[0]);
    var (w6, _) = One("mechanism A\n    when start: x = dubble(2)\n    to double n\n        return n * 2\n");
    Eq(true, w6.Errors[0].Contains("Did you mean 'double'"), w6.Errors[0]);
});
Test("errors when writing: return outside 'to', bad names, 'to' inside a when", () =>
{
    Throws("only works inside a command made with 'to'", () => Parser.Parse("mechanism A\n    when start\n        return 1\n"));
    Throws("already a command", () => Parser.Parse("mechanism A\n    to move x\n        print x\n"));
    Throws("already a function", () => Parser.Parse("mechanism A\n    to length x\n        print x\n"));
    Throws("Builder Script word", () => Parser.Parse("mechanism A\n    to while\n        print 1\n"));
    Throws("goes directly inside the mechanism", () => Parser.Parse("mechanism A\n    when start\n        to x\n"));
    Throws("two commands called 'a'", () => Parser.Parse("mechanism A\n    to a\n        stop\n    to a\n        stop\n"));
    Throws("two inputs", () => Parser.Parse("mechanism A\n    to a x, x\n        stop\n"));
    Throws("'param', 'uses' or 'when'", () => Parser.Parse("mechanism A\n    print 1\n"));
});

Console.WriteLine("Language: waiting, together, run");
Test("wait until: checked each step, goes on at once when already true", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        wait until true\n        a = 1\n        wait until ready\n        b = 1\n");
    Eq(1.0, V(o, "a")); Eq(null, V(o, "b"));
    Run(w, 0.2); Eq(null, V(o, "b"));
    o.Mechanisms[0].Vars["ready"] = true;
    w.Step(1.0 / 60); Eq(1.0, V(o, "b"));
});
Test("wait for a signal: own, Health.died, other.Health.died", () =>
{
    var w = NewWorld("mechanism Boss\n    uses Health max 1\n    when start\n        wait for Health.died\n        print \"boss down\"\n    when start\n        wait for died\n        print \"own died\"\n    when told \"kill\"\n        emit died\nmechanism Fan\n    when touched by other\n        wait for other.Health.died\n        print \"fan saw it\"\n");
    var boss = w.Spawn("Boss", 0, 0, ["Boss"]);
    w.Spawn("Fan", 0, 0, ["Fan"]);
    Run(w, 0.2);
    Eq(0, w.Output.Count);
    w.Tell(boss, "damage", [1.0]); w.Step(1.0 / 60);
    Eq(true, w.Output.Contains("boss down") && w.Output.Contains("fan saw it"), string.Join(" | ", w.Output));
    Eq(true, w.Output.Contains("own died"), "'wait for died' takes it from any mechanism of the object");
    Eq(1, w.Output.Count(x => x == "boss down"), "a wait ends once");
    Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
});
Test("wait for key: only a press after it started waiting", () =>
{
    var (w, o) = One("mechanism A\n    when key \"space\"\n        n += 1\n        wait for key \"space\"\n        done = true\n");
    w.PressKey("space"); w.Step(1.0 / 60);
    Eq(1.0, V(o, "n")); Eq(null, V(o, "done"), "the press that started it doesn't count");
    w.PressKey("x"); w.Step(1.0 / 60);
    Eq(null, V(o, "done"));
    w.PressKey("space"); w.Step(1.0 / 60);
    Eq(true, V(o, "done"));
});
Test("together: branches run at the same time, it ends with the slowest", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        together\n            wait 0.2\n            fast = time\n            repeat 3\n                wait 0.2\n            slow_done = true\n        after = true\n");
    Eq(null, V(o, "after"));
    Run(w, 0.3);
    Eq(true, V(o, "fast") is double, "the instant branch ran");
    Eq(null, V(o, "after"), "still waiting for the slow branch");
    Run(w, 0.35);
    Eq(true, V(o, "after"), "continues after the slowest branch");
});
Test("together: waiting user commands in parallel", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        n = 0\n        together\n            grow 0.5\n            grow 0.25\n        done = time\n    to grow t\n        wait t\n        n += 1\n");
    Run(w, 0.6);
    Eq(2.0, V(o, "n"));
    Eq(true, (double)V(o, "done")! is > 0.45 and < 0.6, $"done at {V(o, "done")}");
});
Test("run: starts in the background and doesn't block", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        turns = 0\n        run spin\n        run\n            wait 0.3\n            late = true\n        after = true\n    to spin\n        repeat forever\n            turns += 1\n            wait 0.1\n");
    Eq(true, V(o, "after"), "the line after run ran at once");
    Eq(1.0, V(o, "turns"), "the background task started right away");
    Run(w, 0.5);
    Eq(true, V(o, "late"));
    Eq(true, (double)V(o, "turns")! >= 5, $"turns {V(o, "turns")}");
});
Test("a tick with a run task still runs every step", () =>
{
    var (w, o) = One("mechanism A\n    when tick\n        ticks += 1\n        if ticks == 1: run keep\n    to keep\n        wait 5\n", 0.5);
    Eq(true, (double)V(o, "ticks")! >= 29, $"ticks {V(o, "ticks")}");
});

Console.WriteLine("Language: loops and lists");
Test("for each over range, a list literal and objects", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        for each i in range(1, 4)\n            sum += i\n        for each word in [\"a\", \"b\"]\n            s += word\n        for each n in range(10, 1, -3)\n            down += text(n) + \" \"\n");
    Eq(10.0, V(o, "sum")); Eq("ab", V(o, "s")); Eq("10 7 4 1 ", V(o, "down"));
});
Test("for each can wait inside; while runs once per frame without a wait", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        for each i in [1, 2, 3]\n            last = i\n            wait 0.1\n    when start\n        while n < 1000\n            n += 1\n", 0.05);
    Eq(1.0, V(o, "last"));
    Run(w, 0.3);
    Eq(3.0, V(o, "last"));
    Eq(true, (double)V(o, "n")! < 40, $"while without wait is one round per frame (n {V(o, "n")})");
});
Test("lists: add, remove, index, length, contains, join, random_item", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        add \"sword\" to bag\n        add \"key\", \"map\" to bag\n        remove \"key\" from bag\n        first = bag[0]\n        last = bag[-1]\n        n = length(bag)\n        has = contains(bag, \"map\")\n        no = contains(bag, \"key\")\n        all_items = join(bag, \"+\")\n        pick = random_item(bag)\n        shown = text(bag)\n        nums = [1, 2] \n        same = nums == [1, 2]\n        letters = length(\"hello\")\n");
    Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
    Eq("sword", V(o, "first")); Eq("map", V(o, "last")); Eq(2.0, V(o, "n"));
    Eq(true, V(o, "has")); Eq(false, V(o, "no")); Eq("sword+map", V(o, "all_items"));
    Eq(true, V(o, "pick") is "sword" or "map");
    Eq("[\"sword\", \"map\"]", V(o, "shown")); Eq(true, V(o, "same")); Eq(5.0, V(o, "letters"));
});
Test("lists: friendly errors", () =>
{
    var (w, _) = One("mechanism A\n    when start\n        items = [1, 2, 3]\n        x = items[5]\n");
    Eq(true, w.Errors[0].Contains("has 3 item(s), so there's no item 5"), w.Errors[0]);
    var (w2, _) = One("mechanism A\n    when start\n        for each i in 5\n            x = i\n");
    Eq(true, w2.Errors[0].Contains("range(1, 10)"), w2.Errors[0]);
    var (w3, _) = One("mechanism A\n    when start\n        add 1 items\n");
    Eq(true, w3.Errors[0].Contains("add 5 to items"), w3.Errors[0]);
    var (w4, _) = One("mechanism A\n    when start\n        x = nothing_here[0]\n");
    Eq(true, w4.Errors[0].Contains("Make one first"), w4.Errors[0]);
});

Console.WriteLine("Language: finding objects");
Test("nearest, all, count, find, distance, has_tag", () =>
{
    var w = NewWorld("mechanism Hunter\n    when start\n        target = nearest(\"enemy\")\n        n = count(\"enemy\")\n        k = length(all(\"enemy\"))\n        boss = find(\"Boss\")\n        d = distance(self, target)\n        d4 = distance(0, 0, 3, 4)\n        tagged = has_tag(target, \"enemy\")\n        nobody = nearest(\"ghost\")\n        for each e in all(\"enemy\")\n            tell e \"hit\"\nmechanism Enemy\n    when told \"hit\"\n        hit = true\n");
    var h = w.Spawn("Hunter", 0, 0, ["Hunter"]);
    var far = w.Spawn("Far", 300, 0, ["Enemy"], null, "enemy");
    var near = w.Spawn("Near", 100, 0, ["Enemy"], null, "enemy");
    w.Spawn("Boss", 500, 0, null, null, "boss");
    w.Step(1.0 / 60);
    Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
    Eq(near, V(h, "target")); Eq(2.0, V(h, "n")); Eq(2.0, V(h, "k"));
    Eq("Boss", ((GameObject)V(h, "boss")!).Name);
    Eq(100.0, V(h, "d")); Eq(5.0, V(h, "d4")); Eq(true, V(h, "tagged")); Eq(null, V(h, "nobody"));
    Eq(true, V(far, "hit")); Eq(true, V(near, "hit"));
    var (w2, _) = One("mechanism A\n    when start: x = distance(self, nearest(\"ghost\"))\n");
    Eq(true, w2.Errors[0].Contains("found none"), w2.Errors[0]);
});

Console.WriteLine("Language: printer and blocks");
Test("round-trip: every new statement and value", () => RoundTrip(
    "mechanism T\n    uses Tools\n\n    when start\n        wait until hp <= 0 or done\n        wait for died\n        wait for Health.died\n        wait for other.Health.died\n        wait for key \"space\"\n" +
    "        together\n            move self to 1, 2 in 3\n            spin 2\n        run spin 3\n        run\n            wait 1\n            x = 1\n        run repeat forever\n            wait 1\n        run:\n" +
    "        together:\n        for each e in all(\"enemy\")\n            tell e \"hit\" [1, 2]\n        while hp > 0\n            wait 1\n        if a: run x = 1\n" +
    "        items = [1, [2, 3], \"x\"]\n        y = items[0] + items[-1][0] + (a + b)[1] + all(\"x\")[0] + Tools.twice(2)\n        Tools.flash 2\n        add 5 to items\n        remove 5 from items\n        wait (until)\n        tell (a) ([1])\n        tell a b, [1]\n\n" +
    "    to spin turns\n        repeat turns\n            wait 0.1\n        return\n\n    to twice x\n        return x * 2\n\n    to empty:\n"));
Test("printer: canonical form of a 'to' and its call", () =>
{
    Eq("mechanism A\n    when start\n        greet \"Bo\", 2\n\n    to greet who, n\n        repeat n\n            print \"hi \" + who\n",
        Printer.Print(Parser.Parse("mechanism A\n  to greet who n\n    repeat n\n      print \"hi \" + who\n  when start\n    greet \"Bo\" 2\n")));
});
Test("words of the new statements still work as variables", () =>
{
    var (w, o) = One("mechanism A\n    when start\n        run = 1\n        while = 2\n        together = run + while\n");
    Eq(3.0, V(o, "together"));
});
Test("block edits: new blocks print, parse and nest", () =>
{
    var m = Parser.Parse("mechanism T\n    when start:\n")[0];
    var body = m.Whens[0].Body;
    foreach (var id in new[] { "while", "foreach", "together", "run", "waituntil", "waitfor", "waitkey", "listadd", "listremove" })
        body.Add(BlockEdits.NewStatement(id));
    var proc = BlockEdits.NewProc(m);
    m.Procs.Add(proc);
    Eq("do_something2", BlockEdits.NewProc(m).Name);
    proc.Body.Add(BlockEdits.NewStatement("return"));
    proc.Body.Add(BlockEdits.NewCall("do_something", 0));
    RoundTrip(Printer.Print([m]));
    Eq(true, Printer.Print([m]).Contains("add \"key\" to items"));
    var together = (TogetherStmt)body[2];
    Eq(true, BlockEdits.MoveStatement(body, 0, together.Body, 0), "a while into the together");
    Eq(true, BlockEdits.Contains(together, ((WhileStmt)together.Body[0]).Body));
    BlockEdits.Duplicate(body, 1);
    ((TogetherStmt)body[2]).Body.Clear();
    Eq(1, ((TogetherStmt)body[1]).Body.Count, "duplicate is deep");
    var path = BlockEdits.PathOf(m, proc.Body)!;
    Eq("1", string.Join(",", path), "'to' bodies come after the whens");
    Eq(true, ReferenceEquals(proc.Body, BlockEdits.BodyAt(m, path)));
    Eq(true, BlockEdits.InProc(m, proc.Body)); Eq(false, BlockEdits.InProc(m, body));
    Eq(true, BlockEdits.IsMechanismLevel("to"));
    BlockEdits.Duplicate(m.Procs, 0);
    Eq("do_something_copy", m.Procs[1].Name);
});
Test("block edits: renaming a command renames its calls", () =>
{
    var m = Parser.Parse("mechanism A\n    when start\n        hit 1\n        if hp > 0\n            x = hit(2) + 1\n    to hit n\n        if n > 0: hit n - 1\n        return n\n")[0];
    Eq(3, BlockEdits.RenameCalls(m, "hit", "strike"));
    m.Procs[0] = m.Procs[0] with { Name = "strike" };
    string code = Printer.Print([m]);
    Eq(false, code.Contains("hit"), code);
    Eq(null, BlockEdits.CheckProcName(m, "smash"));
    Eq(true, BlockEdits.CheckProcName(m, "print") != null);
    Eq("a,b", string.Join(",", BlockEdits.ParseProcParams("a, b")));
    Throws("two inputs", () => BlockEdits.ParseProcParams("a a"));
});
Test("palette: MY COMMANDS lists own, used and project commands", () =>
{
    var defs = Parser.Parse("mechanism Hero\n    uses Tools\n    to dash\n        stop\nmechanism Tools\n    to flash times\n        stop\nmechanism Fx\n    to boom\n        stop\n    to flash\n        stop\nmechanism Empty\n");
    var cmds = BlockEdits.CommandsFor(defs[0], defs);
    Eq("dash|This mechanism, flash|From Tools, boom|In this project, Fx.flash|In this project",
        string.Join(", ", cmds.Select(c => c.CallName + "|" + c.Group)));
    Eq("times", BlockEdits.FindCommand("flash", defs[0], defs)!.Params[0]);
    Eq(0, BlockEdits.FindCommand("Fx.flash", defs[0], defs)!.Params.Count);
    Eq(null, BlockEdits.FindCommand("print", defs[0], defs));
});
Test("starter kit still runs without errors", () =>
{
    var w = NewWorld("");
    foreach (var name in new[] { "Coin", "Health", "Patrol", "Blink", "PlayerMove", "Platformer" }) w.Spawn(name, 0, 0, [name]);
    Run(w, 0.5);
    Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
});

DebugTests.Run(Test); // debugger, param types, variants (DebugTests.cs)
ToolsTests.Run(Test); // editor tools and C# plugins (ToolsTests.cs)
AiTests.Run(Test); // the AI assistant with a fake AI: prompts, reading answers, checks, retry, accept, keys (AiTests.cs)
QolTests.Run(Test); // the scene editor's conveniences: selection, nudge, copy/paste, order, align, wheel, search, shortcuts (QolTests.cs)
GameKitTests.Run(Test); // tiles, lights, mouse, screens, inventory, crafting, cards, sounds, performance (GameKitTests.cs)
DeepdelveTests.Run(Test); // the Deepdelve sample game, played headless (Samples/DeepdelveTests.cs)
NightShiftTests.Run(Test); // the Night Shift sample game played headless (Samples/NightShiftTests.cs)
HollowLanternTests.Run(Test); // the Hollow Lantern sample game, pathfinding, angles, minimap (Samples/HollowLanternTests.cs)
EmberDuelTests.Run(Test); // the Ember Duel sample card game and records, labels, list saves (Samples/EmberDuelTests.cs)
PrefabsTests.Run(Test); // prefabs, named animations, sprite sheets and the pixel-art editor (PrefabsTests.cs)

Console.WriteLine("Prefabs in exported games (GameBoot.UseProject is shared by the editor and the player)");
Test("an exported game's world finds the project's prefabs", () =>
{
    var p = Project.CreateEmpty("Boot");
    p.Prefabs.Add(new Prefab { Name = "Rock", Template = new SceneObject { Name = "Rock", Width = 20, Height = 10 } });
    p.Mechanisms["Dropper"] = "mechanism Dropper\n    when start\n        spawn \"Rock\" 40, 50\n";
    var w = GameBoot.NewWorld(p, Path.GetTempPath());
    w.Spawn("d", 0, 0, ["Dropper"]);
    w.Step(1.0 / 60); w.Step(1.0 / 60);
    Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
    var rock = w.FindObject("Rock");
    Eq(true, rock != null, "the prefab was spawned");
    Eq(20.0, rock!.Width);
});

Console.WriteLine("One-click local AI (AiOllamaSetup.cs)");
Test("Ollama pull progress lines are read", () =>
{
    var a = OllamaSetup.ParsePullLine("{\"status\":\"pulling manifest\"}")!.Value;
    Eq("pulling manifest", a.Status); Eq(-1.0, a.Fraction);
    var b = OllamaSetup.ParsePullLine("{\"status\":\"pulling 6e4c\",\"digest\":\"sha256:6e4c\",\"total\":1000,\"completed\":250}")!.Value;
    Eq(0.25, b.Fraction);
    Eq("success", OllamaSetup.ParsePullLine("{\"status\":\"success\"}")!.Value.Status);
    Eq(null, OllamaSetup.ParsePullLine("not json"));
    Eq(null, OllamaSetup.ParsePullLine("   "));
});
Test("an Ollama pull error becomes a friendly AI error", () =>
{
    try { OllamaSetup.ParsePullLine("{\"error\":\"pull model manifest: file does not exist\"}"); throw new Exception("no error"); }
    catch (AiException e) { Eq(true, e.Message.Contains("file does not exist")); }
});
Test("opening never touches Ollama when the cloud AI is chosen", () =>
{
    var s = new AiSettings { Provider = AiSettings.Auto };
    Eq(null, OllamaSetup.PrepareOnOpenAsync(s).GetAwaiter().GetResult());
});

SystemsTests.Run(Test); // systems (sets of mechanisms that work together) and "Export game…" (SystemsTests.cs)

LinkTests.Run(Test); // the EasyEngine ⇄ BuilderEngine link: .eeproj round-trips, layers, ids, live link (LinkTests.cs)
AuditDataTests.Run(Test); // the data audit: project files, mechanisms, settings, plugins, export, big projects (AuditDataTests.cs)
AuditRuntimeTests.Run(Test); // the runtime audit: crash-proof scripts, runaway loops, values, saves, long play, allocations (AuditRuntimeTests.cs)
Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail;
