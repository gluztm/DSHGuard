<#
    安装官方 DSH 桌面版（DeepSeek Harness，Electron 应用）——用户级安装，不需要管理员权限。

    用法：
      powershell -NoProfile -ExecutionPolicy Bypass -File install-desktop.ps1
      powershell ... -File install-desktop.ps1 -Silent

    行为：
      1. 已经装了（卸载登记表里 DisplayName 含 "DeepSeek Harness"，或固定候选目录里有主程序）→ 跳过退出；
      2. 否则从**官方更新源**读清单、下载官方安装包，再把它启动起来（官方安装器自己会走完安装）；
      3. 本脚本不写任何系统级设置，也不改注册表。

    为什么从更新源取而不是写死下载地址：桌面版自带 electron-updater，安装目录里
    resources/app-update.yml 声明了 provider=generic 与 feed 地址、channel；
    照它的清单取包，地址换了也不会失效。

    退出码：0 = 可用（已安装或本次安装流程已走完）；1 = 失败（附原因与手工安装地址）。
#>
param([switch]$Silent)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$FeedUrl = "https://download.deepseek.com/dsh-desk/feeds/win-x64/"
$Channel = "nightly"

function Say($t) { Write-Host $t }
function Trace($t) {
    # 由安装包调用时控制台不可见，落地一行日志供排查与验收
    try {
        $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        Add-Content -Path (Join-Path $env:TEMP "dshguard-desktop-install.log") -Value "$stamp  $t" -Encoding UTF8
    } catch { }
}
function Fail($t) {
    Write-Host ""
    Write-Host "安装未完成：$t" -ForegroundColor Red
    Write-Host "可以手动安装：到 DeepSeek 官网下载「DeepSeek Harness」桌面版，一路下一步即可。"
    exit 1
}

function Get-DesktopInstallDir {
    $roots = @(
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    )
    foreach ($root in $roots) {
        $keys = @()
        try { $keys = Get-ChildItem $root -ErrorAction SilentlyContinue } catch { }
        foreach ($k in $keys) {
            $p = $null
            try { $p = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue } catch { }
            if ($p -and $p.DisplayName -like "*DeepSeek Harness*") {
                if ($p.InstallLocation -and (Test-Path (Join-Path $p.InstallLocation "DeepSeek Harness.exe"))) {
                    return $p.InstallLocation
                }
            }
        }
    }
    foreach ($c in @((Join-Path $env:LOCALAPPDATA "Programs\DeepSeek Harness"),
                     (Join-Path $env:LOCALAPPDATA "Programs\dsh-desktop"))) {
        if (Test-Path (Join-Path $c "DeepSeek Harness.exe")) { return $c }
    }
    return ""
}

Say "正在检查是否已安装 DSH 桌面版…"
Trace "开始：检查是否已安装"
$installed = Get-DesktopInstallDir
if ($installed) {
    Say "已检测到桌面版：$installed，无需安装。"
    Trace "跳过：已安装于 $installed"
    exit 0
}

Say "正在从官方更新源读取最新版本信息…"
$yml = ""
try {
    $yml = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 30 -Uri ($FeedUrl + $Channel + ".yml")).Content
} catch {
    Fail "读取官方更新清单失败：$($_.Exception.Message)"
}

# 清单是 electron-updater 的 yml：值可能写成折叠块（`path: >-` 换行再写地址，官方源 2026-10 实测如此），
# 旧写法只认同一行的值，会把文件名读成字面量 ">-"。这里把折叠块接回同一行再取。
$lines = @($yml -split "\r?\n")
$top = @{}
for ($i = 0; $i -lt $lines.Count; $i++) {
    $raw = $lines[$i]
    if ($raw -notmatch '^([A-Za-z0-9_]+):\s*(.*)$') { continue }   # 只看顶层键（行首无缩进）
    $key = $Matches[1]; $val = $Matches[2].Trim()
    if ($val -in @('>-', '>', '|', '|-')) {
        $parts = @()
        while ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s+\S') { $i++; $parts += $lines[$i].Trim() }
        $val = ($parts -join ' ')
    }
    $top[$key] = $val.Trim([char]34, [char]39)
}
$version = [string]$top['version']
$path = [string]$top['path']
$sha512 = [string]$top['sha512']
if (-not $path) { Fail "官方更新清单里没有安装包地址（读到的版本：$version）" }

