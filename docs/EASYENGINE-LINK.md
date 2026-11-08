# EasyEngine ⇄ BuilderEngine link (contract, version 1)

Both engines can open each other's projects and edit **the same project at the same time**; each one sees the other's
changes live. This file is the contract both codebases follow. When it changes, bump `linkVersion` and update both.

## The one converter

There is exactly one converter, `BuilderEngine.Script.EasyLink` (in BuilderEngine's UI-free library). It works on raw
JSON (`System.Text.Json.Nodes`), so it doesn't need EasyEngine's classes:

| Call | Used by |
|---|---|
| `EasyLink.FromEeproj(string eeprojJson, string folder) → Project` | BuilderEngine opening a `.eeproj` |
| `EasyLink.ToEeproj(Project, string? previousEeprojJson) → string` | BuilderEngine saving into a `.eeproj` (merges into the previous JSON so every EasyEngine field it doesn't map is kept byte-for-byte where possible) |
| `EasyLink.ToEeprojFromFolder(string beFolder) → string` | EasyEngine opening a BuilderEngine project folder (`project.json`) |
| `EasyLink.SaveFromEeproj(string eeprojJson, string beFolder)` | EasyEngine saving into a BuilderEngine project folder |

EasyEngine references `BuilderEngine.Script.dll` (+ `BuilderEngine.Plugin.dll`) as a **binary reference** copied into
`engine/lib/BuilderEngine/` by `engine/tools/Update-BuilderEngineLib.ps1`, so the EasyEngine repo still builds on its
own (GitHub, other PCs). Rebuild + recopy when the link or the runtime changes.

## Common fields (both engines edit them)

| BuilderEngine (`project.json`) | EasyEngine (`.eeproj`) |
|---|---|
| `Project.Name` | `Name` |
| `Scene.Name / Width / Height / Background` | `Scene.Name / Width / Height / BackgroundColor` |
| `SceneObject.Id / Name / X / Y / Width / Height` | `GameObjectData.Id / Name / X / Y / Width / Height` |
| `SceneObject.Color` | `Color` |
| `SceneObject.Image` (path relative to the project folder) | `SpritePath` (path relative to the project folder) |
| `SceneObject.Locked` | `Locked` |
| object order in the scene (draw order) | `Z` inside its layer |

Colours are `#RRGGBB` (or `#AARRGGBB`); pictures keep their relative path (`images/x.png` or `Assets/sprites/x.png`):
each engine resolves it from the project folder, so both see the same file.

## What each engine keeps for the other

- **In a `.eeproj`**, BuilderEngine's data lives in a `"Builder"` property:
  - on the project: `{"linkVersion":1, "mechanisms": {name: source}, "extra": {...}}` (the project's own mechanisms
    and any BuilderEngine-only project data);
  - on a scene: `{"extra": {...}}` (tile maps, BuilderEngine-only scene data);
  - on an object: `{"mechanisms":[{"name":"Coin","params":{...}}], "tags":[...], "extra": {...}}`.
- **In a `project.json`**, EasyEngine's data lives in an `"easy"` property: on the project (everything not mapped:
  prefabs, theme, variables…), on each scene (layers with their settings, scene variables…) and on each object (the
  whole EasyEngine object minus the common fields: behaviors, physics, animations, variables, its layer index…).
- EasyEngine must keep unknown properties when it loads and saves (`[JsonExtensionData]` on `Project`, `Scene`,
  `Layer`, `GameObjectData`), so the `"Builder"` data survives EasyEngine saving the file.

## Who owns an object's behaviour

- An object **with BuilderEngine mechanisms** (`Builder.mechanisms` non-empty): EasyEngine's inspector shows only the
  common fields and, in Behaviors, a single read-only card **"Built on BuilderEngine"** (open it there to edit). It is
  not given EasyEngine behaviors. When EasyEngine plays, its mechanisms **run for real** through the bridge (below).
- An object **with EasyEngine behaviors**: BuilderEngine's inspector shows the common fields and a read-only card
  **"Built on EasyEngine"**. BuilderEngine keeps its data untouched; when BuilderEngine plays, those objects appear
  (picture, colour) but their EasyEngine behaviors don't run there (play it in EasyEngine).
- Both empty: a plain object either engine can give behaviour to.

## Layers

BuilderEngine has no layers. Opening a `.eeproj` flattens every layer's objects into the scene in draw order and keeps
each object's layer index in `easy.layer`; saving puts each object back in its layer (new objects go to the first
`Base` layer, which is created if missing). Screen-space layers map to BuilderEngine's `ui` tag.

## Ids

One id space per project: a new object gets `max(all ids) + 1`. On load, a duplicated id (both engines created an
object at the same moment) keeps the first and renumbers the second.

## Live link

- Each engine watches the file it has open (`FileSystemWatcher` on the `.eeproj` or `project.json`), debounced
  (~300 ms). A change whose content hash equals the last thing this engine wrote is its own echo and is ignored.
- On an external change: reload the project and keep the editor's state (open scene, selected ids, camera/zoom,
  open mechanism). Never while the game is playing (apply on Stop).
- While a project is open in both (or the person turns **Live link** on), each edit is saved ~500 ms after it,
  atomically (write a temp file, then replace), so the other engine sees it.
- Status/title shows **🔗 Live link** while it is on.

## The bridge (EasyEngine playing BuilderEngine objects)

EasyEngine's PlayWindow creates one `BuilderEngine.Script.World` per scene when the scene has BuilderEngine objects:
it loads the project's mechanisms (`Builder.mechanisms` + the starter kit), spawns the BuilderEngine objects, and adds
every EasyEngine entity as a read-only mirror (name, position, size, tags from its Type/Group) so `touched by`,
`find`, `nearest` work across both. Each frame: copy EasyEngine positions into the mirrors → `World.Step` → copy the
BuilderEngine objects' positions/size/visibility/picture back to their EasyEngine entities for drawing. Prints and
errors go to EasyEngine's F1 console. Commands that need BuilderEngine's own screen (dialogue boxes, HUD) are mapped
to EasyEngine's equivalents where one exists, otherwise logged once as "not available in EasyEngine".
