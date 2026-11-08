using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>A small map of the tile world in a corner of the screen ('show minimap').</summary>
public sealed class MinimapState
{
    public bool Visible;
    /// <summary>Where it is on the screen (view pixels from the top-left) and how big.</summary>
    public double X = 640, Y = 12, Width = 148, Height = 148;
    /// <summary>Only the cells something revealed ('minimap reveal') are drawn; the rest is dark.</summary>
    public bool Fog;
    /// <summary>Objects with these tags are dots of that colour (the player is always one).</summary>
    public List<(string Tag, string Color, bool Always)> Marks { get; } = [];
    internal bool[] Seen = [];
    internal int SeenCols, SeenRows, SeenVersion;
    internal uint[] Pixels = [];
    internal int PixelVersion = int.MinValue, PixelCols, PixelRows;
    /// <summary>Changes whenever <see cref="World.MinimapImage"/> made a new picture (the host copies it again).</summary>
    public int ImageVersion => PixelVersion;

    /// <summary>0..1: how much of the map has been revealed.</summary>
    public double SeenFraction
    {
        get
        {
            if (Seen.Length == 0) return 0;
            int n = 0;
            foreach (var s in Seen) if (s) n++;
            return (double)n / Seen.Length;
        }
    }
}

/// <summary>
/// The minimap:
/// <code>
/// show minimap                          (top-right, 148 × 148)    show minimap at 16, 16 with 120, 120 fog
/// hide minimap
/// minimap mark "enemy" with "#E05D5D"   (dots for objects with that tag; the player is always one; "always" = also in the fog)    minimap unmark "enemy"
/// minimap reveal 6                      (with 'fog': show the cells within 6 cells of this object — call it every tick; 'at player' = around another)
/// minimap reveal all
/// minimap_seen(x, y)                    (has that point been revealed)
/// </code>
/// The host draws <see cref="MinimapImage"/> (one pixel per cell, made again only when tiles or revealed cells change)
/// and <see cref="MinimapDots"/>. With fog on, marks show only on revealed cells.
/// </summary>
public sealed partial class World
{
    public MinimapState Minimap { get; private set; } = new();

