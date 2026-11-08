using BuilderEngine.Script;

/// <summary>Prefabs, named animations and the pixel-art editor's model. Called from Program.cs with its Test runner.</summary>
static class PrefabsTests
{
    static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected <{expected}> but got <{actual}>");
    }

    static void True(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    static World NewWorld(string src = "")
    {
        var w = new World();
        StarterKit.LoadInto(w);
        if (src.Length > 0) w.Load(src);
        return w;
    }

    static void Run(World w, double seconds, double dt = 1.0 / 60)
    {
        for (double t = 0; t < seconds - 1e-9; t += dt) w.Frame(dt);
    }

    /// <summary>A project with a Slime object (Patrol speed 80, a picture, tags) saved as a prefab, and two more copies.</summary>
    static (Project P, SceneObject First, SceneObject A, SceneObject B) Slimes()
    {
        var p = Project.CreateEmpty();
        var s = p.Scenes[0];
        var slime = s.Add(new SceneObject
        {
            Name = "Slime", X = 100, Y = 200, Width = 40, Height = 30, Color = "#6FBF73", Image = "slime.png", Tags = ["enemy"],
            Mechanisms = [new SceneMechanism { Name = "Patrol", Params = { ["speed"] = 80.0 } }],
        });
        var pf = PrefabTools.SaveAsPrefab(p, slime, "Slime", "Enemies");
        var a = PrefabTools.Place(pf, s, 300, 200, "Slime 2");
        var b = PrefabTools.Place(pf, s, 500, 200, "Slime 3");
        return (p, slime, a, b);
    }

    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "BE_PrefabTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Prefabs, animations and the sprite editor (Prefabs.cs, World.Prefabs.cs, PixelCanvas.cs)");

        // ---------------- Prefabs: model ----------------

        test("save as prefab: the template keeps everything but the placement; the object becomes its first copy", () =>
        {
            var (p, first, a, _) = Slimes();
            var pf = p.FindPrefab("slime")!;
            Eq("Enemies", pf.Category);
            Eq(0.0, pf.Template.X); Eq(0, pf.Template.Id); Eq(null, pf.Template.Prefab);
            Eq(40.0, pf.Template.Width); Eq("slime.png", pf.Template.Image); Eq("enemy", pf.Template.Tags[0]);
            Eq(80.0, pf.Template.Mechanisms[0].Params["speed"]);
            Eq("Slime", first.Prefab);
            Eq("Slime", a.Prefab); Eq(300.0, a.X); Eq(false, a.PrefabAll);
            Eq(3, PrefabTools.Copies(p, "Slime").Count());
            Eq(0, PrefabTools.Overrides(p, a).Count, "a fresh copy has no own changes");
        });

        test("prefabs and links are saved in project.json and read back; old projects load without them", () =>
        {
            var (p, _, a, _) = Slimes();
            a.PrefabAll = true;
            a.Animations.Add(new SpriteAnimation { Name = "walk", Frames = ["w1.png", "w2.png"], Fps = 6, Loop = false });
            a.DefaultAnimation = "walk";
            string json = p.ToJson();
            True(json.Contains("\"prefabs\""), "prefabs written");
            var back = Project.FromJson(json);
            Eq(1, back.Prefabs.Count);
            Eq("Enemies", back.Prefabs[0].Category);
            Eq(80.0, back.Prefabs[0].Template.Mechanisms[0].Params["speed"]);
            var a2 = back.Scenes[0].Objects.First(o => o.Name == "Slime 2");
            Eq("Slime", a2.Prefab); Eq(true, a2.PrefabAll);
            Eq("walk", a2.DefaultAnimation);
            Eq(2, a2.Animations[0].Frames.Count); Eq(6.0, a2.Animations[0].Fps); Eq(false, a2.Animations[0].Loop);
            var plain = Project.CreateDemo();
            string old = plain.ToJson();
            True(!old.Contains("prefab") && !old.Contains("animations"), "nothing new is written for a project without prefabs or animations");
            Eq(0, Project.FromJson(old).Prefabs.Count);
            // Hand-written / tolerant: bad entries skipped, frames as text, fps as text.
            var hand = Project.FromJson("{\"scenes\":[{\"objects\":[{\"name\":\"H\",\"animations\":[{\"name\":\"run\",\"frames\":\"a.png, b.png\",\"fps\":\"12\"},{\"frames\":[]},5]}]}],\"prefabs\":[{\"name\":\"\"},3,{\"name\":\"Bat\",\"template\":{\"width\":\"20\"}}]}");
            Eq(1, hand.Prefabs.Count); Eq(20.0, hand.Prefabs[0].Template.Width);
            var h = hand.Scenes[0].Objects[0];
            Eq(1, h.Animations.Count); Eq(12.0, h.Animations[0].Fps); Eq("b.png", h.Animations[0].Frames[1]); Eq(true, h.Animations[0].Loop);
        });

        test("apply to all: a ticked copy's change goes to the prefab and every copy that didn't change it itself", () =>
        {
            var (p, first, a, b) = Slimes();
            b.Width = 64; // b's own change (override)
            a.PrefabAll = true;
            first.PrefabAll = true;
            a.Mechanisms[0].Params["speed"] = 120.0;
            a.Width = 50;
            a.X = 333; // where it is stays its own
            int changed = PrefabTools.ApplyToPrefab(p, a);
            var pf = p.FindPrefab("Slime")!;
            Eq(120.0, pf.Template.Mechanisms[0].Params["speed"]);
            Eq(50.0, pf.Template.Width);
            Eq(0.0, pf.Template.X, "the prefab has no position");
            Eq(2, changed);
            Eq(120.0, first.Mechanisms[0].Params["speed"]); Eq(50.0, first.Width); Eq(100.0, first.X, "placement kept");
            Eq(120.0, b.Mechanisms[0].Params["speed"], "b didn't change the speed: it follows");
            Eq(64.0, b.Width, "b changed its width: kept");
            Eq("Slime 3", b.Name);
            Eq(0, PrefabTools.ApplyToPrefab(p, a), "nothing new to apply");
        });

        test("override: an unticked copy's change stays on it; revert brings the prefab back; unlink cuts it loose", () =>
        {
            var (p, first, a, b) = Slimes();
            a.Image = "red_slime.png";
            Eq(0, PrefabTools.ApplyToPrefab(p, first), "first wasn't changed");
            Eq("slime.png", p.FindPrefab("Slime")!.Template.Image);
            Eq("slime.png", b.Image);
            Eq("image", string.Join(",", PrefabTools.Overrides(p, a)));
            Eq("picture", PrefabTools.Describe(PrefabTools.Overrides(p, a)));
            Eq(true, PrefabTools.Revert(p, a));
            Eq("slime.png", a.Image); Eq(300.0, a.X); Eq("Slime 2", a.Name);
            Eq(0, PrefabTools.Overrides(p, a).Count);
            PrefabTools.Unlink(b);
            Eq(null, b.Prefab);
            Eq(false, PrefabTools.Revert(p, b));
            // Editing the prefab itself (the prefab inspector): copies follow except their own changes; locked ones never.
            a.Height = 99;
            first.Locked = true;
            var before = PrefabTools.Shared(p.FindPrefab("Slime")!.Template);
            p.FindPrefab("Slime")!.Template.Height = 10;
            p.FindPrefab("Slime")!.Template.Color = "#E05D5D";
            Eq(1, PrefabTools.PushChange(p, p.FindPrefab("Slime")!, before));
            Eq(99.0, a.Height, "own height kept"); Eq("#E05D5D", a.Color, "colour followed");
            Eq(30.0, first.Height, "locked copy untouched");
        });

        test("rename and delete a prefab: copies follow the name, or become ordinary objects", () =>
        {
            var (p, first, a, _) = Slimes();
            Eq(null, PrefabTools.Rename(p, "Slime", "Blob"));
            Eq("Blob", a.Prefab); Eq("Blob", first.Prefab); Eq("Blob", p.Prefabs[0].Template.Name);
            p.Prefabs.Add(new Prefab { Name = "Bat" });
            True(PrefabTools.Rename(p, "Blob", "bat") != null, "name taken");
            Eq("Bat 2", PrefabTools.UniqueName(p, "Bat"));
            Eq(3, PrefabTools.Delete(p, "Blob"));
            Eq(null, a.Prefab); Eq(1, p.Prefabs.Count);
        });

        // ---------------- Prefabs: playing ----------------

        test("spawn \"Slime\" makes a copy of the prefab (mechanisms, params, size, tags); spawn Coin still spawns the mechanism", () =>
        {
            var (p, _, _, _) = Slimes();
            p.FindPrefab("Slime")!.Template.Animations.Add(new SpriteAnimation { Name = "idle", Frames = ["s1.png", "s2.png"], Fps = 4 });
            var w = NewWorld("mechanism Maker\n    when start\n        spawn \"Slime\" 10, 20\n        spawn Slime 50, 20\n        spawn Coin here\n");
            w.UsePrefabs(p.Prefabs);
            w.Spawn("maker", 0, 0, ["Maker"]);
            Run(w, 0.1);
            var slimes = w.Objects.Where(o => o.Name == "Slime").ToList();
            Eq(2, slimes.Count);
            var s0 = slimes[0];
            Eq(20.0, s0.Y); Eq(40.0, s0.Width); Eq(30.0, s0.Height); Eq(true, s0.Is("enemy")); Eq("Slime", s0.Prefab);
            Eq(80.0, s0.Mechanisms[0].Vars["speed"], "the prefab's param");
            Eq("s1.png", s0.Image, "the default animation started");
            True(w.Objects.Any(o => o.Name == "Coin" && o.Mechanisms[0].Def.Name == "Coin"), "spawn Coin still works");
        });

        test("a prefab wins over a mechanism with the same name; unknown names are friendly errors", () =>
        {
            var p = Project.CreateEmpty();
            p.Prefabs.Add(new Prefab { Name = "Coin", Template = new SceneObject { Width = 77, Mechanisms = [new SceneMechanism { Name = "Blink" }] } });
            var w = NewWorld("mechanism Maker\n    when start\n        spawn Coin 5, 5\n");
            w.UsePrefabs(p.Prefabs);
            w.Spawn("maker", 0, 0, ["Maker"]);
            Run(w, 0.05);
            var coin = w.Objects.First(o => o.Name == "Coin");
            Eq(77.0, coin.Width); Eq("Blink", coin.Mechanisms[0].Def.Name);
            var w2 = NewWorld("mechanism Maker\n    when start\n        spawn \"Ghost\"\n");
            w2.UsePrefabs(p.Prefabs);
            w2.Spawn("maker", 0, 0, ["Maker"]);
            Run(w2, 0.05);
            True(w2.Errors.Any(e => e.Contains("no prefab or mechanism called 'Ghost'")), string.Join(" | ", w2.Errors));
            var next = w.ForNextScene();
            True(next.Prefabs.ContainsKey("coin"), "the next scene's world keeps the prefabs");
        });

        // ---------------- Animations ----------------

        test("animation model round-trip: clone, default animation, find by name", () =>
        {
            var o = new SceneObject { Name = "Hero" };
            Eq(null, o.ResolveDefaultAnimation());
            o.Animations.Add(new SpriteAnimation { Name = "idle", Frames = ["i1.png"] });
            o.Animations.Add(new SpriteAnimation { Name = "walk", Frames = ["w1.png", "w2.png"], Fps = 12 });
            Eq("idle", o.ResolveDefaultAnimation()!.Name, "no default set: the first");
            o.DefaultAnimation = "WALK";
            Eq("walk", o.ResolveDefaultAnimation()!.Name);
            o.DefaultAnimation = "gone";
            Eq("idle", o.ResolveDefaultAnimation()!.Name, "unknown default: the first");
            var c = o.Clone();
            Eq(2, c.Animations.Count); Eq(12.0, c.Animations[1].Fps);
            c.Animations[1].Frames.Add("w3.png");
            Eq(2, o.Animations[1].Frames.Count, "the clone's frames are its own");
            var back = Scene.FromJson(new Scene { Objects = [o] }.ToJson()).Objects[0];
            Eq("gone", back.DefaultAnimation); Eq("w2.png", back.Animations[1].Frames[1]);
        });

        test("runtime: frames advance at the animation's speed, loop, or stop on the last frame (once)", () =>
        {
            var s = new Scene();
            var so = s.Add(new SceneObject { Name = "Hero" });
            so.Animations.Add(new SpriteAnimation { Name = "walk", Frames = ["a.png", "b.png", "c.png"], Fps = 10 });
            so.Animations.Add(new SpriteAnimation { Name = "die", Frames = ["d1.png", "d2.png"], Fps = 10, Loop = false });
            so.DefaultAnimation = "walk";
            var w = NewWorld();
            var hero = w.LoadScene(s).Keys.First();
            Eq("a.png", hero.Image, "default starts on load");
            Run(w, 0.15);
            Eq("b.png", hero.Image);
            Run(w, 0.1);
            Eq("c.png", hero.Image);
            Run(w, 0.1);
            Eq("a.png", hero.Image, "loops");
            var w2 = NewWorld("mechanism Dier\n    when start\n        animate \"die\"\n        done = true\n");
            var s2 = new Scene();
            var d = s2.Add(new SceneObject { Name = "D", Mechanisms = [new SceneMechanism { Name = "Dier" }] });
            d.Animations.Add(so.Animations[1].Clone());
            d.Animations.Add(new SpriteAnimation { Name = "idle", Frames = ["i.png"] });
            d.DefaultAnimation = "idle";
            var dObj = w2.LoadScene(s2).Keys.First();
            Run(w2, 0.02);
            Eq("d1.png", dObj.Image);
            Run(w2, 0.6);
            Eq("d2.png", dObj.Image, "a non-looping animation stays on its last frame");
            // Asked for every tick (like the Animator does), a one-time animation still ends on its last frame.
            var w3 = NewWorld("mechanism TickDier\n    when tick\n        animate \"die\"\n");
            var s3 = new Scene();
            var t3 = s3.Add(new SceneObject { Name = "T", Mechanisms = [new SceneMechanism { Name = "TickDier" }] });
            t3.Animations.Add(so.Animations[1].Clone());
            var tObj = w3.LoadScene(s3).Keys.First();
            Run(w3, 0.6);
            Eq("d2.png", tObj.Image, "not restarted every tick");
        });

        test("runtime: animate \"x\" once waits; 'at' changes the speed; numbered pictures still work without named ones", () =>
        {
            var w = NewWorld("mechanism A\n    when start\n        animate \"hit\" once\n        after = time\n        animate \"walk\" at 20\n");
            var s = new Scene();
            var so = s.Add(new SceneObject { Name = "H", Mechanisms = [new SceneMechanism { Name = "A" }] });
            so.Animations.Add(new SpriteAnimation { Name = "hit", Frames = ["h1.png", "h2.png", "h3.png", "h4.png"], Fps = 10 });
            so.Animations.Add(new SpriteAnimation { Name = "walk", Frames = ["w1.png", "w2.png"], Fps = 2 });
            var h = w.LoadScene(s).Keys.First();
            Run(w, 0.2);
            True(!h.Mechanisms[0].Vars.ContainsKey("after"), "still waiting for 'hit' to finish");
            Run(w, 0.4);
            True(h.Mechanisms[0].Vars.ContainsKey("after"), "went on after the animation");
            Eq(20.0, h.AnimationFps, "'at' wins over the animation's own speed");

            // No named animation and no project folder: numbered/strip animations behave as before (a strip of 4).
            var w2 = NewWorld("mechanism B\n    when start\n        animate \"run.png\" with 4\n");
            var b = w2.Spawn("b", 0, 0, ["B"]);
            Run(w2, 0.3);
            Eq(4, b.SheetFrames); Eq("run.png", b.Image);
            True(b.SheetFrame > 0, "the strip advances");
        });

        test("the Animator plays named animations from how the object moves (fps 0 = each animation's speed)", () =>
        {
            var w = NewWorld();
            var s = new Scene();
            var so = s.Add(new SceneObject { Name = "Hero", Mechanisms = [new SceneMechanism { Name = "Animator" }] });
            so.Animations.Add(new SpriteAnimation { Name = "idle", Frames = ["i1.png", "i2.png"], Fps = 3 });
            so.Animations.Add(new SpriteAnimation { Name = "walk", Frames = ["w1.png", "w2.png"], Fps = 9 });
            var hero = w.LoadScene(s).Keys.First();
            Run(w, 0.05);
            Eq("i1.png", hero.Image);
            Eq(3.0, hero.AnimationFps);
            hero.Vx = 50;
            Run(w, 0.05);
            True(hero.Image is "w1.png" or "w2.png", "walking: " + hero.Image);
            Eq(9.0, hero.AnimationFps);
            Eq(ParamKind.Animation, ParamTypes.Kind(w.Library["Animator"].Params[0].Type));
        });

        // ---------------- Sprite sheets ----------------

        test("sprite sheet slicing: frames row by row, leftovers ignored, a good first guess", () =>
        {
            var r = SpriteSheet.Slice(100, 64, 3, 2);
            Eq(6, r.Count);
            Eq((0, 0, 33, 32), r[0]); Eq((66, 0, 33, 32), r[2]); Eq((33, 32, 33, 32), r[4]);
            Eq(0, SpriteSheet.Slice(4, 4, 8, 1).Count, "frames under one pixel");
            Eq(0, SpriteSheet.Slice(64, 64, 0, 1).Count);
            Eq((4, 1), SpriteSheet.Guess(128, 32));
            Eq((1, 3), SpriteSheet.Guess(16, 48));
            Eq((1, 1), SpriteSheet.Guess(50, 30));
            // Cropping and saving (an empty last cell is skipped).
            int w = 6, h = 2;
            var px = new uint[w * h];
            px[0] = 0xFFFF0000; px[2 * 1 + 0] = 0xFF00FF00; // frame 1 (x 0..1) red, frame 2 (x 2..3) green, frame 3 empty
            var crop = SpriteSheet.Crop(w, px, (2, 0, 2, 2));
            Eq(0xFF00FF00u, crop[0]);
            string dir = TempDir();
            try
            {
                var names = SpriteSheet.SaveFrames(dir, "hero.png", w, h, px, 3, 1);
                Eq("hero_1.png,hero_2.png", string.Join(",", names));
                Eq((2, 2), Images.Size(Path.Combine(Images.Folder(dir), "hero_1.png")));
                Eq("hero_1.png,hero_2.png", string.Join(",", SpriteSheet.SaveFrames(dir, "hero", w, h, px, 3, 1)), "the same frames again reuse their files");
                Eq("sprite.png", SpriteSheet.FreePictureName(dir, "sprite"));
                Eq("hero_1 2.png", SpriteSheet.FreePictureName(dir, "hero_1.png"));
            }
            finally { Directory.Delete(dir, true); }
        });

        // ---------------- Pixel-art editor model ----------------

        test("pixel canvas: pencil, line, fill, picker, eraser", () =>
        {
            var c = new PixelCanvas(8, 8);
            uint red = 0xFFFF0000, blue = 0xFF0000FF;
            c.Pencil(1, 1, red);
            Eq(red, c.Pick(1, 1));
            c.Line(0, 4, 7, 4, red); // a wall across the middle
            Eq(red, c.Get(7, 4));
            Eq(31, c.Fill(0, 0, blue), "fills the top half (but the red dot) and not past the wall");
            Eq(blue, c.Get(7, 0)); Eq(0u, c.Get(7, 7), "below the wall untouched"); Eq(red, c.Get(1, 1), "a different colour isn't filled");
            Eq(0, c.Fill(0, 0, blue), "filling with the same colour does nothing");
            c.Erase(7, 0);
            Eq(0u, c.Get(7, 0));
            c.Pencil(4, 6, red, 3);
            Eq(red, c.Get(3, 5)); Eq(red, c.Get(5, 7));
            Eq(blue, c.UsedColors()[0], "most used first"); Eq(2, c.UsedColors().Count);
            Eq("#FF0000", PixelCanvas.Hex(red)); Eq("#00000000", PixelCanvas.Hex(0));
        });

        test("pixel canvas: undo / redo by stroke, resize keeps the top-left, PNG out", () =>
        {
            var c = new PixelCanvas(4, 4);
            c.BeginStroke();
            c.Pencil(0, 0, 0xFF112233);
            c.Pencil(1, 0, 0xFF112233); // same stroke
            c.BeginStroke();
            c.Fill(3, 3, 0xFFFFFFFF);
            Eq(true, c.CanUndo);
            c.Undo();
            Eq(0u, c.Get(3, 3), "fill undone");
            Eq(0xFF112233u, c.Get(1, 0), "the stroke before stays");
            c.Undo();
            Eq(0u, c.Get(0, 0)); Eq(0u, c.Get(1, 0));
            Eq(false, c.Undo());
            c.Redo();
            Eq(0xFF112233u, c.Get(1, 0));
            c.BeginStroke();
            c.Resize(8, 2);
            Eq(8, c.Width); Eq(2, c.Height); Eq(0xFF112233u, c.Get(1, 0));
            c.Undo();
            Eq(4, c.Width); Eq(4, c.Height);
            c.FlipHorizontal();
            Eq(0xFF112233u, c.Get(3, 0));
            string dir = TempDir();
            try
            {
                string file = Path.Combine(Images.Folder(dir), "drawn.png");
                PixelArt.Save(file, c.ToPng());
                Eq((4, 4), Images.Size(file));
                var big = PixelCanvas.From(300, 1, new uint[300]);
                Eq(PixelCanvas.MaxSize, big.Width);
            }
            finally { Directory.Delete(dir, true); }
        });
    }
}
