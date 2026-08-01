Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"
$OutDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function S([string]$hex) {
    -join (($hex -split ' ') | Where-Object { $_ } | ForEach-Object { [char][Convert]::ToInt32($_, 16) })
}

$Text = @{
    AppTitle = S "97F3 9891 5DE5 5177 7BB1"
    Subtitle = S "4E00 7AD9 5F0F 97F3 9891 5904 7406 5DE5 5177"
    Convert = S "97F3 9891 683C 5F0F 8F6C 6362"
    Cut = S "97F3 9891 5207 5272"
    Merge = S "97F3 9891 5408 5E76"
    Compress = S "97F3 9891 538B 7F29"
    Select = S "9009 62E9 97F3 9891 6587 4EF6"
    Recent = S "6700 8FD1 4EFB 52A1"
    Ready = S "51C6 5907 5C31 7EEA"
    Success = S "8F6C 6362 6210 529F"
    SuccessBody = (S "97F3 9891 5DF2 6210 529F 8F6C 6362 4E3A") + " MP3"
    OpenFolder = S "6253 5F00 6587 4EF6 5939"
    Close = S "5173 95ED"
    Failure = S "8F6C 6362 5931 8D25"
    FailureBody = S "5904 7406 97F3 9891 65F6 51FA 73B0 95EE 9898"
    Retry = S "91CD 8BD5"
    Support = (S "652F 6301") + " MP3 / FLAC / WAV / OGG / NCM"
}

function ColorFromHex([string]$hex, [int]$alpha = 255) {
    $value = $hex.TrimStart("#")
    [System.Drawing.Color]::FromArgb(
        $alpha,
        [Convert]::ToInt32($value.Substring(0, 2), 16),
        [Convert]::ToInt32($value.Substring(2, 2), 16),
        [Convert]::ToInt32($value.Substring(4, 2), 16)
    )
}

function FontOf([float]$size, [System.Drawing.FontStyle]$style = [System.Drawing.FontStyle]::Regular) {
    New-Object System.Drawing.Font -ArgumentList "Microsoft YaHei UI", $size, $style, ([System.Drawing.GraphicsUnit]::Pixel)
}

function RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function FillRound($g, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r, $color) {
    $path = RoundedPath $x $y $w $h $r
    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()
}

function StrokeRound($g, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r, $color, [float]$width = 1) {
    $path = RoundedPath $x $y $w $h $r
    $pen = New-Object System.Drawing.Pen($color, $width)
    $g.DrawPath($pen, $path)
    $pen.Dispose()
    $path.Dispose()
}

function ShadowRound($g, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r, [int]$depth = 7) {
    for ($i = $depth; $i -ge 1; $i--) {
        $alpha = [Math]::Max(5, [int](18 / $i))
        FillRound $g ($x + $i * 0.7) ($y + $i * 1.1) $w $h $r (ColorFromHex "#233044" $alpha)
    }
}

function DrawText($g, [string]$text, [float]$x, [float]$y, [float]$w, [float]$h, $font, $color, [string]$align = "Near", [string]$valign = "Near") {
    $brush = New-Object System.Drawing.SolidBrush($color)
    $format = New-Object System.Drawing.StringFormat
    $format.Trimming = [System.Drawing.StringTrimming]::EllipsisCharacter
    $format.FormatFlags = [System.Drawing.StringFormatFlags]::NoWrap
    $format.Alignment = [System.Drawing.StringAlignment]::$align
    $format.LineAlignment = [System.Drawing.StringAlignment]::$valign
    $rect = New-Object System.Drawing.RectangleF -ArgumentList $x, $y, $w, $h
    $g.DrawString($text, $font, $brush, $rect, $format)
    $format.Dispose()
    $brush.Dispose()
}

function NewCanvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    @{ Bitmap = $bmp; Graphics = $g }
}

function SaveCanvas($canvas, [string]$name) {
    $path = Join-Path $OutDir $name
    $canvas.Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Graphics.Dispose()
    $canvas.Bitmap.Dispose()
    $path
}

