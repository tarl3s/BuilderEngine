# EasyEngine ⇄ BuilderEngine link (contract, version 1)

Both engines can open each other's projects and edit **the same project at the same time**; each one sees the other's
changes live. This file is the contract both codebases follow. When it changes, bump `linkVersion` and update both.

## The one converter

There is exactly one converter, `BuilderEngine.Script.EasyLink` (in BuilderEngine's UI-free library, files
`src/BuilderEngine.Script/EasyLink.cs` and `EasyLink.Writer.cs`). It works on raw JSON (`System.Text.Json.Nodes`), so
it doesn't need EasyEngine's classes:

| Call | Used by |
|---|---|
| `EasyLink.FromEeproj(string eeprojJson, string folder) → Project` | BuilderEngine opening a `.eeproj` |
| `EasyLink.ToEeproj(Project, string? previousEeprojJson) → string` | BuilderEngine saving into a `.eeproj` (merges into the previous JSON so every EasyEngine field it doesn't map is kept byte-for-byte where possible) |
| `EasyLink.ToEeprojFromFolder(string beFolder) → string` | EasyEngine opening a BuilderEngine project folder (`project.json`) |
| `EasyLink.SaveFromEeproj(string eeprojJson, string beFolder)` | EasyEngine saving into a BuilderEngine project folder |

`ToEeproj(Project, string?, string? folder)` is the same as the two-argument call, plus the project folder so a picture
picked by its bare name is written with the folder it really is in (see *Pictures and sounds*). Helpers both engines
may use: `FindEeproj(folder)`, `IsEasyFolder(folder)`, `IsProjectFolder(folder)`, `ProjectFile(folder)`,
`EasyBehaviors(obj)` / `HasEasyBehaviors(obj)`, `FixIds(project)`, `WriteAtomic(path, text)`, `SameJson(a, b)`.
`FromEeproj` throws `JsonException` when the text isn't a JSON object.

EasyEngine references `BuilderEngine.Script.dll` (+ `BuilderEngine.Plugin.dll`) as a **binary reference** copied into
`engine/lib/BuilderEngine/` by `engine/tools/Update-BuilderEngineLib.ps1` (which runs this repo's `tools/Build-LinkLib.ps1`:
Release build of `BuilderEngine.Script` + `BuilderEngine.Plugin`, DLLs copied to the target folder), so the EasyEngine repo still builds on its
own (GitHub, other PCs). Rebuild + recopy when the link or the runtime changes.

## Common fields (both engines edit them)

| BuilderEngine (`project.json`) | EasyEngine (`.eeproj`) |
|---|---|
| `Project.Name` | `Name` |
| `Scene.Name / Width / Height / Background` | `Scene.Name / Width / Height / BackgroundColor` |
| `SceneObject.Id / Name / X / Y / Width / Height` | `GameObjectData.Id / Name / X / Y / Width / Height` |
| `SceneObject.Color` | `Color` |
| `SceneObject.Image` (path relative to the project folder) | `SpritePath` (path relative to the project folder) |
| `SceneObject.Locked`, `SceneObject.KeepAspect` | `Locked`, `KeepAspect` |
| object order in the scene (draw order) | `Z` inside its layer (and the layer's list order) |
| `SceneObject.Prefab / PrefabAll / Animations / DefaultAnimation` | the same names |

The last row is mapped only when BuilderEngine's `SceneObject` has those properties (the converter checks once, by
reflection); until then they simply stay in the object's `easy` block. Animations are `{Name, Frames, Fps, Loop}` in
EasyEngine and the same in camelCase in `project.json`; a BuilderEngine object with no animations is written as
`"Animations": []`, never `null`.

Project prefabs map both ways: BuilderEngine `Project.Prefabs` (`Prefab{Name, Icon, Category, Template}`, `prefabs` in
`project.json`) ⇄ EasyEngine `Prefabs` (`PrefabDef{Name, Icon, Category, Template}`). A template goes through the same
object mapping as a scene object (its EasyEngine data in the template's `easy` block, its mechanisms and tags in the
template's `Builder` block; no layer). Saving merges by prefab name: an unchanged prefab keeps its exact JSON, one deleted
in BuilderEngine is removed, a new one is added at the end; prefabs BuilderEngine can't read (no name, a repeated name)
stay as they were. Files from the first link version kept BuilderEngine's prefabs in `Builder.extra.prefabs`: they are
read and moved to `Prefabs` on the next save.

Colours are `#RRGGBB` (or `#AARRGGBB`); pictures keep their relative path (`images/x.png` or `Assets\sprites\x.png`):
each engine resolves it from the project folder, so both see the same file.

## What each engine keeps for the other

- **In a `.eeproj`**, BuilderEngine's data lives in a `"Builder"` property, written only when there is something in it:
  - on the project: `{"linkVersion":1, "mechanisms": {name: source}, "extra": {...}}` (the project's own mechanisms
    and any BuilderEngine-only project data);
  - on a scene: `{"extra": {...}}` (BuilderEngine-only scene data; its tile kinds when needed, see *Tilemaps*);
  - on an object: `{"mechanisms":[{"name":"Coin","params":{...}}], "tags":[...], "extra": {...}}`.
  `extra` is everything of BuilderEngine's JSON at that level that isn't a common field; it comes back as is.
- **In a `project.json`**, EasyEngine's data lives in an `"easy"` property: on the project (everything not mapped:
  prefabs, theme, variables…), on each scene (layers with their settings but without their objects, scene variables…)
  and on each object (the whole EasyEngine object minus the common fields: behaviors, physics, variables, its layer
  index as `easy.layer`, and its `Z` as it was when read — only a hint, see *Draw order*).
- EasyEngine must keep unknown properties when it loads and saves (`[JsonExtensionData]` on `Project`, `Scene`,
  `Layer`, `GameObjectData`), so the `"Builder"` data survives EasyEngine saving the file.

## Saving merges into the previous file

`ToEeproj(project, previous)` starts from the previous file and changes only what changed: a common field is written
only when its value differs from what BuilderEngine read from that file, layers and objects that didn't change keep
their exact JSON, and an unchanged project comes back **as the same text** (EasyEngine writes with System.Text.Json,
indented, default escaping — so does the converter). EasyEngine data changed in the file after BuilderEngine read it
(a behavior's parameter, a variable) survives a BuilderEngine save. Objects BuilderEngine deleted are removed; objects
it added are written with EasyEngine's defaults (`Type "Object"`, no behaviors…). Scenes are matched by name (as
BuilderEngine read it), then by the most shared object ids (a renamed scene); `StartScene` follows a rename, and when
the start scene is deleted the first scene starts (a `StartScene` that names no scene is left alone).

Without a previous file (`null`, or `ToEeprojFromFolder`), the `easy` blocks rebuild every EasyEngine field. Known
normalisations of that path: each layer's list is written in draw order, and keys an older EasyEngine left out
(`Z`, `Locked`, `KeepAspect`, `Prefab`…) are written with their defaults.

## Layers

BuilderEngine has no layers. Opening a `.eeproj` flattens every layer's objects into the scene in draw order and keeps
each object's layer index in `easy.layer`; saving puts each object back in its layer (new objects go to the first
`Base` layer, which is created if missing; a scene made in BuilderEngine gets EasyEngine's Background / Base / UI
layers). Screen-space layers map to BuilderEngine's `ui` tag: their objects get the tag when read (it is never stored
in `Builder.tags`); a new object with the tag goes to the first `UI` layer (created if missing); taking the tag off an
object on a screen-space layer moves it to the `Base` layer, adding it to a world object moves it to the `UI` layer.

