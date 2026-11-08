using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// LINK FEATURE (docs/EASYENGINE-LINK.md): EasyEngine projects (.eeproj) open and save here through EasyLink; objects
// with EasyEngine behaviors show a read-only "Built on EasyEngine" card; the open project's file is watched (LiveLink)
// so EasyEngine's — or another BuilderEngine window's — saves show up, and with "Live link" on every edit is saved
// ~500 ms after it. Reloads keep the open scene, the selection, the zoom and the open mechanism, and wait while playing.
public sealed partial class MainWindow
{
    /// <summary>The open project's .eeproj (null = a BuilderEngine folder with project.json).</summary>
    string? _eeproj;
    /// <summary>The .eeproj couldn't be read: it is never saved over (the person fixes or reopens it).</summary>
    bool _eeprojBroken;
    LiveLink? _link;
    /// <summary>Live link on: each edit is saved ~500 ms after it (on by default for EasyEngine projects).</summary>
    bool _liveLink;
    /// <summary>The file changed outside: reload when not playing.</summary>
    bool _linkPending;
    bool _linkDirty, _linkCanvasHooked, _linkClosedHooked;
    DateTime _linkTouched, _linkLastPoll;
    /// <summary>The editor's project as it was last loaded or saved (tells unsaved changes apart).</summary>
    string? _linkSavedHash;
    DispatcherTimer? _linkTimer;
    /// <summary>Objects with EasyEngine behaviors the person agreed to give mechanisms too (asked once per object).</summary>
    readonly HashSet<int> _linkAskedIds = [];

    const int LinkSaveDelayMs = 500;

    string LinkTitle => (_eeproj != null ? "  (EasyEngine)" : "") + (_liveLink ? "  🔗 Live link" : "");

