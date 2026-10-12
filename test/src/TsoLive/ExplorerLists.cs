// Oracle (a): Explorer's own lists, read with ReadProcessMemory. Read-only: no writes, no
// injection, no messages.
//
// L1  CTaskGroup item DPA (one per app, shared by all taskbars): the order of the thumbnails.
// L2  CTaskBtnGroup button DPA (one per app per taskbar): the order of the window buttons.
// The button-group DPA of each taskbar gives the order of the app groups on that taskbar.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TsoShared;

namespace TsoLive;

/// <summary>
/// Where things are in the taskbar's objects. Every object is recognised by its class's vtable,
/// looked up in Microsoft's public symbols for the copy that is loaded; every field is found at
/// attach time as the slot that leads to an object of the expected class, so nothing about the
/// layout is hard-coded.
/// </summary>
public sealed class TaskbarOffsets
{
    /// <summary>The vtables, by their decorated names in the public symbols, in the order of the fields below.</summary>
    static readonly string[] Vtables =
    {
        "??_7CTaskListWnd@@6BITaskListUI@@@", "??_7CTaskBtnGroup@@6BITaskBtnGroup@@@", "??_7CTaskButton@@6BITaskButton@@@",
        "??_7CTaskButton@@6BCTaskUnknown@@@", "??_7CWindowTaskItem@@6BITaskItem@@@", "??_7CImmersiveTaskItem@@6BITaskItem@@@",
        "??_7CTaskGroup@@6BITaskGroup@@@",
    };

    public string Name;

    /// <summary>The vtables' addresses in the shell.</summary>
    public long VtTaskListUI, VtTaskBtnGroup, VtTaskButton, VtTaskButtonUnknown, VtWindowTaskItem, VtImmersiveTaskItem, VtTaskGroup;

    /// <summary>The fields, in bytes from the start of their object: the button group list of a
    /// taskbar's ITaskListUI, the task group and the button list of a CTaskBtnGroup, the task item of
    /// a CTaskButton, the item list of a CTaskGroup and the window of a CWindowTaskItem.</summary>
    public int ButtonGroupsOff, BtnGroupGroupOff, BtnGroupButtonsOff, ButtonItemOff, GroupItemsOff, WindowItemHwndOff;

    public string Fields =>
        $"button groups +0x{ButtonGroupsOff:X}; a button group's task group +0x{BtnGroupGroupOff:X} and buttons +0x{BtnGroupButtonsOff:X}; " +
        $"a button's task item +0x{ButtonItemOff:X}; a task group's items +0x{GroupItemsOff:X}; a window item's window +0x{WindowItemHwndOff:X}";

    /// <summary>The vtables of the taskbar.dll loaded at moduleBase, whose CodeView record names this PDB.</summary>
    internal static TaskbarOffsets For(string pdbName, Guid guid, int age, long moduleBase)
    {
        var vt = TaskbarSymbols.Rvas(pdbName, guid, age, Vtables).Select(rva => moduleBase + rva).ToArray();
        return new TaskbarOffsets
        {
            Name = $"{pdbName} {guid:N}/{age}",
            VtTaskListUI = vt[0], VtTaskBtnGroup = vt[1], VtTaskButton = vt[2], VtTaskButtonUnknown = vt[3],
            VtWindowTaskItem = vt[4], VtImmersiveTaskItem = vt[5], VtTaskGroup = vt[6],
        };
    }
}

public sealed class ProbeButtonGroup
{
    public long Ptr, GroupPtr;
    public int Index;
    public readonly List<long> Items = new();
    public readonly List<IntPtr> Hwnds = new();
}

public sealed class ProbeTaskbar
{
    public IntPtr Tray, TaskList;
    public string TrayClass, Monitor;
    public RECT TrayRect;
    public int UiOffset;
    public readonly List<ProbeButtonGroup> ButtonGroups = new();
}

public sealed class ProbeGroup
{
    public long Ptr;
    public readonly List<long> Items = new();
    public readonly List<IntPtr> Hwnds = new();
}

public sealed class ProbeSnapshot
{
    public int Pid;
    public double ReadMs;
    public readonly List<ProbeTaskbar> Taskbars = new();
    public readonly Dictionary<long, ProbeGroup> Groups = new();
    public readonly List<string> Warnings = new();
}

