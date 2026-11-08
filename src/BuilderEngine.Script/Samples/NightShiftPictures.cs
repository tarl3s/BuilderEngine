using static BuilderEngine.Script.NightShiftCanvas;

namespace BuilderEngine.Script;

/// <summary>
/// The Night Shift sample's pictures, painted from code (original art: a museum office, six rooms, four creatures,
/// their jumpscare close-ups, a shutter, a desk fan and the title picture). Written into the project's images folder.
/// </summary>
internal static class NightShiftPictures
{
    public static void WriteAll(string imagesDir)
    {
        Directory.CreateDirectory(imagesDir);
        void Save(string name, NightShiftCanvas c) => c.Save(Path.Combine(imagesDir, name));
        Save("office.png", Office());
        Save("shutter.png", Shutter());
        Save("fan1.png", Fan(0));
        Save("fan2.png", Fan(1));
        Save("room_hall.png", Hall());
        Save("room_gallery.png", Gallery());
        Save("room_music.png", MusicRoom());
        Save("room_workshop.png", Workshop());
        Save("room_west.png", Corridor(east: false));
        Save("room_east.png", Corridor(east: true));
        Save("doll.png", Doll());
        Save("ballerina.png", Ballerina(restless: false));
        Save("ballerina_restless.png", Ballerina(restless: true));
        Save("soldier.png", Soldier());
        Save("twin.png", Twin());
        Save("scare_doll.png", Scare(ScareKind.Doll));
        Save("scare_ballerina.png", Scare(ScareKind.Ballerina));
        Save("scare_soldier.png", Scare(ScareKind.Soldier));
        Save("scare_twin.png", Scare(ScareKind.Twin));
        Save("title.png", Title());
    }

    static readonly Rgba Black = C("#000000");
    static readonly Rgba Porcelain = C("#EDE5D6");
    static readonly Rgba PorcelainShade = C("#C9BBA8");

    // ---------------- The office ----------------

    static NightShiftCanvas Office()
    {
        var c = new NightShiftCanvas(800, 450, 2);
        // Wallpaper: dark stripes with small diamonds.
        c.VGradient(0, 0, 800, 260, C("#3A2C22"), C("#251C15"));
        for (int x = 0; x < 800; x += 36)
        {
            c.Rect(x + 12, 0, 12, 260, C("#4A382A", 0.5));
            for (int y = 18; y < 250; y += 34) c.Poly(C("#5A4532", 0.55), x + 18, y - 6, x + 23, y, x + 18, y + 6, x + 13, y);
        }
        // Wainscot and skirting.
        c.Rect(0, 250, 800, 82, C("#2A1B12"));
        for (int x = 8; x < 800; x += 66) { c.Rect(x, 258, 56, 64, C("#33221A")); c.Rect(x + 4, 262, 48, 56, C("#2C1D15")); }
        c.Rect(0, 248, 800, 5, C("#4A3426"));
        // Floor: checker tiles in perspective towards the middle of the room.
        c.Rect(0, 332, 800, 118, C("#1C1714"));
        double vx = 400, vy = 180;
        double[] rows = [332, 346, 364, 388, 418, 452];
        for (int r = 0; r + 1 < rows.Length; r++)
        {
            double ya = rows[r], yb = rows[r + 1];
            for (int k = -14; k < 14; k++)
            {
                if (((k + r) & 1) != 0) continue;
                double Xa(double bx, double y) => vx + (bx - vx) * (y - vy) / (450 - vy);
                double b0 = 400 + k * 70, b1 = b0 + 70;
                c.Poly(C("#2C2622"), Xa(b0, ya), ya, Xa(b1, ya), ya, Xa(b1, yb), yb, Xa(b0, yb), yb);
            }
        }
        // The two doorways: frames, a dark hallway beyond, the shutter housing above.
        Doorway(c, 40, false);
        Doorway(c, 630, true);
        // Things on the wall.
        c.Rect(286, 64, 92, 116, C("#6B5326")); c.Rect(292, 70, 80, 104, C("#1E1915")); // a portrait of a doll
        MiniDoll(c, 332, 84, 0.55);
        c.Rect(420, 70, 84, 112, C("#2E3A34")); c.Rect(426, 76, 72, 100, C("#C9B48A", 0.8)); // a museum poster
        c.Ellipse(462, 108, 22, 22, C("#6A2A24")); c.Rect(436, 140, 52, 6, C("#3A2A20")); c.Rect(442, 152, 40, 4, C("#3A2A20")); c.Rect(446, 162, 32, 4, C("#3A2A20"));
        c.Rect(522, 96, 56, 46, C("#E8E0CC", 0.85)); // a child's drawing pinned up: four figures
        for (int i = 0; i < 4; i++)
        {
            double fx = 530 + i * 12;
            c.Ellipse(fx + 2, 110, 3.5, 3.5, C(i == 2 ? "#B03030" : "#303030"));
            c.Line(fx + 2, 113, fx + 2, 126, 1.6, C("#303030")); c.Line(fx + 2, 126, fx - 1, 134, 1.4, C("#303030")); c.Line(fx + 2, 126, fx + 5, 134, 1.4, C("#303030"));
        }
        c.Rect(214, 120, 52, 30, C("#7A1E1A")); c.Rect(217, 123, 46, 24, C("#2A0E0C")); // "staff only" plate
        c.Rect(222, 131, 30, 3, C("#C9A86A")); c.Rect(222, 137, 22, 3, C("#C9A86A"));
        // The desk, with a phone, an old monitor, papers, a mug and the lamp.
        c.Rect(206, 326, 388, 16, C("#4E3322"));
        c.Rect(206, 340, 388, 6, C("#2E1D12"));
        c.Rect(214, 346, 372, 104, C("#2A1A10"));
        c.Rect(232, 360, 120, 70, C("#33211A")); c.Rect(448, 360, 120, 70, C("#33211A"));
        c.Rect(286, 392, 14, 4, C("#8A6A3A")); c.Rect(500, 392, 14, 4, C("#8A6A3A"));
        c.Rect(372, 254, 104, 74, C("#4A4842")); c.Rect(380, 262, 88, 56, C("#0E1A12")); // the monitor
        c.Glow(424, 290, 40, C("#3F8A55", 0.35));
        c.Rect(410, 326, 28, 4, C("#3A3834"));
        c.Rect(318, 318, 44, 10, C("#E8E0CC", 0.9)); c.Rect(322, 314, 40, 8, C("#D8D0BC", 0.9)); // papers
        c.Rect(482, 306, 16, 22, C("#7A2A24")); c.Ellipse(490, 306, 8, 2.5, C("#2A1008")); // mug
        c.Rect(220, 312, 50, 16, C("#151313")); c.Ellipse(245, 312, 24, 7, C("#1E1B1B")); c.Ellipse(245, 312, 9, 4, C("#3A3636")); // phone
        // Desk lamp (the light object sits on it).
        c.Rect(526, 300, 12, 28, C("#2E2A24")); c.Ellipse(532, 326, 18, 4, C("#2E2A24"));
        c.Poly(C("#6A5A3A"), 512, 300, 552, 300, 542, 268, 522, 268);
        c.Glow(532, 304, 26, C("#FFD27A", 0.6));
        c.Grain(0.05, 11);
        c.Vignette(0.55, Black);
        return c;
    }

