using BuilderEngine.Script;

namespace BuilderEngine;

// SAMPLE GAMES, dev tool: `--snap <folder> --snap-only deepdelve` (with --project <a folder made by --make-sample
// "Deepdelve">) plays the sample in the real game view and saves PNGs of its key moments — the title, the surface by
// day with the hotbar, digging, the workshop, a torch-lit shaft, the night with Shamblers and the win screen.
public sealed partial class MainWindow
{
    async Task SnapDeepdelve(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            ScenePlay();
            await Task.Delay(900);
            var w = _play!.World;
            var p = w.FindObject("player")!;
            SnapControl(_play, dir, "deep_1_title");
            Check("the title screen shows", w.ScreenShown("Title"));

            w.Broadcast("begin", []);
            w.Give("planks", 12); w.Give("torch", 6); w.Give("wood pick"); w.Give("soil", 20);
            await Task.Delay(1200);
            void Aim(double x, double y) => w.MouseMove((x - w.Camera.Left) * w.Camera.Zoom, (y - w.Camera.Top) * w.Camera.Zoom);
            Aim(p.X + p.Width + 20, p.Y + p.Height + 8);
            w.MouseDown();
            await Task.Delay(220);
            SnapControl(_play, dir, "deep_2_day_digging");
            await Task.Delay(800);
            w.MouseUp();
            Check("the miner stands at camp", p.OnGround);

            w.KeyDown("c"); await Task.Delay(60); w.KeyUp("c");
            await Task.Delay(400);
            SnapControl(_play, dir, "deep_3_workshop");
            Check("the workshop lists recipes", w.Objects.Count(o => o.Name == "Button" && o.Ui && o.Shown) >= 10);
            w.KeyDown("c"); await Task.Delay(60); w.KeyUp("c");

            // Down a shaft with torches on its walls.
            w.Give("iron pick");
            for (int k = 0; k < 14; k++)
            {
                double x = p.X + p.Width / 2, y = p.Y + p.Height + 8;
                w.Load($"mechanism SnapDig{k}\n    when start\n        break_tile {x.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {y.ToString(System.Globalization.CultureInfo.InvariantCulture)} force\n        destroy self\n");
                w.Spawn("dig", 0, 0, [$"SnapDig{k}"]);
                await Task.Delay(140);
                if (k % 5 == 2)
                {
                    w.Load($"mechanism SnapTorch{k}\n    when start\n        set_tile {(x + 16).ToString(System.Globalization.CultureInfo.InvariantCulture)}, {(p.Y + 6).ToString(System.Globalization.CultureInfo.InvariantCulture)} to \"torch\"\n        destroy self\n");
                    w.Spawn("torch", 0, 0, [$"SnapTorch{k}"]);
                }
            }
            await Task.Delay(700);
            SnapControl(_play, dir, "deep_4_torch_shaft");

            // Back up for the night: a short one.
            p.X = (double)(w.FindObject("player")!.Mechanisms[0].Vars["home_x"] ?? p.X) + 120;
            p.Y = w.GroundY(p.X + p.Width / 2) - p.Height - 1;
            var sky = w.FindObject("Sky")!.Mechanisms[0];
            w.Broadcast("night", []);
            sky.Vars["phase"] = "Night";
            w.Load("mechanism SnapNight\n    when start\n        sunlight 0.1\n        destroy self\n");
            w.Spawn("night", 0, 0, ["SnapNight"]);
            for (int i = 0; i < 3; i++)
                w.Load($"mechanism SnapSham{i}\n    when start\n        p = find(\"player\")\n        make Shambler at p.x + {90 + i * 60}, p.y - 10 with 14, 26\n        destroy self\n");
            for (int i = 0; i < 3; i++) w.Spawn("s", 0, 0, [$"SnapSham{i}"]);
            await Task.Delay(1400);
            SnapControl(_play, dir, "deep_5_night_shamblers");
            Check("Shamblers walk at night", w.Objects.Count(o => o.Name == "Shambler") >= 2);

            w.Give("gem", 5); w.Give("iron", 4); w.Give("copper", 6);
            w.Load("mechanism SnapWin\n    when start\n        broadcast \"won\"\n        destroy self\n");
            w.Spawn("win", 0, 0, ["SnapWin"]);
            await Task.Delay(900);
            SnapControl(_play, dir, "deep_6_win");
            Check("the win screen shows", w.ScreenShown("Win"));
            Check("no script errors", w.Errors.Count == 0);
            if (w.Errors.Count > 0) lines.AddRange(w.Errors.Select(e => "  " + e));
            SceneStop();
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }
}
