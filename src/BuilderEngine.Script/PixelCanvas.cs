namespace BuilderEngine.Script;

/// <summary>
/// The pixel-art editor's picture (headless, testable): pixels as 0xAARRGGBB (0 = transparent), the tools
/// (pencil, eraser, line, fill, colour picker) and undo/redo. A stroke (press, drag, release) is one undo step:
/// call <see cref="BeginStroke"/> before it. Saved as PNG with <see cref="ToPng"/>.
/// </summary>
public sealed class PixelCanvas
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] Pixels { get; private set; }

    public const int MaxSize = 256;

    readonly List<(int W, int H, uint[] Px)> _undo = [];
    readonly List<(int W, int H, uint[] Px)> _redo = [];

    public PixelCanvas(int width, int height)
    {
        Width = Math.Clamp(width, 1, MaxSize);
        Height = Math.Clamp(height, 1, MaxSize);
        Pixels = new uint[Width * Height];
    }

    /// <summary>A canvas holding these pixels (row by row); bigger pictures are cut to <see cref="MaxSize"/>.</summary>
    public static PixelCanvas From(int width, int height, uint[] argb)
    {
        var c = new PixelCanvas(width, height);
        for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++)
                c.Pixels[y * c.Width + x] = argb[y * width + x];
        return c;
    }

    public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public uint Get(int x, int y) => Inside(x, y) ? Pixels[y * Width + x] : 0;

    public void Set(int x, int y, uint argb)
    {
        if (Inside(x, y)) Pixels[y * Width + x] = argb;
    }

    // ---------------- Undo ----------------

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Remembers the picture before a change (a stroke, a fill, a resize): one undo step.</summary>
    public void BeginStroke()
    {
        _undo.Add((Width, Height, (uint[])Pixels.Clone()));
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public bool Undo() => Swap(_undo, _redo);
    public bool Redo() => Swap(_redo, _undo);

    bool Swap(List<(int W, int H, uint[] Px)> from, List<(int W, int H, uint[] Px)> to)
    {
        if (from.Count == 0) return false;
        to.Add((Width, Height, Pixels));
        var (w, h, px) = from[^1];
        from.RemoveAt(from.Count - 1);
        Width = w; Height = h; Pixels = px;
        return true;
    }

    // ---------------- Tools ----------------

    /// <summary>A square brush of <paramref name="size"/> pixels centred on x, y.</summary>
    public void Pencil(int x, int y, uint argb, int size = 1)
    {
        size = Math.Clamp(size, 1, 16);
        int x0 = x - (size - 1) / 2, y0 = y - (size - 1) / 2;
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++) Set(x0 + i, y0 + j, argb);
    }

    public void Erase(int x, int y, int size = 1) => Pencil(x, y, 0, size);

    /// <summary>A straight line of brush dabs (also fills the gaps of a fast drag).</summary>
    public void Line(int x0, int y0, int x1, int y1, uint argb, int size = 1)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        for (int guard = 0; guard < 4 * MaxSize; guard++)
        {
            Pencil(x0, y0, argb, size);
            if (x0 == x1 && y0 == y1) return;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>Paints the area of same-coloured pixels touching x, y (not diagonally). Returns how many pixels changed.</summary>
    public int Fill(int x, int y, uint argb)
    {
        if (!Inside(x, y)) return 0;
        uint target = Get(x, y);
        if (target == argb) return 0;
        int n = 0;
        var stack = new Stack<(int, int)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            if (!Inside(cx, cy) || Pixels[cy * Width + cx] != target) continue;
            Pixels[cy * Width + cx] = argb;
            n++;
            stack.Push((cx + 1, cy)); stack.Push((cx - 1, cy));
            stack.Push((cx, cy + 1)); stack.Push((cx, cy - 1));
        }
        return n;
    }

    /// <summary>The colour at x, y (the colour picker).</summary>
    public uint Pick(int x, int y) => Get(x, y);

    /// <summary>Everything transparent.</summary>
    public void Clear() => Array.Clear(Pixels);

    /// <summary>A new size; the picture stays at the top-left (cut or padded with transparency).</summary>
    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, MaxSize);
        height = Math.Clamp(height, 1, MaxSize);
        var px = new uint[width * height];
        for (int y = 0; y < Math.Min(height, Height); y++)
            for (int x = 0; x < Math.Min(width, Width); x++)
                px[y * width + x] = Pixels[y * Width + x];
        Width = width; Height = height; Pixels = px;
    }

    /// <summary>Mirrors the picture left to right.</summary>
    public void FlipHorizontal()
    {
        for (int y = 0; y < Height; y++)
            Array.Reverse(Pixels, y * Width, Width);
    }

    /// <summary>The colours used in the picture (most used first, transparent left out), at most <paramref name="max"/>.</summary>
    public List<uint> UsedColors(int max = 16) =>
        Pixels.Where(p => (p >> 24) != 0).GroupBy(p => p).OrderByDescending(g => g.Count()).Take(max).Select(g => g.Key).ToList();

    public byte[] ToPng() => PixelArt.Png(Width, Height, Pixels);

    /// <summary>
    /// The starting palette: 16 friendly colours (PICO-8's) — new colours come from "lighter" / "darker" and the picker.
    /// </summary>
    public static readonly uint[] DefaultPalette =
    [
        0xFF000000, 0xFF1D2B53, 0xFF7E2553, 0xFF008751, 0xFFAB5236, 0xFF5F574F, 0xFFC2C3C7, 0xFFFFF1E8,
        0xFFFF004D, 0xFFFFA300, 0xFFFFEC27, 0xFF00E436, 0xFF29ADFF, 0xFF83769C, 0xFFFF77A8, 0xFFFFCCAA,
    ];

    /// <summary>"#RRGGBB" (or "#AARRGGBB" when not opaque) for a colour.</summary>
    public static string Hex(uint argb) => (argb >> 24) == 0xFF ? $"#{argb & 0xFFFFFF:X6}" : $"#{argb:X8}";
}

