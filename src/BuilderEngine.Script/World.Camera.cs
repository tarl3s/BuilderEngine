using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// What the game shows: a view of <see cref="ViewWidth"/>×<see cref="ViewHeight"/> scene pixels centred on X, Y.
/// By default it follows the object tagged "player" and stays inside the scene. Ported from EasyEngine's camera
/// (smooth follow, clamp to the level, shake that fades out).
/// </summary>
public sealed class Camera
{
    /// <summary>The most of a scene shown at zoom 1 (smaller scenes are shown whole).</summary>
    public const double MaxViewWidth = 800, MaxViewHeight = 450;

    /// <summary>Centre of the view, in scene pixels.</summary>
    public double X, Y;
    public double Zoom = 1;
    /// <summary>The view at zoom 1 (the scene's size, at most 800×450).</summary>
    public double BaseWidth = MaxViewWidth, BaseHeight = MaxViewHeight;
    public double ViewWidth => BaseWidth / Zoom;
    public double ViewHeight => BaseHeight / Zoom;
    /// <summary>Top-left of the view, shake included.</summary>
    public double Left => X - ViewWidth / 2 + ShakeX;
    public double Top => Y - ViewHeight / 2 + ShakeY;

    /// <summary>Following the "player" by itself (until a script says 'camera follow', 'camera move'...).</summary>
    public bool Auto = true;
    public GameObject? Target;
    /// <summary>How fast it catches up with what it follows (per second; 0 = sticks to it).</summary>
    public double Smooth = 10;

    public bool Limited = true;
    public double LimitX, LimitY, LimitW = MaxViewWidth, LimitH = MaxViewHeight;

    public double ShakeStrength, ShakeTime, ShakeLeft, ShakeX, ShakeY;
    internal bool Placed;
    internal int MoveTween, ZoomTween;

    /// <summary>
    /// Keeps a view centre inside the limits on one axis. A view bigger than the limits is centred on them.
    /// </summary>
    public static double ClampAxis(double center, double view, double min, double size) =>
        view >= size ? min + size / 2 : Math.Clamp(center, min + view / 2, min + size - view / 2);
}

/// <summary>
/// Camera commands:
/// <code>
/// camera follow Boss        camera follow (this object)
/// camera move to 600, 300 in 2
/// camera zoom 2 in 1        camera zoom 1
/// camera shake 6 for 0.5
/// camera limits             (the scene)   camera limits Room (an object)   camera limits 0, 0, 1600, 450   camera limits off
/// camera reset              (follow the player again, zoom 1, the scene's limits)
/// </code>
/// </summary>
public sealed partial class World
{
    public Camera Camera { get; } = new();

    [ModuleInitializer]
    internal static void InstallCamera() => Installers.Add(w =>
    {
        w.Commands["camera follow"] = c =>
        {
            var cam = c.World.Camera;
            var o = ObjectAt(c, 0);
            if (o == null && c.Args.Count > 0) throw c.Error($"There's no object called '{DescribeArg(c, 0)}' to follow.");
            cam.Target = o ?? c.Self;
            cam.Auto = false;
            cam.MoveTween++;
        };
        w.Commands["camera move"] = c =>
        {
            var cam = c.World.Camera;
            List<object?> v = c.Has("to") ? c.Named["to"] : c.Args;
            if (v.Count == 1 && v[0] is GameObject g) v = [g.X + g.Width / 2, g.Y + g.Height / 2];
            if (v.Count < 2) throw c.Error("'camera move' needs where: camera move to 600, 300 in 2.");
            double sx = cam.X, sy = cam.Y, tx = c.Number(v[0]), ty = c.Number(v[1]);
            cam.Auto = false;
            cam.Target = null;
            int id = ++cam.MoveTween;
            Tween(c, NamedNumber(c, "in") ?? 0, EaseOf(c), k => { cam.X = sx + (tx - sx) * k; cam.Y = sy + (ty - sy) * k; },
                () => cam.MoveTween == id);
        };
        w.Commands["camera zoom"] = c =>
        {
            var cam = c.World.Camera;
            double z = c.Args.Count > 0 ? c.Number(c.Args[0]) : c.Has("to") ? c.Number(c.NamedArg("to")) : 1;
            if (z <= 0) throw c.Error("'camera zoom' needs a number above 0 (1 = normal, 2 = twice as close).");
            z = Math.Clamp(z, 0.1, 10);
            double from = cam.Zoom;
            int id = ++cam.ZoomTween;
            Tween(c, NamedNumber(c, "in") ?? 0, EaseOf(c), k => cam.Zoom = from + (z - from) * k, () => cam.ZoomTween == id);
        };
        w.Commands["camera shake"] = c =>
        {
            var cam = c.World.Camera;
            cam.ShakeStrength = c.Args.Count > 0 && c.Args[0] is double ? Math.Abs(c.Number(c.Args[0])) : 8;
            cam.ShakeTime = cam.ShakeLeft = Math.Max(0.05, NumberAfterWord(c, "for") ?? NamedNumber(c, "in") ?? 0.4);
        };
        w.Commands["camera limits"] = c =>
        {
            var world = c.World;
            var cam = world.Camera;
            if (WordAt(c, 0) is "off" or "none") { cam.Limited = false; return; }
            cam.Limited = true;
            if (ObjectAt(c, 0) is { } room) { cam.LimitX = room.X; cam.LimitY = room.Y; cam.LimitW = room.Width; cam.LimitH = room.Height; return; }
            if (c.Args.Count >= 4)
            {
                cam.LimitX = c.Number(c.Args[0]); cam.LimitY = c.Number(c.Args[1]);
                cam.LimitW = Math.Max(1, c.Number(c.Args[2])); cam.LimitH = Math.Max(1, c.Number(c.Args[3]));
                return;
            }
            if (c.Args.Count > 0) throw c.Error("'camera limits' takes nothing (the scene), an object, four numbers (x, y, width, height) or 'off'.");
            world.LimitCameraToScene();
        };
        w.Commands["camera reset"] = c => c.World.ResetCamera();
    });