## Draw order

Inside a layer EasyEngine draws by `Z`, then by list order. When BuilderEngine kept the order of a layer's objects
(new ones only at the end), the previous list and `Z` values are kept and new objects get the top `Z`. When it
reordered them, the list follows BuilderEngine and `Z` values are raised just enough to keep that order (an untouched
lower `Z` stays). An object without a `Z` key (older files) stays without one unless its `Z` must change.

## Tilemaps

An EasyEngine tilemap is an object with a `Tilemap` behavior: `Nums.tileSize`, `Texts.palette` (a JSON list of tiles:
`{"color","solid"}` plus `"image"` for a picture, or `"frames"` + `"fps"` for an animated tile) and `Texts.data`
(`{"cols","rows","cells"}`, row by row, 1 = the palette's first tile, 0 = empty), its grid's top-left corner at the
object's `X`/`Y`. In BuilderEngine each one is a **tile layer** of its scene, not an object (`EasyLink.Tiles.cs`):

| EasyEngine Tilemap object | BuilderEngine tile layer (`Scene.TileLayers`) |
|---|---|
| `Name`, `X`, `Y` | `TileLayer.Name`, `X`, `Y` |
| `Nums.tileSize` (at least 4) | `CellSize` |
| `Texts.data` cols / rows / cells | `Columns` / `Rows` / cells (a palette number the palette doesn't have reads as empty) |
| palette tiles | the scene's `TileKinds`: one kind per different look (colour, solid, picture file name, frames + fps), shared by every layer of the scene; a picture keeps its relative path; animated tiles keep their `Frames`/`Fps` |
| `Nums.breakable`, `Texts.drop` | the kind's `Breakable` and `Drop` (`"none"` when empty) |
| its layer: Background / Sky / Base / anything up to the first Base layer | `Front = false` (drawn behind the objects) |
| a world layer above the first Base layer | `Front = true` (drawn over the objects) |
| always collides with its solid tiles | `Solid = true` |
| the whole object | `TileLayer.Easy` (the object as BuilderEngine reads it, its EasyEngine data in its own `easy` block) |

New kinds are named after their picture (`grass`, `dirt 2`) or `tile 1`, `tile 2`… Saving writes each layer back as
its object, in its EasyEngine layer and draw order (the writer treats it as one more object, so `Name`/`X`/`Y` follow
the common-field rules). The grid and palette are written only when the layer's cells differ from what BuilderEngine
reads from the file: a cell that still shows the same tile keeps its number (an unchanged map stays the same text,
editing one cell changes one number), and a kind the palette doesn't have is added at its end. `tileSize` is written
when the cell size changed. A layer deleted in BuilderEngine deletes its object.

A **tile layer made in BuilderEngine** becomes a new EasyEngine Tilemap object (`Type "Tilemap"`, the catalog's default
`Nums`, its palette and grid), with an id from `FixIds` (`TileLayer.Easy = {"id": n}` until it is read back): behind the
objects it goes in the first `Background` layer, in front in the first world layer above `Base` (else `Base`).
BuilderEngine-only layer settings travel in the object's `Builder.extra`: `tileFront` when `Front` isn't what its layer
gives, `tileSolid: false` for a layer bodies don't collide with. The scene's kinds are stored in the scene's
`Builder.extra.tileKinds` only when reading the palettes wouldn't give them back as they are (names, hardness, drops,
lights, one-way, unused kinds); reading starts from that list and adds a kind for each look it doesn't have. Files
from the first link version kept BuilderEngine tile maps in `Builder.extra.tileLayers`: they are read as layers made in
BuilderEngine and become Tilemap objects on the next save. EasyEngine needs no change for any of this: it draws and
plays those Tilemap objects natively.

Limits: BuilderEngine draws tile layers either behind or in front of all objects, so a tilemap drawn between two
objects of one EasyEngine layer is drawn behind both; the object's and its layer's opacity, `Active`/off layers and a
screen-space tilemap aren't reproduced (it plays as a world layer); EasyEngine fits a grid to the scene when it draws it
(`TileGrid.Fit`), BuilderEngine shows the stored grid as it is (an empty one is made the scene's size); breaking tiles
uses BuilderEngine's own rules (`break_tile`, `dig_tile`), not EasyEngine's `by…` options.

## Ids

One id space for new objects: an object made in BuilderEngine (no `easy` block — BuilderEngine numbers objects per
scene) whose id is used anywhere else in the project gets `max(all ids) + 1` (`FixIds`; the editor runs it before
each save). On load, two objects of **the same scene** with one id (both engines created an object at the same
moment) keep the first and renumber the second (`max + 1`). Older EasyEngine projects reuse ids in different scenes;
those are left as they are (objects are matched per scene when saving).

## Scene names

BuilderEngine needs unique scene names (`goto_scene` finds scenes by name), so a repeated name is read as "Kitchen 2".
Saving writes the file's spelling back unless the person renamed the scene in BuilderEngine.

## Pictures and sounds

In a folder with a `.eeproj` and no `project.json`, BuilderEngine's pictures folder is `Assets/sprites` and its sounds
folder `Assets/sounds` (unless the folder has `images/` / `sounds/`), so the picture picker, `look`, `play` and Import
use EasyEngine's folders. A relative path (`Assets\sprites\hero.png`) resolves from the project folder (only inside
it). A bare name picked in BuilderEngine is written to `SpritePath` with its folder (`Assets\sprites\hero.png`, or
`images\hero.png` for a BuilderEngine folder); `SaveFromEeproj` turns `images\hero.png` back into `hero.png`.
BuilderEngine doesn't add its starter sounds to an EasyEngine project.

## Who owns an object's behaviour

- An object **with BuilderEngine mechanisms** (`Builder.mechanisms` non-empty): EasyEngine's inspector shows only the
  common fields and, in Behaviors, a single read-only card **"Built on BuilderEngine"** (open it there to edit). It is
  not given EasyEngine behaviors. When EasyEngine plays, its mechanisms **run for real** through the bridge (below).
- An object **with EasyEngine behaviors**: BuilderEngine's inspector shows the common fields and a read-only card
  **"Built on EasyEngine"**: one line per behavior saying whether it runs in BuilderEngine (✓ runs here, ◐ runs here
  partly — and what's missing, ✗ doesn't run here yet), and **Open in EasyEngine** when EasyEngine is found.
  BuilderEngine keeps its data untouched; when BuilderEngine plays, its behaviors **run** as EasyKit mechanisms (see
  *Playing EasyEngine behaviors in BuilderEngine*).
- **Both**: giving a mechanism to an object with EasyEngine behaviors asks first (once per object). It then has both:
  BuilderEngine runs its mechanisms and its behaviors (as EasyKit mechanisms); EasyEngine runs its behaviors and,
  through the bridge, its mechanisms.
- Both empty: a plain object either engine can give behaviour to.

**Open in EasyEngine** saves, then starts EasyEngine (the `EASYENGINE_EXE` variable, else its Start-menu shortcut
`EasyEngine.lnk`, else `%LOCALAPPDATA%\Programs\EasyEngine\EasyEngine.exe`) with the `.eeproj` path as its first
argument: **EasyEngine opens a `.eeproj` given as its first argument in the editor.**

## Opening

BuilderEngine's start screen and Project menu: **Open project folder…** takes a folder with a `project.json` or a
`.eeproj`; **Open EasyEngine project…** takes a `.eeproj` file. Recent lists the folder with an **EasyEngine** badge.
`--project` takes either. A folder with both files opens `project.json` (unless the `.eeproj` is picked as a file).
Saving writes the `.eeproj` atomically; a `.eeproj` that can't be read is never saved over. Likewise `SaveFromEeproj`
throws (an `IOException` the caller shows) instead of saving into a folder whose `project.json` can't be read, since
saving deletes the mechanism files it doesn't list.

## Live link

`LiveLink` (`src/BuilderEngine.Script/LiveLink.cs`, UI-free) holds the watching logic; the editor side is
`MainWindow.Link.cs`.

- Each engine watches the file it has open (`FileSystemWatcher` on the `.eeproj`, or on `project.json` **and**
  `mechanisms/*.bs`), debounced (~300 ms). A change whose content hash equals the last thing this engine wrote or
  loaded is its own echo and is ignored; so is the same content written again. A file that can't be read yet (locked,
  half written) is read again a moment later.
