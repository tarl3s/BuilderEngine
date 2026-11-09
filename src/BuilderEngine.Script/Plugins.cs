using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BuilderEngine.Plugin;

namespace BuilderEngine.Script;

/// <summary>A command or function a plugin adds (for the Plugins window and the blocks palette).</summary>
public sealed record PluginCommandInfo(string Name, string Help, string[] Inputs, string Plugin, bool IsFunction)
{
    /// <summary><c>confetti amount</c> / <c>wave(t)</c></summary>
    public string Usage => IsFunction ? $"{Name}({string.Join(", ", Inputs)})" : Inputs.Length == 0 ? Name : Name + " " + string.Join(", ", Inputs);
}

/// <summary>One plugin class that loaded: what it adds and what went wrong.</summary>
public sealed class LoadedPlugin(IBuilderPlugin instance, string name, string description, string source)
{
    public IBuilderPlugin Instance { get; } = instance;
    public string Name { get; } = name;
    public string Description { get; } = description;
    /// <summary>The file it came from (or "built-in" for plugins added from code).</summary>
    public string Source { get; } = source;
    public List<PluginCommandInfo> Commands { get; } = [];
    public List<string> Errors { get; } = [];
}

/// <summary>
/// C# plugins: every <c>*.dll</c> in a project's <c>plugins/</c> folder is loaded (into its own collectible load
/// context, from memory, so the file stays free to rebuild) and each class implementing
/// <see cref="IBuilderPlugin"/> is made. <see cref="Active"/> is installed into every new <see cref="World"/>:
/// games, each scene they go to, and editor tools. Nothing a plugin does wrong crashes the editor: load errors are
/// listed in <see cref="Errors"/>, failing commands become ordinary script errors.
/// </summary>
public sealed class PluginManager : IDisposable
{
    public const string FolderName = "plugins";

    /// <summary>The plugins every new world gets (null = none; the editor sets it once the person enabled plugins).</summary>
    public static PluginManager? Active
    {
        get => s_active;
        set { s_active = value; Changed(); }
    }
    static PluginManager? s_active;

    static int s_version;

    /// <summary>Goes up whenever the active plugins (and so their commands) may have changed: caches of their names check it.</summary>
    internal static int Version => Volatile.Read(ref s_version);

    static void Changed() => Interlocked.Increment(ref s_version);

    [ThreadStatic] static bool t_noPlugins;

    [ModuleInitializer]
    internal static void InstallPlugins() => World.Installers.Add(w => { if (!t_noPlugins) Active?.InstallInto(w); });

    /// <summary>The folder the plugins come from (null: only plugins added from code).</summary>
    public string? Folder { get; }
    public List<LoadedPlugin> Plugins { get; } = [];
    /// <summary>Files that couldn't be loaded, and plugins whose Register failed.</summary>
    public List<string> Errors { get; } = [];
    /// <summary>Messages for the console (load problems, a plugin's Log, a Register that failed in a world).</summary>
    public event Action<string>? Logged;

    PluginLoadContext? _context;

    public PluginManager(string? folder = null) => Folder = folder;

    public static string FolderOf(string projectDir) => Path.Combine(projectDir, FolderName);

    /// <summary>The plugin files of a project (the engine's own dlls, if someone copied them there, are skipped).</summary>
    public static List<string> PluginFiles(string projectDir)
    {
        string dir = FolderOf(projectDir);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.dll").Where(f => !IsEngineFile(f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool HasPlugins(string projectDir) => PluginFiles(projectDir).Count > 0;

    static bool IsEngineFile(string path) => Path.GetFileNameWithoutExtension(path) is "BuilderEngine.Plugin" or "BuilderEngine.Script" or "BuilderEngine";

    /// <summary>(Re)loads every plugin file in <see cref="Folder"/>. Returns how many plugins loaded.</summary>
    public int Load()
    {
        Unload();
        if (Folder == null || !Directory.Exists(Folder)) return 0;
        foreach (var f in Directory.GetFiles(Folder, "*.dll").Where(f => !IsEngineFile(f)).Order(StringComparer.OrdinalIgnoreCase))
            LoadFile(f);
        return Plugins.Count;
    }

    void LoadFile(string path)
    {
        string file = Path.GetFileName(path);
        try
        {
            _context ??= new PluginLoadContext(Folder);
            string name = Path.GetFileNameWithoutExtension(path);
            var asm = _context.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                      ?? _context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.OfType<Type>().ToArray();
                Report($"{file}: some of its code couldn't load ({e.LoaderExceptions.FirstOrDefault()?.Message ?? "unknown reason"}).");
            }
            var pluginTypes = types.Where(t => typeof(IBuilderPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }).ToList();
            if (pluginTypes.Count == 0)
            {
                Logged?.Invoke($"{file}: no plugin inside (kept as a library for the others).");
                return;
            }
            foreach (var t in pluginTypes)
            {
                if (t.GetConstructor(Type.EmptyTypes) == null) { Report($"{file}: {t.Name} needs a constructor without parameters."); continue; }
                IBuilderPlugin plugin;
                try { plugin = (IBuilderPlugin)Activator.CreateInstance(t)!; }
                catch (Exception e) { Report($"{file}: {t.Name} failed to start: {Inner(e).Message}"); continue; }
                Add(plugin, file);
            }
        }
        catch (BadImageFormatException) { Report($"{file} isn't a .NET plugin (a class library built for .NET 8)."); }
        catch (Exception e) { Report($"{file} couldn't be loaded: {Inner(e).Message}"); }
    }

