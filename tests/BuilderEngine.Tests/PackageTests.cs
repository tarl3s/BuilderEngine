using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BuilderEngine.Script;

/// <summary>
/// Behavior packages in use (BehaviorPackage.Finding.cs, World.Packages.cs, GameBoot.UseProject, EasyCompat): every
/// shipped package validates; each EasyEngine behavior that moved from EasyCompat's hand-made code to a shipped package
/// plays frame for frame like the old mapping did (the old EasyKit mechanisms are kept below as they were, renamed
/// Old&lt;Kind&gt;, and played through the same EasyCompat as "Package" behaviors with the params the old C# gave them); so does every
/// scene of the owner's EasyEngine projects found on this computer (copies of Desktop\Jogos\*\*.eeproj);
/// project / installed / shipped precedence; a "Package" EasyEngine behavior; an exported game keeps behaviors/;
/// "Save as behavior" read back; the packages in a plain BuilderEngine project; "show if".
/// </summary>
static class PackageTests
{
    const double Dt = 1.0 / 60;

    static void True(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    static readonly List<string> Made = [];

    static string NewDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "BE_Pkg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        Made.Add(d);
        return d;
    }

    // ---------------- Small EasyEngine projects (like EasyCompatTests) ----------------

    static JsonObject Beh(string kind, object? nums = null, object? texts = null)
    {
        var b = new JsonObject { ["Kind"] = kind, ["Nums"] = new JsonObject(), ["Texts"] = new JsonObject() };
        if (nums != null) foreach (var p in nums.GetType().GetProperties()) b["Nums"]![p.Name] = JsonValue.Create(Convert.ToDouble(p.GetValue(nums)));
        if (texts != null) foreach (var p in texts.GetType().GetProperties()) b["Texts"]![p.Name] = JsonValue.Create(p.GetValue(texts)?.ToString());
        return b;
    }

    static int _id = 1;

    static JsonObject Obj(string name, double x, double y, double w, double h, string physics, params JsonObject[] behaviors)
    {
        var arr = new JsonArray();
        foreach (var b in behaviors) arr.Add(b);
        return new JsonObject
        {
            ["Id"] = _id++, ["Name"] = name, ["Type"] = name == "Player" ? "Player" : "Object", ["X"] = x, ["Y"] = y, ["Width"] = w, ["Height"] = h,
            ["Color"] = "#9A9AA0", ["SpritePath"] = null, ["Opacity"] = 1, ["Animations"] = new JsonArray(),
            ["Physics"] = new JsonObject { ["Mode"] = physics, ["GravityScale"] = 1, ["Bounciness"] = 0 },
            ["Behaviors"] = arr, ["Variables"] = new JsonObject(),
        };
    }

    static JsonObject Obj(string name, double x, double y, double w, double h, params JsonObject[] behaviors) => Obj(name, x, y, w, h, "None", behaviors);

    static JsonObject SceneOf(string name, double w, double h, params JsonObject[] objects)
    {
        var objs = new JsonArray();
        foreach (var o in objects) objs.Add(o);
        return new JsonObject
        {
            ["Name"] = name, ["Width"] = w, ["Height"] = h, ["BackgroundColor"] = "#202020", ["AmbientLight"] = 1, ["Variables"] = new JsonObject(),
            ["Layers"] = new JsonArray
            {
                new JsonObject { ["Name"] = "Background", ["Kind"] = "Background", ["ScreenSpace"] = false, ["Objects"] = new JsonArray() },
                new JsonObject { ["Name"] = "Base", ["Kind"] = "Base", ["ScreenSpace"] = false, ["Objects"] = objs },
                new JsonObject { ["Name"] = "UI", ["Kind"] = "UI", ["ScreenSpace"] = true, ["Objects"] = new JsonArray() },
            },
        };
    }

    static JsonObject EeOf(params JsonObject[] scenes)
    {
        var arr = new JsonArray();
        foreach (var s in scenes) arr.Add(s);
        return new JsonObject { ["Name"] = "Package test", ["ScreenWidth"] = 960, ["ScreenHeight"] = 540, ["Scenes"] = arr, ["Variables"] = new JsonObject() };
    }

    static Project ProjectOf(JsonObject ee) => EasyLink.FromEeproj(ee.ToJsonString(), "C:\\nowhere");

    static JsonObject Player(double x, double y, params JsonObject[] extra) =>
        Obj("Player", x, y, 40, 60, "Dynamic", [Beh("Platformer", new { controls = 1, speed = 260, jump = 640 }), .. extra]);

    static JsonObject Ground(double y = 500, double w = 2000) => Obj("Ground", 0, y, w, 40, Beh("Solid"));

    // ---------------- Playing, frame by frame ----------------

