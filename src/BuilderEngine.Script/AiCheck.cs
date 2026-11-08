namespace BuilderEngine.Script;

/// <summary>What testing an answer found: the mechanisms it parsed into and the problems (empty = it works).</summary>
public sealed class AiCheckResult
{
    public List<MechanismDef> Defs { get; } = [];
    public List<string> Problems { get; } = [];
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Checks Builder Script written by the AI before the person sees it: it must parse, every command, function, used
/// mechanism and spawned mechanism must exist (also in branches a short run never reaches), and it must run a few
/// seconds in a throwaway world — the open scene, or a tiny test scene with a player walking into it, its keys pressed
/// and its messages told — without a script error.
/// </summary>
public static class AiCheck
{
    const int MaxProblems = 5;
    const double Dt = 1.0 / 60;

    /// <summary>Checks on a background thread at low priority, giving up after <paramref name="timeout"/> (a loop that never waits).</summary>
    public static Task<AiCheckResult> ValidateAsync(string script, AiContext ctx, TimeSpan? timeout = null)
    {
        var done = new TaskCompletionSource<AiCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = new CancelFlag();
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Validate(script, ctx, cancel)); }
            catch (Exception e) { var r = new AiCheckResult(); r.Problems.Add("Testing it failed: " + e.Message); done.TrySetResult(r); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "AI check" };
        thread.Start();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        return Task.Run(async () =>
        {
            if (await Task.WhenAny(done.Task, Task.Delay(limit)) == done.Task) return done.Task.Result;
            cancel.Cancelled = true; // commands and prints now stop it; give it a moment
            if (await Task.WhenAny(done.Task, Task.Delay(1000)) == done.Task) return done.Task.Result;
            var r = new AiCheckResult();
            r.Problems.Add("Testing it took too long: a loop that never waits? (add 'wait' inside long loops)");
            return r;
        });
    }

    sealed class CancelFlag { public volatile bool Cancelled; }

    public static AiCheckResult Validate(string script, AiContext ctx) => Validate(script, ctx, new CancelFlag());

    static AiCheckResult Validate(string script, AiContext ctx, CancelFlag cancel)
    {
        // A "save game" while testing goes to a throwaway folder (never the project's saves, never the shared in-memory ones).
        string saves = Path.Combine(Path.GetTempPath(), "BuilderEngine_AiCheck_" + Guid.NewGuid().ToString("N")[..8]);
        try { return ValidateIn(script, ctx, cancel, saves); }
        finally { try { if (Directory.Exists(saves)) Directory.Delete(saves, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static AiCheckResult ValidateIn(string script, AiContext ctx, CancelFlag cancel, string saves)
    {
        var result = new AiCheckResult();
        void Problem(string p) { if (result.Problems.Count < MaxProblems && !result.Problems.Contains(p)) result.Problems.Add(p); }

        try { result.Defs.AddRange(Parser.Parse(script)); }
        catch (ScriptError e) { Problem(e.ToString()); return result; }
        if (result.Defs.Count == 0) { Problem("There is no mechanism in the answer (it should start with 'mechanism Name')."); return result; }

        var names = new HashSet<string>(result.Defs.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var library = new Dictionary<string, MechanismDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, src) in ctx.Sources)
        {
            if (names.Contains(n)) continue;
            try { foreach (var d in Parser.Parse(src)) library[d.Name] = d; }
            catch (ScriptError) { }
        }
        foreach (var d in result.Defs) library[d.Name] = d;

        // ---- Static checks: every name must exist ----
        var world = new World { SaveFolder = saves };
        world.UseScenes(ctx.SceneNames.Count > 0 ? ctx.SceneNames : ["Level 1"]);
        foreach (var d in result.Defs) StaticCheck(d, library, world, Problem);
        if (!result.Ok) return result;

        // ---- Run it ----
        foreach (var d in library.Values.Where(d => !names.Contains(d.Name))) world.Library[d.Name] = d;
        try { world.Load(script); }
        catch (ScriptError e) { Problem(e.ToString()); return result; }

        int calls = 0;
        void Guard(int line)
        {
            if (cancel.Cancelled || ++calls > 200_000) throw new ScriptError("It runs too many commands at once — a loop without 'wait'?", line);
        }
        foreach (var key in world.Commands.Keys.ToList())
        {
            var inner = world.Commands[key];
            world.Commands[key] = c => { Guard(c.Line); inner(c); };
        }
        world.Printed += _ => Guard(0);
        world.SoundPlayed += _ => Guard(0);

        GameObject? player = null;
        try
        {
            if (ctx.Scene != null) world.LoadScene(ctx.Scene);
            world.Step(0); // scene objects join
            player = world.Objects.FirstOrDefault(o => o.Tags.Contains("player"));
            if (player == null)
            {
                world.SetSceneSize(800, 450);
                var ground = world.Spawn("Ground", 0, 400, ["Solid"], null, "ground");
                ground.Width = 800; ground.Height = 50;
                player = world.Spawn("Player", 100, 352, ["Platformer"], null, "player");
                player.Height = 48;
                world.Spawn("Enemy", 520, 368, null, null, "enemy");
                world.Spawn("Coin", 300, 360, null, null, "coin");
            }
        }
        catch (ScriptError) { } // a broken object of the scene isn't the answer's fault

        var tested = new List<GameObject>();
        double px = player?.X ?? 100, py = player?.Y ?? 352;
        foreach (var d in result.Defs)
        {
            try
            {
                var o = world.Spawn(d.Name, px + 60 + tested.Count * 40, py, [d.Name], null, "test");
                tested.Add(o);
            }
            catch (ScriptError e) { Problem($"{d.Name}: {e.Message}"); }
        }
        if (!result.Ok) return result;

        var keys = new List<string>();
        var told = new List<string>();
        foreach (var d in result.Defs)
        {
            foreach (var w in d.Whens)
            {
                if (w.Event.Kind == EventKind.Key && w.Event.Filter != null) keys.Add(w.Event.Filter);
                if (w.Event.Kind == EventKind.Told && w.Event.Filter != null) told.Add(w.Event.Filter);
            }
            foreach (var s in d.Whens.SelectMany(w => AllStatements(w.Body)).Concat(d.Procs.SelectMany(p => AllStatements(p.Body))))
                if (s is WaitKeyStmt { Key: StringExpr k }) keys.Add(k.Value.ToLowerInvariant());
        }

        try
        {
            int steps = (int)(3.0 / Dt);
            for (int i = 0; i < steps && !cancel.Cancelled; i++)
            {
                double t = i * Dt;
                if (i == 30) world.KeysDown.Add("right");
                if (i == 120) world.KeysDown.Remove("right");
                if (i >= 60 && i < 60 + keys.Count * 10 && (i - 60) % 10 == 0)
                {
                    string k = keys[(i - 60) / 10];
                    world.KeysDown.Add(k);
                    world.PressKey(k);
                }
                if (i >= 60 && (i - 60) % 10 == 5 && (i - 65) / 10 < keys.Count) world.KeysDown.Remove(keys[(i - 65) / 10]);
                if (i == 150)
                    foreach (var o in tested)
                        foreach (var m in told.Distinct()) world.Tell(o, m, [1.0]);
                world.Step(Dt);
                if (world.Objects.Count > 5000) { Problem("It makes thousands of objects in a few seconds — a spawn without 'wait'?"); break; }
            }
        }
        catch (ScriptError e) { Problem(e.ToString()); }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException)
        {
            Problem("The engine stopped while testing it: " + e.Message);
        }

        foreach (var err in world.Errors)
            if (result.Defs.Any(d => err.StartsWith(d.Name + " (line", StringComparison.OrdinalIgnoreCase) || err.Contains($"'{d.Name}'", StringComparison.OrdinalIgnoreCase)))
                Problem(err);
        return result;
    }

    // ---------------- Static checks ----------------

    static void StaticCheck(MechanismDef d, Dictionary<string, MechanismDef> library, World world, Action<string> problem)
    {
        foreach (var u in d.Uses)
            if (!library.ContainsKey(u.Name)) problem($"Line {u.Line}: there's no mechanism called '{u.Name}' to use.{Suggest(u.Name, library.Keys)}");

        var vars = new HashSet<string>(d.Params.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var bodies = d.Whens.Select(w => w.Body).Concat(d.Procs.Select(p => p.Body)).ToList();
        foreach (var s in bodies.SelectMany(AllStatements))
        {
            if (s is AssignStmt { Target.Length: 1 } a) vars.Add(a.Target[0]);
            if (s is ForEachStmt fe) vars.Add(fe.Var);
        }
        foreach (var p in d.Procs) vars.UnionWith(p.Params);

        bool IsProc(string name)
        {
            int dot = name.IndexOf('.');
            if (dot > 0) return library.TryGetValue(name[..dot], out var md) && md.Procs.Any(p => p.Name == name[(dot + 1)..]);
            return library.Values.Any(m => m.Procs.Any(p => p.Name == name));
        }
        bool IsEngineCommand(string name) =>
            world.Commands.ContainsKey(name) || world.Commands.Keys.Any(k => k.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));

        foreach (var s in bodies.SelectMany(AllStatements))
        {
            if (s is CommandStmt c)
            {
                if (!World.BuiltInCommands.Contains(c.Name) && !IsEngineCommand(c.Name) && !IsProc(c.Name))
                {
                    var known = World.BuiltInCommands.Concat(world.Commands.Keys.Select(k => k.Split(' ')[0])).Concat(library.Values.SelectMany(m => m.Procs.Select(p => p.Name)));
                    problem($"Line {c.Line}: there's no command called '{c.Name}'.{Suggest(c.Name, known)}");
                }
                if (c.Name == "spawn" && c.Args.Count > 0)
                {
                    string? target = c.Args[0] switch
                    {
                        StringExpr se => se.Value,
                        NameExpr { Path.Length: 1 } ne when !vars.Contains(ne.Path[0]) && ne.Path[0] is not ("self" or "other" or "here") => ne.Path[0],
                        _ => null,
                    };
                    if (target != null && !library.ContainsKey(target))
                        problem($"Line {c.Line}: 'spawn {target}' — there's no mechanism called '{target}'. Make it too (another mechanism in the answer) or spawn an existing one.{Suggest(target, library.Keys)}");
                }
            }
            if (s is RepeatStmt { Count: NumberExpr { Value: > 10_000 } big })
                problem($"Line {s.Line}: 'repeat {big.Value}' is far too many times for one frame; use 'repeat forever' with a 'wait' inside.");
            foreach (var e in ExprsOf(s).SelectMany(AllExprs))
                if (e is CallExpr call && !World.BuiltInFunctions.Contains(call.Name) && !world.Functions.ContainsKey(call.Name) && !IsProc(call.Name))
                    problem($"Line {call.Line}: there's no function called '{call.Name}'.{Suggest(call.Name, World.BuiltInFunctions.Concat(world.Functions.Keys))}");
        }

        // A mechanism that spawns itself as soon as it starts never stops making copies.
        foreach (var w in d.Whens.Where(w => w.Event.Kind == EventKind.Start))
            foreach (var s in AllStatements(w.Body))
                if (s is CommandStmt { Name: "spawn" } sp && sp.Args.Count > 0 && sp.Args[0] is NameExpr { Path.Length: 1 } n0 && string.Equals(n0.Path[0], d.Name, StringComparison.OrdinalIgnoreCase)
                    || s is CommandStmt { Name: "spawn" } sp2 && sp2.Args.Count > 0 && sp2.Args[0] is StringExpr s0 && string.Equals(s0.Value, d.Name, StringComparison.OrdinalIgnoreCase))
                    problem($"Line {s.Line}: '{d.Name}' spawns itself when it starts, so the copies never stop. Spawn another mechanism, or wait for a key or a touch.");
    }

    static string Suggest(string name, IEnumerable<string> known)
    {
        string? best = null;
        int bestD = int.MaxValue;
        foreach (var k in known.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int dist = Distance(name.ToLowerInvariant(), k.ToLowerInvariant());
            if (dist < bestD) { bestD = dist; best = k; }
        }
        return best != null && bestD <= Math.Max(1, name.Length / 3) ? $" Did you mean '{best}'?" : "";
    }

    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // ---------------- Walking the code ----------------

    /// <summary>Every statement in a body, inside blocks too.</summary>
    public static IEnumerable<Stmt> AllStatements(List<Stmt> body)
    {
        foreach (var s in body)
        {
            yield return s;
            IEnumerable<List<Stmt>> inner = s switch
            {
                IfStmt i => i.Else != null ? [i.Then, i.Else] : [i.Then],
                RepeatStmt r => [r.Body],
                TogetherStmt t => [t.Body],
                RunStmt r => [r.Body],
                ForEachStmt f => [f.Body],
                WhileStmt w => [w.Body],
                _ => [],
            };
            foreach (var b in inner)
                foreach (var x in AllStatements(b)) yield return x;
        }
    }

    /// <summary>The expressions written directly in a statement.</summary>
    public static IEnumerable<Expr> ExprsOf(Stmt s) => s switch
    {
        AssignStmt a => [a.Value],
        IfStmt i => [i.Cond],
        RepeatStmt { Count: { } c } => [c],
        WaitStmt w => [w.Seconds],
        WaitUntilStmt u => [u.Cond],
        WaitKeyStmt k => [k.Key],
        EmitStmt e => e.Args,
        TellStmt t => new[] { t.Target, t.Message }.Concat(t.Args),
        CommandStmt c => c.Args.Concat(c.Named?.Values.SelectMany(v => v) ?? []),
        ForEachStmt f => [f.List],
        WhileStmt w => [w.Cond],
        ReturnStmt { Value: { } v } => [v],
        _ => [],
    };

    /// <summary>An expression and everything inside it.</summary>
    public static IEnumerable<Expr> AllExprs(Expr e)
    {
        yield return e;
        IEnumerable<Expr> inner = e switch
        {
            UnaryExpr u => [u.Operand],
            BinaryExpr b => [b.Left, b.Right],
            CallExpr c => c.Args,
            ListExpr l => l.Items,
            IndexExpr ix => [ix.Target, ix.Index],
            _ => [],
        };
        foreach (var x in inner)
            foreach (var y in AllExprs(x)) yield return y;
    }
}
