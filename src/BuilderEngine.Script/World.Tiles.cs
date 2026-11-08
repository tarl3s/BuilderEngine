using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>The tile map of a playing scene: its kinds (number = place in the list + 1) and its layers.</summary>
public sealed class TileMapState
{
    public List<TileKind> Kinds { get; } = [];
    public List<TileLayer> Layers { get; } = [];
    readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    internal bool[] SolidById = [false], OneWayById = [false];
    /// <summary>The cell being dug and how far (0..1), for the cracks drawn on it.</summary>
    public TileLayer? DigLayer;
    public int DigCol = -1, DigRow = -1;
    public double DigAmount, DigFraction;
    internal double DigTime;

    /// <summary>The kind with this number (1 = the first), or null for 0 / unknown.</summary>
    public TileKind? Kind(int id) => id > 0 && id <= Kinds.Count ? Kinds[id - 1] : null;

    /// <summary>The number of a kind by name; 0 for "", "none", "empty", "air" or an unknown name.</summary>
    public int Id(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        return _ids.TryGetValue(name.Trim(), out var id) ? id : 0;
    }

    public static bool IsEmptyName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().ToLowerInvariant() is "none" or "empty" or "air" or "nothing";

    /// <summary>Adds a kind (or replaces the one with that name). Returns its number.</summary>
    public int Define(TileKind k)
    {
        if (_ids.TryGetValue(k.Name, out var id)) Kinds[id - 1] = k;
        else
        {
            if (Kinds.Count >= ushort.MaxValue - 1) return 0;
            Kinds.Add(k);
            id = Kinds.Count;
            _ids[k.Name] = id;
        }
        Refresh();
        return id;
    }

    internal void Refresh()
    {
        _ids.Clear();
        for (int i = 0; i < Kinds.Count; i++) _ids.TryAdd(Kinds[i].Name, i + 1);
        SolidById = new bool[Kinds.Count + 1];
        OneWayById = new bool[Kinds.Count + 1];
        for (int i = 0; i < Kinds.Count; i++) { SolidById[i + 1] = Kinds[i].Solid; OneWayById[i + 1] = Kinds[i].OneWay; }
        foreach (var l in Layers) l.Touch();
    }

    /// <summary>The first solid layer (the ground), else the first one; null when there are none.</summary>
    public TileLayer? Main
    {
        get
        {
            foreach (var l in Layers) if (l.Solid) return l;
            return Layers.Count > 0 ? Layers[0] : null;
        }
    }

    public TileLayer? Layer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Main;
        foreach (var l in Layers) if (string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    internal bool IsSolid(int id) => (uint)id < (uint)SolidById.Length && SolidById[id];
    internal bool IsOneWay(int id) => (uint)id < (uint)OneWayById.Length && OneWayById[id];
}

/// <summary>
/// Tile maps:
/// <code>
/// make_map 400, 200 with 16              (a grid of 400 × 200 cells of 16 px; 'as "Walls" behind' = a second layer)
/// tile_kind "ore" with "#C0C0C0" hardness 2 drop "ore"     (solid · background · oneway · unbreakable · picture "ore.png" · light 4)
/// set_tile x, y to "dirt"                place_tile "stone" at x, y      break_tile x, y      dig_tile x, y with 2
/// fill_tiles "stone" from x1, y1 to x2, y2                  noise_tiles "none" from "stone" with 0.08, 0.62 below 400
/// random_seed 5                          ('random' and 'noise' repeat the same way)
/// tile_at(x, y)  tile_solid(x, y)  tile_x(x)  tile_y(y)  tile_size()  map_width()  map_height()  ground_y(x)  noise(x, y)
/// when tile broken "stone" at x, y       (kind and drop are set)       when tile placed at x, y
/// </code>
/// Positions are scene pixels (any point inside the cell). 'in "Walls"' picks another layer.
/// </summary>
public sealed partial class World
{
    TileMapState? _tiles;
    /// <summary>The scene's tiles (made empty the first time it's asked for).</summary>
    public TileMapState Tiles => _tiles ??= new TileMapState();
    public bool HasTiles => _tiles != null && _tiles.Layers.Count > 0;
    Noise? _noise;
    int _seed = Environment.TickCount;
    Noise NoiseGen => _noise ??= new Noise(_seed);

