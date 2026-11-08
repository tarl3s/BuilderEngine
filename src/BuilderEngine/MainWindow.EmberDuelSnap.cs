using BuilderEngine.Script;

namespace BuilderEngine;

// EMBER DUEL sample, dev tool (part of --snap when the open project is the Ember Duel sample): plays it in the editor
// and renders the game view at its key moments — the title, the deck builder, your hand with a card zoomed, a card
// being dragged, combat, the rival's turn, the result screen — plus the editor with the scene and CardView's blocks.
// Writes PASS/FAIL lines to checks.txt.
public sealed partial class MainWindow
{
    async Task SnapEmberDuel(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            OpenScene(0);
            await Task.Delay(300);
            Snap(dir, "ember_0_editor_scene");
            OpenMechanism("CardView");
            await Task.Delay(500);
            Snap(dir, "ember_0b_blocks_cardview");
            ShowTab(scene: true);
            await Task.Delay(200);

            ScenePlay();
            await Task.Delay(900);
            var w = _play!.World;
            SnapControl(_play!, dir, "ember_1_title");
            Check("the title screen shows", w.ScreenShown("Title"));

            void Point(GameObject o, double fx = 0.5, double fy = 0.5) => w.MouseMove(o.X + o.Width * fx, o.Y + o.Height * fy);
            async Task Click(GameObject o) { Point(o); await Task.Delay(120); w.MouseDown(); await Task.Delay(40); w.MouseUp(); await Task.Delay(200); }

            // ---- Deck builder ----
            await Click(w.FindObject("Build Button")!);
            await Task.Delay(500);
            var pool = w.Objects.Where(o => o.Tags.Contains("pool")).ToList();
            if (pool.Count > 10) { Point(pool[10]); await Task.Delay(400); }
            SnapControl(_play!, dir, "ember_2_deck_builder");
            Check($"the deck builder shows 30 cards ({pool.Count})", pool.Count == 30);

            // ---- Battle ----
            await Click(w.FindObject("Save Deck")!);
            await Task.Delay(400);
            SnapControl(_play!, dir, "ember_3_battle_banner");
            var duel = w.FindObject("Duel")!.Mechanisms[0].Vars;
            for (int i = 0; i < 60 && !(Equals(duel["turn"], "you") && w.FindObject("Banner")!.Hidden); i++) await Task.Delay(100);
            await Task.Delay(500);
            var you = (Record)duel["you"]!;
            var hand = ((List<object?>)you["hand"]!).Cast<GameObject>().ToList();
            Check($"your turn with a hand of {hand.Count}", Equals(duel["turn"], "you") && hand.Count == 4);
            you["mana"] = 6.0; // a richer first turn for the pictures
            w.Broadcast("arrange", []);
            await Task.Delay(400);
            if (hand.Count > 1) { Point(hand[1], 0.5, 0.6); await Task.Delay(400); }
            SnapControl(_play!, dir, "ember_4_hand_zoom");

            // Drag a creature halfway to a lane.
            var creature = hand.FirstOrDefault(c => c.Draggable && c.DropZone == "open_lane");
            var lane = w.FindObject("Lane 2")!;
            if (creature != null)
            {
                Point(creature, 0.5, 0.6);
                await Task.Delay(200);
                w.MouseDown();
                await Task.Delay(100);
                w.MouseMove(lane.X + 44, lane.Y + 150);
                await Task.Delay(250);
                SnapControl(_play!, dir, "ember_5_dragging");
                w.MouseMove(lane.X + 44, lane.Y + 58);
                await Task.Delay(150);
                w.MouseUp();
                await Task.Delay(900);
                Check("the dragged creature stands on the lane", lane.Mechanisms[0].Vars.GetValueOrDefault("unit") == creature);
            }
            else Check("a creature to drag", false);
            // Play whatever else can be played, then end the turn.
            foreach (var c in ((List<object?>)you["hand"]!).Cast<GameObject>().ToList())
            {
                if (!c.Draggable) continue;
                var target = c.DropZone == "open_lane" ? w.Objects.FirstOrDefault(o => o.Tags.Contains("open_lane"))
                    : c.DropZone == "foe" ? w.FindObject("Rival") : w.FindObject("You");
                if (target == null) continue;
                Point(c, 0.5, 0.6); await Task.Delay(150);
                w.MouseDown(); await Task.Delay(80);
                w.MouseMove(target.X + target.Width / 2, target.Y + target.Height / 2); await Task.Delay(120);
                w.MouseUp(); await Task.Delay(900);
            }
            w.MouseMove(560, 300);
            await Task.Delay(300);
            SnapControl(_play!, dir, "ember_6_board");
            await Click(w.FindObject("End Turn")!);
            await Task.Delay(330);
            SnapControl(_play!, dir, "ember_7_combat");
            for (int i = 0; i < 40 && !Equals(duel["turn"], "rival"); i++) await Task.Delay(100);
            await Task.Delay(2600);
            SnapControl(_play!, dir, "ember_8_rival_turn");

            // ---- Fast-forward: both sides played by the computer until someone wins ----
            ((Record)duel["you"]!)["ai"] = true;
            w.Tell(w.FindObject("Duel")!, "end turn", []);
            for (int i = 0; i < 400 && !w.ScreenShown("Result"); i++) w.RunFor(1);
            await Task.Delay(500);
            SnapControl(_play!, dir, "ember_9_result");
            Check($"the match ended ({duel["winner"]} won in {duel["round"]} rounds)", w.ScreenShown("Result"));
            Check("no script errors: " + string.Join(" | ", w.Errors), w.Errors.Count == 0);
            File.WriteAllText(Path.Combine(dir, "console.txt"), _console.Text ?? "");
            SceneStop();
        }
        catch (Exception e) { lines.Add("FAIL " + e); }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }
}
