using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>A prefab row in the library's PREFABS section.</summary>
sealed record PrefabLibItem(string Name, string Category)
{
    public override string ToString() => Name;
}

// PREFABS FEATURE (model and rules: Script/Prefabs.cs). Any object can be saved as a prefab (★ Save as prefab… in the
// inspector); the library's PREFABS section lists them — drag one onto the scene to place a copy, double-click it to
// edit the prefab itself in the inspector. Copies remember their prefab: "Changes here change every copy" (PrefabAll)
// sends a copy's changes to the prefab and the other copies; otherwise they stay on that copy (its overrides), which
// "Revert to prefab" drops. Scripts spawn a prefab by name (spawn "Slime"), before a mechanism with that name.
// Hooks in shared files: RefreshLibrary → AddPrefabSection, CreateSceneView → HookPrefabs, RebuildInspector →
// PrefabInspector, ObjectInspector → PrefabLinkRow, UseProjectFiles → world.UsePrefabs.
public sealed partial class MainWindow
{
    /// <summary>Drag text for a prefab: SceneCanvas.DragPrefix + this + name.</summary>
    internal const string PrefabDragTag = "prefab:";
    /// <summary>The prefab shown in the inspector (double-click in the library), or null.</summary>
    Prefab? _editingPrefab;
    /// <summary>What the edited prefab shared last time its copies were updated.</summary>
    JsonObject? _editingPrefabBefore;
    DispatcherTimer? _prefabTimer;
    PrefabLibItem? _prefabPress;
    Point _prefabPressAt;
    /// <summary>--snap answers prefab questions with this instead of a dialog.</summary>
    string _snapPrefabChoice = "Apply to all";

    // ---------------- Library ----------------

