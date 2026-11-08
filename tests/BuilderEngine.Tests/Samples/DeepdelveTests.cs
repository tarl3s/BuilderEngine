using System.Diagnostics;
using BuilderEngine.Script;

/// <summary>
/// The Deepdelve sample (src/BuilderEngine.Script/Samples): built into a temp folder, loaded like the editor loads it
/// and played headless with scripted input — walk, chop, craft, place, dig, fight, survive a night, win. Called from Program.cs.
/// </summary>
static class DeepdelveTests
{
    const double Dt = 1.0 / 60;

    static void True(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    static bool Has(World w, string item) => w.CountItem(item) > 0;

    static void NoErrors(World w) => True(w.Errors.Count == 0, "script errors: " + string.Join(" | ", w.Errors));

    /// <summary>A world playing the sample's scene, the way the editor's Play does it (one frame run).</summary>
    static World Play(string dir, Action<Scene>? tweak = null)
    {
        var p = Project.Load(dir);
        var w = new World { ProjectFolder = dir };
        StarterKit.LoadInto(w);
        foreach (var (_, src) in p.Mechanisms) w.Load(src);
        w.Globals["score"] = 0.0;
        w.UseScenes(p.Scenes.Select(s => s.Name));
        var scene = p.Scenes[0];
        tweak?.Invoke(scene);
        w.LoadScene(scene);
        w.Frame(Dt);
        return w;
    }

    static void SetParam(Scene s, string obj, string param, object? value)
    {
        var o = s.Objects.First(x => x.Name == obj);
        o.Mechanisms[0].Params[param] = value;
    }

    static World Begin(World w)
    {
        w.Broadcast("begin", []);
        w.RunFor(1);
        return w;
    }

    static GameObject Player(World w) => w.FindObject("player")!;
    static MechanismInstance Miner(World w) => Player(w).Mechanisms.First(m => m.Def.Name == "Miner");
    static double Num(object? v) => v is double d ? d : 0;
    static double Cx(GameObject o) => o.X + o.Width / 2;
    static double Cy(GameObject o) => o.Y + o.Height / 2;

    /// <summary>Points the mouse at a scene point (through the camera, as the game window does).</summary>
    static void Aim(World w, double x, double y)
    {
        var c = w.Camera;
        w.MouseMove((x - c.Left) * c.Zoom, (y - c.Top) * c.Zoom);
    }

    static string TileAt(World w, double x, double y) => (string)w.Functions["tile_at"]([x, y])!;

    /// <summary>Holds the left button on a scene point until the tile there is gone (or time runs out).</summary>
    static bool Dig(World w, double x, double y, double maxSeconds = 4)
    {
        Aim(w, x, y);
        if (TileAt(w, x, y) == "") return true;
        w.MouseDown();
        for (double t = 0; t < maxSeconds && TileAt(w, x, y) != ""; t += Dt) { Aim(w, x, y); w.Frame(Dt); }
        w.MouseUp();
        w.Frame(Dt);
        return TileAt(w, x, y) == "";
    }

    /// <summary>A right click on a scene point (places the selected block).</summary>
    static void RightClick(World w, double x, double y)
    {
        Aim(w, x, y);
        w.MouseDown("right");
        w.Frame(Dt); w.Frame(Dt);
        w.MouseUp("right");
        w.Frame(Dt);
    }

    /// <summary>Walks (holding a direction, jumping when stopped) until the player's middle is near x.</summary>
    static bool WalkTo(World w, double x, double maxSeconds = 12)
    {
        var p = Player(w);
        for (double t = 0; t < maxSeconds; t += Dt)
        {
            double dx = x - Cx(p);
            if (Math.Abs(dx) < 6) break;
            string key = dx > 0 ? "right" : "left", other = dx > 0 ? "left" : "right";
            w.KeyUp(other);
            w.KeyDown(key);
            w.Frame(Dt);
            if (p.OnGround && Math.Abs(p.Vx) < 1) { w.KeyDown("space"); w.Frame(Dt); w.KeyUp("space"); }
        }
        w.KeyUp("left"); w.KeyUp("right");
        w.RunFor(0.3);
        return Math.Abs(x - Cx(p)) < 10;
    }

    /// <summary>The middle of every column's lowest trunk cell, nearest the player first.</summary>
    static List<(double X, double Y)> Trees(World w)
    {
        var l = w.Tiles.Main!;
        int trunk = w.Tiles.Id("trunk");
        var found = new List<(double, double)>();
        for (int c = 0; c < l.Columns; c++)
            for (int r = l.Rows - 1; r >= 0; r--)
                if (l.Get(c, r) == trunk) { found.Add((l.X + (c + 0.5) * l.CellSize, l.Y + (r + 0.5) * l.CellSize)); break; }
        var p = Player(w);
        return found.OrderBy(t => Math.Abs(t.Item1 - Cx(p))).ToList();
    }

    static int CountCells(World w, string kind)
    {
        int id = w.Tiles.Id(kind), n = 0;
        foreach (var v in w.Tiles.Main!.Cells) if (v == id) n++;
        return n;
    }

    /// <summary>An empty cell beside the miner (at its feet, or one up when the hill rises), for placing a block.</summary>
    static (double X, double Y) FreeSpot(World w)
    {
        var p = Player(w);
        foreach (double dx in new[] { 24.0, -24, 40, -40 })
            foreach (double dy in new[] { 8.0, 24, 40 })
            {
                double x = Cx(p) + dx, y = p.Y + p.Height - dy;
                if (TileAt(w, x, y) == "") return (x, y);
            }
        return (Cx(p) + 24, p.Y);
    }

    static GameObject Workshop(World w) => w.FindObject("Workshop")!;
    static void Craft(World w, string what) { w.Tell(Workshop(w), "craft", [what]); w.Frame(Dt); w.Frame(Dt); }

    /// <summary>Chops trees (walking to each) until the inventory has this much wood.</summary>
    static void ChopWood(World w, int wood)
    {
        foreach (var (tx, ty) in Trees(w).Take(6))
        {
            if (w.CountItem("wood") >= wood) return;
            WalkTo(w, tx);
            for (int k = 0; k < 6 && w.CountItem("wood") < wood; k++)
            {
                double y = ty - k * 16;
                if (TileAt(w, tx, y) != "trunk") break;
                if (!Dig(w, tx, y, 2)) break;
            }
        }
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Sample: Deepdelve");
        string dir = Path.Combine(Path.GetTempPath(), "BE_Test_Deepdelve_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            SampleGames.Make("Deepdelve", dir);
            RunAll(test, dir);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    static void RunAll(Action<string, Action> test, string dir)
    {
        test("engine: move_item puts a stack in a slot; item_slot finds it", () =>
        {
            var w = new World();
            StarterKit.LoadInto(w);
            w.Load("mechanism T\n    when start\n        give \"soil\", 5\n        give \"wood\", 2\n        give \"pick\"\n        move_item \"pick\" to 1\n        a = item_slot(\"pick\")\n        b = item_slot(\"soil\")\n        c = item_slot(\"gem\")\n        move_item \"soil\" to 99\n        d = item_slot(\"soil\")\n        move_item \"gem\" to 1\n");
            var o = w.Spawn("t", 0, 0, ["T"]);
            w.Frame(Dt);
            NoErrors(w);
            var v = o.Mechanisms[0].Vars;
            True(Num(v["a"]) == 1 && Num(v["b"]) == 2 && Num(v["c"]) == 0 && Num(v["d"]) == 3, $"slots {v["a"]} {v["b"]} {v["c"]} {v["d"]}");
            True(string.Join(",", w.Inventory.Select(s => s.Name)) == "pick,wood,soil", "the order");
            w.Load("mechanism U\n    when start\n        move_item \"pick\"\n");
            w.Spawn("u", 0, 0, ["U"]);
            w.Frame(Dt);
            True(w.Errors.Count == 1 && w.Errors[0].Contains("move_item \"pick\" to 1"), "a friendly error without 'to'");
        });

        test("engine: PixelArt writes PNGs from text art (scaled, transparent)", () =>
        {
            var pal = new Dictionary<char, string> { ['#'] = "#FF0000", ['o'] = "#8000FF00" };
            var png = PixelArt.FromText(["#.", ".o"], pal, scale: 3);
            string f = Path.Combine(dir, "art_test.png");
            PixelArt.Save(f, png);
            True(Images.Size(f) is (6, 6), "6 x 6 after scaling by 3");
            True(png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G', "PNG signature");
            var (pw, ph, px) = PixelArt.Pixels(["#.", ".o"], pal);
            True(pw == 2 && ph == 2 && px[0] == 0xFFFF0000 && px[1] == 0 && px[3] == 0x8000FF00, "pixels: opaque red, transparent, half green");
            True(PixelArt.Shade(0xFF808080, -1) == 0xFF000000 && PixelArt.Shade(0xFF808080, 1) == 0xFFFFFFFF, "shade to black and white");
            File.Delete(f);
        });

        test("Deepdelve: --make-sample writes the project, its mechanisms, pictures and sounds", () =>
        {
            True(SampleGames.All.ContainsKey("Deepdelve"), "registered in SampleGames.All");
            True(Project.Exists(dir), "project.json");
            var p = Project.Load(dir);
            foreach (var m in new[] { "DeepWorld", "Miner", "Creature", "Gloop", "Shambler", "SkyCycle", "CreatureSpawner", "Workshop", "DeepHud", "GameFlow", "Reticle", "Note", "Slash", "Poof" })
                True(p.Mechanisms.ContainsKey(m), "mechanism " + m);
            foreach (var (name, src) in p.Mechanisms) Parser.Parse(src); // every one parses
            foreach (var img in new[] { "turf.png", "gem.png", "torch.png", "miner_idle1.png", "miner_walk2.png", "gloop.png", "shambler2.png", "crown.png", "pick_iron.png" })
            {
                string? f = Images.Resolve(dir, img);
                True(f != null, "picture " + img);
                var bytes = File.ReadAllBytes(f!);
                True(bytes.Length > 40 && bytes[0] == 0x89 && bytes[1] == (byte)'P', img + " is a PNG");
            }
            True(Images.Size(Images.Resolve(dir, "turf.png")!) is (16, 16), "tiles are 16 x 16");
            True(Images.Size(Images.Resolve(dir, "miner_idle1.png")!) is (12, 26), "the miner fits its 12 x 26 box");
            foreach (var snd in new[] { "break.wav", "place.wav", "swing.wav", "hurt.wav", "craft.wav", "win.wav", "hit.wav" })
                True(Sounds.Resolve(dir, snd) != null, "sound " + snd);
        });

        test("Deepdelve: the world generates hills, caves, ores, trees and gems deep down", () =>
        {
            var w = Play(dir);
            w.RunFor(1);
            NoErrors(w);
            True(w.HasTiles && w.Tiles.Main!.Columns == 180 && w.Tiles.Main.Rows == 96, "a 180 x 96 map");
            True(w.Tiles.Layers.Count == 2 && !w.Tiles.Layers[0].Solid, "a wall layer behind");
            int turf = CountCells(w, "turf"), trunk = CountCells(w, "trunk"), coal = CountCells(w, "coal"), copper = CountCells(w, "copper"),
                iron = CountCells(w, "iron"), gem = CountCells(w, "gem");
            True(turf > 150, $"grass on the hills ({turf})");
            True(trunk > 20 && Trees(w).Count >= 6, $"trees ({Trees(w).Count})");
            True(coal > 40 && copper > 40 && iron > 30, $"ores: coal {coal}, copper {copper}, iron {iron}");
            True(gem >= 10, $"enough gems for the crown ({gem})");
            // Gems are deep: every one is far under the surface.
            var l = w.Tiles.Main!;
            int gid = w.Tiles.Id("gem"), shallowest = int.MaxValue;
            for (int i = 0; i < l.Cells.Length; i++) if (l.Cells[i] == gid) shallowest = Math.Min(shallowest, i / l.Columns);
            True(shallowest > 45, $"gems start deep (row {shallowest})");
            var p = Player(w);
            True(p.OnGround, "the player stands on the ground at camp");
            True(Math.Abs(Cx(p) - 1440) < 20, $"camp in the middle ({Cx(p)})");
            True(!w.Hotbar.Visible, "no hotbar on the title screen");
            True(w.Darkness > 0.9 && w.Sunlight > 0.99, "dark caves, sunny sky");
        });

        test("Deepdelve: the title screen waits for Start; the game begins", () =>
        {
            var w = Play(dir);
            w.RunFor(0.5);
            var back = w.FindObject("Title Back")!;
            True(w.ScreenShown("Title") && back.Shown, "title shown");
            var p = Player(w);
            double x0 = p.X;
            w.KeyDown("right");
            w.RunFor(0.5);
            w.KeyUp("right");
            True(Math.Abs(p.X - x0) < 1, "the miner waits on the title screen");
            // Click the Start button like a player would.
            var start = w.FindObject("Start Button")!;
            w.MouseMove(start.X + start.Width / 2, start.Y + start.Height / 2);
            w.MouseDown(); w.Frame(Dt); w.MouseUp();
            w.RunFor(0.3);
            True(!w.ScreenShown("Title") && !back.Shown, "title hidden after Start");
            True(Miner(w).Vars["playing"] is true, "the miner plays");
            True(w.Hotbar.Visible && w.Hotbar.Slots == 10 && w.ScreenShown("HUD"), "a 10-slot hotbar and the HUD");
            w.KeyDown("right");
            w.RunFor(0.5);
            w.KeyUp("right");
            True(p.X > x0 + 20, "and walks");
            NoErrors(w);
        });

        test("Deepdelve: picks set what can be dug; the bare hand chops wood but not slate", () =>
        {
            var w = Begin(Play(dir));
            var p = Player(w);
            var l = w.Tiles.Main!;
            // A slate block right next to the miner's feet.
            var (col, row) = l.CellAt(p.X + p.Width + 10, p.Y + p.Height - 4);
            l.Set(col, row, (ushort)w.Tiles.Id("slate"));
            double sx = l.X + (col + 0.5) * l.CellSize, sy = l.Y + (row + 0.5) * l.CellSize;
            True(!Dig(w, sx, sy, 1.5), "slate doesn't break by hand");
            True(w.Toasts.Any(t => t.Text.Contains("wood pick")), "a hint says what is needed");
            w.Give("wood pick");
            True(Dig(w, sx, sy, 2), "a wood pick digs slate");
            True(w.CountItem("slate") == 1, "the slate went into the bag");
            l.Set(col, row, (ushort)w.Tiles.Id("iron"));
            True(!Dig(w, sx, sy, 1.5), "iron needs the copper pick");
            w.Give("copper pick");
            True(Dig(w, sx, sy, 2), "the copper pick digs iron");
            l.Set(col, row, (ushort)w.Tiles.Id("gem"));
            True(!Dig(w, sx, sy, 1.5), "gems need the iron pick");
            w.Give("iron pick");
            double t0 = w.Time;
            True(Dig(w, sx, sy, 2), "the iron pick digs gems");
            True(w.Time - t0 < 1, $"and fast ({w.Time - t0:0.00}s)");
            True(w.CountItem("gem") == 1, "one gem");
            NoErrors(w);
        });

        test("Deepdelve: chop trees, craft planks and a workbench, place it, craft a wood pick, dig down, light a torch", () =>
        {
            var w = Begin(Play(dir));
            var p = Player(w);
            ChopWood(w, 7);
            True(w.CountItem("wood") >= 7, $"chopped wood ({w.CountItem("wood")})");
            // Open the workshop with C and click the planks button.
            w.KeyDown("c"); w.Frame(Dt); w.KeyUp("c");
            w.RunFor(0.2);
            var planksButton = w.Objects.FirstOrDefault(o => o.Name == "Button" && o.Mechanisms[0].Vars.GetValueOrDefault("value") as string == "planks");
            True(planksButton != null && planksButton.Ui && planksButton.Shown, "the workshop lists a planks button");
            w.MouseMove(planksButton!.X + 20, planksButton.Y + 10);
            w.MouseDown(); w.Frame(Dt); w.MouseUp();
            w.RunFor(0.2);
            True(w.CountItem("planks") == 4, $"clicking planks made 4 planks ({w.CountItem("planks")})");
            for (int i = 0; i < 6; i++) Craft(w, "planks");
            True(w.CountItem("planks") == 28, $"28 planks ({w.CountItem("planks")})");
            Craft(w, "wood pick");
            True(!Has(w, "wood pick"), "no wood pick without a workbench nearby");
            Craft(w, "workbench");
            True(w.CountItem("workbench") == 1 && w.SelectedItem == "workbench", "a workbench, selected in slot 1");
            w.KeyDown("c"); w.Frame(Dt); w.KeyUp("c"); // close the workshop
            w.RunFor(0.2);
            // Place it next to the miner, on the ground.
            var (bx, by) = FreeSpot(w);
            RightClick(w, bx, by);
            True(TileAt(w, bx, by) == "workbench", $"the workbench stands next to the miner ({TileAt(w, bx, by)})");
            True(!Has(w, "workbench"), "and left the bag");
            Craft(w, "wood pick");
            True(Has(w, "wood pick") && w.SelectedItem == "wood pick", "a wood pick next to the workbench");
            // Dig a shaft down: five blocks under the miner's feet.
            double y0 = p.Y;
            for (int k = 0; k < 5; k++)
            {
                double dx = Cx(p), dy = p.Y + p.Height + 8;
                if (TileAt(w, dx, dy) == "") { w.RunFor(0.3); continue; }
                True(Dig(w, dx, dy, 3), $"dug block {k + 1} under the miner ({TileAt(w, dx, dy)})");
                w.RunFor(0.4);
            }
            True(p.Y > y0 + 48, $"the miner went down the shaft ({p.Y - y0:0} px)");
            // A torch on the shaft wall lights it up.
            w.Give("coal", 1);
            Craft(w, "torch");
            True(w.CountItem("torch") == 4 && w.SelectedItem == "torch", "4 torches, selected");
            double tx = Cx(p), ty = p.Y + 4;
            if (TileAt(w, tx, ty) != "") tx += 16;
            RightClick(w, tx, ty);
            True(TileAt(w, tx, ty) == "torch", "a torch placed");
            w.RunFor(0.2);
            True(w.LightAt(tx, ty) > 0.6, $"the torch lights the shaft ({w.LightAt(tx, ty):0.00})");
            NoErrors(w);
        });

        test("Deepdelve: Gloops by day; night brings Shamblers, sunrise crumbles them", () =>
        {
            var w = Begin(Play(dir, s =>
            {
                SetParam(s, "Sky", "day", 8.0);
                SetParam(s, "Sky", "night", 9.0);
                SetParam(s, "Spawner", "every", 1.0);
            }));
            var sky = w.FindObject("Sky")!.Mechanisms[0];
            var p = Player(w);
            int gloops = 0;
            for (double t = 0; t < 6; t += 0.25) { w.RunFor(0.25); gloops = Math.Max(gloops, w.Objects.Count(o => o.Name == "Gloop")); }
            True(gloops >= 1, "Gloops come by day");
            w.RunFor(4);
            True((string?)sky.Vars["phase"] == "Night", "night falls");
            True(w.Sunlight < 0.9, "the sky gets darker");
            int shamblers = w.Objects.Count(o => o.Name == "Shambler");
            True(shamblers >= 2, $"Shamblers rise at night ({shamblers})");
            var hud = w.FindObject("HUD")!.Mechanisms[0];
            True(((string?)hud.Vars["tip"] ?? "").StartsWith("Night"), "the tip warns about the night");
            w.RunFor(6);
            True(w.Objects.Count(o => o.Name == "Shambler") >= 2, "more of them over the night");
            w.RunFor(4.5);
            True((string?)sky.Vars["phase"] == "Day" && Num(sky.Vars["day_num"]) == 2, "day 2 dawns");
            w.RunFor(1.6);
            True(w.Objects.Count(o => o.Name == "Shambler") == 0, "Shamblers crumble at sunrise");
            True(Num(Miner(w).Vars["hp"]) >= 0, "the miner is still around");
            NoErrors(w);
        });

        test("Deepdelve: fighting — creatures hurt and knock back; hits (harder with a sword) defeat them and drop loot", () =>
        {
            var w = Begin(Play(dir));
            var p = Player(w);
            var miner = Miner(w);
            // A Shambler right on the miner hurts it (2) and knocks it back.
            var sh = w.Spawn("Shambler", p.X + 4, p.Y, ["Shambler"]);
            sh.Width = 14; sh.Height = 26;
            w.RunFor(0.1);
            True(Num(miner.Vars["hp"]) == 8, $"a Shambler's touch takes 2 health ({miner.Vars["hp"]})");
            w.RunFor(0.4);
            // Hit it with the bare hand: 6 health = 6 hits.
            w.Give("copper sword");
            w.MouseDown();
            for (double t = 0; t < 6 && !sh.Destroyed; t += Dt)
            {
                Aim(w, Cx(sh), Cy(sh));
                w.Frame(Dt);
                if (Num(miner.Vars["hp"]) < 4) miner.Vars["hp"] = 10.0; // keep the test about hitting
            }
            w.MouseUp();
            True(sh.Destroyed, "the Shambler was defeated");
            // A Gloop: 3 health, a sword hit does 3.
            var g = w.Spawn("Gloop", p.X + 40, p.Y + 6, ["Gloop"]);
            g.Width = 18; g.Height = 14;
            w.RunFor(0.1);
            double t0 = w.Time;
            w.MouseDown();
            for (double t = 0; t < 3 && !g.Destroyed; t += Dt) { Aim(w, Cx(g), Cy(g)); w.Frame(Dt); }
            w.MouseUp();
            True(g.Destroyed, "the Gloop was defeated");
            True(w.Time - t0 < 1.2, $"one sword hit ({w.Time - t0:0.00}s)");
            True(w.CountItem("goo") >= 1, "it left goo");
            True(Num(miner.Vars["defeated"]) >= 2, "the miner counts its wins");
            // Goo makes salve; salve heals.
            w.Give("goo", 1);
            Craft(w, "salve");
            miner.Vars["hp"] = 3.0;
            miner.Vars["last_hurt"] = w.Time;
            RightClick(w, Cx(p) + 30, Cy(p));
            True(Num(miner.Vars["hp"]) == 8, $"salve heals 5 ({miner.Vars["hp"]})");
            NoErrors(w);
        });

        test("Deepdelve: knocked out, the miner wakes up at camp with full health", () =>
        {
            var w = Begin(Play(dir));
            var p = Player(w);
            var miner = Miner(w);
            double homeX = Num(miner.Vars["home_x"]);
            p.X += 200;
            w.RunFor(0.5);
            w.Tell(p, "hurt", [new List<object?> { 50.0, 1.0 }]);
            w.RunFor(0.1);
            True(miner.Vars["knocked"] is true, "knocked out");
            w.RunFor(2.5);
            True(miner.Vars["knocked"] is false && Num(miner.Vars["hp"]) == 10, "back with full health");
            True(Math.Abs(p.X - homeX) < 2 && p.OnGround, "at camp, on the ground");
            True(Num(miner.Vars["knockouts"]) == 1, "counted");
            NoErrors(w);
        });

        test("Deepdelve: forging the Gem Crown wins (a shortened path)", () =>
        {
            var w = Begin(Play(dir));
            var p = Player(w);
            w.Give("planks", 8);
            Craft(w, "workbench");
            var (bx, by) = FreeSpot(w);
            RightClick(w, bx, by);
            True(TileAt(w, bx, by) == "workbench", "workbench placed");
            Craft(w, "gem crown");
            True(!Has(w, "gem crown") && !w.ScreenShown("Win"), "no crown without the gems");
            w.Give("gem", 5); w.Give("iron", 4); w.Give("copper", 6);
            var hud = w.FindObject("HUD")!.Mechanisms[0];
            w.Frame(Dt);
            True(((string?)hud.Vars["tip"] ?? "").Contains("Gem Crown"), "the tip says to forge it");
            Craft(w, "gem crown");
            w.RunFor(0.3);
            True(Has(w, "gem crown") && w.ScreenShown("Win"), "the crown is made and the win screen shows");
            True(w.FindObject("Win Back")!.Shown && (w.FindObject("Win Stats")!.Caption ?? "").StartsWith("Crowned in"), "with the time it took");
            var keep = w.FindObject("Keep Button")!;
            w.MouseMove(keep.X + 10, keep.Y + 10);
            w.MouseDown(); w.Frame(Dt); w.MouseUp();
            w.RunFor(0.2);
            True(!w.ScreenShown("Win"), "Keep exploring closes it");
            NoErrors(w);
        });

        test("Deepdelve: a minute of play keeps the frame time low", () =>
        {
            var w = Begin(Play(dir, s => SetParam(s, "Spawner", "every", 1.0)));
            var p = Player(w);
            var sw = new Stopwatch();
            double worst = 0;
            int frames = 0;
            for (double t = 0; t < 30; t += Dt)
            {
                string key = ((int)(t / 3)) % 2 == 0 ? "right" : "left";
                w.KeyUp(key == "right" ? "left" : "right");
                w.KeyDown(key);
                if (p.OnGround && Math.Abs(p.Vx) < 1) w.KeyDown("space"); else w.KeyUp("space");
                Aim(w, Cx(p) + (key == "right" ? 30 : -30), Cy(p) + 20);
                if (frames % 90 == 0) w.MouseDown(); else if (frames % 90 == 60) w.MouseUp();
                sw.Restart();
                w.Frame(Dt);
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                frames++;
            }
            sw.Restart();
            w.RunFor(30);
            double avg = sw.Elapsed.TotalMilliseconds / (30 * 60);
            True(avg < 6, $"average frame {avg:0.00} ms");
            True(worst < 60, $"worst frame {worst:0.0} ms");
            NoErrors(w);
        });
    }
}
