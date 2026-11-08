using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine.Player;

/// <summary>
/// The game window of an exported game: the title is the game's name, the icon its own picture (game/icon.png) or the
/// hammer. It plays the first scene with the editor's own PlaySurface (goto_scene switches scenes, the score carries
/// over), F11 toggles full screen and Esc opens a small menu (Resume / Full screen / Quit).
/// </summary>
sealed class PlayerWindow : Window
{
    readonly string? _dir;
    readonly Project? _project;
    readonly IAudio _audio;
    readonly Grid _root = new();
    readonly Border _menu;
    readonly TextBlock _menuNote = new() { Foreground = Palette.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Center };
    PlaySurface? _play;
    readonly HashSet<string> _missingSounds = new(StringComparer.OrdinalIgnoreCase);

    public PlayerWindow(string? gameDir, bool mute, string? snapPng, bool snapMenu = false)
    {
        _dir = gameDir;
        _audio = Audio.Create(mute);
        Background = Palette.Bg0;
        Width = 1280; Height = 720;
        MinWidth = 480; MinHeight = 270;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Game";
        SetIcon();

        _menu = MenuOverlay();
        _menu.IsVisible = false;
        Content = _root;

        try
        {
            if (gameDir == null || !Project.Exists(gameDir)) throw new IOException("This game's files are missing: the 'game' folder should be next to the program.");
            _project = Project.Load(gameDir);
            Title = _project.Name;
            if (_project.Scenes.Count == 0) throw new IOException("This game has no scenes.");
        }
        catch (Exception e)
        {
            _root.Children.Add(new TextBlock { Text = e.Message, Foreground = Palette.Text, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        }

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            if (_project != null) StartScene(NewWorld(), _project.Scenes[0]);
            if (snapPng != null) _ = SnapAndClose(snapPng, snapMenu);
        };
        Closed += (_, _) => { _play?.Halt(); _audio.Dispose(); };
    }

    void SetIcon()
    {
        try
        {
            string? own = _dir == null ? null : Path.Combine(_dir, GameExport.IconFile);
            Icon = own != null && File.Exists(own)
                ? new WindowIcon(own)
                : new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://BuilderEngine.Player/Assets/BuilderEngine.ico")));
        }
        catch { /* no icon is fine */ }
    }

    World NewWorld()
    {
        var w = GameBoot.NewWorld(_project!, _dir!, GameBoot.SaveFolderFor(_project!.Name));
        w.SoundPlayed += name =>
        {
            if (Sounds.Resolve(_dir!, name) is { } path) _audio.Play(path);
        };
        w.SoundControl += r =>
        {
            if (r.VolumeOnly) { _audio.SetVolume(r.Name, r.Volume); return; }
            if (r.Stop) { _audio.StopLoop(r.Name); return; }
            if (Sounds.Resolve(_dir!, r.Name) is { } path) _audio.Loop(r.Name, path, r.Volume);
            else _missingSounds.Add(r.Name);
        };
        w.SceneRequested += name => Dispatcher.UIThread.Post(() => SwitchScene(name));
        return w;
    }

    void StartScene(World world, Scene scene)
    {
        var spawned = world.LoadScene(scene.Clone());
        _play?.Halt();
        _play = new PlaySurface(world, scene, spawned)
        {
            StopRequested = () => ShowMenu(true),
            Crashed = msg => { _menuNote.Text = "The game stopped: " + msg; ShowMenu(true); },
        };
        _root.Children.Clear();
        _root.Children.Add(_play);
        _root.Children.Add(_menu);
        var surface = _play;
        Dispatcher.UIThread.Post(() => { if (_play == surface) surface.Start(); }, DispatcherPriority.Loaded);
    }

    void SwitchScene(string name)
    {
        if (_play == null || _project?.FindScene(name) is not { } target) return;
        StartScene(_play.World.ForNextScene(), target);
    }

    // ---------------- Esc menu, full screen ----------------

    Border MenuOverlay()
    {
        Button Item(string text, Action run)
        {
            var b = new Button
            {
                Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 9), CornerRadius = new CornerRadius(8), FontSize = 15,
            };
            b.Click += (_, _) => run();
            return b;
        }
        var resume = Item("Resume", () => ShowMenu(false));
        resume.Background = Palette.Accent;
        resume.Foreground = Palette.AccentText;
        var stack = new StackPanel { Spacing = 10, Width = 260 };
        stack.Children.Add(new TextBlock { Text = "Paused", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(resume);
        stack.Children.Add(Item("Full screen (F11)", ToggleFullScreen));
        stack.Children.Add(Item("Quit", Close));
        stack.Children.Add(_menuNote);
        var card = new Border
        {
            Child = stack, Background = Palette.Bg1, BorderBrush = Palette.AccentBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Child = card };
    }

    void ShowMenu(bool show)
    {
        _menu.IsVisible = show;
        if (_play == null) return;
        if (show) { _play.Halt(); _audio.SetVolume("", 0.3); }
        else { _menuNote.Text = ""; _audio.SetVolume("", 1); _play.Start(); }
    }

    void ToggleFullScreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _menu.IsVisible) { ShowMenu(false); e.Handled = true; }
    }

    async Task SnapAndClose(string png, bool menu)
    {
        await Task.Delay(1500);
        if (menu) { ShowMenu(true); await Task.Delay(300); }
        try
        {
            var size = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            bmp.Render(this);
            bmp.Save(png);
        }
        catch { }
        Close();
    }
}
