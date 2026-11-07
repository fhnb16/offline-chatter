# Подпись файлов Authenticode (signtool, SHA-256, метка времени RFC 3161).
#
# Сертификат берётся из переменных окружения (в GitHub Actions — из секретов репозитория):
#   SIGN_PFX_BASE64 + SIGN_PFX_PASSWORD  — PFX-файл в base64 и пароль к нему;
#   SIGN_CERT_THUMBPRINT                — либо отпечаток сертификата в хранилище CurrentUser\My или LocalMachine\My
#                                          (так подключаются сертификаты на токене/смарт-карте через их драйвер);
#   SIGN_TIMESTAMP_URL                  — сервер меток времени (по умолчанию DigiCert);
#   SIGN_NO_TIMESTAMP=1                 — не ставить метку времени (только для локальной проверки без сети).
# Без сертификата скрипт ничего не подписывает и завершается успешно (с предупреждением), если не указан -Required.
#
# Примеры:
#   pwsh tools/sign.ps1 -Files src/OfflineChatter/bin/Release/net48/OfflineChatter.exe
#   pwsh tools/sign.ps1 -Files out/OfflineChatter-0.1.5-Setup.exe -Required
param(
    [Parameter(Mandatory)] [string[]]$Files,
    [switch]$Required,
    [string]$Description = 'Offline Chatter',
    [string]$Url = 'https://github.com/fhnb16/offline-chatter'
)
$ErrorActionPreference = 'Stop'

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin") | Where-Object { Test-Path $_ }
    foreach ($k in $kits) {
        $found = Get-ChildItem $k -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

$pfxB64 = $env:SIGN_PFX_BASE64
$thumb = $env:SIGN_CERT_THUMBPRINT
if (-not $pfxB64 -and -not $thumb) {
    $msg = 'No code signing certificate configured (SIGN_PFX_BASE64 or SIGN_CERT_THUMBPRINT) — files are left unsigned'
    if ($Required) { throw $msg }
    Write-Warning $msg
    exit 0
}

$signtool = Find-SignTool
# Без Windows SDK — встроенный Set-AuthenticodeSignature (только PFX; SHA-256, метка времени тоже ставится)
if (-not $signtool -and -not $pfxB64) { throw 'signtool.exe not found (install the Windows 10/11 SDK) — it is required for SIGN_CERT_THUMBPRINT' }
if (-not $signtool) { Write-Warning 'signtool.exe not found — using Set-AuthenticodeSignature' }
$timestamp = if ($env:SIGN_TIMESTAMP_URL) { $env:SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }

$pfxPath = $null
try {
    $certArgs = @()
    if ($pfxB64) {
        $pfxPath = Join-Path ([IO.Path]::GetTempPath()) ("oc-sign-" + [Guid]::NewGuid().ToString('N') + '.pfx')
        [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($pfxB64))
        $certArgs = @('/f', $pfxPath)
        if ($env:SIGN_PFX_PASSWORD) { $certArgs += @('/p', $env:SIGN_PFX_PASSWORD) }
    } else {
        $certArgs = @('/sha1', $thumb.Replace(' ', ''))
    }

    foreach ($file in $Files) {
        $path = (Resolve-Path $file).Path
        # Метка времени: подпись остаётся действительной и после истечения срока сертификата.
        # Несколько попыток — серверы меток времени иногда отвечают с ошибкой.
        $ok = $false
        for ($attempt = 1; $attempt -le 3 -and -not $ok; $attempt++) {
            if ($signtool) {
                # SIGN_NO_TIMESTAMP=1 — только для локальной проверки без сети: без метки подпись истекает вместе с сертификатом
                $tsSwitches = if ($env:SIGN_NO_TIMESTAMP) { @() } else { @('/td', 'sha256', '/tr', $timestamp) }
                & $signtool sign /fd sha256 @tsSwitches /d $Description /du $Url @certArgs $path | Out-Host
                $ok = $LASTEXITCODE -eq 0
            } else {
                $pfxCert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxPath, $env:SIGN_PFX_PASSWORD,
                    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
                $tsArgs = if ($env:SIGN_NO_TIMESTAMP) { @{} } else { @{ TimestampServer = $timestamp } }
                $r = Set-AuthenticodeSignature -FilePath $path -Certificate $pfxCert -HashAlgorithm SHA256 @tsArgs
                $ok = $null -ne $r.SignerCertificate
            }
            if (-not $ok) { Start-Sleep -Seconds (5 * $attempt) }
        }
        if (-not $ok) { throw "signtool failed for $path" }
        $sig = Get-AuthenticodeSignature $path
        # Самоподписанный сертификат даёт UnknownError (корень не доверен) — подпись при этом есть и цела
        Write-Host ("Signed {0}: {1} — {2}" -f (Split-Path $path -Leaf), $sig.Status, $sig.SignerCertificate.Subject)
        if ($sig.Status -eq 'HashMismatch' -or -not $sig.SignerCertificate) { throw "Signature check failed for $path" }
    }
}
finally {
    if ($pfxPath -and (Test-Path $pfxPath)) { [IO.File]::Delete($pfxPath) }
}
