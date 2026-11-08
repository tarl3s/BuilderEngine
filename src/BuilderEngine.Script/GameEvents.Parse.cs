namespace BuilderEngine.Script;

// GAME EVENTS: the engine events of the game kit, read after 'when' (EventKind.Game, the event's words in Name):
//   when clicked                  when hovered            when unhovered          (this object, under the mouse)
//   when mouse pressed at x, y    when mouse released at x, y    when mouse pressed "right"   (anywhere)
//   when picked up                when dropped            when dropped on "slot"  when received "card"   (dragging)
//   when tile broken              when tile broken "stone" at x, y               when tile placed at x, y
// A word that isn't followed by what these need stays a signal (a bare 'when mouse' is the signal "mouse").
public sealed partial class Parser
{
    /// <summary>Every game event, as written after 'when' (for did-you-mean and the docs).</summary>
    public static readonly string[] GameEvents =
        ["clicked", "hovered", "unhovered", "mouse pressed", "mouse released", "picked up", "dropped", "received", "tile broken", "tile placed"];

    /// <summary>A game event starting with <paramref name="w"/> (already read), or null when these words aren't one.</summary>
    EventSpec? TryParseGameEvent(string w)
    {
        switch (w)
        {
            case "clicked" or "hovered" or "unhovered" when AtEventEnd:
                return new EventSpec(EventKind.Game, Name: w);
            case "mouse" when IsWord("pressed") || IsWord("released"):
            {
                string name = "mouse " + Next().Text;
                string? button = Peek().Kind == Tok.String ? Next().Text.ToLowerInvariant() : null;
                return new EventSpec(EventKind.Game, Filter: button, Bind: ParseAtNames(), Name: name);
            }
            case "picked" when IsWord("up"):
                _p++;
                return new EventSpec(EventKind.Game, Name: "picked up");
            case "dropped" when AtEventEnd || IsWord("on"):
            {
                if (IsWord("on")) _p++;
                string? zone = Peek().Kind == Tok.String ? Next().Text : null;
                return new EventSpec(EventKind.Game, Filter: zone, Name: "dropped");
            }
            case "received" when AtEventEnd || Peek().Kind == Tok.String:
            {
                string? what = Peek().Kind == Tok.String ? Next().Text : null;
                return new EventSpec(EventKind.Game, Filter: what, Name: "received");
            }
            case "tile" when IsWord("broken") || IsWord("placed"):
            {
                string name = "tile " + Next().Text;
                string? kind = Peek().Kind == Tok.String ? Next().Text : null;
                return new EventSpec(EventKind.Game, Filter: kind, Bind: ParseAtNames(), Name: name);
            }
        }
        return null;
    }

    /// <summary>Nothing more on the event line (a ':' or the end of the line).</summary>
    bool AtEventEnd => IsOp(":") || AtLineEnd;

    /// <summary>Optional "at x, y" (or "at pos"): the names that get the event's position. Null when not written.</summary>
    string? ParseAtNames()
    {
        if (!IsWord("at")) return null;
        int line = Next().Line;
        var names = new List<string> { Expect(Tok.Ident, "a name for x, like 'at x, y'").Text };
        if (IsOp(","))
        {
            _p++;
            names.Add(Expect(Tok.Ident, "a second name for y, like 'at x, y'").Text);
        }
        if (names.Count == 2 && names[0] == names[1]) throw new ScriptError("Give x and y two different names, like 'at x, y'.", line);
        return string.Join(", ", names);
    }
}

public static class GameEvents
{
    /// <summary>What follows 'when' for a game event: <c>mouse pressed "right" at x, y</c>, <c>dropped on "slot"</c>.</summary>
    public static string Tail(EventSpec e)
    {
        string s = e.Name ?? "";
        if (e.Filter != null) s += (e.Name == "dropped" ? " on " : " ") + Printer.Quote(e.Filter);
        if (e.Bind != null) s += " at " + e.Bind;
        return s;
    }
}
