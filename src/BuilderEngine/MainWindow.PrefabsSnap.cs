using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using BuilderEngine.Script;

namespace BuilderEngine;

// PREFABS FEATURE, dev tool: `--snap <folder> --snap-only prefabs` (with --project <temp folder>) drives prefabs,
// animations, the sprite-sheet slicer and the sprite editor the way the UI does, renders them to PNGs and writes
// PASS/FAIL lines to prefab_checks.txt.
public sealed partial class MainWindow
{
    static readonly Dictionary<char, string> SlimeColors = new() { ['#'] = "#2E5B33", ['o'] = "#6FBF73", ['w'] = "#FFFFFF" };

    static readonly string[][] SlimeFrames =
    [
        ["........", "..####..", ".#oooo#.", "#owoowo#", "#oooooo#", "#oooooo#", ".######.", "........"],
        ["........", "........", "..####..", ".#oooo#.", "#owoowo#", "#oooooo#", "########", "........"],
        ["........", "........", "........", ".######.", "#owoowo#", "#oooooo#", "########", "........"],
        ["..####..", ".#oooo#.", "#owoowo#", "#oooooo#", "#oooooo#", ".######.", "........", "........"],
    ];

    async Task SnapPrefabs(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            // Pictures made here: three slime frames and a 4-frame sheet.
            string images = Images.Folder(_projectDir);
            for (int i = 0; i < 3; i++) PixelArt.Save(Path.Combine(images, $"slime{i + 1}.png"), PixelArt.FromText(SlimeFrames[i], SlimeColors, 4));
            var sheetRows = Enumerable.Range(0, 8).Select(r => string.Concat(SlimeFrames.Select(f => f[r]))).ToList();
            PixelArt.Save(Path.Combine(images, "sheet.png"), PixelArt.FromText(sheetRows, SlimeColors, 4));
            Pictures.Clear();

            // Save an object as a prefab, place two copies by dropping them.
            Checkpoint();
            var slime = _scene.Add(new SceneObject
            {
                Name = "Slime", X = 160, Y = 360, Width = 40, Height = 40, Color = "#6FBF73", Image = "slime1.png", Tags = ["enemy"],
                Mechanisms = [new SceneMechanism { Name = "Patrol", Params = { ["speed"] = 40.0 } }],
            });
            Select(slime.Id);
            var pf = await SaveAsPrefabAsync(slime, "Slime");
            Check("save as prefab", pf != null && slime.Prefab == "Slime" && _project.Prefabs.Count == 1);
            if (pf == null) return;
            _canvas.MechanismDropped!(PrefabDragTag + "Slime", new Point(380, 380), null);
            var a = Selected!;
            _canvas.MechanismDropped!(PrefabDragTag + "Slime", new Point(600, 380), null);
            var b = Selected!;
            Check("dropping a prefab places linked copies", a.Prefab == "Slime" && b.Prefab == "Slime" && a.Id != b.Id && a.Name == "Slime 2");

            // b changes its own width (override); a is ticked and changes speed and colour.
            b.Width = 64;
            Select(a.Id);
            a.PrefabAll = true;
            a.Mechanisms[0].Params["speed"] = 90.0;
            a.Color = "#E05D5D";
            SyncPrefabsNow();
            Check("a ticked copy's change reaches the prefab", pf.Template.Color == "#E05D5D" && Equals(pf.Template.Mechanisms[0].Params["speed"], 90.0));
            Check("... and the other copies", slime.Color == "#E05D5D" && b.Color == "#E05D5D" && Equals(b.Mechanisms[0].Params["speed"], 90.0));
            Check("a copy's own change is kept", b.Width == 64);
            Select(b.Id);
            await Task.Delay(300);
            Snap(dir, "p1_prefab_copies");
            SnapControl(_inspectorRoot, dir, "p2_copy_inspector");
            double bx = b.X;
            Check("revert to prefab", PrefabTools.Revert(_project, b) && b.Width == 40 && b.X == bx);

            // Edit the prefab itself in the inspector.
            EditPrefab("Slime");
            await Task.Delay(300);
            Check("double-click edits the prefab in the inspector", _editingPrefab == pf);
            pf.Template.Height = 48;
            SyncPrefabsNow();
            Check("editing the prefab updates its copies", a.Height == 48 && b.Height == 48 && slime.Height == 48);
            RebuildInspector();
            await Task.Delay(200);
            SnapControl(_inspectorRoot, dir, "p3_prefab_inspector");
            Snap(dir, "p4_library_prefabs");

            // Animations window: a "walk" from pictures and a "roll" from the sprite sheet.
            var aw = new AnimationWindow(_projectDir, Pictures, "Slime", pf.Template.Animations, pf.Template.DefaultAnimation);
            aw.Show(this);
            await Task.Delay(400);
            aw.AddAnimation("walk");
            aw.AddFrames(["slime1.png", "slime2.png", "slime3.png", "slime2.png"]);
            var sw = new SheetSliceWindow(_projectDir, Pictures, "sheet.png");
            sw.Show(this);
            await Task.Delay(400);
            SnapOpenWindow(sw, dir, "p5_sheet_slicer");
            var frames = sw.Slice(close: false);
            sw.Close();
            Check("the sheet is cut into 4 frames", frames is { Count: 4 } && frames.All(f => Images.Resolve(_projectDir, f) != null));
            aw.AddAnimation("roll");
            aw.AddFrames(frames ?? []);
            aw.Select(aw.Animations[0]);
            await Task.Delay(500);
            SnapOpenWindow(aw, dir, "p6_animation_window");
            aw.Close();
            ApplyAnimations(pf.Template, aw);
            SyncPrefabsNow();
            Check("animations go to the prefab and its copies", pf.Template.Animations.Count == 2 && a.Animations.Count == 2 && b.FindAnimation("roll")?.Frames.Count == 4);
            Check("the first animation is the default", pf.Template.ResolveDefaultAnimation()?.Name == "walk");

            // An object with its own animations and the Animator: its params pick the animations.
            Checkpoint();
            var hero = _scene.Add(new SceneObject
            {
                Name = "Hero", X = 80, Y = 200, Width = 32, Height = 32, Color = "#5B8DEF",
                Animations = [new SpriteAnimation { Name = "idle", Frames = ["slime1.png", "slime2.png"], Fps = 3 }, new SpriteAnimation { Name = "walk", Frames = frames ?? [] }],
                Mechanisms = [new SceneMechanism { Name = "Animator" }],
            });
            Select(hero.Id);
            await Task.Delay(300);
            SnapControl(_inspectorRoot, dir, "p6b_animator_inspector");

            // The sprite editor: open a frame, draw a line, save it as a new picture.
            var se = new SpriteEditorWindow(_projectDir, "slime1.png");
            se.Show(this);
            await Task.Delay(400);
            se.PickColor(0xFFE8B04B);
            se.StrokeStart(1, 30, false, false);
            se.StrokeMove(30, 1);
            se.StrokeEnd();
            await Task.Delay(200);
            SnapOpenWindow(se, dir, "p7_sprite_editor");
            bool saved = se.SaveTo("drawn.png");
            se.Close();
            Check("the sprite editor saves a PNG of the right size", saved && Images.Resolve(_projectDir, "drawn.png") is { } dp && Images.Size(dp) == (32, 32));

            // Play: the copies animate, and a script spawns the prefab by name.
            _editingPrefab = null;
            _sources["SlimeMaker"] = "mechanism SlimeMaker\n    when start\n        wait 0.2\n        spawn \"Slime\" 300, 200\n";
            SceneOnMechanismsChanged();
            Checkpoint();
            _scene.Add(new SceneObject { Name = "Maker", X = 20, Y = 20, Width = 24, Height = 24, Mechanisms = [new SceneMechanism { Name = "SlimeMaker" }] });
            Select(0);
            ClearLog();
            ScenePlay();
            await Task.Delay(1500);
            var world = _play?.World;
            int slimes = world?.Objects.Count(o => o.Prefab == "Slime") ?? 0;
            Check($"playing: 3 placed copies + 1 spawned by name (found {slimes})", slimes == 4);
            Check("playing: the Animator plays the idle animation", world?.Objects.FirstOrDefault(o => o.Name == "Hero")?.Image is "slime1.png" or "slime2.png");
            Check("playing: the default animation runs", world?.Objects.Any(o => o.Prefab == "Slime" && o.Image is "slime1.png" or "slime2.png" or "slime3.png") == true);
            Snap(dir, "p8_playing");
            File.WriteAllText(Path.Combine(dir, "prefab_console.txt"), _console.Text ?? "");
            SceneStop();
            await Task.Delay(200);
            TrySave();
            var back = Project.Load(_projectDir);
            Check("saved and loaded back", back.Prefabs.Count == 1 && back.Prefabs[0].Template.Animations.Count == 2
                && back.Scenes[0].Objects.Count(o => o.Prefab == "Slime") == 3 && back.Scenes[0].Objects.First(o => o.Name == "Slime 2").PrefabAll);
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        finally { File.WriteAllLines(Path.Combine(dir, "prefab_checks.txt"), lines); }
    }

    static void SnapOpenWindow(Window w, string dir, string name)
    {
        var size = new PixelSize(Math.Max(1, (int)w.ClientSize.Width), Math.Max(1, (int)w.ClientSize.Height));
        using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
        bmp.Render(w);
        bmp.Save(Path.Combine(dir, name + ".png"));
    }
}
