using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// AI FEATURE: the "✨ AI" tab in the right column (AiPanel.cs), the "✨ Ask the AI" button in the Mechanism tab, the
// "✨ Fix" button on the console when it shows an error, and the AI settings (AiSettingsWindow.cs, kept in
// settings.json with the rest). The headless part — prompts, reading answers, testing them — is in BuilderEngine.Script/Ai*.cs.
public sealed partial class MainWindow : IAiHost
{
    AiPanel _aiPanel = null!;
    AiSettings _aiSettings = null!;
    readonly ContentControl _rightHost = new();
    readonly Button _rightTabInspector = new(), _rightTabAi = new();
    readonly Button _aiFixButton = new();
    Control? _inspectorPage;
    bool _aiTab;
    /// <summary>The last error the console showed (cleared with the console).</summary>
    string? _consoleError;
    /// <summary>The open mechanism's code check error, if any (checked when asked: cheap, always current).</summary>
    string? CodeError()
    {
        if (_current == null || !_sources.TryGetValue(_current, out var src)) return null;
        try { Parser.Parse(src); return null; }
        catch (ScriptError e) { return e.ToString(); }
    }
    /// <summary>--snap answers with a fake AI instead of a real one.</summary>
    IAiClient? _aiClientOverride;
    const string RightColumnNormal = "230,*,330", RightColumnAi = "230,*,420";

