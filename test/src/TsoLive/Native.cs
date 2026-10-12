// Win32 declarations used only by the live-test library.
using System;
using System.Runtime.InteropServices;
using TsoShared;

namespace TsoLive;

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx, dy;
    public uint mouseData, dwFlags, time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk, wScan;
    public uint dwFlags, time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUT
{
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public MOUSEINPUT mi;
    [FieldOffset(8)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
public struct GUITHREADINFO
{
    public int cbSize;
    public uint flags;
    public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
    public RECT rcCaret;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct PROCESSENTRY32
{
    public int dwSize;
    public int cntUsage;
    public int th32ProcessID;
    public IntPtr th32DefaultHeapID;
    public int th32ModuleID;
    public int cntThreads;
    public int th32ParentProcessID;
    public int pcPriClassBase;
    public int dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam, lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

public delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

public static class Native
{
    public const uint PROCESS_VM_READ = 0x10, PROCESS_QUERY_INFORMATION = 0x400, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000,
        THREAD_QUERY_LIMITED_INFORMATION = 0x800;
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8,
        MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const uint PW_RENDERFULLCONTENT = 2;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const uint WM_QUIT = 0x12;
    public const uint GUI_INMENUMODE = 0x4, GUI_INMOVESIZE = 0x2, GUI_POPUPMENUMODE = 0x10;
    public const uint TH32CS_SNAPPROCESS = 0x2;

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool ReadProcessMemory(IntPtr process, IntPtr address, void* buffer, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr p, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern bool GetThreadTimes(IntPtr t, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern bool QueryProcessCycleTime(IntPtr p, out ulong cycles);
    [DllImport("kernel32.dll")] public static extern bool QueryThreadCycleTime(IntPtr t, out ulong cycles);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);

    public static INPUT Mouse(uint flags, int dx = 0, int dy = 0) =>
        new() { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } };

    public static INPUT Key(ushort vk, bool up, bool extended) =>
        new()
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0) },
        };
}
