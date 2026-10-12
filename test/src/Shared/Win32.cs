// Win32 declarations shared by the test app and the live-test library.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TsoShared;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public override string ToString() => $"({Left},{Top},{Width}x{Height})";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;
    public POINT(int x, int y) { X = x; Y = y; }
    public override string ToString() => $"({X},{Y})";
}

[StructLayout(LayoutKind.Sequential)]
public struct WINDOWPLACEMENT
{
    public int length, flags, showCmd;
    public POINT ptMinPosition, ptMaxPosition;
    public RECT rcNormalPosition;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor, rcWork;
    public int dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
}

public static class Win32
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_MAXIMIZE = 3, SW_SHOWNOACTIVATE = 4,
        SW_SHOW = 5, SW_MINIMIZE = 6, SW_SHOWMINNOACTIVE = 7, SW_SHOWNA = 8, SW_RESTORE = 9;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
        SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;
    public const uint WM_SETTINGCHANGE = 0x1A, SMTO_ABORTIFHUNG = 0x2;
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
    public static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string name);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int cb);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int cb);

    public static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint PidOf(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        return pid;
    }

    /// <summary>The visible frame (DWM extended frame bounds), or the window rect.</summary>
    public static bool FrameRect(IntPtr h, out RECT r)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) == 0)
        {
            return true;
        }
        return GetWindowRect(h, out r);
    }

    public static int Cloaked(IntPtr h)
    {
        return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int v, sizeof(int)) == 0 ? v : -1;
    }

    public static WINDOWPLACEMENT Placement(IntPtr h)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(h, ref wp);
        return wp;
    }

    /// <summary>Sends WM_SETTINGCHANGE("TraySettings") to all top-level windows.</summary>
    public static void BroadcastTraySettings()
    {
        SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "TraySettings", SMTO_ABORTIFHUNG, 2000, out _);
    }

    public static IntPtr ShellTrayWindow() => FindWindowW("Shell_TrayWnd", null);
}
