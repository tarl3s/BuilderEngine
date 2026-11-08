using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// A text drawn on an object at one of nine places (a card's cost in a corner, its name on top, its rules at the
/// bottom). With a back colour it is a round badge. It grows and shrinks with the object (it remembers the width the
/// object had when the label was set), so a card zoomed with <c>resize</c> keeps its layout.
/// </summary>
public sealed class ObjectLabel
{
    public string Name = "";
    /// <summary>top left, top, top right, left, center, right, bottom left, bottom, bottom right.</summary>
    public string Anchor = "center";
    public double Dx, Dy;
    public string Color = "#FFFFFF";
    public double Size = 12;
    /// <summary>A badge's colour (null = just the text).</summary>
    public string? Back;
    /// <summary>The object's width when the label was set: the label scales by width / this.</summary>
    public double BaseWidth = 1;
    internal string Text = "";
    internal string? Template;
    internal MechanismInstance? Owner;
    internal World? World;
    double _at = double.NaN;

    /// <summary>The text shown now: <c>{name}</c> parts are filled in once per frame.</summary>
    public string TextNow()
    {
        if (Template == null || Owner == null || World == null) return Text;
        if (_at != World.RealTime) { _at = World.RealTime; Text = World.Interpolate(Template, Owner); }
        return Text;
    }

    public bool AnchorLeft => Anchor.EndsWith("left", StringComparison.Ordinal);
    public bool AnchorRight => Anchor.EndsWith("right", StringComparison.Ordinal);
    public bool AnchorTop => Anchor.StartsWith("top", StringComparison.Ordinal);
    public bool AnchorBottom => Anchor.StartsWith("bottom", StringComparison.Ordinal);
}

public sealed partial class GameObject
{
    /// <summary>Texts and badges drawn on the object ('label'), in the order they were set.</summary>
    public List<ObjectLabel>? Labels;
    /// <summary>A coloured frame around the box ('outline "#FFD54A", 3'); null = none.</summary>
    public string? Outline;
    public double OutlineWidth = 2;
}

/// <summary>
/// Texts on objects and frames around them:
/// <code>
/// label "cost" "{card.cost}" at top left with "#FFFFFF", 14, "#2F6FD0"     (a badge: the third value is its colour)
/// label "name" card.name at top by 0, 4 with "#2B1B0E", 9                  ('by' moves it, in pixels of the object as it is now)
/// label "rules" "Deal 2 damage." at bottom with "#222222", 8               (long texts wrap inside the box)
/// label made "hp" "5" at bottom right …     label "cost" none     label none   (one, or every label, off)
/// outline "#FFD54A", 3      outline made "#FFFFFF"      outline off
/// </code>
/// Scripts read a label with <c>label_text(obj, "cost")</c>.
/// </summary>
public sealed partial class World
{
    public static readonly string[] LabelAnchors = ["top left", "top", "top right", "left", "center", "right", "bottom left", "bottom", "bottom right"];

    [ModuleInitializer]
    internal static void InstallLabels() => Installers.Add(w =>
    {
        w.Commands["label"] = LabelCommand;
        w.Commands["outline"] = c =>
        {
            bool target = c.Args.Count > 0 && c.Args[0] is GameObject;
            var o = target ? (GameObject)c.Args[0]! : c.Self;
            int i = target ? 1 : 0;
            if (i >= c.Args.Count || WordAt(c, i) is "off" or "none" || c.Args[i] is false || (c.Args[i] is string e && e.Length == 0))
            {
                o.Outline = null;
                return;
            }
            o.Outline = ToText(c.Args[i]);
            if (i + 1 < c.Args.Count) o.OutlineWidth = Math.Clamp(c.Number(c.Args[i + 1]), 0.5, 20);
        };
        w.Functions["label_text"] = a =>
        {
            if (a.Count != 2 || a[0] is not GameObject g) throw new ScriptError("'label_text' needs an object and a label's name, like label_text(self, \"cost\").", 0);
            string name = ToText(a[1]);
            return g.Labels?.FirstOrDefault(l => l.Name == name)?.TextNow() ?? "";
        };
    });

