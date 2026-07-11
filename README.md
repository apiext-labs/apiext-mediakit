# Apiext.MediaKit

基于 [ZLMediaKit](https://github.com/ZLMediaKit/ZLMediaKit) 的**外部**流媒体管理控制台。

- 本平台**不**承载、转发或长期存储音视频媒体面数据（录像文件仍由 ZLM 落盘）。
- ZLMediaKit 以独立进程/容器运行；本平台通过 **HTTP API** 管理 ZLM，并通过 **Webhook** 接收媒体事件。

---

## 技术栈

| 层 | 技术 |
| --- | --- |
| UI | Blazor Web App (.NET 10)、AntDesign Blazor、AntDesign.ProLayout |
| 宿主 | ASP.NET Core 10、Cookie 登录、Interactive Server / WASM |
| 应用 | ABP 10.x（Module、Repository、Local Event Bus） |
| 数据 | SQLite + EF Core（`App_Data/apiext-mediakit.db`） |
| 集成 | `IHttpClientFactory` → ZLM HTTP API；Webhook 入站 |
| 扩展 | `IMediaKitPlugin` + `IMediaKitPluginRegistry` |

```text
src/
├── Apiext.MediaKit/              # 宿主 + API + 领域 + EF
│   ├── Application/              # 应用服务、ZLM 客户端、鉴权、插件、主机统计
│   ├── Controllers/              # /api/auth、/api/mediakit
│   ├── Domain/                   # MediaServer / PullProxy / PushProxy / ...
│   ├── EntityFrameworkCore/
│   └── Services/                 # Cookie 转发、Host 认证状态
└── Apiext.MediaKit.Client/       # Blazor 页面与前端服务
    ├── Pages/MediaKit/           # 业务页
    ├── Pages/User/Login/         # 管理员登录
    ├── Components/               # 全局头、实例选择器等
    ├── Services/                 # API Client、全局当前实例
    └── wwwroot/                  # css / js / menu.json
```

---

## 目标能力与现状（需求对照）

> 媒体分发能力由 **ZLMediaKit** 提供；本平台负责配置、代理、地址生成与（规划中的）国标/安防接入。  
> 「播放是否免登录」指 **访问 ZLM 上的流地址**，不是访问本管理后台。

| # | 需求 | 现状 | 说明 |
| --- | --- | --- | --- |
| 1 | 直接输出 **RTSP / RTMP / HTTP-FLV / WS-FLV / HLS** 等多种协议流地址 | **✅ 已支持** | 在线流「播放」与 `GET /api/mediakit/streams/play-info` 生成地址（依赖 ZLM 已开启对应协议端口）。另含 fMP4 等。 |
| 2 | **一个流地址**即可观看，**无需登录、无需再调接口** | **✅ 媒体面支持** | 流上线后，浏览器/播放器直接请求 **ZLM** 的 FLV/HLS 等 URL 即可，不经过本平台。管理台登录只保护「配置与控制」。若开启 ZLM `on_play` 流 Token 鉴权，则 URL 需带 `token`（见「安全与鉴权」）；开发环境可放空 token。 |
| 3 | **拉流** RTSP/RTMP，分发为各种流格式 | **✅ 已支持** | 「拉流代理」→ ZLM `addStreamProxy`；转协议由 ZLM +「转协议预设」；按需拉流可配 Webhook。 |
| 4 | **推流** RTSP/RTMP，分发为各种流格式 | **✅ 已支持** | 设备/编码器 **推到 ZLM** 后，本平台在「在线流」可见并生成多协议地址；「推流代理」支持再转推到远端；浏览器可用 **WHIP** 推流。 |
| 5 | **国标 GB28181** 接入 | **❌ 未实现（规划中）** | 目标：海康等国标设备注册/点播 → ZLM 收流转协议 → 网页播。见待办 **A**。 |
| 6 | 海康私有协议 **ISUP 5.0 / 4.0 / 2.0** | **⏳ 远期** | 见待办 **B**。 |
| 7 | 大华私有协议 **设备网络 SDK** | **⏳ 远期** | 见待办 **B**。 |

**免登录观看示例**（流已在 ZLM 上，`app=live`，`stream=cam01`，ZLM 为 `http://192.168.1.10:80`）：

```text
HTTP-FLV:  http://192.168.1.10/live/cam01.live.flv
WS-FLV:    ws://192.168.1.10/live/cam01.live.flv
HLS:       http://192.168.1.10/live/cam01/hls.m3u8
RTMP:      rtmp://192.168.1.10/live/cam01
RTSP:      rtsp://192.168.1.10/live/cam01
```

复制到 VLC / ffplay / 网页播放器即可；**不需要**先调 Apiext 接口。管理台用于创建拉流代理、点播国标（规划中）或查看地址。

---

## 应用场景

面向安防与直播混部：统一接入、统一分发、**浏览器无插件观看**。

| 场景 | 说明 | 现状 |
| --- | --- | --- |
| **浏览器无插件播放摄像头视频** | 管理台内嵌 HLS 预览，或业务系统直接使用 HTTP-FLV / WS-FLV / HLS / WebRTC 等地址；无需 ActiveX / 厂商控件。 | **✅ 已支持**（通用流；国标通道入口见待办 A） |
| **国标设备接入** | 摄像机、NVR、上级/下级国标平台等按 **GB/T 28181** 注册、目录、点播，媒体进 ZLM，网页统一预览。 | **❌ 规划中**（待办 A） |
| **RTSP / RTMP 利旧接入** | 存量 IPC、编码器、第三方直播设备通过 **拉流代理**（拉 RTSP/RTMP）或 **推流到 ZLM**，再分发为 FLV/HLS/RTSP/RTMP 等，充分利旧。 | **✅ 已支持** |
| **一地址直看** | 流上线后，业务方只保存一条播放 URL 即可给终端播，无需登录管理台、无需再调业务接口（可选流 Token）。 | **✅ 媒体面已支持** |
| **多节点运维** | 多套 ZLM 统一在控制台管理、切换实例、看在线流与会话。 | **✅ 已支持** |

典型链路（利旧 RTSP）：

```text
存量摄像机 RTSP ──拉流代理──► ZLMediaKit ──HTTP-FLV/HLS/…──► 浏览器 / App / 大屏
                                    ▲
国标摄像机 / NVR / 平台 ──(规划) SIP 点播──┘
```

---

## 当前功能一览

能力按「媒体管理（已实现）」与「国标/安防（规划中）」划分。本项目当前侧重 **ZLM 外部管理与 Web 播放**，**尚未内置 GB28181 SIP**。

### 1. 已实现 · 媒体节点与运行监控

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 多 ZLM 媒体实例 | ✅ | `/servers`：BaseUrl + Secret（Secret 仅服务端） |
| 全局当前实例 | ✅ | 各页顶部切换，localStorage 记忆 |
| 运行概览 | ✅ | 在线流/观看/拉流代理/TCP、可达性、自动刷新 |
| 管理端主机指标 | ✅ | 本进程 CPU 估算、工作集、托管内存、运行时长 |
| ZLM 在线配置 | ✅ | `getServerConfig` / `setServerConfig` |
| 网络 Session | ✅ | 列表、按 ID 踢人 |

### 2. 已实现 · 流与播放（网页端）

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 在线流列表 | ✅ | 协议/VHost/App/Stream/来源/速率/录制标记、搜索 |
| 网页预览播放 | ✅ | HLS 内嵌、FLV 外开；PlayInfo 多协议地址 |
| 截图 | ✅ | ZLM `getSnap` |
| 观众列表 / 踢人 | ✅ | `getMediaPlayerList` 或 Session 回退 |
| 强制关流 | ✅ | `close_streams` |
| 流播放 Token | ✅ | 签发 + Webhook `on_play`/`on_publish` 校验 |

### 3. 已实现 · 代理 / 推流 / 录制

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 拉流代理 | ✅ | 多备用 URL、按需/立即、预设、编辑、启停删 |
| 按需拉流自动化 | ✅ | Webhook `on_stream_not_found` / `on_stream_none_reader` |
| 转协议预设 | ✅ | HLS/MP4/RTSP/RTMP/TS/fMP4/路径等 |
| 推流代理（转推） | ✅ | `addStreamPusherProxy` / 启停删 |
| 浏览器 WHIP 推流 | ✅ | `/whip` 采集摄像头推到当前 ZLM |
| 录制启停 | ✅ | MP4/HLS `startRecord` / `stopRecord` |
| 录像检索与下载链接 | ✅ | `getMp4RecordFile` + `downloadFile` |

### 4. 已实现 · 安全、事件与扩展

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 管理员 Cookie 登录 | ✅ | 单管理员；业务页需登录 |
| 弱口令启动检查 | ✅ | 开发 Warning；可强制阻断 |
| Webhook 入库与查询 | ✅ | 任意 Hook 名 + 过滤 |
| Webhook Secret | ✅ | query / 请求头校验 |
| 插件注册与列表 | ✅ | `IMediaKitPlugin`；内置 stream-auth、on-demand |

### 5. 控制台页面索引

| 菜单 | 路径 |
| --- | --- |
| 运行概览 | `/` |
| 在线流 | `/streams` |
| 拉流代理 | `/pull-proxies` |
| 推流代理 | `/push-proxies` |
| 在线推流 | `/whip` |
| 网络连接 | `/network` |
| 录制管理 | `/recordings` |
| 媒体实例 | `/servers` |
| 转协议预设 | `/protocol-presets` |
| ZLM 配置 | `/configuration` |
| Webhook 事件 | `/webhooks` |
| 插件 | `/plugins` |
| 安全与鉴权 | `/security` |
| 登录 | `/user/login` |

### 6. 主要 HTTP API（需登录，除标注外）

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/auth/login-form` | 浏览器表单登录（AllowAnonymous） |
| GET | `/api/auth/me` · `/api/auth/logout` | 当前用户 / 退出 |
| * | `/api/mediakit/servers*` | 媒体实例 CRUD |
| GET | `/api/mediakit/dashboard` · `host-stats` | 概览 / 主机统计 |
| * | `/api/mediakit/streams*` | 流、播放信息、观众、关流 |
| * | `/api/mediakit/pull-proxies*` · `push-proxies*` | 拉/推流代理 |
| GET | `/api/mediakit/whip-info` | WHIP 地址 |
| * | `/api/mediakit/protocol-presets*` · `sessions*` · `recordings*` · `configuration` | 预设/会话/录像/配置 |
| GET | `/api/mediakit/webhooks` | 事件列表 |
| POST | `/api/mediakit/webhooks/{eventName}` | Webhook 入站（AllowAnonymous + Secret） |
| GET | `/api/mediakit/plugins` · `security/status` | 插件 / 安全体检 |
| POST | `/api/mediakit/security/stream-token` | 签发流 Token |
| GET | `/health` | 健康检查（SQLite + 可选 ZLM 探测，无需登录） |

---

## 快速开始

### 环境要求

- [.NET SDK 10](https://dotnet.microsoft.com/download) 或更高  
- 可选：已运行的 [ZLMediaKit](https://github.com/ZLMediaKit/ZLMediaKit)（HTTP API 可达）

### 启动

```powershell
dotnet restore
dotnet run --project .\src\Apiext.MediaKit\Apiext.MediaKit.csproj --launch-profile http
```

- 默认监听：`http://0.0.0.0:5082`（见 `Properties/launchSettings.json`）  
- 浏览器打开：`http://localhost:5082/user/login`  
- 默认账号（务必修改）：

| 项 | 默认值 |
| --- | --- |
| 用户名 | `admin` |
| 密码 | `change-me` |

首次启动会创建 SQLite：`src/Apiext.MediaKit/App_Data/apiext-mediakit.db`，并按配置种子默认媒体实例。

### 配置要点（`src/Apiext.MediaKit/appsettings.json`）

```json
{
  "Authentication": {
    "UserName": "admin",
    "Password": "改成强密码",
    "DisplayName": "系统管理员"
  },
  "MediaKit": {
    "DefaultServer": {
      "Name": "Local ZLMediaKit",
      "BaseUrl": "http://127.0.0.1:8080",
      "Secret": "与 ZLM api.secret 一致"
    },
    "WebhookSecret": "改成随机串",
    "StreamAuth": {
      "Enabled": true,
      "TokenTtlMinutes": 60,
      "SigningKey": "改成随机串",
      "AllowEmptyTokenInDevelopment": true
    },
    "Security": {
      "BlockWeakAdminPassword": false
    },
    "Webhook": {
      "AllowedIps": [],
      "RateLimitPerMinute": 120
    },
    "PlayUrl": {
      "PublicHost": null,
      "HttpPort": null,
      "HttpsPort": null,
      "RtspPort": 554,
      "RtmpPort": 1935,
      "ForceHttps": false,
      "IncludeCustomVhostInPath": false
    },
    "Health": {
      "ProbeDefaultZlm": true,
      "ProbeTimeoutSeconds": 3
    }
  }
}
```

生产环境建议：

1. 修改管理员密码、`WebhookSecret`、`StreamAuth:SigningKey`  
2. 视需要将 `BlockWeakAdminPassword` 设为 `true`  
3. 将 `AllowEmptyTokenInDevelopment` 设为 `false`  
4. 配置 `Webhook:AllowedIps`（ZLM 出口 IP 或网段 CIDR）  
5. 对外播放时设置 `PlayUrl:PublicHost` / 端口 / `ForceHttps`  
6. 使用反向代理 + HTTPS  

也可登录后在 **系统管理 → 媒体实例** 中增删改 ZLM 节点（不必只依赖 DefaultServer 种子）。

### Docker Compose

```powershell
docker compose up -d --build
```

- 管理台：`http://localhost:5082/user/login`  
- 健康检查：`http://localhost:5082/health`  
- 数据卷：`apiext-data` → 容器内 `/app/App_Data`  
- 可通过环境变量覆盖密码、Webhook Secret、默认 ZLM 地址等（见 `docker-compose.yml`）

### 发布镜像到 GHCR（手动 · Buildx 多平台）

仓库已配置 GitHub Actions：`.github/workflows/docker-ghcr.yml`（仅 `workflow_dispatch` 手动触发）。

使用 **Docker Buildx** + QEMU 构建并推送 **多架构 manifest**（默认 `linux/amd64,linux/arm64`）。

1. 推送代码到 GitHub 后，打开 **Actions → Docker GHCR → Run workflow**  
2. 可选：是否打 `latest`；`platforms` 默认已是双架构，一般不用改  
3. **版本号自动生成**：`yyMMdd-HHmm`（Asia/Shanghai），无需手动输入  
4. 运行完成后镜像在：

```text
ghcr.io/<owner>/<repo>:260712-1530    # 示例：2026-07-12 15:30（含 amd64 + arm64）
ghcr.io/<owner>/<repo>:latest         # 可选
ghcr.io/<owner>/<repo>:sha-<short>
```

拉取示例（私有包需先登录；Docker 会按本机架构自动选择）：

```bash
echo $GITHUB_TOKEN | docker login ghcr.io -u USERNAME --password-stdin
docker pull ghcr.io/<owner>/<repo>:260712-1530
docker pull ghcr.io/<owner>/<repo>:latest
# 查看多平台 manifest：
docker buildx imagetools inspect ghcr.io/<owner>/<repo>:latest
```

包可见性：GitHub → 仓库 / 组织 **Packages** → 对应包 → Package settings → Change visibility。

---

## 简单使用说明

### 1. 登录并选择实例

1. 打开 `/user/login`，使用管理员账号登录。  
2. 各业务页顶部 **媒体实例** 下拉框选择当前 ZLM（全局记忆）。  
3. 右上角显示当前用户；可进入「媒体实例」或退出登录。

### 2. 接入一台 ZLM

1. 确保 ZLM 已启动，且 `http://<zlm>:80或8080/index/api/getServerConfig?secret=...` 可访问。  
2. **系统管理 → 媒体实例**：填写名称、BaseUrl、Secret。  
3. 打开 **运行概览**：连接状态应为「正常」。

### 3. 拉流并在网页播放

1. **拉流代理**：填写源 RTSP/RTMP 等、App/Stream，选择立即或按需，创建。  
2. **在线流**：刷新列表，对目标流点 **播放**（HLS 内嵌）或 **截图 / 观众**。  
3. 按需模式：无流时由播放触发 `on_stream_not_found`（需正确配置 Webhook）。

### 4. 配置 Webhook（按需/事件）

ZLMediaKit `config.ini` 示例（`secret` 与 `MediaKit:WebhookSecret` 一致）：

```ini
[hook]
enable=1
on_stream_not_found=http://<apiext-host>:5082/api/mediakit/webhooks/on_stream_not_found?serverId=<实例GUID>&secret=<WebhookSecret>
on_stream_none_reader=http://<apiext-host>:5082/api/mediakit/webhooks/on_stream_none_reader?serverId=<实例GUID>&secret=<WebhookSecret>
on_play=http://<apiext-host>:5082/api/mediakit/webhooks/on_play?serverId=<实例GUID>&secret=<WebhookSecret>
on_publish=http://<apiext-host>:5082/api/mediakit/webhooks/on_publish?serverId=<实例GUID>&secret=<WebhookSecret>
on_stream_changed=http://<apiext-host>:5082/api/mediakit/webhooks/on_stream_changed?serverId=<实例GUID>&secret=<WebhookSecret>
```

- 也可用请求头：`X-MediaKit-Webhook-Secret: <WebhookSecret>`  
- 成功响应：`{"code":0,"msg":"success"}`  
- 流 Token 开启时，`on_play` / `on_publish` 无有效 token 将返回 `code=-1`（开发环境可放空，见配置）

### 5. 签发播放 Token（可选）

1. **系统管理 → 安全与鉴权**  
2. 填写 app/stream、用途 play/publish、有效期 → **签发 Token**  
3. 将返回的 `?token=...` 附到播放/推流 URL，供 ZLM hook 回传校验  

### 6. 其它常用操作

| 场景 | 操作 |
| --- | --- |
| 浏览器推摄像头 | **在线推流 (WHIP)**：选实例 → app/stream → 枚举设备 → 开始推流（需 ZLM 开 WebRTC，浏览器能访问 ZLM） |
| 转推到远端 | **推流代理**：源 app/stream + 目标 `rtmp://` 或 `rtsp://` → 启动 |
| 录像 | **录制管理**：选流 → 开始录制 → 按日期检索 → 播放/下载 |
| 改 ZLM 参数 | **ZLM 配置**（部分项需重启 ZLM） |

---

## 待办（Backlog）

状态：`[ ]` 未做 · `[~]` 部分具备 · `[x]` 已完成  

**本项目已完成「ZLM 管理 + Web 播放」侧大部分能力；缺口主要在 GB28181 信令与安防业务面。**

### A. 国标 GB28181（目标：海康等国标设备 → 自有网页播放）

> 目标验收：至少 1 路海康 IPC/NVR 国标在线；网页点预览可出画；停止后无残留点播流；未登录不可播。  
> 标准点播时序：**ZLM `openRtpServer` → SIP Invite → 等流上线 → 返回播放地址 → 网页播 ZLM**。

#### A0. 联调基线

- [ ] 选定 ZLM RTP/国标相关端口与 hook 配置  
- [ ] 选定信令落地方式（二选一或分阶段）：  
  - [ ] **方案 1（推荐先通）**：对接外置国标信令服务 HTTP API（设备/通道/点播/停播），本平台做业务壳 + 播放  
  - [ ] **方案 2**：本平台内嵌/自研 SIP 服务端（工作量大，长期一体化）  
- [ ] 海康设备国标参数联调：注册成功 → 目录可见 → 点播有 RTP  

#### A1. MVP · 设备与实时预览

| 能力 | 待办 | 备注 |
| --- | --- | --- |
| 国标设备注册/在线 | [ ] | 设备上下线状态 |
| 设备/通道目录 | [ ] | 通道树或列表；领域 `GbDevice` / `GbChannel` |
| 实时点播（Invite） | [ ] | 编排 openRtp + Invite + Webhook 确认 |
| 停止点播（Bye） | [ ] | 关 RTP、关流、清会话 |
| 网页无插件预览 | [~] | 播放器已有；缺国标点播入口页 |
| 播放地址下发 | [~] | PlayInfo 已有；需绑定国标会话 |
| 多媒体节点选路 | [~] | 多 ZLM 已有；缺按通道绑定收流节点 |

具体任务：

- [ ] 领域与表：`GbPlatform` / `GbDevice` / `GbChannel` / `GbPlaySession`  
- [ ] API：`StartGbPreview` / `StopGbPreview` / 通道列表  
- [ ] 页面：**国标设备 / 通道** → 预览 / 停止（复用 Streams 播放组件）  
- [ ] 会话回收：关页、超时、无人观看停流  

#### A2. 录像与回放

| 能力 | 待办 |
| --- | --- |
| 设备侧录像检索 | [ ] |
| 国标历史回放点播 | [ ] |
| 回放拖拽 / 暂停 / 倍速（按设备能力） | [ ] |
| 平台侧（ZLM）云端录像 | [~] 启停与文件列表已有；缺策略与日历 UI |
| 录像下载 | [~] download 链接已有；缺业务封装 |

#### A3. 控制与事件

| 能力 | 待办 |
| --- | --- |
| 云台 PTZ | [ ] |
| 预置位 | [ ] |
| 告警订阅 / 通知 | [ ] |
| 语音喊话 / 广播 | [ ] |
| 移动位置（可选） | [ ] |

#### A4. 级联与平台互联

| 能力 | 待办 |
| --- | --- |
| 上级平台注册（本平台当下级） | [ ] |
| 下级平台管理（本平台当上级） | [ ] |
| 目录共享 / 推送 | [ ] |
| 跨域 NAT 场景文档与配置模板 | [ ] |

#### A5. 国标运维与安全

- [ ] 目录同步 Job、通道在线率大盘  
- [ ] 点播失败诊断（未注册 / Invite 失败 / 无 RTP / 编码不兼容）  
- [ ] 预览/回放操作审计  
- [ ] 国标播放走流 Token / 有效期 URL  

### B. 非国标 / 厂商私有接入

- [ ] RTSP 通道向导 → 一键创建拉流代理（快速上线，非国标）  
- [ ] 海康 OpenAPI 目录/预览（客户不走国标时）  
- [ ] **远期** 海康私有协议 **ISUP 5.0 / 4.0 / 2.0** 接入（需官方/SDK 与网络可达）  
- [ ] **远期** 大华私有协议 **设备网络 SDK** 接入  
- [ ] Provider 抽象：`Gb28181` / `Rtsp` / `OpenApi` / `HikIsup` / `DahuaSdk`  

### C. 媒体与平台增强（非国标）

- [ ] 录像日历、按保留策略清理、顺序回放  
- [ ] 分屏预览（2/4/9/16）  
- [ ] 插件参数配置 UI / 启用开关  
- [ ] FFmpeg 转码任务  
- [ ] 视频质量探针  
- [ ] 多用户与细粒度权限（查看 / 预览 / 控制 / 配置）  
- [ ] Prometheus 或节点 Agent 指标  

**推荐实施顺序：** **A0 联调** → **A1 国标网页预览 MVP** → A2 回放 → A3 控制事件 → A4 级联。

---

## 插件扩展（开发者）

实现 `IMediaKitPlugin`，在模块初始化时注册到 `IMediaKitPluginRegistry`（也可 `IMediaKitPlugin` DI + 启动扫描注册）：

```csharp
public sealed class MyPlugin : IMediaKitPlugin
{
    public string Name => "my-plugin";
    public string Description => "示例";
    public bool Enabled => true;

    public Task OnWebhookAsync(WebhookReceivedEto eventData, CancellationToken cancellationToken = default)
    {
        // 处理 on_record_mp4、on_stream_changed 等
        return Task.CompletedTask;
    }
}
```

Webhook 入口只负责：Secret 校验、（可选）流 Token 决策、持久化、事件发布；业务在插件或应用服务中完成。

---

## 构建

```powershell
dotnet build .\src\Apiext.MediaKit\Apiext.MediaKit.csproj -c Debug
```

若提示 DLL 被占用，请先停止正在运行的 `Apiext.MediaKit` / Visual Studio 调试进程。

---

## 许可证与声明

- 本仓库业务代码请遵循项目既有许可约定。  
- ZLMediaKit、Ant Design Blazor、ABP 等第三方组件遵循其各自许可证。  
- 国标/安防对接涉及现场许可、网络安全与隐私合规，请在合法授权范围内使用。
