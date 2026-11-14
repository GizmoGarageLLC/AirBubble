# Signs files with the project's self-signed code-signing certificate (CurrentUser\My, thumbprint in thumbprint.txt).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File sign.ps1 <file> [<file> ...]
# A timestamp (free DigiCert server; only the file hash is sent) keeps signatures valid after the certificate expires.
# If the certificate isn't on this machine the build continues unsigned (with a warning).
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Files)
$ErrorActionPreference = 'Stop'
$thumb = (Get-Content (Join-Path $PSScriptRoot 'thumbprint.txt') -ErrorAction SilentlyContinue | Select-Object -First 1)
$cert = if ($thumb) { Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Thumbprint -eq $thumb.Trim() } else { $null }
if (-not $cert) { Write-Host "WARNING: signing certificate not found on this machine - leaving files unsigned"; exit 0 }
foreach ($f in $Files) {
    $r = $null
    try { $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' -IncludeChain Signer }
    catch { }
    if (-not $r -or -not $r.SignerCertificate) {
        # offline? sign without a timestamp rather than not at all
        $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm SHA256 -IncludeChain Signer
        Write-Host "Signed (no timestamp - timestamp server unreachable): $f"
    }
    else { Write-Host ("Signed{0}: {1}" -f $(if ($r.TimeStamperCertificate) { ' + timestamped' } else { '' }), $f) }
}
