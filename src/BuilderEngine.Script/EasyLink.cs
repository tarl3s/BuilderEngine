using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

/// <summary>
/// The one converter between EasyEngine projects (<c>.eeproj</c>, one JSON file) and BuilderEngine projects
/// (a folder with <c>project.json</c> + <c>mechanisms/*.bs</c>). It works on raw JSON (System.Text.Json.Nodes), so it
/// needs neither EasyEngine's classes nor any UI. The contract is docs/EASYENGINE-LINK.md; EasyEngine uses the same
/// four calls through a binary reference to this library, so their signatures stay stable.
/// <list type="bullet">
/// <item>Common fields (name, position, size, colour, picture, lock, draw order) are mapped both ways.</item>
/// <item>Everything EasyEngine has that BuilderEngine doesn't map lives in an <c>easy</c> property of the project,
/// each scene and each object (an object's layer index is <c>easy.layer</c>).</item>
/// <item>Everything BuilderEngine has that EasyEngine doesn't map lives in a <c>"Builder"</c> property of the
/// <c>.eeproj</c>'s project, scenes and objects.</item>
/// <item>Saving merges into the previous file: whatever wasn't changed is left exactly as it was (same JSON text).</item>
/// </list>
/// </summary>
public static partial class EasyLink
{
    /// <summary>The version of the contract (docs/EASYENGINE-LINK.md), written as <c>Builder.linkVersion</c>.</summary>
    public const int LinkVersion = 1;
    public const string Extension = ".eeproj";
    /// <summary>Where BuilderEngine keeps its own data inside a <c>.eeproj</c> (project, scene and object level).</summary>
    public const string BuilderKey = "Builder";
    /// <summary>Where EasyEngine's unmapped data lives inside a <c>project.json</c> (project, scene and object level).</summary>
    public const string EasyKey = "easy";
    /// <summary>An object's EasyEngine layer index, inside its <c>easy</c> block.</summary>
    public const string LayerKey = "layer";
    /// <summary>BuilderEngine's tag for objects drawn on the screen: EasyEngine's screen-space layers.</summary>
    public const string UiTag = "ui";
    /// <summary>EasyEngine's picture and sound folders (relative to the project folder).</summary>
    public static readonly string SpritesFolder = Path.Combine("Assets", "sprites"), SoundsFolder = Path.Combine("Assets", "sounds");

    // ---------------- The four calls of the contract ----------------

    /// <summary>
    /// Reads an EasyEngine project as a BuilderEngine project (BuilderEngine opening a <c>.eeproj</c>). Layers are
    /// flattened into each scene in draw order; <c>Builder.mechanisms</c> becomes <see cref="Project.Mechanisms"/>.
    /// <paramref name="folder"/> is the folder the <c>.eeproj</c> is in (names the project when the file has no name).
    /// Throws <see cref="JsonException"/> when the text isn't a JSON object.
    /// </summary>
    public static Project FromEeproj(string eeprojJson, string folder) => FromEeproj(eeprojJson, folder, stripImagesFolder: false);

    /// <summary>
    /// Writes a BuilderEngine project as an EasyEngine project (BuilderEngine saving into a <c>.eeproj</c>). With the
    /// previous file's text, the result is merged into it: unmapped EasyEngine data, and every field that didn't change,
    /// stays exactly as it was. Null (or unreadable) previous text builds the file from the project's <c>easy</c> blocks.
    /// </summary>
    public static string ToEeproj(Project project, string? previousEeprojJson) => ToEeproj(project, previousEeprojJson, null);

    /// <summary>
    /// <see cref="ToEeproj(Project, string?)"/> with the project's folder: a picture picked by its bare name ("hero.png")
    /// is written with the folder it really is in (<c>images\hero.png</c> or <c>Assets\sprites\hero.png</c>).
    /// </summary>
    public static string ToEeproj(Project project, string? previousEeprojJson, string? folder)
    {
        JsonObject? prev = null;
        if (!string.IsNullOrWhiteSpace(previousEeprojJson))
            try { prev = ParseObject(previousEeprojJson); }
            catch (JsonException) { prev = null; }
        return new EeWriter(project, prev, folder, SpritesFolder).Write().ToJsonString(EeWriteOptions);
    }

