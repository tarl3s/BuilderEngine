using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// DEBUGGER (editor side; the debugger itself is BuilderEngine.Script/Debugger.cs). While playing, the right column is
// the Debug panel: pause / resume / step one frame, the object clicked in the game (its mechanisms, every variable —
// editable while paused — and its running 'when's with their line), the global variables, the breakpoints, and the
// event timeline (filterable by object). Clicking a running 'when', an error or the "paused at" line opens that
// mechanism with the line marked in the code (gutter arrow) and outlined in the blocks. Breakpoints: click a line
// number in the code (DebuggerGutter.cs) or a block's ⋯ → "Pause here"; they stay for the whole session.
public sealed partial class MainWindow
{
    readonly Debugger _debugger = new();
    DebuggerGutter? _gutter;
    DispatcherTimer? _debugTimer;
    long _debugShown = -1;
    GameObject? _debugObject;
    bool _debugOnlyObject;
    /// <summary>The line opened from the panel (marked in the gutter and the blocks while that mechanism is open).</summary>
    string? _debugLineMech;
    int _debugLine;

    static readonly IBrush DebugEventDim = new SolidColorBrush(Color.Parse("#7A7A7A")).ToImmutable();

    // ---------------- Code view: line numbers and breakpoints ----------------

    /// <summary>The code editor with the line-number gutter as its left margin (called once by the shell).</summary>
    Control CodeWithGutter(CodeEditor code)
    {
        _gutter = new DebuggerGutter(code)
        {
            HasBreakpoint = line => _current != null && _debugger.HasBreakpoint(_current, line),
            LineClicked = ToggleBreakpointAt,
            CurrentLine = () => _current != null && string.Equals(_current, _debugLineMech, StringComparison.OrdinalIgnoreCase) ? _debugLine : 0,
        };
        // Typing moves lines around: the marked line no longer means anything.
        code.TextChanged += (_, _) =>
        {
            if (_loading || _debugLine == 0) return;
            _debugLine = 0;
            _blocksView?.HighlightLine(0);
            _gutter?.Refresh();
        };
        Dispatcher.UIThread.Post(() =>
        {
            if (_blocksView == null) return;
            _blocksView.BreakpointAt = line => _current != null && _debugger.HasBreakpoint(_current, line);
            _blocksView.ToggleBreakpoint = ToggleBreakpointAt;
        });
        return code; // the gutter lives inside the editor (TextArea.LeftMargins)
    }

    /// <summary>The lines of a mechanism that hold a statement (where a breakpoint can stop), or null when it doesn't parse.</summary>
    HashSet<int>? StatementLines(string mechanism)
    {
        if (RawDefOf(mechanism, out _) is not { } def) return null;
        var lines = new HashSet<int>();
        void Walk(List<Stmt> body)
        {
            foreach (var s in body)
            {
                lines.Add(s.Line);
                foreach (var inner in BlockEdits.Bodies(s)) Walk(inner);
            }
        }
        foreach (var w in def.Whens) Walk(w.Body);
        foreach (var p in def.Procs) Walk(p.Body);
        return lines;
    }

    /// <summary>A click on a line number (or "Pause here" on a block): turns that line's breakpoint on or off.</summary>
    internal void ToggleBreakpointAt(int line)
    {
        if (_current == null) return;
        if (!_debugger.HasBreakpoint(_current, line) && StatementLines(_current) is { } lines && !lines.Contains(line))
        {
            SetStatus(false, $"Line {line} has nothing that runs: pick a line inside a 'when' or a 'to'.");
            return;
        }
        bool on = _debugger.ToggleBreakpoint(_current, line);
        _gutter?.Refresh();
        _blocksView?.RefreshDebugMarks();
        SetStatus(true, on ? $"The game will pause at line {line} of {_current} while playing (click the number again to remove it)."
            : $"Line {line} of {_current} won't pause the game any more.");
    }

