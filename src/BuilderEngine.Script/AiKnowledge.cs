using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BuilderEngine.Script;

/// <summary>
/// What the AI is told about Builder Script. The rules are the cheat sheet of docs/MVP.md in a few lines; the word,
/// command and function lists come from the language itself (<see cref="Parser.Keywords"/>, <see cref="World.BuiltInCommands"/>,
/// <see cref="World.BuiltInFunctions"/>, the engine's <see cref="World.Commands"/>), and the examples are the starter kit's
/// own mechanisms, so the prompt never drifts from what the engine really runs.
/// </summary>
public static class AiKnowledge
{
    /// <summary>The language in a few lines (from docs/MVP.md).</summary>
    public const string Rules =
        "BUILDER SCRIPT RULES\n" +
        "- Indent with 4 spaces, no braces, no semicolons. A mechanism starts with `mechanism Name` (CamelCase); inside it only `param`, `uses`, `when` and `to` lines.\n" +
        "- `param speed: number = 100` — a value tuned in the inspector (types: number, seconds, yesno, text, Sound, Image, Scene).\n" +
        "- `uses Body` / `uses Health max 3` — reuse another mechanism and set its params.\n" +
        "- Events: `when start`, `when tick` (every frame; `dt` = seconds since the last one), `when touched by \"player\"` (a tag or a name; `other` is what touched), " +
        "`when key \"space\"`, `when told \"damage\" amount`, `when Health.died` (a signal of a used mechanism), `when died` (its own signal).\n" +
        "- Inside: `hp -= 1`, `score += value` (a new name is this object's variable; names never set on the object are global, like score), " +
        "`if cond` / `else` / `else if`, one-line `if hp <= 0: stop`, `repeat 3`, `repeat forever`, `while cond`, `for each e in all(\"enemy\")`, " +
        "`wait 0.5`, `wait until cond`, `wait for key \"space\"`, `emit died`, `tell other \"damage\" 1`, `stop`, `together` (lines at the same time), `run` (in the background).\n" +
        "- `to slide_to x, seconds` makes your own command (a block like `when`); call it `slide_to 400, 1`; `return v` makes it a value: `damage_for(3)`.\n" +
        "- Values: numbers, \"text\" (\"Score: {score}\" fills in values), true/false, lists [1, 2]; and/or/not, == != < > <= >=, + - * / %. " +
        "`self.x self.y self.vx self.vy self.width self.height self.on_ground`, `visible = false`, `key(\"left\")` is true while held.\n" +
        "- Physics: `body 1500, 1000` (gravity, max fall) or `uses Body`; `solid`; `oneway`; `jump 100` (px high, only when standing; needs Body); `push 300, -200`; " +
        "walking = set `self.vx` (px/s, + is right). Platformer = left/right + jump, already made.\n";

    /// <summary>One line per engine command family, as written in scripts.</summary>
    public const string CommandExamples =
        "move Boss to 400, 300 in 2 seconds ease out · move by 0, -50 in 0.5 · move 10, 0 (instant) · resize to 32, 0 in 1 · fade Ghost to 0.3 in 1\n" +
        "camera follow player · camera move to 600, 300 in 2 · camera zoom 2 in 1 · camera shake 6 for 0.5 · camera limits off\n" +
        "say Boss \"You came...\" · say \"Hi!\" · narrate \"Long ago...\" · ask \"Join me?\" \"Yes\", \"No\" (then `answer`) · notify \"Checkpoint!\"\n" +
        "fade out in 1 · fade in in 1 · flash \"red\" · slow motion 0.3 for 2 · pause game · resume game · lock controls · unlock controls\n" +
        "show text \"Score: {score}\" at 16, 16 · show bar hp of max at self · hide text \"Score: {score}\" · hide hud\n" +
        "look \"hero.png\" · animate \"walk\" at 8 · face left · face player · layer 2 · play \"coin.wav\"\n" +
        "spawn Coin here · spawn Bullet self.x, self.y · destroy self · destroy other · goto_scene \"Level 2\" · save game · load game · save \"coins\", 12 · saved(\"coins\")\n" +
        "nearest(\"enemy\") · all(\"coin\") · count(\"enemy\") · find(\"Boss\") · distance(self, other) · random(1, 6) · min(a, b) · length(list) · add x to list\n" +
        "set_tile x, y to \"dirt\" · darkness 0.9 · light 140 · when clicked · draggable to \"slot\" · give \"wood\", 3 · has(\"wood\", 2) · shuffle deck\n";

