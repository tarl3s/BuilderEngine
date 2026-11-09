using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BuilderEngine.Script;

/// <summary>
/// EasyEngine's Custom rules (<c>when → do</c>, a JSON list) translated into a Builder Script mechanism, the way
/// EasyEngine runs them (PlayWindow.RunCustom): every frame, rules started by a moment (a key press, a timer, a touch,
/// a click…) are decided first, then every rule runs in order, each seeing what the ones before it did; a block with
/// several WHEN rows fires when any of them does and runs its DO rows in order; "when it dies" runs at once. Values and
/// conditions written as formulas stay EasyEngine formulas (easy_value). An event or action that can't be translated is
/// said once in the console and leaves its rule out.
/// </summary>
public sealed partial class EasyCompat
{
    /// <summary>One rule as EasyEngine reads it: the event (and its fields before "do"), the action and its fields.</summary>
    sealed class Rule
    {
        public string When = "always", Do = "";
        public int Group;
        public bool Reverse;
        public readonly Dictionary<string, string> WT = new(StringComparer.OrdinalIgnoreCase), T = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double> WN = new(StringComparer.OrdinalIgnoreCase), N = new(StringComparer.OrdinalIgnoreCase);

        public string WStr(string k, string d = "") => WT.TryGetValue(k, out var v) ? v : WN.TryGetValue(k, out var n) ? Fmt(n) : d;
        public string Str(string k, string d = "") => T.TryGetValue(k, out var v) ? v : N.TryGetValue(k, out var n) ? Fmt(n) : d;
        public bool Has(string k) => T.ContainsKey(k) || N.ContainsKey(k);
    }

    static readonly string[] TimingKeys = ["every", "seconds", "cooldown"];

