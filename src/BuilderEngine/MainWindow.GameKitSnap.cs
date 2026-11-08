using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

// GAME KIT, dev tool (part of --snap): plays small scenes made of starter-kit mechanisms and renders the game view —
// a generated tile world with the hotbar, the same world at night with a torch, a dark top-down cave lit around the
// player, a menu screen with buttons, a hand of cards, a security camera with static — and the tile painter in the
// editor. Appends PASS/FAIL lines to checks.txt.
public sealed partial class MainWindow
{
    async Task SnapGameKit(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        int added = 0;
        try
        {
            if (_play != null) SceneStop();

            static SceneObject Obj(string name, double x, double y, double w, double h, string color, string[] mechs, string[]? tags = null,
                Dictionary<string, object?>? ps = null) => new()
            {
                Name = name, X = x, Y = y, Width = w, Height = h, Color = color, Tags = [.. tags ?? []],
                Mechanisms = mechs.Select((m, i) => new SceneMechanism { Name = m, Params = i == 0 && ps != null ? new(ps, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase) }).ToList(),
            };

            async Task<World> Play(Scene s)
            {
                _project.Scenes.Add(s);
                added++;
                OpenScene(_project.Scenes.Count - 1);
                await Task.Delay(150);
                ScenePlay();
                await Task.Delay(500);
                return _play!.World;
            }

            // ---- 1. A generated side-view world, the hotbar, digging ----
            var sandbox = new Scene { Name = "Kit Sandbox", Width = 800, Height = 450, Background = "#8EC5E8" };
            sandbox.Add(Obj("Gen", 0, 0, 16, 16, "#333333", ["WorldGen"], ps: new() { ["seed"] = 11.0, ["columns"] = 160.0, ["rows"] = 80.0, ["surface"] = 30.0 }));
            sandbox.Add(Obj("Player", 1000, 0, 12, 28, "#5B8DEF", ["Platformer", "TileMiner"], ["player"]));
            sandbox.Add(Obj("Bar", 0, 0, 16, 16, "#333333", ["Hotbar"]));
            var w = await Play(sandbox);
            w.Give("dirt", 24); w.Give("stone", 9); w.Give("coal", 3); w.Give("torch", 4);
            w.ItemLooks["torch"] = new ItemLook("#FFB347", "");
            var p = w.FindObject("player")!;
            await Task.Delay(800);
            Check("WorldGen made a 160 × 80 map", w.HasTiles && w.Tiles.Main!.Columns == 160);
            Check("the player stands on the generated ground", p.OnGround);
            // Dig the tile under the player's feet-side with the mouse.
            var cam = w.Camera;
            double tx = p.X + p.Width + 8, ty = p.Y + p.Height + 8;
            w.MouseMove((tx - cam.Left) * cam.Zoom, (ty - cam.Top) * cam.Zoom);
            string before = (string)w.Functions["tile_at"]([tx, ty])!;
            w.MouseDown();
            await Task.Delay(250);
            SnapControl(_play!, dir, "kit_1_tile_world_digging");
            await Task.Delay(1200);
            w.MouseUp();
            Check($"holding the mouse digs the tile ({before} → '{w.Functions["tile_at"]([tx, ty])}')", (string)w.Functions["tile_at"]([tx, ty])! == "" && before != "");
            Check("the hotbar is on screen", w.Hotbar.Visible);

            // ---- 2. The same world at night: sunlight down, a torch on the player ----
            w.Load("mechanism SnapNight\n    when start\n        darkness 1\n        sunlight 0.18\n        light find(\"player\") 150 with \"#FFC870\", 0.25\n");
            w.Spawn("night", 0, 0, ["SnapNight"]);
            await Task.Delay(400);
            SnapControl(_play!, dir, "kit_2_sandbox_night_torch");
            Check("night: dark away from the torch, lit next to it", w.LightAt(p.X + 6, p.Y + 14) > 0.7 && w.LightAt(p.X + 380, p.Y + 600) < 0.2);
            SceneStop();

            // ---- 3. A dark top-down cave, light around the player ----
            var cave = new Scene { Name = "Kit Cave", Width = 800, Height = 450, Background = "#000000" };
            cave.Add(Obj("Gen", 0, 0, 16, 16, "#333333", ["CaveGen"], ps: new() { ["seed"] = 4.0, ["columns"] = 60.0, ["rows"] = 50.0 }));
            cave.Add(Obj("Player", 900, 800, 22, 22, "#E8B04B", ["TopDown", "Light"], ["player"], new() { ["speed"] = 160.0 }));
            cave.Add(Obj("Dark", 0, 0, 16, 16, "#333333", ["Darkness"], ps: new() { ["amount"] = 0.94 }));
            w = await Play(cave);
            w.FindObject("player")!.LightRadius = 170;
            w.KeyDown("right");
            await Task.Delay(700);
            w.KeyUp("right");
            await Task.Delay(200);
            SnapControl(_play!, dir, "kit_3_cave_darkness_light");
            Check("the cave is dark around the lit player", w.Darkness > 0.9 && w.LightsNow().Count >= 1);
            SceneStop();

            // ---- 4. A menu screen with buttons ----
            var menu = new Scene { Name = "Kit Menu", Width = 800, Height = 450, Background = "#20160F" };
            menu.Add(Obj("Title", 200, 40, 400, 70, "#141414", ["Panel"], ps: new() { ["title"] = "NIGHT SHIFT", ["title_size"] = 34.0, ["screen"] = "Main", ["shown"] = true }));
            menu.Add(Obj("Main Menu", 280, 130, 240, 240, "#141414", ["Menu"], ps: new() { ["options"] = new List<object?> { "New game", "Continue", "Options", "Quit" }, ["screen"] = "Main" }));
            menu.Add(Obj("Help", 620, 390, 160, 40, "#3A3A3A", ["Button"], ps: new() { ["text"] = "? Help", ["color"] = "#4FC3F7", ["message"] = "help" }));
            w = await Play(menu);
            await Task.Delay(300);
            var buttons = w.Objects.Where(o => o.Name == "Button").ToList();
            Check("the menu made a button per option", buttons.Count == 4 && buttons.All(b => !b.Hidden));
            if (buttons.Count > 1)
            {
                var b1 = buttons[1];
                w.MouseMove(b1.X + 20, b1.Y + 10);
                await Task.Delay(200);
                Check("hovering a button changes its colour", b1.Color == "#E8956F");
            }
            SnapControl(_play!, dir, "kit_4_menu_buttons");
            SceneStop();

            // ---- 5. A hand of cards ----
            var cards = new Scene { Name = "Kit Cards", Width = 800, Height = 450, Background = "#1F4A33" };
            cards.Add(Obj("Deck", 680, 40, 80, 112, "#3A2A6A", ["Deck"]));
            for (int i = 0; i < 3; i++)
                cards.Add(Obj($"Slot {i + 1}", 220 + i * 130, 120, 96, 128, "#2E6B4A", ["Panel"], ["slot", "ui"]));
            cards.Add(Obj("Turns", 0, 0, 16, 16, "#333333", ["TurnManager"]));
            cards.Add(Obj("End Turn", 20, 20, 140, 40, "#D97757", ["Button"], ps: new() { ["text"] = "End turn", ["message"] = "end turn" }));
            w = await Play(cards);
            w.Tell(w.FindObject("Deck")!, "draw", [5.0]);
            await Task.Delay(700);
            var hand = w.Objects.Where(o => o.Is("in_hand")).ToList();
            Check("the deck dealt 5 cards into a row", hand.Count == 5 && hand.Select(c => c.X).Distinct().Count() == 5);
            if (hand.Count == 5)
            {
                w.Tell(hand[4], "flip", []);
                var c0 = hand[0];
                w.MouseMove(c0.X + 20, c0.Y + 20);
                w.MouseDown();
                var slot = w.FindObject("Slot 2")!;
                w.MouseMove(slot.X + 40, slot.Y + 50);
                await Task.Delay(150);
                w.MouseUp();
                await Task.Delay(500);
                Check("a card dropped on a slot snaps into it", Math.Abs(c0.X + c0.Width / 2 - (slot.X + slot.Width / 2)) < 1);
            }
            SnapControl(_play!, dir, "kit_5_cards_hand");
            SceneStop();

            // ---- 6. A security camera with static ----
            var office = new Scene { Name = "Kit Cameras", Width = 2400, Height = 900, Background = "#101418" };
            office.Add(Obj("Office", 40, 300, 700, 400, "#2A2A30", []));
            office.Add(Obj("Hall", 1200, 100, 900, 300, "#33302A", []));
            office.Add(Obj("Creature", 1500, 220, 50, 120, "#7A2E2E", []));
            office.Add(Obj("Cam 1", 40, 300, 700, 400, "#000000", ["CameraPoint"]));
            office.Add(Obj("Cam 2", 1200, 100, 900, 300, "#000000", ["CameraPoint"]));
            office.Add(Obj("Monitor", 0, 0, 16, 16, "#000000", ["CameraMonitor"], ps: new() { ["cameras"] = new List<object?> { "Cam 1", "Cam 2" }, ["noise"] = 0.25 }));
            office.Add(Obj("Cam label", 20, 20, 160, 36, "#141414", ["Panel"], ps: new() { ["title"] = "CAM 2 — HALL", ["title_size"] = 16.0, ["screen"] = "Cameras" }));
            w = await Play(office);
            w.KeyDown("tab"); await Task.Delay(60); w.KeyUp("tab");
            w.KeyDown("d2"); await Task.Delay(60); w.KeyUp("d2");
            await Task.Delay(400);
            Check("the monitor looks through Cam 2", w.CameraView?.Name == "Cam 2" && w.NoiseAmount > 0);
            SnapControl(_play!, dir, "kit_6_security_camera");
            SceneStop();

            // ---- 7. The tile painter in the editor ----
            var paint = new Scene { Name = "Kit Paint", Width = 800, Height = 450 };
            _project.Scenes.Add(paint);
            added++;
            OpenScene(_project.Scenes.Count - 1);
            await Task.Delay(200);
            MakeTileMap();
            var layer = _scene.TileLayers![0];
            for (int c = 0; c < layer.Columns; c++) { layer.Set(c, 12, 2); layer.Set(c, 13, 1); layer.Set(c, 14, 3); }
            for (int c = 6; c < 11; c++) layer.Set(c, 8, 5);
            _tileKind = 4;
            RefreshTileBar();
            _canvas.InvalidateVisual();
            await Task.Delay(300);
            Snap(dir, "kit_7_editor_tile_painter");
            Check("the editor has a tile map to paint", _scene.TileLayers is { Count: 1 } && _tileOverlay != null);
            SetTileMode(null);
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        finally
        {
            if (_play != null) SceneStop();
            for (int i = 0; i < added && _project.Scenes.Count > 1; i++) _project.Scenes.RemoveAt(_project.Scenes.Count - 1);
            _sceneIndex = 0;
            OpenScene(0);
        }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
