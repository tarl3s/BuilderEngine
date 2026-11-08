using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using BuilderEngine.Plugin;
using BuilderEngine.Script;

/// <summary>
/// The data audit (docs/audit/data-mechanisms-plugins.md): project files, mechanisms, My Library, settings, plugins,
/// export and big projects. Each test covers one finding; measurements are printed like the other suites do.
/// </summary>
static class AuditDataTests
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
        string dir = Path.Combine(Path.GetTempPath(), "BE_Audit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    const string Coin = "mechanism Coin\n    param value: number = 1\n    when touched by \"player\"\n        score += value\n        destroy self\n";

    /// <summary>What the editor does with a project's mechanisms: adopt them (one per name), then save them back.</summary>
    static void AdoptAndSave(Project p, string dir)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, src) in p.SplitMechanismFiles()) sources[name] = src;
        p.Mechanisms.Clear();
        foreach (var (name, src) in sources) p.Mechanisms[name] = src;
        p.Save(dir);
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Audit: project files and mechanisms");

        test("a broken project.json never deletes the project's mechanisms (recovery keeps them, the broken file is kept)", () => InTemp(dir =>
        {
            var p = Project.CreateEmpty("Game");
            p.Mechanisms["Coin"] = Coin;
            p.Mechanisms["Door"] = "mechanism Door\n    param scene: Scene = \"Level 2\"\n";
            p.Save(dir);
            File.WriteAllText(Path.Combine(dir, Project.FileName), "{ \"name\": \"Game\", \"scenes\": [ { \"objects\": [ {"); // cut short
            bool threw = false;
            try { Project.Load(dir); } catch (JsonException) { threw = true; }
            True(threw, "a project.json that isn't JSON throws");
            var r = Project.RecoverBroken(dir, "Game", out string kept);
            True(File.Exists(Path.Combine(dir, kept)) && kept.StartsWith("project.broken-"), "the broken file is kept: " + kept);
            Eq(false, File.Exists(Path.Combine(dir, Project.FileName)), "project.json moved away");
            Eq("Coin,Door", string.Join(",", r.Mechanisms.Keys.Order()), "mechanisms read back");
            AdoptAndSave(r, dir); // what the editor does next
            Eq(true, File.Exists(Path.Combine(dir, Project.MechanismsFolder, "Coin.bs")), "Coin.bs is still on disk");
            Eq(Coin, File.ReadAllText(Path.Combine(dir, Project.MechanismsFolder, "Coin.bs")));
            Eq(2, Project.Load(dir).Mechanisms.Count);
        }));

        test("opening: a broken project.json is recovered; files that can't be read (locked, offline) are reported and never saved over", () => InTemp(dir =>
        {
            var p = Project.CreateEmpty("Game");
            p.Mechanisms["Coin"] = Coin;
            p.Save(dir);
            var ok = Project.Open(dir, "Game", out var r, out string why);
            Eq(Project.OpenResult.Loaded, r); Eq("", why); Eq(1, ok.Mechanisms.Count);

            string json = File.ReadAllText(Path.Combine(dir, Project.FileName));
            using (new FileStream(Path.Combine(dir, Project.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var locked = Project.Open(dir, "Game", out r, out why);
                Eq(Project.OpenResult.Unreadable, r);
                True(why.Contains("Nothing will be saved over them"), why);
                Eq(0, locked.Mechanisms.Count);
            }
            Eq(json, File.ReadAllText(Path.Combine(dir, Project.FileName)), "untouched");
            Eq(true, File.Exists(Path.Combine(dir, Project.MechanismsFolder, "Coin.bs")));

            File.WriteAllText(Path.Combine(dir, Project.FileName), "");
            var rec = Project.Open(dir, "Game", out r, out why);
            Eq(Project.OpenResult.Recovered, r);
            True(why.Contains("project.broken-"), why);
            Eq("Coin", string.Join(",", rec.Mechanisms.Keys));
        }));

        test("mechanism files survive the editor's adopt → save: comments on top, two in one file, wrong file name, no 'mechanism' line, same name twice", () => InTemp(dir =>
        {
            Project.CreateEmpty("Game").Save(dir);
            string mech = Path.Combine(dir, Project.MechanismsFolder);
            Directory.CreateDirectory(mech);
            File.WriteAllText(Path.Combine(mech, "Coin.bs"), "# Picked up by the player.\n# Made by me.\n" + Coin);
            File.WriteAllText(Path.Combine(mech, "Pair.bs"), "# two at once\nmechanism A\n    when start\n        x = 1\n\nmechanism B\n    when start\n        y = 2\n");
            File.WriteAllText(Path.Combine(mech, "Wrong.bs"), "mechanism Right\n    when start\n        z = 3\n");
            File.WriteAllText(Path.Combine(mech, "Draft.bs"), "  mechanism Draft   # half-typed: indented first line\n    when start\n        w = 4\n");
            File.WriteAllText(Path.Combine(mech, "Copy.bs"), "mechanism Coin\n    param value: number = 99\n");
            File.WriteAllText(Path.Combine(mech, "Empty.bs"), "");
            var p = Project.Load(dir);
            var split = p.SplitMechanismFiles().ToDictionary(c => c.Name, c => c.Source, StringComparer.OrdinalIgnoreCase);
            Eq("A,B,Coin,Copy,Draft,Empty,Right", string.Join(",", split.Keys.Order(StringComparer.OrdinalIgnoreCase)));
            True(split["Coin"].StartsWith("# Picked up by the player.\n# Made by me.\nmechanism Coin"), "comments above stay: " + split["Coin"]);
            True(split["A"].StartsWith("# two at once\nmechanism A") && !split["A"].Contains("mechanism B"), "split, comment kept on the first");
            True(split["Copy"].Contains("= 99"), "a second 'Coin' in another file is kept under its file name, not lost");
            True(split["Draft"].Contains("w = 4"), "a file without a 'mechanism' line at the start is kept whole");
            AdoptAndSave(p, dir);
            var files = Directory.GetFiles(mech, "*.bs").Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase);
            Eq("A.bs,B.bs,Coin.bs,Copy.bs,Draft.bs,Empty.bs,Right.bs", string.Join(",", files));
            True(File.ReadAllText(Path.Combine(mech, "Coin.bs")).StartsWith("# Picked up"), "comments still on disk");
            True(File.ReadAllText(Path.Combine(mech, "Draft.bs")).Contains("w = 4"), "the draft is still on disk");
            // And again: saving twice changes nothing.
            var before = Directory.GetFiles(mech).ToDictionary(f => Path.GetFileName(f), File.ReadAllText);
            AdoptAndSave(Project.Load(dir), dir);
            foreach (var (f, text) in before) Eq(text, File.ReadAllText(Path.Combine(mech, f)), f);
        }));

        test("renaming a mechanism also renames it in the prefabs (spawning one still works)", () =>
        {
            var p = Project.CreateEmpty();
            var o = p.Scenes[0].Add(new SceneObject { Name = "Slime", Mechanisms = [new SceneMechanism { Name = "Patrol", Params = { ["speed"] = 80.0 } }] });
            PrefabTools.SaveAsPrefab(p, o, "Slime");
            Eq(2, p.RenameMechanism("Patrol", "Walker"), "the object and the prefab's template");
            Eq("Walker", p.Scenes[0].Objects[0].Mechanisms[0].Name);
            Eq("Walker", p.Prefabs[0].Template.Mechanisms[0].Name);
            Eq(80.0, p.Prefabs[0].Template.Mechanisms[0].Params["speed"], "params kept");
            Eq(0, PrefabTools.Overrides(p, p.Scenes[0].Objects[0]).Count, "the copy is still like its prefab");
        });

        test("renaming a mechanism follows it in other mechanisms: uses, includes, from (mechanism and system), its signals", () =>
        {
            string src = "mechanism Slime\n    uses Health max 2\n    uses HealthBar\n    when Health.died\n        destroy self\n    when touched by \"Health\"\n        say self \"Health\"\n";
            string r = MechanismLibrary.RenameReferences(src, "Health", "Life");
            True(r.Contains("    uses Life max 2\n") && r.Contains("    uses HealthBar\n"), "uses renamed, a longer name untouched: " + r);
            True(r.Contains("    when Life.died\n"), "its signal");
            True(r.Contains("touched by \"Health\"") && r.Contains("say self \"Health\""), "texts are not names");
            Eq("system Kit from Life\n    includes Life\n", MechanismLibrary.RenameReferences("system Kit from Health\n    includes Health\n", "Health", "Life"));
            Eq("mechanism Big from Life\n", MechanismLibrary.RenameReferences("mechanism Big from Health\n", "Health", "Life"));
            Eq("mechanism Health\n", MechanismLibrary.RenameReferences("mechanism Health\n", "Health", "Life"), "its own line is RenameSource's job");
        });

        test("removing a system keeps the mechanisms a prefab or another mechanism still needs", () => InTemp(dir =>
        {
            var lib = new MechanismLibrary(Path.Combine(dir, "Library"));
            lib.Save("Spinner", "mechanism Spinner\n    param turns: number = 2\n");
            lib.Save("Glow", "mechanism Glow\n    when start\n        x = 1\n");
            lib.Save("SpinKit", "system SpinKit\n    includes Spinner\n    includes Glow\n");
            var p = Project.CreateEmpty();
            var r = Systems.Add(p.Mechanisms, p.Scenes[0], lib.Read("SpinKit")!, 0, 0, lib.Read);
            Eq("Spinner,Glow", string.Join(",", r.Copied));
            var part = Systems.PlacePart(p.Scenes[0], "Spinner", 50, 50);
            PrefabTools.SaveAsPrefab(p, part, "Top");            // a prefab carries Spinner…
            p.Scenes[0].Objects.Remove(part);                    // …and no scene object does
            p.Mechanisms["Lamp"] = "mechanism Lamp\n    uses Glow\n"; // the person's own mechanism uses Glow
            var gone = Systems.Remove(p.Mechanisms, p.Scenes, p.Scenes[0], "SpinKit", lib.Read, p.Prefabs.Select(pf => pf.Template));
            Eq(true, gone.SourceRemoved);
            Eq("", string.Join(",", gone.MechanismsRemoved), "Spinner (prefab) and Glow (used by Lamp) stay");
            Eq(true, p.Mechanisms.ContainsKey("Spinner") && p.Mechanisms.ContainsKey("Glow"));
        }));

        test("old/odd files load: a removed or renamed param is ignored, a changed type is converted, a missing mechanism is a friendly error", () =>
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load("mechanism Spinner\n    param speed: text = \"fast\"\n    param on: bool = false\n");
            var s = new Scene();
            s.Add(new SceneObject
            {
                Name = "A",
                Mechanisms = [new SceneMechanism { Name = "Spinner", Params = { ["speed"] = 12.0, ["gone"] = "x", ["ON"] = "yes" } }, new SceneMechanism { Name = "Nope" }]
            });
            w.LoadScene(s);
            w.Step(1.0 / 60);
            var m = w.Objects[0].Mechanisms[0];
            Eq("12", m.Vars["speed"], "a number saved for a param that is text now");
            Eq(true, m.Vars["on"], "names match whatever the case; 'yes' is true");
            Eq(false, m.Vars.ContainsKey("gone"), "removed params are ignored");
            True(w.Errors.Count == 1 && w.Errors[0].Contains("Nope"), string.Join(" | ", w.Errors));
        });

        test("cycles are friendly errors, never a hang: uses A↔B, variants A↔B, systems that include each other", () =>
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load("mechanism UA\n    uses UB\nmechanism UB\n    uses UA\n");
            bool loop = false;
            try { w.Spawn("x", 0, 0, ["UA"]); } catch (ScriptError e) { loop = e.Message.Contains("loop"); }
            True(loop, "uses A↔B");
            w.Load("mechanism VA from VB\nmechanism VB from VA\n");
            loop = false;
            try { w.Spawn("y", 0, 0, ["VA"]); } catch (ScriptError e) { loop = e.Message.Contains("loop"); }
            True(loop, "from A↔B");
            w.Load("system SA\n    includes SB\n    when start\n        visible = false\nsystem SB\n    includes SA\n    when start\n        visible = false\n");
            var s = new Scene();
            s.Add(new SceneObject { Name = "SA", Mechanisms = [new SceneMechanism { Name = "SA" }] });
            s.Add(new SceneObject { Name = "SB", Mechanisms = [new SceneMechanism { Name = "SB" }] });
            var sw = Stopwatch.StartNew();
            w.LoadScene(s);
            w.Step(1.0 / 60);
            True(sw.ElapsedMilliseconds < 2000, "no hang");
            var members = Systems.Members(Systems.Parse("system SA\n    includes SB\n")!);
            Eq("SB", string.Join(",", members));
        });

        test("names with spaces, accents, emoji and Windows device words round-trip (project folder, mechanisms, objects)", () => InTemp(root =>
        {
            string dir = Path.Combine(root, "Meu Jogo ção 🎮");
            var p = Project.CreateEmpty("Meu Jogo \"ção\" 🎮");
            p.Scenes[0].Name = "Fase 1 — início";
            p.Scenes[0].Add(new SceneObject { Name = "Moeda “ouro” ✨", Tags = ["moeda", "ção"], Mechanisms = [new SceneMechanism { Name = "Moeda_ção", Params = { ["texto"] = "olá\n\"mundo\"\\" } }] });
            p.Mechanisms["Moeda_ção"] = "mechanism Moeda_ção\n    param texto: text = \"\"\n";
            p.Mechanisms["CON"] = "mechanism CON\n";
            p.Mechanisms["aux"] = "mechanism aux\n";
            p.Save(dir);
            var back = Project.Load(dir);
            Eq(p.ToJson(), back.ToJson());
            Eq("CON,Moeda_ção,aux", string.Join(",", back.Mechanisms.Keys.Order(StringComparer.Ordinal)));
            Eq("olá\n\"mundo\"\\", back.Scenes[0].Objects[0].Mechanisms[0].Params["texto"]);
        }));

        test("half-written files from an interrupted save are never read as mechanisms", () => InTemp(dir =>
        {
            var p = Project.CreateEmpty();
            p.Mechanisms["Coin"] = Coin;
            p.Save(dir);
            string mech = Path.Combine(dir, Project.MechanismsFolder);
            File.WriteAllText(Path.Combine(mech, "Coin.bs.tmp"), "mechanism Co"); // the crash left this
            File.WriteAllText(Path.Combine(dir, Project.FileName + ".tmp"), "{ \"na");
            var back = Project.Load(dir);
            Eq("Coin", string.Join(",", back.Mechanisms.Keys));
            Eq(Coin, back.Mechanisms["Coin"]);
            back.Save(dir);
            Eq(Coin, File.ReadAllText(Path.Combine(mech, "Coin.bs")));
        }));

        Console.WriteLine("Audit: settings and My Library");

        test("a broken settings.json is kept as settings.broken-….json before it is replaced (API keys, recent list)", () => InTemp(dir =>
        {
            var s = new AppSettings(dir);
            File.WriteAllText(s.FilePath, "{ \"aiKey.groq\": \"secret\", \"recentProjects\": [\"C:\\\\a\" "); // cut short
            Eq(null, s.Get("aiKey.groq"), "a broken file reads as empty");
            Eq(true, s.Set("theme", "dark"));
            var kept = Directory.GetFiles(dir, "settings.broken-*.json");
            Eq(1, kept.Length, "the broken file was kept");
            True(File.ReadAllText(kept[0]).Contains("secret"), "with what it had");
            Eq("dark", new AppSettings(dir).Get("theme"));
            Eq(true, s.Set("other", "x"));
            Eq(1, Directory.GetFiles(dir, "settings.broken-*.json").Length, "kept once, not on every write");
        }));

        test("a settings.json that can't be read (locked) is never overwritten with an empty one", () => InTemp(dir =>
        {
            var s = new AppSettings(dir);
            Eq(true, s.Set("aiKey.groq", "secret"));
            using (new FileStream(s.FilePath, FileMode.Open, FileAccess.Read, FileShare.Delete)) // readers locked out, replacing allowed
            {
                Eq(false, s.Set("theme", "dark"), "refused while the file can't be read");
            }
            Eq("secret", s.Get("aiKey.groq"), "the key is still there");
        }));

        Console.WriteLine("Audit: plugins");

        test("two plugins adding the same command: the first keeps it, the second is reported (no silent replacement)", () =>
        {
            using var pm = new PluginManager();
            pm.Add(new SamePlugin("First", 1));
            var second = pm.Add(new SamePlugin("Second", 2));
            Eq("", string.Join(",", second.Commands.Select(c => c.Name)), "the second plugin's clashing command and function are left out");
            True(pm.Errors.Count == 2 && pm.Errors.All(e => e.Contains("First") && e.Contains("Second")), string.Join(" | ", pm.Errors));
            var old = PluginManager.Active;
            PluginManager.Active = pm;
            try
            {
                var w = new World();
                w.Load("mechanism M\n    when start\n        mark\n        v = which()\n");
                w.Spawn("A", 0, 0, ["M"]);
                w.Step(1.0 / 60);
                Eq(1.0, w.Globals["mark"], "the first plugin's command runs");
                Eq(1.0, w.Objects[0].Mechanisms[0].Vars["v"], "and its function");
            }
            finally { PluginManager.Active = old; }
        });

        test("a plugin whose Name throws is told in the console (not hidden) and listed by its class name", () =>
        {
            using var pm = new PluginManager();
            var said = new List<string>();
            pm.Logged += said.Add;
            var lp = pm.Add(new NamelessPlugin());
            Eq("NamelessPlugin", lp.Name);
            True(said.Any(s => s.Contains("Name or Description failed") && s.Contains("no name today")), string.Join(" | ", said));
            Eq(1, lp.Commands.Count, "it still loads");
        });

        test("plugins unload: the collectible load context is collected once the plugin manager is disposed", () => InTemp(dir =>
        {
            string folder = PluginManager.FolderOf(dir);
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "HelloPlugin.dll"), Path.Combine(folder, "HelloPlugin.dll"));
            var weak = LoadPlayAndUnload(folder);
            int rounds = 0;
            for (; weak.IsAlive && rounds < 20; rounds++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            True(!weak.IsAlive, "the plugins' load context is still alive after 20 collections");
            Console.WriteLine($"         (plugin context unloaded after {rounds} collection(s))");
        }));

        Console.WriteLine("Audit: start-up");

        test("the [ModuleInitializer] installers don't depend on their order: no command or function is set by two of them", () =>
        {
            var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var clashes = new List<string>();
            for (int i = 0; i < World.Installers.Count; i++)
            {
                var w = new World();
                w.Commands.Clear();
                w.Functions.Clear();
                World.Installers[i](w);
                foreach (var k in w.Commands.Keys.Select(k => "command " + k).Concat(w.Functions.Keys.Select(k => "function " + k)))
                    if (owner.TryGetValue(k, out int j)) clashes.Add($"{k} (installers {j} and {i})"); else owner[k] = i;
            }
            Eq("", string.Join(", ", clashes));
            True(World.Installers.Count >= 20 && owner.Count > 50, $"{World.Installers.Count} installers, {owner.Count} names");
        });

        Console.WriteLine("Audit: export");

        test("exporting into a folder inside the project never copies the export (or an earlier one) into the game", () => InTemp(dir =>
        {
            string proj = Path.Combine(dir, "Game");
            var p = Project.CreateEmpty("Game");
            p.Mechanisms["Coin"] = Coin;
            p.Save(proj);
            string player = Path.Combine(dir, "player");
            Directory.CreateDirectory(player);
            File.WriteAllText(Path.Combine(player, GameExport.PlayerExe), "exe");
            string outDir = Path.Combine(proj, "Exports");
            GameExport.Export(new GameExport.Options(proj, outDir, player));
            var r = GameExport.Export(new GameExport.Options(proj, outDir, player)); // again: the first export is inside the project now
            var inside = Directory.GetFiles(Path.Combine(r.Folder, GameExport.GameFolder), "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Path.Combine(r.Folder, GameExport.GameFolder), f)).Order().ToList();
            Eq(false, inside.Any(f => f.StartsWith("Exports")), "game/ has: " + string.Join(", ", inside));
            Eq(true, inside.Contains(Path.Combine(Project.MechanismsFolder, "Coin.bs")));
        }));

        Console.WriteLine("Audit: big projects");

        test("a big project (200 mechanisms in chains, 50 scenes × 500 objects): save, load, play a scene, undo copy — correct and measured", () => InTemp(dir =>
        {
            var p = BigProject();
            var sw = Stopwatch.StartNew();
            p.Save(dir);
            long save = sw.ElapsedMilliseconds;
            long size = new FileInfo(Path.Combine(dir, Project.FileName)).Length;
            GC.Collect();
            long mem0 = GC.GetTotalMemory(true);
            sw.Restart();
            var back = Project.Load(dir);
            long load = sw.ElapsedMilliseconds;
            long mem = GC.GetTotalMemory(true) - mem0;
            Eq(50, back.Scenes.Count);
            Eq(25000, back.Scenes.Sum(s => s.Objects.Count));
            Eq(200, back.Mechanisms.Count);
            sw.Restart();
            Eq(p.ToJson(), back.ToJson(), "round trip");
            long compare = sw.ElapsedMilliseconds;
            sw.Restart();
            var w = GameBoot.NewWorld(back, dir, Path.Combine(dir, "saves"));
            long world = sw.ElapsedMilliseconds;
            Eq("", string.Join(" | ", w.Errors));
            sw.Restart();
            w.LoadScene(back.Scenes[0]);
            w.Step(1.0 / 60);
            long scene = sw.ElapsedMilliseconds;
            Eq("", string.Join(" | ", w.Errors.Take(3)));
            Eq(500, w.Objects.Count);
            var deep = w.Objects.First(o => o.Name == "Thing 9");
            Eq(9.0, deep.Mechanisms[0].Vars["speed"], "a variant 9 levels deep has its own default");
            Eq(9.0, deep.Mechanisms[0].Vars["m"], "its own when ran");
            Eq(9.0, deep.Mechanisms[0].Vars["n"], "and the base's when, with the variant's value");
            sw.Restart();
            for (int i = 0; i < 10; i++) back.Scenes[0].Clone();
            long clone = sw.ElapsedMilliseconds / 10;
            Console.WriteLine($"         (save {save} ms, project.json {size / 1024} KB, load {load} ms, ~{mem / (1024 * 1024)} MB in memory, compare {compare} ms;");
            Console.WriteLine($"          world with 200 mechanisms {world} ms, first scene (500 objects) {scene} ms, undo copy of a scene {clone} ms)");
            True(save < 5000 && load < 5000, "save and load stay under 5 s");
        }));
    }

    /// <summary>20 families of 10 mechanisms: a base, then variants each from the one before (9 levels deep), all using Health.</summary>
    static Project BigProject()
    {
        var p = new Project { Name = "Big" };
        for (int g = 0; g < 20; g++)
        {
            p.Mechanisms[$"B{g}"] = $"mechanism B{g}\n    param speed: number = 0\n    uses Health max 3\n    when start\n        n = speed\n";
            for (int k = 1; k < 10; k++)
                p.Mechanisms[$"B{g}_{k}"] = $"mechanism B{g}_{k} from {(k == 1 ? $"B{g}" : $"B{g}_{k - 1}")}\n    param speed = {k}\n    when start\n        m = {k}\n";
        }
        var rng = new Random(7);
        for (int s = 0; s < 50; s++)
        {
            var scene = new Scene { Id = s + 1, Name = $"Level {s + 1}", Width = 4000, Height = 2000 };
            for (int i = 0; i < 500; i++)
            {
                int g = i % 20, k = i % 10;
                string mech = k == 0 ? $"B{g}" : $"B{g}_{k}";
                scene.Objects.Add(new SceneObject
                {
                    Id = i + 1, Name = $"Thing {i}", X = rng.Next(4000), Y = rng.Next(2000), Width = 32, Height = 32,
                    Color = "#D97757", Image = i % 3 == 0 ? "hero.png" : "", Tags = i % 5 == 0 ? ["enemy", "big"] : [],
                    Mechanisms = [new SceneMechanism { Name = mech, Params = i % 4 == 0 ? new(StringComparer.OrdinalIgnoreCase) { ["label"] = $"n{i}" } : new(StringComparer.OrdinalIgnoreCase) }],
                });
            }
            p.Scenes.Add(scene);
        }
        return p;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference LoadPlayAndUnload(string folder)
    {
        var pm = new PluginManager(folder);
        Eq(1, pm.Load());
        var old = PluginManager.Active;
        PluginManager.Active = pm;
        try
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load("mechanism M\n    when start\n        v = wave(0.25)\n        confetti 3\n");
            w.Spawn("A", 100, 100, ["M"]);
            w.Step(1.0 / 60);
            w.Step(1.0 / 60);
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
        }
        finally { PluginManager.Active = old; }
        var weak = new WeakReference(AssemblyLoadContext.GetLoadContext(pm.Plugins[0].Instance.GetType().Assembly));
        True(weak.Target is AssemblyLoadContext { IsCollectible: true }, "plugins load in a collectible context");
        pm.Dispose();
        return weak;
    }

    sealed class NamelessPlugin : IBuilderPlugin
    {
        public string Name => throw new InvalidOperationException("no name today");
        public void Register(IPluginHost host) => host.AddFunction("nameless_one", _ => 1.0);
    }

    sealed class SamePlugin(string name, double value) : IBuilderPlugin
    {
        public string Name => name;

        public void Register(IPluginHost host)
        {
            host.AddCommand("mark", c => c.Globals["mark"] = value);
            host.AddFunction("which", _ => value);
        }
    }
}
