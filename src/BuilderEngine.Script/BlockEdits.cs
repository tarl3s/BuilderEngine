namespace BuilderEngine.Script;

/// <summary>
/// The editing operations behind the blocks view, kept out of the UI so they can be tested headless:
/// list moves, deep copies, new blocks from the palette and reading what the person typed in a block's field.
/// The AST records are immutable but their lists are not: a block is changed by replacing it in its list.
/// </summary>
public static class BlockEdits
{
    static readonly HashSet<string> Keywords =
    [
        "mechanism", "param", "uses", "when", "if", "else", "repeat", "forever", "wait", "emit", "tell", "stop",
        "and", "or", "not", "true", "false"
    ];

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

    // ---------------- Deep copies ----------------

    public static Stmt Clone(Stmt s) => s switch
    {
        IfStmt i => i with { Then = CloneList(i.Then), Else = i.Else == null ? null : CloneList(i.Else) },
        RepeatStmt r => r with { Body = CloneList(r.Body) },
        EmitStmt e => e with { Args = [.. e.Args] },
        TellStmt t => t with { Args = [.. t.Args] },
        CommandStmt c => c with { Args = [.. c.Args] },
        _ => s // the rest hold only immutable values
    };

    public static List<Stmt> CloneList(List<Stmt> list) => list.Select(Clone).ToList();

    // ---------------- New blocks (palette) ----------------

    /// <summary>The palette's statement blocks, in palette order: id and the text shown.</summary>
    public static readonly (string Id, string Label)[] StatementKinds =
    [
        ("if", "if"), ("ifelse", "if / else"), ("repeat", "repeat"), ("forever", "repeat forever"),
        ("wait", "wait"), ("stop", "stop"),
        ("emit", "emit"), ("tell", "tell"),
        ("set", "set variable"), ("change", "change variable"),
        ("print", "print"), ("play", "play"), ("spawn", "spawn"), ("destroy", "destroy"), ("move", "move"), ("goto", "goto"),
    ];

    /// <summary>A new statement with sensible starting values (palette ids from <see cref="StatementKinds"/>).</summary>
    public static Stmt NewStatement(string id) => id switch
    {
        "if" => new IfStmt(new BoolExpr(true, 0), [], null, 0),
        "ifelse" => new IfStmt(new BoolExpr(true, 0), [], [], 0),
        "repeat" => new RepeatStmt(new NumberExpr(3, 0), [], 0),
        "forever" => new RepeatStmt(null, [], 0),
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

    public static readonly EventKind[] EventKinds = [EventKind.Start, EventKind.Tick, EventKind.Touched, EventKind.Told, EventKind.Key, EventKind.Signal];

    /// <summary>The event's name in a menu: start, tick, touched by, told, key, signal.</summary>
    public static string EventLabel(EventKind k) => k == EventKind.Signal ? "signal" : Printer.EventWord(k);

    public static EventSpec DefaultEvent(EventKind k) => k switch
    {
        EventKind.Touched => new EventSpec(k, Filter: "player"),
        EventKind.Told => new EventSpec(k, Filter: "damage", Bind: "amount"),
        EventKind.Key => new EventSpec(k, Filter: "space"),
        EventKind.Signal => new EventSpec(k, Name: "died"),
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
