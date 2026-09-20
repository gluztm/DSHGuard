<#
.SYNOPSIS
    DSHGuard 端口检测脚本：检查端口监听状态、列出占用进程，可选清理。

.DESCRIPTION
    守护壳与 DSH 引擎已解绑：**端口状态就是"引擎是否在跑"的唯一观测源**。
    同步规则：引擎运行 → 端口应开；引擎关闭 → 端口应关。

    本脚本默认**只读**（检测 + 报告 + 给同步建议）；只有显式加 -Fix 才会结束占用进程。
    可手动运行，也可挂计划任务定期核对。

.PARAMETER Port
    要检测的端口（默认 3080）
.PARAMETER Fix
    结束占用该端口的进程（谨慎：会杀掉正在跑的引擎）

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\port-check.ps1
    powershell -ExecutionPolicy Bypass -File tools\port-check.ps1 -Port 3080
    powershell -ExecutionPolicy Bypass -File tools\port-check.ps1 -Fix
#>
param(
    [int]$Port = 3080,
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

Write-Output "=== 端口检测 ==="
Write-Output ("端口: {0}" -f $Port)

$lines = @(netstat -ano | Select-String (":{0}\s" -f $Port) -ErrorAction SilentlyContinue)
$listening = @($lines | Where-Object { $_ -match 'LISTENING' })

if ($listening.Count -eq 0) {
    Write-Output "状态: 未监听  →  引擎未运行"
    Write-Output "同步: 引擎关闭 → 端口关闭，状态一致，无需处理"
    exit 0
}

Write-Output "状态: 正在监听  →  引擎运行中"

$pids = @()
foreach ($l in $listening) {
    $parts = ($l.Line -split '\s+') | Where-Object { $_ -ne '' }
    if ($parts.Count -ge 5) { $pids += [int]$parts[-1] }
}
$pids = @($pids | Select-Object -Unique)
Write-Output ("占用 PID: {0}" -f ($pids -join ', '))

foreach ($procId in $pids) {
    try {
        $p = Get-Process -Id $procId -ErrorAction Stop
        Write-Output ("  PID {0}  {1}  启动于 {2}" -f $p.Id, $p.ProcessName, $p.StartTime)
    } catch {
        Write-Output ("  PID {0}  （无法读取进程信息，可能需要管理员）" -f $procId)
    }
}

Write-Output ""
Write-Output "同步建议: 引擎运行 → 端口开，状态一致。"
Write-Output "  · 要停止本程序启动的引擎：使用守护壳的「停止并退出」"
Write-Output "  · 外部（终端）启动的引擎：请在原终端停止，本程序不会代为终止进程"

if ($Fix) {
    Write-Output ""
    Write-Output "-- Fix：结束上述占用进程 --"
    foreach ($procId in $pids) {
        try {
            Stop-Process -Id $procId -Force -ErrorAction Stop
            Write-Output ("  已结束 PID {0}" -f $procId)
        } catch {
            Write-Output ("  结束 PID {0} 失败: {1}" -f $procId, $_.Exception.Message)
        }
    }
}
