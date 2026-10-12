#Requires -Version 7.0
<#
.SYNOPSIS
    The Windhawk side of the live test. Dot-source this file (Invoke-LiveTest.ps1 does).

.DESCRIPTION
    The functions the test runner calls (Remove-LocalMod and Exit-SafeMode are for use by hand):

      Install-WindhawkAlpha   records the installed version, then upgrades to the pinned 2.0 alpha
      Install-LocalMod        compiles and installs a .wh.cpp as local@<id> (also a fast reinstall)
      Enable-Mod, Disable-Mod, Remove-LocalMod
      Set-ModSetting          one setting, or a whole array such as excludedApps
      Set-ModLogging          the mod's log on or off
      Start-ModLogCapture, Stop-ModLogCapture
                              the mod's log into a file, read from the debug output (no window)
      Get-WindhawkState       versions, the engine in Explorer, the service, safe mode, the mods (read-only)
      Enter-SafeMode, Exit-SafeMode
      Restore-WindhawkStable  back to the version Install-WindhawkAlpha recorded

    Most of them use windhawk-cli, which comes with Windhawk 2.0. On 1.x only Get-WindhawkState, the
    log capture and Enter-SafeMode -Panic work.

    Whatever changes Windhawk needs an elevated PowerShell: Windhawk keeps its state in HKLM,
    Program Files and ProgramData and runs as a service. Whatever these functions write goes to
    .work\test\windhawk: the installers, the record of the version before the upgrade, a backup
    before each restore, and a log.

PANIC PROCEDURE (Explorer keeps crashing once the mod is enabled)
    1. Wait a few seconds: after two Explorer crashes within a minute, Windhawk's tray process opens
       its toolkit window by itself. Choose Safe mode there (Ctrl+Win+W opens the same window).
    2. From any PowerShell, elevated or not:
           . .\windhawk.ps1; Enter-SafeMode -Panic
       or without this file:
           Add-Type -Namespace P -Name K -MemberDefinition '[DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern System.IntPtr OpenEventW(uint a,bool i,string n); [DllImport("kernel32.dll")] public static extern bool SetEvent(System.IntPtr h);'
           [P.K]::SetEvent([P.K]::OpenEventW(2,$false,'Global\WindhawkServiceSafeModeStopEvent'))
       The service then turns safe mode on, stops, and every mod unloads.
    3. No taskbar at all: Ctrl+Shift+Esc > Run new task > "cmd", with "Create this task with
       administrative privileges" ticked, then
           reg add HKLM\SOFTWARE\Windhawk\Settings /v SafeMode /t REG_DWORD /d 1 /f
           sc stop Windhawk
       (or only the mod: reg add "HKLM\SOFTWARE\Windhawk\Engine\Mods\local@taskbar-spatial-order" /v Disabled /t REG_DWORD /d 1 /f)
       Explorer comes back by itself once nothing crashes it; otherwise run explorer.exe from the same box.
    4. Last resort: start Windows in safe mode, where the Windhawk service does not run.
    Back to normal after a fix: Install-LocalMod with the fixed source, then Exit-SafeMode.
#>

$script:Wh = [ordered]@{
    Work       = Join-Path (Split-Path (Split-Path $PSScriptRoot)) '.work\test\windhawk'
    InstallDir = Join-Path $env:ProgramFiles 'Windhawk'
    # The 2.0 alpha the kit is tested with: the online installer of its GitHub release, and its SHA-256.
    Alpha      = [ordered]@{
        Version = '2.0.0-alpha.7'
        Url     = 'https://github.com/ramensoftware/windhawk/releases/download/2.0.0-alpha.7/windhawk_setup.exe'
        Sha256  = '9000FF70F50CEB850F66BB7C477502023E0C0AEA8D8752F329DFC2F7F1F7365A'
    }
    Releases   = 'https://api.github.com/repos/ramensoftware/windhawk/releases/tags/'
    Publisher  = 'Michael Maltsev'   # in the subject of the certificate Windhawk's installers are signed with
}
$script:WhRecord = Join-Path $script:Wh.Work 'before-upgrade.json'

# ---------------------------------------------------------------------------------------------------------------
# Native helpers: the debug-output capture, named events, the taskbar's process
# ---------------------------------------------------------------------------------------------------------------

