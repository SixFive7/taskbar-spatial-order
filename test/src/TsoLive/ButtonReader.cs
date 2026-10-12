// Reads a test window's own taskbar button (grouping off) from an image of the taskbar, without
// UI Automation. Calibrated on images of the taskbar of build 26300:
//  - the icon is the window's 16x16 icon: a square of the window's colour with a darker centre;
//  - an overlay badge is drawn as a small dot (about 4x4, the accent's light tint, the same for
//    every window) centred on the icon's top-right corner, whatever the overlay icon looks like;
//  - progress is a track exactly as wide as the icon, about 10 px below it: grey track, filled in
//    the accent tint for normal, pink for error, yellow for paused; a button without progress shows
//    only the small grey running dot.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace TsoLive;

public sealed class ButtonReading
{
    public string Key;
    public bool Found;
    public Rectangle Icon;
    public bool Badge;
    public int BadgePixels;
    public string Progress = "none";   // normal, error, paused, none
    public double Fill;
    public int FillColumns, TrackColumns;

    public string Short => !Found ? "not found" : $"badge={(Badge ? "yes" : "no")},progress={Progress}{(Progress != "none" ? $" {Fill * 100:0}%" : "")}";

    public override string ToString() => $"{Key}:{Short}{(Found ? $"@({Icon.X},{Icon.Y})" : "")}";
}

public static class ButtonReader
{
    sealed class Pixels
    {
        public int W, H;
        public int[] P;

        public int this[int x, int y] => P[y * W + x];
    }

