using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// Whole-screen effects and the game clock:
/// <code>
/// fade out in 1            (to black; 'fade out in 1 with "white"' for another colour) — waits
/// fade in in 1             (back from it) — waits
/// fade Ghost to 0.3 in 1   (an object; see World.Tweens.cs) — waits
/// flash                    flash "red"      (a quick flash of colour; doesn't wait)
/// slow motion 0.3 for 2    slow motion off  (game speed; 'for' counts real seconds)
/// pause game               resume game      (the clock stops; keys still reach dialogues and 'when key')
/// </code>
/// </summary>
public sealed partial class World
{
    /// <summary>0 = clear, 1 = the screen is all <see cref="FadeColor"/>.</summary>
    public double FadeAmount { get; private set; }
    public string FadeColor { get; private set; } = "#000000";
    public double FlashAmount { get; private set; }
    public string FlashColor { get; private set; } = "#FFFFFF";
    const double FlashTime = 0.35;
    double _slowLeft;
    int _fadeTween;

    [ModuleInitializer]
    internal static void InstallScreen() => Installers.Add(w =>
    {
        w.Commands["fade"] = FadeCommand;
        w.Commands["fade out"] = c => c.World.FadeScreen(c, 1);
        w.Commands["fade in"] = c => c.World.FadeScreen(c, 0);
        w.Commands["flash"] = c =>
        {
            var world = c.World;
            world.FlashColor = c.Args.Count > 0 && c.Args[0] is string s && s.Length > 0 ? s : WordAt(c, 0) ?? "#FFFFFF";
            world.FlashAmount = 0.85;
        };
        w.Commands["slow motion"] = c =>
        {
            var world = c.World;
            if (WordAt(c, 0) is "off" or "normal" || c.Args.Count == 0) { world.TimeScale = 1; world._slowLeft = 0; return; }
            double f = c.Number(c.Args[0]);
            if (f <= 0) throw c.Error("'slow motion' needs a speed above 0: 0.5 = half speed, 1 = normal.");
            world.TimeScale = Math.Clamp(f, 0.01, 10);
            world._slowLeft = NumberAfterWord(c, "for") ?? 0;
        };
        w.Commands["pause game"] = c => c.World.Paused = true;
        w.Commands["resume game"] = c => c.World.Paused = false;
    });

    /// <summary>'fade' alone or 'fade in 1' (the parser reads 'fade in in 1' that way too) = fade in; 'fade Boss to 0' = an object.</summary>
    static void FadeCommand(CommandContext c)
    {
        if (c.Args.Count > 0)
        {
            var o = ObjectAt(c, 0) ?? throw c.Error($"There's no object called '{DescribeArg(c, 0)}' to fade. For the screen: fade out / fade in.");
            FadeObject(c, o);
            return;
        }
        c.World.FadeScreen(c, 0);
    }

    void FadeScreen(CommandContext c, double to)
    {
        if (c.Named.TryGetValue("with", out var col) && col.Count > 0 && col[0] is string s && s.Length > 0) FadeColor = s;
        else if (to > 0) FadeColor = "#000000";
        double from = FadeAmount;
        int id = ++_fadeTween;
        Tween(c, NamedNumber(c, "in") ?? 0.5, "linear", k => FadeAmount = from + (to - from) * k, () => _fadeTween == id);
    }

    void UpdateScreen(double realDt)
    {
        if (FlashAmount > 0) FlashAmount = Math.Max(0, FlashAmount - realDt / FlashTime);
        if (_slowLeft > 0)
        {
            _slowLeft -= realDt;
            if (_slowLeft <= 0) { _slowLeft = 0; TimeScale = 1; }
        }
    }
}
