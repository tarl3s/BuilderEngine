using BuilderEngine.Script;

/// <summary>The debugger (hooks, breakpoints, step, timeline, editing values), param types and variants. Called from Program.cs.</summary>
static class DebugTests
{
    const double Dt = 1.0 / 60;

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

    static World NewWorld(string src = "")
    {
        var w = new World();
        StarterKit.LoadInto(w);
        if (src.Length > 0) w.Load(src);
        return w;
    }

    static (World W, Debugger D) Debugged(string src)
    {
        var w = NewWorld(src);
        var d = new Debugger();
        d.Attach(w);
        return (w, d);
    }

    static object? Var(GameObject o, string name, int mech = 0) => o.Mechanisms[mech].Vars.GetValueOrDefault(name);

    static void Frames(World w, int n)
    {
        for (int i = 0; i < n; i++) w.Frame(Dt);
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Debugger");
        DebuggerTests(test);
        Console.WriteLine("Param types");
        ParamTypeTests(test);
        Console.WriteLine("Variants");
        VariantTests(test);
    }

    // ---------------- Debugger ----------------

    const string Counter = "mechanism Counter\n    when start\n        x = 1\n        x = 2\n        x = 3\n";

    static void DebuggerTests(Action<string, Action> test)
    {
        test("each statement is reported with its line and object", () =>
        {
            var (w, d) = Debugged(Counter);
            d.Trace = true;
            w.Spawn("C", 0, 0, ["Counter"]);
            w.Frame(Dt);
            Eq("3,4,5", string.Join(",", d.TraceLog.Where(t => t.Source == "Counter").Select(t => t.Line)));
            Eq("C", d.LastStatement!.Value.Object);
            True(d.StatementsRun >= 3, "statements counted");
        });

        test("a breakpoint pauses the game right before its line", () =>
        {
            var (w, d) = Debugged(Counter);
            d.SetBreakpoint("Counter", 4, true);
            var o = w.Spawn("C", 0, 0, ["Counter"]);
            w.Frame(Dt);
            True(d.Paused, "paused");
            Eq(4, d.PausedAt!.Line);
            Eq("C", d.PausedAt.Object.Name);
            Eq(1.0, Var(o, "x"), "line 4 hasn't run yet");
            double t = w.Time;
            Frames(w, 10);
            Eq(t, w.Time, "the clock stands still while paused");
            Eq(1.0, Var(o, "x"));
            var run = w.Running(o);
            True(run.Count == 1 && run[0].AtBreak && run[0].Line == 4, "the running 'when' shows it is stopped at line 4");
            d.Resume();
            w.Frame(Dt);
            Eq(3.0, Var(o, "x"), "resumed: the rest ran");
            Eq(false, d.Paused);
        });

        test("breakpoints are kept per mechanism and line, case-insensitive", () =>
        {
            var d = new Debugger();
            Eq(true, d.ToggleBreakpoint("Coin", 7));
            True(d.HasBreakpoint("coin", 7), "case-insensitive");
            Eq(false, d.ToggleBreakpoint("COIN", 7));
            d.SetBreakpoint("Coin", 3, true);
            d.SetBreakpoint("Health", 9, true);
            Eq("3", string.Join(",", d.BreakpointLines("Coin")));
            d.RenameMechanism("Coin", "Gem");
            True(d.HasBreakpoint("Gem", 3) && !d.HasBreakpoint("Coin", 3), "follows a rename");
            d.ClearBreakpoints("Gem");
            Eq(1, d.Breakpoints().Count);
        });

        test("pause holds every frame; step runs exactly one", () =>
        {
            var (w, d) = Debugged("mechanism Ticker\n    when tick\n        n += 1\n");
            var o = w.Spawn("T", 0, 0, ["Ticker"]);
            Frames(w, 3);
            Eq(3.0, Var(o, "n"));
            d.Pause();
            Frames(w, 5);
            Eq(3.0, Var(o, "n"), "paused");
            double t = w.Time;
            d.StepFrame();
            Eq(4.0, Var(o, "n"), "one step = one frame");
            True(Math.Abs(w.Time - t - Dt) < 1e-9, "the clock moved one frame");
            True(d.Paused, "still paused after a step");
            d.Resume();
            Frames(w, 2);
            Eq(6.0, Var(o, "n"));
        });

        test("a breakpoint in a loop stops every round; step goes on to the next hit", () =>
        {
            var (w, d) = Debugged("mechanism Loop\n    when start\n        repeat 3\n            n += 1\n            wait 0\n");
            d.SetBreakpoint("Loop", 4, true);
            var o = w.Spawn("L", 0, 0, ["Loop"]);
            w.Frame(Dt);
            True(d.Paused && Var(o, "n") == null, "stopped before the first n += 1");
            d.StepFrame();
            Eq(1.0, Var(o, "n"));
            d.StepFrame();
            True(d.PausedAt is { Line: 4 } && Var(o, "n") is 1.0, "stopped again at the next round");
            d.Resume();
            w.Frame(Dt);
            Eq(2.0, Var(o, "n"));
            w.Frame(Dt);
            True(d.Paused && d.PausedAt is { Line: 4 } && Var(o, "n") is 2.0, "and again at the third");
        });

        test("a breakpoint in a command (to) and in a variant's inherited code names whose code it is", () =>
        {
            var (w, d) = Debugged("mechanism Slime\n    when start\n        y = 0\n        hop\n    to hop\n        y = 5\nmechanism RedSlime from Slime\n    param red = true\n");
            d.SetBreakpoint("Slime", 6, true);
            var o = w.Spawn("R", 0, 0, ["RedSlime"]);
            w.Frame(Dt);
            True(d.Paused, "paused in the inherited command");
            Eq("Slime", d.PausedAt!.Source);
            Eq("RedSlime", d.PausedAt.Mechanism.Def.Name);
            Eq("R", d.PausedAt.Object.Name);
            d.Resume();
            w.Frame(Dt);
            Eq(5.0, Var(o, "y"));
        });

        test("a command used as a value never stops at a breakpoint", () =>
        {
            var (w, d) = Debugged("mechanism V\n    when start\n        x = twice(4)\n    to twice n\n        return n * 2\n");
            d.SetBreakpoint("V", 5, true);
            var o = w.Spawn("V", 0, 0, ["V"]);
            w.Frame(Dt);
            Eq(false, d.Paused);
            Eq(8.0, Var(o, "x"));
        });

        test("running 'when's: event, line and what they wait for", () =>
        {
            var (w, d) = Debugged("mechanism Waiter\n    when start\n        wait 2\n        ready = true\n    when told \"go\" n\n        wait until ready\n");
            var o = w.Spawn("W", 0, 0, ["Waiter"]);
            w.Frame(Dt);
            w.Tell(o, "go", [1.0]);
            var run = w.Running(o);
            Eq(2, run.Count);
            string a = run[0].Describe(), b = run[1].Describe();
            True(a.StartsWith("Waiter · when start · line 3 · waiting 2.0s") || a.StartsWith("Waiter · when start · line 3 · waiting 1.9s"), a);
            Eq("Waiter · when told \"go\" n · line 6 · waiting until ready", b);
            var h = w.Spawn("H", 0, 0, ["Health"]);
            w.Frame(Dt);
            w.Tell(h, "damage", [1.0]);
            var hr = w.Running(h);
            True(hr.Count == 1 && hr[0].Describe().StartsWith("Health · when told \"damage\" amount · line ") && hr[0].Waiting.StartsWith("waiting 1"), hr.Count > 0 ? hr[0].Describe() : "none");
        });

        test("a variable changed while paused is what the game uses next", () =>
        {
            var (w, d) = Debugged("mechanism Mover\n    param speed: number = 1\n    when tick\n        self.x += speed\n");
            var o = w.Spawn("M", 0, 0, ["Mover"]);
            Frames(w, 2);
            Eq(2.0, o.X);
            d.Pause();
            Eq(null, Debugger.SetValue(o.Mechanisms[0].Vars, "speed", "10"));
            Eq(10.0, Var(o, "speed"));
            True(Debugger.SetValue(o.Mechanisms[0].Vars, "speed", "fast") != null, "a number stays a number");
            Frames(w, 3);
            Eq(2.0, o.X, "nothing moves while paused");
            d.StepFrame();
            Eq(12.0, o.X);
            w.Globals["score"] = 1.0;
            Eq(null, Debugger.SetValue(w.Globals, "score", "42"));
            Eq(42.0, w.Globals["score"]);
            Eq(null, Debugger.SetValue(w.Globals, "name", "\"Ann\""));
            Eq("Ann", w.Globals["name"]);
            Eq(null, Debugger.SetValue(w.Globals, "items", "[1, 2]"));
            Eq("[1, 2]", World.ToText(w.Globals["items"]));
        });

        test("the timeline records touches, signals, messages, keys, prints, errors and scene changes", () =>
        {
            var (w, d) = Debugged("mechanism Pad\n    when touched by \"player\"\n        print \"hit\"\n        emit pressed\n        tell other \"bump\" 2\n        x = 1 / 0\n    when key \"space\": goto_scene \"Level 2\"\n");
            w.UseScenes(["Level 1", "Level 2"]);
            var pad = w.Spawn("Pad", 0, 0, ["Pad"]);
            var hero = w.Spawn("Hero", 0, 0, null, null, "player");
            w.Frame(Dt);
            w.PressKey("space");
            var kinds = d.Events.Select(e => e.Kind).ToList();
            foreach (var k in new[] { DebugEventKind.Touched, DebugEventKind.Print, DebugEventKind.Emit, DebugEventKind.Told, DebugEventKind.Error, DebugEventKind.Key, DebugEventKind.Scene })
                True(kinds.Contains(k), "missing " + k + ": " + string.Join(", ", kinds));
            var err = d.Events.First(e => e.Kind == DebugEventKind.Error);
            Eq("Pad", err.Source);
            Eq(6, err.Line);
            Eq(pad.Id, err.ObjectId);
            var told = d.Events.First(e => e.Kind == DebugEventKind.Told);
            Eq("Hero told \"bump\" 2", told.Text);
            var heroOnly = d.EventsFor(hero.Id);
            True(heroOnly.Count >= 2 && heroOnly.All(e => e.Involves(hero.Id)), "filter by object");
            True(heroOnly.Any(e => e.Kind == DebugEventKind.Touched), "a touch involves both objects");
            True(d.EventsFor(0)[0].Kind == DebugEventKind.Scene, "newest first");
        });

        test("the timeline keeps the last 200 events", () =>
        {
            var (w, d) = Debugged("mechanism Spam\n    when tick\n        print \"x\"\n");
            w.Spawn("S", 0, 0, ["Spam"]);
            Frames(w, 260);
            Eq(Debugger.MaxEvents, d.Events.Count);
        });

        test("no debugger: nothing is recorded and the game runs the same", () =>
        {
            var w = NewWorld(Counter);
            var o = w.Spawn("C", 0, 0, ["Counter"]);
            w.Frame(Dt);
            Eq(3.0, Var(o, "x"));
            Eq(0, w.Running().Count);
            var d = new Debugger();
            d.Attach(w);
            d.Detach();
            Eq(null, w.Debug);
        });
    }

