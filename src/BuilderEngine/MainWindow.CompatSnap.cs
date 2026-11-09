using Avalonia.Controls;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// EASYCOMPAT, dev tool (part of --snap with `--project <temp copy of an EasyEngine project>`): the "Built on EasyEngine"
// card with what runs here, then Play — the start scene with the player walking, and the first scene with a cutscene
// that plays when the scene starts, through to its end. PNGs compat_*.png, checks in compat_checks.txt, the Builder
// Script made from its rules and cutscenes in compat_generated.txt. Runs before the link snap (which edits the copy).
public sealed partial class MainWindow
{
    async Task SnapEasyCompat(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            var start = EasyCompat.FirstScene(_project);
            OpenScene(_project.Scenes.IndexOf(start));
            await Task.Delay(400);
            var withBehaviors = _scene.Objects.FirstOrDefault(o => EasyLink.EasyBehaviors(o).Contains("Platformer"))
                ?? _scene.Objects.FirstOrDefault(EasyLink.HasEasyBehaviors);
            if (withBehaviors != null)
            {
                Select(withBehaviors.Id);
                await Task.Delay(400);
                SnapControl(_inspectorRoot, dir, "compat_0_card");
                Check("the card says which behaviors run here", _inspectorRoot.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text is { } s && (s.Contains("— runs here") || s.Contains("doesn't run here yet"))));
                Select(0);
            }

            ClearLog();
            ScenePlay();
            await Task.Delay(500);
            Check("Play builds a world with EasyCompat", _play?.World.Easy != null);
            _play?.World.KeysDown.Add("right");
            await Task.Delay(1500);
            _play?.World.KeysDown.Remove("right");
            Snap(dir, "compat_1_playing");
            Check($"the start scene “{start.Name}” plays with no script errors", _play != null && _play.World.Errors.Count == 0);
            SceneStop();
            await Task.Delay(300);

            // The first scene with a cutscene that starts with the scene.
            int cut = _project.Scenes.FindIndex(s => s.Objects.Any(o => o.Easy?["Behaviors"] is System.Text.Json.Nodes.JsonArray b
                && b.Any(x => (string?)x?["Kind"] == "Cutscene" && (string?)x?["Texts"]?["trigger"] == "start")));
            if (cut < 0) { lines.Add("PASS (no cutscene that plays when its scene starts)"); }
            else
            {
                OpenScene(cut);
                await Task.Delay(300);
                ClearLog();
                ScenePlay();
                await Task.Delay(900);
                Snap(dir, "compat_2_cutscene");
                Check("the cutscene started (the player is locked, cinema bars)", _play?.World is { ControlsLocked: true, BarsWanted: true });
                int shot = 3;
                bool finished = false;
                for (int i = 0; i < 60 && !finished; i++)
                {
                    await Task.Delay(500);
                    if (i is 4 or 14 or 26) Snap(dir, $"compat_{shot++}_cutscene");
                    _play?.World.Click(); // moves the dialogue on, like a click
                    finished = (_console.Text ?? "").Contains("finished");
                }
                Snap(dir, $"compat_{shot}_after");
                Check("the cutscene reached its end", finished);
                Check("…with no script errors", _play != null && _play.World.Errors.Count == 0);
                Check("…and gave the keys back", _play?.World.ControlsLocked == false);
            }
            File.WriteAllText(Path.Combine(dir, "compat_console.txt"), _console.Text ?? "");
            if (_play?.World.Easy is { } easy)
                File.WriteAllText(Path.Combine(dir, "compat_generated.txt"),
                    string.Join("\n", easy.Generated.Select(g => g.Value)) + "\n# not supported yet:\n" + string.Join("\n", easy.Unsupported.Select(u => "#   " + u)));
            SceneStop();
            await Task.Delay(300);
            OpenScene(0);
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        File.WriteAllLines(Path.Combine(dir, "compat_checks.txt"), lines);
    }
}
