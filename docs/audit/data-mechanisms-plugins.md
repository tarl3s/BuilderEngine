# Audit: build, mechanisms, files and plugins

Scope: the owner's sections 1 (build & project structure), 4 (mechanisms), 8 (plugins) and 9 (saving, loading, files).
Branch `audit/data`. Every fix has a test in `tests/BuilderEngine.Tests/AuditDataTests.cs` (called from `Program.cs`);
the whole suite is green: **383 passed, 0 failed** (365 before the audit + 18 new). The solution builds with 0 errors in
Debug and Release; the only warnings left are two nullable warnings in `MainWindow.Link*.cs` (see D-17).

Severities: **CRÍTICO** = unusable or data loss · **ALTO** = breaks important features / instability · **MÉDIO** = wrong in
specific situations · **BAIXO** = minor.

| | Count | Fixed |
|---|---|---|
| CRÍTICO | 3 | 2 (the third is in the EasyLink files, reported) |
| ALTO | 2 | 1 |
| MÉDIO | 7 | 6 |
| BAIXO | 12 | 5 |

## Measurements

| What | Result |
|---|---|
| `dotnet build` Debug / Release, all 6 projects (editor, Script, Plugin SDK, Player, HelloPlugin, tests) | 0 errors; 28 → 2 warnings (CA2255 ×26 justified and suppressed, D-03) |
| `tools/Publish-Release.ps1 -NoUpload` | 34 s, `BuilderEngine-win-x64.zip` 82.8 MB, 435 files; extracted, `player\BuilderEngine.Player.exe --check` plays every sample made with `BuilderEngine.exe --make-sample` (Deepdelve 24 objects, Ember Duel 45, Hollow Lantern 37, Night Shift 86 — 0 errors each) |
| Big project: 200 mechanisms (20 chains of variants 9 levels deep, all `uses Health`) + 50 scenes × 500 objects (25 000) | save 365–435 ms, `project.json` 9.8 MB, load 431–449 ms, ~14 MB managed memory, full round trip identical; world with 200 mechanisms 3 ms; first scene (500 objects) loaded + one frame 11–13 ms; undo snapshot of a 500-object scene 5 ms |
| Plugins: collectible load context after `PluginManager.Dispose()` | collected after 2 GC rounds (HelloPlugin loaded, played in a world, then unloaded) |
| Windows names on this machine (Windows 11, .NET 8) | `CON.bs`, `aux.bs`, `NUL.bs`, `COM1.bs` write and list fine; paths of 358 chars work (long paths enabled); a case-only replace (`coin.bs.tmp` → `Coin.bs`) keeps the new case; `*.bs` never matches `*.bs.tmp` / `*.bsx` |

## Findings

