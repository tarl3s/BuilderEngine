using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// SCENE EDITING CONVENIENCES (the "little controls" EasyEngine has): commands on the whole selection (delete, duplicate,
// nudge, copy / cut / paste — also into another scene —, lock, draw order, alignment), the editor's keyboard shortcuts
// (docs/SHORTCUTS.md, F1) and the right-click menu on the scene. The logic lives in SceneEdit (BuilderEngine.Script),
// tested headless; here it is wired to the window, undo (one step per command, one per burst of arrow keys) and the status bar.
public sealed partial class MainWindow
{
    /// <summary>The copied objects (clipboard text from <see cref="SceneEdit.Copy"/>); shared by every scene and project of this run.</summary>
    static string _clipboard = "";
    /// <summary>Pastes since the last copy without a pointer: each lands a little further.</summary>
    static int _pasteCount;
    readonly TextBlock _cursorPos = new() { Foreground = Palette.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MinWidth = 96, Margin = new Thickness(12, 0, 0, 0) };

    /// <summary>The selected objects, in scene (draw) order.</summary>
    List<SceneObject> SelectedObjects() => _scene.Objects.Where(o => _canvas.IsSelected(o.Id)).ToList();

    /// <summary>Selects several objects (the inspector follows).</summary>
    void SelectMany(IEnumerable<int> ids, int primary = 0)
    {
        _canvas.SetSelection(ids, primary);
        RebuildInspector();
    }

    static string Names(IReadOnlyList<SceneObject> objs) =>
        objs.Count == 1 ? objs[0].Name : objs.Count <= 3 ? string.Join(", ", objs.Select(o => o.Name)) : $"{objs.Count} objects";

    /// <summary>The ids of the selected objects that may change; null (with a status line) when they are all locked or nothing is selected.</summary>
    List<int>? EditableSelection(string verb)
    {
        var sel = SelectedObjects();
        if (sel.Count == 0) return null;
        var ids = sel.Where(o => !o.Locked).Select(o => o.Id).ToList();
        if (ids.Count == 0) { SetStatus(false, $"Locked — unlock it to {verb} (Ctrl+L, or 🔒 in the inspector)."); return null; }
        return ids;
    }

    // ---------------- Commands on the selection ----------------

    void DeleteSelected()
    {
        if (EditableSelection("delete") is not { } ids) return;
        var gone = _scene.Objects.Where(o => ids.Contains(o.Id)).ToList();
        Checkpoint();
        SceneEdit.Delete(_scene, ids);
        SelectMany(_canvas.SelectedIds.Where(id => _scene.Find(id) != null).ToList()); // locked ones stay selected
        SetStatus(true, $"Deleted {Names(gone)}. Ctrl+Z brings {(gone.Count == 1 ? "it" : "them")} back.");
    }

    void DuplicateSelected()
    {
        if (EditableSelection("duplicate") is not { } ids) return;
        Checkpoint();
        var copies = SceneEdit.Duplicate(_scene, ids);
        SelectMany(copies.Select(c => c.Id));
        SetStatus(true, copies.Count == 1 ? $"Made {copies[0].Name}." : $"Duplicated {copies.Count} objects.");
    }

    /// <summary>Arrow keys: 1 px, or one grid cell with Shift. A burst of presses on the same objects is one undo step.</summary>
    void Nudge(int dx, int dy, bool shift)
    {
        if (EditableSelection("move") is not { } ids) return;
        double step = SceneEdit.NudgeStep(shift);
        Checkpoint("nudge" + string.Join(",", ids));
        SceneEdit.Nudge(_scene, ids, dx * step, dy * step);
        _canvas.InvalidateVisual();
        SyncTransformFields();
    }

    void SelectAll()
    {
        var ids = SceneEdit.All(_scene);
        SelectMany(ids);
        int locked = _scene.Objects.Count - ids.Count;
        SetStatus(true, $"{ids.Count} object(s) selected" + (locked > 0 ? $" ({locked} locked left out)." : "."));
    }

