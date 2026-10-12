// Watches, on background threads, everything the steps need over time:
//  - Explorer's lists of the test windows, polled every PollMs and kept as a timeline of changes;
//  - the shell process (a different pid or start time is a crash or restart; a hung taskbar
//    window for HangS seconds is a hang);
//  - WinEvents of the test windows, to record which action fires which events.
// Read-only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using TsoShared;

namespace TsoLive;

public sealed class TimelineEntry
{
    public double T;
    public string Kind;   // state, mark, shell, error
    public string Text;
    public TestView View;
}

public sealed class WinEventRecord
{
    public double T;
    public uint Event;
    public IntPtr Hwnd;
    public string Key;

    public static string Name(uint e) => e switch
    {
        0x0003 => "FOREGROUND",
        0x000A => "MOVESIZESTART",
        0x000B => "MOVESIZEEND",
        0x0016 => "MINIMIZESTART",
        0x0017 => "MINIMIZEEND",
        0x8002 => "SHOW",
        0x8003 => "HIDE",
        0x800B => "LOCATIONCHANGE",
        0x8017 => "CLOAKED",
        0x8018 => "UNCLOAKED",
        _ => "0x" + e.ToString("X4"),
    };
}

public sealed class Watcher : IDisposable
{
    readonly Stopwatch _clock;
    readonly Func<IReadOnlyDictionary<IntPtr, string>> _keys;
    readonly Action<string> _log;
    readonly List<TimelineEntry> _timeline = new();
    readonly List<WinEventRecord> _events = new();
    Thread _poll, _eventThread;
    volatile bool _stop;
    uint _eventThreadId;
    volatile int _testPid;
    volatile TestView _latest;
    string _lastSignature, _lastError;
    WinEventDelegate _callback;

    public volatile int PollMs = 15;
    /// <summary>Where the shell's identity comes from (replaceable for the self-tests).</summary>
    public Func<ShellInfo> ShellSource = ExplorerInfo.Shell;
    /// <summary>Whether to read Explorer's lists (off in the self-tests of the crash detection).</summary>
    public bool ProbeEnabled = true;
    public volatile bool ExpectRestart;
    public double HangS = 6;
    public ShellInfo Baseline { get; private set; }
    public volatile string Crash;
    public volatile string Hang;
    public volatile string LastProbeError;

    public Watcher(Stopwatch clock, Func<IReadOnlyDictionary<IntPtr, string>> keys, Action<string> log)
    {
        _clock = clock;
        _keys = keys;
        _log = log;
    }

    public double Now => _clock.Elapsed.TotalSeconds;

