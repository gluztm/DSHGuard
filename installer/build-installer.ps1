# DSH 守护壳 安装包构建脚本
# 用法：pwsh -File installer\build-installer.ps1 [-Version 2.5] [-SkipPublish]
# 产物：dist\DSHGuard-Setup-<版本>.exe（自带 unins000.exe）

param(
    [string]$Version = "",
    [switch]$SkipPublish
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

    # ── 0. 双轨目标卫生检查（2.0.0）：插件写命令必须走目标分发层，不许新增"默认 Web"的写法 ──
    & powershell -NoProfile -ExecutionPolicy Bypass -File "tools\check-target-hygiene.ps1" -Root $root | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "目标卫生检查未通过（见上方清单），已中止打包" }

    # ── 1. 发布单文件 ──
    $publishDir = "bin\Release\net10.0-windows\win-x64\publish"
    if (-not $SkipPublish) {
        Write-Host "正在发布…" -ForegroundColor Cyan
        dotnet publish -c Release --nologo /p:Version=$Version.0 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }
    }
    if (-not (Test-Path "$publishDir\DSHGuard.exe")) { throw "找不到发布产物：$publishDir\DSHGuard.exe" }

    # ── 2. 收集 payload（白名单：只带程序必需文件，不带 Cache/Logs/Config/Snapshots） ──
    $payload = "installer\payload"
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

    $exeMb = [math]::Round((Get-Item "$payload\DSHGuard.exe").Length / 1MB, 1)
    Write-Host "payload：程序 $exeMb MB（动画素材已移除，安装包只剩程序本体）" -ForegroundColor Cyan

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
    & $iscc "/DAppVersion=$Version" "installer\DSHGuard.iss" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "ISCC 打包失败（退出码 $LASTEXITCODE）" }

    # ── 4. 产物与校验和 ──
    $setup = Get-ChildItem "dist\DSHGuard-Setup-$Version.exe" -ErrorAction SilentlyContinue
    if (-not $setup) { throw "没找到安装包产物" }
    # 审计修正（2026-10-19）：部分 Windows PowerShell 5.1 环境里没有 Get-FileHash 这个 cmdlet
    #   （精简镜像缺 Microsoft.PowerShell.Utility 的部分实现）。⚠ 这类"命令不存在"错误属于
    #   **命令解析期**，-ErrorAction SilentlyContinue 压不住（实测仍抛 CommandNotFoundException），
    #   必须先用 Get-Command 探测；否则打包脚本会在产物已经打好之后才报错退出，白跑一轮。
    if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) {
        $hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash
    }
    else {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $fs = [System.IO.File]::OpenRead($setup.FullName)
            try { $hash = ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '') }
            finally { $fs.Dispose() }
        }
        finally { $sha.Dispose() }
    }
    Write-Host ""
    Write-Host "✅ 安装包：$($setup.FullName)" -ForegroundColor Green
    Write-Host "   大小：$([math]::Round($setup.Length / 1MB, 1)) MB" -ForegroundColor Green
    Write-Host "   SHA256：$hash" -ForegroundColor Green
    Write-Host "   卸载器：装完后由 Inno 生成 unins000.exe（含「选择要删除的数据」页）" -ForegroundColor Green
}
finally {
    Pop-Location
}
