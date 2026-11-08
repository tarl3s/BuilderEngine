using System.Diagnostics;
using BuilderEngine.Script;
using BuilderEngine.Script.Samples;

/// <summary>
/// The "Hollow Lantern" sample (a top-down mining survival game in Builder Script) and the engine pieces it added:
/// pathfinding (chase, path_next, can_see), angles (sin, cos, angle, overlaps…), the minimap, wider bars.
/// The sample is built into a temp folder (BE_Test_*), played headless with scripted input, then deleted.
/// </summary>
static class HollowLanternTests
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

    static void NoErrors(World w) => Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));

    static string M(string body, string name = "T") => $"mechanism {name}\n" + string.Join("\n", body.Split('\n').Select(l => "    " + l)) + "\n";

    /// <summary>A small world with a 20 × 12 map of 32-px cells and a wall with one gap (for pathfinding).</summary>
    static World MazeWorld(string src)
    {
        var w = new World();
        StarterKit.LoadInto(w);
        w.Load(M("when start\n    tile_kind \"rock\" with \"#777777\"\n    make_map 20, 12 with 32\n    fill_tiles \"rock\" from 320, 0 to 351, 300", "Maze") + src);
        w.Spawn("maze", 0, 0, ["Maze"]);
        w.Frame(Dt);
        return w;
    }

    /// <summary>The sample loaded into a fresh world, the way the editor plays it.</summary>
    sealed class Game
    {
        public readonly World W;
        public Game(string folder, bool intro = false, Action<Scene>? edit = null)
        {
            var p = Project.Load(folder);
            W = new World();
            StarterKit.LoadInto(W);
            foreach (var (_, src) in p.Mechanisms) W.Load(src);
            W.ProjectFolder = folder;
            W.SaveFolder = Path.Combine(folder, Project.SavesFolder);
            W.UseScenes(p.Scenes.Select(s => s.Name));
            var scene = p.Scenes[0].Clone();
            scene.Objects.First(o => o.Name == "Game").Mechanisms[0].Params["intro"] = intro;
            edit?.Invoke(scene);
            W.LoadScene(scene);
            W.Frame(Dt);
        }
        public GameObject Player => W.FindObject("player")!;
        public GameObject Obj(string name) => W.FindObject(name)!;
        public void Run(double seconds) => W.RunFor(seconds);
        public static object? Var(GameObject o, string mech, string name) =>
            o.AllMechanisms().First(m => string.Equals(m.Def.Name, mech, StringComparison.OrdinalIgnoreCase)).Vars.GetValueOrDefault(name);
        public double Num(GameObject o, string mech, string name) => World.ToNumber(Var(o, mech, name), 0);
        public void Tap(string key) { W.KeyDown(key); W.Frame(Dt); W.KeyUp(key); W.Frame(Dt); }
        public (int Col, int Row) CellOf(double x, double y) => W.Tiles.Main!.CellAt(x, y);
        public void SetCell(double x, double y, string kind)
        {
            var l = W.Tiles.Main!;
            var (c, r) = l.CellAt(x, y);
            l.Set(c, r, (ushort)W.Tiles.Id(kind));
        }
        public string TileAt(double x, double y) => (string)W.Functions["tile_at"]([x, y])!;
        /// <summary>Empties a box of cells (scene pixels) so a test has room.</summary>
        public void Clear(double x0, double y0, double x1, double y1)
        {
            var l = W.Tiles.Main!;
            var (c0, r0) = l.CellAt(x0, y0);
            var (c1, r1) = l.CellAt(x1, y1);
            for (int r = r0; r <= r1; r++) for (int c = c0; c <= c1; c++) l.Set(c, r, 0);
        }
        public void MouseAt(double x, double y)
        {
            var cam = W.Camera;
            W.MouseMove((x - cam.Left) * cam.Zoom, (y - cam.Top) * cam.Zoom);
        }
        /// <summary>Closes dialogue lines (the intro, the Warden's words) by pressing space.</summary>
        public void SkipDialogue(int max = 20)
        {
            for (int i = 0; i < max && W.Dialogue is { Closed: false }; i++) { Tap("space"); Tap("space"); }
        }
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Hollow Lantern (sample game) and its engine pieces");
        string folder = Path.Combine(Path.GetTempPath(), "BE_Test_Hollow_" + Guid.NewGuid().ToString("N")[..8]);
        try { RunAll(test, folder); }
        finally { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { } }
    }

    static void RunAll(Action<string, Action> test, string folder)
    {
        test("the sample writes a project folder with mechanisms, pictures and sounds", () =>
        {
            SampleGames.Make("Hollow Lantern", folder);
            var p = Project.Load(folder);
            Eq("Hollow Lantern", p.Name);
            Eq("Cavern", p.Scenes[0].Name);
            True(p.Mechanisms.Count >= 13, $"{p.Mechanisms.Count} mechanisms");
            foreach (var (name, src) in p.Mechanisms)
            {
                var defs = Parser.Parse(src);
                Eq(name, defs[0].Name, "file name = mechanism name");
            }
            foreach (var pic in new[] { "wick.png", "gloomling.png", "warden.png", "stone.png", "emberite.png", "workbench.png" })
                True(Images.Size(Path.Combine(folder, "images", pic)) is { Width: > 0 }, pic + " is a picture");
            foreach (var snd in new[] { "dig.wav", "roar.wav", "cave_drone.wav", "coin.wav" })
                True(File.Exists(Path.Combine(folder, "sounds", snd)), snd);
        });

        test("Hollow Lantern: plays without script errors and digs out the cave", () =>
        {
            var g = new Game(folder);
            g.Run(2);
            NoErrors(g.W);
            var main = g.W.Tiles.Main!;
            Eq(80, main.Columns); Eq(80, main.Rows);
            var counts = new Dictionary<string, int>();
            foreach (var id in main.Cells) { string k = g.W.Tiles.Kind(id)?.Name ?? ""; counts[k] = counts.GetValueOrDefault(k) + 1; }
            foreach (var k in new[] { "", "dirt", "stone", "coal", "copper", "emberite", "skyglass", "deepstone", "bedrock" })
                True(counts.GetValueOrDefault(k) > 0, $"some '{k}' cells ({string.Join(", ", counts.Select(c => c.Key + "=" + c.Value))})");
            var p = g.Player;
            Eq("", g.TileAt(p.X + 11, p.Y + 11), "the player wakes in a clearing");
            Eq("", g.TileAt(1280, 1280), "the Warden's hall is open");
            Eq("deepstone", g.TileAt(1280 + 7 * 32, 1280 + 16), "a ring of deepstone around the hall");
            True(g.W.FindObject("glowcap") != null, "glowcaps were placed");
        });

        test("Hollow Lantern: the intro talks, then the player can walk", () =>
        {
            var g = new Game(folder, intro: true);
            g.Run(0.5);
            True(g.W.Dialogue is { Closed: false }, "the intro is on screen");
            True(g.W.ControlsLocked, "controls are locked during the intro");
            g.SkipDialogue();
            g.Run(0.2);
            True(!g.W.ControlsLocked, "controls come back after the intro");
            double x0 = g.Player.X;
            g.W.KeyDown("d"); g.Run(0.4); g.W.KeyUp("d");
            True(g.Player.X > x0 + 30, $"the player walked right ({x0} → {g.Player.X})");
            NoErrors(g.W);
        });

        test("Hollow Lantern: darkness, the lantern, the workbench candle and glowing ore", () =>
        {
            var g = new Game(folder);
            g.Run(0.3);
            True(g.W.Darkness > 0.9, $"darkness {g.W.Darkness}");
            var p = g.Player;
            True(p.LightRadius >= 150, $"the lantern ({p.LightRadius})");
            True(g.W.LightAt(p.X + 11, p.Y + 11) > 0.8, "lit around the player");
            True(g.W.LightAt(p.X + 600, p.Y + 300) < 0.15, "dark far away");
            True(g.Obj("workbench").LightRadius > 0, "a candle on the workbench");
            True(g.W.Tiles.Kinds.Any(k => k.Name == "emberite" && k.Light > 0) && g.W.Tiles.Kinds.Any(k => k.Name == "skyglass" && k.Light > 0), "emberite and skyglass glow");
            True(g.W.Minimap.Visible && g.W.Minimap.Fog, "a minimap with fog");
            True(g.W.MinimapSeen(p.X, p.Y) && !g.W.MinimapSeen(1280, 1280), "the lantern reveals the minimap around the player only");
            True(g.W.Hud.Any(h => h.IsBar && h.Id.Contains("hunger")) && g.W.Hud.Any(h => h.IsBar && h.Id.Contains("Health")), "health and hunger bars");
            True(g.W.Hotbar.Visible, "the hotbar");
        });

        test("Hollow Lantern: walking into a wall digs it; holding the mouse digs; ore goes into the inventory", () =>
        {
            var g = new Game(folder);
            g.Run(0.2);
            var p = g.Player;
            p.X = Math.Floor((p.X + 11) / 32) * 32 + 5; p.Y = Math.Floor((p.Y + 11) / 32) * 32 + 5; // in the middle of a cell
            double cx = p.X + 11, cy = p.Y + 11;
            g.Clear(cx - 64, cy - 96, cx + 64, cy + 96);
            g.SetCell(cx + 32, cy, "copper");
            g.W.KeyDown("d"); g.Run(2.0); g.W.KeyUp("d");
            Eq("", g.TileAt(cx + 32, cy), "the copper in front was dug");
            Eq(1, g.W.Inventory.FirstOrDefault(s => s.Name == "copper")?.Count ?? 0, "one copper");
            // The mouse, two cells up.
            p.X = cx - 11; p.Y = cy - 11; p.Vx = p.Vy = 0;
            g.SetCell(cx, cy - 64, "stone");
            g.Run(0.1);
            g.MouseAt(cx, cy - 64);
            g.W.MouseDown(); g.Run(1.5); g.W.MouseUp();
            Eq("", g.TileAt(cx, cy - 64), "the stone under the mouse was dug");
            True((g.W.Inventory.FirstOrDefault(s => s.Name == "stone")?.Count ?? 0) >= 1, "stone in the inventory");
            // Too far away: nothing happens.
            g.SetCell(cx + 200, cy, "stone");
            g.MouseAt(cx + 200, cy);
            g.W.MouseDown(); g.Run(1.5); g.W.MouseUp();
            Eq("stone", g.TileAt(cx + 200, cy), "out of reach");
            // Deepstone needs a better pick.
            g.SetCell(cx, cy + 64, "deepstone");
            g.MouseAt(cx, cy + 64);
            g.W.MouseDown(); g.Run(1.5); g.W.MouseUp();
            Eq("deepstone", g.TileAt(cx, cy + 64), "the Old Pick can't break deepstone");
            // A glowcap is picked up by walking over it.
            g.W.Load("mechanism HL_Drop\n    when start\n        make Glowcap at self.x, self.y with 16, 16\n");
            g.W.Spawn("drop", p.X + 2, p.Y + 2, ["HL_Drop"]);
            g.Run(0.3);
            Eq(1, g.W.Inventory.FirstOrDefault(s => s.Name == "glowcap")?.Count ?? 0, "picked a glowcap");
            NoErrors(g.W);
        });

        test("Hollow Lantern: crafting at the workbench (menu, click, better pick), eating, torches", () =>
        {
            var g = new Game(folder);
            g.Run(0.2);
            var p = g.Player;
            var bench = g.Obj("workbench");
            g.W.Give("copper", 6); g.W.Give("stone", 4); g.W.Give("coal", 2); g.W.Give("glowcap", 3);
            g.Tap("c");
            True(g.W.Objects.Count(o => o.Ui && o.Mechanisms.Any(m => m.Def.Name == "Button")) >= 5, "the crafting menu opened");
            // Click the "copper pick" button.
            var btn = g.W.Objects.First(o => o.Ui && o.Mechanisms.FirstOrDefault(m => m.Def.Name == "Button") is { } b && Equals(b.Vars["value"], "copper pick"));
            g.W.MouseMove(btn.X + btn.Width / 2, btn.Y + btn.Height / 2);
            g.W.MouseDown(); g.W.Frame(Dt); g.W.MouseUp(); g.Run(0.1);
            True(g.W.CountItem("copper pick") > 0, "crafted a copper pick");
            Eq(0, g.W.CountItem("copper"), "copper used");
            g.Run(0.1);
            Eq(2.0, g.Num(p, "Pickaxe", "power"), "the copper pick digs at power 2");
            g.W.Tell(bench, "craft", ["torch"]); g.Run(0.1);
            Eq(3, g.W.CountItem("torch"), "three torches from one coal");
            // Walking away closes the menu.
            p.X += 300; g.Run(0.2);
            Eq(0, g.W.Objects.Count(o => o.Ui && o.Mechanisms.Any(m => m.Def.Name == "Button")), "the menu closed");
            // Eating.
            p.Mechanisms[0].Vars["hunger"] = 40.0;
            g.Tap("f");
            True(g.Num(p, "Wick", "hunger") > 50, "a glowcap fills the belly a little");
            // A torch: right-click next to the player.
            p.X -= 300; g.Run(0.1);
            g.Clear(p.X - 64, p.Y - 64, p.X + 96, p.Y + 96);
            g.MouseAt(p.X + 50, p.Y + 11);
            int torches = g.W.Objects.Count(o => o.Is("torch"));
            g.W.MouseDown("right"); g.W.Frame(Dt); g.W.MouseUp("right"); g.Run(0.1);
            Eq(torches + 1, g.W.Objects.Count(o => o.Is("torch")), "a torch was placed");
            Eq(2, g.W.CountItem("torch"), "one torch used");
            NoErrors(g.W);
        });

        test("Hollow Lantern: gloomlings hunt the player round walls, bite, and flee from torches", () =>
        {
            var g = new Game(folder, edit: s => s.Objects.RemoveAll(o => o.Name is "Spawner" or "Torch"));
            g.Run(0.2);
            var p = g.Player;
            double px = p.X, py = p.Y;
            // An open room with a wall between the creature and the player, and a gap at the bottom.
            g.Clear(px - 64, py - 160, px + 300, py + 128);
            for (double y = py - 160; y <= py + 64; y += 32) g.SetCell(px + 120, y, "bedrock");
            g.W.Load("mechanism HL_Spawn\n    when start\n        make Gloomling at self.x, self.y with 22, 22\n");
            g.W.Spawn("s", px + 220, py, ["HL_Spawn"]);
            g.Run(0.1);
            var gl = g.W.FindObject("gloomling")!;
            gl.Mechanisms[0].Vars["hunting"] = true; // it heard the player
            double hp0 = g.Num(p, "Health", "hp");
            double best = double.MaxValue;
            for (int i = 0; i < 60 * 8 && !gl.Destroyed; i++)
            {
                g.W.Frame(Dt);
                p.X = px; p.Y = py;
                best = Math.Min(best, Math.Abs(gl.X - p.X) + Math.Abs(gl.Y - p.Y));
                if (g.Num(p, "Health", "hp") < hp0) break;
            }
            True(g.W.NavSearches > 0, "it searched for a way");
            True(g.Num(p, "Health", "hp") < hp0, $"it went round the wall and bit the player (closest {best:0})");
            // A torch scares it away.
            g.W.Load("mechanism HL_Torch\n    when start\n        make Torch at self.x, self.y with 16, 20\n");
            g.W.Spawn("t", p.X, p.Y, ["HL_Torch"]);
            g.Run(2.5);
            True(g.W.Objects.Any(o => o.Is("torch")), "the torch is there");
            True(Math.Sqrt(Math.Pow(gl.X - p.X, 2) + Math.Pow(gl.Y - p.Y, 2)) > 80, "it keeps away from the torch");
            // Swinging kills it (3 hits with the Old Pick).
            p.Mechanisms[0].Vars["hunger"] = 100.0;
            gl.X = p.X + 30; gl.Y = p.Y;
            gl.Mechanisms[0].Vars["stun_until"] = g.W.Time + 100; // hold it still for the test
            for (int i = 0; i < 6 && !gl.Destroyed; i++) { gl.X = p.X + 30; gl.Y = p.Y; g.Tap("space"); g.Run(0.4); }
            True(gl.Destroyed, "three swings kill a gloomling");
            NoErrors(g.W);
        });

        test("Hollow Lantern: the Lurkers bring creatures out of the dark, away from the player", () =>
        {
            var g = new Game(folder);
            g.Run(12);
            var lurkers = g.W.Objects.Where(o => o.Is("gloomling")).ToList();
            True(lurkers.Count >= 1, "creatures appeared");
            NoErrors(g.W);
        });

        test("Hollow Lantern: the Murk Warden wakes, fights in three phases and can be beaten", () =>
        {
            var g = new Game(folder, edit: s => s.Objects.RemoveAll(o => o.Name == "Spawner"));
            g.Run(0.2);
            var p = g.Player;
            var boss = g.Obj("Warden");
            g.W.Give("ember pick", 1);
            // Into the hall, near the sleeping Warden.
            p.X = 1280 - 200; p.Y = 1280 - 11;
            g.Run(0.5);
            True(g.W.Dialogue != null, "the Warden speaks when it wakes");
            g.SkipDialogue();
            g.Run(0.2);
            True(g.Num(boss, "Warden", "phase") >= 1, "awake");
            True(g.W.Hud.Any(h => h.IsBar && h.Width >= 400), "a wide boss health bar");
            double maxPhase = 0;
            bool sawSlam = false, sawShard = false, sawDim = false;
            for (int i = 0; i < 60 * 120 && !boss.Destroyed; i++)
            {
                // Stay next to it, swing, and eat now and then (a scripted fight).
                if (i % 2 == 0) { p.X = boss.X - 24; p.Y = boss.Y + 20; }
                if (i % 24 == 0) { g.W.KeyDown("space"); }
                if (i % 24 == 1) { g.W.KeyUp("space"); }
                if (i % 60 == 0) g.W.Tell(p, "heal", [10.0]);
                g.W.Frame(Dt);
                maxPhase = Math.Max(maxPhase, g.Num(boss, "Warden", "phase"));
                sawSlam |= g.W.Objects.Any(o => o.Name == "SlamMark");
                sawShard |= g.W.Objects.Any(o => o.Name == "Shard");
                sawDim |= Equals(Game.Var(p, "Wick", "dimmed"), true);
            }
            True(sawSlam, "red slam marks");
            True(sawShard, "shard rings (phase 2)");
            Eq(3.0, maxPhase, "reached phase 3");
            True(sawDim, "it drank the lantern's light (phase 3)");
            True(boss.Destroyed, $"the Warden fell (hp {g.Num(boss, "Warden", "hp")})");
            g.Run(6);
            Eq(true, Game.Var(g.Obj("Game"), "HollowGame", "won"), "the game is won");
            True(g.W.Darkness < 0.5, $"the light comes back ({g.W.Darkness:0.00})");
            True(g.W.Hud.Any(h => h.Text.Contains("THE HOLLOW IS LIT")), "the win text");
            Eq(0, g.W.Objects.Count(o => o.Is("gloomling")), "the shadows faded");
            NoErrors(g.W);
        });

        test("Hollow Lantern: losing all health starts the cave over", () =>
        {
            var g = new Game(folder);
            string? asked = null;
            g.W.SceneRequested += n => asked = n;
            g.Run(0.2);
            g.W.Tell(g.Player, "damage", [99.0]);
            g.Run(2);
            g.SkipDialogue();
            g.Run(0.2);
            Eq("Cavern", asked, "back to the start of the cave");
            NoErrors(g.W);
        });

        test("Hollow Lantern: a frame with creatures, lights and the 80 × 80 cave stays fast", () =>
        {
            var g = new Game(folder);
            g.Run(0.5);
            g.W.Load("mechanism HL_Many\n    when start\n        repeat 10\n            make Gloomling at self.x + random(-200, 200), self.y + random(-200, 200) with 22, 22\n");
            g.W.Spawn("m", g.Player.X, g.Player.Y, ["HL_Many"]);
            g.Run(0.5);
            var buf = new LightBuffer();
            buf.Resize(200, 113);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 240; i++)
            {
                g.W.Frame(Dt);
                g.W.ComputeLighting(buf, g.W.Camera.Left, g.W.Camera.Top, g.W.Camera.ViewWidth, g.W.Camera.ViewHeight);
                g.W.MinimapImage(out _, out _);
            }
            double ms = sw.Elapsed.TotalMilliseconds / 240;
            Console.WriteLine($"         ({ms:0.00} ms per frame with {g.W.Objects.Count} objects)");
            True(ms < 8, $"{ms:0.00} ms per frame");
            NoErrors(g.W);
        });

        // ---------------- Engine pieces ----------------

        test("chase finds the way round a wall; path_next and can_see", () =>
        {
            var w = MazeWorld(M("when start\n    body 0, 0\nwhen tick\n    chase find(\"goal\") at 120\n    seen = can_see(find(\"goal\"))\n    next = path_next(find(\"goal\"))", "Hunter"));
            var goal = w.Spawn("goal", 520, 100, null, null, "goal");
            goal.Width = goal.Height = 20;
            var h = w.Spawn("hunter", 140, 100, ["Hunter"]);
            h.Width = h.Height = 22;
            w.Frame(Dt);
            Eq(false, h.Mechanisms[0].Vars["seen"], "the wall hides the goal");
            var next = (List<object?>)h.Mechanisms[0].Vars["next"]!;
            True(World.ToNumber(next[1], 0) > 100, "the way goes down to the gap first");
            w.RunFor(6);
            True(Math.Abs(h.X - goal.X) < 24 && Math.Abs(h.Y - goal.Y) < 24, $"reached the goal ({h.X:0}, {h.Y:0})");
            Eq(true, h.Mechanisms[0].Vars["seen"], "and sees it now");
            NoErrors(w);
        });

        test("chase with no tiles goes straight; nothing to chase stands still", () =>
        {
            var w = new World();
            w.Load(M("when tick\n    chase find(\"goal\") at 100") + M("when told \"bye\"\n    destroy self", "Bye"));
            var goal = w.Spawn("goal", 300, 0, ["Bye"], null, "goal");
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.RunFor(1);
            True(o.X > 80 && Math.Abs(o.Y) < 1, $"moved straight ({o.X:0}, {o.Y:0})");
            w.Tell(goal, "bye", []);
            w.RunFor(0.2);
            Eq(0.0, o.Vx, "stands still");
            NoErrors(w);
        });

        test("sin, cos, angle, overlaps, clamp, random_number", () =>
        {
            var w = new World();
            w.Load(M("when start\n    a = round(sin(90) * 100)\n    b = round(cos(180) * 100)\n    c = angle(0, 0, 0, 10)\n    d = angle(self, find(\"o2\"))\n    e = overlaps(self, find(\"o2\"))\n    f = clamp(15, 0, 10)\n    g = random_number(1, 2)"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            var o2 = w.Spawn("o2", 10, 0);
            w.Frame(Dt);
            var v = o.Mechanisms[0].Vars;
            Eq(100.0, v["a"]); Eq(-100.0, v["b"]); Eq(90.0, v["c"]); Eq(0.0, v["d"]); Eq(true, v["e"]); Eq(10.0, v["f"]);
            True(World.ToNumber(v["g"], 0) is >= 1 and <= 2 && World.ToNumber(v["g"], 0) % 1 != 0, "a number with decimals");
            NoErrors(w);
        });

        test("minimap: shown with fog, revealed around an object, marks and its picture", () =>
        {
            var w = MazeWorld(M("when start\n    show minimap at 10, 10 with 100, 60 fog\n    minimap mark \"coin\" with \"#FFD700\"\n    minimap mark \"boss\" with \"#B07BFF\" always\nwhen tick\n    minimap reveal 3", "Map2"));
            var me = w.Spawn("me", 40, 40, ["Map2"], null, "player");
            w.Spawn("coin near", 60, 60, null, null, "coin");
            w.Spawn("coin far", 600, 300, null, null, "coin");
            w.Spawn("boss far", 600, 40, null, null, "boss");
            w.RunFor(0.1);
            var m = w.Minimap;
            True(m.Visible && m.Fog && m.Width == 100 && m.Height == 60, "shown where asked");
            True(w.MinimapSeen(50, 50) && !w.MinimapSeen(600, 300), "revealed only near the object");
            var dots = w.MinimapDots();
            Eq(3, dots.Count, "near coin, the boss (always) and the player");
            Eq("#FFFFFF", dots[^1].Color, "the player last");
            var px = w.MinimapImage(out int cols, out int rows)!;
            Eq(20, cols); Eq(12, rows);
            True(px[1 * cols + 1] != px[11 * cols + 19], "unrevealed cells are dark");
            int v0 = m.ImageVersion;
            w.MinimapImage(out _, out _);
            Eq(v0, m.ImageVersion, "the picture is kept while nothing changes");
            w.Load(M("when start\n    minimap reveal all", "All"));
            w.Spawn("all", 0, 0, ["All"]);
            w.RunFor(0.05);
            True(m.SeenFraction > 0.99, "reveal all");
            NoErrors(w);
        });

        test("starter kit: Hunter walks round a wall to what it saw; Minimap reveals around the player", () =>
        {
            var w = MazeWorld("");
            var prey = w.Spawn("prey", 140, 100, ["Minimap"], null, "player");
            prey.Width = prey.Height = 20;
            var h = w.Spawn("hunter", 240, 100, ["Hunter"]);
            h.Width = h.Height = 22;
            w.RunFor(0.2);
            Eq(true, h.Mechanisms[0].Vars["hunting"], "it saw the prey");
            prey.X = 520; // behind the wall now
            w.RunFor(10);
            True(Math.Abs(h.X - prey.X) < 26 && Math.Abs(h.Y - prey.Y) < 26, $"caught up round the wall ({h.X:0}, {h.Y:0})");
            True(w.Minimap.Visible && w.MinimapSeen(530, 110) && !w.MinimapSeen(20, 370), "the minimap shows where the player has been");
            NoErrors(w);
        });

        test("show bar … size: a wider, taller bar", () =>
        {
            var w = new World();
            w.Load(M("when start\n    hp = 5\n    show bar hp of 10 at 200, 380 with \"#9B6BFF\" size 400, 14"));
            w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            var bar = w.Hud.Single(h => h.IsBar);
            Eq(400.0, bar.Width); Eq(14.0, bar.Height); Eq("#9B6BFF", bar.Color); Eq(0.5, bar.Fraction);
            NoErrors(w);
        });
    }
}
