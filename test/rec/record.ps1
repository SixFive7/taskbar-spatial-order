<#
.SYNOPSIS
    Records the primary monitor with ffmpeg until a file named STOP appears in -OutDir, for
    make-gifs.ps1. Invoke-LiveTest.ps1 -Record runs it around a live run.

.DESCRIPTION
    ffmpeg captures the primary monitor with desktop duplication (ddagrab, on the DXGI output that
    "tso-live monitors" finds for it) and encodes it with -Encoder: libx264 on the processor by
    default, or a graphics card's encoder such as h264_nvenc or h264_amf.

    Writes to -OutDir:
      screen.mkv    the recording
      started.json  as soon as frames are being encoded
      done.json     once it has stopped: { file, videoStartUtc, frames, exitCode, error }
    videoStartUtc, the video's time 0 in UTC, is estimated from ffmpeg's progress reports (the
    earliest "time of a report minus the video time it reports"); make-gifs.ps1 finds the demo's
    scenes, whose times demo.json gives in UTC, with it.
#>
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Encoder = 'libx264',
    [int]$Fps = 30,
    [int]$MaxMinutes = 60
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).ProviderPath
$video = Join-Path $OutDir 'screen.mkv'
$stop = Join-Path $OutDir 'STOP'
foreach ($f in $stop, (Join-Path $OutDir 'started.json'), (Join-Path $OutDir 'done.json')) { Remove-Item -LiteralPath $f -ErrorAction SilentlyContinue }

$cli = Join-Path (Split-Path (Split-Path $PSScriptRoot)) '.work\test\bin\TsoLiveCli\tso-live.exe'
$primary = @(& $cli monitors | ConvertFrom-Json) | Where-Object { $_.primary } | Select-Object -First 1
if (-not $primary -or $primary.dxgiOutput -lt 0) { throw 'No DXGI output found for the primary monitor (tso-live monitors).' }
$source = @()
if ($primary.dxgiAdapter -gt 0) { $source += '-init_hw_device', "d3d11va=rec:$($primary.dxgiAdapter)", '-filter_hw_device', 'rec' }
# The frames stay on the graphics card for its encoders; the processor's need them in memory.
$source += '-filter_complex', ("ddagrab=output_idx=$($primary.dxgiOutput):framerate=$($Fps):draw_mouse=1" +
                               $(if ($Encoder -like 'lib*') { ',hwdownload,format=bgra' }))
$quality = @{
    libx264    = @('-preset', 'ultrafast', '-tune', 'zerolatency', '-crf', '18', '-pix_fmt', 'yuv420p')
    h264_nvenc = @('-preset', 'p4', '-rc', 'vbr', '-cq', '23', '-b:v', '0')
    h264_amf   = @('-rc', 'cqp', '-qp_i', '20', '-qp_p', '20')
}
$ffArgs = @('-hide_banner', '-loglevel', 'error', '-nostats', '-progress', 'pipe:1', '-stats_period', '0.5', '-y') + $source + @('-c:v', $Encoder)
if ($quality.ContainsKey($Encoder)) { $ffArgs += $quality[$Encoder] }
$ffArgs += '-g', [string]($Fps * 2), '-t', [string]($MaxMinutes * 60), $video

$psi = [Diagnostics.ProcessStartInfo]::new((Get-Command ffmpeg -ErrorAction Stop).Source)
foreach ($a in $ffArgs) { $psi.ArgumentList.Add($a) }
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$ff = [Diagnostics.Process]::Start($psi)
$errors = $ff.StandardError.ReadToEndAsync()

# Progress reports: key=value lines, each block ending with progress=continue (or end).
$frames = 0; $outUs = 0L; $start = $null
$next = $ff.StandardOutput.ReadLineAsync()
while (-not (Test-Path -LiteralPath $stop)) {
    if (-not $next.Wait(20)) {
        if ($ff.HasExited) { break }
        continue
    }
    $now = [DateTime]::UtcNow
    $line = $next.Result
    if ($null -eq $line) { break }
    $next = $ff.StandardOutput.ReadLineAsync()
    if ($line -match '^frame=(\d+)') { $frames = [int]$Matches[1] }
    elseif ($line -match '^out_time_us=(\d+)') { $outUs = [long]$Matches[1] }
    elseif ($line -like 'progress=*' -and $frames -gt 0 -and $outUs -gt 0) {
        $t0 = $now.AddTicks(-10 * $outUs)
        if (-not $start) { Set-Content -LiteralPath (Join-Path $OutDir 'started.json') -Value '{}' }
        if (-not $start -or $t0 -lt $start) { $start = $t0 }
    }
}

if (-not $ff.HasExited) { try { $ff.StandardInput.Write('q'); $ff.StandardInput.Flush() } catch { } }   # ffmpeg's own way to stop
if (-not $ff.WaitForExit(20000)) { $ff.Kill() }
$ff.WaitForExit()
[void]$errors.Wait(5000)
$done = [ordered]@{
    file          = $video
    videoStartUtc = if ($start) { $start.ToString('o', [Globalization.CultureInfo]::InvariantCulture) } else { $null }
    frames        = $frames
    exitCode      = $ff.ExitCode
    error         = if ($errors.IsCompleted) { $errors.Result.Trim() } else { '' }
}
Set-Content -LiteralPath (Join-Path $OutDir 'done.json') -Value ($done | ConvertTo-Json)
