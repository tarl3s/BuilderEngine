using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace BuilderEngine;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // --make-sample "Name" <folder>: write a sample game project and exit (no window).
        int i = Array.IndexOf(args, "--make-sample");
        if (i >= 0)
        {
            if (i + 2 >= args.Length) { Console.Error.WriteLine("usage: --make-sample \"Name\" <folder>"); return 2; }
            try { BuilderEngine.Script.SampleGames.Make(args[i + 1], args[i + 2]); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Dev and test runs (--project, --snap) go straight to the editor; a normal start shows the start screen.
            var args = desktop.Args ?? [];
            bool direct = args.Contains("--project") || args.Contains("--snap");
            var window = direct ? (Avalonia.Controls.Window)new MainWindow() : new LauncherWindow();
            desktop.MainWindow = window;
            // Dev: --snap-launcher <file.png> renders the start screen to a picture and exits.
            int s = Array.IndexOf(args, "--snap-launcher");
            if (s >= 0 && s + 1 < args.Length && !direct)
            {
                string png = args[s + 1];
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(700);
                    var size = new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
                    using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                    bmp.Render(window);
                    bmp.Save(png);
                    window.Close();
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
