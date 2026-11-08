using System.Globalization;
using System.Text;

namespace BuilderEngine.Script;

/// <summary>Where the selected objects go in the draw order.</summary>
public enum ZMove { Front, Forward, Backward, Back }

/// <summary>How a group of objects lines up (the selection's outer box is the reference).</summary>
public enum AlignTo { Left, CenterX, Right, Top, Middle, Bottom }

/// <summary>
/// The scene editor's small conveniences as plain logic (no UI), so they are tested headless: arrow-key nudges, the
/// selection box, select all, copy/paste and duplicate of several objects, delete, the draw order, alignment, a resize
/// that keeps the proportions and the mouse wheel stepping a number. Locked objects are skipped by everything that
/// picks or changes objects in the scene (they can still be chosen from the scene's object list).
/// </summary>
public static class SceneEdit
{
    /// <summary>The scene grid (objects snap to it; Shift+arrow moves one cell).</summary>
    public const double Grid = 8;
    /// <summary>How far a duplicate or a paste without a pointer lands from the original (two grid cells).</summary>
    public const double CopyOffset = 16;

    // ---------------- Picking ----------------

    /// <summary>The top object (drawn last) under a point; locked ones only when <paramref name="includeLocked"/>.</summary>
    public static SceneObject? At(Scene s, double x, double y, bool includeLocked = false)
    {
        for (int i = s.Objects.Count - 1; i >= 0; i--)
        {
            var o = s.Objects[i];
            if (!includeLocked && o.Locked) continue;
            if (x >= o.X && x <= o.X + o.Width && y >= o.Y && y <= o.Y + o.Height) return o;
        }
        return null;
    }

    /// <summary>The unlocked objects a selection box (two corners, any order) touches, in scene order.</summary>
    public static List<int> InBox(Scene s, double x1, double y1, double x2, double y2)
    {
        double l = Math.Min(x1, x2), r = Math.Max(x1, x2), t = Math.Min(y1, y2), b = Math.Max(y1, y2);
        var ids = new List<int>();
        foreach (var o in s.Objects)
            if (!o.Locked && o.X <= r && o.X + Math.Max(1, o.Width) >= l && o.Y <= b && o.Y + Math.Max(1, o.Height) >= t) ids.Add(o.Id);
        return ids;
    }

    /// <summary>Ctrl+A: every unlocked object.</summary>
    public static List<int> All(Scene s) => s.Objects.Where(o => !o.Locked).Select(o => o.Id).ToList();

    /// <summary>The objects with these ids that may be changed (not locked), in scene order.</summary>
    public static List<SceneObject> Editable(Scene s, IEnumerable<int> ids)
    {
        var set = ids as ISet<int> ?? ids.ToHashSet();
        return s.Objects.Where(o => set.Contains(o.Id) && !o.Locked).ToList();
    }

    // ---------------- Moving ----------------

    /// <summary>One arrow-key press: 1 px, or one grid cell with Shift.</summary>
    public static double NudgeStep(bool shift) => shift ? Grid : 1;

    /// <summary>Moves the unlocked objects by (dx, dy). Returns how many moved.</summary>
    public static int Nudge(Scene s, IEnumerable<int> ids, double dx, double dy)
    {
        var list = Editable(s, ids);
        foreach (var o in list) { o.X += dx; o.Y += dy; }
        return list.Count;
    }

    public static double Snap(double v, double grid = Grid) => Math.Round(v / grid) * grid;

    /// <summary>
    /// A drag of several objects: the grabbed one snaps to the grid (unless <paramref name="snap"/> is off) and the others
    /// move by the same amount, so the group keeps its shape. <paramref name="axisLock"/> (Shift) keeps only the
    /// larger of the two movements. Returns the movement to apply to every original position.
    /// </summary>
    public static (double Dx, double Dy) DragDelta(double grabbedX, double grabbedY, double rawDx, double rawDy, bool snap, bool axisLock)
    {
        if (axisLock) { if (Math.Abs(rawDx) >= Math.Abs(rawDy)) rawDy = 0; else rawDx = 0; }
        if (!snap) return (rawDx, rawDy);
        double dx = rawDx == 0 ? 0 : Snap(grabbedX + rawDx) - grabbedX;
        double dy = rawDy == 0 ? 0 : Snap(grabbedY + rawDy) - grabbedY;
        return (dx, dy);
    }

