using BuilderEngine.Script;

/// <summary>Engine features (tweens, camera, dialogue, screen, controls, HUD, pictures, saves). Called from Program.cs.</summary>
static class EngineTests
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

    static string M(string body) => "mechanism T\n" + string.Join("\n", body.Split('\n').Select(l => "    " + l)) + "\n";

    static object? Var(GameObject o, string name) => o.Mechanisms[0].Vars.GetValueOrDefault(name);

    static void Press(World w, string key)
    {
        w.KeyDown(key);
        w.Frame(Dt);
        w.KeyUp(key);
    }

    static void InTemp(Action<string> body)
    {
        string d = Path.Combine(Path.GetTempPath(), "BE_Engine_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        try { body(d); }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    /// <summary>The first 24 bytes of a PNG: enough for <see cref="Images.Size"/> and the frame lists.</summary>
    static void FakePng(string path, int w, int h)
    {
        var b = new byte[33];
        byte[] sig = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        sig.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, b);
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Engine: tweens");
        test("easings start at 0, end at 1, and only go up", () =>
        {
            foreach (var e in World.Easings.Append((string?)null))
            {
                Eq(0.0, World.Ease(e, 0), $"{e} at 0");
                Eq(1.0, World.Ease(e, 1), $"{e} at 1");
                double last = 0;
                for (int i = 1; i <= 20; i++) { double v = World.Ease(e, i / 20.0); True(v >= last - 1e-12, $"{e} goes up"); last = v; }
            }
            Near(0.5, World.Ease("inout", 0.5), 1e-9, "inout half way");
            True(World.Ease("in", 0.5) < 0.5 && World.Ease("out", 0.5) > 0.5, "in starts slow, out starts fast");
        });
        test("move to ... in 1: ends exactly there, and the 'when' waits for it", () =>
        {
            var w = NewWorld(M("when start\n    move to 100, 50 in 1 ease linear\n    done = true"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.RunFor(0.5);
            Near(50, o.X, 2, "half way (linear)");
            Eq(null, Var(o, "done"), "still moving");
            w.RunFor(0.55);
            Eq(100.0, o.X); Eq(50.0, o.Y);
            Eq(true, Var(o, "done"), "went on after the tween");
        });
        test("move another object by, resize, fade; the old 'move dx, dy' is instant", () =>
        {
            var w = NewWorld(M("when start\n    move 5, 0\n    step = 1\n    move Boss by 0, -40 in 0.5 ease out\n    resize Boss to 10, 20 in 0.5\n    fade Boss to 0.25 in 0.5\n    fade Boss to 1\n    done = true"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            var boss = w.Spawn("Boss", 300, 300);
            boss.Width = 40; boss.Height = 40;
            w.Frame(Dt);
            Eq(5.0, o.X, "old move is instant");
            w.RunFor(0.25);
            True(boss.Y < 300 - 20, $"ease out: more than half way after half the time (y {boss.Y})");
            w.RunFor(1.6);
            Eq(260.0, boss.Y); Eq(10.0, boss.Width); Eq(20.0, boss.Height);
            Eq(1.0, boss.Opacity, "faded back at once");
            Eq(true, Var(o, "done"));
        });
        test("a newer move takes over an object; a missing object is a friendly error", () =>
        {
            var w = NewWorld("mechanism A\n    when start\n        move Boss to 100, 0 in 1\n        a = true\nmechanism B\n    when start\n        wait 0.2\n        move Boss to 0, 200 in 0.5\nmechanism C\n    when start\n        move Nobody to 1, 1 in 1\n");
            var boss = w.Spawn("Boss", 0, 0);
            var a = w.Spawn("a", 0, 0, ["A"]);
            w.Spawn("b", 0, 0, ["B"]);
            w.Spawn("c", 0, 0, ["C"]);
            w.RunFor(1.2);
            Eq(0.0, boss.X, "the second move won"); Eq(200.0, boss.Y);
            Eq(true, Var(a, "a"), "the first line finished anyway");
            True(w.Errors.Any(e => e.Contains("no object called 'Nobody'")), string.Join(" | ", w.Errors));
        });

        Console.WriteLine("Engine: camera");
        test("camera clamp math", () =>
        {
            Eq(400.0, Camera.ClampAxis(100, 800, 0, 1600), "left edge");
            Eq(1200.0, Camera.ClampAxis(1500, 800, 0, 1600), "right edge");
            Eq(700.0, Camera.ClampAxis(700, 800, 0, 1600), "free in the middle");
            Eq(300.0, Camera.ClampAxis(50, 800, 0, 600), "a view bigger than the limits is centred");
        });
        test("camera follows the player by default, inside the scene; follow / move / zoom / limits / reset", () =>
        {
            var s = new Scene { Width = 1600, Height = 450 };
            s.Add(new SceneObject { Name = "P", X = 1500, Y = 300, Width = 32, Height = 48, Tags = ["player"] });
            s.Add(new SceneObject { Name = "Boss", X = 700, Y = 300, Width = 40, Height = 40 });
            var w = NewWorld("mechanism Cam\n    when told \"follow\"\n        camera follow Boss\n    when told \"move\"\n        camera move to 500, 225 in 1\n        moved = true\n    when told \"zoom\"\n        camera zoom 2 in 0.5\n    when told \"limits\"\n        camera limits 0, 0, 1000, 450\n    when told \"reset\"\n        camera reset\n");
            w.LoadScene(s);
            var cam = w.Camera;
            var ctl = w.Spawn("ctl", 0, 0, ["Cam"]);
            w.Frame(Dt);
            Eq(800.0, cam.ViewWidth); Eq(1200.0, cam.X, "player at the right edge: clamped"); Eq(225.0, cam.Y);
            w.Tell(ctl, "follow", []);
            w.RunFor(2);
            Near(720, cam.X, 1, "follows Boss (smoothly)");
            w.Tell(ctl, "move", []);
            w.RunFor(0.5);
            Eq(null, ctl.Mechanisms[0].Vars.GetValueOrDefault("moved"), "camera move waits");
            w.RunFor(0.6);
            Eq(500.0, cam.X); Eq(true, ctl.Mechanisms[0].Vars["moved"]);
            w.Tell(ctl, "zoom", []);
            w.RunFor(0.6);
            Eq(2.0, cam.Zoom); Eq(400.0, cam.ViewWidth);
            w.Tell(ctl, "limits", []);
            w.Tell(ctl, "reset", []);
            w.Frame(Dt);
            Eq(1.0, cam.Zoom, "reset zoom"); Eq(true, cam.Auto, "reset follows the player");
            w.RunFor(2);
            Near(1200, cam.X, 1, "back on the player, scene limits");
        });
        test("camera shake moves the view, then stops", () =>
        {
            var w = NewWorld(M("when start\n    camera shake 6 for 0.3"));
            w.Spawn("o", 0, 0, ["T"]);
            bool moved = false;
            for (int i = 0; i < 10; i++) { w.Frame(Dt); moved |= w.Camera.ShakeX != 0 || w.Camera.ShakeY != 0; }
            True(moved, "shook");
            w.RunFor(0.4);
            Eq(0.0, w.Camera.ShakeX); Eq(0.0, w.Camera.ShakeY);
        });

        Console.WriteLine("Engine: dialogue");
        test("say types out and waits for a key (the first press finishes the typing)", () =>
        {
            var w = NewWorld(M("when start\n    say Boss \"You came...\"\n    done = true"));
            w.Spawn("Boss", 0, 0);
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            var d = w.Dialogue!;
            Eq("Boss", d.Speaker); True(!d.FullyTyped, "typing");
            Press(w, "space");
            True(d.FullyTyped && !d.Closed, "first press: all the text, still open");
            w.RunFor(3);
            Eq(null, Var(o, "done"), "waits as long as it takes");
            Press(w, "right");
            Eq(null, Var(o, "done"), "arrows don't skip dialogue");
            Press(w, "enter");
            w.Frame(Dt);
            Eq(true, Var(o, "done")); Eq(null, w.Dialogue);
        });
        test("say ... for 1 closes by itself; narrate has no name; {var} is filled in", () =>
        {
            var w = NewWorld(M("when start\n    coins = 3\n    narrate \"You have {coins} coins\" for 0.5\n    done = true"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            Eq("", w.Dialogue!.Speaker); Eq("You have 3 coins", w.Dialogue.Text);
            w.RunFor(2);
            Eq(true, Var(o, "done"));
        });
        test("ask waits and sets 'answer' (arrows + enter, a number key, a click)", () =>
        {
            var w = NewWorld(M("when told \"go\"\n    ask \"Join me?\" \"Yes\", \"No\", \"Maybe\"\n    got = answer"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            w.Tell(o, "go", []);
            w.RunFor(2);
            Eq(3, w.Dialogue!.Options!.Count);
            Press(w, "down");
            Press(w, "enter");
            w.Frame(Dt);
            Eq("No", Var(o, "got"));
            w.Tell(o, "go", []); w.RunFor(2);
            Press(w, "d3"); w.Frame(Dt);
            Eq("Maybe", Var(o, "got"), "number key");
            w.Tell(o, "go", []); w.RunFor(2);
            w.Click(0); w.Frame(Dt);
            Eq("Yes", Var(o, "got"), "click");
        });
        test("notify shows a message that goes away", () =>
        {
            var w = NewWorld(M("when start\n    notify \"Saved!\""));
            w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            Eq("Saved!", w.Toasts.Single().Text);
            w.RunFor(3);
            Eq(0, w.Toasts.Count);
        });

        Console.WriteLine("Engine: controls, time, screen");
        test("lock controls: key() is false and 'when key' doesn't fire; unlock brings held keys back", () =>
        {
            var w = NewWorld(M("when tick\n    left = key(\"left\")\nwhen key \"space\"\n    jumps += 1\nwhen told \"lock\"\n    lock controls\nwhen told \"unlock\"\n    unlock controls"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            w.KeyDown("left"); w.Frame(Dt);
            Eq(true, Var(o, "left"), "unlocked: key works");
            w.Tell(o, "lock", []);
            w.Frame(Dt);
            Eq(false, Var(o, "left"), "locked: key() false");
            Press(w, "space");
            Eq(null, Var(o, "jumps"), "locked: no key event");
            w.Tell(o, "unlock", []);
            w.Frame(Dt);
            Eq(true, Var(o, "left"), "still held after unlocking");
            Press(w, "space");
            Eq(1.0, Var(o, "jumps"));
        });
        test("slow motion scales dt (for real seconds); pause stops the clock", () =>
        {
            var w = NewWorld(M("when told \"slow\"\n    slow motion 0.5 for 1\nwhen told \"pause\"\n    pause game\nwhen told \"resume\"\n    resume game"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            w.Tell(o, "slow", []);
            w.Frame(Dt);
            Near(Dt / 2, w.LastDt, 1e-12, "half speed");
            w.RunFor(1.05);
            Eq(1.0, w.TimeScale, "back to normal after 1 real second");
            w.Tell(o, "pause", []);
            double t = w.Time;
            w.RunFor(0.5);
            Eq(t, w.Time, "paused");
            w.Tell(o, "resume", []);
            w.RunFor(0.5);
            True(w.Time > t + 0.4, "running again");
        });
        test("fade out / fade in wait and cover the screen; flash fades by itself", () =>
        {
            var w = NewWorld(M("when start\n    fade out in 0.5\n    dark = true\n    fade in in 0.5\n    flash \"red\"\n    done = true"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.RunFor(0.3);
            True(w.FadeAmount > 0.3 && w.FadeAmount < 1, $"fading ({w.FadeAmount})");
            w.RunFor(0.3);
            Eq(true, Var(o, "dark"));
            w.RunFor(0.6);
            Eq(0.0, w.FadeAmount); Eq(true, Var(o, "done"));
            Eq("red", w.FlashColor);
            w.RunFor(0.5);
            Eq(0.0, w.FlashAmount);
        });

        Console.WriteLine("Engine: HUD");
        test("show text fills {name} every frame; show bar follows its values; hide", () =>
        {
            var w = NewWorld(M("param hp: number = 3\nwhen start\n    show text \"Score: {score}\" at 10, 20 with \"#FFD700\", 24\n    show bar hp of 4 at 10, 50\nwhen told \"hit\"\n    hp -= 1\nwhen told \"hide\"\n    hide text \"Score: {score}\"\n    hide bar hp"));
            w.Globals["score"] = 0.0;
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            var text = w.Hud.Single(h => !h.IsBar);
            Eq("Score: 0", text.Text); Eq(24.0, text.Size); Eq("#FFD700", text.Color); Eq(10.0, text.X);
            w.Globals["score"] = 7.0;
            w.Frame(Dt);
            Eq("Score: 7", text.Text);
            var bar = w.Hud.Single(h => h.IsBar);
            Near(0.75, bar.Fraction, 1e-9, "3 of 4");
            w.Tell(o, "hit", []); w.Frame(Dt);
            Near(0.5, bar.Fraction, 1e-9, "2 of 4");
            w.Tell(o, "hide", []);
            Eq(0, w.Hud.Count);
        });
        test("starter Label and HealthBar (above the object, gone with it)", () =>
        {
            var w = NewWorld();
            w.Globals["score"] = 2.0;
            var label = w.Spawn("Label", 30, 40, ["Label"]);
            var enemy = w.Spawn("Enemy", 100, 100, ["Health", "HealthBar"]);
            w.Frame(Dt);
            var t = w.Hud.Single(h => !h.IsBar);
            Eq("Score: 2", t.Text); Eq(30.0, t.X); Eq(40.0, t.Y); Eq(null, t.Anchor);
            Eq(false, label.Mechanisms[0].Vars["visible"], "the label object itself is hidden");
            var bar = w.Hud.Single(h => h.IsBar);
            Eq(enemy, bar.Anchor); Eq(1.0, bar.Fraction);
            w.Tell(enemy, "damage", [1.0]); w.Frame(Dt);
            Near(2.0 / 3, bar.Fraction, 1e-9, "hurt");
            w.Load("mechanism Kill\n    when told \"die\"\n        destroy self\n");
            var doomed = w.Spawn("Doomed", 0, 0, ["Health", "HealthBar", "Kill"]);
            w.Frame(Dt);
            Eq(2, w.Hud.Count(h => h.IsBar));
            w.Tell(doomed, "die", []);
            w.Frame(Dt);
            Eq(1, w.Hud.Count(h => h.IsBar), "a destroyed object's bar goes away");
        });

        Console.WriteLine("Engine: pictures");
        test("Image params convert to text; an object's Picture saves and loads", () =>
        {
            Eq("hero.png", World.ConvertParam("hero.png", "Image"));
            Eq("3", World.ConvertParam(3.0, "picture"));
            var s = new Scene();
            s.Add(new SceneObject { Name = "Hero", Image = "hero.png" });
            var back = Scene.FromJson(s.ToJson());
            Eq("hero.png", back.Objects[0].Image);
            Eq("", Scene.FromJson("""{ "objects": [ { "name": "Old" } ] }""").Objects[0].Image, "older files: no picture");
            var w = NewWorld();
            var map = w.LoadScene(back);
            Eq("hero.png", map.Keys.First().Image, "the playing object starts with its picture");
        });
        test("look, face, layer; animate numbered pictures and strips; picture sizes from the file", () => InTemp(dir =>
        {
            string img = Images.Folder(dir);
            FakePng(Path.Combine(img, "walk1.png"), 16, 16);
            FakePng(Path.Combine(img, "walk2.png"), 16, 16);
            FakePng(Path.Combine(img, "walk10.png"), 16, 16);
            FakePng(Path.Combine(img, "run.png"), 64, 16);
            FakePng(Path.Combine(img, "hero.png"), 16, 16);
            Eq((64, 16), Images.Size(Path.Combine(img, "run.png"))!.Value);
            Eq("walk1.png,walk2.png,walk10.png", string.Join(",", Images.Frames(dir, "walk")), "numbered frames in order");
            Eq(Path.Combine(img, "hero.png"), Images.Resolve(dir, "hero"), "the extension may be left out");
            var w = NewWorld(M("when told \"look\"\n    look \"hero.png\"\n    face left\n    layer 2\nwhen told \"walk\"\n    animate \"walk\" at 10\nwhen told \"run\"\n    animate \"run\"\nwhen told \"once\"\n    animate \"walk\" once\n    done = true\nwhen told \"none\"\n    look none"));
            w.ProjectFolder = dir;
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            w.Tell(o, "look", []);
            Eq("hero.png", o.Image); Eq(true, o.FlipX); Eq(2.0, o.Z);
            w.Tell(o, "walk", []);
            Eq("walk1.png", o.Image);
            w.RunFor(0.15);
            Eq("walk2.png", o.Image, "10 frames a second");
            w.Tell(o, "run", []);
            Eq("run.png", o.Image); Eq(4, o.SheetFrames, "a 64×16 strip = 4 square frames");
            w.RunFor(0.3);
            Eq(2, o.SheetFrame);
            w.Tell(o, "once", []);
            w.RunFor(0.2);
            Eq(null, Var(o, "done"), "'once' waits");
            w.RunFor(0.3);
            Eq(true, Var(o, "done")); Eq("walk10.png", o.Image, "stays on the last frame");
            w.Tell(o, "none", []);
            Eq(null, o.Image);
            w.Load(M("when start\n    look \"nope.png\"").Replace("mechanism T", "mechanism U"));
            w.Spawn("u", 0, 0, ["U"]); w.Frame(Dt);
            True(w.Output.Any(l => l.Contains("no picture “nope.png”")), "a missing picture is a warning: " + string.Join(" | ", w.Output));
            Eq(0, w.Errors.Count, "…not an error");
        }));
        test("face an object turns towards it", () =>
        {
            var w = NewWorld(M("when start\n    face Boss"));
            w.Spawn("Boss", -100, 0);
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            Eq(true, o.FlipX);
        });

        Console.WriteLine("Engine: saves");
        test("save game / load game round trip; save \"key\" and saved(\"key\") across worlds", () => InTemp(dir =>
        {
            var w = NewWorld(M("when told \"save\"\n    save game\n    save \"coins\", 12\nwhen told \"load\"\n    load game\n    ok = loaded\nwhen told \"read\"\n    c = saved(\"coins\")\n    nothing = saved(\"nope\")"));
            w.SaveFolder = dir;
            w.Globals["score"] = 5.0;
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            w.Tell(o, "save", []);
            True(File.Exists(Path.Combine(dir, "save.json")), "a file in the saves folder");
            w.Globals["score"] = 0.0;
            w.Tell(o, "load", []);
            Eq(5.0, w.Globals["score"]); Eq(true, Var(o, "ok"));
            var w2 = NewWorld(M("when told \"read\"\n    c = saved(\"coins\")\n    s = saved(\"score\")\n    nothing = saved(\"nope\")"));
            w2.SaveFolder = dir;
            var o2 = w2.Spawn("o", 0, 0, ["T"]);
            w2.Frame(Dt);
            w2.Tell(o2, "read", []);
            Eq(12.0, Var(o2, "c")); Eq(5.0, Var(o2, "s"), "globals kept by save game"); Eq(null, Var(o2, "nothing"));
            var next = w2.ForNextScene();
            Eq(dir, next.SaveFolder, "the next scene saves to the same place");
            w2.ClearSave();
            Eq(false, File.Exists(Path.Combine(dir, "save.json")));
            Eq(false, w2.LoadGame(), "nothing to load");
        }));

        Console.WriteLine("Engine: demo");
        test("demo cutscene plays to the end headless (keys pressed for each line)", () =>
        {
            var p = Project.CreateDemo();
            var w = NewWorld();
            foreach (var src in p.Mechanisms.Values) w.Load(src);
            w.Globals["score"] = 0.0;
            var level2 = p.Scenes[1];
            w.LoadScene(level2);
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
            w.RunFor(0.5);
            var player = w.FindObject("player")!;
            var boss = w.FindObject("Boss")!;
            w.KeyDown("right");
            int lines = 0;
            bool sawLock = false, sawShake = false, sawFade = false, sawBossCam = false;
            for (int i = 0; i < 60 * 30; i++)
            {
                w.Frame(Dt);
                sawLock |= w.ControlsLocked;
                sawShake |= w.Camera.ShakeLeft > 0;
                sawFade |= w.FadeAmount > 0.9;
                sawBossCam |= w.Camera.Target == boss;
                if (w.Dialogue is { Closed: false } d && d.Text.Length > 0 && i % 30 == 0)
                {
                    if (d.FullyTyped) lines++;
                    Press(w, "space");
                }
                if (sawLock && !w.ControlsLocked) break;
            }
            w.KeyUp("right");
            True(sawLock && sawBossCam && sawShake && sawFade, $"lock {sawLock}, boss camera {sawBossCam}, shake {sawShake}, fade {sawFade}");
            Eq(false, w.ControlsLocked, "controls back at the end");
            Eq(2, lines, "both lines were said");
            Eq(1000.0, boss.X); Eq(340.0, boss.Y);
            Eq(0.0, w.FadeAmount); Eq(1.0, w.Camera.Zoom);
            Eq(player, w.Camera.Target, "camera back on the player");
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
            // Walking back through the zone doesn't play it again.
            var intro = w.FindObject("Intro")!;
            player.X = intro.X - 60; w.RunFor(0.2);
            player.X = intro.X; w.RunFor(0.5);
            Eq(false, w.ControlsLocked, "plays once");
        });
        test("demo Npc talks when the player walks up (and turns to face them)", () =>
        {
            var p = Project.CreateDemo();
            var w = NewWorld();
            w.Globals["score"] = 0.0;
            w.LoadScene(p.Scenes[0]);
            Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));
            var npc = w.FindObject("Old Man")!;
            var player = w.FindObject("player")!;
            w.Frame(Dt);
            player.X = npc.X - 20; player.Y = npc.Y;
            w.RunFor(0.3);
            Eq("Old Man", w.Dialogue?.Speaker); Eq(true, w.ControlsLocked); Eq(true, npc.FlipX, "faces the player on its left");
            for (int i = 0; i < 4; i++) { w.RunFor(0.5); Press(w, "space"); }
            w.RunFor(0.2);
            Eq(null, w.Dialogue); Eq(false, w.ControlsLocked);
            Eq("Score: 0", w.Hud.Single(h => !h.IsBar).Text, "the score label");
        });
    }
}
