using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// SCENE FEATURE: the inspector (right panel on the Scene tab). Values are picked (spinners, swatches, dropdowns,
// check boxes); typing is only for names, tags and free text params.
public sealed partial class MainWindow
{
    readonly StackPanel _inspectorRoot = new() { Margin = new Thickness(16, 16, 16, 8), Spacing = 10 };
    bool _inspectorDirty;
    /// <summary>True while the inspector writes its own fields (their change events must not edit the scene).</summary>
    bool _inspectorSync;
    NumericUpDown? _numX, _numY, _numW, _numH;
    readonly Dictionary<string, (string Source, MechanismDef? Def, string? Error)> _defCache = new();

    Control CreateInspector()
    {
        AccentResources(_inspectorRoot.Resources);
        _inspectorRoot.AttachedToVisualTree += (_, _) => { if (_inspectorDirty) RebuildInspector(); };
        RebuildInspector();
        return _inspectorRoot;
    }

    /// <summary>Builds the inspector again (now if it is on screen, otherwise the next time it is shown).</summary>
    void RebuildInspector()
    {
        if (_inspectorRoot.GetVisualRoot() == null) { _inspectorDirty = true; return; }
        _inspectorDirty = false;
        if (_canvas is not null) _canvas.Images = Pictures; // follows a project switch
        _numX = _numY = _numW = _numH = null;
        _nameBox = null;
        _inspectorRoot.Children.Clear();
        if (_play != null) DebugPanel(); // MainWindow.Debug.cs
        else if (_canvas is not null && _canvas.SelectedIds.Count > 1) MultiInspector(); // MainWindow.InspectorQol.cs
        else if (Selected is { } o) ObjectInspector(o);
        else SceneSettings();
        UpdateCanvasOverlay(); // the selected object's paths (MainWindow.ParamPickers.cs)
    }

    // ---------------- Nothing selected: the scene ----------------

    void SceneSettings()
    {
        var s = _scene;
        _inspectorRoot.Children.Add(Label("SCENE"));
        _inspectorRoot.Children.Add(Field("Name", TextField(s.Name, null, v =>
        {
            Checkpoint("scene-name");
            string old = s.Name;
            s.Name = v;
            RetargetDoors(old, v);
            RefreshScenes();
            if (_project.Scenes.Count(x => string.Equals(x.Name.Trim(), v.Trim(), StringComparison.OrdinalIgnoreCase)) > 1)
                SetStatus(false, $"Another scene is already called “{v.Trim()}”: goto_scene would pick the first one.");
        })));
        _inspectorRoot.Children.Add(SceneFlowNote());

        var size = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        var w = Num(s.Width, 16, 64, v => { Checkpoint("scene-w"); s.Width = v; _canvas.InvalidateVisual(); });
        var h = Num(s.Height, 16, 64, v => { Checkpoint("scene-h"); s.Height = v; _canvas.InvalidateVisual(); });
        size.Children.Add(Field("Width", w));
        var hf = Field("Height", h);
        Grid.SetColumn(hf, 2);
        size.Children.Add(hf);
        _inspectorRoot.Children.Add(size);

        _inspectorRoot.Children.Add(Field("Background", Swatch(BackgroundSwatches, s.Background, c =>
        {
            Checkpoint();
            s.Background = c;
            _canvas.InvalidateVisual();
            RebuildInspector();
        })));

        _inspectorRoot.Children.Add(new TextBlock
        {
            Text = $"{s.Objects.Count} object(s).\n\nClick an object to edit it (drag on empty space or Shift+click to select several). Drag a mechanism from the library onto the scene to make an object with it, or onto an object to give it that mechanism.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
        });
        if (s.Objects.Count > 0) _inspectorRoot.Children.Add(ObjectList()); // every object, locked ones too (MainWindow.InspectorQol.cs)
    }

    // ---------------- An object ----------------