    /// <summary>Plays a project for some frames; one line per frame: the scene, every live object and every variable.</summary>
    static List<string> Trace(Project p, string dir, int frames, Action<World, int>? input = null, string? start = null)
    {
        var w = GameBoot.NewWorld(p, dir, Path.Combine(dir, "saves"));
        // the same dice in both worlds ('random' in a real project), so two plays can be compared frame by frame
        typeof(World).GetField("Rng", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(w, new Random(7));
        var errors = new List<string>(w.Errors);
        string? next = null;
        w.SceneRequested += n => next = n;
        string scene = start ?? p.Scenes[0].Name;
        w.LoadScene(p.FindScene(scene)!.Clone());
        var trace = new List<string>();
        for (int i = 0; i < frames; i++)
        {
            input?.Invoke(w, i);
            w.Frame(Dt);
            if (next != null)
            {
                errors.AddRange(w.Errors);
                var nw = w.ForNextScene();
                scene = next;
                next = null;
                w = nw;
                w.LoadScene(p.FindScene(scene)!.Clone());
            }
            trace.Add(scene + " | " + Snapshot(w));
        }
        errors.AddRange(w.Errors);
        True(errors.Count == 0, "script errors: " + string.Join(" | ", errors.Distinct()));
        return trace;
    }

    static string Snapshot(World w)
    {
        var sb = new StringBuilder();
        string F(double d) => Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture);
        foreach (var o in w.Objects.Where(o => !o.Destroyed).OrderBy(o => o.Id))
            sb.Append(o.Name).Append('@').Append(F(o.X)).Append(',').Append(F(o.Y)).Append(" a").Append(F(o.Angle))
              .Append(" o").Append(F(o.Opacity)).Append(o.Solid ? " S" : "").Append(o.OneWay ? " 1W" : "").Append("; ");
        foreach (var (k, v) in w.Globals.OrderBy(g => g.Key, StringComparer.Ordinal))
            if (v is double or string or bool) sb.Append(k).Append('=').Append(World.ToText(v)).Append(' ');
        return sb.ToString();
    }

    // ---------------- The old hand-made mapping ----------------

    /// <summary>
    /// The EasyKit mechanisms the shipped packages replaced, exactly as they were before (StarterKit/EasyEngine/*.bs),
    /// renamed Old&lt;Kind&gt;. They play here as project packages, so both worlds go through the same EasyCompat.
    /// </summary>
    const string OldKit = """
mechanism OldProjectile
    # Projectile: flies in a direction (degrees, 0 = right, 90 = down) and disappears after 'life' seconds (0 = never).
    param speed: number = 420
    param angle: number = 0
    param life: number = 3

    when start
        if self.vx == 0 and self.vy == 0
            self.vx = cos(angle) * speed
            self.vy = sin(angle) * speed
        if life > 0
            wait life
            destroy self

mechanism OldStayInside
    # Stay Inside: kept inside the scene (or what the camera shows) after it moves: it stops, bounces or wraps around.
    param area: text = "scene"
    param sides: text = "all"
    param edge: text = "stop"

    when start
        # a forever loop goes one round per frame, after physics — where EasyEngine keeps it inside
        repeat forever
            x0 = 0
            y0 = 0
            x1 = scene_width
            y1 = scene_height
            if area == "screen"
                x0 = camera_x - view_width / 2
                y0 = camera_y - view_height / 2
                x1 = x0 + view_width
                y1 = y0 + view_height
            lr = sides == "all" or sides == "leftright" or sides == "notbottom"
            top = sides == "all" or sides == "topbottom" or sides == "notbottom"
            bottom = sides == "all" or sides == "topbottom"
            if edge == "wrap"
                if lr
                    if self.x > x1
                        self.x = x0 - self.width
                    else if self.x + self.width < x0
                        self.x = x1
                if bottom and self.y > y1
                    self.y = y0 - self.height
                else if top and self.y + self.height < y0
                    self.y = y1
            else
                k = 0
                if edge == "bounce": k = -1
                if lr
                    if self.x < x0
                        self.x = x0
                        if self.vx < 0: self.vx *= k
                    else if self.x + self.width > x1
                        self.x = x1 - self.width
                        if self.vx > 0: self.vx *= k
                if top and self.y < y0
                    self.y = y0
                    if self.vy < 0: self.vy *= k
                else if bottom and self.y + self.height > y1
                    self.y = y1 - self.height
                    if self.vy > 0: self.vy *= k

mechanism OldSpin
    # Spin: turns on its own (degrees per second).
    param speed: number = 90

    when tick
        self.angle += speed * dt

mechanism OldParallax
    # Parallax: a background that scrolls slower than the camera (0 = far away, 1 = moves with the scene).
    param factor: number = 0.5

    when start
        home_x = self.x
        home_y = self.y
        repeat forever
            k = 1 - clamp(factor, 0, 1)
            self.x = home_x + (camera_x - view_width / 2) * k
            self.y = home_y + (camera_y - view_height / 2) * k

mechanism OldGoToScene
    # Go To Scene: when the trigger touches it, the game goes to the scene.
    param scene: text = ""
    param trigger: text = "Player"
    param cond: text = ""

    when tick
        if scene == "" or not easy_on(cond): stop
        who = easy_find(trigger)
        if who and easy_contact(self, who): easy_goto scene

mechanism OldArea
    # Trigger Area / Zone: invisible; when the target enters — go to a scene, set or add to a variable, or say something.
    param action: text = "goscene"
    param scene: text = ""
    param var: text = ""
    param set_to: number = 1
    param amount: number = 1
    param text: text = ""
    param target: text = "Player"
    param once: yesno = true
    param cond: text = ""

    when start
        self.opacity = 0

    when tick
        if not easy_on(cond): stop
        who = easy_find(target)
        inside = who and easy_contact(self, who)
        fire = inside and not was
        was = inside
        if not fire or (once and fired): stop
        fired = true
        if action == "goscene"
            easy_goto scene
        else if action == "setvar"
            easy_set var, set_to
        else if action == "addvar"
            easy_add var, amount
        else if action == "say"
            run easy_say text, "", false

mechanism OldCollectible
    # Collectible: disappears when the collector touches it, adding its points to a variable.
    param collector: text = "Player"
    param var: text = ""
    param points: number = 1
    param item: text = ""
    param amount: number = 1
    param cond: text = ""

    when tick
        if not easy_on(cond): stop
        who = easy_find(collector)
        if not who or not easy_touching(self, who): stop
        if item != "": easy_add "item:" + item, amount
        if var != "": easy_add var, points
        destroy self

mechanism OldAbilityPickup
    # Ability / Key Pickup: when the collector touches it, a variable turns on (and maybe an ability), and it disappears.
    param var: text = "hasDoubleJump"
    param collector: text = "Player"
    param grant: text = "none"
    param cond: text = ""

    when tick
        if not easy_on(cond): stop
        who = easy_find(collector)
        if not who or not easy_contact(self, who): stop
        easy_set var, 1
        if grant != "none": easy_ability who, grant, true
        destroy self

mechanism OldGate
    # Gate / Locked Door: solid until its variable meets the condition (or the player has the item); then it fades.
    param var: text = "hasKey"
    param op: text = ">="
    param value: number = 1
    param item: text = ""
    param cond: text = ""

    when start
        solid

    when tick
        if not easy_on(cond): stop
        if item != ""
            open = easy_var("item:" + item) >= 1
        else
            open = easy_compare(easy_var(var), op, value)
        solid not open
        if open
            self.opacity = 0.2
        else
            self.opacity = 1

mechanism OldSwitch
    # Switch / Lever: flips a variable between 0 and 1 — clicked, touched, or stood on with a key.
    param var: text = "switch1"
    param mode: text = "click"
    param trigger: text = "Player"
    param key: text = "e"
    param stay_on: yesno = false
    param cond: text = ""

    when clicked
        if mode == "click" and easy_on(cond): flip

    when tick
        if mode == "click" or not easy_on(cond): stop
        who = easy_find(trigger)
        near = who and easy_contact(self, who)
        active = near
        if mode == "interact": active = near and easy_key(key)
        go = active and not was
        was = active
        if go: flip

    to flip
        v = 1
        if not stay_on and easy_var(var) >= 0.5: v = 0
        easy_set var, v
        if v >= 0.5
            self.opacity = 1
        else
            self.opacity = 0.55

mechanism OldSpring
    # Spring / Bounce Pad: launches what lands on it (anything that falls) up, down, left or right.
    param force: number = 820
    param dir: text = "up"
    param cond: text = ""

    when start
        solid

    when tick
        if not easy_on(cond): stop
        for each o in easy_bodies()
            if o != self and easy_near_pad(self, o, dir)
                if dir == "down"
                    o.vy = force
                else if dir == "left"
                    o.vx = -force
                else if dir == "right"
                    o.vx = force
                else
                    o.vy = -force

mechanism OldSpawner
    # Spawner: makes copies of an object or prefab — every 'interval' seconds, or each time a key is pressed. max 0 = no limit.
    param target: text = "Enemy"
    param interval: number = 1.5
    param max_count: number = 10
    param key: text = ""
    param spawn_at: text = "self"
    param cond: text = ""

    when tick
        if not easy_on(cond): stop
        room = max_count <= 0 or made < max_count
        if key != ""
            down = easy_key(key)
            if down and not was and room
                easy_spawn target, spawn_at
                made += 1
            was = down
        else
            clock += dt
            if clock >= max(0.05, interval) and room
                clock = 0
                easy_spawn target, spawn_at
                made += 1

mechanism OldDestroyOffscreen
    # Destroy Off-screen: removed when it leaves what the camera shows (by more than 'margin' pixels).
    param margin: number = 120

    when tick
        view_left = camera_x - view_width / 2 - margin
        view_top = camera_y - view_height / 2 - margin
        if self.x + self.width < view_left or self.x > view_left + view_width + 2 * margin or self.y + self.height < view_top or self.y > view_top + view_height + 2 * margin
            destroy self
""";

