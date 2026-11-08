using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// The editor shell. Layout: top bar (tabs Scene / Mechanism, Save, Play), mechanism library on the left,
/// the scene or the mechanism editor (blocks and/or code) in the middle, inspector + console on the right.
/// Features live in partial files: MainWindow.Scene.cs (scene, inspector, play, project) and
/// MainWindow.Blocks.cs (blocks view). This file only owns the shell and the shared helpers below.
/// </summary>
public sealed partial class MainWindow : Window
{
    // ---- Shared state (used by every feature file) ----
    /// <summary>Mechanism name -> its Builder Script source. The starter kit and the project's own mechanisms.</summary>
    internal readonly Dictionary<string, string> _sources = new();
    /// <summary>The mechanism open in the Mechanism tab.</summary>
    internal string? _current;
    internal readonly ListBox _library = new();

    readonly TextBox _code = new();
    readonly TextBlock _status = new();
    readonly Ellipse _statusDot = new() { Width = 8, Height = 8 };
    readonly TextBlock _console = new();
    readonly ContentControl _center = new();
    readonly ContentControl _rightTop = new();
    readonly Grid _mechArea = new() { ColumnDefinitions = new ColumnDefinitions("*,*") };
    readonly Border _blocksBox = new();
    readonly Border _codeBox = new();
    readonly Button _tabScene = new(), _tabMech = new();
    readonly Button _modeBlocks = new(), _modeBoth = new(), _modeCode = new();
    Control? _sceneView, _inspector, _mechInfo, _mechPanel;
    Control _leftPanel = null!;
    /// <summary>Library | center | right column (the AI tab widens the right column).</summary>
    Grid? _bodyGrid;
    bool _loading;
    bool _sceneTab = true;
    string _mode = "both";

    public MainWindow()
    {
        Title = "BuilderEngine";
        Opacity = 0; // shown once the first frame is drawn (Program.cs), so no empty black box flashes first
        Avalonia.Threading.DispatcherTimer.RunOnce(() => Opacity = 1, TimeSpan.FromMilliseconds(1500)); // never stays hidden
        Width = 1360; Height = 840;
        MinWidth = 960; MinHeight = 600;
        Background = Palette.Bg0;
        Foreground = Palette.Text;

        foreach (var (_, src) in StarterKit.Sources())
            foreach (var chunk in SplitMechanisms(src))
                _sources[chunk.Name] = chunk.Source;
        SceneInit();

        var root = new DockPanel();
        var top = TopBar();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var status = StatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        var body = _bodyGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*,330") };
        var left = LibraryPanel();
        _mechPanel = MechanismPanel();
        _center.Content = null;
        var right = RightPanel();
        Grid.SetColumn(_center, 1);
        Grid.SetColumn(right, 2);
        body.Children.Add(left);
        body.Children.Add(_center);
        body.Children.Add(right);
        root.Children.Add(body);
        Content = root;

        _sceneView = CreateSceneView();
        _inspector = CreateInspector();
        _mechInfo = new TextBlock { Text = "Editing a mechanism. Switch to Scene to place it in the game.", TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, Margin = new Thickness(16) };
        ShowTab(scene: true);
        SetMode("both");
        Closing += (_, e) => SceneOnClosing(e);
        Closed += (_, _) => _audio.Dispose();
    }

    // ---------------- Shared helpers for feature files ----------------

    /// <summary>Opens a mechanism in the Mechanism tab (code + blocks).</summary>
    internal void OpenMechanism(string name)
    {
        if (!_sources.ContainsKey(name)) return;
        _current = name;
        _loading = true;
        _code.Text = _sources[name];
        _loading = false;
        SelectLibraryItem(name);
        Check();
        BlocksShow(_sources[name]);
        ShowTab(scene: false);
    }

    /// <summary>The blocks view changed the mechanism: replace its source (the code view follows).</summary>
    internal void ApplyCodeFromBlocks(string source)
    {
        if (_current == null) return;
        _loading = true;
        _code.Text = source;
        _loading = false;
        CommitSource(source, fromBlocks: true);
    }

