using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BuilderEngine.Script;

/// <summary>
/// A behavior package (<c>.behavior</c>, docs/BEHAVIOR-FORMAT.md): a behavior both engines understand — a manifest
/// (name, icon, description, settings with their pickers) plus its logic in Builder Script. This class is the ONE
/// reader/writer/validator; BuilderEngine and EasyEngine (through the DLL) never parse the file themselves.
/// Unknown properties are kept when the package is written back.
/// </summary>
public sealed partial class BehaviorPackage
{
    public const string FormatName = "builderengine.behavior";
    public const int FormatVersion = 1;
    public const string Extension = ".behavior";

    public string Name { get; set; } = "";
    /// <summary>The EasyEngine BehaviorKind this package implements ("Spin"), or null.</summary>
    public string? Replaces { get; set; }
    public string Icon { get; set; } = "⚙";
    public string Category { get; set; } = "General";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string License { get; set; } = "";
    public string PackageVersion { get; set; } = "1.0.0";
    public bool Repeatable { get; set; }
    public List<PackageParam> Params { get; set; } = [];
    public List<string> AiKeywords { get; set; } = [];
    public string AiGuide { get; set; } = "";
    public string Script { get; set; } = "";

    /// <summary>Where it was read from (null when built in code).</summary>
    public string? SourcePath { get; set; }

    JsonObject _raw = new(); // the whole file as read: unknown properties survive ToJson

    /// <summary>One setting: a Builder Script param type (so it gets a picker), its default and how the inspector shows it.</summary>
    public sealed class PackageParam
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "number";
        public JsonNode? Default { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Step { get; set; }
        public string Help { get; set; } = "";
        /// <summary>A Builder Script condition over the other params; the field is hidden while it is false.</summary>
        public string? ShowIf { get; set; }
        /// <summary>
        /// The key of this setting in an EasyEngine behavior's Nums/Texts when it isn't <see cref="Name"/> (EasyEngine's
        /// "to" and "max" can't be Builder Script names). Only read for a package with <see cref="Replaces"/>.
        /// </summary>
        public string? EasyKey { get; set; }
        /// <summary>Where an EasyEngine behavior keeps this setting: <see cref="EasyKey"/>, else the name.</summary>
        public string EasyEngineKey => EasyKey is { Length: > 0 } k ? k : Name;
        public ParamKind Kind => ParamTypes.Kind(Type);
        /// <summary>The options of a <c>Choice "a", "b"</c> type (empty for other types).</summary>
        public List<string> Options => ChoiceOptions(Type);
    }

    // ---------------- Reading ----------------

