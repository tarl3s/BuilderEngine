namespace BuilderEngine.Script;

public sealed partial class GameObject
{
    /// <summary>
    /// Mechanisms this object doesn't have but whose commands it called (<c>Tools.flash 3</c>): one quiet copy each,
    /// so the command's params have their values. Made the first time a command needs it.
    /// </summary>
    public Dictionary<string, MechanismInstance>? Helpers { get; internal set; }
}

// LANGUAGE FEATURES (Builder Script 2): user commands ('to'), waiting for conditions / signals / keys,
// 'together' and 'run', lists, and the functions that find objects. The statements are run by Exec in Runtime.cs.
public sealed partial class World
{
    /// <summary>How many user commands can run inside each other before it counts as a runaway loop.</summary>
    public const int MaxCommandDepth = 100;

    public static readonly string[] BuiltInFunctions =
    [
        "key", "random", "abs", "round", "floor", "min", "max", "sqrt",
        "length", "contains", "random_item", "text", "join", "range",
        "nearest", "all", "count", "find", "distance", "has_tag"
    ];

    static readonly HashSet<string> BuiltInCommandSet = new(BuiltInCommands, StringComparer.Ordinal);

    // ---------------- Waiting for keys and signals ----------------

    readonly Dictionary<string, long> _keyPressed = new(StringComparer.OrdinalIgnoreCase);
    long _keySeq;
    readonly List<SignalWaiter> _signalWaiters = [];

    /// <summary>A 'wait for' in progress: which object (and maybe which of its mechanisms) must emit which signal.</summary>
    sealed class SignalWaiter(GameObject waiting, GameObject from, MechanismInstance? source, string name)
    {
        public readonly GameObject Waiting = waiting;
        public readonly GameObject From = from;
        public readonly MechanismInstance? Source = source;
        public readonly string Name = name;
        public bool Fired;
    }

    SignalWaiter WaitSignal(WaitSignalStmt ws, Ctx ctx)
    {
        string signal = ws.Path[^1];
        GameObject from = ctx.Inst.Owner;
        MechanismInstance? source = null;
        if (ws.Path.Length > 1)
        {
            string prefix = string.Join('.', ws.Path[..^1]);
            switch (Resolve(ws.Path[..^1], ctx, ws.Line))
            {
                case GameObject g: from = g; break;
                case MechanismInstance m: from = m.Owner; source = m; break;
                case string s when Library.ContainsKey(s):
                    throw new ScriptError($"This object has no '{s}', so it can't wait for {ws.Full}.", ws.Line);
                case null:
                    throw new ScriptError($"I can't find '{prefix}' to wait for its signal '{signal}'.", ws.Line);
                case var other:
                    throw new ScriptError($"'wait for' needs a signal of an object or a mechanism, but '{prefix}' is {Describe(other)}.", ws.Line);
            }
        }
        var w = new SignalWaiter(ctx.Inst.Owner, from, source, signal);
        _signalWaiters.Add(w);
        return w;
    }

    void NotifySignalWaiters(MechanismInstance from, string signal)
    {
        foreach (var w in _signalWaiters)
            if (!w.Fired && w.Name == signal && w.From == from.Owner && (w.Source == null || w.Source == from))
                w.Fired = true;
    }

    // ---------------- together / run ----------------

    /// <summary>One branch of a 'together': its own position in the code and its own wait.</summary>
    sealed class Branch(IEnumerator<WaitFor> it)
    {
        public readonly IEnumerator<WaitFor> It = it;
        public double WakeAt;
        public Func<bool>? Until;
        public bool Done;
    }

    /// <summary>Runs every statement of a 'together' at the same time; ends when the last one has finished.</summary>
    IEnumerable<WaitFor> Together(TogetherStmt tg, Ctx ctx)
    {
        var branches = tg.Body.Select(s => new Branch(Exec([s], ctx).GetEnumerator())).ToList();
        foreach (var b in branches) StepBranch(b);
        while (branches.Any(b => !b.Done) && !ctx.Returned && !ctx.Inst.Owner.Destroyed)
        {
            yield return new WaitFor(0, () => branches.Any(BranchReady));
            foreach (var b in branches)
                if (BranchReady(b)) StepBranch(b);
        }
    }

