using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BuilderEngine.Script;

// ============================================================================================================
// Optional FMOD audio ("better sound"). FMOD is NOT shipped with BuilderEngine or EasyEngine (its own licence,
// fmod.com): when fmod.dll is found the engines use it, otherwise they keep their classic audio. This file is the
// one wrapper both engines use (EasyEngine through lib/BuilderEngine/BuilderEngine.Script.dll):
//   FmodLibrary  finds and loads fmod.dll / fmodstudio.dll (never throws when they are missing), the audioBackend setting
//   FmodCore     one FMOD Core system: sounds from files or memory, channels (volume, pan, pitch, pause, loop, stop)
//   FmodStudio   (FmodStudio.cs) FMOD Studio banks, events and parameters
// Nothing here throws into a game loop: a call that fails returns false / an empty handle and logs once (FmodLibrary.Log).
// Thread-safety: every call takes the object's lock, so calling from the UI thread and a timer is safe; the engines call
// everything from their UI/game thread anyway.
// ============================================================================================================

/// <summary>Which audio the engine uses: <c>auto</c> (FMOD when fmod.dll is found), <c>fmod</c> or <c>classic</c>.</summary>
public enum AudioBackend { Auto, Fmod, Classic }

/// <summary>Finds and loads the FMOD libraries (once per process) and reads the <c>audioBackend</c> setting.</summary>
public static class FmodLibrary
{
    /// <summary>The FMOD API version this wrapper is written against (2.03.15); <see cref="MinVersion"/> is the oldest DLL accepted.</summary>
    public const uint HeaderVersion = 0x00020315;
    /// <summary>FMOD 2.03.00: an older fmod.dll is refused with a clear message (FMOD itself refuses another 2.xx line).</summary>
    public const uint MinVersion = 0x00020300;
    public const string CoreDll = "fmod.dll", StudioDll = "fmodstudio.dll";
    /// <summary>Environment variables: the full path of fmod.dll / fmodstudio.dll. <c>FMOD_DLL=none</c> turns FMOD off (as if not installed).</summary>
    public const string EnvCore = "FMOD_DLL", EnvStudio = "FMOD_STUDIO_DLL";
    /// <summary>The settings key both engines use (<c>auto</c> / <c>fmod</c> / <c>classic</c>).</summary>
    public const string SettingKey = "audioBackend";
    /// <summary>The FMOD Engine's default install folder (64-bit libraries under api\core\lib\x64 and api\studio\lib\x64).</summary>
    public const string InstallFolder = @"FMOD SoundSystem\FMOD Studio API Windows";

    /// <summary>Where messages go (the engines point it at their console/log). Each distinct message is written once.</summary>
    public static Action<string>? Log;
    static readonly HashSet<string> Logged = new();
    internal static void LogOnce(string message)
    {
        lock (Logged) if (!Logged.Add(message)) return;
        try { Log?.Invoke(message); } catch { }
    }

    /// <summary>Tests: behave as if FMOD weren't installed (also <c>FMOD_DLL=none</c>).</summary>
    public static bool ForceMissing { get; set; }
    public static bool Disabled => ForceMissing || IsOff(Environment.GetEnvironmentVariable(EnvCore));
    static bool IsOff(string? v) => v != null && v.Trim().ToLowerInvariant() is "none" or "off" or "0" or "no" or "false";

    // ---------------- the audioBackend setting ----------------

    /// <summary>Reads the setting: anything that isn't <c>fmod</c> or <c>classic</c> (missing, typo) is <see cref="AudioBackend.Auto"/>.</summary>
    public static AudioBackend ParseBackend(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "fmod" => AudioBackend.Fmod,
        "classic" or "windows" or "default" or "off" => AudioBackend.Classic,
        _ => AudioBackend.Auto,
    };

    public static string BackendKey(AudioBackend b) => b switch { AudioBackend.Fmod => "fmod", AudioBackend.Classic => "classic", _ => "auto" };

    /// <summary>Whether a game should use FMOD with this setting: auto and fmod both use it when it loads (fmod without it
    /// falls back to the classic audio too — a game never goes silent because of a setting).</summary>
    public static bool WantsFmod(AudioBackend b) => b != AudioBackend.Classic && !Disabled;