    static string? _vocabulary;

    /// <summary>Every word, command and function the language knows, generated from the engine.</summary>
    public static string Vocabulary()
    {
        if (_vocabulary != null) return _vocabulary;
        var w = new World();
        w.UseScenes(["Level 1"]);
        var engine = w.Commands.Keys.Order(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.Append("WORDS: ").AppendLine(string.Join(" ", Parser.Keywords));
        sb.Append("COMMANDS: ").AppendLine(string.Join(", ", World.BuiltInCommands.Concat(engine).Distinct(StringComparer.OrdinalIgnoreCase)));
        sb.Append("FUNCTIONS: ").AppendLine(string.Join(", ", World.BuiltInFunctions.Concat(w.Functions.Keys).Distinct(StringComparer.OrdinalIgnoreCase)));
        return _vocabulary = sb.ToString();
    }

    // ---------------- The starter kit ----------------

    public sealed record KitMechanism(string Name, string Source, MechanismDef Def);

    static List<KitMechanism>? _kit;

    /// <summary>The starter kit, one entry per mechanism.</summary>
    public static IReadOnlyList<KitMechanism> Kit()
    {
        if (_kit != null) return _kit;
        var list = new List<KitMechanism>();
        foreach (var (file, src) in StarterKit.Sources())
            foreach (var (name, source) in AiMerge.Split(src))
            {
                if (GameKitFiles.Contains(file)) GameKitNames.Add(name);
                try { list.Add(new KitMechanism(name, source, Parser.Parse(source)[0])); }
                catch (ScriptError) { }
            }
        return _kit = list;
    }

    /// <summary>One line per mechanism: name, params, what it can be told, what it emits, its commands.</summary>
    public static string Describe(MechanismDef d)
    {
        var parts = new List<string>();
        if (d.Params.Count > 0) parts.Add("params " + string.Join(", ", d.Params.Select(p => p.Name)));
        if (d.Uses.Count > 0) parts.Add("uses " + string.Join(", ", d.Uses.Select(u => u.Name)));
        var told = d.Whens.Where(x => x.Event.Kind == EventKind.Told).Select(x => $"\"{x.Event.Filter}\"").Distinct().ToList();
        if (told.Count > 0) parts.Add("told " + string.Join(", ", told));
        var emits = new HashSet<string>();
        foreach (var wd in d.Whens) CollectEmits(wd.Body, emits);
        if (emits.Count > 0) parts.Add("emits " + string.Join(", ", emits));
        if (d.Procs.Count > 0) parts.Add("commands " + string.Join("; ", d.Procs.Select(p => p.Params.Count == 0 ? p.Name : p.Name + " " + string.Join(", ", p.Params))));
        return parts.Count == 0 ? d.Name : $"{d.Name} ({string.Join("; ", parts)})";
    }

    static void CollectEmits(List<Stmt> body, HashSet<string> into)
    {
        foreach (var s in AiCheck.AllStatements(body))
            if (s is EmitStmt e) into.Add(e.Signal);
    }

    /// <summary>The starter kit in one line per mechanism, so the AI can `uses` them.</summary>
    public static string KitSummary() => "STARTER KIT (use with `uses Name`): " + string.Join(" · ", Kit().Where(k => !GameKitNames.Contains(k.Name)).Select(k => Describe(k.Def))) +
        "\nGAME KIT: " + string.Join(", ", Kit().Where(k => GameKitNames.Contains(k.Name)).Select(k => k.Name));

    /// <summary>The game kit's files: their mechanisms are listed by name only, to keep the prompt small (examples show the ones a request needs).</summary>
    static readonly HashSet<string> GameKitFiles = new(StringComparer.OrdinalIgnoreCase) { "Tiles.bs", "Lighting.bs", "Mouse.bs", "Ui.bs", "Items.bs", "Cards.bs" };
    static readonly HashSet<string> GameKitNames = new(StringComparer.OrdinalIgnoreCase);

    // ---------------- Picking examples by word overlap ----------------

    /// <summary>Request words that point at a starter mechanism even when its code doesn't contain them.</summary>
    static readonly (string[] Words, string Mechanism)[] Hints =
    [
        (["jump", "jumps", "platformer", "platform", "gravity", "fall", "double"], "Platformer"),
        (["coin", "collect", "pickup", "pick", "score", "point", "points", "gem", "star"], "Coin"),
        (["enemy", "patrol", "back", "forth", "walk", "walks", "guard", "slime"], "Patrol"),
        (["health", "hp", "life", "lives", "damage", "hurt", "die", "dies", "dead", "heal", "hit", "spike"], "Health"),
        (["physics", "body", "falls", "crate", "box", "push"], "Body"),
        (["camera", "follow", "zoom"], "FollowCamera"),
        (["talk", "talks", "dialog", "dialogue", "npc", "say", "says", "speak", "conversation"], "Npc"),
        (["cutscene", "cinematic", "intro", "story"], "Cutscene"),
        (["door", "level", "scene", "portal", "exit", "teleport"], "Door"),
        (["text", "label", "hud", "show", "display"], "Label"),
        (["bar", "healthbar"], "HealthBar"),
        (["save", "checkpoint", "respawn"], "Checkpoint"),
        (["picture", "sprite", "image", "look"], "Sprite"),
        (["animation", "animate", "animated", "frames"], "Animator"),
        (["blink", "flash", "flicker", "invisible", "hide"], "Blink"),
        (["move", "arrows", "topdown", "wasd", "steer"], "PlayerMove"),
        (["solid", "wall", "ground", "floor"], "Solid"),
        (["oneway", "through"], "OneWay"),
        (["tile", "tiles", "mine", "mining", "dig", "digs", "sandbox", "minecraft", "terraria"], "TileMiner"),
        (["world", "generate", "generated", "terrain", "hills", "caves", "ore", "ores", "procedural"], "WorldGen"),
        (["cave", "underground", "dungeon", "rock", "rocks"], "CaveGen"),
        (["light", "lights", "dark", "darkness", "torch", "lamp", "flashlight", "glow"], "Light"),
        (["night", "day", "sun", "sunlight"], "DayNight"),
        (["drag", "drop", "dragged", "dropped", "mouse", "slot"], "Draggable"),
        (["click", "clicked", "clicks", "button", "buttons"], "Button"),
        (["menu", "options", "pause"], "Menu"),
        (["inventory", "item", "items", "pickup", "collect"], "Pickup"),
        (["craft", "crafting", "recipe", "recipes"], "Crafting"),
        (["hotbar", "slots", "toolbar"], "Hotbar"),
        (["card", "cards", "flip", "hand"], "Card"),
        (["deck", "draw", "shuffle", "deal"], "Deck"),
        (["turn", "turns", "rounds"], "TurnManager"),
        (["security", "cameras", "monitor", "watch", "cctv"], "CameraMonitor"),
        (["topdown", "zelda", "rpg"], "TopDown"),
    ];

    /// <summary>Portuguese / Spanish words a request may still have (when the translation was skipped) → English.</summary>
    static readonly Dictionary<string, string> Foreign = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pulo"] = "jump", ["pular"] = "jump", ["pula"] = "jump", ["salto"] = "jump", ["saltar"] = "jump", ["moeda"] = "coin", ["moneda"] = "coin",
        ["inimigo"] = "enemy", ["enemigo"] = "enemy", ["vida"] = "health", ["dano"] = "damage", ["daño"] = "damage", ["chefe"] = "boss",
        ["jefe"] = "boss", ["porta"] = "door", ["puerta"] = "door", ["camera"] = "camera", ["câmera"] = "camera", ["plataforma"] = "platform",
        ["andar"] = "walk", ["anda"] = "walk", ["piscar"] = "blink", ["pisca"] = "blink", ["patrulha"] = "patrol", ["texto"] = "text",
        ["placar"] = "score", ["pontos"] = "points", ["cena"] = "scene", ["fase"] = "level", ["falar"] = "talk", ["fala"] = "talk",
        ["diálogo"] = "dialogue", ["dialogo"] = "dialogue", ["animação"] = "animation", ["animacao"] = "animation", ["tiro"] = "shoot",
        ["atirar"] = "shoot", ["atira"] = "shoot", ["salvar"] = "save", ["imagem"] = "picture", ["figura"] = "picture", ["gravidade"] = "gravity",
        ["cair"] = "fall", ["parede"] = "wall", ["chão"] = "ground", ["chao"] = "ground", ["barra"] = "bar", ["mover"] = "move", ["setas"] = "arrows",
    };

