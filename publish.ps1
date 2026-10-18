# Сборка NEXA LAND в ОДИН файл: dist\NexaLand.exe
#
# Запуск:
#   .\publish.ps1            — обычная сборка одного файла
#   .\publish.ps1 -Fast      — без сжатия (126 МБ, совместимо с антивирусами)
#
# Что получается: один NexaLand.exe, внутри .NET + WPF. Папок с dll рядом нет.

param([switch]$Fast)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\MinecraftLauncher\MinecraftLauncher.csproj"
$dist    = Join-Path $PSScriptRoot "dist"

Get-Process NexaLand -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$args = @(
    "publish", $project,
    "-c", "Release",
    "-o", $dist,
    "-v", "q",
    "--nologo"
)

Write-Host "Собираю один исполняемый файл..." -ForegroundColor Cyan
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }

$exe = Join-Path $dist "NexaLand.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Готово: $exe" -ForegroundColor Green
Write-Host "Размер: $size МБ · файлов в папке: $(@(Get-ChildItem $dist).Count)"
Write-Host ""
Write-Host "Этот файл можно скопировать куда угодно и запустить — .NET устанавливать не нужно." -ForegroundColor DarkGray