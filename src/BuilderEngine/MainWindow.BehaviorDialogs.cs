using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// BEHAVIORS FEATURE, the windows (MainWindow.Behaviors.cs has the rest): a package's code, read-only (with "Make a copy to
// edit"), and "Save as behavior…" — a mechanism becomes behaviors/<Name>.behavior with a name, an icon picked from a small
// list, a category, a description and a label and help for each setting (BehaviorPackage.FromMechanism + Save).
public sealed partial class MainWindow
{
    /// <summary>The icons "Save as behavior…" offers (picked, not typed).</summary>
    internal static readonly string[] BehaviorIcons =
        ["⚙", "↻", "➤", "↔", "🎯", "⭐", "🪙", "💎", "🗝", "🚪", "🚧", "🎚", "🔼", "✨", "💥", "💢", "❤", "🛡", "🔫", "🏹",
         "💡", "🔊", "🎵", "💬", "🏔", "🌀", "🔲", "🗑", "⏱", "🎲", "👾", "🐝", "🧱", "🌊", "🔥", "❄"];

    /// <summary>The categories "Save as behavior…" offers.</summary>
    internal static readonly string[] BehaviorCategories = ["General", "Movement", "Level", "Items", "Combat", "Looks", "Sound", "Camera", "UI"];

    static Window DialogWindow(string title, double width)
    {
        var dlg = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = Palette.Bg1, Foreground = Palette.Text,
        };
        AccentResources(dlg.Resources);
        dlg.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; dlg.Close(null); } };
        return dlg;
    }

    static TextBox DialogBox(string text, string? watermark = null, bool multiline = false) => new()
    {
        Text = text, Watermark = watermark, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
        FontSize = 13, MinHeight = multiline ? 64 : 30, Padding = new Thickness(8, 5), CaretBrush = Palette.Accent,
        AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    // ---------------- A package's code ----------------

    /// <summary>The package's code, read-only, with "Make a copy to edit". Returns the window (shown; --snap renders it).</summary>
    Window? PackageCodeWindow(string name)
    {
        if (!_packages.TryGetValue(name, out var pkg)) return null;
        var dlg = DialogWindow($"{pkg.Name} — behavior package", 680);
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18), Spacing = 10 };
        root.Children.Add(new TextBlock { Text = $"{pkg.Icon}  {pkg.Name}", FontSize = 16, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock { Text = PackageTip(pkg), TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12 });
        var code = new CodeEditor { IsReadOnly = true, Height = 340, ShowLineNumbers = true, Text = pkg.Script };
        code.RefreshColors(_sources.Keys);
        root.Children.Add(new Border { Child = code, BorderBrush = Palette.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true });
        root.Children.Add(new TextBlock
        {
            Text = "A behavior's code can't be changed here (it may be shared by other projects and by EasyEngine). Make a copy to edit it as one of this project's mechanisms.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = AccentButton("Make a copy to edit");
        copy.Click += (_, _) => { dlg.Close(); CopyPackageToEdit(pkg.Name); };
        var close = PlainButton("Close");
        close.Click += (_, _) => dlg.Close();
        row.Children.Add(copy);
        row.Children.Add(close);
        root.Children.Add(row);
        dlg.Content = root;
        return dlg;
    }

    async Task ShowPackageCodeAsync(string name)
    {
        if (PackageCodeWindow(name) is not { } dlg) return;
        if (_snapMode) { dlg.Show(this); return; }
        await dlg.ShowDialog(this);
    }

    // ---------------- Save as behavior ----------------

    /// <summary>
    /// The "Save as behavior…" window for a mechanism (not shown) and what its Save button does: saves
    /// behaviors/&lt;Name&gt;.behavior and returns null, or returns what is wrong (shown in the window).
    /// </summary>
    (Window Window, Func<Task<string?>> Save, Action<string, string, string> Fill)? SaveAsBehaviorWindow(string mech)
    {
        if (!_sources.TryGetValue(mech, out var source)) return null;
        BehaviorPackage draft;
        try { draft = BehaviorPackage.FromMechanism(source); }
        catch (Exception e) when (e is FormatException or ScriptError)
        {
            SetStatus(false, $"“{mech}” can't be a behavior: {e.Message}");
            return null;
        }
        var dlg = DialogWindow("Save as behavior", 560);
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18), Spacing = 10 };
        root.Children.Add(new TextBlock { Text = $"Save “{mech}” as a behavior", FontSize = 15, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "A behavior is a mechanism with a name, an icon and friendly settings that both BuilderEngine and EasyEngine understand. It is saved in this project's behaviors folder; install or export it to share it.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12,
        });

        var name = DialogBox(draft.Name);
        root.Children.Add(Field("Name (letters, digits and _)", name));

        string icon = BehaviorIcons[0];
        var icons = new WrapPanel();
        var iconButtons = new List<Border>();
        foreach (var ic in BehaviorIcons)
        {
            var b = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 4, 4), Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = ic, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Tag = ic,
            };
            ToolTip.SetTip(b, "Use this icon");
            b.PointerPressed += (_, _) => { icon = ic; Paint(); };
            iconButtons.Add(b);
            icons.Children.Add(b);
        }
        void Paint()
        {
            foreach (var b in iconButtons)
            {
                bool on = (string)b.Tag! == icon;
                b.Background = on ? Palette.AccentSoft : Palette.Bg2;
                b.BorderBrush = on ? Palette.Accent : Palette.Border;
                b.BorderThickness = new Thickness(on ? 2 : 1);
            }
        }
        Paint();
        root.Children.Add(Field("Icon", icons));

        var category = new ComboBox
        {
            ItemsSource = BehaviorCategories, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border, FontSize = 13,
        };
        root.Children.Add(Field("Category", category));
        var description = DialogBox("", "What it does, in a sentence or two (the AI reads it too)", multiline: true);
        root.Children.Add(Field("Description", description));

        var labels = new List<(BehaviorPackage.PackageParam P, TextBox Label, TextBox Help)>();
        if (draft.Params.Count > 0)
        {
            root.Children.Add(new TextBlock { Text = "SETTINGS", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.TextDim, Margin = new Thickness(0, 6, 0, 0) });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,8,*"), RowSpacing = 6 };
            for (int i = 0; i < draft.Params.Count; i++)
            {
                var pp = draft.Params[i];
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var title = new TextBlock { Text = pp.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(title, $"{pp.Name}: {pp.Type}");
                var label = DialogBox(pp.Label, "label");
                var help = DialogBox("", "help (a tooltip)");
                Grid.SetRow(title, i); Grid.SetRow(label, i); Grid.SetRow(help, i);
                Grid.SetColumn(label, 1); Grid.SetColumn(help, 3);
                grid.Children.Add(title); grid.Children.Add(label); grid.Children.Add(help);
                labels.Add((pp, label, help));
            }
            root.Children.Add(grid);
        }

        var problem = new TextBlock { Foreground = Palette.Error, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        root.Children.Add(problem);

        async Task<string?> Save()
        {
            string newName = (name.Text ?? "").Trim();
            string src = newName == draft.Name ? source : MechanismLibrary.RenameSource(source, draft.Name, newName);
            BehaviorPackage pkg;
            try { pkg = BehaviorPackage.FromMechanism(src, (description.Text ?? "").Trim()); }
            catch (Exception e) when (e is FormatException or ScriptError) { return e.Message; }
            pkg.Icon = icon;
            pkg.Category = category.SelectedItem as string ?? "General";
            foreach (var (pp, label, help) in labels)
                if (pkg.Params.FirstOrDefault(x => x.Name == pp.Name) is { } q)
                {
                    q.Label = (label.Text ?? "").Trim() is { Length: > 0 } l ? l : pp.Name.Replace('_', ' ');
                    q.Help = (help.Text ?? "").Trim();
                }
            var errors = pkg.Validate();
            if (errors.Count > 0) return string.Join(" ", errors);
            string path = BehaviorPackage.FileIn(BehaviorPackage.ProjectFolder(_projectDir), pkg.Name);
            if (File.Exists(path) && !_snapMode
                && await Dialogs.Ask(this, "Already a behavior", $"This project already has a behavior called “{pkg.Name}”. Replace it?", "Replace", "Cancel") != "Replace")
                return "Not saved: pick another name.";
            try { pkg.Save(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Couldn't save it: {e.Message}"; }
            ReloadPackages();
            SetStatus(true, pkg.Name == mech
                ? $"“{mech}” is a behavior now ({BehaviorPackage.ProjectFolderName}/{pkg.Name}{BehaviorPackage.Extension}) — install or export it to share it."
                : $"Saved the behavior “{pkg.Name}” ({BehaviorPackage.ProjectFolderName}/{pkg.Name}{BehaviorPackage.Extension}).");
            return null;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var save = AccentButton("Save behavior");
        save.Click += async (_, _) =>
        {
            string? why = await Save();
            if (why == null) { dlg.Close("saved"); return; }
            problem.Text = why;
            problem.IsVisible = true;
        };
        var cancel = PlainButton("Cancel");
        cancel.Click += (_, _) => dlg.Close(null);
        row.Children.Add(save);
        row.Children.Add(cancel);
        root.Children.Add(row);
        dlg.Content = new ScrollViewer { Content = root, MaxHeight = 760 };

        // --snap fills it the way a person would: a description, one label and its help.
        void Fill(string desc, string label, string help)
        {
            description.Text = desc;
            icon = BehaviorIcons.Length > 1 ? BehaviorIcons[1] : icon;
            Paint();
            if (labels.Count > 0) { labels[0].Label.Text = label; labels[0].Help.Text = help; }
        }
        return (dlg, Save, Fill);
    }

    async Task SaveAsBehaviorAsync(string mech)
    {
        if (SaveAsBehaviorWindow(mech) is not { } d) return;
        await d.Window.ShowDialog<string?>(this);
    }
}
