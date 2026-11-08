using BuilderEngine.Script;

/// <summary>
/// The Ember Duel sample (a card battler written in Builder Script) and the engine pieces it needed: records,
/// sorted/copy, labels and outlines, lists in saves. The sample is built into a temp folder and played headless with
/// simulated mouse drags and clicks. Called from Program.cs.
/// </summary>
static class EmberDuelTests
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

    static void NoErrors(World w) => Eq(0, w.Errors.Count, string.Join(" | ", w.Errors));

    static World NewWorld(string src = "")
    {
        var w = new World();
        StarterKit.LoadInto(w);
        if (src.Length > 0) w.Load(src);
        return w;
    }

    static string M(string body, string name = "T") => $"mechanism {name}\n" + string.Join("\n", body.Split('\n').Select(l => "    " + l)) + "\n";

    // ---------------- Playing the sample ----------------

    /// <summary>The sample written to a temp folder, its project loaded (deleted when disposed).</summary>
    sealed class Sample : IDisposable
    {
        public readonly string Folder = Path.Combine(Path.GetTempPath(), "BE_Test_EmberDuel_" + Guid.NewGuid().ToString("N")[..8]);
        public readonly Project Project;

        public Sample()
        {
            SampleGames.Make("Ember Duel", Folder);
            Project = Project.Load(Folder);
        }

        /// <summary>A world playing the sample's scene. <paramref name="duel"/> sets Duel params; <paramref name="deck"/> is a saved deck.</summary>
        public World Play(Dictionary<string, object?>? duel = null, List<object?>? deck = null)
        {
            var w = new World();
            StarterKit.LoadInto(w);
            foreach (var (_, src) in Project.Mechanisms) w.Load(src);
            w.ProjectFolder = Folder;
            w.SaveFolder = Path.Combine(Folder, Project.SavesFolder, Guid.NewGuid().ToString("N")[..6]);
            w.Globals["score"] = 0.0;
            w.UseScenes(Project.Scenes.Select(s => s.Name));
            if (deck != null) w.SaveValue("deck", deck);
            var scene = Project.Scenes[0].Clone();
            if (duel != null)
                foreach (var (k, v) in duel)
                    scene.Objects.First(o => o.Name == "Duel").Mechanisms[0].Params[k] = v;
            w.LoadScene(scene);
            w.MouseMove(-10, -10, inside: true);
            w.Frame(Dt);
            w.Frame(Dt);
            return w;
        }

        public void Dispose()
        {
            try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static Dictionary<string, object?> DuelVars(World w) => w.FindObject("Duel")!.Mechanisms[0].Vars;
    static Record Player(World w, string who) => (Record)DuelVars(w)[who]!;
    static List<object?> Hand(Record p) => (List<object?>)p["hand"]!;
    static Record CardOf(GameObject o) => (Record)o.AllMechanisms().First(m => m.Def.Name is "CardView" or "CardZoom").Vars["card"]!;
    static object? LaneUnit(GameObject lane) => lane.Mechanisms[0].Vars.GetValueOrDefault("unit");
    static double N(object? v) => World.ToNumber(v, 0);

    /// <summary>Runs frames until the condition holds (or fails after <paramref name="seconds"/> of game time).</summary>
    static void RunUntil(World w, Func<bool> done, double seconds, string what)
    {
        for (double t = 0; t < seconds; t += Dt)
        {
            if (done()) return;
            w.Frame(Dt);
        }
        if (!done()) throw new Exception($"timed out waiting for {what} ({string.Join(" | ", w.Errors)})");
    }

    static (double X, double Y) Center(GameObject o) => (o.X + o.Width / 2, o.Y + o.Height / 2);

    /// <summary>Drags an object (on the screen) to a point with the mouse, a few frames per move, the way a person does.</summary>
    static void Drag(World w, GameObject o, double x, double y)
    {
        var (cx, cy) = Center(o);
        w.MouseMove(cx, cy);
        w.RunFor(0.2);
        w.MouseDown();
        w.RunFor(0.1);
        for (int i = 1; i <= 6; i++) { w.MouseMove(cx + (x - cx) * i / 6, cy + (y - cy) * i / 6); w.Frame(Dt); }
        w.RunFor(0.1);
        w.MouseUp();
        w.RunFor(0.1);
    }

    static void Click(World w, GameObject o)
    {
        var (cx, cy) = Center(o);
        w.MouseMove(cx, cy);
        w.RunFor(0.15);
        w.MouseDown();
        w.Frame(Dt);
        w.MouseUp();
        w.RunFor(0.15);
    }

    static readonly List<object?> AllCreatures =
    [
        "Cinder Pup", "Ash Sprite", "Reef Crab", "Moss Beetle", "Kindle Fox", "Mist Heron", "Thorn Hedgehog", "Dusk Moth",
        "Grove Stag", "Lantern Wisp", "Tide Caller", "Gloom Lurker", "Brazier Golem", "Coral Warden", "Bramble Knight",
        "Flare Drake", "Rune Sentinel", "Elder Oakheart", "Deepwater Serpent", "Molten Titan",
    ];

    /// <summary>Starts a fight and waits for the player's first turn (mana 1, hand of 4).</summary>
    static World StartFight(Sample s, List<object?>? deck = null)
    {
        var w = s.Play(deck: deck);
        w.Broadcast("fight", []);
        RunUntil(w, () => Equals(DuelVars(w)["turn"], "you") && Hand(Player(w, "you")).Count == 4 && w.FindObject("Banner")!.Hidden, 10, "your first turn");
        w.RunFor(0.4);
        return w;
    }

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Engine: records, labels, saves of lists (for the Ember Duel sample)");
        test("records: literal, dot, brackets, change, copy, keys, missing field", () =>
        {
            var w = NewWorld(M("when start\n    card = {name: \"Imp\", cost: 1, \"two words\": 2}\n    card.cost += 2\n    other = copy(card)\n    other.cost = 9\n    a = card.cost\n    b = card[\"two words\"]\n    c = card.missing\n    d = length(card)\n    e = keys(card)\n    f = has_key(card, \"name\")\n    g = contains(card, \"cost\")\n    t = text(card)"));
            var o = w.Spawn("t", 0, 0, ["T"]);
            w.Step(Dt);
            NoErrors(w);
            var v = o.Mechanisms[0].Vars;
            Eq(3.0, v["a"]); Eq(2.0, v["b"]); Eq(null, v["c"]); Eq(3.0, v["d"]);
            Eq("name,cost,two words", string.Join(",", ((List<object?>)v["e"]!).Select(World.ToText)));
            Eq(true, v["f"]); Eq(true, v["g"]);
            Eq(9.0, ((Record)v["other"]!)["cost"], "the copy changed");
            Eq("{name: \"Imp\", cost: 3, \"two words\": 2}", v["t"]);
        });
        test("records print, parse back and work in blocks' value fields", () =>
        {
            string src = M("when start\n    hand = [{name: \"A\", cost: 2}, {name: \"B\", cost: 1}]\n    x = {}\n    y = {\"to\": 1, n: [1, 2]}[\"to\"]\n    deck = {\n        size: 20,\n        cards: []\n    }");
            var d = Parser.Parse(src)[0];
            string printed = Printer.Print(d);
            True(printed.Contains("hand = [{name: \"A\", cost: 2}, {name: \"B\", cost: 1}]"), printed);
            True(printed.Contains("deck = {size: 20, cards: []}"), printed);
            True(printed.Contains("x = {}"), printed);
            Eq(printed, Printer.Print(Parser.Parse(printed)[0]), "printed twice");
            var e = Parser.ParseExpression("{cost: 1 + 2}");
            Eq("{cost: 1 + 2}", Printer.Print(e));
            var w = NewWorld(src);
            var o = w.Spawn("t", 0, 0, ["T"]);
            w.Step(Dt);
            NoErrors(w);
            Eq(1.0, o.Mechanisms[0].Vars["y"]);
            Eq(20.0, ((Record)o.Mechanisms[0].Vars["deck"]!)["size"]);
        });
        test("names after a call or an item: find(\"Turns\").TurnManager.turn, hand[0].card.cost", () =>
        {
            string src = M("when start\n    hand = [{card: {cost: 4}}]\n    a = hand[0].card.cost\n    b = find(\"Turns\").TurnManager.turn\n    c = all(\"x\")[0].name\n    d = [[1, {n: [7, 8]}]][0][1].n[1]");
            var d = Parser.Parse(src)[0];
            string printed = Printer.Print(d);
            True(printed.Contains("a = hand[0].card.cost") && printed.Contains("b = find(\"Turns\").TurnManager.turn") && printed.Contains("d = [[1, {n: [7, 8]}]][0][1].n[1]"), printed);
            Eq(printed, Printer.Print(Parser.Parse(printed)[0]));
            var w = NewWorld(src);
            w.Spawn("Turns", 0, 0, ["TurnManager"]);
            w.Spawn("Xy", 0, 0, null, null, "x");
            var o = w.Spawn("t", 0, 0, ["T"]);
            w.Step(Dt);
            NoErrors(w);
            var v = o.Mechanisms[0].Vars;
            Eq(4.0, v["a"]); Eq("player", v["b"]); Eq("Xy", v["c"]); Eq(8.0, v["d"]);
        });
        test("records: friendly errors", () =>
        {
            foreach (var (bad, frag) in new[] { ("x = {cost 1}", "':'"), ("x = {cost: 1", "missing its '}'"), ("x = {a: 1, a: 2}", "two fields"), ("x = {1: 2}", "field name") })
            {
                try { Parser.Parse(M("when start\n    " + bad)); throw new Exception("no error for " + bad); }
                catch (ScriptError e) { True(e.Message.Contains(frag), $"{bad}: {e.Message}"); }
            }
        });
        test("sorted, reversed and copy of lists", () =>
        {
            var w = NewWorld(M("when start\n    cards = [{n: \"b\", cost: 3}, {n: \"a\", cost: 1}, {n: \"c\", cost: 3}]\n    s = sorted(cards, \"cost\")\n    names = []\n    for each c in s\n        add c.n to names\n    nums = sorted([3, 1, 2])\n    r = reversed(nums)\n    l = [[1], [2]]\n    k = copy(l)\n    add 5 to k[0]"));
            var o = w.Spawn("t", 0, 0, ["T"]);
            w.Step(Dt);
            NoErrors(w);
            var v = o.Mechanisms[0].Vars;
            Eq("a,b,c", string.Join(",", ((List<object?>)v["names"]!).Select(World.ToText)), "stable sort by cost");
            Eq("[1, 2, 3]", World.ToText(v["nums"]));
            Eq("[3, 2, 1]", World.ToText(v["r"]));
            Eq("[[1], [2]]", World.ToText(v["l"]), "the copy is deep");
        });
        test("save and saved keep lists and records", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "BE_Test_Saves_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var w = NewWorld(M("when start\n    deck = [\"Imp\", \"Fox\"]\n    save \"deck\", deck\n    add \"Owl\" to deck\n    save \"best\", {name: \"Imp\", wins: 3}"));
                w.SaveFolder = dir;
                w.Spawn("t", 0, 0, ["T"]);
                w.Step(Dt);
                NoErrors(w);
                var w2 = NewWorld(M("when start\n    d = saved(\"deck\")\n    b = saved(\"best\")\n    n = b.wins"));
                w2.SaveFolder = dir;
                var o = w2.Spawn("t", 0, 0, ["T"]);
                w2.Step(Dt);
                NoErrors(w2);
                Eq("[\"Imp\", \"Fox\"]", World.ToText(o.Mechanisms[0].Vars["d"]), "the list as it was when saved");
                Eq(3.0, o.Mechanisms[0].Vars["n"]);
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        });
        test("labels: set, replace, remove, anchors, badges, templates; outline", () =>
        {
            var w = NewWorld(M("when start\n    hp = 3\n    label \"cost\" \"2\" at top left by -4, -4 with \"#FFFFFF\", 12, \"#2F6FD0\"\n    label \"hp\" \"{hp}\" at bottom right with \"#FF0000\", 10\n    label \"name\" \"Imp\" at \"top\"\n    label \"cost\" \"5\"\n    outline \"#FFD54A\", 3\n    wait 0.1\n    hp = 7\n    label \"name\" none"));
            var o = w.Spawn("t", 0, 0, ["T"]);
            o.Width = 80;
            w.Frame(Dt);
            NoErrors(w);
            var ls = o.Labels!;
            Eq(3, ls.Count);
            Eq("top left", ls[0].Anchor); Eq("#2F6FD0", ls[0].Back); Eq(-4.0, ls[0].Dx); Eq("5", ls[0].TextNow(), "set again by name");
            Eq("bottom right", ls[1].Anchor); Eq(null, ls[1].Back); Eq("3", ls[1].TextNow());
            Eq("top", ls[2].Anchor);
            Eq(80.0, ls[0].BaseWidth);
            Eq("#FFD54A", o.Outline); Eq(3.0, o.OutlineWidth);
            w.RunFor(0.3);
            Eq(2, o.Labels!.Count, "'label \"name\" none' removed it");
            Eq("7", o.Labels[1].TextNow(), "{hp} follows the variable");
            var w2 = NewWorld(M("when start\n    label \"x\" \"1\" at middle left\n    label none\n    outline off"));
            var o2 = w2.Spawn("t", 0, 0, ["T"]);
            o2.Outline = "#000000";
            w2.Frame(Dt);
            NoErrors(w2);
            Eq(null, o2.Labels); Eq(null, o2.Outline);
            var w3 = NewWorld(M("when start\n    label \"x\" \"1\" at sideways"));
            w3.Spawn("t", 0, 0, ["T"]);
            w3.Frame(Dt);
            True(w3.Errors.Count == 1 && w3.Errors[0].Contains("top left"), "a wrong place is a friendly error: " + string.Join(" | ", w3.Errors));
        });

        Console.WriteLine("Sample: Ember Duel (a card battler in Builder Script)");
        test("the sample writes a project: mechanisms, scene, 30 card pictures, sounds", () =>
        {
            using var s = new Sample();
            True(Project.Exists(s.Folder), "project.json");
            foreach (var m in new[] { "CardBook", "CardView", "CardZoom", "Duel", "RivalAI", "DeckBuilder", "Lane", "Hero", "Banner", "FloatText" })
                True(s.Project.Mechanisms.ContainsKey(m), "mechanism " + m);
            foreach (var (name, src) in s.Project.Mechanisms)
            {
                var defs = Parser.Parse(src);
                Eq(name, defs[0].Name, "file name = mechanism name");
                string printed = Printer.Print(defs);
                Eq(printed, Printer.Print(Parser.Parse(printed)), name + " prints and parses back the same");
            }
            var images = Images.List(s.Folder);
            Eq(30 + 7, images.Count, "30 card faces + back, table, 2 lanes, 2 heroes, banner");
            True(Sounds.List(s.Folder).Count >= 10, "sounds");
            var png = File.ReadAllBytes(Path.Combine(Images.Folder(s.Folder), "Cinder Pup.png"));
            Eq(0x89, (int)png[0], "a PNG");
            Eq(76, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], "card width");
        });
        test("the title shows, the book has 30 cards, the starter deck has 20", () =>
        {
            using var s = new Sample();
            var w = s.Play();
            w.RunFor(0.5);
            NoErrors(w);
            True(w.ScreenShown("Title") && !w.ScreenShown("Battle"), "the title screen");
            var cards = (List<object?>)w.FindObject("Book")!.Mechanisms[0].Vars["cards"]!;
            Eq(30, cards.Count);
            Eq(30, cards.Cast<Record>().Select(c => c["name"]).Distinct().Count(), "names are different");
            True(cards.Cast<Record>().All(c => File.Exists(Path.Combine(Images.Folder(s.Folder), c["name"] + ".png"))), "every card has its picture");
            var chosen = (List<object?>)w.FindObject("Deck Builder")!.Mechanisms[0].Vars["chosen"]!;
            Eq(20, chosen.Count);
        });
        test("fight: decks of 20, an opening hand, mana 1 on turn 1", () =>
        {
            using var s = new Sample();
            var w = StartFight(s);
            NoErrors(w);
            var you = Player(w, "you");
            var rival = Player(w, "rival");
            Eq(20, ((List<object?>)you["deck"]!).Count + Hand(you).Count, "your 20 cards");
            Eq(4, Hand(you).Count, "3 + 1 drawn");
            Eq(4, Hand(rival).Count, "the rival's 4");
            Eq(1.0, you["mana"]); Eq(1.0, you["max_mana"]); Eq(20.0, you["hp"]);
            True(w.ScreenShown("Battle") && !w.ScreenShown("Title"), "the battle screen");
            var playable = Hand(you).Cast<GameObject>().Where(c => c.Draggable).ToList();
            True(playable.All(c => N(CardOf(c)["cost"]) <= 1), "only cards you can pay for can be dragged");
            True(playable.All(c => c.Outline != null), "playable cards glow");
            True(Hand(rival).Cast<GameObject>().All(c => c.Image == "Card Back" && c.Labels == null), "the rival's hand is face down");
        });
        test("dragging a creature onto a lane pays its cost and fills the lane", () =>
        {
            using var s = new Sample();
            var w = StartFight(s, AllCreatures);
            var you = Player(w, "you");
            you["mana"] = 10.0;
            w.Broadcast("arrange", []);
            w.RunFor(0.3);
            var card = Hand(you).Cast<GameObject>().First();
            double cost = N(CardOf(card)["cost"]);
            var lane = w.FindObject("Lane 2")!;
            True(lane.Tags.Contains("open_lane"), "the lane is open");
            Drag(w, card, lane.X + lane.Width / 2, lane.Y + lane.Height / 2);
            w.RunFor(0.6);
            NoErrors(w);
            Eq(10 - cost, N(you["mana"]), "mana paid");
            Eq((object?)card, LaneUnit(lane), "the lane holds the card");
            True(!Hand(you).Contains(card), "it left the hand");
            True(!lane.Tags.Contains("open_lane") && card.Tags.Contains("friend") && !card.Draggable, "tags and drag");
            True(Math.Abs(Center(card).X - Center(lane).X) < 1 && Math.Abs(Center(card).Y - Center(lane).Y) < 1, "it sits in the lane");
            // A card dropped on nothing goes back to the hand.
            var other = Hand(you).Cast<GameObject>().First();
            Drag(w, other, 300, 160);
            w.RunFor(0.5);
            True(Hand(you).Contains(other), "still in the hand");
            True(other.Y > 300, "back in the hand row");
        });
        test("a damage spell dragged onto the rival hurts the rival; shields block", () =>
        {
            using var s = new Sample();
            var w = StartFight(s);
            var you = Player(w, "you");
            var rival = Player(w, "rival");
            you["mana"] = 10.0;
            var cards = (List<object?>)w.FindObject("Book")!.Mechanisms[0].Vars["cards"]!;
            var bolt = cards.Cast<Record>().First(c => Equals(c["name"], "Spark Bolt")).Copy();
            var card = Hand(you).Cast<GameObject>().First();
            card.Mechanisms[0].Vars["card"] = bolt;
            w.Broadcast("arrange", []);
            w.RunFor(0.3);
            True(card.Draggable && card.DropZone == "foe", "a damage spell goes onto an enemy");
            var hero = w.FindObject("Rival")!;
            rival["shield"] = true;
            Drag(w, card, hero.X + 50, hero.Y + 50);
            w.RunFor(1.0);
            NoErrors(w);
            Eq(20.0, rival["hp"], "the shield blocked it");
            Eq(false, rival["shield"]);
            True(card.Destroyed, "the spell is used up");
            Eq(9.0, N(you["mana"]));
            var card2 = Hand(you).Cast<GameObject>().First();
            card2.Mechanisms[0].Vars["card"] = bolt.Copy();
            w.Broadcast("arrange", []);
            w.RunFor(0.3);
            Drag(w, card2, hero.X + 50, hero.Y + 50);
            w.RunFor(1.0);
            Eq(18.0, rival["hp"], "2 damage");
        });
        test("End turn: your creatures attack across their lanes, then the rival plays legal turns", () =>
        {
            using var s = new Sample();
            var w = StartFight(s, AllCreatures);
            var you = Player(w, "you");
            var rival = Player(w, "rival");
            you["mana"] = 10.0;
            w.Broadcast("arrange", []);
            w.RunFor(0.3);
            var card = Hand(you).Cast<GameObject>().First();
            Drag(w, card, w.FindObject("Lane 1")!.X + 44, w.FindObject("Lane 1")!.Y + 58);
            w.RunFor(1.0);
            var rec = CardOf(card);
            double attack = N(rec["attack"]) + (Equals(rec["effect"], "damage") ? N(rec["amount"]) : 0); // some creatures hit when played
            Click(w, w.FindObject("End Turn")!);
            RunUntil(w, () => Equals(DuelVars(w)["turn"], "rival"), 10, "the rival's turn");
            NoErrors(w);
            Eq(20 - attack, N(rival["hp"]), "the empty lane let it hit the rival");
            RunUntil(w, () => Equals(DuelVars(w)["turn"], "you"), 30, "your second turn");
            NoErrors(w);
            Eq(1.0, rival["max_mana"], "the rival's first turn");
            True(N(rival["mana"]) >= 0 && N(rival["mana"]) <= 1, "the rival never spends mana it doesn't have");
            var rivalLanes = Enumerable.Range(1, 4).Select(i => w.FindObject($"Rival Lane {i}")!).ToList();
            var units = rivalLanes.Select(LaneUnit).OfType<GameObject>().ToList();
            True(units.All(u => u.Tags.Contains("foe") && CardOf(u)["kind"] is "creature"), "only creatures on the rival's lanes");
            True(units.Sum(u => N(CardOf(u)["cost"])) <= 1, "what it played cost what it had (1 mana)");
            True(Hand(rival).Count <= 7, "a legal hand");
            Eq(2.0, you["max_mana"]);
        });
        test("the computer picks the best play: it finishes the weakest creature with a damage spell", () =>
        {
            using var s = new Sample();
            var w = StartFight(s, AllCreatures);
            var you = Player(w, "you");
            you["mana"] = 10.0;
            w.Broadcast("arrange", []);
            w.RunFor(0.3);
            // Two creatures of yours on the table: the weaker one should be the target.
            var hand = Hand(you).Cast<GameObject>().ToList();
            Drag(w, hand[0], w.FindObject("Lane 1")!.X + 44, w.FindObject("Lane 1")!.Y + 58);
            w.RunFor(0.8);
            you["mana"] = 10.0; // enough for the second one too
            w.Broadcast("arrange", []);
            w.RunFor(0.3);
            Drag(w, hand[1], w.FindObject("Lane 3")!.X + 44, w.FindObject("Lane 3")!.Y + 58);
            w.RunFor(0.8);
            True(LaneUnit(w.FindObject("Lane 1")!) == hand[0] && LaneUnit(w.FindObject("Lane 3")!) == hand[1], "both creatures were played");
            CardOf(hand[0])["health"] = 5.0; CardOf(hand[0])["attack"] = 1.0;
            CardOf(hand[1])["health"] = 2.0; CardOf(hand[1])["attack"] = 3.0;
            CardOf(hand[0])["shield"] = false; CardOf(hand[1])["shield"] = false; // some creatures shield themselves when played
            var rival = Player(w, "rival");
            var cards = (List<object?>)w.FindObject("Book")!.Mechanisms[0].Vars["cards"]!;
            Record Named(string n) => cards.Cast<Record>().First(x => Equals(x["name"], n)).Copy();
            foreach (var c in Hand(rival).Cast<GameObject>()) c.Mechanisms[0].Vars["card"] = Named("Reef Crab");
            var bolt = (GameObject)Hand(rival)[0]!;
            bolt.Mechanisms[0].Vars["card"] = Named("Spark Bolt");
            Click(w, w.FindObject("End Turn")!);
            RunUntil(w, () => Equals(DuelVars(w)["turn"], "you"), 30, "your next turn");
            NoErrors(w);
            string seen = $"you played {CardOf(hand[0])["name"]} + {CardOf(hand[1])["name"]}; rival hand now {string.Join(", ", Hand(rival).Cast<GameObject>().Select(c => CardOf(c)["name"]))}; rival lanes {string.Join(", ", Enumerable.Range(1, 4).Select(i => LaneUnit(w.FindObject($"Rival Lane {i}")!) is GameObject u ? CardOf(u)["name"] : "-"))}";
            True(bolt.Destroyed, "it cast the bolt (its best play): " + seen);
            True(hand[1].Destroyed, "at the weakest creature, which it finished off");
            True(!hand[0].Destroyed && N(CardOf(hand[0])["health"]) == 5, "the sturdy one wasn't touched");
        });
        test("deck builder: click cards in and out, save the deck, the next game loads it", () =>
        {
            using var s = new Sample();
            var w = s.Play();
            Click(w, w.FindObject("Build Button")!);
            w.RunFor(0.5);
            NoErrors(w);
            True(w.ScreenShown("Build") && !w.ScreenShown("Title"), "the deck screen");
            var pool = w.Objects.Where(o => o.Tags.Contains("pool")).ToList();
            Eq(30, pool.Count, "every card in the grid");
            True(pool.Zip(pool.Skip(1)).All(p => N(CardOf(p.First)["cost"]) <= N(CardOf(p.Second)["cost"])), "cheapest first");
            var builder = w.FindObject("Deck Builder")!.Mechanisms[0].Vars;
            List<object?> Chosen() => (List<object?>)builder["chosen"]!;
            var inDeck = pool.First(c => Chosen().Contains(CardOf(c)["name"]));
            var outOfDeck = pool.First(c => !Chosen().Contains(CardOf(c)["name"]));
            True(inDeck.Outline != null && inDeck.Opacity > 0.9 && outOfDeck.Opacity < 0.6, "cards in the deck are lit");
            Click(w, inDeck);
            Eq(19, Chosen().Count, "taken out");
            Eq("Your deck: 19 / 20", w.FindObject("Deck Count")!.Caption);
            Click(w, w.FindObject("Save Deck")!);
            True(!w.ScreenShown("Battle"), "a deck of 19 can't be saved");
            Click(w, outOfDeck);
            Eq(20, Chosen().Count, "put in");
            Click(w, outOfDeck);
            Click(w, outOfDeck);
            var name = CardOf(outOfDeck)["name"];
            Click(w, w.FindObject("Save Deck")!);
            w.RunFor(0.3);
            True(w.ScreenShown("Battle"), "saved and off to fight");
            var saved = (List<object?>)w.SavedValue("deck")!;
            Eq(20, saved.Count);
            True(saved.Contains(name) && !saved.Contains(CardOf(inDeck)["name"]), "the saved deck is the chosen one");
            NoErrors(w);
            // A new game (same save folder) starts with it.
            var w2 = s.Play(deck: saved);
            Eq(string.Join(",", saved.Select(World.ToText)), string.Join(",", ((List<object?>)w2.FindObject("Deck Builder")!.Mechanisms[0].Vars["chosen"]!).Select(World.ToText)));
        });
        test("computer against computer: a whole match ends with a winner and the result screen", () =>
        {
            using var s = new Sample();
            var w = s.Play(new() { ["auto_player"] = true });
            w.Broadcast("fight", []);
            var vars = DuelVars(w);
            int turns = 0;
            string lastTurn = "";
            bool illegal = false;
            RunUntil(w, () =>
            {
                string t = World.ToText(vars["turn"]);
                if (t != lastTurn && t is "you" or "rival") turns++;
                foreach (var who in new[] { "you", "rival" })
                    if (vars.GetValueOrDefault(who) is Record p && (N(p["mana"]) < 0 || Hand(p).Count > 7 || ((List<object?>)p["lanes"]!).Count != 4)) illegal = true;
                lastTurn = t;
                return w.ScreenShown("Result") || turns > 80;
            }, 1500, "the end of the match");
            True(!illegal, "mana never below 0, hands of 7 at most, 4 lanes each");
            NoErrors(w);
            string winner = World.ToText(vars["winner"]);
            True(winner is "you" or "rival", "a winner: " + winner);
            True(turns <= 80, $"it ended within 40 rounds ({turns} turns)");
            var loser = Player(w, winner == "you" ? "rival" : "you");
            True(N(loser["hp"]) <= 0, "the loser has no health left");
            True((w.FindObject("Result Panel")!.Caption ?? "").Contains(winner == "you" ? "Victory" : "Defeat"), "the result says who won");
            Eq(1.0, N(w.SavedValue(winner == "you" ? "wins" : "losses")), "the score is kept");
            // Play again: a fresh match, nothing left over from the last one.
            Click(w, w.FindObject("Play Again")!);
            RunUntil(w, () => Equals(vars["turn"], "you") && !w.ScreenShown("Result") && Hand(Player(w, "you")).Count == 4, 10, "a new match");
            Eq(20.0, Player(w, "you")["hp"]); Eq(20.0, Player(w, "rival")["hp"]);
            var units = Enumerable.Range(1, 4).SelectMany(i => new[] { LaneUnit(w.FindObject($"Lane {i}")!), LaneUnit(w.FindObject($"Rival Lane {i}")!) }).OfType<GameObject>().ToList();
            var current = Hand(Player(w, "you")).Concat(Hand(Player(w, "rival"))).Cast<GameObject>().Concat(units).ToHashSet();
            True(w.Objects.Where(o => o.Tags.Contains("battle_card") && !o.Destroyed && o.Opacity > 0.99).All(current.Contains), "no cards left over from the last match");
            True(units.Count <= 1, "at most the one creature played this turn");
            NoErrors(w);
        });
        test("leaving a match for the title and fighting again starts fresh", () =>
        {
            using var s = new Sample();
            var w = StartFight(s);
            var first = Player(w, "you");
            Click(w, w.FindObject("Menu")!);
            w.RunFor(0.5);
            True(w.ScreenShown("Title") && !w.ScreenShown("Battle"), "back at the title");
            Eq(false, DuelVars(w)["running"], "the old match stopped");
            Click(w, w.FindObject("Fight Button")!);
            RunUntil(w, () => Equals(DuelVars(w)["turn"], "you") && Hand(Player(w, "you")).Count == 4, 10, "the new match");
            True(!ReferenceEquals(first, Player(w, "you")), "a new player record");
            Eq(1.0, Player(w, "you")["mana"]);
            NoErrors(w);
        });
    }
}