    void ObjectInspector(SceneObject o)
    {
        var head = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var dup = LinkButton("Duplicate", "Ctrl+D");
        dup.Click += (_, _) => DuplicateSelected();
        var del = LinkButton("Delete", "Delete key");
        del.Foreground = Palette.Error;
        del.Click += (_, _) => DeleteSelected();
        var lockButton = LockButton(o); // MainWindow.InspectorQol.cs
        actions.Children.Add(lockButton);
        actions.Children.Add(dup);
        actions.Children.Add(del);
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(Label("OBJECT"));
        _inspectorRoot.Children.Add(head);
        if (o.Locked) _inspectorRoot.Children.Add(LockedNote());

        _nameBox = TextField(o.Name, null, v => { Checkpoint("name" + o.Id); o.Name = v; _canvas.InvalidateVisual(); });
        ToolTip.SetTip(_nameBox, "F2 or a double-click on the object brings you here");
        _inspectorRoot.Children.Add(Field("Name", _nameBox));

        _numX = Num(o.X, 8, double.MinValue, v => { Checkpoint("x" + o.Id); o.X = v; _canvas.InvalidateVisual(); });
        _numY = Num(o.Y, 8, double.MinValue, v => { Checkpoint("y" + o.Id); o.Y = v; _canvas.InvalidateVisual(); });
        _numW = Num(o.Width, 8, 1, v => { Checkpoint("w" + o.Id); SetSide(o, v, width: true); });
        _numH = Num(o.Height, 8, 1, v => { Checkpoint("h" + o.Id); SetSide(o, v, width: false); });
        _inspectorRoot.Children.Add(Pair(Field("X", _numX), Field("Y", _numY)));
        _inspectorRoot.Children.Add(Pair(Field("Width", _numW), Field("Height", _numH)));
        _inspectorRoot.Children.Add(SizeRow(o));  // Keep proportions, Original size (MainWindow.InspectorQol.cs)
        _inspectorRoot.Children.Add(OrderRow(o)); // draw order (MainWindow.InspectorQol.cs)

        _inspectorRoot.Children.Add(Field("Colour", Swatch(Swatches, o.Color, c =>
        {
            Checkpoint();
            o.Color = c;
            _canvas.InvalidateVisual();
            RebuildInspector();
        })));

        var (picture, _) = PicturePicker(o.Image, v => { Checkpoint(); o.Image = v; _canvas.InvalidateVisual(); });
        ToolTip.SetTip(picture, "A picture from the project's images folder, drawn in the object's box (scripts change it with look \"name.png\")");
        _inspectorRoot.Children.Add(Field("Picture", picture));

        _inspectorRoot.Children.Add(Field("Tags", TagsEditor(o)));

        _inspectorRoot.Children.Add(new Border { Height = 1, Background = Palette.Border, Margin = new Thickness(0, 6) });
        _inspectorRoot.Children.Add(Label("MECHANISMS"));
        if (o.Mechanisms.Count == 0)
            _inspectorRoot.Children.Add(new TextBlock
            {
                Text = "No mechanisms yet: this object just sits there. Add one below or drag one from the library onto it.",
                TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12
            });
        foreach (var m in o.Mechanisms.ToList())
            _inspectorRoot.Children.Add(MechanismCard(o, m));
        _inspectorRoot.Children.Add(AddMechanismPicker(o));
        if (o.Locked) DisableAllBut(head, lockButton); // a locked object is read-only until unlocked (MainWindow.InspectorQol.cs)
    }

    /// <summary>The canvas moved or resized the selection: show the new numbers without rebuilding.</summary>
    void SyncTransformFields()
    {
        if (Selected is not { } o || _numX == null) return;
        _inspectorSync = true;
        _numX.Value = (decimal)o.X;
        _numY!.Value = (decimal)o.Y;
        _numW!.Value = (decimal)o.Width;
        _numH!.Value = (decimal)o.Height;
        _inspectorSync = false;
    }

