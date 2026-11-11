using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace BuilderEngine.Script;

/// <summary>
/// "Export game…": a folder anyone can play without BuilderEngine —
/// <code>
/// My Game/
///     My Game.exe          the player (BuilderEngine.Player, renamed): only the game window, no editor
///     …                    the player's files (.NET included, nothing to install)
///     game/                a copy of the project: project.json, mechanisms/, behaviors/, images/, sounds/ (no saves, no plugins)
///     README.txt
/// </code>
/// and optionally <c>My Game.zip</c> next to it. The player comes from a prebuilt <c>player/</c> folder next to
/// BuilderEngine.exe (downloaded BuilderEngine ships one) or, on a computer with the source code and the .NET SDK, from
/// <c>dotnet publish</c> of src/BuilderEngine.Player (<see cref="PublishPlayer"/>). Headless, so it is tested.
/// </summary>
public static class GameExport
{
    /// <summary>The project's folder inside an exported game.</summary>
    public const string GameFolder = "game";
    /// <summary>The player's program as built (the export renames it after the game).</summary>
    public const string PlayerExe = "BuilderEngine.Player.exe";
    /// <summary>The prebuilt player's folder next to BuilderEngine.exe in a release.</summary>
    public const string PlayerFolder = "player";
    /// <summary>A picture in the game folder used as the game window's icon (picked when exporting).</summary>
    public const string IconFile = "icon.png";

    /// <summary>What to export: the project folder, where to put the game, the player to use (null = no player yet), a zip, an icon picture; <paramref name="IncludeFmod"/>: copy this computer's fmod.dll (and fmodstudio.dll) next to the game's program (off by default — FMOD has its own licence).</summary>
    public sealed record Options(string ProjectDir, string OutputDir, string? PlayerDir = null, bool Zip = false, string? IconImage = null, bool IncludeFmod = false);

    /// <summary>The game's folder, its program (null when no player was given), the zip (or null) and things worth telling.</summary>
    public sealed record Result(string Folder, string? Exe, string? Zip, List<string> Warnings);

