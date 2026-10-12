<#
.SYNOPSIS
    Compiles the mod with the compiler that comes with Windhawk, the way Windhawk compiles a mod, plus
    -Wall -Wextra, for x86-64 and ARM64 (what "@architecture x86-64" builds). Changes nothing in
    Windhawk: a quick check before a live run, which installs the mod in step 2.

.DESCRIPTION
    Writes .work\test\compile\<arch>.dll and <arch>.log, and prints one line per architecture. Exit
    code 0 when both compile without a warning.
#>
param(
    [string]$ModPath = (Join-Path (Split-Path $PSScriptRoot) 'taskbar-spatial-order.wh.cpp'),
    [string]$WindhawkDir = (Join-Path $env:ProgramFiles 'Windhawk')
)
$ErrorActionPreference = 'Stop'
$out = Join-Path (Split-Path $PSScriptRoot) '.work\test\compile'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$clang = Join-Path $WindhawkDir 'Compiler\bin\clang++.exe'
if (-not (Test-Path -LiteralPath $clang)) { throw "No compiler at ${clang}: is Windhawk installed there?" }
$engine = Join-Path $WindhawkDir ((Get-Content -LiteralPath (Join-Path $WindhawkDir 'windhawk.ini') |
    Select-String '^\s*EnginePath\s*=\s*(.+?)\s*$' | Select-Object -First 1).Matches[0].Groups[1].Value)

$source = Get-Content -LiteralPath $ModPath -Raw
function Meta([string]$Key) { [regex]::Match($source, "(?m)^//\s*@$Key\s+(.+?)\s*$").Groups[1].Value }
$id = Meta 'id'
$version = Meta 'version'
$options = @((Meta 'compilerOptions') -split '\s+' | Where-Object { $_ })

$failed = $false
foreach ($target in @{ Arch = 'x86_64'; Lib = '64' }, @{ Arch = 'aarch64'; Lib = 'arm64' }) {
    $dll = Join-Path $out "$($target.Arch).dll"
    $log = Join-Path $out "$($target.Arch).log"
    & $clang -std=c++23 -O2 -shared -DUNICODE -D_UNICODE -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -D_WIN32_IE=0x0A00 `
        -DNTDDI_VERSION=0x0A000008 -D__USE_MINGW_ANSI_STDIO=0 -DWH_MOD "-DWH_MOD_ID=L`"$id`"" "-DWH_MOD_VERSION=L`"$version`"" `
        (Join-Path $engine "$($target.Lib)\windhawk.lib") -x c++ $ModPath -include windhawk_api.h `
        -target "$($target.Arch)-w64-mingw32" "-Wl,--export-all-symbols" -o $dll @options -Wall -Wextra *> $log
    $exit = $LASTEXITCODE
    $warnings = @(Select-String -LiteralPath $log -Pattern 'warning:').Count
    $errors = @(Select-String -LiteralPath $log -Pattern 'error:').Count
    "$($target.Arch): exit=$exit warnings=$warnings errors=$errors"
    $failed = $failed -or $exit -ne 0 -or $warnings -ne 0
}
exit ([int]$failed)