$script:WhNativeSource = @'
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TsoWindhawk
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileMappingW(IntPtr file, ref SECURITY_ATTRIBUTES sa, uint protect, uint maxHigh, uint maxLow, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offHigh, uint offLow, UIntPtr bytes);
        [DllImport("kernel32.dll")] public static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool SetEvent(IntPtr h);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenEventW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll")]
        public static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] src, int srcLen, [Out] char[] dst, int dstLen);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr size);
        [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        /// <summary>0, or the Win32 error (2: no such event, so the service is not running).</summary>
        public static int SignalNamedEvent(string name)
        {
            IntPtr h = OpenEventW(0x0002 /* EVENT_MODIFY_STATE */, false, name);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error();
            try { return SetEvent(h) ? 0 : Marshal.GetLastWin32Error(); }
            finally { CloseHandle(h); }
        }

        public static bool NamedMappingExists(string name)
        {
            IntPtr h = OpenFileMappingW(4 /* FILE_MAP_READ */, false, name);
            if (h == IntPtr.Zero) return false;
            CloseHandle(h);
            return true;
        }

        public static int TaskbarProcessId()
        {
            IntPtr hwnd = FindWindowW("Shell_TrayWnd", null);
            uint pid = 0;
            if (hwnd != IntPtr.Zero) GetWindowThreadProcessId(hwnd, out pid);
            return (int)pid;
        }
    }

    // A debug-output (DBWIN) monitor: OutputDebugString is how Wh_Log output leaves Explorer, as
    // "[WH] [<mod id>] <text>". The security descriptor is the one Windhawk's own log window uses
    // (Windhawk, GPL-3.0, https://github.com/ramensoftware/windhawk). The reader copies a message
    // and releases the writer before anything else, because while this monitor exists, every
    // program's OutputDebugString waits for it (up to 10 s).
    public sealed class DbwinCapture : IDisposable
    {
        const string Sddl = "D:(A;;GRGWGX;;;WD)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGWGX;;;AN)(A;;GRGWGX;;;RC)(A;;GRGWGX;;;S-1-15-2-1)S:(ML;;NW;;;LW)";
        const uint PAGE_READWRITE = 4, FILE_MAP_READ = 4, WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102, CP_ACP = 0;
        const int ERROR_ALREADY_EXISTS = 183, BufSize = 4096;

        public static DbwinCapture Current;

        IntPtr _bufferReady, _dataReady, _mapping, _view;
        readonly BlockingCollection<Tuple<DateTime, int, string>> _queue = new BlockingCollection<Tuple<DateTime, int, string>>(200000);
        Thread _reader, _writer;
        volatile bool _stop;
        bool _disposed;
        readonly string _modFilter;
        public readonly string OutFile;
        public long Seen, Written, Dropped;
        public DateTime StartedAt { get; private set; }
        public string LastError { get; private set; }
        public bool IsRunning { get { return !_disposed && _reader != null && _reader.IsAlive; } }

        DbwinCapture(string outFile, string modFilter)
        {
            OutFile = outFile;
            _modFilter = string.IsNullOrEmpty(modFilter) ? null : "[WH] [" + modFilter + "]";
        }

        public static DbwinCapture Start(string outFile, string modFilter)
        {
            if (Current != null && !Current._disposed)
                throw new InvalidOperationException("A capture is already running into " + Current.OutFile);
            var c = new DbwinCapture(outFile, modFilter);
            c.Open(@"Local\");
            c.StartedAt = DateTime.Now;
            c._writer = new Thread(c.WriteLoop) { IsBackground = true, Name = "DBWIN writer" };
            c._reader = new Thread(c.ReadLoop) { IsBackground = true, Name = "DBWIN reader", Priority = ThreadPriority.AboveNormal };
            c._writer.Start();
            c._reader.Start();
            Current = c;
            return c;
        }

        IntPtr Checked(IntPtr h, string what)
        {
            int err = Marshal.GetLastWin32Error();
            if (h == IntPtr.Zero) throw new Win32Exception(err, "Creating " + what + " failed");
            if (err == ERROR_ALREADY_EXISTS)
            {
                Native.CloseHandle(h);
                throw new InvalidOperationException(what + " already exists: another debug-output monitor has it " +
                    "(DebugView, Windhawk's log window or editor, or a debugger). Close it and try again.");
            }
            return h;
        }

        void Open(string prefix)
        {
            IntPtr sd;
            if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out sd, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Building the DBWIN security descriptor failed");
            try
            {
                var sa = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(Native.SECURITY_ATTRIBUTES)), lpSecurityDescriptor = sd };
                _bufferReady = Checked(Native.CreateEventW(ref sa, false, false, prefix + "DBWIN_BUFFER_READY"), prefix + "DBWIN_BUFFER_READY");
                _dataReady = Checked(Native.CreateEventW(ref sa, false, false, prefix + "DBWIN_DATA_READY"), prefix + "DBWIN_DATA_READY");
                _mapping = Checked(Native.CreateFileMappingW(new IntPtr(-1), ref sa, PAGE_READWRITE, 0, BufSize, prefix + "DBWIN_BUFFER"), prefix + "DBWIN_BUFFER");
                _view = Native.MapViewOfFile(_mapping, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
                if (_view == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "MapViewOfFile failed");
            }
            catch { CloseAll(); throw; }
            finally { Native.LocalFree(sd); }
        }

        void ReadLoop()
        {
            var buf = new byte[BufSize - 4];
            Native.SetEvent(_bufferReady);
            try
            {
                while (!_stop)
                {
                    uint w = Native.WaitForSingleObject(_dataReady, 200);
                    if (w == WAIT_TIMEOUT) continue;
                    if (w != WAIT_OBJECT_0) { LastError = "WaitForSingleObject returned " + w; break; }
                    int pid = Marshal.ReadInt32(_view);
                    Marshal.Copy(IntPtr.Add(_view, 4), buf, 0, buf.Length);
                    Native.SetEvent(_bufferReady);
                    Interlocked.Increment(ref Seen);
                    int len = Array.IndexOf(buf, (byte)0);
                    if (len < 0) len = buf.Length;
                    if (len < 5 || buf[0] != '[' || buf[1] != 'W' || buf[2] != 'H' || buf[3] != ']' || buf[4] != ' ') continue;
                    string msg = Decode(buf, len);
                    if (_modFilter != null && !msg.StartsWith(_modFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_queue.TryAdd(Tuple.Create(DateTime.Now, pid, msg))) Interlocked.Increment(ref Dropped);
                }
            }
            catch (Exception e) { LastError = e.Message; }
        }

        static string Decode(byte[] buf, int len)
        {
            var chars = new char[len * 2];
            int n = Native.MultiByteToWideChar(CP_ACP, 0, buf, len, chars, chars.Length);
            return n > 0 ? new string(chars, 0, n) : Encoding.UTF8.GetString(buf, 0, len);
        }

        void WriteLoop()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutFile)));
                using (var fs = new FileStream(OutFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.WriteLine("# capture started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + ", filter " + (_modFilter ?? "[WH] "));
                    sw.Flush();
                    foreach (var item in _queue.GetConsumingEnumerable())
                    {
                        sw.Write(item.Item1.ToString("HH:mm:ss.fff")); sw.Write(' ');
                        sw.Write(item.Item2); sw.Write(' ');
                        sw.Write(ProcessName(item.Item2)); sw.Write("  ");
                        sw.WriteLine(item.Item3.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').TrimEnd());
                        Interlocked.Increment(ref Written);
                        if (_queue.Count == 0) sw.Flush();
                    }
                    sw.WriteLine("# capture stopped " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + ", lines " + Written +
                        ", debug messages seen " + Seen + ", dropped " + Dropped);
                }
            }
            catch (Exception e) { LastError = "writer: " + e.Message; }
        }

        static string ProcessName(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName + ".exe"; }
            catch { return "<exited>"; }
        }

        void CloseAll()
        {
            if (_view != IntPtr.Zero) { Native.UnmapViewOfFile(_view); _view = IntPtr.Zero; }
            foreach (var h in new[] { _mapping, _dataReady, _bufferReady }) if (h != IntPtr.Zero) Native.CloseHandle(h);
            _mapping = _dataReady = _bufferReady = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop = true;
            if (_reader != null) _reader.Join(3000);
            CloseAll();
            _queue.CompleteAdding();
            if (_writer != null) _writer.Join(10000);
            if (Current == this) Current = null;
        }
    }
}
'@

