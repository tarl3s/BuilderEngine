using BuilderEngine.Plugin;

namespace HelloPlugin;

/// <summary>
/// A sample plugin. It adds to Builder Script:
///   confetti 20          throws 20 little pieces up from this object (they fall and land; tag "confetti")
///   wave(time)           a smooth number going -1 → 1 → -1 once per second (wave(time, 2) = twice per second)
/// Use them in any mechanism:
///   when touched by "player"
///       confetti 30
///   when tick
///       self.y = 200 + wave(time) * 20
/// </summary>
public sealed class Hello : IBuilderPlugin
{
    public string Name => "Hello";
    public string Description => "confetti and wave(t): a small example of a C# plugin";

    public void Register(IPluginHost host)
    {
        host.AddFunction("wave", args =>
        {
            double t = args.Count > 0 && args[0] is double d ? d : 0;
            double perSecond = args.Count > 1 && args[1] is double s ? s : 1;
            return Math.Sin(t * perSecond * Math.PI * 2);
        }, "a smooth number from -1 to 1 and back, once per second", "time", "per_second");

        host.AddCommand("confetti", c =>
        {
            int amount = c.Args.Count > 0 ? (int)Math.Clamp(c.Number(c.Arg(0)), 0, 200) : 20;
            if (c.IsEditor) { c.Print("confetti only shows in the game."); return; }
            var rng = new Random();
            var self = c.Self;
            for (int i = 0; i < amount; i++)
            {
                var piece = c.Spawn("Confetti", self.X + self.Width / 2, self.Y, "Body");
                piece.Width = piece.Height = 6;
                piece.Vx = rng.Next(-220, 221);
                piece.Vy = -rng.Next(250, 520);
                piece.Tags.Add("confetti");
            }
        }, "throws little pieces up from this object", "amount");
    }
}