    [ModuleInitializer]
    internal static void InstallTiles() => Installers.Add(w =>
    {
        w.Commands["random_seed"] = c =>
        {
            int seed = c.Args.Count > 0 ? (int)Math.Round(c.Number(c.Args[0])) : Environment.TickCount;
            c.World.Seed(seed);
        };
        w.Commands["make_map"] = MakeMapCommand;
        w.Commands["tile_kind"] = TileKindCommand;
        w.Commands["set_tile"] = c =>
        {
            var (l, col, row) = c.World.CellOf(c, c.Args, 0, "set_tile x, y to \"dirt\"");
            string kind = c.Has("to") ? ToText(c.NamedArg("to")) : c.Args.Count > 2 ? ToText(c.Args[2]) : throw c.Error("'set_tile' needs a kind: set_tile x, y to \"dirt\" (or to \"none\" to empty it).");
            c.World.SetCell(l, col, row, c.World.KindId(c, kind));
        };
        w.Commands["place_tile"] = c =>
        {
            if (c.Args.Count == 0 || !c.Has("at")) throw c.Error("'place_tile' needs a kind and where: place_tile \"stone\" at x, y.");
            var (l, col, row) = c.World.CellOf(c, c.Named["at"], 0, "place_tile \"stone\" at x, y");
            c.Locals["placed"] = c.World.PlaceTile(l, col, row, c.World.KindId(c, ToText(c.Args[0])));
        };
        w.Commands["break_tile"] = c =>
        {
            var (l, col, row) = c.World.CellOf(c, c.Args, 0, "break_tile x, y");
            var k = c.World.BreakTile(l, col, row, force: IndexOfWord(c, "force") >= 0);
            c.Locals["broken"] = k?.Name ?? "";
            c.Locals["drop"] = k?.DropItem ?? "";
        };
        w.Commands["dig_tile"] = c =>
        {
            var (l, col, row) = c.World.CellOf(c, c.Args, 0, "dig_tile x, y");
            double power = NamedNumber(c, "with") ?? 1;
            var k = c.World.DigTile(l, col, row, power);
            c.Locals["broken"] = k?.Name ?? "";
            c.Locals["drop"] = k?.DropItem ?? "";
        };
        w.Commands["fill_tiles"] = c =>
        {
            if (c.Args.Count == 0 || !c.Has("from") || !c.Has("to")) throw c.Error("'fill_tiles' needs a kind and two corners: fill_tiles \"stone\" from x1, y1 to x2, y2.");
            var l = c.World.LayerOf(c);
            int id = c.World.KindId(c, ToText(c.Args[0]));
            var (c0, r0) = c.World.CellOfValues(c, l, c.Named["from"], "fill_tiles");
            var (c1, r1) = c.World.CellOfValues(c, l, c.Named["to"], "fill_tiles");
            c.World.FillCells(l, Math.Min(c0, c1), Math.Min(r0, r1), Math.Max(c0, c1), Math.Max(r0, r1), id);
        };
        w.Commands["noise_tiles"] = NoiseTilesCommand;

        w.Functions["noise"] = a =>
        {
            if (a.Count is < 1 or > 2) throw new ScriptError("'noise' needs x and y, like noise(x * 0.05, 0): it gives 0..1, changing smoothly.", 0);
            return w.NoiseGen.At(ToNumber(a[0], 0), a.Count > 1 ? ToNumber(a[1], 0) : 0.5);
        };
        w.Functions["tile_at"] = a => w.TileName(a, "tile_at(x, y)");
        w.Functions["tile_solid"] = a =>
        {
            if (a.Count < 2) throw new ScriptError("'tile_solid' needs x and y, like tile_solid(self.x, self.y + 40).", 0);
            return w.SolidTileAt(ToNumber(a[0], 0), ToNumber(a[1], 0));
        };
        w.Functions["tile_x"] = a => w.CellCenter(a, x: true);
        w.Functions["tile_y"] = a => w.CellCenter(a, x: false);
        w.Functions["is_tile"] = a => a.Count == 1 && w._tiles != null && w._tiles.Id(ToText(a[0])) > 0;
        w.Functions["tile_size"] = _ => w._tiles?.Main?.CellSize ?? 32.0;
        w.Functions["map_width"] = _ => w._tiles?.Main?.Width ?? w.SceneWidth;
        w.Functions["map_height"] = _ => w._tiles?.Main?.Height ?? w.SceneHeight;
        w.Functions["ground_y"] = a =>
        {
            if (a.Count < 1) throw new ScriptError("'ground_y' needs an x, like ground_y(self.x): the top of the first solid tile in that column.", 0);
            return w.GroundY(ToNumber(a[0], 0));
        };
    });

