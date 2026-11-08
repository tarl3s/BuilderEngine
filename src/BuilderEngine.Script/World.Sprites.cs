using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

public sealed partial class GameObject
{
    /// <summary>The picture drawn in the object's box (a file in the project's images folder), or null for a coloured box.</summary>
    public string? Image;
    /// <summary>The picture is a strip of <see cref="SheetFrames"/> frames side by side; <see cref="SheetFrame"/> is drawn.</summary>
    public int SheetFrames = 1, SheetFrame;
    /// <summary>Drawn mirrored ('face left'). Pictures are drawn as they are when facing right.</summary>
    public bool FlipX;
    /// <summary>Drawing order: higher is drawn on top ('layer 2'). Same layer = scene order.</summary>
    public double Z;

    internal string? AnimName;
    internal List<string>? AnimFrames;
    internal int AnimCount;
    internal double AnimFps = 8, AnimStart;
    internal bool AnimOnce, AnimDone;
}

/// <summary>
/// Pictures and animation:
/// <code>
/// look "hero.png"            look Boss "boss_angry.png"     look none (back to the coloured box)
/// animate "walk"             (walk1.png, walk2.png… at 8 frames a second)   animate "walk" at 12
/// animate "run.png" with 6   (a strip of 6 frames; without 'with', square frames are counted)
/// animate "attack" once      (plays once and waits until it's done)          animate none
/// face left    face right    face player (turn towards an object)    face Boss left
/// layer 2                    (drawn above layer 0 and 1)
/// </code>
/// </summary>
public sealed partial class World
{
    [ModuleInitializer]
    internal static void InstallSprites() => Installers.Add(w =>
    {
        w.Commands["look"] = LookCommand;
        w.Commands["animate"] = AnimateCommand;
        w.Commands["face"] = FaceCommand;
        w.Commands["layer"] = c =>
        {
            var o = c.Args.Count >= 2 ? ObjectAt(c, 0) ?? c.Self : c.Self;
            if (c.Args.Count == 0) throw c.Error("'layer' needs a number: layer 2 draws this object above layer 0 and 1.");
            o.Z = c.Number(c.Args[^1]);
        };
    });

    /// <summary>The object (first value, when there are more) and where the rest start.</summary>
    static (GameObject Target, int Next) TargetAndRest(CommandContext c, int minRest)
    {
        if (c.Args.Count > minRest && ObjectAt(c, 0) is { } o && c.Args[0] is not double) return (o, 1);
        return (c.Self, 0);
    }

    static void LookCommand(CommandContext c)
    {
        var (o, i) = TargetAndRest(c, 1);
        if (i >= c.Args.Count || WordAt(c, i) is "none" or "nothing" || (c.Args[i] is string s0 && s0.Length == 0))
        {
            StopAnimation(o);
            o.Image = null;
            o.SheetFrames = 1; o.SheetFrame = 0;
            return;
        }
        string name = ToText(c.Args[i]).Trim();
        c.World.CheckPicture(name);
        StopAnimation(o);
        o.Image = name;
        o.SheetFrames = 1; o.SheetFrame = 0;
    }

    void CheckPicture(string name)
    {
        if (ProjectFolder != null && Images.Resolve(ProjectFolder, name) == null)
            WarnOnce($"There's no picture “{name}” in the project's images folder. Import it in the inspector (Picture ▸ Import picture…).");
    }

    static void StopAnimation(GameObject o)
    {
        o.AnimName = null;
        o.AnimFrames = null;
        o.AnimCount = 0;
    }

    static void AnimateCommand(CommandContext c)
    {
        var world = c.World;
        int once = IndexOfWord(c, "once");
        var (o, i) = TargetAndRest(c, once >= 0 ? 2 : 1);
        if (i >= c.Args.Count || i == once || WordAt(c, i) is "none" or "off" or "stop") { StopAnimation(o); return; }
        string name = ToText(c.Args[i]).Trim();
        double fps = NamedNumber(c, "at") ?? 8;
        if (fps <= 0) throw c.Error("'at' is frames per second, above 0: animate \"walk\" at 8.");
        bool playOnce = once >= 0;
        int? wanted = NamedNumber(c, "with") is double n ? Math.Max(1, (int)Math.Round(n)) : null;
        string key = $"{name}|{wanted}|{playOnce}";
        if (o.AnimName == key && !playOnce) { o.AnimFps = fps; return; } // already playing: keep going (safe in 'when tick')

        var frames = Images.Frames(world.ProjectFolder, name);
        int count;
        if (frames.Count > 0) count = frames.Count;
        else
        {
            string? path = Images.Resolve(world.ProjectFolder, name);
            if (path == null && world.ProjectFolder != null)
            {
                world.WarnOnce($"There's no animation “{name}”: add numbered pictures ({name}1.png, {name}2.png…) or one strip picture to the images folder.");
                return;
            }
            frames = null!;
            count = wanted ?? (path != null && Images.Size(path) is { } sz && sz.Height > 0 ? Math.Max(1, (int)Math.Round((double)sz.Width / sz.Height)) : 1);
            o.Image = path != null ? Path.GetFileName(path) : name;
            o.SheetFrames = count;
        }
        o.AnimName = key;
        o.AnimFrames = frames;
        o.AnimCount = count;
        o.AnimFps = fps;
        o.AnimStart = world.Time;
        o.AnimOnce = playOnce;
        o.AnimDone = false;
        ApplyFrame(o, 0);
        if (playOnce) c.WaitUntil(() => o.AnimDone || o.AnimName != key || o.Destroyed);
    }

    static void ApplyFrame(GameObject o, int f)
    {
        if (o.AnimFrames != null) { o.Image = o.AnimFrames[f]; o.SheetFrames = 1; o.SheetFrame = 0; }
        else o.SheetFrame = f;
    }

    void UpdateAnimations()
    {
        var objs = Objects;
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            if (o.AnimName == null || o.AnimCount <= 0 || o.AnimDone) continue;
            int f = (int)Math.Floor((Time - o.AnimStart) * o.AnimFps + 1e-9);
            if (o.AnimOnce && f >= o.AnimCount) { o.AnimDone = true; f = o.AnimCount - 1; }
            ApplyFrame(o, ((f % o.AnimCount) + o.AnimCount) % o.AnimCount);
        }
    }

    static void FaceCommand(CommandContext c)
    {
        var o = c.Self;
        int i = 0;
        if (c.Args.Count >= 2 && ObjectAt(c, 0) is { } first) { o = first; i = 1; }
        if (i >= c.Args.Count) throw c.Error("'face' needs a side or an object: face left, face right, face player.");
        switch (WordAt(c, i))
        {
            case "left": o.FlipX = true; return;
            case "right": o.FlipX = false; return;
        }
        if (c.Args[i] is string side && side.ToLowerInvariant() is "left" or "right") { o.FlipX = side.Equals("left", StringComparison.OrdinalIgnoreCase); return; }
        var toward = ObjectAt(c, i) ?? throw c.Error($"'face' needs left, right or an object — there's no '{DescribeArg(c, i)}'.");
        double mine = o.X + o.Width / 2, theirs = toward.X + toward.Width / 2;
        if (Math.Abs(theirs - mine) > 0.5) o.FlipX = theirs < mine;
    }
}