function Initialize-WhNative {
    if (-not ('TsoWindhawk.DbwinCapture' -as [type])) { Add-Type -TypeDefinition $script:WhNativeSource -Language CSharp }
}

# ---------------------------------------------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------------------------------------------

function Write-WhLog {
    param([string]$Message)
    [void][IO.Directory]::CreateDirectory($script:Wh.Work)
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Message
    [IO.File]::AppendAllText((Join-Path $script:Wh.Work 'windhawk.log'), $line + [Environment]::NewLine)
    Write-Verbose $line
}

# A property or $null, also for a missing property or a $null object under Set-StrictMode -Version Latest.
function Get-WhProp {
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { if ($Object.Contains($Name)) { return $Object[$Name] } else { return $null } }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value } else { return $null }
}

# A dotted path (data.config.disabled) into windhawk-cli's JSON, or $null if any part is missing: the
# command line leaves empty fields out (warnings, changes, ...).
function Get-WhJson {
    param($Object, [Parameter(Mandatory)][string]$Path)
    foreach ($name in $Path.Split('.')) { $Object = Get-WhProp $Object $name }
    return $Object
}

function Test-WhElevated {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-WhElevated {
    if (-not (Test-WhElevated)) { throw 'This changes Windhawk, which needs an elevated PowerShell (Run as administrator).' }
}

# Runs a program without a window and waits for it. Console tools: the arguments one by one, the
# output captured. Installers (-NoCapture): the command line as given (NSIS reads "_?=<dir>" unquoted).
function Invoke-WhProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$CommandLine,
        [int]$TimeoutSec = 120,
        [switch]$NoCapture
    )
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $psi = [Diagnostics.ProcessStartInfo]::new($FilePath)
    if ($CommandLine) { $psi.Arguments = $CommandLine } else { foreach ($a in $ArgumentList) { $psi.ArgumentList.Add($a) } }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if (-not $NoCapture) {
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    }
    $p = [Diagnostics.Process]::Start($psi)
    $out = $err = $null
    if (-not $NoCapture) { $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync() }
    $exited = $p.WaitForExit($TimeoutSec * 1000)
    if (-not $exited) { try { $p.Kill($true) } catch { } }
    if ($out) { [void][Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($out, $err), 5000) }   # a grandchild may hold the pipes
    $r = [pscustomobject]@{
        ExitCode   = $(if ($exited) { $p.ExitCode } else { $null })
        StdOut     = $(if ($out -and $out.IsCompleted) { $out.Result } else { '' })
        StdErr     = $(if ($err -and $err.IsCompleted) { $err.Result } else { '' })
        TimedOut   = -not $exited
        DurationMs = $sw.ElapsedMilliseconds
    }
    Write-WhLog ('run "{0}" {1} -> exit {2}{3} in {4} ms' -f $FilePath, $(if ($CommandLine) { $CommandLine } else { $ArgumentList -join ' ' }),
                 $r.ExitCode, $(if ($r.TimedOut) { ' TIMEOUT (killed)' }), $r.DurationMs)
    return $r
}

function Get-WhCliPath { Join-Path $script:Wh.InstallDir 'windhawk-cli.exe' }

# Runs windhawk-cli --json and returns its parsed envelope; throws with the command line's own error.
function Invoke-WhCli {
    param([Parameter(Mandatory)][string[]]$Arguments, [int]$TimeoutSec = 120)
    Assert-WhElevated
    $cli = Get-WhCliPath
    if (-not (Test-Path -LiteralPath $cli)) { throw "windhawk-cli.exe is not in $($script:Wh.InstallDir): it comes with Windhawk 2.0 (Install-WindhawkAlpha)." }
    $r = Invoke-WhProcess -FilePath $cli -ArgumentList (@('--json') + $Arguments) -TimeoutSec $TimeoutSec
    if ($r.TimedOut) { throw "windhawk-cli timed out after $TimeoutSec s: $($Arguments -join ' ')" }
    $resp = $null
    try { $resp = $r.StdOut | ConvertFrom-Json -Depth 32 } catch { }
    if (-not $resp) { throw "windhawk-cli $($Arguments -join ' ') exited $($r.ExitCode) without JSON. stdout: $($r.StdOut.Trim()) stderr: $($r.StdErr.Trim())" }
    if (-not (Get-WhJson $resp 'success')) {
        $code = Get-WhJson $resp 'error.code'
        $ex = [InvalidOperationException]::new("windhawk-cli $($Arguments -join ' ') failed (exit $($r.ExitCode), $code): $(Get-WhJson $resp 'error.message')" +
                                               $(if ($r.StdErr.Trim()) { "`n--- stderr ---`n" + $r.StdErr.Trim() }))
        $ex.Data['ErrorCode'] = $code
        throw $ex
    }
    $resp | Add-Member -NotePropertyName DurationMs -NotePropertyValue $r.DurationMs -Force
    return $resp
}

function Wait-WhCondition {
    param([Parameter(Mandatory)][scriptblock]$Condition, [int]$TimeoutSec = 30, [int]$IntervalMs = 250)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        try { if (& $Condition) { return [pscustomobject]@{ Met = $true; WaitedMs = $sw.ElapsedMilliseconds } } } catch { }
        Start-Sleep -Milliseconds $IntervalMs
    } while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec)
    return [pscustomobject]@{ Met = $false; WaitedMs = $sw.ElapsedMilliseconds }
}

