using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BuilderEngine.Script;

/// <summary>
/// A named animation of an object: pictures from the project's images folder shown one after the other.
/// Same shape as EasyEngine's <c>SpriteAnimation</c> {Name, Frames[], Fps, Loop} so projects map 1:1.
/// </summary>
public sealed class SpriteAnimation
{
    public string Name { get; set; } = "idle";
    /// <summary>Picture names (as an object's Picture writes them: "walk1.png").</summary>
    public List<string> Frames { get; set; } = [];
    public double Fps { get; set; } = 10;
    public bool Loop { get; set; } = true;

    public SpriteAnimation Clone() => new() { Name = Name, Frames = [.. Frames], Fps = Fps, Loop = Loop };

    internal static SpriteAnimation? FromNode(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var a = new SpriteAnimation
        {
            Name = Project.Str(o, "name", "").Trim(),
            Fps = Math.Clamp(Project.Num(o, "fps", 10), 0.1, 120),
            Loop = Project.Get(o, "loop") is not { } l || Project.Value(l) is not (false or "false" or 0.0),
        };
        if (a.Name.Length == 0) return null;
        switch (Project.Get(o, "frames"))
        {
            case JsonArray frames:
                foreach (var f in frames)
                    if (Project.Value(f) is { } v && World.ToText(v).Trim() is { Length: > 0 } pic) a.Frames.Add(pic);
                break;
            case JsonValue one when one.TryGetValue<string>(out var text): // "frames": "a.png, b.png"
                a.Frames.AddRange(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                break;
        }
        return a;
    }
}

/// <summary>
/// A reusable object: everything an object has (mechanisms with their params, picture, size, colour, tags, animations)
/// kept under a name. Placed copies remember it (<see cref="SceneObject.Prefab"/>). Same shape as EasyEngine's
/// <c>PrefabDef</c> {Name, Icon, Category, Template}.
/// </summary>
public sealed class Prefab
{
    public string Name { get; set; } = "Prefab";
    public string Icon { get; set; } = "▢";
    public string Category { get; set; } = "General";
    public SceneObject Template { get; set; } = new();

    internal static Prefab? FromNode(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var p = new Prefab
        {
            Name = Project.Str(o, "name", "").Trim(),
            Icon = Project.Str(o, "icon", "▢"),
            Category = Project.Str(o, "category", "General").Trim() is { Length: > 0 } c ? c : "General",
            Template = Project.Get(o, "template") is JsonObject t ? SceneObject.FromNode(t) : new SceneObject(),
        };
        if (p.Name.Length == 0) return null;
        p.Template.Prefab = null;
        p.Template.PrefabAll = false;
        return p;
    }
}

public sealed partial class SceneObject
{
    /// <summary>The prefab this object was placed from (null = not a copy of a prefab).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prefab { get; set; }

    /// <summary>
    /// "Changes here change every copy": what changes on this object goes to its prefab and to the other copies.
    /// Off: changes stay on this object (its own overrides). EasyEngine's <c>PrefabAll</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PrefabAll { get; set; }

    /// <summary>The object's named animations (written to project.json only when there are some).</summary>
    public List<SpriteAnimation> Animations { get; set; } = [];

    /// <summary>The animation played when the game starts (null or unknown = the first one).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultAnimation { get; set; }

