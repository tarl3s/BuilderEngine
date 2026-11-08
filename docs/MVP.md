# BuilderEngine — MVP

The smallest version where someone can **create a mechanism and make a tiny game with it**.

## Goal of the MVP

A person opens BuilderEngine, writes (or snaps together) a `Coin` mechanism, puts a few coins and a player in a scene,
presses Play and collects them. Then they save `Coin` to their library and use it in a second project.

## In scope

| Area | MVP |
|---|---|
| Language (Builder Script) | 9 keywords: `mechanism param uses when if/else repeat wait emit tell`, plus `stop`; expressions; indentation blocks; friendly errors with line numbers |
| Runtime | objects with position/size/tags, mechanisms attached to objects, events `start`, `tick`, `touched by`, `told`, `key`, signals (`when Health.died`); `wait` without freezing the game (coroutines) |
| Commands (starter library) | `print`, `destroy`, `spawn`, `play`, `move`, `set`, `goto_scene`... a small set, each also a block |
| Starter kit | Sprite, Body (gravity + solid collision), Player Move, Patrol, Health, Coin — all written in Builder Script and openable |
| Editor | mechanism library (project / my library / starter kit), code editor with live error check, scene view with drag-and-drop, inspector built from `param`s, Play window |
| Blocks | the same script shown as blocks (read-only first, then editable) — v0.2 |
| Library | mechanisms saved as `.bs` files in a personal folder, shared by every project |
| Platform | Avalonia, Windows first; runtime separated from the editor (Mac/Linux and 3D later) |

## Out of scope (later)

sharing/gallery ·
export to `.exe` · sound editor · 3D · Mac/Linux builds. (Tilemaps and lighting are done: see *Game kit* at the end.)

## Milestones

1. **Core language** — lexer, parser, interpreter, coroutines, tests. *(done)*
2. **Runtime** — world, objects, collisions, events, starter kit in Builder Script.
3. **Editor shell** — Avalonia window in the orange/black look, library panel, code editor with live error check. *(done)*
4. **Scene + Play** — place objects, attach mechanisms, play window. *(done; no physics yet)*
5. **Library** — save/load mechanisms across projects. *(done: My Library in `%APPDATA%\BuilderEngine\Library`, sections This project / My Library / Starter kit, a clash asks keep both or replace)*
6. **Blocks view** — script ↔ blocks, 1 to 1. *(done; click-to-add, drag-and-drop later)*
7. **Tutorial** — "Your first mechanism: a coin" with a self-ticking checklist.

Also done: a **debugger** (pause, step, breakpoints, live variables, an event timeline), **param types** you pick (Object, Path, List, Color, Key, Choice) and **variants** (`mechanism RedSlime from Slime`) — see the sections at the end.

Also done beyond the plan: several scenes per project (`goto_scene "Name"`, the Door mechanism), a Project menu (new / open / recent / save), and project sounds (`sounds/` folder, generated starter WAVs, import, a Sound dropdown with preview, real playback on Windows). And the engine features for cutscenes and real games: tweens, camera, dialogue and choices, HUD, screen effects, locked controls, slow motion, pictures and animation, saves (see below). And the mechanisms that
extend the editor itself: **editor tools** (a mechanism with `when tool …` edits the scene while you build) and
**C# plugins** for programmers (see below and [PLUGINS.md](PLUGINS.md)).

## Builder Script cheat sheet

```
mechanism Coin
    param value: number = 1

    when touched by "player"
        score += value
        play "coin.wav"
        destroy self
```

| Word | Meaning |
|---|---|
| `mechanism` | starts a mechanism |
| `param` | a value adjustable in the inspector (`param speed: number = 100`) |
| `uses` | use another mechanism inside this one (`uses Health max 2`) |
| `when` | when something happens (`start`, `tick`, `touched by`, `told`, `key`, `Health.died`) |
| `if` / `else` | if (`if hp <= 0: stop` on one line works too) |
| `repeat` | `repeat 3`, `repeat forever` |
| `wait` | wait seconds without freezing the game |
| `emit` | tell everyone something happened (`emit died`) |
| `tell` | send a message to another object (`tell other "damage" 1`) |
| `stop` | end this `when` right now |

### Your own commands, waiting, doing things at once

