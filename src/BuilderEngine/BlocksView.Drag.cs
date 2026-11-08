using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BuilderEngine.Script;

namespace BuilderEngine;

// BLOCKS FEATURE: drag and drop. Press a block (or a palette item) and move the pointer: a see-through copy follows it
// and an orange line shows where it will land. Release to drop; drop on the palette to delete. The list work itself
// (where it lands, refusing a block inside itself) is in BlockEdits, so it is tested headless.
sealed partial class BlocksView
{
    enum DragKind { Palette, Stmt, When, Param, Uses }

    /// <summary>What is being dragged: a palette item (by id) or an existing block (its list and index).</summary>
    sealed record DragSrc(DragKind Kind, System.Collections.IList? List, int Index, string? PaletteId, Control Visual, Action? Click = null);

    /// <summary>A statement list on screen: its area, its blocks (top to bottom), mechanism and nesting depth.</summary>
    sealed record StmtSlot(List<Stmt> List, Control Area, List<Control> Items, int Mech, int Depth);

    /// <summary>A mechanism on screen and the columns of its params, uses and whens.</summary>
    sealed record MechSlot(Control Panel, Panel Params, Panel Uses, Panel Whens);

    /// <summary>Where a drop lands: a list and an index (or the delete zone), and where to draw the line.</summary>
    sealed record DropAt(System.Collections.IList? List, int Index, int Mech, Rect Line, bool Delete = false);

    const double DragThreshold = 5;

    readonly List<StmtSlot> _stmtSlots = [];
    readonly List<MechSlot> _mechSlots = [];
    int _buildDepth;

    readonly Canvas _layer = new() { IsHitTestVisible = false };
    readonly Border _line = new() { Background = Palette.Accent, Height = 3, CornerRadius = new CornerRadius(1.5), IsVisible = false };
    readonly Image _ghost = new() { Opacity = 0.65, IsVisible = false, Stretch = Stretch.None };
    readonly Border _trash = MakeTrash();

    DragSrc? _press;
    Point _pressAt, _grab;
    bool _dragging;
    DropAt? _target;
    IPointer? _pointer;

    Control BuildDragLayer()
    {
        _trash.IsVisible = false;
        _layer.Children.Add(_trash);
        _layer.Children.Add(_line);
        _layer.Children.Add(_ghost);
        return _layer;
    }

    /// <summary>Behind the palette while an existing block is dragged: a red frame, filled with the hint when the pointer is over it.</summary>
    static readonly IBrush TrashHot = new SolidColorBrush(Color.Parse("#3A1512"), 0.94).ToImmutable();

