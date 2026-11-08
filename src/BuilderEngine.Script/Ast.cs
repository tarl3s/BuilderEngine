namespace BuilderEngine.Script;

// ---- Expressions ----
public abstract record Expr(int Line);
public sealed record NumberExpr(double Value, int Line) : Expr(Line);
public sealed record StringExpr(string Value, int Line) : Expr(Line);
public sealed record BoolExpr(bool Value, int Line) : Expr(Line);
/// <summary>A name, possibly dotted: <c>hp</c>, <c>other.Health.hp</c>.</summary>
public sealed record NameExpr(string[] Path, int Line) : Expr(Line)
{
    public string Full => string.Join('.', Path);
}
public sealed record UnaryExpr(string Op, Expr Operand, int Line) : Expr(Line);
public sealed record BinaryExpr(string Op, Expr Left, Expr Right, int Line) : Expr(Line);
public sealed record CallExpr(string Name, List<Expr> Args, int Line) : Expr(Line);

// ---- Statements ----
public abstract record Stmt(int Line);
public sealed record AssignStmt(string[] Target, string Op, Expr Value, int Line) : Stmt(Line);
public sealed record IfStmt(Expr Cond, List<Stmt> Then, List<Stmt>? Else, int Line) : Stmt(Line);
/// <summary><c>repeat 3</c> (Count set) or <c>repeat forever</c> (Count null).</summary>
public sealed record RepeatStmt(Expr? Count, List<Stmt> Body, int Line) : Stmt(Line);
public sealed record WaitStmt(Expr Seconds, int Line) : Stmt(Line);
public sealed record EmitStmt(string Signal, List<Expr> Args, int Line) : Stmt(Line);
public sealed record TellStmt(Expr Target, Expr Message, List<Expr> Args, int Line) : Stmt(Line);
public sealed record StopStmt(int Line) : Stmt(Line);
/// <summary>A library command: <c>play "coin.wav"</c>, <c>destroy self</c>, <c>spawn Coin here</c>.
/// Named values come after connector words: <c>move Boss to 400, 300 in 2 seconds</c> → Named {to: [400, 300], in: [2]}.</summary>
public sealed record CommandStmt(string Name, List<Expr> Args, int Line, Dictionary<string, List<Expr>>? Named = null) : Stmt(Line);

// ---- Mechanism ----
public enum EventKind { Start, Tick, Touched, Told, Key, Signal, Tool }

/// <summary>
/// What a <c>when</c> waits for. Filter = a text the event must match (a tag, a message, a key);
/// Bind = a name that holds the event's value inside the handler (<c>other</c>, <c>amount</c>);
/// Source + Name = a signal (<c>Health.died</c>: Source "Health"; <c>died</c>: Source null).
/// </summary>
public sealed record EventSpec(EventKind Kind, string? Filter = null, string? Bind = null, string? Source = null, string? Name = null)
{
    public override string ToString() => Kind switch
    {
        EventKind.Signal => Source == null ? Name! : $"{Source}.{Name}",
        _ => Kind.ToString().ToLowerInvariant()
    };
}

public sealed record WhenDef(EventSpec Event, List<Stmt> Body, int Line);
public sealed record ParamDef(string Name, string Type, Expr? Default, int Line);
/// <summary><c>uses Health max 2</c>: Name "Health", Args {max: 2}; <c>uses Sprite "slime.png"</c>: Positional.</summary>
public sealed record UsesDef(string Name, Expr? Positional, Dictionary<string, Expr> Args, int Line);

public sealed record MechanismDef(string Name, List<ParamDef> Params, List<UsesDef> Uses, List<WhenDef> Whens, int Line)
{
    /// <summary>The commands this mechanism makes (<c>to name params</c>), in the order they are written.</summary>
    public List<ProcDef> Procs { get; init; } = [];
}

// ---- Builder Script 2: user commands, waiting, parallel, loops, lists ----

/// <summary><c>[1, 2, 3]</c>: a list value.</summary>
public sealed record ListExpr(List<Expr> Items, int Line) : Expr(Line);
/// <summary><c>items[0]</c>: one item of a list (0 is the first, -1 the last) or one letter of a text.</summary>
public sealed record IndexExpr(Expr Target, Expr Index, int Line) : Expr(Line);

/// <summary><c>wait until hp &lt;= 0</c>: pauses until the condition is true (checked once per step).</summary>
public sealed record WaitUntilStmt(Expr Cond, int Line) : Stmt(Line);
/// <summary>
/// <c>wait for died</c> / <c>wait for Health.died</c> / <c>wait for other.Health.died</c>: pauses until that signal
/// is emitted. The last name is the signal; what comes before it (if anything) is the object or mechanism it comes from.
/// </summary>
public sealed record WaitSignalStmt(string[] Path, int Line) : Stmt(Line)
{
    public string Full => string.Join('.', Path);
}
/// <summary><c>wait for key "space"</c>: pauses until that key is pressed.</summary>
public sealed record WaitKeyStmt(Expr Key, int Line) : Stmt(Line);
/// <summary><c>together</c>: each statement inside runs at the same time; continues when all of them have finished.</summary>
public sealed record TogetherStmt(List<Stmt> Body, int Line) : Stmt(Line);
/// <summary><c>run patrol</c> (or <c>run</c> + a block): starts it in the background and goes on without waiting.</summary>
public sealed record RunStmt(List<Stmt> Body, int Line) : Stmt(Line);
/// <summary><c>for each enemy in all("enemy")</c>: the body once per item, with the item in <c>Var</c>.</summary>
public sealed record ForEachStmt(string Var, Expr List, List<Stmt> Body, int Line) : Stmt(Line);
/// <summary><c>while hp &gt; 0</c>: repeats while the condition is true (one round per frame if nothing inside waits).</summary>
public sealed record WhileStmt(Expr Cond, List<Stmt> Body, int Line) : Stmt(Line);
/// <summary><c>return value</c>: ends a user command (inside <c>to</c>), optionally giving back a value.</summary>
public sealed record ReturnStmt(Expr? Value, int Line) : Stmt(Line);

/// <summary>
/// <c>to attack target, power</c>: a command the person makes. Called like any command (<c>attack Boss, 3</c>);
/// when it gives back a value with <c>return</c> it can also be used in a value (<c>damage_for(3)</c>).
/// </summary>
public sealed record ProcDef(string Name, List<string> Params, List<Stmt> Body, int Line);
