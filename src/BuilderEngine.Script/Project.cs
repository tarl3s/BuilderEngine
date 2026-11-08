using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

/// <summary>
/// A game project: its scenes and its own mechanisms. Saved as a folder: <c>project.json</c> plus
/// <c>mechanisms/&lt;Name&gt;.bs</c> for every mechanism that belongs to the project.
/// Loading is tolerant: missing fields get defaults, unknown fields are ignored, numbers may be written as text.
/// </summary>
public sealed class Project
{
    public const string FileName = "project.json";
    public const string MechanismsFolder = "mechanisms";

    public string Name { get; set; } = "My Game";
    public List<Scene> Scenes { get; set; } = [];
    /// <summary>The project's own mechanisms: name -> Builder Script source (not saved inside project.json).</summary>
    public Dictionary<string, string> Mechanisms { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ---------------- Save / load ----------------

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        WriteAllTextSafe(Path.Combine(folder, FileName), ToJson());

        string mechDir = Path.Combine(folder, MechanismsFolder);
        if (Mechanisms.Count > 0 || Directory.Exists(mechDir))
        {
            Directory.CreateDirectory(mechDir);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, src) in Mechanisms)
            {
                string file = SafeFileName(name) + ".bs";
                keep.Add(file);
                WriteAllTextSafe(Path.Combine(mechDir, file), src);
            }
            foreach (var f in Directory.GetFiles(mechDir, "*.bs"))
                if (!keep.Contains(Path.GetFileName(f))) File.Delete(f);
        }
    }

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, FileName));

    /// <summary>Loads a project folder. Throws <see cref="JsonException"/> only when project.json isn't JSON at all.</summary>
    public static Project Load(string folder)
    {
        var p = FromJson(File.ReadAllText(Path.Combine(folder, FileName)));
        string mechDir = Path.Combine(folder, MechanismsFolder);
        if (Directory.Exists(mechDir))
            foreach (var f in Directory.GetFiles(mechDir, "*.bs").Order(StringComparer.OrdinalIgnoreCase))
                p.Mechanisms[Path.GetFileNameWithoutExtension(f)] = File.ReadAllText(f);
        return p;
    }

    static void WriteAllTextSafe(string path, string text)
    {
        // Write next to it first so a crash mid-write never leaves a half file.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    static string SafeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.Length == 0 ? "Mechanism" : sb.ToString();
    }

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(new { name = Name, scenes = Scenes }, WriteOptions);

    public static Project FromJson(string json)
    {
        var root = Parse(json) as JsonObject ?? new JsonObject();
        var p = new Project { Name = Str(root, "name", "My Game") };
        if (Get(root, "scenes") is JsonArray scenes)
            foreach (var s in scenes)
                if (s is JsonObject so) p.Scenes.Add(Scene.FromNode(so));
        if (p.Scenes.Count == 0) p.Scenes.Add(new Scene());
        return p;
    }

    internal static JsonNode? Parse(string json) => JsonNode.Parse(json,
        new JsonNodeOptions { PropertyNameCaseInsensitive = true },
        new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

    internal static string SerializeScene(Scene s) => JsonSerializer.Serialize(s, WriteOptions);

    // ---------------- Editing helpers (headless, testable) ----------------

    /// <summary>A mechanism was renamed: every object that uses it now uses the new name. Returns how many changed.</summary>
    public int RenameMechanism(string oldName, string newName)
    {
        int n = 0;
        foreach (var s in Scenes)
            foreach (var o in s.Objects)
                foreach (var m in o.Mechanisms)
                    if (string.Equals(m.Name, oldName, StringComparison.OrdinalIgnoreCase)) { m.Name = newName; n++; }
        if (Mechanisms.Remove(oldName, out var src)) Mechanisms[newName] = src;
        return n;
    }

    /// <summary>The demo every new project starts with: a player, coins, a patrolling slime and the ground.</summary>
    public static Project CreateDemo(string name = "My Game")
    {
        var s = new Scene { Name = "Level 1", Width = 800, Height = 450 };
        s.Add(new SceneObject { Name = "Ground", X = 0, Y = 400, Width = 800, Height = 50, Color = "#6B5B4B", Tags = ["ground"] });
        s.Add(new SceneObject
        {
            Name = "Player", X = 80, Y = 340, Width = 32, Height = 48, Color = "#5B8DEF", Tags = ["player"],
            Mechanisms = [new SceneMechanism { Name = "PlayerMove" }]
        });
        double[] coinX = [220, 300, 380, 560, 640];
        for (int i = 0; i < coinX.Length; i++)
            s.Add(new SceneObject
            {
                Name = $"Coin {i + 1}", X = coinX[i], Y = i == 2 ? 280 : 350, Width = 16, Height = 16, Color = "#E8B04B", Tags = ["coin"],
                Mechanisms = [new SceneMechanism { Name = "Coin" }]
            });
        s.Add(new SceneObject
        {
            Name = "Slime", X = 440, Y = 368, Width = 40, Height = 32, Color = "#6FBF73", Tags = ["enemy"],
            Mechanisms = [new SceneMechanism { Name = "Patrol", Params = { ["distance"] = 120.0, ["speed"] = 50.0 } }]
        });
        return new Project { Name = name, Scenes = [s] };
    }

    // ---------------- Tolerant JSON readers ----------------

    internal static JsonNode? Get(JsonObject o, string name)
    {
        if (o.TryGetPropertyValue(name, out var v)) return v;
        foreach (var (k, val) in o)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return val;
        return null;
    }

    internal static string Str(JsonObject o, string name, string def)
    {
        var v = Get(o, name) as JsonValue;
        if (v == null) return def;
        if (v.TryGetValue<string>(out var s)) return s;
        return v.ToJsonString();
    }

    internal static double Num(JsonObject o, string name, double def)
    {
        var v = Get(o, name) as JsonValue;
        if (v == null) return def;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
        if (v.TryGetValue<string>(out var s) && TryNumber(s, out d)) return d;
        return def;
    }

    internal static bool TryNumber(string s, out double d) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d)
        || double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);

    /// <summary>A JSON value as the runtime sees it: double, string, bool or null.</summary>
    internal static object? Value(JsonNode? n)
    {
        if (n is not JsonValue v) return n?.ToJsonString();
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s)) return s;
        return v.ToJsonString();
    }
}

