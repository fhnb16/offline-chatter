# Запускает собранное приложение, делает снимок главного окна и закрывает его.
# Для ручной проверки тем и раскладки. Пример:
#   pwsh tools/screenshot.ps1 -Exe src/OfflineChatter/bin/Debug/net48/OfflineChatter.exe -Out shot.png -Skin Aero -Mode Light -Backdrop Auto
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Out,
    [string]$Skin, [string]$Mode, [string]$Backdrop, [string]$Language,
    [double]$Scale = 1.0,
    [int]$WaitSeconds = 4,
    [switch]$HitTestMaxButton,
    [switch]$KeepOpen,
    [string]$Page,
    [string]$Prompt
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('OcShot' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class OcShot {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out R r, int s);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
}
'@
}
$dir = Split-Path (Resolve-Path $Exe) -Parent
$settingsPath = Join-Path $dir 'config\settings.json'
if ((Test-Path $settingsPath) -and ($Skin -or $Mode -or $Backdrop -or $Language)) {
    $s = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if ($Skin) { $s.Appearance.Skin = $Skin }
    if ($Mode) { $s.Appearance.Mode = $Mode }
    if ($Backdrop) { $s.Appearance.Backdrop = $Backdrop }
    if ($Language) { $s.Language = $Language }
    $s | ConvertTo-Json -Depth 6 | Set-Content $settingsPath -Encoding utf8
}
$argsList = @()
if ($Page) { $argsList += '--page', $Page }
if ($Prompt) { $argsList += '--prompt', ('"' + $Prompt.Replace('"', '\"') + '"') }
$p = if ($argsList.Count -gt 0) { Start-Process $Exe -ArgumentList $argsList -PassThru } else { Start-Process $Exe -PassThru }
Start-Sleep $WaitSeconds
$p.Refresh()
if ($p.HasExited) { throw "Process exited with code $($p.ExitCode)" }
$h = $p.MainWindowHandle
[OcShot]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 700
$r = New-Object OcShot+R
[OcShot]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
if ($HitTestMaxButton) {
    $b = New-Object OcShot+R
    [OcShot]::DwmGetWindowAttribute($h, 5, [ref]$b, 16) | Out-Null   # DWMWA_CAPTION_BUTTON_BOUNDS
    # Средняя из трёх кнопок — «развернуть»
    $x = $r.L + $b.L + [int](($b.Ri - $b.L) / 2); $y = $r.T + $b.T + [int](($b.B - $b.T) / 2)
    $ht = [OcShot]::SendMessage($h, 0x84, [IntPtr]::Zero, [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF)))
    Write-Host "WM_NCHITTEST at maximize button = $ht (HTMAXBUTTON = 9)"
}
$bmp = [Drawing.Bitmap]::new($r.Ri - $r.L, $r.B - $r.T)
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
if ($Scale -ne 1.0) { $bmp = [Drawing.Bitmap]::new($bmp, [int]($bmp.Width * $Scale), [int]($bmp.Height * $Scale)) }
$bmp.Save($Out)
if (-not $KeepOpen) { $p.CloseMainWindow() | Out-Null; Start-Sleep 2 }
Write-Host "Saved $Out"
