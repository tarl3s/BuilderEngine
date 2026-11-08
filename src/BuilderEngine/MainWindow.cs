using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// First editor shell: mechanism library on the left, the Builder Script editor in the middle (checked as you type),
/// and a live preview on the right that plays a tiny test scene with the mechanism.
/// </summary>
public sealed class MainWindow : Window
{
    readonly Dictionary<string, string> _sources = new();   // mechanism name -> its source
    readonly ListBox _library = new();
    readonly TextBox _code = new();
    readonly TextBlock _status = new();
    readonly Ellipse _statusDot = new() { Width = 8, Height = 8 };
    readonly Canvas _stage = new() { Background = Brushes.Transparent, ClipToBounds = true };
    readonly TextBlock _console = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    World? _world;
    string? _current;
    bool _loading;

    public MainWindow()
    {
        Title = "BuilderEngine";
        Width = 1280; Height = 800;
        MinWidth = 900; MinHeight = 560;
        Background = Palette.Bg0;
        Foreground = Palette.Text;

        foreach (var (_, src) in StarterKit.Sources())
            foreach (var chunk in SplitMechanisms(src))
                _sources[chunk.Name] = chunk.Source;

        var root = new DockPanel();
        var top = TopBar();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var status = StatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,380") };
        var left = LibraryPanel();
        var center = CodePanel();
        var right = PreviewPanel();
        Grid.SetColumn(center, 1);
        Grid.SetColumn(right, 2);
        body.Children.Add(left);
        body.Children.Add(center);
        body.Children.Add(right);
        root.Children.Add(body);
        Content = root;

        _timer.Tick += (_, _) => StepGame();
        KeyDown += (_, e) => { if (_world != null && !_code.IsFocused) { _world.KeysDown.Add(KeyName(e.Key)); _world.PressKey(KeyName(e.Key)); } };
        KeyUp += (_, e) => _world?.KeysDown.Remove(KeyName(e.Key));

        _library.SelectedIndex = _library.ItemCount > 0 ? 0 : -1;
    }

    // ---------------- Layout ----------------

    Control TopBar()
    {
        var logo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold, FontSize = 15 };
        logo.Inlines!.Add(new Avalonia.Controls.Documents.Run("Builder"));
        logo.Inlines.Add(new Avalonia.Controls.Documents.Run("Engine") { Foreground = Palette.Accent });

        var play = AccentButton("▶  Play");
        play.Click += (_, _) => StartPreview();
        var stop = PlainButton("■  Stop");
        stop.Click += (_, _) => StopPreview();

        var bar = new DockPanel { Height = 52, Background = Palette.Bg1 };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        right.Children.Add(stop);
        right.Children.Add(play);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        logo.Margin = new Thickness(16, 0, 0, 0);
        bar.Children.Add(logo);
        return WithBorder(bar, new Thickness(0, 0, 0, 1));
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

        _library.Background = Brushes.Transparent;
        _library.Foreground = Palette.Text;
        _library.Margin = new Thickness(8, 0);
        RefreshLibrary();
        _library.SelectionChanged += (_, _) => { if (_library.SelectedItem is string s) Open(s); };

