namespace BuilderEngine.Script;

/// <summary>What the inspector shows for a param (its picker) and what the value is at runtime.</summary>
public enum ParamKind { Any, Number, Bool, Text, Sound, Image, Scene, Object, Path, List, Color, Key, Choice, Animation, FmodEvent }

/// <summary>
/// Param types (owner rule: values are picked, not typed). The value saved in project.json and the value a script sees:
/// <list type="table">
/// <item><term>Object</term><description>saved as the object's name; at runtime the object itself (or nothing)</description></item>
/// <item><term>Path</term><description>a list of points <c>[[x, y], ...]</c> in scene pixels — <c>for each p in path</c> + <c>move to p[0], p[1] in 1</c></description></item>
/// <item><term>List / List of T</term><description>a list; each item converted like a param of type T</description></item>
/// <item><term>Color, Key, Choice</term><description>a text: <c>"#E05D5D"</c>, <c>"space"</c>, one of the options</description></item>
/// </list>
/// Loading is tolerant: a single value for a list becomes a list of one, a list written as JSON text is read.
/// </summary>
public static class ParamTypes
{
    /// <summary>The first word of a type, lower case: "List of number" → "list".</summary>
    public static string Base(string type)
    {
        type = type.Trim();
        int sp = type.IndexOf(' ');
        return (sp < 0 ? type : type[..sp]).ToLowerInvariant();
    }

    public static ParamKind Kind(string type) => Base(type) switch
    {
        "number" or "seconds" => ParamKind.Number,
        "bool" or "yesno" => ParamKind.Bool,
        "text" or "string" => ParamKind.Text,
        "sound" => ParamKind.Sound,
        "image" or "picture" => ParamKind.Image,
        "scene" => ParamKind.Scene,
        "object" => ParamKind.Object,
        "path" => ParamKind.Path,
        "list" => ParamKind.List,
        "color" or "colour" => ParamKind.Color,
        "key" => ParamKind.Key,
        "choice" => ParamKind.Choice,
        "animation" => ParamKind.Animation, // one of the object's animations (a dropdown)
        "fmodevent" => ParamKind.FmodEvent, // an event of the project's FMOD Studio banks (a dropdown; FmodStudio.ListEvents)
        _ => ParamKind.Any
    };

    /// <summary>The type of a list's items: "List of number" → "number"; a plain "List" → "any".</summary>
    public static string ItemType(string type)
    {
        int at = type.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? "any" : type[(at + 4)..].Trim();
    }

    /// <summary>A point of a path: <c>[x, y]</c>.</summary>
    public static List<object?> Point(double x, double y) => [x, y];

    /// <summary>The value of a param that has no default: 0, false, "", an empty list, or nothing (Object).</summary>
    public static object? Empty(string type) => Kind(type) switch
    {
        ParamKind.Number => 0.0,
        ParamKind.Bool => false,
        ParamKind.Text or ParamKind.Animation or ParamKind.FmodEvent => "",
        ParamKind.Path or ParamKind.List => new List<object?>(),
        _ => null
    };

    /// <summary>A saved value as the runtime wants it for this type (always a fresh copy for lists).</summary>
    public static object? Convert(object? value, string type)
    {
        switch (Kind(type))
        {
            case ParamKind.Number:
                return value switch
                {
                    double d => d,
                    bool b => b ? 1.0 : 0.0,
                    string s when Project.TryNumber(s, out var d) => d,
                    _ => 0.0
                };
            case ParamKind.Bool:
                return value switch
                {
                    bool b => b,
                    double d => d != 0,
                    string s => s.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "on",
                    _ => false
                };
            case ParamKind.Text or ParamKind.Sound or ParamKind.Scene or ParamKind.Image or ParamKind.Color or ParamKind.Choice or ParamKind.Animation or ParamKind.FmodEvent:
                return World.ToText(value);
            case ParamKind.Key:
                return World.ToText(value).Trim().ToLowerInvariant();
            case ParamKind.Object:
                return value switch
                {
                    GameObject g => g,
                    null => null,
                    _ => World.ToText(value).Trim() is { Length: > 0 } name ? name : null
                };
            case ParamKind.Path:
            {
                var points = new List<object?>();
                foreach (var item in AsList(value))
                    if (ToPoint(item) is { } pt) points.Add(pt);
                return points;
            }
            case ParamKind.List:
            {
                string itemType = ItemType(type);
                var list = new List<object?>();
                foreach (var item in AsList(value)) list.Add(Convert(item, itemType));
                return list;
            }
            default: // 'any' or a custom type: numbers written as text become numbers; lists are copied
                return value switch
                {
                    string t when Project.TryNumber(t, out var n) => n,
                    List<object?> l => l.Select(x => Convert(x, "any")).ToList(),
                    _ => value
                };
        }
    }

    /// <summary>The items of a saved list: a list, a list written as JSON text, nothing, or one value.</summary>
    static List<object?> AsList(object? value)
    {
        switch (value)
        {
            case null: return [];
            case List<object?> l: return l;
            case string s when s.TrimStart().StartsWith('['):
                try { if (Project.Value(Project.Parse(s)) is List<object?> parsed) return parsed; } catch (System.Text.Json.JsonException) { } // not a JSON list: no items
                return [];
            case string { Length: 0 }: return [];
            default: return [value];
        }
    }

    static List<object?>? ToPoint(object? item)
    {
        if (item is List<object?> { Count: >= 2 } l && IsNumber(l[0]) && IsNumber(l[1]))
            return Point(World.ToNumber(l[0], 0), World.ToNumber(l[1], 0));
        if (item is string s && s.Split(',') is { Length: 2 } parts && Project.TryNumber(parts[0], out var x) && Project.TryNumber(parts[1], out var y))
            return Point(x, y);
        return null;
    }

    static bool IsNumber(object? v) => v is double || v is string s && Project.TryNumber(s, out _);

    /// <summary>
    /// The value of a default written with literals only (<c>100</c>, <c>"a.wav"</c>, <c>-2</c>, <c>[[0, 0], [100, 0]]</c>);
    /// false when it needs the game to work out (<c>max * 2</c>).
    /// </summary>
    public static bool TryConstant(Expr? e, out object? value)
    {
        value = null;
        switch (e)
        {
            case null: return false;
            case NumberExpr n: value = n.Value; return true;
            case StringExpr s: value = s.Value; return true;
            case BoolExpr b: value = b.Value; return true;
            case UnaryExpr { Op: "-", Operand: NumberExpr n }: value = -n.Value; return true;
            case ListExpr l:
            {
                var list = new List<object?>();
                foreach (var item in l.Items)
                {
                    if (!TryConstant(item, out var v)) return false;
                    list.Add(v);
                }
                value = list;
                return true;
            }
            default: return false;
        }
    }

    /// <summary>A value written back as Builder Script (a param default): <c>[[10, 20], [30, 40]]</c>, <c>"#E05D5D"</c>.</summary>
    public static Expr ToExpr(object? value) => value switch
    {
        double d when d < 0 => new UnaryExpr("-", new NumberExpr(-d, 0), 0),
        double d => new NumberExpr(d, 0),
        bool b => new BoolExpr(b, 0),
        List<object?> l => new ListExpr(l.Select(ToExpr).ToList(), 0),
        GameObject g => new StringExpr(g.Name, 0),
        null => new StringExpr("", 0),
        _ => new StringExpr(World.ToText(value), 0)
    };
}