    // ---------------- finding the DLLs ----------------

    /// <summary>The places fmod.dll is looked for, in order: next to the program, <c>FMOD_DLL</c>, the FMOD Engine install folder.</summary>
    public static List<string> CoreCandidates(string? exeDir, string? envPath, string? programFilesX86) =>
        Candidates(exeDir, envPath, programFilesX86, CoreDll, @"api\core\lib\x64");

    /// <summary>fmodstudio.dll: next to the program, <c>FMOD_STUDIO_DLL</c>, next to the fmod.dll found, the install folder.</summary>
    public static List<string> StudioCandidates(string? exeDir, string? envPath, string? coreDir, string? programFilesX86)
    {
        var list = Candidates(exeDir, envPath, programFilesX86, StudioDll, @"api\studio\lib\x64");
        if (!string.IsNullOrEmpty(coreDir)) list.Insert(Math.Min(list.Count, string.IsNullOrEmpty(envPath) || IsOff(envPath) ? 1 : 2), Path.Combine(coreDir, StudioDll));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static List<string> Candidates(string? exeDir, string? envPath, string? programFilesX86, string file, string sub)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(exeDir)) list.Add(Path.Combine(exeDir, file));
        if (!string.IsNullOrWhiteSpace(envPath) && !IsOff(envPath))
        {
            string p = envPath.Trim().Trim('"');
            list.Add(Directory.Exists(p) ? Path.Combine(p, file) : p); // a folder or the file itself
        }
        if (!string.IsNullOrEmpty(programFilesX86)) list.Add(Path.Combine(programFilesX86, InstallFolder, sub, file));
        return list;
    }

    /// <summary>The first candidate that exists (null = not found, or FMOD turned off).</summary>
    public static string? Find(IEnumerable<string> candidates)
    {
        if (Disabled) return null;
        foreach (var c in candidates)
            try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
        return null;
    }

    static string? ProgramFilesX86 =>
        Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } p ? p : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    public static string? FindCore() => Find(CoreCandidates(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(EnvCore), ProgramFilesX86));

    public static string? FindStudio(string? coreDir = null) =>
        Find(StudioCandidates(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(EnvStudio), coreDir ?? (CorePath is { } c ? Path.GetDirectoryName(c) : null), ProgramFilesX86));

    /// <summary>FMOD Studio (the authoring tool) when installed: <c>Program Files\FMOD SoundSystem\FMOD Studio x.y.z\FMOD Studio.exe</c>, newest first.</summary>
    public static string? FindStudioApp()
    {
        try
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProgramFilesX86 })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var dir = Path.Combine(root, "FMOD SoundSystem");
                if (!Directory.Exists(dir)) continue;
                foreach (var d in Directory.GetDirectories(dir, "FMOD Studio *").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(Path.Combine(d, "FMOD Studio.exe"))) return Path.Combine(d, "FMOD Studio.exe");
            }
        }
        catch { }
        return null;
    }

    // ---------------- loading (once per process) ----------------

    static readonly object Gate = new();
    static IntPtr _core, _studio;
    static bool _coreTried, _studioTried, _resolverSet;
    public static string? CorePath { get; private set; }
    public static string? StudioPath { get; private set; }
    /// <summary>Why FMOD isn't used (empty when it loaded).</summary>
    public static string CoreMessage { get; private set; } = "";
    public static string StudioMessage { get; private set; } = "";

    /// <summary>Loads fmod.dll once. False (with <see cref="CoreMessage"/>) when it isn't there or can't load — the caller
    /// keeps its classic audio. Never throws.</summary>
    public static bool LoadCore()
    {
        lock (Gate)
        {
            if (Disabled) { CoreMessage = "FMOD is turned off (FMOD_DLL=none)."; return false; } // also after loading: a test or setting can turn it off
            if (_core != IntPtr.Zero) return true;
            if (_coreTried) return false;
            _coreTried = true;
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { CoreMessage = "FMOD is only used by the 64-bit Windows build."; return false; }
            var path = FindCore();
            if (path == null) { CoreMessage = "fmod.dll not found (put it next to the program, set FMOD_DLL, or install the FMOD Engine)."; return false; }
            if (!NativeLibrary.TryLoad(path, out _core)) { _core = IntPtr.Zero; CoreMessage = $"fmod.dll at {path} couldn't be loaded (is it the 64-bit one?)."; LogOnce("FMOD: " + CoreMessage); return false; }
            CorePath = path;
            SetResolver();
            CoreMessage = "";
            return true;
        }
    }

    /// <summary>Loads fmodstudio.dll once (after fmod.dll, which it needs). False with <see cref="StudioMessage"/>.</summary>
    public static bool LoadStudio()
    {
        if (!LoadCore()) { StudioMessage = "FMOD Studio isn't installed (" + CoreMessage + ")"; return false; }
        lock (Gate)
        {
            if (_studio != IntPtr.Zero) return true;
            if (_studioTried) return false;
            _studioTried = true;
            var path = FindStudio();
            if (path == null) { StudioMessage = "FMOD Studio isn't installed (fmodstudio.dll not found)."; return false; }
            if (!NativeLibrary.TryLoad(path, out _studio)) { _studio = IntPtr.Zero; StudioMessage = $"fmodstudio.dll at {path} couldn't be loaded."; LogOnce("FMOD: " + StudioMessage); return false; }
            StudioPath = path;
            StudioMessage = "";
            return true;
        }
    }

    public static bool CoreLoaded => _core != IntPtr.Zero;
    public static bool StudioLoaded => _studio != IntPtr.Zero;

    // [DllImport("fmod")] / [DllImport("fmodstudio")] resolve to the files found above (and nothing else: without them the
    // imports are never called).
    static void SetResolver()
    {
        if (_resolverSet) return;
        _resolverSet = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(FmodLibrary).Assembly, (name, asm, search) =>
                name == Native.Core ? _core : name == Native.Studio ? _studio : IntPtr.Zero);
        }
        catch (InvalidOperationException) { } // a resolver was already set for this assembly: the default search finds fmod.dll next to the exe
    }

    /// <summary>"2.03.15" from FMOD's 0xaaaabbcc version number.</summary>
    public static string VersionText(uint v) => $"{v >> 16:x}.{(v >> 8) & 0xFF:x2}.{v & 0xFF:x2}";

    /// <summary>A short name for an FMOD result code (the common ones; others are "error n").</summary>
    public static string ErrorText(int code) => code switch
    {
        0 => "ok",
        3 => "channel stolen",
        13 => "the file is damaged or not a sound",
        18 => "file not found",
        19 => "unsupported format",
        20 => "fmod.dll is a different FMOD version (header mismatch)",
        30 => "invalid handle",
        31 => "invalid parameter",
        45 => "no sound output",
        51 => "no sound device",
        69 => "a different FMOD version",
        74 => "event not found",
        75 => "FMOD Studio isn't initialised",
        76 => "the bank isn't loaded",
        _ => "error " + code,
    };
}

