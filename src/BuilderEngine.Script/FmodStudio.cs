using System.Runtime.InteropServices;

namespace BuilderEngine.Script;

/// <summary>A parameter an FMOD Studio event (or the whole game, <see cref="Global"/>) can be set to.</summary>
public sealed record FmodParamInfo(string Name, double Min, double Max, double Default, bool Global, bool Discrete);

/// <summary>An event in the loaded banks: its path (<c>event:/Music/Forest</c>) and the parameters the game can set.</summary>
public sealed record FmodEventInfo(string Path, IReadOnlyList<FmodParamInfo> Parameters, bool Is3D, bool OneShot)
{
    /// <summary>The short name people see: <c>event:/Music/Forest</c> → <c>Music/Forest</c>.</summary>
    public string Short => Path.StartsWith("event:/", StringComparison.OrdinalIgnoreCase) ? Path[7..] : Path;
}

/// <summary>A started FMOD Studio event (a stale handle is harmless: calls on it return false).</summary>
public readonly record struct FmodEvent(IntPtr Handle)
{
    public bool IsNone => Handle == IntPtr.Zero;
    public static readonly FmodEvent None = default;
}

/// <summary>
/// FMOD Studio: banks built by FMOD Studio (the free authoring tool) into a project's <c>fmod</c> folder, their events and
/// parameters. In FMOD Studio: File → Preferences → Build (or the project's build settings) → built banks output directory =
/// <c>&lt;project&gt;\fmod</c>; Build (F7) writes <c>fmod\Desktop\Master.bank</c>, <c>Master.strings.bank</c> and the other banks.
/// Optional like <see cref="FmodCore"/>: <see cref="TryCreate"/> returns null with "FMOD Studio isn't installed" when
/// fmodstudio.dll isn't found. Never throws into a game loop; one lock per system (see FmodCore.cs).
/// </summary>
public sealed class FmodStudio : IDisposable
{
    /// <summary>The folder in a game project that holds the built banks.</summary>
    public const string FolderName = "fmod";
    /// <summary>How many game pixels make one FMOD distance unit (3D events: an event's min/max distance in FMOD Studio is in these units).</summary>
    public const double PixelsPerUnit = 100;

    readonly object _lock = new();
    IntPtr _system;
    readonly List<IntPtr> _banks = new();
    readonly Dictionary<string, IntPtr> _descs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte[]> _names = new(StringComparer.Ordinal);
    List<FmodEventInfo> _events = new();
    public bool Silent { get; }
    /// <summary>The bank files loaded (file names), and the folder they came from.</summary>
    public IReadOnlyList<string> BankFiles => _bankFiles;
    readonly List<string> _bankFiles = new();
    public string? BankFolder { get; private set; }
    public IReadOnlyList<FmodEventInfo> Events => _events;

    FmodStudio(IntPtr system, bool silent) { _system = system; Silent = silent; }

    /// <summary>Starts FMOD Studio (null + reason when it isn't installed or can't start). <paramref name="silent"/>: NOSOUND_NRT
    /// output and synchronous updates — nothing is heard and events advance only on <see cref="Update"/> (tests, listing).</summary>
    public static FmodStudio? TryCreate(out string message, bool silent = false)
    {
        message = "";
        if (!FmodLibrary.LoadStudio()) { message = FmodLibrary.StudioMessage; return null; }
        IntPtr sys = IntPtr.Zero;
        try
        {
            int r = StudioNative.System_Create(out sys, FmodLibrary.HeaderVersion);
            if (r != 0) { message = "FMOD Studio couldn't start: " + FmodLibrary.ErrorText(r); return null; }
            if (StudioNative.System_GetCoreSystem(sys, out var core) == 0)
            {
                if (Native.System_GetVersion(core, out uint version, out _) == 0 && version < FmodLibrary.MinVersion)
                {
                    message = $"fmodstudio.dll is FMOD {FmodLibrary.VersionText(version)}, older than {FmodLibrary.VersionText(FmodLibrary.MinVersion)}.";
                    StudioNative.System_Release(sys);
                    return null;
                }
                if (silent) Native.System_SetOutput(core, Native.OUTPUT_NOSOUND_NRT);
            }
            r = StudioNative.System_Initialize(sys, 256, silent ? StudioNative.INIT_SYNCHRONOUS_UPDATE : 0, 0, IntPtr.Zero);
            if (r != 0) { message = "FMOD Studio couldn't start: " + FmodLibrary.ErrorText(r); StudioNative.System_Release(sys); return null; }
            return new FmodStudio(sys, silent);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            message = "fmodstudio.dll couldn't be used: " + e.Message;
            if (sys != IntPtr.Zero) try { StudioNative.System_Release(sys); } catch { }
            return null;
        }
    }

