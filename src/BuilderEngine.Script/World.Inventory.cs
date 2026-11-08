using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>So many of one item in the inventory.</summary>
public sealed class ItemStack(string name, int count)
{
    public string Name { get; } = name;
    public int Count { get; set; } = count;
}

/// <summary>How an item is drawn in the hotbar: a colour and maybe a picture.</summary>
public sealed record ItemLook(string Color, string Image);

/// <summary>A crafting recipe: what it makes (and how many) from what it needs.</summary>
public sealed record Recipe(string Makes, int Amount, List<(string Item, int Count)> Needs)
{
    public string Text => (Amount > 1 ? $"{Makes} ×{Amount}" : Makes) + ": " + string.Join(", ", Needs.Select(n => $"{n.Item} {n.Count}"));
}

/// <summary>The hotbar at the bottom of the screen: the first slots of the inventory, one of them selected.</summary>
public sealed class HotbarState
{
    public bool Visible;
    public int Slots = 9;
    public int Selected;
    /// <summary>Where it is (view pixels); NaN = centred at the bottom.</summary>
    public double X = double.NaN, Y = double.NaN;
    public double SlotSize = 40;
}

/// <summary>
/// Inventory, items, hotbar and crafting (one inventory for the whole game: it goes on to the next scene and into 'save game'):
/// <code>
/// give "wood", 3          take "wood", 2 ('taken' = true when there were enough)       clear inventory
/// has("wood", 2)          count_item("wood")       items()  (the names, in order)
/// item "wood" with "#8B5A2B"      item "sword" with "sword.png"      (how it looks in the hotbar)
/// show hotbar             show hotbar 5 at 200, 400       hide hotbar      (keys 1-9 and the wheel pick a slot)
/// selected_item()         selected_slot()                 select_slot 3
/// recipe "torch" from "wood", 1, "coal", 1 as 4           craft "torch" ('crafted' = true when it could)
/// can_craft("torch")      recipes()      recipe_text("torch")
/// </code>
/// </summary>
public sealed partial class World
{
    /// <summary>The items, in the order they were first given (empty stacks leave).</summary>
    public List<ItemStack> Inventory { get; private set; } = [];
    public Dictionary<string, ItemLook> ItemLooks { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Recipe> Recipes { get; private set; } = [];
    public HotbarState Hotbar { get; private set; } = new();

    [ModuleInitializer]
    internal static void InstallInventory() => Installers.Add(w =>
    {
        w.Commands["give"] = c =>
        {
            if (c.Args.Count == 0) throw c.Error("'give' needs an item: give \"wood\", 3.");
            int n = c.Args.Count > 1 ? (int)Math.Round(c.Number(c.Args[1])) : 1;
            c.World.Give(ItemName(c), n);
        };
        w.Commands["take"] = c =>
        {
            if (c.Args.Count == 0) throw c.Error("'take' needs an item: take \"wood\", 2 (then 'taken' tells if there were enough).");
            int n = c.Args.Count > 1 ? (int)Math.Round(c.Number(c.Args[1])) : 1;
            c.Locals["taken"] = c.World.Take(ItemName(c), n);
        };
        w.Commands["clear inventory"] = c => c.World.Inventory.Clear();
        w.Commands["item"] = c =>
        {
            if (c.Args.Count == 0 || !c.Has("with")) throw c.Error("'item' sets how an item looks: item \"wood\" with \"#8B5A2B\" or item \"sword\" with \"sword.png\".");
            string name = ItemName(c);
            var old = c.World.ItemLooks.GetValueOrDefault(name) ?? new ItemLook("#A3A3A3", "");
            foreach (var v in c.Named["with"])
                if (v is string s && s.Length > 0) old = s.StartsWith('#') ? old with { Color = s } : old with { Image = s };
            c.World.ItemLooks[name] = old;
        };
        w.Commands["show hotbar"] = c =>
        {
            var h = c.World.Hotbar;
            h.Visible = true;
            if (c.Args.Count > 0) h.Slots = Math.Clamp((int)Math.Round(c.Number(c.Args[0])), 1, 10);
            if (c.Named.TryGetValue("at", out var at) && at.Count >= 2) { h.X = c.Number(at[0]); h.Y = c.Number(at[1]); }
            if (NamedNumber(c, "with") is double size) h.SlotSize = Math.Clamp(size, 16, 96);
            h.Selected = Math.Clamp(h.Selected, 0, h.Slots - 1);
        };
        w.Commands["hide hotbar"] = c => c.World.Hotbar.Visible = false;
        w.Commands["select_slot"] = c =>
        {
            if (c.Args.Count == 0) throw c.Error("'select_slot' needs a slot number, 1 to 10.");
            var h = c.World.Hotbar;
            h.Selected = Math.Clamp((int)Math.Round(c.Number(c.Args[0])) - 1, 0, Math.Max(0, h.Slots - 1));
        };
        w.Commands["recipe"] = RecipeCommand;
        w.Commands["craft"] = c =>
        {
            if (c.Args.Count == 0) throw c.Error("'craft' needs what to make: craft \"torch\".");
            c.Locals["crafted"] = c.World.Craft(ItemName(c));
        };

        w.Functions["has"] = a =>
        {
            if (a.Count is < 1 or > 2) throw new ScriptError("'has' needs an item and maybe how many: has(\"wood\", 2).", 0);
            return w.CountItem(ToText(a[0])) >= (a.Count > 1 ? ToNumber(a[1], 0) : 1);
        };
        w.Functions["count_item"] = a =>
        {
            if (a.Count != 1) throw new ScriptError("'count_item' needs an item: count_item(\"wood\").", 0);
            return (double)w.CountItem(ToText(a[0]));
        };
        w.Functions["items"] = _ => w.Inventory.Select(s => (object?)s.Name).ToList();
        w.Functions["selected_item"] = _ => w.SelectedItem;
        w.Functions["selected_slot"] = _ => (double)(w.Hotbar.Selected + 1);
        w.Functions["can_craft"] = a =>
        {
            if (a.Count != 1) throw new ScriptError("'can_craft' needs what to make: can_craft(\"torch\").", 0);
            return w.CanCraft(ToText(a[0]));
        };
        w.Functions["recipes"] = _ => w.Recipes.Select(r => (object?)r.Makes).ToList();
        w.Functions["recipe_text"] = a =>
        {
            if (a.Count != 1) throw new ScriptError("'recipe_text' needs what it makes: recipe_text(\"torch\").", 0);
            return w.FindRecipe(ToText(a[0]))?.Text ?? "";
        };
    });

    static string ItemName(CommandContext c)
    {
        string s = ToText(c.Args[0]).Trim();
        return s.Length > 0 ? s : throw c.Error("An item needs a name, like \"wood\".");
    }

    public int CountItem(string name)
    {
        foreach (var s in Inventory) if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s.Count;
        return 0;
    }

    public void Give(string name, int count = 1)
    {
        if (count <= 0 || string.IsNullOrWhiteSpace(name)) return;
        foreach (var s in Inventory)
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) { s.Count += count; return; }
        Inventory.Add(new ItemStack(name.Trim(), count));
    }

