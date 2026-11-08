using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// An object's animations (like EasyEngine's): the list on the left (★ = the default one, played when the game
/// starts), and for the chosen one its frames strip (◀ ▶ reorder, ✎ draw over it, ⧉ copy, ✕ remove), speed, loop
/// and a live preview. Frames come from the project's pictures, from files, from a sprite sheet cut into a grid, or
/// are drawn in the sprite editor. Works on a copy: <see cref="Animations"/> / <see cref="DefaultAnimation"/> when it closes.
/// </summary>
sealed class AnimationWindow : Window
{
    readonly string _projectDir;
    readonly ImageCache _pictures;
    public List<SpriteAnimation> Animations { get; }
    public string? DefaultAnimation { get; private set; }
    /// <summary>True once anything was changed.</summary>
    public bool Changed { get; private set; }

    SpriteAnimation? _cur;
    readonly ListBox _list = new();
    readonly TextBox _nameBox = new();
    readonly NumericUpDown _fps = new();
    readonly CheckBox _loop = new() { Content = "Loop", Foreground = Palette.Text };
    readonly Button _defaultBtn = MainWindow.PlainButton("☆ Set as default");
    readonly Image _preview = new() { Stretch = Stretch.Uniform };
    readonly WrapPanel _frames = new();
    readonly TextBlock _frameCount = new() { Foreground = Palette.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly ContentControl _right = new();
    Control _editor = null!, _empty = null!;
    readonly DispatcherTimer _timer = new();
    int _pf;
    bool _sync;

    public AnimationWindow(string projectDir, ImageCache pictures, string objectName, IEnumerable<SpriteAnimation> animations, string? defaultAnimation)
    {
        _projectDir = projectDir;
        _pictures = pictures;
        Animations = animations.Select(a => a.Clone()).ToList();
        DefaultAnimation = defaultAnimation;
        Title = $"Animations — {objectName}";
        Width = 900; Height = 600; MinWidth = 720; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Bg1; Foreground = Palette.Text;
        WindowLook.Apply(Resources);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        Content = Build();
        _timer.Tick += (_, _) => PreviewTick();
        Closed += (_, _) => _timer.Stop();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && e.Source is not TextBox) { e.Handled = true; Close(); } };
        RefreshList();
        Select(Animations.FirstOrDefault(a => a.Name == DefaultName) ?? Animations.FirstOrDefault());
    }

    string? DefaultName => Animations.FirstOrDefault(a => string.Equals(a.Name, DefaultAnimation, StringComparison.OrdinalIgnoreCase))?.Name ?? Animations.FirstOrDefault()?.Name;

    void Touch() => Changed = true;

    // ---------------- Layout ----------------

    Control Build()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), Margin = new Thickness(14) };

        // Left: the animations.
        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        var leftTop = MainWindow.Label("ANIMATIONS");
        leftTop.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(leftTop, Dock.Top);
        left.Children.Add(leftTop);
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        leftButtons.Children.Add(Small("+ New", "A new animation (idle, walk, jump…)", () => _ = NewAnimationAsync()));
        leftButtons.Children.Add(Small("⧉", "Duplicate this animation", DuplicateCurrent));
        var del = Small("✕", "Delete this animation", () => _ = DeleteCurrentAsync());
        del.Foreground = Palette.Error;
        leftButtons.Children.Add(del);
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        _list.Background = Palette.Bg0;
        _list.ItemTemplate = new FuncDataTemplate<SpriteAnimation>((a, _) => a == null ? new TextBlock() : AnimRow(a), supportsRecycling: false);
        _list.SelectionChanged += (_, _) => { if (!_sync && _list.SelectedItem is SpriteAnimation a && a != _cur) Select(a); };
        left.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _list });
        grid.Children.Add(left);

        // Right: the chosen animation.
        _empty = new TextBlock
        {
            Text = "No animations yet.\n\n“+ New” makes one (idle, walk, jump…), then add its frames: pictures of the project, files, a sprite sheet, or ✎ draw them.\nScripts play one with  animate \"walk\";  the ★ default one plays when the game starts.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 13, Margin = new Thickness(8)
        };
        _editor = BuildEditor();
        Grid.SetColumn(_right, 1);
        grid.Children.Add(_right);

        var done = MainWindow.AccentButton("Done");
        done.Click += (_, _) => Close();
        var root = new DockPanel();
        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 14) };
        foot.Children.Add(done);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(grid);
        return root;
    }

    Control BuildEditor()
    {
        var panel = new DockPanel();

        // Name, default, speed, loop, preview.
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        var fields = new StackPanel { Spacing = 10 };
        var nameRow = new DockPanel();
        _defaultBtn.Padding = new Thickness(10, 5);
        _defaultBtn.Margin = new Thickness(8, 0, 0, 0);
        ToolTip.SetTip(_defaultBtn, "The default animation plays when the game starts");
        _defaultBtn.Click += (_, _) => { if (_cur != null) { DefaultAnimation = _cur.Name; Touch(); RefreshList(); UpdateDefaultButton(); } };
        DockPanel.SetDock(_defaultBtn, Dock.Right);
        nameRow.Children.Add(_defaultBtn);
        _nameBox.Background = Palette.Bg2; _nameBox.Foreground = Palette.Text; _nameBox.BorderBrush = Palette.Border; _nameBox.FontSize = 15; _nameBox.MinHeight = 32;
        ToolTip.SetTip(_nameBox, "The animation's name: scripts play it with animate \"name\"");
        _nameBox.LostFocus += (_, _) => CommitName();
        _nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); e.Handled = true; } };
        nameRow.Children.Add(_nameBox);
        fields.Children.Add(nameRow);

        var speedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        speedRow.Children.Add(new TextBlock { Text = "Speed", Foreground = Palette.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _fps.Minimum = 1; _fps.Maximum = 60; _fps.Increment = 1; _fps.FormatString = "0"; _fps.Width = 120;
        _fps.Background = Palette.Bg2; _fps.Foreground = Palette.Text; _fps.BorderBrush = Palette.Border;
        _fps.ValueChanged += (_, e) =>
        {
            if (_sync || _cur == null || e.NewValue is not { } v) return;
            _cur.Fps = (double)v; Touch(); RestartPreview();
        };
        speedRow.Children.Add(_fps);
        speedRow.Children.Add(new TextBlock { Text = "frames a second", Foreground = Palette.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _loop.IsCheckedChanged += (_, _) =>
        {
            if (_sync || _cur == null) return;
            _cur.Loop = _loop.IsChecked == true; Touch(); RestartPreview();
        };
        ToolTip.SetTip(_loop, "Off: it plays once and stays on its last frame");
        speedRow.Children.Add(_loop);
        fields.Children.Add(speedRow);
        head.Children.Add(fields);

        var preview = new Border
        {
            Width = 120, Height = 120, Margin = new Thickness(14, 0, 0, 0), CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = SpriteEditorWindow.Checker(8), BorderBrush = Palette.Border, BorderThickness = new Thickness(1), Child = _preview,
        };
        ToolTip.SetTip(preview, "Preview (plays at its speed)");
        Grid.SetColumn(preview, 1);
        head.Children.Add(preview);
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);

        // Adding frames.
        var add = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button A(string text, string tip, Action run) { var b = Small(text, tip, run); b.Margin = new Thickness(0, 0, 8, 6); return b; }
        add.Children.Add(A("+ Pictures…", "Add pictures of the project (or import files) as frames", () => _ = AddPicturesAsync()));
        add.Children.Add(A("▦ From a sprite sheet…", "Cut one picture into frames by columns and rows", () => _ = AddFromSheetAsync()));
        add.Children.Add(A("✎ Draw a frame…", "Draw a new frame in the sprite editor", () => _ = DrawFrameAsync(-1)));
        DockPanel.SetDock(add, Dock.Bottom);
        panel.Children.Add(add);

        var framesHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        framesHead.Children.Add(MainWindow.Label("FRAMES"));
        _frameCount.Margin = new Thickness(10, 0, 0, 0);
        framesHead.Children.Add(_frameCount);
        DockPanel.SetDock(framesHead, Dock.Top);
        panel.Children.Add(framesHead);
        panel.Children.Add(new Border
        {
            Background = Palette.Bg0, CornerRadius = new CornerRadius(8), Padding = new Thickness(10),
            Child = new ScrollViewer { Content = _frames, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        });
        return panel;
    }

    static Button Small(string text, string tip, Action click)
    {
        var b = MainWindow.PlainButton(text);
        b.Padding = new Thickness(10, 5);
        b.FontSize = 12;
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    Control AnimRow(SpriteAnimation a)
    {
        var p = new StackPanel { Spacing = 1 };
        p.Children.Add(new TextBlock { Text = (a.Name == DefaultName ? "★ " : "") + a.Name, FontWeight = FontWeight.SemiBold, FontSize = 13 });
        p.Children.Add(new TextBlock { Text = $"{a.Frames.Count} frame(s) · {a.Fps:0.#} fps{(a.Loop ? "" : " · once")}", Foreground = Palette.TextDim, FontSize = 11 });
        return p;
    }

    void RefreshList()
    {
        _sync = true;
        _list.ItemsSource = null;
        _list.ItemsSource = Animations;
        _list.SelectedItem = _cur;
        _sync = false;
    }

    internal void Select(SpriteAnimation? a)
    {
        _cur = a;
        _right.Content = a == null ? _empty : _editor;
        if (a == null) { _timer.Stop(); RefreshList(); return; }
        _sync = true;
        _nameBox.Text = a.Name;
        _fps.Value = (decimal)Math.Clamp(Math.Round(a.Fps), 1, 60);
        _loop.IsChecked = a.Loop;
        _list.SelectedItem = a;
        _sync = false;
        UpdateDefaultButton();
        RefreshFrames();
    }

    void UpdateDefaultButton()
    {
        bool on = _cur != null && _cur.Name == DefaultName;
        _defaultBtn.Content = on ? "★ Default" : "☆ Set as default";
        _defaultBtn.IsEnabled = !on;
    }

    // ---------------- Frames ----------------

    void RefreshFrames()
    {
        _frames.Children.Clear();
        if (_cur == null) return;
        _frameCount.Text = $"{_cur.Frames.Count} frame(s)";
        for (int i = 0; i < _cur.Frames.Count; i++) _frames.Children.Add(FrameCard(i));
        if (_cur.Frames.Count == 0)
            _frames.Children.Add(new TextBlock { Text = "No frames yet — add some below.", Foreground = Palette.TextDim, FontSize = 12, Margin = new Thickness(4) });
        RestartPreview();
        RefreshList();
    }

    Control FrameCard(int index)
    {
        var a = _cur!;
        string pic = a.Frames[index];
        var stack = new StackPanel { Margin = new Thickness(0, 0, 10, 10), Spacing = 3 };
        var thumb = new Border
        {
            Width = 84, Height = 84, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = SpriteEditorWindow.Checker(6),
            BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            Child = _pictures.Get(pic) is { } bmp
                ? new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(4) }
                : new TextBlock { Text = "missing", Foreground = Palette.Error, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        ToolTip.SetTip(thumb, pic);
        stack.Children.Add(thumb);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Button B(string t, string tip, Action run, IBrush? fg = null)
        {
            var b = new Button { Content = t, Padding = new Thickness(4, 0), FontSize = 11, Background = Brushes.Transparent, Foreground = fg ?? Palette.Text, BorderThickness = new Thickness(0), MinWidth = 0 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => run();
            return b;
        }
        row.Children.Add(B("◀", "Move earlier", () => MoveFrame(index, -1)));
        row.Children.Add(new TextBlock { Text = (index + 1).ToString(), Foreground = Palette.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) });
        row.Children.Add(B("▶", "Move later", () => MoveFrame(index, +1)));
        row.Children.Add(B("✎", "Draw over this frame in the sprite editor", () => _ = DrawFrameAsync(index)));
        row.Children.Add(B("⧉", "Duplicate this frame (the copy goes right after it)", () => { a.Frames.Insert(index + 1, a.Frames[index]); Touch(); RefreshFrames(); }));
        row.Children.Add(B("✕", "Remove this frame (the picture stays in the project)", () => { a.Frames.RemoveAt(index); Touch(); RefreshFrames(); }, Palette.Error));
        stack.Children.Add(row);
        return stack;
    }

    void MoveFrame(int index, int dir)
    {
        if (_cur == null) return;
        int j = index + dir;
        if (j < 0 || j >= _cur.Frames.Count) return;
        (_cur.Frames[index], _cur.Frames[j]) = (_cur.Frames[j], _cur.Frames[index]);
        Touch();
        RefreshFrames();
    }

    /// <summary>Adds frames to the chosen animation (making one called "idle" when there's none).</summary>
    internal void AddFrames(IEnumerable<string> pictures)
    {
        var list = pictures.ToList();
        if (list.Count == 0) return;
        if (_cur == null)
        {
            var a = new SpriteAnimation { Name = SuggestName() };
            Animations.Add(a);
            DefaultAnimation ??= a.Name;
            Select(a);
        }
        _cur!.Frames.AddRange(list);
        _pictures.Clear();
        Touch();
        RefreshFrames();
    }

    /// <summary>Sorts names the way people count: walk2 before walk10.</summary>
    internal static string NaturalKey(string name) => Regex.Replace(Path.GetFileNameWithoutExtension(name), @"\d+", m => m.Value.PadLeft(8, '0'));

    async Task AddPicturesAsync()
    {
        var pick = new PicturePickWindow(_projectDir, _pictures);
        var chosen = await pick.ShowDialog<List<string>?>(this);
        if (chosen != null) AddFrames(chosen.OrderBy(NaturalKey, StringComparer.OrdinalIgnoreCase));
    }

    async Task AddFromSheetAsync()
    {
        var w = new SheetSliceWindow(_projectDir, _pictures);
        var frames = await w.ShowDialog<List<string>?>(this);
        if (frames != null) AddFrames(frames);
    }

    async Task DrawFrameAsync(int index)
    {
        string? edit = index >= 0 && _cur != null ? _cur.Frames[index] : null;
        int size = 32;
        if (edit == null && _cur?.Frames.FirstOrDefault() is { } first && Images.Resolve(_projectDir, first) is { } fp && Images.Size(fp) is { } sz) size = Math.Clamp(Math.Max(sz.Width, sz.Height), 4, PixelCanvas.MaxSize);
        var editor = new SpriteEditorWindow(_projectDir, edit, size);
        await editor.ShowDialog(this);
        if (editor.SavedName is not { } saved) return;
        _pictures.Clear();
        if (index >= 0 && _cur != null) { _cur.Frames[index] = saved; Touch(); RefreshFrames(); }
        else AddFrames([saved]);
    }

    // ---------------- Animations ----------------

    string SuggestName()
    {
        foreach (var c in new[] { "idle", "walk", "run", "jump", "attack", "hurt", "death" })
            if (!Animations.Any(a => a.Name.Equals(c, StringComparison.OrdinalIgnoreCase))) return c;
        return "animation " + (Animations.Count + 1);
    }

    string UniqueName(string wanted, SpriteAnimation? except = null)
    {
        bool Taken(string n) => Animations.Any(a => a != except && a.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(wanted)) return wanted;
        for (int k = 2; ; k++) if (!Taken($"{wanted} {k}")) return $"{wanted} {k}";
    }

    /// <summary>A new, empty animation, chosen. Returns it.</summary>
    internal SpriteAnimation AddAnimation(string name)
    {
        var a = new SpriteAnimation { Name = UniqueName(string.IsNullOrWhiteSpace(name) ? SuggestName() : name.Trim()) };
        Animations.Add(a);
        DefaultAnimation ??= a.Name;
        Touch();
        RefreshList();
        Select(a);
        return a;
    }

    async Task NewAnimationAsync()
    {
        var name = await Dialogs.Input(this, "New animation", "Its name (scripts play it with animate \"name\"): idle, walk, jump…", SuggestName(), "Make it");
        if (name is { Length: > 0 }) AddAnimation(name);
    }

    void DuplicateCurrent()
    {
        if (_cur == null) return;
        var copy = _cur.Clone();
        copy.Name = UniqueName(_cur.Name);
        Animations.Insert(Animations.IndexOf(_cur) + 1, copy);
        Touch();
        Select(copy);
    }

    async Task DeleteCurrentAsync()
    {
        if (_cur == null) return;
        var r = await Dialogs.Ask(this, "Delete animation", $"Delete the animation “{_cur.Name}”? Its pictures stay in the project.", "Delete", "Cancel");
        if (r != "Delete") return;
        int at = Animations.IndexOf(_cur);
        Animations.Remove(_cur);
        if (string.Equals(DefaultAnimation, _cur.Name, StringComparison.OrdinalIgnoreCase)) DefaultAnimation = Animations.FirstOrDefault()?.Name;
        Touch();
        Select(Animations.Count == 0 ? null : Animations[Math.Clamp(at, 0, Animations.Count - 1)]);
    }

    void CommitName()
    {
        if (_cur == null || _sync) return;
        string n = (_nameBox.Text ?? "").Trim();
        if (n.Length == 0 || n == _cur.Name) { _nameBox.Text = _cur.Name; return; }
        n = UniqueName(n, _cur);
        bool wasDefault = _cur.Name == DefaultName;
        _cur.Name = n;
        if (wasDefault) DefaultAnimation = n;
        _nameBox.Text = n;
        Touch();
        RefreshList();
        UpdateDefaultButton();
    }

    // ---------------- Preview ----------------

    void RestartPreview()
    {
        _timer.Stop();
        _pf = 0;
        if (_cur == null || _cur.Frames.Count == 0) { _preview.Source = null; return; }
        _preview.Source = _pictures.Get(_cur.Frames[0]);
        _timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _cur.Fps));
        if (_cur.Frames.Count > 1) _timer.Start();
    }

    void PreviewTick()
    {
        if (_cur == null || _cur.Frames.Count == 0) return;
        _pf++;
        if (_pf >= _cur.Frames.Count)
        {
            if (!_cur.Loop) { _pf = _cur.Frames.Count - 1; _timer.Stop(); }
            else _pf = 0;
        }
        _preview.Source = _pictures.Get(_cur.Frames[_pf]);
    }
}

/// <summary>Chooses pictures of the project (several at once), with "Import files…". Returns their names, or null.</summary>
sealed class PicturePickWindow : Window
{
    readonly string _projectDir;
    readonly ImageCache _pictures;
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle };

    public PicturePickWindow(string projectDir, ImageCache pictures)
    {
        _projectDir = projectDir;
        _pictures = pictures;
        Title = "Choose pictures";
        Width = 420; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Bg1; Foreground = Palette.Text;
        WindowLook.Apply(Resources);
        _list.Background = Palette.Bg0;
        _list.ItemTemplate = new FuncDataTemplate<string>((name, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var thumb = new Border { Width = 32, Height = 32, Background = SpriteEditorWindow.Checker(4), CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = new Image { Source = _pictures.Get(name), Stretch = Stretch.Uniform } };
            RenderOptions.SetBitmapInterpolationMode(thumb, BitmapInterpolationMode.None);
            row.Children.Add(thumb);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }, supportsRecycling: false);
        Fill([]);

        var root = new DockPanel { Margin = new Thickness(14) };
        root.Children.Add(Top("Click the pictures to use as frames (in this order: numbers count up, walk2 before walk10)."));
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var import = MainWindow.PlainButton("Import files…");
        import.Click += async (_, _) => await ImportAsync();
        DockPanel.SetDock(import, Dock.Left);
        buttons.Children.Add(import);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MainWindow.PlainButton("Cancel");
        cancel.Click += (_, _) => Close(null);
        var ok = MainWindow.AccentButton("Add");
        ok.Click += (_, _) => Close(_list.SelectedItems?.OfType<string>().ToList() ?? []);
        right.Children.Add(cancel);
        right.Children.Add(ok);
        buttons.Children.Add(right);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _list });
        Content = root;
    }

    static Control Top(string text)
    {
        var t = new TextBlock { Text = text, Foreground = Palette.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(t, Dock.Top);
        return t;
    }

    void Fill(IEnumerable<string> select)
    {
        _list.ItemsSource = Images.List(_projectDir);
        foreach (var s in select) _list.SelectedItems?.Add(s);
    }

    async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import pictures", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Pictures (png, jpg, bmp)") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }]
        });
        var keep = _list.SelectedItems?.OfType<string>().ToList() ?? [];
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path)
                try { keep.Add(Images.Import(_projectDir, path)); } catch (Exception) { }
        _pictures.Clear();
        Fill(keep.Distinct());
    }
}

