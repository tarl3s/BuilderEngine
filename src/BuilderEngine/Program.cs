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
        // The code editor's look (AvaloniaEdit, CodeEditor.cs).
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://BuilderEngine/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Every window gets the hammer icon (Assets/BuilderEngine.ico, drawn by tools/Make-Icon.ps1).
        try
        {
            var icon = new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://BuilderEngine/Assets/BuilderEngine.ico")));
            Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Avalonia.Controls.Window>((w, _) => w.Icon ??= icon);
        }
        catch { /* no icon is fine */ }

        // Avalonia shows a window a moment before its first frame is drawn: an empty black box that looks like a command
        // prompt flashing. The big windows start see-through and appear once that first frame is ready.
        Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Avalonia.Controls.Window>((w, _) =>
        {
            if (w is not (LauncherWindow or MainWindow)) return;
            WindowFit.ToScreen(w); // never taller or wider than a small (or scaled) screen (WindowFit.cs)
            if (w.Opacity > 0) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => w.Opacity = 1, Avalonia.Threading.DispatcherPriority.Background);
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // An "ollama serve" BuilderEngine started for the local AI stops with it (one the person started stays).
            desktop.Exit += (_, _) => BuilderEngine.Script.OllamaManager.Stop();
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