### D-01 — CRÍTICO — a broken `project.json` deleted every mechanism file of the project (fixed)
*Files*: `MainWindow.Scene.cs` (`LoadProject`), `Project.cs`.
*Cause*: when `Project.Load` threw, the editor renamed `project.json` to `project.broken-….json`, made an empty project
and saved it at once. `Project.Save` deletes every `mechanisms/*.bs` the project doesn't have — and the empty project
had none, because `Load` throws before reading `mechanisms/`. A truncated or hand-edited `project.json` therefore
cost the person all their code. The same catch-all also caught `IOException`/`UnauthorizedAccessException`: a project
in a cloud folder that is offline (the owner's workspace is in OneDrive), locked or not readable was treated as broken
→ renamed, replaced by an empty `project.json`, and its mechanism files deleted.
*Fix*: `Project.Open(folder, name, out OpenResult, out problem)` (headless): JSON errors → `Project.RecoverBroken`
(keeps the broken file, returns an empty project **with the folder's mechanisms**); I/O or permission errors →
`OpenResult.Unreadable`, an empty project that the editor never saves (`_projectUnreadable` makes `TrySave` refuse with
a status message). `LoadProject` uses it.
*Tests*: "a broken project.json never deletes the project's mechanisms…", "opening: a broken project.json is
recovered; files that can't be read (locked, offline) are reported and never saved over".

### D-02 — CRÍTICO — mechanism files the editor couldn't split were deleted on the next save (fixed)
*Files*: `MainWindow.Scene.cs` (`AdoptProjectMechanisms`), `Project.cs` (`SplitMechanismFiles`), `Systems.Split`.
*Cause*: the editor adopted project files through `Systems.Split`, which only keeps text that starts at a column-0
`mechanism`/`system` line. Saving writes back exactly what was adopted and deletes the other `.bs` files. So:
- a file whose first line isn't `mechanism X` (a draft closed mid-edit, an indented first line, a typo like
  `Mechanism`, an empty file) vanished from the editor and was **deleted** on the next save;
- comments above the `mechanism` line (file headers, notes) were silently dropped;
- two files declaring the same mechanism name: the later one silently replaced the other (also lost on save).

The mid-edit case is reachable from the editor itself: the code box's text is saved as is on close.
*Fix*: `Project.SplitMechanismFiles()` returns one source per name without ever dropping text — one mechanism per file
keeps the whole file (comments included), several are split with the leading comments on the first, and a file with no
mechanism line or with a clashing name is kept whole under its (unique) file name, where it shows as a mechanism with a
problem. `AdoptProjectMechanisms` (also used by the live-link reload) uses it.
*Test*: "mechanism files survive the editor's adopt → save: comments on top, two in one file, wrong file name, no
'mechanism' line, same name twice" (also checks that a second save changes nothing).

### D-03 — BAIXO — 26 × CA2255 (`[ModuleInitializer]` in a library) (justified, suppressed)
*Files*: `World.*.cs`, `Plugins.cs`, `Samples/*Sample.cs`.
*Analysis*: the pattern is safe here. The runtime runs a module's initializers once, before any type of the module is
used — so `World.Installers` / `SampleGames.All` are full before the first `World` exists. Order would only matter if
two installers set the same key; a new test runs each installer alone on a cleared world and proves that **no command
or function is set by two installers** (≥ 20 installers, > 50 names). Plugins can't take engine names (checked).
*Fix*: `<NoWarn>CA2255</NoWarn>` in `BuilderEngine.Script.csproj` with the reasoning and the test named.
*Test*: "the [ModuleInitializer] installers don't depend on their order…".

### D-04 — ALTO — renaming a mechanism broke prefabs (fixed)
*File*: `Project.RenameMechanism`. Only scene objects followed the rename; prefab templates kept the old name, so
spawning or placing the prefab failed ("no mechanism called …"). Now templates follow too.
*Test*: "renaming a mechanism also renames it in the prefabs…".

### D-05 — MÉDIO — renaming a mechanism broke the mechanisms that use it (fixed)
*Files*: `MainWindow.Variants.cs` (`VariantsOnRenamed`), `MechanismLibrary.RenameReferences` (new).
Only `mechanism X from Old` lines followed; `uses Old`, `includes Old`, `system X from Old` and `when Old.died` in
other mechanisms broke with "no mechanism called Old". `RenameReferences` renames those line heads (texts like `"Old"`
and longer names like `OldBar` are untouched); the open mechanism's own source is skipped (it is the one being typed).
*Test*: "renaming a mechanism follows it in other mechanisms…".
*Open*: `make Old at …`, `spawn "Old"` and `tell`/`broadcast` texts are not renamed (a value, not a reference).

### D-06 — MÉDIO — removing a system removed mechanisms still in use (fixed)
*File*: `Systems.Remove`. A copied-in part was removed when no scene object used it, even if a prefab template or
another mechanism (`uses`/`from`/`includes`/`make`) still needed it, and even when the system's own source stayed
(edited) and still included it. Now prefab templates count (new optional `prefabs` argument, passed by
`MainWindow.Systems.cs`) and any mechanism left in the project that brings or uses the part keeps it.
*Test*: "removing a system keeps the mechanisms a prefab or another mechanism still needs".

### D-07 — MÉDIO — a broken `settings.json` was silently replaced (API keys, recent list lost) (fixed)
*File*: `AppSettings.cs`. `Load()` had `catch { return new(); }`; the next `Set` wrote a file with one key over the
broken one (it holds the AI API keys, the recent list, plugin consent). Now writes go through `Update`: a file that
isn't JSON is first kept as `settings.broken-<time>.json` (once — the write then fixes the file); a file that exists
but can't be read is never written over; the catch-alls are narrowed to `IOException`/`UnauthorizedAccessException`
(and `JsonException` for parsing). Reading a broken file still returns empty, as before.
*Tests*: "a broken settings.json is kept as settings.broken-….json…", "a settings.json that can't be read (locked)
is never overwritten…" (the second passed before too; kept as a guard).

### D-08 — MÉDIO — two plugins adding the same command: silent replacement (fixed)
*File*: `Plugins.cs`. The load-time recorder only checked duplicates inside one plugin; in each world the later plugin
silently overwrote `world.Commands[name]`. Now the first plugin (file-name order) keeps the name, the other is reported
in `Errors`/console and left out of every world (`PluginManager.OwnerOf`). Documented in `docs/PLUGINS.md`.
*Test*: "two plugins adding the same command: the first keeps it, the second is reported…".

### D-09 — MÉDIO — exporting into a folder inside the project copied the previous export into the game (fixed)
*File*: `GameExport.CopyProject`. With the output folder inside the project (e.g. `Game\Exports`), the second export's
`game/` contained `Exports\Game\Game.exe` and the whole earlier export (it grows each time). `CopyProject` now skips
the export being made and any folder holding a `game/project.json`.
*Test*: "exporting into a folder inside the project never copies the export…".

### D-10 — MÉDIO — release zip had backslash entry names (fixed)
*File*: `tools/Publish-Release.ps1`. Windows PowerShell 5.1's `Compress-Archive` (and, it turned out,
`ZipFile.CreateFromDirectory` in PS 5.1) wrote 217 entries as `player\x.dll`; backslashes aren't valid zip paths
(non-Windows tools and some unzippers create files named `player\x.dll`). The updater (`ZipFile.ExtractToDirectory`
on Windows) happened to cope. The script now names each entry with `/`. `-NotesFile` is only required when uploading.
*Verified*: rebuilt zip has 0 backslash entries; extracted, the editor and the player run (see Measurements).

