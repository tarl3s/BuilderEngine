namespace BuilderEngine.Script;

public sealed partial class World
{
    /// <summary>
    /// Creates the runtime objects of a scene (position, size, tags, mechanisms with their param overrides).
    /// A mechanism that can't be used (unknown name, broken 'uses', a variant of a missing mechanism) is reported in
    /// <see cref="Errors"/> and skipped; the object still appears. Object params (saved as names) become the objects
    /// once the whole scene is there. Returns each runtime object with the scene object it came from.
    /// </summary>
    public Dictionary<GameObject, SceneObject> LoadScene(Scene scene)
    {
        var map = new Dictionary<GameObject, SceneObject>();
        SetSceneSize(scene.Width, scene.Height);
        LoadTiles(scene); // World.Tiles.cs
        foreach (var so in scene.Objects)
        {
            var names = new List<string>();
            var overrides = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in so.Mechanisms)
            {
                MechanismDef? def;
                try { def = TryDef(m.Name, 0); }
                catch (ScriptError e) { SceneError($"'{so.Name}': {e.Message}"); continue; }
                if (def == null)
                {
                    SceneError($"'{so.Name}' uses '{m.Name}', but there's no mechanism with that name.{Suggest(m.Name, Library.Keys)}");
                    continue;
                }
                if (names.Contains(def.Name, StringComparer.OrdinalIgnoreCase)) continue;
                names.Add(def.Name);
                var ov = new Dictionary<string, object?>();
                foreach (var (k, v) in m.Params)
                {
                    var p = def.Params.FirstOrDefault(x => string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase));
                    if (p != null) ov[p.Name] = ConvertParam(v, p.Type);
                }
                overrides[def.Name] = ov;
            }

            GameObject o;
            try { o = Spawn(so.Name, so.X, so.Y, names, overrides, so.Tags.ToArray()); }
            catch (ScriptError e)
            {
                SceneError($"'{so.Name}': {e.Message}");
                o = Spawn(so.Name, so.X, so.Y, null, null, so.Tags.ToArray());
            }
            o.Width = so.Width;
            o.Height = so.Height;
            if (!string.IsNullOrWhiteSpace(so.Image)) o.Image = so.Image.Trim();
            o.Color = so.Color;
            LoadAnimations(o, so); // named animations + default one, prefab link (World.Prefabs.cs)
            if (o.Tags.Contains("ui")) o.Ui = true;
            map[o] = so;
        }
        // Now every object exists: Object params written as names point at the objects.
        foreach (var o in map.Keys)
            foreach (var inst in o.AllMechanisms())
                ResolveObjectParams(inst);
        return map;
    }

    void SceneError(string msg)
    {
        Errors.Add(msg);
        Printed?.Invoke("⚠ " + msg);
        Debug?.Event(DebugEventKind.Error, null, msg);
    }

    /// <summary>A saved param value as the runtime wants it for that param type (see <see cref="ParamTypes"/>).</summary>
    public static object? ConvertParam(object? value, string type) => ParamTypes.Convert(value, type);

    /// <summary>Object params (and lists of objects) that still hold a name get the object with that name (or tag).</summary>
    void ResolveObjectParams(MechanismInstance inst)
    {
        foreach (var p in inst.Def.Params)
        {
            var kind = ParamTypes.Kind(p.Type);
            if (kind == ParamKind.Object && inst.Vars.TryGetValue(p.Name, out var v) && v is string name)
                inst.Vars[p.Name] = ObjectNamed(name) ?? (object?)name;
            else if (kind == ParamKind.List && ParamTypes.Kind(ParamTypes.ItemType(p.Type)) == ParamKind.Object
                     && inst.Vars.TryGetValue(p.Name, out var lv) && lv is List<object?> list)
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is string n) list[i] = ObjectNamed(n) ?? (object?)n;
        }
    }

    /// <summary>The object with exactly this name, else the first with this name or tag (any case).</summary>
    GameObject? ObjectNamed(string name)
    {
        foreach (var o in LiveObjects()) if (o.Name == name) return o;
        return FindObject(name);
    }
}
