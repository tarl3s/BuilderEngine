using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// PROJECTS FEATURE: the app-data folder (settings + My Library), the "Project" menu (new, open, recent, save),
// switching projects. `--appdata <folder>` (or the BUILDERENGINE_APPDATA variable) moves the app-data folder,
// so test and snap runs never touch the real %APPDATA%\BuilderEngine; --snap without it uses <snap folder>\appdata.
// A `--project <folder>` session never writes the settings (unless the app-data folder was moved).
public sealed partial class MainWindow
{
    AppSettings _settings = null!;
    MechanismLibrary _myLibrary = null!;
    IAudio _audio = null!;
    bool _appDataRedirected;
    /// <summary>--snap: no modal dialogs, no sound.</summary>
    bool _snapMode;
    readonly Button _projectButton = PlainButton("Project");

    /// <summary>The folder the start screen picked; the editor opens it (null = the last project).</summary>
    public static string? LaunchProject;

    static string? ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Before the project loads: where settings and My Library live, and the audio player.</summary>
    void ProjectsInit()
    {
        _snapMode = ArgValue("--snap") != null;
        _askWhenCloseCantSave = !_snapMode; // MainWindow.Scene.cs: closing when the save fails asks first
        string? dir = ArgValue("--appdata") ?? Environment.GetEnvironmentVariable("BUILDERENGINE_APPDATA");
        if (string.IsNullOrWhiteSpace(dir) && _snapMode) dir = Path.Combine(Path.GetFullPath(ArgValue("--snap")!), "appdata");
        _appDataRedirected = !string.IsNullOrWhiteSpace(dir);
        _settings = new AppSettings(_appDataRedirected ? Path.GetFullPath(dir!) : AppSettings.DefaultFolder,
            readOnly: _projectFromArg && !_appDataRedirected);
        _myLibrary = new MechanismLibrary(_settings.LibraryFolder);
        _audio = CreateAudio(); // FMOD or the classic player, by the Sound engine setting (MainWindow.Fmod.cs)
    }

    /// <summary>Where "New project" makes folders: Documents\BuilderEngine Projects (inside the app-data folder when it was moved).</summary>
    string ProjectsRoot => _appDataRedirected
        ? Path.Combine(_settings.Folder, "Projects")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BuilderEngine Projects");

    void RememberProject()
    {
        // Sample games are opened from the start screen's list and never go to Recent.
        if (!_settings.ReadOnly && !LauncherWindow.IsSample(_projectDir)) _settings.RememberProject(_projectDir);
    }

    void UpdateTitle()
    {
        Title = $"BuilderEngine — {_project.Name}{LinkTitle}"; // "(EasyEngine)", "🔗 Live link" (MainWindow.Link.cs)
        _projectButton.Content = $"{_project.Name}  ▾";
    }

    // ---------------- The menu ----------------

    Control ProjectMenuButton()
    {
        ToolTip.SetTip(_projectButton, "Project: new, open, recent, save");
        var flyout = new MenuFlyout();
        // An Avalonia MenuFlyout with no items never opens, so fill it now and refresh it each time it opens.
        FillProjectMenu(flyout);
        flyout.Opening += (_, _) => FillProjectMenu(flyout);
        _projectButton.Flyout = flyout;
        UpdateTitle();
        return _projectButton;
    }

    void FillProjectMenu(MenuFlyout f)
    {
        f.Items.Clear();
        MenuItem Item(string text, Action run, KeyGesture? key = null)
        {
            var m = new MenuItem { Header = text, InputGesture = key };
            m.Click += (_, _) => run();
            return m;
        }
        f.Items.Add(Item("New project…", () => _ = NewProjectAsync()));
        f.Items.Add(Item("Open…", () => _ = OpenProjectAsync(), new KeyGesture(Key.O, KeyModifiers.Control)));
        LinkMenuItems(f); // Open EasyEngine project…, Live link, Open in EasyEngine (MainWindow.Link.cs)
        var recent = new MenuItem { Header = "Recent" };
        foreach (var dir in _settings.RecentProjects().Where(d => !AppSettings.SameFolder(d, _projectDir)))
        {
            string d = dir;
            var m = Item($"{Path.GetFileName(d.TrimEnd('\\', '/'))}   —   {d}", () => _ = SwitchProjectAsync(d));
            m.IsEnabled = EasyLink.IsProjectFolder(d);
            if (EasyLink.IsEasyFolder(d)) m.Header += "   (EasyEngine)";
            recent.Items.Add(m);
        }
        if (recent.Items.Count == 0) recent.Items.Add(new MenuItem { Header = "(nothing yet)", IsEnabled = false });
        f.Items.Add(recent);
        f.Items.Add(new Separator());
        f.Items.Add(Item("Save", SceneSave, new KeyGesture(Key.S, KeyModifiers.Control)));
        f.Items.Add(Item("Import sound…", () => _ = ImportSoundsAsync()));
        f.Items.Add(Item("Sound engine…", ShowSoundEngineWindow)); // FMOD or classic, FMOD Studio (MainWindow.Fmod.cs)
        f.Items.Add(Item("Plugins…", ShowPluginsWindow)); // MainWindow.Plugins.cs
        f.Items.Add(Item("Export game…", () => _ = ExportGameAsync())); // MainWindow.Export.cs
        f.Items.Add(Item("Show the project folder", () => OpenFolder(_projectDir)));
        f.Items.Add(Item("Show My Library folder", () => { Directory.CreateDirectory(_myLibrary.Folder); OpenFolder(_myLibrary.Folder); }));
        if (!_projectFromArg)
        {
            f.Items.Add(new Separator());
            f.Items.Add(Item("Start screen…", BackToStartScreen));
        }
    }

