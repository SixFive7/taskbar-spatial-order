// TsoTestApp: test windows for the live test of taskbar-spatial-order.
//
// Opens top-level windows that form a taskbar group of their own (their own AppUserModelID), so
// no other app is involved. Controlled over a named pipe, one command per line (see
// Shared/Protocol.cs and the verbs in Dispatch below). It only ever touches windows it created.
//
// Safety:
//   --owner-pid  the runner's pid: when that process ends, every window is closed (dead-man).
//   --deadman-sec  absolute lifetime; --idle-sec  closes everything after that long without a command.
//   --journal  on a dead-man exit, puts the taskbar grouping values back if the runner changed them
//              and did not restore them (Shared/Journal.cs), and marks the progress log DONE.
//   --hidden   windows are created hidden and never shown; every command that would show,
//              activate or change taskbar state is answered with "dry" instead of performed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using TsoShared;
using static TsoTestApp.Native;

namespace TsoTestApp;

sealed class TestWindow
{
    public string Key, Title, Aumid;
    public IntPtr Hwnd, Brush, IconBig, IconSmall;
    public int Rgb;
    public int ButtonCreated;
    public long FirstButtonMs = -1, LastButtonMs = -1;
    public int? ProgressState;
    public int ProgressValue;
    public int ProgressHr = int.MinValue;
    public int? OverlayRgb;
    public string OverlayDesc;
    public bool OverlaySet;
    public IntPtr OverlayIcon;
    public int OverlayHr = int.MinValue;
    public int ThumbButtons;
    public IntPtr ThumbIcon;
    public int ThumbHr = int.MinValue;
    public int ThumbClicks;
}

sealed class CommandBox
{
    public string Line;
    public string Response;
    public readonly ManualResetEventSlim Done = new(false);
}

static class App
{
    const uint WM_APP_COMMAND = WM_APP + 1, WM_APP_SHUTDOWN = WM_APP + 2;
    const string WindowClass = "TsoLiveTestWindow", MessageClass = "TsoLiveTestMessages";
    const int ThumbRgb = 0xFF0080;

    static string PipeName, Aumid = "TaskbarSpatialOrder.LiveTest", Prefix = "TSO-TEST", JournalPath, LogPath;
    static int OwnerPid, DeadmanSec = 1800, IdleSec = 300;
    static bool Hidden;

    static IntPtr Instance, MsgWnd;
    static uint MsgTaskbarButtonCreated, MsgTaskbarCreated;
    static int TaskbarCreatedCount;
    static readonly SortedDictionary<string, TestWindow> Windows = new(StringComparer.Ordinal);
    static readonly Dictionary<IntPtr, TestWindow> ByHwnd = new();
    static ITaskbarList3 Taskbar;
    static int TaskbarInitHr = int.MinValue;
    static IVirtualDesktopManager Vdm;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long LastCommandMs;
    static volatile bool ShuttingDown;
    static string ShutdownReason;
    static WndProc TestProc, MsgProc;
    static StreamWriter Log;
    static RECT Margins = new(7, 0, 7, 7);  // window rect minus visible frame; measured on first show
    static bool MarginsMeasured;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            ParseArgs(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("TsoTestApp: " + e.Message);
            return 2;
        }

        Win32.SetProcessDpiAwarenessContext(Win32.DPI_PER_MONITOR_AWARE_V2);
        if (LogPath != null)
        {
            Log = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        }
        Trace($"start pid={Environment.ProcessId} pipe={PipeName} owner={OwnerPid} deadman={DeadmanSec}s idle={IdleSec}s hidden={Hidden} aumid={Aumid}");

        int hr = SetCurrentProcessExplicitAppUserModelID(Aumid);
        Trace($"SetCurrentProcessExplicitAppUserModelID hr=0x{hr:X8}");