    static Border MakeTrash() => new()
    {
        Background = Brushes.Transparent,
        BorderBrush = Palette.Error, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6),
        Child = new TextBlock
        {
            Text = BlockText("Drop here to delete"), Foreground = BlockColors.ErrorText, FontWeight = FontWeight.SemiBold,
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 18, 8, 8)
        }
    };

    void InitDrag()
    {
        AddHandler(PointerMovedEvent, (_, e) => { if (_press != null) DragMove(e.GetPosition(_layer)); }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (_press == null) return;
            e.Handled = true;
            DragEnd(e.GetPosition(_layer), drop: true);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        // Losing the capture (another window, a flyout) cancels; the row losing its implicit capture to this view does not.
        AddHandler(PointerCaptureLostEvent, (_, e) => { if (_press != null && ReferenceEquals(e.Source, this)) CancelDrag(); }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>A left press on something draggable: wait to see whether it becomes a drag or stays a click.</summary>
    void Press(PointerPressedEventArgs e, DragSrc src)
    {
        e.Handled = true;
        if (_stale || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _press = src;
        _pressAt = e.GetPosition(_layer);
        _grab = e.GetPosition(src.Visual);
        _dragging = false;
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
    }

    void DragMove(Point p)
    {
        if (_press == null) return;
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressAt.X) < DragThreshold && Math.Abs(p.Y - _pressAt.Y) < DragThreshold) return;
            StartDrag();
        }
        AutoScroll(p);
        Canvas.SetLeft(_ghost, p.X - _grab.X);
        Canvas.SetTop(_ghost, p.Y - _grab.Y);
        _target = FindTarget(p);
        ShowTarget(_target);
    }

    void StartDrag()
    {
        var src = _press!;
        _dragging = true;
        _ghost.Source = Picture(src.Visual);
        _ghost.IsVisible = true;
        if (src.Kind != DragKind.Palette)
        {
            src.Visual.Opacity = 0.35;
            var pal = BoundsIn(_palette);
            if (pal is { } r)
            {
                _trash.Width = Math.Max(0, r.Width - 8);
                _trash.Height = Math.Max(0, r.Height - 8);
                Canvas.SetLeft(_trash, r.X + 4);
                Canvas.SetTop(_trash, r.Y + 4);
                _trash.IsVisible = true;
            }
        }
    }

    /// <summary>Releases the pointer: drops (when dragging) or runs the palette item's click.</summary>
    void DragEnd(Point p, bool drop)
    {
        var src = _press;
        bool dragging = _dragging;
        var target = dragging ? FindTarget(p) : null;
        EndDragVisuals();
        if (src == null) return;
        if (!dragging) { if (drop) src.Click?.Invoke(); return; }
        if (drop && target != null) Drop(src, target);
    }

    /// <summary>Stops a drag without changing anything. True if one was going on.</summary>
    bool CancelDrag()
    {
        if (_press == null) return false;
        EndDragVisuals();
        return true;
    }

    void EndDragVisuals()
    {
        if (_press != null) _press.Visual.Opacity = 1;
        var pointer = _pointer;
        _press = null;
        _pointer = null;
        _dragging = false;
        _target = null;
        _ghost.IsVisible = false;
        _ghost.Source = null;
        _line.IsVisible = false;
        _trash.IsVisible = false;
        if (pointer?.Captured == this) pointer.Capture(null);
    }

    void AutoScroll(Point p)
    {
        if (BoundsIn(_scroll) is not { } r || p.X < r.Left || p.X > r.Right) return;
        double dy = p.Y < r.Top + 28 ? -14 : p.Y > r.Bottom - 28 ? 14 : 0;
        if (dy != 0) _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(_scroll.Offset.Y + dy, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
    }

    // ---------------- Where it lands ----------------

    Rect? BoundsIn(Control c)
    {
        var p = c.TranslatePoint(default, _layer);
        return p == null ? null : new Rect(p.Value, c.Bounds.Size);
    }

    static double Distance(Rect r, Point p)
    {
        double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        double dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    DropAt? FindTarget(Point p)
    {
        var src = _press!;
        if (src.Kind != DragKind.Palette && BoundsIn(_palette) is { } pal && pal.Contains(p))
            return new DropAt(src.List, src.Index, -1, default, Delete: true);
        if (BoundsIn(_scroll) is not { } view || !view.Contains(p) || _defs.Count == 0) return null;

        bool statement = src.Kind == DragKind.Stmt || (src.Kind == DragKind.Palette && !BlockEdits.IsMechanismLevel(src.PaletteId!));
        return statement ? StatementTarget(p, view, src) : MechanismTarget(p, view, src);
    }

    DropAt? StatementTarget(Point p, Rect view, DragSrc src)
    {
        var moving = src.Kind == DragKind.Stmt ? (Stmt)src.List![src.Index]! : null;
        StmtSlot? best = null;
        Rect bestRect = default;
        double bestDist = double.MaxValue;
        foreach (var slot in _stmtSlots)
        {
            if (moving != null && BlockEdits.Contains(moving, slot.List)) continue; // never inside itself
            if (BoundsIn(slot.Area) is not { } r || !r.Intersects(view)) continue;
            // Inside an area wins (the deepest one); otherwise the nearest area takes it.
            double d = r.Contains(p) ? -1 - slot.Depth : Distance(r, p);
            if (d < bestDist || (d == bestDist && best != null && slot.Depth > best.Depth)) { best = slot; bestRect = r; bestDist = d; }
        }
        if (best == null) return null;
        var rows = Rows(best.Items);
        int index = BlockEdits.DropIndex(rows, p.Y);
        return new DropAt(best.List, index, best.Mech, LineAt(rows, index, bestRect, bestRect.Top + 4));
    }

    DropAt? MechanismTarget(Point p, Rect view, DragSrc src)
    {
        var kind = src.Kind == DragKind.Palette ? src.PaletteId switch { "param" => DragKind.Param, "uses" => DragKind.Uses, _ => DragKind.When } : src.Kind;
        int mi = -1;
        Rect mechRect = default;
        double bestDist = double.MaxValue;
        for (int k = 0; k < _mechSlots.Count && k < _defs.Count; k++)
        {
            if (BoundsIn(_mechSlots[k].Panel) is not { } r) continue;
            double d = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            if (d < bestDist) { mi = k; mechRect = r; bestDist = d; }
        }
        if (mi < 0) return null;
        var slot = _mechSlots[mi];
        var m = _defs[mi];
        var (panel, list) = kind switch
        {
            DragKind.Param => (slot.Params, (System.Collections.IList)m.Params),
            DragKind.Uses => (slot.Uses, m.Uses),
            _ => (slot.Whens, (System.Collections.IList)m.Whens)
        };
        var rows = Rows(panel.Children);
        int index = BlockEdits.DropIndex(rows, p.Y);
        double emptyY = BoundsIn(panel) is { } pr ? pr.Top + 2 : mechRect.Top;
        return new DropAt(list, index, mi, LineAt(rows, index, mechRect, emptyY));
    }

    List<(double Top, double Bottom)> Rows(IEnumerable<Control> items)
    {
        var rows = new List<(double, double)>();
        foreach (var c in items)
            if (BoundsIn(c) is { } r) rows.Add((r.Top, r.Bottom));
        return rows;
    }

    /// <summary>The insertion line: in the gap before block <paramref name="index"/>, across the column.</summary>
    static Rect LineAt(List<(double Top, double Bottom)> rows, int index, Rect column, double emptyY)
    {
        double y = rows.Count == 0 ? emptyY
            : index == 0 ? rows[0].Top - 2
            : index >= rows.Count ? rows[^1].Bottom + 1
            : (rows[index - 1].Bottom + rows[index].Top) / 2;
        return new Rect(column.Left + 2, y - 1.5, Math.Max(20, column.Width - 4), 3);
    }

    void ShowTarget(DropAt? t)
    {
        bool hot = t?.Delete == true;
        _trash.Background = hot ? TrashHot : Brushes.Transparent;
        _trash.Child!.IsVisible = hot;
        if (t == null || t.Delete) { _line.IsVisible = false; return; }
        Canvas.SetLeft(_line, t.Line.X);
        Canvas.SetTop(_line, t.Line.Y);
        _line.Width = t.Line.Width;
        _line.IsVisible = true;
    }

    static RenderTargetBitmap? Picture(Control c)
    {
        int w = (int)Math.Ceiling(c.Bounds.Width), h = (int)Math.Ceiling(c.Bounds.Height);
        if (w <= 0 || h <= 0) return null;
        var bmp = new RenderTargetBitmap(new PixelSize(Math.Min(w, 2000), Math.Min(h, 2000)), new Vector(96, 96));
        bmp.Render(c);
        return bmp;
    }

    // ---------------- Dropping ----------------

    void Drop(DragSrc src, DropAt t)
    {
        if (_stale) return;
        if (t.Delete)
        {
            if (src.List == null || src.Index < 0 || src.Index >= src.List.Count) return;
            src.List.RemoveAt(src.Index);
            Commit();
            return;
        }
        if (t.List == null) return;
        bool changed;
        switch (src.Kind)
        {
            case DragKind.Palette:
                object added;
                switch (src.PaletteId)
                {
                    case "when":
                        var w = BlockEdits.NewWhen(EventKind.Start);
                        added = w;
                        _selBody = w.Body;
                        break;
                    case "param": added = BlockEdits.NewParam(_defs[t.Mech]); break;
                    case "uses": added = BlockEdits.NewUses(); break;
                    default:
                        added = BlockEdits.NewStatement(src.PaletteId!);
                        _selBody = (List<Stmt>)t.List; // the place you dropped into becomes the selected one
                        break;
                }
                t.List.Insert(Math.Clamp(t.Index, 0, t.List.Count), added);
                _selMech = t.Mech;
                _reveal = added;
                changed = true;
                break;
            case DragKind.Stmt:
                changed = BlockEdits.MoveStatement((List<Stmt>)src.List!, src.Index, (List<Stmt>)t.List, t.Index);
                if (changed) { _selBody = (List<Stmt>)t.List; _selMech = t.Mech; }
                break;
            case DragKind.When:
                changed = BlockEdits.MoveItem((List<WhenDef>)src.List!, src.Index, (List<WhenDef>)t.List, t.Index);
                break;
            case DragKind.Param:
                changed = BlockEdits.MoveItem((List<ParamDef>)src.List!, src.Index, (List<ParamDef>)t.List, t.Index);
                break;
            case DragKind.Uses:
                changed = BlockEdits.MoveItem((List<UsesDef>)src.List!, src.Index, (List<UsesDef>)t.List, t.Index);
                break;
            default:
                changed = false;
                break;
        }
        if (changed) Commit();
    }

    // ---------------- Dev snapshots (--snap) ----------------

    /// <summary>
    /// Starts a real drag of the first block of the last top-level body and holds it over the gap after the first block
    /// of the first body, so a snapshot shows the ghost and the insertion line. False when there aren't two such bodies.
    /// </summary>
    internal bool DebugBeginDrag(bool toPalette = false)
    {
        var bodies = _stmtSlots.Where(s => s.Depth == 0 && s.Items.Count > 0).ToList();
        if (bodies.Count < 2) return false;
        var from = bodies[^1];
        var to = bodies[0];
        var block = from.Items[0];
        if (BoundsIn(block) is not { } b || BoundsIn(to.Items[0]) is not { } t) return false;
        _press = new DragSrc(DragKind.Stmt, from.List, 0, null, block);
        _grab = new Point(30, 10);
        _pressAt = new Point(b.X + 30, b.Y + 10);
        var pal = BoundsIn(_palette);
        DragMove(toPalette && pal is { } r ? r.Center : new Point(t.X + 60, t.Bottom + 4));
        return _dragging && _target != null && _target.Delete == toPalette;
    }

    /// <summary>Ends the drag started by <see cref="DebugBeginDrag"/>: drops it there (or cancels).</summary>
    internal void DebugEndDrag(bool drop)
    {
        var target = _target;
        var src = _press;
        EndDragVisuals();
        if (drop && src != null && target != null) Drop(src, target);
    }
}

// Dev snapshots, part 2: real pointer events (press, moves, release) sent through the same handlers a mouse uses.
sealed partial class BlocksView
{
    readonly Dictionary<string, Border> _paletteItems = new();

    /// <summary>
    /// Presses a palette item and releases it on the gap after the first block of the first body (when
    /// <paramref name="drag"/>), or releases it in place (a click). Returns false when there is nothing to aim at.
    /// </summary>
    internal bool DebugPointer(string paletteId, bool drag)
    {
        if (!_paletteItems.TryGetValue(paletteId, out var item) || TopLevel.GetTopLevel(this) is not { } root) return false;
        var first = _stmtSlots.FirstOrDefault(s => s.Depth == 0 && s.Items.Count > 0);
        if (first == null) return false;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        Point At(Control c, Point local) => c.TranslatePoint(local, root) ?? default;
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var held = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        var start = At(item, new Point(20, 10));
        item.RaiseEvent(new PointerPressedEventArgs(item, pointer, root, start, 0, down, KeyModifiers.None));
        var end = start;
        if (drag)
        {
            var block = first.Items[0];
            end = At(block, new Point(60, block.Bounds.Height + 4));
            var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
            foreach (var p in new[] { mid, end })
                (pointer.Captured as Interactive ?? this).RaiseEvent(new PointerEventArgs(PointerMovedEvent, this, pointer, root, p, 0, held, KeyModifiers.None));
        }
        (pointer.Captured as Interactive ?? this).RaiseEvent(new PointerReleasedEventArgs(this, pointer, root, end, 0, up, KeyModifiers.None, MouseButton.Left));
        return true;
    }
}
