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
/// <summary>A library command: <c>play "coin.wav"</c>, <c>destroy self</c>, <c>spawn Coin here</c>.</summary>
public sealed record CommandStmt(string Name, List<Expr> Args, int Line) : Stmt(Line);

// ---- Mechanism ----
public enum EventKind { Start, Tick, Touched, Told, Key, Signal }

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

public sealed record MechanismDef(string Name, List<ParamDef> Params, List<UsesDef> Uses, List<WhenDef> Whens, int Line);
