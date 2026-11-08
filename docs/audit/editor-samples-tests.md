# Audit: the editor, the sample games and the test suite

Sections 7 (desktop editor), 12 (sample games) and 11 (tests) of the technical audit, branch `audit/editor`.
Everything below was measured on this machine (Windows 11, .NET 8, Avalonia 11.3.22, a 1920×1080 screen at 100 %)
with the app's own `--snap` rendering and the headless test runner — never a screen capture.

How to run everything: `powershell -ExecutionPolicy Bypass -File tools\Test-All.ps1` (new). It builds the editor into
`bin\test-all\` (never the owner's `bin\Debug`, which Windows locks while the editor is open), runs the unit tests and
every `--snap` mode, and prints one line each. A snap mode passes only when the app exits by itself, writes no
`error.txt`, has no `FAIL` line and has at least the expected number of `PASS` lines (a snap that stops early writes
fewer lines, not a FAIL). `-Only unit,lifecycle` and `-NoBuild` narrow it.

## Summary

| Severity | Found | Fixed here | Fixed by another audit (verified here) | Left open (reported) |
|---|---|---|---|---|
| CRÍTICO | 2 | 0 | 2 (data audit, D-01/D-02 — the UI path is now covered by `--snap-only lifecycle`) | 0 |
| ALTO | 4 | 4 | 0 | 0 |
| MÉDIO | 6 | 5 | 1 partly (runtime audit's console trim) | 1 residual (E-07) |
| BAIXO | 9 | 3 | 1 (CA2255, data audit) | 5 |

## Measurements

| What | Before | After |
|---|---|---|
| Closed editor windows still in memory after a full GC (open + close 4 windows, the start screen's path) | 3 of 3 alive (each kept its project and kept syncing every 400 ms) | 0 of the first 3 (Avalonia holds the most recent one for a while: bounded) |
| Switching between 20 projects and back, managed heap after full GC | — | 7.8 → 8.0 MB (flat) |
| Console after a game printed 36,000 lines (10 min × 60 fps), cost of one more line incl. layout | 1,094 ms per line, 1.3 MB of text | 36 ms with the runtime audit's 40,000-char trim (1,347 lines kept); 5 ms measured with a 200-line cap (see E-07) |
| Editor window at 910×470 / 1024×640 client area (1366×768 at 150 % / small laptop) | Fit, zoom and the last tools were under the inspector, unreachable | all reachable (bars wrap); Play/Save/console on screen |
| 2560×1440 | can't be judged on a 1080p screen: Windows clamps the window to 1924×1061 (PNG made, checks skipped and noted) | — |
| 10 minutes of random input per sample (2 seeds × 5 min, 18,000 frames each; headless through `GameBoot.NewWorld`) | — | 0 script errors in all four; objects bounded (max 42 / 86 / 50 / 86); worst frame 68 ms (Deepdelve), 8.8 ms (Night Shift), 38 ms (Hollow Lantern), 222 ms once (Ember Duel, first deck build) ; Deepdelve 12.3 s, Night Shift 1.7 s, Hollow 3.8 s, Ember Duel 0.9 s per 5 min |
| How far random play got | — | Night Shift: title → intro → night → 4 AM → caught (Lose screen); Ember Duel: Title → Build → Battle; Deepdelve: Title → HUD (player travelled 411–692 px); Hollow Lantern: intro done, 458 px |

Test totals: see the last section.

## Findings

### E-01 · CRÍTICO · a project.json that isn't JSON deleted the project's mechanism files — fixed by the data audit, verified in the UI
Files: `MainWindow.Scene.cs` (`LoadProject`), `Project.cs` (`Save`). Cause: when `Project.Load` threw, the editor made an
empty project and saved it; `Project.Save` deletes every `mechanisms/*.bs` the project doesn't list, so every script of
the project was deleted (the broken project.json itself was moved aside). Reproduced with the new
`--snap-only lifecycle` check "a project.json that isn't JSON: its mechanism files are kept" (FAIL on the code before the
merge). The data audit fixed it in parallel (`Project.Open` / `RecoverBroken`, D-01); my own fix was dropped in the merge
in favour of theirs, and the snap check now passes on it.

### E-02 · CRÍTICO · a broken project.json that couldn't be set aside was saved over later — fixed by the data audit, verified
Same path: the move to `project.broken-….json` failed silently (`catch { }`), the log still said it was kept, and the
next save (closing the window, Ctrl+S) replaced the file. Reproduced by holding the file open (`FileShare.Read`, as
OneDrive or an editor does) while the project opens. Check: "a broken project.json that couldn't be set aside is never
saved over" (FAIL before, PASS now with `Project.OpenResult.Unreadable`).

### E-03 · ALTO · every editor window closed through "Start screen…" stayed in memory forever — fixed
File: `MainWindow.Prefabs.cs`. Cause: the 400 ms prefab-sync `DispatcherTimer` was started and never stopped; a running
DispatcherTimer is rooted by the dispatcher, so the closed window, its project, scenes and bitmaps stayed alive and kept
syncing. Fix: stop it on `Closed`. Test: lifecycle "closed editor windows are freed" (3 of 3 alive before, 0 after).
Other timers checked: `_blocksTimer` is one-shot, `_debugTimer` stops when play stops, `_linkTimer` and
`AnimationWindow._timer` stop on `Closed`.

### E-04 · ALTO · closing the editor when the save failed closed it anyway; changes lost without a word (data audit's D-13) — fixed
Files: `MainWindow.Scene.cs` (`SceneOnClosing`, `AskLeaveUnsavedAsync`), `MainWindow.cs` (passes the closing args),
`MainWindow.Projects.cs` (`BackToStartScreen`, minimal). Now the close is cancelled and the person picks
"Save again" / "Close without saving" / "Cancel"; "Start screen…" asks the same way. A project whose files couldn't be
read (E-02) is never saved, so closing it doesn't ask. `--snap` runs never show modal dialogs (`_askWhenCloseCantSave`
is off), the lifecycle snap turns it on. Test: "closing when the save fails keeps the editor open and asks" and
"…and Cancel keeps it open" (project.json held with `FileShare.None`). Before the fix the window simply closed (the
check can't be run on the old code: the app would exit), verified by reading the old handler.

### E-05 · ALTO · the sprite editor threw a drawing away on Esc, Cancel or the window's X — fixed
File: `SpriteEditorWindow.cs`. No unsaved-changes tracking at all; Esc (a key people press to deselect) closed it. Now
the picture as opened/last saved is kept; closing with changes asks "Save" / "Discard" / "Keep drawing". Tests
(lifecycle): "closing the sprite editor with a new drawing asks first", "…and keeping the drawing leaves it open",
"once saved, it closes without asking". The prefabs snap (which saves then closes) still passes.

### E-06 · MÉDIO · the windows opened bigger than small or scaled screens — fixed
Files: new `WindowFit.cs`, `Program.cs`. The editor is 1360×840 (min 960×600) and the start screen 1000×720, never
fitted: on 1920×1080 at 125–150 % or a 1366×768 laptop the bottom (status bar, console) started off-screen and the
minimum size couldn't shrink. Now, when either window opens, it is shrunk (with its minimum) to the screen's working
area minus the title bar, and centred; a window that fits is untouched. Tests: three `WindowFit.Fit` cases in the
lifecycle snap plus the layout checks at 910×470 and 1024×640.

### E-07 · MÉDIO · the console grew without limit; each new line cost more than the last — fixed by the runtime audit, residual cost reported
`MainWindow.Log` appended to one string: after 36,000 lines one more line took 1,094 ms (text copy + layout of 1.3 MB).
The runtime audit trims the console to ~40,000 characters (merged; I didn't redo it, as asked). Measured in the UI
after the merge: bounded (1,347 lines, 49 KB) but **36 ms per new line** including layout — a game that prints every
frame still costs about two frames of UI time per frame. A 200-line queue measured 5 ms. Recommendation for the
runtime owner: keep fewer characters (~8,000) or a line cap. Test (lifecycle): "the console stays bounded after 36,000
lines" (< 100 ms and ≤ 60,000 chars) and "the console still shows the newest line".

### E-08 · MÉDIO · at 1024 px wide (and below) the scene's bars ran under the inspector — fixed
Files: `MainWindow.Scene.cs` (`EditBar`), `MainWindow.Tools.cs`. The edit bar and the TOOLS row were horizontal
StackPanels: zoom −/+, Fit and the last tools (Eraser, Scatter, Room Maker) were cut off and couldn't be clicked at the
editor's own minimum width (960) and at 1024×640. They are WrapPanels now (the hint keeps its own room and shrinks;
the mouse X/Y keeps its own column). Test: "<size>: Fit and every tool button fit in the scene area" at 910×470 and
1024×640; PNGs `size_*.png`. The default 1360×840 layout is unchanged (full snap PNGs).

### E-09 · MÉDIO · Ember Duel used an unset variable called `none` as "nothing" — fixed at the engine
Files: `Runtime.cs` (`ResolveFirst`, one line — runtime/language area, minimal), `docs/MVP.md`. `Duel`, `Lane` and
`RivalAI` write `best = none`, `return none`, `unit = none`; `none` was never a value, only a name nobody set, so it read
as nothing by accident — any `none = …` anywhere (a global, a variable) would silently change the duel. Found by a new
static check over every sample ("no mechanism reads a name that nothing ever sets"). Now `none` and `nothing` always
read as nothing (consistent with `look none`, `caption none`), documented in MVP.md. Tests: "'none' and 'nothing' are
always nothing…" (FAIL before: a=3 b=5 c=7) and the per-sample never-set check (lists any typo'd or never-set name; the
only other hits were names set by commands — `crafted`, `taken`, `broken`, `drop`, `placed` — which are allowed).

### E-10 · MÉDIO · the printer round-trip tests could not see records, fields, variants, systems or Choice options — fixed
File: `tests/BuilderEngine.Tests/Program.cs` (`DumpE`/`DumpS`/`Dump`). The tree dump used to compare before/after
printing returned `"?"` for any node it didn't know (`RecordExpr`, `MemberExpr`) and ignored `Base`, `IsSystem`,
`Includes` and `ParamDef.Options` — a printer bug there would pass every round-trip test. Now unknown nodes throw, and
those fields are dumped. New tests: a round-trip with all of them, and every mechanism of every sample (the largest real
scripts) through the round-trip. All pass: no printer bug was hiding.

### E-11 · BAIXO · the systems snap failed when the editor was built outside the source tree
`GameExport.FindPlayerProject` walks up from the exe to find `src\BuilderEngine.Player`; a build in `%TEMP%` has none,
so "dotnet publish builds the player" FAILed. Not a product bug (a release ships a prebuilt player); `Test-All.ps1`
builds into the repo's ignored `bin\test-all\`.

### E-12 · BAIXO · snap checks could pass by stopping early
Most snaps write only PASS/FAIL lines; an exception mid-way writes `error.txt`, and a snap that returns early writes
fewer PASS lines and no FAIL. `Test-All.ps1` fails a mode on `error.txt`, a missing checks file or fewer PASS lines
than the measured baseline. The snap now also runs only in the first editor window (`s_snapStarted`), so checks that
open more windows don't start a second snap.

### E-13 · BAIXO · the sample tests don't boot the world the way the editor and the player do (reported)
`DeepdelveTests.Play`, `EmberDuelTests.Sample.Play` and others build the world by hand (`new World`, `StarterKit`,
mechanisms) instead of `GameBoot.NewWorld` — Deepdelve's has no save folder and no prefabs. They test the sample, but
not what the player runs. The new soak and load→save→load tests use `GameBoot.NewWorld`. Recommendation: switch the
sample helpers to it.

### E-14 · BAIXO · in-game random numbers can't be seeded (reported, runtime area)
`World.Rng = new()` — two headless plays with the same input differ, so a failure seen in a long play can't be replayed
exactly. The soak prints its seed (input only). Recommendation: an optional seed on `World` for tests.

### E-15 · BAIXO · Ctrl+[ / Ctrl+] (draw order) on a Brazilian ABNT2 keyboard (reported, not verifiable here)
`OnSceneKey` matches `Key.OemOpenBrackets` / `OemCloseBrackets` (US positions). On ABNT2 the `[` and `]` keys are
different virtual keys (VK_OEM_6 / VK_OEM_5), so the shortcuts likely don't fire on the owner's keyboard. Couldn't be
tested without that layout. Recommendation: also match `e.KeySymbol` "[" / "]", or offer Page Up/Down variants.
The rest of `docs/SHORTCUTS.md` matches the handlers (read against `OnWindowKeyDown`/`OnSceneKey`; the doc ↔
`EditorShortcuts.All` sync is already a test).

### E-16 · BAIXO · AI panel: `_ = SendAsync(...)` drops an unexpected exception (reported)
`AiAssistant.RunAsync` turns `AiException` and cancellation into a result, but anything else (a bug in `AiMerge`, an
unwrapped `HttpRequestException` from a client) would end the fire-and-forget task unobserved: the busy state clears
and nothing is shown. Not reproduced; no generic catch added (owner rule). Recommendation: a test with a client that
throws a plain exception, then decide where it should surface.

### E-17 · BAIXO · build warnings
At the start: 28 (CA2255 ×26, the `ModuleInitializer` registration — suppressed with a justification by the data
audit, D-03 — and CS8604/CS8602 in `MainWindow.Link.cs:76` and `MainWindow.LinkSnap.cs:34`). The two nullable warnings
are in the link files (read-only for this audit): reported to the link agent. None added by this audit.

### Checked, no problem found
- **UI updates from background threads**: the only background work in the editor is `Task.Run` for Ollama's prepare
  (posts its note through `Dispatcher.UIThread.Post`), the AI assistant's log callback (posted), `Progress<T>` in the
  Ollama setup (created on the UI thread, so its callbacks run there), the export's `Task.Run` (data area; awaited,
  results applied after the await on the UI thread), `goto_scene` (`SceneRequested` is posted). Audio threads and the
  LiveLink watcher belong to other audits.
- **Flyouts and menus**: every `MenuFlyout` is filled before it can open (Project, scene ⋯, key picker); context menus
  (tools, library, prefabs, systems) always have items.
- **Modal dialogs and Esc**: `Dialogs`, AI settings, shortcuts, plugins, animation and sprite windows close on Esc;
  dialogs are modal (`ShowDialog`).
- **Switching projects** in one window keeps memory flat (20 switches, +0.2 MB).
- **Random long play of all four samples**: no script errors, no runaway spawning, no dependency on frame timing found;
  the only quirk was E-09.

## Test coverage gaps (not covered by any automatic test)
1. Editor UI logic runs only in `--snap` modes, not in the unit suite (the tests project references only
   `BuilderEngine.Script`); a snap needs a desktop session.
2. Real DPI scaling (125/150 %) — only DIP sizes are checked; text rendering at 1.5× isn't.
3. 2560×1440 and larger layouts on a smaller screen (Windows clamps the window).
4. The AI panel against real providers and the failure paths in E-16.
5. Drag and drop with a real pointer (library → scene, blocks) — snaps call the drop handlers directly.
6. Keyboard layouts other than US (E-15).
7. Audio playback threads and the exported player's window (only `--check` / `--snap` of the player).
8. Undo/redo across switching scenes while the inspector shows a prefab; the inspector after undo is checked only for
   added/removed mechanisms (`SnapChecks`).

## Files changed
- `src/BuilderEngine/MainWindow.LifecycleSnap.cs` (new): `--snap-only lifecycle`, also part of the full `--snap`.
- `src/BuilderEngine/WindowFit.cs` (new), `Program.cs`: fit the windows to the screen.
- `src/BuilderEngine/MainWindow.Prefabs.cs`: stop the prefab timer on close.
- `src/BuilderEngine/MainWindow.Scene.cs`, `MainWindow.cs`, `MainWindow.Projects.cs` (minimal): ask when closing can't save; edit bar wraps.
- `src/BuilderEngine/MainWindow.Tools.cs`: the TOOLS row wraps.
- `src/BuilderEngine/SpriteEditorWindow.cs`: unsaved drawing asks before closing.
- `src/BuilderEngine/MainWindow.SceneSnap.cs`: the lifecycle mode; the snap runs in the first window only.
- `src/BuilderEngine.Script/Runtime.cs` (cross-area, one line): `none` / `nothing`. `docs/MVP.md`: documented.
- `tests/BuilderEngine.Tests/AuditEditorTests.cs` (new), `Program.cs`: stronger round-trip dump, `--only <text>`.
- `tools/Test-All.ps1` (new).

## Open risks and recommendations (by priority)
1. E-07: lower the console's kept size (or virtualize it) — 36 ms per printed line is still visible in play.
2. E-15: check Ctrl+[ / Ctrl+] on the owner's ABNT2 keyboard.
3. E-13: move the sample tests onto `GameBoot.NewWorld`.
4. E-14: a seed for `World.Rng`, so a long-play failure can be replayed.
5. E-16: decide how unexpected AI errors surface, with a test.
6. Run `tools\Test-All.ps1` before every release; its minimum PASS counts are the baseline of this audit — raise them
   when snaps gain checks.

## Test totals (end of this audit, after merging main with the data and runtime audits)

`tools\Test-All.ps1`, all green, build 0 errors and 0 new warnings:

| Mode | Result |
|---|---|
| unit (`tests/BuilderEngine.Tests`) | 424 passed, 0 failed (406 from main + 18 new in `AuditEditorTests.cs`) |
| `--snap` (full editor run, now including lifecycle) | 169 PASS, 0 FAIL |
| `--snap-only qol` | 32 PASS |
| `--snap-only prefabs` | 16 PASS |
| `--snap-only systems` | 19 PASS (3 FAILed when built outside the source tree, E-11) |
| `--snap-only lifecycle` (new) | 21 PASS |
| Deepdelve (`--snap-only deepdelve`) | 6 PASS |
| Night Shift (`--snap` on the sample) | 6 PASS |
| Hollow Lantern (`--snap-only hollow`) | 6 PASS |
| Ember Duel (`--snap` on the sample) | 6 PASS |

The unit runner takes `--only <text>` to run a subset, and `--soak-minutes N` (default 10) for the sample plays.
The EasyEngine-link snap needs a `.eeproj` copy and isn't in `Test-All.ps1` (link agent's area).
