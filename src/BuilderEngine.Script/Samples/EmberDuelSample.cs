using System.Reflection;
using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// The Ember Duel sample: a small card battler written in Builder Script (Samples/EmberDuel/*.bs). This file only
/// writes the project folder: the mechanisms, one scene with every screen (title, deck builder, battle, result,
/// how to play), the pixel-art pictures (EmberDuelArt) and generated sounds.
/// </summary>
public static class EmberDuelSample
{
    public const string Name = "Ember Duel";

    [ModuleInitializer]
    internal static void Register() => SampleGames.All[Name] = Build;

    /// <summary>The sample's mechanisms: file name (= mechanism name) → Builder Script.</summary>
    public static IEnumerable<(string Name, string Source)> Mechanisms()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("Samples.EmberDuel", StringComparison.Ordinal)).Order())
        {
            string file = res["Samples.EmberDuel".Length..].TrimStart('.', '\\', '/');
            string name = file.EndsWith(".bs", StringComparison.Ordinal) ? file[..^3] : file;
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            yield return (name, r.ReadToEnd());
        }
    }

    static void Build(string folder)
    {
        var p = new Project { Name = Name };
        foreach (var (name, src) in Mechanisms()) p.Mechanisms[name] = src;
        p.Scenes.Add(MakeScene());
        p.Save(folder);
        EmberDuelArt.WriteAll(Images.Folder(folder));
        string sounds = Sounds.Folder(folder);
        Directory.CreateDirectory(sounds);
        foreach (var (file, make) in SoundsToWrite) File.WriteAllBytes(Path.Combine(sounds, file), make());
    }

    // ---------------- The scene ----------------

    static SceneObject Obj(Scene s, string name, double x, double y, double w, double h, string image = "", string color = "#333333", params (string Mech, (string, object?)[] Params)[] mechs)
    {
        var o = new SceneObject
        {
            Name = name, X = x, Y = y, Width = w, Height = h, Image = image, Color = color, Tags = ["ui"],
            Mechanisms = mechs.Select(m => new SceneMechanism
            {
                Name = m.Mech,
                Params = m.Params.ToDictionary(kv => kv.Item1, kv => kv.Item2, StringComparer.OrdinalIgnoreCase),
            }).ToList(),
        };
        return s.Add(o);
    }

    static (string, (string, object?)[]) M(string mech, params (string, object?)[] ps) => (mech, ps);

    static (string, (string, object?)[]) Button(string text, string message, string screen, string color, string hover, string textColor = "#141414", double size = 16) =>
        M("Button", ("text", text), ("message", message), ("screen", screen), ("color", color), ("hover_color", hover), ("text_color", textColor), ("text_size", size));

    static (string, (string, object?)[]) Panel(string screen, string title = "", double size = 18, string color = "#00000000", string titleColor = "#ECECEC", bool shown = false) =>
        M("Panel", ("screen", screen), ("title", title), ("title_size", size), ("color", color), ("title_color", titleColor), ("shown", shown));

    static (string, (string, object?)[]) Top(double z) => M("OnTop", ("z", z));

    const string Hint = "Point at a card\nto see it here";

    const string Rules =
        "HOW TO PLAY\n\n" +
        "You and Morrow each start with 20 health and a deck of 20 cards. Every turn your mana grows by one (up to 10) and you draw a card.\n\n" +
        "Drag a creature onto one of your lanes. Drag a spell onto its target: attacks onto an enemy, healing and shields onto a friend. Glowing cards are the ones you can play.\n\n" +
        "When you press End turn, each of your creatures attacks the creature across its lane, or Morrow when that lane is empty.\n\n" +
        "Point at any card to see it big. Bring Morrow to 0 health to win!";

    static Scene MakeScene()
    {
        var s = new Scene { Name = Name, Width = 800, Height = 450, Background = "#140C08" };
        // The data and the table (always there).
        Obj(s, "Book", 0, 0, 16, 16, mechs: M("CardBook"));
        Obj(s, "Table", 0, 0, 800, 450, "Table", "#2C1A10");
        Obj(s, "Flow", 0, 0, 16, 16, mechs: M("GameFlow"));

        // ---- Battle ----
        for (int i = 0; i < 4; i++)
            Obj(s, $"Rival Lane {i + 1}", 170 + i * 100, 40, 88, 116, "Lane Rival", "#000000", M("Lane", ("side", "rival"), ("index", (double)i)));
        for (int i = 0; i < 4; i++)
            Obj(s, $"Lane {i + 1}", 170 + i * 100, 164, 88, 116, "Lane", "#000000", M("Lane", ("side", "you"), ("index", (double)i)));
        Obj(s, "Rival", 30, 46, 100, 100, "Hero Rival", "#5A3A8C", M("Hero", ("title", "Morrow"), ("side", "rival")));
        Obj(s, "You", 30, 176, 100, 100, "Hero You", "#D9822B", M("Hero", ("title", "You"), ("side", "you")));
        Obj(s, "Your Deck", 716, 356, 54, 76, "Card Back", "#5A1A12", Panel("Battle", color: ""));
        Obj(s, "End Turn", 604, 250, 144, 40, mechs: Button("End turn", "end turn", "Battle", "#D9822B", "#F0A050", size: 17));
        Obj(s, "Menu", 604, 296, 144, 26, mechs: Button("Menu", "title", "Battle", "#4A3F36", "#6A5A4A", "#EBDDBF", 13));
        Obj(s, "Zoom Hint", 600, 24, 152, 212, mechs: Panel("Battle", Hint, 12, "#00000000", "#8A7A66"));
        Obj(s, "Duel", 0, 0, 16, 16, mechs: M("Duel"));

        // ---- Deck builder ----
        Obj(s, "Build Backdrop", 18, 8, 548, 428, color: "#000000", mechs: Panel("Build", color: "#B0100A06"));
        Obj(s, "Build Title", 34, 16, 516, 40, mechs: Panel("Build", "Build your deck: click a card to add it or take it out", 15, titleColor: "#F6E7C8"));
        Obj(s, "Build Hint", 600, 24, 152, 212, mechs: Panel("Build", Hint, 12, "#00000000", "#8A7A66"));
        Obj(s, "Deck Builder", 0, 0, 16, 16, mechs: M("DeckBuilder"));
        Obj(s, "Deck Count", 590, 252, 172, 30, mechs: Panel("Build", "", 15, titleColor: "#FFD54A"));
        Obj(s, "Auto Fill", 590, 290, 84, 34, mechs: Button("Auto-fill", "auto fill", "Build", "#3F6FB5", "#5A8AD0", "#FFFFFF", 14));
        Obj(s, "Clear Deck", 678, 290, 84, 34, mechs: Button("Clear", "clear deck", "Build", "#4A3F36", "#6A5A4A", "#EBDDBF", 14));
        Obj(s, "Save Deck", 590, 332, 172, 40, mechs: Button("Save & fight", "save deck", "Build", "#D9822B", "#F0A050", size: 17));
        Obj(s, "Build Back", 590, 380, 172, 30, mechs: Button("Back", "title", "Build", "#4A3F36", "#6A5A4A", "#EBDDBF", 13));

        // ---- Title ----
        Obj(s, "Title Backdrop", 0, 0, 800, 450, color: "#000000", mechs: Panel("Title", color: "#C00A0604", shown: true));
        Obj(s, "Title Card Left", 52, 116, 152, 212, "", "#000000", Panel("Title", color: ""), M("ShowcaseCard", ("card_name", "Flare Drake")));
        Obj(s, "Title Card Right", 596, 116, 152, 212, "", "#000000", Panel("Title", color: ""), M("ShowcaseCard", ("card_name", "Elder Oakheart")));
        Obj(s, "Title", 200, 64, 400, 84, mechs: Panel("Title", "EMBER DUEL", 54, titleColor: "#FFB347"));
        Obj(s, "Subtitle", 200, 146, 400, 26, mechs: Panel("Title", "A duel of cards, sparks and steel", 15, titleColor: "#E8D5B5"));
        Obj(s, "Fight Button", 300, 196, 200, 46, mechs: Button("Fight!", "fight", "Title", "#D9822B", "#F0A050", size: 20));
        Obj(s, "Build Button", 300, 252, 200, 40, mechs: Button("Build deck", "build", "Title", "#3F6FB5", "#5A8AD0", "#FFFFFF", 16));
        Obj(s, "Rules Button", 300, 302, 200, 40, mechs: Button("How to play", "rules", "Title", "#5A4A7A", "#76659A", "#FFFFFF", 16));
        Obj(s, "Scoreboard", 250, 356, 300, 28, mechs: M("Scoreboard"));

        // ---- How to play ----
        Obj(s, "Rules Page", 90, 36, 620, 336, color: "#000000", mechs: Panel("Rules", Rules, 14, "#F0140E0A", "#F6E7C8"));
        Obj(s, "Rules Back", 330, 384, 140, 38, mechs: Button("Back", "title", "Rules", "#D9822B", "#F0A050"));

        // ---- Result (over the battle) ----
        Obj(s, "Result Backdrop", 0, 0, 800, 450, color: "#000000", mechs: [Panel("Result", color: "#A8000000"), Top(70)]);
        Obj(s, "Result Panel", 200, 104, 400, 160, color: "#000000", mechs: [Panel("Result", "", 22, "#F01E140E", "#FFE6B0"), Top(71)]);
        Obj(s, "Play Again", 220, 280, 170, 42, mechs: [Button("Play again", "fight", "Result", "#D9822B", "#F0A050", size: 17), Top(72)]);
        Obj(s, "Change Deck", 410, 280, 170, 42, mechs: [Button("Change deck", "build", "Result", "#3F6FB5", "#5A8AD0", "#FFFFFF", 16), Top(72)]);
        Obj(s, "Result Title", 330, 334, 140, 32, mechs: [Button("Title", "title", "Result", "#4A3F36", "#6A5A4A", "#EBDDBF", 13), Top(72)]);

        // ---- Always on top ----
        Obj(s, "Banner", 200, 186, 400, 64, "Banner", "#8A2A18", M("Banner"));
        Obj(s, "Zoom", 600, 24, 152, 212, color: "#000000", mechs: M("CardZoom"));
        return s;
    }

    // ---------------- Sounds ----------------

    static readonly (string File, Func<byte[]> Make)[] SoundsToWrite =
    [
        ("draw.wav", () => Synth(0.16, t => Noise(t) * 0.5 * Env(t, 0.005, 0.12) * (0.3 + t * 4))),
        ("place.wav", () => Synth(0.22, t => (Math.Sin(2 * Math.PI * (140 - 200 * t) * t) + Noise(t) * 0.4) * Env(t, 0.003, 0.15))),
        ("hit.wav", () => Synth(0.2, t => (Math.Sign(Math.Sin(2 * Math.PI * (220 - 500 * t) * t)) * 0.5 + Noise(t) * 0.7) * Env(t, 0.002, 0.12))),
        ("spell.wav", () => Synth(0.45, t => Notes(t, [659.3, 880, 1318.5], 0.08) * Env(t, 0.01, 0.35))),
        ("heal.wav", () => Synth(0.5, t => (Math.Sin(2 * Math.PI * (440 + 300 * t) * t) * 0.6 + Math.Sin(2 * Math.PI * (660 + 450 * t) * t) * 0.3) * Env(t, 0.03, 0.4))),
        ("shield.wav", () => Synth(0.4, t => (Math.Sin(2 * Math.PI * 1200 * t) * 0.5 + Math.Sin(2 * Math.PI * 1810 * t) * 0.3) * Env(t, 0.002, 0.25))),
        ("die.wav", () => Synth(0.45, t => Math.Sign(Math.Sin(2 * Math.PI * (300 - 400 * t) * t)) * 0.4 * Env(t, 0.005, 0.35))),
        ("click.wav", () => Synth(0.15, t => Math.Sin(2 * Math.PI * 900 * t) * Env(t, 0.001, 0.05))),
        ("win.wav", () => Synth(1.1, t => Notes(t, [523.3, 659.3, 784, 1046.5], 0.16) * Env(t, 0.01, 0.9))),
        ("lose.wav", () => Synth(1.1, t => Notes(t, [392, 349.2, 311.1, 261.6], 0.2) * Env(t, 0.01, 0.9))),
    ];

    static readonly Random NoiseRng = new(11);
    static double Noise(double _) => NoiseRng.NextDouble() * 2 - 1;

    /// <summary>A quick attack, then a decay that ends at <paramref name="decay"/> seconds.</summary>
    static double Env(double t, double attack, double decay) => t < attack ? t / attack : Math.Max(0, 1 - (t - attack) / Math.Max(0.001, decay));

    /// <summary>Notes one after another, each <paramref name="step"/> seconds; a soft square-ish tone.</summary>
    static double Notes(double t, double[] freqs, double step)
    {
        int i = Math.Min(freqs.Length - 1, (int)(t / step));
        double local = t - i * step, f = freqs[i];
        double v = Math.Sin(2 * Math.PI * f * t) * 0.7 + Math.Sign(Math.Sin(2 * Math.PI * f * t)) * 0.15;
        return v * Math.Min(1, local / 0.01);
    }

    static byte[] Synth(double seconds, Func<double, double> f)
    {
        int n = (int)(SoundSynth.Rate * seconds);
        var s = new double[n];
        double max = 1e-9;
        for (int i = 0; i < n; i++) { s[i] = f((double)i / SoundSynth.Rate); max = Math.Max(max, Math.Abs(s[i])); }
        var pcm = new short[n];
        int fade = Math.Min(n / 2, SoundSynth.Rate / 200);
        for (int i = 0; i < n; i++)
        {
            double g = i < fade ? (double)i / fade : i > n - fade ? (double)(n - i) / fade : 1;
            pcm[i] = (short)Math.Clamp(s[i] / max * 0.6 * g * short.MaxValue, short.MinValue, short.MaxValue);
        }
        return SoundSynth.Wav(pcm);
    }
}
