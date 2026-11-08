using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>A block category's colours: background and text (contrast ≥ 4.5:1).</summary>
readonly record struct BlockLook(IBrush Bg, IBrush Fg);

static class BlockColors
{
    public static readonly BlockLook Event = new(B("#3A2419"), B("#F7D6C6"));    // when, wait
    public static readonly BlockLook Control = new(B("#33301F"), B("#EFE3BD"));  // if, repeat, stop
    public static readonly BlockLook Message = new(B("#1D2A36"), B("#B9D0E6"));  // emit, tell
    public static readonly BlockLook Command = new(B("#1F1F1F"), B("#E0E0E0"));  // commands, variables
    public static readonly BlockLook Setting = new(B("#2A1D17"), B("#F2C9B6"));  // param, uses
    public static readonly BlockLook Proc = new(B("#33203A"), B("#EBCDF4"));     // to, my commands, return
    public static readonly BlockLook Parallel = new(B("#1C2E27"), B("#BFE8D3")); // together, run
    public static readonly BlockLook List = new(B("#262033"), B("#D5CCF0"));     // add / remove (lists)
    public static readonly IBrush PillBg = B("#121212");
    public static readonly IBrush PillText = B("#ECECEC");
    public static readonly IBrush PillDim = B("#9A9A9A");
    public static readonly IBrush PillBorder = B("#3A3A3A");
    public static readonly IBrush ErrorBg = B("#3A1512");
    public static readonly IBrush ErrorText = B("#F2B8AE");
    public static readonly IBrush SelectedBody = B("#1A1310");

    static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}

/// <summary>
/// A value inside a block, drawn as a small dark pill. Click to edit it inline; Enter or leaving the box applies it,
/// Escape cancels. The commit function returns the canonical text, or throws a <see cref="ScriptError"/>: the pill
/// then turns red with the error as its tooltip and nothing is applied.
/// </summary>
sealed class Field : Border
{
    readonly TextBlock _label = new() { FontFamily = Palette.Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    readonly Func<string, string> _commit;
    readonly string _placeholder;
    string _value;
    string? _bad;
    TextBox? _box;
    static int _ids;

    /// <summary>Tells each field apart, so consecutive edits of one field merge into one undo step.</summary>
    public int Id { get; } = ++_ids;

    /// <summary>The field whose value is being applied right now (set while its commit function runs).</summary>
    public static Field? Committing { get; private set; }

    public Field(string value, Func<string, string> commit, string placeholder = "")
    {
        _value = value;
        _commit = commit;
        _placeholder = BlocksView.BlockText(placeholder);
        CornerRadius = new CornerRadius(5);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(2, 1);
        MinWidth = 22;
        MinHeight = 22;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Child = _label;
        Refresh();
        PointerPressed += (_, e) => { e.Handled = true; Begin(); };
    }

    void Refresh()
    {
        string shown = _bad ?? _value;
        _label.Text = shown.Length == 0 ? _placeholder : shown;
        _label.Foreground = _bad != null ? BlockColors.ErrorText : shown.Length == 0 ? BlockColors.PillDim : BlockColors.PillText;
        // An empty optional value is a faint outline, so it doesn't compete with the real values.
        Background = _bad != null ? BlockColors.ErrorBg : shown.Length == 0 ? Brushes.Transparent : BlockColors.PillBg;
        BorderBrush = _bad != null ? Palette.Error : BlockColors.PillBorder;
        Padding = new Thickness(6, 1);
    }

    void Begin()
    {
        if (_box != null) return;
        _box = new TextBox
        {
            Text = _bad ?? _value, FontFamily = Palette.Mono, FontSize = 12.5, MinWidth = 70, MinHeight = 0,
            Padding = new Thickness(5, 1), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = BlockColors.PillText, CaretBrush = Palette.Accent, VerticalContentAlignment = VerticalAlignment.Center
        };
        _box.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        _box.LostFocus += (_, _) => Finish(apply: true, fromEnter: false);
        Padding = new Thickness(0);
        Child = _box;
        var box = _box;
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Finish(apply: true, fromEnter: true); }
        else if (e.Key == Key.Escape) { e.Handled = true; Finish(apply: false, fromEnter: false); }
    }

    void Finish(bool apply, bool fromEnter)
    {
        var box = _box;
        if (box == null) return;
        string text = box.Text ?? "";
        string? error = null;
        if (apply && text != _value)
        {
            Committing = this;
            try { _value = _commit(text); }
            catch (ScriptError e) { error = e.Message; }
            finally { Committing = null; }
        }
        _bad = error == null ? null : text;
        ToolTip.SetTip(this, error);
        if (error != null && fromEnter)
        {
            // Keep editing: red frame, the reason in the tooltip.
            Background = BlockColors.ErrorBg;
            BorderBrush = Palette.Error;
            ToolTip.SetIsOpen(this, true);
            return;
        }
        ToolTip.SetIsOpen(this, false);
        _box = null;
        Child = _label;
        Refresh();
    }
}

/// <summary>
/// The blocks view: the open mechanism's code as coloured blocks, 1 to 1 (one statement = one block), with a palette
/// on the left. Editing a block changes the AST and reprints the code (<see cref="Edited"/>); displaying never does.
/// </summary>
sealed partial class BlocksView : Border
{
    /// <summary>The person changed something in the blocks: the new source (canonical, printed from the blocks).</summary>
    public event Action<string>? Edited;