    void LimitCameraToScene()
    {
        Camera.LimitX = 0; Camera.LimitY = 0; Camera.LimitW = SceneWidth; Camera.LimitH = SceneHeight;
        Camera.Limited = true;
    }

    /// <summary>Back to the default: follow the player, zoom 1, inside the scene, no shake.</summary>
    public void ResetCamera()
    {
        var cam = Camera;
        cam.Auto = true;
        cam.Target = null;
        cam.Zoom = 1;
        cam.MoveTween++;
        cam.ZoomTween++;
        cam.ShakeLeft = cam.ShakeX = cam.ShakeY = 0;
        cam.BaseWidth = Math.Min(SceneWidth, ViewMaxWidth); // 'camera size' (World.View.cs)
        cam.BaseHeight = Math.Min(SceneHeight, ViewMaxHeight);
        LimitCameraToScene();
    }

    /// <summary>A new scene size while playing (the camera's limits follow); make_map grows the scene this way.</summary>
    public void ResizeScene(double width, double height) => SetSceneSize(width, height);

    /// <summary>Called when a scene is loaded: its size becomes the camera's limits.</summary>
    internal void SetSceneSize(double width, double height)
    {
        SceneWidth = Math.Max(1, width);
        SceneHeight = Math.Max(1, height);
        ResetCamera();
        Camera.Placed = false;
    }

    /// <summary>Random.Shared: worlds also run on other threads (the AI check), and a shared Random isn't thread-safe.</summary>
    static Random ShakeRng => Random.Shared;

    void UpdateCamera(double realDt)
    {
        var cam = Camera;
        GameObject? t = cam.Auto ? FindObject("player") : cam.Target;
        if (t != null && t.Destroyed) t = null;
        if (t != null)
        {
            double tx = t.X + t.Width / 2, ty = t.Y + t.Height / 2;
            if (!cam.Placed || cam.Smooth <= 0) { cam.X = tx; cam.Y = ty; }
            else
            {
                double k = 1 - Math.Exp(-cam.Smooth * realDt);
                cam.X += (tx - cam.X) * k;
                cam.Y += (ty - cam.Y) * k;
            }
            cam.Placed = true;
        }
        else if (!cam.Placed)
        {
            // Nothing to follow: start at the scene's top-left corner.
            cam.X = cam.ViewWidth / 2;
            cam.Y = cam.ViewHeight / 2;
            cam.Placed = true;
        }
        if (cam.Limited)
        {
            cam.X = Camera.ClampAxis(cam.X, cam.ViewWidth, cam.LimitX, cam.LimitW);
            cam.Y = Camera.ClampAxis(cam.Y, cam.ViewHeight, cam.LimitY, cam.LimitH);
        }
        if (cam.ShakeLeft > 0)
        {
            cam.ShakeLeft = Math.Max(0, cam.ShakeLeft - realDt);
            double a = cam.ShakeStrength * (cam.ShakeLeft / Math.Max(1e-6, cam.ShakeTime));
            cam.ShakeX = (ShakeRng.NextDouble() * 2 - 1) * a;
            cam.ShakeY = (ShakeRng.NextDouble() * 2 - 1) * a;
        }
        else cam.ShakeX = cam.ShakeY = 0;
    }
}
