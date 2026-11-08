using System.Reflection;
using System.Runtime.CompilerServices;

namespace BuilderEngine.Script.Samples;

/// <summary>
/// "Hollow Lantern" — a small top-down mining survival game made with Builder Script: you wake in a dark cavern with a
/// lantern, dig through dirt and stone, gather ore, craft better picks, torches and food at a workbench, keep fed,
/// fight shadow creatures that hunt you in the dark and beat the Murk Warden at the heart of the cave.
/// The game itself is the mechanisms in Samples/HollowLantern/*.bs; this file only writes the project folder
/// (project.json, the mechanisms, generated sounds and pictures).
/// </summary>
public static class HollowLanternSample
{
    public const string Name = "Hollow Lantern";
    public const string SceneName = "Cavern";

    [ModuleInitializer]
    internal static void Register() => SampleGames.All[Name] = Make;

    /// <summary>The game's mechanisms: name → Builder Script source (embedded Samples/HollowLantern/*.bs).</summary>
    public static Dictionary<string, string> Mechanisms()
    {
        var asm = Assembly.GetExecutingAssembly();
        var list = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var res in asm.GetManifestResourceNames())
        {
            string norm = res.Replace('\\', '.').Replace('/', '.');
            const string prefix = "Samples.HollowLantern.";
            if (!norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !norm.EndsWith(".bs")) continue;
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            list[norm[prefix.Length..^3]] = r.ReadToEnd();
        }
        return list;
    }

    /// <summary>The project (one scene, the mechanisms); <see cref="Make"/> also writes its sounds and pictures.</summary>
    public static Project Build()
    {
        var p = new Project { Name = Name };
        foreach (var (name, src) in Mechanisms()) p.Mechanisms[name] = src;
        var s = new Scene { Name = SceneName, Width = 2560, Height = 2560, Background = "#050404" };
        static SceneMechanism M(string name, params (string Key, object? Value)[] ps)
        {
            var m = new SceneMechanism { Name = name };
            foreach (var (k, v) in ps) m.Params[k] = v;
            return m;
        }
        SceneObject O(string name, double x, double y, double w, double h, string color, string image, string[] tags, params SceneMechanism[] mechs) =>
            s.Add(new SceneObject { Name = name, X = x, Y = y, Width = w, Height = h, Color = color, Image = image, Tags = [.. tags], Mechanisms = [.. mechs] });

        // Order matters a little: the cave is dug first (its 'when start' makes the map), then everything else starts on it.
        O("Hollow", 8, 8, 24, 24, "#6E625A", "", [], M("HollowGen"));
        O("Game", 40, 8, 24, 24, "#E8B04B", "", [], M("HollowGame"));
        O("Spawner", 72, 8, 24, 24, "#E05D5D", "", [], M("Lurkers"));
        O("Wick", 365, 421, 22, 22, "#E8B04B", "wick.png", ["player"], M("Wick"), M("Pickaxe"), M("TorchBag"));
        O("Workbench", 400, 360, 48, 32, "#7A5230", "workbench.png", ["workbench"], M("Workbench"));
        O("Torch", 308, 372, 16, 20, "#FFB060", "torch.png", ["torch"], M("Torch"));
        O("Warden", 1248, 1248, 64, 64, "#9B6BFF", "warden.png", ["boss", "enemy"], M("Warden"));
        p.Scenes.Add(s);
        return p;
    }

    public static void Make(string folder)
    {
        Build().Save(folder);
        string sounds = Sounds.Folder(folder), images = Images.Folder(folder);
        Directory.CreateDirectory(sounds);
        Directory.CreateDirectory(images);
        Sounds.WriteDefaults(folder);
        foreach (var (file, wav) in HollowLanternSounds.All()) File.WriteAllBytes(Path.Combine(sounds, file), wav);
        foreach (var (file, png) in HollowLanternArt.All()) File.WriteAllBytes(Path.Combine(images, file), png);
    }
}

/// <summary>The sample's sound effects and its cave hum, made from sine waves and noise (fixed seeds).</summary>
static class HollowLanternSounds
{
    const int Rate = SoundSynth.Rate;

