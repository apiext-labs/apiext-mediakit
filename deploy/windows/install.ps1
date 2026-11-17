<#
.SYNOPSIS
    Apiext MediaKit Windows 原生部署安装脚本（.NET 应用 + ZLMediaKit 双服务）。

.DESCRIPTION
    一次性完成：目录规划 → 复制程序 → 检测并安装 VC++ 运行库（ZLMediaKit 依赖）
    → 生成配置（含 HMAC 签名后的 17 个 ZLM WebHook URL）→ 防火墙放行
    → 注册并启动服务 → 健康检查 → 输出访问地址与凭据。

    服务选择策略（自动）：
      · 脚本目录下有 nssm.exe  → 用 NSSM 注册两个服务（推荐：崩溃自动重启、日志落文件）
      · 没有 nssm.exe          → 应用用 sc.exe 注册为服务；ZLM 用「开机计划任务」拉起
                                 （MediaServer.exe 不支持 SCM，必须靠包装器/计划任务）

.EXAMPLE
    # 最简：对外地址必填（浏览器与国标设备都要能访问到这台机器的 IP）
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -PublicHost 192.168.1.100

.EXAMPLE
    # 自定义目录与端口，跳过防火墙配置
    .\install.ps1 -PublicHost 10.0.0.8 -InstallDir D:\apiext-mediakit -AppPort 28081 -SkipFirewall

.EXAMPLE
    # 自定义全部端口（端口冲突时用；每项必须互不相同）
    .\install.ps1 -PublicHost 10.0.0.8 -AppPort 28081 -ZlmHttpPort 28181 -ZlmRtspPort 28555 `
                  -ZlmRtmpPort 28336 -ZlmRtcPort 28001 -ZlmSrtPort 29001 -ZlmRtpPort 21001 `
                  -GbSipPort 28061 -RtpPortRange 24001-24501

.NOTES
    需要：Windows Server 2016+ / Windows 10+，管理员 PowerShell。
    前置文件（放在本脚本同目录）：
      app\        publish-app.ps1 生成的发布输出（含 Apiext.MediaKit.Server.exe）
      zlm\        ZLMediaKit Windows 版（MediaServer.exe、default.pem 及依赖 dll）
      ffmpeg\     ffmpeg.exe（"兼容"取流模式、抓拍需要；缺失只影响这两项）
      nssm.exe    可选，放进来就优先用它托管服务
      vc_redist.x64.exe  可选：VC++ 2015-2022 x64 运行库（离线环境放这里；缺失且允许时自动从 aka.ms 下载）
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublicHost,

    [string]$InstallDir = "C:\apiext-mediakit",
    [int]$AppPort = 28080,
    [int]$ZlmHttpPort = 28180,
    [int]$ZlmRtspPort = 28554,
    [int]$ZlmRtmpPort = 28335,
    [int]$ZlmRtcPort = 28000,
    [int]$ZlmSrtPort = 29000,
    [int]$ZlmRtpPort = 21000,
    [int]$GbSipPort = 28060,
    [string]$RtpPortRange = "24000-24500",

    [string]$MediaServerId = "apiext-mediakit",
    [string]$AdminUser = "admin",
    [string]$AdminPassword = "",
    [string]$ApiSecret = "",
    [string]$HookSecret = "",
    [string]$PlayTokenSecret = "",
    [string]$JwtKey = "",
    [string]$GbPassword = "",

    [string]$AppSource = "",
    # 目标机缺 VC++ 2015-2022 x64 运行库时是否自动安装（向导页可关；默认开启）
    [bool]$InstallVcRuntime = $true,
    [switch]$SkipFirewall,
    [switch]$SkipServices,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$AppServiceName = "Apiext-MediaKit-App"
$ZlmServiceName = "Apiext-MediaKit-ZLM"
$ZlmTaskName = "Apiext-MediaKit-ZLM"
$FwTcpName = "Apiext-MediaKit-TCP"
$FwUdpName = "Apiext-MediaKit-UDP"

# ─────────────────────────── 工具函数 ───────────────────────────

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Write-Ok([string]$text) { Write-Host "    [OK] $text" -ForegroundColor Green }
function Write-Warn2([string]$text) { Write-Host "    [WARN] $text" -ForegroundColor Yellow }

# ── 原生命令包装（关键）─────────────────────────────────────────
# Windows PowerShell 5.1 在 $ErrorActionPreference='Stop' 下，会把原生命令写到 stderr 的
# 「正常输出」升级成 NativeCommandError 并终止整个脚本，即使加 2>$null 也拦不住。
# 典型：schtasks /Delete 删一个不存在的计划任务 → 「错误: 系统找不到指定的文件。」→ 安装中断。
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

