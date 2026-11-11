using System.Reflection;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

/// <summary>Where a behavior package came from (docs/BEHAVIOR-FORMAT.md, "Where packages live").</summary>
public enum PackageOrigin { Code, Project, Installed, Shipped }

/// <summary>
/// Behavior packages: finding them (the project's <c>behaviors/</c>, the installed folder, the ones shipped inside this
/// library), copying them where they are needed, and what the inspector needs (default values, "show if").
/// </summary>
public sealed partial class BehaviorPackage
{
    /// <summary>Where it came from: set by <see cref="DiscoverAll"/> and <see cref="Shipped"/> (<see cref="PackageOrigin.Code"/> otherwise).</summary>
    public PackageOrigin Origin { get; set; }

    /// <summary>A copy that can be changed without touching this one (same origin and file).</summary>
    public BehaviorPackage Clone()
    {
        var c = Parse(ToJson(), SourcePath);
        c.Origin = Origin;
        return c;
    }

    // ---------------- Shipped packages ----------------

    const string ShippedPrefix = "Packages.";

    /// <summary>The embedded Packages/*.behavior files, read and checked once: file name, JSON, problems (empty = usable).</summary>
    static readonly Lazy<List<(string File, string Json, List<string> Problems)>> ShippedFiles = new(() =>
    {
        var list = new List<(string, string, List<string>)>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(ShippedPrefix) && n.EndsWith(Extension)).Order(StringComparer.OrdinalIgnoreCase))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            string json = r.ReadToEnd();
            List<string> problems;
            try { problems = Parse(json).Validate(); }
            catch (FormatException e) { problems = [e.Message]; }
            list.Add((res[ShippedPrefix.Length..], json, problems));
        }
        return list;
    });

    /// <summary>
    /// The packages shipped with BuilderEngine (Packages/*.behavior, embedded in this library), each a fresh copy, in name
    /// order. A broken one (a bug) is left out and described in <paramref name="problems"/>.
    /// </summary>
    public static List<BehaviorPackage> Shipped(List<string>? problems = null)
    {
        var list = new List<BehaviorPackage>();
        foreach (var (file, json, errs) in ShippedFiles.Value)
        {
            if (errs.Count > 0) { problems?.Add($"{file} (shipped): {string.Join(" ", errs)}"); continue; }
            var p = Parse(json);
            p.Origin = PackageOrigin.Shipped;
            list.Add(p);
        }
        return list;
    }

    /// <summary>The shipped package's file as it is inside the library (for "Export…"), or null.</summary>
    public static string? ShippedJson(string name) =>
        ShippedFiles.Value.FirstOrDefault(f => string.Equals(f.File, name + Extension, StringComparison.OrdinalIgnoreCase)).Json;

    // ---------------- Every package a project can use ----------------

    /// <summary>A project's own packages folder: <c>&lt;project&gt;/behaviors</c>.</summary>
    public static string ProjectFolder(string projectDir) => Path.Combine(projectDir, ProjectFolderName);

    /// <summary>The file a package is kept in inside a folder: <c>&lt;Name&gt;.behavior</c>.</summary>
    public static string FileIn(string folder, string name) => Path.Combine(folder, Project.SafeFileName(name) + Extension);

    /// <summary>
    /// Every valid package a project can use, in precedence order: the project's own (<c>behaviors/</c>), then the
    /// installed ones (<see cref="InstalledFolder"/>), then the shipped ones — on a name clash the first wins. Each has its
    /// <see cref="Origin"/>. Broken files are described in <paramref name="problems"/> (never thrown).
    /// <paramref name="projectDir"/> null = no project folder.
    /// </summary>
    public static List<BehaviorPackage> DiscoverAll(string? projectDir, List<string>? problems = null)
    {
        var all = new List<BehaviorPackage>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<BehaviorPackage> found, PackageOrigin origin)
        {
            foreach (var p in found)
                if (names.Add(p.Name)) { p.Origin = origin; all.Add(p); }
        }
        if (!string.IsNullOrWhiteSpace(projectDir)) Add(Discover([ProjectFolder(projectDir)], problems), PackageOrigin.Project);
        Add(Discover([InstalledFolder], problems), PackageOrigin.Installed);
        Add(Shipped(problems), PackageOrigin.Shipped);
        return all;
    }

    /// <summary>The first package (precedence order) that implements this EasyEngine behavior kind (<see cref="Replaces"/>), or null.</summary>
    public static BehaviorPackage? Replacing(IEnumerable<BehaviorPackage> packages, string kind) =>
        packages.FirstOrDefault(p => p.Replaces != null && string.Equals(p.Replaces, kind.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves a copy into a project's <c>behaviors/</c> folder (so the game works on any computer). Returns the file.</summary>
    public string CopyToProject(string projectDir)
    {
        string path = FileIn(ProjectFolder(projectDir), Name);
        var c = Clone();
        c.Save(path);
        return path;
    }

    /// <summary>Saves a copy into the installed folder, offered to every project in both engines. Returns the file.</summary>
    public string Install()
    {
        string path = FileIn(InstalledFolder, Name);
        var c = Clone();
        c.Save(path);
        return path;
    }

    /// <summary>Reads a .behavior file and checks it; the problems in plain words (empty = it can be used).</summary>
    public static (BehaviorPackage? Package, List<string> Problems) TryLoad(string path)
    {
        try
        {
            var p = Load(path);
            return (p, p.Validate());
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            return (null, [e.Message]);
        }
    }

    // ---------------- Values and "show if" ----------------

    /// <summary>The settings' default values as the runtime sees them (a number, yes/no, a text, a list), by name.</summary>
    public Dictionary<string, object?> DefaultValues()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in AllParams()) d[p.Name] = ParamTypes.Convert(Project.Value(p.Default), p.Type);
        return d;
    }

    /// <summary>The setting with this name (manifest or script), or null.</summary>
    public PackageParam? Param(string name) => AllParams().FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// Whether the inspector shows this setting now: no "show if", or it is true for these values (a setting not in
    /// <paramref name="values"/> has its default). A condition that can't be worked out shows the field.
    /// </summary>
    public bool Shows(PackageParam p, IReadOnlyDictionary<string, object?> values)
    {
        if (p.ShowIf == null) return true;
        try
        {
            var expr = Parser.ParseExpression(p.ShowIf);
            Dictionary<string, object?>? defaults = null;
            object? Lookup(string name)
            {
                if (values.TryGetValue(name, out var v)) return v;
                defaults ??= DefaultValues();
                return defaults.TryGetValue(name, out var dv) ? dv : throw new ArgumentException(name);
            }
            return World.Truthy(Eval(expr, Lookup));
        }
        catch (Exception e) when (e is ScriptError or ArgumentException or InvalidCastException) { return true; }
    }

    /// <summary>A "show if" condition over the settings: literals, names, not / and / or, comparisons and + - * /.</summary>
    static object? Eval(Expr e, Func<string, object?> lookup)
    {
        switch (e)
        {
            case NumberExpr n: return n.Value;
            case StringExpr s: return s.Value;
            case BoolExpr b: return b.Value;
            case NameExpr { Path.Length: 1 } name: return lookup(name.Path[0]);
            case UnaryExpr { Op: "not" } u: return !World.Truthy(Eval(u.Operand, lookup));
            case UnaryExpr { Op: "-" } u: return -World.ToNumber(Eval(u.Operand, lookup), 0);
            case BinaryExpr { Op: "and" } a: return World.Truthy(Eval(a.Left, lookup)) && World.Truthy(Eval(a.Right, lookup));
            case BinaryExpr { Op: "or" } o: return World.Truthy(Eval(o.Left, lookup)) || World.Truthy(Eval(o.Right, lookup));
            case BinaryExpr bin:
            {
                object? l = Eval(bin.Left, lookup), r = Eval(bin.Right, lookup);
                double Num(object? v) => World.ToNumber(v, 0);
                bool Same() => l is string || r is string
                    ? string.Equals(World.ToText(l), World.ToText(r), StringComparison.OrdinalIgnoreCase)
                    : l is bool || r is bool ? World.Truthy(l) == World.Truthy(r) : Num(l) == Num(r);
                return bin.Op switch
                {
                    "==" => Same(),
                    "!=" => !Same(),
                    "<" => Num(l) < Num(r),
                    ">" => Num(l) > Num(r),
                    "<=" => Num(l) <= Num(r),
                    ">=" => Num(l) >= Num(r),
                    "+" when l is string || r is string => World.ToText(l) + World.ToText(r),
                    "+" => Num(l) + Num(r),
                    "-" => Num(l) - Num(r),
                    "*" => Num(l) * Num(r),
                    "/" => Num(r) == 0 ? 0.0 : Num(l) / Num(r),
                    _ => throw new ArgumentException(bin.Op),
                };
            }
            default: throw new ArgumentException("unsupported");
        }
    }
}
