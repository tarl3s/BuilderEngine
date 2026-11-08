using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// Finding the way around solid tiles (creatures that hunt the player through a cave, guards that walk to a spot):
/// <code>
/// chase player at 90                  (call it every tick) walk towards it the short way round the walls
/// chase 400, 300 at 90                the same towards a point
/// path_next(player) · path_next(x, y) the point to head for now on that way, as [x, y] (self.x/vx stay yours)
/// can_see(player) · can_see(x, y)     no solid tile in a straight line between this object and it
/// can_see(a, b)                       the same between two objects
/// </code>
/// The way is a breadth-first search over the cells of the solid tile layers (4 directions, up to
/// <see cref="NavSearchLimit"/> cells), remembered per object and searched again when either end moves to another
/// cell, a solid tile changes or it is older than <see cref="NavRefresh"/> seconds. The object heads for the furthest
/// cell of the way it can reach in a straight line without its box catching a corner, so it moves smoothly through
/// open caves and turns corners cleanly. With no tiles, or no way at all, it heads straight for the target.
/// </summary>
public sealed partial class World
{
    /// <summary>How many cells one search may visit (a 100 × 100 map is 10 000).</summary>
    public int NavSearchLimit { get; set; } = 12000;
    /// <summary>Seconds a found way is kept before it is searched again (when nothing else made it stale).</summary>
    public double NavRefresh { get; set; } = 0.75;
    /// <summary>How many searches ran (tests and the debugger).</summary>
    public int NavSearches { get; private set; }

    sealed class NavMemo
    {
        public readonly int[] Path = new int[PathKeep]; // cell indexes, Path[0] = where it was when searched
        public int Count, To, Version;
        public bool Truncated;
        public double Time;
    }

    const int PathKeep = 32, LookAhead = 8;
    readonly Dictionary<GameObject, NavMemo> _nav = new();
    int[] _navStamp = [], _navDist = [], _navQueue = [];
    int _navGen, _navCalls;

    [ModuleInitializer]
    internal static void InstallNavigation() => Installers.Add(w =>
    {
        w.Commands["chase"] = ChaseCommand;
        w.Functions["path_next"] = a =>
        {
            var self = w.CallingInst?.Owner ?? throw new ScriptError("'path_next' works inside a mechanism.", 0);
            var (tx, ty) = TargetPoint(a, 0, "path_next(player) or path_next(x, y)");
            var (x, y) = w.NextStep(self, tx, ty);
            return new List<object?> { x, y };
        };
        w.Functions["can_see"] = a =>
        {
            double x0, y0, x1, y1;
            if (a.Count == 2 && a[0] is GameObject g0 && a[1] is GameObject g1)
            {
                (x0, y0) = Center(g0);
                (x1, y1) = Center(g1);
            }
            else
            {
                var self = w.CallingInst?.Owner ?? throw new ScriptError("'can_see' works inside a mechanism.", 0);
                (x0, y0) = Center(self);
                (x1, y1) = TargetPoint(a, 0, "can_see(player), can_see(x, y) or can_see(a, b)");
            }
            return w.LineClear(x0, y0, x1, y1, 0, 0);
        };
    });

    static (double X, double Y) Center(GameObject g) => (g.X + g.Width / 2, g.Y + g.Height / 2);

    static (double X, double Y) TargetPoint(List<object?> a, int i, string usage)
    {
        if (a.Count > i && a[i] is GameObject g) return Center(g);
        if (a.Count > i && a[i] is List<object?> { Count: >= 2 } p) return (ToNumber(p[0], 0), ToNumber(p[1], 0));
        if (a.Count > i + 1) return (ToNumber(a[i], 0), ToNumber(a[i + 1], 0));
        throw new ScriptError($"Where to? Write it like {usage}" + (a.Count > i && a[i] == null ? " (the object wasn't found)." : "."), 0);
    }

