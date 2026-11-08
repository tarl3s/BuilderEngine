using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// SCENE FEATURE: the project (load/save), the scene view, play mode and undo. The inspector is in MainWindow.Inspector.cs.
public sealed partial class MainWindow
{
    Project _project = new();
    string _projectDir = "";
    /// <summary>Opened with --project: an explicit (maybe throwaway) folder that must not become the "last project".</summary>
    bool _projectFromArg;
    /// <summary>The starter kit as shipped, to tell which mechanisms belong to the project (new or edited ones).</summary>
    Dictionary<string, string> _starterSources = new();

    Scene _scene => _project.Scenes[Math.Clamp(_sceneIndex, 0, _project.Scenes.Count - 1)];

    SceneCanvas _canvas = null!;
    readonly ContentControl _sceneHost = new();
    readonly ContentControl _sceneBar = new();
    Control _editBar = null!, _playBar = null!;
    readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.TextDim, FontSize = 12, MinWidth = 44, TextAlignment = TextAlignment.Center };
    PlaySurface? _play;
    readonly TextBlock _playText = new();
    /// <summary>The scene the game is showing (it changes with goto_scene; the edited scene stays the same).</summary>
    string? _playSceneName;

    readonly List<string> _undo = new(), _redo = new();
    string? _checkpointKey;
    DateTime _checkpointTime;

    string? _dragName;
    LibItem? _dragItem;
    Point _dragStart;

    // ---------------- Project ----------------

    /// <summary>Before the UI is built: load the project (adds the project's mechanisms to _sources).</summary>
    void SceneInit()
    {
        _starterSources = new Dictionary<string, string>(_sources);
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--project");
        if (i >= 0 && i + 1 < args.Length)
        {
            _projectDir = Path.GetFullPath(args[i + 1]);
            _projectFromArg = true;
        }
        ProjectsInit();
        if (!_projectFromArg)
            _projectDir = _settings.Get("lastProject") is { } last && Project.Exists(last) ? last : Path.Combine(ProjectsRoot, "My Game");

        LoadProject();
        RememberProject();
        UpdateTitle();
        NumberWheel.Install(); // the wheel steps a focused number field (NumberWheel.cs)
        ListSearch.Install();  // type to search in open dropdowns and menus (ListSearch.cs)
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        SceneSnapIfAsked();
    }

    void LoadProject()
    {
        if (Project.Exists(_projectDir))
        {
            try { _project = Project.Load(_projectDir); }
            catch (Exception e)
            {
                // Keep the broken file for the person to look at, start again with the demo.
                string broken = Path.Combine(_projectDir, $"project.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try { File.Move(Path.Combine(_projectDir, Project.FileName), broken); } catch { }
                Log($"⚠ Couldn't read the project ({e.Message}). It was kept as {Path.GetFileName(broken)}; a new demo scene was made.");
                _project = Project.CreateDemo(ProjectNameFromFolder());
                AdoptProjectMechanisms();
                TrySave();
            }
        }
        else
        {
            _project = Project.CreateDemo(ProjectNameFromFolder());
            AdoptProjectMechanisms(); // before saving: saving keeps only the mechanisms it finds in _sources
            TrySave();
        }
        AdoptProjectMechanisms();
        // Projects made before sounds existed (or by hand) get the starter sounds once.
        try { if (!Directory.Exists(Sounds.Folder(_projectDir))) Sounds.WriteDefaults(_projectDir); }
        catch (Exception e) { Log($"⚠ Couldn't write the starter sounds: {e.Message}"); }
        ToolsOnProjectLoaded();   // MainWindow.Tools.cs
        PluginsOnProjectLoaded(); // MainWindow.Plugins.cs
    }

    /// <summary>The project's own mechanisms join the editor's sources (the demo brings its cutscene, Intro).</summary>
    void AdoptProjectMechanisms()
    {
        foreach (var (_, src) in _project.Mechanisms)
            foreach (var chunk in SplitMechanisms(src))
                _sources[chunk.Name] = chunk.Source;
    }

    string ProjectNameFromFolder() => Path.GetFileName(_projectDir.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "My Game";

    /// <summary>The project's own mechanisms: the ones that aren't in the starter kit, or that were edited.</summary>
    void SyncProjectMechanisms()
    {
        _project.Mechanisms.Clear();
        foreach (var (name, src) in _sources)
            if (!_starterSources.TryGetValue(name, out var kit) || Normalize(kit) != Normalize(src))
                _project.Mechanisms[name] = src;
    }

    static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    bool TrySave()
    {
        try
        {
            SyncProjectMechanisms();
            _project.Save(_projectDir);
            return true;
        }
        catch (Exception e)
        {
            Log($"⚠ Couldn't save the project: {e.Message}");
            SetStatus(false, $"Couldn't save: {e.Message}");
            return false;
        }
    }

    void SceneSave()
    {
        if (TrySave()) SetStatus(true, $"Saved “{_project.Name}” to {_projectDir}");
    }

    void SceneOnClosing()
    {
        SceneStop();
        TrySave();
    }

    // ---------------- Mechanism changes from the shell ----------------

    /// <summary>A mechanism was added or its source changed (refresh pickers / inspector params).</summary>
    void SceneOnMechanismsChanged()
    {
        RefreshLibraryIfSectionsChanged();
        RebuildInspector();
        RefreshToolsBar();
    }

    void SceneOnMechanismRenamed(string oldName, string newName)
    {
        _project.RenameMechanism(oldName, newName);
        VariantsOnRenamed(oldName, newName); // variants follow, and breakpoints (MainWindow.Variants.cs)
        // Undo snapshots follow the rename too, so undo never brings back a name that no longer exists.
        for (int i = 0; i < _undo.Count; i++) _undo[i] = RenameInSnapshot(_undo[i], oldName, newName);
        for (int i = 0; i < _redo.Count; i++) _redo[i] = RenameInSnapshot(_redo[i], oldName, newName);
        foreach (var h in _sceneHistory.Values)
        {
            for (int i = 0; i < h.Undo.Count; i++) h.Undo[i] = RenameInSnapshot(h.Undo[i], oldName, newName);
            for (int i = 0; i < h.Redo.Count; i++) h.Redo[i] = RenameInSnapshot(h.Redo[i], oldName, newName);
        }
        _canvas.InvalidateVisual();
        RebuildInspector();
    }

    static string RenameInSnapshot(string json, string oldName, string newName)
    {
        var p = new Project { Scenes = [Scene.FromJson(json)] };
        return p.RenameMechanism(oldName, newName) > 0 ? p.Scenes[0].ToJson() : json;
    }

    // ---------------- Scene view ----------------

    Control CreateSceneView()
    {
        _canvas = new SceneCanvas { Scene = _scene };
        _canvas.Images = Pictures;
        _canvas.PictureOf = ScenePictureOf;
        _canvas.SelectionChanged = RebuildInspector;
        _canvas.BeforeEdit = () => Checkpoint();
        _canvas.Edited = SyncTransformFields;
        _canvas.ViewChanged = () => _zoomText.Text = $"{Math.Round(_canvas.Zoom * 100)}%";
        _canvas.MechanismDropped = OnMechanismDropped;
        _canvas.PictureAspect = PictureAspectOf;
        _canvas.DoubleClicked = o => BeginRename(o.Id);
        _canvas.MenuRequested = ShowSceneMenu;
        _canvas.PointerWorldChanged = ShowCursorPos;
        _canvas.ToolPointer = OnToolPointer;

        _editBar = EditBar();
        _playBar = PlayBar();
        _sceneBar.Content = _editBar;
        _sceneHost.Content = _canvas;

        HookLibraryDrag();

        var root = new DockPanel();
        var bars = new StackPanel();
        bars.Children.Add(_sceneBar);
        bars.Children.Add(ToolsBar()); // the TOOLS row (MainWindow.Tools.cs)
        var bar = new Border { Background = Palette.Bg1, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = bars };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_sceneHost);
        return root;
    }

    Control EditBar()
    {
        var add = PlainButton("+ Object");
        add.Click += (_, _) => AddEmptyObject();
        var grid = SmallButton("Grid", "Show or hide the grid (objects still snap to it; hold Alt to place freely)");
        StyleTab(grid, true);
        grid.Padding = new Thickness(10, 5);
        grid.Click += (_, _) =>
        {
            _canvas.ShowGrid = !_canvas.ShowGrid;
            StyleTab(grid, _canvas.ShowGrid);
            grid.Padding = new Thickness(10, 5);
            _canvas.InvalidateVisual();
        };
        var minus = SmallButton("−", "Zoom out (Ctrl+wheel zooms at the mouse, Ctrl+-)");
        minus.Click += (_, _) => _canvas.ZoomBy(1 / 1.25);
        var plus = SmallButton("+", "Zoom in (Ctrl+wheel zooms at the mouse, Ctrl++)");
        plus.Click += (_, _) => _canvas.ZoomBy(1.25);
        var fit = SmallButton("Fit", "Show the whole scene (Home)");
        fit.Click += (_, _) => _canvas.Fit();

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(ScenePicker());
        left.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Border, Margin = new Thickness(4, 0) });
        left.Children.Add(add);
        left.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Border, Margin = new Thickness(4, 0) });
        left.Children.Add(grid);
        left.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Border, Margin = new Thickness(4, 0) });
        left.Children.Add(minus);
        left.Children.Add(_zoomText);
        left.Children.Add(plus);
        left.Children.Add(fit);

        var hint = new TextBlock
        {
            Text = "Drag to move · Drag on empty space to select several · Alt = no snap · Ctrl+wheel to zoom · Right-click for more · F1 = all shortcuts",
            Foreground = Palette.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right
        };
        var dock = new DockPanel { Margin = new Thickness(12, 6) };
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);
        DockPanel.SetDock(_cursorPos, Dock.Right);
        dock.Children.Add(_cursorPos); // X / Y of the mouse in the scene (MainWindow.SceneKeys.cs)
        dock.Children.Add(hint);
        return dock;
    }

    Control PlayBar()
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = Palette.Ok, VerticalAlignment = VerticalAlignment.Center };
        var text = _playText;
        text.Foreground = Palette.Text;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.FontSize = 12;
        var stop = PlainButton("■  Stop");
        ToolTip.SetTip(stop, "Back to editing (Esc)");
        stop.Click += (_, _) => SceneStop();
        var row = new DockPanel { Margin = new Thickness(12, 6) };
        DockPanel.SetDock(stop, Dock.Right);
        row.Children.Add(stop);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(dot);
        left.Children.Add(text);
        row.Children.Add(left);
        return row;
    }

    static Button SmallButton(string text, string tip)
    {
        var b = PlainButton(text);
        b.Padding = new Thickness(10, 5);
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>Dragging a mechanism out of the library: a click still opens it, a drag carries it to the scene.</summary>
    void HookLibraryDrag()
    {
        _library.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(_library).Properties.IsLeftButtonPressed) return;
            var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if ((item?.DataContext ?? item?.Content) is not LibItem { IsHeader: false } lib || lib.Name.Length == 0) return;
            _dragItem = lib;
            _dragName = lib.Section == LibSection.Mine ? LibDragTag + lib.Name : lib.Name;
            _dragStart = e.GetPosition(_library);
            e.Pointer.Capture(_library);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        _library.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (_dragName == null) return;
            var p = e.GetPosition(_library);
            if (Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) < 6) return;
            string name = _dragName;
            _dragName = null;
            e.Pointer.Capture(null);
            if (_play != null) return;
            ShowTab(scene: true);
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(SceneCanvas.DragPrefix + name));
            SetStatus(true, $"Drop “{(name.StartsWith(LibDragTag) ? name[LibDragTag.Length..] : name)}” on the scene to make an object, or on an object to add it.");
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _library.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (_dragName == null) return;
            _dragName = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            if (_dragItem != null) OnLibraryItemClicked(_dragItem);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>A My Library mechanism dropped on the scene: copy it into the project first (it may ask about a name clash).</summary>
    async Task DropFromLibraryAsync(string libName, Point at, SceneObject? target)
    {
        if (_play != null) return;
        int targetId = target?.Id ?? 0;
        if (await UseFromLibraryAsync(libName, open: false) is not { } name) return;
        OnMechanismDropped(name, at, targetId == 0 ? null : _scene.Find(targetId));
    }

    void OnMechanismDropped(string name, Point at, SceneObject? target)
    {
        if (name.StartsWith(LibDragTag)) { _ = DropFromLibraryAsync(name[LibDragTag.Length..], at, target); return; }
        if (!_sources.ContainsKey(name) || _play != null) return;
        if (target != null)
        {
            if (!target.Has(name))
            {
                Checkpoint();
                target.Mechanisms.Add(new SceneMechanism { Name = name });
                SetStatus(true, $"Added {name} to {target.Name}.");
            }
            Select(target.Id);
        }
        else
        {
            Checkpoint();
            var o = _scene.Add(new SceneObject
            {
                Name = UniqueName(name),
                X = Math.Round((at.X - 16) / SceneCanvas.GridStep) * SceneCanvas.GridStep,
                Y = Math.Round((at.Y - 16) / SceneCanvas.GridStep) * SceneCanvas.GridStep,
                Color = ColorFor(name),
                Mechanisms = [new SceneMechanism { Name = name }]
            });
            Select(o.Id);
            SetStatus(true, $"New object {o.Name} with {name}.");
        }
        _canvas.Focus();
    }

    // ---------------- Scene edits ----------------

    void Select(int id)
    {
        _canvas.SelectedId = id;
        _canvas.InvalidateVisual();
        RebuildInspector();
    }

    SceneObject? Selected => _scene.Find(_canvas.SelectedId);

    void AddEmptyObject()
    {
        if (_play != null) return;
        Checkpoint();
        var c = _canvas.ViewCenter;
        double x = Math.Clamp(c.X - 16, 0, Math.Max(0, _scene.Width - 32)), y = Math.Clamp(c.Y - 16, 0, Math.Max(0, _scene.Height - 32));
        var o = _scene.Add(new SceneObject
        {
            Name = UniqueName("Object"),
            X = Math.Round(x / SceneCanvas.GridStep) * SceneCanvas.GridStep,
            Y = Math.Round(y / SceneCanvas.GridStep) * SceneCanvas.GridStep,
            Color = Swatches[_scene.Objects.Count % Swatches.Length]
        });
        Select(o.Id);
        _canvas.Focus();
    }

    // ---------------- Undo ----------------

    /// <summary>Remembers the scene before a change. Changes with the same key in a row (typing, spinning) are one step.</summary>
    void Checkpoint(string? key = null)
    {
        var now = DateTime.UtcNow;
        if (key != null && key == _checkpointKey && (now - _checkpointTime).TotalSeconds < 1.5) { _checkpointTime = now; return; }
        _checkpointKey = key;
        _checkpointTime = now;
        string json = _scene.ToJson();
        // Nothing changed since the last step (a tool option, a field set to the same value): don't add an empty step.
        if (_undo.Count > 0 && _undo[^1] == json) return;
        _undo.Add(json);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Adds an undo step that was taken earlier: a tool use remembers the scene before it and adds it at the end.</summary>
    void PushUndo(string before)
    {
        _checkpointKey = null;
        _undo.Add(before);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
    }

    void Undo()
    {
        string now = _scene.ToJson();
        while (_undo.Count > 0 && _undo[^1] == now) _undo.RemoveAt(_undo.Count - 1); // steps that changed nothing
        if (_undo.Count == 0) { SetStatus(true, "Nothing to undo."); return; }
        _redo.Add(now);
        RestoreScene(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        SetStatus(true, "Undone.");
    }

    void Redo()
    {
        if (_redo.Count == 0) { SetStatus(true, "Nothing to redo."); return; }
        _undo.Add(_scene.ToJson());
        RestoreScene(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        SetStatus(true, "Redone.");
    }

    void RestoreScene(string json)
    {
        _checkpointKey = null;
        _project.Scenes[_sceneIndex] = Scene.FromJson(json);
        _canvas.Scene = _scene;
        KeepSelectionAfterRestore(); // the objects of the selection that still exist (MainWindow.SceneKeys.cs)
        _canvas.InvalidateVisual();
        RebuildInspector();
        RefreshScenes();
    }

    // ---------------- Play ----------------

    void ScenePlay()
    {
        if (_play != null) SceneStop();
        _canvas.CancelToolDrag();
        var world = NewWorld();
        UseProjectFiles(world);
        world.Globals["score"] = 0.0;
        world.UseScenes(_project.Scenes.Select(s => s.Name));
        world.SoundPlayed += PlayGameSound;
        world.SoundControl += OnGameSound; // loops, stop sound, volume (MainWindow.GameAudio.cs)
        world.SceneRequested += name => Dispatcher.UIThread.Post(() => PlaySwitchScene(name));
        StartPlaySurface(world, _scene.Clone()); // the game plays a copy: the edited scene stays exactly as it was
        RebuildInspector();
        SetStatus(true, "Playing — arrow keys move the player. Esc or Stop to edit again.");
    }

    /// <summary>Shows a world playing a scene (a new surface each scene, so everything a scene sets up starts fresh).</summary>
    void StartPlaySurface(World world, Scene scene)
    {
        DebugAttach(world); // before loading, so scene errors reach the timeline (MainWindow.Debug.cs)
        var spawned = world.LoadScene(scene);
        Log($"▶ Playing {scene.Name} ({spawned.Count} objects).");
        _play?.Halt();
        _play = new PlaySurface(world, scene, spawned, Pictures)
        {
            StopRequested = SceneStop,
            Crashed = msg => { Log($"⚠ The game stopped: {msg}"); SceneStop(); }
        };
        DebugSurface(_play);
        _playSceneName = scene.Name;
        _playText.Text = $"Playing {scene.Name} — Esc or Stop to go back to editing (your scenes stay as they were).";
        _sceneHost.Content = _play;
        _sceneBar.Content = _playBar;
        ShowToolsBar(false);
        var surface = _play;
        Dispatcher.UIThread.Post(() => { if (_play == surface) surface.Start(); }, DispatcherPriority.Loaded);
    }

    /// <summary>A script asked for another scene (goto_scene): same mechanisms and globals (score), the new scene's objects.</summary>
    void PlaySwitchScene(string name)
    {
        if (_play == null) return;
        if (_project.FindScene(name) is not { } target) { Log($"⚠ There's no scene called “{name}” any more."); return; }
        StartPlaySurface(_play.World.ForNextScene(), target.Clone());
    }

    void SceneStop()
    {
        if (_play == null) return;
        _play.Halt();
        _play = null;
        DebugStop();
        _playSceneName = null;
        _audio.StopAll();
        _sceneHost.Content = _canvas;
        _sceneBar.Content = _editBar;
        ShowToolsBar(true);
        RebuildInspector();
        Log("■ Stopped.");
        SetStatus(true, "Stopped. Back to editing.");
        Dispatcher.UIThread.Post(() => _canvas.Focus(), DispatcherPriority.Loaded);
    }

    // ---------------- Colours ----------------

    static readonly string[] Swatches =
    [
        "#5B8DEF", "#4FC3F7", "#6FBF73", "#A3D977", "#E8B04B", "#F2994A",
        "#D97757", "#E05D5D", "#E07AB5", "#9B7BE0", "#6B5B4B", "#ECECEC"
    ];

    static readonly string[] BackgroundSwatches =
    [
        "#1A1410", "#0B0B0B", "#1F1F1F", "#0F1A24", "#14201A", "#201427", "#2B2118", "#1B2A38", "#87B5E0", "#CFE3F0"
    ];

    static string ColorFor(string mechanism) => mechanism.ToLowerInvariant() switch
    {
        "coin" => "#E8B04B",
        "playermove" => "#5B8DEF",
        "patrol" => "#6FBF73",
        "health" => "#E05D5D",
        "blink" => "#9B7BE0",
        _ => Swatches[(int)((uint)StableHash(mechanism) % Swatches.Length)]
    };

    static int StableHash(string s)
    {
        int h = 17;
        foreach (var c in s) h = h * 31 + char.ToLowerInvariant(c);
        return h;
    }
}
