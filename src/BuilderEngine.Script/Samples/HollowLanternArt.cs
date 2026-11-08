using System.IO.Compression;

namespace BuilderEngine.Script.Samples;

/// <summary>
/// The pictures of the Hollow Lantern sample, painted pixel by pixel (fixed seeds, so every build is the same) and
/// written as small PNGs. Tiles are 16 × 16 (drawn into 32-pixel cells, crisp); characters 16 × 16; the Warden 32 × 32.
/// All original art: a hooded lantern-bearer, shadow blobs with red eyes, a stone-and-murk guardian.
/// </summary>
static class HollowLanternArt
{
    public static IEnumerable<(string File, byte[] Png)> All()
    {
        yield return ("floor.png", Floor("#29231F", "#322A25", "#1F1A17", "#3D342D", 11));
        yield return ("floor_moss.png", Floor("#232A20", "#2E3A28", "#1B2018", "#3E5232", 12));
        yield return ("floor_hall.png", HallFloor());
        yield return ("dirt.png", Wall("#6B4A32", "#80593C", "#4E3524", 21, (p, r) => p.Speckle(r, 10, "#5A3E2A").Speckle(r, 5, "#8A6444")));
        yield return ("stone.png", Wall("#5E5A57", "#76716D", "#423F3D", 22, (p, r) => p.Speckle(r, 8, "#4F4B49").Crack(r, "#3E3B39")));
        yield return ("coal.png", Wall("#57534F", "#6E6965", "#3D3A38", 23, (p, r) => p.Blobs(r, 4, 2, "#1B1918", "#2C2927")));
        yield return ("copper.png", Wall("#5A5450", "#716A65", "#3F3B38", 24, (p, r) => p.Blobs(r, 4, 2, "#B9652F", "#E8A066")));
        yield return ("emberite.png", Wall("#3E3532", "#544843", "#2A2321", 25, (p, r) => p.Crystals(r, 4, "#E0661F", "#FFC26A", "#FFF0C0")));
        yield return ("skyglass.png", Wall("#2F3A44", "#43515E", "#1F272F", 26, (p, r) => p.Crystals(r, 4, "#3AA6D8", "#8FE4FF", "#F0FFFF")));
        yield return ("deepstone.png", Wall("#2C2340", "#3D3158", "#1C162A", 27, (p, r) => p.Veins(r, "#5B4786", "#7A62B0")));
        yield return ("bedrock.png", Wall("#141211", "#211E1C", "#0A0909", 28, (p, r) => p.Speckle(r, 14, "#1C1918")));
        yield return ("wick.png", Wick());
        yield return ("gloomling.png", Gloom(false));
        yield return ("deep_gloom.png", Gloom(true));
        yield return ("warden.png", Warden(false));
        yield return ("warden_glow.png", Warden(true));
        yield return ("shard.png", Shard());
        yield return ("slam_mark.png", SlamMark(false));
        yield return ("slam_hit.png", SlamMark(true));
        yield return ("slash.png", Slash());
        yield return ("torch.png", Torch());
        yield return ("glowcap.png", Glowcap());
        yield return ("workbench.png", Workbench());
        yield return ("item_stone.png", Gem("#7E7873", "#A39C96", "#55504C", rock: true));
        yield return ("item_coal.png", Gem("#2A2725", "#4A4542", "#141211", rock: true));
        yield return ("item_copper.png", Gem("#C26A30", "#F0A86C", "#7E3F18", rock: true));
        yield return ("item_ember.png", Gem("#F07A24", "#FFD48A", "#9A3E0E", rock: false));
        yield return ("item_skyglass.png", Gem("#3FB2E6", "#C8F4FF", "#1B6A96", rock: false));
        yield return ("murk.png", Gem("#7A4FC8", "#C9A8FF", "#3C2470", rock: false));
        yield return ("copper_pick.png", Pick("#D07A3C", "#F2B27A"));
        yield return ("ember_pick.png", Pick("#F08A2A", "#FFE08A"));
        yield return ("glow_stew.png", Stew());
        yield return ("bright_lantern.png", Lantern());
    }

    // ---------------- Tiles ----------------

