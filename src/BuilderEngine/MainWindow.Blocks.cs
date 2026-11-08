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
        _blocksView = new BlocksView();
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
        // Typing in the code redraws the blocks once the typing pauses.
        _blocksPending = source;
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
        }
        catch (Exception e) { Check("no exception: " + e.Message, false); }
        finally { File.WriteAllLines(Path.Combine(dir, "blocks_checks.txt"), lines); }
    }
}