```
mechanism Boss
    uses Health max 5

    when start
        together                      # both at the same time; goes on when both are done
            slide_to 400, 1
            shake 4
        run taunt                     # starts in the background, doesn't wait
        wait for Health.died
        for each e in all("enemy")
            tell e "flee"

    to slide_to x, seconds            # a command you make: use it like any other
        move self to x, 200 in seconds
        wait seconds

    to damage_for level               # 'return' makes it usable in values: damage_for(3)
        return level * 2
```

| Write | Meaning |
|---|---|
| `to name a, b` | make a command (inside: `a`, `b` and new names are its own); call it `name 1, 2` or, with `return x`, `name(1, 2)`. The commands of mechanisms you `uses`, or of any other one, work too (`Tools.flash 2` picks one) |
| `wait until hp <= 0` | wait until it's true |
| `wait for died` / `wait for Health.died` / `wait for other.Health.died` / `wait for key "space"` | wait for a signal or a key press |
| `together` / `run` | do each line at the same time / start something without waiting |
| `for each x in list` / `while cond` | go through a list (`range(1, 10)` for numbers) / repeat while true (once per frame) |
| `[1, 2]`, `list[0]`, `add x to list`, `remove x from list` | lists: `length(l)`, `contains(l, x)`, `random_item(l)`, `join(l, ", ")`, `text(x)` |
| `nearest("enemy")`, `all("coin")`, `count("enemy")`, `find("Boss")` | find objects by tag or name; `distance(a, b)`, `has_tag(o, "x")` |

### Physics

Physics is a set of starter-kit mechanisms (`StarterKit/Physics.bs`) — open them to see how they work.

| Mechanism | What it does |
|---|---|
| `Body` | falls with `gravity` (px/s²) up to `max_fall` (px/s) and stops at solid things |
| `Solid` | bodies stand on it and can't go through it (ground, walls, crates) |
| `OneWay` | solid only from above: jump up through it, land on top |
| `Platformer` | left/right (arrows or A/D) at `speed`, jump `jump_height` px with space/up/W; uses `Body` |

A crate is `Body` + `Solid`; a walking enemy is `Patrol` + `Body`; a moving platform is `Solid` + something that moves it.

| In a script | Meaning |
|---|---|
| `body 1500, 1000` | turn on gravity and collisions (gravity, max fall speed) |
| `solid` / `solid false` / `oneway` | make this object solid / not solid / a one-way platform |
| `jump 100` | jump about 100 px high if standing (a little early or late still counts) |
| `push 300, -200` | add speed (a spring, a knockback) |
| `self.vx`, `self.vy` | speed in px/s (+ is right/down); every object moves by it, only bodies stop at solids |
| `self.on_ground` | true while the body stands on something |

Landing on or bumping into a solid counts as touching it, so `when touched by "ground"` fires when you land.
Changing `self.x` / `self.y` directly moves an object without collisions (a teleport).

### Engine commands (cutscenes, bosses, real games)

Commands that take time (a tween, a line of dialogue, a fade) pause only the `when` running them; the rest of the
game goes on. Named values come after words like `to`, `in`, `at`, `by`, `with`, `ease`; the object is optional
(no object = this object).

