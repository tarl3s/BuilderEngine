using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// SCENES FEATURE: the scene picker in the scene toolbar (open, add, duplicate, rename, move, delete).
// The first scene is where the game starts; Play plays the open one; scripts switch with goto_scene "Name".
// Undo is per scene (each scene keeps its own history while another one is open).
public sealed partial class MainWindow
{
    int _sceneIndex;
    readonly ComboBox _sceneCombo = new();
    bool _sceneComboSync;
    readonly Dictionary<int, (List<string> Undo, List<string> Redo)> _sceneHistory = new();

    Control ScenePicker()
    {
        _sceneCombo.MinWidth = 150;
        _sceneCombo.MaxWidth = 240;
        _sceneCombo.Background = Palette.Bg2;
        _sceneCombo.Foreground = Palette.Text;
        _sceneCombo.BorderBrush = Palette.Border;
        _sceneCombo.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_sceneCombo, "The scene you are editing. The first scene is where the game starts.");
        _sceneCombo.SelectionChanged += (_, _) =>
        {
            if (!_sceneComboSync && _sceneCombo.SelectedIndex >= 0 && _sceneCombo.SelectedIndex != _sceneIndex) OpenScene(_sceneCombo.SelectedIndex);
        };

        var add = SmallButton("+ Scene", "Add an empty scene");
        add.Click += (_, _) => AddSceneCommand();

