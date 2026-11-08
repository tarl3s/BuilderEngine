using Avalonia.Controls;
using Avalonia.Threading;

namespace BuilderEngine;

// BLOCKS FEATURE: the open mechanism as editable blocks (BlocksView.cs), kept in step with the code view.
public sealed partial class MainWindow
{
    BlocksView? _blocksView;
    /// <summary>The source the blocks show (the last one that parsed, or the last one the blocks printed).</summary>
    string? _blocksSource;
    string? _blocksPending;
    string? _blocksMechanism;
    DispatcherTimer? _blocksTimer;

    /// <summary>Creates the blocks view shown left of the code (called once).</summary>
    Control CreateBlocksView()
    {
        _blocksView = new BlocksView { ProjectDefs = BlocksProjectDefs };
        _blocksView.Edited += source =>
        {
            _blocksTimer?.Stop();
            _blocksPending = null;
            _blocksSource = source;
            ApplyCodeFromBlocks(source);
            _blocksMechanism = _current; // a rename from the blocks is still the same mechanism (keeps its undo steps)
        };
        // When the code stops parsing the shell doesn't call BlocksShow: grey the blocks until it parses again.
        // (This handler runs after the shell's own, which calls BlocksShow first when the code parses.)
        _code.TextChanged += (_, _) =>
        {
            bool fresh = _blocksSource != null && BlocksNorm(_code.Text ?? "") == BlocksNorm(_blocksSource);
            // A redraw still waiting for the typing to pause would show older code (and unlock the blocks) over code
            // that no longer parses: drop it; the next code that parses schedules a new one.
            if (!fresh) { _blocksTimer?.Stop(); _blocksPending = null; }
            _blocksView.SetStale(!fresh);
        };
        _blocksTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _blocksTimer.Tick += (_, _) =>
        {
            _blocksTimer.Stop();
            if (_blocksPending != null) _blocksView.Show(_blocksPending);
            _blocksPending = null;
        };
        return _blocksView;
    }

    static string BlocksNorm(string s) => s.Replace("\r\n", "\n");

    /// <summary>Parsed sources, by text, so the palette's MY COMMANDS doesn't reparse every mechanism on each redraw.</summary>
    readonly Dictionary<string, List<Script.MechanismDef>> _blocksParsed = new();

    /// <summary>The project's mechanisms other than the open one (those that parse), for MY COMMANDS and call blocks.</summary>
    IReadOnlyList<Script.MechanismDef> BlocksProjectDefs()
    {
        if (_blocksParsed.Count > 400) _blocksParsed.Clear();
        var list = new List<Script.MechanismDef>();
        foreach (var (name, src) in _sources)
        {
            if (name == _current) continue;
            if (!_blocksParsed.TryGetValue(src, out var defs))
            {
                try { defs = Script.Parser.Parse(src); }
                catch (Script.ScriptError) { defs = []; }
                _blocksParsed[src] = defs;
            }
            list.AddRange(defs);
        }
        return list;
    }

    /// <summary>The open mechanism's source changed in the code view (it parses): redraw the blocks.</summary>
    void BlocksShow(string source)
    {
        if (_blocksView == null) return;
        bool first = _blocksSource == null || _blocksMechanism != _current;
        _blocksMechanism = _current;
        _blocksSource = source;
        _blocksView.SetStale(false);
        if (first)
        {
            _blocksTimer!.Stop();
            _blocksPending = null;
            _blocksView.ResetUndo(); // undo steps belong to one mechanism
            _blocksView.Show(source);
            return;
        }
        // Typing in the code redraws the blocks once the typing pauses; until then they take no edits (Expect).
        _blocksPending = source;
        _blocksView.Expect();
        _blocksTimer!.Stop();
        _blocksTimer.Start();
    }
}

