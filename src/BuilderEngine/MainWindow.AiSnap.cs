using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using BuilderEngine.Script;

namespace BuilderEngine;

// AI FEATURE, dev tool (part of --snap): drives the AI panel with a fake AI (no network) — Make with its preview and
// Accept, Edit with its diff, Explain, the console's ✨ Fix button and the settings window — appends PASS/FAIL lines to
// checks.txt and renders PNGs.
public sealed partial class MainWindow
{
    const string SnapMakeAnswer = "Here it is:\n```\nmechanism DoubleJump\n    # A second jump in the air (put it on the player instead of Platformer).\n    param height: number = 90\n    uses Platformer\n\n" +
        "    when tick\n        if self.on_ground: air_jumps = 1\n\n    when key \"space\"\n        if not self.on_ground and air_jumps > 0\n            air_jumps -= 1\n" +
        "            self.vy = -sqrt(2 * 1500 * height)\n            play \"jump.wav\"\n```\nIt keeps Platformer's moves and adds one more jump while in the air.";

    const string SnapEditAnswer = "```\nmechanism Coin\n    param value: number = 5\n    param sound: Sound = \"coin.wav\"\n\n    when touched by \"player\"\n" +
        "        score += value\n        play sound\n        flash \"#E8B04B\"\n        destroy self\n```\nNow each coin is worth 5 and the screen flashes gold.";

    const string SnapExplainAnswer = "Coin gives points when the player touches it, then disappears.\n\n" +
        "• `param value` is how many points (you change it in the inspector).\n• `when touched by \"player\"` runs when the player walks into it.\n" +
        "• It adds the value to the score, plays its sound and destroys itself.\n\nTo make a coin worth more, change its value:\n```\n    param value: number = 5\n```";

    async Task SnapAiFeatures(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            if (_play != null) SceneStop();
            ShowTab(scene: true);
            ClearLog();

            // ---- Make → preview → Accept ----
            _aiClientOverride = new ScriptedAiClient(SnapMakeAnswer) { Name = "Snapshot AI (fake)" };
            ShowAiTab(true);
            _aiPanel.SetMode(AiMode.Make);
            await _aiPanel.SendAsync("make a double jump");
            await Task.Delay(500);
            var made = _aiPanel.LastResult;
            Check("AI make: a tested preview of a new mechanism", made is { HasCode: true, Problems.Count: 0, Error: null } && made.Mechanisms[0] is { IsNew: true, Name: "DoubleJump" } && _aiPanel.HasPendingPreview);
            Check("AI make: nothing changes before Accept", !_sources.ContainsKey("DoubleJump"));
            Check("AI tab widens the right column", _bodyGrid?.ColumnDefinitions[2].Width.Value == 420);
            Snap(dir, "11_ai_make_preview");
            SnapControl(_aiPanel, dir, "11b_ai_panel");
            Check("AI make: Accept adds it and opens it in blocks + code", _aiPanel.AcceptPending() && _sources.ContainsKey("DoubleJump") && _current == "DoubleJump" && IsProjectMechanism("DoubleJump") && !_sceneTab);
            await Task.Delay(500);
            Snap(dir, "12_ai_accepted");

            // ---- Edit → diff → Discard ----
            string coinBefore = _sources["Coin"];
            OpenMechanism("Coin");
            _aiClientOverride = new ScriptedAiClient(SnapEditAnswer) { Name = "Snapshot AI (fake)" };
            _aiPanel.SetMode(AiMode.Edit);
            await _aiPanel.SendAsync("make it worth 5 and flash gold");
            await Task.Delay(500);
            var edit = _aiPanel.LastResult;
            var diff = edit?.Mechanisms.FirstOrDefault()?.Diff() ?? [];
            Check("AI edit: a diff of the open mechanism", edit?.Mechanisms.FirstOrDefault() is { IsNew: false, Name: "Coin" } && diff.Any(l => l.Kind == '+') && diff.Any(l => l.Kind == '-'));
            Snap(dir, "13_ai_edit_diff");
            _aiPanel.DiscardPending();
            Check("AI edit: Discard leaves the mechanism as it was", _sources["Coin"] == coinBefore);

            // ---- Explain ----
            _aiClientOverride = new ScriptedAiClient(SnapExplainAnswer) { Name = "Snapshot AI (fake)" };
            _aiPanel.SetMode(AiMode.Explain);
            await _aiPanel.SendAsync("what does this do?");
            await Task.Delay(500);
            Check("AI explain: answers without changing anything", _aiPanel.LastResult is { Mode: AiMode.Explain, HasCode: false } && _sources["Coin"] == coinBefore);
            SnapControl(_aiPanel, dir, "14_ai_explain");

            // ---- ✨ Fix on the console ----
            ShowTab(scene: true);
            ClearLog();
            Check("AI fix: no error, no Fix button", !_aiFixButton.IsVisible);
            Log("⚠ Coin (line 7): There's no command called 'ply'.");
            Check("AI fix: a console error shows ✨ Fix", _aiFixButton.IsVisible && ((IAiHost)this).ErrorToFix!.StartsWith("Coin (line 7)"));
            ShowAiTab(false);
            await Task.Delay(300);
            Snap(dir, "15_ai_fix_button");
            ClearLog();
            Check("AI fix: clearing the console hides it", !_aiFixButton.IsVisible);

            // ---- Settings window ----
            var win = new AiSettingsWindow(new AiSettings(_settings));
            win.Show(this);
            await Task.Delay(800);
            var size = new PixelSize(Math.Max(1, (int)win.Bounds.Width), Math.Max(1, (int)win.Bounds.Height));
            using (var bmp = new RenderTargetBitmap(size, new Vector(96, 96)))
            {
                bmp.Render(win);
                bmp.Save(Path.Combine(dir, "16_ai_settings.png"));
            }
            win.Close();
            Check("AI settings live in the snap's app-data folder (no real keys)", _aiSettings.Secrets().Count() == 0 && _settings.Folder.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) { lines.Add("FAIL AI snapshot: " + e); }
        finally
        {
            _aiClientOverride = null;
            ShowAiTab(false);
        }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }
}