    /// <summary>Takes items if there are enough (else nothing changes). True when it did.</summary>
    public bool Take(string name, int count = 1)
    {
        if (count <= 0) return true;
        for (int i = 0; i < Inventory.Count; i++)
        {
            var s = Inventory[i];
            if (!string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Count < count) return false;
            s.Count -= count;
            if (s.Count == 0) Inventory.RemoveAt(i);
            return true;
        }
        return false;
    }

    /// <summary>The item in the hotbar's selected slot ("" when it's empty).</summary>
    public string SelectedItem => Hotbar.Selected >= 0 && Hotbar.Selected < Inventory.Count ? Inventory[Hotbar.Selected].Name : "";

    /// <summary>Keys 1-9 (and 0 for the tenth) pick a hotbar slot while it's on screen. True when it used the key.</summary>
    bool HotbarKey(string key)
    {
        if (!Hotbar.Visible) return false;
        string k = key.StartsWith("numpad") ? key[6..] : key.Length == 2 && key[0] == 'd' ? key[1..] : key;
        if (k.Length != 1 || k[0] is < '0' or > '9') return false;
        int slot = k[0] == '0' ? 9 : k[0] - '1';
        if (slot >= Hotbar.Slots) return false;
        Hotbar.Selected = slot;
        return true;
    }

    // ---------------- Crafting ----------------

    static void RecipeCommand(CommandContext c)
    {
        if (c.Args.Count == 0 || !c.Has("from")) throw c.Error("'recipe' needs what it makes and what it takes: recipe \"torch\" from \"wood\", 1, \"coal\", 1 as 4.");
        var needs = new List<(string, int)>();
        var from = c.Named["from"];
        for (int i = 0; i < from.Count; i++)
        {
            if (from[i] is not string item || item.Trim().Length == 0) throw c.Error("After 'from' write each item and how many: from \"wood\", 2, \"stone\", 1.");
            int n = i + 1 < from.Count && from[i + 1] is double d ? (int)Math.Round(d) : 1;
            if (i + 1 < from.Count && from[i + 1] is double) i++;
            needs.Add((item.Trim(), Math.Max(1, n)));
        }
        int amount = Math.Max(1, (int)Math.Round(NamedNumber(c, "as") ?? 1));
        var r = new Recipe(ItemName(c), amount, needs);
        var list = c.World.Recipes;
        int at = list.FindIndex(x => string.Equals(x.Makes, r.Makes, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) list[at] = r; else list.Add(r);
    }

    public Recipe? FindRecipe(string makes) => Recipes.FirstOrDefault(r => string.Equals(r.Makes, makes, StringComparison.OrdinalIgnoreCase));

    public bool CanCraft(string makes) => FindRecipe(makes) is { } r && r.Needs.All(n => CountItem(n.Item) >= n.Count);

    /// <summary>Takes what a recipe needs and gives what it makes. False (and nothing changes) when something is missing.</summary>
    public bool Craft(string makes)
    {
        if (!CanCraft(makes)) return false;
        var r = FindRecipe(makes)!;
        foreach (var (item, count) in r.Needs) Take(item, count);
        Give(r.Makes, r.Amount);
        return true;
    }
}
