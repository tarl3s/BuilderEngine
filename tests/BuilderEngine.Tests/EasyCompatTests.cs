using System.Text.Json.Nodes;
using BuilderEngine.Script;

/// <summary>
/// EasyCompat (EasyCompat*.cs, StarterKit/EasyEngine/*.bs): EasyEngine projects playing in BuilderEngine, headless.
/// Small hand-made .eeproj projects check each behavior the way EasyEngine plays it; a copy of the owner's Mistify (when
/// it is on this computer: Desktop\Jogos\Mistify, or BE_MISTIFY) plays with scripted keys, with no script errors and its
/// cutscenes reaching their end. Real projects are only read: the tests work on copies in a temp folder.
/// </summary>
static class EasyCompatTests
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

    /// <summary>A game playing an EasyEngine project the way the editor's Play and the exported player do.</summary>
    sealed class Session
    {
        public readonly Project Project;
        public readonly string Dir;
        public World World = null!;
        public readonly List<string> Printed = [];
        public readonly List<string> Sounds = [];
        public readonly List<string> Errors = [];
        public readonly List<string> Scenes = [];
        string? _next;

        public Session(Project p, string dir, string? scene = null)
        {
            Project = p;
            Dir = dir;
            var w = GameBoot.NewWorld(p, dir, Path.Combine(dir, "saves"));
            Errors.AddRange(w.Errors);
            Hook(w);
            Start(w, scene ?? p.Scenes[0].Name);
        }

        void Hook(World w)
        {
            w.Printed += s => Printed.Add(s);
            w.SoundPlayed += s => Sounds.Add(s);
            w.SceneRequested += name => _next = name;
        }

        void Start(World w, string scene)
        {
            World = w;
            Scenes.Add(scene);
            w.LoadScene(Project.FindScene(scene)!.Clone());
        }

        /// <summary>Plays <paramref name="seconds"/>, switching scenes like the hosts do; script errors are collected.</summary>
        public void Run(double seconds, Action<double>? each = null)
        {
            for (double t = 0; t < seconds - 1e-9; t += Dt)
            {
                each?.Invoke(t);
                World.Frame(Dt);
                if (_next != null)
                {
                    Errors.AddRange(World.Errors);
                    var next = World.ForNextScene();
                    string name = _next;
                    _next = null;
                    Start(next, name);
                }
            }
        }

        public List<string> AllErrors() => Errors.Concat(World.Errors).Distinct().ToList();

        public GameObject Find(string name) => World.Objects.FirstOrDefault(o => o.Name == name) ?? World.FindObject(name) ?? throw new Exception($"no object '{name}' in {World.Objects.Count} objects");

        public void Down(string key) => World.KeyDown(key);
        public void Up(string key) => World.KeyUp(key);

        /// <summary>A key pressed for one frame.</summary>
        public void Tap(string key)
        {
            Down(key);
            Run(Dt);
            Up(key);
        }
    }

    static readonly List<string> Made = []; // every temp folder made, deleted when the tests end

    static string NewDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "BE_Compat_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        Made.Add(d);
        return d;
    }

    static void InTemp(Action<string> body)
    {
        string d = NewDir();
        try { body(d); }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---------------- Building small EasyEngine projects ----------------

    static JsonObject Beh(string kind, object? nums = null, object? texts = null)
    {
        var b = new JsonObject { ["Kind"] = kind, ["Nums"] = new JsonObject(), ["Texts"] = new JsonObject() };
        if (nums != null) foreach (var p in nums.GetType().GetProperties()) b["Nums"]![p.Name] = JsonValue.Create(Convert.ToDouble(p.GetValue(nums)));
        if (texts != null) foreach (var p in texts.GetType().GetProperties()) b["Texts"]![p.Name] = JsonValue.Create(p.GetValue(texts)?.ToString());
        return b;
    }

    static int _id = 1;

    static JsonObject Obj(string name, double x, double y, double w, double h, params JsonObject[] behaviors) => Obj(name, x, y, w, h, "None", behaviors);

    static JsonObject Obj(string name, double x, double y, double w, double h, string physics, params JsonObject[] behaviors)
    {
        var arr = new JsonArray();
        foreach (var b in behaviors) arr.Add(b);
        return new JsonObject
        {
            ["Id"] = _id++, ["Name"] = name, ["Type"] = name == "Player" ? "Player" : "Object", ["X"] = x, ["Y"] = y, ["Width"] = w, ["Height"] = h,
            ["Color"] = "#9A9AA0", ["SpritePath"] = null, ["Opacity"] = 1, ["Animations"] = new JsonArray(),
            ["Physics"] = new JsonObject { ["Mode"] = physics, ["GravityScale"] = 1, ["Bounciness"] = 0 },
            ["Behaviors"] = arr, ["Variables"] = new JsonObject(),
        };
    }

    static JsonObject SceneOf(string name, double w, double h, params JsonObject[] objects)
    {
        var objs = new JsonArray();
        foreach (var o in objects) objs.Add(o);
        return new JsonObject
        {
            ["Name"] = name, ["Width"] = w, ["Height"] = h, ["BackgroundColor"] = "#202020", ["AmbientLight"] = 1, ["Variables"] = new JsonObject(),
            ["Layers"] = new JsonArray
            {
                new JsonObject { ["Name"] = "Background", ["Kind"] = "Background", ["ScreenSpace"] = false, ["Objects"] = new JsonArray() },
                new JsonObject { ["Name"] = "Base", ["Kind"] = "Base", ["ScreenSpace"] = false, ["Objects"] = objs },
                new JsonObject { ["Name"] = "UI", ["Kind"] = "UI", ["ScreenSpace"] = true, ["Objects"] = new JsonArray() },
            },
        };
    }

    static Project ProjectOf(params JsonObject[] scenes)
    {
        var arr = new JsonArray();
        foreach (var s in scenes) arr.Add(s);
        var ee = new JsonObject { ["Name"] = "Compat test", ["ScreenWidth"] = 960, ["ScreenHeight"] = 540, ["Scenes"] = arr, ["Variables"] = new JsonObject() };
        return EasyLink.FromEeproj(ee.ToJsonString(), "C:\\nowhere");
    }

    static JsonObject Player(double x, double y, params JsonObject[] extra) =>
        Obj("Player", x, y, 40, 60, "Dynamic", [Beh("Platformer", new { controls = 1, speed = 260, jump = 640 }), .. extra]);

    static JsonObject Ground(double y = 500, double w = 2000) => Obj("Ground", 0, y, w, 40, Beh("Solid"));

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("EasyCompat: EasyEngine behaviors playing in BuilderEngine (EasyCompat*.cs, StarterKit/EasyEngine)");

        test("compat: a project without EasyEngine behaviors gets no EasyCompat", () =>
        {
            var p = new Project { Name = "Plain" };
            p.Scenes.Add(new Scene { Name = "Level 1" });
            var w = GameBoot.NewWorld(p, NewDir());
            Eq(null, w.Easy);
            True(!w.Library.ContainsKey("EasyPlatformer"), "no EasyKit in a plain project");
        });

        test("compat: every EasyKit mechanism loads and the project keeps EasyEngine's data", () =>
        {
            var p = ProjectOf(SceneOf("Level", 960, 540, Player(100, 400), Ground()));
            string before = p.Scenes[0].Objects.First(o => o.Name == "Player").Easy!.ToJsonString();
            var s = new Session(p, NewDir());
            True(s.World.Easy != null, "EasyCompat is on");
            Eq(0, s.AllErrors().Count, string.Join(" | ", s.AllErrors()));
            foreach (var m in new[] { "EasyPlatformer", "EasySolid", "EasyHealth", "EasyInteract", "EasyDayNight", "EasyStayInside" })
                True(s.World.Library.ContainsKey(m), m + " is loaded");
            True(s.Find("Player").Mechanisms.Any(m => m.Def.Name == "EasyPlatformer"), "the player plays as EasyPlatformer");
            Eq(0, p.Scenes[0].Objects.First(o => o.Name == "Player").Mechanisms.Count, "nothing is added to the saved object");
            Eq(before, p.Scenes[0].Objects.First(o => o.Name == "Player").Easy!.ToJsonString(), "its EasyEngine data is untouched");
            Eq(960.0, s.World.Camera.BaseWidth, "EasyEngine's 960 × 540 view");
        });

        test("compat: platformer: EasyEngine's speed, gravity and jump", () =>
        {
            var p = ProjectOf(SceneOf("Level", 2000, 540, Player(100, 440), Ground()));
            var s = new Session(p, NewDir());
            s.Run(0.5);
            var pl = s.Find("Player");
            True(Math.Abs(pl.Y + pl.Height - 500) < 1, $"it stands on the ground (bottom {pl.Y + pl.Height})");
            True(pl.OnGround, "on the ground");
            double x0 = pl.X;
            s.Down("right");
            s.Run(1);
            s.Up("right");
            double ran = pl.X - x0;
            True(Math.Abs(ran - 260) < 12, $"runs 260 px a second (ran {ran:0})");
            double y0 = pl.Y, top = pl.Y;
            s.Down("space");
            s.Run(0.6, _ => top = Math.Min(top, pl.Y));
            s.Up("space");
            double height = y0 - top;
            True(Math.Abs(height - 102) < 8, $"a jump of 640 rises about 102 px (rose {height:0})");
            s.Run(0.6);
            True(pl.OnGround, "lands again");
            // a short tap is a small hop (letting go cuts the rise)
            top = pl.Y; y0 = pl.Y;
            s.Down("space"); s.Run(0.05); s.Up("space");
            s.Run(0.6, _ => top = Math.Min(top, pl.Y));
            True(y0 - top < 60, $"a tap is a small hop (rose {y0 - top:0})");
        });

        test("compat: solid: blocks the player; one-way: jump through from below", () =>
        {
            var wall = Obj("Wall", 300, 300, 40, 200, Beh("Solid"));
            var shelf = Obj("Shelf", 80, 420, 120, 10, Beh("Solid", new { oneWay = 1 }));
            var p = ProjectOf(SceneOf("Level", 2000, 540, Player(100, 440), Ground(), wall, shelf));
            var s = new Session(p, NewDir());
            s.Run(0.3);
            var pl = s.Find("Player");
            s.Down("right"); s.Run(1.5); s.Up("right");
            True(pl.X + pl.Width <= 300.5, $"stopped by the wall (right edge {pl.X + pl.Width:0})");
            s.Down("left"); s.Run(0.65); s.Up("left");
            s.Down("space"); s.Run(0.7); s.Up("space");
            s.Run(0.5);
            True(Math.Abs(pl.Y + pl.Height - 420) < 1.5, $"jumped up through the one-way shelf and stands on it (bottom {pl.Y + pl.Height:0})");
        });

        test("compat: patrol turns at its ends and at walls", () =>
        {
            var guard = Obj("Guard", 400, 300, 40, 40, Beh("Patrol", new { speed = 100, distance = 200 }));
            var wall = Obj("Wall", 900, 0, 40, 540, Beh("Solid"));
            var walker = Obj("Walker", 700, 440, 40, 60, "Dynamic", Beh("Patrol", new { speed = 150, distance = 2000 }));
            var p = ProjectOf(SceneOf("Level", 2000, 540, guard, Ground(), wall, walker));
            var s = new Session(p, NewDir());
            var g = s.Find("Guard");
            double maxX = 0, minX = 9999;
            s.Run(5, _ => { maxX = Math.Max(maxX, g.X); minX = Math.Min(minX, g.X); });
            True(maxX >= 598 && maxX <= 604, $"goes to its far end (max x {maxX:0})");
            True(minX >= 398 && minX <= 402, $"and back (min x {minX:0})");
            var wk = s.Find("Walker");
            True(wk.X + wk.Width <= 900.5 && wk.Vx < 0 || wk.X < 860, $"turned at the wall (x {wk.X:0}, vx {wk.Vx:0})");
        });

        test("compat: health and damager: a hit, safe time, knockback, and an enemy that dies", () =>
        {
            var spikes = Obj("Spikes", 400, 480, 80, 20, Beh("Damager", new { damage = 1, knockback = 300 }, new { target = "Player" }));
            var player = Player(300, 440, Beh("Health", new { max = 3, invincible = 1, respawn = 1 }, new { var = "health" }));
            var bullet = Obj("Bullet", 600, 300, 10, 10, Beh("Damager", new { damage = 5, knockback = 0, vanish = 1 }, new { target = "Slime" }));
            var slime = Obj("Slime", 600, 290, 40, 40, Beh("Health", new { max = 2, respawn = 0 }, new { var = "slimeHP" }));
            var p = ProjectOf(SceneOf("Level", 2000, 540, player, Ground(), spikes, bullet, slime));
            var s = new Session(p, NewDir());
            s.Run(0.2);
            Eq(3.0, s.World.Globals["health"], "health starts full");
            s.Down("right"); s.Run(0.5); s.Up("right");
            s.Run(0.1);
            Eq(2.0, s.World.Globals["health"], "one hit, then a safe moment");
            True(s.World.Objects.All(o => o.Name != "Slime"), "the slime with no health left is gone");
            True(s.World.Objects.All(o => o.Name != "Bullet"), "the bullet is used up by its hit");
            Eq(0, s.AllErrors().Count, string.Join(" | ", s.AllErrors()));
        });

        test("compat: interact + go to scene: press E near the sign, walk into the door", () =>
        {
            var sign = Obj("Sign", 160, 440, 40, 60, Beh("Interact", new { radius = 80 }, new { action = "say", text = "Hello there.\nSecond line.", key = "E", trigger = "Player" }));
            var door = Obj("Door", 600, 400, 40, 100, Beh("GoToScene", null, new { scene = "Room 2", trigger = "Player" }));
            var p = ProjectOf(SceneOf("Room 1", 960, 540, Player(100, 440), Ground(), sign, door), SceneOf("Room 2", 960, 540, Player(50, 440), Ground()));
            var s = new Session(p, NewDir());
            s.Run(0.3);
            s.Tap("e");
            s.Run(0.1);
            True(s.World.Dialogue != null && s.World.Dialogue.Text == "Hello there.", "E near the sign says its first line");
            s.Run(1.5);
            s.Tap("enter");
            s.Run(0.1);
            True(s.World.Dialogue?.Text == "Second line.", "Enter shows the next line");
            s.Run(1.0);
            s.Tap("enter");
            s.Run(0.1);
            True(s.World.Dialogue == null || s.World.Dialogue.Closed, "and closes it");
            s.Down("right"); s.Run(2.5); s.Up("right");
            Eq("Room 2", s.Scenes[^1], "walking into the door goes to Room 2");
            Eq(0, s.AllErrors().Count, string.Join(" | ", s.AllErrors()));
        });

        test("compat: cutscene: its steps run in order and it ends (lock, bars, say, move, camera, wait, variable)", () =>
        {
            string steps = "{\"steps\":[{\"do\":\"say\",\"text\":\"Look!\"},{\"do\":\"move\",\"object\":\"Box\",\"x\":500,\"y\":100,\"time\":1},"
                + "{\"do\":\"camera\",\"object\":\"Box\",\"time\":0.5},{\"do\":\"wait\",\"time\":0.5},{\"do\":\"cameraback\",\"time\":0.3},"
                + "{\"do\":\"setvar\",\"var\":\"seen\",\"to\":1},{\"do\":\"say\",\"text\":\"Done.\",\"speaker\":\"Cultist\"}]}";
            var cut = Obj("Intro", 0, 0, 10, 10, Beh("Cutscene", new { once = 1, @lock = 1, bars = 1 }, new { trigger = "start", script = steps, camera = "follow" }));
            var box = Obj("Box", 100, 100, 30, 30);
            var p = ProjectOf(SceneOf("Level", 1600, 540, Player(100, 440, Beh("FollowCamera")), Ground(), cut, box));
            var s = new Session(p, NewDir());
            s.Run(0.2);
            True(s.World.ControlsLocked, "the player is locked");
            True(s.World.BarsWanted, "cinema bars are on");
            Eq("Look!", s.World.Dialogue?.Text, "the first line");
            s.Run(1.2); s.Tap("enter"); s.Run(1.5);
            var b = s.Find("Box");
            True(Math.Abs(b.X - 500) < 0.5, $"the box moved to 500 (x {b.X:0})");
            s.Run(1.5);
            Eq("Done.", s.World.Dialogue?.Text, "the last line");
            Eq("Cultist", s.World.Dialogue?.Speaker, "with its speaker's name");
            Eq(1.0, s.World.Globals["seen"], "the variable was set");
            s.Run(0.6); s.Tap("enter"); s.Run(0.6);
            True(!s.World.ControlsLocked, "the player is free again at the end");
            True(!s.World.BarsWanted, "the bars go away");
            True(s.Printed.Any(l => l.Contains("Cutscene 'Intro' finished")), "it says it finished");
            Eq(0, s.AllErrors().Count, string.Join(" | ", s.AllErrors()));
        });

        test("compat: custom rules: key press, timer, becomes, touch, blocks with several WHENs and DOs", () =>
        {
            string rules = "[{\"when\":\"keypress\",\"key\":\"K\",\"do\":\"addvar\",\"var\":\"presses\",\"amount\":1},"
                + "{\"when\":\"timer\",\"every\":0.5,\"do\":\"addvar\",\"var\":\"ticks\",\"amount\":1},"
                + "{\"when\":\"becomes\",\"var\":\"ticks\",\"op\":\">=\",\"value\":2,\"do\":\"setvar\",\"var\":\"two\",\"to\":1},"
                + "{\"when\":\"touch\",\"name\":\"Coin\",\"do\":\"destroy\",\"target\":\"other\"},"
                + "{\"when\":\"keypress\",\"key\":\"J\",\"block\":1},{\"when\":\"ifvar\",\"var\":\"presses\",\"op\":\">=\",\"value\":3,\"do\":\"setvar\",\"var\":\"a\",\"to\":5,\"block\":1},"
                + "{\"when\":\"keypress\",\"key\":\"L\",\"do\":\"addvar\",\"var\":\"b\",\"amount\":1,\"block\":1}]";
            var player = Player(100, 440, Beh("Custom", null, new { script = rules }));
            var coin = Obj("Coin", 150, 460, 20, 20);
            var p = ProjectOf(SceneOf("Level", 960, 540, player, Ground(), coin));
            var s = new Session(p, NewDir());
            s.Run(0.1);
            s.Tap("k"); s.Run(0.4); s.Tap("k"); s.Run(0.4);
            Eq(2.0, s.World.Globals["presses"], "a key press adds once (with its cooldown)");
            s.Run(1.2);
            True(s.World.Globals["ticks"] is double t && t >= 3, "the timer fires every 0.5 s");
            Eq(1.0, s.World.Globals["two"], "'becomes' fired when ticks reached 2");
            s.Down("right"); s.Run(0.4); s.Up("right");
            True(s.World.Objects.All(o => o.Name != "Coin"), "touching the coin destroyed it (destroy other)");
            True(!s.World.Globals.ContainsKey("a") || s.World.Globals["a"] is 0.0, "the block hasn't fired yet");
            s.Tap("k"); s.Run(0.5);
            Eq(5.0, s.World.Globals["a"], "a block fires when any of its WHENs does (presses >= 3)");
            Eq(0, s.AllErrors().Count, string.Join(" | ", s.AllErrors()));
        });

        test("compat: stay inside clamps to the scene; follow camera follows and stays in the scene", () =>
        {
            var p = ProjectOf(SceneOf("Level", 2400, 540, Player(100, 440, Beh("StayInside", null, new { area = "scene", sides = "all", edge = "stop" }), Beh("FollowCamera", new { smooth = 0.12, clamp = 1 })), Ground(500, 2400)));
            var s = new Session(p, NewDir());
            s.Run(0.3);
            var pl = s.Find("Player");
            s.Down("left"); s.Run(1); s.Up("left");
            True(pl.X >= -0.01 && pl.X < 1, $"stopped at the left edge (x {pl.X:0.0})");
            True(Math.Abs(s.World.Camera.X - 480) < 1, $"the camera stays inside the scene (centre {s.World.Camera.X:0})");
            s.Down("right"); s.Run(4); s.Up("right");
            s.Run(1);
            True(Math.Abs(s.World.Camera.X - (pl.X + pl.Width / 2)) < 30, $"the camera follows the player (camera {s.World.Camera.X:0}, player {pl.X:0})");
        });

        test("compat: a Label shows a variable; a Bar fills with one", () =>
        {
            var label = Obj("Score", 20, 20, 200, 40, Beh("Label", new { fontSize = 20 }, new { text = "Coins: {coins} / {item:gem}" }));
            var rules = Obj("Rules", 0, 0, 10, 10, Beh("Custom", null, new { script = "[{\"when\":\"start\",\"do\":\"setvar\",\"var\":\"coins\",\"to\":7},{\"when\":\"start\",\"do\":\"additem\",\"item\":\"gem\",\"amount\":2}]" }));
            var p = ProjectOf(SceneOf("Level", 960, 540, label, rules));
            var s = new Session(p, NewDir());
            s.Run(0.2);
            Eq("Coins: 7 / 2", s.Find("Score").Caption, "the label shows the variables");
        });

        test("compat: collectible, checkpoint, chase, shooter, spawner, area, gate, switch, top-down", () =>
        {
            var coin = Obj("Coin", 200, 460, 20, 20, Beh("Collectible", new { points = 10 }, new { collector = "Player", var = "score" }));
            var flag = Obj("Flag", 300, 440, 20, 60, Beh("Checkpoint", null, new { target = "Player" }));
            var gate = Obj("Gate", 700, 300, 30, 200, Beh("Gate", new { value = 1 }, new { var = "open", op = ">=" }));
            var zone = Obj("Zone", 400, 400, 60, 100, Beh("Area", new { to = 1, once = 1 }, new { action = "setvar", var = "open", target = "Player" }));
            var hunter = Obj("Hunter", 1200, 300, 30, 30, Beh("Chase", new { speed = 100 }, new { target = "Player" }));
            var turret = Obj("Turret", 1500, 100, 30, 30, Beh("Shooter", new { directions = 4, speed = 200, cooldown = 0.5, life = 1 }, new { key = "", bullet = "Nothing here" }));
            var nest = Obj("Nest", 1800, 100, 30, 30, Beh("Spawner", new { interval = 0.5, max = 3 }, new { target = "Coin", at = "self" }));
            var p = ProjectOf(SceneOf("Level", 2000, 540, Player(100, 440), Ground(), coin, flag, gate, zone, hunter, turret, nest));
            var s = new Session(p, NewDir());
            s.Run(0.2);
            s.Down("right"); s.Run(1.6); s.Up("right");
            Eq(10.0, s.World.Globals["score"], "the coin added its points");
            Eq(1.0, s.World.Globals["open"], "walking into the zone set its variable");
            s.Run(0.2);
            True(!s.Find("Gate").Solid, "the gate opened (not solid any more)");
            True(s.Find("Hunter").X < 1150, "the chaser comes after the player");
            True(s.World.Objects.Count(o => o.Name == "Bullet") > 0, "the turret fires by itself");
            True(s.World.Objects.Count(o => o.Name == "Coin") >= 2, "the spawner makes copies of a scene object");
            Eq(0, s.AllErrors().Count, string.Join(" | ", s.AllErrors()));

            var td = ProjectOf(SceneOf("Top", 960, 540, Obj("Player", 100, 100, 30, 30, Beh("TopDown", new { speed = 200 })), Obj("Wall", 200, 0, 30, 540, Beh("Solid"))));
            var t = new Session(td, NewDir());
            t.Run(0.1);
            var pl = t.Find("Player");
            t.Down("down"); t.Run(0.5); t.Up("down");
            True(Math.Abs(pl.Y - 200) < 6, $"top-down walks down at its speed, no gravity (y {pl.Y:0})");
            t.Down("right"); t.Run(1); t.Up("right");
            True(pl.X + pl.Width <= 200.5, $"and stops at walls (right {pl.X + pl.Width:0})");
        });

        test("compat: engine additions — camera size / smooth / hold, cinema bars, self.angle, a speaker by name", () =>
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load("mechanism T\n    when start\n        camera size 960, 540\n        camera smooth 0\n        bars on in 0.2\n        self.angle = 30\n        say \"Old man\" \"Hello\"\n        camera hold\n        bars off\n");
            w.ResizeScene(2000, 1000);
            var o = w.Spawn("T", 100, 100, ["T"]);
            w.Frame(Dt);
            Eq(960.0, w.Camera.BaseWidth, "camera size");
            Eq(0.0, w.Camera.Smooth, "camera smooth");
            Eq(30.0, o.Angle, "self.angle");
            Eq("Old man", w.Dialogue?.Speaker, "a speaker that is just a name");
            w.RunFor(0.3);
            Eq(1.0, w.BarsAmount, "the bars slid in");
            w.Click(); w.Click();
            w.RunFor(0.1);
            True(!w.Camera.Auto && w.Camera.Target == null, "camera hold: it follows nothing");
            True(!w.BarsWanted, "bars off");
            Eq(960.0, w.ForNextScene().ViewMaxWidth, "the view size goes on to the next scene");
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
        });

        test("compat: the support table names every behavior the catalog has", () =>
        {
            foreach (var k in new[] { "Platformer", "Cutscene", "Custom", "Interact", "GoToScene", "BossAI", "Lives" })
                True(EasyCompat.Table.Any(t => t.Kind == k), k + " is in the table");
            Eq(EasyCompat.Level.No, EasyCompat.SupportOf("SomethingNew").Level, "an unknown kind doesn't run");
        });

        test("compat: an unsupported behavior is said once, not as an error", () =>
        {
            var boss = Obj("Boss", 400, 300, 80, 80, Beh("BossAI"));
            var boss2 = Obj("Boss 2", 600, 300, 80, 80, Beh("BossAI"));
            var p = ProjectOf(SceneOf("Level", 960, 540, boss, boss2));
            var s = new Session(p, NewDir());
            s.Run(0.2);
            Eq(0, s.AllErrors().Count, string.Join(" | ", s.AllErrors()));
            Eq(1, s.Printed.Count(l => l.Contains("not supported yet: Boss AI")), "said once");
        });

        MistifyTests(test);
        foreach (var d in Made) try { Directory.Delete(d, true); } catch { }
        Made.Clear();
    }

    // ---------------- The owner's game ----------------

    static string? MistifyFolder()
    {
        string? env = Environment.GetEnvironmentVariable("BE_MISTIFY");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Jogos", "Mistify");
        return File.Exists(Path.Combine(d, "Mistify.eeproj")) ? d : null;
    }

    /// <summary>A copy of Mistify's .eeproj and pictures (not its big sounds) in a temp folder.</summary>
    static string CopyMistify(string from)
    {
        string d = NewDir();
        foreach (var f in Directory.GetFiles(from, "*.eeproj")) File.Copy(f, Path.Combine(d, Path.GetFileName(f)));
        string sprites = Path.Combine(from, "Assets", "sprites");
        if (Directory.Exists(sprites))
            foreach (var f in Directory.GetFiles(sprites, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(d, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
        return d;
    }

    /// <summary>The scene with this object (the tests don't depend on the owner's scene names).</summary>
    static string SceneWith(Project p, string objectName) =>
        p.Scenes.FirstOrDefault(s => s.Objects.Any(o => o.Name == objectName))?.Name ?? throw new Exception($"no scene has '{objectName}'");

    static void MistifyTests(Action<string, Action> test)
    {
        string? from = MistifyFolder();
        if (from == null) { Console.WriteLine("  (Mistify isn't on this computer: its tests are skipped)"); return; }

        test("compat: Mistify: every scene plays 6 s with the player walking, with no script errors", () =>
        {
            string d = CopyMistify(from);
            try
            {
                var p = EasyLink.FromEeproj(File.ReadAllText(Directory.GetFiles(d, "*.eeproj")[0]), d);
                foreach (var sc in p.Scenes)
                {
                    var s = new Session(p, d, sc.Name);
                    s.Run(6, t =>
                    {
                        if (t < 2) s.World.KeysDown.Add("right"); else s.World.KeysDown.Remove("right");
                        if ((int)(t * 4) % 4 == 0) s.World.Click(); // move dialogues on
                    });
                    Eq(0, s.AllErrors().Count, $"{sc.Name}: " + string.Join(" | ", s.AllErrors()));
                }
            }
            finally { try { Directory.Delete(d, true); } catch { } }
        });

        test("compat: Mistify: the bedroom's interacts talk, and the music box toggles its music", () =>
        {
            string d = CopyMistify(from);
            try
            {
                var p = EasyLink.FromEeproj(File.ReadAllText(Directory.GetFiles(d, "*.eeproj")[0]), d);
                string wanted = p.Easy?["StartScene"]?.GetValue<string>() ?? "";
                Eq(p.Scenes.Any(sc => sc.Name == wanted) ? wanted : p.Scenes[0].Name, EasyCompat.FirstScene(p).Name, "the game starts in EasyEngine's start scene (the first one when it names none)");
                var s = new Session(p, d, SceneWith(p, "musicbox"));
                s.Run(0.5);
                var pl = s.Find("Player");
                var box = s.Find("musicbox");
                pl.X = box.X + box.Width / 2 - pl.Width / 2;
                pl.Y = box.Y + box.Height / 2 - pl.Height / 2;
                s.Run(0.1);
                s.Tap("e");
                s.Run(0.2);
                Eq(1.0, s.World.Globals["musicIsPlaying"], "E at the music box turns the music on");
                True(s.Sounds.Any(x => x.Contains("Smells_Like")), "the song plays");
                s.Run(0.3);
                pl.X = box.X + box.Width / 2 - pl.Width / 2; // (no ground in this copy until its tile map is converted: it fell)
                pl.Y = box.Y + box.Height / 2 - pl.Height / 2;
                pl.Vy = 0;
                s.Tap("e");
                s.Run(0.2);
                Eq(0.0, s.World.Globals["musicIsPlaying"], "E again turns it off (one press, one Interact)");
                var bed = s.Find("bed");
                pl.X = bed.X + bed.Width / 2 - pl.Width / 2;
                pl.Y = bed.Y + bed.Height / 2 - pl.Height / 2;
                s.Run(0.1);
                s.Tap("e");
                s.Run(0.2);
                Eq("My bed.", s.World.Dialogue?.Text, "the bed talks");
                True(s.World.ControlsLocked, "and freezes the player while it talks");
                if (Environment.GetEnvironmentVariable("BE_COMPAT_DUMP") is { Length: > 0 } dump)
                    foreach (var (name, src) in s.World.Easy!.Generated) File.WriteAllText(Path.Combine(dump, name + ".bs"), src);
                Eq(0, s.AllErrors().Count, string.Join(" | ", s.AllErrors()));
            }
            finally { try { Directory.Delete(d, true); } catch { } }
        });

        test("compat: Mistify: the outdoor and forest cutscenes play to their end and go to the next scene", () =>
        {
            string d = CopyMistify(from);
            try
            {
                var p = EasyLink.FromEeproj(File.ReadAllText(Directory.GetFiles(d, "*.eeproj")[0]), d);
                var s = new Session(p, d, SceneWith(p, "Object_3_2"));
                s.Run(0.3);
                var pl = s.Find("Player");
                var tree = s.Find("Object_3_2");
                pl.X = tree.X + 20; // walks into the tree with the cutscene
                s.Run(0.5);
                True(s.World.ControlsLocked && s.World.BarsWanted, "the outdoor cutscene started (locked, bars)");
                s.Run(12, t => { if ((int)(t * 2) % 2 == 0) s.World.Click(); });
                True(s.Scenes.Contains(SceneWith(p, "Custcene")), "it went to the forest — scenes: " + string.Join(", ", s.Scenes));
                var p2 = s.Find("Player");
                var c = s.Find("Custcene");
                p2.X = c.X + 10; // (no ground in this copy until its tile map is converted: put it right in the trigger)
                p2.Y = c.Y + 10;
                p2.Vy = 0;
                s.Run(8, t => { if ((int)(t * 2) % 2 == 0) s.World.Click(); });
                True(s.Scenes.Contains(SceneWith(p, "animator")), "the forest cutscene went to the cult — scenes: " + string.Join(", ", s.Scenes));
                s.Run(20, t => { if ((int)(t * 2) % 2 == 0) s.World.Click(); });
                True(s.Printed.Any(l => l.Contains("Cutscene 'animator' finished")), "the cult cutscene reached its end:\n" + string.Join("\n", s.Printed.TakeLast(12)));
                Eq(0, s.AllErrors().Count, string.Join(" | ", s.AllErrors()));
                foreach (var u in s.World.Easy!.Unsupported) Console.WriteLine("       (not supported yet: " + u + ")");
                if (Environment.GetEnvironmentVariable("BE_COMPAT_DUMP") is { Length: > 0 } dump) // dev: the Builder Script made from its rules and cutscenes
                    foreach (var (name, src) in s.World.Easy.Generated) File.WriteAllText(Path.Combine(dump, name + ".bs"), src);
            }
            finally { try { Directory.Delete(d, true); } catch { } }
        });
    }
}