    // ---------------- Param types ----------------

    static void ParamTypeTests(Action<string, Action> test)
    {
        const string typed = "mechanism Typed\n" +
            "    param target: Object\n" +
            "    param route: Path = [[0, 0], [100, 0]]\n" +
            "    param items: List = [1, \"a\"]\n" +
            "    param costs: List of number = [3, 4]\n" +
            "    param friends: List of Object\n" +
            "    param tint: Color = \"#E05D5D\"\n" +
            "    param jump_key: Key = \"space\"\n" +
            "    param level: Choice \"easy\", \"normal\", \"hard\" = \"normal\"\n";

        test("param types parse and print back the same", () =>
        {
            var m = Parser.Parse(typed)[0];
            Eq("Object|Path|List|List of number|List of Object|Color|Key|Choice", string.Join("|", m.Params.Select(p => p.Type)));
            Eq("easy,normal,hard", string.Join(",", m.Params[7].Options!));
            string printed = Printer.Print(m);
            Eq(typed, printed);
            Eq(printed, Printer.Print(Parser.Parse(printed)[0]));
            Eq(ParamKind.Path, ParamTypes.Kind("Path"));
            Eq(ParamKind.List, ParamTypes.Kind("List of Object"));
            Eq("Object", ParamTypes.ItemType("List of Object"));
            var (t, opts) = Parser.ParseTypeText("Choice \"a\", \"b\"");
            True(t == "Choice" && opts!.Count == 2, "the type field reads a Choice");
            Eq("List of Sound", Parser.ParseTypeText("List of Sound").Type);
        });

        test("friendly errors for a Choice without options and a List of nothing", () =>
        {
            Throws("options in quotes", () => Parser.Parse("mechanism A\n    param level: Choice = \"easy\"\n"));
            Throws("type of the items", () => Parser.Parse("mechanism A\n    param items: List of\n"));
        });

        test("saved values convert to each type (tolerant)", () =>
        {
            var path = (List<object?>)World.ConvertParam(new List<object?> { new List<object?> { 1.0, "2" }, "30,40", "junk" }, "Path")!;
            Eq("[[1, 2], [30, 40]]", World.ToText(path));
            Eq("[[5, 6]]", World.ToText(World.ConvertParam("[[5, 6]]", "Path")));
            Eq("[1, 2.5]", World.ToText(World.ConvertParam(new List<object?> { "1", 2.5 }, "List of number")));
            Eq("[7]", World.ToText(World.ConvertParam(7.0, "List")));
            Eq("[]", World.ToText(World.ConvertParam(null, "List of text")));
            Eq("space", World.ConvertParam(" Space ", "Key"));
            Eq("#E05D5D", World.ConvertParam("#E05D5D", "Color"));
            Eq("hard", World.ConvertParam("hard", "Choice"));
            Eq("Boss", World.ConvertParam("Boss", "Object"));
            Eq(null, World.ConvertParam("", "Object"));
            var original = new List<object?> { 1.0 };
            var copy = (List<object?>)World.ConvertParam(original, "List of number")!;
            copy.Add(2.0);
            Eq(1, original.Count, "the game gets a copy of the saved list");
            True(ParamTypes.TryConstant(Parser.ParseExpression("[[0, -1], [2, 3]]"), out var c) && World.ToText(c) == "[[0, -1], [2, 3]]", "a literal default");
            Eq("[[0, -1], [2, 3]]", Printer.Print(ParamTypes.ToExpr(c)));
            Eq(false, ParamTypes.TryConstant(Parser.ParseExpression("max * 2"), out _));
        });

        test("an Object param is the scene object (even one placed later); a List of Object too", () =>
        {
            var w = NewWorld("mechanism Pointer\n    param target: Object\n    param pals: List of Object\n    when start\n        d = distance(self, target)\n        tell target \"ping\"\n        n = length(pals)\n" +
                             "mechanism Pong\n    when told \"ping\": got = true\n");
            var scene = new Scene();
            scene.Add(new SceneObject { Name = "Pointer", X = 0, Y = 0, Mechanisms = [new SceneMechanism { Name = "Pointer", Params = { ["target"] = "Boss", ["pals"] = new List<object?> { "Boss", "Nobody" } } }] });
            scene.Add(new SceneObject { Name = "Boss", X = 300, Y = 400, Mechanisms = [new SceneMechanism { Name = "Pong" }] });
            var map = w.LoadScene(scene);
            w.Frame(Dt);
            var pointer = map.Keys.First(o => o.Name == "Pointer");
            var boss = map.Keys.First(o => o.Name == "Boss");
            Eq<object?>(boss, Var(pointer, "target"));
            Eq(500.0, Var(pointer, "d"));
            Eq(true, Var(boss, "got"));
            var pals = (List<object?>)Var(pointer, "pals")!;
            Eq<object?>(boss, pals[0]);
            Eq<object?>("Nobody", pals[1]);
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
        });

        test("a Path param works with for each and move to", () =>
        {
            var w = NewWorld("mechanism Walker\n    param route: Path = [[0, 0], [50, 0], [50, 40]]\n    when start\n        for each p in route\n            move to p[0], p[1] in 0.1\n        done = true\n");
            var scene = new Scene();
            scene.Add(new SceneObject { Name = "W", Mechanisms = [new SceneMechanism { Name = "Walker", Params = { ["route"] = new List<object?> { new List<object?> { 10.0, 10.0 }, new List<object?> { 70.0, 20.0 } } } }] });
            var o = w.LoadScene(scene).Keys.First();
            w.RunFor(1);
            Eq(true, Var(o, "done"));
            Eq(70.0, Math.Round(o.X, 3));
            Eq(20.0, Math.Round(o.Y, 3));
            var o2 = w.Spawn("W2", 0, 0, ["Walker"]);
            w.RunFor(1);
            Eq(50.0, Math.Round(o2.X, 3), "the default path");
            Eq(40.0, Math.Round(o2.Y, 3));
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
        });

        test("list, path, colour, key and choice values survive project save and load", () =>
        {
            var p = new Project();
            var s = new Scene();
            s.Add(new SceneObject
            {
                Name = "Typed",
                Mechanisms = [new SceneMechanism
                {
                    Name = "Typed",
                    Params =
                    {
                        ["route"] = new List<object?> { new List<object?> { 10.0, 20.0 }, new List<object?> { 30.5, 40.0 } },
                        ["items"] = new List<object?> { 1.0, "a", true },
                        ["tint"] = "#6FBF73", ["jump_key"] = "w", ["level"] = "hard", ["target"] = "Boss"
                    }
                }]
            });
            p.Scenes.Add(s);
            var back = Project.FromJson(p.ToJson());
            var ps = back.Scenes[0].Objects[0].Mechanisms[0].Params;
            Eq("[[10, 20], [30.5, 40]]", World.ToText(ps["route"]));
            Eq("[1, \"a\", true]", World.ToText(ps["items"]));
            Eq("#6FBF73", ps["tint"]);
            Eq("w", ps["jump_key"]);
            Eq("hard", ps["level"]);
            Eq("Boss", ps["target"]);
            // A hand-written file with the path as text still loads.
            var old = Project.FromJson("{\"scenes\":[{\"objects\":[{\"name\":\"T\",\"mechanisms\":[{\"name\":\"Typed\",\"params\":{\"route\":\"[[1,2],[3,4]]\"}}]}]}]}");
            Eq("[[1, 2], [3, 4]]", World.ToText(World.ConvertParam(old.Scenes[0].Objects[0].Mechanisms[0].Params["route"], "Path")));
        });
    }

