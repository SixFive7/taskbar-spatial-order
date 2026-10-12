// tso-live: the live test's tools from the command line.
//
//   selftest                 unit tests of the pure logic
//   steps                    the step table with estimated times
//   plan [steps]             the steps a run would take, in order, with estimates (touches nothing)
//   probe [out]              oracle (a): Explorer's lists, read with ReadProcessMemory
//   uia [out]                oracle (b): taskbar buttons via UI Automation (and an open flyout)
//   explorer [seconds]       oracle (c): shell identity, start time, CPU over a few seconds, modules
//   capture <dir>            oracle (d): PrintWindow images of every taskbar, with checks
//   oracle-check             the oracles against each other on the live taskbars
//   jumplist-probe           what the jump list detection sees now (shell hosts, foreground, UIA root)
//   monitors                 the monitors as JSON, with the DXGI output that shows each (for rec\record.ps1)
//   demo-layout [x]          the README demo's layout on the primary monitor (monitor geometry only)
//   testapp-hidden <exe> <dir>   exercises the test app's command channel with hidden windows
//   deadman <exe> <dir>      the test app's dead-man paths (owner exit, deadman, idle, guardian)
//   sleep <seconds>          a short-lived process, used as a fake owner
// All of these are read-only towards Explorer and draw nothing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32;
using TsoLive;
using TsoShared;

namespace TsoLiveCli;

