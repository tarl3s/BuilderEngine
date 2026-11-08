using System.Globalization;

namespace BuilderEngine.Script;

/// <summary>A thing in the game world. It has no behavior of its own: its mechanisms give it one.</summary>
public sealed class GameObject
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
public sealed class MechanismInstance
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
    readonly HashSet<(int, int)> _touching = [];
    int _nextId = 1;

    public IReadOnlyList<GameObject> Objects => _objects;
    public Dictionary<string, MechanismDef> Library { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> Globals { get; } = new();
    public double Time { get; private set; }
    /// <summary>Seconds the last step took (<c>dt</c> in scripts).</summary>
    public double LastDt { get; private set; } = 1.0 / 60;
    public List<string> Output { get; } = [];
    public List<string> Errors { get; } = [];
    public HashSet<string> KeysDown { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra commands the host adds (graphics, sound...). Built-in ones are in <see cref="RunCommand"/>.</summary>
    public Dictionary<string, Action<CommandContext>> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Printed;
    public event Action<string>? SoundPlayed;

    /// <summary>Adds every mechanism in a Builder Script source to the library.</summary>
    public List<MechanismDef> Load(string source)
    {
        var defs = Parser.Parse(source);
        foreach (var d in defs) Library[d.Name] = d;
        return defs;
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
        _pendingAdd.Add(o);
        return o;
    }

    MechanismDef Find(string name, int line) =>
        Library.TryGetValue(name, out var d) ? d
            : throw new ScriptError($"There's no mechanism called '{name}'.{Suggest(name, Library.Keys)}", line);

    MechanismInstance Instantiate(MechanismDef def, GameObject owner, MechanismInstance? parent, Dictionary<string, object?>? overrides, int depth = 0)
    {
        if (depth > 16) throw new ScriptError($"'{def.Name}' uses itself in a loop.", def.Line);
        var inst = new MechanismInstance(def, owner, parent);
        var ctx = new Ctx(this, inst);
        foreach (var p in def.Params)
            inst.Vars[p.Name] = overrides != null && overrides.TryGetValue(p.Name, out var v) ? v
                : p.Default != null ? Eval(p.Default, ctx) : DefaultFor(p.Type);
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

    static object? DefaultFor(string type) => type.ToLowerInvariant() switch
    {
        "number" or "seconds" => 0.0,
        "bool" or "yesno" => false,
        "text" or "string" => "",
        _ => null
    };

    public GameObject? FindObject(string nameOrTag) =>
        _objects.FirstOrDefault(o => !o.Destroyed && o.Is(nameOrTag)) ?? _pendingAdd.FirstOrDefault(o => o.Is(nameOrTag));

    public void PressKey(string key)
    {
        key = key.ToLowerInvariant();
        foreach (var o in _objects.ToArray())
            foreach (var m in o.AllMechanisms())
                foreach (var w in m.Def.Whens)
                    if (w.Event.Kind == EventKind.Key && w.Event.Filter == key) StartWhen(m, w, null);
    }

    /// <summary>Advances the game by dt seconds: new objects start, ticks run, touches fire, waits resume.</summary>
    public void Step(double dt)
    {
        // 1. Newly spawned objects join and run 'when start'.
        while (_pendingAdd.Count > 0)
        {
            var batch = _pendingAdd.ToArray();
            _pendingAdd.Clear();
            _objects.AddRange(batch);
            foreach (var o in batch)
                foreach (var m in o.AllMechanisms())
                    foreach (var w in m.Def.Whens)
                        if (w.Event.Kind == EventKind.Start) StartWhen(m, w, null);
        }

        Time += dt;
        LastDt = dt;

        // 2. 'when tick' runs every step (a tick still busy with a 'wait' isn't started again).
        foreach (var o in _objects.ToArray())
            foreach (var m in o.AllMechanisms())
                foreach (var w in m.Def.Whens)
                    if (w.Event.Kind == EventKind.Tick && !_running.Any(c => c.Inst == m && c.When == w)) StartWhen(m, w, null);

        // 3. Touches: fire once when two objects start overlapping.
        var live = _objects.Where(o => !o.Destroyed).ToArray();
        for (int i = 0; i < live.Length; i++)
            for (int j = i + 1; j < live.Length; j++)
            {
                var a = live[i]; var b = live[j];
                var key = (Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id));
                if (a.Overlaps(b))
                {
                    if (_touching.Add(key)) { FireTouch(a, b); FireTouch(b, a); }
                }
                else _touching.Remove(key);
            }

        // 4. Resume coroutines whose wait is over.
        RunReady();

        // 5. Destroyed objects leave.
        _objects.RemoveAll(o => o.Destroyed);
        _running.RemoveAll(c => c.Inst.Owner.Destroyed);
    }

    void FireTouch(GameObject self, GameObject other)
    {
        if (self.Destroyed || other.Destroyed) return;
        foreach (var m in self.AllMechanisms())
            foreach (var w in m.Def.Whens)
            {
                if (w.Event.Kind != EventKind.Touched) continue;
                if (w.Event.Filter != null && !other.Is(w.Event.Filter)) continue;
                StartWhen(m, w, w.Event.Bind == null ? null : new() { [w.Event.Bind] = other }, other);
            }
    }

    internal void Emit(MechanismInstance from, string signal, List<object?> args)
    {
        foreach (var m in from.Owner.AllMechanisms().ToArray())
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
        foreach (var m in target.AllMechanisms().ToArray())
            foreach (var w in m.Def.Whens)
                if (w.Event.Kind == EventKind.Told && w.Event.Filter == message)
                    StartWhen(m, w, w.Event.Bind == null ? null : new() { [w.Event.Bind] = args.Count > 0 ? args[0] : null });
    }

    void StartWhen(MechanismInstance m, WhenDef w, Dictionary<string, object?>? locals, GameObject? other = null)
    {
        if (m.Owner.Destroyed) return;
        var ctx = new Ctx(this, m) { Locals = locals ?? new(), Other = other };
        var co = new Coroutine(m, w, Exec(w.Body, ctx).GetEnumerator());
        // Runs right away until its first 'wait' (so effects happen in the same step).
        Advance(co);
        if (!co.Done) _running.Add(co);
    }

    void RunReady()
    {
        foreach (var co in _running.ToArray())
            if (!co.Done && co.WakeAt <= Time + 1e-9) Advance(co);
        _running.RemoveAll(c => c.Done);
    }

    void Advance(Coroutine co)
    {
        try
        {
            if (co.Inst.Owner.Destroyed || !co.It.MoveNext()) { co.Done = true; return; }
            co.WakeAt = Time + Math.Max(0, co.It.Current);
        }
        catch (StopException) { co.Done = true; }
        catch (ScriptError e)
        {
            co.Done = true;
            string msg = $"{co.Inst.Def.Name} (line {e.Line}): {e.Message}";
            Errors.Add(msg);
            Printed?.Invoke("⚠ " + msg);
        }
    }

    sealed class Coroutine(MechanismInstance inst, WhenDef when, IEnumerator<double> it)
    {
        public readonly MechanismInstance Inst = inst;
        public readonly WhenDef When = when;
        public readonly IEnumerator<double> It = it;
        public double WakeAt;
        public bool Done;
    }

    sealed class StopException : Exception;

    internal sealed class Ctx(World world, MechanismInstance inst)
    {
        public readonly World World = world;
        public readonly MechanismInstance Inst = inst;
        public Dictionary<string, object?> Locals = new();
        public GameObject? Other;
    }

    // ---------------- Statements ----------------

    IEnumerable<double> Exec(List<Stmt> body, Ctx ctx)
    {
        foreach (var s in body)
        {
            if (ctx.Inst.Owner.Destroyed) yield break;
            switch (s)
            {
                case WaitStmt w:
                    yield return ToNumber(Eval(w.Seconds, ctx), w.Line);
                    break;
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
                        while (!ctx.Inst.Owner.Destroyed)
                        {
                            bool waited = false;
                            foreach (var y in Exec(r.Body, ctx)) { waited = true; yield return y; }
                            if (!waited) yield return 0; // a forever loop with no wait runs once per frame
                        }
                    }
                    else
                    {
                        int n = (int)Math.Round(ToNumber(Eval(r.Count, ctx), r.Line));
                        for (int k = 0; k < n; k++)
                            foreach (var y in Exec(r.Body, ctx)) yield return y;
                    }
                    break;
                }
                default:
                    ExecSimple(s, ctx);
                    break;
            }
        }
    }

    void ExecSimple(Stmt s, Ctx ctx)
    {
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
            case CommandStmt c: RunCommand(c, ctx); break;
            default: throw new ScriptError("This line can't run here.", s.Line);
        }
    }

    void Assign(AssignStmt a, Ctx ctx)
    {
        object? value = Eval(a.Value, ctx);
        if (a.Target.Length == 1)
        {
            string n = a.Target[0];
            Dictionary<string, object?> bag =
                ctx.Locals.ContainsKey(n) ? ctx.Locals :
                ctx.Inst.Vars.ContainsKey(n) ? ctx.Inst.Vars :
                Globals.ContainsKey(n) ? Globals : ctx.Inst.Vars;
            bag.TryGetValue(n, out var old);
            bag[n] = Combine(a.Op, old, value, a.Line);
            return;
        }
        var holder = Resolve(a.Target[..^1], ctx, a.Line);
        string member = a.Target[^1];
        switch (holder)
        {
            case GameObject g:
            {
                double cur = member switch { "x" => g.X, "y" => g.Y, "width" => g.Width, "height" => g.Height, _ => throw new ScriptError($"Objects have x, y, width and height — not '{member}'.", a.Line) };
                double nv = ToNumber(Combine(a.Op, cur, value, a.Line), a.Line);
                switch (member) { case "x": g.X = nv; break; case "y": g.Y = nv; break; case "width": g.Width = nv; break; default: g.Height = nv; break; }
                break;
            }
            case MechanismInstance m:
                m.Vars.TryGetValue(member, out var o);
                m.Vars[member] = Combine(a.Op, o, value, a.Line);
                break;
            default:
                throw new ScriptError($"Can't change '{member}' of {Describe(holder)}.", a.Line);
        }
    }

    static object? Combine(string op, object? old, object? value, int line) => op switch
    {
        "=" => value,
        "+=" => old is string || value is string ? ToText(old) + ToText(value) : ToNumber(old, line) + ToNumber(value, line),
        "-=" => ToNumber(old, line) - ToNumber(value, line),
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
            case NumberExpr n: return n.Value;
            case StringExpr s: return s.Value;
            case BoolExpr b: return b.Value;
            case NameExpr n: return Resolve(n.Path, ctx, n.Line);
            case UnaryExpr u:
            {
                var v = Eval(u.Operand, ctx);
                return u.Op == "not" ? !Truthy(v) : -ToNumber(v, u.Line);
            }
            case BinaryExpr b:
            {
                if (b.Op == "and") return Truthy(Eval(b.Left, ctx)) && Truthy(Eval(b.Right, ctx));
                if (b.Op == "or") return Truthy(Eval(b.Left, ctx)) || Truthy(Eval(b.Right, ctx));
                var l = Eval(b.Left, ctx);
                var r = Eval(b.Right, ctx);
                switch (b.Op)
                {
                    case "+": return l is string || r is string ? ToText(l) + ToText(r) : ToNumber(l, b.Line) + ToNumber(r, b.Line);
                    case "-": return ToNumber(l, b.Line) - ToNumber(r, b.Line);
                    case "*": return ToNumber(l, b.Line) * ToNumber(r, b.Line);
                    case "/": return Divide(ToNumber(l, b.Line), ToNumber(r, b.Line), b.Line);
                    case "%": { double d = ToNumber(r, b.Line); return d == 0 ? throw new ScriptError("Division by zero.", b.Line) : ToNumber(l, b.Line) % d; }
                    case "==": return AreEqual(l, r);
                    case "!=": return !AreEqual(l, r);
                    case "<": return ToNumber(l, b.Line) < ToNumber(r, b.Line);
                    case ">": return ToNumber(l, b.Line) > ToNumber(r, b.Line);
                    case "<=": return ToNumber(l, b.Line) <= ToNumber(r, b.Line);
                    case ">=": return ToNumber(l, b.Line) >= ToNumber(r, b.Line);
                }
                throw new ScriptError($"Unknown operator '{b.Op}'.", b.Line);
            }
            case CallExpr c: return CallFunction(c, ctx);
        }
        throw new ScriptError("I can't work out this value.", e.Line);
    }

    static readonly Random Rng = new();

    object? CallFunction(CallExpr c, Ctx ctx)
    {
        if (c.Name == "key")
        {
            if (c.Args.Count != 1) throw new ScriptError("'key' needs one key, like key(\"left\").", c.Line);
            return KeysDown.Contains(ToText(Eval(c.Args[0], ctx)));
        }
        var a = c.Args.Select(x => ToNumber(Eval(x, ctx), c.Line)).ToArray();
        void Need(int n) { if (a.Length != n) throw new ScriptError($"'{c.Name}' needs {n} value(s), got {a.Length}.", c.Line); }
        switch (c.Name)
        {
            case "random": Need(2); return Math.Floor(a[0] + Rng.NextDouble() * (a[1] - a[0] + 1));
            case "abs": Need(1); return Math.Abs(a[0]);
            case "round": Need(1); return Math.Round(a[0]);
            case "floor": Need(1); return Math.Floor(a[0]);
            case "min": Need(2); return Math.Min(a[0], a[1]);
            case "max": Need(2); return Math.Max(a[0], a[1]);
            case "sqrt": Need(1); return Math.Sqrt(a[0]);
        }
        throw new ScriptError($"There's no function called '{c.Name}'.{Suggest(c.Name, ["random", "abs", "round", "floor", "min", "max", "sqrt"])}", c.Line);
    }

    object? Resolve(string[] path, Ctx ctx, int line)
    {
        object? cur = ResolveFirst(path[0], ctx, line);
        for (int i = 1; i < path.Length; i++)
        {
            string m = path[i];
            cur = cur switch
            {
                GameObject g => m switch
                {
                    "x" => g.X, "y" => g.Y, "width" => g.Width, "height" => g.Height, "name" => g.Name,
                    _ => (object?)g.AllMechanisms().FirstOrDefault(x => string.Equals(x.Def.Name, m, StringComparison.OrdinalIgnoreCase))
                         ?? throw new ScriptError($"'{g.Name}' has no '{m}'.", line)
                },
                MechanismInstance inst => inst.Vars.TryGetValue(m, out var v) ? v
                    : inst.Children.TryGetValue(m, out var ch) ? ch : null,
                _ => throw new ScriptError($"{Describe(cur)} has no '{m}'.", line)
            };
        }
        return cur;
    }

    object? ResolveFirst(string n, Ctx ctx, int line)
    {
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
        foreach (var other in ctx.Inst.Owner.AllMechanisms())
            if (string.Equals(other.Def.Name, n, StringComparison.Ordinal)) return other;
        if (Globals.TryGetValue(n, out v)) return v;
        if (Library.ContainsKey(n)) return n; // a mechanism name, e.g. 'spawn Coin'
        return null; // an unset variable reads as nothing (0 / false / empty text)
    }

    // ---------------- Commands ----------------

    void RunCommand(CommandStmt c, Ctx ctx)
    {
        var args = c.Args.Select(x => Eval(x, ctx)).ToList();
        switch (c.Name)
        {
            case "print":
            {
                string text = string.Join(" ", args.Select(ToText));
                Output.Add(text);
                Printed?.Invoke(text);
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
                if (args.Count == 0) throw new ScriptError("'spawn' needs a mechanism, like 'spawn Coin here'.", c.Line);
                string name = ToText(args[0]);
                Find(name, c.Line);
                double x = ctx.Inst.Owner.X, y = ctx.Inst.Owner.Y;
                if (args.Count >= 2 && args[1] is GameObject at) { x = at.X; y = at.Y; }
                else if (args.Count >= 3) { x = ToNumber(args[1], c.Line); y = ToNumber(args[2], c.Line); }
                Spawn(name, x, y, [name]);
                return;
            }
            case "play":
            {
                string sound = args.Count > 0 ? ToText(args[0]) : "";
                Output.Add("♪ " + sound);
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
        if (Commands.TryGetValue(c.Name, out var cmd)) { cmd(new CommandContext(this, ctx.Inst, args, c.Line)); return; }
        throw new ScriptError($"There's no command called '{c.Name}'.{Suggest(c.Name, BuiltInCommands.Concat(Commands.Keys))}", c.Line);
    }

    public static readonly string[] BuiltInCommands = ["print", "destroy", "spawn", "play", "move", "goto"];

    // ---------------- Values ----------------

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0,
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
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => v.ToString() ?? ""
    };

    static bool AreEqual(object? a, object? b)
    {
        if (a is double || b is double)
            try { return ToNumber(a, 0) == ToNumber(b, 0); } catch (ScriptError) { return false; }
        if (a is GameObject ga && b is string sb) return ga.Is(sb);
        return Equals(a, b) || (a == null && Equals(b, "")) || (b == null && Equals(a, ""));
    }

    static string Describe(object? v) => v switch
    {
        null => "nothing",
        string s => $"the text \"{s}\"",
        double d => $"the number {ToText(d)}",
        bool b => b ? "true" : "false",
        GameObject g => $"the object '{g.Name}'",
        MechanismInstance m => $"the mechanism '{m.Def.Name}'",
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

/// <summary>What a host command receives: the world, the mechanism running it, its values and line.</summary>
public sealed record CommandContext(World World, MechanismInstance Mechanism, List<object?> Args, int Line);
