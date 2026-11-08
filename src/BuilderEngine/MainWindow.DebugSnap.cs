using Avalonia;
using Avalonia.Input;
using Avalonia.LogicalTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// DEBUGGER, PARAM TYPES AND VARIANTS, dev tool (part of --snap): the visual param pickers (and editing a path and
// picking an object on the canvas), making a variant from the library, and the debugger (a breakpoint set from the
// gutter, the game pausing at it, the line opened in the code and the blocks, inspecting an object, step).
// Appends PASS/FAIL lines to checks.txt and renders PNGs.
public sealed partial class MainWindow
{
    const string GuardSource = "mechanism Guard\n" +
        "    param target: Object\n" +
        "    param route: Path = [[100, 120], [260, 120]]\n" +
        "    param loot: List of Image\n" +
        "    param costs: List of number = [3, 5]\n" +
        "    param tint: Color = \"#E05D5D\"\n" +
        "    param attack_key: Key = \"space\"\n" +
        "    param level: Choice \"easy\", \"normal\", \"hard\" = \"normal\"\n\n" +
        "    when start\n" +
        "        for each p in route\n" +
        "            move to p[0], p[1] in 0.5\n";

    async Task SnapDebugFeatures(string dir)
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
            ShowTab(scene: true);
            OpenScene(0);
            await Task.Delay(200);

            // ---- Param types ----
            _sources["Guard"] = GuardSource;
            RefreshLibrary();
            SceneOnMechanismsChanged();
            Checkpoint();
            var guard = _scene.Add(new SceneObject { Name = "Guard", X = 96, Y = 200, Width = 32, Height = 32, Color = "#9B7BE0", Mechanisms = [new SceneMechanism { Name = "Guard" }] });
            guard.Mechanisms[0].Params["loot"] = new List<object?> { "hero.png", "run.png" };
            Select(guard.Id);
            await Task.Delay(400);
            Snap(dir, "21_param_types");
            SnapControl(_inspectorRoot, dir, "21b_inspector_param_types");
            Check("the default path is drawn for the selected object", _canvas.Overlay is PathOverlay);

            // Edit the path on the canvas: start editing, click three points, drag the last one.
            _pathEditing = (guard.Id, "Guard", "route");
            RebuildInspector();
            var ov = _canvas.Overlay;
            Check("editing a path puts the path tool on the canvas", ov is PathOverlay);
            guard.Mechanisms[0].Params["route"] = new List<object?>();
            foreach (var pt in new[] { new Point(120, 160), new Point(320, 160), new Point(320, 300) })
            {
                ov!.Press(pt, false, KeyModifiers.None);
                ov.Release();
            }
            ov!.Press(new Point(320, 300), false, KeyModifiers.None);
            ov.Drag(new Point(400, 296), KeyModifiers.None);
            ov.Release();
            await Task.Delay(300);
            var route = World.ToText(guard.Mechanisms[0].Params["route"]);
            Check("clicking adds points and dragging moves one (snapped to the grid): " + route, route == "[[120, 160], [320, 160], [400, 296]]");
            ov.Press(new Point(320, 160), true, KeyModifiers.None);
            ov.Release();
            Check("right-clicking a point removes it", World.ToText(guard.Mechanisms[0].Params["route"]) == "[[120, 160], [400, 296]]");
            await Task.Delay(200);
            Snap(dir, "22_path_editing");
            _pathEditing = null;

            // Pick an object for the Object param by clicking it on the canvas.
            var player = _scene.Objects.First(o => o.Tags.Contains("player"));
            StartObjectPick(guard.Id, o => { Checkpoint(); guard.Mechanisms[0].Params["target"] = o.Name; RebuildInspector(); });
            await Task.Delay(200);
            Snap(dir, "23_object_pick");
            _canvas.Overlay?.Press(new Point(player.X + 4, player.Y + 4), false, KeyModifiers.None);
            await Task.Delay(300);
            Check("picking in the scene sets the Object param", Equals(guard.Mechanisms[0].Params.GetValueOrDefault("target"), player.Name) && _pickOverlay == null);
            guard.Mechanisms[0].Params["attack_key"] = "e";
            guard.Mechanisms[0].Params["level"] = "hard";
            guard.Mechanisms[0].Params["tint"] = "#4FC3F7";
            RebuildInspector();
            await Task.Delay(300);
            SnapControl(_inspectorRoot, dir, "24_inspector_params_set");
            Check("the values save and load back", Project.FromJson(_project.ToJson()).Scenes[0].Find(guard.Id)!.Mechanisms[0].Params is var back
                && World.ToText(back["route"]) == "[[120, 160], [400, 296]]" && World.ToText(back["loot"]) == "[\"hero.png\", \"run.png\"]" && Equals(back["target"], player.Name));

