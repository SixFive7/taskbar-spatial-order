// Oracle (b): UI Automation. Read-only: what a screen reader sees.
//
// Taskbar buttons are "Taskbar.TaskListButtonAutomationPeer" elements; a combined group's
// AutomationId is "Appid: <AUMID>" and its Name "<App> - N running windows[ pinned]"; with
// grouping off a window's button has the AutomationId "Window: 0x<hwnd>". An overlay badge is an
// Image child with the AutomationId "OverlayIcon". Thumbnails in the hover flyout are
// TaskItemThumbnailViewAutomationPeer elements, their toolbar buttons ThumbBarButtonAutomationPeer
// (the class names UI Automation reports on build 26300).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using TsoShared;
using WRect = System.Windows.Rect;

namespace TsoLive;

public sealed class UiaNode
{
    public string ControlType, ClassName, Name, AutomationId;
    public WRect Rect;
    public IntPtr Host;
    public double? RangeValue;

    public double CenterX => Rect.IsEmpty ? double.NaN : Rect.X + Rect.Width / 2;
    public double CenterY => Rect.IsEmpty ? double.NaN : Rect.Y + Rect.Height / 2;

    public bool Contains(UiaNode other) =>
        !Rect.IsEmpty && !other.Rect.IsEmpty && Rect.Contains(new System.Windows.Point(other.CenterX, other.CenterY));

    public override string ToString() =>
        $"[{ControlType}] cls=\"{ClassName}\" name=\"{Logic.OneLine(Name)}\" id=\"{AutomationId}\" rect=({Rect.X:0},{Rect.Y:0},{Rect.Width:0}x{Rect.Height:0})" +
        (RangeValue != null ? $" value={RangeValue}" : "");
}

public sealed class UiaButton
{
    public IntPtr Tray;
    public string TrayClass;
    public UiaNode Node;
    public int Index;
    public UiaNode Overlay;
    public UiaNode ProgressBar;

    public string Name => Node.Name;
    public string AutomationId => Node.AutomationId;
    public WRect Rect => Node.Rect;
    public int RunningWindows => Logic.RunningWindows(Node.Name);
    public IntPtr Window => Logic.WindowFromAutomationId(Node.AutomationId);
    public string AppId => Logic.AppIdFromAutomationId(Node.AutomationId);

    public override string ToString() =>
        $"#{Index} {Node}{(Overlay != null ? " +overlay" : "")}{(ProgressBar != null ? " +progress=" + ProgressBar.RangeValue : "")}";
}

public sealed class FlyoutRead
{
    public bool Open;
    public readonly List<IntPtr> Hosts = new();
    public readonly List<UiaNode> All = new();
    public readonly List<UiaNode> Thumbnails = new();
    public readonly List<UiaNode> ThumbBarButtons = new();

    /// <summary>Thumbnail names left to right (then top to bottom).</summary>
    public List<string> ThumbnailNames() =>
        Thumbnails.OrderBy(t => Math.Round(t.Rect.Y / 40)).ThenBy(t => t.Rect.X).Select(t => t.Name).ToList();

    /// <summary>The toolbar buttons under each thumbnail, by thumbnail name.</summary>
    public Dictionary<string, List<string>> ThumbBarsByThumbnail()
    {
        var map = Thumbnails.ToDictionary(t => t.Name ?? "", _ => new List<string>());
        foreach (var b in ThumbBarButtons)
        {
            var owner = Thumbnails.FirstOrDefault(t => t.Contains(b)) ??
                        Thumbnails.OrderBy(t => Math.Abs(t.CenterX - b.CenterX)).FirstOrDefault();
            if (owner != null)
            {
                map[owner.Name ?? ""].Add(b.Name);
            }
        }
        return map;
    }
}

public static class Uia
{
    static readonly string[] TaskButtonClasses = { "Taskbar.TaskListButtonAutomationPeer" };

    /// <summary>Runs f on a fresh MTA thread and gives up after timeoutMs (UIA calls into Explorer can block).</summary>
    public static T WithTimeout<T>(Func<T> f, int timeoutMs, string what)
    {
        T result = default;
        Exception error = null;
        var t = new Thread(() =>
        {
            try
            {
                Win32.SetThreadDpiAwarenessContext(Win32.DPI_PER_MONITOR_AWARE_V2);
                result = f();
            }
            catch (Exception e)
            {
                error = e;
            }
        })
        { IsBackground = true, Name = "uia " + what };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        if (!t.Join(timeoutMs))
        {
            throw new TimeoutException($"UIA {what} took longer than {timeoutMs} ms");
        }
        if (error != null)
        {
            throw new InvalidOperationException($"UIA {what}: {error.Message}", error);
        }
        return result;
    }

    static CacheRequest NewCache()
    {
        var cr = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.AutomationIdProperty);
        cr.Add(AutomationElement.ClassNameProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(RangeValuePattern.ValueProperty);
        return cr;
    }

    static UiaNode ToNode(AutomationElement e, IntPtr host)
    {
        object Get(AutomationProperty p)
        {
            try
            {
                object v = e.GetCachedPropertyValue(p, true);
                return v == AutomationElement.NotSupported ? null : v;
            }
            catch
            {
                return null;
            }
        }
        var ct = Get(AutomationElement.ControlTypeProperty) as ControlType;
        var rect = Get(AutomationElement.BoundingRectangleProperty) is WRect r ? r : WRect.Empty;
        return new UiaNode
        {
            ControlType = ct?.ProgrammaticName?.Replace("ControlType.", "") ?? "?",
            ClassName = Get(AutomationElement.ClassNameProperty) as string ?? "",
            Name = Get(AutomationElement.NameProperty) as string ?? "",
            AutomationId = Get(AutomationElement.AutomationIdProperty) as string ?? "",
            Rect = rect.IsEmpty || double.IsInfinity(rect.X) ? WRect.Empty : rect,
            Host = host,
            RangeValue = Get(RangeValuePattern.ValueProperty) is double d ? d : null,
        };
    }