    public SpriteAnimation? FindAnimation(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Animations.FirstOrDefault(a => string.Equals(a.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The default animation (by name, else the first), or null when there are none.</summary>
    public SpriteAnimation? ResolveDefaultAnimation() => Animations.Count == 0 ? null : FindAnimation(DefaultAnimation) ?? Animations[0];

    internal void ReadPrefabFields(JsonObject o)
    {
        Prefab = Project.Str(o, "prefab", "").Trim() is { Length: > 0 } pf ? pf : null;
        PrefabAll = Project.Value(Project.Get(o, "prefabAll")) is true or "true";
        if (Project.Get(o, "animations") is JsonArray anims)
            foreach (var n in anims)
                if (SpriteAnimation.FromNode(n) is { } a && FindAnimation(a.Name) == null) Animations.Add(a);
        DefaultAnimation = Project.Str(o, "defaultAnimation", "").Trim() is { Length: > 0 } d ? d : null;
    }
}

public sealed partial class Project
{
    /// <summary>The project's prefabs (saved in project.json as "prefabs"; left out when there are none).</summary>
    public List<Prefab> Prefabs { get; set; } = [];

    public Prefab? FindPrefab(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Prefabs.FirstOrDefault(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    internal void ReadPrefabs(JsonObject root)
    {
        if (Get(root, "prefabs") is not JsonArray arr) return;
        foreach (var n in arr)
            if (Prefab.FromNode(n) is { } p && FindPrefab(p.Name) == null) Prefabs.Add(p);
    }

    internal void WritePrefabs(JsonObject root)
    {
        if (Prefabs.Count > 0) root["prefabs"] = JsonSerializer.SerializeToNode(Prefabs, WriteOptions);
    }
}

/// <summary>project.json details for the prefab and animation fields.</summary>
static class PrefabJson
{
    /// <summary>The default resolver, but an object's empty Animations list isn't written (old projects stay the same).</summary>
    internal static readonly IJsonTypeInfoResolver Resolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipEmptyAnimations } };

    static void SkipEmptyAnimations(JsonTypeInfo ti)
    {
        if (ti.Type != typeof(SceneObject)) return;
        foreach (var p in ti.Properties)
            if (string.Equals(p.Name, "animations", StringComparison.OrdinalIgnoreCase))
                p.ShouldSerialize = (_, v) => v is ICollection { Count: > 0 };
    }
}

/// <summary>
/// Prefabs while editing (headless, testable). A copy shares everything with its prefab except where it is placed:
/// its id, name, position, lock, and the prefab link itself (<see cref="PlacementKeys"/>).
/// <list type="bullet">
/// <item>A copy with <see cref="SceneObject.PrefabAll"/> ticked passes its changes to the prefab (<see cref="ApplyToPrefab"/>).</item>
/// <item>When a prefab changes, every copy gets the changed fields, except the fields a copy changed itself
/// (its overrides); ticked copies get everything (<see cref="PushChange"/>).</item>
/// <item><see cref="Revert"/> drops a copy's overrides; <see cref="Unlink"/> makes it independent.</item>
/// </list>
/// Locked copies are never changed.
/// </summary>
public static class PrefabTools
{
    /// <summary>project.json keys that belong to one placement, not to the prefab.</summary>
    public static readonly string[] PlacementKeys = ["id", "name", "x", "y", "locked", "prefab", "prefabAll"];

    static readonly System.Reflection.PropertyInfo[] SharedProps = typeof(SceneObject).GetProperties()
        .Where(p => p.CanRead && p.CanWrite && !PlacementKeys.Contains(JsonNamingPolicy.CamelCase.ConvertName(p.Name))).ToArray();

    /// <summary>What a copy shares with its prefab, as JSON (placement keys left out).</summary>
    public static JsonObject Shared(SceneObject o)
    {
        var node = JsonSerializer.SerializeToNode(o, Project.WriteOptions)!.AsObject();
        foreach (var k in PlacementKeys) node.Remove(k);
        return node;
    }

    static string SharedText(SceneObject o) => Shared(o).ToJsonString();

    /// <summary>Gives <paramref name="dst"/> the shared part described by <paramref name="shared"/> (a fresh copy), keeping its placement.</summary>
    static void CopyShared(JsonObject shared, SceneObject dst)
    {
        var src = SceneObject.FromNode((JsonObject)shared.DeepClone());
        foreach (var p in SharedProps) p.SetValue(dst, p.GetValue(src));
    }

    /// <summary>Gives <paramref name="dst"/> everything <paramref name="src"/> shares, keeping dst's placement.</summary>
    public static void CopyShared(SceneObject src, SceneObject dst) => CopyShared(Shared(src), dst);

    /// <summary>The shared fields where a copy differs from its prefab (its overrides), e.g. ["width", "image"]. Empty = same.</summary>
    public static List<string> Overrides(Project p, SceneObject o)
    {
        if (p.FindPrefab(o.Prefab) is not { } pf) return [];
        var mine = Shared(o);
        var theirs = Shared(pf.Template);
        return mine.Select(kv => kv.Key).Union(theirs.Select(kv => kv.Key))
            .Where(k => !JsonNode.DeepEquals(mine[k], theirs[k]) && !IsEmptyList(mine[k], theirs[k])).ToList();
    }

    static bool IsEmptyList(JsonNode? a, JsonNode? b) => a is null && b is JsonArray { Count: 0 } || b is null && a is JsonArray { Count: 0 };

    /// <summary>Every copy of a prefab in the project.</summary>
    public static IEnumerable<SceneObject> Copies(Project p, string prefab) =>
        p.Scenes.SelectMany(s => s.Objects).Where(o => string.Equals(o.Prefab, prefab, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Slime" if no prefab has that name, else "Slime 2", "Slime 3"...</summary>
    public static string UniqueName(Project p, string baseName)
    {
        baseName = string.IsNullOrWhiteSpace(baseName) ? "Prefab" : baseName.Trim();
        if (p.FindPrefab(baseName) == null) return baseName;
        for (int k = 2; ; k++) if (p.FindPrefab($"{baseName} {k}") == null) return $"{baseName} {k}";
    }

    /// <summary>
    /// Saves an object as a prefab (a prefab with that name is replaced). The object becomes its first copy.
    /// Returns the prefab.
    /// </summary>
    public static Prefab SaveAsPrefab(Project p, SceneObject o, string name, string? category = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? UniqueName(p, o.Name) : name.Trim();
        var t = o.Clone();
        t.Id = 0; t.X = 0; t.Y = 0; t.Locked = false;
        t.Prefab = null; t.PrefabAll = false;
        t.Name = name;
        var pf = new Prefab { Name = name, Template = t, Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim() };
        int at = p.Prefabs.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) { pf.Icon = p.Prefabs[at].Icon; p.Prefabs[at] = pf; } else p.Prefabs.Add(pf);
        o.Prefab = name;
        return pf;
    }

    /// <summary>A new copy of a prefab in a scene at x, y (given a fresh id). <paramref name="name"/> defaults to the prefab's name.</summary>
    public static SceneObject Place(Prefab pf, Scene s, double x, double y, string? name = null)
    {
        var o = pf.Template.Clone();
        o.Name = name ?? pf.Name;
        o.X = x; o.Y = y;
        o.Locked = false;
        o.Prefab = pf.Name;
        o.PrefabAll = false;
        return s.Add(o);
    }

    /// <summary>
    /// A copy with "change every copy" ticked was edited: its shared part becomes the prefab's, and the other copies
    /// follow (<see cref="PushChange"/>). Returns how many other copies changed (0 when nothing differs).
    /// </summary>
    public static int ApplyToPrefab(Project p, SceneObject copy)
    {
        if (p.FindPrefab(copy.Prefab) is not { } pf) return 0;
        var before = Shared(pf.Template);
        var now = Shared(copy);
        if (JsonNode.DeepEquals(before, now)) return 0;
        string keepName = pf.Template.Name;
        CopyShared(now, pf.Template);
        pf.Template.Name = keepName;
        return PushChange(p, pf, before, except: copy);
    }

    /// <summary>
    /// The prefab changed (its shared part was <paramref name="before"/>): each copy gets every changed field it hadn't
    /// changed itself; ticked copies get them all. Locked copies and <paramref name="except"/> are left alone.
    /// Returns how many copies changed.
    /// </summary>
    public static int PushChange(Project p, Prefab pf, JsonObject before, SceneObject? except = null)
    {
        var after = Shared(pf.Template);
        var changed = after.Select(kv => kv.Key).Union(before.Select(kv => kv.Key))
            .Where(k => !JsonNode.DeepEquals(after[k], before[k])).ToList();
        if (changed.Count == 0) return 0;
        int n = 0;
        foreach (var o in Copies(p, pf.Name).ToList())
        {
            if (ReferenceEquals(o, except) || o.Locked) continue;
            var mine = Shared(o);
            bool any = false;
            foreach (var k in changed)
            {
                if (!o.PrefabAll && !JsonNode.DeepEquals(mine[k], before[k]) && !IsEmptyList(mine[k], before[k])) continue; // its own change: kept
                if (JsonNode.DeepEquals(mine[k], after[k])) continue;
                if (after[k] is { } v) mine[k] = v.DeepClone(); else mine.Remove(k);
                any = true;
            }
            if (!any) continue;
            CopyShared(mine, o);
            n++;
        }
        return n;
    }

    /// <summary>Drops a copy's own changes: it becomes exactly like its prefab again (keeping where it is). False when it has no prefab.</summary>
    public static bool Revert(Project p, SceneObject copy)
    {
        if (p.FindPrefab(copy.Prefab) is not { } pf) return false;
        CopyShared(pf.Template, copy);
        return true;
    }

    /// <summary>The copy stops being a copy: it keeps everything it has, and prefab changes no longer reach it.</summary>
    public static void Unlink(SceneObject copy)
    {
        copy.Prefab = null;
        copy.PrefabAll = false;
    }

    /// <summary>Renames a prefab; its copies follow. Returns why it can't, or null when renamed.</summary>
    public static string? Rename(Project p, string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return "A prefab needs a name.";
        if (p.FindPrefab(oldName) is not { } pf) return $"There's no prefab called “{oldName}”.";
        if (p.FindPrefab(newName) is { } other && other != pf) return $"There's already a prefab called “{newName}”.";
        foreach (var o in Copies(p, pf.Name).ToList()) o.Prefab = newName;
        if (string.Equals(pf.Template.Name, pf.Name, StringComparison.Ordinal)) pf.Template.Name = newName;
        pf.Name = newName;
        return null;
    }

    /// <summary>Removes a prefab. Its copies stay in the scenes as ordinary objects. Returns how many copies were unlinked.</summary>
    public static int Delete(Project p, string name)
    {
        if (p.FindPrefab(name) is not { } pf) return 0;
        p.Prefabs.Remove(pf);
        int n = 0;
        foreach (var o in Copies(p, pf.Name).ToList()) { Unlink(o); n++; }
        return n;
    }

    /// <summary>The fields' friendly names for the inspector: "width" → "size", "mechanisms" → "mechanisms"...</summary>
    public static string Describe(IEnumerable<string> keys) => string.Join(", ", keys.Select(k => k switch
    {
        "width" or "height" => "size",
        "image" => "picture",
        "color" => "colour",
        "defaultAnimation" => "animations",
        "keepAspect" => "proportions",
        _ => k
    }).Distinct());
}
