using System.Globalization;

namespace BuilderEngine.Script;

/// <summary>What the event timeline records.</summary>
public enum DebugEventKind { Touched, Emit, Told, Key, Scene, Print, Error, Paused }

/// <summary>One line of the event timeline: when (game time), what, which object (and the other one for a touch).</summary>
public sealed record DebugEvent(double Time, DebugEventKind Kind, string Text, int ObjectId = 0, string? ObjectName = null,
    int OtherId = 0, string? Source = null, int Line = 0)
{
    /// <summary>True when this event is about that object (it did it, was told, or was touched).</summary>
    public bool Involves(int objectId) => objectId != 0 && (ObjectId == objectId || OtherId == objectId);

    public string KindLabel => Kind switch
    {
        DebugEventKind.Touched => "touch",
        DebugEventKind.Emit => "emit",
        DebugEventKind.Told => "told",
        DebugEventKind.Key => "key",
        DebugEventKind.Scene => "scene",
        DebugEventKind.Print => "print",
        DebugEventKind.Error => "error",
        _ => "pause"
    };
}

/// <summary>
/// What the runtime tells a debugger. <see cref="World.Debug"/> is null when none is attached, and then the runtime
/// does nothing extra but a null check per statement.
/// </summary>
public interface IDebugHooks
{
    /// <summary>A statement is about to run. Return true to stop right here (a breakpoint): the game pauses.</summary>
    bool Statement(MechanismInstance inst, string source, Stmt s, bool canBreak);
    /// <summary>Something happened: a touch, a signal, a message, a key, a scene change, a print, an error.</summary>
    void Event(DebugEventKind kind, GameObject? obj, string text, GameObject? other = null, string? source = null, int line = 0);
    /// <summary>Asked at the start of every frame: false holds the game (paused by the debugger).</summary>
    bool AllowFrame();
    /// <summary>True while a 'when' stopped at a breakpoint must keep waiting.</summary>
    bool HoldBreak { get; }
}

/// <summary>Where the game stopped at a breakpoint: the object, its mechanism, whose code (a variant's parent maybe) and the line.</summary>
public sealed record BreakInfo(GameObject Object, MechanismInstance Mechanism, string Source, int Line)
{
    public override string ToString() => $"{Source}, line {Line} ({Object.Name})";
}

/// <summary>One statement the debugger saw run (when <see cref="Debugger.Trace"/> is on).</summary>
public readonly record struct TraceLine(string Object, string Source, int Line);

/// <summary>
/// The debugger behind the editor's Debug panel (no UI here, so tests drive it): pause / resume / step one frame,
/// breakpoints by mechanism and line (the game pauses when any 'when' reaches one), the last
/// <see cref="MaxEvents"/> events, and helpers to show and change values while paused. Attach it to each world
/// (<see cref="Attach"/>); breakpoints and events stay across scenes.
/// </summary>
public sealed class Debugger : IDebugHooks
{
    public const int MaxEvents = 200;
    const int MaxTrace = 5000;

    readonly List<DebugEvent> _events = [];
    readonly HashSet<(string Mech, int Line)> _breakpoints = new(new BreakpointComparer());
    readonly List<TraceLine> _trace = [];
    int _allowFrames;
    bool _stepping;

    public World? World { get; private set; }
    /// <summary>The last events, oldest first.</summary>
    public IReadOnlyList<DebugEvent> Events => _events;
    /// <summary>Changes whenever something the panel shows changes (an event, pause, step, breakpoints).</summary>
    public long Version { get; private set; }
    public bool Paused { get; private set; }
    /// <summary>Set when a breakpoint paused the game; null when paused by hand or running.</summary>
    public BreakInfo? PausedAt { get; private set; }
    /// <summary>Raised on pause, resume, a breakpoint hit and breakpoint changes (on the thread running the game).</summary>
    public event Action? Changed;
    /// <summary>How many statements ran while attached.</summary>
    public long StatementsRun { get; private set; }
    /// <summary>The last statement that ran (object, mechanism whose code, line).</summary>
    public TraceLine? LastStatement { get; private set; }
    /// <summary>Records every statement in <see cref="TraceLog"/> (tests; off by default).</summary>
    public bool Trace { get; set; }
    public IReadOnlyList<TraceLine> TraceLog => _trace;

