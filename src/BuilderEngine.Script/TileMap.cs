using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BuilderEngine.Script;

/// <summary>
/// A kind of tile: how it looks (a colour or a picture), whether bodies stand on it, how long it takes to dig and what
/// it drops. A scene lists its kinds (<see cref="Scene.TileKinds"/>); a cell stores the kind's number (1 = the first
/// kind, 0 = empty). Scripts add kinds too: <c>tile_kind "ore" with "#C0C0C0" hardness 2 drop "ore"</c>.
/// </summary>
public sealed class TileKind
{
    public string Name { get; set; } = "dirt";
    public string Color { get; set; } = "#8B5A2B";
    /// <summary>A picture from the project's images folder ("" = just the colour).</summary>
    public string Image { get; set; } = "";
    /// <summary>Bodies stand on it and can't go through it. Not solid = background (a wall behind, a decoration).</summary>
    public bool Solid { get; set; } = true;
    /// <summary>Solid only from above (a wooden platform): jump up through it, land on top.</summary>
    public bool OneWay { get; set; }
    public bool Breakable { get; set; } = true;
    /// <summary>Seconds of digging (at power 1) before it breaks; 0 = breaks at once.</summary>
    public double Hardness { get; set; } = 0.4;
    /// <summary>The item breaking it gives ("" = an item with the kind's name, "none" = nothing).</summary>
    public string Drop { get; set; } = "";
    /// <summary>Light it gives, as a radius in cells (a torch, lava). 0 = none.</summary>
    public double Light { get; set; }
    public string LightColor { get; set; } = "#FFC870";
    /// <summary>An animated tile: its pictures, played at <see cref="Fps"/> (every copy in step). Null or one picture = still (<see cref="Image"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Frames { get; set; }
    /// <summary>Frames per second of an animated tile.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Fps { get; set; }

    /// <summary>The item this tile gives when broken, or "" for nothing.</summary>
    [JsonIgnore] public string DropItem => Drop.Length == 0 ? Name : string.Equals(Drop, "none", StringComparison.OrdinalIgnoreCase) ? "" : Drop;

    /// <summary>Animated: more than one picture in <see cref="Frames"/>.</summary>
    [JsonIgnore] public bool Animated => Frames is { Count: > 1 };

    /// <summary>The picture drawn <paramref name="time"/> seconds into the game: an animated tile's frame then, else <see cref="Image"/>.</summary>
    public string PictureAt(double time)
    {
        if (Frames is not { Count: > 1 } f) return Image;
        double fps = Math.Clamp(Fps <= 0 ? 8 : Fps, 1, 60);
        return f[(int)(Math.Floor(Math.Max(0, time) * fps) % f.Count)];
    }

    public TileKind Clone()
    {
        var c = (TileKind)MemberwiseClone();
        if (Frames != null) c.Frames = [.. Frames];
        return c;
    }

    internal static TileKind FromNode(JsonObject o) => new()
    {
        Name = Project.Str(o, "name", "tile"),
        Color = Project.Str(o, "color", "#8B5A2B"),
        Image = Project.Str(o, "image", ""),
        Solid = Bool(o, "solid", true),
        OneWay = Bool(o, "oneWay", false),
        Breakable = Bool(o, "breakable", true),
        Hardness = Math.Max(0, Project.Num(o, "hardness", 0.4)),
        Drop = Project.Str(o, "drop", ""),
        Light = Math.Max(0, Project.Num(o, "light", 0)),
        LightColor = Project.Str(o, "lightColor", "#FFC870"),
        Frames = Project.Get(o, "frames") is JsonArray fr
            ? fr.Select(f => f is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0).ToList() is { Count: > 0 } list ? list : null
            : null,
        Fps = Math.Clamp(Project.Num(o, "fps", 0), 0, 60),
    };

    internal static bool Bool(JsonObject o, string name, bool def) => Project.Get(o, name) switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Trim().ToLowerInvariant() is "true" or "yes" or "1" || (s.Trim().Length == 0 && def),
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => def
    };
}

/// <summary>
/// A grid of tiles: <see cref="Columns"/> × <see cref="Rows"/> cells of <see cref="CellSize"/> scene pixels, its top-left
/// corner at <see cref="X"/>, <see cref="Y"/>. Saved as a short run-length text (<see cref="Data"/>: "0*120,3*5,1").
/// A solid layer is the ground bodies walk on; a non-solid one is decoration (walls behind, grass).
/// </summary>
public sealed class TileLayer
{
    public const int MaxCells = 4_000_000;

    public string Name { get; set; } = "Tiles";
    public int Columns { get; private set; } = 25;
    public int Rows { get; private set; } = 15;
    public double CellSize { get; set; } = 32;
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Bodies collide with its solid tiles.</summary>
    public bool Solid { get; set; } = true;
    /// <summary>Drawn over the objects (leaves in front of the player) instead of behind them.</summary>
    public bool Front { get; set; }
    /// <summary>
    /// The EasyEngine Tilemap object this layer is (an EasyEngine project opened in BuilderEngine): that object as
    /// BuilderEngine reads it, its EasyEngine data in its own <c>easy</c> block. Null for a layer made in BuilderEngine.
    /// See <see cref="EasyLink"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Easy { get; set; }

    ushort[] _cells = new ushort[25 * 15];
    /// <summary>One kind number per cell, row by row (0 = empty).</summary>
    [JsonIgnore] public ushort[] Cells => _cells;
    /// <summary>Goes up on every change (the renderer and the light map keep what they made until it changes).</summary>
    [JsonIgnore] public int Version { get; private set; }

    public TileLayer() { }

