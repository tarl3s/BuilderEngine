using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

enum LibSection { Project, Mine, Starter, Systems }

/// <summary>A row of the mechanism library: a section header, or a mechanism in one of the three sections.</summary>
sealed record LibItem(string Name, LibSection Section, string? Header = null, string? Badge = null)
{
    public bool IsHeader => Header != null;
}

// LIBRARY FEATURE: the mechanism list in three sections — This project / My Library / Starter kit.
// My Library is a personal folder (%APPDATA%\BuilderEngine\Library\*.bs) shared by every project: right-click a
// mechanism (or "Save to My Library" in the Mechanism tab) to put it there; clicking or dragging a library
// mechanism copies it into the project. Name clashes ask: keep both (Coin2) or replace.
public sealed partial class MainWindow
{
    /// <summary>Drag text for a My Library mechanism: SceneCanvas.DragPrefix + this + name (names can't contain ':').</summary>
    internal const string LibDragTag = "lib:";
    List<object> _libItems = [];
    string _libSectionsKey = "";
    /// <summary>--snap answers name-clash questions with this instead of a dialog.</summary>
    string _snapClashChoice = "Keep both";

    void SetupLibraryList()
    {
        _library.ItemTemplate = new FuncDataTemplate<LibItem>((item, _) => item == null ? new TextBlock() : LibraryRow(item));
        _library.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ListBoxItem li) return;
            bool header = e.Index >= 0 && e.Index < _libItems.Count && _libItems[e.Index] is LibItem { IsHeader: true };
            li.IsEnabled = !header;
            li.Padding = header ? new Thickness(8, 14, 8, 4) : new Thickness(12, 6);
            li.MinHeight = 0;
        };
    }

    Control LibraryRow(LibItem item)
    {
        if (item.IsHeader)
            return new TextBlock { Text = item.Header, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.TextDim, LetterSpacing = 1 };
        if (item.Section == LibSection.Systems) return SystemLibraryRow(item); // MainWindow.Systems.cs
        if (item.Name.Length == 0) // an empty section's hint
            return new TextBlock { Text = item.Badge, FontSize = 11, Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic };
        var row = new DockPanel { Background = Brushes.Transparent };
        if (item.Badge != null)
        {
            var badge = new TextBlock { Text = item.Badge, FontSize = 10, Foreground = Palette.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
        }
        row.Children.Add(new TextBlock { Text = item.Section == LibSection.Mine ? item.Name : MechLabel(item.Name), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        row.ContextMenu = LibraryContextMenu(item);
        return row;
    }

    ContextMenu LibraryContextMenu(LibItem item)
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action run)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => run();
            return m;
        }
        if (item.Section == LibSection.Mine)
        {
            menu.Items.Add(Item("Use in this project", () => _ = UseFromLibraryAsync(item.Name, open: true)));
            menu.Items.Add(Item("Remove from My Library…", () => _ = RemoveFromLibraryAsync(item.Name)));
        }
        else
        {
            menu.Items.Add(Item("Open", () => OpenMechanism(item.Name)));
            menu.Items.Add(Item("Save to My Library", () => _ = SaveToLibraryAsync(item.Name)));
            menu.Items.Add(Item("Make a variant…", () => _ = MakeVariantAsync(item.Name))); // MainWindow.Variants.cs
        }
        return menu;
    }

    /// <summary>Is this mechanism the project's own (new, or a starter one that was edited)?</summary>
    bool IsProjectMechanism(string name) =>
        _sources.TryGetValue(name, out var src) && (!_starterSources.TryGetValue(name, out var kit) || Normalize(kit) != Normalize(src));

    internal void RefreshLibrary()
    {
        // Only the open mechanism stays highlighted (re-selecting anything else would open it).
        var selName = (_library.SelectedItem as LibItem)?.Name is { } s && s == _current ? s : null;
        _library.SelectedItem = null;
        var items = new List<object>();
        var own = _sources.Keys.Where(IsProjectMechanism).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var mine = _myLibrary.Names().Where(n => !IsLibrarySystem(n)).ToList(); // systems are listed in SYSTEMS (MainWindow.Systems.cs)
        var kit = _sources.Keys.Where(n => !IsProjectMechanism(n)).Order(StringComparer.OrdinalIgnoreCase).ToList();

        items.Add(new LibItem("", LibSection.Project, Header: "THIS PROJECT"));
        foreach (var n in own) items.Add(new LibItem(n, LibSection.Project, Badge: _starterSources.ContainsKey(n) ? "edited" : null));
        if (own.Count == 0) items.Add(new LibItem("", LibSection.Project, Badge: "Nothing yet — “+ New” makes one."));

        items.Add(new LibItem("", LibSection.Mine, Header: "MY LIBRARY"));
        foreach (var n in mine)
        {
            // "in project" only when the project's one is the same mechanism (a different one with that name isn't).
            bool same = _sources.TryGetValue(n, out var have) && _myLibrary.Read(n) is { } lib && MechanismLibrary.Compare(lib, have) == NameClash.Same;
            items.Add(new LibItem(n, LibSection.Mine, Badge: same ? "in project" : null));
        }
        if (mine.Count == 0) items.Add(new LibItem("", LibSection.Mine, Badge: "Empty — right-click a mechanism → Save to My Library."));

        LibrarySystemsSection(items); // SYSTEMS (MainWindow.Systems.cs)

        items.Add(new LibItem("", LibSection.Starter, Header: "STARTER KIT"));
        foreach (var n in kit) items.Add(new LibItem(n, LibSection.Starter));

        _libItems = items;
        _libSectionsKey = SectionsKey();
        _library.ItemsSource = items;
        if (selName != null) SelectLibraryItem(selName);
    }

    /// <summary>Which mechanisms are in which section: when it changes (a starter mechanism got edited...), the list is rebuilt.</summary>
    string SectionsKey() => string.Join("|", _sources.Keys.Where(IsProjectMechanism).Order()) + "#" + _sources.Count;

    void RefreshLibraryIfSectionsChanged()
    {
        if (SectionsKey() != _libSectionsKey) RefreshLibrary();
    }

    /// <summary>Highlights the project / starter row of a mechanism.</summary>
    void SelectLibraryItem(string name)
    {
        var item = _libItems.OfType<LibItem>().FirstOrDefault(i => !i.IsHeader && i.Section != LibSection.Mine && i.Name == name);
        if (!ReferenceEquals(_library.SelectedItem, item)) _library.SelectedItem = item;
    }

    /// <summary>A click on a library row (not a drag).</summary>
    void OnLibraryItemClicked(LibItem item)
    {
        if (item.IsHeader || item.Name.Length == 0) return;
        if (item.Section == LibSection.Systems) { OnSystemRowClicked(item); return; } // MainWindow.Systems.cs
        if (item.Section == LibSection.Mine) _ = UseFromLibraryAsync(item.Name, open: true);
        else OpenMechanism(item.Name);
    }

    // ---------------- My Library ----------------

    async Task<string?> AskClash(string title, string message)
    {
        if (_snapMode) return _snapClashChoice;
        return await Dialogs.Ask(this, title, message, "Keep both", "Replace", "Cancel");
    }

    /// <summary>Saves a project or starter mechanism to My Library. Returns the name it was saved as, or null.</summary>
    async Task<string?> SaveToLibraryAsync(string name)
    {
        if (!_sources.TryGetValue(name, out var src)) return null;
        bool overwrite = false;
        switch (_myLibrary.Check(name, src))
        {
            case NameClash.Same:
                SetStatus(true, $"{name} is already in My Library, exactly like this.");
                return name;
            case NameClash.Different:
                var r = await AskClash("Already in My Library",
                    $"My Library already has a different mechanism called “{name}”. Keep both (this one becomes “{MechanismLibrary.UniqueName(name, _myLibrary.Contains)}”) or replace the one in the library?");
                if (r is not ("Keep both" or "Replace")) return null;
                overwrite = r == "Replace";
                break;
        }
        try
        {
            string saved = _myLibrary.Save(name, src, overwrite);
            RefreshLibrary();
            SetStatus(true, saved == name ? $"Saved {name} to My Library — every project can use it now." : $"Saved to My Library as {saved}.");
            return saved;
        }
        catch (Exception e)
        {
            SetStatus(false, $"Couldn't save to My Library: {e.Message}");
            return null;
        }
    }

    /// <summary>Copies a My Library mechanism into the project (asking on a name clash). Returns its name in the project, or null.</summary>
    async Task<string?> UseFromLibraryAsync(string name, bool open)
    {
        string? file = _myLibrary.Read(name);
        if (file == null) { SetStatus(false, $"{name} isn't in My Library any more."); RefreshLibrary(); return null; }
        var chunks = SplitMechanisms(file).ToList();
        if (chunks.Count == 0) { SetStatus(false, $"{name} in My Library is empty."); return null; }
        var (realName, src) = chunks.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (realName == null) (realName, src) = chunks[0];

        string useAs = realName;
        var clash = _sources.TryGetValue(realName, out var existing) ? MechanismLibrary.Compare(existing, src) : NameClash.None;
        if (clash == NameClash.Different)
        {
            string other = MechanismLibrary.UniqueName(realName, _sources.ContainsKey);
            var r = await AskClash("Name already used",
                $"This project already has a different mechanism called “{realName}”. Keep both (the library one comes in as “{other}”) or replace the project's one?");
            if (r is not ("Keep both" or "Replace")) return null;
            if (r == "Keep both")
            {
                useAs = other;
                src = MechanismLibrary.RenameSource(src, realName, useAs);
            }
        }
        if (clash != NameClash.Same)
        {
            _sources[useAs] = src.Replace("\r\n", "\n").TrimEnd() + "\n";
            RefreshLibrary();
            SceneOnMechanismsChanged();
            SetStatus(true, $"Copied {useAs} from My Library into this project.");
        }
        if (open) OpenMechanism(useAs);
        return useAs;
    }

    async Task RemoveFromLibraryAsync(string name)
    {
        var r = await Dialogs.Ask(this, "Remove from My Library",
            $"Remove “{name}” from My Library? Projects that already use it keep their copy.", "Cancel", "Remove");
        if (r != "Remove") return;
        _myLibrary.Delete(name);
        RefreshLibrary();
        SetStatus(true, $"Removed {name} from My Library.");
    }
}
