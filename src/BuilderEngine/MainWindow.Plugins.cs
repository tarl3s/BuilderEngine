using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// PLUGINS FEATURE: C# plugins in a project's plugins/ folder (Plugins.cs in BuilderEngine.Script). The first time a
// project with plugins opens, the editor asks before running them (remembered per project in the settings); once on,
// every game and tool world gets their commands. Project → Plugins… lists them, opens the folder and reloads.
public sealed partial class MainWindow
{
    PluginManager? _plugins;
    /// <summary>Turned on in this session (a --project session can't save the answer, so it is kept here too).</summary>
    readonly HashSet<string> _pluginsOnThisSession = new(StringComparer.OrdinalIgnoreCase);
    bool _pluginAskPending;
    Window? _pluginsWindow;

    const string PluginWarning = "Plugins run code on your computer — only use ones you trust.";

    bool PluginsAllowed => _pluginsOnThisSession.Contains(_projectDir) || PluginConsent.IsAllowed(_settings, _projectDir);

    /// <summary>A project opened (also at start-up): load its plugins, or ask once whether they may run.</summary>
    void PluginsOnProjectLoaded()
    {
        UnloadPlugins();
        if (!PluginManager.HasPlugins(_projectDir)) return;
        if (PluginsAllowed) { LoadPlugins(); return; }
        if (PluginConsent.WasAsked(_settings, _projectDir) || _snapMode)
        {
            Log("🔌 This project has plugins, but they are off. Project → Plugins… turns them on.");
            return;
        }
        if (IsVisible) Dispatcher.UIThread.Post(() => _ = AskPluginConsentAsync(), DispatcherPriority.Background);
        else if (!_pluginAskPending)
        {
            _pluginAskPending = true;
            Opened += AskOnOpen;
        }
    }

    void AskOnOpen(object? sender, EventArgs e)
    {
        Opened -= AskOnOpen;
        _pluginAskPending = false;
        Dispatcher.UIThread.Post(() => _ = AskPluginConsentAsync(), DispatcherPriority.Background);
    }

    async Task AskPluginConsentAsync()
    {
        if (!PluginManager.HasPlugins(_projectDir) || PluginsAllowed) return;
        string dir = _projectDir;
        var answer = await Dialogs.Ask(this, "This project has plugins", ConsentMessage(), "Turn plugins on", "Not now");
        if (!AppSettings.SameFolder(dir, _projectDir)) return; // another project opened meanwhile
        if (answer == "Turn plugins on") EnablePlugins(true);
        else if (answer == "Not now")
        {
            PluginConsent.Decline(_settings, dir);
            Log("🔌 Plugins stay off. Project → Plugins… turns them on.");
        }
    }

    string ConsentMessage()
    {
        var files = string.Join(", ", PluginManager.PluginFiles(_projectDir).Select(Path.GetFileName));
        return $"{PluginWarning}\n\nThe plugins folder has: {files}.\nTurn them on for “{_project.Name}”? (You can change this later in Project → Plugins…)";
    }

    void EnablePlugins(bool on)
    {
        if (on)
        {
            _pluginsOnThisSession.Add(_projectDir);
            PluginConsent.Allow(_settings, _projectDir);
            LoadPlugins();
        }
        else
        {
            _pluginsOnThisSession.Remove(_projectDir);
            PluginConsent.Decline(_settings, _projectDir);
            UnloadPlugins();
            Log("🔌 Plugins are off for this project.");
        }
    }

    void LoadPlugins()
    {
        UnloadPlugins();
        var pm = new PluginManager(PluginManager.FolderOf(_projectDir));
        pm.Logged += msg => Dispatcher.UIThread.Post(() => Log(msg));
        int n = pm.Load();
        _plugins = pm;
        PluginManager.Active = pm;
        var names = pm.Plugins.Select(p => p.Name).ToList();
        Log(n == 0 ? "🔌 No plugins loaded." : $"🔌 Plugins on: {string.Join(", ", names)} ({pm.AllCommands.Count()} command(s) and function(s)).");
        if (pm.Errors.Count > 0) SetStatus(false, $"A plugin had a problem: {pm.Errors[0]}");
    }

    void UnloadPlugins()
    {
        if (_plugins == null) return;
        if (PluginManager.Active == _plugins) PluginManager.Active = null;
        _plugins.Dispose();
        _plugins = null;
    }

    // ---------------- The Plugins window ----------------

    void ShowPluginsWindow()
    {
        if (_pluginsWindow != null) { _pluginsWindow.Activate(); return; }
        var w = BuildPluginsWindow();
        _pluginsWindow = w;
        w.Closed += (_, _) => _pluginsWindow = null;
        w.Show(this);
    }

