using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// SCENE EDITING CONVENIENCES, dev tool (part of --snap; `--snap-only qol` runs just this): drives the selection box,
// several-object commands, keys, copy/paste between scenes, lock, order, the wheel on a number field, the right-click
// menu with a search and the shortcuts help the way the UI does. Appends PASS/FAIL lines to checks.txt and renders PNGs.
public sealed partial class MainWindow
{
    async Task SnapQol(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        KeyEventArgs Key(Key k, KeyModifiers mods = KeyModifiers.None) => new() { Key = k, KeyModifiers = mods, RoutedEvent = KeyDownEvent };
        try
        {
            SceneStop();
            ShowTab(scene: true);
            var s = _project.AddScene("QoL Test", _scene);
            s.Add(new SceneObject { Name = "Ground", X = 0, Y = 400, Width = s.Width, Height = 50, Color = "#6B5B4B", Locked = true, Mechanisms = [new SceneMechanism { Name = "Solid" }] });
            s.Add(new SceneObject { Name = "Coin", X = 120, Y = 200, Width = 24, Height = 24, Color = "#E8B04B", Mechanisms = [new SceneMechanism { Name = "Coin" }] });
            s.Add(new SceneObject { Name = "Coin 2", X = 200, Y = 240, Width = 24, Height = 24, Color = "#E8B04B", Mechanisms = [new SceneMechanism { Name = "Coin" }] });
            s.Add(new SceneObject { Name = "Slime", X = 320, Y = 368, Width = 40, Height = 32, Color = "#6FBF73", Mechanisms = [new SceneMechanism { Name = "Patrol" }] });
            s.Add(new SceneObject { Name = "Box", X = 560, Y = 120, Width = 64, Height = 64, Color = "#5B8DEF" });
            RefreshScenes();
            OpenScene(_project.Scenes.IndexOf(s));
            await Task.Delay(400);
            _canvas.Fit();
            int Id(string name) => _scene.Objects.First(o => o.Name == name).Id;

            // Selection box over the two coins and the slime (the locked ground is under it too, and is left out).
            _canvas.ShowSelectionBox(new Point(100, 180), new Point(380, 420));
            RebuildInspector();
            await Task.Delay(300);
            Check($"selection box picks the coins and the slime, not the locked ground ({_canvas.SelectedIds.Count})",
                _canvas.SelectedIds.Count == 3 && !_canvas.IsSelected(Id("Ground")));
            Snap(dir, "30_selection_box");
            _canvas.ShowSelectionBox(null, default);
            RebuildInspector();
            await Task.Delay(300);
            Check("several selected: the inspector shows them all", _inspectorRoot.Children.OfType<DockPanel>().Any(d => d.Children.OfType<TextBlock>().Any(t => t.Text == "3 OBJECTS")));
            Snap(dir, "31_multi_selected");
            SnapControl(_inspectorRoot, dir, "31b_multi_inspector");

            // Arrow keys: a burst is one undo step; Shift = one grid cell.
            int undo0 = _undo.Count;
            double x0 = _scene.Find(Id("Coin"))!.X;
            OnSceneKey(Key(Avalonia.Input.Key.Right)); OnSceneKey(Key(Avalonia.Input.Key.Right)); OnSceneKey(Key(Avalonia.Input.Key.Right, KeyModifiers.Shift));
            Check("arrows move every selected object (1 + 1 + 8 px)", _scene.Find(Id("Coin"))!.X == x0 + 10 && _scene.Find(Id("Slime"))!.X == 330);
            Check($"a burst of arrow keys is one undo step ({undo0} → {_undo.Count})", _undo.Count <= undo0 + 1);
            Undo();
            Check("undo puts them back", _scene.Find(Id("Coin"))!.X == x0);

            // Align / order / duplicate several.
            AlignSelected(AlignTo.Bottom);
            Check("align bottom lines them up", _scene.Find(Id("Coin"))!.Y + 24 == 400 && _scene.Find(Id("Coin 2"))!.Y + 24 == 400);
            Undo();
            OnSceneKey(Key(Avalonia.Input.Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift));
            Check("Ctrl+Shift+[ sends them to the back, behind the ground", _scene.Objects[0].Name == "Coin" && _scene.Objects[3].Name == "Ground");
            Undo();
            int n0 = _scene.Objects.Count;
            OnSceneKey(Key(Avalonia.Input.Key.D, KeyModifiers.Control));
            Check("Ctrl+D duplicates all three and selects the copies", _scene.Objects.Count == n0 + 3 && _canvas.SelectedIds.Count == 3 && Selected?.Name.StartsWith("Slime") == true);
            Undo();

            // Copy, then paste into another scene and back.
            SelectMany([Id("Coin"), Id("Slime")]);
            OnSceneKey(Key(Avalonia.Input.Key.C, KeyModifiers.Control));
            int here = _sceneIndex, other = _project.Scenes.FindIndex(x => x.Name != "QoL Test");
            OpenScene(other);
            int m0 = _scene.Objects.Count;
            PasteObjects(new Point(403, 99));
            Check("Ctrl+V pastes into another scene, at the point, as new objects", _scene.Objects.Count == m0 + 2 && _canvas.SelectedIds.Count == 2
                && _scene.Objects.Any(o => o.X == 400 && o.Y == 96));
            Undo();
            Check("the paste is one undo step", _scene.Objects.Count == m0);
            OpenScene(here);
            await Task.Delay(200);

            // Ctrl+A, Delete, Esc.
            OnSceneKey(Key(Avalonia.Input.Key.A, KeyModifiers.Control));
            Check("Ctrl+A selects every unlocked object", _canvas.SelectedIds.Count == _scene.Objects.Count - 1);
            OnSceneKey(Key(Avalonia.Input.Key.Escape));
            Check("Esc selects nothing", _canvas.SelectedIds.Count == 0);
            Select(Id("Box"));
            OnSceneKey(Key(Avalonia.Input.Key.Delete));
            Check("Delete deletes", _scene.Objects.All(o => o.Name != "Box"));
            Undo();

            // Lock: not clickable, read-only inspector, survives undo.
            Select(Id("Box"));
            OnSceneKey(Key(Avalonia.Input.Key.L, KeyModifiers.Control));
            var box = _scene.Find(Id("Box"))!;
            Check("Ctrl+L locks", box.Locked);
            Check("a locked object can't be clicked in the scene", _canvas.ObjectAt(new Point(box.X + 4, box.Y + 4), includeLocked: false) == null);
            Check("its inspector is read-only (Name disabled)", _nameBox is { IsEffectivelyEnabled: false });
            OnSceneKey(Key(Avalonia.Input.Key.Delete));
            Check("Delete leaves a locked object", _scene.Find(box.Id) != null);
            await Task.Delay(200);
            Snap(dir, "32_locked");
            Select(0);
            await Task.Delay(200);
            Snap(dir, "32b_scene_object_list");
            Check("the scene's object list shows the lock", _inspectorRoot.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string == "🔒") == 2);
            Undo();
            Check("undo unlocks", !_scene.Find(Id("Box"))!.Locked);

            // The wheel on a focused number field: X goes up a step per notch, Shift ×10; one undo step.
            Select(Id("Box"));
            await Task.Delay(200);
            int u1 = _undo.Count;
            _numX!.Value = (decimal)NumberWheel.Step(_numX, 1, false, false);
            _numX.Value = (decimal)NumberWheel.Step(_numX, 1, true, false);
            Check($"wheel on X: +8 then +80 ({_scene.Find(Id("Box"))!.X})", _scene.Find(Id("Box"))!.X == 560 + 88);
            Check($"the notches are one undo step ({u1} → {_undo.Count})", _undo.Count <= u1 + 1);
            Undo();
            Check("one undo takes all the notches back", _scene.Find(Id("Box"))!.X == 560);

            // Keep proportions: typing Width sets Height.
            box = _scene.Find(Id("Box"))!;
            box.KeepAspect = true;
            box.Width = 64; box.Height = 32;
            RebuildInspector();
            _numW!.Value = 128;
            Check("Keep proportions: Width 128 → Height 64", box.Height == 64);
            box.KeepAspect = false;

            // The right-click menu, and searching it.
            Select(Id("Coin"));
            var menu = BuildSceneMenu(new Point(140, 210), _scene.Find(Id("Coin")));
            menu.Open(_canvas);
            await Task.Delay(500);
            SnapControl(menu, dir, "33_context_menu");
            Check("the menu lists the commands", menu.Items.OfType<MenuItem>().Select(m => m.Tag as string).Where(t => t != null).ToList() is var tags
                && new[] { "Copy", "Paste here", "Duplicate", "Delete", "Bring to front", "Send to back", "🔒 Lock" }.All(tags.Contains));
            Check("an open menu is a searchable list", ListSearch.Active);
            ListSearch.TestType("dup");
            await Task.Delay(300);
            var visible = ListSearch.TestVisible();
            Check($"typing 'dup' leaves only Duplicate ({string.Join(" | ", visible)})", visible.Count == 1 && visible[0].StartsWith("Duplicate"));
            SnapControl(menu, dir, "34_menu_search");
            int n1 = _scene.Objects.Count;
            ListSearch.TestPick(this);
            await Task.Delay(300);
            Check("Enter picks it: the coin is duplicated, the menu closed", _scene.Objects.Count == n1 + 1 && !menu.IsOpen && !ListSearch.Active);
            Undo();

            // A dropdown: the scene picker, searched without changing the scene.
            _sceneCombo.IsDropDownOpen = true;
            await Task.Delay(500);
            ListSearch.TestType("qol");
            await Task.Delay(200);
            var scenes = ListSearch.TestVisible();
            Check($"typing in an open dropdown filters it ({string.Join(" | ", scenes)})", scenes.Count == 1 && scenes[0].StartsWith("QoL Test"));
            Check("searching doesn't pick anything", _scene.Name == "QoL Test");
            _sceneCombo.IsDropDownOpen = false;
            await Task.Delay(200);
            Check("closing the dropdown ends the search", !ListSearch.Active && ListSearch.Query.Length == 0);

            // The locked object's menu, and the empty-space menu.
            var lockedMenu = BuildSceneMenu(new Point(10, 420), _scene.Find(Id("Ground")));
            Check("right-clicking a locked object offers Unlock", lockedMenu.Items.OfType<MenuItem>().Any(m => (m.Tag as string ?? "").StartsWith("🔓 Unlock")));
            Select(0);
            var emptyMenu = BuildSceneMenu(new Point(700, 50), null);
            Check("empty space offers Paste here and + Object here", emptyMenu.Items.OfType<MenuItem>().Select(m => m.Tag as string).ToList() is var et && et.Contains("Paste here") && et.Contains("+ Object here"));

            // The shortcuts help.
            SnapDetached(ShortcutsHelp.Content(), 720, dir, "35_shortcuts_help");

            // Clean up: the test scene goes.
            DeleteOpenScene();
        }
        catch (Exception e) { lines.Add("FAIL qol snap crashed: " + e); }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }

    /// <summary>Renders a control that isn't in the window (measured at a width, as tall as it needs).</summary>
    static void SnapDetached(Control c, double width, string dir, string name)
    {
        c.Measure(new Size(width, double.PositiveInfinity));
        c.Arrange(new Rect(0, 0, width, c.DesiredSize.Height));
        var size = new PixelSize((int)width, Math.Max(1, (int)c.DesiredSize.Height));
        using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
        bmp.Render(c);
        bmp.Save(Path.Combine(dir, name + ".png"));
    }
}
