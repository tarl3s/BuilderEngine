using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// TOOLS FEATURE: editor tools — mechanisms with 'when tool …' (EditorTools.cs in BuilderEngine.Script). The TOOLS row
// under the scene bar has "Select" and one button per tool; the active tool gets the canvas's clicks and drags, and its
// params show in an options row (the inspector's param fields). Each use (press → release, a button, a key) is one
// undo step.
public sealed partial class MainWindow
{
    // Wraps onto a second line when the scene area is narrow (at 1024 px wide the last tools were cut off and unreachable).
    readonly WrapPanel _toolButtons = new() { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
    readonly WrapPanel _toolOptions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _toolOptionsRow = new() { IsVisible = false, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0) };
    Control? _toolsBar;
    /// <summary>The active tool's name (null = Select: click to select, drag to move).</summary>
    string? _activeTool;
    ToolRunner? _toolRunner;
    string? _toolRunnerKey;
    bool _toolUseOpen;
    /// <summary>Each tool's options as set in the options row (kept while the editor is open).</summary>
    readonly Dictionary<string, SceneMechanism> _toolParams = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, MechanismDef?> _toolParse = new();

    Control ToolsBar()
    {
        var row = new DockPanel { Margin = new Thickness(12, 0, 12, 6), VerticalAlignment = VerticalAlignment.Center };
        var label = Label("TOOLS");
        ToolTip.SetTip(label, "Tools are mechanisms with 'when tool …': they edit the scene while you build. Open one (in the Starter kit) to see how it works, or make your own.");
        label.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        row.Children.Add(_toolButtons);
        _toolOptionsRow.Child = _toolOptions;
        _toolOptions.Margin = new Thickness(12, 4, 12, 4);
        var panel = new StackPanel();
        panel.Children.Add(row);
        panel.Children.Add(_toolOptionsRow);
        panel.Children.Add(TileBar()); // painting tiles (MainWindow.TilePaint.cs)
        _toolsBar = panel;
        RefreshToolsBar();
        return panel;
    }

    void ShowToolsBar(bool show)
    {
        if (_toolsBar != null) _toolsBar.IsVisible = show;
    }

