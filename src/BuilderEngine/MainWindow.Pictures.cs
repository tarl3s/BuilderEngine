using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BuilderEngine.Script;

namespace BuilderEngine;

// PICTURES FEATURE: the project's images/ folder — an object's Picture, Image params (a dropdown with thumbnails and
// "Import picture…"), pictures in the scene editor, and the folders a playing world uses (images, saves).
public sealed partial class MainWindow
{
    ImageCache? _imageCache;

    /// <summary>The project's pictures, loaded once (follows project switches).</summary>
    ImageCache Pictures
    {
        get
        {
            _imageCache ??= new ImageCache(_projectDir);
            _imageCache.SetProject(_projectDir);
            return _imageCache;
        }
    }

    /// <summary>A playing world finds the project's pictures and saves its games in the project's saves folder.</summary>
    void UseProjectFiles(World w)
    {
        w.ProjectFolder = _projectDir;
        w.SaveFolder = Path.Combine(_projectDir, Project.SavesFolder);
    }

    static bool IsPictureType(string type) => type.ToLowerInvariant() is "image" or "picture";

    /// <summary>What an object shows in the scene: its Picture, else the first picture a mechanism param picks.</summary>
    string? ScenePictureOf(SceneObject o)
    {
        if (!string.IsNullOrWhiteSpace(o.Image)) return o.Image;
        foreach (var m in o.Mechanisms)
        {
            if (DefOf(m.Name, out _) is not { } def) continue;
            foreach (var p in def.Params)
            {
                if (!IsPictureType(p.Type)) continue;
                string v = m.Params.TryGetValue(p.Name, out var set) ? World.ToText(set) : World.ToText(ParamDefault(p));
                if (v.Trim().Length > 0) return v.Trim();
            }
        }
        return null;
    }

    sealed class NoPictureEntry { public override string ToString() => "(none)"; }
    sealed class ImportPictureEntry { public override string ToString() => "Import picture…"; }

    /// <summary>
    /// A dropdown of the project's pictures with thumbnails, "(none)" and "Import picture…". A name that isn't in the
    /// images folder is still shown, marked missing. Returns the control and a way to show another value without
    /// calling <paramref name="set"/>.
    /// </summary>
    (Control Box, Action<string> Show) PicturePicker(string value, Action<string> set)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
            FontSize = 13, MinWidth = 0,
            ItemTemplate = new FuncDataTemplate<object>((item, _) => PictureRow(item), supportsRecycling: false),
        };
        var none = new NoPictureEntry();
        string current = value;
        void Fill(string v)
        {
            current = v;
            var pictures = Images.List(_projectDir);
            var items = new List<object> { none };
            string? match = pictures.FirstOrDefault(s => string.Equals(s, v, StringComparison.OrdinalIgnoreCase))
                ?? (Images.Resolve(_projectDir, v) is { } path ? Path.GetFileName(path) : null);
            if (match == null && v.Trim().Length > 0) items.Add($"{v}  (missing)");
            items.AddRange(pictures);
            items.Add(new ImportPictureEntry());
            _inspectorSync = true;
            combo.ItemsSource = items;
            combo.SelectedItem = v.Trim().Length == 0 ? none : match != null ? items.FirstOrDefault(i => Equals(i, match)) : items[1];
            _inspectorSync = false;
        }
        Fill(value);
        combo.SelectionChanged += async (_, _) =>
        {
            if (_inspectorSync) return;
            switch (combo.SelectedItem)
            {
                case ImportPictureEntry:
                {
                    Fill(current);
                    if (await ImportPicturesAsync() is { } imported) { set(imported); RebuildInspector(); }
                    break;
                }
                case NoPictureEntry:
                    current = "";
                    set("");
                    break;
                case string s when !s.EndsWith("  (missing)"):
                    current = s;
                    set(s);
                    break;
            }
        };
        return (combo, Fill);
    }

    Control PictureRow(object? item)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (item is string name && !name.EndsWith("  (missing)"))
        {
            var thumb = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(4), Background = Palette.Bg0, ClipToBounds = true,
                Child = new Image { Source = Pictures.Get(name), Stretch = Stretch.Uniform }
            };
            RenderOptions.SetBitmapInterpolationMode(thumb, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            row.Children.Add(thumb);
        }
        row.Children.Add(new TextBlock
        {
            Text = item?.ToString() ?? "", VerticalAlignment = VerticalAlignment.Center, FontSize = 13,
            Foreground = item is ImportPictureEntry ? Palette.Accent : item is NoPictureEntry ? Palette.TextDim : Palette.Text,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return row;
    }

    /// <summary>Picks picture files and copies them into the project. Returns the last one's name there, or null.</summary>
    async Task<string?> ImportPicturesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import pictures", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Pictures (png, jpg, bmp)") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }]
        });
        string? last = null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path && ImportPictureFile(path) is { } name) last = name;
        return last;
    }

    /// <summary>Copies one picture into the project's images folder. Returns its name there, or null.</summary>
    string? ImportPictureFile(string path)
    {
        try
        {
            string name = Images.Import(_projectDir, path);
            Pictures.Clear();
            _canvas.InvalidateVisual();
            SetStatus(true, $"Imported {name}. Use it as a Picture, or in a script with look \"{name}\".");
            return name;
        }
        catch (Exception e)
        {
            SetStatus(false, $"Couldn't import {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }
}
