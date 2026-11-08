using BuilderEngine.Script;

/// <summary>
/// The "Night Shift" sample game: built into a temp folder and played headless with scripted keys and clicks
/// (power, doors, cameras, creatures on their routes, the jumpscare, winning a night, saved progress). Called from Program.cs.
/// </summary>
static class NightShiftTests
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

    /// <summary>A game being played: the world, the save folder and quick ways to press, click and read.</summary>
    sealed class Game
    {
        public readonly World W;
        public Game(World w) => W = w;
        public object? G(string name) => W.Globals.GetValueOrDefault(name);
        public double Num(string name) => G(name) is double d ? d : 0;
        public string Mode => World.ToText(G("mode"));
        public GameObject O(string name) => W.Objects.First(o => o.Name == name);
        public object? Var(string obj, string name) => O(obj).AllMechanisms().Select(m => m.Vars.GetValueOrDefault(name)).FirstOrDefault(v => v != null);
        public string Where(string creature) => World.ToText(Var(creature, "where"));
        public void Run(double seconds) => W.RunFor(seconds, Dt);
        public void Press(string key) { W.KeyDown(key); W.Frame(Dt); W.KeyUp(key); W.Frame(Dt); }
        /// <summary>Clicks the middle of a screen object (screen pixels) or of a scene object (through the camera).</summary>
        public void Click(string name)
        {
            var o = O(name);
            var cam = W.Camera;
            double x = o.X + o.Width / 2, y = o.Y + o.Height / 2;
            if (!o.Ui) { x = (x - cam.Left) * cam.Zoom; y = (y - cam.Top) * cam.Zoom; }
            W.MouseMove(x, y);
            W.MouseDown();
            W.Frame(Dt);
            W.MouseUp();
            W.Frame(Dt);
        }
        public void NoErrors() => Eq(0, W.Errors.Count, string.Join(" | ", W.Errors));
        /// <summary>Runs until the condition holds (or fails after the time limit).</summary>
        public void RunUntil(Func<bool> done, double limit, string what)
        {
            for (double t = 0; t < limit; t += Dt)
            {
                if (done()) return;
                W.Frame(Dt);
            }
            throw new Exception($"timed out waiting for: {what} (mode {Mode}, errors: {string.Join(" | ", W.Errors)})");
        }
    }

    /// <summary>The sample's project with some params changed (night length, creature levels…).</summary>
    static Project Sample(double nightSeconds = 60, Action<Scene>? tweak = null)
    {
        var p = NightShiftSample.Build();
        var s = p.Scenes[0];
        Param(s, "Night Shift", "night_seconds", nightSeconds);
        Param(s, "Night Shift", "intro_seconds", 0.3);
        tweak?.Invoke(s);
        return p;
    }

    static void Param(Scene s, string obj, string key, object? value) => s.Objects.First(o => o.Name == obj).Mechanisms[0].Params[key] = value;

    static List<object?> Levels(double a, double b, double c) => [a, b, c];

    /// <summary>Every creature asleep (levels 0): for power, camera and clock tests.</summary>
    static void Calm(Scene s)
    {
        foreach (var n in new[] { "Clementine", "Corporal Tock", "Odette", "Pip" }) Param(s, n, "levels", Levels(0, 0, 0));
    }

    static Game Play(Project p, string saveDir)
    {
        var w = new World();
        StarterKit.LoadInto(w);
        foreach (var src in p.Mechanisms.Values) w.Load(src);
        w.Globals["score"] = 0.0; // what the editor sets
        w.SaveFolder = saveDir;
        w.UseScenes(p.Scenes.Select(s => s.Name));
        w.LoadScene(p.Scenes[0]);
        var g = new Game(w);
        g.Run(0.1);
        return g;
    }

    /// <summary>Title → New game (clicked) → the intro → on duty.</summary>
    static Game NewNight(Project p, string saveDir)
    {
        var g = Play(p, saveDir);
        g.Click("New Game");
        g.RunUntil(() => g.Mode == "night", 5, "the night starts");
        g.Run(1); // the fade in
        return g;
    }

    static void InTemp(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "BE_Test_NightShift_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Engine additions for the Night Shift sample");
        test("'global power, night' makes values every mechanism shares; a variable made before is handed over", () =>
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load("mechanism Boss\n    when start\n        power = 7\n        global power, night\n        power -= 2\n        night = 3\n" +
                   "mechanism Reader\n    when tick\n        seen = power\n        if night == 3: night_seen = true\n    when told \"use\"\n        power -= 1\n" +
                   "mechanism Bad\n    when start\n        global 5\n");
            w.Spawn("boss", 0, 0, ["Boss"]);
            var r = w.Spawn("reader", 0, 0, ["Reader"]);
            w.RunFor(0.1);
            Eq(5.0, w.Globals["power"], "the mechanism's 7 became the global, then -2");
            Eq(3.0, w.Globals["night"]);
            Eq(5.0, r.Mechanisms[0].Vars["seen"]); Eq(true, r.Mechanisms[0].Vars["night_seen"]);
            w.Tell(r, "use", []);
            Eq(4.0, w.Globals["power"], "another mechanism changes the same value");
            True(!r.Mechanisms[0].Vars.ContainsKey("power"), "no copy of its own");
            Eq(4.0, w.ForNextScene().Globals["power"], "goes on to the next scene");
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
            w.Spawn("bad", 0, 0, ["Bad"]);
            w.RunFor(0.05);
            True(w.Errors.Count == 1 && w.Errors[0].Contains("'global' takes names"), "a friendly error: " + string.Join(" | ", w.Errors));
        });
        test("'camera view off' jumps back where the game started when there's no player to follow", () =>
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load("mechanism Look\n    when told \"on\"\n        camera view find(\"Cam\")\n    when told \"off\"\n        camera view off\n");
            var s = new Scene { Width = 1600, Height = 900 };
            s.Add(new SceneObject { Name = "Cam", X = 1000, Y = 500, Width = 400, Height = 225, Mechanisms = [new SceneMechanism { Name = "CameraPoint" }] });
            s.Add(new SceneObject { Name = "Look", X = 0, Y = 0, Mechanisms = [new SceneMechanism { Name = "Look" }] });
            w.LoadScene(s);
            w.RunFor(0.1);
            var look = w.Objects.First(o => o.Name == "Look");
            Eq(400.0, w.Camera.X); Eq(225.0, w.Camera.Y);
            w.Tell(look, "on", []);
            w.RunFor(0.1);
            Eq(1200.0, w.Camera.X); Eq(2.0, w.Camera.Zoom);
            w.Tell(look, "off", []);
            w.Frame(Dt);
            Eq(400.0, w.Camera.X, "back at once"); Eq(225.0, w.Camera.Y); Eq(1.0, w.Camera.Zoom);
        });

        Console.WriteLine("Sample: Night Shift at the Museum of Dolls");
        test("night shift: the sample writes a project folder; every mechanism parses; pictures and sounds are there", () => InTemp(dir =>
        {
            SampleGames.Make("Night Shift", dir);
            var p = Project.Load(dir);
            Eq("Night Shift", p.Name);
            foreach (var m in new[] { "NightShift", "Office", "OfficeDoor", "HallLight", "WallSwitch", "Doorway", "UsageCell", "Spinner", "Tablet", "CamButton", "Room", "Creature", "Ballerina", "Jumpscare" })
                True(p.Mechanisms.ContainsKey(m), "mechanism " + m);
            foreach (var (name, src) in p.Mechanisms) Eq(name, Parser.Parse(src).Single().Name, "one mechanism per file");
            var images = Images.List(dir);
            foreach (var img in new[] { "office.png", "doll.png", "ballerina.png", "ballerina_restless.png", "soldier.png", "twin.png", "scare_doll.png", "title.png", "room_hall.png", "fan1.png", "fan2.png", "shutter.png" })
                True(images.Contains(img), "picture " + img);
            var png = File.ReadAllBytes(Path.Combine(Images.Folder(dir), "doll.png"));
            Eq(0x89, (int)png[0], "PNG signature"); Eq((byte)'P', png[1]);
            foreach (var snd in new[] { "drone.wav", "hum.wav", "musicbox.wav", "tick.wav", "door.wav", "scream.wav", "chime.wav", "static.wav", "knock.wav" })
            {
                var wav = File.ReadAllBytes(Path.Combine(Sounds.Folder(dir), snd));
                True(wav.Length > 2000 && wav[0] == 'R' && wav[8] == 'W', "sound " + snd);
            }
            // Every picture and sound the scripts and the scene name exists.
            var all = string.Join("\n", p.Mechanisms.Values) + p.ToJson();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(all, "\"([a-z_0-9]+\\.(png|wav))\""))
                True(m.Groups[2].Value == "png" ? images.Contains(m.Groups[1].Value) : Sounds.Resolve(dir, m.Groups[1].Value) != null, "missing file " + m.Groups[1].Value);
        }));

        test("night shift: the title screen, then 'New game' (clicked) starts night 1 at 12 AM with no script errors", () => InTemp(dir =>
        {
            var g = Play(Sample(), dir);
            Eq("title", g.Mode);
            True(g.W.ScreenShown("Title"), "title screen");
            True(g.O("Continue").Hidden, "no Continue without a save");
            g.Click("New Game");
            True(g.W.ScreenShown("Intro"), "the night's intro");
            Eq("Night 1", g.O("Intro Night").Caption);
            g.RunUntil(() => g.Mode == "night", 5, "on duty");
            Eq("Grand Hall", g.Where("Clementine"));
            Eq("Music Room", g.Where("Odette"));
            g.Run(1);
            True(g.W.ScreenShown("Hud") && !g.W.ScreenShown("Title"), "HUD shown, title gone");
            Eq("12 AM", g.O("Clock").Caption);
            Eq(100.0, Math.Ceiling(g.Num("power")), "full power");
            True(g.O("Pip").Hidden, "Pip sleeps on night 1");
            True(g.W.Darkness > 0.8, "the office is dark");
            True(g.W.Loops.ContainsKey("hum.wav") && g.W.Loops.ContainsKey("drone.wav"), "ambient loops");
            g.NoErrors();
        }));

        test("night shift: power drains over the night, about three times faster with both doors shut", () => InTemp(dir =>
        {
            var g = NewNight(Sample(60, Calm), dir);
            double p0 = g.Num("power");
            g.Run(3);
            double open = p0 - g.Num("power");
            True(open > 0.5, $"power drains ({open})");
            Eq(1.0, g.Num("usage"));
            g.Press("q");
            g.Click("Right Door Button");
            g.Run(0.3);
            True(g.O("Left Door").Height > 280 && g.O("Right Door").Height > 280, "both shutters down");
            Eq(3.0, g.Num("usage"));
            Eq("#D84A3A", g.O("Left Door Button").Color, "the button glows");
            double p1 = g.Num("power");
            g.Run(3);
            double closed = p1 - g.Num("power");
            True(closed > open * 2.6 && closed < open * 3.4, $"three times faster ({open} → {closed})");
            g.Press("a");
            Eq(4.0, Math.Round(g.Num("usage")), "a hall light adds a bar");
            True(g.O("Left Hall Light").LightRadius > 0, "the hall light shines");
            g.Press("a"); g.Press("q"); g.Click("Right Door Button");
            g.Run(0.4);
            Eq(1.0, g.Num("usage"));
            True(g.O("Left Door").Height < 1, "the shutter is up again");
            g.NoErrors();
        }));

        test("night shift: Tab opens the cameras, 1-6 and the map switch rooms, Tab goes back to the office", () => InTemp(dir =>
        {
            var g = NewNight(Sample(60, Calm), dir);
            g.Press("tab");
            g.Run(0.2);
            Eq("Grand Hall", g.W.Functions["camera_view"]([]));
            True(g.W.ScreenShown("Cameras"), "the tablet's screen");
            Eq("CAM 1   Grand Hall", g.O("Cam Label").Caption);
            Eq(2.0, g.Num("usage"), "the tablet draws power");
            True(g.W.NoiseAmount > 0, "static");
            True(g.W.OnScreen(g.O("Clementine")), "Clementine is on camera");
            g.Press("3");
            Eq("Music Room", g.W.Functions["camera_view"]([]));
            True(g.W.OnScreen(g.O("Odette")) && !g.W.OnScreen(g.O("Clementine")), "Odette watched now");
            g.Click("Cam 6");
            Eq("East Corridor", g.W.Functions["camera_view"]([]));
            Eq("#3F8A55", g.O("Cam 6").Color, "its map button is lit");
            g.Click("Cameras Button");
            g.Run(0.2);
            Eq("Office View", g.W.Functions["camera_view"]([]));
            True(!g.W.ScreenShown("Cameras"), "tablet down");
            Eq(400.0, g.W.Camera.X); Eq(705.0, g.W.Camera.Y);
            Eq(1.0, g.Num("usage"));
            g.NoErrors();
        }));

        test("night shift: a creature walks its route room by room to the office doorway", () => InTemp(dir =>
        {
            var g = NewNight(Sample(30, s => { Calm(s); Param(s, "Clementine", "levels", Levels(20, 20, 20)); Param(s, "Clementine", "move_every", 20.0); }), dir);
            var seen = new List<string> { g.Where("Clementine") };
            g.RunUntil(() =>
            {
                string w = g.Where("Clementine");
                if (w != seen[^1]) seen.Add(w);
                return w == "Left Doorway";
            }, 10, "Clementine at the left doorway");
            Eq("Grand Hall, Doll Gallery, West Corridor, Left Doorway", string.Join(", ", seen));
            var c = g.O("Clementine");
            var spot = g.O("Left Doorway");
            Eq(spot.X, c.X); Eq(spot.Height, c.Height, "she fills the doorway");
            g.NoErrors();
        }));

        test("night shift: a shut door sends it back (it knocks, power drops); an open one lets it in → jumpscare → game over", () => InTemp(dir =>
        {
            var p = Sample(30, s => { Calm(s); Param(s, "Clementine", "levels", Levels(20, 20, 20)); });
            var g = NewNight(p, dir);
            g.Press("q");
            g.RunUntil(() => g.Where("Clementine") == "Left Doorway", 10, "Clementine at the door");
            double before = g.Num("power");
            g.RunUntil(() => g.Where("Clementine") == "Grand Hall", 5, "sent back to the start");
            Eq("night", g.Mode, "still alive");
            True(g.W.Output.Contains("♪ knock.wav"), "she knocked");
            True(g.Num("power") < before - 0.9, "the knock cost power");
            g.Press("q"); // open again
            g.RunUntil(() => g.Mode == "over", 15, "caught");
            Eq("Clementine", g.G("caught_by"));
            var js = g.O("Jumpscare");
            True(!js.Hidden && js.Image == "scare_doll.png", "the jumpscare fills the screen");
            True(g.W.Output.Contains("♪ scream.wav"), "a scream");
            True(g.W.FlashAmount > 0, "a red flash");
            g.RunUntil(() => g.W.ScreenShown("Lose"), 4, "game over screen");
            Eq("Clementine got into the office.", g.O("Lose Text").Caption);
            True(js.Hidden, "the picture is gone");
            // Try again: the same night from the start.
            g.Click("Try Again");
            g.RunUntil(() => g.Mode == "night", 5, "night again");
            Eq(100.0, Math.Ceiling(g.Num("power")));
            Eq("Grand Hall", g.Where("Clementine"));
            True(g.O("Left Door").Height < 1, "doors open again");
            g.NoErrors();
        }));

        test("night shift: a shy doll stays put while the camera watches her", () => InTemp(dir =>
        {
            var g = NewNight(Sample(30, s => { Calm(s); Param(s, "Clementine", "levels", Levels(20, 20, 20)); Param(s, "Clementine", "move_every", 20.0); }), dir);
            g.Press("tab"); // CAM 1: the Grand Hall, where she starts
            g.Run(3);
            Eq("Grand Hall", g.Where("Clementine"), "watched: no move");
            g.Press("tab");
            g.RunUntil(() => g.Where("Clementine") != "Grand Hall", 3, "she moves when nobody looks");
            g.NoErrors();
        }));

        test("night shift: Odette grows restless unwatched and runs for the right door; watching calms her", () => InTemp(dir =>
        {
            var g = NewNight(Sample(30, s => { Calm(s); Param(s, "Odette", "levels", Levels(8, 8, 8)); }), dir);
            g.Run(2);
            double r1 = (double)g.Var("Odette", "restless")!;
            True(r1 > 30, $"restless while nobody watches ({r1})");
            g.Press("tab"); g.Press("3");
            g.Run(1);
            True((double)g.Var("Odette", "restless")! < r1, "watching calms her");
            g.Press("tab");
            g.RunUntil(() => g.Where("Odette") == "Right Doorway", 10, "she runs to the right doorway");
            True(g.W.Output.Contains("♪ music_stop.wav"), "the music stopped first");
            g.Click("Right Door Button");
            g.RunUntil(() => g.Where("Odette") == "Music Room", 5, "the shut door sends her back");
            Eq("night", g.Mode);
            g.NoErrors();
        }));

        test("night shift: when the power runs out the doors open, it goes black, and Odette comes", () => InTemp(dir =>
        {
            var g = NewNight(Sample(30, Calm), dir);
            g.Press("q");
            g.W.Globals["power"] = 0.5;
            g.RunUntil(() => g.G("power_out") is true, 2, "power out");
            g.Run(1.6);
            True(g.O("Left Door").Height < 1, "the shutter opened by itself");
            True(g.W.Darkness > 0.97, $"black ({g.W.Darkness})");
            g.Press("q");
            True(g.O("Left Door").Height < 1, "the doors don't work any more");
            g.Press("tab");
            True(!g.W.ScreenShown("Cameras"), "neither do the cameras");
            g.RunUntil(() => g.Mode == "over", 10, "Odette gets in");
            Eq("Odette", g.G("caught_by"));
            True(g.W.Output.Contains("♪ musicbox.wav"), "her music box plays in the dark");
            g.NoErrors();
        }));

        test("night shift: surviving to 6 AM wins the night and saves it; Continue starts the next night", () => InTemp(dir =>
        {
            var g = NewNight(Sample(12, Calm), dir);
            g.RunUntil(() => g.Num("hour") >= 3, 8, "3 AM");
            Eq("3 AM", g.O("Clock").Caption);
            g.RunUntil(() => g.Mode == "won", 8, "6 AM");
            True(g.W.Output.Contains("♪ chime.wav"), "the 6 AM chime");
            g.Run(1.5);
            True(g.W.ScreenShown("Win"), "the win screen");
            True(!g.O("Next Night").Hidden && g.O("Win Title").Hidden, "Next night offered");
            Eq(2.0, g.W.SavedValue("night"), "progress saved");
            // A new session: the title offers Continue at night 2.
            var g2 = Play(Sample(12, s => { Calm(s); Param(s, "Pip", "levels", Levels(0, 1, 0)); }), dir);
            True(!g2.O("Continue").Hidden, "Continue shown");
            Eq("Continue  ·  Night 2", g2.O("Continue").Caption);
            g2.Click("Continue");
            g2.RunUntil(() => g2.Mode == "night", 5, "night 2");
            Eq(2.0, g2.Num("night"));
            True(!g2.O("Pip").Hidden, "Pip is awake on night 2");
            g2.NoErrors();
            g.NoErrors();
        }));

        test("night shift: winning the last night ends the game; the title shows the trophy", () => InTemp(dir =>
        {
            var g = Play(Sample(12, Calm), dir);
            g.W.SaveValue("night", 3.0);
            var g2 = Play(Sample(12, Calm), dir);
            g2.Click("Continue");
            g2.RunUntil(() => g2.Mode == "won", 20, "6 AM on night 3");
            g2.Run(1.5);
            True(g2.O("Next Night").Hidden && !g2.O("Win Title").Hidden, "only back to the title");
            Eq(true, g2.W.SavedValue("beaten"));
            g2.Click("Win Title");
            True(g2.W.ScreenShown("Title") && !g2.O("Trophy").Hidden, "the trophy");
            g2.NoErrors();
        }));
    }
}
