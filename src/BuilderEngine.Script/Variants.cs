namespace BuilderEngine.Script;

/// <summary>
/// Variants: <c>mechanism RedSlime from Slime</c> is Slime with changes. It gets every param, <c>uses</c>, <c>when</c>
/// and <c>to</c> command of Slime, and can then:
/// <list type="bullet">
/// <item>give a param a new default without its type: <c>param speed = 90</c> (with a type it replaces the param);</item>
/// <item>add params, <c>uses</c> (the same mechanism again replaces Slime's line), and <c>when</c>s — a <c>when</c> is
/// always added, so both Slime's <c>when start</c> and RedSlime's run;</item>
/// <item>replace a command: a <c>to</c> with the same name as one of Slime's is used instead of it (also by Slime's own
/// <c>when</c>s, so overriding a command is how a variant changes what an inherited <c>when</c> does).</item>
/// </list>
/// A variant of a variant works too. Changing Slime changes every variant the next time it is resolved.
/// </summary>
public static class Variants
{
    /// <summary>
    /// The mechanism as it runs: a plain mechanism as it is; a variant merged with the mechanism(s) it comes from.
    /// <paramref name="find"/> returns a mechanism as written (not resolved), or null. Throws a friendly
    /// <see cref="ScriptError"/> when the parent is missing or the variants go round in a loop.
    /// </summary>
    public static MechanismDef Resolve(MechanismDef def, Func<string, MechanismDef?> find) =>
        def.Base == null ? def : Merge(def, find, [def.Name]);

    /// <summary>"RedSlime" or, for a variant, "RedSlime (from Slime)".</summary>
    public static string Label(MechanismDef def) => def.Base == null ? def.Name : $"{def.Name} (from {def.Base})";

    /// <summary>The chain of mechanisms a variant comes from, nearest first (Slime, then Slime's parent...). Stops at a loop.</summary>
    public static List<string> Ancestors(MechanismDef def, Func<string, MechanismDef?> find)
    {
        var list = new List<string>();
        var cur = def;
        while (cur.Base != null && !list.Contains(cur.Base, StringComparer.OrdinalIgnoreCase) && !string.Equals(cur.Base, def.Name, StringComparison.OrdinalIgnoreCase))
        {
            list.Add(cur.Base);
            if (find(cur.Base) is not { } next) break;
            cur = next;
        }
        return list;
    }

    static MechanismDef Merge(MechanismDef def, Func<string, MechanismDef?> find, List<string> chain)
    {
        if (def.Base == null) return def;
        if (chain.Contains(def.Base, StringComparer.OrdinalIgnoreCase))
            throw new ScriptError($"'{chain[0]}' comes from itself in a loop ({string.Join(" → ", chain)} → {def.Base}). A mechanism can't be a variant of one of its own variants.", def.Line);
        var parentRaw = find(def.Base)
            ?? throw new ScriptError($"'{def.Name}' comes from '{def.Base}', but there's no mechanism called '{def.Base}'.", def.Line);
        chain.Add(parentRaw.Name);
        var parent = Merge(parentRaw, find, chain);
        string parentName = parentRaw.Name;

        // Params: the parent's, in their order; a variant's 'param speed = 90' only changes the default.
        var ps = new List<ParamDef>(parent.Params);
        foreach (var p in def.Params)
        {
            int i = ps.FindIndex(x => x.Name == p.Name);
            if (i < 0) ps.Add(p);
            else if (p.Type == "any" && p.Options == null) ps[i] = ps[i] with { Default = p.Default ?? ps[i].Default, Line = p.Line };
            else ps[i] = p;
        }

        var uses = new List<UsesDef>(parent.Uses);
        foreach (var u in def.Uses)
        {
            int i = uses.FindIndex(x => string.Equals(x.Name, u.Name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) uses.Add(u); else uses[i] = u;
        }

        // 'when's are added; each remembers whose code it is (for errors and the debugger).
        var whens = new List<WhenDef>(parent.Whens.Count + def.Whens.Count);
        foreach (var w in parent.Whens) whens.Add(w.From == null ? w with { From = parentName } : w);
        whens.AddRange(def.Whens);

        var procs = new List<ProcDef>(parent.Procs.Count + def.Procs.Count);
        foreach (var p in parent.Procs) procs.Add(p.From == null ? p with { From = parentName } : p);
        foreach (var p in def.Procs)
        {
            int i = procs.FindIndex(x => x.Name == p.Name);
            if (i < 0) procs.Add(p); else procs[i] = p;
        }

        return new MechanismDef(def.Name, ps, uses, whens, def.Line) { Procs = procs, Base = def.Base };
    }
}