    /// <summary>
    /// Reads a BuilderEngine project folder (<c>project.json</c> + <c>mechanisms/*.bs</c>) as <c>.eeproj</c> text
    /// (EasyEngine opening a BuilderEngine project). Its pictures are written as <c>images\name.png</c>.
    /// </summary>
    public static string ToEeprojFromFolder(string beFolder)
    {
        var p = Project.Load(beFolder);
        return new EeWriter(p, null, beFolder, Project.ImagesFolder).Write().ToJsonString(EeWriteOptions);
    }

    /// <summary>
    /// Saves <c>.eeproj</c> text into a BuilderEngine project folder (EasyEngine saving a BuilderEngine project):
    /// <c>project.json</c> (atomically) and the mechanisms from <c>Builder.mechanisms</c>. When the text carries no
    /// <c>Builder.mechanisms</c> at all, the folder's mechanisms are kept. Throws <see cref="IOException"/> (nothing written)
    /// when the folder has a <c>project.json</c> that can't be read: saving would delete mechanism files it doesn't list.
    /// </summary>
    public static void SaveFromEeproj(string eeprojJson, string beFolder)
    {
        var ee = ParseObject(eeprojJson);
        var p = FromEeproj(ee, beFolder, stripImagesFolder: true);
        bool carriesMechanisms = Get(ee, BuilderKey) is JsonObject b && Get(b, "mechanisms") is JsonObject;
        if (Project.Exists(beFolder))
        {
            // Never save over a folder whose project can't be read: saving deletes the mechanism files it doesn't list.
            Project old;
            try
            {
                if (Project.Parse(File.ReadAllText(Path.Combine(beFolder, Project.FileName))) is not JsonObject)
                    throw new JsonException("project.json isn't a JSON object.");
                old = Project.Load(beFolder);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                throw new IOException($"The BuilderEngine project in \"{beFolder}\" can't be read ({e.Message}), so it wasn't saved over. Fix or move its project.json first.", e);
            }
            if (!carriesMechanisms) foreach (var (n, src) in old.Mechanisms) p.Mechanisms.TryAdd(n, src);
        }
        p.Save(beFolder);
    }

    // ---------------- Helpers for both engines ----------------

