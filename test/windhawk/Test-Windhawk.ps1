<#
.SYNOPSIS
    Tests windhawk.ps1 without Windhawk: the functions run under Set-StrictMode -Version Latest against
    canned windhawk-cli replies that leave optional fields out (warnings, changes, changed, ...), as
    the 2.0 command line does; and windhawk-stub.ps1 must offer the same functions and parameters.
    Nothing real is called: the process runner, the command line's path, the elevation check, the mod
    record, Explorer's module list and the log are replaced after dot-sourcing.
#>
param([string]$ModPath = (Join-Path $PSScriptRoot '..\..\taskbar-spatial-order.wh.cpp'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ModPath = (Resolve-Path -LiteralPath $ModPath).Path
$script:Checks = 0; $script:Failures = 0
$script:Report = [Collections.Generic.List[string]]::new()
function Check([string]$What, [bool]$Ok, [string]$Detail = '') {
    $script:Checks++
    if ($Ok) { $script:Report.Add("ok   $What") } else { $script:Failures++; $script:Report.Add("FAIL $What $Detail") }
}

# ------------------------------------------------------------------ the stubs offer what the real file does
function Get-Functions([string]$Path) {
    $tokens = $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    Check "$(Split-Path $Path -Leaf) parses" ($errors.Count -eq 0) (($errors | ForEach-Object { $_.Message }) -join '; ')
    $map = @{}
    foreach ($f in $ast.FindAll({ param($a) $a -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        $map[$f.Name] = @(if ($f.Body.ParamBlock) { $f.Body.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath } })
    }
    $map
}
$real = Get-Functions (Join-Path $PSScriptRoot 'windhawk.ps1')
$stub = Get-Functions (Join-Path $PSScriptRoot 'windhawk-stub.ps1')
foreach ($name in $stub.Keys | Where-Object { $_ -notmatch 'Stub' } | Sort-Object) {
    $missing = @(if ($real.ContainsKey($name)) { $stub[$name] | Where-Object { $real[$name] -notcontains $_ } } else { '(the function)' })
    Check "stub $name matches windhawk.ps1" ($missing.Count -eq 0) "windhawk.ps1 lacks: $($missing -join ', ')"
}

# ------------------------------------------------------------------ windhawk.ps1 with everything real replaced
. (Join-Path $PSScriptRoot 'windhawk.ps1')
$scratch = Join-Path $script:Wh.Work ('test-' + [guid]::NewGuid().ToString('N'))
$script:Wh.Work = $scratch

$script:Calls = [Collections.Generic.List[string]]::new()
$script:Replies = [ordered]@{}   # argument substring -> windhawk-cli's stdout
$script:Record = $null           # what Get-WhModRecord returns
$script:Loaded = @()             # the mod DLLs "in Explorer"
$script:StateMods = @()

function Invoke-WhProcess {
    param([string]$FilePath, [string[]]$ArgumentList = @(), [string]$CommandLine, [int]$TimeoutSec, [switch]$NoCapture)
    $line = $ArgumentList -join ' '
    $script:Calls.Add($line)
    $out = $null
    foreach ($k in $script:Replies.Keys) { if ($line.Contains($k)) { $out = $script:Replies[$k]; break } }
    if ($null -eq $out) { throw "no canned reply for: $line" }
    [pscustomobject]@{ ExitCode = 0; StdOut = $out; StdErr = ''; TimedOut = $false; DurationMs = 7 }
}
function Get-WhCliPath { (Get-Process -Id $PID).Path }
function Assert-WhElevated { }
function Get-WhModRecord { param([string]$Id) $script:Record }
function Get-WhExplorerModules {
    [pscustomobject]@{ Pid = 1; IsTaskbar = $true; EnginePath = 'windhawk.dll'; EngineVersion = '2.0.0-alpha.7'; ModDlls = @($script:Loaded); Error = $null }
}
function Write-WhLog { param([string]$Message) }
function Get-WindhawkState { [pscustomobject]@{ SafeMode = $false; ServiceState = 'Running'; ExplorerEngineVersion = '2.0.0-alpha.7'; Mods = @($script:StateMods) } }

function Try-Call([string]$What, [scriptblock]$Body) {
    try { $r = & $Body; Check $What $true; return $r }
    catch { Check $What $false "-> $($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber)]"; return $null }
}
function Reply([string]$Match, [string]$Json) { $script:Replies.Clear(); $script:Replies[$Match] = $Json }

try {
    $lib = 'local@taskbar-spatial-order_1.0_123456.dll'

    # Install-LocalMod: a clean compile (no warnings field at all), the mod enabled.
    Reply 'mod install' ('{"success":true,"data":{"id":"local@taskbar-spatial-order","version":"1.0","config":{"libraryFileName":"' + $lib + '"}}}')
    $script:Loaded = @($lib)
    $r = Try-Call 'Install-LocalMod, no warnings/disabled fields' { Install-LocalMod -Path $ModPath -WaitLoadedSec 2 }
    if ($r) {
        Check '  Id' ($r.Id -eq 'local@taskbar-spatial-order') $r.Id
        Check '  Enabled (no disabled field = enabled)' ($r.Enabled -eq $true)
        Check '  Warnings null' ($null -eq $r.Warnings)
        Check '  LoadedInExplorer' ($r.LoadedInExplorer -eq $true) "$($r.LoadedInExplorer)"
        Check '  CliMs' ($r.CliMs -eq 7) "$($r.CliMs)"
        Check '  --strict, no --disabled' ($script:Calls[-1] -like '--json mod install --file * --strict') $script:Calls[-1]
    }

    # Disabled, with warnings, and no libraryFileName.
    Reply 'mod install' '{"success":true,"data":{"id":"local@taskbar-spatial-order","version":"1.0","config":{"disabled":true},"warnings":["w1"]}}'
    $r = Try-Call 'Install-LocalMod -Disabled, warnings present, no libraryFileName' { Install-LocalMod -Path $ModPath -Disabled }
    if ($r) {
        Check '  Enabled false' ($r.Enabled -eq $false)
        Check '  Warnings w1' ([string]$r.Warnings -eq 'w1') "$($r.Warnings)"
        Check '  no load wait when disabled' ($null -eq $r.LoadedInExplorer)
        Check '  --disabled passed' ($script:Calls[-1] -like '* --strict --disabled') $script:Calls[-1]
    }

    # An envelope without data at all.
    Reply 'mod install' '{"success":true}'
    $r = Try-Call 'Install-LocalMod, envelope without data' { Install-LocalMod -Path $ModPath }
    if ($r) { Check '  Id null' ($null -eq $r.Id) }

    # Enable, Disable, Remove without data.changed, with and without a mod record.
    $script:Record = [pscustomobject]@{ Id = 'local@taskbar-spatial-order'; Enabled = $true; LibraryFileName = $lib }
    Reply 'mod enable' '{"success":true,"data":{}}'
    $script:Loaded = @($lib)
    $r = Try-Call 'Enable-Mod, no changed field' { Enable-Mod -Id 'taskbar-spatial-order' -WaitLoadedSec 2 }
    if ($r) { Check '  LoadedInExplorer' ($r.LoadedInExplorer -eq $true); Check '  Changed null' ($null -eq $r.Changed) }
    Reply 'mod disable' '{"success":true}'
    $script:Loaded = @()
    $r = Try-Call 'Disable-Mod, no data' { Disable-Mod -Id 'taskbar-spatial-order' -WaitUnloadedSec 2 }
    if ($r) { Check '  UnloadedFromExplorer' ($r.UnloadedFromExplorer -eq $true) }
    $script:Record = $null
    Reply 'mod enable' '{"success":true,"data":{"changed":false}}'
    $r = Try-Call 'Enable-Mod, no mod record (no wait)' { Enable-Mod -Id 'taskbar-spatial-order' }
    if ($r) { Check '  Changed false' ($r.Changed -eq $false); Check '  WaitedMs null' ($null -eq $r.WaitedMs) }
    Reply 'mod remove' '{"success":true,"data":{}}'
    $r = Try-Call 'Remove-LocalMod, no removed field, no record' { Remove-LocalMod -Id 'taskbar-spatial-order' }

    # Set-ModSetting: a scalar without changes, with an empty list, with changes; an array with no settings.
    Reply 'mod settings set' '{"success":true,"data":{}}'
    $r = Try-Call 'Set-ModSetting scalar, no changes field' { Set-ModSetting -Id 'taskbar-spatial-order' -Key 'minimisedLast' -Value $false }
    if ($r) { Check '  Changes empty' (@($r.Changes).Count -eq 0); Check '  false as false' ($script:Calls[-1] -like '* minimisedLast=false') $script:Calls[-1] }
    Reply 'mod settings set' '{"success":true,"data":{"changes":[]}}'
    $r = Try-Call 'Set-ModSetting scalar, empty changes' { Set-ModSetting -Id 'taskbar-spatial-order' -Key 'minimisedLast' -Value $true }
    Reply 'mod settings set' '{"success":true,"data":{"changes":[{"key":"minimisedLast","value":true},{"key":"x"}]}}'
    $r = Try-Call 'Set-ModSetting scalar, a change without value' { Set-ModSetting -Id 'taskbar-spatial-order' -Key 'minimisedLast' -Value $true }
    if ($r) { Check '  two changes' (@($r.Changes).Count -eq 2) "$(@($r.Changes).Count)" }
    $script:Replies.Clear()
    $script:Replies['mod settings get'] = '{"success":true,"data":{}}'
    $script:Replies['mod settings write'] = '{"success":true}'
    $r = Try-Call 'Set-ModSetting array, settings get without settings' { Set-ModSetting -Id 'taskbar-spatial-order' -Key 'excludedApps' -Value @('a.exe', 'b.exe') }
    $script:Replies['mod settings get'] = '{"success":true,"data":{"settings":{"minimisedLast":1,"excludedApps[0]":"old.exe"}}}'
    $r = Try-Call 'Set-ModSetting empty array over existing settings' { Set-ModSetting -Id 'taskbar-spatial-order' -Key 'excludedApps' -Value @() }

    # Set-ModLogging without data.value.
    Reply 'mod config set' '{"success":true}'
    $r = Try-Call 'Set-ModLogging on, no value field' { Set-ModLogging -Id 'taskbar-spatial-order' -Enabled $true }
    $r = Try-Call 'Set-ModLogging off, no data' { Set-ModLogging -Id 'taskbar-spatial-order' -Enabled $false }
    Check '  loggingEnabled false' ($script:Calls[-1] -like '* loggingEnabled false') $script:Calls[-1]

    # A failure envelope without an error object: a clear error, not a strict-mode one.
    Reply 'mod enable' '{"success":false}'
    try { Enable-Mod -Id 'taskbar-spatial-order' | Out-Null; Check 'failure envelope throws' $false 'no exception' }
    catch { Check 'failure envelope without error object: the command line''s error' ($_.Exception.Message -like 'windhawk-cli mod enable*failed*') $_.Exception.Message }
    Reply 'mod enable' '{"success":false,"error":{"code":"MOD_NOT_FOUND","message":"no such mod"}}'
    try { Enable-Mod -Id 'taskbar-spatial-order' | Out-Null; Check 'failure envelope throws' $false 'no exception' }
    catch { Check 'failure envelope with error: code and message' ($_.Exception.Message -like '*MOD_NOT_FOUND*no such mod*' -and $_.Exception.Data['ErrorCode'] -eq 'MOD_NOT_FOUND') $_.Exception.Message }

    # Exit-SafeMode with no mod loaded (member enumeration of an empty list), and with one of two.
    Reply 'safeMode' '{"success":true}'
    $script:StateMods = @()
    $r = Try-Call 'Exit-SafeMode, no mods loaded' { Exit-SafeMode -TimeoutSec 2 }
    if ($r) { Check '  ModsLoaded empty' (@($r.ModsLoaded).Count -eq 0) }
    $script:StateMods = @([pscustomobject]@{ Id = 'local@a'; LoadedInExplorer = $true }, [pscustomobject]@{ Id = 'local@b'; LoadedInExplorer = $false })
    $r = Try-Call 'Exit-SafeMode, one of two mods loaded' { Exit-SafeMode -TimeoutSec 2 }
    if ($r) { Check '  ModsLoaded local@a' ((@($r.ModsLoaded) -join ',') -eq 'local@a') (@($r.ModsLoaded) -join ',') }
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
$script:Report
"windhawk.ps1 test: $script:Checks checks, $script:Failures failures; $($script:Calls.Count) windhawk-cli calls (all canned)"
exit ([int]($script:Failures -gt 0))
