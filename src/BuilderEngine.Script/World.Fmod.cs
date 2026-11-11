namespace BuilderEngine.Script;

/// <summary>
/// FMOD Studio events from a game (optional FMOD audio, FmodStudio.cs): the commands raise <see cref="FmodControl"/> and the
/// host plays them through <see cref="FmodEventHost"/>. Without FMOD Studio the host ignores them (one log line).
/// </summary>
public sealed partial class World
{
    /// <summary>FMOD Studio requests (play / stop an event, set a parameter) for the host.</summary>
    public event Action<FmodRequest>? FmodControl;

    internal void RaiseFmod(FmodRequest r) => FmodControl?.Invoke(r);
}
