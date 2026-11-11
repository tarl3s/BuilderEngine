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
| `params` | one entry per setting, in the order shown. `type` is a **Builder Script param type** (`number`, `seconds`, `bool`, `text`, `Sound`, `Image`, `Color`, `Key`, `Object`, `Scene`, `Path`, `animation`, `List of …`, `Choice "a", "b"`), so every setting gets a picker (owner rule: picked, not typed). `default` is a JSON value. `min`/`max`/`step` for numbers, `help` a tooltip, `showIf` a Builder Script condition over the other params (the field is hidden while it is false) |
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
| shipped with BuilderEngine (embedded) | the official packages (e.g. the simple EasyEngine behaviors rewritten in this format) |

A project copy wins over an installed one with the same `name`; an installed one wins over a shipped one.

## How each engine stores its use

- **BuilderEngine**: like any mechanism (`SceneObject.Mechanisms` entry `{name, params}`); the package's script is the
  mechanism's source (kept in `behaviors/`, not `mechanisms/`).
- **EasyEngine**: a behavior of kind `Package` with `Texts["package"] = name`; numeric settings in `Nums`, the others in
  `Texts` (lists/paths as JSON text) — the same bags as every EasyEngine behavior, so old EasyEngine versions keep the
  data untouched. In BuilderEngine (via the link) such a behavior is the package's mechanism with those params.

## Complex built-ins

EasyEngine behaviors that are big or speed-critical (Platformer, Boss, Day/Night "Blocks" lighting, Terrain, Infinite
World…) stay **hand-translated** in BuilderEngine's `EasyCompat` — they don't need to become packages. Simple ones move
to packages over time; once a built-in has a package with `replaces`, EasyCompat uses the package instead of its own code.
