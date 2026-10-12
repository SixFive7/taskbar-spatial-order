<#
.SYNOPSIS
    Builds the kit's tools into .work\test\bin (needs the .NET 10 SDK).

.DESCRIPTION
      bin\TsoTestApp\TsoTestApp.exe   the test windows
      bin\TsoLive\TsoLive.dll         oracles, input and the runner (loaded by Invoke-LiveTest.ps1)
      bin\TsoLiveCli\tso-live.exe     the same from the command line, with the self-tests
    Invoke-LiveTest.ps1 runs this by itself when a source is newer than the last build.

.PARAMETER Idle
    Builds at idle priority, without build servers or reused nodes (which would not inherit it), for
    building while the PC is in use.
#>
[CmdletBinding()]
param([switch]$Idle)
$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path $PSScriptRoot) '.work\test\bin'
$logs = Join-Path $bin 'build-logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$extra = @()
if ($Idle) {
    (Get-Process -Id $PID).PriorityClass = 'Idle'
    $extra = @('--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false')
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
}
foreach ($project in 'TsoTestApp', 'TsoLive', 'TsoLiveCli') {
    $log = Join-Path $logs "$project.log"
    & dotnet build (Join-Path $PSScriptRoot "src\$project\$project.csproj") -c Release -nologo @extra *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content $log | Select-String ' error ' | Select-Object -First 20 | ForEach-Object { $_.Line }
        throw "build of $project failed (exit $LASTEXITCODE), see $log"
    }
    $warnings = @(Get-Content $log | Select-String ' warning ' | ForEach-Object { $_.Line } | Sort-Object -Unique)
    "built $project ($($warnings.Count) warnings)"
    $warnings | Select-Object -First 10
}
Set-Content -LiteralPath (Join-Path $bin 'build.stamp') -Value (Get-Date -Format o)
