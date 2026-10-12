// Detects an open taskbar jump list, read-only. On build 26300 the jump list is drawn by
// ShellExperienceHost.exe (JumpViewUI.dll), not by Explorer, whose taskbar is only told that it
// is showing. A jump list therefore shows as:
//  (1) a window of a shell experience host becoming visible and uncloaked, with a real size;
//  (2) a new top-level element of such a process under the UI Automation root;
//  (3) the foreground window moving to such a process;
//  (4) pixels changing in the area above the button (corroboration only: a thumbnail flyout
//      changes them too).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Automation;
using TsoShared;

namespace TsoLive;

public sealed class HostWindow
{
    public IntPtr Hwnd;
    public string Process, Class, Title;
    public bool Visible;
    public int Cloaked;
    public RECT Rect;

    public bool Shown => Visible && Cloaked == 0 && Rect.Width >= 80 && Rect.Height >= 80;

    public override string ToString() =>
        $"{Process} 0x{Hwnd.ToInt64():X} cls=\"{Class}\" title=\"{Title}\" vis={(Visible ? 1 : 0)} cloak={Cloaked} {Rect}";
}

public sealed class PopupSnapshot
{
    public double T;
    public readonly List<HostWindow> Windows = new();
    public IntPtr Foreground;
    public string ForegroundProcess = "", ForegroundClass = "";
    public readonly List<UiaNode> Root = new();
    public bool UiaRead;
}

public sealed class JumpListSignals
{
    public bool Confirmed;
    public IntPtr Host;
    public string HostProcess;
    public readonly List<string> Evidence = new();

    public override string ToString() => (Confirmed ? "open: " : "not open: ") + (Evidence.Count == 0 ? "no signal" : string.Join("; ", Evidence));
}

public static class JumpListProbe
{
    /// <summary>Processes that host shell popups (jump lists, Start, flyouts).</summary>
    public static readonly string[] HostProcesses = { "ShellExperienceHost", "StartMenuExperienceHost", "ShellHost" };

