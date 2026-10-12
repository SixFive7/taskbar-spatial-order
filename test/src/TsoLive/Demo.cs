// Step 16, the README demo: two short scenes for the README's GIFs, played on a temporary virtual
// desktop (a taskbar with nothing else running on it) and recorded by rec\record.ps1 when the run
// has -Record; rec\make-gifs.ps1 cuts them out of the recording with demo.json.
//
//   without  The mod off. A..D open in a shuffled order (C, A, D, B) at places A B C D left to right;
//            hovering the group button shows the thumbnails in opening order: C A D B.
//   with     The mod on: it sorts them, the flyout shows A B C D; D is dragged to the far left of the
//            row; the flyout shows D A B C.
//
// demo.json (in the run directory) holds, in pixels of the primary monitor's recording: the crop
// (the four windows in a row, the flyout and the button), the test button, every other taskbar
// button inside the crop (the GIF builder paints them over with the taskbar's colour), points of
// known colour (to undo the brightening an HDR monitor's capture can have), and each scene's start
// and end in UTC.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using TsoShared;

namespace TsoLive;

/// <summary>Where the demo puts things. Pure geometry, unit-tested.</summary>
public sealed class DemoLayout
{
    public const int CropW = 1280, CropH = 800, WinW = 208, WinH = 156, Gap = 40, FlyoutH = 260;

    /// <summary>Screen pixels; its bottom is the monitor's bottom, so the taskbar is in it.</summary>
    public RECT Crop;
    /// <summary>Five window frames left to right; slot 0 stays empty until D is dragged there.</summary>
    public RECT[] Slots;
    /// <summary>Where the thumbnail flyout appears, below the windows (read by colour).</summary>
    public RECT FlyoutZone;
    /// <summary>Where the cursor waits between moves.</summary>
    public POINT Rest;

    public static DemoLayout Compute(RECT monitor, RECT work, RECT button)
    {
        int bx = (button.Left + button.Right) / 2;
        int x0 = Math.Clamp(bx - CropW / 2, monitor.Left, monitor.Right - CropW) & ~1;
        int y0 = (monitor.Bottom - CropH) & ~1;
        var crop = new RECT(x0, y0, x0 + CropW, y0 + CropH);
        int rowW = 5 * WinW + 4 * Gap;
        int left = x0 + (CropW - rowW) / 2;
        // The windows centred in the part of the crop above the flyout.
        int zoneBottom = work.Bottom - FlyoutH;
        int top = y0 + Math.Max(20, (zoneBottom - y0 - WinH) / 2);
        var slots = Enumerable.Range(0, 5)
            .Select(i => new RECT(left + i * (WinW + Gap), top, left + i * (WinW + Gap) + WinW, top + WinH)).ToArray();
        return new DemoLayout
        {
            Crop = crop,
            Slots = slots,
            FlyoutZone = new RECT(x0, work.Bottom - FlyoutH + 30, x0 + CropW, work.Bottom - 2),
            Rest = new POINT(x0 + CropW - 120, y0 + 70),
        };
    }