    /// <summary>
    /// An EasyEngine behavior as the deleted C# mapping in EasyCompat.MechanismsFor gave it to its old mechanism: a
    /// "Package" behavior naming Old&lt;Kind&gt; with the very params it computed (keys already in Builder Script's names).
    /// Null for a kind that didn't move.
    /// </summary>
    static JsonObject? OldOf(JsonObject b)
    {
        string kind = (string)b["Kind"]!;
        var nums = (JsonObject)b["Nums"]!;
        var texts = (JsonObject)b["Texts"]!;
        double N(string k, double d) => nums[k] is not JsonValue v ? d
            : v.TryGetValue<double>(out var x) ? x : v.TryGetValue<bool>(out var y) ? (y ? 1 : 0)
            : v.TryGetValue<string>(out var s) && double.TryParse(s.Replace(',', '.'), CultureInfo.InvariantCulture, out var z) ? z : d;
        string T(string k, string d) => texts[k] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : d;
        bool On(string k, double d) => N(k, d) >= 0.5;
        string Key(string k) => k.Trim().ToLowerInvariant() is "none" ? "" : k.Trim().ToLowerInvariant();
        string cond = T("cond", "").Trim();
        var on = new JsonObject();
        var ot = new JsonObject();
        void Num(string name, double v) => on[name] = v;
        void Txt(string name, string v) => ot[name] = v;
        void Yes(string name, bool v) => on[name] = v ? 1 : 0;
        switch (kind)
        {
            case "StayInside": Txt("area", T("area", "scene")); Txt("sides", T("sides", "all")); Txt("edge", T("edge", "stop")); break;
            case "GoToScene": Txt("scene", T("scene", "")); Txt("trigger", T("trigger", "Player")); Txt("cond", cond); break;
            case "Area":
                Txt("action", T("action", "goscene").ToLowerInvariant()); Txt("scene", T("scene", "")); Txt("var", T("var", ""));
                Num("set_to", N("to", 1)); Num("amount", N("amount", 1)); Txt("text", T("text", "")); Txt("target", T("target", "Player"));
                Yes("once", On("once", 1)); Txt("cond", cond);
                break;
            case "Spin": Num("speed", N("speed", 90)); break;
            case "Projectile": Num("speed", N("speed", 420)); Num("angle", N("angle", 0)); Num("life", N("life", 3)); break;
            case "Collectible":
                Txt("collector", T("collector", "Player")); Txt("var", T("var", "")); Num("points", N("points", 1));
                Txt("item", T("item", "")); Num("amount", N("amount", 1)); Txt("cond", cond);
                break;
            case "Spawner":
                Txt("target", T("target", "Enemy")); Num("interval", N("interval", 1.5)); Num("max_count", N("max", 10));
                Txt("key", Key(T("key", ""))); Txt("spawn_at", T("at", "self")); Txt("cond", cond);
                break;
            case "AbilityPickup": Txt("var", T("var", "hasDoubleJump")); Txt("collector", T("collector", "Player")); Txt("grant", T("grant", "none")); Txt("cond", cond); break;
            case "Gate": Txt("var", T("var", "hasKey")); Txt("op", T("op", ">=")); Num("value", N("value", 1)); Txt("item", T("item", "")); Txt("cond", cond); break;
            case "Switch":
                Txt("var", T("var", "switch1")); Txt("mode", T("mode", "click").ToLowerInvariant()); Txt("trigger", T("trigger", "Player"));
                Txt("key", Key(T("key", "E"))); Yes("stay_on", On("stayOn", 0)); Txt("cond", cond);
                break;
            case "Spring": Num("force", N("force", 820)); Txt("dir", T("dir", "up").ToLowerInvariant()); Txt("cond", cond); break;
            case "Parallax": Num("factor", N("factor", 0.5)); break;
            case "DestroyOffscreen": Num("margin", N("margin", 120)); break;
            default: return null;
        }
        ot["package"] = "Old" + kind;
        return new JsonObject { ["Kind"] = "Package", ["Nums"] = on, ["Texts"] = ot };
    }

