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
    Eq("PlayerMove", d.Mechanisms[0].Name);
});

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail;
