namespace BuilderEngine.Script;

/// <summary>
/// Turns the demo scene (<see cref="Project.CreateDemo"/>) into a small platformer with the physics starter kit:
/// the ground and two edge walls are Solid, the player is a Platformer, the slime has a Body, plus a one-way ledge and
/// a crate (Body + Solid) to climb.
/// </summary>
public static class PhysicsDemo
{
    /// <summary>Applies the physics changes to a scene made by <see cref="Project.CreateDemo"/> (matched by object name).</summary>
    public static void Apply(Scene s)
    {
        foreach (var o in s.Objects)
            switch (o.Name)
            {
                case "Ground": Ensure(o, "Solid"); break;
                case "Player":
                    o.Mechanisms.RemoveAll(m => string.Equals(m.Name, "PlayerMove", StringComparison.OrdinalIgnoreCase));
                    Ensure(o, "Platformer");
                    break;
                case "Slime": Ensure(o, "Body"); break;
            }
        foreach (var o in ExtraObjects())
            if (!s.Objects.Any(x => x.Name == o.Name)) s.Add(o);
    }

    /// <summary>The objects the physics demo adds to the scene.</summary>
    public static List<SceneObject> ExtraObjects() =>
    [
        new() { Name = "Wall Left", X = 0, Y = 0, Width = 16, Height = 400, Color = "#6B5B4B", Tags = ["ground"], Mechanisms = [new() { Name = "Solid" }] },
        new() { Name = "Wall Right", X = 784, Y = 0, Width = 16, Height = 400, Color = "#6B5B4B", Tags = ["ground"], Mechanisms = [new() { Name = "Solid" }] },
        new() { Name = "Ledge", X = 520, Y = 300, Width = 140, Height = 12, Color = "#8A7560", Tags = ["ground"], Mechanisms = [new() { Name = "OneWay" }] },
        new()
        {
            Name = "Crate", X = 720, Y = 368, Width = 32, Height = 32, Color = "#B08850", Tags = ["crate"],
            Mechanisms = [new() { Name = "Body" }, new() { Name = "Solid" }]
        },
    ];

    static void Ensure(SceneObject o, string mechanism)
    {
        if (!o.Has(mechanism)) o.Mechanisms.Add(new SceneMechanism { Name = mechanism });
    }
}
