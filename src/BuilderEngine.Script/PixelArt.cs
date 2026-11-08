using System.Globalization;
using System.IO.Compression;

namespace BuilderEngine.Script;

/// <summary>
/// Small pictures made from code, for sample games and starter projects that ship without image files: a PNG encoder
/// (no packages: zlib from the framework) and pixel art written as text, one character per pixel.
/// <code>
/// var png = PixelArt.FromText(["..##..", ".#oo#."], new() { ['#'] = "#3A2A1A", ['o'] = "#E8B04B" }, scale: 2);
/// PixelArt.Save(Path.Combine(Images.Folder(dir), "coin.png"), png);
/// </code>
/// '.' and ' ' are transparent; a character missing from the palette is transparent too.
/// </summary>
public static class PixelArt
{
    /// <summary>A PNG (8-bit RGBA) of <paramref name="width"/> × <paramref name="height"/> pixels, given as 0xAARRGGBB, row by row.</summary>
    public static byte[] Png(int width, int height, uint[] argb)
    {
        if (width < 1 || height < 1 || argb.Length < width * height) throw new ArgumentException("The pixels don't fill the picture.");
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            for (int x = 0; x < width; x++)
            {
                uint p = argb[y * width + x];
                raw.WriteByte((byte)(p >> 16)); raw.WriteByte((byte)(p >> 8)); raw.WriteByte((byte)p); raw.WriteByte((byte)(p >> 24));
            }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zs);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)width);
        BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bits per channel
        ihdr[9] = 6;  // RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// Pixel art from text: every row is a line of characters, each looked up in <paramref name="palette"/> ("#RGB",
    /// "#RRGGBB" or "#AARRGGBB"). Rows shorter than the longest are padded with transparency. Each pixel becomes
    /// <paramref name="scale"/> × <paramref name="scale"/> pixels.
    /// </summary>
    public static byte[] FromText(IReadOnlyList<string> rows, IReadOnlyDictionary<char, string> palette, int scale = 1)
    {
        var (w, h, px) = Pixels(rows, palette);
        return Scaled(w, h, px, scale);
    }

    /// <summary>The pixels of text art (see <see cref="FromText"/>), unscaled: width, height and 0xAARRGGBB values.</summary>
    public static (int Width, int Height, uint[] Argb) Pixels(IReadOnlyList<string> rows, IReadOnlyDictionary<char, string> palette)
    {
        int h = Math.Max(1, rows.Count), w = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Length));
        var colors = new Dictionary<char, uint>();
        foreach (var (ch, hex) in palette) colors[ch] = Color(hex);
        var px = new uint[w * h];
        for (int y = 0; y < rows.Count; y++)
            for (int x = 0; x < rows[y].Length; x++)
                if (colors.TryGetValue(rows[y][x], out var c) && rows[y][x] is not ('.' or ' ')) px[y * w + x] = c;
        return (w, h, px);
    }

    /// <summary>A PNG of pixels made bigger by a whole number (crisp: every pixel becomes a square).</summary>
    public static byte[] Scaled(int width, int height, uint[] argb, int scale)
    {
        scale = Math.Clamp(scale, 1, 64);
        if (scale == 1) return Png(width, height, argb);
        int sw = width * scale, sh = height * scale;
        var big = new uint[sw * sh];
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
                big[y * sw + x] = argb[(y / scale) * width + x / scale];
        return Png(sw, sh, big);
    }

    /// <summary>"#RGB", "#RRGGBB" or "#AARRGGBB" → 0xAARRGGBB (opaque unless an alpha is given; transparent when it isn't a colour).</summary>
    public static uint Color(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return 0;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length == 6) s = "FF" + s;
        return s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>A colour made lighter (amount &gt; 0) or darker (&lt; 0), -1..1, keeping its alpha.</summary>
    public static uint Shade(uint argb, double amount)
    {
        static byte Ch(uint v, int shift, double k) => (byte)Math.Clamp(Math.Round(k >= 0 ? ((v >> shift) & 0xFF) + (255 - ((v >> shift) & 0xFF)) * k : ((v >> shift) & 0xFF) * (1 + k)), 0, 255);
        amount = Math.Clamp(amount, -1, 1);
        return (argb & 0xFF000000) | ((uint)Ch(argb, 16, amount) << 16) | ((uint)Ch(argb, 8, amount) << 8) | Ch(argb, 0, amount);
    }

    /// <summary>Writes a PNG into a file (making its folder).</summary>
    public static void Save(string path, byte[] png)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, png);
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BigEndian(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        var c = new byte[4];
        BigEndian(c, 0, crc);
        s.Write(c);
    }

    static void BigEndian(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    static readonly uint[] CrcTable = MakeCrcTable();

    static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
