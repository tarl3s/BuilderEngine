using BuilderEngine.Script;

/// <summary>The AI assistant, headless, with a fake AI (no network): prompts, reading answers, testing them, retry,
/// accept, translation and keys. Called from Program.cs with its Test runner.</summary>
static class AiTests
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

    static T Sync<T>(Task<T> t) => t.GetAwaiter().GetResult();

    static void InTemp(Action<string> body)
    {
        string d = Path.Combine(Path.GetTempPath(), "BE_AiTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        try { body(d); }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    /// <summary>The starter kit + a project mechanism, a demo-like scene, the given mechanism open.</summary>
    static AiContext Context(string? open = null, string? openSource = null, string? error = null, string? selection = null, string? answerLanguage = null)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in AiKnowledge.Kit()) sources[k.Name] = k.Source;
        sources["Dash"] = "mechanism Dash\n    param power: number = 400\n\n    when key \"shift\"\n        dash_now power\n\n    to dash_now p\n        push p, 0\n";
        if (open != null && openSource != null) sources[open] = openSource;
        var scene = Project.CreateDemo("AI Test").Scenes[0];
        return new AiContext
        {
            Sources = sources,
            ProjectMechanisms = open != null && !AiKnowledge.Kit().Any(k => k.Name == open) ? ["Dash", open] : ["Dash"],
            OpenName = open,
            Error = error,
            Selection = selection,
            SceneName = scene.Name,
            SceneNames = ["Level 1", "Level 2"],
            SceneObjects = scene.Objects.Select(o => new AiSceneObject(o.Name, o.Tags, o.Mechanisms.Select(m => m.Name).ToList())).ToList(),
            Scene = scene,
            AnswerLanguage = answerLanguage,
        };
    }

    const string GoodJumper = "mechanism SuperJump\n    param height: number = 200\n    uses Body\n\n    when key \"space\"\n        jump height\n        play \"jump.wav\"\n";

    public static void Run(Action<string, Action> test)
    {
        Console.WriteLine("AI assistant");

        // ---------------- Prompt ----------------
        test("prompt: rules, vocabulary, closest starter examples, project and scene", () =>
        {
            var ctx = Context();
            var (sys, user) = AiPrompt.Build(AiMode.Make, "make a coin that gives 5 points and plays a sound", ctx);
            foreach (var word in new[] { "mechanism", "uses", "when", "together" }) True(sys.Contains(word), "keyword " + word);
            foreach (var cmd in new[] { "camera follow", "goto_scene", "show text", "spawn", "jump" }) True(sys.Contains(cmd), "command " + cmd);
            foreach (var fn in new[] { "nearest", "random", "saved" }) True(sys.Contains(fn), "function " + fn);
            True(sys.Contains("Platformer (params speed, jump_height"), "starter kit summary with params:\n" + sys);
            True(user.Contains("mechanism Coin\n"), "the Coin example is picked for a coin request");
            True(user.Contains("Dash (params power; commands dash_now p)"), "project mechanisms with their commands:\n" + user);
            True(user.Contains("[tags player]"), "scene objects with tags");
            True(user.Contains("REQUEST: make a coin"), "the request");
            True(sys.Length + user.Length < AiPrompt.MaxChars, $"size {sys.Length + user.Length}");
            Console.WriteLine($"       (a typical Make prompt: {sys.Length} + {user.Length} chars)");
        });
        test("prompt: examples follow the request's words (jump → Platformer, talk → Npc)", () =>
        {
            Eq("Platformer", AiKnowledge.PickExamples("make the player jump twice", 1)[0].Name);
            Eq("Npc", AiKnowledge.PickExamples("a character who talks to the player", 1)[0].Name);
            Eq("Patrol", AiKnowledge.PickExamples("um inimigo que anda de um lado pro outro", 1)[0].Name, "Portuguese words still pick");
            Eq(2, AiKnowledge.PickExamples("zzz qqq", 2).Count, "nothing matches: still two examples");
        });
        test("prompt: stays under the size budget with a huge project and a long open mechanism", () =>
        {
            var ctx = Context("Big", "mechanism Big\n" + string.Concat(Enumerable.Range(0, 400).Select(i => $"    when told \"m{i}\"\n        x{i} = {i}\n")));
            for (int i = 0; i < 80; i++) { ctx.Sources[$"Mech{i}"] = $"mechanism Mech{i}\n    param a{i}: number = 1\n    to cmd{i} x\n        stop\n"; ctx.ProjectMechanisms.Add($"Mech{i}"); }
            for (int i = 0; i < 200; i++) ctx.SceneObjects.Add(new AiSceneObject($"Object number {i}", ["enemy", "tag" + i], ["Patrol", "Body"]));
            foreach (var mode in new[] { AiMode.Make, AiMode.Edit, AiMode.Explain, AiMode.Fix })
            {
                var (sys, user) = AiPrompt.Build(mode, "make it jump higher and add a double jump", ctx);
                True(sys.Length + user.Length < AiPrompt.MaxChars, $"{mode}: {sys.Length + user.Length} chars");
            }
        });
        test("prompt: Edit shows the open mechanism and keeps its name; Fix shows the error", () =>
        {
            var ctx = Context("Platformer", AiKnowledge.Kit().First(k => k.Name == "Platformer").Source, error: "Platformer (line 7): There's no command called 'jmp'.");
            var (_, edit) = AiPrompt.Build(AiMode.Edit, "make the jump higher", ctx);
            True(edit.Contains("keep its name Platformer") && edit.Contains("param jump_height"), edit);
            var (_, fix) = AiPrompt.Build(AiMode.Fix, "", ctx);
            True(fix.Contains("ERROR: Platformer (line 7)"), fix);
        });
        test("prompt: Explain answers in the question's language and never edits", () =>
        {
            var ctx = Context("Coin", AiKnowledge.Kit().First(k => k.Name == "Coin").Source);
            var (sys, user) = AiPrompt.Build(AiMode.Explain, "o que esse mecanismo faz?", ctx);
            True(sys.Contains("same language as the question"), "language rule");
            True(sys.Contains("never change the project"), "teacher never edits");
            True(user.Contains("mechanism Coin"), "open mechanism shown");
            var (sysEn, _) = AiPrompt.Build(AiMode.Explain, "what does this do?", ctx);
            True(sysEn.Contains("Answer in English."), "English question → English");
            var (sysForced, _) = AiPrompt.Build(AiMode.Explain, "o que faz?", Context(answerLanguage: "English"));
            True(sysForced.Contains("Answer in English."), "setting forces English");
            var (_, sel) = AiPrompt.Build(AiMode.Explain, "", Context("Coin", AiKnowledge.Kit().First(k => k.Name == "Coin").Source, selection: "destroy self"));
            True(sel.Contains("THESE LINES") && sel.Contains("destroy self") && sel.Contains("What do these lines do?"), sel);
        });

        // ---------------- Reading answers ----------------
        test("reply: code in a fence among prose, with tabs, braces, semicolons, // and on start", () =>
        {
            string reply = "Sure! Here is your mechanism:\n\n```python\nmechanism Spinner {\n\tparam speed: number = 90;\n\ton start {\n\t\tvisible = true; // show it\n\t\tif (speed > 0 && !paused) {\n\t\t\tprint \"go; {speed}\"\n\t\t}\n\t}\n}\n```\nThis makes it spin. Enjoy!";
            string? s = AiReply.ExtractScript(reply);
            True(s != null, "found code");
            var d = Parser.Parse(s!)[0];
            Eq("Spinner", d.Name); Eq(1, d.Params.Count); Eq(EventKind.Start, d.Whens[0].Event.Kind);
            True(s!.Contains("print \"go; {speed}\""), "text in quotes untouched:\n" + s);
            True(s.Contains("speed > 0 and not paused"), "&& and ! become and / not:\n" + s);
            True(!s.Contains('\t') && !s.Contains("Enjoy"), "no tabs, no prose");
        });
        test("reply: bare code between sentences, two-space indents", () =>
        {
            string reply = "Okay.\nmechanism Hopper\n  param height: number = 80\n  when key \"space\"\n    jump height\nThat's it, it hops.";
            string s = AiReply.ExtractScript(reply)!;
            True(s.Contains("\n    when key \"space\"\n        jump height"), "4-space indents:\n" + s);
            Eq(false, s.Contains("That's it"), "prose after the code dropped");
            Eq(1, Parser.Parse(s).Count);
        });
        test("reply: code without a mechanism line gets one; function/def become 'to'", () =>
        {
            string reply = "```\nwhen start\n    twice\n\nfunction twice() {\n    jump 50\n    jump 50\n}\n```";
            string s = AiReply.ExtractScript(reply, fallbackName: "DoubleHop")!;
            var d = Parser.Parse(s)[0];
            Eq("DoubleHop", d.Name); Eq("twice", d.Procs[0].Name);
            var d2 = Parser.Parse(AiReply.ExtractScript("```\nmechanism A\n    def boom(power):\n        push power, 0\n    on Health.died:\n        stop\n```")!)[0];
            Eq("boom", d2.Procs[0].Name); Eq("power", d2.Procs[0].Params[0]); Eq("Health", d2.Whens[0].Event.Source);
        });
        test("reply: before/after blocks keep the last; two mechanisms stay together; forced name", () =>
        {
            string reply = "Before:\n```\nmechanism Coin\n    when start\n        x = 1\n```\nAfter:\n```\nmechanism Coin\n    when start\n        x = 2\nmechanism Sparkle\n    when start\n        flash\n```";
            var defs = Parser.Parse(AiReply.ExtractScript(reply)!);
            Eq("Coin,Sparkle", string.Join(",", defs.Select(d => d.Name)));
            True(defs[0].Whens[0].Body[0] is AssignStmt { Value: NumberExpr { Value: 2 } }, "the 'after' version");
            Eq("Gem", Parser.Parse(AiReply.ExtractScript(reply, forcedName: "Gem")!)[0].Name);
            Eq(null, AiReply.ExtractScript("I can't help with that, sorry."));
            Eq(null, AiReply.ExtractScript(""));
        });
        test("reply: other-language habits (elif, while true, var, when update, collide)", () =>
        {
            string s = AiReply.ExtractScript("```\nmechanism A\n    when update:\n        var n = 1\n        if (n > 2):\n            n = 0\n        elif n == 1:\n            n = 5\n    when collides with \"player\":\n        while true:\n            wait(1)\n```")!;
            var d = Parser.Parse(s)[0];
            Eq(EventKind.Tick, d.Whens[0].Event.Kind); Eq(EventKind.Touched, d.Whens[1].Event.Kind); Eq("player", d.Whens[1].Event.Filter);
            True(d.Whens[1].Body[0] is RepeatStmt { Count: null }, "while true → repeat forever:\n" + s);
        });

        // ---------------- Testing answers ----------------
        test("check: a good mechanism passes (parsed, run in the scene)", () =>
        {
            var r = AiCheck.Validate(GoodJumper, Context());
            True(r.Ok, string.Join(" | ", r.Problems));
            Eq("SuperJump", r.Defs[0].Name);
            var saver = AiCheck.Validate("mechanism Saver\n    when start\n        save game\n        save \"coins\", 3\n", Context());
            True(saver.Ok, string.Join(" | ", saver.Problems));
            Eq(0, Directory.GetDirectories(Path.GetTempPath(), "BuilderEngine_AiCheck_*").Length, "the test's saves folder is deleted");
        });
        test("check: unknown command, function, used and spawned mechanism are caught (even in branches never run)", () =>
        {
            var r = AiCheck.Validate("mechanism Bad\n    uses Healht\n    when told \"never\"\n        jmp 100\n        x = randum(1, 2)\n        spawn Bullet here\n", Context());
            string all = string.Join(" | ", r.Problems);
            True(all.Contains("'Healht'") && all.Contains("Did you mean 'Health'"), all);
            True(all.Contains("command called 'jmp'") && all.Contains("Did you mean 'jump'"), all);
            True(all.Contains("function called 'randum'"), all);
            True(all.Contains("spawn Bullet"), all);
            var ok = AiCheck.Validate("mechanism Gun\n    param what: text = \"Coin\"\n    when key \"x\"\n        spawn what\n        spawn Coin here\n        Dash.dash_now 10\n        camera shake 4 for 0.2\n", Context());
            True(ok.Ok, string.Join(" | ", ok.Problems));
        });
        test("check: runtime errors when keys are pressed or the player walks in", () =>
        {
            var r = AiCheck.Validate("mechanism Oops\n    when key \"e\"\n        tell 5 \"hi\"\n", Context());
            True(r.Problems.Any(p => p.Contains("Oops (line 3)") && p.Contains("needs an object")), string.Join(" | ", r.Problems));
            var t = AiCheck.Validate("mechanism Ouch\n    when touched by \"player\"\n        hp = \"a\" - 1\n", Context());
            True(!t.Ok, "touching the player runs the broken line: " + string.Join(" | ", t.Problems));
            var bad = AiCheck.Validate("mechanism Oops\n    when start\n        x = (\n", Context());
            True(bad.Problems[0].StartsWith("Line "), bad.Problems[0]);
        });
        test("check: endless things are refused (self-spawn, giant repeat, a loop that never waits)", () =>
        {
            Eq(false, AiCheck.Validate("mechanism Rabbit\n    when start\n        spawn Rabbit here\n", Context()).Ok);
            Eq(false, AiCheck.Validate("mechanism Busy\n    when start\n        repeat 99999999\n            x += 1\n", Context()).Ok);
            var slow = Sync(AiCheck.ValidateAsync("mechanism Spam\n    when start\n        repeat 9000\n            repeat 9000\n                print \"x\"\n", Context(), TimeSpan.FromMilliseconds(300)));
            True(!slow.Ok && (slow.Problems[0].Contains("loop") || slow.Problems[0].Contains("too")), string.Join(" | ", slow.Problems));
        });

        // ---------------- The assistant (fake AI) ----------------
        test("assistant: a broken answer is retried once with its error, then previewed", () =>
        {
            var ai = new ScriptedAiClient("```\nmechanism SuperJump\n    when key \"space\"\n        jmp 200\n```", "Fixed:\n```\n" + GoodJumper + "```\nIt jumps 200 px.");
            var log = new List<string>();
            var r = Sync(new AiAssistant(ai, log.Add).RunAsync(AiMode.Make, "make a super jump", Context()));
            Eq(null, r.Error);
            True(r.Retried, "retried");
            Eq(2, ai.Calls.Count, "one try + one retry, no translation for English");
            True(ai.Calls[1].Prompt.Contains("jmp") && ai.Calls[1].Prompt.Contains("IT DOESN'T WORK"), "the retry shows the error");
            Eq(0, r.Problems.Count);
            Eq("SuperJump", r.Mechanisms[0].Name); True(r.Mechanisms[0].IsNew, "new");
            True(r.Text.Contains("200 px"), "prose kept: " + r.Text);
            True(log.Any(l => l.Contains("asking once more")), string.Join("\n", log));
        });
        test("assistant: still broken after the retry → previewed with its problems; no code → error", () =>
        {
            var ai = new ScriptedAiClient("```\nmechanism A\n    when start\n        jmp 1\n```");
            var r = Sync(new AiAssistant(ai).RunAsync(AiMode.Make, "make A", Context()));
            Eq(2, ai.Calls.Count); True(r.Problems.Count > 0 && r.HasCode, "problems shown with the code");
            var none = Sync(new AiAssistant(new ScriptedAiClient("I don't know.")).RunAsync(AiMode.Make, "make something", Context()));
            True(none.Error != null && !none.HasCode, "no code → a friendly error");
        });
        test("assistant: Edit keeps the open mechanism's name and previews a diff", () =>
        {
            string coin = AiKnowledge.Kit().First(k => k.Name == "Coin").Source;
            string answer = "```\nmechanism BigCoin\n    param value: number = 5\n    param sound: Sound = \"coin.wav\"\n\n    when touched by \"player\"\n        score += value\n        play sound\n        destroy self\n```";
            var r = Sync(new AiAssistant(new ScriptedAiClient(answer)).RunAsync(AiMode.Edit, "make it worth 5", Context("Coin", coin)));
            Eq(null, r.Error);
            var m = r.Mechanisms.Single();
            Eq("Coin", m.Name); Eq(false, m.IsNew); True(m.Source.StartsWith("mechanism Coin\n"), m.Source);
            var diff = m.Diff();
            True(diff.Any(l => l.Kind == '-' && l.Text.Contains("= 1")) && diff.Any(l => l.Kind == '+' && l.Text.Contains("= 5")), string.Join("\n", diff));
            Eq(true, diff.Count(l => l.Kind == ' ') >= 5, "unchanged lines kept");
            var noOpen = Sync(new AiAssistant(new ScriptedAiClient(answer)).RunAsync(AiMode.Edit, "x", Context()));
            True(noOpen.Error != null, "Edit with nothing open asks to open one");
        });
        test("assistant: Explain only answers (no code applied)", () =>
        {
            var ai = new ScriptedAiClient("It adds points and disappears.\n```\nmechanism Nope\n```");
            var r = Sync(new AiAssistant(ai).RunAsync(AiMode.Explain, "what does it do?", Context("Coin", AiKnowledge.Kit().First(k => k.Name == "Coin").Source)));
            Eq(0, r.Mechanisms.Count); True(r.Text.StartsWith("It adds points"), r.Text);
        });
        test("assistant: Fix sends the error; MechanismInError finds the mechanism", () =>
        {
            Eq("Coin", AiAssistant.MechanismInError("⚠ Coin (line 4): There's no command called 'ply'.", ["Coin", "Health"]));
            Eq("Health", AiAssistant.MechanismInError("'Player' uses 'Health', but…", ["Coin", "Health"]));
            Eq(null, AiAssistant.MechanismInError("something else", ["Coin"]));
            var broken = "mechanism Coin\n    when touched by \"player\"\n        ply \"coin.wav\"\n";
            var ai = new ScriptedAiClient("```\nmechanism Coin\n    when touched by \"player\"\n        play \"coin.wav\"\n```\n'ply' should be 'play'.");
            var r = Sync(new AiAssistant(ai).RunAsync(AiMode.Fix, "", Context("Coin", broken, error: "Line 3: There's no command called 'ply'.")));
            True(ai.Calls[0].Prompt.Contains("ERROR: Line 3"), "error in the prompt");
            Eq(0, r.Problems.Count); Eq("Coin", r.Mechanisms[0].Name);
        });

        // ---------------- Accept ----------------
        test("accept: new mechanisms are added, changed ones replaced, clashes renamed, identical ones skipped", () =>
        {
            var ctx = Context();
            var make = AiMerge.Propose("mechanism Coin\n    when start\n        x = 9\nmechanism Health\n" + AiKnowledge.Kit().First(k => k.Name == "Health").Source["mechanism Health\n".Length..], AiMode.Make, ctx);
            Eq("Coin2", make.Single().Name, "Coin exists → Coin2; an identical Health is left out");
            True(make[0].Source.StartsWith("mechanism Coin2\n"), "renamed in the code too");
            var sources = new Dictionary<string, string>(ctx.Sources);
            Eq("Coin2", AiMerge.Apply(sources, make));
            True(sources.ContainsKey("Coin2") && sources["Coin"] == ctx.Sources["Coin"], "the old Coin untouched");
            var edit = AiMerge.Propose("mechanism Other\n    when start\n        x = 1\n", AiMode.Edit, Context("Dash", ctx.Sources["Dash"]));
            AiMerge.Apply(sources, edit);
            True(sources["Dash"].StartsWith("mechanism Dash\n    when start"), "the open mechanism replaced, name kept");
            Eq("Coin3", AiMerge.UniqueName("Coin", n => n is "Coin" or "Coin2"));
        });

        // ---------------- Translation ----------------
        test("translation: English requests skip it; Portuguese goes to English first", () =>
        {
            True(AiTranslate.LooksEnglish("make the jump higher"), "English");
            True(!AiTranslate.LooksEnglish("faz o pulo ficar mais alto"), "Portuguese");
            True(AiTranslate.LooksEnglish("make \"Olá mundo\" appear"), "quoted text doesn't count");
            var en = new ScriptedAiClient("```\n" + GoodJumper + "```");
            Sync(new AiAssistant(en).RunAsync(AiMode.Make, "make a super jump", Context()));
            Eq(0, en.Calls.Count(c => c.Json), "no translation call for English");
            var pt = new ScriptedAiClient("```\n" + GoodJumper + "```");
            pt.JsonReplies.Enqueue("{\"english\":\"make a jump 200 high\"}");
            var r = Sync(new AiAssistant(pt).RunAsync(AiMode.Make, "faz um pulo de 200 de altura", Context()));
            Eq(1, pt.Calls.Count(c => c.Json), "one translation call");
            True(r.Translated && pt.Calls.Last().Prompt.Contains("REQUEST: make a jump 200 high"), pt.Calls.Last().Prompt);
            Eq(null, AiTranslate.Clean("faz um pulo de 200", "{\"english\":\"make a jump\"}"), "a translation that lost a number isn't used");
            Eq(null, AiTranslate.Clean("faz o Coin girar", "{\"english\":\"make the coin spin\"}"), "a lost name isn't used");
            var ex = new ScriptedAiClient("Ele pula.");
            Sync(new AiAssistant(ex).RunAsync(AiMode.Explain, "o que isso faz?", Context()));
            Eq(0, ex.Calls.Count(c => c.Json), "Explain answers in the question's language: no translation");
        });

        // ---------------- Settings and keys ----------------
        test("settings: saved in settings.json (with --appdata), read-only stays in memory", () => InTemp(dir =>
        {
            var s = new AiSettings(new AppSettings(dir)) { Provider = "groq", LocalModel = "qwen2.5-coder:1.5b", AnswerLanguage = AiSettings.AnswerEnglish };
            s.SetKey("groq", "  gsk_abcdefghijklmnop1234  ");
            True(s.Save(), "saved");
            var back = new AiSettings(new AppSettings(dir));
            Eq("groq", back.Provider); Eq("qwen2.5-coder:1.5b", back.LocalModel); Eq("gsk_abcdefghijklmnop1234", back.Key("groq")); Eq(AiSettings.AnswerEnglish, back.AnswerLanguage);
            Eq("Cloud · Groq", back.Label());
            var ro = new AiSettings(new AppSettings(dir, readOnly: true)) { Provider = AiSettings.Auto };
            Eq(false, ro.Save()); Eq("groq", new AiSettings(new AppSettings(dir)).Provider, "read-only didn't write");
            True(new AiSettings().CreateClient(["llama3:8b", "qwen2.5-coder:3b"]) is OllamaClient { Model: "qwen2.5-coder:3b" }, "local picks the coder model");
            True(new AiSettings { Provider = AiSettings.Auto }.CreateClient() is CloudAuto { Clients.Count: 0 }, "auto without keys has no clients");
        }));
        test("keys never appear in logs, errors or the log file", () => InTemp(dir =>
        {
            const string key = "gsk_SECRETsecret0123456789";
            var settings = new AiSettings(); settings.SetKey("groq", key);
            var ai = new ScriptedAiClient("x") { Throw = new AiException($"Groq refused key {key} (Bearer {key})") };
            var log = new List<string>();
            var assistant = new AiAssistant(ai, log.Add, settings.Secrets()) { LogFile = Path.Combine(dir, "ai.log") };
            var r = Sync(assistant.RunAsync(AiMode.Make, "make a coin", Context()));
            True(r.Error != null && !r.Error.Contains(key), "error hides the key: " + r.Error);
            True(log.Count > 0 && log.All(l => !l.Contains(key)), string.Join("\n", log));
            True(!File.Exists(assistant.LogFile) || !File.ReadAllText(assistant.LogFile).Contains(key), "log file");
            Eq("token •••", AiSecrets.Redact("token " + key, null), "a Groq-looking key is hidden even when unknown");
            Eq("Authorization: Bearer •••", AiSecrets.Redact("Authorization: Bearer abcdefghijklmnopqrstu", null));
        }));
        test("clients: Ollama tags and context size; free tiers fall through to the next provider", () =>
        {
            Eq("qwen2.5-coder:3b,llama3:8b", string.Join(",", OllamaClient.ParseModels("{\"models\":[{\"name\":\"qwen2.5-coder:3b\"},{\"name\":\"llama3:8b\"}]}")));
            Eq(0, OllamaClient.ParseModels("not json").Count);
            Eq(4096, OllamaClient.ContextFor(1000)); True(OllamaClient.ContextFor(30000) > 4096, "big prompt → bigger window");
            var busy = new ScriptedAiClient { Throw = new CloudException("Groq limit reached for now.") { RateLimited = true, RetryAfter = 999 } };
            var ok = new ScriptedAiClient("hello");
            Eq("hello", Sync(new CloudAuto([busy, ok]).GenerateAsync("s", "p")));
            try { Sync(new CloudAuto([]).GenerateAsync("s", "p")); throw new Exception("expected an error"); }
            catch (AiException e) { True(e.Message.Contains("key"), e.Message); }
            Eq(7.1, CloudClient.RetryAfterSeconds("Please try again in 7.1s."));
            Eq("gemini-3.9-flash", CloudClient.PickModel("gemini", ["gemini-3.8-flash", "gemini-3.9-flash", "gemini-embedding"]));
        });
    }
}