    Control TagsEditor(SceneObject o)
    {
        var panel = new StackPanel { Spacing = 6 };
        var chips = new WrapPanel();
        foreach (var t in o.Tags.ToList())
        {
            var x = new Button
            {
                Content = "×", Padding = new Thickness(4, 0), Background = Brushes.Transparent, Foreground = Palette.TextDim,
                BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13
            };
            ToolTip.SetTip(x, $"Remove the tag “{t}”");
            x.Click += (_, _) => { Checkpoint(); o.Tags.Remove(t); RebuildInspector(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            row.Children.Add(new TextBlock { Text = t, Foreground = Palette.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(x);
            chips.Children.Add(new Border
            {
                Child = row, Background = Palette.AccentSoft, BorderBrush = Palette.AccentBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 1, 4, 1), Margin = new Thickness(0, 0, 6, 6)
            });
        }
        if (o.Tags.Count > 0) panel.Children.Add(chips);

        // Tags the mechanisms look for ("touched by \"player\"") and tags used in the scene: one click to add.
        var suggestions = SuggestedTags().Where(t => !o.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)).Take(8).ToList();
        if (suggestions.Count > 0)
        {
            var quick = new WrapPanel();
            foreach (var t in suggestions)
            {
                var b = LinkButton("+ " + t, $"Add the tag “{t}”");
                b.Margin = new Thickness(0, 0, 6, 4);
                b.Click += (_, _) => { Checkpoint(); o.Tags.Add(t); RebuildInspector(); };
                quick.Children.Add(b);
            }
            panel.Children.Add(quick);
        }

        var box = new TextBox { Watermark = "new tag…", FontSize = 12, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, MinHeight = 28, Padding = new Thickness(8, 4) };
        var add = PlainButton("Add");
        add.Padding = new Thickness(10, 4);
        void Commit()
        {
            string t = (box.Text ?? "").Trim();
            if (t.Length == 0 || o.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) return;
            Checkpoint();
            o.Tags.Add(t);
            RebuildInspector();
        }
        add.Click += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        var line = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        add.Margin = new Thickness(6, 0, 0, 0);
        line.Children.Add(add);
        line.Children.Add(box);
        panel.Children.Add(line);
        return panel;
    }

    IEnumerable<string> SuggestedTags()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _sources.Keys.Order())
        {
            if (DefOf(name, out _) is not { } def) continue;
            foreach (var w in def.Whens)
                if (w.Event.Kind == EventKind.Touched && w.Event.Filter is { Length: > 0 } f && seen.Add(f)) yield return f;
        }
        foreach (var o in _scene.Objects)
            foreach (var t in o.Tags)
                if (seen.Add(t)) yield return t;
    }

    // ---------------- Mechanism cards ----------------