    static byte[] Floor(string bg, string light, string dark, string pebble, int seed)
    {
        var r = new Random(seed);
        var p = new Pix(16, 16).Fill(bg).Speckle(r, 22, light).Speckle(r, 22, dark);
        for (int i = 0; i < 3; i++) { int x = r.Next(1, 14), y = r.Next(1, 14); p.Set(x, y, pebble).Set(x + 1, y, pebble).Set(x, y + 1, dark); }
        return p.Png();
    }

    static byte[] HallFloor()
    {
        var p = new Pix(16, 16).Fill("#221B2C").Speckle(new Random(13), 18, "#2A2236");
        for (int i = 0; i < 16; i++) { p.Set(i, 0, "#2E2540"); p.Set(0, i, "#2E2540"); }
        p.Set(5, 5, "#4A3A6E").Set(6, 6, "#4A3A6E").Set(7, 7, "#5B4888").Set(8, 8, "#4A3A6E").Set(9, 9, "#4A3A6E");
        p.Set(9, 5, "#3E3160").Set(5, 9, "#3E3160");
        return p.Png();
    }

    /// <summary>A block seen from above: its top lighter towards the top-left, a shadow at the bottom-right.</summary>
    static byte[] Wall(string baseHex, string lightHex, string darkHex, int seed, Action<Pix, Random> detail)
    {
        var r = new Random(seed);
        var p = new Pix(16, 16).Fill(baseHex);
        detail(p, r);
        for (int i = 0; i < 16; i++)
        {
            p.Set(i, 0, lightHex).Set(0, i, lightHex);
            p.Set(i, 15, darkHex).Set(15, i, darkHex).Set(i, 14, Pix.Mix(baseHex, darkHex, 0.5));
        }
        return p.Png();
    }

    // ---------------- Characters ----------------

    static byte[] Wick()
    {
        var p = new Pix(16, 16);
        // Cloak and hood (facing right), boots, a lantern held out in front.
        p.Disc(7, 5, 4, "#2E4866");
        p.Rect(3, 7, 9, 6, "#3B5A7A");
        p.Rect(4, 13, 2, 2, "#2A1E18").Rect(8, 13, 2, 2, "#2A1E18");
        p.Rect(4, 8, 1, 5, "#4C7096");
        p.Rect(8, 4, 3, 3, "#F2D2A8");             // face
        p.Set(10, 5, "#1A1A1A");                    // eye
        p.Rect(5, 2, 4, 1, "#253B55");              // hood top
        p.Rect(11, 9, 2, 1, "#8A6A40");             // arm
        p.Rect(12, 8, 3, 4, "#6A4A2A");             // lantern frame
        p.Rect(13, 9, 1, 2, "#FFE08A");             // flame
        p.Set(12, 7, "#8A6A40").Set(14, 7, "#8A6A40");
        return p.Png();
    }

    static byte[] Gloom(bool deep)
    {
        var p = new Pix(16, 16);
        string body = deep ? "#2A1440" : "#120E1A", edge = deep ? "#4A2A70" : "#261E34", eye = deep ? "#FFB040" : "#FF4A3A";
        p.Disc(8, 8, 6, body);
        for (int i = 3; i <= 12; i += 3) p.Rect(i, 13, 1, 2, body);
        p.Ring(8, 8, 6, edge);
        if (deep) { p.Set(3, 2, edge).Set(4, 3, edge).Set(12, 2, edge).Set(11, 3, edge).Set(8, 1, edge); }
        p.Rect(5, 6, 2, 2, eye).Rect(10, 6, 2, 2, eye);
        p.Set(5, 6, "#FFFFFF").Set(10, 6, "#FFFFFF");
        p.Rect(6, 10, 4, 1, deep ? "#1A0A2A" : "#060408");
        return p.Png();
    }