    void CopySelected(bool quiet = false)
    {
        var sel = SelectedObjects();
        if (sel.Count == 0) { SetStatus(true, "Select objects to copy."); return; }
        _clipboard = SceneEdit.Copy(_scene, sel.Select(o => o.Id));
        _pasteCount = 0;
        if (!quiet) SetStatus(true, $"Copied {Names(sel)} — Ctrl+V pastes at the mouse pointer (in any scene).");
    }

    void CutSelected()
    {
        if (EditableSelection("cut") is not { } ids) return;
        _clipboard = SceneEdit.Copy(_scene, ids);
        _pasteCount = 0;
        var gone = _scene.Objects.Where(o => ids.Contains(o.Id)).ToList();
        Checkpoint();
        SceneEdit.Delete(_scene, ids);
        SelectMany(_canvas.SelectedIds.Where(id => _scene.Find(id) != null).ToList());
        SetStatus(true, $"Cut {Names(gone)} — Ctrl+V pastes {(gone.Count == 1 ? "it" : "them")}.");
    }

    /// <summary>Pastes the copied objects: their top-left corner at <paramref name="at"/> (the pointer, or a right-click), else a little further each time.</summary>
    void PasteObjects(Point? at)
    {
        if (_play != null) return;
        if (SceneEdit.FromClipboard(_clipboard).Count == 0) { SetStatus(true, "Nothing to paste — select objects and press Ctrl+C first."); return; }
        Checkpoint();
        if (at == null) _pasteCount++;
        var copies = SceneEdit.Paste(_scene, _clipboard, at is { } p ? (p.X, p.Y) : null, _pasteCount);
        SelectMany(copies.Select(c => c.Id));
        _canvas.Focus();
        SetStatus(true, copies.Count == 1 ? $"Pasted {copies[0].Name}." : $"Pasted {copies.Count} objects.");
    }

    /// <summary>Locks the selection, or unlocks it when it is all locked already. One undo step.</summary>
    void ToggleLockSelected()
    {
        var sel = SelectedObjects();
        if (sel.Count == 0) return;
        SetLocked(sel, !sel.All(o => o.Locked));
    }

    void SetLocked(IReadOnlyList<SceneObject> objs, bool locked)
    {
        var change = objs.Where(o => o.Locked != locked).ToList();
        if (change.Count == 0) return;
        Checkpoint();
        foreach (var o in change) o.Locked = locked;
        _canvas.InvalidateVisual();
        RebuildInspector();
        SetStatus(true, locked
            ? $"Locked {Names(change)}: it can't be clicked, moved or deleted in the scene now. Unlock it from the scene's object list (click empty space) or the right-click menu."
            : $"Unlocked {Names(change)}.");
    }

    void ReorderSelected(ZMove move)
    {
        if (EditableSelection("change its order") is not { } ids) return;
        if (!SceneEdit.Reorder(_scene.Clone(), ids, move)) { SetStatus(true, move is ZMove.Front or ZMove.Forward ? "Already in front." : "Already at the back."); return; }
        Checkpoint();
        SceneEdit.Reorder(_scene, ids, move);
        _canvas.InvalidateVisual();
        RebuildInspector();
        SetStatus(true, move switch
        {
            ZMove.Front => "Brought to the front (drawn over the others).",
            ZMove.Back => "Sent to the back (drawn behind the others).",
            ZMove.Forward => "Brought forward one step.",
            _ => "Sent backward one step."
        });
    }

    void AlignSelected(AlignTo to)
    {
        if (EditableSelection("move") is not { } ids) return;
        if (!SceneEdit.Align(_scene.Clone(), ids, to)) return;
        Checkpoint();
        SceneEdit.Align(_scene, ids, to);
        _canvas.InvalidateVisual();
        SetStatus(true, $"Aligned {ids.Count} objects.");
    }

