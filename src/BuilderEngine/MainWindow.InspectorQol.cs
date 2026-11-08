using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// SCENE EDITING CONVENIENCES in the inspector: 🔒 Lock (a locked object's inspector is read-only), Keep proportions and
// Original size, the draw order, the panel for several selected objects (align, space evenly, order, lock, duplicate,
// delete) and the scene's object list (the way to pick a locked object, which the scene can't click).
public sealed partial class MainWindow
{
    /// <summary>The selected object's Name field (F2 and double-click focus it).</summary>
    TextBox? _nameBox;
    const string KeepEnabledTag = "lock";

    Button LockButton(SceneObject o)
    {
        var b = LinkButton(o.Locked ? "🔒 Locked" : "🔓 Lock", o.Locked
            ? "Unlock it to move, resize, delete or edit it (Ctrl+L)"
            : "Lock it: it can't be clicked, moved, resized or deleted in the scene, so you don't move it by accident (Ctrl+L). The game ignores the lock.");
        if (o.Locked) b.Foreground = Palette.Text;
        b.Click += (_, _) => SetLocked(_canvas.SelectedIds.Count > 1 ? SelectedObjects() : [o], !o.Locked);
        return b;
    }

    static TextBlock LockedNote() => new()
    {
        Text = "Locked — unlock it to edit. Locked objects can't be clicked in the scene; pick them in the scene's object list (click empty space) or right-click them.",
        TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12, Tag = KeepEnabledTag
    };

    /// <summary>A locked object's inspector: every row but the header's lock button (and the note) is disabled and dimmed.</summary>
    void DisableAllBut(Panel head, Control keep)
    {
        foreach (var c in _inspectorRoot.Children)
        {
            if (c.Tag as string == KeepEnabledTag) continue;
            if (ReferenceEquals(c, head))
            {
                foreach (var part in head.Children.OfType<Panel>().SelectMany(p => p.Children))
                    if (!ReferenceEquals(part, keep)) { part.IsEnabled = false; part.Opacity = 0.55; }
                continue;
            }
            c.IsEnabled = false;
            c.Opacity = 0.55;
        }
    }

    /// <summary>Width or Height typed (or wheeled) in the inspector; with Keep proportions the other side follows.</summary>
    void SetSide(SceneObject o, double v, bool width)
    {
        double? ratio = o.KeepAspect ? PictureAspectOf(o) ?? (o.Height > 0 ? o.Width / o.Height : null) : null;
        if (width) o.Width = v; else o.Height = v;
        if (ratio is { } r && r > 0)
        {
            double other = SceneEdit.OtherSide(v, r, width);
            if (width) o.Height = other; else o.Width = other;
            _inspectorSync = true;
            if (width && _numH != null) _numH.Value = (decimal)other;
            if (!width && _numW != null) _numW.Value = (decimal)other;
            _inspectorSync = false;
        }
        _canvas.InvalidateVisual();
    }

    Control SizeRow(SceneObject o)
    {
        var wrap = new WrapPanel();
        var keep = new CheckBox
        {
            Content = "Keep proportions", IsChecked = o.KeepAspect, Foreground = Palette.Text, FontSize = 12, Margin = new Thickness(0, 0, 12, 0)
        };
        ToolTip.SetTip(keep, "Resizing keeps the picture's proportions (or the box's). Hold Shift while dragging a handle to keep them just once.");
        keep.IsCheckedChanged += (_, _) =>
        {
            if (_inspectorSync || o.KeepAspect == (keep.IsChecked == true)) return;
            Checkpoint();
            o.KeepAspect = keep.IsChecked == true;
        };
        var size = PictureSizeOf(o);
        var orig = LinkButton("Original size", size is { } s ? $"Use the picture's real size: {s.Width} × {s.Height} px" : "Give it a picture first: this sets the size to the picture's real pixels.");
        orig.IsEnabled = size != null;
        orig.Click += (_, _) => OriginalSize(o);
        wrap.Children.Add(keep);
        wrap.Children.Add(orig);
        return wrap;
    }

