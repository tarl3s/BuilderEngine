using System.Globalization;

namespace BuilderEngine.Script;

/// <summary>
/// EasyEngine's formula language (its Expr.cs), for "Only while" conditions, rule values and conditions that
/// EasyCompat keeps as they were written: numbers, names (<c>score</c>, <c>item:wood</c>, <c>key:E</c>, <c>Player.x</c>,
/// <c>self.health</c>, <c>local.hp</c>), functions (abs, min, max, floor, ceil, round, sqrt, sign, sin, cos, atan2,
/// clamp, rand, randi, dist), + - * / %, comparisons (also <c>=</c> and <c>&lt;&gt;</c>), and / or / not (&amp;&amp; || !).
/// Like EasyEngine it never throws: anything it can't read is 0. A formula is read once and kept, with every name
/// already sorted out (a key, the mouse, obj.prop, a variable…), so playing it reads values and does arithmetic only.
/// </summary>
public sealed partial class EasyCompat
{
    abstract record Node;
    sealed record NumNode(double V) : Node;
    /// <summary>key:E — a key held (the key already in Builder Script's name).</summary>
    sealed record KeyNode(string Key) : Node;
    /// <summary>mousex, time, scenew… — a value of the world.</summary>
    sealed record WorldNode(WorldValue What) : Node;
    /// <summary>count:list — how many items a list variable has.</summary>
    sealed record CountNode(string List) : Node;
    /// <summary>local.x — the object's own variable.</summary>
    sealed record LocalNode(string Name) : Node;
    /// <summary>self.x / Player.health — a property of an object (<see cref="Who"/> is ignored for self).</summary>
    sealed record PropNode(bool Self, string Who, Prop Prop, string Name) : Node;
    /// <summary>score, item:wood — a global variable.</summary>
    sealed record VarNode(string Name) : Node;
    sealed record CallNode(Fn Fn, Node[] Args) : Node;
    sealed record UnNode(char Op, Node A) : Node;
    sealed record BinNode(BinOp Op, Node A, Node B) : Node;

    enum WorldValue { MouseX, MouseY, MouseScreenX, MouseScreenY, Time, SceneWidth, SceneHeight }
    enum Prop { X, Y, Vx, Vy, Width, Height, Cx, Cy, Rot, Facing, Opacity, Scale, Speed, Health, MaxHealth, Grounded, Dead, Exists, Other }
    enum Fn { Abs, Min, Max, Floor, Ceil, Round, Sqrt, Sign, Sin, Cos, Atan2, Clamp, Rand, Randi, Dist, Unknown }
    enum BinOp { Or, And, Add, Sub, Mul, Div, Mod, Ge, Le, Gt, Lt, Eq, Ne }

    readonly Dictionary<string, Node> _formulas = new();

    /// <summary>The value of an EasyEngine formula for an object (<c>self</c>); 0 when it can't be read.</summary>
    internal double Eval(World w, string expr, GameObject? self)
    {
        if (string.IsNullOrWhiteSpace(expr)) return 0;
        if (!_formulas.TryGetValue(expr, out var node))
        {
            try { node = new FormulaReader(expr).Read(); }
            catch (Exception) { node = new NumNode(0); }
            _formulas[expr] = node;
        }
        try { return Value(w, node, self, 0); }
        catch (Exception e) when (e is not OutOfMemoryException) { return 0; }
    }

