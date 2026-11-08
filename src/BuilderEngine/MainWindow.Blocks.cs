using Avalonia.Controls;

namespace BuilderEngine;

// BLOCKS FEATURE (stub — replaced by the blocks feature branch).
public sealed partial class MainWindow
{
    /// <summary>Creates the blocks view shown left of the code (called once).</summary>
    Control CreateBlocksView() => new TextBlock { Text = "Blocks view coming soon.", Foreground = Palette.TextDim, Margin = new Avalonia.Thickness(16) };

    /// <summary>The open mechanism's source changed in the code view (it parses): redraw the blocks.</summary>
    void BlocksShow(string source) { }
}