    /// <summary>Reads a package. Throws <see cref="FormatException"/> with a plain-words message when the file can't be one;
    /// call <see cref="Validate"/> afterwards for the deeper checks (script, params).</summary>
    public static BehaviorPackage Parse(string json, string? sourcePath = null)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new FormatException("A behavior file must be a JSON object.");
        }
        catch (JsonException e) { throw new FormatException($"The behavior file isn't valid JSON: {e.Message}"); }

        string format = Str(root, "format");
        if (format != FormatName) throw new FormatException($"Not a behavior package (format is \"{format}\", expected \"{FormatName}\").");
        int version = (int)Num(root, "version", 0);
        if (version > FormatVersion)
            throw new FormatException($"This behavior was made for a newer version of the format ({version}); update BuilderEngine/EasyEngine to use it.");

        var p = new BehaviorPackage
        {
            _raw = root,
            SourcePath = sourcePath,
            Name = Str(root, "name"),
            Replaces = root["replaces"] is JsonValue r && r.TryGetValue<string>(out var rs) && rs.Length > 0 ? rs : null,
            Icon = Str(root, "icon", "⚙"),
            Category = Str(root, "category", "General"),
            Description = Str(root, "description"),
            Author = Str(root, "author"),
            License = Str(root, "license"),
            PackageVersion = Str(root, "packageVersion", "1.0.0"),
            Repeatable = root["repeatable"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb,
            Script = Str(root, "script"),
        };
        if (root["params"] is JsonArray ps)
            foreach (var n in ps)
                if (n is JsonObject o)
                    p.Params.Add(new PackageParam
                    {
                        Name = Str(o, "name"),
                        Label = Str(o, "label"),
                        Type = Str(o, "type", "number"),
                        Default = o["default"]?.DeepClone(),
                        Min = o["min"] is JsonValue ? Num(o, "min", 0) : null,
                        Max = o["max"] is JsonValue ? Num(o, "max", 0) : null,
                        Step = o["step"] is JsonValue ? Num(o, "step", 0) : null,
                        Help = Str(o, "help"),
                        ShowIf = o["showIf"] is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s : null,
                        EasyKey = o["easyKey"] is JsonValue ek && ek.TryGetValue<string>(out var eks) && eks.Trim().Length > 0 ? eks.Trim() : null,
                    });
        if (root["ai"] is JsonObject ai)
        {
            if (ai["keywords"] is JsonArray kw)
                foreach (var k in kw) if (k is JsonValue kv && kv.TryGetValue<string>(out var ks)) p.AiKeywords.Add(ks);
            p.AiGuide = Str(ai, "guide");
        }
        return p;
    }

    public static BehaviorPackage Load(string path) => Parse(File.ReadAllText(path), path);

    // ---------------- Validation ----------------

    static readonly Regex NamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Every problem with the package, in plain words; empty when it can be used.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Name.Length == 0) errors.Add("It has no \"name\".");
        else if (!NamePattern.IsMatch(Name)) errors.Add($"\"{Name}\" isn't a valid name (letters, digits and _ only, not starting with a digit).");

        MechanismDef? def = null;
        if (Script.Trim().Length == 0) errors.Add("It has no \"script\".");
        else
        {
            try
            {
                var defs = Parser.Parse(Script);
                if (defs.Count != 1) errors.Add($"The script must hold exactly one mechanism (it has {defs.Count}).");
                else if (defs[0].Name != Name) errors.Add($"The script's mechanism is called \"{defs[0].Name}\", but the package is \"{Name}\".");
                else def = defs[0];
            }
            catch (ScriptError e) { errors.Add($"The script has an error: {e}"); }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pp in Params)
        {
            string label = pp.Name.Length > 0 ? pp.Name : "(no name)";
            if (pp.Name.Length == 0 || !NamePattern.IsMatch(pp.Name)) { errors.Add($"A setting has an invalid name: \"{label}\"."); continue; }
            if (!seen.Add(pp.Name)) errors.Add($"The setting \"{pp.Name}\" is listed twice.");
            if (pp.Kind == ParamKind.Any && ParamTypes.Base(pp.Type) is not ("any" or "")) errors.Add($"The setting \"{pp.Name}\" has an unknown type \"{pp.Type}\".");
            if (pp.Kind == ParamKind.Choice && pp.Options.Count == 0) errors.Add($"The setting \"{pp.Name}\" is a Choice with no options.");
            if (def != null)
            {
                var sp = def.Params.FirstOrDefault(x => x.Name == pp.Name);
                if (sp == null) errors.Add($"The setting \"{pp.Name}\" isn't a param of the script.");
                else if (!SameType(ScriptType(sp), pp.Type)) errors.Add($"The setting \"{pp.Name}\" is \"{pp.Type}\" here but \"{ScriptType(sp)}\" in the script.");
            }
            if (pp.Default != null && !DefaultFits(pp)) errors.Add($"The default of \"{pp.Name}\" doesn't fit its type \"{pp.Type}\".");
            if (pp.ShowIf != null)
            {
                try { Parser.ParseExpression(pp.ShowIf); }
                catch (ScriptError e) { errors.Add($"The \"show if\" of \"{pp.Name}\" has an error: {e.Message}"); }
            }
        }
        return errors;
    }

    public bool IsValid => Validate().Count == 0;

    /// <summary>The settings to show, in order: the manifest's, then any script param it doesn't list (with script defaults).</summary>
    public List<PackageParam> AllParams()
    {
        var list = new List<PackageParam>(Params);
        try
        {
            var defs = Parser.Parse(Script);
            if (defs.Count == 1)
                foreach (var sp in defs[0].Params)
                    if (list.All(x => x.Name != sp.Name))
                        list.Add(new PackageParam
                        {
                            Name = sp.Name, Label = sp.Name.Replace('_', ' '), Type = ScriptType(sp),
                            Default = ParamTypes.TryConstant(sp.Default, out var v) ? ToNode(v) : null,
                        });
        }
        catch (ScriptError) { /* Validate reports it; the manifest's own settings still show */ }
        return list;
    }

    /// <summary>A script param's full type as written: the parser keeps a Choice's options apart (<see cref="ParamDef.Options"/>).</summary>
    public static string ScriptType(ParamDef p) =>
        p.Options is { Count: > 0 } opts ? "Choice " + string.Join(", ", opts.Select(o => "\"" + o.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) : p.Type;

    static bool SameType(string a, string b) =>
        string.Equals(Regex.Replace(a.Trim(), @"\s+", " "), Regex.Replace(b.Trim(), @"\s+", " "), StringComparison.OrdinalIgnoreCase);

    static bool DefaultFits(PackageParam p) => p.Kind switch
    {
        ParamKind.Number => p.Default is JsonValue v && v.TryGetValue<double>(out _),
        ParamKind.Bool => p.Default is JsonValue v && v.TryGetValue<bool>(out _),
        ParamKind.List or ParamKind.Path => p.Default is JsonArray,
        ParamKind.Choice => p.Default is JsonValue v && v.TryGetValue<string>(out var s) && p.Options.Contains(s),
        ParamKind.Any => true,
        _ => p.Default is JsonValue v && v.TryGetValue<string>(out _),
    };

    /// <summary>The quoted options of a <c>Choice "a", "b"</c> type.</summary>
    public static List<string> ChoiceOptions(string type) =>
        ParamTypes.Kind(type) != ParamKind.Choice ? []
        : Regex.Matches(type, "\"((?:[^\"\\\\]|\\\\.)*)\"").Select(m => m.Groups[1].Value).ToList();

    // ---------------- Writing ----------------

    /// <summary>The package as JSON (indented); unknown properties from the file it was read from are kept.</summary>
    public string ToJson()
    {
        var root = (JsonObject)_raw.DeepClone();
        root["format"] = FormatName;
        root["version"] = FormatVersion;
        root["name"] = Name;
        if (Replaces != null) root["replaces"] = Replaces; else root.Remove("replaces");
        root["icon"] = Icon;
        root["category"] = Category;
        root["description"] = Description;
        SetOrRemove(root, "author", Author);
        SetOrRemove(root, "license", License);
        root["packageVersion"] = PackageVersion;
        if (Repeatable) root["repeatable"] = true; else root.Remove("repeatable");
        var ps = new JsonArray();
        foreach (var p in Params)
        {
            var o = new JsonObject { ["name"] = p.Name };
            if (p.Label.Length > 0) o["label"] = p.Label;
            o["type"] = p.Type;
            if (p.Default != null) o["default"] = p.Default.DeepClone();
            if (p.Min is { } mn) o["min"] = mn;
            if (p.Max is { } mx) o["max"] = mx;
            if (p.Step is { } st) o["step"] = st;
            if (p.Help.Length > 0) o["help"] = p.Help;
            if (p.ShowIf != null) o["showIf"] = p.ShowIf;
            if (p.EasyKey != null) o["easyKey"] = p.EasyKey;
            ps.Add(o);
        }
        root["params"] = ps;
        if (AiKeywords.Count > 0 || AiGuide.Length > 0)
        {
            var ai = new JsonObject();
            if (AiKeywords.Count > 0) ai["keywords"] = new JsonArray(AiKeywords.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
            if (AiGuide.Length > 0) ai["guide"] = AiGuide;
            root["ai"] = ai;
        }
        else root.Remove("ai");
        root["script"] = Script;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Writes the package atomically (temp file, then replace).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
        SourcePath = path;
    }

    /// <summary>
    /// A package from a mechanism: the manifest's settings are built from its params (a Choice keeps its options,
    /// defaults come from the script). Used by "Save as behavior…".
    /// </summary>
    public static BehaviorPackage FromMechanism(string source, string? description = null)
    {
        var defs = Parser.Parse(source);
        if (defs.Count != 1) throw new FormatException("A behavior is made from exactly one mechanism.");
        var d = defs[0];
        var p = new BehaviorPackage { Name = d.Name, Description = description ?? "", Script = source.Replace("\r\n", "\n") };
        foreach (var sp in d.Params)
            p.Params.Add(new PackageParam
            {
                Name = sp.Name, Label = sp.Name.Replace('_', ' '), Type = ScriptType(sp),
                Default = ParamTypes.TryConstant(sp.Default, out var v) ? ToNode(v) : null,
            });
        return p;
    }

    // ---------------- Finding packages ----------------

    /// <summary>
    /// Every valid package in these folders, first folder winning on a name clash (pass the project's
    /// <c>behaviors/</c> first, then the installed folder). Broken files are returned in <paramref name="problems"/>.
    /// </summary>
    public static List<BehaviorPackage> Discover(IEnumerable<string> folders, List<string>? problems = null)
    {
        var found = new Dictionary<string, BehaviorPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*" + Extension).Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var p = Load(file);
                    var errs = p.Validate();
                    if (errs.Count > 0) { problems?.Add($"{Path.GetFileName(file)}: {string.Join(" ", errs)}"); continue; }
                    found.TryAdd(p.Name, p);
                }
                catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
                {
                    problems?.Add($"{Path.GetFileName(file)}: {e.Message}");
                }
            }
        }
        return found.Values.ToList();
    }

    /// <summary>The person's installed packages folder, shared by both engines (%APPDATA%\BuilderEngine\Behaviors).
    /// The editor points it elsewhere when its app-data folder is moved (--appdata, --snap); tests use a temp folder.</summary>
    public static string InstalledFolder
    {
        get => _installedFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BuilderEngine", "Behaviors");
        set => _installedFolder = string.IsNullOrWhiteSpace(value) ? null : value;
    }
    static string? _installedFolder;

    public const string ProjectFolderName = "behaviors";

    // ---------------- helpers ----------------

    static string Str(JsonObject o, string key, string def = "") =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;

    static double Num(JsonObject o, string key, double def) =>
        o[key] is JsonValue v ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<string>(out var s) && Project.TryNumber(s, out var n) ? n : def) : def;

    static void SetOrRemove(JsonObject o, string key, string value)
    {
        if (value.Length > 0) o[key] = value; else o.Remove(key);
    }

    static JsonNode? ToNode(object? v) => v switch
    {
        null => null,
        double d => JsonValue.Create(d),
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        IEnumerable<object?> list => new JsonArray(list.Select(ToNode).ToArray()),
        _ => JsonValue.Create(v.ToString()),
    };
}