    /// <summary>To back / Back / Forward / To front, and the place in the draw order.</summary>
    Control OrderRow(SceneObject? o)
    {
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        void Add(string text, string tip, ZMove move)
        {
            var b = SmallButton(text, tip);
            b.Margin = new Thickness(0, 0, 6, 4);
            b.FontSize = 12;
            b.Click += (_, _) => ReorderSelected(move);
            wrap.Children.Add(b);
        }
        Add("To back", "Send to back: drawn behind every other object (Ctrl+Shift+[)", ZMove.Back);
        Add("Back", "Send backward one step (Ctrl+[)", ZMove.Backward);
        Add("Forward", "Bring forward one step (Ctrl+])", ZMove.Forward);
        Add("To front", "Bring to front: drawn over every other object (Ctrl+Shift+])", ZMove.Front);
        if (o != null)
        {
            var (place, count) = SceneEdit.OrderOf(_scene, o.Id);
            wrap.Children.Add(new TextBlock { Text = $"{place} of {count}", Foreground = Palette.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 4) });
        }
        var field = Field("Draw order", wrap);
        ToolTip.SetTip(field, "Objects later in the order are drawn on top (in the editor and in the game, among objects of the same layer).");
        return field;
    }

    // ---------------- Several objects selected ----------------

    void MultiInspector()
    {
        var sel = SelectedObjects();
        int locked = sel.Count(o => o.Locked);
        var head = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bool allLocked = locked == sel.Count;
        var lockAll = LinkButton(allLocked ? "🔒 Unlock" : "🔓 Lock", "Lock or unlock all of them (Ctrl+L)");
        lockAll.Click += (_, _) => SetLocked(sel, !allLocked);
        var dup = LinkButton("Duplicate", "Ctrl+D");
        dup.Click += (_, _) => DuplicateSelected();
        var del = LinkButton("Delete", "Delete key");
        del.Foreground = Palette.Error;
        del.Click += (_, _) => DeleteSelected();
        actions.Children.Add(lockAll);
        actions.Children.Add(dup);
        actions.Children.Add(del);
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(Label($"{sel.Count} OBJECTS"));
        _inspectorRoot.Children.Add(head);
        _inspectorRoot.Children.Add(Note(
            "Drag any of them to move them all · arrow keys nudge them (Shift = a grid cell) · Ctrl+C / Ctrl+V copy them (into another scene too) · Shift+click takes one out."
            + (locked > 0 ? $"\n{locked} of them locked: those stay where they are." : ""), Palette.TextDim));

        var align = new WrapPanel();
        void AlignButton(string text, string tip, Action act)
        {
            var b = SmallButton(text, tip);
            b.Margin = new Thickness(0, 0, 6, 6);
            b.FontSize = 12;
            b.Click += (_, _) => act();
            align.Children.Add(b);
        }
        AlignButton("Left", "Line up their left edges", () => AlignSelected(AlignTo.Left));
        AlignButton("Centre", "Line up their centres (across)", () => AlignSelected(AlignTo.CenterX));
        AlignButton("Right", "Line up their right edges", () => AlignSelected(AlignTo.Right));
        AlignButton("Top", "Line up their tops", () => AlignSelected(AlignTo.Top));
        AlignButton("Middle", "Line up their middles (up and down)", () => AlignSelected(AlignTo.Middle));
        AlignButton("Bottom", "Line up their bottoms (e.g. on the ground)", () => AlignSelected(AlignTo.Bottom));
        _inspectorRoot.Children.Add(Field("Align", align));

        var spread = new WrapPanel();
        var across = SmallButton("Across", "Equal gaps from left to right (3 or more objects)");
        across.Margin = new Thickness(0, 0, 6, 0);
        across.FontSize = 12;
        across.Click += (_, _) => DistributeSelected(horizontal: true);
        var down = SmallButton("Up and down", "Equal gaps from top to bottom (3 or more objects)");
        down.FontSize = 12;
        down.Click += (_, _) => DistributeSelected(horizontal: false);
        spread.Children.Add(across);
        spread.Children.Add(down);
        _inspectorRoot.Children.Add(Field("Space evenly", spread));
        _inspectorRoot.Children.Add(OrderRow(null));

        var list = new StackPanel { Spacing = 2 };
        foreach (var o in sel)
        {
            var b = LinkButton((o.Locked ? "🔒 " : "") + o.Name, "Select only this one");
            b.Foreground = Palette.Text;
            b.HorizontalAlignment = HorizontalAlignment.Left;
            int id = o.Id;
            b.Click += (_, _) => Select(id);
            list.Children.Add(b);
        }
        _inspectorRoot.Children.Add(Field("Selected", list));
    }

    // ---------------- The scene's object list ----------------

    /// <summary>Every object of the scene, the front one first: click to select it (locked ones too), 🔒 to lock or unlock it.</summary>
    Control ObjectList()
    {
        var panel = new StackPanel { Spacing = 0, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(Label("OBJECTS"));
        int shown = 0;
        for (int i = _scene.Objects.Count - 1; i >= 0 && shown < 300; i--, shown++)
        {
            var o = _scene.Objects[i];
            var row = new DockPanel { Margin = new Thickness(0, 1) };
            var lockB = new Button
            {
                Content = o.Locked ? "🔒" : "🔓", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 0),
                Foreground = o.Locked ? Palette.Text : Palette.TextDim, Opacity = o.Locked ? 1 : 0.45, FontSize = 12
            };
            ToolTip.SetTip(lockB, o.Locked ? $"Unlock {o.Name}" : $"Lock {o.Name}: it can't be clicked or moved in the scene");
            var obj = o;
            lockB.Click += (_, _) => SetLocked([obj], !obj.Locked);
            DockPanel.SetDock(lockB, Dock.Right);
            row.Children.Add(lockB);
            var name = new Button
            {
                Content = new TextBlock { Text = o.Name, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = o.Locked ? Palette.TextDim : Palette.Text, FontSize = 12 },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left
            };
            ToolTip.SetTip(name, "Select it");
            name.Click += (_, _) => { Select(obj.Id); _canvas.Focus(); };
            row.Children.Add(name);
            panel.Children.Add(row);
        }
        if (_scene.Objects.Count > shown) panel.Children.Add(Note($"…and {_scene.Objects.Count - shown} more.", Palette.TextDim));
        return panel;
    }
}