    internal void ShowTab(bool scene)
    {
        _sceneTab = scene;
        _center.Content = scene ? _sceneView : _mechPanel;
        _rightTop.Content = scene || _play != null ? _inspector : _mechInfo; // while playing: the Debug panel on both tabs
        StyleTab(_tabScene, scene);
        StyleTab(_tabMech, !scene);
        _tabMech.Content = _current == null ? "Mechanism" : $"Mechanism: {_current}";
    }

    /// <summary>A fresh world with every mechanism loaded; prints and errors go to the console.</summary>
    internal World NewWorld()
    {
        var w = new World();
        w.Printed += Log;
        w.SoundPlayed += s => Log("♪ " + s);
        foreach (var (name, src) in _sources)
        {
            try { w.Load(src); }
            catch (ScriptError e) { Log($"⚠ {name}: {e}"); }
        }
        return w;
    }

    /// <summary>
    /// The console keeps about its last 40,000 characters. Every line copies and lays out the whole text again, so a game
    /// printing every frame used to make each frame slower than the one before (minutes later the editor crawled).
    /// </summary>
    const int ConsoleKeep = 40_000, ConsoleMax = 60_000;

    internal void Log(string line)
    {
        string text = (_console.Text ?? "") + line + "\n";
        if (text.Length > ConsoleMax)
        {
            int cut = text.IndexOf('\n', text.Length - ConsoleKeep);
            text = cut >= 0 ? text[(cut + 1)..] : text[^ConsoleKeep..];
        }
        _console.Text = text;
        AiOnLog(line);
    }

    internal void ClearLog()
    {
        _console.Text = "";
        AiOnLog(null);
    }

    // ---------------- Layout ----------------

    Control TopBar()
    {
        var logo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold, FontSize = 15, Margin = new Thickness(16, 0, 24, 0) };
        logo.Inlines!.Add(new Avalonia.Controls.Documents.Run("Builder"));
        logo.Inlines.Add(new Avalonia.Controls.Documents.Run("Engine") { Foreground = Palette.Accent });

        _tabScene.Content = "Scene";
        _tabScene.Click += (_, _) => ShowTab(true);
        _tabMech.Content = "Mechanism";
        _tabMech.Click += (_, _) => { if (_current != null) ShowTab(false); };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        tabs.Children.Add(_tabScene);
        tabs.Children.Add(_tabMech);

        var save = PlainButton("Save");
        ToolTip.SetTip(save, "Save the project (Ctrl+S)");
        save.Click += (_, _) => SceneSave();
        var stop = PlainButton("■  Stop");
        ToolTip.SetTip(stop, "Stop playing (Esc)");
        stop.Click += (_, _) => SceneStop();
        var play = AccentButton("▶  Play");
        ToolTip.SetTip(play, "Play the scene you are editing (F5)");
        var help = PlainButton("?");
        help.Padding = new Thickness(12, 7);
        ToolTip.SetTip(help, "Keyboard shortcuts (F1)");
        help.Click += (_, _) => ShowShortcuts();
        play.Click += (_, _) => { ShowTab(true); ClearLog(); ScenePlay(); };

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        right.Children.Add(help);
        right.Children.Add(save);
        right.Children.Add(stop);
        right.Children.Add(play);