    /// <summary>
    /// The taskbar buttons to paint over: every given rectangle in the taskbar band that meets the
    /// crop, except the test button and what lies inside it, clipped to the crop; nested ones and
    /// containers wider than a button are dropped.
    /// </summary>
    public static List<RECT> MaskRects(RECT crop, RECT work, RECT monitor, IEnumerable<RECT> buttons, RECT testButton)
    {
        int tcx = (testButton.Left + testButton.Right) / 2, tcy = (testButton.Top + testButton.Bottom) / 2;
        var list = new List<RECT>();
        foreach (var b in buttons)
        {
            if (b.Width <= 0 || b.Height <= 0 || b.Width > 320 || b.Top < work.Bottom - 4 || b.Bottom > monitor.Bottom + 4)
            {
                continue;
            }
            if (b.Left <= tcx && tcx < b.Right && b.Top <= tcy && tcy < b.Bottom)
            {
                continue;  // the test button, or something around or inside it
            }
            if (b.Left >= testButton.Left && b.Right <= testButton.Right && b.Top >= testButton.Top && b.Bottom <= testButton.Bottom)
            {
                continue;
            }
            int l = Math.Max(b.Left, crop.Left), t = Math.Max(b.Top, crop.Top), r = Math.Min(b.Right, crop.Right), btm = Math.Min(b.Bottom, crop.Bottom);
            if (r - l < 2 || btm - t < 2)
            {
                continue;
            }
            list.Add(new RECT(l, t, r, btm));
        }
        // Drop exact duplicates (all but the first) and rectangles inside another one.
        var keep = new List<RECT>();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            bool drop = false;
            for (int j = 0; j < list.Count && !drop; j++)
            {
                if (j == i)
                {
                    continue;
                }
                var b = list[j];
                bool contains = b.Left <= a.Left && b.Top <= a.Top && b.Right >= a.Right && b.Bottom >= a.Bottom;
                bool same = b.Left == a.Left && b.Top == a.Top && b.Right == a.Right && b.Bottom == a.Bottom;
                drop = contains && (!same || j < i);
            }
            if (!drop)
            {
                keep.Add(a);
            }
        }
        return keep.OrderBy(a => a.Left).ToList();
    }

    /// <summary>A point in the middle row of the taskbar inside the crop, at least margin pixels from
    /// every button and the test button, nearest the crop's edges; null if there is none.</summary>
    public static POINT? TaskbarSample(RECT crop, RECT work, RECT monitor, IEnumerable<RECT> buttons, RECT testButton, int margin = 8)
    {
        int y = (work.Bottom + monitor.Bottom) / 2;
        var all = buttons.Append(testButton).ToList();
        for (int d = 6; d < crop.Width / 2; d += 2)
        {
            foreach (int x in new[] { crop.Left + d, crop.Right - 1 - d })
            {
                if (all.All(b => x < b.Left - margin || x >= b.Right + margin))
                {
                    return new POINT(x, y);
                }
            }
        }
        return null;
    }
}

/// <summary>One scene of the demo, as it goes into demo.json.</summary>
sealed class DemoScene
{
    public string Name, Expected, Flyout;
    public DateTime Start, End;
    public readonly List<(DateTime t, string what)> Marks = new();
    public RECT TestButton;
    public List<RECT> Others = new();
}

public sealed partial class LiveRunner
{
    // The shuffled opening order, and each window's place (slot) in the row: A B C D left to right.
    static readonly string[] DemoOpenOrder = { "C", "A", "D", "B" };
    static readonly Dictionary<string, int> DemoSlot = new() { ["A"] = 1, ["B"] = 2, ["C"] = 3, ["D"] = 4 };

    // Timing, for the camera: deliberate, eased cursor moves; about 5 s per scene.
    const int DemoGlideMs = 650, DemoAwayMs = 550, DemoApproachMs = 600, DemoDragMs = 950, DemoOpenGapMs = 320;
    const double DemoLeadS = 0.4, DemoTailS = 0.4, DemoHoldWithoutS = 2.0, DemoHoldFirstS = 1.1, DemoHoldSecondS = 1.6;

    DemoLayout _demoLayout;
    RECT _demoButton;
    List<RECT> _demoOthers = new();

