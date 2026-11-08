using Avalonia;
using Avalonia.Controls;

namespace BuilderEngine;

/// <summary>
/// The start screen and the editor open at a fixed size (1000×720, 1360×840) with a minimum size. On a small or
/// scaled screen (a 1366×768 laptop, or 1920×1080 at 150% = 1280×720 usable) that is taller than the screen, so the
/// status bar and the console started off the bottom and couldn't be reached. When the window opens it is shrunk
/// (and its minimum with it) to the screen's working area and centred there. Wired in Program.cs.
/// </summary>
static class WindowFit
{
    /// <summary>The client size and minimum size that fit in <paramref name="availW"/>×<paramref name="availH"/> (all in DIPs).</summary>
    public static (double W, double H, double MinW, double MinH) Fit(double w, double h, double minW, double minH, double availW, double availH)
    {
        availW = Math.Max(1, availW);
        availH = Math.Max(1, availH);
        return (Math.Min(w, availW), Math.Min(h, availH), Math.Min(minW, availW), Math.Min(minH, availH));
    }

    /// <summary>Shrinks an open window that doesn't fit on its screen; leaves one that fits exactly as it is.</summary>
    public static void ToScreen(Window win)
    {
        var screen = win.Screens.ScreenFromWindow(win) ?? win.Screens.Primary;
        if (screen == null) return;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var area = screen.WorkingArea;
        // The title bar and borders are outside Width/Height (the client area).
        double chromeW = Math.Max(0, (win.FrameSize?.Width ?? win.Width) - win.ClientSize.Width);
        double chromeH = Math.Max(0, (win.FrameSize?.Height ?? win.Height) - win.ClientSize.Height);
        var f = Fit(win.Width, win.Height, win.MinWidth, win.MinHeight, area.Width / scale - chromeW, area.Height / scale - chromeH);
        if (f.W >= win.Width && f.H >= win.Height) return;
        win.MinWidth = f.MinW;
        win.MinHeight = f.MinH;
        win.Width = f.W;
        win.Height = f.H;
        win.Position = new PixelPoint(area.X + (int)((area.Width - (f.W + chromeW) * scale) / 2), area.Y + (int)((area.Height - (f.H + chromeH) * scale) / 2));
    }
}
