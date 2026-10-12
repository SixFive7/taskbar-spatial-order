<#
.SYNOPSIS
    Tests rec\make-gifs.ps1 on a generated recording: no screen capture, no graphics card (libx264).

.DESCRIPTION
    1. The demo's layout on the primary monitor (tso-live demo-layout: monitor geometry only).
    2. A made-up demo.json like the demo step writes (two scenes, the "with" one longer than 6 s so it
       must be sped up), and a done.json whose recording started at a fixed UTC time.
    3. A made-up recording of the primary monitor's size at 30 fps, drawn with ffmpeg: wallpaper,
       taskbar with other buttons and the test button, the four windows opening, D dragged to the far
       left, three flyouts, a cursor; then brightened the way an HDR monitor's 8-bit capture is
       (linear x2.5, clipped), encoded BT.709 limited range with the colour tags cleared.
    4. make-gifs.ps1 run under a comma-decimal culture (nl-NL).
    5. Checks: both GIFs, 640 wide, at most 6 s, under 1 MB; the colour fit found the gain and the
       matrix; a window's colour and a masked button's area read back as the true colours.
    The generated video and the work files are deleted afterwards; the GIFs and the contact sheet stay
    in -OutDir (.work\test\gif-test) for a look.
#>
param([string]$OutDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) '.work\test\gif-test'), [switch]$KeepVideo)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
[Threading.Thread]::CurrentThread.CurrentCulture = $inv
function N([double]$v, [string]$f = '0.####') { $v.ToString($f, $inv) }

$cli = Join-Path (Split-Path (Split-Path $PSScriptRoot)) '.work\test\bin\TsoLiveCli\tso-live.exe'
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
$rec = Join-Path $OutDir 'rec dir with spaces'
$run = Join-Path $OutDir 'run'
New-Item -ItemType Directory -Force -Path $rec, $run | Out-Null
$checks = 0; $fails = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') { $script:checks++; if (-not $ok) { $script:fails++ }; "$(if ($ok) { 'ok  ' } else { 'FAIL' }) $what $detail" }

# ------------------------------------------------------------------ 1. layout
$L = (& $cli demo-layout) -join "`n" | ConvertFrom-Json
$crop = $L.crop; $button = $L.button; $slots = @($L.slots); $rest = $L.rest
$tbTop = [int]$L.work.h; $monH = [int]$L.monitor.h
"layout: crop $($crop.w)x$($crop.h) at $($crop.x),$($crop.y); button at $($button.x); slots y $($slots[0].y)"

# ------------------------------------------------------------------ 2. demo.json and done.json
$t0 = [DateTime]::new(2026, 1, 1, 12, 0, 0, [DateTimeKind]::Utc)
function U([double]$s) { $t0.AddSeconds($s).ToString('o', $inv) }
$bx = [int]$button.x; $by = [int]$button.y; $bw = [int]$button.w; $bh = [int]$button.h
$others = @(
    @{ x = $bx - 140; y = $tbTop; w = 44; h = $bh }, @{ x = $bx - 92; y = $tbTop; w = 44; h = $bh }, @{ x = $bx - 48; y = $tbTop; w = 44; h = $bh },
    @{ x = [int]$crop.x - 20; y = $tbTop; w = 44; h = $bh })