    static void Doorway(NightShiftCanvas c, double x, bool right)
    {
        // The dark hallway beyond (a little perspective), then the frame and the shutter housing.
        c.Rect(x, 90, 130, 290, C("#060606"));
        double inner = right ? x + 18 : x + 30;
        c.Poly(C("#121010"), x, 380, x + 130, 380, inner + 82, 300, inner, 300);
        c.VGradient(inner, 160, 82, 140, C("#000000", 0), C("#141110", 0.8));
        c.Rect(x - 10, 80, 10, 300, C("#4A3A2C")); c.Rect(x + 130, 80, 10, 300, C("#4A3A2C"));
        c.Rect(x - 10, 80, 150, 10, C("#4A3A2C"));
        c.Rect(x - 6, 80, 3, 300, C("#6A5442", 0.6)); c.Rect(x + 133, 80, 3, 300, C("#6A5442", 0.6));
        c.Rect(x - 8, 64, 146, 20, C("#3A3A3A")); c.Rect(x - 8, 82, 146, 3, C("#202020"));
        for (int i = 0; i < 6; i++) c.Ellipse(x + 4 + i * 24, 74, 2, 2, C("#6A6A6A"));
        // The button plate next to the doorway.
        double bx = right ? x - 66 : x + 142;
        c.Rect(bx, 182, 58, 86, C("#2A2622")); c.Rect(bx + 2, 184, 54, 82, C("#3A342E"));
    }

    static void MiniDoll(NightShiftCanvas c, double cx, double top, double k)
    {
        c.Ellipse(cx, top + 40 * k, 30 * k, 32 * k, C("#3A2216"));
        c.Ellipse(cx, top + 44 * k, 22 * k, 25 * k, Porcelain);
        c.Ellipse(cx - 9 * k, top + 42 * k, 5 * k, 6 * k, Black); c.Ellipse(cx + 9 * k, top + 42 * k, 5 * k, 6 * k, Black);
        c.Poly(C("#8A6070"), cx - 26 * k, top + 140 * k, cx + 26 * k, top + 140 * k, cx + 12 * k, top + 72 * k, cx - 12 * k, top + 72 * k);
    }

