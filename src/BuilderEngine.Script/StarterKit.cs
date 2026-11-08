using System.Reflection;

namespace BuilderEngine.Script;

/// <summary>The mechanisms that ship with BuilderEngine, written in Builder Script (embedded StarterKit/*.bs).</summary>
public static class StarterKit
{
    public static IEnumerable<(string File, string Source)> Sources()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("StarterKit.")).Order())
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            yield return (name["StarterKit.".Length..], r.ReadToEnd());
        }
    }

    public static void LoadInto(World world)
    {
        foreach (var (_, src) in Sources()) world.Load(src);
    }
}
