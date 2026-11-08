using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// EXPORT FEATURE: Project ▸ Export game… makes a folder anyone can play (GameExport.cs has the headless side): the
// player program (only the game window — src/BuilderEngine.Player) named after the game, and a copy of the project.
// The player comes from the prebuilt player/ folder of a downloaded BuilderEngine, or — running from the source code
// with the .NET SDK — from 'dotnet publish'. It all runs in the background with a progress window; the editor never freezes.
public sealed partial class MainWindow
{
    /// <summary>Where exported games go unless the person picked another folder: Documents\BuilderEngine Games.</summary>
    static string DefaultExportFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BuilderEngine Games");

    /// <summary>Where 'dotnet publish' builds the player on a computer with the source code (reused next time).</summary>
    static string PlayerBuildFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BuilderEngine", "player-build");

    /// <summary>The last export's progress window (the --snap check renders it).</summary>
    Window? _exportWindow;

    async Task ExportGameAsync()
    {
        if (_play != null) SceneStop();
        if (!TrySave()) return;
        var opts = await AskExportOptionsAsync();
        if (opts == null) return;
        _settings.Set("exportFolder", opts.OutputDir);
        await RunExportAsync(opts);
    }

    /// <summary>The export window: where to put the game, a zip or not, the window's icon. Null when cancelled.</summary>
    async Task<GameExport.Options?> AskExportOptionsAsync()
    {
        string folder = _settings.Get("exportFolder") is { Length: > 0 } f ? f : DefaultExportFolder;
        var dlg = new Window
        {
            Title = "Export game", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Palette.Bg1, Foreground = Palette.Text,
        };
        var root = new StackPanel { Margin = new Thickness(22, 18), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = $"Export “{_project.Name}”", FontSize = 15, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = $"Makes a folder with “{GameExport.SafeName(_project.Name)}.exe” and your game: anyone can play it on Windows, nothing to install. It starts with the first scene ({_project.Scenes[0].Name}).",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 13,
        });
        var path = new TextBox { Text = folder, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, MinHeight = 32, Padding = new Thickness(8, 6) };
        var choose = PlainButton("Choose…");
        choose.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where to put the game", AllowMultiple = false });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } p) path.Text = p;
        };
        var pathRow = new DockPanel();
        DockPanel.SetDock(choose, Dock.Right);
        choose.Margin = new Thickness(8, 0, 0, 0);
        pathRow.Children.Add(choose);
        pathRow.Children.Add(path);
        root.Children.Add(Field("Folder", pathRow));

        var icon = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text };
        icon.Items.Add("(the hammer)");
        foreach (var img in Images.List(_projectDir)) icon.Items.Add(img);
        icon.SelectedIndex = 0;
        ToolTip.SetTip(icon, "The picture shown on the game window's title bar and taskbar");
        root.Children.Add(Field("Window icon", icon));

        var zip = new CheckBox { Content = "Also make a .zip (to send or upload)", IsChecked = true, Foreground = Palette.Text };
        root.Children.Add(zip);

        GameExport.Options? result = null;
        var ok = AccentButton("Export");
        var cancel = PlainButton("Cancel");
        ok.Click += (_, _) =>
        {
            string outDir = (path.Text ?? "").Trim();
            if (outDir.Length == 0) return;
            result = new GameExport.Options(_projectDir, outDir, null, zip.IsChecked == true, icon.SelectedIndex > 0 ? icon.SelectedItem as string : null);
            dlg.Close();
        };
        cancel.Click += (_, _) => dlg.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dlg.Content = root;
        await dlg.ShowDialog(this);
        return result;
    }

    /// <summary>Finds or builds the player and exports, in the background, showing the steps. Returns the result (null on failure).</summary>
    async Task<GameExport.Result?> RunExportAsync(GameExport.Options opts, bool showWindow = true)
    {
        var status = new TextBlock { Text = "Getting ready…", FontSize = 14, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap };
        var log = new TextBlock { FontFamily = Palette.Mono, FontSize = 11, Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { IsIndeterminate = true, Height = 6, Foreground = Palette.Accent };
        var open = AccentButton("Show the game");
        var close = PlainButton("Close");
        open.IsEnabled = false;
        close.IsEnabled = false;
        var win = new Window
        {
            Title = "Exporting " + _project.Name, Width = 520, Height = 360, CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Palette.Bg1, Foreground = Palette.Text,
        };
        var root = new DockPanel { Margin = new Thickness(22, 18) };
        var top = new StackPanel { Spacing = 10 };
        top.Children.Add(status);
        top.Children.Add(bar);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(open);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var scroll = new ScrollViewer { Content = log, Margin = new Thickness(0, 12, 0, 0) };
        root.Children.Add(scroll);
        win.Content = root;
        close.Click += (_, _) => win.Close();
        _exportWindow = win;
        if (showWindow) win.Show(this);

        void Step(string line) => Dispatcher.UIThread.Post(() =>
        {
            status.Text = line;
            log.Text = (log.Text ?? "") + line + "\n";
            scroll.ScrollToEnd();
        });
        void Note(string line) => Dispatcher.UIThread.Post(() => { log.Text = (log.Text ?? "") + line + "\n"; scroll.ScrollToEnd(); });

        GameExport.Result? result = null;
        string? error = null;
        await Task.Run(() =>
        {
            try
            {
                string? player = opts.PlayerDir ?? GameExport.FindPrebuiltPlayer(AppContext.BaseDirectory);
                if (player == null && GameExport.FindPlayerProject(AppContext.BaseDirectory) is { } csproj)
                {
                    Step("Building the player with the .NET SDK (the first time takes a minute)…");
                    player = GameExport.PublishPlayer(csproj, PlayerBuildFolder, Note);
                }
                result = GameExport.Export(opts with { PlayerDir = player }, Step);
            }
            catch (Exception e) { error = e.Message; }
        });

        bar.IsIndeterminate = false;
        bar.Value = 100;
        close.IsEnabled = true;
        if (error != null || result == null)
        {
            status.Text = "Couldn't export: " + error;
            status.Foreground = Palette.Error;
            Log($"⚠ Export failed: {error}");
            return null;
        }
        foreach (var w in result.Warnings) Note("⚠ " + w);
        status.Text = result.Exe != null
            ? $"Done: {result.Exe}" + (result.Zip != null ? $"\nand {Path.GetFileName(result.Zip)}" : "")
            : "Exported the game, but without a player: " + string.Join(" ", result.Warnings);
        open.IsEnabled = true;
        open.Click += (_, _) => OpenFolder(result.Folder);
        Log($"📦 Exported {_project.Name} to {result.Folder}");
        SetStatus(result.Exe != null, result.Exe != null ? $"Exported {_project.Name} — double-click {Path.GetFileName(result.Exe)} to play." : "Exported without a player (see the export window).");
        return result;
    }
}