    static NightShiftCanvas Shutter()
    {
        var c = new NightShiftCanvas(130, 290, 2);
        c.Fill(C("#4C4F52"));
        for (int y = 0; y < 270; y += 14)
        {
            c.Rect(0, y, 130, 11, C("#5E6266"));
            c.Rect(0, y + 11, 130, 3, C("#2C2E30"));
            c.Rect(0, y + 1, 130, 2, C("#7A7E82", 0.5));
        }
        for (int x = 0; x < 130; x += 20) c.Poly(C("#D8B030"), x, 290, x + 10, 290, x + 20, 272, x + 10, 272);
        for (int x = 10; x < 130; x += 20) c.Poly(C("#1A1A1A"), x, 290, x + 10, 290, x + 20, 272, x + 10, 272);
        c.Rect(0, 268, 130, 4, C("#202020"));
        c.Rect(54, 250, 22, 8, C("#2A2A2A"));
        c.Grain(0.06, 3);
        return c;
    }

    static NightShiftCanvas Fan(int frame)
    {
        var c = new NightShiftCanvas(60, 60, 3);
        c.Rect(24, 44, 12, 10, C("#2A2A28")); c.Ellipse(30, 56, 16, 4, C("#232321"));
        c.Ellipse(30, 26, 22, 22, C("#3A3A36", 0.9));
        c.Ellipse(30, 26, 19, 19, C("#141412"));
        for (int b = 0; b < 3; b++)
        {
            double a = frame * Math.PI / 3 + b * 2 * Math.PI / 3;
            double ex = 30 + Math.Cos(a) * 17, ey = 26 + Math.Sin(a) * 17;
            double sx = 30 + Math.Cos(a + 0.5) * 15, sy = 26 + Math.Sin(a + 0.5) * 15;
            c.Poly(C("#8A8A80"), 30, 26, ex, ey, sx, sy);
        }
        for (int r = 6; r <= 20; r += 7) { c.Ellipse(30, 26, r, r, C("#5A5A54", 0.0)); }
        for (int k = 0; k < 8; k++) { double a = k * Math.PI / 4; c.Line(30, 26, 30 + Math.Cos(a) * 21, 26 + Math.Sin(a) * 21, 0.8, C("#6A6A64", 0.8)); }
        c.Ellipse(30, 26, 4, 4, C("#6A6A64"));
        return c;
    }

    // ---------------- The rooms (what the cameras see) ----------------

    static NightShiftCanvas RoomBase(Rgba wallTop, Rgba wallBottom, Rgba floor, int seed)
    {
        var c = new NightShiftCanvas(400, 225, 2);
        c.VGradient(0, 0, 400, 160, wallTop, wallBottom);
        c.Rect(0, 158, 400, 4, C("#140E0A"));
        c.VGradient(0, 162, 400, 63, floor, floor.Mix(Black, 0.5));
        for (int x = -200; x < 600; x += 40) c.Line(200 + (x - 200) * 0.35, 162, x, 225, 1, C("#000000", 0.25));
        c.Grain(0.08, seed);
        return c;
    }

    static void Finish(NightShiftCanvas c)
    {
        c.Vignette(0.7, Black);
        for (int y = 0; y < 225; y += 3) c.Rect(0, y, 400, 1, C("#000000", 0.18)); // scan lines
    }

    static NightShiftCanvas Hall()
    {
        var c = RoomBase(C("#3A1E1C"), C("#24110F"), C("#2E2018"), 21);
        for (int i = 0; i < 4; i++) // columns and arches
        {
            double x = 30 + i * 110;
            c.Rect(x, 20, 18, 140, C("#4A2E26")); c.Rect(x + 3, 20, 4, 140, C("#5E3E32", 0.7));
        }
        for (int i = 0; i < 3; i++) c.Ellipse(94 + i * 110, 38, 46, 30, C("#1A0C0A"));
        c.Rect(0, 0, 400, 22, C("#1A0E0C"));
        c.Rect(130, 26, 140, 22, C("#6A1A18")); c.Rect(136, 30, 128, 14, C("#8A2420")); // a banner
        for (int i = 0; i < 8; i++) c.Rect(146 + i * 14, 34, 8, 6, C("#D8B060", 0.85));
        // The stage everyone starts on, with curtains.
        c.Rect(20, 172, 360, 30, C("#3A2214")); c.Rect(20, 170, 360, 4, C("#5A3A22"));
        c.Rect(20, 202, 360, 8, C("#1A0E08"));
        c.Poly(C("#7A1414"), 0, 0, 60, 0, 40, 60, 46, 200, 0, 205);
        c.Poly(C("#7A1414"), 400, 0, 340, 0, 360, 60, 354, 200, 400, 205);
        for (int k = 0; k < 4; k++) { c.Line(10 + k * 10, 0, 6 + k * 9, 200, 2, C("#4A0808", 0.7)); c.Line(390 - k * 10, 0, 394 - k * 9, 200, 2, C("#4A0808", 0.7)); }
        // A chandelier.
        c.Line(200, 0, 200, 30, 1.5, C("#2A2A2A"));
        c.Poly(C("#5A4A2A"), 170, 30, 230, 30, 216, 44, 184, 44);
        for (int k = 0; k < 5; k++) c.Glow(176 + k * 12, 28, 6, C("#FFD890", 0.6));
        Finish(c);
        return c;
    }

