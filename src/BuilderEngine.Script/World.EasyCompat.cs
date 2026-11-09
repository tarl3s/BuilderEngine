using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

/// <summary>
/// The world's side of EasyCompat (EasyCompat.cs): the scene-loading hooks and the <c>easy_…</c> functions and commands
/// the EasyKit mechanisms and the translated rules and cutscenes use for what is EasyEngine's own way of doing things
/// (objects found by name, its formulas, its variables, its dialogue queue, one key press per Interact…). They are
/// there in every world but do nothing useful outside an EasyEngine project.
/// </summary>
public sealed partial class World
{
    /// <summary>EasyEngine behaviors played here, or null (set by <see cref="GameBoot.UseProject"/>; kept by <see cref="ForNextScene"/>).</summary>
    public EasyCompat? Easy { get; internal set; }

    void EasySceneStarting(Scene scene) => Easy?.SceneStarting(this, scene);

    /// <summary>EasyEngine leaves out objects turned off ("Active" off) and the objects of layers turned off.</summary>
    bool EasySkips(SceneObject so) => Easy != null && Easy.Skips(so);

    void AddEasyMechanisms(SceneObject so, List<string> names, Dictionary<string, Dictionary<string, object?>> overrides)
    {
        if (Easy == null || !EasyLink.HasEasyBehaviors(so)) return;
        foreach (var (name, ps) in Easy.MechanismsFor(this, so))
        {
            MechanismDef? def;
            try { def = TryDef(name); }
            catch (ScriptError e) { SceneError($"'{so.Name}': {e.Message}"); continue; }
            if (def == null || names.Contains(def.Name, StringComparer.OrdinalIgnoreCase)) continue;
            names.Add(def.Name);
            var ov = new Dictionary<string, object?>();
            foreach (var (k, v) in ps)
                if (def.Params.FirstOrDefault(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase)) is { } p) ov[p.Name] = ConvertParam(v, p.Type);
            overrides[def.Name] = ov;
        }
    }

    void EasyObjectMade(GameObject o, SceneObject so) => Easy?.ObjectMade(this, o, so);

    /// <summary>EasyCompat's own commands and functions (easy_…): there for EasyEngine projects, not words to teach or offer.</summary>
    public static bool IsEasyWord(string name) => name.StartsWith("easy_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The objects in the game now (EasyCompat finds objects by name among them), without making a list.</summary>
    internal LiveObjectList EasyObjects() => new(_objects, _pendingAdd);

    /// <summary>The objects not destroyed in two lists (the world's and the ones added this frame), for foreach.</summary>
    internal readonly struct LiveObjectList(List<GameObject> first, List<GameObject> then)
    {
        public Enumerator GetEnumerator() => new(first, then);

        public struct Enumerator(List<GameObject> first, List<GameObject> then)
        {
            int _i = -1;
            bool _second;
            public GameObject Current { get; private set; } = null!;

            public bool MoveNext()
            {
                while (true)
                {
                    var list = _second ? then : first;
                    if (++_i >= list.Count)
                    {
                        if (_second) return false;
                        _second = true;
                        _i = -1;
                        continue;
                    }
                    if (!list[_i].Destroyed) { Current = list[_i]; return true; }
                }
            }
        }
    }

    /// <summary>EasyEngine key names already turned into Builder Script's (<see cref="EasyCompat.Key"/>), so a held key costs a lookup.</summary>
    readonly Dictionary<string, string> _easyKeys = new(StringComparer.Ordinal);

    internal string EasyKey(string ee)
    {
        if (_easyKeys.TryGetValue(ee, out var k)) return k;
        if (_easyKeys.Count >= 512) _easyKeys.Clear(); // keys made from changing texts: don't keep them all
        return _easyKeys[ee] = EasyCompat.Key(ee);
    }

    readonly List<object?> _easyBodies = [];

    /// <summary>A line in the console (prints go the same way).</summary>
    internal void Say(string line) => RaisePrinted(line);

    // ---------------- EasyEngine's dialogue: a queue of lines ----------------

    readonly Queue<Dialogue> _easyLines = new();

    /// <summary>Queues each line of an EasyEngine message (a new line or '|' starts the next one) and returns them.</summary>
    internal List<Dialogue> EasySay(string text, string speaker, MechanismInstance inst)
    {
        var lines = new List<Dialogue>();
        foreach (var raw in text.Split(['|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            var d = new Dialogue { Speaker = Interpolate(speaker, inst), Text = Interpolate(line, inst) };
            lines.Add(d);
            _easyLines.Enqueue(d);
        }
        PumpEasyLines();
        return lines;
    }

    /// <summary>The next queued line shows when nothing else is on the dialogue panel.</summary>
    internal void PumpEasyLines()
    {
        if (Dialogue is { Closed: false }) return;
        while (_easyLines.Count > 0)
        {
            var d = _easyLines.Dequeue();
            if (d.Closed) continue;
            OpenDialogue(d);
            return;
        }
    }

    internal bool EasyTalking => Dialogue is { Closed: false } || _easyLines.Count > 0;

    // ---------------- Animations ----------------

    /// <summary>EasyEngine's "Play an animation": once / loop / auto (back to the automatic one) / pause / resume.</summary>
    internal void EasyAnimate(GameObject o, string name, string mode)
    {
        var st = Easy!.State(o);
        switch (mode)
        {
            case "pause":
                if (o.AnimName != null && o.AnimCount > 0 && !st.Paused)
                {
                    int f = o.AnimDone ? o.AnimCount - 1 : (int)Math.Floor((Time - o.AnimStart) * o.AnimFps + 1e-9);
                    st.Paused = true;
                    st.PausedFrame = ((f % o.AnimCount) + o.AnimCount) % o.AnimCount;
                    o.AnimDone = true; // the frame shown stays (UpdateAnimations skips a finished one)
                    ApplyFrame(o, st.PausedFrame);
                }
                return;
            case "resume":
                if (st.Paused && o.AnimCount > 0) { st.Paused = false; o.AnimDone = false; o.AnimStart = Time - st.PausedFrame / Math.Max(0.1, o.AnimFps); }
                return;
        }
        st.Paused = false;
        if (mode == "auto" || name.Length == 0) { st.Forced = null; return; }
        var a = o.FindAnimation(name);
        if (a == null || a.Frames.Count == 0) { Easy.Note(this, $"an animation called “{name}” on '{o.Name}'"); return; }
        st.Forced = a;
        st.ForcedOnce = mode != "loop";
        PlayNamed(o, a, null, once: st.ForcedOnce, restart: true);
    }

    /// <summary>EasyEngine's automatic animation: a rule's one first, else jump in the air, walk when moving, idle.</summary>
    void EasyAutoAnimate(GameObject o, string idle, string walk, string jump)
    {
        var st = Easy!.State(o);
        if (st.Paused) return;
        if (st.Forced != null)
        {
            if (!(st.ForcedOnce && o.AnimDone)) return;
            st.Forced = null; // a "once" animation ended: back to the automatic one
        }
        string want = !o.OnGround && o.HasBody && jump.Length > 0 ? jump : Math.Abs(o.Vx) > 1 && walk.Length > 0 ? walk : idle;
        if (want.Length == 0 || o.FindAnimation(want) is not { Frames.Count: > 0 } a) return;
        PlayNamed(o, a, null, once: !a.Loop, restart: false);
    }

    // ---------------- Spawning copies ----------------

    /// <summary>A copy of a prefab, or of a scene object with that name (EasyEngine spawns both), at x, y; null when neither exists.</summary>
    internal GameObject? EasySpawn(string name, double x, double y)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        if (SpawnPrefab(name, x, y) is { } p) return p;
        if (Easy != null && Easy.SceneTemplates.TryGetValue(name, out var t)) return SpawnTemplate(t.Name, t, x, y);
        return null;
    }

    // ---------------- The easy_ functions and commands ----------------

    [ModuleInitializer]
    internal static void InstallEasyCompat() => Installers.Add(w =>
    {
        EasyCompat E() => w.Easy ?? throw new ScriptError("This only works in an EasyEngine project.", 0);
        GameObject Me() => w.CallingInst?.Owner ?? throw new ScriptError("This needs an object.", 0);
        string Arg(List<object?> a, int i) => i < a.Count ? ToText(a[i]) : "";

        // values
        w.Functions["easy_on"] = a =>
        {
            string cond = Arg(a, 0);
            return Box(string.IsNullOrWhiteSpace(cond) || E().Eval(w, cond, w.CallingInst?.Owner) != 0);
        };
        w.Functions["easy_value"] = a => E().Eval(w, Arg(a, 0), w.CallingInst?.Owner);
        w.Functions["easy_var"] = a => E().Var(w, Arg(a, 0), w.CallingInst?.Owner);
        w.Functions["easy_text"] = a => E().TextOf(w, Arg(a, 0), w.CallingInst?.Owner);
        w.Functions["easy_compare"] = a => Box(EasyCompat.Compare(EasyCompat.Number(a.Count > 0 ? a[0] : null), Arg(a, 1), EasyCompat.Number(a.Count > 2 ? a[2] : null)));
        w.Functions["easy_bar_max"] = a => Arg(a, 0).Length > 0 ? E().Var(w, Arg(a, 0), null) : (a.Count > 1 ? a[1] : 1.0);
        w.Functions["easy_key"] = a => Box(w.KeysDown.Contains(w.EasyKey(Arg(a, 0))));
        w.Functions["easy_talking"] = _ => Box(w.EasyTalking);
        w.Functions["easy_knocked"] = _ => Box(E().State(Me()).KnockUntil > w.Time);
        w.Functions["easy_dynamic"] = _ => Box(Me().HasBody && Me().Gravity > 0);
        w.Functions["easy_fell"] = _ => Box(Me() is { HasBody: true, Gravity: > 0 } o && o.Y > w.SceneHeight + 320);
        w.Functions["easy_interact_count"] = _ => (double)E().State(Me()).Interactions;
        w.Functions["easy_local"] = a => E().Local(Me(), Arg(a, 0));
        w.Functions["easy_format"] = a => E().Format(w, Arg(a, 0), w.CallingInst?.Owner);
        w.Functions["easy_text_is"] = a => Box(E().TextIs(w, Me(), Arg(a, 0), Arg(a, 1), Arg(a, 2), a.Count > 3 && Truthy(a[3])));
        w.Functions["easy_list_is"] = a => Box(E().ListIs(w, Me(), Arg(a, 0), Arg(a, 1), Arg(a, 2), a.Count > 3 && Truthy(a[3])));

        // objects
        w.Functions["easy_find"] = a => EasyCompat.FindNamed(w, Arg(a, 0));
        w.Functions["easy_hurtable"] = a =>
        {
            string name = Arg(a, 0);
            var me = w.CallingInst?.Owner;
            foreach (var o in w.EasyObjects())
                if (o != me && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) && EasyCompat.HasMechanism(o, "EasyHealth")) return o;
            return null;
        };
        w.Functions["easy_contact"] = a => Box(a.Count >= 2 && a[0] is GameObject x && a[1] is GameObject y && EasyCompat.Contact(x, y));
        w.Functions["easy_touching"] = a => Box(a.Count >= 2 && a[0] is GameObject x && a[1] is GameObject y && EasyCompat.Touching(x, y));
        w.Functions["easy_touch"] = a => EasyCompat.TouchingNamed(w, Me(), Arg(a, 0));
        w.Functions["easy_near"] = a => Box(EasyCompat.Near(w, Me(), Arg(a, 0), a.Count > 1 ? ToNumber(a[1], 0) : 100));
        w.Functions["easy_count"] = a =>
        {
            string family = Arg(a, 0);
            int n = 0;
            foreach (var o in w.EasyObjects()) if (EasyCompat.InFamily(o.Name, family)) n++;
            return (double)n;
        };
        w.Functions["easy_bodies"] = _ =>
        {
            // one list, refilled at each call (for each goes through a copy of it)
            var bodies = w._easyBodies;
            bodies.Clear();
            foreach (var o in w.EasyObjects()) if (o.HasBody && o.Gravity > 0) bodies.Add(o);
            return bodies;
        };
        w.Functions["easy_solid_beside"] = a => Box(EasyCompat.SolidBeside(w, Me(), a.Count > 0 ? ToNumber(a[0], 0) : 1));
        w.Functions["easy_near_pad"] = a => Box(a.Count >= 3 && a[0] is GameObject pad && a[1] is GameObject o && EasyCompat.OnPad(pad, o, Arg(a, 2)));
        w.Functions["easy_latch"] = a =>
        {
            var st = E().State(Me());
            string k = w.EasyKey(Arg(a, 0));
            return Box(st.Latched.Add(k));
        };

        // commands
        w.Commands["easy_set"] = c => c.World.Easy?.SetVar(c.World, c.Self, ToText(c.Arg(0)), c.Arg(1), add: false);
        w.Commands["easy_add"] = c => c.World.Easy?.SetVar(c.World, c.Self, ToText(c.Arg(0)), c.Arg(1), add: true);
        w.Commands["easy_set_local"] = c => c.World.Easy?.SetLocal(c.Self, ToText(c.Arg(0)), c.Arg(1), add: false);
        w.Commands["easy_add_local"] = c => c.World.Easy?.SetLocal(c.Self, ToText(c.Arg(0)), c.Arg(1), add: true);
        w.Commands["easy_unlatch"] = c => c.World.Easy?.State(c.Self).Latched.Remove(c.World.EasyKey(ToText(c.Arg(0))));
        w.Commands["easy_interacted"] = c => { if (c.World.Easy != null) c.World.Easy.State(c.Self).Interactions++; };
        w.Commands["easy_note"] = c => c.World.Easy?.Note(c.World, ToText(c.Arg(0)));
        w.Commands["easy_goto"] = c =>
        {
            string wanted = ToText(c.Arg(0)).Trim();
            if (wanted.Length == 0) return;
            string? match = c.World.SceneNames.FirstOrDefault(n => string.Equals(n.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
            if (match != null) c.World.RequestScene(match); // EasyEngine just stays when there's no such scene
            else c.World.Easy?.Note(c.World, $"a scene called “{wanted}”");
        };
        w.Commands["easy_say"] = c =>
        {
            var world = c.World;
            string text = ToText(c.Arg(0));
            if (text.Trim().Length == 0) return;
            bool freeze = c.Args.Count > 2 && Truthy(c.Args[2]);
            var lines = world.EasySay(text, ToText(c.Arg(1)), c.Mechanism);
            if (lines.Count == 0) return;
            if (freeze) world.LockControls(true);
            c.WaitUntil(() =>
            {
                world.PumpEasyLines();
                foreach (var d in lines) if (!d.Closed) return false;
                if (freeze && world._easyLines.Count == 0) world.LockControls(false);
                return true;
            });
        };
        w.Commands["easy_knock"] = c =>
        {
            var o = c.Self;
            double kb = c.Number(c.Arg(0)), from = c.Number(c.Arg(1));
            if (kb <= 0 || c.World.Easy == null) return;
            double dir = o.X + o.Width / 2 < from ? -1 : 1;
            o.Vx = dir * kb;
            o.Vy = o.HasBody && o.Gravity > 0 ? -kb * 0.6 : 0;
            c.World.Easy.State(o).KnockUntil = c.World.Time + 0.2; // EasyEngine's KnockTime: the movers leave the push alone
        };
        w.Commands["easy_anim"] = c =>
        {
            if (c.World.Easy == null) return;
            var o = c.Arg(0) as GameObject ?? (c.Arg(0) is string s ? EasyCompat.FindNamed(c.World, s) : null);
            if (o != null) c.World.EasyAnimate(o, ToText(c.Arg(1)).Trim(), ToText(c.Arg(2)).Trim().ToLowerInvariant());
        };
        w.Commands["easy_auto_anim"] = c => { if (c.World.Easy != null) c.World.EasyAutoAnimate(c.Self, ToText(c.Arg(0)), ToText(c.Arg(1)), ToText(c.Arg(2))); };
        w.Commands["easy_spawn"] = c => c.World.Easy?.Spawn(c.World, c.Self, ToText(c.Arg(0)), ToText(c.Arg(1)), c.Args.Count > 3 ? c.Number(c.Arg(2)) : null, c.Args.Count > 3 ? c.Number(c.Arg(3)) : null);
        w.Commands["easy_shoot"] = c => c.World.Easy?.Shoot(c.World, c.Self, (int)Math.Round(c.Number(c.Arg(0))), c.Number(c.Arg(1)), c.Number(c.Arg(2)), ToText(c.Arg(3)), ToText(c.Arg(4)));
        w.Commands["easy_speed"] = c => c.World.Easy?.Speed(c.Self, c.Number(c.Arg(0)), multiply: ToText(c.Arg(1)) == "boost");
        w.Commands["easy_ability"] = c =>
        {
            var o = c.Arg(0) as GameObject ?? c.Self;
            c.World.Easy?.Ability(o, ToText(c.Arg(1)), c.Args.Count < 3 || Truthy(c.Args[2]));
        };
        w.Commands["easy_controls"] = c =>
        {
            foreach (var m in c.Self.AllMechanisms())
                if (m.Def.Name.StartsWith("EasyPlatformer") || m.Def.Name.StartsWith("EasyTopDown")) m.Vars["controls"] = Box(Truthy(c.Arg(0)));
        };
        w.Commands["easy_behavior"] = c => c.World.Easy?.SwitchBehavior(c.World, c.Self, ToText(c.Arg(0)), Truthy(c.Arg(1)));
        w.Commands["easy_list"] = c => c.World.Easy?.ListAction(c.World, c.Self, ToText(c.Arg(0)), ToText(c.Arg(1)), ToText(c.Arg(2)), c.Args.Count > 3 && Truthy(c.Args[3]));
        w.Commands["easy_camera_back"] = c =>
        {
            // a cutscene gives the camera back: it glides to what the follow camera follows, then follows it again
            var world = c.World;
            var cam = world.Camera;
            var home = world.LiveObjects().FirstOrDefault(o => o.Mechanisms.Any(m => m.Def.Name == "EasyFollowCamera")) ?? world.FindObject("player");
            if (home == null) { cam.Auto = true; return; }
            double sx = cam.X, sy = cam.Y;
            int id = ++cam.MoveTween;
            cam.Auto = false;
            cam.Target = null;
            Tween(c, Math.Max(0, c.Number(c.Arg(0))), "inout", k =>
            {
                double tx = home.X + home.Width / 2, ty = home.Y + home.Height / 2;
                cam.X = sx + (tx - sx) * k;
                cam.Y = sy + (ty - sy) * k;
                if (k >= 1) { cam.Target = home; cam.MoveTween++; }
            }, () => cam.MoveTween == id && !home.Destroyed);
            if (c.Pending == null) cam.Target = home; // no glide (0 seconds): follow at once
        };
        w.Commands["easy_hold"] = c =>
        {
            // a cutscene holds a key for the player for a while (also while the player is locked)
            var world = c.World;
            string k = world.EasyKey(ToText(c.Arg(0)));
            double until = world.Time + Math.Max(0, c.Number(c.Arg(1)));
            if (k.Length == 0) return;
            if (world.KeysDown.Add(k)) world.PressKey(k);
            c.WaitUntil(() =>
            {
                if (world.Time < until) { world.KeysDown.Add(k); return false; }
                if (!world._held.Contains(k) || world.ControlsLocked) world.KeysDown.Remove(k);
                return true;
            });
        };
    });
}