    double Value(World w, Node n, GameObject? self, int depth)
    {
        if (depth > 200) return 0;
        switch (n)
        {
            case NumNode num: return num.V;
            case VarNode v: return w.Globals.TryGetValue(v.Name, out var gv) ? Number(gv) : 0;
            case PropNode p: return Property(w, p, self);
            case KeyNode k: return w.KeysDown.Contains(k.Key) ? 1 : 0;
            case WorldNode wv:
                return wv.What switch
                {
                    WorldValue.MouseX => w.MouseX,
                    WorldValue.MouseY => w.MouseY,
                    WorldValue.MouseScreenX => w.MouseViewX,
                    WorldValue.MouseScreenY => w.MouseViewY,
                    WorldValue.Time => w.Time,
                    WorldValue.SceneWidth => w.SceneWidth,
                    _ => w.SceneHeight,
                };
            case CountNode c: return w.Globals.TryGetValue(c.List, out var lv) && lv is List<object?> list ? list.Count : 0;
            case LocalNode l: return self != null ? Number(Local(self, l.Name)) : 0;
            case UnNode u:
            {
                double a = Value(w, u.A, self, depth + 1);
                return u.Op == '-' ? -a : u.Op == '!' ? (a == 0 ? 1 : 0) : a;
            }
            case BinNode b:
            {
                if (b.Op == BinOp.Or) return Value(w, b.A, self, depth + 1) != 0 || Value(w, b.B, self, depth + 1) != 0 ? 1 : 0;
                if (b.Op == BinOp.And) return Value(w, b.A, self, depth + 1) != 0 && Value(w, b.B, self, depth + 1) != 0 ? 1 : 0;
                double x = Value(w, b.A, self, depth + 1), y = Value(w, b.B, self, depth + 1);
                return b.Op switch
                {
                    BinOp.Add => x + y, BinOp.Sub => x - y, BinOp.Mul => x * y,
                    BinOp.Div => y == 0 ? 0 : x / y, BinOp.Mod => y == 0 ? 0 : x % y,
                    BinOp.Ge => x >= y ? 1 : 0, BinOp.Le => x <= y ? 1 : 0, BinOp.Gt => x > y ? 1 : 0, BinOp.Lt => x < y ? 1 : 0,
                    BinOp.Eq => Math.Abs(x - y) < 1e-6 ? 1 : 0, BinOp.Ne => Math.Abs(x - y) >= 1e-6 ? 1 : 0,
                    _ => 0,
                };
            }
            case CallNode c:
            {
                int count = c.Args.Length;
                Span<double> a = count <= 8 ? stackalloc double[8] : new double[count];
                a = a[..count];
                for (int i = 0; i < count; i++) a[i] = Value(w, c.Args[i], self, depth + 1);
                return Call(w, c.Fn, a);
            }
        }
        return 0;
    }

    static double Call(World w, Fn fn, ReadOnlySpan<double> a)
    {
        double A(ReadOnlySpan<double> s, int i) => i < s.Length ? s[i] : 0;
        switch (fn)
        {
            case Fn.Abs: return Math.Abs(A(a, 0));
            case Fn.Min: return a.Length == 0 ? 0 : MinOf(a);
            case Fn.Max: return a.Length == 0 ? 0 : MaxOf(a);
            case Fn.Floor: return Math.Floor(A(a, 0));
            case Fn.Ceil: return Math.Ceiling(A(a, 0));
            case Fn.Round: return Math.Round(A(a, 0));
            case Fn.Sqrt: return A(a, 0) <= 0 ? 0 : Math.Sqrt(A(a, 0));
            case Fn.Sign: return Math.Sign(A(a, 0));
            case Fn.Sin: return Math.Sin(A(a, 0));
            case Fn.Cos: return Math.Cos(A(a, 0));
            case Fn.Atan2: return Math.Atan2(A(a, 0), A(a, 1));
            case Fn.Clamp: return A(a, 1) > A(a, 2) ? A(a, 1) : Math.Clamp(A(a, 0), A(a, 1), A(a, 2));
            case Fn.Rand: return a.Length >= 2 ? A(a, 0) + w.Rng.NextDouble() * (A(a, 1) - A(a, 0)) : w.Rng.NextDouble();
            case Fn.Randi: return a.Length >= 2 ? w.Rng.Next((int)A(a, 0), (int)Math.Max(A(a, 0), A(a, 1)) + 1) : w.Rng.Next();
            case Fn.Dist: return a.Length >= 4 ? Math.Sqrt((A(a, 2) - A(a, 0)) * (A(a, 2) - A(a, 0)) + (A(a, 3) - A(a, 1)) * (A(a, 3) - A(a, 1))) : 0;
            default: return 0;
        }
    }

    // min / max the way LINQ's do (what EasyCompat used before): a NaN wins min and loses max.
    static double MinOf(ReadOnlySpan<double> a)
    {
        double v = a[0];
        if (double.IsNaN(v)) return v;
        for (int i = 1; i < a.Length; i++)
        {
            double x = a[i];
            if (x < v) v = x;
            else if (double.IsNaN(x)) return x;
        }
        return v;
    }

