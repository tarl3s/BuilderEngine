# Keyboard shortcuts and editor conveniences

Press **F1** (or the **?** button in the top bar) to see this list inside BuilderEngine. The list in the app comes from
`src/BuilderEngine.Script/EditorShortcuts.cs`; a test checks that every line there is also here.

## Everywhere

| Keys | What |
|---|---|
| `F1` | Show this list of shortcuts |
| `F5` | Play the open scene (while playing with the debugger: pause / resume) |
| `Esc  or  Shift+F5` | Stop playing |
| `Ctrl+S` | Save the project |
| `Ctrl+O` | Open a project |
| `Ctrl+Z  /  Ctrl+Y  (or Ctrl+Shift+Z)` | Undo / redo — the scene's in the Scene tab, the blocks' in the blocks, the text's while typing |

## Scene: select

| Keys | What |
|---|---|
| `Click` | Select an object (locked objects can't be clicked: pick them in the scene's object list) |
| `Shift+click  or  Ctrl+click` | Add an object to the selection, or take it out |
| `Drag on empty space` | Selection box: selects every object it touches (Shift or Ctrl keeps the others) |
| `Ctrl+A` | Select every object (not the locked ones) |
| `Esc` | Cancel a drag in progress, else stop the tool, else select nothing |
| `Double-click  or  F2` | Rename the selected object (with nothing selected: rename the scene) |
| `Right-click` | Menu: copy, paste here, duplicate, delete, rename, order, lock, original size |

## Scene: move and size

| Keys | What |
|---|---|
| `Drag` | Move the selected objects (they snap to the grid) |
| `Alt while dragging` | Don't snap to the grid |
| `Shift while dragging` | Move only across or only up and down |
| `Arrow keys` | Move the selection 1 pixel |
| `Shift+arrow keys` | Move the selection one grid cell (8 pixels) |
| `Drag a handle` | Resize the selected object |
| `Shift while resizing` | Keep the proportions (the picture's, else the box's) |

## Scene: edit

| Keys | What |
|---|---|
| `Delete  or  Backspace` | Delete the selected objects |
| `Ctrl+D` | Duplicate the selected objects |
| `Ctrl+C  /  Ctrl+X` | Copy / cut the selected objects |
| `Ctrl+V` | Paste at the mouse pointer (or a little further each time); works in another scene too |
| `Ctrl+L` | Lock or unlock the selected objects |
| `Ctrl+Shift+]  /  Ctrl+Shift+[` | Bring to front / send to back |
| `Ctrl+]  /  Ctrl+[` | Bring forward / send backward one step |

## Scene: view

| Keys | What |
|---|---|
| `Ctrl+wheel` | Zoom at the mouse pointer |
| `Wheel  /  Shift+wheel` | Scroll up and down / sideways |
| `Middle-drag  or  Space+drag` | Pan the view |
| `Home  or  Ctrl+0` | Fit the whole scene in view |
| `Ctrl+1` | Zoom to 100% |
| `Ctrl++  /  Ctrl+-` | Zoom in / out |
| `Ctrl+PageUp  /  Ctrl+PageDown` | Edit the previous / next scene |

## Fields and lists

| Keys | What |
|---|---|
| `Wheel over a field you clicked into` | Change the number live (Shift = ×10, Ctrl = ×0.1); a burst of notches is one undo step |
| `Type in an open dropdown or menu` | Search it: only matching entries stay; Enter picks the first, Backspace edits, Esc clears |
| `Enter` | Confirm a value in a block or a tag |

## Mechanism

| Keys | What |
|---|---|
| `Tab  /  Enter` | Indent 4 spaces / new line that keeps the indentation (code) |
| `Ctrl+F` | Find in the code (Enter / Shift+Enter: next / previous, Esc closes) |
| `Right-click the code` | Cut, copy, paste, undo, redo, select all |
| `Esc` | Cancel a block drag, or stop editing a block value |

## Playing (debugger)

| Keys | What |
|---|---|
| `F5` | Pause / resume |
| `F10` | Step one frame |
| `Click an object` | See its variables and running whens |

## Without a key

