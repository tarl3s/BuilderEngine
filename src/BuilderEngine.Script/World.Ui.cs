using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>A line someone says ('say') or a question ('ask') on the dialogue panel. It types itself out.</summary>
public sealed class Dialogue
{
    /// <summary>The name on the panel ("" = a narrator, no name).</summary>
    public string Speaker = "";
    public GameObject? SpeakerObject;
    public string Text = "";
    /// <summary>The choices of an 'ask' (null for 'say').</summary>
    public List<string>? Options;
    public int Selected;
    public string? Answer;
    /// <summary>How many letters are shown so far.</summary>
    public double Typed;
    public bool Closed;
    /// <summary>'say … for 2': closes by itself after this many real seconds once typed (0 = waits for a key).</summary>
    public double AutoClose;
    public bool FullyTyped => Typed >= Text.Length;
    public string Visible => FullyTyped ? Text : Text[..(int)Typed];
}

/// <summary>A short message at the top of the screen ('notify').</summary>
public sealed class Toast
{
    public string Text = "";
    public double Left;
    public double Total;
}

/// <summary>A text or a bar on top of the game ('show text', 'show bar', the Label and HealthBar mechanisms).</summary>
public sealed class HudItem
{
    public string Id = "";
    public bool IsBar;
    /// <summary>The text as written, with {name} parts ("Score: {score}").</summary>
    public string Template = "";
    /// <summary>The text shown now ({name} parts filled in every frame).</summary>
    public string Text = "";
    /// <summary>Screen position (view pixels from the top-left), or, with <see cref="Anchor"/>, an offset from the object.</summary>
    public double X, Y;
    public GameObject? Anchor;
    public string Color = "#ECECEC";
    public double Size = 18;
    public double Width = 160, Height = 12;
    public double Value, Max = 1;
    /// <summary>0..1, how full a bar is.</summary>
    public double Fraction => Max <= 0 ? 0 : Math.Clamp(Value / Max, 0, 1);
    internal Expr? ValueExpr, MaxExpr;
    internal MechanismInstance? Owner;
}

/// <summary>
/// Dialogue, choices, messages and the HUD:
/// <code>
/// say Boss "You came..."             (waits for space / enter / a click)   say "Hi!" (this object speaks)
/// say Boss "Run!" for 1.5            (closes by itself)                    narrate "Long ago..."
/// ask "Help me?" "Yes", "No"         (waits; then 'answer' is "Yes" or "No")
/// notify "Checkpoint!"               (a message that goes away by itself)
/// show text "Score: {score}" at 16, 16 with "#E8B04B", 24      hide text "Score: {score}"
/// show bar hp of max at 16, 50 with "#E05D5D"                  show bar Health.hp of Health.max at self   hide bar hp
/// hide hud
/// </code>
/// </summary>
public sealed partial class World
{
    public const double TypeSpeed = 45; // letters per real second
    public Dialogue? Dialogue { get; private set; }
    public List<Toast> Toasts { get; } = [];
    public List<HudItem> Hud { get; } = [];

    static readonly HashSet<string> ConfirmKeys = new(StringComparer.OrdinalIgnoreCase) { "space", "enter", "e", "z", "x", "j" };

    [ModuleInitializer]
    internal static void InstallUi() => Installers.Add(w =>
    {
        w.Commands["say"] = c => c.World.SayCommand(c, narrator: false);
        w.Commands["narrate"] = c => c.World.SayCommand(c, narrator: true);
        w.Commands["ask"] = AskCommand;
        w.Commands["notify"] = c =>
        {
            if (c.Args.Count == 0) throw c.Error("'notify' needs a text, like notify \"Checkpoint!\".");
            var world = c.World;
            string text = world.Interpolate(string.Join(" ", c.Args.Select(ToText)), c.Mechanism, c.Locals);
            world.Toasts.Add(new Toast { Text = text, Left = 2.5, Total = 2.5 });
            if (world.Toasts.Count > 4) world.Toasts.RemoveAt(0);
        };
        w.Commands["show text"] = ShowTextCommand;
        w.Commands["show bar"] = ShowBarCommand;
        w.Commands["hide text"] = c => c.World.HideHud(c, bar: false);
        w.Commands["hide bar"] = c => c.World.HideHud(c, bar: true);
        w.Commands["hide hud"] = c => c.World.Hud.Clear();
    });

    // ---------------- Dialogue ----------------

