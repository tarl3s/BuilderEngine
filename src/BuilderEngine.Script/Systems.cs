using System.Reflection;
using System.Text;

namespace BuilderEngine.Script;

/// <summary>
/// Systems: a set of mechanisms that work together, written in Builder Script as a mechanism with two small extras —
/// <c>system</c> instead of <c>mechanism</c>, and <c>includes</c> lines:
/// <code>
/// system Inventory
///     includes Pickup sound pickup_sound     # comes with it: placed on things; every Pickup gets this default
///     includes Recipe
///     param slots: number = 9                # one card in the inspector sets the whole system
///     uses Hotbar slots slots                # runs on the system's own object, with the shared settings
///
///     when start                             # the setup: makes the manager objects it needs
///         visible = false
///         make Crafting at 260, 60 with 280, 320
/// </code>
/// In a project a system is one object (named after it) carrying the system mechanism. This class is the headless
/// editing side, shared by the editor and the tests: the starter systems, what a system brings, adding one to a scene,
/// removing it again, placing its parts, making a new one and saving one to My Library.
/// </summary>
public static class Systems
{
    // ---------------- Reading systems ----------------

    static List<(string Name, string Source)>? _starter;

    /// <summary>The systems that ship with BuilderEngine (embedded Systems/*.bs), sorted by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> Starter()
    {
        if (_starter != null) return _starter;
        var asm = Assembly.GetExecutingAssembly();
        var list = new List<(string, string)>();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("Systems.") && n.EndsWith(".bs")).Order())
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            string src = r.ReadToEnd().Replace("\r\n", "\n");
            list.Add((NameOf(src) ?? res["Systems.".Length..^3], src));
        }
        return _starter = list;
    }

    /// <summary>The source of a starter system, or null.</summary>
    public static string? StarterSource(string name) =>
        Starter().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Source;

    static HashSet<string>? _kitNames;
    static Dictionary<string, string>? _kitSources;

    /// <summary>The starter kit's mechanisms as shipped (name → source), available in every project without copying.</summary>
    public static IReadOnlyDictionary<string, string> StarterKitSources()
    {
        if (_kitSources != null) return _kitSources;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, src) in StarterKit.Sources())
            foreach (var (name, chunk) in Split(src)) d[name] = chunk;
        _kitNames = new HashSet<string>(d.Keys, StringComparer.OrdinalIgnoreCase);
        return _kitSources = d;
    }

    public static bool IsStarterMechanism(string name)
    {
        StarterKitSources();
        return _kitNames!.Contains(name);
    }

    /// <summary>Is this Builder Script source a system (its first line, after comments, starts with 'system')?</summary>
    public static bool IsSystemSource(string? source) => HeadWord(source) == "system";

    /// <summary>The name after 'system' or 'mechanism' on the first line, or null.</summary>
    public static string? NameOf(string? source)
    {
        var line = FirstLine(source);
        if (line == null) return null;
        var parts = line.Split('#')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] is "system" or "mechanism" ? parts[1] : null;
    }

    static string? HeadWord(string? source) => FirstLine(source)?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    static string? FirstLine(string? source)
    {
        if (source == null) return null;
        foreach (var raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            string t = raw.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            return t;
        }
        return null;
    }

    /// <summary>The comment lines right under the 'system' line, joined: what the system is for.</summary>
    public static string Description(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        bool started = false;
        foreach (var raw in lines)
        {
            string t = raw.Trim();
            if (!started) { if (t.StartsWith("system ") || t.StartsWith("mechanism ")) started = true; continue; }
            if (!t.StartsWith('#')) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t.TrimStart('#').Trim());
        }
        return sb.ToString();
    }

    /// <summary>Parses a system's source; null when it doesn't parse or isn't a system.</summary>
    public static MechanismDef? Parse(string source)
    {
        try { return Parser.Parse(source).FirstOrDefault(d => d.IsSystem); }
        catch (ScriptError) { return null; }
    }

    /// <summary>The mechanisms placed on things with this system (its 'includes'), in order.</summary>
    public static List<string> Parts(MechanismDef def) => def.Includes.Select(u => u.Name).ToList();

    /// <summary>
    /// Every mechanism the system brings: its parts ('includes'), what runs on its own object ('uses') and the manager
    /// objects its code makes ('make Crafting at …'), in that order, each once.
    /// </summary>
    public static List<string> Members(MechanismDef def)
    {
        var list = new List<string>();
        void Add(string n) { if (n.Length > 0 && !list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n); }
        foreach (var u in def.Includes) Add(u.Name);
        foreach (var u in def.Uses) Add(u.Name);
        foreach (var w in def.Whens) foreach (var n in MadeIn(w.Body)) Add(n);
        foreach (var p in def.Procs) foreach (var n in MadeIn(p.Body)) Add(n);
        return list;
    }

    /// <summary>The mechanisms a block of code makes with 'make Name …'.</summary>
    static IEnumerable<string> MadeIn(List<Stmt> body)
    {
        foreach (var s in body)
        {
            if (s is CommandStmt { Name: "make", Args.Count: > 0 } c && c.Args[0] is NameExpr { Path.Length: 1 } n) yield return n.Path[0];
            foreach (var inner in Inner(s))
                foreach (var x in MadeIn(inner)) yield return x;
        }
    }

    static IEnumerable<List<Stmt>> Inner(Stmt s)
    {
        switch (s)
        {
            case IfStmt i: yield return i.Then; if (i.Else != null) yield return i.Else; break;
            case RepeatStmt r: yield return r.Body; break;
            case TogetherStmt t: yield return t.Body; break;
            case RunStmt r: yield return r.Body; break;
            case ForEachStmt f: yield return f.Body; break;
            case WhileStmt w: yield return w.Body; break;
        }
    }

    /// <summary>Splits a file with several mechanisms (or systems) into one source per mechanism: name → source.</summary>
    public static IEnumerable<(string Name, string Source)> Split(string src)
    {
        var lines = src.Replace("\r\n", "\n").Split('\n');
        var starts = new List<int>();
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("mechanism ") || lines[i].StartsWith("system ")) starts.Add(i);
        for (int k = 0; k < starts.Count; k++)
        {
            int end = k + 1 < starts.Count ? starts[k + 1] : lines.Length;
            string body = string.Join("\n", lines[starts[k]..end]).TrimEnd() + "\n";
            if (NameOf(body) is { } name) yield return (name, body);
        }
    }

    // ---------------- Systems in a project ----------------

    /// <summary>What adding a system did: its object, the name it has in the project, and the mechanisms it copied in.</summary>
    public sealed record AddResult(SceneObject Object, string Name, List<string> Copied, List<string> Missing);

    /// <summary>What removing a system did.</summary>
    public sealed record RemoveResult(int ObjectsRemoved, bool SourceRemoved, List<string> MechanismsRemoved);

    /// <summary>
    /// Adds a system to a scene: its source goes into the project's mechanisms (<paramref name="sources"/>; the same one
    /// already there is reused, a different one with that name is replaced when <paramref name="replace"/>, else this one
    /// comes in as Inventory2), the mechanisms it brings that the project doesn't have yet are copied in from
    /// <paramref name="library"/> (the starter kit is always there), and one object carrying the system is added at x, y.
    /// </summary>
    public static AddResult Add(IDictionary<string, string> sources, Scene scene, string systemSource, double x, double y,
        Func<string, string?>? library = null, bool replace = false)
    {
        var def = Parse(systemSource) ?? throw new ScriptError("That isn't a system: its file should start with 'system Name'.", 0);
        string name = def.Name;
        string src = systemSource.Replace("\r\n", "\n").TrimEnd() + "\n";
        if (sources.TryGetValue(name, out var have) && MechanismLibrary.Compare(have, src) == NameClash.Different && !replace)
        {
            string other = MechanismLibrary.UniqueName(name, n => sources.ContainsKey(n) || IsStarterMechanism(n));
            src = RenameSystem(src, name, other);
            name = other;
        }
        sources[name] = src;

        var copied = new List<string>();
        var missing = new List<string>();
        foreach (var m in Members(def))
        {
            if (sources.ContainsKey(m) || IsStarterMechanism(m)) continue;
            if (library?.Invoke(m) is { } lib && Split(lib).FirstOrDefault(c => string.Equals(c.Name, m, StringComparison.OrdinalIgnoreCase)) is { Name: not null } chunk)
            {
                sources[chunk.Name] = chunk.Source;
                copied.Add(chunk.Name);
            }
            else missing.Add(m);
        }

        var o = scene.Add(new SceneObject
        {
            Name = UniqueObjectName(scene, name), X = x, Y = y, Width = 32, Height = 32, Color = SystemColor,
            Mechanisms = [new SceneMechanism { Name = name }],
        });
        return new AddResult(o, name, copied, missing);
    }

    /// <summary>The colour of a system's object in the editor (it isn't drawn in the game).</summary>
    public const string SystemColor = "#9B7BE0";

    /// <summary>
    /// Removes a system from a scene: its objects (an object with other mechanisms only loses the system). When no
    /// scene uses it any more, its source leaves the project — unless it was changed from <paramref name="original"/>
    /// (the starter or My Library copy), so your own work is never thrown away — and so do the mechanisms it copied in
    /// that nothing else uses and nobody changed.
    /// </summary>
    public static RemoveResult Remove(IDictionary<string, string> sources, IEnumerable<Scene> scenes, Scene scene, string systemName,
        Func<string, string?>? original = null)
    {
        int removed = 0;
        foreach (var o in scene.Objects.ToList())
        {
            if (!o.Has(systemName)) continue;
            o.Mechanisms.RemoveAll(m => string.Equals(m.Name, systemName, StringComparison.OrdinalIgnoreCase));
            if (o.Mechanisms.Count == 0) scene.Objects.Remove(o);
            removed++;
        }
        var all = scenes.ToList();
        bool Used(string n) => all.Any(s => s.Objects.Any(o => o.Has(n)));
        var gone = new List<string>();
        bool sourceRemoved = false;
        if (!Used(systemName) && sources.TryGetValue(systemName, out var src))
        {
            var def = Parse(src);
            var orig = original?.Invoke(systemName);
            if (orig != null && MechanismLibrary.Compare(orig, src) == NameClash.Same)
            {
                sources.Remove(systemName);
                sourceRemoved = true;
            }
            if (def != null)
                foreach (var m in Members(def))
                {
                    if (IsStarterMechanism(m) || Used(m) || !sources.TryGetValue(m, out var ms)) continue;
                    if (OtherSystemsBring(sources, m, systemName)) continue;
                    var mo = original?.Invoke(m);
                    var chunk = mo == null ? null : Split(mo).FirstOrDefault(c => string.Equals(c.Name, m, StringComparison.OrdinalIgnoreCase)).Source;
                    if (chunk != null && MechanismLibrary.Compare(chunk, ms) == NameClash.Same)
                    {
                        sources.Remove(m);
                        gone.Add(m);
                    }
                }
        }
        return new RemoveResult(removed, sourceRemoved, gone);
    }

    static bool OtherSystemsBring(IDictionary<string, string> sources, string mechanism, string except)
    {
        foreach (var (name, src) in sources)
        {
            if (string.Equals(name, except, StringComparison.OrdinalIgnoreCase) || !IsSystemSource(src)) continue;
            if (Parse(src) is { } d && Members(d).Contains(mechanism, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The systems in a project's mechanisms (name → parsed), for the inspector and the library badges.</summary>
    public static Dictionary<string, MechanismDef> InProject(IEnumerable<KeyValuePair<string, string>> sources)
    {
        var d = new Dictionary<string, MechanismDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, src) in sources)
            if (IsSystemSource(src) && Parse(src) is { } def) d[name] = def;
        return d;
    }

    /// <summary>
    /// Places one of a system's parts in the scene: a new object with that mechanism near the system's object (or at
    /// x, y). A few starter parts get a sensible shape: the player (tagged "player"), ground, a coin.
    /// </summary>
    public static SceneObject PlacePart(Scene scene, string part, double x, double y)
    {
        var (w, h, color, tag) = PartLook(part);
        var o = new SceneObject
        {
            Name = UniqueObjectName(scene, tag == "player" ? "Player" : part), X = x, Y = y, Width = w, Height = h, Color = color,
            Mechanisms = [new SceneMechanism { Name = part }],
        };
        if (tag != null) o.Tags.Add(tag);
        return scene.Add(o);
    }

    static (double W, double H, string Color, string? Tag) PartLook(string part) => part.ToLowerInvariant() switch
    {
        "platformer" or "topdown" or "playermove" => (32, 48, "#5B8DEF", "player"),
        "solid" or "oneway" => (192, 32, "#6B5B4B", "ground"),
        "coin" => (16, 16, "#E8B04B", "coin"),
        "pickup" => (20, 20, "#8B5A2B", null),
        "checkpoint" => (24, 48, "#6FBF73", null),
        "card" => (80, 112, "#ECE3D0", null),
        "light" => (16, 16, "#FFD27A", null),
        "recipe" => (24, 24, "#C9A86A", null),
        _ => (32, 32, "#D97757", null),
    };

    /// <summary>"Inventory", or "Inventory 2", "Inventory 3"… when the scene already has an object with that name.</summary>
    public static string UniqueObjectName(Scene scene, string baseName)
    {
        bool Taken(string n) => scene.Objects.Any(o => string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(baseName)) return baseName;
        for (int k = 2; ; k++) if (!Taken($"{baseName} {k}")) return $"{baseName} {k}";
    }

    // ---------------- Making systems ----------------

    /// <summary>Why this can't be a new system's name (a word, not taken), or null.</summary>
    public static string? CheckName(string name, Func<string, bool> taken)
    {
        name = name.Trim();
        if (name.Length == 0) return "A system needs a name.";
        if (!char.IsLetter(name[0]) && name[0] != '_') return "A name starts with a letter.";
        if (name.Any(c => !char.IsLetterOrDigit(c) && c != '_')) return "Use letters, digits and _ only (no spaces), like MyKit.";
        if (Parser.Keywords.Contains(name)) return $"'{name}' is a Builder Script word.";
        if (taken(name) || IsStarterMechanism(name)) return $"There's already a mechanism called “{name}”.";
        return null;
    }

    /// <summary>
    /// A new system made from mechanisms: each one becomes an 'includes' line (they stay on the objects that have them;
    /// change a line to 'uses' to run it on the system's own object, add params and pass them on).
    /// </summary>
    public static string NewSource(string name, IEnumerable<string> mechanisms, string? description = null)
    {
        var sb = new StringBuilder();
        sb.Append("system ").Append(name).Append('\n');
        var list = mechanisms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        sb.Append("    # ").Append(description ?? (list.Count == 0 ? "A set of mechanisms that work together." : "Made from " + string.Join(", ", list) + ".")).Append('\n');
        sb.Append("    # Pass shared settings on with a param: param speed: number = 200, then 'includes Platformer speed speed'.\n");
        foreach (var m in list) sb.Append("    includes ").Append(m).Append('\n');
        sb.Append("\n    when start\n        visible = false\n");
        return sb.ToString();
    }

    /// <summary>The 'system Old' line (and signals written 'Old.x') renamed.</summary>
    public static string RenameSystem(string source, string oldName, string newName) =>
        MechanismLibrary.RenameSource(source, oldName, newName);

    /// <summary>The systems in My Library (file names), sorted.</summary>
    public static List<string> LibrarySystems(MechanismLibrary lib) => lib.Names().Where(n => IsSystemSource(lib.Read(n))).ToList();

    /// <summary>
    /// Saves a system to My Library together with the project mechanisms it brings that aren't in the starter kit (or
    /// were changed from it), so another project gets all of it. Returns the names saved (the system first).
    /// </summary>
    public static List<string> SaveToLibrary(MechanismLibrary lib, string name, IReadOnlyDictionary<string, string> sources, bool overwrite = true)
    {
        if (!sources.TryGetValue(name, out var src) || Parse(src) is not { } def)
            throw new ScriptError($"“{name}” isn't a system in this project.", 0);
        var saved = new List<string> { lib.Save(name, src, overwrite) };
        var kit = StarterKitSources();
        foreach (var m in Members(def))
        {
            if (!sources.TryGetValue(m, out var ms)) continue;
            if (kit.TryGetValue(m, out var k) && MechanismLibrary.Compare(k, ms) == NameClash.Same) continue;
            saved.Add(lib.Save(m, ms, overwrite));
        }
        return saved;
    }
}
