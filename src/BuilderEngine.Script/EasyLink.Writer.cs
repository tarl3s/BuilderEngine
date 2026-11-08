using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

public static partial class EasyLink
{
    // EasyEngine's own property order (Model.cs), used when a scene, layer or object is written from scratch.
    static readonly string[] EeProjectOrder =
        ["Name", "AiModel", "ScreenWidth", "ScreenHeight", "Scenes", "Prefabs", "Variables", "Flags", "Texts", "Colors", "Lists", "Theme", "StartScene", "ConsoleOnStart"];
    static readonly string[] EeSceneOrder =
        ["Name", "Locked", "Width", "Height", "BackgroundColor", "AmbientLight", "Variables", "Flags", "Texts", "Colors", "Lists", "Layers"];
    static readonly string[] EeObjectOrder =
        ["Id", "Name", "Type", "Prefab", "PrefabAll", "X", "Y", "Width", "Height", "Rotation", "Color", "SpritePath", "TileImage", "Opacity",
         "BorderColor", "BorderWidth", "CornerRadius", "FontFamily", "Bold", "Animations", "DefaultAnimation", "Physics", "Behaviors",
         "Variables", "Flags", "Texts", "Colors", "Lists", "Group", "Locked", "Active", "Z", "KeepAspect", "Aspect"];

    /// <summary>What EasyEngine writes for an object nobody gave EasyEngine data (a new BuilderEngine object).</summary>
    static JsonObject DefaultEeObject() => new()
    {
        ["Type"] = "Object", ["Prefab"] = null, ["PrefabAll"] = false, ["Rotation"] = 0, ["TileImage"] = false, ["Opacity"] = 1,
        ["BorderColor"] = null, ["BorderWidth"] = 0, ["CornerRadius"] = -1, ["FontFamily"] = null, ["Bold"] = false,
        ["Animations"] = new JsonArray(), ["DefaultAnimation"] = null,
        ["Physics"] = new JsonObject { ["Mode"] = "None", ["GravityScale"] = 1, ["Bounciness"] = 0, ["FitHitbox"] = false },
        ["Behaviors"] = new JsonArray(), ["Variables"] = new JsonObject(), ["Flags"] = new JsonArray(), ["Texts"] = new JsonObject(),
        ["Colors"] = new JsonArray(), ["Lists"] = new JsonObject(), ["Group"] = "", ["Active"] = true, ["Aspect"] = 0,
    };

    static JsonObject DefaultEeScene() => new()
    {
        ["Locked"] = false, ["AmbientLight"] = 1, ["Variables"] = new JsonObject(), ["Flags"] = new JsonArray(),
        ["Texts"] = new JsonObject(), ["Colors"] = new JsonArray(), ["Lists"] = new JsonObject(),
    };

    static JsonObject NewLayer(string name, string kind, bool screen) => new()
    {
        ["Name"] = name, ["Kind"] = kind, ["Visible"] = true, ["ScreenSpace"] = screen, ["Locked"] = false, ["Opacity"] = 1,
        ["Active"] = true, ["Objects"] = new JsonArray(),
    };

    /// <summary>
    /// A new object with the keys in <paramref name="order"/> first (from <paramref name="mapped"/>, else
    /// <paramref name="rest"/>), then the rest of <paramref name="rest"/>, then the rest of <paramref name="mapped"/>.
    /// </summary>
    static JsonObject Ordered(string[] order, JsonObject mapped, JsonObject rest)
    {
        var r = new JsonObject();
        void Take(string k, JsonObject from)
        {
            if (r.ContainsKey(k) || !from.TryGetPropertyValue(k, out var v)) return;
            from.Remove(k);
            r[k] = v;
        }
        foreach (var k in order)
        {
            Take(k, mapped);
            Take(k, rest);
        }
        foreach (var k in rest.Select(p => p.Key).ToList()) Take(k, rest);
        foreach (var k in mapped.Select(p => p.Key).ToList()) Take(k, mapped);
        return r;
    }

