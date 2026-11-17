<#
.SYNOPSIS
    下载 Windows 安装包所需的第三方载荷：ZLMediaKit、ffmpeg、NSSM。

.DESCRIPTION
    产出目标（与 build-installer.ps1 的前置条件一致）：
      deploy\windows\zlm\MediaServer.exe + default.pem + www\record（空的录像目录）
      deploy\windows\ffmpeg\ffmpeg.exe
      deploy\windows\nssm.exe

    ZLMediaKit 取官方仓库 CI 产物（GitHub Actions artifact），按优先级：
      1) -ZlmArtifactUrl 直接给 zip（本地文件路径或直链）；
      2) 给了 -Token 则走 GitHub API 取最近一次成功的 windows 构建产物
         （跨仓库读 Actions 产物需要能访问该仓库的 PAT，仓库自带的 GITHUB_TOKEN 不行）；
      3) 都没有时走 nightly.link（公共仓库产物免登录直链）。

.EXAMPLE
    # 默认：nightly.link 免登录取 ZLM 最新产物 + ffmpeg + NSSM（CI 也是这条路径）
    pwsh -File deploy\windows\fetch-payload.ps1

.EXAMPLE
    # 有令牌时走 GitHub API（可获得更精确的构建记录）
    pwsh -File deploy\windows\fetch-payload.ps1 -Token $env:GITHUB_TOKEN

.EXAMPLE
    # 离线：直接用本地下载好的 zip
    pwsh -File deploy\windows\fetch-payload.ps1 -ZlmArtifactUrl .\Windows_master_2026-11-02.zip

.EXAMPLE
    # 只补 ffmpeg 与 nssm
    pwsh -File deploy\windows\fetch-payload.ps1 -SkipZlm
