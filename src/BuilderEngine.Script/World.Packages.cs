namespace BuilderEngine.Script;

/// <summary>
/// The behavior packages a game uses (docs/BEHAVIOR-FORMAT.md): each is a mechanism named after it, loaded by
/// <see cref="GameBoot.UseProject"/> from the project's <c>behaviors/</c>, the installed folder and the shipped ones.
/// EasyCompat plays an EasyEngine behavior with the package that <c>replaces</c> it (<see cref="PackageReplacing"/>).
/// </summary>
public sealed partial class World
{
    readonly List<BehaviorPackage> _packages = [];

    /// <summary>The behavior packages loaded into this game, in precedence order (project, installed, shipped).</summary>
    public IReadOnlyList<BehaviorPackage> Packages => _packages;

    /// <summary>The loaded package with this name, or null.</summary>
    public BehaviorPackage? Package(string name)
    {
        name = name.Trim();
        foreach (var p in _packages) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>The loaded package that implements this EasyEngine behavior kind ("replaces"), or null.</summary>
    public BehaviorPackage? PackageReplacing(string kind) => BehaviorPackage.Replacing(_packages, kind);

    /// <summary>
    /// Loads behavior packages, in precedence order, as mechanisms named after them. A package with the name of one
    /// already loaded is skipped (the first wins), and so is one named like a mechanism in <paramref name="ownMechanisms"/>
    /// (the project's own mechanism wins). One that doesn't load is said once in the console, never thrown.
    /// </summary>
    public void UsePackages(IEnumerable<BehaviorPackage> packages, IEnumerable<string>? ownMechanisms = null)
    {
        var own = new HashSet<string>(ownMechanisms ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages)
        {
            if (Package(p.Name) != null || own.Contains(p.Name)) continue;
            try
            {
                Load(p.Script);
                _packages.Add(p);
            }
            catch (ScriptError e) { PackageProblem($"{p.Name}: {e.Message}"); }
        }
    }

    /// <summary>A broken behavior package, said in the console (and kept in <see cref="Output"/>).</summary>
    public void PackageProblem(string what)
    {
        string line = "⚠ Behavior package " + what;
        AddOutput(line);
        RaisePrinted(line);
    }
}
