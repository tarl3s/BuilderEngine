using System.Text.Json;
using System.Text.Json.Nodes;
using BuilderEngine.Script;

/// <summary>
/// EasyEngine tilemaps ⇄ BuilderEngine tile layers through the link (EasyLink.Tiles.cs; docs/EASYENGINE-LINK.md, "Tilemaps").
/// Real projects are only read: the tests work on copies in a temp folder (Mistify when it can be found, see
/// <see cref="FindProject"/>).
/// </summary>
static class TileLinkTests
{
    static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected <{expected}> but got <{actual}>");
    }

    static void True(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    static void InTemp(Action<string> body)
    {
        string d = Path.Combine(Path.GetTempPath(), "BE_Tiles_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        try { body(d); }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    /// <summary>An EasyEngine project by folder name (Desktop\Jogos, Documents\EasyEngine Samples, BE_EASYENGINE_PROJECTS), or null.</summary>
    static string? FindProject(string name)
    {
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Jogos"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EasyEngine Samples"),
        };
        if (Environment.GetEnvironmentVariable("BE_EASYENGINE_PROJECTS") is { Length: > 0 } extra)
            roots.AddRange(extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var r in roots)
            if (EasyLink.FindEeproj(Path.Combine(r, name)) is { } f) return f;
        return null;
    }

    /// <summary>Copies a project's .eeproj and its pictures (not its sounds) into <paramref name="tmp"/>; returns the copy's .eeproj.</summary>
    static string CopyProject(string eeproj, string tmp)
    {
        string src = Path.GetDirectoryName(eeproj)!, dir = Path.Combine(tmp, Path.GetFileName(src));
        Directory.CreateDirectory(dir);
        string sprites = Path.Combine(src, "Assets", "sprites");
        if (Directory.Exists(sprites))
        {
            Directory.CreateDirectory(Path.Combine(dir, "Assets", "sprites"));
            foreach (var f in Directory.GetFiles(sprites)) File.Copy(f, Path.Combine(dir, "Assets", "sprites", Path.GetFileName(f)));
        }
        string copy = Path.Combine(dir, Path.GetFileName(eeproj));
        File.Copy(eeproj, copy);
        return copy;
    }

    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    // ---------------- EasyEngine's own reading of a tilemap (TilePalette.Parse, PlayWindow.BuildTilemap), strict ----------------

    sealed record EeTileKind(string Color, bool Solid, string? Image, List<string> Frames, double Fps);

    /// <summary>EasyEngine's TilePalette.Parse (JsonDocument; only objects count; colour defaults to #888888).</summary>
    static List<EeTileKind> EePalette(string json)
    {
        var pal = new List<EeTileKind>();
        using var doc = JsonDocument.Parse(json);
        True(doc.RootElement.ValueKind == JsonValueKind.Array, "the palette is a JSON list");
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string color = e.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "#888888";
            bool solid = e.TryGetProperty("solid", out var s) && s.ValueKind == JsonValueKind.True;
            string? image = e.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.String ? im.GetString() : null;
            var frames = new List<string>();
            if (e.TryGetProperty("frames", out var fr) && fr.ValueKind == JsonValueKind.Array)
                foreach (var f in fr.EnumerateArray()) frames.Add(f.GetString()!);
            double fps = e.TryGetProperty("fps", out var fp) ? fp.GetDouble() : 8;
            pal.Add(new EeTileKind(color, solid, image, frames, fps));
        }
        return pal;
    }

    /// <summary>EasyEngine's reading of Texts.data (GetInt32 everywhere: anything else and the map is empty).</summary>
    static (int Cols, int Rows, int[] Cells) EeData(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        int cols = r.GetProperty("cols").GetInt32(), rows = r.GetProperty("rows").GetInt32();
        var cells = new int[cols * rows];
        int i = 0;
        foreach (var v in r.GetProperty("cells").EnumerateArray()) { if (i < cells.Length) cells[i++] = v.GetInt32(); }
        Eq(cols * rows, i, "one number per cell");
        return (cols, rows, cells);
    }

    static IEnumerable<(JsonObject Scene, JsonObject Obj)> EeObjects(JsonObject root) =>
        root["Scenes"]!.AsArray().OfType<JsonObject>()
            .SelectMany(s => (s["Layers"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(l => (l["Objects"] as JsonArray ?? []).OfType<JsonObject>()).Select(o => (s, o)));

    static JsonObject? Tilemap(JsonObject o) =>
        (o["Behaviors"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(b => (string?)b["Kind"] == "Tilemap");

    // ---------------- Synthetic: two tilemaps, colours, a picture, an animated tile ----------------

    static JsonObject EeTilemap(int id, string name, double x, double y, double ts, string palette, int cols, int rows, int[] cells, int z = 0) => new()
    {
        ["Id"] = id, ["Name"] = name, ["Type"] = "Tilemap", ["Prefab"] = "Tilemap", ["PrefabAll"] = false, ["X"] = x, ["Y"] = y,
        ["Width"] = 32, ["Height"] = 32, ["Rotation"] = 0, ["Color"] = "#9A9AA0", ["SpritePath"] = null, ["TileImage"] = false, ["Opacity"] = 1,
        ["BorderColor"] = null, ["BorderWidth"] = 0, ["CornerRadius"] = -1, ["FontFamily"] = null, ["Bold"] = false,
        ["Animations"] = new JsonArray(), ["DefaultAnimation"] = null,
        ["Physics"] = new JsonObject { ["Mode"] = "None", ["GravityScale"] = 1, ["Bounciness"] = 0, ["FitHitbox"] = false },
        ["Behaviors"] = new JsonArray(new JsonObject
        {
            ["Kind"] = "Tilemap",
            ["Nums"] = new JsonObject { ["tileSize"] = ts, ["breakable"] = 0, ["breakHits"] = 1, ["byBullet"] = 1, ["byMelee"] = 1, ["byDamage"] = 1, ["byStomp"] = 0, ["byTouch"] = 0, ["breakParticles"] = 1, ["dropAmount"] = 1 },
            ["Texts"] = new JsonObject { ["palette"] = palette, ["data"] = JsonSerializer.Serialize(new { cols, rows, cells }), ["requires"] = "", ["drop"] = "" },
        }),
        ["Variables"] = new JsonObject(), ["Flags"] = new JsonArray(), ["Texts"] = new JsonObject(), ["Colors"] = new JsonArray(), ["Lists"] = new JsonObject(),
        ["Group"] = "", ["Locked"] = false, ["Active"] = true, ["Z"] = z, ["KeepAspect"] = false, ["Aspect"] = 0,
    };

    static JsonObject EeBox(int id, string name, double x, double y, double w, double h, params string[] behaviors) => new()
    {
        ["Id"] = id, ["Name"] = name, ["Type"] = "Object", ["Prefab"] = null, ["PrefabAll"] = false, ["X"] = x, ["Y"] = y,
        ["Width"] = w, ["Height"] = h, ["Rotation"] = 0, ["Color"] = "#D04040", ["SpritePath"] = null, ["TileImage"] = false, ["Opacity"] = 1,
        ["BorderColor"] = null, ["BorderWidth"] = 0, ["CornerRadius"] = -1, ["FontFamily"] = null, ["Bold"] = false,
        ["Animations"] = new JsonArray(), ["DefaultAnimation"] = null,
        ["Physics"] = new JsonObject { ["Mode"] = "None", ["GravityScale"] = 1, ["Bounciness"] = 0, ["FitHitbox"] = false },
        ["Behaviors"] = new JsonArray(behaviors.Select(b => (JsonNode?)new JsonObject { ["Kind"] = b, ["Nums"] = new JsonObject(), ["Texts"] = new JsonObject() }).ToArray()),
        ["Variables"] = new JsonObject(), ["Flags"] = new JsonArray(), ["Texts"] = new JsonObject(), ["Colors"] = new JsonArray(), ["Lists"] = new JsonObject(),
        ["Group"] = "", ["Locked"] = false, ["Active"] = true, ["Z"] = 0, ["KeepAspect"] = false, ["Aspect"] = 0,
    };

    static JsonObject EeLayer(string name, string kind, bool screen, params JsonObject[] objects) => new()
    {
        ["Name"] = name, ["Kind"] = kind, ["Visible"] = true, ["ScreenSpace"] = screen, ["Locked"] = false, ["Opacity"] = 1, ["Active"] = true,
        ["Objects"] = new JsonArray(objects.Select(o => (JsonNode?)o).ToArray()),
    };

    const string Palette1 = "[{\"color\":\"#5BA862\",\"solid\":true},{\"color\":\"#6B5A45\",\"solid\":true,\"image\":\"Assets\\\\sprites\\\\dirt.png\"},{\"color\":\"#2E6FD8\",\"solid\":false,\"frames\":[\"Assets\\\\sprites\\\\w1.png\",\"Assets\\\\sprites\\\\w2.png\",\"Assets\\\\sprites\\\\w3.png\"],\"fps\":4}]";
    const string Palette2 = "[{\"color\":\"#3A3A46\",\"solid\":false},{\"color\":\"#5BA862\",\"solid\":true}]";

    /// <summary>A 320 × 192 scene: a 10 × 6 ground map (32 px) in Base and a 20 × 12 wall map (16 px) behind, in Background.</summary>
    static string Synthetic()
    {
        var ground = new int[60];
        for (int c = 0; c < 10; c++) ground[5 * 10 + c] = 1; // bottom row: grass
        ground[4 * 10 + 2] = 2;                             // a dirt block on it
        ground[4 * 10 + 7] = 3;                             // water (not solid, animated)
        ground[0] = 9;                                      // a number the palette doesn't have (EasyEngine draws nothing)
        var walls = new int[240];
        for (int i = 0; i < 240; i += 3) walls[i] = 1;
        walls[239] = 2;
        var scene = new JsonObject
        {
            ["Name"] = "Level", ["Locked"] = false, ["Width"] = 320, ["Height"] = 192, ["BackgroundColor"] = "#202030", ["AmbientLight"] = 1,
            ["Variables"] = new JsonObject(), ["Flags"] = new JsonArray(), ["Texts"] = new JsonObject(), ["Colors"] = new JsonArray(), ["Lists"] = new JsonObject(),
            ["Layers"] = new JsonArray(
                EeLayer("Background", "Background", false, EeTilemap(1, "Walls", 0, 0, 16, Palette2, 20, 12, walls)),
                EeLayer("Base", "Base", false, EeTilemap(2, "Ground", 0, 0, 32, Palette1, 10, 6, ground), EeBox(3, "Hero", 40, 20, 20, 30, "Platformer")),
                EeLayer("Leaves", "Custom", false, EeTilemap(4, "Leaves", 32, 0, 32, Palette2, 2, 1, [2, 2])),
                EeLayer("UI", "UI", true, EeBox(5, "Score", 8, 8, 100, 20, "Label"))),
        };
        var root = new JsonObject { ["Name"] = "Tiles", ["ScreenWidth"] = 320, ["ScreenHeight"] = 192, ["Scenes"] = new JsonArray(scene), ["StartScene"] = "Level" };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("EasyEngine tilemaps ⇄ BuilderEngine tile layers (EasyLink.Tiles.cs)");

        test("EasyEngine tilemaps become tile layers (place, cell size, cells, kinds shared by look, pictures, animation)", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "C:\\x\\Tiles");
            var s = p.Scenes[0];
            Eq("Hero,Score", string.Join(",", s.Objects.Select(o => o.Name)), "tilemaps aren't objects in BuilderEngine");
            Eq("Walls,Ground,Leaves", string.Join(",", s.TileLayers!.Select(l => l.Name)), "one layer per tilemap, in draw order");
            var walls = s.TileLayers[0]; var ground = s.TileLayers[1]; var leaves = s.TileLayers[2];
            Eq((20, 12, 16.0), (walls.Columns, walls.Rows, walls.CellSize));
            Eq((10, 6, 32.0, 0.0, 0.0), (ground.Columns, ground.Rows, ground.CellSize, ground.X, ground.Y));
            Eq(32.0, leaves.X);
            Eq((false, false, true), (walls.Front, ground.Front, leaves.Front), "Background / Base behind, a world layer above Base in front");
            True(walls.Solid && ground.Solid, "EasyEngine tilemaps collide");
            // Kinds: grass (#5BA862 solid) is in both palettes → one kind; 4 different looks.
            Eq(4, s.TileKinds!.Count);
            var grass = s.TileKinds[ground.Get(0, 5) - 1];
            Eq("#5BA862", grass.Color);
            Eq(ground.Get(0, 5), walls.Get(19, 11), "the walls' second tile is the ground's grass");
            var dirt = s.TileKinds[ground.Get(2, 4) - 1];
            Eq(("Assets\\sprites\\dirt.png", "dirt", true), (dirt.Image, dirt.Name, dirt.Solid));
            var water = s.TileKinds[ground.Get(7, 4) - 1];
            True(water.Animated && water.Frames!.Count == 3 && water.Fps == 4 && !water.Solid, "animated water");
            Eq("Assets\\sprites\\w1.png", water.PictureAt(0.1));
            Eq("Assets\\sprites\\w2.png", water.PictureAt(0.3), "4 frames a second");
            Eq("Assets\\sprites\\w1.png", water.PictureAt(0.8), "loops");
            Eq((ushort)0, ground.Get(0, 0), "a number the palette doesn't have is empty");
            Eq(12, ground.Cells.Count(c => c != 0));
            True(!grass.Breakable && grass.DropItem == "", "a tilemap that isn't breakable gives unbreakable kinds");
            True(EasyLink.IsEasyTilemap(ground), "the layer keeps its EasyEngine object");
            // An unchanged project comes back as the same text, also through project.json (BuilderEngine's own save).
            Eq(json, EasyLink.ToEeproj(p, json));
            var again = Project.FromJson(p.ToJson());
            Eq(json, EasyLink.ToEeproj(again, json));
            // Undo snapshots (Scene.Clone) keep the link too.
            Eq(json, EasyLink.ToEeproj(new Project { Name = p.Name, Scenes = [s.Clone()], Easy = p.Easy }, json));
        });

        test("editing a cell in BuilderEngine changes exactly that cell in the .eeproj", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var ground = p.Scenes[0].TileLayers![1];
            ushort dirt = ground.Get(2, 4);
            ground.Set(5, 3, dirt);       // paint dirt
            ground.Set(0, 5, 0);          // erase a grass cell
            string back = EasyLink.ToEeproj(p, json);
            var before = EeData((string)Tilemap(EeObjects(Parse(json)).First(x => (string?)x.Obj["Name"] == "Ground").Obj)!["Texts"]!["data"]!);
            var gObj = EeObjects(Parse(back)).First(x => (string?)x.Obj["Name"] == "Ground").Obj;
            var after = EeData((string)Tilemap(gObj)!["Texts"]!["data"]!);
            Eq((10, 6), (after.Cols, after.Rows));
            var changed = Enumerable.Range(0, 60).Where(i => before.Cells[i] != after.Cells[i]).ToList();
            Eq("35,50", string.Join(",", changed), "only the two cells changed");
            Eq(2, after.Cells[35]); Eq(0, after.Cells[50]); Eq(9, after.Cells[0], "a number BuilderEngine can't show stays");
            Eq((string)Tilemap(EeObjects(Parse(json)).First(x => (string?)x.Obj["Name"] == "Ground").Obj)!["Texts"]!["palette"]!,
                (string)Tilemap(gObj)!["Texts"]!["palette"]!, "the palette stays the same text");
            // Everything else is the same text.
            var restored = Parse(back);
            var gr = EeObjects(restored).First(x => (string?)x.Obj["Name"] == "Ground").Obj;
            Tilemap(gr)!["Texts"]!["data"] = (string)Tilemap(EeObjects(Parse(json)).First(x => (string?)x.Obj["Name"] == "Ground").Obj)!["Texts"]!["data"]!;
            Eq(json, restored.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // Read back: the same cells.
            var q = EasyLink.FromEeproj(back, "x");
            Eq(ground.Data, q.Scenes[0].TileLayers![1].Data);
            Eq(back, EasyLink.ToEeproj(q, back), "and saved again: the same text");
        });

        test("a kind the tilemap's palette doesn't have is added to it; moving / resizing a layer writes the object", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var s = p.Scenes[0];
            var walls = s.TileLayers![0];
            ushort water = s.TileLayers[1].Get(7, 4);
            walls.Set(1, 1, water);
            s.TileKinds!.Add(new TileKind { Name = "lava", Color = "#FF4000", Solid = false, Light = 3 });
            walls.Set(2, 1, (ushort)s.TileKinds.Count);
            var leaves = s.TileLayers[2];
            leaves.X = 64; leaves.CellSize = 16;
            var back = Parse(EasyLink.ToEeproj(p, json));
            var wObj = EeObjects(back).First(x => (string?)x.Obj["Name"] == "Walls").Obj;
            var pal = EePalette((string)Tilemap(wObj)!["Texts"]!["palette"]!);
            Eq(4, pal.Count, "two tiles added at the end");
            Eq(("#2E6FD8", 3, 4.0, "Assets\\sprites\\w2.png"), (pal[2].Color, pal[2].Frames.Count, pal[2].Fps, pal[2].Frames[1]));
            Eq(("#FF4000", false), (pal[3].Color, pal[3].Solid));
            var d = EeData((string)Tilemap(wObj)!["Texts"]!["data"]!);
            Eq((3, 4), (d.Cells[21], d.Cells[22]));
            var lObj = EeObjects(back).First(x => (string?)x.Obj["Name"] == "Leaves").Obj;
            Eq(64.0, lObj["X"]!.GetValue<double>());
            Eq(16.0, Tilemap(lObj)!["Nums"]!["tileSize"]!.GetValue<double>());
            // The lava kind (light, name) isn't something EasyEngine has: the scene's kinds are kept in Builder.extra.
            var q = EasyLink.FromEeproj(back.ToJsonString(), "x");
            var lava = q.Scenes[0].TileKinds!.First(k => k.Name == "lava");
            Eq(3.0, lava.Light);
            Eq(lava, q.Scenes[0].TileKinds![q.Scenes[0].TileLayers![0].Get(2, 1) - 1], "the painted cell reads back as lava");
        });

        test("a player body lands on a converted solid tile when BuilderEngine plays", () =>
        {
            var p = EasyLink.FromEeproj(Synthetic(), "x");
            var s = p.Scenes[0].Clone();
            var hero = s.Objects.First(o => o.Name == "Hero");
            hero.X = 70; hero.Y = 40; // above the dirt block at column 2 (x 64..96), row 4 (y 128..160)
            hero.Mechanisms.Add(new SceneMechanism { Name = "Body" });
            var w = new World();
            StarterKit.LoadInto(w);
            w.LoadScene(s);
            w.RunFor(1.5);
            var h = w.FindObject("Hero")!;
            True(Math.Abs(h.Y + h.Height - 128) < 0.1, $"stands on the dirt block (bottom at {h.Y + h.Height})");
            var w2 = new World();
            StarterKit.LoadInto(w2);
            hero.X = 230; // above the water (not solid) at column 7: falls to the grass row (y 160)
            w2.LoadScene(s);
            w2.RunFor(1.5);
            var h2 = w2.FindObject("Hero")!;
            True(Math.Abs(h2.Y + h2.Height - 160) < 0.1, $"falls through water onto the grass (bottom at {h2.Y + h2.Height})");
        });

        test("a tile layer made in BuilderEngine becomes an EasyEngine Tilemap object EasyEngine can read", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var s = p.Scenes[0];
            var made = new TileLayer(10, 6, 32, "Clouds") { Front = false };
            s.TileKinds!.Add(new TileKind { Name = "cloud", Color = "#F0F0F0", Solid = false, Image = "cloud.png" });
            made.Set(3, 0, (ushort)s.TileKinds.Count); made.Set(4, 0, (ushort)s.TileKinds.Count); made.Set(9, 5, 1);
            s.TileLayers!.Add(made);
            EasyLink.FixIds(p); // the editor does it before each save: the layer gets its object's id
            string back = EasyLink.ToEeproj(p, json);
            var root = Parse(back);
            var (scene, obj) = EeObjects(root).First(x => (string?)x.Obj["Name"] == "Clouds");
            Eq(6, obj["Id"]!.GetValue<int>(), "a new id (max + 1)");
            Eq("Background", (string?)scene["Layers"]![0]!["Name"]);
            True(scene["Layers"]![0]!["Objects"]!.AsArray().Any(o => (string?)o!["Name"] == "Clouds"), "behind the objects: the Background layer");
            // EasyEngine's typed model: a GameObjectData with one Tilemap behavior (Nums / Texts), every key the catalog has.
            var bh = Tilemap(obj)!;
            var nums = bh["Nums"]!.Deserialize<Dictionary<string, double>>()!;
            var texts = bh["Texts"]!.Deserialize<Dictionary<string, string>>()!;
            foreach (var k in new[] { "tileSize", "breakable", "breakHits", "byBullet", "byMelee", "byDamage", "byStomp", "byTouch", "breakParticles", "dropAmount" })
                True(nums.ContainsKey(k), "Nums." + k);
            foreach (var k in new[] { "palette", "data", "requires", "drop" }) True(texts.ContainsKey(k), "Texts." + k);
            Eq(32.0, nums["tileSize"]);
            Eq((0.0, 0.0), (obj["X"]!.GetValue<double>(), obj["Y"]!.GetValue<double>()));
            Eq("Tilemap", (string?)obj["Type"]);
            foreach (var k in new[] { "Physics", "Behaviors", "Variables", "Flags", "Texts", "Colors", "Lists", "Active", "Z", "Opacity" }) True(obj.ContainsKey(k), "object key " + k);
            var pal = EePalette(texts["palette"]);
            var (cols, rows, cells) = EeData(texts["data"]);
            Eq((10, 6), (cols, rows));
            True(cells.All(v => v >= 0 && v <= pal.Count), "every cell is empty or a palette tile");
            Eq(("#F0F0F0", false, "Assets\\sprites\\cloud.png"), (pal[cells[3] - 1].Color, pal[cells[3] - 1].Solid, pal[cells[3] - 1].Image));
            Eq(cells[3], cells[4]);
            Eq(("#3A3A46", false), (pal[cells[59] - 1].Color, pal[cells[59] - 1].Solid), "the scene's first kind");
            Eq(2, pal.Count);
            // Read back: the same layer (name, place, cells, kinds by name), and saving again changes nothing (same id).
            var q = EasyLink.FromEeproj(back, "x");
            var ql = q.Scenes[0].TileLayers!.First(l => l.Name == "Clouds");
            Eq(made.Data, ql.Data);
            Eq("cloud", q.Scenes[0].TileKinds![ql.Get(3, 0) - 1].Name);
            Eq(back, EasyLink.ToEeproj(q, back));
            Eq(back, EasyLink.ToEeproj(p, back), "the editor's project saved again: the same text");
            // Deleting a layer in BuilderEngine deletes its Tilemap object.
            q.Scenes[0].TileLayers!.RemoveAll(l => l.Name == "Walls");
            True(!EeObjects(Parse(EasyLink.ToEeproj(q, back))).Any(x => (string?)x.Obj["Name"] == "Walls"), "Walls deleted");
        });

        test("a BuilderEngine project's tile maps go to EasyEngine as Tilemap objects and come back", () =>
        {
            InTemp(tmp =>
            {
                string a = Path.Combine(tmp, "A"), b = Path.Combine(tmp, "B");
                var p = Project.CreateDemo("Tiled");
                p.Scenes[0].TileKinds = [new TileKind { Name = "dirt", Color = "#6B4A2B" }, new TileKind { Name = "plank", Color = "#C08040", OneWay = true, Hardness = 2 }];
                var l = new TileLayer(25, 15, 32, "Ground");
                for (int c = 0; c < 25; c++) l.Set(c, 14, 1);
                l.Set(5, 10, 2);
                var front = new TileLayer(5, 2, 16, "Leaves") { X = 100, Y = 40, Front = true, Solid = false };
                front.Set(1, 1, 1);
                p.Scenes[0].TileLayers = [l, front];
                p.Save(a);
                string ee = EasyLink.ToEeprojFromFolder(a);
                var root = Parse(ee);
                Eq(2, EeObjects(root).Count(x => Tilemap(x.Obj) != null));
                var leaves = EeObjects(root).First(x => (string?)x.Obj["Name"] == "Leaves").Obj;
                Eq(true, leaves["Builder"]!["extra"]!["tileFront"]!.GetValue<bool>(), "in front, kept for BuilderEngine");
                Eq(false, leaves["Builder"]!["extra"]!["tileSolid"]!.GetValue<bool>());
                EasyLink.SaveFromEeproj(ee, b);
                var q = Project.Load(b);
                var ql = q.Scenes[0].TileLayers!;
                Eq("Ground,Leaves", string.Join(",", ql.Select(x => x.Name)));
                Eq(l.Data, ql[0].Data);
                Eq((front.Data, 100.0, 40.0, 16.0, true, false), (ql[1].Data, ql[1].X, ql[1].Y, ql[1].CellSize, ql[1].Front, ql[1].Solid));
                Eq("dirt,plank", string.Join(",", q.Scenes[0].TileKinds!.Select(k => k.Name)));
                True(q.Scenes[0].TileKinds![1].OneWay && q.Scenes[0].TileKinds[1].Hardness == 2, "kinds keep what EasyEngine has no field for");
                if (LinkTests.FirstDiff(root, Parse(EasyLink.ToEeprojFromFolder(b))) is { } drift) throw new Exception("the second trip drifted: " + drift);
            });
        });

        test("tile maps an earlier link version kept in Builder.extra become Tilemap objects", () =>
        {
            var root = Parse(Synthetic());
            var scene = root["Scenes"]![0]!.AsObject();
            foreach (var layer in scene["Layers"]!.AsArray()) layer!["Objects"]!.AsArray().Clear();
            var tl = new TileLayer(4, 2, 16, "Old"); tl.Set(0, 0, 1); tl.Set(3, 1, 1);
            scene["Builder"] = new JsonObject
            {
                ["extra"] = new JsonObject
                {
                    ["tileKinds"] = JsonSerializer.SerializeToNode(new List<TileKind> { new() { Name = "dirt", Color = "#6B4A2B" } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                    ["tileLayers"] = new JsonArray(JsonSerializer.SerializeToNode(tl, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })),
                },
            };
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var p = EasyLink.FromEeproj(json, "x");
            Eq("Old", p.Scenes[0].TileLayers!.Single().Name);
            Eq(tl.Data, p.Scenes[0].TileLayers![0].Data);
            EasyLink.FixIds(p);
            var back = Parse(EasyLink.ToEeproj(p, json));
            True(EeObjects(back).Any(x => (string?)x.Obj["Name"] == "Old" && Tilemap(x.Obj) != null), "a Tilemap object now");
            True(back["Scenes"]![0]!["Builder"]!["extra"]!["tileLayers"] == null, "no longer in Builder.extra");
            Eq(tl.Data, EasyLink.FromEeproj(back.ToJsonString(), "x").Scenes[0].TileLayers!.Single().Data);
        });

        test("Mistify (a copy): every tilemap becomes a tile layer with its cells, an edit changes one cell, an unchanged save is identical", () =>
        {
            string? src = FindProject("Mistify");
            if (src == null) { Console.WriteLine("        (Mistify not found: skipped)"); return; }
            InTemp(tmp =>
            {
                string copy = CopyProject(src, tmp), dir = Path.GetDirectoryName(copy)!;
                string json = File.ReadAllText(copy);
                var root = Parse(json);
                var p = EasyLink.FromEeproj(json, dir);
                Eq(json.Replace("\r\n", "\n").TrimEnd(), EasyLink.ToEeproj(p, json, dir).Replace("\r\n", "\n").TrimEnd(), "unchanged: the same text");
                var eeScenes = root["Scenes"]!.AsArray().OfType<JsonObject>().ToList();
                int total = 0;
                for (int si = 0; si < eeScenes.Count; si++)
                {
                    var maps = (eeScenes[si]["Layers"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(l => (l["Objects"] as JsonArray ?? []).OfType<JsonObject>())
                        .Where(o => Tilemap(o) != null).ToList();
                    var layers = p.Scenes[si].TileLayers ?? [];
                    Eq(maps.Count, layers.Count, $"scene {si} layers");
                    total += maps.Count;
                    foreach (var m in maps)
                    {
                        var l = layers.First(x => EasyLink.IsEasyTilemap(x) && x.Easy!["id"]!.GetValue<int>() == m["Id"]!.GetValue<int>());
                        var t = Tilemap(m)!["Texts"]!;
                        var pal = EePalette((string)t["palette"]!);
                        var (cols, rows, cells) = EeData((string)t["data"]!);
                        Eq((cols, rows), (l.Columns, l.Rows), $"{m["Name"]} size");
                        Eq(Tilemap(m)!["Nums"]!["tileSize"]!.GetValue<double>(), l.CellSize);
                        for (int i = 0; i < cells.Length; i++)
                        {
                            int v = cells[i];
                            var k = p.Scenes[si].TileKinds!.ElementAtOrDefault(l.Cells[i] - 1);
                            if (v < 1 || v > pal.Count) { Eq((ushort)0, l.Cells[i], "empty cell"); continue; }
                            True(k != null && k.Color == pal[v - 1].Color && k.Solid == pal[v - 1].Solid
                                && (pal[v - 1].Image == null || k.Image == pal[v - 1].Image), $"{m["Name"]} cell {i}: the palette's tile {v}");
                        }
                    }
                }
                Console.WriteLine($"        ({total} tilemaps in {eeScenes.Count} scenes)");
                True(total >= 15, "Mistify's scene tilemaps found");
                // Paint one cell of the first scene's first map and save: only that number changes in the file.
                var scene = p.Scenes.First(x => x.TileLayers is { Count: > 0 });
                var layer = scene.TileLayers![0];
                int idx = Array.FindIndex(layer.Cells, c => c == 0);
                ushort kind = layer.Cells.First(c => c != 0);
                layer.Cells[idx] = kind; layer.Touch();
                string back = EasyLink.ToEeproj(p, json, dir);
                var diffs = new List<string>();
                var a = json.Replace("\r\n", "\n").Split('\n');
                var b2 = back.Replace("\r\n", "\n").Split('\n');
                Eq(a.Length, b2.Length, "same number of lines");
                for (int i = 0; i < a.Length; i++) if (a[i] != b2[i]) diffs.Add(b2[i]);
                Eq(1, diffs.Count, "one line changed (the map's data)");
                int id = layer.Easy!["id"]!.GetValue<int>(), sceneAt = p.Scenes.IndexOf(scene);
                (int Cols, int Rows, int[] Cells) DataOf(JsonObject r) => EeData((string)Tilemap(r["Scenes"]![sceneAt]!["Layers"]!.AsArray().SelectMany(l => l!["Objects"]!.AsArray())
                    .Select(o => o!.AsObject()).First(o => o["Id"]!.GetValue<int>() == id))!["Texts"]!["data"]!);
                var before = DataOf(root);
                var after = DataOf(Parse(back));
                Eq(1, Enumerable.Range(0, before.Cells.Length).Count(i => before.Cells[i] != after.Cells[i]), "one cell changed");
                True(after.Cells[idx] > 0, "the painted cell has a tile");
            });
        });
    }
}
