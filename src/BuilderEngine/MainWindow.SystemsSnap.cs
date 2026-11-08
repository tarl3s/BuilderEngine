using Avalonia.Controls;
using Avalonia.VisualTree;
using BuilderEngine.Script;

namespace BuilderEngine;

// SYSTEMS AND EXPORT, dev tool (`--snap <dir> --snap-only systems`): adds systems from the library the way the UI does,
// places their parts, opens one as blocks, plays, removes one, makes a new one, saves it to My Library and exports the
// game (building the player with the .NET SDK into the snap folder). Appends PASS/FAIL lines to checks.txt, renders
// PNGs and writes the exported game's folder to export.txt (to launch it afterwards).
public sealed partial class MainWindow
{
    async Task SnapSystems(string dir)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add((ok ? "PASS " : "FAIL ") + what);
        try
        {
            SceneStop();
            ShowTab(scene: true);
            var s = _project.AddScene("Systems Test", _scene);
            s.Add(new SceneObject { Name = "Ground", X = 0, Y = 400, Width = s.Width, Height = 50, Color = "#6B5B4B", Mechanisms = [new SceneMechanism { Name = "Solid" }] });
            RefreshScenes();
            OpenScene(_project.Scenes.IndexOf(s));
            await Task.Delay(300);
            _canvas.Fit();

            // The library's SYSTEMS section.
            RefreshLibrary();
            var rows = _libItems.OfType<LibItem>().Where(i => i.Section == LibSection.Systems && !i.IsHeader).Select(i => i.Name).ToList();
            Check($"the library lists the starter systems ({string.Join(", ", rows)})", Systems.Starter().All(st => rows.Contains(st.Name)) && rows.Contains(NewSystemRow));
            await Task.Delay(200);
            SnapControl(_leftPanel, dir, "60_library_systems");

            // Add Inventory with a click (as OnLibraryItemClicked does), PlatformerKit with a drop.
            OnLibraryItemClicked(new LibItem("Inventory", LibSection.Systems));
            await Task.Delay(300);
            var inv = _scene.Objects.FirstOrDefault(o => o.Has("Inventory"));
            Check("a click adds the Inventory system: one object, selected, its code in the project", inv != null && _canvas.SelectedId == inv.Id && _sources.ContainsKey("Inventory"));
            Check("the system shows in THIS PROJECT", _libItems.OfType<LibItem>().Any(i => i.Section == LibSection.Project && i.Name == "Inventory"));
            await Task.Delay(300);
            SnapControl(_inspectorRoot, dir, "61_inventory_card");
            Check("its card has the params and the parts to place", _inspectorRoot.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "+ Pickup")
                && _inspectorRoot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "slots"));
            PlaceSystemPart(inv!, "Pickup");
            PlaceSystemPart(inv!, "Pickup");
            PlaceSystemPart(inv!, "Recipe");
            Check("+ Pickup places pickups", _scene.Objects.Count(o => o.Has("Pickup")) == 2);

            OnMechanismDropped(SysDragTag + "PlatformerKit", new Avalonia.Point(520, 80), null);
            await Task.Delay(300);
            var kit = _scene.Objects.FirstOrDefault(o => o.Has("PlatformerKit"));
            Check("dropping PlatformerKit adds it where it was dropped", kit != null && Math.Abs(kit.X - 504) <= 16);
            kit!.Mechanisms[0].Params["speed"] = 320.0;
            PlaceSystemPart(kit, "Platformer");
            var player = _scene.Objects.Last(o => o.Has("Platformer"));
            player.X = 120; player.Y = 340;
            PlaceSystemPart(kit, "Coin");
            PlaceSystemPart(kit, "Checkpoint");
            Check("the placed Platformer is the player", player.Tags.Contains("player"));
            Select(kit.Id);
            await Task.Delay(300);
            Snap(dir, "62_scene_with_systems");
            SnapControl(_inspectorRoot, dir, "62b_platformer_kit_card");

            // The system as blocks and code.
            OpenMechanism("Inventory");
            await Task.Delay(700);
            Check("the code starts with 'system Inventory'", (_code.Text ?? "").StartsWith("system Inventory"));
            var words = _blocksBox.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Check("the blocks show 'system' and 'includes' rows", words.Contains("system") && words.Count(w => w == "includes") == 2);
            Snap(dir, "63_blocks_system");
            ShowTab(scene: true);

            // Play: the crafting menu and the hotbar come from the system; the player runs at the kit's speed.
            ClearLog();
            ScenePlay();
            await Task.Delay(400);
            var world = _play?.World;
            world?.KeysDown.Add("right");
            await Task.Delay(1200);
            Snap(dir, "64_playing_systems");
            var p = world?.FindObject("player");
            Check("playing: the crafting menu was made", world != null && world.Objects.Any(o => o.Mechanisms.Any(m => m.Def.Name == "Crafting")));
            Check($"playing: the player has the kit's speed ({p?.AllMechanisms().FirstOrDefault(m => m.Def.Name == "Platformer")?.Vars.GetValueOrDefault("speed")})",
                p != null && Equals(p.AllMechanisms().First(m => m.Def.Name == "Platformer").Vars["speed"], 320.0));
            Check($"playing: no script errors ({string.Join(" | ", world?.Errors ?? [])})", world != null && world.Errors.Count == 0);
            SceneStop();
            await Task.Delay(300);

            // Remove Inventory: its object and its (unchanged) code go; the pickups stay.
            await RemoveSystemAsync("Inventory");
            Check("removing Inventory: object and code gone, pickups kept", !_scene.Objects.Any(o => o.Has("Inventory")) && !_sources.ContainsKey("Inventory") && _scene.Objects.Count(o => o.Has("Pickup")) == 2);

            // New system from the selected objects' mechanisms, saved to My Library.
            Check("New system: a bad name is refused", MakeSystem("my kit", ["Coin"]) != null);
            Check("New system from Coin + Checkpoint", MakeSystem("SnapKit", ["Coin", "Checkpoint"]) == null && Systems.IsSystemSource(_sources.GetValueOrDefault("SnapKit")));
            SaveSystemToLibrary("SnapKit");
            RefreshLibrary();
            Check("saved to My Library: listed in SYSTEMS as mine, not in MY LIBRARY", _libItems.OfType<LibItem>().Any(i => i.Section == LibSection.Systems && i.Name == "SnapKit")
                && !_libItems.OfType<LibItem>().Any(i => i.Section == LibSection.Mine && i.Name == "SnapKit"));
            await Task.Delay(200);
            SnapControl(_leftPanel, dir, "65_library_with_mine");

            // Export: build the player into the snap folder, then export with a zip (the systems scene first: the game starts there).
            _project.MoveScene(_project.Scenes.IndexOf(s), 0);
            TrySave();
            string? csproj = GameExport.FindPlayerProject(AppContext.BaseDirectory);
            Check("finds the player project from the source build", csproj != null);
            var buildLog = new List<string>();
            string? playerDir = csproj == null ? null : await Task.Run(() => GameExport.PublishPlayer(csproj, Path.Combine(dir, "player"), buildLog.Add));
            File.WriteAllLines(Path.Combine(dir, "player_build.txt"), buildLog);
            Check("dotnet publish builds the player", playerDir != null);
            var result = await RunExportAsync(new GameExport.Options(_projectDir, Path.Combine(dir, "export"), playerDir, Zip: true));
            await Task.Delay(500);
            if (_exportWindow?.Content is Control c) SnapControl(c, dir, "66_export_done");
            _exportWindow?.Close();
            Check("exported: the game's exe, its project and the zip", result?.Exe != null && File.Exists(result.Exe) && Project.Exists(Path.Combine(result.Folder, GameExport.GameFolder)) && File.Exists(result!.Zip!));
            if (result?.Exe != null) File.WriteAllText(Path.Combine(dir, "export.txt"), result.Exe);
        }
        catch (Exception e) { lines.Add("FAIL exception: " + e); }
        File.AppendAllLines(Path.Combine(dir, "checks.txt"), lines);
    }
}
