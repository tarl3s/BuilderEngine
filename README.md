# BuilderEngine

A game engine where you build your own **mechanisms** — reusable pieces for anything (behaviors, entities, systems,
lights, editor tools) — with blocks or with **Builder Script**, a tiny language of 9 keywords. The more mechanisms you
make, the faster you make games.

Status: early prototype (v0.3.0). See [docs/MVP.md](docs/MVP.md).

What works today:
- **Scene**: drag mechanisms from the library onto the scene, move/resize objects (snap, undo/redo), inspector with a field per `param`, Play inside the editor (Esc to stop).
- **Scenes**: a project has several scenes — pick one in the scene toolbar, add, duplicate, rename, reorder, delete (the first one is where the game starts). Play plays the open scene; a script changes scene with `goto_scene "Level 2"` (or `scene "Level 2"`), and the starter kit's **Door** does it on touch. Global variables (the score) carry over.
- **Mechanisms**: edit as blocks, as code, or both side by side — they stay 1:1. Value fields in blocks are edited inline; the palette adds blocks to the selected place.
- **Library**: three sections — *This project*, *My Library* and *Starter kit*. Right-click a mechanism (or "Save to My Library" in the Mechanism tab) to keep it in your personal library (`%APPDATA%\BuilderEngine\Library\*.bs`, shared by every project); clicking or dragging one copies it into the project. Same name, different mechanism? It asks: keep both (`Coin2`) or replace.
- **Sounds**: each project has a `sounds/` folder (new projects start with generated coin, jump, hit and powerup WAVs). Import wav/mp3/ogg, listen with ▶, and pick them in `Sound` params from a dropdown. Games really play them (`play "coin.wav"`; Windows for now).
- **Cutscenes and game feel**: tweens (`move Boss to 400, 300 in 2 seconds ease out`, `resize`, `fade`), a camera that follows the player inside the scene (`camera follow / move / zoom / shake / limits`), dialogue (`say Boss "You came..."`, `ask "Join me?" "Yes", "No"`, `notify`), screen fades and flashes, `slow motion`, `pause game`, `lock controls`. Waiting commands pause only their own `when`. The demo's Level 2 plays a cutscene; an Npc talks in Level 1.
- **HUD**: `show text "Score: {score}" at 16, 16` (`{name}` is filled in every frame), `show bar hp of max`, and the starter kit's **Label** and **HealthBar**.
- **Pictures**: each project has an `images/` folder. An object's **Picture** (and `Image` params) is a dropdown with thumbnails and "Import picture…"; the scene and the game draw it. Scripts use `look "hero.png"`, `animate "walk"` (walk1.png, walk2.png… or a strip of frames), `face left`, `layer 2`; the **Sprite** and **Animator** mechanisms do it for you.
- **Saves**: `save game` / `load game` (global variables), `save "coins", 12` and `saved("coins")`, in the project's `saves/` folder.
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
| `src/BuilderEngine.Script` | the language and runtime (lexer, parser, interpreter with `wait` coroutines, starter kit) and the engine features (camera, tweens, dialogue, HUD, pictures, saves) — no UI, reusable by any host |
| `src/BuilderEngine` | the editor (Avalonia: Windows now, Mac/Linux later) |
| `tests/BuilderEngine.Tests` | test runner, no NuGet packages |
