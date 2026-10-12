// Oracle (c): the shell process's identity, start time, responsiveness, CPU time and modules.
// Read-only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TsoShared;

namespace TsoLive;

public sealed class ShellInfo
{
    public int Pid;
    public long Creation;  // FILETIME ticks
    public IntPtr Tray;
    public uint TrayThread;
    public bool Hung;

    public bool SameProcess(ShellInfo other) => other != null && other.Pid == Pid && other.Creation == Creation;

    public DateTime StartLocal => Creation == 0 ? DateTime.MinValue : DateTime.FromFileTime(Creation);

    public override string ToString() =>
        Pid == 0 ? "no shell" : $"pid={Pid} start={StartLocal:yyyy-MM-dd HH:mm:ss.fff} tray=0x{Tray.ToInt64():X} thread={TrayThread}{(Hung ? " HUNG" : "")}";
}

public sealed class CpuSample
{
    public long Wall;           // Stopwatch timestamp
    public long Process;        // kernel + user, 100 ns (15.6 ms granularity)
    public long Thread;         // taskbar thread kernel + user, 100 ns
    public ulong ProcessCycles; // precise, in CPU reference cycles
    public ulong ThreadCycles;
    public int Pid;
}

public sealed class ModuleRow
{
    public string Name, Path, FileVersion;
    public long Base;

    public override string ToString() => $"{Name} {FileVersion} 0x{Base:X} {Path}";
}

public static class ExplorerInfo
{
    public static ShellInfo Shell()
    {
        var s = new ShellInfo { Tray = Win32.ShellTrayWindow() };
        if (s.Tray == IntPtr.Zero)
        {
            return s;
        }
        s.TrayThread = Win32.GetWindowThreadProcessId(s.Tray, out uint pid);
        s.Pid = (int)pid;
        s.Hung = Win32.IsHungAppWindow(s.Tray);
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, s.Pid);
        if (h != IntPtr.Zero)
        {
            Native.GetProcessTimes(h, out s.Creation, out _, out _, out _);
            Native.CloseHandle(h);
        }
        return s;
    }

    public static CpuSample Cpu(ShellInfo s)
    {
        var c = new CpuSample { Wall = Stopwatch.GetTimestamp(), Pid = s.Pid };
        IntPtr p = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, s.Pid);
        if (p != IntPtr.Zero)
        {
            Native.GetProcessTimes(p, out _, out _, out long k, out long u);
            c.Process = k + u;
            Native.QueryProcessCycleTime(p, out c.ProcessCycles);
            Native.CloseHandle(p);
        }
        IntPtr t = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, s.TrayThread);
        if (t != IntPtr.Zero)
        {
            Native.GetThreadTimes(t, out _, out _, out long k, out long u);
            c.Thread = k + u;
            Native.QueryThreadCycleTime(t, out c.ThreadCycles);
            Native.CloseHandle(t);
        }
        return c;
    }

    /// <summary>The processor's nominal clock (cycle counts tick at this rate), from the registry.</summary>
    public static double CyclesPerMs()
    {
        object mhz = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", null);
        return mhz is int m && m > 0 ? m * 1000.0 : double.NaN;
    }

    /// <summary>CPU milliseconds per wall-clock second, for the process and the taskbar thread:
    /// from the cycle counters when the clock rate is known (precise), else from the times.</summary>
    public static (double process, double thread, double seconds) Rate(CpuSample a, CpuSample b)
    {
        double seconds = (b.Wall - a.Wall) / (double)Stopwatch.Frequency;
        if (seconds <= 0 || a.Pid != b.Pid)
        {
            return (double.NaN, double.NaN, seconds);
        }
        double perMs = CyclesPerMs();
        if (!double.IsNaN(perMs) && b.ProcessCycles > a.ProcessCycles)
        {
            return ((b.ProcessCycles - a.ProcessCycles) / perMs / seconds, (b.ThreadCycles - a.ThreadCycles) / perMs / seconds, seconds);
        }
        return ((b.Process - a.Process) / 1e4 / seconds, (b.Thread - a.Thread) / 1e4 / seconds, seconds);
    }

    /// <summary>The same from the 15.6 ms-granular times, for comparison.</summary>
    public static (double process, double thread) RateFromTimes(CpuSample a, CpuSample b)
    {
        double seconds = (b.Wall - a.Wall) / (double)Stopwatch.Frequency;
        return seconds <= 0 ? (double.NaN, double.NaN) : ((b.Process - a.Process) / 1e4 / seconds, (b.Thread - a.Thread) / 1e4 / seconds);
    }

    public static List<ModuleRow> Modules(int pid, Func<string, bool> filter = null)
    {
        var rows = new List<ModuleRow>();
        using var proc = Process.GetProcessById(pid);
        foreach (ProcessModule m in proc.Modules)
        {
            if (filter != null && !filter(m.ModuleName))
            {
                continue;
            }
            string ver = "";
            try
            {
                ver = m.FileVersionInfo.FileVersion ?? "";
            }
            catch
            {
            }
            rows.Add(new ModuleRow { Name = m.ModuleName, Path = m.FileName, Base = m.BaseAddress.ToInt64(), FileVersion = ver });
        }
        return rows;
    }

    /// <summary>The GUI state of a thread: capture, menu mode, move/size (what the mod's IsUserBusy looks at).</summary>
    public static string GuiThreadState(uint tid)
    {
        var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(tid, ref gi))
        {
            return "unavailable";
        }
        var parts = new List<string>();
        if ((gi.flags & Native.GUI_INMENUMODE) != 0) parts.Add("menu");
        if ((gi.flags & Native.GUI_POPUPMENUMODE) != 0) parts.Add("popupmenu");
        if ((gi.flags & Native.GUI_INMOVESIZE) != 0) parts.Add("movesize");
        if (gi.hwndCapture != IntPtr.Zero) parts.Add($"capture=0x{gi.hwndCapture.ToInt64():X}({Win32.ClassOf(gi.hwndCapture)})");
        return parts.Count == 0 ? "idle" : string.Join(",", parts);
    }

    /// <summary>Processes whose parent is the given process (Toolhelp snapshot).</summary>
    public static List<(int pid, string exe)> Children(int parentPid)
    {
        var list = new List<(int, string)>();
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
        {
            return list;
        }
        try
        {
            var e = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Native.Process32FirstW(snap, ref e); ok; ok = Native.Process32NextW(snap, ref e))
            {
                if (e.th32ParentProcessID == parentPid && e.th32ProcessID != parentPid)
                {
                    list.Add((e.th32ProcessID, e.szExeFile));
                }
            }
        }
        finally
        {
            Native.CloseHandle(snap);
        }
        return list;
    }

    /// <summary>Visible top-level windows whose title starts with the prefix (any process).</summary>
    public static List<(IntPtr hwnd, uint pid, string title, bool visible)> WindowsWithPrefix(string prefix)
    {
        var list = new List<(IntPtr, uint, string, bool)>();
        Win32.EnumWindows((h, l) =>
        {
            string t = Win32.TitleOf(h);
            if (t.StartsWith(prefix, StringComparison.Ordinal))
            {
                list.Add((h, Win32.PidOf(h), t, Win32.IsWindowVisible(h)));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
