using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

// PERFORMANCE: what lets a game run ~2000 objects and big tile maps at 60 fps.
//  - every mechanism's 'when's sorted by event once (WhenIndex), every object's mechanisms flattened once (Flat);
//  - a 'when tick' still waiting is marked on its instance instead of searching every running 'when';
//  - touches come from a grid broadphase (only objects with 'when touched' — or every object while a debugger
//    watches — look for neighbours), and bodies ask a grid for the solids near them (World.Physics.cs).
// Nothing here allocates per frame once the lists have grown.

/// <summary>A mechanism's 'when's by event, made once per mechanism definition.</summary>
internal sealed class WhenIndex
{
    public WhenDef[] Start = [], Tick = [], Key = [], Touched = [], Told = [], Game = [];
    static readonly ConditionalWeakTable<MechanismDef, WhenIndex> Cache = new();

    public static WhenIndex For(MechanismDef d) => Cache.GetValue(d, Build);

    static WhenIndex Build(MechanismDef d)
    {
        WhenDef[] Of(EventKind k) => d.Whens.Where(w => w.Event.Kind == k).ToArray();
        return new WhenIndex
        {
            Start = Of(EventKind.Start), Tick = Of(EventKind.Tick), Key = Of(EventKind.Key),
            Touched = Of(EventKind.Touched), Told = Of(EventKind.Told), Game = Of(EventKind.Game),
        };
    }
}

public sealed partial class MechanismInstance
{
    WhenIndex? _ix;
    internal WhenIndex Ix => _ix ??= WhenIndex.For(Def);
    /// <summary>'when tick's of this instance still running (waiting): they aren't started again until they end.</summary>
    WhenDef? _busy1;
    List<WhenDef>? _busyMore;

    internal bool IsTickBusy(WhenDef w) => ReferenceEquals(_busy1, w) || (_busyMore != null && _busyMore.Contains(w));

    internal void MarkTickBusy(WhenDef w, bool busy)
    {
        if (busy)
        {
            if (IsTickBusy(w)) return;
            if (_busy1 == null) _busy1 = w;
            else (_busyMore ??= []).Add(w);
        }
        else if (ReferenceEquals(_busy1, w)) _busy1 = null;
        else _busyMore?.Remove(w);
    }
}

public sealed partial class GameObject
{
    MechanismInstance[]? _flat;
    /// <summary>Every mechanism on the object (also the ones used inside others), made once.</summary>
    internal MechanismInstance[] Flat => _flat ??= AllMechanisms().ToArray();
    /// <summary>Where it is in the world's list during this step (draw order, touch order).</summary>
    internal int StepIndex;
    internal int GridStamp;
    byte _flags; // 1 = known, 2 = has 'when touched', 4 = has mouse events

    void EnsureFlags()
    {
        if (_flags != 0) return;
        int f = 1;
        foreach (var m in Flat)
        {
            if (m.Ix.Touched.Length > 0) f |= 2;
            foreach (var w in m.Ix.Game)
                if (w.Event.Name is "clicked" or "hovered" or "unhovered" or "picked up" or "dropped" or "received") f |= 4;
        }
        _flags = (byte)f;
    }

    /// <summary>Some mechanism of it has 'when touched by'.</summary>
    internal bool ListensTouch { get { EnsureFlags(); return (_flags & 2) != 0; } }
    /// <summary>The mouse can click, hover or drag it (it has those events, or it is draggable).</summary>
    internal bool MouseAware { get { EnsureFlags(); return (_flags & 4) != 0 || Draggable; } }
}

/// <summary>
/// A uniform grid of objects for "what is near this box" questions. Objects covering many cells go to
/// <see cref="Big"/> (always returned). Cleared and refilled every step; lists are reused.
/// </summary>
internal sealed class SpatialGrid(double cell = 64, int maxCellsPerObject = 64)
{
    readonly double _cell = cell;
    readonly Dictionary<long, List<GameObject>> _cells = new();
    readonly List<List<GameObject>> _used = [];
    public readonly List<GameObject> Big = [];
    int _stamp;
    public int Count { get; private set; }

    static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

    public void Clear()
    {
        foreach (var l in _used) l.Clear();
        _used.Clear();
        Big.Clear();
        Count = 0;
        if (_cells.Count > 60000) _cells.Clear(); // things flew far away: don't keep every cell they visited
    }

    int Cell(double v) => (int)Math.Floor(v / _cell);