public sealed class ProbeException : Exception
{
    public ProbeException(string message) : base(message) { }
}

sealed class Remote : IDisposable
{
    public readonly IntPtr Handle;
    public readonly int Pid;

    public Remote(int pid)
    {
        Pid = pid;
        Handle = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, pid);
        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({pid})");
        }
    }

    /// <summary>Reads count bytes into buffer; false if any of them can't be read.</summary>
    unsafe bool TryRead(long address, void* buffer, int count) =>
        address != 0 && Native.ReadProcessMemory(Handle, (IntPtr)address, buffer, (IntPtr)count, out IntPtr read) && read == (IntPtr)count;

    public unsafe bool TryQ(long address, out long value)
    {
        long v = 0;
        bool ok = TryRead(address, &v, 8);
        value = v;
        return ok;
    }

    public long Q(long address) => TryQ(address, out long v) ? v : throw new ProbeException($"read failed at 0x{address:X}");

    public unsafe int D(long address)
    {
        int v;
        return TryRead(address, &v, 4) ? v : throw new ProbeException($"read failed at 0x{address:X}");
    }

    public unsafe byte[] Bytes(long address, int count)
    {
        var buf = new byte[count];
        fixed (byte* p = buf)
        {
            return TryRead(address, p, count) ? buf : throw new ProbeException($"read of {count} bytes failed at 0x{address:X}");
        }
    }

    /// <summary>count pointers from an address; null if they can't all be read.</summary>
    unsafe long[] TryQs(long address, int count)
    {
        var result = new long[count];
        fixed (long* p = result)
        {
            return count == 0 || TryRead(address, p, count * 8) ? result : null;
        }
    }

    public long[] Qs(long address, int count) =>
        TryQs(address, count) ?? throw new ProbeException($"read of {count} pointers failed at 0x{address:X}");

    /// <summary>The pointers in a DPA ({ int cpItems; void** pArray; ... }; none if hdpa is null);
    /// null if it can't be read or holds more than max.</summary>
    public long[] TryDpa(long hdpa, int max)
    {
        if (hdpa == 0)
        {
            return Array.Empty<long>();
        }
        if (!TryQ(hdpa, out long head) || !TryQ(hdpa + 8, out long array))
        {
            return null;
        }
        int n = (int)head;
        return n >= 0 && n <= max ? TryQs(array, n) : null;
    }

    public long[] Dpa(long hdpa, int max = 8192) =>
        TryDpa(hdpa, max) ?? throw new ProbeException($"no readable DPA of at most {max} pointers at 0x{hdpa:X}");

    public void Dispose() => Native.CloseHandle(Handle);
}

/// <summary>Reads Explorer's lists. Not thread-safe: one instance per thread.</summary>
public sealed class Probe : IDisposable
{
    TaskbarOffsets _offsets;
    bool _fieldsFound;
    Remote _remote;
    long _base;
    string _moduleCheck;
    readonly Dictionary<IntPtr, int> _uiOffsets = new();

    public int Pid => _remote?.Pid ?? 0;

    /// <summary>The shell's process id: the owner of Shell_TrayWnd, or 0.</summary>
    public static int ShellPid()
    {
        IntPtr tray = Win32.ShellTrayWindow();
        return tray == IntPtr.Zero ? 0 : (int)Win32.PidOf(tray);
    }

