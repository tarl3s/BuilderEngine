using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// The mouse wheel changes the number in the field you clicked into (X, a speed, the scene's width, a number in a block),
/// applied at every notch so the change shows live: up = more, Shift = ×10, Ctrl = a tenth of the step. Only a focused
/// field: just scrolling past one never changes it (the panel scrolls instead). The inspector's fields add one undo step
/// per burst of notches (their Checkpoint key). The step math is <see cref="SceneEdit.WheelValue"/> / <see cref="SceneEdit.WheelText"/>.
/// </summary>
static class NumberWheel
{
    static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        InputElement.PointerWheelChangedEvent.AddClassHandler<NumericUpDown>(OnNumber, RoutingStrategies.Tunnel);
        InputElement.PointerWheelChangedEvent.AddClassHandler<TextBox>(OnText, RoutingStrategies.Tunnel);
    }

    static (int Dir, bool Shift, bool Ctrl) Notch(PointerWheelEventArgs e)
    {
        double d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X; // Shift+wheel may come in sideways
        return (Math.Sign(d), (e.KeyModifiers & KeyModifiers.Shift) != 0, (e.KeyModifiers & KeyModifiers.Control) != 0);
    }

    static void OnNumber(NumericUpDown n, PointerWheelEventArgs e)
    {
        if (e.Handled || !n.IsKeyboardFocusWithin || !n.IsEnabled || n.IsReadOnly) return;
        var (dir, shift, ctrl) = Notch(e);
        if (dir == 0) return;
        n.Value = (decimal)Step(n, dir, shift, ctrl);
        e.Handled = true;
    }

    /// <summary>The value a notch gives (also used by the snap checks).</summary>
    internal static double Step(NumericUpDown n, int dir, bool shift, bool ctrl) =>
        SceneEdit.WheelValue((double)(n.Value ?? 0), dir, (double)n.Increment, shift, ctrl, (double)n.Minimum, (double)n.Maximum);

    static void OnText(TextBox t, PointerWheelEventArgs e)
    {
        if (e.Handled || !t.IsKeyboardFocusWithin || t.IsReadOnly || !t.IsEnabled || t.AcceptsReturn) return;
        if (t.FindAncestorOfType<NumericUpDown>() != null) return; // the number field above does it
        var (dir, shift, ctrl) = Notch(e);
        if (dir == 0 || SceneEdit.WheelText(t.Text, dir, shift, ctrl) is not { } next) return; // not a plain number: scroll as usual
        t.Text = next;
        t.CaretIndex = next.Length;
        e.Handled = true;
    }
}