    // ---------------- Attaching ----------------

    public void Attach(World world)
    {
        if (World != null && World != world && World.Debug == this) World.Debug = null;
        World = world;
        world.Debug = this;
        Touch();
    }

    public void Detach()
    {
        if (World?.Debug == this) World.Debug = null;
        World = null;
        Paused = false;
        PausedAt = null;
        Touch();
    }

    public void ClearEvents()
    {
        _events.Clear();
        _trace.Clear();
        Touch();
    }

    void Touch() => Version++;

    // ---------------- Pause / resume / step ----------------

    public void Pause()
    {
        if (Paused) return;
        Paused = true;
        PausedAt = null;
        Touch();
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (!Paused) return;
        Paused = false;
        PausedAt = null;
        Touch();
        Changed?.Invoke();
    }

    /// <summary>Runs exactly one frame and stays paused (pauses first if it was running).</summary>
    public void StepFrame(double dt = 1.0 / 60)
    {
        if (World == null) return;
        Paused = true;
        PausedAt = null;
        _allowFrames = 1;
        _stepping = true;
        try { World.Frame(dt); }
        finally { _stepping = false; _allowFrames = 0; }
        Touch();
        Changed?.Invoke();
    }

    public bool AllowFrame()
    {
        if (!Paused) return true;
        if (_allowFrames <= 0) return false;
        _allowFrames--;
        return true;
    }

    public bool HoldBreak => Paused && !_stepping;

    // ---------------- Breakpoints ----------------

    public bool HasBreakpoint(string mechanism, int line) => _breakpoints.Contains((mechanism, line));

    /// <summary>Turns a breakpoint on or off. Returns true when it is now on.</summary>
    public bool ToggleBreakpoint(string mechanism, int line)
    {
        bool on = !HasBreakpoint(mechanism, line);
        SetBreakpoint(mechanism, line, on);
        return on;
    }

    public void SetBreakpoint(string mechanism, int line, bool on)
    {
        if (line <= 0 || string.IsNullOrEmpty(mechanism)) return;
        bool changed = on ? _breakpoints.Add((mechanism, line)) : _breakpoints.Remove((mechanism, line));
        if (!changed) return;
        Touch();
        Changed?.Invoke();
    }

    /// <summary>The lines with a breakpoint in one mechanism, in order.</summary>
    public List<int> BreakpointLines(string mechanism) =>
        _breakpoints.Where(b => string.Equals(b.Mech, mechanism, StringComparison.OrdinalIgnoreCase)).Select(b => b.Line).Order().ToList();

    public List<(string Mechanism, int Line)> Breakpoints() =>
        _breakpoints.OrderBy(b => b.Mech, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Line).ToList();

    public void ClearBreakpoints(string? mechanism = null)
    {
        int n = _breakpoints.RemoveWhere(b => mechanism == null || string.Equals(b.Mech, mechanism, StringComparison.OrdinalIgnoreCase));
        if (n == 0) return;
        Touch();
        Changed?.Invoke();
    }

    /// <summary>A mechanism was renamed: its breakpoints follow.</summary>
    public void RenameMechanism(string oldName, string newName)
    {
        foreach (var b in _breakpoints.Where(b => string.Equals(b.Mech, oldName, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _breakpoints.Remove(b);
            _breakpoints.Add((newName, b.Line));
        }
        Touch();
    }

    sealed class BreakpointComparer : IEqualityComparer<(string Mech, int Line)>
    {
        public bool Equals((string Mech, int Line) a, (string Mech, int Line) b) =>
            a.Line == b.Line && string.Equals(a.Mech, b.Mech, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Mech, int Line) b) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(b.Mech), b.Line);
    }