    static double MaxOf(ReadOnlySpan<double> a)
    {
        int i = 0;
        double v = a[0];
        while (double.IsNaN(v))
        {
            if (++i >= a.Length) return v;
            v = a[i];
        }
        for (i++; i < a.Length; i++) if (a[i] > v) v = a[i];
        return v;
    }

    static Fn FnOf(string name) => name.ToLowerInvariant() switch
    {
        "abs" => Fn.Abs, "min" => Fn.Min, "max" => Fn.Max, "floor" => Fn.Floor, "ceil" => Fn.Ceil, "round" => Fn.Round,
        "sqrt" => Fn.Sqrt, "sign" => Fn.Sign, "sin" => Fn.Sin, "cos" => Fn.Cos, "atan2" => Fn.Atan2, "clamp" => Fn.Clamp,
        "rand" => Fn.Rand, "randi" => Fn.Randi, "dist" => Fn.Dist,
        _ => Fn.Unknown,
    };

    /// <summary>A name in a formula, sorted out once: a key held (key:E), the mouse, the time, obj.prop, local.x, or a variable.</summary>
    static Node NameNode(string name)
    {
        if (name.Length > 4 && name.StartsWith("key:", StringComparison.OrdinalIgnoreCase)) return new KeyNode(Key(name[4..]));
        switch (name.ToLowerInvariant())
        {
            case "mousex": return new WorldNode(WorldValue.MouseX);
            case "mousey": return new WorldNode(WorldValue.MouseY);
            case "mousescreenx": return new WorldNode(WorldValue.MouseScreenX);
            case "mousescreeny": return new WorldNode(WorldValue.MouseScreenY);
            case "time": return new WorldNode(WorldValue.Time);
            case "true": return new NumNode(1);
            case "false": return new NumNode(0);
            case "pi": return new NumNode(Math.PI);
            case "scenew": case "scenewidth": return new WorldNode(WorldValue.SceneWidth);
            case "sceneh": case "sceneheight": return new WorldNode(WorldValue.SceneHeight);
        }
        if (name.StartsWith("count:", StringComparison.OrdinalIgnoreCase)) return new CountNode(name[6..]);
        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            string who = name[..dot], prop = name[(dot + 1)..];
            if (who.Equals("local", StringComparison.OrdinalIgnoreCase)) return new LocalNode(prop);
            return new PropNode(who.Equals("self", StringComparison.OrdinalIgnoreCase), who, PropOf(prop), prop);
        }
        return new VarNode(name);
    }

    static Prop PropOf(string prop) => prop.ToLowerInvariant() switch
    {
        "x" => Prop.X, "y" => Prop.Y, "vx" => Prop.Vx, "vy" => Prop.Vy,
        "w" or "width" => Prop.Width, "h" or "height" => Prop.Height, "cx" => Prop.Cx, "cy" => Prop.Cy,
        "rot" => Prop.Rot, "facing" => Prop.Facing, "opacity" or "alpha" => Prop.Opacity, "scale" => Prop.Scale,
        "speed" => Prop.Speed, "health" or "hp" => Prop.Health, "maxhealth" or "maxhp" => Prop.MaxHealth,
        "grounded" => Prop.Grounded, "dead" => Prop.Dead, "exists" => Prop.Exists,
        _ => Prop.Other,
    };

    double Property(World w, PropNode p, GameObject? self)
    {
        var e = p.Self ? self : FindNamed(w, p.Who);
        if (e == null) return 0;
        return p.Prop switch
        {
            Prop.X => e.X,
            Prop.Y => e.Y,
            Prop.Vx => e.Vx,
            Prop.Vy => e.Vy,
            Prop.Width => e.Width,
            Prop.Height => e.Height,
            Prop.Cx => e.X + e.Width / 2,
            Prop.Cy => e.Y + e.Height / 2,
            Prop.Rot => e.Angle,
            Prop.Facing => e.FlipX ? -1 : 1,
            Prop.Opacity => e.Opacity,
            Prop.Scale => 1,
            Prop.Speed => SpeedScale(e),
            Prop.Health => Number(HealthVar(e, "hp")),
            Prop.MaxHealth => Number(HealthVar(e, "max_health")),
            Prop.Grounded => e.OnGround ? 1 : 0,
            Prop.Dead => e.Destroyed ? 1 : 0,
            Prop.Exists => 1,
            _ => e == self ? Number(Local(e, p.Name)) : 0,
        };
    }

    /// <summary>A variable of the object's EasyHealth mechanism (null when it has none).</summary>
    static object? HealthVar(GameObject g, string var)
    {
        foreach (var m in g.Mechanisms)
            if (m.Def.Name == "EasyHealth") return m.Vars.TryGetValue(var, out var v) ? v : null;
        return null;
    }

    /// <summary>A value as a number, the way the runtime reads one (a text that isn't a number is 0), a list as its length.</summary>
    internal static double Number(object? v) => v is List<object?> l ? l.Count : World.TryNumber(v, out var d) ? d : 0;

    /// <summary>Reads a formula the way EasyEngine's Expr.cs does (tolerant: trailing text is ignored).</summary>
    sealed class FormulaReader(string s)
    {
        int _i;

        public Node Read() => Or();

        Node Or()
        {
            var a = And();
            while (Match("||") || Word("or")) a = new BinNode(BinOp.Or, a, And());
            return a;
        }

        Node And()
        {
            var a = Compare();
            while (Match("&&") || Word("and")) a = new BinNode(BinOp.And, a, Compare());
            return a;
        }

        Node Compare()
        {
            var a = Add();
            if (Match(">=")) return new BinNode(BinOp.Ge, a, Add());
            if (Match("<=")) return new BinNode(BinOp.Le, a, Add());
            if (Match("==")) return new BinNode(BinOp.Eq, a, Add());
            if (Match("!=")) return new BinNode(BinOp.Ne, a, Add());
            if (Match("=>")) return new BinNode(BinOp.Ge, a, Add());
            if (Match("=<")) return new BinNode(BinOp.Le, a, Add());
            if (Match("<>")) return new BinNode(BinOp.Ne, a, Add());
            if (Match(">")) return new BinNode(BinOp.Gt, a, Add());
            if (Match("<")) return new BinNode(BinOp.Lt, a, Add());
            if (Match("=")) return new BinNode(BinOp.Eq, a, Add());
            return a;
        }

        Node Add()
        {
            var a = Mul();
            while (true)
            {
                if (Match("+")) a = new BinNode(BinOp.Add, a, Mul());
                else if (Match("-")) a = new BinNode(BinOp.Sub, a, Mul());
                else return a;
            }
        }

        Node Mul()
        {
            var a = Unary();
            while (true)
            {
                if (Match("*")) a = new BinNode(BinOp.Mul, a, Unary());
                else if (Match("/")) a = new BinNode(BinOp.Div, a, Unary());
                else if (Match("%")) a = new BinNode(BinOp.Mod, a, Unary());
                else return a;
            }
        }

        Node Unary()
        {
            if (Match("-")) return new UnNode('-', Unary());
            if (Match("+")) return Unary();
            if (Match("!") || Word("not")) return new UnNode('!', Unary());
            return Primary();
        }

        Node Primary()
        {
            if (Match("("))
            {
                var v = Or();
                Match(")");
                return v;
            }
            char c = Peek();
            if (char.IsDigit(c) || c == '.')
            {
                int start = _i;
                while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.')) _i++;
                double.TryParse(s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
                return new NumNode(v);
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = _i;
                while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] is '_' or '.' or ':')) _i++;
                string name = s[start.._i];
                if (Peek() == '(')
                {
                    _i++;
                    var args = new List<Node>();
                    if (Peek() != ')')
                    {
                        args.Add(Or());
                        while (Match(",")) args.Add(Or());
                    }
                    Match(")");
                    return new CallNode(FnOf(name), args.ToArray());
                }
                return NameNode(name);
            }
            return new NumNode(0);
        }

        void Ws() { while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++; }
        char Peek() { Ws(); return _i < s.Length ? s[_i] : '\0'; }

        bool Match(string op)
        {
            Ws();
            if (_i + op.Length > s.Length || string.CompareOrdinal(s, _i, op, 0, op.Length) != 0) return false;
            _i += op.Length;
            return true;
        }

        bool Word(string word)
        {
            Ws();
            int end = _i + word.Length;
            if (end > s.Length || string.Compare(s, _i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            if (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '_' or '.' or ':')) return false;
            _i = end;
            return true;
        }
    }
}