# 地址两种写法：同源相对文件名，或 https://download.deepseek.com/ 下的绝对地址；其余一律拒绝
if ($path.Contains("://")) {
    try { $u = [Uri]$path } catch { Fail "官方更新清单里的地址不合法：$path" }
    if ($u.Scheme -ne 'https' -or $u.Host -ne 'download.deepseek.com' -or $u.Query -or $u.Fragment) {
        Fail "官方更新清单里的地址不在官方域名下：$path"
    }
    $url = $u.AbsoluteUri
    $file = [Uri]::UnescapeDataString($u.Segments[-1])
} else {
    $file = $path
    $url = $FeedUrl + [Uri]::EscapeDataString($path)
}
if (-not $file -or $file.Contains("/") -or $file.Contains("\") -or $file.Contains("..") -or
    $file.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or -not $file.EndsWith(".exe")) {
    Fail "官方更新清单里的文件名不合法：$file"
}

$dir = Join-Path $env:TEMP "DSHGuard-DesktopSetup"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$out = Join-Path $dir $file
Say "下载桌面版 $version 安装包：$file"
Trace "下载 $version / $url"
try {
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 900 -Uri $url -OutFile $out
} catch {
    Fail "下载安装包失败：$($_.Exception.Message)"
}
if (-not (Test-Path $out)) { Fail "下载没有产生文件：$out" }
$size = (Get-Item $out).Length
if ($size -le 0) { Fail "下载到的安装包是空的：$out" }
Trace ("已下载：{0}（{1} MB）" -f $out, [math]::Round($size / 1MB, 1))

# 执行前校验（2026-10-16 审计 H1）：清单给了 sha512 就必须一致；签名存在却校验不过一律拒绝；
# 两样保证都没有时不执行（宁可让用户去官网手动下载）。
$shaOk = $false
if ($sha512) {
    $hex = (Get-FileHash -Algorithm SHA512 -Path $out).Hash
    $bytes = New-Object byte[] ($hex.Length / 2)
    for ($k = 0; $k -lt $bytes.Length; $k++) { $bytes[$k] = [Convert]::ToByte($hex.Substring($k * 2, 2), 16) }
    if ([Convert]::ToBase64String($bytes) -ne $sha512) {
        Remove-Item $out -Force -ErrorAction SilentlyContinue
        Fail "安装包校验失败（SHA-512 与官方清单不一致），已删除，未执行"
    }
    $shaOk = $true
}
$sig = Get-AuthenticodeSignature -FilePath $out
if ($sig.Status -ne 'Valid' -and $sig.Status -ne 'NotSigned') {
    Remove-Item $out -Force -ErrorAction SilentlyContinue
    Fail "安装包的数字签名校验失败（$($sig.Status)），已删除，未执行"
}
if (-not $shaOk -and $sig.Status -ne 'Valid') {
    Remove-Item $out -Force -ErrorAction SilentlyContinue
    Fail "官方清单未提供校验值且安装包无有效签名，无法确认来源，已删除，未执行"
}
Trace ("校验通过：SHA-512={0}；签名={1}" -f $shaOk, $sig.Status)

Say "正在启动官方安装包 —— 请按它自己的界面完成安装。"
Trace "启动安装器"
if ($Silent) { Start-Process -FilePath $out -ArgumentList "/S" -Wait } else { Start-Process -FilePath $out -Wait }
Say "官方安装流程已结束。装好后回到本程序，点「启动桌面版」即可。"
Trace "安装器已退出"
exit 0
