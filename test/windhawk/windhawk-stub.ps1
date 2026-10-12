# Stubs of the functions in windhawk.ps1, for -DryRun: the same names and parameters, and results
# shaped like the real ones (mod ids local@<id>, AppVersion, mod records with Enabled and
# LoggingEnabled). They change nothing: each call is appended to $env:TSO_STUB_LOG, and
# Start-ModLogCapture writes a made-up mod log that the other stubs append to, so a dry run takes
# the whole path from the runner into PowerShell and back.
# Like windhawk-cli, Set-ModSetting rejects a value of the wrong type (true/false for the booleans).
# Get-WindhawkState reports Stub = $true; the runner refuses a dry run without that marker.

$script:StubState = @{
    Before   = '1.0.0'           # any 1.x release: the stubs only pretend
    Alpha    = '2.0.0-alpha.7'
    Version  = '1.0.0'
    SafeMode = $false
    Mods     = @{}
    LogFile  = $null
}

function Write-StubCall([string]$What) {
    if ($env:TSO_STUB_LOG) {
        Add-Content -LiteralPath $env:TSO_STUB_LOG -Value ('{0:HH:mm:ss.fff} {1}' -f (Get-Date), $What)
    }
}

function Resolve-StubId([string]$Id) { if ($Id -match '@') { $Id } else { "local@$Id" } }

function Write-StubModLog([string]$Id, [string[]]$Lines) {
    if ($script:StubState.LogFile) {
        $stamp = Get-Date -Format 'HH:mm:ss.fff'
        Add-Content -LiteralPath $script:StubState.LogFile -Value ($Lines | ForEach-Object { "$stamp 0 explorer.exe  [WH] [$Id] [stub] $_" })
    }
}

function Write-StubInit([string]$Id) {
    Write-StubModLog $Id @('[1007:Wh_ModInit]: >',
        '[997:HookTaskbarSymbols]: Button group list in slot 9, item list in slot 3',
        '[1014:Wh_ModAfterInit]: >')
}

function Get-StubMod([string]$Id) {
    $Id = Resolve-StubId $Id
    if (-not $script:StubState.Mods.ContainsKey($Id)) { throw "stub: mod $Id is not installed" }
    $script:StubState.Mods[$Id]
}

function Install-WindhawkAlpha {
    [CmdletBinding()] param()
    Write-StubCall 'Install-WindhawkAlpha'
    $script:StubState.Version = $script:StubState.Alpha
    Get-WindhawkState
}

function Restore-WindhawkStable {
    [CmdletBinding()] param()
    Write-StubCall 'Restore-WindhawkStable'
    $script:StubState.Version = $script:StubState.Before
    Get-WindhawkState
}

function Install-LocalMod {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path, [switch]$Disabled)
    Write-StubCall "Install-LocalMod -Path $Path -Disabled:$Disabled"
    if (-not (Test-Path -LiteralPath $Path)) { throw "stub: $Path does not exist" }
    $source = Get-Content -LiteralPath $Path -Raw
    $id = 'local@' + [regex]::Match($source, '(?m)^//\s*@id\s+(\S+)').Groups[1].Value
    $version = [regex]::Match($source, '(?m)^//\s*@version\s+(\S+)').Groups[1].Value
    $script:StubState.Mods[$id] = @{ Id = $id; Version = $version; Enabled = -not $Disabled; LoggingEnabled = $false
        LibraryFileName = "$($id)_$($version)_stub.dll"; Settings = @{} }
    [pscustomobject]@{ Id = $id; Version = $version; Enabled = -not $Disabled; LoadedInExplorer = $null }
}

function Enable-Mod {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Id)
    Write-StubCall "Enable-Mod -Id $Id"
    $m = Get-StubMod $Id
    $m.Enabled = $true
    Write-StubInit $m.Id
    [pscustomobject]@{ Id = $m.Id; Changed = $true; LoadedInExplorer = $false }
}

