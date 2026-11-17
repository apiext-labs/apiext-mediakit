# Gb28181 目录

GB/T 28181（国标）设备接入：SIP 信令引擎、设备注册/心跳、目录/录像查询、实时点播/回放、云台与报警。

> 平台作为 **SIP 服务器（上级/平台侧）** 接收下级设备注册；**信令面**由本目录的 SIP 引擎处理，
> **媒体面**由 ZLMediaKit 的 RTP 接口承载。

## 文件说明

| 文件 | 职责 |
|---|---|
| `GbSipEngine.cs` | SIP 信令引擎（`BackgroundService`）：UDP 监听、注册/心跳、Digest 鉴权、INVITE/BYE/INFO |
| `Gb28181Service.cs` | 业务编排：设备/通道查询、目录查询、实时点播、回放、云台、报警、预登记设备（白名单）与接入模式读写 |
| `GbCascadeEngine.cs` | 平台级联引擎：向多个上级平台注册、推送目录、响应上级点播/云台/停止（RTP 转发）；上级平台列表持久化于 DB，周期/即时热重载 |
| `GbPlayWatchdog.cs` | 无人观看自动断流：周期检查 ZLM 读者数，超时自动停止点播 |
| `../Controllers/Gb28181Controller.cs` | GB28181 REST API（JWT Bearer 鉴权）；云台/校时的访问令牌入口见 `../Controllers/Gb28181IntegrationController.cs` |
| `GbDigest.cs` | SIP Digest 鉴权（RFC2617，MD5） |
| `GbXmlCodec.cs` | 目录（Catalog）XML 编解码 |
| `GbRecordCodec.cs` | 录像查询（RecordInfo）XML 编解码 |
| `GbAlarmCodec.cs` | 报警（Alarm）XML 编解码 |
| `GbSdpCodec.cs` | 点播/回放 SDP 编解码 |
| `GbPtz.cs` | 云台控制：PTZ 命令 + DeviceControl XML |

## 关键实现点

### GbSipEngine（SIP 信令引擎）

- 基于 **SIPSorcery** 库（`SIPSorcery.SIP`），单例 + `HostedService` 常驻运行。
- 监听 **UDP 28060**（默认，本地开发为 5060），处理设备 `REGISTER`（注册）、`MESSAGE`（心跳/目录/录像/报警响应）。
- **Digest 鉴权**：无 `Authorization` 回 401 挑战，有则校验 `response`（`auto` 模式受开关 `EnableAuth` 控制，`whitelist` 模式总是鉴权，见下）。
- **接入模式（双模式）**：注册时 `CheckAccessAsync` 读取 `AppSetting` 键 `gb28181.access.mode`（热生效）判定：
  - `auto`（默认）：注册即接入，是否鉴权由 `EnableAuth` 决定，密码用全局 `Password`；
  - `whitelist`：预登记 + 白名单鉴权——`GbPresetDevice` 表按国标 ID 匹配，未登记回 `403 Forbidden`，已登记用独立密码（留空回退全局密码）鉴权，落库名称用预登记名称。
- **主动会话**：点播/回放发 `INVITE`，等设备 `200 OK` 后回 `ACK`；停止发 `BYE`；回放控制发 `INFO`（PAUSE / PLAY MANSRTSP）。
- 内部状态用 `ConcurrentDictionary` 维护：在线设备、会话、以及各类等待响应的 `TaskCompletionSource`。
- **媒体接收地址**：SDP 的 `c=` 行必须是「**设备可达 + RTP 收流端口所在主机（ZLM）**」的地址。
  `Gb28181:MediaIp` 留空时回落到 `LocalIp`，再回落到自动探测的**应用本机 IP**——应用与 ZLM 分离部署时这会指向错误主机，
  表现为 `INVITE` 200 OK 但 ZLM `getMediaList` 查不到流（黑屏）。分离部署请把 `MediaIp` 显式配成 ZLM 主机对设备可达的 IP。

### 配置（`appsettings.json` 的 `Gb28181` 段）

| 键 | 默认值 | 说明 |
|---|---|---|
| `SipPort` | 5060 | SIP 信令 UDP 端口 |
| `SipId` | 34020000002000000001 | 平台 SIP 编码 |
| `SipDomain` | 3402000000 | 平台域 |
| `Realm` | 同 SipDomain | Digest 鉴权 realm |
| `Password` | My6moOWiyMoz5yJv | 鉴权密码（设备统一接入密码） |
| `HeartbeatTimeout` | 300 | 心跳超时（秒） |
| `EnableAuth` | true | `auto` 模式下是否开启 Digest 鉴权（`whitelist` 模式忽略此开关，总是鉴权） |
| `LocalIp` | 自动探测 | 平台本机 IP |
| `MediaIp` | 同 LocalIp | 设备推流目标地址（ZLM） |

### 业务编排（Gb28181Service）

