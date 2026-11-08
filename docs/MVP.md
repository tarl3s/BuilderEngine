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
| Commands (starter library) | `print`, `destroy`, `spawn`, `play`, `move`, `set`... a small set, each also a block |
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
5. **Library** — save/load mechanisms across projects.
6. **Blocks view** — script ↔ blocks, 1 to 1. *(done; click-to-add, drag-and-drop later)*
7. **Tutorial** — "Your first mechanism: a coin" with a self-ticking checklist.

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
