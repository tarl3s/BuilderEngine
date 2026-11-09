using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

/// <summary>
/// EasyCompat: an EasyEngine project plays in BuilderEngine. When a world gets ready for a project with EasyEngine
/// behaviors (<see cref="GameBoot.UseProject"/>, shared by the editor's Play and exported games), the EasyKit mechanisms
/// (<c>StarterKit/EasyEngine/*.bs</c>, Builder Script) are loaded, and as each scene loads every object's EasyEngine
/// behaviors become those mechanisms with the behavior's settings as params (<see cref="MechanismsFor"/>). Custom rules
/// (<c>when → do</c>) and cutscene steps are translated into Builder Script mechanisms of their own
/// (EasyCompat.Rules.cs, EasyCompat.Steps.cs). It happens only while playing: the project keeps EasyEngine's data
/// exactly as it was (EasyEngine still owns it). What can't play yet is said once in the console
/// ("EasyEngine: not supported yet: …"), never as an error. The support table is <see cref="Table"/>.
/// </summary>
public sealed partial class EasyCompat
{
    /// <summary>How well an EasyEngine behavior plays in BuilderEngine.</summary>
    public enum Level { Runs, Partly, No }

    /// <summary>One row of the support table: the behavior kind, how well it plays, and what's different.</summary>
    public sealed record Support(string Kind, Level Level, string Note);

    /// <summary>Every EasyEngine behavior kind and how it plays here (docs/EASYENGINE-LINK.md shows the same table).</summary>
    public static readonly IReadOnlyList<Support> Table =
    [
        new("Platformer", Level.Runs, "EasyPlatformer: EasyEngine's speeds, jump, gravity, coyote time, double/wall jump, dash"),
        new("TopDown", Level.Runs, "EasyTopDown"),
        new("Solid", Level.Runs, "EasySolid (one-way too)"),
        new("FollowCamera", Level.Runs, "EasyFollowCamera: same smoothing and scene clamp"),
        new("StayInside", Level.Runs, "EasyStayInside: scene or screen, stop / bounce / wrap"),
        new("Custom", Level.Partly, "translated into a Builder Script mechanism; the few events/actions it can't do are listed in the console"),
        new("Cutscene", Level.Partly, "translated into a Builder Script mechanism; no skip key, dialogue box looks and fonts are the default ones"),
        new("Interact", Level.Partly, "EasyInteract: say / set / add / go to a scene; the dialogue box's look and font are the default ones"),
        new("GoToScene", Level.Runs, "EasyGoToScene"),
        new("SoundEmitter", Level.Partly, "EasySound: plays on start (once or looping); 'only heard nearby' and left/right panning aren't there yet"),
        new("LightSource", Level.Partly, "EasyLight: a round light; beam / half / area / bar shapes show as a circle"),
        new("DayNight", Level.Partly, "EasyDayNight: the light and isDay/isNight/timeOfDay; no sun and moon, no darker underground"),
        new("Spin", Level.Runs, "EasySpin"),
        new("Projectile", Level.Runs, "EasyProjectile"),
        new("Patrol", Level.Runs, "EasyPatrol"),
        new("Health", Level.Runs, "EasyHealth (signals hurt / died for rules)"),
        new("Damager", Level.Runs, "EasyDamager"),
        new("Chase", Level.Runs, "EasyChase"),
        new("Collectible", Level.Runs, "EasyCollectible"),
        new("Checkpoint", Level.Runs, "EasyCheckpoint"),
        new("Shooter", Level.Runs, "EasyShooter"),
        new("Spawner", Level.Runs, "EasySpawner"),
        new("Label", Level.Partly, "EasyLabel: the text in the object; no text effects"),
        new("Bar", Level.Runs, "EasyBar"),
        new("Area", Level.Runs, "EasyArea"),
        new("AbilityPickup", Level.Runs, "EasyAbilityPickup"),
        new("Gate", Level.Runs, "EasyGate"),
        new("Switch", Level.Runs, "EasySwitch"),
        new("Spring", Level.Runs, "EasySpring"),
        new("Parallax", Level.Runs, "EasyParallax"),
        new("DestroyOffscreen", Level.Runs, "EasyDestroyOffscreen"),
        new("Tilemap", Level.Runs, "its tiles become a tile layer when the project opens"),
        new("PathChase", Level.Partly, "plays as Chase (straight at the target, no path round walls)"),
        new("AdvancedPatrol", Level.No, ""),
        new("BossAI", Level.No, ""),
        new("Lives", Level.No, ""),
        new("UIButton", Level.No, ""),
        new("Shop", Level.No, ""),
        new("Pushable", Level.No, ""),
        new("Particles", Level.No, ""),
        new("Hotbar", Level.No, ""),
        new("ToolUse", Level.No, ""),
        new("Harvestable", Level.No, ""),
        new("Terrain", Level.No, ""),
        new("Minimap", Level.No, ""),
        new("Generator", Level.No, ""),
        new("Checklist", Level.No, ""),
    ];