    // ---------------- Variants ----------------

    const string Slime = "mechanism Slime\n    param speed: number = 60\n    param color: Color = \"#6FBF73\"\n    uses Health max 2\n\n    when start\n        x = speed\n        said = \"\"\n        greet\n\n    to greet\n        said = \"blub\"\n\n    to power\n        return 2\n";

    static void VariantTests(Action<string, Action> test)
    {
        test("a variant has its parent's params, uses, whens and commands", () =>
        {
            var w = NewWorld(Slime + "mechanism RedSlime from Slime\n");
            var o = w.Spawn("R", 0, 0, ["RedSlime"]);
            w.Frame(Dt);
            Eq(60.0, Var(o, "speed"));
            Eq("#6FBF73", Var(o, "color"));
            Eq(60.0, Var(o, "x"));
            Eq("blub", Var(o, "said"));
            True(o.Mechanisms[0].Children.ContainsKey("Health"), "uses Health");
            Eq(2.0, o.Mechanisms[0].Children["Health"].Vars["hp"]);
            Eq("RedSlime", o.Mechanisms[0].Def.Name);
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
        });

        test("'param speed = 90' changes the default and keeps the type", () =>
        {
            var w = NewWorld(Slime + "mechanism RedSlime from Slime\n    param speed = 90\n    param angry: bool = true\n");
            var def = w.TryDef("RedSlime")!;
            Eq("speed:number|color:Color|angry:bool", string.Join("|", def.Params.Select(p => p.Name + ":" + p.Type)));
            var o = w.Spawn("R", 0, 0, ["RedSlime"]);
            w.Frame(Dt);
            Eq(90.0, Var(o, "x"));
            Eq(true, Var(o, "angry"));
            var s = w.Spawn("S", 0, 0, ["Slime"]);
            w.Frame(Dt);
            Eq(60.0, Var(s, "x"), "the parent is unchanged");
        });

        test("a command with the same name replaces the parent's (even inside inherited whens)", () =>
        {
            var w = NewWorld(Slime + "mechanism RedSlime from Slime\n    to greet\n        said = \"GRR\"\n");
            var o = w.Spawn("R", 0, 0, ["RedSlime"]);
            w.Frame(Dt);
            Eq("GRR", Var(o, "said"));
            Eq("greet,power", string.Join(",", w.TryDef("RedSlime")!.Procs.Select(p => p.Name)), "greet replaced, power kept");
        });

        test("a variant's whens are added to the parent's", () =>
        {
            var w = NewWorld(Slime + "mechanism RedSlime from Slime\n    when start\n        y = 1\n");
            var o = w.Spawn("R", 0, 0, ["RedSlime"]);
            w.Frame(Dt);
            Eq(60.0, Var(o, "x"));
            Eq(1.0, Var(o, "y"));
            var def = w.TryDef("RedSlime")!;
            Eq("Slime,", string.Join(",", def.Whens.Select(x => x.From)));
        });

        test("a variant of a variant, and editing the parent updates the variants", () =>
        {
            var w = NewWorld(Slime + "mechanism RedSlime from Slime\n    param speed = 90\nmechanism BigRedSlime from RedSlime\n    param size = 3\n");
            var o = w.Spawn("B", 0, 0, ["BigRedSlime"]);
            w.Frame(Dt);
            Eq(90.0, Var(o, "x"));
            Eq(3.0, Var(o, "size"));
            w.Load(Slime.Replace("said = \"blub\"", "said = \"plop\""));
            var o2 = w.Spawn("B2", 0, 0, ["BigRedSlime"]);
            w.Frame(Dt);
            Eq("plop", Var(o2, "said"));
        });

        test("friendly errors: a loop of variants, a missing parent, coming from itself", () =>
        {
            var w = NewWorld("mechanism A from B\nmechanism B from C\nmechanism C from A\n");
            Throws("loop", () => w.Spawn("x", 0, 0, ["A"]));
            var w2 = NewWorld("mechanism Ghost from Nothing\n");
            Throws("no mechanism called 'Nothing'", () => w2.Spawn("x", 0, 0, ["Ghost"]));
            Throws("itself", () => Parser.Parse("mechanism A from A\n"));
            var scene = new Scene();
            scene.Add(new SceneObject { Name = "G", Mechanisms = [new SceneMechanism { Name = "Ghost" }] });
            w2.LoadScene(scene);
            True(w2.Errors.Any(e => e.Contains("Nothing")), "the scene reports it and goes on");
        });

        test("variants print and parse back the same", () =>
        {
            const string src = "mechanism RedSlime from Slime\n    param speed = 90\n\n    when touched by \"player\"\n        tell other \"damage\" 1\n\n    to greet\n        said = \"GRR\"\n";
            var m = Parser.Parse(src)[0];
            Eq("Slime", m.Base);
            Eq(src, Printer.Print(m));
            Eq("RedSlime (from Slime)", Variants.Label(m));
            var all = Parser.Parse(Slime + "mechanism RedSlime from Slime\nmechanism Big from RedSlime\n");
            Eq("RedSlime,Slime", string.Join(",", Variants.Ancestors(all[2], n => all.FirstOrDefault(d => d.Name == n))));
        });

        test("an error in inherited code names the parent; a scene sets inherited params", () =>
        {
            var w = NewWorld("mechanism Base1\n    param hits: number = 1\n    when start\n        x = 1 / 0\nmechanism Child1 from Base1\n");
            w.Spawn("c", 0, 0, ["Child1"]);
            w.Frame(Dt);
            True(w.Errors.Count == 1 && w.Errors[0].StartsWith("Child1 (from Base1, line 4)"), string.Join(" | ", w.Errors));
            var w2 = NewWorld(Slime + "mechanism RedSlime from Slime\n");
            var scene = new Scene();
            scene.Add(new SceneObject { Name = "R", Mechanisms = [new SceneMechanism { Name = "RedSlime", Params = { ["speed"] = "120" } }] });
            var o = w2.LoadScene(scene).Keys.First();
            w2.Frame(Dt);
            Eq(120.0, Var(o, "x"));
        });
    }
}