### D-11 — BAIXO — an `ollama serve` started by BuilderEngine kept running after it closed (fixed)
`OllamaManager.Stop()` ("stops the server only if BuilderEngine started it") was never called. `Program.cs` now calls
it on the desktop lifetime's `Exit`. One the person started themselves is untouched (`_server` stays null).
*Test*: none possible headless (UI lifetime wiring); verified by build and code reading.

### D-12 — BAIXO — catch-alls that hid errors in owned files (fixed where it matters)
Narrowed: `ParamTypes.AsList` (`JsonException`), `MechanismLibrary.Read` (+`UnauthorizedAccessException`, which used
to escape and could crash the library list), `AppSettings` (D-07, `SameFolder`), `MainWindow.Projects.OpenFolder`
(`Win32Exception`). `PluginManager.Add` no longer swallows a throwing `Name`/`Description` silently: it is logged.
*Test*: "a plugin whose Name throws is told in the console (not hidden)…".
*Kept on purpose* (reported to the person or by design): plugin isolation catches in `Plugins.cs` (every one reports);
UI boundaries in `MainWindow.Library/Systems/Projects/Export.cs` that show `e.Message` in the status bar; `Updater.cs`
(`ParseLatest`, `CheckAsync`, temp cleanup) — a start-up update check must never crash the launcher, and narrowing it
can't be tested from the test project (the editor assembly isn't referenced); `OpenProjectAsync`'s start-folder hint.

### D-13 — ALTO — closing the editor when the save fails loses the changes without asking (not fixed: editor UI)
`MainWindow.Closing → SceneOnClosing → TrySave()`; on failure (disk full, folder gone, permissions, D-01's unreadable
state) the window closes anyway — only a status line flashes. `SwitchProjectAsync` already asks in that case. There is
no autosave (only Ctrl+S, close, switch, export and the live link save), so a crash loses everything since the last
save. *Recommendation* (editor UI owner): cancel `Closing` when `TrySave` fails and ask "Close anyway?" (skip in
`--snap`); consider a periodic autosave of a dirty project.

