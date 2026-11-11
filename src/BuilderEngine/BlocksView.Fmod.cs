using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BuilderEngine.Script;

namespace BuilderEngine;

// FMOD STUDIO blocks (World.FmodEvents.cs): 'play event' gets a ▾ that lists the project's FMOD Studio events and
// 'set event' a ▾ with that event's parameters (owner rule: values are picked, not typed). The values stay editable
// fields; without FMOD Studio or banks the ▾ says why. Other FMOD commands are ordinary command blocks.
sealed partial class BlocksView
{
    /// <summary>The project's folder, for its FMOD Studio events (set by the window; null = no events to list).</summary>
    public Func<string?>? ProjectDir { get; set; }

    IReadOnlyList<FmodEventInfo> FmodEvents(out string message)
    {
        message = "No project.";
        return ProjectDir?.Invoke() is { } dir ? FmodStudio.ListEvents(dir, out message) : [];
    }

    /// <summary>The block of <c>play event "…"</c> / <c>set event "…" "…" to v</c>, or null for any other command.</summary>
    WrapPanel? FmodParts(List<Stmt> list, int j, CommandStmt c, BlockLook look)
    {
        if (c.Args is not [NameExpr { Path: ["event"] }, ..]) return null;
        if (c.Name == "play" && c.Args.Count == 2)
        {
            var parts = Parts(Word("play event", look.Fg), ArgField(list, j, 1, "event"),
                PickButton("Pick an event of your FMOD Studio project", () =>
                {
                    var events = FmodEvents(out var msg);
                    return events.Count == 0 ? ([], msg) : (events.Select(e => (e.Path, (Action)(() => SetArg(list, j, 1, e.Path)))).ToList(), "");
                }));
            NamedParts(parts, list, j, c, look);
            return parts;
        }
        if (c.Name == "set" && c.Args.Count == 3)
        {
            var parts = Parts(Word("set event", look.Fg), ArgField(list, j, 1, "name"), ArgField(list, j, 2, "parameter"),
                PickButton("Pick a parameter of this event", () =>
                {
                    var events = FmodEvents(out var msg);
                    if (events.Count == 0) return ([], msg);
                    var cur = (CommandStmt)list[j];
                    string? path = cur.Args[1] is StringExpr s ? EventOf(s.Value) : null;
                    var known = path == null ? null : events.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
                    var names = (known != null ? known.Parameters : events.SelectMany(e => e.Parameters))
                        .Where(p => !p.Global).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
                    return names.Count == 0
                        ? ([], known != null ? "This event has no parameters." : "No event in this project has parameters.")
                        : (names.Select(n => (n, (Action)(() => SetArg(list, j, 2, n)))).ToList(), "");
                }));
            NamedParts(parts, list, j, c, look);
            return parts;
        }
        return null;
    }

    /// <summary>The event path a name means: a path itself, or the event a 'play event … as "name"' in this code starts.</summary>
    string? EventOf(string name)
    {
        foreach (var d in _defs)
            foreach (var s in d.Whens.SelectMany(w => AiCheck.AllStatements(w.Body)).Concat(d.Procs.SelectMany(p => AiCheck.AllStatements(p.Body))))
                if (s is CommandStmt { Name: "play", Args: [NameExpr { Path: ["event"] }, StringExpr ev] } c
                    && c.Named?.GetValueOrDefault("as") is [StringExpr asName] && string.Equals(asName.Value, name, StringComparison.OrdinalIgnoreCase))
                    return FmodStudio.NormalizePath(ev.Value);
        return name.Contains('/') || name.StartsWith("event:", StringComparison.OrdinalIgnoreCase) ? FmodStudio.NormalizePath(name) : null;
    }

    Field ArgField(List<Stmt> list, int j, int at, string placeholder) => ExprField(((CommandStmt)list[j]).Args[at], e =>
    {
        if (e == null) throw Bad("Write a value here.");
        var cur = (CommandStmt)list[j];
        var args = cur.Args.ToList();
        args[at] = e;
        list[j] = cur with { Args = args };
    }, placeholder);

    void SetArg(List<Stmt> list, int j, int at, string text)
    {
        if (j >= list.Count || list[j] is not CommandStmt cur || at >= cur.Args.Count) return;
        var args = cur.Args.ToList();
        args[at] = new StringExpr(text, 0);
        list[j] = cur with { Args = args };
        Commit();
    }

    /// <summary>A small ▾ that lists choices when opened (or one line saying why there are none).</summary>
    Button PickButton(string tip, Func<(List<(string Label, Action Pick)> Choices, string Empty)> choices)
    {
        var b = new Button
        {
            Content = "▾", FontSize = 12.5, Padding = new Thickness(6, 1), MinHeight = 22,
            Background = BlockColors.PillBg, Foreground = BlockColors.PillText, BorderBrush = BlockColors.PillBorder,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Margin = new Thickness(2, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(b, BlockText(tip));
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = "…" }); // an empty MenuFlyout never opens
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var (list, empty) = choices();
            if (list.Count == 0) menu.Items.Add(new MenuItem { Header = empty.Length > 0 ? empty : "Nothing to pick.", IsEnabled = false });
            foreach (var (label, pick) in list)
            {
                var mi = new MenuItem { Header = label };
                mi.Click += (_, _) => pick();
                menu.Items.Add(mi);
            }
        };
        b.Flyout = menu;
        return b;
    }
}
