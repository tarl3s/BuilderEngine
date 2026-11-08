using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// LINK FEATURE, dev tool (part of --snap): with `--project <temp copy of an EasyEngine project folder or .eeproj>`,
// shows the EasyEngine scene and the "Built on EasyEngine" card, then drives saving, live-link autosave, an outside
// change (as EasyEngine would write it), deferral while playing and adding a mechanism. PASS/FAIL lines go to
// link_checks.txt. Never point it at a real project: it edits and saves the file.
public sealed partial class MainWindow
{
    async Task SnapEasyLink(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        string file = _eeproj!;
        try
        {
            string original = File.ReadAllText(file);
            var root = JsonNode.Parse(original)!.AsObject();
            JsonObject? Ee(JsonObject r, int id) => r["Scenes"]!.AsArray().SelectMany(s => s!["Layers"]!.AsArray())
                .SelectMany(l => l!["Objects"]!.AsArray()).Select(o => o!.AsObject()).FirstOrDefault(o => (int?)o["Id"] == id);
            int fileObjects = root["Scenes"]![0]!["Layers"]!.AsArray().Sum(l => l!["Objects"]!.AsArray().Count);

            OpenScene(0);
            await Task.Delay(500);
            Snap(dir, "link_1_scene");
            Check($"the first scene shows every object of the file ({fileObjects})", _scene.Objects.Count == fileObjects);
            var withPictures = _scene.Objects.Where(o => o.Image.Length > 0).ToList();
            Check($"every picture is found in Assets/sprites ({withPictures.Count})", withPictures.All(o => Pictures.Get(o.Image) != null));
            Check("the title says EasyEngine and live link", Title.Contains("(EasyEngine)") && Title.Contains("🔗 Live link"));

            var target = _scene.Objects.FirstOrDefault(o => EasyLink.HasEasyBehaviors(o) && o.Image.Length > 0 && !o.Locked)
                ?? _scene.Objects.First(o => EasyLink.HasEasyBehaviors(o) && !o.Locked);
            Select(target.Id);
            await Task.Delay(400);
            Snap(dir, "link_2_selected");
            SnapControl(_inspectorRoot, dir, "link_2b_inspector");
            Check("the inspector shows the “Built on EasyEngine” card",
                _inspectorRoot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Built on EasyEngine"));

            // Saving with no change leaves the file exactly as it was.
            SceneSave();
            Check("saving an unchanged project leaves the .eeproj byte for byte", File.ReadAllText(file) == original);

            // Live link: an edit is saved by itself ~500 ms later; EasyEngine's data is kept.
            Checkpoint();
            target.X += 16;
            _canvas.InvalidateVisual();
            await Task.Delay(1400);
            var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var savedTarget = Ee(saved, target.Id);
            Check("live link saved the move by itself", savedTarget != null && (double?)savedTarget["X"] == target.X);
            Ee(root, target.Id)!["X"] = target.X;
            Check("…and nothing else in the file changed", EasyLink.SameJson(root, saved));

            // EasyEngine saves (plain overwrite, like its File.WriteAllText): the editor reloads, keeping its state.
            var other = _scene.Objects.First(o => o.Id != target.Id);
            double zoom = _canvas.Zoom;
            int scene = _sceneIndex, updates = CountLog("🔗 Updated");
            var outside = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Ee(outside, other.Id)!["Name"] = "Renamed in EasyEngine";
            File.WriteAllText(file, outside.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            await Task.Delay(1500);
            Check("an outside change is loaded", _scene.Find(other.Id)?.Name == "Renamed in EasyEngine");
            Check("…keeping the scene, the selection and the zoom", _sceneIndex == scene && _canvas.SelectedId == target.Id && Math.Abs(_canvas.Zoom - zoom) < 1e-9);
            Check("…once (our own saves are not reloaded)", CountLog("🔗 Updated") == updates + 1);
            Snap(dir, "link_3_reloaded");
            Undo();
            Check("Ctrl+Z undoes the outside change in this scene", _scene.Find(other.Id)?.Name == other.Name);
            Redo();

            // While playing, outside changes wait until Stop.
            ScenePlay();
            await Task.Delay(800);
            Snap(dir, "link_4_playing");
            var outside2 = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Ee(outside2, other.Id)!["Name"] = "Renamed while playing";
            File.WriteAllText(file, outside2.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            await Task.Delay(1000);
            Check("an outside change waits while playing", _scene.Find(other.Id)?.Name != "Renamed while playing");
            SceneStop();
            await Task.Delay(800);
            Check("…and is loaded after Stop", _scene.Find(other.Id)?.Name == "Renamed while playing");

            // A mechanism on an object with EasyEngine behaviors: it gets both (the question is answered yes in snap mode).
            var t2 = _scene.Find(target.Id)!;
            int behaviors = EasyLink.EasyBehaviors(t2).Count;
            OnMechanismDropped("Blink", new Point(t2.X + 2, t2.Y + 2), t2);
            await Task.Delay(1400);
            var both = Ee(JsonNode.Parse(File.ReadAllText(file))!.AsObject(), target.Id);
            Check("a mechanism added to an EasyEngine object is saved in its Builder block, its behaviors kept",
                both?["Builder"]?["mechanisms"]?[0]?["name"]?.GetValue<string>() == "Blink" && both["Behaviors"]!.AsArray().Count == behaviors);
            Select(target.Id);
            await Task.Delay(300);
            SnapControl(_inspectorRoot, dir, "link_5_both");
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        File.WriteAllLines(Path.Combine(dir, "link_checks.txt"), lines);
    }

    int CountLog(string fragment) => (_console.Text ?? "").Split('\n').Count(l => l.Contains(fragment));
}