        var more = SmallButton("⋯", "Rename, duplicate, move or delete this scene");
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            MenuItem Item(string text, Action run, bool enabled = true)
            {
                var m = new MenuItem { Header = text, IsEnabled = enabled };
                m.Click += (_, _) => run();
                return m;
            }
            int n = _project.Scenes.Count;
            flyout.Items.Add(Item("Rename…", () => _ = RenameSceneAsync()));
            flyout.Items.Add(Item("Duplicate", DuplicateSceneCommand));
            flyout.Items.Add(Item("Move earlier", () => MoveSceneCommand(-1), _sceneIndex > 0));
            flyout.Items.Add(Item("Move later", () => MoveSceneCommand(+1), _sceneIndex < n - 1));
            flyout.Items.Add(new Separator());
            flyout.Items.Add(Item("Delete…", () => _ = DeleteSceneAsync(), n > 1));
        };
        more.Flyout = flyout;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_sceneCombo);
        row.Children.Add(add);
        row.Children.Add(more);
        RefreshScenes();
        return row;
    }

    /// <summary>The picker shows the scenes again (after a rename, add, move...).</summary>
    void RefreshScenes()
    {
        _sceneComboSync = true;
        _sceneIndex = Math.Clamp(_sceneIndex, 0, _project.Scenes.Count - 1);
        _sceneCombo.ItemsSource = _project.Scenes.Select((s, i) => i == 0 ? $"{s.Name}   (start)" : s.Name).ToList();
        _sceneCombo.SelectedIndex = _sceneIndex;
        _sceneComboSync = false;
    }

    /// <summary>Edits another scene (its own undo history comes back with it).</summary>
    void OpenScene(int index)
    {
        if (_play != null || index < 0 || index >= _project.Scenes.Count) { RefreshScenes(); return; }
        StashHistory();
        _sceneIndex = index;
        RestoreHistory();
        _canvas.Scene = _scene;
        _canvas.SelectedId = 0;
        _canvas.InvalidateVisual();
        Dispatcher.UIThread.Post(() => _canvas.Fit(), DispatcherPriority.Loaded);
        RefreshScenes();
        RebuildInspector();
        SetStatus(true, $"Editing {_scene.Name}.");
    }

    void StashHistory() => _sceneHistory[_scene.Id] = (_undo.ToList(), _redo.ToList());

    void RestoreHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _checkpointKey = null;
        if (_sceneHistory.Remove(_scene.Id, out var h)) { _undo.AddRange(h.Undo); _redo.AddRange(h.Redo); }
    }

    void AddSceneCommand()
    {
        if (_play != null) return;
        var s = _project.AddScene(null, _scene);
        OpenScene(_project.Scenes.IndexOf(s));
        SetStatus(true, $"New scene {s.Name}. Drag mechanisms onto it; a Door (or goto_scene \"{s.Name}\") leads here.");
    }

    void DuplicateSceneCommand()
    {
        if (_play != null) return;
        var s = _project.DuplicateScene(_sceneIndex);
        OpenScene(_project.Scenes.IndexOf(s));
        SetStatus(true, $"Made {s.Name}.");
    }

    void MoveSceneCommand(int delta)
    {
        if (_play != null || !_project.MoveScene(_sceneIndex, _sceneIndex + delta)) return;
        _sceneIndex += delta;
        RefreshScenes();
        SetStatus(true, _sceneIndex == 0 ? $"{_scene.Name} is now the first scene: the game starts here." : $"Moved {_scene.Name}.");
    }

    async Task RenameSceneAsync()
    {
        if (_play != null) return;
        string? name = await Dialogs.Input(this, "Rename scene", "Scripts and doors find scenes by this name (goto_scene \"Name\").", _scene.Name, "Rename");
        if (name == null) return;
        RenameSceneTo(name);
    }

    /// <summary>Renames the open scene. Doors and goto_scene lines that used the old name follow it.</summary>
    bool RenameSceneTo(string name)
    {
        string old = _scene.Name;
        if (_project.RenameScene(_sceneIndex, name) is { } why) { SetStatus(false, why); return false; }
        int doors = RetargetDoors(old, _scene.Name);
        RefreshScenes();
        RebuildInspector();
        SetStatus(true, doors > 0 ? $"Renamed to {_scene.Name}; {doors} door(s) now lead here." : $"Renamed to {_scene.Name}.");
        return true;
    }

    /// <summary>Door-like params (a text param holding the old scene name) follow a renamed scene.</summary>
    int RetargetDoors(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return 0;
        // Only when the name moved cleanly: no other scene still has the old name, and only one has the new one.
        int Named(string x) => _project.Scenes.Count(s => string.Equals(s.Name.Trim(), x.Trim(), StringComparison.OrdinalIgnoreCase));
        if (Named(oldName) > 0 || Named(newName) != 1) return 0;
        int n = 0;
        foreach (var s in _project.Scenes)
            foreach (var o in s.Objects)
                foreach (var m in o.Mechanisms)
                    foreach (var k in m.Params.Keys.ToList())
                        if (string.Equals(k, "scene", StringComparison.OrdinalIgnoreCase) && m.Params[k] is string v
                            && string.Equals(v.Trim(), oldName.Trim(), StringComparison.OrdinalIgnoreCase)) { m.Params[k] = newName; n++; }
        return n;
    }

    async Task DeleteSceneAsync()
    {
        if (_play != null || _project.Scenes.Count <= 1) return;
        var r = _snapMode ? "Delete" : await Dialogs.Ask(this, "Delete scene",
            $"Delete “{_scene.Name}” and its {_scene.Objects.Count} object(s)? This can't be undone.", "Cancel", "Delete");
        if (r != "Delete") return;
        DeleteOpenScene();
    }

    void DeleteOpenScene()
    {
        string name = _scene.Name;
        _sceneHistory.Remove(_scene.Id);
        if (!_project.RemoveScene(_sceneIndex)) return;
        _sceneIndex = Math.Min(_sceneIndex, _project.Scenes.Count - 1);
        RestoreHistory();
        _canvas.Scene = _scene;
        _canvas.SelectedId = 0;
        _canvas.InvalidateVisual();
        RefreshScenes();
        RebuildInspector();
        SetStatus(true, $"Deleted the scene {name}.");
    }

    /// <summary>A note for the scene settings: where the game starts and how scenes connect.</summary>
    Control SceneFlowNote()
    {
        string text = _sceneIndex == 0
            ? "This is the first scene: the game starts here."
            : $"Scene {_sceneIndex + 1} of {_project.Scenes.Count}.";
        return new TextBlock
        {
            Text = text + " Play plays the scene you're editing. To change scene in the game, use a Door or write goto_scene \"Name\".",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12, Margin = new Thickness(0, 4, 0, 0)
        };
    }
}
