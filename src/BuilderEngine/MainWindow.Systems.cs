using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// SYSTEMS FEATURE (Systems.cs has the headless side): a SYSTEMS section in the library lists the starter systems and the
// ones in My Library; a click (or a drag onto the scene) adds one — its source and the mechanisms it brings come into the
// project and one object carries it. That object's card in the inspector sets the whole system, places its parts
// ("+ Pickup"), saves it to My Library and removes it again (with everything it added that nobody changed or uses).
// "＋ New system…" makes one from the mechanisms of the selected objects.
public sealed partial class MainWindow
{
    /// <summary>Drag text for a library system: SceneCanvas.DragPrefix + this + name.</summary>
    internal const string SysDragTag = "sys:";
    /// <summary>The SYSTEMS section's last row: makes a new system (not a name a mechanism can have).</summary>
    const string NewSystemRow = "＋ New system…";

    // ---------------- Library section ----------------

    /// <summary>Adds the SYSTEMS section to the library list (MainWindow.Library.cs calls this).</summary>
    void LibrarySystemsSection(List<object> items)
    {
        items.Add(new LibItem("", LibSection.Systems, Header: "SYSTEMS"));
        var mine = new HashSet<string>(SafeLibrarySystems(), StringComparer.OrdinalIgnoreCase);
        var names = new SortedSet<string>(mine, StringComparer.OrdinalIgnoreCase);
        foreach (var (n, _) in Systems.Starter()) names.Add(n);
        foreach (var n in names)
        {
            string? badge = _sources.ContainsKey(n) ? "in project" : mine.Contains(n) ? "mine" : null;
            items.Add(new LibItem(n, LibSection.Systems, Badge: badge));
        }
        items.Add(new LibItem(NewSystemRow, LibSection.Systems));
    }

    List<string> SafeLibrarySystems()
    {
        try { return Systems.LibrarySystems(_myLibrary); }
        catch (IOException) { return []; }
    }

    /// <summary>Is this My Library file a system? (MY LIBRARY lists only mechanisms; systems are in SYSTEMS.)</summary>
    bool IsLibrarySystem(string name) => Systems.IsSystemSource(_myLibrary.Read(name));

    /// <summary>A system as it comes in: from My Library (your saved version), else the starter one.</summary>
    string? SystemOriginal(string name) => _myLibrary.Read(name) is { } lib && Systems.IsSystemSource(lib) ? lib : Systems.StarterSource(name);

    /// <summary>What a mechanism or system was when it came into the project (to tell whether it was changed since).</summary>
    string? OriginalSource(string name) => _myLibrary.Read(name) ?? Systems.StarterSource(name);

