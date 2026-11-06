# Проверка переключения тем НА ЛЕТУ: запускает программу один раз, затем по очереди меняет оформление
# (второй запуск с --appearance Skin:Mode:Backdrop передаётся работающему окну) и снимает окно после каждого шага.
# Окно выводится на передний план — не запускайте, пока за компьютером работают.
#   pwsh tools/theme-tour.ps1 -Exe src/OfflineChatter/bin/Debug/net48/OfflineChatter.exe -Out shots
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Out,
    [string[]]$Steps = @(
        'Fluent:Light:Mica', 'Fluent:Dark:Mica', 'Fluent:Light:MicaAlt', 'Fluent:Dark:Acrylic', 'Fluent:Light:Acrylic',
        'Fluent:Light:None', 'Fluent:Dark:None', 'Aero:Light:Mica', 'Aero2:Light:None', 'Luna:Light:Acrylic',
        'Classic:Light:Mica', 'Aero:Dark:Mica', 'Fluent:Light:Mica'),
    [string]$Page = 'Settings',
    [string]$Section = 'Appearance',
    [double]$Scale = 0.5,
    [int]$StepDelayMs = 1800
)
$ErrorActionPreference = 'Stop'
# pwsh -File передаёт массив одной строкой — разрешаем «A:B:C,D:E:F»
$Steps = @($Steps | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim(" '") } | Where-Object { $_ })
Add-Type -AssemblyName System.Drawing
if (-not ('OcTour' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class OcTour {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out R r, int s);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
}
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$exePath = (Resolve-Path $Exe).Path

$startArgs = @('--page', $Page)
if ($Page -eq 'Settings' -and $Section) { $startArgs += '--section', $Section }
$p = Start-Process $exePath -ArgumentList $startArgs -PassThru
Start-Sleep 5
$p.Refresh()
if ($p.HasExited) { throw "Process exited with code $($p.ExitCode)" }
$h = $p.MainWindowHandle

$i = 0
foreach ($step in $Steps) {
    $i++
    Start-Process $exePath -ArgumentList '--appearance', $step -Wait
    Start-Sleep -Milliseconds $StepDelayMs
    [OcTour]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 500
    $r = New-Object OcTour+R
    [OcTour]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
    # Фактические атрибуты DWM: 20 — тёмный режим окна, 38 — тип системной подложки
    $dark = 0; $bd = 0
    [OcTour]::DwmGetWindowAttribute($h, 20, [ref]$dark, 4) | Out-Null
    [OcTour]::DwmGetWindowAttribute($h, 38, [ref]$bd, 4) | Out-Null
    $bmp = [Drawing.Bitmap]::new($r.Ri - $r.L, $r.B - $r.T)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $g.Dispose()
    if ($Scale -ne 1.0) { $small = [Drawing.Bitmap]::new($bmp, [int]($bmp.Width * $Scale), [int]($bmp.Height * $Scale)); $bmp.Dispose(); $bmp = $small }
    $name = ('{0:00}-{1}.png' -f $i, $step.Replace(':', '-'))
    $bmp.Save((Join-Path $Out $name))
    $bmp.Dispose()
    Write-Host ("{0,-24} dark={1} backdrop={2} -> {3}" -f $step, $dark, $bd, $name)
}
$p.CloseMainWindow() | Out-Null
Start-Sleep 2
