namespace BuilderEngine.Script;

/// <summary>
/// Sample games built from code (each one in its own file under Samples/, registered from a [ModuleInitializer]).
/// <c>BuilderEngine.exe --make-sample "Name" &lt;folder&gt;</c> writes one as a normal project folder.
/// </summary>
public static class SampleGames
{
    /// <summary>Name → a builder that fills a project folder (project.json, mechanisms, images, sounds).</summary>
    public static readonly Dictionary<string, Action<string>> All = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes the sample into <paramref name="folder"/> (replacing an older copy of the same files).</summary>
    public static void Make(string name, string folder)
    {
        if (!All.TryGetValue(name, out var build))
            throw new ArgumentException($"No sample called '{name}'. Samples: {string.Join(", ", All.Keys)}");
        Directory.CreateDirectory(folder);
        build(folder);
    }
}