    static NightShiftCanvas Gallery()
    {
        var c = RoomBase(C("#26302A"), C("#18201B"), C("#2A241E"), 22);
        for (int x = 0; x < 400; x += 24) c.Rect(x, 0, 2, 158, C("#2E3A33", 0.8));
        for (int i = 0; i < 3; i++) // glass cabinets full of dolls
        {
            double x = 22 + i * 128;
            c.Rect(x, 30, 100, 150, C("#3A2A1E")); c.Rect(x + 6, 36, 88, 138, C("#101612"));
            for (int s = 0; s < 3; s++)
            {
                double sy = 76 + s * 44;
                c.Rect(x + 6, sy, 88, 4, C("#4A3626"));
                for (int d = 0; d < 4; d++)
                {
                    double dx = x + 18 + d * 21;
                    c.Ellipse(dx, sy - 26, 6, 6, C("#D8CCB8", 0.85));
                    c.Ellipse(dx - 2, sy - 27, 1.4, 1.6, Black); c.Ellipse(dx + 2, sy - 27, 1.4, 1.6, Black);
                    c.Poly(C((d + s) % 2 == 0 ? "#6A3A4A" : "#3A4A6A", 0.9), dx - 7, sy, dx + 7, sy, dx + 3, sy - 19, dx - 3, sy - 19);
                }
            }
            c.Poly(C("#FFFFFF", 0.07), x + 10, 36, x + 30, 36, x + 12, 174, x + 6, 174); // glass shine
        }
        c.Ellipse(200, 205, 150, 14, C("#3A1A1A", 0.7)); // a rug
        Finish(c);
        return c;
    }

    static NightShiftCanvas MusicRoom()
    {
        var c = RoomBase(C("#232036"), C("#151324"), C("#221C1A"), 23);
        for (int y = 14; y < 150; y += 28) for (int x = 14; x < 400; x += 28) c.Ellipse(x, y, 3, 3, C("#3A3456", 0.7));
        // A grand piano on the left.
        c.Poly(C("#0C0A0C"), 14, 120, 120, 120, 140, 150, 130, 170, 14, 170);
        c.Rect(14, 112, 106, 10, C("#1A161A")); for (int k = 0; k < 14; k++) c.Rect(18 + k * 7, 122, 5, 6, C(k % 3 == 1 ? "#1A1A1A" : "#D8D4C8"));
        c.Rect(24, 170, 6, 30, C("#0C0A0C")); c.Rect(118, 170, 6, 30, C("#0C0A0C"));
        // A gramophone on the right.
        c.Rect(318, 150, 50, 40, C("#3A2414")); c.Poly(C("#8A6A2A"), 340, 150, 352, 150, 390, 90, 344, 96);
        c.Ellipse(368, 93, 22, 12, C("#5A4418"));
        // The music box Odette dances on (middle, at the bottom).
        c.Rect(160, 196, 80, 26, C("#5A3A1A")); c.Rect(160, 194, 80, 4, C("#C9A04A"));
        c.Rect(164, 202, 72, 3, C("#C9A04A", 0.7)); c.Rect(236, 204, 10, 4, C("#C9A04A")); c.Ellipse(250, 206, 4, 4, C("#C9A04A"));
        c.Glow(200, 120, 90, C("#B0A0FF", 0.12));
        Finish(c);
        return c;
    }

    static NightShiftCanvas Workshop()
    {
        var c = RoomBase(C("#2E2618"), C("#1C170E"), C("#2A2014"), 24);
        c.Rect(30, 30, 160, 90, C("#4A3A22")); // pegboard with tools
        for (int y = 38; y < 116; y += 10) for (int x = 38; x < 186; x += 10) c.Ellipse(x, y, 1, 1, C("#2A2010"));
        c.Line(60, 44, 60, 100, 4, C("#6A6A6A")); c.Rect(52, 40, 16, 10, C("#3A3A3A"));
        c.Line(100, 44, 110, 104, 3, C("#8A5A2A")); c.Ellipse(150, 70, 20, 20, C("#6A6A6A")); c.Ellipse(150, 70, 8, 8, C("#2A2A2A"));
        Gear(c, 300, 60, 34, C("#5A4A2A")); Gear(c, 352, 104, 22, C("#4A3A20"));
        c.Rect(20, 150, 230, 12, C("#5A3E22")); c.Rect(30, 162, 10, 50, C("#3A2814")); c.Rect(230, 162, 10, 50, C("#3A2814")); // workbench
        for (int i = 0; i < 6; i++) // toy soldiers waiting on the bench
        {
            double x = 50 + i * 30;
            c.Rect(x, 128, 10, 22, C("#8A1A1A")); c.Rect(x, 120, 10, 9, C("#D8C0A0")); c.Rect(x - 1, 110, 12, 11, C("#141414"));
        }
        c.Rect(276, 166, 90, 40, C("#3A2A18")); c.Rect(282, 172, 78, 28, C("#2A1E10")); // a crate
        Finish(c);
        return c;
    }

