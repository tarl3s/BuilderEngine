using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

/// <summary>
/// The editor's settings file (<c>settings.json</c> in the app-data folder, normally <c>%APPDATA%\BuilderEngine</c>):
/// <c>lastProject</c>, <c>recentProjects</c>... Keys it doesn't know are kept as they are. A read-only instance never
/// writes (used when a session must not touch the person's settings, e.g. <c>--project</c>).
/// </summary>
public sealed class AppSettings(string folder, bool readOnly = false)
{
    public const int MaxRecent = 10;
    public const string LibraryFolderName = "Library";

    public string Folder { get; } = folder;
    public bool ReadOnly { get; } = readOnly;
    public string FilePath => Path.Combine(Folder, "settings.json");
    /// <summary>My Library: personal mechanisms shared by every project.</summary>
    public string LibraryFolder => Path.Combine(Folder, LibraryFolderName);

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BuilderEngine");

    JsonObject Load()
    {
        try { return File.Exists(FilePath) ? Project.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new() : new(); }
        catch { return new(); }
    }

    bool Store(JsonObject o)
    {
        if (ReadOnly) return false;
        try
        {
            Directory.CreateDirectory(Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch { return false; } // settings are a convenience
    }

    public string? Get(string key) => Load()[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public bool Set(string key, string value)
    {
        var o = Load();
        o[key] = value;
        return Store(o);
    }

    public List<string> GetList(string key) =>
        Load()[key] is JsonArray a ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];

    public bool SetList(string key, IEnumerable<string> values)
    {
        var o = Load();
        o[key] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return Store(o);
    }

    // ---------------- Recent projects ----------------

    public List<string> RecentProjects() => GetList("recentProjects");

    /// <summary>Opened a project: it becomes <c>lastProject</c> and goes to the top of the recent list.</summary>
    public bool RememberProject(string folder)
    {
        var o = Load();
        o["lastProject"] = folder;
        var list = o["recentProjects"] is JsonArray a ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>() : [];
        o["recentProjects"] = new JsonArray(AddRecent(list, folder).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return Store(o);
    }

    /// <summary>Puts a folder at the top of a recent list: no duplicates (same folder written differently counts), at most <see cref="MaxRecent"/>.</summary>
    public static List<string> AddRecent(IEnumerable<string> list, string folder, int max = MaxRecent)
    {
        var result = new List<string> { folder };
        foreach (var f in list)
            if (!SameFolder(f, folder) && !result.Any(r => SameFolder(r, f))) result.Add(f);
        return result.Take(Math.Max(1, max)).ToList();
    }

    public static bool SameFolder(string a, string b)
    {
        static string N(string p)
        {
            try { p = Path.GetFullPath(p); } catch { }
            return p.Replace('/', '\\').TrimEnd('\\');
        }
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }
}
