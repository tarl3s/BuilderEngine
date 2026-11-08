using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace BuilderEngine;

// LINK FEATURE on the start screen (docs/EASYENGINE-LINK.md): "Open EasyEngine project…" (a .eeproj file) and the
// "EasyEngine" badge on recent projects saved as a .eeproj. "Open project folder…" also takes a folder with a .eeproj.
public sealed partial class LauncherWindow
{
    Control OpenEasyEngineButton()
    {
        var b = MainWindow.PlainButton("Open EasyEngine project…");
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTip.SetTip(b, "Open a .eeproj made in EasyEngine: both engines can edit it, even at the same time");
        b.Click += (_, _) => _ = OpenEasyEngineAsync();
        return b;
    }

    async Task OpenEasyEngineAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open an EasyEngine project", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("EasyEngine project (.eeproj)") { Patterns = ["*.eeproj"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) OpenEditor(path);
    }

    static Control RecentTitle(string name, bool exists, bool easy)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = exists ? Palette.Text : Palette.TextDim, VerticalAlignment = VerticalAlignment.Center });
        if (easy)
        {
            var badge = new Border
            {
                Background = Palette.AccentSoft, BorderBrush = Palette.AccentBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "EasyEngine", FontSize = 11, Foreground = Palette.Text },
            };
            ToolTip.SetTip(badge, "An EasyEngine project (.eeproj): saving here saves into it");
            row.Children.Add(badge);
        }
        return row;
    }
}
