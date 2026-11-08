using BuilderEngine.Script;

/// <summary>Scenes, sounds, My Library and settings (headless). Called from Program.cs with its Test runner.</summary>
static class ProjectTests
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

    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "BE_Test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static void InTemp(Action<string> body)
    {
        string dir = TempDir();
        try { body(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static World NewWorld()
    {
        var w = new World();
        StarterKit.LoadInto(w);
        return w;
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Scenes");
        test("multi-scene project: save -> load keeps every scene, id and order", () => InTemp(dir =>
        {
            var p = Project.CreateDemo("Two Levels");
            Eq(2, p.Scenes.Count, "demo scenes");
            var s3 = p.AddScene("Boss", p.Scenes[0]);
            s3.Add(new SceneObject { Name = "Boss", X = 300 });
            p.Save(dir);
            var q = Project.Load(dir);
            Eq(3, q.Scenes.Count);
            Eq("Level 1", q.Scenes[0].Name); Eq("Level 2", q.Scenes[1].Name); Eq("Boss", q.Scenes[2].Name);
            Eq(p.Scenes[2].Id, q.Scenes[2].Id, "scene id");
            Eq(3, q.Scenes.Select(s => s.Id).Distinct().Count(), "unique scene ids");
            Eq(300.0, q.Scenes[2].Objects[0].X);
            Eq("#0F1A24", q.Scenes[1].Background);
        }));
        test("old single-scene files load (scenes without ids, a 'scene' object, objects at the top)", () =>
        {
            var a = Project.FromJson("""{ "name": "Old", "scenes": [ { "name": "Level 1", "objects": [ { "name": "P", "x": 5 } ] } ] }""");
            Eq(1, a.Scenes.Count); Eq(1, a.Scenes[0].Id); Eq(5.0, a.Scenes[0].Objects[0].X);
            var b = Project.FromJson("""{ "name": "Older", "scene": { "name": "Only", "objects": [ { "name": "Q" } ] } }""");
            Eq(1, b.Scenes.Count); Eq("Only", b.Scenes[0].Name); Eq("Q", b.Scenes[0].Objects[0].Name);
            var c = Project.FromJson("""{ "name": "Flat", "width": 640, "objects": [ { "name": "R" }, { "name": "S" } ] }""");
            Eq(1, c.Scenes.Count); Eq("Level 1", c.Scenes[0].Name); Eq(640.0, c.Scenes[0].Width); Eq(2, c.Scenes[0].Objects.Count);
            var d = Project.FromJson("""{ "scenes": [ { "id": 4, "name": "A" }, { "id": 4, "name": "a" }, { "name": " " } ] }""");
            Eq(3, d.Scenes.Select(s => s.Id).Distinct().Count(), "duplicate ids fixed");
            Eq(3, d.Scenes.Select(s => s.Name.ToLowerInvariant()).Distinct().Count(), "duplicate names fixed");
        });
        test("duplicate scene: deep copy with a new id, right after the original", () =>
        {
            var p = Project.CreateDemo();
            var copy = p.DuplicateScene(0);
            Eq(3, p.Scenes.Count);
            Eq(copy, p.Scenes[1]);
            Eq("Level 1 copy", copy.Name);
            True(copy.Id != p.Scenes[0].Id && p.Scenes.Select(s => s.Id).Distinct().Count() == 3, "new unique scene id");
            Eq(p.Scenes[0].Objects.Count, copy.Objects.Count);
            copy.Objects[0].X = 999; copy.Objects[1].Mechanisms[0].Params["speed"] = 5.0;
            Eq(0.0, p.Scenes[0].Objects[0].X, "original untouched");
            Eq(false, p.Scenes[0].Objects[1].Mechanisms[0].Params.ContainsKey("speed"));
            Eq("Level 1 copy 2", p.DuplicateScene(0).Name);
        });
        test("scene add / rename / move / delete rules", () =>
        {
            var p = Project.CreateDemo();
            Eq("Level 3", p.AddScene().Name);
            Eq("Level 4", p.UniqueSceneName("Level 2"));
            True(p.RenameScene(2, "level 1") != null, "duplicate name refused");
            True(p.RenameScene(2, "  ") != null, "empty name refused");
            Eq(null, p.RenameScene(2, "Cave"));
            Eq(2, p.IndexOfScene(" cave "));
            Eq(true, p.MoveScene(2, 0));
            Eq("Cave", p.Scenes[0].Name);
            Eq(false, p.MoveScene(0, 5));
            Eq(true, p.RemoveScene(0)); Eq(true, p.RemoveScene(0));
            Eq(false, p.RemoveScene(0), "the last scene stays");
            Eq(1, p.Scenes.Count);
        });
        test("goto_scene asks for a scene; the next world keeps mechanisms and score", () =>
        {
            var p = Project.CreateDemo();
            var w = NewWorld();
            w.Globals["score"] = 0.0;
            w.UseScenes(p.Scenes.Select(s => s.Name));
            string? asked = null;
            w.SceneRequested += n => asked = n;
            var map = w.LoadScene(p.Scenes[0]);
            w.Step(1.0 / 60);
            var player = w.FindObject("player")!;
            var door = map.First(kv => kv.Value.Name == "Door").Key;
            player.X = door.X; player.Y = door.Y;
            w.Step(1.0 / 60);
            Eq("Level 2", asked, "event");
            Eq("Level 2", w.RequestedScene);
            Eq(true, w.Output.Contains("♪ powerup.wav"), "door sound");

            w.Globals["score"] = 3.0;
            var next = w.ForNextScene();
            Eq(null, next.RequestedScene);
            Eq(0, next.Objects.Count);
            Eq(3.0, next.Globals["score"], "score carries over");
            next.LoadScene(p.FindScene(asked!)!);
            next.Step(1.0 / 60);
            Eq(p.Scenes[1].Objects.Count, next.Objects.Count);
            Eq(true, next.Library.ContainsKey("Coin"));
            // 'scene' is the short form, and the new world can switch again (event listeners came along).
            asked = null;
            next.Load("mechanism Back\n    when start\n        scene \"level 1\"\n");
            next.Spawn("b", 0, 0, ["Back"]);
            next.Step(1.0 / 60);
            Eq("Level 1", asked);
        });
        test("goto_scene: unknown scene is a friendly error; only the first request counts", () =>
        {
            var w = NewWorld();
            w.UseScenes(["Level 1", "Level 2"]);
            w.Load("mechanism Go\n    when start\n        goto_scene \"Levle 2\"\n");
            w.Spawn("g", 0, 0, ["Go"]);
            w.Step(1.0 / 60);
            Eq(null, w.RequestedScene);
            True(w.Errors.Count == 1 && w.Errors[0].Contains("Did you mean 'Level 2'"), string.Join(" / ", w.Errors));
            int fired = 0;
            w.SceneRequested += _ => fired++;
            w.RequestScene("Level 2"); w.RequestScene("Level 1");
            Eq(1, fired); Eq("Level 2", w.RequestedScene);
            var plain = NewWorld(); // a world that wasn't given scenes
            plain.Load("mechanism Go\n    when start\n        goto_scene \"X\"\n");
            plain.Spawn("g", 0, 0, ["Go"]);
            plain.Step(1.0 / 60);
            True(plain.Errors.Count == 1 && plain.Errors[0].Contains("goto_scene"), string.Join(" / ", plain.Errors));
        });

        Console.WriteLine("Sounds");
        test("generated WAVs have valid headers", () =>
        {
            foreach (var (file, make) in SoundSynth.Defaults)
            {
                var b = make();
                Eq("RIFF", System.Text.Encoding.ASCII.GetString(b, 0, 4), file);
                Eq(b.Length - 8, BitConverter.ToInt32(b, 4), file + " RIFF size");
                Eq("WAVE", System.Text.Encoding.ASCII.GetString(b, 8, 4), file);
                Eq("fmt ", System.Text.Encoding.ASCII.GetString(b, 12, 4), file);
                Eq((short)1, BitConverter.ToInt16(b, 20), file + " PCM");
                Eq((short)1, BitConverter.ToInt16(b, 22), file + " mono");
                Eq(SoundSynth.Rate, BitConverter.ToInt32(b, 24), file + " rate");
                Eq((short)16, BitConverter.ToInt16(b, 34), file + " bits");
                Eq("data", System.Text.Encoding.ASCII.GetString(b, 36, 4), file);
                int data = BitConverter.ToInt32(b, 40);
                Eq(b.Length - 44, data, file + " data size");
                True(data / 2 >= SoundSynth.Rate * 0.15, file + " at least 150 ms");
                True(Enumerable.Range(0, data / 2).Any(i => BitConverter.ToInt16(b, 44 + i * 2) != 0), file + " not silent");
            }
            True(SoundSynth.Coin().AsSpan().SequenceEqual(SoundSynth.Coin()), "same every time");
        });
        test("sound list: defaults, discovery, resolve, import", () => InTemp(dir =>
        {
            Eq(0, Sounds.List(dir).Count, "no folder yet");
            Eq(4, Sounds.WriteDefaults(dir));
            Eq(0, Sounds.WriteDefaults(dir), "existing ones are kept");
            File.WriteAllText(Path.Combine(Sounds.Folder(dir), "notes.txt"), "not a sound");
            File.WriteAllBytes(Path.Combine(Sounds.Folder(dir), "Music.MP3"), [1, 2, 3]);
            var list = Sounds.List(dir);
            Eq("coin.wav,hit.wav,jump.wav,Music.MP3,powerup.wav", string.Join(",", list));
            Eq(Path.Combine(Sounds.Folder(dir), "coin.wav"), Sounds.Resolve(dir, "coin.wav"));
            Eq(Path.Combine(Sounds.Folder(dir), "coin.wav"), Sounds.Resolve(dir, "coin"), "no extension");
            Eq(Path.Combine(Sounds.Folder(dir), "coin.wav"), Sounds.Resolve(dir, "sounds/coin.wav"), "with the folder");
            Eq(null, Sounds.Resolve(dir, "nope.wav"));
            Eq(null, Sounds.Resolve(dir, ""));

            string outside = Path.Combine(dir, "boom.wav");
            File.WriteAllBytes(outside, SoundSynth.Hit());
            Eq("boom.wav", Sounds.Import(dir, outside));
            Eq("boom.wav", Sounds.Import(dir, outside), "same file again is reused");
            File.WriteAllBytes(outside, SoundSynth.Jump());
            Eq("boom 2.wav", Sounds.Import(dir, outside), "different file, same name");
            bool refused = false;
            try { Sounds.Import(dir, Path.Combine(Sounds.Folder(dir), "notes.txt")); } catch (InvalidOperationException) { refused = true; }
            Eq(true, refused, "not a sound");
        }));

        Console.WriteLine("My Library");
        test("library: save, list, read, clash naming", () => InTemp(dir =>
        {
            var lib = new MechanismLibrary(Path.Combine(dir, "Library"));
            Eq(0, lib.Names().Count, "no folder yet");
            const string coin = "mechanism Coin\n    param value: number = 1\n";
            Eq(NameClash.None, lib.Check("Coin", coin));
            Eq("Coin", lib.Save("Coin", coin));
            Eq(NameClash.Same, lib.Check("Coin", coin.Replace("\n", "\r\n") + "\n"));
            const string other = "mechanism Coin\n    param value: number = 5\n\n    when Coin.picked\n        x = 1\n";
            Eq(NameClash.Different, lib.Check("Coin", other));
            Eq("Coin2", lib.Save("Coin", other), "keep both");
            Eq(true, lib.Read("Coin2")!.StartsWith("mechanism Coin2\n"), "renamed inside");
            Eq(true, lib.Read("Coin2")!.Contains("when Coin2.picked"), "own signals renamed");
            Eq(1, Parser.Parse(lib.Read("Coin2")!).Count);
            Eq("Coin3", lib.Save("Coin", other + "# again\n"));
            Eq("Coin", lib.Save("Coin", other, overwrite: true));
            Eq(true, lib.Read("Coin")!.Contains("= 5"), "replaced");
            Eq("Coin,Coin2,Coin3", string.Join(",", lib.Names()));
            Eq(true, lib.Delete("Coin3")); Eq(false, lib.Delete("Coin3"));
            Eq("Coin4", MechanismLibrary.UniqueName("Coin3", n => n is "Coin3"));
            Eq("Jump2", MechanismLibrary.UniqueName("Jump", n => n == "Jump"));
            Eq("Jump", MechanismLibrary.UniqueName("Jump", _ => false));
            // A second "project" sees the same library.
            Eq(2, new MechanismLibrary(Path.Combine(dir, "Library")).Names().Count);
        }));

        Console.WriteLine("Settings");
        test("recent list: newest first, no duplicates, at most 10", () =>
        {
            var list = new List<string>();
            for (int i = 0; i < 15; i++) list = AppSettings.AddRecent(list, $@"C:\Games\P{i}");
            Eq(AppSettings.MaxRecent, list.Count);
            Eq(@"C:\Games\P14", list[0]);
            list = AppSettings.AddRecent(list, @"c:/games/p10/");
            Eq(AppSettings.MaxRecent, list.Count);
            Eq(@"c:/games/p10/", list[0]);
            Eq(1, list.Count(x => AppSettings.SameFolder(x, @"C:\Games\P10")), "deduped");
            Eq(3, AppSettings.AddRecent([@"C:\a", @"C:\b", @"C:\A\"], @"C:\c").Count, "old duplicates dropped too");
        });
        test("settings file keeps other keys; read-only never writes", () => InTemp(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "lastProject": "C:\\old", "theme": "dark", "size": 3 }""");
            var s = new AppSettings(dir);
            Eq("C:\\old", s.Get("lastProject"));
            Eq(true, s.RememberProject(@"C:\new"));
            Eq(@"C:\new", s.Get("lastProject"));
            Eq("dark", s.Get("theme"), "other keys kept");
            Eq(true, File.ReadAllText(s.FilePath).Contains("\"size\": 3"));
            Eq(@"C:\new", s.RecentProjects()[0]);
            Eq(Path.Combine(dir, "Library"), s.LibraryFolder);

            string before = File.ReadAllText(s.FilePath);
            var ro = new AppSettings(dir, readOnly: true);
            Eq(false, ro.RememberProject(@"C:\other"));
            Eq(false, ro.Set("theme", "light"));
            Eq(before, File.ReadAllText(s.FilePath), "untouched");
            Eq(null, new AppSettings(Path.Combine(dir, "missing")).Get("lastProject"));
            File.WriteAllText(s.FilePath, "{ not json");
            Eq(null, s.Get("lastProject"), "a broken file reads as empty");
        }));
    }
}
