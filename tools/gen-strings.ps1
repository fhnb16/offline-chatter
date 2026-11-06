# Генерирует Strings.resx (EN) и StringsRu.resx (RU) из единой таблицы Strings.tsv.
# RU лежит в основной сборке как отдельный ресурс (не satellite), чтобы exe оставался одним файлом.
# Формат строки: key<TAB>english<TAB>russian. "\n" внутри текста — перевод строки. Строки на "#" — комментарии.
# Запуск: pwsh tools/gen-strings.ps1
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\src\OfflineChatter\Localization\Strings.tsv')
)
$ErrorActionPreference = 'Stop'
$dir = Split-Path $Source -Parent
$rows = [ordered]@{}
$lineNo = 0
foreach ($line in [IO.File]::ReadAllLines($Source, [Text.Encoding]::UTF8)) {
    $lineNo++
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
    $parts = $line.Split("`t")
    if ($parts.Count -ne 3) { throw "Strings.tsv:${lineNo}: expected 3 tab-separated columns, got $($parts.Count)" }
    $key = $parts[0].Trim()
    if ($key -notmatch '^[A-Za-z][A-Za-z0-9_]*$') { throw "Strings.tsv:${lineNo}: bad key '$key'" }
    if ($rows.Contains($key)) { throw "Strings.tsv:${lineNo}: duplicate key '$key'" }
    if ([string]::IsNullOrWhiteSpace($parts[1]) -or [string]::IsNullOrWhiteSpace($parts[2])) { throw "Strings.tsv:${lineNo}: empty translation for '$key'" }
    $rows[$key] = @($parts[1].Replace('\n', "`n"), $parts[2].Replace('\n', "`n"))
}

function Write-Resx([string]$path, [int]$col) {
    $sb = [Text.StringBuilder]::new()
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine('<!-- Сгенерировано tools/gen-strings.ps1 из Strings.tsv. Не редактировать вручную. -->')
    [void]$sb.AppendLine('<root>')
    [void]$sb.AppendLine('  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>')
    [void]$sb.AppendLine('  <resheader name="version"><value>2.0</value></resheader>')
    [void]$sb.AppendLine('  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>')
    [void]$sb.AppendLine('  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>')
    foreach ($k in $rows.Keys) {
        $v = [Security.SecurityElement]::Escape($rows[$k][$col])
        [void]$sb.AppendLine("  <data name=`"$k`" xml:space=`"preserve`"><value>$v</value></data>")
    }
    [void]$sb.AppendLine('</root>')
    [IO.File]::WriteAllText($path, $sb.ToString(), [Text.UTF8Encoding]::new($false))
}

Write-Resx (Join-Path $dir 'Strings.resx') 0
Write-Resx (Join-Path $dir 'StringsRu.resx') 1
Write-Host "Generated $($rows.Count) strings."
