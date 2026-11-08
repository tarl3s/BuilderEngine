using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace BuilderEngine.Script;

/// <summary>A thing in the game world. It has no behavior of its own: its mechanisms give it one.</summary>
public sealed partial class GameObject
{
    public int Id { get; internal set; }
    public string Name { get; set; } = "Object";
    public double X, Y, Width = 32, Height = 32;
    public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MechanismInstance> Mechanisms { get; } = [];
    public bool Destroyed { get; internal set; }
    public bool Overlaps(GameObject o) => X < o.X + o.Width && o.X < X + Width && Y < o.Y + o.Height && o.Y < Y + Height;
    public bool Is(string tagOrName) => Tags.Contains(tagOrName) || string.Equals(Name, tagOrName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every mechanism on this object, including the ones used inside others.</summary>
    public IEnumerable<MechanismInstance> AllMechanisms()
    {
        foreach (var m in Mechanisms)
            foreach (var x in m.SelfAndChildren()) yield return x;
    }

    public override string ToString() => Name;
}

/// <summary>One mechanism attached to one object: its own variables and its running 'when's.</summary>
public sealed partial class MechanismInstance
{
    public MechanismDef Def { get; }
    public GameObject Owner { get; }
    public MechanismInstance? Parent { get; }
    public Dictionary<string, object?> Vars { get; } = new();
    public Dictionary<string, MechanismInstance> Children { get; } = new();

    internal MechanismInstance(MechanismDef def, GameObject owner, MechanismInstance? parent)
    {
        Def = def; Owner = owner; Parent = parent;
    }

    public IEnumerable<MechanismInstance> SelfAndChildren()
    {
        yield return this;
        foreach (var c in Children.Values)
            foreach (var x in c.SelfAndChildren()) yield return x;
    }

    public override string ToString() => Def.Name;
}

/// <summary>The game: objects, global variables, the mechanism library and the clock.</summary>
public sealed partial class World
{
    readonly List<GameObject> _objects = [];
    readonly List<GameObject> _pendingAdd = [];
    readonly List<Coroutine> _running = [];
    HashSet<(int, int)> _touching = [];
    int _nextId = 1;

