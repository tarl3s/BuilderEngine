using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// ANIMATIONS + SPRITE EDITOR FEATURE (inspector side). An object's Animations row: the default animation (picked) and
// "🎞 Edit…" (AnimationWindow.cs). Picture dropdowns get "✎ Draw…" (SpriteEditorWindow.cs). Params of type
// 'animation' (the Animator's idle/walk/jump) pick one of the object's animations or numbered pictures.
// Runtime: Script/World.Prefabs.cs (default animation on start) and World.Sprites.cs (animate "walk").
public sealed partial class MainWindow
{
    /// <summary>The "Animations" field of an object (or a prefab): the default one and the editor button.</summary>
    Control AnimationsRow(SceneObject o)
    {
        var row = new DockPanel();
        var edit = PlainButton(o.Animations.Count == 0 ? "🎞 Add animations…" : $"🎞 Edit… ({o.Animations.Count})");
        edit.Padding = new Thickness(10, 5);
        edit.FontSize = 12;
        ToolTip.SetTip(edit, "Named animations (idle, walk, jump…) made of the project's pictures, a sprite sheet or drawn frames; scripts play them with animate \"walk\"");
        edit.Click += (_, _) => _ = OpenAnimationsAsync(o);
        if (o.Animations.Count == 0)
        {
            edit.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(edit);
            return Field("Animations", row);
        }
        DockPanel.SetDock(edit, Dock.Right);
        edit.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(edit);
        var names = o.Animations.Select(a => a.Name).ToList();
        var combo = new ComboBox
        {
            ItemsSource = names, SelectedItem = o.ResolveDefaultAnimation()?.Name, HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, FontSize = 13, MinWidth = 0
        };
        ToolTip.SetTip(combo, "The animation that plays when the game starts");
        combo.SelectionChanged += (_, _) =>
        {
            if (_inspectorSync || combo.SelectedItem is not string n || n == o.ResolveDefaultAnimation()?.Name) return;
            Checkpoint();
            o.DefaultAnimation = n;
            _canvas.InvalidateVisual();
        };
        row.Children.Add(combo);
        return Field("Animations (default)", row);
    }

    /// <summary>Opens the Animations window for an object; its changes come back as one undo step.</summary>
    async Task OpenAnimationsAsync(SceneObject o)
    {
        int id = o.Id;
        var w = new AnimationWindow(_projectDir, Pictures, o.Name, o.Animations, o.DefaultAnimation);
        await w.ShowDialog(this);
        ApplyAnimations(id == 0 ? o : _scene.Find(id) ?? o, w);
    }

    void ApplyAnimations(SceneObject o, AnimationWindow w)
    {
        if (!w.Changed) return;
        Checkpoint();
        o.Animations = w.Animations;
        o.DefaultAnimation = w.Animations.Count == 0 ? null : w.DefaultAnimation;
        Pictures.Clear();
        _canvas.InvalidateVisual();
        RebuildInspector();
        SetStatus(true, w.Animations.Count == 0 ? $"{o.Name} has no animations now." : $"{o.Name} has {w.Animations.Count} animation(s); “{o.ResolveDefaultAnimation()?.Name}” plays when the game starts.");
    }

    /// <summary>A picture dropdown's "✎ Draw…": the sprite editor with the current picture (or a new one). Returns the saved name.</summary>
    async Task<string?> DrawPictureAsync(string current)
    {
        var editor = new SpriteEditorWindow(_projectDir, current.Trim().Length > 0 ? current : null);
        await editor.ShowDialog(this);
        if (editor.SavedName is not { } saved) return null;
        Pictures.Clear();
        _canvas.InvalidateVisual();
        SetStatus(true, $"Saved {saved} in the images folder.");
        return saved;
    }

    sealed class DrawPictureEntry { public override string ToString() => "✎ Draw…"; }

    /// <summary>
    /// A param of type 'animation': one of the object's animations, a numbered-pictures animation of the project
    /// (walk1.png, walk2.png… → "walk"), or (none).
    /// </summary>
    Control AnimationPicker(SceneObject o, string value, Action<string> set)
    {
        var options = new List<string> { NoAnimation };
        options.AddRange(o.Animations.Select(a => a.Name));
        foreach (var stem in NumberedAnimations())
            if (!options.Contains(stem, StringComparer.OrdinalIgnoreCase)) options.Add(stem);
        return ChoicePicker(options, value.Trim().Length == 0 ? NoAnimation : value, v => set(v == NoAnimation ? "" : v));
    }

    const string NoAnimation = "(none)";

    /// <summary>"walk" for walk1.png, walk2.png… in the images folder (two frames or more).</summary>
    IEnumerable<string> NumberedAnimations() => Images.List(_projectDir)
        .Select(f => Path.GetFileNameWithoutExtension(f))
        .Where(s => s.Length > 1 && char.IsAsciiDigit(s[^1]))
        .Select(s => s.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd('_', '-', ' '))
        .Where(s => s.Length > 0)
        .GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2).Select(g => g.Key);
}
