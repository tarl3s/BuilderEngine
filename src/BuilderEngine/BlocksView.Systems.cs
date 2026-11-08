using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

// SYSTEMS in the blocks: a system's header says "system" (MechanismBlock), and its 'includes' lines are rows right under
// the header — a mechanism name and its settings, edited like a 'uses' row — with "+ includes" to add one. Same tree as
// the code (Printer prints them first), so blocks and code stay 1:1.
sealed partial class BlocksView
{
    /// <summary>The 'includes' rows of the system shown at <paramref name="mi"/>, and a link to add one.</summary>
    Control IncludesPanel(int mi)
    {
        var list = _defs[mi].Includes;
        var panel = new StackPanel();
        for (int i = 0; i < list.Count; i++)
        {
            int k = i;
            var u = list[k];
            var row = Row(BlockColors.Setting, Parts(
                Word("includes", BlockColors.Setting.Fg),
                NameField(u.Name, false, n =>
                {
                    if (list.Where((x, q) => q != k).Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)))
                        throw Bad($"This system already includes '{n}'.");
                    list[k] = list[k] with { Name = n };
                }, "mechanism"),
                new Field(Printer.UsesTail(u), text =>
                {
                    var (pos, args) = BlockEdits.ParseUsesTail(text);
                    list[k] = list[k] with { Positional = pos, Args = args };
                    Commit(rebuild: false);
                    return Printer.UsesTail(list[k]);
                }, "settings")),
                Ops(list, k, null), RowRadius, () => { _selMech = mi; });
            row.Margin = new Thickness(0, 0, 0, 3);
            ToolTip.SetTip(row, BlockText("A mechanism this system comes with. Its settings are the defaults every object with it gets."));
            Mark(u, row);
            panel.Children.Add(row);
        }
        var add = new TextBlock
        {
            Text = BlockText("+ includes"), Foreground = BlockColors.Setting.Fg, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(6, 0, 0, 6), Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(add, BlockText("Add a mechanism this system comes with (placed on things, like Pickup or Coin)"));
        add.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (_stale) return;
            string name = "Coin";
            for (int n = 2; list.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = "Coin" + n;
            var u = new UsesDef(name, null, new Dictionary<string, Expr>(), 0);
            list.Add(u);
            _selMech = mi;
            _reveal = u;
            Commit();
        };
        panel.Children.Add(add);
        return panel;
    }
}