- On an external change: reload the project and keep the editor's state (open scene, selected ids, camera/zoom,
  open mechanism and tab). Never while the game is playing (apply on Stop). BuilderEngine adds an undo step, so Ctrl+Z
  brings back the scene as it was before the outside change. With unsaved changes and Live link off, it asks first.
- While a project is open in both (or the person turns **Live link** on), each edit is saved ~500 ms after it,
  atomically (write a temp file, then replace), so the other engine sees it. If the other side saved meanwhile, its
  version is loaded first. BuilderEngine turns Live link on by default for `.eeproj` projects (Project menu → 🔗 Live
  link toggles it); it works the same between two BuilderEngine windows on one `project.json`.
- Status/title shows **🔗 Live link** while it is on.

## The bridge (EasyEngine playing BuilderEngine objects)

EasyEngine's PlayWindow creates one `BuilderEngine.Script.World` per scene when the scene has BuilderEngine objects:
it loads the project's mechanisms (`Builder.mechanisms` + the starter kit), spawns the BuilderEngine objects, and adds
every EasyEngine entity as a read-only mirror (name, position, size, tags from its Type/Group) so `touched by`,
`find`, `nearest` work across both. Each frame: copy EasyEngine positions into the mirrors → `World.Step` → copy the
BuilderEngine objects' positions/size/visibility/picture back to their EasyEngine entities for drawing. Prints and
errors go to EasyEngine's F1 console. Commands that need BuilderEngine's own screen (dialogue boxes, HUD) are mapped
to EasyEngine's equivalents where one exists, otherwise logged once as "not available in EasyEngine".

