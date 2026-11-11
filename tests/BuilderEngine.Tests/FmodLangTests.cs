using BuilderEngine.Script;

/// <summary>
/// FMOD Studio in the language (World.FmodEvents.cs), the FmodEmitter, the FmodEvent param type, the Sound engine setting
/// and the export option. Everything but the last test runs without FMOD (the commands only raise requests); the last one
/// plays the FMOD Engine's example banks through a silent <see cref="FmodEventHost"/> where FMOD Studio is installed.
/// </summary>
static class FmodLangTests
{
    const double Dt = 1.0 / 60;
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }
    static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what} expected <{expected}> but got <{actual}>");
    }

    static string M(string body, string name = "T") => $"mechanism {name}\n    when start\n" + string.Join("", body.Split('\n').Select(l => "        " + l + "\n"));

    /// <summary>A world playing one object with this 'when start', and the FMOD requests it raised.</summary>
    static (World W, GameObject O, List<FmodRequest> Got) Play(string body, double seconds = Dt)
    {
        var w = new World();
        StarterKit.LoadInto(w);
        w.Load(M(body));
        var got = new List<FmodRequest>();
        w.FmodControl += got.Add;
        var o = w.Spawn("Thing", 100, 50, ["T"]);
        w.RunFor(seconds);
        Check(w.Errors.Count == 0, string.Join(" | ", w.Errors));
        return (w, o, got);
    }

    public static void Run(Action<string, Action> test, Action<string> roundTrip)
    {
        Console.WriteLine("FMOD Studio in the language (World.FmodEvents.cs)");

        test("fmod commands: parse, print as written and round-trip", () =>
        {
            string[] lines =
            [
                "play event \"event:/Music/Forest\"",
                "play event \"Music/Forest\" as \"music\" with 0.5",
                "play event \"SFX/Fire\" at self",
                "play event sound_event with volume at 10, 20",
                "stop event \"music\"",
                "stop event \"music\", now",
                "stop events",
                "stop events now",
                "set event \"music\", \"danger\" to 0.8",
                "set fmod \"time_of_day\" to 1",
                "place event \"music\" at 400, 300",
                "place event \"music\" at Boss",
            ];
            foreach (var line in lines)
            {
                var src = M(line);
                roundTrip(src);
                Eq(src, Printer.Print(Parser.Parse(src)), "printed as written: " + line);
            }
            // The space form reads the same as the printed comma form.
            Eq(Printer.Print(Parser.Parse(M("stop event \"music\", now"))), Printer.Print(Parser.Parse(M("stop event \"music\" now"))), "stop event … now without the comma");
            Eq(Printer.Print(Parser.Parse(M("set event \"music\", \"danger\" to 0.8"))), Printer.Print(Parser.Parse(M("set event \"music\" \"danger\" to 0.8"))), "set event without the comma");
            Check(Parser.Parse(M("stop event \"music\""))[0].Whens[0].Body[0] is CommandStmt { Name: "stop" }, "'stop event' is a command, not the 'stop' statement");
        });

        test("fmod blocks: the palette's FMOD blocks print, parse and play; a stop block may stop events", () =>
        {
            var m = Parser.Parse("mechanism B\n    when start:\n")[0];
            foreach (var id in new[] { "playevent", "setevent", "stopevent" })
            {
                Check(BlockEdits.StatementKinds.Any(k => k.Id == id), "in the palette: " + id);
                m.Whens[0].Body.Add(BlockEdits.NewStatement(id));
            }
            string printed = Printer.Print([m]);
            roundTrip(printed);
            Check(printed.Contains("play event \"event:/Music\" as \"music\"") && printed.Contains("set event \"music\", \"intensity\" to 1") && printed.Contains("stop event \"music\""), printed);
            Eq(null, BlockEdits.CheckCommandValues("stop", [new NameExpr(["event"], 0), new StringExpr("music", 0)]), "stop event");
            Eq(null, BlockEdits.CheckCommandValues("stop", [new NameExpr(["events"], 0)]), "stop events");
            Check(BlockEdits.CheckCommandValues("stop", [new StringExpr("music", 0)]) != null, "a bare value is still refused");
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load(printed);
            var got = new List<FmodRequest>();
            w.FmodControl += got.Add;
            w.Spawn("b", 0, 0, ["B"]);
            w.RunFor(Dt);
            Check(w.Errors.Count == 0, string.Join(" | ", w.Errors));
            Eq("Play,SetParameter,Stop", string.Join(",", got.Where(r => r.Name.Length > 0).Select(r => r.Action)));
        });

        test("play event: the event's path, its name (as), its volume (with), a position (at)", () =>
        {
            var (_, o, got) = Play("play event \"Music/Forest\" as \"music\" with 0.5\nplay event \"event:/UI/Click\"\nplay event \"SFX/Hum\" with 3 at 10, 20");
            var plays = got.Where(r => r.Action == FmodAction.Play).ToList();
            Eq(3, plays.Count, "three plays");
            Eq(new FmodRequest(FmodAction.Play, "event:/Music/Forest", "music", Volume: 0.5), plays[0]);
            Eq(new FmodRequest(FmodAction.Play, "event:/UI/Click", "event:/UI/Click"), plays[1]);
            Eq(new FmodRequest(FmodAction.Play, "event:/SFX/Hum", "event:/SFX/Hum", Volume: 1, X: 10, Y: 20), plays[2]);
            Check(got.Any(r => r.Action == FmodAction.Position && r.Name == ""), "the listener (the camera's centre)");
        });

        test("stop event (fades), stop event … now, stop events; by name or by the event's path", () =>
        {
            var (_, _, got) = Play("play event \"Music/Forest\" as \"music\"\nplay event \"UI/Click\"\nstop event \"music\"\nstop event \"UI/Click\" now\nstop event \"never played\"\nstop events now");
            var stops = got.Where(r => r.Action == FmodAction.Stop).ToList();
            Eq(4, stops.Count, string.Join(" | ", stops));
            Eq(new FmodRequest(FmodAction.Stop, "event:/Music/Forest", "music", Fade: true), stops[0]);
            Eq(new FmodRequest(FmodAction.Stop, "event:/UI/Click", "event:/UI/Click", Fade: false), stops[1]);
            Eq(new FmodRequest(FmodAction.Stop, Name: "never played", Fade: true), stops[2]);
            Eq(new FmodRequest(FmodAction.Stop, Fade: false), stops[3]);
        });

        test("set event (a parameter of the named event), set fmod (a global parameter), place event", () =>
        {
            var (_, _, got) = Play("play event \"Music/Forest\" as \"music\"\nset event \"music\" \"danger\" to 0.8\nset event \"Music/Forest\", \"calm\" to 2\nset fmod \"time_of_day\" to 1\nplace event \"music\" at 400, 300");
            Eq(new FmodRequest(FmodAction.SetParameter, "event:/Music/Forest", "music", "danger", 0.8), got.First(r => r.Action == FmodAction.SetParameter));
            Eq(new FmodRequest(FmodAction.SetParameter, "event:/Music/Forest", "music", "calm", 2), got.Where(r => r.Action == FmodAction.SetParameter).Last());
            Eq(new FmodRequest(FmodAction.SetGlobal, Parameter: "time_of_day", Value: 1), got.First(r => r.Action == FmodAction.SetGlobal));
            Eq(new FmodRequest(FmodAction.Position, "event:/Music/Forest", "music", X: 400, Y: 300), got.First(r => r.Action == FmodAction.Position && r.Name == "music"));
        });

        test("fmod commands explain what they need", () =>
        {
            foreach (var (line, says) in new[] { ("play event", "needs the event"), ("stop event", "needs the event's name"), ("set event \"m\" to 1", "set event \"music\""), ("set fmod \"x\"", "set fmod"), ("place event \"m\"", "place event") })
            {
                var w = new World();
                w.Load(M(line));
                w.Spawn("x", 0, 0, ["T"]);
                w.RunFor(Dt);
                Check(w.Errors.Any(e => e.Contains(says)), $"{line}: {string.Join(" | ", w.Errors)}");
            }
        });

        test("an event played at an object follows it, stops when it is destroyed; music plays on into the next scene", () =>
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load(M("play event \"SFX/Fire\" at self\nplay event \"Music/Forest\" as \"music\"\nwait 0.1\nself.x += 50\nwait 0.1\ndestroy self", "Torch"));
            var got = new List<FmodRequest>();
            w.FmodControl += got.Add;
            var torch = w.Spawn("Torch", 100, 50, ["Torch"]);
            w.RunFor(Dt);
            string fire = "event:/SFX/Fire@" + torch.Id;
            Eq(new FmodRequest(FmodAction.Play, "event:/SFX/Fire", fire, X: 116, Y: 66), got.First(r => r.Action == FmodAction.Play));
            w.RunFor(0.2);
            Check(got.Any(r => r.Action == FmodAction.Position && r.Name == fire && r.X == 166), "followed it: " + string.Join(" | ", got.Where(r => r.Action == FmodAction.Position)));
            w.RunFor(0.2);
            Eq(1, got.Count(r => r.Action == FmodAction.Stop && r.Name == fire), "stopped when destroyed");
            Eq(1, w.FmodPlaying.Count(), "the music is still playing");
            got.Clear();
            var next = w.ForNextScene();
            next.Load(M("stop event \"music\"", "Stopper"));
            next.Spawn("s", 0, 0, ["Stopper"]);
            next.RunFor(Dt);
            Eq(new FmodRequest(FmodAction.Stop, "event:/Music/Forest", "music"), got.Single(r => r.Action == FmodAction.Stop), "the next scene knows the music by name (same host)");
        });

        test("FmodEmitter: plays its event at its object on start; nothing without an event; the param is a FmodEvent picker", () =>
        {
            var def = AiKnowledge.Kit().Single(k => k.Name == "FmodEmitter").Def;
            Eq(ParamKind.FmodEvent, ParamTypes.Kind(def.Params.Single(p => p.Name == "event").Type));
            Eq("", ParamTypes.Empty("FmodEvent"), "empty value");
            Eq("event:/A", ParamTypes.Convert("event:/A", "FmodEvent"), "kept as text");
            var w = new World();
            StarterKit.LoadInto(w);
            var got = new List<FmodRequest>();
            w.FmodControl += got.Add;
            var a = w.Spawn("Fire", 0, 0, ["FmodEmitter"], new() { ["FmodEmitter"] = new() { ["event"] = "event:/SFX/Fire", ["volume"] = 0.4 } });
            w.Spawn("Music", 0, 0, ["FmodEmitter"], new() { ["FmodEmitter"] = new() { ["event"] = "Music/Forest", ["follow"] = false } });
            w.Spawn("Nothing", 0, 0, ["FmodEmitter"]);
            w.RunFor(Dt);
            Check(w.Errors.Count == 0, string.Join(" | ", w.Errors));
            var plays = got.Where(r => r.Action == FmodAction.Play).ToList();
            Eq(2, plays.Count, "two emitters with an event");
            Eq(new FmodRequest(FmodAction.Play, "event:/SFX/Fire", "event:/SFX/Fire@" + a.Id, Volume: 0.4, X: 16, Y: 16), plays[0]);
            Eq(new FmodRequest(FmodAction.Play, "event:/Music/Forest", "event:/Music/Forest"), plays[1]);
            Check(AiKnowledge.CommandExamples.Contains("play event") && AiKnowledge.Vocabulary().Contains("set event"), "the AI knows the commands");
        });

        test("Sound engine setting: auto by default, saved in settings.json; the status line", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "be_fmod_settings_" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                var s = new AppSettings(dir);
                Eq(AudioBackend.Auto, s.GetAudioBackend(), "not set");
                Check(s.SetAudioBackend(AudioBackend.Classic), "saved");
                Eq(AudioBackend.Classic, new AppSettings(dir).GetAudioBackend(), "read back");
                Check(File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("\"audioBackend\": \"classic\""), "the key");
                Check(!new AppSettings(dir, readOnly: true).SetAudioBackend(AudioBackend.Fmod), "a read-only session never writes");
                Eq(AudioBackend.Classic, s.GetAudioBackend(), "unchanged");
                Eq(3, FmodSettings.Choices.Length, "auto / FMOD / classic");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            Eq("Sound: FMOD 2.03.15 ✓", FmodSettings.StatusLine(AudioBackend.Auto, "2.03.15", ""));
            Eq("Sound: Classic (fmod.dll not found)", FmodSettings.StatusLine(AudioBackend.Fmod, null, "fmod.dll not found (put it next to the program, set FMOD_DLL, or install the FMOD Engine)."));
            Eq("Sound: Classic (chosen in Sound engine…)", FmodSettings.StatusLine(AudioBackend.Classic, null, "whatever"));
            FmodLibrary.ForceMissing = true;
            try { Eq(0, FmodSettings.ExportFiles().Count, "no FMOD, nothing to export"); }
            finally { FmodLibrary.ForceMissing = false; }
        });

        test("export: FMOD only when asked (and installed); the fmod bank folder is in the game", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "be_fmod_export_" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                string proj = Path.Combine(dir, "proj");
                Project.CreateDemo("Loud Game").Save(proj);
                Directory.CreateDirectory(Path.Combine(proj, "fmod", "Desktop"));
                File.WriteAllText(Path.Combine(proj, "fmod", "Desktop", "Master.bank"), "bank");
                var r = GameExport.Export(new GameExport.Options(proj, Path.Combine(dir, "out")));
                Check(File.Exists(Path.Combine(r.Folder, "game", "fmod", "Desktop", "Master.bank")), "banks copied with the project");
                Check(!File.Exists(Path.Combine(r.Folder, "fmod.dll")), "no FMOD by default");
                Check(r.Warnings.Any(x => x.Contains("FMOD wasn't included")), "told the banks need FMOD: " + string.Join(" | ", r.Warnings));
                var r2 = GameExport.Export(new GameExport.Options(proj, Path.Combine(dir, "out"), IncludeFmod: true));
                if (FmodLibrary.FindCore() != null)
                {
                    Check(File.Exists(Path.Combine(r2.Folder, "fmod.dll")), "fmod.dll next to the game");
                    Check(FmodLibrary.FindStudio() == null || File.Exists(Path.Combine(r2.Folder, "fmodstudio.dll")), "fmodstudio.dll too");
                    Check(r2.Warnings.Any(x => x.Contains("licence")), "the licence reminder");
                }
                else Check(r2.Warnings.Any(x => x.Contains("isn't installed")), "nothing to include: " + string.Join(" | ", r2.Warnings));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        // ---------------- real FMOD Studio, silent ----------------
        var examples = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", FmodLibrary.InstallFolder, @"api\studio\examples\media");
        if (FmodLibrary.FindStudio() == null || !File.Exists(Path.Combine(examples, "Vehicles.bank")))
        {
            Console.WriteLine("  skip FMOD Studio game test: fmodstudio.dll or the FMOD Engine's example banks aren't installed");
            return;
        }

        test("a game's events through a silent FmodEventHost: play event, set event, stop event, update", () =>
        {
            var proj = Path.Combine(Path.GetTempPath(), "be_fmod_host_" + Guid.NewGuid().ToString("N")[..6]);
            var banks = Path.Combine(proj, "fmod", "Desktop");
            Directory.CreateDirectory(banks);
            try
            {
                foreach (var b in new[] { "Master.bank", "Master.strings.bank", "Vehicles.bank" }) File.Copy(Path.Combine(examples, b), Path.Combine(banks, b));
                var engine = FmodStudio.ListEvents(proj, out var msg).FirstOrDefault(e => e.Parameters.Count > 0);
                Check(engine != null, "an event with a parameter: " + msg);
                var p = engine!.Parameters[0];
                using var host = new FmodEventHost(proj, silent: true);
                var w = new World();
                StarterKit.LoadInto(w);
                w.Load(M($"play event \"{engine.Short}\" as \"car\" with 0.5 at self\nwait 0.1\nset event \"car\" \"{p.Name}\" to {(p.Min + p.Max) / 2}\nwait 0.1\nstop event \"car\" now"));
                w.FmodControl += host.Handle;
                w.Spawn("Car", 10, 10, ["T"]);
                for (int i = 0; i < 3; i++) { w.Frame(Dt); host.Update(); }
                Check(w.Errors.Count == 0, string.Join(" | ", w.Errors));
                Check(host.IsPlaying("car"), "the car plays: " + host.Status);
                Check(host.Status.Contains("event(s)"), host.Status);
                for (int i = 0; i < 8; i++) { w.Frame(Dt); host.Update(); }
                Check(host.IsPlaying("car"), "still playing after its parameter changed");
                for (int i = 0; i < 12; i++) { w.Frame(Dt); host.Update(); }
                Check(!host.IsPlaying("car"), "stopped");
                Eq(3, host.Reload(), "reload the banks");
            }
            finally { FmodStudio.ClearCache(); try { Directory.Delete(proj, true); } catch { } }
        });
    }
}