    /// <summary>EasyEngine's tolerant rule reader (ParseCustom): comments, trailing commas, numbers in quotes.</summary>
    static List<Rule> ParseRules(string json)
    {
        var list = new List<Rule>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return list; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var r = new Rule();
                bool seenDo = false, reverse = false;
                foreach (var p in el.EnumerateObject())
                {
                    string name = p.Name.ToLowerInvariant();
                    if (name == "when") { r.When = (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "always" : "always").ToLowerInvariant(); continue; }
                    if (name == "do") { r.Do = (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "").ToLowerInvariant(); seenDo = true; continue; }
                    if (name == "block")
                    {
                        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var g)) r.Group = g;
                        else if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out var gs)) r.Group = gs;
                        continue;
                    }
                    if (name == "reverse") { reverse = p.Value.ValueKind == JsonValueKind.True || p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "true" or "1"; continue; }
                    bool guard = name is "cond" or "if" or "ifop" or "ifval";
                    bool toTrigger = !seenDo && !guard;
                    switch (p.Value.ValueKind)
                    {
                        case JsonValueKind.Number:
                            (toTrigger ? r.WN : r.N)[name] = p.Value.GetDouble();
                            break;
                        case JsonValueKind.String:
                        {
                            string v = p.Value.GetString() ?? "";
                            if (v.TrimStart().StartsWith("expr:", StringComparison.OrdinalIgnoreCase)) v = v.TrimStart()[5..].Trim();
                            (toTrigger ? r.WT : r.T)[name] = v;
                            if (Project.TryNumber(v, out var qn)) (toTrigger ? r.WN : r.N)[name] = qn;
                            break;
                        }
                        case JsonValueKind.True:
                        case JsonValueKind.False:
                            (toTrigger ? r.WN : r.N)[name] = p.Value.ValueKind == JsonValueKind.True ? 1 : 0;
                            break;
                    }
                }
                foreach (var k in TimingKeys) // an event's timing written after "do" is still the event's
                    if (!r.WN.ContainsKey(k) && !r.WT.ContainsKey(k) && TriggerUses(r.When, k))
                    {
                        if (r.N.TryGetValue(k, out var n)) r.WN[k] = n;
                        if (r.T.TryGetValue(k, out var s)) r.WT[k] = s;
                    }
                if (reverse) ReverseRule(r);
                list.Add(r);
            }
        }
        return list;
    }

    static bool TriggerUses(string when, string key) => (when, key) switch
    {
        ("timer", "every") or ("after", "seconds") or ("after", "every") or ("keypress", "cooldown") or ("keyidle", "seconds") => true,
        _ => false,
    };

    /// <summary>"↔ Reverse": the action does its opposite (EasyEngine's ReverseRule).</summary>
    static void ReverseRule(Rule r)
    {
        void Neg(string k, double def)
        {
            bool any = false;
            if (r.N.TryGetValue(k, out var v)) { r.N[k] = -v; any = true; }
            if (r.T.TryGetValue(k, out var s) && s.Trim().Length > 0) { r.T[k] = Project.TryNumber(s, out var sv) ? Fmt(-sv) : $"-({s})"; any = true; }
            if (!any) r.N[k] = -def;
        }
        void Flip(string k, string def)
        {
            string cur = r.T.TryGetValue(k, out var s) ? s : r.N.TryGetValue(k, out var n) ? (n >= 0.5 ? "on" : "off") : def;
            bool on = IsOn(cur);
            r.T[k] = on ? "off" : "on";
            r.N[k] = on ? 0 : 1;
        }
        switch (r.Do)
        {
            case "hide": r.Do = "show"; break;
            case "show": r.Do = "hide"; break;
            case "additem": r.Do = "removeitem"; break;
            case "removeitem": r.Do = "additem"; break;
            case "listadd": r.Do = "listremove"; break;
            case "listremove": r.Do = "listadd"; break;
            case "addvar": case "addlocal":
                if (r.T.TryGetValue("expr", out var ex) && ex.Trim().Length > 0) r.T["expr"] = $"-({ex})"; else Neg("amount", 1);
                break;
            case "velocity": Neg("vx", 0); Neg("vy", 0); break;
            case "move": Neg("dx", 0); Neg("dy", 0); break;
            case "accelerate": Neg("ax", 0); Neg("ay", 0); break;
            case "rotate": Neg("speed", 90); break;
            case "heal": Neg("amount", 1); break;
            case "boost":
            {
                double f = r.N.TryGetValue("factor", out var fv) ? fv : 2;
                r.T.Remove("factor");
                r.N["factor"] = f == 0 ? 1 : 1 / f;
                break;
            }
            case "controls": Flip("on", "off"); break;
            case "ability": Flip("on", "on"); break;
            case "behavior": Flip("on", "off"); break;
            case "setactive": Flip("on", "on"); break;
            case "playsound": r.Reverse = true; break;
        }
    }

    // ---------------- The mechanism ----------------

    /// <summary>The mechanism that plays an object's Custom rules (made and loaded once per different set of rules).</summary>
    string? RulesMechanism(World w, SceneObject so, string script)
    {
        var kinds = EasyLink.EasyBehaviors(so);
        string name = "EasyRules_" + Hash(so.Name + "\n" + string.Join(",", kinds) + "\n" + script);
        if (w.Library.ContainsKey(name)) return name;
        string src = TranslateRules(name, so.Name, kinds, script, out var unsupported);
        if (src.Length > 0) _generated[name] = src;
        foreach (var u in unsupported) Note(w, u);
        if (src.Length == 0) return null;
        try { w.Load(src); }
        catch (ScriptError e)
        {
            Note(w, $"the Custom rules of '{so.Name}' couldn't be translated ({e.Message})");
            return null;
        }
        return name;
    }

    /// <summary>
    /// The Builder Script mechanism <paramref name="name"/> that plays these EasyEngine Custom rules (a JSON list) on an
    /// object with these behaviors; "" when there are no rules. <paramref name="unsupported"/> lists what it left out.
    /// </summary>
    public static string TranslateRules(string name, string objectName, IReadOnlyList<string> behaviorKinds, string script, out List<string> unsupported)
    {
        var rules = ParseRules(script);
        var notes = new List<string>();
        unsupported = notes;
        if (rules.Count == 0) return "";
        var tr = new RuleWriter(rules, behaviorKinds, notes);
        return tr.Write(name, objectName);
    }

    /// <summary>Writes the Builder Script of one object's rules.</summary>
    sealed class RuleWriter(List<Rule> rules, IReadOnlyList<string> kinds, List<string> notes)
    {
        readonly StringBuilder _sb = new();
        int _ind;
        readonly HashSet<string> _globals = new(StringComparer.Ordinal);
        bool _usesClick;
        bool Has(string kind) => kinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

        void L(string line) => _sb.Append(' ', _ind * 4).Append(line).Append('\n');
        void In() => _ind++;

        /// <summary>A line that isn't a comment was written since <paramref name="mark"/> (a block can't be only comments).</summary>
        bool WroteCode(int mark)
        {
            foreach (var line in _sb.ToString(mark, _sb.Length - mark).Split('\n'))
                if (line.Trim().Length > 0 && !line.TrimStart().StartsWith('#')) return true;
            return false;
        }
        void Out() => _ind--;

        void Unsupported(string what)
        {
            if (!notes.Contains(what)) notes.Add(what);
        }

        static bool Instant(string when) => when is "keypress" or "timer" or "after" or "touch" or "click" or "rightclick" or "mousepress"
            or "wheelup" or "wheeldown" or "landed" or "hurt" or "interact" or "keynear" or "keyidle";

        public string Write(string name, string objectName)
        {
            var body = new StringBuilder();
            // Which rules do something here (an action that can't play leaves its rule out; a block runs if any DO does).
            var useful = new bool[rules.Count];
            for (int i = 0; i < rules.Count; i++)
            {
                int first = i, last = i;
                if (rules[i].Group != 0)
                {
                    while (first > 0 && rules[first - 1].Group == rules[i].Group) first--;
                    while (last + 1 < rules.Count && rules[last + 1].Group == rules[i].Group) last++;
                }
                int toucher = Enumerable.Range(first, last - first + 1).FirstOrDefault(k => rules[k].When == "touch", i);
                int m = _sb.Length;
                Act(rules[i], i, toucher);
                useful[i] = WroteCode(m);
                _sb.Length = m;
            }
            for (int i = 0; i < rules.Count; i++)
                if (rules[i].Group != 0 && useful[i])
                    for (int k = 0; k < rules.Count; k++) if (rules[k].Group == rules[i].Group) useful[k] = true;

            // The tick: instant events first, then the rules in order.
            var tick = new StringBuilder();
            var save = _sb.Length;
            _ind = 2;
            for (int i = 0; i < rules.Count; i++)
                if (Instant(rules[i].When) && useful[i]) { L($"# rule {i + 1}: {Summary(rules[i])}"); Decide(rules[i], i); }
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                if (r.When == "death") continue;
                if (!useful[i]) { L($"# rule {i + 1}: {Summary(r)} — nothing it does plays here"); continue; }
                if (r.Group == 0)
                {
                    if (!Instant(r.When)) { L($"# rule {i + 1}: {Summary(r)}"); Decide(r, i); }
                    L($"if r{i + 1}");
                    In();
                    int mark = _sb.Length;
                    Act(r, i, i);
                    if (!WroteCode(mark)) L("skip = 0");
                    Out();
                    continue;
                }
                int end = i;
                while (end + 1 < rules.Count && rules[end + 1].Group == r.Group) end++;
                L($"# block {r.Group}: rules {i + 1}–{end + 1}");
                for (int k = i; k <= end; k++)
                    if (!Instant(rules[k].When) && rules[k].When != "death") { L($"# rule {k + 1}: {Summary(rules[k])}"); Decide(rules[k], k); }
                var fires = Enumerable.Range(i, end - i + 1).Where(k => rules[k].When != "death").Select(k => $"r{k + 1}").ToList();
                if (fires.Count > 0)
                {
                    int toucher = Enumerable.Range(i, end - i + 1).FirstOrDefault(k => rules[k].When == "touch", -1);
                    L($"if {string.Join(" or ", fires)}");
                    In();
                    int mark = _sb.Length;
                    for (int k = i; k <= end; k++)
                        if (rules[k].Do.Length > 0) Act(rules[k], k, toucher >= 0 ? toucher : k);
                    if (!WroteCode(mark)) L("skip = 0");
                    Out();
                }
                i = end;
            }
            bool tickCode = WroteCode(save);
            if (tickCode) tick.Append(_sb, save, _sb.Length - save);
            _sb.Length = save;

            // "When it dies": at once, before it goes.
            var death = new StringBuilder();
            if (rules.Any(r => r.When == "death"))
            {
                if (!Has("Health")) Unsupported("“when it dies” on an object without Health");
                else
                {
                    _ind = 2;
                    var done = new HashSet<int>();
                    for (int i = 0; i < rules.Count; i++)
                    {
                        var r = rules[i];
                        if (r.When != "death") continue;
                        L($"# rule {i + 1}: {Summary(r)}");
                        string guard = Guards(r);
                        if (guard.Length > 0) { L($"if {guard}"); In(); }
                        int mark = _sb.Length;
                        if (r.Group == 0) Act(r, i, i);
                        else if (done.Add(r.Group))
                            for (int k = 0; k < rules.Count; k++)
                                if (rules[k].Group == r.Group && rules[k].Do.Length > 0) Act(rules[k], k, k);
                        if (!WroteCode(mark)) L("skip = 0");
                        if (guard.Length > 0) Out();
                    }
                    death.Append(_sb);
                    _sb.Clear();
                }
            }

            if (tick.Length == 0 && death.Length == 0) return ""; // nothing of it plays here (each reason is in the notes)
            body.Append($"mechanism {name}\n");
            body.Append($"    # The Custom rules of '{Clean(objectName)}' in EasyEngine, translated into Builder Script when the game starts\n");
            body.Append("    # (EasyCompat). The project keeps the rules as EasyEngine wrote them: change them in EasyEngine.\n\n");
            if (_globals.Count > 0)
            {
                body.Append("    when start\n");
                body.Append("        global ").Append(string.Join(", ", _globals.Order(StringComparer.Ordinal))).Append('\n');
                body.Append('\n');
            }
            if (_usesClick) body.Append("    when clicked\n        clicked_now = true\n\n");
            if (tick.Length > 0) body.Append("    when tick\n").Append(tick).Append('\n');
            if (death.Length > 0) body.Append("    when EasyHealth.died\n").Append(death).Append('\n');
            return body.ToString();
        }

        // ---------------- Events ----------------

        /// <summary>Writes the lines that set r{i} (true when the rule fires this frame, its guards included).</summary>
        void Decide(Rule r, int i)
        {
            string v = $"r{i + 1}";
            string ev = Event(r, i, v);
            string guard = Guards(r);
            L($"{v} = " + (guard.Length == 0 ? ev : $"({ev}) and {guard}"));
        }

        string Guards(Rule r)
        {
            var parts = new List<string>();
            if (r.T.TryGetValue("if", out var iv) && iv.Trim().Length > 0)
                parts.Add($"easy_compare({VarRead(iv)}, {Lit(r.Str("ifop", ">="))}, {Num(r, "ifval", 0)})");
            if (r.T.TryGetValue("cond", out var c) && c.Trim().Length > 0) parts.Add(Formula(c));
            return string.Join(" and ", parts);
        }

        /// <summary>The event as a value (writing the lines it needs first); state lives in r{i}_… variables.</summary>
        string Event(Rule r, int i, string v)
        {
            string s = $"r{i + 1}_";
            switch (r.When)
            {
                case "start":
                    L($"{s}now = not {s}done");
                    L($"{s}done = true");
                    return $"{s}now";
                case "always": return "true";
                case "key": case "keydown": return $"easy_key({Lit(KeyOf(r.WStr("key", "Space")))})";
                case "keypress":
                    L($"{s}t -= dt");
                    L($"{s}now = easy_key({Lit(KeyOf(r.WStr("key", "Space")))}) and {s}t <= 0");
                    L($"if {s}now: {s}t = max(0.05, {WNum(r, "cooldown", 0.3)})");
                    return $"{s}now";
                case "timer":
                    L($"if not {s}started");
                    In(); L($"{s}started = true"); L($"{s}t = max(0.05, {WNum(r, "every", 1)})"); Out();
                    L($"{s}t -= dt");
                    L($"{s}now = {s}t <= 0");
                    L($"if {s}now: {s}t += max(0.05, {WNum(r, "every", 1)})");
                    return $"{s}now";
                case "after":
                {
                    string secs = r.WT.ContainsKey("seconds") || r.WN.ContainsKey("seconds") ? WNum(r, "seconds", 1) : WNum(r, "every", 1);
                    L($"{s}now = false");
                    L($"if not {s}done");
                    In(); L($"{s}t += dt"); L($"if {s}t >= {secs}"); In(); L($"{s}now = true"); L($"{s}done = true"); Out(); Out();
                    return $"{s}now";
                }
                case "touch":
                    L($"{s}obj = easy_touch({Lit(r.WStr("name"))})");
                    L($"{s}now = {s}obj and not {s}was");
                    L($"{s}was = {s}obj");
                    return $"{s}now";
                case "ifvar": return $"{VarRead(r.WStr("var"))} {Op(r.WStr("op", ">="))} {WNum(r, "value", 0)}";
                case "iflocal": return $"easy_local({Lit(r.WStr("var"))}) {Op(r.WStr("op", ">="))} {WNum(r, "value", 0)}";
                case "becomes":
                    L($"{s}is = {VarRead(r.WStr("var"))} {Op(r.WStr("op", ">="))} {WNum(r, "value", 0)}");
                    L($"{s}now = {s}started and {s}is and not {s}was");
                    L($"{s}started = true");
                    L($"{s}was = {s}is");
                    return $"{s}now";
                case "hasitem": return $"easy_var({Lit("item:" + r.WStr("item"))}) >= 1";
                case "iftext":
                    return $"easy_text_is({Lit(r.WStr("var"))}, {Lit(r.WStr("op", "is"))}, {Lit(r.WStr("value"))}, {Bool(r.WStr("local"))})";
                case "iflist":
                    return $"easy_list_is({Lit(r.WStr("var"))}, {Lit(r.WStr("op", "contains"))}, {Lit(r.WStr("value"))}, {Bool(r.WStr("local"))})";
                case "isnight": return "easy_var(\"isNight\") >= 0.5";
                case "isday": return "easy_var(\"isNight\") < 0.5";
                case "expr": return $"easy_value({Lit(r.WStr("expr"))}) != 0";
                case "click":
                    _usesClick = true;
                    L($"{s}now = clicked_now");
                    L("clicked_now = false");
                    return $"{s}now";
                case "rightclick":
                    L($"{s}is = mouse_down(\"right\") and mouse_over()");
                    L($"{s}now = {s}is and not {s}was");
                    L($"{s}was = {s}is");
                    return $"{s}now";
                case "mousepress":
                    L($"{s}is = mouse_down()");
                    L($"{s}now = {s}is and not {s}was");
                    L($"{s}was = {s}is");
                    return $"{s}now";
                case "mouseover": case "hover": return "mouse_over()";
                case "mousedown": return "mouse_down() and mouse_over()";
                case "landed":
                    L($"{s}now = false");
                    L("if self.on_ground");
                    In(); L($"{s}now = {s}air >= 2"); L($"{s}air = 0"); Out();
                    L("else");
                    In(); L($"{s}air += 1"); Out();
                    return $"{s}now";
                case "hurt":
                    if (!Has("Health")) { Unsupported("“when it gets hurt” on an object without Health"); return "false"; }
                    L($"{s}n = self.EasyHealth.hits");
                    L($"{s}now = {s}n > {s}seen");
                    L($"{s}seen = {s}n");
                    return $"{s}now";
                case "interact":
                    L($"{s}n = easy_interact_count()");
                    L($"{s}now = {s}n > {s}seen");
                    L($"{s}seen = {s}n");
                    return $"{s}now";
                case "keynear":
                    L($"{s}down = easy_key({Lit(KeyOf(r.WStr("key", "E")))})");
                    L($"{s}now = {s}down and not {s}was and easy_near({Lit(r.WStr("name", "Player"))}, {WNum(r, "radius", 100)})");
                    L($"{s}was = {s}down");
                    return $"{s}now";
                case "keyidle":
                    L($"{s}now = false");
                    L($"if easy_key({Lit(KeyOf(r.WStr("key", "Space")))})");
                    In(); L($"{s}armed = true"); L($"{s}t = 0"); Out();
                    L($"else if {s}armed");
                    In(); L($"{s}t += dt"); L($"if {s}t >= max(0, {WNum(r, "seconds", 1)})"); In(); L($"{s}armed = false"); L($"{s}now = true"); Out(); Out();
                    return $"{s}now";
                case "allgone":
                    L($"{s}n = easy_count({Lit(r.WStr("name"))})");
                    L($"{s}now = {s}n == 0 and {s}last >= 1");
                    L($"{s}last = {s}n");
                    return $"{s}now";
                case "wheelup": case "wheeldown":
                    Unsupported("the mouse wheel event in rules");
                    return "false";
                default:
                    Unsupported($"the rule event “{r.When}”");
                    return "false";
            }
        }

        // ---------------- Actions ----------------

        /// <summary>The lines of one action; <paramref name="toucher"/> is the rule whose touch "other" means.</summary>
        void Act(Rule r, int i, int toucher)
        {
            L($"# {r.Do}");
            switch (r.Do)
            {
                case "": break;
                case "velocity":
                    L($"self.vx = {Num(r, "vx", 0)}");
                    L($"self.vy = {Num(r, "vy", 0)}");
                    break;
                case "move":
                    L($"self.x += ({Num(r, "dx", 0)}) * dt");
                    L($"self.y += ({Num(r, "dy", 0)}) * dt");
                    break;
                case "accelerate": case "accel":
                    L($"self.vx += ({Num(r, "ax", 0)}) * dt");
                    L($"self.vy += ({Num(r, "ay", 0)}) * dt");
                    break;
                case "jump": L($"if self.on_ground: self.vy = -({Num(r, "force", 600)})"); break;
                case "rotate": L($"self.angle += ({Num(r, "speed", 90)}) * dt"); break;
                case "shoot":
                    L($"easy_shoot {Num(r, "directions", 8)}, {Num(r, "speed", 420)}, {Num(r, "life", 2)}, {Lit(r.Str("bullet", "Bullet"))}, {Lit(r.Str("at", r.Str("aim", r.Str("target", ""))))}");
                    break;
                case "spawn":
                    L($"easy_spawn {Lit(r.T.ContainsKey("object") ? r.Str("object") : r.Str("name", "Enemy"))}, {Lit(r.Str("at", ""))}");
                    break;
                case "destroy": WithTarget(r, toucher, t => L($"destroy {t}")); break;
                case "hide": WithTarget(r, toucher, t => L($"hide {t}")); break;
                case "show":
                    WithTarget(r, toucher, t => { L($"show {t}"); L($"if {t}.opacity < 0.05: {t}.opacity = 1"); });
                    break;
                case "goscene": L($"easy_goto {Lit(r.Str("scene"))}"); break;
                case "setvar": SetVar(r.Str("var"), Value(r, 0, "to", "value"), add: false); break;
                case "addvar": SetVar(r.Str("var"), Value(r, 1, "amount", "value", "by"), add: true); break;
                case "setlocal": L($"easy_set_local {Lit(r.Str("var"))}, {Value(r, 0, "to", "value")}"); break;
                case "addlocal": L($"easy_add_local {Lit(r.Str("var"))}, {Value(r, 1, "amount", "value", "by")}"); break;
                case "settext": case "addtext": case "toggle": case "setcolor":
                {
                    bool local = IsOn(r.Str("local", "0"));
                    string var = r.Str("var");
                    string set = local ? "easy_set_local" : "easy_set";
                    string read = local ? $"easy_local({Lit(var)})" : $"easy_text({Lit(var)})";
                    string words = $"easy_format({Lit(r.Str("to", r.Str("text", r.Str("value"))))})";
                    if (r.Do == "settext") L($"{set} {Lit(var)}, {words}");
                    else if (r.Do == "addtext") L($"{set} {Lit(var)}, {read} + {words}");
                    else if (r.Do == "toggle") L($"{set} {Lit(var)}, {(local ? read : $"easy_var({Lit(var)})")} < 0.5");
                    else L($"{set} {Lit(var)}, {words}");
                    break;
                }
                case "tint":
                {
                    string color = r.Str("color", r.Str("to", r.Str("value", "")));
                    WithTarget(r, toucher, t => L(color.Length == 0 ? $"{t}.color = none" : $"{t}.color = easy_format({Lit(color)})"));
                    break;
                }
                case "listadd": case "listremove": case "listclear":
                    L($"easy_list {Lit(r.Do[4..])}, {Lit(r.Str("var"))}, easy_format({Lit(r.Str("value", r.Str("item", r.Str("text", r.Str("to")))))}), {Bool(r.Str("local", "0"))}");
                    break;
                case "set": SetProp(r, toucher); break;
                case "additem": L($"easy_add {Lit("item:" + r.Str("item"))}, {Num(r, "amount", 1)}"); break;
                case "removeitem":
                    L($"easy_set {Lit("item:" + r.Str("item"))}, max(0, easy_var({Lit("item:" + r.Str("item"))}) - ({Num(r, "amount", 1)}))");
                    break;
                case "boost": L($"easy_speed {Num(r, "factor", 2)}, \"boost\""); break;
                case "setspeed": L($"easy_speed {Num(r, "factor", 1)}, \"set\""); break;
                case "say": L($"run easy_say {Lit(Text(r.Str("text")))}, {Lit(r.Str("speaker"))}, false"); break;
                case "save": L("save game"); break;
                case "load": L("load game"); break;
                case "controls": L($"easy_controls {On(r, "on", false)}"); break;
                case "ability": L($"easy_ability self, {Lit(r.Str("ability", "doubleJump"))}, {On(r, "on", true)}"); break;
                case "setjump":
                    if (Has("Platformer")) L($"self.EasyPlatformer.jump = {Num(r, "jump", 800)}");
                    else Unsupported("“change its jump height” on an object without Platformer");
                    break;
                case "heal":
                {
                    string amount = Num(r, "amount", 1);
                    WithTarget(r, toucher, t => L($"tell {t} \"easy heal\" {amount}"));
                    break;
                }
                case "behavior": L($"easy_behavior {Lit(r.Str("kind", "Patrol"))}, {On(r, "on", false)}"); break;
                case "playanim": L($"easy_anim self, {Lit(r.Str("anim"))}, {Lit(r.Str("mode", "once"))}"); break;
                case "playsound":
                    if (r.Reverse) L($"stop sound {Lit(r.Str("sound"))}");
                    else L($"play {Lit(r.Str("sound"))} with {Num(r, "volume", 0.8)}");
                    break;
                case "log":
                    L($"print easy_format({Lit(r.Str("text", r.Str("message")))})");
                    break;
                case "tween": Tween(r); break;
                case "setvolume":
                    if (r.Str("channel", "master").Equals("master", StringComparison.OrdinalIgnoreCase)) L($"volume {Num(r, "to", 1)}");
                    else Unsupported($"the volume of the “{r.Str("channel")}” channel");
                    break;
                case "emit": Unsupported("particles"); break;
                case "setactive": Unsupported("turning objects or layers on and off while playing"); break;
                case "breaktile": Unsupported("breaking tiles from a rule"); break;
                case "settime": Unsupported("setting the time of day from a rule"); break;
                default: Unsupported($"the rule action “{r.Do}”"); break;
            }
        }

        /// <summary>"self", what its touch touched ("other"), or an object by name (checked: no object, nothing happens).</summary>
        void WithTarget(Rule r, int toucher, Action<string> write)
        {
            string who = r.T.TryGetValue("target", out var t0) ? t0.Trim() : "self";
            if (who.Length == 0 || who.Equals("self", StringComparison.OrdinalIgnoreCase)) { write("self"); return; }
            string expr = who.Equals("other", StringComparison.OrdinalIgnoreCase)
                ? (rules[toucher].When == "touch" ? $"r{toucher + 1}_obj" : "none")
                : $"easy_find({Lit(who)})";
            if (expr == "none") return;
            L($"tgt_obj = {expr}");
            L("if tgt_obj");
            In();
            write("tgt_obj");
            Out();
        }

        void SetProp(Rule r, int toucher)
        {
            string prop = r.Str("prop").Trim().ToLowerInvariant();
            string v = Value(r, 0, "to", "value");
            switch (prop)
            {
                case "x": case "y": case "vx": case "vy":
                    WithTarget(r, toucher, t => L($"{t}.{prop} = {v}"));
                    break;
                case "w": case "width": WithTarget(r, toucher, t => L($"{t}.width = max(1, {v})")); break;
                case "h": case "height": WithTarget(r, toucher, t => L($"{t}.height = max(1, {v})")); break;
                case "opacity": WithTarget(r, toucher, t => L($"{t}.opacity = clamp({v}, 0, 1)")); break;
                case "rot": WithTarget(r, toucher, t => L($"{t}.angle = {v}")); break;
                case "facing":
                    WithTarget(r, toucher, t =>
                    {
                        L($"if {v} >= 0");
                        In(); L($"face {t} right"); Out();
                        L("else");
                        In(); L($"face {t} left"); Out();
                    });
                    break;
                case "health": case "hp":
                    if (r.T.TryGetValue("target", out var who) && who.Trim().Length > 0 && !who.Trim().Equals("self", StringComparison.OrdinalIgnoreCase) || !Has("Health"))
                        Unsupported("setting another object's health from a rule");
                    else L($"self.EasyHealth.hp = {v}");
                    break;
                case "speed": case "speedscale": L($"easy_speed {v}, \"set\""); break;
                case "layer": break; // EasyEngine reads no "layer" property either: the rule does nothing there too
                default: Unsupported($"setting the property “{prop}” from a rule"); break;
            }
        }

        void Tween(Rule r)
        {
            string prop = r.Str("prop", "x").Trim().ToLowerInvariant();
            string to = Value(r, 0, "to", "value");
            string dur = Num(r, "duration", 0.5);
            string ease = r.Str("ease", "linear").Trim().ToLowerInvariant() switch { "easein" => "in", "easeout" => "out", "easeinout" => "inout", _ => "linear" };
            if (IsOn(r.Str("loop", "0")) || IsOn(r.Str("yoyo", "0"))) Unsupported("looping glides (it glides once)");
            switch (prop)
            {
                case "x": L($"run move self to {to}, self.y in max(0.01, {dur}) ease {ease}"); break;
                case "y": L($"run move self to self.x, {to} in max(0.01, {dur}) ease {ease}"); break;
                case "w": L($"run resize self to {to}, self.height in max(0.01, {dur}) ease {ease}"); break;
                case "h": L($"run resize self to self.width, {to} in max(0.01, {dur}) ease {ease}"); break;
                case "opacity": L($"run fade self to {to} in max(0.01, {dur})"); break;
                default: Unsupported($"gliding “{prop}”"); break;
            }
        }

        void SetVar(string var, string value, bool add)
        {
            var = var.Trim();
            if (var.Length == 0) return;
            if (IsName(var))
            {
                _globals.Add(var);
                L(add ? $"{var} += {value}" : $"{var} = {value}");
            }
            else L(add ? $"easy_add {Lit(var)}, {value}" : $"easy_set {Lit(var)}, {value}");
        }

        /// <summary>A variable read: a plain name is the global itself; others (item:wood, local.x) through easy_var.</summary>
        string VarRead(string var)
        {
            var = var.Trim();
            if (IsName(var)) { _globals.Add(var); return var; }
            return $"easy_var({Lit(var)})";
        }

        // ---------------- Values ----------------

        /// <summary>An action's number: a number, or an EasyEngine formula.</summary>
        static string Num(Rule r, string key, double def) => NumOf(r.T, r.N, key, def);

        static string WNum(Rule r, string key, double def) => NumOf(r.WT, r.WN, key, def);

        static string NumOf(Dictionary<string, string> t, Dictionary<string, double> n, string key, double def)
        {
            if (t.TryGetValue(key, out var s) && !Project.TryNumber(s, out _)) return s.Trim().Length == 0 ? Fmt(def) : Formula(s);
            return Fmt(n.TryGetValue(key, out var v) ? v : def);
        }

        /// <summary>A rule's value: its "expr" formula, else the first of the keys it has, else the default.</summary>
        static string Value(Rule r, double def, params string[] keys)
        {
            if (r.T.TryGetValue("expr", out var ex) && ex.Trim().Length > 0) return Project.TryNumber(ex, out var d) ? Fmt(d) : Formula(ex);
            foreach (var k in keys) if (r.Has(k)) return Num(r, k, def);
            return Fmt(def);
        }

        static string Formula(string ee) => $"easy_value({Lit(ee.Trim())})";

        static string Op(string op) => NormalOp(op);

        static string On(Rule r, string key, bool def) => (r.T.TryGetValue(key, out var s) ? IsOn(s) : r.N.TryGetValue(key, out var n) ? n >= 0.5 : def) ? "true" : "false";

        static string Bool(string s) => IsOn(s) ? "true" : "false";

        static string KeyOf(string ee) => Key(ee);

        string Summary(Rule r)
        {
            string trig = r.When switch
            {
                "key" or "keydown" or "keypress" => $"{r.When} {r.WStr("key", "Space")}",
                "timer" => $"every {r.WStr("every", "1")}s",
                "after" => $"after {r.WStr("seconds", r.WStr("every", "1"))}s",
                "touch" => $"touch {r.WStr("name")}",
                "ifvar" or "iflocal" or "becomes" => $"{r.When} {r.WStr("var")} {r.WStr("op", ">=")} {r.WStr("value", "0")}",
                "keynear" => $"keynear {r.WStr("key", "E")} near {r.WStr("name", "Player")}",
                "allgone" => $"none left: {r.WStr("name")}",
                "expr" => $"({r.WStr("expr")})",
                _ => r.When,
            };
            return Clean($"when {trig} → {(r.Do.Length == 0 ? "(another WHEN of the block)" : r.Do)}");
        }
    }

    // ---------------- Shared ----------------

    static string Lit(string s) => Printer.Quote(s.Replace("\r\n", "\n").Replace('\r', '\n'));

    /// <summary>Text for a comment: one line, no control characters.</summary>
    static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsControl(c) ? ' ' : c);
        string r = sb.ToString();
        return r.Length > 100 ? r[..100] + "…" : r;
    }

    /// <summary>
    /// A number written into the generated source: up to 6 decimals on purpose (not <see cref="World.ShortNumber"/>'s 2,
    /// which is for showing), so 0.125 s or a 0.0005 factor keep their value; never "-0".
    /// </summary>
    static string Fmt(double d)
    {
        string s = d.ToString("0.######", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