    static byte[] Warden(bool glow)
    {
        var p = new Pix(32, 32);
        string stone = "#2B2238", rim = "#40335A", core = glow ? "#E0C8FF" : "#9B6BFF", crack = glow ? "#C9A8FF" : "#7A52C8";
        p.Disc(16, 18, 13, stone);
        p.Ring(16, 18, 13, rim);
        // Horns
        for (int i = 0; i < 6; i++) { p.Rect(4 + i / 2, 9 - i, 2, 1, rim); p.Rect(26 - i / 2, 9 - i, 2, 1, rim); }
        // Hollow eyes
        p.Rect(9, 12, 5, 4, "#0A0710").Rect(18, 12, 5, 4, "#0A0710");
        p.Rect(10, 13, 2, 2, glow ? "#FFFFFF" : "#D8C8FF").Rect(20, 13, 2, 2, glow ? "#FFFFFF" : "#D8C8FF");
        // Cracks glowing from the core
        p.Disc(16, 22, 3, core);
        p.Line(16, 22, 8, 27, crack).Line(16, 22, 25, 27, crack).Line(16, 22, 16, 30, crack).Line(16, 22, 6, 20, crack).Line(16, 22, 27, 19, crack);
        p.Rect(12, 18, 8, 1, "#0A0710");            // mouth line
        if (glow) p.Ring(16, 18, 14, "#C9A8FF");
        return p.Png();
    }

    static byte[] Shard()
    {
        var p = new Pix(8, 8);
        p.Line(4, 0, 7, 4, "#B080FF").Line(7, 4, 4, 7, "#7A52C8").Line(4, 7, 0, 4, "#7A52C8").Line(0, 4, 4, 0, "#B080FF");
        p.Rect(3, 2, 2, 4, "#D8C8FF").Set(4, 3, "#FFFFFF");
        return p.Png();
    }

    static byte[] SlamMark(bool hit)
    {
        var p = new Pix(32, 32);
        if (hit)
        {
            var r = new Random(31);
            p.Disc(16, 16, 14, "#FFB070", 120);
            p.Disc(16, 16, 9, "#FFE0B0", 170);
            for (int i = 0; i < 12; i++) { double a = i * Math.PI / 6 + r.NextDouble() * 0.3; p.Line(16, 16, 16 + (int)(Math.Cos(a) * 15), 16 + (int)(Math.Sin(a) * 15), "#3A2A20"); }
            return p.Png();
        }
        p.Disc(16, 16, 15, "#E03020", 70);
        p.Ring(16, 16, 15, "#FF5040");
        p.Ring(16, 16, 14, "#FF5040");
        p.Ring(16, 16, 9, "#FF7050");
        p.Line(16, 3, 16, 29, "#FF7050").Line(3, 16, 29, 16, "#FF7050");
        return p.Png();
    }

    static byte[] Slash()
    {
        var p = new Pix(16, 16);
        for (int i = 0; i < 9; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / 8;
            int x = 7 + (int)Math.Round(Math.Cos(a) * 6), y = 8 + (int)Math.Round(Math.Sin(a) * 6);
            p.Set(x, y, "#FFF4D0").Set(x - 1, y, "#FFD27A", 180);
        }
        return p.Png();
    }

    static byte[] Torch()
    {
        var p = new Pix(8, 10);
        p.Rect(3, 4, 2, 6, "#6A4424").Set(3, 9, "#4A2E18");
        p.Rect(2, 3, 4, 1, "#3A3430");
        p.Rect(2, 1, 4, 2, "#FF8A2A").Rect(3, 0, 2, 2, "#FFD27A").Set(3, 1, "#FFF6D0");
        return p.Png();
    }

    static byte[] Glowcap()
    {
        var p = new Pix(8, 8);
        p.Rect(3, 4, 2, 4, "#E8F0E0");
        p.Rect(1, 2, 6, 2, "#3FD8B8").Rect(2, 1, 4, 1, "#7FF0D8");
        p.Set(2, 2, "#C8FFF0").Set(5, 3, "#C8FFF0");
        return p.Png();
    }

    static byte[] Workbench()
    {
        var p = new Pix(24, 16);
        p.Rect(1, 2, 22, 12, "#7A5230");
        for (int y = 4; y < 14; y += 3) p.Rect(1, y, 22, 1, "#5E3E22");
        p.Rect(1, 2, 22, 1, "#9A6C42");
        p.Rect(2, 14, 2, 2, "#4A3018").Rect(20, 14, 2, 2, "#4A3018");
        p.Rect(13, 4, 7, 3, "#5A5A62").Rect(14, 7, 5, 2, "#46464E").Rect(12, 4, 1, 2, "#5A5A62"); // anvil
        p.Line(4, 4, 9, 9, "#C8C8D0").Rect(3, 3, 3, 2, "#8A8A92");                              // pick on the table
        p.Rect(9, 10, 2, 2, "#FFD27A").Set(9, 9, "#FFF6D0");                                   // candle
        return p.Png();
    }

