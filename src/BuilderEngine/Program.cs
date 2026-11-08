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
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