    /// <summary>A file and folder name for the game: the project's name without characters Windows can't use.</summary>
    public static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim()) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        string s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 ? "My Game" : s;
    }

    /// <summary>
    /// Copies a project folder for playing: everything but saves, plugins, backups, half-written files and exported games
    /// kept inside the project (<paramref name="exportFolder"/>, the export being made, and any folder with a
    /// <c>game/project.json</c>) — exporting into the project's own folder never copies an export into the next one.
    /// </summary>
    public static int CopyProject(string projectDir, string dest, string? exportFolder = null)
    {
        int n = 0;
        Directory.CreateDirectory(dest);
        var skip = new List<string> { Path.GetFullPath(dest) };
        if (exportFolder != null) skip.Add(Path.GetFullPath(exportFolder));
        foreach (var dir in Directory.GetDirectories(projectDir, "*", SearchOption.AllDirectories))
            if (Project.Exists(Path.Combine(dir, GameFolder))) skip.Add(Path.GetFullPath(dir));
        bool Skipped(string file) => skip.Any(d => Path.GetFullPath(file).StartsWith(d.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        foreach (var file in Directory.GetFiles(projectDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(projectDir, file);
            string top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            string fname = Path.GetFileName(file);
            if (rel != fname && top.ToLowerInvariant() is Project.SavesFolder or "plugins" or ".backup" or ".git") continue;
            if (fname.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || fname.StartsWith("project.broken-", StringComparison.OrdinalIgnoreCase)) continue;
            if (Skipped(file)) continue;
            string to = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
            n++;
        }
        return n;
    }

    static int CopyFolder(string from, string to)
    {
        int n = 0;
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            n++;
        }
        return n;
    }

    /// <summary>Makes the game folder (replacing an earlier export of it) and the zip. Throws IOException with a friendly message.</summary>
    public static Result Export(Options o, Action<string>? progress = null)
    {
        if (!Project.Exists(o.ProjectDir)) throw new IOException($"There's no project in {o.ProjectDir}.");
        var project = Project.Load(o.ProjectDir);
        string name = SafeName(project.Name);
        string folder = Path.Combine(o.OutputDir, name);
        var warnings = new List<string>();

        if (Directory.Exists(folder))
        {
            bool earlier = Project.Exists(Path.Combine(folder, GameFolder)) || !Directory.EnumerateFileSystemEntries(folder).Any();
            if (!earlier) throw new IOException($"“{folder}” already exists and isn't an exported game. Pick another folder.");
            progress?.Invoke("Removing the earlier export…");
            Directory.Delete(folder, recursive: true);
        }
        Directory.CreateDirectory(folder);

        string? exe = null;
        if (o.PlayerDir != null)
        {
            progress?.Invoke("Copying the player…");
            int n = CopyFolder(o.PlayerDir, folder);
            string built = Path.Combine(folder, PlayerExe);
            if (File.Exists(built))
            {
                exe = Path.Combine(folder, name + ".exe");
                if (!string.Equals(built, exe, StringComparison.OrdinalIgnoreCase)) File.Move(built, exe, overwrite: true);
            }
            else warnings.Add($"The player folder has no {PlayerExe} ({n} files copied).");
        }
        else warnings.Add("No player was found, so the folder has the game but no program to start it.");

        progress?.Invoke("Copying the project…");
        string game = Path.Combine(folder, GameFolder);
        int files = CopyProject(o.ProjectDir, game, exportFolder: folder);
        if (o.IconImage is { Length: > 0 } icon && Images.Resolve(o.ProjectDir, icon) is { } iconPath)
            File.Copy(iconPath, Path.Combine(game, IconFile), overwrite: true);
        if (Directory.Exists(Path.Combine(o.ProjectDir, "plugins")) && Directory.EnumerateFiles(Path.Combine(o.ProjectDir, "plugins"), "*.dll").Any())
            warnings.Add("C# plugins aren't exported: exported games play without them.");
        CopyFmod(o, folder, progress, warnings);
        File.WriteAllText(Path.Combine(folder, "README.txt"),
            $"{project.Name}\r\n\r\nDouble-click “{name}.exe” to play. F11: full screen. Esc: menu.\r\n\r\nMade with BuilderEngine.\r\n",
            new UTF8Encoding(false));
        progress?.Invoke($"Copied {files} project files.");

        string? zip = null;
        if (o.Zip)
        {
            progress?.Invoke("Making the zip…");
            zip = Path.Combine(o.OutputDir, name + ".zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        progress?.Invoke("Done.");
        return new Result(folder, exe, zip, warnings);
    }

    /// <summary>
    /// Optional FMOD audio: with <see cref="Options.IncludeFmod"/> this computer's fmod.dll (and fmodstudio.dll) go next to the
    /// game's program, so it plays with FMOD (and its FMOD Studio events) everywhere; without it the game uses the classic
    /// audio unless its player has FMOD installed. The project's <c>fmod</c> bank folder is part of the project copy.
    /// </summary>
    static void CopyFmod(Options o, string folder, Action<string>? progress, List<string> warnings)
    {
        bool banks = FmodSettings.HasBanks(o.ProjectDir);
        if (!o.IncludeFmod)
        {
            if (banks) warnings.Add("This game has FMOD Studio banks but FMOD wasn't included: its events play only where FMOD is installed.");
            return;
        }
        var files = FmodSettings.ExportFiles();
        if (files.Count == 0) { warnings.Add("FMOD isn't installed on this computer, so it wasn't included: the game uses the classic audio."); return; }
        progress?.Invoke("Including FMOD…");
        foreach (var f in files) File.Copy(f, Path.Combine(folder, Path.GetFileName(f)), overwrite: true);
        if (banks && files.Count < 2) warnings.Add("fmodstudio.dll wasn't found, so the game's FMOD Studio events won't play.");
        warnings.Add("FMOD was included: you must follow FMOD's licence (fmod.com/licensing) to share this game.");
    }

    // ---------------- Where the player comes from ----------------

    /// <summary>The prebuilt player next to the editor (<c>player/</c> in a release), or null.</summary>
    public static string? FindPrebuiltPlayer(string appDir)
    {
        string d = Path.Combine(appDir, PlayerFolder);
        return File.Exists(Path.Combine(d, PlayerExe)) ? d : null;
    }

    /// <summary>src/BuilderEngine.Player/BuilderEngine.Player.csproj when the editor runs from the source code, or null.</summary>
    public static string? FindPlayerProject(string appDir)
    {
        var d = new DirectoryInfo(appDir);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "src", "BuilderEngine.Player", "BuilderEngine.Player.csproj");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Builds the player with the .NET SDK (<c>dotnet publish</c>, self-contained for Windows, so players need nothing
    /// installed) into <paramref name="outDir"/>. Lines of output go to <paramref name="log"/>. Returns the folder, or null.
    /// </summary>
    public static string? PublishPlayer(string csproj, string outDir, Action<string>? log = null)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(csproj)!,
        };
        foreach (var a in new[] { "publish", csproj, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", outDir, "-p:DebugType=None", "-nologo", "-v:q" })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) { log?.Invoke("Couldn't start dotnet."); return null; }
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke(e.Data.Trim()); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke(e.Data.Trim()); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode != 0) { log?.Invoke($"dotnet publish failed (exit code {p.ExitCode})."); return null; }
            return File.Exists(Path.Combine(outDir, PlayerExe)) ? outDir : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            log?.Invoke("The .NET SDK (dotnet) isn't installed on this computer.");
            return null;
        }
    }
}

/// <summary>
/// Starting a game the way the exported player (and the editor's Play) does: every starter mechanism plus the
/// project's own, the scene names for goto_scene, the score at 0, the project's pictures and sounds. Headless.
/// </summary>
public static class GameBoot
{
    /// <summary>The bundled project next to the player (<c>game/</c>), or null.</summary>
    public static string? FindGame(string playerDir)
    {
        string d = Path.Combine(playerDir, GameExport.GameFolder);
        return Project.Exists(d) ? d : null;
    }

    /// <summary>Where an exported game keeps its saves: %LOCALAPPDATA%\BuilderEngine Games\&lt;game&gt;\saves.</summary>
    public static string SaveFolderFor(string gameName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BuilderEngine Games", GameExport.SafeName(gameName), Project.SavesFolder);

    /// <summary>
    /// THE place where a world gets ready for a project — used by the editor's Play (MainWindow.UseProjectFiles) and by
    /// exported games (<see cref="NewWorld"/>): the project's folders (pictures, sounds, saves). Anything else a project
    /// gives every world it plays in (prefabs, behavior packages…) belongs here, so the editor and the player stay the same.
    /// Called after the starter kit and the project's mechanisms are loaded.
    /// </summary>
    public static void UseProject(World w, Project p, string projectDir, string? saveFolder = null)
    {
        w.ProjectFolder = projectDir;
        w.SaveFolder = saveFolder ?? Path.Combine(projectDir, Project.SavesFolder);
        UsePackages(w, p, projectDir);
        EasyCompat.Use(w, p); // an EasyEngine project: its behaviors play as EasyKit mechanisms and packages (EasyCompat.cs)
        w.UsePrefabs(p.Prefabs); // spawn "Slime" finds the project's prefabs (Prefabs.cs), in the editor and in exported games
    }

    /// <summary>
    /// Behavior packages (docs/BEHAVIOR-FORMAT.md): the project's own (<c>behaviors/</c>, copied into exported games),
    /// the installed ones and the shipped ones, first wins; a mechanism of the project with the same name wins over a
    /// package. A broken package file is said once in the console, never thrown.
    /// </summary>
    static void UsePackages(World w, Project p, string projectDir)
    {
        var problems = new List<string>();
        var packages = BehaviorPackage.DiscoverAll(projectDir, problems);
        w.UsePackages(packages, p.SplitMechanismFiles().Select(m => m.Name));
        foreach (var problem in problems) w.PackageProblem(problem);
    }

    /// <summary>A fresh world with every mechanism loaded (a broken one is reported in Errors and skipped).</summary>
    public static World NewWorld(Project p, string projectDir, string? saveFolder = null)
    {
        var w = new World();
        StarterKit.LoadInto(w);
        foreach (var (name, src) in p.Mechanisms)
        {
            try { w.Load(src); }
            catch (ScriptError e) { w.Errors.Add($"{name}: {e.Message}"); }
        }
        UseProject(w, p, projectDir, saveFolder);
        w.Globals["score"] = 0.0;
        w.UseScenes(p.Scenes.Select(s => s.Name));
        return w;
    }
}