    // ---------------- Item icons ----------------

    static byte[] Gem(string mid, string light, string dark, bool rock)
    {
        var p = new Pix(16, 16);
        if (rock)
        {
            p.Disc(8, 9, 5, mid);
            p.Disc(6, 7, 2, light);
            p.Ring(8, 9, 5, dark);
        }
        else
        {
            for (int y = 0; y < 10; y++)
            {
                int half = y < 4 ? y + 1 : 9 - y;
                p.Rect(8 - half, 3 + y, half * 2, 1, y < 4 ? light : mid);
            }
            p.Line(8, 3, 3, 7, dark).Line(8, 3, 12, 7, dark).Line(3, 8, 8, 12, dark).Line(12, 8, 8, 12, dark);
            p.Set(7, 5, "#FFFFFF");
        }
        return p.Png();
    }

    static byte[] Pick(string head, string shine)
    {
        var p = new Pix(16, 16);
        p.Line(4, 13, 11, 6, "#7A5230").Line(5, 13, 12, 6, "#5E3E22");
        for (int i = 0; i < 9; i++) { double a = Math.PI * (1.05 + i * 0.11); p.Set(9 + (int)Math.Round(Math.Cos(a) * -5) - 2, 7 + (int)Math.Round(Math.Sin(a) * 5) - 2, head); }
        p.Line(6, 3, 13, 4, head).Line(12, 3, 14, 9, head).Line(7, 2, 12, 2, shine);
        return p.Png();
    }

    static byte[] Stew()
    {
        var p = new Pix(16, 16);
        p.Rect(3, 8, 10, 4, "#6A4A30").Rect(4, 12, 8, 1, "#4E3420");
        p.Rect(3, 7, 10, 1, "#8A6440");
        p.Rect(4, 6, 8, 2, "#3FD8B8").Set(6, 6, "#C8FFF0").Set(9, 7, "#C8FFF0");
        p.Set(6, 3, "#BFBFBF", 140).Set(7, 4, "#BFBFBF", 140).Set(9, 2, "#BFBFBF", 120).Set(10, 3, "#BFBFBF", 140);
        return p.Png();
    }

    static byte[] Lantern()
    {
        var p = new Pix(16, 16);
        p.Rect(7, 1, 2, 2, "#8A8A92").Rect(5, 3, 6, 1, "#6A6A72");
        p.Rect(5, 4, 6, 8, "#3FB2E6").Rect(6, 5, 4, 6, "#C8F4FF").Rect(7, 6, 2, 3, "#FFFFFF");
        p.Rect(4, 4, 1, 8, "#5A5A62").Rect(11, 4, 1, 8, "#5A5A62").Rect(4, 12, 8, 2, "#5A5A62");
        return p.Png();
    }

    // ---------------- A tiny canvas and PNG writer ----------------

    sealed class Pix(int w, int h)
    {
        readonly uint[] _px = new uint[w * h]; // ARGB

        public static uint Color(string hex, int alpha = 255)
        {
            int rgb = World.ParseHex(hex.Length > 7 ? hex[..7] : hex, 0xFF00FF);
            return ((uint)Math.Clamp(alpha, 0, 255) << 24) | (uint)rgb;
        }

        public static string Mix(string a, string b, double k)
        {
            int ca = World.ParseHex(a, 0), cb = World.ParseHex(b, 0);
            int Ch(int s) => (int)Math.Round(((ca >> s) & 255) * (1 - k) + ((cb >> s) & 255) * k);
            return $"#{Ch(16):X2}{Ch(8):X2}{Ch(0):X2}";
        }

        public Pix Set(int x, int y, string hex, int alpha = 255)
        {
            if ((uint)x < (uint)w && (uint)y < (uint)h) _px[y * w + x] = Color(hex, alpha);
            return this;
        }

        public Pix Fill(string hex) { Array.Fill(_px, Color(hex)); return this; }

