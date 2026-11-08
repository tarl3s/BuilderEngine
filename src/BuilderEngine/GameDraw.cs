using Avalonia.Input;
using Avalonia.Media;

namespace BuilderEngine;

/// <summary>
/// Small helpers the game's drawing and keyboard need, shared by the editor and the exported player
/// (src/BuilderEngine.Player compiles PlaySurface*.cs, TileDraw.cs, ImageCache.cs, Palette.cs, Audio.cs and this file
/// as linked sources, so the game looks and plays the same in both without a second copy of the code).
/// </summary>
static class GameDraw
{
    static readonly Dictionary<string, IBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A brush for "#RRGGBB" (cached), or <paramref name="fallback"/> when the text isn't a colour.</summary>
    public static IBrush BrushOf(string? hex, IBrush fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        if (BrushCache.TryGetValue(hex, out var b)) return b;
        b = Color.TryParse(hex, out var c) ? new SolidColorBrush(c).ToImmutable() : fallback;
        BrushCache[hex] = b;
        return b;
    }

    /// <summary>A key as Builder Script names it: "left", "space", "a"…</summary>
    public static string KeyName(Key k) => k switch
    {
        Key.Left => "left", Key.Right => "right", Key.Up => "up", Key.Down => "down", Key.Space => "space", Key.Enter => "enter",
        _ => k.ToString().ToLowerInvariant()
    };
}