    public IReadOnlyList<GameObject> Objects => _objects;
    public Dictionary<string, MechanismDef> Library { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> Globals { get; } = new();
    public double Time { get; private set; }
    /// <summary>Seconds the last step took (<c>dt</c> in scripts).</summary>
    public double LastDt { get; private set; } = 1.0 / 60;
    /// <summary>What the game printed (and the sounds it played), the newest last; the oldest go after <see cref="MaxOutputLines"/>.</summary>
    public List<string> Output { get; } = [];
    /// <summary>The most lines <see cref="Output"/> keeps: a game printing every frame for hours would otherwise keep them all.</summary>
    public const int MaxOutputLines = 20_000;

    internal void AddOutput(string line)
    {
        if (Output.Count >= MaxOutputLines) Output.RemoveRange(0, MaxOutputLines / 2); // now and then, so adding stays cheap
        Output.Add(line);
    }
    public List<string> Errors { get; } = [];
    public HashSet<string> KeysDown { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extra commands (engine features and the host). A key may have several words ("camera follow", "lock controls"):
    /// a script line <c>camera follow Boss</c> finds it because the leading bare names are tried as part of the name.
    /// A command that must take time (a tween, a dialogue) calls <see cref="CommandContext.WaitUntil"/>.
    /// Built-in ones are in <see cref="RunCommand"/>.
    /// </summary>
    public Dictionary<string, Action<CommandContext>> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Feature files register their commands and per-world setup here (from a <c>[ModuleInitializer]</c>),
    /// so every new World gets them without one shared constructor to edit.
    /// </summary>
    public static readonly List<Action<World>> Installers = [];

    public World()
    {
        foreach (var install in Installers) install(this);
    }

    public event Action<string>? Printed;
    public event Action<string>? SoundPlayed;

    /// <summary>Adds every mechanism in a Builder Script source to the library.</summary>
    public List<MechanismDef> Load(string source)
    {
        var defs = Parser.Parse(source);
        foreach (var d in defs) Library[d.Name] = d;
        _procIndex = null; // user commands are looked up again
        _resolved.Clear(); // variants are merged again (their parent may have changed)
        return defs;
    }

    /// <summary>Mechanisms as they run (variants merged with what they come from), made the first time they are needed.</summary>
    readonly Dictionary<string, (MechanismDef Raw, MechanismDef Def)> _resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A mechanism as it runs — a variant (<c>mechanism RedSlime from Slime</c>) merged with its parent — or null when
    /// there's none with that name. A broken variant (missing parent, a loop) is a friendly error.
    /// </summary>
    public MechanismDef? TryDef(string name, int line = 0)
    {
        if (!Library.TryGetValue(name, out var raw)) return null;
        if (raw.Base == null) return raw;
        if (_resolved.TryGetValue(name, out var c) && ReferenceEquals(c.Raw, raw)) return c.Def;
        MechanismDef def;
        try { def = Variants.Resolve(raw, n => Library.GetValueOrDefault(n)); }
        catch (ScriptError e) when (line > 0) { throw new ScriptError(e.Message, line); }
        _resolved[name] = (raw, def);
        return def;
    }

    /// <summary>Creates an object with these mechanisms. Overrides set params by name.</summary>
    public GameObject Spawn(string name, double x, double y, IEnumerable<string>? mechanisms = null,
        Dictionary<string, Dictionary<string, object?>>? overrides = null, params string[] tags)
    {
        var o = new GameObject { Id = _nextId++, Name = name, X = x, Y = y };
        foreach (var t in tags) o.Tags.Add(t);
        foreach (var m in mechanisms ?? [])
        {
            Dictionary<string, object?>? ov = null;
            overrides?.TryGetValue(m, out ov);
            o.Mechanisms.Add(Instantiate(Find(m, 0), o, null, ov));
        }
        _ = o.Flat; // every mechanism is there now: the flat list can be made (lookups by name use it)
        _pendingAdd.Add(o);
        return o;
    }

    MechanismDef Find(string name, int line) =>
        TryDef(name, line) ?? throw new ScriptError($"There's no mechanism called '{name}'.{Suggest(name, Library.Keys)}", line);

    MechanismInstance Instantiate(MechanismDef def, GameObject owner, MechanismInstance? parent, Dictionary<string, object?>? overrides, int depth = 0)
    {
        if (depth > 16) throw new ScriptError($"'{def.Name}' uses itself in a loop.", def.Line);
        var inst = new MechanismInstance(def, owner, parent);
        var ctx = new Ctx(this, inst);
        foreach (var p in def.Params)
        {
            var value = overrides != null && overrides.TryGetValue(p.Name, out var v) ? v
                : SystemDefault(def.Name, p.Name, out var sv) ? sv // a system's 'includes' setting (World.Systems.cs)
                : p.Default != null ? Eval(p.Default, ctx) : DefaultFor(p.Type);
            // An Object param written as a name ("Boss") is that object, when it is already there.
            if (value is string name && ParamTypes.Kind(p.Type) == ParamKind.Object) value = ObjectNamed(name) ?? (object?)name;
            inst.Vars[p.Name] = value;
        }
        if (def.Includes.Count > 0) RegisterSystem(def, ctx); // World.Systems.cs
        foreach (var u in def.Uses)
        {
            var childDef = Find(u.Name, u.Line);
            var ov = new Dictionary<string, object?>();
            if (u.Positional != null)
            {
                if (childDef.Params.Count == 0) throw new ScriptError($"'{u.Name}' has no params to set.", u.Line);
                ov[childDef.Params[0].Name] = Eval(u.Positional, ctx);
            }
            foreach (var (k, e) in u.Args)
            {
                if (childDef.Params.All(p => p.Name != k))
                    throw new ScriptError($"'{u.Name}' has no param called '{k}'.{Suggest(k, childDef.Params.Select(p => p.Name))}", u.Line);
                ov[k] = Eval(e, ctx);
            }
            inst.Children[u.Name] = Instantiate(childDef, owner, inst, ov, depth + 1);
        }
        return inst;
    }

    static object? DefaultFor(string type) => ParamTypes.Empty(type);

    public GameObject? FindObject(string nameOrTag)
    {
        foreach (var o in _objects) if (!o.Destroyed && o.Is(nameOrTag)) return o;
        foreach (var o in _pendingAdd) if (!o.Destroyed && o.Is(nameOrTag)) return o; // spawned this step (and not destroyed already)
        return null;
    }

    public void PressKey(string key)
    {
        key = key.ToLowerInvariant();
        _keyPressed[key] = ++_keySeq; // for 'wait for key'
        Debug?.Event(DebugEventKind.Key, null, "key " + key);
        for (int i = 0, n = _objects.Count; i < n && i < _objects.Count; i++)
            foreach (var m in _objects[i].Flat)
                foreach (var w in m.Ix.Key)
                    if (w.Event.Filter == key) StartWhen(m, w, null);
    }

    /// <summary>Advances the game by dt seconds: new objects start, ticks run, touches fire, waits resume.</summary>
    public void Step(double dt)
    {
        _burst = 0; // lines run outside a 'when' (values in the HUD) count from here
        // 1. Newly spawned objects join and run 'when start'.
        while (_pendingAdd.Count > 0)
        {
            var batch = _pendingAdd.ToArray();
            _pendingAdd.Clear();
            _objects.AddRange(batch);
            foreach (var o in batch)
            {
                ObjectJoined(o); // game kit caches (World.Broadphase.cs)
                foreach (var m in o.Flat)
                    foreach (var w in m.Ix.Start) StartWhen(m, w, null);
            }
        }

        Time += dt;
        LastDt = dt;

        // 2. 'when tick' runs every step (a tick still busy with a 'wait' isn't started again).
        for (int i = 0, n = _objects.Count; i < n && i < _objects.Count; i++)
            foreach (var m in _objects[i].Flat)
            {
                var ticks = m.Ix.Tick;
                for (int k = 0; k < ticks.Length; k++)
                    if (!m.IsTickBusy(ticks[k])) StartWhen(m, ticks[k], null);
            }

        // 2b. Physics: objects move by their speed; bodies fall and stop at solids and solid tiles (World.Physics.cs).
        StepPhysics(dt);

        // 3. Touches: fire once when two objects start overlapping (or, with a solid, start resting against it).
        //    A grid broadphase finds the pairs (World.Broadphase.cs).
        StepTouches();

        // 4. Resume coroutines whose wait is over.
        RunReady();

        // 5. Destroyed objects leave.
        _objects.RemoveAll(o => o.Destroyed);
        _running.RemoveAll(c => c.Inst.Owner.Destroyed);
        _signalWaiters.RemoveAll(w => w.Fired || w.Waiting.Destroyed);
    }

    void FireTouch(GameObject self, GameObject other)
    {
        if (self.Destroyed || other.Destroyed) return;
        foreach (var m in self.Flat)
            foreach (var w in m.Ix.Touched)
            {
                if (w.Event.Filter != null && !other.Is(w.Event.Filter)) continue;
                StartWhen(m, w, w.Event.Bind == null ? null : new() { [w.Event.Bind] = other }, other);
            }
    }

    internal void Emit(MechanismInstance from, string signal, List<object?> args)
    {
        Debug?.Event(DebugEventKind.Emit, from.Owner, $"{from.Def.Name}: emit {signal}" + DebugArgs(args));
        NotifySignalWaiters(from, signal);
        foreach (var m in from.Owner.Flat)
            foreach (var w in m.Def.Whens)
            {
                if (w.Event.Kind != EventKind.Signal || w.Event.Name != signal) continue;
                bool own = w.Event.Source == null && m == from;
                bool other = w.Event.Source != null && string.Equals(w.Event.Source, from.Def.Name, StringComparison.OrdinalIgnoreCase);
                if (own || other) StartWhen(m, w, null);
            }
    }

    public void Tell(GameObject target, string message, List<object?> args)
    {
        if (target.Destroyed) return;
        Debug?.Event(DebugEventKind.Told, target, $"{target.Name} told {Printer.Quote(message)}" + DebugArgs(args));
        foreach (var m in target.Flat)
            foreach (var w in m.Ix.Told)
                if (w.Event.Filter == message)
                    StartWhen(m, w, w.Event.Bind == null ? null : new() { [w.Event.Bind] = args.Count > 0 ? args[0] : null });
    }

    /// <summary>
    /// How many 'when's can be started inside each other in one go (a 'when told' that tells, an 'emit' whose handler
    /// emits…). More is a loop that never waits: it is stopped with an error instead of overflowing the stack and
    /// closing the program.
    /// </summary>
    public const int MaxEventNesting = 100;

    /// <summary>
    /// How many lines one 'when' (with what it starts at once) may run without a 'wait' before it counts as a runaway
    /// loop (<c>repeat 1000000000</c>). About 2–5 seconds of work; a 'while' or 'repeat forever' already pauses each round.
    /// </summary>
    public int MaxLinesWithoutWait { get; set; } = 10_000_000;

    int _advancing; // Advance calls inside each other (a 'when' that started another right away)
    int _burst;     // lines run since the outermost Advance (or the step) began

    void StartWhen(MechanismInstance m, WhenDef w, Dictionary<string, object?>? locals, GameObject? other = null)
    {
        if (m.Owner.Destroyed) return;
        if (_advancing >= MaxEventNesting)
            throw new ScriptError($"'{Printer.WhenHead(w.Event)}' was started inside itself more than {MaxEventNesting} times at once " +
                "(something tells or emits it again before any 'wait'). Add a 'wait' or an 'if' that stops it.", w.Line);
        var ctx = new Ctx(this, m) { Other = other, Source = w.From };
        if (locals != null) ctx.Locals = locals;
        var co = new Coroutine(m, w, Exec(w.Body, ctx).GetEnumerator());
        if (Debug != null) co.State = ctx.State = new CoState { Line = w.Line, Source = w.From };
        // Runs right away until its first 'wait' (so effects happen in the same step).
        Advance(co);
        if (co.Done) return;
        _running.Add(co);
        if (w.Event.Kind == EventKind.Tick) m.MarkTickBusy(w, true); // cleared when it ends (Coroutine.Done)
    }

    void RunReady()
    {
        // New ones (started by 'run' or a tell) are added at the end and wait for the next step, as before.
        for (int i = 0, n = _running.Count; i < n; i++)
        {
            var co = _running[i];
            if (!co.Done && co.WakeAt <= Time + 1e-9 && (co.Until == null || SafeUntil(co))) Advance(co);
        }
        _running.RemoveAll(c => c.Done);
    }

    void Advance(Coroutine co)
    {
        if (_advancing == 0) _burst = 0;
        _advancing++;
        try
        {
            if (co.Inst.Owner.Destroyed || !co.It.MoveNext()) { co.Done = true; return; }
            co.WakeAt = Time + Math.Max(0, co.It.Current.Seconds);
            co.Until = co.It.Current.Until;
        }
        catch (StopException) { co.Done = true; }
        catch (ScriptError e) { CoroutineError(co, e); }
        finally { _advancing--; }
    }

    /// <summary>Counts one line run; too many without a 'wait' is a runaway loop (an error, not a frozen editor).</summary>
    void CountLine(int line)
    {
        if (++_burst > MaxLinesWithoutWait)
            throw new ScriptError($"This ran more than {MaxLinesWithoutWait:N0} lines without waiting — a loop that never ends? " +
                "Put a 'wait' inside long loops (or use 'while' / 'repeat forever', which go one round per frame).", line);
    }

    /// <summary>A value or command inside itself so deep that the stack would run out: an error instead of a crash.</summary>
    static void CheckStack(int line)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new ScriptError("This goes too deep: a value or a command inside itself too many times (a very long formula, or one that never stops calling itself).", line);
    }

    /// <summary>A condition that throws (a script error inside 'wait until') ends that coroutine with the error.</summary>
    bool SafeUntil(Coroutine co)
    {
        try { return co.Until!(); }
        catch (ScriptError e)
        {
            CoroutineError(co, e);
            return false;
        }
    }

    /// <summary>A script error ends its 'when': it goes to Errors, the console and the debugger (with whose code it was).</summary>
    void CoroutineError(Coroutine co, ScriptError e)
    {
        co.Done = true;
        string? source = co.State?.Source ?? co.When?.From;
        string where = source != null && !string.Equals(source, co.Inst.Def.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{co.Inst.Def.Name} (from {source}, line {e.Line})" : $"{co.Inst.Def.Name} (line {e.Line})";
        string msg = $"{where}: {e.Message}";
        // The same error again (a broken 'when tick' fails every frame): counted, but printed once — 60 lines a second
        // would bury the console and slow the editor down. The kept text is reused, so repeats cost almost no memory.
        bool again = _errorTexts.TryGetValue(msg, out var known);
        if (again) msg = known!;
        else _errorTexts.Add(msg);
        Errors.Add(msg);
        if (!again) Printed?.Invoke("⚠ " + msg);
        Debug?.Event(DebugEventKind.Error, co.Inst.Owner, msg, null, source ?? co.Inst.Def.Name, e.Line);
    }

    readonly HashSet<string> _errorTexts = [];

    /// <summary>A running 'when' (When set) or a background task started with 'run' (When null).</summary>
    sealed class Coroutine(MechanismInstance inst, WhenDef? when, IEnumerator<WaitFor> it)
    {
        public readonly MechanismInstance Inst = inst;
        public readonly WhenDef? When = when;
        public readonly IEnumerator<WaitFor> It = it;
        public double WakeAt;
        public Func<bool>? Until;
        bool _done;
        /// <summary>Finished; a 'when tick' that ends can be started again next step.</summary>
        public bool Done
        {
            get => _done;
            set
            {
                if (value && !_done && When?.Event.Kind == EventKind.Tick) Inst.MarkTickBusy(When, false);
                _done = value;
            }
        }
        /// <summary>Where it is (only while a debugger is attached).</summary>
        public CoState? State;
    }

    sealed class StopException : Exception;

    internal sealed class Ctx(World world, MechanismInstance inst)
    {
        public readonly World World = world;
        public readonly MechanismInstance Inst = inst;
        public Dictionary<string, object?> Locals = new();
        public GameObject? Other;
        /// <summary>Running a user command ('to'): new names become locals; 'return' ends it.</summary>
        public bool InProc;
        /// <summary>How many user commands deep this is (to stop runaway recursion).</summary>
        public int Depth;
        public bool Returned;
        public object? ReturnValue;
        /// <summary>The mechanism whose code this is when it isn't Inst's own (an inherited 'when' or command of a variant).</summary>
        public string? Source;
        /// <summary>The running 'when's position, shared with its commands (only while a debugger is attached).</summary>
        public CoState? State;
        /// <summary>Running inside a value (<c>bonus(3)</c>): it can't stop at a breakpoint.</summary>
        public bool NoBreak;
    }

    // ---------------- Statements ----------------

    /// <summary>
    /// Runs statements as a coroutine. Each yielded <see cref="WaitFor"/> pauses this 'when' only:
    /// for some seconds, and/or until a condition is true (checked once per step).
    /// </summary>
    internal IEnumerable<WaitFor> Exec(List<Stmt> body, Ctx ctx)
    {
        foreach (var s in body)
        {
            if (ctx.Inst.Owner.Destroyed || ctx.Returned) yield break;
            CountLine(s.Line);
            // Debugger: where each 'when' is, and breakpoints (nothing but two null checks when none is attached).
            if (ctx.State is { } st) { st.Line = s.Line; st.Stmt = s; st.Source = ctx.Source; }
            if (Debug is { } dbg && dbg.Statement(ctx.Inst, ctx.Source ?? ctx.Inst.Def.Name, s, canBreak: !ctx.NoBreak))
            {
                if (ctx.State is { } bs) bs.AtBreak = true;
                yield return BreakWait(dbg);
                if (ctx.State is { } be) be.AtBreak = false;
            }
            switch (s)
            {
                case WaitStmt w:
                    yield return new WaitFor(ToNumber(Eval(w.Seconds, ctx), w.Line));
                    break;
                case WaitUntilStmt u:
                    // Already true: go on right away. Otherwise check once per step.
                    if (!Truthy(Eval(u.Cond, ctx))) yield return UntilTrue(u.Cond, ctx);
                    break;
                case WaitSignalStmt ws:
                {
                    var waiter = WaitSignal(ws, ctx);
                    yield return UntilFired(waiter);
                    break;
                }
                case WaitKeyStmt wk:
                {
                    string key = ToText(Eval(wk.Key, ctx)).ToLowerInvariant();
                    long since = _keySeq;
                    yield return UntilKey(key, since);
                    break;
                }
                case ReturnStmt ret:
                    ctx.ReturnValue = ret.Value == null ? null : Eval(ret.Value, ctx);
                    ctx.Returned = true;
                    yield break;
                case RunStmt run:
                    StartBackground(run.Body, ctx);
                    break;
                case TogetherStmt tg:
                    foreach (var y in Together(tg, ctx)) yield return y;
                    break;
                case WhileStmt wh:
                    while (!ctx.Inst.Owner.Destroyed && !ctx.Returned && Truthy(Eval(wh.Cond, ctx)))
                    {
                        bool waited = false;
                        foreach (var y in Exec(wh.Body, ctx)) { waited = true; yield return y; }
                        if (!waited && !ctx.Returned) yield return new WaitFor(0); // like repeat forever: one round per frame
                    }
                    break;
                case ForEachStmt fe:
                {
                    var items = Items(Eval(fe.List, ctx), fe.Line, "for each");
                    foreach (var item in items)
                    {
                        if (ctx.Returned || ctx.Inst.Owner.Destroyed) break;
                        if (item is GameObject { Destroyed: true }) continue;
                        ctx.Locals[fe.Var] = item;
                        foreach (var y in Exec(fe.Body, ctx)) yield return y;
                    }
                    break;
                }
                case CommandStmt cmd when FindProc(cmd.Name, ctx, cmd.Line, statement: true) is { } proc:
                {
                    if (cmd.Named != null)
                        throw new ScriptError($"'{cmd.Name}' is a command you made: give its values separated by commas ({ProcUsage(proc.Def)}).", cmd.Line);
                    var args = cmd.Args.Select(x => Eval(x, ctx)).ToList();
                    var pctx = ProcContext(proc, args, ctx, cmd.Line);
                    foreach (var y in Exec(proc.Def.Body, pctx)) yield return y;
                    break;
                }
                case StopStmt:
                    throw new StopException();
                case IfStmt i:
                {
                    var branch = Truthy(Eval(i.Cond, ctx)) ? i.Then : i.Else;
                    if (branch != null)
                        foreach (var y in Exec(branch, ctx)) yield return y;
                    break;
                }
                case RepeatStmt r:
                {
                    if (r.Count == null)
                    {
                        while (!ctx.Inst.Owner.Destroyed && !ctx.Returned)
                        {
                            bool waited = false;
                            foreach (var y in Exec(r.Body, ctx)) { waited = true; yield return y; }
                            if (!waited && !ctx.Returned) yield return new WaitFor(0); // a forever loop with no wait runs once per frame
                        }
                    }
                    else
                    {
                        int n = (int)Math.Round(ToNumber(Eval(r.Count, ctx), r.Line));
                        for (int k = 0; k < n && !ctx.Returned; k++)
                            foreach (var y in Exec(r.Body, ctx)) yield return y;
                    }
                    break;
                }
                default:
                    if (ExecSimple(s, ctx) is { } wait) yield return wait;
                    break;
            }
        }
    }

    // The waits Exec yields, made here: a lambda inside Exec would capture its locals in an object made on every run of
    // every block (a few hundred bytes per frame per object), even when no wait is ever reached.
    static WaitFor BreakWait(IDebugHooks dbg) => new(0, () => !dbg.HoldBreak);
    WaitFor UntilTrue(Expr cond, Ctx ctx) => new(0, () => Truthy(Eval(cond, ctx)));
    static WaitFor UntilFired(SignalWaiter waiter) => new(0, () => waiter.Fired);
    WaitFor UntilKey(string key, long since) => new(0, () => _keyPressed.TryGetValue(key, out var seq) && seq > since);

    /// <summary>Runs a one-line statement; a command that takes time returns what to wait for.</summary>
    WaitFor? ExecSimple(Stmt s, Ctx ctx)
    {
        if (s is CommandStmt cmd) return RunCommand(cmd, ctx);
        switch (s)
        {
            case AssignStmt a: Assign(a, ctx); break;
            case EmitStmt e: Emit(ctx.Inst, e.Signal, e.Args.Select(x => Eval(x, ctx)).ToList()); break;
            case TellStmt t:
            {
                var target = Eval(t.Target, ctx) switch
                {
                    GameObject g => g,
                    string name => FindObject(name),
                    var other => throw new ScriptError($"'tell' needs an object, but got {Describe(other)}.", t.Line)
                };
                if (target != null) Tell(target, ToText(Eval(t.Message, ctx)), t.Args.Select(x => Eval(x, ctx)).ToList());
                break;
            }
            default: throw new ScriptError("This line can't run here.", s.Line);
        }
        return null;
    }

    void Assign(AssignStmt a, Ctx ctx)
    {
        object? value = Eval(a.Value, ctx);
        if (a.Target.Length == 1)
        {
            string n = a.Target[0];
            // Inside a user command a new name is a local (gone when the command ends).
            Dictionary<string, object?> bag =
                ctx.Locals.ContainsKey(n) ? ctx.Locals :
                ctx.Inst.Vars.ContainsKey(n) ? ctx.Inst.Vars :
                Globals.ContainsKey(n) ? Globals : ctx.InProc ? ctx.Locals : ctx.Inst.Vars;
            bag.TryGetValue(n, out var old);
            bag[n] = Combine(a.Op, old, value, a.Line);
            return;
        }
        var holder = Resolve(a.Target, a.Target.Length - 1, ctx, a.Line); // all but the last name (no array copy per line)
        string member = a.Target[^1];
        switch (holder)
        {
            case GameObject g when SetGameMember(g, member, a.Op, value, a.Line): // hidden, color, text… (World.Screens.cs)
                break;
            case GameObject g:
            {
                double cur = member switch { "x" => g.X, "y" => g.Y, "width" => g.Width, "height" => g.Height, "vx" => g.Vx, "vy" => g.Vy, _ => throw new ScriptError($"Objects have x, y, width, height, vx and vy you can change — not '{member}'.", a.Line) };
                double nv = ToNumber(Combine(a.Op, cur, value, a.Line), a.Line);
                // NaN or infinity (sqrt(-1), a huge product) would make the object vanish for good and slow physics down.
                if (!double.IsFinite(nv))
                    throw new ScriptError($"{g.Name}.{member} can't be {ToText(nv)} — that isn't a number the game can use (sqrt of a negative number? a number too big?).", a.Line);
                switch (member) { case "x": g.X = nv; break; case "y": g.Y = nv; break; case "width": g.Width = nv; break; case "vx": g.Vx = nv; break; case "vy": g.Vy = nv; break; default: g.Height = nv; break; }
                break;
            }
            case MechanismInstance m:
                m.Vars.TryGetValue(member, out var o);
                m.Vars[member] = Combine(a.Op, o, value, a.Line);
                break;
            case Record r: // card.health -= 2 (World.Records.cs)
                r.TryGetValue(member, out var rv);
                r[member] = Combine(a.Op, rv, value, a.Line);
                break;
            default:
                throw new ScriptError($"Can't change '{member}' of {Describe(holder)}.", a.Line);
        }
    }

    static object? Combine(string op, object? old, object? value, int line) => op switch
    {
        "=" => value,
        "+=" => old is string || value is string ? ToText(old) + ToText(value) : Box(ToNumber(old, line) + ToNumber(value, line)),
        "-=" => Box(ToNumber(old, line) - ToNumber(value, line)),
        "*=" => ToNumber(old, line) * ToNumber(value, line),
        "/=" => Divide(ToNumber(old, line), ToNumber(value, line), line),
        _ => throw new ScriptError($"Unknown operator '{op}'.", line)
    };

    static double Divide(double a, double b, int line) =>
        b == 0 ? throw new ScriptError("Division by zero.", line) : a / b;

    // ---------------- Expressions ----------------

    internal object? Eval(Expr e, Ctx ctx)
    {
        switch (e)
        {
            case NumberExpr n: return Box(n.Value);
            case StringExpr s: return s.Value;
            case BoolExpr b: return Box(b.Value);
            case NameExpr n: return Resolve(n.Path, n.Path.Length, ctx, n.Line);
        }
        CheckStack(e.Line); // only values made of other values can nest (a formula thousands of terms long)
        switch (e)
        {
            case UnaryExpr u:
            {
                var v = Eval(u.Operand, ctx);
                return u.Op == "not" ? Box(!Truthy(v)) : -ToNumber(v, u.Line);
            }
            case BinaryExpr b:
            {
                if (b.Op == "and") return Box(Truthy(Eval(b.Left, ctx)) && Truthy(Eval(b.Right, ctx)));
                if (b.Op == "or") return Box(Truthy(Eval(b.Left, ctx)) || Truthy(Eval(b.Right, ctx)));
                var l = Eval(b.Left, ctx);
                var r = Eval(b.Right, ctx);
                switch (b.Op)
                {
                    case "+": return l is string || r is string ? ToText(l) + ToText(r) : Box(ToNumber(l, b.Line) + ToNumber(r, b.Line));
                    case "-": return Box(ToNumber(l, b.Line) - ToNumber(r, b.Line));
                    case "*": return Box(ToNumber(l, b.Line) * ToNumber(r, b.Line));
                    case "/": return Divide(ToNumber(l, b.Line), ToNumber(r, b.Line), b.Line);
                    case "%": { double d = ToNumber(r, b.Line); return d == 0 ? throw new ScriptError("Division by zero.", b.Line) : ToNumber(l, b.Line) % d; }
                    case "==": return Box(AreEqual(l, r));
                    case "!=": return Box(!AreEqual(l, r));
                    case "<": return Box(ToNumber(l, b.Line) < ToNumber(r, b.Line));
                    case ">": return Box(ToNumber(l, b.Line) > ToNumber(r, b.Line));
                    case "<=": return Box(ToNumber(l, b.Line) <= ToNumber(r, b.Line));
                    case ">=": return Box(ToNumber(l, b.Line) >= ToNumber(r, b.Line));
                }
                throw new ScriptError($"Unknown operator '{b.Op}'.", b.Line);
            }
            case CallExpr c: return CallFunction(c, ctx);
            case ListExpr l: return l.Items.Select(x => Eval(x, ctx)).ToList();
            case IndexExpr x: return Index(Eval(x.Target, ctx), Eval(x.Index, ctx), x.Line);
            case RecordExpr r: return Record.Make(r, x => Eval(x, ctx)); // World.Records.cs
            case MemberExpr me: return Member(Eval(me.Target, ctx), me.Name, me.Line); // find("Turns").TurnManager.turn
        }
        throw new ScriptError("I can't work out this value.", e.Line);
    }

    /// <summary>The game's dice ('random', 'random_item', 'shuffle'); 'random_seed 5' makes them repeat (World.Tiles.cs).</summary>
    internal Random Rng = new();


    /// <summary>The value of the first <paramref name="count"/> names of a dotted path (<c>Boss.Health.hp</c>).</summary>
    object? Resolve(string[] path, int count, Ctx ctx, int line)
    {
        object? cur = ResolveFirst(path[0], ctx, line);
        for (int i = 1; i < count; i++) cur = Member(cur, path[i], line);
        return cur;
    }

    object? Resolve(string[] path, Ctx ctx, int line) => Resolve(path, path.Length, ctx, line);

    static readonly object True = true, False = false;
    /// <summary>true / false as a value, without a new box each time (comparisons run every frame).</summary>
    static object Box(bool b) => b ? True : False;

    // Whole numbers from -128 to 1024 (counters, scores, literals) as values made once: "n += 1" every frame on
    // thousands of objects doesn't make a new box each time. Other numbers are boxed as usual.
    static readonly object[] SmallNumbers = Enumerable.Range(-128, 1024 + 129).Select(i => (object)(double)i).ToArray();
    static object Box(double d) => d >= -128 && d <= 1024 && d == Math.Floor(d) ? SmallNumbers[(int)d + 128] : d;

    static MechanismInstance? MechanismNamed(GameObject g, string name, StringComparison how)
    {
        if (g.FlatIfMade is { } flat)
        {
            foreach (var m in flat)
                if (string.Equals(m.Def.Name, name, how)) return m;
            return null;
        }
        // Still being made (a param's default value is worked out): its list isn't complete, so it isn't cached yet.
        foreach (var m in g.AllMechanisms())
            if (string.Equals(m.Def.Name, name, how)) return m;
        return null;
    }

    /// <summary>One step of a dotted name: <c>Boss.x</c>, <c>other.Health</c>, <c>Health.hp</c>, <c>card.cost</c>.</summary>
    object? Member(object? cur, string m, int line) => cur switch
    {
        GameObject g => m switch
        {
            "x" => g.X, "y" => g.Y, "width" => g.Width, "height" => g.Height, "name" => g.Name,
            "vx" => g.Vx, "vy" => g.Vy, "on_ground" => Box(g.OnGround),
            _ => (object?)MechanismNamed(g, m, StringComparison.OrdinalIgnoreCase)
                 ?? (TryGameMember(g, m, out var gm) ? gm : throw new ScriptError($"'{g.Name}' has no '{m}'.", line)) // World.Screens.cs
        },
        MechanismInstance inst => inst.Vars.TryGetValue(m, out var v) ? v
            : inst.Children.TryGetValue(m, out var ch) ? ch : null,
        Record r => r.GetValueOrDefault(m), // card.cost (World.Records.cs); a missing field reads as nothing
        _ => throw new ScriptError($"{Describe(cur)} has no '{m}'.", line)
    };

    object? ResolveFirst(string n, Ctx ctx, int line)
    {
        // 'none' / 'nothing' is the empty value ('best = none', like 'look none'): always nothing, even if something
        // somewhere sets a variable with that name (it used to be just an unset variable that happened to read as nothing).
        if (n is "none" or "nothing") return null;
        if (ctx.Locals.TryGetValue(n, out var v)) return v;
        switch (n)
        {
            case "self": case "here": return ctx.Inst.Owner;
            case "other": if (ctx.Other != null) return ctx.Other; break;
            case "time": return Time;
            case "dt": return LastDt;
        }
        for (var m = ctx.Inst; m != null; m = m.Parent)
        {
            if (m.Vars.TryGetValue(n, out v)) return v;
            if (m.Children.TryGetValue(n, out var ch)) return ch;
            if (m != ctx.Inst) break; // a child sees its own names, a parent's children only through the parent
        }
        if (MechanismNamed(ctx.Inst.Owner, n, StringComparison.Ordinal) is { } other) return other;
        if (Globals.TryGetValue(n, out v)) return v;
        if (EngineValues.TryGetValue(n, out var ev)) return ev(this); // mouse_x, mouse_y… (World.Mouse.cs)
        if (Library.ContainsKey(n)) return n; // a mechanism name, e.g. 'spawn Coin'
        if (Prefabs.ContainsKey(n)) return n; // a prefab name, e.g. 'spawn Slime' (World.Prefabs.cs)
        return null; // an unset variable reads as nothing (0 / false / empty text)
    }

    // ---------------- Commands ----------------

    WaitFor? RunCommand(CommandStmt c, Ctx ctx)
    {
        // A registered command with several words wins: "camera follow Boss" → "camera follow" with [Boss].
        var (name, skip) = MatchCommandPhrase(c);
        if (name != null)
        {
            var cc = new CommandContext(this, ctx.Inst, c.Args.Skip(skip).Select(x => Eval(x, ctx)).ToList(), c.Line)
            {
                Raw = c.Args.Skip(skip).ToList(),
                Named = c.Named?.ToDictionary(kv => kv.Key, kv => kv.Value.Select(x => Eval(x, ctx)).ToList()) ?? new(),
                RawNamed = c.Named ?? new(),
                Other = ctx.Other,
                Locals = ctx.Locals,
            };
            try { Commands[name](cc); }
            catch (ScriptError e) when (e.Line == 0) { throw new ScriptError(e.Message, c.Line); } // a helper that didn't know the line
            return cc.Pending;
        }
        RunBuiltInCommand(c, ctx);
        return null;
    }

    (string? Name, int Skip) MatchCommandPhrase(CommandStmt c)
    {
        string? best = Commands.ContainsKey(c.Name) ? c.Name : null;
        int bestSkip = 0;
        string phrase = c.Name;
        for (int i = 0; i < c.Args.Count && c.Args[i] is NameExpr { Path.Length: 1 } n; i++)
        {
            phrase += " " + n.Path[0];
            if (Commands.ContainsKey(phrase)) { best = phrase; bestSkip = i + 1; }
        }
        return (best, bestSkip);
    }

    void RunBuiltInCommand(CommandStmt c, Ctx ctx)
    {
        var args = c.Args.Select(x => Eval(x, ctx)).ToList();
        switch (c.Name)
        {
            case "print":
            {
                string text = string.Join(" ", args.Select(ToText));
                AddOutput(text);
                Printed?.Invoke(text);
                Debug?.Event(DebugEventKind.Print, ctx.Inst.Owner, text, null, ctx.Source ?? ctx.Inst.Def.Name, c.Line);
                return;
            }
            case "destroy":
            {
                var target = args.Count > 0 ? args[0] as GameObject : ctx.Inst.Owner;
                if (target != null) target.Destroyed = true;
                return;
            }
            case "spawn":
            {
                if (args.Count == 0) throw new ScriptError("'spawn' needs a prefab or a mechanism, like 'spawn Coin here'.", c.Line);
                string name = ToText(args[0]);
                double x = ctx.Inst.Owner.X, y = ctx.Inst.Owner.Y;
                if (args.Count >= 2 && args[1] is GameObject at) { x = at.X; y = at.Y; }
                else if (args.Count >= 3) { x = ToNumber(args[1], c.Line); y = ToNumber(args[2], c.Line); }
                if (SpawnPrefab(name, x, y, c.Line) != null) return; // a prefab comes first (World.Prefabs.cs)
                if (Prefabs.Count > 0 && TryDef(name, c.Line) == null)
                    throw new ScriptError($"There's no prefab or mechanism called '{name}'.{Suggest(name, Prefabs.Keys.Concat(Library.Keys))}", c.Line);
                Find(name, c.Line);
                Spawn(name, x, y, [name]);
                return;
            }
            case "play":
            {
                string sound = args.Count > 0 ? ToText(args[0]) : "";
                AddOutput("♪ " + sound);
                SoundPlayed?.Invoke(sound);
                return;
            }
            case "move":
            {
                if (args.Count < 2) throw new ScriptError("'move' needs two numbers: 'move 10, 0'.", c.Line);
                ctx.Inst.Owner.X += ToNumber(args[0], c.Line);
                ctx.Inst.Owner.Y += ToNumber(args[1], c.Line);
                return;
            }
            case "goto":
            {
                if (args.Count == 1 && args[0] is GameObject g) { ctx.Inst.Owner.X = g.X; ctx.Inst.Owner.Y = g.Y; return; }
                if (args.Count < 2) throw new ScriptError("'goto' needs x and y, or an object.", c.Line);
                ctx.Inst.Owner.X = ToNumber(args[0], c.Line);
                ctx.Inst.Owner.Y = ToNumber(args[1], c.Line);
                return;
            }
        }
        if (RunPhysicsCommand(c.Name, ctx.Inst.Owner, args, c.Line)) return;
        if (RunListCommand(c, args, ctx)) return;
        throw new ScriptError($"There's no command called '{c.Name}'.{Suggest(c.Name, BuiltInCommands.Concat(Commands.Keys).Concat(VisibleProcNames(ctx)))}", c.Line);
    }

    public static readonly string[] BuiltInCommands = ["print", "destroy", "spawn", "play", "move", "goto", "body", "solid", "oneway", "jump", "push", "add", "remove"];

    // ---------------- Values ----------------

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0,
        List<object?> l => l.Count > 0,
        _ => true
    };

    public static double ToNumber(object? v, int line) => v switch
    {
        null => 0,
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new ScriptError($"Expected a number but got {Describe(v)}.", line)
    };

    public static string ToText(object? v) => v switch
    {
        null => "",
        double d => (d == 0 ? 0.0 : d).ToString(CultureInfo.InvariantCulture), // -0 (0 * -1) reads as 0
        bool b => b ? "true" : "false",
        List<object?> or Record => NestedText(v),
        _ => v.ToString() ?? ""
    };

    /// <summary>A number the way texts show it ("Score: {score}", the HUD): at most 2 decimals, never "-0".</summary>
    public static string ShortNumber(double d) => (Math.Round(d, 2) == 0 ? 0.0 : d).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// How deep lists and records inside each other are shown or compared. A list that contains itself
    /// (<c>add hand to hand</c>, a card whose owner holds the card) shows as <c>[…]</c> instead of never ending.
    /// </summary>
    public const int MaxValueDepth = 64;

    static string NestedText(object v)
    {
        var sb = new StringBuilder();
        AppendNested(sb, v, []);
        return sb.ToString();
    }

    static void AppendNested(StringBuilder sb, object? v, List<object> path)
    {
        if (v is not (List<object?> or Record))
        {
            sb.Append(v is string s ? Printer.Quote(s) : ToText(v));
            return;
        }
        if (path.Count >= MaxValueDepth || path.Exists(p => ReferenceEquals(p, v))) { sb.Append(v is Record ? "{…}" : "[…]"); return; }
        path.Add(v);
        if (v is List<object?> l)
        {
            sb.Append('[');
            for (int i = 0; i < l.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendNested(sb, l[i], path);
            }
            sb.Append(']');
        }
        else
        {
            sb.Append('{');
            bool first = true;
            foreach (var (k, x) in (Record)v)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(Record.Key(k)).Append(": ");
                AppendNested(sb, x, path);
            }
            sb.Append('}');
        }
        path.RemoveAt(path.Count - 1);
    }

