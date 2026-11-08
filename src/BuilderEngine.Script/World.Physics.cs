namespace BuilderEngine.Script;

/// <summary>
/// Physics state of an object. Mechanisms switch it on with commands (see StarterKit/Physics.bs):
/// <c>body</c> (falls with gravity, stops at solid things), <c>solid</c> / <c>oneway</c> (bodies stand on it),
/// <c>jump</c> and <c>push</c>. Scripts read and change <c>self.vx</c>, <c>self.vy</c> and read <c>self.on_ground</c>.
/// </summary>
public sealed partial class GameObject
{
    /// <summary>Speed in pixels per second. Every object moves by it; a body also falls and stops at solids.</summary>
    public double Vx, Vy;
    /// <summary>Falls with <see cref="Gravity"/> (px/s²) up to <see cref="MaxFall"/> (px/s) and can't go through solids.</summary>
    public bool HasBody;
    public double Gravity, MaxFall;
    /// <summary>Bodies can't go through it. <see cref="OneWay"/>: only from above (jump up through it, land on it).</summary>
    public bool Solid, OneWay;
    /// <summary>Standing on a solid since the last physics step.</summary>
    public bool OnGround { get; internal set; }
    /// <summary>What it stands on (moving platforms carry it).</summary>
    public GameObject? Ground { get; internal set; }

    internal double Coyote, JumpBuffer, JumpHeight; // seconds left to still jump after leaving a ledge / a jump pressed early
    internal double LastX, LastY;                    // a solid's position after the last physics step (to carry riders)
    internal bool LastKnown;

    /// <summary>Overlapping, or resting against it (within 1 px) when one of them is solid: standing on the ground touches it.</summary>
    internal bool InContact(GameObject o)
    {
        if (Overlaps(o)) return true;
        if (!Solid && !o.Solid) return false;
        return X <= o.X + o.Width + 1 && o.X <= X + Width + 1 && Y <= o.Y + o.Height + 1 && o.Y <= Y + Height + 1;
    }
}

public sealed partial class World
{
    // Ported from EasyEngine's PlayWindow (UpdatePhysics / ResolveX / ResolveY / UpdatePlatformer), as a mechanism toolkit:
    // axis-separated AABB moves (x first, then y), gravity with a max fall speed, landing sets on_ground, one-way
    // platforms, moving-platform carry, coyote time (0.1 s) and a jump buffer (0.12 s). Moves are swept, so a fast
    // fall can't skip through a thin floor.

    public const double DefaultGravity = 1500, DefaultMaxFall = 1000, DefaultJumpHeight = 100;
    const double CoyoteTime = 0.1, JumpBufferTime = 0.12, Eps = 0.01, MaxCarry = 64;

    readonly List<GameObject> _bodies = [];
    readonly List<GameObject> _solids = [];

    /// <summary>Moves every object by its speed; bodies fall and are stopped by solids. Runs once per step, after the ticks.</summary>
    void StepPhysics(double dt)
    {
        _bodies.Clear();
        _solids.Clear();
        for (int i = 0; i < _objects.Count; i++)
        {
            var o = _objects[i];
            if (o.Destroyed) continue;
            if (o.Solid) _solids.Add(o);
            if (o.HasBody) _bodies.Add(o);
            else if (o.Vx != 0 || o.Vy != 0) { o.X += o.Vx * dt; o.Y += o.Vy * dt; } // no Body: flies freely, nothing stops it
        }

        // The lowest body first, so a stack of crates falls and lands together.
        _bodies.Sort(LowestFirst.Instance);
        for (int i = 0; i < _bodies.Count; i++) StepBody(_bodies[i], dt);

        for (int i = 0; i < _solids.Count; i++)
        {
            var s = _solids[i];
            s.LastX = s.X; s.LastY = s.Y; s.LastKnown = true;
        }
    }

    void StepBody(GameObject b, double dt)
    {
        // Standing on something that moved since the last step (a Patrol platform): move along with it.
        var g = b.Ground;
        if (g != null && g.Solid && !g.Destroyed && g.LastKnown)
        {
            double cx = g.X - g.LastX, cy = g.Y - g.LastY;
            if (Math.Abs(cx) <= MaxCarry && Math.Abs(cy) <= MaxCarry) { b.X += cx; b.Y += cy; }
        }

        // A jump pressed a moment before landing happens now.
        if (b.JumpBuffer > 0)
        {
            if (b.OnGround || b.Coyote > 0) DoJump(b, b.JumpHeight, dt);
            else b.JumpBuffer -= dt;
        }

        b.Vy += b.Gravity * dt;
        if (b.MaxFall > 0 && b.Vy > b.MaxFall) b.Vy = b.MaxFall;

        MoveX(b, b.Vx * dt);
        b.OnGround = false;
        b.Ground = null;
        MoveY(b, b.Vy * dt);

        b.Coyote = b.OnGround ? CoyoteTime : Math.Max(0, b.Coyote - dt);
    }

