namespace BuilderEngine.Plugin;

/// <summary>
/// A BuilderEngine plugin: a public class with a parameterless constructor in a .NET 8 class library. Put the built
/// <c>.dll</c> in a project's <c>plugins/</c> folder; BuilderEngine finds every class that implements this interface.
/// </summary>
/// <example>
/// <code>
/// public sealed class Hello : IBuilderPlugin
/// {
///     public void Register(IPluginHost host)
///     {
///         host.AddFunction("wave", args => Math.Sin((double)args[0]! * Math.PI * 2));
///         host.AddCommand("shout", c => c.Print(c.Text(c.Arg(0)).ToUpperInvariant()), "prints a text in capitals", "text");
///     }
/// }
/// </code>
/// </example>
public interface IBuilderPlugin
{
    /// <summary>The name shown in the Plugins window (default: the class name).</summary>
    string Name => GetType().Name;

    /// <summary>One line about what the plugin adds (shown in the Plugins window).</summary>
    string Description => "";

    /// <summary>
    /// Adds the plugin's commands and functions. Called once when the plugin loads (to list them) and then once for
    /// every world: each game that starts, each scene it goes to, each editor tool run. Keep it quick and side-effect free.
    /// </summary>
    void Register(IPluginHost host);
}

/// <summary>What a plugin can add to Builder Script.</summary>
public interface IPluginHost
{
    /// <summary>True when the world runs an editor tool (it edits the scene), false in a game.</summary>
    bool IsEditor { get; }

    /// <summary>The open project's folder (null in tests that have none).</summary>
    string? ProjectFolder { get; }

    /// <summary>
    /// A command scripts use on its own line: <c>confetti 20</c>, <c>shake_all with 3</c>. A name may have several words
    /// (<c>"sky color"</c>). Throw <see cref="IPluginCommand.Error"/> for a friendly script error.
    /// </summary>
    /// <param name="name">What scripts write.</param>
    /// <param name="run">Runs the command.</param>
    /// <param name="help">One line shown in the Plugins window and the blocks palette.</param>
    /// <param name="inputs">The names of its values, for the blocks palette (<c>"amount"</c>).</param>
    void AddCommand(string name, Action<IPluginCommand> run, string help = "", params string[] inputs);

    /// <summary>
    /// A function scripts use in values: <c>wave(time)</c>. It gets the values (double, string, bool, a list,
    /// an <see cref="IPluginObject"/> or null) and gives one back.
    /// </summary>
    void AddFunction(string name, Func<List<object?>, object?> run, string help = "", params string[] inputs);

    /// <summary>Writes a line to the editor's console.</summary>
    void Log(string text);

    /// <summary>The engine's own world object (a <c>BuilderEngine.Script.World</c>), for plugins that reference BuilderEngine.Script.</summary>
    object Native { get; }
}

/// <summary>A running command: its values, the object running it and the world around it.</summary>
public interface IPluginCommand
{
    /// <summary>The values written after the command name: <c>confetti 20, "red"</c> → [20, "red"].</summary>
    IReadOnlyList<object?> Args { get; }

    /// <summary>The value at <paramref name="i"/>, or null when there is none.</summary>
    object? Arg(int i);

    /// <summary>A value written after a connector word: <c>confetti 20 at 100, 50</c> → NamedArg("at", 1) = 50.</summary>
    object? NamedArg(string connector, int i = 0);

    /// <summary>True when the line has that connector word (<c>to in at by with from ease over as then</c>).</summary>
    bool Has(string connector);

    /// <summary>A value as a number (a friendly error when it isn't one).</summary>
    double Number(object? value);

    /// <summary>A value as text.</summary>
    string Text(object? value);

    /// <summary>The script line (for messages).</summary>
    int Line { get; }

    /// <summary>The object whose mechanism runs the command.</summary>
    IPluginObject Self { get; }

    /// <summary>The other object in a <c>when touched by</c>, or null.</summary>
    IPluginObject? Other { get; }

    /// <summary>The world's objects, or only those with this tag or name.</summary>
    IEnumerable<IPluginObject> Objects(string? tagOrName = null);

    /// <summary>The first object with this name or tag, or null.</summary>
    IPluginObject? Find(string nameOrTag);

    /// <summary>A new object (with these mechanisms, if any) at x, y.</summary>
    IPluginObject Spawn(string name, double x, double y, params string[] mechanisms);

    /// <summary>The global variables (<c>score</c>...).</summary>
    IDictionary<string, object?> Globals { get; }

    /// <summary>Game time in seconds.</summary>
    double Time { get; }

    /// <summary>True in an editor tool, false in a game.</summary>
    bool IsEditor { get; }

    /// <summary>Prints a line to the console (like <c>print</c>).</summary>
    void Print(string text);

    /// <summary>Makes the script line wait this many seconds (only its own <c>when</c> pauses).</summary>
    void WaitSeconds(double seconds);

    /// <summary>Makes the script line wait until <paramref name="done"/> is true (checked once per frame).</summary>
    void WaitUntil(Func<bool> done);

    /// <summary>A friendly script error for this line: <c>throw c.Error("confetti needs a number")</c>.</summary>
    Exception Error(string message);

    /// <summary>The engine's own command context (a <c>BuilderEngine.Script.CommandContext</c>).</summary>
    object Native { get; }
}

/// <summary>An object in the game (or, in an editor tool, in the scene being edited).</summary>
public interface IPluginObject
{
    string Name { get; }
    double X { get; set; }
    double Y { get; set; }
    double Width { get; set; }
    double Height { get; set; }
    /// <summary>Speed in px/s (+ is right / down).</summary>
    double Vx { get; set; }
    double Vy { get; set; }
    ICollection<string> Tags { get; }
    bool Destroyed { get; }
    /// <summary>Removes the object (at the end of the frame in a game; from the scene in an editor tool).</summary>
    void Destroy();
}