    public void Start()
    {
        Baseline = ShellSource();
        _poll = new Thread(PollLoop) { IsBackground = true, Name = "watcher poll" };
        _poll.Start();
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "watcher events" };
        _eventThread.Start();
    }

    public void Rebaseline()
    {
        Baseline = ShellSource();
        Crash = null;
        Hang = null;
        Add(new TimelineEntry { T = Now, Kind = "shell", Text = "baseline " + Baseline });
    }

    public void SetTestPid(int pid)
    {
        _testPid = pid;
        if (_eventThreadId != 0)
        {
            Native.PostThreadMessageW(_eventThreadId, 0x8000 + 5, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Mark(string label) => Add(new TimelineEntry { T = Now, Kind = "mark", Text = label });

    void Add(TimelineEntry e)
    {
        lock (_timeline)
        {
            _timeline.Add(e);
        }
    }

    /// <summary>The latest view; throws if the poller has not produced one recently.</summary>
    public TestView Latest(double maxAgeS = 1.5)
    {
        var v = _latest;
        if (v == null || Now - v.T > maxAgeS)
        {
            throw new InvalidOperationException($"no fresh Explorer snapshot ({LastProbeError ?? "watcher stalled"})");
        }
        return v;
    }

    public TestView LatestOrNull() => _latest;

    public List<TimelineEntry> Timeline(double from, double to)
    {
        lock (_timeline)
        {
            return _timeline.Where(e => e.T >= from && e.T <= to).ToList();
        }
    }

    /// <summary>The last state entry at or before t (the state in force at t).</summary>
    public TestView StateAt(double t)
    {
        lock (_timeline)
        {
            return _timeline.LastOrDefault(e => e.Kind == "state" && e.T <= t)?.View;
        }
    }

    public List<WinEventRecord> Events(double from, double to)
    {
        lock (_events)
        {
            return _events.Where(e => e.T >= from && e.T <= to).ToList();
        }
    }

    void PollLoop()
    {
        Win32.SetThreadDpiAwarenessContext(Win32.DPI_PER_MONITOR_AWARE_V2);
        using var probe = new Probe();
        double lastShell = -1, hungSince = -1;
        while (!_stop)
        {
            double t = Now;
            if (t - lastShell >= 0.2)
            {
                lastShell = t;
                ShellInfo s;
                try
                {
                    s = ShellSource();
                }
                catch (Exception e)
                {
                    LastProbeError = "shell: " + e.Message;
                    Thread.Sleep(50);
                    continue;
                }
                if (!s.SameProcess(Baseline))
                {
                    if (ExpectRestart)
                    {
                        // The runner rebaselines once the new shell is up.
                    }
                    else if (Crash == null)
                    {
                        Crash = $"shell changed: was {Baseline}, now {s}";
                        Add(new TimelineEntry { T = t, Kind = "shell", Text = Crash });
                        _log("WATCHER " + Crash);
                    }
                }
                if (s.Pid != 0 && s.Hung)
                {
                    if (hungSince < 0)
                    {
                        hungSince = t;
                    }
                    else if (t - hungSince >= HangS && Hang == null && !ExpectRestart)
                    {
                        Hang = $"taskbar window not responding for {t - hungSince:0.0}s ({s})";
                        Add(new TimelineEntry { T = t, Kind = "shell", Text = Hang });
                        _log("WATCHER " + Hang);
                    }
                }
                else
                {
                    hungSince = -1;
                }
            }

            if (!ProbeEnabled)
            {
                Thread.Sleep(Math.Max(1, PollMs));
                continue;
            }
            try
            {
                var snap = probe.Read();
                var view = TestView.From(snap, _keys(), Now);
                if (view.Signature != _lastSignature)
                {
                    _lastSignature = view.Signature;
                    Add(new TimelineEntry { T = view.T, Kind = "state", Text = view.Signature, View = view });
                }
                _latest = view;
                LastProbeError = null;
                _lastError = null;
            }
            catch (Exception e)
            {
                LastProbeError = e.Message;
                if (e.Message != _lastError)
                {
                    _lastError = e.Message;
                    Add(new TimelineEntry { T = Now, Kind = "error", Text = e.Message });
                    _log("WATCHER probe: " + e.Message);
                }
            }

            int ms = PollMs;
            Thread.Sleep(ms < 1 ? 1 : ms);
        }
    }

    void EventLoop()
    {
        _eventThreadId = Native.GetCurrentThreadId();
        _callback = OnEvent;
        var hooks = new List<IntPtr>
        {
            Native.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS),
            Native.SetWinEventHook(0x000A, 0x000B, IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS),
            Native.SetWinEventHook(0x0016, 0x0017, IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS),
            Native.SetWinEventHook(0x8017, 0x8018, IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS),
        };
        IntPtr location = IntPtr.Zero;
        void HookLocation()
        {
            if (location != IntPtr.Zero)
            {
                Native.UnhookWinEvent(location);
                location = IntPtr.Zero;
            }
            int pid = _testPid;
            if (pid != 0)
            {
                // Window-location events of the test app's process only (show/hide/location).
                location = Native.SetWinEventHook(0x8002, 0x800B, IntPtr.Zero, _callback, (uint)pid, 0, Native.WINEVENT_OUTOFCONTEXT);
            }
        }
        HookLocation();

        while (Native.GetMessageW(out MSG m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.message == 0x8000 + 5)
            {
                HookLocation();
                continue;
            }
            Native.DispatchMessageW(ref m);
        }
        foreach (var h in hooks.Append(location))
        {
            if (h != IntPtr.Zero)
            {
                Native.UnhookWinEvent(h);
            }
        }
    }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != 0 || idChild != 0)
        {
            return;
        }
        if (ev >= 0x8002 && ev <= 0x800B && ev != 0x8002 && ev != 0x8003 && ev != 0x800B)
        {
            return;
        }
        try
        {
            // An exception escaping a WinEvent callback would end the process.
            if (!_keys().TryGetValue(hwnd, out string key))
            {
                return;
            }
            lock (_events)
            {
                _events.Add(new WinEventRecord { T = Now, Event = ev, Hwnd = hwnd, Key = key });
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_eventThreadId != 0)
        {
            Native.PostThreadMessageW(_eventThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _poll?.Join(2000);
        _eventThread?.Join(2000);
    }
}
