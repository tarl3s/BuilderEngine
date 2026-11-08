using Avalonia.Media;

namespace BuilderEngine;

/// <summary>The BuilderEngine look: black surfaces, a faded orange accent, light text.</summary>
public static class Palette
{
    public static readonly IBrush Bg0 = Solid("#0B0B0B");     // editor background
    public static readonly IBrush Bg1 = Solid("#111111");     // panels
    public static readonly IBrush Bg2 = Solid("#1C1C1C");     // inputs, buttons
    public static readonly IBrush Border = Solid("#262626");
    public static readonly IBrush Text = Solid("#ECECEC");
    public static readonly IBrush TextDim = Solid("#A3A3A3");
    public static readonly IBrush Accent = Solid("#D97757");  // faded orange
    public static readonly IBrush AccentSoft = Solid("#2A1D17");
    public static readonly IBrush AccentBorder = Solid("#5A3A2C");
    public static readonly IBrush AccentText = Solid("#141414");
    public static readonly IBrush Ok = Solid("#6FAE8E");
    public static readonly IBrush Error = Solid("#E07A6A");

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, JetBrains Mono, monospace");

    static IBrush Solid(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}
