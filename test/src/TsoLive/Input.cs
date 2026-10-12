// Injected mouse and keyboard input (SendInput), for the live test only. Every entry point is
// gated: with Enabled false (the kit's own tests and dry runs) nothing is sent and the call is logged.
//
// The hover sequence (test\README.md, "Driving the hover"): SetCursorPos does not open a flyout
// (it puts nothing in the input queue); park outside the taskbar, dwell, glide in with injected
// moves, then keep wiggling by one pixel while the flyout is read.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using TsoShared;

namespace TsoLive;

public static class Input
{
    public static volatile bool Enabled;
    public static Action<string> Log = _ => { };

    public const ushort VK_LWIN = 0x5B, VK_LCONTROL = 0xA2, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_ESCAPE = 0x1B, VK_LMENU = 0xA4, VK_D = 0x44, VK_F4 = 0x73;

    static bool Gate(string what)
    {
        if (Enabled)
        {
            Log("input: " + what);
            return true;
        }
        Log("DRY input skipped: " + what);
        return false;
    }

    public static POINT Cursor()
    {
        Native.GetCursorPos(out POINT p);
        return p;
    }

    static void Send(params INPUT[] inputs)
    {
        uint n = Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        if (n != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput sent {n} of {inputs.Length} (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>Absolute move anywhere on the virtual desktop, as injected input.</summary>
    static void RawMove(int x, int y)
    {
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        int nx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        Send(Native.Mouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, nx, ny));
    }

    static void RawGlide(POINT from, POINT to, int steps, int stepMs)
    {
        for (int i = 1; i <= steps; i++)
        {
            RawMove(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps);
            Thread.Sleep(stepMs);
        }
    }

    public static void MoveTo(int x, int y)
    {
        if (Gate($"move to ({x},{y})"))
        {
            RawMove(x, y);
        }
    }

    public static void RestoreCursor(POINT p)
    {
        if (Gate($"restore cursor to {p}"))
        {
            Native.SetCursorPos(p.X, p.Y);
        }
    }

    /// <summary>
    /// Drags with the left button from grab to drop, pausing (button held) for pauseMs halfway, so a
    /// sort that does not wait for the end of a drag would happen during the pause. The callback
    /// gets "down", "pause", "resume" and "up" at the moments they happen.
    /// </summary>
    public static void Drag(POINT grab, POINT drop, int steps, int stepMs, int pauseMs, Action<string> mark)
    {
        if (!Gate($"drag {grab} -> {drop}, {steps} steps of {stepMs} ms, pause {pauseMs} ms halfway"))
        {
            return;
        }
        RawGlide(Cursor(), grab, 12, 10);
        Thread.Sleep(150);
        Send(Native.Mouse(Native.MOUSEEVENTF_LEFTDOWN));
        mark("down");
        try
        {
            Thread.Sleep(150);
            int half = steps / 2;
            var mid = new POINT(grab.X + (drop.X - grab.X) * half / steps, grab.Y + (drop.Y - grab.Y) * half / steps);
            RawGlide(grab, mid, half, stepMs);
            mark("pause");
            Thread.Sleep(pauseMs);
            mark("resume");
            RawGlide(mid, drop, steps - half, stepMs);
            Thread.Sleep(100);
        }
        finally
        {
            Send(Native.Mouse(Native.MOUSEEVENTF_LEFTUP));
            mark("up");
        }
    }

    public static void Click(POINT p, bool right)
    {
        if (!Gate($"{(right ? "right" : "left")} click at {p}"))
        {
            return;
        }
        RawGlide(Cursor(), p, 10, 10);
        Thread.Sleep(80);
        Send(Native.Mouse(right ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_LEFTDOWN));
        Thread.Sleep(60);
        Send(Native.Mouse(right ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_LEFTUP));
    }

    /// <summary>Presses the keys in order and releases them in reverse (a chord such as Win+Left).</summary>
    public static void Chord(string what, params ushort[] keys)
    {
        if (!Gate($"keys {what}"))
        {
            return;
        }
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            bool ext = keys[i] is VK_LWIN or VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN;
            inputs[i] = Native.Key(keys[i], up: false, extended: ext);
            inputs[keys.Length * 2 - 1 - i] = Native.Key(keys[i], up: true, extended: ext);
        }
        // Down events one by one with a short gap, so the shell sees a held modifier.
        for (int i = 0; i < keys.Length; i++)
        {
            Send(inputs[i]);
            Thread.Sleep(30);
        }
        for (int i = keys.Length; i < inputs.Length; i++)
        {
            Send(inputs[i]);
            Thread.Sleep(20);
        }
    }

    /// <summary>Moves the cursor in circles around a centre for a while (for the CPU measurement).</summary>
    public static int Circles(POINT center, int radius, int durationMs, int stepMs)
    {
        if (!Gate($"circles around {center} r={radius} for {durationMs} ms"))
        {
            return 0;
        }
        int moves = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            double a = moves * 0.15;
            RawMove(center.X + (int)(radius * Math.Cos(a)), center.Y + (int)(radius * Math.Sin(a)));
            moves++;
            Thread.Sleep(stepMs);
        }
        return moves;
    }

