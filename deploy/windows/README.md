# Apiext MediaKit — Windows 原生部署包

不依赖 Docker，在 Windows 上以「双服务」方式运行：

```
Apiext-MediaKit-ZLM    ZLMediaKit（MediaServer.exe，流媒体引擎）
Apiext-MediaKit-App    Apiext.MediaKit.Server.exe（管理平台：Blazor UI + Web API + 业务）
```

校验流程：`APPLICATION → http://127.0.0.1:28180/index/api/*`（ZLM API）、`ZLM → http://127.0.0.1:28080/api/zlm/hook/*`（WebHook，URL 带 HMAC `?cap=`）。

---

## 1. 目录结构

```
deploy/windows/
├─ installer.nsi             NSIS 安装包脚本（一键安装，内置 app/zlm/ffmpeg/nssm）
├─ build-installer.ps1       编译安装包 → Apiext-MediaKit-Setup-<版本>.exe
├─ fetch-payload.ps1         下载 zlm/ffmpeg/nssm 载荷（CI 与本地打包共用）
├─ install.ps1               安装（生成配置、放行端口、注册并启动服务；安装包内部也调它）
├─ uninstall.ps1             卸载（可保留数据）
├─ publish-app.ps1           生成发布输出到 app/（在仓库根目录执行）
├─ detect-ip.ps1             安装向导预填本机 IP 用
├─ app/                      ← publish-app.ps1 生成（不要手工改）
├─ zlm/                      ← ZLMediaKit Windows 版（MediaServer.exe、default.pem、www\record）
├─ ffmpeg/                   ← ffmpeg.exe（「兼容」取流与抓拍）
├─ nssm.exe                  ← NSSM（托管两个服务；安装包已内置）
└─ vc_redist.x64.exe         ← 可选：VC++ 2015-2022 x64 运行库（离线环境放这里；默认不内置）
```

安装后的目标机目录：

```
C:\apiext-mediakit\
├─ app\                    程序（升级时整体覆盖）
│  └─ appsettings.Production.json   由 install.ps1 生成
├─ zlm\                    MediaServer.exe + config.ini（生成）+ www\record（录像）
├─ ffmpeg\                 ffmpeg.exe
├─ APP_DATA\apiext-mediakit.db    SQLite 数据库（升级时保留）
├─ logs\                   服务 stdout/stderr（NSSM 模式）
└─ install-report.txt      本次安装的地址、账号、各项密钥
```

---

## 2. 前置条件

| 项 | 要求 |
|---|---|
| 系统 | Windows Server 2016+ / Windows 10+（x64） |
| 权限 | 管理员 PowerShell |
| .NET | 用自包含发布（默认）则**无需安装**；框架依赖模式需 ASP.NET Core Hosting Bundle 10 |
| ZLMediaKit | 必须放到 `deploy\windows\zlm\`：`MediaServer.exe`（Windows 编译版）+ `default.pem` + 依赖 dll |
| NSSM | 可选但推荐，见下方说明 |
| ffmpeg | 可选：`ffmpeg.exe` 放 `deploy\windows\ffmpeg\`，否则「兼容」取流与抓拍快照不可用 |
| VC++ 运行库 | `MediaServer.exe` 依赖 VC++ 2015-2022 x64；向导会检测并按需联网下载，离线可放 `vc_redist.x64.exe` |

> **ZLM 起不来的首要原因**：干净的 Windows Server 常缺 `VCRUNTIME140.dll` / `VCRUNTIME140_1.dll` / `MSVCP140.dll`，启动 `MediaServer.exe` 会弹「系统错误：找不到 VCRUNTIME140_1.dll」，NSSM 则报 `无法启动服务 Apiext-MediaKit-ZLM`。安装包**不内置** 25 MB 的 `vc_redist.x64.exe`（NSIS 用 `File /nonfatal` 可选打包），默认由向导页按需从 `https://aka.ms/vs/17/release/vc_redist.x64.exe` 下载并静默安装；下载或安装失败**不阻断安装**，只提示手动安装后执行 `Start-Service Apiext-MediaKit-ZLM`（无 NSSM 时用 `schtasks /Run /TN Apiext-MediaKit-ZLM`）。

