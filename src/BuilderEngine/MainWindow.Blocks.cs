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
            _blocksView.Show(source);
            return;
        }
        // Typing in the code redraws the blocks once the typing pauses.
        _blocksPending = source;
        _blocksTimer!.Stop();
        _blocksTimer.Start();
    }
}
