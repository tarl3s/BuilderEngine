using BuilderEngine.Script;
using BuilderEngine.Script.Samples;

/// <summary>Systems (sets of mechanisms that work together) and "Export game…" (headless). Called from Program.cs.</summary>
static class SystemsTests
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

    static void NoErrors(World w) => True(w.Errors.Count == 0, "script errors: " + string.Join(" | ", w.Errors));

    static void InTemp(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "BE_Sys_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>A world playing the project's first scene like the editor and the exported player do (one frame run).</summary>
    static World Play(Project p, Scene? scene = null, string dir = "")
    {
        var w = GameBoot.NewWorld(p, dir);
        NoErrors(w);
        w.LoadScene(scene ?? p.Scenes[0]);
        w.Frame(1.0 / 60);
        return w;
    }

    static Project WithGround()
    {
        var p = Project.CreateEmpty("Systems Test");
        p.Scenes[0].Add(new SceneObject { Name = "Ground", X = 0, Y = 400, Width = 800, Height = 50, Mechanisms = [new SceneMechanism { Name = "Solid" }] });
        return p;
    }

    static MechanismInstance Mech(GameObject o, string name) => o.AllMechanisms().First(m => string.Equals(m.Def.Name, name, StringComparison.OrdinalIgnoreCase));

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Systems (Systems.cs, World.Systems.cs)");
        test("a system parses: 'system', 'includes' with settings, members", () =>
        {
            var d = Parser.Parse("system Kit\n    includes Coin value worth\n    includes Solid\n    param worth: number = 5\n    uses Hotbar slots 4\n\n    when start\n        make Crafting at 0, 0 with 10, 10\n")[0];
            True(d.IsSystem, "is a system");
            Eq(2, d.Includes.Count, "includes");
            Eq(true, d.Includes[0].Args.ContainsKey("value"), "includes setting");
            Eq("Coin,Solid,Hotbar,Crafting", string.Join(",", Systems.Members(d)), "members");
            Eq("Coin,Solid", string.Join(",", Systems.Parts(d)), "parts");
        });
        test("'includes' in a plain mechanism is a friendly error", () =>
        {
            try { Parser.Parse("mechanism A\n    includes Coin\n"); throw new Exception("no error"); }
            catch (ScriptError e) { True(e.Message.Contains("system A"), e.Message); }
        });
        test("a system included twice is a friendly error", () =>
        {
            try { Parser.Parse("system A\n    includes Coin\n    includes Coin value 2\n"); throw new Exception("no error"); }
            catch (ScriptError e) { True(e.Message.Contains("twice"), e.Message); }
        });
        test("every starter system round-trips through the printer (blocks ↔ code stay 1:1)", () =>
        {
            True(Systems.Starter().Count is >= 4 and <= 6, $"4-6 starter systems ({Systems.Starter().Count})");
            foreach (var (name, src) in Systems.Starter())
            {
                var defs = Parser.Parse(src);
                Eq(1, defs.Count, name);
                True(defs[0].IsSystem, name + " is a system");
                string printed = Printer.Print(defs);
                True(printed.StartsWith("system " + name + "\n"), name + " prints its 'system' line");
                var again = Parser.Parse(printed);
                Eq(printed, Printer.Print(again), name + " prints the same twice");
                Eq(defs[0].Includes.Count, again[0].Includes.Count, name + " includes");
                True(Systems.Description(src).Length > 20, name + " says what it is for");
            }
        });
        test("a variant of a system is a system and keeps its includes (a line again replaces it)", () =>
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load("system Kit\n    includes Coin value 2\n    includes Solid\n");
            w.Load("system BigKit from Kit\n    includes Coin value 10\n");
            var d = w.TryDef("BigKit")!;
            True(d.IsSystem, "variant is a system");
            Eq(2, d.Includes.Count, "includes merged");
            Eq("10", Printer.Print(d.Includes.First(u => u.Name == "Coin").Args["value"]), "the variant's setting wins");
        });

        test("adding a system to a project: one object, its source; again: the same source is reused", () =>
        {
            var p = WithGround();
            var r = Systems.Add(p.Mechanisms, p.Scenes[0], Systems.StarterSource("Inventory")!, 40, 40);
            Eq("Inventory", r.Name);
            Eq(true, p.Mechanisms.ContainsKey("Inventory"), "source in the project");
            Eq("Inventory", r.Object.Name);
            Eq(true, r.Object.Has("Inventory"), "the object carries the system");
            Eq(0, r.Missing.Count, "starter members are always there");
            var r2 = Systems.Add(p.Mechanisms, p.Scenes[0], Systems.StarterSource("Inventory")!, 80, 40);
            Eq("Inventory", r2.Name, "same system reused");
            Eq("Inventory 2", r2.Object.Name, "second object");
            p.Mechanisms["Inventory"] = p.Mechanisms["Inventory"].Replace("= 9", "= 4");
            var r3 = Systems.Add(p.Mechanisms, p.Scenes[0], Systems.StarterSource("Inventory")!, 120, 40);
            Eq("Inventory2", r3.Name, "a different one with that name comes in as Inventory2");
            True(p.Mechanisms["Inventory2"].StartsWith("system Inventory2"), "renamed source");
        });
        test("removing a system removes its objects and its unchanged source; your edits stay", () =>
        {
            var p = WithGround();
            string? Original(string n) => Systems.StarterSource(n);
            var s = p.Scenes[0];
            Systems.Add(p.Mechanisms, s, Systems.StarterSource("DarkCave")!, 0, 0);
            var shared = s.Add(new SceneObject { Name = "Both", Mechanisms = [new SceneMechanism { Name = "DarkCave" }, new SceneMechanism { Name = "Blink" }] });
            var r = Systems.Remove(p.Mechanisms, p.Scenes, s, "DarkCave", Original);
            Eq(2, r.ObjectsRemoved, "objects");
            Eq(true, r.SourceRemoved, "source removed");
            Eq(false, p.Mechanisms.ContainsKey("DarkCave"), "gone from the project");
            Eq(true, s.Objects.Contains(shared) && shared.Mechanisms.Count == 1, "an object with other mechanisms only loses the system");
            Systems.Add(p.Mechanisms, s, Systems.StarterSource("DarkCave")!, 0, 0);
            p.Mechanisms["DarkCave"] += "\n    when told \"boo\"\n        print 1\n";
            var r2 = Systems.Remove(p.Mechanisms, p.Scenes, s, "DarkCave", Original);
            Eq(false, r2.SourceRemoved, "an edited system stays in the project");
        });
        test("adding a system copies its mechanisms from My Library; removing takes the unused ones back out", () => InTemp(dir =>
        {
            var lib = new MechanismLibrary(Path.Combine(dir, "Library"));
            lib.Save("Spinner", "mechanism Spinner\n    param turns: number = 2\n\n    when start\n        print turns\n");
            lib.Save("SpinKit", "system SpinKit\n    includes Spinner turns many\n    param many: number = 7\n");
            Eq("SpinKit", string.Join(",", Systems.LibrarySystems(lib)), "library systems");
            var p = WithGround();
            var r = Systems.Add(p.Mechanisms, p.Scenes[0], lib.Read("SpinKit")!, 0, 0, lib.Read);
            Eq("Spinner", string.Join(",", r.Copied), "copied");
            var spin = Systems.PlacePart(p.Scenes[0], "Spinner", 100, 100);
            var w = Play(p);
            NoErrors(w);
            Eq(7.0, Mech(w.Objects.First(o => o.Name == spin.Name), "Spinner").Vars["turns"], "the system's setting reached the part");
            p.Scenes[0].Objects.Remove(spin);
            var gone = Systems.Remove(p.Mechanisms, p.Scenes, p.Scenes[0], "SpinKit", lib.Read);
            Eq(true, gone.SourceRemoved, "system source removed");
            Eq("Spinner", string.Join(",", gone.MechanismsRemoved), "unused library copy removed");
        }));
        test("system params reach its mechanisms: 'uses' settings and 'includes' defaults (the object's own value wins)", () =>
        {
            var p = WithGround();
            var s = p.Scenes[0];
            var early = Systems.PlacePart(s, "Platformer", 100, 300); // listed before the system object
            var r = Systems.Add(p.Mechanisms, s, Systems.StarterSource("PlatformerKit")!, 8, 8);
            r.Object.Mechanisms[0].Params["speed"] = 300.0;
            r.Object.Mechanisms[0].Params["gravity"] = 900.0;
            var own = Systems.PlacePart(s, "Platformer", 200, 300);
            own.Tags.Clear();
            own.Mechanisms[0].Params["speed"] = 120.0;
            var inv = Systems.Add(p.Mechanisms, s, Systems.StarterSource("Inventory")!, 40, 8);
            inv.Object.Mechanisms[0].Params["slots"] = 5.0;
            var w = Play(p);
            NoErrors(w);
            var e = w.Objects.First(o => o.Name == early.Name);
            Eq(300.0, Mech(e, "Platformer").Vars["speed"], "includes default reaches an object listed before the system");
            Eq(900.0, Mech(e, "Body").Vars["gravity"], "and a mechanism the part uses (Platformer uses Body)");
            Eq(120.0, Mech(w.Objects.First(o => o.Name == own.Name), "Platformer").Vars["speed"], "an object's own setting wins");
            Eq(5.0, Mech(w.Objects.First(o => o.Name == inv.Object.Name), "Hotbar").Vars["slots"], "a 'uses' setting");
            w.RunFor(0.2);
            Eq(5, w.Hotbar.Slots, "the hotbar shows 5 slots");
            Eq(300.0, w.SystemDefaults["Platformer"]["speed"], "registered defaults");
        });

        foreach (var (name, src) in Systems.Starter())
            test($"starter system {name} plays headless with its parts placed", () =>
            {
                var p = WithGround();
                var s = p.Scenes[0];
                var r = Systems.Add(p.Mechanisms, s, src, 8, 8);
                double x = 60;
                foreach (var part in Systems.Parts(Systems.Parse(src)!))
                {
                    if (part is "Solid" or "Body") continue;
                    Systems.PlacePart(s, part, x, part == "Platformer" ? 340 : 300);
                    x += 70;
                }
                if (!s.Objects.Any(o => o.Tags.Contains("player")))
                    s.Add(new SceneObject { Name = "Player", X = 400, Y = 340, Width = 32, Height = 48, Tags = ["player"], Mechanisms = [new SceneMechanism { Name = "PlayerMove" }] });
                var w = Play(p);
                w.KeysDown.Add("right");
                w.RunFor(3);
                NoErrors(w);
                True(w.Objects.Any(o => o.Name == r.Object.Name), "the system's object is there");
                switch (name)
                {
                    case "Inventory": True(w.Objects.Any(o => o.Mechanisms.Any(m => m.Def.Name == "Crafting")), "made the crafting menu"); break;
                    case "CardBattle":
                        Eq(5, w.Objects.Count(o => o.Tags.Contains("in_hand")), "dealt a hand of 5");
                        Eq(3, w.Objects.Count(o => o.Tags.Contains("slot")), "made 3 slots");
                        Eq("player", Mech(w.Objects.First(o => o.Name == r.Object.Name), "TurnManager").Vars["turn"], "the player's turn");
                        break;
                    case "DarkCave": True(w.Darkness > 0.5, "the scene is dark"); break;
                    case "DayNightCycle": Eq("Day", w.Globals.GetValueOrDefault("daytime"), "it's day"); break;
                    case "PlatformerKit": True(w.FindObject("player")!.X > 60, "the player ran right"); break;
                }
            });

        test("New system… from mechanisms: parses, includes each one, saves to My Library with its own mechanisms", () => InTemp(dir =>
        {
            var p = WithGround();
            p.Mechanisms["Spinner"] = "mechanism Spinner\n    when start\n        print 1\n";
            Eq(null, Systems.CheckName("MyKit", p.Mechanisms.ContainsKey), "free name");
            True(Systems.CheckName("Coin", p.Mechanisms.ContainsKey) != null, "a starter mechanism's name is taken");
            True(Systems.CheckName("my kit", p.Mechanisms.ContainsKey) != null, "no spaces");
            string src = Systems.NewSource("MyKit", ["Spinner", "Coin", "Spinner"]);
            var d = Systems.Parse(src)!;
            Eq("Spinner,Coin", string.Join(",", Systems.Members(d)), "members");
            p.Mechanisms["MyKit"] = src;
            var lib = new MechanismLibrary(Path.Combine(dir, "Library"));
            var saved = Systems.SaveToLibrary(lib, "MyKit", p.Mechanisms);
            Eq("MyKit,Spinner", string.Join(",", saved), "the system and its non-starter mechanism");
            Eq(true, Systems.IsSystemSource(lib.Read("MyKit")), "saved as a system");
        }));
        test("a sample's mechanisms become a system: Deepdelve's hotbar and spawner run from one 'DeepdelveKit'", () => InTemp(dir =>
        {
            SampleGames.Make(DeepdelveSample.Name, dir);
            var p = Project.Load(dir);
            var s = p.Scenes[0];
            // The kit: the managers run on the system's object, the scene's other mechanisms come with it.
            var used = s.Objects.SelectMany(o => o.Mechanisms).Select(m => m.Name).Distinct().Where(n => n is not ("Hotbar" or "CreatureSpawner")).ToList();
            string kit = Systems.NewSource("DeepdelveKit", used).Replace("\n    when start",
                "    param slots: number = 10\n    param spawn_every: seconds = 6\n    uses Hotbar slots slots\n    uses CreatureSpawner every spawn_every\n\n    when start");
            var d = Systems.Parse(kit)!;
            True(Systems.Members(d).Contains("CreatureSpawner") && Systems.Members(d).Contains("Miner"), "members");
            s.Objects.RemoveAll(o => o.Name is "Hotbar" or "Spawner");
            var r = Systems.Add(p.Mechanisms, s, kit, 8, 8);
            r.Object.Mechanisms[0].Params["slots"] = 8.0;
            p.Save(dir);
            var q = Project.Load(dir);
            True(q.Mechanisms.ContainsKey("DeepdelveKit") && Systems.IsSystemSource(q.Mechanisms["DeepdelveKit"]), "saved and loaded as a system");
            var w = Play(q, dir: dir);
            w.Broadcast("begin", []);
            w.RunFor(2);
            NoErrors(w);
            var kitObj = w.FindObject("DeepdelveKit")!;
            Eq(8.0, Mech(kitObj, "Hotbar").Vars["slots"], "the kit's slots reach its hotbar (the game's own flow then shows 10)");
            Eq(true, Mech(kitObj, "CreatureSpawner").Vars["on"], "the spawner runs on the kit's object and heard 'begin'");
        }));

        Console.WriteLine("Export game (GameExport.cs)");
        test("export folder: player renamed after the game, project copied without saves, zip", () => InTemp(dir =>
        {
            string proj = Path.Combine(dir, "proj");
            var p = Project.CreateDemo("Moon Run");
            p.Save(proj);
            Sounds.WriteDefaults(proj);
            Directory.CreateDirectory(Path.Combine(proj, "images"));
            File.WriteAllBytes(Path.Combine(proj, "images", "hero.png"), [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(proj, "saves"));
            File.WriteAllText(Path.Combine(proj, "saves", "game.json"), "{}");
            File.WriteAllText(Path.Combine(proj, "project.json.tmp"), "x");
            string player = Path.Combine(dir, "player");
            Directory.CreateDirectory(Path.Combine(player, "runtimes"));
            File.WriteAllText(Path.Combine(player, GameExport.PlayerExe), "exe");
            File.WriteAllText(Path.Combine(player, "BuilderEngine.Player.dll"), "dll");
            File.WriteAllText(Path.Combine(player, "runtimes", "x.dll"), "x");
            Eq(player, GameExport.FindPrebuiltPlayer(dir), "finds player/ next to the editor");

            var steps = new List<string>();
            var r = GameExport.Export(new GameExport.Options(proj, Path.Combine(dir, "out"), player, Zip: true, IconImage: "hero.png"), steps.Add);
            string game = Path.Combine(dir, "out", "Moon Run");
            Eq(game, r.Folder);
            Eq(Path.Combine(game, "Moon Run.exe"), r.Exe, "exe named after the game");
            True(File.Exists(r.Exe!) && !File.Exists(Path.Combine(game, GameExport.PlayerExe)), "renamed, not copied twice");
            True(File.Exists(Path.Combine(game, "BuilderEngine.Player.dll")) && File.Exists(Path.Combine(game, "runtimes", "x.dll")), "the player's files");
            True(Project.Exists(Path.Combine(game, "game")), "project.json");
            True(File.Exists(Path.Combine(game, "game", "mechanisms", "Intro.bs")), "mechanisms");
            True(File.Exists(Path.Combine(game, "game", "images", "hero.png")) && File.Exists(Path.Combine(game, "game", "sounds", "coin.wav")), "assets");
            True(File.Exists(Path.Combine(game, "game", GameExport.IconFile)), "the icon picture");
            True(!Directory.Exists(Path.Combine(game, "game", "saves")), "no saves");
            True(!File.Exists(Path.Combine(game, "game", "project.json.tmp")), "no half-written files");
            True(File.Exists(r.Zip!) && r.Zip!.EndsWith("Moon Run.zip"), "zip");
            using (var z = System.IO.Compression.ZipFile.OpenRead(r.Zip!))
                True(z.Entries.Any(e => e.FullName.Replace('\\', '/') == "Moon Run/game/project.json"), "zip has the game folder");
            True(steps.Count >= 3 && steps[^1] == "Done.", "progress steps");
            Eq(0, r.Warnings.Count, "no warnings");

            // Again: replaces the earlier export. Without a player: a warning, no exe.
            var r2 = GameExport.Export(new GameExport.Options(proj, Path.Combine(dir, "out")));
            Eq(null, r2.Exe, "no player, no exe");
            True(r2.Warnings.Count == 1 && !File.Exists(Path.Combine(game, "Moon Run.exe")), "warned; the old export was replaced");
            // A folder that isn't an export is never deleted.
            Directory.CreateDirectory(Path.Combine(dir, "out2", "Moon Run"));
            File.WriteAllText(Path.Combine(dir, "out2", "Moon Run", "mine.txt"), "keep");
            try { GameExport.Export(new GameExport.Options(proj, Path.Combine(dir, "out2"))); throw new Exception("no error"); }
            catch (IOException e) { True(e.Message.Contains("isn't an exported game"), e.Message); }
            True(File.Exists(Path.Combine(dir, "out2", "Moon Run", "mine.txt")), "kept");
        }));
        test("the player loads an exported project headless (with a system) and plays it", () => InTemp(dir =>
        {
            string proj = Path.Combine(dir, "proj");
            var p = Project.CreateDemo("Cave Run");
            Systems.Add(p.Mechanisms, p.Scenes[0], Systems.StarterSource("DarkCave")!, 8, 8);
            p.Save(proj);
            var r = GameExport.Export(new GameExport.Options(proj, Path.Combine(dir, "out")));
            string? game = GameBoot.FindGame(r.Folder);
            True(game != null, "finds game/ next to the player");
            var q = Project.Load(game!);
            Eq("Cave Run", q.Name);
            var w = GameBoot.NewWorld(q, game!, Path.Combine(dir, "saves"));
            w.LoadScene(q.Scenes[0]);
            w.KeysDown.Add("right");
            w.RunFor(2);
            NoErrors(w);
            True(w.Darkness > 0.5, "the system runs in the exported game");
            Eq(game, w.ProjectFolder, "pictures and sounds come from the game folder");
            True(GameBoot.SaveFolderFor("Cave: Run").EndsWith(Path.Combine("BuilderEngine Games", "Cave_ Run", "saves")), "saves in LocalAppData");
            Eq(null, GameBoot.FindGame(dir), "no game/ here");
        }));
    }
}
