using System.Globalization;

namespace BuilderEngine.Script;

/// <summary>
/// EasyEngine's formula language (its Expr.cs), for "Only while" conditions, rule values and conditions that
/// EasyCompat keeps as they were written: numbers, names (<c>score</c>, <c>item:wood</c>, <c>key:E</c>, <c>Player.x</c>,
/// <c>self.health</c>, <c>local.hp</c>), functions (abs, min, max, floor, ceil, round, sqrt, sign, sin, cos, atan2,
/// clamp, rand, randi, dist), + - * / %, comparisons (also <c>=</c> and <c>&lt;&gt;</c>), and / or / not (&amp;&amp; || !).
/// Like EasyEngine it never throws: anything it can't read is 0. A formula is read once and kept.
/// </summary>
public sealed partial class EasyCompat
{
    abstract record Node;
    sealed record NumNode(double V) : Node;
    sealed record NameNode(string Name) : Node;
    sealed record CallNode(string Name, Node[] Args) : Node;
    sealed record UnNode(char Op, Node A) : Node;
    sealed record BinNode(string Op, Node A, Node B) : Node;

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
            case NameNode name: return Resolve(w, name.Name, self);
            case UnNode u:
            {
                double a = Value(w, u.A, self, depth + 1);
                return u.Op == '-' ? -a : u.Op == '!' ? (a == 0 ? 1 : 0) : a;
            }
            case BinNode b:
            {
                if (b.Op == "or") return Value(w, b.A, self, depth + 1) != 0 || Value(w, b.B, self, depth + 1) != 0 ? 1 : 0;
                if (b.Op == "and") return Value(w, b.A, self, depth + 1) != 0 && Value(w, b.B, self, depth + 1) != 0 ? 1 : 0;
                double x = Value(w, b.A, self, depth + 1), y = Value(w, b.B, self, depth + 1);
                return b.Op switch
                {
                    "+" => x + y, "-" => x - y, "*" => x * y,
                    "/" => y == 0 ? 0 : x / y, "%" => y == 0 ? 0 : x % y,
                    ">=" => x >= y ? 1 : 0, "<=" => x <= y ? 1 : 0, ">" => x > y ? 1 : 0, "<" => x < y ? 1 : 0,
                    "==" => Math.Abs(x - y) < 1e-6 ? 1 : 0, "!=" => Math.Abs(x - y) >= 1e-6 ? 1 : 0,
                    _ => 0,
                };
            }
            case CallNode c:
            {
                var a = new double[c.Args.Length];
                for (int i = 0; i < a.Length; i++) a[i] = Value(w, c.Args[i], self, depth + 1);
                return Call(w, c.Name, a);
            }
        }
        return 0;
    }

    static double Call(World w, string name, double[] a)
    {
        double A(int i) => i < a.Length ? a[i] : 0;
        switch (name.ToLowerInvariant())
        {
            case "abs": return Math.Abs(A(0));
            case "min": return a.Length == 0 ? 0 : a.Min();
            case "max": return a.Length == 0 ? 0 : a.Max();
            case "floor": return Math.Floor(A(0));
            case "ceil": return Math.Ceiling(A(0));
            case "round": return Math.Round(A(0));
            case "sqrt": return A(0) <= 0 ? 0 : Math.Sqrt(A(0));
            case "sign": return Math.Sign(A(0));
            case "sin": return Math.Sin(A(0));
            case "cos": return Math.Cos(A(0));
            case "atan2": return Math.Atan2(A(0), A(1));
            case "clamp": return A(1) > A(2) ? A(1) : Math.Clamp(A(0), A(1), A(2));
            case "rand": return a.Length >= 2 ? A(0) + w.Rng.NextDouble() * (A(1) - A(0)) : w.Rng.NextDouble();
            case "randi": return a.Length >= 2 ? w.Rng.Next((int)A(0), (int)Math.Max(A(0), A(1)) + 1) : w.Rng.Next();
            case "dist": return a.Length >= 4 ? Math.Sqrt((A(2) - A(0)) * (A(2) - A(0)) + (A(3) - A(1)) * (A(3) - A(1))) : 0;
            default: return 0;
        }
    }

    /// <summary>A name in a formula: a key held (key:E), the mouse, the time, obj.prop, local.x, or a variable.</summary>
    double Resolve(World w, string name, GameObject? self)
    {
        if (name.Length > 4 && name.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
            return w.KeysDown.Contains(Key(name[4..])) ? 1 : 0;
        switch (name.ToLowerInvariant())
        {
            case "mousex": return w.MouseX;
            case "mousey": return w.MouseY;
            case "mousescreenx": return w.MouseViewX;
            case "mousescreeny": return w.MouseViewY;
            case "time": return w.Time;
            case "true": return 1;
            case "false": return 0;
            case "pi": return Math.PI;
            case "scenew": case "scenewidth": return w.SceneWidth;
            case "sceneh": case "sceneheight": return w.SceneHeight;
        }
        if (name.StartsWith("count:", StringComparison.OrdinalIgnoreCase))
            return w.Globals.TryGetValue(name[6..], out var lv) && lv is List<object?> list ? list.Count : 0;
        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            string who = name[..dot], prop = name[(dot + 1)..];
            if (who.Equals("local", StringComparison.OrdinalIgnoreCase)) return self != null ? Number(Local(self, prop)) : 0;
            var e = who.Equals("self", StringComparison.OrdinalIgnoreCase) ? self : FindNamed(w, who);
            if (e == null) return 0;
            switch (prop.ToLowerInvariant())
            {
                case "x": return e.X;
                case "y": return e.Y;
                case "vx": return e.Vx;
                case "vy": return e.Vy;
                case "w": case "width": return e.Width;
                case "h": case "height": return e.Height;
                case "cx": return e.X + e.Width / 2;
                case "cy": return e.Y + e.Height / 2;
                case "rot": return e.Angle;
                case "facing": return e.FlipX ? -1 : 1;
                case "opacity": case "alpha": return e.Opacity;
                case "scale": return 1;
                case "speed": return SpeedScale(e);
                case "health": case "hp": return Number(HealthVar(e, "hp"));
                case "maxhealth": case "maxhp": return Number(HealthVar(e, "max_health"));
                case "grounded": return e.OnGround ? 1 : 0;
                case "dead": return e.Destroyed ? 1 : 0;
                case "exists": return 1;
                default: return e == self ? Number(Local(e, prop)) : 0;
            }
        }
        return w.Globals.TryGetValue(name, out var v) ? Number(v) : 0;
    }

    static object? HealthVar(GameObject g, string var) =>
        g.Mechanisms.FirstOrDefault(m => m.Def.Name == "EasyHealth") is { } h && h.Vars.TryGetValue(var, out var v) ? v : null;

    internal static double Number(object? v) => v switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
        List<object?> l => l.Count,
        _ => 0,
    };

    /// <summary>Reads a formula the way EasyEngine's Expr.cs does (tolerant: trailing text is ignored).</summary>
    sealed class FormulaReader(string s)
    {
        int _i;

        public Node Read() => Or();

        Node Or()
        {
            var a = And();
            while (Match("||") || Word("or")) a = new BinNode("or", a, And());
            return a;
        }

        Node And()
        {
            var a = Compare();
            while (Match("&&") || Word("and")) a = new BinNode("and", a, Compare());
            return a;
        }

        Node Compare()
        {
            var a = Add();
            if (Match(">=")) return new BinNode(">=", a, Add());
            if (Match("<=")) return new BinNode("<=", a, Add());
            if (Match("==")) return new BinNode("==", a, Add());
            if (Match("!=")) return new BinNode("!=", a, Add());
            if (Match("=>")) return new BinNode(">=", a, Add());
            if (Match("=<")) return new BinNode("<=", a, Add());
            if (Match("<>")) return new BinNode("!=", a, Add());
            if (Match(">")) return new BinNode(">", a, Add());
            if (Match("<")) return new BinNode("<", a, Add());
            if (Match("=")) return new BinNode("==", a, Add());
            return a;
        }

        Node Add()
        {
            var a = Mul();
            while (true)
            {
                if (Match("+")) a = new BinNode("+", a, Mul());
                else if (Match("-")) a = new BinNode("-", a, Mul());
                else return a;
            }
        }

        Node Mul()
        {
            var a = Unary();
            while (true)
            {
                if (Match("*")) a = new BinNode("*", a, Unary());
                else if (Match("/")) a = new BinNode("/", a, Unary());
                else if (Match("%")) a = new BinNode("%", a, Unary());
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
                    return new CallNode(name, args.ToArray());
                }
                return new NameNode(name);
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
