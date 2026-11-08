using BuilderEngine.Script;

namespace BuilderEngine;

// NIGHT SHIFT SAMPLE, dev tool (part of --snap when the open project is the sample): plays the game in the editor and
// renders the title, the night's intro, the dark office, two cameras, a creature in the lit doorway, a shut door, the
// jumpscare, the game over and the 6 AM screens. Writes PASS/FAIL lines to nightshift_checks.txt, then the app closes.
//   BuilderEngine.exe --make-sample "Night Shift" <tmp>\proj
//   BuilderEngine.exe --project <tmp>\proj --snap <tmp>\snap --mute
public sealed partial class MainWindow
{
    async Task<bool> SnapNightShift(string dir)
    {
        if (_project.Name != NightShiftSample.Name) return false;
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            Snap(dir, "ns_0_editor");
            ScenePlay();
            await Task.Delay(900);
            var w = _play!.World;
            string Mode() => World.ToText(w.Globals.GetValueOrDefault("mode"));
            void Shot(string name) => SnapControl(_play!, dir, name);
            void Tap(string key) { w.KeyDown(key); w.KeyUp(key); }
            GameObject O(string name) => w.Objects.First(o => o.Name == name);

            Shot("ns_1_title");
            Check("the title screen shows", w.ScreenShown("Title"));
            w.Broadcast("new game", []);
            await Task.Delay(1200);
            Shot("ns_2_intro");
            for (int i = 0; i < 40 && Mode() != "night"; i++) await Task.Delay(100);
            await Task.Delay(1200);
            Shot("ns_3_office");
            Check("on duty at 12 AM", Mode() == "night");

            Tap("tab");
            await Task.Delay(600);
            Shot("ns_4_cam_hall");
            Check("the tablet shows the Grand Hall", World.ToText(w.Functions["camera_view"]([])) == "Grand Hall");
            Tap("3");
            await Task.Delay(600);
            Shot("ns_5_cam_music");
            Tap("2");
            await Task.Delay(500);
            Shot("ns_5b_cam_gallery");
            Tap("6");
            await Task.Delay(500);
            Shot("ns_5c_cam_east");
            Tap("tab");
            await Task.Delay(300);

            // Clementine in the left doorway, the hall light on.
            var doll = O("Clementine");
            var spot = O("Left Doorway");
            doll.X = spot.X; doll.Y = spot.Y; doll.Width = spot.Width; doll.Height = spot.Height;
            Tap("a");
            await Task.Delay(500);
            Shot("ns_6_doorway_lit");
            Check("the hall light shows her", w.LightAt(spot.X + spot.Width / 2, spot.Y + spot.Height / 3) > 0.5);
            Tap("q");
            Tap("e");
            await Task.Delay(500);
            Shot("ns_7_doors_shut");
            Tap("a");

            w.Globals["caught_by"] = "Clementine";
            w.Broadcast("caught", ["scare_doll.png"]);
            await Task.Delay(400);
            Shot("ns_8_jumpscare");
            await Task.Delay(2200);
            Shot("ns_9_game_over");
            Check("game over screen", w.ScreenShown("Lose"));

            w.Globals["win_text"] = "Night 1 survived. They will be busier tomorrow.";
            w.ShowScreen("Lose", false);
            w.ShowScreen("Win", true);
            O("Win Title").Hidden = true;
            await Task.Delay(300);
            Shot("ns_10_six_am");
            Check("no script errors", w.Errors.Count == 0);
            if (w.Errors.Count > 0) lines.AddRange(w.Errors.Select(e => "  " + e));
            SceneStop();
        }
        catch (Exception e) { lines.Add("FAIL crashed: " + e); }
        File.WriteAllLines(Path.Combine(dir, "nightshift_checks.txt"), lines);
        File.WriteAllText(Path.Combine(dir, "console.txt"), _console.Text ?? "");
        return true;
    }
}
