# 生成示例图片（渐变背景 + 编号文字），用于演示相册/图片查看功能。
# 依赖：Windows 自带 System.Drawing（.NET Framework），无需额外安装。
# 用法：右键「使用 PowerShell 运行」或  powershell -ExecutionPolicy Bypass -File tools/make_samples.ps1

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot   # 仓库根 = tools/ 的上级

function New-SampleImage {
    param(
        [string]$Path,
        [int]$W,
        [int]$H,
        [System.Drawing.Color]$Color1,
        [System.Drawing.Color]$Color2,
        [string]$Label
    )

    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    # 45° 渐变背景
    $rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $Color1, $Color2, 45)
    $g.FillRectangle($brush, $rect)

    # 两个半透明圆装饰，增加视觉区分
    $r = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r.AddEllipse($W * 0.7, -$H * 0.15, $W * 0.6, $W * 0.6)
    $cBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 255, 255, 255))
    $g.FillPath($cBrush, $r)
    $r2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r2.AddEllipse(-$W * 0.25, $H * 0.6, $W * 0.55, $W * 0.55)
    $g.FillPath($cBrush, $r2)

    # 居中编号文字（黑色偏移阴影 + 白色本体）
    $font = New-Object System.Drawing.Font('Microsoft YaHei', [Math]::Max(36, [int]($H * 0.05)), [System.Drawing.FontStyle]::Bold)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center

    $shadowRect = New-Object System.Drawing.RectangleF(4, 6, $W, $H)
    $shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(120, 0, 0, 0))
    $g.DrawString($Label, $font, $shadowBrush, $shadowRect, $sf)
    $whiteBrush = [System.Drawing.Brushes]::White
    $rectF = New-Object System.Drawing.RectangleF(0, 0, $W, $H)
    $g.DrawString($Label, $font, $whiteBrush, $rectF, $sf)

    # 保存 JPEG（质量 92）
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
    $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]92)
    $bmp.Save($Path, $codec, $ep)

    $g.Dispose(); $brush.Dispose(); $cBrush.Dispose(); $bmp.Dispose()
}

function New-Series {
    param(
        [string]$Album,          # 相册目录名
        [int]$Count,             # 张数
        [int]$W, [int]$H,        # 图片尺寸
        [System.Drawing.Color[]]$Palette  # 配色对数组（每对2色，超出循环）
    )
    $dir = Join-Path $root "pictures\$Album"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    for ($i = 0; $i -lt $Count; $i++) {
        $c1 = $Palette[($i * 2) % $Palette.Count]
        $c2 = $Palette[($i * 2 + 1) % $Palette.Count]
        $label = "{0} {1:D3}" -f ($Album -replace '示例相册', ''), ($i + 1)
        $file = Join-Path $dir ("{0:D3}.jpg" -f ($i + 1))
        New-SampleImage -Path $file -W $W -H $H -Color1 $c1 -Color2 $c2 -Label $label
    }
    Write-Host "已生成 $Count 张 -> pictures/$Album"
}

# 示例相册A：暖色横图 1600x900 × 5
$palA = @(
    [System.Drawing.Color]::FromArgb(255, 180, 80), [System.Drawing.Color]::FromArgb(255, 90, 90),
    [System.Drawing.Color]::FromArgb(90, 200, 120), [System.Drawing.Color]::FromArgb(40, 120, 80),
    [System.Drawing.Color]::FromArgb(80, 160, 255), [System.Drawing.Color]::FromArgb(50, 60, 200),
    [System.Drawing.Color]::FromArgb(200, 120, 255), [System.Drawing.Color]::FromArgb(120, 50, 180),
    [System.Drawing.Color]::FromArgb(255, 120, 160), [System.Drawing.Color]::FromArgb(200, 50, 60)
)
New-Series -Album "示例相册A" -Count 5 -W 1600 -H 900 -Palette $palA

# 示例相册B：冷色竖图 1000x1400 × 4
$palB = @(
    [System.Drawing.Color]::FromArgb(40, 60, 140), [System.Drawing.Color]::FromArgb(10, 20, 60),
    [System.Drawing.Color]::FromArgb(30, 120, 120), [System.Drawing.Color]::FromArgb(5, 50, 60),
    [System.Drawing.Color]::FromArgb(80, 60, 160), [System.Drawing.Color]::FromArgb(30, 10, 80),
    [System.Drawing.Color]::FromArgb(60, 100, 180), [System.Drawing.Color]::FromArgb(15, 40, 90)
)
New-Series -Album "示例相册B" -Count 4 -W 1000 -H 1400 -Palette $palB

Write-Host "完成。可删除 pictures/ 下示例目录，放入自己的图片。"