$true4 = [ordered]@{ A = 'C81E1E'; B = '1E8C1E'; C = '1E46C8'; D = 'B8860B' }
$slotOf = @{ A = 1; B = 2; C = 3; D = 4 }
$refs = @(foreach ($k in 'A', 'B', 'C') { $s = $slots[$slotOf[$k]]; @{ utc = (U 8.2); what = "window $k"; x = [int]$s.x + 24; y = [int]$s.y + [int]$s.h - 20; rgb = $true4[$k] } })
$refs += @{ utc = (U 8.2); what = 'window D'; x = [int]$slots[4].x + 24; y = [int]$slots[4].y + [int]$slots[4].h - 20; rgb = $true4['D'] }
$refs += @{ utc = (U 8.2); what = 'taskbar (GDI copy)'; x = [int]$crop.x + [int]$crop.w - 40; y = $tbTop + 24; rgb = '202020' }
$demo = [ordered]@{
    version = 1; runId = 'synthetic'; dry = $false; createdUtc = (U 17)
    monitor = @{ device = $L.device; x = 0; y = 0; w = [int]$L.monitor.w; h = $monH; workBottom = $tbTop }
    crop = $crop; taskbar = @{ y = $tbTop; h = $monH - $tbTop }; slots = $slots; colourRefs = $refs
    testButton = $button; otherButtons = $others
    scenes = @(
        [ordered]@{ name = 'without'; startUtc = (U 1.0); endUtc = (U 6.8); expected = 'C,A,D,B'; flyout = 'C,A,D,B'; marks = @(); testButton = $button; otherButtons = $others }
        [ordered]@{ name = 'with'; startUtc = (U 8.5); endUtc = (U 16.6); expected = 'A,B,C,D then D,A,B,C'; flyout = 'A,B,C,D then D,A,B,C'; marks = @(); testButton = $button; otherButtons = $others })
}
$demoPath = Join-Path $run 'demo.json'
[IO.File]::WriteAllText($demoPath, ($demo | ConvertTo-Json -Depth 6))
$main = Join-Path $rec 'screen.mkv'
$doneJson = [ordered]@{ file = $main; videoStartUtc = (U 0); frames = 525; exitCode = 0; error = '' }
[IO.File]::WriteAllText((Join-Path $rec 'done.json'), ($doneJson | ConvertTo-Json))

# ------------------------------------------------------------------ 3. the synthetic recording
function Lin([double]$c) { if ($c -le 0.04045) { $c / 12.92 } else { [Math]::Pow(($c + 0.055) / 1.055, 2.4) } }
function Enc([double]$l) { if ($l -le 0) { 0.0 } elseif ($l -ge 1) { 1.0 } elseif ($l -le 0.0031308) { 12.92 * $l } else { 1.055 * [Math]::Pow($l, 1 / 2.4) - 0.055 } }
$gain = 2.5
$cube = @('TITLE "HDR brightening x2.5"', 'LUT_1D_SIZE 256') + @(0..255 | ForEach-Object { $o = N (Enc ([Math]::Min(1.0, $gain * (Lin ($_ / 255))))) '0.000000'; "$o $o $o" })
[IO.File]::WriteAllLines((Join-Path $rec 'boost.cube'), $cube)

# Piecewise-linear expression of t through keyframes (time, value).
function PL($keys) {
    $e = [string]$keys[-1][1]
    for ($i = $keys.Count - 2; $i -ge 0; $i--) {
        $a = $keys[$i]; $b = $keys[$i + 1]
        $seg = if ($b[0] -eq $a[0]) { "$($b[1])" } else { "($($a[1])+($($b[1])-($($a[1])))*(t-$(N $a[0]))/$(N ($b[0] - $a[0])))" }
        $e = "if(lt(t\,$(N $b[0]))\,$seg\,$e)"
    }
    return "if(lt(t\,$(N $keys[0][0]))\,$($keys[0][1])\,$e)"
}
$cxB = $bx + [int]($bw / 2); $cyB = $by + [int]($bh / 2)
$grabX = [int]$slots[4].x + 40; $grabY = [int]$slots[4].y + 14
$dropX = $grabX - ([int]$slots[4].x - [int]$slots[0].x)
$cur = @(@(0, 0), @(2.8, 0), @(3.45, 1), @(5.95, 1), @(6.5, 0), @(8.9, 0), @(9.55, 1), @(11.05, 1), @(11.65, 2), @(11.9, 2), @(12.85, 3), @(13.4, 3), @(14.05, 1), @(15.85, 1), @(16.4, 0))
$pts = @(@($rest.x, $rest.y), @($cxB, $cyB), @($grabX, $grabY), @($dropX, $grabY))
$curX = PL @($cur | ForEach-Object { , @($_[0], $pts[$_[1]][0]) })
$curY = PL @($cur | ForEach-Object { , @($_[0], $pts[$_[1]][1]) })
$dX = PL @(@(11.9, [int]$slots[4].x), @(12.85, [int]$slots[0].x))

