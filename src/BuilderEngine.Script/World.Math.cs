using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// Angles and boxes for games where things fly in every direction (a boss's ring of shards, a creature that wanders):
/// <code>
/// sin(90)  cos(0)                     degrees, so a circle is 0..360
/// angle(self, player)                 the direction from one object to another, in degrees (0 = right, 90 = down)
/// angle(x1, y1, x2, y2)               the same between two points
/// overlaps(self, player)              are the two boxes on top of each other right now (not only when they start)
/// clamp(hp, 0, 10)                    a number kept between two others
/// random_number(0.5, 2)               a random number with decimals (random(1, 6) gives whole numbers)
/// </code>
/// A ring of 8 shards: <c>for each i in range(0, 7)</c> → <c>made.vx = cos(i * 45) * 200</c>, <c>made.vy = sin(i * 45) * 200</c>.
/// </summary>
public sealed partial class World
{
    [ModuleInitializer]
    internal static void InstallMath() => Installers.Add(w =>
    {
        w.Functions["sin"] = a => Math.Sin(Degrees(a, "sin(90)"));
        w.Functions["cos"] = a => Math.Cos(Degrees(a, "cos(0)"));
        w.Functions["angle"] = a =>
        {
            double x1, y1, x2, y2;
            if (a.Count == 2 && a[0] is GameObject g1 && a[1] is GameObject g2)
            {
                (x1, y1) = (g1.X + g1.Width / 2, g1.Y + g1.Height / 2);
                (x2, y2) = (g2.X + g2.Width / 2, g2.Y + g2.Height / 2);
            }
            else if (a.Count == 4) { x1 = ToNumber(a[0], 0); y1 = ToNumber(a[1], 0); x2 = ToNumber(a[2], 0); y2 = ToNumber(a[3], 0); }
            else throw new ScriptError("'angle' needs two objects or two points, like angle(self, player) or angle(x1, y1, x2, y2): degrees, 0 = right, 90 = down.", 0);
            double deg = Math.Atan2(y2 - y1, x2 - x1) * 180 / Math.PI;
            return deg < 0 ? deg + 360 : deg;
        };
        w.Functions["overlaps"] = a =>
        {
            if (a.Count != 2) throw new ScriptError("'overlaps' needs two objects, like overlaps(self, player).", 0);
            return a[0] is GameObject { Destroyed: false } g1 && a[1] is GameObject { Destroyed: false } g2 && g1 != g2 && g1.Overlaps(g2);
        };
        w.Functions["clamp"] = a =>
        {
            if (a.Count != 3) throw new ScriptError("'clamp' needs a number and the lowest and highest it may be, like clamp(hp, 0, 10).", 0);
            double v = ToNumber(a[0], 0), lo = ToNumber(a[1], 0), hi = ToNumber(a[2], 0);
            return Math.Min(Math.Max(v, Math.Min(lo, hi)), Math.Max(lo, hi));
        };
        w.Functions["random_number"] = a =>
        {
            if (a.Count != 2) throw new ScriptError("'random_number' needs the lowest and the highest, like random_number(0.5, 2): a number with decimals.", 0);
            double lo = ToNumber(a[0], 0), hi = ToNumber(a[1], 0);
            return lo + w.Rng.NextDouble() * (hi - lo);
        };
    });

    static double Degrees(List<object?> a, string usage)
    {
        if (a.Count != 1) throw new ScriptError($"'{usage[..usage.IndexOf('(')]}' needs one angle in degrees, like {usage}.", 0);
        return ToNumber(a[0], 0) * Math.PI / 180;
    }
}