    static bool AreEqual(object? a, object? b) => AreEqual(a, b, 0);

    static bool AreEqual(object? a, object? b, int depth)
    {
        if (a is List<object?> la && b is List<object?> lb)
        {
            if (ReferenceEquals(la, lb)) return true;
            if (la.Count != lb.Count || depth >= MaxValueDepth) return false; // lists inside themselves: only the same list is equal
            for (int i = 0; i < la.Count; i++)
                if (!AreEqual(la[i], lb[i], depth + 1)) return false;
            return true;
        }
        if (a is double || b is double) return TryNumber(a, out var x) && TryNumber(b, out var y) && x == y; // "1" == 1
        if (a is GameObject ga && b is string sb) return ga.Is(sb);
        // Nothing (an unset variable) equals what it reads as: "", 0 (above) and false.
        if (a == null) return b == null || Equals(b, "") || Equals(b, false);
        if (b == null) return Equals(a, "") || Equals(a, false);
        return Equals(a, b);
    }

    /// <summary>Like <see cref="ToNumber"/> without the error (comparisons with a text that isn't a number are just false).</summary>
    static bool TryNumber(object? v, out double d)
    {
        switch (v)
        {
            case null: d = 0; return true;
            case double n: d = n; return true;
            case bool b: d = b ? 1 : 0; return true;
            case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            default: d = 0; return false;
        }
    }