# The Windhawk engine and mod DLLs inside every explorer.exe. Read-only.
function Get-WhExplorerModules {
    Initialize-WhNative
    $taskbarPid = [TsoWindhawk.Native]::TaskbarProcessId()
    foreach ($p in @(Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        $mods = @(); $err = $null
        try { $mods = @($p.Modules) } catch { $err = $_.Exception.Message }
        $engine = $mods | Where-Object { $_.ModuleName -ieq 'windhawk.dll' } | Select-Object -First 1
        [pscustomobject]@{
            Pid           = $p.Id
            IsTaskbar     = ($p.Id -eq $taskbarPid)
            EnginePath    = Get-WhProp $engine 'FileName'
            EngineVersion = Get-WhProp (Get-WhProp $engine 'FileVersionInfo') 'FileVersion'
            ModDlls       = @($mods | Where-Object { $_.FileName -like '*\Windhawk\Engine\Mods\*' -and $_.ModuleName -like '*.dll' -and $_.ModuleName -notlike 'windhawk-mod-shim*' } |
                              ForEach-Object { $_.ModuleName })
            Error         = $err
        }
        $p.Dispose()
    }
}

function Get-WhTaskbarModules { Get-WhExplorerModules | Where-Object IsTaskbar | Select-Object -First 1 }

# The id Windhawk knows the mod by: as given when that exists, else local@<id> (the kit installs local mods).
function Resolve-WhModId {
    param([Parameter(Mandatory)][string]$Id)
    if ($Id -match '@' -or (Test-Path -LiteralPath "HKLM:\SOFTWARE\Windhawk\Engine\Mods\$Id")) { return $Id }
    return "local@$Id"
}

function Get-WhModRecord {
    param([Parameter(Mandatory)][string]$Id)
    $key = "HKLM:\SOFTWARE\Windhawk\Engine\Mods\$Id"
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $v = Get-ItemProperty -LiteralPath $key
    [pscustomobject]@{
        Id              = $Id
        Version         = Get-WhProp $v 'Version'
        Enabled         = -not [bool](Get-WhProp $v 'Disabled')
        LoggingEnabled  = [bool](Get-WhProp $v 'LoggingEnabled')
        LibraryFileName = Get-WhProp $v 'LibraryFileName'
    }
}

# Waits until the taskbar's explorer.exe has (or no longer has) a mod DLL loaded.
function Wait-WhModInExplorer {
    param([string]$LibraryFileName, [bool]$Loaded = $true, [int]$TimeoutSec = 30)
    if (-not $LibraryFileName -or $TimeoutSec -le 0) { return $null }
    Wait-WhCondition -TimeoutSec $TimeoutSec -IntervalMs 200 -Condition { (@(Get-WhProp (Get-WhTaskbarModules) 'ModDlls') -contains $LibraryFileName) -eq $Loaded }
}

# Windhawk files queued for deletion at the next reboot (an uninstaller's RMDir /REBOOTOK on a file in
# use). A queued file that an install writes again would be deleted at that reboot.
function Get-WhPendingDeletes {
    $v = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations
    $out = for ($i = 0; $v -and $i -lt $v.Count; $i += 2) {
        $src = $v[$i] -replace '^\*\d+', '' -replace '^\\\?\?\\', ''
        if ($src -like '*\Windhawk\*' -and -not ($i + 1 -lt $v.Count -and $v[$i + 1])) { $src }
    }
    return @($out)
}

# A Windhawk installer in .work\test\windhawk\installers, downloaded once and checked every time: the
# pinned alpha against its pinned SHA-256; any other version against the SHA-256 its GitHub release
# publishes. Either must also carry a valid signature of Windhawk's publisher.
function Get-WhInstaller {
    param([Parameter(Mandatory)][string]$Version, [switch]$Offline)
    $name = if ($Offline) { 'windhawk_setup_offline.exe' } else { 'windhawk_setup.exe' }
    if ($Version -eq $script:Wh.Alpha.Version -and -not $Offline) {
        $url = $script:Wh.Alpha.Url
        $sha = $script:Wh.Alpha.Sha256
    }
    else {
        $release = $null
        foreach ($tag in "v$Version", $Version) {   # stable releases are tagged v1.2.3, previews 2.0.0-alpha.7
            try { $release = Invoke-RestMethod -Uri ($script:Wh.Releases + $tag) -TimeoutSec 60; break } catch { }
        }
        $asset = @(Get-WhProp $release 'assets') | Where-Object { (Get-WhProp $_ 'name') -eq $name } | Select-Object -First 1
        if (-not $asset) { throw "Windhawk $Version has no $name on GitHub (or GitHub could not be reached)." }
        $url = $asset.browser_download_url
        $sha = if ((Get-WhProp $asset 'digest') -match '^sha256:([0-9a-fA-F]{64})$') { $Matches[1].ToUpperInvariant() } else { $null }
    }
    $path = Join-Path $script:Wh.Work "installers\$Version\$name"
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
        Write-WhLog "download $url"
        Invoke-WebRequest -Uri $url -OutFile "$path.part" -TimeoutSec 600
        Move-Item -LiteralPath "$path.part" -Destination $path -Force
    }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $sig = Get-AuthenticodeSignature -LiteralPath $path
    $problem = if ($sha -and $hash -ne $sha) { "SHA-256 $hash, expected $sha" }
               elseif ($sig.Status -ne 'Valid') { "signature $($sig.Status)" }
               elseif ($sig.SignerCertificate.Subject -notmatch [regex]::Escape($script:Wh.Publisher)) { "signed by $($sig.SignerCertificate.Subject)" }
    if ($problem) {
        Remove-Item -LiteralPath $path -Force
        throw "The Windhawk installer from $url failed its check ($problem); it was deleted."
    }
    Write-WhLog "installer $path ok: SHA-256 $hash$(if (-not $sha) { ' (no digest published to compare with)' }), signed by $($sig.SignerCertificate.Subject)"
    return [pscustomobject]@{ Path = $path; Sha256 = $hash }
}

