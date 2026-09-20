<#
    安装运行环境（Node.js LTS）——用户级安装，不需要管理员权限。

    用法：
      powershell -NoProfile -ExecutionPolicy Bypass -File install-node.ps1
      powershell ... -File install-node.ps1 -TargetDir <目录> [-Force]

    行为：
      1. 已经装过（命令行或本程序目录里能找到 node）→ 直接跳过并退出；
      2. 否则下载官方 Windows ZIP 版，解压到用户目录，并把该目录加入「用户 PATH」；
      3. 全程不需要管理员权限，也不改动系统级设置。

    退出码：0 = 可用（已安装或本次安装完成）；1 = 失败（附原因与手工安装地址）。
#>
param(
    [string]$TargetDir = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Say($text) { Write-Host $text }
function Trace($text) {
    # 留一行落地记录：由安装包调用时控制台不可见，排查与验收靠它确认执行情况
    try {
        $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text
        Add-Content -Path (Join-Path $env:TEMP "dshguard-node-install.log") -Value $line -Encoding UTF8
    } catch { }
}
function Fail($text) {
    Write-Host ""
    Write-Host "安装未完成：$text" -ForegroundColor Red
    Write-Host "可以手动安装：打开 https://nodejs.org/zh-cn 下载「LTS」版，一路下一步即可。"
    exit 1
}

$target = if ($TargetDir) { $TargetDir } else { Join-Path $env:LOCALAPPDATA "Programs\nodejs" }
Say "运行环境目录：$target"
Trace "开始：目标目录 $target"

# ① 已安装则跳过
if (-not $Force) {
    $existing = $null
    $cmd = Get-Command node -ErrorAction SilentlyContinue
    if ($cmd) { $existing = $cmd.Source }
    elseif (Test-Path (Join-Path $target "node.exe")) { $existing = Join-Path $target "node.exe" }
    if ($existing) {
        try {
            $ver = & $existing -v
            Say "已检测到 Node.js $ver，无需安装。"
        Trace "跳过：已安装 Node.js $ver"
            exit 0
        } catch { }
    }
}

# ② 取最新 LTS 版本号（官方源取不到就用国内镜像兜底）
$version = $null
try {
    Say "正在查询最新长期支持版本…"
    $index = Invoke-RestMethod -Uri "https://nodejs.org/dist/index.json" -TimeoutSec 30
    $lts = $index | Where-Object { $_.lts } | Select-Object -First 1
    if ($lts) { $version = $lts.version }
} catch { }
if (-not $version) { $version = "v22.21.0" }
Say "将安装：Node.js $version（长期支持版）"

# ③ 下载（官方源失败自动换镜像）
$zipName = "node-$version-win-x64.zip"
$temp = Join-Path $env:TEMP ("node-install-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$zipPath = Join-Path $temp $zipName

$urls = @(
    "https://nodejs.org/dist/$version/$zipName",
    "https://npmmirror.com/mirrors/node/$version/$zipName"
)
$downloaded = $false
foreach ($url in $urls) {
    try {
        Say "正在下载：$url"
        Invoke-WebRequest -Uri $url -OutFile $zipPath -TimeoutSec 600 -UseBasicParsing
        if ((Get-Item $zipPath).Length -gt 5MB) { $downloaded = $true; break }
        Say "下载内容不完整，换下一个地址重试。"
    } catch {
        Say "这个地址没成功（$($_.Exception.Message)），换下一个地址。"
    }
}
if (-not $downloaded) { Fail "下载 Node.js 失败（网络不可用或被拦截）。" }

# ④ 解压到目标目录
try {
    Say "正在解压…"
    Expand-Archive -Path $zipPath -DestinationPath $temp -Force
    $inner = Get-ChildItem $temp -Directory | Where-Object { $_.Name -like "node-*-win-x64" } | Select-Object -First 1
    if (-not $inner) { Fail "下载的压缩包结构不符合预期。" }
    if (Test-Path $target) { Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Move-Item $inner.FullName $target -Force
} catch {
    Fail "解压失败：$($_.Exception.Message)"
}

# ⑤ 写入「用户 PATH」（只影响当前用户，不需要管理员）
# 指定了 -TargetDir 的属于测试/临时安装，不写 PATH，避免留下失效条目
if ($TargetDir) {
    Say "（指定了目标目录，跳过写入 PATH）"
} else {
    try {
        $userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
        if (-not $userPath) { $userPath = "" }
        $parts = $userPath.Split(';') | Where-Object { $_ -and $_.Trim() -ne "" }
        if (-not ($parts | Where-Object { $_.TrimEnd('\') -ieq $target.TrimEnd('\') })) {
            $newPath = ($target + ";" + $userPath).TrimEnd(';')
            [Environment]::SetEnvironmentVariable("PATH", $newPath, "User")
            Say "已把运行环境加入用户 PATH。"
        }
    } catch {
        Say "提示：写入用户 PATH 失败（不影响使用，程序会直接找安装目录）。"
    }
}

# ⑥ 通知系统环境已变化（尽力而为）
try {
    Add-Type -Namespace Win32 -Name NativeMethods -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.IntPtr lpdwResult);
"@ -ErrorAction SilentlyContinue
    $result = [System.IntPtr]::Zero
    [void][Win32.NativeMethods]::SendMessageTimeout([System.IntPtr]0xffff, 0x001A, [System.IntPtr]::Zero, "Environment", 2, 5000, [ref]$result)
} catch { }

# ⑦ 自检
try {
    $nodeExe = Join-Path $target "node.exe"
    $npxCmd = Join-Path $target "npx.cmd"
    if (-not (Test-Path $nodeExe)) { Fail "安装目录里没有找到 node.exe。" }
    $nodeVer = & $nodeExe -v
    $npxVer = if (Test-Path $npxCmd) { & $npxCmd -v } else { "未找到" }
    Say ""
    Say "安装完成：Node.js $nodeVer（npx $npxVer）"
    Say "安装位置：$target"
    Say "现在可以回到程序，点「一键启动引擎」开始使用。"
    Trace "完成：Node.js $nodeVer / npx $npxVer → $target"
    exit 0
} catch {
    Fail "自检失败：$($_.Exception.Message)"
}