    /// <summary>'random_seed': the same seed gives the same dice and the same noise.</summary>
    public void Seed(int seed)
    {
        _seed = seed;
        _noise = new Noise(seed);
        Rng = new Random(seed);
    }

    // ---------------- Reading the command ----------------

    TileLayer LayerOf(CommandContext c)
    {
        var t = Tiles;
        string? name = c.Has("in") ? ToText(c.NamedArg("in")) : null;
        var l = t.Layer(name);
        if (l != null) return l;
        if (name != null) throw c.Error($"There's no tile layer called '{name}'.{Suggest(name, t.Layers.Select(x => x.Name))}");
        // No map yet: one the size of the scene, 32-px cells.
        l = new TileLayer((int)Math.Ceiling(SceneWidth / 32), (int)Math.Ceiling(SceneHeight / 32), 32);
        t.Layers.Add(l);
        return l;
    }

    (TileLayer L, int Col, int Row) CellOf(CommandContext c, List<object?> values, int i, string usage)
    {
        var l = LayerOf(c);
        var (col, row) = CellOfValues(c, l, values.Skip(i).ToList(), usage);
        return (l, col, row);
    }

    (int Col, int Row) CellOfValues(CommandContext c, TileLayer l, List<object?> values, string usage)
    {
        double x, y;
        if (values.Count >= 1 && values[0] is GameObject g) { x = g.X + g.Width / 2; y = g.Y + g.Height / 2; }
        else if (values.Count >= 1 && values[0] is List<object?> { Count: >= 2 } p) { x = c.Number(p[0]); y = c.Number(p[1]); }
        else if (values.Count >= 2) { x = c.Number(values[0]); y = c.Number(values[1]); }
        else throw c.Error($"Where? Write it like {usage} (scene pixels).");
        return l.CellAt(x, y);
    }

    int KindId(CommandContext c, string name)
    {
        if (TileMapState.IsEmptyName(name)) return 0;
        int id = Tiles.Id(name);
        if (id == 0)
            throw c.Error($"There's no tile kind called '{name}'. Make it first: tile_kind \"{name}\" with \"#8B5A2B\".{Suggest(name, Tiles.Kinds.Select(k => k.Name))}");
        return id;
    }

