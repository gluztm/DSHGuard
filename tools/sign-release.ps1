# sign-release.ps1 -- sign DSHGuard release artifacts with a code-signing certificate.
#
# Order matters: the installer embeds installer\payload\DSHGuard.exe verbatim, so the payload
# must be signed FIRST, then the setup is rebuilt from it, then the setup is signed.
# Signing the setup before rebuilding would leave an unsigned payload inside a signed setup.
#
# Usage (certificate already imported into Cert:\CurrentUser\My):
#   .\tools\sign-release.ps1 -Thumbprint 0123456789ABCDEF...
#
# Usage (directly from a .pfx, no import):
#   .\tools\sign-release.ps1 -PfxPath D:\certs\dsguard.pfx -PfxPassword 'secret'
#
# Add -SkipSetup to sign only the payload (no rebuild, no setup signing).

[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(ParameterSetName = 'Store', Mandatory = $true)]
    [string]$Thumbprint,

    [Parameter(ParameterSetName = 'Pfx', Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [string]$PfxPassword,

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [switch]$SkipSetup
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$payload = Join-Path $repo 'installer\payload\DSHGuard.exe'
$setup = Join-Path $repo 'dist\DSHGuard-Setup-2.3.0.exe'
$buildScript = Join-Path $repo 'installer\build-installer.ps1'

function Get-SignCert {
    if ($PSCmdlet.ParameterSetName -eq 'Pfx') {
        if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
        $secure = if ($PfxPassword) { ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force } else { $null }
        return Get-PfxCertificate -FilePath $PfxPath
    }
    $c = Get-ChildItem -Path 'Cert:\CurrentUser\My' | Where-Object { $_.Thumbprint -eq $Thumbprint }
    if (-not $c) { throw "No certificate with thumbprint $Thumbprint in Cert:\CurrentUser\My" }
    return $c
}

function Assert-CodeSigningCert($cert) {
    $hasCs = @($cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0
    if (-not $hasCs) {
        throw "Certificate $($cert.Thumbprint) has no Code Signing EKU (1.3.6.1.5.5.7.3.3). Signing with it produces a signature Windows will not treat as a publisher identity."
    }
    if (-not $cert.HasPrivateKey) {
        throw "Certificate $($cert.Thumbprint) has no private key loaded. Import the .pfx with its key, or use -PfxPath."
    }
    Write-Host ("cert OK: {0}" -f $cert.Subject)
    Write-Host ("         thumbprint {0}, expires {1}" -f $cert.Thumbprint, $cert.NotAfter)
}

function Invoke-Sign([string]$file, $cert) {
    if (-not (Test-Path $file)) { throw "missing artifact: $file" }
    Write-Host ("signing: {0}" -f $file)
    $args = @{
        FilePath      = $file
        Certificate   = $cert
        HashAlgorithm = 'SHA256'
    }
    if ($TimestampUrl) { $args.TimestampServer = $TimestampUrl }
    $result = Set-AuthenticodeSignature @args
    if ($result.Status -eq 'Valid') {
        Write-Host ("  OK  Status=Valid")
        return
    }
    if ($result.SignerCertificate -and $result.SignatureType -eq 'Authenticode') {
        # Signature block is present but this machine does not trust the chain (typical for a
        # self-signed rehearsal cert, or a freshly issued cert whose root is not yet installed).
        Write-Host ("  WARN Status={0} -- signature block written, signer={1}" -f $result.Status, $result.SignerCertificate.Subject)
        Write-Host ("       {0}" -f $result.StatusMessage)
        Write-Host '       A release build must reach Status=Valid on a machine with the CA chain installed.'
        return
    }
    throw ("signing failed for {0}: Status={1} Message={2}" -f $file, $result.Status, $result.StatusMessage)
}

function Show-Signature([string]$file) {
    $s = Get-AuthenticodeSignature -FilePath $file
    $signer = if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { '(none)' }
    Write-Host ("  {0}" -f (Split-Path -Leaf $file))
    Write-Host ("    Status : {0}" -f $s.Status)
    Write-Host ("    Signer : {0}" -f $signer)
    if ($s.TimeStamperCertificate) {
        Write-Host ("    Stamped: {0}" -f $s.TimeStamperCertificate.Subject)
    } else {
        Write-Host '    Stamped: (none)'
    }
    return $s
}

function Show-Hash([string]$file) {
    $item = Get-Item $file
    $hash = (Get-FileHash -Path $file -Algorithm SHA256).Hash
    Write-Host ("  {0}`n    size   : {1} bytes`n    sha256 : {2}" -f (Split-Path -Leaf $file), $item.Length, $hash)
}

$cert = Get-SignCert
Assert-CodeSigningCert $cert

Write-Host ''
Write-Host '--- 1/4 sign payload ---'
Invoke-Sign $payload $cert

Write-Host ''
Write-Host '--- 2/4 verify payload ---'
$ps = Show-Signature $payload
if ($ps.Status -ne 'Valid') { throw 'payload signature is not Valid after signing' }

if ($SkipSetup) {
    Write-Host ''
    Write-Host 'SkipSetup set: stopping before rebuild. The distributed setup still embeds the OLD payload.'
} else {
    Write-Host ''
    Write-Host '--- 3/4 rebuild setup from signed payload ---'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "build-installer.ps1 failed with exit $LASTEXITCODE" }

    Write-Host ''
    Write-Host '--- 4/4 sign setup ---'
    Invoke-Sign $setup $cert
    $ss = Show-Signature $setup
    if ($ss.Status -ne 'Valid') { throw 'setup signature is not Valid after signing' }
}

Write-Host ''
Write-Host '=== signed artifacts ==='
Show-Hash $payload
if (-not $SkipSetup) { Show-Hash $setup }

Write-Host ''
Write-Host 'Next: the sizes and SHA-256 above changed. Update them in'
Write-Host '  RELEASE-NOTES-2.3.0.md, HANDOFF-2.3.0-交付-20261026.md (section 5 table too), CHANGELOG.md'
Write-Host 'and move the previous values into the superseded list. Then re-run'
Write-Host '  .\installer\payload\DSHGuard.exe --selftest'
Write-Host 'because a signed single-file binary is still one self-extracting payload and must still pass 1031/1028/0/3.'
