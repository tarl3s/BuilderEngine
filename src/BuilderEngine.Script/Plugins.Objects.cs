using BuilderEngine.Plugin;

namespace BuilderEngine.Script;

// PLUGINS: game objects as plugins see them (BuilderEngine.Plugin.IPluginObject).
public sealed partial class GameObject : IPluginObject
{
    double IPluginObject.X { get => X; set => X = value; }
    double IPluginObject.Y { get => Y; set => Y = value; }
    double IPluginObject.Width { get => Width; set => Width = value; }
    double IPluginObject.Height { get => Height; set => Height = value; }
    double IPluginObject.Vx { get => Vx; set => Vx = value; }
    double IPluginObject.Vy { get => Vy; set => Vy = value; }
    ICollection<string> IPluginObject.Tags => Tags;
    void IPluginObject.Destroy() => Destroyed = true;
}
