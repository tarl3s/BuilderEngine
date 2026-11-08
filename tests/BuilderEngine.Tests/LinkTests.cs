using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BuilderEngine.Script;

/// <summary>
/// The EasyEngine ⇄ BuilderEngine link (EasyLink.cs, LiveLink.cs; contract in docs/EASYENGINE-LINK.md), headless.
/// Real EasyEngine projects are round-tripped when they can be found (copied to a temp folder first, never written):
/// Desktop\Jogos\*, Documents\EasyEngine Samples\* and the folders in BE_EASYENGINE_PROJECTS (separated by ';').
/// </summary>
static class LinkTests
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
        string d = Path.Combine(Path.GetTempPath(), "BE_Link_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        try { body(d); }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    /// <summary>The EasyEngine projects on this computer (only read; the tests work on copies).</summary>
    static List<string> RealEeprojs()
    {
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Jogos"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EasyEngine Samples"),
        };
        if (Environment.GetEnvironmentVariable("BE_EASYENGINE_PROJECTS") is { Length: > 0 } extra)
            roots.AddRange(extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var found = new List<string>();
        foreach (var r in roots.Where(Directory.Exists))
            foreach (var d in Directory.GetDirectories(r))
                if (EasyLink.FindEeproj(d) is { } f && new FileInfo(f).Length < 20_000_000) found.Add(f);
        return found.DistinctBy(f => f.ToLowerInvariant()).ToList();
    }

    // ---------------- A synthetic EasyEngine project with every case the converter handles ----------------

    static JsonObject Obj(int id, string name, double x, double y, double w = 64, double h = 64, int z = 0, string? sprite = null, params string[] behaviors)
    {
        var o = new JsonObject
        {
            ["Id"] = id, ["Name"] = name, ["Type"] = "Object", ["Prefab"] = null, ["PrefabAll"] = false, ["X"] = x, ["Y"] = y,
            ["Width"] = w, ["Height"] = h, ["Rotation"] = 0, ["Color"] = "#9A9AA0", ["SpritePath"] = sprite, ["TileImage"] = false,
            ["Opacity"] = 1, ["BorderColor"] = null, ["BorderWidth"] = 0, ["CornerRadius"] = -1, ["FontFamily"] = null, ["Bold"] = false,
            ["Animations"] = new JsonArray(), ["DefaultAnimation"] = null,
            ["Physics"] = new JsonObject { ["Mode"] = "None", ["GravityScale"] = 1, ["Bounciness"] = 0, ["FitHitbox"] = false },
            ["Behaviors"] = new JsonArray(behaviors.Select(b => (JsonNode?)new JsonObject
            {
                ["Kind"] = b, ["Nums"] = new JsonObject { ["speed"] = 220.5 }, ["Texts"] = new JsonObject { ["key"] = "Space" }
            }).ToArray()),
            ["Variables"] = new JsonObject(), ["Flags"] = new JsonArray(), ["Texts"] = new JsonObject(), ["Colors"] = new JsonArray(),
            ["Lists"] = new JsonObject(), ["Group"] = "", ["Locked"] = false, ["Active"] = true, ["Z"] = z, ["KeepAspect"] = false, ["Aspect"] = 0,
        };
        return o;
    }

    static JsonObject Layer(string name, string kind, bool screen, params JsonObject[] objects) => new()
    {
        ["Name"] = name, ["Kind"] = kind, ["Visible"] = true, ["ScreenSpace"] = screen, ["Locked"] = false, ["Opacity"] = 1, ["Active"] = true,
        ["Objects"] = new JsonArray(objects.Select(o => (JsonNode?)o).ToArray()),
    };

    static JsonObject SceneNode(string name, params JsonObject[] layers) => new()
    {
        ["Name"] = name, ["Locked"] = false, ["Width"] = 1900, ["Height"] = 720, ["BackgroundColor"] = "#2B2340", ["AmbientLight"] = 0.8,
        ["Variables"] = new JsonObject { ["wave"] = 1 }, ["Flags"] = new JsonArray(), ["Texts"] = new JsonObject(), ["Colors"] = new JsonArray(),
        ["Lists"] = new JsonObject(), ["Layers"] = new JsonArray(layers.Select(l => (JsonNode?)l).ToArray()),
    };

    static string Synthetic()
    {
        var anim = Obj(7, "Hero", 120, 360, 48, 64, 0, "Assets\\sprites\\hero.png", "Platformer", "Health");
        anim["Animations"] = new JsonArray(new JsonObject { ["Name"] = "walk", ["Frames"] = new JsonArray("Assets\\sprites\\w1.png", "Assets\\sprites\\w2.png"), ["Fps"] = 8, ["Loop"] = true });
        anim["DefaultAnimation"] = "walk";
        anim["Prefab"] = "Player";
        var old = Obj(9, "Old format", 10, 10);
        old.Remove("Z"); old.Remove("KeepAspect"); old.Remove("Locked"); old.Remove("Prefab"); old.Remove("PrefabAll");
        var root = new JsonObject
        {
            ["Name"] = "Synthetic", ["AiModel"] = "qwen2.5-coder:1.5b", ["ScreenWidth"] = 960, ["ScreenHeight"] = 540,
            ["Scenes"] = new JsonArray(
                SceneNode("Arena",
                    Layer("Sky", "Sky", true, Obj(1, "Sun", 120, 70, 46, 46)),
                    Layer("Background", "Background", false, Obj(2, "Moon", 1250, 70, 110, 110, 3), Obj(3, "Pillar", 220, 120, 70, 520, -1)),
                    Layer("Base", "Base", false, Obj(4, "Ground", 0, 640, 1900, 80, 0, null, "Solid"), anim, old,
                        Obj(5, "Back thing", 300, 300, 10, 10, -2)),
                    Layer("UI", "UI", true, Obj(6, "Score", 16, 16, 200, 30, 0, null, "Label"))),
                SceneNode("Cave", Layer("Base", "Base", false, Obj(8, "Rock", 50, 50, 32, 32, 0, "Assets\\sprites\\rock.png")))),
            ["Prefabs"] = new JsonArray(new JsonObject { ["Name"] = "Player", ["Icon"] = "🎮", ["Template"] = Obj(0, "Player", 0, 0) }),
            ["Variables"] = new JsonObject { ["score"] = 0 },
            ["Theme"] = new JsonObject { ["FontFamily"] = "Segoe UI", ["DialogSpeed"] = 45 },
            ["StartScene"] = "Arena",
            ["ConsoleOnStart"] = false,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>The same project with every layer's objects sorted the way EasyEngine draws them (Z, then list order).</summary>
    static JsonObject InDrawOrder(JsonObject root)
    {
        foreach (var layer in root["Scenes"]!.AsArray().SelectMany(s => s!["Layers"]!.AsArray()))
        {
            var arr = layer!["Objects"]!.AsArray();
            var sorted = arr.Select((o, i) => (o: o!, i)).OrderBy(x => x.o["Z"]?.GetValue<double>() ?? 0).ThenBy(x => x.i).Select(x => x.o).ToList();
            arr.Clear();
            foreach (var o in sorted) arr.Add(o);
        }
        return root;
    }

    static JsonObject FindEe(JsonObject root, string name) =>
        root["Scenes"]!.AsArray().SelectMany(s => s!["Layers"]!.AsArray()).SelectMany(l => l!["Objects"]!.AsArray())
            .Select(o => o!.AsObject()).First(o => (string?)o["Name"] == name);

    static string LayerOf(JsonObject root, string name) =>
        root["Scenes"]!.AsArray().SelectMany(s => s!["Layers"]!.AsArray())
            .First(l => l!["Objects"]!.AsArray().Any(o => (string?)o!["Name"] == name))!["Name"]!.GetValue<string>();

    /// <summary>The first difference between two JSON trees (for failure messages), or null.</summary>
    static string? FirstDiff(JsonNode? a, JsonNode? b, string path = "$")
    {
        if (EasyLink.SameJson(a, b)) return null;
        if (a is JsonObject ao && b is JsonObject bo)
        {
            foreach (var (k, v) in ao)
                if (FirstDiff(v, bo[k], path + "." + k) is { } d) return d;
            foreach (var (k, v) in bo)
                if (!ao.ContainsKey(k)) return $"{path}.{k} added";
        }
        if (a is JsonArray aa && b is JsonArray ba)
        {
            if (aa.Count != ba.Count) return $"{path}: {aa.Count} items became {ba.Count}";
            for (int i = 0; i < aa.Count; i++)
                if (FirstDiff(aa[i], ba[i], $"{path}[{i}]") is { } d) return d;
        }
        string Short(JsonNode? n) { var s = n?.ToJsonString() ?? "null"; return s.Length > 80 ? s[..80] + "…" : s; }
        return $"{path}: {Short(a)} became {Short(b)}";
    }

    // ---------------- An EasyEngine-like serializer: typed classes that keep unknown properties ----------------

    sealed class EeProject
    {
        public string Name { get; set; } = "";
        public List<EeScene> Scenes { get; set; } = [];
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
    sealed class EeScene
    {
        public string Name { get; set; } = "";
        public double Width { get; set; }
        public List<EeLayer> Layers { get; set; } = [];
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
    sealed class EeLayer
    {
        public string Name { get; set; } = "";
        public List<EeObject> Objects { get; set; } = [];
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
    sealed class EeObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public string Color { get; set; } = "";
        public bool Active { get; set; } = true; // a field EasyEngine knows and BuilderEngine keeps in "easy"
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("EasyEngine link (EasyLink.cs, LiveLink.cs)");

        test("real EasyEngine projects round-trip unchanged (FromEeproj → ToEeproj)", () =>
        {
            var files = RealEeprojs();
            Console.WriteLine($"        ({files.Count} real .eeproj found)");
            InTemp(tmp =>
            {
                int copyNo = 0;
                foreach (var src in files)
                {
                    string dir = Path.Combine(tmp, (++copyNo).ToString(), Path.GetFileName(Path.GetDirectoryName(src)!));
                    Directory.CreateDirectory(dir);
                    string copy = Path.Combine(dir, Path.GetFileName(src));
                    File.Copy(src, copy);
                    string json = File.ReadAllText(copy);
                    var p = EasyLink.FromEeproj(json, dir);
                    string back = EasyLink.ToEeproj(p, json, dir);
                    if (FirstDiff(Parse(json), Parse(back)) is { } d) throw new Exception($"{Path.GetFileName(src)}: {d}");
                    // EasyEngine writes with System.Text.Json too: an unchanged project comes back as the same text.
                    Eq(json.Replace("\r\n", "\n").TrimEnd(), back.Replace("\r\n", "\n").TrimEnd(), Path.GetFileName(src) + " text");
                    // and through BuilderEngine's own project.json (save/load) it is still the same.
                    var again = Project.FromJson(p.ToJson());
                    foreach (var (n, s) in p.Mechanisms) again.Mechanisms[n] = s;
                    if (FirstDiff(Parse(json), Parse(EasyLink.ToEeproj(again, json, dir))) is { } d2) throw new Exception($"{Path.GetFileName(src)} via project.json: {d2}");
                }
            });
        });

        test("a synthetic project with every case round-trips to the same text", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "C:\\x\\Synthetic");
            Eq(json, EasyLink.ToEeproj(p, json));
            // Without the previous file, the easy blocks rebuild every EasyEngine field (an object saved by an old EasyEngine,
            // with missing keys, gets them written with their defaults: left out here).
            var full = Parse(json);
            full["Scenes"]![0]!["Layers"]![2]!["Objects"]!.AsArray().RemoveAt(2);
            var q = EasyLink.FromEeproj(full.ToJsonString(), "Synthetic");
            full["Prefabs"]![0]!["Category"] = "General"; // a prefab saved without a Category gets the default too
            // Each layer comes back in draw order (the order it had in the list can only come from the previous file).
            if (FirstDiff(InDrawOrder(full), InDrawOrder(Parse(EasyLink.ToEeproj(q, null)))) is { } d) throw new Exception(d);
        });

        test("layers flatten in draw order; screen-space layers get the ui tag", () =>
        {
            var p = EasyLink.FromEeproj(Synthetic(), "x");
            var s = p.Scenes[0];
            Eq("Sun,Pillar,Moon,Back thing,Ground,Hero,Old format,Score", string.Join(",", s.Objects.Select(o => o.Name)));
            True(s.Objects.First(o => o.Name == "Sun").Tags.Contains("ui") && s.Objects.First(o => o.Name == "Score").Tags.Contains("ui"), "ui tags");
            True(!s.Objects.First(o => o.Name == "Ground").Tags.Contains("ui"), "world objects have no ui tag");
            Eq(2, (int)s.Objects.First(o => o.Name == "Ground").Easy![EasyLink.LayerKey]!.GetValue<int>(), "easy.layer");
            Eq("Assets\\sprites\\hero.png", s.Objects.First(o => o.Name == "Hero").Image);
            Eq("#2B2340", s.Background);
            Eq(1900.0, s.Width);
            True(EasyLink.HasEasyBehaviors(s.Objects.First(o => o.Name == "Hero")), "Hero has behaviors");
            Eq("Platformer,Health", string.Join(",", EasyLink.EasyBehaviors(s.Objects.First(o => o.Name == "Hero"))));
            True(!EasyLink.HasEasyBehaviors(s.Objects.First(o => o.Name == "Moon")), "Moon has none");
            True(p.Easy is JsonObject pe && !pe.ContainsKey("Prefabs") && pe.ContainsKey("Theme") && !pe.ContainsKey("Scenes"), "project easy");
            Eq("Player", p.Prefabs.Single().Name, "EasyEngine prefabs are BuilderEngine prefabs");
            True(s.Easy is JsonObject se && se["Layers"]!.AsArray().Count == 4 && !se["Layers"]![0]!.AsObject().ContainsKey("Objects"), "scene easy keeps layer settings");
        });

        test("edits in BuilderEngine change only those fields; layers are restored", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var s = p.Scenes[0];
            var hero = s.Objects.First(o => o.Name == "Hero");
            hero.X = 333; hero.Name = "Hero 2"; hero.Color = "#FF0000"; hero.Locked = true;
            hero.Mechanisms.Add(new SceneMechanism { Name = "Coin", Params = { ["value"] = 5.0 } });
            hero.Tags.Add("player");
            var back = Parse(EasyLink.ToEeproj(p, json));
            var h = FindEe(back, "Hero 2");
            Eq(333.0, h["X"]!.GetValue<double>()); Eq("#FF0000", (string?)h["Color"]); Eq(true, h["Locked"]!.GetValue<bool>());
            Eq("Base", LayerOf(back, "Hero 2"));
            Eq("Platformer", (string?)h["Behaviors"]![0]!["Kind"]);
            Eq(220.5, h["Behaviors"]![0]!["Nums"]!["speed"]!.GetValue<double>());
            Eq("Coin", (string?)h["Builder"]!["mechanisms"]![0]!["name"]);
            Eq(5.0, h["Builder"]!["mechanisms"]![0]!["params"]!["value"]!.GetValue<double>());
            Eq("player", (string?)h["Builder"]!["tags"]![0]);
            // Everything else is untouched: put the four fields back and the files are equal again.
            h["X"] = 120; h["Name"] = "Hero"; h["Color"] = "#9A9AA0"; h["Locked"] = false; h.Remove("Builder");
            if (FirstDiff(Parse(json), back) is { } d) throw new Exception(d);
        });

        test("new objects go to the first Base layer, ui-tagged ones to the UI layer", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var s = p.Scenes[0];
            var a = s.Add(new SceneObject { Name = "New box", X = 5, Y = 6, Image = "box.png" });
            var b = s.Add(new SceneObject { Name = "New label", Tags = ["ui"] });
            s.Objects.First(o => o.Name == "Score").Tags.Remove("ui"); // off the screen: back to a world layer
            EasyLink.FixIds(p);
            var back = Parse(EasyLink.ToEeproj(p, json));
            Eq("Base", LayerOf(back, "New box"));
            Eq("UI", LayerOf(back, "New label"));
            Eq("Base", LayerOf(back, "Score"));
            var nb = FindEe(back, "New box");
            Eq("Object", (string?)nb["Type"]);
            Eq("Assets\\sprites\\box.png", (string?)nb["SpritePath"]);
            True(nb["Builder"] == null, "a plain new object has no Builder block");
            True(FindEe(back, "New label")["Builder"] == null, "the derived ui tag isn't stored");
            int[] ids = back["Scenes"]!.AsArray().SelectMany(sc => sc!["Layers"]!.AsArray()).SelectMany(l => l!["Objects"]!.AsArray()).Select(o => o!["Id"]!.GetValue<int>()).ToArray();
            Eq(ids.Length, ids.Distinct().Count(), "ids are unique in the project");
            // A scene with no Base layer gets one; a scene made in BuilderEngine gets EasyEngine's three layers.
            p.Scenes[1].Easy!["Layers"]![0]!["Kind"] = "Custom";
            p.Scenes[1].Easy!["Layers"]![0]!["ScreenSpace"] = true;
            p.Scenes[1].Objects[0].Easy = null;
            var ns = p.AddScene("Made here");
            ns.Add(new SceneObject { Name = "Thing" });
            EasyLink.FixIds(p);
            var back2 = Parse(EasyLink.ToEeproj(p, null));
            var cave = back2["Scenes"]![1]!["Layers"]!.AsArray();
            True(cave.Any(l => (string?)l!["Kind"] == "Base" && l["Objects"]!.AsArray().Count == 1), "Base layer made for the cave");
            var made = back2["Scenes"]![2]!.AsObject();
            Eq("Background,Base,UI", string.Join(",", made["Layers"]!.AsArray().Select(l => (string?)l!["Name"])));
            Eq("Base", LayerOf(back2, "Thing"));
        });

        test("reordering in BuilderEngine keeps EasyEngine's draw order (Z raised only as needed)", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var s = p.Scenes[0];
            var ground = s.Objects.First(o => o.Name == "Ground");
            s.Objects.Remove(ground);
            s.Objects.Insert(s.Objects.FindIndex(o => o.Name == "Score"), ground); // to the front of the world layers
            var back = Parse(EasyLink.ToEeproj(p, json));
            var baseLayer = back["Scenes"]![0]!["Layers"]![2]!["Objects"]!.AsArray().Select(o => o!.AsObject()).ToList();
            var drawn = baseLayer.Select((o, i) => (o, i)).OrderBy(x => x.o["Z"]?.GetValue<double>() ?? 0).ThenBy(x => x.i).Select(x => (string)x.o["Name"]!).ToList();
            Eq("Back thing,Hero,Old format,Ground", string.Join(",", drawn));
            Eq(-2.0, FindEe(back, "Back thing")["Z"]!.GetValue<double>(), "untouched Z stays");
            True(FindEe(back, "Old format")["Z"] == null, "an old object without Z stays without Z");
            // Round-trip again: the order is stable.
            var p2 = EasyLink.FromEeproj(back.ToJsonString(), "x");
            Eq("Back thing,Hero,Old format,Ground", string.Join(",", p2.Scenes[0].Objects.Where(o => (int)o.Easy![EasyLink.LayerKey]!.GetValue<int>() == 2).Select(o => o.Name)));
        });

        test("duplicated ids in a scene: the first keeps it, the later one is renumbered; new objects get max + 1", () =>
        {
            var root = Parse(Synthetic());
            root["Scenes"]![0]!["Layers"]![3]!["Objects"]![0]!["Id"] = 4; // Score (UI, drawn last) gets Ground's id
            root["Scenes"]![1]!["Layers"]![0]!["Objects"]![0]!["Id"] = 2; // Rock reuses Moon's id in another scene: allowed
            var p = EasyLink.FromEeproj(root.ToJsonString(), "x");
            var all = p.Scenes.SelectMany(s => s.Objects).ToList();
            Eq(4, all.First(o => o.Name == "Ground").Id);
            Eq(10, all.First(o => o.Name == "Score").Id, "max + 1");
            Eq(2, all.First(o => o.Name == "Rock").Id, "ids reused in another scene stay");
            var back = Parse(EasyLink.ToEeproj(p, root.ToJsonString()));
            Eq(10, FindEe(back, "Score")["Id"]!.GetValue<int>());
            Eq("Label", (string?)FindEe(back, "Score")["Behaviors"]![0]!["Kind"]);
            Eq(4, FindEe(back, "Ground")["Id"]!.GetValue<int>());
            Eq("Solid", (string?)FindEe(back, "Ground")["Behaviors"]![0]!["Kind"]);
            Eq(@"Assets\sprites\rock.png", (string?)FindEe(back, "Rock")["SpritePath"], "Rock matched in its own scene, not Moon");
            Eq(50.0, FindEe(back, "Rock")["X"]!.GetValue<double>());
            // A new BuilderEngine object (numbered per scene) whose id another scene uses is the one renumbered.
            var fresh = new SceneObject { Id = 4, Name = "Fresh" };
            p.Scenes[1].Objects.Insert(0, fresh);
            var changed = EasyLink.FixIds(p);
            Eq(1, changed.Count); Eq("Fresh", changed[0].Object.Name); Eq(4, changed[0].OldId); Eq(11, fresh.Id);
            Eq(4, all.First(o => o.Name == "Ground").Id);
            Eq(0, EasyLink.FixIds(p).Count, "nothing left to fix");
        });

        test("an EasyEngine change to unmapped data made meanwhile survives a BuilderEngine save", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            p.Scenes[0].Objects.First(o => o.Name == "Moon").Y = 99;
            var disk = Parse(json);
            FindEe(disk, "Hero")["Behaviors"]![0]!["Nums"]!["speed"] = 999; // EasyEngine edited a behavior after we loaded
            disk["Variables"]!["lives"] = 3;
            var back = Parse(EasyLink.ToEeproj(p, disk.ToJsonString()));
            Eq(999.0, FindEe(back, "Hero")["Behaviors"]![0]!["Nums"]!["speed"]!.GetValue<double>());
            Eq(3, back["Variables"]!["lives"]!.GetValue<int>());
            Eq(99.0, FindEe(back, "Moon")["Y"]!.GetValue<double>());
        });

        test("renaming a scene follows StartScene; deleted scenes and objects go away", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            Eq(null, p.RenameScene(0, "Big Arena"));
            p.Scenes[0].Objects.RemoveAll(o => o.Name == "Moon");
            p.RemoveScene(1);
            var back = Parse(EasyLink.ToEeproj(p, json));
            Eq("Big Arena", (string?)back["StartScene"]);
            Eq(1, back["Scenes"]!.AsArray().Count);
            Eq(0.8, back["Scenes"]![0]!["AmbientLight"]!.GetValue<double>(), "scene data kept through the rename");
            True(!back.ToJsonString().Contains("\"Moon\""), "Moon deleted");
        });

        test("BuilderEngine → .eeproj → BuilderEngine keeps mechanisms, params, tags and extra data", () =>
        {
            InTemp(tmp =>
            {
                string a = Path.Combine(tmp, "A"), b = Path.Combine(tmp, "B");
                var p = Project.CreateDemo("Linked");
                p.Scenes[0].TileKinds = [new TileKind { Name = "dirt", Color = "#6B4A2B", Solid = true }];
                var tl = new TileLayer(4, 2, 16, "Ground"); tl.Set(0, 0, 1); tl.Set(3, 1, 1);
                p.Scenes[0].TileLayers = [tl];
                p.Scenes[0].Objects[1].KeepAspect = true;
                p.Scenes[0].Objects[1].Image = "hero.png";
                p.Save(a);
                Directory.CreateDirectory(Path.Combine(a, "images"));
                File.WriteAllBytes(Path.Combine(a, "images", "hero.png"), [1, 2, 3]);
                string ee = EasyLink.ToEeprojFromFolder(a);
                var eeRoot = Parse(ee);
                Eq(1, eeRoot["Builder"]!["linkVersion"]!.GetValue<int>());
                True(eeRoot["Builder"]!["mechanisms"]!["Intro"] != null, "project mechanisms in Builder.mechanisms");
                Eq("images\\hero.png", (string?)FindEe(eeRoot, "Player")["SpritePath"]);
                True(eeRoot["Scenes"]![0]!["Builder"]!["extra"]!["tileLayers"] != null, "tile maps in the scene's Builder.extra");
                EasyLink.SaveFromEeproj(ee, b);
                var q = Project.Load(b);
                Eq(p.Mechanisms["Intro"], q.Mechanisms["Intro"]);
                static JsonNode Strip(JsonNode n)
                {
                    if (n is JsonObject o) { o.Remove("easy"); foreach (var (_, v) in o.ToList()) if (v != null) Strip(v); }
                    if (n is JsonArray arr) foreach (var v in arr) if (v != null) Strip(v);
                    return n;
                }
                var pLoaded = Project.Load(a);
                EasyLink.FixIds(pLoaded); // one id space per project: scene 2's objects get new ids
                var pa = Strip(JsonNode.Parse(pLoaded.ToJson())!);
                var pb = Strip(JsonNode.Parse(q.ToJson())!);
                if (FirstDiff(pa, pb) is { } d) throw new Exception(d);
                // and again: .eeproj from B equals .eeproj from A (nothing drifts)
                if (FirstDiff(eeRoot, Parse(EasyLink.ToEeprojFromFolder(b))) is { } d2) throw new Exception("second trip: " + d2);
                Eq("hero.png", q.Scenes[0].Objects[1].Image, "images\\ is taken off again in a BuilderEngine folder");
            });
        });

        test("an EasyEngine prefab is a BuilderEngine prefab; edits to it change only those fields", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var pf = p.Prefabs.Single();
            Eq("Player", pf.Name); Eq("🎮", pf.Icon); Eq("General", pf.Category, "no Category key reads as General");
            True(pf.Template.Easy is JsonObject te && te.ContainsKey("Behaviors") && !te.ContainsKey(EasyLink.LayerKey), "template keeps EasyEngine data, no layer");
            Eq(json, EasyLink.ToEeproj(p, json), "unchanged prefabs come back as the same text");
            pf.Icon = "★"; pf.Template.Width = 99;
            pf.Template.Mechanisms.Add(new SceneMechanism { Name = "Coin", Params = { ["value"] = 3.0 } });
            var back = Parse(EasyLink.ToEeproj(p, json));
            var bp = back["Prefabs"]![0]!.AsObject();
            Eq("★", (string?)bp["Icon"]);
            True(!bp.ContainsKey("Category"), "an unchanged default isn't added");
            Eq(99.0, bp["Template"]!["Width"]!.GetValue<double>());
            Eq("Coin", (string?)bp["Template"]!["Builder"]!["mechanisms"]![0]!["name"]);
            Eq("Object", (string?)bp["Template"]!["Type"], "EasyEngine data of the template survives");
        });

        test("a BuilderEngine prefab survives .eeproj → BuilderEngine → .eeproj (and into a BuilderEngine folder)", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var slime = new SceneObject { Name = "Slime", Width = 40, Height = 30, Color = "#33CC66", Tags = ["enemy"] };
            slime.Mechanisms.Add(new SceneMechanism { Name = "Patrol", Params = { ["speed"] = 80.0 } });
            p.Prefabs.Add(new Prefab { Name = "Slime", Icon = "🟢", Category = "Enemies", Template = slime });
            p.Prefabs.RemoveAll(x => x.Name == "Player");
            string saved = EasyLink.ToEeproj(p, json);
            var root = Parse(saved);
            var arr = root["Prefabs"]!.AsArray();
            Eq(1, arr.Count, "the deleted prefab is gone");
            var s = arr[0]!.AsObject();
            Eq("Slime", (string?)s["Name"]); Eq("Enemies", (string?)s["Category"]);
            Eq("Object", (string?)s["Template"]!["Type"], "a new template gets EasyEngine's defaults");
            Eq(80.0, s["Template"]!["Builder"]!["mechanisms"]![0]!["params"]!["speed"]!.GetValue<double>());
            Eq("enemy", (string?)s["Template"]!["Builder"]!["tags"]![0]);
            True(root["Builder"]?["extra"]?["prefabs"] == null, "prefabs no longer travel in Builder.extra");
            var q = EasyLink.FromEeproj(saved, "x");
            var qs = q.Prefabs.Single();
            Eq("Slime", qs.Name); Eq("🟢", qs.Icon); Eq(40.0, qs.Template.Width); Eq("Patrol", qs.Template.Mechanisms[0].Name);
            Eq(saved, EasyLink.ToEeproj(q, saved), "and again: nothing drifts");
            InTemp(tmp =>
            {
                EasyLink.SaveFromEeproj(saved, tmp);
                var f = Project.Load(tmp);
                Eq("Slime", f.Prefabs.Single().Name);
                Eq("Patrol", f.Prefabs[0].Template.Mechanisms[0].Name);
                var ee2 = Parse(EasyLink.ToEeprojFromFolder(tmp));
                Eq("Slime", (string?)ee2["Prefabs"]![0]!["Name"]);
            });
        });

        test("prefabs from the first link version (Builder.extra.prefabs) are read and moved to Prefabs", () =>
        {
            var root = Parse(Synthetic());
            root["Builder"] = new JsonObject
            {
                ["linkVersion"] = 1,
                ["extra"] = new JsonObject { ["prefabs"] = new JsonArray(new JsonObject { ["name"] = "Bat", ["icon"] = "B", ["category"] = "General", ["template"] = new JsonObject { ["name"] = "Bat", ["width"] = 20, ["height"] = 20 } }) },
            };
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var p = EasyLink.FromEeproj(json, "x");
            Eq("Player,Bat", string.Join(",", p.Prefabs.Select(x => x.Name)));
            var back = Parse(EasyLink.ToEeproj(p, json));
            Eq("Player,Bat", string.Join(",", back["Prefabs"]!.AsArray().Select(x => (string?)x!["Name"])));
            True(back["Builder"] == null || back["Builder"]!["extra"] == null, "the old place is emptied");
        });

        test("the Builder block survives an EasyEngine-like load and save that keeps unknown properties", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var hero = p.Scenes[0].Objects.First(o => o.Name == "Hero");
            hero.Mechanisms.Add(new SceneMechanism { Name = "Blink", Params = { ["speed"] = 2.5, ["on"] = true, ["who"] = "player" } });
            hero.Tags.Add("hero");
            p.Mechanisms["MyThing"] = "mechanism MyThing\n    when start\n        print \"hi\"\n";
            p.Scenes[0].TileKinds = [new TileKind { Name = "stone" }];
            string saved = EasyLink.ToEeproj(p, json);
            // EasyEngine: typed load ([JsonExtensionData] keeps what it doesn't know), an edit, its own save.
            var ee = JsonSerializer.Deserialize<EeProject>(saved)!;
            ee.Scenes[0].Layers[2].Objects.First(o => o.Name == "Ground").X = 42;
            string resaved = JsonSerializer.Serialize(ee, new JsonSerializerOptions { WriteIndented = true });
            var q = EasyLink.FromEeproj(resaved, "x");
            var h2 = q.Scenes[0].Objects.First(o => o.Name == "Hero");
            Eq("Blink", h2.Mechanisms[0].Name);
            Eq(2.5, h2.Mechanisms[0].Params["speed"]); Eq(true, h2.Mechanisms[0].Params["on"]); Eq("player", h2.Mechanisms[0].Params["who"]);
            True(h2.Tags.Contains("hero"), "tags");
            True(q.Mechanisms.ContainsKey("MyThing"), "project mechanisms");
            Eq("stone", q.Scenes[0].TileKinds![0].Name);
            Eq(42.0, q.Scenes[0].Objects.First(o => o.Name == "Ground").X);
            Eq("Platformer", EasyLink.EasyBehaviors(h2)[0]);
        });

        test("pictures and sounds resolve in EasyEngine's Assets folders", () =>
        {
            InTemp(dir =>
            {
                File.WriteAllText(Path.Combine(dir, "Game.eeproj"), Synthetic());
                Directory.CreateDirectory(Path.Combine(dir, "Assets", "sprites"));
                Directory.CreateDirectory(Path.Combine(dir, "Assets", "sounds"));
                File.WriteAllBytes(Path.Combine(dir, "Assets", "sprites", "hero.png"), [0]);
                File.WriteAllBytes(Path.Combine(dir, "Assets", "sounds", "chop.wav"), [0]);
                True(EasyLink.IsEasyFolder(dir) && EasyLink.IsProjectFolder(dir), "an EasyEngine folder");
                Eq(Path.Combine(dir, "Game.eeproj"), EasyLink.ProjectFile(dir));
                Eq(Path.Combine(dir, "Assets", "sprites"), Images.Folder(dir));
                Eq(Path.Combine(dir, "Assets", "sounds"), Sounds.Folder(dir));
                Eq(Path.Combine(dir, "Assets", "sprites", "hero.png"), Images.Resolve(dir, "Assets\\sprites\\hero.png"));
                Eq(Path.Combine(dir, "Assets", "sprites", "hero.png"), Images.Resolve(dir, "hero.png"));
                Eq(Path.Combine(dir, "Assets", "sounds", "chop.wav"), Sounds.Resolve(dir, "Assets\\sounds\\chop.wav"));
                Eq(Path.Combine(dir, "Assets", "sounds", "chop.wav"), Sounds.Resolve(dir, "chop"));
                Eq("hero.png", Images.List(dir).Single());
                Eq(null, Images.Resolve(dir, "..\\..\\Windows\\win.ini"));
                Eq(null, EasyLink.ResolveRelative(dir, "C:\\Windows\\notepad.exe", Images.IsImage));
                // A BuilderEngine folder is untouched by all this.
                string be = Path.Combine(dir, "be");
                Project.CreateEmpty().Save(be);
                Eq(Path.Combine(be, "images"), Images.Folder(be));
                True(!EasyLink.IsEasyFolder(be), "a BuilderEngine folder isn't an EasyEngine one");
            });
        });

        test("Prefab and animations: common fields when SceneObject has them, kept in easy otherwise", () =>
        {
            string json = Synthetic();
            var p = EasyLink.FromEeproj(json, "x");
            var hero = p.Scenes[0].Objects.First(o => o.Name == "Hero");
            var prefab = typeof(SceneObject).GetProperty("Prefab");
            var anims = typeof(SceneObject).GetProperty("Animations");
            if (prefab != null && anims != null)
            {
                Eq("Player", prefab.GetValue(hero) as string);
                Eq(1, (anims.GetValue(hero) as System.Collections.IList)?.Count ?? 0, "animations read");
                True(hero.Easy!["Animations"] == null && hero.Easy["Prefab"] == null, "not also in easy");
                prefab.SetValue(hero, "Enemy");
                (anims.GetValue(hero) as System.Collections.IList)?.Clear();
                var back = Parse(EasyLink.ToEeproj(p, json));
                Eq("Enemy", (string?)FindEe(back, "Hero")["Prefab"]);
                Eq(0, FindEe(back, "Hero")["Animations"]!.AsArray().Count, "cleared animations are an empty list, never null");
                Eq("Assets\\sprites\\w1.png", (string?)FindEe(Parse(json), "Hero")["Animations"]![0]!["Frames"]![0]);
            }
            else
            {
                Eq("Player", (string?)hero.Easy!["Prefab"]);
                Eq(1, hero.Easy["Animations"]!.AsArray().Count);
                Eq("walk", (string?)hero.Easy["DefaultAnimation"]);
            }
        });

        test("optional fields convert their keys (Animations ⇄ animations)", () =>
        {
            var ee = JsonNode.Parse("""[{"Name":"walk","Frames":["a.png"],"Fps":8,"Loop":true}]""");
            var be = EasyLink.RenameKeys(ee, camel: true)!;
            Eq("walk", (string?)be[0]!["name"]); Eq(8, be[0]!["fps"]!.GetValue<int>());
            True(EasyLink.SameJson(ee, EasyLink.RenameKeys(be, camel: false)), "and back");
        });

        test("SameJson: numbers by value, any key order, null = missing", () =>
        {
            True(EasyLink.SameJson(JsonNode.Parse("{\"a\":1,\"b\":[1,2]}"), JsonNode.Parse("{\"b\":[1.0,2],\"a\":1.00}")), "same");
            True(EasyLink.SameJson(JsonNode.Parse("{\"a\":null}"), JsonNode.Parse("{}")), "null = missing");
            True(!EasyLink.SameJson(JsonNode.Parse("[1,2]"), JsonNode.Parse("[2,1]")), "array order matters");
            True(!EasyLink.SameJson(JsonNode.Parse("{\"a\":\"1\"}"), JsonNode.Parse("{\"a\":1}")), "text isn't a number");
        });

        test("live link: outside writes are debounced into one change; our own writes are echoes", () =>
        {
            InTemp(dir =>
            {
                string file = Path.Combine(dir, "Game.eeproj");
                File.WriteAllText(file, "{}");
                int changes = 0;
                using var gotOne = new AutoResetEvent(false);
                using var link = new LiveLink(file, 120).Start();
                link.Changed += () => { Interlocked.Increment(ref changes); gotOne.Set(); };
                for (int i = 0; i < 5; i++) { File.WriteAllText(file, "{\"n\":" + i + "}"); Thread.Sleep(15); }
                True(gotOne.WaitOne(3000), "an outside change is reported");
                Thread.Sleep(400);
                Eq(1, changes, "a burst of writes is one change");
                link.Write("{\"mine\":1}");
                Thread.Sleep(500);
                Eq(1, changes, "our own atomic write is ignored");
                File.WriteAllText(file, "{\"mine\":1}");
                Thread.Sleep(500);
                Eq(1, changes, "the same content written again is ignored");
                True(!link.ChangedOnDisk(), "nothing pending");
                File.WriteAllText(file, "{\"theirs\":2}");
                True(gotOne.WaitOne(3000), "a second outside change");
                Eq(2, changes);
                link.BeginWrite();
                File.WriteAllText(file, "{\"half\":");
                Thread.Sleep(300);
                File.WriteAllText(file, "{\"saved\":3}");
                link.EndWrite();
                Thread.Sleep(500);
                Eq(2, changes, "events during our own multi-file save are ignored");
                File.WriteAllText(Path.Combine(dir, "other.txt"), "x");
                Thread.Sleep(400);
                Eq(2, changes, "other files don't count");
            });
        });

        test("live link on a project.json folder: mechanism files count, two windows see each other", () =>
        {
            InTemp(dir =>
            {
                var p = Project.CreateDemo();
                p.Save(dir);
                using var gotOne = new AutoResetEvent(false);
                using var windowB = new LiveLink(Path.Combine(dir, Project.FileName), 120).Start();
                using var windowA = new LiveLink(Path.Combine(dir, Project.FileName), 120).Start();
                int bChanges = 0, aChanges = 0;
                windowB.Changed += () => { Interlocked.Increment(ref bChanges); gotOne.Set(); };
                windowA.Changed += () => Interlocked.Increment(ref aChanges);
                True(windowA.Matters(Path.Combine(dir, "mechanisms", "Intro.bs")) && !windowA.Matters(Path.Combine(dir, "sounds", "a.wav")), "what counts");
                // Window A saves (several files) and remembers its echo.
                p.Mechanisms["Intro"] += "\n# edited\n";
                windowA.BeginWrite();
                p.Save(dir);
                windowA.EndWrite();
                True(gotOne.WaitOne(3000), "window B sees window A's save");
                Thread.Sleep(400);
                Eq(1, bChanges); Eq(0, aChanges, "window A ignores its own save");
                var q = Project.Load(dir);
                True(q.Mechanisms["Intro"].Contains("# edited"), "B reads the new mechanism");
            });
        });

        test("a big project (5000 objects) converts in reasonable time", () =>
        {
            var objs = Enumerable.Range(1, 5000).Select(i => Obj(i, "Box " + i, i % 100 * 40, i / 100 * 40, 32, 32, 0, i % 3 == 0 ? "Assets\\sprites\\b.png" : null, "Solid")).ToArray();
            var root = Parse(Synthetic());
            root["Scenes"]![1]!["Layers"]!.AsArray().Add(Layer("Many", "Custom", false, objs.Select(o => { o["Id"] = o["Id"]!.GetValue<int>() + 100; return o; }).ToArray()));
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var sw = Stopwatch.StartNew();
            var p = EasyLink.FromEeproj(json, "x");
            long read = sw.ElapsedMilliseconds;
            p.Scenes[1].Objects[^1].X += 8;
            string back = EasyLink.ToEeproj(p, json);
            long total = sw.ElapsedMilliseconds;
            Console.WriteLine($"        (5000 objects: read {read} ms, read + write {total} ms)");
            Eq(5009, p.Scenes.Sum(s => s.Objects.Count));
            True(total < 8000, $"too slow: {total} ms");
            var b = Parse(back);
            Eq(5000, b["Scenes"]![1]!["Layers"]![1]!["Objects"]!.AsArray().Count);
        });
    }
}
