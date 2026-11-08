using System.IO.Compression;

namespace BuilderEngine.Script;

/// <summary>
/// A tiny painter for the Night Shift sample's pictures: filled rectangles, ellipses, polygons, thick lines and
/// gradients, drawn supersampled (smooth edges) and saved as PNG with no packages (zlib from the BCL, our own CRC).
/// </summary>
internal sealed class NightShiftCanvas
{
    readonly int _w, _h, _s;
    readonly float[] _r, _g, _b, _a;
    public int Width { get; }
    public int Height { get; }

    public NightShiftCanvas(int width, int height, int supersample = 3)
    {
        Width = width; Height = height; _s = supersample;
        _w = width * _s; _h = height * _s;
        _r = new float[_w * _h]; _g = new float[_w * _h]; _b = new float[_w * _h]; _a = new float[_w * _h];
    }

    public readonly record struct Rgba(float R, float G, float B, float A)
    {
        public Rgba WithAlpha(double a) => this with { A = (float)a };
        public Rgba Mix(Rgba o, double k) => new((float)(R + (o.R - R) * k), (float)(G + (o.G - G) * k), (float)(B + (o.B - B) * k), (float)(A + (o.A - A) * k));
    }

    public static Rgba C(string hex, double alpha = 1)
    {
        var s = hex.TrimStart('#');
        int v = Convert.ToInt32(s, 16);
        return new Rgba(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, (float)alpha);
    }

    void Blend(int i, Rgba c, float cover = 1)
    {
        float sa = c.A * cover;
        if (sa <= 0) return;
        float da = _a[i], oa = sa + da * (1 - sa);
        if (oa <= 0) return;
        _r[i] = (c.R * sa + _r[i] * da * (1 - sa)) / oa;
        _g[i] = (c.G * sa + _g[i] * da * (1 - sa)) / oa;
        _b[i] = (c.B * sa + _b[i] * da * (1 - sa)) / oa;
        _a[i] = oa;
    }

    public void Fill(Rgba c) => Rect(0, 0, Width, Height, c);