        var panel = new DockPanel { Background = Palette.Bg1 };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(_library);
        return WithBorder(panel, new Thickness(0, 0, 1, 0));
    }

    Control CodePanel()
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
        return _code;
    }

    Control PreviewPanel()
    {
        var panel = new DockPanel { Background = Palette.Bg1 };
        var title = Label("PREVIEW");
        title.Margin = new Thickness(16, 16, 16, 8);
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);

        var hint = new TextBlock { Text = "Play runs a test scene: a player (arrow keys) and three copies of this mechanism.", TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12, Margin = new Thickness(16, 0, 16, 8) };
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(hint);

        var stageBox = new Border { Height = 240, Margin = new Thickness(16, 0), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.Parse("#1A1410")), BorderBrush = Palette.Border, BorderThickness = new Thickness(1), Child = _stage, ClipToBounds = true };
        DockPanel.SetDock(stageBox, Dock.Top);
        panel.Children.Add(stageBox);

        var consoleTitle = Label("CONSOLE");
        consoleTitle.Margin = new Thickness(16, 20, 16, 8);
        DockPanel.SetDock(consoleTitle, Dock.Top);
        panel.Children.Add(consoleTitle);

        _console.FontFamily = Palette.Mono;
        _console.FontSize = 12;
        _console.Foreground = Palette.TextDim;
        _console.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(new ScrollViewer { Margin = new Thickness(16, 0, 16, 16), Content = _console });
        return WithBorder(panel, new Thickness(1, 0, 0, 0));
    }

    // ---------------- Library and editing ----------------

    void RefreshLibrary()
    {
        var sel = _library.SelectedItem as string;
        _library.ItemsSource = _sources.Keys.OrderBy(k => k).ToList();
        if (sel != null) _library.SelectedItem = sel;
    }

    void Open(string name)
    {
        _current = name;
        _loading = true;
        _code.Text = _sources[name];
        _loading = false;
        Check();
    }

    void NewMechanism()
    {
        int n = 1;
        while (_sources.ContainsKey($"MyMechanism{n}")) n++;
        string name = $"MyMechanism{n}";
        _sources[name] = $"mechanism {name}\n    param speed: number = 100\n\n    when start\n        print \"Hello from {name}!\"\n";
        RefreshLibrary();
        _library.SelectedItem = name;
    }

    void OnCodeChanged()
    {
        if (_current == null) return;
        var text = _code.Text ?? "";
        _sources[_current] = text;
        var parsed = Check();
        // Renamed in the code? Follow the new name in the library.
        if (parsed != null && parsed.Count == 1 && parsed[0].Name != _current && !_sources.ContainsKey(parsed[0].Name))
        {
            _sources.Remove(_current);
            _current = parsed[0].Name;
            _sources[_current] = text;
            RefreshLibrary();
            _library.SelectedItem = _current;
        }
    }

    /// <summary>Indent with 4 spaces; Enter keeps the indentation and adds a level after a line ending in a block head.</summary>
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
            string line = text[lineStart..caret];
            string indent = new(line.TakeWhile(c => c == ' ').ToArray());
            string trimmed = line.Trim();
            if (trimmed.StartsWith("mechanism") || trimmed.StartsWith("when") || trimmed.StartsWith("if") || trimmed.StartsWith("else") || trimmed.StartsWith("repeat"))
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
        return $"{d.Name}: {d.Params.Count} param(s), {d.Uses.Count} used mechanism(s), {d.Whens.Count} 'when'(s).";
    }

    void SetStatus(bool ok, string text)
    {
        _status.Text = text;
        _status.Foreground = ok ? Palette.TextDim : Palette.Error;
        _statusDot.Fill = ok ? Palette.Ok : Palette.Error;
    }

    // ---------------- Preview ----------------

    void StartPreview()
    {
        StopPreview();
        if (_current == null) return;
        var w = new World();
        _console.Text = "";
        w.Printed += Log;
        w.SoundPlayed += s => Log("♪ " + s);
        try
        {
            foreach (var src in _sources.Values) w.Load(src);
            w.Globals["score"] = 0.0;
            var player = w.Spawn("Player", 40, 100, ["PlayerMove"], null, "player");
            player.Width = 24; player.Height = 32;
            for (int i = 0; i < 3; i++)
            {
                var o = w.Spawn(_current, 130 + i * 70, 100, [_current]);
                o.Width = 20; o.Height = 20;
            }
        }
        catch (ScriptError e)
        {
            Log("⚠ " + e);
            return;
        }
        _world = w;
        Log($"Playing '{_current}'. Use the arrow keys (click the preview first).");
        _stage.Focusable = true;
        _stage.Focus();
        _timer.Start();
    }

    void StopPreview()
    {
        _timer.Stop();
        _world = null;
        _stage.Children.Clear();
    }

    void StepGame()
    {
        if (_world == null) return;
        _world.Step(1.0 / 60);
        _stage.Children.Clear();
        foreach (var o in _world.Objects)
        {
            bool isPlayer = o.Is("player");
            var shape = new Rectangle
            {
                Width = o.Width, Height = o.Height, RadiusX = isPlayer ? 4 : o.Width / 2, RadiusY = isPlayer ? 4 : o.Height / 2,
                Fill = isPlayer ? new SolidColorBrush(Color.Parse("#7D9CBB")) : Palette.Accent
            };
            if (o.AllMechanisms().Any(m => m.Vars.TryGetValue("visible", out var v) && v is bool b && !b)) shape.Opacity = 0.2;
            Canvas.SetLeft(shape, o.X);
            Canvas.SetTop(shape, o.Y);
            _stage.Children.Add(shape);
        }
        var score = new TextBlock { Text = $"score {World.ToText(_world.Globals.GetValueOrDefault("score"))}", FontFamily = Palette.Mono, FontSize = 12, Foreground = Palette.Text };
        Canvas.SetRight(score, 10);
        Canvas.SetTop(score, 8);
        _stage.Children.Add(score);
    }

    void Log(string line) => _console.Text = (_console.Text ?? "") + line + "\n";

    // ---------------- Helpers ----------------

    static string KeyName(Key k) => k switch
    {
        Key.Left => "left", Key.Right => "right", Key.Up => "up", Key.Down => "down", Key.Space => "space", Key.Enter => "enter",
        _ => k.ToString().ToLowerInvariant()
    };

    /// <summary>Splits a file with several mechanisms into one source per mechanism (comments before it stay with it).</summary>
    static IEnumerable<(string Name, string Source)> SplitMechanisms(string src)
    {
        var lines = src.Replace("\r\n", "\n").Split('\n');
        var starts = new List<int>();
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("mechanism ")) starts.Add(i);
        for (int k = 0; k < starts.Count; k++)
        {
            int end = k + 1 < starts.Count ? starts[k + 1] : lines.Length;
            string body = string.Join("\n", lines[starts[k]..end]).TrimEnd() + "\n";
            yield return (lines[starts[k]]["mechanism ".Length..].Trim(), body);
        }
    }

    static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Palette.TextDim, VerticalAlignment = VerticalAlignment.Center, LetterSpacing = 1 };

    static Button AccentButton(string text) => new()
    {
        Content = text, Background = Palette.Accent, Foreground = Palette.AccentText, FontWeight = FontWeight.SemiBold,
        Padding = new Thickness(18, 8), CornerRadius = new CornerRadius(8)
    };

    static Button PlainButton(string text) => new()
    {
        Content = text, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
        Padding = new Thickness(14, 7), CornerRadius = new CornerRadius(8)
    };

    static Border WithBorder(Control c, Thickness t) => new() { Child = c, BorderBrush = Palette.Border, BorderThickness = t };
}