    void DistributeSelected(bool horizontal)
    {
        if (EditableSelection("move") is not { } ids) return;
        if (ids.Count < 3) { SetStatus(true, "Select 3 or more objects to space them evenly."); return; }
        if (!SceneEdit.Distribute(_scene.Clone(), ids, horizontal)) return;
        Checkpoint();
        SceneEdit.Distribute(_scene, ids, horizontal);
        _canvas.InvalidateVisual();
        SetStatus(true, $"Spaced {ids.Count} objects evenly.");
    }

    /// <summary>Sets an object's box to its picture's real pixel size (the top-left corner stays).</summary>
    void OriginalSize(SceneObject o)
    {
        if (o.Locked || PictureSizeOf(o) is not { } size) return;
        if (o.Width == size.Width && o.Height == size.Height) return;
        Checkpoint();
        o.Width = size.Width;
        o.Height = size.Height;
        _canvas.InvalidateVisual();
        RebuildInspector();
        SetStatus(true, $"Size set to the picture's {size.Width} × {size.Height} px.");
    }

    /// <summary>The pixel size of the picture an object shows (null = no picture).</summary>
    PixelSize? PictureSizeOf(SceneObject o) => Pictures.Get(ScenePictureOf(o)) is { } bmp && bmp.PixelSize.Width > 0 && bmp.PixelSize.Height > 0 ? bmp.PixelSize : null;

    double? PictureAspectOf(SceneObject o) => PictureSizeOf(o) is { } s ? (double)s.Width / s.Height : null;

