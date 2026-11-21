# 프로그램 아이콘(icon.ico) 만들기: 노즐 + 쌓이는 출력 층. 한 번 실행하면 icon.ico 가 생기고 build.ps1 이 그걸 쓴다.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
Add-Type -AssemblyName System.Drawing

function Draw([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $k = $s / 256.0
    # 바탕: 둥근 사각형
    $r = 48 * $k; $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $p.AddArc($s-2*$r-1, 0, 2*$r, 2*$r, 270, 90)
    $p.AddArc($s-2*$r-1, $s-2*$r-1, 2*$r, 2*$r, 0, 90); $p.AddArc(0, $s-2*$r-1, 2*$r, 2*$r, 90, 90); $p.CloseFigure()
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0,0), (New-Object Drawing.Point 0,$s), ([Drawing.Color]::FromArgb(40,70,120)), ([Drawing.Color]::FromArgb(18,30,56))
    $g.FillPath($bg, $p)
    # 출력 층 3줄 (아래가 넓음)
    $orange = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,140,40))
    $light = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,190,110))
    $g.FillRectangle($orange, 52*$k, 196*$k, 152*$k, 26*$k)
    $g.FillRectangle($light, 70*$k, 166*$k, 116*$k, 26*$k)
    $g.FillRectangle($orange, 88*$k, 136*$k, 80*$k, 26*$k)
    # 노즐: 히터 블록 + 뾰족한 끝
    $steel = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(225,230,238))
    $g.FillRectangle($steel, 98*$k, 34*$k, 60*$k, 50*$k)
    $tip = [Drawing.PointF[]]@((New-Object Drawing.PointF (104*$k),(84*$k)), (New-Object Drawing.PointF (152*$k),(84*$k)), (New-Object Drawing.PointF (128*$k),(118*$k)))
    $g.FillPolygon($steel, $tip)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

# ICO = 머리 6바이트 + 크기마다 16바이트 목록 + PNG 데이터
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = $sizes | ForEach-Object { ,(Draw $_) }
$out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = $sizes[$i] % 256  # 256 은 0 으로 적는다
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$off)
    $off += $pngs[$i].Length
}
foreach ($b in $pngs) { $w.Write($b) }
[IO.File]::WriteAllBytes("$PSScriptRoot\icon.ico", $out.ToArray())
# 눈으로 확인용 256px 그림
[IO.File]::WriteAllBytes("$PSScriptRoot\icon_preview.png", $pngs[-1])
"icon.ico 만듦"
