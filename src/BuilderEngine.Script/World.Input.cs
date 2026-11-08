using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// The keyboard as the host reports it (<see cref="KeyDown"/>/<see cref="KeyUp"/>/<see cref="Click"/>), and
/// <c>lock controls</c> / <c>unlock controls</c>: while locked, <c>key("left")</c> is false and <c>when key</c> doesn't
/// fire, so the player stands still during a cutscene — but the cutscene's own lines keep running.
/// An open dialogue takes the keys it uses (space, enter, E, Z, X; arrows and numbers for choices) so they don't also
/// make the player jump.
/// </summary>
public sealed partial class World
{
    public bool ControlsLocked { get; private set; }
    readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Keys a dialogue used: they don't reach the game until let go.</summary>
    readonly HashSet<string> _swallowed = new(StringComparer.OrdinalIgnoreCase);

    [ModuleInitializer]
    internal static void InstallInput() => Installers.Add(w =>
    {
        w.Commands["lock controls"] = c => c.World.LockControls(true);
        w.Commands["unlock controls"] = c => c.World.LockControls(false);
    });

    public void LockControls(bool locked)
    {
        if (ControlsLocked == locked) return;
        ControlsLocked = locked;
        KeysDown.Clear();
        if (!locked)
            foreach (var k in _held)
                if (!_swallowed.Contains(k)) KeysDown.Add(k); // still held: walking goes on, but no new press
    }

    /// <summary>A key went down (auto-repeat is ignored).</summary>
    public void KeyDown(string key)
    {
        key = NormalKey(key);
        if (!_held.Add(key)) return;
        if (UiKey(key)) { _swallowed.Add(key); return; }
        if (ControlsLocked) return;
        HotbarKey(key); // 1-9 pick a hotbar slot (World.Inventory.cs); the key still reaches the game
        if (KeysDown.Add(key)) PressKey(key);
    }

    public void KeyUp(string key)
    {
        key = NormalKey(key);
        _held.Remove(key);
        _swallowed.Remove(key);
        KeysDown.Remove(key);
    }

    /// <summary>Lower case; the number keys ("d1" from the keyboard, "numpad1") are just "1", so 'when key "1"' works.</summary>
    static string NormalKey(string key)
    {
        key = key.ToLowerInvariant();
        if (key.Length == 2 && key[0] == 'd' && char.IsAsciiDigit(key[1])) return key[1..];
        if (key.Length == 7 && key.StartsWith("numpad") && char.IsAsciiDigit(key[6])) return key[6..];
        return key;
    }

    /// <summary>The window lost the keyboard: every key counts as let go.</summary>
    public void ReleaseKeys()
    {
        _held.Clear();
        _swallowed.Clear();
        KeysDown.Clear();
    }

    /// <summary>A click on the game. <paramref name="option"/> = the choice clicked in an 'ask' (0-based), if any.</summary>
    public void Click(int? option = null)
    {
        var d = Dialogue;
        if (d == null || d.Closed) return;
        if (!d.FullyTyped) { d.Typed = d.Text.Length; return; }
        if (d.Options == null) { CloseDialogue(d); return; }
        if (option is int i && i >= 0 && i < d.Options.Count) Choose(d, i);
    }
}
