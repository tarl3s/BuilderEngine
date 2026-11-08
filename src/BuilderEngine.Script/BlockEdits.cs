namespace BuilderEngine.Script;

/// <summary>
/// The editing operations behind the blocks view, kept out of the UI so they can be tested headless:
/// list moves, deep copies, new blocks from the palette and reading what the person typed in a block's field.
/// The AST records are immutable but their lists are not: a block is changed by replacing it in its list.
/// </summary>
public static class BlockEdits
{
    static HashSet<string> Keywords => Parser.Keywords;

    // ---------------- List operations ----------------

    public static void Insert<T>(List<T> list, int index, T item) => list.Insert(Math.Clamp(index, 0, list.Count), item);

    public static bool Delete<T>(List<T> list, int index)
    {
        if (index < 0 || index >= list.Count) return false;
        list.RemoveAt(index);
        return true;
    }

    /// <summary>Moves an item up (delta -1) or down (+1); false when it is already at that end.</summary>
    public static bool Move<T>(List<T> list, int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= list.Count || to < 0 || to >= list.Count) return false;
        (list[index], list[to]) = (list[to], list[index]);
        return true;
    }

    /// <summary>Inserts a deep copy right after the item.</summary>
    public static void Duplicate(List<Stmt> list, int index) => list.Insert(index + 1, Clone(list[index]));

    public static void Duplicate(List<WhenDef> list, int index)
    {
        var w = list[index];
        list.Insert(index + 1, w with { Body = CloneList(w.Body) });
    }

    // ---------------- Drag and drop ----------------

    /// <summary>The statement lists directly inside a block: an if's then and else, a repeat's body.</summary>
    public static IEnumerable<List<Stmt>> Bodies(Stmt s)
    {
        switch (s)
        {
            case IfStmt i:
                yield return i.Then;
                if (i.Else != null) yield return i.Else;
                break;
            case RepeatStmt r:
                yield return r.Body;
                break;
            case TogetherStmt t:
                yield return t.Body;
                break;
            case RunStmt r:
                yield return r.Body;
                break;
            case ForEachStmt f:
                yield return f.Body;
                break;
            case WhileStmt w:
                yield return w.Body;
                break;
        }
    }

    /// <summary>True when <paramref name="list"/> is one of the bodies of <paramref name="s"/>, at any depth.</summary>
    public static bool Contains(Stmt s, List<Stmt> list)
    {
        foreach (var b in Bodies(s))
        {
            if (ReferenceEquals(b, list)) return true;
            foreach (var c in b) if (Contains(c, list)) return true;
        }
        return false;
    }

    /// <summary>
    /// Moves <c>from[index]</c> so it ends up before what is now <c>to[toIndex]</c> (toIndex = to.Count: at the end).
    /// Works inside one list or between two. False (and nothing changes) when the index is wrong or the move changes nothing.
    /// </summary>
    public static bool MoveItem<T>(List<T> from, int index, List<T> to, int toIndex)
    {
        if (index < 0 || index >= from.Count || toIndex < 0 || toIndex > to.Count) return false;
        if (ReferenceEquals(from, to))
        {
            if (toIndex == index || toIndex == index + 1) return false;
            var item = from[index];
            from.RemoveAt(index);
            from.Insert(toIndex > index ? toIndex - 1 : toIndex, item);
            return true;
        }
        var moved = from[index];
        from.RemoveAt(index);
        to.Insert(toIndex, moved);
        return true;
    }

    /// <summary>Like <see cref="MoveItem{T}"/>, but refuses to put a block inside itself (into one of its own bodies).</summary>
    public static bool MoveStatement(List<Stmt> from, int index, List<Stmt> to, int toIndex)
    {
        if (index < 0 || index >= from.Count || Contains(from[index], to)) return false;
        return MoveItem(from, index, to, toIndex);
    }

    /// <summary>
    /// Where a drop lands in a column of blocks: the number of blocks whose middle is above <paramref name="y"/>
    /// (rows are top/bottom pairs, top to bottom). 0 = before the first block, rows.Count = after the last.
    /// </summary>
    public static int DropIndex(IReadOnlyList<(double Top, double Bottom)> rows, double y)
    {
        int n = 0;
        foreach (var (top, bottom) in rows)
            if ((top + bottom) / 2 < y) n++;
            else break;
        return n;
    }

    /// <summary>Palette blocks that belong to the mechanism itself, not inside a when: when, param, uses, to.</summary>
    public static bool IsMechanismLevel(string paletteId) => paletteId is "when" or "param" or "uses" or "to";

    /// <summary>The top-level bodies of a mechanism: every when's, then every 'to' command's.</summary>
    public static List<Stmt>? TopBody(MechanismDef m, int index) =>
        index < 0 ? null : index < m.Whens.Count ? m.Whens[index].Body
            : index - m.Whens.Count < m.Procs.Count ? m.Procs[index - m.Whens.Count].Body : null;

    /// <summary>
    /// The address of a statement list inside a mechanism, so it can be found again after the code is parsed anew:
    /// [top index (whens first, then 'to' commands), then for each level: block index, branch (0 = then / body, 1 = else)].
    /// Null when it isn't there.
    /// </summary>
    public static int[]? PathOf(MechanismDef m, List<Stmt> body)
    {
        for (int w = 0; TopBody(m, w) is { } top; w++)
        {
            var path = new List<int> { w };
            if (Find(top, body, path)) return [.. path];
        }
        return null;

        static bool Find(List<Stmt> list, List<Stmt> target, List<int> path)
        {
            if (ReferenceEquals(list, target)) return true;
            for (int i = 0; i < list.Count; i++)
            {
                int branch = 0;
                foreach (var b in Bodies(list[i]))
                {
                    path.Add(i); path.Add(branch++);
                    if (Find(b, target, path)) return true;
                    path.RemoveRange(path.Count - 2, 2);
                }
            }
            return false;
        }
    }

    /// <summary>The statement list at a <see cref="PathOf"/> address, or null when the mechanism no longer has it.</summary>
    public static List<Stmt>? BodyAt(MechanismDef m, int[] path)
    {
        if (path.Length == 0 || path.Length % 2 == 0 || TopBody(m, path[0]) is not { } list) return null;
        for (int k = 1; k < path.Length; k += 2)
        {
            int i = path[k], branch = path[k + 1];
            if (i < 0 || i >= list.Count) return null;
            var next = Bodies(list[i]).Skip(branch).FirstOrDefault();
            if (next == null) return null;
            list = next;
        }
        return list;
    }

    // ---------------- Deep copies ----------------

    public static Stmt Clone(Stmt s) => s switch
    {
        IfStmt i => i with { Then = CloneList(i.Then), Else = i.Else == null ? null : CloneList(i.Else) },
        RepeatStmt r => r with { Body = CloneList(r.Body) },
        TogetherStmt t => t with { Body = CloneList(t.Body) },
        RunStmt r => r with { Body = CloneList(r.Body) },
        ForEachStmt f => f with { Body = CloneList(f.Body) },
        WhileStmt w => w with { Body = CloneList(w.Body) },
        EmitStmt e => e with { Args = [.. e.Args] },
        TellStmt t => t with { Args = [.. t.Args] },
        CommandStmt c => c with { Args = [.. c.Args], Named = c.Named?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()) },
        _ => s // the rest hold only immutable values
    };

    public static List<Stmt> CloneList(List<Stmt> list) => list.Select(Clone).ToList();

    public static void Duplicate(List<ProcDef> list, int index)
    {
        var p = list[index];
        string name = p.Name + "_copy";
        for (int n = 2; list.Any(x => x.Name == name); n++) name = p.Name + "_copy" + n;
        list.Insert(index + 1, p with { Name = name, Params = [.. p.Params], Body = CloneList(p.Body) });
    }

    // ---------------- User commands ----------------

    /// <summary>True when the statement list is inside a 'to' command (where 'return' can go).</summary>
    public static bool InProc(MechanismDef m, List<Stmt> body) => PathOf(m, body) is { } p && p[0] >= m.Whens.Count;

    /// <summary>A new 'to' command with a name the mechanism doesn't use yet (do_something, do_something2...).</summary>
    public static ProcDef NewProc(MechanismDef m)
    {
        string name = "do_something";
        for (int n = 2; m.Procs.Any(p => p.Name == name); n++) name = "do_something" + n;
        return new ProcDef(name, [], [], 0);
    }

    /// <summary>A call to a user command, one starting value (0) per input.</summary>
    public static CommandStmt NewCall(string name, int inputs) =>
        new(name, Enumerable.Range(0, inputs).Select(_ => (Expr)new NumberExpr(0, 0)).ToList(), 0);

    /// <summary>Reads a command's inputs typed in a block (<c>target, power</c>): names, no duplicates.</summary>
    public static List<string> ParseProcParams(string text)
    {
        var list = new List<string>();
        foreach (var raw in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (CheckName(raw) is { } err) throw new ScriptError(err, 0);
            if (list.Contains(raw)) throw new ScriptError($"There are two inputs called '{raw}'.", 0);
            list.Add(raw);
        }
        return list;
    }

    /// <summary>Null when a user command may have this name in this mechanism; else why not.</summary>
    public static string? CheckProcName(MechanismDef m, string name, ProcDef? self = null)
    {
        if (CheckName(name) is { } err) return err;
        if (Parser.CheckCommandName(name) is { } bad) return bad;
        if (m.Procs.Any(p => p != self && p.Name == name)) return $"'{m.Name}' already has a command called '{name}'.";
        return null;
    }

    /// <summary>
    /// Renames the calls to a user command inside one mechanism (statements and values, at any depth), so renaming
    /// a 'to' block keeps its callers working. Returns how many calls changed.
    /// </summary>
    public static int RenameCalls(MechanismDef m, string oldName, string newName)
    {
        int n = 0;
        foreach (var w in m.Whens) n += RenameIn(w.Body, oldName, newName);
        foreach (var p in m.Procs) n += RenameIn(p.Body, oldName, newName);
        return n;
    }

    static int RenameIn(List<Stmt> list, string oldName, string newName)
    {
        int n = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s is CommandStmt c && c.Name == oldName) { list[i] = s = c with { Name = newName }; n++; }
            var renamed = RenameExprs(list[i], oldName, newName, ref n);
            list[i] = renamed;
            foreach (var b in Bodies(renamed)) n += RenameIn(b, oldName, newName);
        }
        return n;
    }

    static Stmt RenameExprs(Stmt s, string o, string nn, ref int n)
    {
        int count = 0;
        Expr R(Expr e) => RenameExpr(e, o, nn, ref count);
        List<Expr> RL(List<Expr> l) => l.Select(R).ToList();
        Stmt result = s switch
        {
            AssignStmt a => a with { Value = R(a.Value) },
            IfStmt i => i with { Cond = R(i.Cond) },
            RepeatStmt r when r.Count != null => r with { Count = R(r.Count) },
            WaitStmt w => w with { Seconds = R(w.Seconds) },
            WaitUntilStmt u => u with { Cond = R(u.Cond) },
            WhileStmt w => w with { Cond = R(w.Cond) },
            ForEachStmt f => f with { List = R(f.List) },
            ReturnStmt r when r.Value != null => r with { Value = R(r.Value) },
            EmitStmt e => e with { Args = RL(e.Args) },
            TellStmt t => t with { Target = R(t.Target), Message = R(t.Message), Args = RL(t.Args) },
            CommandStmt c => c with { Args = RL(c.Args), Named = c.Named?.ToDictionary(kv => kv.Key, kv => RL(kv.Value)) },
            _ => s
        };
        n += count;
        return count > 0 ? result : s;
    }

    static Expr RenameExpr(Expr e, string o, string nn, ref int n)
    {
        int count = 0;
        Expr R(Expr x) => RenameExpr(x, o, nn, ref count);
        Expr result = e switch
        {
            CallExpr c => c with { Name = c.Name == o ? Counted(nn, ref count) : c.Name, Args = c.Args.Select(R).ToList() },
            UnaryExpr u => u with { Operand = R(u.Operand) },
            BinaryExpr b => b with { Left = R(b.Left), Right = R(b.Right) },
            ListExpr l => l with { Items = l.Items.Select(R).ToList() },
            IndexExpr x => x with { Target = R(x.Target), Index = R(x.Index) },
            _ => e
        };
        n += count;
        return count > 0 ? result : e;

        static string Counted(string name, ref int c) { c++; return name; }
    }

    /// <summary>A user command someone can call from the open mechanism, and the mechanism that makes it.</summary>
    public sealed record UserCommand(string CallName, ProcDef Def, string Mechanism, string Group);

    /// <summary>
    /// The user commands the open mechanism can call, for the palette's MY COMMANDS: its own ("This mechanism"),
    /// the ones of the mechanisms it uses ("From Health") and every other mechanism's in the project ("In this project").
    /// A command whose name more than one other mechanism makes is called with its mechanism (<c>Tools.flash</c>).
    /// </summary>
    public static List<UserCommand> CommandsFor(MechanismDef open, IEnumerable<MechanismDef> project)
    {
        var all = project.Where(d => d.Name != open.Name).GroupBy(d => d.Name).Select(g => g.First()).ToList();
        var result = new List<UserCommand>();
        var seen = new HashSet<string>(); // names already callable plainly: a later one with the same name needs its mechanism
        foreach (var p in open.Procs)
            if (seen.Add(p.Name)) result.Add(new UserCommand(p.Name, p, open.Name, "This mechanism"));
        var used = new List<MechanismDef>();
        foreach (var u in open.Uses)
            if (all.FirstOrDefault(x => x.Name == u.Name) is { } d && !used.Contains(d)) used.Add(d);
        foreach (var d in used)
            foreach (var p in d.Procs)
            {
                bool plain = !seen.Contains(p.Name) && used.Count(x => x.Procs.Any(q => q.Name == p.Name)) == 1;
                result.Add(new UserCommand(plain ? p.Name : d.Name + "." + p.Name, p, d.Name, "From " + d.Name));
                seen.Add(p.Name);
            }
        foreach (var d in all.Where(x => !used.Contains(x)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var p in d.Procs)
            {
                bool plain = !seen.Contains(p.Name) && all.Count(x => x.Procs.Any(q => q.Name == p.Name)) == 1;
                result.Add(new UserCommand(plain ? p.Name : d.Name + "." + p.Name, p, d.Name, "In this project"));
            }
        return result;
    }

    /// <summary>The command a call statement refers to (same rules as the game, by name), or null.</summary>
    public static ProcDef? FindCommand(string callName, MechanismDef open, IEnumerable<MechanismDef> project)
    {
        int dot = callName.IndexOf('.');
        if (dot > 0)
        {
            string mech = callName[..dot], proc = callName[(dot + 1)..];
            var md = mech == open.Name ? open : project.FirstOrDefault(d => d.Name == mech);
            return md?.Procs.FirstOrDefault(p => p.Name == proc);
        }
        if (World.BuiltInCommands.Contains(callName)) return null;
        return CommandsFor(open, project).FirstOrDefault(c => c.CallName == callName)?.Def;
    }

    // ---------------- New blocks (palette) ----------------

    /// <summary>The palette's statement blocks, in palette order: id and the text shown.</summary>
    public static readonly (string Id, string Label)[] StatementKinds =
    [
        ("if", "if"), ("ifelse", "if / else"), ("repeat", "repeat"), ("forever", "repeat forever"),
        ("while", "while"), ("foreach", "for each"), ("together", "together"), ("run", "run in background"),
        ("wait", "wait"), ("waituntil", "wait until"), ("waitfor", "wait for signal"), ("waitkey", "wait for key"), ("stop", "stop"),
        ("emit", "emit"), ("tell", "tell"),
        ("set", "set variable"), ("change", "change variable"),
        ("listadd", "add to list"), ("listremove", "remove from list"),
        ("print", "print"), ("play", "play"), ("spawn", "spawn"), ("destroy", "destroy"), ("move", "move"), ("goto", "goto"),
    ];

    /// <summary>A new statement with sensible starting values (palette ids from <see cref="StatementKinds"/>).</summary>
    public static Stmt NewStatement(string id) => id switch
    {
        "if" => new IfStmt(new BoolExpr(true, 0), [], null, 0),
        "ifelse" => new IfStmt(new BoolExpr(true, 0), [], [], 0),
        "repeat" => new RepeatStmt(new NumberExpr(3, 0), [], 0),
        "forever" => new RepeatStmt(null, [], 0),
        "while" => new WhileStmt(new BinaryExpr(">", new NameExpr(["hp"], 0), new NumberExpr(0, 0), 0), [], 0),
        "foreach" => new ForEachStmt("enemy", new CallExpr("all", [new StringExpr("enemy", 0)], 0), [], 0),
        "together" => new TogetherStmt([], 0),
        "run" => new RunStmt([], 0),
        "waituntil" => new WaitUntilStmt(new NameExpr(["self", "on_ground"], 0), 0),
        "waitfor" => new WaitSignalStmt(["Health", "died"], 0),
        "waitkey" => new WaitKeyStmt(new StringExpr("space", 0), 0),
        "return" => new ReturnStmt(null, 0),
        "listadd" => new CommandStmt("add", [new StringExpr("key", 0)], 0, new() { ["to"] = [new NameExpr(["items"], 0)] }),
        "listremove" => new CommandStmt("remove", [new StringExpr("key", 0)], 0, new() { ["from"] = [new NameExpr(["items"], 0)] }),
        "wait" => new WaitStmt(new NumberExpr(1, 0), 0),
        "stop" => new StopStmt(0),
        "emit" => new EmitStmt("done", [], 0),
        "tell" => new TellStmt(new NameExpr(["other"], 0), new StringExpr("hello", 0), [], 0),
        "set" => new AssignStmt(["x"], "=", new NumberExpr(1, 0), 0),
        "change" => new AssignStmt(["x"], "+=", new NumberExpr(1, 0), 0),
        "print" => new CommandStmt("print", [new StringExpr("Hello!", 0)], 0),
        "play" => new CommandStmt("play", [new StringExpr("sound.wav", 0)], 0),
        "spawn" => new CommandStmt("spawn", [new NameExpr(["Coin"], 0), new NameExpr(["here"], 0)], 0),
        "destroy" => new CommandStmt("destroy", [new NameExpr(["self"], 0)], 0),
        "move" => new CommandStmt("move", [new NumberExpr(10, 0), new NumberExpr(0, 0)], 0),
        "goto" => new CommandStmt("goto", [new NumberExpr(0, 0), new NumberExpr(0, 0)], 0),
        _ => throw new ArgumentException($"Unknown block '{id}'.")
    };

    public static readonly EventKind[] EventKinds = [EventKind.Start, EventKind.Tick, EventKind.Touched, EventKind.Told, EventKind.Key, EventKind.Signal, EventKind.Tool];

    /// <summary>The event's name in a menu: start, tick, touched by, told, key, signal.</summary>
    public static string EventLabel(EventKind k) => k == EventKind.Signal ? "signal" : Printer.EventWord(k);

    public static EventSpec DefaultEvent(EventKind k) => k switch
    {
        EventKind.Touched => new EventSpec(k, Filter: "player"),
        EventKind.Told => new EventSpec(k, Filter: "damage", Bind: "amount"),
        EventKind.Key => new EventSpec(k, Filter: "space"),
        EventKind.Signal => new EventSpec(k, Name: "died"),
        EventKind.Tool => new EventSpec(k, Name: "click", Bind: "x, y"),
        _ => new EventSpec(k)
    };

    public static WhenDef NewWhen(EventKind k) => new(DefaultEvent(k), [], 0);

    /// <summary>A new param with a name the mechanism doesn't use yet (speed, speed2...).</summary>
    public static ParamDef NewParam(MechanismDef m)
    {
        string name = "speed";
        for (int n = 2; m.Params.Any(p => p.Name == name); n++) name = "speed" + n;
        return new ParamDef(name, "number", new NumberExpr(100, 0), 0);
    }

    public static UsesDef NewUses() => new("Health", null, new Dictionary<string, Expr>(), 0);

    // ---------------- Reading fields ----------------

    /// <summary>Null when <paramref name="text"/> is a valid name (dotted like <c>self.x</c> if allowed); else why not.</summary>
    public static string? CheckName(string text, bool dotted = false)
    {
        text = text.Trim();
        if (text.Length == 0) return "Write a name here.";
        var parts = text.Split('.');
        if (parts.Length > 1 && !dotted) return "A name can't have dots here.";
        foreach (var p in parts)
        {
            if (p.Length == 0 || !(char.IsLetter(p[0]) || p[0] == '_') || !p.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return $"'{text}' is not a name: use letters, digits and _ (starting with a letter).";
        }
        if (Keywords.Contains(parts[0])) return $"'{parts[0]}' is a Builder Script word; pick another name.";
        return null;
    }

    /// <summary>Reads an event from its kind and the text after the event word (<c>"damage" amount</c>).</summary>
    public static EventSpec ParseEvent(EventKind kind, string tail)
    {
        tail = tail.Trim();
        if (kind is EventKind.Start or EventKind.Tick) return new EventSpec(kind);
        if (kind == EventKind.Signal && tail.Length == 0) throw new ScriptError("Write the signal's name, like died or Health.died.", 0);
        if (kind is EventKind.Told or EventKind.Key && tail.Length == 0) throw new ScriptError("Write the text in quotes, like \"damage\".", 0);
        var ev = ParseOne($"when {Printer.EventWord(kind)} {tail}:").Whens[0].Event;
        if (ev.Kind != kind) throw new ScriptError($"'{tail}' is not a {EventLabel(kind)} event.", 0);
        return ev;
    }

    /// <summary>Reads what follows <c>uses Name</c>: a value and/or <c>param value</c> pairs.</summary>
    public static (Expr? Positional, Dictionary<string, Expr> Args) ParseUsesTail(string tail)
    {
        var u = ParseOne($"uses X {tail.Trim()}").Uses[0];
        return (u.Positional, u.Args);
    }

    /// <summary>Parses one line placed inside a throwaway mechanism.</summary>
    static MechanismDef ParseOne(string line)
    {
        if (line.Contains('\n') || line.Contains('\r')) throw new ScriptError("Keep it on one line.", 0);
        try { return Parser.Parse("mechanism M\n    " + line + "\n")[0]; }
        catch (ScriptError e) { throw new ScriptError(e.Message, 0); }
    }
}
