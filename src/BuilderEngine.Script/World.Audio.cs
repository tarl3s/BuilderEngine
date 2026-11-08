using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>What the game asks the host's audio to do with a looping sound or the volume.</summary>
public sealed record SoundRequest(string Name, bool Loop = false, bool Stop = false, double Volume = 1, bool VolumeOnly = false);

/// <summary>
/// Sounds:
/// <code>
/// play "coin.wav"              play "rain.wav" loop       play "music.mp3" loop with 0.4   (volume 0..1)
/// stop sound "rain.wav"        stop sounds                (every looping sound)
/// volume "rain.wav" 0.2        volume 0.5                 (all sounds)
/// playing("rain.wav")          (is that loop on?)
/// </code>
/// A one-shot 'play' still raises <see cref="SoundPlayed"/>; loops, stops and volumes go to <see cref="SoundControl"/>.
/// </summary>
public sealed partial class World
{
    /// <summary>Loops, stops and volume changes for the host's audio.</summary>
    public event Action<SoundRequest>? SoundControl;
    /// <summary>The loops playing now (name → volume).</summary>
    public Dictionary<string, double> Loops { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public double MasterVolume { get; private set; } = 1;

    [ModuleInitializer]
    internal static void InstallAudio() => Installers.Add(w =>
    {
        w.Commands["play"] = c =>
        {
            var world = c.World;
            string sound = c.Args.Count > 0 ? ToText(c.Args[0]) : "";
            bool loop = IndexOfWord(c, "loop") >= 0 || IndexOfWord(c, "forever") >= 0;
            double volume = NamedNumber(c, "with") is double v ? Math.Clamp(v, 0, 1) : 1;
            world.Output.Add("♪ " + sound);
            if (!loop) { world.SoundPlayed?.Invoke(sound); return; }
            world.Loops[sound] = volume;
            world.SoundControl?.Invoke(new SoundRequest(sound, Loop: true, Volume: volume * world.MasterVolume));
        };
        w.Commands["stop sound"] = c =>
        {
            var world = c.World;
            if (c.Args.Count == 0) { world.StopLoops(); return; }
            string sound = ToText(c.Args[0]);
            world.Loops.Remove(sound);
            world.SoundControl?.Invoke(new SoundRequest(sound, Stop: true));
        };
        w.Commands["stop sounds"] = c => c.World.StopLoops();
        w.Commands["volume"] = c =>
        {
            var world = c.World;
            if (c.Args.Count == 0) throw c.Error("'volume' needs how loud (0..1): volume 0.5, or volume \"rain.wav\" 0.2.");
            if (c.Args.Count >= 2 && c.Args[0] is string name)
            {
                double v = Math.Clamp(c.Number(c.Args[1]), 0, 1);
                if (world.Loops.ContainsKey(name)) world.Loops[name] = v;
                world.SoundControl?.Invoke(new SoundRequest(name, Volume: v * world.MasterVolume, VolumeOnly: true));
                return;
            }
            world.MasterVolume = Math.Clamp(c.Number(c.Args[0]), 0, 1);
            world.SoundControl?.Invoke(new SoundRequest("", Volume: world.MasterVolume, VolumeOnly: true));
            foreach (var (n, v) in world.Loops)
                world.SoundControl?.Invoke(new SoundRequest(n, Volume: v * world.MasterVolume, VolumeOnly: true));
        };
        w.Functions["playing"] = a => a.Count > 0 && w.Loops.ContainsKey(ToText(a[0]));
    });

    void StopLoops()
    {
        foreach (var n in Loops.Keys.ToList()) SoundControl?.Invoke(new SoundRequest(n, Stop: true));
        Loops.Clear();
    }
}