function Invoke-NativeRequired([string]$exe, [string[]]$argv, [string]$desc) {
    $code = Invoke-Native $exe $argv
    if ($code -ne 0) { throw "$desc 失败（$exe 退出码 $code）" }
}

# ── VC++ 2015-2022 x64 运行库（ZLMediaKit 依赖）────────────────────
# MediaServer.exe 是 MSVC 编译的，缺 VCRUNTIME140_1.dll / VCRUNTIME140.dll / MSVCP140.dll 时
# 会弹「系统错误」并退出，NSSM 表现为「服务无法启动」。默认按需自动装，失败也不阻断安装。
function Test-VcRuntime {
    # 32 位 PowerShell 访问 System32 会被 WOW64 重定向到 SysWOW64（那里没有 x64 运行库）；
    # Sysnative 是访问真实 System32 的别名，64 位进程下它不存在，走 System32 即可。
    $dirs = @((Join-Path $env:SystemRoot "Sysnative"), (Join-Path $env:SystemRoot "System32"))
    foreach ($name in @('VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'MSVCP140.dll')) {
        $found = $false
        foreach ($d in $dirs) {
            if (Test-Path (Join-Path $d $name)) { $found = $true; break }
        }
        if (-not $found) { return $false }
    }
    return $true
}

function Ensure-VcRuntime {
    $hint = "请手动安装 https://aka.ms/vs/17/release/vc_redist.x64.exe 后启动 ZLM（Start-Service $ZlmServiceName 或 schtasks /Run /TN $ZlmTaskName）"

    if (Test-VcRuntime) {
        Write-Ok "VC++ 2015-2022 x64 运行库已就绪"
        return
    }
    if (-not $InstallVcRuntime) {
        Write-Warn2 "已跳过 VC++ 运行库安装：ZLMediaKit 可能无法启动。$hint"
        return
    }

    Write-Step "安装 VC++ 2015-2022 x64 运行库（ZLMediaKit 依赖）"

    # 优先用 payload 里的本地文件（离线场景）；没有再联网下载，不塞进安装包
    $local = Join-Path $PSScriptRoot "vc_redist.x64.exe"
    $installer = $local
    $tempFile = Join-Path $env:TEMP "apiext-vc_redist.x64.exe"

    if (-not (Test-Path $local)) {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' `
                -OutFile $tempFile -UseBasicParsing -TimeoutSec 180
        }
        catch {
            Write-Warn2 "下载 VC++ 运行库失败：$($_.Exception.Message)"
        }

        if ((Test-Path $tempFile) -and (Get-Item $tempFile).Length -gt 1MB) {
            $installer = $tempFile
            Write-Ok "已下载 VC++ 运行库安装包"
        }
        else {
            if (Test-Path $tempFile) { Remove-Item $tempFile -Force -ErrorAction SilentlyContinue }
            Write-Warn2 "未能获取 VC++ 运行库安装包（无网络或被拦截）：ZLMediaKit 可能无法启动。$hint"
            return
        }
    }
    else {
        Write-Ok "使用随包提供的 vc_redist.x64.exe"
    }

    # 0 = 成功，3010 = 成功但需重启（DLL 通常已可用）
    # 注意：vc_redist 是 GUI 程序，用 & 调用 PowerShell 不会等它结束；必须 Start-Process -Wait
    try {
        $proc = Start-Process -FilePath $installer -ArgumentList @('/install', '/quiet', '/norestart') -Wait -PassThru
        $rc = $proc.ExitCode
    }
    catch {
        Write-Warn2 "运行 VC++ 运行库安装包失败：$($_.Exception.Message)。$hint"
        return
    }
    if ($rc -eq 0 -or $rc -eq 3010) {
        if (Test-VcRuntime) {
            Write-Ok "VC++ 运行库安装完成"
        }
        else {
            Write-Warn2 "安装返回 $rc 但 DLL 仍未就绪（可能需要重启）：$hint"
        }
    }
    else {
        Write-Warn2 "VC++ 运行库安装返回 $rc（0 / 3010 才是成功）：$hint"
    }

    if ($installer -eq $tempFile -and (Test-Path $tempFile)) {
        Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
    }
}

function Assert-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "请用「管理员身份」运行 PowerShell 后再执行本脚本。"
    }
}

function New-HexSecret([int]$byteCount = 32) {
    $buf = New-Object byte[] $byteCount
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buf) } finally { $rng.Dispose() }
    return (($buf | ForEach-Object { $_.ToString("x2") }) -join "")
}

