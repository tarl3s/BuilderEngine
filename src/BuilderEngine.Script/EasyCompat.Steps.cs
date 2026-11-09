using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

/// <summary>
/// EasyEngine's Cutscene behavior translated into a Builder Script mechanism: it plays when the scene starts, when
/// "who" walks into the object, when a variable turns on or when a key is pressed; its steps (EasyEngine's StepCatalog)
/// become the Builder Script commands that do the same — say, move in, camera moves, fades, cinema bars, waits, the
/// player's keys, animations, sounds, variables, scenes — one after another, each waiting like EasyEngine's does.
/// At the end it gives back what it took (the keys, the bars, the camera, a fade it left on).
/// </summary>
public sealed partial class EasyCompat
{
    string? CutsceneMechanism(World w, SceneObject so, JsonObject b)
    {
        string script = T(b, "script", "{\"steps\":[]}");
        string trigger = T(b, "trigger", "start").Trim().ToLowerInvariant();
        string name = "EasyCutscene_" + Hash(so.Name + "\n" + trigger + T(b, "var", "") + T(b, "key", "") + T(b, "who", "") + T(b, "camera", "")
            + N(b, "once", 1) + N(b, "lock", 1) + N(b, "bars", 1) + "\n" + script);
        if (w.Library.ContainsKey(name)) return name;
        string src = TranslateCutscene(name, so.Name, trigger, T(b, "var", ""), T(b, "key", "E"), T(b, "who", "Player"),
            On(b, "once", 1), On(b, "lock", 1), On(b, "bars", 1), T(b, "camera", "follow"), script, out var unsupported);
        _generated[name] = src;
        if (T(b, "skipKey", "Escape").Trim().Length > 0) unsupported.Add("skipping a cutscene with a key");
        if (T(b, "font", "").Trim().Length > 0) unsupported.Add("cutscene fonts (the default font is used)");
        foreach (var u in unsupported) Note(w, u);
        try { w.Load(src); }
        catch (ScriptError e)
        {
            Note(w, $"the cutscene of '{so.Name}' couldn't be translated ({e.Message})");
            return null;
        }
        return name;
    }

    /// <summary>One step: what it does and its fields as texts (EasyEngine's SeqStep).</summary>
    sealed record Step(string Do, Dictionary<string, string> P)
    {
        public string Str(string k, string d = "") => P.TryGetValue(k, out var v) ? v : d;
        public double Num(string k, double d = 0) => P.TryGetValue(k, out var v) && TryNum(v, out var n) ? n : d;
        public bool On(string k, bool d) => P.TryGetValue(k, out var v) ? (TryNum(v, out var n) ? n >= 0.5 : IsOn(v) || (v.Trim().ToLowerInvariant() is not ("off" or "false" or "no") && d)) : d;
    }

