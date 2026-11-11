using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// BEHAVIORS FEATURE (docs/BEHAVIOR-FORMAT.md; the headless side is BehaviorPackage*.cs and World.Packages.cs): behavior
// packages (.behavior) — a behavior both engines understand — in the editor. The library's BEHAVIORS section lists the
// project's (behaviors/), the installed (%APPDATA%\BuilderEngine\Behaviors) and the shipped ones; each is a mechanism
// named after it: drag it onto the scene or an object like any mechanism (an installed or shipped one is copied into the
// project's behaviors/ first, so the game works on any computer). Its inspector card shows the package's labels, help,
// limits and "show if". Right-click: open its code (read-only, or a copy to edit), install, export, remove; a mechanism's
// menu has "Save as behavior…" (MainWindow.BehaviorDialogs.cs). Packages are never saved as mechanisms/*.bs.
public sealed partial class MainWindow
{
    /// <summary>The behavior packages this project can use, by name, in precedence order (project, installed, shipped).</summary>
    Dictionary<string, BehaviorPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The BEHAVIORS section's last row: imports a .behavior file (not a name a package can have).</summary>
    const string ImportBehaviorRow = "＋ Import .behavior…";
    /// <summary>--snap: "Import" and "Export" use this file instead of a file picker.</summary>
    string? _snapBehaviorFile;

    /// <summary>A mechanism that is a behavior package (its source is the package's script, not the project's own mechanism).</summary>
    bool IsPackage(string name) =>
        _packages.TryGetValue(name, out var p) && _sources.TryGetValue(name, out var src) && Normalize(src) == Normalize(p.Script);

    BehaviorPackage? PackageOf(string name) => IsPackage(name) ? _packages[name] : null;

    /// <summary>The project's own mechanism (new, or a starter one that was edited), packages not counted.</summary>
    bool IsOwnMechanism(string name) =>
        _sources.TryGetValue(name, out var src) && (!_starterSources.TryGetValue(name, out var kit) || Normalize(kit) != Normalize(src));

    /// <summary>
    /// After the project's mechanisms joined <c>_sources</c> (AdoptProjectMechanisms): the packages it can use join too,
    /// each as a mechanism named after it — but a mechanism of the project with the same name wins. The packages of the
    /// project shown before leave first (a starter mechanism they covered comes back).
    /// </summary>
    void AdoptPackages()
    {
        foreach (var (name, pkg) in _packages)
            if (_sources.TryGetValue(name, out var src) && Normalize(src) == Normalize(pkg.Script))
            {
                if (_starterSources.TryGetValue(name, out var kit)) _sources[name] = kit;
                else _sources.Remove(name);
            }
        var problems = new List<string>();
        List<BehaviorPackage> found;
        try { found = BehaviorPackage.DiscoverAll(_projectDir, problems); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { found = BehaviorPackage.Shipped(problems); problems.Add(e.Message); }
        _packages = new Dictionary<string, BehaviorPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in found)
        {
            _packages[p.Name] = p;
            if (!IsOwnMechanism(p.Name)) _sources[p.Name] = p.Script.Replace("\r\n", "\n");
        }
        _defCache.Clear();
        if (problems.Count > 0) Dispatcher.UIThread.Post(() => { foreach (var pr in problems) Log("⚠ Behavior package " + pr); });
    }

    /// <summary>Reads the packages again (after one was saved, imported, installed or removed) and shows them.</summary>
    void ReloadPackages()
    {
        AdoptPackages();
        RefreshLibrary();
        RebuildInspector();
    }