/// <summary>A playing sound in <see cref="FmodCore"/> (FMOD keeps track of stale handles: using an ended one just returns false).</summary>
public readonly record struct FmodChannel(IntPtr Handle)
{
    public bool IsNone => Handle == IntPtr.Zero;
    public static readonly FmodChannel None = default;
}

/// <summary>
/// One FMOD Core system. Short files load once into memory (cached by path); files over <see cref="StreamBytes"/> stream
/// from disk, one stream per play (released when it ends). wav, mp3, ogg, flac… — whatever FMOD reads.
/// Call <see cref="Update"/> once per frame (or from a timer).
/// </summary>
public sealed class FmodCore : IDisposable
{
    /// <summary>Files bigger than this (music) stream from disk instead of loading whole.</summary>
    public const long StreamBytes = 1_000_000;

    readonly object _lock = new();
    IntPtr _system, _master;
    readonly Dictionary<string, IntPtr> _sounds = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(IntPtr channel, IntPtr sound)> _streams = new();
    public uint Version { get; }
    public string VersionText => FmodLibrary.VersionText(Version);
    /// <summary>True when made with the NOSOUND output (tests): everything works, nothing is heard.</summary>
    public bool Silent { get; }
    public bool IsOpen => _system != IntPtr.Zero;

    FmodCore(IntPtr system, uint version, bool silent)
    {
        _system = system; Version = version; Silent = silent;
        Native.System_GetMasterChannelGroup(system, out _master);
    }