# Everything is drawn in RGB (the overlays too, format=gbrp): a YUV round trip would shift the true colours.
$f = [Collections.Generic.List[string]]::new()
$f.Add("[0:v]format=gbrp,drawbox=x=0:y=$($tbTop):w=iw:h=$($monH - $tbTop):color=0x202020@1:t=fill")
foreach ($o in $others) { $f.Add("drawbox=x=$($o.x):y=$($o.y + 4):w=$($o.w):h=$($o.h - 8):color=0x6A7FA0@1:t=fill") }
$f.Add("drawbox=x=$($bx):y=$($by):w=$($bw):h=$($bh):color=0x2E4A6E@1:t=fill,drawbox=x=$($bx + 10):y=$($by + 12):w=24:h=24:color=0xC81E1E@1:t=fill")
$opened = @{ C = 1.4; A = 1.72; D = 2.04; B = 2.36 }
foreach ($k in 'A', 'B', 'C', 'D') {
    $s = $slots[$slotOf[$k]]
    $en = if ($k -eq 'D') { "between(t\,$(N $opened[$k])\,11.9)" } else { "gte(t\,$(N $opened[$k]))" }
    $f.Add("drawbox=x=$($s.x):y=$($s.y):w=$($s.w):h=$($s.h):color=0x$($true4[$k])@1:t=fill:enable='$en'")
    $f.Add("drawbox=x=$($s.x):y=$($s.y):w=$($s.w):h=30:color=0xF3F3F3@1:t=fill:enable='$en'")
}
$s0 = $slots[0]
$f.Add("drawbox=x=$($s0.x):y=$($s0.y):w=$($s0.w):h=$($s0.h):color=0x$($true4['D'])@1:t=fill:enable='gte(t\,12.85)'")
$f.Add("drawbox=x=$($s0.x):y=$($s0.y):w=$($s0.w):h=30:color=0xF3F3F3@1:t=fill:enable='gte(t\,12.85)'")
$fx = $cxB - 380; $fy = $tbTop - 210
foreach ($fl in @(@(3.85, 5.95, 'C', 'A', 'D', 'B'), @(9.95, 11.1, 'A', 'B', 'C', 'D'), @(14.45, 15.9, 'D', 'A', 'B', 'C'))) {
    $en = "between(t\,$(N $fl[0])\,$(N $fl[1]))"
    $f.Add("drawbox=x=$($fx):y=$($fy):w=760:h=200:color=0x2B2B2B@1:t=fill:enable='$en'")
    for ($i = 0; $i -lt 4; $i++) { $f.Add("drawbox=x=$($fx + 30 + $i * 180):y=$($fy + 60):w=150:h=100:color=0x$($true4[$fl[2 + $i]])@1:t=fill:enable='$en'") }
}
$chain = ($f -join ',') + '[bg]'
$chain += ";[1:v]format=gbrp,drawbox=x=0:y=0:w=iw:h=30:color=0xF3F3F3@1:t=fill[dwin]"
$chain += ";[bg][dwin]overlay=x='$dX':y=$($slots[4].y):format=gbrp:enable='between(t\,11.9\,12.85)'[b2]"
$chain += ";[2:v]format=gbrp[cur];[b2][cur]overlay=x='$curX':y='$curY':format=gbrp"
$chain += ",lut1d=file=boost.cube,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=colorspace=unknown:range=unknown:color_trc=unknown:color_primaries=unknown[out]"
[IO.File]::WriteAllText((Join-Path $rec 'synthetic.fc'), $chain)
$sw = [Diagnostics.Stopwatch]::StartNew()
Push-Location -LiteralPath $rec
try {
    & ffmpeg -hide_banner -loglevel error -nostdin -f lavfi -i "color=c=0x0E1621:s=$($L.monitor.w)x$($monH):r=30:d=17.5" `
        -f lavfi -i "color=c=0x$($true4['D']):s=$($slots[4].w)x$($slots[4].h):r=30:d=17.5" -f lavfi -i 'color=c=white:s=14x20:r=30:d=17.5' `
        -/filter_complex synthetic.fc -map '[out]' -c:v libx264 -preset ultrafast -crf 14 -g 60 -y screen.mkv 2> synthetic.err
    if ($LASTEXITCODE -ne 0) { throw "synthetic video: ffmpeg exit ${LASTEXITCODE}: $(Get-Content synthetic.err -Raw)" }
}
finally { Pop-Location }
"synthetic recording: $([Math]::Round((Get-Item -LiteralPath $main).Length / 1MB, 1)) MB in $([int]$sw.Elapsed.TotalSeconds) s"