    Window BuildPluginsWindow()
    {
        var w = new Window
        {
            Title = "Plugins", Width = 560, Height = 520, MinWidth = 420, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = Palette.Bg1, Foreground = Palette.Text,
        };
        var body = new StackPanel { Spacing = 10 };
        void Fill()
        {
            body.Children.Clear();
            FillPluginsPanel(body, Fill);
        }
        Fill();
        w.Content = new ScrollViewer { Content = new Border { Padding = new Thickness(22, 18), Child = body } };
        w.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) w.Close(); };
        return w;
    }

    void FillPluginsPanel(StackPanel body, Action refill)
    {
        string folder = PluginManager.FolderOf(_projectDir);
        bool on = PluginsAllowed;
        body.Children.Add(new TextBlock { Text = "Plugins", FontSize = 16, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = "C# plugins add commands and functions to Builder Script (for programmers). Put a plugin's .dll in this project's plugins folder, then Reload.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12.5
        });
        body.Children.Add(new TextBlock { Text = "⚠  " + PluginWarning, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Accent, FontSize = 12.5 });
        body.Children.Add(new TextBlock { Text = folder, FontFamily = Palette.Mono, FontSize = 11.5, Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap });

        var files = PluginManager.PluginFiles(_projectDir);
        string state = files.Count == 0 ? "No plugin files yet."
            : on ? $"On — {_plugins?.Plugins.Count ?? 0} plugin(s) loaded from {files.Count} file(s)."
            : $"Off — {files.Count} file(s) waiting: {string.Join(", ", files.Select(Path.GetFileName))}.";
        body.Children.Add(new TextBlock { Text = state, FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = on && files.Count > 0 ? Palette.Ok : Palette.Text, TextWrapping = TextWrapping.Wrap });

        if (on && _plugins != null)
        {
            foreach (var p in _plugins.Plugins) body.Children.Add(PluginCard(p));
            foreach (var err in _plugins.Errors.Where(e => !_plugins.Plugins.Any(p => p.Errors.Any(e.Contains))))
                body.Children.Add(new TextBlock { Text = "⚠  " + err, Foreground = Palette.Error, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Button Add(Button b, Action run)
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Click += (_, _) => { run(); refill(); };
            buttons.Children.Add(b);
            return b;
        }
        Add(PlainButton("Open plugins folder"), () => { Directory.CreateDirectory(folder); OpenFolder(folder); });
        if (on) Add(PlainButton("Reload"), LoadPlugins);
        if (files.Count > 0) Add(on ? PlainButton("Turn off") : AccentButton("Turn plugins on"), () => EnablePlugins(!on));
        body.Children.Add(buttons);
        body.Children.Add(new TextBlock
        {
            Text = "Make one: see docs/PLUGINS.md and the samples/HelloPlugin project (a 'confetti' command and a 'wave(t)' function).",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 11.5
        });
    }

    static Control PluginCard(LoadedPlugin p)
    {
        var card = new StackPanel { Spacing = 4 };
        var head = new TextBlock { FontSize = 13.5 };
        head.Inlines!.Add(new Avalonia.Controls.Documents.Run(p.Name) { Foreground = Palette.Accent, FontWeight = FontWeight.SemiBold });
        head.Inlines.Add(new Avalonia.Controls.Documents.Run("   " + p.Source) { Foreground = Palette.TextDim, FontSize = 11.5 });
        card.Children.Add(head);
        if (p.Description.Length > 0) card.Children.Add(new TextBlock { Text = p.Description, Foreground = Palette.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        foreach (var c in p.Commands)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var usage = new Avalonia.Controls.Documents.Span { FontFamily = Palette.Mono, Foreground = CodeColors.Text };
            CodeColors.AddRuns(usage.Inlines, c.Usage); // coloured like the code editor
            line.Inlines!.Add(usage);
            line.Inlines.Add(new Avalonia.Controls.Documents.Run((c.IsFunction ? "   function" : "   command") + (c.Help.Length > 0 ? " — " + c.Help : "")) { Foreground = Palette.TextDim });
            card.Children.Add(line);
        }
        if (p.Commands.Count == 0 && p.Errors.Count == 0) card.Children.Add(new TextBlock { Text = "It adds nothing.", Foreground = Palette.TextDim, FontSize = 12 });
        foreach (var e in p.Errors) card.Children.Add(new TextBlock { Text = "⚠  " + e, Foreground = Palette.Error, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Child = card, Background = Palette.Bg2, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8)
        };
    }
}
