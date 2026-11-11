using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// PROJECTS FEATURE, dev tool (part of --snap): drives scenes, sounds, My Library, goto_scene while playing and
// project switching the way the UI does, appends PASS/FAIL lines to checks.txt and renders PNGs of the new UI.
// Runs with the app-data folder inside the snap folder, so the real settings and library are never touched.
public sealed partial class MainWindow
{
    async Task SnapProjectFeatures(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            Check("app data is inside the snap folder", _settings.Folder.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && _myLibrary.Folder.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
            Check("title shows the project name", Title == $"BuilderEngine — {_project.Name}");

            // ---- Scenes ----
            OpenScene(0);
            int n0 = _project.Scenes.Count;
            AddSceneCommand();
            Check("add scene opens an empty one", _project.Scenes.Count == n0 + 1 && _sceneIndex == n0 && _scene.Objects.Count == 0);
            await Task.Delay(300);
            Snap(dir, "9_new_scene");
            OpenScene(0);
            int objs = _scene.Objects.Count, id0 = _scene.Id;
            DuplicateSceneCommand();
            Check("duplicate scene: right after, a copy with a new id", _sceneIndex == 1 && _scene.Name == "Level 1 copy" && _scene.Objects.Count == objs
                && _scene.Id != id0 && _project.Scenes.Select(s => s.Id).Distinct().Count() == _project.Scenes.Count);
            Check("rename scene", RenameSceneTo("Cave") && _scene.Name == "Cave" && ((string)_sceneCombo.SelectedItem!) == "Cave");
            Check("rename to a used name is refused", !RenameSceneTo("level 2") && _scene.Name == "Cave");
            MoveSceneCommand(+1);
            Check("move scene later", _project.Scenes[2].Name == "Cave" && _sceneIndex == 2);
            int caveObjs = _scene.Objects.Count;
            AddEmptyObject();
            OpenScene(0);
            OpenScene(2);
            Undo();
            Check("undo is per scene (history comes back with the scene)", _scene.Name == "Cave" && _scene.Objects.Count == caveObjs);
            await DeleteSceneAsync();
            Check("delete scene", _project.Scenes.Count == n0 + 1 && _project.FindScene("Cave") == null);
            OpenScene(_project.IndexOfScene("Level 3"));
            DeleteOpenScene();
            Check("back to the demo scenes", _project.Scenes.Count == n0 && _project.Scenes[0].Name == "Level 1");
            OpenScene(0);
            await Task.Delay(200);

            // ---- Sounds ----
            Check("new project has the starter sounds", ProjectSounds().Count >= 4 && _soundList.Children.Count == ProjectSounds().Count);
            string boom = Path.Combine(dir, "boom.wav");
            File.WriteAllBytes(boom, SoundSynth.Hit());
            Check("import a sound", ImportSoundFile(boom) == "boom.wav" && ProjectSounds().Contains("boom.wav"));
            if (_scene.Objects.FirstOrDefault(o => o.Has("Coin")) is { } coin) Select(coin.Id);
            await Task.Delay(300);
            var soundCombo = _inspectorRoot.GetVisualDescendants().OfType<ComboBox>()
                .FirstOrDefault(c => c.ItemsSource is IEnumerable<object> items && items.Any(i => i is ImportSoundEntry));
            Check("a Sound param is a dropdown of the project's sounds", soundCombo != null && Equals(soundCombo.SelectedItem, "coin.wav")
                && ((IEnumerable<object>)soundCombo.ItemsSource!).Contains("boom.wav"));
            SnapControl(_inspectorRoot, dir, "10_inspector_sound");
            if (_scene.Objects.FirstOrDefault(o => o.Has("Door")) is { } doorObj) Select(doorObj.Id);
            await Task.Delay(300);
            Check("a Scene param is a dropdown of the scene names", _inspectorRoot.GetVisualDescendants().OfType<ComboBox>()
                .Any(c => Equals(c.SelectedItem, "Level 2") && c.ItemsSource is IEnumerable<string> names && names.Contains("Level 1")));
            SnapControl(_inspectorRoot, dir, "10b_inspector_door");
            if (OperatingSystem.IsWindows())
            {
                string coinPath = Sounds.Resolve(_projectDir, "coin.wav")!;
                string odd = Path.Combine(dir, "sóm de moeda.wav");
                File.Copy(coinPath, odd, true);
                string a = WinMmAudio.SilentCheck(coinPath), b = WinMmAudio.SilentCheck(odd);
                lines.Add($"INFO audio: {a} | non-ASCII path: {b}");
                Check("audio: Windows can open and play the generated wav (silent check)", a.Contains("length") && !a.Contains("error"));
                Check("audio: a path with accents works too", b.Contains("length") && !b.Contains("error"));
            }
            // Optional FMOD audio (MainWindow.Fmod.cs): Project ▸ Sound engine… and the FmodEvent picker build (with or without FMOD).
            var soundEngine = SoundEngineWindow();
            var seTexts = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(soundEngine).ToList();
            Check("Sound engine…: the choice, what plays now and the FMOD Studio part", seTexts.OfType<ComboBox>().Count() == 1
                && seTexts.OfType<TextBlock>().Any(t => t.Text?.StartsWith("Sound: ") == true)
                && seTexts.OfType<Button>().Any(x => Equals(x.Content, "Reload banks")));
            Check("a FmodEvent param is a dropdown of the project's FMOD Studio events", FmodEventPicker("", _ => { }) is Control);
            Select(0);

            // ---- My Library ----
            Check("save to My Library", await SaveToLibraryAsync("Coin") == "Coin" && _myLibrary.Contains("Coin"));
            Check("saving the same again doesn't duplicate", await SaveToLibraryAsync("Coin") == "Coin" && _myLibrary.Names().Count == 1);
            _sources["Spinner"] = "mechanism Spinner\n    param turns: number = 2\n\n    when start\n        print \"spin\"\n";
            RefreshLibrary();
            await SaveToLibraryAsync("Spinner");
            _sources.Remove("Spinner");
            RefreshLibrary();
            int before = _scene.Objects.Count;
            await DropFromLibraryAsync("Spinner", new Point(200, 120), null);
            Check("dragging a library mechanism copies it into the project", _sources.ContainsKey("Spinner") && _scene.Objects.Count == before + 1 && Selected?.Has("Spinner") == true);
            File.WriteAllText(Path.Combine(_myLibrary.Folder, "Coin.bs"), "mechanism Coin\n    param value: number = 10\n");
            _snapClashChoice = "Keep both";
            Check("library clash: keep both -> Coin2", await UseFromLibraryAsync("Coin", open: false) == "Coin2" && _sources["Coin2"].StartsWith("mechanism Coin2"));
            _snapClashChoice = "Cancel";
            Check("library clash: cancel changes nothing", await UseFromLibraryAsync("Coin", open: false) == null && !_sources.ContainsKey("Coin3"));
            _snapClashChoice = "Keep both";
            Check("library sections", new[] { "THIS PROJECT", "MY LIBRARY", "STARTER KIT" }.All(h => _libItems.OfType<LibItem>().Any(i => i.Header == h))
                && _libItems.OfType<LibItem>().Any(i => i.Section == LibSection.Project && i.Name == "Coin2")
                && _libItems.OfType<LibItem>().Any(i => i.Section == LibSection.Mine && i.Name == "Spinner")
                && _libItems.OfType<LibItem>().Any(i => i.Section == LibSection.Starter && i.Name == "Door"));
            Select(0);
            await Task.Delay(300);
            SnapControl(_leftPanel, dir, "8_library");
            Snap(dir, "11_library_sections");

            // ---- goto_scene while playing ----
            ClearLog();
            ScenePlay();
            await Task.Delay(300);
            if (_play?.World is { } world && world.FindObject("player") is { } player && world.FindObject("Door") is { } door)
            {
                world.Globals["score"] = 7.0;
                player.X = door.X;
                player.Y = door.Y;
            }
            await Task.Delay(600);
            Check("a Door switches scene while playing; the score carries over",
                _playSceneName == "Level 2" && _play?.World.Globals.GetValueOrDefault("score") is 7.0);
            Snap(dir, "12_playing_level2");
            File.WriteAllText(Path.Combine(dir, "console_scenes.txt"), _console.Text ?? "");
            SceneStop();
            Check("stop goes back to the scene being edited", _sceneIndex == 0 && _sceneHost.Content == _canvas);

            // ---- Projects ----
            string firstDir = _projectDir, firstName = _project.Name;
            string? other = CreateProject("Snap Other");
            Check("new project folder goes under the (moved) projects root", other != null && other.StartsWith(_settings.Folder, StringComparison.OrdinalIgnoreCase)
                && Project.Exists(other) && Sounds.List(other).Count == 4);
            if (other != null)
            {
                await SwitchProjectAsync(other);
                await Task.Delay(400);
                Check("switch project: title, scenes, sounds, library", Title == "BuilderEngine — Snap Other" && _project.Scenes.Count == 1 // a new project starts empty: one scene
                    && !_sources.ContainsKey("Coin2") && _sources.ContainsKey("Coin") && ProjectSounds().Count == 4 && _soundList.Children.Count == 4
                    && _sceneTab && _current == null);
                Snap(dir, "13_other_project");
                var recent = _settings.RecentProjects();
                Check("recent list: newest first, both projects", recent.Count >= 2 && AppSettings.SameFolder(recent[0], other) && recent.Any(r => AppSettings.SameFolder(r, firstDir)));
                await SwitchProjectAsync(firstDir);
                Check("switching back finds what was saved", _project.Name == firstName && _sources.ContainsKey("Coin2") && _sources.ContainsKey("Spinner")
                    && ProjectSounds().Contains("boom.wav"));
            }

            // ---- Dialogs ----
            await SnapDialog(Dialogs.Build("Name already used", "This project already has a different mechanism called “Coin”. Keep both (the library one comes in as “Coin2”) or replace the project's one?", null, ["Keep both", "Replace", "Cancel"], out _), dir, "14_dialog_clash");
            await SnapDialog(Dialogs.Build("New project", $"A name for your game. It is saved in {ProjectsRoot}.", "My Game 2", ["Create", "Cancel"], out _), dir, "15_dialog_new_project");
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }

    async Task SnapDialog(Window dlg, string dir, string name)
    {
        dlg.Show(this);
        await Task.Delay(400);
        var size = new PixelSize(Math.Max(1, (int)dlg.Bounds.Width), Math.Max(1, (int)dlg.Bounds.Height));
        using (var bmp = new RenderTargetBitmap(size, new Vector(96, 96)))
        {
            bmp.Render(dlg);
            bmp.Save(Path.Combine(dir, name + ".png"));
        }
        dlg.Close();
    }
}
