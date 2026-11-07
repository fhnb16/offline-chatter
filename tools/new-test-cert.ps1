# Самоподписанный сертификат подписи кода — для проверки подписи в CI и локально.
# Создаётся в памяти и сразу сохраняется в PFX: хранилище сертификатов Windows не меняется.
# Windows такому сертификату не доверяет: SmartScreen и «Неизвестный издатель» он не убирает —
# для этого нужен сертификат от удостоверяющего центра (README, раздел «Подпись»).
#
#   pwsh tools/new-test-cert.ps1 -Out test-sign.pfx -Password 'пароль'
# Секреты для CI: SIGN_PFX_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes('test-sign.pfx')), SIGN_PFX_PASSWORD = пароль.
param(
    [string]$Out = 'test-sign.pfx',
    [Parameter(Mandatory)] [string]$Password,
    [string]$Subject = 'CN=Offline Chatter (test)',
    [int]$Years = 3
)
$ErrorActionPreference = 'Stop'
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    $req = [Security.Cryptography.X509Certificates.CertificateRequest]::new($Subject, $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $req.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $eku = [Security.Cryptography.OidCollection]::new(); [void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) # Code Signing
    $req.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
    $cert = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears($Years))
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
    [IO.File]::WriteAllBytes($full, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password))
    Write-Host "Created $Out ($($cert.Subject), thumbprint $($cert.Thumbprint))"
}
finally {
    $rsa.Dispose()
}
