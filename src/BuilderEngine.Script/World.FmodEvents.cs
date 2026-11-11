using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// FMOD Studio events (optional FMOD audio — the host plays them through <see cref="FmodEventHost"/>, World.Fmod.cs):
/// <code>
/// play event "event:/Music/Forest"                 play event "Music/Forest" as "music" with 0.5   (a name, a volume 0..1)
/// play event "SFX/Fire" at self                    (a 3D event that follows this object and stops when it is destroyed)
/// stop event "music"           stop event "music" now   (now = no fade-out)      stop events      stop events now
/// set event "music" "danger" to 0.8                (a parameter of one playing event)
/// set fmod "time_of_day" to 1                      (a global parameter)
/// place event "music" at 400, 300                  place event "music" at Boss   (3D position; an object = follow it)
/// </code>
/// An event is named by <c>as</c>, else by its path. Without FMOD Studio (or banks) the host ignores the requests after one
/// "FMOD Studio isn't installed" line in the console. The listener is the camera's centre.
/// </summary>
public sealed partial class World
{
    public static readonly string[] FmodCommands = ["play event", "stop event", "stop events", "set event", "set fmod", "place event"];

    /// <summary>An event instance this game started: its event path and the object it follows (null = none).</summary>
    sealed class FmodTrack(string ev, GameObject? owner)
    {
        public string Event = ev;
        public GameObject? Owner = owner;
        public double X = double.NaN, Y = double.NaN;
    }

    readonly Dictionary<string, FmodTrack> _fmodTracks = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _fmodScratch = [];
    bool _fmodUsed;
    double _listenerX = double.NaN, _listenerY = double.NaN;

    /// <summary>The event instances this game started and hasn't stopped (name → event path). Ended one-shots stay listed.</summary>
    public IEnumerable<(string Name, string Event)> FmodPlaying => _fmodTracks.Select(kv => (kv.Key, kv.Value.Event));

    [ModuleInitializer]
    internal static void InstallFmod() => Installers.Add(w =>
    {
        w.Commands["play event"] = PlayEventCommand;
        w.Commands["stop event"] = c =>
        {
            if (c.Args.Count == 0 || IndexOfWord(c, "now") == 0)
                throw c.Error("'stop event' needs the event's name: stop event \"music\" (or stop events for all of them).");
            c.World.StopFmod(ToText(c.Args[0]), fade: IndexOfWord(c, "now") < 0);
        };
        w.Commands["stop events"] = c => c.World.StopAllFmod(fade: IndexOfWord(c, "now") < 0);
        w.Commands["set event"] = c =>
        {
            if (c.Args.Count < 2 || !c.Named.ContainsKey("to"))
                throw c.Error("Write it like set event \"music\" \"danger\" to 0.8 (the event's name, its parameter, the value).");
            string who = ToText(c.Args[0]), param = ToText(c.Args[1]);
            double v = c.Number(c.NamedArg("to"));
            var names = c.World.FmodNames(who);
            if (names.Count == 0) c.World.RaiseFmodUsed(new FmodRequest(FmodAction.SetParameter, Name: who, Parameter: param, Value: v));
            foreach (var n in names) c.World.RaiseFmodUsed(new FmodRequest(FmodAction.SetParameter, c.World._fmodTracks[n].Event, n, param, v));
        };
        w.Commands["set fmod"] = c =>
        {
            if (c.Args.Count < 1 || !c.Named.ContainsKey("to"))
                throw c.Error("Write it like set fmod \"time_of_day\" to 1 (a global parameter of your FMOD Studio project).");
            c.World.RaiseFmodUsed(new FmodRequest(FmodAction.SetGlobal, Parameter: ToText(c.Args[0]), Value: c.Number(c.NamedArg("to"))));
        };
        w.Commands["place event"] = c =>
        {
            if (c.Args.Count == 0 || !c.Named.TryGetValue("at", out var at) || at.Count == 0)
                throw c.Error("Write it like place event \"music\" at 400, 300 (or at an object, which it then follows).");
            var (x, y, owner) = WhereAt(c, at);
            foreach (var n in c.World.FmodNames(ToText(c.Args[0])))
            {
                var t = c.World._fmodTracks[n];
                t.Owner = owner;
                t.X = x; t.Y = y;
                c.World.RaiseFmodUsed(new FmodRequest(FmodAction.Position, t.Event, n, X: x, Y: y));
            }
        };
    });

