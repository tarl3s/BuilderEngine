using System.Globalization;

namespace BuilderEngine.Script;

/// <summary>A running 'when' (or a 'run' task) as the debugger shows it: whose, which event, the line and what it waits for.</summary>
public sealed record RunningWhen(MechanismInstance Mechanism, WhenDef? When, string Source, int Line, string Waiting, bool AtBreak)
{
    /// <summary>"Health · when told "damage" amount · line 7 · waiting 0.4s".</summary>
    public string Describe()
    {
        string who = string.Equals(Source, Mechanism.Def.Name, StringComparison.OrdinalIgnoreCase) ? Mechanism.Def.Name : $"{Mechanism.Def.Name} (from {Source})";
        string what = When != null ? Printer.WhenHead(When.Event) : "run (in the background)";
        return $"{who} · {what} · line {Line} · {Waiting}";
    }
}

// DEBUGGER: the runtime side (see Debugger.cs for the debugger itself). Runtime.cs calls Debug at each statement and
// event when one is attached; each running 'when' then keeps its position in a CoState.
public sealed partial class World
{
    /// <summary>The attached debugger, or null (then the runtime does nothing extra).</summary>
    public IDebugHooks? Debug { get; set; }

    /// <summary>Where a running 'when' is: the statement it is on (or waiting at), its line and whose code it is.</summary>
    internal sealed class CoState
    {
        public int Line;
        public Stmt? Stmt;
        public string? Source;
        public bool AtBreak;
    }

    /// <summary>The 'when's running now (waiting, or stopped at a breakpoint), optionally only one object's.</summary>
    public List<RunningWhen> Running(GameObject? only = null)
    {
        var list = new List<RunningWhen>();
        foreach (var co in _running)
        {
            if (co.Done || (only != null && co.Inst.Owner != only)) continue;
            var st = co.State;
            string source = st?.Source ?? co.When?.From ?? co.Inst.Def.Name;
            int line = st?.Line ?? co.When?.Line ?? 0;
            list.Add(new RunningWhen(co.Inst, co.When, source, line, Waiting(co), st?.AtBreak == true));
        }
        return list;
    }

    string Waiting(Coroutine co)
    {
        if (co.State?.AtBreak == true) return "stopped at a breakpoint";
        double left = co.WakeAt - Time;
        string Secs() => left.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        switch (co.State?.Stmt)
        {
            case WaitStmt when left > 1e-9: return "waiting " + Secs();
            case WaitUntilStmt u: return "waiting until " + Printer.Print(u.Cond);
            case WaitSignalStmt f: return "waiting for " + f.Full;
            case WaitKeyStmt k: return "waiting for key " + Printer.Print(k.Key);
            case CommandStmt c when co.Until != null || left > 1e-9: return "busy: " + Printer.Simple(c);
        }
        if (left > 1e-9) return "waiting " + Secs();
        return co.Until != null ? "waiting" : "next frame";
    }

    /// <summary>" 1, \"x\"": the values of a signal or message, for the timeline.</summary>
    static string DebugArgs(List<object?> args) => args.Count == 0 ? "" : " " + string.Join(", ", args.Select(a => Debugger.Show(a, 30)));
}