    /// <summary>The project to open: a folder (project.json, else its .eeproj) or an .eeproj file.</summary>
    void SetProjectPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full) && full.EndsWith(EasyLink.Extension, StringComparison.OrdinalIgnoreCase))
        {
            _projectDir = Path.GetDirectoryName(full)!;
            _eeproj = full;
        }
        else
        {
            _projectDir = full;
            _eeproj = Project.Exists(full) ? null : EasyLink.FindEeproj(full);
        }
    }

    // ---------------- Load / save ----------------

    /// <summary>Reads the .eeproj (EasyEngine may be writing it: a few tries). A file that can't be read is never saved over.</summary>
    void LoadEeproj()
    {
        _eeprojBroken = false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _project = EasyLink.FromEeproj(LiveLink.ReadShared(_eeproj!), _projectDir);
                return;
            }
            catch (Exception e) when ((e is IOException or JsonException) && attempt < 4)
            {
                Thread.Sleep(150);
            }
            catch (Exception e)
            {
                _eeprojBroken = true;
                _project = Project.CreateEmpty(Path.GetFileNameWithoutExtension(_eeproj) ?? "EasyEngine project");
                Log($"⚠ Couldn't read the EasyEngine project ({e.Message}). It won't be saved over: fix it in EasyEngine and open it again.");
                return;
            }
        }
    }

    /// <summary>Saves the open project: project.json + mechanisms, or the .eeproj (merged into the file on disk, atomically).</summary>
    void SaveProjectFiles()
    {
        if (_eeproj == null)
        {
            _link?.BeginWrite();
            try { _project.Save(_projectDir); }
            finally { _link?.EndWrite(); }
        }
        else
        {
            if (_eeprojBroken) throw new InvalidOperationException("the EasyEngine project couldn't be read, so it isn't saved over");
            FixIdsKeepingSelection();
            string disk = File.Exists(_eeproj) ? LiveLink.ReadShared(_eeproj) : "";
            string text = EasyLink.ToEeproj(_project, disk, _projectDir);
            if (text != disk)
            {
                if (_link != null) _link.Write(text);
                else EasyLink.WriteAtomic(_eeproj, text);
            }
            else _link?.Remember(disk);
        }
        _linkSavedHash = EditorStateHash();
    }

    /// <summary>EasyEngine needs ids no other object of the project has: new objects get max + 1 (the selection follows).</summary>
    void FixIdsKeepingSelection()
    {
        var changed = EasyLink.FixIds(_project);
        if (changed.Count == 0 || _canvas == null) return;
        var sel = _canvas.SelectedIds.ToList();
        int primary = _canvas.SelectedId;
        foreach (var (obj, oldId) in changed)
        {
            if (!_scene.Objects.Contains(obj)) continue;
            int i = sel.IndexOf(oldId);
            if (i >= 0) sel[i] = obj.Id;
            if (primary == oldId) primary = obj.Id;
        }
        _canvas.SetSelection(sel, primary);
    }

    /// <summary>A hash of what the editor would save (the project and its own mechanisms).</summary>
    string EditorStateHash()
    {
        SyncProjectMechanisms();
        var sb = new System.Text.StringBuilder(_project.ToJson());
        foreach (var (name, src) in _project.Mechanisms.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append('\0').Append(name).Append('\0').Append(src);
        return LiveLink.Hash(sb.ToString());
    }

    // ---------------- Watching and the live link ----------------

    /// <summary>After a project loads: watch its file; live link starts on for EasyEngine projects.</summary>
    void LinkOnProjectLoaded()
    {
        _link?.Dispose();
        _link = null;
        _linkPending = false;
        _linkDirty = false;
        _linkAskedIds.Clear();
        _liveLink = _eeproj != null && !_eeprojBroken;
        if (EasyLink.ProjectFile(_eeproj != null ? null : _projectDir) is var beFile && (_eeproj ?? beFile) is { } file && File.Exists(file))
        {
            try
            {
                _link = new LiveLink(file);
                _link.Changed += () => Dispatcher.UIThread.Post(() => _linkPending = true);
                _link.Start();
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _link?.Dispose();
                _link = null;
                Log($"⚠ Can't watch the project file for changes: {e.Message}");
            }
        }
        _linkSavedHash = EditorStateHash();
        if (_linkTimer == null)
        {
            _linkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _linkTimer.Tick += (_, _) => LinkTick();
            _linkTimer.Start();
        }
        if (!_linkClosedHooked)
        {
            _linkClosedHooked = true;
            Closed += (_, _) => { _linkTimer?.Stop(); _link?.Dispose(); };
        }
    }

    /// <summary>Something was edited: with live link on it is saved <see cref="LinkSaveDelayMs"/> after the last edit.</summary>
    void LinkTouched()
    {
        _linkDirty = true;
        _linkTouched = DateTime.UtcNow;
    }

    void LinkTick()
    {
        if (!_linkCanvasHooked && _canvas != null)
        {
            // Dragging edits the scene continuously after one checkpoint: each move counts as an edit.
            _linkCanvasHooked = true;
            var edited = _canvas.Edited;
            _canvas.Edited = () => { edited?.Invoke(); LinkTouched(); };
        }
        if (_link == null) return;
        if (_linkPending && _play == null) { _ = LinkApplyOutsideChangeAsync(); return; }
        if (!_liveLink || _play != null || _linkAsking) return;
        var now = DateTime.UtcNow;
        bool due = _linkDirty && (now - _linkTouched).TotalMilliseconds >= LinkSaveDelayMs;
        bool poll = (now - _linkLastPoll).TotalSeconds >= 2; // edits that didn't go through a checkpoint
        if (!due && !poll) return;
        _linkLastPoll = now;
        if (due) _linkDirty = false;
        if (EditorStateHash() == _linkSavedHash) return;
        if (_link.ChangedOnDisk()) { _linkPending = true; return; } // the other side saved meanwhile: theirs first
        TrySave();
    }

    bool _linkAsking;

    /// <summary>The file changed outside: reload, keeping the editor's state. Unsaved changes with live link off: ask first.</summary>
    async Task LinkApplyOutsideChangeAsync()
    {
        _linkPending = false;
        if (_linkAsking) return;
        if (!_liveLink && !_snapMode && EditorStateHash() != _linkSavedHash)
        {
            _linkAsking = true;
            try
            {
                var r = await Dialogs.Ask(this, "The project changed on disk",
                    $"“{_project.Name}” was saved by {(_eeproj != null ? "EasyEngine (or another program)" : "another window or program")}, and you have changes here that aren't saved. Load the saved version (your changes here are lost) or keep yours (saving writes them over it)?",
                    "Load the saved one", "Keep mine");
                if (r != "Load the saved one") { SetStatus(true, "Kept your version: saving writes it over the one on disk."); return; }
            }
            finally { _linkAsking = false; }
        }
        LinkReload();
    }

    /// <summary>Loads the project again from disk, keeping the open scene, selection, zoom/pan, tab and open mechanism.</summary>
    void LinkReload()
    {
        Project fresh;
        try
        {
            fresh = _eeproj != null ? EasyLink.FromEeproj(LiveLink.ReadShared(_eeproj), _projectDir) : Project.Load(_projectDir);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _link?.Forget(); // half written: look again in a moment
            return;
        }
        if (_eeproj != null) _eeprojBroken = false;

        int sceneId = _scene.Id, sceneIndex = _sceneIndex;
        string sceneName = _scene.Name;
        var selected = _canvas?.SelectedIds.ToList() ?? [];
        int primary = _canvas?.SelectedId ?? 0;
        string? current = _current;
        bool onSceneTab = _sceneTab;

        _checkpointKey = null;
        if (_canvas != null) Checkpoint(); // Ctrl+Z brings back the scene as it was before the outside change

        _project = fresh;
        foreach (var name in _sources.Keys.ToList())
            if (!_starterSources.ContainsKey(name)) _sources.Remove(name);
        foreach (var (name, src) in _starterSources) _sources[name] = src;
        AdoptProjectMechanisms();

        int idx = _project.Scenes.FindIndex(s => s.Name == sceneName);
        if (idx < 0) idx = _project.Scenes.FindIndex(s => s.Id == sceneId);
        _sceneIndex = idx >= 0 ? idx : Math.Clamp(sceneIndex, 0, _project.Scenes.Count - 1);

        if (_canvas != null)
        {
            _canvas.Scene = _scene; // the zoom and pan stay as they were
            _canvas.SetSelection(selected.Where(id => _scene.Find(id) != null), _scene.Find(primary) != null ? primary : 0);
        }
        if (current != null && !_sources.ContainsKey(current))
        {
            _current = null;
            if (!onSceneTab) ShowTab(scene: true);
        }
        else if (current != null && (_code.Text ?? "") != _sources[current])
        {
            _loading = true;
            _code.SetTextKeepView(_sources[current]); // only what changed on disk: the caret and the scroll stay
            _code.RefreshColors(_sources.Keys);
            _loading = false;
            BlocksShow(_sources[current]);
        }
        RefreshLibrary();
        RefreshScenes();
        RefreshSounds();
        RefreshToolsBar();
        RebuildInspector();
        UpdateTitle();
        _canvas?.InvalidateVisual();
        _linkSavedHash = EditorStateHash();
        _linkDirty = false;
        string who = _eeproj != null ? "EasyEngine" : "another window";
        Log($"🔗 Updated from {who} ({DateTime.Now:HH:mm:ss}).");
        SetStatus(true, $"🔗 Updated from {who}. Ctrl+Z undoes it in this scene.");
    }

    void ToggleLiveLink()
    {
        _liveLink = !_liveLink;
        UpdateTitle();
        if (_liveLink)
        {
            _linkLastPoll = default;
            LinkTouched();
            SetStatus(true, "🔗 Live link on: every change is saved right away, and changes saved elsewhere show up here.");
        }
        else SetStatus(true, "Live link off: save with Ctrl+S. Changes saved elsewhere still show up (you're asked when you have unsaved ones).");
    }

    // ---------------- Menu ----------------

    void LinkMenuItems(MenuFlyout f)
    {
        var open = new MenuItem { Header = "Open EasyEngine project…" };
        ToolTip.SetTip(open, "Open a .eeproj: both engines can edit it, even at the same time");
        open.Click += (_, _) => _ = OpenEasyEngineProjectAsync();
        f.Items.Add(open);
        var live = new MenuItem
        {
            Header = "🔗 Live link", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _liveLink,
        };
        ToolTip.SetTip(live, "Save every change right away, so EasyEngine (or another BuilderEngine window) sees it live");
        live.Click += (_, _) => ToggleLiveLink();
        f.Items.Add(live);
        if (_eeproj != null && FindEasyEngine() != null)
        {
            var ee = new MenuItem { Header = "Open in EasyEngine" };
            ee.Click += (_, _) => OpenInEasyEngine();
            f.Items.Add(ee);
        }
    }

    async Task OpenEasyEngineProjectAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open an EasyEngine project", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("EasyEngine project (.eeproj)") { Patterns = ["*.eeproj"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (_eeproj != null && string.Equals(Path.GetFullPath(path), _eeproj, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(true, $"“{_project.Name}” is already open.");
            return;
        }
        SceneStop();
        TrySave();
        OpenProjectFolder(path);
    }

    // ---------------- Opening EasyEngine ----------------

    /// <summary>
    /// EasyEngine on this computer: the EASYENGINE_EXE variable, its Start menu shortcut (EasyEngine makes one), or the
    /// usual install folder. Null when it can't be found.
    /// </summary>
    static string? FindEasyEngine()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("EASYENGINE_EXE") is { Length: > 0 } env && File.Exists(env)) return env;
            if (!OperatingSystem.IsWindows()) return null;
            string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "EasyEngine.lnk");
            if (File.Exists(link)) return link;
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "EasyEngine", "EasyEngine.exe");
            return File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }

    /// <summary>Saves, then starts EasyEngine with the project's .eeproj (EasyEngine opens a .eeproj given as its first argument).</summary>
    void OpenInEasyEngine()
    {
        if (_eeproj == null || FindEasyEngine() is not { } app) return;
        TrySave();
        try
        {
            Process.Start(new ProcessStartInfo { FileName = app, Arguments = $"\"{_eeproj}\"", UseShellExecute = true });
            SetStatus(true, "Opening EasyEngine… With live link on, changes in either engine show up in the other.");
        }
        catch (Exception e) { SetStatus(false, $"Couldn't start EasyEngine: {e.Message}"); }
    }

    // ---------------- The inspector ----------------

    /// <summary>The read-only "Built on EasyEngine" card of an object with EasyEngine behaviors.</summary>
    void EasyEngineCard(SceneObject o)
    {
        if (!EasyLink.HasEasyBehaviors(o)) return;
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = "Built on EasyEngine", FontWeight = FontWeight.SemiBold, Foreground = Palette.Accent });
        var names = EasyLink.EasyBehaviors(o).Select(Words).Distinct().ToList();
        string list = names.Count <= 6 ? string.Join(", ", names) : string.Join(", ", names.Take(6)) + $" and {names.Count - 6} more";
        body.Children.Add(new TextBlock { Text = "Behaviors: " + list, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text, FontSize = 12 });
        body.Children.Add(new TextBlock
        {
            Text = o.Mechanisms.Count == 0
                ? "BuilderEngine shows this object and keeps its behaviors exactly as they are, but doesn't run them: edit and play them in EasyEngine."
                : "It also has BuilderEngine mechanisms: here only the mechanisms run; EasyEngine runs its behaviors and the mechanisms.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextDim, FontSize = 12
        });
        if (_eeproj != null && FindEasyEngine() != null)
        {
            var b = LinkButton("Open in EasyEngine", "Saves, then opens this project in EasyEngine");
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(0, 2);
            b.Click += (_, _) => OpenInEasyEngine();
            body.Children.Add(b);
        }
        _inspectorRoot.Children.Add(new Border
        {
            Child = body, Background = Palette.Bg2, BorderBrush = Palette.AccentBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 6, 0, 2)
        });
    }

    /// <summary>"FollowCamera" → "Follow Camera".</summary>
    static string Words(string kind)
    {
        var sb = new System.Text.StringBuilder(kind.Length + 4);
        for (int i = 0; i < kind.Length; i++)
        {
            if (i > 0 && char.IsUpper(kind[i]) && !char.IsUpper(kind[i - 1])) sb.Append(' ');
            sb.Append(kind[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Adding a mechanism to an object with EasyEngine behaviors: asks once per object (it will then have both). True when
    /// it took over (the mechanism is added after the person agrees); false to go on adding it now.
    /// </summary>
    bool EasyAsksFirst(SceneObject o, string mechanism)
    {
        if (!EasyLink.HasEasyBehaviors(o) || o.Mechanisms.Count > 0 || _linkAskedIds.Contains(o.Id)) return false;
        if (_snapMode) { _linkAskedIds.Add(o.Id); return false; }
        int id = o.Id;
        string list = string.Join(", ", EasyLink.EasyBehaviors(o).Select(Words).Distinct().Take(6));
        _ = AskAsync();
        return true;

        async Task AskAsync()
        {
            var r = await Dialogs.Ask(this, "Built on EasyEngine",
                $"“{o.Name}” has EasyEngine behaviors ({list}). Give it the mechanism {mechanism} too?\n\n" +
                "It will have both: BuilderEngine keeps the behaviors untouched (edit them in EasyEngine) and runs only its mechanisms; " +
                "EasyEngine runs its behaviors and, through the link, its mechanisms.",
                "Add it", "Cancel");
            if (r != "Add it" || _play != null || _scene.Find(id) is not { } target || target.Has(mechanism)) return;
            _linkAskedIds.Add(id);
            Checkpoint();
            target.Mechanisms.Add(new SceneMechanism { Name = mechanism });
            Select(id);
            SetStatus(true, $"Added {mechanism} to {target.Name} (it keeps its EasyEngine behaviors).");
        }
    }
}