    /// <summary>Adds a plugin that is already made (built-in ones, tests). Asks it what it adds right away.</summary>
    public LoadedPlugin Add(IBuilderPlugin plugin, string source = "built-in")
    {
        string name, description;
        Exception? nameFailed = null;
        try { name = plugin.Name; description = plugin.Description; }
        catch (Exception e) { name = plugin.GetType().Name; description = ""; nameFailed = e; } // plugin code: anything may come out
        var lp = new LoadedPlugin(plugin, string.IsNullOrWhiteSpace(name) ? plugin.GetType().Name : name, description ?? "", source);
        Plugins.Add(lp);
        if (nameFailed != null) Logged?.Invoke($"⚠ Plugin {lp.Name} ({source}): its Name or Description failed ({Inner(nameFailed).Message}); the class name is used.");
        try { plugin.Register(new Recorder(this, lp)); }
        catch (Exception e)
        {
            lp.Errors.Add(Inner(e).Message);
            Report($"Plugin {lp.Name} ({source}) failed while registering: {Inner(e).Message}");
        }
        Changed();
        return lp;
    }

    /// <summary>Gives a world every plugin's commands and functions (a plugin that fails is reported and skipped).</summary>
    public void InstallInto(World w)
    {
        foreach (var lp in Plugins.ToList())
        {
            if (lp.Errors.Count > 0) continue; // it already failed when it loaded
            try { lp.Instance.Register(new WorldHost(this, lp, w)); }
            catch (Exception e)
            {
                string msg = $"Plugin {lp.Name} failed: {Inner(e).Message}";
                w.Errors.Add(msg);
                Logged?.Invoke("⚠ " + msg);
            }
        }
    }

    /// <summary>Every command and function the loaded plugins add.</summary>
    public IEnumerable<PluginCommandInfo> AllCommands => Plugins.SelectMany(p => p.Commands);

    /// <summary>Forgets the plugins and unloads their code (once nothing uses it any more).</summary>
    public void Unload()
    {
        Plugins.Clear();
        Changed();
        Errors.Clear();
        var c = _context;
        _context = null;
        try { c?.Unload(); } catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (Active == this) Active = null;
        Unload();
    }

    void Report(string msg)
    {
        Errors.Add(msg);
        Logged?.Invoke("⚠ " + msg);
    }

    internal void Log(string msg) => Logged?.Invoke(msg);

    static Exception Inner(Exception e) => e is TargetInvocationException { InnerException: { } i } ? i : e;

    // ---------------- Names a plugin may not take ----------------

    static HashSet<string>? s_engineCommands, s_engineFunctions;

    /// <summary>The commands and functions the engine itself has (a plugin can't replace them).</summary>
    internal static (HashSet<string> Commands, HashSet<string> Functions) EngineNames()
    {
        if (s_engineCommands == null || s_engineFunctions == null)
        {
            t_noPlugins = true;
            try
            {
                var w = new World();
                s_engineCommands = new(w.Commands.Keys.Concat(World.BuiltInCommands).Concat(World.SceneCommands).Concat(EditorTools.Commands), StringComparer.OrdinalIgnoreCase);
                s_engineFunctions = new(w.Functions.Keys.Concat(World.BuiltInFunctions).Concat(EditorTools.Functions), StringComparer.OrdinalIgnoreCase);
            }
            finally { t_noPlugins = false; }
        }
        return (s_engineCommands, s_engineFunctions);
    }

