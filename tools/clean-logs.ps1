<#
.SYNOPSIS
    清理 DSHGuard 日志（规则与程序内置的启动清理完全一致）。

.DESCRIPTION
    ① 删除最后修改时间超过 -MaxDays 天的日志文件；
    ② 若剩余文件仍超过 -MaxFiles 个，从最旧的继续删。

    日志只在**出错时**产生（正常启动不落盘），因此通常无需清理。
    可手动运行，也可挂计划任务：
        schtasks /create /tn "DSHGuard 清理日志" /tr "powershell -NoProfile -ExecutionPolicy Bypass -File \"<安装目录>\tools\clean-logs.ps1\"" /sc weekly /d SUN /st 03:00

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\clean-logs.ps1
    powershell -ExecutionPolicy Bypass -File tools\clean-logs.ps1 -DryRun
    powershell -ExecutionPolicy Bypass -File tools\clean-logs.ps1 -MaxDays 7 -MaxFiles 20
#>
param(
    [string]$LogDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Logs'),
    [int]$MaxDays = 15,
    [int]$MaxFiles = 50,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogDir)) {
    Write-Host "日志目录不存在: $LogDir"
    exit 0
}

$prefix = if ($DryRun) { '[DryRun] ' } else { '' }
$cutoff = (Get-Date).AddDays(-$MaxDays)

# 全部日志，新 → 旧
$files = @(Get-ChildItem -LiteralPath $LogDir -Filter '*.log' -File |
           Sort-Object LastWriteTime -Descending)

$removedCount = 0
$kept = New-Object System.Collections.ArrayList

# ① 超龄
foreach ($f in $files) {
    if ($f.LastWriteTime -lt $cutoff) {
        if (-not $DryRun) {
            Remove-Item -LiteralPath $f.FullName -Force
            Write-Host "  [超龄] $($f.Name)   $($f.LastWriteTime)"
        }
        $removedCount++
    } else {
        [void]$kept.Add($f)
    }
}

# ② 超额：kept 中保留最新 $MaxFiles 个，其余从最旧的删
if ($kept.Count -gt $MaxFiles) {
    foreach ($f in @($kept | Select-Object -Skip $MaxFiles)) {
        if (-not $DryRun) {
            Remove-Item -LiteralPath $f.FullName -Force
            Write-Host "  [超额] $($f.Name)   $($f.LastWriteTime)"
        }
        $removedCount++
    }
}

$left = @(Get-ChildItem -LiteralPath $LogDir -Filter '*.log' -File).Count

if ($removedCount -eq 0) {
    Write-Host "$prefix无需清理。当前 $left 个日志（上限：$MaxFiles 个 / $MaxDays 天）"
} else {
    Write-Host "$prefix已清理 $removedCount 个日志，剩余 $left 个（规则：>$MaxDays 天 或 >$MaxFiles 个）"
}