    /// <summary>Opens a mechanism with one line marked: the gutter arrow, the line selected, its block outlined.</summary>
    internal void OpenMechanismAt(string mechanism, int line)
    {
        if (!_sources.ContainsKey(mechanism)) { SetStatus(false, $"There's no mechanism called “{mechanism}” to open."); return; }
        if (_current != mechanism || _sceneTab) OpenMechanism(mechanism);
        _debugLineMech = mechanism;
        _debugLine = line;
        _blocksView?.HighlightLine(line, mechanism);
        Dispatcher.UIThread.Post(() =>
        {
            SelectCodeLine(line);
            _gutter?.ScrollToLine(line);
        }, DispatcherPriority.Loaded);
        SetStatus(true, $"{mechanism}, line {line}.");
    }

    void SelectCodeLine(int line)
    {
        _code.SelectLine(line);
        _gutter?.Refresh();
    }

    // ---------------- Attaching to the game ----------------

    /// <summary>A world is about to play (Play, or the next scene): the debugger watches it.</summary>
    void DebugAttach(World world)
    {
        if (_debugger.World == null) _debugger.ClearEvents(); // a new Play starts a new timeline; a scene change keeps it
        _debugger.Attach(world);
        _debugObject = null;
    }

    void DebugSurface(PlaySurface surface)
    {
        surface.Debugger = _debugger;
        surface.ObjectClicked = g =>
        {
            _debugObject = g;
            surface.Inspected = g;
            RebuildInspector();
        };
        _debugger.Changed -= OnDebuggerChanged;
        _debugger.Changed += OnDebuggerChanged;
        if (_debugTimer == null)
        {
            _debugTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _debugTimer.Tick += (_, _) =>
            {
                if (_play == null) { _debugTimer.Stop(); return; }
                // Running: refresh the live values. Paused: only when something changed (so a field being edited keeps its focus).
                if (!_debugger.Paused || _debugger.Version != _debugShown) RebuildInspector();
            };
        }
        _debugTimer.Start();
    }

    void DebugStop()
    {
        _debugTimer?.Stop();
        _debugger.Changed -= OnDebuggerChanged;
        _debugger.Detach();
        _debugObject = null;
    }

