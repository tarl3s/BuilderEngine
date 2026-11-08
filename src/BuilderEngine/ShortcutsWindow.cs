using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>The "Keyboard shortcuts" help (F1 or the ? button): every line of <see cref="EditorShortcuts.All"/>, by group.</summary>
static class ShortcutsHelp
{
    /// <summary>The list itself (also rendered by the --snap dev tool).</summary>
    public static Control Content()
    {
        var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 22), Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text, Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(new TextBlock
        {
            Text = "Most commands are also in the scene's right-click menu. Esc closes this window.",
            Foreground = Palette.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        });
        foreach (var group in EditorShortcuts.All.GroupBy(s => s.Group))
        {
            stack.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 1,
                Foreground = Palette.Accent, Margin = new Thickness(0, 12, 0, 4)
            });
            foreach (var s in group)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("270,*"), Margin = new Thickness(0, 2) };
                var keys = new Border
                {
                    Background = Palette.Bg2, BorderBrush = Palette.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0),
                    Child = new TextBlock { Text = s.Keys, FontFamily = Palette.Mono, FontSize = 12, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap }
                };
                var what = new TextBlock { Text = s.What, FontSize = 12.5, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(what, 1);
                row.Children.Add(keys);
                row.Children.Add(what);
                stack.Children.Add(row);
            }
        }
        return new Border { Background = Palette.Bg1, Child = stack };
    }
}

public sealed partial class MainWindow
{
    Window? _shortcutsWindow;

    /// <summary>F1 / the ? button: the shortcuts list in its own window (one at a time; F1 again brings it forward).</summary>
    void ShowShortcuts()
    {
        if (_snapMode) return; // no extra windows in dev snapshots (SnapQol renders the list instead)
        if (_shortcutsWindow != null) { _shortcutsWindow.Activate(); return; }
        var w = new Window
        {
            Title = "Keyboard shortcuts — BuilderEngine", Width = 720, Height = 720, MinWidth = 520, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Palette.Bg1, Foreground = Palette.Text,
            Content = new ScrollViewer { Content = ShortcutsHelp.Content() }
        };
        w.KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.F1) { e.Handled = true; w.Close(); } };
        w.Closed += (_, _) => _shortcutsWindow = null;
        _shortcutsWindow = w;
        w.Show(this);
    }
}
