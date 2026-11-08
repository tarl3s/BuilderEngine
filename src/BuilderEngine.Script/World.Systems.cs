namespace BuilderEngine.Script;

// SYSTEMS at runtime: a system's 'includes Pickup sound pickup_sound' gives every Pickup in the scene that default
// (an object that sets 'sound' itself in the inspector keeps its own). The system's params are read where the system
// object is, so changing the system's card changes every Pickup at once. See Systems.cs for the editor side.
public sealed partial class World
{
    /// <summary>Mechanism → param → the default a system in this scene gives it (empty when there are no systems).</summary>
    readonly Dictionary<string, Dictionary<string, object?>> _systemDefaults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The default a system gives this mechanism's param (an 'includes' setting), if any.</summary>
    internal bool SystemDefault(string mechanism, string param, out object? value)
    {
        value = null;
        return _systemDefaults.Count > 0 && _systemDefaults.TryGetValue(mechanism, out var ps) && ps.TryGetValue(param, out value);
    }

    /// <summary>What the systems in this world give their mechanisms (for tests and the debugger): mechanism → param → value.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, object?>> SystemDefaults => _systemDefaults;

    /// <summary>
    /// A system's 'includes' lines, evaluated with the system's params: they become the defaults of those mechanisms.
    /// A missing mechanism or param is a friendly error, like a broken 'uses'.
    /// </summary>
    internal void RegisterSystem(MechanismDef def, Ctx ctx)
    {
        foreach (var u in def.Includes)
        {
            var target = TryDef(u.Name, u.Line)
                ?? throw new ScriptError($"'{def.Name}' includes '{u.Name}', but there's no mechanism called '{u.Name}'.{Suggest(u.Name, Library.Keys)}", u.Line);
            if (u.Positional == null && u.Args.Count == 0) continue;
            if (!_systemDefaults.TryGetValue(target.Name, out var ps))
                _systemDefaults[target.Name] = ps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (u.Positional != null)
            {
                if (target.Params.Count == 0) throw new ScriptError($"'{u.Name}' has no params to set.", u.Line);
                ps[target.Params[0].Name] = ConvertParam(Eval(u.Positional, ctx), target.Params[0].Type);
            }
            foreach (var (k, e) in u.Args)
            {
                var p = target.Params.FirstOrDefault(x => string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ScriptError($"'{u.Name}' has no param called '{k}'.{Suggest(k, target.Params.Select(x => x.Name))}", u.Line);
                ps[p.Name] = ConvertParam(Eval(e, ctx), p.Type);
            }
        }
    }

    /// <summary>
    /// Before a scene's objects are made: every system in it registers its 'includes' settings, so an object listed
    /// before the system object still gets them. Problems are reported later, when the system object itself is made.
    /// </summary>
    void PrepareSystems(Scene scene)
    {
        foreach (var so in scene.Objects)
            foreach (var m in so.Mechanisms)
            {
                MechanismDef? def;
                try { def = TryDef(m.Name, 0); }
                catch (ScriptError) { continue; }
                if (def == null || def.Includes.Count == 0) continue;
                try
                {
                    var inst = new MechanismInstance(def, new GameObject { Name = so.Name, X = so.X, Y = so.Y, Width = so.Width, Height = so.Height }, null);
                    var ctx = new Ctx(this, inst);
                    foreach (var p in def.Params)
                    {
                        var set = m.Params.FirstOrDefault(kv => string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase));
                        inst.Vars[p.Name] = set.Key != null ? ConvertParam(set.Value, p.Type)
                            : p.Default != null ? Eval(p.Default, ctx) : DefaultFor(p.Type);
                    }
                    RegisterSystem(def, ctx);
                }
                catch (ScriptError) { /* reported when the object is made */ }
            }
    }
}
