<#
.SYNOPSIS
    Builds the logic harness with the compiler that comes with Windhawk and runs it: the mod's logic
    against a model of the taskbar, with no Explorer, no window and no input. Changes nothing on the
    PC and takes about half a minute.

.DESCRIPTION
    Writes .work\test\harness\harness.exe, build.log and run.log, and prints one line per test and
    the total as it runs. Exit code 0 when the harness builds without a warning and every check
    passes.
#>
param(
    [string]$ModPath = (Join-Path $PSScriptRoot '..\..\taskbar-spatial-order.wh.cpp'),
    [string]$WindhawkDir = (Join-Path $env:ProgramFiles 'Windhawk')
)
$ErrorActionPreference = 'Stop'
$out = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.work\test\harness'))
New-Item -ItemType Directory -Force -Path $out | Out-Null
$clang = Join-Path $WindhawkDir 'Compiler\bin\clang++.exe'
if (-not (Test-Path -LiteralPath $clang)) { throw "No compiler at ${clang}: is Windhawk installed there?" }

# harness.cpp includes the mod; WH_EDITING makes Windhawk's functions inline stubs.
$mod = (Resolve-Path -LiteralPath $ModPath).Path -replace '\\', '/'
$exe = Join-Path $out 'harness.exe'
$log = Join-Path $out 'build.log'
& $clang -std=c++23 -O2 -fno-strict-aliasing -DUNICODE -D_UNICODE -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 `
    -D_WIN32_IE=0x0A00 -DNTDDI_VERSION=0x0A000008 -D__USE_MINGW_ANSI_STDIO=0 -DWH_MOD -DWH_EDITING `
    "-DMOD_FILE=`"$mod`"" -include windhawk_api.h -target x86_64-w64-mingw32 -static `
    (Join-Path $PSScriptRoot 'harness.cpp') -o $exe -ldwmapi -Wall -Wextra *> $log
$exit = $LASTEXITCODE
$warnings = @(Select-String -LiteralPath $log -Pattern 'warning:').Count
"build: exit=$exit warnings=$warnings"
if ($exit -ne 0 -or $warnings -ne 0) { Get-Content -LiteralPath $log | Select-Object -First 20 }
if ($exit -ne 0) { exit 1 }

& $exe | Tee-Object -FilePath (Join-Path $out 'run.log')
exit ([int]($LASTEXITCODE -ne 0 -or $warnings -ne 0))