// BLOCKS FEATURE, dev tool (`--snap`): a drag held mid-way, the drop, and undo / redo, checked on the real view.
public sealed partial class MainWindow
{
    async Task BlocksSnap(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            if (_blocksView == null || _current == null) { Check("a mechanism is open in the blocks", false); return; }
            string original = _sources[_current];
            bool dragging = _blocksView.DebugBeginDrag();
            Check("a drag shows the ghost and the insertion line", dragging);
            await Task.Delay(200);
            Snap(dir, "8_blocks_dragging");
            _blocksView.DebugEndDrag(drop: true);
            await Task.Delay(400);
            Snap(dir, "9_blocks_dropped");
            string moved = _sources[_current];
            Check("the drop moved the block (code changed)", moved != original && (_code.Text ?? "") == moved);
            _blocksView.Undo();
            await Task.Delay(200);
            Check("undo puts the code back exactly", _sources[_current] == original && (_code.Text ?? "") == original);
            Check("redo is possible after undo", _blocksView.CanRedo && !_blocksView.CanUndo);
            _blocksView.Redo();
            await Task.Delay(200);
            Check("redo moves it again", _sources[_current] == moved);
            _blocksView.Undo();
            await Task.Delay(200);
            Check("undo again", _sources[_current] == original);
            Check("a drag over the palette offers to delete", _blocksView.DebugBeginDrag(toPalette: true));
            await Task.Delay(200);
            Snap(dir, "10_blocks_trash");
            _blocksView.DebugEndDrag(drop: true);
            await Task.Delay(300);
            Check("dropping on the palette deletes the block", _sources[_current].Length < original.Length);
            _blocksView.Undo();
            await Task.Delay(200);
            Check("undo brings the deleted block back", _sources[_current] == original);
            static int Count(string s, string part) => (s.Length - s.Replace(part, "").Length) / part.Length;
            int stops = Count(original, "stop");
            Check("pointer: clicking a palette item adds it", _blocksView.DebugPointer("stop", drag: false) && Count(_sources[_current], "stop") == stops + 1);
            _blocksView.Undo();
            await Task.Delay(200);
            Check("pointer: dragging a palette item drops it at the line",
                _blocksView.DebugPointer("wait", drag: true) && BlocksNorm(_sources[_current]).Contains("hp = max\n        wait 1\n"));
            await Task.Delay(300);
            Snap(dir, "11_blocks_palette_drop");
            _blocksView.Undo();
            await Task.Delay(200);
            Check("undo after the pointer edits", _sources[_current] == original);
            await SnapCodeAndBlocks(original, Check);
            await SnapLanguageBlocks(dir, Check);
        }
        catch (Exception e) { Check("no exception: " + e.Message, false); }
        finally { File.WriteAllLines(Path.Combine(dir, "blocks_checks.txt"), lines); }
    }

    /// <summary>
    /// Typing in the code and editing blocks together (language audit): while the blocks wait for the typing to pause they
    /// take no edits; undo steps back through the typed code instead of jumping over it; after a block edit every block
    /// knows the line it is on now (for breakpoints); a comment survives a block edit.
    /// </summary>
    async Task SnapCodeAndBlocks(string original, Action<string, bool> check)
    {
        string typed = original.TrimEnd('\n', '\r') + "\n        # a note typed in the code\n        print \"typed\"\n";
        _code.Text = typed; // as if typed: the shell parses it and the blocks redraw after a pause
        await Task.Delay(30); // the text box reports the change on the next turn (well before the 150 ms pause)
        check("right after typing the blocks take no edit (no older blocks printed over the new code)",
            !_blocksView!.DebugPointer("stop", drag: false) || _sources[_current!] == typed);
        await Task.Delay(500);
        check("after the pause the blocks show the typed code", _sources[_current!] == typed && _blocksView.DebugHasBlocks);
        check("a block edit after typing works", _blocksView.DebugPointer("stop", drag: false) && _sources[_current!] != typed);
        await Task.Delay(300);
        check("the comment typed in the code survives the block edit", _sources[_current!].Contains("# a note typed in the code"));
        check("every block knows its line in the printed code", _blocksView.DebugLinesMatchCode());
        _blocksView.Undo();
        await Task.Delay(200);
        check("undo takes back the block edit only", BlocksNorm(_sources[_current!]) == BlocksNorm(typed));
        _blocksView.Undo();
        await Task.Delay(200);
        check("undo again takes back the typing", _sources[_current!] == original);
        _blocksView.Redo();
        await Task.Delay(200);
        check("redo brings the typed code back", BlocksNorm(_sources[_current!]) == BlocksNorm(typed));
        _blocksView.Undo();
        await Task.Delay(200);
    }

    /// <summary>
    /// Builder Script 2 in blocks: a boss written with 'to' commands, 'together', 'run', 'for each' and a list; its
    /// commands in the palette's MY COMMANDS (clicking one adds a call), and the named parts of a command as fields.
    /// </summary>
    async Task SnapLanguageBlocks(string dir, Action<string, bool> check)
    {
        const string tools = "mechanism FxTools\n    to shake power\n        repeat 3\n            move power, 0\n            wait 0.05\n            move -power, 0\n            wait 0.05\n";
        const string boss = "mechanism BossFight\n    uses Health max 5\n    param speed: number = 120\n\n    when start\n        loot = []\n        together\n            slide_to 400, 1\n            shake 4\n        run taunt\n        wait for Health.died\n        for each item in [\"gem\", \"key\"]\n            add item to loot\n        print \"loot: \" + join(loot, \", \")\n\n" +
            "    to slide_to x, seconds\n        move self to x, 200 in seconds\n        wait seconds\n\n    to taunt\n        while not self.on_ground\n            wait 0.5\n        print \"Is that all?\"\n\n    to power_for level\n        return level * 2\n";
        string? previous = _current;
        _sources["FxTools"] = tools;
        _sources["BossFight"] = boss;
        RefreshLibrary();
        try
        {
            OpenMechanism("BossFight");
            await Task.Delay(700);
            Snap(dir, "12_blocks_commands");
            if (_blocksView!.Bounds.Width > 0) SnapControl(_blocksView, dir, "12b_blocks_commands_view");
            string mode = _mode;
            SetMode("blocks");
            await Task.Delay(400);
            _blocksView.DebugScrollToCommands();
            await Task.Delay(300);
            Snap(dir, "12c_blocks_to_commands");
            SetMode(mode);
            await Task.Delay(300);
            check("the boss shows as blocks (no error banner)", _blocksView.DebugHasBlocks);
            check("MY COMMANDS lists its own and the project's commands", _blocksView.DebugPaletteHas("call:slide_to") && _blocksView.DebugPaletteHas("call:shake") && _blocksView.DebugPaletteHas("call:power_for"));
            string before = _sources["BossFight"];
            _blocksView.DebugSelectFirstBody();
            bool clicked = _blocksView.DebugPointer("call:shake", drag: false);
            await Task.Delay(300);
            check("clicking a command in MY COMMANDS adds a call with its input", clicked && BlocksNorm(_sources["BossFight"]).Contains("        shake 0\n"));
            _blocksView.Undo();
            await Task.Delay(200);
            check("undo removes it", _sources["BossFight"] == before);
            check("the code printed by the blocks still parses and keeps the commands", Script.Parser.Parse(_sources["BossFight"])[0].Procs.Count == 3);
        }
        finally
        {
            if (previous != null && _sources.ContainsKey(previous)) OpenMechanism(previous);
            _sources.Remove("BossFight");
            _sources.Remove("FxTools");
            RefreshLibrary();
        }
    }
}