    /// <summary>EasyEngine's cutscene steps: {"steps":[{"do":"say","text":…},…]} (a bare list works too).</summary>
    static List<Step> ParseSteps(string json)
    {
        var steps = new List<Step>();
        JsonNode? root;
        try { root = JsonNode.Parse(json, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return steps; }
        var arr = root as JsonArray ?? (root is JsonObject o ? EasyLink.Get(o, "steps") as JsonArray : null);
        if (arr == null) return steps;
        foreach (var s in arr.OfType<JsonObject>())
        {
            var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string what = "";
            foreach (var (k, v) in s)
            {
                string text = v is JsonValue jv ? (jv.TryGetValue<string>(out var str) ? str : jv.TryGetValue<bool>(out var bo) ? (bo ? "1" : "0") : jv.ToJsonString()) : v?.ToJsonString() ?? "";
                if (k.Equals("do", StringComparison.OrdinalIgnoreCase)) what = text.Trim().ToLowerInvariant();
                else p[k] = text;
            }
            if (what.Length > 0) steps.Add(new Step(what, p));
        }
        return steps;
    }

    /// <summary>The Builder Script mechanism <paramref name="name"/> that plays an EasyEngine cutscene.</summary>
    public static string TranslateCutscene(string name, string objectName, string trigger, string var, string key, string who,
        bool once, bool lockPlayer, bool bars, string camera, string script, out List<string> unsupported)
    {
        var steps = ParseSteps(script);
        var notes = new List<string>();
        unsupported = notes;
        var sb = new StringBuilder();
        int ind = 1;
        void L(string line) => sb.Append(' ', ind * 4).Append(line).Append('\n');
        void Note(string what) { if (!notes.Contains(what)) notes.Add(what); }
        bool hold = camera.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase);

        sb.Append($"mechanism {name}\n");
        sb.Append($"    # The cutscene of '{Clean(objectName)}' in EasyEngine ({steps.Count} steps), translated into Builder Script when the game\n");
        sb.Append("    # starts (EasyCompat). The project keeps the steps as EasyEngine wrote them: change them in EasyEngine.\n\n");
        switch (trigger)
        {
            case "enter":
                L("when tick");
                ind++;
                L($"who_in = easy_find({Lit(who)})");
                L("inside = who_in and who_in != self and overlaps(self, who_in)");
                L("go = inside and not was_inside");
                L("was_inside = inside");
                L("if go: play_cutscene");
                ind--;
                break;
            case "var":
                L("when tick");
                ind++;
                L($"on = easy_var({Lit(var)}) >= 0.5");
                L("go = on and not was_on");
                L("was_on = on");
                L("if go: play_cutscene");
                ind--;
                break;
            case "key":
                L("when tick");
                ind++;
                L($"on = easy_key({Lit(Key(key))})");
                L("go = on and not was_on");
                L("was_on = on");
                L("if go: play_cutscene");
                ind--;
                break;
            default:
                L("when start");
                ind++;
                L("play_cutscene");
                ind--;
                break;
        }
        sb.Append('\n');
        L("to play_cutscene");
        ind++;
        string memory = "cut:" + objectName;
        if (once)
        {
            L($"if easy_var({Lit(memory)}) >= 1: stop");
            L($"easy_set {Lit(memory)}, 1");
        }
        L($"print {Lit($"🎬 Cutscene '{objectName}' started — {steps.Count} steps")}");
        if (lockPlayer) L("lock controls");
        if (bars) L("bars on");
        if (hold) L("camera hold");
        bool panned = false, faded = false, wentAway = false;
        int n = 0;
        foreach (var s in steps)
        {
            n++;
            L($"# step {n}: {s.Do}");
            int mark = sb.Length;
            WriteStep(s);
            if (sb.Length == mark) L("skip = 0");
        }
        if (bars) L("bars off");
        if (lockPlayer) L("unlock controls");
        if (faded && !wentAway) L("fade in in 0.5");
        if (panned || hold) L("easy_camera_back 0.6");
        L($"print {Lit($"🎬 Cutscene '{objectName}' finished")}");
        ind--;
        return sb.ToString();

        // "self" = the cutscene's object, "player" = the player, else an object by name: put into 'var', then the lines
        // under 'if var' (nothing happens when there's no such object, like EasyEngine).
        bool Find(string varName, string objName)
        {
            objName = objName.Trim();
            if (objName.Length == 0) return false;
            L(objName.Equals("self", StringComparison.OrdinalIgnoreCase) ? $"{varName} = self" : $"{varName} = easy_find({Lit(objName)})");
            L($"if not {varName}: print {Lit($"⚠ Cutscene '{objectName}': no object '{objName}' for a step")}");
            return true;
        }

        string Secs(Step s, double def) => Fmt(Math.Max(0, s.Num("time", def)));

        // a value that may be an EasyEngine formula ("to" and "amount" of setvar/addvar)
        string Val(Step s, string k, double def)
        {
            string v = s.Str(k, "");
            if (v.Trim().Length == 0) return Fmt(def);
            return TryNum(v, out var d) ? Fmt(d) : $"easy_value({Lit(v.Trim())})";
        }

        void WriteStep(Step s)
        {
            switch (s.Do)
            {
                case "wait": L($"wait {Secs(s, 1)}"); break;
                case "say":
                    if (s.Str("look").Trim() is { Length: > 0 } look && look != "theme") Note($"dialogue box look '{look}' (the default box is used)");
                    L($"easy_say {Lit(Text(s.Str("text")))}, {Lit(s.Str("speaker"))}, false");
                    break;
                case "title":
                    L($"notify {Lit(Text(s.Str("text")))}");
                    if (s.On("wait", true)) L($"wait {Fmt(Math.Max(0.3, s.Num("time", 2)))}");
                    break;
                case "fadeout":
                    faded = true;
                    L($"fade out in {Secs(s, 1)} with {Lit(s.Str("color", "#000000"))}");
                    break;
                case "fadein": L($"fade in in {Secs(s, 1)}"); break;
                case "bars": L(s.On("on", true) ? "bars on" : "bars off"); break;
                case "camera":
                {
                    panned = true;
                    string obj = s.Str("object").Trim();
                    if (obj.Length > 0)
                    {
                        Find("look_at", obj);
                        L("if look_at");
                        ind++;
                        L($"camera move to look_at in {Secs(s, 1)}");
                        L("camera follow look_at");
                        ind--;
                    }
                    else L($"camera move to {Fmt(s.Num("x"))}, {Fmt(s.Num("y"))} in {Secs(s, 1)}");
                    break;
                }
                case "cameraback":
                    L($"easy_camera_back {Secs(s, 1)}");
                    panned = false;
                    break;
                case "shake": L($"camera shake {Fmt(s.Num("strength", 8))} for {Fmt(Math.Max(0.01, s.Num("time", 0.5)))}"); break;
                case "move":
                {
                    if (!Find("mover", s.Str("object", "player"))) break;
                    L("if mover");
                    ind++;
                    string to = s.Str("to").Trim();
                    string run = s.On("wait", true) ? "" : "run ";
                    if (to.Length > 0)
                    {
                        Find("goal", to);
                        L("if goal");
                        ind++;
                        L($"{run}move mover to goal.x + goal.width / 2 - mover.width / 2, goal.y + goal.height / 2 - mover.height / 2 in {Secs(s, 1)}");
                        ind--;
                    }
                    else L($"{run}move mover to {Fmt(s.Num("x"))}, {Fmt(s.Num("y"))} in {Secs(s, 1)}");
                    ind--;
                    break;
                }
                case "face":
                {
                    if (!Find("turner", s.Str("object", "self"))) break;
                    string toward = s.Str("toward", "player").Trim();
                    L("if turner");
                    ind++;
                    if (toward.Equals("left", StringComparison.OrdinalIgnoreCase) || toward.Equals("right", StringComparison.OrdinalIgnoreCase))
                        L($"face turner {toward.ToLowerInvariant()}");
                    else
                    {
                        L($"toward_obj = easy_find({Lit(toward)})");
                        L("if toward_obj: face turner toward_obj");
                    }
                    ind--;
                    break;
                }
                case "show":
                case "hide":
                    if (!Find("thing", s.Str("object", "self"))) break;
                    L("if thing");
                    ind++;
                    L($"{s.Do} thing");
                    if (s.Do == "show") L("if thing.opacity < 0.05: thing.opacity = 1");
                    ind--;
                    break;
                case "control":
                case "presskey":
                {
                    string k = s.Do == "presskey" ? Key(s.Str("key", "E")) : s.Str("action", "right").Trim().ToLowerInvariant() switch
                    {
                        "left" => "left", "up" => "up", "down" => "down", "jump" => "space", "dash" => "leftshift", _ => "right",
                    };
                    string secs = Fmt(Math.Max(0, s.Num("time", s.Do == "control" ? 1 : 0.1)));
                    L($"{(s.On("wait", true) ? "" : "run ")}easy_hold {Lit(k)}, {secs}");
                    break;
                }
                case "playanim":
                    if (!Find("actor", s.Str("object", "player"))) break;
                    L($"if actor: easy_anim actor, {Lit(s.Str("anim"))}, {Lit(s.Str("mode", "once"))}");
                    break;
                case "lock": L("lock controls"); break;
                case "unlock": L("unlock controls"); break;
                case "setvar": L($"easy_set {Lit(s.Str("var"))}, {Val(s, "to", 1)}"); break;
                case "addvar": L($"easy_add {Lit(s.Str("var"))}, {Val(s, "amount", 1)}"); break;
                case "settext":
                    L($"easy_set {Lit(s.Str("var"))}, easy_format({Lit(s.Str("to") is { Length: > 0 } words ? words : s.Str("text", s.Str("value")))})");
                    break;
                case "setcolor": L($"easy_set {Lit(s.Str("var"))}, easy_format({Lit(s.Str("to", s.Str("color")))})"); break;
                case "listadd":
                    L($"easy_list \"add\", {Lit(s.Str("var"))}, easy_format({Lit(s.Str("value") is { Length: > 0 } item ? item : s.Str("to", s.Str("text")))}), false");
                    break;
                case "playsound": L($"play {Lit(s.Str("sound"))} with {Fmt(Math.Clamp(s.Num("volume", 0.8), 0, 1))}"); break;
                case "goscene":
                    wentAway = true;
                    L($"easy_goto {Lit(s.Str("scene"))}");
                    break;
                case "destroy":
                    if (!Find("victim", s.Str("object", "self"))) break;
                    L("if victim: destroy victim");
                    break;
                case "spawn":
                {
                    string what = s.Str("object").Trim();
                    if (what.Length == 0) break;
                    string at = s.Str("at", "self").Trim();
                    if (at.Length == 0) L($"easy_spawn {Lit(what)}, \"\", {Fmt(s.Num("x"))}, {Fmt(s.Num("y"))}");
                    else if (at.Equals("self", StringComparison.OrdinalIgnoreCase)) L($"easy_spawn {Lit(what)}, \"\"");
                    else
                    {
                        Find("spot", at);
                        L($"if spot: easy_spawn {Lit(what)}, \"\", spot.x + spot.width / 2, spot.y + spot.height / 2");
                    }
                    break;
                }
                case "emit": Note("particles"); break;
                case "settime": Note("setting the time of day from a cutscene"); break;
                default: Note($"the cutscene step “{s.Do}”"); break;
            }
        }
    }
}
