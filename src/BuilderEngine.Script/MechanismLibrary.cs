using System.Text;
using System.Text.RegularExpressions;

namespace BuilderEngine.Script;

/// <summary>How a mechanism compares with one of the same name somewhere else.</summary>
public enum NameClash { None, Same, Different }

/// <summary>
/// "My Library": mechanisms saved as <c>&lt;Name&gt;.bs</c> files in a personal folder shared by every project
/// (the editor uses <c>%APPDATA%\BuilderEngine\Library</c>). Using one in a project copies it into the project.
/// </summary>
public sealed class MechanismLibrary(string folder)
{
    public string Folder { get; } = folder;

    /// <summary>The mechanisms in the library (file names without .bs), sorted.</summary>
    public List<string> Names()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetFiles(Folder, "*.bs").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    string FileOf(string name) => Path.Combine(Folder, SafeFileName(name) + ".bs");

    public bool Contains(string name) => File.Exists(FileOf(name));

    public string? Read(string name)
    {
        try { return File.Exists(FileOf(name)) ? File.ReadAllText(FileOf(name)) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>Is there already a mechanism with this name in the library, and is it the same?</summary>
    public NameClash Check(string name, string source) => Compare(Read(name), source);

    /// <summary>
    /// Saves a mechanism. On a clash with a different one, <paramref name="overwrite"/> replaces it; otherwise both are
    /// kept and this one gets a free name ("Coin2", its 'mechanism' line renamed too). Returns the name it was saved as.
    /// </summary>
    public string Save(string name, string source, bool overwrite = false)
    {
        Directory.CreateDirectory(Folder);
        string saveAs = name;
        if (!overwrite && Check(name, source) == NameClash.Different)
        {
            saveAs = UniqueName(name, n => Contains(n));
            source = RenameSource(source, name, saveAs);
        }
        string path = FileOf(saveAs), tmp = path + ".tmp";
        File.WriteAllText(tmp, source.Replace("\r\n", "\n"), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return saveAs;
    }

    public bool Delete(string name)
    {
        if (!Contains(name)) return false;
        File.Delete(FileOf(name));
        return true;
    }

    // ---------------- Shared helpers (also used when copying into a project) ----------------

    public static NameClash Compare(string? existing, string source) =>
        existing == null ? NameClash.None : Normalize(existing) == Normalize(source) ? NameClash.Same : NameClash.Different;

    static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    /// <summary>"Coin" -> "Coin2", "Coin3"... (a name already ending in a number counts up: "Coin2" -> "Coin3").</summary>
    public static string UniqueName(string name, Func<string, bool> taken)
    {
        if (!taken(name)) return name;
        int cut = name.Length;
        while (cut > 0 && char.IsDigit(name[cut - 1])) cut--;
        string stem = cut > 0 ? name[..cut] : name;
        int k = cut < name.Length && int.TryParse(name[cut..], out var n0) ? n0 + 1 : 2;
        while (taken(stem + k)) k++;
        return stem + k;
    }

    /// <summary>Renames the mechanism in its source: the <c>mechanism Old</c> line and signals written <c>Old.something</c>.</summary>
    public static string RenameSource(string source, string oldName, string newName)
    {
        string esc = Regex.Escape(oldName);
        source = Regex.Replace(source, $@"(?m)^(\s*(?:mechanism|system)\s+){esc}\b", m => m.Groups[1].Value + newName);
        return Regex.Replace(source, $@"(?m)^(\s*when\s+){esc}\.", m => m.Groups[1].Value + newName + ".");
    }

    static string SafeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.Length == 0 ? "Mechanism" : sb.ToString();
    }
}