    /// <summary>Moves sideways by dx, stopping at the first solid side in the way (one-way platforms don't block sideways).</summary>
    void MoveX(GameObject b, double dx)
    {
        if (dx == 0) return;
        double top = b.Y + Eps, bottom = b.Y + b.Height - Eps;
        double edge = dx > 0 ? b.X + b.Width : b.X;
        double stop = edge + dx;
        bool hit = false;
        for (int i = 0; i < _solids.Count; i++)
        {
            var s = _solids[i];
            if (s == b || s.OneWay || s.Destroyed || s.Y >= bottom || s.Y + s.Height <= top) continue;
            if (dx > 0) { if (s.X >= edge - Eps && s.X < stop) { stop = s.X; hit = true; } }
            else { double r = s.X + s.Width; if (r <= edge + Eps && r > stop) { stop = r; hit = true; } }
        }
        b.X += stop - edge;
        if (hit) b.Vx = 0;
    }

    /// <summary>Moves up or down by dy, landing on the first solid top below (or bumping the first ceiling above).</summary>
    void MoveY(GameObject b, double dy)
    {
        if (dy == 0) return;
        double left = b.X + Eps, right = b.X + b.Width - Eps;
        double edge = dy > 0 ? b.Y + b.Height : b.Y;
        double stop = edge + dy;
        GameObject? hit = null;
        for (int i = 0; i < _solids.Count; i++)
        {
            var s = _solids[i];
            if (s == b || s.Destroyed || s.X >= right || s.X + s.Width <= left) continue;
            if (dy > 0) { if (s.Y >= edge - Eps && s.Y < stop) { stop = s.Y; hit = s; } }    // feet were above its top
            else if (!s.OneWay) { double r = s.Y + s.Height; if (r <= edge + Eps && r > stop) { stop = r; hit = s; } }
        }
        b.Y += stop - edge;
        if (hit == null) return;
        b.Vy = 0;
        if (dy > 0) { b.OnGround = true; b.Ground = hit; }
    }

    /// <summary>Jumps <paramref name="height"/> pixels high (the speed comes from the body's gravity), if it can jump now.</summary>
    void DoJump(GameObject b, double height, double dt)
    {
        double gdt = b.Gravity * dt;
        // Speed whose step-by-step rise peaks at 'height': solves v²/2g − v·dt/2 = h. No gravity: the height is the speed.
        b.Vy = b.Gravity > 0 ? -(gdt + Math.Sqrt(gdt * gdt + 8 * b.Gravity * Math.Max(0, height))) / 2 : -height;
        b.OnGround = false;
        b.Ground = null;
        b.Coyote = 0;
        b.JumpBuffer = 0;
    }

    /// <summary>The physics commands. Returns false when the name isn't one of them.</summary>
    bool RunPhysicsCommand(string name, GameObject self, List<object?> args, int line)
    {
        double Arg(int i, double def) => args.Count > i ? ToNumber(args[i], line) : def;
        switch (name)
        {
            case "body":
                self.HasBody = true;
                self.Gravity = Arg(0, DefaultGravity);
                self.MaxFall = Arg(1, DefaultMaxFall);
                return true;
            case "solid":
                self.Solid = args.Count == 0 || Truthy(args[0]);
                self.OneWay = false;
                return true;
            case "oneway":
                self.Solid = self.OneWay = args.Count == 0 || Truthy(args[0]);
                return true;
            case "jump":
            {
                if (!self.HasBody) throw new ScriptError("'jump' needs a Body: add 'uses Body' to the mechanism (or the Body mechanism to the object).", line);
                double h = Arg(0, DefaultJumpHeight);
                if (self.OnGround || self.Coyote > 0) DoJump(self, h, LastDt);
                else { self.JumpBuffer = JumpBufferTime; self.JumpHeight = h; } // pressed in the air: jump when it lands soon
                return true;
            }
            case "push":
                if (args.Count < 2) throw new ScriptError("'push' needs two numbers: 'push 300, -200' (pixels per second).", line);
                self.Vx += Arg(0, 0);
                self.Vy += Arg(1, 0);
                if (self.Vy < 0) { self.OnGround = false; self.Ground = null; }
                return true;
        }
        return false;
    }

    sealed class LowestFirst : IComparer<GameObject>
    {
        public static readonly LowestFirst Instance = new();
        public int Compare(GameObject? a, GameObject? b) => (b!.Y + b.Height).CompareTo(a!.Y + a.Height);
    }
}
