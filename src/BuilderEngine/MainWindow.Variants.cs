using System.Text.RegularExpressions;
using BuilderEngine.Script;

namespace BuilderEngine;

// VARIANTS: 'mechanism RedSlime from Slime' (rules in BuilderEngine.Script/Variants.cs). The library and the inspector
// show "RedSlime (from Slime)"; the library's right-click menu makes one ("Make a variant…"); renaming the parent
// renames the 'from' of its variants. The inspector shows a variant's params merged with its parent's (DefOf).
public sealed partial class MainWindow
{
    /// <summary>"RedSlime (from Slime)" for a variant, otherwise the name.</summary>
    internal string MechLabel(string name) => RawDefOf(name, out _) is { Base: not null } d ? Variants.Label(d) : name;

    /// <summary>A mechanism as written (parsed from its source), for resolving variants.</summary>
    MechanismDef? RawDef(string name) => RawDefOf(name, out _);

    /// <summary>Why a variant can't work (its parent is missing, a loop), or null.</summary>
    string? VariantProblem(MechanismDef def)
    {
        if (def.Base == null) return null;
        try
        {
            Variants.Resolve(def, n => string.Equals(n, def.Name, StringComparison.OrdinalIgnoreCase) ? def : RawDef(n));
            return null;
        }
        catch (ScriptError e) { return e.ToString(); }
    }

    /// <summary>Asks for a name and makes a variant of a mechanism (it opens, ready to change).</summary>
    async Task MakeVariantAsync(string parent)
    {
        if (!_sources.ContainsKey(parent)) return;
        string suggestion = MechanismLibrary.UniqueName("My" + parent, _sources.ContainsKey);
        string? name = _snapMode ? suggestion
            : await Dialogs.Input(this, "Make a variant", $"A variant is “{parent}” with changes: it does everything {parent} does, and you can change its defaults, add whens or replace its commands. Changing {parent} later changes the variant too.\n\nName of the variant:", suggestion, "Make it");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (BlockEdits.CheckName(name) is { } bad) { SetStatus(false, bad); return; }
        if (_sources.ContainsKey(name)) { SetStatus(false, $"There's already a mechanism called “{name}”."); return; }
        _sources[name] = VariantSource(name, parent, RawDef(parent));
        RefreshLibrary();
        SceneOnMechanismsChanged();
        OpenMechanism(name);
        SetStatus(true, $"Made {name}, a variant of {parent}. Change a default, add a 'when', or write a 'to' with the same name to replace one of {parent}'s commands.");
    }

    /// <summary>The first lines of a new variant: the 'from' line and its parent's first param, ready to change.</summary>
    internal static string VariantSource(string name, string parent, MechanismDef? parentDef)
    {
        var lines = new List<string> { $"mechanism {name} from {parent}" };
        if (parentDef?.Params.FirstOrDefault(p => p.Default != null) is { } p)
            lines.Add($"    param {p.Name} = {Printer.Print(p.Default!)}");
        else
            lines.Add("    when start:");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>A mechanism was renamed: variants that come from it follow, and so do its breakpoints.</summary>
    void VariantsOnRenamed(string oldName, string newName)
    {
        _debugger.RenameMechanism(oldName, newName);
        string esc = Regex.Escape(oldName);
        foreach (var key in _sources.Keys.ToList())
        {
            string src = _sources[key];
            string renamed = Regex.Replace(src, $@"(?m)^(\s*mechanism\s+\w+\s+from\s+){esc}\b", m => m.Groups[1].Value + newName);
            if (renamed != src) _sources[key] = renamed;
        }
    }
}
