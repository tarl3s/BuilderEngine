using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace BuilderEngine;

/// <summary>
/// Pictures as plain pixels (0xAARRGGBB, not premultiplied) for the sprite editor and the sprite-sheet slicer:
/// any picture Avalonia reads (png, jpg, bmp) in, a bitmap to draw out.
/// </summary>
static class PictureDecode
{
    /// <summary>The pixels of a picture file, or null when it can't be read.</summary>
    public static (int W, int H, uint[] Px)? Load(string path)
    {
        try
        {
            using var bmp = new Bitmap(path);
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
            if (w < 1 || h < 1) return null;
            using var wb = new WriteableBitmap(bmp.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            var px = new uint[w * h];
            using (var fb = wb.Lock())
            {
                bmp.CopyPixels(fb, AlphaFormat.Unpremul);
                var row = new int[w];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, w);
                    Buffer.BlockCopy(row, 0, px, y * w * 4, w * 4); // BGRA bytes = 0xAARRGGBB little-endian
                }
            }
            return (w, h, px);
        }
        catch (Exception) { return null; }
    }

    /// <summary>A bitmap showing these pixels (made once; update it with <see cref="Write"/>).</summary>
    public static WriteableBitmap ToBitmap(int w, int h, uint[] px)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        Write(wb, w, h, px);
        return wb;
    }

    public static void Write(WriteableBitmap wb, int w, int h, uint[] px)
    {
        using var fb = wb.Lock();
        var row = new int[w];
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(px, y * w * 4, row, 0, w * 4);
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, w);
        }
    }
}
