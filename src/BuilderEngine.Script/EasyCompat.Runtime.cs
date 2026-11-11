using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace BuilderEngine.Script;

/// <summary>EasyCompat while playing: each object's EasyEngine state, variables, finding objects, touching, spawning.</summary>
public sealed partial class EasyCompat
{
    /// <summary>What EasyEngine keeps per object that no mechanism owns: its own variables, a key press it used, a knockback…</summary>
    internal sealed class ObjectState
    {
        public readonly Dictionary<string, object?> Locals = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Latched = new(StringComparer.OrdinalIgnoreCase);
        public double KnockUntil = -1;
        public int Interactions;
        public double Speed = 1;
        public SpriteAnimation? Forced;
        public bool ForcedOnce, Paused;
        public int PausedFrame;
    }

    readonly ConditionalWeakTable<GameObject, ObjectState> _states = new();

    internal ObjectState State(GameObject o) => _states.GetValue(o, _ => new ObjectState());

    void ResetSceneState() => _formulas.Clear();

    /// <summary>EasyEngine leaves out objects turned off ("Active": false) and every object on a layer turned off.</summary>
    internal bool Skips(SceneObject so)
    {
        if (so.Easy is not JsonObject e) return false;
        if (EasyLink.Get(e, "Active") is JsonValue a && a.TryGetValue<bool>(out var on) && !on) return true;
        return _offLayers.Contains(Num(e, EasyLink.LayerKey, -1));
    }

    readonly HashSet<double> _offLayers = [];

    /// <summary>The layers turned off in this scene (their objects aren't in the game).</summary>
    void ReadLayers(Scene scene)
    {
        _offLayers.Clear();
        if (scene.Easy is not JsonObject se || EasyLink.Get(se, "Layers") is not JsonArray layers) return;
        for (int i = 0; i < layers.Count; i++)
            if (layers[i] is JsonObject l && EasyLink.Get(l, "Active") is JsonValue a && a.TryGetValue<bool>(out var on) && !on) _offLayers.Add(i);
    }

    /// <summary>A new object from an EasyEngine object: its opacity, its own variables, how far it is turned.</summary>
    internal void ObjectMade(World w, GameObject o, SceneObject so)
    {
        if (so.Easy is not JsonObject e) return;
        double opacity = Num(e, "Opacity", 1);
        if (opacity < 0.999) o.Opacity = Math.Clamp(opacity, 0, 1);
        double rot = Num(e, "Rotation", 0);
        if (rot != 0 && double.IsFinite(rot)) o.Angle = rot % 360;
        if (EasyLink.Get(e, "Variables") is JsonObject vars && vars.Count > 0)
        {
            var st = State(o);
            foreach (var (k, v) in vars) st.Locals[k] = Project.Value(v) is double d ? d : 0.0;
        }
    }

    // ---------------- Variables ----------------

    /// <summary>An EasyEngine variable: a global (score, item:wood, a text, a list), "local.x" = the object's own.</summary>
    internal object? Var(World w, string name, GameObject? self)
    {
        name = name.Trim();
        if (name.StartsWith("local.", StringComparison.OrdinalIgnoreCase)) return self != null ? Local(self, name[6..]) ?? 0.0 : 0.0;
        return w.Globals.TryGetValue(name, out var v) && v != null ? v : 0.0;
    }

    internal object? Local(GameObject o, string name) => State(o).Locals.TryGetValue(name, out var v) ? v : 0.0;

    /// <summary>What "{name}" shows in EasyEngine: a text, a list's items, a number without needless decimals.</summary>
    internal string TextOf(World w, string key, GameObject? self)
    {
        key = key.Trim();
        if (key.StartsWith("count:", StringComparison.OrdinalIgnoreCase))
            return (w.Globals.TryGetValue(key[6..], out var lv) && lv is List<object?> l ? l.Count : 0).ToString(CultureInfo.InvariantCulture);
        var v = Var(w, key, self);
        return v switch
        {
            string s => s,
            List<object?> list => string.Join(", ", list.Select(World.ToText)),
            _ => World.ShortNumber(Number(v)),
        };
    }