    /// <summary>Every text a block shows goes through here, so blocks can be translated later (code keywords stay English).</summary>
    internal static string BlockText(string english) => english;

    /// <summary>
    /// The project's other mechanisms (parsed), so the palette can list their commands under MY COMMANDS and a call
    /// block can show its inputs by name. Set by the window; empty when not set.
    /// </summary>
    public Func<IReadOnlyList<MechanismDef>>? ProjectDefs { get; set; }
    IReadOnlyList<MechanismDef> _project = [];

    sealed record BlockOps(Action? Up, Action? Down, Action? Duplicate, Action Delete, string? ExtraText = null, string? ExtraTip = null, Action? Extra = null);

    List<MechanismDef> _defs = [];
    string _shown = "";
    bool _stale;
    /// <summary>The code changed and <see cref="Show"/> comes in a moment (see <see cref="Expect"/>).</summary>
    bool _pending;
    /// <summary>No edits now: the code has an error (stale) or newer code is about to be shown (pending).</summary>
    bool Locked => _stale || _pending;
    /// <summary>
    /// False right after another mechanism was opened (or the code didn't parse): the next code shown is a fresh start,
    /// not an edit made in the code view (which becomes an undo step, see <see cref="Show"/>).
    /// </summary>
    bool _codeIsEdit;
    /// <summary>The line each statement is on in the code printed by the last block edit (null = the parsed lines).</summary>
    Dictionary<Stmt, int>? _lines;
    readonly StackPanel _blocks = new() { Margin = new Thickness(12, 12, 16, 48) };
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Border _banner;
    readonly TextBlock _bannerText = new() { Foreground = BlockColors.Event.Fg, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    readonly Control _palette;

    // Where the palette adds: a statement list (when body, if branch, repeat body) and its mechanism.
    List<Stmt>? _selBody;
    int _selMech = int.MaxValue;
    readonly Dictionary<List<Stmt>, (Border Rail, Border Area, IBrush Normal)> _bodies = new();
    readonly Dictionary<List<Stmt>, int> _bodyMech = new();
    object? _reveal;
    Control? _revealControl;

    // Undo / redo of block edits (snapshots of the printed source).
    readonly BlockUndo _undo = new();
    readonly Button _undoButton, _redoButton;

    public BlocksView()
    {
        Background = Palette.Bg0;
        Focusable = true;
        FocusAdorner = null;
        _palette = BuildPalette();
        _scroll.Content = _blocks;

        _undoButton = BarButton("↶", "Undo (Ctrl+Z)", Undo);
        _redoButton = BarButton("↷", "Redo (Ctrl+Y)", Redo);
        var bar = new DockPanel { Margin = new Thickness(12, 6, 16, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(_undoButton);
        buttons.Children.Add(_redoButton);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(new TextBlock
        {
            Text = BlockText("Drag blocks to move them. Drop one on the palette to delete it."), FontSize = 11,
            Foreground = Palette.TextDim, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        });
        UpdateUndoButtons();

        var bannerRow = new StackPanel { Spacing = 2 };
        bannerRow.Children.Add(new TextBlock { Text = "⚠  " + BlockText("Fix the code to edit blocks"), FontWeight = FontWeight.SemiBold, Foreground = BlockColors.Event.Fg });
        bannerRow.Children.Add(_bannerText);
        _banner = new Border
        {
            Background = Palette.AccentSoft, BorderBrush = Palette.AccentBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Margin = new Thickness(12, 12, 16, 0),
            Child = bannerRow, IsVisible = false
        };

        var right = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar);
        DockPanel.SetDock(_banner, Dock.Top);
        right.Children.Add(_banner);
        right.Children.Add(_scroll);

        var root = new DockPanel();
        DockPanel.SetDock(_palette, Dock.Left);
        root.Children.Add(_palette);
        root.Children.Add(right);
        // The drag ghost, the insertion line and the delete zone are drawn on a layer above everything.
        var layers = new Grid();
        layers.Children.Add(root);
        layers.Children.Add(BuildDragLayer());
        Child = layers;

        // Clicking anywhere in the blocks (but not in a text box) gives them the keyboard, for Ctrl+Z / Ctrl+Y.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) == null) Focus();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnBlocksKey);
        InitDrag();
    }

    Button BarButton(string text, string tip, Action act)
    {
        var b = new Button
        {
            Content = text, FontSize = 15, Padding = new Thickness(8, 0), Height = 24, MinHeight = 0, MinWidth = 30,
            Background = Palette.Bg2, Foreground = Palette.Text, BorderThickness = new Thickness(1), BorderBrush = Palette.Border,
            CornerRadius = new CornerRadius(5), VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(b, BlockText(tip));
        b.Click += (_, e) => { e.Handled = true; act(); Focus(); };
        return b;
    }

    void OnBlocksKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && CancelDrag()) { e.Handled = true; return; }
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if ((e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) != null) return; // a field has its own undo
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { Redo(); e.Handled = true; }
        else if (e.Key == Key.Z) { Undo(); e.Handled = true; }
    }

    // ---------------- Undo ----------------

    /// <summary>Steps back one edit (a block edit, or the code typed since the last one); the code view follows.</summary>
    public void Undo() => Restore(Locked ? null : _undo.Undo(_shown));

    /// <summary>Steps forward one edit.</summary>
    public void Redo() => Restore(Locked ? null : _undo.Redo(_shown));

    /// <summary>Forgets the undo steps and the selection: another mechanism is being opened.</summary>
    public void ResetUndo()
    {
        _undo.Reset();
        _codeIsEdit = false;
        _selBody = null;
        _selMech = int.MaxValue;
        UpdateUndoButtons();
    }

    internal bool CanUndo => _undo.CanUndo;
    internal bool CanRedo => _undo.CanRedo;

    void Restore(string? source)
    {
        if (source == null) return;
        List<MechanismDef> defs;
        try { defs = Parser.Parse(source); }
        catch (ScriptError) { return; } // snapshots always parsed when they were taken
        var place = SelAddress();
        _defs = defs;
        _shown = source;
        _lines = null;
        SelectAddress(place);
        Rebuild();
        UpdateUndoButtons();
        Edited?.Invoke(source);
    }

    void UpdateUndoButtons()
    {
        _undoButton.IsEnabled = !Locked && _undo.CanUndo;
        _redoButton.IsEnabled = !Locked && _undo.CanRedo;
    }

    /// <summary>Where the selected body is, as an address that survives a reparse.</summary>
    (int Mech, int[]? Path) SelAddress()
    {
        if (_selBody != null && _bodyMech.TryGetValue(_selBody, out int mi) && mi < _defs.Count)
            return (mi, BlockEdits.PathOf(_defs[mi], _selBody));
        return (_selMech, null);
    }

    void SelectAddress((int Mech, int[]? Path) place)
    {
        _selMech = place.Mech;
        _selBody = place.Path != null && place.Mech >= 0 && place.Mech < _defs.Count ? BlockEdits.BodyAt(_defs[place.Mech], place.Path) : null;
    }

    // ---------------- Showing ----------------

    static string Norm(string s) => s.Replace("\r\n", "\n");

    /// <summary>Shows a mechanism's source as blocks. Source that doesn't parse leaves the view empty, waiting for a fix.</summary>
    public void Show(string source)
    {
        _pending = false;
        if (Norm(source) == Norm(_shown) && _defs.Count > 0) { SetStale(false); return; }
        List<MechanismDef> defs;
        try { defs = Parser.Parse(source); }
        catch (ScriptError e)
        {
            _defs = [];
            _shown = source;
            _codeIsEdit = false; // an undo step must always hold code that parses
            SetStale(true, e.ToString());
            Rebuild();
            return;
        }
        // Code typed in the code view is an undo step too (typing until the next block edit is one step): without it,
        // undoing the block edit before it put that older text back and the typed code was gone.
        if (_codeIsEdit) _undo.Record(_shown, source, "code");
        _codeIsEdit = true;
        var place = SelAddress(); // the code changed: keep the same place selected when it still exists
        _defs = defs;
        _shown = source;
        _lines = null;
        SelectAddress(place);
        Rebuild();
        SetStale(false);
    }

    /// <summary>
    /// The code changed and will be shown after a short pause in the typing: until <see cref="Show"/> the blocks take no
    /// edits, so an edit can't print the older blocks over the newer code (losing what was just typed).
    /// </summary>
    public void Expect()
    {
        _pending = true;
        ApplyLock();
    }

    /// <summary>Stale = the code has an error: keep the last good blocks, greyed and locked, with a note.</summary>
    public void SetStale(bool stale, string? detail = null)
    {
        _stale = stale;
        _banner.IsVisible = stale;
        _bannerText.Text = detail ?? BlockText("The blocks show the last version that worked. They come back as soon as the code has no errors.");
        _scroll.Opacity = stale ? 0.4 : 1;
        _palette.Opacity = stale ? 0.4 : 1;
        ApplyLock();
    }

    void ApplyLock()
    {
        _scroll.IsHitTestVisible = !Locked;
        _palette.IsHitTestVisible = !Locked;
        if (Locked) CancelDrag();
        UpdateUndoButtons();
    }

    /// <summary>The line a statement is on in the code shown now (it moves when a block edit reprints the code).</summary>
    int LineOf(Stmt s) => _lines != null && _lines.TryGetValue(s, out int line) ? line : s.Line;

    /// <summary>
    /// Applies an edit to the blocks: reprints the code, records one undo step (edits of one field in a row merge) and
    /// tells the code view. The selected place stays selected (the bodies are the same lists).
    /// </summary>
    void Commit(bool rebuild = true)
    {
        string before = _shown;
        _shown = Printer.Print(_defs);
        _lines = BlockEdits.LinesAfterPrint(_defs, _shown) ?? _lines;
        _undo.Record(before, _shown, Field.Committing is { } f ? "field:" + f.Id : null);
        if (_pending) { _pending = false; ApplyLock(); } // this edit replaces the code that was about to be shown
        UpdateUndoButtons();
        if (rebuild) Dispatcher.UIThread.Post(Rebuild);
        Edited?.Invoke(_shown);
    }

    void Rebuild()
    {
        var offset = _scroll.Offset;
        _bodies.Clear();
        _bodyMech.Clear();
        _stmtSlots.Clear();
        _mechSlots.Clear();
        _revealControl = null;
        _blocks.Children.Clear();
        _debugStmt = _debugLine > 0 ? FindStmtAt(_debugLine) : null; // BlocksView.Debug.cs
        _project = ProjectDefs?.Invoke() ?? [];
        RefreshMyCommands();
        if (_defs.Count == 0)
            _blocks.Children.Add(Note(_stale ? "" : "Empty — start with 'mechanism Name' in the code."));
        for (int mi = 0; mi < _defs.Count; mi++) _blocks.Children.Add(MechanismBlock(mi));
        if (_selBody == null || !_bodies.ContainsKey(_selBody)) _selBody = DefaultBody();
        Highlight();
        _reveal = null;
        var reveal = _revealControl;
        Dispatcher.UIThread.Post(() =>
        {
            if (reveal != null) reveal.BringIntoView();
            else _scroll.Offset = offset;
        }, DispatcherPriority.Loaded);
    }

    int MechIndex() => Math.Clamp(_selMech, 0, Math.Max(0, _defs.Count - 1));

    List<Stmt>? DefaultBody()
    {
        if (_defs.Count == 0) return null;
        var m = _defs[MechIndex()];
        return m.Whens.Count > 0 ? m.Whens[^1].Body : m.Procs.Count > 0 ? m.Procs[0].Body : null;
    }

    void Select(List<Stmt> body)
    {
        _selBody = body;
        if (_bodyMech.TryGetValue(body, out int mi)) _selMech = mi;
        Highlight();
    }

    void Highlight()
    {
        foreach (var (body, (rail, area, normal)) in _bodies)
        {
            bool sel = ReferenceEquals(body, _selBody);
            rail.Background = sel ? Palette.Accent : normal;
            area.Background = sel ? BlockColors.SelectedBody : Brushes.Transparent;
        }
    }

    // ---------------- Palette ----------------

    Control BuildPalette()
    {
        var stack = new StackPanel { Margin = new Thickness(10, 10, 10, 24), Spacing = 4 };
        void Section(string title) => stack.Children.Add(new TextBlock
        {
            Text = BlockText(title), FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 1,
            Foreground = Palette.TextDim, Margin = new Thickness(2, 10, 0, 2)
        });
        void Add(string id, BlockLook look)
        {
            var label = BlockEdits.StatementKinds.First(k => k.Id == id).Label;
            var tip = Printer.Print(BlockEdits.NewStatement(id), 0).TrimEnd();
            stack.Children.Add(PaletteItem(label, look, tip, id, _ => AddStatement(id)));
        }

        // MY COMMANDS first: the commands the person made are the blocks they reach for most.
        Section("MY COMMANDS");
        stack.Children.Add(_myCommands);
        Section("EVENTS");
        stack.Children.Add(PaletteItem("when ▾", BlockColors.Event, "when start / tick / touched by / told / key / a signal (drag it to place a 'when start')", "when", item =>
        {
            var menu = new MenuFlyout();
            foreach (var k in BlockEdits.EventKinds)
            {
                var mi = new MenuItem { Header = BlockText("when " + BlockEdits.EventLabel(k)) };
                mi.Click += (_, _) => AddWhen(k);
                menu.Items.Add(mi);
            }
            FlyoutBase.SetAttachedFlyout(item, menu);
            FlyoutBase.ShowAttachedFlyout(item);
        }));
        Add("wait", BlockColors.Event);
        Add("waituntil", BlockColors.Event);
        Add("waitfor", BlockColors.Event);
        Add("waitkey", BlockColors.Event);
        Section("CONTROL");
        Add("if", BlockColors.Control);
        Add("ifelse", BlockColors.Control);
        Add("repeat", BlockColors.Control);
        Add("forever", BlockColors.Control);
        Add("while", BlockColors.Control);
        Add("foreach", BlockColors.Control);
        Add("stop", BlockColors.Control);
        Section("AT THE SAME TIME");
        Add("together", BlockColors.Parallel);
        Add("run", BlockColors.Parallel);
        Section("MESSAGES");
        Add("emit", BlockColors.Message);
        Add("tell", BlockColors.Message);
        Section("VARIABLES");
        Add("set", BlockColors.Command);
        Add("change", BlockColors.Command);
        Section("LISTS");
        Add("listadd", BlockColors.List);
        Add("listremove", BlockColors.List);
        Section("COMMANDS");
        foreach (var id in new[] { "print", "play", "spawn", "destroy", "move", "goto" }) Add(id, BlockColors.Command);
        Section("MECHANISM");
        stack.Children.Add(PaletteItem("param", BlockColors.Setting, "param speed: number = 100", "param", _ => AddParam()));
        stack.Children.Add(PaletteItem("uses", BlockColors.Setting, "uses Health", "uses", _ => AddUses()));
        stack.Children.Add(new TextBlock
        {
            Text = BlockText("Click a block to add it to the selected place (the orange rail), or drag it to the exact spot."), FontSize = 11,
            Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 12, 0, 0)
        });

        return new Border
        {
            Width = 152, Background = Palette.Bg1, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
    }

    Border PaletteItem(string label, BlockLook look, string tip, string dragId, Action<Border> click) =>
        PaletteItem(new TextBlock { Text = BlockText(label), Foreground = look.Fg, FontWeight = FontWeight.SemiBold, FontSize = 12.5 }, look, tip, dragId, click);

    Border PaletteItem(Control content, BlockLook look, string tip, string dragId, Action<Border> click)
    {
        var b = new Border
        {
            Background = look.Bg, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
            Cursor = new Cursor(StandardCursorType.Hand), Child = content
        };
        ToolTip.SetTip(b, BlockText(tip));
        b.PointerEntered += (_, _) => b.Opacity = 0.8;
        b.PointerExited += (_, _) => b.Opacity = 1;
        // Clicking adds the block where the orange rail is (on release); dragging places it exactly.
        b.PointerPressed += (_, e) => Press(e, new DragSrc(DragKind.Palette, null, -1, dragId, b, () => click(b)));
        _paletteItems[dragId] = b;
        return b;
    }

    /// <summary>A new statement for a palette id: a built-in block, or a call to a user command ("call:name").</summary>
    Stmt NewBlock(string id) => _paletteFactories.TryGetValue(id, out var make) ? make() : BlockEdits.NewStatement(id);

    void AddStatement(string id)
    {
        if (Locked || _defs.Count == 0) return;
        var body = _selBody;
        if (id == "return")
        {
            // 'return' only goes inside a 'to': use the selected one, or the mechanism's first.
            var m = _defs[MechIndex()];
            if (body == null || !_bodies.ContainsKey(body) || !BlockEdits.InProc(m, body)) body = m.Procs.Count > 0 ? m.Procs[0].Body : null;
            if (body == null) return;
        }
        if (body == null || !_bodies.ContainsKey(body))
        {
            var w = BlockEdits.NewWhen(EventKind.Start);
            _defs[MechIndex()].Whens.Add(w);
            body = w.Body;
        }
        var s = NewBlock(id);
        body.Add(s);
        // The place you were adding into stays selected, so several clicks add blocks one after another.
        _selBody = body;
        _reveal = s;
        Commit();
    }

    void AddWhen(EventKind k)
    {
        if (Locked || _defs.Count == 0) return;
        var w = BlockEdits.NewWhen(k);
        _defs[MechIndex()].Whens.Add(w);
        _selBody = w.Body;
        _reveal = w;
        Commit();
    }

    void AddParam()
    {
        if (Locked || _defs.Count == 0) return;
        var m = _defs[MechIndex()];
        var p = BlockEdits.NewParam(m);
        m.Params.Add(p);
        _reveal = p;
        Commit();
    }

    void AddUses()
    {
        if (Locked || _defs.Count == 0) return;
        var u = BlockEdits.NewUses();
        _defs[MechIndex()].Uses.Add(u);
        _reveal = u;
        Commit();
    }

    // ---------------- Building blocks ----------------

    static TextBlock Note(string text) => new()
    {
        Text = BlocksView.BlockText(text), Foreground = Palette.TextDim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4, 3), VerticalAlignment = VerticalAlignment.Center
    };