    Control SystemLibraryRow(LibItem item)
    {
        if (item.Name == NewSystemRow)
        {
            var link = new TextBlock { Text = NewSystemRow, Foreground = Palette.Accent, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(link, "Make a system from the mechanisms of the selected objects (or pick them)");
            return link;
        }
        var row = new DockPanel { Background = Brushes.Transparent };
        if (item.Badge != null)
        {
            var badge = new TextBlock { Text = item.Badge, FontSize = 10, Foreground = Palette.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
        }
        row.Children.Add(new TextBlock { Text = "⚙ " + item.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (SystemOriginal(item.Name) is { } src && Systems.Parse(src) is { } def)
            ToolTip.SetTip(row, $"{Systems.Description(src)}\n\nComes with: {string.Join(", ", Systems.Members(def))}\nClick or drag onto the scene to add it.");
        var menu = new ContextMenu();
        var add = new MenuItem { Header = "Add to this scene" };
        add.Click += (_, _) => _ = AddSystemAsync(item.Name, null);
        menu.Items.Add(add);
        if (_sources.ContainsKey(item.Name))
        {
            var open = new MenuItem { Header = "Open its code" };
            open.Click += (_, _) => OpenMechanism(item.Name);
            menu.Items.Add(open);
        }
        if (_myLibrary.Read(item.Name) is { } mine && Systems.IsSystemSource(mine))
        {
            var rm = new MenuItem { Header = "Remove from My Library…" };
            rm.Click += (_, _) => _ = RemoveFromLibraryAsync(item.Name);
            menu.Items.Add(rm);
        }
        var make = new MenuItem { Header = "New system…" };
        make.Click += (_, _) => _ = NewSystemAsync();
        menu.Items.Add(make);
        row.ContextMenu = menu;
        return row;
    }

    /// <summary>A click on a SYSTEMS row: add that system (or make a new one).</summary>
    void OnSystemRowClicked(LibItem item)
    {
        if (item.Name == NewSystemRow) _ = NewSystemAsync();
        else _ = AddSystemAsync(item.Name, null);
    }

    /// <summary>A SYSTEMS row dropped on the scene: add that system where it was dropped.</summary>
    void OnSystemDropped(string name, Point at)
    {
        if (name == NewSystemRow) _ = NewSystemAsync();
        else _ = AddSystemAsync(name, at);
    }

    // ---------------- Adding and removing ----------------

    /// <summary>Adds a library system to the scene at <paramref name="at"/> (or the middle of the view). Returns its object.</summary>
    async Task<SceneObject?> AddSystemAsync(string name, Point? at)
    {
        if (_play != null) { SetStatus(false, "Stop the game first to add a system."); return null; }
        if (SystemOriginal(name) is not { } src) { SetStatus(false, $"There's no system called {name}."); RefreshLibrary(); return null; }
        bool replace = false;
        if (_sources.TryGetValue(name, out var have) && MechanismLibrary.Compare(have, src) == NameClash.Different)
        {
            var r = await AskClash("Name already used",
                $"This project already has a different “{name}”. Keep both (this one comes in as “{MechanismLibrary.UniqueName(name, _sources.ContainsKey)}”) or replace the project's one?");
            if (r is not ("Keep both" or "Replace")) return null;
            replace = r == "Replace";
        }
        var p = at ?? _canvas.ViewCenter;
        double x = Math.Clamp(Math.Round((p.X - 16) / SceneCanvas.GridStep) * SceneCanvas.GridStep, 0, Math.Max(0, _scene.Width - 32));
        double y = Math.Clamp(Math.Round((p.Y - 16) / SceneCanvas.GridStep) * SceneCanvas.GridStep, 0, Math.Max(0, _scene.Height - 32));
        Checkpoint();
        Systems.AddResult result;
        try { result = Systems.Add(_sources, _scene, src, x, y, _myLibrary.Read, replace); }
        catch (ScriptError e) { SetStatus(false, $"Couldn't add {name}: {e.Message}"); return null; }
        foreach (var m in result.Missing) Log($"⚠ The {result.Name} system needs a mechanism called “{m}”, but there's none in the project or My Library.");
        RefreshLibrary();
        SceneOnMechanismsChanged();
        Select(result.Object.Id);
        string copied = result.Copied.Count == 0 ? "" : $" Copied {string.Join(", ", result.Copied)} from My Library.";
        SetStatus(true, $"Added the {result.Name} system — set it up in its card on the right.{copied}");
        return result.Object;
    }

    /// <summary>Removes a system from the scene (after asking), with what it added that nothing else uses.</summary>
    async Task RemoveSystemAsync(string name)
    {
        if (_play != null) return;
        if (!_snapMode)
        {
            var r = await Dialogs.Ask(this, "Remove system",
                $"Remove the {name} system from this scene? Its object goes, and — if no other scene uses it — its code and the mechanisms it copied in (unless you changed them). The things you placed stay.",
                "Remove", "Cancel");
            if (r != "Remove") return;
        }
        Checkpoint();
        var result = Systems.Remove(_sources, _project.Scenes, _scene, name, OriginalSource);
        if (_current != null && !_sources.ContainsKey(_current)) { _current = null; ShowTab(scene: true); }
        RefreshLibrary();
        SceneOnMechanismsChanged();
        Select(0);
        var gone = new List<string>();
        if (result.SourceRemoved) gone.Add(name);
        gone.AddRange(result.MechanismsRemoved);
        SetStatus(true, $"Removed the {name} system." + (gone.Count > 0 ? $" Took {string.Join(", ", gone)} out of the project." : ""));
    }

    /// <summary>Places one of a system's parts next to the system's object.</summary>
    void PlaceSystemPart(SceneObject system, string part)
    {
        if (_play != null) return;
        Checkpoint();
        int n = _scene.Objects.Count(o => o.Has(part));
        double x = Math.Clamp(system.X + 48 + n * 40 % 400, 0, Math.Max(0, _scene.Width - 32));
        double y = Math.Clamp(system.Y + 48, 0, Math.Max(0, _scene.Height - 48));
        var o = Systems.PlacePart(_scene, part, x, y);
        Select(o.Id);
        SetStatus(true, $"Placed {o.Name} ({part}). Move it where it goes.");
    }

    // ---------------- The system's card ----------------

    /// <summary>The extra part of a system's card: what it is for, what it comes with (+ to place a part), Save and Remove.</summary>
    Control SystemCardExtras(SceneObject o, SceneMechanism m, MechanismDef def)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        string desc = _sources.TryGetValue(m.Name, out var src) ? Systems.Description(src) : "";
        panel.Children.Add(new TextBlock { Text = "SYSTEM" + (desc.Length > 0 ? " — " + desc : ""), Foreground = Palette.TextDim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });

        var parts = Systems.Parts(def);
        var runs = Systems.Members(def).Where(x => !parts.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
        if (runs.Count > 0)
            panel.Children.Add(new TextBlock { Text = "Also brings: " + string.Join(", ", runs), Foreground = Palette.TextDim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        if (parts.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Place its parts:", Foreground = Palette.TextDim, FontSize = 11.5 });
            var wrap = new WrapPanel();
            foreach (var part in parts)
            {
                int placed = _scene.Objects.Count(x => x.Has(part));
                var b = LinkButton($"+ {part}" + (placed > 0 ? $" ({placed})" : ""), $"Place a new object with {part} in the scene (the system's settings are its defaults)");
                b.Margin = new Thickness(0, 0, 8, 4);
                b.Click += (_, _) => PlaceSystemPart(o, part);
                wrap.Children.Add(b);
            }
            panel.Children.Add(wrap);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = SmallButton("Save to My Library", "Keep this system (and the mechanisms of yours it brings) for every project");
        save.Click += (_, _) => SaveSystemToLibrary(m.Name);
        var remove = SmallButton("Remove system", "Remove this system and what it added");
        remove.Click += (_, _) => _ = RemoveSystemAsync(m.Name);
        row.Children.Add(save);
        row.Children.Add(remove);
        panel.Children.Add(row);
        return panel;
    }

    void SaveSystemToLibrary(string name)
    {
        try
        {
            var saved = Systems.SaveToLibrary(_myLibrary, name, _sources);
            RefreshLibrary();
            SetStatus(true, $"Saved to My Library: {string.Join(", ", saved)} — every project can add it from SYSTEMS now.");
        }
        catch (Exception e) { SetStatus(false, $"Couldn't save to My Library: {e.Message}"); }
    }

    // ---------------- New system ----------------

    /// <summary>Makes a system from mechanisms, adds its object to the scene and selects it. Returns why not, or null.</summary>
    string? MakeSystem(string name, IReadOnlyCollection<string> mechanisms)
    {
        name = name.Trim();
        if (Systems.CheckName(name, n => _sources.ContainsKey(n) || SafeLibrarySystems().Contains(n, StringComparer.OrdinalIgnoreCase)) is { } bad) return bad;
        if (mechanisms.Count == 0) return "Pick at least one mechanism.";
        Checkpoint();
        var c = _canvas.ViewCenter;
        var r = Systems.Add(_sources, _scene, Systems.NewSource(name, mechanisms), Math.Max(0, Math.Round(c.X - 16)), Math.Max(0, Math.Round(c.Y - 16)));
        RefreshLibrary();
        SceneOnMechanismsChanged();
        Select(r.Object.Id);
        SetStatus(true, $"Made the {name} system from {string.Join(", ", mechanisms)}. Open its code to add shared settings; Save to My Library to reuse it.");
        return null;
    }

    /// <summary>"New system…": a name and the mechanisms to bring (those of the selected objects are ticked).</summary>
    async Task NewSystemAsync()
    {
        if (_play != null) return;
        var selected = _canvas.SelectedIds.Select(id => _scene.Find(id)).OfType<SceneObject>().SelectMany(o => o.Mechanisms).Select(m => m.Name).ToList();
        var candidates = _scene.Objects.SelectMany(o => o.Mechanisms).Select(m => m.Name)
            .Concat(_sources.Keys.Where(IsProjectMechanism))
            .Where(n => !Systems.IsSystemSource(_sources.GetValueOrDefault(n)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

        var dlg = new Window
        {
            Title = "New system", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Palette.Bg1, Foreground = Palette.Text,
        };
        var root = new StackPanel { Margin = new Thickness(22, 18), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = "New system", FontSize = 15, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "A system is a set of mechanisms that work together, with one card to set them up. Pick the mechanisms it brings; you can add shared settings in its code later.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 13,
        });
        var nameBox = new TextBox { Text = MechanismLibrary.UniqueName("MyKit", n => _sources.ContainsKey(n)), Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, MinHeight = 32, Padding = new Thickness(8, 6) };
        root.Children.Add(nameBox);
        var checks = new StackPanel { Spacing = 2 };
        foreach (var n in candidates)
            checks.Children.Add(new CheckBox { Content = n, Tag = n, Foreground = Palette.Text, IsChecked = selected.Contains(n, StringComparer.OrdinalIgnoreCase) });
        if (candidates.Count == 0) checks.Children.Add(new TextBlock { Text = "Nothing to pick yet: put some mechanisms on objects first.", Foreground = Palette.TextDim });
        root.Children.Add(new ScrollViewer { Content = checks, MaxHeight = 260 });
        var error = new TextBlock { Foreground = Palette.Error, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        root.Children.Add(error);
        var ok = AccentButton("Make system");
        var cancel = PlainButton("Cancel");
        ok.Click += (_, _) =>
        {
            var chosen = checks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
            if (MakeSystem(nameBox.Text ?? "", chosen) is { } why) error.Text = why;
            else dlg.Close();
        };
        cancel.Click += (_, _) => dlg.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.Opened += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        await dlg.ShowDialog(this);
    }
}
