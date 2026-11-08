using System.Diagnostics;
using BuilderEngine.Script;

/// <summary>
/// The runtime audit (docs/audit/runtime-and-engine.md): scripts that used to close the whole program (a 'when told'
/// that tells itself, a list inside itself, a formula thousands of terms long), runaway loops, values and their
/// comparisons, error isolation, saves on a bad disk, long play without growing memory, and the interpreter's
/// allocations per frame. Called from Program.cs.
/// </summary>
static class AuditRuntimeTests
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

    static World NewWorld(string src = "")
    {
        var w = new World();
        StarterKit.LoadInto(w);
        if (src.Length > 0) w.Load(src);
        return w;
    }

    static string M(string body, string name = "T") =>
        $"mechanism {name}\n" + string.Join("\n", body.Split('\n').Select(l => "    " + l)) + "\n";

    static object? Var(GameObject o, string name) => o.Mechanisms[0].Vars.GetValueOrDefault(name);

    static string OneError(World w)
    {
        True(w.Errors.Count >= 1, "expected an error, got none");
        return w.Errors[0];
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Audit: crashes that closed the program");
        test("a 'when told' that tells itself stops with an error (it used to overflow the stack)", () =>
        {
            var w = NewWorld(M("when told \"ping\"\n    n += 1\n    tell self \"ping\"\n    after += 1\nwhen start\n    tell self \"ping\""));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Step(Dt); w.Step(Dt);
            True(OneError(w).Contains("inside itself"), w.Errors[0]);
            Eq(1, w.Errors.Count, "one error, not one per level");
            Eq((double)World.MaxEventNesting - 1, Var(o, "n"), "each level ran up to the tell (the when start is the first of the 100)");
            Eq((double)World.MaxEventNesting - 2, Var(o, "after"), "the outer levels went on after the inner one stopped");
        });
        test("an 'emit' whose handler emits again stops with an error; the rest of the game goes on", () =>
        {
            var w = NewWorld(M("when start\n    emit ping\nwhen ping\n    emit ping") + M("when tick\n    ticks += 1", "Counter"));
            w.Spawn("o", 0, 0, ["T"]);
            var c = w.Spawn("c", 0, 0, ["Counter"]);
            w.RunFor(0.5);
            True(OneError(w).Contains("inside itself"), w.Errors[0]);
            True((double)Var(c, "ticks")! >= 29, "other objects keep running");
        });
        test("a formula thousands of terms long is an error, not a crash", () =>
        {
            string sum = string.Join(" + ", Enumerable.Repeat("1", 20000));
            // Since the language audit the parser refuses it when the code is read (Parser.MaxSteps), before it can run.
            try { NewWorld(M("when start\n    x = " + sum + "\n    after = 1")); throw new Exception("no error"); }
            catch (ScriptError e) { True(e.Message.Contains("too deep"), e.Message); }
            var ok = NewWorld(M("when start\n    x = " + string.Join(" + ", Enumerable.Repeat("1", 300))));
            var p = ok.Spawn("p", 0, 0, ["T"]);
            ok.Step(Dt);
            Eq(300.0, Var(p, "x"), "a long but normal formula still works");
        });
        test("a record that holds itself (a card whose owner holds the card) prints, compares and copies", () =>
        {
            var w = NewWorld(M("when start\n    p = {name: \"Ann\"}\n    c = {owner: p}\n    p.card = c\n    print p\n    hand = [1]\n    add hand to hand\n    print hand\n    other = [1]\n    add other to other\n    same = hand == hand\n    diff = hand == other\n    h2 = copy(hand)\n    inner = h2[1] == h2\n    n = length(h2)"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Step(Dt);
            Eq(0, w.Errors.Count, string.Join("; ", w.Errors));
            Eq("{name: \"Ann\", card: {owner: {…}}}", w.Output[0]);
            Eq("[1, […]]", w.Output[1]);
            Eq(true, Var(o, "same")); Eq(false, Var(o, "diff"));
            Eq(true, Var(o, "inner"), "the copy holds itself (not the old list)");
            Eq(2.0, Var(o, "n"));
            True(BuilderEngine.Script.Debugger.Show(Var(o, "hand"), 200).EndsWith("[…]]]]]"), "the debugger shows it a few levels deep");
        });
        test("saving a list inside itself is an error on that line, not a crash", () =>
        {
            var w = NewWorld(M("when start\n    hand = [1]\n    add hand to hand\n    save \"h\", hand\n    after = 1"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Step(Dt);
            True(OneError(w).Contains("contains itself") && w.Errors[0].Contains("line 5"), w.Errors[0]);
            Eq(null, Var(o, "after"));
        });
        test("lists nested thousands deep print, compare and copy without crashing", () =>
        {
            var w = NewWorld(M("when start\n    a = []\n    repeat 5000\n        a = [a]\n    t = text(a)\n    same = a == a\n    b = copy(a)\n    done = 1"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Step(Dt);
            True(((string)Var(o, "t")!).Contains("[…]"), "shown up to a depth");
            Eq(true, Var(o, "same"));
            True(Var(o, "done") != null || w.Errors.Count == 1, "copied, or a clean error: " + string.Join("; ", w.Errors));
        });

        Console.WriteLine("Audit: runaway loops and error isolation");
        test("a loop that never waits stops with an error instead of freezing the editor", () =>
        {
            var w = NewWorld(M("when start\n    repeat 100000000\n        n += 1\n    after = 1") + M("when tick\n    ticks += 1", "Counter"));
            w.MaxLinesWithoutWait = 50_000;
            var o = w.Spawn("o", 0, 0, ["T"]);
            var c = w.Spawn("c", 0, 0, ["Counter"]);
            w.Step(Dt); w.Step(Dt); w.Step(Dt);
            True(OneError(w).Contains("without waiting") && w.Errors[0].Contains("line 4"), w.Errors[0]);
            True((double)Var(o, "n")! < 60_000, "stopped near the limit");
            Eq(null, Var(o, "after"));
            Eq(3.0, Var(c, "ticks"), "the game went on");
        });
        test("the line budget is per 'when' at a time: many objects each doing a little are fine", () =>
        {
            var w = NewWorld(M("when tick\n    repeat 50\n        n += 1"));
            w.MaxLinesWithoutWait = 1_000;
            for (int i = 0; i < 200; i++) w.Spawn("o" + i, 0, 0, ["T"]);
            w.RunFor(0.2);
            Eq(0, w.Errors.Count, string.Join("; ", w.Errors.Take(2)));
            var big = NewWorld(M("when start\n    repeat 200000\n        n += 1"));
            var b = big.Spawn("b", 0, 0, ["T"]);
            big.Step(Dt);
            Eq(200000.0, Var(b, "n"), "a long one-off job (world generation) still runs at the default budget");
            True(big.ForNextScene().MaxLinesWithoutWait == big.MaxLinesWithoutWait, "kept by the next scene");
        });
        test("an error in one 'when' doesn't stop the others in the same step", () =>
        {
            var w = NewWorld(M("when tick\n    x = 1 / 0") + M("when tick\n    ok += 1", "Good"));
            w.Spawn("bad", 0, 0, ["T"]);
            var g = w.Spawn("good", 0, 0, ["Good"]);
            w.RunFor(0.5);
            True(w.Errors.Count >= 29, "the broken tick fails every frame");
            True((double)Var(g, "ok")! >= 29, "the good one ran every frame");
        });
        test("the same error every frame is counted every time but printed once", () =>
        {
            var w = NewWorld(M("when tick\n    x = 1 / 0"));
            var printed = new List<string>();
            w.Printed += printed.Add;
            w.Spawn("bad", 0, 0, ["T"]);
            w.RunFor(1);
            True(w.Errors.Count >= 59, $"counted ({w.Errors.Count})");
            Eq(1, printed.Count(p => p.Contains("Division by zero")), "printed once");
            True(ReferenceEquals(w.Errors[0], w.Errors[^1]), "the repeats share one text (no memory per frame)");
        });
        test("errors inside functions say their line", () =>
        {
            var w = NewWorld(M("when start\n    a = 1\n    k = keys(5)"));
            w.Spawn("o", 0, 0, ["T"]);
            w.Step(Dt);
            True(OneError(w).Contains("line 4") && w.Errors[0].Contains("keys"), w.Errors[0]);
        });

        Console.WriteLine("Audit: values");
        test("nothing equals false, like it equals 0 and \"\" (if done == false works before 'done' is set)", () =>
        {
            var w = NewWorld(M("when start\n    a = unset == false\n    b = unset != false\n    c = unset == 0\n    d = unset == \"\"\n    e = \"1\" == 1\n    f = \"abc\" == 0\n    g = false == unset\n    h = unset == true\n    if opened == false: branch = 1"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Step(Dt);
            Eq(true, Var(o, "a")); Eq(false, Var(o, "b")); Eq(true, Var(o, "c")); Eq(true, Var(o, "d"));
            Eq(true, Var(o, "e")); Eq(false, Var(o, "f")); Eq(true, Var(o, "g")); Eq(false, Var(o, "h"));
            Eq(1.0, Var(o, "branch"));
        });
        test("-0 reads as 0 (in print, text() and {x} in texts)", () =>
        {
            var w = NewWorld(M("when start\n    v = 0\n    v = v * -1\n    print v\n    t = text(v)\n    show text \"V {v}\" at 0, 0"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Frame(Dt);
            Eq("0", w.Output[0]); Eq("0", Var(o, "t"));
            Eq("V 0", w.Hud.Single().Text);
        });
        test("x, y, size and speed can't become NaN or infinity (the object would vanish for good)", () =>
        {
            var w = NewWorld(M("when start\n    self.x = sqrt(-1)\n    after = 1"));
            var o = w.Spawn("o", 5, 0, ["T"]);
            w.Step(Dt);
            True(OneError(w).Contains("isn't a number") && w.Errors[0].Contains("line 3"), w.Errors[0]);
            Eq(5.0, o.X, "x unchanged");
            Eq(null, Var(o, "after"));
        });
        test("a mistake inside {…} in a text shows '?' and says why once in the console", () =>
        {
            var w = NewWorld(M("when start\n    show text \"HP {1 / 0}\" at 0, 0"));
            w.Spawn("o", 0, 0, ["T"]);
            w.RunFor(0.2);
            Eq("HP ?", w.Hud.Single().Text);
            Eq(1, w.Output.Count(l => l.Contains("Division by zero")), "warned once");
        });

        Console.WriteLine("Audit: objects and lifecycle");
        test("find() doesn't return an object spawned and destroyed in the same step", () =>
        {
            var w = NewWorld(M("when start\n    spawn Coin\n    destroy find(\"Coin\")\n    f = find(\"Coin\")") + M("when start\n    x = 1", "Coin"));
            var o = w.Spawn("o", 0, 0, ["T"]);
            w.Step(Dt);
            Eq(null, Var(o, "f"));
            w.Step(Dt);
            Eq(1, w.Objects.Count, "the coin never joined");
        });
        test("a param whose default names a mechanism listed later doesn't hide that mechanism's 'when's", () =>
        {
            var w = NewWorld(M("param link = Second\nwhen start\n    a = 1") + M("when start\n    b = 1", "Second"));
            var o = w.Spawn("o", 0, 0, ["T", "Second"]);
            w.Step(Dt);
            Eq(1.0, Var(o, "a"));
            Eq(1.0, o.Mechanisms[1].Vars.GetValueOrDefault("b"), "Second ran its 'when start'");
            True(Var(o, "link") is MechanismInstance or string, "the default was worked out");
        });
        test("ten simulated minutes of spawning and destroying: objects, running 'when's and memory stay flat", () =>
        {
            var w = NewWorld(M("when start\n    repeat forever\n        spawn Bullet\n        wait 0.05", "Gun") +
                M("param life: number = 0\nwhen tick\n    life += dt\n    self.x += 200 * dt\n    if life > 0.5: destroy\nwhen start\n    wait 10\n    never = 1", "Bullet"));
            for (int i = 0; i < 3; i++) w.Spawn("g" + i, i * 50, 0, ["Gun"]);
            w.RunFor(5, 0.1);
            int objects = w.Objects.Count, running = w.Running().Count;
            long before = GC.GetTotalMemory(true);
            w.RunFor(600, 0.1); // 10 minutes in 0.1 s steps
            long after = GC.GetTotalMemory(true);
            Eq(0, w.Errors.Count, string.Join("; ", w.Errors.Take(2)));
            True(Math.Abs(w.Objects.Count - objects) <= 3, $"objects stay about the same ({objects} → {w.Objects.Count})");
            True(Math.Abs(w.Running().Count - running) <= 6, $"running 'when's of destroyed bullets are gone ({running} → {w.Running().Count})");
            Console.WriteLine($"         (managed heap {before / 1024} KB → {after / 1024} KB after 10 simulated minutes)");
            True(after - before < 2_000_000, $"memory doesn't grow ({before / 1024} KB → {after / 1024} KB)");
        });
        test("Output keeps the newest lines only (a print every frame for hours)", () =>
        {
            var w = NewWorld(M("when tick\n    print time"));
            w.Spawn("o", 0, 0, ["T"]);
            for (int i = 0; i < World.MaxOutputLines + 500; i++) w.Step(Dt);
            True(w.Output.Count <= World.MaxOutputLines, $"capped ({w.Output.Count})");
            Eq(World.ToText(w.Time), w.Output[^1], "the newest is kept");
        });

        Console.WriteLine("Audit: saves on a bad disk");
        test("a save that can't be written is an error on its line; the game goes on", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "BE_AuditSave_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            string notAFolder = Path.Combine(dir, "saves");
            File.WriteAllText(notAFolder, "a file where the saves folder should be");
            try
            {
                var w = NewWorld(M("when start\n    score = 3\n    save game\n    after = 1") + M("when tick\n    ticks += 1", "Counter"));
                w.SaveFolder = notAFolder;
                var o = w.Spawn("o", 0, 0, ["T"]);
                var c = w.Spawn("c", 0, 0, ["Counter"]);
                w.Step(Dt); w.Step(Dt);
                True(OneError(w).Contains("couldn't be saved") && w.Errors[0].Contains("line 4"), w.Errors[0]);
                Eq(null, Var(o, "after"));
                Eq(2.0, Var(c, "ticks"));
            }
            finally { Directory.Delete(dir, true); }
        });
        test("a damaged save is reported once and reads as no save", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "BE_AuditSave_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "save.json"), "{ \"globals\": { \"score\": 3 "); // cut off half-way
            try
            {
                var w = NewWorld(M("when start\n    load game\n    got = loaded"));
                w.SaveFolder = dir;
                var o = w.Spawn("o", 0, 0, ["T"]);
                w.Step(Dt);
                Eq(false, Var(o, "got"));
                Eq(1, w.Output.Count(l => l.Contains("damaged")), "warned");
                Eq(0, w.Errors.Count);
            }
            finally { Directory.Delete(dir, true); }
        });

        Console.WriteLine("Audit: performance");
        test("the interpreter allocates less per 'when tick' (5000 objects)", () =>
        {
            var w = NewWorld(M("param speed: number = 30\nwhen tick\n    self.x += speed * dt\n    if self.x > 6000: self.x = 0\n    hp = Health.hp\n    total = total + 1", "Mover") +
                M("param hp: number = 3", "Health") + M("when tick\n    score = score + 1\n    if lives < 0: print \"x\"", "G"));
            w.Globals["score"] = 0.0; w.Globals["lives"] = 3.0;
            var rng = new Random(1);
            for (int i = 0; i < 5000; i++) w.Spawn("m" + i, rng.Next(0, 6000), rng.Next(0, 3000), ["Mover", "Health", "G"]);
            for (int i = 0; i < 10; i++) w.Frame(Dt);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            const int frames = 30;
            for (int i = 0; i < frames; i++) w.Frame(Dt);
            sw.Stop();
            double perTick = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)frames / 10000;
            Console.WriteLine($"         ({sw.Elapsed.TotalMilliseconds / frames:0.00} ms per frame, {perTick:0} bytes per 'when tick' run; before the audit: 2088 in a Debug build, 1664 in Release)");
            Eq(0, w.Errors.Count, string.Join("; ", w.Errors.Take(2)));
            True(perTick < 1600, $"{perTick:0} bytes per tick run");
        });
        test("10000 'wait until' checks per frame allocate almost nothing", () =>
        {
            var w = NewWorld(M("when start\n    wait until flag\n    n += 1"));
            for (int i = 0; i < 10000; i++) w.Spawn("w" + i, 0, 0, ["T"]);
            for (int i = 0; i < 5; i++) w.Frame(Dt);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) w.Frame(Dt);
            double perCheck = (GC.GetAllocatedBytesForCurrentThread() - a0) / 20.0 / 10000;
            Console.WriteLine($"         ({perCheck:0.0} bytes per check; before the audit: ~172)");
            True(perCheck < 64, $"{perCheck:0.0} bytes per check");
            w.Globals["flag"] = true;
            w.Frame(Dt);
            Eq(1.0, w.Objects[0].Mechanisms[0].Vars["n"], "they still wake up");
        });
    }
}
