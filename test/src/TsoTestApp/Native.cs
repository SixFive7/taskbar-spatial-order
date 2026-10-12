// Win32 and COM declarations used only by the test app.
using System;
using System.Runtime.InteropServices;
using TsoShared;

namespace TsoTestApp;

delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct WNDCLASSEX
{
    public int cbSize;
    public uint style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public IntPtr hInstance, hIcon, hCursor, hbrBackground;
    public string lpszMenuName, lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam, lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
struct PAINTSTRUCT
{
    public IntPtr hdc;
    public bool fErase;
    public RECT rcPaint;
    public bool fRestore, fIncUpdate;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
}

[StructLayout(LayoutKind.Sequential)]
struct ICONINFO
{
    public bool fIcon;
    public int xHotspot, yHotspot;
    public IntPtr hbmMask, hbmColor;
}

[StructLayout(LayoutKind.Sequential)]
struct BITMAPINFOHEADER
{
    public int biSize, biWidth, biHeight;
    public short biPlanes, biBitCount;
    public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
}

static class Native
{
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WM_DESTROY = 0x2, WM_PAINT = 0xF, WM_CLOSE = 0x10, WM_ERASEBKGND = 0x14, WM_SETICON = 0x80,
        WM_NCHITTEST = 0x84, WM_COMMAND = 0x111, WM_SYSCOMMAND = 0x112, WM_TIMER = 0x113, WM_APP = 0x8000;
    public const int HTCAPTION = 2;
    public const int SC_KEYMENU = 0xF100;
    public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
    public const uint MSGFLT_ALLOW = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string s);
    [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetWindowTextW(IntPtr h, string s);
    [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr hdc, string s, int n, ref RECT r, uint fmt);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr id);
    [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO ii);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int bgr);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern int SetTextColor(IntPtr hdc, int bgr);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[] bits);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll")] public static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pv);

    public static int Bgr(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    /// <summary>A solid-colour icon with an optional darker square in the middle, drawn in a 32-bpp DIB.</summary>
    public static IntPtr SolidIcon(int size, int rgb, bool inner = false)
    {
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        int px = unchecked((int)0xFF000000) | rgb;
        int dark = unchecked((int)0xFF000000) | ((rgb >> 1) & 0x7F7F7F);
        var row = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool middle = inner && x >= size / 4 && x < size * 3 / 4 && y >= size / 4 && y < size * 3 / 4;
                row[y * size + x] = middle ? dark : px;
            }
        }
        Marshal.Copy(row, 0, bits, row.Length);
        IntPtr mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
        var ii = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
        IntPtr icon = CreateIconIndirect(ref ii);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }
}

[StructLayout(LayoutKind.Sequential)]
struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Sequential)]
struct PROPVARIANT
{
    public ushort vt;
    public ushort r1, r2, r3;
    public IntPtr p;
    public IntPtr p2;
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    [PreserveSig] int Commit();
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct THUMBBUTTON
{
    public uint dwMask;
    public uint iId;
    public uint iBitmap;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
    public uint dwFlags;
}

[ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ITaskbarList3
{
    [PreserveSig] int HrInit();
    [PreserveSig] int AddTab(IntPtr hwnd);
    [PreserveSig] int DeleteTab(IntPtr hwnd);
    [PreserveSig] int ActivateTab(IntPtr hwnd);
    [PreserveSig] int SetActiveAlt(IntPtr hwnd);
    [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
    [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
    [PreserveSig] int RegisterTab(IntPtr tab, IntPtr mdi);
    [PreserveSig] int UnregisterTab(IntPtr tab);
    [PreserveSig] int SetTabOrder(IntPtr tab, IntPtr insertBefore);
    [PreserveSig] int SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
    [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
    [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
    [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
    [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
    [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
    [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr clip);
}

[ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
    [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
}

static class ComIds
{
    public static readonly Guid CLSID_TaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    public static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    public static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    public static PROPERTYKEY PKEY_AppUserModel_ID = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
    public const ushort VT_EMPTY = 0, VT_LPWSTR = 31;
    public const int TBPF_NOPROGRESS = 0, TBPF_INDETERMINATE = 1, TBPF_NORMAL = 2, TBPF_ERROR = 4, TBPF_PAUSED = 8;
    public const uint THB_ICON = 2, THB_TOOLTIP = 4, THB_FLAGS = 8;
    public const int THBN_CLICKED = 0x1800;
}
