using Avalonia.Media.Imaging;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// The project's pictures loaded once (by name, as scripts and params write them). A name with no file is remembered
/// as missing, so drawing never touches the disk again for it. <see cref="Clear"/> after importing pictures.
/// </summary>
sealed class ImageCache
{
    readonly Dictionary<string, Bitmap?> _byName = new(StringComparer.OrdinalIgnoreCase);
    public string ProjectDir { get; private set; }

    public ImageCache(string projectDir) { ProjectDir = projectDir; }

    public void SetProject(string projectDir)
    {
        if (string.Equals(projectDir, ProjectDir, StringComparison.OrdinalIgnoreCase)) return;
        ProjectDir = projectDir;
        Clear();
    }

    public void Clear()
    {
        // Bitmaps may still be on screen (a frame being drawn): let the GC free them.
        _byName.Clear();
    }

    public Bitmap? Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_byName.TryGetValue(name, out var b)) return b;
        b = null;
        if (Images.Resolve(ProjectDir, name) is { } path)
            try { b = new Bitmap(path); }
            catch (Exception) { b = null; } // not a picture Avalonia can read
        _byName[name] = b;
        return b;
    }
}
