using System.Globalization;
using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

public sealed partial class GameObject
{
    /// <summary>Light around the object (pixels; 0 = none). 'light 120 with "#FFD27A" flicker 0.2'.</summary>
    public double LightRadius;
    public string LightColor = "#FFE2A8";
    /// <summary>0..1: how much the light trembles (a torch, a candle).</summary>
    public double LightFlicker;
    /// <summary>0..1: how bright the middle of the light is.</summary>
    public double LightStrength = 1;
}

/// <summary>The light map the host draws over the game: one value per cell of a coarse grid over the view.</summary>
public sealed class LightBuffer
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>ARGB (not premultiplied): the darkness over each cell, tinted by the lights near it.</summary>
    public uint[] Pixels { get; private set; } = [];
    internal float[] Light = [];
    internal float[] Tint = []; // how much the light here comes from coloured object lights
    internal int[] TintColor = [];

    public void Resize(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == Width && h == Height) return;
        Width = w; Height = h;
        Pixels = new uint[w * h];
        Light = new float[w * h];
        Tint = new float[w * h];
        TintColor = new int[w * h];
    }
}

/// <summary>
/// Darkness and lights:
/// <code>
/// darkness 0.9              darkness 0.4 in 3      (0 = none, 1 = black where nothing shines)
/// light 140                 light 140 with "#FFD27A" flicker 0.3      light off      light Torch 80
/// sunlight 1                sunlight 0.2 in 10     sunlight off   (tiles open to the sky are lit; it spreads into caves)
/// light_at(x, y)            (0..1, how lit a point is)
/// </code>
/// Ported from EasyEngine's PlayWindow.Lighting/LightGrid: smooth darkness with soft round lights, and a per-cell light
/// map for tiles (sky light and glowing tiles spread a few cells, less through solid ground).
/// </summary>
public sealed partial class World
{
    /// <summary>0..1: how dark it is where no light shines.</summary>
    public double Darkness { get; set; }
    /// <summary>Light from the sky on tiles open to it (0..1), or below 0 when tile lighting is off.</summary>
    public double Sunlight { get; set; } = -1;
    public string DarkColor { get; set; } = "#04060C";
    int _darkTween, _sunTween;

    /// <summary>Is there anything to draw (some darkness)?</summary>
    public bool LightingOn => Darkness > 0.001;

    [ModuleInitializer]
    internal static void InstallLights() => Installers.Add(w =>
    {
        w.Commands["darkness"] = c =>
        {
            var world = c.World;
            double to = WordAt(c, 0) is "off" or "none" ? 0 : c.Args.Count > 0 ? Math.Clamp(c.Number(c.Args[0]), 0, 1) : 0.85;
            if (c.Has("with") && c.NamedArg("with") is string col && col.Length > 0) world.DarkColor = col;
            double from = world.Darkness;
            int id = ++world._darkTween;
            Tween(c, NamedNumber(c, "in") ?? 0, "linear", k => world.Darkness = from + (to - from) * k, () => world._darkTween == id);
        };
        w.Commands["sunlight"] = c =>
        {
            var world = c.World;
            if (WordAt(c, 0) is "off" or "none") { world.Sunlight = -1; world._sunTween++; return; }
            double to = c.Args.Count > 0 ? Math.Clamp(c.Number(c.Args[0]), 0, 1) : 1;
            double from = Math.Max(0, world.Sunlight);
            int id = ++world._sunTween;
            Tween(c, NamedNumber(c, "in") ?? 0, "linear", k => world.Sunlight = from + (to - from) * k, () => world._sunTween == id);
        };
        w.Commands["light"] = c =>
        {
            var (o, i) = TargetAndRest(c, 1);
            if (WordAt(c, i) is "off" or "none") { o.LightRadius = 0; return; }
            o.LightRadius = i < c.Args.Count && c.Args[i] is double or string ? Math.Max(0, c.Number(c.Args[i])) : 160;
            var words = Words(c, "with");
            if (c.Named.TryGetValue("with", out var with))
                for (int k = 0; k < with.Count; k++)
                    if (with[k] is string s && s.StartsWith('#')) o.LightColor = s;
                    else if (with[k] is double fl && (k == 0 || with[k - 1] is not null)) o.LightFlicker = Math.Clamp(fl, 0, 1); // light 140 with "#FFD27A", 0.3
            if (ValueAfter(words, "flicker") is { } f) o.LightFlicker = Math.Clamp(c.Number(f), 0, 1);
            if (ValueAfter(words, "strength") is { } st) o.LightStrength = Math.Clamp(c.Number(st), 0, 1);
        };
        w.Functions["light_at"] = a =>
        {
            if (a.Count < 2) throw new ScriptError("'light_at' needs x and y, like light_at(self.x, self.y): 0 = dark, 1 = fully lit.", 0);
            return w.LightAt(ToNumber(a[0], 0), ToNumber(a[1], 0));
        };
        w.Functions["darkness"] = _ => w.Darkness;
    });