static class Program
{
    static int Main(string[] args)
    {
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_PER_MONITOR_AWARE_V2);
        if (args.Length == 0)
        {
            Console.WriteLine("verbs: selftest steps plan probe uia explorer capture oracle-check jumplist-probe monitors demo-layout testapp-hidden deadman sleep");
            return 2;
        }
        try
        {
            return args[0] switch
            {
                "selftest" => SelfTests.Run(),
                "steps" => Steps(),
                "plan" => Plan(args.Length > 1 ? args[1] : "all"),
                "probe" => ProbeDump(args.Length > 1 ? args[1] : null),
                "uia" => UiaDump(args.Length > 1 ? args[1] : null),
                "explorer" => ExplorerDump(args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 2),
                "capture" => CaptureDump(args[1]),
                "oracle-check" => OracleCheck(),
                "jumplist-probe" => JumpListSnapshot(),
                "monitors" => MonitorsDump(),
                "demo-layout" => DemoLayoutDump(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : (int?)null),
                "testapp-hidden" => TestAppHidden(args[1], args[2]),
                "deadman" => Deadman(args[1], args[2]),
                "sleep" => SleepFor(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)),
                _ => Usage(args[0]),
            };
        }
        catch (Exception e)
        {
            Console.WriteLine("ERROR " + e);
            return 3;
        }
    }

    static int Usage(string verb)
    {
        Console.WriteLine("unknown verb " + verb);
        return 2;
    }

    /// <summary>A read-only snapshot of the shell hosts' windows, the foreground and the UIA root.</summary>
    static int JumpListSnapshot()
    {
        var sw = Stopwatch.StartNew();
        var s = JumpListProbe.Take(0, withUia: true);
        Console.WriteLine($"snapshot in {sw.ElapsedMilliseconds} ms: {JumpListProbe.Describe(s)}");
        foreach (var w in s.Windows.Where(w => !w.Process.Equals("explorer", StringComparison.OrdinalIgnoreCase) || w.Visible))
        {
            Console.WriteLine("  " + w);
        }
        Console.WriteLine("JumpViewUI.dll: " + JumpListProbe.JumpViewLoaded());
        var again = JumpListProbe.Take(1, withUia: true);
        Console.WriteLine("compared with a second snapshot (nothing opened): " + JumpListProbe.Compare(s, again));
        return 0;
    }

    static int SleepFor(double s)
    {
        Thread.Sleep(TimeSpan.FromSeconds(s));
        return 0;
    }

    static int Steps()
    {
        foreach (string line in LiveRunner.Describe())
        {
            Console.WriteLine(line);
        }
        return 0;
    }

    /// <summary>plan [steps]: the steps a run would take, in order, with estimates.</summary>
    static int Plan(string steps)
    {
        foreach (string line in LiveRunner.Plan(steps))
        {
            Console.WriteLine(line);
        }
        return 0;
    }

    static JsonObject Box(RECT r) => new() { ["x"] = r.Left, ["y"] = r.Top, ["w"] = r.Width, ["h"] = r.Height };

    static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>monitors: every monitor in screen pixels, with the DXGI adapter and output that show it.</summary>
    static int MonitorsDump()
    {
        var all = Monitors.All();
        Monitors.FindDxgiOutputs(all);
        var list = new JsonArray(all.Select(m => (JsonNode)new JsonObject
        {
            ["index"] = m.Index, ["device"] = m.Device, ["primary"] = m.Primary, ["bounds"] = Box(m.Bounds), ["work"] = Box(m.Work),
            ["dxgiAdapter"] = m.DxgiAdapter, ["dxgiOutput"] = m.DxgiOutput,
        }).ToArray());
        Console.WriteLine(list.ToJsonString(Indented));
        return 0;
    }

    /// <summary>
    /// demo-layout [button-centre-x]: the README demo's layout on the primary monitor (monitor
    /// geometry only, nothing else is read), for a test button centred at x (default: the taskbar's
    /// middle), as JSON in screen pixels.
    /// </summary>
    static int DemoLayoutDump(int? buttonX)
    {
        var p = Monitors.All().First(m => m.Primary);
        int cx = buttonX ?? (p.Work.Left + p.Work.Right) / 2;
        var button = new RECT(cx - 22, p.Work.Bottom, cx + 22, p.Bounds.Bottom);
        var L = DemoLayout.Compute(p.Bounds, p.Work, button);
        var o = new JsonObject
        {
            ["monitor"] = Box(p.Bounds), ["work"] = Box(p.Work), ["device"] = p.Device, ["button"] = Box(button),
            ["crop"] = Box(L.Crop), ["flyoutZone"] = Box(L.FlyoutZone), ["rest"] = new JsonObject { ["x"] = L.Rest.X, ["y"] = L.Rest.Y },
            ["slots"] = new JsonArray(L.Slots.Select(s => (JsonNode)Box(s)).ToArray()),
        };
        Console.WriteLine(o.ToJsonString(Indented));
        return 0;
    }

    static void Write(string outPath, string text)
    {
        if (outPath == null)
        {
            Console.Write(text);
        }
        else
        {
            File.WriteAllText(outPath, text);
            Console.WriteLine($"wrote {outPath} ({text.Length} chars)");
        }
    }

    static string Exe(IntPtr h)
    {
        try
        {
            using var p = Process.GetProcessById((int)Win32.PidOf(h));
            return p.ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    // ------------------------------------------------------------------ oracle (a)

    static int ProbeDump(string outPath)
    {
        using var probe = new Probe();
        var sb = new StringBuilder();
        sb.AppendLine(probe.Describe());
        var sw = Stopwatch.StartNew();
        var snap = probe.Read();
        double first = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        for (int i = 0; i < 20; i++)
        {
            probe.Read();
        }
        sb.AppendLine($"read: {snap.Taskbars.Count} taskbars, {snap.Groups.Count} groups; first read {first:0.0} ms, then {sw.Elapsed.TotalMilliseconds / 20:0.00} ms per read");
        foreach (var w in snap.Warnings)
        {
            sb.AppendLine("warning: " + w);
        }
        foreach (var tb in snap.Taskbars)
        {
            sb.AppendLine($"== {tb.TrayClass} 0x{tb.Tray.ToInt64():X} on {tb.Monitor} {tb.TrayRect}; MSTaskListWClass 0x{tb.TaskList.ToInt64():X}; ITaskListUI at +0x{tb.UiOffset:X}; {tb.ButtonGroups.Count} button groups");
            foreach (var bg in tb.ButtonGroups)
            {
                var g = snap.Groups[bg.GroupPtr];
                string exe = g.Hwnds.FirstOrDefault(h => h != IntPtr.Zero) is var h0 && h0 != IntPtr.Zero ? Exe(h0) : "(no window)";
                var idx = bg.Items.Select(it => g.Items.IndexOf(it)).ToList();
                bool inOrder = idx.Zip(idx.Skip(1), (a, b) => a < b).All(x => x);
                sb.AppendLine($"   #{bg.Index,2} {exe,-24} L1 items={g.Items.Count,3} L2 buttons={bg.Items.Count,3} L2-in-L1-order={inOrder} L1 index of each button: {string.Join(",", idx)}");
            }
        }
        Write(outPath, sb.ToString());
        return 0;
    }

    // ------------------------------------------------------------------ oracle (b)

    static int UiaDump(string outPath)
    {
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var buttons = Uia.TaskbarButtons();
        sb.AppendLine($"{buttons.Count} task buttons in {sw.ElapsedMilliseconds} ms");
        foreach (var g in buttons.GroupBy(b => b.Tray))
        {
            sb.AppendLine($"== {g.First().TrayClass} 0x{g.Key.ToInt64():X}");
            foreach (var b in g)
            {
                sb.AppendLine($"   {b} running={b.RunningWindows} appid={b.AppId} window=0x{b.Window.ToInt64():X}");
            }
        }
        var shell = ExplorerInfo.Shell();
        sw.Restart();
        var fly = Uia.Flyout(shell.Pid, "TSO-TEST");
        sb.AppendLine($"flyout: open={fly.Open} hosts={fly.Hosts.Count} elements={fly.All.Count} thumbnails={fly.Thumbnails.Count} toolbar buttons={fly.ThumbBarButtons.Count} in {sw.ElapsedMilliseconds} ms");
        foreach (var h in Uia.PopupHosts(shell.Pid))
        {
            sb.AppendLine($"   visible popup host 0x{h.hwnd.ToInt64():X} {h.cls}");
        }
        Write(outPath, sb.ToString());
        return 0;
    }

    // ------------------------------------------------------------------ oracle (c)

    static int ExplorerDump(double seconds)
    {
        var s = ExplorerInfo.Shell();
        Console.WriteLine("shell " + s);
        var a = ExplorerInfo.Cpu(s);
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        var b = ExplorerInfo.Cpu(s);
        var r = ExplorerInfo.Rate(a, b);
        var rt = ExplorerInfo.RateFromTimes(a, b);
        Console.WriteLine($"cpu over {r.seconds:0.00}s (cycles at {ExplorerInfo.CyclesPerMs() / 1000:0} MHz): process {r.process:0.00} ms/s, taskbar thread {r.thread:0.00} ms/s (thread {s.TrayThread}); from times: {rt.process:0.00} / {rt.thread:0.00} ms/s");
        Console.WriteLine("taskbar thread GUI state: " + ExplorerInfo.GuiThreadState(s.TrayThread));
        foreach (var m in ExplorerInfo.Modules(s.Pid, n => n.StartsWith("windhawk", StringComparison.OrdinalIgnoreCase) || n.Contains("taskbar", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("module " + m);
        }
        var again = ExplorerInfo.Shell();
        Console.WriteLine($"same process after {seconds}s: {again.SameProcess(s)}; hung: {again.Hung}");
        return 0;
    }

    // ------------------------------------------------------------------ oracle (d)

    static int CaptureDump(string dir)
    {
        var shots = Capture.SaveTrays(dir, "capture");
        int bad = 0;
        foreach (var (tray, rect, path) in shots)
        {
            using var bmp = new System.Drawing.Bitmap(path);
            bool blank = Capture.IsBlank(bmp);
            bad += blank ? 1 : 0;
            Console.WriteLine($"{path} {rect} {(blank ? "BLANK" : "has content")}");
        }
        // Every overlay badge on the taskbars now, read back from the image (a real-world check of
        // the badge sampling used for the test windows).
        foreach (var b in Uia.TaskbarButtons().Where(b => b.Overlay != null))
        {
            var shot = shots.First(s => s.tray == b.Tray);
            using var bmp = new System.Drawing.Bitmap(shot.path);
            var r = new System.Drawing.Rectangle((int)b.Overlay.Rect.X - shot.rect.Left, (int)b.Overlay.Rect.Y - shot.rect.Top, (int)b.Overlay.Rect.Width, (int)b.Overlay.Rect.Height);
            var px = Capture.Stats(bmp, r, 0x000000, 765);
            var btn = new System.Drawing.Rectangle((int)b.Rect.X - shot.rect.Left, (int)b.Rect.Y - shot.rect.Top, (int)b.Rect.Width, (int)b.Rect.Height);
            Console.WriteLine($"overlay on '{b.Name}' at {b.Overlay.Rect}: {px.Count} pixels sampled; progress strip: {Capture.Progress(bmp, btn)}");
        }
        Console.WriteLine($"captures: {shots.Count}, blank: {bad}");
        return bad == 0 && shots.Count > 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ oracles against each other

    static int OracleCheck()
    {
        using var probe = new Probe();
        ProbeSnapshot snap;
        try
        {
            Console.WriteLine(probe.Describe());
            snap = probe.Read();
        }
        catch (ProbeException e)
        {
            Console.WriteLine("oracle-check: Explorer's lists can't be read: " + e.Message);
            return 1;
        }
        var buttons = Uia.TaskbarButtons();
        int problems = 0;
        foreach (var tb in snap.Taskbars)
        {
            var ui = buttons.Where(b => b.Tray == tb.Tray).OrderBy(b => b.Rect.X).ToList();
            var groups = tb.ButtonGroups;
            Console.WriteLine($"{tb.TrayClass} 0x{tb.Tray.ToInt64():X} {tb.Monitor}: probe {groups.Count} button groups, UIA {ui.Count} task buttons");
            if (groups.Count != ui.Count)
            {
                Console.WriteLine("   MISMATCH in count");
                problems++;
                continue;
            }
            for (int i = 0; i < groups.Count; i++)
            {
                var bg = groups[i];
                int windows = bg.Hwnds.Count(h => h != IntPtr.Zero);
                int uiWindows = ui[i].RunningWindows;
                string exe = bg.Hwnds.FirstOrDefault(h => h != IntPtr.Zero) is var h0 && h0 != IntPtr.Zero ? Exe(h0) : "(pinned)";
                bool ok = windows == uiWindows;
                problems += ok ? 0 : 1;
                Console.WriteLine($"   #{i,2} probe {exe,-22} {windows,3} window buttons | UIA '{Logic.OneLine(ui[i].Name)}' {(ok ? "ok" : "MISMATCH")}");
            }
        }
        // L2 is an ordered subsequence of L1 for every group (the model the mod relies on).
        foreach (var tb in snap.Taskbars)
        {
            foreach (var bg in tb.ButtonGroups)
            {
                var g = snap.Groups[bg.GroupPtr];
                var idx = bg.Items.Select(it => g.Items.IndexOf(it)).ToList();
                if (idx.Any(x => x < 0) || !idx.Zip(idx.Skip(1), (a, b) => a < b).All(x => x))
                {
                    Console.WriteLine($"   L2 not an ordered subsequence of L1 in group 0x{g.Ptr:X}");
                    problems++;
                }
            }
        }
        // The current order of every group against the mod's rule (informative before the mod runs).
        int sorted = 0, unsorted = 0, skipped = 0;
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
            if (Logic.ExpectedOrder(geoms, true).SequenceEqual(geoms.Select(x => x.Key)))
            {
                sorted++;
            }
            else
            {
                unsorted++;
                Console.WriteLine($"   not in on-screen order yet: {Exe(g.Hwnds[0])} ({g.Items.Count} windows)");
            }
        }
        Console.WriteLine($"groups with 2+ windows: {sorted} already in the mod's order, {unsorted} not, {skipped} skipped");
        Console.WriteLine($"oracle-check: {problems} problems");
        return problems == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ the test app, hidden

    sealed class VisibilityGuard : IDisposable
    {
        readonly Thread _t;
        volatile bool _stop;
        public volatile int Violations;
        public int Polls;
        public readonly List<string> Seen = new();

        public VisibilityGuard(Func<int> pid)
        {
            _t = new Thread(() =>
            {
                while (!_stop)
                {
                    int p = pid();
                    if (p != 0)
                    {
                        Win32.EnumWindows((h, l) =>
                        {
                            if (Win32.PidOf(h) == p && Win32.IsWindowVisible(h))
                            {
                                Violations++;
                                lock (Seen)
                                {
                                    Seen.Add($"0x{h.ToInt64():X} '{Win32.TitleOf(h)}' {Win32.ClassOf(h)}");
                                }
                            }
                            return true;
                        }, IntPtr.Zero);
                    }
                    Polls++;
                    Thread.Sleep(2);
                }
            })
            { IsBackground = true };
            _t.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _t.Join(1000);
        }
    }

    static int _fails, _oks;

    static void Expect(bool cond, string what, JsonObject r = null)
    {
        if (cond)
        {
            _oks++;
            Console.WriteLine($"  ok   {what}");
        }
        else
        {
            _fails++;
            Console.WriteLine($"  FAIL {what} {r?.ToJsonString()}");
        }
    }

    static string NewJournal(string dir, string name)
    {
        string p = Path.Combine(dir, name);
        Journal.Save(p, new JournalData { RunId = name, ProgressLog = Path.Combine(dir, name + ".progress.log") });
        return p;
    }

    static int TestAppHidden(string exe, string dir)
    {
        Directory.CreateDirectory(dir);
        int pid = 0;
        using var guard = new VisibilityGuard(() => pid);
        var sw = Stopwatch.StartNew();
        using var app = TestAppClient.Start(exe, "tso-selftest-" + Environment.ProcessId, Environment.ProcessId,
            NewJournal(dir, "hidden-journal.json"), Path.Combine(dir, "testapp-hidden.log"), hidden: true, deadmanSec: 120, idleSec: 60,
            aumid: SelfTestAumid, prefix: "TSO-TEST");
        pid = app.Process.Id;
        Console.WriteLine($"test app pid {pid}, pipe up after {sw.ElapsedMilliseconds} ms");

        var info = app.Send("info");
        Expect(info["hidden"]!.GetValue<bool>() && info["taskbarInitHr"]!.ToString() == "0x00000000", "info: hidden, ITaskbarList3 HrInit ok", info);
        var t = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            app.Send("ping");
        }
        Console.WriteLine($"  50 pings in {t.ElapsedMilliseconds} ms");

        // Hidden windows can be anywhere, also where no monitor is.
        var opened = new Dictionary<string, RECT>
        {
            ["A"] = new(1400, 200, 1400 + 640, 200 + 420),
            ["B"] = new(700, 200, 700 + 640, 200 + 420),
            ["C"] = new(-5000, 300, -5000 + 640, 300 + 420),
            ["D"] = new(9000, 300, 9000 + 640, 300 + 420),
        };
        foreach (var kv in opened)
        {
            var r = app.Cmd("open", kv.Key, kv.Value.Left, kv.Value.Top, kv.Value.Width, kv.Value.Height, new Command.Kv("color", "C81E1E"));
            Expect(r["dry"]?.GetValue<bool>() == true && r["window"]?["visible"]?.GetValue<bool>() == false, $"open {kv.Key} hidden", r);
        }
        var z = app.Cmd("open", "Z", 1500, 700, 400, 300, new Command.Kv("aumid", SelfTestAumid + ".Sentinel"));
        Expect(z["aumidRead"] != null, $"open Z with an AUMID: read back '{z["aumidRead"]}' (not written in hidden mode)", z);
        var dup = app.TrySend("open A 0 0 640 420");
        Expect(dup["ok"]!.GetValue<bool>() == false, "open an open key: refused", dup);

        var list = app.Send("list")["windows"]!.AsArray();
        Expect(list.Count == 5, $"list: {list.Count} windows");
        foreach (var w in list)
        {
            string k = w["key"]!.ToString();
            var win = w["window"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
            if (opened.TryGetValue(k, out RECT want))
            {
                // Hidden windows have no DWM frame; the window rect is the frame plus the default borders.
                bool placed = Math.Abs(win[0] + 7 - want.Left) <= 1 && Math.Abs(win[1] - want.Top) <= 1 && Math.Abs(win[2] - 7 - want.Right) <= 1;
                Expect(placed && w["visible"]!.GetValue<bool>() == false, $"{k} placed at {want} (window rect {string.Join(",", win)}), not visible", w.AsObject());
            }
        }
        IntPtr ha = new IntPtr(Convert.ToInt64(list[0]!["hwnd"]!.ToString().Substring(2), 16));
        Expect(Win32.IsWindow(ha) && !Win32.IsWindowVisible(ha) && Win32.TitleOf(ha) == "TSO-TEST A", "A is a real hidden top-level window with the prefix title");
        var geom = Logic.Geometry("A", ha);
        Console.WriteLine($"  geometry of hidden A by the mod's rule: {geom}");

        var mv = app.Cmd("move", "A", 300, 200, 640, 420);
        Expect(mv["window"]!["window"]!.AsArray()[0]!.GetValue<int>() == 293, "move A (hidden window moved, still hidden)", mv);
        var sn = app.Cmd("snap", "B", "left");
        Expect(sn["target"] != null, $"snap B left (programmatic) -> {sn["target"]?.ToJsonString()}", sn);
        foreach (string verb in new[] { "min", "unmin", "restore", "max", "focus", "raise" })
        {
            var r = app.Cmd(verb, "C");
            Expect(r["dry"]?.GetValue<bool>() == true, $"{verb} C: dry in hidden mode", r);
        }
        var au = app.Cmd("aumid", "C", SelfTestAumid + ".Alt");
        Expect(au["dry"]?.GetValue<bool>() == true, $"aumid C: dry, current '{au["current"]}'", au);
        foreach (string line in new[] { "progress A normal 25", "progress B error 50", "overlay A FF00FF", "overlay B none", "thumbbar A 2", "reapply all" })
        {
            var r = app.Send(line);
            Expect(r["dry"]?.GetValue<bool>() == true || r["results"] != null, $"{line}: dry", r);
        }
        var tb2 = app.TrySend("thumbbar A 2");
        Expect(tb2["ok"]!.GetValue<bool>() == false, "thumbbar twice: refused", tb2);
        var vg = app.Cmd("vdget", "A");
        Console.WriteLine($"  vdget A (read-only): {vg.ToJsonString()}");
        var (desktops, current) = Monitors.VirtualDesktops();
        if (desktops.Count > 1)
        {
            var vm = app.Cmd("vdmove", "A", desktops.First(d => d != current).ToString());
            Expect(vm["dry"]?.GetValue<bool>() == true, "vdmove A: dry", vm);
        }
        var grab = app.Cmd("grab", "A");
        Expect(grab["y"]!.GetValue<int>() > 200 && grab["y"]!.GetValue<int>() < 200 + 30, $"grab A: caption point ({grab["x"]},{grab["y"]})", grab);
        Expect(app.Cmd("title", "A", "TSO-TEST A renamed")["ok"]!.GetValue<bool>(), "title A");
        foreach (string bad in new[] { "title A Notepad", "nosuchverb", "close Q", "move A 1 2", "open AA 0 0 640 420", "move A 0 0 10 10", "progress A sideways 5" })
        {
            var r = app.TrySend(bad);
            Expect(r["ok"]!.GetValue<bool>() == false, $"refused: '{bad}' -> {r["error"]}", r);
        }
        Expect(app.Cmd("close", "B")["ok"]!.GetValue<bool>(), "close B");
        Expect(app.Send("list")["windows"]!.AsArray().Count == 4, "list after close: 4");
        bool exited = app.Quit(5000);
        Expect(exited, "quit: process exited");
        Thread.Sleep(50);
        Expect(!Win32.IsWindow(ha), "A destroyed");
        Expect(ExplorerInfo.WindowsWithPrefix("TSO-TEST").Count == 0, "no TSO-TEST windows left");
        guard.Dispose();
        Expect(guard.Violations == 0, $"visibility guard: {guard.Polls} polls, {guard.Violations} visible windows {string.Join(", ", guard.Seen.Distinct())}");
        Console.WriteLine($"testapp-hidden: {_oks} ok, {_fails} failed");
        return _fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ dead-man paths

    const string SelfTestAumid = "TaskbarSpatialOrder.LiveTest.SelfTest";

    static Process StartOwner(double seconds)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("sleep");
        psi.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(psi)!;
    }

    static int Deadman(string exe, string dir)
    {
        Directory.CreateDirectory(dir);
        int pid = 0;
        using var guard = new VisibilityGuard(() => pid);

        // 1. The owner (the runner) dies: windows closed within a second or two.
        using (var owner = StartOwner(1.5))
        {
            var app = TestAppClient.Start(exe, "tso-selftest-owner-" + Environment.ProcessId, owner.Id, NewJournal(dir, "owner-journal.json"),
                Path.Combine(dir, "deadman-owner.log"), true, 120, 60, SelfTestAumid, "TSO-TEST");
            pid = app.Process.Id;
            app.Send("open A 100 100 640 420");
            app.Send("open B 900 100 640 420");
            var sw = Stopwatch.StartNew();
            owner.WaitForExit();
            bool gone = app.Process.WaitForExit(5000);
            Expect(gone, $"owner exited: test app gone {sw.ElapsedMilliseconds} ms after the owner started");
            Expect(File.ReadAllText(Path.Combine(dir, "deadman-owner.log")).Contains("shutdown owner-exited"), "logged: shutdown owner-exited");
            app.Kill();
        }

        // 2. The absolute dead-man.
        {
            var sw = Stopwatch.StartNew();
            var app = TestAppClient.Start(exe, "tso-selftest-deadman-" + Environment.ProcessId, Environment.ProcessId, NewJournal(dir, "deadman-journal.json"),
                Path.Combine(dir, "deadman-deadman.log"), true, 3, 60, SelfTestAumid, "TSO-TEST");
            pid = app.Process.Id;
            app.StartPinger(500);
            app.Send("open A 100 100 640 420");
            bool gone = app.Process.WaitForExit(8000);
            Expect(gone && sw.Elapsed.TotalSeconds >= 2.9, $"deadman 3 s: exited after {sw.Elapsed.TotalSeconds:0.0} s despite pings");
            app.Kill();
        }

        // 3. Idle: no commands for idle seconds.
        {
            var sw = Stopwatch.StartNew();
            var app = TestAppClient.Start(exe, "tso-selftest-idle-" + Environment.ProcessId, Environment.ProcessId, NewJournal(dir, "idle-journal.json"),
                Path.Combine(dir, "deadman-idle.log"), true, 120, 2, SelfTestAumid, "TSO-TEST");
            pid = app.Process.Id;
            app.Send("open A 100 100 640 420");
            bool gone = app.Process.WaitForExit(8000);
            Expect(gone && sw.Elapsed.TotalSeconds >= 1.9, $"idle 2 s: exited after {sw.Elapsed.TotalSeconds:0.0} s");
            app.Kill();
        }

        // 4. The guardian: the runner dies after changing the grouping. A scratch registry key stands
        //    in for Explorer\Advanced; the journal points the guardian at it.
        string scratch = @"Software\TsoLiveTestScratch-" + Guid.NewGuid().ToString("N");
        try
        {
            RegistryValues.WriteDword(Registry.CurrentUser, scratch, "TaskbarGlomLevel", 0);
            var original = RegistryValues.ReadAll(Registry.CurrentUser, scratch, RegistryValues.GlomNames);
            RegistryValues.WriteDword(Registry.CurrentUser, scratch, "TaskbarGlomLevel", 2);
            RegistryValues.WriteDword(Registry.CurrentUser, scratch, "MMTaskbarGlomLevel", 2);
            string jp = Path.Combine(dir, "guardian-journal.json");
            string progress = Path.Combine(dir, "guardian-progress.log");
            File.WriteAllLines(progress, new[] { "TOTAL eta=100", "STEP 1/1 s07-never-combine START eta=30" });
            Journal.Save(jp, new JournalData
            {
                RunId = "guardian",
                ProgressLog = progress,
                Glom = new GlomJournal { KeyPath = scratch, Original = original, Changed = true },
            });
            using var owner = StartOwner(1.0);
            var app = TestAppClient.Start(exe, "tso-selftest-guardian-" + Environment.ProcessId, owner.Id, jp,
                Path.Combine(dir, "deadman-guardian.log"), true, 120, 60, SelfTestAumid, "TSO-TEST");
            pid = app.Process.Id;
            app.Send("open A 100 100 640 420");
            owner.WaitForExit();
            app.Process.WaitForExit(5000);
            var now = RegistryValues.ReadAll(Registry.CurrentUser, scratch, RegistryValues.GlomNames);
            Expect(RegistryValues.Same(original[0], now[0]) && !now[1].Existed, $"guardian restored the scratch values: {string.Join(", ", now)}");
            var j = Journal.Load(jp);
            Expect(j.Glom.Restored && j.Glom.RestoredBy == "guardian:owner-exited", $"journal: restored by {j.Glom.RestoredBy}");
            Expect(File.ReadAllLines(progress).Last() == "DONE pass=0 fail=0 aborted=runner-died:owner-exited", "progress log closed with DONE ... aborted=runner-died");
            app.Kill();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(scratch, false);
        }

        guard.Dispose();
        Expect(guard.Violations == 0, $"visibility guard: {guard.Polls} polls, {guard.Violations} visible windows");
        Expect(Process.GetProcessesByName("TsoTestApp").Length == 0, "no TsoTestApp process left");
        Console.WriteLine($"deadman: {_oks} ok, {_fails} failed");
        return _fails == 0 ? 0 : 1;
    }
}