# ---------------------------------------------------------------------------------------------------------------
# The functions the runner calls
# ---------------------------------------------------------------------------------------------------------------

function Get-WindhawkState {
    [CmdletBinding()]
    param()
    Initialize-WhNative
    $exe = Join-Path $script:Wh.InstallDir 'windhawk.exe'
    $svc = Get-Service -Name Windhawk -ErrorAction SilentlyContinue
    $taskbar = Get-WhTaskbarModules
    $mods = @()
    if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Windhawk\Engine\Mods') {
        $mods = @(Get-ChildItem -LiteralPath 'HKLM:\SOFTWARE\Windhawk\Engine\Mods' | ForEach-Object {
                $rec = Get-WhModRecord -Id $_.PSChildName
                if ($rec -and $rec.LibraryFileName) {
                    $rec | Add-Member -NotePropertyName LoadedInExplorer -NotePropertyValue (@(Get-WhProp $taskbar 'ModDlls') -contains $rec.LibraryFileName) -PassThru
                }
            })
    }
    $capture = if ('TsoWindhawk.DbwinCapture' -as [type]) { [TsoWindhawk.DbwinCapture]::Current }
    [pscustomobject]@{
        AppVersion            = if (Test-Path -LiteralPath $exe) { (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } else { $null }
        ExplorerEngineVersion = Get-WhProp $taskbar 'EngineVersion'
        ServiceState          = if ($svc) { [string]$svc.Status } else { $null }
        SafeMode              = [bool](Get-WhProp (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Windhawk\Settings' -ErrorAction SilentlyContinue) 'SafeMode')
        Mods                  = $mods
        EditorRunning         = [bool](Get-Process -Name VSCodium -ErrorAction SilentlyContinue)      # the 1.x editor
        UiRunning             = [bool](Get-Process -Name windhawk-ui -ErrorAction SilentlyContinue)   # the 2.0 window
        LogCapture            = if ($capture) { [pscustomobject]@{ OutFile = $capture.OutFile; Running = $capture.IsRunning; Lines = $capture.Written; Dropped = $capture.Dropped } }
        DbwinOwnedByOther     = (-not $capture) -and [TsoWindhawk.Native]::NamedMappingExists('Local\DBWIN_BUFFER')
        Elevated              = Test-WhElevated
    }
}

function Install-WindhawkAlpha {
    <#
    .SYNOPSIS  Records the installed Windhawk version (and fetches its offline installer, the way back), then
               upgrades to the pinned 2.0 alpha with its online installer, silently. Returns when the service
               runs and Explorer has the alpha's engine. Nothing to do on the alpha itself.
    .NOTES     windhawk_setup.exe /S /SKIP_SCHEDULED_TASK_UPDATE /SKIP_INSTALL_WEBVIEW2. The alpha keeps the mods
               and settings of 1.x; WebView2 is only needed by its new window, which the test never opens.
    #>
    [CmdletBinding()]
    param([int]$InstallerTimeoutSec = 600, [int]$ReadyTimeoutSec = 120)
    $alpha = $script:Wh.Alpha.Version
    $before = Get-WindhawkState
    if (-not $before.AppVersion) { throw "Windhawk is not installed in $($script:Wh.InstallDir)." }
    [void][IO.Directory]::CreateDirectory($script:Wh.Work)
    if ($before.AppVersion -eq $alpha) {
        if (-not (Test-Path -LiteralPath $script:WhRecord)) {
            [IO.File]::WriteAllText($script:WhRecord, (@{ Version = $alpha } | ConvertTo-Json))   # nothing to restore later
        }
        Write-WhLog "Install-WindhawkAlpha: already $alpha"
        return $before
    }
    if (-not $before.AppVersion.StartsWith('1.')) {
        throw "Windhawk $($before.AppVersion) is installed. The kit upgrades a 1.x release to $alpha (and back), or runs on $alpha itself."
    }
    Assert-WhElevated
    if ($before.EditorRunning) { throw "Windhawk's mod editor is open, and the upgrade would close it: close it first." }

    # The way back first: if it cannot be had, nothing is changed.
    $back = Get-WhInstaller -Version $before.AppVersion -Offline
    [IO.File]::WriteAllText($script:WhRecord, ([ordered]@{ Version = $before.AppVersion; Installer = $back.Path; Sha256 = $back.Sha256 } | ConvertTo-Json))
    $setup = (Get-WhInstaller -Version $alpha).Path

    Write-WhLog "Install-WindhawkAlpha: from $($before.AppVersion)"
    $r = Invoke-WhProcess -FilePath $setup -CommandLine '/S /SKIP_SCHEDULED_TASK_UPDATE /SKIP_INSTALL_WEBVIEW2' -TimeoutSec $InstallerTimeoutSec -NoCapture
    if ($r.TimedOut) { throw "The installer did not finish within $InstallerTimeoutSec s." }
    if ($r.ExitCode -ne 0) { throw "The installer exited with $($r.ExitCode) (1: cancelled, 2: aborted)." }
    $w = Wait-WhCondition -TimeoutSec $ReadyTimeoutSec -IntervalMs 500 -Condition {
        $s = Get-WindhawkState
        $s.AppVersion -eq $alpha -and $s.ServiceState -eq 'Running' -and $s.ExplorerEngineVersion -eq $alpha
    }
    $state = Get-WindhawkState
    $state | Add-Member -NotePropertyName Ready -NotePropertyValue $w.Met
    Write-WhLog "Install-WindhawkAlpha: ready=$($w.Met) app=$($state.AppVersion) engine=$($state.ExplorerEngineVersion) service=$($state.ServiceState)"
    if (-not $w.Met) { Write-Warning "Installed, but not ready within $ReadyTimeoutSec s: app $($state.AppVersion), service $($state.ServiceState), engine in Explorer $($state.ExplorerEngineVersion)." }
    return $state
}

function Install-LocalMod {
    <# .NOTES windhawk-cli --json mod install --file <Path> --strict [--disabled] #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [switch]$Disabled, [int]$WaitLoadedSec = 30, [int]$TimeoutSec = 300)
    $full = (Resolve-Path -LiteralPath $Path).ProviderPath
    if (-not ([IO.File]::ReadAllText($full) -match '(?m)^//\s*@id\s+(\S+)')) { throw "No // @id in $full" }
    $previous = Get-WhModRecord -Id "local@$($Matches[1])"
    $cliArgs = @('mod', 'install', '--file', $full, '--strict')
    if ($Disabled) { $cliArgs += '--disabled' }
    $res = Invoke-WhCli -Arguments $cliArgs -TimeoutSec $TimeoutSec
    $d = Get-WhJson $res 'data'
    $enabled = -not (Get-WhJson $d 'config.disabled')
    $library = Get-WhJson $d 'config.libraryFileName'
    $wait = if ($enabled) { Wait-WhModInExplorer -LibraryFileName $library -Loaded $true -TimeoutSec $WaitLoadedSec }
    $out = [pscustomobject]@{
        Id               = Get-WhJson $d 'id'
        Version          = Get-WhJson $d 'version'
        LibraryFileName  = $library
        PreviousLibrary  = Get-WhProp $previous 'LibraryFileName'
        Enabled          = $enabled
        Warnings         = Get-WhJson $d 'warnings'
        CliMs            = Get-WhProp $res 'DurationMs'
        LoadedInExplorer = Get-WhProp $wait 'Met'
    }
    Write-WhLog "Install-LocalMod: $($out.Id) $library enabled=$enabled warnings=$([bool]$out.Warnings) loaded=$($out.LoadedInExplorer)"
    return $out
}

function Enable-Mod {
    <# .NOTES windhawk-cli --json mod enable <id>; the engine in Explorer sees the change and loads the mod. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Id, [int]$WaitLoadedSec = 30)
    $Id = Resolve-WhModId -Id $Id
    $res = Invoke-WhCli -Arguments @('mod', 'enable', $Id)
    $wait = Wait-WhModInExplorer -LibraryFileName (Get-WhProp (Get-WhModRecord -Id $Id) 'LibraryFileName') -Loaded $true -TimeoutSec $WaitLoadedSec
    Write-WhLog "Enable-Mod: $Id loaded=$(Get-WhProp $wait 'Met')"
    [pscustomobject]@{ Id = $Id; Changed = Get-WhJson $res 'data.changed'; LoadedInExplorer = Get-WhProp $wait 'Met'; WaitedMs = Get-WhProp $wait 'WaitedMs' }
}

function Disable-Mod {
    <# .NOTES windhawk-cli --json mod disable <id>; the engine unloads the mod (Wh_ModBeforeUninit, Wh_ModUninit). #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Id, [int]$WaitUnloadedSec = 15)
    $Id = Resolve-WhModId -Id $Id
    $library = Get-WhProp (Get-WhModRecord -Id $Id) 'LibraryFileName'
    $res = Invoke-WhCli -Arguments @('mod', 'disable', $Id)
    $wait = Wait-WhModInExplorer -LibraryFileName $library -Loaded $false -TimeoutSec $WaitUnloadedSec
    Write-WhLog "Disable-Mod: $Id unloaded=$(Get-WhProp $wait 'Met')"
    [pscustomobject]@{ Id = $Id; Changed = Get-WhJson $res 'data.changed'; UnloadedFromExplorer = Get-WhProp $wait 'Met'; WaitedMs = Get-WhProp $wait 'WaitedMs' }
}

function Remove-LocalMod {
    <# .NOTES windhawk-cli --json --yes mod remove <id>: the configuration, settings, source and DLLs. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Id, [int]$WaitUnloadedSec = 15)
    $Id = Resolve-WhModId -Id $Id
    if ($Id -notlike 'local@*') { throw "Remove-LocalMod only removes local mods (local@...), not '$Id'." }
    $record = Get-WhModRecord -Id $Id
    $res = Invoke-WhCli -Arguments @('--yes', 'mod', 'remove', $Id)
    $wait = if (Get-WhProp $record 'Enabled') { Wait-WhModInExplorer -LibraryFileName $record.LibraryFileName -Loaded $false -TimeoutSec $WaitUnloadedSec }
    Write-WhLog "Remove-LocalMod: $Id"
    [pscustomobject]@{ Id = $Id; Removed = Get-WhJson $res 'data.removed'; UnloadedFromExplorer = Get-WhProp $wait 'Met' }
}

function Set-ModSetting {
    <#
    .SYNOPSIS  One setting. An array replaces the whole array (-Key excludedApps -Value @('mspaint.exe')); anything
               else sets one key (also 'excludedApps[1]').
    .NOTES     A scalar: windhawk-cli --json mod settings set <id> <key>=<value>. An array: mod settings get, then
               mod settings write with all of them. The engine in Explorer calls the mod's Wh_ModSettingsChanged.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Key,
          [Parameter(Mandatory)][AllowEmptyString()][AllowEmptyCollection()][AllowNull()][object]$Value)
    $Id = Resolve-WhModId -Id $Id
    if (($Value -is [Collections.IList]) -and $Value -isnot [string]) {
        $current = Get-WhJson (Invoke-WhCli -Arguments @('mod', 'settings', 'get', $Id)) 'data.settings'
        $all = [ordered]@{}
        $element = '^' + [regex]::Escape($Key) + '\[\d+\]'
        if ($null -ne $current) { foreach ($p in $current.PSObject.Properties) { if ($p.Name -notmatch $element) { $all[$p.Name] = $p.Value } } }
        $i = 0
        foreach ($v in $Value) { $all["$Key[$i]"] = [string]$v; $i++ }
        if ($i -eq 0) { $all["$Key[0]"] = '' }
        $file = Join-Path $script:Wh.Work ("settings-{0}.json" -f [guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($script:Wh.Work)
        [IO.File]::WriteAllText($file, ($all | ConvertTo-Json -Depth 4 -Compress))
        try { $res = Invoke-WhCli -Arguments @('mod', 'settings', 'write', $Id, $file) }
        finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
    }
    else {
        $v = if ($Value -is [bool]) { $Value.ToString().ToLowerInvariant() } elseif ($null -eq $Value) { '' } else { [string]$Value }
        $res = Invoke-WhCli -Arguments @('mod', 'settings', 'set', $Id, "$Key=$v")
    }
    $changes = @(Get-WhJson $res 'data.changes' | Where-Object { $null -ne $_ })
    Write-WhLog ("Set-ModSetting: {0} {1} -> {2}" -f $Id, $Key, (($changes | ForEach-Object { "$(Get-WhProp $_ 'key')=$(Get-WhProp $_ 'value')" }) -join ', '))
    [pscustomobject]@{ Id = $Id; Changes = $changes; CliMs = Get-WhProp $res 'DurationMs' }
}

function Set-ModLogging {
    <# .NOTES windhawk-cli --json mod config set <id> loggingEnabled true|false; applies at once, without reloading the mod. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][bool]$Enabled)
    $Id = Resolve-WhModId -Id $Id
    $res = Invoke-WhCli -Arguments @('mod', 'config', 'set', $Id, 'loggingEnabled', $Enabled.ToString().ToLowerInvariant())
    Write-WhLog "Set-ModLogging: $Id $Enabled"
    [pscustomobject]@{ Id = $Id; LoggingEnabled = Get-WhJson $res 'data.value' }
}

function Start-ModLogCapture {
    <#
    .SYNOPSIS  The mod's log (its Wh_Log lines) into a file, "HH:mm:ss.fff <pid> <process>  [WH] [<mod>] <text>",
               read from the debug output the way Windhawk's own log window does it, without a window.
               It runs on background threads of this PowerShell until Stop-ModLogCapture or the process ends.
               The mod only writes while its logging is on (Set-ModLogging -Enabled $true).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OutFile, [string]$ModId)
    Initialize-WhNative
    $filter = if ($ModId) { Resolve-WhModId -Id $ModId }
    $c = [TsoWindhawk.DbwinCapture]::Start([IO.Path]::GetFullPath($OutFile), $filter)
    Write-WhLog "Start-ModLogCapture: $($c.OutFile) filter=$filter"
    [pscustomobject]@{ OutFile = $c.OutFile; StartedAt = $c.StartedAt }
}

function Stop-ModLogCapture {
    [CmdletBinding()]
    param()
    $c = if ('TsoWindhawk.DbwinCapture' -as [type]) { [TsoWindhawk.DbwinCapture]::Current }
    if (-not $c) { return $null }
    $c.Dispose()
    Write-WhLog "Stop-ModLogCapture: $($c.OutFile) lines=$($c.Written) seen=$($c.Seen) dropped=$($c.Dropped) error=$($c.LastError)"
    [pscustomobject]@{ OutFile = $c.OutFile; Lines = $c.Written; DebugMessagesSeen = $c.Seen; Dropped = $c.Dropped; Error = $c.LastError }
}

function Enter-SafeMode {
    <#
    .SYNOPSIS  Stops every mod. Normally windhawk-cli --json app settings set safeMode true --confirm-app-restart.
               -Panic (also unelevated, also on 1.x): signals the service's safe-mode event; the service turns safe
               mode on and stops, and every mod unloads.
    #>
    [CmdletBinding()]
    param([switch]$Panic, [int]$TimeoutSec = 60)
    Initialize-WhNative
    $noEngine = { -not (Get-WhExplorerModules | Where-Object { $_.EnginePath }) }
    if ($Panic -or -not (Test-Path -LiteralPath (Get-WhCliPath))) {
        $err = [TsoWindhawk.Native]::SignalNamedEvent('Global\WindhawkServiceSafeModeStopEvent')
        if ($err -ne 0) {
            Write-Warning "Signalling the safe-mode event failed (Win32 error $err; 2: the service is not running). Setting SafeMode and stopping the service instead."
            Assert-WhElevated
            Set-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Windhawk\Settings' -Name SafeMode -Value 1 -Type DWord
            Stop-Service -Name Windhawk -ErrorAction SilentlyContinue
        }
        $w = Wait-WhCondition -TimeoutSec $TimeoutSec -Condition { (Get-WhProp (Get-Service Windhawk -ErrorAction SilentlyContinue) 'Status') -ne 'Running' -and (& $noEngine) }
    }
    else {
        $null = Invoke-WhCli -Arguments @('app', 'settings', 'set', 'safeMode', 'true', '--confirm-app-restart')
        $w = Wait-WhCondition -TimeoutSec $TimeoutSec -IntervalMs 500 -Condition { (Get-WhProp (Get-Service Windhawk -ErrorAction SilentlyContinue) 'Status') -eq 'Running' -and (& $noEngine) }
    }
    Write-WhLog "Enter-SafeMode: panic=$([bool]$Panic) done=$($w.Met)"
    $s = Get-WindhawkState
    [pscustomobject]@{ SafeMode = $s.SafeMode; ServiceState = $s.ServiceState; ExplorerEngineVersion = $s.ExplorerEngineVersion; Done = $w.Met }
}

function Exit-SafeMode {
    <# .NOTES windhawk-cli --json app settings set safeMode false --confirm-app-restart (which also starts a service that
              Enter-SafeMode -Panic stopped). Waits until Explorer has the engine back. #>
    [CmdletBinding()]
    param([int]$TimeoutSec = 90)
    if (Test-Path -LiteralPath (Get-WhCliPath)) {
        $null = Invoke-WhCli -Arguments @('app', 'settings', 'set', 'safeMode', 'false', '--confirm-app-restart')
    }
    else {
        Assert-WhElevated
        Set-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Windhawk\Settings' -Name SafeMode -Value 0 -Type DWord
        Restart-Service -Name Windhawk
    }
    $w = Wait-WhCondition -TimeoutSec $TimeoutSec -IntervalMs 500 -Condition {
        (Get-WhProp (Get-Service Windhawk -ErrorAction SilentlyContinue) 'Status') -eq 'Running' -and (Get-WhProp (Get-WhTaskbarModules) 'EnginePath')
    }
    Write-WhLog "Exit-SafeMode: done=$($w.Met)"
    $s = Get-WindhawkState
    [pscustomobject]@{ SafeMode = $s.SafeMode; ServiceState = $s.ServiceState; ExplorerEngineVersion = $s.ExplorerEngineVersion
                       ModsLoaded = @($s.Mods | Where-Object LoadedInExplorer | ForEach-Object { $_.Id }); Done = $w.Met }
}

function Restore-WindhawkStable {
    <#
    .SYNOPSIS  Back to the version Install-WindhawkAlpha recorded. Not an official path (an older installer asks for
               the newer version to be uninstalled first), so it does exactly that, keeping the data:
    .NOTES     1. a backup to .work\test\windhawk\restore-<time>: windhawk-cli data export, a registry export of
                  HKLM\SOFTWARE\Windhawk, the mod sources
               2. a copy of the alpha's uninstall.exe runs: uninstall.exe /S _?=<install folder>. Silent, it
                  keeps HKLM\SOFTWARE\Windhawk and ProgramData\Windhawk (mods, settings, sources); the service,
                  scheduled tasks, the windhawk:// handler, shortcuts and the program folder go.
               3. the recorded version's offline installer: /S /SKIP_SCHEDULED_TASK_UPDATE (offline, because the
                  uninstall also took the compiler and the editor that an online installer would download again)
               4. wait for its service and its engine in Explorer
    #>
    [CmdletBinding()]
    param([int]$StepTimeoutSec = 900, [int]$ReadyTimeoutSec = 120)
    if (-not (Test-Path -LiteralPath $script:WhRecord)) { throw "No record of the version before the upgrade ($script:WhRecord); Install-WindhawkAlpha writes it." }
    $record = Get-Content -LiteralPath $script:WhRecord -Raw | ConvertFrom-Json
    $target = [string]$record.Version
    $before = Get-WindhawkState
    if ($before.AppVersion -eq $target) {
        Write-WhLog "Restore-WindhawkStable: already $target"
        return $before
    }
    Assert-WhElevated
    if ($before.EditorRunning -or $before.UiRunning) { throw "A Windhawk window or its mod editor is open: close it first." }
    $setup = [string](Get-WhProp $record 'Installer')
    if (-not $setup -or -not (Test-Path -LiteralPath $setup) -or (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne (Get-WhProp $record 'Sha256') -or
        (Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid') {
        throw "The installer of Windhawk $target ($setup) is missing or has changed since Install-WindhawkAlpha checked it."
    }

    $bak = Join-Path $script:Wh.Work ('restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $bak | Out-Null
    try { $null = Invoke-WhCli -Arguments @('data', 'export', '--out', (Join-Path $bak 'windhawk-data-export.json'), '--force') }
    catch { Write-Warning "windhawk-cli data export failed: $($_.Exception.Message)" }
    & reg.exe export 'HKLM\SOFTWARE\Windhawk' (Join-Path $bak 'HKLM-SOFTWARE-Windhawk.reg') /y *> $null
    $sources = Join-Path $env:ProgramData 'Windhawk\ModsSource'
    if (Test-Path -LiteralPath $sources) { Copy-Item -LiteralPath $sources -Destination (Join-Path $bak 'ModsSource') -Recurse -Force }
    $before | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bak 'state-before.json')

    $uninstaller = Join-Path $bak 'uninstall.exe'   # a copy: the original deletes itself
    Copy-Item -LiteralPath (Join-Path $script:Wh.InstallDir 'uninstall.exe') -Destination $uninstaller
    $r = Invoke-WhProcess -FilePath $uninstaller -CommandLine "/S _?=$($script:Wh.InstallDir)" -TimeoutSec $StepTimeoutSec -NoCapture
    if ($r.TimedOut -or $r.ExitCode -ne 0) { throw "The uninstall did not complete (exit $($r.ExitCode)). Windhawk may be half removed: run $setup." }
    $gone = Wait-WhCondition -TimeoutSec 60 -Condition { -not (Get-Service Windhawk -ErrorAction SilentlyContinue) -and -not (Test-Path -LiteralPath (Join-Path $script:Wh.InstallDir 'windhawk.exe')) }
    if (-not $gone.Met) { Write-Warning 'After the uninstall, the service or windhawk.exe is still there; installing anyway.' }

    $r = Invoke-WhProcess -FilePath $setup -CommandLine '/S /SKIP_SCHEDULED_TASK_UPDATE' -TimeoutSec $StepTimeoutSec -NoCapture
    if ($r.TimedOut -or $r.ExitCode -ne 0) { throw "The install of $target did not complete (exit $($r.ExitCode)). Run $setup yourself." }
    $w = Wait-WhCondition -TimeoutSec $ReadyTimeoutSec -IntervalMs 500 -Condition {
        $s = Get-WindhawkState
        $s.AppVersion -eq $target -and $s.ServiceState -eq 'Running' -and $s.ExplorerEngineVersion -eq $target
    }
    $after = Get-WindhawkState
    $after | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bak 'state-after.json')
    $doomed = @(Get-WhPendingDeletes | Where-Object { Test-Path -LiteralPath $_ })
    if ($doomed.Count) {
        Write-Warning ("The uninstall queued these files for deletion at the next reboot, and the install wrote them again: " + ($doomed -join ', ') +
                       ". After the next reboot, run $setup again.")
    }
    $after | Add-Member -NotePropertyName Ready -NotePropertyValue $w.Met
    $after | Add-Member -NotePropertyName BackupDir -NotePropertyValue $bak
    $after | Add-Member -NotePropertyName RebootWouldDelete -NotePropertyValue $doomed
    Write-WhLog "Restore-WindhawkStable: ready=$($w.Met) app=$($after.AppVersion) engine=$($after.ExplorerEngineVersion) mods=$(($after.Mods | ForEach-Object { "$($_.Id):$($_.Enabled):$($_.LoadedInExplorer)" }) -join ',')"
    return $after
}
