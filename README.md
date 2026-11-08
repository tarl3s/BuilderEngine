# BuilderEngine

A game engine where you build your own **mechanisms** — reusable pieces for anything (behaviors, entities, systems,
lights, editor tools) — with blocks or with **Builder Script**, a small language you can start with 9 words. The more mechanisms you
make, the faster you make games.

**[⬇ Download for Windows](https://github.com/tarl3s/BuilderEngine/releases/latest/download/BuilderEngine-win-x64.zip)** — unzip and run `BuilderEngine.exe` (no install, no .NET needed). It updates itself when a new version comes out, and the four sample games (a sandbox, a cave survival, a camera horror and a card battler) come with it.

Status: early version (v0.1.0). See [docs/MVP.md](docs/MVP.md). MIT license.

What works today:
- **Scene**: drag mechanisms from the library onto the scene, move/resize objects (snap, undo/redo), inspector with a field per `param`, Play inside the editor (Esc to stop).
- **Editing conveniences**: a selection box and Shift/Ctrl+click select several objects (move, nudge with the arrow keys, align, delete, duplicate them together), Ctrl+C / Ctrl+V (paste at the mouse, also into another scene), Ctrl+A, F2 rename, 🔒 lock (a locked object can't be clicked or moved), draw order (Ctrl+[ / Ctrl+]), Shift keeps the proportions and "Original size" for pictures, a right-click menu, Ctrl+wheel zoom at the mouse, the wheel steps a number field you clicked into, typing searches an open dropdown or menu, F5 plays. F1 (or **?**) lists every shortcut: [docs/SHORTCUTS.md](docs/SHORTCUTS.md).
- **Scenes**: a project has several scenes — pick one in the scene toolbar, add, duplicate, rename, reorder, delete (the first one is where the game starts). Play plays the open scene; a script changes scene with `goto_scene "Level 2"` (or `scene "Level 2"`), and the starter kit's **Door** does it on touch. Global variables (the score) carry over.
- **Mechanisms**: edit as blocks, as code, or both side by side — they stay 1:1. Value fields in blocks are edited inline; the palette adds blocks to the selected place.
- **Your own commands**: `to slide_to x, seconds` makes a command you then use like a built-in one (`slide_to 400, 1`, or as a value with `return`). The palette's **MY COMMANDS** lists yours, the ones of the mechanisms you use and the whole project's. Also `wait until` / `wait for Health.died` / `wait for key "space"`, `together` (at the same time), `run` (in the background), `for each`, `while`, lists (`[1, 2]`, `add x to items`) and `nearest("enemy")` / `all("coin")` / `count(...)`.
- **Library**: three sections — *This project*, *My Library* and *Starter kit*. Right-click a mechanism (or "Save to My Library" in the Mechanism tab) to keep it in your personal library (`%APPDATA%\BuilderEngine\Library\*.bs`, shared by every project); clicking or dragging one copies it into the project. Same name, different mechanism? It asks: keep both (`Coin2`) or replace.
- **Sounds**: each project has a `sounds/` folder (new projects start with generated coin, jump, hit and powerup WAVs). Import wav/mp3/ogg, listen with ▶, and pick them in `Sound` params from a dropdown. Games really play them (`play "coin.wav"`; Windows for now).
- **Cutscenes and game feel**: tweens (`move Boss to 400, 300 in 2 seconds ease out`, `resize`, `fade`), a camera that follows the player inside the scene (`camera follow / move / zoom / shake / limits`), dialogue (`say Boss "You came..."`, `ask "Join me?" "Yes", "No"`, `notify`), screen fades and flashes, `slow motion`, `pause game`, `lock controls`. Waiting commands pause only their own `when`. The demo's Level 2 plays a cutscene; an Npc talks in Level 1.
- **HUD**: `show text "Score: {score}" at 16, 16` (`{name}` is filled in every frame), `show bar hp of max`, and the starter kit's **Label** and **HealthBar**.
- **Pictures**: each project has an `images/` folder. An object's **Picture** (and `Image` params) is a dropdown with thumbnails and "Import picture…"; the scene and the game draw it. Scripts use `look "hero.png"`, `animate "walk"` (walk1.png, walk2.png… or a strip of frames), `face left`, `layer 2`; the **Sprite** and **Animator** mechanisms do it for you.
- **Prefabs**: select an object → **★ Save as prefab…**; the library's **PREFABS** section lists them — drag one onto the scene to place a copy, double-click to edit the prefab (its copies follow, except what a copy changed itself). A copy with **Changes here change every copy** ticked passes its changes to the prefab and the other copies; **Revert to prefab** drops a copy's own changes. Scripts spawn them by name: `spawn "Slime"` (a prefab is found before a mechanism with that name).
- **Animations and pixel art**: each object has named animations (idle, walk…) made in the **Animations** window — frames from the project's pictures, files, a sprite sheet cut by columns × rows, or drawn — with speed, loop, a preview and a default one that plays when the game starts; `animate "walk"` and the **Animator** play them. "✎ Draw…" in any picture dropdown opens a **sprite editor** (pencil, eraser, fill, colour picker, palette, zoom, undo) that saves PNGs in `images/`.
- **Saves**: `save game` / `load game` (global variables), `save "coins", 12` and `saved("coins")`, in the project's `saves/` folder.
- **Debugger**: while playing, the right column is a Debug panel — pause, resume, step one frame (F5 / F10); click an object in the game to see its variables live (change them while paused) and the `when`s running in it with their line; globals; the last 200 events (touches, signals, messages, keys, scene changes, errors), filterable by object. Click a line number in the code (or a block's ⋯ → Pause here) to set a breakpoint; clicking a running `when` or an error opens the mechanism at that line, in the code and the blocks.
- **Picked values**: `param target: Object` (a dropdown of the scene's objects, or pick it in the scene), `param route: Path` (points you click and drag on the scene; `for each p in route` + `move to p[0], p[1] in 1`), `param items: List of number` (rows to add, reorder and remove — also of text, Object, Image, Sound, Color, Key), `param tint: Color` (swatches), `param jump: Key` (press a key), `param level: Choice "easy", "normal", "hard"` (a dropdown).
- **Variants**: `mechanism RedSlime from Slime` has everything Slime has and follows its changes; `param speed = 90` changes a default, new `when`s are added, a `to` with the same name replaces Slime's command. Right-click a mechanism → Make a variant…
- **Editor tools**: a mechanism with `when tool click at x, y` (or `drag`, `release`, `used`, `key`) is a tool — it shows in the scene's **TOOLS** row and edits the scene while you build: `place "Coin" at x, y` (a copy of a scene object, or a new object with that mechanism), `erase at x, y`, `fill_row "Ground" from 0 to 800 at 400`, `select obj`, `objects_at(x, y)`, `snap(x, 32)`. Its params show in an options row; each use is one Ctrl+Z. The starter kit has PlatformPainter, CoinLine, Eraser, Scatter and RoomMaker — every tool you make speeds up every game after it.
- **C# plugins** (for programmers): a .NET 8 class library implementing `IBuilderPlugin` (`src/BuilderEngine.Plugin`) adds commands and functions to Builder Script. Put the `.dll` in the project's `plugins/` folder; BuilderEngine asks once per project before running plugins, and Project → Plugins… lists, reloads and opens the folder. See [docs/PLUGINS.md](docs/PLUGINS.md) and `samples/HelloPlugin`.
- **Projects**: a folder with `project.json` + `mechanisms/*.bs` + `sounds/`. The **Project** menu makes new projects (in `Documents\BuilderEngine Projects`), opens folders, lists recent ones and saves; switching saves first. Settings live in `%APPDATA%\BuilderEngine\settings.json`. `--project <folder>` opens another one without touching the settings; `--appdata <folder>` moves settings and My Library (dev/test runs).
- **Game kit** (for sandboxes, mining survival, horror and card games — see [docs/MVP.md](docs/MVP.md#game-kit-tiles-lights-mouse-screens-items-cards)): **tile maps** painted in the editor's TILES row (brush, erase, fill) or made by scripts (`make_map 300, 120 with 16`, `set_tile x, y to "dirt"`, `dig_tile mouse_x, mouse_y`, `noise(x, y)`, `random_seed 5`; bodies stand on solid tiles); **darkness and lights** (`darkness 0.9`, `light 140 with "#FFD27A", 0.3`, `sunlight 1` lights tiles open to the sky); **the mouse** (`mouse_x`, `when clicked`, `when hovered`, `when mouse pressed at x, y`, `draggable to "slot"` + `when dropped on "slot"`, `cursor "hand"`, objects tagged `ui` drawn on the screen); **screens and buttons** (`caption "Play"`, `show screen "Menu"`, `broadcast "play"`, `make Button at x, y`); **inventory and crafting** (`give "wood", 3`, `has("wood", 2)`, `show hotbar`, `recipe "torch" from "wood", 1, "coal", 1 as 4`, `craft "torch"`); **security cameras** (`camera view "Cam 2"`, `screen noise 0.3`, `on_screen(Ghost)`, `play "rain.wav" loop`, `hide` / `show`); **cards** (`shuffle deck`, `pop(deck)`, `arrange all("in_hand") in row at 400, 380 gap 10`, records like `{name: "Imp", cost: 1}`, `label "cost" "{card.cost}" at top left` badges, `outline`; the sample **Ember Duel** is a full card battler). Starter mechanisms: WorldGen, CaveGen, TileMiner, TopDown, Light, Darkness, DayNight, Draggable, Clickable, HoverGlow, Button, Panel, Menu, CameraPoint, CameraMonitor, Pickup, Hotbar, Recipe, Crafting, Card, Deck, TurnManager. The game loop handles ~2000 objects and a 400 × 200 tile map at 60 fps.
- **AI assistant** (the **✨ AI** tab next to the Inspector): it writes mechanisms and teaches — it never builds your game behind your back. **Make** ("make me a mechanism that…"), **Edit** the open mechanism ("make the jump higher"), **Explain** it (or the selected lines, or "how do I…?") and **Fix** an error (the console's **✨ Fix** button). What the AI writes is parsed and tested a few seconds in your scene first (one automatic retry with the error), then shown as a preview — a diff for changes — and only **Accept** puts it in the project. Works with a local model (Ollama, e.g. `qwen2.5-coder:3b`) or free cloud tiers (Groq, Gemini, OpenRouter, Mistral) and your own keys (OpenAI, Claude, DeepSeek, any OpenAI-compatible URL), set in ⚙. Requests in Portuguese (or other languages) work; explanations come back in your language. Keys stay in `settings.json`, never in projects or logs.

```
mechanism Coin
    param value: number = 1

    when touched by "player"
        score += value
        play "coin.wav"
        destroy self
```

## Build and run

```bash
dotnet build
dotnet run --project src/BuilderEngine              # editor
dotnet run --project tests/BuilderEngine.Tests      # language/runtime tests
```

| Project | What |
|---|---|
| `src/BuilderEngine.Script` | the language and runtime (lexer, parser, interpreter with `wait` coroutines, starter kit) and the engine features (camera, tweens, dialogue, HUD, pictures, saves) — no UI, reusable by any host |
| `src/BuilderEngine.Script` (also) | editor tools (`EditorTools.cs`: tool events run on a copy of the scene, edits go back as one undo step) and the plugin loader (`Plugins.cs`) |
| `src/BuilderEngine.Plugin` | the plugin SDK: `IBuilderPlugin`, `IPluginHost`, `IPluginCommand`, `IPluginObject` (no dependencies) |
| `src/BuilderEngine` | the editor (Avalonia: Windows now, Mac/Linux later) |
| `samples/HelloPlugin` | a sample plugin (`confetti 20`, `wave(time)`) |
| `tests/BuilderEngine.Tests` | test runner, no NuGet packages |