    void SayCommand(CommandContext c, bool narrator)
    {
        GameObject? speaker = c.Self;
        object? text;
        int forAt = IndexOfWord(c, "for");
        int count = forAt >= 0 ? forAt : c.Args.Count;
        if (narrator) { speaker = null; text = count > 0 ? c.Args[0] : null; }
        else if (count >= 2)
        {
            speaker = ObjectAt(c, 0);
            if (speaker == null) throw c.Error($"There's no object called '{DescribeArg(c, 0)}' to speak.");
            text = c.Args[1];
        }
        else text = count == 1 ? c.Args[0] : null;
        if (text == null) throw c.Error(narrator ? "'narrate' needs a text: narrate \"Long ago...\"." : "'say' needs a text: say \"Hello!\" or say Boss \"You came...\".");
        var d = new Dialogue
        {
            Speaker = speaker?.Name ?? "",
            SpeakerObject = speaker,
            Text = Interpolate(ToText(text), c.Mechanism, c.Locals),
            AutoClose = NumberAfterWord(c, "for") ?? 0,
        };
        OpenDialogue(d);
        c.WaitUntil(() => d.Closed);
    }

    static void AskCommand(CommandContext c)
    {
        var world = c.World;
        int first = 0;
        GameObject? speaker = null;
        if (c.Args.Count > 0 && c.Args[0] is GameObject g) { speaker = g; first = 1; }
        if (c.Args.Count - first < 3) throw c.Error("'ask' needs a question and at least two answers: ask \"Help me?\" \"Yes\", \"No\".");
        var d = new Dialogue
        {
            Speaker = speaker?.Name ?? "",
            SpeakerObject = speaker,
            Text = world.Interpolate(ToText(c.Args[first]), c.Mechanism, c.Locals),
            Options = c.Args.Skip(first + 1).Select(a => world.Interpolate(ToText(a), c.Mechanism, c.Locals)).ToList(),
        };
        world.OpenDialogue(d);
        var locals = c.Locals;
        c.WaitUntil(() =>
        {
            if (!d.Closed) return false;
            locals["answer"] = d.Answer ?? "";
            return true;
        });
    }

    void OpenDialogue(Dialogue d)
    {
        if (Dialogue is { Closed: false } old) old.Closed = true; // a newer line takes over; the older one's 'when' goes on
        Dialogue = d;
    }

    void CloseDialogue(Dialogue d)
    {
        d.Closed = true;
        if (Dialogue == d) Dialogue = null;
    }

    void Choose(Dialogue d, int i)
    {
        d.Selected = i;
        d.Answer = d.Options![i];
        CloseDialogue(d);
    }

    /// <summary>A key while a dialogue is open: finish typing, move the choice, pick it, or go on. True = the dialogue used it.</summary>
    bool UiKey(string key)
    {
        var d = Dialogue;
        if (d == null || d.Closed) return false;
        bool confirm = ConfirmKeys.Contains(key);
        if (d.Options == null)
        {
            if (!confirm) return false;
            if (!d.FullyTyped) d.Typed = d.Text.Length;
            else CloseDialogue(d);
            return true;
        }
        if (!d.FullyTyped) { d.Typed = d.Text.Length; return true; }
        int n = d.Options.Count;
        switch (key)
        {
            case "up" or "left" or "w" or "a": d.Selected = (d.Selected + n - 1) % n; return true;
            case "down" or "right" or "s" or "d": d.Selected = (d.Selected + 1) % n; return true;
        }
        if (key.Length == 1 && key[0] is >= '1' and <= '9' && key[0] - '1' < n) { Choose(d, key[0] - '1'); return true; }
        if (key.Length == 2 && key[0] == 'd' && key[1] is >= '1' and <= '9' && key[1] - '1' < n) { Choose(d, key[1] - '1'); return true; } // "d1" from Avalonia's Key.D1
        if (confirm) { Choose(d, d.Selected); return true; }
        return true; // a choice is on screen: other keys don't reach the game
    }

    void UpdateUi(double realDt)
    {
        var d = Dialogue;
        if (d != null)
        {
            if (d.SpeakerObject is { Destroyed: true }) d.SpeakerObject = null;
            if (!d.FullyTyped) d.Typed = Math.Min(d.Text.Length, d.Typed + TypeSpeed * realDt);
            else if (d.AutoClose > 0 && (d.AutoClose -= realDt) <= 0) CloseDialogue(d);
        }
        for (int i = Toasts.Count - 1; i >= 0; i--)
            if ((Toasts[i].Left -= realDt) <= 0) Toasts.RemoveAt(i);
    }

    // ---------------- HUD ----------------

    /// <summary>"at 16, 40" → screen position; "at self" / "at Boss" → follows that object.</summary>
    static (double X, double Y, GameObject? Anchor) Placement(CommandContext c, double defX, double defY)
    {
        if (!c.Named.TryGetValue("at", out var at) || at.Count == 0) return (defX, defY, null);
        if (at[0] is GameObject g) return (0, 0, g);
        if (at[0] is string s && c.World.FindObject(s) is { } named) return (0, 0, named);
        if (at.Count < 2) throw c.Error("'at' needs x and y (from the top-left of the screen), or an object.");
        return (c.Number(at[0]), c.Number(at[1]), null);
    }