function DrawLogoMark($g, [float]$cx, [float]$cy, [float]$scale) {
    $blue = ColorFromHex "#2563EB"
    $teal = ColorFromHex "#14B8A6"
    $white = ColorFromHex "#FFFFFF"
    $penBlue = New-Object System.Drawing.Pen($blue, (7 * $scale))
    $penBlue.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penBlue.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penTeal = New-Object System.Drawing.Pen($teal, (6 * $scale))
    $penTeal.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penTeal.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $wave = New-Object System.Drawing.Drawing2D.GraphicsPath
    $wave.AddBezier($cx - 46 * $scale, $cy + 2 * $scale, $cx - 26 * $scale, $cy - 46 * $scale, $cx - 7 * $scale, $cy + 46 * $scale, $cx + 13 * $scale, $cy - 2 * $scale)
    $wave.AddBezier($cx + 13 * $scale, $cy - 2 * $scale, $cx + 28 * $scale, $cy - 34 * $scale, $cx + 42 * $scale, $cy + 30 * $scale, $cx + 58 * $scale, $cy - 14 * $scale)
    $g.DrawPath($penBlue, $wave)

    $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $arrow.AddArc($cx - 62 * $scale, $cy - 62 * $scale, 124 * $scale, 124 * $scale, 208, 116)
    $g.DrawPath($penTeal, $arrow)
    $points = @(
        (New-Object System.Drawing.PointF -ArgumentList ($cx + 42 * $scale), ($cy - 54 * $scale)),
        (New-Object System.Drawing.PointF -ArgumentList ($cx + 68 * $scale), ($cy - 42 * $scale)),
        (New-Object System.Drawing.PointF -ArgumentList ($cx + 45 * $scale), ($cy - 24 * $scale))
    )
    $brushTeal = New-Object System.Drawing.SolidBrush($teal)
    $g.FillPolygon($brushTeal, $points)

    $shine = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, $white))
    $g.FillEllipse($shine, $cx - 44 * $scale, $cy - 45 * $scale, 18 * $scale, 18 * $scale)
    $shine.Dispose()
    $brushTeal.Dispose()
    $wave.Dispose()
    $arrow.Dispose()
    $penBlue.Dispose()
    $penTeal.Dispose()
}

function DrawCircleIcon($g, [float]$x, [float]$y, [string]$kind, $bg, $fg) {
    $brush = New-Object System.Drawing.SolidBrush($bg)
    $g.FillEllipse($brush, $x, $y, 44, 44)
    $brush.Dispose()
    $pen = New-Object System.Drawing.Pen($fg, 3)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    if ($kind -eq "convert") {
        $g.DrawArc($pen, $x + 10, $y + 10, 24, 24, 210, 230)
        $g.DrawLine($pen, $x + 31, $y + 10, $x + 36, $y + 16)
        $g.DrawLine($pen, $x + 31, $y + 10, $x + 25, $y + 14)
        $g.DrawArc($pen, $x + 10, $y + 10, 24, 24, 30, 230)
        $g.DrawLine($pen, $x + 13, $y + 34, $x + 8, $y + 28)
        $g.DrawLine($pen, $x + 13, $y + 34, $x + 19, $y + 30)
    } elseif ($kind -eq "cut") {
        $g.DrawLine($pen, $x + 14, $y + 14, $x + 31, $y + 31)
        $g.DrawLine($pen, $x + 31, $y + 14, $x + 14, $y + 31)
        $g.DrawEllipse($pen, $x + 9, $y + 27, 9, 9)
        $g.DrawEllipse($pen, $x + 26, $y + 27, 9, 9)
    } elseif ($kind -eq "merge") {
        $g.DrawBezier($pen, $x + 10, $y + 15, $x + 20, $y + 15, $x + 20, $y + 29, $x + 34, $y + 29)
        $g.DrawBezier($pen, $x + 10, $y + 31, $x + 20, $y + 31, $x + 20, $y + 15, $x + 34, $y + 15)
        $g.DrawLine($pen, $x + 31, $y + 11, $x + 37, $y + 15)
        $g.DrawLine($pen, $x + 31, $y + 19, $x + 37, $y + 15)
        $g.DrawLine($pen, $x + 31, $y + 25, $x + 37, $y + 29)
        $g.DrawLine($pen, $x + 31, $y + 33, $x + 37, $y + 29)
    } else {
        $g.DrawLine($pen, $x + 13, $y + 14, $x + 13, $y + 30)
        $g.DrawLine($pen, $x + 22, $y + 10, $x + 22, $y + 34)
        $g.DrawLine($pen, $x + 31, $y + 17, $x + 31, $y + 27)
        $g.DrawLine($pen, $x + 10, $y + 35, $x + 34, $y + 35)
    }
    $pen.Dispose()
}

