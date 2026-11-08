using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

// EasyEngine tilemaps ⇄ BuilderEngine tile layers (docs/EASYENGINE-LINK.md, "Tilemaps").
//
// An EasyEngine tilemap is an object with a "Tilemap" behavior: Nums.tileSize, Texts.palette (a JSON list of tiles:
// colour, solid, picture or animated frames) and Texts.data ({"cols","rows","cells"}, 1 = the palette's first tile),
// its grid's top-left corner at the object's X/Y. Reading, each one becomes a BuilderEngine tile layer of its scene (not
// an object): same place, cell size and cells, its palette's tiles mapped to the scene's tile kinds (one kind per
// different look, shared by every layer). The layer keeps the whole object as BuilderEngine read it in TileLayer.Easy.
// Saving turns each layer back into that object, in its layer and draw order (the writer sees it as one more object),
// and writes the grid / palette only when the cells BuilderEngine has differ from what it would read from the file.
// A layer made in BuilderEngine becomes a new EasyEngine Tilemap object.
public static partial class EasyLink
{
    public const string TilemapKind = "Tilemap";

    /// <summary>The object's EasyEngine Tilemap behavior, or null when it isn't a tilemap.</summary>
    internal static JsonObject? TilemapBehavior(JsonObject eeObj) =>
        Get(eeObj, "Behaviors") is JsonArray arr
            ? arr.OfType<JsonObject>().FirstOrDefault(b => Text(b, "Kind", "").Equals(TilemapKind, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>A tile layer that is an EasyEngine Tilemap object (it has that object's EasyEngine data).</summary>
    public static bool IsEasyTilemap(TileLayer l) => l.Easy is JsonObject e && Get(e, EasyKey) is JsonObject;

    // ---------------- EasyEngine's palette and grid (the same reading as EasyEngine's TilePalette / BuildTilemap) ----------------

    /// <summary>One palette tile. <see cref="Pictures"/>: none (a colour), one (a still picture) or more (animated, at <see cref="Fps"/>).</summary>
    sealed record EeTile(string Color, bool Solid, List<string> Pictures, double Fps);

    /// <summary>The palette's tiles (only its objects count, like EasyEngine) and the parsed list (null when it can't be read).</summary>
    static (List<EeTile> Tiles, JsonArray? Raw) ParsePalette(string? text)
    {
        var tiles = new List<EeTile>();
        if (string.IsNullOrWhiteSpace(text)) return (tiles, null);
        JsonArray? arr;
        try { arr = JsonNode.Parse(text, null, DocOptions) as JsonArray; }
        catch (JsonException) { return (tiles, null); }
        if (arr == null) return (tiles, null);
        foreach (var e in arr.OfType<JsonObject>())
        {
            string color = Get(e, "color") is JsonValue cv && cv.TryGetValue<string>(out var c) ? c : "#888888";
            bool solid = Get(e, "solid") switch
            {
                JsonValue v when v.TryGetValue<bool>(out var b) => b,
                JsonValue v when v.TryGetValue<string>(out var s) => s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || Project.TryNumber(s, out var sn) && sn >= 0.5,
                JsonValue v when TryNumber(v, out var d) => d >= 0.5,
                _ => false,
            };
            var frames = Get(e, "frames") is JsonArray fr
                ? fr.Select(f => f is JsonValue fv && fv.TryGetValue<string>(out var fs) ? fs : "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                : [];
            string? image = Get(e, "image") is JsonValue iv && iv.TryGetValue<string>(out var im) && !string.IsNullOrWhiteSpace(im) ? im : null;
            double fps = Get(e, "fps") is JsonValue pv && (TryNumber(pv, out var fd) || pv.TryGetValue<string>(out var ps) && Project.TryNumber(ps, out fd)) ? Math.Clamp(fd, 1, 60) : 8;
            var pics = frames.Count > 0 ? frames : image != null ? [image] : new List<string>();
            tiles.Add(new EeTile(color, solid, pics, fps));
        }
        return (tiles, arr);
    }

    /// <summary>The grid: columns, rows and one palette number per cell (0, 0 and none when it can't be read).</summary>
    static (int Cols, int Rows, int[] Cells) ParseData(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0, []);
        try
        {
            if (JsonNode.Parse(text, null, DocOptions) is not JsonObject d) return (0, 0, []);
            int cols = (int)Math.Clamp(Number(d, "cols", 0), 0, 10000), rows = (int)Math.Clamp(Number(d, "rows", 0), 0, 10000);
            if (cols == 0 || rows == 0 || (long)cols * rows > TileLayer.MaxCells) return (0, 0, []);
            var cells = new int[cols * rows];
            if (Get(d, "cells") is JsonArray arr)
                for (int i = 0; i < arr.Count && i < cells.Length; i++)
                    if (TryNumber(arr[i], out var v)) cells[i] = (int)Math.Clamp(v, int.MinValue, int.MaxValue);
            return (cols, rows, cells);
        }
        catch (JsonException) { return (0, 0, []); }
    }

    /// <summary>EasyEngine's grid as text, the way it writes it: {"cols":30,"rows":17,"cells":[…]}.</summary>
    static string DataText(int cols, int rows, int[] cells)
    {
        var sb = new StringBuilder(cells.Length * 2 + 40);
        sb.Append("{\"cols\":").Append(cols.ToString(CultureInfo.InvariantCulture))
          .Append(",\"rows\":").Append(rows.ToString(CultureInfo.InvariantCulture)).Append(",\"cells\":[");
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(cells[i].ToString(CultureInfo.InvariantCulture));
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>The grid EasyEngine makes for a map with no cells yet: enough to reach the scene's right and bottom edges (TileGrid.Fit).</summary>
    static (int Cols, int Rows) FitDims(double ts, double x, double y, double sceneW, double sceneH) =>
        (Math.Clamp((int)Math.Ceiling((sceneW - x) / ts - 1e-6), 1, 1000), Math.Clamp((int)Math.Ceiling((sceneH - y) / ts - 1e-6), 1, 1000));

    // ---------------- One look = one kind ----------------

    static string PicName(string p)
    {
        string s = p.Trim().Replace('\\', '/');
        return s[(s.LastIndexOf('/') + 1)..].ToLowerInvariant();
    }

    /// <summary>What a tile looks like and does in EasyEngine: colour, solid, pictures (by file name), speed when animated.</summary>
    static string LookKey(string color, bool solid, IReadOnlyList<string> pics, double fps) =>
        $"{color.Trim().ToUpperInvariant()}|{(solid ? 1 : 0)}|{string.Join(";", pics.Select(PicName))}|" +
        (pics.Count > 1 ? (fps <= 0 ? 8 : fps).ToString("0.###", CultureInfo.InvariantCulture) : "");

    static string LookKey(EeTile t) => LookKey(t.Color, t.Solid, t.Pictures, t.Fps);

    static List<string> KindPictures(TileKind k) => k.Animated ? k.Frames! : k.Image.Length > 0 ? [k.Image] : [];

    static string LookKey(TileKind k) => LookKey(k.Color, k.Solid, KindPictures(k), k.Fps);

    /// <summary>
    /// A scene's tile kinds while reading its tilemaps: palette tiles that look the same share one kind. Starts from the kinds
    /// BuilderEngine saved for the scene (<c>Builder.extra.tileKinds</c>: names, hardness, drops…), so they come back.
    /// </summary>
    sealed class KindTable
    {
        public readonly List<TileKind> Kinds = [];
        readonly Dictionary<string, int> _byKey = [];
        readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        public KindTable(IEnumerable<TileKind>? stored)
        {
            foreach (var k in stored ?? []) Add(k);
        }

        void Add(TileKind k)
        {
            Kinds.Add(k);
            _names.Add(k.Name);
            _byKey.TryAdd(LookKey(k), Kinds.Count);
        }

        /// <summary>The kind number for a palette tile (a new kind when no kind looks like it).</summary>
        public int IdFor(EeTile t, bool breakable, string drop, bool stripImagesFolder)
        {
            if (_byKey.TryGetValue(LookKey(t), out var id)) return id;
            var pics = t.Pictures.Select(p => stripImagesFolder ? StripImagesFolder(p) : p).ToList();
            var k = new TileKind
            {
                Name = NewName(pics.Count > 0 ? Path.GetFileNameWithoutExtension(PicName(pics[0])) : ""),
                Color = t.Color, Image = pics.Count > 0 ? pics[0] : "", Solid = t.Solid, OneWay = false,
                Breakable = breakable, Drop = drop.Trim().Length > 0 ? drop.Trim() : "none",
            };
            if (pics.Count > 1) { k.Frames = pics; k.Fps = t.Fps; }
            Add(k);
            return Kinds.Count;
        }

        string NewName(string stem)
        {
            if (stem.Length > 0 && !_names.Contains(stem)) return stem;
            string b = stem.Length > 0 ? stem : "tile";
            for (int n = stem.Length > 0 ? 2 : 1; ; n++)
                if (!_names.Contains($"{b} {n}")) return $"{b} {n}";
        }
    }

    // ---------------- Reading (.eeproj -> tile layers) ----------------

    /// <summary>
    /// Where a tilemap is drawn in BuilderEngine (which draws tile layers behind or in front of all objects): in front when
    /// its EasyEngine layer is a world layer above the first Base layer (not Background / Sky), else behind.
    /// </summary>
    static bool FrontFor(List<JsonObject> eeLayers, int li)
    {
        if (li < 0 || li >= eeLayers.Count || IsScreen(eeLayers[li])) return false;
        if (Kind(eeLayers[li]) is "Background" or "Sky") return false;
        int baseAt = eeLayers.FindIndex(l => !IsScreen(l) && Kind(l) == "Base");
        return baseAt >= 0 && li > baseAt;
    }

    /// <summary>
    /// A scene's tilemaps (in draw order) as its <c>tileKinds</c> / <c>tileLayers</c>. Tile maps an earlier link version kept
    /// in <c>Builder.extra.tileLayers</c> come back too (as layers made in BuilderEngine: they become Tilemap objects on save).
    /// </summary>
    static void TilesToBe(JsonObject be, JsonObject eeScene, List<JsonObject> eeLayers, List<(JsonObject Ee, JsonObject Be, int Layer)> maps,
        JsonObject? builder, bool stripImagesFolder)
    {
        var extra = builder != null ? Get(builder, "extra") as JsonObject : null;
        var stored = extra != null && Get(extra, "tileKinds") is JsonArray sk ? sk.OfType<JsonObject>().Select(TileKind.FromNode).ToList() : null;
        var oldLayers = extra != null && Get(extra, "tileLayers") is JsonArray ol ? ol.OfType<JsonObject>().ToList() : null;
        if (maps.Count == 0 && stored == null && oldLayers == null) return;
        var table = new KindTable(stored);
        double sw = Number(eeScene, "Width", 960), sh = Number(eeScene, "Height", 540);
        var layers = new JsonArray();
        foreach (var (ee, beo, li) in maps)
            layers.Add(JsonSerializer.SerializeToNode(ReadTilemap(ee, beo, li, eeLayers, table, sw, sh, stripImagesFolder), Project.WriteOptions));
        foreach (var l in oldLayers ?? []) layers.Add(l.DeepClone());
        be["tileKinds"] = JsonSerializer.SerializeToNode(table.Kinds, Project.WriteOptions);
        be["tileLayers"] = layers;
    }

    /// <summary>One EasyEngine tilemap as a tile layer (<paramref name="beo"/>: the object as BuilderEngine reads it, kept in <see cref="TileLayer.Easy"/>).</summary>
    static TileLayer ReadTilemap(JsonObject ee, JsonObject beo, int li, List<JsonObject> eeLayers, KindTable table, double sceneW, double sceneH, bool stripImagesFolder)
    {
        var bh = TilemapBehavior(ee)!;
        var nums = Get(bh, "Nums") as JsonObject ?? new JsonObject();
        var texts = Get(bh, "Texts") as JsonObject ?? new JsonObject();
        double ts = Math.Max(4, Number(nums, "tileSize", 32));
        bool breakable = Number(nums, "breakable", 0) >= 0.5;
        var (tiles, _) = ParsePalette(Text(texts, "palette", ""));
        var ids = tiles.Select(t => table.IdFor(t, breakable, Text(texts, "drop", ""), stripImagesFolder)).ToArray();
        var (cols, rows, cells) = ParseData(Text(texts, "data", ""));
        double x = Number(ee, "X", 0), y = Number(ee, "Y", 0);
        if (cols == 0) { (cols, rows) = FitDims(ts, x, y, sceneW, sceneH); cells = new int[cols * rows]; }

        var layer = new TileLayer(cols, rows, ts, Text(beo, "name", TilemapKind)) { X = x, Y = y }; // the name the object reads as (so saving keeps it)
        int lc = layer.Columns;
        for (int r = 0; r < Math.Min(rows, layer.Rows); r++)
            for (int c = 0; c < Math.Min(cols, lc); c++)
            {
                int v = cells[r * cols + c];
                if (v >= 1 && v <= ids.Length) layer.Cells[r * lc + c] = (ushort)ids[v - 1];
            }
        layer.Touch();
        // BuilderEngine's own settings of the layer, kept in the object's Builder.extra (only when they differ from the defaults).
        layer.Front = TakeFlag(beo, "tileFront") ?? FrontFor(eeLayers, li);
        layer.Solid = TakeFlag(beo, "tileSolid") ?? true;
        layer.Easy = beo;
        return layer;
    }

    /// <summary>Takes a yes/no key out of an object (null when it isn't there).</summary>
    static bool? TakeFlag(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v)) return null;
        o.Remove(key);
        return v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : null;
    }

    // ---------------- Ids (FixIds) ----------------

    static int LayerId(TileLayer l) => l.Easy is JsonObject e ? IdOfBe(e) : 0;

    static int IdOfBe(JsonObject beo)
    {
        double d = Number(beo, "id", 0);
        return d >= 1 && d <= int.MaxValue && d == Math.Floor(d) ? (int)d : 0;
    }

    static void SetLayerId(TileLayer l, int id)
    {
        if (l.Easy is JsonObject e) e["id"] = id;
        else l.Easy = new JsonObject { ["id"] = id };
    }

    // ---------------- Writing (tile layers -> Tilemap objects) ----------------

    sealed partial class EeWriter
    {
        /// <summary>The Tilemap object each layer is while the scene is written (the writer sees them as objects).</summary>
        readonly Dictionary<SceneObject, TileLayer> _tileOf = new(ReferenceEqualityComparer.Instance);
        Scene? _tileScene;

        bool StripPictures => defaultPictureFolder == Project.ImagesFolder;

        /// <summary>
        /// The scene with its tile layers as Tilemap objects (and their BuilderEngine JSON), each where it was in the
        /// previous file's draw order (a new one: at the end of its layer).
        /// </summary>
        (Scene Scene, JsonObject Be) WithTilemapObjects(Scene s, JsonObject beScene, JsonObject node, JsonObject? ps)
        {
            _tileOf.Clear();
            _tileScene = s;
            if (s.TileLayers is not { Count: > 0 } tileLayers) return (s, beScene);
            if (Get(node, "Layers") is not JsonArray)
                node[KeyIn(node, "Layers")] = new JsonArray(NewLayer("Background", "Background", false), NewLayer("Base", "Base", false), NewLayer("UI", "UI", true));
            var eeLayers = Layers(node);

            var objs = new List<SceneObject>(s.Objects);
            var beObjs = (Get(beScene, "objects") as JsonArray ?? []).Select(n => n?.DeepClone() ?? new JsonObject()).ToList();
            while (beObjs.Count < objs.Count) beObjs.Add(new JsonObject());
            while (beObjs.Count > objs.Count) beObjs.RemoveAt(beObjs.Count - 1);

            var prevOrder = ps == null ? [] : Layers(ps).SelectMany(l => InDrawOrder(LayerObjects(l))).Select(IdOf).ToList();
            var todo = tileLayers.Select((l, i) => (L: l, I: i, Pos: IsEasyTilemap(l) ? prevOrder.IndexOf(LayerId(l)) : -1))
                .OrderBy(t => t.Pos < 0 ? int.MaxValue : t.Pos).ThenBy(t => t.I).ToList();
            foreach (var (l, _, pos) in todo)
            {
                var (o, beo) = TilemapObject(l, eeLayers);
                int at = InsertAt(objs, o, pos, prevOrder, IsEasyTilemap(l));
                objs.Insert(at, o);
                beObjs.Insert(at, beo);
                _tileOf[o] = l;
            }
            var ws = new Scene { Id = s.Id, Name = s.Name, Width = s.Width, Height = s.Height, Background = s.Background, Objects = objs, Easy = s.Easy, TileKinds = s.TileKinds, TileLayers = s.TileLayers };
            var wbe = beScene.DeepClone().AsObject();
            wbe["objects"] = new JsonArray(beObjs.ToArray());
            return (ws, wbe);
        }

        /// <summary>Where a Tilemap object goes in the scene's list: after the object drawn just before it in the previous file.</summary>
        static int InsertAt(List<SceneObject> objs, SceneObject o, int pos, List<int> prevOrder, bool fromEasy)
        {
            if (pos >= 0)
            {
                for (int k = pos - 1; k >= 0; k--)
                {
                    int id = prevOrder[k];
                    int at = objs.FindIndex(x => x.Id == id && x.Easy != null);
                    if (at >= 0) return at + 1;
                }
                return 0;
            }
            if (!fromEasy) return objs.Count; // made in BuilderEngine: on top of its layer
            // No previous file: by layer, then by the Z it had when it was read.
            int li = LayerOf(o);
            double z = ZHint(o);
            for (int i = 0; i < objs.Count; i++)
            {
                int ol = LayerOf(objs[i]);
                if (ol > li || ol == li && ZHint(objs[i]) > z) return i;
            }
            return objs.Count;
        }

        static int LayerOf(SceneObject o) => o.Easy is JsonObject e && TryNumber(Get(e, LayerKey), out var li) ? (int)li : int.MaxValue;
        static double ZHint(SceneObject o) => o.Easy is JsonObject e && TryNumber(Get(e, "Z"), out var z) ? z : 0;

        /// <summary>A tile layer as the object BuilderEngine would have read (and its JSON for the writer).</summary>
        (SceneObject O, JsonObject Be) TilemapObject(TileLayer l, List<JsonObject> eeLayers)
        {
            JsonObject beo;
            if (IsEasyTilemap(l)) beo = l.Easy!.DeepClone().AsObject();
            else
            {
                // Made in BuilderEngine: a new EasyEngine Tilemap object (FixIds gave the layer its id), behind the objects in
                // the Background layer, or in front in the first world layer above Base.
                int baseAt = eeLayers.FindIndex(x => !IsScreen(x) && Kind(x) == "Base");
                int li = l.Front
                    ? eeLayers.FindIndex(x => !IsScreen(x) && FrontFor(eeLayers, eeLayers.IndexOf(x)))
                    : eeLayers.FindIndex(x => !IsScreen(x) && Kind(x) == "Background");
                if (li < 0) li = baseAt;
                var easy = Without(DefaultEeObject(), ["Prefab", "PrefabAll", "Animations", "DefaultAnimation"]);
                easy["Type"] = TilemapKind;
                easy["Behaviors"] = new JsonArray(NewTilemapBehavior(l.CellSize));
                if (li >= 0) easy[LayerKey] = li;
                beo = new JsonObject
                {
                    ["id"] = LayerId(l), ["name"] = l.Name, ["x"] = l.X, ["y"] = l.Y, ["width"] = 32, ["height"] = 32, ["color"] = "#9A9AA0",
                    ["image"] = "", ["tags"] = new JsonArray(), ["mechanisms"] = new JsonArray(), [EasyKey] = easy,
                };
            }
            beo["name"] = l.Name;
            beo["x"] = l.X;
            beo["y"] = l.Y;
            beo.Remove("tileFront");
            beo.Remove("tileSolid");
            int layerIndex = Get(beo, EasyKey) is JsonObject e && TryNumber(Get(e, LayerKey), out var d) ? (int)d : -1;
            if (l.Front != FrontFor(eeLayers, layerIndex)) beo["tileFront"] = l.Front;
            if (!l.Solid) beo["tileSolid"] = false;
            return (SceneObject.FromNode(JsonNode.Parse(beo.ToJsonString())!.AsObject()), beo); // through text: values set in code (ints) read as numbers
        }

        /// <summary>EasyEngine's Tilemap behavior with its catalog defaults (BehaviorCatalog.cs) and no cells yet.</summary>
        static JsonObject NewTilemapBehavior(double tileSize) => new()
        {
            ["Kind"] = TilemapKind,
            ["Nums"] = new JsonObject
            {
                ["tileSize"] = tileSize, ["breakable"] = 0, ["breakHits"] = 1, ["byBullet"] = 1, ["byMelee"] = 1, ["byDamage"] = 1,
                ["byStomp"] = 0, ["byTouch"] = 0, ["breakParticles"] = 1, ["dropAmount"] = 1,
            },
            ["Texts"] = new JsonObject { ["palette"] = "[]", ["data"] = "", ["requires"] = "", ["drop"] = "" },
        };

        /// <summary>
        /// Writes a tile layer's cells into its Tilemap object (<paramref name="node"/>, as EasyEngine stores it) when they differ
        /// from what BuilderEngine reads from it. A cell that still shows the same tile keeps its number; a kind the palette
        /// doesn't have is added at its end.
        /// </summary>
        void WriteTilemapCells(JsonObject node, TileLayer l)
        {
            if (TilemapBehavior(node) is not { } bh) return; // EasyEngine took the behavior off meanwhile: its file wins
            var nums = Get(bh, "Nums") as JsonObject;
            if (nums == null) bh[KeyIn(bh, "Nums")] = nums = new JsonObject();
            var texts = Get(bh, "Texts") as JsonObject;
            if (texts == null) bh[KeyIn(bh, "Texts")] = texts = new JsonObject();
            double ts = Math.Max(4, Number(nums, "tileSize", 32));
            if (l.CellSize != ts) nums[KeyIn(nums, "tileSize")] = l.CellSize;

            var kinds = _tileScene?.TileKinds ?? [];
            var kindKeys = kinds.Select(LookKey).ToArray();
            string KindKey(int id) => id >= 1 && id <= kindKeys.Length ? kindKeys[id - 1] : "";
            var (tiles, raw) = ParsePalette(Text(texts, "palette", ""));
            var tileKeys = tiles.Select(LookKey).ToList();
            string TileKey(int v) => v >= 1 && v <= tileKeys.Count ? tileKeys[v - 1] : "";
            var (cols, rows, cells) = ParseData(Text(texts, "data", ""));
            bool empty = cols == 0;
            int wc = cols, wr = rows;
            if (empty) (wc, wr) = FitDims(Math.Max(4, l.CellSize), l.X, l.Y, _tileScene?.Width ?? 960, _tileScene?.Height ?? 540);

            var lc = l.Cells;
            bool same = l.Columns == wc && l.Rows == wr;
            for (int i = 0; same && i < lc.Length; i++)
                if (KindKey(lc[i]) != (empty ? "" : TileKey(cells[i]))) same = false;
            if (same) return;

            var added = new List<JsonObject>();
            var byKind = new Dictionary<int, int>();
            int PaletteNumber(int id)
            {
                string key = KindKey(id);
                if (key.Length == 0) return 0;
                if (byKind.TryGetValue(id, out var n)) return n;
                n = tileKeys.IndexOf(key) + 1;
                if (n == 0)
                {
                    added.Add(PaletteEntry(kinds[id - 1]));
                    tileKeys.Add(key);
                    n = tileKeys.Count;
                }
                return byKind[id] = n;
            }
            bool sameGrid = !empty && l.Columns == cols && l.Rows == rows;
            var output = new int[lc.Length];
            for (int i = 0; i < lc.Length; i++)
                output[i] = sameGrid && KindKey(lc[i]) == TileKey(cells[i]) ? cells[i] : PaletteNumber(lc[i]);
            texts[KeyIn(texts, "data")] = DataText(l.Columns, l.Rows, output);
            if (added.Count > 0)
            {
                var arr = raw?.DeepClone().AsArray() ?? [];
                foreach (var a in added) arr.Add(a);
                texts[KeyIn(texts, "palette")] = arr.ToJsonString();
            }
        }

        /// <summary>A tile kind as an EasyEngine palette tile (EasyEngine's key order: color, solid, image, frames, fps).</summary>
        JsonObject PaletteEntry(TileKind k)
        {
            var e = new JsonObject { ["color"] = k.Color, ["solid"] = k.Solid };
            if (k.Animated)
            {
                e["frames"] = new JsonArray(k.Frames!.Select(PicturePath).ToArray());
                e["fps"] = k.Fps <= 0 ? 8 : k.Fps;
            }
            else if (k.Image.Length > 0) e["image"] = PicturePath(k.Image);
            return e;
        }

        /// <summary>
        /// The scene's tile kinds for <c>Builder.extra.tileKinds</c>, or null when reading the written scene's tilemaps gives
        /// exactly these kinds anyway (then nothing is stored, so an unchanged project stays the same text).
        /// </summary>
        JsonNode? StoredTileKinds(Scene s, JsonObject sceneNode)
        {
            if (s.TileKinds is not { Count: > 0 } kinds) return null;
            var table = new KindTable(null);
            foreach (var layer in Layers(sceneNode))
                foreach (var o in InDrawOrder(LayerObjects(layer)))
                    if (TilemapBehavior(o) is { } bh)
                    {
                        var nums = Get(bh, "Nums") as JsonObject ?? new JsonObject();
                        var texts = Get(bh, "Texts") as JsonObject ?? new JsonObject();
                        bool breakable = Number(nums, "breakable", 0) >= 0.5;
                        foreach (var t in ParsePalette(Text(texts, "palette", "")).Tiles) table.IdFor(t, breakable, Text(texts, "drop", ""), StripPictures);
                    }
            var mine = JsonSerializer.SerializeToNode(kinds, Project.WriteOptions);
            return SameJson(mine, JsonSerializer.SerializeToNode(table.Kinds, Project.WriteOptions)) ? null : mine;
        }
    }
}
