namespace BuilderEngine.Script;

// EDITOR TOOLS: reading the tool events. 'when tool' followed by a word is a tool event (a bare 'when tool' is still
// a signal called "tool"):
//   when tool click at x, y      when tool drag at x, y      when tool release at x, y
//   when tool click at pos       (pos = [x, y])              when tool used      when tool key "r"
public sealed partial class Parser
{
    EventSpec ParseToolEvent()
    {
        var t = Next();
        string what = t.Text;
        switch (what)
        {
            case "click" or "drag" or "release":
            {
                if (IsWord("at")) _p++;
                if (Peek().Kind != Tok.Ident) return new EventSpec(EventKind.Tool, Name: what);
                var names = new List<string> { Next().Text };
                if (IsOp(","))
                {
                    _p++;
                    names.Add(Expect(Tok.Ident, "a second name for y, like 'at x, y'").Text);
                }
                if (names.Distinct().Count() != names.Count) throw new ScriptError("Give x and y two different names, like 'at x, y'.", t.Line);
                return new EventSpec(EventKind.Tool, Name: what, Bind: string.Join(", ", names));
            }
            case "used":
                return new EventSpec(EventKind.Tool, Name: what);
            case "key":
            {
                string key = Peek().Kind == Tok.String ? Next().Text : Expect(Tok.Ident, "a key, like \"r\"").Text;
                return new EventSpec(EventKind.Tool, Name: what, Filter: key.ToLowerInvariant());
            }
            default:
                throw new ScriptError($"A tool reacts to click, drag, release, used or key — not '{what}'.{World.Suggest(what, EditorTools.Events)}", t.Line);
        }
    }
}
