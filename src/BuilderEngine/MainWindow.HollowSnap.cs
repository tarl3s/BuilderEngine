using BuilderEngine.Script;

namespace BuilderEngine;

// SAMPLE GAME, dev tool: `--project <Hollow Lantern folder> --snap <dir> --snap-only hollow` plays the Hollow Lantern
// sample and renders the game view at key moments (the intro, exploring by lantern light, digging, the workbench,
// a gloomling hunting, torches, the Warden waking, its shard ring, the win) — then closes. PASS/FAIL in checks.txt.
public sealed partial class MainWindow
{
    async Task SnapHollowLantern(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            int cavern = _project.Scenes.FindIndex(s => s.Name == "Cavern");
            if (cavern < 0) { lines.Add("FAIL no Cavern scene (open the Hollow Lantern sample with --project)"); return; }
            OpenScene(cavern);
            await Task.Delay(300);
            Snap(dir, "hl_0_editor");
            ScenePlay();
            await Task.Delay(900);
            var w = _play!.World;
            void Shot(string name) => SnapControl(_play!, dir, name);
            Shot("hl_1_intro");
            for (int i = 0; i < 10 && w.Dialogue is { Closed: false }; i++) { w.KeyDown("space"); await Task.Delay(60); w.KeyUp("space"); await Task.Delay(120); }
            var p = w.FindObject("player")!;
            w.KeyDown("s");
            await Task.Delay(500);
            w.KeyUp("s");
            await Task.Delay(300);
            Shot("hl_2_lantern");
            Check("no script errors after the intro", w.Errors.Count == 0);

            // Dig: hold the mouse on the nearest wall.
            double cx = p.X + 11, cy = p.Y + 11;
            (double X, double Y)? wall = null;
            for (int r = 1; r <= 3 && wall == null; r++)
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    if (w.SolidTileAt(cx + dx * 32 * r, cy + dy * 32 * r)) { wall = (cx + dx * 32 * r, cy + dy * 32 * r); break; }
            if (wall is { } at)
            {
                var cam = w.Camera;
                w.MouseMove((at.X - cam.Left) * cam.Zoom, (at.Y - cam.Top) * cam.Zoom);
                w.MouseDown();
                await Task.Delay(250);
                Shot("hl_3_digging");
                await Task.Delay(1500);
                w.MouseUp();
                Check("holding the mouse digs a wall", !w.SolidTileAt(at.X, at.Y));
            }

            // The workbench menu.
            w.Give("copper", 6); w.Give("stone", 5); w.Give("coal", 3); w.Give("glowcap", 4); w.Give("ember", 2);
            var bench = w.FindObject("workbench")!;
            p.X = bench.X - 30; p.Y = bench.Y + 40;
            await Task.Delay(300);
            w.KeyDown("c"); await Task.Delay(60); w.KeyUp("c");
            await Task.Delay(400);
            Shot("hl_4_workbench");
            Check("the crafting menu opens", w.Objects.Any(o => o.Ui && !o.Destroyed));
            w.Tell(bench, "craft", ["copper pick"]);
            w.Tell(bench, "craft", ["torch"]);
            await Task.Delay(200);
            w.KeyDown("c"); await Task.Delay(60); w.KeyUp("c");

            // A gloomling hunting in the dark, and torches set down.
            w.Load("mechanism HL_SnapSpawn\n    when start\n        visible = false\n        make Gloomling at self.x, self.y with 22, 22\n        made.Gloomling.hunting = true\n");
            p.X += 260; p.Y += 40;
            var main = w.Tiles.Main!;
            var (pc, pr) = main.CellAt(p.X + 11, p.Y + 11);
            for (int r = pr - 3; r <= pr + 3; r++) for (int c = pc - 2; c <= pc + 7; c++) main.Set(c, r, 0); // a little room to show it in
            await Task.Delay(200);
            w.Spawn("spawn", p.X + 150, p.Y - 20, ["HL_SnapSpawn"]);
            await Task.Delay(700);
            Shot("hl_5_gloomling");
            w.Tell(p, "heal", [10.0]);
            var cam2 = w.Camera;
            w.MouseMove((p.X + 70 - cam2.Left) * cam2.Zoom, (p.Y + 11 - cam2.Top) * cam2.Zoom);
            w.MouseDown("right"); await Task.Delay(60); w.MouseUp("right");
            await Task.Delay(900);
            Shot("hl_6_torch");

            // The Warden.
            w.Give("ember pick", 1);
            foreach (var g in w.Objects.Where(o => o.Is("gloomling")).ToList()) w.Tell(g, "damage", [99.0]);
            p.X = 1280 - 190; p.Y = 1280 - 11;
            await Task.Delay(900);
            Shot("hl_7_warden_wakes");
            for (int i = 0; i < 10 && w.Dialogue is { Closed: false }; i++) { w.KeyDown("space"); await Task.Delay(60); w.KeyUp("space"); await Task.Delay(120); }
            var boss = w.FindObject("Warden")!;
            boss.Mechanisms[0].Vars["hp"] = 55.0;
            w.Tell(boss, "damage", [1.0]); // into phase 2
            bool shards = false;
            for (int i = 0; i < 60 && !shards; i++) { await Task.Delay(50); shards = w.Objects.Any(o => o.Name == "Shard"); w.Tell(p, "heal", [10.0]); }
            await Task.Delay(150);
            Shot("hl_8_shard_ring");
            Check("the Warden throws shards in phase 2", shards);
            bool slam = false;
            for (int i = 0; i < 80 && !slam; i++) { await Task.Delay(50); slam = w.Objects.Any(o => o.Name == "SlamMark"); w.Tell(p, "heal", [10.0]); }
            await Task.Delay(300);
            Shot("hl_9_slam_marks");
            w.Tell(boss, "damage", [200.0]);
            await Task.Delay(7000);
            Shot("hl_10_win");
            Check("the Warden can fall and the game is won", boss.Destroyed && w.Hud.Any(h => h.Text.Contains("HOLLOW IS LIT")));
            Check("no script errors", w.Errors.Count == 0);
            if (w.Errors.Count > 0) lines.AddRange(w.Errors.Select(e => "  " + e));
            SceneStop();
        }
        catch (Exception e) { lines.Add("FAIL " + e); }
        finally { File.WriteAllLines(Path.Combine(dir, "checks.txt"), lines); }
    }
}