    // ---------------- Resizing ----------------

    /// <summary>
    /// Dragging a resize handle (0 top-left, 1 top, 2 top-right, 3 right, 4 bottom-right, 5 bottom, 6 bottom-left, 7 left)
    /// of a box to the point (ex, ey). With a <paramref name="ratio"/> (width / height) the proportions are kept: a corner
    /// follows the side that moved most and stays anchored at the opposite corner; a side handle keeps the box centred on
    /// the other axis.
    /// </summary>
    public static (double X, double Y, double W, double H) Resize(double x, double y, double w, double h, int handle, double ex, double ey, double? ratio, double min = 4)
    {
        double l = x, t = y, r = x + w, b = y + h;
        bool left = handle is 0 or 6 or 7, right = handle is 2 or 3 or 4, top = handle is 0 or 1 or 2, bottom = handle is 4 or 5 or 6;
        if (left) l = Math.Min(ex, r - min);
        if (right) r = Math.Max(ex, l + min);
        if (top) t = Math.Min(ey, b - min);
        if (bottom) b = Math.Max(ey, t + min);
        if (ratio is not { } k || k <= 0 || !double.IsFinite(k)) return (l, t, r - l, b - t);

        double W = r - l, H = b - t;
        bool horiz = left || right, vert = top || bottom;
        if (horiz && vert)
        {
            if (Math.Abs(W - w) / Math.Max(1e-9, w) >= Math.Abs(H - h) / Math.Max(1e-9, h)) H = W / k;
            else W = H * k;
        }
        else if (horiz) H = W / k;
        else W = H * k;
        if (W < min) { W = min; H = W / k; }
        if (H < min) { H = min; W = H * k; }
        double L = left ? x + w - W : horiz ? x : x + (w - W) / 2;
        double T = top ? y + h - H : vert ? y : y + (h - H) / 2;
        return (L, T, W, H);
    }

    /// <summary>The other side when one is typed with Keep proportions on (rounded to 2 decimals, at least 1).</summary>
    public static double OtherSide(double typed, double ratio, bool typedWidth) =>
        Math.Max(1, Math.Round(typedWidth ? typed / ratio : typed * ratio, 2));

    // ---------------- Copy, paste, duplicate, delete ----------------

    /// <summary>The objects as clipboard text (deep copies, in scene order so they stack the same way when pasted).</summary>
    public static string Copy(Scene s, IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        var copy = new Scene { Objects = s.Objects.Where(o => set.Contains(o.Id)).Select(o => o.Clone()).ToList() };
        return copy.ToJson();
    }

    /// <summary>The objects held by a clipboard text (empty when it isn't one).</summary>
    public static List<SceneObject> FromClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        try { return Scene.FromJson(text).Objects; }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// How far pasted objects move: with a point (the pointer, or where the scene was right-clicked) the group's top-left
    /// corner lands there, on the grid; without one each paste lands <see cref="CopyOffset"/> further than the last.
    /// </summary>
    public static (double Dx, double Dy) PasteOffset(IReadOnlyList<SceneObject> copies, (double X, double Y)? at, int pasteNumber)
    {
        if (copies.Count == 0) return (0, 0);
        if (at is { } p) return (Snap(p.X) - copies.Min(o => o.X), Snap(p.Y) - copies.Min(o => o.Y));
        double d = CopyOffset * Math.Max(1, pasteNumber);
        return (d, d);
    }

    /// <summary>Adds the clipboard's objects to the scene (new ids and names, unlocked, moved by the offset). Returns them.</summary>
    public static List<SceneObject> Paste(Scene s, string clipboard, (double X, double Y)? at, int pasteNumber)
    {
        var copies = FromClipboard(clipboard);
        var (dx, dy) = PasteOffset(copies, at, pasteNumber);
        foreach (var c in copies) AddCopy(s, c, dx, dy);
        return copies;
    }

