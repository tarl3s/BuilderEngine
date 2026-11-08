namespace BuilderEngine.Script;

// EDITOR TOOLS: the few things a tool run needs from the interpreter. An editor world (World.NewEditor) holds a copy of
// the scene's objects (no mechanisms running on them) and a tool's mechanism on a hidden object that isn't in the world;
// the tool's 'when tool ...' bodies run through the same Exec as a game. See EditorTools.cs.
public sealed partial class World
{
    [ThreadStatic] static bool t_makingEditor;

    /// <summary>True for a world made by <see cref="NewEditor"/>: it runs editor tools on a scene, not a game.</summary>
    public bool IsEditor { get; } = t_makingEditor;

    /// <summary>A world for editor tools (plugins see <c>IsEditor</c> while it is being made).</summary>
    public static World NewEditor()
    {
        t_makingEditor = true;
        try { return new World(); }
        finally { t_makingEditor = false; }
    }

    /// <summary>A mechanism instance on an object that is not part of the world (the tool's hidden object).</summary>
    internal MechanismInstance InstantiateDetached(MechanismDef def, GameObject owner, Dictionary<string, object?>? overrides) =>
        Instantiate(def, owner, null, overrides);

    /// <summary>A new object that is in the world right away (not waiting for the next step), with no mechanisms.</summary>
    internal GameObject AddStill(string name, double x, double y, double width, double height, IEnumerable<string> tags)
    {
        var o = new GameObject { Id = _nextId++, Name = name, X = x, Y = y, Width = width, Height = height };
        foreach (var t in tags) o.Tags.Add(t);
        _objects.Add(o);
        return o;
    }

    /// <summary>A detached object for a tool's mechanism (it gets an id, but the world doesn't list it).</summary>
    internal GameObject NewDetached(string name) => new() { Id = _nextId++, Name = name };

    /// <summary>Removes every object (a tool run copies the scene again before each use).</summary>
    internal void ClearObjects()
    {
        _objects.Clear();
        _pendingAdd.Clear();
        _touching.Clear();
    }

    internal void DropDestroyed() => _objects.RemoveAll(o => o.Destroyed);

    /// <summary>Starts a 'when' of <paramref name="inst"/> now, with these locals (runs until its first wait).</summary>
    internal void StartWhenWith(MechanismInstance inst, WhenDef w, Dictionary<string, object?> locals) => StartWhen(inst, w, locals);

    /// <summary>Is anything of this mechanism still waiting (a tool that used 'wait')?</summary>
    internal bool IsBusy(MechanismInstance inst) => _running.Any(c => c.Inst == inst && !c.Done);

    /// <summary>Drops everything this mechanism still has running.</summary>
    internal void CancelRunning(MechanismInstance inst) => _running.RemoveAll(c => { if (c.Inst != inst) return false; c.Done = true; return true; });

    /// <summary>Prints a line like the <c>print</c> command (plugins use it).</summary>
    internal void PrintLine(string text)
    {
        Output.Add(text);
        Printed?.Invoke(text);
    }

    /// <summary>A param's default value (its expression evaluated for this instance), or the type's empty value.</summary>
    internal object? ParamDefaultFor(MechanismInstance inst, ParamDef p) =>
        p.Default != null ? Eval(p.Default, new Ctx(this, inst)) : DefaultFor(p.Type);
}
