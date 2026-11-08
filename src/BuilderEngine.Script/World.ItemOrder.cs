using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// The order of the inventory (the hotbar shows its first slots):
/// <code>
/// move_item "pick" to 1        (that stack goes to slot 1; the others move along — nothing happens when there's none)
/// item_slot("pick")            (its slot, 1 = the first; 0 when there's none)
/// </code>
/// New items go to the end, so a tool crafted late would sit past the hotbar without this.
/// </summary>
public sealed partial class World
{
    [ModuleInitializer]
    internal static void InstallItemOrder() => Installers.Add(w =>
    {
        w.Commands["move_item"] = c =>
        {
            if (c.Args.Count == 0 || !c.Has("to")) throw c.Error("'move_item' needs an item and a slot: move_item \"pick\" to 1.");
            c.World.MoveItem(ItemName(c), (int)Math.Round(c.Number(c.NamedArg("to"))));
        };
        w.Functions["item_slot"] = a =>
        {
            if (a.Count != 1) throw new ScriptError("'item_slot' needs an item: item_slot(\"pick\") gives its slot (1 = the first), or 0.", 0);
            return (double)(w.Inventory.FindIndex(s => string.Equals(s.Name, ToText(a[0]), StringComparison.OrdinalIgnoreCase)) + 1);
        };
    });

    /// <summary>Moves an item's stack to a slot (1 = the first; past the end = the last). False when there's none.</summary>
    public bool MoveItem(string name, int slot)
    {
        int from = Inventory.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return false;
        var stack = Inventory[from];
        Inventory.RemoveAt(from);
        Inventory.Insert(Math.Clamp(slot - 1, 0, Inventory.Count), stack);
        return true;
    }
}