    static void Gear(NightShiftCanvas c, double cx, double cy, double r, Rgba col)
    {
        for (int k = 0; k < 10; k++)
        {
            double a = k * Math.PI / 5;
            c.Line(cx + Math.Cos(a) * r * 0.7, cy + Math.Sin(a) * r * 0.7, cx + Math.Cos(a) * r * 1.12, cy + Math.Sin(a) * r * 1.12, r * 0.28, col);
        }
        c.Ellipse(cx, cy, r, r, col);
        c.Ellipse(cx, cy, r * 0.35, r * 0.35, C("#1A140C"));
    }

    static NightShiftCanvas Corridor(bool east)
    {
        var c = new NightShiftCanvas(400, 225, 2);
        c.Fill(C("#0A0808"));
        double vx = east ? 230 : 170, vy = 104;
        c.Poly(C("#2A2018"), 0, 0, vx - 34, vy - 44, vx - 34, vy + 40, 0, 225);    // left wall
        c.Poly(C("#241C15"), 400, 0, vx + 34, vy - 44, vx + 34, vy + 40, 400, 225); // right wall
        c.Poly(C("#1A1410"), 0, 225, vx - 34, vy + 40, vx + 34, vy + 40, 400, 225); // floor
        c.Poly(C("#4A1A1A"), 150, 225, vx - 10, vy + 40, vx + 10, vy + 40, 250, 225); // a long rug
        c.Poly(C("#100C0A"), 0, 0, vx - 34, vy - 44, vx + 34, vy - 44, 400, 0);     // ceiling
        c.Rect(vx - 34, vy - 44, 68, 84, C("#020202"));                              // the far end: dark
        for (int k = 0; k < 3; k++) // doors along the walls
        {
            double t = 0.15 + k * 0.25;
            double lx = t * (vx - 34), ly0 = t * (vy - 44), ly1 = 225 + t * (vy + 40 - 225);
            double lx2 = lx + 26 * (1 - t);
            c.Poly(C("#140E0A"), lx, ly0 + (ly1 - ly0) * 0.25, lx2, ly0 + (ly1 - ly0) * 0.28 + 4, lx2, ly1 - 6, lx, ly1);
        }
        if (east)
        {
            // A grandfather clock and a moonlit window.
            c.Rect(300, 50, 34, 140, C("#3A2414")); c.Ellipse(317, 76, 13, 13, C("#C8C0A8")); c.Line(317, 76, 317, 68, 1.5, Black); c.Line(317, 76, 323, 79, 1.5, Black);
            c.Rect(308, 100, 18, 60, C("#1A0E06")); c.Ellipse(317, 146, 6, 6, C("#C9A04A"));
            c.Poly(C("#5A6A8A", 0.7), 48, 40, 96, 52, 96, 120, 48, 124);
            c.Glow(80, 150, 60, C("#8AA0D0", 0.18));
        }
        else
        {
            c.Rect(56, 50, 46, 58, C("#6B5326")); c.Rect(61, 55, 36, 48, C("#1A1612")); // a portrait
            c.Ellipse(79, 74, 10, 12, C("#C8BCA8", 0.8)); c.Ellipse(75, 72, 2, 2.5, Black); c.Ellipse(83, 72, 2, 2.5, Black);
            c.Rect(320, 120, 30, 50, C("#2A1E14")); c.Ellipse(335, 116, 18, 12, C("#3A6A3A", 0.6)); // a plant stand
        }
        c.Grain(0.08, east ? 26 : 25);
        Finish(c);
        return c;
    }

    // ---------------- The creatures ----------------