    /// <summary>The same EasyEngine project with every moved behavior turned into its old mechanism.</summary>
    static JsonObject WithOldMapping(JsonObject ee)
    {
        var copy = (JsonObject)ee.DeepClone();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    if (o["Behaviors"] is JsonArray arr)
                        for (int i = 0; i < arr.Count; i++)
                            if (arr[i] is JsonObject b && b["Nums"] is JsonObject && b["Texts"] is JsonObject && OldOf(b) is { } old) arr[i] = old;
                    foreach (var (_, v) in o.ToList()) Walk(v);
                    break;
                case JsonArray a:
                    foreach (var v in a.ToList()) Walk(v);
                    break;
            }
        }
        Walk(copy); // every object, in scenes and prefabs
        return copy;
    }

    /// <summary>A project folder holding the old mechanisms as behavior packages (behaviors/Old*.behavior).</summary>
    static string OldKitDir()
    {
        string dir = NewDir();
        foreach (var chunk in System.Text.RegularExpressions.Regex.Split(OldKit.Replace("\r\n", "\n"), @"(?m)^(?=mechanism )").Where(c => c.StartsWith("mechanism ")))
            BehaviorPackage.FromMechanism(chunk.TrimEnd() + "\n").Save(BehaviorPackage.FileIn(BehaviorPackage.ProjectFolder(dir), chunk.Split(' ', '\n')[1].Trim()));
        return dir;
    }

    /// <summary>Plays the project with the shipped packages and with the old mapping, and wants the same frames.</summary>
    static void SameAsOld(string what, JsonObject ee, int frames, Action<World, int>? input = null, Func<List<string>, bool>? happened = null)
    {
        string newDir = NewDir(), oldDir = OldKitDir();
        var now = Trace(ProjectOf(ee), newDir, frames, input);
        var old = Trace(ProjectOf(WithOldMapping(ee)), oldDir, frames, input);
        for (int i = 0; i < frames; i++)
            if (now[i] != old[i])
                throw new Exception($"{what}: frame {i} differs\n      package: {now[i]}\n      old:     {old[i]}");
        if (happened != null) True(happened(now), $"{what}: the fixture didn't do what it should (last frame: {now[^1]})");
    }

    static Action<World, int> Hold(string key, int from = 0, int to = int.MaxValue) => (w, i) =>
    {
        if (i == from) w.KeyDown(key);
        if (i == to) w.KeyUp(key);
    };

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("Behavior packages in use (shipped packages, EasyCompat, precedence, export)");
        string? installedBefore = null;
        string installed = NewDir();
        try
        {
            installedBefore = BehaviorPackage.InstalledFolder;
            BehaviorPackage.InstalledFolder = installed; // never the person's real folder
            RunAll(test, installed);
        }
        finally
        {
            BehaviorPackage.InstalledFolder = null!; // back to %APPDATA%
            foreach (var d in Made) try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The owner's EasyEngine projects on this computer (only read; each test plays a copy): Desktop\Jogos\*\*.eeproj, and
    /// the folders in BE_EASYENGINE_PROJECTS (separated by ';'). None found: no test.
    /// </summary>
    static List<string> RealEasyEngineProjects()
    {
        var dirs = new List<string>();
        string? env = Environment.GetEnvironmentVariable("BE_EASYENGINE_PROJECTS");
        if (!string.IsNullOrWhiteSpace(env)) dirs.AddRange(env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        string jogos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Jogos");
        try { if (Directory.Exists(jogos)) dirs.AddRange(Directory.GetDirectories(jogos)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        bool OneProject(string d)
        {
            try { return Directory.Exists(d) && Directory.GetFiles(d, "*.eeproj").Length == 1; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
        return dirs.Where(OneProject).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void RunAll(Action<string, Action> test, string installed)
    {
        test("every shipped package validates, has its own name and replaces a built-in EasyCompat knows", () =>
        {
            var problems = new List<string>();
            var shipped = BehaviorPackage.Shipped(problems);
            True(problems.Count == 0, string.Join(" | ", problems));
            True(shipped.Count >= 13, $"{shipped.Count} shipped packages");
            var kit = new World();
            StarterKit.LoadInto(kit);
            foreach (var p in shipped)
            {
                True(p.Validate().Count == 0, p.Name + ": " + string.Join(" ", p.Validate()));
                True(p.Origin == PackageOrigin.Shipped, p.Name + " is shipped");
                True(!kit.Library.ContainsKey(p.Name), p.Name + " has the name of a starter-kit mechanism");
                True(p.Replaces != null && EasyCompat.SupportOf(p.Replaces).Level == EasyCompat.Level.Runs, p.Name + " replaces a kind that runs");
                True(p.Icon.Length > 0 && p.Description.Length > 0 && p.Params.All(x => x.Label.Length > 0), p.Name + ": icon, description, labels");
                True(BehaviorPackage.ShippedJson(p.Name) is { } json && BehaviorPackage.Parse(json).Name == p.Name, p.Name + ": its file");
            }
            foreach (var kind in new[] { "Spin", "Projectile", "Parallax", "DestroyOffscreen", "StayInside", "Collectible", "Gate", "Switch", "Spring", "Area", "GoToScene", "AbilityPickup", "Spawner" })
                True(BehaviorPackage.Replacing(shipped, kind) != null, kind + " has a shipped package");
            var easyKit = string.Concat(EasyCompat.KitSources().Select(s => s.Source));
            foreach (var gone in new[] { "EasySpin", "EasyProjectile", "EasyParallax", "EasyDestroyOffscreen", "EasyStayInside", "EasyCollectible", "EasyGate", "EasySwitch", "EasySpring", "EasyArea", "EasyGoToScene", "EasyAbilityPickup", "EasySpawner" })
                True(!easyKit.Contains("mechanism " + gone + "\n") && !easyKit.Contains("mechanism " + gone + "\r"), gone + " is gone from EasyKit");
        });

        test("migrated: Spin, Projectile, Destroy Off-screen play like the old mapping", () =>
            SameAsOld("spin/projectile", EeOf(SceneOf("Level", 960, 540,
                Obj("Saw", 100, 100, 40, 40, Beh("Spin", new { speed = 150 })),
                Obj("Fan", 200, 100, 40, 40, Beh("Spin")),
                Obj("Arrow", 300, 200, 20, 10, Beh("Projectile", new { speed = 700, angle = 0, life = 0 }), Beh("DestroyOffscreen", new { margin = 40 })),
                Obj("Drop", 500, 100, 10, 10, Beh("Projectile", new { speed = 200, angle = 90, life = 0.5 })))), 120,
                happened: t => !t[^1].Contains("Arrow@") && !t[^1].Contains("Drop@") && t[20].Contains("Drop@")));

        test("migrated: Stay Inside (stop, bounce, wrap) plays like the old mapping", () =>
            SameAsOld("stay inside", EeOf(SceneOf("Level", 600, 400,
                Obj("Stopper", 500, 100, 20, 20, Beh("Projectile", new { speed = 300, angle = 0, life = 0 }), Beh("StayInside", texts: new { edge = "stop" })),
                Obj("Bouncer", 300, 300, 20, 20, Beh("Projectile", new { speed = 400, angle = 45, life = 0 }), Beh("StayInside", texts: new { edge = "bounce" })),
                Obj("Wrapper", 50, 50, 20, 20, Beh("Projectile", new { speed = 400, angle = 180, life = 0 }), Beh("StayInside", texts: new { edge = "wrap", sides = "leftright" })))), 150));

        test("migrated: Parallax follows the camera like the old mapping", () =>
            SameAsOld("parallax", EeOf(SceneOf("Level", 3000, 540,
                Player(100, 440, Beh("FollowCamera")), Ground(500, 3000),
                Obj("Hills", 0, 100, 800, 200, Beh("Parallax", new { factor = 0.3 })),
                Obj("Sky", 0, 0, 800, 100, Beh("Parallax")))), 120, Hold("right")));

        test("migrated: Collectible, Ability Pickup and Gate play like the old mapping", () =>
            SameAsOld("pickups", EeOf(SceneOf("Level", 2000, 540,
                Player(60, 440), Ground(),
                Obj("Coin", 160, 450, 20, 20, Beh("Collectible", new { points = 5 }, new { var = "score", collector = "Player" })),
                Obj("Gem", 220, 450, 20, 20, Beh("Collectible", new { amount = 2 }, new { item = "gem" })),
                Obj("Locked", 260, 450, 20, 20, Beh("Collectible", new { points = 100 }, new { var = "score", cond = "hasKey >= 1" })),
                Obj("Key", 300, 450, 20, 20, Beh("AbilityPickup", texts: new { var = "hasKey", grant = "doubleJump" })),
                Obj("Door", 420, 380, 20, 120, Beh("Gate", new { value = 1 }, new { var = "hasKey", op = "=" })),
                Obj("Vault", 700, 380, 20, 120, Beh("Gate", texts: new { item = "gem" })))), 150, Hold("right"),
                happened: t => t[^1].Contains("score=105") && t[^1].Contains("hasKey=1") && t[^1].Contains("item:gem=2") && t[^1].Contains("Door@420,380 a0 o0.2")));

        test("migrated: Switch (touch, interact) and Spring play like the old mapping", () =>
            SameAsOld("switch/spring", EeOf(SceneOf("Level", 2000, 540,
                Player(60, 440), Ground(),
                Obj("Plate", 160, 490, 40, 10, Beh("Switch", texts: new { var = "plate", mode = "Touch" })),
                Obj("Lever", 300, 440, 30, 60, Beh("Switch", new { stayOn = 1 }, new { var = "lever", mode = "interact", key = "E" })),
                Obj("Box", 600, 100, 30, 30, "Dynamic"),
                Obj("Pad", 590, 480, 50, 20, Beh("Spring", new { force = 900 }, new { dir = "Up" })))), 160,
                (w, i) =>
                {
                    if (i == 0) w.KeyDown("right");
                    if (i == 60) w.KeyUp("right");
                    if (i is 90 or 120) w.KeyDown("e");
                    if (i is 92 or 122) w.KeyUp("e");
                },
                happened: t => t.Any(f => f.Contains("plate=1")) && t[^1].Contains("lever=1")));

        test("migrated: Trigger Area and Go To Scene play like the old mapping (scene change included)", () =>
            SameAsOld("areas/doors", EeOf(
                SceneOf("One", 2000, 540,
                    Player(60, 440), Ground(),
                    Obj("Zone", 150, 400, 40, 100, Beh("Area", new { amount = 3, once = 0 }, new { action = "AddVar", var = "visits" })),
                    Obj("Sign", 250, 400, 40, 100, Beh("Area", new { to = 7 }, new { action = "setvar", var = "seen" })),
                    Obj("Door", 420, 400, 40, 100, Beh("GoToScene", texts: new { scene = "Two" }))),
                SceneOf("Two", 960, 540, Player(100, 440), Ground(),
                    Obj("Exit", 600, 400, 40, 100, Beh("Area", texts: new { action = "goscene", scene = "One" })))), 200, Hold("right", 0, 110),
                happened: t => t.Any(f => f.StartsWith("Two")) && t.Any(f => f.Contains("seen=7"))));

        test("migrated: Spawner (timer, key, max, from the top) plays like the old mapping", () =>
            SameAsOld("spawner", EeOf(SceneOf("Level", 960, 540,
                Obj("Enemy", 800, 100, 20, 20),
                Obj("Nest", 100, 100, 30, 30, Beh("Spawner", new { interval = 0.25, max = 3 }, new { target = "Enemy" })),
                Obj("Gun", 300, 100, 30, 30, Beh("Spawner", new { max = 0 }, new { target = "Enemy", key = "E" })))), 120,
                (w, i) => { if (i is 10 or 40) w.KeyDown("e"); if (i is 12 or 42) w.KeyUp("e"); },
                happened: t => System.Text.RegularExpressions.Regex.Matches(t[^1], "Enemy@").Count == 6));

        test("migrated: 'Only while' and turning a behavior off from a rule still reach the package", () =>
        {
            var ee = EeOf(SceneOf("Level", 960, 540,
                Obj("Saw", 100, 100, 40, 40, Beh("Spin")),
                Obj("Gun", 300, 100, 30, 30, Beh("Spawner", new { interval = 0.1, max = 0 }, new { target = "Saw" }),
                    Beh("Custom", texts: new { script = "[{\"when\":\"timer\",\"every\":0.5,\"do\":\"behavior\",\"kind\":\"Spawner\",\"on\":0}]" }))));
            var t = Trace(ProjectOf(ee), NewDir(), 90);
            int at50 = System.Text.RegularExpressions.Regex.Matches(t[50], "Saw@").Count, at89 = System.Text.RegularExpressions.Regex.Matches(t[89], "Saw@").Count;
            True(at50 > 1 && at50 == at89, $"the spawner stops when a rule turns it off ({at50} then {at89})");
        });

        test("a behavior of kind Package runs the project's package with the behavior's settings", () =>
        {
            string dir = NewDir();
            var pkg = BehaviorPackage.FromMechanism("mechanism Counter\n    param every: seconds = 1\n    param by: number = 1\n    param label: text = \"hits\"\n\n    when start\n        repeat forever\n            wait every\n            easy_add label, by\n");
            pkg.Save(BehaviorPackage.FileIn(BehaviorPackage.ProjectFolder(dir), "Counter"));
            var ee = EeOf(SceneOf("Level", 960, 540, Obj("Clock", 0, 0, 10, 10, Beh("Package", new { every = 0.25, by = 2 }, new { package = "Counter", label = "ticks" })),
                Obj("Ghost", 50, 0, 10, 10, Beh("Package", texts: new { package = "Nowhere" }))));
            var w = GameBoot.NewWorld(ProjectOf(ee), dir);
            w.LoadScene(w.Easy != null ? ProjectOf(ee).Scenes[0].Clone() : throw new Exception("EasyCompat is off"));
            for (int i = 0; i < 60; i++) w.Frame(Dt);
            True(w.Errors.Count == 0, string.Join(" | ", w.Errors));
            True(World.ToNumber(w.Globals.GetValueOrDefault("ticks"), 0) is >= 6 and <= 8, $"ticks = {World.ToText(w.Globals.GetValueOrDefault("ticks"))}");
            True(w.Easy!.Unsupported.Any(u => u.Contains("Nowhere")), "a missing package is said once");
        });

        test("precedence: the project's package, then the installed one, then the shipped one", () =>
        {
            string dir = NewDir();
            BehaviorPackage Spin(string desc, double factor)
            {
                var p = BehaviorPackage.Shipped().First(x => x.Name == "Spin").Clone();
                p.Description = desc;
                p.Script = p.Script.Replace("self.angle += speed * dt", $"self.angle += speed * dt * {factor.ToString(CultureInfo.InvariantCulture)}");
                return p;
            }
            string projFile = BehaviorPackage.FileIn(BehaviorPackage.ProjectFolder(dir), "Spin"), instFile = BehaviorPackage.FileIn(installed, "Spin");
            Spin("project", 3).Save(projFile);
            Spin("installed", 2).Save(instFile);
            var ee = EeOf(SceneOf("Level", 960, 540, Obj("Saw", 100, 100, 40, 40, Beh("Spin", new { speed = 60 }))));
            double AngleAfterOneSecond()
            {
                var w = GameBoot.NewWorld(ProjectOf(ee), dir);
                w.LoadScene(ProjectOf(ee).Scenes[0].Clone());
                for (int i = 0; i < 60; i++) w.Frame(Dt);
                return w.Objects.First(o => o.Name == "Saw").Angle;
            }
            var all = BehaviorPackage.DiscoverAll(dir);
            True(all.First(p => p.Name == "Spin") is { Description: "project", Origin: PackageOrigin.Project }, "the project's wins");
            True(Math.Abs(AngleAfterOneSecond() - 180) < 4, "EasyCompat plays the project's Spin");
            File.Delete(projFile);
            True(BehaviorPackage.DiscoverAll(dir).First(p => p.Name == "Spin") is { Description: "installed", Origin: PackageOrigin.Installed }, "then the installed one");
            True(Math.Abs(AngleAfterOneSecond() - 120) < 4, "EasyCompat plays the installed Spin");
            File.Delete(instFile);
            True(BehaviorPackage.DiscoverAll(dir).First(p => p.Name == "Spin").Origin == PackageOrigin.Shipped, "then the shipped one");
            True(Math.Abs(AngleAfterOneSecond() - 60) < 4, "EasyCompat plays the shipped Spin");
        });

        test("a broken package is said once in the console and never thrown; a project mechanism beats a package", () =>
        {
            string dir = NewDir();
            Directory.CreateDirectory(BehaviorPackage.ProjectFolder(dir));
            File.WriteAllText(Path.Combine(BehaviorPackage.ProjectFolder(dir), "Broken.behavior"), "{ nope");
            var p = new Project { Name = "Plain" };
            p.Scenes.Add(new Scene { Name = "Level 1" });
            p.Mechanisms["Spin"] = "mechanism Spin\n    when start\n        print \"mine\"\n";
            var w = GameBoot.NewWorld(p, dir);
            True(w.Output.Count(l => l.Contains("Broken.behavior")) == 1, "said once: " + string.Join(" | ", w.Output));
            True(w.Package("Spin") == null && w.Library["Spin"].Params.Count == 0, "the project's own Spin is the one that plays");
            True(w.Package("Gate") != null, "the other shipped packages are there");
        });

        test("shipped packages play in a plain BuilderEngine project (no EasyEngine behaviors)", () =>
        {
            var p = new Project { Name = "Plain" };
            var s = new Scene { Name = "Level 1", Width = 960, Height = 540 };
            s.Objects.Add(new SceneObject { Id = 1, Name = "Player", X = 100, Y = 100, Width = 32, Height = 32, Tags = { "player" } });
            s.Objects.Add(new SceneObject { Id = 2, Name = "Coin", X = 110, Y = 110, Width = 16, Height = 16, Mechanisms = [new SceneMechanism { Name = "Collectible", Params = { ["var"] = "score", ["points"] = 10.0 } }] });
            s.Objects.Add(new SceneObject { Id = 3, Name = "Saw", X = 300, Y = 100, Mechanisms = [new SceneMechanism { Name = "Spin", Params = { ["speed"] = 120.0 } }] });
            p.Scenes.Add(s);
            var w = GameBoot.NewWorld(p, NewDir());
            True(w.Easy == null, "not an EasyEngine project");
            w.LoadScene(s.Clone());
            for (int i = 0; i < 30; i++) w.Frame(Dt);
            True(w.Errors.Count == 0, string.Join(" | ", w.Errors));
            True(World.ToNumber(w.Globals["score"], 0) == 10, $"score = {World.ToText(w.Globals["score"])}");
            True(w.Objects.All(o => o.Name != "Coin" || o.Destroyed), "the coin was picked up");
            True(Math.Abs(w.Objects.First(o => o.Name == "Saw").Angle - 60) < 3, "the saw turns");
        });

        test("an exported game keeps the project's behaviors folder and plays its packages", () =>
        {
            string proj = NewDir(), outDir = NewDir();
            var p = new Project { Name = "Pkg Game" };
            var s = new Scene { Name = "Level 1" };
            s.Objects.Add(new SceneObject { Id = 1, Name = "Thing", Mechanisms = [new SceneMechanism { Name = "Wobble" }] });
            p.Scenes.Add(s);
            p.Save(proj);
            BehaviorPackage.FromMechanism("mechanism Wobble\n    param by: number = 5\n\n    when tick\n        self.x += by\n").Save(BehaviorPackage.FileIn(BehaviorPackage.ProjectFolder(proj), "Wobble"));
            var r = GameExport.Export(new GameExport.Options(proj, outDir));
            string game = Path.Combine(r.Folder, GameExport.GameFolder);
            True(File.Exists(Path.Combine(game, BehaviorPackage.ProjectFolderName, "Wobble.behavior")), "behaviors/Wobble.behavior is in the game");
            var w = GameBoot.NewWorld(Project.Load(game), game);
            w.LoadScene(Project.Load(game).Scenes[0].Clone());
            w.Frame(Dt);
            True(w.Package("Wobble")?.Origin == PackageOrigin.Project && w.Objects[0].X == 5, "the exported game plays it");
        });

        test("Save as behavior: a mechanism saved as a package reads back as the same mechanism", () =>
        {
            string dir = NewDir();
            const string src = "mechanism Blinker\n    # blinks\n    param every: seconds = 0.5\n    param color: Color = \"#FF0000\"\n    param mode: Choice \"soft\", \"hard\" = \"soft\"\n\n    when start\n        repeat forever\n            wait every\n            visible = not visible\n";
            var p = BehaviorPackage.FromMechanism(src, "Blinks on and off.");
            p.Icon = "💡";
            p.Category = "Looks";
            p.Params[0].Label = "Every (s)";
            p.Params[0].Help = "How often.";
            p.Params[2].ShowIf = "every > 0";
            p.Save(BehaviorPackage.FileIn(BehaviorPackage.ProjectFolder(dir), p.Name));
            var back = BehaviorPackage.DiscoverAll(dir).First(x => x.Name == "Blinker");
            True(back.Origin == PackageOrigin.Project && back.Script == src && back.Icon == "💡" && back.Params[0].Help == "How often.", "fields survive");
            True(Printer.Print(Parser.Parse(back.Script)[0]) == Printer.Print(Parser.Parse(src)[0]), "the same mechanism");
            True(back.Validate().Count == 0, string.Join(" | ", back.Validate()));
            string installedAt = back.Install();
            True(File.Exists(installedAt) && BehaviorPackage.DiscoverAll(null).First(x => x.Name == "Blinker").Origin == PackageOrigin.Installed, "installed for every project");
            File.Delete(installedAt);
        });

        foreach (var from in RealEasyEngineProjects())
            test($"real project (a copy): {Path.GetFileName(from)}: every scene plays frame for frame like the old mapping", () =>
        {
            string file = Directory.GetFiles(from, "*.eeproj")[0];
            string Copy(string to)
            {
                // the .eeproj and its pictures (not its sounds: they can be big), like EasyCompatTests' Mistify copy
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
                string sprites = Path.Combine(from, "Assets", "sprites");
                if (Directory.Exists(sprites))
                    foreach (var f in Directory.GetFiles(sprites, "*", SearchOption.AllDirectories))
                    {
                        string dest = Path.Combine(to, Path.GetRelativePath(from, f));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(f, dest, overwrite: true);
                    }
                return to;
            }
            var ee = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
            string newDir = Copy(NewDir()), oldDir = Copy(OldKitDir());
            var now = EasyLink.FromEeproj(ee.ToJsonString(), newDir);
            var old = EasyLink.FromEeproj(WithOldMapping(ee).ToJsonString(), oldDir);
            Action<World, int> keys = (w, i) =>
            {
                if (i == 10) w.KeyDown("right");
                if (i == 120) w.KeyUp("right");
                if (i is 60 or 140) w.KeyDown("e");
                if (i is 62 or 142) w.KeyUp("e");
                if (i == 90) w.KeyDown("space");
                if (i == 96) w.KeyUp("space");
            };
            int scenes = 0, moved = 0;
            foreach (var sc in now.Scenes)
            {
                var a = Trace(now, newDir, 180, keys, sc.Name);
                var b = Trace(old, oldDir, 180, keys, sc.Name);
                for (int i = 0; i < a.Count; i++)
                    if (a[i] != b[i]) throw new Exception($"scene “{sc.Name}”, frame {i} differs\n      package: {a[i]}\n      old:     {b[i]}");
                scenes++;
                if (sc.Objects.Any(o => EasyLink.EasyBehaviors(o).Any(k => BehaviorPackage.Replacing(BehaviorPackage.Shipped(), k) != null))) moved++;
            }
            Console.WriteLine($"       ({scenes} scenes, {moved} with behaviors that are packages now)");
        });

        test("show if: hidden while false, shown while true or when it can't be worked out; easyKey round-trips", () =>
        {
            var area = BehaviorPackage.Shipped().First(p => p.Name == "TriggerArea");
            var values = new Dictionary<string, object?> { ["action"] = "setvar" };
            True(area.Shows(area.Param("set_to")!, values) && !area.Shows(area.Param("amount")!, values) && !area.Shows(area.Param("scene")!, values), "setvar shows 'set to' only");
            True(area.Shows(area.Param("var")!, values) && area.Shows(area.Param("target")!, values), "or, and fields without a condition");
            True(area.Shows(area.Param("scene")!, new Dictionary<string, object?>()), "missing values are the defaults (goscene)");
            var odd = BehaviorPackage.Parse(BehaviorPackageTests.Spin);
            odd.Params[1].ShowIf = "nothing_like_this > 2";
            True(odd.Shows(odd.Params[1], values), "a condition that can't be worked out shows the field");
            True(area.Param("set_to")!.EasyEngineKey == "to" && BehaviorPackage.Parse(area.ToJson()).Param("set_to")!.EasyKey == "to", "easyKey");
        });
    }
}
