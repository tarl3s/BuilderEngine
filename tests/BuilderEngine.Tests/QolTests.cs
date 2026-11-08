using BuilderEngine.Script;

/// <summary>The scene editor's small conveniences (SceneEdit) and the shortcuts list. Called from Program.cs with its Test runner.</summary>
static class QolTests
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

    /// <summary>A scene with A (0,0 32×32), B (100,0 32×32), C (200,100 16×16, locked), D (300,300 50×20).</summary>
    static Scene Sample()
    {
        var s = new Scene { Name = "Test", Width = 800, Height = 450 };
        s.Add(new SceneObject { Name = "A", X = 0, Y = 0 });
        s.Add(new SceneObject { Name = "B", X = 100, Y = 0 });
        s.Add(new SceneObject { Name = "C", X = 200, Y = 100, Width = 16, Height = 16, Locked = true });
        s.Add(new SceneObject { Name = "D", X = 300, Y = 300, Width = 50, Height = 20 });
        return s;
    }

    static int Id(Scene s, string name) => s.Objects.First(o => o.Name == name).Id;
    static string Order(Scene s) => string.Join("", s.Objects.Select(o => o.Name));

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Editor conveniences");

        test("locked and keep-proportions are saved only when on, and read back", () =>
        {
            var s = Sample();
            string json = s.ToJson();
            True(json.Contains("\"locked\": true"), json);
            Eq(1, json.Split("\"locked\"").Length - 1, "only C writes locked");
            True(!json.Contains("keepAspect"), "keepAspect is left out while off");
            s.Objects[0].KeepAspect = true;
            var back = Scene.FromJson(s.ToJson());
            Eq(true, back.Objects[2].Locked);
            Eq(true, back.Objects[0].KeepAspect);
            Eq(false, back.Objects[1].Locked);
            Eq(true, Scene.FromJson("{\"objects\":[{\"name\":\"X\",\"locked\":true}]}").Objects[0].Locked, "hand-written json");
            Eq(true, s.Objects[2].Clone().Locked, "Clone keeps the lock");
        });

        test("picking skips locked objects; the top object wins", () =>
        {
            var s = Sample();
            Eq(null, SceneEdit.At(s, 205, 105), "locked C can't be clicked");
            Eq("C", SceneEdit.At(s, 205, 105, includeLocked: true)?.Name);
            s.Add(new SceneObject { Name = "Over", X = 10, Y = 10 });
            Eq("Over", SceneEdit.At(s, 15, 15)?.Name, "drawn last = on top");
            Eq(null, SceneEdit.At(s, 600, 400));
        });

        test("selection box: touched objects, any drag direction, never locked ones", () =>
        {
            var s = Sample();
            Eq("A,B", string.Join(",", SceneEdit.InBox(s, -10, -10, 110, 20).Select(id => s.Find(id)!.Name)));
            Eq("A,B", string.Join(",", SceneEdit.InBox(s, 110, 20, -10, -10).Select(id => s.Find(id)!.Name)), "dragged up-left");
            Eq(0, SceneEdit.InBox(s, 190, 90, 230, 130).Count, "only the locked C");
            Eq("D", string.Join(",", SceneEdit.InBox(s, 349, 319, 360, 330).Select(id => s.Find(id)!.Name)), "touching a corner is enough");
            Eq(3, SceneEdit.All(s).Count, "Ctrl+A leaves the locked one out");
        });

        test("arrow keys: 1 px, Shift one grid cell; locked objects stay", () =>
        {
            Eq(1.0, SceneEdit.NudgeStep(false));
            Eq(SceneEdit.Grid, SceneEdit.NudgeStep(true));
            var s = Sample();
            int moved = SceneEdit.Nudge(s, [Id(s, "A"), Id(s, "C")], 8, -1);
            Eq(1, moved);
            Eq(8.0, s.Objects[0].X); Eq(-1.0, s.Objects[0].Y);
            Eq(200.0, s.Objects[2].X, "locked C didn't move");
        });

        test("dragging several: the grabbed one snaps, the group keeps its shape; Shift keeps one axis", () =>
        {
            Eq((8.0, 16.0), SceneEdit.DragDelta(0, 0, 9, 13, snap: true, axisLock: false));
            Eq((7.0, 0.0), SceneEdit.DragDelta(3, 5, 7, 2, snap: false, axisLock: true), "free, mostly across");
            Eq((0.0, 13.0), SceneEdit.DragDelta(0, 0, 4, 13, snap: false, axisLock: true));
            Eq((5.0, 0.0), SceneEdit.DragDelta(3, 5, 6, 1, snap: true, axisLock: true), "grabbed at 3 lands on 8");
        });

        test("resize: free, and keeping the proportions from a corner and a side", () =>
        {
            Eq((0.0, 0.0, 64.0, 32.0), SceneEdit.Resize(0, 0, 32, 32, 3, 64, 0, null), "right side, free");
            Eq((0.0, 0.0, 64.0, 64.0), SceneEdit.Resize(0, 0, 32, 32, 4, 64, 40, 1.0), "corner follows the side that moved most");
            Eq((10.0, 10.0, 4.0, 4.0), SceneEdit.Resize(10, 10, 32, 32, 4, -50, -50, null), "never below the minimum");
            var (x, y, w, h) = SceneEdit.Resize(0, 0, 40, 20, 3, 80, 0, 2.0); // right side of a 2:1 box: centred up and down
            Eq((0.0, -10.0, 80.0, 40.0), (x, y, w, h));
            var tl = SceneEdit.Resize(0, 0, 40, 20, 0, -40, 5, 2.0); // top-left corner: anchored bottom-right
            Eq(40.0, tl.X + tl.W, "right edge stays");
            Eq(20.0, tl.Y + tl.H, "bottom edge stays");
            Eq(2.0, tl.W / tl.H, "ratio kept");
            Eq(50.0, SceneEdit.OtherSide(100, 2, typedWidth: true));
            Eq(200.0, SceneEdit.OtherSide(100, 2, typedWidth: false));
        });

        test("copy / paste: deep copies, new ids and names, at the pointer or a little further each time", () =>
        {
            var s = Sample();
            string clip = SceneEdit.Copy(s, [Id(s, "B"), Id(s, "A")]);
            s.Objects[0].X = 999; // later edits don't change what was copied
            var copies = SceneEdit.Paste(s, clip, null, 1);
            Eq("A 2,B 2", string.Join(",", copies.Select(c => c.Name)), "in scene order, numbered");
            Eq(16.0, copies[0].X); Eq(116.0, copies[1].X);
            Eq(6, s.Objects.Count);
            True(copies.All(c => c.Id > 4) && copies[0].Id != copies[1].Id, "fresh ids");
            var again = SceneEdit.Paste(s, clip, null, 2);
            Eq(32.0, again[0].X, "the 2nd paste lands further");
            Eq("A 3", again[0].Name);
            var at = SceneEdit.Paste(s, clip, (403, 197), 0);
            Eq(400.0, at[0].X, "group corner at the pointer, on the grid"); Eq(200.0, at[0].Y);
            Eq(500.0, at[1].X, "the group keeps its shape");
            // Into another scene (the clipboard is just text): the lock doesn't come along.
            var other = new Scene { Name = "Level 2" };
            var lockedClip = SceneEdit.Copy(s, [Id(s, "C")]);
            var pasted = SceneEdit.Paste(other, lockedClip, null, 1);
            Eq("C", pasted[0].Name, "no clash in the other scene: same name");
            Eq(false, pasted[0].Locked, "a pasted copy is unlocked");
            Eq(0, SceneEdit.FromClipboard("not json").Count);
            Eq(0, SceneEdit.FromClipboard("").Count);
        });

        test("duplicate and delete several; locked ones are left alone", () =>
        {
            var s = Sample();
            var copies = SceneEdit.Duplicate(s, [Id(s, "A"), Id(s, "C"), Id(s, "D")]);
            Eq("A 2,D 2", string.Join(",", copies.Select(c => c.Name)));
            Eq(SceneEdit.CopyOffset, copies[0].X);
            Eq(2, SceneEdit.Delete(s, [Id(s, "A"), Id(s, "C"), Id(s, "B")]));
            Eq("CDA 2D 2", Order(s));
            Eq("Coin", SceneEdit.UniqueName(s, "Coin"));
            Eq("D 3", SceneEdit.UniqueName(s, "D"), "D and D 2 are taken");
        });

        test("draw order: front, back, one step, several at once; locked objects stay", () =>
        {
            var s = Sample();
            Eq(true, SceneEdit.Reorder(s, [Id(s, "A")], ZMove.Front));
            Eq("BCDA", Order(s));
            Eq(false, SceneEdit.Reorder(s, [Id(s, "A")], ZMove.Front), "already in front");
            SceneEdit.Reorder(s, [Id(s, "A")], ZMove.Back);
            Eq("ABCD", Order(s));
            SceneEdit.Reorder(s, [Id(s, "A")], ZMove.Forward);
            Eq("BACD", Order(s));
            SceneEdit.Reorder(s, [Id(s, "D")], ZMove.Backward);
            Eq("BADC", Order(s));
            SceneEdit.Reorder(s, [Id(s, "B"), Id(s, "A")], ZMove.Front);
            Eq("DCBA", Order(s), "a group keeps its own order");
            Eq(false, SceneEdit.Reorder(s, [Id(s, "C")], ZMove.Back), "locked C doesn't move");
            Eq((4, 4), SceneEdit.OrderOf(s, Id(s, "A")));
        });

        test("align and space evenly", () =>
        {
            var s = Sample();
            int[] abd = [Id(s, "A"), Id(s, "B"), Id(s, "D")];
            Eq(true, SceneEdit.Align(s, abd, AlignTo.Bottom));
            Eq("288,288,300", string.Join(",", s.Objects.Where(o => o.Name != "C").Select(o => o.Y)));
            SceneEdit.Align(s, abd, AlignTo.Left);
            True(s.Objects.Where(o => o.Name != "C").All(o => o.X == 0), "all at the left edge");
            Eq(200.0, s.Objects[2].X, "locked C stays");
            var t = new Scene();
            t.Add(new SceneObject { Name = "1", X = 0, Width = 10 });
            t.Add(new SceneObject { Name = "2", X = 15, Width = 10 });
            t.Add(new SceneObject { Name = "3", X = 90, Width = 10 });
            Eq(true, SceneEdit.Distribute(t, t.Objects.Select(o => o.Id), horizontal: true));
            Eq("0,45,90", string.Join(",", t.Objects.Select(o => o.X)));
            Eq(false, SceneEdit.Distribute(t, t.Objects.Take(2).Select(o => o.Id), true), "needs 3");
            Eq(false, SceneEdit.Align(t, [t.Objects[0].Id], AlignTo.Top), "needs 2");
        });

        test("mouse wheel on numbers: step, Shift ×10, Ctrl ×0.1, limits, plain text numbers", () =>
        {
            Eq(9.0, SceneEdit.WheelValue(8, 1, 1, false, false));
            Eq(18.0, SceneEdit.WheelValue(8, 1, 1, true, false));
            Eq(8.1, SceneEdit.WheelValue(8, 1, 1, false, true));
            Eq(0.0, SceneEdit.WheelValue(8, -1, 8, false, false));
            Eq(1.0, SceneEdit.WheelValue(1, -1, 8, false, false, min: 1), "kept at the minimum");
            Eq(0.31, SceneEdit.WheelValue(0.3, 1, 0.1, false, true), "0.1 × 0.1, no float noise");
            Eq(-80.0, SceneEdit.WheelValue(0, -1, 8, true, false));
            Eq("13", SceneEdit.WheelText("12", 1, false, false));
            Eq("0.9", SceneEdit.WheelText("0.8", 1, false, false), "the step follows the decimals");
            Eq("22", SceneEdit.WheelText("12", 1, true, false));
            Eq("12.1", SceneEdit.WheelText("12", 1, false, true));
            Eq("0", SceneEdit.WheelText("0", -1, false, false), "0 or more never goes below 0");
            Eq("-6", SceneEdit.WheelText("-5", -1, false, false));
            Eq("1.6", SceneEdit.WheelText("1,5", 1, false, false), "a comma decimal");
            Eq(null, SceneEdit.WheelText("speed * 2", 1, false, false), "a formula isn't stepped");
            Eq(null, SceneEdit.WheelText("", 1, false, false));
        });

        test("type to search: case, accents and emoji don't matter; starts-with ranks first", () =>
        {
            Eq("plataforma rapida", SceneEdit.Fold("🏃  Plataforma Rápida!"));
            Eq(3, SceneEdit.MatchScore("Coin", "co"));
            Eq(2, SceneEdit.MatchScore("Player Move", "mov"));
            Eq(1, SceneEdit.MatchScore("Platformer", "form"));
            Eq(0, SceneEdit.MatchScore("Health", "coin"));
            Eq(3, SceneEdit.MatchScore("🔒 Lock", "lock"));
            Eq(3, SceneEdit.MatchScore("Pássaro", "pass"));
            Eq(1, SceneEdit.MatchScore("anything", ""));
        });

        test("shortcuts help: every line is in docs/SHORTCUTS.md and no two lines share keys in a group", () =>
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BuilderEngine.sln"))) dir = dir.Parent;
            True(dir != null, "repo root not found");
            string doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "SHORTCUTS.md"));
            foreach (var s in EditorShortcuts.All)
            {
                True(doc.Contains(s.Keys), $"docs/SHORTCUTS.md is missing the keys “{s.Keys}”");
                True(doc.Contains(s.What), $"docs/SHORTCUTS.md is missing “{s.What}”");
            }
            foreach (var g in EditorShortcuts.All.GroupBy(s => s.Group))
                Eq(g.Count(), g.Select(s => s.Keys).Distinct().Count(), $"duplicate keys in {g.Key}");
            True(EditorShortcuts.All.Count >= 35, "the list covers the editor");
        });
    }
}