public sealed class Scene
{
    public string Name { get; set; } = "Level 1";
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 450;
    public string Background { get; set; } = "#1A1410";
    public List<SceneObject> Objects { get; set; } = [];

    /// <summary>Adds an object, giving it a fresh id. Returns it.</summary>
    public SceneObject Add(SceneObject o)
    {
        o.Id = NextId();
        Objects.Add(o);
        return o;
    }

    public int NextId() => Objects.Count == 0 ? 1 : Objects.Max(o => o.Id) + 1;

    public SceneObject? Find(int id) => Objects.FirstOrDefault(o => o.Id == id);

    /// <summary>A deep copy (used by undo and duplicate).</summary>
    public Scene Clone() => FromJson(ToJson());

    public string ToJson() => Project.SerializeScene(this);
    public static Scene FromJson(string json) => FromNode(Project.Parse(json) as JsonObject ?? new JsonObject());

    internal static Scene FromNode(JsonObject o)
    {
        var s = new Scene
        {
            Name = Project.Str(o, "name", "Level 1"),
            Width = Math.Max(16, Project.Num(o, "width", 800)),
            Height = Math.Max(16, Project.Num(o, "height", 450)),
            Background = Project.Str(o, "background", "#1A1410"),
        };
        if (Project.Get(o, "objects") is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject on) s.Objects.Add(SceneObject.FromNode(on));
        // Ids must be unique and positive: fix duplicates or missing ones.
        var seen = new HashSet<int>();
        int next = s.Objects.Count == 0 ? 1 : Math.Max(1, s.Objects.Max(x => x.Id) + 1);
        foreach (var x in s.Objects)
            if (x.Id <= 0 || !seen.Add(x.Id)) { x.Id = next++; seen.Add(x.Id); }
        return s;
    }
}

public sealed class SceneObject
{
    public int Id { get; set; }
    public string Name { get; set; } = "Object";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 32;
    public double Height { get; set; } = 32;
    public string Color { get; set; } = "#D97757";
    public List<string> Tags { get; set; } = [];
    public List<SceneMechanism> Mechanisms { get; set; } = [];

    public SceneObject Clone() => FromNode((JsonObject)JsonSerializer.SerializeToNode(this, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!);

    public bool Has(string mechanism) => Mechanisms.Any(m => string.Equals(m.Name, mechanism, StringComparison.OrdinalIgnoreCase));

    internal static SceneObject FromNode(JsonObject o)
    {
        var s = new SceneObject
        {
            Id = (int)Project.Num(o, "id", 0),
            Name = Project.Str(o, "name", "Object"),
            X = Project.Num(o, "x", 0),
            Y = Project.Num(o, "y", 0),
            Width = Math.Max(1, Project.Num(o, "width", 32)),
            Height = Math.Max(1, Project.Num(o, "height", 32)),
            Color = Project.Str(o, "color", "#D97757"),
        };
        switch (Project.Get(o, "tags"))
        {
            case JsonArray tags:
                foreach (var t in tags)
                    if (Project.Value(t) is { } v && World.ToText(v).Trim() is { Length: > 0 } tag && !s.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        s.Tags.Add(tag);
                break;
            case JsonValue one when one.TryGetValue<string>(out var text): // "tags": "player, hero"
                foreach (var t in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) s.Tags.Add(t);
                break;
        }
        if (Project.Get(o, "mechanisms") is JsonArray mechs)
            foreach (var m in mechs)
            {
                if (m is JsonValue mv && mv.TryGetValue<string>(out var bare)) { s.Mechanisms.Add(new SceneMechanism { Name = bare }); continue; }
                if (m is not JsonObject mo) continue;
                var sm = new SceneMechanism { Name = Project.Str(mo, "name", "") };
                if (sm.Name.Length == 0) continue;
                if (Project.Get(mo, "params") is JsonObject ps)
                    foreach (var (k, v) in ps) sm.Params[k] = Project.Value(v);
                s.Mechanisms.Add(sm);
            }
        return s;
    }
}

/// <summary>A mechanism attached to a scene object, with the params set in the inspector (the rest keep their defaults).</summary>
public sealed class SceneMechanism
{
    public string Name { get; set; } = "";
    /// <summary>Param name -> value (double, string or bool).</summary>
    public Dictionary<string, object?> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