## Playing EasyEngine behaviors in BuilderEngine (EasyCompat)

Pressing Play in BuilderEngine on an EasyEngine project plays its behaviors the way EasyEngine does. This is
**EasyCompat** (`src/BuilderEngine.Script/EasyCompat*.cs`, `World.EasyCompat.cs`) and the **EasyKit** mechanisms
(`src/BuilderEngine.Script/StarterKit/EasyEngine/*.bs`, Builder Script, embedded as `EasyKit.*.bs`).

**How it works.** `GameBoot.UseProject` — the one place a world gets ready for a project, shared by the editor's Play
and exported games — calls `EasyCompat.Use`: for a project with EasyEngine behaviors it loads EasyKit into the world,
EasyEngine's view size (`ScreenWidth` × `ScreenHeight`, 960 × 540) and the project's variables. When a scene loads
(`World.LoadScene`, and `SpawnTemplate` for prefabs and copies), each object's EasyEngine behaviors become EasyKit
mechanisms with the behavior's settings as params (`Platformer` → `EasyPlatformer speed 260 jump 640 …`), added to the
running object only. **Nothing is added to the project**: the saved object keeps its behaviors and no mechanisms, so
EasyEngine still owns it and saving is unchanged. Objects turned off (`Active` false, or on a layer turned off) aren't
created; an exported EasyEngine game starts in its `StartScene`.