    /// <summary>The lower-case words of a text, Portuguese/Spanish stems mapped to English, a plural 's' dropped.</summary>
    public static HashSet<string> Words(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text ?? "", @"[\p{L}_]+"))
        {
            // CamelCase names count by their parts too ("DoubleJump" → double, jump)
            foreach (var part in Regex.Split(m.Value, @"(?<=\p{Ll})(?=\p{Lu})|_").Prepend(m.Value))
            {
                string w = part.ToLowerInvariant();
                if (w.Length < 2) continue;
                if (Foreign.TryGetValue(w, out var en)) w = en;
                set.Add(w);
                if (w.Length > 3 && w.EndsWith('s')) set.Add(w[..^1]);
            }
        }
        return set;
    }

    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "to", "of", "it", "is", "in", "on", "at", "by", "for", "with", "make", "me", "my", "that", "this",
        "when", "if", "else", "self", "param", "number", "mechanism", "uses", "true", "false", "not", "can", "add", "one", "so", "player", "object", "it",
    };

    /// <summary>The starter mechanisms closest to a request (word overlap + hint words), best first.</summary>
    public static List<KitMechanism> PickExamples(string request, int count, IEnumerable<string>? exclude = null, int maxChars = 1200)
    {
        if (count <= 0) return [];
        var words = Words(request);
        words.ExceptWith(Common);
        var skip = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        var scored = new List<(KitMechanism K, double Score)>();
        foreach (var k in Kit())
        {
            if (skip.Contains(k.Name) || k.Source.Length > maxChars) continue;
            double score = 0;
            var nameWords = Words(k.Name);
            var codeWords = Words(k.Source);
            foreach (var w in words)
            {
                if (nameWords.Contains(w)) score += 4;
                else if (codeWords.Contains(w)) score += 1;
            }
            foreach (var (hintWords, mech) in Hints)
                if (mech == k.Name && hintWords.Any(words.Contains)) score += 3;
            if (score > 0) scored.Add((k, score));
        }
        var picked = scored.OrderByDescending(x => x.Score).ThenBy(x => x.K.Source.Length).Select(x => x.K).Take(count).ToList();
        // Nothing matched: the two that show the most of the language in the fewest lines.
        foreach (var fallback in new[] { "Coin", "Health" })
            if (picked.Count < count && Kit().FirstOrDefault(k => k.Name == fallback) is { } f && !picked.Contains(f) && !skip.Contains(f.Name)) picked.Add(f);
        return picked;
    }

    /// <summary>A mechanism name made from a request: "make a double jump" → DoubleJump (null when nothing fits).</summary>
    public static string? SuggestName(string request)
    {
        var quoted = Regex.Match(request ?? "", @"\b(?:called|named|name it)\s+[""']?([A-Za-z][A-Za-z0-9_]*)");
        if (quoted.Success) return Capitalize(quoted.Groups[1].Value);
        var skip = new HashSet<string>(Common, StringComparer.OrdinalIgnoreCase)
        { "mechanism", "that", "which", "who", "something", "thing", "object", "please", "makes", "create", "write", "give", "want", "would", "like", "could", "you", "i", "be", "has", "have", "an", "every", "some" };
        var words = Regex.Matches(request ?? "", @"[A-Za-z]+").Select(m => m.Value).Where(w => !skip.Contains(w) && w.Length > 1).Take(2).ToList();
        if (words.Count == 0) return null;
        return string.Concat(words.Select(Capitalize));
    }

    static string Capitalize(string w) => w.Length == 0 ? w : char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..];
}