#>
[CmdletBinding()]
param(
    # ZLM 仓库与工作流（用于查最近一次成功构建）
    [string]$ZlmRepo = "ZLMediaKit/ZLMediaKit",
    [string]$ZlmWorkflow = "windows.yml",
    [string]$ZlmBranch = "master",
    # 产物名过滤（留空=取该次构建的第一个产物）
    [string]$ZlmArtifact = "",
    # 直接给 zip 地址，给了就不查 GitHub API
    [string]$ZlmArtifactUrl = "",
    # GitHub 令牌（下载 artifact 必需）
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
    [string]$NssmUrl = "https://nssm.cc/release/nssm-2.24.zip",
    [switch]$SkipZlm,
    [switch]$SkipFfmpeg,
    [switch]$SkipNssm,
    # 已有载荷时重新下载覆盖
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$dir = $PSScriptRoot
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("apiext-payload-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

function Write-Step([string]$text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    [OK] $text" -ForegroundColor Green }

function Save-Remote([string]$url, [string]$outFile, [string]$token) {
    $curl = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
    if ($curl) {
        $curlArgs = @("-fsSL", "--retry", "3", "--retry-delay", "3", "-o", $outFile)
        if ($token) { $curlArgs += @("-H", "Authorization: Bearer $token") }
        $curlArgs += $url
        & $curl @curlArgs
        if ($LASTEXITCODE -ne 0) { throw "下载失败（curl 退出码 $LASTEXITCODE）：$url" }
    }
    else {
        $headers = @{}
        if ($token) { $headers["Authorization"] = "Bearer $token" }
        Invoke-WebRequest -Uri $url -OutFile $outFile -Headers $headers -MaximumRedirection 10 -UseBasicParsing
    }
}

function Expand-Zip([string]$zip, [string]$dest) {
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $dest -Force
    return $dest
}

function Get-ZlmArtifactUrlViaNightlyLink {
    # 无令牌兜底：nightly.link 为公共仓库的 Actions 产物提供免登录直链，页面上列出最新的几个产物
    # 注意：nightly.link 的 URL 用「工作流名」而不是文件名，windows.yml → windows
    $wfName = $ZlmWorkflow -replace '\.ya?ml$', ''
    $page = "https://nightly.link/$ZlmRepo/workflows/$wfName/$ZlmBranch"
    Write-Host "    查询 $page"
    $html = (Invoke-WebRequest -Uri $page -UseBasicParsing -TimeoutSec 60).Content
    $pattern = "https://nightly\.link/$([regex]::Escape($ZlmRepo))/workflows/$([regex]::Escape($wfName))/$([regex]::Escape($ZlmBranch))/[^`"\s<>]+?\.zip"
    # 注意用 @() 包住：只有一个匹配时管道结果是字符串，$links[0] 会退化成取首字符
    $links = @([regex]::Matches($html, $pattern) | ForEach-Object { $_.Value } | Select-Object -Unique)
    if ($links.Count -eq 0) {
        throw "nightly.link 页面上找不到产物链接（$page）。`n" +
              "  方案一：手动下载 ZLM 的 Actions 产物 zip，用 -ZlmArtifactUrl <zip 路径>；`n" +
              "  方案二：提供 -Token（需要能读 $ZlmRepo Actions 的 PAT）后重跑。"
    }
    if ($ZlmArtifact) {
        $links = @($links | Where-Object { $_ -like "*$ZlmArtifact*" })
        if ($links.Count -eq 0) { throw "nightly.link 上没有匹配 '$ZlmArtifact' 的产物。" }
    }
    return $links[0]
}

function Get-ZlmArtifactUrlViaApi {
    if (-not $Token) {
        throw "未提供 -ZlmArtifactUrl 且没有 -Token/GITHUB_TOKEN，无法从 GitHub 取 ZLM 构建产物。`n" +
              "  方案一：登录后从 Actions 页面下载 zip，用 -ZlmArtifactUrl <zip 路径或 nightly.link 直链>；`n" +
              "  方案二：export GITHUB_TOKEN=<有 public_repo 的 PAT> 后重跑。"
    }
    $headers = @{
        Authorization          = "Bearer $Token"
        Accept                 = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    $runsUrl = "https://api.github.com/repos/$ZlmRepo/actions/workflows/$ZlmWorkflow/runs?branch=$ZlmBranch&status=success&per_page=5"
    $runs = Invoke-RestMethod -Uri $runsUrl -Headers $headers -TimeoutSec 60
    if (-not $runs.workflow_runs -or $runs.workflow_runs.Count -eq 0) {
        throw "在 $ZlmRepo 的 $ZlmWorkflow（$ZlmBranch）上找不到成功的构建记录。"
    }
    foreach ($run in $runs.workflow_runs) {
        $arts = Invoke-RestMethod -Uri "$($run.artifacts_url)?per_page=100" -Headers $headers -TimeoutSec 60
        $candidates = @($arts.artifacts | Where-Object { -not $_.expired })
        if ($ZlmArtifact) {
            $candidates = @($candidates | Where-Object { $_.name -like "*$ZlmArtifact*" })
        }
        if ($candidates.Count -gt 0) {
            $pick = $candidates[0]
            Write-Host "    选中构建：$($run.head_sha.Substring(0,8)) / $($run.created_at) / 产物 $($pick.name)"
            return $pick.archive_download_url
        }
    }
    throw "最近 5 次成功构建里没有可用产物（可能均已过期），请改用 -ZlmArtifactUrl 指定 zip。"
}

# ─────────────────────────── ZLMediaKit ───────────────────────────
if (-not $SkipZlm) {
    $zlmDir = Join-Path $dir "zlm"
    if ((Test-Path (Join-Path $zlmDir "MediaServer.exe")) -and -not $Force) {
        Write-Step "ZLMediaKit 已存在，跳过（-Force 覆盖）"
    }
    else {
        Write-Step "获取 ZLMediaKit（$ZlmRepo / $ZlmWorkflow）"
        $zip = Join-Path $tmp "zlm.zip"
        if ($ZlmArtifactUrl) {
            if (Test-Path $ZlmArtifactUrl) {
                Copy-Item -LiteralPath $ZlmArtifactUrl -Destination $zip -Force
                Write-Ok "使用本地文件 $ZlmArtifactUrl"
            }
            else {
                Save-Remote -url $ZlmArtifactUrl -outFile $zip -token $null
                Write-Ok "下载 $ZlmArtifactUrl"
            }
        }
        elseif ($Token) {
            $apiUrl = Get-ZlmArtifactUrlViaApi
            Save-Remote -url $apiUrl -outFile $zip -token $Token
            Write-Ok "下载 GitHub artifact（令牌）"
        }
        else {
            $nightlyUrl = Get-ZlmArtifactUrlViaNightlyLink
            Save-Remote -url $nightlyUrl -outFile $zip -token $null
            Write-Ok "下载 $nightlyUrl"
        }

        $unpack = Expand-Zip -zip $zip -dest (Join-Path $tmp "zlm")
        $server = Get-ChildItem $unpack -Filter "MediaServer.exe" -Recurse -File | Select-Object -First 1
        if (-not $server) { throw "产物里没有 MediaServer.exe：$zip" }

        if (Test-Path $zlmDir) { Remove-Item $zlmDir -Recurse -Force }
        New-Item -ItemType Directory -Path $zlmDir -Force | Out-Null
        Copy-Item $server.FullName (Join-Path $zlmDir "MediaServer.exe") -Force
        # 官方产物根目录的 pem 与依赖 dll。mk_api.dll 是 C API 二次开发用，MediaServer.exe
        # 不引用它（PE 里没有该名字），打进去只会白占 10 MB，排除掉。
        Get-ChildItem $server.DirectoryName -File |
            Where-Object { $_.Extension -in @(".pem", ".dll") -and $_.Name -ne "mk_api.dll" } |
            Copy-Item -Destination $zlmDir -Force
        # 只用空的录像目录，不带官方 www 前端（swagger / test_* 示例，减体积也避免暴露调试界面）
        New-Item -ItemType Directory -Path (Join-Path $zlmDir "www\record") -Force | Out-Null
        Write-Ok "zlm\ ← $($server.Name)（$([math]::Round($server.Length / 1MB, 1)) MB）"
    }
}

# ─────────────────────────── ffmpeg ───────────────────────────
if (-not $SkipFfmpeg) {
    $ffDir = Join-Path $dir "ffmpeg"
    if ((Test-Path (Join-Path $ffDir "ffmpeg.exe")) -and -not $Force) {
        Write-Step "ffmpeg 已存在，跳过（-Force 覆盖）"
    }
    else {
        Write-Step "获取 ffmpeg"
        $zip = Join-Path $tmp "ffmpeg.zip"
        Save-Remote -url $FfmpegUrl -outFile $zip -token $null
        $unpack = Expand-Zip -zip $zip -dest (Join-Path $tmp "ffmpeg")
        $exe = Get-ChildItem $unpack -Filter "ffmpeg.exe" -Recurse -File | Select-Object -First 1
        if (-not $exe) { throw "ffmpeg 包内没有 ffmpeg.exe：$zip" }
        New-Item -ItemType Directory -Path $ffDir -Force | Out-Null
        Copy-Item $exe.FullName (Join-Path $ffDir "ffmpeg.exe") -Force
        Write-Ok "ffmpeg\ffmpeg.exe（$([math]::Round($exe.Length / 1MB, 1)) MB）"
    }
}

# ─────────────────────────── NSSM ───────────────────────────
if (-not $SkipNssm) {
    $nssmExe = Join-Path $dir "nssm.exe"
    if ((Test-Path $nssmExe) -and -not $Force) {
        Write-Step "nssm.exe 已存在，跳过（-Force 覆盖）"
    }
    else {
        Write-Step "获取 NSSM"
        $zip = Join-Path $tmp "nssm.zip"
        Save-Remote -url $NssmUrl -outFile $zip -token $null
        $unpack = Expand-Zip -zip $zip -dest (Join-Path $tmp "nssm")
        $exe = Get-ChildItem $unpack -Filter "nssm.exe" -Recurse -File |
            Where-Object { $_.DirectoryName -match "win64" } | Select-Object -First 1
        if (-not $exe) { $exe = Get-ChildItem $unpack -Filter "nssm.exe" -Recurse -File | Select-Object -First 1 }
        if (-not $exe) { throw "NSSM 包内没有 nssm.exe：$zip" }
        Copy-Item $exe.FullName $nssmExe -Force
        Write-Ok "nssm.exe（$([math]::Round($exe.Length / 1MB, 2)) MB）"
    }
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "载荷就绪。下一步：pwsh -File deploy\windows\build-installer.ps1" -ForegroundColor Green