### D-14 — CRÍTICO — `EasyLink.SaveFromEeproj` can delete a BuilderEngine project's mechanisms (reported, other agent's file)
`EasyLink.cs` lines 85–88: when the incoming `.eeproj` text has no `Builder.mechanisms` it keeps the folder's mechanisms
by calling `Project.Load(beFolder)`; if that throws (broken `project.json`, file locked) the catch swallows it and
`p.Save(beFolder)` then deletes every `mechanisms/*.bs`. *Suggested fix*: read the `.bs` files directly (they don't
depend on `project.json`), e.g. through `Project.RecoverBroken`/a small public `ReadMechanisms`, and don't save when
they can't be read. Also `LiveLink.Matters` has a bare `catch { return false; }` (minor).

### D-15 — MÉDIO — plugins have no isolation from hangs (documented, not fixable in-process)
Plugins run on the game thread; a `Register` or command that loops forever freezes the editor (on project open for
`Register`), and .NET can't abort a thread. Exceptions are isolated (all reported). Documented in `docs/PLUGINS.md`.
Real isolation would need an out-of-process host — a large change, not justified now (plugins are opt-in, per project,
behind a consent dialog). Other plugin risks: all plugins share one load context, so two plugins shipping different
versions of the same library get the first one loaded (a mismatch surfaces as a script error); a plugin that references
`BuilderEngine.Script` and adds to `World.Installers` would pin its load context forever. Unloading works otherwise
(measured, D-measurements). Old-plugin compatibility: `IBuilderPlugin.Name/Description` are default interface members,
so adding members that way keeps old plugins loading; adding a non-default member would break them (`TypeLoadException`,
reported as a load error).