    // ------------------------------------------------------------------ smooth movement (the README demo)

    /// <summary>Smoothstep easing: slow start, slow end, 0 to 1.</summary>
    public static double Ease(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    /// <summary>The points of an eased glide from one point to another, one per step of stepMs.</summary>
    public static List<POINT> GlidePath(POINT from, POINT to, int durationMs, int stepMs = 8)
    {
        int steps = Math.Max(1, durationMs / Math.Max(1, stepMs));
        var path = new List<POINT>(steps);
        for (int i = 1; i <= steps; i++)
        {
            double e = Ease(i / (double)steps);
            path.Add(new POINT((int)Math.Round(from.X + (to.X - from.X) * e), (int)Math.Round(from.Y + (to.Y - from.Y) * e)));
        }
        return path;
    }

    static void RawGlideEased(POINT from, POINT to, int durationMs, int stepMs = 8)
    {
        var sw = Stopwatch.StartNew();
        var path = GlidePath(from, to, durationMs, stepMs);
        for (int i = 0; i < path.Count; i++)
        {
            RawMove(path[i].X, path[i].Y);
            // Paced by the clock, not by the sleeps, so a slow step does not stretch the glide.
            int due = (int)((i + 1) * (long)durationMs / path.Count - sw.ElapsedMilliseconds);
            if (due > 0)
            {
                Thread.Sleep(due);
            }
        }
    }

    /// <summary>An eased glide of the cursor from where it is to a point (injected moves).</summary>
    public static void Glide(POINT to, int durationMs)
    {
        if (Gate($"glide to {to} in {durationMs} ms"))
        {
            RawGlideEased(Cursor(), to, durationMs);
        }
    }

    /// <summary>
    /// A drag for the camera: an eased glide to the grab point, button down, an eased glide to the drop
    /// point, button up. No pause on the way. The callback gets "down" and "up" when they happen.
    /// </summary>
    public static void DragSmooth(POINT grab, POINT drop, int approachMs, int dragMs, Action<string> mark)
    {
        if (!Gate($"smooth drag {grab} -> {drop} in {dragMs} ms"))
        {
            return;
        }
        RawGlideEased(Cursor(), grab, approachMs);
        Thread.Sleep(120);
        Send(Native.Mouse(Native.MOUSEEVENTF_LEFTDOWN));
        mark?.Invoke("down");
        try
        {
            Thread.Sleep(120);
            RawGlideEased(grab, drop, dragMs);
            Thread.Sleep(80);
        }
        finally
        {
            Send(Native.Mouse(Native.MOUSEEVENTF_LEFTUP));
            mark?.Invoke("up");
        }
    }

    /// <summary>Hovers a point the way that opens a taskbar flyout; keeps the hover alive until disposed.
    /// With glideMs > 0 the cursor glides (eased) from where it is instead of jumping to a parking spot.</summary>
    public static IDisposable Hover(POINT target, int parkAboveBy = 700, int glideMs = 0) => new Hovering(target, parkAboveBy, glideMs);

    sealed class Hovering : IDisposable
    {
        readonly Thread _wiggle;
        volatile bool _stop;
        readonly bool _active;

        public Hovering(POINT target, int parkAboveBy, int glideMs)
        {
            _active = Gate($"hover {target}{(glideMs > 0 ? $" (glide {glideMs} ms)" : "")}");
            if (!_active)
            {
                return;
            }
            if (glideMs > 0)
            {
                RawGlideEased(Cursor(), target, glideMs);
            }
            else
            {
                var park = new POINT(target.X, target.Y - parkAboveBy);
                RawMove(park.X, park.Y);
                Thread.Sleep(700);
                RawGlide(park, target, 30, 18);
            }
            _wiggle = new Thread(() =>
            {
                Win32.SetThreadDpiAwarenessContext(Win32.DPI_PER_MONITOR_AWARE_V2);
                bool odd = false;
                while (!_stop)
                {
                    try
                    {
                        RawMove(target.X + (odd ? 1 : 0), target.Y);
                    }
                    catch (Exception e)
                    {
                        Log("hover wiggle stopped: " + e.Message);  // never let a background thread end the process
                        return;
                    }
                    odd = !odd;
                    Thread.Sleep(60);
                }
            })
            { IsBackground = true, Name = "hover wiggle" };
            _wiggle.Start();
        }

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }
            _stop = true;
            _wiggle?.Join(1000);
        }
    }
}
