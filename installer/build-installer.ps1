# DSH 守护壳 安装包构建脚本
# 用法：pwsh -File installer\build-installer.ps1 [-Version 2.5] [-SkipPublish] [-SkipSelfTest] [-AllowDirty]
# 产物：dist\DSHGuard-Setup-<版本>.exe（自带 unins000.exe）

param(
    [string]$Version = "",
    [switch]$SkipPublish,
    [switch]$SkipSelfTest,
    # 保留**现有** payload 目录里的字节，不重新收集。
    # 给签名链专用：sign-release.ps1 签完 payload 后要重打 setup，
    # 若此时重新收集，刚签好的字节会被未签名的 publish 产物覆盖 ⇒ 签名丢失
    # （而外面套着的 setup 签名状态仍是 Valid，表面看不出来）。
    [switch]$KeepPayload,
    # 允许在**脏工作区**（有未提交改动）打包。
    # 默认禁止：脏树打出来的包，产物里的版本串指向的是一个不含本次改动的旧提交 ⇒ 装进机器的
    # 与跑过自检的对不上号（这正是 2.3.1 之前的老毛病）。确实需要临时包时才显式加这个开关。
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Push-Location $root
try {
    # ── 版本号：唯一出处是 GuardVersion.cs（2.0.0 起格式为 Major.Minor.Patch） ──
    if (-not $Version) {
        $gv = Get-Content "GuardVersion.cs" -Raw -Encoding UTF8
        $mMajor = [regex]::Match($gv, 'Major\s*=\s*(\d+)')
        $mMinor = [regex]::Match($gv, 'Minor\s*=\s*(\d+)')
        $mPatch = [regex]::Match($gv, 'Patch\s*=\s*(\d+)')
        if (-not $mMinor.Success -or -not $mPatch.Success) { throw "无法从 GuardVersion.cs 读出 Minor/Patch" }
        $major = if ($mMajor.Success) { [int]$mMajor.Groups[1].Value } else { 1 }
        $minor = [int]$mMinor.Groups[1].Value
        $patch = [int]$mPatch.Groups[1].Value
        # 与 GuardVersion.VersionFor 保持一致：2.x 起始终显示三段，1.x 保持旧口径
        if ($major -ge 2) {
            $Version = "$major.$minor.$patch"
        } else {
            $Version = if ($patch -le 0) { "1.$minor" } else { "1.$minor.$patch" }
        }
    }
    Write-Host "版本号：$Version" -ForegroundColor Cyan

    # ── 0a. 提交标识（2.3.2）：产物要能自证来源 ──
    #   取当前 HEAD 的短 hash，写进 setup 的 VersionInfoVersion 与 SHA256SUMS 记录。
    #   为什么必须取**提交**而不是版本号：版本号只说「这是 2.3.2」，提交才说「是哪个 2.3.2」。
    $commit = ""
    try { $commit = (git rev-parse --short=12 HEAD 2>$null).Trim() } catch { }
    if (-not $commit) { throw "取不到 git 提交号（不是 git 仓库，或 git 不在 PATH）" }
    Write-Host "提交：$commit" -ForegroundColor Cyan

    # ── 0b. 脏工作区检查（2.3.2）：脏树不许打包 ──
    #   产物里的版本串是构建时写进去的提交号；脏树上打包，那个提交号**不含**本次改动，
    #   于是「装进机器的」与「跑过自检的」不是同一个二进制，且表面看不出差别。
    if (-not $AllowDirty) {
        $dirty = @(git status --porcelain 2>$null)
        # 忽略打包自己产生的中间物（payload 与 dist 都在 .gitignore 里，正常不会出现在这里）
        $realDirty = @($dirty | Where-Object { $_ -notmatch 'installer/payload' -and $_ -notmatch 'dist/' })
        if ($realDirty.Count -gt 0) {
            Write-Host ""
            Write-Host "工作区有未提交改动（$($realDirty.Count) 项），打包已中止：" -ForegroundColor Red
            $realDirty | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
            if ($realDirty.Count -gt 10) { Write-Host "  …（其余 $($realDirty.Count - 10) 项略）" -ForegroundColor DarkYellow }
            Write-Host ""
            Write-Host "要么先提交（提交后包的版本串才对得上），要么显式加 -AllowDirty 强制打。" -ForegroundColor Yellow
            throw "工作区不干净，已中止打包"
        }
    }
    else {
        Write-Host "⚠ -AllowDirty：允许在脏工作区打包（产物版本串将指向一个不含本次改动的提交）" -ForegroundColor Yellow
    }

    # ── 0. 双轨目标卫生检查（2.0.0）：插件写命令必须走目标分发层，不许新增"默认 Web"的写法 ──
    & powershell -NoProfile -ExecutionPolicy Bypass -File "tools\check-target-hygiene.ps1" -Root $root | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "目标卫生检查未通过（见上方清单），已中止打包" }

    # ── 1. 发布单文件 ──
    $publishDir = "bin\Release\net10.0-windows\win-x64\publish"
    if (-not $SkipPublish) {
        Write-Host "正在发布…" -ForegroundColor Cyan
        dotnet publish -c Release --nologo /p:Version=$Version.0 /p:SourceRevisionId=$commit | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }
    }
    if (-not (Test-Path "$publishDir\DSHGuard.exe")) { throw "找不到发布产物：$publishDir\DSHGuard.exe" }

    # ── 2. 收集 payload（白名单：只带程序必需文件，不带 Cache/Logs/Config/Snapshots） ──
    $payload = "installer\payload"
    if ($KeepPayload) {
        # 签名链专用：保留现有 payload（含已签名的那份字节），只校验它在位。
        if (-not (Test-Path (Join-Path $payload "DSHGuard.exe"))) {
            throw "-KeepPayload：payload 目录里没有 DSHGuard.exe（先正常打包一次，或去掉这个开关）"
        }
        Write-Host "payload：保留现有字节（签名链专用，不重新收集）" -ForegroundColor Cyan
    }
    else {
        if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
        New-Item -ItemType Directory -Path "$payload\Tools" -Force | Out-Null

        Copy-Item "$publishDir\DSHGuard.exe" "$payload\DSHGuard.exe" -Force
        # install-desktop.ps1 由 DSHGuard.iss 的 [Run]「安装官方 DSH 桌面版」调用；漏带时那一项会静默失败（审计 M）
        foreach ($f in @("clean-logs.ps1", "port-check.ps1", "check-plugin-updates.ps1", "install-node.ps1", "install-desktop.ps1")) {
            $src = "$publishDir\Tools\$f"
            if (-not (Test-Path $src)) { throw "缺少脚本：$src（检查 csproj 的 tools 复制规则）" }
            Copy-Item $src "$payload\Tools\$f" -Force
        }

        # 图标：与主程序同一个 .ico（安装程序与卸载器都用它，保证 exe/unins000 图标一致）
        $ico = Join-Path $PSScriptRoot "..\Assets\whale-girl.ico"
        if (-not (Test-Path $ico)) { throw "缺少图标：$ico" }
        Copy-Item $ico "$payload\whale-girl.ico" -Force
    }

    # 载荷内容校验（2.3.2）：Tools 里必须**恰好**是那 5 个运行时脚本。
    #   少一个 ⇒ 用户机器上少一个功能（install-desktop 缺失会让「装官方桌面版」静默失败）；
    #   多一个 ⇒ 把发布期脚本（sign-release / check-target-hygiene）塞进了用户机器。
    $expectedTools = @("clean-logs.ps1", "port-check.ps1", "check-plugin-updates.ps1", "install-node.ps1", "install-desktop.ps1")
    $actualTools = @(Get-ChildItem "$payload\Tools" -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    $missing = @($expectedTools | Where-Object { $actualTools -notcontains $_ })
    $extra = @($actualTools | Where-Object { $expectedTools -notcontains $_ })
    if ($missing.Count -gt 0) { throw "payload\Tools 缺少脚本：$($missing -join ', ')" }
    if ($extra.Count -gt 0) { throw "payload\Tools 多了脚本（发布期脚本不该进用户机器）：$($extra -join ', ')" }
    Write-Host "payload\Tools：$($actualTools.Count) 个脚本，齐全且无多余" -ForegroundColor Cyan

    $exeMb = [math]::Round((Get-Item "$payload\DSHGuard.exe").Length / 1MB, 1)
    Write-Host "payload：程序 $exeMb MB（动画素材已移除，安装包只剩程序本体）" -ForegroundColor Cyan

    # ── 2b. 自检门禁（2.3.2）：--selftest 接进发布链 ──
    #   为什么放这里而不是事后手敲：发布链此前**只到「打出一个包」**，不到「这个包里的二进制跑过自检」。
    #   在这里跑一次，且跑的是**将要装进机器的那个字节**（payload\DSHGuard.exe），才谈得上自证。
    #   ⚠ 自检是 GUI 子系统：必须把 stdout 重定向到文件，否则没有输出（不是「没跑」）。
    #   ⚠ 输出是 GBK，且行首带序号，形如 "[1063] PASS 版本 1.5 · ..." ⇒ 解析要认这个序号。
    if (-not $SkipSelfTest) {
        Write-Host ""
        Write-Host "正在跑自检（payload 那个字节）…" -ForegroundColor Cyan
        $selftestOut = Join-Path $env:TEMP "dshguard-selftest-build.stdout"
        Remove-Item $selftestOut -ErrorAction SilentlyContinue
        $selftestErr = Join-Path $env:TEMP "dshguard-selftest-build.stderr"
        Remove-Item $selftestErr -ErrorAction SilentlyContinue
        $payloadExePath = Join-Path $root "installer\payload\DSHGuard.exe"
        # Start-Process + 重定向：GUI 子系统的 stdout 只有被重定向到文件才拿得到
        $proc = Start-Process -FilePath $payloadExePath -ArgumentList "--selftest" `
                    -Wait -PassThru -NoNewWindow `
                    -RedirectStandardOutput $selftestOut -RedirectStandardError $selftestErr
        $selftestExit = $proc.ExitCode
        $selftestText = ""
        if (Test-Path $selftestOut) { $selftestText = Get-Content $selftestOut -Raw -Encoding Default }
        # 统计形如 "[1063] PASS …" / "FAIL …" / "SKIP …" 的行
        $passCount = ([regex]::Matches($selftestText, '(?m)^\[\d+\]\s+PASS\b')).Count
        $failCount = ([regex]::Matches($selftestText, '(?m)^\[\d+\]\s+FAIL\b')).Count
        $skipCount = ([regex]::Matches($selftestText, '(?m)^\[\d+\]\s+SKIP\b')).Count
        Write-Host ("  退出码=$selftestExit  PASS=$passCount  FAIL=$failCount  SKIP=$skipCount") -ForegroundColor $(if ($failCount -gt 0 -or $selftestExit -ne 0) { "Red" } else { "Green" })
        # ⚠ 三种失败都要拦住，缺一不可：
        #   ① 退出码非 0；② 有 FAIL 行；③ **一条 PASS 都没解析到** ⇒ 说明根本没跑起来
        #     （GUI 无 stdout / 输出格式变了 / 进程被秒退），此时若放行就是「假绿」。
        if ($selftestExit -ne 0 -or $failCount -gt 0 -or $passCount -eq 0) {
            Write-Host "  自检未过（期望 EXIT=0 / FAIL=0 / PASS>0）。输出：$selftestOut" -ForegroundColor Red
            if ($failCount -gt 0) {
                $fl = ($selftestText -split "`r?`n") | Where-Object { $_ -match '^\[\d+\]\s+FAIL' } | Select-Object -First 20
                if ($fl) { $fl | ForEach-Object { Write-Host "    $_" -ForegroundColor Red } }
            }
            if ($passCount -eq 0) {
                Write-Host "    （一条 PASS 都没解析到 ⇒ 自检没真跑起来，不能放行）" -ForegroundColor Red
                if ($selftestText) {
                    $head = ($selftestText -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 5
                    if ($head) { $head | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }
                }
            }
            throw "自检未通过，已中止打包"
        }
    }
    else {
        Write-Host "⚠ -SkipSelfTest：跳过了自检门禁（这个包不能声称「跑过自检」）" -ForegroundColor Yellow
    }

    # ── 3. 调 ISCC ──
    # 探测顺序：用户目录 7/6 → 机器级 x86 7/6 → 机器级 7/6。
    # 2026-10-27 实机：本机装的是 **Inno Setup 7**（C:\Program Files (x86)\Inno Setup 7），
    # 原脚本只认 6，于是打包步骤直接抛"未找到 ISCC"。补上 7 的路径（7 向后兼容 6 的 iss）。
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 7\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "未找到 ISCC.exe：请先安装 Inno Setup 6 或 7（winget install -e --id JRSoftware.InnoSetup --scope user）" }

    Write-Host "正在打包…" -ForegroundColor Cyan
    # AppVersion 带提交号 ⇒ 安装包的 FileVersion / ProductVersion 能指回是哪个提交打的。
    # Inno 的 VersionInfoVersion 必须是「点分数字」形式（否则 Windows 资源里存不进去），
    # 所以提交号单独走 AppVersion 之外的一条：这里用四段版本 + 提交号拼成合法数字串。
    $versionInfo = "$($Version -replace '[^0-9.]', '').0"
    & $iscc "/DAppVersion=$Version" "/DVersionInfoVersion=$versionInfo" "/DCommitId=$commit" "installer\DSHGuard.iss" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "ISCC 打包失败（退出码 $LASTEXITCODE）" }

    # ── 4. 产物与校验和 ──
    $setup = Get-ChildItem "dist\DSHGuard-Setup-$Version.exe" -ErrorAction SilentlyContinue
    if (-not $setup) { throw "没找到安装包产物" }
    # 审计修正（2026-10-19）：部分 Windows PowerShell 5.1 环境里没有 Get-FileHash 这个 cmdlet
    #   （精简镜像缺 Microsoft.PowerShell.Utility 的部分实现）。⚠ 这类"命令不存在"错误属于
    #   **命令解析期**，-ErrorAction SilentlyContinue 压不住（实测仍抛 CommandNotFoundException），
    #   必须先用 Get-Command 探测；否则打包脚本会在产物已经打好之后才报错退出，白跑一轮。
    function Get-Sha256([string]$path) {
        if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) {
            return (Get-FileHash $path -Algorithm SHA256).Hash
        }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $fs = [System.IO.File]::OpenRead($path)
            try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '') }
            finally { $fs.Dispose() }
        }
        finally { $sha.Dispose() }
    }
    $hash = Get-Sha256 $setup.FullName
    $payloadExe = Join-Path $root "installer\payload\DSHGuard.exe"
    $payloadHash = Get-Sha256 $payloadExe

    # ── 4b. 哈希落盘（2.3.2）：双哈希 + 提交号写进 SHA256SUMS-<ver>.txt ──
    #   为什么必须落盘：此前哈希只打到控制台就散了，谁也没法在事后核对「装进机器的是哪个字节」。
    #   现在这个文件同时记 payload 与 setup 两个哈希 + 提交号 + 自检结论 ⇒
    #   第三方拿到安装包后，可以验证「包里的 payload 哈希 == 记录里的 payload 哈希」。
    $sums = Join-Path $root "dist\SHA256SUMS-$Version.txt"
    # 注释一律英文：这份文件用 ASCII 落盘（见下方编码说明），中文会变成一串 "?"。
    $selftestNote = if ($SkipSelfTest) { "SKIPPED (-SkipSelfTest)" } else { "PASSED (EXIT=0 / FAIL=0 / PASS=$passCount / SKIP=$skipCount)" }
    $lines = @(
        "$hash  $($setup.Name)",
        "$payloadHash  DSHGuard-$Version-payload.exe",
        "",
        "# version $Version / commit $commit / built $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "# payload is the DSHGuard.exe embedded in this installer ($([math]::Round((Get-Item $payloadExe).Length / 1MB, 1)) MB)",
        "# selftest gate: $selftestNote",
        "# code signing: unsigned builds trigger SmartScreen (need 'More info -> Run anyway')"
    )
    # ⚠ 编码用 ASCII 而不是 UTF8：Windows PowerShell 5.1 的 Out-File -Encoding UTF8 会写 BOM，
    #   而带 BOM 的第一行会让 `sha256sum -c` 认不出哈希（既有 SHA256SUMS-2.3.0.txt 就是无 BOM 的）。
    #   这份文件里全是 ASCII ⇒ 注释也必须写英文，写中文会变成一串 "?"。
    #   既有惯例见 dist\SHA256SUMS-2.3.0.txt（无 BOM、纯 ASCII）。
    $lines | Out-File -FilePath $sums -Encoding ASCII
    Write-Host "   校验记录：$sums" -ForegroundColor Green

    # 自校验（2.3.2）：刚写下的记录必须与磁盘上的实际字节吻合，否则这份记录本身就是谎话。
    $readBack = @(Get-Content $sums | Where-Object { $_ -match '^[0-9A-Fa-f]{64}\s' })
    if ($readBack.Count -ne 2) { throw "校验记录格式不对（应有 2 行哈希，实际 $($readBack.Count) 行）" }
    foreach ($line in $readBack) {
        $parts = $line -split '\s+', 2
        $expect = $parts[0]; $name = $parts[1].Trim()
        $target = if ($name -like '*payload*') { $payloadExe } else { $setup.FullName }
        $actual = Get-Sha256 $target
        if ($actual -ne $expect) { throw "校验记录与实际字节不符：$name（记录 $expect / 实际 $actual）" }
    }
    Write-Host "   ✅ 校验记录已自校验：两个哈希与磁盘字节逐字节吻合" -ForegroundColor Green

    Write-Host ""
    Write-Host "✅ 安装包：$($setup.FullName)" -ForegroundColor Green
    Write-Host "   大小：$([math]::Round($setup.Length / 1MB, 1)) MB" -ForegroundColor Green
    Write-Host "   SHA256：$hash" -ForegroundColor Green
    Write-Host "   payload SHA256：$payloadHash" -ForegroundColor Green
    Write-Host "   提交：$commit" -ForegroundColor Green
    Write-Host "   卸载器：装完后由 Inno 生成 unins000.exe（含「选择要删除的数据」页）" -ForegroundColor Green
}
finally {
    Pop-Location
}
