namespace BuilderEngine.Script;

/// <summary>
/// A project's pictures: the files in its <c>images/</c> folder. <c>look "hero.png"</c>, an object's Picture and
/// Image params name a file there (the extension may be left out). Animations are numbered pictures
/// (<c>walk1.png, walk2.png…</c>) or a strip of square frames side by side in one picture.
/// </summary>
public static class Images
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp"];

    public static string Folder(string projectDir) => Path.Combine(projectDir, Project.ImagesFolder);

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The project's picture file names (no folders), sorted.</summary>
    public static List<string> List(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir)) return [];
        string dir = Folder(projectDir);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir).Where(IsImage).Select(Path.GetFileName).OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The file a picture name points to ("hero.png", "hero", "images/hero.png"), or null when there's none.</summary>
    public static string? Resolve(string? projectDir, string? name)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || string.IsNullOrWhiteSpace(name)) return null;
        string file = Path.GetFileName(name.Trim().Replace('\\', '/').Split('/')[^1]);
        if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        string dir = Folder(projectDir);
        string direct = Path.Combine(dir, file);
        if (IsImage(file) && File.Exists(direct)) return direct;
        foreach (var ext in Extensions)
        {
            string p = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// The numbered pictures of an animation, in order: "walk" → walk1.png, walk2.png… (also walk_1, walk-01, "walk 2").
    /// Empty when there are none.
    /// </summary>
    public static List<string> Frames(string? projectDir, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name.Trim());
        var found = new List<(int N, string File)>();
        foreach (var f in List(projectDir))
        {
            string s = Path.GetFileNameWithoutExtension(f);
            if (!s.StartsWith(stem, StringComparison.OrdinalIgnoreCase) || s.Length == stem.Length) continue;
            string rest = s[stem.Length..].TrimStart('_', '-', ' ');
            if (rest.Length > 0 && rest.All(char.IsAsciiDigit) && int.TryParse(rest, out int n)) found.Add((n, f));
        }
        return found.OrderBy(x => x.N).Select(x => x.File).ToList();
    }

    /// <summary>
    /// Copies a picture into the project's images folder and returns its name there. The same file already there is
    /// reused; a different file with the same name is kept as "name 2.png".
    /// </summary>
    public static string Import(string projectDir, string sourceFile)
    {
        if (!IsImage(sourceFile)) throw new InvalidOperationException($"“{Path.GetFileName(sourceFile)}” isn't a picture (png, jpg or bmp).");
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

    /// <summary>A picture's size in pixels read from its header (png, jpg, bmp), without loading it. Null if unknown.</summary>
    public static (int Width, int Height)? Size(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[26];
            if (f.Read(head, 0, head.Length) < 24) return null;
            if (head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G')
                return (BigEndian(head, 16), BigEndian(head, 20));
            if (head[0] == (byte)'B' && head[1] == (byte)'M')
                return (BitConverter.ToInt32(head, 18), Math.Abs(BitConverter.ToInt32(head, 22)));
            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                f.Position = 2;
                var b = new byte[9];
                while (f.Read(b, 0, 4) == 4)
                {
                    if (b[0] != 0xFF) return null;
                    int marker = b[1], len = (b[2] << 8) | b[3];
                    bool sof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
                    if (sof)
                    {
                        if (f.Read(b, 0, 5) < 5) return null;
                        return ((b[3] << 8) | b[4], (b[1] << 8) | b[2]);
                    }
                    f.Position += len - 2;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    static int BigEndian(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
}