# ------------------------------------------------------------------ 4. make-gifs under a comma-decimal culture
$gifs = Join-Path $OutDir 'gifs'
$mg = Join-Path $PSScriptRoot 'make-gifs.ps1'
$cmd = "[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('nl-NL'); [Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo('nl-NL'); try { & '$mg' -RecDir '$rec' -Demo '$demoPath' -OutDir '$gifs'; exit `$LASTEXITCODE } catch { `$_ | Out-String; `$_.ScriptStackTrace; exit 9 }"
$sw.Restart()
& (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -Command $cmd *> (Join-Path $OutDir 'make-gifs.out')
$mgExit = $LASTEXITCODE
"make-gifs exit $mgExit in $([int]$sw.Elapsed.TotalSeconds) s:"
Get-Content -LiteralPath (Join-Path $OutDir 'make-gifs.out')

# ------------------------------------------------------------------ 5. checks
Check 'make-gifs exit code 0' ($mgExit -eq 0)
$sum = Get-Content -LiteralPath (Join-Path $gifs 'gifs.json') -Raw | ConvertFrom-Json
Check 'colour fit: matrix bt709' ($sum.colour.matrix -eq 'bt709') $sum.colour.matrix
Check 'colour fit: range tv (limited)' ($sum.colour.range -eq 'tv') $sum.colour.range
Check 'colour fit: gain about 2.5' ([Math]::Abs([double]$sum.colour.gain - $gain) -lt 0.2) "$($sum.colour.gain) (tried: $($sum.colour.tried -join '; '))"
foreach ($g in $sum.gifs) {
    Check "$($g.name).gif 640 wide" ($g.width -eq 640) "$($g.width)x$($g.height)"
    Check "$($g.name).gif at most 6 s" ([double]$g.seconds -le 6.05) "$($g.seconds) s (speed x$($g.speed))"
    Check "$($g.name).gif under 1 MB" ($g.kb -le 1024) "$($g.kb) KB, $($g.colors) colours, $($g.fps) fps"
    Check "$($g.name).gif 4 masks" (@($g.masks).Count -eq 4) ($g.masks -join ' ')
}
# Read pixels through a file (binary safe).
function GifRgb([string]$file, [double]$at, [int]$x, [int]$y) {
    $tmp = Join-Path $OutDir 'px.rgb'
    & ffmpeg -hide_banner -loglevel error -nostdin -i $file -vf "select='gte(t\,$(N $at))',crop=3:3:$($x - 1):$($y - 1),format=rgb24" -frames:v 1 -f rawvideo -y $tmp
    $b = [IO.File]::ReadAllBytes($tmp)
    Remove-Item -LiteralPath $tmp
    return @(($b[12]), ($b[13]), ($b[14]))   # the middle pixel of the 3x3
}
$scale = 640 / [double]$crop.w
function ToGif([int]$vx, [int]$vy) { @([int][Math]::Round(($vx - [int]$crop.x) * $scale), [int][Math]::Round(($vy - [int]$crop.y) * $scale)) }
$wo = Join-Path $gifs 'without.gif'
$sB = $slots[2]
$p = ToGif ([int]$sB.x + 40) ([int]$sB.y + 110)
$rgb = GifRgb $wo 3.0 $p[0] $p[1]
$want = @(0x1E, 0x8C, 0x1E)
$d = (0..2 | ForEach-Object { [Math]::Abs($rgb[$_] - $want[$_]) } | Measure-Object -Maximum).Maximum
Check 'window B reads back as its true colour 1E8C1E (HDR brightening undone)' ($d -le 16) "($($rgb -join ',')), largest difference $d"
$m = $others[1]
$p = ToGif ($m.x + 22) ($tbTop + 24)
$rgb = GifRgb $wo 3.0 $p[0] $p[1]
$d = (0..2 | ForEach-Object { [Math]::Abs($rgb[$_] - 0x20) } | Measure-Object -Maximum).Maximum
Check 'a masked taskbar button shows the taskbar colour 202020, not the button' ($d -le 10) "($($rgb -join ',')), largest difference $d"
$p = ToGif ($cxB) ($by + 24)
$rgb = GifRgb $wo 1.5 $p[0] $p[1]
Check 'the test button is not masked (its red icon shows)' ($rgb[0] -gt 150 -and $rgb[1] -lt 70) "($($rgb -join ','))"
Check 'contact sheet written' (Test-Path -LiteralPath (Join-Path $gifs 'contact-sheet.png'))

# ------------------------------------------------------------------ clean-up: the big generated files go
if (-not $KeepVideo) { Remove-Item -LiteralPath $rec -Recurse -Force; "generated recording deleted; kept: $gifs" }
"make-gifs synthetic test: $checks checks, $fails failures"
exit ([int]($fails -gt 0))