    static string? CheckName(string name, bool function)
    {
        if (string.IsNullOrWhiteSpace(name)) return "a command or function needs a name";
        var words = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (function && words.Length > 1) return $"a function's name can't have spaces ('{name}')";
        foreach (var w in words)
            if (!(char.IsLetter(w[0]) || w[0] == '_') || !w.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return $"'{name}' is not a name (use letters, digits and _)";
        if (Parser.Keywords.Contains(words[0])) return $"'{words[0]}' is a Builder Script word";
        var (cmds, fns) = EngineNames();
        if (function ? fns.Contains(name) : cmds.Contains(name.Trim())) return $"the engine already has a {(function ? "function" : "command")} called '{name}'";
        return null;
    }

    /// <summary>
    /// The other plugin that already adds this command (or function), or null: the first plugin to add a name keeps it,
    /// a later one is told when it loads and left out of every world — never a silent replacement.
    /// </summary>
    LoadedPlugin? OwnerOf(string name, bool function, LoadedPlugin except) =>
        Plugins.FirstOrDefault(p => p != except && p.Commands.Any(c => c.IsFunction == function && string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));

    // ---------------- Hosts ----------------

    /// <summary>Records what a plugin adds when it loads (nothing runs).</summary>
    sealed class Recorder(PluginManager manager, LoadedPlugin lp) : IPluginHost
    {
        World? _native;
        public bool IsEditor => false;
        public string? ProjectFolder => manager.Folder == null ? null : Path.GetDirectoryName(manager.Folder.TrimEnd('\\', '/'));
        public object Native => _native ??= new World();
        public void Log(string text) => manager.Log($"[{lp.Name}] {text}");

        public void AddCommand(string name, Action<IPluginCommand> run, string help = "", params string[] inputs)
        {
            if (CheckName(name, function: false) is { } why) { manager.Report($"Plugin {lp.Name}: {why} — that command was left out."); return; }
            if (manager.OwnerOf(name, function: false, except: lp) is { } owner) { manager.Report($"Plugin {lp.Name}: the plugin {owner.Name} already adds a command called '{name.Trim()}' — this one was left out."); return; }
            if (lp.Commands.Any(c => !c.IsFunction && string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))) return;
            lp.Commands.Add(new PluginCommandInfo(name.Trim(), help ?? "", inputs ?? [], lp.Name, false));
        }

        public void AddFunction(string name, Func<List<object?>, object?> run, string help = "", params string[] inputs)
        {
            if (CheckName(name, function: true) is { } why) { manager.Report($"Plugin {lp.Name}: {why} — that function was left out."); return; }
            if (manager.OwnerOf(name, function: true, except: lp) is { } owner) { manager.Report($"Plugin {lp.Name}: the plugin {owner.Name} already adds a function called '{name.Trim()}' — this one was left out."); return; }
            if (lp.Commands.Any(c => c.IsFunction && string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))) return;
            lp.Commands.Add(new PluginCommandInfo(name.Trim(), help ?? "", inputs ?? [], lp.Name, true));
        }

    }

    /// <summary>Adds a plugin's commands and functions to one world.</summary>
    sealed class WorldHost(PluginManager manager, LoadedPlugin lp, World world) : IPluginHost
    {
        public bool IsEditor => world.IsEditor;
        public string? ProjectFolder => world.ProjectFolder;
        public object Native => world;
        public void Log(string text) => manager.Log($"[{lp.Name}] {text}");

        public void AddCommand(string name, Action<IPluginCommand> run, string help = "", params string[] inputs)
        {
            if (CheckName(name, function: false) != null || manager.OwnerOf(name, function: false, except: lp) != null) return; // reported when it loaded
            string key = name.Trim();
            string plugin = lp.Name;
            world.Commands[key] = c =>
            {
                try { run(new PluginCommand(c)); }
                catch (ScriptError) { throw; }
                catch (Exception e) { throw new ScriptError($"The plugin {plugin} failed in '{key}': {Inner(e).Message}", c.Line); }
            };
        }

        public void AddFunction(string name, Func<List<object?>, object?> run, string help = "", params string[] inputs)
        {
            if (CheckName(name, function: true) != null || manager.OwnerOf(name, function: true, except: lp) != null) return; // reported when it loaded
            string key = name.Trim();
            string plugin = lp.Name;
            world.Functions[key] = args =>
            {
                try { return Normalize(run(args)); }
                catch (ScriptError) { throw; }
                catch (Exception e) { throw new ScriptError($"The plugin {plugin} failed in '{key}(…)': {Inner(e).Message}", 0); }
            };
        }

        /// <summary>Scripts use doubles: an int, float or decimal from a plugin becomes one.</summary>
        static object? Normalize(object? v) => v switch
        {
            int i => (double)i, long l => (double)l, float f => (double)f, decimal m => (double)m,
            short s => (double)s, byte b => (double)b, uint u => (double)u,
            _ => v
        };
    }

    /// <summary>A running command as a plugin sees it.</summary>
    sealed class PluginCommand(CommandContext c) : IPluginCommand
    {
        public IReadOnlyList<object?> Args => c.Args;
        public object? Arg(int i) => c.Arg(i);
        public object? NamedArg(string connector, int i = 0) => c.NamedArg(connector, i);
        public bool Has(string connector) => c.Has(connector);
        public double Number(object? value) => c.Number(value);
        public string Text(object? value) => World.ToText(value);
        public int Line => c.Line;
        public IPluginObject Self => c.Self;
        public IPluginObject? Other => c.Other;
        public IEnumerable<IPluginObject> Objects(string? tagOrName = null) =>
            c.World.Objects.Where(o => !o.Destroyed && (tagOrName == null || o.Is(tagOrName))).ToList();
        public IPluginObject? Find(string nameOrTag) => c.World.FindObject(nameOrTag);
        public IPluginObject Spawn(string name, double x, double y, params string[] mechanisms)
        {
            try { return c.World.Spawn(name, x, y, mechanisms ?? []); }
            catch (ScriptError e) { throw new ScriptError(e.Message, c.Line); }
        }
        public IDictionary<string, object?> Globals => c.World.Globals;
        public double Time => c.World.Time;
        public bool IsEditor => c.World.IsEditor;
        public void Print(string text) => c.World.PrintLine(text);
        public void WaitSeconds(double seconds) => c.WaitSeconds(seconds);
        public void WaitUntil(Func<bool> done) => c.WaitUntil(done);
        public Exception Error(string message) => c.Error(message);
        public object Native => c;
    }

    /// <summary>Plugins load here: shares the engine's own assemblies (so IBuilderPlugin is the same type) and finds the rest in the plugins folder.</summary>
    sealed class PluginLoadContext(string? folder) : AssemblyLoadContext("BuilderEngine plugins", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == null) return null;
            if (name.Name is "BuilderEngine.Plugin" or "BuilderEngine.Script") return null; // always the engine's own copy
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null; // BuilderEngine.Plugin, BuilderEngine.Script, System.*…
            if (Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name) is { } mine) return mine;
            if (folder == null) return null;
            string path = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(path) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(path))) : null;
        }
    }
}

