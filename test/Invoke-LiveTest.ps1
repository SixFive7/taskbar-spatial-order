<#
.SYNOPSIS
    The live test of the Windhawk mod Taskbar Spatial Order: it drives the PC's real taskbar by
    itself. Read README.md first: a live run takes over the mouse, the keyboard and the taskbar.

.DESCRIPTION
    Sixteen steps (-ListSteps lists them in the order they run): Windhawk goes to the 2.0 alpha, the
    mod is installed with logging, the README demo plays (step 16, right after step 2), then test
    windows of an app of their own are opened, dragged, snapped, minimised, spread over the monitors,
    uncombined, hovered, re-identified and moved across virtual desktops while Explorer's own lists
    (read from its memory), UI Automation, images of the taskbars and the mod's log are checked.
    Last, Windhawk goes back to the version it was and everything is cleaned up. The mod stays
    installed and enabled, with its default settings.

    Output, in the run folder (.work\test\runs\<yyyyMMdd-HHmmss>, with -dry for a dry run):
      progress.log   one line per event, nothing else:
                       TOTAL eta=<s>
                       STEP i/N <name> START eta=<s>
                       STEP i/N <name> PASS|FAIL <detail> took=<s>
                       DONE pass=<n> fail=<n> [aborted=<reason>]
      detail.log     everything, with a CHECK line for every check and the reason for every FAIL
      mod.log        the mod's log
      journal.json   what the run changed, for -Recover
      captures\      images of the taskbars and flyouts, UI Automation dumps of the flyouts
      demo.json      the demo's scenes (UTC times), crop and taskbar buttons
      testapp.log    the test app's own log
      recording\, gifs\   with -Record

    A safe stop (an Explorer crash or hang, or a restart other than the installer's in steps 1 and 14
    or the planned one in step 11, a file named STOP in the run folder, or Ctrl+C) disables the mod,
    puts the taskbar grouping back and checks it, and closes the test windows. If this process dies,
    the test app closes its windows by itself and puts the grouping back from the journal; -Recover
    does the rest.

.PARAMETER ModPath
    The mod source to install (a .wh.cpp). Default: the repository's taskbar-spatial-order.wh.cpp.

.PARAMETER Steps
    Which steps: "all" (default), "1-13,16", "3,4,7", "3-6,9". The steps always run in the table's
    order, which puts 16 (the demo) right after 2. Steps 3-13 and 16 need the mod loaded; without
    step 2 the mod that is already installed is used.

.PARAMETER DryRun
    Changes nothing: the test windows are created hidden and never shown, no input is sent, no
    registry value is written, Explorer is not restarted, and Windhawk is replaced by the stubs in
    windhawk\windhawk-stub.ps1. The oracles run for real (they only read).

.PARAMETER Record
    Records the primary monitor during the run (rec\record.ps1, with -Encoder), then cuts the README
    GIFs out of the recording (rec\make-gifs.ps1) into the run folder's gifs\. Needs ffmpeg.

.PARAMETER Recover
    Undoes what an interrupted run left behind, from its journal (-RunDir, or the last live run).
    With -DisableMod it also disables the mod.

.PARAMETER GlomPrimaryOnly
    Changes the grouping ("never combine") on the primary monitor's taskbar only; step 7 then skips
    its part on the right-hand monitor.

.PARAMETER NoBuild
    Uses the last build even if a source is newer.

.EXAMPLE
    pwsh -NoProfile -File test\Invoke-LiveTest.ps1 -DryRun
.EXAMPLE
    pwsh -NoProfile -File test\Invoke-LiveTest.ps1 -Steps 1,2,16,14,15 -Record
#>
[CmdletBinding()]
param(
    [string]$ModPath,
    [string]$Steps = 'all',
    [switch]$DryRun,
    [string]$RunDir,
    [switch]$GlomPrimaryOnly,
    [switch]$Record,
    [string]$Encoder = 'libx264',
    [switch]$Recover,
    [switch]$DisableMod,
    [switch]$ListSteps,
    [switch]$PlanOnly,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path $here
$work = Join-Path $repo '.work\test'
$bin = Join-Path $work 'bin'

if (-not $NoBuild) {
    $newest = (Get-ChildItem (Join-Path $here 'src') -Recurse -Include *.cs, *.csproj, *.props | Measure-Object LastWriteTimeUtc -Maximum).Maximum
    $stamp = Join-Path $bin 'build.stamp'
    if (-not (Test-Path $stamp) -or (Get-Item $stamp).LastWriteTimeUtc -lt $newest) {
        'building (sources changed since the last build)'
        & (Join-Path $here 'build.ps1')
    }
}
Add-Type -Path (Join-Path $bin 'TsoLive\TsoLive.dll')

if ($ListSteps) { return [TsoLive.LiveRunner]::Describe() }
if ($PlanOnly) { return [TsoLive.LiveRunner]::Plan($Steps) }

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $DryRun -and -not $Recover -and -not $elevated) {
    throw 'A live run changes Windhawk, which needs an elevated PowerShell (Run as administrator). -DryRun, -PlanOnly and -ListSteps do not.'
}