    static Pixels Load(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var p = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, p, y * bmp.Width, bmp.Width);
            }
            return new Pixels { W = bmp.Width, H = bmp.Height, P = p };
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    static (int r, int g, int b) Rgb(int argb) => ((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);

    static int Dist(int argb, int rgb)
    {
        var (r, g, b) = Rgb(argb);
        return Math.Abs(r - ((rgb >> 16) & 0xFF)) + Math.Abs(g - ((rgb >> 8) & 0xFF)) + Math.Abs(b - (rgb & 0xFF));
    }

    /// <summary>The icon of the given colour: a connected square of about 16 px with the darker centre
    /// the test app draws. Null if there is none (or it is mid-animation).</summary>
    static Rectangle? FindIcon(Pixels px, int rgb)
    {
        int inner = (rgb >> 1) & 0x7F7F7F;
        var seen = new bool[px.W * px.H];
        Rectangle? best = null;
        for (int y = 0; y < px.H; y++)
        {
            for (int x = 0; x < px.W; x++)
            {
                int i = y * px.W + x;
                if (seen[i] || Dist(px.P[i], rgb) > 30)
                {
                    continue;
                }
                // Flood fill this component.
                int minX = x, maxX = x, minY = y, maxY = y, count = 0;
                var stack = new Stack<int>();
                stack.Push(i);
                seen[i] = true;
                while (stack.Count > 0)
                {
                    int j = stack.Pop();
                    int cx = j % px.W, cy = j / px.W;
                    count++;
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                    foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                    {
                        if (nx < 0 || ny < 0 || nx >= px.W || ny >= px.H)
                        {
                            continue;
                        }
                        int k = ny * px.W + nx;
                        if (!seen[k] && Dist(px.P[k], rgb) <= 30)
                        {
                            seen[k] = true;
                            stack.Push(k);
                        }
                    }
                }
                int w = maxX - minX + 1, h = maxY - minY + 1;
                if (w < 12 || h < 12 || w > 28 || h > 28 || Math.Abs(w - h) > 3 || count < w * h * 0.5)
                {
                    continue;
                }
                if (Dist(px[minX + w / 2, minY + h / 2], inner) > 60)
                {
                    continue;
                }
                best = new Rectangle(minX, minY, w, h);
            }
        }
        return best;
    }

    static bool Colourful(int argb, out double hue)
    {
        var (r, g, b) = Rgb(argb);
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        hue = 0;
        if (max < 150 || max - min < 50)
        {
            return false;
        }
        double d = max - min;
        hue = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (hue < 0)
        {
            hue += 360;
        }
        return true;
    }

    static bool LightGrey(int argb)
    {
        var (r, g, b) = Rgb(argb);
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max >= 100 && max - min <= 20;
    }

    public static ButtonReading Read(Bitmap bmp, string key, int iconRgb) => Read(Load(bmp), key, iconRgb);

    static ButtonReading Read(Pixels px, string key, int iconRgb)
    {
        var r = new ButtonReading { Key = key };
        var icon = FindIcon(px, iconRgb);
        if (icon == null)
        {
            return r;
        }
        r.Found = true;
        r.Icon = icon.Value;
        int left = r.Icon.Left, right = r.Icon.Right - 1, top = r.Icon.Top, bottom = r.Icon.Bottom - 1;

        // Badge: bright, coloured pixels that are not the icon's, around the top-right corner.
        for (int y = Math.Max(0, top - 4); y <= Math.Min(px.H - 1, top + 1); y++)
        {
            for (int x = Math.Max(0, right - 2); x <= Math.Min(px.W - 1, right + 3); x++)
            {
                int p = px[x, y];
                var (cr, cg, cb) = Rgb(p);
                int max = Math.Max(cr, Math.Max(cg, cb)), min = Math.Min(cr, Math.Min(cg, cb));
                if (max >= 140 && max - min >= 40 && Dist(p, iconRgb) > 60)
                {
                    r.BadgePixels++;
                }
            }
        }
        r.Badge = r.BadgePixels >= 4;

        // Progress: the track row is the row below the icon with the most track or fill pixels (its
        // height above the bottom edge is the same for 16 and 24 px icons); per column, a filled
        // (coloured) or a track (grey) pixel within a row of it.
        int x0 = Math.Max(0, left - 2), x1 = Math.Min(px.W - 1, right + 3);
        int trackRow = -1, bestCount = 2;
        for (int y = bottom + 2; y < px.H; y++)
        {
            int n = 0;
            for (int x = x0; x <= x1; x++)
            {
                if (Colourful(px[x, y], out _) || LightGrey(px[x, y]))
                {
                    n++;
                }
            }
            if (n > bestCount)
            {
                bestCount = n;
                trackRow = y;
            }
        }
        var classes = new List<string>();
        for (int x = x0; trackRow >= 0 && x <= x1; x++)
        {
            string fill = null;
            bool track = false;
            for (int y = Math.Max(bottom + 2, trackRow - 1); y <= Math.Min(px.H - 1, trackRow + 1); y++)
            {
                int p = px[x, y];
                if (Colourful(p, out double hue))
                {
                    fill = hue >= 330 || hue <= 15 ? "error" : hue >= 45 && hue <= 70 && Saturation(p) >= 0.6 ? "paused" : "normal";
                    break;
                }
                if (LightGrey(p))
                {
                    track = true;
                }
            }
            if (fill != null)
            {
                r.FillColumns++;
                classes.Add(fill);
            }
            else if (track)
            {
                r.TrackColumns++;
            }
        }
        if (r.FillColumns > 0)
        {
            r.Progress = classes.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
            r.Fill = r.FillColumns / (double)(r.FillColumns + r.TrackColumns);
        }
        return r;
    }

    static double Saturation(int argb)
    {
        var (r, g, b) = Rgb(argb);
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max == 0 ? 0 : (max - min) / (double)max;
    }

    public static Dictionary<string, ButtonReading> ReadAll(Bitmap bmp, IDictionary<string, int> keyRgb)
    {
        var px = Load(bmp);
        return keyRgb.ToDictionary(kv => kv.Key, kv => Read(px, kv.Key, kv.Value));
    }

    /// <summary>Whether a reading matches the state the test app set: badge present, progress state and
    /// fill within 0.12 of the value. Returns a reason when it does not.</summary>
    public static string Mismatch(ButtonReading r, bool badge, string state, int value)
    {
        if (!r.Found)
        {
            return "button not found";
        }
        var problems = new List<string>();
        if (r.Badge != badge)
        {
            problems.Add($"badge {(r.Badge ? "present" : "missing")}");
        }
        if (r.Progress != state)
        {
            problems.Add($"progress {r.Progress} (want {state})");
        }
        else if (state != "none" && Math.Abs(r.Fill - value / 100.0) > 0.12)
        {
            problems.Add($"fill {r.Fill * 100:0}% (want {value}%)");
        }
        return problems.Count == 0 ? null : string.Join(", ", problems);
    }

    // ------------------------------------------------------------------ settling and crops

    /// <summary>Whether two images are identical inside a region.</summary>
    public static bool SameRegion(Bitmap a, Bitmap b, Rectangle region)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }
        region = Rectangle.Intersect(region, new Rectangle(0, 0, a.Width, a.Height));
        var pa = Load(a);
        var pb = Load(b);
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                if (pa[x, y] != pb[x, y])
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Captures repeatedly until two consecutive images are identical in the region (the
    /// taskbar's animations are over), or the time is up. Returns the last image.</summary>
    public static (Bitmap image, int frames, double seconds, bool settled) WaitSettled(Func<Bitmap> capture, Rectangle region, double maxSeconds, int intervalMs = 80)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Bitmap previous = capture();
        int frames = 1;
        while (sw.Elapsed.TotalSeconds < maxSeconds)
        {
            System.Threading.Thread.Sleep(intervalMs);
            Bitmap next = capture();
            frames++;
            bool same = SameRegion(previous, next, region);
            previous.Dispose();
            previous = next;
            if (same)
            {
                return (previous, frames, sw.Elapsed.TotalSeconds, true);
            }
        }
        return (previous, frames, sw.Elapsed.TotalSeconds, false);
    }

    /// <summary>Writes the region of each image enlarged (nearest neighbour), one above the other,
    /// separated by a light line.</summary>
    public static void SaveStacked(IList<Bitmap> images, Rectangle region, int zoom, string path)
    {
        const int gap = 6;
        int w = region.Width * zoom, h = region.Height * zoom;
        using var output = new Bitmap(w, images.Count * h + (images.Count - 1) * gap, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            g.Clear(Color.FromArgb(0xE0, 0xE0, 0xE0));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            for (int i = 0; i < images.Count; i++)
            {
                g.DrawImage(images[i], new Rectangle(0, i * (h + gap), w, h), region, GraphicsUnit.Pixel);
            }
        }
        output.Save(path, ImageFormat.Png);
    }
}