    static readonly HashSet<string> ExplorerIgnored = new()
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "EdgeUiInputTopWndClass", "DummyDWMListenerWindow",
        "ThumbnailDeviceHelperWnd", "ForegroundStaging", "CabinetWClass", "IME", "MSCTFIME UI", "tooltips_class32",
    };

    static string ProcessName(uint pid, Dictionary<uint, string> cache)
    {
        if (!cache.TryGetValue(pid, out string n))
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                n = p.ProcessName;
            }
            catch
            {
                n = "?";
            }
            cache[pid] = n;
        }
        return n;
    }

    public static PopupSnapshot Take(double t, bool withUia)
    {
        var s = new PopupSnapshot { T = t };
        var names = new Dictionary<uint, string>();
        Win32.EnumWindows((h, l) =>
        {
            uint pid = Win32.PidOf(h);
            string proc = ProcessName(pid, names);
            bool host = HostProcesses.Contains(proc, StringComparer.OrdinalIgnoreCase);
            string cls = Win32.ClassOf(h);
            if (!host && !(proc.Equals("explorer", StringComparison.OrdinalIgnoreCase) && !ExplorerIgnored.Contains(cls)))
            {
                return true;
            }
            Win32.GetWindowRect(h, out RECT r);
            s.Windows.Add(new HostWindow
            {
                Hwnd = h, Process = proc, Class = cls, Title = Win32.TitleOf(h), Visible = Win32.IsWindowVisible(h),
                Cloaked = Win32.Cloaked(h), Rect = r,
            });
            return true;
        }, IntPtr.Zero);
        s.Foreground = Win32.GetForegroundWindow();
        if (s.Foreground != IntPtr.Zero)
        {
            s.ForegroundProcess = ProcessName(Win32.PidOf(s.Foreground), names);
            s.ForegroundClass = Win32.ClassOf(s.Foreground);
        }
        if (withUia)
        {
            try
            {
                s.Root.AddRange(Uia.WithTimeout(() =>
                {
                    var root = AutomationElement.RootElement;
                    var cr = new CacheRequest { AutomationElementMode = AutomationElementMode.None, TreeScope = TreeScope.Element };
                    cr.Add(AutomationElement.NameProperty);
                    cr.Add(AutomationElement.ClassNameProperty);
                    cr.Add(AutomationElement.ProcessIdProperty);
                    cr.Add(AutomationElement.NativeWindowHandleProperty);
                    cr.Add(AutomationElement.BoundingRectangleProperty);
                    var list = new List<UiaNode>();
                    using (cr.Activate())
                    {
                        foreach (AutomationElement e in root.FindAll(TreeScope.Children, Condition.TrueCondition))
                        {
                            int pid = (int)e.GetCachedPropertyValue(AutomationElement.ProcessIdProperty);
                            var rect = (System.Windows.Rect)e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                            list.Add(new UiaNode
                            {
                                Name = e.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "",
                                ClassName = e.GetCachedPropertyValue(AutomationElement.ClassNameProperty) as string ?? "",
                                AutomationId = ProcessName((uint)pid, names),  // the process name, for the record
                                Host = new IntPtr((int)e.GetCachedPropertyValue(AutomationElement.NativeWindowHandleProperty)),
                                Rect = rect.IsEmpty || double.IsInfinity(rect.X) ? System.Windows.Rect.Empty : rect,
                                ControlType = "root child",
                            });
                        }
                    }
                    return list;
                }, 4000, "root children"));
                s.UiaRead = true;
            }
            catch
            {
                // A slow UIA read only costs this signal.
            }
        }
        return s;
    }

    static bool IsHost(string proc) => HostProcesses.Contains(proc, StringComparer.OrdinalIgnoreCase);

    /// <summary>What changed between a snapshot before the right-click and one after it.</summary>
    public static JumpListSignals Compare(PopupSnapshot before, PopupSnapshot now)
    {
        var sig = new JumpListSignals();
        var shownBefore = before.Windows.Where(w => w.Shown).Select(w => w.Hwnd).ToHashSet();
        foreach (var w in now.Windows.Where(w => w.Shown && !shownBefore.Contains(w.Hwnd)))
        {
            // An Explorer window here is more likely the thumbnail flyout: evidence, not confirmation.
            bool host = IsHost(w.Process);
            sig.Evidence.Add($"window shown: {w}");
            if (host && sig.Host == IntPtr.Zero)
            {
                sig.Host = w.Hwnd;
                sig.HostProcess = w.Process;
                sig.Confirmed = true;
            }
        }
        if (before.UiaRead && now.UiaRead)
        {
            var old = before.Root.Select(n => n.Host).ToHashSet();
            foreach (var n in now.Root.Where(n => !old.Contains(n.Host)))
            {
                sig.Evidence.Add($"new UIA top-level element: {n.AutomationId} hwnd=0x{n.Host.ToInt64():X} cls=\"{n.ClassName}\" name=\"{n.Name}\" rect=({n.Rect.X:0},{n.Rect.Y:0},{n.Rect.Width:0}x{n.Rect.Height:0})");
                if (IsHost(n.AutomationId) || n.Name.Contains("Jump", StringComparison.OrdinalIgnoreCase))
                {
                    sig.Confirmed = true;
                    if (sig.Host == IntPtr.Zero)
                    {
                        sig.Host = n.Host;
                        sig.HostProcess = n.AutomationId;
                    }
                }
            }
        }
        if (now.Foreground != before.Foreground)
        {
            sig.Evidence.Add($"foreground: {before.ForegroundProcess} \"{before.ForegroundClass}\" -> {now.ForegroundProcess} \"{now.ForegroundClass}\" 0x{now.Foreground.ToInt64():X}");
            if (IsHost(now.ForegroundProcess))
            {
                sig.Confirmed = true;
                if (sig.Host == IntPtr.Zero)
                {
                    sig.Host = now.Foreground;
                    sig.HostProcess = now.ForegroundProcess;
                }
            }
        }
        return sig;
    }

    /// <summary>Whether the jump list found earlier is still there.</summary>
    public static bool StillOpen(JumpListSignals open)
    {
        if (open?.Host == null || open.Host == IntPtr.Zero || !Win32.IsWindow(open.Host))
        {
            return false;
        }
        Win32.GetWindowRect(open.Host, out RECT r);
        bool shown = Win32.IsWindowVisible(open.Host) && Win32.Cloaked(open.Host) == 0 && r.Width >= 80 && r.Height >= 80;
        return shown || Win32.GetForegroundWindow() == open.Host;
    }

    /// <summary>Whether the foreground window belongs to a shell popup host (where a key press goes
    /// to the jump list, not to another app).</summary>
    public static bool ForegroundIsHost(out string what)
    {
        IntPtr fg = Win32.GetForegroundWindow();
        string proc = "?";
        try
        {
            using var p = Process.GetProcessById((int)Win32.PidOf(fg));
            proc = p.ProcessName;
        }
        catch
        {
        }
        what = $"{proc} \"{Win32.ClassOf(fg)}\" 0x{fg.ToInt64():X}";
        return IsHost(proc);
    }

    /// <summary>Whether JumpViewUI.dll is loaded in ShellExperienceHost (the jump list code has run).</summary>
    public static string JumpViewLoaded()
    {
        var found = new List<string>();
        foreach (var p in Process.GetProcessesByName("ShellExperienceHost").Concat(Process.GetProcessesByName("StartMenuExperienceHost")))
        {
            try
            {
                if (p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("JumpViewUI.dll", StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add($"{p.ProcessName}({p.Id})");
                }
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }
        return found.Count == 0 ? "not loaded" : "loaded in " + string.Join(", ", found);
    }

    public static string Describe(PopupSnapshot s) =>
        $"{s.Windows.Count(w => w.Shown)} shown host windows of {s.Windows.Count}; foreground {s.ForegroundProcess} \"{s.ForegroundClass}\"; " +
        $"{(s.UiaRead ? s.Root.Count + " UIA top-level elements" : "UIA not read")}";
}
