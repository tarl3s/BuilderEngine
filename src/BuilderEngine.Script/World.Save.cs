using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

/// <summary>
/// Saving progress:
/// <code>
/// save game            load game            (every global variable — the score… — and the saved values)
/// save game "slot 2"   load game "slot 2"   ('loaded' is true after a load that found a save)
/// save "coins", 12     saved("coins")       (one value, kept on disk right away; nothing saved yet reads as nothing)
/// clear save
/// </code>
/// Saves are JSON files in <see cref="SaveFolder"/> (the project's saves/ folder while playing in the editor);
/// without a folder they live in memory until the program closes.
/// </summary>
public sealed partial class World
{
    public const string DefaultSlot = "save";
    /// <summary>Where save files go. Null = in memory only.</summary>
    public string? SaveFolder { get; set; }
    static readonly Dictionary<string, string> MemorySaves = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, object?>? _store;

    [ModuleInitializer]
    internal static void InstallSave() => Installers.Add(w =>
    {
        w.Commands["save game"] = c => c.World.SaveGame(SlotOf(c));
        w.Commands["load game"] = c => c.Locals["loaded"] = c.World.LoadGame(SlotOf(c));
        w.Commands["clear save"] = c => c.World.ClearSave(SlotOf(c));
        w.Commands["save"] = c =>
        {
            if (c.Args.Count < 2 || c.Args[0] is not string key || key.Length == 0)
                throw c.Error("'save' needs a name in quotes and a value: save \"coins\", score — or 'save game'.");
            c.World.SaveValue(key, c.Args[1]);
        };
        w.Functions["saved"] = a =>
        {
            if (a.Count != 1) throw new ScriptError("'saved' needs one name, like saved(\"coins\").", 0);
            return w.SavedValue(ToText(a[0]));
        };
    });

    static string SlotOf(CommandContext c) => c.Args.Count > 0 && c.Args[0] is string s && s.Trim().Length > 0 ? s.Trim() : DefaultSlot;

    /// <summary>Writes every global variable and the saved values to a slot.</summary>
    public void SaveGame(string slot = DefaultSlot)
    {
        var data = ReadSlot(slot) ?? new JsonObject();
        var globals = new JsonObject();
        foreach (var (k, v) in Globals)
            if (ToJson(v) is { } node) globals[k] = node;
        data["globals"] = globals;
        var inv = new JsonObject();
        foreach (var s in Inventory) inv[s.Name] = s.Count; // the items (World.Inventory.cs)
        data["inventory"] = inv;
        data["store"] = StoreJson(slot == DefaultSlot ? Store() : ReadStore(data));
        WriteSlot(slot, data);
    }

    /// <summary>Brings back the global variables and saved values of a slot. False when there's no save there.</summary>
    public bool LoadGame(string slot = DefaultSlot)
    {
        var data = ReadSlot(slot);
        if (data == null) return false;
        if (data["globals"] is JsonObject g)
            foreach (var (k, v) in g) Globals[k] = FromSaveJson(v);
        if (data["inventory"] is JsonObject inv)
        {
            Inventory.Clear();
            foreach (var (k, v) in inv)
                if (FromSaveJson(v) is double n && n >= 1) Inventory.Add(new ItemStack(k, (int)n));
        }
        if (slot == DefaultSlot) _store = ReadStore(data);
        return true;
    }

