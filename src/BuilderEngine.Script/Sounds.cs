namespace BuilderEngine.Script;

/// <summary>
/// A project's sounds: the files in its <c>sounds/</c> folder. <c>play "coin.wav"</c> and Sound params name a file
/// there (the extension may be left out). Headless: the editor plays the resolved file through its audio player.
/// </summary>
public static class Sounds
{
    public static readonly string[] Extensions = [".wav", ".mp3", ".ogg"];

    public static string Folder(string projectDir) => Path.Combine(projectDir, Project.SoundsFolder);

    public static bool IsSound(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The project's sound file names (no folders), sorted.</summary>
    public static List<string> List(string projectDir)
    {
        string dir = Folder(projectDir);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir).Where(IsSound).Select(Path.GetFileName).OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The file a sound name points to ("coin.wav", "coin", "sounds/coin.wav"), or null when there's none.</summary>
    public static string? Resolve(string projectDir, string name)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || string.IsNullOrWhiteSpace(name)) return null;
        string file = Path.GetFileName(name.Trim().Replace('\\', '/').Split('/')[^1]);
        if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        string dir = Folder(projectDir);
        string direct = Path.Combine(dir, file);
        if (IsSound(file) && File.Exists(direct)) return direct;
        foreach (var ext in Extensions)
        {
            string p = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Copies a sound file into the project's sounds folder and returns its name there. The same file already there
    /// is reused; a different file with the same name is kept as "name 2.wav".
    /// </summary>
    public static string Import(string projectDir, string sourceFile)
    {
        if (!IsSound(sourceFile)) throw new InvalidOperationException($"“{Path.GetFileName(sourceFile)}” isn't a sound (wav, mp3 or ogg).");
        string dir = Folder(projectDir);
        Directory.CreateDirectory(dir);
        string name = Path.GetFileName(sourceFile);
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name).ToLowerInvariant();
        var bytes = File.ReadAllBytes(sourceFile);
        for (int k = 1; ; k++)
        {
            string candidate = k == 1 ? stem + ext : $"{stem} {k}{ext}";
            string target = Path.Combine(dir, candidate);
            if (!File.Exists(target))
            {
                File.WriteAllBytes(target, bytes);
                return candidate;
            }
            if (Path.GetFullPath(target).Equals(Path.GetFullPath(sourceFile), StringComparison.OrdinalIgnoreCase)
                || File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return candidate;
        }
    }

    /// <summary>Writes the generated starter sounds (coin, jump, hit, powerup) that are missing. Returns how many were written.</summary>
    public static int WriteDefaults(string projectDir)
    {
        string dir = Folder(projectDir);
        Directory.CreateDirectory(dir);
        int n = 0;
        foreach (var (file, make) in SoundSynth.Defaults)
        {
            string p = Path.Combine(dir, file);
            if (File.Exists(p)) continue;
            File.WriteAllBytes(p, make());
            n++;
        }
        return n;
    }
}