    static void MakeMapCommand(CommandContext c)
    {
        if (c.Args.Count < 2) throw c.Error("'make_map' needs columns and rows: make_map 400, 200 with 16 (16-pixel cells).");
        var w = c.World;
        int cols = (int)Math.Round(c.Number(c.Args[0])), rows = (int)Math.Round(c.Number(c.Args[1]));
        if (cols < 1 || rows < 1) throw c.Error("'make_map' needs at least 1 column and 1 row.");
        double cell = NamedNumber(c, "with") ?? w._tiles?.Main?.CellSize ?? 32;
        if (cell < 2) throw c.Error("Cells must be at least 2 pixels: make_map 400, 200 with 16.");
        var words = Words(c, "as", "with");
        string name = c.Named.TryGetValue("as", out var a) && a.Count > 0 && a[0] is string s && s.Length > 0 ? s : "Tiles";
        var t = w.Tiles;
        var l = t.Layer(name) ?? (name == "Tiles" ? t.Main : null);
        if (l == null) { l = new TileLayer(cols, rows, cell, name); t.Layers.Add(l); }
        else { l.CellSize = cell; l.Resize(cols, rows); l.Clear(); }
        if (HasWord(words, "behind")) { l.Solid = false; l.Front = false; t.Layers.Remove(l); t.Layers.Insert(0, l); }
        if (HasWord(words, "front")) { l.Solid = false; l.Front = true; }
        // The scene (and the camera's limits) grow to hold the map.
        w.SetSceneSize(Math.Max(w.SceneWidth, l.X + l.Width), Math.Max(w.SceneHeight, l.Y + l.Height));
    }

    static void TileKindCommand(CommandContext c)
    {
        if (c.Args.Count == 0 || c.Args[0] is not string name || name.Trim().Length == 0)
            throw c.Error("'tile_kind' needs a name: tile_kind \"dirt\" with \"#8B5A2B\" (then: solid, background, oneway, hardness 1, drop \"dirt\", picture \"dirt.png\", light 4, unbreakable).");
        var w = c.World;
        name = name.Trim();
        var k = w.Tiles.Kind(w.Tiles.Id(name))?.Clone() ?? new TileKind { Name = name };
        var words = Words(c, "with");
        for (int i = 1; i < words.Count; i++)
        {
            var word = words[i];
            object? next = i + 1 < words.Count ? words[i + 1].Value : null;
            switch (word.Name)
            {
                case "solid": k.Solid = true; k.OneWay = false; continue;
                case "background" or "behind" or "decoration": k.Solid = false; k.OneWay = false; continue;
                case "oneway" or "platform": k.Solid = true; k.OneWay = true; continue;
                case "unbreakable": k.Breakable = false; continue;
                case "breakable": k.Breakable = true; continue;
                case "hardness": k.Hardness = Math.Max(0, c.Number(next)); i++; continue;
                case "drop": k.Drop = ToText(next); i++; continue;
                case "picture" or "image": k.Image = ToText(next); i++; continue;
                case "light": k.Light = Math.Max(0, c.Number(next)); i++; continue;
                case "color" or "colour": k.Color = ToText(next); i++; continue;
            }
            if (word.Value is string s && s.Length > 0)
            {
                if (s.StartsWith('#')) k.Color = s;
                else if (Images.IsImage(s)) k.Image = s;
            }
        }
        w.Tiles.Define(k);
        // The item looks like its tile (World.Inventory.cs): the kind with the item's own name wins (grass drops dirt: dirt looks like dirt).
        if (k.DropItem.Length > 0 && (!w.ItemLooks.ContainsKey(k.DropItem) || string.Equals(k.DropItem, k.Name, StringComparison.OrdinalIgnoreCase)))
            w.ItemLooks[k.DropItem] = new ItemLook(k.Color, k.Image);
    }