### D-16 — BAIXO — deep `uses` chains are reported as loops (reported, runtime owner)
`World.Instantiate` throws "'X' uses itself in a loop" when `depth > 16`, so a legitimate, non-cyclic chain of 18
mechanisms each using the next gets a misleading message. Real cycles are caught correctly (test "cycles are friendly
errors, never a hang": `uses` A↔B, `from` A↔B, systems including each other — no hang). *Recommendation*: track the
names on the path to tell a cycle from depth, or say "uses too many levels".

### D-17 — BAIXO — nullable warnings CS8604 `MainWindow.Link.cs(76)`, CS8602 `MainWindow.LinkSnap.cs(34)` (reported)
Link files are being edited by another agent; left for them. `_eeproj` is non-null on that path (`?? ""` or `!`
would do).

### D-18 — BAIXO — My Library copies don't bring their dependencies (reported)
"Save to My Library" of a plain mechanism saves only that file; a variant (`from Slime`) or a mechanism that `uses` a
project mechanism comes into another project with a friendly error ("there's no mechanism called 'Slime'"). Systems
already save and copy their members (`Systems.SaveToLibrary` / `Systems.Add(library:)`). *Recommendation*: reuse that
for plain mechanisms (`from` + `uses` parents that aren't starter-kit ones). Updating a library mechanism never changes
projects that already copied it — by design (copy semantics, said in the Remove dialog).

### D-19 — BAIXO — duplicated code (partly fixed)
`SafeFileName` existed twice (Project, MechanismLibrary): now one (`Project.SafeFileName`, internal). Still duplicated:
`Sounds.Import`/`Images.Import` and `Sounds.Resolve`/`Images.Resolve` (same code, different folder/extensions),
`GameExport.SafeName` vs the folder-name cleaning in `MainWindow.Projects.CreateProject`/`LauncherWindow`.
`Systems.InProject` is unused (documented for the inspector, never called) — left in place, not a feature.

### D-20 — BAIXO — non-atomic writes that remain
Atomic (temp file + move): `project.json`, `mechanisms/*.bs`, My Library, `settings.json`, game saves, `.eeproj`.
Not atomic: `Sounds.Import`/`Images.Import` (new files only — a crash leaves a partial new file, never damages an
existing one) and `PixelArt.Save` (the sprite editor overwrites a picture in place: a crash mid-write corrupts that
picture — editor/runtime owner). Interrupted saves leave `*.tmp` files, which are never read (test "half-written files
from an interrupted save are never read as mechanisms") nor exported; they are not cleaned up.

### D-21 — BAIXO — names and paths (verified, no change)
Unicode, emoji, quotes, spaces in project folders, scene/object names, tags and params round-trip (test "names with
spaces, accents, emoji and Windows device words round-trip"). Mechanism names are identifiers (letters incl. accents,
digits, `_`), so they can't contain path characters. On Windows 10 a mechanism called `CON`/`AUX`/`NUL`/`COM1`
would fail to save (`CON.bs` is a device name there; Windows 11 allows it — measured). *Recommendation*: refuse those
names in `BlockEdits.CheckName`/`Systems.CheckName` rather than renaming files (file names are the load keys).

### D-22 — BAIXO — old / odd project data (verified, no change)
Missing fields get defaults, numbers as text are read, unknown fields ignored (existing tests); a scene object's param
that was removed or renamed in the mechanism is ignored, a param whose type changed is converted (`12` → `"12"`,
`"yes"` → `true`), names match case-insensitively, a missing mechanism is one friendly error and the object still
appears (test "old/odd files load…"). Removed params stay in `project.json` forever (harmless leftovers).

### D-23 — BAIXO — docs vs real formats (fixed in README)
Code and docs agree: `project.json`, `mechanisms/<Name>.bs`, `images/`, `sounds/`, `saves/` (editor) or
`%LOCALAPPDATA%\BuilderEngine Games\<game>\saves` (exported), `plugins/`, `%APPDATA%\BuilderEngine\{settings.json,Library\*.bs}`,
`.eeproj` for EasyEngine. The README's project line listed only `project.json + mechanisms/*.bs + sounds/`; it now
lists every folder and says how crashes and a broken `project.json` are handled. No extension was changed.

### Note (not counted) — the live link benefits from D-02
`LinkReload` reuses `AdoptProjectMechanisms`, so D-02's fix also covers a `.bs` file another program is writing while
the live link reloads. (Its own `Project.Load` failures already retry instead of saving.)

### D-25 — BAIXO — section 1 structure notes (no change)
Project references are clean (Plugin ← Script ← Editor/Player; HelloPlugin references the SDK with `Private=false`, so
no second SDK copy sits next to it); the Player compiles the editor's drawing/audio files as links (one copy of the
code). All three Avalonia packages are 11.3.22 in both apps. Versions: editor and player 0.1.0, SDK 1.0.0 (only tag
`v0.1.0`). No NuGet dependencies besides Avalonia.

## Files changed

Owned: `Project.cs`, `Systems.cs`, `MechanismLibrary.cs`, `Plugins.cs`, `AppSettings.cs`, `ParamTypes.cs`,
`GameExport.cs`, `BuilderEngine.Script.csproj`, `MainWindow.Variants.cs`, `MainWindow.Systems.cs`,
`MainWindow.Projects.cs`, `tools/Publish-Release.ps1`, tests (`AuditDataTests.cs`, one line in `Program.cs`), docs.
Cross-area (minimal): `MainWindow.Scene.cs` (`LoadProject` uses `Project.Open`, `AdoptProjectMechanisms` uses
`SplitMechanismFiles`, `TrySave` refuses an unreadable project), `Program.cs` (stop Ollama on exit), `README.md`,
`docs/PLUGINS.md`.

## Open risks and recommendations (by priority)
1. D-14 (EasyLink sidecar save can delete mechanisms) — fix in the EasyLink branch.
2. D-13 (close without saving when the save fails; no autosave) — editor UI.
3. D-15 (plugin hangs) — documented; consider a watchdog that at least reports a slow `Register`.
4. D-18 (library dependencies), D-21 (refuse device names), D-16 (deep `uses` message), D-19/D-20 cleanups.