    /// <summary>
    /// Starts FMOD (null with a reason when it can't: not installed, too old, no sound device…). <paramref name="silent"/> uses
    /// FMOD's NOSOUND_NRT output: nothing is heard, sounds advance only when <see cref="Update"/> is called (tests).
    /// </summary>
    public static FmodCore? TryCreate(out string message, bool silent = false, int maxChannels = 64)
    {
        message = "";
        if (!FmodLibrary.LoadCore()) { message = FmodLibrary.CoreMessage; return null; }
        IntPtr sys = IntPtr.Zero;
        try
        {
            int r = Native.System_Create(out sys, FmodLibrary.HeaderVersion);
            if (r != 0) { message = "FMOD couldn't start: " + FmodLibrary.ErrorText(r); return null; }
            r = Native.System_GetVersion(sys, out uint version, out _);
            if (r != 0 || version < FmodLibrary.MinVersion)
            {
                message = r != 0 ? "FMOD couldn't start: " + FmodLibrary.ErrorText(r)
                    : $"fmod.dll is FMOD {FmodLibrary.VersionText(version)}, older than {FmodLibrary.VersionText(FmodLibrary.MinVersion)}: install FMOD Engine {FmodLibrary.VersionText(FmodLibrary.HeaderVersion)} or newer.";
                Native.System_Release(sys);
                return null;
            }
            if (silent) Native.System_SetOutput(sys, Native.OUTPUT_NOSOUND_NRT);
            r = Native.System_Init(sys, Math.Clamp(maxChannels, 8, 512), 0, IntPtr.Zero);
            if (r != 0) { message = "FMOD couldn't start: " + FmodLibrary.ErrorText(r); Native.System_Release(sys); return null; }
            return new FmodCore(sys, version, silent);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            message = "fmod.dll couldn't be used: " + e.Message;
            if (sys != IntPtr.Zero) try { Native.System_Release(sys); } catch { }
            return null;
        }
    }

    bool Ok(int r, string what)
    {
        if (r == 0) return true;
        if (r != 30 && r != 3) FmodLibrary.LogOnce($"FMOD: {what}: {FmodLibrary.ErrorText(r)}"); // a stale channel is normal (it ended)
        return false;
    }

    /// <summary>Lets FMOD do its work (streams, ended sounds). Once per frame.</summary>
    public bool Update()
    {
        lock (_lock)
        {
            if (_system == IntPtr.Zero) return false;
            for (int i = _streams.Count - 1; i >= 0; i--) // a stream that ended is released (its channel is free again)
            {
                var (ch, snd) = _streams[i];
                if (Native.Channel_IsPlaying(ch, out int on) == 0 && on != 0) continue;
                Native.Sound_Release(snd);
                _streams.RemoveAt(i);
            }
            return Ok(Native.System_Update(_system), "update");
        }
    }

    /// <summary>Opens a short sound now so its first play has no delay (long files stream and aren't preloaded).</summary>
    public bool Preload(string path)
    {
        lock (_lock) return _system != IntPtr.Zero && (IsLong(path) || Sample(path) != IntPtr.Zero);
    }

    static bool IsLong(string path) { try { return new FileInfo(path).Length > StreamBytes; } catch { return false; } }

    IntPtr Sample(string path)
    {
        if (_sounds.TryGetValue(path, out var s)) return s;
        if (!File.Exists(path)) { FmodLibrary.LogOnce("FMOD: sound file not found: " + path); return IntPtr.Zero; }
        int r = Native.System_CreateSound(_system, Utf8(path), Native.MODE_LOOP_OFF | Native.MODE_2D | Native.MODE_CREATESAMPLE, IntPtr.Zero, out s);
        if (!Ok(r, "open " + Path.GetFileName(path))) return IntPtr.Zero;
        return _sounds[path] = s;
    }

