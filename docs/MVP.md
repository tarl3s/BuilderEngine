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

AI tutor and "make me a mechanism" · editable blocks with drag and drop · editor-tool mechanisms · sharing/gallery ·
export to `.exe` · sound editor · tilemaps · lighting · 3D · Mac/Linux builds.

## Milestones

1. **Core language** — lexer, parser, interpreter, coroutines, tests. *(done)*
2. **Runtime** — world, objects, collisions, events, starter kit in Builder Script.
3. **Editor shell** — Avalonia window in the orange/black look, library panel, code editor with live error check. *(done)*
4. **Scene + Play** — place objects, attach mechanisms, play window. *(done; no physics yet)*
5. **Library** — save/load mechanisms across projects. *(done: My Library in `%APPDATA%\BuilderEngine\Library`, sections This project / My Library / Starter kit, a clash asks keep both or replace)*
6. **Blocks view** — script ↔ blocks, 1 to 1. *(done; click-to-add, drag-and-drop later)*
7. **Tutorial** — "Your first mechanism: a coin" with a self-ticking checklist.

Also done beyond the plan: several scenes per project (`goto_scene "Name"`, the Door mechanism), a Project menu (new / open / recent / save), and project sounds (`sounds/` folder, generated starter WAVs, import, a Sound dropdown with preview, real playback on Windows). And the engine features for cutscenes and real games: tweens, camera, dialogue and choices, HUD, screen effects, locked controls, slow motion, pictures and animation, saves (see below).

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
