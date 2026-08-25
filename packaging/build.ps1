# Hoping Image Viewer 一键打包脚本
# 产物：zip 便携版 + Inno Setup 安装程序（含绿色安装/卸载，卸载可选保留数据）
# 输出目录：原工作区 Release（不放在 GitHub 代码仓库内），可自行修改 $ReleaseDir
# 用法（项目根目录）：
#   powershell -ExecutionPolicy Bypass -File packaging\build.ps1
$ErrorActionPreference = 'Stop'
$Version = '1.1.0'
# 发布产物按版本归类到子目录（Release\<版本>\）
$ReleaseDir = "H:\WorkSpace\CherryStudio\DeepSeek\ImageViewer_c\Release\$Version"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "[1/3] 自包含发布..." -ForegroundColor Cyan
dotnet publish src\ImageViewer.App -c Release -r win-x64 --self-contained true -o packaging\staging\app
if ($LASTEXITCODE -ne 0) { throw 'publish 失败' }

Write-Host "[2/3] 打包 zip 便携版..." -ForegroundColor Cyan
Remove-Item packaging\ziproot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path packaging\ziproot\HopingImageViewer -Force | Out-Null
Copy-Item packaging\staging\app\* packaging\ziproot\HopingImageViewer\ -Recurse -Force
Get-ChildItem packaging\ziproot -Recurse -Filter *.pdb | Remove-Item -Force
@"
Hoping Image Viewer 便携版（绿色免安装）

解压后双击 HopingImageViewer.exe 即可使用，无需安装 .NET。
程序数据（相册链接/标签/AI 配置）保存在本文件夹内 data/ 下，
可把整个文件夹拷贝到任意位置（U 盘/其他电脑）直接运行。
"@ | Out-File packaging\ziproot\HopingImageViewer\使用说明.txt -Encoding UTF8
New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
if (-not (Test-Path $sevenZip)) { throw "未找到 7-Zip: $sevenZip" }
# 7z 的 a 命令是追加模式，打包前先删旧 zip，避免条目累积翻倍
Remove-Item "$ReleaseDir\HopingImageViewer-portable-$Version-win-x64.zip" -Force -ErrorAction SilentlyContinue
# 进入 ziproot 再打包，zip 内根目录就是 HopingImageViewer（不带 packaging\ziproot 前缀）
Push-Location packaging\ziproot
& $sevenZip a -tzip -mx=9 -bso0 -bsp0 "$ReleaseDir\HopingImageViewer-portable-$Version-win-x64.zip" HopingImageViewer
Pop-Location
Remove-Item packaging\ziproot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "[3/3] 编译 Inno Setup 安装程序..." -ForegroundColor Cyan
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { throw "未找到 ISCC.exe: $iscc" }
& $iscc /Q "/O$ReleaseDir" packaging\HopingImageViewer.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 编译失败' }

Write-Host "`n打包完成！产物在：$ReleaseDir" -ForegroundColor Green
Get-ChildItem $ReleaseDir -File | ForEach-Object { Write-Host "  $($_.Name)  ($([math]::Round($_.Length/1MB,1)) MB)" }