| Area | In a script | Meaning |
|---|---|---|
| Tweens | `move Boss to 400, 300 in 2 seconds ease out` | glide there and wait (`ease inout` default, `in`, `out`, `linear`; no `in` = instant) |
| | `move by 0, -50 in 0.5` · `resize Door to 32, 0 in 1` · `fade Ghost to 0.3 in 1` | relative move, size, opacity (0..1) |
| | `move 10, 0` | the old instant move of this object |
| Camera | `camera follow Boss` · `camera follow player` | follow an object (by default the camera follows the object tagged "player", inside the scene) |
| | `camera move to 600, 300 in 2` · `camera zoom 2 in 1` · `camera shake 6 for 0.5` | pan, zoom, shake |
| | `camera limits` · `camera limits Room` · `camera limits 0, 0, 1600, 450` · `camera limits off` · `camera reset` | where the camera may go |
| Dialogue | `say Boss "You came..."` · `say "Hi!"` · `narrate "Long ago..."` · `say Boss "Run!" for 1.5` | a line that types out and waits for space / enter / E / Z / X / a click |
| | `ask "Join me?" "Yes", "No"` | a choice (arrows + enter, a number key or a click); then `answer` is the chosen text |
| | `notify "Checkpoint!"` | a message that goes away by itself |
| Screen | `fade out in 1` · `fade in in 1` · `fade out in 1 with "white"` | fade the whole screen (waits) |
| | `flash` · `flash "red"` | a quick flash of colour |
| | `slow motion 0.3 for 2` · `slow motion off` · `pause game` · `resume game` | game speed (`for` counts real seconds); a paused game still gets keys |
| Control | `lock controls` · `unlock controls` | while locked, `key()` is false and `when key` doesn't fire; cutscene lines still run |
| HUD | `show text "Score: {score}" at 16, 16 with "#E8B04B", 24` · `hide text "Score: {score}"` | a text on the screen; `{name}` shows a variable or any value, updated every frame; `at self` = on an object |
| | `show bar hp of max at 16, 50 with "#E05D5D"` · `show bar Health.hp of Health.max at self` · `hide bar hp` · `hide hud` | a bar (on the screen, or above an object) |
| Pictures | `look "hero.png"` · `look Boss "angry.png"` · `look none` | the object's picture (from the project's `images/` folder), drawn in its box |
| | `animate "walk" at 8` · `animate "run.png" with 6` · `animate "attack" once` · `animate none` | numbered pictures (`walk1.png, walk2.png…`) or a strip of frames; `once` waits until it's done |
| | `face left` · `face right` · `face player` · `layer 2` | mirror the picture, turn towards an object, draw above lower layers |
| Saves | `save game` · `load game` · `save "coins", 12` · `saved("coins")` · `clear save` | keep the global variables and values on disk (`saves/` in the project); `loaded` tells if a load found a save |

Starter kit: `Cutscene` (an invisible zone that plays once), `Npc` (says its lines when the player walks up),
`FollowCamera`, `Label` (a HUD text), `HealthBar`, `Checkpoint` (saves and says so), `Sprite` (a picture) and
`Animator` (idle / walk / jump animations from how the object moves). The demo's Level 2 has a cutscene, `Intro`:

```
mechanism Intro
    when touched by "player"
        if played: stop
        played = true
        lock controls
        camera follow Boss
        move Boss to 1000, 340 in 2 seconds
        say Boss "You came..."
        camera shake 6 for 0.5
        fade out in 0.6
        fade in in 0.6
        camera follow player
        unlock controls
```

### Debugger

While the game plays, the right column is the **Debug** panel:

| Part | What it does |
|---|---|
| ⏸ Pause / ▶ Resume / ⏭ Step | freeze the game, let it run, or run exactly one frame (also F5 / F10 in the game) |
| Object | click an object in the game: its mechanisms and every variable, live (while paused numbers, yes/no and texts can be changed — the game uses the new value when it goes on; x and y too), and the `when`s running in it with their line: `Health · when told "damage" amount · line 18 · waiting 0.4s` |
| Globals | the global variables (editable while paused) |
| Breakpoints | click a line number in the code, or a block's ⋯ → **Pause here**: the game pauses when any object gets there (the panel says which, the object gets a red outline). They stay for the whole session |
| Events | the last 200 touches, signals, messages, keys, prints, scene changes and errors, with the time; **This object** shows only the inspected object's |

Clicking a running `when`, an error or the "Paused at" line opens that mechanism with the line marked in the code (an
arrow in the line numbers) and its block outlined. A variant's inherited code opens in the mechanism it comes from.
The runtime only reports to a debugger when one is attached (`World.Debug`); without one it costs a null check per line.

### Param types (values are picked, not typed)

```
mechanism Guard
    param target: Object                        # a dropdown of the scene's objects + ⌖ pick it in the scene
    param route: Path = [[100, 120], [260, 120]] # points: ✎ Edit in scene, click to add, drag, right-click to remove
    param loot: List of Image                   # rows with ↑ ↓ ✕ and + Add (List, List of number / text / Object / Image / Sound / Color / Key)
    param tint: Color = "#E05D5D"               # swatches
    param attack_key: Key = "space"             # click, then press the key
    param level: Choice "easy", "normal", "hard" = "normal"   # a dropdown of the options

    when start
        for each p in route
            move to p[0], p[1] in 0.5
        tell target "alarm"
```