- 设备列表：DB 记录 + 内存在线状态合并（`GbDeviceRow`）。
- 目录查询：发 SIP `MESSAGE` 目录查询并等待设备回传（**先登记等待槽再发送**，否则设备秒回时会丢响应误判超时），收到 `Catalog` 响应立即按设备编码落库通道，并记录条目数日志（0 条也记，便于排查设备不支持目录查询）。
- 实时点播：ZLM 开 RTP 端口 → SIP `INVITE` → 返回播放地址（HTTP-FLV）。
- 多协议播放地址：`GetPlayUrls` 返回 RTSP / RTMP / HLS / FLV / WS-FLV（`PlayAddresses`）。
- 录像查询/回放：`RecordInfo` 查询 → 回放 `INVITE`（`t=` 为起止 Unix 秒）→ `INFO` 控制。
- 云台：`DeviceControl` MESSAGE（见 `GbPtz`）；网络校时下发 `TimeSync` 指令（`TimeSyncAsync`）。
- 主/子码流切换：同设备通道（`GetSiblingChannelsAsync`）间切换。
- 无人观看自动断流：`GbPlayWatchdog` 周期检查 ZLM 读者数，无观众超时自动停（`IdleTimeout`），级联流不参与。

### 实时点播与排障

点播链路：`openRtpServer`（ZLM 开收流端口）→ `INVITE`（SDP 带 `y=`/rtpmap）→ 等 `200 OK` → `ACK` → 设备向 `c=` 地址推 RTP。

| 现象 | 常见原因 | 处理 |
|---|---|---|
| 点播超时 | 设备没回 `200 OK` | 看后端日志 `[GB28181] 点播收到非 200 应答 <状态码>`；500 多为 SDP 不合规，4xx 多为通道不可用/鉴权问题 |
| 点播成功但黑屏 | `MediaIp` 指错主机，或设备到该地址不通 | 该地址必须是 ZLM 主机；放行 `30000-30500/udp`；用 ZLM `getMediaList` 确认有 `app=rtp&stream=<通道号>` |
| 设备显示离线（重启平台后） | 内存注册状态丢失 | 设备心跳（`MESSAGE KEEPALIVE`）会凭 DB 记录恢复在线（最多一个心跳周期）；否则等设备重发 `REGISTER` |
| 查询目录超时但通道数变了 | 早期竞态（已修） | 刷新页面即可；若仍超时且通道数为 0，抓包确认设备是否回 `Catalog` |

### 平台级联（GbCascadeEngine）

本平台可作为**下级域**向**一个或多个**上级国标平台（如上级监控中心）注册，实现上下级域互联：

- **注册**：作为 UAC 向每个上级周期 `REGISTER`（Digest 鉴权，自动处理 401 挑战并带 `Authorization` 重发）。
- **目录推送**：注册成功后自动推送，并响应上级目录查询（`Catalog`）。
- **响应点播**：上级 `INVITE` → 查通道 → 向设备点播 → **ZLM `startSendRtp` 把 RTP 转发到该上级收流地址**。
- **响应停止/云台**：上级 `BYE` 停止；上级 `DeviceControl` 转发 PTZ 指令给设备。
- 复用 `GbSipEngine` 的 SIP 传输层（同一 UDP 端口），通过 `ExternalPeerFilter` 区分上级请求与设备请求；每个上级独立的注册状态（`nonce`/`CallId`/`CSeq`/`Registered`）。

#### 级联配置（`Gb28181:Cascade` 段）

| 键 | 默认值 | 说明 |
|---|---|---|
| `Enabled` | false | 是否启用级联 |
| `LocalId` | — | 本平台作为下级的编码（必填） |
| `RegisterInterval` | 60 | 注册周期（秒） |
| `ReloadInterval` | 5 | 上级平台列表同步周期（秒） |

> 上级平台列表**不写配置文件**，而是持久化在数据库（`CascadeUpper` 实体 / `CascadeUppers` 表），
> 通过 REST API 动态增删改；`GbCascadeEngine` 每 `ReloadInterval` 秒（或 API 增删改后立即）从 DB diff 到运行时状态并热生效。

上级平台字段（`CascadeUpper`）：

| 字段 | 默认值 | 说明 |
|---|---|---|
| `Name` | — | 上级平台名称（展示用） |
| `Ip` | — | 上级平台 SIP 地址（必填） |
| `Port` | 5060 | 上级平台 SIP 端口 |
| `PlatformId` | — | 上级平台编码（必填） |
| `Domain` | — | 上级平台域 |
| `Realm` | — | 上级鉴权 realm（可空，优先用 401 返回的 realm） |
| `Password` | admin123 | 向上级注册的鉴权密码 |

#### 级联管理入口

- **页面**：「高级配置」页（`Components/Pages/Advanced.razor`，路由 `/advanced`）→「国标级联」Tab，查看/新增/编辑/删除上级平台（调用 `Gb28181Service` 的 `Get/Add/Update/DeleteCascadeUpperAsync`），另有「端口清单」Tab 收纳防火墙放行说明。设备列表页不再提供级联入口，统一从该页操作。
- **REST API**：`/api/gb28181/cascade/uppers` 系列端点（见下文 API 表），供独立客户端调用。

