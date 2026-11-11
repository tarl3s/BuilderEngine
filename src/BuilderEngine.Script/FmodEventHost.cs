namespace BuilderEngine.Script;

/// <summary>What a game asks the host to do with FMOD Studio events (World.FmodControl → <see cref="FmodEventHost.Handle"/>).</summary>
public enum FmodAction { Play, Stop, SetParameter, SetGlobal, Position }

/// <summary>
/// One FMOD Studio request from a game. <see cref="Name"/> is the playing instance's name ("music" from
/// <c>play event "event:/Music/Forest" as "music"</c>; the event path when not named). <see cref="X"/>/<see cref="Y"/>: a
/// game position in pixels for 3D events (NaN = not placed).
/// </summary>
public sealed record FmodRequest(FmodAction Action, string Event = "", string Name = "", string Parameter = "", double Value = 0,
    bool Fade = true, double Volume = 1, double X = double.NaN, double Y = double.NaN);

/// <summary>
/// Plays a game's FMOD Studio events for a host (BuilderEngine's editor and player, EasyEngine's play window): owns one
/// <see cref="FmodStudio"/> with the project's banks and the named instances. Without FMOD Studio (or banks) every request is
/// ignored after one "FMOD Studio isn't installed" / "no banks" line in <see cref="FmodLibrary.Log"/>. Never throws.
/// Call <see cref="Update"/> once per frame and <see cref="Dispose"/> when the game stops.
/// </summary>
public sealed class FmodEventHost : IDisposable
{
    readonly string _projectDir;
    readonly bool _silent;
    FmodStudio? _studio;
    bool _tried;
    readonly Dictionary<string, FmodEvent> _named = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _scratch = new();

    /// <summary>Why events don't play ("" when they do), or "n bank(s), m event(s)".</summary>
    public string Status { get; private set; } = "";
    public FmodStudio? Studio => Open();

    public FmodEventHost(string projectDir, bool silent = false) { _projectDir = projectDir; _silent = silent; }

    // FMOD Studio starts on the first request (a game without events never loads it)
    FmodStudio? Open()
    {
        if (_tried) return _studio;
        _tried = true;
        _studio = FmodStudio.TryCreate(out var why, _silent);
        if (_studio == null) { Status = why; FmodLibrary.LogOnce("FMOD: " + why); return null; }
        int n = _studio.LoadProject(_projectDir, out var msg);
        Status = n > 0 ? (msg.Length > 0 ? msg : $"{n} bank(s), {_studio.Events.Count} event(s)") : msg;
        if (n == 0 || msg.Length > 0) FmodLibrary.LogOnce("FMOD Studio: " + Status);
        return _studio;
    }

    /// <summary>Loads the banks again (after a new build in FMOD Studio). Playing events stop. Returns the number of banks.</summary>
    public int Reload()
    {
        StopAll(fade: false);
        if (Open() is not { } s) return 0;
        int n = s.LoadProject(_projectDir, out var msg);
        Status = n > 0 ? (msg.Length > 0 ? msg : $"{n} bank(s), {s.Events.Count} event(s)") : msg;
        return n;
    }

    public void Handle(FmodRequest r)
    {
        try
        {
            if (Open() is not { } s) return;
            string name = r.Name.Length > 0 ? r.Name : FmodStudio.NormalizePath(r.Event);
            switch (r.Action)
            {
                case FmodAction.Play:
                    if (_named.Remove(name, out var old)) { s.Stop(old, fade: true); s.Release(old); } // the same name again restarts it
                    var ev = s.Play(r.Event, r.Volume);
                    if (ev.IsNone) return;
                    if (!double.IsNaN(r.X)) s.SetPosition(ev, r.X, r.Y);
                    _named[name] = ev;
                    break;
                case FmodAction.Stop:
                    if (name.Length == 0) { StopAll(r.Fade); return; }
                    if (_named.Remove(name, out var st)) { s.Stop(st, r.Fade); s.Release(st); }
                    break;
                case FmodAction.SetParameter:
                    if (_named.TryGetValue(name, out var pe)) s.SetParameter(pe, r.Parameter, r.Value);
                    else s.SetGlobalParameter(r.Parameter, r.Value); // no such instance playing: a global parameter of that name, if any
                    break;
                case FmodAction.SetGlobal:
                    s.SetGlobalParameter(r.Parameter, r.Value);
                    break;
                case FmodAction.Position:
                    if (_named.TryGetValue(name, out var po)) s.SetPosition(po, r.X, r.Y);
                    else if (name.Length == 0) s.SetListener(r.X, r.Y);
                    break;
            }
        }
        catch (Exception e) { FmodLibrary.LogOnce("FMOD Studio: " + e.Message); }
    }

    /// <summary>The playing instance with this name (or event path), to set its volume or position directly.</summary>
    public bool TryGet(string name, out FmodEvent ev) => _named.TryGetValue(name, out ev);

    /// <summary>Whether the instance with this name (or event path) is still playing.</summary>
    public bool IsPlaying(string name) => _studio != null && _named.TryGetValue(name, out var ev) && _studio.IsPlaying(ev);

    /// <summary>Sets where the game listens from (the camera centre or the player), in pixels.</summary>
    public void SetListener(double x, double y) { if (_studio != null) _studio.SetListener(x, y); }

    /// <summary>Once per frame: FMOD Studio's update; instances that ended are forgotten (no allocation when none did).</summary>
    public void Update()
    {
        if (_studio == null) return;
        _studio.Update();
        if (_named.Count == 0) return;
        _scratch.Clear();
        foreach (var (n, ev) in _named) if (!_studio.IsPlaying(ev)) _scratch.Add(n);
        foreach (var n in _scratch) if (_named.Remove(n, out var ev)) _studio.Release(ev);
    }

    public void StopAll(bool fade = false)
    {
        if (_studio == null) return;
        foreach (var ev in _named.Values) { _studio.Stop(ev, fade); _studio.Release(ev); }
        _named.Clear();
    }

    public void Dispose()
    {
        StopAll();
        _studio?.Dispose();
        _studio = null;
    }
}