    /// <summary>An installed or shipped package used in this project is copied into its behaviors/ folder (so the game works anywhere).</summary>
    void EnsurePackageInProject(string name)
    {
        if (PackageOf(name) is not { Origin: not PackageOrigin.Project } pkg || _projectUnreadable) return;
        try
        {
            pkg.CopyToProject(_projectDir);
            ReloadPackages();
            Log($"Copied the behavior “{name}” into this project's {BehaviorPackage.ProjectFolderName} folder.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetStatus(false, $"Couldn't copy the behavior into the project: {e.Message}"); }
    }

    // ---------------- Library section ----------------

    static string OriginWord(PackageOrigin o) => o switch
    {
        PackageOrigin.Project => "project",
        PackageOrigin.Installed => "installed",
        _ => "shipped",
    };

    /// <summary>Adds the BEHAVIORS section to the library list: the project's, then installed, then shipped (MainWindow.Library.cs calls this).</summary>
    void LibraryBehaviorsSection(List<object> items)
    {
        items.Add(new LibItem("", LibSection.Behaviors, Header: "BEHAVIORS"));
        foreach (var origin in new[] { PackageOrigin.Project, PackageOrigin.Installed, PackageOrigin.Shipped })
            foreach (var p in _packages.Values.Where(p => p.Origin == origin).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                items.Add(new LibItem(p.Name, LibSection.Behaviors, Badge: IsPackage(p.Name) ? OriginWord(origin) : "off"));
        items.Add(new LibItem(ImportBehaviorRow, LibSection.Behaviors));
    }

    Control BehaviorLibraryRow(LibItem item)
    {
        if (item.Name == ImportBehaviorRow)
        {
            var link = new TextBlock { Text = ImportBehaviorRow, Foreground = Palette.Accent, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(link, "Add a behavior someone shared (a .behavior file) to this project");
            return link;
        }
        var row = new DockPanel { Background = Brushes.Transparent };
        var badge = new TextBlock { Text = item.Badge, FontSize = 10, Foreground = Palette.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(badge, Dock.Right);
        row.Children.Add(badge);
        if (!_packages.TryGetValue(item.Name, out var pkg)) return row;
        row.Children.Add(new TextBlock { Text = pkg.Icon + "  " + pkg.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        ToolTip.SetTip(row, PackageTip(pkg));
        row.ContextMenu = BehaviorContextMenu(pkg);
        return row;
    }

    string PackageTip(BehaviorPackage pkg)
    {
        var lines = new List<string> { $"{pkg.Icon} {pkg.Name} — {pkg.Category}", "", pkg.Description.Length > 0 ? pkg.Description : "(no description)" };
        lines.Add("");
        lines.Add(pkg.Origin switch
        {
            PackageOrigin.Project => "In this project's behaviors folder.",
            PackageOrigin.Installed => "Installed: every project (and EasyEngine) can use it.",
            _ => "Comes with BuilderEngine.",
        });
        if (pkg.Replaces != null) lines.Add($"Plays EasyEngine's “{System.Text.RegularExpressions.Regex.Replace(pkg.Replaces, "(?<=[a-z])(?=[A-Z])", " ")}” behavior.");
        if (pkg.Author.Length > 0) lines.Add($"By {pkg.Author}{(pkg.License.Length > 0 ? " (" + pkg.License + ")" : "")}, version {pkg.PackageVersion}.");
        if (!IsPackage(pkg.Name)) lines.Add($"Off: this project has its own mechanism called “{pkg.Name}”.");
        else lines.Add("Drag it onto the scene or onto an object.");
        return string.Join("\n", lines);
    }

    ContextMenu BehaviorContextMenu(BehaviorPackage pkg)
    {
        var menu = new ContextMenu();
        void Item(string text, Action run)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => run();
            menu.Items.Add(m);
        }
        Item("Open its code", () => _ = ShowPackageCodeAsync(pkg.Name));
        Item("Make a copy to edit", () => CopyPackageToEdit(pkg.Name));
        if (pkg.Origin != PackageOrigin.Installed) Item("Install (every project can use it)", () => InstallPackage(pkg.Name));
        Item("Export…", () => _ = ExportPackageAsync(pkg.Name));
        Item("Import .behavior…", () => _ = ImportBehaviorAsync());
        if (pkg.Origin == PackageOrigin.Project) Item("Remove from this project…", () => _ = RemovePackageAsync(pkg.Name));
        if (pkg.Origin == PackageOrigin.Installed) Item("Uninstall…", () => _ = RemovePackageAsync(pkg.Name));
        return menu;
    }

    void OnBehaviorRowClicked(LibItem item)
    {
        if (item.Name == ImportBehaviorRow) _ = ImportBehaviorAsync();
        else _ = ShowPackageCodeAsync(item.Name);
    }

    // ---------------- Actions ----------------

    void InstallPackage(string name)
    {
        if (!_packages.TryGetValue(name, out var pkg)) return;
        try
        {
            string path = pkg.Install();
            ReloadPackages();
            SetStatus(true, $"Installed “{name}”: every project (and EasyEngine) can use it now.");
            Log($"Installed the behavior “{name}” ({path}).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetStatus(false, $"Couldn't install “{name}”: {e.Message}"); }
    }

    /// <summary>A project mechanism with the package's code under a new name ("Spin2"), opened to edit; objects keep the package.</summary>
    void CopyPackageToEdit(string name)
    {
        if (!_packages.TryGetValue(name, out var pkg)) return;
        string copy = MechanismLibrary.UniqueName(name, n => _sources.ContainsKey(n) || _packages.ContainsKey(n));
        _sources[copy] = MechanismLibrary.RenameSource(pkg.Script, pkg.Name, copy).Replace("\r\n", "\n").TrimEnd() + "\n";
        RefreshLibrary();
        SceneOnMechanismsChanged();
        OpenMechanism(copy);
        SetStatus(true, $"“{copy}” is a copy of the behavior “{name}” in this project: edit it freely (objects with {name} keep the behavior).");
    }

    async Task RemovePackageAsync(string name)
    {
        if (!_packages.TryGetValue(name, out var pkg) || pkg.SourcePath == null) return;
        bool installed = pkg.Origin == PackageOrigin.Installed;
        var r = _snapMode ? "Remove" : await Dialogs.Ask(this, installed ? "Uninstall a behavior" : "Remove a behavior",
            installed ? $"Uninstall “{name}”? Projects that use it keep their own copy."
                      : $"Remove “{name}” from this project's behaviors folder? Objects that use it stop doing it (unless an installed or shipped one has the same name).",
            "Cancel", "Remove");
        if (r != "Remove") return;
        try
        {
            File.Delete(pkg.SourcePath);
            ReloadPackages();
            SetStatus(true, installed ? $"Uninstalled “{name}”." : $"Removed “{name}” from this project.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetStatus(false, $"Couldn't remove “{name}”: {e.Message}"); }
    }

    /// <summary>Picks a .behavior file, checks it and copies it into the project's behaviors/ (showing its problems if it can't be used).</summary>
    async Task ImportBehaviorAsync()
    {
        string? path = _snapBehaviorFile;
        if (path == null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import a behavior", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Behavior package (.behavior)") { Patterns = ["*" + BehaviorPackage.Extension] }]
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
        }
        if (path == null) return;
        var (pkg, problems) = BehaviorPackage.TryLoad(path);
        if (pkg == null || problems.Count > 0)
        {
            string msg = $"“{Path.GetFileName(path)}” can't be used:\n\n• " + string.Join("\n• ", problems);
            Log("⚠ Behavior package " + msg.Replace("\n\n• ", " ").Replace("\n• ", " "));
            if (!_snapMode) await Dialogs.Ask(this, "This behavior has problems", msg, "OK");
            SetStatus(false, $"{Path.GetFileName(path)} has {problems.Count} problem(s) — nothing was imported.");
            return;
        }
        string target = BehaviorPackage.FileIn(BehaviorPackage.ProjectFolder(_projectDir), pkg.Name);
        if (File.Exists(target) && !_snapMode
            && await Dialogs.Ask(this, "Already in this project", $"This project already has a behavior called “{pkg.Name}”. Replace it?", "Replace", "Cancel") != "Replace")
            return;
        try
        {
            pkg.CopyToProject(_projectDir);
            ReloadPackages();
            SetStatus(true, $"Imported “{pkg.Name}” — drag it from BEHAVIORS onto the scene or an object.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetStatus(false, $"Couldn't import: {e.Message}"); }
    }

    /// <summary>Saves a copy of a package anywhere (to share it).</summary>
    async Task ExportPackageAsync(string name)
    {
        if (!_packages.TryGetValue(name, out var pkg)) return;
        string? path = _snapBehaviorFile;
        if (path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Export the behavior “{name}”", SuggestedFileName = name + BehaviorPackage.Extension, DefaultExtension = BehaviorPackage.Extension.TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType("Behavior package (.behavior)") { Patterns = ["*" + BehaviorPackage.Extension] }]
            });
            path = file?.TryGetLocalPath();
        }
        if (path == null) return;
        try
        {
            var copy = pkg.Clone();
            copy.Save(path);
            SetStatus(true, $"Exported “{name}” to {path} — anyone can import it in BuilderEngine or EasyEngine.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetStatus(false, $"Couldn't export: {e.Message}"); }
    }

    // ---------------- Inspector ----------------

    /// <summary>The package fields with a "show if", on the inspector now: hidden while their condition is false.</summary>
    readonly List<(Control Row, BehaviorPackage Pkg, BehaviorPackage.PackageParam Param, SceneMechanism Mech)> _showIfRows = [];

    /// <summary>Shows or hides each package field by its "show if" over the card's current values (after every edit).</summary>
    void UpdateShowIf()
    {
        foreach (var (row, pkg, pp, m) in _showIfRows)
            row.IsVisible = pkg.Shows(pp, m.Params);
    }
}
