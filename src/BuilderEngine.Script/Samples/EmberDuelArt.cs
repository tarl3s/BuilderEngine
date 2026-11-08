using System.IO.Compression;

namespace BuilderEngine.Script;

/// <summary>
/// The pictures of the Ember Duel sample, drawn in code as small pixel art and written as PNGs: one face per card
/// (frame, name band, art window with the card's creature or symbol, rules box — the numbers and texts are labels
/// drawn by the game), the card back, the table, lanes, hero portraits and the turn banner. Everything is original.
/// </summary>
static class EmberDuelArt
{
    // ---------------- A tiny canvas and a PNG writer ----------------

    internal sealed class Canvas(int w, int h)
    {
        public readonly int W = w, H = h;
        public readonly uint[] Px = new uint[w * h]; // 0xAARRGGBB

        public void Set(int x, int y, uint c)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            uint a = c >> 24;
            if (a == 255) { Px[y * W + x] = c; return; }
            if (a == 0) return;
            uint d = Px[y * W + x], da = d >> 24;
            double k = a / 255.0;
            uint Mix(int shift) => (uint)Math.Round(((c >> shift) & 255) * k + ((d >> shift) & 255) * (1 - k));
            uint na = (uint)Math.Min(255, a + da * (1 - k));
            Px[y * W + x] = (na << 24) | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
        }

