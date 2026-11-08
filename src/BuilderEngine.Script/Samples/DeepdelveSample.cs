using System.Reflection;
using System.Runtime.CompilerServices;

namespace BuilderEngine.Script.Samples;

/// <summary>
/// "Deepdelve": a small side-view sandbox written in Builder Script (Samples/Deepdelve/*.bs) — a generated world of
/// tiles, digging and building, crafting with a workbench, pick tiers, day and night with torches, Gloops by day and
/// Shamblers at night, and a Gem Crown to forge. This file only writes the project folder: the scene, the mechanisms,
/// generated pixel-art pictures and sounds.
/// </summary>
public static class DeepdelveSample
{
    public const string Name = "Deepdelve";
    const string Resource = "Samples.Deepdelve";

    [ModuleInitializer]
    internal static void Register() => SampleGames.All[Name] = Build;

    /// <summary>The game's mechanisms: name → Builder Script (embedded Samples/Deepdelve/*.bs).</summary>
    public static Dictionary<string, string> Mechanisms()
    {
        var asm = Assembly.GetExecutingAssembly();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var res in asm.GetManifestResourceNames())
        {
            string n = res.Replace('\\', '.').Replace('/', '.');
            if (!n.StartsWith(Resource + ".", StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".bs", StringComparison.OrdinalIgnoreCase)) continue;
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            result[n[(Resource.Length + 1)..^3]] = r.ReadToEnd().Replace("\r\n", "\n");
        }
        return result;
    }

    /// <summary>The project (scene + mechanisms), without its files.</summary>
    public static Project CreateProject()
    {
        var p = new Project { Name = Name, Scenes = [MakeScene()] };
        foreach (var (name, src) in Mechanisms()) p.Mechanisms[name] = src;
        return p;
    }

    static void Build(string folder)
    {
        CreateProject().Save(folder);
        Sounds.WriteDefaults(folder);
        WriteSounds(folder);
        WritePictures(folder);
    }

    // ---------------- The scene ----------------

    static SceneObject Obj(string name, double x, double y, double w, double h, string color, string mech,
        Dictionary<string, object?>? ps = null, string[]? tags = null, string image = "") => new()
    {
        Name = name, X = x, Y = y, Width = w, Height = h, Color = color, Image = image, Tags = [.. tags ?? []],
        Mechanisms = mech.Length == 0 ? [] : [new SceneMechanism { Name = mech, Params = new(ps ?? [], StringComparer.OrdinalIgnoreCase) }],
    };

    static Scene MakeScene()
    {
        var s = new Scene { Id = 1, Name = "Deepdelve", Width = 2880, Height = 1536, Background = "#86C2E2" };
        s.Add(Obj("World", 8, 8, 24, 24, "#5E9E3A", "DeepWorld"));
        var player = Obj("Player", 1442, 380, 12, 26, "#3E7CC9", "Miner", tags: ["player"], image: "miner_idle1.png");
        player.Mechanisms.Add(new SceneMechanism
        {
            Name = "Animator",
            Params = new(StringComparer.OrdinalIgnoreCase) { ["idle"] = "miner_idle", ["walk"] = "miner_walk", ["jump"] = "miner_jump", ["fps"] = 9.0 },
        });
        s.Add(player);
        s.Add(Obj("Sky", 40, 8, 24, 24, "#F2C46B", "SkyCycle"));
        s.Add(Obj("Spawner", 72, 8, 24, 24, "#4FC9A4", "CreatureSpawner"));
        s.Add(Obj("Reticle", 1480, 380, 16, 16, "#FFFFFF", "Reticle", image: "reticle.png"));
        s.Add(Obj("HUD", 104, 8, 24, 24, "#F4E9DA", "DeepHud"));
        s.Add(Obj("Hotbar", 136, 8, 24, 24, "#E8913A", "Hotbar", new() { ["slots"] = 10.0 }));
        s.Add(Obj("Workshop", 110, 84, 580, 236, "#1A130E", "Workshop", tags: ["ui"]));
        s.Add(Obj("Workshop Button", 16, 406, 140, 34, "#E8913A", "Button", new()
        {
            ["text"] = "C · Workshop", ["message"] = "toggle workshop", ["target"] = "Workshop", ["color"] = "#2A1F17",
            ["hover_color"] = "#3D2C20", ["text_color"] = "#F2C46B", ["text_size"] = 14.0,
        }, ["ui"]));
        // Dark backs behind the HUD texts (the screen "HUD" shows when the game begins).
        s.Add(Obj("HUD Back", 8, 6, 486, 66, "#A6100B08", "Panel", new() { ["color"] = "#A6100B08", ["screen"] = "HUD" }, ["ui"]));
        s.Add(Obj("Clock Back", 630, 5, 162, 46, "#A6100B08", "Panel", new() { ["color"] = "#A6100B08", ["screen"] = "HUD" }, ["ui"]));
        s.Add(Obj("Game", 168, 8, 24, 24, "#D97757", "GameFlow"));

        // Title screen (shown at start; Start or Enter begins).
        s.Add(Obj("Title Back", 0, 0, 800, 450, "#EE100B08", "Panel", new() { ["color"] = "#EE100B08", ["screen"] = "Title" }, ["ui"]));
        s.Add(Note("Title Logo", 0, 58, 800, 70, "DEEPDELVE", 54, "#E8913A", "Title"));
        s.Add(Note("Title Line", 0, 126, 800, 30, "Dig deep. Build. Survive the nights. Forge the Gem Crown.", 16, "#F4E9DA", "Title"));
        s.Add(Note("Title Keys", 180, 170, 440, 118,
            "A / D  walk        Space / W  jump\nHold left mouse  dig a block or hit a creature\nRight mouse  place the selected block\n1-9 or the wheel  pick a slot        C  workshop",
            14, "#CDBFAE", "Title", "#1E1611"));
        s.Add(Obj("Title Miner", 388, 298, 24, 52, "#00000000", "Note", new() { ["screen"] = "Title" }, ["ui"], "miner_idle1.png"));
        s.Add(Obj("Start Button", 310, 362, 180, 44, "#E8913A", "Button", new()
        {
            ["text"] = "Start digging", ["message"] = "begin", ["screen"] = "Title", ["color"] = "#E8913A",
            ["hover_color"] = "#FFB060", ["text_color"] = "#1A120B", ["text_size"] = 18.0,
        }, ["ui"]));

        // Win screen (shown when the Gem Crown is crafted).
        s.Add(Obj("Win Back", 150, 96, 500, 262, "#F41A130E", "Panel", new() { ["color"] = "#F41A130E", ["screen"] = "Win" }, ["ui"]));
        s.Add(Obj("Win Crown", 370, 112, 60, 60, "#00000000", "Note", new() { ["screen"] = "Win" }, ["ui"], "crown.png"));
        s.Add(Note("Win Title", 150, 180, 500, 44, "THE GEM CROWN IS YOURS!", 28, "#7FE8DA", "Win"));
        s.Add(Note("Win Stats", 150, 226, 500, 30, "", 16, "#F4E9DA", "Win"));
        s.Add(Note("Win Thanks", 150, 256, 500, 26, "Thanks for playing Deepdelve.", 13, "#CDBFAE", "Win"));
        s.Add(Obj("Keep Button", 310, 298, 180, 40, "#E8913A", "Button", new()
        {
            ["text"] = "Keep exploring", ["message"] = "keep playing", ["screen"] = "Win", ["color"] = "#E8913A",
            ["hover_color"] = "#FFB060", ["text_color"] = "#1A120B", ["text_size"] = 16.0,
        }, ["ui"]));
        return s;
    }

    static SceneObject Note(string name, double x, double y, double w, double h, string text, double size, string color, string screen, string back = "#00000000") =>
        Obj(name, x, y, w, h, back, "Note", new() { ["text"] = text, ["size"] = size, ["text_color"] = color, ["color"] = back, ["screen"] = screen }, ["ui"]);

    // ---------------- Pictures (generated pixel art) ----------------

    static void WritePictures(string folder)
    {
        string dir = Images.Folder(folder);
        void Save(string file, byte[] png) => PixelArt.Save(Path.Combine(dir, file), png);
        foreach (var (file, w, h, px) in Tiles()) Save(file, PixelArt.Scaled(w, h, px, 2));
        foreach (var (file, rows, pal) in Sprites()) Save(file, PixelArt.FromText(rows, pal, 2));
    }

    static uint C(string hex) => PixelArt.Color(hex);

    /// <summary>8 × 8 tile textures, made from a few colours with a fixed seed each.</summary>
    static IEnumerable<(string File, int W, int H, uint[] Px)> Tiles()
    {
        const int N = 8;
        uint[] Speckle(int seed, string baseHex, double dark, double light, double spread = 0.12)
        {
            var rng = new Random(seed);
            uint b = C(baseHex);
            var px = new uint[N * N];
            for (int i = 0; i < px.Length; i++)
            {
                double r = rng.NextDouble();
                px[i] = r < dark ? PixelArt.Shade(b, -spread - rng.NextDouble() * 0.08)
                      : r > 1 - light ? PixelArt.Shade(b, spread * 0.8 + rng.NextDouble() * 0.06) : b;
            }
            return px;
        }
        uint[] Ore(int seed, string stoneHex, string oreHex, int blobs)
        {
            var px = Speckle(seed, stoneHex, 0.25, 0.15);
            var rng = new Random(seed * 7 + 1);
            uint ore = C(oreHex), hi = PixelArt.Shade(ore, 0.35), lo = PixelArt.Shade(ore, -0.3);
            for (int k = 0; k < blobs; k++)
            {
                int x = 1 + rng.Next(N - 3), y = 1 + rng.Next(N - 3);
                px[y * N + x] = hi; px[y * N + x + 1] = ore; px[(y + 1) * N + x] = ore; px[(y + 1) * N + x + 1] = lo;
            }
            return px;
        }
        uint[] Rows(params string[] rows) => PixelArt.Pixels(rows, Palette).Argb;

        yield return ("soil.png", N, N, Speckle(11, "#7A5232", 0.3, 0.18));
        var turf = Speckle(12, "#7A5232", 0.3, 0.18);
        var rngT = new Random(5);
        for (int x = 0; x < N; x++)
        {
            int depth = 2 + rngT.Next(2);
            for (int y = 0; y < depth; y++) turf[y * N + x] = y == 0 ? C("#7CC24E") : rngT.NextDouble() < 0.5 ? C("#5E9E3A") : C("#4E8A30");
        }
        yield return ("turf.png", N, N, turf);
        yield return ("slate.png", N, N, Speckle(13, "#5C6170", 0.28, 0.16));
        yield return ("basalt.png", N, N, Speckle(14, "#34303E", 0.3, 0.2));
        yield return ("deepstone.png", N, N, Speckle(15, "#141219", 0.3, 0.1, 0.25));
        yield return ("coal.png", N, N, Ore(16, "#5C6170", "#1C1B20", 3));
        yield return ("copper.png", N, N, Ore(17, "#5C6170", "#D9803F", 3));
        yield return ("iron.png", N, N, Ore(18, "#5C6170", "#D8CBB8", 3));
        yield return ("gem.png", N, N, Ore(19, "#34303E", "#59E0D0", 3));
        yield return ("soilwall.png", N, N, Speckle(20, "#3A2718", 0.25, 0.1, 0.1));
        yield return ("slatewall.png", N, N, Speckle(21, "#262830", 0.25, 0.1, 0.1));
        yield return ("trunk.png", N, N, Rows(
            ".BbbbBb.", ".BbBbbb.", ".bbbBbB.", ".Bbbbbb.", ".bBbbBb.", ".bbBbbb.", ".Bbbbbb.", ".bbbBbb."));
        var leaves = Speckle(22, "#3F7F3A", 0.3, 0.25, 0.15);
        var rngL = new Random(23);
        for (int i = 0; i < leaves.Length; i++) if (rngL.NextDouble() < 0.08) leaves[i] = 0;
        yield return ("leaves.png", N, N, leaves);
        yield return ("planks.png", N, N, Rows(
            "PPPPPPPP", "pPPPPpPP", "PPPPPPPP", "qqqqqqqq", "PPPpPPPP", "PPPPPPpP", "PPPPPPPP", "qqqqqqqq"));
        yield return ("platform.png", N, N, Rows(
            "PPPPPPPP", "pPPpPPpP", "qqqqqqqq", ".q....q.", ".q....q.", "........", "........", "........"));
        yield return ("torch.png", N, N, Rows(
            "...f....", "..fFf...", "..FyF...", "...F....", "...t....", "...t....", "...t....", "...t...."));
        yield return ("workbench.png", N, N, Rows(
            "........", "PPPPPPPP", "qqqqqqqq", ".t.mm.t.", ".t.mm.t.", ".tqqqqt.", ".t....t.", ".t....t."));
    }

    static readonly Dictionary<char, string> Palette = new()
    {
        ['B'] = "#5E3E22", ['b'] = "#7B5230", ['P'] = "#B8844C", ['p'] = "#9C6B3E", ['q'] = "#6E4826",
        ['f'] = "#FF7A2A", ['F'] = "#FFB547", ['y'] = "#FFF0A0", ['t'] = "#6E4826", ['m'] = "#8E9AA8",
    };

    /// <summary>Characters, creatures, effects and item icons as text art.</summary>
    static IEnumerable<(string File, string[] Rows, Dictionary<char, string> Pal)> Sprites()
    {
        var miner = new Dictionary<char, string>
        {
            ['o'] = "#E8913A", ['O'] = "#B5651D", ['l'] = "#FFE27A", ['s'] = "#E8B98A", ['k'] = "#2A1E16",
            ['b'] = "#3E7CC9", ['B'] = "#2C5A96", ['n'] = "#4A3A2E", ['K'] = "#1E1712",
        };
        string[] Miner(params string[] legsAndArms) => [".oool.", "OOOOOO", ".ssss.", ".sssk.", ".ssss.", .. legsAndArms];
        yield return ("miner_idle1.png", Miner(".bbbb.", "bbbbbb", "sbBBbs", ".bbbb.", ".nnnn.", ".n..n.", ".n..n.", "KK..KK"), miner);
        yield return ("miner_walk1.png", Miner(".bbbb.", "bbbbbb", "sbBBbs", ".bbbb.", ".nnnn.", ".n..n.", "n....n", "K....K"), miner);
        yield return ("miner_walk2.png", Miner(".bbbb.", "bbbbbb", ".bBBbs", ".bbbb.", ".nnnn.", "..nn..", "..nn..", "..KK.."), miner);
        yield return ("miner_jump1.png", Miner("sbbbbs", "bbbbbb", ".bBBb.", ".bbbb.", ".nnnn.", ".n..n.", "n....n", "K....K"), miner);

        var gloop = new Dictionary<char, string> { ['g'] = "#4FC9A4", ['l'] = "#A6F0D8", ['d'] = "#2E8C73", ['w'] = "#FFFFFF", ['k'] = "#16302A" };
        yield return ("gloop.png", ["...ggg...", "..gllgg..", ".gllgggg.", "ggwkggwkg", "ggggggggg", "gdgggggdg", ".ddddddd."], gloop);
        yield return ("gloop_hop.png", ["...ggg...", "..glggg..", "..lgggg..", ".gwkgwkg.", ".ggggggg.", ".ggggggg.", "..ddddd.."], gloop);

        var sham = new Dictionary<char, string>
        {
            ['z'] = "#8FA27A", ['Z'] = "#5E6E50", ['r'] = "#FF5040", ['p'] = "#6A4A8C", ['P'] = "#4E3468", ['d'] = "#3E3A48", ['k'] = "#1E1A22",
        };
        yield return ("shambler1.png", [".......", "..zzz..", ".zzzzz.", ".zZzrz.", "..zzz..", ".pppp..", "zpPppzz", ".pPpp..", ".pppp..", ".dddd..", ".d..d..", ".d..d..", "kk..kk."], sham);
        yield return ("shambler2.png", [".......", "..zzz..", ".zzzzz.", ".zZzrz.", "..zzz..", ".ppppzz", ".pPpp..", ".pPppz.", ".pppp..", ".dddd..", "..dd...", "..dd...", "..kk..."], sham);

        var fx = new Dictionary<char, string> { ['w'] = "#FFFFFF", ['W'] = "#BFE8FF", ['g'] = "#C8BBA8", ['G'] = "#8E8274" };
        yield return ("slash.png", ["......ww..", "........w.", ".........w", ".........w", "........Ww", ".......Ww.", ".....WWw..", "..WWWw....", "WWw.......", ".........."], fx);
        yield return ("poof.png", ["....gg......", "..ggggg..g..", ".gGggggggg..", ".ggggGgggg..", "ggggggggGgg.", "gGgg..gggggg", ".ggg...gggg.", "..gggggGgg..", "...gggggg...", "..gg.ggg..g.", ".....g......", "............"], fx);
        yield return ("reticle.png", ["ww....ww", "w......w", "........", "........", "........", "........", "w......w", "ww....ww"], fx);

        var items = new Dictionary<char, string>
        {
            ['B'] = "#5E3E22", ['b'] = "#8A5A32", ['L'] = "#C89A62", ['h'] = "#6E4826",
            ['c'] = "#D9803F", ['C'] = "#9E5527", ['i'] = "#D8D2C8", ['I'] = "#9A948A", ['w'] = "#B8844C", ['W'] = "#8C6238",
            ['g'] = "#58C9A8", ['G'] = "#2E8C73", ['e'] = "#A6F0D8", ['r'] = "#E06A7A", ['R'] = "#A84455", ['j'] = "#E8DCCB",
            ['y'] = "#F2C94C", ['Y'] = "#B88A1E", ['m'] = "#59E0D0", ['M'] = "#C04A8E",
        };
        yield return ("wood.png", ["............", "............", "..LLbbbbbb..", ".LBLbbbbbbb.", ".LLLbBbbbbb.", ".LBLbbbbBbb.", ".LLLbbbbbbb.", "..LLbbbbbb..", "............", "............", "............", "............"], items);
        yield return ("goo.png", ["............", "............", ".....gg.....", "....gegg....", "...geggggg..", "..ggggggggg.", "..gggggggGg.", "...GGGGGGG..", "............", "............", "............", "............"], items);
        yield return ("salve.png", ["............", "....jjjj....", "....hhhh....", "...jrrrrj...", "..jrrrrrrj..", "..jrjjjrrj..", "..jrrjrrrj..", "..jrrrrrrj..", "..jRRRRRRj..", "...jjjjjj...", "............", "............"], items);
        string[] pick =
        [
            "............", "..aaaaaa....", ".aAAAAAAa...", ".a.....hAa..", "......h..Aa.", ".....h....a.", "....h.......", "...h........", "..h.........", ".h..........", "............", "............",
        ];
        string[] Tint(string[] rows, char a, char b) => rows.Select(r => r.Replace('a', a).Replace('A', b)).ToArray();
        yield return ("pick_wood.png", Tint(pick, 'w', 'W'), items);
        yield return ("pick_copper.png", Tint(pick, 'c', 'C'), items);
        yield return ("pick_iron.png", Tint(pick, 'i', 'I'), items);
        yield return ("sword_copper.png", ["..........cc", ".........cCc", "........cCc.", ".......cCc..", "......cCc...", ".....cCc....", "..h.cCc.....", "...hCc......", "...hh.......", "..h..h......", ".h..........", "............"], items);
        yield return ("crown.png", ["............", ".y...y...y..", ".yy.yyy.yy..", ".yyyyyyyyy..", ".ymyyMyymy..", ".yyyyyyyyy..", ".YYYYYYYYY..", "............", "............", "............", "............", "............"], items);
    }

    // ---------------- Sounds ----------------

    static void WriteSounds(string folder)
    {
        string dir = Sounds.Folder(folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "break.wav"), Noise(0.16, 7, 900, 0.05, 140));
        File.WriteAllBytes(Path.Combine(dir, "place.wav"), Noise(0.12, 8, 400, 0.03, 90));
        File.WriteAllBytes(Path.Combine(dir, "swing.wav"), Sweep(0.15, 900, 300, 0.2, noise: 0.8));
        File.WriteAllBytes(Path.Combine(dir, "hurt.wav"), Sweep(0.25, 420, 120, 0.12, noise: 0.25));
        File.WriteAllBytes(Path.Combine(dir, "craft.wav"), Notes(0.09, 523.25, 659.25, 783.99));
        File.WriteAllBytes(Path.Combine(dir, "win.wav"), Notes(0.16, 523.25, 659.25, 783.99, 1046.5, 783.99, 1046.5));
    }

    static double[] Buffer(double seconds) => new double[(int)(SoundSynth.Rate * seconds)];

    static byte[] Finish(double[] s, double peak)
    {
        double max = 1e-9;
        foreach (var v in s) max = Math.Max(max, Math.Abs(v));
        int fade = SoundSynth.Rate / 200;
        var pcm = new short[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            double g = i < fade ? (double)i / fade : i > s.Length - fade ? (double)(s.Length - i) / fade : 1;
            pcm[i] = (short)Math.Clamp(s[i] / max * peak * g * short.MaxValue, short.MinValue, short.MaxValue);
        }
        return SoundSynth.Wav(pcm);
    }

    /// <summary>A crunch: filtered noise that dies away, over a low thump.</summary>
    static byte[] Noise(double seconds, int seed, double brightness, double decay, double thump)
    {
        var s = Buffer(seconds);
        var rng = new Random(seed);
        double lo = 0, k = Math.Clamp(brightness / SoundSynth.Rate * 6, 0.02, 0.9);
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / SoundSynth.Rate;
            lo += k * ((rng.NextDouble() * 2 - 1) - lo);
            s[i] = (lo * 0.8 + Math.Sin(2 * Math.PI * thump * t) * 0.6) * Math.Exp(-t / decay);
        }
        return Finish(s, 0.5);
    }

    /// <summary>A pitch sliding from one frequency to another, with some noise in it.</summary>
    static byte[] Sweep(double seconds, double from, double to, double decay, double noise)
    {
        var s = Buffer(seconds);
        var rng = new Random(3);
        double phase = 0, lo = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / SoundSynth.Rate;
            phase += 2 * Math.PI * (from + (to - from) * t / seconds) / SoundSynth.Rate;
            lo += 0.25 * ((rng.NextDouble() * 2 - 1) - lo);
            s[i] = (Math.Sin(phase) * (1 - noise) + lo * noise * 2) * Math.Exp(-t / decay);
        }
        return Finish(s, 0.5);
    }

    /// <summary>A little tune: square-ish notes one after another.</summary>
    static byte[] Notes(double each, params double[] freqs)
    {
        var s = Buffer(each * freqs.Length + 0.15);
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / SoundSynth.Rate;
            int n = Math.Min(freqs.Length - 1, (int)(t / each));
            double local = t - n * each, f = freqs[n];
            double v = Math.Sin(2 * Math.PI * f * t) * 0.7 + Math.Sign(Math.Sin(2 * Math.PI * f * t)) * 0.3;
            s[i] = v * Math.Exp(-local / (n == freqs.Length - 1 ? 0.2 : 0.08));
        }
        return Finish(s, 0.55);
    }
}