            // ---- Variants ----
            await MakeVariantAsync("Patrol");
            await Task.Delay(500);
            Check("Make a variant creates and opens it", _current == "MyPatrol" && _sources.TryGetValue("MyPatrol", out var vs) && vs.StartsWith("mechanism MyPatrol from Patrol"));
            Check("the library shows where a variant comes from", MechLabel("MyPatrol") == "MyPatrol (from Patrol)");
            Snap(dir, "25_variant_code");
            ShowTab(scene: true);
            OnMechanismDropped("MyPatrol", new Point(600, 200), null);
            await Task.Delay(400);
            SnapControl(_inspectorRoot, dir, "26_variant_inspector");
            Check("the inspector shows the parent's params on a variant", Selected is { } sel && DefOf(sel.Mechanisms[0].Name, out _)?.Params.Count == 2);
            _scene.Objects.RemoveAll(o => o.Name == "Guard" || o.Has("MyPatrol"));
            Select(0);

            // ---- Debugger ----
            OpenMechanism("Coin");
            await Task.Delay(400);
            int line = Array.FindIndex(_sources["Coin"].Replace("\r\n", "\n").Split('\n'), l => l.Trim() == "score += value") + 1;
            ToggleBreakpointAt(line);
            Check("clicking a line number sets a breakpoint", _debugger.HasBreakpoint("Coin", line));
            ToggleBreakpointAt(2);
            Check("a line with nothing to run can't have one", !_debugger.HasBreakpoint("Coin", 2));
            await Task.Delay(300);
            Snap(dir, "27_breakpoint_gutter");
            ShowTab(scene: true);
            ClearLog();
            ScenePlay();
            await Task.Delay(400);
            var w = _play!.World;
            var p = w.FindObject("player");
            var coin = w.FindObject("Coin 1");
            if (p != null && coin != null) { p.X = coin.X - 4; p.Y = coin.Y - 30; }
            Check("the game pauses at the breakpoint", await Until(() => _debugger.PausedAt is { Source: "Coin" }, 3000));
            await Task.Delay(500);
            Snap(dir, "28_debug_paused");
            SnapControl(_inspectorRoot, dir, "28b_debug_panel");
            Check("the panel looks inside the object that stopped", _debugObject?.Name == "Coin 1" && w.Running(_debugObject).Any(r => r.AtBreak && r.Line == line));
            double t0 = w.Time;
            await Task.Delay(300);
            Check("the clock stands still while paused", w.Time == t0);
            var at = _debugger.PausedAt!;
            OpenMechanismAt(at.Source, at.Line);
            await Task.Delay(600);
            Check("the paused line opens in the code and the blocks", _current == "Coin" && _debugLine == line && _blocksView!.HighlightedLine == line);
            Snap(dir, "29_debug_open_line");
            ShowTab(scene: true);
            await Task.Delay(200);
            double score = w.Globals.TryGetValue("score", out var s0) ? World.ToNumber(s0, 0) : 0;
            _debugger.StepFrame();
            await Task.Delay(200);
            Check("step runs the rest of the 'when' (the score went up)", World.ToNumber(w.Globals.GetValueOrDefault("score"), 0) > score && _debugger.Paused);
            _debugger.SetBreakpoint("Coin", line, false);
            _debugger.Resume();
            await Task.Delay(400);
            _play!.ObjectClicked?.Invoke(w.FindObject("player"));
            _debugOnlyObject = true;
            RebuildInspector();
            await Task.Delay(300);
            Snap(dir, "30_debug_object");
            SnapControl(_inspectorRoot, dir, "30b_debug_object_panel");
            Check("the timeline has the touch, and filters by the inspected object",
                _debugger.Events.Any(e => e.Kind == DebugEventKind.Touched) && _debugger.EventsFor(w.FindObject("player")!.Id).All(e => e.Involves(w.FindObject("player")!.Id)));
            _debugger.Pause();
            await Task.Delay(500);
            Check("paused: the variables become editable", _inspectorRoot.GetLogicalDescendants().OfType<Avalonia.Controls.NumericUpDown>().Any());
            Snap(dir, "31_debug_paused_edit");
            _debugger.Resume();
            _debugOnlyObject = false;
            SceneStop();
            Check("stopping detaches the debugger", _debugger.World == null && !_debugger.Paused);
            _sources.Remove("MyPatrol");
            _sources.Remove("Guard");
            RefreshLibrary();
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }
}
