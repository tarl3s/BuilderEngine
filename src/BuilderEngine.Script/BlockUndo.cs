namespace BuilderEngine.Script;

/// <summary>
/// Undo / redo for the blocks view. A snapshot is the mechanism's whole source, so undoing just puts an older text back.
/// Every edit is one step, except consecutive edits that give the same merge key (typing again in the same field):
/// those stay one step, undone together.
/// </summary>
public sealed class BlockUndo
{
    readonly List<string> _undo = [];
    readonly List<string> _redo = [];
    string? _lastKey;

    /// <summary>How many steps are kept.</summary>
    public int Limit { get; init; } = 200;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>
    /// An edit turned <paramref name="before"/> into <paramref name="after"/>. Nothing is recorded when nothing changed.
    /// A non-null <paramref name="mergeKey"/> equal to the previous edit's key joins that step instead of making a new one.
    /// </summary>
    public void Record(string before, string after, string? mergeKey = null)
    {
        if (Norm(before) == Norm(after)) return;
        bool merge = mergeKey != null && mergeKey == _lastKey && _undo.Count > 0;
        _lastKey = mergeKey;
        _redo.Clear();
        if (merge) return;
        _undo.Add(before);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
    }

    /// <summary>Steps back: returns the source to show, or null when there is nothing to undo.</summary>
    public string? Undo(string current)
    {
        if (_undo.Count == 0) return null;
        _lastKey = null;
        _redo.Add(current);
        string s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return s;
    }

    /// <summary>Steps forward again: returns the source to show, or null when there is nothing to redo.</summary>
    public string? Redo(string current)
    {
        if (_redo.Count == 0) return null;
        _lastKey = null;
        _undo.Add(current);
        string s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return s;
    }

    /// <summary>The next edit starts a new step even if its key matches the last one.</summary>
    public void BreakMerge() => _lastKey = null;

    /// <summary>Forgets everything (another mechanism was opened).</summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _lastKey = null;
    }

    static string Norm(string s) => s.Replace("\r\n", "\n");
}
