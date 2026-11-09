using BuilderEngine.Script;

namespace BuilderEngine;

// GAME KIT, sounds: looping sounds, 'stop sound' and 'volume' from a playing game (World.Audio.cs) reach the audio player.
public sealed partial class MainWindow
{
    void OnGameSound(SoundRequest r)
    {
        if (r.VolumeOnly) { _audio.SetVolume(r.Name, r.Volume); return; }
        if (r.Stop) { _audio.StopLoop(r.Name); if (Sounds.Resolve(_projectDir, r.Name) is { } one) _audio.StopFile(one); return; } // a one-shot of that file too
        if (Sounds.Resolve(_projectDir, r.Name) is { } path) _audio.Loop(r.Name, path, r.Volume);
        else if (r.Name.Length > 0 && _missingSoundsWarned.Add(r.Name))
            Log($"⚠ There's no sound “{r.Name}” in the project's sounds folder. Import it with Project ▸ Import sound…");
    }
}
