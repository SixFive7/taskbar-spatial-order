// Microsoft's public symbols for taskbar.dll, the way the mod finds its functions through Windhawk:
// the PDB that matches the loaded copy is downloaded once from the Microsoft symbol server into
// .work\test\symbols, then read with the dbghelp.dll that comes with Windows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace TsoLive;

public static class TaskbarSymbols
{
    /// <summary>.work\test\symbols: this assembly is in .work\test\bin\&lt;project&gt;.</summary>
    public static readonly string Folder =
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(TaskbarSymbols).Assembly.Location)!, "..", "..", "symbols"));

    static readonly Dictionary<string, long[]> Cache = new();
    static readonly object Lock = new();

    /// <summary>The RVAs of the named (decorated) public symbols in the PDB with this name, GUID and age.</summary>
    public static long[] Rvas(string pdbName, Guid guid, int age, string[] names)
    {
        string key = guid.ToString("N").ToUpperInvariant() + age.ToString("X");
        lock (Lock)
        {
            if (!Cache.TryGetValue(key, out long[] rvas))
            {
                Cache[key] = rvas = Lookup(Download(pdbName, key), names);
            }
            return rvas;
        }
    }

    static string Download(string pdbName, string key)
    {
        string path = Path.Combine(Folder, pdbName, key, pdbName);
        if (File.Exists(path))
        {
            return path;
        }
        string url = $"https://msdl.microsoft.com/download/symbols/{pdbName}/{key}/{pdbName}";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            byte[] bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".part", bytes);
            File.Move(path + ".part", path, true);
            return path;
        }
        catch (Exception e)
        {
            throw new ProbeException($"the symbols of taskbar.dll could not be downloaded from {url}: {e.Message}");
        }
    }

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SymInitializeW(IntPtr handle, string searchPath, bool invade);
    [DllImport("dbghelp.dll")]
    static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ulong SymLoadModuleExW(IntPtr handle, IntPtr file, string image, string module, ulong baseOfDll, uint size, IntPtr data, uint flags);
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SymFromNameW(IntPtr handle, string name, IntPtr symbolInfo);
    [DllImport("dbghelp.dll")]
    static extern bool SymCleanup(IntPtr handle);

    const uint SYMOPT_PUBLICS_ONLY = 0x4000, SYMOPT_NO_PROMPTS = 0x80000;
    // SYMBOL_INFOW: SizeOfStruct at 0, Address at 56, MaxNameLen at 80, the name from 84 (88 bytes without it).
    const int InfoSize = 88, AddressAt = 56, MaxNameLenAt = 80, MaxName = 2000;

    /// <summary>Loads the PDB on its own (no image needed) at a made-up base and looks the names up.</summary>
    static long[] Lookup(string pdb, string[] names)
    {
        var handle = new IntPtr(0x7A5B);  // dbghelp only needs a unique value here, not a process handle
        const ulong Base = 0x10000000;
        SymSetOptions(SYMOPT_PUBLICS_ONLY | SYMOPT_NO_PROMPTS);
        if (!SymInitializeW(handle, null, false))
        {
            throw new ProbeException($"dbghelp: SymInitialize failed ({Marshal.GetLastWin32Error()})");
        }
        IntPtr info = Marshal.AllocHGlobal(InfoSize + 2 * MaxName);
        try
        {
            if (SymLoadModuleExW(handle, IntPtr.Zero, pdb, null, Base, 0x10000000, IntPtr.Zero, 0) == 0)
            {
                throw new ProbeException($"dbghelp could not load {pdb} ({Marshal.GetLastWin32Error()})");
            }
            var rvas = new long[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                Marshal.WriteInt32(info, 0, InfoSize);
                Marshal.WriteInt32(info, MaxNameLenAt, MaxName);
                if (!SymFromNameW(handle, names[i], info))
                {
                    throw new ProbeException($"{names[i]} is not in the symbols of this taskbar.dll ({pdb})");
                }
                rvas[i] = Marshal.ReadInt64(info, AddressAt) - (long)Base;
            }
            return rvas;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            SymCleanup(handle);
        }
    }
}
