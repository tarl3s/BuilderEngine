namespace BuilderEngine.Script;

public sealed partial class World
{
    /// <summary>
    /// Creates the runtime objects of a scene (position, size, tags, mechanisms with their param overrides).
    /// A mechanism that can't be used (unknown name, broken 'uses') is reported in <see cref="Errors"/> and skipped;
    /// the object still appears. Returns each runtime object with the scene object it came from.
    /// </summary>
    public Dictionary<GameObject, SceneObject> LoadScene(Scene scene)
    {
        var map = new Dictionary<GameObject, SceneObject>();
        SetSceneSize(scene.Width, scene.Height);
        foreach (var so in scene.Objects)
        {
            var names = new List<string>();
            var overrides = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in so.Mechanisms)
            {
                if (!Library.TryGetValue(m.Name, out var def))
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
            map[o] = so;
        }
        return map;
    }

    void SceneError(string msg)
    {
        Errors.Add(msg);
        Printed?.Invoke("⚠ " + msg);
    }

    /// <summary>A saved param value as the runtime wants it for that param type.</summary>
    public static object? ConvertParam(object? value, string type)
    {
        switch (type.ToLowerInvariant())
        {
            case "number" or "seconds":
                return value switch
                {
                    double d => d,
                    bool b => b ? 1.0 : 0.0,
                    string s when Project.TryNumber(s, out var d) => d,
                    _ => 0.0
                };
            case "bool" or "yesno":
                return value switch
                {
                    bool b => b,
                    double d => d != 0,
                    string s => s.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "on",
                    _ => false
                };
            case "text" or "string" or "sound" or "scene" or "image" or "picture":
                return ToText(value);
            default: // 'any' or a custom type: numbers written as text become numbers
                return value is string t && Project.TryNumber(t, out var n) ? n : value;
        }
    }
}