/// <summary>
/// Cuts a sprite sheet into frames: the picture (from the project or a file), how many columns and rows (picked), a
/// preview with the grid, and the frames' name. Saves each frame as a PNG ("name_1.png"…) and returns their names.
/// </summary>
sealed class SheetSliceWindow : Window
{
    readonly string _projectDir;
    readonly ImageCache _pictures;
    readonly ComboBox _source = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    readonly NumericUpDown _cols = new(), _rows = new();
    readonly TextBox _name = new();
    readonly TextBlock _info = new() { Foreground = Palette.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly SheetPreview _preview = new();
    (int W, int H, uint[] Px)? _pixels;
    bool _sync;

    public SheetSliceWindow(string projectDir, ImageCache pictures, string? picture = null)
    {
        _projectDir = projectDir;
        _pictures = pictures;
        Title = "Frames from a sprite sheet";
        Width = 760; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Bg1; Foreground = Palette.Text;
        WindowLook.Apply(Resources);

        foreach (var n in new[] { _cols, _rows })
        {
            n.Minimum = 1; n.Maximum = 64; n.Increment = 1; n.FormatString = "0"; n.Width = 110; n.Value = 1;
            n.Background = Palette.Bg2; n.Foreground = Palette.Text; n.BorderBrush = Palette.Border;
            n.ValueChanged += (_, _) => { if (!_sync) UpdateGrid(); };
        }
        _source.Background = Palette.Bg2;
        _source.ItemsSource = Images.List(projectDir);
        _source.PlaceholderText = "Choose the sheet…";
        _source.SelectionChanged += (_, _) => { if (_source.SelectedItem is string s) LoadSheet(s); };
        _name.Background = Palette.Bg2; _name.Foreground = Palette.Text; _name.BorderBrush = Palette.Border; _name.MinHeight = 30;
        ToolTip.SetTip(_name, "The frames are saved as name_1.png, name_2.png…");

        var root = new DockPanel { Margin = new Thickness(14) };
        var form = new StackPanel { Width = 230, Spacing = 10, Margin = new Thickness(0, 0, 14, 0) };
        form.Children.Add(MainWindow.Label("SHEET"));
        form.Children.Add(_source);
        var import = MainWindow.PlainButton("Import a file…");
        import.Click += async (_, _) => await ImportAsync();
        form.Children.Add(import);
        form.Children.Add(MainWindow.Label("GRID"));
        form.Children.Add(Row("Columns", _cols));
        form.Children.Add(Row("Rows", _rows));
        form.Children.Add(MainWindow.Label("FRAMES' NAME"));
        form.Children.Add(_name);
        form.Children.Add(_info);
        DockPanel.SetDock(form, Dock.Left);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = MainWindow.PlainButton("Cancel");
        cancel.Click += (_, _) => Close(null);
        var slice = MainWindow.AccentButton("Cut into frames");
        slice.Click += (_, _) => Slice();
        buttons.Children.Add(cancel);
        buttons.Children.Add(slice);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(form);
        root.Children.Add(new Border { Background = Palette.Bg0, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _preview });
        Content = root;
        if (picture != null) _source.SelectedItem = Images.List(projectDir).FirstOrDefault(p => string.Equals(p, picture, StringComparison.OrdinalIgnoreCase));
        UpdateGrid();
    }

    static Control Row(string label, Control field)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
        g.Children.Add(new TextBlock { Text = label, Foreground = Palette.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(field, 1);
        g.Children.Add(field);
        return g;
    }

    void LoadSheet(string name)
    {
        _pixels = Images.Resolve(_projectDir, name) is { } path ? PictureDecode.Load(path) : null;
        _preview.Bitmap = _pictures.Get(name);
        if (_pixels is { } p)
        {
            var (c, r) = SpriteSheet.Guess(p.W, p.H);
            _sync = true;
            _cols.Value = c; _rows.Value = r;
            _sync = false;
        }
        _name.Text = Path.GetFileNameWithoutExtension(name);
        UpdateGrid();
    }

    void UpdateGrid()
    {
        int c = (int)(_cols.Value ?? 1), r = (int)(_rows.Value ?? 1);
        _preview.Columns = c; _preview.Rows = r;
        _preview.InvalidateVisual();
        if (_pixels is not { } p) { _info.Text = "Choose a picture that has the frames side by side (a strip or a grid)."; return; }
        var rects = SpriteSheet.Slice(p.W, p.H, c, r);
        _info.Text = rects.Count == 0 ? "Too many columns or rows for this picture." : $"{rects.Count} frame(s) of {rects[0].W} × {rects[0].H} px (empty cells are skipped).";
    }

    async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a sprite sheet", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pictures (png, jpg, bmp)") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            string name = Images.Import(_projectDir, path);
            _pictures.Clear();
            _source.ItemsSource = Images.List(_projectDir);
            _source.SelectedItem = name;
        }
        catch (Exception e) { _info.Text = $"Couldn't import it: {e.Message}"; }
    }

    /// <summary>Cuts the sheet now (also used by --snap). Returns the frames' names.</summary>
    internal List<string>? Slice(bool close = true)
    {
        if (_pixels is not { } p) return null;
        try
        {
            var names = SpriteSheet.SaveFrames(_projectDir, string.IsNullOrWhiteSpace(_name.Text) ? "frame" : _name.Text!, p.W, p.H, p.Px, (int)(_cols.Value ?? 1), (int)(_rows.Value ?? 1));
            _pictures.Clear();
            if (close) Close(names);
            return names;
        }
        catch (Exception e)
        {
            _info.Text = $"Couldn't save the frames: {e.Message}";
            return null;
        }
    }

    internal void SetGrid(int columns, int rows)
    {
        _cols.Value = columns; _rows.Value = rows;
        UpdateGrid();
    }

    /// <summary>The sheet fitted in the box with the cutting grid over it.</summary>
    sealed class SheetPreview : Control
    {
        public Bitmap? Bitmap;
        public int Columns = 1, Rows = 1;

        public override void Render(DrawingContext ctx)
        {
            if (Bitmap == null || Bounds.Width < 10) return;
            double sw = Bitmap.PixelSize.Width, sh = Bitmap.PixelSize.Height;
            double k = Math.Min((Bounds.Width - 20) / sw, (Bounds.Height - 20) / sh);
            if (k >= 1) k = Math.Floor(k);
            var r = new Rect((Bounds.Width - sw * k) / 2, (Bounds.Height - sh * k) / 2, sw * k, sh * k);
            ctx.FillRectangle(SpriteEditorWindow.Checker(6), r);
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                ctx.DrawImage(Bitmap, new Rect(0, 0, sw, sh), r);
            var pen = new Pen(Palette.Accent, 1);
            double fw = Math.Floor(sw / Math.Max(1, Columns)) * k, fh = Math.Floor(sh / Math.Max(1, Rows)) * k;
            if (fw < 1 || fh < 1) return;
            for (int c = 0; c <= Columns; c++) ctx.DrawLine(pen, new Point(r.X + c * fw, r.Y), new Point(r.X + c * fw, r.Y + Rows * fh));
            for (int row = 0; row <= Rows; row++) ctx.DrawLine(pen, new Point(r.X, r.Y + row * fh), new Point(r.X + Columns * fw, r.Y + row * fh));
        }
    }
}

/// <summary>The BuilderEngine orange (not Fluent's blue) for checked boxes, focused fields and chosen list rows in these windows.</summary>
static class WindowLook
{
    public static void Apply(IResourceDictionary r)
    {
        foreach (var k in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
                     "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed",
                     "TextControlBorderBrushFocused", "ComboBoxBorderBrushFocused" })
            r[k] = Palette.Accent;
        foreach (var k in new[] { "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed" })
            r[k] = Palette.AccentText;
        foreach (var k in new[] { "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush", "SystemControlHighlightListAccentHighBrush" })
            r[k] = Palette.AccentSoft;
    }
}