    public void Insert(GameObject o, double pad = 0)
    {
        Count++;
        int x0 = Cell(o.X - pad), y0 = Cell(o.Y - pad), x1 = Cell(o.X + Math.Max(0, o.Width) + pad), y1 = Cell(o.Y + Math.Max(0, o.Height) + pad);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > maxCellsPerObject) { Big.Add(o); return; }
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
            {
                long k = Key(cx, cy);
                if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = [];
                if (l.Count == 0) _used.Add(l);
                l.Add(o);
            }
    }

    /// <summary>Every object whose cells meet the box (each once, Big ones included), added to <paramref name="result"/>.</summary>
    public void Query(double x0, double y0, double x1, double y1, List<GameObject> result)
    {
        int stamp = ++_stamp;
        foreach (var o in Big)
            if (o.GridStamp != stamp) { o.GridStamp = stamp; result.Add(o); }
        int cx0 = Cell(x0), cy0 = Cell(y0), cx1 = Cell(x1), cy1 = Cell(y1);
        if ((long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1) > 4096) // a huge box: just look at every cell in use
        {
            foreach (var l in _used)
                foreach (var o in l)
                    if (o.GridStamp != stamp && o.X <= x1 && o.X + o.Width >= x0 && o.Y <= y1 && o.Y + o.Height >= y0) { o.GridStamp = stamp; result.Add(o); }
            return;
        }
        for (int cx = cx0; cx <= cx1; cx++)
            for (int cy = cy0; cy <= cy1; cy++)
            {
                if (!_cells.TryGetValue(Key(cx, cy), out var l)) continue;
                for (int i = 0; i < l.Count; i++)
                {
                    var o = l[i];
                    if (o.GridStamp != stamp) { o.GridStamp = stamp; result.Add(o); }
                }
            }
    }
}

public sealed partial class World
{
    readonly SpatialGrid _touchGrid = new();
    readonly List<GameObject> _live = [];
    readonly List<GameObject> _near = [];
    HashSet<(int, int)> _touchingNext = [];
    readonly List<(int I, int J)> _newTouches = [];

    /// <summary>A new object joined the world (step 1): its caches are made now, not in the middle of a frame.</summary>
    void ObjectJoined(GameObject o)
    {
        _ = o.ListensTouch;
        if (o.Tags.Contains("ui")) o.Ui = true;
    }

    /// <summary>
    /// Step 3: pairs that start touching fire 'when touched by' on both, in the same order as the old all-pairs loop
    /// (by position in the world's list). Only objects that listen for touches (or all, with a debugger) look around.
    /// </summary>
    void StepTouches()
    {
        _live.Clear();
        _touchGrid.Clear();
        for (int i = 0; i < _objects.Count; i++)
        {
            var o = _objects[i];
            if (o.Destroyed) continue;
            o.StepIndex = _live.Count;
            _live.Add(o);
            _touchGrid.Insert(o);
        }
        bool everyone = Debug != null;
        _touchingNext.Clear();
        _newTouches.Clear();
        for (int i = 0; i < _live.Count; i++)
        {
            var a = _live[i];
            bool aListens = everyone || a.ListensTouch;
            if (!aListens) continue;
            _near.Clear();
            _touchGrid.Query(a.X - 1, a.Y - 1, a.X + a.Width + 1, a.Y + a.Height + 1, _near);
            for (int k = 0; k < _near.Count; k++)
            {
                var b = _near[k];
                if (b == a || a.Ui != b.Ui) continue;
                bool bListens = everyone || b.ListensTouch;
                if (bListens && b.StepIndex < a.StepIndex) continue; // that pair was looked at from b
                if (!a.InContact(b)) continue;
                var key = (Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id));
                _touchingNext.Add(key);
                if (!_touching.Contains(key)) _newTouches.Add((Math.Min(a.StepIndex, b.StepIndex), Math.Max(a.StepIndex, b.StepIndex)));
            }
        }
        (_touching, _touchingNext) = (_touchingNext, _touching);
        if (_newTouches.Count == 0) return;
        if (_newTouches.Count > 1) _newTouches.Sort(static (p, q) => p.I != q.I ? p.I.CompareTo(q.I) : p.J.CompareTo(q.J));
        foreach (var (i, j) in _newTouches)
        {
            var a = _live[i]; var b = _live[j];
            Debug?.Event(DebugEventKind.Touched, a, $"{a.Name} touched {b.Name}", b);
            FireTouch(a, b);
            FireTouch(b, a);
        }
    }

    /// <summary>Every pair of objects in contact now, the slow way (tests compare it with the broadphase).</summary>
    public List<(int, int)> ContactsBruteForce()
    {
        var list = new List<(int, int)>();
        var live = _objects.Where(o => !o.Destroyed).ToArray();
        for (int i = 0; i < live.Length; i++)
            for (int j = i + 1; j < live.Length; j++)
                if (live[i].Ui == live[j].Ui && live[i].InContact(live[j])) list.Add((Math.Min(live[i].Id, live[j].Id), Math.Max(live[i].Id, live[j].Id)));
        return list;
    }

    /// <summary>The pairs the last step found touching (for tests).</summary>
    public IReadOnlyCollection<(int, int)> TouchingPairs => _touching;
}