At runtime an Object param is the object itself (saved by name, found when the scene loads — `distance(self, target)`
works), a Path is a list of `[x, y]` points in scene pixels, a List is a list, and Color / Key / Choice are texts.
Everything is saved in `project.json` (lists as JSON arrays; a list written as text, or a single value, still loads).

### Variants

`mechanism RedSlime from Slime` is Slime with changes — it has everything Slime has (params, `uses`, `when`s, `to`
commands), and Slime's changes reach it. Right-click a mechanism in the library → **Make a variant…**; the library and
the inspector show "RedSlime (from Slime)".

```
mechanism RedSlime from Slime
    param speed = 90          # no type: a new default for Slime's 'speed'
    param angry: bool = true  # a new param

    when touched by "player"  # 'when's are added: Slime's ones still run too
        tell other "damage" 2

    to greet                  # same name as one of Slime's commands: this one replaces it (also inside Slime's 'when's)
        say "GRR"
```

A variant of a variant works; a loop (`A from B`, `B from A`) or a missing parent is a friendly error (the object is
still placed, without that mechanism).
### Editor tools: mechanisms that build for you

A mechanism with `when tool …` is a **tool**: it shows up in the scene's **TOOLS** row and edits the scene while you
build (it does nothing in the game). Pick it, set its options in the row under it, then click or drag on the scene.
Everything one use does (press → drag → release) is one Ctrl+Z. Right-click a tool to open it and see how it works.

```
mechanism CoinLine
    # Click where a line of coins starts, then where it ends.
    param coin: text = "Coin"
    param gap: number = 40

    when tool click at x, y
        if not has_start
            sx = x
            sy = y
            has_start = true
            stop
        has_start = false
        n = max(1, round(distance(sx, sy, x, y) / gap))
        for each i in range(0, n)
            place coin at sx + (x - sx) * i / n, sy + (y - sy) * i / n
```

| Events | |
|---|---|
| `when tool click at x, y` | the mouse was pressed on the scene (`at pos` gives `pos` = `[x, y]`) |
| `when tool drag at x, y` / `when tool release at x, y` | moved while pressed / let go (a tool with `release` shows a box while you drag) |
| `when tool used` | the tool's button was pressed (also a **▶ Use** button in its options) |
| `when tool key "r"` | a key while it is the active tool (Esc always goes back to Select) |

| Only in tools | |
|---|---|
| `place "Coin" at x, y` · `place "Ground" at x, y with 32, 32` | a copy of the scene object called Coin (its mechanisms, params, colour, picture, tags — a "prefab"), or a new object with the mechanism Coin; then `placed` is the new object |
| `erase at x, y` · `erase obj` · `erase all("coin")` · `erase "Rock 2"` | remove the top object there / that object / a list |
| `fill_row "Ground" from x1 to x2 at y` | copies side by side |
| `select obj` · `selected` | select an object in the editor / the selected one |
| `objects_at(x, y)` · `object_at(x, y)` · `snap(x, 32)` | the objects there (top first) / the top one / the start of the 32-px cell |
| `obj.x = …`, `destroy obj`, `all("coin")`, `find("Ground")`, `print`, `notify` | the usual commands work on the scene's objects; `print` / `notify` show in the status bar |

Tool variables last between uses (CoinLine remembers the first click). The starter kit has **PlatformPainter**
(drag to paint ground tiles), **CoinLine**, **Eraser**, **Scatter** (drag a box: random things inside) and
**RoomMaker** (drag a box: walls around it).

### C# plugins

For programmers: a .NET 8 class library implementing `IBuilderPlugin` (`src/BuilderEngine.Plugin`) adds commands and
functions to Builder Script. Drop its `.dll` in the project's `plugins/` folder; BuilderEngine asks once per project
before running it ("Plugins run code on your computer — only use ones you trust"). Project → Plugins… lists them,
opens the folder and reloads; their commands also show in the blocks palette. See [PLUGINS.md](PLUGINS.md) and
`samples/HelloPlugin` (`confetti 20`, `wave(time)`).
## AI assistant

In BuilderEngine the AI **writes mechanisms and teaches** — it doesn't build whole games silently. It lives in the
**✨ AI** tab of the right column (and the **✨ Ask the AI** button of the Mechanism tab, and **✨ Fix** on the console).

