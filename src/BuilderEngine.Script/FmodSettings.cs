namespace BuilderEngine.Script;

/// <summary>
/// The editor side of the optional FMOD audio that doesn't need a window (so it is tested): the <c>audioBackend</c> setting,
/// the "Sound: …" status line and the FMOD files an export may include.
/// </summary>
public static class FmodSettings
{
    /// <summary>The audio the person chose (Sound engine…): <c>auto</c> when not set.</summary>
    public static AudioBackend GetAudioBackend(this AppSettings s) => FmodLibrary.ParseBackend(s.Get(FmodLibrary.SettingKey));

    public static bool SetAudioBackend(this AppSettings s, AudioBackend b) => s.Set(FmodLibrary.SettingKey, FmodLibrary.BackendKey(b));

    /// <summary>The choices of the setting, as shown: (value, label).</summary>
    public static readonly (AudioBackend Value, string Label)[] Choices =
    [
        (AudioBackend.Auto, "Automatic (FMOD when it is installed)"),
        (AudioBackend.Fmod, "FMOD"),
        (AudioBackend.Classic, "Classic (Windows' own player)"),
    ];

    /// <summary>
    /// "Sound: FMOD 2.03.15 ✓" when FMOD plays (<paramref name="fmodVersion"/> not null), else "Sound: Classic (why)":
    /// chosen in the settings, or why FMOD couldn't be used (<paramref name="why"/>, e.g. "fmod.dll not found").
    /// </summary>
    public static string StatusLine(AudioBackend wanted, string? fmodVersion, string why)
    {
        if (fmodVersion != null) return $"Sound: FMOD {fmodVersion} ✓";
        if (wanted == AudioBackend.Classic) return "Sound: Classic (chosen in Sound engine…)";
        string reason = why.Trim();
        if (reason.StartsWith("fmod.dll not found", StringComparison.OrdinalIgnoreCase)) reason = "fmod.dll not found";
        reason = reason.TrimEnd('.');
        return reason.Length == 0 ? "Sound: Classic" : $"Sound: Classic ({reason})";
    }

    /// <summary>
    /// The FMOD libraries an exported game may carry next to its program: fmod.dll, and fmodstudio.dll when found (FMOD
    /// Studio events). Empty when FMOD isn't installed here. The person must follow FMOD's licence to ship them.
    /// </summary>
    public static List<string> ExportFiles()
    {
        var list = new List<string>();
        if (FmodLibrary.FindCore() is not { } core) return list;
        list.Add(core);
        if (FmodLibrary.FindStudio(Path.GetDirectoryName(core)) is { } studio) list.Add(studio);
        return list;
    }

    /// <summary>Whether a project has FMOD Studio banks (its <c>fmod</c> folder).</summary>
    public static bool HasBanks(string projectDir) => FmodStudio.FindBankFolder(projectDir) != null;
}
