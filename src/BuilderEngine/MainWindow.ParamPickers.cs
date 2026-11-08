using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// PARAM PICKERS: the inspector fields for the visual param types (owner rule: values are picked, not typed).
//   Object  → a dropdown of the scene's objects + "pick in the scene" (click an object on the canvas)
//   Path    → points edited on the canvas (click to add, drag to move, right-click to remove), drawn when selected
//   List    → rows with ↑ ↓ ✕ and "+ Add", each row the item type's picker (List of number / Object / Image / Sound...)
//   Color   → swatches · Key → "press a key" · Choice → a dropdown of its options
// Values are saved in project.json as names, [[x, y], ...] lists, lists and texts (see Script/ParamTypes.cs).
public sealed partial class MainWindow
{
    /// <summary>Which Path param the canvas edits now: object id, mechanism, param (null = none).</summary>
    (int Obj, string Mech, string Param)? _pathEditing;
    PathOverlay? _pathOverlay;
    PickOverlay? _pickOverlay;

    static readonly string[] KeyChoices = ["space", "enter", "left", "right", "up", "down", "w", "a", "s", "d", "e", "q", "z", "x", "shift"];

    /// <summary>The field for a param of one of the visual types, or null for the types the inspector already knew.</summary>
    (Control Field, Action<bool> Show)? TypedParamField(SceneObject o, SceneMechanism m, ParamDef p, object? def, object? current, Button reset)
    {
        var kind = ParamTypes.Kind(p.Type);
        if (kind is not (ParamKind.Object or ParamKind.Path or ParamKind.List or ParamKind.Color or ParamKind.Key or ParamKind.Choice or ParamKind.Animation)) return null;
        string key = $"param{o.Id}.{m.Name}.{p.Name}";
        void Set(object? v, bool rebuild = false, string? undoKey = null)
        {
            Checkpoint(undoKey);
            m.Params[p.Name] = v;
            reset.IsVisible = true;
            _canvas.InvalidateVisual();
            if (rebuild) Dispatcher.UIThread.Post(RebuildInspector);
        }
        reset.Click += (_, _) =>
        {
            Checkpoint();
            m.Params.Remove(p.Name);
            _canvas.InvalidateVisual();
            Dispatcher.UIThread.Post(RebuildInspector);
        };
        Control field = kind switch
        {
            ParamKind.Object => ObjectPicker(ObjectName(current), o.Id, v => Set(v, rebuild: true)),
            ParamKind.Path => PathField(o, m, p, current as List<object?> ?? [], v => Set(v, rebuild: true)),
            ParamKind.List => ListEditor(o, ParamTypes.ItemType(p.Type), current as List<object?> ?? [], (v, rebuild) => Set(v, rebuild, rebuild ? null : key)),
            ParamKind.Color => ColorRow(World.ToText(current), c => Set(c, rebuild: true)),
            ParamKind.Key => KeyCapture(World.ToText(current), k => Set(k)),
            ParamKind.Animation => AnimationPicker(o, World.ToText(current), v => Set(v)), // MainWindow.Animations.cs
            _ => ChoicePicker(p.Options ?? [], World.ToText(current), c => Set(c)),
        };
        return (field, _ => { });
    }

    static string ObjectName(object? v) => v switch { GameObject g => g.Name, null => "", _ => World.ToText(v) };

    // ---------------- Object ----------------

    const string NoObject = "(none)";