    Control MechanismCard(SceneObject o, SceneMechanism m)
    {
        var body = new StackPanel { Spacing = 8 };

        var title = new Button
        {
            Content = MechLabel(m.Name), Background = Brushes.Transparent, Foreground = Palette.Accent, FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(title, "Open this mechanism");
        title.Click += (_, _) => OpenMechanism(m.Name);
        var remove = new Button
        {
            Content = "×", Background = Brushes.Transparent, Foreground = Palette.TextDim, BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0), FontSize = 15
        };
        ToolTip.SetTip(remove, $"Remove {m.Name} from this object");
        remove.Click += (_, _) => { Checkpoint(); o.Mechanisms.Remove(m); RebuildInspector(); };
        var head = new DockPanel();
        DockPanel.SetDock(remove, Dock.Right);
        head.Children.Add(remove);
        head.Children.Add(title);
        body.Children.Add(head);

        var def = DefOf(m.Name, out var error);
        if (!_sources.ContainsKey(m.Name))
            body.Children.Add(Note($"There's no mechanism called “{m.Name}” any more.", Palette.Error));
        else if (def == null)
            body.Children.Add(Note($"This mechanism has a problem — open it to fix it. {error}", Palette.Error));
        else if (def.Params.Count == 0)
            body.Children.Add(Note("Nothing to set.", Palette.TextDim));
        else
            foreach (var p in def.Params)
                body.Children.Add(ParamRow(o, m, p));
        if (def?.IsSystem == true) body.Children.Add(SystemCardExtras(o, m, def)); // one card sets up the whole system (MainWindow.Systems.cs)

        return new Border
        {
            Child = body, Background = Palette.Bg2, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 8, 10)
        };
    }

    Control ParamRow(SceneObject o, SceneMechanism m, ParamDef p)
    {
        object? def = ParamDefault(p);
        bool overridden = m.Params.ContainsKey(p.Name);
        object? current = overridden ? World.ConvertParam(m.Params[p.Name], p.Type) : def;
        string type = p.Type.ToLowerInvariant();
        string key = $"param{o.Id}.{m.Name}.{p.Name}";

        var reset = new Button
        {
            Content = "reset", Background = Brushes.Transparent, Foreground = Palette.TextDim, BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0), FontSize = 11, IsVisible = overridden, VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(reset, $"Back to the mechanism's default ({FormatValue(def)})");

        Control field;
        Action<bool> showOverridden;
        if (TypedParamField(o, m, p, def, current, reset) is { } typed) (field, showOverridden) = typed; // Object, Path, List, Color, Key, Choice (MainWindow.ParamPickers.cs)
        else switch (type)
        {
            case "number" or "seconds":
            {
                bool secs = type == "seconds";
                var n = Num(World.ToNumber(current is double or bool ? current : 0.0, 0), secs ? 0.1 : 1, double.MinValue, v =>
                {
                    Checkpoint(key);
                    m.Params[p.Name] = v;
                }, secs ? "0.###" : "0.##");
                showOverridden = on => n.Foreground = on ? Palette.Text : Palette.TextDim;
                field = n;
                reset.Click += (_, _) =>
                {
                    Checkpoint();
                    m.Params.Remove(p.Name);
                    _inspectorSync = true;
                    n.Value = (decimal)World.ToNumber(def is double or bool ? def : 0.0, 0);
                    _inspectorSync = false;
                };
                n.ValueChanged += (_, _) => { if (!_inspectorSync) { reset.IsVisible = true; showOverridden(true); } };
                break;
            }
            case "bool" or "yesno":
            {
                var c = new CheckBox { IsChecked = World.Truthy(current), Foreground = Palette.Text, Content = World.Truthy(current) ? "yes" : "no" };
                c.IsCheckedChanged += (_, _) =>
                {
                    c.Content = c.IsChecked == true ? "yes" : "no";
                    if (_inspectorSync) return;
                    Checkpoint();
                    m.Params[p.Name] = c.IsChecked == true;
                    reset.IsVisible = true;
                };
                reset.Click += (_, _) =>
                {
                    Checkpoint();
                    m.Params.Remove(p.Name);
                    _inspectorSync = true;
                    c.IsChecked = World.Truthy(def);
                    _inspectorSync = false;
                };
                showOverridden = _ => { };
                field = c;
                break;
            }
            case "scene":
            {
                var combo = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
                    FontSize = 13, MinWidth = 0
                };
                void Fill(string v)
                {
                    var names = _project.Scenes.Select(s => s.Name).ToList();
                    string? match = names.FirstOrDefault(n => string.Equals(n.Trim(), v.Trim(), StringComparison.OrdinalIgnoreCase));
                    var items = new List<string>();
                    if (match == null && v.Length > 0) items.Add($"{v}  (missing)");
                    items.AddRange(names);
                    _inspectorSync = true;
                    combo.ItemsSource = items;
                    combo.SelectedItem = match ?? (items.Count > 0 ? items[0] : null);
                    _inspectorSync = false;
                }
                Fill(World.ToText(current));
                combo.SelectionChanged += (_, _) =>
                {
                    if (_inspectorSync || combo.SelectedItem is not string s || s.EndsWith("  (missing)")) return;
                    Checkpoint();
                    m.Params[p.Name] = s;
                    reset.IsVisible = true;
                };
                reset.Click += (_, _) =>
                {
                    Checkpoint();
                    m.Params.Remove(p.Name);
                    Fill(World.ToText(def));
                };
                showOverridden = _ => { };
                field = combo;
                break;
            }
            case "image" or "picture":
            {
                var (box, setValue) = PicturePicker(World.ToText(current), v =>
                {
                    Checkpoint();
                    m.Params[p.Name] = v;
                    reset.IsVisible = true;
                    _canvas.InvalidateVisual();
                });
                reset.Click += (_, _) =>
                {
                    Checkpoint();
                    m.Params.Remove(p.Name);
                    setValue(World.ToText(def));
                    _canvas.InvalidateVisual();
                };
                showOverridden = _ => { };
                field = box;
                break;
            }
            case "sound":
            {
                var (box, setValue) = SoundPicker(World.ToText(current), v =>
                {
                    Checkpoint();
                    m.Params[p.Name] = v;
                    reset.IsVisible = true;
                });
                reset.Click += (_, _) =>
                {
                    Checkpoint();
                    m.Params.Remove(p.Name);
                    setValue(World.ToText(def));
                };
                showOverridden = _ => { };
                field = box;
                break;
            }
            default:
            {
                var t = TextField(overridden ? World.ToText(current) : "", FormatValue(def), v =>
                {
                    Checkpoint(key);
                    if (v.Length == 0) m.Params.Remove(p.Name); else m.Params[p.Name] = v;
                });
                t.TextChanged += (_, _) => { if (!_inspectorSync) reset.IsVisible = (t.Text ?? "").Length > 0; };
                reset.Click += (_, _) =>
                {
                    Checkpoint();
                    m.Params.Remove(p.Name);
                    _inspectorSync = true;
                    t.Text = "";
                    _inspectorSync = false;
                };
                showOverridden = _ => { };
                field = t;
                break;
            }
        }
        reset.Click += (_, _) => { reset.IsVisible = false; showOverridden(false); };
        showOverridden(overridden);

        var label = new TextBlock { Text = Pretty(p.Name), Foreground = Palette.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(label, $"{p.Name}: {Printer.ParamType(p)}");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*,Auto") };
        row.Children.Add(label);
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        Grid.SetColumn(reset, 2);
        row.Children.Add(reset);
        return row;
    }

    Control AddMechanismPicker(SceneObject o)
    {
        var names = _sources.Keys.Where(n => !o.Has(n)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var combo = new ComboBox
        {
            ItemsSource = names, PlaceholderText = "+ Add mechanism", HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.AccentBorder, IsEnabled = names.Count > 0,
            Margin = new Thickness(0, 2, 0, 0)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string name) return;
            Checkpoint();
            o.Mechanisms.Add(new SceneMechanism { Name = name });
            Dispatcher.UIThread.Post(RebuildInspector);
        };
        return combo;
    }

    // ---------------- Mechanism definitions ----------------

    /// <summary>A mechanism as it runs: a variant merged with its parent (so the inspector shows the inherited params too).</summary>
    MechanismDef? DefOf(string name, out string? error)
    {
        var raw = RawDefOf(name, out error);
        if (raw?.Base == null) return raw;
        try { return Variants.Resolve(raw, RawDef); }
        catch (ScriptError e) { error = e.ToString(); return null; }
    }

    /// <summary>A mechanism as written (parsed once per source text).</summary>
    MechanismDef? RawDefOf(string name, out string? error)
    {
        error = null;
        if (!_sources.TryGetValue(name, out var src)) return null;
        if (_defCache.TryGetValue(name, out var c) && ReferenceEquals(c.Source, src)) { error = c.Error; return c.Def; }
        MechanismDef? def = null;
        try
        {
            var defs = Parser.Parse(src);
            def = defs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) ?? defs.FirstOrDefault();
            if (def == null) error = "It is empty.";
        }
        catch (ScriptError e) { error = e.ToString(); }
        _defCache[name] = (src, def, error);
        return def;
    }

    /// <summary>The value a param has when the inspector doesn't set it (literal defaults; otherwise the type's zero).</summary>
    static object? ParamDefault(ParamDef p)
    {
        if (ParamTypes.TryConstant(p.Default, out var v) && v != null) return World.ConvertParam(v, p.Type);
        return ParamTypes.Kind(p.Type) switch
        {
            ParamKind.Number => 0.0,
            ParamKind.Bool => false,
            ParamKind.Path or ParamKind.List => new List<object?>(),
            _ => ""
        };
    }

    static string FormatValue(object? v) => v switch
    {
        double d => d.ToString("0.###", CultureInfo.CurrentCulture),
        bool b => b ? "yes" : "no",
        null => "",
        _ => World.ToText(v)
    };

    /// <summary>"safe_time" -> "safe time".</summary>
    static string Pretty(string name) => name.Replace('_', ' ');

    // ---------------- Small controls ----------------

    /// <summary>Fluent's blue highlights (checked boxes, focused fields) in the BuilderEngine orange, for everything inside.</summary>
    static void AccentResources(IResourceDictionary r)
    {
        foreach (var k in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
                     "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed",
                     "TextControlBorderBrushFocused", "ComboBoxBorderBrushFocused" })
            r[k] = Palette.Accent;
        foreach (var k in new[] { "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed" })
            r[k] = Palette.AccentText;
        r["TextControlSelectionHighlightColor"] = Palette.AccentBorder;
    }

    NumericUpDown Num(double value, double increment, double min, Action<double> set, string format = "0.##")
    {
        var n = new NumericUpDown
        {
            Value = (decimal)Math.Clamp(value, -1e9, 1e9), Increment = (decimal)increment, FormatString = format,
            Minimum = min <= -1e9 ? -1_000_000_000m : (decimal)min, Maximum = 1_000_000_000m,
            Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
            HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, FontSize = 13,
            ParsingNumberStyle = NumberStyles.Float
        };
        n.ValueChanged += (_, e) =>
        {
            if (_inspectorSync || e.NewValue is not { } v) return;
            set((double)v);
        };
        return n;
    }

    /// <summary>The last entry of a sound dropdown: picks files to import.</summary>
    sealed class ImportSoundEntry
    {
        public override string ToString() => "Import sound…";
    }

    /// <summary>
    /// A dropdown of the project's sounds (+ "Import sound…") with a ▶ to listen. A name that isn't in the sounds folder
    /// is still shown, marked missing. Returns the control and a way to show another value without calling <paramref name="set"/>.
    /// </summary>
    (Control Box, Action<string> Show) SoundPicker(string value, Action<string> set)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
            FontSize = 13, MinWidth = 0
        };
        string current = value;
        void Fill(string v)
        {
            current = v;
            var sounds = ProjectSounds();
            var items = new List<object>();
            string? match = sounds.FirstOrDefault(s => string.Equals(s, v, StringComparison.OrdinalIgnoreCase))
                ?? (Sounds.Resolve(_projectDir, v) is { } path ? Path.GetFileName(path) : null);
            if (match == null && v.Length > 0) items.Add($"{v}  (missing)");
            items.AddRange(sounds);
            items.Add(new ImportSoundEntry());
            _inspectorSync = true;
            combo.ItemsSource = items;
            combo.SelectedItem = match != null ? items.FirstOrDefault(i => Equals(i, match)) : v.Length > 0 ? items[0] : null;
            _inspectorSync = false;
        }
        Fill(value);
        combo.SelectionChanged += async (_, _) =>
        {
            if (_inspectorSync) return;
            switch (combo.SelectedItem)
            {
                case ImportSoundEntry:
                {
                    string before = current;
                    Fill(before);
                    if (await ImportSoundsAsync() is { } imported) { set(imported); RebuildInspector(); }
                    break;
                }
                case string s when !s.EndsWith("  (missing)"):
                    current = s;
                    set(s);
                    break;
            }
        };
        var play = PreviewButton(() => current);
        play.Margin = new Thickness(4, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(play, Dock.Right);
        row.Children.Add(play);
        row.Children.Add(combo);
        return (row, Fill);
    }

    TextBox TextField(string value, string? placeholder, Action<string> set)
    {
        var t = new TextBox
        {
            Text = value, Watermark = placeholder, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
            FontSize = 13, MinHeight = 30, Padding = new Thickness(8, 5)
        };
        t.TextChanged += (_, _) => { if (!_inspectorSync) set(t.Text ?? ""); };
        return t;
    }

    static Control Field(string label, Control field)
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(new TextBlock { Text = label, Foreground = Palette.TextDim, FontSize = 12 });
        p.Children.Add(field);
        return p;
    }

    static Control Pair(Control a, Control b)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        g.Children.Add(a);
        Grid.SetColumn(b, 2);
        g.Children.Add(b);
        return g;
    }

    static TextBlock Note(string text, IBrush color) => new() { Text = text, Foreground = color, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    static Button LinkButton(string text, string tip)
    {
        var b = new Button
        {
            Content = text, Background = Brushes.Transparent, Foreground = Palette.Accent, BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>A row of colour swatches; the current colour is outlined (and shown first if it isn't one of them).</summary>
    static Control Swatch(string[] colors, string current, Action<string> pick)
    {
        var wrap = new WrapPanel();
        IEnumerable<string> all = colors.Contains(current, StringComparer.OrdinalIgnoreCase) ? colors : colors.Prepend(current);
        foreach (var c in all)
        {
            bool on = string.Equals(c, current, StringComparison.OrdinalIgnoreCase);
            // A Border, not a Button: the button theme would repaint the colour on hover.
            var b = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 6),
                Background = SceneCanvas.BrushOf(c, Palette.Bg2), BorderBrush = on ? Palette.Text : Palette.Border,
                BorderThickness = new Thickness(on ? 2 : 1), Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(b, c);
            b.PointerPressed += (_, e) => { if (e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) { e.Handled = true; pick(c); } };
            wrap.Children.Add(b);
        }
        return wrap;
    }
}