**Why Builder Script.** Each behavior is a short mechanism anyone can open and read (the same language the person
learns), and the engine features they needed were added as generic, tested Builder Script features instead of C# per
behavior: `camera size`, `camera smooth`, `camera hold`, `bars on/off` (cinema bars), `self.angle` (drawn rotated),
`say "Name" "line"` (a speaker that is just a name), `scene_width`/`scene_height`/`camera_x`/`camera_y`/`view_width`/
`view_height`, and `stop sound` also stopping a long one-shot sound. What is EasyEngine's own way of doing things
(objects found by name, its formula language, its variables with `:` in their names, one key press per Interact, its
dialogue queue, knockback, automatic idle/walk/jump animations) is a small set of `easy_…` functions and commands in
C# (`World.EasyCompat.cs`; hidden from the AI's vocabulary).

**Custom rules** (`when → do`) are translated into a Builder Script mechanism per set of rules (`EasyRules_<hash>`),
running them as EasyEngine's `RunCustom` does: every frame the rules started by a moment (key press, timer, after,
touch, click, landed, hurt, interact, key near, key idle…) are decided first, then the rules run in order; a block with
several WHEN rows fires when any of them does and runs its DO rows in order; "when it dies" runs at once, before the
object goes; guards (`cond`, the old `if/ifop/ifval`) and "↔ reverse" work. Numbers stay numbers; anything written as a
formula stays an EasyEngine formula (`easy_value("…")`, EasyEngine's Expr ported: `key:E`, `Player.x`, `local.hp`,
`item:wood`, and/or/not, `=`/`<>`). EasyEngine variables are BuilderEngine globals of the same name (`{score}` shows in
captions; `global musicIsPlaying` in the translated rules). **Covered:** every event of the block palette but the
mouse wheel; every action but particles (`emit`), `setactive`, `breaktile` and `settime`. Looping tweens play once.
**Cutscenes** are translated the same way (`EasyCutscene_<hash>`): the triggers (scene start, someone walks in, a
variable turns on, a key), once, lock, cinema bars, "camera stops", and the steps say, title, fade out/in, bars,
camera to an object or point, camera back, shake, move (waiting or not), face, show/hide, control the player / press a
key, play an animation (once/loop/auto/pause/resume), lock/unlock, set/add variables, texts, colours, lists, play
sound, go to a scene, destroy, spawn; at the end it gives back the keys, the bars, the camera and a fade it left on. The
generated Builder Script is kept in `EasyCompat.Generated` (the snap writes it to `compat_generated.txt`).