        public void Fill(int x0, int y0, int w, int h, uint c)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++) Set(x, y, c);
        }

        public void VGradient(int x0, int y0, int w, int h, uint top, uint bottom)
        {
            for (int y = 0; y < h; y++)
            {
                uint c = Lerp(top, bottom, h <= 1 ? 0 : (double)y / (h - 1));
                for (int x = 0; x < w; x++) Set(x0 + x, y0 + y, c);
            }
        }

        public void Glow(double cx, double cy, double r, uint color, double strength)
        {
            for (int y = (int)(cy - r); y <= cy + r; y++)
                for (int x = (int)(cx - r); x <= cx + r; x++)
                {
                    double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy)) / r;
                    if (d >= 1) continue;
                    double a = (1 - d) * (1 - d) * strength;
                    // Dithered steps keep the pixel-art look.
                    a = Math.Round(a * 4) / 4;
                    if (a > 0) Set(x, y, WithAlpha(color, a));
                }
        }

        public void Sprite(string[] rows, int x0, int y0, Dictionary<char, uint> pal, int scale = 1)
        {
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                    if (pal.TryGetValue(rows[y][x], out var c))
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++) Set(x0 + x * scale + sx, y0 + y * scale + sy, c);
        }

        public Canvas Scaled(int k)
        {
            var o = new Canvas(W * k, H * k);
            for (int y = 0; y < o.H; y++)
                for (int x = 0; x < o.W; x++) o.Px[y * o.W + x] = Px[(y / k) * W + x / k];
            return o;
        }

        public byte[] Png()
        {
            using var ms = new MemoryStream();
            ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var ihdr = new byte[13];
            BigEndian(ihdr, 0, (uint)W); BigEndian(ihdr, 4, (uint)H);
            ihdr[8] = 8; ihdr[9] = 6; // 8 bits, RGBA
            Chunk(ms, "IHDR", ihdr);
            var raw = new byte[H * (W * 4 + 1)];
            for (int y = 0, i = 0; y < H; y++)
            {
                raw[i++] = 0; // no filter
                for (int x = 0; x < W; x++)
                {
                    uint c = Px[y * W + x];
                    raw[i++] = (byte)(c >> 16); raw[i++] = (byte)(c >> 8); raw[i++] = (byte)c; raw[i++] = (byte)(c >> 24);
                }
            }
            using (var z = new MemoryStream())
            {
                using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
                Chunk(ms, "IDAT", z.ToArray());
            }
            Chunk(ms, "IEND", []);
            return ms.ToArray();
        }

        static void BigEndian(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            BigEndian(len, 0, (uint)data.Length);
            s.Write(len);
            var td = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
            Array.Copy(data, 0, td, 4, data.Length);
            s.Write(td);
            var crc = new byte[4];
            BigEndian(crc, 0, Crc(td));
            s.Write(crc);
        }

        static uint[]? _crcTable;
        static uint Crc(byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            uint r = 0xFFFFFFFFu;
            foreach (var b in data) r = _crcTable[(r ^ b) & 0xFF] ^ (r >> 8);
            return r ^ 0xFFFFFFFFu;
        }
    }

    static uint C(string hex) => 0xFF000000u | Convert.ToUInt32(hex.TrimStart('#'), 16);
    static uint WithAlpha(uint c, double a) => ((uint)Math.Clamp(Math.Round(a * 255), 0, 255) << 24) | (c & 0xFFFFFF);

    static uint Lerp(uint a, uint b, double t)
    {
        uint L(int s) => (uint)Math.Round(((a >> s) & 255) + ((((b >> s) & 255) - (double)((a >> s) & 255)) * t));
        return (L(24) << 24) | (L(16) << 16) | (L(8) << 8) | L(0);
    }

    // ---------------- Elements ----------------

    sealed record Element(uint Frame, uint Hi, uint Lo, uint BgTop, uint BgBottom, uint Glow, uint A, uint B, uint Cc, uint D, uint E);

    static readonly Dictionary<string, Element> Elements = new()
    {
        ["ember"] = new(C("C65A2E"), C("F08A4B"), C("7A2E14"), C("2A0E07"), C("8A2E12"), C("FF9A3C"), C("E8743A"), C("A8401C"), C("FFC46B"), C("FFE08A"), C("FFF6C0")),
        ["tide"] = new(C("2F7DB5"), C("66B4E6"), C("164166"), C("071A30"), C("1B5C8A"), C("6FD3FF"), C("4FA3D9"), C("2A628F"), C("A9E3FF"), C("E0F7FF"), C("FFFFFF")),
        ["grove"] = new(C("4E9A3A"), C("8FD06A"), C("23501A"), C("0C2410"), C("2F6E26"), C("B6F27A"), C("6DBA4A"), C("3B7A2A"), C("B8EC84"), C("8B5A2B"), C("FFF3A0")),
        ["dusk"] = new(C("7A4FB2"), C("B48AE6"), C("3B2160"), C("12091F"), C("432870"), C("D79BFF"), C("8E62C9"), C("5A3A8C"), C("CDA8FF"), C("F2C14E"), C("FFE27A")),
    };

    /// <summary>Each card's look: its element, whether it's a spell, the motif drawn in the art window and colour changes.</summary>
    internal static readonly (string Name, string Element, bool Spell, string Motif, string? A, string? B, string? Cc)[] Cards =
    [
        ("Cinder Pup", "ember", false, "beast", null, null, null),
        ("Ash Sprite", "ember", false, "wisp", "C9C2BA", "8A8078", "FFB070"),
        ("Kindle Fox", "ember", false, "beast", "F06A28", "A83E14", "FFD08A"),
        ("Brazier Golem", "ember", false, "golem", "6E5A4A", "463830", "FF9A3C"),
        ("Flare Drake", "ember", false, "dragon", null, null, null),
        ("Molten Titan", "ember", false, "golem", "B8402A", "6E2214", "FFB050"),
        ("Spark Bolt", "ember", true, "bolt", "FFD24A", "C88A1A", "FFF6C0"),
        ("Fire Lance", "ember", true, "flame", null, null, null),
        ("Ember Storm", "ember", true, "meteor", null, null, null),
        ("Reef Crab", "tide", false, "crab", "E0603A", "A03A20", "FFB08A"),
        ("Mist Heron", "tide", false, "bird", "DDE8F0", "8FA8BC", "FFFFFF"),
        ("Tide Caller", "tide", false, "hood", "3A78B0", "1E4A78", "8FD0FF"),
        ("Coral Warden", "tide", false, "knight", "E07A8A", "A04A5A", "FFC0C8"),
        ("Deepwater Serpent", "tide", false, "serpent", null, null, null),
        ("Deep Insight", "tide", true, "cards", "E8DCC0", "A89878", "FFFFFF"),
        ("Tidal Ward", "tide", true, "shield", null, null, null),
        ("Moss Beetle", "grove", false, "insect", null, null, null),
        ("Thorn Hedgehog", "grove", false, "beast", "8B6A3E", "5A4020", "C9A36A"),
        ("Grove Stag", "grove", false, "stag", "A8743A", "6E4A22", "E0B070"),
        ("Bramble Knight", "grove", false, "knight", null, null, null),
        ("Elder Oakheart", "grove", false, "tree", null, null, null),
        ("Healing Spring", "grove", true, "heart", "7CE08A", "3E9A4E", "D8FFD8"),
        ("Mending Dew", "grove", true, "drop", "A8E8F0", "5AA8B8", "FFFFFF"),
        ("Rally Horn", "grove", true, "horn", "E0B050", "9A7020", "FFE6A0"),
        ("Dusk Moth", "dusk", false, "moth", null, null, null),
        ("Gloom Lurker", "dusk", false, "hood", "3A2A50", "22183A", "6A5A8A"),
        ("Lantern Wisp", "dusk", false, "wisp", "F2C14E", "B8862A", "FFF0A0"),
        ("Rune Sentinel", "dusk", false, "golem", "6A5A8A", "403458", "D79BFF"),
        ("Hollow Colossus", "dusk", false, "golem", "4A3A60", "2A1E3A", "8A72B0"),
        ("Shadow Spike", "dusk", true, "bolt", "B48AE6", "6A3EA8", "F0D8FF"),
    ];

    // ---------------- Writing every picture ----------------

    internal static void WriteAll(string imagesDir)
    {
        Directory.CreateDirectory(imagesDir);
        void Save(string name, Canvas c, int scale) => File.WriteAllBytes(Path.Combine(imagesDir, name + ".png"), c.Scaled(scale).Png());
        foreach (var card in Cards) Save(card.Name, CardFace(card), 2);
        Save("Card Back", CardBack(), 2);
        Save("Table", Table(), 2);
        Save("Lane", Lane(C("C9A36A")), 2);
        Save("Lane Rival", Lane(C("B48AE6")), 2);
        Save("Hero You", Portrait(Sprites["knight"], Elements["ember"], C("D9822B"), C("8A4A1E"), C("FFD08A")), 2);
        Save("Hero Rival", Portrait(Sprites["hood"], Elements["dusk"], C("5A3A8C"), C("2E1E4A"), C("B48AE6")), 2);
        Save("Banner", Banner(), 2);
    }

    static Dictionary<char, uint> Palette(Element el, string? a, string? b, string? c) => new()
    {
        ['o'] = C("1B110B"),
        ['a'] = a != null ? C(a) : el.A,
        ['b'] = b != null ? C(b) : el.B,
        ['c'] = c != null ? C(c) : el.Cc,
        ['d'] = el.D,
        ['e'] = el.E,
    };

    /// <summary>A 38 × 53 card face: frame, name band, art window with its motif, gem, rules box.</summary>
    static Canvas CardFace((string Name, string Element, bool Spell, string Motif, string? A, string? B, string? Cc) card)
    {
        var el = Elements[card.Element];
        var cv = new Canvas(38, 53);
        uint frame = card.Spell ? Lerp(el.Frame, C("C8CED8"), 0.55) : el.Frame;
        uint hi = card.Spell ? Lerp(el.Hi, C("FFFFFF"), 0.5) : el.Hi;
        uint lo = card.Spell ? Lerp(el.Lo, C("5A6070"), 0.5) : el.Lo;
        uint ink = C("1B110B");
        // Body and frame (corners cut for a rounded look).
        cv.Fill(0, 0, 38, 53, ink);
        cv.Fill(1, 1, 36, 51, frame);
        for (int x = 1; x < 37; x++) { cv.Set(x, 1, hi); cv.Set(x, 51, lo); }
        for (int y = 1; y < 52; y++) { cv.Set(1, y, hi); cv.Set(36, y, lo); }
        foreach (var (x, y) in new[] { (0, 0), (37, 0), (0, 52), (37, 52) }) cv.Px[y * 38 + x] = 0;
        foreach (var (x, y) in new[] { (1, 1), (36, 1), (1, 51), (36, 51) }) cv.Set(x, y, ink);
        // Name band.
        cv.VGradient(3, 3, 32, 6, C("3A281C"), C("1E140E"));
        for (int x = 3; x < 35; x++) cv.Set(x, 9, ink);
        // Art window: element sky, sparks, a glow, the motif.
        int ax = 3, ay = 10, aw = 32, ah = 21;
        cv.VGradient(ax, ay, aw, ah, el.BgTop, el.BgBottom);
        var rng = new Random(Seed(card.Name));
        for (int i = 0; i < 14; i++) cv.Set(ax + rng.Next(aw), ay + rng.Next(ah), WithAlpha(el.Glow, 0.35 + rng.NextDouble() * 0.5));
        cv.Glow(ax + aw / 2.0, ay + ah / 2.0 + 1, 13, el.Glow, 0.55);
        if (card.Spell)
            for (int i = 0; i < 4; i++) // an arch: the window's top corners are frame
                for (int j = 0; j < 4 - i; j++) { cv.Set(ax + j, ay + i, frame); cv.Set(ax + aw - 1 - j, ay + i, frame); }
        var spr = Sprites[card.Motif];
        cv.Sprite(spr, ax + (aw - 16) / 2, ay + ah - 16 - 2, Palette(el, card.A, card.B, card.Cc));
        for (int x = ax; x < ax + aw; x++) cv.Set(x, ay + ah, ink);
        // The divider with a gem: a diamond for creatures, a star for spells.
        int gx = 18, gy = 32;
        cv.Set(gx, gy - 1, ink); cv.Set(gx + 1, gy - 1, ink);
        cv.Fill(gx - 1, gy, 4, 1, ink);
        cv.Set(gx, gy, el.Glow); cv.Set(gx + 1, gy, card.Spell ? C("FFFFFF") : el.Glow);
        // Rules box: parchment with a darker edge.
        int rx = 3, ry = 33, rw = 32, rh = 16;
        cv.Fill(rx, ry, rw, rh, C("B89E74"));
        cv.VGradient(rx + 1, ry + 1, rw - 2, rh - 2, C("F1E4C4"), C("DCC79E"));
        var tex = new Random(Seed(card.Name) + 7);
        for (int i = 0; i < 18; i++) cv.Set(rx + 1 + tex.Next(rw - 2), ry + 1 + tex.Next(rh - 2), WithAlpha(C("B89E74"), 0.35));
        cv.Set(gx, gy, el.Glow);
        return cv;
    }

    static int Seed(string s)
    {
        int h = 17;
        foreach (char ch in s) h = unchecked(h * 31 + ch);
        return h & 0x7FFFFFFF;
    }

    static Canvas CardBack()
    {
        var cv = new Canvas(38, 53);
        uint ink = C("1B110B"), red = C("5A1A12"), red2 = C("7A2618"), gold = C("D8A84A");
        cv.Fill(0, 0, 38, 53, ink);
        cv.Fill(1, 1, 36, 51, gold);
        cv.Fill(2, 2, 34, 49, ink);
        cv.VGradient(3, 3, 32, 47, red2, red);
        for (int y = 3; y < 50; y++) // a diamond lattice
            for (int x = 3; x < 35; x++)
                if (((x + y) % 6 == 0) || ((x - y + 60) % 6 == 0)) cv.Set(x, y, WithAlpha(C("2A0A06"), 0.55));
        cv.Glow(19, 26.5, 14, C("FF9A3C"), 0.6);
        cv.Sprite(Sprites["flame"], 11, 18, new() { ['o'] = ink, ['a'] = gold, ['b'] = C("A8742A"), ['c'] = C("FFE08A"), ['d'] = C("FFF6C0"), ['e'] = C("FFFFFF") });
        foreach (var (x, y) in new[] { (0, 0), (37, 0), (0, 52), (37, 52) }) cv.Px[y * 38 + x] = 0;
        return cv;
    }

    /// <summary>The 400 × 225 table: wooden edges, a green mat where the lanes are, a dark panel for the big card.</summary>
    static Canvas Table()
    {
        var cv = new Canvas(400, 225);
        var rng = new Random(5);
        // Wood planks.
        for (int y = 0; y < 225; y++)
        {
            int plank = y / 15;
            uint baseC = Lerp(C("3A2416"), C("2C1A10"), (plank % 3) / 3.0);
            for (int x = 0; x < 400; x++)
            {
                double grain = Math.Sin((x + plank * 37) * 0.09 + Math.Sin(y * 0.7 + plank) * 1.5) * 0.5 + 0.5;
                uint c = Lerp(baseC, C("4A3020"), grain * 0.35);
                if (y % 15 == 0) c = C("1C1008");
                cv.Px[y * 400 + x] = c;
            }
        }
        // The mat with a gold trim.
        int mx = 76, my = 14, mw = 212, mh = 134;
        cv.Fill(mx - 2, my - 2, mw + 4, mh + 4, C("1B110B"));
        cv.Fill(mx - 1, my - 1, mw + 2, mh + 2, C("B88A3A"));
        cv.VGradient(mx, my, mw, mh / 2, C("2A1C2E"), C("22303A"));
        cv.VGradient(mx, my + mh / 2, mw, mh - mh / 2, C("1E3A30"), C("173026"));
        for (int i = 0; i < 260; i++) cv.Set(mx + rng.Next(mw), my + rng.Next(mh), WithAlpha(C("FFFFFF"), 0.04));
        // The middle line with a little ember in the centre.
        for (int x = mx + 4; x < mx + mw - 4; x++) cv.Set(x, my + mh / 2, (x / 3) % 2 == 0 ? C("B88A3A") : C("6E5226"));
        cv.Glow(mx + mw / 2.0, my + mh / 2.0, 10, C("FF9A3C"), 0.5);
        cv.Fill(mx + mw / 2 - 1, my + mh / 2 - 1, 2, 2, C("FFD08A"));
        // Hero alcoves on the left.
        foreach (int hy in new[] { 18, 82 })
        {
            cv.Fill(10, hy, 60, 60, C("1B110B"));
            cv.VGradient(11, hy + 1, 58, 58, C("2A1C14"), C("140C08"));
        }
        // The right panel (the big card, End turn).
        cv.Fill(294, 6, 100, 160, C("1B110B"));
        cv.VGradient(295, 7, 98, 158, C("241812"), C("140C08"));
        // Embers floating in the wood.
        for (int i = 0; i < 40; i++)
        {
            int x = rng.Next(400), y = 150 + rng.Next(75);
            cv.Set(x, y, WithAlpha(C("FF9A3C"), 0.15 + rng.NextDouble() * 0.25));
        }
        return cv;
    }

    static Canvas Lane(uint tint)
    {
        var cv = new Canvas(44, 58);
        cv.Fill(1, 1, 42, 56, WithAlpha(C("000000"), 0.28));
        for (int x = 2; x < 42; x++)
        {
            bool on = (x / 2) % 2 == 0;
            if (on) { cv.Set(x, 1, WithAlpha(tint, 0.7)); cv.Set(x, 56, WithAlpha(tint, 0.7)); }
        }
        for (int y = 2; y < 56; y++)
        {
            bool on = (y / 2) % 2 == 0;
            if (on) { cv.Set(1, y, WithAlpha(tint, 0.7)); cv.Set(42, y, WithAlpha(tint, 0.7)); }
        }
        // Corner marks and a faint rune in the middle.
        foreach (var (x, y) in new[] { (1, 1), (40, 1), (1, 54), (40, 54) })
            cv.Fill(x, y, 3, 3, WithAlpha(tint, 0.95));
        var rune = new[] { "..o..", ".o.o.", "o.o.o", ".o.o.", "..o.." };
        cv.Sprite(rune, 20, 27, new() { ['o'] = WithAlpha(tint, 0.35) });
        return cv;
    }

    static Canvas Portrait(string[] sprite, Element el, uint a, uint b, uint c)
    {
        var cv = new Canvas(56, 56);
        uint ink = C("1B110B");
        cv.Fill(0, 0, 56, 56, ink);
        cv.Fill(1, 1, 54, 54, C("B88A3A"));
        cv.Fill(2, 2, 52, 52, ink);
        cv.VGradient(3, 3, 50, 50, el.BgTop, el.BgBottom);
        cv.Glow(28, 30, 22, el.Glow, 0.5);
        cv.Sprite(sprite, 12, 14, new() { ['o'] = ink, ['a'] = a, ['b'] = b, ['c'] = c, ['d'] = el.D, ['e'] = el.E }, 2);
        foreach (var (x, y) in new[] { (0, 0), (55, 0), (0, 55), (55, 55) }) cv.Px[y * 56 + x] = 0;
        return cv;
    }

    static Canvas Banner()
    {
        var cv = new Canvas(200, 32);
        uint ink = C("1B110B"), red = C("8A2A18"), red2 = C("B8402A"), gold = C("E0B050");
        // Folded ends.
        for (int y = 6; y < 28; y++)
            for (int x = 0; x < 16; x++)
            {
                bool notch = Math.Abs(y - 17) < 6 - x / 3;
                if (!notch) { cv.Set(x, y, x == 0 || y == 6 || y == 27 ? ink : Lerp(red, C("5A1A10"), 0.4)); cv.Set(199 - x, y, x == 0 || y == 6 || y == 27 ? ink : Lerp(red, C("5A1A10"), 0.4)); }
            }
        cv.Fill(12, 2, 176, 26, ink);
        cv.Fill(13, 3, 174, 24, gold);
        cv.VGradient(14, 4, 172, 22, red2, red);
        for (int x = 16; x < 184; x += 4) { cv.Set(x, 6, WithAlpha(gold, 0.5)); cv.Set(x, 23, WithAlpha(gold, 0.5)); }
        return cv;
    }

    // ---------------- Motifs (16 × 16): o outline, a main, b shade, c highlight, d accent, e eyes / glow ----------------

    static readonly Dictionary<string, string[]> Sprites = new()
    {
        ["beast"] =
        [
            "................",
            "................",
            "..........o..o..",
            ".........oaooao.",
            ".........oaaaao.",
            "........oaaaeao.",
            "..o.....oaaaaaoo",
            ".oao...oaaaaacbo",
            ".oaao.oaaaaaaoo.",
            "..oaaoaaaaaaao..",
            "...oaccaaaaaao..",
            "...oaaaaaaabbo..",
            "...oaabooobaao..",
            "...oabo...oabo..",
            "...obo....obo...",
            "...oo.....oo....",
        ],
        ["stag"] =
        [
            ".......o...o....",
            "......odo.odo...",
            ".......odoodo...",
            "........odddo...",
            ".........oaao...",
            ".........oaeao..",
            "..o.....oaaaaoo.",
            ".oao...oaaaaacbo",
            "..oao.oaaaaaaoo.",
            "...oaoaaaaaaao..",
            "...oaccaaaaaao..",
            "...oaaaaaaabbo..",
            "...oaabooobaao..",
            "...oabo...oabo..",
            "...obo....obo...",
            "...oo.....oo....",
        ],
        ["bird"] =
        [
            "................",
            ".......ooo......",
            "......oaaeo.....",
            "......oaaaoddo..",
            ".......oaao.....",
            "..oo...oaao.....",
            ".oaaoooaaaao....",
            "oaabbaaaaaaao...",
            ".oaabbaacaaaao..",
            "..ooabbaaaaaao..",
            "....ooaaaaaao...",
            "......oooaoo....",
            ".........o......",
            ".........o......",
            "........ooo.....",
            "................",
        ],
        ["serpent"] =
        [
            "................",
            "..........ooo...",
            ".........oaaao..",
            "........oaeaao..",
            "........oaaaod..",
            ".........oao....",
            "....ooo..oao....",
            "...oaaao.oao....",
            "..oaabbaooao....",
            "..oao.oaaaao....",
            "..oao..oooo.....",
            "..oaao......oo..",
            "...oaacoooooao..",
            "....oaaaaaaaao..",
            ".....oooooooo...",
            "................",
        ],
        ["golem"] =
        [
            "................",
            ".....oooooo.....",
            "....oaaaaaao....",
            "....oaeaaeao....",
            "....oaaaaaao....",
            "..ooobbbbbbooo..",
            ".oaaoaaaaaaoaao.",
            ".oaaoaaccaaoaao.",
            ".oaaoaaccaaoaao.",
            ".obboaaaaaaobbo.",
            "..oooaaaaaaooo..",
            "....oaaoaaao....",
            "....oaao.oaao...",
            "...oaaao.oaaao..",
            "...ooooo.ooooo..",
            "................",
        ],
        ["wisp"] =
        [
            "................",
            ".......d........",
            "...d...c........",
            "......ooo....d..",
            "....ooacaoo.....",
            "...oaaccaaao....",
            "...oaccccaao.d..",
            "..oaaeaaeaaao...",
            "..oaaaaaaaaao...",
            "..oaaaoooaaao...",
            "...oaaaaaaao....",
            "...oabaaabao....",
            "....oaoaoaoo....",
            ".....o.o.o......",
            "..d.........d...",
            "................",
        ],
        ["insect"] =
        [
            "................",
            "....o......o....",
            ".....o....o.....",
            "......oooo......",
            ".....oaeeao.....",
            "....oooooooo....",
            "...oaacoocaao...",
            "..ooaaaooaaaoo..",
            ".o.oaaaooaaao.o.",
            "...oabaooabao...",
            "..ooaaaooaaaoo..",
            ".o.oaabooaaao.o.",
            "...oaaaooaaao...",
            "....oaaooaao....",
            ".....oooooo.....",
            "................",
        ],
        ["knight"] =
        [
            "................",
            "......oooo......",
            ".....oaaaao.....",
            "....oaacaaao....",
            "....oaoooooo....",
            "....oaeoeeoo....",
            "....oaaaaaao....",
            "..oooaaaaaaooo..",
            ".oddoaaccaaoaao.",
            ".oddoaaccaaoaao.",
            ".oddoaaaaaaoaao.",
            "..oo.oaaaaaoao..",
            ".....oaaoaao.o..",
            ".....oaao.oao...",
            "....ooaao.oaoo..",
            "....oooo...ooo..",
        ],
        ["hood"] =
        [
            "......oooo......",
            "....ooaaaaoo....",
            "...oaaaaaaaao...",
            "..oaaaoooooaao..",
            "..oaaoooooooao..",
            "..oaaooeooeoao..",
            "..oaaoooooooao..",
            "..oaaaoooooaao..",
            "...oaaaooaaao...",
            "..oaaaacaaaaao..",
            ".oaaabaaaabaaao.",
            ".oaaabaddabaaao.",
            "oaaaabaddabaaaao",
            "oaaaabaaaabaaaao",
            "oooooooooooooooo",
            "................",
        ],
        ["dragon"] =
        [
            "................",
            ".o.........ooo..",
            "oao.......oaaeo.",
            "oaao.....oaaaaoo",
            ".oaao...oaaaoddo",
            ".oaaaooaaaao....",
            "..oaaaaaaao.....",
            ".oaaaabbaaao....",
            "oaaobbcbbaaao...",
            ".oo.obbbbaaaao..",
            "....oaaaaaoaao..",
            "....oaooaoo.oao.",
            "...oao.oao...oo.",
            "...oo..oo.......",
            "................",
            "................",
        ],
        ["crab"] =
        [
            "................",
            "..oo........oo..",
            ".oaao......oaao.",
            ".oaoao....oaoao.",
            "..oaao....oaao..",
            "...oao.ee.oao...",
            "....oaoooooao...",
            "...oaaaaaaaaao..",
            "..oaaccaaaaaaao.",
            "..oaaaaaaaaaaao.",
            "...oabaaaaaabo..",
            "..o.oooooooo.o..",
            ".o..o.o..o.o..o.",
            "....o.o..o.o....",
            "................",
            "................",
        ],
        ["tree"] =
        [
            "......oooo......",
            "....ooaaaaoo....",
            "...oaaaccaaao...",
            "..oaaaaaaaaaao..",
            "..oaacaaaaacao..",
            "..oabaaaaaaabo..",
            "...oaaaaaaaao...",
            "....oooddooo....",
            "......oddo......",
            ".....oedeeo.....",
            "......oddo......",
            "......oddo......",
            ".....oddddo.....",
            "....oddooddo....",
            "...oo......oo...",
            "................",
        ],
        ["moth"] =
        [
            "................",
            "...o........o...",
            "....o......o....",
            ".ooo.o....o.ooo.",
            "oaaaoo.oo.ooaaao",
            "oacaaooeeooaacao",
            "oaaaaaoddoaaaaao",
            ".oaaaaoddoaaaao.",
            "..oaabodddobaao.",
            ".oaaaaoddoaaaao.",
            "oaacaaoddoaacaao",
            "oaaaao.oo.oaaaao",
            ".oooo......oooo.",
            "................",
            "................",
            "................",
        ],
        ["bolt"] =
        [
            "................",
            ".........oooo...",
            "........oaco....",
            ".......oaco.....",
            "......oaao......",
            ".....oaaoooo....",
            "....oaaaaaao....",
            "....ooooaco.....",
            "......oaao......",
            ".....oaco.......",
            "....oaao........",
            "...oaao.........",
            "...oao..........",
            "...oo...........",
            "................",
            "................",
        ],
        ["flame"] =
        [
            ".......o........",
            "......oao...o...",
            ".....oaao..oao..",
            "..o..oaco.oaao..",
            ".oao.oacaoaaao..",
            ".oaaooacccaaao..",
            "..oaacccdccaao..",
            "..oaaccdddccao..",
            "..oaccdddddcao..",
            "..oaccddeddcao..",
            "...oacdddddco...",
            "...oaccdddcao...",
            "....oaacccao....",
            ".....ooaaoo.....",
            ".......oo.......",
            "................",
        ],
        ["meteor"] =
        [
            "oo..............",
            ".oo....oo.......",
            "..ooo.oaao......",
            "...ooocaao......",
            ".....occco..oo..",
            "......ooo..oaao.",
            "..oo.......ocao.",
            ".oaao...oo..oo..",
            ".ocao..oaao.....",
            "..oo...occo.....",
            "........oo......",
            "..........ooo...",
            ".........oaaao..",
            ".........oacao..",
            "..........ooo...",
            "................",
        ],
        ["heart"] =
        [
            "................",
            "................",
            "..oooo...oooo...",
            ".oaacao.oaaaao..",
            "oaacccaoaaaaaao.",
            "oaacaaaaaaaaaao.",
            "oaaaaaaaaaaaaao.",
            "oaaaaaaaaaaaabo.",
            ".oaaaaaaaaaabo..",
            "..oaaaaaaaabo...",
            "...oaaaaaabo....",
            "....oaaaabo.....",
            ".....oaabo......",
            "......obo.......",
            ".......o........",
            "................",
        ],
        ["drop"] =
        [
            ".......oo.......",
            "......oaao......",
            "......oaao......",
            ".....oaaaao.....",
            ".....oacaao.....",
            "....oaccaaao....",
            "....oacaaaao....",
            "...oaaaaaaaao...",
            "...oaaaaaaaao...",
            "...oaaaaaaabo...",
            "...oaaaaaaabo...",
            "....oaaaaabo....",
            ".....obbbbo.....",
            "......oooo......",
            "..e.........e...",
            "................",
        ],
        ["cards"] =
        [
            "................",
            "......oooooooo..",
            ".....oaaaaaaaao.",
            "...ooooooooooao.",
            "..ocaaaaaaaaoao.",
            "..ocaaooaaaaoao.",
            "..ocaoeeoaaaoao.",
            "..ocaoeeoaaaoao.",
            "..ocaaooaaaaoao.",
            "..ocaaaaaaaaoo..",
            "..ocaaaaaaaao...",
            "..obbbbbbbbbo...",
            "..ocaaaaaaaao...",
            "..oooooooooo....",
            "................",
            "................",
        ],
        ["shield"] =
        [
            "................",
            "...oooooooooo...",
            "..oaaaaccaaaao..",
            "..oaaaaccaaaao..",
            "..occcccccccco..",
            "..occcccccccco..",
            "..oaaaaccaaaao..",
            "..oaaaaccaaaao..",
            "...oaaaccaaao...",
            "...obaaccaabo...",
            "....obaccabo....",
            ".....obccbo.....",
            "......obbo......",
            ".......oo.......",
            "................",
            "................",
        ],
        ["horn"] =
        [
            "................",
            "................",
            "............oo..",
            "..........ooao..",
            "........ooaaao..",
            "..oo..ooaaacao..",
            ".oddooaaaaacao..",
            ".oddaaaaaaacao..",
            ".oddaaaaaaacao..",
            ".oddoobbaaacao..",
            "..oo..oobbacao..",
            "........oobbao..",
            "..........ooao..",
            "............oo..",
            "................",
            "................",
        ],
    };
}
