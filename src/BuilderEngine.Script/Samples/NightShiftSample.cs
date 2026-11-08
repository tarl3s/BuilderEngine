using System.Reflection;
using System.Runtime.CompilerServices;

namespace BuilderEngine.Script;

/// <summary>
/// "Night Shift at the Museum of Dolls" — a small surveillance horror game made with BuilderEngine. You guard a doll
/// museum from 12 AM to 6 AM: watch six cameras, close the office doors and switch the hall lights while the power
/// drains, and keep Clementine, Corporal Tock, Odette and Pip out. Three nights, each busier; progress is saved.
/// The game is written in Builder Script (Samples/NightShift/*.bs, embedded); this file only lays out the scene and
/// paints the pictures and sounds. <c>BuilderEngine.exe --make-sample "Night Shift" &lt;folder&gt;</c>.
/// </summary>
public static class NightShiftSample
{
    public const string Name = "Night Shift";
    public const string SceneName = "Museum";

    [ModuleInitializer]
    internal static void Register() => SampleGames.All[Name] = Make;

    /// <summary>The game's mechanisms (name → Builder Script source), one per mechanism.</summary>
    public static Dictionary<string, string> Mechanisms()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Order())
        {
            string norm = res.Replace('\\', '.').Replace('/', '.');
            if (!norm.StartsWith("Samples.NightShift.", StringComparison.OrdinalIgnoreCase)) continue;
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            foreach (var (name, src) in Split(r.ReadToEnd())) result[name] = src;
        }
        return result;
    }

    /// <summary>A file with several mechanisms → one source per mechanism (blank lines between them dropped).</summary>
    static IEnumerable<(string Name, string Source)> Split(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var starts = new List<int>();
        for (int i = 0; i < lines.Length; i++) if (lines[i].StartsWith("mechanism ")) starts.Add(i);
        for (int k = 0; k < starts.Count; k++)
        {
            int end = k + 1 < starts.Count ? starts[k + 1] : lines.Length;
            string body = string.Join("\n", lines[starts[k]..end]).TrimEnd() + "\n";
            string name = lines[starts[k]]["mechanism ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            yield return (name, body);
        }
    }

    /// <summary>Writes the project: project.json, mechanisms/, images/, sounds/.</summary>
    public static void Make(string folder)
    {
        var p = Build();
        p.Save(folder);
        NightShiftPictures.WriteAll(Images.Folder(folder));
        NightShiftSounds.WriteAll(Sounds.Folder(folder));
    }

    /// <summary>The project in memory (tests change params on it before playing).</summary>
    public static Project Build()
    {
        var s = new Scene { Id = 1, Name = SceneName, Width = 1240, Height = 1440, Background = "#000000" };
        BuildOffice(s);
        BuildRooms(s);
        BuildScreens(s);
        var p = new Project { Name = Name, Scenes = [s] };
        foreach (var (name, src) in Mechanisms()) p.Mechanisms[name] = src;
        return p;
    }

    // ---------------- Helpers ----------------

    const string Clear = "#00000000";

    static SceneObject Obj(Scene s, string name, double x, double y, double w, double h, string color = Clear, string image = "", string[]? tags = null)
        => s.Add(new SceneObject { Name = name, X = x, Y = y, Width = w, Height = h, Color = color, Image = image, Tags = [.. tags ?? []] });

    static SceneObject With(this SceneObject o, string mechanism, params (string Key, object? Value)[] ps)
    {
        var m = new SceneMechanism { Name = mechanism };
        foreach (var (k, v) in ps) m.Params[k] = v;
        o.Mechanisms.Add(m);
        return o;
    }

    static List<object?> Names(params string[] names) => [.. names];

    // The top-left 800 × 450 of the scene is the screen: the screen objects (HUD, tablet, title…) are laid out there.
    // The office is under it and the six rooms under the office.
    const double OY = 480;

    // ---------------- The office (scene 0, 480 – 800, 930) ----------------

    static void BuildOffice(Scene s)
    {
        Obj(s, "Night Shift", 2, 2, 20, 20, "#5A1E1A").With("NightShift", ("dark_visitor", "Odette"));
        Obj(s, "Office Background", 0, OY, 800, 450, "#1A140F", "office.png");
        Obj(s, "Left Doorway", 52, 122 + OY, 106, 258).With("Doorway", ("door", "Left Door"));
        Obj(s, "Right Doorway", 642, 122 + OY, 106, 258).With("Doorway", ("door", "Right Door"));
        Obj(s, "Left Door", 40, 90 + OY, 130, 290, "#4C4F52", "shutter.png").With("OfficeDoor");
        Obj(s, "Right Door", 630, 90 + OY, 130, 290, "#4C4F52", "shutter.png").With("OfficeDoor");
        Obj(s, "Left Hall Light", 85, 190 + OY, 40, 40).With("HallLight");
        Obj(s, "Right Hall Light", 675, 190 + OY, 40, 40).With("HallLight");
        Obj(s, "Left Door Button", 188, 192 + OY, 46, 32, "#3A2622").With("WallSwitch", ("label", "DOOR"), ("target", "Left Door"));
        Obj(s, "Left Light Button", 188, 230 + OY, 46, 32, "#2E2A1E").With("WallSwitch", ("label", "LIGHT"), ("target", "Left Hall Light"), ("on_color", "#D8C060"), ("off_color", "#2E2A1E"));
        Obj(s, "Right Door Button", 566, 192 + OY, 46, 32, "#3A2622").With("WallSwitch", ("label", "DOOR"), ("target", "Right Door"));
        Obj(s, "Right Light Button", 566, 230 + OY, 46, 32, "#2E2A1E").With("WallSwitch", ("label", "LIGHT"), ("target", "Right Hall Light"), ("on_color", "#D8C060"), ("off_color", "#2E2A1E"));
        Obj(s, "Desk Lamp", 512, 262 + OY, 40, 40);
        Obj(s, "Desk Fan", 250, 268 + OY, 60, 60, Clear, "fan1.png").With("Spinner");
        Obj(s, "Guard Post", 26, 2, 20, 20, "#3A2A1A").With("Office",
            ("lamp", "Desk Lamp"), ("view", "Office View"), ("left_door", "Left Door"), ("right_door", "Right Door"), ("left_light", "Left Hall Light"), ("right_light", "Right Hall Light"));
        Obj(s, "Office View", 0, OY, 800, 450).With("CameraPoint");
    }

    // ---------------- The six rooms the cameras see (below the office) ----------------

    static readonly (string Name, string Image, double X, double Y, string Lamp, bool Near)[] Rooms =
    [
        ("Grand Hall", "room_hall.png", 0, 960, "#FFC890", false),
        ("Doll Gallery", "room_gallery.png", 420, 960, "#A8D0B8", false),
        ("Music Room", "room_music.png", 840, 960, "#B8A8FF", false),
        ("Toy Workshop", "room_workshop.png", 0, 1205, "#FFD8A0", false),
        ("West Corridor", "room_west.png", 420, 1205, "#9DB4FF", true),
        ("East Corridor", "room_east.png", 840, 1205, "#9DB4FF", true),
    ];

    static void BuildRooms(Scene s)
    {
        foreach (var r in Rooms)
        {
            Obj(s, r.Name + " Backdrop", r.X, r.Y, 400, 225, "#14100C", r.Image);
            Obj(s, r.Name, r.X, r.Y, 400, 225, Clear, tags: r.Near ? ["near"] : null).With("Room", ("lamp_color", r.Lamp));
        }
        Obj(s, "Clementine", 30, 1071, 56, 104, "#D8CCB8", "doll.png").With("Creature",
            ("display_name", "Clementine"), ("route", Names("Grand Hall", "Doll Gallery", "West Corridor", "Left Doorway")),
            ("levels", new List<object?> { 5.0, 9.0, 14.0 }), ("shy", true), ("slot", 0.0), ("scare", "scare_doll.png"));
        Obj(s, "Corporal Tock", 250, 1055, 54, 120, "#A81818", "soldier.png").With("Creature",
            ("display_name", "Corporal Tock"), ("route", Names("Grand Hall", "Toy Workshop", "East Corridor", "Right Doorway")),
            ("levels", new List<object?> { 3.0, 7.0, 11.0 }), ("move_every", 6.0), ("knock_cost", 3.0), ("slot", 2.0),
            ("sound", "tick.wav"), ("scare", "scare_soldier.png"));
        Obj(s, "Odette", 1010, 1063, 60, 112, "#EFE6DA", "ballerina.png").With("Ballerina",
            ("route", Names("Music Room", "East Corridor", "Right Doorway")), ("sound", "musicbox.wav"), ("scare", "scare_ballerina.png"));
        Obj(s, "Pip", 150, 1101, 40, 74, "#C8B8D8", "twin.png").With("Creature",
            ("display_name", "Pip"), ("route", Names("Grand Hall", "Doll Gallery", "West Corridor", "Left Doorway")),
            ("alt_route", Names("Grand Hall", "Toy Workshop", "East Corridor", "Right Doorway")),
            ("levels", new List<object?> { 0.0, 6.0, 12.0 }), ("first_night", 2.0), ("move_every", 4.0), ("door_wait", 3.0),
            ("slot", 1.0), ("scare", "scare_twin.png"));
    }

    // ---------------- Screen objects: HUD, tablet, intro, win, lose, title, jumpscare ----------------

    static SceneObject Label(Scene s, string name, double x, double y, double w, double h, string text, double size, string color, string screen, string bg = Clear)
        => Obj(s, name, x, y, w, h, bg, tags: ["ui"]).With("Panel", ("title", text), ("title_size", size), ("title_color", color), ("screen", screen));

    static SceneObject Btn(Scene s, string name, double x, double y, double w, double h, string text, string message, string screen,
        string color = "#5A1E1A", string hover = "#7A2A22", string textColor = "#F2E6D8", double size = 18, string target = "")
    {
        var o = Obj(s, name, x, y, w, h, color, tags: ["ui"]).With("Button", ("text", text), ("message", message), ("screen", screen),
            ("color", color), ("hover_color", hover), ("text_color", textColor), ("text_size", size));
        if (target.Length > 0) o.Mechanisms[0].Params["target"] = target;
        return o;
    }

    static void BuildScreens(Scene s)
    {
        // The tablet itself (not drawn): its cameras are the rooms, its office view the office.
        Obj(s, "Tablet", 50, 2, 20, 20, "#1E2A22").With("Tablet",
            ("cameras", Names(Rooms.Select(r => r.Name).ToArray())), ("office", "Office View"));

        // HUD while on duty.
        Label(s, "Clock", 620, 8, 170, 40, "{clock_text}", 30, "#F2E6D8", "Hud");
        Label(s, "Night Label", 620, 46, 170, 22, "Night {night}", 15, "#B8A894", "Hud");
        Label(s, "Keys Help", 516, 418, 280, 20, "Q / E doors  ·  A / D lights  ·  Tab cameras", 12, "#7A6E60", "Hud");
        Label(s, "Power Label", 8, 384, 230, 26, "Power left: {max(0, floor(power))}%", 17, "#F2E6D8", "Hud");
        Label(s, "Usage Label", 8, 412, 64, 24, "Usage", 14, "#B8A894", "Hud");
        string[] cellColors = ["#6FCF6A", "#6FCF6A", "#D8C34A", "#E0883A", "#E04A3A"];
        for (int i = 0; i < 5; i++)
            Obj(s, $"Usage {i + 1}", 74 + i * 22, 416, 18, 16, "#202820", tags: ["ui"]).With("UsageCell", ("level", i + 1.0), ("color", cellColors[i]));
        Btn(s, "Cameras Button", 290, 412, 220, 30, "CAMERAS   [Tab]", "flip", "Hud", "#16241A", "#24402C", "#CFE8D0", 13, target: "Tablet");

        // The camera tablet: a bezel, the room's name, a blinking REC, the map.
        Label(s, "Bezel Top", 0, 0, 800, 12, "", 10, "#000000", "Cameras", "#101410");
        Label(s, "Bezel Left", 0, 0, 12, 410, "", 10, "#000000", "Cameras", "#101410");
        Label(s, "Bezel Right", 788, 0, 12, 410, "", 10, "#000000", "Cameras", "#101410");
        Label(s, "Cam Label", 22, 18, 340, 30, "{cam_label}", 18, "#D8F0DC", "Cameras");
        Label(s, "Rec", 22, 48, 80, 22, "● REC", 15, "#FF4A4A", "Cameras").With("Blink", ("every", 0.7));
        Label(s, "Map", 536, 226, 252, 182, "", 10, "#000000", "Cameras", "#C00A140D");
        Label(s, "Map Title", 542, 228, 60, 16, "MAP", 10, "#6A8A70", "Cameras");
        Label(s, "Map Hall Line", 664, 262, 2, 22, "", 10, "#000000", "Cameras", "#3A5A40");
        Label(s, "Map Row Line", 586, 292, 158, 2, "", 10, "#000000", "Cameras", "#3A5A40");
        Label(s, "Map West Line", 600, 304, 2, 22, "", 10, "#000000", "Cameras", "#3A5A40");
        Label(s, "Map East Line", 728, 304, 2, 22, "", 10, "#000000", "Cameras", "#3A5A40");
        Label(s, "Map Office Line", 600, 346, 130, 2, "", 10, "#000000", "Cameras", "#3A5A40");
        Label(s, "Map Office Stem", 664, 346, 2, 22, "", 10, "#000000", "Cameras", "#3A5A40");
        (int N, double X, double Y)[] cams = [(1, 630, 240), (2, 552, 282), (4, 630, 282), (3, 708, 282), (5, 566, 324), (6, 694, 324)];
        foreach (var (n, x, y) in cams)
            Obj(s, $"Cam {n}", x, y, 70, 24, "#18301F", tags: ["ui"]).With("CamButton", ("number", (double)n), ("tablet", "Tablet"));
        Label(s, "Map You", 630, 368, 70, 24, "YOU", 11, "#F2D8B8", "Cameras", "#4A2E1A");

        // Night intro.
        Label(s, "Intro Back", 0, 0, 800, 450, "", 10, "#000000", "Intro", "#000000");
        Label(s, "Intro Night", 150, 140, 500, 64, "Night {night}", 46, "#F2E6D8", "Intro");
        Label(s, "Intro Time", 150, 204, 500, 30, "12:00 AM", 20, "#B8A894", "Intro");
        Label(s, "Intro Hint", 130, 290, 540, 60, "{hint}", 14, "#8E8070", "Intro");

        // 6 AM.
        Label(s, "Win Back", 0, 0, 800, 450, "", 10, "#000000", "Win", "#D0080604");
        Label(s, "Win Six", 150, 100, 500, 90, "6 AM", 72, "#F4D58D", "Win");
        Label(s, "Win Text", 100, 196, 600, 50, "{win_text}", 16, "#E8DCC8", "Win");
        Btn(s, "Next Night", 290, 276, 220, 46, "Next night", "next night", "Win");
        Btn(s, "Win Title", 290, 276, 220, 46, "Back to the title", "to title", "Win");

        // Game over.
        Label(s, "Lose Back", 0, 0, 800, 450, "", 10, "#000000", "Lose", "#F0080000");
        Label(s, "Game Over", 100, 110, 600, 80, "GAME OVER", 60, "#E04040", "Lose");
        Label(s, "Lose Text", 100, 196, 600, 34, "{caught_by} got into the office.", 18, "#E8DCC8", "Lose");
        Btn(s, "Try Again", 176, 276, 210, 46, "Try again", "retry", "Lose");
        Btn(s, "Lose Title", 414, 276, 210, 46, "Title screen", "to title", "Lose");

        // Title.
        Obj(s, "Title Art", 0, 0, 800, 450, "#0A0606", "title.png", ["ui"]).With("Panel", ("screen", "Title"));
        Label(s, "Title Name", 30, 78, 460, 66, "NIGHT SHIFT", 56, "#E8DCC8", "Title");
        Label(s, "Title Sub", 30, 142, 460, 30, "at the Museum of Dolls", 21, "#C07A6A", "Title");
        Btn(s, "New Game", 140, 214, 240, 46, "New game", "new game", "Title");
        Btn(s, "Continue", 140, 270, 240, 46, "Continue  ·  Night {best}", "continue", "Title");
        Label(s, "Trophy", 60, 326, 400, 24, "★  All three nights survived", 14, "#F4D58D", "Title");
        Label(s, "Title Help", 20, 384, 480, 50, "Survive from 12 AM to 6 AM. Watch the cameras, shut the doors in time — and save the power.", 13, "#9A8C7C", "Title");

        Obj(s, "Jumpscare", -20, -14, 840, 478, Clear, tags: ["ui"]).With("Jumpscare");
    }
}
