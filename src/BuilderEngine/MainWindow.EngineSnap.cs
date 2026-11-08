using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BuilderEngine.Script;

namespace BuilderEngine;

// ENGINE FEATURES, dev tool (part of --snap): pictures in the scene and the game, the Npc's dialogue, the demo cutscene
// (camera on the Boss, the dialogue panel, the fade), a question with choices, a message and a bar, the camera
// following the player in a wide scene. Appends PASS/FAIL lines to checks.txt and renders PNGs.
public sealed partial class MainWindow
{
    async Task SnapEngineFeatures(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        async Task<bool> Until(Func<bool> done, int ms)
        {
            for (int t = 0; t < ms; t += 50)
            {
                if (done()) return true;
                await Task.Delay(50);
            }
            return done();
        }
        try
        {
            if (_play != null) SceneStop();
            OpenScene(0);
            await Task.Delay(200);

            // ---- Pictures ----
            string images = Images.Folder(_projectDir);
            Directory.CreateDirectory(images);
            SavePixels(Path.Combine(images, "hero.png"), 16, 24, HeroPixel);
            SavePixels(Path.Combine(images, "run.png"), 64, 16, (x, y) => StripPixel(x, y));
            Pictures.Clear();
            var player = _scene.Objects.First(o => o.Tags.Contains("player"));
            player.Image = "hero.png";
            Select(player.Id);
            await Task.Delay(300);
            Snap(dir, "16_scene_pictures");
            SnapControl(_inspectorRoot, dir, "16b_inspector_picture");
            Check("the scene editor draws a picture", Pictures.Get(ScenePictureOf(player)) != null);
            Select(0);

            // ---- Npc dialogue while playing Level 1 ----
            ClearLog();
            ScenePlay();
            await Task.Delay(300);
            var w = _play!.World;
            Check("the playing world knows the project folder and saves folder", w.ProjectFolder == _projectDir && w.SaveFolder!.StartsWith(_projectDir));
            Check("the player plays with its picture", w.FindObject("player")?.Image == "hero.png");
            var npc = w.FindObject("Old Man");
            if (w.FindObject("player") is { } p && npc != null) { p.X = npc.X - 10; p.Y = npc.Y; }
            Check("walking up to the Npc opens the dialogue", await Until(() => w.Dialogue is { Speaker: "Old Man", FullyTyped: true }, 3000));
            Snap(dir, "17_dialogue_npc");
            SceneStop();

            // ---- The cutscene in Level 2 ----
            OpenScene(1);
            await Task.Delay(200);
            ScenePlay();
            await Task.Delay(300);
            w = _play!.World;
            var boss = w.FindObject("Boss")!;
            w.KeyDown("right");
            Check("the cutscene starts: controls locked, camera on the Boss", await Until(() => w.ControlsLocked && w.Camera.Target == boss, 4000));
            await Until(() => boss.X < 1150, 2000);
            Snap(dir, "18_camera_on_boss");
            Check("the Boss says its line", await Until(() => w.Dialogue is { Speaker: "Boss", FullyTyped: true }, 5000));
            Snap(dir, "19_cutscene_say");
            w.KeyDown("space"); w.KeyUp("space");
            await Until(() => w.Dialogue is { Speaker: "Boss", FullyTyped: true } d && d.Text.StartsWith("Then"), 3000);
            w.KeyDown("space"); w.KeyUp("space");
            Check("the screen fades out", await Until(() => w.FadeAmount > 0.5, 3000));
            Snap(dir, "20_fade");
            Check("the cutscene ends: controls back, camera on the player", await Until(() => !w.ControlsLocked && w.FadeAmount == 0, 5000) && w.Camera.Target == w.FindObject("player"));
            await Task.Delay(1500); // walking right: the camera scrolls with the player
            Snap(dir, "21_camera_follow");
            w.KeyUp("right");
            Check("the camera scrolled with the player in the wide scene", w.Camera.X > 400);

            // ---- A question, a message, a bar, a flipped animated picture ----
            w.Load("mechanism SnapAsk\n    when start\n        look \"run.png\"\n        animate \"run\"\n        face left\n        notify \"Checkpoint!\"\n        show bar 3 of 5 at 16, 50\n        ask \"Join me?\" \"Yes\", \"No\", \"Maybe later\"\n");
            var asker = w.Spawn("Asker", w.Camera.X - 60, 300, ["SnapAsk"]);
            asker.Width = 48; asker.Height = 48;
            Check("ask shows the choices", await Until(() => w.Dialogue is { Options.Count: 3, FullyTyped: true }, 3000));
            w.KeyDown("down"); w.KeyUp("down");
            await Task.Delay(200);
            Snap(dir, "22_ask_notify_bar");
            Check("a strip picture animates", asker.SheetFrames == 4);
            File.WriteAllText(Path.Combine(dir, "console_engine.txt"), _console.Text ?? "");
            SceneStop();
            OpenScene(0);
            player = _scene.Objects.First(o => o.Tags.Contains("player"));
            player.Image = "";
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }

    static uint HeroPixel(int x, int y)
    {
        const uint none = 0, skin = 0xFFF2C9A0, coat = 0xFFD97757, dark = 0xFF141414, boots = 0xFF5A3A2C;
        if (y < 2 || y >= 24) return none;
        if (y < 9) return x is >= 4 and < 12 ? (y == 5 && x is 6 or 9 ? dark : skin) : none;   // head and eyes
        if (y < 18) return x is >= 3 and < 13 ? (x == 12 && y is 11 or 12 ? skin : coat) : none; // coat, a hand
        return x is (>= 4 and < 7) or (>= 9 and < 12) ? boots : none;                            // legs
    }

    static uint StripPixel(int x, int y)
    {
        uint[] c = [0xFF6FBF73, 0xFF4FC3F7, 0xFFE8B04B, 0xFFE07AB5];
        int f = x / 16, lx = x % 16;
        int dx = lx - 8, dy = y - 8;
        return dx * dx + dy * dy <= 30 + f * 6 ? c[f] : 0;
    }

    /// <summary>Writes a small PNG from a pixel function (ARGB).</summary>
    static void SavePixels(string path, int w, int h, Func<int, int, uint> pixel)
    {
        using var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        var data = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) data[y * w + x] = unchecked((int)pixel(x, y));
        using (var fb = bmp.Lock())
            for (int y = 0; y < h; y++) Marshal.Copy(data, y * w, fb.Address + y * fb.RowBytes, w);
        bmp.Save(path);
    }
}
