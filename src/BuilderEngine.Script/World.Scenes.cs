namespace BuilderEngine.Script;

// Scenes while playing: the 'goto_scene "Level 2"' command (also written 'scene "Level 2"').
// The world only records the request and raises SceneRequested; the host builds the next world with
// ForNextScene() (same mechanisms, global variables and host commands) and loads the new scene into it.
public sealed partial class World
{
    public static readonly string[] SceneCommands = ["goto_scene", "scene"];

    /// <summary>The scene names 'goto_scene' accepts (set by <see cref="UseScenes"/>).</summary>
    public IReadOnlyList<string> SceneNames { get; private set; } = [];

    /// <summary>The scene asked for by 'goto_scene' during this world's life (the first request wins), or null.</summary>
    public string? RequestedScene { get; private set; }

    /// <summary>Raised once, right when a script asks for another scene. The host switches after the current step.</summary>
    public event Action<string>? SceneRequested;

    /// <summary>Turns on 'goto_scene' / 'scene' for these scene names (an unknown name is a friendly error).</summary>
    public void UseScenes(IEnumerable<string> sceneNames)
    {
        SceneNames = sceneNames.ToList();
        foreach (var c in SceneCommands) Commands[c] = GotoSceneCommand;
    }

    static void GotoSceneCommand(CommandContext c)
    {
        if (c.Args.Count == 0) throw new ScriptError("'goto_scene' needs the scene's name, like goto_scene \"Level 2\".", c.Line);
        string wanted = ToText(c.Args[0]).Trim();
        var w = c.World;
        string? match = w.SceneNames.FirstOrDefault(n => string.Equals(n.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new ScriptError(w.SceneNames.Count == 0 ? $"There are no scenes to go to." : $"There's no scene called '{wanted}'.{Suggest(wanted, w.SceneNames)}", c.Line);
        w.RequestScene(match);
    }

    /// <summary>Asks the host for another scene (what 'goto_scene' does). Later requests in the same world are ignored.</summary>
    public void RequestScene(string name)
    {
        if (RequestedScene != null) return;
        RequestedScene = name;
        Debug?.Event(DebugEventKind.Scene, null, "go to scene " + Printer.Quote(name));
        SceneRequested?.Invoke(name);
    }

    /// <summary>
    /// A fresh world for the next scene: the same mechanisms, global variables (the score carries over), host commands,
    /// scene names, keys held and event listeners — but no objects and a clock at zero.
    /// </summary>
    public World ForNextScene()
    {
        var w = new World();
        foreach (var (k, v) in Library) w.Library[k] = v;
        foreach (var (k, v) in Globals) w.Globals[k] = v;
        foreach (var (k, v) in Commands) w.Commands[k] = v;
        foreach (var k in KeysDown) w.KeysDown.Add(k);
        foreach (var k in _held) w._held.Add(k);
        foreach (var k in _swallowed) w._swallowed.Add(k);
        w.ProjectFolder = ProjectFolder;
        w.SaveFolder = SaveFolder;
        w._store = _store;
        w.SceneNames = SceneNames;
        w.MaxLinesWithoutWait = MaxLinesWithoutWait;
        w.ViewMaxWidth = ViewMaxWidth; w.ViewMaxHeight = ViewMaxHeight; // 'camera size' (World.View.cs)
        w.Easy = Easy; // EasyEngine behaviors playing here (EasyCompat.cs)
        w._packages.AddRange(_packages); // behavior packages (World.Packages.cs)
        foreach (var (k, v) in Prefabs) w.Prefabs[k] = v; // spawn "Slime" (World.Prefabs.cs)
        w.Printed = Printed;
        w.SoundPlayed = SoundPlayed;
        w.SceneRequested = SceneRequested;
        // The game kit: one inventory, item looks, recipes, hotbar, looping sounds and dice for the whole game.
        w.Inventory = Inventory;
        w.ItemLooks = ItemLooks;
        w.Recipes = Recipes;
        w.Hotbar = Hotbar;
        w.Loops = Loops;
        w.MasterVolume = MasterVolume;
        w.SoundControl = SoundControl;
        w.Rng = Rng;
        w._seed = _seed;
        return w;
    }
}