    /// <summary>F2 / double-click: the Name field of the object, focused with its text selected (nothing selected: rename the scene).</summary>
    void BeginRename(int id)
    {
        if (_play != null) return;
        if (id == 0) { _ = RenameSceneAsync(); return; }
        if (_canvas.SelectedId != id || _canvas.SelectedIds.Count != 1) Select(id);
        ShowTab(scene: true);
        Dispatcher.UIThread.Post(() =>
        {
            if (_nameBox is not { IsEffectivelyEnabled: true } box) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The scene point where the mouse is, while it is over the scene.</summary>
    void ShowCursorPos(Point? world) => _cursorPos.Text = world is { } p ? $"X {Math.Round(p.X)}   Y {Math.Round(p.Y)}" : "";

    void AddObjectAt(Point at)
    {
        if (_play != null) return;
        Checkpoint();
        var o = _scene.Add(new SceneObject
        {
            Name = UniqueName("Object"),
            X = SceneEdit.Snap(at.X), Y = SceneEdit.Snap(at.Y),
            Color = Swatches[_scene.Objects.Count % Swatches.Length]
        });
        Select(o.Id);
        _canvas.Focus();
    }

    string UniqueName(string baseName) => SceneEdit.UniqueName(_scene, baseName);

    // ---------------- Keyboard ----------------

    /// <summary>The window's shortcuts (tunnel, so they work wherever the keyboard is): see EditorShortcuts / docs/SHORTCUTS.md.</summary>
    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool ctrl = (mods & KeyModifiers.Control) != 0, shift = (mods & KeyModifiers.Shift) != 0;
        if (ListSearch.Active) return; // an open dropdown or menu keeps the keyboard (and its search)
        var focus = FocusManager?.GetFocusedElement();
        bool typing = focus is TextBox or AvaloniaEdit.Editing.TextArea; // a text field, or the code editor
        bool numberField = typing && (focus as Visual)?.FindAncestorOfType<NumericUpDown>() != null;

        if (e.Key == Key.F1) { ShowShortcuts(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { SceneSave(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.O && !_snapMode) { _ = OpenProjectAsync(); e.Handled = true; return; }
        if (_play != null)
        {
            // F5 / F10 stay the debugger's (PlaySurface); Esc stops wherever the keyboard is (but not while typing a value).
            if ((e.Key == Key.Escape && !typing) || (e.Key == Key.F5 && shift)) { SceneStop(); e.Handled = true; }
            return;
        }
        if (e.Key == Key.F5 && mods == KeyModifiers.None) { ShowTab(true); ClearLog(); ScenePlay(); e.Handled = true; return; }
        if (!_sceneTab)
        {
            // The Mechanism tab: Ctrl+Z / Ctrl+Y undo the blocks wherever the keyboard is (the code box keeps its own text undo).
            if (ctrl && (e.Key == Key.Y || e.Key == Key.Z) && !typing && _blocksView != null && _mode != "code")
            {
                if (e.Key == Key.Y || shift) _blocksView.Redo(); else _blocksView.Undo();
                e.Handled = true;
            }
            return;
        }
        if (ctrl && (e.Key == Key.Y || e.Key == Key.Z) && (!typing || numberField))
        {
            // A number field's own text undo is no use: Ctrl+Z there undoes the scene, like everywhere else on the Scene tab.
            if (e.Key == Key.Y || shift) Redo(); else Undo();
            e.Handled = true;
            return;
        }
        if (typing || focus is ComboBox or ComboBoxItem or ListBox or ListBoxItem or MenuItem) return; // typing, or a list keeps its keys
        if (OnSceneKey(e)) e.Handled = true;
    }

    /// <summary>The scene editing keys (the keyboard isn't in a text field or a list). True when handled.</summary>
    bool OnSceneKey(KeyEventArgs e)
    {
        if (_play != null) return false;
        if (e.Key == Key.Escape && _canvas.CancelDrag()) { SetStatus(true, "Cancelled."); return true; }
        if (ToolKey(e)) return true;
        var mods = e.KeyModifiers;
        bool ctrl = (mods & KeyModifiers.Control) != 0, shift = (mods & KeyModifiers.Shift) != 0, alt = (mods & KeyModifiers.Alt) != 0;
        if (!ctrl && !alt)
            switch (e.Key)
            {
                case Key.Delete or Key.Back: DeleteSelected(); return true;
                case Key.Escape:
                    if (_canvas.SelectedIds.Count == 0) return false;
                    Select(0);
                    return true;
                case Key.Left: Nudge(-1, 0, shift); return true;
                case Key.Right: Nudge(1, 0, shift); return true;
                case Key.Up: Nudge(0, -1, shift); return true;
                case Key.Down: Nudge(0, 1, shift); return true;
                case Key.F2: BeginRename(_canvas.SelectedIds.Count == 1 ? _canvas.SelectedId : 0); return true;
                case Key.Home: _canvas.Fit(); return true;
            }
        if (!ctrl || alt) return false;
        switch (e.Key)
        {
            case Key.A: SelectAll(); return true;
            case Key.D: DuplicateSelected(); return true;
            case Key.C: CopySelected(); return true;
            case Key.X: CutSelected(); return true;
            case Key.V: PasteObjects(_canvas.PointerWorld); return true;
            case Key.L: ToggleLockSelected(); return true;
            case Key.OemCloseBrackets: ReorderSelected(shift ? ZMove.Front : ZMove.Forward); return true;
            case Key.OemOpenBrackets: ReorderSelected(shift ? ZMove.Back : ZMove.Backward); return true;
            case Key.D0 or Key.NumPad0: _canvas.Fit(); return true;
            case Key.D1 or Key.NumPad1: _canvas.ZoomTo(1); return true;
            case Key.OemPlus or Key.Add: _canvas.ZoomBy(1.25); return true;
            case Key.OemMinus or Key.Subtract: _canvas.ZoomBy(1 / 1.25); return true;
            case Key.PageUp: OpenScene(_sceneIndex - 1); return true;
            case Key.PageDown: OpenScene(_sceneIndex + 1); return true;
        }
        return false;
    }

    // ---------------- The right-click menu ----------------

    /// <summary>A right-click on the scene: the menu for the selection, a locked object, or empty space.</summary>
    void ShowSceneMenu(Point at, SceneObject? hit)
    {
        if (_play != null) return;
        var menu = BuildSceneMenu(at, hit);
        menu.Open(_canvas);
    }

    /// <summary>A menu row: the command on the left, its shortcut dimmed on the right.</summary>
    static MenuItem MenuRow(string text, string keys, Action run, bool enabled = true)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28,Auto"), MinWidth = 210 };
        grid.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var k = new TextBlock { Text = keys, Foreground = Palette.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(k, 2);
        grid.Children.Add(k);
        var mi = new MenuItem { Header = grid, IsEnabled = enabled, Tag = text };
        mi.Click += (_, _) => run();
        return mi;
    }

    ContextMenu BuildSceneMenu(Point at, SceneObject? hit)
    {
        var menu = new ContextMenu();
        var items = menu.Items;
        var sel = SelectedObjects();
        bool canPaste = SceneEdit.FromClipboard(_clipboard).Count > 0;

        if (hit is { Locked: true } && !_canvas.IsSelected(hit.Id))
        {
            // A locked object (it can't be clicked): unlock it or select it from here.
            var locked = hit;
            items.Add(MenuRow($"🔓 Unlock “{locked.Name}”", "", () => SetLocked([locked], false)));
            items.Add(MenuRow($"Select “{locked.Name}”", "", () => Select(locked.Id)));
            items.Add(new Separator());
            items.Add(MenuRow("Paste here", "Ctrl+V", () => PasteObjects(at), canPaste));
            return menu;
        }
        if (sel.Count == 0)
        {
            items.Add(MenuRow("Paste here", "Ctrl+V", () => PasteObjects(at), canPaste));
            items.Add(MenuRow("+ Object here", "", () => AddObjectAt(at)));
            items.Add(new Separator());
            items.Add(MenuRow("Select all", "Ctrl+A", SelectAll, _scene.Objects.Any(o => !o.Locked)));
            items.Add(MenuRow("Fit the view", "Home", _canvas.Fit));
            items.Add(MenuRow("Keyboard shortcuts", "F1", ShowShortcuts));
            return menu;
        }

        bool anyEditable = sel.Any(o => !o.Locked);
        bool allLocked = sel.All(o => o.Locked);
        items.Add(MenuRow("Cut", "Ctrl+X", CutSelected, anyEditable));
        items.Add(MenuRow("Copy", "Ctrl+C", () => CopySelected()));
        items.Add(MenuRow("Paste here", "Ctrl+V", () => PasteObjects(at), canPaste));
        items.Add(MenuRow("Duplicate", "Ctrl+D", DuplicateSelected, anyEditable));
        items.Add(MenuRow("Delete", "Del", DeleteSelected, anyEditable));
        items.Add(new Separator());
        if (sel.Count == 1) items.Add(MenuRow("Rename…", "F2", () => BeginRename(sel[0].Id)));
        items.Add(MenuRow("Bring to front", "Ctrl+Shift+]", () => ReorderSelected(ZMove.Front), anyEditable));
        items.Add(MenuRow("Bring forward", "Ctrl+]", () => ReorderSelected(ZMove.Forward), anyEditable));
        items.Add(MenuRow("Send backward", "Ctrl+[", () => ReorderSelected(ZMove.Backward), anyEditable));
        items.Add(MenuRow("Send to back", "Ctrl+Shift+[", () => ReorderSelected(ZMove.Back), anyEditable));
        items.Add(new Separator());
        items.Add(MenuRow(allLocked ? "🔓 Unlock" : "🔒 Lock", "Ctrl+L", ToggleLockSelected));
        if (sel.Count == 1 && PictureSizeOf(sel[0]) is { } px)
            items.Add(MenuRow($"Original size ({px.Width} × {px.Height})", "", () => OriginalSize(sel[0]), !sel[0].Locked));
        items.Add(new Separator());
        items.Add(MenuRow("Select all", "Ctrl+A", SelectAll));
        return menu;
    }

    /// <summary>The scene's undo snapshot keeps the selection's objects that still exist after an undo or redo.</summary>
    void KeepSelectionAfterRestore()
    {
        var keep = _canvas.SelectedIds.Where(id => _scene.Find(id) != null).ToList();
        _canvas.SetSelection(keep, _canvas.SelectedId);
    }
}