    /// <summary>Attaches to the shell: the vtables of its taskbar.dll, then the fields, which are
    /// looked for again at every read until they are found (a taskbar that has only just started
    /// may show no window yet).</summary>
    void Attach(int pid)
    {
        if (_remote == null || _remote.Pid != pid)
        {
            _remote?.Dispose();
            _remote = null;
            _uiOffsets.Clear();
            _base = 0;
            _moduleCheck = null;
            _fieldsFound = false;

            var remote = new Remote(pid);
            try
            {
                using var proc = Process.GetProcessById(pid);
                foreach (ProcessModule m in proc.Modules)
                {
                    if (string.Equals(m.ModuleName, "taskbar.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        _base = m.BaseAddress.ToInt64();
                        break;
                    }
                }
                if (_base == 0)
                {
                    throw new ProbeException("taskbar.dll is not loaded in the shell process");
                }
                var (pdb, guid, age) = CodeView(remote, _base);
                _offsets = TaskbarOffsets.For(pdb, guid, age, _base);
                _moduleCheck = $"taskbar.dll base=0x{_base:X}, vtables from the symbols of {_offsets.Name}";
                _remote = remote;
            }
            catch
            {
                remote.Dispose();
                throw;
            }
        }
        if (!_fieldsFound)
        {
            FindFields(pid);
            _fieldsFound = true;
        }
    }

    /// <summary>
    /// Finds the fields in the objects on the taskbars. Each is the first of an object's first 64
    /// slots that leads to an object of the expected class, in one of the objects (in every one,
    /// for a window item's window). Throws if one isn't found, as when no taskbar shows a window.
    /// </summary>
    void FindFields(int pid)
    {
        var o = _offsets;
        // A read that fails leads nowhere.
        long At(long address) => _remote.TryQ(address, out long v) ? v : 0;
        long[] Dpa(long hdpa) => _remote.TryDpa(hdpa, 4096) ?? Array.Empty<long>();
        bool IsA(long obj, params long[] vtables) => Array.IndexOf(vtables, At(obj)) >= 0;
        bool IsDpaOf(long hdpa, params long[] vtables) => Dpa(hdpa) is { Length: > 0 } d && d.All(p => IsA(p, vtables));
        bool IsTaskItem(long p) => IsA(p, o.VtWindowTaskItem, o.VtImmersiveTaskItem);

        int Field(string what, IReadOnlyCollection<long> objects, Func<long, bool> leadsTo, bool every = false)
        {
            for (int off = 0; off < 64 * 8 && objects.Count > 0; off += 8)
            {
                bool Leads(long obj) => leadsTo(At(obj + off));
                if (every ? objects.All(Leads) : objects.Any(Leads))
                {
                    return off;
                }
            }
            throw new ProbeException($"could not find the {what} (searched the first 64 slots of {objects.Count} objects on the taskbars)");
        }

        var taskLists = Trays(pid).Select(t => (tray: t.Item1, list: FindDescendant(t.Item1, "MSTaskListWClass")))
            .Where(t => t.list != IntPtr.Zero).Select(t => TaskListUi(t.tray, t.list, out _)).ToList();
        o.ButtonGroupsOff = Field("button group list of a taskbar", taskLists, v => IsDpaOf(v, o.VtTaskBtnGroup));
        var buttonGroups = taskLists.SelectMany(t => Dpa(At(t + o.ButtonGroupsOff))).ToList();
        o.BtnGroupGroupOff = Field("task group of a button group", buttonGroups, v => IsA(v, o.VtTaskGroup));
        o.BtnGroupButtonsOff = Field("button list of a button group", buttonGroups, v => IsDpaOf(v, o.VtTaskButton, o.VtTaskButtonUnknown));
        var buttons = buttonGroups.SelectMany(g => Dpa(At(g + o.BtnGroupButtonsOff)).Select(b => (group: g, button: b))).ToList();
        o.ButtonItemOff = Field("task item of a button", buttons.Select(b => b.button).ToList(), IsTaskItem);
        var (taskGroup, item) = buttons.Select(b => (group: At(b.group + o.BtnGroupGroupOff), item: At(b.button + o.ButtonItemOff)))
            .FirstOrDefault(p => IsTaskItem(p.item));
        o.GroupItemsOff = Field("item list of a task group", new[] { taskGroup }, v => Dpa(v).Contains(item));
        var windowItems = buttonGroups.Select(g => At(g + o.BtnGroupGroupOff)).Distinct()
            .SelectMany(g => Dpa(At(g + o.GroupItemsOff))).Where(i => IsA(i, o.VtWindowTaskItem)).ToList();
        o.WindowItemHwndOff = Field("window of a window item", windowItems, v => Win32.IsWindow((IntPtr)v), every: true);
    }

    /// <summary>The ITaskListUI of a taskbar: CTaskListWnd, the object behind its MSTaskListWClass
    /// window, holds that interface's vtable in one of its first slots.</summary>
    long TaskListUi(IntPtr tray, IntPtr taskList, out int uiOff)
    {
        long obj = Win32.GetWindowLongPtr(taskList, 0).ToInt64();
        if (!_uiOffsets.TryGetValue(taskList, out uiOff))
        {
            uiOff = Array.IndexOf(_remote.Qs(obj, 0x300 / 8), _offsets.VtTaskListUI) * 8;
            if (uiOff < 0)
            {
                throw new ProbeException($"ITaskListUI vtable not found in CTaskListWnd of tray 0x{tray.ToInt64():X}");
            }
            _uiOffsets[taskList] = uiOff;
        }
        return obj + uiOff;
    }

    /// <summary>The CodeView record of a loaded module: the name, GUID and age of its PDB.</summary>
    static (string pdb, Guid guid, int age) CodeView(Remote r, long b)
    {
        long nt = b + r.D(b + 0x3C);
        if (r.D(nt) != 0x4550)
        {
            throw new ProbeException("taskbar.dll: no PE signature");
        }
        long dirs = nt + 4 + 20 + 112;  // PE32+ data directories
        int dbgRva = r.D(dirs + 6 * 8), dbgSize = r.D(dirs + 6 * 8 + 4);
        for (int off = 0; off + 28 <= dbgSize; off += 28)
        {
            long e = b + dbgRva + off;
            int size = r.D(e + 16);
            if (r.D(e + 12) != 2 || size < 25 || size > 1024)  // IMAGE_DEBUG_TYPE_CODEVIEW
            {
                continue;
            }
            var cv = r.Bytes(b + r.D(e + 20), size);
            if (cv[0] != 'R' || cv[1] != 'S' || cv[2] != 'D' || cv[3] != 'S')
            {
                continue;
            }
            int end = Array.IndexOf(cv, (byte)0, 24);
            string path = Encoding.UTF8.GetString(cv, 24, (end < 0 ? size : end) - 24);
            return (System.IO.Path.GetFileName(path), new Guid(cv.AsSpan(4, 16)), BitConverter.ToInt32(cv, 20));
        }
        throw new ProbeException("taskbar.dll: no CodeView record");
    }

    /// <summary>The vtables and fields of the shell's taskbar (a read attaches, and finds the fields).</summary>
    public TaskbarOffsets Offsets()
    {
        Read();
        return _offsets;
    }

    public string Describe()
    {
        var o = Offsets();
        return $"shell pid={Pid} {_moduleCheck}; fields: {o.Fields}";
    }

    public ProbeSnapshot Read()
    {
        Exception last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                // Strict first (an unexpected object is usually a torn read while Explorer edits a
                // list, so read again); the last attempt records unexpected objects as warnings.
                return ReadOnce(lenient: attempt == 3);
            }
            catch (ProbeException e)
            {
                last = e;
                _uiOffsets.Clear();
                System.Threading.Thread.Sleep(5);
            }
        }
        throw new ProbeException("probe failed 4 times: " + last?.Message);
    }

    ProbeSnapshot ReadOnce(bool lenient)
    {
        var sw = Stopwatch.StartNew();
        int pid = ShellPid();
        if (pid == 0)
        {
            throw new ProbeException("no Shell_TrayWnd");
        }
        Attach(pid);
        var snap = new ProbeSnapshot { Pid = pid };
        var o = _offsets;

        foreach (var (tray, cls) in Trays(pid))
        {
            var tb = new ProbeTaskbar { Tray = tray, TrayClass = cls };
            Win32.GetWindowRect(tray, out tb.TrayRect);
            var rect = tb.TrayRect;
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            Win32.GetMonitorInfoW(Win32.MonitorFromRect(ref rect, Win32.MONITOR_DEFAULTTONEAREST), ref mi);
            tb.Monitor = mi.szDevice;
            tb.TaskList = FindDescendant(tray, "MSTaskListWClass");
            if (tb.TaskList == IntPtr.Zero)
            {
                snap.Warnings.Add($"{cls} 0x{tray.ToInt64():X}: no MSTaskListWClass");
                snap.Taskbars.Add(tb);
                continue;
            }

            long ui = TaskListUi(tray, tb.TaskList, out tb.UiOffset);
            var bgs = _remote.Dpa(_remote.Q(ui + o.ButtonGroupsOff), 1024);
            for (int i = 0; i < bgs.Length; i++)
            {
                long bg = bgs[i];
                if (_remote.Q(bg) != o.VtTaskBtnGroup)
                {
                    if (!lenient)
                    {
                        throw new ProbeException($"unexpected object 0x{bg:X} in the button group list");
                    }
                    snap.Warnings.Add($"unexpected object 0x{bg:X} in the button group list of {cls}");
                    continue;
                }
                var pb = new ProbeButtonGroup { Ptr = bg, Index = i, GroupPtr = _remote.Q(bg + o.BtnGroupGroupOff) };
                foreach (long btn in _remote.Dpa(_remote.Q(bg + o.BtnGroupButtonsOff), 4096))
                {
                    long vt = _remote.Q(btn);
                    if (vt != o.VtTaskButton && vt != o.VtTaskButtonUnknown)
                    {
                        snap.Warnings.Add($"unexpected button object 0x{btn:X} (vtable rva 0x{vt - _base:X}) on {cls}");
                        pb.Items.Add(0);
                        pb.Hwnds.Add(IntPtr.Zero);
                        continue;
                    }
                    long item = _remote.Q(btn + o.ButtonItemOff);
                    pb.Items.Add(item);
                    pb.Hwnds.Add(WindowOf(item));
                }
                tb.ButtonGroups.Add(pb);

                if (!snap.Groups.ContainsKey(pb.GroupPtr))
                {
                    if (_remote.Q(pb.GroupPtr) != o.VtTaskGroup)
                    {
                        if (!lenient)
                        {
                            throw new ProbeException($"unexpected task group object 0x{pb.GroupPtr:X}");
                        }
                        snap.Warnings.Add($"unexpected task group object 0x{pb.GroupPtr:X} on {cls}");
                        snap.Groups[pb.GroupPtr] = new ProbeGroup { Ptr = pb.GroupPtr };
                        continue;
                    }
                    var g = new ProbeGroup { Ptr = pb.GroupPtr };
                    foreach (long item in _remote.Dpa(_remote.Q(pb.GroupPtr + o.GroupItemsOff), 4096))
                    {
                        g.Items.Add(item);
                        g.Hwnds.Add(WindowOf(item));
                    }
                    snap.Groups[pb.GroupPtr] = g;
                }
            }
            snap.Taskbars.Add(tb);
        }

        // Consistency: every button belongs to an item of its group's list, in list order.
        foreach (var tb in snap.Taskbars)
        {
            foreach (var bg in tb.ButtonGroups)
            {
                var g = snap.Groups[bg.GroupPtr];
                int last = -1;
                foreach (long item in bg.Items)
                {
                    int idx = g.Items.IndexOf(item);
                    if (idx < 0)
                    {
                        snap.Warnings.Add($"button without item in group 0x{g.Ptr:X} on {tb.TrayClass}");
                        break;
                    }
                    if (idx < last)
                    {
                        snap.Warnings.Add($"buttons of group 0x{g.Ptr:X} on {tb.TrayClass} are out of item-list order");
                        break;
                    }
                    last = idx;
                }
            }
        }

        snap.ReadMs = sw.Elapsed.TotalMilliseconds;
        return snap;
    }

    /// <summary>The window of a desktop window's item; zero for any other item.</summary>
    IntPtr WindowOf(long item) =>
        _remote.Q(item) == _offsets.VtWindowTaskItem ? (IntPtr)_remote.Q(item + _offsets.WindowItemHwndOff) : IntPtr.Zero;

    static List<(IntPtr, string)> Trays(int pid)
    {
        var list = new List<(IntPtr, string)>();
        Win32.EnumWindows((h, l) =>
        {
            string cls = Win32.ClassOf(h);
            if ((cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") && Win32.PidOf(h) == pid)
            {
                list.Add((h, cls));
            }
            return true;
        }, IntPtr.Zero);
        // Primary first, then by the left edge of the tray.
        return list.OrderBy(t => t.Item2 == "Shell_TrayWnd" ? 0 : 1)
                   .ThenBy(t => { Win32.GetWindowRect(t.Item1, out RECT r); return r.Left; }).ToList();
    }

    static IntPtr FindDescendant(IntPtr parent, string cls)
    {
        IntPtr found = IntPtr.Zero;
        Win32.EnumChildWindows(parent, (h, l) =>
        {
            if (Win32.ClassOf(h) == cls)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public void Dispose()
    {
        _remote?.Dispose();
        _remote = null;
    }
}
