using System.Diagnostics;
using BuilderEngine.Script;

/// <summary>The game kit: tiles, lights, mouse, UI screens, inventory, crafting, cards, sounds, performance. Called from Program.cs.</summary>
static class GameKitTests
{
    const double Dt = 1.0 / 60;

    static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected <{expected}> but got <{actual}>");
    }

    static void True(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    static void Near(double expected, double actual, double tol, string what) =>
        True(Math.Abs(expected - actual) <= tol, $"{what}: {actual} (expected {expected} ± {tol})");

    static World NewWorld(string src = "")
    {
        var w = new World();
        StarterKit.LoadInto(w);
        if (src.Length > 0) w.Load(src);
        return w;
    }

    static string M(string body, string name = "T") => $"mechanism {name}\n" + string.Join("\n", body.Split('\n').Select(l => "    " + l)) + "\n";

    static object? Var(GameObject o, string name) => o.Mechanisms[0].Vars.GetValueOrDefault(name);

    static void NoErrors(World w) => Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));

    /// <summary>A world with a 25 × 15 map of 32-px cells: a floor on row 12 and a wall at column 15.</summary>
    static World TileWorld(string src = "")
    {
        var w = NewWorld(M("when start\n    tile_kind \"dirt\" with \"#8B5A2B\" hardness 0.5\n    tile_kind \"stone\" with \"#777777\" hardness 1 drop \"rock\"\n    tile_kind \"plank\" with \"#C08040\" oneway\n    tile_kind \"bedrock\" with \"#111111\" unbreakable\n    make_map 25, 15 with 32\n    fill_tiles \"dirt\" from 0, 384 to 799, 415\n    fill_tiles \"stone\" from 480, 0 to 511, 383", "Map") + src);
        w.Spawn("map", 0, 0, ["Map"]);
        w.Frame(Dt);
        return w;
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Game kit: events in the language");
        test("game events parse, print and read back", () =>
        {
            string src = M("when clicked: x = 1\nwhen hovered: x = 1\nwhen unhovered: x = 1\nwhen mouse pressed at x, y: x = 1\nwhen mouse released \"right\": x = 1\n" +
                           "when picked up: x = 1\nwhen dropped: x = 1\nwhen dropped on \"slot\": x = 1\nwhen received \"card\": x = 1\nwhen tile broken \"stone\" at x, y: x = 1\nwhen tile placed: x = 1\nwhen mouse: x = 1");
            var d = Parser.Parse(src)[0];
            var names = d.Whens.Select(w => w.Event.Kind == EventKind.Game ? w.Event.Name : "signal:" + w.Event.Name).ToList();
            Eq("clicked,hovered,unhovered,mouse pressed,mouse released,picked up,dropped,dropped,received,tile broken,tile placed,signal:mouse", string.Join(",", names));
            Eq("x, y", d.Whens[3].Event.Bind);
            Eq("right", d.Whens[4].Event.Filter);
            Eq("slot", d.Whens[7].Event.Filter);
            Eq("when dropped on \"slot\"", Printer.WhenHead(d.Whens[7].Event));
            Eq("when tile broken \"stone\" at x, y", Printer.WhenHead(d.Whens[9].Event));
            var again = Parser.Parse(Printer.Print(d))[0];
            Eq(d.Whens.Count, again.Whens.Count);
            for (int i = 0; i < d.Whens.Count; i++) Eq(d.Whens[i].Event, again.Whens[i].Event, $"when {i}");
            Eq("input", BlockEdits.EventLabel(EventKind.Game));
            Eq("tile broken", BlockEdits.ParseEvent(EventKind.Game, "tile broken \"dirt\"").Name);
        });
        test("'stop sound' is a command, a bare 'stop' still stops", () =>
        {
            var d = Parser.Parse(M("when start\n    stop sound \"rain.wav\"\n    stop sounds\n    stop\n    x = 1"))[0];
            True(d.Whens[0].Body[0] is CommandStmt { Name: "stop" } && d.Whens[0].Body[1] is CommandStmt && d.Whens[0].Body[2] is StopStmt, "statement kinds");
        });

        Console.WriteLine("Game kit: tiles");
        test("a body lands on tiles and a tile wall stops it", () =>
        {
            var w = TileWorld();
            var p = w.Spawn("p", 100, 200, ["Body"]);
            p.Width = 24; p.Height = 40;
            w.RunFor(1.5);
            Near(384 - 40, p.Y, 0.05, "stands on the floor");
            True(p.OnGround, "on ground");
            True(p.Ground == null, "the ground is the tile map");
            p.Vx = 600;
            w.RunFor(1);
            Near(480 - 24, p.X, 0.05, "stopped by the stone column");
            p.X = 600; p.Vx = -600;
            w.RunFor(1);
            Near(512, p.X, 0.05, "stopped from the other side");
            NoErrors(w);
        });
        test("one-way tiles hold from above only; a fast fall doesn't skip a thin floor", () =>
        {
            var w = TileWorld(M("when start\n    set_tile 40, 300 to \"plank\"\n    set_tile 72, 300 to \"plank\"", "Planks"));
            w.Spawn("pl", 0, 0, ["Planks"]);
            w.Frame(Dt);
            var p = w.Spawn("p", 40, 330, ["Body"]);
            p.Width = 20; p.Height = 30;
            w.Frame(Dt);
            p.Vy = -900; // jumps up through the planks
            w.RunFor(0.2);
            True(p.Y < 288, $"went up through the plank ({p.Y})");
            w.RunFor(2);
            Near(288 - 30, p.Y, 0.05, "lands on the plank");
            var fast = w.Spawn("f", 200, 0, ["Body"]);
            fast.Width = fast.Height = 10;
            w.Frame(Dt);
            fast.MaxFall = 100000; fast.Vy = 50000;
            w.Frame(Dt);
            Near(384 - 10, fast.Y, 0.05, "a very fast fall still lands");
        });
        test("set, break, place, fill and dig tiles; 'when tile broken' gets kind, drop and x, y", () =>
        {
            var w = TileWorld(M("when tile broken at x, y\n    broke = kind\n    dropped = drop\n    bx = x\n    by = y\nwhen tile placed \"dirt\"\n    placed_count += 1", "Watch"));
            var watch = w.Spawn("watch", 0, 0, ["Watch"]);
            w.Load(M("when start\n    a = tile_at(100, 390)\n    s = tile_solid(100, 390)\n    e = tile_at(100, 100)\n    break_tile 490, 100\n    b = broken\n    break_tile 100, 100\n    place_tile \"dirt\" at 100, 100\n    p1 = placed\n    place_tile \"dirt\" at 100, 100\n    p2 = placed\n    set_tile 5, 5 to \"bedrock\"\n    break_tile 5, 5\n    u = broken\n    gy = ground_y(100)\n    tx = tile_x(70)\n    ts = tile_size()", "Ops"));
            var o = w.Spawn("ops", 0, 0, ["Ops"]);
            w.Frame(Dt);
            NoErrors(w);
            Eq("dirt", Var(o, "a")); Eq(true, Var(o, "s")); Eq("", Var(o, "e"));
            Eq("stone", Var(o, "b"));
            Eq("stone", Var(watch, "broke")); Eq("rock", Var(watch, "dropped"));
            Eq(496.0, Var(watch, "bx")); Eq(112.0, Var(watch, "by"));
            Eq(true, Var(o, "p1")); Eq(false, Var(o, "p2"));
            Eq(1.0, Var(watch, "placed_count"));
            Eq("", Var(o, "u"), "bedrock can't be broken");
            Eq(96.0, Var(o, "gy"), "the placed dirt at row 3 is now the highest");
            Eq(80.0, Var(o, "tx")); Eq(32.0, Var(o, "ts"));
            // Digging: dirt has hardness 0.5 → about 30 frames at power 1, 15 at power 2.
            w.Load(M("when tick\n    dig_tile 200, 390 with 2\n    if broken != \"\": done = time", "Digger"));
            var dg = w.Spawn("dg", 0, 0, ["Digger"]);
            w.RunFor(0.6);
            True(Var(dg, "done") is double t && t > 0.2 && t < 0.4, $"broke after about 0.25 s ({Var(dg, "done")})");
            Eq("", w.Functions["tile_at"]([200.0, 390.0]));
        });
        test("placing a solid tile where a body stands is refused", () =>
        {
            var w = TileWorld();
            var p = w.Spawn("p", 100, 344, ["Body"]);
            p.Width = 20; p.Height = 40;
            w.RunFor(0.2);
            var l = w.Tiles.Main!;
            var (c, r) = l.CellAt(105, 360);
            True(!w.PlaceTile(l, c, r, w.Tiles.Id("dirt")), "refused inside the body");
            True(w.PlaceTile(l, c + 3, r, w.Tiles.Id("dirt")), "allowed next to it");
        });
        test("noise and random repeat with the same seed", () =>
        {
            string src = M("when start\n    random_seed 42\n    n1 = noise(3.3, 7.1)\n    n2 = noise(3.31, 7.1)\n    r = random(1, 1000000)\n    lo = 1\n    hi = 0\n    for each i in range(0, 400)\n        v = noise(i * 0.37, i * 0.11)\n        lo = min(lo, v)\n        hi = max(hi, v)");
            var a = NewWorld(src); var oa = a.Spawn("a", 0, 0, ["T"]); a.Frame(Dt);
            var b = NewWorld(src); var ob = b.Spawn("b", 0, 0, ["T"]); b.Frame(Dt);
            NoErrors(a);
            Eq(Var(oa, "n1"), Var(ob, "n1")); Eq(Var(oa, "r"), Var(ob, "r"));
            True(Math.Abs((double)Var(oa, "n1")! - (double)Var(oa, "n2")!) < 0.05, "smooth: close inputs give close values");
            True((double)Var(oa, "lo")! >= 0 && (double)Var(oa, "hi")! <= 1 && (double)Var(oa, "hi")! - (double)Var(oa, "lo")! > 0.4, "0..1 with a good spread");
            var c = NewWorld(src.Replace("42", "7")); var oc = c.Spawn("c", 0, 0, ["T"]); c.Frame(Dt);
            True(!Equals(Var(oa, "n1"), Var(oc, "n1")), "another seed, another world");
        });
        test("WorldGen makes hills, layers, caves and ores, and puts the player on the ground", () =>
        {
            var w = NewWorld();
            var gen = w.Spawn("gen", 0, 0, ["WorldGen"], new() { ["WorldGen"] = new() { ["seed"] = 5.0, ["columns"] = 200.0, ["rows"] = 100.0 } });
            var p = w.Spawn("Player", 800, 0, ["Body"], null, "player");
            p.Width = 12; p.Height = 28;
            var sw = Stopwatch.StartNew();
            w.Frame(Dt);
            sw.Stop();
            NoErrors(w);
            var l = w.Tiles.Main!;
            Eq(200, l.Columns); Eq(100, l.Rows);
            var counts = new Dictionary<string, int>();
            foreach (var id in l.Cells) { string k = w.Tiles.Kind(id)?.Name ?? "air"; counts[k] = counts.GetValueOrDefault(k) + 1; }
            foreach (var k in new[] { "grass", "dirt", "stone", "coal", "iron", "bedrock", "air" }) True(counts.GetValueOrDefault(k) > 0, $"has {k}: {string.Join(", ", counts)}");
            int underground = 0, caves = 0;
            for (int r = 60; r < 98; r++) for (int c = 0; c < 200; c++) { underground++; if (l.Get(c, r) == 0) caves++; }
            True(caves > underground * 0.03 && caves < underground * 0.5, $"some caves ({caves} of {underground})");
            var heights = Enumerable.Range(0, 200).Select(c => w.GroundY(c * 16 + 8)).ToList();
            True(heights.Max() - heights.Min() >= 48, "hills");
            w.RunFor(0.5);
            True(p.OnGround, "the player stands on the ground");
            True(sw.ElapsedMilliseconds < 1500, $"made quickly ({sw.ElapsedMilliseconds} ms)");
            Eq(3200.0, w.SceneWidth, "the scene grows to the map");
        });
        test("CaveGen makes a walled top-down cave with a floor layer and a clearing at the player", () =>
        {
            var w = NewWorld();
            w.Spawn("gen", 0, 0, ["CaveGen"], new() { ["CaveGen"] = new() { ["seed"] = 3.0, ["columns"] = 60.0, ["rows"] = 60.0 } });
            var p = w.Spawn("Player", 900, 900, ["TopDown"], null, "player");
            p.Width = p.Height = 24;
            w.Frame(Dt);
            NoErrors(w);
            Eq(2, w.Tiles.Layers.Count);
            Eq("Floor", w.Tiles.Layers[0].Name);
            Eq("wall", w.Functions["tile_at"]([5.0, 5.0]));
            Eq("", w.Functions["tile_at"]([910.0, 910.0]), "clearing");
            Eq("floor", w.Functions["tile_at"]([910.0, 910.0, "Floor"]));
            w.KeyDown("right");
            w.RunFor(3);
            True(p.X < 60 * 32 - 32 - 24 + 0.1, "walls stop the top-down walker");
            True(p.Y == 900, "no gravity");
        });
        test("tile layers are saved as short text and come back the same", () =>
        {
            var s = new Scene { TileKinds = [new TileKind { Name = "dirt" }, new TileKind { Name = "rock", Solid = false, Hardness = 2, Drop = "none" }] };
            var l = new TileLayer(40, 20, 16);
            for (int c = 0; c < 40; c++) l.Set(c, 19, 1);
            l.Set(3, 3, 2);
            s.TileLayers = [l];
            string json = s.ToJson();
            True(json.Contains("\"data\": \"0*123,2,0*636,1*40\""), json);
            var back = Scene.FromJson(json);
            Eq(40, back.TileLayers![0].Columns);
            Eq((ushort)2, back.TileLayers[0].Get(3, 3));
            Eq((ushort)1, back.TileLayers[0].Get(39, 19));
            Eq(false, back.TileKinds![1].Solid); Eq(2.0, back.TileKinds[1].Hardness); Eq("", back.TileKinds[1].DropItem);
            True(!new Scene().ToJson().Contains("tile", StringComparison.OrdinalIgnoreCase), "a scene without tiles saves no tile fields");
            // Playing a scene copies its tiles: breaking one doesn't change the scene.
            s.Objects.Add(new SceneObject { Name = "p", X = 20, Y = 0, Width = 10, Height = 10, Mechanisms = [new SceneMechanism { Name = "Body" }] });
            var w = NewWorld();
            w.LoadScene(s);
            w.RunFor(1);
            Near(304 - 10, w.FindObject("p")!.Y, 0.05, "lands on the scene's tiles");
            w.BreakTile(w.Tiles.Main!, 3, 19);
            Eq((ushort)1, l.Get(3, 19), "the scene keeps its tile");
        });

        Console.WriteLine("Game kit: lights");
        test("lights: the Light mechanism, darkness, light_at", () =>
        {
            var w = NewWorld(M("when start\n    darkness 0.9"));
            w.Spawn("dark", 0, 0, ["T"]);
            var torch = w.Spawn("torch", 400, 200, ["Light"], new() { ["Light"] = new() { ["radius"] = 100.0, ["flicker"] = 0.3 } });
            torch.Width = torch.Height = 20;
            w.Frame(Dt);
            Eq(0.9, w.Darkness);
            var lights = w.LightsNow();
            Eq(1, lights.Count);
            Near(410, lights[0].X, 0.01, "centre");
            True(lights[0].Radius > 80 && lights[0].Radius <= 100, "flicker changes the radius a little");
            True(w.LightAt(410, 210) > 0.8, "bright next to it");
            Eq(0.0, w.LightAt(700, 400), "dark far away");
            torch.LightRadius = 0;
            Eq(0, w.LightsNow().Count, "light off");
        });
        test("the light overlay: dark around, clear near the light, tinted by its colour", () =>
        {
            var w = NewWorld(M("when start\n    darkness 1\n    light 120 with \"#FF0000\""));
            var o = w.Spawn("p", 390, 215, ["T"]);
            o.Width = o.Height = 20;
            w.Frame(Dt);
            var buf = new LightBuffer();
            buf.Resize(200, 113);
            w.ComputeLighting(buf, 0, 0, 800, 450);
            uint far = buf.Pixels[0], near = buf.Pixels[56 * 200 + 100];
            True(far >> 24 > 250, $"black far away ({far >> 24})");
            True(near >> 24 < 60, $"clear at the light ({near >> 24})");
            uint ring = buf.Pixels[56 * 200 + 120]; // ~80 px away: fading, reddish
            True(((ring >> 16) & 255) > ((ring >> 8) & 255), "tinted red");
        });
        test("sunlight lights tiles open to the sky; caves stay dark", () =>
        {
            var w = TileWorld(M("when start\n    darkness 1\n    sunlight 1\n    fill_tiles \"stone\" from 0, 192 to 799, 383", "Sky"));
            w.Spawn("sky", 0, 0, ["Sky"]);
            w.Frame(Dt);
            True(w.LightAt(100, 100) > 0.95, "the open sky is lit");
            True(w.LightAt(100, 360) < 0.05, "deep in the rock it's dark");
            True(w.LightAt(100, 200) > 0.4, "the light reaches a little into the ground");
            w.Load(M("when start\n    sunlight 0.2", "Night"));
            w.Spawn("n", 0, 0, ["Night"]);
            w.Frame(Dt);
            True(w.LightAt(100, 100) < 0.25, "night");
        });

        Console.WriteLine("Game kit: mouse");
        test("mouse_x / mouse_y follow the camera; clicks hit the top object", () =>
        {
            var w = NewWorld(M("when clicked\n    clicks += 1\nwhen hovered\n    hov = true\nwhen unhovered\n    hov = false", "Clicky") + M("when mouse pressed at x, y\n    px = x\n    py = y\nwhen mouse released \"right\"\n    right = true", "Any"));
            w.ResizeScene(2000, 450);
            var low = w.Spawn("low", 1000, 200, ["Clicky"]);
            var high = w.Spawn("high", 1010, 210, ["Clicky"]);
            var any = w.Spawn("any", 0, 0, ["Any"]);
            var player = w.Spawn("Player", 990, 200, null, null, "player");
            w.Frame(Dt);
            True(w.Camera.Left > 500, "the camera follows the player");
            double vx = 1015 - w.Camera.Left, vy = 215 - w.Camera.Top;
            w.MouseMove(vx, vy);
            Near(1015, w.MouseX, 1e-6, "mouse_x"); Near(215, w.MouseY, 1e-6, "mouse_y");
            w.Frame(Dt);
            Eq(true, Var(high, "hov")); Eq(null, Var(low, "hov"));
            w.MouseDown(); w.Frame(Dt); w.MouseUp();
            Eq(1.0, Var(high, "clicks")); Eq(null, Var(low, "clicks"), "only the top one");
            Near(1015, (double)Var(any, "px")!, 1e-6, "pressed at x"); Near(215, (double)Var(any, "py")!, 1e-6, "pressed at y");
            w.MouseMove(1, 1);
            w.Frame(Dt);
            Eq(false, Var(high, "hov"), "unhovered");
            w.MouseDown("right"); w.MouseUp("right");
            Eq(true, Var(any, "right"));
            high.Z = -1;
            w.MouseMove(vx, vy);
            w.MouseDown(); w.MouseUp();
            Eq(1.0, Var(low, "clicks"), "a lower layer is under");
            w.Camera.Zoom = 2;
            w.Frame(Dt);
            Near(w.Camera.Left + vx / 2, w.MouseX, 1e-6, "zoom");
            NoErrors(w);
        });
        test("screen objects: drawn without the camera, clicked in screen pixels, above the scene", () =>
        {
            var w = NewWorld(M("when clicked\n    clicks += 1", "Clicky"));
            w.ResizeScene(2000, 450);
            var ui = w.Spawn("btn", 20, 20, ["Clicky"], null, "ui");
            ui.Width = 100; ui.Height = 30;
            var under = w.Spawn("under", 0, 0, ["Clicky"]);
            under.Width = 3000; under.Height = 3000;
            var player = w.Spawn("Player", 1500, 200, null, null, "player");
            w.Frame(Dt);
            True(ui.Ui, "the tag ui");
            w.MouseMove(50, 30);
            w.MouseDown(); w.MouseUp();
            Eq(1.0, Var(ui, "clicks")); Eq(null, Var(under, "clicks"));
            w.MouseMove(300, 300);
            w.MouseDown(); w.MouseUp();
            Eq(1.0, Var(under, "clicks"));
            Eq(true, w.Functions["mouse_over_ui"]([]) is false, "not over the ui now");
            // A screen object never touches scene objects.
            var coin = w.Spawn("coin", 20, 20, ["Coin"], null, "coin");
            w.Frame(Dt);
            True(!coin.Destroyed, "different spaces don't touch");
        });
        test("drag and drop: follows the mouse, dropped on a zone, snaps back elsewhere", () =>
        {
            var w = NewWorld(M("when dropped on \"slot\"\n    on = other.name\nwhen dropped\n    drops += 1\nwhen picked up\n    picked = true", "CardT") + M("when received \"card\"\n    got = other.name", "SlotT"));
            var card = w.Spawn("card", 100, 300, ["CardT", "Draggable"], new() { ["Draggable"] = new() { ["zone"] = "slot" } }, "card", "ui");
            card.Width = 60; card.Height = 80;
            var slot = w.Spawn("slot A", 400, 100, ["SlotT"], null, "slot", "ui");
            slot.Width = 80; slot.Height = 100;
            w.Frame(Dt);
            True(card.Draggable && card.DropZone == "slot", "Draggable with a zone");
            w.MouseMove(130, 340);
            w.MouseDown();
            Eq(true, Var(card, "picked"));
            w.MouseMove(440, 150);
            w.Frame(Dt);
            Near(410, card.X, 1e-6, "follows the mouse (keeps where it was grabbed)");
            w.MouseUp();
            Eq("slot A", Var(card, "on")); Eq("card", Var(slot, "got")); Eq(1.0, Var(card, "drops"));
            w.RunFor(0.3);
            Near(410, card.X, 0.5, "snapped into the slot (x)"); Near(110, card.Y, 0.5, "snapped into the slot (y)");
            w.MouseMove(440, 150);
            w.MouseDown();
            w.MouseMove(700, 400);
            w.Frame(Dt);
            w.MouseUp();
            w.RunFor(0.4);
            Near(410, card.X, 0.5, "dropped on nothing: back where it was picked up");
            Eq(2.0, Var(card, "drops"));
            NoErrors(w);
        });
        test("cursor, locked controls ignore the mouse, mouse_down()", () =>
        {
            var w = NewWorld(M("when start\n    cursor \"hand\"\nwhen tick\n    down = mouse_down()\nwhen clicked\n    clicks += 1", "C"));
            var o = w.Spawn("o", 0, 0, ["C"]);
            w.Frame(Dt);
            Eq("hand", w.Cursor);
            w.MouseMove(10, 10);
            w.MouseDown(); w.Frame(Dt);
            Eq(true, Var(o, "down")); Eq(1.0, Var(o, "clicks"));
            w.MouseUp();
            w.LockControls(true);
            w.MouseDown(); w.Frame(Dt);
            Eq(false, Var(o, "down")); Eq(1.0, Var(o, "clicks"));
        });

        Console.WriteLine("Game kit: screens and buttons");
        test("buttons broadcast, screens show and hide, Menu makes its buttons", () =>
        {
            var w = NewWorld(M("when told \"play\" v\n    played = true\nwhen told \"Quit\"\n    quit = true", "Listener"));
            var l = w.Spawn("listener", 0, 0, ["Listener"]);
            var b = w.Spawn("Play", 300, 200, ["Button"]);
            b.Width = 200; b.Height = 40;
            var menu = w.Spawn("menu", 50, 50, ["Menu"], new() { ["Menu"] = new() { ["options"] = new List<object?> { "Start", "Quit" }, ["screen"] = "Main" } });
            menu.Width = 200; menu.Height = 150;
            w.Frame(Dt); w.Frame(Dt);
            True(b.Ui && b.Caption == "Play" && b.Color == "#D97757", "a button on the screen with its text and colour");
            w.MouseMove(310, 210); w.Frame(Dt);
            Eq("#E8956F", b.Color, "hover colour");
            w.MouseDown(); w.MouseUp();
            Eq(true, Var(l, "played"));
            var buttons = w.Objects.Where(o => o.Name == "Button").ToList();
            Eq(2, buttons.Count);
            True(buttons.All(x => x.Hidden) && menu.Hidden, "the screen starts hidden");
            w.ShowScreen("Main", true);
            True(buttons.All(x => !x.Hidden) && !menu.Hidden, "show screen");
            Eq("Quit", buttons[1].Caption);
            w.MouseMove(buttons[1].X + 5, buttons[1].Y + 5); w.Frame(Dt);
            w.MouseDown(); w.MouseUp();
            Eq(true, Var(l, "quit"));
            w.Load(M("when start\n    hide screen \"Main\"\n    s = screen_shown(\"Main\")", "Hider"));
            var h = w.Spawn("h", 0, 0, ["Hider"]);
            w.Frame(Dt);
            True(buttons.All(x => x.Hidden), "hide screen");
            Eq(false, Var(h, "s"));
            NoErrors(w);
        });
        test("hide / show, color, caption with live values, tags, self.hidden", () =>
        {
            var w = NewWorld(M("when start\n    hide\n    h = self.hidden\n    tag self \"in_hand\"\n    n = count(\"in_hand\")\n    untag self \"in_hand\"\n    m = count(\"in_hand\")\n    caption \"HP {hp}\" with \"#FFFFFF\", 20\n    color \"#123456\"\n    self.opacity = 0.5\n    hp = 3"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            Eq(true, o.Hidden); Eq(true, Var(o, "h")); Eq(1.0, Var(o, "n")); Eq(0.0, Var(o, "m"));
            Eq("HP 3", o.Caption); Eq(20.0, o.CaptionSize); Eq("#123456", o.Color); Eq(0.5, o.Opacity);
            o.Mechanisms[0].Vars["hp"] = 2.0;
            w.Frame(Dt);
            Eq("HP 2", o.Caption, "updates by itself");
            NoErrors(w);
        });
        test("security cameras: camera view jumps and fits the box, static noise, on_screen", () =>
        {
            var w = NewWorld(M("when start\n    camera view \"Cam 2\"\n    screen noise 0.3\n    seen = on_screen(find(\"Ghost\"))\n    hidden_seen = on_screen(find(\"Far\"))", "Watcher"));
            w.ResizeScene(3000, 1000);
            var cam = w.Spawn("Cam 2", 2000, 400, ["CameraPoint"]);
            cam.Width = 400; cam.Height = 225;
            w.Spawn("Ghost", 2100, 500, null);
            w.Spawn("Far", 100, 100, null);
            var watcher = w.Spawn("w", 0, 0, ["Watcher"]);
            w.Frame(Dt);
            Near(2200, w.Camera.X, 1e-6, "camera x"); Near(512.5, w.Camera.Y, 1e-6, "camera y");
            Near(2, w.Camera.Zoom, 1e-6, "zoom fits the box");
            Eq(0.3, w.NoiseAmount);
            Eq(true, Var(watcher, "seen")); Eq(false, Var(watcher, "hidden_seen"));
            Eq("Cam 2", w.Functions["camera_view"]([]));
            True(!cam.Shown, "a camera point isn't drawn");
        });
        test("CameraMonitor switches cameras with keys and shows the Cameras screen", () =>
        {
            var w = NewWorld();
            w.ResizeScene(3000, 1000);
            var c1 = w.Spawn("Cam 1", 0, 0, ["CameraPoint"]); c1.Width = 800; c1.Height = 450;
            var c2 = w.Spawn("Cam 2", 1600, 400, ["CameraPoint"]); c2.Width = 800; c2.Height = 450;
            w.Spawn("Monitor", 0, 0, ["CameraMonitor"], new() { ["CameraMonitor"] = new() { ["cameras"] = new List<object?> { "Cam 1", "Cam 2" } } });
            var frame = w.Spawn("frame", 0, 0, ["Panel"], new() { ["Panel"] = new() { ["screen"] = "Cameras" } });
            w.Frame(Dt);
            True(frame.Hidden, "the Cameras screen starts hidden");
            w.KeyDown("tab"); w.Frame(Dt); w.KeyUp("tab");
            Eq(c1, w.CameraView); True(!frame.Hidden, "watching shows the screen");
            w.KeyDown("d2"); w.Frame(Dt); w.KeyUp("d2");
            Eq(c2, w.CameraView, "the 2 key (d2 from the keyboard)");
            w.KeyDown("tab"); w.Frame(Dt); w.KeyUp("tab");
            Eq(null, w.CameraView); True(frame.Hidden, "closed");
            NoErrors(w);
        });

        Console.WriteLine("Game kit: inventory and crafting");
        test("give, take, has, count_item, items; the hotbar picks with keys and the wheel", () =>
        {
            var w = NewWorld(M("when start\n    give \"wood\", 3\n    give \"stone\"\n    give \"wood\", 2\n    take \"wood\", 4\n    t1 = taken\n    take \"stone\", 5\n    t2 = taken\n    a = has(\"wood\", 1)\n    b = has(\"wood\", 2)\n    n = count_item(\"stone\")\n    all_items = items()\n    show hotbar 5"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            NoErrors(w);
            Eq(true, Var(o, "t1")); Eq(false, Var(o, "t2"));
            Eq(true, Var(o, "a")); Eq(false, Var(o, "b")); Eq(1.0, Var(o, "n"));
            Eq("[\"wood\", \"stone\"]", World.ToText(Var(o, "all_items")));
            True(w.Hotbar.Visible && w.Hotbar.Slots == 5, "hotbar");
            Eq("wood", w.SelectedItem);
            w.KeyDown("d2"); w.KeyUp("d2");
            Eq("stone", w.SelectedItem);
            w.MouseWheel(-1);
            Eq(2, w.Hotbar.Selected); Eq("", w.SelectedItem, "an empty slot");
            w.MouseWheel(1); w.MouseWheel(1); w.MouseWheel(1);
            Eq(4, w.Hotbar.Selected, "wraps around");
            w.Take("wood", 1);
            Eq("stone", w.Inventory[0].Name, "an empty stack leaves");
        });
        test("Pickup, Recipe and craft; the inventory goes to the next scene and into saves", () =>
        {
            var w = NewWorld();
            w.Spawn("Player", 100, 100, null, null, "player");
            for (int i = 0; i < 3; i++) w.Spawn("log", 100, 100, ["Pickup"], new() { ["Pickup"] = new() { ["item"] = "wood", ["amount"] = 2.0 } });
            w.Spawn("r", 0, 0, ["Recipe"], new() { ["Recipe"] = new() { ["makes"] = "torch", ["amount"] = 4.0, ["need1"] = "wood", ["count1"] = 1.0, ["need2"] = "coal", ["count2"] = 1.0 } });
            w.Spawn("r2", 0, 0, ["Recipe"], new() { ["Recipe"] = new() { ["makes"] = "table", ["amount"] = 1.0, ["need1"] = "wood", ["count1"] = 4.0, ["need2"] = "" } });
            w.Frame(Dt); w.Frame(Dt);
            Eq(6, w.CountItem("wood"));
            Eq(2, w.Recipes.Count);
            Eq("torch ×4: wood 1, coal 1", w.FindRecipe("torch")!.Text);
            True(!w.CanCraft("torch") && w.CanCraft("table"), "can_craft");
            True(!w.Craft("torch"), "missing coal");
            Eq(6, w.CountItem("wood"), "nothing taken when it can't");
            w.Give("coal", 1);
            True(w.Craft("torch"), "craft");
            Eq(5, w.CountItem("wood")); Eq(0, w.CountItem("coal")); Eq(4, w.CountItem("torch"));
            var next = w.ForNextScene();
            Eq(4, next.CountItem("torch"), "next scene");
            Eq(2, next.Recipes.Count);
            w.SaveGame("kit");
            w.Take("torch", 4);
            True(w.LoadGame("kit"), "load");
            Eq(4, w.CountItem("torch"), "saved and loaded");
            w.ClearSave("kit");
        });
        test("Crafting menu: C opens one button per recipe; clicking crafts", () =>
        {
            var w = NewWorld();
            var menu = w.Spawn("Crafting", 200, 50, ["Crafting"]);
            menu.Width = 300; menu.Height = 200;
            w.Load(M("when start\n    recipe \"plank\" from \"wood\", 1 as 2\n    recipe \"door\" from \"plank\", 6\n    give \"wood\", 2", "Setup"));
            w.Spawn("s", 0, 0, ["Setup"]);
            w.Frame(Dt);
            True(menu.Hidden, "closed at first");
            w.KeyDown("c"); w.Frame(Dt); w.KeyUp("c");
            True(!menu.Hidden, "open");
            w.Frame(Dt);
            var buttons = w.Objects.Where(o => o.Name == "Button" && !o.Destroyed).ToList();
            Eq(2, buttons.Count);
            Eq("#6FBF73", buttons[0].Color); Eq("#5A5A5A", buttons[1].Color);
            w.MouseMove(buttons[0].X + 5, buttons[0].Y + 5); w.Frame(Dt);
            w.MouseDown(); w.MouseUp(); w.Frame(Dt);
            Eq(2, w.CountItem("plank")); Eq(1, w.CountItem("wood"));
            NoErrors(w);
        });

        Console.WriteLine("Game kit: cards");
        test("shuffle, pop and arrange in a row, a fan and a grid", () =>
        {
            var w = NewWorld(M("when start\n    random_seed 1\n    deck = range(1, 20)\n    shuffle deck\n    n = length(deck)\n    s = 0\n    for each v in deck\n        s += v\n    last = deck[-1]\n    top = pop(deck)\n    first = pop(deck, 0)\n    left = length(deck)\n    arrange all(\"card\") in row at 400, 300 gap 10\n    e = pop([])"));
            for (int i = 0; i < 4; i++) { var c = w.Spawn("c" + i, 0, 0, null, null, "card"); c.Width = 50; c.Height = 70; }
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            NoErrors(w);
            Eq(20.0, Var(o, "n")); Eq(210.0, Var(o, "s"), "same numbers");
            Eq(Var(o, "last"), Var(o, "top")); Eq(18.0, Var(o, "left"));
            Eq(null, Var(o, "e"));
            var cards = w.Objects.Where(x => x.Is("card")).ToList();
            Eq("285 345 405 465", string.Join(" ", cards.Select(c => c.X)), "row centred on 400");
            True(cards.All(c => c.Y == 265), "centred on y");
            w.Load(M("when start\n    arrange all(\"card\") in grid at 400, 300 gap 0 columns 2 over 0.2\n    done = true", "G"));
            var g = w.Spawn("g", 0, 0, ["G"]);
            w.Frame(Dt);
            Eq(null, Var(g, "done"), "'over' waits");
            w.RunFor(0.3);
            Eq(true, Var(g, "done"));
            Eq("350 400 350 400", string.Join(" ", cards.Select(c => c.X)), "grid x");
            Eq("230 230 300 300", string.Join(" ", cards.Select(c => c.Y)), "grid y");
            True(new string(World.ToText(Var(o, "first")).Where(char.IsDigit).ToArray()).Length > 0, "pop(…, 0) gives an item");
        });
        test("Deck deals Cards into the hand; a Card flips; TurnManager passes turns", () =>
        {
            var w = NewWorld(M("when told \"enemy turn\" r\n    enemy = true\nwhen told \"player turn\" r\n    rounds = r", "Ears"));
            var ears = w.Spawn("ears", 0, 0, ["Ears"]);
            var deck = w.Spawn("Deck", 700, 300, ["Deck"]);
            deck.Width = 80; deck.Height = 112;
            var turns = w.Spawn("Turns", 0, 0, ["TurnManager"]);
            w.Frame(Dt);
            Eq(1.0, Var(ears, "rounds"), "player turn 1 at the start");
            w.Tell(deck, "draw", [3.0]);
            w.RunFor(0.4);
            var hand = w.Objects.Where(o => o.Is("in_hand")).ToList();
            Eq(3, hand.Count);
            True(hand.All(c => c.Ui && c.Draggable && c.DropZone == "slot" && c.Caption!.Contains('\n')), "cards on the screen, draggable, with title and value");
            Eq("268 360 452", string.Join(" ", hand.Select(c => Math.Round(c.X, 3))), "a row centred on the hand");
            Eq("5", deck.Caption, "cards left");
            var card = hand[0];
            w.Tell(card, "flip", []);
            w.RunFor(0.3);
            Eq(false, card.Mechanisms[0].Vars["face_up"]);
            Eq("#5B3FA0", card.Color); Eq(null, card.Caption);
            Near(80, card.Width, 0.01, "back to its width");
            w.MouseMove(deck.X + 5, deck.Y + 5);
            w.MouseDown(); w.MouseUp();
            w.RunFor(0.4);
            Eq(4, w.Objects.Count(o => o.Is("in_hand")), "clicking the deck draws one");
            w.Tell(turns, "end turn", []);
            Eq(true, Var(ears, "enemy"));
            w.Tell(turns, "end turn", []);
            Eq(2.0, Var(ears, "rounds"));
            NoErrors(w);
        });

        Console.WriteLine("Game kit: sounds and keys");
        test("looping sounds, stop sound, volume", () =>
        {
            var w = NewWorld(M("when start\n    play \"coin.wav\"\n    play \"rain.wav\" loop with 0.5\n    p = playing(\"rain.wav\")\n    volume 0.5\n    stop sound \"rain.wav\"\n    q = playing(\"rain.wav\")\n    play \"music.mp3\" loop\n    stop sounds"));
            var reqs = new List<SoundRequest>();
            var shots = new List<string>();
            w.SoundControl += reqs.Add;
            w.SoundPlayed += shots.Add;
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            NoErrors(w);
            Eq("coin.wav", string.Join(",", shots));
            Eq(true, Var(o, "p")); Eq(false, Var(o, "q"));
            True(reqs[0] is { Name: "rain.wav", Loop: true, Volume: 0.5 }, "loop with volume");
            True(reqs.Any(r => r is { Name: "rain.wav", VolumeOnly: true, Volume: 0.25 }), "the master volume reaches the loop");
            True(reqs.Any(r => r is { Name: "rain.wav", Stop: true }), "stop sound");
            True(reqs.Last() is { Name: "music.mp3", Stop: true }, "stop sounds");
            Eq(0, w.Loops.Count);
            True(w.Output.Contains("♪ coin.wav"), "the console still shows sounds");
        });
        test("number keys: 'when key \"1\"' fires for the keyboard's d1 and numpad1", () =>
        {
            var w = NewWorld(M("when key \"1\"\n    ones += 1"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            w.KeyDown("d1"); w.KeyUp("d1");
            w.KeyDown("NumPad1"); w.KeyUp("NumPad1");
            Eq(2.0, Var(o, "ones"));
        });

        Console.WriteLine("Game kit: performance");
        test("the broadphase finds the same touches as checking every pair", () =>
        {
            var w = NewWorld(M("when touched by other\n    n += 1", "Toucher"));
            var rng = new Random(9);
            for (int i = 0; i < 400; i++)
            {
                var o = w.Spawn("o" + i, rng.Next(0, 1500), rng.Next(0, 900), ["Toucher"]);
                o.Width = rng.Next(4, 90); o.Height = rng.Next(4, 90);
                if (i % 7 == 0) o.Solid = true;
                if (i % 50 == 0) { o.Width = 1600; o.Height = 6; }
            }
            w.Step(Dt);
            var brute = w.ContactsBruteForce().ToHashSet();
            var fast = w.TouchingPairs.ToHashSet();
            True(brute.Count > 100, $"enough contacts to compare ({brute.Count})");
            True(brute.SetEquals(fast), $"same pairs (brute {brute.Count}, grid {fast.Count})");
            long fired = w.Objects.Sum(o => (long)(double)(Var(o, "n") ?? 0.0));
            Eq((long)brute.Count * 2, fired, "each new touch fires on both");
        });
        test("touches still fire in the old order (coins picked up left to right)", () =>
        {
            var w = NewWorld(M("when touched by \"coin\"\n    order = order + other.name", "Picker"));
            var p = w.Spawn("p", 0, 0, ["Picker"], null, "player");
            p.Width = 300; p.Height = 50;
            for (int i = 0; i < 5; i++) w.Spawn("c" + i, 250 - i * 50, 10, null, null, "coin");
            w.Step(Dt);
            Eq("c0c1c2c3c4", Var(p, "order"));
        });
        test("2000 objects and a 400 × 200 tile map step at 60 fps", () =>
        {
            var w = NewWorld(M("when tick\n    self.x += speed * dt\n    if self.x > 6000: self.x = 0", "Mover") + M("when touched by \"player\"\n    hits += 1", "Spike"));
            w.Load(M("when start\n    tile_kind \"dirt\" with \"#8B5A2B\"\n    make_map 400, 200 with 16\n    fill_tiles \"dirt\" from 0, 2800 to 6399, 3199", "BigMap"));
            w.Spawn("map", 0, 0, ["BigMap"]);
            var rng = new Random(1);
            for (int i = 0; i < 2000; i++)
            {
                GameObject o = (i % 4) switch
                {
                    0 => w.Spawn("m" + i, rng.Next(0, 6000), rng.Next(0, 2700), ["Mover"], new() { ["Mover"] = new() { ["speed"] = 60.0 } }),
                    1 => w.Spawn("b" + i, rng.Next(0, 6300), rng.Next(0, 2600), ["Body"]),
                    2 => w.Spawn("s" + i, rng.Next(0, 6300), rng.Next(0, 2790), ["Spike"]),
                    _ => w.Spawn("w" + i, rng.Next(0, 6300), rng.Next(0, 2790), ["Solid"]),
                };
                o.Width = 16; o.Height = 16;
            }
            for (int i = 0; i < 20; i++) w.Spawn("Player", rng.Next(0, 6000), 100, ["Body"], null, "player");
            w.Frame(Dt); w.Frame(Dt); // join, start
            for (int i = 0; i < 30; i++) w.Frame(Dt); // warm up
            var sw = Stopwatch.StartNew();
            const int frames = 120;
            for (int i = 0; i < frames; i++) w.Frame(Dt);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / frames;
            Console.WriteLine($"         ({ms:0.00} ms per frame for {w.Objects.Count} objects)");
            NoErrors(w);
            True(ms < 12, $"a frame took {ms:0.00} ms (budget 12 ms; 16.7 ms is one frame at 60 fps)");
            True(w.Objects.Where(o => o.Name.StartsWith('b')).All(o => o.Y <= 2800 - 16 + 0.01), "bodies rest on the tiles");
        });
    }
}