    /// <summary>A dropdown of the scene's objects (+ "(none)") and a ⌖ button to pick one by clicking it in the scene.</summary>
    Control ObjectPicker(string value, int ownerId, Action<string> set)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
            FontSize = 13, MinWidth = 0
        };
        var names = _scene.Objects.Where(x => x.Id != ownerId).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var items = new List<string> { NoObject };
        string? match = names.FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
        if (match == null && value.Length > 0) items.Add($"{value}  (missing)");
        items.AddRange(names);
        _inspectorSync = true;
        combo.ItemsSource = items;
        combo.SelectedItem = match ?? (value.Length > 0 ? items[1] : NoObject);
        _inspectorSync = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (_inspectorSync || combo.SelectedItem is not string s || s.EndsWith("  (missing)")) return;
            set(s == NoObject ? "" : s);
        };
        var pick = new Button
        {
            Content = "⌖", FontSize = 15, Padding = new Thickness(8, 2), Margin = new Thickness(4, 0, 0, 0), MinHeight = 0,
            Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Stretch
        };
        ToolTip.SetTip(pick, "Pick in the scene: click an object (Esc cancels)");
        pick.Click += (_, _) => StartObjectPick(ownerId, picked => set(picked.Name));
        var row = new DockPanel();
        DockPanel.SetDock(pick, Dock.Right);
        row.Children.Add(pick);
        row.Children.Add(combo);
        return row;
    }

    /// <summary>The next click on the canvas picks an object for a param (instead of selecting it).</summary>
    void StartObjectPick(int ownerId, Action<SceneObject> picked)
    {
        _pickOverlay = new PickOverlay(this, ownerId, o =>
        {
            _pickOverlay = null;
            if (o != null && o.Id != ownerId) picked(o);
            else SetStatus(true, o == null ? "Picking cancelled." : "An object can't pick itself — use self in the code instead.");
            UpdateCanvasOverlay();
            _canvas.InvalidateVisual();
        });
        UpdateCanvasOverlay();
        _canvas.Focus();
        _canvas.InvalidateVisual();
        SetStatus(true, "Click an object in the scene to pick it (right-click or Esc cancels).");
    }

    /// <summary>The canvas tool for now: picking an object, the selected object's paths, or nothing.</summary>
    void UpdateCanvasOverlay()
    {
        if (_canvas is null) return;
        if (_play != null) { _canvas.Overlay = null; return; }
        if (_pickOverlay != null && Selected?.Id != _pickOverlay.OwnerId) _pickOverlay = null; // the selection moved on: cancel
        if (_pathEditing is { } pe && Selected?.Id != pe.Obj) _pathEditing = null;
        _pathOverlay ??= new PathOverlay(this);
        _canvas.Overlay = _pickOverlay != null ? (ICanvasOverlay)_pickOverlay : _tileOverlay != null ? _tileOverlay : Selected is { } o && PathParams(o).Any() ? _pathOverlay : null; // _tileOverlay: MainWindow.TilePaint.cs
        _canvas.InvalidateVisual();
    }

    sealed class PickOverlay(MainWindow w, int ownerId, Action<SceneObject?> done) : ICanvasOverlay
    {
        public int OwnerId => ownerId;
        static readonly IBrush Banner = new SolidColorBrush(Color.FromArgb(230, 42, 29, 23)).ToImmutable();

        public void Draw(DrawingContext ctx, SceneCanvas canvas)
        {
            var ft = new FormattedText("Click an object to pick it  ·  right-click or Esc cancels", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 13, Palette.Text);
            var r = new Rect((canvas.Bounds.Width - ft.Width) / 2 - 14, 12, ft.Width + 28, ft.Height + 12);
            ctx.DrawRectangle(Banner, new Pen(Palette.Accent, 1), r, 8, 8);
            ctx.DrawText(ft, new Point(r.X + 14, r.Y + 6));
        }

        public bool Press(Point world, bool right, KeyModifiers mods)
        {
            done(right ? null : w._canvas.ObjectAt(world));
            return true;
        }

        public void Drag(Point world, KeyModifiers mods) { }
        public void Release() { }
    }

    // ---------------- Path ----------------

    /// <summary>The Path params of an object's mechanisms (with the scene mechanism that holds their values).</summary>
    IEnumerable<(SceneMechanism M, ParamDef P)> PathParams(SceneObject o)
    {
        foreach (var m in o.Mechanisms)
            if (DefOf(m.Name, out _) is { } def)
                foreach (var p in def.Params)
                    if (ParamTypes.Kind(p.Type) == ParamKind.Path) yield return (m, p);
    }

    /// <summary>A Path's points as [x, y] lists (the saved value, or the default).</summary>
    static List<object?> PathPoints(SceneMechanism m, ParamDef p) =>
        (List<object?>)World.ConvertParam(m.Params.TryGetValue(p.Name, out var v) ? v : ParamDefault(p), "Path")!;

    Control PathField(SceneObject o, SceneMechanism m, ParamDef p, List<object?> points, Action<List<object?>> set)
    {
        bool editing = _pathEditing is { } pe && pe.Obj == o.Id && pe.Mech == m.Name && pe.Param == p.Name;
        var panel = new StackPanel { Spacing = 4 };
        var row = new DockPanel();
        var edit = editing ? MainWindow.AccentButton("✓ Done") : PlainButton("✎ Edit in scene");
        edit.Padding = new Thickness(10, 4);
        edit.FontSize = 12;
        ToolTip.SetTip(edit, editing ? "Stop editing the path" : "Click in the scene to add points, drag them to move, right-click one to remove it");
        edit.Click += (_, _) =>
        {
            _pathEditing = editing ? null : (o.Id, m.Name, p.Name);
            if (!editing && !m.Params.ContainsKey(p.Name)) { Checkpoint(); m.Params[p.Name] = PathPoints(m, p); } // edit a copy of the default
            RebuildInspector();
            _canvas.Focus();
        };
        var clear = LinkButton("Clear", "Remove every point");
        clear.IsEnabled = points.Count > 0;
        clear.Click += (_, _) => set([]);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(clear);
        buttons.Children.Add(edit);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(new TextBlock
        {
            Text = points.Count == 0 ? "no points" : points.Count == 1 ? "1 point" : $"{points.Count} points",
            Foreground = points.Count == 0 ? Palette.TextDim : Palette.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(row);
        if (editing)
            panel.Children.Add(Note("Click the scene to add a point · drag a point to move it · right-click a point to remove it (Alt = no snap).", Palette.Accent));
        return panel;
    }

    /// <summary>Draws the selected object's paths on the canvas and, while one is being edited, edits it with the mouse.</summary>
    sealed class PathOverlay(MainWindow w) : ICanvasOverlay
    {
        List<object?>? _dragList;
        int _dragIndex = -1;
        static readonly IPen Line = new ImmutablePen((IImmutableBrush)Palette.Accent, 2, new ImmutableDashStyle([4, 3], 0));
        static readonly IPen LineDim = new ImmutablePen(new SolidColorBrush(Color.FromArgb(150, 217, 119, 87)).ToImmutable(), 1.5, new ImmutableDashStyle([4, 3], 0));
        static readonly IPen DotEdge = new ImmutablePen((IImmutableBrush)Palette.Bg0, 1.5);
        static readonly Typeface Font = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        (SceneMechanism M, ParamDef P)? Editing()
        {
            if (w._pathEditing is not { } pe || w.Selected is not { } o || o.Id != pe.Obj) return null;
            foreach (var (m, p) in w.PathParams(o))
                if (m.Name == pe.Mech && p.Name == pe.Param) return (m, p);
            return null;
        }

        public void Draw(DrawingContext ctx, SceneCanvas canvas)
        {
            if (w.Selected is not { } o) return;
            var editing = Editing();
            foreach (var (m, p) in w.PathParams(o))
            {
                bool on = editing is { } e && e.M == m && e.P.Name == p.Name;
                var pts = PathPoints(m, p).Select(x => canvas.WorldToScreen(ToPoint(x))).ToList();
                var pen = on ? Line : LineDim;
                for (int i = 1; i < pts.Count; i++) ctx.DrawLine(pen, pts[i - 1], pts[i]);
                for (int i = 0; i < pts.Count; i++)
                {
                    double r = on ? 8 : 5;
                    ctx.DrawEllipse(on ? Palette.Accent : LineDim.Brush, DotEdge, pts[i], r, r);
                    if (!on) continue;
                    var ft = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, Palette.AccentText);
                    ctx.DrawText(ft, new Point(pts[i].X - ft.Width / 2, pts[i].Y - ft.Height / 2));
                }
                if (pts.Count > 0 && on)
                {
                    var label = new FormattedText($"{p.Name}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 11, Palette.Accent);
                    ctx.DrawText(label, new Point(pts[0].X + 10, pts[0].Y - 22));
                }
            }
        }

        static Point ToPoint(object? x) => x is List<object?> { Count: >= 2 } l ? new Point(World.ToNumber(l[0], 0), World.ToNumber(l[1], 0)) : default;

        static double Snap(double v, KeyModifiers mods) => (mods & KeyModifiers.Alt) != 0 ? Math.Round(v) : Math.Round(v / SceneCanvas.GridStep) * SceneCanvas.GridStep;

        int HandleAt(List<object?> pts, Point world)
        {
            double tol = 10 / Math.Max(0.1, w._canvas.Zoom);
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                var pt = ToPoint(pts[i]);
                if (Math.Abs(pt.X - world.X) <= tol && Math.Abs(pt.Y - world.Y) <= tol) return i;
            }
            return -1;
        }

        public bool Press(Point world, bool right, KeyModifiers mods)
        {
            if (Editing() is not { } e) return false;
            var pts = PathPoints(e.M, e.P);
            int hit = HandleAt(pts, world);
            if (right && hit < 0) return false;
            w.Checkpoint();
            if (right)
            {
                pts.RemoveAt(hit);
                e.M.Params[e.P.Name] = pts;
                w.Dispatcher_Rebuild();
                return true;
            }
            if (hit < 0)
            {
                pts.Add(ParamTypes.Point(Snap(world.X, mods), Snap(world.Y, mods)));
                hit = pts.Count - 1;
            }
            e.M.Params[e.P.Name] = pts;
            _dragList = pts;
            _dragIndex = hit;
            return true;
        }

        public void Drag(Point world, KeyModifiers mods)
        {
            if (_dragList == null || _dragIndex < 0 || _dragIndex >= _dragList.Count) return;
            _dragList[_dragIndex] = ParamTypes.Point(Snap(world.X, mods), Snap(world.Y, mods));
        }

        public void Release()
        {
            _dragList = null;
            _dragIndex = -1;
            w.Dispatcher_Rebuild();
        }
    }

    void Dispatcher_Rebuild() => Dispatcher.UIThread.Post(RebuildInspector);

    // ---------------- List ----------------

    /// <summary>Rows for a list param: each item with its type's picker, ↑ ↓ ✕, and "+ Add".</summary>
    Control ListEditor(SceneObject o, string itemType, List<object?> items, Action<List<object?>, bool> set)
    {
        var panel = new StackPanel { Spacing = 4 };
        var kind = ParamTypes.Kind(itemType);
        for (int i = 0; i < items.Count; i++)
        {
            int k = i;
            void SetItem(object? v)
            {
                var copy = new List<object?>(items) { [k] = v };
                items = copy;
                set(copy, false);
            }
            Control editor = ItemPicker(o, kind, items[k], SetItem);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Margin = new Thickness(4, 0, 0, 0) };
            Button Tool(string text, string tip, bool enabled, Action act)
            {
                var b = new Button
                {
                    Content = text, FontSize = 11, Padding = new Thickness(5, 0), MinHeight = 0, Height = 24, Opacity = enabled ? 1 : 0, IsHitTestVisible = enabled,
                    Background = Brushes.Transparent, Foreground = Palette.TextDim, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(b, tip);
                b.Click += (_, _) => act();
                return b;
            }
            tools.Children.Add(Tool("↑", "Move up", k > 0, () => { var c = new List<object?>(items); (c[k - 1], c[k]) = (c[k], c[k - 1]); set(c, true); }));
            tools.Children.Add(Tool("↓", "Move down", k < items.Count - 1, () => { var c = new List<object?>(items); (c[k + 1], c[k]) = (c[k], c[k + 1]); set(c, true); }));
            tools.Children.Add(Tool("✕", "Remove", true, () => { var c = new List<object?>(items); c.RemoveAt(k); set(c, true); }));
            var row = new DockPanel();
            DockPanel.SetDock(tools, Dock.Right);
            row.Children.Add(tools);
            row.Children.Add(new TextBlock { Text = (k + 1) + ".", Width = 20, Foreground = Palette.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(row.Children[^1], Dock.Left);
            row.Children.Add(editor);
            panel.Children.Add(row);
        }
        if (items.Count == 0) panel.Children.Add(Note("Empty list.", Palette.TextDim));
        var add = LinkButton("+ Add", $"Add an item ({(itemType == "any" ? "a value" : itemType)})");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) => set(new List<object?>(items) { NewItem(kind) }, true);
        panel.Children.Add(add);
        return panel;
    }

    static object? NewItem(ParamKind kind) => kind switch
    {
        ParamKind.Number => 0.0,
        ParamKind.Bool => false,
        ParamKind.Color => Swatches[0],
        ParamKind.Key => "space",
        _ => ""
    };

    /// <summary>One list item's picker, by the list's item type.</summary>
    Control ItemPicker(SceneObject o, ParamKind kind, object? value, Action<object?> set)
    {
        switch (kind)
        {
            case ParamKind.Number:
                return Num(World.ToNumber(value is double or bool ? value : 0.0, 0), 1, double.MinValue, v => set(v));
            case ParamKind.Bool:
            {
                var c = new CheckBox { IsChecked = World.Truthy(value), Foreground = Palette.Text, Content = World.Truthy(value) ? "yes" : "no" };
                c.IsCheckedChanged += (_, _) => { c.Content = c.IsChecked == true ? "yes" : "no"; set(c.IsChecked == true); };
                return c;
            }
            case ParamKind.Object:
                return ObjectPicker(ObjectName(value), o.Id, v => { set(v); Dispatcher_Rebuild(); });
            case ParamKind.Image:
                return PicturePicker(World.ToText(value), v => { set(v); _canvas.InvalidateVisual(); }).Box;
            case ParamKind.Sound:
                return SoundPicker(World.ToText(value), v => set(v)).Box;
            case ParamKind.Color:
                return ColorDrop(World.ToText(value), v => { set(v); Dispatcher_Rebuild(); });
            case ParamKind.Key:
                return KeyCapture(World.ToText(value), v => set(v));
            default:
                return TextField(World.ToText(value), "value", v => set(Project_TryNumber(v, out var d) && kind == ParamKind.Any ? d : v));
        }
    }

    static bool Project_TryNumber(string s, out double d) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);

    // ---------------- Color, Key, Choice ----------------

    /// <summary>Colour swatches; the current one outlined (shown first when it isn't one of them).</summary>
    static Control ColorRow(string current, Action<string> pick)
    {
        var wrap = new WrapPanel();
        var all = current.Length > 0 && !Swatches.Contains(current, StringComparer.OrdinalIgnoreCase) ? Swatches.Prepend(current) : Swatches;
        foreach (var c in all)
        {
            bool on = string.Equals(c, current, StringComparison.OrdinalIgnoreCase);
            var b = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 5, 5),
                Background = SceneCanvas.BrushOf(c, Palette.Bg2), BorderBrush = on ? Palette.Text : Palette.Border,
                BorderThickness = new Thickness(on ? 2 : 1), Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(b, c);
            b.PointerPressed += (_, e) => { if (e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) { e.Handled = true; pick(c); } };
            wrap.Children.Add(b);
        }
        return wrap;
    }

    /// <summary>A compact colour picker (for list rows): the colour as a button; the swatches drop down.</summary>
    static Control ColorDrop(string current, Action<string> pick)
    {
        var b = new Button
        {
            Width = 44, Height = 24, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
            Background = SceneCanvas.BrushOf(current, Palette.Bg2), BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTip.SetTip(b, current.Length > 0 ? current : "Pick a colour");
        var flyout = new Flyout();
        flyout.Content = new Border { Width = 200, Padding = new Thickness(8), Child = ColorRow(current, c => { flyout.Hide(); pick(c); }) };
        b.Flyout = flyout;
        return b;
    }

    /// <summary>"Press a key": click, then press the key you want (Esc cancels). A ▾ lists the usual keys too.</summary>
    Control KeyCapture(string value, Action<string> set)
    {
        string current = value;
        var b = new Button
        {
            Content = current.Length > 0 ? current : "press to set", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Palette.Bg2, Foreground = current.Length > 0 ? Palette.Text : Palette.TextDim, BorderBrush = Palette.Border,
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 4), FontFamily = Palette.Mono, FontSize = 13, CornerRadius = new CornerRadius(4)
        };
        ToolTip.SetTip(b, "Click, then press the key (Esc cancels)");
        bool listening = false;
        void Show() { b.Content = current.Length > 0 ? current : "press to set"; b.Foreground = current.Length > 0 ? Palette.Text : Palette.TextDim; b.BorderBrush = Palette.Border; }
        b.Click += (_, _) =>
        {
            listening = true;
            b.Content = "press a key…";
            b.Foreground = Palette.Accent;
            b.BorderBrush = Palette.Accent;
            b.Focus();
        };
        b.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!listening) return;
            e.Handled = true;
            listening = false;
            if (e.Key != Key.Escape)
            {
                current = KeyName(e.Key);
                set(current);
            }
            Show();
        }, RoutingStrategies.Tunnel);
        b.LostFocus += (_, _) => { if (listening) { listening = false; Show(); } };
        var menu = new Button
        {
            Content = "▾", Padding = new Thickness(6, 2), Margin = new Thickness(4, 0, 0, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch,
            Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4)
        };
        ToolTip.SetTip(menu, "The usual keys");
        var flyout = new MenuFlyout();
        foreach (var k in KeyChoices)
        {
            var mi = new MenuItem { Header = k };
            mi.Click += (_, _) => { current = k; set(k); Show(); };
            flyout.Items.Add(mi);
        }
        menu.Flyout = flyout;
        var row = new DockPanel();
        DockPanel.SetDock(menu, Dock.Right);
        row.Children.Add(menu);
        row.Children.Add(b);
        return row;
    }

    /// <summary>A dropdown of a Choice's options (a value that isn't one of them shows as missing).</summary>
    Control ChoicePicker(List<string> options, string value, Action<string> set)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
            FontSize = 13, MinWidth = 0
        };
        var items = new List<string>();
        string? match = options.FirstOrDefault(x => x == value) ?? options.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (match == null && value.Length > 0) items.Add($"{value}  (missing)");
        items.AddRange(options);
        _inspectorSync = true;
        combo.ItemsSource = items;
        combo.SelectedItem = match ?? (items.Count > 0 ? items[0] : null);
        _inspectorSync = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (_inspectorSync || combo.SelectedItem is not string s || s.EndsWith("  (missing)")) return;
            set(s);
        };
        return combo;
    }
}