    static void NoiseTilesCommand(CommandContext c)
    {
        if (c.Args.Count == 0 || !c.Has("with")) throw c.Error("'noise_tiles' needs a kind, what to replace and the noise: noise_tiles \"none\" from \"stone\" with 0.08, 0.62 (scale per cell, and how much: higher = less).");
        var w = c.World;
        var l = w.LayerOf(c);
        int to = w.KindId(c, ToText(c.Args[0]));
        int? from = c.Has("from") ? w.KindId(c, ToText(c.NamedArg("from"))) : null;
        var with = c.Named["with"];
        double scale = with.Count > 0 ? c.Number(with[0]) : 0.08, threshold = with.Count > 1 ? c.Number(with[1]) : 0.6;
        var words = Words(c, "from", "with");
        double below = ValueAfter(words, "below") is { } b ? c.Number(b) : double.NegativeInfinity;
        double above = ValueAfter(words, "above") is { } ab ? c.Number(ab) : double.PositiveInfinity;
        var noise = w.NoiseGen;
        double ox = w.Rng.NextDouble() * 1000, oy = w.Rng.NextDouble() * 1000; // each call its own pattern
        var cells = l.Cells;
        for (int r = 0; r < l.Rows; r++)
        {
            double top = l.Y + r * l.CellSize;
            if (top < below || top >= above) continue;
            for (int col = 0; col < l.Columns; col++)
            {
                int i = r * l.Columns + col;
                if (from != null && cells[i] != from) continue;
                if (noise.At(ox + col * scale, oy + r * scale) > threshold) cells[i] = (ushort)to;
            }
        }
        l.Touch();
        w.Tiles.DigLayer = null;
    }

    // ---------------- Changing cells ----------------

    void SetCell(TileLayer l, int col, int row, int id)
    {
        if (l.Set(col, row, (ushort)id) && Tiles.DigLayer == l && Tiles.DigCol == col && Tiles.DigRow == row) Tiles.DigAmount = Tiles.DigFraction = 0;
    }

    void FillCells(TileLayer l, int c0, int r0, int c1, int r1, int id)
    {
        c0 = Math.Max(0, c0); r0 = Math.Max(0, r0); c1 = Math.Min(l.Columns - 1, c1); r1 = Math.Min(l.Rows - 1, r1);
        var cells = l.Cells;
        for (int r = r0; r <= r1; r++)
            Array.Fill(cells, (ushort)id, r * l.Columns + c0, Math.Max(0, c1 - c0 + 1));
        l.Touch();
    }

    /// <summary>Puts a tile in an empty cell (not where a body stands, for a solid kind). True when it did.</summary>
    public bool PlaceTile(TileLayer l, int col, int row, int id)
    {
        if (id == 0 || !l.Inside(col, row) || l.Get(col, row) != 0) return false;
        if (Tiles.IsSolid(id) && l.Solid)
        {
            double x = l.X + col * l.CellSize, y = l.Y + row * l.CellSize, s = l.CellSize;
            foreach (var o in LiveObjects())
                if (o.HasBody && o.X < x + s - Eps && o.X + o.Width > x + Eps && o.Y < y + s - Eps && o.Y + o.Height > y + Eps) return false;
        }
        l.Set(col, row, (ushort)id);
        var k = Tiles.Kind(id)!;
        FireGame("tile placed", filter: k.Name, x: l.X + (col + 0.5) * l.CellSize, y: l.Y + (row + 0.5) * l.CellSize,
            extra: [("kind", k.Name)]);
        return true;
    }

    /// <summary>Breaks a tile (if it can be broken, or with <paramref name="force"/>): empties it and fires 'when tile broken'. Returns its kind, or null.</summary>
    public TileKind? BreakTile(TileLayer l, int col, int row, bool force = false)
    {
        var k = Tiles.Kind(l.Get(col, row));
        if (k == null || (!k.Breakable && !force)) return null;
        SetCell(l, col, row, 0);
        if (Tiles.DigLayer == l && Tiles.DigCol == col && Tiles.DigRow == row) Tiles.DigLayer = null;
        FireGame("tile broken", filter: k.Name, x: l.X + (col + 0.5) * l.CellSize, y: l.Y + (row + 0.5) * l.CellSize,
            extra: [("kind", k.Name), ("drop", k.DropItem)]);
        return k;
    }