    /// <summary>Saves, shows the start screen (new / open / recent / samples) and closes the editor.</summary>
    void BackToStartScreen()
    {
        SceneStop();
        // The save failed: ask first (MainWindow.Scene.cs), like closing the window does.
        if (!TrySave() && !_projectUnreadable && _askWhenCloseCantSave) { _ = AskLeaveUnsavedAsync(ShowStartScreenAndClose); return; }
        ShowStartScreenAndClose();
    }

    void ShowStartScreenAndClose()
    {
        _leaveWithoutSaving = true; // already saved (or the person chose to leave): closing doesn't save or ask again
        var launcher = new LauncherWindow();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = launcher;
        launcher.Show();
        Close();
    }

    static void OpenFolder(string dir)
    {
        try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no file manager */ }
    }

    // ---------------- New / open / switch ----------------

    async Task NewProjectAsync()
    {
        string suggestion = "My Game";
        for (int k = 2; Directory.Exists(Path.Combine(ProjectsRoot, suggestion)); k++) suggestion = $"My Game {k}";
        string? name = await Dialogs.Input(this, "New project", $"A name for your game. It is saved in {ProjectsRoot}.", suggestion, "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (CreateProject(name) is { } dir) await SwitchProjectAsync(dir);
    }

    /// <summary>Makes a new project folder (the demo scenes and the starter sounds) under <see cref="ProjectsRoot"/>. Returns it.</summary>
    string? CreateProject(string name)
    {
        name = name.Trim();
        string clean = new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        clean = clean.Trim().TrimEnd('.');
        if (clean.Length == 0) { SetStatus(false, "A project needs a name."); return null; }
        string dir = Path.Combine(ProjectsRoot, clean);
        for (int k = 2; Directory.Exists(dir); k++) dir = Path.Combine(ProjectsRoot, $"{clean} {k}");
        try
        {
            Project.CreateEmpty(name).Save(dir);
            Sounds.WriteDefaults(dir);
            return dir;
        }
        catch (Exception e)
        {
            SetStatus(false, $"Couldn't make the project: {e.Message}");
            return null;
        }
    }

    async Task OpenProjectAsync()
    {
        IStorageFolder? start = null;
        try { if (Directory.Exists(ProjectsRoot)) start = await StorageProvider.TryGetFolderFromPathAsync(ProjectsRoot); } catch { }
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a BuilderEngine project folder", AllowMultiple = false, SuggestedStartLocation = start
        });
        string? path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        if (!EasyLink.IsProjectFolder(path))
        {
            var r = await Dialogs.Ask(this, "No project in this folder",
                $"“{Path.GetFileName(path.TrimEnd('\\', '/'))}” has no BuilderEngine project in it (project.json). Make a new project there?",
                "Make one", "Cancel");
            if (r != "Make one") return;
        }
        await SwitchProjectAsync(path);
    }

    /// <summary>Saves the open project (asks when that fails), then opens another one.</summary>
    async Task SwitchProjectAsync(string dir)
    {
        if (AppSettings.SameFolder(dir, _projectDir)) { SetStatus(true, $"“{_project.Name}” is already open."); return; }
        SceneStop();
        if (!TrySave())
        {
            var r = _snapMode ? "Switch anyway" : await Dialogs.Ask(this, "Couldn't save",
                "This project couldn't be saved (see the console). Open the other one anyway? Changes since the last save will be lost.",
                "Cancel", "Switch anyway");
            if (r != "Switch anyway") return;
        }
        OpenProjectFolder(dir);
    }

    /// <summary>Opens a project folder (making the demo there when it has none). Doesn't save the current one.</summary>
    void OpenProjectFolder(string dir)
    {
        SceneStop();
        // Back to the starter kit as shipped, then the new project's own mechanisms on top.
        foreach (var name in _sources.Keys.ToList())
            if (!_starterSources.ContainsKey(name)) _sources.Remove(name);
        foreach (var (name, src) in _starterSources) _sources[name] = src;
        SetProjectPath(dir); // a folder or an .eeproj (MainWindow.Link.cs)
        LoadProject();

        _sceneIndex = 0;
        _sceneHistory.Clear();
        _undo.Clear();
        _redo.Clear();
        _canvas.Scene = _scene;
        _canvas.SelectedId = 0;
        _current = null;
        ShowTab(scene: true);
        RefreshLibrary();
        RefreshScenes();
        RefreshSounds();
        RebuildInspector();
        UpdateTitle();
        RememberProject();
        _canvas.InvalidateVisual();
        Dispatcher.UIThread.Post(() => _canvas.Fit(), DispatcherPriority.Loaded);
        Log($"Opened “{_project.Name}” ({_projectDir}).");
        SetStatus(true, $"Opened “{_project.Name}”.");
    }
}