### 编解码器

- `GbDigest`：nonce 生成、`WWW-Authenticate` 挑战构造、无 qop 形态的 Digest `response` 计算、`Authorization` 解析。
- `GbXmlCodec` / `GbRecordCodec` / `GbAlarmCodec`：分别处理 Catalog / RecordInfo / Alarm 的 XML 构造与解析。
- `GbSdpCodec`：点播/回放 INVITE 的 SDP 构造（平台作为接收方，`a=recvonly`），以及 `y=` 字段（SSRC）解析。
  `m=video` 行必须带 `RTP/AVP 96 97 98 99` 与对应 `a=rtpmap`（`PS`/`MPEG4`/`H264`/`H265`，90000）以及 `f=` 行——
  缺 rtpmap 时不少设备（含国标模拟器）会直接回 `500 Internal Server Error`。
- `GbPtz`：`PtzCommand` 枚举（上/下/左/右/放大/缩小等）+ 国标 `DeviceControl` XML 编码。

### REST API（Controllers/Gb28181Controller）

以 `/api/gb28181` 为前缀，全部走 **JWT Bearer 鉴权**（先 `POST /api/auth/login` 换 token，请求头带 `Authorization: Bearer <token>`）。
例外：`ptz` / `timesync` 面向第三方集成，用**访问令牌**（`?token=` 或 `Authorization: Bearer <访问令牌>`），由 `Controllers/Gb28181IntegrationController.cs` 承载。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/gb28181/devices` | 设备列表 |
| GET | `/api/gb28181/devices/{deviceId}/channels` | 通道列表 |
| POST | `/api/gb28181/devices/{deviceId}/catalog` | 触发目录查询 |
| POST | `/api/gb28181/channels/{channelId}/play` | 实时点播（返回播放地址） |
| DELETE | `/api/gb28181/channels/{channelId}/play` | 停止点播 |
| POST | `/api/gb28181/channels/{channelId}/ptz` | 云台控制（body `{command,param}`） |
| GET | `/api/gb28181/channels/{channelId}/records?start=&end=` | 录像查询 |
| POST | `/api/gb28181/channels/{channelId}/playback` | 回放点播（body 为 `GbRecordItem`） |
| POST | `/api/gb28181/playback/{streamId}/control` | 回放控制（body `{rangeTime,pause}`） |
| DELETE | `/api/gb28181/playback/{streamId}` | 停止回放 |
| GET | `/api/gb28181/alarms` | 报警列表 |
| POST | `/api/gb28181/channels/{channelId}/alarm/subscribe` | 订阅报警 |
| GET | `/api/gb28181/cascade/status` | 级联状态（enabled/registered/上级平台参数） |
| GET | `/api/gb28181/cascade/uppers` | 上级平台列表（持久化记录） |
| POST | `/api/gb28181/cascade/uppers` | 新增上级平台（body 为 `CascadeUpper`） |
| PUT | `/api/gb28181/cascade/uppers/{id}` | 更新上级平台 |
| DELETE | `/api/gb28181/cascade/uppers/{id}` | 删除上级平台 |

- 云台 `command` 为 `PtzCommand` 枚举名（大小写不敏感，如 `Up` / `ZoomIn` / `GotoPreset`）。
- JWT 鉴权在 `Program.cs` 通过 `AddJwtBearer("Bearer")` + 显式 `"Bearer"` 授权策略落地（依赖 `Microsoft.AspNetCore.Authentication.JwtBearer` 包）。

## 依赖关系

- 本目录位于 `Apiext.MediaKit.Server.Gb28181` 命名空间。
- 依赖库：**SIPSorcery**（SIP 协议栈）；JWT 鉴权依赖 `Microsoft.AspNetCore.Authentication.JwtBearer`。
- `GbSipEngine`、`GbCascadeEngine` 在 `Program.cs` 注册为单例 `HostedService`；`Gb28181Service` 为 Scoped 服务，注入 `AppDbContext`、`ZlmService`、`GbSipEngine`、`GbCascadeEngine`。
- REST API 由 `Controllers/Gb28181Controller.cs` 承载（`app.MapControllers()` 挂载），类级 `[Authorize(Policy = "Bearer")]`。
- 页面入口为设备列表（`/devices`）的 GB28181 详情**抽屉** `Components/DeviceDetail/GbDetail.razor`（内含通道查询、实时播放、地址、回放、云台、抓拍）；预登记（白名单）操作在「设备列表 → 添加 → GB28181」页签，接入模式切换在「高级配置 → SIP 接入」。持久化实体在 `Persistence/`（`GbDevice` / `GbChannel` / `GbAlarm` / `CascadeUpper` / `GbPresetDevice`）。