    /// <summary>Sets (or adds to) an EasyEngine variable. A yes/no value is 1 or 0, like EasyEngine's.</summary>
    internal void SetVar(World w, GameObject self, string name, object? value, bool add)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        if (name.StartsWith("local.", StringComparison.OrdinalIgnoreCase)) { SetLocal(self, name[6..], value, add); return; }
        if (value is bool b) value = b ? 1.0 : 0.0;
        w.Globals[name] = add ? Number(Var(w, name, self)) + Number(value) : value is string or List<object?> ? value : Number(value);
    }

    internal void SetLocal(GameObject self, string name, object? value, bool add)
    {
        var st = State(self);
        if (value is bool b) value = b ? 1.0 : 0.0;
        st.Locals[name] = add ? Number(Local(self, name)) + Number(value) : value is string ? value : Number(value);
    }

    /// <summary>
    /// A text with EasyEngine's {name} parts filled in (Substitute): texts, lists, numbers, {local.x}, {count:list}.
    /// A part is a '{', at least one character, and the first '}' after it (the text itself when there's none).
    /// </summary>
    internal string Format(World w, string text, GameObject? self)
    {
        int open = text.IndexOf('{');
        if (open < 0) return text;
        System.Text.StringBuilder? sb = null;
        int copied = 0;
        while (open >= 0)
        {
            int close = text.IndexOf('}', open + 1);
            if (close < 0) break; // no '}' after it: nothing more to fill in
            if (close == open + 1) { open = text.IndexOf('{', close); continue; } // "{}" isn't a part
            sb ??= new System.Text.StringBuilder(text.Length + 16);
            sb.Append(text, copied, open - copied).Append(TextOf(w, text.Substring(open + 1, close - open - 1), self));
            copied = close + 1;
            open = text.IndexOf('{', copied);
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    object? Holder(World w, GameObject self, string name, bool local) => local ? Local(self, name) : w.Globals.GetValueOrDefault(name);

    /// <summary>A word like "isnot" as a rule wrote it, trimmed and in small letters (no copy when it already is).</summary>
    static string LowerWord(string s)
    {
        string t = s.Trim();
        foreach (char c in t)
            if (c > 127 || char.IsAsciiLetterUpper(c)) return t.ToLowerInvariant();
        return t;
    }

    /// <summary>"While a text variable…": is / isnot / contains / empty / notempty (capital letters don't matter).</summary>
    internal bool TextIs(World w, GameObject self, string var, string op, string value, bool local)
    {
        string cur = Holder(w, self, var, local) is string s ? s : "";
        string want = Format(w, value, self);
        return LowerWord(op) switch
        {
            "isnot" => !cur.AsSpan().Trim().Equals(want.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase),
            "contains" => cur.Contains(want, StringComparison.OrdinalIgnoreCase),
            "empty" => cur.Length == 0,
            "notempty" => cur.Length > 0,
            _ => cur.AsSpan().Trim().Equals(want.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>"While a list…": contains / notcontains / empty / notempty / countis / countatleast.</summary>
    internal bool ListIs(World w, GameObject self, string var, string op, string value, bool local)
    {
        var list = Holder(w, self, var, local) as List<object?> ?? [];
        string want = Format(w, value, self).Trim();
        bool has = false;
        foreach (var x in list)
            if (World.ToText(x).AsSpan().Trim().Equals(want, StringComparison.OrdinalIgnoreCase)) { has = true; break; }
        double n = Project.TryNumber(want, out var d) ? d : 0;
        return LowerWord(op) switch
        {
            "notcontains" => !has,
            "empty" => list.Count == 0,
            "notempty" => list.Count > 0,
            "countis" => list.Count == (int)Math.Round(n),
            "countatleast" => list.Count >= n,
            _ => has,
        };
    }

    /// <summary>Adds to / removes from / empties a list variable (the object's own when 'local').</summary>
    internal void ListAction(World w, GameObject self, string action, string var, string value, bool local)
    {
        if (var.Trim().Length == 0) return;
        var bag = local ? State(self).Locals : w.Globals;
        if (bag.GetValueOrDefault(var) is not List<object?> list) bag[var] = list = [];
        switch (action.Trim().ToLowerInvariant())
        {
            case "add": list.Add(value); break;
            case "remove":
                int i = list.FindIndex(x => string.Equals(World.ToText(x).Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));
                if (i >= 0) list.RemoveAt(i);
                break;
            case "clear": list.Clear(); break;
        }
    }

    internal static bool Compare(double a, string op, double b) => NormalOp(op) switch
    {
        ">" => a > b, "<" => a < b, "<=" => a <= b,
        "==" => Math.Abs(a - b) < 0.0001, "!=" => Math.Abs(a - b) >= 0.0001,
        _ => a >= b,
    };

    // ---------------- Objects ----------------

    /// <summary>EasyEngine finds objects by name ("player" also finds the player).</summary>
    internal static GameObject? FindNamed(World w, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        foreach (var o in w.EasyObjects()) if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) return o;
        return name.Equals("player", StringComparison.OrdinalIgnoreCase) ? w.FindObject("player") : null;
    }

    /// <summary>The object has a mechanism of this name (not counting the ones used inside others).</summary>
    internal static bool HasMechanism(GameObject o, string name)
    {
        foreach (var m in o.Mechanisms) if (m.Def.Name == name) return true;
        return false;
    }

    /// <summary>"Coin" names Coin and its numbered copies (Coin_2, Coin 3, Coin4…).</summary>
    internal static bool InFamily(string name, string want)
    {
        if (want.Length == 0 || name.Length < want.Length || string.Compare(name, 0, want, 0, want.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        int i = want.Length;
        if (i == name.Length) return true;
        if (name[i] is '_' or ' ' or '-') i++;
        if (i >= name.Length) return false;
        for (; i < name.Length; i++) if (!char.IsDigit(name[i])) return false;
        return true;
    }

    /// <summary>EasyEngine's "touching": overlapping, or resting against it (within 1 px).</summary>
    internal static bool Touching(GameObject a, GameObject b) =>
        a.X <= b.X + b.Width + 1 && a.X + a.Width >= b.X - 1 && a.Y <= b.Y + b.Height + 1 && a.Y + a.Height >= b.Y - 1;

    /// <summary>A solid touches what rests against it; anything else only what overlaps it.</summary>
    internal static bool Contact(GameObject self, GameObject other) => self.Solid ? Touching(self, other) : self.Overlaps(other);

    /// <summary>The first other object of that name (or its numbered copies) it touches, or null.</summary>
    internal static GameObject? TouchingNamed(World w, GameObject self, string name)
    {
        foreach (var o in w.EasyObjects())
            if (o != self && InFamily(o.Name, name) && Touching(self, o)) return o;
        return null;
    }

    /// <summary>Another object of that name (or a numbered copy) within radius of its centre, or touching it.</summary>
    internal static bool Near(World w, GameObject self, string name, double radius)
    {
        double cx = self.X + self.Width / 2, cy = self.Y + self.Height / 2;
        foreach (var o in w.EasyObjects())
        {
            if (o == self || !InFamily(o.Name, name)) continue;
            double dx = o.X + o.Width / 2 - cx, dy = o.Y + o.Height / 2 - cy;
            if (dx * dx + dy * dy <= radius * radius || Touching(self, o)) return true;
        }
        return false;
    }

    /// <summary>A solid (not one-way) right beside it, on the side dir (+1 right, -1 left): a wall to jump off.</summary>
    internal static bool SolidBeside(World w, GameObject self, double dir)
    {
        double x = self.X + Math.Sign(dir) * 3, y = self.Y + 3, wdt = self.Width, h = self.Height - 6;
        foreach (var s in w.EasyObjects())
            if (s != self && s.Solid && !s.OneWay && x < s.X + s.Width && x + wdt > s.X && y < s.Y + s.Height && y + h > s.Y) return true;
        if (!w.HasTiles) return false;
        double edge = dir > 0 ? x + wdt : x; // the side it would touch: a few points down it
        return w.SolidTileAt(edge, y) || w.SolidTileAt(edge, y + h / 2) || w.SolidTileAt(edge, y + h);
    }

    /// <summary>A bounce pad launches what is on it (feet above its middle) — up; down/left/right launch what is near.</summary>
    internal static bool OnPad(GameObject pad, GameObject o, string dir)
    {
        bool near = o.X < pad.X + pad.Width + 4 && o.X + o.Width + 4 > pad.X && o.Y < pad.Y + pad.Height + 6 && o.Y + o.Height + 6 > pad.Y;
        if (!near) return false;
        if (dir is "down" or "left" or "right") return true;
        return !(o.Y + o.Height > pad.Y + pad.Height / 2 || o.X + o.Width <= pad.X || o.X >= pad.X + pad.Width);
    }

    // ---------------- Spawning, shooting ----------------

    /// <summary>EasyEngine's spawn: at this object (centred), falling from the top of the view, near it on the ground, or at x, y.</summary>
    internal void Spawn(World w, GameObject self, string what, string at, double? x, double? y)
    {
        at = at.Trim().ToLowerInvariant();
        double cx = self.X + self.Width / 2, cy = self.Y + self.Height / 2;
        var made = w.EasySpawn(what, 0, 0);
        if (made == null) { Note(w, $"something called “{what}” to create (an object in this scene or a prefab)"); return; }
        if (x is double px && y is double py) { made.X = px; made.Y = py; }
        else if (at == "top")
        {
            double left = w.Camera.X - w.Camera.ViewWidth / 2;
            made.X = left + w.Rng.NextDouble() * Math.Max(1, w.Camera.ViewWidth - made.Width);
            made.Y = w.Camera.Y - w.Camera.ViewHeight / 2 - made.Height;
        }
        else if (at == "near")
        {
            made.X = cx + (w.Rng.Next(2) == 0 ? -1 : 1) * (60 + w.Rng.NextDouble() * 120) - made.Width / 2;
            made.Y = self.Y + self.Height - made.Height;
        }
        else { made.X = cx - made.Width / 2; made.Y = cy - made.Height / 2; }
    }

    /// <summary>One shot: one bullet ahead (or at the aim object), or a ring of them turned so one points at it.</summary>
    internal void Shoot(World w, GameObject self, int dirs, double speed, double life, string bullet, string aim)
    {
        dirs = Math.Max(1, dirs);
        string proj = w.PackageReplacing("Projectile")?.Name ?? "EasyProjectile"; // the Projectile behavior package
        var target = aim.Trim().Length == 0 ? null : FindNamed(w, aim);
        if (target == self) target = null;
        double cx = self.X + self.Width / 2, cy = self.Y + self.Height / 2;
        double baseAng = target != null
            ? Math.Atan2(target.Y + target.Height / 2 - cy, target.X + target.Width / 2 - cx) * 180 / Math.PI
            : dirs == 1 ? (self.FlipX ? 180 : 0) : 0;
        for (int i = 0; i < dirs; i++)
        {
            double ang = (baseAng + i * 360.0 / dirs) * Math.PI / 180;
            var b = w.EasySpawn(bullet, 0, 0);
            if (b == null)
            {
                // no bullet object: a small round one that flies and hurts what it was aimed at
                b = w.Spawn("Bullet", 0, 0, [proj, "EasyDamager"], new()
                {
                    [proj] = new() { ["life"] = life },
                    ["EasyDamager"] = new() { ["target"] = target?.Name ?? "Player", ["vanish"] = true },
                });
                b.Width = b.Height = 10;
                b.Color = "#FFE070";
            }
            b.X = cx - b.Width / 2;
            b.Y = cy - b.Height / 2;
            b.Vx = Math.Cos(ang) * speed;
            b.Vy = Math.Sin(ang) * speed;
            foreach (var m in b.AllMechanisms())
                if (m.Def.Name == proj) m.Vars["life"] = life;
        }
    }

    // ---------------- Rule actions on behaviors ----------------

    /// <summary>"boost" multiplies its movers' speed; "setspeed" sets it (1 = normal).</summary>
    internal void Speed(GameObject o, double factor, bool multiply)
    {
        var st = State(o);
        st.Speed = multiply ? st.Speed * factor : factor;
        foreach (var m in o.AllMechanisms())
            if (m.Vars.ContainsKey("scale") && m.Def.Name.StartsWith("Easy")) m.Vars["scale"] = st.Speed;
    }

    internal double SpeedScale(GameObject o) => State(o).Speed;

    /// <summary>Gives or takes a Platformer ability (double jump, wall jump, dash).</summary>
    internal void Ability(GameObject o, string ability, bool on)
    {
        string key = ability.Trim().ToLowerInvariant() switch
        {
            "walljump" or "wall" => "wall_jump",
            "dash" => "dash",
            _ => "double_jump",
        };
        foreach (var m in o.AllMechanisms())
            if (m.Def.Name.StartsWith("EasyPlatformer")) m.Vars[key] = on;
    }

    /// <summary>"Turn one of its behaviors on or off": its mechanism rests while off (its 'cond' is never true).</summary>
    internal void SwitchBehavior(World w, GameObject o, string kind, bool on)
    {
        string mech = w.PackageReplacing(kind)?.Name ?? "Easy" + kind.Trim(); // a behavior package, or its EasyKit mechanism
        bool found = false;
        foreach (var m in o.AllMechanisms())
        {
            if (!m.Def.Name.StartsWith(mech, StringComparison.OrdinalIgnoreCase) || !m.Def.Params.Any(p => p.Name == "cond")) continue;
            found = true;
            if (!on)
            {
                if (!m.Vars.ContainsKey("cond_was_on")) m.Vars["cond_was_on"] = m.Vars.GetValueOrDefault("cond");
                m.Vars["cond"] = "0";
            }
            else if (m.Vars.Remove("cond_was_on", out var old)) m.Vars["cond"] = old ?? "";
        }
        if (!found) Note(w, $"turning '{Words(kind)}' on or off");
    }
}