    /// <summary>Ctrl+D: copies of the unlocked objects next to them. Returns the copies (the new selection).</summary>
    public static List<SceneObject> Duplicate(Scene s, IEnumerable<int> ids, double offset = CopyOffset)
    {
        var copies = new List<SceneObject>();
        foreach (var o in Editable(s, ids)) copies.Add(AddCopy(s, o.Clone(), offset, offset));
        return copies;
    }

    static SceneObject AddCopy(Scene s, SceneObject c, double dx, double dy)
    {
        c.Name = UniqueName(s, EditorTools.BaseName(c.Name)); // added one by one: two copies of Coin become Coin 2 and Coin 3
        c.X = Math.Round(c.X + dx, 2);
        c.Y = Math.Round(c.Y + dy, 2);
        c.Locked = false; // a copy is there to be placed
        return s.Add(c);
    }

    /// <summary>Removes the unlocked objects. Returns how many went.</summary>
    public static int Delete(Scene s, IEnumerable<int> ids)
    {
        var list = Editable(s, ids);
        foreach (var o in list) s.Objects.Remove(o);
        return list.Count;
    }

    /// <summary>"Coin", or "Coin 2", "Coin 3"... — the first name no object of the scene has.</summary>
    public static string UniqueName(Scene s, string baseName)
    {
        bool Taken(string n) => s.Objects.Any(o => string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(baseName)) return baseName;
        int i = 2;
        while (Taken($"{baseName} {i}")) i++;
        return $"{baseName} {i}";
    }

    // ---------------- Draw order ----------------

    /// <summary>
    /// Moves the objects in the draw order (later = drawn on top, here and in the game among objects of the same layer).
    /// Locked objects stay where they are. Returns true when something moved.
    /// </summary>
    public static bool Reorder(Scene s, IEnumerable<int> ids, ZMove move)
    {
        var set = Editable(s, ids).Select(o => o.Id).ToHashSet();
        if (set.Count == 0) return false;
        var before = s.Objects.Select(o => o.Id).ToList();
        var list = s.Objects;
        switch (move)
        {
            case ZMove.Front:
            {
                var moving = list.Where(o => set.Contains(o.Id)).ToList();
                list.RemoveAll(o => set.Contains(o.Id));
                list.AddRange(moving);
                break;
            }
            case ZMove.Back:
            {
                var moving = list.Where(o => set.Contains(o.Id)).ToList();
                list.RemoveAll(o => set.Contains(o.Id));
                list.InsertRange(0, moving);
                break;
            }
            case ZMove.Forward: // each one swaps with the next object that isn't moving
                for (int i = list.Count - 2; i >= 0; i--)
                    if (set.Contains(list[i].Id) && !set.Contains(list[i + 1].Id)) (list[i], list[i + 1]) = (list[i + 1], list[i]);
                break;
            case ZMove.Backward:
                for (int i = 1; i < list.Count; i++)
                    if (set.Contains(list[i].Id) && !set.Contains(list[i - 1].Id)) (list[i], list[i - 1]) = (list[i - 1], list[i]);
                break;
        }
        return !before.SequenceEqual(s.Objects.Select(o => o.Id));
    }

    /// <summary>"3 of 12": the object's place in the draw order (1 = at the back).</summary>
    public static (int Place, int Count) OrderOf(Scene s, int id) => (s.Objects.FindIndex(o => o.Id == id) + 1, s.Objects.Count);

    // ---------------- Alignment ----------------

    /// <summary>Lines up the unlocked objects on an edge or centre of the box around all of them. Returns true when something moved.</summary>
    public static bool Align(Scene s, IEnumerable<int> ids, AlignTo to)
    {
        var list = Editable(s, ids);
        if (list.Count < 2) return false;
        double l = list.Min(o => o.X), r = list.Max(o => o.X + o.Width), t = list.Min(o => o.Y), b = list.Max(o => o.Y + o.Height);
        bool moved = false;
        foreach (var o in list)
        {
            double x = o.X, y = o.Y;
            switch (to)
            {
                case AlignTo.Left: x = l; break;
                case AlignTo.Right: x = r - o.Width; break;
                case AlignTo.CenterX: x = Math.Round((l + r) / 2 - o.Width / 2, 2); break;
                case AlignTo.Top: y = t; break;
                case AlignTo.Bottom: y = b - o.Height; break;
                case AlignTo.Middle: y = Math.Round((t + b) / 2 - o.Height / 2, 2); break;
            }
            if (x != o.X || y != o.Y) moved = true;
            o.X = x; o.Y = y;
        }
        return moved;
    }

