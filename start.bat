@echo off
rem One-click start: prefer the published Release exe (instant), else build & run dev.
cd /d "%~dp0"
if exist "Release\HopingImageViewer.exe" (
  echo Starting Hoping Image Viewer (Release)...
  start "" "Release\HopingImageViewer.exe"
  exit /b 0
)
echo Release build not found. Building & launching dev build (first run compiles)...
dotnet run --project src\ImageViewer.App -c Release
echo App closed.
pause