/// <summary>
/// A sprite sheet: one picture holding frames in a grid. Slicing cuts it into <c>columns × rows</c> frames, read row
/// by row (left to right, top to bottom); pixels left over at the right/bottom edge are ignored.
/// </summary>
public static class SpriteSheet
{
    /// <summary>The frame rectangles (x, y, width, height) of a sheet, row by row. Empty when a frame would be smaller than 1 pixel.</summary>
    public static List<(int X, int Y, int W, int H)> Slice(int sheetWidth, int sheetHeight, int columns, int rows)
    {
        var list = new List<(int, int, int, int)>();
        if (columns < 1 || rows < 1) return list;
        int fw = sheetWidth / columns, fh = sheetHeight / rows;
        if (fw < 1 || fh < 1) return list;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                list.Add((c * fw, r * fh, fw, fh));
        return list;
    }

    /// <summary>A good first guess of the grid: square frames in a strip (a 128×32 sheet → 4 × 1), else 1 × 1.</summary>
    public static (int Columns, int Rows) Guess(int sheetWidth, int sheetHeight)
    {
        if (sheetWidth <= 0 || sheetHeight <= 0) return (1, 1);
        if (sheetWidth >= sheetHeight && sheetWidth % sheetHeight == 0) return (sheetWidth / sheetHeight, 1);
        if (sheetHeight > sheetWidth && sheetHeight % sheetWidth == 0) return (1, sheetHeight / sheetWidth);
        return (1, 1);
    }

    /// <summary>The pixels of one frame of a sheet.</summary>
    public static uint[] Crop(int sheetWidth, uint[] argb, (int X, int Y, int W, int H) r)
    {
        var px = new uint[r.W * r.H];
        for (int y = 0; y < r.H; y++)
            Array.Copy(argb, (r.Y + y) * sheetWidth + r.X, px, y * r.W, r.W);
        return px;
    }

    /// <summary>True when a frame has no visible pixel (empty cells at the end of a sheet are skipped).</summary>
    public static bool IsEmpty(uint[] px) => px.All(p => (p >> 24) == 0);

    /// <summary>
    /// Cuts a sheet into frames and saves each one in the project's images folder as "name_1.png", "name_2.png"…
    /// (a free name is used when one is taken by a different picture). Fully transparent frames are skipped.
    /// Returns the saved pictures' names in order.
    /// </summary>
    public static List<string> SaveFrames(string projectDir, string baseName, int sheetWidth, int sheetHeight, uint[] argb, int columns, int rows)
    {
        var names = new List<string>();
        string stem = Path.GetFileNameWithoutExtension(baseName.Trim()) is { Length: > 0 } s ? s : "frame";
        string dir = Images.Folder(projectDir);
        Directory.CreateDirectory(dir);
        int k = 1;
        foreach (var r in Slice(sheetWidth, sheetHeight, columns, rows))
        {
            var px = Crop(sheetWidth, argb, r);
            if (IsEmpty(px)) continue;
            var png = PixelArt.Png(r.W, r.H, px);
            string name;
            for (; ; k++)
            {
                name = $"{stem}_{k}.png";
                string path = Path.Combine(dir, name);
                if (!File.Exists(path) || File.ReadAllBytes(path).AsSpan().SequenceEqual(png)) break;
            }
            File.WriteAllBytes(Path.Combine(dir, name), png);
            names.Add(name);
            k++;
        }
        return names;
    }

    /// <summary>A picture name not used yet in the images folder: "sprite.png", "sprite 2.png"...</summary>
    public static string FreePictureName(string? projectDir, string wanted)
    {
        string stem = Path.GetFileNameWithoutExtension((wanted ?? "").Trim()) is { Length: > 0 } s ? s : "sprite";
        foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(projectDir)) return stem + ".png";
        for (int k = 1; ; k++)
        {
            string name = k == 1 ? stem + ".png" : $"{stem} {k}.png";
            if (Images.Resolve(projectDir, name) == null) return name;
        }
    }
}