    /// <summary>Plays a sound file. <paramref name="volume"/> 0..1, <paramref name="pan"/> −1 left … +1 right, <paramref name="pitch"/>
    /// 1 = normal. Returns the channel (<see cref="FmodChannel.None"/> when it can't play — logged once).</summary>
    public FmodChannel Play(string path, double volume = 1, double pan = 0, bool loop = false, double pitch = 1, bool paused = false)
    {
        lock (_lock)
        {
            if (_system == IntPtr.Zero || string.IsNullOrEmpty(path)) return FmodChannel.None;
            try
            {
                if (IsLong(path))
                {
                    uint mode = (loop ? Native.MODE_LOOP_NORMAL : Native.MODE_LOOP_OFF) | Native.MODE_2D | Native.MODE_CREATESTREAM;
                    if (!Ok(Native.System_CreateStream(_system, Utf8(path), mode, IntPtr.Zero, out var stream), "open " + Path.GetFileName(path))) return FmodChannel.None;
                    var ch = Start(stream, volume, pan, loop, pitch, paused);
                    if (ch.IsNone) Native.Sound_Release(stream); else _streams.Add((ch.Handle, stream));
                    return ch;
                }
                var s = Sample(path);
                return s == IntPtr.Zero ? FmodChannel.None : Start(s, volume, pan, loop, pitch, paused);
            }
            catch (Exception e) { FmodLibrary.LogOnce("FMOD: " + e.Message); return FmodChannel.None; }
        }
    }

    /// <summary>Plays a whole sound file held in memory (e.g. a WAV made by a sound editor). The sound is released when it
    /// ends or is stopped.</summary>
    public FmodChannel PlayMemory(byte[] data, double volume = 1, double pan = 0, bool loop = false, double pitch = 1)
    {
        lock (_lock)
        {
            if (_system == IntPtr.Zero || data.Length == 0) return FmodChannel.None;
            var info = new Native.CREATESOUNDEXINFO { cbsize = Marshal.SizeOf<Native.CREATESOUNDEXINFO>(), length = (uint)data.Length };
            var pin = GCHandle.Alloc(info, GCHandleType.Pinned);
            try
            {
                uint mode = (loop ? Native.MODE_LOOP_NORMAL : Native.MODE_LOOP_OFF) | Native.MODE_2D | Native.MODE_CREATESAMPLE | Native.MODE_OPENMEMORY;
                if (!Ok(Native.System_CreateSoundMem(_system, data, mode, pin.AddrOfPinnedObject(), out var s), "open sound from memory")) return FmodChannel.None;
                var ch = Start(s, volume, pan, loop, pitch, false);
                if (ch.IsNone) Native.Sound_Release(s); else _streams.Add((ch.Handle, s)); // released by Update once it ends
                return ch;
            }
            finally { pin.Free(); }
        }
    }

    FmodChannel Start(IntPtr sound, double volume, double pan, bool loop, double pitch, bool paused)
    {
        if (!Ok(Native.System_PlaySound(_system, sound, IntPtr.Zero, 1, out var ch), "play")) return FmodChannel.None;
        Native.Channel_SetVolume(ch, (float)Math.Clamp(volume, 0, 4));
        if (pan != 0) Native.Channel_SetPan(ch, (float)Math.Clamp(pan, -1, 1));
        if (pitch != 1) Native.Channel_SetPitch(ch, (float)Math.Clamp(pitch, 0.01, 16));
        if (loop) { Native.Channel_SetMode(ch, Native.MODE_LOOP_NORMAL); Native.Channel_SetLoopCount(ch, -1); }
        if (!paused) Native.Channel_SetPaused(ch, 0);
        return new FmodChannel(ch);
    }

