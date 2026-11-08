# C# plugins

Builder Script covers most things. When you need more — a file format, a maths library, a web call, a big
algorithm — a **plugin** written in C# can add new commands and functions that every mechanism can use.

> Plugins run code on your computer. BuilderEngine asks before it turns a project's plugins on (once per project,
> remembered in the settings) — only use plugins you trust.

## Use a plugin

1. Put the plugin's `.dll` in the project's `plugins/` folder (Project → Plugins… → *Open plugins folder*).
2. Open the project (or press *Reload* in the Plugins window). The first time, BuilderEngine asks whether the
   project's plugins may run; *Turn plugins on* remembers the answer for that project.
3. Project → Plugins… lists every loaded plugin with its commands and functions. They also show in the blocks
   palette under **MY COMMANDS → From plugins**.

If a plugin is broken (not a .NET dll, its code throws), the Plugins window and the console say so — the editor and
the game keep running. A command that fails while the game runs is an ordinary script error on that line.

## Make a plugin

A plugin is a .NET 8 class library with a public class that implements `IBuilderPlugin`
(from `BuilderEngine.Plugin.dll`, which sits next to `BuilderEngine.exe`).

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="BuilderEngine.Plugin">
      <HintPath>path\to\BuilderEngine.Plugin.dll</HintPath>
      <Private>false</Private> <!-- BuilderEngine has its own copy -->
    </Reference>
  </ItemGroup>
</Project>
```

```csharp
using BuilderEngine.Plugin;

public sealed class Hello : IBuilderPlugin
{
    public string Name => "Hello";
    public string Description => "confetti and wave(t)";

    public void Register(IPluginHost host)
    {
        // A function: used in values, like  self.y = 200 + wave(time) * 20
        host.AddFunction("wave", args => Math.Sin((double)args[0]! * Math.PI * 2), "a smooth -1..1 wave", "time");

        // A command: used on its own line, like  confetti 30
        host.AddCommand("confetti", c =>
        {
            int amount = (int)c.Number(c.Arg(0));
            for (int i = 0; i < amount; i++)
            {
                var piece = c.Spawn("Confetti", c.Self.X, c.Self.Y, "Body");
                piece.Vx = Random.Shared.Next(-200, 200);
                piece.Vy = -Random.Shared.Next(250, 500);
            }
        }, "throws little pieces up", "amount");
    }
}
```

The full sample is `samples/HelloPlugin` (build it with `dotnet build samples/HelloPlugin -c Release` and copy
`HelloPlugin.dll` into a project's `plugins/` folder).

### What you get

| In `Register(host)` | |
|---|---|
| `host.AddCommand(name, run, help, inputs…)` | a command (`name` may have several words: `"sky color"`) |
| `host.AddFunction(name, run, help, inputs…)` | a function for values; gets `List<object?>`, returns a value |
| `host.IsEditor` | true while an **editor tool** runs (the scene is being edited, not played) |
| `host.ProjectFolder`, `host.Log(text)` | the project folder; a line in the editor's console |

| In a command (`IPluginCommand c`) | |
|---|---|
| `c.Args`, `c.Arg(i)`, `c.NamedArg("at", 1)`, `c.Has("with")` | the values written after the command (named ones after `to in at by with from ease over as then`) |
| `c.Number(v)`, `c.Text(v)`, `throw c.Error("…")` | conversions; a friendly error on the script's line |
| `c.Self`, `c.Other`, `c.Find("Boss")`, `c.Objects("enemy")`, `c.Spawn(…)` | objects (`X Y Width Height Vx Vy Tags Destroy()`) |
| `c.Globals`, `c.Time`, `c.Print(text)` | global variables (`score`), game time, the console |
| `c.WaitSeconds(s)`, `c.WaitUntil(() => …)` | make the line take time (only its own `when` waits) |

Values are what Builder Script uses: `double` for numbers (an `int` you return becomes one), `string`, `bool`,
`List<object?>`, objects (`IPluginObject`) and `null`.

Programmers who want everything can also reference `BuilderEngine.Script.dll`: `host.Native` is the
`World` and `c.Native` the `CommandContext`.

### Rules

- A plugin can't replace the engine's own commands and functions (`print`, `move`, `random`…); those names are
  refused with a message in the Plugins window.
- `Register` runs once when the plugin loads (to list what it adds) and again for every world: each game, each scene
  it goes to, each editor tool. Keep it quick.
- Plugins load from memory (the dll isn't locked): rebuild it, then press *Reload*.