function New-Password([int]$length = 14) {
    # 注意：不能用 RandomNumberGenerator.GetInt32（.NET Framework 的 PS 5.1 没有），这里取随机字节取模
    $alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    $buf = New-Object byte[] $length
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buf) } finally { $rng.Dispose() }
    $chars = for ($i = 0; $i -lt $length; $i++) { $alphabet[$buf[$i] % $alphabet.Length] }
    return -join $chars
}

# cap = base64url(HMAC-SHA256(CapabilitySecret, mediaServerId + "\n" + event))
function New-HookCap([string]$secret, [string]$serverId, [string]$event) {
    # 注意：New-Object 会把 byte[] 展开成多个构造参数（PS5.1 报 "argument count: 64"），必须用 ::new()
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($secret))
    try {
        $payload = [System.Text.Encoding]::UTF8.GetBytes(($serverId + "`n" + $event))
        $hash = $hmac.ComputeHash($payload)
        return ([Convert]::ToBase64String($hash)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    finally { $hmac.Dispose() }
}

function Write-TextFile([string]$path, [string]$content) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText($path, $content, $utf8NoBom)
}

# 统一成 CRLF（脚本源码可能是 LF，直接 Replace 会产生 \r\r\n）
function ConvertTo-Crlf([string]$text) {
    return ($text -replace "`r`n", "`n").Replace("`n", "`r`n")
}

function Stop-AndDeleteService([string]$name) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Host "    停止并删除已有服务 $name"
        if ($svc.Status -ne 'Stopped') {
            Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
        Invoke-Native 'sc.exe' @('delete', $name) | Out-Null
        Start-Sleep -Seconds 1
    }
}

function Wait-Http([string]$url, [int]$timeoutSeconds = 90) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
            if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 400) { return $true }
        }
        catch { Start-Sleep -Seconds 2 }
    }
    return $false
}

# ─────────────────────────── 前置校验 ───────────────────────────

Assert-Admin

# 端口校验：范围 / 重复 / UDP 范围格式（尽早失败，避免装到一半才报错）
$portItems = [ordered]@{
    "应用 (Web)" = $AppPort
    "ZLM HTTP"   = $ZlmHttpPort
    "RTSP"       = $ZlmRtspPort
    "RTMP"       = $ZlmRtmpPort
    "WebRTC"     = $ZlmRtcPort
    "SRT"        = $ZlmSrtPort
    "RTP 收流"   = $ZlmRtpPort
    "国标 SIP"   = $GbSipPort
}
foreach ($kv in $portItems.GetEnumerator()) {
    if ($kv.Value -lt 1 -or $kv.Value -gt 65535) {
        throw "端口「$($kv.Key)」= $($kv.Value) 非法，应为 1-65535。"
    }
}
$duplicated = $portItems.GetEnumerator() | Group-Object -Property Value | Where-Object { $_.Count -gt 1 }
if ($duplicated) {
    $desc = ($duplicated | ForEach-Object { "$($_.Name)（$(($_.Group.Name) -join ' / ')）" }) -join "；"
    throw "端口重复：$desc。请为每项服务指定不同端口。"
}
if ($RtpPortRange -match '^\s*(\d{2,5})\s*-\s*(\d{2,5})\s*$') {
    $rtpStart = [int]$Matches[1]
    $rtpEnd = [int]$Matches[2]
    if ($rtpStart -gt 65535 -or $rtpEnd -gt 65535) { throw "RTP UDP 端口范围超出 65535。" }
    if ($rtpStart -ge $rtpEnd) { throw "RTP UDP 端口范围「$RtpPortRange」起始端口必须小于结束端口。" }
    $RtpPortRange = "$rtpStart-$rtpEnd"
}
else {
    throw "RTP UDP 端口范围「$RtpPortRange」格式不正确，应形如 24000-24500。"
}

if (-not $AppSource) { $AppSource = Join-Path $PSScriptRoot "app" }
$zlmSource = Join-Path $PSScriptRoot "zlm"
$ffmpegSource = Join-Path $PSScriptRoot "ffmpeg"
$nssm = Join-Path $PSScriptRoot "nssm.exe"

$appExeName = "Apiext.MediaKit.Server.exe"
if (-not (Test-Path (Join-Path $AppSource $appExeName))) {
    throw "未找到 $AppSource\$appExeName 。载荷目录里必须有 app\ 子目录：手工部署请先执行 deploy\windows\publish-app.ps1；若来自安装包，说明安装包构建不完整，请重新执行 deploy\windows\build-installer.ps1 后重新发布。"
}
if (-not (Test-Path (Join-Path $zlmSource "MediaServer.exe"))) {
    throw "未找到 $zlmSource\MediaServer.exe 。载荷目录里必须有 zlm\ 子目录：手工部署请把 ZLMediaKit Windows 版放到 deploy\windows\zlm；若来自安装包，请重新执行 deploy\windows\build-installer.ps1 后重新发布。"
}