        var bar = new DockPanel { Height = 52, Background = Palette.Bg1 };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        DockPanel.SetDock(logo, Dock.Left);
        bar.Children.Add(logo);
        var project = ProjectMenuButton();
        project.VerticalAlignment = VerticalAlignment.Center;
        project.Margin = new Thickness(0, 0, 16, 0);
        DockPanel.SetDock(project, Dock.Left);
        bar.Children.Add(project);
        bar.Children.Add(tabs);
        return WithBorder(bar, new Thickness(0, 0, 0, 1));
    }

    static void StyleTab(Button b, bool active)
    {
        b.Background = active ? Palette.AccentSoft : Brushes.Transparent;
        b.Foreground = active ? Palette.Text : Palette.TextDim;
        b.BorderBrush = active ? Palette.AccentBorder : Brushes.Transparent;
        b.BorderThickness = new Thickness(1);
        b.Padding = new Thickness(14, 7);
        b.CornerRadius = new CornerRadius(8);
    }

    Control StatusBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 0), VerticalAlignment = VerticalAlignment.Center };
        _statusDot.VerticalAlignment = VerticalAlignment.Center;
        _status.Foreground = Palette.TextDim;
        _status.FontSize = 12;
        row.Children.Add(_statusDot);
        row.Children.Add(_status);
        var bar = new Border { Height = 28, Background = Palette.Bg1, Child = row };
        return WithBorder(bar, new Thickness(0, 1, 0, 0));
    }

    Control LibraryPanel()
    {
        var header = new DockPanel { Margin = new Thickness(16, 16, 12, 8) };
        var add = PlainButton("+ New");
        add.Foreground = Palette.Accent;
        add.Click += (_, _) => NewMechanism();
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(Label("MECHANISMS"));

        var hint = new TextBlock { Text = "Click to edit. Drag onto the scene to use. Right-click to save one to My Library.", Foreground = Palette.TextDim, FontSize = 11, Margin = new Thickness(16, 0, 16, 8), TextWrapping = TextWrapping.Wrap };

        _library.Background = Brushes.Transparent;
        _library.Foreground = Palette.Text;
        _library.Margin = new Thickness(8, 0);
        // Selected/hovered items in the accent instead of Fluent's blue.
        void ItemStyle(Func<Avalonia.Styling.Selector?, Avalonia.Styling.Selector> sel, IBrush bg)
        {
            var st = new Avalonia.Styling.Style(x => sel(x).Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>());
            st.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, bg));
            st.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Palette.Text));
            _library.Styles.Add(st);
        }
        ItemStyle(x => x.OfType<ListBoxItem>().Class(":pointerover"), Palette.Bg2);
        ItemStyle(x => x.OfType<ListBoxItem>().Class(":selected"), Palette.AccentSoft);
        SetupLibraryList();
        RefreshLibrary();
        _library.SelectionChanged += (_, _) =>
        {
            if (_library.SelectedItem is LibItem { IsHeader: false, Section: LibSection.Project or LibSection.Starter } it && it.Name.Length > 0 && it.Name != _current) OpenMechanism(it.Name);
        };
        var sounds = SoundsPanel();

        var panel = new DockPanel { Background = Palette.Bg1 };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(hint);
        DockPanel.SetDock(sounds, Dock.Bottom);
        panel.Children.Add(sounds);
        panel.Children.Add(_library);
        _leftPanel = WithBorder(panel, new Thickness(0, 0, 1, 0));
        return _leftPanel;
    }

    Control MechanismPanel()
    {
        _code.AcceptsReturn = true;
        _code.AcceptsTab = true;
        _code.FontFamily = Palette.Mono;
        _code.FontSize = 14;
        _code.Background = Palette.Bg0;
        _code.Foreground = Palette.Text;
        _code.BorderThickness = new Thickness(0);
        _code.Padding = new Thickness(20, 16);
        _code.CaretBrush = Palette.Accent;
        _code.SelectionBrush = Palette.AccentSoft;
        _code.TextWrapping = TextWrapping.NoWrap;
        _code.TextChanged += (_, _) => { if (!_loading) OnCodeChanged(); };
        _code.AddHandler(KeyDownEvent, OnCodeKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ScrollViewer.SetHorizontalScrollBarVisibility(_code, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        _blocksBox.Child = CreateBlocksView();
        _blocksBox.Background = Palette.Bg0;
        _codeBox.Child = CodeWithGutter(_code); // line numbers + breakpoints (DebuggerGutter.cs)
        _codeBox.BorderBrush = Palette.Border;
        _codeBox.BorderThickness = new Thickness(1, 0, 0, 0);
        Grid.SetColumn(_codeBox, 1);
        _mechArea.Children.Add(_blocksBox);
        _mechArea.Children.Add(_codeBox);

        _modeBlocks.Content = "Blocks"; _modeBlocks.Click += (_, _) => SetMode("blocks");
        _modeBoth.Content = "Blocks + Code"; _modeBoth.Click += (_, _) => SetMode("both");
        _modeCode.Content = "Code"; _modeCode.Click += (_, _) => SetMode("code");
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(12, 8), HorizontalAlignment = HorizontalAlignment.Right };
        modes.Children.Add(_modeBlocks);
        modes.Children.Add(_modeBoth);
        modes.Children.Add(_modeCode);
        var toLibrary = PlainButton("Save to My Library");
        toLibrary.Padding = new Thickness(12, 5);
        toLibrary.Margin = new Thickness(12, 8);
        ToolTip.SetTip(toLibrary, "Keep a copy in your personal library, so every project can use it");
        toLibrary.Click += (_, _) => { if (_current != null) _ = SaveToLibraryAsync(_current); };
        var stripRow = new DockPanel();
        DockPanel.SetDock(modes, Dock.Right);
        stripRow.Children.Add(modes);
        DockPanel.SetDock(toLibrary, Dock.Left);
        stripRow.Children.Add(toLibrary);
        var askAi = AiAskButton();
        askAi.HorizontalAlignment = HorizontalAlignment.Left;
        stripRow.Children.Add(askAi);
        var strip = new Border { Background = Palette.Bg1, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = stripRow };

        var panel = new DockPanel();
        DockPanel.SetDock(strip, Dock.Top);
        panel.Children.Add(strip);
        panel.Children.Add(_mechArea);
        return panel;
    }

    void SetMode(string mode)
    {
        _mode = mode;
        _mechArea.ColumnDefinitions = new ColumnDefinitions(mode switch { "blocks" => "*,0", "code" => "0,*", _ => "*,*" });
        _blocksBox.IsVisible = mode != "code";
        _codeBox.IsVisible = mode != "blocks";
        StyleTab(_modeBlocks, mode == "blocks");
        StyleTab(_modeBoth, mode == "both");
        StyleTab(_modeCode, mode == "code");
    }

    Control RightPanel()
    {
        var panel = new Grid { RowDefinitions = new RowDefinitions("*,Auto,200"), Background = Palette.Bg1 };
        panel.Children.Add(AiRightTop(new ScrollViewer { Content = _rightTop }));
        var consoleTitle = AiConsoleHeader(Label("CONSOLE"));
        consoleTitle.Margin = new Thickness(16, 12, 16, 6);
        Grid.SetRow(consoleTitle, 1);
        panel.Children.Add(consoleTitle);
        _console.FontFamily = Palette.Mono;
        _console.FontSize = 12;
        _console.Foreground = Palette.TextDim;
        _console.TextWrapping = TextWrapping.Wrap;
        var sv = new ScrollViewer { Margin = new Thickness(16, 0, 16, 12), Content = _console };
        Grid.SetRow(sv, 2);
        panel.Children.Add(sv);
        return WithBorder(panel, new Thickness(1, 0, 0, 0));
    }

    // ---------------- Editing ----------------

    void NewMechanism()
    {
        int n = 1;
        while (_sources.ContainsKey($"MyMechanism{n}")) n++;
        string name = $"MyMechanism{n}";
        _sources[name] = $"mechanism {name}\n    param speed: number = 100\n\n    when start\n        print \"Hello from {name}!\"\n";
        RefreshLibrary();
        SceneOnMechanismsChanged();
        OpenMechanism(name);
    }

    void OnCodeChanged() => CommitSource(_code.Text ?? "", fromBlocks: false);

    void CommitSource(string text, bool fromBlocks)
    {
        if (_current == null) return;
        LinkTouched(); // live link: save soon (MainWindow.Link.cs)
        _sources[_current] = text;
        var parsed = Check();
        if (parsed != null && parsed.Count == 1 && parsed[0].Name != _current && !_sources.ContainsKey(parsed[0].Name))
        {
            // Renamed in the code: follow the new name in the library.
            string old = _current;
            _sources.Remove(old);
            _current = parsed[0].Name;
            _sources[_current] = text;
            RefreshLibrary();
            SelectLibraryItem(_current);
            SceneOnMechanismRenamed(old, _current);
            ShowTab(false);
        }
        if (!fromBlocks && parsed != null) BlocksShow(text);
        SceneOnMechanismsChanged();
    }

    /// <summary>Indent with 4 spaces; Enter keeps the indentation and adds a level after a block head.</summary>
    void OnCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            InsertAtCaret("    ");
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            string text = _code.Text ?? "";
            int caret = _code.CaretIndex;
            int lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
            if (caret == 0) lineStart = 0;
            string line = text[lineStart..caret];
            string indent = new(line.TakeWhile(c => c == ' ').ToArray());
            string trimmed = line.Trim();
            if (trimmed.StartsWith("mechanism") || trimmed.StartsWith("system ") || trimmed.StartsWith("when") || trimmed.StartsWith("if") || trimmed.StartsWith("else") || trimmed.StartsWith("repeat")
                || trimmed.StartsWith("to ") || trimmed.StartsWith("together") || trimmed.StartsWith("for each") || trimmed.StartsWith("while") || trimmed is "run" or "run:")
                if (!trimmed.Contains(':') || trimmed.EndsWith(':')) indent += "    ";
            InsertAtCaret("\n" + indent);
            e.Handled = true;
        }
    }

    void InsertAtCaret(string s)
    {
        string text = _code.Text ?? "";
        int caret = _code.CaretIndex;
        int selStart = Math.Min(_code.SelectionStart, _code.SelectionEnd), selEnd = Math.Max(_code.SelectionStart, _code.SelectionEnd);
        if (selEnd > selStart) { text = text.Remove(selStart, selEnd - selStart); caret = selStart; }
        _code.Text = text.Insert(caret, s);
        _code.CaretIndex = caret + s.Length;
    }

    List<MechanismDef>? Check()
    {
        try
        {
            var defs = Parser.Parse(_code.Text ?? "");
            SetStatus(true, defs.Count == 0 ? "Empty — start with 'mechanism Name'." : $"No problems. {Summary(defs)}");
            if (defs.Count > 0 && VariantProblem(defs[0]) is { } problem) SetStatus(false, problem); // MainWindow.Variants.cs
            return defs;
        }
        catch (ScriptError e)
        {
            SetStatus(false, e.ToString());
            return null;
        }
    }

    static string Summary(List<MechanismDef> defs)
    {
        var d = defs[0];
        return $"{Variants.Label(d)}: {d.Params.Count} param(s), {d.Uses.Count} used mechanism(s), {d.Whens.Count} 'when'(s).";
    }

    internal void SetStatus(bool ok, string text)
    {
        _status.Text = text;
        _status.Foreground = ok ? Palette.TextDim : Palette.Error;
        _statusDot.Fill = ok ? Palette.Ok : Palette.Error;
        AiOnStatus(ok, text);
    }

    // ---------------- Small UI helpers (shared) ----------------

    internal static string KeyName(Key k) => GameDraw.KeyName(k);

    /// <summary>Splits a file with several mechanisms (or systems) into one source per mechanism.</summary>
    internal static IEnumerable<(string Name, string Source)> SplitMechanisms(string src) => Systems.Split(src);

    internal static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Palette.TextDim, VerticalAlignment = VerticalAlignment.Center, LetterSpacing = 1 };

    internal static Button AccentButton(string text) => new()
    {
        Content = text, Background = Palette.Accent, Foreground = Palette.AccentText, FontWeight = FontWeight.SemiBold,
        Padding = new Thickness(18, 8), CornerRadius = new CornerRadius(8)
    };

    internal static Button PlainButton(string text) => new()
    {
        Content = text, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
        Padding = new Thickness(14, 7), CornerRadius = new CornerRadius(8)
    };

    internal static Border WithBorder(Control c, Thickness t) => new() { Child = c, BorderBrush = Palette.Border, BorderThickness = t };
}
