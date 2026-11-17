<#
.SYNOPSIS
    用 NSIS 编译 Windows 安装包（Apiext-MediaKit-Setup-<版本>.exe）。

.DESCRIPTION
    前置：deploy/windows 下已具备 app\、zlm\、ffmpeg\、nssm.exe（缺一不可）。
    NSIS 由 scoop 安装（scoop install nsis）或官网安装，脚本会用 PATH 上的 makensis。
    版本号未指定时自动取当前时间 yyMMdd-HHmm（Asia/Shanghai），并写入 installer.nsi 的 APP_VERSION。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\windows\build-installer.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\windows\build-installer.ps1 -Version 261117-1140
#>
[CmdletBinding()]
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$dir = $PSScriptRoot

$required = @(
    (Join-Path $dir "app\Apiext.MediaKit.Server.exe"),
    (Join-Path $dir "zlm\MediaServer.exe"),
    (Join-Path $dir "ffmpeg\ffmpeg.exe"),
    (Join-Path $dir "nssm.exe"),
    (Join-Path $dir "installer.nsi")
)

$missing = $required | Where-Object { -not (Test-Path $_) }
if ($missing) {
    Write-Host "缺少以下文件，无法打包：" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Host "提示："
    Write-Host "  1) app\      执行 deploy\windows\publish-app.ps1 生成"
    Write-Host "  2) zlm\      放入 ZLMediaKit Windows 版（MediaServer.exe、default.pem、www\record）"
    Write-Host "  3) ffmpeg\   放入 ffmpeg.exe"
    Write-Host "  4) nssm.exe  放一份 NSSM（win64）"
    throw "打包前置条件不满足"
}

$makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
if (-not $makensis) {
    throw "未找到 makensis，请先安装 NSIS：scoop install nsis（或官网下载）"
}

# 版本号：未指定时取当前时间 yyMMdd-HHmm（Asia/Shanghai，UTC+8）
if (-not $Version) {
    $Version = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString("yyMMdd-HHmm")
}
$nsi = Join-Path $dir "installer.nsi"
$nsiOriginal = [IO.File]::ReadAllText($nsi)
$nsiText = $nsiOriginal -replace '!define\s+APP_VERSION\s+"[^"]+"', ('!define APP_VERSION     "' + $Version + '"')
# installer.nsi 是 UTF-8 with BOM 且 `Unicode true`，必须保留 BOM，否则 makensis 按 ANSI 解析导致中文乱码
$nsiEncoding = New-Object System.Text.UTF8Encoding($true)
[IO.File]::WriteAllText($nsi, $nsiText, $nsiEncoding)
Write-Host "版本号: $Version"

$appSize = [math]::Round(((Get-ChildItem (Join-Path $dir "app") -Recurse -File | Measure-Object -Sum Length).Sum / 1MB), 1)
Write-Host "makensis: $makensis"
Write-Host "载荷体积: app $appSize MB + zlm/ffmpeg/nssm，开始编译（LZMA 压缩较慢，约 2-5 分钟）..."
Write-Host ""

Push-Location $dir
try {
    & $makensis "installer.nsi"
    if ($LASTEXITCODE -ne 0) { throw "makensis 失败（退出码 $LASTEXITCODE）" }
}
finally {
    Pop-Location
    # 编译后还原 installer.nsi，避免本地打包把模板版本号改成时间戳污染工作区
    [IO.File]::WriteAllText($nsi, $nsiOriginal, $nsiEncoding)
}

$setup = Get-ChildItem $dir -Filter "Apiext-MediaKit-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setup) {
    Write-Host ""
    Write-Host ("安装包生成：{0}（{1} MB）" -f $setup.FullName, [math]::Round($setup.Length / 1MB, 1)) -ForegroundColor Green
}
