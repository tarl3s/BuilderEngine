using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Path = System.IO.Path;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// The start screen (like EasyEngine's launcher): new project, open a folder, recent projects and the sample games.
/// Picking one opens the editor on it and closes this window.
/// </summary>
public sealed class LauncherWindow : Window
{
    readonly AppSettings _settings = new(AppSettings.DefaultFolder);
    readonly StackPanel _recent = new() { Spacing = 6 };

    static string ProjectsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BuilderEngine Projects");

    /// <summary>The sample games shown on the start screen: name in <see cref="SampleGames.All"/> and a one-line pitch.</summary>
    static readonly (string Name, string Pitch, string Color)[] Samples =
    [
        ("Deepdelve", "Side-view sandbox: dig, build, craft, survive the night.", "#C9893F"),
        ("Hollow Lantern", "Top-down cave survival in the dark, with a boss.", "#7FA7D9"),
        ("Night Shift", "Watch the cameras, shut the doors, last until 6 AM.", "#B05A6A"),
        ("Ember Duel", "Card battler: build a deck, beat Morrow.", "#D97757"),
    ];

    public LauncherWindow()
    {
        Title = "BuilderEngine";
        Width = 1000; Height = 720;
        MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Bg0;
        Foreground = Palette.Text;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*") };
        var left = LeftColumn();
        var right = RightColumn();
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = grid;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control) { _ = NewProjectAsync(); e.Handled = true; }
            if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control) { _ = OpenFolderAsync(); e.Handled = true; }
        };
        InstallSamplesOnce();
        FillRecent();
        Opened += async (_, _) => await CheckForUpdateAsync();
    }

    readonly TextBlock _versionText = new() { Foreground = Palette.TextDim, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

    /// <summary>
    /// Like EasyEngine: ask GitHub for the latest release; if it is newer, ask, download it and let a small script swap the
    /// files after BuilderEngine closes. A build run from the source code (the owner's bin\Debug) never updates this way.
    /// </summary>
    async Task CheckForUpdateAsync()
    {
        if (Updater.IsDevBuild || Environment.GetCommandLineArgs().Contains("--snap-launcher")) return;
        var r = await Updater.CheckAsync();
        if (r == null) return;
        string notes = r.Notes.Length > 600 ? r.Notes[..600] + "…" : r.Notes;
        var answer = await Dialogs.Ask(this, "A new version is ready",
            $"BuilderEngine v{r.Version.ToString(3)} is available (you have v{Updater.Current.ToString(3)}).\n\n{notes}\n\nUpdate now? BuilderEngine closes and opens again by itself.",
            "Update", "Later");
        if (answer != "Update") return;
        try
        {
            _versionText.Text = "Downloading the update…";
            var progress = new Progress<double>(p => _versionText.Text = $"Downloading the update… {Math.Round(p * 100)}%");
            var dir = await Updater.DownloadAsync(r, progress);
            Updater.StartSwap(dir, Updater.InstallDir, Environment.ProcessId);
            Close();
        }
        catch (Exception e)
        {
            _versionText.Text = "v" + Version();
            await Dialogs.Ask(this, "The update failed", e.Message, "OK");
        }
    }

    /// <summary>
    /// Like EasyEngine's samples: the first time the start screen opens, each sample game is made in
    /// Documents\BuilderEngine Projects and added to Recent. Settings remember which were installed
    /// ("samplesInstalled"), so one the person removes doesn't come back.
    /// </summary>
    void InstallSamplesOnce()
    {
        try
        {
            var installed = _settings.GetList("samplesInstalled");
            var recent = _settings.RecentProjects();
            bool changed = false;
            foreach (var (name, _, _) in Samples)
            {
                if (installed.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                string dir = Path.Combine(ProjectsRoot, name);
                try { if (!Project.Exists(dir)) SampleGames.Make(name, dir); }
                catch { continue; } // try again next time
                if (!recent.Any(r => AppSettings.SameFolder(r, dir))) recent.Add(dir);
                installed.Add(name);
                changed = true;
            }
            if (!changed) return;
            _settings.SetList("samplesInstalled", installed);
            _settings.SetList("recentProjects", recent.Take(AppSettings.MaxRecent));
        }
        catch { /* samples are a convenience */ }
    }

    // ---------------- Layout ----------------

    Control LeftColumn()
    {
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        logo.Children.Add(Logo());
        var name = new TextBlock { FontSize = 26, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        name.Inlines!.Add(new Avalonia.Controls.Documents.Run("Builder"));
        name.Inlines.Add(new Avalonia.Controls.Documents.Run("Engine") { Foreground = Palette.Accent });
        logo.Children.Add(name);

        var pitch = new TextBlock
        {
            Text = "Build your own mechanisms with blocks or code — every one you make makes the next game faster.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, Margin = new Thickness(0, 12, 0, 0)
        };
        var version = _versionText;
        version.Text = "v" + Version() + (Environment.GetCommandLineArgs().Contains("--updated") ? "  — updated ✓" : "");

        var newBtn = MainWindow.AccentButton("＋  New project");
        newBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        newBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(newBtn, "Ctrl+N");
        newBtn.Click += (_, _) => _ = NewProjectAsync();
        var openBtn = MainWindow.PlainButton("Open project folder…");
        openBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTip.SetTip(openBtn, "Ctrl+O");
        openBtn.Click += (_, _) => _ = OpenFolderAsync();

        var col = new StackPanel { Spacing = 10, Margin = new Thickness(32, 36, 28, 28) };
        col.Children.Add(logo);
        col.Children.Add(pitch);
        col.Children.Add(version);
        col.Children.Add(new Border { Height = 8 });
        col.Children.Add(newBtn);
        col.Children.Add(openBtn);
        col.Children.Add(new Border { Height = 6 });
        var samplesTitle = MainWindow.Label("SAMPLE GAMES");
        samplesTitle.Margin = new Thickness(0, 4, 0, 0);
        col.Children.Add(samplesTitle);
        foreach (var s in Samples) col.Children.Add(SampleCard(s.Name, s.Pitch, s.Color));

        return new Border
        {
            Background = Palette.Bg1, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer { Content = col }
        };
    }

    Control RightColumn()
    {
        var title = MainWindow.Label("RECENT PROJECTS");
        title.Margin = new Thickness(0, 0, 0, 10);
        var col = new DockPanel { Margin = new Thickness(28, 36, 28, 28) };
        DockPanel.SetDock(title, Dock.Top);
        col.Children.Add(title);
        col.Children.Add(new ScrollViewer { Content = _recent });
        return col;
    }

    static Control Logo()
    {
        // The hexagon of EasyEngine's launcher, in BuilderEngine's faded orange, with a block inside.
        var hex = new Polygon
        {
            Points = [new(20, 2), new(37, 11), new(37, 31), new(20, 40), new(3, 31), new(3, 11)],
            Stroke = Palette.Accent, StrokeThickness = 2.5, Fill = Palette.AccentSoft
        };
        var block = new Rectangle { Width = 12, Height = 12, Fill = Palette.Accent, RadiusX = 2, RadiusY = 2 };
        Canvas.SetLeft(block, 14); Canvas.SetTop(block, 15);
        var c = new Canvas { Width = 40, Height = 42 };
        c.Children.Add(hex);
        c.Children.Add(block);
        return c;
    }

    Control SampleCard(string name, string pitch, string color)
    {
        var dot = new Rectangle { Width = 10, Height = 34, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = pitch, FontSize = 12, Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel();
        DockPanel.SetDock(dot, Dock.Left);
        dot.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(dot);
        row.Children.Add(text);
        var b = new Button
        {
            Content = row, Background = Palette.Bg2, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ToolTip.SetTip(b, Directory.Exists(Path.Combine(ProjectsRoot, name)) ? "Open it" : "Make a copy in your projects and open it");
        b.Click += (_, _) => OpenSample(name);
        return b;
    }

    void FillRecent()
    {
        _recent.Children.Clear();
        var list = _settings.RecentProjects();
        if (list.Count == 0)
        {
            _recent.Children.Add(new TextBlock
            {
                Text = "No projects yet. Click “New project” to start, or open a sample game.",
                Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        foreach (var dir in list) _recent.Children.Add(RecentRow(dir));
    }

    Control RecentRow(string dir)
    {
        bool exists = Project.Exists(dir);
        string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        string when = "";
        try { if (exists) when = File.GetLastWriteTime(Path.Combine(dir, Project.FileName)).ToString("dd/MM/yyyy HH:mm"); } catch { }

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = exists ? Palette.Text : Palette.TextDim });
        text.Children.Add(new TextBlock
        {
            Text = exists ? dir : $"{dir}  — folder not found", FontSize = 12, Foreground = Palette.TextDim,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var remove = new Button
        {
            Content = "✕", Background = Brushes.Transparent, Foreground = Palette.TextDim, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(remove, "Remove from this list (the folder is kept)");
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            _settings.SetList("recentProjects", _settings.RecentProjects().Where(d => !AppSettings.SameFolder(d, dir)));
            FillRecent();
        };
        var whenText = new TextBlock { Text = when, FontSize = 12, Foreground = Palette.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };

        var row = new DockPanel();
        DockPanel.SetDock(remove, Dock.Right);
        DockPanel.SetDock(whenText, Dock.Right);
        row.Children.Add(remove);
        row.Children.Add(whenText);
        row.Children.Add(text);

        var b = new Button
        {
            Content = row, Background = Palette.Bg1, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = true
        };
        b.Click += (_, _) => { if (exists) OpenEditor(dir); };
        return b;
    }

    // ---------------- Actions ----------------

    async Task NewProjectAsync()
    {
        string suggestion = "My Game";
        for (int k = 2; Directory.Exists(Path.Combine(ProjectsRoot, suggestion)); k++) suggestion = $"My Game {k}";
        string? name = await Dialogs.Input(this, "New project", $"A name for your game. It is saved in {ProjectsRoot}.", suggestion, "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        string clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) return;
        string dir = Path.Combine(ProjectsRoot, clean);
        for (int k = 2; Directory.Exists(dir); k++) dir = Path.Combine(ProjectsRoot, $"{clean} {k}");
        try
        {
            Project.CreateDemo(name).Save(dir);
            Sounds.WriteDefaults(dir);
        }
        catch (Exception e)
        {
            await Dialogs.Ask(this, "Couldn't make the project", e.Message, "OK");
            return;
        }
        OpenEditor(dir);
    }

    async Task OpenFolderAsync()
    {
        IStorageFolder? start = null;
        try { if (Directory.Exists(ProjectsRoot)) start = await StorageProvider.TryGetFolderFromPathAsync(ProjectsRoot); } catch { }
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a BuilderEngine project folder", AllowMultiple = false, SuggestedStartLocation = start
        });
        string? path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        if (!Project.Exists(path))
        {
            var r = await Dialogs.Ask(this, "No project in this folder",
                $"“{Path.GetFileName(path.TrimEnd('\\', '/'))}” has no BuilderEngine project in it (project.json). Make a new project there?",
                "Make one", "Cancel");
            if (r != "Make one") return;
        }
        OpenEditor(path);
    }

    void OpenSample(string name)
    {
        string dir = Path.Combine(ProjectsRoot, name);
        if (!Project.Exists(dir))
        {
            try { SampleGames.Make(name, dir); }
            catch (Exception e) { _ = Dialogs.Ask(this, "Couldn't make the sample", e.Message, "OK"); return; }
        }
        OpenEditor(dir);
    }

    /// <summary>Opens the editor on that folder and closes the start screen.</summary>
    void OpenEditor(string dir)
    {
        MainWindow.LaunchProject = Path.GetFullPath(dir);
        var editor = new MainWindow();
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = editor;
        editor.Show();
        Close();
    }

    static string Version()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