function DrawButton($g, [float]$x, [float]$y, [float]$w, [float]$h, [string]$label, $fill, $textColor) {
    FillRound $g $x $y $w $h 9 $fill
    $font = FontOf 13 ([System.Drawing.FontStyle]::Bold)
    DrawText $g $label $x ($y + 1) $w $h $font $textColor "Center" "Center"
    $font.Dispose()
}

function DrawFeatureCard($g, [float]$x, [float]$y, [float]$w, [float]$h, [string]$title, [string]$kind, [bool]$active) {
    $border = if ($active) { ColorFromHex "#93C5FD" } else { ColorFromHex "#DDE7F0" }
    $bg = if ($active) { ColorFromHex "#EEF6FF" } else { ColorFromHex "#FFFFFF" }
    ShadowRound $g $x $y $w $h 16 5
    FillRound $g $x $y $w $h 16 $bg
    StrokeRound $g $x $y $w $h 16 $border $(if ($active) { 2 } else { 1 })
    DrawCircleIcon $g ($x + 22) ($y + 22) $kind $(if ($active) { ColorFromHex "#DBEAFE" } else { ColorFromHex "#F1F5F9" }) $(if ($active) { ColorFromHex "#2563EB" } else { ColorFromHex "#64748B" })

    $titleFont = FontOf 18 ([System.Drawing.FontStyle]::Bold)
    $mutedFont = FontOf 12
    DrawText $g $title ($x + 82) ($y + 22) ($w - 104) 26 $titleFont (ColorFromHex "#172033")
    $caption = if ($active) { $Text.Support } else { $Text.Ready }
    DrawText $g $caption ($x + 82) ($y + 53) ($w - 104) 22 $mutedFont (ColorFromHex "#657083")
    if ($active) {
        DrawButton $g ($x + 82) ($y + 88) 128 34 $Text.Select (ColorFromHex "#2563EB") (ColorFromHex "#FFFFFF")
    } else {
        FillRound $g ($x + 82) ($y + 90) 82 28 8 (ColorFromHex "#F3F7FA")
        DrawText $g $Text.Ready ($x + 82) ($y + 90) 82 28 $mutedFont (ColorFromHex "#657083") "Center" "Center"
    }
    $titleFont.Dispose()
    $mutedFont.Dispose()
}

function DrawMainPage($g, [bool]$muted = $false) {
    $bg = if ($muted) { ColorFromHex "#EEF2F7" } else { ColorFromHex "#F3F7FA" }
    $g.Clear($bg)
    ShadowRound $g 20 18 760 464 22 7
    FillRound $g 20 18 760 464 22 (ColorFromHex "#FFFFFF")
    StrokeRound $g 20 18 760 464 22 (ColorFromHex "#DDE7F0") 1

    FillRound $g 45 42 42 42 12 (ColorFromHex "#E8F4FF")
    DrawLogoMark $g 66 63 0.22

    $titleFont = FontOf 25 ([System.Drawing.FontStyle]::Bold)
    $subFont = FontOf 13
    DrawText $g $Text.AppTitle 100 39 260 34 $titleFont (ColorFromHex "#172033")
    DrawText $g $Text.Subtitle 101 73 260 22 $subFont (ColorFromHex "#657083")
    FillRound $g 652 46 88 32 16 (ColorFromHex "#ECFDF5")
    DrawText $g $Text.Ready 652 47 88 32 $subFont (ColorFromHex "#0F766E") "Center" "Center"

    DrawFeatureCard $g 48 122 334 128 $Text.Convert "convert" $true
    DrawFeatureCard $g 418 122 334 128 $Text.Cut "cut" $false
    DrawFeatureCard $g 48 280 334 128 $Text.Merge "merge" $false
    DrawFeatureCard $g 418 280 334 128 $Text.Compress "compress" $false

    FillRound $g 48 433 704 30 10 (ColorFromHex "#F8FBFD")
    StrokeRound $g 48 433 704 30 10 (ColorFromHex "#DDE7F0") 1
    DrawText $g $Text.Recent 64 438 130 22 $subFont (ColorFromHex "#657083")
    DrawText $g $Text.Ready 650 438 82 22 $subFont (ColorFromHex "#14B8A6") "Far" "Near"

    $titleFont.Dispose()
    $subFont.Dispose()
}