    public void Rect(double x, double y, double w, double h, Rgba c)
    {
        int x0 = Math.Max(0, (int)Math.Round(x * _s)), x1 = Math.Min(_w, (int)Math.Round((x + w) * _s));
        int y0 = Math.Max(0, (int)Math.Round(y * _s)), y1 = Math.Min(_h, (int)Math.Round((y + h) * _s));
        for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++) Blend(py * _w + px, c);
    }

    /// <summary>A rectangle whose colour goes from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
    public void VGradient(double x, double y, double w, double h, Rgba top, Rgba bottom)
    {
        int y0 = Math.Max(0, (int)Math.Round(y * _s)), y1 = Math.Min(_h, (int)Math.Round((y + h) * _s));
        int x0 = Math.Max(0, (int)Math.Round(x * _s)), x1 = Math.Min(_w, (int)Math.Round((x + w) * _s));
        for (int py = y0; py < y1; py++)
        {
            var c = top.Mix(bottom, (py - y0) / (double)Math.Max(1, y1 - y0 - 1));
            for (int px = x0; px < x1; px++) Blend(py * _w + px, c);
        }
    }

    public void Ellipse(double cx, double cy, double rx, double ry, Rgba c)
    {
        double scx = cx * _s, scy = cy * _s, srx = rx * _s, sry = ry * _s;
        int x0 = Math.Max(0, (int)(scx - srx)), x1 = Math.Min(_w - 1, (int)(scx + srx) + 1);
        int y0 = Math.Max(0, (int)(scy - sry)), y1 = Math.Min(_h - 1, (int)(scy + sry) + 1);
        for (int py = y0; py <= y1; py++)
        {
            double dy = (py + 0.5 - scy) / sry;
            for (int px = x0; px <= x1; px++)
            {
                double dx = (px + 0.5 - scx) / srx;
                if (dx * dx + dy * dy <= 1) Blend(py * _w + px, c);
            }
        }
    }

    /// <summary>A soft round glow: full colour in the middle, fading to nothing at the radius.</summary>
    public void Glow(double cx, double cy, double radius, Rgba c)
    {
        double scx = cx * _s, scy = cy * _s, sr = radius * _s;
        int x0 = Math.Max(0, (int)(scx - sr)), x1 = Math.Min(_w - 1, (int)(scx + sr) + 1);
        int y0 = Math.Max(0, (int)(scy - sr)), y1 = Math.Min(_h - 1, (int)(scy + sr) + 1);
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                double d = Math.Sqrt((px + 0.5 - scx) * (px + 0.5 - scx) + (py + 0.5 - scy) * (py + 0.5 - scy)) / sr;
                if (d < 1) Blend(py * _w + px, c, (float)((1 - d) * (1 - d)));
            }
    }

    /// <summary>A filled polygon (even-odd), points as x0, y0, x1, y1…</summary>
    public void Poly(Rgba c, params double[] pts)
    {
        int n = pts.Length / 2;
        if (n < 3) return;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++) { minY = Math.Min(minY, pts[i * 2 + 1]); maxY = Math.Max(maxY, pts[i * 2 + 1]); }
        int y0 = Math.Max(0, (int)(minY * _s)), y1 = Math.Min(_h - 1, (int)(maxY * _s) + 1);
        var xs = new List<double>(8);
        for (int py = y0; py <= y1; py++)
        {
            double sy = (py + 0.5) / _s;
            xs.Clear();
            for (int i = 0; i < n; i++)
            {
                double ax = pts[i * 2], ay = pts[i * 2 + 1], bx = pts[(i + 1) % n * 2], by = pts[(i + 1) % n * 2 + 1];
                if ((ay <= sy && by > sy) || (by <= sy && ay > sy)) xs.Add(ax + (sy - ay) / (by - ay) * (bx - ax));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int xa = Math.Max(0, (int)Math.Round(xs[k] * _s)), xb = Math.Min(_w, (int)Math.Round(xs[k + 1] * _s));
                for (int px = xa; px < xb; px++) Blend(py * _w + px, c);
            }
        }
    }

    public void Line(double x1, double y1, double x2, double y2, double width, Rgba c)
    {
        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return;
        double nx = -dy / len * width / 2, ny = dx / len * width / 2;
        Poly(c, x1 + nx, y1 + ny, x2 + nx, y2 + ny, x2 - nx, y2 - ny, x1 - nx, y1 - ny);
    }

    /// <summary>A line through several points (x0, y0, x1, y1…).</summary>
    public void Path(double width, Rgba c, params double[] pts)
    {
        for (int i = 0; i + 3 < pts.Length; i += 2) Line(pts[i], pts[i + 1], pts[i + 2], pts[i + 3], width, c);
    }

    /// <summary>Speckles of light and dark (film grain, dust), the same every time for a seed.</summary>
    public void Grain(double amount, int seed)
    {
        var rng = new Random(seed);
        for (int i = 0; i < _r.Length; i++)
        {
            if (_a[i] <= 0) continue;
            float d = (float)((rng.NextDouble() - 0.5) * amount);
            _r[i] = Math.Clamp(_r[i] + d, 0, 1); _g[i] = Math.Clamp(_g[i] + d, 0, 1); _b[i] = Math.Clamp(_b[i] + d, 0, 1);
        }
    }

    /// <summary>Darkens the edges (an old lens, a dark room).</summary>
    public void Vignette(double strength, Rgba color)
    {
        for (int py = 0; py < _h; py++)
            for (int px = 0; px < _w; px++)
            {
                double dx = (px + 0.5) / _w - 0.5, dy = (py + 0.5) / _h - 0.5;
                double d = Math.Sqrt(dx * dx * 2.2 + dy * dy * 2.6);
                double k = Math.Clamp((d - 0.35) / 0.6, 0, 1) * strength;
                if (k > 0) Blend(py * _w + px, color, (float)k);
            }
    }

    // ---------------- PNG ----------------

    public void Save(string path)
    {
        var raw = new byte[(Width * 4 + 1) * Height];
        float inv = 1f / (_s * _s);
        for (int y = 0; y < Height; y++)
        {
            int row = y * (Width * 4 + 1);
            raw[row] = 0; // no filter
            for (int x = 0; x < Width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < _s; sy++)
                    for (int sx = 0; sx < _s; sx++)
                    {
                        int i = (y * _s + sy) * _w + x * _s + sx;
                        float pa = _a[i];
                        r += _r[i] * pa; g += _g[i] * pa; b += _b[i] * pa; a += pa;
                    }
                int o = row + 1 + x * 4;
                if (a > 0) { raw[o] = Byte(r / a); raw[o + 1] = Byte(g / a); raw[o + 2] = Byte(b / a); }
                raw[o + 3] = Byte(a * inv);
            }
        }
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, Width); BigEndian(ihdr, 4, Height);
        ihdr[8] = 8; ihdr[9] = 6; // 8 bits, RGBA
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        File.WriteAllBytes(path, ms.ToArray());
    }

    static byte Byte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    static void BigEndian(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc(head.AsSpan(4, 4), 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        var c = new byte[4];
        BigEndian(c, 0, (int)crc);
        s.Write(c);
    }

    static uint[]? _crcTable;

    static uint Crc(ReadOnlySpan<byte> data, uint crc)
    {
        var t = _crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        foreach (var b in data) crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
