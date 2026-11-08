using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// Global variables made by a script — values every mechanism shares (they go on to the next scene and into
/// <c>save game</c>), like the score:
/// <code>
/// global power, night       (makes them global: a new one starts at 0, one that exists keeps its value)
/// power -= 1                (any mechanism of any object now changes the same 'power'; "{power}" in captions too)
/// </code>
/// A param or a variable of a mechanism with the same name still wins inside that mechanism (the one that says
/// 'global' gives its own up: its value becomes the global's).
/// </summary>
public sealed partial class World
{
    [ModuleInitializer]
    internal static void InstallGlobals() => Installers.Add(w => w.Commands["global"] = GlobalCommand);

    static void GlobalCommand(CommandContext c)
    {
        if (c.Raw.Count == 0) throw c.Error("'global' needs the names to share: global power, night.");
        foreach (var e in c.Raw)
        {
            if (e is not NameExpr { Path.Length: 1 } n) throw c.Error("'global' takes names, like: global power, night.");
            string name = n.Path[0];
            if (c.Mechanism.Vars.Remove(name, out var own) && own != null) c.World.Globals[name] = own;
            else if (!c.World.Globals.ContainsKey(name)) c.World.Globals[name] = 0.0;
        }
    }
}