    /// <summary>The tools among the editor's mechanisms (starter kit, project, ones copied from My Library), in library order.</summary>
    List<(EditorTool Tool, string Source)> AvailableTools()
    {
        var list = new List<(EditorTool, string)>();
        foreach (var (name, src) in _sources)
        {
            if (!src.Contains("when tool", StringComparison.Ordinal)) continue;
            if (!_toolParse.TryGetValue(src, out var def))
            {
                try { def = Parser.Parse(src).FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)); }
                catch (ScriptError) { def = null; }
                if (_toolParse.Count > 200) _toolParse.Clear();
                _toolParse[src] = def;
            }
            if (def != null && EditorTools.IsTool(def)) list.Add((new EditorTool(def), src));
        }
        return list;
    }

    void RefreshToolsBar()
    {
        if (_toolsBar == null) return;
        var tools = AvailableTools();
        if (_activeTool != null && tools.All(t => !string.Equals(t.Tool.Name, _activeTool, StringComparison.OrdinalIgnoreCase))) SetActiveTool(null, quiet: true);
        _toolButtons.Children.Clear();
        var select = ToolButton("↖ Select", "Click to select, drag to move (Esc goes back to this)", _activeTool == null);
        select.Click += (_, _) => SetActiveTool(null);
        _toolButtons.Children.Add(select);
        foreach (var (tool, src) in tools)
        {
            string name = tool.Name;
            string about = EditorTools.Description(src, name);
            var b = ToolButton(SplitWords(name), ToolHelp(tool, about), string.Equals(_activeTool, name, StringComparison.OrdinalIgnoreCase));
            b.Click += (_, _) => OnToolButton(name);
            b.ContextMenu = ToolMenu(name);
            _toolButtons.Children.Add(b);
        }
        RefreshToolOptions();
    }

    static Button ToolButton(string text, string tip, bool active)
    {
        var b = PlainButton(text);
        b.Padding = new Thickness(10, 4);
        b.FontSize = 12;
        ToolTip.SetTip(b, tip);
        StyleTab(b, active);
        b.Padding = new Thickness(10, 4);
        return b;
    }

    ContextMenu ToolMenu(string name)
    {
        var open = new MenuItem { Header = "Open this tool (see how it works)" };
        open.Click += (_, _) => OpenMechanism(name);
        var menu = new ContextMenu();
        menu.Items.Add(open);
        return menu;
    }

    static string ToolHelp(EditorTool t, string about)
    {
        var how = new List<string>();
        if (t.Has("click")) how.Add("click");
        if (t.Has("drag")) how.Add("drag");
        if (t.ShowsBox) how.Add("drag a box");
        if (t.Has("used")) how.Add("runs when you press this button");
        foreach (var k in t.Keys) how.Add($"key {k.ToUpperInvariant()}");
        string text = about.Length > 0 ? about : $"The tool {t.Name}.";
        return text + (how.Count > 0 ? "\n(" + string.Join(", ", how) + ")" : "") + "\nRight-click to open it.";
    }

    /// <summary>"PlatformPainter" → "Platform Painter".</summary>
    static string SplitWords(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(name[i] == '_' ? ' ' : name[i]);
        }
        return sb.ToString();
    }

    void OnToolButton(string name)
    {
        if (_play != null) return;
        var tool = AvailableTools().FirstOrDefault(t => string.Equals(t.Tool.Name, name, StringComparison.OrdinalIgnoreCase)).Tool;
        if (tool == null) return;
        bool stays = tool.UsesPointer || tool.Keys.Any();
        if (stays || tool.Def.Params.Count > 0) SetActiveTool(tool.Name);
        if (tool.Has("used")) RunToolOnce(tool.Name, "used");
        else if (!stays) SetStatus(true, $"{SplitWords(tool.Name)} has nothing to do yet: give it a 'when tool used' or 'when tool click at x, y'.");
    }

    void SetActiveTool(string? name, bool quiet = false)
    {
        _canvas.CancelToolDrag();
        _activeTool = name;
        var tool = name == null ? null : AvailableTools().FirstOrDefault(t => t.Tool.Name == name).Tool;
        _canvas.ToolActive = tool is { UsesPointer: true };
        _canvas.ToolShowsBox = tool is { ShowsBox: true };
        if (tool != null && _canvas.SelectedId != 0 && tool.UsesPointer) _canvas.InvalidateVisual();
        RefreshToolsBar();
        if (quiet) return;
        if (tool == null) SetStatus(true, "Select: click an object to select it, drag to move it.");
        else
        {
            var how = new List<string>();
            if (tool.ShowsBox) how.Add("drag a box on the scene");
            else if (tool.Has("drag")) how.Add("click or drag on the scene");
            else if (tool.Has("click")) how.Add("click on the scene");
            foreach (var k in tool.Keys) how.Add($"press {k.ToUpperInvariant()}");
            SetStatus(true, $"{SplitWords(tool.Name)}: {(how.Count > 0 ? string.Join(", ", how) : "press its button to use it")}. Esc goes back to Select; Ctrl+Z undoes a whole use.");
        }
        _canvas.Focus();
    }

    /// <summary>The options row: the active tool's params, with the inspector's fields (they change the tool, not the scene).</summary>
    void RefreshToolOptions()
    {
        _toolOptions.Children.Clear();
        var tool = _activeTool == null ? null : AvailableTools().FirstOrDefault(t => t.Tool.Name == _activeTool).Tool;
        if (tool == null || (tool.Def.Params.Count == 0 && !tool.Has("used"))) { _toolOptionsRow.IsVisible = false; return; }
        if (!_toolParams.TryGetValue(tool.Name, out var settings)) _toolParams[tool.Name] = settings = new SceneMechanism { Name = tool.Name };
        var title = new TextBlock { Text = SplitWords(tool.Name), Foreground = Palette.Accent, FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _toolOptions.Children.Add(title);
        var holder = new SceneObject { Id = 0, Name = tool.Name };
        foreach (var p in tool.Def.Params)
        {
            var row = ParamRow(holder, settings, p);
            row.Width = 250;
            row.Margin = new Thickness(0, 2, 16, 2);
            _toolOptions.Children.Add(row);
        }
        if (tool.Has("used"))
        {
            var use = AccentButton("▶ Use");
            use.Padding = new Thickness(12, 4);
            use.Click += (_, _) => RunToolOnce(tool.Name, "used");
            _toolOptions.Children.Add(use);
        }
        _toolOptionsRow.IsVisible = true;
    }

    /// <summary>The runner for the active tool (made again when the tool's code changed).</summary>
    ToolRunner? Runner(string name)
    {
        if (!_sources.TryGetValue(name, out var src)) return null;
        string key = name + "\n" + src;
        if (_toolRunner == null || _toolRunnerKey != key)
        {
            try
            {
                _toolRunner = new ToolRunner(name, _sources.Values, _projectDir);
                _toolRunner.Message += OnToolMessage;
                _toolRunnerKey = key;
            }
            catch (ScriptError e)
            {
                SetStatus(false, $"{name}: {e.Message}");
                Log($"⚠ {name}: {e}");
                _toolRunner = null;
                _toolRunnerKey = null;
                return null;
            }
        }
        _toolRunner.Params.Clear();
        if (_toolParams.TryGetValue(name, out var settings))
            foreach (var (k, v) in settings.Params) _toolRunner.Params[k] = v;
        _toolRunner.SelectedId = _canvas.SelectedId;
        return _toolRunner;
    }

    void OnToolMessage(string msg)
    {
        Log(msg);
        SetStatus(!msg.StartsWith('⚠'), msg.TrimStart('⚠', ' '));
    }

    /// <summary>The canvas's pointer events while a tool is active: one use from the press to the release.</summary>
    void OnToolPointer(string ev, Point at)
    {
        if (_play != null || _activeTool == null) return;
        if (ev == "click")
        {
            if (Runner(_activeTool) is not { } r) return;
            r.BeginUse(_scene);
            _toolUseOpen = true;
        }
        if (!_toolUseOpen || _toolRunner == null) return;
        _toolRunner.Fire(ev, Math.Round(at.X, 2), Math.Round(at.Y, 2));
        AfterToolEvent();
        if (ev == "release") FinishToolUse();
    }

    void AfterToolEvent()
    {
        if (_toolRunner != null && _toolRunner.SelectedId != _canvas.SelectedId) _canvas.SelectedId = _toolRunner.SelectedId;
        _canvas.InvalidateVisual();
    }

    void FinishToolUse()
    {
        _toolUseOpen = false;
        if (_toolRunner?.EndUse() is { } before) PushUndo(before);
        if (_scene.Find(_canvas.SelectedId) == null) _canvas.SelectedId = 0;
        _canvas.InvalidateVisual();
        RebuildInspector();
    }

    /// <summary>A use without the pointer: the tool's button ('when tool used') or a key ('when tool key "r"').</summary>
    void RunToolOnce(string name, string ev, string? key = null)
    {
        if (_play != null || Runner(name) is not { } r) return;
        r.BeginUse(_scene);
        _toolUseOpen = true;
        r.Fire(ev, _canvas.ViewCenter.X, _canvas.ViewCenter.Y, key);
        AfterToolEvent();
        FinishToolUse();
    }

    /// <summary>Keys while a tool is active: Esc back to Select, and the tool's own 'when tool key'.</summary>
    bool ToolKey(KeyEventArgs e)
    {
        if (_activeTool == null || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0) return false;
        if (e.Key == Key.Escape) { SetActiveTool(null); return true; }
        var tool = AvailableTools().FirstOrDefault(t => t.Tool.Name == _activeTool).Tool;
        string k = KeyName(e.Key); // the same names a game uses
        if (tool == null || !tool.Keys.Contains(k)) return false;
        RunToolOnce(tool.Name, "key", k);
        return true;
    }

    /// <summary>A project opened: its tools may differ, and the old runner belongs to the old project.</summary>
    void ToolsOnProjectLoaded()
    {
        _toolRunner = null;
        _toolRunnerKey = null;
        _toolUseOpen = false;
        if (_toolsBar == null) return;
        SetActiveTool(null, quiet: true);
    }
}