### 为什么 ZLM 需要 NSSM 或计划任务

`MediaServer.exe` 不实现 Windows 服务控制协议（不调用 `StartServiceCtrlDispatcher`），用 `sc.exe` 直接注册会启动失败（错误 1053）。脚本的处理策略：

- **有 `nssm.exe`** → 两个服务都用 NSSM（崩溃自动重启、日志落文件、开机自启）；
- **没有** → 应用用 `sc.exe` 注册为服务，ZLM 用**开机计划任务**（`ONSTART` + SYSTEM）拉起。

> 建议把 [NSSM](https://nssm.cc/download) 的 `nssm.exe` 放进 `deploy\windows\` 一起交付。

---

## 3. 打包（开发机执行）

```powershell
# 在仓库根目录
powershell -ExecutionPolicy Bypass -File deploy\windows\publish-app.ps1
# 目标机没有 .NET 运行时（默认就是自包含，不需要额外参数）
# 想减小体积（需目标机装 Hosting Bundle）：
powershell -ExecutionPolicy Bypass -File deploy\windows\publish-app.ps1 -SelfContained:$false
```

然后把 `zlm\`、`ffmpeg\`、`nssm.exe` 补齐，整个 `deploy/windows` 目录打包带走。

### 3.1 打包成单文件安装包（NSIS，推荐交付方式）

`app\ zlm\ ffmpeg\ nssm.exe` 齐备后：

```powershell
# 需要 NSIS：scoop install nsis
powershell -ExecutionPolicy Bypass -File deploy\windows\build-installer.ps1
# 产物：deploy\windows\Apiext-MediaKit-Setup-261117-1140.exe（版本号默认取当前时间，-Version 可指定）
```

安装包做的事（等价于手工跑 install.ps1，只是加了向导）：

1. 向导页 1「配置访问地址」：**对外 IP**（默认预填本机第一个内网 IPv4）+ **应用端口**（默认 28080，与 Docker 版宿主端口一致）；
2. 向导页 2「配置流媒体端口」：ZLM HTTP / RTSP / RTMP / WebRTC / SRT / 国标 SIP / RTP 收流 与 **RTP UDP 端口范围**（默认 24000-24500），端口冲突时才需要改；退出前会校验 1-65535、互不重复、UDP 范围格式；
3. 向导页 3「VC++ 运行库」：**仅在目标机缺 VC++ 2015-2022 x64 运行库时出现**（已装则自动跳过），默认勾选「安装时自动下载并安装」，并提供「打开官方下载页」按钮；取消勾选或下载失败只影响 ZLM 启动，不阻断安装；
4. 载荷解到 `%TEMP%`，调用 `install.ps1 -InstallDir <安装目录> -PublicHost <IP> -AppPort <端口> -ZlmHttpPort ... -RtpPortRange <范围> -InstallVcRuntime <0|1> -Force`；`install.ps1` 优先用随包的 `vc_redist.x64.exe`，没有才联网下载；
5. 卸载入口（`安装目录\Uninstall.exe` + 注册表卸载项）在调用 `install.ps1` **之前**就已写入，安装完成后再写 `管理平台.url` 快捷方式；
6. 完成页可一键打开**安装报告**（含账号密码、各项密钥与实际端口）和**管理平台**。

注意：安装过程需要 `%TEMP%` 至少约 200 MB 空闲（自包含应用载荷先落临时目录），安装目录约需 400 MB。

> **服务启动失败不会中断安装**：`Start-Service` / `schtasks /Run` 失败（例如目标机缺 VC++ 运行库）只记 `[WARN]`，安装照常做健康检查、写 `install-report.txt` 并正常收尾，**不会让 NSIS 弹「安装失败 → Abort」**。卸载入口已提前落地，任何情况下都能从控制面板卸载。所有服务警告会汇总到 `install-report.txt` 的「安装警告」段，并附上修复后的手工启动命令（`Start-Service Apiext-MediaKit-ZLM` / `Start-Service Apiext-MediaKit-App`）。

> **给打包维护者的坑**：`installer.nsi` 里每组 `File /r "app\*.*"` / `zlm\*.*` / `ffmpeg\*.*` **前必须先 `SetOutPath` 到 `$PLUGINSDIR\payload\<同名子目录>`**。NSIS 的 `File /r` 只把目录「内容」平铺进当前 `$OUTDIR`，不会自动创建 `app\`、`zlm\` 这层；而 `install.ps1` 是按 `$PSScriptRoot\app` / `zlm` / `ffmpeg` 找载荷的。少写这行就会出现「未找到 …\payload\app\Apiext.MediaKit.Server.exe」，且三组文件互相覆盖混在一起。

静默安装（无人值守）：

```powershell
Apiext-MediaKit-Setup-261117-1140.exe /S /D=C:\apiext-mediakit
```
> `/S` 静默时不会弹参数页，脚本取 `detect-ip.ps1` 探测到的 IP 与全部默认端口（28080 / 28180 / 28554 / 28335 / 28000 / 29000 / 21000 / 28060 / 24000-24500）；缺 VC++ 运行库时按默认勾选联网下载安装（不联网就随包放 `vc_redist.x64.exe`，或直接跑 `install.ps1 -InstallVcRuntime $false`）。需要自定义地址或端口时请用交互安装，或直接跑 `install.ps1` 传参。

---

### 3.2 手动发布（GitHub Actions）

在 Actions 页面手动触发 `.github/workflows/windows-installer.yml`（Run workflow），无需本地打包、无需推标签：

- 默认 `publish=true`：创建/更新 Release 并附 `Apiext-MediaKit-Setup-<版本>.exe`；取消勾选则只产出 artifact，不发 Release。
- 版本号自动生成为 `yyMMdd-HHmm`（Asia/Shanghai，如 `261117-1140`），同时改写 `installer.nsi` 的 `APP_VERSION`。

工作流在 `windows-latest` 上依次执行：`publish-app.ps1`（自包含发布）→ `fetch-payload.ps1`（下载 ZLMediaKit / ffmpeg / NSSM）→ 安装 NSIS 编译安装包 → 上传 Actions artifact → （`publish=true` 时）创建/更新 Release。

载荷下载（`fetch-payload.ps1`）：

| 载荷 | 默认来源 | 覆盖参数 |
|---|---|---|
| `zlm\` | ZLMediaKit 官方 Actions 产物（nightly.link 免登录直链） | `-ZlmArtifactUrl`（本地 zip 或直链）/ `-Token`（走 GitHub API） |
| `ffmpeg\` | gyan.dev essentials build | `-FfmpegUrl` |
| `nssm.exe` | nssm.cc 2.24 win64 | `-NssmUrl` |

> `vc_redist.x64.exe` **不在** `fetch-payload.ps1` 的下载清单里：安装包默认不内置，由目标机向导按需联网下载。完全离线交付时，先从 `https://aka.ms/vs/17/release/vc_redist.x64.exe` 下载并放到 `deploy/windows/`，再执行 `build-installer.ps1`（NSIS 的 `File /nonfatal` 会自动把它打进包）。

本地想走同一套流程：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\fetch-payload.ps1   # -Force 覆盖已有载荷
powershell -ExecutionPolicy Bypass -File deploy\windows\build-installer.ps1
```

> ZLM 产物保留 90 天、且有新构建即刷新，脚本每次都取最新的成功构建；需要固定版本时下载对应 zip 后用 `-ZlmArtifactUrl .\Windows_master_<日期>.zip`。

## 4. 安装（目标机执行）

```powershell
# 最小用法：-PublicHost 必填，必须是「浏览器能访问、国标设备能推流到」的本机 IP
powershell -ExecutionPolicy Bypass -File .\install.ps1 -PublicHost 192.168.1.100
```

常用参数：

| 参数 | 默认 | 说明 |
|---|---|---|
| `-PublicHost` | 必填 | 对外 IP/域名，写入 `ZlMediaKit:PublicHost` 与 `Gb28181:LocalIp/MediaIp` |
| `-InstallDir` | `C:\apiext-mediakit` | 安装目录 |
| `-AppPort` | `28080` | 应用监听端口（与 Docker 版宿主端口一致；改端口会重建 config.ini 的 hook URL） |
| `-ZlmHttpPort` | `28180` | ZLM HTTP/API 端口，浏览器播放地址也用它 |
| `-ZlmRtspPort` / `-ZlmRtmpPort` | `28554` / `28335` | RTSP / RTMP 端口 |
| `-ZlmRtcPort` / `-ZlmSrtPort` | `28000` / `29000` | WebRTC（UDP+TCP）/ SRT |
| `-ZlmRtpPort` | `21000` | 国标 RTP over TCP 监听端口 |
| `-GbSipPort` | `28060` | 国标 SIP（UDP）端口 |
| `-RtpPortRange` | `24000-24500` | 国标 RTP 收流 UDP 端口段（格式 `起始-结束`，起始须小于结束） |
| `-AdminPassword` | 随机生成 | 初始管理员密码（旧库已存在则沿用库里的） |
| `-ApiSecret` / `-HookSecret` / `-PlayTokenSecret` / `-JwtKey` / `-GbPassword` | 随机生成 | 留空自动随机；填值便于多机统一 |
| `-InstallVcRuntime` | `true`（向导复选框） | 缺 VC++ 运行库时是否自动下载/安装；`false` 只警告不安装 |
| `-SkipFirewall` | 否 | 跳过防火墙规则（手工开端口时用） |
| `-SkipServices` | 否 | 只铺文件与配置，不注册服务 |

安装脚本会自动做：**端口校验（1-65535、互不重复、UDP 范围合法）** → 目录规划 → 复制程序 → **检测并安装 VC++ 运行库**（优先 payload 里的本地文件，否则联网下载）→ 生成 `appsettings.Production.json` → 生成 `zlm\config.ini`（含 17 个带 `?cap=` 的 hook URL，HMAC 由脚本现算）→ 防火墙放行 → 注册并启动服务 → 健康检查 → 写 `install-report.txt`。端口填错会在最开头直接报错退出，不会装到一半才失败。

> `?cap=` 只签 `mediaServerId + event`，**改端口/改 IP 不需要重算**，重新跑一次 install.ps1 会整体重建。

---

## 5. 端口清单（需放行）

下表是默认值，**全部可在安装向导改**（页 1 改应用端口，页 2 改流媒体端口；或跑 `install.ps1` 传对应参数）：

| 端口 | 协议 | 用途 |
|---|---|---|
| 28080 | TCP | 管理平台（`-AppPort`；容器版是宿主 28080 → 容器 8080，Windows 直连 28080） |
| 28180 | TCP | ZLM HTTP：API、HTTP-FLV、HLS、抓拍、录像下载 |
| 28554 | TCP | RTSP |
| 28335 | TCP | RTMP |
| 28000 | UDP + TCP | WebRTC |
| 29000 | UDP | SRT |
| 21000 | TCP | 国标 RTP over TCP |
| 24000-24500 | UDP（+TCP） | 国标点播收流端口段，**国标黑屏优先查这里** |
| 28060 | UDP | 国标 SIP 信令 |

脚本会创建两条入站规则：`Apiext-MediaKit-TCP`、`Apiext-MediaKit-UDP`。

> **监听地址 = 0.0.0.0（全部网卡）**：应用由 `Urls=http://0.0.0.0:<AppPort>` 显式指定，ZLM 的 HTTP/RTSP/RTMP/WebRTC/SRT/RTP 与国标 SIP 也都监听任意 IP（含 IPv6）。向导里填的「对外 IP」**只用于对外通告**（播放地址、SIP `From`、SDP `c=`），不影响监听——换网卡、加网卡都不需要重装，能访问到本机任一 IP 即可。应用在没有 `Urls` 也没有 `ASPNETCORE_URLS` 时（例如手工双击 exe）也会兜底监听 `0.0.0.0:App:PublicPort`，不会退化成只监听 localhost。

---

## 6. 安装后验证清单

1. 浏览器打开 `http://<PublicHost>:28080`，用 `install-report.txt` 里的账号登录；
2. 首页「运行概览」有数字、无「流媒体服务未连接」告警 → ZLM 连通；
3. 「设备列表 → 添加」接入一路 RTSP，点「播放」能出画面（H.265 走 WASM 软解，CPU 会高）；
4. 国标设备注册上线，点播能出画面（黑屏先查 UDP 24000-24500 防火墙与 `MediaIp`）；
5. 设备卡片封面出现快照；拖动云台正常；
6. 开录像 → `C:\apiext-mediakit\zlm\www\record` 下有 mp4 生成 → 「录像回放」能播。

---

## 7. 升级

```powershell
Stop-Service Apiext-MediaKit-App
# 用新发布的 app 目录覆盖（不要删除 APP_DATA）
Copy-Item .\app\* C:\apiext-mediakit\app -Recurse -Force
Start-Service Apiext-MediaKit-App
```

- 数据库结构由 `DbInitializer` 在启动时自动补齐（`CREATE TABLE IF NOT EXISTS` + 幂等 `ALTER TABLE ADD COLUMN`），**旧库直接可用，无需手工迁移**；
- 配置项有新增时，把新键补进 `app\appsettings.Production.json`（合并式配置，缺的键回落 `appsettings.json` 默认值）；
- 升级 ZLM：停 `Apiext-MediaKit-ZLM` → 覆盖 `zlm\` 下除 `config.ini` 外的文件 → 启动。

---

## 8. 卸载

```powershell
# 只卸载服务 / 计划任务 / 防火墙规则，保留程序与数据
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1

# 连安装目录一起删（会删数据库与录像，需二次确认）
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveData
```

---

## 9. 排障

| 现象 | 排查 |
|---|---|
| `sc start` 报 1053 | ZLM 不能用 `sc.exe`，用 NSSM 或计划任务；应用侧请确认用的是本包发布的 exe（已内置 `UseWindowsService`） |
| `MediaServer.exe` 起不来、事件日志报缺 `MSVCP140.dll` / `VCRUNTIME140.dll` / `VCRUNTIME140_1.dll` | 官方 ZLM Windows 产物动态链接 VC++ 运行库；安装包向导会按需下载并静默安装 **VC++ 2015-2022 x64 运行库**（`vc_redist.x64.exe`）。若当时取消勾选或离线无网：手动装运行库后执行 `Start-Service Apiext-MediaKit-ZLM`（无 NSSM 时 `schtasks /Run /TN Apiext-MediaKit-ZLM`），或把这几个 dll 放到安装目录 `zlm\` 下；服务未启动会记在 `install-report.txt` 的「安装警告」段 |
| 应用启动即退出 / 找不到 appsettings | 必须走服务或在本目录运行；已把 ContentRoot 固定为程序目录，手工运行时也要 `cd` 到 `app\` |
| 登录后页面 500、提示数据库只读 | `APP_DATA` 目录权限不足；用有写权限的账号或把目录移到非系统盘 |
| 播放地址是 127.0.0.1 / 内网错网卡 | 服务本身监听 `0.0.0.0`，能访问但**播放地址里带的主机名不对**：`-PublicHost` 传错，或有多网卡（Hyper-V/VMware 虚拟网卡）时自动探测取了虚拟网卡，需显式指定真实局域网 IP |
| 国标点播 200 但黑屏 | ① UDP 24000-24500 未放行 ② `Gb28181:MediaIp` 不是设备可达 IP ③ 杀软/网络防护拦截 UDP |
| 抓拍/封面不显示、兼容取流失败 | `zlm\config.ini` 的 `[ffmpeg] bin` 是否指向真实 `ffmpeg.exe`；缺 ffmpeg 时这两项不可用 |
| 录像列表为空 | `Recording:SourcePath` 必须等于 `C:\apiext-mediakit\zlm\www\record`（脚本已按此生成） |
| 日志在哪 | NSSM 模式：`logs\app.out.log`、`logs\app.err.log`、`logs\zlm.*.log`；也可看「事件查看器 → Windows 日志 → 应用程序」（源名 `Apiext-MediaKit`） |
| 端口被占用 | `netstat -ano | findstr :28180`；卸载脚本会兜底结束残留的 `MediaServer` / `Apiext.MediaKit.Server` 进程 |
| 安装时报「未找到 …\payload\app\Apiext.MediaKit.Server.exe」 | 安装包构建不完整：载荷被平铺到 `payload` 根目录。请用修复后的 `installer.nsi` 重新执行 `build-installer.ps1` 并重新发布安装包；已下载的安装包请丢弃重下 |

> Windows 版 ZLM 同样**未链接 libavcodec**：`addStreamProxy` 的 `enable_audio` 无效，G.711 转 AAC、视频重编码只能靠 ffmpeg 取流模式（`PullMode=ffmpeg`），2 核机不要承诺 2.5K 实时转码。

---

## 9.1 打包进安装包的第三方组件

| 组件 | 版本 / 来源 | 用途 | 许可 |
|---|---|---|---|
| ZLMediaKit（Windows x64） | 官方 CI 产物 `Windows_master_2026-11-02`（nightly.link 的 `windows.yml` artifact） | 流媒体引擎（RTSP/RTMP/HLS/HTTP-FLV/WebRTC/GB28181 收流） | MIT |
| ffmpeg | 9.0.2 essentials build（gyan.dev） | 「兼容」取流、G711→AAC、抓拍 | GPL（分发时需随包提供许可与源码获取方式） |
| NSSM | 2.24（win64） | 把 MediaServer.exe 托管成 Windows 服务 | 公共领域 |
| .NET 运行时 | 10.0（自包含发布，随 app 目录） | 应用运行时 | MIT |

> **VC++ 运行库默认不进包**：`vc_redist.x64.exe`（约 25 MB）只是可选离线载荷（NSIS `File /nonfatal`），放到 `deploy/windows/` 再打包才会内置，体积相应增加；不放时由目标机向导按需联网下载并静默安装。

> 说明：只从 ZLM 产物里取了 `MediaServer.exe`、`default.pem` 与空的 `www\record`（去掉了 test_* 示例与 swagger 页面，减少体积也避免暴露调试界面）。ZLM 的 `mk_api.dll` 是 C API 用库，`MediaServer.exe` 自身静态链接，不需要。
>
> 升级这三个组件时：替换 `zlm\`、`ffmpeg\`、`nssm.exe` 后重新执行 `build-installer.ps1` 即可，`install.ps1` 无需改动（新版本若改了配置键，才需要同步 `install.ps1` 里的 config.ini 模板）。

### 9.1.1 体积构成（`MediaServer.exe` 只有 10.7 MB 是正常的）

安装包（如 `Apiext-MediaKit-Setup-261117-1140.exe`）约 80 MB，内部载荷未压缩 276.6 MB（LZMA 压到 28.8%）：

| 内容 | 未压缩 | 占比 | 说明 |
|---|---|---|---|
| `app\`（.NET 10 自包含 + 应用） | 154.9 MB | 56% | 占大头，换来「目标机不用装运行时」 |
| `ffmpeg\ffmpeg.exe` | 100.5 MB | 36% | 静态链接全部 codec 的单文件 |
| `zlm\`（`MediaServer.exe` 10.72 MB + `default.pem` 5.5 KB + 空 `www\record`） | 10.7 MB | 4% | 流媒体引擎本体 |
| `nssm.exe` | 0.3 MB | — | 服务托管器 |

`MediaServer.exe` 体积小的原因（**不是残缺产物**）：

1. **静态链接**：openssl / zlib / media-server 全部编进 exe —— 同目录 **0 个 dll**，PE 里也**不引用 `mk_api.dll`**；
2. **无调试符号**：release 构建、无 `.pdb`，未 strip 的 debug 构建通常大 2-3 倍；
3. **只取运行必需**：官方 artifact 里的 `www\` 前端（swagger / 播放器 / `test_*` 示例）与 `mk_api.dll`（C API，二次开发用）都没打进来。

同理 `ffmpeg.exe` 那 100.5 MB 也是静态链接的必然结果：Linux 上同样功能是 `/usr/bin/ffmpeg` **335 KB** + `/usr/lib/x86_64-linux-gnu` 里 **536 MB** 的 `libav*` 共享库。

**别用 Docker 镜像体积推断二进制大小**——同一批 CI 产物（`fb32657` / 2026-11-02）在容器里只有 9.1 MB：

| 位置 | 文件 | 体积 |
|---|---|---|
| 容器镜像 `apiext-mediakit:latest`（1.17 GB） | `/opt/media/bin/MediaServer` | **9.1 MB** |
| Windows 安装包 | `zlm\MediaServer.exe` | **10.7 MB** |

镜像那 1.17 GB 花在别处（容器内实测）：

| 目录 | 体积 | 内容 |
|---|---|---|
| `/usr/lib/x86_64-linux-gnu` | 536 MB | ffmpeg 依赖全家桶：`libavcodec60`、`libavformat`、`libllvm20`(140 MB)、`mesa-libgallium`(42 MB)、`libicu74` |
| `/usr/share/dotnet` | 106 MB | .NET 运行时 |
| `/opt/media` | 42 MB | ZLM 目录（9.1 MB 的 exe + `www` 前端资源） |
| 其余 | ~490 MB | Ubuntu 24.04 基础系统 + 构建工具链（`gcc-13` 61 MB、`g++-13` 33 MB、`cpp-13` 31 MB、`libpython3.12-dev` 29 MB、`vim-runtime` 36 MB…） |

> 一句话：镜像体积衡量的是「操作系统 + 共享库 + 编译器」，Windows 单文件分发则把库全塞进 exe，两个数字不能互相推断。

### 9.1.2 如何自证打包内容（交付验收用）

```powershell
# 1) 二进制指纹
Get-FileHash C:\apiext-mediakit\zlm\MediaServer.exe -Algorithm SHA256

# 2) 让 ZLM 自报版本（编译日期 / git hash），应与本包记录一致
C:\apiext-mediakit\zlm\MediaServer.exe -v

# 3) 运行时自证：应返回 code=0，且 getApiList 里有 74 个接口
$s = (Select-String C:\apiext-mediakit\zlm\config.ini '^secret=').Line -replace '^secret=',''
(Invoke-WebRequest "http://127.0.0.1:28180/index/api/getApiList?secret=$s" -UseBasicParsing).Content

# 4) 监听地址应为 0.0.0.0（不是某个固定网卡 IP）
netstat -ano | findstr ":28180 :28554 :28335 :28000 :21000"
```

本包实测基线（`Windows_master_2026-11-02`）：

| 校验项 | 期望值 |
|---|---|
| PE 元数据 | 公司名 `Applied ZLMediaKit Informatics Software`，文件说明 `This file is part of the C++ ZLM` |
| `MediaServer.exe -v` | 编译日期 `2026-11-02T07:13:53`，git hash `fb32657` |
| `getApiList` | `code=0`，接口数 **74**；含 `addStreamProxy` / `addFFmpegSource` / `openRtpServer` / `getMediaList` / `getSnap` / `startRecord` / `close_streams` |
| `getServerConfig` | 读到的就是 `zlm\config.ini` 里生成的值（`general.mediaServerId=apiext-mediakit`、`http.port` 等） |
| `netstat` | `28180 / 28554 / 28335 / 28000 / 21000` 全部 `0.0.0.0`（含 `[::]`）LISTENING |

## 10. 与容器版的关键差异

| 项 | 容器版 | Windows 原生 |
|---|---|---|
| 服务托管 | 单容器 entrypoint 拉起两个进程 | 双服务（NSSM）或 应用服务 + ZLM 计划任务 |
| 数据目录 | 卷挂载 `/app/APP_DATA` | `C:\apiext-mediakit\APP_DATA`（绝对路径写入连接串） |
| 录像目录 | 共享 `record` 卷 | `C:\apiext-mediakit\zlm\www\record` |
| 对外地址 | 由 `.env` 的 `PUBLIC_HOST` 注入 | `install.ps1 -PublicHost` 写入配置 |
| 端口 | compose 1:1 映射 | 直连本机端口（数值保持一致，便于互相迁移） |
| 监听地址 | 容器内 `0.0.0.0`，由 compose 端口映射决定可见性 | 全部 `0.0.0.0`（应用 `Urls` 显式指定，ZLM/SIP 默认任意 IP），靠防火墙收口 |
| 体积构成 | 镜像 1.17 GB：ZLM 9.1 MB + ffmpeg 共享库 536 MB + .NET 106 MB + OS/工具链约 490 MB | 安装包 80 MB（压缩后）：.NET 154.9 MB + ffmpeg 100.5 MB + ZLM 10.7 MB，详见 9.1.1 |
| 防火墙 | 宿主/云安全组 | install.ps1 建两条入站规则 |
