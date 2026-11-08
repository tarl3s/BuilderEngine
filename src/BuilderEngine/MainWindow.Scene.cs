using Avalonia.Controls;

namespace BuilderEngine;

// SCENE FEATURE (stub — replaced by the scene feature branch).
public sealed partial class MainWindow
{
    /// <summary>Before the UI is built: load the project (may add project mechanisms to _sources).</summary>
    void SceneInit() { }

    Control CreateSceneView() => new TextBlock { Text = "Scene view coming soon.", Foreground = Palette.TextDim, Margin = new Avalonia.Thickness(16) };
    Control CreateInspector() => new TextBlock { Text = "Inspector", Foreground = Palette.TextDim, Margin = new Avalonia.Thickness(16) };

    void ScenePlay() { }
    void SceneStop() { }
    void SceneSave() { }
    void SceneOnClosing() { }
    /// <summary>A mechanism was added or its source changed (refresh pickers / inspector params).</summary>
    void SceneOnMechanismsChanged() { }
    void SceneOnMechanismRenamed(string oldName, string newName) { }
}