    void OnDebuggerChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_play == null) return;
        RebuildInspector();
        _play.InvalidateVisual();
        if (_debugger.PausedAt is { } at) SetStatus(true, $"Paused at {at}. Resume (F5) or Step (F10) in the Debug panel; click the line there to open it.");
    });

    // ---------------- The panel ----------------

    void DebugPanel()
    {
        _debugShown = _debugger.Version;
        var w = _play!.World;
        var root = _inspectorRoot;
        bool paused = _debugger.Paused;

        var head = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var pause = paused ? AccentButton("▶  Resume") : PlainButton("⏸  Pause");
        pause.Padding = new Thickness(10, 4);
        ToolTip.SetTip(pause, paused ? "Let the game run again (F5)" : "Freeze the game to look inside it (F5)");
        pause.Click += (_, _) => { if (_debugger.Paused) _debugger.Resume(); else _debugger.Pause(); _play?.Focus(); };
        var step = PlainButton("⏭  Step");
        step.Padding = new Thickness(10, 4);
        ToolTip.SetTip(step, "Run exactly one frame, then stay paused (F10)");
        step.Click += (_, _) => _debugger.StepFrame();
        buttons.Children.Add(pause);
        buttons.Children.Add(step);
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(Label("DEBUG"));
        root.Children.Add(head);

        if (_debugger.PausedAt is { } at)
        {
            var link = DebugLink($"⏸ Paused at {at.Source}, line {at.Line} ({at.Object.Name}) — open", Palette.Accent, () => OpenMechanismAt(at.Source, at.Line));
            root.Children.Add(link);
            _debugObject ??= at.Object;
        }
        else
            root.Children.Add(Note(paused ? string.Create(CultureInfo.InvariantCulture, $"⏸ Paused at {w.Time:0.00} s.") : string.Create(CultureInfo.InvariantCulture, $"● Running · {w.Time:0.0} s · {w.Objects.Count} objects. Click an object in the game to look inside it."),
                paused ? Palette.Accent : Palette.TextDim));
        if (_play.Inspected != _debugObject) _play.Inspected = _debugObject;

        // ---- The inspected object ----
        DebugSection("OBJECT");
        if (_debugObject is not { } o || o.Destroyed)
            root.Children.Add(Note(_debugObject?.Destroyed == true ? $"{_debugObject.Name} is gone (destroyed)." : "Click an object in the game to see its variables and the 'when's running in it.", Palette.TextDim));
        else DebugObject(w, o, paused);

        // ---- Globals ----
        DebugSection("GLOBALS");
        if (w.Globals.Count == 0) root.Children.Add(Note("No global variables yet.", Palette.TextDim));
        else root.Children.Add(VarsTable(w.Globals, paused));

        // ---- Breakpoints ----
        var bps = _debugger.Breakpoints();
        DebugSection("BREAKPOINTS");
        if (bps.Count == 0)
            root.Children.Add(Note("Click a line number in the code, or a block's ⋯ → Pause here: the game pauses when it gets there.", Palette.TextDim));
        foreach (var (mech, line) in bps)
        {
            var row = new DockPanel();
            var x = LinkButton("✕", "Remove this breakpoint");
            x.Foreground = Palette.TextDim;
            x.Click += (_, _) => { _debugger.SetBreakpoint(mech, line, false); _gutter?.Refresh(); _blocksView?.RefreshDebugMarks(); };
            DockPanel.SetDock(x, Dock.Right);
            row.Children.Add(x);
            row.Children.Add(DebugLink($"● {mech} · line {line}", Palette.Text, () => OpenMechanismAt(mech, line)));
            root.Children.Add(row);
        }

        // ---- Events ----
        var evHead = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var all = SmallToggle("All", !_debugOnlyObject, () => { _debugOnlyObject = false; RebuildInspector(); });
        var mine = SmallToggle("This object", _debugOnlyObject, () => { _debugOnlyObject = true; RebuildInspector(); });
        mine.IsEnabled = _debugObject != null;
        filters.Children.Add(all);
        filters.Children.Add(mine);
        DockPanel.SetDock(filters, Dock.Right);
        evHead.Children.Add(filters);
        evHead.Children.Add(Label("EVENTS"));
        root.Children.Add(evHead);
        int id = _debugOnlyObject && _debugObject != null ? _debugObject.Id : 0;
        var events = _debugger.EventsFor(id, 80);
        if (events.Count == 0) root.Children.Add(Note(id == 0 ? "Nothing yet: touches, signals, messages, keys, scene changes and errors show up here." : "Nothing about this object yet.", Palette.TextDim));
        var list = new StackPanel { Spacing = 1 };
        foreach (var e in events) list.Children.Add(EventRow(e));
        root.Children.Add(list);
    }

    void DebugSection(string title)
    {
        _inspectorRoot.Children.Add(new Border { Height = 1, Background = Palette.Border, Margin = new Thickness(0, 4, 0, 0) });
        _inspectorRoot.Children.Add(Label(title));
    }

    void DebugObject(World w, GameObject o, bool paused)
    {
        var root = _inspectorRoot;
        string tags = o.Tags.Count > 0 ? "  ·  tags: " + string.Join(", ", o.Tags) : "";
        root.Children.Add(new TextBlock { Text = o.Name, FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = Palette.Text });
        if (paused)
        {
            var nx = Num(o.X, 8, double.MinValue, v => o.X = v);
            var ny = Num(o.Y, 8, double.MinValue, v => o.Y = v);
            root.Children.Add(Pair(Field("x", nx), Field("y", ny)));
            if (tags.Length > 0) root.Children.Add(Note(tags.TrimStart(' ', '·'), Palette.TextDim));
        }
        else
            root.Children.Add(Note(string.Create(CultureInfo.InvariantCulture, $"x {o.X:0.#}  y {o.Y:0.#}  ·  speed {o.Vx:0.#}, {o.Vy:0.#}{tags}"), Palette.TextDim));

        foreach (var inst in o.AllMechanisms())
        {
            var body = new StackPanel { Spacing = 4 };
            string title = Variants.Label(inst.Def) + (inst.Parent != null ? $"  (used by {inst.Parent.Def.Name})" : "");
            body.Children.Add(DebugLink(title, Palette.Accent, () => OpenMechanism(inst.Def.Name), FontWeight.SemiBold));
            body.Children.Add(inst.Vars.Count == 0 ? Note("No variables.", Palette.TextDim) : VarsTable(inst.Vars, paused));
            root.Children.Add(new Border
            {
                Child = body, Background = Palette.Bg2, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 8, 8)
            });
        }
        if (o.Mechanisms.Count == 0) root.Children.Add(Note("No mechanisms.", Palette.TextDim));

        var running = w.Running(o);
        root.Children.Add(new TextBlock { Text = running.Count == 0 ? "Nothing running right now." : $"Running now ({running.Count}):", Foreground = Palette.TextDim, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        foreach (var r in running)
            root.Children.Add(DebugLink(r.Describe(), r.AtBreak ? Palette.Accent : Palette.Text, () => OpenMechanismAt(r.Source, r.Line)));
    }

    /// <summary>Name / value rows. Paused: numbers, yes/no and texts can be changed (the game uses the new value when it goes on).</summary>
    Control VarsTable(Dictionary<string, object?> bag, bool editable)
    {
        var panel = new StackPanel { Spacing = 3 };
        foreach (var (name, value) in bag.ToList())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
            var label = new TextBlock { Text = name, Foreground = Palette.TextDim, FontSize = 12, FontFamily = Palette.Mono, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(label, name);
            row.Children.Add(label);
            Control field = editable ? ValueEditor(bag, name, value) : new TextBlock
            {
                Text = Debugger.Show(value), FontFamily = Palette.Mono, FontSize = 12, Foreground = Palette.Text,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(field, 1);
            row.Children.Add(field);
            panel.Children.Add(row);
        }
        return panel;
    }

    Control ValueEditor(Dictionary<string, object?> bag, string name, object? value)
    {
        switch (value)
        {
            case double d:
                return Num(d, Math.Abs(d) < 10 && d != Math.Floor(d) ? 0.1 : 1, double.MinValue, v => bag[name] = v, "0.###");
            case bool b:
            {
                var c = new CheckBox { IsChecked = b, Content = b ? "true" : "false", Foreground = Palette.Text };
                c.IsCheckedChanged += (_, _) => { bag[name] = c.IsChecked == true; c.Content = c.IsChecked == true ? "true" : "false"; };
                return c;
            }
            case string s:
            {
                var t = new TextBox
                {
                    Text = s, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, FontSize = 12,
                    MinHeight = 26, Padding = new Thickness(6, 3), FontFamily = Palette.Mono
                };
                void Commit() => bag[name] = t.Text ?? "";
                t.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
                t.LostFocus += (_, _) => Commit();
                return t;
            }
            default:
                return new TextBlock
                {
                    Text = Debugger.Show(value), FontFamily = Palette.Mono, FontSize = 12, Foreground = Palette.TextDim,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                };
        }
    }

    Control EventRow(DebugEvent e)
    {
        bool error = e.Kind == DebugEventKind.Error;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(e.Time.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(6) + "  ") { Foreground = DebugEventDim, FontFamily = Palette.Mono });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run(e.KindLabel.PadRight(6) + " ")
        {
            Foreground = error ? Palette.Error : e.Kind == DebugEventKind.Paused ? Palette.Accent : Palette.TextDim, FontFamily = Palette.Mono, FontWeight = FontWeight.SemiBold
        });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run(e.Text) { Foreground = error ? Palette.Error : Palette.Text });
        if (e.Source == null || e.Line <= 0 || !_sources.ContainsKey(e.Source)) return text;
        var b = new Button
        {
            Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(b, $"Open {e.Source} at line {e.Line}");
        b.Click += (_, _) => OpenMechanismAt(e.Source, e.Line);
        return b;
    }

    static Button DebugLink(string text, IBrush color, Action click, FontWeight? weight = null)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = color, FontSize = 12, FontWeight = weight ?? FontWeight.Normal },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Cursor = new Cursor(StandardCursorType.Hand)
        };
        b.Click += (_, _) => click();
        return b;
    }

    static Button SmallToggle(string text, bool on, Action click)
    {
        var b = new Button { Content = text, FontSize = 11 };
        StyleTab(b, on);
        b.Padding = new Thickness(8, 2);
        b.Click += (_, _) => click();
        return b;
    }
}