    // Channel controls: false when the channel ended (or was never playing). No allocation: safe every frame.
    public bool SetVolume(FmodChannel ch, double volume) { lock (_lock) return Live(ch) && Ok(Native.Channel_SetVolume(ch.Handle, (float)Math.Clamp(volume, 0, 4)), "volume"); }
    public bool SetPan(FmodChannel ch, double pan) { lock (_lock) return Live(ch) && Ok(Native.Channel_SetPan(ch.Handle, (float)Math.Clamp(pan, -1, 1)), "pan"); }
    public bool SetPitch(FmodChannel ch, double pitch) { lock (_lock) return Live(ch) && Ok(Native.Channel_SetPitch(ch.Handle, (float)Math.Clamp(pitch, 0.01, 16)), "pitch"); }
    public bool SetPaused(FmodChannel ch, bool paused) { lock (_lock) return Live(ch) && Ok(Native.Channel_SetPaused(ch.Handle, paused ? 1 : 0), "pause"); }
    public bool SetLoop(FmodChannel ch, bool loop)
    {
        lock (_lock)
            return Live(ch) && Ok(Native.Channel_SetMode(ch.Handle, loop ? Native.MODE_LOOP_NORMAL : Native.MODE_LOOP_OFF), "loop")
                && (!loop || Ok(Native.Channel_SetLoopCount(ch.Handle, -1), "loop"));
    }
    public bool Stop(FmodChannel ch) { lock (_lock) return Live(ch) && Ok(Native.Channel_Stop(ch.Handle), "stop"); }
    public bool IsPlaying(FmodChannel ch)
    {
        lock (_lock) return Live(ch) && Native.Channel_IsPlaying(ch.Handle, out int on) == 0 && on != 0;
    }
    bool Live(FmodChannel ch) => _system != IntPtr.Zero && !ch.IsNone;

    /// <summary>Stops every sound (the master channel group).</summary>
    public void StopAll() { lock (_lock) if (_system != IntPtr.Zero && _master != IntPtr.Zero) Ok(Native.ChannelGroup_Stop(_master), "stop all"); }

    /// <summary>The volume of everything (0..1).</summary>
    public bool SetMasterVolume(double volume) { lock (_lock) return _system != IntPtr.Zero && _master != IntPtr.Zero && Ok(Native.ChannelGroup_SetVolume(_master, (float)Math.Clamp(volume, 0, 1)), "master volume"); }

    /// <summary>Forgets a file opened before (it changed on disk): the next play opens it again.</summary>
    public void Forget(string path) { lock (_lock) if (_sounds.Remove(path, out var s)) Native.Sound_Release(s); }

    /// <summary>How many short sounds are kept open (tests).</summary>
    public int CachedSounds { get { lock (_lock) return _sounds.Count; } }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_system == IntPtr.Zero) return;
            try
            {
                foreach (var (_, s) in _streams) Native.Sound_Release(s);
                foreach (var s in _sounds.Values) Native.Sound_Release(s);
                Native.System_Release(_system); // closes the output and frees everything left
            }
            catch { }
            _streams.Clear(); _sounds.Clear();
            _system = _master = IntPtr.Zero;
        }
    }

    // UTF-8 path/name with its 0 at the end (FMOD reads UTF-8 on every platform).
    internal static byte[] Utf8(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }
}

/// <summary>The FMOD C functions used (FMOD 2.03: core exports are FMOD5_*, Studio exports FMOD_Studio_*). FMOD_BOOL is an int.</summary>
static class Native
{
    public const string Core = "fmod", Studio = "fmodstudio";
    public const uint MODE_LOOP_OFF = 0x1, MODE_LOOP_NORMAL = 0x2, MODE_2D = 0x8, MODE_CREATESTREAM = 0x80, MODE_CREATESAMPLE = 0x100, MODE_OPENMEMORY = 0x800;
    public const int OUTPUT_NOSOUND_NRT = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct CREATESOUNDEXINFO
    {
        public int cbsize; public uint length, fileoffset; public int numchannels, defaultfrequency, format; public uint decodebuffersize;
        public int initialsubsound, numsubsounds; public IntPtr inclusionlist; public int inclusionlistnum;
        public IntPtr pcmreadcallback, pcmsetposcallback, nonblockcallback, dlsname, encryptionkey; public int maxpolyphony;
        public IntPtr userdata; public int suggestedsoundtype;
        public IntPtr fileuseropen, fileuserclose, fileuserread, fileuserseek, fileuserasyncread, fileuserasynccancel, fileuserdata;
        public int filebuffersize, channelorder; public IntPtr initialsoundgroup; public uint initialseekposition, initialseekpostype;
        public int ignoresetfilesystem; public uint audioqueuepolicy, minmidigranularity; public int nonblockthreadid; public IntPtr fsbguid;
    }