    static NightShiftCanvas Doll()
    {
        // Clementine: a porcelain doll with ringlets, hollow eyes and a crack across her face.
        var c = new NightShiftCanvas(120, 220, 3);
        var hair = C("#3A2014");
        c.Ellipse(60, 52, 44, 44, hair);
        for (int i = 0; i < 4; i++) { c.Ellipse(20 + i * 2, 70 + i * 20, 10, 12, hair); c.Ellipse(100 - i * 2, 70 + i * 20, 10, 12, hair); }
        c.Ellipse(60, 60, 32, 36, Porcelain);
        c.Ellipse(60, 66, 30, 30, PorcelainShade.WithAlpha(0.25));
        c.Poly(hair, 28, 44, 92, 44, 84, 30, 60, 22, 36, 30); // fringe
        c.Ellipse(47, 62, 12, 14, C("#6A5A50", 0.35)); c.Ellipse(73, 62, 12, 14, C("#6A5A50", 0.35)); // shadowed sockets
        c.Ellipse(47, 60, 9, 11, Black); c.Ellipse(73, 60, 9, 11, Black);
        c.Ellipse(47, 61, 2, 2, C("#FF3A2A")); c.Ellipse(73, 61, 2, 2, C("#FF3A2A"));
        c.Ellipse(41, 77, 6, 4, C("#C87A7A", 0.5)); c.Ellipse(79, 77, 6, 4, C("#C87A7A", 0.5));
        c.Path(1.6, C("#5A141C"), 50, 84, 56, 85, 64, 85, 70, 84); // a thin, too-wide smile
        c.Line(52, 82, 52, 87, 1, C("#5A141C")); c.Line(60, 83, 60, 88, 1, C("#5A141C")); c.Line(68, 82, 68, 87, 1, C("#5A141C"));
        c.Path(1.4, C("#4A3A30"), 70, 28, 66, 40, 72, 50, 64, 64, 68, 74); // the crack
        c.Rect(54, 94, 12, 10, Porcelain);
        c.Poly(C("#F2EEE4"), 34, 104, 86, 104, 80, 116, 40, 116); // lace collar
        c.Poly(C("#7A5A72"), 40, 112, 80, 112, 104, 186, 16, 186); // dress
        for (int k = 0; k < 6; k++) c.Line(30 + k * 12, 186, 50 + k * 4, 114, 1.4, C("#5A3A52", 0.8));
        c.Rect(16, 182, 88, 6, C("#F2EEE4"));
        c.Line(42, 118, 26, 160, 7, Porcelain); c.Line(78, 118, 94, 160, 7, Porcelain); // arms
        c.Rect(46, 188, 10, 22, C("#E8E4DC")); c.Rect(64, 188, 10, 22, C("#E8E4DC")); // legs
        c.Ellipse(50, 212, 8, 5, C("#141010")); c.Ellipse(70, 212, 8, 5, C("#141010"));
        return c;
    }

    static NightShiftCanvas Ballerina(bool restless)
    {
        // Odette: a music-box ballerina. Calm: arms up, eyes closed. Restless: arms down, red eyes, turned to you.
        var c = new NightShiftCanvas(120, 224, 3);
        var skin = C("#EFE6DA");
        c.Ellipse(60, 22, 11, 9, C("#2A1A14")); // bun
        c.Ellipse(60, 44, 19, 22, skin);
        if (restless)
        {
            c.Ellipse(52, 44, 4.5, 5, Black); c.Ellipse(68, 44, 4.5, 5, Black);
            c.Ellipse(52, 44, 2, 2, C("#FF2A2A")); c.Ellipse(68, 44, 2, 2, C("#FF2A2A"));
            c.Line(53, 58, 67, 57, 1.6, C("#5A1A1A"));
            c.Line(46, 78, 30, 130, 5, skin); c.Line(74, 78, 90, 130, 5, skin); // arms hanging, long fingers
            c.Path(1.2, skin, 30, 130, 27, 142); c.Path(1.2, skin, 30, 130, 31, 144); c.Path(1.2, skin, 90, 130, 93, 142); c.Path(1.2, skin, 90, 130, 89, 144);
        }
        else
        {
            c.Path(1.6, C("#3A2A2A"), 48, 45, 52, 47, 56, 45); c.Path(1.6, C("#3A2A2A"), 64, 45, 68, 47, 72, 45);
            c.Ellipse(60, 56, 3, 1.6, C("#B04A5A"));
            c.Path(5, skin, 46, 78, 30, 50, 38, 14, 56, 4); c.Path(5, skin, 74, 78, 90, 50, 82, 14, 64, 4); // arms in a ring
        }
        c.Rect(55, 64, 10, 10, skin);
        c.Poly(C("#E8D8E0"), 44, 74, 76, 74, 70, 112, 50, 112); // bodice
        c.Ellipse(60, 116, 48, 13, C("#F4ECF2"));               // tutu
        c.Ellipse(60, 112, 40, 9, C("#FFFFFF", 0.85));
        for (int k = 0; k < 9; k++) c.Ellipse(18 + k * 10.5, 124, 5, 3, C("#E0D0DC"));
        c.Line(54, 124, 50, 196, 4, skin); c.Line(66, 124, 70, 196, 4, skin); // legs
        c.Poly(C("#E8A0B0"), 47, 192, 53, 192, 52, 208, 48, 208); c.Poly(C("#E8A0B0"), 67, 192, 73, 192, 72, 208, 68, 208);
        c.Rect(57, 206, 6, 10, C("#C9A04A")); c.Ellipse(60, 218, 16, 4, C("#C9A04A")); // the spindle
        return c;
    }

