<#
check-plugin-updates.ps1: report available updates for installed plugins (read-only, writes nothing)

IMPORTANT: this file must stay pure ASCII (see "ENCODING" below). Do not add non-ASCII text.

USAGE:
  powershell -NoProfile -ExecutionPolicy Bypass -File check-plugin-updates.ps1 `
      [-ProfileDir <profile dir>] [-Registry https://registry.npmmirror.com] [-TimeoutSec 8] [-Only <package name>]

OUTPUT (stdout, UTF-8 without BOM, JSON):
  { "registry": "...", "checkedAt": "...", "profile": "...",
    "packages": [ { "name": "...", "installed": "...", "latest": "...", "hasUpdate": true,
                    "published": "yyyy-MM-dd HH:mm",
                    "created": "yyyy-MM-dd HH:mm",
                    "newDeclarationJson": "{\"dsh\":{\"compatibility\":{\"dsh\":\">=0.1.5-rc.1\"}},
                                            \"engines\":{\"dsh\":\">=0.1.4\"},
                                            \"peerDependencies\":{\"@deepseek-ai/dsh\":\"^0.1.5-rc.1\"}}",
                    "newDshRequirement": ">=0.1.5-rc.1",
                    "newRequirementSource": "dsh.compatibility", "error": null } ] }

NOTES:
  - Read-only: reads the profile package.json and each plugin package.json under node_modules,
    and issues GET requests to the registry mirror. No files are written.
  - hasUpdate is produced here by a built-in simplified semver comparison, for standalone
    command-line reference only. The DSHGuard UI recomputes it with C# VersionInfo.Compare,
    which is the single authoritative result.
  - newDeclarationJson is a verbatim copy of the declaration sites found in that version's
    package.json on the mirror (NESTED SHAPE PRESERVED). Only the four sites the criterion
    reads are copied; it is not the whole package.json and carries no unrelated fields such as
    scripts. The actual decision is made by C#: the DSHGuard UI derives both the required
    version and its source by calling VersionInfo.ExtractDshRequirement. That single field
    priority list is the only criterion anywhere.
  - newDshRequirement / newRequirementSource are for eye reference when running this script
    standalone on the command line. They are NOT a criterion: the UI never reads them (their
    ordering used to disagree with the C# one, which is exactly the "two drifting criteria"
    incident). Source values: dsh.compatibility / dsh.engines / engines / peerDependencies.
  - A failing single package query is written into that package error field only; other
    packages are unaffected.

ENCODING (do not break this):
  Keep this file ASCII with LF line endings. It is executed by Windows PowerShell 5.1,
  which parses a file WITHOUT a UTF-8 BOM as ANSI. Non-ASCII text in comments or in code
  was previously mis-decoded and broke the AST outright ("Unexpected token '}'"), which is
  why a UTF-8 BOM had to be kept. All comments and tokens are ASCII again, so the file no
  longer depends on a BOM: every ASCII-superset encoding decodes the token stream
  identically, and a tool that rewrites the file and drops the BOM can no longer break it.
  Span of pure ASCII: every line except the two quoted string literals listed below.
  Those are data, not tokens - a quoted literal is scanned byte-wise, so it survives any
  ASCII-superset decode. They are user-visible message text and are intentionally kept
  as-is (out of scope for the ASCII-comment task):
    line 166  Write-Host "profile package.json not found: $pkgFile"
    line 210  throw  'the mirror has no dist-tags.latest'
#>
param(
    [string]$ProfileDir = '',
    [string]$Registry = 'https://registry.npmmirror.com',
    [int]$TimeoutSec = 8,
    [string]$Only = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# ---- Simplified semver comparison (same idea as DSHGuard VersionInfo.Compare:
#      release > prerelease) ----
function Compare-Ver([string]$A, [string]$B) {
    if (-not $A) { $A = '0.0.0' }
    if (-not $B) { $B = '0.0.0' }
    $A = $A.Trim().TrimStart('^', '~', '>', '=', 'v', ' ')
    $B = $B.Trim().TrimStart('^', '~', '>', '=', 'v', ' ')

    $da = $A.IndexOf('-'); $db = $B.IndexOf('-')
    $ca = if ($da -ge 0) { $A.Substring(0, $da) } else { $A }
    $pa = if ($da -ge 0) { $A.Substring($da + 1) } else { $null }
    $cb = if ($db -ge 0) { $B.Substring(0, $db) } else { $B }
    $pb = if ($db -ge 0) { $B.Substring($db + 1) } else { $null }

    $xa = @($ca -split '\.'); $xb = @($cb -split '\.')
    $n = [Math]::Max($xa.Count, $xb.Count)
    for ($i = 0; $i -lt $n; $i++) {
        $va = 0; $vb = 0
        if ($i -lt $xa.Count) { [void][int]::TryParse(($xa[$i] -replace '[^0-9]', ''), [ref]$va) }
        if ($i -lt $xb.Count) { [void][int]::TryParse(($xb[$i] -replace '[^0-9]', ''), [ref]$vb) }
        if ($va -ne $vb) { if ($va -lt $vb) { return -1 } else { return 1 } }
    }
    if (-not $pa -and -not $pb) { return 0 }
    if (-not $pa) { return 1 }     # release > prerelease
    if (-not $pb) { return -1 }
    return [Math]::Sign([string]::CompareOrdinal($pa, $pb))
}

# ---- Verbatim copy of the "declaration sites" of that version on the mirror.
#      This file holds NO criterion: the single priority list lives in C# only. ----
# C# side: PluginManager.ParseUpdateReport deserializes newDeclarationJson into a
# package.json root object, then calls VersionInfo.ExtractDshRequirement to derive
# (requirement, source). So here we only copy the raw shape of package.json verbatim
# and never rewrite a value. Return $null only when all four sites are unreadable
# (C# then treats it as "not declared", as before).
#   dsh.compatibility.dsh (nested) -> dsh.engines.dsh (nested) -> engines.dsh (top level) -> peerDependencies
#   WARNING: the shape must stay NESTED. Do not flatten it into {compatibility:..., engines:...}:
#     ExtractDshRequirement reads the two NESTED paths dsh.compatibility.dsh and engines.dsh;
#     a flattened top-level compatibility / engines is not recognized at all. The first
#     version of this script fell into exactly that trap, and packages that declared all
#     four sites were downgraded to peerDependencies.
# This file does NOT sort, does NOT take the highest and does NOT compare: a field priority
# (dsh.engines -> peerDependencies, missing dsh.compatibility and top-level engines) used to
# be hand-written here, so the same declaration could yield different conclusions in the
# local list, the marketplace and the update card. That duplicated criterion was the root
# cause and has been removed.
function Get-DeclarationSnapshot($verObj) {
    if (-not $verObj) { return $null }
    $snap = [ordered]@{}
    try {
        $dsh = [ordered]@{}
        if ($verObj.dsh -and $verObj.dsh.compatibility -and $verObj.dsh.compatibility.dsh) {
            $dsh['compatibility'] = [ordered]@{ dsh = $verObj.dsh.compatibility.dsh }
        }
        if ($verObj.dsh -and $verObj.dsh.engines -and $verObj.dsh.engines.dsh) {
            $dsh['engines'] = [ordered]@{ dsh = $verObj.dsh.engines.dsh }
        }
        if ($dsh.Count -gt 0) { $snap['dsh'] = $dsh }
    } catch { }
    try {
        if ($verObj.engines -and $verObj.engines.dsh) { $snap['engines'] = [ordered]@{ dsh = $verObj.engines.dsh } }
    } catch { }
    try {
        if ($verObj.peerDependencies) { $snap['peerDependencies'] = $verObj.peerDependencies }
    } catch { }
    if ($snap.Count -eq 0) { return $null }
    return [pscustomobject]$snap
}

# Source annotation for eye reference when running this script standalone on the command
# line: NOT a criterion. The DSHGuard UI always uses the C# one
# (VersionInfo.ExtractDshRequirement). It exists only so this script's output explains
# itself; never treat it as a conclusion.
function Get-RequirementRef($snap) {
    if (-not $snap) { return @{ Req = ''; Src = '' } }
    if ($snap.dsh -and $snap.dsh.compatibility -and $snap.dsh.compatibility.dsh) {
        return @{ Req = [string]$snap.dsh.compatibility.dsh; Src = 'dsh.compatibility' }
    }
    if ($snap.dsh -and $snap.dsh.engines -and $snap.dsh.engines.dsh) {
        return @{ Req = [string]$snap.dsh.engines.dsh; Src = 'dsh.engines' }
    }
    if ($snap.engines -and $snap.engines.dsh) {
        return @{ Req = [string]$snap.engines.dsh; Src = 'engines' }
    }
    if ($snap.peerDependencies) {
        $best = ''; $bestVer = '0.0.0'
        foreach ($pd in $snap.peerDependencies.PSObject.Properties) {
            if ($pd.Name -notlike '@deepseek-ai/dsh*') { continue }
            $s = [string]$pd.Value
            if (-not $s) { continue }
            $m = [regex]::Match($s, '\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?')
            $v = if ($m.Success) { $m.Value } else { '0.0.0' }
            if ((Compare-Ver $v $bestVer) -gt 0) { $best = $s; $bestVer = $v }
        }
        if ($best) { return @{ Req = $best; Src = 'peerDependencies' } }
    }
    return @{ Req = ''; Src = '' }
}

if (-not $ProfileDir) { $ProfileDir = Join-Path $env:USERPROFILE '.dsh\profiles\web' }
$pkgFile = Join-Path $ProfileDir 'package.json'
if (-not (Test-Path $pkgFile)) {
    Write-Host "找不到 profile 的 package.json: $pkgFile"
    exit 2
}

$pkg = [System.IO.File]::ReadAllText($pkgFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$deps = @{}
if ($pkg.PSObject.Properties['dependencies']) {
    foreach ($p in $pkg.dependencies.PSObject.Properties) { $deps[$p.Name] = [string]$p.Value }
}

$results = New-Object System.Collections.ArrayList
$Registry = $Registry.TrimEnd('/')

foreach ($name in ($deps.Keys | Sort-Object)) {
    $spec = $deps[$name]
    # local / workspace dependencies have no concept of "latest online version": skip
    if ($spec -match '^(file|link|workspace|portal):') { continue }
    if ($Only -and $name -ne $Only) { continue }

    $installed = ''
    $modPkg = Join-Path (Join-Path (Join-Path $ProfileDir 'node_modules') $name) 'package.json'
    if (Test-Path $modPkg) {
        try { $installed = [string](([System.IO.File]::ReadAllText($modPkg, [System.Text.Encoding]::UTF8) | ConvertFrom-Json).version) } catch { }
    }

    $entry = [ordered]@{
        name                 = $name
        installed            = $installed
        latest               = ''
        hasUpdate            = $false
        published            = ''
        created              = ''
        newDeclarationJson   = ''
        newDshRequirement    = ''
        newRequirementSource = ''
        error                = $null
    }

    try {
        $url = $Registry + '/' + ($name -replace '/', '%2F')
        $doc = Invoke-RestMethod -Uri $url -TimeoutSec $TimeoutSec

        $latest = ''
        if ($doc.'dist-tags' -and $doc.'dist-tags'.latest) { $latest = [string]$doc.'dist-tags'.latest }
        if (-not $latest) { throw '镜像源里没有 dist-tags.latest' }
        $entry.latest = $latest

        if ($doc.time -and $doc.time.PSObject.Properties[$latest]) {
            $raw = [string]$doc.time.PSObject.Properties[$latest].Value
            try { $entry.published = ([datetime]$raw).ToLocalTime().ToString('yyyy-MM-dd HH:mm') } catch { $entry.published = $raw }
        }

        # created = the author's first release time. The registry "time" table holds the publish
        #   time of EVERY version, so the earliest entry is the first release. That same table
        #   also carries two non-version keys (created / modified): they are excluded here, and
        #   only keys shaped like a version number are counted. Format and fallback match
        #   "published" exactly. Empty or unreadable table leaves '' - never throws.
        try {
            if ($doc.time) {
                $earliestAt = $null
                $earliestRaw = ''
                foreach ($t in $doc.time.PSObject.Properties) {
                    if ($t.Name -eq 'created' -or $t.Name -eq 'modified') { continue }
                    if ($t.Name -notmatch '^\d+\.\d+\.\d+') { continue }
                    $craw = [string]$t.Value
                    if (-not $craw) { continue }
                    try { $when = [datetime]$craw } catch { continue }
                    if ($null -eq $earliestAt -or $when -lt $earliestAt) { $earliestAt = $when; $earliestRaw = $craw }
                }
                if ($null -ne $earliestAt) {
                    try { $entry.created = $earliestAt.ToLocalTime().ToString('yyyy-MM-dd HH:mm') } catch { $entry.created = $earliestRaw }
                }
            }
        } catch { $entry.created = '' }

        $verObj = $null
        if ($doc.versions -and $doc.versions.PSObject.Properties[$latest]) {
            $verObj = $doc.versions.PSObject.Properties[$latest].Value
        }
        if ($verObj) {
            # * The criterion is NOT here: the raw declaration text is handed to C#
            #   (see the notes above Get-DeclarationSnapshot).
            #   This file used to hand-write a field priority (dsh.engines -> peerDependencies),
            #   missing dsh.compatibility.dsh and top-level engines.dsh, so it inevitably
            #   drifted from the C# one. Removed.
            try {
                $declaration = Get-DeclarationSnapshot $verObj
                if ($declaration) {
                    # depth 4 is enough: top level + the four sites + one level under peerDependencies
                    $entry.newDeclarationJson = ($declaration | ConvertTo-Json -Depth 4 -Compress)
                }
            } catch { $entry.newDeclarationJson = '' }

            # command-line reference values (NOT a criterion, the UI never reads them):
            # priority is aligned with C# only so that what the eye sees matches the UI
            $ref = Get-RequirementRef $declaration
            $entry.newDshRequirement = [string]$ref.Req
            $entry.newRequirementSource = [string]$ref.Src
        }

        if ($installed) { $entry.hasUpdate = ((Compare-Ver $latest $installed) -gt 0) }
    }
    catch {
        $entry.error = $_.Exception.Message
    }

    [void]$results.Add([pscustomobject]$entry)
}

$report = [pscustomobject]@{
    registry  = $Registry
    checkedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    profile   = $ProfileDir
    packages  = @($results)
}
$report | ConvertTo-Json -Depth 6