    public void ClearSave(string slot = DefaultSlot)
    {
        if (slot == DefaultSlot) _store = new();
        if (SaveFolder == null) { MemorySaves.Remove(slot); return; }
        try { File.Delete(SlotPath(slot)); }
        catch (DirectoryNotFoundException) { } // no saves folder yet: nothing to clear
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ScriptError($"The save “{slot}” couldn't be cleared: {e.Message}", 0);
        }
    }

    public void SaveValue(string key, object? value)
    {
        Store()[key] = value is GameObject g ? g.Name : value is MechanismInstance m ? m.Def.Name : CopyValue(value); // a copy: changing the list later doesn't change the save
        var data = ReadSlot(DefaultSlot) ?? new JsonObject();
        data["store"] = StoreJson(Store());
        WriteSlot(DefaultSlot, data);
    }

    /// <summary>A saved value ('save "coins", 12' or a global kept by 'save game'), or null.</summary>
    public object? SavedValue(string key)
    {
        if (Store().TryGetValue(key, out var v)) return v;
        return ReadSlot(DefaultSlot)?["globals"] is JsonObject g && g.TryGetPropertyValue(key, out var n) ? FromSaveJson(n) : null;
    }

    Dictionary<string, object?> Store() => _store ??= ReadSlot(DefaultSlot) is { } d ? ReadStore(d) : new();

    static Dictionary<string, object?> ReadStore(JsonObject data)
    {
        var s = new Dictionary<string, object?>();
        if (data["store"] is JsonObject o)
            foreach (var (k, v) in o) s[k] = FromSaveJson(v);
        return s;
    }

    static JsonObject StoreJson(Dictionary<string, object?> store)
    {
        var o = new JsonObject();
        foreach (var (k, v) in store)
            if (ToJson(v) is { } node) o[k] = node;
        return o;
    }

    static JsonNode? ToJson(object? v) => ToJson(v, []);

    /// <summary>
    /// A value as save JSON (null = it can't be saved: an object, a mechanism). A list or record inside itself, or nested
    /// deeper than a save file can hold, is an error instead of a save that never ends.
    /// </summary>
    static JsonNode? ToJson(object? v, List<object> path)
    {
        switch (v)
        {
            case double d when double.IsFinite(d): return JsonValue.Create(d);
            case bool b: return JsonValue.Create(b);
            case string s: return JsonValue.Create(s);
            case List<object?> or Record:
            {
                if (path.Exists(p => ReferenceEquals(p, v)))
                    throw new ScriptError("A list or record that contains itself can't be saved (it would never end).", 0);
                if (path.Count >= SaveDepth) throw new ScriptError($"Lists and records nested more than {SaveDepth} deep can't be saved.", 0);
                path.Add(v);
                JsonNode node = v is List<object?> l
                    ? new JsonArray(l.Select(x => ToJson(x, path) ?? JsonValue.Create("")).ToArray()) // lists and records (World.Records.cs)
                    : new JsonObject(((Record)v).Select(kv => KeyValuePair.Create(kv.Key, ToJson(kv.Value, path))));
                path.RemoveAt(path.Count - 1);
                return node;
            }
            default: return null;
        }
    }

    /// <summary>System.Text.Json writes at most 64 levels; the save's own levels (slot → globals → value) come first.</summary>
    const int SaveDepth = 60;

    /// <summary>A saved JSON value as a script value: numbers, yes/no, texts, lists and records.</summary>
    static object? FromSaveJson(JsonNode? n) => n switch
    {
        JsonObject o => o.Aggregate(new Record(), (r, kv) => { r[kv.Key] = FromSaveJson(kv.Value); return r; }),
        JsonArray a => a.Select(FromSaveJson).ToList(),
        _ => Project.Value(n)
    };

    string SlotPath(string slot)
    {
        var sb = new StringBuilder();
        foreach (var ch in slot) sb.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
        return Path.Combine(SaveFolder!, sb + ".json");
    }

    JsonObject? ReadSlot(string slot)
    {
        string? text;
        if (SaveFolder == null) text = MemorySaves.GetValueOrDefault(slot);
        else
        {
            string p = SlotPath(slot);
            try { text = File.Exists(p) ? File.ReadAllText(p) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                WarnOnce($"The save “{slot}” couldn't be read ({e.Message}): playing without it.");
                text = null;
            }
        }
        if (text == null) return null;
        try { return Project.Parse(text) as JsonObject; }
        catch (JsonException e)
        {
            WarnOnce($"The save “{slot}” is damaged ({e.Message}): playing without it. Saving again replaces it.");
            return null;
        }
    }

    void WriteSlot(string slot, JsonObject data)
    {
        string text = data.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (SaveFolder == null) { MemorySaves[slot] = text; return; }
        string p = SlotPath(slot), tmp = p + ".tmp";
        // A disk problem (full, read-only, a file locked by a sync app) is the script's error, not a crash of the whole
        // game: the line reports it and the game goes on. The old save stays as it was (the new one is written aside first).
        try
        {
            Directory.CreateDirectory(SaveFolder);
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, p, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ScriptError($"The game couldn't be saved to “{slot}”: {e.Message}", 0);
        }
    }
}
