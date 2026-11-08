namespace BuilderEngine.Script;

/// <summary>One line of the editor's "Keyboard shortcuts" help.</summary>
public sealed record Shortcut(string Group, string Keys, string What);

/// <summary>
/// Every keyboard and mouse shortcut of the editor, as the "Keyboard shortcuts" help (F1 or the ? button) shows them.
/// docs/SHORTCUTS.md lists the same lines (a test keeps the two in step).
/// </summary>
public static class EditorShortcuts
{
    public static readonly IReadOnlyList<Shortcut> All =
    [
        new("Everywhere", "F1", "Show this list of shortcuts"),
        new("Everywhere", "F5", "Play the open scene (while playing with the debugger: pause / resume)"),
        new("Everywhere", "Esc  or  Shift+F5", "Stop playing"),
        new("Everywhere", "Ctrl+S", "Save the project"),
        new("Everywhere", "Ctrl+O", "Open a project"),
        new("Everywhere", "Ctrl+Z  /  Ctrl+Y  (or Ctrl+Shift+Z)", "Undo / redo — the scene's in the Scene tab, the blocks' in the blocks, the text's while typing"),

        new("Scene: select", "Click", "Select an object (locked objects can't be clicked: pick them in the scene's object list)"),
        new("Scene: select", "Shift+click  or  Ctrl+click", "Add an object to the selection, or take it out"),
        new("Scene: select", "Drag on empty space", "Selection box: selects every object it touches (Shift or Ctrl keeps the others)"),
        new("Scene: select", "Ctrl+A", "Select every object (not the locked ones)"),
        new("Scene: select", "Esc", "Cancel a drag in progress, else stop the tool, else select nothing"),
        new("Scene: select", "Double-click  or  F2", "Rename the selected object (with nothing selected: rename the scene)"),
        new("Scene: select", "Right-click", "Menu: copy, paste here, duplicate, delete, rename, order, lock, original size"),

        new("Scene: move and size", "Drag", "Move the selected objects (they snap to the grid)"),
        new("Scene: move and size", "Alt while dragging", "Don't snap to the grid"),
        new("Scene: move and size", "Shift while dragging", "Move only across or only up and down"),
        new("Scene: move and size", "Arrow keys", "Move the selection 1 pixel"),
        new("Scene: move and size", "Shift+arrow keys", "Move the selection one grid cell (8 pixels)"),
        new("Scene: move and size", "Drag a handle", "Resize the selected object"),
        new("Scene: move and size", "Shift while resizing", "Keep the proportions (the picture's, else the box's)"),

        new("Scene: edit", "Delete  or  Backspace", "Delete the selected objects"),
        new("Scene: edit", "Ctrl+D", "Duplicate the selected objects"),
        new("Scene: edit", "Ctrl+C  /  Ctrl+X", "Copy / cut the selected objects"),
        new("Scene: edit", "Ctrl+V", "Paste at the mouse pointer (or a little further each time); works in another scene too"),
        new("Scene: edit", "Ctrl+L", "Lock or unlock the selected objects"),
        new("Scene: edit", "Ctrl+Shift+]  /  Ctrl+Shift+[", "Bring to front / send to back"),
        new("Scene: edit", "Ctrl+]  /  Ctrl+[", "Bring forward / send backward one step"),

        new("Scene: view", "Ctrl+wheel", "Zoom at the mouse pointer"),
        new("Scene: view", "Wheel  /  Shift+wheel", "Scroll up and down / sideways"),
        new("Scene: view", "Middle-drag  or  Space+drag", "Pan the view"),
        new("Scene: view", "Home  or  Ctrl+0", "Fit the whole scene in view"),
        new("Scene: view", "Ctrl+1", "Zoom to 100%"),
        new("Scene: view", "Ctrl++  /  Ctrl+-", "Zoom in / out"),
        new("Scene: view", "Ctrl+PageUp  /  Ctrl+PageDown", "Edit the previous / next scene"),

        new("Fields and lists", "Wheel over a field you clicked into", "Change the number live (Shift = ×10, Ctrl = ×0.1); a burst of notches is one undo step"),
        new("Fields and lists", "Type in an open dropdown or menu", "Search it: only matching entries stay; Enter picks the first, Backspace edits, Esc clears"),
        new("Fields and lists", "Enter", "Confirm a value in a block or a tag"),

        new("Mechanism", "Tab  /  Enter", "Indent 4 spaces / new line that keeps the indentation (code)"),
        new("Mechanism", "Ctrl+F", "Find in the code (Enter / Shift+Enter: next / previous, Esc closes)"),
        new("Mechanism", "Right-click the code", "Cut, copy, paste, undo, redo, select all"),
        new("Mechanism", "Esc", "Cancel a block drag, or stop editing a block value"),

        new("Playing (debugger)", "F5", "Pause / resume"),
        new("Playing (debugger)", "F10", "Step one frame"),
        new("Playing (debugger)", "Click an object", "See its variables and running whens"),
    ];
}
