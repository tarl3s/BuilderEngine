# Audit: interpreter, runtime, engine and performance

Scope: the Builder Script interpreter (`Runtime*.cs`), events / signals / coroutines / `together` / `run`, the engine
(`World*.cs`, `TileMap.cs`, physics, tiles, camera, scenes, sprites, runtime prefabs), the debugger's runtime side,
`GameBoot`, the play surface (`PlaySurface*.cs`, `ImageCache.cs`, `Audio.cs`) and performance.
Branch `audit/runtime`. Every fix has a test in `tests/BuilderEngine.Tests/AuditRuntimeTests.cs` unless it says otherwise.

Method: each suspicion was first reproduced, either in a scratch console program that ran the script in a child
process (needed for stack overflows, which can't be caught) or as a failing test. Measurements come from a scratch
benchmark (Release, best of 5 runs) and from the test suite itself (Debug). The machine was shared with other agents
running suites, so timings are noisy; the allocation figures are exact.

Severity: **CRÍTICO** = unusable or data loss, **ALTO** = breaks important features / instability,
**MÉDIO** = wrong in specific situations, **BAIXO** = minor.

## Summary

| Severity | Found | Fixed | Left open (reported) |
|---|---|---|---|
| CRÍTICO | 3 | 3 | 0 in this area (1 for the language auditor, see R1) |
| ALTO | 4 | 4 | 0 |
| MÉDIO | 5 | 5 | 0 |
| BAIXO | 14 | 7 | 7 |

Tests: 365 before → 388 after, all passing (23 new in `AuditRuntimeTests.cs`).

## CRÍTICO

### C1. A 'when' that tells or emits itself closed the program (stack overflow)
- **Files**: `Runtime.cs` (`StartWhen`, `Advance`).
- **Cause**: `tell` and `emit` start the handler right away (until its first `wait`) on the same C# stack. A handler
  that tells itself again (`when told "hit": … tell self "hit"`, or `when died: emit died`) recursed without limit.
  The process died with `Stack overflow` — the editor too, losing unsaved work. The AI check runs scripts on a
  background thread, so an AI answer with such a loop also killed the editor.
- **Proof**: reproduced in a child process: `Stack overflow. at World.Assign…` (tell) and `Repeat 605 times` (emit).
- **Fix**: `World.MaxEventNesting = 100`. `Advance` counts how deep it is nested; `StartWhen` refuses to start a
  101st nested 'when' with a script error on the handler's line ("…was started inside itself more than 100 times at
  once… Add a 'wait' or an 'if' that stops it."). The error ends only the innermost 'when'; the outer ones finish
  normally, other objects keep running, one error is reported.
- **Tests**: "a 'when told' that tells itself…", "an 'emit' whose handler emits again…".

### C2. A list or record that contains itself closed the program
- **Files**: `Runtime.cs` (`ToText`, `AreEqual`), `World.Records.cs` (`CopyValue`, `Record.ToString`, `Record.Copy`),
  `World.Save.cs` (`ToJson`), `Debugger.cs` (`Show`).
- **Cause**: printing, comparing, copying, saving and showing values recursed into lists/records with no cycle check.
  Cycles are easy to make with records (a card whose `owner` holds the card in its `hand`) or `add hand to hand`.
  Worse, the editor draws every global with `ToText` each frame, and the debugger panel shows values: one cyclic
  global crashed the editor while merely playing.
- **Proof**: child-process runs of `print p`, `a == b`, `copy(a)`, `save "h", hand` → `Stack overflow` each.
- **Fix**: one nested-value printer (`AppendNested`) shared by lists and records that tracks the containers on the
  current path: a container already on the path prints as `[…]` / `{…}`, and nesting deeper than
  `MaxValueDepth = 64` too. `AreEqual` stops at the same depth (deeper lists are equal only when they are the same
  list). `CopyValue` keeps cycles as cycles (the copy holds its own copy; acyclic data is copied exactly as before)
  and checks the stack for absurd depth. `ToJson` raises a script error for a cycle ("A list or record that contains
  itself can't be saved") or more than 60 levels (System.Text.Json's own limit is 64). `Debugger.Show` stops lists at
  4 levels. Output for ordinary values is byte-for-byte unchanged.
- **Tests**: "a record that holds itself … prints, compares and copies", "saving a list inside itself…",
  "lists nested thousands deep print, compare and copy without crashing".

### C3. A long formula closed the program
- **Files**: `Runtime.cs` (`Eval`, `CheckStack`), `Runtime.Lang.cs` (`ProcContext`).
- **Cause**: `Eval` recurses once per operator. The parser builds `1 + 1 + …` iteratively, so a formula of ~2,000
  terms (Release; fewer in the Debug build the editor runs) parsed fine and then overflowed the stack when run.
  Generated code (AI, blocks, plugins) can reach that.
- **Fix**: `CheckStack` calls `RuntimeHelpers.TryEnsureSufficientExecutionStack()` for composite expressions (not for
  literals/names, which are most evaluations) and when a user command starts; when the stack is nearly used up it
  raises a script error ("This goes too deep…") instead of crashing. Normal formulas are unaffected.
- **Test**: "a formula thousands of terms long is an error, not a crash" (20,000 terms; 300 still work).

## ALTO

### A1. A loop that never waits froze the editor for good
- **Files**: `Runtime.cs` (`Exec`, `CountLine`, `Advance`, `Step`), `World.Scenes.cs`.
- **Cause**: `while` and `repeat forever` already pause one round per frame, but `repeat 1000000000`, nested
  `for each` over big ranges, or a value-command with a loop run on the UI thread with no way out: the person can only
  kill the editor. The AI check's guard only counts commands, so an AI answer with such a loop of assignments kept a
  background thread spinning forever.
- **Fix**: `World.MaxLinesWithoutWait` (default 10,000,000 ≈ 2–5 s of work; settable, kept by `ForNextScene`). Each
  statement increments a counter that resets when the outermost `Advance` starts and at the start of each step; going
  over raises a script error on that line suggesting a `wait`. Measured throughput: ~4 million statements/s (Release),
  so a long one-off job such as world generation (200,000 iterations tested) is far below it.
- **Tests**: "a loop that never waits stops with an error…", "the line budget is per 'when' at a time…".

### A2. Printing every frame (or one broken 'when tick') made the editor crawl
- **Files**: `MainWindow.cs` (`Log`, **cross-area, minimal**), `Runtime.cs` (`CoroutineError`).
- **Cause**: `Log` did `_console.Text = _console.Text + line + "\n"` on a `TextBlock`: every line copies and lays out
  the whole text again (quadratic). A beginner's `when tick: print self.x`, or a typo in a `when tick` that errors
  every frame (60 "⚠" lines a second), slowed every frame down until the editor was unusable.
- **Fix**: the console keeps about its last 40,000 characters (trimmed at a line break when it passes 60,000). The
  same runtime error repeated (same text) is still added to `Errors` every time — the badge count and tests are
  unchanged — but printed once, and the repeats reuse the first string, so they cost a list slot, not a new string.
- **Measured** (string work only, 10 simulated minutes of one print per frame): 6,010 ms total and a 906 KB text →
  491 ms and 46 KB; the layout cost of the TextBlock shrinks the same way.
- **Tests**: "the same error every frame is counted every time but printed once". `MainWindow.Log` is UI code the test
  project can't reach; verified by build and the benchmark above.

### A3. A save the disk refused stopped the whole game
- **Files**: `World.Save.cs` (`WriteSlot`, `ReadSlot`, `ClearSave`).
- **Cause**: `File.WriteAllText`/`File.Move` exceptions (disk full, read-only folder, a file locked by a sync app — the
  owner's projects live under OneDrive) were not script errors: they escaped `Step`, and the play surface stopped the
  game ("The game stopped: …"). `ReadSlot` did not catch `UnauthorizedAccessException` either.
- **Fix**: IO/permission failures become script errors on the `save game` / `save` / `clear save` line ("The game
  couldn't be saved to “save”: …"); the game goes on and the previous save is untouched (it was already written to a
  `.tmp` first). A missing saves folder on `clear save` is still "nothing to clear". A save that can't be read, or is
  damaged JSON, now prints one warning ("…is damaged…: playing without it") instead of silently reading as no save.
  Line-0 errors from commands and functions now get their line (see B3), so the message says where.
- **Tests**: "a save that can't be written is an error on its line; the game goes on", "a damaged save is reported
  once and reads as no save".

### A4. The interpreter allocated ~1.7 KB per 'when tick' run
- **Files**: `Runtime.cs`, `Runtime.Lang.cs`, `World.Broadphase.cs`.
- **Cause** (found by measuring each statement kind separately):
  - every unresolved name (any global, `flag` in `wait until flag`, a mechanism name like `Health`) walked
    `AllMechanisms()` — nested iterators allocated on every read; `other.Health` did the same plus a LINQ closure;
  - `Ctx` was created with a `Locals` dictionary and then given another one (two dictionaries per 'when' run);
  - every dotted assignment (`self.x += …`) copied its path (`a.Target[..^1]`);
  - every comparison and every number literal boxed a new object;
  - the lambdas for `wait until` / `wait for` / `wait for key` / breakpoints captured `Exec`'s locals, so every run of
    every block allocated a closure object even when no wait was reached;
  - `emit` copied the object's mechanisms into a new array each time.
- **Fix**: lookups by name use the object's cached flat mechanism list (`Flat`), made once at `Spawn` after every
  mechanism exists (while an object is still being built — a param default naming another mechanism — the slow path
  is used and nothing is cached, see the test); one dictionary per context; `Resolve(path, count)`; cached boxes for
  `true`/`false` and whole numbers −128…1024; the wait lambdas moved into small helper methods; `emit` uses `Flat`.
  No behaviour change.
- **Measured** (5,000 objects, two 'when tick' mechanisms each, `Frame` at 60 fps):

  | | Release before | Release after | Debug before | Debug after |
  |---|---|---|---|---|
  | bytes per 'when tick' run | 1,664 | 867 | 2,088 | 1,227 |
  | ms per frame (best of 5) | 18.96 | 10.18 | 40.7 | 24.4 |

  Per statement (Release, bytes per tick run): `a = 1` 712 → 528, `y = Health.hp` 928 → 528, `g = score` (a global)
  904 → 528, `emit ping` 1,136 → 664. 10,000 coroutines in `wait until flag`: 1,719 KB/frame → 312 KB/frame
  (≈172 → 32 bytes per check). 20 objects computing `fib(12)` each tick: 14.5 MB → 11.2 MB per frame.
- **Tests**: "the interpreter allocates less per 'when tick' (5000 objects)" (< 1,600 B; the old code fails it in both
  builds), "10000 'wait until' checks per frame allocate almost nothing", plus the existing 2,000-object budget test.

## MÉDIO

### M1. `nothing == false` was false (while `nothing == 0` and `false == 0` were true)
- **File**: `Runtime.cs` (`AreEqual`). An unset variable "reads as nothing (0 / false / empty text)", but equality
  only honoured 0 and "". `if opened == false` before `opened` was ever set skipped its branch.
- **Fix**: nothing equals nothing, "", 0 and false. Also, comparing a number with a text that isn't a number no longer
  throws and catches an exception per comparison (`TryNumber`), which was slow in a per-frame `if`.
- **Test**: "nothing equals false, like it equals 0 and \"\"…" (also checks `"1" == 1`, `"abc" == 0`, `nothing == true`).

### M2. NaN / infinity positions lost the object and slowed physics
- **File**: `Runtime.cs` (`Assign`). `self.x = sqrt(-1)` made x NaN: the object vanished for good, and a NaN body made
  the tile collision scan every row of the map each frame (`(int)NaN` is `int.MinValue`, and `int.MinValue - 1`
  wraps to `int.MaxValue`, so the clamp became "all rows").
- **Fix**: assigning a non-finite value to `x`, `y`, `width`, `height`, `vx` or `vy` is a script error on that line;
  the old value stays.
- **Test**: "x, y, size and speed can't become NaN or infinity…".

### M3. Play surface kept every spawned object and every gone HUD text
- **File**: `PlaySurface.cs` (`BrushFor`, `ForgetGoneHudTexts`).
- **Cause**: `_brushes` cached a brush per spawned `GameObject` and `_hudTexts` a laid-out text per `HudItem`; neither
  was ever pruned, so every bullet and every enemy's floating text stayed referenced (with its mechanisms and
  variables) for the whole play session.
- **Fix**: spawned objects look their colour up by mechanism each draw (a dictionary lookup) instead of being cached;
  HUD texts whose item is no longer in `World.Hud` are dropped once there are clearly more than shown.
- **Test**: none automatic (Avalonia UI class outside the test project); verified by build and code review. The
  world itself was measured flat over 10 simulated minutes (see M5).

### M4. `find()` returned an object spawned and destroyed in the same step
- **File**: `Runtime.cs` (`FindObject`): the pending list wasn't checked for `Destroyed`. Now it is, and the LINQ
  (two closures per call; `find` and the camera's per-frame player lookup use it) became plain loops.
- **Test**: "find() doesn't return an object spawned and destroyed in the same step".

### M5. Long play: unbounded `Output`, template cache
- **Files**: `Runtime.cs` (`AddOutput`, `MaxOutputLines`), `World.Audio.cs`, `World.Engine.cs`, `World.Tools.cs`.
- **Cause**: every `print` and every `play` added a line to `World.Output` forever (exported games have no reader for
  it). Texts built at run time with `{…}` (`"HP " + hp + " {x}"`) were each cached forever by `Interpolate`.
- **Fix**: `Output` keeps the newest 20,000 lines (trimmed by half when full, so adding stays O(1)); the template
  cache is cleared past 2,000 entries.
- **Measured**: 3 guns firing bullets that live 0.5 s, 10 simulated minutes: managed heap 27,795 KB → 27,810 KB,
  objects and running 'when's flat; destroyed objects' coroutines and signal waiters are removed every step.
- **Tests**: "ten simulated minutes of spawning and destroying…", "Output keeps the newest lines only…".

## BAIXO — fixed

- **B1. `-0` shown as "-0"** (`0 * -1`, `vx = -vx` at rest; .NET Core formats negative zero with a sign).
  `ToText` and a new shared `World.ShortNumber` (used by `{x}` in texts, labels and the globals overlay) print 0.
  Test: "-0 reads as 0…".
- **B2. Errors inside `{…}` in texts were swallowed silently** (`World.Engine.cs`): the text still shows "?", and now
  the console says why, once. Test: "a mistake inside {…} in a text shows '?'…".
- **B3. Errors from functions and commands said "line 0"**: `CallFunction` and `RunCommand` attach the calling line
  to a line-0 script error (plugins and many built-in functions throw with 0). Test: "errors inside functions say
  their line".
- **B4. Camera shake used a shared `System.Random` from several threads** (the AI check steps worlds on a background
  thread; `Random` isn't thread-safe and can get stuck returning 0). Now `Random.Shared`. No test (a race).
- **B5. `Record.ToString`/`Copy` duplicated the list logic** — now go through `World.ToText` / `World.CopyValue`
  (one implementation, needed for C2).
- **B6. A param default naming a mechanism listed later**: with the new cached lookup this would have hidden that
  mechanism's 'when's; guarded (`FlatIfMade`) and tested ("a param whose default names a mechanism listed later…").
- **B7. One error per level for nested failures** is now one error (C1).

## BAIXO — not fixed (reported)

- **O1. `for each` variable stays in the 'when's locals after the loop** (`Runtime.cs`): if it has the name of a
  mechanism variable, later assignments in the same 'when' go to the local. Undocumented either way; changing scope
  could break scripts that read the last item after the loop. Recommendation: document it, or restore the previous
  binding after the loop in a language revision.
- **O2. `round(2.5)` is 2** (`Math.Round` defaults to banker's rounding; `-7 % 3` is -1). Beginners expect 3.
  Recommendation: `MidpointRounding.AwayFromZero` after the owner agrees (it changes existing games' numbers).
- **O3. `saved("x")` for a value kept only by `save game` reads and parses the save file on every call**
  (`World.Save.cs`); in a per-frame HUD that is disk I/O 60 times a second. Values saved with `save` are cached.
  Recommendation: cache the default slot's JSON, invalidated by writes.
- **O4. `ImageCache.Get` catches every exception while decoding a picture** and draws a coloured box with no message
  (`ImageCache.cs`). It doesn't crash, but a corrupt picture is invisible to the person. Recommendation: remember the
  failure text and let the host print it once (needs a small host hook in MainWindow).
- **O5. The audio thread ignores every exception** (`Audio.cs`, `catch { }`). MCI reports problems through return
  codes, so this only hides interop failures (a missing winmm). Acceptable; could log once.
- **O6. A `wait for` whose signal never comes keeps its waiter** until the waiting object is destroyed, also when the
  'when' was abandoned by a `return` in another `together` branch. Bounded and small.
- **O7. A key pressed in the same step an object is spawned doesn't reach it** (`PressKey` looks only at joined
  objects). Negligible.

## Checked and found correct

- Error isolation: a script error ends only its own 'when'; other 'when's in the same step still run
  (test "an error in one 'when' doesn't stop the others…"). `stop` inside `together` ends the whole 'when'; inside
  `run` only the background task.
- Coroutines of destroyed objects are removed every step; destroyed objects aren't moved, touched, drawn or sent keys.
  Spawning during iteration goes to a pending list; index loops are bounded to the count at the start.
- Touch events: the grid broadphase gives the same pairs as all-pairs (existing test), fires once per new contact in
  the old order; destroyed-mid-frame objects are skipped.
- `goto_scene` builds a fresh `World` (`ForNextScene`): old coroutines are dropped with the old world; globals, host
  commands, plugins (re-installed by `World.Installers`) and settings carry over.
- Timers use game time (slow motion scales them, pause stops them); real-time effects use `RealTime`. While paused the
  'when tick's still run with `dt = 0` — by design, so a pause menu can read keys.
- Physics: swept moves against solids and solid tiles (no tunnelling at any speed tested); camera clamp centres views
  larger than a small scene; animations advance on game time, so variable `dt` is fine. Engine overhead with no
  scripts: 5,000 objects 0.31 ms/frame, 5,000 solids + 100 bodies 0.88 ms/frame, 1000×500 tile map + 500 bodies
  0.24 ms/frame, 2,000 touching objects (4,996 pairs) 1.36 ms/frame, all with ~0 allocation per frame.
- Thread safety: all MCI work runs on the audio thread's queue (its state is only touched there); LiveLink and the
  debugger post to the UI thread; the AI check uses its own `World` and its own save folder. Shared statics used by
  worlds: `WhenIndex.Cache` (ConditionalWeakTable, thread-safe), `World.Installers` (filled by module initializers
  before any world), the camera RNG (fixed, B4), `MemorySaves` (only without a save folder; the AI check sets one).

## Cross-area notes

- **R1 (language auditor, CRÍTICO there)**: the parser itself overflows the stack on deeply nested input —
  3,000 nested parentheses or list brackets close the program while parsing (`Parser.cs`, recursive descent). Same
  remedy as C3: `RuntimeHelpers.TryEnsureSufficientExecutionStack()` in the recursive parse functions. `Printer` and
  the blocks view recurse over expressions the same way.
- **R2 (language auditor)**: `1e308` isn't a number literal; it lexes as `1` followed by the name `e308`, so
  `print 1e308 * 10` prints "1 0".
- **R3 (AI area)**: `AiCheck` could set a small `World.MaxLinesWithoutWait` (e.g. 200,000) so a runaway loop in an AI
  answer is reported at once instead of after the 5-second timeout.
- **Changed outside my files**: `src/BuilderEngine/MainWindow.cs` — only `Log` (console trimming, A2).

## Open risks

- The line budget (A1) is a heuristic: a script that legitimately runs more than 10 million lines in one go without
  waiting now stops with an error. That is ~2–5 seconds of frozen game; the message tells how to fix it, and the budget
  is a property the host can raise.
- `CheckStack` relies on .NET's "sufficient stack" threshold; very large stack frames between two checks (a plugin
  command recursing on its own) are not covered.
- Two cyclic lists with the same shape compare as different (only the same list is equal past the cycle). Python
  raises an error there; this is the safer choice for a game.
- Timings in this document were taken on a machine shared with other test runs; the allocation numbers are exact.

## Recommendations

1. Keep `AuditRuntimeTests` in the suite: the crash tests guard against regressions that would close the editor.
2. When adding a recursive helper over script values, reuse `AppendNested` / `CopyNested` (or their path check);
   when adding recursion over syntax, call `CheckStack`.
3. Next interpreter step if more speed is needed: the `Exec` iterator object (~300 bytes per block run) is now the
   largest per-tick allocation; splitting rarely used statement kinds into their own iterators or caching a
   per-'when' context would cut it further. Measure with the same workload before changing it.
4. Consider O2 (rounding) and O1 (loop variable scope) for the next language revision, with the owner.
