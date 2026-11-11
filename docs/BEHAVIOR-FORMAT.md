# Behavior packages (`.behavior`, format version 1)

A **behavior package** is one file that both engines understand:

- **EasyEngine** shows it in the inspector like any built-in behavior (name, icon, description, a picker for every
  setting) and runs it through the BuilderEngine runtime it already embeds.
- **BuilderEngine** treats it as a mechanism (its `script`), with the same settings in the inspector.

So a behavior written once works in both engines, future EasyEngine behaviors need no hand translation, and anyone
can make and share behaviors. The logic is **Builder Script**, which can't touch files, the network or the computer:
a package from someone else is safe to use (C# plugins are a different, trusted-only thing).

The one reader/writer/validator is `BuilderEngine.Script.BehaviorPackage` (UI-free; EasyEngine uses it through
`lib/BuilderEngine/BuilderEngine.Script.dll`). Engines never parse the file themselves.

## The file

UTF-8 JSON, extension `.behavior`. Unknown properties are kept when the file is rewritten.

```json
{
  "format": "builderengine.behavior",
  "version": 1,
  "name": "Spin",
  "replaces": "Spin",
  "icon": "🌀",
  "category": "Movement",
  "description": "Turns the object round and round.",
  "author": "tarl3s",
  "license": "MIT",
  "packageVersion": "1.0.0",
  "repeatable": false,
  "params": [
    { "name": "speed", "label": "Speed (degrees per second)", "type": "number", "default": 90, "min": -1080, "max": 1080, "step": 15,
      "help": "Negative turns the other way." },
    { "name": "direction", "label": "Direction", "type": "Choice \"clockwise\", \"counter\"", "default": "clockwise" },
    { "name": "only_when_moving", "label": "Only while moving", "type": "bool", "default": false,
      "showIf": "direction == \"clockwise\"" }
  ],
  "ai": {
    "keywords": ["spin", "rotate", "turn", "girar"],
    "guide": "Add Spin to make a coin or a saw turn. Speed is degrees per second."
  },
  "script": "mechanism Spin\n    param speed: number = 90\n    param direction: Choice \"clockwise\", \"counter\" = \"clockwise\"\n    param only_when_moving: bool = false\n\n    when tick\n        ...\n"
}
```

| Property | Meaning |
|---|---|
| `format`, `version` | always `"builderengine.behavior"` and `1` (a newer version is refused with a clear message) |
| `name` | the behavior's name; **must equal the mechanism name** in `script`; letters, digits, `_` |
| `replaces` | optional: the EasyEngine `BehaviorKind` this package implements (`"Spin"`). EasyEngine then plays its built-in `Spin` objects with this package in BuilderEngine; EasyEngine itself keeps its own built-in unless told otherwise |
| `icon`, `category`, `description` | what the inspector and the behavior picker show (`description` is also what the AI reads) |
| `author`, `license`, `packageVersion` | free text, shown in the package's details |
| `repeatable` | `true` when two of it on one object make sense (like EasyEngine's Interact, Sound…) |
| `params` | one entry per setting, in the order shown. `type` is a **Builder Script param type** (`number`, `seconds`, `bool`, `text`, `Sound`, `Image`, `Color`, `Key`, `Object`, `Scene`, `Path`, `animation`, `List of …`, `Choice "a", "b"`), so every setting gets a picker (owner rule: picked, not typed). `default` is a JSON value. `min`/`max`/`step` for numbers, `help` a tooltip, `showIf` a Builder Script condition over the other params (the field is hidden while it is false; `BehaviorPackage.Shows` works it out — names, literals, `not`/`and`/`or`, comparisons, `+ - * /`; one it can't work out shows the field). `easyKey` (optional, only read for a package with `replaces`): the key of this setting in the EasyEngine built-in's `Nums`/`Texts` when it can't be the param's name — EasyEngine's `to` and `max` are Builder Script words, so Trigger Area's `set_to` has `"easyKey": "to"` and Spawner's `max_count` has `"easyKey": "max"` |
| `ai` | optional words that make the AI think of it, and a short how-to for Learn mode |
| `script` | the Builder Script source: exactly one `mechanism` whose name is `name` and whose `param`s are the `params` (same names and types). It may `uses` starter-kit mechanisms |

## Validation (`BehaviorPackage.Validate`)

A package is refused (with every problem listed, in plain words) when: the JSON is broken or `format`/`version` is
wrong; `name` is missing or not a valid name; `script` doesn't parse; it doesn't hold exactly one mechanism named
`name`; a manifest param is missing from the script or has a different type; a `default` doesn't fit its type; a
`showIf` doesn't parse. Script params missing from `params` are allowed (shown with their script defaults).

## Where packages live

| Folder | Who |
|---|---|
| `<project>/behaviors/*.behavior` | the project's own and every package it uses (copied in, so a shared game works anywhere) |
| `%APPDATA%\BuilderEngine\Behaviors\*.behavior` | the person's installed packages, offered in **both** engines |
| shipped with BuilderEngine (embedded) | the official packages (`src/BuilderEngine.Script/Packages/*.behavior`, embedded as `Packages.<Name>.behavior`; `BehaviorPackage.Shipped()`) |

A project copy wins over an installed one with the same `name`; an installed one wins over a shipped one
(`BehaviorPackage.DiscoverAll(projectDir)` returns them in that order, each with its `Origin`). A mechanism of the
project with the same name wins over any package. `GameBoot.UseProject` — the editor's Play and exported games — loads
them all into the world (`World.Packages`; a broken file is said once in the console, never thrown), and an exported
game copies the project's `behaviors/` folder.

## Shipped packages

The simple EasyEngine behaviors, rewritten as packages (each `replaces` its built-in; settings with EasyEngine's names,
defaults and labels). They play in any BuilderEngine project too.

| Package | Replaces (EasyEngine) | What it does |
|---|---|---|
| `Spin` ↻ | Spin | turns on its own (degrees per second) |
| `Projectile` ➤ | Projectile | flies in a direction, disappears after a while |
| `Parallax` 🏔 | Parallax | a background that scrolls slower than the camera |
| `DestroyOffscreen` 🗑 | DestroyOffscreen | removed when it leaves the screen |
| `StayInside` 🔲 | StayInside | kept inside the scene or the screen: stops, bounces or wraps |
| `Collectible` ⭐ | Collectible | picked up on touch: points to a variable, an item |
| `Gate` 🚧 | Gate | solid until a variable meets a condition (or the player has an item) |
| `Switch` 🎚 | Switch | flips a variable: click, touch or a key |
| `Spring` 🔼 | Spring | launches what lands on it |
| `TriggerArea` 🟦 | Area | an invisible zone: go to a scene, set / add a variable, say |
| `GoToScene` 🚪 | GoToScene | a door to another scene |
| `AbilityPickup` 🗝 | AbilityPickup | turns a variable on (and a platformer ability) on touch |
| `Spawner` ✨ | Spawner | copies of an object or prefab, on a timer or a key |

They use a few `easy_…` words (objects found by name, EasyEngine's variables and "only while" formulas); outside an
EasyEngine project those work on plain global variables. Their `cond` setting ("Only while") is EasyEngine's formula.

## Making and sharing one

- **In BuilderEngine**: right-click a mechanism in the library → **Save as behavior…**: a name, an icon (picked from a
  list), a category, a description and a label and help for each setting. It is saved as `behaviors/<Name>.behavior`
  (`BehaviorPackage.FromMechanism` + `Save`) and from then on the mechanism *is* the behavior (it isn't saved in
  `mechanisms/` any more). Add `min`/`max`/`step`/`showIf` by editing the file if you need them.
- **The BEHAVIORS section** of the library lists the project's, installed and shipped packages (icon, name, where it
  comes from; the tooltip is its description). Drag one onto the scene or an object like a mechanism — an installed or
  shipped one is first copied into the project's `behaviors/`. Right-click: **Open its code** (read-only, with **Make a
  copy to edit**: a project mechanism `Spin2` you can change), **Install** (copies it to the installed folder: every
  project and EasyEngine get it), **Export…** (a `.behavior` file to send), **Import .behavior…** (checks it and copies
  it into the project; a broken one is refused with the reasons), **Remove** / **Uninstall**.
- **Its card in the inspector** shows the package's labels (above each field), help as tooltips, spinners limited to
  `min`/`max` and stepping by `step`, and hides a field while its `showIf` is false.
- **By hand**: write the JSON above (or export one and change it); `BehaviorPackage.Validate` lists what is wrong.

## How each engine stores its use

- **BuilderEngine**: like any mechanism (`SceneObject.Mechanisms` entry `{name, params}`); the package's script is the
  mechanism's source (kept in `behaviors/`, not `mechanisms/`).
- **EasyEngine**: a behavior of kind `Package` with `Texts["package"] = name`; numeric settings in `Nums`, the others in
  `Texts` (lists/paths as JSON text) — the same bags as every EasyEngine behavior, so old EasyEngine versions keep the
  data untouched. In BuilderEngine (via the link) such a behavior is the package's mechanism with those params (a
  setting it doesn't have keeps the package's default; a package that isn't there is said once in the console).

## Complex built-ins

EasyEngine behaviors that are big or speed-critical (Platformer, Boss, Day/Night "Blocks" lighting, Terrain, Infinite
World…) stay **hand-translated** in BuilderEngine's `EasyCompat` — they don't need to become packages. Simple ones move
to packages over time; once a built-in has a package with `replaces`, EasyCompat uses the package instead of its own code
— the project's or an installed one first, then the shipped one (`World.PackageReplacing(kind)`). The built-in's settings
become the package's params by name (or `easyKey`): numbers and yes/no from `Nums`, the rest from `Texts`; a `Key`
becomes Builder Script's key name, a `Choice` the option it names in any case, a `text` shows `{name}` parts. A package
param called `cond` gets the behavior's "Only while" formula (and "turn a behavior on/off" rules switch it).

Still hand-made in EasyCompat, and why: Platformer, Top-Down, Body, Patrol, Chase / Smart Chase (the movers share the
mover rules: who steers, knockback, speed boosts, Patrol giving way to Chase), Health, Damager, Shooter, Checkpoint (the
combat chain: hits, invincibility, knockback, respawn points, the health variable one object owns, and the "hurt"/"died"
rule events), Interact (one key press per object, the rules' "interact" event, dialogue looks), Solid and Follow Camera
(added for physics modes and used by cutscenes), Bar (it reads the object's UI layer), Label, Sound, Light, Day / Night
(only partly supported: their notes in the console), Custom rules and Cutscene (translated per project), Tilemap.