    // ---------------- Object lights ----------------

    readonly record struct LightSource(double X, double Y, double Radius, double Strength, int Color);
    readonly List<LightSource> _lightList = [];
    readonly Dictionary<string, int> _lightColors = new(StringComparer.OrdinalIgnoreCase);

    int ParsedColor(string hex)
    {
        if (_lightColors.TryGetValue(hex, out var c)) return c;
        c = ParseHex(hex, 0xFFE2A8);
        _lightColors[hex] = c;
        return c;
    }

    /// <summary>"#RGB", "#RRGGBB" or "#AARRGGBB" → 0xRRGGBB (the fallback when it isn't a colour).</summary>
    public static int ParseHex(string? hex, int fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => new string(ch, 2)));
        if (s.Length == 8) s = s[2..];
        return s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    void CollectLights()
    {
        _lightList.Clear();
        foreach (var o in _objects)
        {
            if (o.Destroyed || o.LightRadius <= 0 || o.Ui) continue;
            double strength = o.LightStrength, radius = o.LightRadius;
            if (o.LightFlicker > 0)
            {
                double t = RealTime, k = o.Id * 1.618;
                double wobble = 0.5 + 0.25 * Math.Sin(t * 11.3 + k) + 0.25 * Math.Sin(t * 23.7 + k * 2.1);
                strength *= 1 - o.LightFlicker * 0.45 * wobble;
                radius *= 1 - o.LightFlicker * 0.12 * wobble;
            }
            _lightList.Add(new LightSource(o.X + o.Width / 2, o.Y + o.Height / 2, radius, strength, ParsedColor(o.LightColor)));
        }
    }

    /// <summary>How much an object light gives at distance d (soft edge, bright middle).</summary>
    static double Falloff(double d, double r)
    {
        if (d >= r) return 0;
        double t = d / r;
        double inner = 0.35;
        if (t <= inner) return 1;
        double k = (t - inner) / (1 - inner);
        return 1 - k * k * (3 - 2 * k);
    }

    // ---------------- Tile light (sky + glowing tiles) ----------------

    float[] _grid = [];
    int _gx0, _gy0, _gw, _gh;
    double _gCell, _gX, _gY;
    int[] _skyRow = [];
    int _skyVersion = -1;
    TileLayer? _skyLayer;
    const float OpenDecay = 1f / 12, SolidDecay = 1f / 4;

    bool TileLightOn => _tiles != null && _tiles.Main != null && (Sunlight >= 0 || HasGlowingTiles());

    bool HasGlowingTiles()
    {
        foreach (var k in _tiles!.Kinds) if (k.Light > 0) return true;
        return false;
    }

    int SolidVersion()
    {
        int v = 17;
        foreach (var l in _tiles!.Layers) if (l.Solid) v = v * 31 + l.Version;
        return v;
    }

    /// <summary>The light level of every cell around the view (sky light from the top, glowing tiles), spread with falloff.</summary>
    void ComputeTileGrid(double left, double top, double right, double bottom)
    {
        var t = _tiles!;
        var main = t.Main!;
        double cs = main.CellSize;
        const int margin = 14;
        int c0 = Math.Max(0, (int)Math.Floor((left - main.X) / cs) - margin), c1 = Math.Min(main.Columns - 1, (int)Math.Floor((right - main.X) / cs) + margin);
        int r0 = Math.Max(0, (int)Math.Floor((top - main.Y) / cs) - margin), r1 = Math.Min(main.Rows - 1, (int)Math.Floor((bottom - main.Y) / cs) + margin);
        _gCell = cs; _gX = main.X; _gY = main.Y;
        if (c0 > c1 || r0 > r1) { _gw = _gh = 0; return; }
        _gx0 = c0; _gy0 = r0; _gw = c1 - c0 + 1; _gh = r1 - r0 + 1;
        if (_grid.Length < _gw * _gh) _grid = new float[_gw * _gh];
        Array.Clear(_grid, 0, _gw * _gh);

        // The first solid row of every column (sky reaches down to it), kept until a solid layer changes.
        int ver = SolidVersion();
        if (ver != _skyVersion || _skyLayer != main || _skyRow.Length != main.Columns)
        {
            _skyVersion = ver;
            _skyLayer = main;
            _skyRow = new int[main.Columns];
            for (int col = 0; col < main.Columns; col++)
            {
                int r = 0;
                while (r < main.Rows && !SolidCell(t, main, col, r)) r++;
                _skyRow[col] = r;
            }
        }
        float sun = (float)Math.Max(0, Sunlight);
        for (int col = c0; col <= c1; col++)
        {
            int sky = _skyRow[col];
            for (int r = r0; r <= r1; r++)
            {
                int i = (r - r0) * _gw + (col - c0);
                if (Sunlight >= 0 && r < sky) _grid[i] = sun;
                var k = t.Kind(main.Get(col, r));
                if (k != null && k.Light > 0) _grid[i] = Math.Max(_grid[i], (float)Math.Min(1, k.Light * OpenDecay));
            }
        }
        // Spread: two sweeps each way (like a distance map), losing more through solid cells.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int r = 0; r < _gh; r++)
                for (int col = 0; col < _gw; col++)
                {
                    int i = r * _gw + col;
                    float decay = SolidCell(t, main, col + c0, r + r0) ? SolidDecay : OpenDecay;
                    float best = _grid[i];
                    if (col > 0) best = Math.Max(best, _grid[i - 1] - decay);
                    if (r > 0) best = Math.Max(best, _grid[i - _gw] - decay);
                    _grid[i] = best;
                }
            for (int r = _gh - 1; r >= 0; r--)
                for (int col = _gw - 1; col >= 0; col--)
                {
                    int i = r * _gw + col;
                    float decay = SolidCell(t, main, col + c0, r + r0) ? SolidDecay : OpenDecay;
                    float best = _grid[i];
                    if (col < _gw - 1) best = Math.Max(best, _grid[i + 1] - decay);
                    if (r < _gh - 1) best = Math.Max(best, _grid[i + _gw] - decay);
                    _grid[i] = best;
                }
        }
    }

    static bool SolidCell(TileMapState t, TileLayer main, int col, int row)
    {
        foreach (var l in t.Layers)
        {
            if (!l.Solid) continue;
            if (l == main) { if (t.IsSolid(l.Get(col, row))) return true; continue; }
            var (c, r) = l.CellAt(main.X + (col + 0.5) * main.CellSize, main.Y + (row + 0.5) * main.CellSize);
            if (t.IsSolid(l.Get(c, r))) return true;
        }
        return false;
    }

    /// <summary>The tile light at a scene point (smoothly between cells); the sunlight when there's no map; 0 when off.</summary>
    double GridLight(double x, double y)
    {
        if (_gw <= 0) return 0;
        double fx = (x - _gX) / _gCell - 0.5 - _gx0, fy = (y - _gY) / _gCell - 0.5 - _gy0;
        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
        double ax = fx - ix, ay = fy - iy;
        double G(int cx, int cy) => cx < 0 || cy < 0 || cx >= _gw || cy >= _gh ? 0 : Math.Max(0, _grid[cy * _gw + cx]);
        return (G(ix, iy) * (1 - ax) + G(ix + 1, iy) * ax) * (1 - ay) + (G(ix, iy + 1) * (1 - ax) + G(ix + 1, iy + 1) * ax) * ay;
    }

    // ---------------- The overlay ----------------

    /// <summary>
    /// Fills <paramref name="buf"/> with the darkness over the view (top-left at <paramref name="left"/>, <paramref name="top"/>,
    /// <paramref name="viewW"/> × <paramref name="viewH"/> scene pixels): alpha = darkness × (1 − light), tinted near coloured lights.
    /// </summary>
    public void ComputeLighting(LightBuffer buf, double left, double top, double viewW, double viewH)
    {
        int w = buf.Width, h = buf.Height, n = w * h;
        double cw = viewW / w, ch = viewH / h;
        var light = buf.Light;
        var tint = buf.Tint;
        var tcol = buf.TintColor;
        Array.Clear(light, 0, n);
        Array.Clear(tint, 0, n);

        bool grid = TileLightOn;
        if (grid) ComputeTileGrid(left, top, left + viewW, top + viewH);
        else _gw = 0;
        float flat = !grid && Sunlight >= 0 ? (float)Sunlight : 0;

        CollectLights();
        foreach (var l in _lightList)
        {
            int x0 = Math.Max(0, (int)Math.Floor((l.X - l.Radius - left) / cw)), x1 = Math.Min(w - 1, (int)Math.Ceiling((l.X + l.Radius - left) / cw));
            int y0 = Math.Max(0, (int)Math.Floor((l.Y - l.Radius - top) / ch)), y1 = Math.Min(h - 1, (int)Math.Ceiling((l.Y + l.Radius - top) / ch));
            for (int py = y0; py <= y1; py++)
            {
                double wy = top + (py + 0.5) * ch - l.Y;
                for (int px = x0; px <= x1; px++)
                {
                    double wx = left + (px + 0.5) * cw - l.X;
                    double f = Falloff(Math.Sqrt(wx * wx + wy * wy), l.Radius) * l.Strength;
                    if (f <= 0) continue;
                    int i = py * w + px;
                    if (f > tint[i]) { tint[i] = (float)f; tcol[i] = l.Color; }
                    light[i] = (float)(1 - (1 - light[i]) * (1 - f)); // two lights together are brighter, never above 1
                }
            }
        }

        int dark = ParseHex(DarkColor, 0x04060C);
        int dr = (dark >> 16) & 255, dg = (dark >> 8) & 255, db = dark & 255;
        double d = Math.Clamp(Darkness, 0, 1);
        var px2 = buf.Pixels;
        for (int py = 0; py < h; py++)
        {
            double wy = top + (py + 0.5) * ch;
            for (int px = 0; px < w; px++)
            {
                int i = py * w + px;
                double L = light[i];
                double g = grid ? GridLight(left + (px + 0.5) * cw, wy) : flat;
                if (g > L) L = g;
                L = Math.Clamp(L, 0, 1);
                double a = d * (1 - L);
                int r = dr, gg = dg, b = db;
                double tk = tint[i];
                if (tk > 0)
                {
                    int c = tcol[i];
                    // The edge of a coloured light is a dim version of its colour (never brighter than a dark scene).
                    double k = Math.Min(1, tk);
                    r = (int)(dr + (((c >> 16) & 255) * 0.25 - dr) * k);
                    gg = (int)(dg + (((c >> 8) & 255) * 0.25 - dg) * k);
                    b = (int)(db + ((c & 255) * 0.25 - db) * k);
                }
                px2[i] = ((uint)(Math.Clamp(a, 0, 1) * 255) << 24) | ((uint)r << 16) | ((uint)gg << 8) | (uint)b;
            }
        }
    }

    /// <summary>0..1: how lit a scene point is (object lights, tile light, sunlight) — 1 when there's no darkness.</summary>
    public double LightAt(double x, double y)
    {
        if (!LightingOn) return 1;
        CollectLights();
        double L = 0;
        foreach (var l in _lightList)
        {
            double dx = x - l.X, dy = y - l.Y;
            double f = Falloff(Math.Sqrt(dx * dx + dy * dy), l.Radius) * l.Strength;
            L = 1 - (1 - L) * (1 - f);
        }
        if (TileLightOn)
        {
            ComputeTileGrid(x - 1, y - 1, x + 1, y + 1);
            L = Math.Max(L, GridLight(x, y));
        }
        else if (Sunlight >= 0) L = Math.Max(L, Sunlight);
        return Math.Clamp(L, 0, 1);
    }

    /// <summary>The lights of this frame, for tests: (x, y, radius) of every lit object.</summary>
    public IReadOnlyList<(double X, double Y, double Radius)> LightsNow()
    {
        CollectLights();
        return _lightList.Select(l => (l.X, l.Y, l.Radius)).ToList();
    }
}
