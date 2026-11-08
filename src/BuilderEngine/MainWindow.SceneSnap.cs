using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;


namespace BuilderEngine;

// SCENE FEATURE, dev tool: `--snap <folder>` (use with --project <temp folder>) renders the window itself to PNGs —
// the scene, a selected object with its inspector, a few seconds of play holding "right" — then closes.
// It never captures the screen, only this window's own visuals.
public sealed partial class MainWindow
{
    void SceneSnapIfAsked()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--snap");
        if (i < 0 || i + 1 >= args.Length) return;
        string dir = Path.GetFullPath(args[i + 1]);
        Directory.CreateDirectory(dir);
        Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(800);
                if (ArgValue("--snap-only") == "qol") { await SnapQol(dir); Close(); return; } // just the editor conveniences (MainWindow.QolSnap.cs)
                if (ArgValue("--snap-only") == "prefabs") { await SnapPrefabs(dir); Close(); return; } // prefabs, animations, sprite editor (MainWindow.PrefabsSnap.cs)
                if (ArgValue("--snap-only") == "deepdelve") { await SnapDeepdelve(dir); Close(); return; } // the Deepdelve sample (MainWindow.DeepdelveSnap.cs)
                if (await SnapNightShift(dir)) { Close(); return; } // the Night Shift sample project (MainWindow.NightShiftSnap.cs)
                if (ArgValue("--snap-only") == "hollow") { await SnapHollowLantern(dir); Close(); return; } // the Hollow Lantern sample (MainWindow.HollowSnap.cs)
                if (_project.Name == BuilderEngine.Script.EmberDuelSample.Name) { await SnapEmberDuel(dir); Close(); return; } // the sample card game (MainWindow.EmberDuelSnap.cs)
                Snap(dir, "1_scene");
                if (_scene.Objects.FirstOrDefault(o => o.Mechanisms.Count > 0 && o.Tags.Contains("player")) is { } player) Select(player.Id);
                await Task.Delay(300);
                Snap(dir, "2_selected");
                SnapControl(_inspectorRoot, dir, "2b_inspector");
                if (_scene.Objects.FirstOrDefault(o => o.Has("Patrol")) is { } slime) Select(slime.Id);
                await Task.Delay(300);
                Snap(dir, "3_patrol");
                SnapControl(_inspectorRoot, dir, "3b_inspector");
                SnapChecks(dir);
                Select(0);
                ClearLog();
                ScenePlay();
                await Task.Delay(300);
                var world = _play?.World;
                world?.KeysDown.Add("right");
                await Task.Delay(2500);
                Snap(dir, "4_playing");
                File.WriteAllText(Path.Combine(dir, "console.txt"), _console.Text ?? "");
                SceneStop();
                await Task.Delay(300);
                Snap(dir, "5_stopped");
                OpenMechanism("Health");
                await Task.Delay(600);
                Snap(dir, "7_blocks_health");
                await BlocksSnap(dir);
                ShowTab(scene: true);
                await Task.Delay(300);
                await SnapProjectFeatures(dir);
                await SnapEngineFeatures(dir);
                await SnapDebugFeatures(dir); // MainWindow.DebugSnap.cs
                await SnapTools(dir); // MainWindow.ToolsSnap.cs
                await SnapGameKit(dir); // MainWindow.GameKitSnap.cs
                await SnapAiFeatures(dir);
                await SnapQol(dir); // MainWindow.QolSnap.cs
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "error.txt"), e.ToString()); }
            Close();
        };
    }

    /// <summary>Drives the editing commands the way the UI does and writes PASS/FAIL lines to checks.txt.</summary>
    void SnapChecks(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        int n0 = _scene.Objects.Count;
        OnMechanismDropped("Blink", new Point(300, 100), null);
        Check("drop on empty space makes an object", _scene.Objects.Count == n0 + 1 && Selected?.Has("Blink") == true && Selected.Name == "Blink");
        DuplicateSelected();
        Check("duplicate", _scene.Objects.Count == n0 + 2 && Selected?.Name == "Blink 2");
        DeleteSelected();
        Check("delete", _scene.Objects.Count == n0 + 1 && Selected == null);
        Undo();
        Check("undo delete", _scene.Objects.Count == n0 + 2);
        Undo(); Undo();
        Check("undo back to start", _scene.Objects.Count == n0);
        Redo();
        Check("redo", _scene.Objects.Count == n0 + 1);
        Undo();
        var player = _scene.Objects.First(o => o.Tags.Contains("player"));
        OnMechanismDropped("Health", new Point(player.X + 2, player.Y + 2), player);
        player = _scene.Objects.First(o => o.Tags.Contains("player"));
        Check("drop on an object adds the mechanism", player.Has("Health") && Selected?.Id == player.Id);
        SnapControl(_inspectorRoot, dir, "6_inspector_health");
        Undo();
        Check("undo the added mechanism", !_scene.Objects.First(o => o.Tags.Contains("player")).Has("Health"));
        File.WriteAllLines(Path.Combine(dir, "checks.txt"), lines);
    }

    static void SnapControl(Control c, string dir, string name)
    {
        c.UpdateLayout();
        var size = new PixelSize(Math.Max(1, (int)c.Bounds.Width), Math.Max(1, (int)c.Bounds.Height));
        using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
        bmp.Render(c);
        bmp.Save(Path.Combine(dir, name + ".png"));
    }

    void Snap(string dir, string name)
    {
        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
        bmp.Render(this);
        bmp.Save(Path.Combine(dir, name + ".png"));
    }
}