What can't play yet is said **once** in the console (`ℹ EasyEngine: not supported yet: …`), never as an error.

| EasyEngine behavior | In BuilderEngine |
|---|---|
| Platformer | ✓ `EasyPlatformer`: EasyEngine's speed, jump speed, gravity 2000, max fall 1500, coyote 0.1 s, buffer 0.12 s, variable jump (×0.45), double / wall jump, dash, extra keys, "Only while" |
| Top-Down | ✓ `EasyTopDown` |
| Solid | ✓ `EasySolid` (one-way too); physics "Static" is a solid, "Dynamic" falls (`EasyBody`) |
| Follow Camera | ✓ `EasyFollowCamera`: EasyEngine's per-frame smoothing as `camera smooth`, the scene clamp |
| Stay Inside | ✓ `EasyStayInside` (after physics, like EasyEngine): scene or screen, stop / bounce / wrap, sides |
| Custom | ◐ translated rules (above) |
| Cutscene | ◐ translated steps (above); no skip key; dialogue looks and fonts are the default ones |
| Interact | ◐ `EasyInteract`: say (line by line, freeze), set / add a variable, go to a scene, once, one press per object; default dialogue box |
| Go To Scene | ✓ `EasyGoToScene` (with "Only while", e.g. `key:E >= 1`) |
| Sound | ◐ `EasySound`: on start, once or looping; "only heard nearby" and left/right panning aren't there yet |
| Light | ◐ `EasyLight`: a round light (flicker); beam / half / area / bar shapes shine as a circle |
| Day / Night | ◐ `EasyDayNight`: the light level and isDay / isNight / timeOfDay / dayness; no sun and moon, no darker underground |
| Spin, Projectile, Patrol, Chase, Health, Damager, Collectible, Checkpoint, Shooter, Spawner, Bar, Trigger Area, Ability Pickup, Gate, Switch, Spring, Parallax, Destroy Off-screen | ✓ `Easy<Kind>` |
| Label | ◐ `EasyLabel`: the text in the object; no text effects |
| Smart Chase | ◐ plays as Chase (straight at the target) |
| Tilemap | ✓ a tile layer (see *Tilemaps*) |
| Boss AI, Lives, Advanced Patrol, UI Button, Shop, Pushable, Particles, Hotbar, Use Item, Harvestable, Terrain, Minimap, Generator, Test Checklist | ✗ not yet (said once in the console; the object still appears) |

## Tests

`tests/BuilderEngine.Tests/LinkTests.cs` round-trips every EasyEngine project it finds (`Desktop\Jogos\*`,
`Documents\EasyEngine Samples\*`, folders in `BE_EASYENGINE_PROJECTS`; copied to a temp folder, never written) and
checks the text comes back identical; plus a synthetic project with every case, layers, ids, draw order, merges, the
`Builder` block through an EasyEngine-like typed load/save, pictures/sounds, the live-link debounce and echoes, and a
5000-object project. `BuilderEngine.exe --project <temp copy of an EasyEngine project> --snap <folder> --mute` renders
the scene and the card and drives save / autosave / outside change / play (`link_checks.txt`), then renders every scene
with tilemaps and, in the one with the most, paints a cell with the tile brush, checks the file, undoes and plays
(`tiles_checks.txt`, `tiles_*.png`). `tests/BuilderEngine.Tests/TileLinkTests.cs`: tilemaps read as layers, one edited
cell = one changed number, new kinds added to a palette, a body landing on a converted tile, a BuilderEngine layer read
by EasyEngine's strict model, old `Builder.extra.tileLayers`, and a Mistify copy (when found).
`tests/BuilderEngine.Tests/EasyCompatTests.cs`: each mapped behavior on small hand-made `.eeproj` projects played
headless (platformer feel, solids, patrol, health/damage, interact + scene change, a cutscene's steps in order to its end,
custom rules with blocks, stay inside, follow camera, labels, pickups, gate, chase, shooter, spawner, top-down), and a
Mistify copy: every scene plays with scripted keys with no script errors, the music box toggles, its cutscenes reach
their end and change scene. Before the link snap, the `--snap` run above also plays the start scene and the first
scene with a cutscene to its end (`compat_checks.txt`, `compat_*.png`, `compat_generated.txt`).
