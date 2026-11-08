using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BuilderEngine.Script;

namespace BuilderEngine;

// SOUNDS FEATURE: the project's sounds/ folder — the list under the library (▶ to hear one), "Import sound…",
// and the sounds a game plays ('play "coin.wav"' -> sounds/coin.wav through the audio player).
public sealed partial class MainWindow
{
    readonly StackPanel _soundList = new() { Spacing = 2 };
    readonly HashSet<string> _missingSoundsWarned = new(StringComparer.OrdinalIgnoreCase);

    Control SoundsPanel()
    {
        var header = new DockPanel { Margin = new Thickness(16, 10, 12, 6) };
        var import = LinkButton("+ Import", "Copy wav, mp3 or ogg files into this project's sounds folder");
        import.Click += (_, _) => _ = ImportSoundsAsync();
        DockPanel.SetDock(import, Dock.Right);
        header.Children.Add(import);
        header.Children.Add(Label("SOUNDS"));

        var scroll = new ScrollViewer { Content = _soundList, MaxHeight = 150, Margin = new Thickness(8, 0, 8, 10) };
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(scroll);
        RefreshSounds();
        return new Border { Child = panel, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    List<string> ProjectSounds() => Sounds.List(_projectDir);

    void RefreshSounds()
    {
        _soundList.Children.Clear();
        var list = ProjectSounds();
        if (list.Count == 0)
            _soundList.Children.Add(new TextBlock
            {
                Text = "No sounds yet. Import wav, mp3 or ogg files; scripts play them with play \"name.wav\".",
                TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 11, FontStyle = FontStyle.Italic, Margin = new Thickness(8, 0)
            });
        foreach (var name in list)
        {
            string n = name;
            var play = PreviewButton(() => n);
            var text = new TextBlock { Text = n, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
            var row = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(4, 0) };
            DockPanel.SetDock(play, Dock.Left);
            row.Children.Add(play);
            row.Children.Add(text);
            ToolTip.SetTip(row, $"play \"{n}\"");
            _soundList.Children.Add(row);
        }
    }

    /// <summary>A small ▶ that plays the sound <paramref name="name"/> returns at click time.</summary>
    Button PreviewButton(Func<string?> name)
    {
        var b = new Button
        {
            Content = "▶", FontSize = 10, Padding = new Thickness(7, 3), MinWidth = 0, Background = Palette.Bg2, Foreground = Palette.Accent,
            BorderBrush = Palette.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(b, "Listen");
        b.Click += (_, _) => { if (name() is { Length: > 0 } n) PreviewSound(n); };
        return b;
    }

    void PreviewSound(string name)
    {
        if (Sounds.Resolve(_projectDir, name) is { } path) { _audio.Play(path); SetStatus(true, $"♪ {name}"); }
        else SetStatus(false, $"There's no sound “{name}” in this project's sounds folder.");
    }

    /// <summary>A game played a sound ('play "coin.wav"'): find it in the project and play it.</summary>
    void PlayGameSound(string name)
    {
        if (Sounds.Resolve(_projectDir, name) is { } path) _audio.Play(path);
        else if (name.Length > 0 && _missingSoundsWarned.Add(name))
            Log($"⚠ There's no sound “{name}” in the project's sounds folder. Import it with Project ▸ Import sound…");
    }

    /// <summary>Picks sound files and copies them into the project. Returns the last one's name there, or null.</summary>
    async Task<string?> ImportSoundsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import sounds", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Sounds (wav, mp3, ogg)") { Patterns = ["*.wav", "*.mp3", "*.ogg"] }]
        });
        string? last = null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path && ImportSoundFile(path) is { } name) last = name;
        return last;
    }

    /// <summary>Copies one sound file into the project's sounds folder. Returns its name there, or null.</summary>
    string? ImportSoundFile(string path)
    {
        try
        {
            string name = Sounds.Import(_projectDir, path);
            _missingSoundsWarned.Remove(name);
            RefreshSounds();
            RebuildInspector();
            SetStatus(true, $"Imported {name}. Use it with play \"{name}\" or in a Sound field.");
            return name;
        }
        catch (Exception e)
        {
            SetStatus(false, $"Couldn't import {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }
}