    static string? IdOf(CommandContext c) =>
        NamedWord(c, "as") is { } w ? w : c.Named.TryGetValue("as", out var l) && l.Count > 0 ? ToText(l[0]) : null;

    static void ShowTextCommand(CommandContext c)
    {
        if (c.Args.Count == 0) throw c.Error("'show text' needs a text: show text \"Score: {score}\" at 16, 16.");
        var world = c.World;
        string template = ToText(c.Args[0]);
        var (x, y, anchor) = Placement(c, 16, 16);
        string id = IdOf(c) ?? template;
        var item = world.HudFor(id, c.Mechanism, bar: false);
        item.Template = template;
        item.X = x; item.Y = y; item.Anchor = anchor;
        if (c.Named.TryGetValue("with", out var with))
            foreach (var v in with)
                if (v is string col) item.Color = col;
                else if (v is double size) item.Size = Math.Clamp(size, 6, 200);
        item.Text = world.Interpolate(template, c.Mechanism, null);
    }

    static void ShowBarCommand(CommandContext c)
    {
        if (c.Raw.Count == 0) throw c.Error("'show bar' needs a value: show bar hp of max at 16, 40.");
        var world = c.World;
        int of = IndexOfWord(c, "of");
        Expr valueExpr = c.Raw[0];
        Expr? maxExpr = of >= 0 && of + 1 < c.Raw.Count ? c.Raw[of + 1] : null;
        var (x, y, anchor) = Placement(c, 16, 50);
        string id = IdOf(c) ?? (valueExpr is NameExpr n ? n.Full : "bar");
        var item = world.HudFor(id, c.Mechanism, bar: true);
        item.ValueExpr = valueExpr;
        item.MaxExpr = maxExpr;
        item.X = x; item.Y = y; item.Anchor = anchor;
        item.Color = "#E05D5D";
        if (anchor != null) { item.Width = Math.Max(24, anchor.Width); item.Height = 5; }
        if (c.Named.TryGetValue("with", out var with))
            foreach (var v in with)
                if (v is string col) item.Color = col;
        var words = Words(c, "at", "with"); // 'size 300, 14': a wider or taller bar (a boss's health)
        int sz = words.FindIndex(x => x.Name == "size");
        if (sz >= 0 && sz + 1 < words.Count) item.Width = Math.Max(4, c.Number(words[sz + 1].Value));
        if (sz >= 0 && sz + 2 < words.Count) item.Height = Math.Max(2, c.Number(words[sz + 2].Value));
        item.Value = c.Number(c.Args[0]);
        item.Max = maxExpr != null ? c.Number(c.Args[of + 1]) : Math.Max(item.Value, 1);
    }

    HudItem HudFor(string id, MechanismInstance owner, bool bar)
    {
        foreach (var h in Hud)
            if (h.IsBar == bar && h.Owner == owner && h.Id == id) return h;
        var item = new HudItem { Id = id, Owner = owner, IsBar = bar };
        Hud.Add(item);
        return item;
    }

    void HideHud(CommandContext c, bool bar)
    {
        string? id = c.Raw.Count > 0 && c.Raw[0] is NameExpr n && !(c.Args.Count > 0 && c.Args[0] is string) ? n.Full
            : c.Args.Count > 0 ? ToText(c.Args[0]) : null;
        // This mechanism's own item first; otherwise every item with that name.
        int removed = Hud.RemoveAll(h => h.IsBar == bar && h.Owner == c.Mechanism && (id == null || h.Id == id || h.Template == id));
        if (removed == 0 && id != null) Hud.RemoveAll(h => h.IsBar == bar && (h.Id == id || h.Template == id));
    }

    void UpdateHud()
    {
        for (int i = Hud.Count - 1; i >= 0; i--)
        {
            var h = Hud[i];
            if (h.Owner is { Owner.Destroyed: true } || h.Anchor is { Destroyed: true }) { Hud.RemoveAt(i); continue; }
            if (h.Owner == null) continue;
            if (!h.IsBar) { h.Text = Interpolate(h.Template, h.Owner); continue; }
            var ctx = new Ctx(this, h.Owner);
            try
            {
                h.Value = ToNumber(Eval(h.ValueExpr!, ctx), h.ValueExpr!.Line);
                if (h.MaxExpr != null) h.Max = ToNumber(Eval(h.MaxExpr, ctx), h.MaxExpr.Line);
            }
            catch (ScriptError e) { WarnOnce($"{h.Owner.Def.Name}: a bar can't read its value ({e.Message})"); }
        }
    }
}
