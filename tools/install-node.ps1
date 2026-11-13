<#
    安装运行环境（Node.js LTS）——用户级安装，不需要管理员权限。

    用法：
      powershell -NoProfile -ExecutionPolicy Bypass -File install-node.ps1
      powershell ... -File install-node.ps1 -TargetDir <目录> [-Force]
      powershell ... -File install-node.ps1 -PnpmOnly        # 只补 pnpm，不碰 Node

    行为：
      1. 已经装过（命令行或本程序目录里能找到 node）→ 直接跳过并退出；
      2. 否则下载官方 Windows ZIP 版，解压到用户目录，并把该目录加入「用户 PATH」；
      3. 全程不需要管理员权限，也不改动系统级设置。
      4. -PnpmOnly：目标机已有 node/npx、只缺 pnpm 时用它 —— 不下载 Node，
         只在合适的位置把 pnpm 补上（落点见下方 ⓪ 段）。

    退出码：0 = 可用（已安装或本次安装完成）；1 = 失败（附原因与手工安装地址）。
#>
param(
    [string]$TargetDir = "",
    [switch]$Force,
    [switch]$PnpmOnly
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

# ⓪ -PnpmOnly：只补 pnpm，不碰 Node
# 什么时候用：目标机已经有 node / npx（多半是系统级安装在 C:\Program Files\nodejs），只缺 pnpm。
# 这时候不能重装 Node（会覆盖用户现有环境、还要重下约 100 MB），也不该靠「① 已安装则跳过」混过去。
# 落点规则（本机实测得出，不是猜的）：
#   · node 目录可写 → 落 node 目录，和全量安装走同一条路（corepack 的 %~dp0 相对回跳才命中）；
#   · node 目录不可写（系统级安装，普通用户写入会 UnauthorizedAccessException）→ 退 npm 全局前缀
#     （npm prefix -g 的第一行），那里一般早已在用户 PATH 上，装完立即可用；
#   · 都不可写 → 明确失败，不装作成功。
# 判据和 ⑤-b 一样：只看 pnpm.cmd 在不在 + 能不能取到版本号，绝不看输出（npm 必打 stderr）。
function Test-DirWritable([string]$dir) {
    try {
        if (-not (Test-Path $dir)) { return $false }
        $probe = Join-Path $dir (".dshguard-wtest-" + [Guid]::NewGuid().ToString("N") + ".tmp")
        [System.IO.File]::WriteAllText($probe, "x")
        Remove-Item $probe -Force -ErrorAction SilentlyContinue
        return $true
    } catch { return $false }
}

function Get-NpmGlobalPrefix([string]$nodeDir) {
    # npm prefix -g 的第一行就是全局前缀；取不到返回空串
    $npmInNode = Join-Path $nodeDir "npm.cmd"
    if (-not (Test-Path $npmInNode)) { return "" }
    $prevEapLocal = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $text = ""
    try { $text = (& $npmInNode prefix -g 2>&1 | Out-String) } catch { }
    $ErrorActionPreference = $prevEapLocal
    if (-not $text) { return "" }
    # 输出里可能混着 warn（stderr 合并进来），只认第一行看起来像绝对路径的
    foreach ($l in ($text -split "\r?\n")) {
        $t = $l.Trim()
        if ($t -and ($t -match '^[A-Za-z]:\\')) { return $t.TrimEnd('\') }
    }
    return ""
}

function Install-PnpmInto([string]$dir) {
    # 在指定目录里把 pnpm 装上；成功返回 $true。全过程只记日志，不抛异常。
    $pnpm = Join-Path $dir "pnpm.cmd"
    if (Test-Path $pnpm) { return $true }
    $prevEapLocal = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $corepackCmd = Join-Path $dir "corepack.cmd"
    if (Test-Path $corepackCmd) {
        try {
            Trace "尝试用 corepack 安装 pnpm（$dir）"
            $out = & $corepackCmd enable pnpm --install-directory $dir 2>&1 | Out-String
            Trace ("corepack enable pnpm 退出码：{0}" -f $LASTEXITCODE)
            if ($out -and $out.Trim()) { Trace ("corepack 输出：{0}" -f $out.Trim()) }
        } catch { Trace ("corepack 方式失败：{0}" -f $_.Exception.Message) }
    }
    if (-not (Test-Path $pnpm)) {
        $npmCmd = Join-Path $dir "npm.cmd"
        if (Test-Path $npmCmd) {
            try {
                Trace "尝试用 npm 安装 pnpm（--prefix $dir）"
                $out = & $npmCmd install -g pnpm --prefix $dir --no-fund --no-audit 2>&1 | Out-String
                Trace ("npm install -g pnpm 退出码：{0}" -f $LASTEXITCODE)
                if ($out -and $out.Trim()) { Trace ("npm 输出：{0}" -f $out.Trim()) }
            } catch { Trace ("npm 方式失败：{0}" -f $_.Exception.Message) }
        }
    }
    $ErrorActionPreference = $prevEapLocal
    return (Test-Path $pnpm)
}

# 环境变化广播：两条写 PATH 的路径（Add-ToUserPath 与 ⑤ 主路径）写成功后调用，
# 让资源管理器等已开进程立即感知新 PATH，不必注销重启。尽力而为，失败静默。
function Notify-EnvironmentChanged {
    try {
        Add-Type -Namespace Win32 -Name NativeMethods -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.IntPtr lpdwResult);
"@ -ErrorAction SilentlyContinue
        $result = [System.IntPtr]::Zero
        [void][Win32.NativeMethods]::SendMessageTimeout([System.IntPtr]0xffff, 0x001A, [System.IntPtr]::Zero, "Environment", 2, 5000, [ref]$result)
    } catch { }
}

# git 可选提示：git 没装引擎照常能跑，只影响依赖 git 拉取的插件；在两条正常出口提前说清楚
function Hint-GitOptional {
    $gitCmd = Get-Command git -ErrorAction SilentlyContinue
    if (-not $gitCmd) {
        Say "提示：未检测到 git，可选组件；引擎核心功能不受影响，部分插件依赖 git 拉取会失败。可从 https://git-scm.com/download/win 安装。"
        Trace "git 缺席提示已发出"
    }
}

function Add-ToUserPath([string]$dir) {
    try {
        $userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
        if (-not $userPath) { $userPath = "" }
        $parts = $userPath.Split(';') | Where-Object { $_ -and $_.Trim() -ne "" }
        if (-not ($parts | Where-Object { $_.TrimEnd('\') -ieq $dir.TrimEnd('\') })) {
            $newPath = ($dir + ";" + $userPath).TrimEnd(';')
            [Environment]::SetEnvironmentVariable("PATH", $newPath, "User")
            Say "已把 pnpm 目录加入用户 PATH：$dir"
            Trace "已写入用户 PATH：$dir"
            Notify-EnvironmentChanged
        }
    } catch { Trace ("写用户 PATH 失败：{0}" -f $_.Exception.Message) }
}

if ($PnpmOnly) {
    Trace "PnpmOnly：只补 pnpm，不碰 Node"
    $nodeCmd = Get-Command node -ErrorAction SilentlyContinue
    if (-not $nodeCmd) {
        Fail "本机没有 node，无法只补 pnpm。去掉 -PnpmOnly 重跑即可安装完整运行环境。"
    }
    $nodeDir = Split-Path -Parent $nodeCmd.Source
    Say "运行环境位置：$nodeDir"
    Trace "PnpmOnly：node 在 $nodeDir"

    $existingPnpm = Get-Command pnpm -ErrorAction SilentlyContinue
    if ($existingPnpm) {
        $v = ""
        $prevEapLocal = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try { $v = (& pnpm -v 2>&1 | Out-String).Trim() } catch { }
        $ErrorActionPreference = $prevEapLocal
        Say "pnpm 已存在（$v），无需安装。"
        Trace "PnpmOnly：pnpm 已存在 $v"
        exit 0
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    if (Test-DirWritable $nodeDir) { $candidates.Add($nodeDir) } else { Trace "node 目录不可写，跳过：$nodeDir" }
    $npmPrefix = Get-NpmGlobalPrefix $nodeDir
    if ($npmPrefix) {
        Trace "npm 全局前缀：$npmPrefix"
        if (Test-DirWritable $npmPrefix) { $candidates.Add($npmPrefix) } else { Trace "npm 前缀不可写，跳过：$npmPrefix" }
    }
    $roamingNpm = Join-Path $env:APPDATA "npm"
    if (Test-DirWritable $roamingNpm) { $candidates.Add($roamingNpm) }

    if ($candidates.Count -eq 0) {
        Fail "找不到可写目录来安装 pnpm（运行环境目录与 npm 全局前缀都不可写）。"
    }

    $landed = ""
    foreach ($cand in $candidates) {
        Say "正在安装 pnpm 到 $cand …"
        if (Install-PnpmInto $cand) { $landed = $cand; break }
    }
    if (-not $landed) {
        Say "提示：pnpm 没能自动装上（离线或环境不支持）。引擎照常可用，但安装桌面版插件会失败。"
        Trace "PnpmOnly 失败：所有落点都没装成"
        exit 1
    }

    Add-ToUserPath $landed
    $pnpmExe = Join-Path $landed "pnpm.cmd"
    $pnpmVer = "未找到"
    $prevEapLocal = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $pnpmVer = (& $pnpmExe -v 2>&1 | Out-String).Trim() } catch { }
    $ErrorActionPreference = $prevEapLocal
    Say ""
    Say "pnpm 已装好：$pnpmVer（$landed）"
    Say "现在可以回到程序，点「安装桌面版插件」继续。"
    Trace "PnpmOnly 完成：pnpm $pnpmVer → $landed"
    exit 0
}

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
            Hint-GitOptional
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

# ③-b 校验（2026-10-16 审计 H1）：对照 SHASUMS256.txt。优先取官方 nodejs.org 的清单 ——
#      即使压缩包来自镜像，官方清单也能证明字节没被换；官方不可达时退到镜像清单（只能防损坏）。
#      解压后还会再验 node.exe 的数字签名（官方构建由 OpenJS Foundation 签名），两道一起才放行。
$expected = $null
$sumSource = ""
foreach ($sumUrl in @("https://nodejs.org/dist/$version/SHASUMS256.txt",
                      "https://npmmirror.com/mirrors/node/$version/SHASUMS256.txt")) {
    try {
        $sums = (Invoke-WebRequest -Uri $sumUrl -TimeoutSec 30 -UseBasicParsing).Content
        if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
        foreach ($l in ($sums -split "\r?\n")) {
            if ($l -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($zipName))\s*$") { $expected = $Matches[1].ToLower(); break }
        }
        if ($expected) { $sumSource = $sumUrl; break }
    } catch { }
}
if (-not $expected) { Fail "取不到 Node.js 的官方校验清单（SHASUMS256.txt），无法确认下载内容，已停止安装。" }
$actual = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLower()
if ($actual -ne $expected) {
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Fail "Node.js 压缩包校验失败（SHA-256 与 $sumSource 不一致），已删除。"
}
Trace "SHA-256 校验通过（$sumSource）"

# ④ 解压到目标目录
try {
    Say "正在解压…"
    Expand-Archive -Path $zipPath -DestinationPath $temp -Force
    $inner = Get-ChildItem $temp -Directory | Where-Object { $_.Name -like "node-*-win-x64" } | Select-Object -First 1
    if (-not $inner) { Fail "下载的压缩包结构不符合预期。" }
    # 签名关：官方 node.exe 带 Authenticode 签名；没有或不通过 ⇒ 不装
    $sig = Get-AuthenticodeSignature -FilePath (Join-Path $inner.FullName "node.exe")
    if ($sig.Status -ne 'Valid') { Fail "node.exe 的数字签名校验未通过（$($sig.Status)），已停止安装。" }
    Trace ("node.exe 签名有效：{0}" -f $sig.SignerCertificate.Subject)
    # 只清理"看得出是旧 Node 安装"的目录；-TargetDir 可以指向任意位置，绝不递归删除别的东西
    if (Test-Path $target) {
        $isNodeDir = Test-Path (Join-Path $target "node.exe")
        $isEmpty = -not (Get-ChildItem -Force $target | Select-Object -First 1)
        if (-not ($isNodeDir -or $isEmpty)) { Fail "目标目录已存在且不是 Node.js 安装目录，为免误删已停止：$target" }
        Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    }
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
            Notify-EnvironmentChanged
        }
    } catch {
        Say "提示：写入用户 PATH 失败（不影响使用，程序会直接找安装目录）。"
    }
}

# ⑤-b 一起把 pnpm 装进同一个目录
# 为什么必须在这儿装：DSH 桌面版插件是在 profile 目录里直接调 PATH 上的 pnpm 的，
# 目标机只有 Node 没有 pnpm 时，点「安装桌面版插件」是确定性失败。
# 注意（实测）：Node.js v26 的官方 zip 里已经**不再带 corepack.cmd**，
# 所以 corepack 那条分支在新装机器上基本不会命中，npm 分支才是主路；
# 保留 corepack 分支是为了照顾旧版本 Node 或系统里另装的 corepack。
# shim 为什么必须落进 $target：corepack 生成的 pnpm.cmd 里没有任何绝对路径，
# 它靠 "%~dp0\..\..\..\..\..\..\Program Files\nodejs\node_modules\corepack\dist\pnpm.js"
# 这样的相对路径回跳去找 node —— 只有落在 node 目录里才会命中 "%~dp0\node.exe" 那个分支。
# 两条路都会往 stderr 打东西（corepack 打 Downloading…，npm 打 warn install-scripts），
# 退出码却都是 0 ⇒ 判成功只看 pnpm.cmd 在不在、拿不拿得到版本号，绝不以「有输出」判失败。
$pnpmCmd = Join-Path $target "pnpm.cmd"
if (-not (Test-Path $pnpmCmd)) {
    Say "正在安装 pnpm…"
    # 原生程序的 stderr 在 $ErrorActionPreference='Stop' 下会被当成异常抛出，这里临时放开
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $corepackCmd = Join-Path $target "corepack.cmd"
    if (Test-Path $corepackCmd) {
        try {
            Trace "尝试用 corepack 安装 pnpm"
            $out = & $corepackCmd enable pnpm --install-directory $target 2>&1 | Out-String
            Trace ("corepack enable pnpm 退出码：{0}" -f $LASTEXITCODE)
            if ($out -and $out.Trim()) { Trace ("corepack 输出：{0}" -f $out.Trim()) }
        } catch {
            Trace ("corepack 方式失败：{0}" -f $_.Exception.Message)
        }
    } else {
        Trace "node 目录里没有 corepack.cmd，跳过 corepack 方式"
    }
    if (-not (Test-Path $pnpmCmd)) {
        $npmCmd = Join-Path $target "npm.cmd"
        if (Test-Path $npmCmd) {
            try {
                Trace "尝试用 npm 安装 pnpm"
                $out = & $npmCmd install -g pnpm --prefix $target --no-fund --no-audit 2>&1 | Out-String
                Trace ("npm install -g pnpm 退出码：{0}" -f $LASTEXITCODE)
                if ($out -and $out.Trim()) { Trace ("npm 输出：{0}" -f $out.Trim()) }
            } catch {
                Trace ("npm 方式失败：{0}" -f $_.Exception.Message)
            }
        } else {
            Trace "node 目录里没有 npm.cmd，跳过 npm 方式"
        }
    }
    $ErrorActionPreference = $prevEap
    if (-not (Test-Path $pnpmCmd)) {
        # 不 Fail：Node 装好了引擎就能跑，缺的只是桌面版插件那条路，降级放行并说清楚
        Say "提示：pnpm 没能自动装上（离线或环境不支持）。引擎照常可用，但安装桌面版插件会失败。"
        Trace "pnpm 自举失败，已降级放行"
    }
}

# ⑥ 已并入 Notify-EnvironmentChanged 函数（见文件前部）：两条写 PATH 的路径写成功才广播。

# ⑦ 自检
try {
    $nodeExe = Join-Path $target "node.exe"
    $npxCmd = Join-Path $target "npx.cmd"
    if (-not (Test-Path $nodeExe)) { Fail "安装目录里没有找到 node.exe。" }
    $nodeVer = & $nodeExe -v
    $npxVer = if (Test-Path $npxCmd) { & $npxCmd -v } else { "未找到" }
    $pnpmVer = "未找到"
    if (Test-Path $pnpmCmd) {
        try {
            $prevEap2 = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            $pnpmVer = (& $pnpmCmd -v 2>&1 | Out-String).Trim()
            $ErrorActionPreference = $prevEap2
        } catch { $pnpmVer = "未找到" }
    }
    Say ""
    Say "安装完成：Node.js $nodeVer（npx $npxVer，pnpm $pnpmVer）"
    Say "安装位置：$target"
    Say "现在可以回到程序，点「一键启动引擎」开始使用。"
    Trace "完成：Node.js $nodeVer / npx $npxVer / pnpm $pnpmVer → $target"
    Hint-GitOptional
    exit 0
} catch {
    Fail "自检失败：$($_.Exception.Message)"
}
