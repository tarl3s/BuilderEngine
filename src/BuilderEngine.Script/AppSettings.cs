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
        TryLoad(out var o, out _);
        return o;
    }

    /// <summary>
    /// Reads the file (missing = empty). A file that isn't JSON (cut short, hand-edited) reads as empty with
    /// <paramref name="broken"/> set. False when the file is there but can't be read (locked, no permission).
    /// </summary>
    bool TryLoad(out JsonObject o, out bool broken)
    {
        o = new JsonObject();
        broken = false;
        string text;
        try
        {
            if (!File.Exists(FilePath)) return true;
            text = File.ReadAllText(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        try
        {
            if (Project.Parse(text) is JsonObject read) { o = read; return true; }
        }
        catch (JsonException) { }
        broken = true;
        return true;
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } // settings are a convenience: callers get false
    }

    /// <summary>
    /// Changes the file: read it, change it, write it back. Never when it couldn't be read (that would write an empty
    /// file over it); a file that wasn't JSON is first kept as <c>settings.broken-….json</c>, so its keys (API keys, the
    /// recent list…) can still be found.
    /// </summary>
    bool Update(Action<JsonObject> change)
    {
        if (ReadOnly || !TryLoad(out var o, out bool broken)) return false;
        if (broken)
        {
            try { File.Copy(FilePath, Path.Combine(Folder, $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } // not kept: don't replace it either
        }
        change(o);
        return Store(o);
    }

    public string? Get(string key) => Load()[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public bool Set(string key, string value) => Update(o => o[key] = value);

    public List<string> GetList(string key) =>
        Load()[key] is JsonArray a ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];

    public bool SetList(string key, IEnumerable<string> values) =>
        Update(o => o[key] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));

    // ---------------- Recent projects ----------------

    public List<string> RecentProjects() => GetList("recentProjects");

    /// <summary>Opened a project: it becomes <c>lastProject</c> and goes to the top of the recent list.</summary>
    public bool RememberProject(string folder) => Update(o =>
    {
        o["lastProject"] = folder;
        var list = o["recentProjects"] is JsonArray a ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>() : [];
        o["recentProjects"] = new JsonArray(AddRecent(list, folder).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    });

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
            try { p = Path.GetFullPath(p); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { } // not a path: compared as written
            return p.Replace('/', '\\').TrimEnd('\\');
        }
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }
}
