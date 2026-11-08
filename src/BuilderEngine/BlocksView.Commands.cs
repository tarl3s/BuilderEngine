using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// BLOCKS FEATURE: Builder Script 2 blocks. The 'to' block (a command you make, like Scratch's "define"), the palette's
// MY COMMANDS section (your commands, ready to click or drag), together / run / for each / while, the waiting blocks,
// and command blocks that show their named parts ("move [Boss] to [400, 300] in [2]") as labelled fields.
sealed partial class BlocksView
{
    readonly StackPanel _myCommands = new() { Spacing = 4 };
    /// <summary>Palette ids that make something other than a built-in block (a call to a user command: "call:name").</summary>
    readonly Dictionary<string, Func<Stmt>> _paletteFactories = new();

    // ---------------- Palette: MY COMMANDS ----------------

    /// <summary>Lists the commands the open mechanism can use: its own, those of the mechanisms it uses, the project's.</summary>
    void RefreshMyCommands()
    {
        _myCommands.Children.Clear();
        foreach (var key in _paletteFactories.Keys.ToList()) { _paletteFactories.Remove(key); _paletteItems.Remove(key); }

        var make = PaletteItem("+ New command", BlockColors.Proc,
            "Make your own command with 'to': give it a name and inputs, put blocks inside, then use it anywhere like a built-in one.",
            "to", _ => AddProc());
        make.BorderBrush = BlockColors.Proc.Fg;
        make.BorderThickness = new Thickness(1);
        _myCommands.Children.Add(make);
        if (_defs.Count == 0) return;

        var open = _defs[MechIndex()];
        var commands = BlockEdits.CommandsFor(open, _project);
        if (commands.Count == 0)
        {
            _myCommands.Children.Add(PaletteNote("Commands you make show up here, ready to use in this mechanism and the others."));
            return;
        }
        string? group = null;
        foreach (var cmd in commands)
        {
            if (cmd.Group != group)
            {
                group = cmd.Group;
                _myCommands.Children.Add(PaletteNote(group, top: 4));
            }
            string id = "call:" + cmd.CallName;
            int inputs = cmd.Def.Params.Count;
            string callName = cmd.CallName;
            _paletteFactories[id] = () => BlockEdits.NewCall(callName, inputs);
            var content = new WrapPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = cmd.CallName, Foreground = BlockColors.Proc.Fg, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            foreach (var p in cmd.Def.Params) content.Children.Add(InputChip(p));
            string tip = Printer.ProcHead(cmd.Def) + "   (" + BlockText("from") + " " + cmd.Mechanism + ")\n" + BlockText("Click to add it to the selected place, or drag it to the exact spot.");
            _myCommands.Children.Add(PaletteItem(content, BlockColors.Proc, tip, id, _ => AddStatement(id)));
        }
        if (open.Procs.Count > 0)
            _myCommands.Children.Add(PaletteItem("return", BlockColors.Proc, "return value — ends a command made with 'to' and gives back a value (then it can be used in values, like bonus(3)). Only inside a 'to'.", "return", _ => AddStatement("return")));
    }

