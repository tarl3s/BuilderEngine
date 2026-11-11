using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// OPTIONAL FMOD AUDIO (Script/FmodCore.cs, FmodStudio.cs, FmodEventHost.cs): FMOD isn't shipped (its own licence); when
// fmod.dll is found the editor's sounds play through FMOD (FmodAudio.cs), otherwise through Windows' classic player.
//   Project ▸ Sound engine…  the audioBackend setting (auto / FMOD / classic), what plays now, FMOD Studio: open it,
//                            reload the banks, how many banks and events this project has
//   Play                     one FmodEventHost per game: 'play event' & co. (World.FmodEvents.cs) reach FMOD Studio
//   Inspector                a FmodEvent param picks one of the project's events (FmodEventPicker)
public sealed partial class MainWindow
{
    FmodEventHost? _fmodHost;
    DispatcherTimer? _fmodTimer;
    AudioBackend _audioBackendInUse;

    bool AudioMuted => _snapMode || Environment.GetCommandLineArgs().Contains("--mute");

    /// <summary>The audio player for the chosen sound engine (MainWindow.Projects.cs at start; again when the choice changes).</summary>
    IAudio CreateAudio()
    {
        FmodLibrary.Log ??= m => Dispatcher.UIThread.Post(() => Log("🔊 " + m)); // "FMOD Studio isn't installed"… once, in the console
        _audioBackendInUse = _settings.GetAudioBackend();
        return Audio.Create(AudioMuted, _audioBackendInUse);
    }

    /// <summary>A new choice in Sound engine…: the audio is made again now, or when the playing game stops.</summary>
    void ApplyAudioBackend()
    {
        if (_play != null || _settings.GetAudioBackend() == _audioBackendInUse) return;
        _audio.Dispose();
        _audio = CreateAudio();
    }

    // ---------------- FMOD Studio events while playing ----------------

