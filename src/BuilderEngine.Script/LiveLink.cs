using System.Security.Cryptography;
using System.Text;

namespace BuilderEngine.Script;

/// <summary>
/// Watches the file a project is saved in (a <c>.eeproj</c>, or a <c>project.json</c> plus its <c>mechanisms/*.bs</c>)
/// so another engine's — or another BuilderEngine window's — saves show up (docs/EASYENGINE-LINK.md, "Live link").
/// <list type="bullet">
/// <item>File events are debounced (<see cref="DebounceMs"/>): a burst of writes is one change.</item>
/// <item>The "state" is the file's text (plus the mechanism files for a <c>project.json</c>); its hash is compared with
/// the last state this side wrote or loaded, so our own saves (echoes) and rewrites with the same content are ignored.</item>
/// <item><see cref="Changed"/> is raised on a background thread; the editor applies it on its UI thread.</item>
/// </list>
/// UI-free, so the debounce and echo logic is tested headless.
/// </summary>
public sealed class LiveLink : IDisposable
{
    /// <summary>The file the project is saved in.</summary>
    public string FilePath { get; }
    public string Folder { get; }
    /// <summary>A <c>project.json</c>: its <c>mechanisms/*.bs</c> count as part of the project.</summary>
    public bool IncludesMechanisms { get; }
    public int DebounceMs { get; }
    /// <summary>The project changed on disk (not by this side). Raised on a background thread, after the debounce.</summary>
    public event Action? Changed;

    readonly object _gate = new();
    readonly Timer _timer;
    FileSystemWatcher? _watcher;
    string? _known;
    int _writing, _retries;
    bool _disposed;

    public LiveLink(string filePath, int debounceMs = 300)
    {
        FilePath = Path.GetFullPath(filePath);
        Folder = Path.GetDirectoryName(FilePath)!;
        IncludesMechanisms = string.Equals(Path.GetFileName(FilePath), Project.FileName, StringComparison.OrdinalIgnoreCase);
        DebounceMs = Math.Max(10, debounceMs);
        _timer = new Timer(_ => Fire());
    }

    /// <summary>Starts watching. Remembers what is on disk now as known (call <see cref="Remember"/> after loading instead when it may differ).</summary>
    public LiveLink Start()
    {
        _known ??= Hash(ReadState());
        _watcher = new FileSystemWatcher(Folder)
        {
            IncludeSubdirectories = IncludesMechanisms,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += (s, e) => { if (Matters(e.FullPath) || Matters(e.OldFullPath)) Poke(); };
        _watcher.EnableRaisingEvents = true;
        return this;
    }

    void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (Matters(e.FullPath)) Poke();
    }

    /// <summary>A file event is about the project: the project file itself, or (project.json) a mechanism file.</summary>
    public bool Matters(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return false; } // not a usable path
        if (string.Equals(full, FilePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (!IncludesMechanisms) return false;
        string dir = Path.Combine(Folder, Project.MechanismsFolder) + Path.DirectorySeparatorChar;
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && full.EndsWith(".bs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>(Re)starts the debounce: the check runs <see cref="DebounceMs"/> after the last call.</summary>
    public void Poke()
    {
        lock (_gate) { if (!_disposed) _timer.Change(DebounceMs, Timeout.Infinite); }
    }

    void Fire()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_writing > 0) { _timer.Change(DebounceMs, Timeout.Infinite); return; } // our own save is in progress
        }
        string? state = ReadState();
        if (state == null)
        {
            // Locked or half written by the other side: look again a little later (a few times).
            if (Interlocked.Increment(ref _retries) <= 20) Poke();
            return;
        }
        _retries = 0;
        string h = Hash(state);
        lock (_gate)
        {
            if (h == _known) return; // our echo, or the same content written again
            _known = h;
        }
        Changed?.Invoke();
    }

    /// <summary>The state on disk differs from the last one known (an outside change not handled yet).</summary>
    public bool ChangedOnDisk()
    {
        string? state = ReadState();
        lock (_gate) return state != null && Hash(state) != _known;
    }

    /// <summary>This is what the project on disk is now (just loaded or saved by this side): events that find it are echoes.</summary>
    public void Remember(string? state)
    {
        if (state == null) return;
        lock (_gate) _known = Hash(state);
    }

    /// <summary>Remembers whatever is on disk now (after saving several files, e.g. project.json and mechanisms).</summary>
    public void RememberDisk() => Remember(ReadState());

    /// <summary>Forgets the known state, so the next check reports the disk as changed (a reload that failed and must be retried).</summary>
    public void Forget()
    {
        lock (_gate) _known = null;
        Poke();
    }

    /// <summary>Writes the project file atomically and remembers it (the watcher then ignores its own echo).</summary>
    public void Write(string text)
    {
        BeginWrite();
        try
        {
            EasyLink.WriteAtomic(FilePath, text);
            Remember(IncludesMechanisms ? ReadState() : text);
        }
        finally { EndWrite(remember: false); }
    }

    /// <summary>Around a save made of several files: checks wait until <see cref="EndWrite"/>, which remembers the result.</summary>
    public void BeginWrite() { lock (_gate) _writing++; }

    public void EndWrite(bool remember = true)
    {
        if (remember) RememberDisk();
        lock (_gate) _writing = Math.Max(0, _writing - 1);
    }

    /// <summary>The project's state on disk: the file's text (+ the mechanism files for a project.json); null when it can't be read now.</summary>
    public string? ReadState()
    {
        try
        {
            if (!File.Exists(FilePath)) return "";
            string text = ReadShared(FilePath);
            if (!IncludesMechanisms) return text;
            var sb = new StringBuilder(text);
            string mech = Path.Combine(Folder, Project.MechanismsFolder);
            if (Directory.Exists(mech))
                foreach (var f in Directory.GetFiles(mech, "*.bs").Order(StringComparer.OrdinalIgnoreCase))
                    sb.Append('\0').Append(Path.GetFileName(f)).Append('\0').Append(ReadShared(f));
            return sb.ToString();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Reads a file another program may be writing (no lock taken).</summary>
    public static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    public static string Hash(string? text) =>
        text == null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