    static NightShiftCanvas Soldier()
    {
        // Corporal Tock: a wind-up tin soldier, painted cheeks, a brass key in his back.
        var c = new NightShiftCanvas(108, 240, 3);
        c.Line(88, 120, 104, 112, 6, C("#C9A04A")); c.Ellipse(104, 104, 6, 10, C("#C9A04A")); c.Ellipse(104, 120, 6, 10, C("#C9A04A")); // the key
        c.Rect(30, 2, 48, 52, C("#121212")); c.Rect(28, 50, 52, 6, C("#2A2A2A")); // shako
        c.Rect(46, 14, 16, 16, C("#C9A04A")); c.Poly(C("#B01A1A"), 50, 2, 58, 2, 60, -10, 48, -10);
        c.Ellipse(54, -2, 7, 9, C("#C81E1E"));
        c.Ellipse(54, 72, 22, 20, C("#E8CCA8"));
        c.Ellipse(44, 68, 5, 6, Black); c.Ellipse(64, 68, 5, 6, Black);
        c.Ellipse(45, 66, 1.4, 1.4, C("#FFFFFF")); c.Ellipse(65, 66, 1.4, 1.4, C("#FFFFFF"));
        c.Ellipse(38, 80, 6, 5, C("#E04040", 0.85)); c.Ellipse(70, 80, 6, 5, C("#E04040", 0.85));
        c.Poly(C("#2A1408"), 42, 84, 54, 80, 66, 84, 62, 88, 54, 85, 46, 88); // moustache
        c.Rect(46, 90, 16, 8, C("#E8CCA8"));
        c.Rect(26, 96, 56, 70, C("#A81818")); // coat
        c.Line(28, 98, 80, 160, 6, C("#F0EEE8")); c.Line(80, 98, 28, 160, 6, C("#F0EEE8")); // cross belts
        for (int k = 0; k < 4; k++) c.Ellipse(54, 104 + k * 16, 3, 3, C("#E8C860"));
        c.Rect(18, 98, 10, 56, C("#A81818")); c.Rect(80, 98, 10, 56, C("#A81818")); // stiff arms
        c.Ellipse(23, 158, 6, 6, C("#F0EEE8")); c.Ellipse(85, 158, 6, 6, C("#F0EEE8"));
        c.Rect(26, 160, 56, 8, C("#141414"));
        c.Rect(30, 168, 20, 54, C("#1A2A6A")); c.Rect(58, 168, 20, 54, C("#1A2A6A"));
        c.Rect(28, 216, 24, 20, C("#0E0E0E")); c.Rect(56, 216, 24, 20, C("#0E0E0E"));
        return c;
    }

    static NightShiftCanvas Twin()
    {
        // Pip: the smallest doll — a bonnet, button eyes, a stitched smile, a patched dress.
        var c = new NightShiftCanvas(96, 180, 3);
        c.Ellipse(48, 46, 38, 38, C("#C8B8D8")); // bonnet
        c.Ellipse(48, 52, 26, 28, Porcelain);
        c.Ellipse(38, 50, 7, 7, C("#1A1A1A")); c.Ellipse(58, 50, 7, 7, C("#1A1A1A"));
        c.Line(35, 47, 41, 53, 1.4, C("#E8E0D0")); c.Line(41, 47, 35, 53, 1.4, C("#E8E0D0"));
        c.Line(55, 47, 61, 53, 1.4, C("#E8E0D0")); c.Line(61, 47, 55, 53, 1.4, C("#E8E0D0"));
        c.Path(1.4, C("#6A1A20"), 36, 66, 42, 70, 48, 71, 54, 70, 60, 66);
        for (int k = 0; k < 5; k++) c.Line(38 + k * 5.5, 64, 38 + k * 5.5, 72, 1, C("#6A1A20"));
        c.Line(26, 74, 70, 74, 3, C("#8A70A0")); // bonnet ribbon
        c.Poly(C("#5A6A4A"), 32, 80, 64, 80, 84, 150, 12, 150); // dress
        c.Rect(26, 104, 16, 14, C("#8A4A3A")); c.Rect(52, 124, 18, 14, C("#3A4A7A")); // patches
        c.Path(1, C("#E8E0D0"), 26, 104, 42, 104, 42, 118, 26, 118, 26, 104);
        c.Line(34, 86, 18, 124, 6, Porcelain); c.Line(62, 86, 78, 124, 6, Porcelain);
        c.Rect(36, 150, 8, 20, C("#E8E4DC")); c.Rect(52, 150, 8, 20, C("#E8E4DC"));
        c.Ellipse(40, 172, 7, 4, C("#2A1A1A")); c.Ellipse(56, 172, 7, 4, C("#2A1A1A"));
        return c;
    }

    // ---------------- Jumpscares ----------------

    enum ScareKind { Doll, Ballerina, Soldier, Twin }