| Mode | What it does |
|---|---|
| **Make** | "make me a mechanism that…" → a new mechanism in Builder Script |
| **Edit** | changes the open mechanism ("make the jump higher", "add a double jump") |
| **Explain** | explains the open mechanism, the selected lines, or answers "how do I…?" from this cheat sheet and the starter kit — never edits |
| **Fix** | sends the mechanism and the error (the console's, or the code check's) and asks for the smallest fix |

What happens to an answer: the Builder Script is pulled out of the reply (a ``` block or bare code; tabs, braces,
semicolons, `//` comments, `&&`, `on start`, `function x()`, `elif`, `while true`… are turned into Builder Script), parsed,
checked (every command, function, `uses` and `spawn` must exist — also in branches that don't run), then **run for three
seconds in a copy of the open scene** with a player walking into it, its keys pressed and its messages told. Any error →
the AI is asked **once more** with the error. Then you see a **preview** (the code, or a diff of the changes) with
**Accept** / **Discard**; only Accept changes the project (and opens the mechanism in blocks + code).

The prompt is made per request and stays small (under ~9k characters — small local models are the worst case): the
rules above, every word / command / function the engine knows (generated from the engine itself), the starter kit in
one line per mechanism, the **two starter mechanisms closest to the request** as examples, the open mechanism, the
project's mechanisms with their `to` commands, and the scene's objects with their tags. A request in Portuguese (or
another language) is first turned into English by the same AI (names, numbers and quoted text must survive, or the
original is used); explanations come back in the language of the question (or always English — a setting).

Which AI (⚙ in the panel): **Local** (Ollama on this computer — `ollama pull qwen2.5-coder:3b`; the model list comes from
`/api/tags`), **Cloud — free tiers** (Groq, Gemini, OpenRouter, Mistral in turn; a busy or used-up one hands over to the
next), or one provider with your own key (OpenAI, Claude, DeepSeek, any OpenAI-compatible URL). **Test connection**
asks for one short answer. Keys are saved only in `settings.json` (`%APPDATA%\BuilderEngine`, or the `--appdata` folder)
and hidden from the console and from `%TEMP%\builderengine_ai.log` (the last request's prompt and answer, for debugging).

Code: `src/BuilderEngine.Script/Ai*.cs` (headless: clients, settings, prompt, reply reader, checks, the assistant) and
`src/BuilderEngine/AiPanel.cs`, `AiSettingsWindow.cs`, `MainWindow.Ai.cs`; tests in `tests/BuilderEngine.Tests/AiTests.cs`
(a fake AI, no network).

## Game kit: tiles, lights, mouse, screens, items, cards

What four kinds of games need — a side-view sandbox (mine, build, craft, night monsters), a top-down mining survival
with darkness, a security-camera horror game, a card game — each as a few Builder Script words and starter-kit
mechanisms (`StarterKit/Tiles.bs`, `Lighting.bs`, `Mouse.bs`, `Ui.bs`, `Items.bs`, `Cards.bs`; open them to see how
they work). Code: `src/BuilderEngine.Script/World.Tiles.cs`, `World.Lights.cs`, `World.Mouse.cs`, `World.Screens.cs`,
`World.Inventory.cs`, `World.Cards.cs`, `World.Audio.cs`, `World.Broadphase.cs`, `TileMap.cs`, `GameEvents.Parse.cs`;
drawing in `PlaySurface.GameKit.cs` and `TileDraw.cs`; the tile painter in `MainWindow.TilePaint.cs`; tests in
`tests/BuilderEngine.Tests/GameKitTests.cs`.

### Tiles

A scene can have tile layers: a grid of cells, each holding a kind of tile (a colour or a picture; solid, background
or one-way; how hard it is to dig; what it drops; a glow). Bodies stand on solid tiles and bump into them (only the
cells near a body are checked, so a 400 × 200 map costs nothing extra), and only the cells on screen are drawn.
In the editor the **TILES** row (under TOOLS) makes a map for the scene, lists its kinds, and paints with **Brush**,
**Erase** (or the right button) and **Fill**; each stroke is one Ctrl+Z. The map is saved in `project.json` as a short
run-length text.

| In a script | Meaning |
|---|---|
| `make_map 300, 120 with 16` | a map of 300 × 120 cells of 16 px (the scene grows to hold it); `as "Back" behind` = a second layer drawn behind, not solid |
| `tile_kind "ore" with "#C0C0C0" hardness 2 drop "ore"` | a kind of tile; also `solid`, `background`, `oneway`, `unbreakable`, `picture "ore.png"`, `light 4` (cells) |
| `set_tile x, y to "dirt"` · `set_tile x, y to "none"` | change the cell at a scene point (any point inside it); `in "Back"` = another layer |
| `place_tile "stone" at x, y` | only into an empty cell, not where a body stands; then `placed` is true or false |
| `break_tile x, y` · `dig_tile x, y with 2` | break it now / dig it a frame at a time (call it every tick while the button is held: it breaks after its hardness in seconds ÷ power, with cracks on it); then `broken` and `drop` |
| `fill_tiles "stone" from x1, y1 to x2, y2` | fill a box of cells |
| `noise_tiles "none" from "stone" with 0.09, 0.6` | turn stone into nothing where smooth noise is above 0.6 (caves); `below 800` = only under y 800 |
| `random_seed 5` · `noise(x, y)` | the same seed gives the same world (and the same `random` numbers); noise is 0..1 and changes smoothly |
| `tile_at(x, y)` · `tile_solid(x, y)` · `is_tile("dirt")` · `ground_y(x)` · `tile_x(x)` · `tile_y(y)` · `tile_size()` · `map_width()` · `map_height()` | reading the map |
| `when tile broken "stone" at x, y` · `when tile placed at x, y` | on every object that has it; `kind` and `drop` are set |

Starter kit: **WorldGen** (side view: grassy hills, dirt, stone, caves, coal/iron/gold, bedrock, dark walls behind;
puts the player on the ground), **CaveGen** (top-down: rock with open caves, ores, glowing crystals, a wall around, a
floor layer, a clearing at the player), **TileMiner** (hold the left button to dig within reach — drops go into the
inventory; the right button places the selected hotbar item), **TopDown** (walk in four directions without gravity,
stopped by solid tiles).

### Darkness and lights

| In a script | Meaning |
|---|---|
| `darkness 0.9` · `darkness 0.3 in 5` | how dark it is where no light shines (0 none, 1 black) |
| `light 140 with "#FFD27A", 0.3` · `light off` · `light Torch 80` | a soft round light around an object (radius, colour, flicker 0..1) |
| `sunlight 1` · `sunlight 0.2 in 10` · `sunlight off` | tiles open to the sky are lit and the light reaches a few cells into caves (less through solid ground); glowing tiles light their cells too |
| `light_at(x, y)` | 0..1, how lit a point is |

The game draws a small light map over the view (smooth, tinted near coloured lights), under the screen objects and the
HUD. Starter kit: **Light**, **Darkness**, **DayNight** (sunlight goes down and up; everyone is told "night" and "day").

### The mouse

| In a script | Meaning |
|---|---|
| `mouse_x`, `mouse_y` · `mouse_screen_x`, `mouse_screen_y` | the pointer in scene pixels (camera position and zoom included) / in screen pixels |
| `mouse_down()` · `mouse_down("right")` · `mouse_over()` · `mouse_over(Boss)` · `mouse_over_ui()` · `under_mouse()` | |
| `when clicked` | this object was clicked (only the top one under the pointer: screen objects first, then the higher layer, then the later one) |
| `when hovered` · `when unhovered` | the pointer came over it / left it (a hand cursor shows over clickable things) |
| `when mouse pressed at x, y` · `when mouse released "right" at x, y` | anywhere |
| `draggable` · `draggable to "slot"` · `draggable off` | the mouse can pick it up; with a tag it snaps into things with that tag and glides back when dropped anywhere else |
| `when picked up` · `when dropped` · `when dropped on "slot"` (`other` = the slot) · `when received "card"` (on the slot) | |
| `cursor "hand"` · `"cross"` · `"none"` · `"move"` · `cursor default` | the pointer's look |

**Screen objects**: an object with the tag `ui` (or `ui` in a script) is drawn on the screen like the HUD — the camera
doesn't move it — and is clicked in screen pixels; it never touches scene objects. Locked controls ignore the mouse.
Starter kit: **Draggable**, **Clickable**, **HoverGlow**.

### Screens and buttons

| In a script | Meaning |
|---|---|
| `color "#E05D5D"` · `caption "Play" with "#111111", 20` · `caption "HP {hp}"` · `caption none` | the box's colour, a text in its middle (`{name}` updates by itself) |
| `hide` · `hide Door` · `hide all("ghost")` · `show` | not drawn and not clicked (it still moves and touches); scripts read and change `self.hidden`, `self.color`, `self.caption`, `self.layer`, `self.opacity`, and read `self.hovered` |
| `join screen "Menu"` · `show screen "Menu"` · `hide screen "Menu"` · `toggle screen "Menu"` · `hide screen` · `screen_shown("Menu")` | a screen is a group of objects shown together (they start hidden until the screen is shown) |
| `broadcast "play"` | every object's `when told "play"` runs |
| `make Button at 300, 200 with 200, 48 as "Play"` | a new object; then `made` is it (`made.Button.text = "Play"` before it starts) |
| `tag self "in_hand"` · `untag self "in_hand"` | |

Starter kit: **Button** (text, colour, hover colour; a click tells its target — or everyone — its message and value),
**Panel** (a box with a title, maybe part of a screen), **Menu** (one button per option).

### Inventory, hotbar and crafting

One inventory for the whole game: it goes on to the next scene and into `save game`.

| In a script | Meaning |
|---|---|
| `give "wood", 3` · `take "wood", 2` (then `taken`) · `clear inventory` | |
| `has("wood", 2)` · `count_item("wood")` · `items()` | |
| `item "wood" with "#8B5A2B"` · `item "sword" with "sword.png"` | how an item looks in the hotbar (a tile's drop looks like the tile) |
| `show hotbar` · `show hotbar 5 at 200, 400` · `hide hotbar` · `select_slot 3` · `selected_item()` · `selected_slot()` | the first slots of the inventory; keys 1-9 and the mouse wheel pick one |
| `recipe "torch" from "wood", 1, "coal", 1 as 4` · `craft "torch"` (then `crafted`) · `can_craft("torch")` · `recipes()` · `recipe_text("torch")` | |

Starter kit: **Pickup**, **Hotbar**, **Recipe** (one recipe, every part picked in the inspector), **Crafting** (C opens
a menu with a button per recipe, dim when something is missing; click to craft).

### Security cameras, static, sounds

| In a script | Meaning |
|---|---|
| `camera view "Cam 3"` · `camera view off` · `camera_view()` | the camera jumps to that object and zooms so its box fills the view |
| `on_screen()` · `on_screen(Ghost)` | is it inside what the camera shows (for things that move only when nobody watches) |
| `screen noise 0.3` · `screen noise 0.9 for 0.2` · `screen noise off` · `screen tint "#103018", 0.3` | static and a colour over the view (under the screen objects) |
| `play "rain.wav" loop` · `play "music.mp3" loop with 0.4` · `stop sound "rain.wav"` · `stop sounds` · `volume 0.5` · `volume "rain.wav" 0.2` · `playing("rain.wav")` | looping sounds and volume |
| `when key "1"` | the number keys (also on the numpad) |

Starter kit: **CameraPoint** (a box a camera looks at), **CameraMonitor** (Tab opens the cameras, 1-6 switch, with
static; the screen "Cameras" shows while watching; everyone is told "cameras on" / "cameras off").

### Cards and turns

| In a script | Meaning |
|---|---|
| `shuffle deck` · `pop(deck)` · `pop(deck, 0)` | mix a list; take the last (or first) item out |
| `arrange all("in_hand") in row at 400, 380 gap 10 over 0.3` | lay objects out centred on x, y: `row`, `fan`, `column`, `grid … columns 4`, `stack`; `over` glides there and waits |

Starter kit: **Card** (front picture or title + value, back colour or picture; tell it "flip"; with a zone it can be
dragged onto slots and then leaves the hand), **Deck** (shuffled; tell it "draw" 3 or click it; cards go to a row
tagged `in_hand`), **TurnManager** ("player" / "enemy"; tell it "end turn"; everyone is told "player turn" / "enemy
turn" with the round).

### Performance

The game loop handles ~2000 objects and a 400 × 200 tile map well inside a 60 fps frame (the test measures about
3 ms per frame): each mechanism's `when`s are sorted by event once; a waiting `when tick` is marked on its mechanism
instead of searching every running `when`; touches come from a grid (only objects with `when touched` look for
neighbours — every object while the debugger watches — and they still fire in the old order); bodies ask a grid for
the solids near them and the tile map for the cells near them; drawing skips objects and cells off the screen.