    static string Describe(object? v) => v switch
    {
        null => "nothing",
        string s => $"the text \"{s}\"",
        double d => $"the number {ToText(d)}",
        bool b => b ? "true" : "false",
        GameObject g => $"the object '{g.Name}'",
        MechanismInstance m => $"the mechanism '{m.Def.Name}'",
        List<object?> l => l.Count == 0 ? "an empty list" : $"a list of {l.Count} item(s)",
        _ => v.ToString() ?? "?"
    };

    /// <summary>" Did you mean 'x'?" when a known name is close to the typed one.</summary>
    public static string Suggest(string typed, IEnumerable<string> known)
    {
        string? best = null;
        int bestD = int.MaxValue;
        foreach (var k in known)
        {
            int d = Distance(typed.ToLowerInvariant(), k.ToLowerInvariant());
            if (d < bestD) { bestD = d; best = k; }
        }
        return best != null && bestD <= Math.Max(1, typed.Length / 3) ? $" Did you mean '{best}'?" : "";
    }

    static int Distance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return dp[a.Length, b.Length];
    }
}

/// <summary>Pauses a running 'when': for Seconds, then (if set) until Until() is true.</summary>
public readonly record struct WaitFor(double Seconds, Func<bool>? Until = null);

/// <summary>
/// What a command receives: the world, the mechanism running it, its values (Args = positional, Named = after
/// connector words like <c>to</c>/<c>in</c>), the raw expressions, and the line (for errors).
/// </summary>
public sealed class CommandContext(World world, MechanismInstance mechanism, List<object?> args, int line)
{
    public World World { get; } = world;
    public MechanismInstance Mechanism { get; } = mechanism;
    public GameObject Self => Mechanism.Owner;
    public List<object?> Args { get; } = args;
    public int Line { get; } = line;
    public List<Expr> Raw { get; init; } = [];
    public Dictionary<string, List<object?>> Named { get; init; } = new();
    /// <summary>The named values as written (<c>ease out</c> keeps the word <c>out</c>).</summary>
    public Dictionary<string, List<Expr>> RawNamed { get; init; } = new();
    public GameObject? Other { get; init; }
    public Dictionary<string, object?> Locals { get; init; } = new();

    /// <summary>Set by <see cref="WaitUntil"/>/<see cref="WaitSeconds"/>: the line pauses its 'when' until then.</summary>
    public WaitFor? Pending { get; private set; }
    public void WaitUntil(Func<bool> done) => Pending = new WaitFor(0, done);
    public void WaitSeconds(double s) => Pending = new WaitFor(s);

    public bool Has(string connector) => Named.ContainsKey(connector);
    public object? Arg(int i) => i < Args.Count ? Args[i] : null;
    public object? NamedArg(string connector, int i = 0) => Named.TryGetValue(connector, out var l) && i < l.Count ? l[i] : null;
    public double Number(object? v) => World.ToNumber(v, Line);
    public ScriptError Error(string message) => new(message, Line);
}
