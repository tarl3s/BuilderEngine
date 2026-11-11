using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// BEHAVIORS FEATURE, dev tool (part of the full --snap run on the demo project): the library's BEHAVIORS section, a
// shipped package dropped on the scene (copied into the project's behaviors/), its inspector card with the package's
// labels and "show if", a spinner's limits, "Save as behavior…" (the window, then the file read back), the read-only code
// view, install / export / import (a broken file is refused), and Play with a package. PNGs beh_*.png; PASS/FAIL lines
// appended to checks.txt. Everything it adds is removed again at the end.
public sealed partial class MainWindow
{
    async Task SnapBehaviors(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + "behaviors: " + what);
        string projBehaviors = BehaviorPackage.ProjectFolder(_projectDir);
        var made = new List<string>();
        int n0 = _scene.Objects.Count;
        try
        {
            ShowTab(scene: true);
            Select(0);
            RefreshLibrary();
            var header = _libItems.OfType<LibItem>().FirstOrDefault(i => i.Header == "BEHAVIORS");
            Check("the library has a BEHAVIORS section", header != null);
            var rows = _libItems.OfType<LibItem>().Where(i => i.Section == LibSection.Behaviors && !i.IsHeader).ToList();
            var shipped = BehaviorPackage.Shipped();
            Check("every shipped package is listed (shipped)", shipped.All(p => rows.Any(r => r.Name == p.Name && r.Badge == "shipped")));
            Check("…and not in the starter kit or this project", !_libItems.OfType<LibItem>().Any(i => i.Section is LibSection.Starter or LibSection.Project && shipped.Any(p => p.Name == i.Name)));
            Check("the last row imports a .behavior", rows.LastOrDefault()?.Name == ImportBehaviorRow);
            if (rows.Count > 0) _library.ScrollIntoView(rows[^1]); // then the header: it comes to the top
            await Task.Delay(200);
            if (header != null) _library.ScrollIntoView(header);
            await Task.Delay(400);
            Snap(dir, "beh_1_library");

            // A shipped package dropped on empty space: a new object with it, and a copy in the project's behaviors/.
            OnMechanismDropped("TriggerArea", new Point(620, 140), null);
            await Task.Delay(300);
            var zone = Selected;
            Check("drop makes an object with the package", zone?.Has("TriggerArea") == true);
            Check("…and copies it into the project's behaviors folder", File.Exists(BehaviorPackage.FileIn(projBehaviors, "TriggerArea"))
                && _packages.TryGetValue("TriggerArea", out var tp) && tp.Origin == PackageOrigin.Project);
            made.Add(BehaviorPackage.FileIn(projBehaviors, "TriggerArea"));
            Check("…which isn't saved as one of the project's mechanisms", !IsProjectMechanism("TriggerArea") && IsPackage("TriggerArea"));
            bool Shown(string label) => _inspectorRoot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label && t.IsEffectivelyVisible);
            await Task.Delay(200);
            Check("the card shows the package's labels", Shown("What happens") && Shown("Who enters") && Shown("Only once"));
            Check("show if: a scene for goscene, no variable", Shown("Scene") && !Shown("Variable") && !Shown("Set to"));
            SnapControl(_inspectorRoot, dir, "beh_2_card_goscene");
            var action = _inspectorRoot.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.ItemsSource is IEnumerable<string> items && items.Contains("setvar"));
            if (action != null) action.SelectedItem = "setvar";
            await Task.Delay(300);
            Check("show if follows the choice (setvar: a variable and its value, no scene)", action != null && Shown("Variable") && Shown("Set to") && !Shown("Scene"));
            SnapControl(_inspectorRoot, dir, "beh_3_card_setvar");

            // A number's limits come from the package.
            if (zone != null)
            {
                zone.Mechanisms.Add(new SceneMechanism { Name = "Spin" });
                EnsurePackageInProject("Spin");
                made.Add(BehaviorPackage.FileIn(projBehaviors, "Spin"));
                RebuildInspector();
                await Task.Delay(200);
                var spins = _inspectorRoot.GetVisualDescendants().OfType<NumericUpDown>().Where(n => n.Maximum == 3600m && n.Minimum == -3600m && n.Increment == 15m).ToList();
                Check("a number field has the package's min, max and step", spins.Count == 1);
            }

            // Save as behavior: a mechanism of the project becomes behaviors/Wobbler.behavior.
            const string wobbler = "mechanism Wobbler\n    param amount: number = 4\n    param every: seconds = 0.5\n\n    when start\n        repeat forever\n            self.x += amount\n            wait every\n            self.x -= amount\n            wait every\n";
            _sources["Wobbler"] = wobbler;
            RefreshLibrary();
            var dlg = SaveAsBehaviorWindow("Wobbler");
            Check("Save as behavior… opens its window", dlg != null);
            if (dlg is { } d)
            {
                d.Fill("Wobbles from side to side.", "How far (px)", "Pixels it moves each way.");
                d.Window.Show(this);
                await Task.Delay(500);
                SnapControl(d.Window, dir, "beh_4_save_as");
                string? why = await d.Save();
                d.Window.Close();
                string file = BehaviorPackage.FileIn(projBehaviors, "Wobbler");
                made.Add(file);
                Check("…saves behaviors/Wobbler.behavior" + (why != null ? " (" + why + ")" : ""), why == null && File.Exists(file));
                var back = File.Exists(file) ? BehaviorPackage.Load(file) : null;
                Check("…which reads back as the same mechanism with its label, help and icon",
                    back != null && back.Script.TrimEnd() == wobbler.TrimEnd() && back.Params[0].Label == "How far (px)" && back.Params[0].Help == "Pixels it moves each way."
                    && back.Icon == BehaviorIcons[1] && back.Description == "Wobbles from side to side." && back.Validate().Count == 0);
                Check("…and the mechanism is the behavior now (not a project mechanism)", IsPackage("Wobbler") && !IsProjectMechanism("Wobbler"));
            }

            // The code, read-only.
            var code = PackageCodeWindow("Spin");
            if (code != null)
            {
                code.Show(this);
                await Task.Delay(500);
                var editor = code.GetVisualDescendants().OfType<CodeEditor>().FirstOrDefault();
                Check("Open its code: read-only, the package's script", editor is { IsReadOnly: true } && editor.Text == _packages["Spin"].Script);
                SnapControl(code, dir, "beh_5_code");
                code.Close();
            }

            // Install, export, import (a broken file is refused).
            InstallPackage("Wobbler");
            string inst = BehaviorPackage.FileIn(BehaviorPackage.InstalledFolder, "Wobbler");
            made.Add(inst);
            Check("Install copies it to the installed folder", File.Exists(inst));
            string exported = Path.Combine(dir, "beh_export", "Wobbler.behavior");
            _snapBehaviorFile = exported;
            await ExportPackageAsync("Wobbler");
            Check("Export writes a .behavior anyone can import", File.Exists(exported) && BehaviorPackage.Load(exported).Validate().Count == 0);
            string broken = Path.Combine(dir, "beh_export", "Broken.behavior");
            File.WriteAllText(broken, "{ \"format\": \"builderengine.behavior\", \"version\": 1, \"name\": \"Broken\", \"script\": \"mechanism Other\\n\" }");
            _snapBehaviorFile = broken;
            await ImportBehaviorAsync();
            Check("Import refuses a broken file and says why", !File.Exists(BehaviorPackage.FileIn(projBehaviors, "Broken")) && (_status.Text ?? "").Contains("problem"));
            File.Delete(BehaviorPackage.FileIn(projBehaviors, "Wobbler"));
            ReloadPackages();
            Check("with the project copy gone, the installed one is used", _packages.TryGetValue("Wobbler", out var wp) && wp.Origin == PackageOrigin.Installed);
            _snapBehaviorFile = exported;
            await ImportBehaviorAsync();
            Check("Import copies a good file into the project", File.Exists(BehaviorPackage.FileIn(projBehaviors, "Wobbler")) && _packages["Wobbler"].Origin == PackageOrigin.Project);
            _snapBehaviorFile = null;
            RefreshLibrary();
            await Task.Delay(300);
            Snap(dir, "beh_6_library_after");

            // Play: the package plays like any mechanism.
            ClearLog();
            ScenePlay();
            await Task.Delay(700);
            Check("Play loads the project's packages", _play?.World.Package("TriggerArea")?.Origin == PackageOrigin.Project && _play.World.Package("Wobbler") != null);
            Check("…with no script errors", _play != null && _play.World.Errors.Count == 0);
            SceneStop();
            await Task.Delay(300);
        }
        catch (Exception e) { lines.Add("FAIL behaviors: exception: " + e); }
        finally
        {
            _snapBehaviorFile = null;
            foreach (var f in made) try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
            for (int i = 0; i < 20 && _scene.Objects.Count > n0; i++) Undo();
            ReloadPackages();
            Select(0);
        }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }
}