    // ---------------- Runtime hooks ----------------

    public bool Statement(MechanismInstance inst, string source, Stmt s, bool canBreak)
    {
        StatementsRun++;
        var at = new TraceLine(inst.Owner.Name, source, s.Line);
        LastStatement = at;
        if (Trace && _trace.Count < MaxTrace) _trace.Add(at);
        if (!canBreak || _breakpoints.Count == 0 || !_breakpoints.Contains((source, s.Line))) return false;
        Paused = true;
        PausedAt = new BreakInfo(inst.Owner, inst, source, s.Line);
        Event(DebugEventKind.Paused, inst.Owner, $"Paused at {source}, line {s.Line} ({inst.Owner.Name})", null, source, s.Line);
        Changed?.Invoke();
        return true;
    }

    public void Event(DebugEventKind kind, GameObject? obj, string text, GameObject? other = null, string? source = null, int line = 0)
    {
        if (_events.Count >= MaxEvents) _events.RemoveAt(0);
        _events.Add(new DebugEvent(World?.Time ?? 0, kind, text, obj?.Id ?? 0, obj?.Name, other?.Id ?? 0, source, line));
        Touch();
    }

    /// <summary>The events about one object (0 = all), newest first.</summary>
    public List<DebugEvent> EventsFor(int objectId, int max = MaxEvents)
    {
        var list = new List<DebugEvent>();
        for (int i = _events.Count - 1; i >= 0 && list.Count < max; i--)
            if (objectId == 0 || _events[i].Involves(objectId)) list.Add(_events[i]);
        return list;
    }

    // ---------------- Values ----------------

    /// <summary>A value as the panel shows it: 3.5, "text", true, an object's name, [1, 2, 3], nothing.</summary>
    public static string Show(object? v, int max = 60) => Show(v, max, 0);

    /// <summary>Lists inside lists are shown a few levels deep (a list inside itself would never end).</summary>
    static string Show(object? v, int max, int depth)
    {
        string s = v switch
        {
            null => "nothing",
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            string t => Printer.Quote(t),
            GameObject g => g.Destroyed ? $"{g.Name} (gone)" : g.Name,
            MechanismInstance m => m.Def.Name,
            List<object?> when depth >= 4 => "[…]",
            List<object?> l => "[" + string.Join(", ", l.Select(x => Show(x, max, depth + 1))) + "]",
            _ => v.ToString() ?? "?" // a record: World.ToText, which stops at a record inside itself
        };
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }

    /// <summary>
    /// Changes a variable from what the person typed: a number stays a number, true/false a yes/no, a text a text
    /// (quotes optional); anything else is read as a value (<c>[1, 2]</c>, <c>"hi"</c>, <c>3</c>). Returns null when
    /// it worked, otherwise why not.
    /// </summary>
    public static string? SetValue(Dictionary<string, object?> bag, string name, string text)
    {
        bag.TryGetValue(name, out var old);
        text = text.Trim();
        object? value;
        switch (old)
        {
            case double:
                if (!Project.TryNumber(text, out var d)) return $"'{name}' is a number: write a number.";
                value = d;
                break;
            case bool:
                switch (text.ToLowerInvariant())
                {
                    case "true" or "yes" or "1" or "on": value = true; break;
                    case "false" or "no" or "0" or "off": value = false; break;
                    default: return $"'{name}' is true or false.";
                }
                break;
            case string:
                value = text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
                break;
            case GameObject or MechanismInstance:
                return $"'{name}' is an object: it can't be typed.";
            default:
                try { value = ParamTypes.TryConstant(Parser.ParseExpression(text), out var c) ? c : text; }
                catch (ScriptError) { value = text; }
                break;
        }
        bag[name] = value;
        return null;
    }
}
