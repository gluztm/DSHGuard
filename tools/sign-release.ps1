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

# ── 版本号：唯一出处是 GuardVersion.cs（2.3.2 改）──
#   此前这里写死 'dist\DSHGuard-Setup-2.3.0.exe'，于是：重建产出的其实是新版本（版本号从
#   GuardVersion.cs 读），但签名那一步签的仍是写死路径上的**旧版本文件** ⇒
#   新包没签、旧包被重签，而且全程不报错。现在版本跟着一起来。
$gv = Get-Content (Join-Path $repo 'GuardVersion.cs') -Raw -Encoding UTF8
$mMajor = [regex]::Match($gv, 'Major\s*=\s*(\d+)')
$mMinor = [regex]::Match($gv, 'Minor\s*=\s*(\d+)')
$mPatch = [regex]::Match($gv, 'Patch\s*=\s*(\d+)')
if (-not $mMinor.Success -or -not $mPatch.Success) { throw '无法从 GuardVersion.cs 读出 Minor/Patch' }
$major = if ($mMajor.Success) { [int]$mMajor.Groups[1].Value } else { 1 }
$minor = [int]$mMinor.Groups[1].Value
$patch = [int]$mPatch.Groups[1].Value
$Version = if ($major -ge 2) { "$major.$minor.$patch" } else { $(if ($patch -le 0) { "1.$minor" } else { "1.$minor.$patch" }) }
Write-Host ("version : {0}" -f $Version)

$payload = Join-Path $repo 'installer\payload\DSHGuard.exe'
$setup = Join-Path $repo "dist\DSHGuard-Setup-$Version.exe"
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
    # ⚠ 必须带 -SkipPublish（2.3.2 修）：
    #   不带的话，build-installer 会重新 dotnet publish，把**刚签好的 payload 覆盖成未签名的**，
    #   于是装进机器的是一个没签名的 exe，而外面套着一个签了名的 setup —— 签名形同虚设，
    #   而且从表面看不出来（setup 的签名状态是 Valid）。
    #   带 -SkipPublish 时，脚本只重新收集 payload（还是那个已签名的字节）+ 重新调 ISCC。
    & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript -SkipPublish -SkipSelfTest -KeepPayload
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

# ── 签名后重写校验记录（2.3.2）：签名会改变字节，哈希必须重算落盘 ──
#   不重写的话，dist\SHA256SUMS-<ver>.txt 里留的是**签名前**的哈希 ⇒
#   第三方拿签名后的包去核对，记录对不上，反而成了「这包有问题」的假信号。
if (-not $SkipSetup) {
    $sums = Join-Path $repo "dist\SHA256SUMS-$Version.txt"
    $payloadHash = (Get-FileHash -Path $payload -Algorithm SHA256).Hash
    $setupHash = (Get-FileHash -Path $setup -Algorithm SHA256).Hash
    $commit = ''
    try { $commit = (git rev-parse --short=12 HEAD 2>$null).Trim() } catch { }
    @(
        "$setupHash  $(Split-Path -Leaf $setup)",
        "$payloadHash  DSHGuard-$Version-payload.exe",
        '',
        "# version $Version / commit $commit / signed $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "# payload is the DSHGuard.exe embedded in this installer",
        "# both artifacts are code-signed; the hashes below are the POST-signing values",
        "# selftest: re-run the signed payload (signing changes the bytes, not the logic)"
    ) | Out-File -FilePath $sums -Encoding ASCII
    Write-Host "校验记录已重写（签名后哈希）：$sums" -ForegroundColor Green
}

# ── 发布说明正本在 GitHub Release 页面（仓库根的 RELEASE-NOTES.md 是历代版本一览表）──
$releaseUrl = ""
try {
    $origin = (git remote get-url origin 2>$null | Select-Object -First 1)
    if ($origin -and ($origin -match "github[.]com[:/](?<repo>[^/]+/[^/]+?)([.]git)?$")) {
        $releaseUrl = "https://github.com/$($Matches['repo'])/releases/tag/$Version"
    }
} catch { }
if (-not $releaseUrl) { $releaseUrl = "https://github.com/gluztm/DSHGuard/releases/tag/$Version" }

Write-Host ''
Write-Host 'Next: the sizes and SHA-256 above changed. Update them in'
Write-Host ("  CHANGELOG.md, and the GitHub Release body for {0}:" -f $Version)
Write-Host ("  {0}" -f $releaseUrl)
Write-Host '  (the repo keeps one RELEASE-NOTES.md listing every release;'
Write-Host '  the Release page is the canonical release note). Then re-run'
Write-Host '  .\installer\payload\DSHGuard.exe --selftest'
Write-Host 'because a signed single-file binary is still one self-extracting payload and must still pass 0 failures.'
