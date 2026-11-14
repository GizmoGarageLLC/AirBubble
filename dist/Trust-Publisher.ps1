# OPTIONAL - run only on PCs you own, as administrator.
# Makes this PC trust the self-signed "PC Viewer for XREAL glasses" certificate, so the app's signature shows as
# valid with a known publisher. It does NOT affect other people's PCs, and SmartScreen may still ask once for
# files downloaded from the internet (it judges download reputation, not just signatures).
#
#   Trust:   powershell -ExecutionPolicy Bypass -File Trust-Publisher.ps1
#   Undo:    powershell -ExecutionPolicy Bypass -File Trust-Publisher.ps1 -Remove
#
# Security note: trusting a self-made root lets anything signed with its private key be trusted on this PC.
# The private key exists only in the build PC's user certificate store - keep that PC/account secure.
param([switch]$Remove)
$cer = Join-Path $PSScriptRoot 'XrealPcViewer-publisher.cer'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Start-Process powershell -Verb RunAs -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "{0}" {1}' -f $PSCommandPath, $(if ($Remove) { '-Remove' } else { '' }))
    exit
}
$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
foreach ($store in 'Root', 'TrustedPublisher') {
    $s = New-Object System.Security.Cryptography.X509Certificates.X509Store($store, 'LocalMachine')
    $s.Open('ReadWrite')
    if ($Remove) { $s.Remove($c) } else { $s.Add($c) }
    $s.Close()
}
Write-Host $(if ($Remove) { "Removed trust for: $($c.Subject)" } else { "This PC now trusts: $($c.Subject)" })
Start-Sleep 3
