using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

public sealed partial class GameObject
{
    /// <summary>How far it is turned, in degrees (clockwise; drawn rotated round its centre). Scripts: self.angle.</summary>
    public double Angle;
}

/// <summary>
/// The size of the view, the camera's feel, and cinema bars:
/// <code>
/// camera size 960, 540      the most of the scene shown at zoom 1 (default 800 × 450; a smaller scene is shown whole)
/// camera smooth 8           how fast the camera catches up with what it follows (per second; 0 = sticks to it)
/// camera hold               stop following: the view stays where it is (camera follow / camera reset start again)
/// bars on   bars off        cinema bars slide in from the top and the bottom (bars on in 0.4 = how long they take)
/// scene_width, scene_height, camera_x, camera_y, view_width, view_height   values a script can read
/// self.angle                how far an object is turned (degrees, clockwise), drawn rotated
/// </code>
/// </summary>
public sealed partial class World
{
    /// <summary>The most of a scene the view shows at zoom 1 ('camera size'); kept by <see cref="ForNextScene"/>.</summary>
    public double ViewMaxWidth { get; set; } = Camera.MaxViewWidth;
    public double ViewMaxHeight { get; set; } = Camera.MaxViewHeight;

    /// <summary>0 = no cinema bars, 1 = fully in. Each bar is <see cref="BarsSize"/> of the view's height.</summary>
    public double BarsAmount { get; private set; }
    public bool BarsWanted { get; private set; }
    public const double BarsSize = 0.11;
    double _barsTime = 0.4;

    [ModuleInitializer]
    internal static void InstallView() => Installers.Add(w =>
    {
        w.Commands["camera size"] = c =>
        {
            if (c.Args.Count < 2) throw c.Error("'camera size' needs a width and a height: camera size 960, 540.");
            double vw = c.Number(c.Args[0]), vh = c.Number(c.Args[1]);
            if (vw < 16 || vh < 16) throw c.Error("'camera size' needs at least 16 × 16 pixels.");
            var world = c.World;
            world.ViewMaxWidth = vw;
            world.ViewMaxHeight = vh;
            world.Camera.BaseWidth = Math.Min(world.SceneWidth, vw);
            world.Camera.BaseHeight = Math.Min(world.SceneHeight, vh);
        };
        w.Commands["camera smooth"] = c =>
        {
            if (c.Args.Count < 1) throw c.Error("'camera smooth' needs a number: camera smooth 8 (0 = sticks to what it follows).");
            c.World.Camera.Smooth = Math.Max(0, c.Number(c.Args[0]));
        };
        w.Commands["camera hold"] = c =>
        {
            var cam = c.World.Camera;
            cam.Auto = false;
            cam.Target = null;
            cam.MoveTween++;
        };
        w.Commands["bars"] = c =>
        {
            var world = c.World;
            string? word = WordAt(c, 0);
            bool on = word switch
            {
                "on" => true,
                "off" or "none" => false,
                _ => c.Args.Count == 0 || Truthy(c.Args[0]),
            };
            world.BarsWanted = on;
            if (NamedNumber(c, "in") is double t) world._barsTime = Math.Max(0, t);
        };
        w.EngineValues["scene_width"] = x => x.SceneWidth;
        w.EngineValues["scene_height"] = x => x.SceneHeight;
        w.EngineValues["camera_x"] = x => x.Camera.X;
        w.EngineValues["camera_y"] = x => x.Camera.Y;
        w.EngineValues["view_width"] = x => x.Camera.ViewWidth;
        w.EngineValues["view_height"] = x => x.Camera.ViewHeight;
    });

    /// <summary>Cinema bars slide towards what was asked (real time, like the other screen effects).</summary>
    void UpdateBars(double realDt)
    {
        double target = BarsWanted ? 1 : 0;
        if (BarsAmount == target) return;
        double step = _barsTime <= 0 ? 1 : realDt / _barsTime;
        BarsAmount = BarsWanted ? Math.Min(1, BarsAmount + step) : Math.Max(0, BarsAmount - step);
    }
}