        Instance = GetModuleHandleW(null);
        MsgTaskbarButtonCreated = RegisterWindowMessageW("TaskbarButtonCreated");
        MsgTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        TestProc = TestWindowProc;
        MsgProc = MessageWindowProc;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(TestProc),
            hInstance = Instance,
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
            lpszClassName = WindowClass,
        };
        RegisterClassExW(ref wc);
        var mc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(MsgProc),
            hInstance = Instance,
            lpszClassName = MessageClass,
        };
        RegisterClassExW(ref mc);
        MsgWnd = CreateWindowExW(0, MessageClass, "TsoLiveTest messages", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, Instance, IntPtr.Zero);
        SetTimer(MsgWnd, new IntPtr(1), 500, IntPtr.Zero);

        try
        {
            Taskbar = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.CLSID_TaskbarList));
            TaskbarInitHr = Taskbar.HrInit();
        }
        catch (Exception e)
        {
            Trace("ITaskbarList3 unavailable: " + e.Message);
        }

        StartOwnerWatch();
        var pipeThread = new Thread(PipeLoop) { IsBackground = true, Name = "pipe" };
        pipeThread.Start();

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        Trace($"exit reason={ShutdownReason}");
        Log?.Dispose();
        return 0;
    }

    static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + a);
            switch (a)
            {
                case "--pipe": PipeName = Next(); break;
                case "--owner-pid": OwnerPid = int.Parse(Next()); break;
                case "--deadman-sec": DeadmanSec = int.Parse(Next()); break;
                case "--idle-sec": IdleSec = int.Parse(Next()); break;
                case "--aumid": Aumid = Next(); break;
                case "--prefix": Prefix = Next(); break;
                case "--journal": JournalPath = Next(); break;
                case "--log": LogPath = Next(); break;
                case "--hidden": Hidden = true; break;
                default: throw new ArgumentException("unknown argument " + a);
            }
        }
        if (string.IsNullOrEmpty(PipeName))
        {
            throw new ArgumentException("--pipe is required");
        }
    }

    static void Trace(string s)
    {
        try
        {
            Log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- dead-man

    static void StartOwnerWatch()
    {
        if (OwnerPid <= 0)
        {
            return;
        }
        var t = new Thread(() =>
        {
            try
            {
                using var p = Process.GetProcessById(OwnerPid);
                p.WaitForExit();
            }
            catch
            {
                // Already gone.
            }
            PostMessageW(MsgWnd, WM_APP_SHUTDOWN, new IntPtr(1), IntPtr.Zero);
        })
        { IsBackground = true, Name = "owner-watch" };
        t.Start();
    }

    static void Housekeeping()
    {
        long now = Clock.ElapsedMilliseconds;
        if (DeadmanSec > 0 && now > DeadmanSec * 1000L)
        {
            Shutdown("deadman", guardian: true);
        }
        else if (IdleSec > 0 && now - LastCommandMs > IdleSec * 1000L)
        {
            Shutdown("idle", guardian: true);
        }
    }

    static void Shutdown(string reason, bool guardian)
    {
        if (ShuttingDown)
        {
            return;
        }
        ShuttingDown = true;
        ShutdownReason = reason;
        Trace($"shutdown {reason}");
        foreach (var w in Windows.Values.ToList())
        {
            DestroyWindow(w.Hwnd);
        }
        if (guardian)
        {
            try
            {
                Trace("guardian: " + Journal.GuardianRestore(JournalPath, reason, broadcast: !Hidden));
            }
            catch (Exception e)
            {
                Trace("guardian failed: " + e.Message);
            }
        }
        PostQuitMessage(0);
    }

    // ---------------------------------------------------------------- pipe

    static void PipeLoop()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
        var utf8 = new UTF8Encoding(false);
        while (!ShuttingDown)
        {
            try
            {
                using var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.None, 65536, 65536, security);
                server.WaitForConnection();
                using var reader = new StreamReader(server, utf8, false);
                using var writer = new StreamWriter(server, utf8) { AutoFlush = true, NewLine = "\n" };
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Posted, not sent: a command run inside SendMessage is an input-synchronous
                    // call, in which COM calls to other processes (the virtual desktop manager in
                    // Explorer) fail with RPC_E_CANTCALLOUT_ININPUTSYNCCALL.
                    var box = new CommandBox { Line = line };
                    var handle = GCHandle.Alloc(box);
                    bool posted = PostMessageW(MsgWnd, WM_APP_COMMAND, IntPtr.Zero, GCHandle.ToIntPtr(handle));
                    bool done = posted && box.Done.Wait(TimeSpan.FromSeconds(30));
                    if (done || !posted)
                    {
                        handle.Free();  // otherwise the UI thread may still use it; leak it
                    }
                    writer.WriteLine(box.Response ?? (done ? null : "{\"ok\":false,\"error\":\"command did not finish within 30 s\"}")
                                     ?? "{\"ok\":false,\"error\":\"shutting down\"}");
                }
            }
            catch (Exception e)
            {
                Trace("pipe: " + e.Message);
                Thread.Sleep(50);
            }
        }
    }

    // ---------------------------------------------------------------- window procedures

    static IntPtr MessageWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case WM_APP_COMMAND:
                var box = (CommandBox)GCHandle.FromIntPtr(l).Target;
                try
                {
                    box.Response = ShuttingDown ? null : Dispatch(box.Line);
                }
                finally
                {
                    box.Done.Set();
                }
                return IntPtr.Zero;
            case WM_APP_SHUTDOWN:
                Shutdown(w == new IntPtr(1) ? "owner-exited" : "requested", guardian: w == new IntPtr(1));
                return IntPtr.Zero;
            case WM_TIMER:
                Housekeeping();
                return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, w, l);
    }

    static IntPtr TestWindowProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        ByHwnd.TryGetValue(h, out TestWindow w);
        if (w != null)
        {
            if (msg == MsgTaskbarButtonCreated && msg != 0)
            {
                w.ButtonCreated++;
                w.LastButtonMs = Clock.ElapsedMilliseconds;
                bool first = w.FirstButtonMs < 0;
                if (first)
                {
                    w.FirstButtonMs = w.LastButtonMs;
                }
                Trace($"TaskbarButtonCreated {w.Key} #{w.ButtonCreated}");
                // Only the first one applies the requested state: later ones are counted, never
                // answered, so a reorder that loses the state cannot be masked by the app.
                if (first)
                {
                    ApplyTaskbarState(w);
                }
                return IntPtr.Zero;
            }
            if (msg == MsgTaskbarCreated && msg != 0)
            {
                TaskbarCreatedCount++;
                Trace($"TaskbarCreated #{TaskbarCreatedCount} (seen by {w.Key})");
                return IntPtr.Zero;
            }
        }

        switch (msg)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_PAINT:
                Paint(h, w);
                return IntPtr.Zero;
            case WM_SYSCOMMAND when ((int)wParam.ToInt64() & 0xFFF0) == SC_KEYMENU:
                return IntPtr.Zero;  // no Alt menu mode
            case WM_COMMAND when w != null && (((int)wParam.ToInt64() >> 16) & 0xFFFF) == ComIds.THBN_CLICKED:
                w.ThumbClicks++;
                return IntPtr.Zero;
            case WM_DESTROY:
                if (w != null)
                {
                    Trace($"destroyed {w.Key}");
                    ByHwnd.Remove(h);
                    Windows.Remove(w.Key);
                    FreeWindow(w);
                }
                return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, wParam, lParam);
    }

    static void Paint(IntPtr h, TestWindow w)
    {
        IntPtr hdc = BeginPaint(h, out PAINTSTRUCT ps);
        GetClientRect(h, out RECT rc);
        if (w != null)
        {
            FillRect(hdc, ref rc, w.Brush);
            SetBkMode(hdc, 1);
            SetTextColor(hdc, 0xFFFFFF);
            IntPtr font = CreateFontW(-Math.Max(24, Math.Min(rc.Height, rc.Width) / 3), 0, 0, 0, 700, 0, 0, 0, 0, 0, 0, 5, 0, "Segoe UI");
            IntPtr old = SelectObject(hdc, font);
            DrawTextW(hdc, w.Key, -1, ref rc, 0x25);  // DT_CENTER | DT_VCENTER | DT_SINGLELINE
            SelectObject(hdc, old);
            DeleteObject(font);
        }
        EndPaint(h, ref ps);
    }

    static void FreeWindow(TestWindow w)
    {
        if (w.Brush != IntPtr.Zero) DeleteObject(w.Brush);
        if (w.IconBig != IntPtr.Zero) DestroyIcon(w.IconBig);
        if (w.IconSmall != IntPtr.Zero) DestroyIcon(w.IconSmall);
        if (w.OverlayIcon != IntPtr.Zero) DestroyIcon(w.OverlayIcon);
        if (w.ThumbIcon != IntPtr.Zero) DestroyIcon(w.ThumbIcon);
        w.Brush = w.IconBig = w.IconSmall = w.OverlayIcon = w.ThumbIcon = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- commands

    static string Dispatch(string line)
    {
        LastCommandMs = Clock.ElapsedMilliseconds;
        Command c;
        try
        {
            c = Command.Parse(line);
        }
        catch (Exception e)
        {
            return Error(e.Message);
        }

        try
        {
            JsonObject r = c.Verb switch
            {
                "ping" => Ok(),
                "info" => Info(),
                "list" => List(),
                "open" => Open(c),
                "close" => Close(c),
                "move" => Move(c),
                "snap" => Snap(c),
                "min" => Show(c, Win32.SW_SHOWMINNOACTIVE),
                "unmin" => Show(c, Win32.SW_SHOWNOACTIVATE),
                "restore" => Show(c, Win32.SW_RESTORE),
                "max" => Show(c, Win32.SW_MAXIMIZE),
                "focus" => Focus(c),
                "raise" => Raise(c),
                "aumid" => SetAumid(c),
                "progress" => Progress(c),
                "overlay" => Overlay(c),
                "thumbbar" => ThumbBar(c),
                "reapply" => Reapply(c),
                "vdmove" => VdMove(c),
                "vdget" => VdGet(c),
                "grab" => Grab(c),
                "title" => Title(c),
                "quit" => Quit(),
                _ => throw new FormatException("unknown verb " + c.Verb),
            };
            Trace($"{line} -> ok");
            return r.ToJsonString();
        }
        catch (Exception e)
        {
            Trace($"{line} -> error {e.Message}");
            return Error($"{c.Verb}: {e.Message}");
        }
    }

    static JsonObject Ok() => new() { ["ok"] = true };

    static JsonObject Dry(string what) => new() { ["ok"] = true, ["dry"] = true, ["skipped"] = what };

    static string Error(string message) => new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString();

    static TestWindow Get(Command c, int index = 0)
    {
        string key = c.Key(index);
        return Windows.TryGetValue(key, out var w) ? w : throw new InvalidOperationException($"no window {key}");
    }

    static string Hex(IntPtr h) => "0x" + h.ToInt64().ToString("X");

    static JsonArray Rect(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    static JsonObject Describe(TestWindow w)
    {
        Win32.FrameRect(w.Hwnd, out RECT frame);
        Win32.GetWindowRect(w.Hwnd, out RECT win);
        var wp = Win32.Placement(w.Hwnd);
        return new JsonObject
        {
            ["key"] = w.Key,
            ["hwnd"] = Hex(w.Hwnd),
            ["title"] = w.Title,
            ["visible"] = Win32.IsWindowVisible(w.Hwnd),
            ["iconic"] = Win32.IsIconic(w.Hwnd),
            ["zoomed"] = Win32.IsZoomed(w.Hwnd),
            ["cloaked"] = Win32.Cloaked(w.Hwnd),
            ["frame"] = Rect(frame),
            ["window"] = Rect(win),
            ["normal"] = Rect(wp.rcNormalPosition),
            ["showCmd"] = wp.showCmd,
            ["aumid"] = w.Aumid ?? Aumid,
            ["rgb"] = w.Rgb.ToString("X6"),
            ["buttonCreated"] = w.ButtonCreated,
            ["progress"] = w.ProgressState == null ? null : new JsonObject
            {
                ["state"] = w.ProgressState, ["value"] = w.ProgressValue, ["hr"] = HrText(w.ProgressHr),
            },
            ["overlay"] = w.OverlayRgb == null ? null : new JsonObject
            {
                ["rgb"] = w.OverlayRgb.Value.ToString("X6"), ["hr"] = HrText(w.OverlayHr),
            },
            ["thumbbar"] = w.ThumbButtons == 0 ? null : new JsonObject
            {
                ["buttons"] = w.ThumbButtons, ["hr"] = HrText(w.ThumbHr), ["clicks"] = w.ThumbClicks,
            },
        };
    }

    static string HrText(int hr) => hr == int.MinValue ? "pending" : "0x" + hr.ToString("X8");

    static JsonObject Info() => new()
    {
        ["ok"] = true,
        ["pid"] = Environment.ProcessId,
        ["aumid"] = Aumid,
        ["prefix"] = Prefix,
        ["hidden"] = Hidden,
        ["uptimeMs"] = Clock.ElapsedMilliseconds,
        ["deadmanSec"] = DeadmanSec,
        ["idleSec"] = IdleSec,
        ["taskbarCreated"] = TaskbarCreatedCount,
        ["taskbarInitHr"] = HrText(TaskbarInitHr),
        ["margins"] = Rect(Margins),
        ["windows"] = Windows.Count,
    };

    static JsonObject List()
    {
        var arr = new JsonArray();
        foreach (var w in Windows.Values)
        {
            arr.Add(Describe(w));
        }
        return new JsonObject { ["ok"] = true, ["windows"] = arr };
    }

    static RECT ReadFrame(Command c, int first)
    {
        int x = c.ArgInt(first, "x"), y = c.ArgInt(first + 1, "y"), w = c.ArgInt(first + 2, "w"), h = c.ArgInt(first + 3, "h");
        if (w < 100 || h < 80 || w > 8000 || h > 8000)
        {
            throw new FormatException($"implausible size {w}x{h}");
        }
        return new RECT(x, y, x + w, y + h);
    }

    static JsonObject Open(Command c)
    {
        string key = c.Key();
        if (Windows.ContainsKey(key))
        {
            throw new InvalidOperationException($"window {key} is already open");
        }
        RECT frame = ReadFrame(c, 1);
        var w = new TestWindow
        {
            Key = key,
            Title = c.Opt("title", $"{Prefix} {key}"),
            Aumid = c.Opt("aumid"),
            Rgb = Command.ParseRgb(c.Opt("color", "808080")),
        };
        if (!w.Title.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new FormatException($"title must start with {Prefix}");
        }
        w.Brush = CreateSolidBrush(Bgr(w.Rgb));
        w.IconBig = SolidIcon(32, w.Rgb, inner: true);
        w.IconSmall = SolidIcon(16, w.Rgb, inner: true);

        RECT win = ToWindowRect(frame);
        IntPtr h = CreateWindowExW(0, WindowClass, w.Title, WS_OVERLAPPEDWINDOW, win.Left, win.Top, win.Width, win.Height,
            IntPtr.Zero, IntPtr.Zero, Instance, IntPtr.Zero);
        if (h == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }
        w.Hwnd = h;
        Windows[key] = w;
        ByHwnd[h] = w;
        SendMessageW(h, WM_SETICON, new IntPtr(1), w.IconBig);
        SendMessageW(h, WM_SETICON, IntPtr.Zero, w.IconSmall);
        // When the runner (and so this app) is elevated, Explorer's messages need letting through.
        if (MsgTaskbarButtonCreated != 0)
        {
            ChangeWindowMessageFilterEx(h, MsgTaskbarButtonCreated, MSGFLT_ALLOW, IntPtr.Zero);
        }
        if (MsgTaskbarCreated != 0)
        {
            ChangeWindowMessageFilterEx(h, MsgTaskbarCreated, MSGFLT_ALLOW, IntPtr.Zero);
        }
        ChangeWindowMessageFilterEx(h, WM_COMMAND, MSGFLT_ALLOW, IntPtr.Zero);

        var r = Ok();
        if (Hidden)
        {
            if (w.Aumid != null)
            {
                r["aumidRead"] = ReadAumid(h);  // read-only in hidden mode
            }
            r["dry"] = true;
            r["skipped"] = "show";
        }
        else
        {
            if (w.Aumid != null)
            {
                r["aumidHr"] = HrText(WriteAumid(h, w.Aumid));
            }
            ShowWindow(h, c.OptBool("activate") ? Win32.SW_SHOWNORMAL : Win32.SW_SHOWNOACTIVATE);
            if (!MarginsMeasured)
            {
                MeasureMargins(h);
                SetFrame(h, frame);
            }
        }
        r["window"] = Describe(w);
        return r;
    }

    static RECT ToWindowRect(RECT frame) =>
        new(frame.Left - Margins.Left, frame.Top - Margins.Top, frame.Right + Margins.Right, frame.Bottom + Margins.Bottom);

    static void MeasureMargins(IntPtr h)
    {
        Win32.GetWindowRect(h, out RECT win);
        if (Win32.DwmGetWindowAttribute(h, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0 &&
            frame.Width > 0)
        {
            Margins = new RECT(frame.Left - win.Left, frame.Top - win.Top, win.Right - frame.Right, win.Bottom - frame.Bottom);
            MarginsMeasured = true;
            Trace($"margins {Margins}");
        }
    }

    static void SetFrame(IntPtr h, RECT frame)
    {
        RECT win = ToWindowRect(frame);
        SetWindowPos(h, IntPtr.Zero, win.Left, win.Top, win.Width, win.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    static JsonObject Close(Command c)
    {
        var w = Get(c);
        DestroyWindow(w.Hwnd);
        return Ok();
    }

    static JsonObject Move(Command c)
    {
        var w = Get(c);
        SetFrame(w.Hwnd, ReadFrame(c, 1));
        var r = Ok();
        r["window"] = Describe(w);
        return r;
    }

    static JsonObject Snap(Command c)
    {
        var w = Get(c);
        string side = c.Arg(1, "side").ToLowerInvariant();
        Win32.FrameRect(w.Hwnd, out RECT frame);
        IntPtr mon = Win32.MonitorFromRect(ref frame, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        Win32.GetMonitorInfoW(mon, ref mi);
        RECT work = mi.rcWork;
        int half = work.Width / 2;
        RECT target = side switch
        {
            "left" => new RECT(work.Left, work.Top, work.Left + half, work.Bottom),
            "right" => new RECT(work.Left + half, work.Top, work.Right, work.Bottom),
            _ => throw new FormatException("side must be left or right"),
        };
        SetFrame(w.Hwnd, target);
        var r = Ok();
        r["target"] = Rect(target);
        r["window"] = Describe(w);
        return r;
    }

    static JsonObject Show(Command c, int cmd)
    {
        var w = Get(c);
        if (Hidden)
        {
            return Dry($"{c.Verb} {w.Key}");
        }
        ShowWindow(w.Hwnd, cmd);
        var r = Ok();
        r["window"] = Describe(w);
        return r;
    }

    /// <summary>Puts a window on top of the other windows without activating it, so that injected
    /// clicks meant for it cannot land on another app's window.</summary>
    static JsonObject Raise(Command c)
    {
        var w = Get(c);
        if (Hidden)
        {
            return Dry($"raise {w.Key}");
        }
        SetWindowPos(w.Hwnd, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        return Ok();
    }

    static JsonObject Focus(Command c)
    {
        var w = Get(c);
        if (Hidden)
        {
            return Dry($"focus {w.Key}");
        }
        IntPtr h = w.Hwnd;
        if (Win32.IsIconic(h))
        {
            ShowWindow(h, Win32.SW_RESTORE);
        }
        bool done = Win32.GetForegroundWindow() == h || (SetForegroundWindow(h) && Win32.GetForegroundWindow() == h);
        string how = done ? "direct" : null;
        if (!done)
        {
            IntPtr fg = Win32.GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            if (attached)
            {
                AttachThreadInput(me, fgThread, false);
            }
            done = Win32.GetForegroundWindow() == h;
            how = attached ? "attach" : "retry";
        }
        var r = Ok();
        r["foreground"] = done;
        r["how"] = how;
        return r;
    }

    // ------------------------------------------------ AppUserModelID

    static int WriteAumid(IntPtr h, string id)
    {
        Guid iid = ComIds.IID_IPropertyStore;
        int hr = SHGetPropertyStoreForWindow(h, ref iid, out IPropertyStore store);
        if (hr != 0)
        {
            return hr;
        }
        try
        {
            var pv = new PROPVARIANT();
            if (id != null)
            {
                pv.vt = ComIds.VT_LPWSTR;
                pv.p = Marshal.StringToCoTaskMemUni(id);
            }
            var key = ComIds.PKEY_AppUserModel_ID;
            hr = store.SetValue(ref key, ref pv);
            if (hr == 0)
            {
                hr = store.Commit();
            }
            PropVariantClear(ref pv);
            return hr;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    static string ReadAumid(IntPtr h)
    {
        Guid iid = ComIds.IID_IPropertyStore;
        if (SHGetPropertyStoreForWindow(h, ref iid, out IPropertyStore store) != 0)
        {
            return "<no store>";
        }
        try
        {
            var key = ComIds.PKEY_AppUserModel_ID;
            if (store.GetValue(ref key, out PROPVARIANT pv) != 0)
            {
                return "<error>";
            }
            string s = pv.vt == ComIds.VT_LPWSTR ? Marshal.PtrToStringUni(pv.p) : $"<vt {pv.vt}>";
            PropVariantClear(ref pv);
            return s;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    static JsonObject SetAumid(Command c)
    {
        var w = Get(c);
        string id = c.Arg(1, "aumid");
        string value = id.Equals("default", StringComparison.OrdinalIgnoreCase) ? Aumid : id;
        if (Hidden)
        {
            var d = Dry($"aumid {w.Key} {value}");
            d["current"] = ReadAumid(w.Hwnd);
            return d;
        }
        int hr = WriteAumid(w.Hwnd, value);
        if (hr == 0)
        {
            w.Aumid = value;
        }
        var r = Ok();
        r["hr"] = HrText(hr);
        r["current"] = ReadAumid(w.Hwnd);
        return r;
    }

    // ------------------------------------------------ taskbar state (ITaskbarList3)

    static JsonObject Progress(Command c)
    {
        var w = Get(c);
        w.ProgressState = c.Arg(1, "state").ToLowerInvariant() switch
        {
            "none" => ComIds.TBPF_NOPROGRESS,
            "indeterminate" => ComIds.TBPF_INDETERMINATE,
            "normal" => ComIds.TBPF_NORMAL,
            "error" => ComIds.TBPF_ERROR,
            "paused" => ComIds.TBPF_PAUSED,
            _ => throw new FormatException("state must be none, indeterminate, normal, error or paused"),
        };
        w.ProgressValue = Math.Clamp(c.Args.Count > 2 ? c.ArgInt(2, "value") : 0, 0, 100);
        w.ProgressHr = int.MinValue;
        return ApplyTaskbarState(w, "progress");
    }

    static JsonObject Overlay(Command c)
    {
        var w = Get(c);
        string v = c.Arg(1, "colour");
        if (w.OverlayIcon != IntPtr.Zero)
        {
            DestroyIcon(w.OverlayIcon);
            w.OverlayIcon = IntPtr.Zero;
        }
        w.OverlayRgb = v.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : Command.ParseRgb(v);
        w.OverlayDesc = c.Opt("desc", $"{w.Title} badge");
        if (w.OverlayRgb != null)
        {
            w.OverlayIcon = SolidIcon(16, w.OverlayRgb.Value);
        }
        w.OverlaySet = true;
        w.OverlayHr = int.MinValue;
        return ApplyTaskbarState(w, "overlay");
    }

    static JsonObject ThumbBar(Command c)
    {
        var w = Get(c);
        int n = Math.Clamp(c.ArgInt(1, "count"), 1, 7);
        if (w.ThumbButtons != 0)
        {
            throw new InvalidOperationException("thumbnail toolbar buttons can be added once per window");
        }
        w.ThumbButtons = n;
        w.ThumbIcon = SolidIcon(16, ThumbRgb);
        w.ThumbHr = int.MinValue;
        return ApplyTaskbarState(w, "thumbbar");
    }

    static JsonObject Reapply(Command c)
    {
        var targets = c.Arg(0, "key").Equals("all", StringComparison.OrdinalIgnoreCase) ? Windows.Values.ToList() : new List<TestWindow> { Get(c) };
        var arr = new JsonArray();
        foreach (var w in targets)
        {
            if (w.ProgressState != null) w.ProgressHr = int.MinValue;
            if (w.OverlaySet) w.OverlayHr = int.MinValue;
            if (w.ThumbButtons != 0) w.ThumbHr = int.MinValue;
            arr.Add(ApplyTaskbarState(w, "reapply"));
        }
        return new JsonObject { ["ok"] = true, ["results"] = arr };
    }

    /// <summary>Calls ITaskbarList3 for whatever is pending on a window. Nothing is applied before
    /// the window's first TaskbarButtonCreated, and nothing in hidden mode.</summary>
    static JsonObject ApplyTaskbarState(TestWindow w, string what = "pending")
    {
        if (Hidden)
        {
            return Dry($"{what} {w.Key} (ITaskbarList3 HrInit=0x{TaskbarInitHr:X8} only)");
        }
        if (Taskbar == null || w.ButtonCreated == 0)
        {
            var p = Ok();
            p["pending"] = true;
            return p;
        }

        if (w.ProgressState != null && w.ProgressHr == int.MinValue)
        {
            int hr = 0;
            int state = w.ProgressState.Value;
            if (state == ComIds.TBPF_NORMAL || state == ComIds.TBPF_ERROR || state == ComIds.TBPF_PAUSED)
            {
                hr = Taskbar.SetProgressValue(w.Hwnd, (ulong)w.ProgressValue, 100);
            }
            int hr2 = Taskbar.SetProgressState(w.Hwnd, state);
            w.ProgressHr = hr != 0 ? hr : hr2;
        }
        if (w.OverlaySet && w.OverlayHr == int.MinValue)
        {
            w.OverlayHr = w.OverlayRgb != null
                ? Taskbar.SetOverlayIcon(w.Hwnd, w.OverlayIcon, w.OverlayDesc)
                : Taskbar.SetOverlayIcon(w.Hwnd, IntPtr.Zero, null);
        }
        if (w.ThumbButtons != 0 && w.ThumbHr == int.MinValue)
        {
            var buttons = new THUMBBUTTON[w.ThumbButtons];
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i] = new THUMBBUTTON
                {
                    dwMask = ComIds.THB_ICON | ComIds.THB_TOOLTIP | ComIds.THB_FLAGS,
                    iId = (uint)(100 + i),
                    hIcon = w.ThumbIcon,
                    szTip = $"{w.Title} btn {i + 1}",
                    dwFlags = 0,
                };
            }
            w.ThumbHr = Taskbar.ThumbBarAddButtons(w.Hwnd, (uint)buttons.Length, buttons);
        }
        var r = Ok();
        r["window"] = Describe(w);
        return r;
    }

    // ------------------------------------------------ virtual desktops

    static IVirtualDesktopManager Desktops() =>
        Vdm ??= (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.CLSID_VirtualDesktopManager));

    static JsonObject VdGet(Command c)
    {
        var w = Get(c);
        int hr = Desktops().GetWindowDesktopId(w.Hwnd, out Guid id);
        int hr2 = Desktops().IsWindowOnCurrentVirtualDesktop(w.Hwnd, out bool current);
        var r = Ok();
        r["hr"] = HrText(hr);
        r["desktop"] = hr == 0 ? id.ToString() : null;
        r["onCurrent"] = hr2 == 0 ? current : null;
        return r;
    }

    static JsonObject VdMove(Command c)
    {
        var w = Get(c);
        Guid target = Guid.Parse(c.Arg(1, "desktop guid"));
        if (Hidden)
        {
            var d = Dry($"vdmove {w.Key} {target}");
            d["read"] = VdGet(c);
            return d;
        }
        int hr = Desktops().MoveWindowToDesktop(w.Hwnd, ref target);
        var r = Ok();
        r["hr"] = HrText(hr);
        r["cloaked"] = Win32.Cloaked(w.Hwnd);
        return r;
    }

    // ------------------------------------------------ misc

    /// <summary>A screen point in the title bar that hit-tests as caption, for dragging: clear of the
    /// icon and the caption buttons where the window is wide enough, anywhere in the title bar of a
    /// narrow one (the README demo's windows are 208 pixels wide).</summary>
    static JsonObject Grab(Command c)
    {
        var w = Get(c);
        Win32.FrameRect(w.Hwnd, out RECT frame);
        foreach (var (from, to, step) in new[] { (frame.Left + 90, frame.Right - 160, 20), (frame.Left + 8, frame.Right - 8, 4) })
        {
            foreach (int dy in new[] { 12, 8, 16, 20 })
            {
                for (int x = from; x < to; x += step)
                {
                    int y = frame.Top + dy;
                    IntPtr l = new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
                    if (SendMessageW(w.Hwnd, WM_NCHITTEST, IntPtr.Zero, l) == new IntPtr(HTCAPTION))
                    {
                        var r = Ok();
                        r["x"] = x;
                        r["y"] = y;
                        return r;
                    }
                }
            }
        }
        throw new InvalidOperationException("no caption point found");
    }

    static JsonObject Title(Command c)
    {
        var w = Get(c);
        string t = c.Arg(1, "title");
        if (!t.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new FormatException($"title must start with {Prefix}");
        }
        w.Title = t;
        SetWindowTextW(w.Hwnd, t);
        return Ok();
    }

    static JsonObject Quit()
    {
        PostMessageW(MsgWnd, WM_APP_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
        return Ok();
    }
}