    static List<UiaNode> Descendants(IntPtr hwnd, Condition condition)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var list = new List<UiaNode>();
        var cr = NewCache();
        using (cr.Activate())
        {
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, condition))
            {
                list.Add(ToNode(e, hwnd));
            }
        }
        return list;
    }

    public static List<(IntPtr hwnd, string cls)> Trays()
    {
        var list = new List<(IntPtr, string)>();
        Win32.EnumWindows((h, l) =>
        {
            string cls = Win32.ClassOf(h);
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
            {
                list.Add((h, cls));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>All task buttons on all taskbars, with their overlay badges and progress bars.</summary>
    public static List<UiaButton> TaskbarButtons(int timeoutMs = 10000) => WithTimeout(() =>
    {
        var result = new List<UiaButton>();
        var cond = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar));
        foreach (var (tray, cls) in Trays())
        {
            var nodes = Descendants(tray, cond);
            var buttons = nodes.Where(n => TaskButtonClasses.Contains(n.ClassName) && !n.Rect.IsEmpty)
                               .OrderBy(n => n.Rect.X).ThenBy(n => n.Rect.Y).ToList();
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = new UiaButton { Tray = tray, TrayClass = cls, Node = buttons[i], Index = i };
                b.Overlay = nodes.FirstOrDefault(n => n.AutomationId == "OverlayIcon" && buttons[i].Contains(n));
                b.ProgressBar = nodes.FirstOrDefault(n => n.ControlType == "ProgressBar" && buttons[i].Contains(n));
                result.Add(b);
            }
        }
        return result;
    }, timeoutMs, "taskbar buttons");

    static readonly HashSet<string> NotFlyout = new()
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "EdgeUiInputTopWndClass",
        "DummyDWMListenerWindow", "ThumbnailDeviceHelperWnd", "ForegroundStaging", "CabinetWClass",
    };

    /// <summary>Visible top-level windows of the shell process that could host a flyout or jump list.</summary>
    public static List<(IntPtr hwnd, string cls)> PopupHosts(int shellPid)
    {
        var list = new List<(IntPtr, string)>();
        Win32.EnumWindows((h, l) =>
        {
            if (Win32.IsWindowVisible(h) && Win32.PidOf(h) == shellPid)
            {
                string cls = Win32.ClassOf(h);
                if (!NotFlyout.Contains(cls))
                {
                    list.Add((h, cls));
                }
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The open thumbnail flyout, if any: its thumbnails and their toolbar buttons.</summary>
    public static FlyoutRead Flyout(int shellPid, string namePrefix, int timeoutMs = 8000) => WithTimeout(() =>
    {
        var read = new FlyoutRead();
        var hosts = PopupHosts(shellPid);
        foreach (var (h, cls) in hosts)
        {
            read.Hosts.Add(h);
            List<UiaNode> nodes;
            try
            {
                nodes = Descendants(h, Condition.TrueCondition);
            }
            catch
            {
                continue;
            }
            read.All.AddRange(nodes);
            foreach (var n in nodes)
            {
                if (n.ClassName.Contains("ThumbBarButton", StringComparison.OrdinalIgnoreCase))
                {
                    read.ThumbBarButtons.Add(n);
                }
                else if (n.ClassName.Contains("TaskItemThumbnailView", StringComparison.OrdinalIgnoreCase) ||
                         (namePrefix != null && n.Name.StartsWith(namePrefix, StringComparison.Ordinal) &&
                          (n.ControlType == "ListItem" || n.ControlType == "Button" || n.ControlType == "Group")))
                {
                    read.Thumbnails.Add(n);
                }
            }
        }
        if (read.Thumbnails.Count == 0)
        {
            // Not in a popup window: look in the taskbars themselves.
            foreach (var (tray, _) in Trays())
            {
                foreach (var n in Descendants(tray, Condition.TrueCondition))
                {
                    if (n.ClassName.Contains("ThumbBarButton", StringComparison.OrdinalIgnoreCase))
                    {
                        read.ThumbBarButtons.Add(n);
                        read.All.Add(n);
                    }
                    else if (n.ClassName.Contains("TaskItemThumbnailView", StringComparison.OrdinalIgnoreCase))
                    {
                        read.Thumbnails.Add(n);
                        read.All.Add(n);
                    }
                }
            }
        }
        // A thumbnail can match twice (peer and its named child); keep the outermost per name.
        var outer = read.Thumbnails.GroupBy(t => t.Name)
                        .Select(g => g.OrderByDescending(t => t.Rect.Width * t.Rect.Height).First()).ToList();
        read.Thumbnails.Clear();
        read.Thumbnails.AddRange(outer);
        read.Open = read.Thumbnails.Count > 0;
        return read;
    }, timeoutMs, "flyout");

    /// <summary>Every element under a window, for the record.</summary>
    public static List<UiaNode> Subtree(IntPtr hwnd, int timeoutMs = 8000) =>
        WithTimeout(() => Descendants(hwnd, Condition.TrueCondition), timeoutMs, "subtree");
}