    /// <summary>
    /// One frame of digging a cell (call it every tick while the button is held): it breaks after its hardness in
    /// seconds, divided by <paramref name="power"/>. Digging another cell, or stopping, starts over. Returns the kind when it broke.
    /// </summary>
    public TileKind? DigTile(TileLayer l, int col, int row, double power = 1)
    {
        var t = Tiles;
        var k = t.Kind(l.Get(col, row));
        if (k == null || !k.Breakable) { t.DigLayer = null; return null; }
        if (t.DigLayer != l || t.DigCol != col || t.DigRow != row || Time - t.DigTime > 0.25)
        {
            t.DigLayer = l; t.DigCol = col; t.DigRow = row; t.DigAmount = 0;
        }
        t.DigTime = Time;
        t.DigAmount += LastDt * Math.Max(0, power);
        t.DigFraction = k.Hardness <= 0 ? 1 : Math.Clamp(t.DigAmount / k.Hardness, 0, 1);
        if (t.DigFraction < 1) return null;
        return BreakTile(l, col, row);
    }

    // ---------------- Reading cells ----------------

    object? TileName(List<object?> a, string usage)
    {
        if (a.Count < 2) throw new ScriptError($"'{usage[..usage.IndexOf('(')]}' needs x and y, like {usage}.", 0);
        if (_tiles == null) return "";
        var l = a.Count > 2 ? _tiles.Layer(ToText(a[2])) : _tiles.Main;
        if (l == null) return "";
        var (c, r) = l.CellAt(ToNumber(a[0], 0), ToNumber(a[1], 0));
        return _tiles.Kind(l.Get(c, r))?.Name ?? "";
    }

    /// <summary>Is there a solid tile (in any solid layer) at this scene point?</summary>
    public bool SolidTileAt(double x, double y)
    {
        if (_tiles == null) return false;
        foreach (var l in _tiles.Layers)
        {
            if (!l.Solid) continue;
            var (c, r) = l.CellAt(x, y);
            if (_tiles.IsSolid(l.Get(c, r))) return true;
        }
        return false;
    }

    object? CellCenter(List<object?> a, bool x)
    {
        if (a.Count < 1) throw new ScriptError(x ? "'tile_x' needs an x, like tile_x(mouse_x): the middle of that cell." : "'tile_y' needs a y, like tile_y(mouse_y): the middle of that cell.", 0);
        var l = _tiles?.Main;
        double v = ToNumber(a[0], 0), s = l?.CellSize ?? 32, o = x ? l?.X ?? 0 : l?.Y ?? 0;
        return o + (Math.Floor((v - o) / s) + 0.5) * s;
    }

    /// <summary>The top of the highest solid tile in the column at x (the map's bottom when there's none).</summary>
    public double GroundY(double x)
    {
        var l = _tiles?.Main;
        if (l == null) return SceneHeight;
        var (c, _) = l.CellAt(x, 0);
        for (int r = 0; r < l.Rows; r++)
            if (_tiles!.IsSolid(l.Get(c, r))) return l.Y + r * l.CellSize;
        return l.Y + l.Height;
    }

    // ---------------- Collisions (World.Physics.cs asks) ----------------

    /// <summary>A body moving sideways by dx between top and bottom: the nearest solid tile side before <paramref name="stop"/>. True when one is nearer.</summary>
    bool TileStopX(double dx, double top, double bottom, double edge, ref double stop)
    {
        bool hit = false;
        var t = _tiles!;
        foreach (var l in t.Layers)
        {
            if (!l.Solid) continue;
            double cs = l.CellSize;
            int r0 = Math.Max(0, (int)Math.Floor((top - l.Y) / cs)), r1 = Math.Min(l.Rows - 1, (int)Math.Ceiling((bottom - l.Y) / cs) - 1);
            if (r0 > r1) continue;
            if (dx > 0)
            {
                int c0 = Math.Max(0, (int)Math.Ceiling((edge - Eps - l.X) / cs)), c1 = Math.Min(l.Columns - 1, (int)Math.Ceiling((stop - l.X) / cs) - 1);
                for (int col = c0; col <= c1; col++)
                    if (ColumnBlocks(t, l, col, r0, r1)) { double sx = l.X + col * cs; if (sx < stop) { stop = sx; hit = true; } break; }
            }
            else
            {
                int c1 = Math.Min(l.Columns - 1, (int)Math.Floor((edge + Eps - l.X) / cs) - 1), c0 = Math.Max(0, (int)Math.Floor((stop - l.X) / cs));
                for (int col = c1; col >= c0; col--)
                    if (ColumnBlocks(t, l, col, r0, r1)) { double rx = l.X + (col + 1) * cs; if (rx > stop) { stop = rx; hit = true; } break; }
            }
        }
        return hit;
    }