    /// <summary>Play: the game's FMOD Studio events go to a new host (the next scenes keep it — World.ForNextScene).</summary>
    void FmodGameStart(World world)
    {
        FmodGameStop();
        ApplyAudioBackend();
        FmodLibrary.ResetLog(); // a new game console shows FMOD messages again
        _fmodHost = new FmodEventHost(_projectDir, silent: AudioMuted);
        world.FmodControl += _fmodHost.Handle;
        var host = _fmodHost;
        _fmodTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => host.Update());
        _fmodTimer.Start();
    }

    void FmodGameStop()
    {
        _fmodTimer?.Stop();
        _fmodTimer = null;
        _fmodHost?.Dispose();
        _fmodHost = null;
        if (_play == null) Dispatcher.UIThread.Post(ApplyAudioBackend); // a choice made while playing
    }

    // ---------------- Project ▸ Sound engine… ----------------

    /// <summary>The project's FMOD Studio banks: "2 bank(s), 14 event(s)", or why there are none.</summary>
    string FmodBanksLine()
    {
        var events = FmodStudio.ListEvents(_projectDir, out var msg);
        return msg.Length > 0 ? msg : $"{events.Count} event(s)";
    }

    internal Window SoundEngineWindow()
    {
        var dlg = new Window
        {
            Title = "Sound engine", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Palette.Bg1, Foreground = Palette.Text,
        };
        var root = new StackPanel { Margin = new Thickness(22, 18), Spacing = 10 };
        root.Children.Add(new TextBlock { Text = "Sound engine", FontSize = 15, FontWeight = FontWeight.SemiBold });
        root.Children.Add(Note("BuilderEngine plays sounds with Windows' own player. With FMOD installed (free for small games at fmod.com, " +
            "its own licence) it can use FMOD instead: more sounds at once, ogg and flac, and FMOD Studio events.", Palette.TextDim));

        var status = new TextBlock { Text = Audio.StatusLine, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var pending = Note("", Palette.TextDim);
        void ShowStatus()
        {
            status.Text = Audio.StatusLine;
            pending.Text = _settings.GetAudioBackend() == _audioBackendInUse ? "" : "The new choice applies when the game stops.";
            pending.IsVisible = pending.Text.Length > 0;
        }
        var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border };
        foreach (var (_, label) in FmodSettings.Choices) choice.Items.Add(label);
        choice.SelectedIndex = Math.Max(0, Array.FindIndex(FmodSettings.Choices, c => c.Value == _settings.GetAudioBackend()));
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex < 0) return;
            _settings.SetAudioBackend(FmodSettings.Choices[choice.SelectedIndex].Value);
            ApplyAudioBackend();
            ShowStatus();
        };
        root.Children.Add(Field("Use", choice));
        root.Children.Add(status);
        root.Children.Add(pending);
        ShowStatus();

        // FMOD Studio: adaptive music and sound events, made in FMOD Studio and built into this project's fmod folder.
        root.Children.Add(new TextBlock { Text = "FMOD Studio", FontSize = 13.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        string fmodDir = Path.Combine(_projectDir, FmodStudio.FolderName);
        root.Children.Add(Note($"Make your events in FMOD Studio, set its built banks folder (Edit ▸ Preferences ▸ Build) to this project's “fmod” folder " +
            $"({fmodDir}), then File ▸ Build. Pick the events in the FmodEmitter mechanism or use play event \"event:/…\" in code.", Palette.TextDim));
        var banks = new TextBlock { Text = FmodBanksLine(), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(banks);

        var buttons = new WrapPanel();
        string? studioApp = FmodLibrary.FindStudioApp();
        var open = PlainButton("Open FMOD Studio");
        open.IsEnabled = studioApp != null;
        ToolTip.SetTip(open, studioApp ?? "FMOD Studio isn't installed (free download at fmod.com).");
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = studioApp!, UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { SetStatus(false, "Couldn't open FMOD Studio: " + e.Message); }
        };
        var reload = PlainButton("Reload banks");
        ToolTip.SetTip(reload, "After a new Build in FMOD Studio: read the banks again (a playing game restarts its events)");
        reload.Click += (_, _) =>
        {
            FmodStudio.ClearCache();
            _fmodHost?.Reload();
            banks.Text = FmodBanksLine();
        };
        var folder = PlainButton("Show the fmod folder");
        folder.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(fmodDir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            OpenFolder(fmodDir);
        };
        foreach (var b in new[] { open, reload, folder }) { b.Margin = new Thickness(0, 0, 8, 6); buttons.Children.Add(b); }
        root.Children.Add(buttons);
        if (studioApp == null) root.Children.Add(Note("FMOD Studio isn't installed on this computer.", Palette.TextDim));

        var close = AccentButton("Close");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => dlg.Close();
        root.Children.Add(close);
        dlg.Content = root;
        return dlg;
    }

    void ShowSoundEngineWindow()
    {
        var dlg = SoundEngineWindow();
        if (_snapMode) return;
        _ = dlg.ShowDialog(this);
    }

    // ---------------- Export game… ----------------

    /// <summary>"Include FMOD" in the export window: off by default; disabled (with why) when FMOD isn't on this computer.</summary>
    CheckBox ExportFmodOption()
    {
        bool found = FmodSettings.ExportFiles().Count > 0;
        var box = new CheckBox
        {
            Content = "Include FMOD (you must follow FMOD's licence)", IsChecked = false, IsEnabled = found, Foreground = Palette.Text,
        };
        ToolTip.SetTip(box, found
            ? "Copies fmod.dll (and fmodstudio.dll) next to the game, so it plays with FMOD and its FMOD Studio events everywhere. FMOD is free for small games under its own licence (fmod.com/licensing)."
            : "FMOD isn't installed on this computer: the game uses the classic sound.");
        return box;
    }

    // ---------------- Inspector: a FmodEvent param ----------------

    const string NoEvent = "(none)";

    /// <summary>A dropdown of the project's FMOD Studio events (their paths); when there are none, why (not installed, no banks).</summary>
    Control FmodEventPicker(string value, Action<string> set)
    {
        var events = FmodStudio.ListEvents(_projectDir, out var msg);
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
            FontSize = 13, MinWidth = 0
        };
        var items = new List<string> { NoEvent };
        string norm = FmodStudio.NormalizePath(value);
        string? match = events.FirstOrDefault(e => string.Equals(e.Path, norm, StringComparison.OrdinalIgnoreCase))?.Path;
        if (match == null && value.Length > 0) items.Add($"{value}  (missing)");
        items.AddRange(events.Select(e => e.Path));
        _inspectorSync = true;
        combo.ItemsSource = items;
        combo.SelectedItem = match ?? (value.Length > 0 ? items[1] : NoEvent);
        _inspectorSync = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (_inspectorSync || combo.SelectedItem is not string s || s.EndsWith("  (missing)")) return;
            set(s == NoEvent ? "" : s);
        };
        if (events.Count > 0) return combo;
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(combo);
        stack.Children.Add(Note(msg.Length > 0 ? msg : "No events in this project's FMOD banks.", Palette.TextDim));
        return stack;
    }
}