    public static IEnumerable<(string File, byte[] Wav)> All()
    {
        yield return ("dig.wav", Make(0.12, 0.5, 1, (t, n) => n * Math.Exp(-t / 0.03) * 0.8 + Math.Sin(2 * Math.PI * 140 * t) * Math.Exp(-t / 0.04)));
        yield return ("break.wav", Make(0.3, 0.75, 2, (t, n) => n * Math.Exp(-t / 0.08) + Math.Sin(2 * Math.PI * (120 - 160 * t) * t) * Math.Exp(-t / 0.1)));
        yield return ("swing.wav", Make(0.18, 0.45, 3, (t, n) => n * Math.Sin(Math.PI * t / 0.18) * (0.4 + t * 3)));
        yield return ("hurt.wav", Make(0.3, 0.7, 4, (t, n) => Math.Sign(Math.Sin(2 * Math.PI * (300 - 600 * t) * t)) * Math.Exp(-t / 0.12) * 0.7 + n * 0.3 * Math.Exp(-t / 0.05)));
        yield return ("squish.wav", Make(0.22, 0.6, 5, (t, n) => Math.Sin(2 * Math.PI * (90 + 200 * Math.Exp(-t / 0.03)) * t) * Math.Exp(-t / 0.08) + n * 0.25 * Math.Exp(-t / 0.04)));
        yield return ("pickup.wav", Make(0.2, 0.5, 6, (t, _) => Math.Sin(2 * Math.PI * (t < 0.07 ? 880 : 1320) * t) * Math.Exp(-(t % 0.07) / 0.08)));
        yield return ("craft.wav", Make(0.45, 0.6, 7, (t, n) => (Math.Sin(2 * Math.PI * 660 * t) + 0.5 * Math.Sin(2 * Math.PI * 1320 * t)) * Math.Exp(-(t % 0.15) / 0.05) + n * 0.1 * Math.Exp(-(t % 0.15) / 0.01)));
        yield return ("eat.wav", Make(0.35, 0.5, 8, (t, n) => n * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 14 * t)) * Math.Exp(-t / 0.2)));
        yield return ("torch.wav", Make(0.4, 0.45, 9, (t, n) => n * (1 - Math.Exp(-t / 0.02)) * Math.Exp(-t / 0.15)));
        yield return ("roar.wav", Make(1.3, 0.85, 10, (t, n) => (Math.Sin(2 * Math.PI * (70 + 15 * Math.Sin(t * 9)) * t) + 0.6 * n) * Math.Min(1, t * 6) * Math.Exp(-t / 0.7)));
        yield return ("slam.wav", Make(0.5, 0.9, 11, (t, n) => Math.Sin(2 * Math.PI * (60 - 30 * t) * t) * Math.Exp(-t / 0.18) + n * Math.Exp(-t / 0.06)));
        yield return ("rumble.wav", Make(0.6, 0.5, 12, (t, n) => n * 0.6 * Math.Sin(Math.PI * t / 0.6) + Math.Sin(2 * Math.PI * 45 * t) * 0.5 * Math.Sin(Math.PI * t / 0.6)));
        yield return ("charge.wav", Make(0.7, 0.55, 13, (t, _) => Math.Sin(2 * Math.PI * (200 + 700 * t * t) * t) * Math.Min(1, t * 4)));
        yield return ("shards.wav", Make(0.35, 0.6, 14, (t, n) => (Math.Sin(2 * Math.PI * 1800 * t) * 0.4 + n) * Math.Exp(-t / 0.1)));
        yield return ("boss_hit.wav", Make(0.2, 0.7, 15, (t, n) => Math.Sin(2 * Math.PI * 110 * t) * Math.Exp(-t / 0.06) + n * 0.5 * Math.Exp(-t / 0.03)));
        yield return ("boss_die.wav", Make(2.0, 0.85, 16, (t, n) => (Math.Sin(2 * Math.PI * (90 - 30 * t) * t) + n * 0.7) * Math.Exp(-t / 0.9)));
        yield return ("win.wav", Make(1.6, 0.6, 17, (t, _) =>
        {
            double[] notes = [392, 494, 587, 784];
            int k = Math.Min(3, (int)(t / 0.22));
            double local = t - k * 0.22;
            return (Math.Sin(2 * Math.PI * notes[k] * t) + 0.3 * Math.Sin(4 * Math.PI * notes[k] * t)) * Math.Exp(-local / (k == 3 ? 0.6 : 0.25));
        }));
        yield return ("cave_drone.wav", Drone());
    }

    /// <summary>A sound of <paramref name="seconds"/> from f(t, noise), normalized to <paramref name="peak"/> with short fades.</summary>
    static byte[] Make(double seconds, double peak, int seed, Func<double, double, double> f)
    {
        var rng = new Random(seed);
        int n = (int)(Rate * seconds);
        var s = new double[n];
        double lo = 0;
        for (int i = 0; i < n; i++)
        {
            lo += 0.4 * ((rng.NextDouble() * 2 - 1) - lo); // softened noise
            s[i] = f((double)i / Rate, lo);
        }
        return Pcm(s, peak, Rate * 5 / 1000);
    }

    /// <summary>Six seconds of low cave hum that loops without a click (whole cycles of every partial).</summary>
    static byte[] Drone()
    {
        const double len = 6;
        int n = (int)(Rate * len);
        var s = new double[n];
        var rng = new Random(99);
        double lo = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            lo += 0.02 * ((rng.NextDouble() * 2 - 1) - lo);
            double swell = 0.75 + 0.25 * Math.Sin(2 * Math.PI * t / len);
            s[i] = swell * (Math.Sin(2 * Math.PI * 55 * t) * 0.5 + Math.Sin(2 * Math.PI * 82.5 * t) * 0.25 + Math.Sin(2 * Math.PI * 110.5 * t) * 0.12) + lo * 2.5;
        }
        return Pcm(s, 0.5, 0);
    }

    static byte[] Pcm(double[] s, double peak, int fade)
    {
        double max = 1e-9;
        foreach (var v in s) max = Math.Max(max, Math.Abs(v));
        var pcm = new short[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            double g = 1;
            if (fade > 0 && i < fade) g = (double)i / fade;
            else if (fade > 0 && i > s.Length - fade) g = (double)(s.Length - i) / fade;
            pcm[i] = (short)Math.Clamp(s[i] / max * peak * g * short.MaxValue, short.MinValue, short.MaxValue);
        }
        return SoundSynth.Wav(pcm);
    }
}