/// <summary>Whether a project's plugins may run: the person is asked once per project and the answer is kept in the settings.</summary>
public static class PluginConsent
{
    const string AllowedKey = "pluginsAllowed", DeclinedKey = "pluginsDeclined";

    public static bool IsAllowed(AppSettings s, string projectDir) => s.GetList(AllowedKey).Any(d => AppSettings.SameFolder(d, projectDir));
    public static bool WasDeclined(AppSettings s, string projectDir) => s.GetList(DeclinedKey).Any(d => AppSettings.SameFolder(d, projectDir));
    /// <summary>Asked before (yes or no): don't ask again when the project opens.</summary>
    public static bool WasAsked(AppSettings s, string projectDir) => IsAllowed(s, projectDir) || WasDeclined(s, projectDir);

    public static bool Allow(AppSettings s, string projectDir) => Set(s, projectDir, AllowedKey, DeclinedKey);
    public static bool Decline(AppSettings s, string projectDir) => Set(s, projectDir, DeclinedKey, AllowedKey);

    static bool Set(AppSettings s, string dir, string add, string remove)
    {
        var a = s.GetList(add).Where(d => !AppSettings.SameFolder(d, dir)).Append(dir).ToList();
        var r = s.GetList(remove).Where(d => !AppSettings.SameFolder(d, dir)).ToList();
        return s.SetList(add, a) & s.SetList(remove, r);
    }
}

/// <summary>
/// What the loaded plugins add, for other parts of the editor (the blocks palette lists <see cref="BlockCommands"/>
/// under MY COMMANDS, "From plugins").
/// </summary>
public static class PluginRegistry
{
    public static IReadOnlyList<PluginCommandInfo> Commands => PluginManager.Active?.AllCommands.Where(c => !c.IsFunction).ToList() ?? [];
    public static IReadOnlyList<PluginCommandInfo> Functions => PluginManager.Active?.AllCommands.Where(c => c.IsFunction).ToList() ?? [];

    /// <summary>The plugins' commands as palette entries (a call with one field per input).</summary>
    public static List<BlockEdits.UserCommand> BlockCommands() =>
        Commands.Select(c => new BlockEdits.UserCommand(c.Name, new ProcDef(c.Name, c.Inputs.ToList(), [], 0), c.Plugin, "From plugins")).ToList();
}
