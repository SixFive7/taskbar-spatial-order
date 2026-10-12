// The sixteen steps of the live test. Each step sets up what it needs (so any subset can be run
// again after a fix), acts, and checks. Expected orders are written out literally; ExpectOrder
// also derives them from the windows' geometry with the mod's rule, as a check on the layout.
//
// Window keys: A..D (and E) are the test app's main group, ordered left to right A,B,C,D at their
// standard places on the primary monitor; Z, Z1, Z2 are sentinel windows of a second group, opened
// after the main group, so that a group jumping to the end of a taskbar would land behind them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using TsoShared;

namespace TsoLive;

public sealed partial class LiveRunner
{
    // The order of this table is the order the steps run in: step 16, the README demo, runs right
    // after step 2 (it needs the 2.0 alpha's command line to switch the mod off and on, and a fresh
    // taskbar before the tests open their windows). A selection only picks steps from it.
    List<StepDef> Table() => new()
    {
        // Estimates: the longest live run plus a margin; steps 1, 2 and 14 are dominated by the
        // Windhawk functions (installer, compile, uninstall plus the offline install of the version
        // that was there before).
        new() { Id = 1, Name = "s01-upgrade-windhawk", Eta = 30, Body = Step01 },
        new() { Id = 2, Name = "s02-install-mod-with-log", Eta = 20, Body = Step02 },
        new() { Id = 16, Name = "s16-readme-demo", Eta = 35, NeedsMod = true, Body = Step16 },
        new() { Id = 3, Name = "s03-open-right-to-left", Eta = 12, NeedsMod = true, Body = Step03 },
        new() { Id = 4, Name = "s04-drag-across", Eta = 8, NeedsMod = true, Body = Step04 },
        new() { Id = 5, Name = "s05-snap-maximise-restore", Eta = 8, NeedsMod = true, Body = Step05 },
        new() { Id = 6, Name = "s06-minimise-and-settings", Eta = 18, NeedsMod = true, Body = Step06 },
        new() { Id = 7, Name = "s07-never-combine", Eta = 15, NeedsMod = true, Body = Step07 },
        new() { Id = 8, Name = "s08-three-monitors", Eta = 8, NeedsMod = true, Body = Step08 },
        new() { Id = 9, Name = "s09-flyout-and-jump-list", Eta = 25, NeedsMod = true, Body = Step09 },
        new() { Id = 10, Name = "s10-open-close-identity-desktops", Eta = 18, NeedsMod = true, Body = Step10 },
        new() { Id = 11, Name = "s11-disable-enable-restart", Eta = 45, NeedsMod = true, Body = Step11 },
        new() { Id = 12, Name = "s12-explorer-cpu", Eta = 50, NeedsMod = true, Body = Step12 },
        new() { Id = 13, Name = "s13-progress-badge-toolbar", Eta = 20, NeedsMod = true, Body = Step13 },
        new() { Id = 14, Name = "s14-restore-windhawk-stable", Eta = 60, Body = Step14 },
        new() { Id = 15, Name = "s15-clean-up", Eta = 5, Body = Step15 },
    };

    // ------------------------------------------------------------------ helpers for the steps

