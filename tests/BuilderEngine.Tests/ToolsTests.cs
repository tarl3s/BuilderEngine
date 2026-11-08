using BuilderEngine.Plugin;
using BuilderEngine.Script;

/// <summary>Editor tools (Builder Script that edits the scene) and C# plugins. Called from Program.cs with its Test runner.</summary>
static class ToolsTests
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

    static void InTemp(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "BE_Tools_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static IEnumerable<string> Kit(string extra = "") =>
        StarterKit.Sources().Select(s => s.Source).Concat(extra.Length > 0 ? [extra] : []);

    /// <summary>An empty 800×450 scene.</summary>
    static Scene Empty() => new() { Name = "Test", Width = 800, Height = 450 };

    static int Count(Scene s, string baseName) => s.Objects.Count(o => EditorTools.BaseName(o.Name) == baseName);

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Editor tools");

        test("tool events: parse, print back, and 'when tool' alone is still a signal", () =>
        {
            const string src = "mechanism T\n    when tool click at x, y\n        print x\n    when tool drag at pos\n        print pos\n    when tool release\n        print 1\n    when tool used\n        print 2\n    when tool key \"r\"\n        print 3\n    when tool\n        print 4\n";
            var m = Parser.Parse(src)[0];
            Eq(EventKind.Tool, m.Whens[0].Event.Kind); Eq("click", m.Whens[0].Event.Name); Eq("x, y", m.Whens[0].Event.Bind);
            Eq("pos", m.Whens[1].Event.Bind); Eq(null, m.Whens[2].Event.Bind);
            Eq("used", m.Whens[3].Event.Name); Eq("r", m.Whens[4].Event.Filter);
            Eq(EventKind.Signal, m.Whens[5].Event.Kind, "bare 'when tool'");
            string printed = Printer.Print([m]);
            True(printed.Contains("when tool click at x, y") && printed.Contains("when tool drag at pos") && printed.Contains("when tool key \"r\""), printed);
            Eq(printed, Printer.Print(Parser.Parse(printed)), "round trip");
            True(EditorTools.IsTool(m), "a mechanism with 'when tool' is a tool");
            try { Parser.Parse("mechanism T\n    when tool clik at x, y: print 1\n"); throw new Exception("no error"); }
            catch (ScriptError e) { True(e.Message.Contains("click"), e.Message); }
        });

        test("tool events in blocks: the 'when' menu offers tool, and its field reads back", () =>
        {
            True(BlockEdits.EventKinds.Contains(EventKind.Tool), "EventKinds has Tool");
            Eq("click at x, y", Printer.EventTail(BlockEdits.DefaultEvent(EventKind.Tool)));
            var ev = BlockEdits.ParseEvent(EventKind.Tool, "drag at a, b");
            Eq("drag", ev.Name); Eq("a, b", ev.Bind);
        });

        test("starter kit has the five tools, and they do nothing in a game", () =>
        {
            var defs = StarterKit.Sources().SelectMany(s => Parser.Parse(s.Source)).ToList();
            var tools = EditorTools.Find(defs).Select(t => t.Name).ToList();
            foreach (var name in new[] { "PlatformPainter", "CoinLine", "Eraser", "Scatter", "RoomMaker" })
                True(tools.Contains(name), name + " is a starter tool");
            var w = new World();
            StarterKit.LoadInto(w);
            foreach (var name in tools) w.Spawn(name, 10, 10, [name]);
            w.RunFor(0.5);
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
            Eq("Click or drag to paint solid ground, one tile at a time.", EditorTools.Description(StarterKit.Sources().First(s => s.File == "Tools.bs").Source, "PlatformPainter"));
        });

        test("place: from a mechanism or a copy of a scene object; 'placed', sizes and names", () =>
        {
            const string src = "mechanism Placer\n    when tool click at x, y\n        place \"Coin\" at x, y\n        place \"Coin\" at x + 40, y with 20, 10\n        placed.y += 5\n        place \"Rock\" at 300, 300\n        select placed\n";
            var s = Empty();
            s.Add(new SceneObject { Name = "Rock", X = 0, Y = 0, Width = 50, Height = 30, Color = "#123456", Tags = ["stone"], Mechanisms = [new SceneMechanism { Name = "Solid" }] });
            var r = new ToolRunner("Placer", Kit(src));
            string? undo = r.Use(s, "click", 100, 200);
            True(undo != null, "the use changed the scene");
            Eq(4, s.Objects.Count);
            var coin = s.Objects.First(o => o.Name == "Coin");
            Eq(100.0, coin.X); Eq(200.0, coin.Y); Eq("Coin", coin.Mechanisms[0].Name); Eq("#E8B04B", coin.Color);
            var coin2 = s.Objects.First(o => o.Name == "Coin 2");
            Eq(140.0, coin2.X); Eq(205.0, coin2.Y); Eq(20.0, coin2.Width); Eq(10.0, coin2.Height);
            var rock2 = s.Objects.First(o => o.Name == "Rock 2");
            Eq(300.0, rock2.X); Eq(50.0, rock2.Width); Eq("#123456", rock2.Color); Eq("stone", rock2.Tags[0]); Eq("Solid", rock2.Mechanisms[0].Name);
            Eq(rock2.Id, r.SelectedId, "select placed");
            Eq(4, s.Objects.Select(o => o.Id).Distinct().Count(), "ids are unique");
        });

        test("erase, fill_row, objects_at, snap and moving objects with obj.x", () =>
        {
            const string src = "mechanism Edit\n    when tool click at x, y\n        erase at x, y\n    when tool used\n        fill_row \"Solid\" from 0 to 100 at 400 with 32, 16\n        erase \"Junk\"\n        for each c in all(\"coin\")\n            c.x = snap(c.x + 5, 32)\n        n = length(objects_at(20, 410))\n        print \"under: \" + text(n)\n";
            var s = Empty();
            s.Add(new SceneObject { Name = "Back", X = 0, Y = 0, Width = 800, Height = 450 });
            s.Add(new SceneObject { Name = "Top", X = 90, Y = 90, Width = 20, Height = 20 });
            s.Add(new SceneObject { Name = "Junk", X = 500, Y = 10 });
            s.Add(new SceneObject { Name = "Gold", X = 70, Y = 10, Tags = ["coin"] });
            var r = new ToolRunner("Edit", Kit(src));
            var said = new List<string>();
            r.Message += said.Add;
            r.Use(s, "click", 100, 100);
            True(s.Objects.All(o => o.Name != "Top"), "erase at removes the top object only");
            True(s.Objects.Any(o => o.Name == "Back"), "the one under it stays");
            r.Use(s, "used");
            Eq(4, Count(s, "Solid"), "0..100 in 32 px steps = 4 tiles");
            Eq(96.0, s.Objects.Where(o => EditorTools.BaseName(o.Name) == "Solid").Max(o => o.X));
            True(s.Objects.All(o => o.Name != "Junk"), "erase by name");
            Eq(64.0, s.Objects.First(o => o.Name == "Gold").X, "snap(75, 32) = 64");
            True(said.Contains("under: 2"), string.Join(" | ", said));
        });

        test("one use (press, drags, release) is one undo step; a use that changes nothing gives none", () =>
        {
            var s = Empty();
            s.Add(new SceneObject { Name = "Ground", X = 0, Y = 400, Width = 800, Height = 50, Tags = ["ground"], Mechanisms = [new SceneMechanism { Name = "Solid" }] });
            string original = s.ToJson();
            var undo = new List<string>();
            var r = new ToolRunner("PlatformPainter", Kit());
            r.BeginUse(s);
            r.Fire("click", 10, 10);
            r.Fire("drag", 45, 12);
            r.Fire("drag", 80, 15);
            r.Fire("drag", 82, 15); // same tile again: nothing new
            r.Fire("release", 82, 15);
            if (r.EndUse() is { } before) undo.Add(before);
            Eq(1, undo.Count, "one undo step");
            Eq(3, Count(s, "Ground") - 1, "three painted tiles");
            var tiles = s.Objects.Where(o => o.Name != "Ground").ToList();
            True(tiles.All(t => t.Width == 32 && t.Height == 32 && t.Y == 0 && t.Has("Solid") && t.Tags.Contains("ground")), "tiles are 32×32 copies of Ground");
            Eq("0,32,64", string.Join(",", tiles.Select(t => t.X)));
            Eq(original, undo[0], "the undo step is the scene before the use");
            Eq(null, r.Use(s, "click", 500, 420), "painting on the ground itself changes nothing");
            // Undo works: restoring the snapshot brings back the scene as it was.
            Eq(1, Scene.FromJson(undo[0]).Objects.Count);
        });

        test("starter tools: CoinLine, Eraser, Scatter, RoomMaker, params from the options bar", () =>
        {
            var s = Empty();
            s.Add(new SceneObject { Name = "Coin 1", X = 0, Y = 0, Width = 16, Height = 16, Color = "#E8B04B", Tags = ["coin"], Mechanisms = [new SceneMechanism { Name = "Coin" }] });
            var line = new ToolRunner("CoinLine", Kit());
            var said = new List<string>();
            line.Message += said.Add;
            Eq(null, line.Use(s, "click", 100, 300), "the first click only remembers the start");
            True(said.Any(m => m.Contains("ends")), "it says what to do next");
            True(line.Use(s, "click", 260, 300) != null, "the second click places coins");
            Eq(6, Count(s, "Coin"), "1 + 5 coins (160 px / 40 gap = 4 gaps, 5 coins)");
            var placedCoins = s.Objects.Where(o => o.Name != "Coin 1").OrderBy(o => o.X).ToList();
            Eq(92.0, placedCoins[0].X, "centred on the click"); Eq(252.0, placedCoins[^1].X);
            True(placedCoins.All(c => c.Has("Coin") && c.Tags.Contains("coin")), "copies of Coin 1");

            var eraser = new ToolRunner("Eraser", Kit());
            eraser.BeginUse(s);
            eraser.Fire("click", 100, 305);
            eraser.Fire("drag", 140, 305);
            True(eraser.EndUse() != null, "erasing changed the scene");
            Eq(4, Count(s, "Coin"));

            var scatter = new ToolRunner("Scatter", Kit());
            scatter.Params["amount"] = 6.0;
            scatter.BeginUse(s);
            scatter.Fire("click", 400, 100);
            scatter.Fire("drag", 500, 160);
            scatter.Fire("release", 600, 200);
            scatter.EndUse();
            var inBox = s.Objects.Where(o => o.X >= 400 && o.X + o.Width <= 600 && o.Y >= 100 && o.Y + o.Height <= 200).ToList();
            Eq(6, inBox.Count, "amount from the options bar, all inside the box");

            var room = new ToolRunner("RoomMaker", Kit());
            room.Params["thickness"] = "8"; // text from a field still works
            int before = s.Objects.Count;
            room.BeginUse(s);
            room.Fire("click", 40, 40);
            room.Fire("release", 200, 160);
            True(room.EndUse() != null, "a room was made");
            var walls = s.Objects.Skip(before).ToList();
            Eq(4, walls.Count);
            True(walls.All(wl => wl.Has("Solid")), "walls are solid (no Ground to copy: the Solid mechanism)");
            Eq(160.0, walls[0].Width); Eq(8.0, walls[0].Height); Eq(40.0, walls[0].X); Eq(40.0, walls[0].Y);
            Eq(152.0, walls[1].Y, "bottom wall");
            Eq(192.0, walls[3].X, "right wall");
        });

        test("a tool that fails reports it and leaves the scene alone", () =>
        {
            var s = Empty();
            var r = new ToolRunner("Bad", Kit("mechanism Bad\n    when tool click at x, y\n        place \"Nope\" at x, y\n"));
            var said = new List<string>();
            r.Message += said.Add;
            Eq(null, r.Use(s, "click", 1, 1));
            Eq(0, s.Objects.Count);
            True(r.Errors.Count == 1 && r.Errors[0].Contains("Nope"), string.Join(" | ", r.Errors));
            True(said.Any(m => m.Contains("Nope")), "the message reaches the editor");
            Eq(false, r.Use(s, "drag", 1, 1) != null, "no drag event: nothing");
            try { _ = new ToolRunner("Coin", Kit()); throw new Exception("no error"); }
            catch (ScriptError e) { True(e.Message.Contains("isn't a tool"), e.Message); }
        });

        test("tool variables last from one use to the next; 'selected' is the editor's selection", () =>
        {
            const string src = "mechanism Counter\n    when tool used\n        uses_count += 1\n        print uses_count\n        if selected\n            selected.width = 99\n";
            var s = Empty();
            var box = s.Add(new SceneObject { Name = "Box" });
            var r = new ToolRunner("Counter", Kit(src));
            var said = new List<string>();
            r.Message += said.Add;
            r.Use(s, "used");
            r.SelectedId = box.Id;
            r.Use(s, "used");
            Eq("1,2", string.Join(",", said));
            Eq(99.0, box.Width);
        });

        Console.WriteLine("Plugins");

        test("a plugin's commands and functions reach every new world; failures become script errors", () =>
        {
            using var pm = new PluginManager();
            var lp = pm.Add(new TestPlugin());
            Eq("TestPlugin", lp.Name);
            Eq("double_it amount|triple(x)|explode", string.Join("|", lp.Commands.Select(c => c.Usage)));
            Eq(1, pm.Errors.Count, "the 'print' command was refused");
            True(pm.Errors[0].Contains("print"), pm.Errors[0]);
            var old = PluginManager.Active;
            PluginManager.Active = pm;
            try
            {
                var w = new World();
                w.Load("mechanism M\n    when start\n        double_it 21\n        y = triple(2)\n        explode\n        z = 1\n");
                w.Spawn("A", 0, 0, ["M"]);
                w.Step(1.0 / 60);
                Eq(42.0, w.Globals["doubled"]);
                Eq(6.0, w.Objects[0].Mechanisms[0].Vars["y"]);
                True(w.Errors.Count == 1 && w.Errors[0].Contains("TestPlugin failed in 'explode'") && w.Errors[0].Contains("boom"), string.Join(" | ", w.Errors));
                Eq(false, w.Objects[0].Mechanisms[0].Vars.ContainsKey("z"), "the line after the error didn't run");
                Eq(false, w.IsEditor);
                True(World.NewEditor().IsEditor, "editor worlds say so (plugins see host.IsEditor)");
                Eq(true, TestPlugin.SawEditor);
                Eq("double_it|From plugins|TestPlugin,explode|From plugins|TestPlugin", string.Join(",", PluginRegistry.BlockCommands().Select(c => $"{c.CallName}|{c.Group}|{c.Mechanism}")));
                Eq("amount", PluginRegistry.BlockCommands()[0].Def.Params[0]);
            }
            finally { PluginManager.Active = old; }
            True(PluginRegistry.BlockCommands().Count == 0 || PluginManager.Active != null, "nothing listed without active plugins");
        });

        test("a plugin whose Register throws is reported, not thrown, and skipped", () =>
        {
            using var pm = new PluginManager();
            var lp = pm.Add(new BrokenPlugin());
            Eq(1, lp.Errors.Count);
            True(pm.Errors[0].Contains("broken on purpose"), pm.Errors[0]);
            pm.InstallInto(new World()); // nothing happens
        });

        test("plugins load from a project's plugins folder (the HelloPlugin sample dll), bad files are reported", () => InTemp(dir =>
        {
            string folder = PluginManager.FolderOf(dir);
            Directory.CreateDirectory(folder);
            Eq(false, PluginManager.HasPlugins(dir), "no dll yet");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "HelloPlugin.dll"), Path.Combine(folder, "HelloPlugin.dll"));
            File.WriteAllText(Path.Combine(folder, "Broken.dll"), "not a dll");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "BuilderEngine.Plugin.dll"), Path.Combine(folder, "BuilderEngine.Plugin.dll"));
            Eq(2, PluginManager.PluginFiles(dir).Count, "the engine's own dll is skipped");
            using var pm = new PluginManager(folder);
            Eq(1, pm.Load());
            Eq("Hello", pm.Plugins[0].Name);
            Eq("wave(time, per_second)|confetti amount", string.Join("|", pm.AllCommands.Select(c => c.Usage)));
            True(pm.Errors.Count == 1 && pm.Errors[0].Contains("Broken.dll"), string.Join(" | ", pm.Errors));
            var old = PluginManager.Active;
            PluginManager.Active = pm;
            try
            {
                var w = new World();
                StarterKit.LoadInto(w);
                w.Load("mechanism M\n    when start\n        v = wave(0.25)\n        confetti 5\n");
                w.Spawn("A", 100, 100, ["M"]);
                w.Step(1.0 / 60);
                Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
                Eq(1.0, Math.Round((double)w.Objects[0].Mechanisms[0].Vars["v"]!, 6));
                w.Step(1.0 / 60);
                Eq(5, w.Objects.Count(o => o.Tags.Contains("confetti")));
            }
            finally { PluginManager.Active = old; }
            // The file isn't locked: it can be replaced (rebuilt) while loaded, then reloaded.
            File.Copy(Path.Combine(AppContext.BaseDirectory, "HelloPlugin.dll"), Path.Combine(folder, "HelloPlugin.dll"), overwrite: true);
            Eq(1, pm.Load());
            pm.Unload();
            Eq(0, pm.Plugins.Count);
        }));

        test("plugin consent: asked once per project, remembered in the settings", () => InTemp(dir =>
        {
            var s = new AppSettings(Path.Combine(dir, "appdata"));
            string game = Path.Combine(dir, "Game");
            Eq(false, PluginConsent.IsAllowed(s, game));
            Eq(false, PluginConsent.WasAsked(s, game));
            True(PluginConsent.Decline(s, game), "saved");
            Eq(true, PluginConsent.WasAsked(s, game)); Eq(false, PluginConsent.IsAllowed(s, game));
            True(PluginConsent.Allow(s, game + "\\"), "saved");
            Eq(true, PluginConsent.IsAllowed(new AppSettings(Path.Combine(dir, "appdata")), game), "remembered (same folder written differently)");
            Eq(false, PluginConsent.WasDeclined(s, game));
            Eq(false, PluginConsent.IsAllowed(s, Path.Combine(dir, "Other")), "per project");
            var ro = new AppSettings(Path.Combine(dir, "appdata2"), readOnly: true);
            Eq(false, PluginConsent.Allow(ro, game), "a read-only session doesn't save it");
        }));
    }

    sealed class TestPlugin : IBuilderPlugin
    {
        public static bool SawEditor;

        public void Register(IPluginHost host)
        {
            if (host.IsEditor) SawEditor = true;
            host.AddCommand("double_it", c => c.Globals["doubled"] = c.Number(c.Arg(0)) * 2, "doubles a number into 'doubled'", "amount");
            host.AddFunction("triple", a => (int)World.ToNumber(a[0], 0) * 3, "three times x", "x"); // an int: becomes a number
            host.AddCommand("explode", _ => throw new InvalidOperationException("boom"));
            host.AddCommand("print", _ => { }); // the engine has it: refused
        }
    }

    sealed class BrokenPlugin : IBuilderPlugin
    {
        public void Register(IPluginHost host) => throw new Exception("broken on purpose");
    }
}
