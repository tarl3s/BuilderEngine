using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// Type to search in every dropdown and menu: with one open, typing hides the entries that don't match (case, accents
/// and emoji don't matter; see <see cref="SceneEdit.MatchScore"/>) and shows what was typed in a tip above the list.
/// Nothing is picked until the person does it (a click, or Enter for the first match), so a search never changes a value
/// or adds an undo step. Backspace edits the search, Esc clears it (a second Esc closes the list). Typing on a focused,
/// closed dropdown opens it and starts the search.
/// </summary>
static class ListSearch
{
    static bool _installed;
    static ComboBox? _combo;
    static MenuBase? _menu;
    static string _query = "";
    static ComboBox? _pendingCombo;
    static string _pending = "";
    static readonly HashSet<Control> _hidden = new();
    static Control? _tipOwner;
    static object? _savedTip;
    static PlacementMode _savedPlacement;

    /// <summary>A dropdown or menu is open: the window's own shortcuts leave the keys to it.</summary>
    public static bool Active => (_combo?.IsDropDownOpen ?? false) || _menu != null;
    /// <summary>What was typed in the open list ("" = no search).</summary>
    public static string Query => _query;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        // Every entry of a dropdown gets its container while open (hiding needs them): no virtualizing panel. Lists are short.
        Application.Current?.Styles.Add(new Style(x => x.OfType<ComboBox>())
        {
            Setters = { new Setter(ItemsControl.ItemsPanelProperty, new FuncTemplate<Panel?>(() => new StackPanel())) }
        });
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((p, e) => OnPopup(p, e.NewValue is true));
        InputElement.TextInputEvent.AddClassHandler<Window>(OnText, RoutingStrategies.Tunnel);
        InputElement.KeyDownEvent.AddClassHandler<Window>(OnKey, RoutingStrategies.Tunnel);
    }

    static void OnPopup(Popup p, bool open)
    {
        if (p.TemplatedParent is ComboBox cb)
        {
            if (open)
            {
                Reset();
                _combo = cb;
                if (ReferenceEquals(_pendingCombo, cb)) { _query = _pending; Dispatcher.UIThread.Post(Apply, DispatcherPriority.Loaded); } // after the entries exist
                _pendingCombo = null;
            }
            else if (ReferenceEquals(_combo, cb)) Reset();
        }
        else if (p.Child is MenuBase m)
        {
            if (open) { Reset(); _menu = m; }
            else if (ReferenceEquals(_menu, m)) Reset();
        }
    }

    static void OnText(Window w, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (!Active)
        {
            // A focused, closed dropdown: open it and search.
            if (w.FocusManager?.GetFocusedElement() is not ComboBox { IsDropDownOpen: false, IsEnabled: true } cb || e.Text.Trim().Length == 0) return;
            _pendingCombo = cb; // the search starts when its list opens (OnPopup)
            _pending = e.Text;
            e.Handled = true;
            cb.IsDropDownOpen = true;
            return;
        }
        if (_query.Length == 0 && e.Text == " ") return; // Space on its own keeps picking the highlighted entry
        _query += e.Text;
        e.Handled = true;
        Apply();
    }

    static void OnKey(Window w, KeyEventArgs e)
    {
        if (!Active || _query.Length == 0) return;
        switch (e.Key)
        {
            case Key.Back:
                _query = _query[..^1];
                Apply();
                e.Handled = true;
                break;
            case Key.Escape:
                _query = "";
                Apply();
                e.Handled = true;
                break;
            case Key.Enter:
                PickFirst(w);
                e.Handled = true;
                break;
            case Key.Space:
                e.Handled = true; // part of the search (the text arrives as text input), not "pick this entry"
                break;
        }
    }

    /// <summary>The open list's entries with their containers.</summary>
    static List<(Control Container, object? Item)> Entries()
    {
        var list = new List<(Control, object?)>();
        ItemsControl? ic = _combo is { IsDropDownOpen: true } ? _combo : _menu;
        if (ic == null) return list;
        for (int i = 0; i < ic.ItemCount; i++)
            if (ic.ContainerFromIndex(i) is { } c) list.Add((c, ic.Items[i]));
        return list;
    }

    /// <summary>Every text an entry can be found by: a string, a menu header (or the texts inside it), its Tag.</summary>
    internal static string TextOf(object? item, Control? container)
    {
        var texts = new List<string>();
        void Add(object? o)
        {
            switch (o)
            {
                case null: return;
                case string s: texts.Add(s); return;
                case TextBlock tb: texts.Add(tb.Text ?? ""); return;
                case MenuItem mi: Add(mi.Header); if (mi.Tag is string tag) texts.Add(tag); return;
                case ContentControl cc: Add(cc.Content); return;
                case Control c: foreach (var t in c.GetLogicalDescendants().OfType<TextBlock>()) texts.Add(t.Text ?? ""); return;
                default: texts.Add(o.ToString() ?? ""); return;
            }
        }
        Add(item);
        if (texts.Count == 0 && container != null && !ReferenceEquals(container, item)) Add(container);
        return string.Join("  ", texts);
    }

    static void Apply()
    {
        var entries = Entries();
        int visible = 0;
        foreach (var (c, item) in entries)
        {
            bool show = _query.Length == 0 || (c is not Separator && SceneEdit.MatchScore(TextOf(item, c), _query) > 0);
            if (show)
            {
                if (_hidden.Remove(c)) c.IsVisible = true;
                if (c is not Separator && c.IsVisible) visible++;
            }
            else if (c.IsVisible) { c.IsVisible = false; _hidden.Add(c); }
        }
        ShowTip(_query.Length == 0 ? null : $"🔍 {_query}" + (visible == 0 ? "   — no match" : "   — Enter picks the first"));
    }

    static void ShowTip(string? text)
    {
        Control? owner = (Control?)_combo ?? _menu;
        if (text == null || owner == null)
        {
            if (_tipOwner != null)
            {
                ToolTip.SetIsOpen(_tipOwner, false);
                ToolTip.SetTip(_tipOwner, _savedTip);
                ToolTip.SetPlacement(_tipOwner, _savedPlacement);
                _tipOwner = null;
            }
            return;
        }
        if (!ReferenceEquals(_tipOwner, owner))
        {
            ShowTip(null);
            _tipOwner = owner;
            _savedTip = ToolTip.GetTip(owner);
            _savedPlacement = ToolTip.GetPlacement(owner);
            ToolTip.SetPlacement(owner, PlacementMode.Top);
        }
        ToolTip.SetTip(owner, text);
        ToolTip.SetIsOpen(owner, true);
    }

    /// <summary>Enter: the highlighted entry when it matches, else the first that does.</summary>
    static void PickFirst(Window w)
    {
        var entries = Entries().Where(x => x.Container.IsVisible && x.Container is not Separator && x.Container.IsEnabled).ToList();
        if (entries.Count == 0) return;
        var focused = w.FocusManager?.GetFocusedElement() as Control;
        var pick = entries.FirstOrDefault(x => ReferenceEquals(x.Container, focused));
        if (pick.Container == null) pick = entries[0];
        if (_combo is { IsDropDownOpen: true } cb)
        {
            int index = cb.IndexFromContainer(pick.Container);
            Reset();
            cb.SelectedIndex = index;
            cb.IsDropDownOpen = false;
            cb.Focus();
        }
        else if (_menu is { } menu && pick.Container is MenuItem mi)
        {
            if (mi.ItemCount > 0) { mi.IsSubMenuOpen = true; return; } // a submenu (Recent): open it
            Reset();
            menu.Close();
            mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
    }

    /// <summary>Shows every entry again and forgets the search.</summary>
    static void Reset()
    {
        foreach (var c in _hidden) c.IsVisible = true;
        _hidden.Clear();
        _query = "";
        ShowTip(null);
        _combo = null;
        _menu = null;
    }

    // ---- snap/test hooks: search an open list as if typing, and read what stays visible ----
    internal static void TestType(string text) { _query += text; Apply(); }
    internal static List<string> TestVisible() => Entries().Where(x => x.Container.IsVisible && x.Container is not Separator).Select(x => TextOf(x.Item, x.Container)).ToList();
    internal static void TestPick(Window w) => PickFirst(w);
}
