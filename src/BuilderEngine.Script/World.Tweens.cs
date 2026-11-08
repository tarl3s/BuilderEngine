using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

public sealed partial class GameObject
{
    /// <summary>0 = invisible, 1 = solid. 'fade Boss to 0.5 in 1' changes it.</summary>
    public double Opacity = 1;
    // A newer tween of the same kind takes over from an older one (the older one's line just finishes).
    internal int MoveTween, SizeTween, FadeTween;
}

/// <summary>
/// Tweens: change a value smoothly over time and wait until it's done (only the 'when' running it waits).
/// <code>
/// move Boss to 400, 300 in 2 seconds ease out
/// move by 0, -50 in 0.5          (no object = this object)
/// resize Door to 32, 0 in 1
/// fade Ghost to 0.2 in 1
/// </code>
/// Without <c>in</c> the change is instant. The old <c>move dx, dy</c> still moves this object right away.
/// Easing: <c>ease inout</c> (default: starts and ends gently), <c>in</c>, <c>out</c>, <c>linear</c>.
/// </summary>
public sealed partial class World
{
    [ModuleInitializer]
    internal static void InstallTweens() => Installers.Add(w =>
    {
        w.Commands["move"] = MoveCommand;
        w.Commands["resize"] = ResizeCommand;
    });

    public static readonly string[] Easings = ["inout", "in", "out", "linear"];

    /// <summary>0..1 → 0..1 along an easing curve; always 0 at 0 and 1 at 1.</summary>
    public static double Ease(string? kind, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return kind switch
        {
            "linear" => t,
            "in" => t * t,
            "out" => 1 - (1 - t) * (1 - t),
            _ => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2,
        };
    }

    static string? EaseOf(CommandContext c)
    {
        string? e = NamedWord(c, "ease");
        if (e == null) return null;
        e = e.Replace("_", "").Replace("-", "");
        if (e is "inout" or "in" or "out" or "linear") return e;
        throw c.Error($"'ease' can be in, out, inout or linear — not '{e}'.");
    }

    /// <summary>
    /// Runs <paramref name="apply"/> from 0 to 1 over <paramref name="seconds"/> of game time, and makes the line wait
    /// until it's done. <paramref name="alive"/> false = a newer tween took over: stop without changing anything.
    /// </summary>
    internal static void Tween(CommandContext c, double seconds, string? ease, Action<double> apply, Func<bool>? alive = null)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) { apply(1); return; }
        var w = c.World;
        double t0 = w.Time;
        c.WaitUntil(() =>
        {
            if (alive != null && !alive()) return true;
            double k = (w.Time - t0) / seconds;
            if (k >= 1 - 1e-6) { apply(1); return true; }
            apply(Ease(ease, k));
            return false;
        });
    }

    static (double X, double Y) TwoNumbers(CommandContext c, List<object?> values, string what)
    {
        if (values.Count == 1 && values[0] is GameObject g) return (g.X, g.Y);
        if (values.Count < 2) throw c.Error($"'{what}' needs two numbers, like {what} 400, 300.");
        return (c.Number(values[0]), c.Number(values[1]));
    }

    static void MoveCommand(CommandContext c)
    {
        bool to = c.Has("to"), by = c.Has("by");
        var target = ObjectAt(c, 0);
        if (!to && !by)
        {
            // The old 'move dx, dy' (this object) — or 'move Boss 10, 0'.
            int first = target != null ? 1 : 0;
            if (c.Args.Count - first < 2) throw c.Error("'move' needs two numbers: 'move 10, 0' — or 'move Boss to 400, 300 in 2'.");
            var who = target ?? c.Self;
            who.X += c.Number(c.Args[first]);
            who.Y += c.Number(c.Args[first + 1]);
            return;
        }
        if (target == null && c.Args.Count > 0 && c.Args[0] is not GameObject)
            throw c.Error($"There's no object called '{DescribeArg(c, 0)}' to move.");
        var o = target ?? c.Self;
        var (ax, ay) = TwoNumbers(c, c.Named[to ? "to" : "by"], "move");
        double sx = o.X, sy = o.Y;
        double tx = to ? ax : sx + ax, ty = to ? ay : sy + ay;
        int id = ++o.MoveTween;
        Tween(c, NamedNumber(c, "in") ?? 0, EaseOf(c), k =>
        {
            o.X = sx + (tx - sx) * k;
            o.Y = sy + (ty - sy) * k;
            if (o.HasBody) { o.Vx = 0; o.Vy = 0; }
        }, () => o.MoveTween == id && !o.Destroyed);
    }

    static void ResizeCommand(CommandContext c)
    {
        var target = ObjectAt(c, 0);
        var o = target ?? c.Self;
        List<object?> values = c.Has("to") ? c.Named["to"] : c.Args.Skip(target != null ? 1 : 0).ToList();
        if (values.Count < 2) throw c.Error("'resize' needs a width and a height: resize Door to 32, 0 in 1.");
        double sw = o.Width, sh = o.Height, tw = Math.Max(0, c.Number(values[0])), th = Math.Max(0, c.Number(values[1]));
        int id = ++o.SizeTween;
        Tween(c, NamedNumber(c, "in") ?? 0, EaseOf(c), k =>
        {
            o.Width = sw + (tw - sw) * k;
            o.Height = sh + (th - sh) * k;
        }, () => o.SizeTween == id && !o.Destroyed);
    }

    /// <summary>'fade Ghost to 0.2 in 1' (no 'to' = fade away to 0). Called by the 'fade' command for objects.</summary>
    static void FadeObject(CommandContext c, GameObject o)
    {
        double to = c.Has("to") ? Math.Clamp(c.Number(c.NamedArg("to")), 0, 1) : 0;
        double from = o.Opacity;
        int id = ++o.FadeTween;
        Tween(c, NamedNumber(c, "in") ?? 0, EaseOf(c) ?? "linear", k => o.Opacity = from + (to - from) * k,
            () => o.FadeTween == id && !o.Destroyed);
    }

    static string DescribeArg(CommandContext c, int i) =>
        i < c.Raw.Count && c.Raw[i] is NameExpr n ? n.Full : i < c.Args.Count ? ToText(c.Args[i]) : "";
}