    /// <summary>Spreads 3+ unlocked objects so the gaps between them are equal (the first and last stay put).</summary>
    public static bool Distribute(Scene s, IEnumerable<int> ids, bool horizontal)
    {
        var list = Editable(s, ids);
        if (list.Count < 3) return false;
        list = horizontal ? list.OrderBy(o => o.X).ToList() : list.OrderBy(o => o.Y).ToList();
        double start = horizontal ? list[0].X : list[0].Y;
        double end = horizontal ? list[^1].X + list[^1].Width : list[^1].Y + list[^1].Height;
        double sizes = list.Sum(o => horizontal ? o.Width : o.Height);
        double gap = (end - start - sizes) / (list.Count - 1);
        double at = start;
        bool moved = false;
        foreach (var o in list)
        {
            double v = Math.Round(at, 2);
            if (horizontal) { moved |= o.X != v; o.X = v; at += o.Width + gap; }
            else { moved |= o.Y != v; o.Y = v; at += o.Height + gap; }
        }
        return moved;
    }

    // ---------------- Mouse wheel on numbers ----------------

    /// <summary>One wheel notch on a number field: its step, ×10 with Shift, a tenth with Ctrl.</summary>
    public static double WheelStep(double increment, bool shift, bool ctrl)
    {
        double step = increment > 0 ? increment : 1;
        if (shift) step *= 10;
        if (ctrl) step /= 10;
        return step;
    }

    /// <summary>The field's value after one notch (dir 1 = up), kept between min and max and rounded to the step's decimals.</summary>
    public static double WheelValue(double value, int dir, double increment, bool shift, bool ctrl, double min = double.MinValue, double max = double.MaxValue)
    {
        double step = WheelStep(increment, shift, ctrl);
        int decimals = Math.Clamp(Decimals(step), 0, 6);
        double v = Math.Round(value + Math.Sign(dir) * step, decimals);
        return Math.Clamp(v, min, max);
    }

    /// <summary>
    /// One notch on a text box holding a plain number: the step follows the number (1 for 12, 0.1 for 0.8), ×10 with Shift,
    /// a tenth with Ctrl. Null when the text isn't a plain number (a formula, a word). A number that was 0 or more doesn't go below 0.
    /// </summary>
    public static string? WheelText(string? text, int dir, bool shift, bool ctrl)
    {
        var s = (text ?? "").Trim().Replace(',', '.');
        if (s.Length == 0 || !double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return null;
        int dot = s.IndexOf('.');
        int decimals = dot < 0 ? 0 : Math.Min(4, s.Length - dot - 1);
        double step = Math.Pow(10, -decimals);
        if (shift) step *= 10;
        if (ctrl) { step /= 10; decimals = Math.Min(4, decimals + 1); }
        double n = Math.Round(v + Math.Sign(dir) * step, decimals);
        if (v >= 0 && n < 0) n = 0;
        return n.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);
    }

    static int Decimals(double step)
    {
        for (int d = 0; d <= 6; d++)
            if (Math.Abs(step * Math.Pow(10, d) - Math.Round(step * Math.Pow(10, d))) < 1e-9) return d;
        return 6;
    }

    // ---------------- Type to search in lists ----------------

    /// <summary>Lower case, no accents, letters and digits only (anything else — emoji, symbols — becomes one space), trimmed.</summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        bool space = true;
        foreach (char c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); space = false; }
            else if (!space) { sb.Append(' '); space = true; }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>How well a text matches a typed search: 3 starts with it, 2 a word starts with it, 1 contains it, 0 no match.</summary>
    public static int MatchScore(string? text, string? query)
    {
        string f = Fold(text), q = Fold(query);
        if (q.Length == 0) return 1;
        int i = f.IndexOf(q, StringComparison.Ordinal);
        if (i < 0) return 0;
        if (i == 0) return 3;
        return f.IndexOf(" " + q, StringComparison.Ordinal) >= 0 ? 2 : 1;
    }
}