    /// <summary>The <c>.eeproj</c> in a folder (the first by name when there are several), or null.</summary>
    public static string? FindEeproj(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        try { return Directory.EnumerateFiles(folder, "*" + Extension).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>An EasyEngine project folder: it has a <c>.eeproj</c> and no <c>project.json</c>.</summary>
    public static bool IsEasyFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && !Project.Exists(folder) && FindEeproj(folder) != null;

    /// <summary>A folder either engine can open: a <c>project.json</c> or a <c>.eeproj</c> in it.</summary>
    public static bool IsProjectFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && (Project.Exists(folder) || FindEeproj(folder) != null);

    /// <summary>The file a project folder is saved in: its <c>project.json</c>, else its <c>.eeproj</c>, else null.</summary>
    public static string? ProjectFile(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : Project.Exists(folder) ? Path.Combine(folder, Project.FileName) : FindEeproj(folder);

    /// <summary>
    /// The folder a project keeps pictures or sounds in: BuilderEngine's own (<paramref name="beFolder"/>) when it
    /// exists or the project isn't an EasyEngine one, else EasyEngine's (<paramref name="easyFolder"/>, e.g. Assets/sprites).
    /// </summary>
    public static string AssetFolder(string projectDir, string beFolder, string easyFolder)
    {
        string be = Path.Combine(projectDir, beFolder);
        return !Directory.Exists(be) && IsEasyFolder(projectDir) ? Path.Combine(projectDir, easyFolder) : be;
    }

    /// <summary>
    /// A path relative to the project folder ("Assets\sprites\hero.png", the way EasyEngine stores pictures and sounds)
    /// that names an existing file of the right kind inside the project folder; null otherwise (a bare name, a missing
    /// file, a path that leaves the folder).
    /// </summary>
    public static string? ResolveRelative(string projectDir, string name, Func<string, bool> isRightKind)
    {
        string clean = name.Trim().Replace('\\', '/');
        if (!clean.Contains('/') || Path.IsPathRooted(clean) || !isRightKind(clean)) return null;
        try
        {
            string root = Path.GetFullPath(projectDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, clean));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The EasyEngine behaviors of an object ("Platformer", "Health"…), from its <c>easy</c> block.</summary>
    public static List<string> EasyBehaviors(SceneObject o)
    {
        var list = new List<string>();
        if (o.Easy is JsonObject e && Get(e, "Behaviors") is JsonArray arr)
            foreach (var b in arr)
                if (b is JsonObject bo && Get(bo, "Kind") is JsonValue k)
                    list.Add(k.TryGetValue<string>(out var s) ? s : k.ToJsonString());
        return list;
    }

    /// <summary>The object has EasyEngine behaviors: BuilderEngine shows them read-only ("Built on EasyEngine").</summary>
    public static bool HasEasyBehaviors(SceneObject o) => o.Easy is JsonObject e && Get(e, "Behaviors") is JsonArray { Count: > 0 };

    /// <summary>
    /// One id space per project: an object made in BuilderEngine (no <c>easy</c> block; BuilderEngine numbers objects
    /// per scene) whose id is used anywhere else in the project gets max(all ids) + 1, like EasyEngine's new objects.
    /// Objects that came from EasyEngine keep their ids unless two share one inside the same scene (then the first
    /// keeps it). Older EasyEngine projects that reuse ids in different scenes are left as they are. Returns the
    /// renumbered objects with their old ids.
    /// </summary>
    public static List<(SceneObject Object, int OldId)> FixIds(Project project)
    {
        var (losers, layerLosers, next) = IdLosers(project);
        var changed = new List<(SceneObject, int)>();
        foreach (var o in losers)
        {
            changed.Add((o, o.Id));
            o.Id = next++;
        }
        // Tile layers are EasyEngine Tilemap objects (EasyLink.Tiles.cs): one made in BuilderEngine gets its object's id now.
        foreach (var l in layerLosers) SetLayerId(l, next++);
        return changed;
    }

    /// <summary>Some ids <see cref="FixIds"/> would change.</summary>
    static bool NeedsIdFix(Project project)
    {
        var (losers, layerLosers, _) = IdLosers(project);
        return losers.Count > 0 || layerLosers.Count > 0;
    }

    /// <summary>The objects and tile layers <see cref="FixIds"/> renumbers, and the first free id.</summary>
    static (List<SceneObject> Objects, List<TileLayer> Layers, int Next) IdLosers(Project project)
    {
        var used = new HashSet<int>();
        var losers = new List<SceneObject>();
        var layerLosers = new List<TileLayer>();
        foreach (var s in project.Scenes)
        {
            var inScene = new HashSet<int>();
            foreach (var o in s.Objects)
                if (o.Easy != null && (o.Id <= 0 || !inScene.Add(o.Id))) losers.Add(o);
            foreach (var l in s.TileLayers ?? [])
                if (IsEasyTilemap(l) && LayerId(l) is var id && (id <= 0 || !inScene.Add(id))) layerLosers.Add(l);
            used.UnionWith(inScene);
        }
        foreach (var s in project.Scenes)
            foreach (var o in s.Objects)
                if (o.Easy == null && (o.Id <= 0 || !used.Add(o.Id))) losers.Add(o);
        foreach (var s in project.Scenes)
            foreach (var l in s.TileLayers ?? [])
                if (!IsEasyTilemap(l) && LayerId(l) is var id && (id <= 0 || !used.Add(id))) layerLosers.Add(l);
        return (losers, layerLosers, Math.Max(1, (used.Count == 0 ? 0 : used.Max()) + 1));
    }

    /// <summary>Writes a file atomically: a temp file next to it, then a replace (a reader never sees half a file).</summary>
    public static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // ---------------- Reading (.eeproj -> project.json shape) ----------------

    static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    /// <summary>The way EasyEngine writes its files (System.Text.Json, indented, default escaping), so an unchanged file comes back identical.</summary>
    static readonly JsonSerializerOptions EeWriteOptions = new() { WriteIndented = true };
    static readonly JsonSerializerOptions BeNodeOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, null, DocOptions) as JsonObject ?? throw new JsonException("The project file isn't a JSON object.");

    static Project FromEeproj(string eeprojJson, string folder, bool stripImagesFolder) =>
        FromEeproj(ParseObject(eeprojJson), folder, stripImagesFolder);

    static Project FromEeproj(JsonObject ee, string folder, bool stripImagesFolder)
    {
        var be = ToBeJson(ee, folder, stripImagesFolder, out var mechanisms);
        var p = Project.FromJson(be.ToJsonString());
        foreach (var (name, src) in mechanisms) p.Mechanisms[name] = src;
        return p;
    }

    /// <summary>The <c>project.json</c> shape of an EasyEngine project (its mechanisms come out apart).</summary>
    internal static JsonObject ToBeJson(JsonObject ee, string? folder, bool stripImagesFolder, out Dictionary<string, string> mechanisms)
    {
        mechanisms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var builder = Get(ee, BuilderKey) as JsonObject;
        if (builder != null && Get(builder, "mechanisms") is JsonObject mechs)
            foreach (var (k, v) in mechs)
                if (v is JsonValue sv && sv.TryGetValue<string>(out var src)) mechanisms[k] = src;

        string fallbackName = string.IsNullOrWhiteSpace(folder) ? "My Game" : Path.GetFileName(folder.TrimEnd('\\', '/'));
        var root = new JsonObject { ["name"] = Text(ee, "Name", fallbackName) };
        var ids = NewIds(ee);
        var scenes = new JsonArray();
        int si = 0;
        foreach (var s in Scenes(ee))
            scenes.Add(SceneToBe(s, ++si, ids, stripImagesFolder));
        root["scenes"] = scenes;
        if (Get(ee, "Prefabs") is JsonArray eePrefabs) root["prefabs"] = PrefabsToBe(eePrefabs, stripImagesFolder);
        root[EasyKey] = Without(ee, ProjectKeys);
        MergeExtra(root, builder);
        MergeOldPrefabs(root, builder);
        return root;
    }

    static JsonObject SceneToBe(JsonObject s, int id, Dictionary<JsonObject, int>? ids, bool stripImagesFolder)
    {
        var be = new JsonObject
        {
            ["id"] = id,
            ["name"] = Text(s, "Name", "Scene 1"),
            ["width"] = Number(s, "Width", 960),
            ["height"] = Number(s, "Height", 540),
            ["background"] = Text(s, "BackgroundColor", "#1A1A20"),
        };
        var objects = new JsonArray();
        var layers = Layers(s);
        var tilemaps = new List<(JsonObject, JsonObject, int)>(); // tilemaps become tile layers (EasyLink.Tiles.cs)
        if (ids != null)
            for (int li = 0; li < layers.Count; li++)
            {
                bool screen = IsScreen(layers[li]);
                foreach (var o in InDrawOrder(LayerObjects(layers[li])))
                {
                    var beo = ObjectToBe(o, li, screen, ids.TryGetValue(o, out var fixedId) ? fixedId : (int)Number(o, "Id", 0), stripImagesFolder);
                    if (TilemapBehavior(o) != null) tilemaps.Add((o, beo, li));
                    else objects.Add(beo);
                }
            }
        be["objects"] = objects;
        if (ids != null) TilesToBe(be, s, layers, tilemaps, Get(s, BuilderKey) as JsonObject, stripImagesFolder);
        var easy = Without(s, SceneKeys);
        if (Get(s, "Layers") is JsonArray)
        {
            var bare = new JsonArray();
            foreach (var l in layers) bare.Add(Without(l, ["Objects"]));
            easy["Layers"] = bare;
        }
        be[EasyKey] = easy;
        MergeExtra(be, Get(s, BuilderKey) as JsonObject);
        return be;
    }

    static JsonObject ObjectToBe(JsonObject o, int layer, bool screen, int id, bool stripImagesFolder)
    {
        var builder = Get(o, BuilderKey) as JsonObject;
        string sprite = Text(o, "SpritePath", "");
        if (stripImagesFolder) sprite = StripImagesFolder(sprite);
        var be = new JsonObject
        {
            ["id"] = id,
            ["name"] = Text(o, "Name", "Object"),
            ["x"] = Number(o, "X", 0),
            ["y"] = Number(o, "Y", 0),
            ["width"] = Number(o, "Width", 64),
            ["height"] = Number(o, "Height", 64),
            ["color"] = Text(o, "Color", "#9A9AA0"),
            ["image"] = sprite,
        };
        if (Flag(o, "Locked")) be["locked"] = true;
        if (Flag(o, "KeepAspect")) be["keepAspect"] = true;
        var tags = new JsonArray();
        if (builder != null && Get(builder, "tags") is JsonArray bt)
            foreach (var t in bt) tags.Add(t?.DeepClone());
        if (screen && !tags.Any(t => t is JsonValue v && v.TryGetValue<string>(out var s) && s.Equals(UiTag, StringComparison.OrdinalIgnoreCase)))
            tags.Add(UiTag);
        be["tags"] = tags;
        be["mechanisms"] = builder != null && Get(builder, "mechanisms") is JsonArray bm ? bm.DeepClone() : new JsonArray();
        foreach (var f in OptionalFields)
            if (f.Mapped && Get(o, f.Ee) is { } v) be[f.Be] = f.ToBe(v);
        var easy = Without(o, ObjectKeys);
        easy[LayerKey] = layer;
        be[EasyKey] = easy;
        MergeExtra(be, builder);
        return be;
    }

    /// <summary>EasyEngine's project prefabs (<c>PrefabDef{Name, Icon, Category, Template}</c>) as BuilderEngine prefabs.</summary>
    static JsonArray PrefabsToBe(JsonArray eePrefabs, bool stripImagesFolder)
    {
        var arr = new JsonArray();
        foreach (var pf in eePrefabs.OfType<JsonObject>()) arr.Add(PrefabToBe(pf, stripImagesFolder));
        return arr;
    }

    /// <summary>One prefab: its template goes through the same object mapping as a scene object (no layer).</summary>
    static JsonObject PrefabToBe(JsonObject pf, bool stripImagesFolder)
    {
        var t = Get(pf, "Template") as JsonObject ?? new JsonObject();
        var template = ObjectToBe(t, 0, false, IdOf(t), stripImagesFolder);
        if (template[EasyKey] is JsonObject e) e.Remove(LayerKey); // a template isn't on any layer
        return new JsonObject
        {
            ["name"] = Text(pf, "Name", "Prefab"),
            ["icon"] = Text(pf, "Icon", "▢"),
            ["category"] = Text(pf, "Category", "General"),
            ["template"] = template,
        };
    }

    /// <summary>
    /// Files written by the first link version kept prefabs made in BuilderEngine in <c>Builder.extra.prefabs</c>:
    /// the ones whose name isn't already an EasyEngine prefab are added.
    /// </summary>
    static void MergeOldPrefabs(JsonObject be, JsonObject? builder)
    {
        if (builder == null || Get(builder, "extra") is not JsonObject extra || Get(extra, "prefabs") is not JsonArray old) return;
        if (be["prefabs"] is not JsonArray now) return;
        var names = new HashSet<string>(now.OfType<JsonObject>().Select(p => Text(p, "name", "").Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var p in old.OfType<JsonObject>())
            if (names.Add(Text(p, "name", "").Trim())) now.Add(p.DeepClone());
    }

    /// <summary>"images\hero.png" in a BuilderEngine folder is just "hero.png" there.</summary>
    static string StripImagesFolder(string path)
    {
        string p = path.Replace('\\', '/');
        string prefix = Project.ImagesFolder + "/";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', prefix.Length) < 0 ? p[prefix.Length..] : path;
    }

    /// <summary>
    /// Which objects need a new id, in file order: when two objects of a scene share an id (two engines made an object
    /// at the same moment) the first keeps it and the later one gets max(all ids) + 1, like a missing id. Ids reused in
    /// different scenes (older EasyEngine projects) are kept.
    /// </summary>
    static Dictionary<JsonObject, int> NewIds(JsonObject ee)
    {
        var all = new HashSet<int>();
        var result = new Dictionary<JsonObject, int>(ReferenceEqualityComparer.Instance);
        var later = new List<JsonObject>();
        foreach (var s in Scenes(ee))
        {
            var seen = new HashSet<int>();
            foreach (var o in Layers(s).SelectMany(LayerObjects))
            {
                int id = IdOf(o);
                if (id <= 0 || !seen.Add(id)) later.Add(o);
            }
            all.UnionWith(seen);
        }
        int next = (all.Count == 0 ? 0 : all.Max()) + 1;
        foreach (var o in later) result[o] = next++;
        return result;
    }

    /// <summary>An object's id; 0 when it has none (or it isn't a positive whole number).</summary>
    static int IdOf(JsonObject o)
    {
        double d = Number(o, "Id", 0);
        return d >= 1 && d <= int.MaxValue && d == Math.Floor(d) ? (int)d : 0;
    }

    // BuilderEngine-only data comes back from the "Builder" block's "extra" (keys BuilderEngine itself already set win).
    static void MergeExtra(JsonObject be, JsonObject? builder)
    {
        if (builder == null || Get(builder, "extra") is not JsonObject extra) return;
        foreach (var (k, v) in extra)
            if (!be.ContainsKey(k)) be[k] = v?.DeepClone();
    }

    // ---------------- Field lists ----------------

    /// <summary>EasyEngine keys mapped to BuilderEngine (or structural): everything else goes to <c>easy</c>.</summary>
    static readonly string[] ProjectKeys = ["Name", "Scenes", "Prefabs", BuilderKey];
    static readonly string[] SceneKeys = ["Name", "Width", "Height", "BackgroundColor", "Layers", BuilderKey];
    static string[] ObjectKeys => _objectKeys ??=
        ["Id", "Name", "X", "Y", "Width", "Height", "Color", "SpritePath", "Locked", "KeepAspect", BuilderKey,
         .. OptionalFields.Where(f => f.Mapped).Select(f => f.Ee)];
    static string[]? _objectKeys;

    /// <summary>BuilderEngine keys mapped to EasyEngine (or structural): everything else goes to <c>Builder.extra</c>.</summary>
    static readonly string[] BeProjectKeys = ["name", "scenes", "prefabs", EasyKey];
    static readonly string[] BeSceneKeys = ["id", "name", "width", "height", "background", "objects", "tileKinds", "tileLayers", EasyKey];
    static string[] BeObjectKeys => _beObjectKeys ??=
        ["id", "name", "x", "y", "width", "height", "color", "image", "locked", "keepAspect", "tags", "mechanisms", EasyKey,
         .. OptionalFields.Where(f => f.Mapped).Select(f => f.Be)];
    static string[]? _beObjectKeys;

    /// <summary>
    /// Fields both engines may have: mapped as common fields when BuilderEngine's <see cref="SceneObject"/> has the
    /// property, otherwise they simply stay in the object's <c>easy</c> block.
    /// </summary>
    sealed record OptionalField(string Ee, string Be, string Property, Func<JsonNode, JsonNode?> ToBe, Func<JsonNode, JsonNode?> ToEe, Func<JsonNode?> Empty)
    {
        public bool Mapped { get; } = typeof(SceneObject).GetProperty(Property) != null;
        /// <summary>BuilderEngine may leave a default value out of its JSON: missing and null read as <see cref="Empty"/>.</summary>
        public JsonNode? OrEmpty(JsonNode? v) => v is null || v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Null ? Empty() : v;
    }

    // SceneObject.Prefab (string?), PrefabAll (bool), Animations (list of {Name, Frames, Fps, Loop}; omitted when empty),
    // DefaultAnimation (string?): the same names as EasyEngine's GameObjectData.
    static readonly OptionalField[] OptionalFields =
    [
        new("Prefab", "prefab", "Prefab", v => v.DeepClone(), v => v.DeepClone(), () => null),
        new("PrefabAll", "prefabAll", "PrefabAll", v => v.DeepClone(), v => v.DeepClone(), () => false),
        new("Animations", "animations", "Animations", v => RenameKeys(v, camel: true), v => RenameKeys(v, camel: false), () => new JsonArray()),
        new("DefaultAnimation", "defaultAnimation", "DefaultAnimation", v => v.DeepClone(), v => v.DeepClone(), () => null),
    ];

    /// <summary>A copy with every object key in camelCase (BuilderEngine) or PascalCase (EasyEngine).</summary>
    public static JsonNode? RenameKeys(JsonNode? n, bool camel)
    {
        switch (n)
        {
            case JsonObject o:
            {
                var r = new JsonObject();
                foreach (var (k, v) in o)
                {
                    string key = k.Length == 0 ? k : (camel ? char.ToLowerInvariant(k[0]) : char.ToUpperInvariant(k[0])) + k[1..];
                    r[key] = RenameKeys(v, camel);
                }
                return r;
            }
            case JsonArray a:
                return new JsonArray(a.Select(x => RenameKeys(x, camel)).ToArray());
            default:
                return n?.DeepClone();
        }
    }

    // ---------------- Small JSON helpers ----------------

    internal static JsonNode? Get(JsonObject o, string name) => Project.Get(o, name);

    internal static string Text(JsonObject o, string name, string def) =>
        Get(o, name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;

    internal static double Number(JsonObject o, string name, double def) =>
        TryNumber(Get(o, name), out var d) ? d : Project.Num(o, name, def);

    /// <summary>A JSON number however it was made (parsed text, or an int/long/double value set in code).</summary>
    internal static bool TryNumber(JsonNode? n, out double d)
    {
        d = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue(out d)) return double.IsFinite(d);
        if (v.TryGetValue(out int i)) { d = i; return true; }
        if (v.TryGetValue(out long l)) { d = l; return true; }
        if (v.TryGetValue(out float f)) { d = f; return double.IsFinite(d); }
        if (v.TryGetValue(out decimal m)) { d = (double)m; return true; }
        if (v.GetValueKind() == JsonValueKind.Number) return double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d);
        return false;
    }

    static bool Flag(JsonObject o, string name) => Get(o, name) is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    internal static IEnumerable<JsonObject> Scenes(JsonObject ee) =>
        Get(ee, "Scenes") is JsonArray a ? a.OfType<JsonObject>() : [];

    internal static List<JsonObject> Layers(JsonObject scene) =>
        Get(scene, "Layers") is JsonArray a ? a.OfType<JsonObject>().ToList() : [];

    internal static IEnumerable<JsonObject> LayerObjects(JsonObject layer) =>
        Get(layer, "Objects") is JsonArray a ? a.OfType<JsonObject>() : [];

    static bool IsScreen(JsonObject layer) => Flag(layer, "ScreenSpace");

    static string Kind(JsonObject layer) => Text(layer, "Kind", "Base");

    static double ZOf(JsonObject o) => Number(o, "Z", 0);

    /// <summary>EasyEngine's drawing order inside a layer: by Z, lowest first; equal Z keeps the list order.</summary>
    static IEnumerable<JsonObject> InDrawOrder(IEnumerable<JsonObject> objects) => objects.OrderBy(ZOf);

    /// <summary>A copy without some keys (case-insensitive), keeping the order of the rest.</summary>
    static JsonObject Without(JsonObject o, IReadOnlyCollection<string> keys)
    {
        var r = new JsonObject();
        foreach (var (k, v) in o)
            if (!keys.Contains(k, StringComparer.OrdinalIgnoreCase)) r[k] = v?.DeepClone();
        return r;
    }

    /// <summary>
    /// Same JSON data: numbers by value, strings exactly, objects by key (any order), arrays in order. Null and a
    /// missing key are the same thing for objects.
    /// </summary>
    public static bool SameJson(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null || IsJsonNull(a) && IsJsonNull(b);
        switch (a)
        {
            case JsonObject ao when b is JsonObject bo:
                foreach (var (k, v) in ao)
                    if (!SameJson(v, bo.TryGetPropertyValue(k, out var bv) ? bv : null)) return false;
                foreach (var (k, v) in bo)
                    if (!ao.ContainsKey(k) && !IsJsonNull(v)) return false;
                return true;
            case JsonArray aa when b is JsonArray ba:
                if (aa.Count != ba.Count) return false;
                for (int i = 0; i < aa.Count; i++) if (!SameJson(aa[i], ba[i])) return false;
                return true;
            case JsonValue av when b is JsonValue bv:
                var ka = av.GetValueKind();
                if (ka != bv.GetValueKind()) return false;
                return ka switch
                {
                    JsonValueKind.Number => TryNumber(av, out var x) && TryNumber(bv, out var y) && x == y,
                    JsonValueKind.String => av.GetValue<string>() == bv.GetValue<string>(),
                    _ => true, // true/false/null: the kind says it all
                };
            default:
                return false;
        }
    }

    static bool IsJsonNull(JsonNode? n) => n is null || n is JsonValue v && v.GetValueKind() == JsonValueKind.Null;

    /// <summary>Used by <see cref="Project.ToJson"/>: adds the project's <c>easy</c> block to its JSON when it has one.</summary>
    internal static string WithEasy(string projectJson, JsonNode? easy, JsonSerializerOptions options)
    {
        if (easy == null) return projectJson;
        var root = JsonNode.Parse(projectJson)!.AsObject();
        root[EasyKey] = easy.DeepClone();
        return root.ToJsonString(options);
    }
}