    /// <summary>How a behavior kind plays here (an unknown kind: not at all).</summary>
    public static Support SupportOf(string kind) =>
        Table.FirstOrDefault(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase)) ?? new Support(kind, Level.No, "");

    /// <summary>The project has EasyEngine behaviors somewhere (a scene object or a prefab).</summary>
    public static bool Needed(Project p) =>
        p.Scenes.Any(s => s.Objects.Any(EasyLink.HasEasyBehaviors)) || p.Prefabs.Any(pf => EasyLink.HasEasyBehaviors(pf.Template));

    /// <summary>
    /// Gets a world ready to play the project's EasyEngine behaviors (nothing for a project without them): the EasyKit
    /// mechanisms, the project's variables, EasyEngine's screen size. Called by <see cref="GameBoot.UseProject"/>.
    /// </summary>
    public static void Use(World w, Project p)
    {
        if (!Needed(p)) { w.Easy = null; return; }
        var e = new EasyCompat(p);
        w.Easy = e;
        foreach (var (_, src) in KitSources()) w.Load(src);
        if (p.Easy is JsonObject pe)
        {
            e.SeedVariables(w, pe, overwrite: true);
            double sw = Num(pe, "ScreenWidth", 0), sh = Num(pe, "ScreenHeight", 0);
            if (sw >= 16 && sh >= 16) { w.ViewMaxWidth = sw; w.ViewMaxHeight = sh; } // EasyEngine shows 960 × 540 by default
        }
    }

    /// <summary>The scene a game starts in: an EasyEngine project's start scene ("StartScene"), else the first one.</summary>
    public static Scene FirstScene(Project p)
    {
        if (p.Easy is JsonObject pe && Str(pe, "StartScene", "").Trim() is { Length: > 0 } start
            && p.Scenes.FirstOrDefault(s => string.Equals(s.Name.Trim(), start, StringComparison.OrdinalIgnoreCase)) is { } found)
            return found;
        return p.Scenes[0];
    }

    /// <summary>The EasyKit sources (embedded StarterKit/EasyEngine/*.bs).</summary>
    public static IEnumerable<(string File, string Source)> KitSources()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("EasyKit.")).Order())
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            yield return (name["EasyKit.".Length..], r.ReadToEnd());
        }
    }

    readonly Project _project;
    EasyCompat(Project p) { _project = p; }

    /// <summary>The project's scene names (goto_scene checks them).</summary>
    internal IEnumerable<string> SceneNames => _project.Scenes.Select(s => s.Name);

    // ---------------- What's said once ----------------

    readonly HashSet<string> _noted = new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, string> _generated = new();

    /// <summary>The Builder Script made from the project's Custom rules and cutscenes so far, by mechanism name.</summary>
    public IReadOnlyDictionary<string, string> Generated => _generated;

    /// <summary>Everything said "not supported yet" so far (tests, the snap).</summary>
    public IReadOnlyCollection<string> Unsupported => _noted;

    /// <summary>"EasyEngine: not supported yet: X" in the console, once per game.</summary>
    internal void Note(World w, string what)
    {
        if (!_noted.Add(what)) return;
        string line = "ℹ EasyEngine: not supported yet: " + what;
        w.AddOutput(line);
        w.Say(line);
    }

    // ---------------- Behaviors → mechanisms ----------------

    static readonly string[] MoverKinds = ["Patrol", "Chase", "PathChase", "AdvancedPatrol"];
    static readonly string[] WalkNames = ["walk", "run", "move"], JumpNames = ["jump", "fall", "air"], IdleNames = ["idle", "stand"];

    /// <summary>
    /// The mechanisms (with params) that play an object's EasyEngine behaviors, in the behaviors' order. Translated rules
    /// and cutscenes are loaded into the world as mechanisms of their own the first time they are needed.
    /// </summary>
    internal List<(string Name, Dictionary<string, object?> Params)> MechanismsFor(World w, SceneObject so)
    {
        var list = new List<(string, Dictionary<string, object?>)>();
        if (so.Easy is not JsonObject e || EasyLink.Get(e, "Behaviors") is not JsonArray arr) return list;
        var behaviors = arr.OfType<JsonObject>().ToList();
        var kinds = behaviors.Select(KindOf).ToList();
        bool Has(string k) => kinds.Contains(k, StringComparer.OrdinalIgnoreCase);
        bool driven = kinds.Any(k => MoverKinds.Contains(k, StringComparer.OrdinalIgnoreCase));
        var physics = EasyLink.Get(e, "Physics") as JsonObject;
        string mode = physics != null ? Str(physics, "Mode", "None") : "None";
        double gravity = 2000 * (physics != null ? Num(physics, "GravityScale", 1) : 1);
        var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string mech, Dictionary<string, object?> ps, string? cond = null)
        {
            if (cond is { Length: > 0 }) ps["cond"] = cond;
            list.Add((Repeat(w, mech, count), ps));
        }

        for (int bi = 0; bi < behaviors.Count; bi++)
        {
            var b = behaviors[bi];
            string kind = kinds[bi];
            string cond = T(b, "cond", "").Trim();
            switch (kind)
            {
                case "Platformer":
                    Add("EasyPlatformer", new()
                    {
                        ["controls"] = On(b, "controls", 1), ["speed"] = N(b, "speed", 260), ["jump"] = N(b, "jump", 640),
                        ["double_jump"] = On(b, "doubleJump", 0), ["wall_jump"] = On(b, "wallJump", 0), ["dash"] = On(b, "dash", 0),
                        ["dash_speed"] = N(b, "dashSpeed", 560), ["dash_key"] = Key(T(b, "dashKey", "LeftShift")),
                        ["left_key"] = Key(T(b, "leftKey", "")), ["right_key"] = Key(T(b, "rightKey", "")), ["jump_key"] = Key(T(b, "jumpKey", "")),
                        ["collide"] = On(b, "collide", 1) && !Has("TopDown"), ["gravity"] = gravity, ["driven"] = driven, ["fall_dies"] = !Has("Health"),
                    }, cond);
                    break;
                case "TopDown":
                    Add("EasyTopDown", new()
                    {
                        ["controls"] = On(b, "controls", 1), ["speed"] = N(b, "speed", 220), ["collide"] = On(b, "collide", 1),
                        ["up_key"] = Key(T(b, "upKey", "")), ["down_key"] = Key(T(b, "downKey", "")),
                        ["left_key"] = Key(T(b, "leftKey", "")), ["right_key"] = Key(T(b, "rightKey", "")), ["driven"] = driven,
                    }, cond);
                    break;
                case "Solid":
                    Add("EasySolid", new() { ["one_way"] = On(b, "oneWay", 0) });
                    break;
                case "FollowCamera":
                {
                    double s = Math.Clamp(N(b, "smooth", 0.12), 0.01, 1);
                    Add("EasyFollowCamera", new() { ["rate"] = s >= 0.999 ? 0 : -Math.Log(1 - s) * 60, ["clamp"] = On(b, "clamp", 1) });
                    break;
                }
                case "StayInside":
                    Add("EasyStayInside", new() { ["area"] = T(b, "area", "scene"), ["sides"] = T(b, "sides", "all"), ["edge"] = T(b, "edge", "stop") });
                    break;
                case "GoToScene":
                    Add("EasyGoToScene", new() { ["scene"] = T(b, "scene", ""), ["trigger"] = T(b, "trigger", "Player") }, cond);
                    break;
                case "Interact":
                {
                    string action = T(b, "action", "say").ToLowerInvariant();
                    if (action == "say") NoteLook(w, b);
                    Add("EasyInteract", new()
                    {
                        ["action"] = action, ["text"] = Text(T(b, "text", "Hello!")), ["speaker"] = Text(T(b, "speaker", "")),
                        ["var"] = T(b, "var", ""), ["set_to"] = N(b, "to", 1), ["amount"] = N(b, "amount", 1), ["scene"] = T(b, "scene", ""),
                        ["key"] = Key(T(b, "key", "E")), ["trigger"] = T(b, "trigger", "Player"), ["radius"] = N(b, "radius", 60),
                        ["once"] = On(b, "once", 0), ["freeze"] = On(b, "freeze", 0),
                    }, cond);
                    break;
                }
                case "Area":
                    Add("EasyArea", new()
                    {
                        ["action"] = T(b, "action", "goscene").ToLowerInvariant(), ["scene"] = T(b, "scene", ""), ["var"] = T(b, "var", ""),
                        ["set_to"] = N(b, "to", 1), ["amount"] = N(b, "amount", 1), ["text"] = Text(T(b, "text", "")),
                        ["target"] = T(b, "target", "Player"), ["once"] = On(b, "once", 1),
                    }, cond);
                    break;
                case "SoundEmitter":
                    if (On(b, "range", 0)) Note(w, "Sound: 'only heard nearby' (it plays at its own volume)");
                    if (On(b, "pan", 0)) Note(w, "Sound: left / right in the headphones");
                    Add("EasySound", new() { ["asset"] = T(b, "asset", ""), ["volume"] = N(b, "volume", 0.8), ["on_start"] = On(b, "onStart", 1), ["loop"] = On(b, "loop", 0) });
                    break;
                case "LightSource":
                {
                    string shape = T(b, "shape", "circle").ToLowerInvariant();
                    if (shape is not ("" or "circle")) Note(w, $"Light shape '{shape}' (it shines as a circle)");
                    string effect = T(b, "effect", "none").ToLowerInvariant();
                    if (effect is "pulse" or "blink") Note(w, $"Light effect '{effect}'");
                    Add("EasyLight", new() { ["radius"] = N(b, "radius", 200), ["color"] = T(b, "color", "#FFA040"), ["flicker"] = effect == "flicker" ? 0.35 : 0.0 });
                    break;
                }
                case "DayNight":
                    if (On(b, "coverDark", 0)) Note(w, "Day / Night: darker underground");
                    Add("EasyDayNight", new()
                    {
                        ["day"] = N(b, "dayLength", 60), ["night"] = N(b, "nightLength", 60), ["fade"] = N(b, "transition", 4),
                        ["day_light"] = N(b, "dayLight", 1), ["night_light"] = N(b, "nightLight", 0.25), ["start_at_night"] = On(b, "startAtNight", 0),
                        ["color"] = On(b, "trueBlack", 0) ? "#000000" : T(b, "nightColor", "#0A1030"),
                    });
                    break;
                case "Spin":
                    Add("EasySpin", new() { ["speed"] = N(b, "speed", 90) });
                    break;
                case "Projectile":
                    Add("EasyProjectile", new() { ["speed"] = N(b, "speed", 420), ["angle"] = N(b, "angle", 0), ["life"] = N(b, "life", 3) });
                    break;
                case "Patrol":
                    Add("EasyPatrol", new()
                    {
                        ["speed"] = N(b, "speed", 90), ["distance"] = N(b, "distance", 200), ["vertical"] = On(b, "axis", 0),
                        ["smooth"] = On(b, "smooth", 0), ["slow_steps"] = N(b, "slowSteps", 0), ["pause"] = N(b, "pause", 0),
                        ["has_chase"] = Has("Chase") || Has("PathChase"),
                    }, cond);
                    break;
                case "Chase":
                case "PathChase":
                    if (kind == "PathChase") Note(w, "Smart Chase finding its way round walls (it chases in a straight line)");
                    if (kind == "PathChase" && Has("Chase")) break; // one chaser per object
                    Add("EasyChase", new() { ["target"] = T(b, "target", "Player"), ["speed"] = N(b, "speed", 140), ["range"] = N(b, "range", 0), ["driven"] = Has("Patrol") }, cond);
                    break;
                case "Health":
                    Add("EasyHealth", new()
                    {
                        ["max_health"] = N(b, "max", 3), ["invincible"] = N(b, "invincible", 1), ["respawn"] = On(b, "respawn", 1),
                        ["var"] = T(b, "var", "health"), ["publish"] = PublishesHealth(so, T(b, "var", "health")),
                    });
                    break;
                case "Damager":
                    Add("EasyDamager", new() { ["damage"] = N(b, "damage", 1), ["knockback"] = N(b, "knockback", 320), ["vanish"] = On(b, "vanish", 0), ["target"] = T(b, "target", "Player") }, cond);
                    break;
                case "Collectible":
                    Add("EasyCollectible", new()
                    {
                        ["collector"] = T(b, "collector", "Player"), ["var"] = T(b, "var", ""), ["points"] = N(b, "points", 1),
                        ["item"] = T(b, "item", ""), ["amount"] = N(b, "amount", 1),
                    }, cond);
                    break;
                case "Checkpoint":
                    Add("EasyCheckpoint", new() { ["target"] = T(b, "target", "Player") });
                    break;
                case "Shooter":
                    Add("EasyShooter", new()
                    {
                        ["key"] = Key(T(b, "key", "T")), ["directions"] = N(b, "directions", 8), ["speed"] = N(b, "speed", 420),
                        ["cooldown"] = N(b, "cooldown", 0.3), ["life"] = N(b, "life", 2), ["bullet"] = T(b, "bullet", "Bullet"), ["aim"] = T(b, "aim", ""),
                    }, cond);
                    break;
                case "Spawner":
                    Add("EasySpawner", new()
                    {
                        ["target"] = T(b, "target", "Enemy"), ["interval"] = N(b, "interval", 1.5), ["max_count"] = N(b, "max", 10),
                        ["key"] = Key(T(b, "key", "")), ["spawn_at"] = T(b, "at", "self"),
                    }, cond);
                    break;
                case "Label":
                    if (T(b, "effect", "none") is { } fx && fx != "none" && fx != "") Note(w, $"Label effect '{fx}'");
                    Add("EasyLabel", new() { ["text"] = Text(T(b, "text", "Score: {score}")), ["color"] = T(b, "color", "#FFFFFF"), ["font_size"] = N(b, "fontSize", 20) });
                    break;
                case "Bar":
                    Add("EasyBar", new()
                    {
                        ["var"] = T(b, "var", "health"), ["max_value"] = N(b, "max", 3), ["max_var"] = T(b, "maxVar", ""),
                        ["color"] = T(b, "color", "#3BC97A"), ["on_screen"] = so.Tags.Contains(EasyLink.UiTag, StringComparer.OrdinalIgnoreCase),
                    });
                    break;
                case "AbilityPickup":
                    Add("EasyAbilityPickup", new() { ["var"] = T(b, "var", "hasDoubleJump"), ["collector"] = T(b, "collector", "Player"), ["grant"] = T(b, "grant", "none") }, cond);
                    break;
                case "Gate":
                    Add("EasyGate", new() { ["var"] = T(b, "var", "hasKey"), ["op"] = T(b, "op", ">="), ["value"] = N(b, "value", 1), ["item"] = T(b, "item", "") }, cond);
                    break;
                case "Switch":
                    Add("EasySwitch", new()
                    {
                        ["var"] = T(b, "var", "switch1"), ["mode"] = T(b, "mode", "click").ToLowerInvariant(), ["trigger"] = T(b, "trigger", "Player"),
                        ["key"] = Key(T(b, "key", "E")), ["stay_on"] = On(b, "stayOn", 0),
                    }, cond);
                    break;
                case "Spring":
                    Add("EasySpring", new() { ["force"] = N(b, "force", 820), ["dir"] = T(b, "dir", "up").ToLowerInvariant() }, cond);
                    break;
                case "Parallax":
                    Add("EasyParallax", new() { ["factor"] = N(b, "factor", 0.5) });
                    break;
                case "DestroyOffscreen":
                    Add("EasyDestroyOffscreen", new() { ["margin"] = N(b, "margin", 120) });
                    break;
                case "Custom":
                    if (RulesMechanism(w, so, T(b, "script", "[]")) is { } rules) list.Add((rules, new()));
                    break;
                case "Cutscene":
                    if (CutsceneMechanism(w, so, b) is { } cut) list.Add((cut, new()));
                    break;
                case "Tilemap":
                    break; // its tiles become a tile layer when the project opens (EasyLink)
                default:
                    Note(w, Words(kind));
                    break;
            }
        }

        bool mover = Has("Platformer") || Has("TopDown");
        if (!mover && mode.Equals("Dynamic", StringComparison.OrdinalIgnoreCase))
            Add("EasyBody", new() { ["gravity"] = gravity, ["fall_dies"] = !Has("Health") }); // physics "Dynamic": it falls
        if (mode.Equals("Static", StringComparison.OrdinalIgnoreCase) && !Has("Solid") && !Has("Tilemap"))
            Add("EasySolid", new() { ["one_way"] = false }); // physics "Static": a solid
        if ((mover || driven) && AutoAnimations(so) is { } anim)
            Add("EasyAutoAnim", new() { ["idle"] = anim.Idle, ["walk"] = anim.Walk, ["jump"] = anim.Jump });
        return list;
    }

    /// <summary>The k-th behavior of a kind on one object plays as a variant of its mechanism ("EasyInteract_2"), made once.</summary>
    static string Repeat(World w, string mech, Dictionary<string, int> count)
    {
        int k = count[mech] = count.GetValueOrDefault(mech) + 1;
        if (k == 1) return mech;
        string name = $"{mech}_{k}";
        if (!w.Library.ContainsKey(name)) w.Load($"mechanism {name} from {mech}\n");
        return name;
    }

    /// <summary>The idle / walk / jump animations EasyEngine would pick by itself, or null when it has no walk or jump one.</summary>
    static (string Idle, string Walk, string Jump)? AutoAnimations(SceneObject so)
    {
        if (so.Animations.Count == 0) return null;
        string Find(string[] names) => so.Animations.FirstOrDefault(a => names.Contains(a.Name.Trim(), StringComparer.OrdinalIgnoreCase))?.Name ?? "";
        string walk = Find(WalkNames), jump = Find(JumpNames);
        if (walk.Length == 0 && jump.Length == 0) return null;
        string idle = Find(IdleNames);
        if (idle.Length == 0) idle = so.ResolveDefaultAnimation()?.Name ?? "";
        return (idle, walk, jump);
    }

    void NoteLook(World w, JsonObject b)
    {
        string look = T(b, "look", "theme");
        if (look is not ("" or "theme")) Note(w, $"dialogue box look '{look}' (the default box is used)");
        if (T(b, "font", "").Length > 0) Note(w, "dialogue fonts (the default font is used)");
    }

    // ---------------- The scene ----------------

    /// <summary>Which object writes each Health variable in this scene: the most player-like (EasyEngine's rule).</summary>
    readonly Dictionary<string, SceneObject> _healthOwners = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The scene's objects by name: EasyEngine spawns copies of scene objects as well as prefabs.</summary>
    internal readonly Dictionary<string, SceneObject> SceneTemplates = new(StringComparer.OrdinalIgnoreCase);

    internal void SceneStarting(World w, Scene scene)
    {
        _healthOwners.Clear();
        SceneTemplates.Clear();
        ResetSceneState();
        ReadLayers(scene);
        foreach (var so in scene.Objects)
        {
            SceneTemplates.TryAdd(so.Name, so);
            if (so.Easy is not JsonObject e || EasyLink.Get(e, "Behaviors") is not JsonArray arr) continue;
            foreach (var b in arr.OfType<JsonObject>())
            {
                if (KindOf(b) != "Health") continue;
                string v = T(b, "var", "health");
                if (!_healthOwners.TryGetValue(v, out var cur) || PlayerLikeness(so) > PlayerLikeness(cur)) _healthOwners[v] = so;
            }
        }
        if (scene.Easy is JsonObject se)
        {
            SeedVariables(w, se, overwrite: true);
            double ambient = Num(se, "AmbientLight", 1);
            if (ambient < 0.999) { w.Darkness = Math.Clamp(1 - ambient, 0, 1); w.DarkColor = "#000000"; }
        }
    }

    bool PublishesHealth(SceneObject so, string var) => !_healthOwners.TryGetValue(var, out var owner) || ReferenceEquals(owner, so);

    /// <summary>3 = the camera follows it, 2 = the keys drive it, 1 = its type is Player, 0 = anything else.</summary>
    static int PlayerLikeness(SceneObject so)
    {
        var kinds = EasyLink.EasyBehaviors(so);
        if (kinds.Contains("FollowCamera")) return 3;
        if (so.Easy is JsonObject e && EasyLink.Get(e, "Behaviors") is JsonArray arr
            && arr.OfType<JsonObject>().Any(b => KindOf(b) is "Platformer" or "TopDown" && On(b, "controls", 1))) return 2;
        return so.Easy is JsonObject e2 && Str(e2, "Type", "") == "Player" ? 1 : 0;
    }

    /// <summary>EasyEngine's number, yes/no and text variables (and lists) become global variables of the same names.</summary>
    void SeedVariables(World w, JsonObject holder, bool overwrite)
    {
        void Put(string name, object? v)
        {
            if (name.Length == 0) return;
            if (overwrite || !w.Globals.ContainsKey(name)) w.Globals[name] = v;
        }
        if (EasyLink.Get(holder, "Variables") is JsonObject vars)
            foreach (var (k, v) in vars) Put(k, Project.Value(v) is double d ? d : 0.0);
        if (EasyLink.Get(holder, "Flags") is JsonArray flags)
            foreach (var f in flags) if (Project.Value(f) is string name) Put(name, 1.0);
        if (EasyLink.Get(holder, "Texts") is JsonObject texts)
            foreach (var (k, v) in texts) Put(k, Project.Value(v) is { } t ? World.ToText(t) : "");
        if (EasyLink.Get(holder, "Lists") is JsonObject lists)
            foreach (var (k, v) in lists)
                Put(k, v is JsonArray a ? a.Select(x => (object?)World.ToText(Project.Value(x))).ToList() : new List<object?>());
    }

    // ---------------- Small helpers ----------------

    internal static string KindOf(JsonObject b) =>
        EasyLink.Get(b, "Kind") is JsonValue k ? (k.TryGetValue<string>(out var s) ? s : k.ToJsonString()) : "";

    static JsonObject? Bag(JsonObject b, string name) => EasyLink.Get(b, name) as JsonObject;

    /// <summary>A behavior's number (its "Nums"), a number written as text too.</summary>
    internal static double N(JsonObject b, string key, double def)
    {
        if (Bag(b, "Nums") is { } nums && EasyLink.Get(nums, key) is { } n && Project.Value(n) is { } v)
            return v switch
            {
                double d => d,
                bool x => x ? 1 : 0,
                string s when double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => def,
            };
        return def;
    }

    internal static bool On(JsonObject b, string key, double def) => N(b, key, def) >= 0.5;

    /// <summary>A behavior's text (its "Texts").</summary>
    internal static string T(JsonObject b, string key, string def) =>
        Bag(b, "Texts") is { } texts && EasyLink.Get(texts, key) is { } t && Project.Value(t) is { } v ? World.ToText(v) : def;

    static string Str(JsonObject o, string key, string def) => EasyLink.Get(o, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;
    static double Num(JsonObject o, string key, double def) => Project.Value(EasyLink.Get(o, key)) is double d ? d : def;

    /// <summary>An EasyEngine key (WPF's names: "E", "Space", "LeftShift", "Return", "D1") as Builder Script names it.</summary>
    internal static string Key(string ee)
    {
        string k = ee.Trim().ToLowerInvariant();
        return k switch
        {
            "" or "none" => "",
            "return" => "enter",
            "esc" => "escape",
            "shift" => "leftshift",
            "ctrl" or "control" => "leftctrl",
            _ when k.Length == 2 && k[0] == 'd' && char.IsAsciiDigit(k[1]) => k[1..],
            _ when k.StartsWith("numpad") && k.Length == 7 && char.IsAsciiDigit(k[6]) => k[6..],
            _ => k,
        };
    }

    /// <summary>"FollowCamera" → "Follow Camera".</summary>
    internal static string Words(string kind)
    {
        var sb = new StringBuilder(kind.Length + 4);
        for (int i = 0; i < kind.Length; i++)
        {
            if (i > 0 && char.IsUpper(kind[i]) && !char.IsUpper(kind[i - 1])) sb.Append(' ');
            sb.Append(kind[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A text with EasyEngine's {name} parts as Builder Script shows them: a plain name stays ({score}); any other
    /// ({item:wood}, {local.hp}, {count:quests}) is read by easy_text, EasyEngine's own way.
    /// </summary>
    internal static string Text(string ee)
    {
        ee = ee.Replace("\r\n", "\n").Replace('\r', '\n');
        if (ee.IndexOf('{') < 0) return ee;
        return System.Text.RegularExpressions.Regex.Replace(ee, @"\{([^{}]+)\}", m =>
        {
            string key = m.Groups[1].Value.Trim();
            return IsName(key) ? "{" + key + "}" : "{easy_text(" + Printer.Quote(key) + ")}";
        });
    }

    /// <summary>A name Builder Script can use as a variable (not one of its words).</summary>
    internal static bool IsName(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_')
        && !Parser.Keywords.Contains(s) && s is not ("self" or "other" or "time" or "dt" or "here" or "none" or "nothing");

    /// <summary>A short stable name part from some text (the same rules always make the same mechanism).</summary>
    internal static string Hash(string text)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(h, 0, 4);
    }
}