    [ModuleInitializer]
    internal static void InstallMinimap() => Installers.Add(w =>
    {
        w.Commands["show minimap"] = c =>
        {
            var m = c.World.Minimap;
            m.Visible = true;
            if (c.Named.TryGetValue("at", out var at) && at.Count >= 2) { m.X = c.Number(at[0]); m.Y = c.Number(at[1]); }
            if (c.Named.TryGetValue("with", out var size) && size.Count >= 2) { m.Width = Math.Max(16, c.Number(size[0])); m.Height = Math.Max(16, c.Number(size[1])); }
            m.Fog = HasWord(Words(c, "at", "with"), "fog");
        };
        w.Commands["hide minimap"] = c => c.World.Minimap.Visible = false;
        w.Commands["minimap mark"] = c =>
        {
            if (c.Args.Count == 0) throw c.Error("'minimap mark' needs a tag: minimap mark \"enemy\" with \"#E05D5D\".");
            string tag = ToText(c.Args[0]);
            string color = c.NamedArg("with") is string s && s.Length > 0 ? s : "#E05D5D";
            var marks = c.World.Minimap.Marks;
            marks.RemoveAll(x => string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase));
            marks.Add((tag, color, HasWord(Words(c, "with"), "always")));
        };
        w.Commands["minimap unmark"] = c =>
        {
            string tag = c.Args.Count > 0 ? ToText(c.Args[0]) : "";
            c.World.Minimap.Marks.RemoveAll(x => string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase));
        };
        w.Commands["minimap reveal"] = c =>
        {
            var world = c.World;
            if (WordAt(c, 0) == "all") { world.RevealMinimap(0, 0, double.PositiveInfinity); return; }
            double radius = c.Args.Count > 0 && c.Args[0] is double ? c.Number(c.Args[0]) : 6;
            var (x, y) = Center(c.Self);
            if (c.Named.TryGetValue("at", out var at)) // minimap reveal 6 at find("player")  ·  at 400, 300
            {
                if (at.Count >= 1 && at[0] is GameObject g) (x, y) = Center(g);
                else if (at.Count >= 2) { x = c.Number(at[0]); y = c.Number(at[1]); }
                else return; // the object wasn't found
            }
            world.RevealMinimap(x, y, radius);
        };
        w.Functions["minimap_seen"] = a =>
        {
            if (a.Count < 2) throw new ScriptError("'minimap_seen' needs x and y, like minimap_seen(self.x, self.y).", 0);
            return w.MinimapSeen(ToNumber(a[0], 0), ToNumber(a[1], 0));
        };
    });

    bool EnsureSeen()
    {
        var main = _tiles?.Main;
        if (main == null) return false;
        var m = Minimap;
        if (m.SeenCols != main.Columns || m.SeenRows != main.Rows)
        {
            m.Seen = new bool[main.Columns * main.Rows];
            m.SeenCols = main.Columns; m.SeenRows = main.Rows;
            m.SeenVersion++;
        }
        return true;
    }

    /// <summary>Reveals the cells within <paramref name="radiusCells"/> cells of a scene point.</summary>
    public void RevealMinimap(double x, double y, double radiusCells)
    {
        if (!EnsureSeen()) return;
        var main = _tiles!.Main!;
        var m = Minimap;
        if (double.IsPositiveInfinity(radiusCells)) { Array.Fill(m.Seen, true); m.SeenVersion++; return; }
        var (cc, cr) = main.CellAt(x, y);
        int r = (int)Math.Ceiling(Math.Max(0, radiusCells));
        double r2 = radiusCells * radiusCells;
        bool changed = false;
        for (int row = Math.Max(0, cr - r); row <= Math.Min(main.Rows - 1, cr + r); row++)
            for (int col = Math.Max(0, cc - r); col <= Math.Min(main.Columns - 1, cc + r); col++)
            {
                int dx = col - cc, dy = row - cr;
                if (dx * dx + dy * dy > r2) continue;
                int i = row * main.Columns + col;
                if (!m.Seen[i]) { m.Seen[i] = true; changed = true; }
            }
        if (changed) m.SeenVersion++;
    }

    public bool MinimapSeen(double x, double y)
    {
        if (!EnsureSeen()) return false;
        var main = _tiles!.Main!;
        var (c, r) = main.CellAt(x, y);
        return main.Inside(c, r) && Minimap.Seen[r * main.Columns + c];
    }

    /// <summary>
    /// The minimap picture: one ARGB pixel per cell of the main tile layer (a solid tile's colour, else the colour of
    /// the layer behind, darker; unrevealed cells nearly black with fog on). Null when there are no tiles.
    /// </summary>
    public uint[]? MinimapImage(out int columns, out int rows)
    {
        columns = rows = 0;
        var main = _tiles?.Main;
        if (main == null) return null;
        EnsureSeen();
        var m = Minimap;
        int ver = m.SeenVersion * 7919 + (m.Fog ? 1 : 0);
        foreach (var l in _tiles!.Layers) ver = ver * 31 + l.Version;
        columns = main.Columns; rows = main.Rows;
        if (ver == m.PixelVersion && m.PixelCols == columns && m.PixelRows == rows) return m.Pixels;
        int n = columns * rows;
        if (m.Pixels.Length != n) m.Pixels = new uint[n];
        var colors = new uint[_tiles.Kinds.Count + 1];
        for (int k = 0; k < _tiles.Kinds.Count; k++) colors[k + 1] = 0xFF000000u | (uint)ParseHex(_tiles.Kinds[k].Color, 0x808080);
        TileLayer? behind = null;
        foreach (var l in _tiles.Layers) if (l != main && !l.Front && l.Columns == columns && l.Rows == rows) { behind = l; break; }
        uint empty = 0xFF000000u | (uint)ParseHex(DarkColor, 0x04060C);
        var cells = main.Cells;
        for (int i = 0; i < n; i++)
        {
            uint px;
            int id = cells[i];
            if (id != 0 && id < colors.Length) px = colors[id];
            else if (behind != null && behind.Cells[i] is var b && b != 0 && b < colors.Length) px = Dim(colors[b], 0.55);
            else px = empty;
            if (m.Fog && !m.Seen[i]) px = 0xE0050505;
            m.Pixels[i] = px;
        }
        m.PixelVersion = ver; m.PixelCols = columns; m.PixelRows = rows;
        return m.Pixels;
    }

    static uint Dim(uint argb, double k) =>
        (argb & 0xFF000000u) | ((uint)(((argb >> 16) & 255) * k) << 16) | ((uint)(((argb >> 8) & 255) * k) << 8) | (uint)((argb & 255) * k);

    /// <summary>The dots to draw now: where (0..1 across and down the map) and the colour. The player comes last.</summary>
    public List<(double Fx, double Fy, string Color)> MinimapDots()
    {
        var list = new List<(double, double, string)>();
        var main = _tiles?.Main;
        if (main == null || main.Width <= 0 || main.Height <= 0) return list;
        var m = Minimap;
        GameObject? player = FindObject("player");
        foreach (var (tag, color, always) in m.Marks)
            foreach (var o in _objects)
            {
                if (o.Destroyed || o == player || !o.Is(tag)) continue;
                var (x, y) = Center(o);
                if (m.Fog && !always && !MinimapSeen(x, y)) continue;
                list.Add(((x - main.X) / main.Width, (y - main.Y) / main.Height, color));
            }
        if (player != null)
        {
            var (x, y) = Center(player);
            list.Add(((x - main.X) / main.Width, (y - main.Y) / main.Height, "#FFFFFF"));
        }
        return list;
    }
}