    bool BranchReady(Branch b) => !b.Done && b.WakeAt <= Time + 1e-9 && (b.Until == null || b.Until());

    void StepBranch(Branch b)
    {
        if (!b.It.MoveNext()) { b.Done = true; return; }
        b.WakeAt = Time + Math.Max(0, b.It.Current.Seconds);
        b.Until = b.It.Current.Until;
    }

    /// <summary>'run': starts the block as its own background task (with a copy of the current locals) and goes on.</summary>
    void StartBackground(List<Stmt> body, Ctx ctx)
    {
        var bctx = new Ctx(this, ctx.Inst) { Locals = new(ctx.Locals), Other = ctx.Other, InProc = ctx.InProc, Depth = ctx.Depth };
        var co = new Coroutine(ctx.Inst, null, Exec(body, bctx).GetEnumerator());
        Advance(co);
        if (!co.Done) _running.Add(co);
    }

    // ---------------- User commands ('to') ----------------

    internal sealed record ProcRef(ProcDef Def, MechanismDef Mech);

    Dictionary<string, List<MechanismDef>>? _procIndex;

    /// <summary>Which mechanisms make a command with this name (every loaded mechanism).</summary>
    List<MechanismDef> ProcOwners(string name)
    {
        if (_procIndex == null)
        {
            _procIndex = new(StringComparer.Ordinal);
            foreach (var d in Library.Values)
                foreach (var p in d.Procs)
                {
                    if (!_procIndex.TryGetValue(p.Name, out var list)) _procIndex[p.Name] = list = [];
                    list.Add(d);
                }
        }
        return _procIndex.TryGetValue(name, out var l) ? l : [];
    }

    static ProcDef? OwnProc(MechanismDef d, string name)
    {
        foreach (var p in d.Procs) if (p.Name == name) return p;
        return null;
    }

