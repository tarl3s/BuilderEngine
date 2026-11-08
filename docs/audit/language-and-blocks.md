# Audit: the language front-end and blocks ⇄ code

Scope: the Builder Script lexer, parser, AST and printer (`src/BuilderEngine.Script/{Lexer,Parser,Ast,Printer}.cs`,
`EditorTools.Parse.cs`, `GameEvents.Parse.cs`), the blocks model and edits (`BlockEdits.cs`, `BlockUndo.cs`), and the
blocks view (`src/BuilderEngine/BlocksView*.cs`, `MainWindow.Blocks.cs`).

How it was checked:

- **Probes** of every lexer/parser edge case listed in the brief (strings, numbers, operators, indentation, line endings,
  BOM, empty files, brackets spanning lines, keywords as names, connectors, inline forms, error lines).
- **Corpus round-trip**: every Builder Script source in the repository — the 60+ embedded `.bs` files (starter kit,
  systems, the four sample games), the `.bs` files on disk, and the sources written as C# string literals in the
  tests (harvested automatically) — 332 sources in all. Each must satisfy `Dump(Parse(Print(Parse(s)))) == Dump(Parse(s))`, print
  idempotently, and keep every comment with its text. `Dump` covers every field of every node (named values, records,
  members, param options, includes, variants, events, comments), not the partial dump the older tests use.
- **Grammar fuzzer**: 3,000 random programs built as trees (every statement and value kind, comments, the names a block
  field accepts — including connector words, `seconds`, `each`, `key`, `until`) printed and parsed back; 4,000 random
  values printed and parsed back.
- **Block-edit fuzzer**: 4,000 rounds of random edits done the way the blocks view does them (palette insert, drag with
  the view's drop rules, delete, duplicate, ↑↓, ± else, typing in value and list fields, new when/to/param/uses, event
  fields) on every sample program; after each edit the printed code must parse back to the same blocks and every block
  must know its new line.
- **The real editor** (`--snap`, `MainWindow.Blocks.cs` → `SnapCodeAndBlocks`): typing then clicking a block, undo/redo
  through typed code, comments surviving a block edit, block lines after an edit. All 25 blocks checks pass.

Every fix below has a test in `tests/BuilderEngine.Tests/AuditLangTests.cs` (run by `Program.cs`). Totals after the
audit: **438 passed, 0 failed**; build: 0 errors, no new warnings (the two remaining are in `MainWindow.Link*.cs`, not
mine).

## Findings

Severity: CRÍTICO = can't use the engine or data loss; ALTO = breaks important features / instability; MÉDIO = wrong
behaviour in specific situations; BAIXO = minor.

### CRÍTICO

