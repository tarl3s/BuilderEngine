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
  - on a scene: `{"extra": {...}}` (tile maps, BuilderEngine-only scene data);
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
  **"Built on EasyEngine"** (the behavior names, and **Open in EasyEngine** when EasyEngine is found). BuilderEngine keeps
  its data untouched; when BuilderEngine plays, those objects appear (picture, colour) but their EasyEngine behaviors
  don't run there (play it in EasyEngine).
- **Both**: giving a mechanism to an object with EasyEngine behaviors asks first (once per object). It then has both:
  BuilderEngine runs only its mechanisms; EasyEngine runs its behaviors and, through the bridge, its mechanisms.
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

## Tests

`tests/BuilderEngine.Tests/LinkTests.cs` round-trips every EasyEngine project it finds (`Desktop\Jogos\*`,
`Documents\EasyEngine Samples\*`, folders in `BE_EASYENGINE_PROJECTS`; copied to a temp folder, never written) and
checks the text comes back identical; plus a synthetic project with every case, layers, ids, draw order, merges, the
`Builder` block through an EasyEngine-like typed load/save, pictures/sounds, the live-link debounce and echoes, and a
5000-object project. `BuilderEngine.exe --project <temp copy of an EasyEngine project> --snap <folder> --mute` renders
the scene and the card and drives save / autosave / outside change / play (`link_checks.txt`).
