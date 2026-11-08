using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace BuilderEngine;

/// <summary>Small modal dialogs in the BuilderEngine look: a question with buttons, and a question with a text box.</summary>
static class Dialogs
{
    /// <summary>Asks a question. Returns the chosen button's text, or null when closed. The first button is the accent one.</summary>
    public static Task<string?> Ask(Window owner, string title, string message, params string[] buttons)
    {
        var dlg = Build(title, message, null, buttons, out _);
        return dlg.ShowDialog<string?>(owner);
    }

    /// <summary>Asks for a text. Returns it (trimmed) or null when cancelled.</summary>
    public static async Task<string?> Input(Window owner, string title, string message, string initial, string okText = "OK")
    {
        var dlg = Build(title, message, initial, [okText, "Cancel"], out var box);
        var r = await dlg.ShowDialog<string?>(owner);
        return r == okText ? (box!.Text ?? "").Trim() : null;
    }

    /// <summary>The dialog window (not shown). <paramref name="initial"/> != null adds a text box (Enter = first button).</summary>
    public static Window Build(string title, string message, string? initial, string[] buttons, out TextBox? box)
    {
        var dlg = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = Palette.Bg1, Foreground = Palette.Text,
        };
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 13 });
        TextBox? input = null;
        if (initial != null)
        {
            input = new TextBox
            {
                Text = initial, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
                FontSize = 13, MinHeight = 32, Padding = new Thickness(8, 6), CaretBrush = Palette.Accent,
            };
            input.Resources["TextControlBorderBrushFocused"] = Palette.Accent;
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; dlg.Close(buttons[0]); } };
            root.Children.Add(input);
            dlg.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            string text = buttons[i];
            var b = i == 0 ? MainWindow.AccentButton(text) : MainWindow.PlainButton(text);
            b.Click += (_, _) => dlg.Close(text);
            row.Children.Add(b);
        }
        root.Children.Add(row);
        dlg.Content = root;
        dlg.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; dlg.Close(null); } };
        box = input;
        return dlg;
    }
}