**C1. Deeply nested code crashed the whole editor (stack overflow) — fixed.**
`Parser.cs`. Values inside values (≈3,000 parentheses or brackets, `- - - …`, `not not …`), blocks inside blocks
(`run run run …`, 3,000 indented `if`s, a 3,000-long `else if` chain) and very long one-operator chains (`1 + 1 + … `,
`l[0][0]…`, which nest to the left as deep as they are long) were parsed — and printed, and walked by the blocks and the
game — by recursion with no limit. A stack overflow can't be caught in .NET: the process dies and unsaved work is lost.
Reported by the runtime auditor for the parser; I also found the printer overflowing on trees the parser accepted.
Fix: the parser counts nesting (`Deeper`, max `Parser.MaxDepth` = 200 levels, plus
`RuntimeHelpers.TryEnsureSufficientExecutionStack`, the same check the runtime uses) and the steps of one whole value
(`Longer`, max `Parser.MaxSteps` = 500 operators/items/members per value). Both throw a friendly `ScriptError` ("This
goes too deep… Split it into smaller steps with variables."). The step limit bounds the depth of the tree along any
side, so everything the parser accepts can be printed, edited as blocks and played (tested at the limits). A 300-term
formula (the runtime audit's "long but normal") still works; the runtime audit's 20,000-term case is now refused when the
code is read — I adapted its test (`AuditRuntimeTests.cs`, 4 lines; listed below).
Tests: "parser: code nested thousands deep is an error, not a crash of the whole editor", "parser: the deepest code
accepted still prints, round-trips and plays without running out of stack".

### ALTO

**A1. Any block edit deleted every comment of the mechanism — fixed.**
`Lexer.cs`, `Parser.cs`, `Ast.cs`, `Printer.cs`. The lexer threw comments away, the AST had no place for them, and a
block edit replaces the code with `Printer.Print(blocks)`: one click on a block and all the person's `# notes` were gone
(silently; only undo brought them back). The printer's own doc said "comments are not kept"; nothing told the user.
Fix: the lexer reports comments (`Lexer.Tokenize(source, comments)`, a `Comment(Line, Text, Trailing)` per comment); the
parser hangs them on the code next to them (`TakeComments`): whole comment lines go to the next piece of code
(`CodeComments.Before`), an end-of-line comment to the code that starts on that line (`CodeComments.After`), the comments
after the last line to the last mechanism (`MechanismDef.EndComments`). Statements, `when`s, `to`s, params, `uses`,
`includes` and mechanisms derive from a small `Commented` record. The printer writes them back in place. Moving or
duplicating a block moves/copies its comments with it. The game never reads them.
Known, harmless moves (text kept): a comment inside a value spanning lines, or at the end of a block, goes above the
next line of code; comments above a plain `else` go to its first line; an inline `if c: x  # note` with an `else` prints
in block form with the note on the `if` line; comments of a `run` and its one statement share their line.
Tests: "printer: comments stay where they were written" (exact text), "printer: a commented block moved into a 'run'…",
the corpus and fuzz round-trips (which compare comments too), and the snap check "the comment typed in the code
survives the block edit". Updated: the old "printer: exact canonical form" test, which expected the comment to vanish.

**A2. A block edit right after typing printed the older blocks over the newer code — fixed.**
`BlocksView.cs`, `MainWindow.Blocks.cs`. Code typed in the code view reaches the blocks after a 150 ms pause; until then
the blocks show (and accept edits on) the previous version. An edit in that window — or a pending redraw firing after
the code had just stopped parsing, which also unlocked the greyed blocks and hid the error banner — reprinted the old
blocks over the code, losing what was just typed. Fix: `BlocksView.Expect()` locks the blocks (no clicks, drags, undo)
until `Show` runs; a redraw still waiting is dropped when the code stops parsing (`MainWindow.Blocks.cs`, the
`TextChanged` handler). An edit can't happen while locked; if a field that was already open commits, it clears the lock.
Test: snap checks "right after typing the blocks take no edit…", "after the pause the blocks show the typed code".

### MÉDIO

**M1. `if a: repeat 3`, `if a: if b: c` and a stray `else` parsed as commands — fixed.** `Parser.cs`
(`ParseSimpleStmt`). The one-line form after `:` takes one simple statement; `repeat`/`if` there (and an `else` that
doesn't follow an `if`) were read as commands named `repeat`/`if`/`else`. The editor said "No problems"; the game failed
with "There's no command called 'repeat'" only when it reached the line. Now they are parse errors with a clear message
(`'repeat' needs its own lines…`, `'else' goes right after the lines of an 'if'…`). Such scripts already failed whenever
they ran. Tests: "parser: 'if' / 'repeat' after a ':' are errors…", "parser: a stray 'else' is an error…".

**M2. `a == not b` silently became two values — fixed.** `Parser.cs` (`ParseAtom`). After an operator, `not` was read as
a variable called `not`; in a command (`print a == not b`) the `b` became one more value. Now an error asking for
`a == (not b)`. `not a == b` keeps meaning `not (a == b)`. Test: "parser: 'a == not b' is an error…".

**M3. Exponents: `1e3` was read as `1` and a variable `e3` — fixed (supported).** `Lexer.cs`. In a command
(`say 1e3`) that was silently two values; elsewhere an odd error. Numbers now take an exponent (`1e308`, `2.5E-4`,
`1e+3`) — only an `e` followed by digits (a sign may come first), so `3times`, `2e`, `x = 2` followed by a name read as
before. The printer still writes plain digits, which older versions read too. Test: "lexer: numbers with an exponent…".

**M4. Command values starting with a connector word printed into different code — fixed.** `Printer.cs` (`Command`).
The parser reads `to/in/at/by/with/from/ease/over/as/then` followed by anything as the start of a named part, and skips
`seconds` after a named number. A value typed in a block like `to`, `in + 1`, `at(1)`, `from.x`, or a `seconds` after a
named number, printed as written and read back as something else (or not at all) — the blocks silently changed meaning.
Such values now get parentheses (`move (to), (in + 1) by 2, (seconds)`); a lone word at the very end stays bare
(`say to`). Test: "printer: command values starting with a connector word get parentheses" + fuzzers.

**M5. A block holding a `return` could be dragged out of its `to` — fixed.** `BlocksView.Drag.cs`, `BlockEdits.cs`. The
drop rule only refused a bare `return` outside a `to`; an `if` with a `return` inside could be dropped into a `when`,
printing code that doesn't parse. New `BlockEdits.HasReturn` / `CanPlace` (also "never inside itself"), used while
dragging and again on drop. Test: "blocks: a block holding a 'return' can only go inside a 'to'…" + block fuzzer.

**M6. A `stop sound` block's values could be edited into code that doesn't parse — fixed.** `BlocksView.Commands.cs`,
`BlockEdits.cs`. `stop sound "x"` is a command whose first value must be `sound`/`sounds`; the values field accepted
anything, and `stop "x"` reads as a plain `stop` with values left over. `BlockEdits.CheckCommandValues` makes the field
refuse it (red, with the reason). Found by the block fuzzer. Test: "blocks: a 'stop sound' block's values…".

**M7. `wait for key` with a value starting with a symbol didn't parse — fixed.** `Parser.cs` (`ParseWait`). `wait for key
(k)`, `wait for key -1` (what the printer writes for those values, e.g. from the blocks' key field) went down the
signal path and failed. Only `.` after `key` means a signal now (`wait for key.pressed`). These were errors before, so no
valid script changes meaning. Test: "parser: 'wait for key' takes any value…".

**M8. A repeat count starting with `forever` printed as `repeat forever` — fixed.** `Printer.cs` (`RepeatCount`):
`forever + 1` typed in the count field printed `repeat forever + 1`. It now gets parentheses. Test: "printer: a repeat
count starting with 'forever'…".

**M9. After a block edit, breakpoints and the debugger's highlight used the old line numbers — fixed.** `BlocksView*.cs`,
`BlockEdits.cs`. A block edit reprints the code (other lines, and new blocks had line 0) but the blocks kept the lines of
the last parse, which only happens when the code view changes: "Pause here (line N)" set the breakpoint on the wrong
line, new blocks had no ⋯ menu, the debugger outlined the wrong block. `BlockEdits.LinesAfterPrint` reads the printed
code and maps each block to its line; the view uses `LineOf(block)`, and the ⋯ menu reads the line when it opens.
Tests: "blocks: after an edit each block knows its new line…", the block fuzzer (every block, every edit), snap "every
block knows its line in the printed code".

**M10. A duplicated block was the same object as the original — fixed.** `BlockEdits.Clone` returned simple statements
as-is (they are immutable). The view tells blocks apart by reference (lines, the block to scroll to, the debugger's
outline), so both copies were "the same block". Clone now makes a new record. Found by the block fuzzer. Test: "blocks:
a duplicated block is a new block…".

**M11. Undo in the blocks jumped over code typed since the last block edit — fixed.** `BlocksView.cs`. Undo snapshots
were only taken for block edits: block edit, then typing, then Undo put back the text from before the block edit — the
typed code vanished (redo had it, until the next block edit cleared redo). Typing between block edits is now an undo
step (one step for all the typing until the next block edit; never across mechanisms, never for code that didn't parse).
Tests: "blocks: undo steps through code typed between block edits…" (BlockUndo), snap "undo takes back the block edit
only", "undo again takes back the typing", "redo brings the typed code back".

### BAIXO

- **B1. A byte order mark at the start was an error** ("I don't understand the symbol"), e.g. text pasted from some
  editors. Skipped now (`Lexer.cs`). Test: "lexer: a byte order mark…".
- **B2. A 400-digit number was read as infinity and printed as the name `Infinity`.** Now "The number … is too big"
  (`Lexer.cs`; `1e400` too). Test: "lexer: a number too big…".
- **B3. Numbers printed inexactly.** `1.2345678901234567e22` printed with 15 digits (a different number), `3e-30` printed
  as `0` (`Printer.Number` used a custom format). Now the shortest exact digits, without exponent. Test: "printer: numbers
  come back exactly…".
- **B4. An emoji outside quotes was reported as half a character** (`'�'`). Now the whole symbol (`Lexer.cs`).
- **B5. `stop sound "x"` printed as `stop sound, "x"`** after a block edit. Prints as written now (`Printer.cs`).
- **B6. Renaming a `to` block didn't rename a call inside `wait for key …`** (`BlockEdits.RenameExprs`). Fixed.

### Found and not fixed (with reasons)

- **N1 (MÉDIO). Renaming a `to` block only renames its calls in the same mechanism**, not calls from other mechanisms nor
  `Mech.name` calls; those fail when played ("has no command called…"). Fixing it means editing other mechanisms'
  sources from the blocks view (a cross-file change owned by the project/library code). Recommendation: rename across
  `_sources` in `MainWindow` with the same `RenameCalls`, as one undoable step.
- **N2 (BAIXO). Printing normalizes layout**: positional values are separated by commas (`show bar, hp, of, max`,
  `spawn Coin, here`), blank lines collapse, `to` commands print after the `when`s and params before `uses`, the reading
  words `seconds`/`times` are dropped. Meaning is kept (tested); the text the person wrote changes after a block edit.
  A space instead of a comma is only safe between some values and the printer doesn't know which commands take "word"
  values; recommendation: keep the source order of whens/procs in one list in the AST if it matters to users.
- **N3 (BAIXO). Silent last-wins**: `uses Health max 2, max 3` keeps 3; `move a to 1 to 2` keeps `to 2`. Making them errors
  could stop old projects from loading; left as is.
- **N4 (BAIXO). Not in the grammar**: `(a).b`, `(a + b).x`, `a.b.c(1)`, `3[0]`, assigning to an item or a member of a call
  (`l[0] = 1`, `find("x").hp = 3`); the errors are generic ("Expected a value … but found '='"). Variables named like a
  connector can't start a command value (`say at + 1`) and `and`/`or` still work as names (`say and`). Comparisons chain
  to the left (`1 < x < 3` is `(1 < x) < 3`), unlike Python. Design choices; worth a friendlier message for `l[0] = 1`.
- **N5 (BAIXO). One error at a time**: the parser stops at the first error (no recovery). Fine for small mechanisms.
- **N6 (BAIXO). Tabs count as 4 columns** and mixing tabs and spaces is accepted when the columns match.
- **N7 (BAIXO). Hand-built trees the parser never makes** can print unparseable code (`MemberExpr` on a plain name or a
  record prints `(a).b`; an empty named part at the end of a command). Nothing builds them today.
- **N8 (BAIXO). `BlockEdits.ParseEvent` builds `when <kind> <tail>:`**; a tail with `#` or `:` gives odd messages ("must be
  indented"). Value fields accept and drop a trailing `# comment`.

### Existing catch blocks in these files

- `BlocksView.Restore` returns silently on a `ScriptError`; it can't happen (every undo snapshot is code that parsed — I
  kept that true for the new typed-code steps).
- `MainWindow.BlocksProjectDefs` skips other mechanisms that don't parse (MY COMMANDS just doesn't list theirs); their
  errors are shown when they are opened.
- `BlocksView.Show` on code that doesn't parse empties the blocks instead of keeping the last good ones; the window never
  calls it with such code (it uses `SetStale`), so it's unreachable today.
- New: `BlockEdits.LinesAfterPrint` returns null on a `ScriptError` (the code view reports that error; the blocks keep
  their old lines).

## AST coverage

Every expression (`Number, String, Bool, Name, Unary, Binary, Call, List, Index, Record, Member`) and statement (`Assign,
If, Repeat, Wait, WaitUntil, WaitSignal, WaitKey, Emit, Tell, Stop, Command, Together, Run, ForEach, While, Return`) kind
is handled by the runtime, the printer, `BlockEdits` (bodies, clone, rename) and the blocks view (a block for each; a
plain-text block as the fallback). Information the tree doesn't keep: layout (spacing, blank lines, comma vs space,
`times`/`seconds`, `<>` becomes `!=`, quote style), and the relative order of `when`s and `to`s.

## Changes outside my files (minimal, listed as asked)

- `tests/BuilderEngine.Tests/AuditRuntimeTests.cs`: the 20,000-term formula is now refused when the code is read, not at
  run time — the test expects that `ScriptError` (4 lines). Its "300 terms still works" half is unchanged and passes.
- `tests/BuilderEngine.Tests/Program.cs`: one call line, and the "exact canonical form" test now expects the comment kept.

## Open risks and recommendations

- **Deep blocks in the UI**: 200 nested blocks are accepted; the Avalonia layout of that many nested C-blocks wasn't
  measured (not testable headless). If it proves heavy, lower `Parser.MaxDepth` for statements.
- **Comment placement** follows simple rules (above → next code, end of line → that line). Unusual placements move (text
  kept). If users rely on comments between `if` and `else`, consider an `ElseComments` slot.
- The blocks still reformat the whole mechanism on the first block edit (N2). With comments kept this is cosmetic, but a
  "minimal text edit" (replace only the changed statement's lines) would be the next step for code-first users.
- Keep `AuditLangTests` growing with the language: the corpus test picks up every new `.bs` and test source by itself,
  and the fuzzers cover new node kinds once added to the generator and the `Dump`.
