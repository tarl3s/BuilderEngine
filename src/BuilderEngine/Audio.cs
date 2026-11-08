using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace BuilderEngine;

/// <summary>Plays sound files. Windows has a real player now; other systems get <see cref="SilentAudio"/> until they get theirs.</summary>
interface IAudio : IDisposable
{
    /// <summary>Starts a sound file and returns right away (several can play at once).</summary>
    void Play(string path);
    void StopAll();
    /// <summary>Starts a sound that repeats until <see cref="StopLoop"/> (the same key again restarts it at the new volume).</summary>
    void Loop(string key, string path, double volume);
    void StopLoop(string key);
    /// <summary>The volume (0..1) of one loop, or of every sound when <paramref name="key"/> is "".</summary>
    void SetVolume(string key, double volume);
}

static class Audio
{
    public static IAudio Create(bool mute) => !mute && OperatingSystem.IsWindows() ? new WinMmAudio() : new SilentAudio();
}

sealed class SilentAudio : IAudio
{
    public void Play(string path) { }
    public void StopAll() { }
    public void Loop(string key, string path, double volume) { }
    public void StopLoop(string key) { }
    public void SetVolume(string key, double volume) { }
    public void Dispose() { }
}

/// <summary>
/// Windows' built-in MCI player (winmm.dll, no packages): wav and mp3 (ogg when a codec is installed), up to
/// <see cref="Voices"/> sounds at once. All MCI calls run on one background thread, so a slow open never stalls a frame.
/// A wav that MCI can't open falls back to PlaySound (one at a time).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class WinMmAudio : IAudio
{
    const int Voices = 8;
    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_FILENAME = 0x00020000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
    static extern int MciSendString(string command, StringBuilder? result, int resultLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW")]
    static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;
    readonly bool[] _open = new bool[Voices];
    int _next;

    public WinMmAudio()
    {
        _thread = new Thread(() =>
        {
            foreach (var job in _queue.GetConsumingEnumerable())
                try { job(); } catch { /* a sound that can't play never stops the game */ }
            CloseAll();
        }) { IsBackground = true, Name = "BuilderEngine audio" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Play(string path)
    {
        if (_queue.IsAddingCompleted || !File.Exists(path)) return;
        _queue.Add(() =>
        {
            int v = _next++ % Voices;
            string alias = "bevoice" + v;
            if (_open[v]) { MciSendString("close " + alias, null, 0, IntPtr.Zero); _open[v] = false; }
            if (MciSendString($"open \"{path}\" type mpegvideo alias {alias}", null, 0, IntPtr.Zero) == 0)
            {
                _open[v] = true;
                if (_master < 0.999) MciSendString($"setaudio {alias} volume to {(int)(_master * 1000)}", null, 0, IntPtr.Zero);
                MciSendString("play " + alias, null, 0, IntPtr.Zero);
            }
            else if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT);
        });
    }

    // Looping sounds (music, rain, a humming machine): one MCI alias each, played with "repeat".
    readonly Dictionary<string, string> _loops = new(StringComparer.OrdinalIgnoreCase);
    int _loopSeq;
    double _master = 1;

    public void Loop(string key, string path, double volume)
    {
        if (_queue.IsAddingCompleted || !File.Exists(path)) return;
        _queue.Add(() =>
        {
            CloseLoop(key);
            string alias = "beloop" + _loopSeq++;
            if (MciSendString($"open \"{path}\" type mpegvideo alias {alias}", null, 0, IntPtr.Zero) != 0) return;
            _loops[key] = alias;
            MciSendString($"setaudio {alias} volume to {(int)(Math.Clamp(volume, 0, 1) * 1000)}", null, 0, IntPtr.Zero);
            MciSendString($"play {alias} repeat", null, 0, IntPtr.Zero);
        });
    }

    public void StopLoop(string key)
    {
        if (!_queue.IsAddingCompleted) _queue.Add(() => CloseLoop(key));
    }

    public void SetVolume(string key, double volume)
    {
        if (_queue.IsAddingCompleted) return;
        _queue.Add(() =>
        {
            if (key.Length == 0) { _master = Math.Clamp(volume, 0, 1); return; }
            if (_loops.TryGetValue(key, out var alias))
                MciSendString($"setaudio {alias} volume to {(int)(Math.Clamp(volume, 0, 1) * 1000)}", null, 0, IntPtr.Zero);
        });
    }

    void CloseLoop(string key)
    {
        if (_loops.Remove(key, out var alias)) MciSendString("close " + alias, null, 0, IntPtr.Zero);
    }

    public void StopAll()
    {
        if (!_queue.IsAddingCompleted) _queue.Add(CloseAll);
    }

    void CloseAll()
    {
        for (int v = 0; v < Voices; v++)
            if (_open[v]) { MciSendString("close bevoice" + v, null, 0, IntPtr.Zero); _open[v] = false; }
        foreach (var alias in _loops.Values) MciSendString("close " + alias, null, 0, IntPtr.Zero);
        _loops.Clear();
        _master = 1;
        PlaySound(null, IntPtr.Zero, 0);
    }

    public void Dispose()
    {
        if (_queue.IsAddingCompleted) return;
        _queue.CompleteAdding();
        _thread.Join(1000);
    }

    /// <summary>
    /// Dev check (used by --snap, makes no sound): opens the file, plays it at volume 0 and asks MCI what it is doing.
    /// Returns e.g. "length 300 ms, mode playing", or the MCI error.
    /// </summary>
    public static string SilentCheck(string path)
    {
        string result = "";
        var t = new Thread(() =>
        {
            var sb = new StringBuilder(128);
            int err = MciSendString($"open \"{path}\" type mpegvideo alias becheck", null, 0, IntPtr.Zero);
            if (err != 0) { result = $"MCI error {err}"; return; }
            try
            {
                MciSendString("setaudio becheck volume to 0", null, 0, IntPtr.Zero);
                MciSendString("set becheck time format milliseconds", null, 0, IntPtr.Zero);
                MciSendString("status becheck length", sb, sb.Capacity, IntPtr.Zero);
                string length = sb.ToString();
                MciSendString("play becheck", null, 0, IntPtr.Zero);
                Thread.Sleep(60);
                sb.Clear();
                MciSendString("status becheck mode", sb, sb.Capacity, IntPtr.Zero);
                result = $"length {length} ms, mode {sb}";
            }
            finally { MciSendString("close becheck", null, 0, IntPtr.Zero); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(5000);
        return result;
    }
}