- **🔒 Lock** (inspector header, right-click menu, or the 🔓 next to each name in the scene's object list): a locked
  object can't be clicked, boxed, moved, resized, nudged or deleted in the scene, its inspector is read-only, and editor
  tools (`erase`, `objects_at`...) leave it alone. The game ignores the lock. Click empty space to see the scene's
  object list and pick a locked object there, or right-click it → *Unlock*.
- **Draw order** (inspector): *To back / Back / Forward / To front* and "3 of 12". Later objects are drawn on top, in the
  editor and in the game (among objects of the same `layer`).
- **Keep proportions** (inspector): resizing with the handles or typing Width / Height keeps the picture's proportions
  (or the box's). **Original size** sets the box to the picture's real pixels.
- **Several objects selected**: the inspector shows them all with *Align* (left, centre, right, top, middle, bottom),
  *Space evenly* (across, up and down), the draw order, Lock, Duplicate and Delete, and the list of names (click one to
  keep only it).
- The mouse position in the scene (X / Y) is shown at the right of the scene toolbar.
- Buttons say their shortcut in their tooltip (Play F5, Stop Esc, Save Ctrl+S, Fit Home, zoom Ctrl+wheel).

## Coming from EasyEngine

The checklist of EasyEngine's editor conveniences and what BuilderEngine does with each one.

| EasyEngine | BuilderEngine |
|---|---|
| Arrow keys nudge 1 px, Shift = 10 px / one grid cell, a burst = one undo step | Done: 1 px, Shift = one grid cell (BuilderEngine always snaps to its 8 px grid), a burst = one undo step |
| Mouse wheel on a focused number (`NumberWheel`), Shift ×10, Ctrl ×0.1, a burst = one undo step | Done (number fields, number text fields, numbers in blocks) |
| Focus kept after the inspector rebuilds on a wheel notch | Not needed: BuilderEngine's number fields update in place, the inspector isn't rebuilt |
| Type to search in every dropdown / context menu, nothing picked until Enter (`ListSearch`) | Done (dropdowns, right-click menus, ⋯ menus) |
| Selection box (rubber band), Ctrl keeps the selection | Done (Shift or Ctrl keeps it); locked objects are left out |
| Ctrl+click multi-select, move several | Done (Shift+click too) |
| Ctrl+C / Ctrl+V, paste at the right-click point, into another scene, new ids and names, one undo step | Done, plus paste at the mouse pointer and Ctrl+X |
| Ctrl+D duplicate | Done, for several objects |
| Delete | Done (Backspace too), for several objects |
| Ctrl+A | Done (new in BuilderEngine: EasyEngine has it only in the sprite editor) |
| Esc (cancel / deselect) | Done: cancels a drag (objects go back), stops a tool, deselects; stops the game |
| Double-click an object → its inspector | Double-click renames it (BuilderEngine's inspector is always open) |
| 🔒 Lock objects (read-only inspector, the AI skips them) | Done: also not clickable in the scene (picked in the object list), editor tools skip them |
| Z order: −/+, To front, To back | Done as To back / Back / Forward / To front + Ctrl+[ ] keys (BuilderEngine orders by the object list) |
| Keep proportions (checkbox, Shift while resizing) and Original size | Done |
| Ctrl+wheel zoom at the pointer, plain wheel scrolls | Done (before, the plain wheel zoomed) |
| Ctrl+0 fit, Ctrl++ / Ctrl+- zoom | Done, plus Home = fit and Ctrl+1 = 100% |
| Middle-drag pan | Kept (and Space+drag) |
| Right-click menu on objects (copy, paste, duplicate, delete, move to layer) | Done, plus rename, order, lock and original size |
| Cursor X / Y in the status bar | Done (scene toolbar) |
| Ctrl+S save, Ctrl+Z / Ctrl+Y undo / redo | Kept; Ctrl+Z in a number field now undoes the scene |
| F1 console | F1 is this shortcuts list; the console is always visible in BuilderEngine |
| Snap to grid | Kept (Alt = free) |
| Move to layer | Skipped: BuilderEngine's editor has no layers (scripts use `layer 2`) |
| Ctrl+K show the last AI change | Skipped: BuilderEngine's AI shows its change as a preview / diff before Accept |
| `SliderBar` (click and drag sliders) | Skipped: it fixed a WPF problem; Avalonia's sliders already click and drag |
| `Dropdown` helper | Skipped: BuilderEngine's pickers (`MainWindow.ParamPickers.cs`) already play that role |
| Minimap | Skipped: in EasyEngine it is a game behavior, not an editor convenience |
| Scene lock, layer lock / opacity / on-off | Skipped: no editor layers; object lock covers the need |
| Alignment helpers | Added (EasyEngine has none): align and space evenly for several objects |