    bool WaitUntil(Func<bool> cond, double timeoutS)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            CheckStop();
            if (cond())
            {
                return true;
            }
            if (sw.Elapsed.TotalSeconds > timeoutS)
            {
                return false;
            }
            Thread.Sleep(100);
        }
    }

    void WaitForShell(double timeoutS)
    {
        var sw = Stopwatch.StartNew();
        while (ExplorerInfo.Shell().Pid == 0 && sw.Elapsed.TotalSeconds < timeoutS)
        {
            Thread.Sleep(200);
        }
    }

    static string L1(TestView v) => v?.GroupOfAny(MainKeys)?.L1Text ?? "";

    static string L2(TestView v) => v?.GroupOfAny(MainKeys)?.L2Text ?? "";

    string EventsText(string key, double t0, double t1)
    {
        var ev = W.Events(t0, t1).Where(e => e.Key == key).ToList();
        if (ev.Count == 0)
        {
            return $"{key}: no events";
        }
        return $"{key}: " + string.Join(", ", ev.GroupBy(e => WinEventRecord.Name(e.Event))
            .Select(g => $"{g.Key}x{g.Count()}@{g.First().T - t0:0.00}s"));
    }

    int TrayOf(TestView v, MonitorInfo m) => v.TrayMonitors.IndexOf(m.Device);

    void MainBeforeSentinel(string label, MonitorInfo mon)
    {
        if (O.DryRun)
        {
            Check(label, true, "dry", dry: true);
            return;
        }
        var v = W.Latest();
        int t = TrayOf(v, mon);
        var main = v.GroupOfAny(MainKeys);
        var sentinel = v.Groups.FirstOrDefault(g => g.L1.Any(k => k.StartsWith("Z", StringComparison.Ordinal)));
        if (t < 0 || main == null || sentinel == null || !v.Sequence.ContainsKey(t))
        {
            Info(label, "main or sentinel group not on this taskbar");
            return;
        }
        int a = v.Sequence[t].IndexOf(main.Ptr), b = v.Sequence[t].IndexOf(sentinel.Ptr);
        Check(label, a >= 0 && b >= 0 && a < b, $"{mon.Device}: main group at {a}, sentinel at {b} of {v.Sequence[t].Count}");
    }

    Dictionary<int, List<long>> Sequences() => O.DryRun ? new() : W.Latest().Sequence.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());

    string GroupLabel(long ptr)
    {
        var g = W.LatestOrNull()?.Groups.FirstOrDefault(x => x.Ptr == ptr);
        return g != null ? $"[{g.L1Text}]" : ptr.ToString("X")[^5..];
    }

    void CheckSequence(string label, Dictionary<int, List<long>> reference)
    {
        if (O.DryRun)
        {
            Check(label, true, "dry", dry: true);
            return;
        }
        var now = W.Latest().Sequence;
        var problems = new List<string>();
        foreach (var kv in reference)
        {
            if (now.TryGetValue(kv.Key, out var cur))
            {
                string d = Logic.CompareGroupSequence(kv.Value, cur, GroupLabel);
                if (d != null)
                {
                    problems.Add($"t{kv.Key}: {d}");
                }
            }
        }
        Check(label, problems.Count == 0, problems.Count == 0 ? $"group order unchanged on {reference.Count} taskbars" : string.Join("; ", problems));
    }

    void ExpectUiaOrder(string label, MonitorInfo mon, string expected, bool soft = false)
    {
        if (O.DryRun)
        {
            Check(label, true, $"would read the window buttons on {mon.Device}", dry: true);
            return;
        }
        var want = Logic.Split(expected);
        List<string> got = new();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 3)
        {
            try
            {
                got = WindowButtons(mon.Device).Where(want.Contains).ToList();
            }
            catch (Exception e)
            {
                Detail("UIA: " + e.Message);
            }
            if (Logic.Join(got) == expected)
            {
                break;
            }
            Sleep(0.2);
        }
        if (soft && got.Count < want.Count)
        {
            Info(label, $"UIA shows {Logic.Join(got)} of {expected} (the rest may be in the overflow)");
            return;
        }
        Check(label, Logic.Join(got) == expected, $"window buttons left to right: {Logic.Join(got)}");
    }

    void CheckUiaCounts(string label, Dictionary<MonitorInfo, int> want)
    {
        if (O.DryRun)
        {
            Check(label, true, "dry", dry: true);
            return;
        }
        var buttons = Uia.TaskbarButtons().Where(b => b.AppId == O.Aumid).ToList();
        var have = new List<string>();
        bool ok = true;
        foreach (var kv in want)
        {
            var b = buttons.FirstOrDefault(x => { Win32.GetWindowRect(x.Tray, out RECT r); return Monitors.Of(r, Mons)?.Device == kv.Key.Device; });
            int n = b?.RunningWindows ?? 0;
            have.Add($"{kv.Key.Device}={n}{(b == null ? "(no button)" : "")}");
            ok &= n == kv.Value;
        }
        Check(label, ok, $"want {string.Join(" ", want.Select(kv => $"{kv.Key.Device}={kv.Value}"))}; have {string.Join(" ", have)}");
    }

    UiaButton WindowButton(string key) => Uia.TaskbarButtons().FirstOrDefault(b => KeyOfButton(b) == key);

    Probe _probe;

    /// <summary>The list slots in the mod's log against those the probe finds in the same taskbar.</summary>
    void CheckSlots(string label, bool seen, ModLogFacts f)
    {
        int groups, items;
        try
        {
            var o = (_probe ??= new Probe()).Offsets();
            (groups, items) = (o.ButtonGroupsOff / 8, o.GroupItemsOff / 8);
        }
        catch (ProbeException e)
        {
            Check(label, false, "the probe could not find the slots to compare with: " + e.Message);
            return;
        }
        Check(label, seen && f.ButtonGroupsSlot == groups && f.ItemsSlot == items, f.ButtonGroupsSlot != null
            ? $"button group list in slot {f.ButtonGroupsSlot} (probe {groups}), item list in slot {f.ItemsSlot} (probe {items})"
            : $"no slots in the log ({f.Lines} lines; failures: {string.Join(" | ", f.Failures)})");
    }

    /// <summary>Every app's group on every taskbar is in the mod's order (default settings).</summary>
    void CheckAllGroupsSorted(string label)
    {
        if (O.DryRun)
        {
            Check(label, true, "dry", dry: true);
            return;
        }
        _probe ??= new Probe();
        var snap = _probe.Read();
        int sorted = 0, skipped = 0;
        var wrong = new List<string>();
        foreach (var g in snap.Groups.Values.Where(g => g.Items.Count >= 2))
        {
            if (g.Hwnds.Any(h => h == IntPtr.Zero || !Win32.IsWindow(h)))
            {
                skipped++;
                continue;
            }
            var geoms = g.Hwnds.Select((h, i) => Logic.Geometry(i.ToString(), h)).ToList();
            if (geoms.All(x => x.Minimised))
            {
                skipped++;
                continue;
            }
            var want = Logic.ExpectedOrder(geoms, true);
            var have = geoms.Select(x => x.Key).ToList();
            if (want.SequenceEqual(have))
            {
                sorted++;
            }
            else
            {
                string exe = "?";
                try
                {
                    using var p = Process.GetProcessById((int)Win32.PidOf(g.Hwnds[0]));
                    exe = p.ProcessName;
                }
                catch
                {
                }
                wrong.Add($"{exe} ({g.Items.Count} windows)");
            }
        }
        Check(label, wrong.Count == 0, $"{sorted} groups sorted, {skipped} skipped (all minimised or non-window items){(wrong.Count > 0 ? "; not sorted: " + string.Join(", ", wrong) : "")}");
    }

    string EngineInExplorer()
    {
        var s = ExplorerInfo.Shell();
        if (s.Pid == 0)
        {
            return "no shell";
        }
        var mods = ExplorerInfo.Modules(s.Pid, n => n.StartsWith("windhawk", StringComparison.OrdinalIgnoreCase));
        return mods.Count == 0 ? "not loaded" : string.Join(", ", mods.Select(m => $"{m.Name} {m.FileVersion}"));
    }

    /// <summary>Whether Get-WindhawkState says the mod's logging is on (LoggingEnabled, or Logging in the stubs).</summary>
    bool IsLoggingOn(JsonNode state)
    {
        var mod = Windhawk.FindMod(state, ModId);
        string v = Windhawk.Find(mod, "LoggingEnabled") ?? Windhawk.Find(mod, "Logging");
        return mod == null || v == null || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsDisabled(JsonNode mod)
    {
        string disabled = Windhawk.Find(mod, "Disabled"), enabled = Windhawk.Find(mod, "Enabled");
        return string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
    }

    string KnownModsFile => Path.Combine(StateDir, "installed-mod-ids.txt");

    IEnumerable<string> KnownModIds() => File.Exists(KnownModsFile) ? File.ReadAllLines(KnownModsFile).Where(l => l.Trim().Length > 0).Distinct() : Enumerable.Empty<string>();

    void RememberModId(string id)
    {
        if (!O.DryRun && !KnownModIds().Contains(id))
        {
            File.AppendAllLines(KnownModsFile, new[] { id });
        }
    }

    /// <summary>Taskbar state as the test app reports it: whether ITaskbarList3 accepted it.</summary>
    void CheckTaskbarStateApplied(string label)
    {
        var list = App.Send("list")["windows"]!.AsArray();
        var problems = new List<string>();
        var counts = new List<string>();
        foreach (var w in list)
        {
            string k = w["key"]!.ToString();
            if (!ProgressSpec.ContainsKey(k))
            {
                continue;
            }
            counts.Add($"{k}:created={w["buttonCreated"]}");
            foreach (string part in new[] { "progress", "overlay", "thumbbar" })
            {
                string hr = w[part]?["hr"]?.ToString() ?? "unset";
                if (hr != "0x00000000")
                {
                    problems.Add($"{k} {part} {hr}");
                }
            }
        }
        Check(label, problems.Count == 0, problems.Count == 0 ? "progress, badge and toolbar accepted for A-D; " + string.Join(" ", counts) : string.Join(", ", problems));
    }

    // ------------------------------------------------------------------ 1. upgrade Windhawk

    void Step01()
    {
        var shell0 = ExplorerInfo.Shell();
        var before = Wh.State();
        string v0 = Windhawk.FindVersion(before.Result);
        Info("before", $"Windhawk {v0 ?? "?"}; engine in Explorer: {EngineInExplorer()}");
        J.WindhawkUpgraded = true;
        SaveJournal();
        WhResult r;
        W.ExpectRestart = true;  // an installer may restart Explorer
        try
        {
            Act("Install-WindhawkAlpha");
            r = Wh.InstallAlpha();
        }
        finally
        {
            WaitForShell(30);
            W.Rebaseline();
            W.ExpectRestart = false;
        }
        Check("Install-WindhawkAlpha", r.Ok, r.ToString());
        var after = Wh.State();
        string v1 = Windhawk.FindVersion(after.Result);
        bool alpha = v1 != null && (Logic.IsPrerelease(v1) || (v0 != null && Logic.CompareVersions(v1, v0) > 0));
        Check("Windhawk is the alpha", alpha, $"{v0 ?? "?"} -> {v1 ?? "?"}");
        var shell1 = ExplorerInfo.Shell();
        Check("Explorer alive", shell1.Pid != 0, $"{shell0} -> {shell1}{(shell1.SameProcess(shell0) ? "" : " (restarted during the install)")}");
        Info("engine in Explorer", EngineInExplorer());
    }

    // ------------------------------------------------------------------ 2. install the mod with logging

    void Step02()
    {
        foreach (string other in KnownModIds().Where(id => id != ModId))
        {
            Info("other variant disabled", Wh.DisableMod(other).ToString());
        }
        Act("Install-LocalMod (disabled)");
        var r = Wh.InstallLocalMod(O.ModPath, disabled: true);
        Check("Install-LocalMod", r.Ok, r.ToString());
        RememberModId(ModId);
        r = Wh.SetModLogging(ModId, true);
        J.ModLoggingEnabledByRun = r.Ok;
        SaveJournal();
        Check("Set-ModLogging on", r.Ok, r.ToString());
        r = Wh.StartLogCapture(Log.Path);
        J.LogCaptureStarted = r.Ok;
        SaveJournal();
        Check("Start-ModLogCapture", r.Ok, r.ToString());

        var seqBefore = Sequences();
        long mark = Log.Mark();
        Act("Enable-Mod");
        r = Wh.EnableMod(ModId);
        Check("Enable-Mod", r.Ok, r.ToString());
        var (seen, f) = WaitLog(mark, x => x.ButtonGroupsSlot != null || x.Failures.Count > 0 || x.Unexpected.Count > 0, O.DryRun ? 3 : 90);
        CheckSlots("log shows the offsets it found", seen, f);
        Sleep(O.DryRun ? 0.1 : 2.5);  // the first pass sorts every app's group
        CheckLog("log has no Unexpected lines", mark);
        _modOk = ModLoadedInExplorer();
        Check("mod loaded in Explorer", _modOk, _modOk ? "the mod's DLL is in the shell process" : "the mod's DLL is not in the shell process");
        CheckSequence("no app group moved when the mod sorted them", seqBefore);
        CheckAllGroupsSorted("every app's group is in on-screen order");
    }

    // ------------------------------------------------------------------ 3. open right to left

    void Step03()
    {
        EnsureApp();
        RestoreGlom("grouping");
        EnsureDefaultSettings();
        foreach (var k in _hwnd.Keys.ToList())
        {
            Close(k);
        }
        Sleep(0.8);
        long mark = Log.Mark();
        string[] keys = { "A", "B", "C", "D" };
        double[] fx = { 0.70, 0.49, 0.29, 0.08 };
        for (int i = 0; i < keys.Length; i++)
        {
            Open(keys[i], AtX(keys[i], fx[i]));
            RequestTaskbarState(keys[i]);
            Sleep(0.15);
        }
        Open("Z", Std("Z"), SentinelAumid);
        ExpectOrder("opened A..D right to left: sorted", "D,C,B,A", timeoutS: 5);
        MainBeforeSentinel("main group before the sentinel", Primary);
        CheckTaskbarStateAppliedSoon("progress, badge, toolbar set");

        UiaButton gb = O.DryRun ? null : GroupButton(O.Aumid, Primary.Device);
        if (!O.DryRun)
        {
            Check("one combined button with 4 windows", gb != null && gb.RunningWindows == 4, gb?.ToString() ?? "no group button");
        }
        if (gb != null || O.DryRun)
        {
            POINT target = gb != null ? Center(gb.Rect) : default;
            if (!PointIsTaskbar(target, out string under))
            {
                Check("group button reachable", false, $"under the button's centre is {under}, not the taskbar: no hover");
            }
            else
            {
                var read = HoverRead(target, "open", fr => ThumbnailKeys(fr).Count(Main4.Contains) >= 4);
                CheckFlyoutOrder("thumbnail flyout order", read, "D,C,B,A");
                if (read != null)
                {
                    Info("thumbnail toolbar buttons in the flyout", string.Join("; ", read.ThumbBarsByThumbnail().Select(kv => $"{kv.Key}: {string.Join(",", kv.Value)}")));
                }
            }
        }
        CheckLog("log", mark);
    }

    List<string> _lastImageOrder = new();
    Dictionary<string, int> _lastThumbPixelsByKey = new();
    int _lastThumbPixels;

    void CheckFlyoutOrder(string label, FlyoutRead read, string expected)
    {
        var uia = ThumbnailKeys(read).Where(MainKeys.Contains).ToList();
        if (uia.Count > 0)
        {
            Check(label, Logic.Join(uia) == expected, $"UIA thumbnails left to right: {Logic.Join(ThumbnailKeys(read))}; image: {Logic.Join(_lastImageOrder)}");
        }
        else
        {
            var img = _lastImageOrder.Where(MainKeys.Contains).ToList();
            Check(label, Logic.Join(img) == expected, $"no thumbnails via UIA ({read?.All.Count ?? 0} elements under {read?.Hosts.Count ?? 0} hosts); by window colour: {Logic.Join(img)}");
        }
    }

    void CheckTaskbarStateAppliedSoon(string label)
    {
        if (O.DryRun)
        {
            Check(label, true, "dry (hidden windows have no taskbar buttons)", dry: true);
            return;
        }
        WaitUntil(() =>
        {
            var list = App.Send("list")["windows"]!.AsArray();
            return list.Where(w => ProgressSpec.ContainsKey(w["key"]!.ToString()))
                       .All(w => (w["progress"]?["hr"]?.ToString() ?? "") != "pending" && (w["thumbbar"]?["hr"]?.ToString() ?? "") != "pending");
        }, 5);
        CheckTaskbarStateApplied(label);
    }

    // ------------------------------------------------------------------ 4. drag across

    void Step04()
    {
        Baseline();
        long mark = Log.Mark();
        RECT target = AtX("D", 0.03);
        RECT f0 = Frame("D");
        POINT grab;
        if (!O.DryRun)
        {
            var g = App.Cmd("grab", "D");
            grab = new POINT(g["x"]!.GetValue<int>(), g["y"]!.GetValue<int>());
        }
        else
        {
            grab = new POINT(f0.Left + 100, f0.Top + 12);
        }
        var drop = new POINT(grab.X - (f0.Left - target.Left), grab.Y);
        Raise("D");
        if (!PointIsWindow(grab, "D", out string under))
        {
            Check("D's title bar is under the grab point", false, $"under {grab} is {under}: no drag (a click there would hit another window)");
            return;
        }
        double tDown = double.NaN, tUp = double.NaN, tPause = double.NaN, tResume = double.NaN;
        POINT saved = Input.Cursor();
        Act($"drag D by its title bar from {grab} to {drop}");
        Input.Drag(grab, drop, steps: 50, stepMs: 18, pauseMs: 700, mark: m =>
        {
            double t = W.Now;
            W.Mark("drag " + m);
            switch (m)
            {
                case "down": tDown = t; break;
                case "pause": tPause = t; break;
                case "resume": tResume = t; break;
                case "up": tUp = t; break;
            }
        });
        if (O.DryRun)
        {
            Check("drag", true, "dry: no input", dry: true);
            return;
        }
        Sleep(2.5);
        double tEnd = W.Now;
        RECT f1 = Frame("D");
        Check("the window moved", Math.Abs(f1.Left - target.Left) < 60, $"D {f0} -> {f1}; target left {target.Left}");

        var states = new List<(double, string, string)>();
        var first = W.StateAt(tDown);
        if (first != null)
        {
            states.Add((first.T, L1(first), L2(first)));
        }
        foreach (var e in W.Timeline(tDown, tEnd).Where(e => e.Kind == "state"))
        {
            states.Add((e.T, L1(e.View), L2(e.View)));
        }
        var v = Logic.AnalyzeDrag(states, tDown, tUp, tEnd, "D,A,B,C");
        Check("nothing during the drag, one move ~0.4 s after release", v.Pass,
            v.Detail + $"; drag {tUp - tDown:0.00}s with a {tResume - tPause:0.00}s pause");
        var f = CheckLog("log", mark);
        int reorders = f.Reorders.Count(r => r.process.Equals("TsoTestApp.exe", StringComparison.OrdinalIgnoreCase));
        Check("one reorder of the test group in the log", reorders == 1,
            $"{reorders} reorder lines; moved: {string.Join(", ", f.Moves.Where(m => m.process.Equals("TsoTestApp.exe", StringComparison.OrdinalIgnoreCase)).Select(m => m.moved))}");
        Info("events of the drag", EventsText("D", tDown - 0.2, tUp + 0.6));
        CheckIdentity("drag", W.Latest());
        Input.RestoreCursor(saved);
    }

    // ------------------------------------------------------------------ 5. snap, maximise, restore

    void Step05()
    {
        Baseline();
        long mark = Log.Mark();

        Raise("D");
        var focus = App.Cmd("focus", "D");
        bool fg = O.DryRun || (focus["foreground"]?.GetValue<bool>() == true && Win32.GetForegroundWindow() == H("D"));
        Check("D in the foreground for Win+Left", fg, focus.ToJsonString() + $" foreground now 0x{Win32.GetForegroundWindow().ToInt64():X}");
        double t0 = W.Now;
        bool Snapped()
        {
            RECT r = Frame("D");
            return r.Left == Primary.Work.Left && r.Top == Primary.Work.Top && Math.Abs(r.Width - Primary.Work.Width / 2) < 30;
        }
        if (fg)
        {
            // Keys go to the foreground window: sent only when that is D, never to another app.
            Act("Win+Left on D (keyboard)");
            Input.Chord("Win+Left", Input.VK_LWIN, Input.VK_LEFT);
            if (!O.DryRun)
            {
                WaitUntil(Snapped, 2);
                var wp = Win32.Placement(H("D"));
                Check("D snapped to the left half", Snapped(), $"frame {Frame("D")}; restored rect {wp.rcNormalPosition} (differs from the frame for a real snap)");
            }
        }
        else
        {
            // Without the keyboard snap, the programmatic equivalent keeps the later expectations valid.
            Act("D to the left half (SetWindowPos, instead of Win+Left)");
            App.Cmd("snap", "D", "left");
        }
        ExpectOrder("after Win+Left", "D,A,B,C");
        Info("events of Win+Left (keyboard)", EventsText("D", t0, W.Now));

        t0 = W.Now;
        Act("A to the right half (SetWindowPos)");
        App.Cmd("snap", "A", "right");
        ExpectOrder("after the programmatic snap", "D,B,C,A");
        Info("events of the programmatic snap", EventsText("A", t0, W.Now));

        Move("D", Std("D"));
        Move("A", Std("A"));
        ExpectOrder("back to the standard places", "A,B,C,D");

        t0 = W.Now;
        Show("max", "C");
        ExpectOrder("C maximised", "C,A,B,D");
        Info("events of maximise", EventsText("C", t0, W.Now));
        t0 = W.Now;
        Show("restore", "C");
        ExpectOrder("C restored", "A,B,C,D");
        Info("events of restore", EventsText("C", t0, W.Now));
        CheckLog("log", mark);
    }

    // ------------------------------------------------------------------ 6. minimise, settings, exclusion

    void Step06()
    {
        Baseline();
        long mark = Log.Mark();
        Show("min", "B");
        ExpectOrder("B minimised: last", "A,C,D,B");
        Show("min", "A");
        ExpectOrder("A minimised: minimised ones by restored place", "C,D,A,B");
        Show("min", "C");
        ExpectOrder("C minimised", "D,A,B,C");
        Show("min", "D");
        ExpectUnchanged("all minimised: group left alone", "D,A,B,C", 1.5);

        SetSetting("skipFullyMinimisedGroups", false, "skip off");
        ExpectOrder("skip off: all minimised sorted by restored place", "A,B,C,D", timeoutS: 5);
        SetSetting("skipFullyMinimisedGroups", true, "skip on");

        Show("unmin", "A");
        Show("unmin", "C");
        ExpectOrder("A, C restored: minimised last", "A,C,B,D");
        SetSetting("minimisedLast", false, "minimised-last off");
        ExpectOrder("minimised-last off: by place", "A,B,C,D", timeoutS: 5);
        SetSetting("minimisedLast", true, "minimised-last on");
        ExpectOrder("minimised-last on again", "A,C,B,D", timeoutS: 5);
        Show("unmin", "B");
        Show("unmin", "D");
        ExpectOrder("all restored", "A,B,C,D");

        SetSetting("excludedApps", new[] { "TsoTestApp.exe" }, "exclude the test app");
        Move("A", AtX("A", 0.86));
        ExpectUnchanged("excluded: not reordered", "A,B,C,D", 1.5);
        SetSetting("excludedApps", Array.Empty<string>(), "exclusion removed");
        ExpectOrder("exclusion removed: sorted", "B,C,D,A", timeoutS: 5);
        CheckLog("log", mark);
    }

    // ------------------------------------------------------------------ 7. never combine

    void Step07()
    {
        Baseline();
        long mark = Log.Mark();
        SetNeverCombine("never combine: each window gets its own button");
        ExpectOrder("uncombined", "A,B,C,D", timeoutS: 5);
        ExpectUiaOrder("window buttons left to right", Primary, "A,B,C,D");
        MainBeforeSentinel("main group before the sentinel", Primary);
        CheckTaskbarStateVisual("uncombined");
        var seq = Sequences();

        Move("A", AtX("A", 0.86));
        ExpectOrder("A moved to the right end", "B,C,D,A");
        ExpectUiaOrder("window buttons follow", Primary, "B,C,D,A");
        CheckSequence("no group jumped (A last)", seq);
        MainBeforeSentinel("main group still before the sentinel", Primary);
        CheckTaskbarStateVisual("A last");

        Move("A", Std("A"));
        ExpectOrder("A back to the left end", "A,B,C,D");
        ExpectUiaOrder("window buttons follow", Primary, "A,B,C,D");
        CheckSequence("no group jumped (A first)", seq);

        Move("C", AtX("C", 0.03));
        ExpectOrder("C moved to the left end", "C,A,B,D");
        ExpectUiaOrder("window buttons follow", Primary, "C,A,B,D");
        CheckSequence("no group jumped (C first)", seq);
        MainBeforeSentinel("main group still before the sentinel", Primary);
        CheckTaskbarStateVisual("C first");

        if (!O.GlomPrimaryOnly && RightMon != null)
        {
            Move("C", RightMon.At(0.05, 0.3, 640, 420));
            Move("D", RightMon.At(0.40, 0.3, 640, 420));
            ExpectTrays("C, D on the right monitor", "A,B,C,D", new() { [Primary] = "A,B", [RightMon] = "C,D" });
            var seqR = Sequences();
            Move("C", RightMon.At(0.55, 0.55, 640, 420));
            ExpectTrays("C behind D on the right monitor", "A,B,D,C", new() { [Primary] = "A,B", [RightMon] = "D,C" });
            ExpectUiaOrder("window buttons on the right taskbar", RightMon, "D,C", soft: true);
            CheckSequence("no group jumped on the right taskbar", seqR);
            Move("C", Std("C"));
            Move("D", Std("D"));
            ExpectOrder("back on the primary monitor", "A,B,C,D");
        }
        RestoreGlom("grouping restored, verified by reading it back");
        CheckLog("log", mark);
    }

    // ------------------------------------------------------------------ 8. three monitors

    void Step08()
    {
        Baseline();
        if (LeftMon == null || RightMon == null)
        {
            Check("three monitors", false, $"{Mons.Count} monitors; this step needs one left and one right of the primary");
            return;
        }
        long mark = Log.Mark();
        Move("D", LeftMon.At(0.15, 0.3, 640, 420));
        Move("A", RightMon.At(0.40, 0.3, 640, 420));
        Move("C", RightMon.At(0.05, 0.3, 640, 420));
        Open("E", Primary.At(0.60, 0.28, 640, 420));
        ExpectTrays("spread over three monitors (one alone on the left)", "D,B,E,C,A",
            new() { [LeftMon] = "D", [Primary] = "B,E", [RightMon] = "C,A" }, 5);
        Open("Z1", LeftMon.At(0.5, 0.65, 400, 300), SentinelAumid);
        Open("Z2", RightMon.At(0.5, 0.65, 400, 300), SentinelAumid);
        Sleep(O.DryRun ? 0 : 1.0);
        var seq = Sequences();
        MainBeforeSentinel("left taskbar: main group before the sentinel", LeftMon);
        MainBeforeSentinel("right taskbar: main group before the sentinel", RightMon);
        CheckUiaCounts("window count per taskbar", new() { [LeftMon] = 1, [Primary] = 2, [RightMon] = 2 });

        Move("A", RightMon.At(0.01, 0.3, 640, 420));
        ExpectTrays("A before C on the right monitor", "D,B,E,A,C", new() { [LeftMon] = "D", [Primary] = "B,E", [RightMon] = "A,C" });
        CheckSequence("no group jumped", seq);

        Move("B", LeftMon.At(0.5, 0.3, 640, 420));
        ExpectTrays("B moved to the left monitor", "D,B,E,A,C", new() { [LeftMon] = "D,B", [Primary] = "E", [RightMon] = "A,C" });
        CheckSequence("no group jumped after a move between monitors", seq);
        CheckUiaCounts("window count per taskbar after the move", new() { [LeftMon] = 2, [Primary] = 1, [RightMon] = 2 });

        Close("Z1");
        Close("Z2");
        Close("E");
        CheckLog("log", mark);
    }

    // ------------------------------------------------------------------ 9. move while the flyout or jump list is open

    void Step09()
    {
        Baseline();
        long mark = Log.Mark();
        var shell = ExplorerInfo.Shell();
        UiaButton gb = O.DryRun ? null : GroupButton(O.Aumid, Primary.Device);
        if (!O.DryRun && gb == null)
        {
            Check("group button", false, "no combined button for the test group");
            return;
        }
        POINT target = gb != null ? Center(gb.Rect) : default;
        if (!PointIsTaskbar(target, out string under))
        {
            Check("group button reachable", false, $"under the button's centre is {under}, not the taskbar: no hover, no right-click");
            return;
        }

        bool stayedOpen = false;
        List<string> during = new();
        var before = HoverRead(target, "flyout-move", fr => ThumbnailKeys(fr).Count(Main4.Contains) >= 4, whileOpen: () =>
        {
            Move("B", AtX("B", 0.80));
            ExpectOrder("sorted while the flyout is open", "A,C,D,B", timeoutS: 3);
            if (!O.DryRun)
            {
                try
                {
                    var fr = Uia.Flyout(shell.Pid, O.Prefix);
                    stayedOpen = fr.Open;
                    during = ThumbnailKeys(fr);
                }
                catch (Exception e)
                {
                    Detail("flyout read: " + e.Message);
                }
            }
        });
        CheckFlyoutOrder("flyout before the move", before, "A,B,C,D");
        Info("flyout during the move", $"stayed open: {stayedOpen}; thumbnails now {Logic.Join(during)}");
        if (stayedOpen && during.Count(Main4.Contains) >= 4)
        {
            Check("open flyout shows the new order", Logic.Join(during.Where(Main4.Contains)) == "A,C,D,B", $"thumbnails {Logic.Join(during)}");
        }
        else
        {
            var again = HoverRead(target, "flyout-after", fr => ThumbnailKeys(fr).Count(Main4.Contains) >= 4);
            CheckFlyoutOrder("flyout opened again shows the new order", again, "A,C,D,B");
        }

        // The jump list. On build 26300 it is drawn by ShellExperienceHost (JumpViewUI.dll), not by
        // Explorer, so it is detected there (JumpList.cs). It is always closed before the step ends:
        // while it is open the mod rightly waits, and the next step would start unsorted.
        Info("jump list code before", O.DryRun ? "dry" : JumpListProbe.JumpViewLoaded());
        var snapBefore = O.DryRun ? new PopupSnapshot() : JumpListProbe.Take(W.Now, withUia: true);
        POINT saved = Input.Cursor();
        Act("right-click the group button (jump list)");
        Input.Click(target, right: true);
        var open = new JumpListSignals();
        double tOpen = W.Now, tClosed = double.NaN;
        bool reorderedWhileOpen = false;
        try
        {
            if (!O.DryRun)
            {
                var sw = Stopwatch.StartNew();
                do
                {
                    CheckStop();
                    open = JumpListProbe.Compare(snapBefore, JumpListProbe.Take(W.Now, withUia: true));
                    if (open.Confirmed)
                    {
                        break;
                    }
                    Thread.Sleep(80);
                }
                while (sw.Elapsed.TotalSeconds < 3);
                Check("jump list opened", open.Confirmed, $"{open} | after {sw.Elapsed.TotalSeconds:0.00}s");
                Info("taskbar thread while the jump list is open", ExplorerInfo.GuiThreadState(shell.TrayThread));
            }
            Move("B", Std("B"));
            double tMove = W.Now;
            if (!O.DryRun && open.Confirmed)
            {
                bool stillOpen = true;
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < 1.5)
                {
                    CheckStop();
                    if (!JumpListProbe.StillOpen(open))
                    {
                        stillOpen = false;
                        break;
                    }
                    Thread.Sleep(50);
                }
                var changes = W.Timeline(tMove, W.Now).Where(e => e.Kind == "state" && L1(e.View) != "A,C,D,B").ToList();
                reorderedWhileOpen = changes.Count > 0;
                Info("jump list still open while watching", stillOpen ? $"open for the {W.Now - tMove:0.00}s after the move" : $"closed {W.Now - tMove:0.00}s after the move");
                Check("nothing reorders while the jump list is open", !reorderedWhileOpen,
                    reorderedWhileOpen ? $"reordered to {L1(changes[0].View)} {changes[0].T - tMove:0.000}s after the move, with the jump list open"
                                       : $"Explorer's list stayed A,C,D,B for {W.Now - tMove:0.00}s after the move");
            }
            else
            {
                Sleep(O.DryRun ? 0 : 1.5);
            }
        }
        finally
        {
            // Whatever happened above (a failed check, an exception, a stop request): close it.
            tClosed = CloseJumpList(open, snapBefore, "jump list closed before the step ends") ?? double.NaN;
        }
        if (!O.DryRun && open.Confirmed && !reorderedWhileOpen && !double.IsNaN(tClosed))
        {
            var (ok, view, _) = WaitFor(v => L1(v) == "A,B,C,D", 2.5, 0.2);
            var hit = W.Timeline(tOpen, W.Now).FirstOrDefault(e => e.Kind == "state" && L1(e.View) == "A,B,C,D");
            double delay = hit != null ? hit.T - tClosed : double.NaN;
            Check("reorder within 1.5 s after the jump list closed", ok && hit != null && delay <= 1.5,
                hit != null ? $"Explorer's list became A,B,C,D {delay:0.000}s after the jump list closed" : $"no reorder; list {L1(view)}");
        }
        ExpectOrder("sorted after the jump list closed", "A,B,C,D", timeoutS: 3);
        Input.RestoreCursor(saved);
        CheckLog("log", mark);
    }

    /// <summary>
    /// Closes the jump list (or any shell popup the right-click opened) and checks that nothing is
    /// open any more. Esc only goes out when a shell popup host has the foreground (then the key
    /// reaches the popup, not another app); otherwise a click on test window A's client area, only if
    /// A is what is under that point, dismisses it. When nothing was detected, the click is made
    /// anyway (it is harmless) in case the jump list is open but went unseen. Returns when it was
    /// seen closed (on the runner's clock), null if it is still open.
    /// </summary>
    double? CloseJumpList(JumpListSignals open, PopupSnapshot before, string label)
    {
        // No CheckStop in here: this also runs while a stop request unwinds the step.
        void Note(string what)
        {
            W.Mark(what);
            Detail("ACT " + what);
        }
        if (O.DryRun)
        {
            Note("Esc (close the jump list)");
            Input.Chord("Esc", Input.VK_ESCAPE);
            Check(label, true, "dry", dry: true);
            return W.Now;
        }
        bool Anything(out string what)
        {
            var sig = JumpListProbe.Compare(before, JumpListProbe.Take(W.Now, withUia: false));
            bool still = JumpListProbe.StillOpen(open);
            what = still ? $"the jump list (0x{open.Host.ToInt64():X} {open.HostProcess}) is still shown" : sig.Confirmed ? sig.ToString() : "nothing open";
            return still || sig.Confirmed;
        }
        bool WaitClosed(double seconds)
        {
            var sw = Stopwatch.StartNew();
            while (Anything(out _))
            {
                if (sw.Elapsed.TotalSeconds > seconds)
                {
                    return false;
                }
                Thread.Sleep(60);
            }
            return true;
        }
        void ClickTestWindow(string why)
        {
            if (!IsOpen("A"))
            {
                Detail("not clicking to close the jump list: test window A is not open");
                return;
            }
            App.Cmd("raise", "A");
            RECT a = Frame("A");
            var p = new POINT(a.Left + a.Width / 2, a.Top + a.Height * 3 / 4);
            if (PointIsWindow(p, "A", out string under))
            {
                Note($"click test window A to dismiss the jump list ({why})");
                Input.Click(p, right: false);
            }
            else
            {
                Detail($"not clicking to close the jump list: under {p} is {under}");
            }
        }
        bool seen = open.Confirmed;
        for (int attempt = 1; attempt <= 3 && Anything(out _); attempt++)
        {
            seen = true;
            if (JumpListProbe.ForegroundIsHost(out string fg))
            {
                Note($"Esc (close the jump list; foreground {fg})");
                Input.Chord("Esc", Input.VK_ESCAPE);
            }
            else
            {
                ClickTestWindow("the foreground is not a shell popup host: " + fg);
            }
            WaitClosed(2);
        }
        if (!seen)
        {
            ClickTestWindow("nothing was detected; dismissing in case it is open but unseen");
            Thread.Sleep(300);
        }
        bool closed = !Anything(out string state);
        double t = W.Now;
        Check(label, closed, closed ? (seen ? "closed" : "nothing detected open; dismissed with a click on test window A anyway") : state);
        return closed ? t : null;
    }

    // ------------------------------------------------------------------ 10. open, close, identity, desktops

    void Step10()
    {
        Baseline();
        long mark = Log.Mark();
        Open("E", Std("E"));
        ExpectOrder("E opened between B and C", "A,B,E,C,D");
        Close("B");
        ExpectOrder("B closed", "A,E,C,D");
        if (!O.DryRun)
        {
            CheckUiaCounts("window count after close", new() { [Primary] = 4 });
        }

        Act("C: AppUserModelID -> another app");
        App.Cmd("aumid", "C", AltAumid);
        _changedAumid.Add("C");
        _itemRef.Remove("C");
        if (!O.DryRun)
        {
            var (ok, view, _) = WaitFor(v => v.GroupOf("C") != null && v.GroupOf("A") != null && v.GroupOf("C") != v.GroupOf("A") && L1(v) == "A,E,D", 3, 0.3);
            Check("C left the group", ok, $"main L1={L1(view)}; C in [{view?.GroupOf("C")?.L1Text}]");
        }
        Act("C: AppUserModelID -> back");
        App.Cmd("aumid", "C", "default");
        _changedAumid.Remove("C");
        _itemRef.Remove("C");  // joining a group is a new item
        ExpectOrder("C back in the group, sorted in", "A,E,C,D", timeoutS: 4);

        if (_desktops.Count >= 2)
        {
            Guid current = CurrentDesktop();
            Guid other = _desktops.First(d => d != current);
            Act("A, D to another virtual desktop (programmatically)");
            _onOtherDesktop.Add("A");
            _onOtherDesktop.Add("D");
            App.Cmd("vdmove", "D", other.ToString());
            App.Cmd("vdmove", "A", other.ToString());
            if (!O.DryRun)
            {
                var (ok, view, _) = WaitFor(v => v.GroupOfAny(MainKeys) is { } g && !g.L2.Values.SelectMany(x => x).Any(k => k == "A" || k == "D"), 3, 0.3);
                Check("A, D left the taskbar", ok, $"L1={L1(view)} buttons {L2(view)}; cloaked A={Win32.Cloaked(H("A"))} D={Win32.Cloaked(H("D"))}");
            }
            Move("A", AtX("A", 0.60));
            Act("A, D back to this desktop");
            App.Cmd("vdmove", "A", current.ToString());
            App.Cmd("vdmove", "D", current.ToString());
            _onOtherDesktop.Clear();
            // Explorer may rebuild the items of windows that come back from another desktop.
            _itemRef.Remove("A");
            _itemRef.Remove("D");
            ExpectOrder("back from the other desktop: sorted", "E,C,A,D", timeoutS: 4);

            int idx = _desktops.IndexOf(current);
            bool right = idx < _desktops.Count - 1;
            if (SwitchDesktop(right, "switched to another desktop"))
            {
                if (!O.DryRun)
                {
                    bool cloaked = WaitUntil(() => Win32.Cloaked(H("A")) != 0, 2);
                    Check("test windows hidden by the switch", cloaked, $"cloaked A={Win32.Cloaked(H("A"))}");
                }
                Move("E", AtX("E", 0.80));
                Sleep(O.DryRun ? 0 : 0.8);
                SwitchDesktop(!right, "switched back");
                if (!O.DryRun)
                {
                    Check("back on the original desktop", CurrentDesktop() == _desktopAtStart, $"current {CurrentDesktop()}, original {_desktopAtStart}");
                }
                _itemRef.Clear();
                ExpectOrder("after switching back: sorted", "C,A,D,E", timeoutS: 4);
            }
        }
        else
        {
            Check("virtual desktops", false, "only one virtual desktop: nothing to switch to");
        }
        Close("E");
        CheckLog("log", mark);
    }

    // ------------------------------------------------------------------ 11. disable, enable, settings, Explorer restart

    void Step11()
    {
        Baseline();
        long mark = Log.Mark();

        Act("Disable-Mod");
        var r = Wh.DisableMod(ModId);
        Check("Disable-Mod", r.Ok, r.ToString());
        if (!O.DryRun)
        {
            bool unloaded = WaitUntil(() => !ModLoadedInExplorer(), 8);
            Check("mod unloaded from Explorer", unloaded, unloaded ? "DLL gone from the shell process" : "DLL still loaded after 8s");
        }
        Move("A", AtX("A", 0.86));
        ExpectUnchanged("disabled: nothing is sorted", "A,B,C,D", 1.5);

        long m2 = Log.Mark();
        Act("Enable-Mod");
        r = Wh.EnableMod(ModId);
        Check("Enable-Mod", r.Ok, r.ToString());
        var (seen, f) = WaitLog(m2, x => x.ButtonGroupsSlot != null, O.DryRun ? 1 : 20);
        CheckSlots("enabled again: log shows the offsets", seen, f);
        ExpectOrder("enabled again: sorted", "B,C,D,A", timeoutS: 5);
        _modOk = O.DryRun || ModLoadedInExplorer();

        SetSetting("minimisedLast", false, "setting changed while enabled");
        ExpectOrder("still sorted", "B,C,D,A");
        SetSetting("minimisedLast", true, "setting changed back");
        ExpectOrder("still sorted", "B,C,D,A");

        long m3 = Log.Mark();
        RestartExplorer();
        ExpectOrder("sorted after Explorer restarted", "B,C,D,A", timeoutS: 20);
        if (!O.DryRun)
        {
            var (seen3, f3) = WaitLog(m3, x => x.ButtonGroupsSlot != null, 20);
            bool loaded = ModLoadedInExplorer();
            Check("the new Explorer loaded the mod", seen3 && loaded, $"offsets line: {seen3} (slots {f3.ButtonGroupsSlot},{f3.ItemsSlot}); DLL loaded: {loaded}");
            _modOk = loaded;
        }
        // A new Explorer starts without any app's progress, badges and toolbars; the app sets them again.
        App.Cmd("reapply", "all");
        CheckTaskbarStateAppliedSoon("progress, badge, toolbar set again after the restart");
        CheckLog("log", mark);
    }

    // ------------------------------------------------------------------ 12. Explorer CPU, mod on and off

    (double process, double thread) Measure(string label, POINT centre)
    {
        var s = ExplorerInfo.Shell();
        var a = ExplorerInfo.Cpu(s);
        Act("mouse circles: " + label);
        int moves = Input.Circles(centre, 150, 6000, 8);
        if (O.DryRun)
        {
            Thread.Sleep(300);
        }
        var b = ExplorerInfo.Cpu(s);
        var rate = ExplorerInfo.Rate(a, b);
        var coarse = ExplorerInfo.RateFromTimes(a, b);
        Info(label, $"{moves} moves in {rate.seconds:0.0}s: Explorer {rate.process:0.00} ms/s, taskbar thread {rate.thread:0.00} ms/s " +
                    $"(cycle counters; from times {coarse.process:0.0} / {coarse.thread:0.0} ms/s)");
        return (rate.process, rate.thread);
    }

    void Step12()
    {
        Baseline();
        RECT fb = Frame("B");
        var centre = new POINT(fb.Left + fb.Width / 2, fb.Top + fb.Height / 2);
        Raise("B");  // the circles stay over B (moves only, no clicks)
        POINT saved = Input.Cursor();
        int poll = W.PollMs;
        W.PollMs = 500;
        try
        {
            var on1 = Measure("mod on", centre);
            Act("Disable-Mod");
            var r = Wh.DisableMod(ModId);
            Check("Disable-Mod", r.Ok, r.ToString());
            if (!O.DryRun)
            {
                WaitUntil(() => !ModLoadedInExplorer(), 8);
                Sleep(1);
            }
            var offRate = Measure("mod off", centre);
            long mark = Log.Mark();
            Act("Enable-Mod");
            r = Wh.EnableMod(ModId);
            Check("Enable-Mod", r.Ok, r.ToString());
            if (!O.DryRun)
            {
                WaitLog(mark, x => x.ButtonGroupsSlot != null, 20);
                Sleep(1);
            }
            var on2 = Measure("mod on again", centre);
            // The lower of the two "on" samples: background work in Explorer only ever adds to a sample.
            var (pass, detail) = Logic.CpuVerdict(Math.Min(on1.thread, on2.thread), offRate.thread, Math.Min(on1.process, on2.process), offRate.process, 15, 40);
            Check("Explorer CPU while the mouse moves: mod on vs off", pass, detail + $"; on samples thread {on1.thread:0.0}/{on2.thread:0.0}, process {on1.process:0.0}/{on2.process:0.0}", dry: O.DryRun);
        }
        finally
        {
            W.PollMs = poll;
            Input.RestoreCursor(saved);
        }
        _modOk = O.DryRun || ModLoadedInExplorer();
        ExpectOrder("still sorted", "A,B,C,D");
    }

    // ------------------------------------------------------------------ 13. progress, badge and toolbar survive

    void Step13()
    {
        Baseline();
        if (!_taskbarStateRequested)
        {
            foreach (var k in Main4)
            {
                RequestTaskbarState(k);
            }
        }
        CheckTaskbarStateAppliedSoon("progress, badge, toolbar set");
        int failed = Survival.Count(s => !s.Pass);
        Check("survived every earlier reorder", failed == 0,
            $"{Survival.Count} survival checks during this run, {failed} failed{(failed > 0 ? ": " + string.Join("; ", Survival.Where(s => !s.Pass).Take(4).Select(s => s.Name + " " + s.Detail)) : "")}");

        SetNeverCombine("never combine");
        ExpectOrder("uncombined", "A,B,C,D", timeoutS: 5);
        CheckTaskbarStateVisual("before the reorder");
        Move("D", AtX("D", 0.03));
        ExpectOrder("D moved to the left end", "D,A,B,C");
        CheckTaskbarStateVisual("after the reorder");

        foreach (var k in new[] { "D", "A" })
        {
            UiaButton b = O.DryRun ? null : WindowButton(k);
            if (!O.DryRun && b == null)
            {
                Check($"{k}'s button", false, "not found");
                continue;
            }
            POINT target = b != null ? Center(b.Rect) : default;
            if (!PointIsTaskbar(target, out string under))
            {
                Check($"{k}'s button reachable", false, $"under the button's centre is {under}, not the taskbar: no hover");
                continue;
            }
            var read = HoverRead(target, "toolbar-" + k, fr => fr.ThumbBarButtons.Count >= 2);
            var names = read?.ThumbBarButtons.Select(n => n.Name).ToList() ?? new List<string>();
            int mine = names.Count(n => n.StartsWith($"{O.Prefix} {k} btn", StringComparison.Ordinal));
            bool ok = mine == 2 || (names.Count == 0 && _lastThumbPixels >= 2 * 60);
            Check($"{k}'s thumbnail toolbar ({(k == "D" ? "moved" : "not moved")})", ok,
                $"UIA toolbar buttons: [{string.Join(", ", names)}]; toolbar-colour pixels in the flyout image: {_lastThumbPixels}");
        }
        RestoreGlom("grouping restored, verified by reading it back");
        if (!O.DryRun)
        {
            var list = App.Send("list")["windows"]!.AsArray();
            Info("TaskbarButtonCreated per window", string.Join(" ", list.Select(w => $"{w["key"]}={w["buttonCreated"]}")));
        }
    }

    // ------------------------------------------------------------------ 14. Windhawk back to the version from before

    void Step14()
    {
        var before = Wh.State();
        string v0 = Windhawk.FindVersion(before.Result);
        // Logging off while the 2.0 command line still exists (1.x has no windhawk-cli).
        if (IsLoggingOn(before.Result))
        {
            var lr = Wh.SetModLogging(ModId, false);
            Check("mod logging off before the downgrade", lr.Ok, lr.ToString());
            J.ModLoggingEnabledByRun = !lr.Ok;
            SaveJournal();
        }
        WhResult r;
        W.ExpectRestart = true;
        try
        {
            Act("Restore-WindhawkStable");
            r = Wh.RestoreStable();
        }
        finally
        {
            WaitForShell(30);
            W.Rebaseline();
            W.ExpectRestart = false;
        }
        Check("Restore-WindhawkStable", r.Ok, r.ToString());
        var after = Wh.State();
        string v1 = Windhawk.FindVersion(after.Result);
        bool back = v1 != null && J.WindhawkVersionBefore != null && Logic.CompareVersions(v1, J.WindhawkVersionBefore) == 0;
        Check("Windhawk is the version from before the run", back, $"{v0 ?? "?"} -> {v1 ?? "?"} (before the run: {J.WindhawkVersionBefore ?? "?"})");
        J.WindhawkUpgraded = !back;
        SaveJournal();
        var mod = Windhawk.FindMod(after.Result, ModId);
        Check("mod still installed and enabled", mod != null && !IsDisabled(mod), mod != null ? Logic.OneLine(mod.ToJsonString()) : "not in Get-WindhawkState");
        bool loaded = O.DryRun || WaitUntil(ModLoadedInExplorer, 30);
        Check("mod loaded in Explorer", loaded, loaded ? "DLL in the shell process" : "not loaded within 30s", dry: O.DryRun);
        _modOk = loaded;
        if (loaded)
        {
            Baseline();
            Move("A", AtX("A", 0.86));
            ExpectOrder("the mod still sorts", "B,C,D,A", timeoutS: 5);
            Move("A", Std("A"));
            ExpectOrder("and back", "A,B,C,D");
        }
    }

    // ------------------------------------------------------------------ 15. clean up

    void Step15()
    {
        if (_modOk || O.DryRun)
        {
            EnsureDefaultSettings();
        }
        WhResult r;
        var stateBefore = Wh.State();
        if (IsLoggingOn(stateBefore.Result))
        {
            r = Wh.SetModLogging(ModId, false);
            Check("mod logging off", r.Ok, r.ToString());
            J.ModLoggingEnabledByRun = !r.Ok;
            SaveJournal();
        }
        else
        {
            Check("mod logging off", true, "already off");
        }
        QuitApp();
        var leftovers = Process.GetProcessesByName("TsoTestApp");
        Check("test app gone", leftovers.Length == 0, leftovers.Length == 0 ? "no TsoTestApp process" : $"{leftovers.Length} still running");
        var stray = ExplorerInfo.WindowsWithPrefix(O.Prefix);
        Check("no test windows", stray.Count == 0, stray.Count == 0 ? "none" : string.Join(", ", stray.Select(s => s.title)));
        if (J.LogCaptureStarted)
        {
            r = Wh.StopLogCapture();
            J.LogCaptureStarted = !r.Ok;
            Check("log capture stopped", r.Ok, r.ToString());
        }
        RestoreGlom("grouping");
        var now = RegistryValues.ReadAll(Microsoft.Win32.Registry.CurrentUser, J.Glom.KeyPath, RegistryValues.GlomNames);
        bool same = J.Glom.Original.All(o => now.Any(n => RegistryValues.Same(n, o)));
        Check("grouping as before the run", same, $"before {string.Join(", ", J.Glom.Original)}; now {string.Join(", ", now)}");
        Check("same virtual desktop as before", O.DryRun || CurrentDesktop() == _desktopAtStart, $"{CurrentDesktop()} (was {_desktopAtStart})");
        var state = Wh.State();
        var mod = Windhawk.FindMod(state.Result, ModId);
        Check("mod installed and enabled", mod != null && !IsDisabled(mod), mod != null ? Logic.OneLine(mod.ToJsonString()) : "not in Get-WindhawkState");
        if (!O.DryRun)
        {
            bool loaded = ModLoadedInExplorer();
            Check("mod loaded in Explorer", loaded, loaded ? "yes" : "no");
        }
        Check("settings at their defaults", Settings == (true, true, ""), $"minimisedLast={Settings.minimisedLast} skip={Settings.skip} excluded='{Settings.excluded}'");
        var children = ExplorerInfo.Children(Environment.ProcessId).Where(c => !c.exe.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        Check("nothing left running", children.Count == 0, children.Count == 0 ? "no child processes" : string.Join(", ", children.Select(c => $"{c.exe}({c.pid})")));
        Input.RestoreCursor(_cursorAtStart);
    }
}
