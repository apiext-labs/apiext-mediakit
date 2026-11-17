<#
.SYNOPSIS
    生成 Windows 部署包：把 .NET 应用发布到 deploy\windows\app。

.DESCRIPTION
    在仓库根目录或任意位置执行均可（脚本按自身位置推算仓库路径）。
    默认自包含（--self-contained true），目标机器无需安装 .NET 运行时；
    如需减小体积，用 -SelfContained:$false（目标机需装 ASP.NET Core Hosting Bundle 10）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\windows\publish-app.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\windows\publish-app.ps1 -SelfContained:$false
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [switch]$SkipClean
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repoRoot "src\Apiext.MediaKit.Server\Apiext.MediaKit.Server.csproj"
$outDir = Join-Path $PSScriptRoot "app"

if (-not (Test-Path $project)) {
    throw "未找到项目文件：$project"
}

if (-not $SkipClean -and (Test-Path $outDir)) {
    Write-Host "清理旧输出 $outDir"
    Remove-Item $outDir -Recurse -Force
}

$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $outDir,
    "--self-contained", ($(if ($SelfContained) { "true" } else { "false" })),
    "--nologo"
)

Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

$exe = Join-Path $outDir "Apiext.MediaKit.Server.exe"
if (-not (Test-Path $exe)) { throw "发布输出缺少 $exe" }

$size = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object -Sum Length).Sum / 1MB), 1)
Write-Host ""
Write-Host "发布完成：$outDir（$size MB，SelfContained=$SelfContained）" -ForegroundColor Green
Write-Host "下一步："
Write-Host "  1) 把 ZLMediaKit Windows 版放到 deploy\windows\zlm（MediaServer.exe 等）"
Write-Host "  2) 把 ffmpeg.exe 放到 deploy\windows\ffmpeg（可选，'兼容'取流与抓拍需要）"
Write-Host "  3) 打包整个 deploy/windows 目录，在目标机管理员 PowerShell 执行 install.ps1 -PublicHost <IP>"