    public TileLayer(int columns, int rows, double cellSize, string name = "Tiles")
    {
        Name = name;
        CellSize = Math.Max(1, cellSize);
        Resize(columns, rows);
    }

    /// <summary>The kind number at a cell; 0 outside the grid.</summary>
    public ushort Get(int col, int row) => (uint)col < (uint)Columns && (uint)row < (uint)Rows ? _cells[row * Columns + col] : (ushort)0;

    /// <summary>Changes a cell (nothing outside the grid). True when it changed.</summary>
    public bool Set(int col, int row, ushort id)
    {
        if ((uint)col >= (uint)Columns || (uint)row >= (uint)Rows) return false;
        ref var c = ref _cells[row * Columns + col];
        if (c == id) return false;
        c = id;
        Version++;
        return true;
    }

    public void Touch() => Version++;

    /// <summary>The cell a scene point is in (it may be outside the grid).</summary>
    public (int Col, int Row) CellAt(double x, double y) => ((int)Math.Floor((x - X) / CellSize), (int)Math.Floor((y - Y) / CellSize));

    public bool Inside(int col, int row) => (uint)col < (uint)Columns && (uint)row < (uint)Rows;

    [JsonIgnore] public double Width => Columns * CellSize;
    [JsonIgnore] public double Height => Rows * CellSize;

    /// <summary>A new size; what was in the top-left part stays.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, 10000);
        rows = Math.Clamp(rows, 1, 10000);
        while ((long)columns * rows > MaxCells) { if (columns > rows) columns--; else rows--; }
        var next = new ushort[columns * rows];
        int oldCols = Columns, oldRows = Rows;
        if (_cells.Length == oldCols * oldRows)
            for (int r = 0; r < Math.Min(rows, oldRows); r++)
                Array.Copy(_cells, r * oldCols, next, r * columns, Math.Min(columns, oldCols));
        Columns = columns;
        Rows = rows;
        _cells = next;
        Version++;
    }

    public void Clear()
    {
        Array.Clear(_cells);
        Version++;
    }

    /// <summary>The cells as run-length text: "3*25,0*500,1" (kind*count, comma separated).</summary>
    public string Data
    {
        get
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < _cells.Length)
            {
                ushort v = _cells[i];
                int n = 1;
                while (i + n < _cells.Length && _cells[i + n] == v) n++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(v.ToString(CultureInfo.InvariantCulture));
                if (n > 1) sb.Append('*').Append(n.ToString(CultureInfo.InvariantCulture));
                i += n;
            }
            return sb.ToString();
        }
        set
        {
            var cells = new ushort[Math.Max(1, Columns * Rows)];
            int at = 0;
            foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (at >= cells.Length) break;
                int star = part.IndexOf('*');
                if (!ushort.TryParse(star < 0 ? part : part[..star], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) v = 0;
                int n = 1;
                if (star >= 0 && !int.TryParse(part[(star + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) n = 1;
                n = Math.Clamp(n, 0, cells.Length - at);
                if (v != 0) Array.Fill(cells, v, at, n);
                at += n;
            }
            _cells = cells;
            Version++;
        }
    }

    public TileLayer Clone()
    {
        var c = (TileLayer)MemberwiseClone();
        c._cells = (ushort[])_cells.Clone();
        c.Easy = Easy?.DeepClone();
        return c;
    }

    internal static TileLayer FromNode(JsonObject o)
    {
        var l = new TileLayer
        {
            Name = Project.Str(o, "name", "Tiles"),
            CellSize = Math.Clamp(Project.Num(o, "cellSize", 32), 1, 1024),
            X = Project.Num(o, "x", 0),
            Y = Project.Num(o, "y", 0),
            Solid = TileKind.Bool(o, "solid", true),
            Front = TileKind.Bool(o, "front", false),
            Easy = Project.Get(o, "easy")?.DeepClone(),
        };
        l.Resize((int)Project.Num(o, "columns", 25), (int)Project.Num(o, "rows", 15));
        l.Data = Project.Str(o, "data", "");
        return l;
    }
}

/// <summary>
/// Smooth noise for making worlds: <c>noise(x, y)</c> gives 0..1 and changes slowly as x and y change (hills, caves,
/// ore veins). The same seed always gives the same world. Classic Perlin gradient noise, two octaves.
/// </summary>
public sealed class Noise
{
    readonly byte[] _perm = new byte[512];

    public Noise(int seed)
    {
        var p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;
        var rng = new Random(seed);
        for (int i = 255; i > 0; i--) { int j = rng.Next(i + 1); (p[i], p[j]) = (p[j], p[i]); }
        for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    /// <summary>0..1, about 0.5 on average.</summary>
    public double At(double x, double y)
    {
        double v = Perlin(x, y) * 0.7 + Perlin(x * 2.03 + 17.1, y * 2.03 + 5.3) * 0.3;
        return Math.Clamp(0.5 + v * 0.85, 0, 1);
    }

    double Perlin(double x, double y)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double xf = x - xi, yf = y - yi;
        int X = xi & 255, Y = yi & 255;
        double u = Fade(xf), v = Fade(yf);
        int aa = _perm[_perm[X] + Y], ab = _perm[_perm[X] + Y + 1], ba = _perm[_perm[X + 1] + Y], bb = _perm[_perm[X + 1] + Y + 1];
        double x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        double x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v);
    }

    static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    static double Grad(int h, double x, double y) => (h & 7) switch
    {
        0 => x + y, 1 => -x + y, 2 => x - y, 3 => -x - y,
        4 => x, 5 => -x, 6 => y, _ => -y
    };
}
