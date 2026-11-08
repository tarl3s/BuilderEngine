using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// Cards and laying things out:
/// <code>
/// shuffle deck                    (mixes a list in place)
/// pop(deck)   pop(deck, 0)        (takes the last item out of the list and gives it; 0 = the first)
/// arrange all("in_hand") in row at 400, 380 gap 10 over 0.3      (centred on x, y; 'over' glides there and waits)
/// arrange cards in fan at 400, 400 gap -20 · in column · in grid at 100, 100 columns 4 · in stack
/// </code>
/// </summary>
public sealed partial class World
{
    [ModuleInitializer]
    internal static void InstallCards() => Installers.Add(w =>
    {
        w.Commands["shuffle"] = c =>
        {
            if (c.Args.Count == 0 || c.Args[0] is not List<object?> list)
                throw c.Error(c.Args.Count == 0 ? "'shuffle' needs a list: shuffle deck." : $"'shuffle' needs a list, but got {Describe(c.Args[0])}.");
            var rng = c.World.Rng;
            for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
        };
        w.Functions["pop"] = a =>
        {
            if (a.Count is < 1 or > 2 || a[0] is not List<object?> list)
                throw new ScriptError("'pop' needs a list (and maybe which item): pop(deck) takes the last one out, pop(deck, 0) the first.", 0);
            if (list.Count == 0) return null;
            int i = a.Count > 1 ? (int)Math.Floor(ToNumber(a[1], 0)) : list.Count - 1;
            if (i < 0) i += list.Count;
            if (i < 0 || i >= list.Count) throw new ScriptError($"The list has {list.Count} item(s), so there's no item {i}.", 0);
            var v = list[i];
            list.RemoveAt(i);
            return v;
        };
        w.Commands["arrange"] = ArrangeCommand;
    });

    static void ArrangeCommand(CommandContext c)
    {
        if (c.Args.Count == 0) throw c.Error("'arrange' needs objects: arrange all(\"in_hand\") in row at 400, 380 gap 10.");
        var objs = ObjectsIn(c, 0).ToList();
        string layout = c.RawNamed.TryGetValue("in", out var raw) && raw.Count > 0 && raw[0] is NameExpr { Path.Length: 1 } n ? n.Path[0].ToLowerInvariant()
            : c.Named.TryGetValue("in", out var l) && l.Count > 0 && l[0] is string s ? s.ToLowerInvariant() : "row";
        var words = Words(c, "in", "at", "with", "over");
        double x = c.Self.X, y = c.Self.Y;
        if (c.Named.TryGetValue("at", out var at) && at.Count >= 2 && at[0] is not null) { x = c.Number(at[0]); y = c.Number(at[1]); }
        double gap = ValueAfter(words, "gap") is { } g ? c.Number(g) : c.Has("with") && c.NamedArg("with") is double wg ? wg : 10;
        int columns = ValueAfter(words, "columns") is { } colsWord ? Math.Max(1, (int)Math.Round(c.Number(colsWord))) : 4;
        double seconds = NamedNumber(c, "over") ?? 0;
        if (objs.Count == 0) return;

        var targets = new (double X, double Y)[objs.Count];
        switch (layout)
        {
            case "row" or "fan":
            {
                double total = objs.Sum(o => o.Width) + gap * (objs.Count - 1), left = x - total / 2;
                double mid = (objs.Count - 1) / 2.0;
                for (int i = 0; i < objs.Count; i++)
                {
                    var o = objs[i];
                    double dy = layout == "fan" ? Math.Abs(i - mid) * Math.Abs(i - mid) * Math.Max(2, o.Height * 0.04) : 0;
                    targets[i] = (left, y - o.Height / 2 + dy);
                    left += o.Width + gap;
                }
                break;
            }
            case "column":
            {
                double total = objs.Sum(o => o.Height) + gap * (objs.Count - 1), top = y - total / 2;
                for (int i = 0; i < objs.Count; i++) { targets[i] = (x - objs[i].Width / 2, top); top += objs[i].Height + gap; }
                break;
            }
            case "grid":
            {
                double cw = objs.Max(o => o.Width) + gap, ch = objs.Max(o => o.Height) + gap;
                int cols = Math.Min(columns, objs.Count), rows = (objs.Count + columns - 1) / columns;
                double left = x - (cols * cw - gap) / 2, top = y - (rows * ch - gap) / 2;
                for (int i = 0; i < objs.Count; i++) targets[i] = (left + (i % columns) * cw, top + (i / columns) * ch);
                break;
            }
            case "stack" or "pile":
                for (int i = 0; i < objs.Count; i++) targets[i] = (x - objs[i].Width / 2 + i * 0.5, y - objs[i].Height / 2 - i * 0.5);
                break;
            default:
                throw c.Error($"'arrange' can lay things out in row, fan, column, grid or stack — not '{layout}'.");
        }

        var from = objs.Select(o => (o.X, o.Y)).ToArray();
        for (int i = 0; i < objs.Count; i++) objs[i].MoveTween++;
        var ids = objs.Select(o => o.MoveTween).ToArray();
        Tween(c, seconds, "out", k =>
        {
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                if (o.Destroyed || o.MoveTween != ids[i] || o == c.World.Dragging) continue;
                o.X = from[i].X + (targets[i].X - from[i].X) * k;
                o.Y = from[i].Y + (targets[i].Y - from[i].Y) * k;
            }
        });
    }
}
