using BuilderEngine.Script;

/// <summary>Behavior packages (.behavior, docs/BEHAVIOR-FORMAT.md): reading, validating, writing back, discovering.</summary>
static class BehaviorPackageTests
{
    public const string Spin = """
    {
      "format": "builderengine.behavior", "version": 1,
      "name": "Spin", "replaces": "Spin", "icon": "🌀", "category": "Movement",
      "description": "Turns the object round and round.",
      "params": [
        { "name": "speed", "label": "Speed", "type": "number", "default": 90, "min": -1080, "max": 1080 },
        { "name": "direction", "type": "Choice \"clockwise\", \"counter\"", "default": "clockwise", "showIf": "speed != 0" }
      ],
      "ai": { "keywords": ["spin", "girar"], "guide": "Makes things turn." },
      "community": { "stars": 5 },
      "script": "mechanism Spin\n    param speed: number = 90\n    param direction: Choice \"clockwise\", \"counter\" = \"clockwise\"\n    param extra: bool = true\n\n    when tick\n        if direction == \"clockwise\"\n            self.angle += speed * dt\n        else\n            self.angle -= speed * dt\n"
    }
    """;

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Behavior packages (BehaviorPackage.cs)");

        test("a valid package reads, validates and lists every setting", () =>
        {
            var p = BehaviorPackage.Parse(Spin);
            Check(p.Validate().Count == 0, string.Join(" | ", p.Validate()));
            Check(p.Name == "Spin" && p.Replaces == "Spin" && p.Icon == "🌀", "manifest");
            Check(p.Params[1].Options.SequenceEqual(["clockwise", "counter"]), "choice options");
            var all = p.AllParams();
            Check(all.Count == 3 && all[2].Name == "extra" && all[2].Kind == ParamKind.Bool, "script-only param is listed with its default");
        });

        test("writing back keeps unknown properties and reads the same", () =>
        {
            var p = BehaviorPackage.Parse(Spin);
            string json = p.ToJson();
            Check(json.Contains("\"community\""), "unknown property kept");
            var q = BehaviorPackage.Parse(json);
            Check(q.ToJson() == json, "stable round-trip");
            Check(q.Params.Count == 2 && q.Params[0].Min == -1080 && q.AiKeywords.Contains("girar"), "fields survive");
        });

        test("the package runs as a mechanism", () =>
        {
            var p = BehaviorPackage.Parse(Spin);
            var w = new World();
            w.Load(p.Script);
            var o = w.Spawn("saw", 0, 0, ["Spin"]);
            for (int i = 0; i < 60; i++) w.Step(1.0 / 60);
            Check(w.Errors.Count == 0, string.Join(" | ", w.Errors));
            Check(Math.Abs(o.Angle - 90) < 3, $"turned 90 degrees in a second (got {o.Angle})");
        });

        test("broken packages are refused with every problem in plain words", () =>
        {
            Throws(() => BehaviorPackage.Parse("{ not json"), "isn't valid JSON");
            Throws(() => BehaviorPackage.Parse("{\"format\":\"other\"}"), "Not a behavior package");
            Throws(() => BehaviorPackage.Parse("{\"format\":\"builderengine.behavior\",\"version\":99}"), "newer version");
            var bad = BehaviorPackage.Parse("""
            { "format": "builderengine.behavior", "version": 1, "name": "Mover",
              "params": [ { "name": "speed", "type": "text", "default": 3 }, { "name": "ghost", "type": "number" },
                          { "name": "mode", "type": "Choice \"a\", \"b\"", "default": "c", "showIf": "speed >" } ],
              "script": "mechanism Other\n    param speed: number = 1\n" }
            """);
            var errs = bad.Validate();
            Check(errs.Any(e => e.Contains("called \"Other\"")), "mechanism name mismatch");
            Check(errs.Any(e => e.Contains("default of \"speed\"")), "default type");
            Check(errs.Any(e => e.Contains("default of \"mode\"")), "choice default not an option");
            Check(errs.Any(e => e.Contains("\"show if\" of \"mode\"")), "showIf parse error");
        });

        test("a package made from a mechanism validates", () =>
        {
            var p = BehaviorPackage.FromMechanism("mechanism Blinker\n    param every: seconds = 0.5\n    when start\n        repeat forever\n            wait every\n");
            Check(p.Validate().Count == 0 && p.Params.Single().Type == "seconds", string.Join(" | ", p.Validate()));
        });

        test("discovery: the first folder wins a name clash, broken files are reported", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "BE_Test_Behaviors_" + Guid.NewGuid().ToString("N"));
            try
            {
                string proj = Path.Combine(root, "project"), inst = Path.Combine(root, "installed");
                Directory.CreateDirectory(proj); Directory.CreateDirectory(inst);
                var mine = BehaviorPackage.Parse(Spin); mine.Description = "project copy"; mine.Save(Path.Combine(proj, "Spin.behavior"));
                BehaviorPackage.Parse(Spin).Save(Path.Combine(inst, "Spin.behavior"));
                File.WriteAllText(Path.Combine(inst, "Broken.behavior"), "{ nope");
                var problems = new List<string>();
                var found = BehaviorPackage.Discover([proj, inst], problems);
                Check(found.Count == 1 && found[0].Description == "project copy", "project copy wins");
                Check(problems.Count == 1 && problems[0].StartsWith("Broken.behavior"), "broken one reported");
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        });
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    static void Throws(Action a, string fragment)
    {
        try { a(); }
        catch (FormatException e)
        {
            if (!e.Message.Contains(fragment)) throw new Exception($"message \"{e.Message}\" doesn't mention \"{fragment}\"");
            return;
        }
        throw new Exception($"expected an error mentioning \"{fragment}\"");
    }
}