    [DllImport(Core, EntryPoint = "FMOD5_System_Create")] public static extern int System_Create(out IntPtr system, uint headerversion);
    [DllImport(Core, EntryPoint = "FMOD5_System_Release")] public static extern int System_Release(IntPtr system);
    [DllImport(Core, EntryPoint = "FMOD5_System_SetOutput")] public static extern int System_SetOutput(IntPtr system, int output);
    [DllImport(Core, EntryPoint = "FMOD5_System_Init")] public static extern int System_Init(IntPtr system, int maxchannels, uint flags, IntPtr extradriverdata);
    [DllImport(Core, EntryPoint = "FMOD5_System_Update")] public static extern int System_Update(IntPtr system);
    [DllImport(Core, EntryPoint = "FMOD5_System_GetVersion")] public static extern int System_GetVersion(IntPtr system, out uint version, out uint buildnumber);
    [DllImport(Core, EntryPoint = "FMOD5_System_CreateSound")] public static extern int System_CreateSound(IntPtr system, byte[] name, uint mode, IntPtr exinfo, out IntPtr sound);
    [DllImport(Core, EntryPoint = "FMOD5_System_CreateSound")] public static extern int System_CreateSoundMem(IntPtr system, byte[] data, uint mode, IntPtr exinfo, out IntPtr sound);
    [DllImport(Core, EntryPoint = "FMOD5_System_CreateStream")] public static extern int System_CreateStream(IntPtr system, byte[] name, uint mode, IntPtr exinfo, out IntPtr sound);
    [DllImport(Core, EntryPoint = "FMOD5_System_PlaySound")] public static extern int System_PlaySound(IntPtr system, IntPtr sound, IntPtr channelgroup, int paused, out IntPtr channel);
    [DllImport(Core, EntryPoint = "FMOD5_System_GetMasterChannelGroup")] public static extern int System_GetMasterChannelGroup(IntPtr system, out IntPtr channelgroup);
    [DllImport(Core, EntryPoint = "FMOD5_Sound_Release")] public static extern int Sound_Release(IntPtr sound);
    [DllImport(Core, EntryPoint = "FMOD5_Channel_SetVolume")] public static extern int Channel_SetVolume(IntPtr channel, float volume);
    [DllImport(Core, EntryPoint = "FMOD5_Channel_SetPan")] public static extern int Channel_SetPan(IntPtr channel, float pan);
    [DllImport(Core, EntryPoint = "FMOD5_Channel_SetPitch")] public static extern int Channel_SetPitch(IntPtr channel, float pitch);
    [DllImport(Core, EntryPoint = "FMOD5_Channel_SetPaused")] public static extern int Channel_SetPaused(IntPtr channel, int paused);
    [DllImport(Core, EntryPoint = "FMOD5_Channel_SetMode")] public static extern int Channel_SetMode(IntPtr channel, uint mode);
    [DllImport(Core, EntryPoint = "FMOD5_Channel_SetLoopCount")] public static extern int Channel_SetLoopCount(IntPtr channel, int loopcount);
    [DllImport(Core, EntryPoint = "FMOD5_Channel_Stop")] public static extern int Channel_Stop(IntPtr channel);
    [DllImport(Core, EntryPoint = "FMOD5_Channel_IsPlaying")] public static extern int Channel_IsPlaying(IntPtr channel, out int isplaying);
    [DllImport(Core, EntryPoint = "FMOD5_ChannelGroup_Stop")] public static extern int ChannelGroup_Stop(IntPtr group);
    [DllImport(Core, EntryPoint = "FMOD5_ChannelGroup_SetVolume")] public static extern int ChannelGroup_SetVolume(IntPtr group, float volume);
}