    static NightShiftCanvas Scare(ScareKind kind)
    {
        var c = new NightShiftCanvas(800, 450, 2);
        c.Fill(C("#0A0000"));
        c.Glow(400, 225, 520, C("#5A0808", 0.8));
        var rng = new Random((int)kind + 7);
        for (int i = 0; i < 40; i++) // speed streaks
        {
            double a = rng.NextDouble() * Math.PI * 2, r0 = 240 + rng.NextDouble() * 60;
            c.Line(400 + Math.Cos(a) * r0, 225 + Math.Sin(a) * r0, 400 + Math.Cos(a) * 700, 225 + Math.Sin(a) * 700, 2 + rng.NextDouble() * 4, C("#000000", 0.5));
        }
        double cx = 400, cy = 250;
        var face = kind == ScareKind.Soldier ? C("#E8CCA8") : kind == ScareKind.Ballerina ? C("#EFE6DA") : Porcelain;
        switch (kind)
        {
            case ScareKind.Doll:
                c.Ellipse(cx, cy - 20, 300, 290, C("#2A160E"));
                for (int i = 0; i < 5; i++) { c.Ellipse(110 + i * 8, 230 + i * 50, 46, 40, C("#2A160E")); c.Ellipse(690 - i * 8, 230 + i * 50, 46, 40, C("#2A160E")); }
                break;
            case ScareKind.Ballerina:
                c.Ellipse(cx, 20, 90, 70, C("#1E1210"));
                break;
            case ScareKind.Soldier:
                c.Rect(cx - 190, -40, 380, 140, C("#0E0E0E")); c.Rect(cx - 40, 0, 80, 70, C("#C9A04A"));
                break;
            case ScareKind.Twin:
                c.Ellipse(cx, cy - 10, 320, 300, C("#B8A8C8"));
                break;
        }
        c.Ellipse(cx, cy, 220, 240, face);
        c.Ellipse(cx, cy + 30, 200, 190, C("#000000", 0.12));
        // Eyes: deep and glowing.
        double ex = 96, ey = cy - 50;
        foreach (var sx in new[] { -1, 1 })
        {
            c.Ellipse(cx + sx * ex, ey, 52, 62, Black);
            var glow = kind == ScareKind.Soldier ? C("#FFE070") : kind == ScareKind.Twin ? C("#E8E8E8") : C("#FF2A2A");
            c.Glow(cx + sx * ex, ey, 46, glow.WithAlpha(0.8));
            c.Ellipse(cx + sx * ex, ey, 9, 11, C("#FFFFFF"));
        }
        if (kind == ScareKind.Soldier) { c.Ellipse(cx - 160, cy + 60, 40, 30, C("#E04040", 0.8)); c.Ellipse(cx + 160, cy + 60, 40, 30, C("#E04040", 0.8)); }
        if (kind == ScareKind.Doll) c.Path(6, C("#3A2A20"), cx + 60, cy - 240, cx + 30, cy - 150, cx + 80, cy - 90, cx + 20, cy + 10, cx + 60, cy + 90);
        // A mouth wide open, with teeth.
        c.Poly(Black, cx - 110, cy + 90, cx + 110, cy + 90, cx + 80, cy + 200, cx - 80, cy + 200);
        c.Poly(C("#3A0606"), cx - 70, cy + 140, cx + 70, cy + 140, cx + 60, cy + 196, cx - 60, cy + 196);
        for (int k = 0; k < 9; k++)
        {
            double tx = cx - 100 + k * 25;
            c.Poly(C("#E8E0C8"), tx, cy + 90, tx + 20, cy + 90, tx + 10, cy + 122);
            c.Poly(C("#D8D0B8"), tx + 6, cy + 202, tx + 24, cy + 202, tx + 15, cy + 172);
        }
        c.Grain(0.12, (int)kind + 40);
        c.Vignette(0.8, C("#000000"));
        return c;
    }

    static NightShiftCanvas Title()
    {
        var c = new NightShiftCanvas(800, 450, 2);
        c.VGradient(0, 0, 800, 450, C("#140C0A"), C("#050303"));
        c.Glow(600, 200, 300, C("#4A1A14", 0.55));
        // A tall doll in the dark on the right, eyes catching the light.
        c.Ellipse(600, 150, 110, 110, C("#0C0605"));
        for (int i = 0; i < 4; i++) { c.Ellipse(500 - i * 3, 190 + i * 46, 26, 30, C("#0C0605")); c.Ellipse(700 + i * 3, 190 + i * 46, 26, 30, C("#0C0605")); }
        c.Ellipse(600, 168, 80, 90, C("#2A2420"));
        c.Ellipse(570, 160, 18, 22, Black); c.Ellipse(630, 160, 18, 22, Black);
        c.Glow(570, 160, 16, C("#FF3030", 0.9)); c.Glow(630, 160, 16, C("#FF3030", 0.9));
        c.Path(3, C("#0A0606"), 620, 90, 610, 130, 628, 156, 612, 196);
        c.Poly(C("#100808"), 520, 260, 680, 260, 760, 450, 440, 450);
        c.Poly(C("#1E1412"), 560, 250, 640, 250, 650, 276, 550, 276);
        for (int i = 0; i < 60; i++) { var r = new Random(i); c.Ellipse(r.Next(0, 800), r.Next(0, 450), 1, 1, C("#C8B8A0", 0.15 + r.NextDouble() * 0.2)); }
        c.Grain(0.06, 5);
        c.Vignette(0.7, Black);
        return c;
    }
}