    static void LabelCommand(CommandContext c)
    {
        var o = c.Args.Count > 0 && c.Args[0] is GameObject g ? g : c.Self;
        int i = c.Args.Count > 0 && c.Args[0] is GameObject ? 1 : 0;
        if (i >= c.Args.Count) throw c.Error("'label' needs a name and a text: label \"cost\" \"3\" at top left (or 'label none').");
        if (WordAt(c, i) is "none" or "off" && i + 1 >= c.Args.Count) { o.Labels = null; return; }
        string name = ToText(c.Args[i]);
        if (i + 1 >= c.Args.Count) throw c.Error($"'label' needs a text after the name: label \"{name}\" \"3\" at top left (or label \"{name}\" none).");
        if (WordAt(c, i + 1) is "none" or "off") { o.Labels?.RemoveAll(l => l.Name == name); return; }

        var label = o.Labels?.FirstOrDefault(l => l.Name == name);
        if (label == null)
        {
            label = new ObjectLabel { Name = name };
            (o.Labels ??= []).Add(label);
        }
        object? v = c.Args[i + 1];
        string text = v is double d ? ShortNumber(d) : ToText(v);
        label.World = c.World;
        label.Owner = c.Mechanism;
        label.Template = text.IndexOf('{') >= 0 ? text : null;
        label.Text = c.World.Interpolate(text, c.Mechanism, c.Locals);
        label.BaseWidth = Math.Max(1, o.Width);
        if (c.Has("at")) label.Anchor = AnchorOf(c);
        if (c.Named.TryGetValue("by", out var by) && by.Count >= 2) { label.Dx = c.Number(by[0]); label.Dy = c.Number(by[1]); }
        else if (c.Has("at")) label.Dx = label.Dy = 0;
        if (c.Named.TryGetValue("with", out var with))
        {
            int colours = 0;
            foreach (var x in with)
                if (x is double size) label.Size = Math.Clamp(size, 3, 200);
                else if (x is string col && col.Length > 0) { if (colours++ == 0) label.Color = col; else label.Back = col; }
            if (colours < 2) label.Back = null;
        }
    }

    /// <summary>The place after 'at': bare words (<c>at top left</c>) or a text (<c>at "top left"</c>).</summary>
    static string AnchorOf(CommandContext c)
    {
        var parts = new List<string>();
        var raw = c.RawNamed["at"];
        var vals = c.Named["at"];
        for (int k = 0; k < raw.Count; k++)
            if (raw[k] is NameExpr { Path.Length: 1 } n && (k >= vals.Count || vals[k] is null or string)) parts.Add(n.Path[0]);
            else if (k < vals.Count && vals[k] is string s) parts.AddRange(s.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries));
        bool top = false, bottom = false, left = false, right = false;
        foreach (var p in parts)
            switch (p.ToLowerInvariant())
            {
                case "top" or "up" or "topleft" or "topright": top = true; if (p.EndsWith("left", StringComparison.OrdinalIgnoreCase)) left = true; if (p.EndsWith("right", StringComparison.OrdinalIgnoreCase)) right = true; break;
                case "bottom" or "down" or "bottomleft" or "bottomright": bottom = true; if (p.EndsWith("left", StringComparison.OrdinalIgnoreCase)) left = true; if (p.EndsWith("right", StringComparison.OrdinalIgnoreCase)) right = true; break;
                case "left": left = true; break;
                case "right": right = true; break;
                case "center" or "centre" or "middle": break;
                default: throw c.Error($"A label goes at one of: {string.Join(", ", LabelAnchors)} — not '{p}'.");
            }
        string v = top ? "top" : bottom ? "bottom" : "";
        string h = left ? "left" : right ? "right" : "";
        return v.Length == 0 && h.Length == 0 ? "center" : v.Length == 0 ? h : h.Length == 0 ? v : v + " " + h;
    }
}
