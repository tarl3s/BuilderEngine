# BuilderEngine

A game engine where you build your own **mechanisms** — reusable pieces for anything (behaviors, entities, systems,
lights, editor tools) — with blocks or with **Builder Script**, a small language you can start with 9 words. The more mechanisms you
make, the faster you make games.

Status: early prototype (v0.3.0). See [docs/MVP.md](docs/MVP.md).

What works today:
- **Scene**: drag mechanisms from the library onto the scene, move/resize objects (snap, undo/redo), inspector with a field per `param`, Play inside the editor (Esc to stop).
- **Scenes**: a project has several scenes — pick one in the scene toolbar, add, duplicate, rename, reorder, delete (the first one is where the game starts). Play plays the open scene; a script changes scene with `goto_scene "Level 2"` (or `scene "Level 2"`), and the starter kit's **Door** does it on touch. Global variables (the score) carry over.
- **Mechanisms**: edit as blocks, as code, or both side by side — they stay 1:1. Value fields in blocks are edited inline; the palette adds blocks to the selected place.
- **Your own commands**: `to slide_to x, seconds` makes a command you then use like a built-in one (`slide_to 400, 1`, or as a value with `return`). The palette's **MY COMMANDS** lists yours, the ones of the mechanisms you use and the whole project's. Also `wait until` / `wait for Health.died` / `wait for key "space"`, `together` (at the same time), `run` (in the background), `for each`, `while`, lists (`[1, 2]`, `add x to items`) and `nearest("enemy")` / `all("coin")` / `count(...)`.
- **Library**: three sections — *This project*, *My Library* and *Starter kit*. Right-click a mechanism (or "Save to My Library" in the Mechanism tab) to keep it in your personal library (`%APPDATA%\BuilderEngine\Library\*.bs`, shared by every project); clicking or dragging one copies it into the project. Same name, different mechanism? It asks: keep both (`Coin2`) or replace.
- **Sounds**: each project has a `sounds/` folder (new projects start with generated coin, jump, hit and powerup WAVs). Import wav/mp3/ogg, listen with ▶, and pick them in `Sound` params from a dropdown. Games really play them (`play "coin.wav"`; Windows for now).
- **Projects**: a folder with `project.json` + `mechanisms/*.bs` + `sounds/`. The **Project** menu makes new projects (in `Documents\BuilderEngine Projects`), opens folders, lists recent ones and saves; switching saves first. Settings live in `%APPDATA%\BuilderEngine\settings.json`. `--project <folder>` opens another one without touching the settings; `--appdata <folder>` moves settings and My Library (dev/test runs).

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
| `src/BuilderEngine.Script` | the language and runtime (lexer, parser, interpreter with `wait` coroutines, starter kit) — no UI, reusable by any host |
| `src/BuilderEngine` | the editor (Avalonia: Windows now, Mac/Linux later) |
| `tests/BuilderEngine.Tests` | test runner, no NuGet packages |