    /// <summary>The right column's top part: tabs "Inspector" / "✨ AI" over the inspector (or the AI panel).</summary>
    Control AiRightTop(Control inspectorPage)
    {
        _aiSettings = new AiSettings(_settings);
        _aiPanel = new AiPanel(this);
        _inspectorPage = inspectorPage;
        _rightTabInspector.Content = "Inspector";
        _rightTabInspector.Click += (_, _) => ShowAiTab(false);
        _rightTabAi.Content = "✨ AI";
        ToolTip.SetTip(_rightTabAi, "The AI assistant: make, change, explain and fix mechanisms");
        _rightTabAi.Click += (_, _) => ShowAiTab(true);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(10, 8, 10, 4) };
        tabs.Children.Add(_rightTabInspector);
        tabs.Children.Add(_rightTabAi);
        var strip = new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = tabs };
        var root = new DockPanel();
        DockPanel.SetDock(strip, Dock.Top);
        root.Children.Add(strip);
        root.Children.Add(_rightHost);
        ShowAiTab(false);
        return root;
    }

    internal void ShowAiTab(bool ai)
    {
        _aiTab = ai;
        _rightHost.Content = ai ? _aiPanel : _inspectorPage;
        StyleTab(_rightTabInspector, !ai);
        StyleTab(_rightTabAi, ai);
        _rightTabInspector.Padding = _rightTabAi.Padding = new Thickness(12, 5);
        if (_bodyGrid != null) _bodyGrid.ColumnDefinitions = new ColumnDefinitions(ai ? RightColumnAi : RightColumnNormal);
        if (ai) { _aiPanel.RefreshProvider(); _aiPanel.SetMode(_aiPanel.Mode); }
    }

    /// <summary>Opens the AI tab in a mode, ready to type.</summary>
    internal void OpenAi(AiMode mode)
    {
        ShowAiTab(true);
        _aiPanel.SetMode(mode);
        _aiPanel.FocusInput();
    }

    /// <summary>"✨ Ask the AI" in the Mechanism tab strip: Edit the open mechanism.</summary>
    Control AiAskButton()
    {
        var b = PlainButton("✨ Ask the AI");
        b.Padding = new Thickness(12, 5);
        b.Margin = new Thickness(0, 8, 12, 8);
        b.Foreground = Palette.Accent;
        ToolTip.SetTip(b, "Ask the AI to change this mechanism, explain it, or fix it");
        b.Click += (_, _) => OpenAi(CodeError() != null ? AiMode.Fix : AiMode.Edit);
        return b;
    }

    /// <summary>The console's title row with the "✨ Fix" button (shown while there is an error to fix).</summary>
    Control AiConsoleHeader(Control title)
    {
        _aiFixButton.Content = "✨ Fix";
        _aiFixButton.Background = Palette.AccentSoft;
        _aiFixButton.Foreground = Palette.Accent;
        _aiFixButton.BorderBrush = Palette.AccentBorder;
        _aiFixButton.BorderThickness = new Thickness(1);
        _aiFixButton.CornerRadius = new CornerRadius(7);
        _aiFixButton.Padding = new Thickness(10, 2);
        _aiFixButton.FontSize = 11.5;
        _aiFixButton.IsVisible = false;
        ToolTip.SetTip(_aiFixButton, "Ask the AI to fix this error (you'll see the change before it happens)");
        _aiFixButton.Click += (_, _) =>
        {
            OpenAi(AiMode.Fix);
            if (((IAiHost)this).ErrorToFix != null) _ = _aiPanel.SendAsync("");
        };
        var row = new DockPanel();
        DockPanel.SetDock(_aiFixButton, Dock.Right);
        row.Children.Add(_aiFixButton);
        row.Children.Add(title);
        return row;
    }

    /// <summary>Every console line passes here (null = the console was cleared).</summary>
    void AiOnLog(string? line)
    {
        if (line == null) _consoleError = null;
        else if (line.StartsWith('⚠') && !line.StartsWith("⚠ AI")) _consoleError = line.TrimStart('⚠', ' ');
        else return; // ordinary lines (prints while playing) change nothing
        UpdateFixButton();
    }

    /// <summary>The status bar changed (the open mechanism's code check may have found or lost an error).</summary>
    void AiOnStatus(bool ok, string text) => UpdateFixButton();

    void UpdateFixButton()
    {
        if (_aiPanel == null) return;
        _aiFixButton.IsVisible = ((IAiHost)this).ErrorToFix != null;
        if (_aiTab && _aiPanel.Mode == AiMode.Fix && !_aiPanel.Busy) _aiPanel.SetMode(AiMode.Fix);
    }

    // ---------------- IAiHost ----------------

    string? IAiHost.OpenMechanismName => _current;

    string? IAiHost.ErrorToFix => CodeError() ?? _consoleError;

    string IAiHost.ProviderLabel => _aiClientOverride?.Name ?? _aiSettings.Label();

    IEnumerable<string> IAiHost.Secrets => _aiSettings.Secrets();

    string IAiHost.LogFile => Path.Combine(Path.GetTempPath(), "builderengine_ai.log");

    void IAiHost.Log(string line) => Log(line);

    async Task<IAiClient> IAiHost.CreateClientAsync()
    {
        if (_aiClientOverride != null) return _aiClientOverride;
        var models = _aiSettings.Provider == AiSettings.Local && _aiSettings.LocalModel.Length == 0
            ? await new OllamaClient("", _aiSettings.OllamaUrl).ListModelsAsync() : [];
        return _aiSettings.CreateClient(models);
    }

    AiContext IAiHost.BuildContext(AiMode mode)
    {
        string? open = _current;
        string? error = null;
        if (mode == AiMode.Fix)
        {
            if (CodeError() is { } codeError) error = codeError;
            else if (_consoleError != null)
            {
                error = _consoleError;
                // The console names the mechanism ("Coin (line 4): …"): open it, so the person sees what gets fixed.
                if (AiAssistant.MechanismInError(_consoleError, _sources.Keys) is { } name)
                {
                    if (name != _current) OpenMechanism(name);
                    open = name;
                }
            }
        }
        string? selection = null;
        if (mode == AiMode.Explain && !_sceneTab && _code.SelectedText is { Length: > 0 } sel) selection = sel;
        return new AiContext
        {
            Sources = new Dictionary<string, string>(_sources, StringComparer.OrdinalIgnoreCase),
            ProjectMechanisms = _sources.Keys.Where(IsProjectMechanism).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            // Make only looks at the open mechanism while it is on screen; the other modes work on it.
            OpenName = mode == AiMode.Make && _sceneTab ? null : open,
            Selection = selection,
            Error = error,
            SceneName = _scene.Name,
            SceneNames = _project.Scenes.Select(s => s.Name).ToList(),
            SceneObjects = _scene.Objects.Select(o => new AiSceneObject(o.Name, o.Tags.ToList(), o.Mechanisms.Select(m => m.Name).ToList())).ToList(),
            Scene = _scene.Clone(),
            AnswerLanguage = _aiSettings.AnswerLanguage == AiSettings.AnswerEnglish ? "English" : null,
        };
    }

    void IAiHost.Accept(AiResult result)
    {
        string? first = AiMerge.Apply(_sources, result.Mechanisms);
        if (first == null) return;
        RefreshLibrary();
        SceneOnMechanismsChanged();
        // Show it in blocks + code (reloads the open one when it was changed).
        _current = null;
        OpenMechanism(first);
        SetStatus(true, result.Mechanisms.Count == 1
            ? $"✨ {(result.Mechanisms[0].IsNew ? "Added" : "Updated")} {first}. Drag it onto an object in the scene to use it."
            : $"✨ Added {string.Join(", ", result.Mechanisms.Select(m => m.Name))}.");
    }

    void IAiHost.OpenSettings() => _ = OpenAiSettingsAsync();

    async Task OpenAiSettingsAsync()
    {
        if (_snapMode) return; // no modal windows in dev snapshots
        var win = new AiSettingsWindow(_aiSettings);
        await win.ShowDialog(this);
        if (win.Saved && !_aiSettings.Save() && _aiSettings.CanSave) Log("⚠ AI: couldn't write the AI settings.");
        _aiPanel.RefreshProvider();
    }
}
