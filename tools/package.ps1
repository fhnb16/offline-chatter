# Собирает поставку Offline Chatter из Release-сборки:
#   OfflineChatter-<версия>.zip и .rar — папка OfflineChatter\ с exe и готовой структурой (models\, engine\, chats\ с подсказками);
#   папка stage\OfflineChatter — её же берёт установщик (installer\OfflineChatter.iss).
# Пример:
#   pwsh tools/package.ps1 -BinDir src/OfflineChatter/bin/Release/net48 -Version 0.1.42 -Out out
# RAR собирается, только если найден Rar.exe (WinRAR); без него — предупреждение и только zip.
param(
    [Parameter(Mandatory)] [string]$BinDir,
    [Parameter(Mandatory)] [string]$Version,
    [string]$Out = 'out'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Out = [IO.Path]::GetFullPath($Out)
$stageRoot = Join-Path $Out 'stage'
$app = Join-Path $stageRoot 'OfflineChatter'
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

foreach ($f in 'OfflineChatter.exe', 'OfflineChatter.exe.config') {
    Copy-Item (Join-Path $BinDir $f) $app
}

function Write-Text([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.UTF8Encoding]::new($true))
}

Write-Text (Join-Path $app 'README.txt') @"
Offline Chatter $Version
https://github.com/fhnb16/offline-chatter

RU
Запустите OfflineChatter.exe. При первом запуске мастер скачает движок llama.cpp и небольшую модель.
Все данные хранятся рядом с программой:
  models\   модели GGUF (сюда можно положить свои файлы *.gguf — программа найдёт их кнопкой «Найти скачанные»)
  engine\   сборки llama.cpp (скачиваются автоматически; свою сборку можно подключить из любой папки)
  chats\    чаты
  config\   настройки (создаётся при первом запуске)
  logs\     журналы (создаётся при первом запуске)
Если распаковать программу в «Загрузки» или на рабочий стол, она предложит перенести себя
в %LOCALAPPDATA%\Programs\OfflineChatter и добавить ярлык в меню «Пуск».
Нужен .NET Framework 4.8 (уже есть в Windows 10 1903+ и Windows 11).

EN
Run OfflineChatter.exe. On first start the wizard downloads the llama.cpp engine and a small model.
All data lives next to the program:
  models\   GGUF models (drop your own *.gguf here and press "Find downloaded")
  engine\   llama.cpp builds (downloaded automatically; your own build can be linked from any folder)
  chats\    chats
  config\   settings (created on first start)
  logs\     logs (created on first start)
If you unpack it to Downloads or the desktop, it offers to move itself to
%LOCALAPPDATA%\Programs\OfflineChatter and add a Start menu shortcut.
Requires .NET Framework 4.8 (built into Windows 10 1903+ and Windows 11).
"@

Write-Text (Join-Path $app 'models\README.txt') @"
RU: сюда кладутся модели GGUF. Скачанные из программы модели появляются здесь сами.
Свои файлы *.gguf можно положить сюда (в любую подпапку) и нажать «Модели → Найти скачанные».
Для vision-моделей рядом с моделью должен лежать файл mmproj-*.gguf.

EN: GGUF models go here. Models downloaded from the app appear here automatically.
Drop your own *.gguf files here (any subfolder) and press "Models → Find downloaded".
Vision models need their mmproj-*.gguf file next to the model.
"@

Write-Text (Join-Path $app 'engine\README.txt') @"
RU: сборки llama.cpp (по одной папке на версию и бэкенд). Программа скачивает их сама.
Старые сборки удаляются кнопкой «Движок → Очистить старые сборки».

EN: llama.cpp builds (one folder per version and backend). The app downloads them itself.
Remove old builds with "Engine → Clean up old builds".
"@

New-Item -ItemType Directory -Force (Join-Path $app 'chats') | Out-Null

# ZIP: папка OfflineChatter в корне архива
$zip = Join-Path $Out "OfflineChatter-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }
[IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "ZIP: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 2)) MB)"

# RAR (WinRAR): пустые папки сохраняются, -ep1 — без пути до stage
$rarExe = @("$env:ProgramFiles\WinRAR\Rar.exe", "${env:ProgramFiles(x86)}\WinRAR\Rar.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $rarExe) { $rarExe = (Get-Command rar -ErrorAction SilentlyContinue)?.Source }
if ($rarExe) {
    $rar = Join-Path $Out "OfflineChatter-$Version.rar"
    if (Test-Path $rar) { Remove-Item $rar }
    & $rarExe a -r -ep1 -m5 -idq $rar $app
    if ($LASTEXITCODE -ne 0) { throw "rar failed with code $LASTEXITCODE" }
    Write-Host "RAR: $rar ($([math]::Round((Get-Item $rar).Length / 1MB, 2)) MB)"
} else {
    Write-Warning 'Rar.exe not found — RAR archive skipped (install WinRAR to build it)'
}
