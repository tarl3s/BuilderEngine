using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using BuilderEngine.Script;

namespace BuilderEngine.Player;

/// <summary>
/// An exported game: opens the project in the <c>game/</c> folder next to this program and plays its first scene in
/// one window. <c>--project &lt;folder&gt;</c> plays another project (dev), <c>--mute</c> plays without sound,
/// <c>--check</c> plays two seconds headless and exits with the number of script errors, <c>--snap &lt;file.png&gt;</c>
/// renders the window after a moment and exits (visual checks; with <c>--snap-menu</c> the Esc menu is open).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--check")) return Check(args);
        return AppBuilder.Configure<PlayerApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }

    internal static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The project to play: --project, else game/ next to the program.</summary>
    internal static string? GameFolder(string[] args) =>
        Arg(args, "--project") is { } p ? Path.GetFullPath(p) : GameBoot.FindGame(AppContext.BaseDirectory);

    static int Check(string[] args)
    {
        string? dir = GameFolder(args);
        if (dir == null || !Project.Exists(dir)) { Console.Error.WriteLine("No game folder."); return 100; }
        var p = Project.Load(dir);
        var w = GameBoot.NewWorld(p, dir, Path.Combine(Path.GetTempPath(), "BuilderEngine_player_check"));
        w.LoadScene(EasyCompat.FirstScene(p));
        w.RunFor(2);
        foreach (var e in w.Errors) Console.Error.WriteLine(e);
        Console.WriteLine($"{p.Name}: {w.Objects.Count} objects, {w.Errors.Count} errors");
        return w.Errors.Count;
    }
}

public sealed class PlayerApp : Application
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
            var args = desktop.Args ?? [];
            desktop.MainWindow = new PlayerWindow(Program.GameFolder(args), args.Contains("--mute"), Program.Arg(args, "--snap"), args.Contains("--snap-menu"));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
