// Oracle (d): images of the taskbars, taken with PrintWindow(PW_RENDERFULLCONTENT), which asks
// DWM for the window's content and draws nothing on screen; and copies of screen regions (for
// an open flyout). Plus the pixel analysis used on them.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TsoShared;

namespace TsoLive;

public sealed class ColorStats
{
    public int Count;
    public double MeanX, MeanY;
    public int MinX = int.MaxValue, MaxX = int.MinValue, MinY = int.MaxValue, MaxY = int.MinValue;
}

public sealed class ProgressRead
{
    public string Hue = "none";   // green, red, yellow, none
    public double Fill;           // fraction of the scanned width
    public int Pixels;

    public override string ToString() => Hue == "none" ? "none" : $"{Hue} {Fill:P0}";
}

public static class Capture
{
    public static Bitmap Window(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out RECT r) || r.Width <= 0 || r.Height <= 0)
        {
            throw new InvalidOperationException($"no rect for 0x{hwnd.ToInt64():X}");
        }
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            bool ok = Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
            if (!ok)
            {
                bmp.Dispose();
                throw new InvalidOperationException($"PrintWindow failed for 0x{hwnd.ToInt64():X}");
            }
        }
        return bmp;
    }

    public static Bitmap Screen(RECT r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
        return bmp;
    }

    /// <summary>Saves an image of every taskbar; returns (tray, rect, path).</summary>
    public static List<(IntPtr tray, RECT rect, string path)> SaveTrays(string dir, string tag)
    {
        Directory.CreateDirectory(dir);
        var list = new List<(IntPtr, RECT, string)>();
        foreach (var (tray, cls) in Uia.Trays())
        {
            Win32.GetWindowRect(tray, out RECT r);
            using var bmp = Window(tray);
            string path = Path.Combine(dir, $"{tag}.{cls}-0x{tray.ToInt64():X}.png");
            bmp.Save(path, ImageFormat.Png);
            list.Add((tray, r, path));
        }
        return list;
    }

    static int[] Pixels(Bitmap bmp, out int width, out int height)
    {
        width = bmp.Width;
        height = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, px, y * width, width);
            }
            return px;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    static bool Near(int argb, int rgb, int tol)
    {
        int r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        int R = (rgb >> 16) & 0xFF, G = (rgb >> 8) & 0xFF, B = rgb & 0xFF;
        return Math.Abs(r - R) + Math.Abs(g - G) + Math.Abs(b - B) <= tol;
    }

    public static Rectangle Clip(Rectangle region, Bitmap bmp) => Rectangle.Intersect(region, new Rectangle(0, 0, bmp.Width, bmp.Height));

    public static ColorStats Stats(Bitmap bmp, Rectangle region, int rgb, int tol = 60)
    {
        var s = new ColorStats();
        var px = Pixels(bmp, out int w, out _);
        region = Clip(region, bmp);
        double sx = 0, sy = 0;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                if (Near(px[y * w + x], rgb, tol))
                {
                    s.Count++;
                    sx += x;
                    sy += y;
                    s.MinX = Math.Min(s.MinX, x);
                    s.MinY = Math.Min(s.MinY, y);
                    s.MaxY = Math.Max(s.MaxY, y);
                    s.MaxX = Math.Max(s.MaxX, x);
                }
            }
        }
        if (s.Count > 0)
        {
            s.MeanX = sx / s.Count;
            s.MeanY = sy / s.Count;
        }
        return s;
    }

    /// <summary>Left-to-right order of the keys whose colour covers at least minPixels of the region.</summary>
    public static List<string> OrderByColors(Bitmap bmp, Rectangle region, IDictionary<string, int> keyRgb, int tol, int minPixels)
    {
        return keyRgb.Select(kv => (kv.Key, s: Stats(bmp, region, kv.Value, tol)))
                     .Where(t => t.s.Count >= minPixels)
                     .OrderBy(t => t.s.MeanX)
                     .Select(t => t.Key).ToList();
    }

    /// <summary>Hue class of a pixel, for progress bars: green, red, yellow or null.</summary>
    public static string HueClass(int argb)
    {
        double r = ((argb >> 16) & 0xFF) / 255.0, g = ((argb >> 8) & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (max < 0.25 || delta < 0.30 || delta / max < 0.45)
        {
            return null;
        }
        double h = max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (h < 0) h += 360;
        if (h >= 75 && h <= 165) return "green";
        if (h <= 18 || h >= 340) return "red";
        if (h >= 35 && h < 70) return "yellow";
        return null;
    }

    /// <summary>The progress bar in the bottom rows of a button: its colour and how much of the width it fills.</summary>
    public static ProgressRead Progress(Bitmap bmp, Rectangle button, int rows = 8)
    {
        var px = Pixels(bmp, out int w, out _);
        var region = Clip(new Rectangle(button.Left, button.Bottom - rows, button.Width, rows), bmp);
        var counts = new Dictionary<string, int>();
        var columns = new Dictionary<string, HashSet<int>>();
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                string c = HueClass(px[y * w + x]);
                if (c == null)
                {
                    continue;
                }
                counts[c] = counts.GetValueOrDefault(c) + 1;
                if (!columns.TryGetValue(c, out var set))
                {
                    columns[c] = set = new HashSet<int>();
                }
                set.Add(x);
            }
        }
        var read = new ProgressRead();
        if (counts.Count == 0)
        {
            return read;
        }
        var top = counts.OrderByDescending(kv => kv.Value).First();
        if (top.Value < 6)
        {
            return read;
        }
        read.Hue = top.Key;
        read.Pixels = top.Value;
        read.Fill = columns[top.Key].Count / (double)Math.Max(1, region.Width);
        return read;
    }

    /// <summary>
    /// In an image of a thumbnail flyout: per window (by its body colour), the toolbar-colour pixels
    /// below its thumbnail, and the box around all thumbnails with their titles and toolbars.
    /// </summary>
    public static (Dictionary<string, int> perKey, Rectangle crop) ToolbarsBelowThumbnails(Bitmap img, IDictionary<string, int> colours, int toolbarRgb)
    {
        var whole = new Rectangle(0, 0, img.Width, img.Height);
        var perKey = new Dictionary<string, int>();
        var boxes = new List<Rectangle>();
        foreach (var kv in colours)
        {
            var s = Stats(img, whole, kv.Value, 40);
            if (s.Count < 150)
            {
                continue;
            }
            var box = Rectangle.FromLTRB(s.MinX, s.MinY, s.MaxX + 1, s.MaxY + 1);
            boxes.Add(box);
            perKey[kv.Key] = Stats(img, Rectangle.FromLTRB(box.Left, box.Bottom, box.Right, img.Height), toolbarRgb, 60).Count;
        }
        var crop = boxes.Count == 0 ? whole : Rectangle.Intersect(whole, Rectangle.FromLTRB(
            boxes.Min(b => b.Left) - 30, boxes.Min(b => b.Top) - 50, boxes.Max(b => b.Right) + 30, boxes.Max(b => b.Bottom) + 70));
        return (perKey, crop);
    }

    /// <summary>Whether an image is all one colour (a failed capture).</summary>
    public static bool IsBlank(Bitmap bmp)
    {
        var px = Pixels(bmp, out _, out _);
        int first = px.Length > 0 ? px[0] : 0;
        return px.All(p => p == first);
    }
}
