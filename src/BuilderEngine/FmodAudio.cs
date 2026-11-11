using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// The "better sound" (optional FMOD audio, Script/FmodCore.cs): the same <see cref="IAudio"/> as <see cref="WinMmAudio"/>,
/// played by FMOD — wav, mp3, ogg, flac…, as many sounds at once as a game wants, exact volumes and loops. Made by
/// <see cref="Audio.Create"/> only when fmod.dll is found; FMOD's update runs on a ~16 ms UI timer. Volumes work like the
/// classic player: the master volume ("") applies to one-shots started after it, a loop has its own volume.
/// (src/BuilderEngine.Player compiles this file too.)
/// </summary>
sealed class FmodAudio : IAudio
{
    readonly FmodCore _core;
    readonly DispatcherTimer _timer;
    readonly Dictionary<string, FmodChannel> _loops = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Path, FmodChannel Channel)> _oneShots = [];
    double _master = 1;

    public string VersionText => _core.VersionText;

    FmodAudio(FmodCore core)
    {
        _core = core;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => Update());
        _timer.Start();
    }

    /// <summary>FMOD's audio, or null with the reason (not installed, no sound device…).</summary>
    public static FmodAudio? TryCreate(out string why)
    {
        var core = FmodCore.TryCreate(out why);
        return core == null ? null : new FmodAudio(core);
    }

    void Update()
    {
        _core.Update();
        if (_oneShots.Count > 0) _oneShots.RemoveAll(s => !_core.IsPlaying(s.Channel)); // ended one-shots
    }

    public void Play(string path)
    {
        if (!File.Exists(path)) return;
        var ch = _core.Play(path, _master);
        if (!ch.IsNone) _oneShots.Add((path, ch));
    }

    public void Loop(string key, string path, double volume)
    {
        StopLoop(key);
        if (!File.Exists(path)) return;
        var ch = _core.Play(path, Math.Clamp(volume, 0, 1), loop: true);
        if (!ch.IsNone) _loops[key] = ch;
    }

    public void StopLoop(string key)
    {
        if (_loops.Remove(key, out var ch)) _core.Stop(ch);
    }

    public void StopFile(string path)
    {
        foreach (var (p, ch) in _oneShots)
            if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) _core.Stop(ch);
        _oneShots.RemoveAll(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public void SetVolume(string key, double volume)
    {
        if (key.Length == 0) { _master = Math.Clamp(volume, 0, 1); return; }
        if (_loops.TryGetValue(key, out var ch)) _core.SetVolume(ch, Math.Clamp(volume, 0, 1));
    }

    public void StopAll()
    {
        _core.StopAll();
        _loops.Clear();
        _oneShots.Clear();
        _master = 1;
    }

    public void Dispose()
    {
        _timer.Stop();
        _loops.Clear();
        _oneShots.Clear();
        _core.Dispose();
    }
}
