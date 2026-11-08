using System.Diagnostics;
using BuilderEngine.Script;

/// <summary>
/// The editor/samples/tests audit (docs/audit/editor-samples-tests.md): long headless plays of every sample game with
/// random input (a "soak"), every sample mechanism through the printer round-trip, and the create → save → load → play
/// cycle. Called from Program.cs. <c>--soak-minutes N</c> (default 10, split over two seeds) sets how long each sample is played.
/// </summary>
static class AuditEditorTests
{
    const double Dt = 1.0 / 60;

    static void True(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    /// <summary>What one long play measured.</summary>
    sealed record SoakResult(int Frames, int MaxObjects, int FinalObjects, int Scenes, int Prints, List<string> Errors, double Seconds, double WorstFrameMs, SortedSet<string> States, double Travel);

    /// <summary>
    /// Plays a project's first scene for <paramref name="minutes"/> of game time the way the editor's Play and the exported
    /// player do (GameBoot.NewWorld, goto_scene through SceneRequested), with seeded random input: keys held and
    /// released, the mouse moving, left and right clicks, the wheel. Same seed = same input (the game's own random
    /// numbers aren't seeded, so two plays still differ).
    /// </summary>
    static SoakResult Soak(string dir, double minutes, int seed)
    {
        var project = Project.Load(dir);
        string saves = Path.Combine(dir, "soak_saves");
        var w = GameBoot.NewWorld(project, dir, saves);
        string? pending = null;
        int scenes = 1, prints = 0;
        void Hook(World x)
        {
            x.SceneRequested += name => pending = name;
            x.Printed += _ => prints++;
        }
        Hook(w);
        w.LoadScene(project.Scenes[0].Clone());

        var rng = new Random(seed);
        string[] keys = ["left", "right", "up", "down", "space", "a", "d", "w", "s", "e", "q", "f", "r", "t", "c", "enter", "tab", "1", "2", "3", "escape"];
        var held = new Dictionary<string, int>(); // key -> frames left
        var clock = Stopwatch.StartNew();
        double worst = 0;
        int frames = (int)(minutes * 60 / Dt), maxObjects = 0;
        bool mouseDown = false;
        double minX = double.MaxValue, maxX = double.MinValue; // how far the player went
        var states = new SortedSet<string>(StringComparer.Ordinal); // every "global = text" seen: how far the play got
        var screens = project.Mechanisms.Values.SelectMany(src => System.Text.RegularExpressions.Regex.Matches(src, "screen \"([^\"]+)\"").Select(m => m.Groups[1].Value)).Distinct().ToList();
        for (int f = 0; f < frames; f++)
        {
            // Keys: start holding one now and then (a tap or a long hold), release when its time is up.
            if (rng.NextDouble() < 0.06)
            {
                string k = keys[rng.Next(keys.Length)];
                if (!held.ContainsKey(k)) { w.KeyDown(k); held[k] = rng.NextDouble() < 0.5 ? 2 : rng.Next(10, 120); }
            }
            foreach (var k in held.Keys.ToList())
                if (--held[k] <= 0) { w.KeyUp(k); held.Remove(k); }
            // The mouse: wander, click (left mostly), sometimes hold to drag, sometimes the wheel.
            if (rng.NextDouble() < 0.1) w.MouseMove(rng.NextDouble() * w.Camera.BaseWidth, rng.NextDouble() * w.Camera.BaseHeight);
            if (!mouseDown && rng.NextDouble() < 0.03) { w.MouseDown(rng.NextDouble() < 0.8 ? "left" : "right"); mouseDown = true; }
            else if (mouseDown && rng.NextDouble() < 0.15) { w.MouseUp("left"); w.MouseUp("right"); mouseDown = false; }
            if (rng.NextDouble() < 0.005) w.MouseWheel(rng.NextDouble() < 0.5 ? 1 : -1);

            long t0 = clock.ElapsedTicks;
            w.Frame(Dt);
            worst = Math.Max(worst, (clock.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency);
            maxObjects = Math.Max(maxObjects, w.Objects.Count);
            if (f % 30 == 0)
            {
                foreach (var (k, v) in w.Globals)
                    if (v is string s && s.Length < 24) states.Add(k + "=" + s);
                foreach (var sc in screens) if (w.ScreenShown(sc)) states.Add("screen:" + sc);
                if (w.FindObject("player") is { } pl) { minX = Math.Min(minX, pl.X); maxX = Math.Max(maxX, pl.X); }
            }
            if (pending is { } next)
            {
                pending = null;
                if (project.FindScene(next) is { } target)
                {
                    var errors = w.Errors.ToList();
                    w = w.ForNextScene();
                    w.Errors.AddRange(errors.Where(e => !w.Errors.Contains(e)));
                    Hook(w);
                    w.LoadScene(target.Clone());
                    scenes++;
                }
            }
        }
        return new SoakResult(frames, maxObjects, w.Objects.Count, scenes, prints, w.Errors.Distinct().ToList(), clock.Elapsed.TotalSeconds, worst, states, maxX >= minX ? maxX - minX : 0);
    }

    /// <summary>Every name an expression reads (first part of a dotted name), outside command arguments (those are often words).</summary>
    static void Reads(Expr? e, HashSet<string> into)
    {
        switch (e)
        {
            case NameExpr n: into.Add(n.Path[0]); break;
            case UnaryExpr u: Reads(u.Operand, into); break;
            case BinaryExpr b: Reads(b.Left, into); Reads(b.Right, into); break;
            case CallExpr c: foreach (var a in c.Args) Reads(a, into); break;
            case ListExpr l: foreach (var a in l.Items) Reads(a, into); break;
            case IndexExpr x: Reads(x.Target, into); Reads(x.Index, into); break;
            case MemberExpr m: Reads(m.Target, into); break;
            case RecordExpr r: foreach (var f in r.Fields) Reads(f.Value, into); break;
        }
    }

    static void Walk(List<Stmt>? body, HashSet<string> reads, HashSet<string> sets)
    {
        if (body == null) return;
        foreach (var s in body)
            switch (s)
            {
                case AssignStmt a: sets.Add(a.Target[0]); if (a.Op != "=") reads.Add(a.Target[0]); Reads(a.Value, reads); break;
                case IfStmt i: Reads(i.Cond, reads); Walk(i.Then, reads, sets); Walk(i.Else, reads, sets); break;
                case RepeatStmt r: Reads(r.Count, reads); Walk(r.Body, reads, sets); break;
                case WaitStmt w: Reads(w.Seconds, reads); break;
                case EmitStmt em: foreach (var x in em.Args) Reads(x, reads); break;
                case TellStmt t: Reads(t.Target, reads); Reads(t.Message, reads); foreach (var x in t.Args) Reads(x, reads); break;
                case WaitUntilStmt u: Reads(u.Cond, reads); break;
                case TogetherStmt tg: Walk(tg.Body, reads, sets); break;
                case RunStmt rn: Walk(rn.Body, reads, sets); break;
                case ForEachStmt f: sets.Add(f.Var); Reads(f.List, reads); Walk(f.Body, reads, sets); break;
                case WhileStmt wh: Reads(wh.Cond, reads); Walk(wh.Body, reads, sets); break;
                case ReturnStmt rt: Reads(rt.Value, reads); break;
            }
    }

    static double SoakMinutes()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--soak-minutes");
        return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 10;
    }

    public static void Run(Action<string, Action> test, Action<string> roundTrip)
    {
        Console.WriteLine("Audit: sample games played long with random input, sample code round-trips, save/load/play");
        test("round-trip covers records, fields, choices, variants and systems (the dump used to skip them)", () =>
        {
            roundTrip("mechanism Slime\n    param side: Choice \"you\", \"rival\" = \"you\"\n    when start\n        card = {name: \"Imp\", cost: 1}\n        card.cost += 1\n        x = card.cost + other.Health.hp\n" +
                      "mechanism RedSlime from Slime\n    param side = \"rival\"\n" +
                      "system Cave\n    param light_radius: number = 90\n    includes Light radius light_radius\n");
        });
        test("'none' and 'nothing' are always nothing, even when a script sets a variable with that name", () =>
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load("mechanism T\n    when start\n        a = none\n        none = 5\n        nothing = 7\n        b = none\n        c = nothing\n        d = (none == nothing)\n");
            var o = w.Spawn("t", 0, 0, ["T"]);
            w.Globals["none"] = 3.0;
            w.Frame(Dt);
            True(w.Errors.Count == 0, "no errors: " + string.Join(" | ", w.Errors));
            var v = o.Mechanisms[0].Vars;
            True(v["a"] == null && v["b"] == null && v["c"] == null && Equals(v["d"], true), $"a={v["a"]} b={v["b"]} c={v["c"]} d={v["d"]}");
        });
        double minutes = SoakMinutes();
        foreach (string name in new[] { "Deepdelve", "Night Shift", "Hollow Lantern", "Ember Duel" })
        {
            string dir = Path.Combine(Path.GetTempPath(), "BE_Test_Soak_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                SampleGames.Make(name, dir);
                test($"{name}: every mechanism prints and parses back to the same tree", () =>
                {
                    var p = Project.Load(dir);
                    True(p.Mechanisms.Count > 0, "the sample has mechanisms");
                    foreach (var (_, src) in p.Mechanisms) roundTrip(src);
                });
                // An unset variable reads as nothing, so a typo ('helth') or a name used as a constant ('none' before it
                // was one) plays without an error. Every name a sample reads must be set somewhere: by a script, a param,
                // an event, a 'for each', or one of the commands that set a name for the lines after them.
                test($"{name}: no mechanism reads a name that nothing ever sets", () =>
                {
                    var p = Project.Load(dir);
                    var defs = StarterKit.Sources().SelectMany(s => Parser.Parse(s.Source)).Concat(p.Mechanisms.Values.SelectMany(Parser.Parse)).ToList();
                    var sets = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "self", "here", "other", "time", "dt", "none", "nothing", "mouse_x", "mouse_y", "mouse_screen_x", "mouse_screen_y", "score",
                        "crafted", "taken", "broken", "drop", "placed", // set by craft, take, dig_tile / break_tile, 'when tile broken', place
                    };
                    var readsBy = new Dictionary<string, HashSet<string>>();
                    foreach (var d in defs)
                    {
                        sets.Add(d.Name);
                        var reads = readsBy[d.Name] = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var pr in d.Params) sets.Add(pr.Name);
                        foreach (var u in d.Uses.Concat(d.Includes)) sets.Add(u.Name);
                        foreach (var w in d.Whens) { if (w.Event.Bind != null) sets.Add(w.Event.Bind); Walk(w.Body, reads, sets); }
                        foreach (var pc in d.Procs) { sets.Add(pc.Name); foreach (var a in pc.Params) sets.Add(a); Walk(pc.Body, reads, sets); }
                    }
                    foreach (var pf in p.Prefabs) sets.Add(pf.Name);
                    var unknown = readsBy.SelectMany(kv => kv.Value.Where(r => !sets.Contains(r)).Select(r => kv.Key + " reads " + r)).ToList();
                    True(unknown.Count == 0, "names nothing sets: " + string.Join(", ", unknown));
                });
                test($"{name}: load → save → load keeps the project exactly, and the saved copy plays", () =>
                {
                    string copy = dir + "_saved";
                    try
                    {
                        GameExport.CopyProject(dir, copy);
                        var first = Project.Load(dir);
                        first.Save(copy);
                        string once = File.ReadAllText(Path.Combine(copy, Project.FileName));
                        var second = Project.Load(copy);
                        second.Save(copy);
                        string twice = File.ReadAllText(Path.Combine(copy, Project.FileName));
                        True(once == twice, "saving a loaded project changes it again (the save isn't stable)");
                        True(second.Scenes.Count == first.Scenes.Count && second.Prefabs.Count == first.Prefabs.Count, "scenes and prefabs survive");
                        True(second.Mechanisms.Count == first.Mechanisms.Count && first.Mechanisms.All(kv => second.Mechanisms.TryGetValue(kv.Key, out var s) && s == kv.Value),
                            "every mechanism comes back with the same code");
                        for (int i = 0; i < first.Scenes.Count; i++)
                            True(first.Scenes[i].Objects.Count == second.Scenes[i].Objects.Count, $"scene {first.Scenes[i].Name}: same objects");
                        var w = GameBoot.NewWorld(second, copy, Path.Combine(copy, "soak_saves"));
                        w.LoadScene(second.Scenes[0].Clone());
                        w.RunFor(5);
                        True(w.Errors.Count == 0, "the saved copy plays without errors: " + string.Join(" | ", w.Errors.Take(3)));
                        True(w.Objects.Count > 0, "the saved copy has objects when playing");
                    }
                    finally
                    {
                        try { if (Directory.Exists(copy)) Directory.Delete(copy, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                });
                test($"{name}: {minutes:0.#} minutes of random play, no script errors, objects stay bounded", () =>
                {
                    foreach (int seed in new[] { 1, 2 })
                    {
                        var r = Soak(dir, minutes / 2, seed);
                        Console.WriteLine($"         seed {seed}: {r.Frames} frames in {r.Seconds:0.0} s (worst frame {r.WorstFrameMs:0.0} ms), " +
                                          $"objects max {r.MaxObjects} / end {r.FinalObjects}, scenes {r.Scenes}, prints {r.Prints}, player travelled {r.Travel:0} px");
                        Console.WriteLine("           states seen: " + string.Join(", ", r.States.Take(40)));
                        True(r.Errors.Count == 0, $"seed {seed}: script errors: " + string.Join(" | ", r.Errors.Take(5)));
                        True(r.MaxObjects < 5000, $"seed {seed}: {r.MaxObjects} objects at once (something keeps spawning)");
                    }
                });
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
