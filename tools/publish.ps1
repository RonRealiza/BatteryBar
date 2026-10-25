# Собирает Release и обновляет установленную копию в %LOCALAPPDATA%\Programs\BatteryBar,
# перезапуская приложение, если оно было запущено. Автозапуск указывает на эту папку.
# Запуск: powershell -ExecutionPolicy Bypass -File tools\publish.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\BatteryBar'
$staging = Join-Path $env:TEMP 'bbpublish'

dotnet publish (Join-Path $root 'BatteryBar.csproj') -c Release -r win-x64 --self-contained false -o $staging -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }

$wasRunning = [bool](Get-Process BatteryBar -ErrorAction SilentlyContinue)
Get-Process BatteryBar -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 800

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $staging '*') $target -Force

if ($wasRunning) { Start-Process (Join-Path $target 'BatteryBar.exe') }
Write-Output "Установлено в $target$(if ($wasRunning) { ' и перезапущено' })"