    bool Ok(int r, string what)
    {
        if (r == 0) return true;
        if (r != 30) FmodLibrary.LogOnce($"FMOD Studio: {what}: {FmodLibrary.ErrorText(r)}");
        return false;
    }

    // ---------------- banks ----------------

    /// <summary>The folder with the banks of a game project: <c>fmod</c>, or the <c>fmod\Desktop</c> FMOD Studio builds into
    /// (whichever holds Master.bank; else the first with any .bank). Null when the project has none.</summary>
    public static string? FindBankFolder(string projectDir)
    {
        if (string.IsNullOrEmpty(projectDir)) return null;
        try
        {
            var root = Path.Combine(projectDir, FolderName);
            if (!Directory.Exists(root)) return null;
            var places = new List<string> { root, Path.Combine(root, "Desktop") };
            places.AddRange(Directory.GetDirectories(root).Where(d => !places.Contains(d, StringComparer.OrdinalIgnoreCase)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
            foreach (var p in places) if (File.Exists(Path.Combine(p, "Master.bank"))) return p;
            foreach (var p in places) if (Directory.Exists(p) && Directory.EnumerateFiles(p, "*.bank").Any()) return p;
        }
        catch { }
        return null;
    }

    /// <summary>The order banks load in: Master.bank, Master.strings.bank (event names), then the others by name.</summary>
    public static List<string> BankOrder(IEnumerable<string> files) =>
        files.OrderBy(f => Path.GetFileName(f).Equals("Master.bank", StringComparison.OrdinalIgnoreCase) ? 0
                         : Path.GetFileName(f).Equals("Master.strings.bank", StringComparison.OrdinalIgnoreCase) ? 1
                         : Path.GetFileName(f).StartsWith("Master", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
             .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Unloads what was loaded and loads every bank in <paramref name="folder"/> (a project's bank folder, see
    /// <see cref="FindBankFolder"/>). Returns how many loaded; <paramref name="message"/> says what went wrong, if anything.</summary>
    public int LoadBanks(string folder, out string message)
    {
        message = "";
        lock (_lock)
        {
            if (_system == IntPtr.Zero) { message = "FMOD Studio isn't running."; return 0; }
            UnloadLocked();
            string[] files;
            try { files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.bank") : []; }
            catch (Exception e) { message = e.Message; return 0; }
            if (files.Length == 0) { message = $"No .bank files in {folder} (build the FMOD Studio project into the game's \"{FolderName}\" folder)."; return 0; }
            var problems = new List<string>();
            foreach (var f in BankOrder(files))
            {
                int r = StudioNative.System_LoadBankFile(_system, FmodCore.Utf8(f), 0, out var bank);
                if (r == 0) { _banks.Add(bank); _bankFiles.Add(Path.GetFileName(f)); }
                else problems.Add($"{Path.GetFileName(f)}: {FmodLibrary.ErrorText(r)}");
            }
            BankFolder = folder;
            ReadEvents();
            if (!_bankFiles.Any(b => b.Equals("Master.strings.bank", StringComparison.OrdinalIgnoreCase)) && _banks.Count > 0)
                problems.Add("Master.strings.bank is missing: events can't be found by name");
            message = string.Join("; ", problems);
            return _banks.Count;
        }
    }

    /// <summary>Loads the banks of a game project (its <c>fmod</c> folder). 0 with a message when it has none.</summary>
    public int LoadProject(string projectDir, out string message)
    {
        var folder = FindBankFolder(projectDir);
        if (folder == null) { message = $"This project has no FMOD banks (build them into its \"{FolderName}\" folder)."; lock (_lock) UnloadLocked(); return 0; }
        return LoadBanks(folder, out message);
    }

    public void UnloadAll() { lock (_lock) UnloadLocked(); }

    void UnloadLocked()
    {
        if (_system != IntPtr.Zero && _banks.Count > 0) StudioNative.System_UnloadAll(_system);
        _banks.Clear(); _bankFiles.Clear(); _descs.Clear(); _events = new();
        BankFolder = null;
    }

    void ReadEvents()
    {
        var list = new List<FmodEventInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buf = new byte[512];
        foreach (var bank in _banks)
        {
            if (StudioNative.Bank_GetEventCount(bank, out int n) != 0 || n <= 0) continue;
            var arr = new IntPtr[n];
            if (StudioNative.Bank_GetEventList(bank, arr, n, out int got) != 0) continue;
            for (int i = 0; i < got; i++)
            {
                var desc = arr[i];
                if (StudioNative.EventDescription_GetPath(desc, buf, buf.Length, out int len) != 0 || len <= 1) continue; // no strings bank: no names
                string path = System.Text.Encoding.UTF8.GetString(buf, 0, len - 1);
                if (!seen.Add(path)) continue;
                _descs[path] = desc;
                var ps = new List<FmodParamInfo>();
                if (StudioNative.EventDescription_GetParameterDescriptionCount(desc, out int pc) == 0)
                    for (int k = 0; k < pc; k++)
                        if (StudioNative.EventDescription_GetParameterDescriptionByIndex(desc, k, out var pd) == 0 && (pd.flags & (StudioNative.PARAM_READONLY | StudioNative.PARAM_AUTOMATIC)) == 0)
                            ps.Add(new FmodParamInfo(Marshal.PtrToStringUTF8(pd.name) ?? "", pd.minimum, pd.maximum, pd.defaultvalue,
                                (pd.flags & StudioNative.PARAM_GLOBAL) != 0, (pd.flags & StudioNative.PARAM_DISCRETE) != 0));
                StudioNative.EventDescription_Is3D(desc, out int is3d);
                StudioNative.EventDescription_IsOneshot(desc, out int oneshot);
                list.Add(new FmodEventInfo(path, ps, is3d != 0, oneshot != 0));
            }
        }
        list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        _events = list;
    }

    /// <summary>The event with this path (<c>event:/Music/Forest</c>; <c>Music/Forest</c> works too), or null.</summary>
    public FmodEventInfo? FindEvent(string path)
    {
        var p = NormalizePath(path);
        lock (_lock) return _events.FirstOrDefault(e => e.Path.Equals(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>Music/Forest</c>, <c>/Music/Forest</c> and <c>event:/Music/Forest</c> all name <c>event:/Music/Forest</c>.</summary>
    public static string NormalizePath(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/');
        if (p.Length == 0 || p.StartsWith("event:", StringComparison.OrdinalIgnoreCase) || p.StartsWith("snapshot:", StringComparison.OrdinalIgnoreCase) || p.StartsWith("{")) return p;
        return "event:/" + p.TrimStart('/');
    }

    // ---------------- events ----------------

    IntPtr Desc(string path)
    {
        var p = NormalizePath(path);
        if (_descs.TryGetValue(p, out var d)) return d;
        if (StudioNative.System_GetEvent(_system, Name(p), out d) != 0) { FmodLibrary.LogOnce($"FMOD Studio: event not found: {p}"); return IntPtr.Zero; }
        return _descs[p] = d;
    }

    /// <summary>Starts an event. <paramref name="keep"/> false = fire and forget (released when it ends, the handle can still
    /// stop it until then); true = the caller releases it (<see cref="Release"/>) — for one it will set parameters on or restart.</summary>
    public FmodEvent Play(string path, double volume = 1, bool keep = true)
    {
        lock (_lock)
        {
            if (_system == IntPtr.Zero || string.IsNullOrWhiteSpace(path)) return FmodEvent.None;
            try
            {
                var d = Desc(path);
                if (d == IntPtr.Zero || !Ok(StudioNative.EventDescription_CreateInstance(d, out var inst), "create " + path)) return FmodEvent.None;
                if (volume != 1) StudioNative.EventInstance_SetVolume(inst, (float)Math.Clamp(volume, 0, 4));
                if (!Ok(StudioNative.EventInstance_Start(inst), "start " + path)) { StudioNative.EventInstance_Release(inst); return FmodEvent.None; }
                if (!keep) StudioNative.EventInstance_Release(inst);
                return new FmodEvent(inst);
            }
            catch (Exception e) { FmodLibrary.LogOnce("FMOD Studio: " + e.Message); return FmodEvent.None; }
        }
    }

    /// <summary>Stops an event: <paramref name="fade"/> lets it fade out as designed in FMOD Studio (AHDSR), else at once.</summary>
    public bool Stop(FmodEvent ev, bool fade = true) { lock (_lock) return Live(ev) && Ok(StudioNative.EventInstance_Stop(ev.Handle, fade ? 0 : 1), "stop"); }
    /// <summary>Lets FMOD free the event once it has stopped (call after <see cref="Stop"/> for a kept event).</summary>
    public bool Release(FmodEvent ev) { lock (_lock) return Live(ev) && Ok(StudioNative.EventInstance_Release(ev.Handle), "release"); }
    public bool IsPlaying(FmodEvent ev)
    {
        lock (_lock) return Live(ev) && StudioNative.EventInstance_GetPlaybackState(ev.Handle, out int s) == 0 && s != StudioNative.PLAYBACK_STOPPED;
    }
    public bool SetVolume(FmodEvent ev, double volume) { lock (_lock) return Live(ev) && Ok(StudioNative.EventInstance_SetVolume(ev.Handle, (float)Math.Clamp(volume, 0, 4)), "volume"); }
    /// <summary>Sets a parameter of one event (by its name in FMOD Studio).</summary>
    public bool SetParameter(FmodEvent ev, string name, double value)
    {
        lock (_lock) return Live(ev) && !string.IsNullOrWhiteSpace(name) && Ok(StudioNative.EventInstance_SetParameterByName(ev.Handle, Name(name.Trim()), (float)value, 0), $"parameter \"{name}\"");
    }
    /// <summary>Sets a global parameter (one for the whole game, made global in FMOD Studio).</summary>
    public bool SetGlobalParameter(string name, double value)
    {
        lock (_lock) return _system != IntPtr.Zero && !string.IsNullOrWhiteSpace(name) && Ok(StudioNative.System_SetParameterByName(_system, Name(name.Trim()), (float)value, 0), $"global parameter \"{name}\"");
    }

    /// <summary>Places a 3D event at a game position in pixels (x right, y down; <see cref="PixelsPerUnit"/> pixels = 1 FMOD unit).</summary>
    public bool SetPosition(FmodEvent ev, double x, double y)
    {
        lock (_lock)
        {
            if (!Live(ev)) return false;
            var a = Attributes(x, y);
            return StudioNative.EventInstance_Set3DAttributes(ev.Handle, ref a) == 0; // a 2D event says "needs 3D": not an error worth logging
        }
    }

    /// <summary>Where the game listens from (the camera or the player), in pixels.</summary>
    public bool SetListener(double x, double y)
    {
        lock (_lock)
        {
            if (_system == IntPtr.Zero) return false;
            var a = Attributes(x, y);
            return Ok(StudioNative.System_SetListenerAttributes(_system, 0, ref a, IntPtr.Zero), "listener");
        }
    }

    static StudioNative.ATTRIBUTES_3D Attributes(double x, double y) => new()
    {
        px = (float)(x / PixelsPerUnit), py = (float)(-y / PixelsPerUnit), // screen y goes down, FMOD's y goes up
        fz = 1, uy = 1,
    };

    bool Live(FmodEvent ev) => _system != IntPtr.Zero && !ev.IsNone;

    /// <summary>Lets FMOD Studio do its work. Once per frame.</summary>
    public bool Update() { lock (_lock) return _system != IntPtr.Zero && Ok(StudioNative.System_Update(_system), "update"); }

    byte[] Name(string s)
    {
        if (_names.TryGetValue(s, out var b)) return b;
        b = FmodCore.Utf8(s);
        if (_names.Count < 4096) _names[s] = b; // the few names a game uses, made once (no allocation per frame)
        return b;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_system == IntPtr.Zero) return;
            try { StudioNative.System_Release(_system); } catch { } // unloads the banks and stops every event
            _system = IntPtr.Zero;
            _banks.Clear(); _bankFiles.Clear(); _descs.Clear(); _events = new();
        }
    }

    // ---------------- listing events for the editors' pickers ----------------

    static readonly object CacheGate = new();
    static (string folder, DateTime stamp, List<FmodEventInfo> events, string message)? _cache;

    /// <summary>
    /// The events (and their parameters) in a game project's banks, for pickers: read with a silent FMOD Studio that is closed
    /// again, cached until a bank file changes. Empty with a message when FMOD Studio isn't installed or there are no banks.
    /// </summary>
    public static IReadOnlyList<FmodEventInfo> ListEvents(string projectDir, out string message)
    {
        var folder = FindBankFolder(projectDir);
        if (folder == null)
        {
            message = FmodLibrary.LoadStudio() ? $"No FMOD banks in this project yet (build your FMOD Studio project into its \"{FolderName}\" folder)." : FmodLibrary.StudioMessage;
            return [];
        }
        DateTime stamp;
        try { stamp = Directory.GetFiles(folder, "*.bank").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max(); }
        catch { stamp = DateTime.MinValue; }
        lock (CacheGate)
        {
            if (_cache is { } c && string.Equals(c.folder, folder, StringComparison.OrdinalIgnoreCase) && c.stamp == stamp) { message = c.message; return c.events; }
            var studio = TryCreate(out message, silent: true);
            if (studio == null) return [];
            using (studio)
            {
                int n = studio.LoadBanks(folder, out message);
                var events = studio.Events.ToList();
                if (n > 0 && message.Length == 0) message = $"{n} bank(s), {events.Count} event(s)";
                _cache = (folder, stamp, events, message);
                return events;
            }
        }
    }

    /// <summary>Forgets the cached event list (after "Reload banks").</summary>
    public static void ClearCache() { lock (CacheGate) _cache = null; }
}

/// <summary>The FMOD Studio C functions used (fmodstudio.dll).</summary>
static class StudioNative
{
    const string Dll = Native.Studio;
    public const uint INIT_SYNCHRONOUS_UPDATE = 0x4;
    public const uint PARAM_READONLY = 0x1, PARAM_AUTOMATIC = 0x2, PARAM_GLOBAL = 0x4, PARAM_DISCRETE = 0x8;
    public const int PLAYBACK_STOPPED = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct PARAMETER_DESCRIPTION
    {
        public IntPtr name; public uint id1, id2; public float minimum, maximum, defaultvalue; public int type; public uint flags;
        public uint g1; public ushort g2, g3; public uint g4a, g4b; // FMOD_GUID (Data4 is 8 bytes at offset 44)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ATTRIBUTES_3D
    {
        public float px, py, pz, vx, vy, vz, fx, fy, fz, ux, uy, uz; // position, velocity, forward, up
    }

    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_Create")] public static extern int System_Create(out IntPtr system, uint headerversion);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_GetCoreSystem")] public static extern int System_GetCoreSystem(IntPtr system, out IntPtr core);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_Initialize")] public static extern int System_Initialize(IntPtr system, int maxchannels, uint studioflags, uint flags, IntPtr extradriverdata);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_Release")] public static extern int System_Release(IntPtr system);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_Update")] public static extern int System_Update(IntPtr system);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_LoadBankFile")] public static extern int System_LoadBankFile(IntPtr system, byte[] filename, uint flags, out IntPtr bank);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_UnloadAll")] public static extern int System_UnloadAll(IntPtr system);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_GetEvent")] public static extern int System_GetEvent(IntPtr system, byte[] path, out IntPtr desc);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_SetParameterByName")] public static extern int System_SetParameterByName(IntPtr system, byte[] name, float value, int ignoreseekspeed);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_System_SetListenerAttributes")] public static extern int System_SetListenerAttributes(IntPtr system, int index, ref ATTRIBUTES_3D attributes, IntPtr attenuationposition);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_Bank_GetEventCount")] public static extern int Bank_GetEventCount(IntPtr bank, out int count);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_Bank_GetEventList")] public static extern int Bank_GetEventList(IntPtr bank, [Out] IntPtr[] array, int capacity, out int count);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventDescription_GetPath")] public static extern int EventDescription_GetPath(IntPtr desc, [Out] byte[] path, int size, out int retrieved);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventDescription_GetParameterDescriptionCount")] public static extern int EventDescription_GetParameterDescriptionCount(IntPtr desc, out int count);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventDescription_GetParameterDescriptionByIndex")] public static extern int EventDescription_GetParameterDescriptionByIndex(IntPtr desc, int index, out PARAMETER_DESCRIPTION parameter);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventDescription_Is3D")] public static extern int EventDescription_Is3D(IntPtr desc, out int is3d);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventDescription_IsOneshot")] public static extern int EventDescription_IsOneshot(IntPtr desc, out int oneshot);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventDescription_CreateInstance")] public static extern int EventDescription_CreateInstance(IntPtr desc, out IntPtr instance);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventInstance_Start")] public static extern int EventInstance_Start(IntPtr instance);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventInstance_Stop")] public static extern int EventInstance_Stop(IntPtr instance, int mode);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventInstance_Release")] public static extern int EventInstance_Release(IntPtr instance);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventInstance_GetPlaybackState")] public static extern int EventInstance_GetPlaybackState(IntPtr instance, out int state);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventInstance_SetVolume")] public static extern int EventInstance_SetVolume(IntPtr instance, float volume);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventInstance_SetParameterByName")] public static extern int EventInstance_SetParameterByName(IntPtr instance, byte[] name, float value, int ignoreseekspeed);
    [DllImport(Dll, EntryPoint = "FMOD_Studio_EventInstance_Set3DAttributes")] public static extern int EventInstance_Set3DAttributes(IntPtr instance, ref ATTRIBUTES_3D attributes);
}
