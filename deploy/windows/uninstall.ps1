<#
.SYNOPSIS
    卸载 Apiext MediaKit Windows 部署（服务 / 计划任务 / 防火墙规则）。

.DESCRIPTION
    默认只卸载「服务 + 计划任务 + 防火墙规则」，程序与数据目录保留（便于升级/排障）。
    加 -RemoveData 才会删除安装目录（含 APP_DATA 数据库与录像，请先备份！）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveData
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\apiext-mediakit",
    [switch]$RemoveData,
    [switch]$KeepFirewall
)

$ErrorActionPreference = "Stop"

$AppServiceName = "Apiext-MediaKit-App"
$ZlmServiceName = "Apiext-MediaKit-ZLM"
$ZlmTaskName = "Apiext-MediaKit-ZLM"
$FwTcpName = "Apiext-MediaKit-TCP"
$FwUdpName = "Apiext-MediaKit-UDP"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请用「管理员身份」运行 PowerShell 后再执行本脚本。"
}

# ── 原生命令包装（关键）─────────────────────────────────────────
# Windows PowerShell 5.1 在 $ErrorActionPreference='Stop' 下，会把原生命令写到 stderr 的
# 「正常输出」升级成 NativeCommandError 并终止整个脚本，即使加 2>$null 也拦不住。
# 典型：schtasks /Query 查一个不存在的计划任务 → 「错误: 系统找不到指定的文件。」→ 卸载中断。
# 这里临时降到 Continue、把 stderr 并入输出流后丢弃，只返回退出码。
function Invoke-Native([string]$exe, [string[]]$argv) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @argv 2>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev }
}

Write-Host "==> 停止并删除服务" -ForegroundColor Cyan

foreach ($name in @($AppServiceName, $ZlmServiceName)) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -ne 'Stopped') {
            Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
        Invoke-Native 'sc.exe' @('delete', $name) | Out-Null
        Write-Host "    已删除服务 $name"
    }
    else {
        Write-Host "    服务 $name 不存在，跳过"
    }
}

# NSSM 注册的 ZLM 也被上面包含（服务名相同）；若用计划任务则这里清理
$taskExists = (Invoke-Native 'schtasks.exe' @('/Query', '/TN', $ZlmTaskName)) -eq 0
if ($taskExists) {
    Invoke-Native 'schtasks.exe' @('/End', '/TN', $ZlmTaskName) | Out-Null
    Invoke-Native 'schtasks.exe' @('/Delete', '/TN', $ZlmTaskName, '/F') | Out-Null
    Write-Host "    已删除计划任务 $ZlmTaskName"
}
else {
    Write-Host "    计划任务 $ZlmTaskName 不存在，跳过"
}

# 兜底：强制结束残留进程（服务删除后进程偶发残留，会占用端口）
foreach ($procName in @("Apiext.MediaKit.Server", "MediaServer")) {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "    结束残留进程 $($_.ProcessName) (PID $($_.Id))"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}

if (-not $KeepFirewall) {
    Write-Host "==> 删除防火墙规则" -ForegroundColor Cyan
    foreach ($n in @($FwTcpName, $FwUdpName)) {
        $rule = Get-NetFirewallRule -DisplayName $n -ErrorAction SilentlyContinue
        if ($rule) {
            $rule | Remove-NetFirewallRule
            Write-Host "    已删除规则 $n"
        }
    }
}

if ($RemoveData) {
    if (-not (Test-Path $InstallDir)) {
        Write-Host "    安装目录 $InstallDir 不存在，跳过"
    }
    else {
        Write-Host "==> 删除安装目录 $InstallDir（含数据库与录像）" -ForegroundColor Yellow
        $answer = Read-Host "    不可恢复，确认删除？(yes/N)"
        if ($answer -eq "yes") {
            Remove-Item $InstallDir -Recurse -Force
            Write-Host "    已删除"
        }
        else {
            Write-Host "    已取消删除目录"
        }
    }
}
else {
    Write-Host "==> 程序与数据保留在 $InstallDir（如需彻底删除请加 -RemoveData）" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "卸载完成" -ForegroundColor Green