    static void ChaseCommand(CommandContext c)
    {
        var self = c.Self;
        if (c.Args.Count == 0) throw c.Error("'chase' needs what to go to: chase player at 90 (or chase x, y at 90). Call it every tick.");
        if (c.Args[0] == null) { self.Vx = self.Vy = 0; return; } // nothing to chase (find() found none): stand still
        double tx, ty;
        try { (tx, ty) = TargetPoint(c.Args, 0, "chase player at 90"); }
        catch (ScriptError e) { throw c.Error(e.Message); }
        double speed = NamedNumber(c, "at") ?? 80;
        var w = c.World;
        var (ax, ay) = w.NextStep(self, tx, ty);
        var (sx, sy) = Center(self);
        double dx = ax - sx, dy = ay - sy, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 0.5) { self.Vx = self.Vy = 0; return; }
        double dt = w.LastDt > 0 ? w.LastDt : 1.0 / 60;
        double v = Math.Min(Math.Max(0, speed), d / dt); // never past the point it heads for
        self.Vx = dx / d * v;
        self.Vy = dy / d * v;
        if (Math.Abs(self.Vx) > 1) self.FlipX = self.Vx < 0;
    }

    /// <summary>The point <paramref name="self"/> should head for now on its way to (tx, ty).</summary>
    public (double X, double Y) NextStep(GameObject self, double tx, double ty)
    {
        var main = _tiles?.Main;
        if (main == null || !main.Solid) return (tx, ty);
        var (sx, sy) = Center(self);
        var (sc, sr) = main.CellAt(sx, sy);
        var (tc, tr) = main.CellAt(tx, ty);
        if (!main.Inside(sc, sr) || !main.Inside(tc, tr)) return (tx, ty);
        if (sc == tc && sr == tr) return (tx, ty);
        int cols = main.Columns, from = sr * cols + sc, to = tr * cols + tc;
        int ver = SolidVersion();
        double halfW = Math.Max(0, self.Width / 2 - 0.5), halfH = Math.Max(0, self.Height / 2 - 0.5);

        if (++_navCalls % 256 == 0) PurgeNav();
        if (!_nav.TryGetValue(self, out var m)) _nav[self] = m = new NavMemo { Count = 0 };
        int at = m.Count > 0 && m.To == to && m.Version == ver && Time - m.Time < NavRefresh ? Array.IndexOf(m.Path, from, 0, m.Count) : -1;
        if (at < 0 || (m.Truncated && at > m.Count - LookAhead))
        {
            if (!FindWay(main, from, to, m)) { m.Count = 0; return (tx, ty); } // no way: straight at it
            m.To = to; m.Version = ver; m.Time = Time;
            at = 0;
        }
        // The furthest cell of the way it can reach in a straight line (its box clear of every solid corner).
        double cs = main.CellSize;
        for (int k = Math.Min(m.Count - 1, at + LookAhead); k > at; k--)
        {
            int cell = m.Path[k];
            bool last = k == m.Count - 1 && !m.Truncated;
            double px = last ? tx : main.X + (cell % cols + 0.5) * cs, py = last ? ty : main.Y + (cell / cols + 0.5) * cs;
            if (LineClear(sx, sy, px, py, halfW, halfH)) return (px, py);
        }
        // Nothing clear (a corner in the way): line up with the middle of its own cell first.
        return (main.X + (sc + 0.5) * cs, main.Y + (sr + 0.5) * cs);
    }

    /// <summary>Breadth-first search from the target back to the start; fills the memo with the start's way (true when found).</summary>
    bool FindWay(TileLayer main, int from, int to, NavMemo m)
    {
        NavSearches++;
        var t = _tiles!;
        int cols = main.Columns, rows = main.Rows, n = cols * rows;
        if (_navStamp.Length < n) { _navStamp = new int[n]; _navDist = new int[n]; _navQueue = new int[n]; _navGen = 0; }
        int gen = ++_navGen;
        if (gen == int.MaxValue) { Array.Clear(_navStamp); _navGen = gen = 1; }
        int head = 0, tail = 0, visited = 0;
        _navStamp[to] = gen; _navDist[to] = 0; _navQueue[tail++] = to;
        bool found = false;
        while (head < tail && visited < NavSearchLimit)
        {
            int cur = _navQueue[head++];
            visited++;
            if (cur == from) { found = true; break; }
            int c = cur % cols, r = cur / cols, d = _navDist[cur] + 1;
            for (int k = 0; k < 4; k++)
            {
                int nc = c + (k == 0 ? 1 : k == 1 ? -1 : 0), nr = r + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows) continue;
                int ni = nr * cols + nc;
                if (_navStamp[ni] == gen) continue;
                if (ni != from && SolidCell(t, main, nc, nr)) continue; // the start may sit half in a wall: it still counts
                _navStamp[ni] = gen; _navDist[ni] = d; _navQueue[tail++] = ni;
            }
        }
        if (!found) return false;
        // Walk down the distances from the start to the target.
        m.Count = 0;
        int step = from;
        m.Path[m.Count++] = step;
        while (step != to && m.Count < PathKeep)
        {
            int c = step % cols, r = step / cols, best = -1, bestD = _navDist[step];
            for (int k = 0; k < 4; k++)
            {
                int nc = c + (k == 0 ? 1 : k == 1 ? -1 : 0), nr = r + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows) continue;
                int ni = nr * cols + nc;
                if (_navStamp[ni] == gen && _navDist[ni] < bestD) { bestD = _navDist[ni]; best = ni; }
            }
            if (best < 0) break;
            step = best;
            m.Path[m.Count++] = step;
        }
        m.Truncated = step != to;
        return true;
    }

    /// <summary>
    /// No solid tile along the straight line from (x0, y0) to (x1, y1) — for a box of half size (hw, hh) when given
    /// (its four corners are checked), else for the line itself.
    /// </summary>
    public bool LineClear(double x0, double y0, double x1, double y1, double hw, double hh)
    {
        var main = _tiles?.Main;
        if (main == null) return true;
        double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
        int steps = Math.Max(1, (int)Math.Ceiling(len / Math.Max(2, main.CellSize / 4)));
        for (int i = 0; i <= steps; i++)
        {
            double f = (double)i / steps, x = x0 + dx * f, y = y0 + dy * f;
            if (hw <= 0 && hh <= 0) { if (SolidTileAt(x, y)) return false; continue; }
            if (SolidTileAt(x - hw, y - hh) || SolidTileAt(x + hw, y - hh) || SolidTileAt(x - hw, y + hh) || SolidTileAt(x + hw, y + hh)) return false;
        }
        return true;
    }

    void PurgeNav()
    {
        List<GameObject>? gone = null;
        foreach (var o in _nav.Keys) if (o.Destroyed) (gone ??= []).Add(o);
        if (gone != null) foreach (var o in gone) _nav.Remove(o);
    }
}