$hasFfmpeg = Test-Path (Join-Path $ffmpegSource "ffmpeg.exe")
$hasNssm = Test-Path $nssm

if (-not $Force -and (Test-Path (Join-Path $InstallDir "app\appsettings.json"))) {
    Write-Warn2 "$InstallDir 下已存在安装（将保留 APP_DATA 数据库并覆盖程序与配置）"
    $answer = Read-Host "    继续？(y/N)"
    if ($answer -ne "y") { throw "已取消。" }
}

# ─────────────────────────── 目录规划 ───────────────────────────

Write-Step "规划目录 $InstallDir"

$appDir = Join-Path $InstallDir "app"
$zlmDir = Join-Path $InstallDir "zlm"
$ffmpegDir = Join-Path $InstallDir "ffmpeg"
$dataDir = Join-Path $InstallDir "APP_DATA"
$logDir = Join-Path $InstallDir "logs"
$recordDir = Join-Path $zlmDir "www\record"
$zlmFfmpegLogDir = Join-Path $zlmDir "ffmpeg"

foreach ($d in @($InstallDir, $appDir, $zlmDir, $ffmpegDir, $dataDir, $logDir, $recordDir, $zlmFfmpegLogDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}
Write-Ok "目录就绪（数据库: $dataDir，录像: $recordDir）"

# ─────────────────────────── 复制程序 ───────────────────────────

Write-Step "复制程序文件"

Get-ChildItem -Path $AppSource -Force | ForEach-Object {
    Copy-Item $_.FullName -Destination $appDir -Recurse -Force
}
Write-Ok "应用 → $appDir"

Get-ChildItem -Path $zlmSource -Force | Where-Object { $_.Name -ne "config.ini" } | ForEach-Object {
    Copy-Item $_.FullName -Destination $zlmDir -Recurse -Force
}
Write-Ok "ZLMediaKit → $zlmDir"

if ($hasFfmpeg) {
    Get-ChildItem -Path $ffmpegSource -Force | ForEach-Object {
        Copy-Item $_.FullName -Destination $ffmpegDir -Recurse -Force
    }
    Write-Ok "ffmpeg → $ffmpegDir"
}
else {
    Write-Warn2 "未提供 deploy\windows\ffmpeg\ffmpeg.exe：'兼容'取流模式与抓拍快照将不可用（其余功能不受影响）"
}

# 必须在启动 ZLM 服务之前：缺运行库会直接弹「找不到 VCRUNTIME140_1.dll」
Ensure-VcRuntime

# ─────────────────────────── 生成密钥 ───────────────────────────

if (-not $ApiSecret) { $ApiSecret = [guid]::NewGuid().ToString() }
if (-not $HookSecret) { $HookSecret = New-HexSecret 32 }
if (-not $PlayTokenSecret) { $PlayTokenSecret = New-HexSecret 32 }
if (-not $JwtKey) { $JwtKey = New-HexSecret 40 }
if (-not $GbPassword) { $GbPassword = New-Password 12 }
if (-not $AdminPassword) { $AdminPassword = New-Password 14 }

# ─────────────────────── 生成 appsettings.Production.json ───────────────────────

Write-Step "生成应用配置 appsettings.Production.json"

$dbPath = Join-Path $dataDir "apiext-mediakit.db"
$appSettings = [ordered]@{
    Urls              = "http://0.0.0.0:$AppPort"
    ConnectionStrings = [ordered]@{ Default = "Data Source=$dbPath" }
    App               = [ordered]@{ PublicPort = $AppPort }
    Jwt               = [ordered]@{ Key = $JwtKey }
    ZlMediaKit        = [ordered]@{
        BaseUrl         = "http://127.0.0.1:$ZlmHttpPort"
        PublicHost      = $PublicHost
        PublicHttpPort  = $ZlmHttpPort
        Secret          = $ApiSecret
        RtspPort        = $ZlmRtspPort
        RtmpPort        = $ZlmRtmpPort
        InternalRtmpPort = $ZlmRtmpPort
        WebRtcPort      = $ZlmRtcPort
        SrtPort         = $ZlmSrtPort
    }
    ZlmHook           = [ordered]@{
        Enabled                   = $true
        CapabilitySecret          = $HookSecret
        MediaServerId             = $MediaServerId
        AllowedIps                = @("127.0.0.1", "::1")
        ReplaceOnDemandPolling    = $true
        EnforcePublishAuth        = $true
        HttpAccessAuth            = $true
        HttpAccessGuardedPrefixes = @("/record/")
        StreamChangedCache        = $true
        AllowShellLogin           = $false
    }
    PlayToken         = [ordered]@{
        Secret        = $PlayTokenSecret
        TtlSeconds    = 1800
        GraceSeconds  = 43200
        BindClientIp  = $false
    }
    Recording         = [ordered]@{ SourcePath = $recordDir }
    Gb28181           = [ordered]@{
        LocalIp = $PublicHost
        MediaIp = $PublicHost
        SipPort = $GbSipPort
        Password = $GbPassword
    }
    Admin             = [ordered]@{ Username = $AdminUser; Password = $AdminPassword }
}

$appSettingsPath = Join-Path $appDir "appsettings.Production.json"
Write-TextFile $appSettingsPath ($appSettings | ConvertTo-Json -Depth 10)
Write-Ok $appSettingsPath

# ─────────────────────── 生成 ZLMediaKit config.ini ───────────────────────

Write-Step "生成 ZLMediaKit 配置（含 17 个带 cap 的 WebHook URL）"

$hookEvents = @(
    "on_server_started", "on_stream_changed", "on_stream_none_reader", "on_stream_not_found",
    "on_publish", "on_play", "on_record_mp4", "on_rtp_server_timeout", "on_http_access",
    "on_flow_report", "on_record_ts", "on_rtsp_realm", "on_rtsp_auth",
    "on_send_rtp_stopped", "on_server_exited", "on_server_keepalive", "on_shell_login"
)
$hookBase = "http://127.0.0.1:$AppPort/api/zlm/hook"
$hookLines = ($hookEvents | ForEach-Object {
    # 必须用 ${ev} 包起来：PowerShell 会把 "$ev?cap" 的变量名解析成 "ev?cap"（变量名允许 ?），拼出来就少了 ?cap=
    $ev = $_
    $cap = New-HookCap -secret $HookSecret -serverId $MediaServerId -event $ev
    "${ev}=$hookBase/${ev}?cap=$cap"
}) -join "`r`n"

$ffmpegExe = (Join-Path $ffmpegDir "ffmpeg.exe").Replace('\', '/')

$iniTemplate = @'
; ─────────────────────────────────────────────────────────────
; Apiext MediaKit - ZLMediaKit 配置（由 deploy\windows\install.ps1 生成，改动后需重启 ZLM 服务）
; 端口默认与容器版一致（1:1 使用，不做 NAT）；安装时可在向导第二页改，改完需与 appsettings.Production.json 保持一致
; ─────────────────────────────────────────────────────────────
[general]
mediaServerId=__MEDIA_SERVER_ID__
streamNoneReaderDelayMS=10000

[api]
apiDebug=0
secret=__API_SECRET__

[http]
port=__ZLM_HTTP_PORT__

[rtsp]
port=__ZLM_RTSP_PORT__

[rtmp]
port=__ZLM_RTMP_PORT__

[rtc]
port=__ZLM_RTC_PORT__
tcpPort=__ZLM_RTC_PORT__

[srt]
port=__ZLM_SRT_PORT__

[rtp_proxy]
port=__ZLM_RTP_PORT__
port_range=__ZLM_RTP_RANGE__
timeoutSec=15

; 取流模式 PullMode=ffmpeg / 抓拍快照使用（ZLM 本体未链接 libavcodec，转码只能走 ffmpeg）
[ffmpeg]
bin=__FFMPEG_BIN__
cmd=%s -re -rtsp_transport tcp -i %s -c:v copy -c:a aac -ar 44100 -ac 2 -f flv %s
log=./ffmpeg/ffmpeg.log
restart_sec=0
snap=%s -i %s -y -f mjpeg -frames:v 1 -an %s

[hook]
enable=1
timeoutSec=3
retry=1
retry_delay=2.0

__HOOK_LINES__

; 只监听 rtsp/rtmp，避免同一流按 hls/ts/fmp4 重复回调
stream_changed_schemas=rtsp/rtmp
'@

$ini = $iniTemplate
$ini = $ini.Replace('__MEDIA_SERVER_ID__', $MediaServerId)
$ini = $ini.Replace('__API_SECRET__', $ApiSecret)
$ini = $ini.Replace('__ZLM_HTTP_PORT__', "$ZlmHttpPort")
$ini = $ini.Replace('__ZLM_RTSP_PORT__', "$ZlmRtspPort")
$ini = $ini.Replace('__ZLM_RTMP_PORT__', "$ZlmRtmpPort")
$ini = $ini.Replace('__ZLM_RTC_PORT__', "$ZlmRtcPort")
$ini = $ini.Replace('__ZLM_SRT_PORT__', "$ZlmSrtPort")
$ini = $ini.Replace('__ZLM_RTP_PORT__', "$ZlmRtpPort")
$ini = $ini.Replace('__ZLM_RTP_RANGE__', $RtpPortRange)
$ini = $ini.Replace('__FFMPEG_BIN__', $ffmpegExe)
$ini = $ini.Replace('__HOOK_LINES__', $hookLines)

$iniPath = Join-Path $zlmDir "config.ini"
Write-TextFile $iniPath $ini
Write-Ok $iniPath

# ZLM 启动脚本（无 NSSM 时由计划任务调用；有 NSSM 时也可手工排障用）
$pem = Join-Path $zlmDir "default.pem"
$sslArg = if (Test-Path $pem) { ' -s default.pem' } else { '' }
$runZlm = @"
@echo off
cd /d "%~dp0"
"%~dp0MediaServer.exe" -c "%~dp0config.ini" -l 0$sslArg
"@
Write-TextFile (Join-Path $zlmDir "run-zlm.cmd") (ConvertTo-Crlf $runZlm)
Write-Ok (Join-Path $zlmDir "run-zlm.cmd")

# ─────────────────────────── 防火墙 ───────────────────────────

if (-not $SkipFirewall) {
    Write-Step "配置防火墙入站规则"
    foreach ($n in @($FwTcpName, $FwUdpName)) {
        Get-NetFirewallRule -DisplayName $n -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    }
    $tcpPorts = @($AppPort, $ZlmHttpPort, $ZlmRtspPort, $ZlmRtmpPort, $ZlmRtcPort, $ZlmSrtPort, $ZlmRtpPort, $GbSipPort) |
        Sort-Object -Unique | ForEach-Object { "$_" }
    $udpPorts = @("$ZlmRtcPort", "$ZlmSrtPort", "$ZlmRtpPort", "$GbSipPort", $RtpPortRange) | Sort-Object -Unique

    New-NetFirewallRule -DisplayName $FwTcpName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $tcpPorts | Out-Null
    New-NetFirewallRule -DisplayName $FwUdpName -Direction Inbound -Action Allow -Protocol UDP -LocalPort $udpPorts | Out-Null
    Write-Ok "TCP: $($tcpPorts -join ', ')"
    Write-Ok "UDP: $($udpPorts -join ', ')（国标收流依赖 $RtpPortRange）"
}
else {
    Write-Warn2 "已跳过防火墙配置；请自行放行 TCP $AppPort,$ZlmHttpPort,$ZlmRtspPort,$ZlmRtmpPort 与 UDP $RtpPortRange 等端口"
}

# ─────────────────────────── 注册服务 ───────────────────────────

$zlmParams = "-c `"$iniPath`" -l 0"
if (Test-Path $pem) { $zlmParams = "-s default.pem " + $zlmParams }

# 服务注册/启动阶段的警告一律只记录，不允许中断安装：
# NSIS 一旦 Abort，Uninstall.exe 与注册表卸载项就写不进去，用户反而没法卸载。
$serviceWarnings = New-Object 'System.Collections.Generic.List[string]'

if (-not $SkipServices) {
    Write-Step "注册并启动服务（$(if ($hasNssm) { 'NSSM' } else { 'sc.exe + 计划任务' })）"

    $registered = $false
    try {
        Stop-AndDeleteService $AppServiceName
        Stop-AndDeleteService $ZlmServiceName
        # 任务不存在时 schtasks /Delete 会往 stderr 写「错误: 系统找不到指定的文件。」，
        # 必须走 Invoke-Native（内部降 EAP 并把 stderr 并入输出丢弃），否则 $ErrorActionPreference='Stop' 下会中断安装
        Invoke-Native 'schtasks.exe' @('/Delete', '/TN', $ZlmTaskName, '/F') | Out-Null

        if ($hasNssm) {
            & $nssm install $AppServiceName (Join-Path $appDir $appExeName) | Out-Null
            & $nssm set $AppServiceName AppDirectory $appDir | Out-Null
            & $nssm set $AppServiceName DisplayName "Apiext MediaKit 管理平台" | Out-Null
            & $nssm set $AppServiceName Description "Apiext MediaKit 管理平台（Blazor + Web API）" | Out-Null
            & $nssm set $AppServiceName Start SERVICE_AUTO_START | Out-Null
            & $nssm set $AppServiceName AppStdout (Join-Path $logDir "app.out.log") | Out-Null
            & $nssm set $AppServiceName AppStderr (Join-Path $logDir "app.err.log") | Out-Null
            & $nssm set $AppServiceName AppRotateFiles 1 | Out-Null
            & $nssm set $AppServiceName AppRotateBytes 10485760 | Out-Null
            & $nssm set $AppServiceName AppExit Default Restart | Out-Null
            & $nssm set $AppServiceName AppRestartDelay 3000 | Out-Null

            & $nssm install $ZlmServiceName (Join-Path $zlmDir "MediaServer.exe") | Out-Null
            & $nssm set $ZlmServiceName AppDirectory $zlmDir | Out-Null
            & $nssm set $ZlmServiceName AppParameters $zlmParams | Out-Null
            & $nssm set $ZlmServiceName DisplayName "Apiext MediaKit 流媒体服务（ZLMediaKit）" | Out-Null
            & $nssm set $ZlmServiceName Description "ZLMediaKit 流媒体引擎" | Out-Null
            & $nssm set $ZlmServiceName Start SERVICE_AUTO_START | Out-Null
            & $nssm set $ZlmServiceName AppStdout (Join-Path $logDir "zlm.out.log") | Out-Null
            & $nssm set $ZlmServiceName AppStderr (Join-Path $logDir "zlm.err.log") | Out-Null
            & $nssm set $ZlmServiceName AppRotateFiles 1 | Out-Null
            & $nssm set $ZlmServiceName AppRotateBytes 10485760 | Out-Null
            & $nssm set $ZlmServiceName AppExit Default Restart | Out-Null
            & $nssm set $ZlmServiceName AppRestartDelay 3000 | Out-Null
            Write-Ok "NSSM 服务已注册：$ZlmServiceName / $AppServiceName"
        }
        else {
            $binPath = "`"$(Join-Path $appDir $appExeName)`""
            Invoke-NativeRequired 'sc.exe' @('create', $AppServiceName, 'binPath=', $binPath, 'start=', 'auto', 'DisplayName=', 'Apiext MediaKit 管理平台') '注册应用服务'
            Invoke-NativeRequired 'sc.exe' @('description', $AppServiceName, 'Apiext MediaKit 管理平台（Blazor + Web API）') '写入服务描述'
            Invoke-NativeRequired 'sc.exe' @('failure', $AppServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/10000/norestart/0') '配置服务失败重启策略'
            Write-Ok "应用服务已注册（sc.exe）：$AppServiceName"

            # /TR 传裸路径：PowerShell 会自动给含空格的参数加引号（手工套引号在 5.1 下容易被再转义）
            Invoke-NativeRequired 'schtasks.exe' `
                @('/Create', '/TN', $ZlmTaskName, '/SC', 'ONSTART', '/RU', 'SYSTEM', '/RL', 'HIGHEST',
                  '/TR', (Join-Path $zlmDir 'run-zlm.cmd'), '/F') '注册 ZLM 开机计划任务'
            Write-Ok "ZLM 已注册为开机计划任务：$ZlmTaskName（MediaServer.exe 不支持 SCM，故用计划任务）"
        }
        $registered = $true
    }
    catch {
        $msg = "服务注册阶段异常：$($_.Exception.Message)"
        $serviceWarnings.Add($msg)
        Write-Warn2 $msg
    }

    # 先起流媒体，再起应用；逐项尽力启动，某个失败不影响其它服务、更不中断安装
    if ($registered) {
        if ($hasNssm) {
            $startTargets = @(
                @{ Name = $ZlmServiceName; Kind = 'service' },
                @{ Name = $AppServiceName; Kind = 'service' }
            )
        }
        else {
            $startTargets = @(
                @{ Name = $ZlmTaskName; Kind = 'task' },
                @{ Name = $AppServiceName; Kind = 'service' }
            )
        }

        $okCount = 0
        foreach ($target in $startTargets) {
            try {
                if ($target.Kind -eq 'service') {
                    Start-Service -Name $target.Name -ErrorAction Stop
                }
                else {
                    Invoke-NativeRequired 'schtasks.exe' @('/Run', '/TN', $target.Name) '启动 ZLM 计划任务'
                }
                $okCount++
                Write-Ok "已启动：$($target.Name)"
            }
            catch {
                $msg = "启动 $($target.Name) 失败：$($_.Exception.Message)"
                $serviceWarnings.Add($msg)
                Write-Warn2 $msg
            }
        }

        if ($okCount -eq $startTargets.Count) {
            Write-Ok "服务已启动"
        }
        else {
            Write-Warn2 "$okCount/$($startTargets.Count) 项已启动；未启动的可修复后手工启动，安装继续"
        }
    }
    else {
        $serviceWarnings.Add("服务未注册，跳过启动检查；请查看上方注册阶段的报错")
    }
}
else {
    Write-Warn2 "已跳过服务注册。手工启动：`"$zlmDir\run-zlm.cmd`" 与 `"$(Join-Path $appDir $appExeName)`""
}

# ─────────────────────────── 健康检查 ───────────────────────────

$appUrl = "http://$PublicHost`:$AppPort"
$localUrl = "http://127.0.0.1:$AppPort"

if (-not $SkipServices) {
    Write-Step "健康检查"
    # 服务启动已有警告时不必等满 90 秒，尽快收尾让安装正常完成
    $healthTimeout = 90
    if ($serviceWarnings.Count -gt 0) { $healthTimeout = 20 }
    if (Wait-Http -url "$localUrl/login" -timeoutSeconds $healthTimeout) {
        Write-Ok "应用已就绪：$localUrl/login"
    }
    else {
        Write-Warn2 "应用 $healthTimeout 秒内未就绪，请查看日志：$logDir\app.err.log（或 事件查看器 → Windows 日志 → 应用程序）"
    }

    try {
        $zlmApi = Invoke-WebRequest -Uri "http://127.0.0.1:$ZlmHttpPort/index/api/getServerConfig?secret=$ApiSecret" -UseBasicParsing -TimeoutSec 5
        if ($zlmApi.StatusCode -eq 200) { Write-Ok "ZLMediaKit 已就绪：http://127.0.0.1:$ZlmHttpPort" }
        else { Write-Warn2 "ZLMediaKit HTTP 返回 $($zlmApi.StatusCode)" }
    }
    catch {
        Write-Warn2 "ZLMediaKit API 暂不可用（$($_.Exception.Message)），查看 $logDir\zlm.err.log"
    }
}

# ─────────────────────────── 安装报告 ───────────────────────────

$reportPath = Join-Path $InstallDir "install-report.txt"
$report = @"
Apiext MediaKit Windows 安装报告
生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

访问地址:      $appUrl       (本机: $localUrl)
监听地址:      0.0.0.0（应用与 ZLM 都监听全部网卡；上面的对外 IP 只用于对外通告，不影响监听）
默认账号:      $AdminUser / $AdminPassword
安装目录:      $InstallDir
应用目录:      $appDir
ZLM 目录:      $zlmDir
数据目录:      $dataDir   (升级时保留)
录像目录:      $recordDir
日志目录:      $logDir
服务名:        应用=$AppServiceName, ZLM=$(if ($hasNssm) { $ZlmServiceName } else { "计划任务 $ZlmTaskName" })

--- 以下密钥请妥善保管，重装会重新生成 ---
ZlMediaKit api secret : $ApiSecret
Hook 能力密钥        : $HookSecret
播放令牌密钥         : $PlayTokenSecret
JWT 密钥             : $JwtKey
国标 SIP 密码        : $GbPassword

端口: 应用 $AppPort / ZLM-HTTP $ZlmHttpPort / RTSP $ZlmRtspPort / RTMP $ZlmRtmpPort / WebRTC $ZlmRtcPort / SRT $ZlmSrtPort
      国标 SIP(UDP) $GbSipPort / RTP 收流 $ZlmRtpPort / RTP UDP 范围 $RtpPortRange
"@
if ($serviceWarnings.Count -gt 0) {
    $report += "`r`n--- 安装警告（服务未完全启动，安装已正常完成）---`r`n"
    $report += (($serviceWarnings | ForEach-Object { "- $_" }) -join "`r`n")
    $report += "`r`n`r`n修复后手工启动：`r`n  Start-Service $AppServiceName`r`n"
    if ($hasNssm) { $report += "  Start-Service $ZlmServiceName`r`n" }
    else { $report += "  schtasks /Run /TN $ZlmTaskName`r`n" }
    $report += "若因缺 VC++ 2015-2022 x64 运行库导致 ZLM 未启动：先安装 https://aka.ms/vs/17/release/vc_redist.x64.exe 再执行上述命令`r`n"
    $report += "卸载入口：安装目录\Uninstall.exe（服务异常不影响卸载）`r`n"
}
Write-TextFile $reportPath (ConvertTo-Crlf $report)

Write-Host ""
Write-Host "安装完成" -ForegroundColor Green
Write-Host "  访问地址 : $appUrl" -ForegroundColor Green
Write-Host "  账号密码 : $AdminUser / $AdminPassword"
Write-Host "  报告文件 : $reportPath"
Write-Host ""
Write-Host "常用操作："
Write-Host "  查看状态 : Get-Service $AppServiceName,$ZlmServiceName  (无 NSSM 时 ZLM 用 schtasks /Query /TN $ZlmTaskName)"
Write-Host "  重启应用 : Restart-Service $AppServiceName"
Write-Host "  查看日志 : Get-Content '$logDir\app.out.log' -Tail 50 -Wait"
Write-Host "  卸载     : powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveData"

# 服务启动失败已降级为警告：安装必须正常收尾，否则 NSIS 会 Abort、卸载入口写不进去
exit 0
