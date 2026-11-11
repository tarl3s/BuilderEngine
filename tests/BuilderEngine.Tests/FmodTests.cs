using BuilderEngine.Script;

/// <summary>
/// Optional FMOD audio (FmodCore.cs, FmodStudio.cs). The first part runs everywhere (finding the DLLs, the setting, the
/// fallback when FMOD is missing). The smoke tests run only where FMOD is installed, always with FMOD's NOSOUND output:
/// nothing is ever heard. Without FMOD they are skipped, and say so.
/// </summary>
static class FmodTests
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Optional FMOD audio (FmodCore.cs, FmodStudio.cs)");

        test("audioBackend setting: auto by default, fmod, classic, anything else is auto", () =>
        {
            Check(FmodLibrary.ParseBackend(null) == AudioBackend.Auto, "null");
            Check(FmodLibrary.ParseBackend("") == AudioBackend.Auto, "empty");
            Check(FmodLibrary.ParseBackend(" FMOD ") == AudioBackend.Fmod, "fmod");
            Check(FmodLibrary.ParseBackend("Classic") == AudioBackend.Classic, "classic");
            Check(FmodLibrary.ParseBackend("banana") == AudioBackend.Auto, "typo");
            foreach (var b in Enum.GetValues<AudioBackend>()) Check(FmodLibrary.ParseBackend(FmodLibrary.BackendKey(b)) == b, "round trip " + b);
            Check(!FmodLibrary.WantsFmod(AudioBackend.Classic), "classic never uses FMOD");
        });

        test("fmod.dll is looked for next to the program, then FMOD_DLL, then the FMOD Engine folder", () =>
        {
            var c = FmodLibrary.CoreCandidates(@"C:\Game", @"D:\libs\fmod.dll", @"C:\Program Files (x86)");
            Check(c.Count == 3, "three places");
            Check(c[0] == @"C:\Game\fmod.dll", c[0]);
            Check(c[1] == @"D:\libs\fmod.dll", c[1]);
            Check(c[2] == @"C:\Program Files (x86)\FMOD SoundSystem\FMOD Studio API Windows\api\core\lib\x64\fmod.dll", c[2]);
            Check(FmodLibrary.CoreCandidates(@"C:\Game", "none", null).Count == 1, "FMOD_DLL=none is not a path");
            var s = FmodLibrary.StudioCandidates(@"C:\Game", null, @"D:\libs", @"C:\PF86");
            Check(s[0] == @"C:\Game\fmodstudio.dll" && s[1] == @"D:\libs\fmodstudio.dll" && s[^1].EndsWith(@"api\studio\lib\x64\fmodstudio.dll"), string.Join(" | ", s));
        });

        test("a missing fmod.dll is 'not found', and FMOD turned off finds nothing even when it exists", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "be_fmod_find_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                Check(FmodLibrary.Find([Path.Combine(dir, "fmod.dll")]) == null, "nothing there");
                File.WriteAllText(Path.Combine(dir, "fmod.dll"), "not really a dll");
                if (!FmodLibrary.Disabled) // FMOD_DLL=none: Find never finds anything (checked below)
                    Check(FmodLibrary.Find([Path.Combine(dir, "nope.dll"), Path.Combine(dir, "fmod.dll")]) is { } f && f.EndsWith("fmod.dll"), "the first that exists");
                FmodLibrary.ForceMissing = true;
                try
                {
                    Check(FmodLibrary.Find([Path.Combine(dir, "fmod.dll")]) == null, "turned off");
                    Check(FmodCore.TryCreate(out var why, silent: true) == null && why.Length > 0, "no FMOD: null with a reason");
                    Check(FmodStudio.TryCreate(out var why2, silent: true) == null && why2.Contains("isn't installed"), "no Studio: " + why2);
                    Check(!FmodLibrary.WantsFmod(AudioBackend.Auto), "auto without FMOD = classic");
                }
                finally { FmodLibrary.ForceMissing = false; }
            }
            finally { Directory.Delete(dir, true); }
        });

        test("version text, event paths and the order banks load in", () =>
        {
            Check(FmodLibrary.VersionText(0x00020315) == "2.03.15", FmodLibrary.VersionText(0x00020315));
            Check(FmodStudio.NormalizePath("Music/Forest") == "event:/Music/Forest", "short path");
            Check(FmodStudio.NormalizePath("event:/A") == "event:/A" && FmodStudio.NormalizePath(" /B ") == "event:/B", "full / slash");
            var order = FmodStudio.BankOrder(["SFX.bank", "Master.strings.bank", "Music.bank", "Master.bank"]);
            Check(string.Join(",", order) == "Master.bank,Master.strings.bank,Music.bank,SFX.bank", string.Join(",", order));
            Check(new FmodEventInfo("event:/UI/Click", [], false, true).Short == "UI/Click", "short name");
        });

        test("a project's bank folder: fmod, or the fmod\\Desktop FMOD Studio builds into", () =>
        {
            var proj = Path.Combine(Path.GetTempPath(), "be_fmod_proj_" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Directory.CreateDirectory(proj);
                Check(FmodStudio.FindBankFolder(proj) == null, "no fmod folder");
                Directory.CreateDirectory(Path.Combine(proj, "fmod", "Desktop"));
                Check(FmodStudio.FindBankFolder(proj) == null, "no banks yet");
                File.WriteAllText(Path.Combine(proj, "fmod", "Desktop", "Master.bank"), "x");
                Check(FmodStudio.FindBankFolder(proj) == Path.Combine(proj, "fmod", "Desktop"), "Desktop");
                File.WriteAllText(Path.Combine(proj, "fmod", "Master.bank"), "x");
                Check(FmodStudio.FindBankFolder(proj) == Path.Combine(proj, "fmod"), "fmod itself first");
            }
            finally { try { Directory.Delete(proj, true); } catch { } }
        });

        // ---------------- real FMOD, silent (NOSOUND_NRT output) ----------------
        if (FmodLibrary.FindCore() == null)
        {
            Console.WriteLine("  skip FMOD smoke tests: fmod.dll isn't installed on this machine (install the FMOD Engine to run them)");
            return;
        }

        test("FMOD Core (silent): play, loop, volume/pan/pitch, stop, ended sounds, memory sounds, release", () =>
        {
            var core = FmodCore.TryCreate(out var why, silent: true);
            Check(core != null, "FMOD didn't start: " + why);
            using (core)
            {
                Check(core!.Silent && core.Version >= FmodLibrary.MinVersion, "version " + core.VersionText);
                var wav = Path.Combine(Path.GetTempPath(), "be_fmod_" + Guid.NewGuid().ToString("N")[..6] + ".wav");
                File.WriteAllBytes(wav, Wav(0.1));
                try
                {
                    Check(core.Preload(wav) && core.CachedSounds == 1, "preload");
                    var one = core.Play(wav, 0.5, -0.5);
                    Check(!one.IsNone && core.IsPlaying(one), "a one-shot plays");
                    Check(core.SetVolume(one, 0.2) && core.SetPan(one, 0.7) && core.SetPitch(one, 1.5), "volume, pan, pitch");
                    var loop = core.Play(wav, 0.3, 0, loop: true);
                    Check(core.IsPlaying(loop), "the loop plays");
                    for (int i = 0; i < 60; i++) core.Update(); // ~1 s of mixing at NRT speed
                    Check(!core.IsPlaying(one), "the one-shot ended");
                    Check(!core.SetVolume(one, 1), "an ended channel is just false");
                    Check(core.IsPlaying(loop), "the loop still plays");
                    Check(core.SetPaused(loop, true) && core.SetPaused(loop, false) && core.SetLoop(loop, true), "pause / loop");
                    Check(core.Stop(loop) && !core.IsPlaying(loop), "stopped");
                    var mem = core.PlayMemory(Wav(0.05));
                    Check(core.IsPlaying(mem), "a sound from memory plays");
                    for (int i = 0; i < 30; i++) core.Update();
                    Check(!core.IsPlaying(mem), "and ends");
                    var again = core.Play(wav);
                    core.StopAll();
                    Check(!core.IsPlaying(again), "stop all");
                    Check(core.SetMasterVolume(0.5), "master volume");
                    Check(core.Play(Path.Combine(Path.GetTempPath(), "missing_" + Guid.NewGuid() + ".wav")).IsNone, "a missing file plays nothing (no throw)");
                    core.Forget(wav);
                    Check(core.CachedSounds == 0, "forget");
                }
                finally { try { File.Delete(wav); } catch { } }
            }
            Check(!core!.IsOpen && core.Play("x.wav").IsNone && !core.Update(), "after release every call is a harmless no-op");
        });

        var examples = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", FmodLibrary.InstallFolder, @"api\studio\examples\media");
        if (FmodLibrary.FindStudio() == null || !File.Exists(Path.Combine(examples, "Vehicles.bank")))
        {
            Console.WriteLine("  skip FMOD Studio smoke test: fmodstudio.dll or the FMOD Engine's example banks aren't installed (banks can only be built by FMOD Studio)");
            return;
        }

        test("FMOD Studio (silent): banks from a project's fmod folder, events and parameters, play, set, stop", () =>
        {
            var proj = Path.Combine(Path.GetTempPath(), "be_fmod_studio_" + Guid.NewGuid().ToString("N")[..6]);
            var banks = Path.Combine(proj, "fmod", "Desktop");
            Directory.CreateDirectory(banks);
            try
            {
                foreach (var b in new[] { "Master.bank", "Master.strings.bank", "Vehicles.bank" }) File.Copy(Path.Combine(examples, b), Path.Combine(banks, b));
                var listed = FmodStudio.ListEvents(proj, out var msg);
                Check(listed.Count > 0, "no events listed: " + msg);
                var engine = listed.FirstOrDefault(e => e.Parameters.Count > 0);
                Check(engine != null, "an event with a parameter: " + string.Join(", ", listed.Select(e => e.Path)));
                Check(ReferenceEquals(listed, FmodStudio.ListEvents(proj, out _)), "cached until a bank changes");

                var studio = FmodStudio.TryCreate(out var why, silent: true);
                Check(studio != null, "FMOD Studio didn't start: " + why);
                using (studio)
                {
                    Check(studio!.LoadProject(proj, out msg) == 3 && msg.Length == 0, "3 banks: " + msg);
                    Check(studio.FindEvent(engine!.Short) != null, "found by its short name");
                    var p = engine.Parameters[0];
                    var ev = studio.Play(engine.Path);
                    Check(!ev.IsNone, "started");
                    studio.Update();
                    Check(studio.IsPlaying(ev), "playing");
                    Check(studio.SetParameter(ev, p.Name, (p.Min + p.Max) / 2), "set " + p.Name);
                    Check(!studio.SetParameter(ev, "no such parameter", 1), "an unknown parameter is false, no throw");
                    Check(studio.SetVolume(ev, 0.5), "volume");
                    studio.SetPosition(ev, 320, 240); studio.SetListener(0, 0);
                    Check(studio.Play("event:/Nope/Nothing").IsNone, "an unknown event plays nothing");
                    Check(studio.Stop(ev, fade: false), "stop");
                    for (int i = 0; i < 10; i++) studio.Update();
                    Check(!studio.IsPlaying(ev), "stopped");
                    Check(studio.Release(ev), "released");
                    studio.UnloadAll();
                    Check(studio.Events.Count == 0, "unloaded");
                }
            }
            finally { FmodStudio.ClearCache(); try { Directory.Delete(proj, true); } catch { } }
        });
    }

    /// <summary>A short quiet 16-bit mono WAV (never heard: the tests use FMOD's NOSOUND output).</summary>
    static byte[] Wav(double seconds)
    {
        const int rate = 22050;
        int n = (int)(rate * seconds);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(i * 0.1) * 2000));
        w.Flush();
        return ms.ToArray();
    }
}
