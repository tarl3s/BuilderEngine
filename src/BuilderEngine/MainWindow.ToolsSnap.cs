using Avalonia;
using BuilderEngine.Script;

namespace BuilderEngine;

// TOOLS + PLUGINS FEATURES, dev tool (part of --snap): uses the starter tools on a fresh scene the way the canvas does
// (press, drags, release), checks one undo step per use, and renders the TOOLS row, a painted platform and the Plugins
// window. Results go to checks.txt like the other snaps.
public sealed partial class MainWindow
{
    async Task SnapTools(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            SceneStop();
            ShowTab(scene: true);
            var s = _project.AddScene("Tools Test", _scene);
            s.Add(new SceneObject { Name = "Ground", X = 0, Y = 400, Width = s.Width, Height = 50, Color = "#6B5B4B", Tags = ["ground"], Mechanisms = [new SceneMechanism { Name = "Solid" }] });
            s.Add(new SceneObject { Name = "Coin", X = 40, Y = 40, Width = 16, Height = 16, Color = "#E8B04B", Tags = ["coin"], Mechanisms = [new SceneMechanism { Name = "Coin" }] });
            RefreshScenes();
            OpenScene(_project.Scenes.IndexOf(s));
            _canvas.Fit();
            await Task.Delay(300);

            var names = AvailableTools().Select(t => t.Tool.Name).ToList();
            Check("TOOLS row lists the starter tools", new[] { "PlatformPainter", "CoinLine", "Eraser", "Scatter", "RoomMaker" }.All(names.Contains));
            OnToolButton("PlatformPainter");
            await Task.Delay(300);
            Check("a tool becomes active: the canvas sends it the pointer", _activeTool == "PlatformPainter" && _canvas.ToolActive && _toolOptionsRow.IsVisible);
            Snap(dir, "20_tools_bar");
            SnapControl(_toolsBar!, dir, "20b_tools_row");

            int undoBefore = _undo.Count, n0 = _scene.Objects.Count;
            OnToolPointer("click", new Point(200, 300));
            for (double x = 210; x <= 420; x += 14) OnToolPointer("drag", new Point(x, 305));
            OnToolPointer("release", new Point(420, 305));
            int painted = _scene.Objects.Count - n0;
            Check($"painting a platform makes tiles ({painted})", painted == 8 && _scene.Objects.Skip(n0).All(o => o.Has("Solid") && o.Width == 32 && o.Y == 288));
            Check("the whole drag is one undo step", _undo.Count == undoBefore + 1);
            await Task.Delay(200);
            Snap(dir, "21_painted_platform");
            Undo();
            Check("undo removes the whole platform", _scene.Objects.Count == n0);
            Redo();
            Check("redo brings it back", _scene.Objects.Count == n0 + 8);

            OnToolButton("CoinLine");
            OnToolPointer("click", new Point(120, 200)); OnToolPointer("release", new Point(120, 200));
            OnToolPointer("click", new Point(440, 160)); OnToolPointer("release", new Point(440, 160));
            Check("CoinLine: two clicks, a line of coins", _scene.Objects.Count(o => o.Has("Coin")) == 1 + 9);
            OnToolButton("RoomMaker");
            OnToolPointer("click", new Point(520, 120));
            OnToolPointer("drag", new Point(760, 384));
            _canvas.ShowToolBox(new Point(520, 120), new Point(760, 384));
            await Task.Delay(300);
            Snap(dir, "22_room_dragging");
            _canvas.ShowToolBox(null, new Point(760, 384));
            OnToolPointer("release", new Point(760, 384));
            Check("RoomMaker: four walls", _scene.Objects.Count(o => o.X >= 512 && o.Y >= 112 && o.Y < 400 && o.Has("Solid")) == 4);
            SetActiveTool(null);
            Check("Esc / Select gives the canvas back", !_canvas.ToolActive);
            await Task.Delay(200);
            Snap(dir, "23_tools_result");
            OpenMechanism("CoinLine");
            await Task.Delay(600);
            Snap(dir, "23b_blocks_tool");
            ShowTab(scene: true);

            // ---- Plugins ----
            bool hasFiles = PluginManager.HasPlugins(_projectDir);
            Check("plugins stay off until the person turns them on (snap never asks)", _plugins == null);
            await SnapWindow(BuildPluginsWindow(), dir, "24_plugins_off");
            if (hasFiles)
            {
                await SnapDialog(Dialogs.Build("This project has plugins", ConsentMessage(), null, ["Turn plugins on", "Not now"], out _), dir, "24b_plugin_consent");
                EnablePlugins(true);
                Check("turning plugins on loads them and remembers it", _plugins is { Plugins.Count: > 0 } && PluginManager.Active == _plugins && PluginConsent.IsAllowed(_settings, _projectDir));
                await SnapWindow(BuildPluginsWindow(), dir, "25_plugins_on");
                var w = NewWorld();
                w.Load("mechanism WaveTest\n    when start\n        v = wave(0.25)\n");
                w.Spawn("A", 0, 0, ["WaveTest"]);
                w.Step(1.0 / 60);
                Check("a plugin function works in a game world", w.Errors.Count == 0 && w.Objects[0].Mechanisms[0].Vars.TryGetValue("v", out var v) && v is double d && Math.Abs(d - 1) < 1e-9);
                OpenMechanism("Coin");
                await Task.Delay(600);
                Check("the blocks palette lists plugin commands (MY COMMANDS, From plugins)", _blocksView?.DebugPaletteHas("call:confetti") == true);
                Snap(dir, "26_blocks_plugin_command");
                ShowTab(scene: true);
                EnablePlugins(false);
                Check("turning them off unloads them", _plugins == null && PluginManager.Active == null);
            }
            else lines.Add("SKIP plugins on: put a plugin .dll in the project's plugins folder to snap it");
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }

    async Task SnapWindow(Avalonia.Controls.Window w, string dir, string name)
    {
        w.Show(this);
        await Task.Delay(400);
        var size = new PixelSize(Math.Max(1, (int)w.Bounds.Width), Math.Max(1, (int)w.Bounds.Height));
        using (var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96, 96)))
        {
            bmp.Render(w);
            bmp.Save(Path.Combine(dir, name + ".png"));
        }
        w.Close();
    }
}