    static string Utc(DateTime t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    void SceneMark(DemoScene s, string what)
    {
        s?.Marks.Add((DateTime.UtcNow, what));
        W?.Mark("demo: " + what);
    }

    void Step16()
    {
        long mark = Log.Mark();
        POINT saved = Input.Cursor();
        var scenes = new List<DemoScene>();
        var refs = new JsonArray();
        bool onTemp = false;
        try
        {
            // A temporary desktop: its taskbar shows only pinned apps and what is opened on it.
            RestoreDesktop();
            onTemp = CreateTempDesktop();
            if (!onTemp && !O.DryRun)
            {
                return;
            }
            EnsureApp();
            foreach (var k in _hwnd.Keys.ToList())
            {
                Close(k);
            }

            // Where the test button will be, and the other buttons beside it (with the test app's button
            // there, as in the scenes): one window opened and closed again, before anything is filmed.
            DemoPreRoll();
            _demoLayout = DemoLayout.Compute(Primary.Bounds, Primary.Work, _demoButton);
            _demoOthers = DemoLayout.MaskRects(_demoLayout.Crop, Primary.Work, Primary.Bounds, _demoOthers, _demoButton);
            Info("demo layout", $"button {_demoButton}; crop {_demoLayout.Crop}; slots {string.Join(" ", _demoLayout.Slots.Select(s => s.ToString()))}; " +
                                $"flyout zone {_demoLayout.FlyoutZone}; {_demoOthers.Count} other buttons in the crop: {string.Join(" ", _demoOthers)}");

            // The mod off before any window opens (otherwise it sorts them as they open).
            Act("Disable-Mod (scene without)");
            var r = Wh.DisableMod(ModId);
            J.ModDisabledByDemo = r.Ok;
            SaveJournal();
            Check("mod off for the scene without", r.Ok, r.ToString());
            if (!O.DryRun)
            {
                bool gone = WaitUntil(() => !ModLoadedInExplorer(), 8);
                Check("mod unloaded from Explorer", gone, gone ? "DLL gone from the shell process" : "DLL still loaded after 8s");
                if (!gone)
                {
                    return;
                }
            }
            Input.Glide(_demoLayout.Rest, 500);
            Sleep(O.DryRun ? 0 : 0.5);

            // ---- scene "without"
            var without = new DemoScene { Name = "without", Expected = "C,A,D,B", Start = DateTime.UtcNow };
            scenes.Add(without);
            SceneMark(without, "start");
            Sleep(O.DryRun ? 0 : DemoLeadS);
            foreach (var k in DemoOpenOrder)
            {
                Open(k, _demoLayout.Slots[DemoSlot[k]]);
                SceneMark(without, "opened " + k);
                Sleep(O.DryRun ? 0 : DemoOpenGapMs / 1000.0);
            }
            ExpectOrder("without the mod: the opening order C,A,D,B", "C,A,D,B", timeoutS: 3, stableS: 0.2, model: false);
            DemoButtons(without);
            without.Flyout = DemoHover(without, "without", DemoHoldWithoutS);
            DemoGlideAway(without);
            Sleep(O.DryRun ? 0 : DemoTailS);
            without.End = DateTime.UtcNow;
            SceneMark(without, "end");
            DemoRecheckButton(without);

            // ---- the mod on: its first pass sorts the group (between the scenes)
            Act("Enable-Mod (scene with)");
            r = Wh.EnableMod(ModId);
            J.ModDisabledByDemo = !r.Ok;
            SaveJournal();
            Check("mod on for the scene with", r.Ok, r.ToString());
            _modOk = O.DryRun || WaitUntil(ModLoadedInExplorer, 10);
            ExpectOrder("with the mod: sorted to the places A,B,C,D", "A,B,C,D", timeoutS: 8);
            Sleep(O.DryRun ? 0 : 0.6);
            refs = DemoColourRefs(without);

            // ---- scene "with"
            var with = new DemoScene { Name = "with", Expected = "A,B,C,D then D,A,B,C", Start = DateTime.UtcNow };
            scenes.Add(with);
            SceneMark(with, "start");
            Sleep(O.DryRun ? 0 : DemoLeadS);
            DemoButtons(with);
            string first = DemoHover(with, "with-sorted", DemoHoldFirstS, expected: "A,B,C,D");
            POINT grab;
            if (O.DryRun)
            {
                RECT fd = Frame("D");
                grab = new POINT(fd.Left + 40, fd.Top + 14);
            }
            else
            {
                var g = App.Cmd("grab", "D");
                grab = new POINT(g["x"]!.GetValue<int>(), g["y"]!.GetValue<int>());
            }
            var drop = new POINT(grab.X - (Frame("D").Left - _demoLayout.Slots[0].Left), grab.Y);
            Act($"drag D by its title bar from {grab} to {drop} (the far left of the row)");
            Input.DragSmooth(grab, drop, DemoApproachMs, DemoDragMs, m => SceneMark(with, "drag " + m));
            ExpectOrder("D dragged to the far left: D,A,B,C", "D,A,B,C", timeoutS: 3, stableS: 0.15);
            string second = DemoHover(with, "with-dragged", DemoHoldSecondS, expected: "D,A,B,C");
            with.Flyout = first + " then " + second;
            DemoGlideAway(with);
            Sleep(O.DryRun ? 0 : DemoTailS);
            with.End = DateTime.UtcNow;
            SceneMark(with, "end");
            foreach (var s in scenes)
            {
                Info($"scene {s.Name}", $"{(s.End - s.Start).TotalSeconds:0.0}s; flyout {s.Flyout} (expected {s.Expected})");
            }
        }
        finally
        {
            // The windows first, then the desktop: closing a desktop moves its windows to another one.
            DemoCloseWindows();
            if (J.ModDisabledByDemo && !Aborting())
            {
                var r = Wh.EnableMod(ModId);
                J.ModDisabledByDemo = !r.Ok;
                SaveJournal();
                _modOk = r.Ok && (O.DryRun || WaitQuiet(ModLoadedInExplorer, 10));
                Check("mod enabled again after the demo", r.Ok && _modOk, r.ToString());
            }
            if (J.TempDesktop != null)
            {
                bool closed = CloseTempDesktop();
                Check("temporary desktop closed", closed, closed ? "closed with Ctrl+Win+F4 while it was the current desktop" : "still there (see the detail log)");
            }
            if (!O.DryRun && !Aborting())
            {
                RestoreDesktop();
                Check("back on the original desktop", CurrentDesktop() == _desktopAtStart, $"current {CurrentDesktop()}, original {_desktopAtStart}");
            }
            Input.RestoreCursor(saved);
            if (_demoLayout != null)
            {
                WriteDemoJson(scenes, refs);
            }
        }
        CheckLog("log", mark);
    }

    /// <summary>Whether a stop is under way (an Explorer crash or hang, Ctrl+C or a STOP file).</summary>
    bool Aborting() => W?.Crash != null || W?.Hang != null || _cancel || File.Exists(Path.Combine(O.RunDir, "STOP"));

    /// <summary>WaitUntil without the stop check, for clean-up paths.</summary>
    static bool WaitQuiet(Func<bool> cond, double timeoutS)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed.TotalSeconds > timeoutS)
            {
                return false;
            }
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>Opens C, reads where its button is and every other button of the primary taskbar (as
    /// UI Automation reports them), closes C again. Dry: the taskbar's middle and two made-up neighbours.</summary>
    void DemoPreRoll()
    {
        int cx = (Primary.Work.Left + Primary.Work.Right) / 2;
        var simulated = new RECT(cx - 22, Primary.Work.Bottom, cx + 22, Primary.Bounds.Bottom);
        _demoButton = simulated;
        _demoOthers = new List<RECT>();
        if (O.DryRun)
        {
            _demoOthers.Add(new RECT(cx - 70, Primary.Work.Bottom, cx - 26, Primary.Bounds.Bottom));
            _demoOthers.Add(new RECT(cx + 26, Primary.Work.Bottom, cx + 70, Primary.Bounds.Bottom));
            return;
        }
        // A provisional layout around the middle of the taskbar, only for this one window.
        var provisional = DemoLayout.Compute(Primary.Bounds, Primary.Work, simulated);
        Open("C", provisional.Slots[DemoSlot["C"]]);
        UiaButton b = null;
        WaitUntil(() => (b = GroupButton(O.Aumid, Primary.Device)) != null, 4);
        if (b != null)
        {
            Sleep(0.5);  // the taskbar's own animation as the button arrives
            b = GroupButton(O.Aumid, Primary.Device) ?? b;
            _demoButton = ToRect(b.Rect);
            try
            {
                _demoOthers = Uia.Subtree(PrimaryTray(), 8000).Where(n => n.ControlType == "Button" && !n.Rect.IsEmpty).Select(n => ToRect(n.Rect)).ToList();
            }
            catch (Exception e)
            {
                Detail("demo: taskbar buttons not read: " + e.Message);
            }
        }
        else
        {
            Check("demo: the test button found", false, "no button for the test app on the primary taskbar within 4s; using the taskbar's middle");
        }
        Close("C");
        Sleep(0.6);
    }

    static RECT ToRect(System.Windows.Rect r) =>
        new((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.X + r.Width), (int)Math.Round(r.Y + r.Height));

    /// <summary>The test button and the other taskbar buttons inside the crop, for the scene: as the
    /// pre-roll found them (nothing is read while filming).</summary>
    void DemoButtons(DemoScene s)
    {
        s.TestButton = _demoButton;
        s.Others = _demoOthers.ToList();
    }

    /// <summary>After a scene: the test button read again; a move since the pre-roll is reported and
    /// taken over for the next scene.</summary>
    void DemoRecheckButton(DemoScene s)
    {
        if (O.DryRun)
        {
            return;
        }
        var b = GroupButton(O.Aumid, Primary.Device);
        if (b == null)
        {
            Check($"demo {s.Name}: the test button still there", false, "no button for the test app on the primary taskbar");
            return;
        }
        var now = ToRect(b.Rect);
        bool moved = Math.Abs(now.Left - _demoButton.Left) > 2 || Math.Abs(now.Top - _demoButton.Top) > 2;
        Check($"demo {s.Name}: the test button where the pre-roll found it", !moved, $"pre-roll {_demoButton}, now {now}");
        _demoButton = now;
    }

    /// <summary>
    /// Hovers the test button with an eased glide until the thumbnail flyout shows the four windows
    /// (read by their colours in the flyout zone), holds it there, and returns the order it showed.
    /// The cursor stays on the button.
    /// </summary>
    string DemoHover(DemoScene s, string tag, double holdS, string expected = null)
    {
        expected ??= s.Expected;
        RECT tb = s.TestButton;
        var target = new POINT((tb.Left + tb.Right) / 2, (tb.Top + tb.Bottom) / 2);
        if (!PointIsTaskbar(target, out string under))
        {
            Check($"demo {tag}: test button reachable", false, $"under the button's centre is {under}: no hover");
            return "";
        }
        var colours = Main4.ToDictionary(k => k, Rgb);
        List<string> order = new();
        SceneMark(s, "hover " + tag);
        Act($"hover the test button {target} ({tag})");
        using (Input.Hover(target, glideMs: DemoGlideMs))
        {
            var sw = Stopwatch.StartNew();
            while (!O.DryRun && sw.Elapsed.TotalSeconds < 3.5)
            {
                CheckStop();
                using (var img = Capture.Screen(_demoLayout.FlyoutZone))
                {
                    order = Capture.OrderByColors(img, new Rectangle(0, 0, img.Width, img.Height), colours, 40, 150);
                }
                if (order.Count == 4)
                {
                    break;
                }
                Thread.Sleep(90);
            }
            SceneMark(s, $"flyout {tag}: {Logic.Join(order)}");
            if (!O.DryRun)
            {
                using var shot = Capture.Screen(_demoLayout.FlyoutZone);
                shot.Save(CapturePath($"16-{tag}-flyout.png"));
            }
            Sleep(O.DryRun ? 0 : holdS);
        }
        Check($"demo {tag}: the flyout shows {expected}", Logic.Join(order) == expected,
            $"thumbnails left to right by colour: {(order.Count == 0 ? "none seen" : Logic.Join(order))}", dry: O.DryRun);
        return Logic.Join(order);
    }

    /// <summary>The cursor glides off the button to its resting place; waits until the flyout is gone.</summary>
    void DemoGlideAway(DemoScene s)
    {
        SceneMark(s, "away");
        Input.Glide(_demoLayout.Rest, DemoAwayMs);
        if (O.DryRun)
        {
            return;
        }
        var colours = Main4.ToDictionary(k => k, Rgb);
        bool gone = WaitUntil(() =>
        {
            using var img = Capture.Screen(_demoLayout.FlyoutZone);
            return Capture.OrderByColors(img, new Rectangle(0, 0, img.Width, img.Height), colours, 40, 150).Count == 0;
        }, 2);
        SceneMark(s, gone ? "flyout closed" : "flyout still open after 2s");
    }

    /// <summary>Points of known colour for the GIF builder (it compares them with the recording): the
    /// lower left of each window's client area, and a point of the taskbar as a GDI copy shows it.</summary>
    JsonArray DemoColourRefs(DemoScene basis)
    {
        var refs = new JsonArray();
        string now = Utc(DateTime.UtcNow);
        foreach (var k in Main4.Where(IsOpen))
        {
            RECT f = Frame(k);
            refs.Add(new JsonObject
            {
                ["utc"] = now, ["what"] = "window " + k, ["x"] = f.Left + 24 - Primary.Bounds.Left, ["y"] = f.Bottom - 20 - Primary.Bounds.Top,
                ["rgb"] = Rgb(k).ToString("X6"),
            });
        }
        if (!O.DryRun)
        {
            try
            {
                var p = DemoLayout.TaskbarSample(_demoLayout.Crop, Primary.Work, Primary.Bounds, basis.Others, basis.TestButton);
                if (p is POINT q)
                {
                    using var px = Capture.Screen(new RECT(q.X, q.Y, q.X + 1, q.Y + 1));
                    int argb = px.GetPixel(0, 0).ToArgb() & 0xFFFFFF;
                    refs.Add(new JsonObject
                    {
                        ["utc"] = now, ["what"] = "taskbar (GDI copy)", ["x"] = q.X - Primary.Bounds.Left, ["y"] = q.Y - Primary.Bounds.Top, ["rgb"] = argb.ToString("X6"),
                    });
                }
            }
            catch (Exception e)
            {
                Detail("demo: taskbar colour not read: " + e.Message);
            }
        }
        return refs;
    }

    void DemoCloseWindows()
    {
        foreach (var k in _hwnd.Keys.ToList())
        {
            try
            {
                W?.Mark("close " + k);
                Detail("ACT close " + k);
                App?.Cmd("close", k);
            }
            catch (Exception e)
            {
                Detail($"demo: closing {k} failed: {e.Message}");
            }
            _hwnd.Remove(k);
            _itemRef.Remove(k);
        }
        Publish();
    }

    JsonObject Box(RECT r) => new()
    {
        ["x"] = r.Left - Primary.Bounds.Left, ["y"] = r.Top - Primary.Bounds.Top, ["w"] = r.Width, ["h"] = r.Height,
    };

    void WriteDemoJson(List<DemoScene> scenes, JsonArray refs)
    {
        var L = _demoLayout;
        var json = new JsonObject
        {
            ["version"] = 1,
            ["runId"] = Path.GetFileName(O.RunDir),
            ["dry"] = O.DryRun,
            ["createdUtc"] = Utc(DateTime.UtcNow),
            ["note"] = "Rectangles are in pixels of the primary monitor's recording (screen minus the monitor's origin); times are UTC.",
            ["monitor"] = new JsonObject
            {
                ["device"] = Primary.Device, ["x"] = Primary.Bounds.Left, ["y"] = Primary.Bounds.Top, ["w"] = Primary.Bounds.Width, ["h"] = Primary.Bounds.Height,
                ["workBottom"] = Primary.Work.Bottom - Primary.Bounds.Top,
            },
            ["crop"] = Box(L.Crop),
            ["taskbar"] = new JsonObject { ["y"] = Primary.Work.Bottom - Primary.Bounds.Top, ["h"] = Primary.Bounds.Bottom - Primary.Work.Bottom },
            ["slots"] = new JsonArray(L.Slots.Select(s => (JsonNode)Box(s)).ToArray()),
            ["colourRefs"] = refs,
        };
        var first = scenes.FirstOrDefault(s => s.TestButton.Width > 0);
        if (first != null)
        {
            json["testButton"] = Box(first.TestButton);
            json["otherButtons"] = new JsonArray(first.Others.Select(o => (JsonNode)Box(o)).ToArray());
        }
        var arr = new JsonArray();
        foreach (var s in scenes)
        {
            var o = new JsonObject
            {
                ["name"] = s.Name,
                ["startUtc"] = Utc(s.Start),
                ["endUtc"] = s.End == default ? null : Utc(s.End),
                ["seconds"] = s.End == default ? null : Math.Round((s.End - s.Start).TotalSeconds, 3),
                ["expected"] = s.Expected,
                ["flyout"] = s.Flyout,
                ["marks"] = new JsonArray(s.Marks.Select(m => (JsonNode)new JsonObject { ["utc"] = Utc(m.t), ["what"] = m.what }).ToArray()),
            };
            if (s.TestButton.Width > 0)
            {
                o["testButton"] = Box(s.TestButton);
                o["otherButtons"] = new JsonArray(s.Others.Select(x => (JsonNode)Box(x)).ToArray());
            }
            arr.Add(o);
        }
        json["scenes"] = arr;
        string path = Path.Combine(O.RunDir, "demo.json");
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Info("demo.json", $"{path}: {scenes.Count} scenes, crop {L.Crop.Width}x{L.Crop.Height}");
    }

    // ------------------------------------------------------------------ the temporary desktop

    /// <summary>Ctrl+Win+D: a new virtual desktop, switched to. The desktops before it are journaled
    /// first, the new one as soon as it shows in the registry.</summary>
    bool CreateTempDesktop()
    {
        var (before, current) = Monitors.VirtualDesktops();
        J.DesktopsBeforeTemp = before.Select(g => g.ToString()).ToList();
        SaveJournal();
        Act("Ctrl+Win+D (a temporary virtual desktop for the demo)");
        if (O.DryRun)
        {
            Input.Chord("Ctrl+Win+D", Input.VK_LCONTROL, Input.VK_LWIN, Input.VK_D);
            Check("temporary desktop", true, $"dry: would add a desktop to {before.Count} and switch to it", dry: true);
            return false;
        }
        Input.Chord("Ctrl+Win+D", Input.VK_LCONTROL, Input.VK_LWIN, Input.VK_D);
        Guid temp = Guid.Empty;
        bool ok = WaitUntil(() =>
        {
            var (now, cur) = Monitors.VirtualDesktops();
            var added = now.Where(g => !before.Contains(g)).ToList();
            if (added.Count == 1 && cur == added[0])
            {
                temp = added[0];
                return true;
            }
            return false;
        }, 4);
        if (!ok)
        {
            // Late in the registry, or not switched to: still ours if exactly one desktop is new (or the
            // current one is unknown), so it is journaled and closed again in any case.
            Thread.Sleep(1500);
            var (now, cur) = Monitors.VirtualDesktops();
            var added = now.Where(g => !before.Contains(g)).ToList();
            temp = added.Count == 1 ? added[0] : (cur != Guid.Empty && !before.Contains(cur) ? cur : Guid.Empty);
            ok = temp != Guid.Empty && cur == temp;
        }
        if (temp != Guid.Empty)
        {
            J.TempDesktop = temp.ToString();
            SaveJournal();
        }
        var after = Monitors.VirtualDesktops();
        Check("temporary desktop created and current", ok, ok
            ? $"{temp}: {before.Count} -> {after.desktops.Count} desktops"
            : $"no new current desktop within 4s; desktops {string.Join(", ", after.desktops)}, current {after.current}");
        Sleep(0.8);  // the switch animation
        return ok;
    }

    /// <summary>Closes the temporary desktop (never another one), then the journal forgets it.</summary>
    bool CloseTempDesktop()
    {
        if (J?.TempDesktop == null || !Guid.TryParse(J.TempDesktop, out Guid temp))
        {
            return true;
        }
        var before = J.DesktopsBeforeTemp.Select(g => Guid.Parse(g)).ToHashSet();
        bool ok = CloseTempDesktopCore(temp, before, s => Detail("desktop: " + s));
        if (ok)
        {
            J.TempDesktop = null;
            SaveJournal();
            Thread.Sleep(600);  // the switch animation
        }
        return ok;
    }

    /// <summary>
    /// Ctrl+Win+F4 closes the current virtual desktop: pressed only while the temporary one is
    /// current (switching to it first, by the registry's list), and never for a desktop that existed
    /// before the demo. Input must be enabled. True when it is gone.
    /// </summary>
    internal static bool CloseTempDesktopCore(Guid temp, ICollection<Guid> before, Action<string> say)
    {
        if (before.Contains(temp))
        {
            say($"refusing to close {temp}: it existed before the demo");
            return false;
        }
        var (list, cur) = Monitors.VirtualDesktops();
        if (!list.Contains(temp))
        {
            say($"temporary desktop {temp} is already gone");
            return true;
        }
        for (int i = 0; i < list.Count && cur != temp; i++)
        {
            if (!list.Contains(cur))
            {
                say($"current desktop {cur} is not in the list: not switching");
                return false;
            }
            bool right = list.IndexOf(temp) > list.IndexOf(cur);
            Input.Chord(right ? "Ctrl+Win+Right" : "Ctrl+Win+Left", Input.VK_LCONTROL, Input.VK_LWIN, right ? Input.VK_RIGHT : Input.VK_LEFT);
            Thread.Sleep(500);
            cur = Monitors.VirtualDesktops().current;
        }
        if (cur != temp)
        {
            say($"could not switch to the temporary desktop {temp} (current {cur})");
            return false;
        }
        say($"Ctrl+Win+F4 on the temporary desktop {temp}");
        Input.Chord("Ctrl+Win+F4", Input.VK_LCONTROL, Input.VK_LWIN, Input.VK_F4);
        bool gone = WaitQuiet(() => !Monitors.VirtualDesktops().desktops.Contains(temp), 4);
        say(gone ? $"temporary desktop {temp} closed; current {Monitors.VirtualDesktops().current}" : $"temporary desktop {temp} still there 4s after Ctrl+Win+F4");
        return gone;
    }
}