    /// <summary>Sets a key only when the value changed: an unchanged field keeps its exact text (and a missing default stays missing).</summary>
    static void Put(JsonObject node, string key, JsonNode? value, bool changed, bool isDefault = false)
    {
        if (node.ContainsKey(key) ? changed : !isDefault) node[key] = value;
    }

    /// <summary>Updates the parts of a "Builder" block, keeping any other key in it; removes the block when it ends up empty.</summary>
    static void PutBuilder(JsonObject node, JsonObject parts, string[] partKeys)
    {
        var old = Get(node, BuilderKey) as JsonObject;
        var b = old?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var k in partKeys)
        {
            if (parts.TryGetPropertyValue(k, out var v)) { parts.Remove(k); b[k] = v; }
            else b.Remove(k);
        }
        if (old != null && SameJson(old, b)) return;
        string key = node.Select(p => p.Key).FirstOrDefault(k => k.Equals(BuilderKey, StringComparison.OrdinalIgnoreCase)) ?? BuilderKey;
        if (b.Count == 0) node.Remove(key);
        else node[key] = b;
    }

    static JsonObject? NonEmpty(JsonObject o) => o.Count == 0 ? null : o;

    /// <summary>BuilderEngine -> .eeproj, merged into the previous file when there is one.</summary>
    sealed partial class EeWriter(Project project, JsonObject? prev, string? folder, string defaultPictureFolder)
    {
        /// <summary>Previous objects by id: the ones of the scene being written, else ids used only once in the whole file.</summary>
        Dictionary<int, JsonObject> _sceneById = [];
        readonly Dictionary<int, JsonObject?> _prevById = [];
        readonly HashSet<JsonObject> _usedScenes = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<JsonObject, string> _readName = new(ReferenceEqualityComparer.Instance);

        string ReadName(JsonObject prevScene) => _readName.TryGetValue(prevScene, out var n) ? n : Text(prevScene, "Name", "Scene 1").Trim();

        static Dictionary<int, JsonObject> ById(IEnumerable<JsonObject> objects)
        {
            var d = new Dictionary<int, JsonObject>();
            foreach (var o in objects) if (IdOf(o) is > 0 and var id) d.TryAdd(id, o);
            return d;
        }

        public JsonObject Write()
        {
            if (prev != null)
                foreach (var o in Scenes(prev).SelectMany(Layers).SelectMany(LayerObjects))
                    if (IdOf(o) is > 0 and var id && !_prevById.TryAdd(id, o)) _prevById[id] = null; // used twice: not safe to guess

            // EasyEngine's new objects get ids no other object has (BuilderEngine numbers objects per scene): fix a copy.
            if (NeedsIdFix(project))
            {
                var copy = Project.FromJson(project.ToJson());
                foreach (var (n, src) in project.Mechanisms) copy.Mechanisms[n] = src;
                FixIds(copy);
                project = copy;
            }

            var be = JsonNode.Parse(project.ToJson())!.AsObject();
            var beScenes = Get(be, "scenes") as JsonArray ?? [];

            JsonObject root;
            if (prev != null)
            {
                root = prev.DeepClone().AsObject();
                string was = Text(prev, "Name", project.Name);
                Put(root, KeyIn(root, "Name"), project.Name, was != project.Name);
            }
            else
            {
                var rest = (project.Easy as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
                root = Ordered(EeProjectOrder, new JsonObject { ["Name"] = project.Name, ["Scenes"] = new JsonArray() }, rest);
            }

            var prevScenes = prev == null ? [] : Scenes(prev).ToList();
            if (prevScenes.Count > 0)
            {
                // The names BuilderEngine gave the previous scenes when it read them (trimmed; a repeated name gets " 2"):
                // a scene whose name is still that one keeps the file's spelling.
                var names = new JsonObject { ["scenes"] = new JsonArray(prevScenes.Select(x => (JsonNode?)new JsonObject { ["name"] = Text(x, "Name", "Scene 1") }).ToArray()) };
                var read = Project.FromJson(names.ToJsonString()).Scenes;
                for (int k = 0; k < prevScenes.Count && k < read.Count; k++) _readName[prevScenes[k]] = read[k].Name;
            }
            var matches = MatchScenes(prevScenes);
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            var scenes = new JsonArray();
            for (int i = 0; i < project.Scenes.Count; i++)
            {
                var s = project.Scenes[i];
                var ps = matches[i];
                if (ps != null && ReadName(ps) != s.Name) renamed[Text(ps, "Name", "")] = s.Name;
                scenes.Add(WriteScene(s, i < beScenes.Count ? beScenes[i] as JsonObject : null, ps));
            }
            root[KeyIn(root, "Scenes")] = scenes;

            // The start scene follows a rename; when it is deleted the first scene starts.
            if (Get(root, "StartScene") is JsonValue sv && sv.TryGetValue<string>(out var start) && project.Scenes.Count > 0)
            {
                string key = KeyIn(root, "StartScene");
                if (renamed.TryGetValue(start, out var now)) root[key] = now;
                else if (prevScenes.Any(x => Text(x, "Name", "") == start) && !matches.Any(m => m != null && Text(m, "Name", "") == start))
                    root[key] = project.Scenes[0].Name; // the start scene was deleted (one that never existed is left alone)
            }
            else if (prev == null && project.Easy == null && project.Scenes.Count > 0) root["StartScene"] = project.Scenes[0].Name;

            root = WritePrefabs(root, be);

            var parts = new JsonObject();
            if (project.Mechanisms.Count > 0)
            {
                var m = new JsonObject();
                foreach (var (name, src) in project.Mechanisms.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) m[name] = src;
                parts["mechanisms"] = m;
            }
            if (NonEmpty(Without(be, BeProjectKeys)) is { } extra) parts["extra"] = extra;
            if (parts.Count > 0)
            {
                var old = Get(root, BuilderKey) as JsonObject;
                parts["linkVersion"] = old != null && Get(old, "linkVersion") is JsonValue lv ? lv.DeepClone() : LinkVersion;
            }
            PutBuilder(root, parts, ["linkVersion", "mechanisms", "extra"]);
            return root;
        }

        /// <summary>
        /// The project's prefabs (<c>Prefabs</c>), merged into the previous list: a prefab BuilderEngine read keeps its
        /// exact JSON except what changed (its template goes through <see cref="WriteObject"/>), one it deleted is removed,
        /// one it made is added at the end. Prefabs BuilderEngine couldn't read (no name, a repeated name) stay as they were.
        /// </summary>
        JsonObject WritePrefabs(JsonObject root, JsonObject be)
        {
            var bePrefabs = Get(be, "prefabs") as JsonArray ?? [];
            var prevArr = prev != null ? Get(prev, "Prefabs") as JsonArray : Get(root, "Prefabs") as JsonArray; // no previous file: an older project.json kept them in easy
            if (prevArr == null && project.Prefabs.Count == 0) return root;
            var used = new bool[project.Prefabs.Count];
            int Match(string name)
            {
                for (int i = 0; i < project.Prefabs.Count; i++)
                    if (!used[i] && project.Prefabs[i].Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
            JsonObject? BeOf(int i) => i < bePrefabs.Count ? bePrefabs[i] as JsonObject : null;

            var output = new JsonArray();
            if (prevArr != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pp in prevArr)
                {
                    if (pp is not JsonObject po) { output.Add(pp?.DeepClone()); continue; }
                    string name = Text(po, "Name", "").Trim();
                    bool readable = name.Length > 0 && seen.Add(name);
                    int i = readable ? Match(name) : -1;
                    if (i >= 0) { used[i] = true; output.Add(WritePrefab(project.Prefabs[i], BeOf(i), po)); }
                    else if (!readable || prev == null) output.Add(po.DeepClone()); // BuilderEngine never saw it (or an older easy list)
                }
            }
            for (int i = 0; i < project.Prefabs.Count; i++)
                if (!used[i]) output.Add(WritePrefab(project.Prefabs[i], BeOf(i), null));

            if (Get(root, "Prefabs") is JsonArray old && SameJson(old, output)) return root; // keep the exact previous text
            bool had = Get(root, "Prefabs") != null;
            root[KeyIn(root, "Prefabs")] = output;
            return had || prev != null ? root : Ordered(EeProjectOrder, new JsonObject(), root);
        }

        JsonObject WritePrefab(Prefab p, JsonObject? beP, JsonObject? prevP)
        {
            var beTemplate = beP != null && Get(beP, "template") is JsonObject bt ? bt : new JsonObject();
            var prevT = prevP != null ? Get(prevP, "Template") as JsonObject : null;
            JsonObject node;
            if (prevP != null)
            {
                node = prevP.DeepClone().AsObject();
                var was = Prefab.FromNode(PrefabToBe(prevP, false)) ?? new Prefab();
                Put(node, KeyIn(node, "Name"), p.Name, was.Name != p.Name, isDefault: was.Name == p.Name);
                Put(node, KeyIn(node, "Icon"), p.Icon, was.Icon != p.Icon, isDefault: was.Icon == p.Icon); // a missing key that still reads the same stays missing
                Put(node, KeyIn(node, "Category"), p.Category, was.Category != p.Category, isDefault: was.Category == p.Category);
            }
            else node = new JsonObject { ["Name"] = p.Name, ["Icon"] = p.Icon, ["Category"] = p.Category };
            var t = WriteObject(p.Template, beTemplate, false, template: true, templatePrev: prevT);
            if (prevT == null || !SameJson(prevT, t)) node[KeyIn(node, "Template")] = t;
            return node;
        }

        /// <summary>The key as the object already spells it (case-insensitive), else as given.</summary>
        static string KeyIn(JsonObject o, string key) =>
            o.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;

        /// <summary>Each BuilderEngine scene's previous scene: same name first, then the one sharing the most object ids.</summary>
        List<JsonObject?> MatchScenes(List<JsonObject> prevScenes)
        {
            var result = new List<JsonObject?>(new JsonObject?[project.Scenes.Count]);
            for (int i = 0; i < project.Scenes.Count; i++)
            {
                string n = project.Scenes[i].Name.Trim();
                var ps = prevScenes.FirstOrDefault(x => !_usedScenes.Contains(x) && ReadName(x).Equals(n, StringComparison.OrdinalIgnoreCase));
                if (ps == null) continue;
                _usedScenes.Add(ps);
                result[i] = ps;
            }
            for (int i = 0; i < project.Scenes.Count; i++)
            {
                if (result[i] != null) continue;
                var ids = project.Scenes[i].Objects.Where(o => o.Easy != null).Select(o => o.Id).ToHashSet();
                if (ids.Count == 0) continue;
                var best = prevScenes.Where(x => !_usedScenes.Contains(x))
                    .Select(x => (Scene: x, Shared: Layers(x).SelectMany(LayerObjects).Count(o => ids.Contains((int)Number(o, "Id", 0)))))
                    .Where(x => x.Shared > 0).OrderByDescending(x => x.Shared).FirstOrDefault();
                if (best.Scene == null) continue;
                _usedScenes.Add(best.Scene);
                result[i] = best.Scene;
            }
            return result;
        }

        JsonObject WriteScene(Scene s, JsonObject? beScene, JsonObject? ps)
        {
            beScene ??= new JsonObject();
            JsonObject node;
            if (ps != null)
            {
                node = ps.DeepClone().AsObject();
                var was = Scene.FromNode(SceneToBe(ps, 1, null, false));
                Put(node, KeyIn(node, "Name"), s.Name, ReadName(ps) != s.Name);
                Put(node, KeyIn(node, "Width"), s.Width, was.Width != s.Width);
                Put(node, KeyIn(node, "Height"), s.Height, was.Height != s.Height);
                Put(node, KeyIn(node, "BackgroundColor"), s.Background, was.Background != s.Background);
            }
            else
            {
                var rest = (s.Easy as JsonObject)?.DeepClone().AsObject() ?? DefaultEeScene();
                var mapped = new JsonObject { ["Name"] = s.Name, ["Width"] = s.Width, ["Height"] = s.Height, ["BackgroundColor"] = s.Background };
                node = Ordered(EeSceneOrder, mapped, rest);
            }
            _sceneById = ps != null ? ById(Layers(ps).SelectMany(LayerObjects)) : [];
            var (withMaps, beWithMaps) = WithTilemapObjects(s, beScene, node, ps); // tile layers are Tilemap objects (EasyLink.Tiles.cs)
            WriteLayers(node, withMaps, beWithMaps, fromPrev: ps != null);

            var parts = new JsonObject();
            var extraNode = Without(beScene, BeSceneKeys);
            if (StoredTileKinds(s, node) is { } tileKinds) extraNode["tileKinds"] = tileKinds;
            if (NonEmpty(extraNode) is { } extra) parts["extra"] = extra;
            PutBuilder(node, parts, ["extra"]);
            return node;
        }

        void WriteLayers(JsonObject scene, Scene s, JsonObject beScene, bool fromPrev)
        {
            string layersKey = KeyIn(scene, "Layers");
            if (Get(scene, "Layers") is not JsonArray layersArr)
            {
                layersArr = [NewLayer("Background", "Background", false), NewLayer("Base", "Base", false), NewLayer("UI", "UI", true)];
                scene[layersKey] = layersArr;
            }
            var layers = layersArr.OfType<JsonObject>().ToList();
            var beObjects = Get(beScene, "objects") as JsonArray ?? [];

            // Which layer each object goes to: its own (easy.layer), or the first Base one; the "ui" tag means a
            // screen-space layer (the first UI one), no "ui" tag means a layer that moves with the camera.
            var target = new JsonObject?[s.Objects.Count];
            var needBase = new List<int>();
            var needUi = new List<int>();
            for (int i = 0; i < s.Objects.Count; i++)
            {
                var o = s.Objects[i];
                bool ui = o.Tags.Contains(UiTag, StringComparer.OrdinalIgnoreCase);
                JsonObject? l = o.Easy is JsonObject e && TryNumber(Get(e, LayerKey), out var li)
                    && li >= 0 && li < layers.Count && li == Math.Floor(li) ? layers[(int)li] : null;
                if (l != null && IsScreen(l) != ui) l = null;
                target[i] = l;
                if (l == null) (ui ? needUi : needBase).Add(i);
            }
            if (needBase.Count > 0)
            {
                var b = layers.FirstOrDefault(l => !IsScreen(l) && Kind(l) == "Base") ?? layers.FirstOrDefault(l => !IsScreen(l) && Kind(l) != "Background");
                if (b == null)
                {
                    b = NewLayer("Base", "Base", false);
                    int at = layers.FindIndex(l => IsScreen(l) && Kind(l) != "Sky");
                    if (at < 0) at = layers.Count;
                    layers.Insert(at, b);
                }
                foreach (var i in needBase) target[i] = b;
            }
            if (needUi.Count > 0)
            {
                var u = layers.FirstOrDefault(l => IsScreen(l) && Kind(l) == "UI") ?? layers.FirstOrDefault(l => IsScreen(l) && Kind(l) != "Sky");
                if (u == null) { u = NewLayer("UI", "UI", true); layers.Add(u); }
                foreach (var i in needUi) target[i] = u;
            }
            if (layers.Count != layersArr.Count)
            {
                // Layers were added: rebuild the array (detach the nodes first).
                layersArr.Clear();
                foreach (var l in layers) layersArr.Add(l);
            }

            foreach (var layer in layers)
            {
                var items = new List<(SceneObject O, JsonObject Be)>();
                for (int i = 0; i < s.Objects.Count; i++)
                    if (ReferenceEquals(target[i], layer)) items.Add((s.Objects[i], i < beObjects.Count && beObjects[i] is JsonObject bo ? bo : new JsonObject()));
                var prevList = fromPrev ? LayerObjects(layer).ToList() : [];
                WriteLayerObjects(layer, items, prevList, IsScreen(layer), fromPrev);
            }
        }

        /// <summary>
        /// A layer's objects in BuilderEngine's order. When the order didn't change (new objects only at the end) the
        /// previous list and Z values stay as they were; otherwise the list follows BuilderEngine and Z values are raised
        /// just enough to keep the order (EasyEngine draws by Z, then list order).
        /// </summary>
        void WriteLayerObjects(JsonObject layer, List<(SceneObject O, JsonObject Be)> items, List<JsonObject> prevList, bool screen, bool fromPrev)
        {
            var prevZ = new Dictionary<int, double>();
            var prevOrder = new List<int>();
            foreach (var p in prevList)
            {
                int id = (int)Number(p, "Id", 0);
                if (prevZ.TryAdd(id, ZOf(p))) prevOrder.Add(id);
            }
            var nodes = items.Select(it => WriteObject(it.O, it.Be, screen)).ToList();
            var beIds = items.Select(it => it.O.Id).ToList();
            var survivors = prevOrder.Where(beIds.Contains).ToList();
            var drawPrev = survivors.OrderBy(id => prevZ[id]).ToList();
            var beOld = beIds.Where(prevZ.ContainsKey).ToList();
            int firstNew = beIds.FindIndex(id => !prevZ.ContainsKey(id));
            bool newAtEnd = firstNew < 0 || beIds.Skip(firstNew).All(id => !prevZ.ContainsKey(id));

            var output = new JsonArray();
            if (fromPrev && drawPrev.SequenceEqual(beOld) && newAtEnd && beOld.Distinct().Count() == beOld.Count)
            {
                double top = survivors.Count == 0 ? 0 : Math.Max(0, survivors.Max(id => prevZ[id]));
                foreach (var id in survivors) output.Add(nodes[beIds.IndexOf(id)]);
                for (int i = 0; i < beIds.Count; i++)
                    if (!prevZ.ContainsKey(beIds[i])) { SetZ(nodes[i], top); output.Add(nodes[i]); }
            }
            else
            {
                double? last = null;
                for (int i = 0; i < nodes.Count; i++)
                {
                    // Without a previous file the Z each object had when it was read (kept in easy) is the starting point.
                    double z = prevZ.TryGetValue(beIds[i], out var pz) ? pz
                        : !fromPrev && items[i].O.Easy is JsonObject e && Get(e, "Z") is { } ez && TryNumber(ez, out var hz) ? hz : last ?? 0;
                    if (last is { } l && z < l) z = l;
                    SetZ(nodes[i], z);
                    last = z;
                    output.Add(nodes[i]);
                }
            }
            if (Get(layer, "Objects") is JsonArray old && SameJson(old, output)) return; // keep the exact previous text
            layer[KeyIn(layer, "Objects")] = output;
        }

        static void SetZ(JsonObject node, double z)
        {
            string key = KeyIn(node, "Z");
            Put(node, key, z, ZOf(node) != z, isDefault: z == 0);
        }

        /// <summary>
        /// One object as EasyEngine stores it. A prefab's template (<paramref name="template"/>) is matched with the previous
        /// prefab's template (<paramref name="templatePrev"/>) instead of by id.
        /// </summary>
        JsonObject WriteObject(SceneObject o, JsonObject beObj, bool screen, bool template = false, JsonObject? templatePrev = null)
        {
            JsonObject node;
            JsonObject? prevObj = null;
            if (template) prevObj = templatePrev;
            else if (o.Easy != null && !_sceneById.TryGetValue(o.Id, out prevObj)) prevObj = _prevById.GetValueOrDefault(o.Id);
            if (prevObj != null)
            {
                node = prevObj.DeepClone().AsObject();
                var was = SceneObject.FromNode(ObjectToBe(prevObj, 0, false, o.Id, false));
                Put(node, KeyIn(node, "Id"), o.Id, Number(prevObj, "Id", 0) != o.Id);
                Put(node, KeyIn(node, "Name"), o.Name, was.Name != o.Name);
                Put(node, KeyIn(node, "X"), o.X, was.X != o.X);
                Put(node, KeyIn(node, "Y"), o.Y, was.Y != o.Y);
                Put(node, KeyIn(node, "Width"), o.Width, was.Width != o.Width);
                Put(node, KeyIn(node, "Height"), o.Height, was.Height != o.Height);
                Put(node, KeyIn(node, "Color"), o.Color, was.Color != o.Color);
                Put(node, KeyIn(node, "SpritePath"), PicturePath(o.Image), was.Image != o.Image, isDefault: o.Image.Length == 0);
                Put(node, KeyIn(node, "Locked"), o.Locked, was.Locked != o.Locked, isDefault: !o.Locked);
                Put(node, KeyIn(node, "KeepAspect"), o.KeepAspect, was.KeepAspect != o.KeepAspect, isDefault: !o.KeepAspect);
                foreach (var f in OptionalFields.Where(f => f.Mapped))
                {
                    var now = f.OrEmpty(Get(beObj, f.Be));
                    var before = f.OrEmpty(Get(prevObj, f.Ee) is { } pv ? f.ToBe(pv) : null);
                    if (!SameJson(now, before)) node[KeyIn(node, f.Ee)] = now == null ? null : f.ToEe(now);
                }
            }
            else
            {
                JsonObject rest;
                if (o.Easy is JsonObject easy)
                {
                    rest = easy.DeepClone().AsObject();
                    foreach (var k in rest.Select(p => p.Key).Where(k => k.Equals(LayerKey, StringComparison.OrdinalIgnoreCase)).ToList()) rest.Remove(k);
                }
                else rest = DefaultEeObject();
                var mapped = new JsonObject
                {
                    ["Id"] = o.Id, ["Name"] = o.Name, ["X"] = o.X, ["Y"] = o.Y, ["Width"] = o.Width, ["Height"] = o.Height,
                    ["Color"] = o.Color, ["SpritePath"] = PicturePath(o.Image), ["Locked"] = o.Locked, ["Z"] = 0, ["KeepAspect"] = o.KeepAspect,
                };
                foreach (var f in OptionalFields.Where(f => f.Mapped))
                    mapped[f.Ee] = f.OrEmpty(Get(beObj, f.Be)) is { } v ? f.ToEe(v) : null;
                node = Ordered(EeObjectOrder, mapped, rest);
            }

            if (!template && _tileOf.TryGetValue(o, out var tileLayer)) WriteTilemapCells(node, tileLayer);
            var parts = new JsonObject();
            if (Get(beObj, "mechanisms") is JsonArray { Count: > 0 } mechs) parts["mechanisms"] = mechs.DeepClone();
            var tags = o.Tags.Where(t => !(screen && t.Equals(UiTag, StringComparison.OrdinalIgnoreCase))).ToList();
            if (tags.Count > 0) parts["tags"] = new JsonArray(tags.Select(t => (JsonNode?)t).ToArray());
            if (NonEmpty(Without(beObj, BeObjectKeys)) is { } extra) parts["extra"] = extra;
            PutBuilder(node, parts, ["mechanisms", "tags", "extra"]);
            return node;
        }

        /// <summary>
        /// A picture as EasyEngine stores it: a path relative to the project folder. A bare name ("hero.png", picked in
        /// BuilderEngine) is found in the folder (images/ or Assets/sprites/), else put in the default picture folder.
        /// </summary>
        JsonNode? PicturePath(string image)
        {
            if (string.IsNullOrWhiteSpace(image)) return null;
            if (image.Contains('/') || image.Contains('\\')) return image.Replace('/', '\\');
            if (folder != null)
                foreach (var dir in new[] { Project.ImagesFolder, SpritesFolder })
                    if (File.Exists(Path.Combine(folder, dir, image))) return Path.Combine(dir, image).Replace('/', '\\');
            return Path.Combine(defaultPictureFolder, image).Replace('/', '\\');
        }
    }
}