    static void PlayEventCommand(CommandContext c)
    {
        var w = c.World;
        if (c.Args.Count == 0) throw c.Error("'play event' needs the event: play event \"event:/Music/Forest\" (pick it from your FMOD Studio project).");
        string ev = FmodStudio.NormalizePath(ToText(c.Args[0]));
        if (ev.Length == 0) throw c.Error("'play event' needs the event: play event \"event:/Music/Forest\".");
        double volume = NamedNumber(c, "with") is double v ? Math.Clamp(v, 0, 1) : 1;
        double x = double.NaN, y = double.NaN;
        GameObject? owner = null;
        if (c.Named.TryGetValue("at", out var at) && at.Count > 0) (x, y, owner) = WhereAt(c, at);
        // Named by 'as'; else by its path — one per object when it follows one (two emitters of one event don't restart each other).
        string name = c.Named.TryGetValue("as", out var a) && a.Count > 0 && ToText(a[0]).Trim() is { Length: > 0 } given ? given
            : owner != null ? ev + "@" + owner.Id : ev;
        w.AddOutput("♪ " + ev);
        w._fmodTracks[name] = new FmodTrack(ev, owner) { X = x, Y = y };
        w.RaiseFmodUsed(new FmodRequest(FmodAction.Play, ev, name, Volume: volume, X: x, Y: y));
    }

    /// <summary>'at 400, 300' or 'at Boss' (the object's centre; it is then followed).</summary>
    static (double X, double Y, GameObject? Owner) WhereAt(CommandContext c, List<object?> at)
    {
        if (at[0] is GameObject g) return (g.X + g.Width / 2, g.Y + g.Height / 2, g);
        if (at[0] is string s && c.World.FindObject(s) is { } named) return (named.X + named.Width / 2, named.Y + named.Height / 2, named);
        if (at.Count < 2) throw c.Error("After 'at' write a position (at 400, 300) or an object (at self).");
        return (c.Number(at[0]), c.Number(at[1]), null);
    }

    /// <summary>The instances a name means: that name, or every instance of that event when it is an event path.</summary>
    List<string> FmodNames(string who)
    {
        if (_fmodTracks.ContainsKey(who)) return [who];
        string path = FmodStudio.NormalizePath(who);
        return _fmodTracks.Where(kv => string.Equals(kv.Value.Event, path, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
    }

    void RaiseFmodUsed(FmodRequest r)
    {
        if (!_fmodUsed) { _fmodUsed = true; _listenerX = _listenerY = double.NaN; }
        RaiseFmod(r);
    }

    void StopFmod(string who, bool fade)
    {
        var names = FmodNames(who);
        if (names.Count == 0) { RaiseFmodUsed(new FmodRequest(FmodAction.Stop, Name: who, Fade: fade)); return; } // the host may still know it
        foreach (var n in names)
        {
            var t = _fmodTracks[n];
            _fmodTracks.Remove(n);
            RaiseFmodUsed(new FmodRequest(FmodAction.Stop, t.Event, n, Fade: fade));
        }
    }

    void StopAllFmod(bool fade)
    {
        _fmodTracks.Clear();
        RaiseFmodUsed(new FmodRequest(FmodAction.Stop, Fade: fade)); // no name = every event
    }

    /// <summary>
    /// Once per frame (from <see cref="Frame"/>): events placed at an object follow it and stop when it is destroyed; the
    /// listener is the camera's centre. Nothing at all until the game used an FMOD command; no allocation when nothing moved.
    /// </summary>
    void UpdateFmodEvents()
    {
        if (!_fmodUsed) return;
        if (Camera.X != _listenerX || Camera.Y != _listenerY)
        {
            _listenerX = Camera.X; _listenerY = Camera.Y;
            RaiseFmod(new FmodRequest(FmodAction.Position, X: _listenerX, Y: _listenerY)); // no name = the listener
        }
        _fmodScratch.Clear();
        foreach (var (n, t) in _fmodTracks)
        {
            if (t.Owner is not { } o) continue;
            if (o.Destroyed) { _fmodScratch.Add(n); continue; }
            double x = o.X + o.Width / 2, y = o.Y + o.Height / 2;
            if (x == t.X && y == t.Y) continue;
            t.X = x; t.Y = y;
            RaiseFmod(new FmodRequest(FmodAction.Position, t.Event, n, X: x, Y: y));
        }
        foreach (var n in _fmodScratch)
        {
            var t = _fmodTracks[n];
            _fmodTracks.Remove(n);
            RaiseFmod(new FmodRequest(FmodAction.Stop, t.Event, n));
        }
    }

    /// <summary>
    /// The next scene (<see cref="ForNextScene"/>): the same host; events that follow an object of this scene stop (their
    /// objects leave), the others (music) play on and keep their names.
    /// </summary>
    void PassFmodTo(World next)
    {
        next.FmodControl = FmodControl;
        next._fmodUsed = _fmodUsed;
        foreach (var (n, t) in _fmodTracks)
        {
            if (t.Owner != null) RaiseFmod(new FmodRequest(FmodAction.Stop, t.Event, n));
            else next._fmodTracks[n] = new FmodTrack(t.Event, null) { X = t.X, Y = t.Y };
        }
        _fmodTracks.Clear();
    }
}