function DrawDialog($g, [float]$x, [float]$y, [string]$state) {
    $isSuccess = $state -eq "success"
    $accent = if ($isSuccess) { ColorFromHex "#14B8A6" } else { ColorFromHex "#EF4444" }
    $soft = if ($isSuccess) { ColorFromHex "#DDFBF5" } else { ColorFromHex "#FEE2E2" }
    $title = if ($isSuccess) { $Text.Success } else { $Text.Failure }
    $body = if ($isSuccess) { $Text.SuccessBody } else { $Text.FailureBody }
    $primary = if ($isSuccess) { $Text.OpenFolder } else { $Text.Retry }
    $w = 420
    $h = 260
    ShadowRound $g $x $y $w $h 18 9
    FillRound $g $x $y $w $h 18 (ColorFromHex "#FFFFFF")
    StrokeRound $g $x $y $w $h 18 (ColorFromHex "#DDE7F0") 1

    $iconX = $x + 32
    $iconY = $y + 34
    $brushSoft = New-Object System.Drawing.SolidBrush($soft)
    $g.FillEllipse($brushSoft, $iconX, $iconY, 56, 56)
    $brushSoft.Dispose()
    $pen = New-Object System.Drawing.Pen($accent, 4)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    if ($isSuccess) {
        $g.DrawLine($pen, $iconX + 16, $iconY + 29, $iconX + 25, $iconY + 38)
        $g.DrawLine($pen, $iconX + 25, $iconY + 38, $iconX + 42, $iconY + 19)
    } else {
        $g.DrawLine($pen, $iconX + 19, $iconY + 19, $iconX + 37, $iconY + 37)
        $g.DrawLine($pen, $iconX + 37, $iconY + 19, $iconX + 19, $iconY + 37)
    }
    $pen.Dispose()

    $titleFont = FontOf 23 ([System.Drawing.FontStyle]::Bold)
    $bodyFont = FontOf 14
    DrawText $g $title ($x + 104) ($y + 38) 250 32 $titleFont (ColorFromHex "#172033")
    DrawText $g $body ($x + 104) ($y + 75) 260 26 $bodyFont (ColorFromHex "#657083")

    FillRound $g ($x + 32) ($y + 122) ($w - 64) 1 1 (ColorFromHex "#E8EEF5")
    DrawButton $g ($x + 152) ($y + 188) 128 40 $primary (ColorFromHex "#2563EB") (ColorFromHex "#FFFFFF")
    FillRound $g ($x + 292) ($y + 188) 86 40 10 (ColorFromHex "#F3F7FA")
    StrokeRound $g ($x + 292) ($y + 188) 86 40 10 (ColorFromHex "#DDE7F0") 1
    DrawText $g $Text.Close ($x + 292) ($y + 189) 86 40 $bodyFont (ColorFromHex "#657083") "Center" "Center"
    $titleFont.Dispose()
    $bodyFont.Dispose()
}

function RenderMainPage {
    $canvas = NewCanvas 800 500
    DrawMainPage $canvas.Graphics
    SaveCanvas $canvas "main-page-800x500.png"
}

function RenderLogo {
    $canvas = NewCanvas 1024 1024
    $g = $canvas.Graphics
    $g.Clear((ColorFromHex "#F3F7FA"))
    $rect = New-Object System.Drawing.Rectangle -ArgumentList 96, 96, 832, 832
    $path = RoundedPath 96 96 832 832 190
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $rect, (ColorFromHex "#2563EB"), (ColorFromHex "#14B8A6"), 35
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()
    FillRound $g 178 178 668 668 150 ([System.Drawing.Color]::FromArgb(42, 255, 255, 255))
    DrawLogoMark $g 512 512 3.75
    SaveCanvas $canvas "app-logo-1024x1024.png"
}

function RenderStandaloneDialogs {
    $canvas = NewCanvas 900 320
    $g = $canvas.Graphics
    $g.Clear((ColorFromHex "#F3F7FA"))
    DrawDialog $g 25 30 "success"
    DrawDialog $g 455 30 "failure"
    SaveCanvas $canvas "message-dialogs-900x320.png"
}

function RenderDialogContext {
    $canvas = NewCanvas 800 500
    $g = $canvas.Graphics
    DrawMainPage $g $true
    $overlay = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(112, 255, 255, 255))
    $g.FillRectangle($overlay, 20, 18, 760, 464)
    $overlay.Dispose()
    DrawDialog $g 190 120 "success"
    SaveCanvas $canvas "message-dialog-in-main-800x500.png"
}

RenderMainPage
RenderLogo
RenderStandaloneDialogs
RenderDialogContext