        public Pix Rect(int x, int y, int rw, int rh, string hex, int alpha = 255)
        {
            for (int j = y; j < y + rh; j++) for (int i = x; i < x + rw; i++) Set(i, j, hex, alpha);
            return this;
        }

        public Pix Disc(int cx, int cy, int r, string hex, int alpha = 255)
        {
            for (int j = cy - r; j <= cy + r; j++)
                for (int i = cx - r; i <= cx + r; i++)
                    if ((i - cx) * (i - cx) + (j - cy) * (j - cy) <= r * r + r) Set(i, j, hex, alpha);
            return this;
        }

        public Pix Ring(int cx, int cy, int r, string hex)
        {
            for (int k = 0; k < r * 8; k++)
            {
                double a = k * Math.PI * 2 / (r * 8);
                Set(cx + (int)Math.Round(Math.Cos(a) * r), cy + (int)Math.Round(Math.Sin(a) * r), hex);
            }
            return this;
        }

        public Pix Line(int x0, int y0, int x1, int y1, string hex)
        {
            int n = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
            for (int i = 0; i <= n; i++)
            {
                double f = n == 0 ? 0 : (double)i / n;
                Set((int)Math.Round(x0 + (x1 - x0) * f), (int)Math.Round(y0 + (y1 - y0) * f), hex);
            }
            return this;
        }

        public Pix Speckle(Random r, int count, string hex)
        {
            for (int i = 0; i < count; i++) Set(r.Next(w), r.Next(h), hex);
            return this;
        }

        public Pix Crack(Random r, string hex)
        {
            int x = r.Next(3, 12), y = r.Next(3, 12);
            for (int i = 0; i < 6; i++) { Set(x, y, hex); x += r.Next(-1, 2); y += 1; }
            return this;
        }

        /// <summary>Lumps of ore: a dark body with a lighter pixel on top.</summary>
        public Pix Blobs(Random r, int count, int size, string body, string shine)
        {
            for (int i = 0; i < count; i++)
            {
                int x = r.Next(2, 13 - size), y = r.Next(2, 13 - size);
                Rect(x, y, size + 1, size, body);
                Set(x, y, shine);
            }
            return this;
        }

        /// <summary>Glowing crystals poking out of the rock.</summary>
        public Pix Crystals(Random r, int count, string dark, string mid, string bright)
        {
            for (int i = 0; i < count; i++)
            {
                int x = r.Next(2, 12), y = r.Next(4, 13), len = r.Next(2, 5);
                for (int k = 0; k < len; k++) { Set(x + k / 2, y - k, mid); Set(x + k / 2 + 1, y - k, dark); }
                Set(x + len / 2, y - len + 1, bright);
            }
            return this;
        }

        public Pix Veins(Random r, string dark, string light)
        {
            for (int v = 0; v < 2; v++)
            {
                int x = r.Next(1, 14), y = 1;
                while (y < 15) { Set(x, y, light); if (r.Next(3) == 0) Set(x + 1, y, dark.Length > 7 ? dark[..7] : dark); x = Math.Clamp(x + r.Next(-1, 2), 1, 14); y++; }
            }
            return this;
        }

        public byte[] Png()
        {
            using var raw = new MemoryStream();
            for (int y = 0; y < h; y++)
            {
                raw.WriteByte(0); // no filter
                for (int x = 0; x < w; x++)
                {
                    uint c = _px[y * w + x];
                    raw.WriteByte((byte)(c >> 16)); raw.WriteByte((byte)(c >> 8)); raw.WriteByte((byte)c); raw.WriteByte((byte)(c >> 24));
                }
            }
            using var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zs);
            using var png = new MemoryStream();
            png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)w); WriteBE(ihdr, 4, (uint)h);
            ihdr[8] = 8; ihdr[9] = 6; // 8 bits, RGBA
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", z.ToArray());
            Chunk(png, "IEND", []);
            return png.ToArray();
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, (uint)data.Length);
            s.Write(len);
            var td = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
            Array.Copy(data, 0, td, 4, data.Length);
            s.Write(td);
            var crc = new byte[4];
            WriteBE(crc, 0, Crc(td));
            s.Write(crc);
        }

        static void WriteBE(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

        static readonly uint[] CrcTable = MakeCrcTable();
        static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        static uint Crc(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
