// The monitors, left to right, and the virtual desktops (from the registry). Read-only.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TsoShared;

namespace TsoLive;

public sealed class MonitorInfo
{
    public IntPtr Handle;
    public string Device;
    public RECT Bounds, Work;
    public bool Primary;
    public int Index;
    /// <summary>The DXGI adapter and output that show the monitor (what ffmpeg's ddagrab captures), once
    /// Monitors.FindDxgiOutputs has run; -1 when none does.</summary>
    public int DxgiAdapter = -1, DxgiOutput = -1;

    public override string ToString() => $"#{Index} {Device}{(Primary ? " primary" : "")} bounds={Bounds} work={Work}";

    /// <summary>A frame at fractions of the work area (x, y are fractions; w, h pixels).</summary>
    public RECT At(double fx, double fy, int w, int h)
    {
        int x = Work.Left + (int)Math.Round(fx * Work.Width);
        int y = Work.Top + (int)Math.Round(fy * Work.Height);
        x = Math.Min(x, Work.Right - w - 20);
        y = Math.Min(y, Work.Bottom - h - 20);
        return new RECT(x, y, x + w, y + h);
    }
}

public static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            Win32.GetMonitorInfoW(h, ref mi);
            list.Add(new MonitorInfo { Handle = h, Device = mi.szDevice, Bounds = mi.rcMonitor, Work = mi.rcWork, Primary = (mi.dwFlags & 1) != 0 });
            return true;
        }, IntPtr.Zero);
        list = list.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Index = i;
        }
        return list;
    }

    public static MonitorInfo Of(RECT r, List<MonitorInfo> all)
    {
        IntPtr h = Win32.MonitorFromRect(ref r, Win32.MONITOR_DEFAULTTONEAREST);
        return all.FirstOrDefault(m => m.Handle == h);
    }

    const string VdKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    /// <summary>The virtual desktops in order and the current one, from the registry.</summary>
    public static (List<Guid> desktops, Guid current) VirtualDesktops()
    {
        using var k = Registry.CurrentUser.OpenSubKey(VdKey);
        var ids = new List<Guid>();
        if (k?.GetValue("VirtualDesktopIDs") is byte[] b)
        {
            for (int i = 0; i + 16 <= b.Length; i += 16)
            {
                ids.Add(new Guid(b.AsSpan(i, 16)));
            }
        }
        Guid cur = k?.GetValue("CurrentVirtualDesktop") is byte[] c && c.Length == 16 ? new Guid(c) : Guid.Empty;
        return (ids, cur);
    }

    // ------------------------------------------------------------------ DXGI outputs (for the recorder)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public RECT DesktopCoordinates;
        public int AttachedToDesktop, Rotation;
        public IntPtr Monitor;
    }

    // Only the methods called are declared with their signatures; the ones before them hold their
    // places in the vtable.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter adapter);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    /// <summary>Sets each monitor's DXGI adapter and output, in the order DXGI enumerates them.</summary>
    public static void FindDxgiOutputs(List<MonitorInfo> monitors)
    {
        Guid iid = typeof(IDXGIFactory1).GUID;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out IDXGIFactory1 factory));
        for (uint a = 0; factory.EnumAdapters(a, out IDXGIAdapter adapter) >= 0; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output) >= 0; o++)
            {
                output.GetDesc(out DXGI_OUTPUT_DESC d);
                var m = monitors.FirstOrDefault(x => x.Handle == d.Monitor);
                if (m != null && m.DxgiAdapter < 0)
                {
                    (m.DxgiAdapter, m.DxgiOutput) = ((int)a, (int)o);
                }
                Marshal.ReleaseComObject(output);
            }
            Marshal.ReleaseComObject(adapter);
        }
        Marshal.ReleaseComObject(factory);
    }
}