    /// <summary>
    /// Finds the user command a line calls: <c>Mech.name</c>; else one of this mechanism; else one of the mechanisms it
    /// uses; else (for a statement, unless an engine command has that name) any loaded mechanism's, if only one has it.
    /// Null when none does. Two candidates at the same level are an error asking for <c>Mechanism.name</c>.
    /// </summary>
    internal ProcRef? FindProc(string name, Ctx ctx, int line, bool statement)
    {
        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            string mech = name[..dot], proc = name[(dot + 1)..];
            if (!Library.TryGetValue(mech, out var md))
                throw new ScriptError($"There's no mechanism called '{mech}'.{Suggest(mech, Library.Keys)}", line);
            return OwnProc(md, proc) is { } pd ? new ProcRef(pd, md)
                : throw new ScriptError($"'{md.Name}' has no command called '{proc}'.{Suggest(proc, md.Procs.Select(p => p.Name))}", line);
        }
        if (statement && BuiltInCommandSet.Contains(name)) return null; // a user command can't take these names
        var own = ctx.Inst.Def;
        if (OwnProc(own, name) is { } mine) return new ProcRef(mine, own);
        ProcRef? found = null;
        foreach (var u in own.Uses)
        {
            if (!Library.TryGetValue(u.Name, out var ud) || OwnProc(ud, name) is not { } up) continue;
            if (found != null && found.Mech != ud)
                throw new ScriptError($"Both '{found.Mech.Name}' and '{ud.Name}' have a command called '{name}': write {found.Mech.Name}.{name} or {ud.Name}.{name}.", line);
            found = new ProcRef(up, ud);
        }
        if (found != null) return found;
        if (statement && IsEngineCommand(name)) return null;
        var owners = ProcOwners(name);
        if (owners.Count == 1) return new ProcRef(OwnProc(owners[0], name)!, owners[0]);
        if (owners.Count > 1)
            throw new ScriptError($"Several mechanisms have a command called '{name}' ({string.Join(", ", owners.Select(o => o.Name))}): write {owners[0].Name}.{name} to pick one, or add 'uses {owners[0].Name}'.", line);
        return null;
    }

    bool IsEngineCommand(string name)
    {
        if (Commands.ContainsKey(name)) return true;
        foreach (var k in Commands.Keys)
            if (k.Length > name.Length && k[name.Length] == ' ' && k.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Every user command a mechanism's code can call by its plain name (for did-you-mean).</summary>
    IEnumerable<string> VisibleProcNames(Ctx ctx)
    {
        var names = new HashSet<string>(ctx.Inst.Def.Procs.Select(p => p.Name));
        foreach (var u in ctx.Inst.Def.Uses)
            if (Library.TryGetValue(u.Name, out var ud)) names.UnionWith(ud.Procs.Select(p => p.Name));
        foreach (var d in Library.Values) names.UnionWith(d.Procs.Select(p => p.Name));
        return names;
    }

    static string ProcUsage(ProcDef p) => p.Params.Count == 0 ? p.Name : p.Name + " " + string.Join(", ", p.Params);

    /// <summary>Where a user command runs: in the mechanism that makes it, on the calling object, with its inputs as locals.</summary>
    Ctx ProcContext(ProcRef proc, List<object?> args, Ctx caller, int line)
    {
        var p = proc.Def;
        if (args.Count != p.Params.Count)
            throw new ScriptError(p.Params.Count == 0
                ? $"'{p.Name}' takes no values, but got {args.Count}."
                : $"'{p.Name}' needs {p.Params.Count} value(s) ({ProcUsage(p)}), but got {args.Count}.", line);
        if (caller.Depth >= MaxCommandDepth)
            throw new ScriptError($"'{p.Name}' was called more than {MaxCommandDepth} times inside itself. Make sure it stops calling itself (an 'if' around the call).", line);
        var owner = caller.Inst.Owner;
        string mech = proc.Mech.Name;
        bool Same(MechanismInstance m) => string.Equals(m.Def.Name, mech, StringComparison.OrdinalIgnoreCase);
        MechanismInstance? inst = Same(caller.Inst) ? caller.Inst : caller.Inst.Children.Values.FirstOrDefault(Same);
        inst ??= owner.AllMechanisms().FirstOrDefault(Same);
        if (inst == null)
        {
            owner.Helpers ??= new(StringComparer.OrdinalIgnoreCase);
            if (!owner.Helpers.TryGetValue(mech, out inst))
                owner.Helpers[mech] = inst = Instantiate(proc.Mech, owner, null, null);
        }
        var locals = new Dictionary<string, object?>();
        for (int i = 0; i < p.Params.Count; i++) locals[p.Params[i]] = args[i];
        return new Ctx(this, inst) { Locals = locals, Other = caller.Other, InProc = true, Depth = caller.Depth + 1 };
    }

    /// <summary>A user command used as a value (<c>bonus(3)</c>): runs it right away and gives what it returns.</summary>
    object? CallProcValue(ProcRef proc, CallExpr c, Ctx ctx)
    {
        var pctx = ProcContext(proc, c.Args.Select(x => Eval(x, ctx)).ToList(), ctx, c.Line);
        foreach (var _ in Exec(proc.Def.Body, pctx))
            throw new ScriptError($"'{c.Name}' waits, so it can't be used inside a value. Put it on its own line: {ProcUsage(proc.Def)}", c.Line);
        return pctx.ReturnValue;
    }

    // ---------------- Functions ----------------

    object? CallFunction(CallExpr c, Ctx ctx)
    {
        int n = c.Args.Count;
        object? A(int i) => Eval(c.Args[i], ctx);
        double N(int i) => ToNumber(A(i), c.Line);
        void Need(int count, string usage)
        {
            if (n != count) throw new ScriptError($"'{c.Name}' needs {count} value(s), like {usage} — got {n}.", c.Line);
        }
        switch (c.Name)
        {
            case "key": Need(1, "key(\"left\")"); return KeysDown.Contains(ToText(A(0)));
            case "random": Need(2, "random(1, 6)"); { double a = N(0), b = N(1); return Math.Floor(a + Rng.NextDouble() * (b - a + 1)); }
            case "abs": Need(1, "abs(x)"); return Math.Abs(N(0));
            case "round": Need(1, "round(x)"); return Math.Round(N(0));
            case "floor": Need(1, "floor(x)"); return Math.Floor(N(0));
            case "min": Need(2, "min(a, b)"); return Math.Min(N(0), N(1));
            case "max": Need(2, "max(a, b)"); return Math.Max(N(0), N(1));
            case "sqrt": Need(1, "sqrt(x)"); return Math.Sqrt(N(0));

            // ---- Lists and text ----
            case "length":
                Need(1, "length(items)");
                return A(0) switch
                {
                    List<object?> l => (double)l.Count,
                    null => 0.0,
                    var v => (double)ToText(v).Length
                };
            case "contains":
            {
                Need(2, "contains(items, \"key\")");
                var where = A(0);
                var what = A(1);
                return where switch
                {
                    List<object?> l => l.Any(x => AreEqual(x, what)),
                    null => false,
                    var v => ToText(v).Contains(ToText(what), StringComparison.OrdinalIgnoreCase)
                };
            }
            case "random_item":
            {
                Need(1, "random_item(items)");
                var l = Items(A(0), c.Line, "random_item");
                return l.Count == 0 ? null : l[Rng.Next(l.Count)];
            }
            case "text": Need(1, "text(score)"); return ToText(A(0));
            case "join":
            {
                if (n is < 1 or > 2) throw new ScriptError($"'join' needs a list and maybe a separator, like join(items, \", \") — got {n} value(s).", c.Line);
                string sep = n == 2 ? ToText(A(1)) : ", ";
                return string.Join(sep, Items(A(0), c.Line, "join").Select(ToText));
            }
            case "range":
            {
                if (n is < 2 or > 3) throw new ScriptError($"'range' needs a first and a last number, like range(1, 10) — got {n} value(s).", c.Line);
                double from = N(0), to = N(1), step = n == 3 ? N(2) : (to >= from ? 1 : -1);
                if (step == 0 || (to - from) / step < 0) return new List<object?>();
                if ((to - from) / step > 100000) throw new ScriptError("That range is too long (more than 100000 numbers).", c.Line);
                var list = new List<object?>();
                for (double x = from; step > 0 ? x <= to + 1e-9 : x >= to - 1e-9; x += step) list.Add(x);
                return list;
            }

            // ---- Finding objects ----
            case "nearest":
            {
                Need(1, "nearest(\"enemy\")");
                string tag = ToText(A(0));
                var self = ctx.Inst.Owner;
                GameObject? best = null;
                double bestD = double.MaxValue;
                foreach (var o in LiveObjects())
                {
                    if (o == self || !o.Is(tag)) continue;
                    double d = CenterDistance(self, o);
                    if (d < bestD) { bestD = d; best = o; }
                }
                return best;
            }
            case "all":
            {
                Need(1, "all(\"coin\")");
                string tag = ToText(A(0));
                return LiveObjects().Where(o => o.Is(tag)).Cast<object?>().ToList();
            }
            case "count":
            {
                Need(1, "count(\"enemy\")");
                string tag = ToText(A(0));
                return (double)LiveObjects().Count(o => o.Is(tag));
            }
            case "find": Need(1, "find(\"Boss\")"); return FindObject(ToText(A(0)));
            case "distance":
            {
                if (n == 4) { double dx = N(2) - N(0), dy = N(3) - N(1); return Math.Sqrt(dx * dx + dy * dy); }
                Need(2, "distance(self, nearest(\"enemy\"))");
                var a = A(0);
                var b = A(1);
                if (a is GameObject ga && b is GameObject gb) return CenterDistance(ga, gb);
                var bad = a is GameObject ? b : a;
                throw new ScriptError($"'distance' needs two objects, but got {Describe(bad)}" + (bad == null ? " (maybe nearest(...) found none)." : "."), c.Line);
            }
            case "has_tag":
            {
                Need(2, "has_tag(other, \"enemy\")");
                return A(0) is GameObject g && g.Tags.Contains(ToText(A(1)));
            }
        }
        if (FindProc(c.Name, ctx, c.Line, statement: false) is { } proc) return CallProcValue(proc, c, ctx);
        throw new ScriptError($"There's no function called '{c.Name}'.{Suggest(c.Name, BuiltInFunctions.Concat(VisibleProcNames(ctx)))}", c.Line);
    }

    IEnumerable<GameObject> LiveObjects()
    {
        foreach (var o in _objects) if (!o.Destroyed) yield return o;
        foreach (var o in _pendingAdd) if (!o.Destroyed) yield return o;
    }

    static double CenterDistance(GameObject a, GameObject b)
    {
        double dx = (b.X + b.Width / 2) - (a.X + a.Width / 2), dy = (b.Y + b.Height / 2) - (a.Y + a.Height / 2);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ---------------- Lists ----------------

    /// <summary>The items to go through: a copy of a list (so changing it inside the loop is safe), or a text's letters.</summary>
    static List<object?> Items(object? v, int line, string what) => v switch
    {
        List<object?> l => [.. l],
        null => [],
        string s => s.Select(ch => (object?)ch.ToString()).ToList(),
        double => throw new ScriptError($"'{what}' needs a list, but got {Describe(v)}. For numbers use range(1, 10).", line),
        _ => throw new ScriptError($"'{what}' needs a list, but got {Describe(v)}.", line)
    };

    static object? Index(object? target, object? index, int line)
    {
        int i = (int)Math.Floor(ToNumber(index, line));
        switch (target)
        {
            case List<object?> l:
            {
                int k = i < 0 ? l.Count + i : i;
                if (k < 0 || k >= l.Count)
                    throw new ScriptError(l.Count == 0 ? $"The list is empty, so there's no item {i}."
                        : $"The list has {l.Count} item(s), so there's no item {i} (the first is item 0, the last is item {l.Count - 1}).", line);
                return l[k];
            }
            case string s:
            {
                int k = i < 0 ? s.Length + i : i;
                if (k < 0 || k >= s.Length) throw new ScriptError($"The text has {s.Length} letter(s), so there's no letter {i}.", line);
                return s[k].ToString();
            }
            case null:
                throw new ScriptError("There's no list here (it's nothing yet). Make one first, like items = [].", line);
            default:
                throw new ScriptError($"Only lists and texts have items, not {Describe(target)}.", line);
        }
    }

    /// <summary><c>add 5 to items</c> (makes the list if the variable is still empty) and <c>remove 5 from items</c>.</summary>
    bool RunListCommand(CommandStmt c, List<object?> args, Ctx ctx)
    {
        if (c.Name is not ("add" or "remove")) return false;
        string connector = c.Name == "add" ? "to" : "from";
        if (args.Count == 0 || c.Named == null || !c.Named.TryGetValue(connector, out var where) || where.Count != 1 || c.Named.Count != 1)
            throw new ScriptError($"Write it like '{c.Name} 5 {connector} items' (a value, then '{connector}' and the list).", c.Line);
        var list = Eval(where[0], ctx);
        if (list == null && c.Name == "add" && where[0] is NameExpr name)
        {
            Assign(new AssignStmt(name.Path, "=", new ListExpr([], c.Line), c.Line), ctx);
            list = Eval(name, ctx);
        }
        if (list == null) return true; // removing from nothing: nothing to do
        if (list is not List<object?> l)
            throw new ScriptError($"'{Printer.Print(where[0])}' is not a list (it's {Describe(list)}), so I can't {c.Name} {connector} it.", c.Line);
        foreach (var v in args)
        {
            if (c.Name == "add") l.Add(v);
            else
            {
                int k = l.FindIndex(x => AreEqual(x, v));
                if (k >= 0) l.RemoveAt(k);
            }
        }
        return true;
    }
}
