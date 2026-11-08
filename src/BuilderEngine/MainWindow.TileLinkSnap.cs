using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using BuilderEngine.Script;

namespace BuilderEngine;

// LINK FEATURE, dev tool (part of the link snap, MainWindow.LinkSnap.cs): an EasyEngine project's tilemaps in the editor.
// Opens the scene with the most Tilemap objects, renders it (tiles_1_scene.png, tiles_2_painting.png), paints one cell
// with the real tile brush and checks the live link wrote exactly that cell into the .eeproj, undoes it, then plays the
// scene (tiles_3_playing.png). PASS/FAIL lines go to tiles_checks.txt. Only for a temp copy: it edits and saves the file.
public sealed partial class MainWindow
{
    async Task SnapEasyTiles(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        string file = _eeproj!;
        try
        {
            static bool IsTilemap(JsonNode? o) => o?["Behaviors"] is JsonArray b && b.Any(x => (string?)x?["Kind"] == EasyLink.TilemapKind);
            static List<JsonObject> Maps(JsonObject root, int scene) => root["Scenes"]![scene]!["Layers"]!.AsArray()
                .SelectMany(l => l!["Objects"]!.AsArray()).Where(IsTilemap).Select(o => o!.AsObject()).ToList();
            static int[] Cells(JsonObject map) =>
                JsonNode.Parse(map["Behaviors"]!.AsArray().First(b => (string?)b!["Kind"] == EasyLink.TilemapKind)!["Texts"]!["data"]!.GetValue<string>())!["cells"]!
                    .AsArray().Select(c => c!.GetValue<int>()).ToArray();

            var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            int scenes = root["Scenes"]!.AsArray().Count, si = Enumerable.Range(0, scenes).OrderByDescending(i => Maps(root, i).Count).First();
            int count = Maps(root, si).Count;
            if (count == 0) { lines.Add("PASS (no tilemaps in this project)"); return; }

            for (int i = 0; i < scenes; i++) // every scene with tilemaps, to compare with EasyEngine
                if (Maps(root, i).Count > 0)
                {
                    OpenScene(i);
                    await Task.Delay(300);
                    Snap(dir, $"tiles_0_scene{i + 1}");
                }
            OpenScene(si);
            await Task.Delay(500);
            Snap(dir, "tiles_1_scene");
            var layers = _scene.TileLayers ?? [];
            Check($"scene “{_scene.Name}”: each of its {count} tilemaps is a tile layer", layers.Count == count && layers.All(EasyLink.IsEasyTilemap));
            Check("tilemaps aren't objects in BuilderEngine", !_scene.Objects.Any(o => o.Easy is JsonObject e && IsTilemap(e)));
            var pictured = (_scene.TileKinds ?? []).Where(k => k.Image.Length > 0).ToList();
            Check($"every tile picture is found ({pictured.Count})", pictured.All(k => Pictures.Get(k.Image) != null));
            Check("the TILES row lists the layers to paint", count < 2 || _tileRow.Children.OfType<ComboBox>().Any());

            // Paint one empty cell of the last layer with the real brush: live link saves it.
            _tileLayer = layers.Count - 1;
            var layer = layers[^1];
            int idx = Array.FindIndex(layer.Cells, c => c == 0), kind = layer.Cells.FirstOrDefault(c => c != 0);
            if (kind == 0) kind = 1;
            int id = layer.Easy!["id"]!.GetValue<int>();
            var before = Cells(Maps(root, si).First(o => (int?)o["Id"] == id));
            _tileKind = kind;
            SetTileMode("brush");
            await Task.Delay(300);
            var at = new Point(layer.X + (idx % layer.Columns + 0.5) * layer.CellSize, layer.Y + (idx / layer.Columns + 0.5) * layer.CellSize);
            _tileOverlay!.Press(at, false, KeyModifiers.None);
            _tileOverlay.Release();
            _canvas.InvalidateVisual();
            await Task.Delay(1500);
            Snap(dir, "tiles_2_painting");
            var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var after = Cells(Maps(saved, si).First(o => (int?)o["Id"] == id));
            var changed = Enumerable.Range(0, Math.Min(before.Length, after.Length)).Where(i => before[i] != after[i]).ToList();
            Check($"painting a cell of “{layer.Name}” wrote exactly that cell into the .eeproj (cell {idx})", changed.Count == 1 && changed[0] == idx && after[idx] > 0);
            Undo();
            await Task.Delay(1500);
            Check("Ctrl+Z takes it back out of the file", Cells(Maps(JsonNode.Parse(File.ReadAllText(file))!.AsObject(), si).First(o => (int?)o["Id"] == id)).SequenceEqual(before));
            SetTileMode(null);

            ScenePlay();
            await Task.Delay(900);
            Snap(dir, "tiles_3_playing");
            var w = _play?.World;
            Check("playing: the tile layers and kinds are in the game", w != null && w.Tiles.Layers.Count == count && w.Tiles.Kinds.Count == (_scene.TileKinds?.Count ?? 0));
            SceneStop();
            await Task.Delay(300);
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        finally { File.WriteAllLines(Path.Combine(dir, "tiles_checks.txt"), lines); }
    }
}
