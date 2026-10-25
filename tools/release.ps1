# Собирает файлы релиза в папку dist:
#   BatteryBar-<версия>-Setup.exe     — установщик (Inno Setup)
#   BatteryBar-<версия>-Portable.zip  — портативная версия: один exe + метка portable (данные хранятся рядом)
# Обеим версиям нужен .NET 9 Desktop Runtime. Версия берётся из <Version> в BatteryBar.csproj.
# Запуск: powershell -ExecutionPolicy Bypass -File tools\release.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'BatteryBar.csproj'
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Не найдена <Version> в BatteryBar.csproj' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Не найден Inno Setup 6 (winget install JRSoftware.InnoSetup)' }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null

# Файлы для установщика: обычная сборка без отладочных символов.
dotnet publish $project -c Release -r win-x64 --self-contained false -p:DebugType=none -o (Join-Path $dist 'app') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Сборка для установщика не удалась' }

# Портативная версия: всё в одном exe.
$portableBuild = Join-Path $dist 'portable-build'
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $portableBuild -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Портативная сборка не удалась' }

$portable = Join-Path $dist 'BatteryBar'
New-Item -ItemType Directory $portable | Out-Null
Copy-Item (Join-Path $portableBuild 'BatteryBar.exe') $portable
Copy-Item (Join-Path $root 'LICENSE') $portable
# Метка: с ней настройки и журнал хранятся в папке data рядом с программой.
Set-Content (Join-Path $portable 'portable') 'BatteryBar portable mode: settings and log are stored in the data folder next to the program.'
Compress-Archive -Path $portable -DestinationPath (Join-Path $dist "BatteryBar-$version-Portable.zip")

& $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\BatteryBar.iss')
if ($LASTEXITCODE -ne 0) { throw 'Сборка установщика не удалась' }

Remove-Item $portableBuild, $portable -Recurse -Force
Get-ChildItem $dist -File | ForEach-Object { '{0}  {1:N1} МБ' -f $_.Name, ($_.Length / 1MB) }
