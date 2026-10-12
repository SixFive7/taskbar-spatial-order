<#
.SYNOPSIS
    Cuts the README GIFs out of a recording of the demo: without.gif, with.gif and contact-sheet.png.

.DESCRIPTION
    Input: rec\record.ps1's recording of the primary monitor (the video and the UTC time of its start,
    from <RecDir>\done.json) and the demo step's demo.json (scene times in UTC, the crop, the test
    button, every other taskbar button in the crop, points of known colour).
    Invoke-LiveTest.ps1 -Record runs this by itself after the run.

    For each scene: the crop is cut out, the other taskbar buttons are painted over with the colour
    of the taskbar right beside them, the colours are corrected (below), then the clip is scaled to
    -Width (lanczos), resampled to -Fps and turned into a GIF with its own palette (palettegen with
    stats_mode=diff, paletteuse with diff_mode=rectangle). A scene longer than -MaxSeconds is played
    a little faster to fit. Above -MaxKB the GIF is made again with fewer colours, then a lower frame
    rate, then a smaller width. -ExtraMasks "x,y,w,h;..." paints over more rectangles (in the
    recording's pixels); -MaskColor 0xRRGGBB sets the colour they are painted with.

    Colours: an 8-bit desktop-duplication capture of a monitor in HDR mode comes out brightened (linear
    light times a gain, clipped). demo.json carries points whose true colour is known (the test
    windows' fill, the taskbar as a GDI copy shows it). The script reads them from the recording and
    picks the YUV matrix and range, and the brightening gain, that explain them best; a gain well
    above 1 is undone with a 1D LUT (values that were clipped stay white). -Hdr off / -Hdr <gain>,
    -Matrix and -Range override the choice.

    Numbers and dates are handled with the invariant culture; dates in the JSON files stay strings.
    Writes gifs.json (what was done) next to the GIFs; the work files are removed unless -KeepWork.

.EXAMPLE
    pwsh -NoProfile -File test\rec\make-gifs.ps1 -RecDir <run dir>\recording -Demo <run dir>\demo.json
#>
param(
    [Parameter(Mandatory)][string]$RecDir,
    [Parameter(Mandatory)][string]$Demo,
    [string]$OutDir,
    [int]$Width = 640,
    [int]$Fps = 15,
    [double]$MaxSeconds = 6.0,
    [int]$MaxKB = 1024,
    [double]$Lead = 0.0,
    [double]$Tail = 0.0,
    [string]$Hdr = 'auto',
    [ValidateSet('auto', 'bt709', 'bt601')][string]$Matrix = 'auto',
    [ValidateSet('auto', 'tv', 'pc')][string]$Range = 'auto',
    [string]$ExtraMasks = '',
    [string]$MaskColor = '',
    [switch]$KeepWork
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
[Threading.Thread]::CurrentThread.CurrentCulture = $inv
[Threading.Thread]::CurrentThread.CurrentUICulture = $inv

function N([double]$v, [string]$f = '0.###') { $v.ToString($f, $inv) }
function ToUtc($v) {
    if ($v -is [DateTime]) { return $v.ToUniversalTime() }
    return [DateTime]::Parse([string]$v, $inv, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}
function Read-Json([string]$Path) {
    $raw = Get-Content -LiteralPath $Path -Raw
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { return ($raw | ConvertFrom-Json -DateKind String) }
    return ($raw | ConvertFrom-Json)
}
function Prop($o, [string]$name) {
    if ($null -eq $o) { return $null }
    $p = $o.PSObject.Properties[$name]
    if ($p) { return $p.Value } else { return $null }
}

$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$ffprobe = (Get-Command ffprobe -ErrorAction Stop).Source
$RecDir = (Resolve-Path -LiteralPath $RecDir).ProviderPath
$Demo = (Resolve-Path -LiteralPath $Demo).ProviderPath
if (-not $OutDir) { $OutDir = Join-Path (Split-Path $Demo) 'gifs' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).ProviderPath
$work = Join-Path $OutDir 'work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$ffLog = Join-Path $work 'ffmpeg.log'

function FF([string[]]$FfArgs, [string]$What) {
    Add-Content -LiteralPath $ffLog -Value ("--- $What`nffmpeg " + ($FfArgs -join ' '))
    Push-Location -LiteralPath $work   # filter files are given relative to here (no escaping of C:\)
    try { & $ffmpeg -hide_banner -loglevel error -nostdin @FfArgs 2>> $ffLog }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed ($What), exit $LASTEXITCODE; see $ffLog" }
}

# ------------------------------------------------------------------ inputs
$rec = Read-Json (Join-Path $RecDir 'done.json')
$videoFile = Prop $rec 'file'
if (-not $videoFile -or -not (Test-Path -LiteralPath $videoFile)) { $videoFile = Join-Path $RecDir 'screen.mkv' }
if (-not (Test-Path -LiteralPath $videoFile)) { throw "recording not found: $videoFile" }
if (-not (Prop $rec 'videoStartUtc')) { throw "done.json has no start time for the recording (did it record any frames?)" }
$videoStart = ToUtc $rec.videoStartUtc

$d = Read-Json $Demo
$crop = $d.crop
$cx = [int]$crop.x; $cy = [int]$crop.y; $cw = [int]$crop.w; $ch = [int]$crop.h
$tbY = [int]$d.taskbar.y - $cy; $tbH = [int]$d.taskbar.h

$probe = (& $ffprobe -v error -select_streams v:0 -show_entries 'stream=width,height,color_space,color_range,r_frame_rate:format=duration' -of json $videoFile) -join "`n" | ConvertFrom-Json
$st = $probe.streams[0]
$vw = [int]$st.width; $vh = [int]$st.height
$duration = [double]::Parse([string]$probe.format.duration, $inv)
if ($cx -lt 0 -or $cy -lt 0 -or $cx + $cw -gt $vw -or $cy + $ch -gt $vh) { throw "crop ${cw}x$ch at $cx,$cy is outside the ${vw}x$vh video" }
$tagMatrix = [string](Prop $st 'color_space'); $tagRange = [string](Prop $st 'color_range')

# ------------------------------------------------------------------ colour: matrix, range, HDR gain
function Lin([double]$c) { if ($c -le 0.04045) { $c / 12.92 } else { [Math]::Pow(($c + 0.055) / 1.055, 2.4) } }
function Enc([double]$l) { if ($l -le 0) { 0.0 } elseif ($l -ge 1) { 1.0 } elseif ($l -le 0.0031308) { 12.92 * $l } else { 1.055 * [Math]::Pow($l, 1 / 2.4) - 0.055 } }

function Get-Frame([double]$At, [string]$M, [string]$R) {
    $file = Join-Path $work ("frame-{0}-{1}-{2}.rgb" -f (N $At '0.000'), $M, $R)
    if (-not (Test-Path -LiteralPath $file)) {
        FF @('-ss', (N $At '0.000'), '-i', $videoFile, '-frames:v', '1',
             '-vf', "crop=${cw}:${ch}:${cx}:${cy},scale=in_color_matrix=$($M):in_range=$($R):out_range=pc,format=rgb24",
             '-f', 'rawvideo', '-y', (Split-Path $file -Leaf)) "reference frame at $(N $At) s ($M, $R)"
    }
    return , [IO.File]::ReadAllBytes($file)   # one byte[] (not three million pipeline items)
}
function Pixel([byte[]]$F, [int]$X, [int]$Y, [int]$Radius = 2) {
    $s = @(0.0, 0.0, 0.0); $n = 0
    for ($yy = [Math]::Max(0, $Y - $Radius); $yy -le [Math]::Min($ch - 1, $Y + $Radius); $yy++) {
        for ($xx = [Math]::Max(0, $X - $Radius); $xx -le [Math]::Min($cw - 1, $X + $Radius); $xx++) {
            $i = ($yy * $cw + $xx) * 3
            $s[0] += $F[$i]; $s[1] += $F[$i + 1]; $s[2] += $F[$i + 2]; $n++
        }
    }
    return @(($s[0] / $n), ($s[1] / $n), ($s[2] / $n))   # (the comma binds tighter than the division)
}
function Fit($Samples, [double]$FixedGain) {
    # Samples: @{ T = true 0..255 per channel; R = recorded 0..255 } -> gain and mean error.
    $ratios = [Collections.Generic.List[double]]::new()
    foreach ($s in $Samples) {
        for ($c = 0; $c -lt 3; $c++) {
            $t = Lin ($s.T[$c] / 255); $r = $s.R[$c]
            if ($t -gt 0.003 -and $r -gt 12 -and $r -lt 243) { $ratios.Add((Lin ($r / 255)) / $t) }
        }
    }
    $g = if ($FixedGain -gt 0) { $FixedGain } elseif ($ratios.Count -gt 0) { ($ratios | Sort-Object)[[int][Math]::Floor($ratios.Count / 2)] } else { 1.0 }
    $err = 0.0; $n = 0
    foreach ($s in $Samples) {
        for ($c = 0; $c -lt 3; $c++) {
            $pred = 255 * (Enc ([Math]::Min(1.0, $g * (Lin ($s.T[$c] / 255)))))
            $err += [Math]::Abs($pred - $s.R[$c]); $n++
        }
    }
    return [pscustomobject]@{ Gain = $g; Error = $(if ($n) { $err / $n } else { [double]::NaN }); Ratios = $ratios.Count }
}

$refs = @(Prop $d 'colourRefs' | Where-Object { $_ })
$fixedGain = 0.0
if ($Hdr -eq 'off') { $fixedGain = 1.0 } elseif ($Hdr -ne 'auto') { $fixedGain = [double]::Parse($Hdr, $inv) }
$matrices = if ($Matrix -ne 'auto') { @($Matrix) } elseif ($tagMatrix -eq 'bt709') { @('bt709') } elseif ($tagMatrix -in 'smpte170m', 'bt470bg') { @('bt601') } else { @('bt709', 'bt601') }
$ranges = if ($Range -ne 'auto') { @($Range) } elseif ($tagRange -eq 'tv') { @('tv') } elseif ($tagRange -eq 'pc' -and $tagMatrix -notin 'gbr', 'unknown', '') { @('pc') } else { @('tv', 'pc') }
$choice = [pscustomobject]@{ Matrix = $matrices[0]; Range = $ranges[0]; Gain = $(if ($fixedGain -gt 0) { $fixedGain } else { 1.0 }); Error = [double]::NaN; Samples = 0; Tried = @() }
$refAt = $null
if ($refs.Count -gt 0) {
    $refAt = ((ToUtc $refs[0].utc) - $videoStart).TotalSeconds
    $tried = foreach ($m in $matrices) {
        foreach ($r in $ranges) {
            $frame = Get-Frame $refAt $m $r
            $samples = foreach ($ref in $refs) {
                $px = [int]$ref.x - $cx; $py = [int]$ref.y - $cy
                if ($px -lt 0 -or $py -lt 0 -or $px -ge $cw -or $py -ge $ch) { continue }
                $hex = [Convert]::ToInt32([string]$ref.rgb, 16)
                @{ T = @((($hex -shr 16) -band 255), (($hex -shr 8) -band 255), ($hex -band 255)); R = (Pixel $frame $px $py 2); What = [string]$ref.what }
            }
            $fit = Fit @($samples) $fixedGain
            [pscustomobject]@{ Matrix = $m; Range = $r; Gain = $fit.Gain; Error = $fit.Error; Samples = @($samples).Count; Ratios = $fit.Ratios }
        }
    }
    $best = $tried | Sort-Object Error | Select-Object -First 1
    $choice = [pscustomobject]@{ Matrix = $best.Matrix; Range = $best.Range; Gain = $best.Gain; Error = $best.Error; Samples = $best.Samples; Tried = @($tried) }
}
$correct = [Math]::Abs($choice.Gain - 1.0) -gt 0.05
if ($correct) {
    # Inverse of "linear times gain, clipped": exact up to a knee; above it, on to white (those were clipped).
    $knee = 235
    $kneeOut = Enc ((Lin ($knee / 255)) / $choice.Gain)
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('TITLE "undo HDR brightening, gain ' + (N $choice.Gain '0.000') + '"')
    $lines.Add('LUT_1D_SIZE 256')
    for ($v = 0; $v -le 255; $v++) {
        $o = if ($v -le $knee) { Enc ((Lin ($v / 255)) / $choice.Gain) } else { $kneeOut + (1 - $kneeOut) * ($v - $knee) / (255 - $knee) }
        $s = N $o '0.000000'
        $lines.Add("$s $s $s")
    }
    [IO.File]::WriteAllLines((Join-Path $work 'hdr.cube'), $lines)
}

# ------------------------------------------------------------------ masks: other taskbar buttons
function Rects($list) { @($list | Where-Object { $_ } | ForEach-Object { [pscustomobject]@{ X = [int]$_.x; Y = [int]$_.y; W = [int]$_.w; H = [int]$_.h } }) }
$extra = @()
if ($ExtraMasks) {
    $extra = @($ExtraMasks.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object {
        $p = $_.Split(',') | ForEach-Object { [int]::Parse($_.Trim(), $inv) }
        [pscustomobject]@{ X = $p[0]; Y = $p[1]; W = $p[2]; H = $p[3] } })
}

function Mask-Filters($Scene, [byte[]]$Frame) {
    $others = Rects $(if (Prop $Scene 'otherButtons') { $Scene.otherButtons } else { Prop $d 'otherButtons' })
    $test = @(Rects $(if (Prop $Scene 'testButton') { @($Scene.testButton) } else { @(Prop $d 'testButton') }))
    $all = @($others) + @($extra)
    $avoid = @($all) + @($test)
    $midY = [Math]::Min($ch - 1, $tbY + [int]($tbH / 2))
    $filters = [Collections.Generic.List[string]]::new()
    $notes = [Collections.Generic.List[string]]::new()
    foreach ($m in $all) {
        # Crop coordinates, one pixel larger on every side (hover halos, anti-aliasing), clipped.
        $x0 = [Math]::Max(0, $m.X - $cx - 1); $y0 = [Math]::Max(0, $m.Y - $cy - 1)
        $x1 = [Math]::Min($cw, $m.X - $cx + $m.W + 1); $y1 = [Math]::Min($ch, $m.Y - $cy + $m.H + 1)
        if ($x1 - $x0 -lt 1 -or $y1 - $y0 -lt 1) { continue }
        # The taskbar's colour right beside the button, left and right, where no button is.
        $samples = [Collections.Generic.List[object]]::new()
        foreach ($dir in -1, 1) {
            for ($k = 3; $k -le 60; $k += 2) {
                $sx = if ($dir -lt 0) { $x0 - $k } else { $x1 - 1 + $k }
                if ($sx -lt 0 -or $sx -ge $cw) { break }
                $vx = $sx + $cx; $vy = $midY + $cy
                $inside = $avoid | Where-Object { $vx -ge $_.X - 2 -and $vx -lt $_.X + $_.W + 2 -and $vy -ge $_.Y -and $vy -lt $_.Y + $_.H }
                if (-not $inside) { $samples.Add((Pixel $Frame $sx $midY 1)); break }
            }
        }
        if ($samples.Count -eq 0) { $samples.Add((Pixel $Frame ([Math]::Min($cw - 1, 2)) $midY 1)) }
        $rgb = @(0, 1, 2 | ForEach-Object { $c = $_; [int][Math]::Round((($samples | ForEach-Object { $_[$c] }) | Measure-Object -Average).Average) })
        $hex = '0x{0:X2}{1:X2}{2:X2}' -f $rgb[0], $rgb[1], $rgb[2]
        if ($MaskColor) { $hex = $MaskColor }
        $filters.Add("drawbox=x=$($x0):y=$($y0):w=$($x1 - $x0):h=$($y1 - $y0):color=$($hex)@1:t=fill")
        $notes.Add("$($m.X),$($m.Y),$($m.W)x$($m.H)=$hex")
    }
    return [pscustomobject]@{ Filters = $filters; Notes = $notes }
}

# ------------------------------------------------------------------ the GIFs
$scenes = @($d.scenes | Where-Object { (Prop $_ 'startUtc') -and (Prop $_ 'endUtc') })
if ($scenes.Count -eq 0) { throw 'demo.json has no finished scene' }
$attempts = @(@{ Colors = 256; Fps = $Fps; Width = $Width }, @{ Colors = 128; Fps = $Fps; Width = $Width }, @{ Colors = 128; Fps = 12; Width = $Width },
              @{ Colors = 96; Fps = 12; Width = [Math]::Min($Width, 600) }, @{ Colors = 64; Fps = 12; Width = [Math]::Min($Width, 560) })
$results = [Collections.Generic.List[object]]::new()
foreach ($s in $scenes) {
    $t0 = ((ToUtc $s.startUtc) - $videoStart).TotalSeconds - $Lead
    $len = ((ToUtc $s.endUtc) - (ToUtc $s.startUtc)).TotalSeconds + $Lead + $Tail
    if ($t0 -lt 0) { throw "scene $($s.name) starts $(N (-$t0)) s before the recording does" }
    if ($t0 + $len -gt $duration + 0.05) { throw "scene $($s.name) ends $(N ($t0 + $len - $duration)) s after the recording does" }
    $speed = [Math]::Max(1.0, $len / $MaxSeconds)
    $frame0 = Get-Frame ($t0 + 0.05) $choice.Matrix $choice.Range
    $masks = Mask-Filters $s $frame0
    $chain = [Collections.Generic.List[string]]::new()
    $chain.Add("crop=${cw}:${ch}:${cx}:${cy}")
    $chain.Add("scale=in_color_matrix=$($choice.Matrix):in_range=$($choice.Range):out_range=pc,format=gbrp")
    foreach ($f in $masks.Filters) { $chain.Add($f) }
    if ($correct) { $chain.Add('lut1d=file=hdr.cube:interp=nearest') }
    $chain.Add("setpts=(PTS-STARTPTS)/$(N $speed '0.0000')")
    $out = Join-Path $OutDir "$($s.name).gif"
    $made = $null
    foreach ($a in $attempts) {
        $vf = (@($chain) + @("fps=$($a.Fps)", "scale=$($a.Width):-2:flags=lanczos",
                           "split[a][b];[a]palettegen=max_colors=$($a.Colors):stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle")) -join ','
        FF @('-ss', (N $t0 '0.000'), '-t', (N $len '0.000'), '-i', $videoFile, '-filter_complex', $vf, '-loop', '0', '-y', $out) "$($s.name).gif $($a.Colors) colours $($a.Fps) fps $($a.Width) px"
        $kb = (Get-Item -LiteralPath $out).Length / 1024
        $made = [pscustomobject]@{ Colors = $a.Colors; Fps = $a.Fps; Width = $a.Width; KB = [Math]::Round($kb, 1) }
        if ($kb -le $MaxKB) { break }
    }
    $info = (& $ffprobe -v error -count_frames -select_streams v:0 -show_entries 'stream=width,height,nb_read_frames:format=duration' -of json $out) -join "`n" | ConvertFrom-Json
    $results.Add([pscustomobject][ordered]@{
        name = $s.name; file = $out; kb = $made.KB; underMaxKB = ($made.KB -le $MaxKB)
        width = [int]$info.streams[0].width; height = [int]$info.streams[0].height; frames = [int]$info.streams[0].nb_read_frames
        seconds = [Math]::Round([double]::Parse([string]$info.format.duration, $inv), 2); fps = $made.Fps; colors = $made.Colors
        speed = [Math]::Round($speed, 3); sourceOffset = [Math]::Round($t0, 3); sourceSeconds = [Math]::Round($len, 3)
        masks = @($masks.Notes)
    })
}

# ------------------------------------------------------------------ contact sheet
$sheets = [Collections.Generic.List[string]]::new()
foreach ($r in $results) {
    $every = [Math]::Max(1, [int][Math]::Floor($r.frames / 8))
    $sheet = "sheet-$($r.name).png"
    FF @('-i', $r.file, '-vf', "select='not(mod(n\,$every))',scale=480:-2:flags=lanczos,tile=4x2:margin=6:padding=6:color=0x181818", '-frames:v', '1', '-y', $sheet) "contact sheet $($r.name)"
    $sheets.Add($sheet)
}
$contact = Join-Path $OutDir 'contact-sheet.png'
$font = 'C\:/Windows/Fonts/segoeui.ttf'
$labelled = for ($i = 0; $i -lt $sheets.Count; $i++) {
    "[$i]pad=iw:ih+48:0:48:color=0x181818,drawtext=fontfile='$font':text='$($results[$i].name) ($($results[$i].kb) KB, $($results[$i].seconds) s, $($results[$i].fps) fps)':x=12:y=12:fontsize=26:fontcolor=white[s$i]"
}
$fc = ($labelled -join ';') + ';' + ((0..($sheets.Count - 1) | ForEach-Object { "[s$_]" }) -join '') + "vstack=inputs=$($sheets.Count)"
$inputs = @($sheets | ForEach-Object { '-i', $_ })
try { FF (@($inputs) + @('-filter_complex', $fc, '-frames:v', '1', '-y', $contact)) 'contact sheet' }
catch {
    # Without labels if drawtext is not available.
    $fc = ((0..($sheets.Count - 1) | ForEach-Object { "[$_]" }) -join '') + "vstack=inputs=$($sheets.Count)"
    FF (@($inputs) + @('-filter_complex', $fc, '-frames:v', '1', '-y', $contact)) 'contact sheet (no labels)'
}

$summary = [ordered]@{
    createdUtc = [DateTime]::UtcNow.ToString('o', $inv)
    video = $videoFile; videoStartUtc = $videoStart.ToString('o', $inv); videoTags = "$tagMatrix/$tagRange"
    demo = $Demo; crop = "${cw}x$ch at $cx,$cy"
    colour = [ordered]@{ matrix = $choice.Matrix; range = $choice.Range; gain = [Math]::Round($choice.Gain, 3); corrected = $correct
                         meanError = $(if ([double]::IsNaN($choice.Error)) { $null } else { [Math]::Round($choice.Error, 2) }); samples = $choice.Samples
                         referenceAt = $(if ($null -ne $refAt) { [Math]::Round($refAt, 3) } else { $null })
                         tried = @($choice.Tried | ForEach-Object { "$($_.Matrix)/$($_.Range): gain $(N $_.Gain '0.000') error $(N $_.Error '0.00')" }) }
    gifs = @($results); contactSheet = $contact
}
[IO.File]::WriteAllText((Join-Path $OutDir 'gifs.json'), ($summary | ConvertTo-Json -Depth 6))
if (-not $KeepWork) { Remove-Item -LiteralPath $work -Recurse -Force }
foreach ($r in $results) { "$($r.name).gif: $($r.kb) KB, $($r.width)x$($r.height), $($r.frames) frames, $($r.seconds) s at $($r.fps) fps, $($r.colors) colours, speed x$($r.speed)$(if (-not $r.underMaxKB) { " (over $MaxKB KB)" })" }
"colour: $($choice.Matrix)/$($choice.Range), gain $(N $choice.Gain '0.000') ($(if ($correct) { 'undone' } else { 'no correction' })), mean error $(N $choice.Error '0.00') over $($choice.Samples) points"
"contact sheet: $contact"
exit ([int](@($results | Where-Object { -not $_.underMaxKB }).Count -gt 0))
