using Avalonia;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// EDITOR LIFECYCLE, dev tool (part of --snap; `--snap-only lifecycle` runs just this): opening a project whose
// project.json can't be read (its mechanisms must survive, and a file that couldn't be set aside is never saved over),
// switching between 20 projects (memory must come back), opening and closing editor windows the way the start screen
// does (a closed window must be freed), the console's size, and the window fitting a small screen.
// Appends PASS/FAIL lines to checks.txt and the measurements to lifecycle.txt.
public sealed partial class MainWindow
{
    async Task SnapLifecycle(string dir)
    {
        var lines = new List<string>();
        var notes = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        string home = _projectDir;
        try
        {
            SceneStop();
            string root = Path.Combine(dir, "lifecycle");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            const string hero = "mechanism Hero\n    when start\n        print \"hero\"\n";
            string MakeBroken(string name, string json)
            {
                string d = Path.Combine(root, name);
                Directory.CreateDirectory(Path.Combine(d, Project.MechanismsFolder));
                File.WriteAllText(Path.Combine(d, Project.FileName), json);
                File.WriteAllText(Path.Combine(d, Project.MechanismsFolder, "Hero.bs"), hero);
                return d;
            }

            // 1. project.json isn't JSON: a new project is made around the mechanisms, never over them.
            string broken = MakeBroken("broken", "{ \"name\": \"Broken\", \"scenes\": [ oops");
            OpenProjectFolder(broken);
            await Task.Delay(200);
            TrySave(); // what Ctrl+S, closing or switching does
            string heroFile = Path.Combine(broken, Project.MechanismsFolder, "Hero.bs");
            Check("a project.json that isn't JSON: its mechanism files are kept",
                File.Exists(heroFile) && File.ReadAllText(heroFile) == hero && _sources.ContainsKey("Hero"));
            Check("a project.json that isn't JSON is set aside as project.broken-….json", Directory.GetFiles(broken, "project.broken-*.json").Length == 1);

            // 2. ...and another program holds it (OneDrive, an editor): it can't be set aside, so nothing may be saved over it.
            const string lockedJson = "{ not json at all";
            string locked = MakeBroken("locked", lockedJson);
            string lockedFile = Path.Combine(locked, Project.FileName);
            using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                OpenProjectFolder(locked);
            await Task.Delay(200);
            bool saved = TrySave(); // the lock is gone now: a save would replace it
            Check("a broken project.json that couldn't be set aside is never saved over",
                !saved && File.ReadAllText(lockedFile) == lockedJson && File.Exists(Path.Combine(locked, Project.MechanismsFolder, "Hero.bs")));

            // 2b. Closing while the save fails (project.json held by another program): the window stays and asks (D-13).
            string held = Path.Combine(root, "held");
            Project.CreateDemo("Held").Save(held);
            OpenProjectFolder(held);
            await Task.Delay(200);
            _askWhenCloseCantSave = true;
            using (new FileStream(Path.Combine(held, Project.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Close();
                await Task.Delay(400);
                var question = OwnedWindows.FirstOrDefault();
                Check("closing when the save fails keeps the editor open and asks", IsVisible && question != null);
                question?.Close(); // Cancel
                await Task.Delay(200);
                Check("…and Cancel keeps it open", IsVisible);
            }
            _askWhenCloseCantSave = false;

            // 3. Switching between 20 projects and back: the memory comes back.
            OpenProjectFolder(home);
            await Task.Delay(300);
            long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
            long before = Mem();
            for (int i = 0; i < 20; i++)
            {
                string p = Path.Combine(root, $"p{i}");
                Project.CreateDemo($"Switch {i}").Save(p);
                OpenProjectFolder(p);
                await Task.Delay(60);
            }
            OpenProjectFolder(home);
            await Task.Delay(300);
            long after = Mem();
            double grewMb = (after - before) / 1048576.0;
            notes.Add($"switching 20 projects: managed memory {before / 1048576.0:0.0} MB -> {after / 1048576.0:0.0} MB ({grewMb:+0.0;-0.0;0.0} MB)");
            Check($"switching 20 projects and back keeps memory flat ({grewMb:+0.0;-0.0;0.0} MB)", grewMb < 20);

            // 4. Editor windows opened and closed the way the start screen does: a closed window is freed.
            var refs = new List<WeakReference>();
            for (int i = 0; i < 4; i++) refs.Add(await OpenAndCloseEditor());
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            // Avalonia itself may hold on to the most recently closed window for a while (input and focus bookkeeping);
            // every earlier one must be gone, or each trip to the start screen leaks a whole editor and its project.
            int alive = refs.Take(refs.Count - 1).Count(r => r.IsAlive);
            notes.Add($"closed editor windows still in memory after a full GC: {alive} of the first {refs.Count - 1}; the last one: {(refs[^1].IsAlive ? "alive" : "freed")}");
            Check($"closed editor windows are freed ({alive} of {refs.Count - 1} still alive)", alive == 0);

            // 5. The sprite editor never throws a drawing away without asking (Esc, Cancel and X used to).
            var se = new SpriteEditorWindow(_projectDir, null, 16);
            se.Show(this);
            await Task.Delay(300);
            se.PickColor(0xFFE8B04B);
            se.StrokeStart(1, 1, false, false);
            se.StrokeMove(10, 10);
            se.StrokeEnd();
            se.Close(); // like Esc / Cancel / X
            await Task.Delay(300);
            var ask = se.OwnedWindows.FirstOrDefault();
            Check("closing the sprite editor with a new drawing asks first", se.IsVisible && se.HasUnsavedChanges && ask != null);
            ask?.Close(); // "Keep drawing"
            await Task.Delay(200);
            Check("…and keeping the drawing leaves it open", se.IsVisible);
            se.SaveTo("lifecycle_drawn.png");
            se.Close();
            await Task.Delay(300);
            Check("once saved, it closes without asking", !se.IsVisible && se.OwnedWindows.Count == 0);

            // 6. Window sizes: the fit to small screens, then the editor laid out small and large (PNGs to look at).
            Check("fit: a 1360×840 editor on 1920×1080 at 150% (1280×648 free) shrinks to it",
                WindowFit.Fit(1360, 840, 960, 600, 1280, 648) == (1280, 648, 960, 600));
            Check("fit: on a 1366×768 laptop at 150% (910×470 free) the minimum shrinks too",
                WindowFit.Fit(1360, 840, 960, 600, 910, 470) == (910, 470, 910, 470));
            Check("fit: a window that fits stays as it is", WindowFit.Fit(1000, 720, 760, 520, 1920, 1040) == (1000, 720, 760, 520));
            double w0 = Width, h0 = Height, mw0 = MinWidth, mh0 = MinHeight;
            MinWidth = 600; MinHeight = 400;
            foreach (var (sw, sh) in new[] { (910.0, 470.0), (1024.0, 640.0), (2560.0, 1440.0) })
            {
                Width = sw; Height = sh;
                await Task.Delay(500);
                string size = $"{sw}x{sh}";
                Snap(dir, $"size_{size}");
                notes.Add($"window asked {size}, got {ClientSize.Width}x{ClientSize.Height}");
                // Windows never makes a window bigger than the screen: on a smaller screen the big size can't be judged here
                // (the layout keeps the asked size and the edge is cut off — the case WindowFit avoids when a window opens).
                if (ClientSize.Width < sw - 1 || ClientSize.Height < sh - 1) { notes.Add($"  {size} is bigger than this screen: only the picture was made"); continue; }
                var buttons = this.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Where(b => b.IsEffectivelyVisible).ToList();
                bool Inside(Avalonia.Controls.Control c) => c.TranslatePoint(new Avalonia.Point(c.Bounds.Width, c.Bounds.Height), this) is { } p
                    && p.X <= ClientSize.Width + 0.5 && p.Y <= ClientSize.Height + 0.5;
                var play = buttons.FirstOrDefault(b => b.Content as string == "▶  Play");
                var save = buttons.FirstOrDefault(b => b.Content as string == "Save");
                Check($"{size}: Play and Save are on screen", play != null && save != null && Inside(play) && Inside(save));
                // The scene's bars used to run under the inspector when the middle was narrow (Fit and the last tools unreachable).
                double centerRight = _center.TranslatePoint(new Avalonia.Point(_center.Bounds.Width, 0), this)?.X ?? 0;
                bool InCenter(Avalonia.Controls.Control c) => c.TranslatePoint(new Avalonia.Point(c.Bounds.Width, 0), this) is { } p && p.X <= centerRight + 0.5;
                var fitButton = buttons.FirstOrDefault(b => b.Content as string == "Fit");
                var lastTool = buttons.FirstOrDefault(b => b.Content as string == "Room Maker");
                Check($"{size}: Fit and every tool button fit in the scene area", fitButton != null && lastTool != null && InCenter(fitButton) && InCenter(lastTool));
                Check($"{size}: the console is on screen", _console.IsEffectivelyVisible && _console.TranslatePoint(new Avalonia.Point(0, 0), this) is { } c0 && c0.Y < ClientSize.Height - 20);
            }
            MinWidth = mw0; MinHeight = mh0; Width = w0; Height = h0;
            await Task.Delay(300);

            // 7. A game that prints every frame for 10 minutes (36,000 lines): one more line must stay cheap.
            ClearLog();
            for (int i = 0; i < 36000; i++) Log($"frame {i}: the slime is at {i % 800}, 320");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 30; i++) { Log("one more line"); _console.Measure(new Avalonia.Size(_console.Bounds.Width > 0 ? _console.Bounds.Width : 300, double.PositiveInfinity)); }
            double perLine = clock.Elapsed.TotalMilliseconds / 30;
            int kept = (_console.Text ?? "").Count(c => c == '\n');
            notes.Add($"console after 36,000 lines: {kept} lines kept, {(_console.Text ?? "").Length / 1024} KB of text, {perLine:0.0} ms per new line (text + layout)");
            // Before the trim (runtime audit) one more line cost 1,094 ms here. Bounded now; the layout of the ~40,000 kept
            // characters still costs tens of ms per line (docs/audit/editor-samples-tests.md, E-07).
            Check($"the console stays bounded after 36,000 lines ({perLine:0.0} ms per line, {kept} lines kept)",
                perLine < 100 && (_console.Text ?? "").Length <= 60_000);
            Check("the console still shows the newest line", (_console.Text ?? "").TrimEnd().EndsWith("one more line"));
            ClearLog();
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        finally
        {
            if (!AppSettings.SameFolder(_projectDir, home)) OpenProjectFolder(home);
            File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
            File.WriteAllLines(Path.Combine(dir, "lifecycle.txt"), notes);
        }
    }

    /// <summary>A second editor window (on the --project folder), shown, then closed. Returns a weak reference to it.</summary>
    static async Task<WeakReference> OpenAndCloseEditor()
    {
        var w = new MainWindow();
        w.Show();
        await Task.Delay(1800); // past its first frame and the 1.5 s "never stays hidden" timer
        w.Close();
        await Task.Delay(300);
        var weak = new WeakReference(w);
        w = null!; // the async method keeps its locals until it ends
        return weak;
    }
}