    static TextBlock Word(string text, IBrush fg) => new()
    {
        Text = BlockText(text), Foreground = fg, FontWeight = FontWeight.SemiBold, FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0)
    };

    static WrapPanel Parts(params Control[] items)
    {
        var w = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in items) w.Children.Add(c);
        return w;
    }

    void Mark(object item, Control c)
    {
        if (ReferenceEquals(item, _reveal)) _revealControl = c;
    }

    static Exception Bad(string message) => new ScriptError(message, 0);

    Field NameField(string value, bool dotted, Action<string> set, string placeholder = "name") => new(value, text =>
    {
        string? err = BlockEdits.CheckName(text, dotted);
        if (err != null) throw Bad(err);
        text = text.Trim();
        set(text);
        Commit(rebuild: false);
        return text;
    }, placeholder);

    Field ExprField(Expr? value, Action<Expr?> set, string placeholder = "value", bool optional = false) => new(value == null ? "" : Printer.Print(value), text =>
    {
        Expr? e = optional && text.Trim().Length == 0 ? null : Parser.ParseExpression(text);
        set(e);
        Commit(rebuild: false);
        return e == null ? "" : Printer.Print(e);
    }, placeholder);

    Field ListField(List<Expr> values, Action<List<Expr>> set, string placeholder = "values") => new(Printer.Args(values), text =>
    {
        var list = Parser.ParseExpressionList(text);
        set(list);
        Commit(rebuild: false);
        return Printer.Args(list);
    }, placeholder);

    /// <summary>A small button that opens a menu of choices (operator, event kind).</summary>
    static Button ChoiceButton(string text, BlockLook look, IEnumerable<(string Label, Action Pick)> choices)
    {
        var b = new Button
        {
            Content = text, FontSize = 12.5, FontFamily = Palette.Mono, Padding = new Thickness(7, 1), MinHeight = 22,
            Background = BlockColors.PillBg, Foreground = BlockColors.PillText, BorderBrush = BlockColors.PillBorder,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Margin = new Thickness(2, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        var menu = new MenuFlyout();
        foreach (var (label, pick) in choices)
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => pick();
            menu.Items.Add(mi);
        }
        b.Flyout = menu;
        return b;
    }

    BlockOps Ops<T>(List<T> list, int i, Action? duplicate, string? extraText = null, string? extraTip = null, Action? extra = null) => new(
        i > 0 ? () => { BlockEdits.Move(list, i, -1); Commit(); } : null,
        i < list.Count - 1 ? () => { BlockEdits.Move(list, i, 1); Commit(); } : null,
        duplicate,
        () => { BlockEdits.Delete(list, i); Commit(); },
        extraText, extraTip, extra);

    /// <summary>One coloured row; its tools (move, duplicate, delete) appear on hover.</summary>
    Border Row(BlockLook look, Control content, BlockOps? ops, CornerRadius radius, Action click, Func<Control, DragSrc>? drag = null)
    {
        var dock = new DockPanel { MinHeight = 24 };
        dock.Children.Add(content);
        var row = new Border { Background = look.Bg, CornerRadius = radius, Padding = new Thickness(8, 3, 4, 3), Child = dock };
        var stmt = _rowStmt; // a statement's head row gets the ⋯ menu (Pause here)
        _rowStmt = null;
        if (ops != null)
        {
            StackPanel? tools = null;
            row.PointerEntered += (_, _) =>
            {
                tools ??= Tools(look, ops, dock, stmt == null ? null : p => AddMoreTool(p, look, stmt));
                tools.IsVisible = true;
            };
            row.PointerExited += (_, _) => { if (tools != null) tools.IsVisible = false; };
        }
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            click();
            if (drag != null) Press(e, drag(row)); // moving the pointer far enough starts a drag
        };
        return row;
    }

    static StackPanel Tools(BlockLook look, BlockOps ops, DockPanel dock, Action<StackPanel>? more = null)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Top };
        void Tool(string text, string tip, Action? act)
        {
            if (act == null) return;
            var b = new Button
            {
                Content = text, FontSize = 11, Padding = new Thickness(6, 0), Height = 22, MinHeight = 0,
                Background = Brushes.Transparent, Foreground = look.Fg, BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4), VerticalContentAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(b, BlockText(tip));
            b.Click += (_, e) => { e.Handled = true; act(); };
            p.Children.Add(b);
        }
        if (ops.Extra != null) Tool(ops.ExtraText!, ops.ExtraTip!, ops.Extra);
        more?.Invoke(p);
        Tool("↑", "Move up", ops.Up);
        Tool("↓", "Move down", ops.Down);
        Tool("⧉", "Duplicate", ops.Duplicate);
        Tool("✕", "Delete", ops.Delete);
        DockPanel.SetDock(p, Dock.Right);
        dock.Children.Insert(0, p);
        return p;
    }

    /// <summary>
    /// The inside of a C-shaped block: a coloured rail on the left and the child blocks. A parallel rail ('together')
    /// is split in two lengthwise, so the blocks beside it read as running side by side.
    /// </summary>
    Control Body(List<Stmt> body, BlockLook look, int mi, bool parallel = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(parallel ? "18,*" : "14,*") };
        var rail = new Border { Background = look.Bg };
        if (parallel)
            rail.Child = new Border { Width = 3, Background = BlockColors.PillBg, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2), CornerRadius = new CornerRadius(1.5) };
        var stack = new StackPanel();
        var area = new Border { Padding = new Thickness(6, 3, 0, 3), Background = Brushes.Transparent, Child = stack, MinHeight = 26 };
        Grid.SetColumn(area, 1);
        grid.Children.Add(rail);
        grid.Children.Add(area);
        var items = new List<Control>();
        int depth = _buildDepth++;
        for (int j = 0; j < body.Count; j++) { var c = StmtBlock(body, j, mi); items.Add(c); stack.Children.Add(c); }
        _buildDepth--;
        _stmtSlots.Add(new StmtSlot(body, area, items, mi, depth));
        if (body.Count == 0) stack.Children.Add(Note("Empty — drag a block here, or click here and pick one in the palette."));
        area.PointerPressed += (_, e) => { e.Handled = true; Select(body); };
        _bodies[body] = (rail, area, look.Bg);
        _bodyMech[body] = mi;
        return grid;
    }

    static Border Foot(BlockLook look) => new() { Background = look.Bg, Height = 10, CornerRadius = new CornerRadius(0, 6, 6, 6) };

    static readonly CornerRadius HeadRadius = new(6, 6, 6, 0), MidRadius = new(0, 6, 6, 0), RowRadius = new(6);

    Control MechanismBlock(int mi)
    {
        var m = _defs[mi];
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };

        var title = Parts(Word(m.IsSystem ? "system" : "mechanism", Palette.AccentText), NameField(m.Name, false, n =>
        {
            if (_defs.Where((d, k) => k != mi).Any(d => d.Name == n)) throw Bad($"There's already a mechanism called '{n}'.");
            if (n == _defs[mi].Base) throw Bad($"'{n}' can't come from itself.");
            _defs[mi] = _defs[mi] with { Name = n };
        }));
        if (m.Base != null) // a variant: 'mechanism RedSlime from Slime'
            title.Children.Add(Parts(Word("from", Palette.AccentText), NameField(m.Base, false, n =>
            {
                if (n == _defs[mi].Name) throw Bad($"'{n}' can't come from itself.");
                _defs[mi] = _defs[mi] with { Base = n };
            }, "mechanism")));
        var header = new Border { Background = Palette.Accent, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), Child = title, Margin = new Thickness(0, 0, 0, 6) };
        header.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _selMech = mi;
            _selBody = DefaultBody();
            Highlight();
        };
        panel.Children.Add(header);
        if (m.IsSystem) panel.Children.Add(IncludesPanel(mi)); // a system's 'includes' rows (BlocksView.Systems.cs)
        var paramsPanel = new StackPanel();
        var usesPanel = new StackPanel();
        var whensPanel = new StackPanel();
        panel.Children.Add(paramsPanel);
        panel.Children.Add(usesPanel);

        var ps = m.Params;
        for (int i = 0; i < ps.Count; i++)
        {
            int k = i;
            var p = ps[k];
            var row = Row(BlockColors.Setting, Parts(
                Word("param", BlockColors.Setting.Fg),
                NameField(p.Name, false, n =>
                {
                    if (ps.Where((x, q) => q != k).Any(x => x.Name == n)) throw Bad($"The param '{n}' already exists.");
                    ps[k] = ps[k] with { Name = n };
                }),
                Word(":", BlockColors.Setting.Fg),
                new Field(Printer.ParamType(p), text =>
                {
                    // A name (number, Object, Path...), 'List of number', or 'Choice "easy", "hard"'.
                    string t = text.Trim();
                    if (t.Length == 0) ps[k] = ps[k] with { Type = "any", Options = null };
                    else
                    {
                        var (type, options) = Parser.ParseTypeText(t);
                        if (BlockEdits.CheckName(ParamTypes.Base(type)) is { } err) throw Bad(err);
                        ps[k] = ps[k] with { Type = type, Options = options };
                    }
                    Commit(rebuild: false);
                    return Printer.ParamType(ps[k]);
                }, "any type"),
                Word("=", BlockColors.Setting.Fg),
                ExprField(p.Default, e => ps[k] = ps[k] with { Default = e }, "no default", optional: true)),
                Ops(ps, k, null), RowRadius, () => { _selMech = mi; }, r => new DragSrc(DragKind.Param, ps, k, null, r));
            row.Margin = new Thickness(0, 0, 0, 3);
            Mark(p, row);
            paramsPanel.Children.Add(row);
        }

        var us = m.Uses;
        for (int i = 0; i < us.Count; i++)
        {
            int k = i;
            var u = us[k];
            var row = Row(BlockColors.Setting, Parts(
                Word("uses", BlockColors.Setting.Fg),
                NameField(u.Name, false, n => us[k] = us[k] with { Name = n }, "mechanism"),
                new Field(Printer.UsesTail(u), text =>
                {
                    var (pos, args) = BlockEdits.ParseUsesTail(text);
                    us[k] = us[k] with { Positional = pos, Args = args };
                    Commit(rebuild: false);
                    return Printer.UsesTail(us[k]);
                }, "settings")),
                Ops(us, k, null), RowRadius, () => { _selMech = mi; }, r => new DragSrc(DragKind.Uses, us, k, null, r));
            row.Margin = new Thickness(0, 0, 0, 3);
            Mark(u, row);
            usesPanel.Children.Add(row);
        }

        if (ps.Count + us.Count > 0 && m.Whens.Count + m.Procs.Count > 0) panel.Children.Add(new Border { Height = 8 });
        for (int i = 0; i < m.Whens.Count; i++) whensPanel.Children.Add(WhenBlock(m.Whens, i, mi));
        panel.Children.Add(whensPanel);
        var procsPanel = new StackPanel();
        for (int i = 0; i < m.Procs.Count; i++) procsPanel.Children.Add(ProcBlock(m.Procs, i, mi));
        panel.Children.Add(procsPanel);
        _mechSlots.Add(new MechSlot(panel, paramsPanel, usesPanel, whensPanel, procsPanel));
        if (ps.Count + us.Count + m.Whens.Count + m.Procs.Count == 0) panel.Children.Add(Note("Empty mechanism — add a 'when' from the palette."));
        return panel;
    }

    Control WhenBlock(List<WhenDef> whens, int i, int mi)
    {
        var w = whens[i];
        var look = BlockColors.Event;
        var ev = w.Event;
        var parts = Parts(Word("when", look.Fg), ChoiceButton(BlockText(BlockEdits.EventLabel(ev.Kind)) + " ▾", look,
            BlockEdits.EventKinds.Select(k => (BlockText(BlockEdits.EventLabel(k)), (Action)(() =>
            {
                if (whens[i].Event.Kind == k) return;
                whens[i] = whens[i] with { Event = BlockEdits.DefaultEvent(k) };
                Commit();
            })))));
        if (ev.Kind is not (EventKind.Start or EventKind.Tick))
        {
            string placeholder = ev.Kind switch
            {
                EventKind.Touched => "anything",
                EventKind.Told => "\"message\" value",
                EventKind.Key => "\"space\"",
                _ => "signal"
            };
            parts.Children.Add(new Field(Printer.EventTail(ev), text =>
            {
                var spec = BlockEdits.ParseEvent(whens[i].Event.Kind, text);
                whens[i] = whens[i] with { Event = spec };
                Commit(rebuild: false);
                return Printer.EventTail(spec);
            }, placeholder));
        }
        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        outer.Children.Add(Row(look, parts, Ops(whens, i, () => { BlockEdits.Duplicate(whens, i); Commit(); }), HeadRadius, () => Select(w.Body),
            _ => new DragSrc(DragKind.When, whens, i, null, outer)));
        outer.Children.Add(Body(w.Body, look, mi));
        outer.Children.Add(Foot(look));
        Mark(w, outer);
        return outer;
    }

    Control StmtBlock(List<Stmt> list, int j, int mi)
    {
        var s = list[j];
        Action dup = () => { BlockEdits.Duplicate(list, j); Commit(); };
        _rowStmt = s;
        Control block = s switch
        {
            IfStmt i => IfBlock(list, j, i, mi, dup),
            RepeatStmt r => RepeatBlock(list, j, r, mi, dup),
            TogetherStmt or RunStmt or ForEachStmt or WhileStmt => NewCBlock(list, j, s, mi, dup),
            _ => SimpleBlock(list, j, s, dup, mi)
        };
        block.Margin = new Thickness(0, 0, 0, 3);
        _rowStmt = null;
        block = DebugDecorate(s, block); // breakpoint dot, the highlighted line (BlocksView.Debug.cs)
        Mark(s, block);
        return block;
    }

    Control IfBlock(List<Stmt> list, int j, IfStmt s, int mi, Action dup)
    {
        var look = BlockColors.Control;
        var parts = Parts(Word("if", look.Fg), ExprField(s.Cond, e => list[j] = ((IfStmt)list[j]) with { Cond = e! }, "condition"));
        BlockOps ops = s.Else == null
            ? Ops(list, j, dup, "+ else", "Add an 'else' part", () => { list[j] = ((IfStmt)list[j]) with { Else = [] }; Commit(); })
            : s.Else.Count == 0
                ? Ops(list, j, dup, "− else", "Remove the empty 'else' part", () => { list[j] = ((IfStmt)list[j]) with { Else = null }; Commit(); })
                : Ops(list, j, dup);
        var outer = new StackPanel();
        outer.Children.Add(Row(look, parts, ops, HeadRadius, () => Select(s.Then), _ => new DragSrc(DragKind.Stmt, list, j, null, outer)));
        outer.Children.Add(Body(s.Then, look, mi));
        if (s.Else != null)
        {
            var elseBody = s.Else;
            outer.Children.Add(Row(look, Parts(Word("else", look.Fg)), null, MidRadius, () => Select(elseBody)));
            outer.Children.Add(Body(elseBody, look, mi));
        }
        outer.Children.Add(Foot(look));
        return outer;
    }

    Control RepeatBlock(List<Stmt> list, int j, RepeatStmt s, int mi, Action dup)
    {
        var look = BlockColors.Control;
        var parts = s.Count == null
            ? Parts(Word("repeat forever", look.Fg))
            : Parts(Word("repeat", look.Fg), ExprField(s.Count, e => list[j] = ((RepeatStmt)list[j]) with { Count = e! }, "times"), Word("times", look.Fg));
        var outer = new StackPanel();
        outer.Children.Add(Row(look, parts, Ops(list, j, dup), HeadRadius, () => Select(s.Body), _ => new DragSrc(DragKind.Stmt, list, j, null, outer)));
        outer.Children.Add(Body(s.Body, look, mi));
        outer.Children.Add(Foot(look));
        return outer;
    }

    Control SimpleBlock(List<Stmt> list, int j, Stmt s, Action dup, int mi)
    {
        BlockLook look;
        WrapPanel parts;
        if (NewSimpleBlock(list, j, s, mi) is { } made) (look, parts) = made;
        else switch (s)
        {
            case AssignStmt a:
            {
                look = BlockColors.Command;
                var op = ChoiceButton(a.Op, look, new[] { "=", "+=", "-=", "*=", "/=" }.Select(o => (o, (Action)(() =>
                {
                    list[j] = ((AssignStmt)list[j]) with { Op = o };
                    Commit();
                }))));
                parts = Parts(Word(a.Op == "=" ? "set" : "change", look.Fg),
                    NameField(string.Join('.', a.Target), true, t => list[j] = ((AssignStmt)list[j]) with { Target = t.Split('.') }, "variable"),
                    op,
                    ExprField(a.Value, e => list[j] = ((AssignStmt)list[j]) with { Value = e! }));
                break;
            }
            case WaitStmt w:
                look = BlockColors.Event;
                parts = Parts(Word("wait", look.Fg), ExprField(w.Seconds, e => list[j] = ((WaitStmt)list[j]) with { Seconds = e! }, "seconds"), Word("seconds", look.Fg));
                break;
            case StopStmt:
                look = BlockColors.Control;
                parts = Parts(Word("stop", look.Fg));
                break;
            case EmitStmt em:
                look = BlockColors.Message;
                parts = Parts(Word("emit", look.Fg),
                    NameField(em.Signal, false, n => list[j] = ((EmitStmt)list[j]) with { Signal = n }, "signal"),
                    ListField(em.Args, l => list[j] = ((EmitStmt)list[j]) with { Args = l }));
                break;
            case TellStmt t:
                look = BlockColors.Message;
                parts = Parts(Word("tell", look.Fg),
                    ExprField(t.Target, e => list[j] = ((TellStmt)list[j]) with { Target = e! }, "who"),
                    ExprField(t.Message, e => list[j] = ((TellStmt)list[j]) with { Message = e! }, "message"),
                    ListField(t.Args, l => list[j] = ((TellStmt)list[j]) with { Args = l }));
                break;
            case CommandStmt c:
                look = c.Name is "add" or "remove" ? BlockColors.List : BlockColors.Command;
                parts = CommandParts(list, j, c, look);
                break;
            default:
                look = BlockColors.Command;
                parts = Parts(Word(Printer.Simple(s), look.Fg));
                break;
        }
        return Row(look, parts, Ops(list, j, dup), RowRadius, () => Select(list), r => new DragSrc(DragKind.Stmt, list, j, null, r));
    }
}