# The Windhawk functions: the stubs in a dry run, never the real ones.
. (Join-Path $here $(if ($DryRun) { 'windhawk\windhawk-stub.ps1' } else { 'windhawk\windhawk.ps1' }))

# The runner calls back into PowerShell through this: a function name and JSON of named arguments
# in, JSON {ok, result | error} out.
$invoke = [Func[string, string, string]] {
    param([string]$Name, [string]$ArgumentsJson)
    try {
        $params = @{}
        if ($ArgumentsJson) { $params = ConvertFrom-Json -InputObject $ArgumentsJson -AsHashtable }
        $out = & $Name @params
        return (@{ ok = $true; result = $out } | ConvertTo-Json -Depth 8 -Compress -WarningAction SilentlyContinue)
    }
    catch {
        # Where it really failed: the first frame of the script stack (the call site is only this adapter).
        $at = @(($_.ScriptStackTrace -split "`r?`n") | Where-Object { $_ })[0]
        return (@{ ok = $false; error = "$($_.Exception.Message) [$at]" } | ConvertTo-Json -Compress)
    }
}

if ($Recover) {
    $journal = if ($RunDir) { Join-Path $RunDir 'journal.json' } else { Get-Content (Join-Path $work 'state\active-run.txt') -ErrorAction Stop }
    exit [TsoLive.LiveRunner]::Recover($journal, $invoke, [bool]$DisableMod)
}

if (-not $ModPath) { $ModPath = Join-Path $repo 'taskbar-spatial-order.wh.cpp' }
$ModPath = (Resolve-Path -LiteralPath $ModPath).Path
if (-not $RunDir) {
    $RunDir = Join-Path $work ('runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + $(if ($DryRun) { '-dry' }))
}
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$RunDir = (Resolve-Path -LiteralPath $RunDir).Path
$env:TSO_STUB_LOG = Join-Path $RunDir 'windhawk-stub-calls.log'

$options = [TsoLive.RunOptions]::new()
$options.WorkDir = $work
$options.RunDir = $RunDir
$options.ModPath = $ModPath
$options.Steps = $Steps
$options.TestAppExe = Join-Path $bin 'TsoTestApp\TsoTestApp.exe'
$options.DryRun = [bool]$DryRun
$options.GlomPrimaryOnly = [bool]$GlomPrimaryOnly

# -Record: the recorder runs in a process of its own (hidden), from before the first step to after the last.
$recorder = $null
$recDir = Join-Path $RunDir 'recording'
if ($Record -and -not $DryRun) {
    $psi = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    foreach ($a in '-NoProfile', '-File', (Join-Path $here 'rec\record.ps1'), '-OutDir', $recDir, '-Encoder', $Encoder) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $recorder = [Diagnostics.Process]::Start($psi)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path (Join-Path $recDir 'started.json')) -and -not $recorder.HasExited -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path (Join-Path $recDir 'started.json'))) {
        New-Item -ItemType File -Force -Path (Join-Path $recDir 'STOP') | Out-Null
        [void]$recorder.WaitForExit(30000)
        throw "The recorder did not start (see $recDir\done.json); nothing was changed."
    }
    "recording the primary monitor into $recDir"
}

"run folder:   $RunDir"
"progress log: $(Join-Path $RunDir 'progress.log')"
try { $code = [TsoLive.LiveRunner]::Run($options, $invoke) }
finally {
    if ($recorder) {
        New-Item -ItemType File -Force -Path (Join-Path $recDir 'STOP') | Out-Null
        if (-not $recorder.WaitForExit(60000)) { $recorder.Kill($true) }
    }
}
"exit code $code (0 all passed, 1 some failed, 2 aborted)"

$demo = Join-Path $RunDir 'demo.json'
if ($recorder -and (Test-Path $demo)) {
    & (Join-Path $here 'rec\make-gifs.ps1') -RecDir $recDir -Demo $demo -OutDir (Join-Path $RunDir 'gifs')
}
exit $code
