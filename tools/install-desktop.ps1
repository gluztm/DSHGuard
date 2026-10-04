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

$version = ""
$file = ""
foreach ($raw in ($yml -split "\r?\n")) {
    $line = $raw.Trim()
    if ($line -match "^version:\s*(.+)$") { $version = $Matches[1].Trim([char]34, [char]39) }
    elseif ($line -match "^path:\s*(.+)$") { $file = $Matches[1].Trim([char]34, [char]39) }
}
if (-not $file) { Fail "官方更新清单里没有安装包文件名（读到的版本：$version）" }
# 只接受同源文件名：清单被改过时，不允许把任意路径或网址塞进来
if ($file.Contains("/") -or $file.Contains("\") -or $file.Contains("..") -or $file.Contains("://")) {
    Fail "官方更新清单里的文件名不合法：$file"
}

$dir = Join-Path $env:TEMP "DSHGuard-DesktopSetup"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$out = Join-Path $dir $file
Say "下载桌面版 $version 安装包：$file"
Trace "下载 $version / $file"
try {
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 900 -Uri ($FeedUrl + $file) -OutFile $out
} catch {
    Fail "下载安装包失败：$($_.Exception.Message)"
}
if (-not (Test-Path $out)) { Fail "下载没有产生文件：$out" }
$size = (Get-Item $out).Length
if ($size -le 0) { Fail "下载到的安装包是空的：$out" }
Trace ("已下载：{0}（{1} MB）" -f $out, [math]::Round($size / 1MB, 1))

Say "正在启动官方安装包 —— 请按它自己的界面完成安装。"
Trace "启动安装器"
if ($Silent) { Start-Process -FilePath $out -ArgumentList "/S" -Wait } else { Start-Process -FilePath $out -Wait }
Say "官方安装流程已结束。装好后回到本程序，点「启动桌面版」即可。"
Trace "安装器已退出"
exit 0