    /// <summary>The PREFABS section of the library (called by RefreshLibrary).</summary>
    void AddPrefabSection(List<object> items)
    {
        items.Add(new LibItem("", LibSection.Project, Header: "PREFABS"));
        foreach (var pf in _project.Prefabs.OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            items.Add(new PrefabLibItem(pf.Name, pf.Category));
        if (_project.Prefabs.Count == 0) items.Add(new LibItem("", LibSection.Project, Badge: "None yet — select an object, then ★ Save as prefab."));
    }

    Control PrefabLibRow(PrefabLibItem item)
    {
        var pf = _project.FindPrefab(item.Name);
        var row = new DockPanel { Background = Brushes.Transparent };
        if (!string.Equals(item.Category, "General", StringComparison.OrdinalIgnoreCase))
        {
            var badge = new TextBlock { Text = item.Category, FontSize = 10, Foreground = Palette.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
        }
        var thumb = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), ClipToBounds = true,
            Background = pf != null ? SceneCanvas.BrushOf(pf.Template.Color, Palette.Bg2) : Palette.Bg2,
            Child = pf != null && Pictures.Get(PrefabPicture(pf.Template)) is { } bmp ? new Image { Source = bmp, Stretch = Stretch.Uniform } : null,
        };
        RenderOptions.SetBitmapInterpolationMode(thumb, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        DockPanel.SetDock(thumb, Dock.Left);
        row.Children.Add(thumb);
        row.Children.Add(new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        ToolTip.SetTip(row, $"Drag “{item.Name}” onto the scene to place a copy · double-click to edit the prefab · scripts: spawn \"{item.Name}\"");
        row.ContextMenu = PrefabMenu(item.Name);
        return row;
    }

    /// <summary>The picture a prefab (or object) shows: its default animation's first frame, else its picture.</summary>
    static string? PrefabPicture(SceneObject t) => t.ResolveDefaultAnimation()?.Frames.FirstOrDefault() is { } f ? f : t.Image.Length > 0 ? t.Image : null;

    ContextMenu PrefabMenu(string name)
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action run)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => run();
            return m;
        }
        menu.Items.Add(Item("Edit prefab", () => EditPrefab(name)));
        menu.Items.Add(Item("Place in the scene", () => PlacePrefab(name, _canvas.ViewCenter)));
        menu.Items.Add(Item("Rename…", () => _ = RenamePrefabAsync(name)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete prefab…", () => _ = DeletePrefabAsync(name)));
        return menu;
    }

    /// <summary>Once, after the scene view exists: the library rows, drag and double-click, drops, pictures, the sync timer.</summary>
    void HookPrefabs()
    {
        _library.DataTemplates.Add(new FuncDataTemplate<PrefabLibItem>((item, _) => item == null ? new TextBlock() : PrefabLibRow(item), supportsRecycling: false));

        _library.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(_library).Properties.IsLeftButtonPressed) return;
            var li = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if ((li?.DataContext ?? li?.Content) is not PrefabLibItem item) return;
            e.Handled = true;
            if (e.ClickCount >= 2) { _prefabPress = null; EditPrefab(item.Name); return; }
            _prefabPress = item;
            _prefabPressAt = e.GetPosition(_library);
            e.Pointer.Capture(_library);
        }, RoutingStrategies.Tunnel);

        _library.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (_prefabPress == null) return;
            var p = e.GetPosition(_library);
            if (Math.Abs(p.X - _prefabPressAt.X) + Math.Abs(p.Y - _prefabPressAt.Y) < 6) return;
            string name = _prefabPress.Name;
            _prefabPress = null;
            e.Pointer.Capture(null);
            if (_play != null) return;
            ShowTab(scene: true);
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(SceneCanvas.DragPrefix + PrefabDragTag + name));
            SetStatus(true, $"Drop “{name}” on the scene to place a copy of the prefab.");
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _library.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (_prefabPress == null) return;
            string name = _prefabPress.Name;
            _prefabPress = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            SetStatus(true, $"Drag “{name}” onto the scene to place a copy; double-click to edit the prefab. Scripts: spawn \"{name}\".");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // A prefab dropped on the scene places a copy; everything else goes on to the mechanism drop.
        var dropped = _canvas.MechanismDropped;
        _canvas.MechanismDropped = (name, at, target) =>
        {
            if (name.StartsWith(PrefabDragTag)) PlacePrefab(name[PrefabDragTag.Length..], at);
            else dropped?.Invoke(name, at, target);
        };
        // Objects with animations show their default animation's first frame in the scene.
        var pictureOf = _canvas.PictureOf;
        _canvas.PictureOf = o => o.ResolveDefaultAnimation()?.Frames.FirstOrDefault() ?? pictureOf?.Invoke(o);
        // Clicking an object in the scene leaves the prefab inspector.
        var selectionChanged = _canvas.SelectionChanged;
        _canvas.SelectionChanged = () => { _editingPrefab = null; selectionChanged?.Invoke(); };

        _prefabTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _prefabTimer.Tick += (_, _) => SyncPrefabs();
        _prefabTimer.Start();
        // A running DispatcherTimer is rooted by the dispatcher: left running, it kept every closed editor window (and
        // its project) alive and syncing forever after "Start screen…" (MainWindow.LifecycleSnap.cs checks this).
        Closed += (_, _) => _prefabTimer.Stop();
    }

    // ---------------- Placing, saving ----------------

    /// <summary>A copy of the prefab, centred on <paramref name="at"/> (snapped to the grid), selected. Returns it.</summary>
    SceneObject? PlacePrefab(string name, Point at)
    {
        if (_play != null || _project.FindPrefab(name) is not { } pf) return null;
        Checkpoint();
        double x = Math.Round((at.X - pf.Template.Width / 2) / SceneCanvas.GridStep) * SceneCanvas.GridStep;
        double y = Math.Round((at.Y - pf.Template.Height / 2) / SceneCanvas.GridStep) * SceneCanvas.GridStep;
        var o = PrefabTools.Place(pf, _scene, x, y, UniqueName(pf.Name));
        _editingPrefab = null;
        Select(o.Id);
        SetStatus(true, $"Placed {o.Name}, a copy of the prefab “{pf.Name}”.");
        _canvas.Focus();
        return o;
    }

    /// <summary>★ Save as prefab…: asks for a name (and what to do when it's taken). Returns the prefab, or null.</summary>
    async Task<Prefab?> SaveAsPrefabAsync(SceneObject o, string? nameForSnap = null)
    {
        string? name = nameForSnap ?? await Dialogs.Input(this, "Save as prefab", "The prefab's name (scripts spawn it with spawn \"name\"):", o.Name, "Save");
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        if (_project.FindPrefab(name) is { } existing && nameForSnap == null)
        {
            var r = await Dialogs.Ask(this, "Prefab already exists", $"There's already a prefab called “{existing.Name}”. Replace it with this object, or keep both (this one becomes “{PrefabTools.UniqueName(_project, name)}”)?", "Keep both", "Replace", "Cancel");
            if (r == "Keep both") name = PrefabTools.UniqueName(_project, name);
            else if (r != "Replace") return null;
        }
        Checkpoint();
        var pf = PrefabTools.SaveAsPrefab(_project, o, name, _project.FindPrefab(name)?.Category);
        RefreshLibrary();
        RebuildInspector();
        SetStatus(true, $"Saved “{pf.Name}” as a prefab. Drag it from the library's PREFABS to place copies; scripts: spawn \"{pf.Name}\".");
        return pf;
    }

    async Task RenamePrefabAsync(string name)
    {
        if (_project.FindPrefab(name) is not { } pf) return;
        var n = await Dialogs.Input(this, "Rename prefab", "The prefab's new name (its copies follow):", pf.Name, "Rename");
        if (n == null) return;
        if (PrefabTools.Rename(_project, pf.Name, n) is { } why) { SetStatus(false, why); return; }
        RefreshLibrary();
        RebuildInspector();
        SetStatus(true, $"The prefab is now “{pf.Name}”. Scripts that spawn the old name need the new one.");
    }

    async Task DeletePrefabAsync(string name)
    {
        if (_project.FindPrefab(name) is not { } pf) return;
        var r = _snapMode ? "Delete" : await Dialogs.Ask(this, "Delete prefab", $"Delete the prefab “{pf.Name}”? Its copies stay in the scenes as ordinary objects.", "Delete", "Cancel");
        if (r != "Delete") return;
        Checkpoint();
        int n = PrefabTools.Delete(_project, pf.Name);
        if (_editingPrefab == pf) _editingPrefab = null;
        RefreshLibrary();
        RebuildInspector();
        SetStatus(true, n == 0 ? $"Deleted the prefab “{pf.Name}”." : $"Deleted the prefab “{pf.Name}”; its {n} cop{(n == 1 ? "y is" : "ies are")} now ordinary objects.");
    }

    // ---------------- Keeping copies in step ----------------

    /// <summary>
    /// Every 0.4 s while editing: a selected copy with "change every copy" ticked that differs from its prefab passes
    /// its changes on; the prefab open in the inspector passes its changes to its copies.
    /// </summary>
    void SyncPrefabs()
    {
        if (_play != null || _canvas == null || _project.Prefabs.Count == 0) return;
        int changed = 0;
        bool prefabChanged = false;
        foreach (var id in _canvas.SelectedIds.ToList())
        {
            if (_scene.Find(id) is not { PrefabAll: true, Prefab: not null, Locked: false } o) continue;
            if (PrefabTools.Overrides(_project, o).Count == 0) continue;
            changed += PrefabTools.ApplyToPrefab(_project, o);
            prefabChanged = true;
        }
        if (_editingPrefab is { } pf && _project.Prefabs.Contains(pf))
        {
            var now = PrefabTools.Shared(pf.Template);
            if (_editingPrefabBefore != null && !JsonNode.DeepEquals(now, _editingPrefabBefore))
            {
                changed += PrefabTools.PushChange(_project, pf, _editingPrefabBefore);
                prefabChanged = true;
            }
            _editingPrefabBefore = now;
        }
        if (!prefabChanged) return;
        _canvas.InvalidateVisual();
        RefreshLibrary(); // the thumbnail may have changed
        if (changed > 0) SetStatus(true, $"Updated {changed} other cop{(changed == 1 ? "y" : "ies")} of the prefab.");
    }

    /// <summary>Passes pending prefab changes on now (before saving or playing, and in tests).</summary>
    internal void SyncPrefabsNow() => SyncPrefabs();

    // ---------------- Inspector: a copy ----------------

    /// <summary>Under the OBJECT header: "★ Save as prefab…", or the copy's link (change every copy, revert, unlink).</summary>
    void PrefabLinkRow(SceneObject o)
    {
        if (o.Prefab == null)
        {
            var save = LinkButton("★ Save as prefab…", "Keep this object (mechanisms, picture, size, tags, animations) as a prefab to place copies of it and spawn it by name");
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Click += (_, _) => _ = SaveAsPrefabAsync(o);
            _inspectorRoot.Children.Add(save);
            return;
        }
        var box = new StackPanel { Spacing = 6 };
        var pf = _project.FindPrefab(o.Prefab);
        var head = new DockPanel();
        var unlink = LinkButton("Unlink", "Make this one independent: it stops being a copy of the prefab");
        unlink.Click += (_, _) => { Checkpoint(); PrefabTools.Unlink(o); RebuildInspector(); SetStatus(true, $"{o.Name} is an ordinary object now."); };
        DockPanel.SetDock(unlink, Dock.Right);
        head.Children.Add(unlink);
        if (pf != null)
        {
            var edit = LinkButton("Edit prefab", "Edit the prefab itself: every copy follows (except what a copy changed itself)");
            edit.Click += (_, _) => EditPrefab(pf.Name);
            DockPanel.SetDock(edit, Dock.Right);
            head.Children.Add(edit);
        }
        head.Children.Add(new TextBlock
        {
            Text = pf != null ? $"🔗 A copy of the prefab “{pf.Name}”" : $"🔗 Its prefab “{o.Prefab}” is gone",
            Foreground = pf != null ? Palette.TextDim : Palette.Error, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
        });
        box.Children.Add(head);
        if (pf != null)
        {
            var all = new CheckBox { Content = "Changes here change every copy", IsChecked = o.PrefabAll, Foreground = Palette.Text, FontSize = 12 };
            ToolTip.SetTip(all, "Ticked: what you change on this object also changes the prefab and its other copies. Off: changes stay on this one only.");
            all.IsCheckedChanged += (_, _) => { if (!_inspectorSync) _ = SetPrefabAllAsync(o, all.IsChecked == true); };
            box.Children.Add(all);
            var own = PrefabTools.Overrides(_project, o);
            if (own.Count > 0 && !o.PrefabAll)
            {
                var ownRow = new DockPanel();
                var revert = LinkButton("Revert to prefab", "Drop this copy's own changes: it becomes exactly like the prefab again (it stays where it is)");
                revert.Click += (_, _) => { Checkpoint(); PrefabTools.Revert(_project, o); _canvas.InvalidateVisual(); RebuildInspector(); SetStatus(true, $"{o.Name} is like the prefab “{pf.Name}” again."); };
                DockPanel.SetDock(revert, Dock.Right);
                ownRow.Children.Add(revert);
                ownRow.Children.Add(new TextBlock { Text = $"Its own changes: {PrefabTools.Describe(own)}", Foreground = Palette.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                box.Children.Add(ownRow);
            }
        }
        _inspectorRoot.Children.Add(new Border
        {
            Child = box, Background = Palette.AccentSoft, BorderBrush = Palette.AccentBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 6, 8)
        });
    }

    /// <summary>Ticking "change every copy" on a copy with its own changes asks what wins: this copy, or the prefab.</summary>
    async Task SetPrefabAllAsync(SceneObject o, bool on)
    {
        Checkpoint();
        if (on && PrefabTools.Overrides(_project, o) is { Count: > 0 } own)
        {
            var r = _snapMode ? _snapPrefabChoice : await Dialogs.Ask(this, "Change every copy",
                $"{o.Name} has its own changes ({PrefabTools.Describe(own)}). Give them to the prefab and every copy, or make this one like the prefab first?",
                "Apply to all", "Revert this one", "Cancel");
            if (r == "Revert this one") PrefabTools.Revert(_project, o);
            else if (r != "Apply to all") { RebuildInspector(); return; }
        }
        o.PrefabAll = on;
        SyncPrefabs();
        _canvas.InvalidateVisual();
        RebuildInspector();
        if (on) SetStatus(true, $"What you change on {o.Name} now changes the prefab and its other copies.");
    }

    // ---------------- Inspector: the prefab itself ----------------

    /// <summary>Shows a prefab in the inspector to edit it (double-click in the library).</summary>
    internal void EditPrefab(string name)
    {
        if (_play != null || _project.FindPrefab(name) is not { } pf) return;
        SyncPrefabs(); // a pending copy change first
        _editingPrefab = pf;
        _editingPrefabBefore = PrefabTools.Shared(pf.Template);
        _canvas.SelectedId = 0;
        _canvas.InvalidateVisual();
        ShowTab(scene: true);
        RebuildInspector();
        SetStatus(true, $"Editing the prefab “{pf.Name}”: its copies follow (except what a copy changed itself).");
    }

    /// <summary>The inspector for the prefab being edited (called by RebuildInspector). False when no prefab is being edited.</summary>
    bool PrefabInspector()
    {
        if (_editingPrefab is not { } pf) return false;
        if (!_project.Prefabs.Contains(pf)) { _editingPrefab = null; return false; }
        var t = pf.Template;
        var head = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var place = LinkButton("Place", "Place a copy in the middle of the scene view");
        place.Click += (_, _) => PlacePrefab(pf.Name, _canvas.ViewCenter);
        var done = LinkButton("Done", "Back to the scene");
        done.Click += (_, _) => { SyncPrefabs(); _editingPrefab = null; RebuildInspector(); };
        actions.Children.Add(place);
        actions.Children.Add(done);
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(Label("PREFAB"));
        _inspectorRoot.Children.Add(head);
        int copies = PrefabTools.Copies(_project, pf.Name).Count();
        _inspectorRoot.Children.Add(Note($"{copies} cop{(copies == 1 ? "y" : "ies")} in the project. What you change here reaches every copy, except what a copy changed itself (copies with “change every copy” ticked get everything). Scripts: spawn \"{pf.Name}\".", Palette.TextDim));

        var name = TextField(pf.Name, null, _ => { });
        void CommitName()
        {
            string v = (name.Text ?? "").Trim();
            if (v == pf.Name) return;
            if (PrefabTools.Rename(_project, pf.Name, v) is { } why) { SetStatus(false, why); name.Text = pf.Name; return; }
            RefreshLibrary();
            SetStatus(true, $"The prefab is now “{pf.Name}”.");
        }
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); e.Handled = true; } };
        _inspectorRoot.Children.Add(Field("Name", name));

        var cats = _project.Prefabs.Select(p => p.Category).Append("General").Append(pf.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var cat = new ComboBox { ItemsSource = cats.Append("New category…").ToList(), SelectedItem = cats.First(c => string.Equals(c, pf.Category, StringComparison.OrdinalIgnoreCase)), HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, FontSize = 13 };
        ToolTip.SetTip(cat, "Groups prefabs in the library");
        cat.SelectionChanged += async (_, _) =>
        {
            if (cat.SelectedItem is not string c || string.Equals(c, pf.Category, StringComparison.OrdinalIgnoreCase)) return;
            if (c == "New category…")
            {
                var n = await Dialogs.Input(this, "New category", "The category's name (groups prefabs in the library):", "", "OK");
                if (string.IsNullOrWhiteSpace(n)) { RebuildInspector(); return; }
                c = n.Trim();
            }
            pf.Category = c;
            RefreshLibrary();
            RebuildInspector();
        };
        _inspectorRoot.Children.Add(Field("Category", cat));

        var w = Num(t.Width, 8, 1, v => t.Width = v);
        var h = Num(t.Height, 8, 1, v => t.Height = v);
        _inspectorRoot.Children.Add(Pair(Field("Width", w), Field("Height", h)));
        _inspectorRoot.Children.Add(Field("Colour", Swatch(Swatches, t.Color, c => { t.Color = c; RebuildInspector(); })));
        var (picture, _) = PicturePicker(t.Image, v => { t.Image = v; RefreshLibrary(); });
        _inspectorRoot.Children.Add(Field("Picture", picture));
        _inspectorRoot.Children.Add(AnimationsRow(t)); // MainWindow.Animations.cs
        _inspectorRoot.Children.Add(Field("Tags", TagsEditor(t)));

        _inspectorRoot.Children.Add(new Border { Height = 1, Background = Palette.Border, Margin = new Thickness(0, 6) });
        _inspectorRoot.Children.Add(Label("MECHANISMS"));
        foreach (var m in t.Mechanisms.ToList()) _inspectorRoot.Children.Add(MechanismCard(t, m));
        _inspectorRoot.Children.Add(AddMechanismPicker(t));

        var del = LinkButton("Delete prefab…", "Its copies stay in the scenes as ordinary objects");
        del.Foreground = Palette.Error;
        del.HorizontalAlignment = HorizontalAlignment.Left;
        del.Margin = new Thickness(0, 10, 0, 0);
        del.Click += (_, _) => _ = DeletePrefabAsync(pf.Name);
        _inspectorRoot.Children.Add(del);
        return true;
    }
}