    static Border InputChip(string name) => new()
    {
        Background = BlockColors.PillBg, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0), Margin = new Thickness(0, 1, 3, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = name, FontFamily = Palette.Mono, FontSize = 11, Foreground = BlockColors.PillDim }
    };

    static TextBlock PaletteNote(string text, double top = 0) => new()
    {
        Text = BlockText(text), FontSize = 10.5, Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, top, 0, 0)
    };

    void AddProc()
    {
        if (_stale || _defs.Count == 0) return;
        var m = _defs[MechIndex()];
        var p = BlockEdits.NewProc(m);
        m.Procs.Add(p);
        _selBody = p.Body;
        _reveal = p;
        Commit();
    }

    // ---------------- The 'to' block ----------------

    Control ProcBlock(List<ProcDef> procs, int i, int mi)
    {
        var p = procs[i];
        var look = BlockColors.Proc;
        var name = new Field(p.Name, text =>
        {
            string n = text.Trim();
            var m = _defs[mi];
            if (BlockEdits.CheckProcName(m, n, procs[i]) is { } err) throw Bad(err);
            string old = procs[i].Name;
            procs[i] = procs[i] with { Name = n };
            BlockEdits.RenameCalls(m, old, n); // the calls in this mechanism follow the new name
            Commit();
            return n;
        }, "name");
        var inputs = new Field(string.Join(", ", p.Params), text =>
        {
            var list = BlockEdits.ParseProcParams(text);
            procs[i] = procs[i] with { Params = list };
            Commit();
            return string.Join(", ", list);
        }, "+ inputs");
        ToolTip.SetTip(inputs, BlockText("The values this command takes, separated by commas (like: target, power). Inside, they work like variables."));
        var parts = Parts(Word("to", look.Fg), name, inputs);
        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var head = Row(look, parts, Ops(procs, i, () => { BlockEdits.Duplicate(procs, i); Commit(); }), HeadRadius, () => Select(p.Body),
            _ => new DragSrc(DragKind.Proc, procs, i, null, outer));
        ToolTip.SetTip(head, BlockText("A command you made. Use it from MY COMMANDS in the palette, or write its name in the code."));
        outer.Children.Add(head);
        outer.Children.Add(Body(p.Body, look, mi));
        outer.Children.Add(FootNote(look, "end of " + p.Name));
        Mark(p, outer);
        return outer;
    }

    /// <summary>The bottom of a C-shaped block with a short dim note in it ("then continue when all are done").</summary>
    static Border FootNote(BlockLook look, string note) => new()
    {
        Background = look.Bg, CornerRadius = new CornerRadius(0, 6, 6, 6), Padding = new Thickness(10, 1, 8, 2), MinHeight = 10,
        Child = new TextBlock { Text = BlockText(note), FontSize = 10.5, Foreground = look.Fg, Opacity = 0.7 }
    };

    static TextBlock Hint(string text, BlockLook look) => new()
    {
        Text = BlockText(text), FontSize = 11, Foreground = look.Fg, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0)
    };

    // ---------------- together / run / for each / while ----------------

    Control NewCBlock(List<Stmt> list, int j, Stmt s, int mi, Action dup)
    {
        BlockLook look;
        WrapPanel parts;
        List<Stmt> body;
        string? note = null;
        bool parallel = false;
        switch (s)
        {
            case TogetherStmt t:
                look = BlockColors.Parallel;
                parts = Parts(Word("together", look.Fg), Hint("each block runs at the same time", look));
                body = t.Body;
                note = "then continue when all have finished";
                parallel = true;
                break;
            case RunStmt r:
                look = BlockColors.Parallel;
                parts = Parts(Word("run", look.Fg), Hint("in the background — doesn't wait", look));
                body = r.Body;
                break;
            case ForEachStmt f:
                look = BlockColors.Control;
                parts = Parts(Word("for each", look.Fg),
                    NameField(f.Var, false, n => list[j] = ((ForEachStmt)list[j]) with { Var = n }, "item"),
                    Word("in", look.Fg),
                    ExprField(f.List, e => list[j] = ((ForEachStmt)list[j]) with { List = e! }, "list"));
                body = f.Body;
                break;
            case WhileStmt w:
                look = BlockColors.Control;
                parts = Parts(Word("while", look.Fg), ExprField(w.Cond, e => list[j] = ((WhileStmt)list[j]) with { Cond = e! }, "condition"));
                body = w.Body;
                break;
            default:
                throw new ArgumentException(s.GetType().Name);
        }
        var outer = new StackPanel();
        outer.Children.Add(Row(look, parts, Ops(list, j, dup), HeadRadius, () => Select(body), _ => new DragSrc(DragKind.Stmt, list, j, null, outer)));
        outer.Children.Add(Body(body, look, mi, parallel));
        outer.Children.Add(note == null ? Foot(look) : FootNote(look, note));
        return outer;
    }

    // ---------------- One-line blocks ----------------

    /// <summary>The waiting blocks, return, and calls to user commands; null for the others (built in BlocksView.cs).</summary>
    (BlockLook, WrapPanel)? NewSimpleBlock(List<Stmt> list, int j, Stmt s, int mi)
    {
        switch (s)
        {
            case WaitUntilStmt u:
            {
                var look = BlockColors.Event;
                return (look, Parts(Word("wait until", look.Fg), ExprField(u.Cond, e => list[j] = ((WaitUntilStmt)list[j]) with { Cond = e! }, "condition")));
            }
            case WaitSignalStmt ws:
            {
                var look = BlockColors.Event;
                var f = new Field(ws.Full, text =>
                {
                    string t = text.Trim();
                    if (BlockEdits.CheckName(t, dotted: true) is { } err) throw Bad(err);
                    list[j] = ((WaitSignalStmt)list[j]) with { Path = t.Split('.') };
                    Commit(rebuild: false);
                    return t;
                }, "Health.died");
                ToolTip.SetTip(f, BlockText("A signal: died (this object's), Health.died (from its Health) or other.Health.died (another object's)."));
                return (look, Parts(Word("wait for", look.Fg), f));
            }
            case WaitKeyStmt k:
            {
                var look = BlockColors.Event;
                return (look, Parts(Word("wait for key", look.Fg), ExprField(k.Key, e => list[j] = ((WaitKeyStmt)list[j]) with { Key = e! }, "\"space\"")));
            }
            case ReturnStmt r:
            {
                var look = BlockColors.Proc;
                return (look, Parts(Word("return", look.Fg), ExprField(r.Value, e => list[j] = ((ReturnStmt)list[j]) with { Value = e }, "nothing", optional: true)));
            }
            case CommandStmt c when c.Named == null && mi < _defs.Count && BlockEdits.FindCommand(c.Name, _defs[mi], _project) is { } proc:
                return (BlockColors.Proc, CallParts(list, j, c, proc));
        }
        return null;
    }

    /// <summary>A call to a user command: its name, then each input labelled with its name ("attack  target [Boss]  power [3]").</summary>
    WrapPanel CallParts(List<Stmt> list, int j, CommandStmt c, ProcDef proc)
    {
        var look = BlockColors.Proc;
        var parts = Parts(Word(c.Name, look.Fg));
        ToolTip.SetTip(parts, Printer.ProcHead(proc));
        if (c.Args.Count != proc.Params.Count)
        {
            // The command's inputs changed: show the values as typed, with the reason.
            parts.Children.Add(ListField(c.Args, l => list[j] = ((CommandStmt)list[j]) with { Args = l }));
            var warn = Word("⚠", BlocksView.WarnBrush);
            ToolTip.SetTip(warn, BlockText($"'{proc.Name}' needs {proc.Params.Count} value(s): {string.Join(", ", proc.Params)}"));
            parts.Children.Add(warn);
            return parts;
        }
        for (int k = 0; k < proc.Params.Count; k++)
        {
            int at = k;
            parts.Children.Add(Hint(proc.Params[k], look));
            parts.Children.Add(ExprField(c.Args[k], e =>
            {
                var cur = (CommandStmt)list[j];
                var args = cur.Args.ToList();
                args[at] = e!;
                list[j] = cur with { Args = args };
            }, proc.Params[k]));
        }
        return parts;
    }

    // ---------------- Dev snapshots (--snap) ----------------

    internal bool DebugHasBlocks => !_stale && _defs.Count > 0 && _stmtSlots.Count > 0;

    /// <summary>Scrolls the blocks so the first 'to' block is at the top.</summary>
    internal void DebugScrollToCommands()
    {
        foreach (var slot in _mechSlots)
            if (slot.Procs.Children.Count > 0 && slot.Procs.TranslatePoint(default, _blocks) is { } p)
            {
                _scroll.Offset = new Vector(0, Math.Max(0, p.Y - 8));
                return;
            }
    }
    internal bool DebugPaletteHas(string id) => _paletteItems.ContainsKey(id) && _myCommands.Children.Contains(_paletteItems[id]);

    /// <summary>Selects the first when's body (where a palette click adds).</summary>
    internal void DebugSelectFirstBody()
    {
        if (_defs.Count > 0 && _defs[MechIndex()].Whens.Count > 0) Select(_defs[MechIndex()].Whens[0].Body);
    }

    static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#F2B8AE")).ToImmutable();

    /// <summary>
    /// A command: its name, its values, then each named part after its connector word as its own labelled field
    /// ("move [Boss] to [400, 300] in [2]"). Emptying a named part removes it (the code would read it as a name otherwise).
    /// </summary>
    WrapPanel CommandParts(List<Stmt> list, int j, CommandStmt c, BlockLook look)
    {
        var parts = Parts(Word(c.Name, look.Fg), ListField(c.Args, l => list[j] = ((CommandStmt)list[j]) with { Args = l }));
        if (c.Named == null) return parts;
        foreach (var (connector, values) in c.Named)
        {
            string key = connector;
            parts.Children.Add(Word(key, look.Fg));
            parts.Children.Add(ListField(values, l =>
            {
                var cur = (CommandStmt)list[j];
                var named = cur.Named!.ToDictionary(kv => kv.Key, kv => kv.Value);
                if (l.Count == 0) named.Remove(key);
                else named[key] = l;
                list[j] = cur with { Named = named.Count == 0 ? null : named };
            }, "values"));
        }
        return parts;
    }
}