    static bool ColumnBlocks(TileMapState t, TileLayer l, int col, int r0, int r1)
    {
        var cells = l.Cells;
        for (int r = r0; r <= r1; r++)
        {
            int id = cells[r * l.Columns + col];
            if (id != 0 && t.IsSolid(id) && !t.IsOneWay(id)) return true;
        }
        return false;
    }

    /// <summary>A body moving up (dy &lt; 0) or down between left and right: the nearest solid tile top / bottom. True when one is nearer.</summary>
    bool TileStopY(double dy, double left, double right, double edge, ref double stop)
    {
        bool hit = false;
        var t = _tiles!;
        foreach (var l in t.Layers)
        {
            if (!l.Solid) continue;
            double cs = l.CellSize;
            int c0 = Math.Max(0, (int)Math.Floor((left - l.X) / cs)), c1 = Math.Min(l.Columns - 1, (int)Math.Ceiling((right - l.X) / cs) - 1);
            if (c0 > c1) continue;
            if (dy > 0)
            {
                int r0 = Math.Max(0, (int)Math.Ceiling((edge - Eps - l.Y) / cs)), r1 = Math.Min(l.Rows - 1, (int)Math.Ceiling((stop - l.Y) / cs) - 1);
                for (int r = r0; r <= r1; r++)
                    if (RowBlocks(t, l, r, c0, c1, oneWayToo: true)) { double sy = l.Y + r * cs; if (sy < stop) { stop = sy; hit = true; } break; }
            }
            else
            {
                int r1 = Math.Min(l.Rows - 1, (int)Math.Floor((edge + Eps - l.Y) / cs) - 1), r0 = Math.Max(0, (int)Math.Floor((stop - l.Y) / cs));
                for (int r = r1; r >= r0; r--)
                    if (RowBlocks(t, l, r, c0, c1, oneWayToo: false)) { double by = l.Y + (r + 1) * cs; if (by > stop) { stop = by; hit = true; } break; }
            }
        }
        return hit;
    }

    static bool RowBlocks(TileMapState t, TileLayer l, int r, int c0, int c1, bool oneWayToo)
    {
        var cells = l.Cells;
        int start = r * l.Columns;
        for (int col = c0; col <= c1; col++)
        {
            int id = cells[start + col];
            if (id != 0 && t.IsSolid(id) && (oneWayToo || !t.IsOneWay(id))) return true;
        }
        return false;
    }

    /// <summary>The scene's tiles become this world's (copies, so playing never changes the scene).</summary>
    internal void LoadTiles(Scene scene)
    {
        if (scene.TileLayers is not { Count: > 0 } layers) return;
        var t = Tiles;
        t.Kinds.Clear();
        t.Layers.Clear();
        foreach (var k in scene.TileKinds ?? []) t.Kinds.Add(k.Clone());
        t.Refresh();
        foreach (var l in layers) t.Layers.Add(l.Clone());
        foreach (var k in t.Kinds)
            if (k.DropItem.Length > 0 && !ItemLooks.ContainsKey(k.DropItem)) ItemLooks[k.DropItem] = new ItemLook(k.Color, k.Image);
    }
}
