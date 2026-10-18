# Сборка NEXA LAND в ОДИН защищённый файл: dist\NexaLand.exe
#
# Что делает скрипт:
#   1. dotnet build -c Release
#   2. Обфускация через Obfuscar 2.2.50 (шифрование строк + переименование
#      приватных/внутренних членов — безопасно для WPF/XAML)
#   3. dotnet publish --no-build -p:PublishSingleFile=true  -> dist\NexaLand.exe
#
# Требуется: .NET SDK 10 и папка tools\ (обфускатор лежит там же, nuget не нужен).
#
# Запуск:
#   powershell -ExecutionPolicy Bypass -File .\publish-protected.ps1

param([switch]$Fast)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$project = Join-Path $root "src\MinecraftLauncher\MinecraftLauncher.csproj"
$bin     = Join-Path $root "src\MinecraftLauncher\bin\Release\net10.0-windows\win-x64"
$dll     = Join-Path $bin "NexaLand.dll"
# Выходная папка вне OneDrive — синхронизация блокирует удаление файлов там
$dist    = "C:\Users\Acer\AppData\Local\Temp\opencode\dist"
$distCopy = Join-Path $root "dist"
$tools   = Join-Path $root "tools"
$obf     = Join-Path $tools "obfuscar\Obfuscar.Console.exe"
$config  = Join-Path $tools "obfuscar-const.cfx.xml"
# Временная папка вне OneDrive — иначе синхронизация блокирует запись Mapping.txt
$outDir  = "C:\Users\Acer\AppData\Local\Temp\opencode\obf-work"

Get-Process NexaLand -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

# 1. Сборка
Write-Host "[1/3] Сборка..." -ForegroundColor Cyan
& dotnet build $project -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }

# 2. Обфускация
Write-Host "[2/3] Обфускация (Obfuscar)..." -ForegroundColor Cyan
if (-not (Test-Path $obf)) { throw "Нет обфускатора: $obf" }
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
# правим путь к dll в конфиге (на случай переноса папки проекта)
$configText = Get-Content $config -Raw
$configText = $configText -replace '<Module file="[^"]*" />', "<Module file=`"$dll`" />"
$configText = $configText -replace '<Var name="OutPath" value="[^"]*" />', "<Var name=`"OutPath`" value=`"$outDir`" />"
Set-Content -Path $config -Value $configText -Encoding UTF8

& $obf -v:q $config
if ($LASTEXITCODE -ne 0) { throw "Обфускация не удалась" }
if (-not (Test-Path (Join-Path $outDir "NexaLand.dll"))) { throw "Обфусцированный dll не создан" }

# подменяем dll на обфусцированный (оригинал запоминаем, чтобы вернуть после публикации)
$backup = "$dll.orig"
Copy-Item $dll $backup -Force
Copy-Item (Join-Path $outDir "NexaLand.dll") $dll -Force
"  исходный: 462336 байт, обфусц.: " + (Get-Item $dll).Length + " байт"

# 3. Публикация в один файл
Write-Host "[3/3] Упаковка в один файл..." -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
& dotnet publish $project -c Release -o $dist --no-build -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Публикация не удалась" }

$exe = Join-Path $dist "NexaLand.exe"

# возвращаем оригинальный dll, чтобы исходник проекта остался чистым
if (Test-Path $backup) {
    Copy-Item $backup $dll -Force
    Remove-Item $backup -Force
}

# копируем в папку проекта (best effort — OneDrive иногда блокирует)
Copy-Item $exe (Join-Path $distCopy "NexaLand.exe") -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Готово: $exe" -ForegroundColor Green
Write-Host "Размер: $([math]::Round((Get-Item $exe).Length/1MB,1)) МБ · файлов рядом: 1"
Write-Host "Файл можно скопировать куда угодно и запустить — .NET устанавливать не нужно." -ForegroundColor DarkGray