function Disable-Mod {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Id)
    Write-StubCall "Disable-Mod -Id $Id"
    $Id = Resolve-StubId $Id
    if ($script:StubState.Mods.ContainsKey($Id)) {
        $script:StubState.Mods[$Id].Enabled = $false
        Write-StubModLog $Id @('[1027:Wh_ModUninit]: >')
    }
    [pscustomobject]@{ Id = $Id; Changed = $true; UnloadedFromExplorer = $true }
}

function Remove-LocalMod {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Id)
    Write-StubCall "Remove-LocalMod -Id $Id"
    $script:StubState.Mods.Remove((Resolve-StubId $Id))
}

function Set-ModSetting {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][AllowEmptyString()][AllowEmptyCollection()][AllowNull()][object]$Value)
    $isArray = ($Value -is [System.Collections.IList]) -and -not ($Value -is [string])
    $shown = if ($isArray) { '@(' + (@($Value) -join ',') + ')' } else { "'$Value'" }
    Write-StubCall "Set-ModSetting -Id $Id -Key $Key -Value $shown"
    $m = Get-StubMod $Id
    switch -Regex ($Key) {
        '^(minimisedLast|skipFullyMinimisedGroups)$' {
            if ($Value -isnot [bool]) { throw "stub: $Key takes true or false, got $($Value.GetType().Name) '$Value' (windhawk-cli checks the type)" }
        }
        '^excludedApps$' { if (-not $isArray) { throw "stub: $Key without an index takes an array" } }
        '^excludedApps\[\d+\]$' { if ($Value -isnot [string]) { throw "stub: $Key takes a string" } }
        default { throw "stub: unknown setting $Key" }
    }
    $m.Settings[$Key] = $Value
    Write-StubModLog $m.Id @('[1049:Wh_ModSettingsChanged]: >', '[1027:Wh_ModUninit]: >')
    Write-StubInit $m.Id
    [pscustomobject]@{ Id = $m.Id; Changes = @(@{ key = $Key; value = "$Value" }) }
}

function Set-ModLogging {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][bool]$Enabled)
    Write-StubCall "Set-ModLogging -Id $Id -Enabled $Enabled"
    $m = Get-StubMod $Id
    $m.LoggingEnabled = $Enabled
    [pscustomobject]@{ Id = $m.Id; LoggingEnabled = $Enabled }
}

function Start-ModLogCapture {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$OutFile, [string]$ModId)
    Write-StubCall "Start-ModLogCapture -OutFile $OutFile"
    $script:StubState.LogFile = $OutFile
    Set-Content -LiteralPath $OutFile -Value "# capture started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff') (stub)"
    [pscustomobject]@{ OutFile = $OutFile }
}

function Stop-ModLogCapture {
    [CmdletBinding()] param()
    Write-StubCall 'Stop-ModLogCapture'
    $f = $script:StubState.LogFile
    $script:StubState.LogFile = $null
    [pscustomobject]@{ OutFile = $f }
}

function Get-WindhawkState {
    [CmdletBinding()] param()
    Write-StubCall 'Get-WindhawkState'
    [pscustomobject]@{
        Stub                  = $true
        AppVersion            = $script:StubState.Version
        ExplorerEngineVersion = $script:StubState.Version
        SafeMode              = $script:StubState.SafeMode
        Mods                  = @($script:StubState.Mods.Values | ForEach-Object {
            [pscustomobject]@{ Id = $_.Id; Version = $_.Version; Enabled = $_.Enabled; LoggingEnabled = $_.LoggingEnabled
                LibraryFileName = $_.LibraryFileName; LoadedInExplorer = $false; Settings = $_.Settings }
        })
    }
}

function Enter-SafeMode {
    [CmdletBinding()] param([switch]$Panic)
    Write-StubCall 'Enter-SafeMode'
    $script:StubState.SafeMode = $true
}

function Exit-SafeMode {
    [CmdletBinding()] param()
    Write-StubCall 'Exit-SafeMode'
    $script:StubState.SafeMode = $false
}
