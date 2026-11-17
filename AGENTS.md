# Apiext MediaKit — 开发者手册

> 面向**参与本项目开发的人类和 AI 代理**：怎么把环境跑起来、代码放在哪、改一个功能要动哪些文件、有哪些必须遵守的约定和已经踩过的坑。
> 只想知道项目是做什么的、怎么试用，看 [`README.md`](README.md)；只关心部署运维，看 [`deploy/README.md`](deploy/README.md)。修改代码前先读本文第 2～5 节。

---

## 1. 文档分工

| 文档 | 读者 | 回答的问题 |
|---|---|---|
| `README.md` | 外部读者 / 使用者 | 项目是什么、能做什么、怎么试用、发布物在哪 |
| `AGENTS.md`（本文） | 开发者 / AI 代理 | 怎么开发、代码放哪、约定与踩坑 |
| `deploy/README.md` | 交付 / 运维 | 两种形态怎么选、最短路径、端口、文档导览 |
| `deploy/docker/README.md` | 运维 | 容器部署细节、国标、WebHook、升级回滚、排障 |
| `deploy/windows/README.md` | 打包 / 现场实施 | Windows 打包、安装、升级、卸载、体积与许可 |

**改动后同步文档的规矩**：改端口/环境变量 → 同步 `deploy/docker/{docker-compose.yml,.env.example,install.sh,config.ini,README.md}`；改交付流程 → 同步 `.github/workflows/` 与 `deploy/README.md`；改项目约定 → 只改本文。

---

## 2. 先把项目跑起来

### 2.1 环境

- .NET **10** SDK（10.0.101 起），VS 2022 17.13+ / Rider / VS Code 均可；解决方案是 `Apiext.MediaKit.slnx`（XML slnx，SDK 9+）。
- 一个可访问的 **ZLMediaKit**：本地最快 `docker compose -f deploy/docker/docker-compose.zlm.yml up -d`（只起 ZLM，端口 28180），或指向已有 ZLM。
- 可选：Docker（构建镜像）、NSIS（打 Windows 安装包）、Git（版本管理）。

### 2.2 启动

```bash
dotnet build Apiext.MediaKit.slnx
cd src/Apiext.MediaKit.Server
dotnet run --launch-profile http        # http://0.0.0.0:5080，默认 admin / admin123
```

- 本机 ZLM 地址与 secret 写在 `src/Apiext.MediaKit.Server/appsettings.Development.json`（`ZlMediaKit:BaseUrl` / `Secret`），**不要写进 `appsettings.json` 提交**。
- 国标开发还需要设备可达的 SIP / 媒体地址：`Gb28181:LocalIp` / `MediaIp` 显式配置，别依赖自动探测（见 7.3）。
- 启动时 `[Startup]` 会打印环境、监听地址、ZLM/播放基址、SIP 编码、DB 路径；先看这行确认「连的是哪个 ZLM、哪个库」。
- SQLite 连接串是相对路径 `Data Source=APP_DATA/apiext-mediakit.db`，启动自动建目录；`APP_DATA/` 与 `*.db*` 均被 gitignore。

### 2.3 调试开关

```bash
Logging__LogLevel__Apiext.MediaKit.Server=Debug   # ZLM 每次调用都会记 Debug
Logging__LogLevel__Default=Warning
```

日志走 `ILogger` 输出到 stdout，前缀固定：`[Startup]` `[Request]` `[Token]` `[PlayToken]` `[ZLM]` `[PlayProxy]` `[PullProxy]` `[Onvif]` `[GB28181]` `[Cascade]` `[Hook]` `[OnDemand]` `[Reconcile]` `[RecordSchedule]` `[Retention]` `[Backup]`。新增模块沿用英文方括号，便于 `grep`。

---

## 3. 代码地图与分层

```
Apiext.MediaKit/
├─ Apiext.MediaKit.slnx
├─ src/Apiext.MediaKit.Server/         # 单项目：Blazor 页面 + Web API + 后台服务（同进程）
│  ├─ Program.cs                       # 启动自检、UseWindowsService、AddApiextMediaKit / UseApiextMediaKit
│  ├─ Extensions/                      # AddApiextMediaKit（DI 注册）/ UseApiextMediaKit（中间件管道、Swagger）
│  ├─ Controllers/                     # **全部 HTTP API 入口**（MVC Controller）
│  ├─ Contracts/                        # 对外 API 请求/响应 DTO
│  ├─ Apis/                             # IZlMediaKitApi 声明式接口 + ZlmModels
│  ├─ Services/                         # 业务服务（Zlm / PullProxy / DeviceList / Onvif / Recording* / PlayToken / ZlmHook / ...）
│  ├─ Persistence/                     # AppDbContext + 实体 + DbInitializer（含 PasswordHasher）
│  ├─ Auth/                             # JwtTokenService / AuthService / AccountService / ApiTokenAuth
│  ├─ Gb28181/                          # SIP 引擎、级联引擎、编解码（REST 入口在 Controllers/）
│  ├─ Onvif/                            # SOAP 客户端 + WS-Discovery（目录内有 README）
│  ├─ Components/                      # App/Routes、Layout、Pages、DeviceDetail、Advanced、PtzPad
│  └─ wwwroot/                          # css/theme.css、logo.svg、js/auth.js、js/player.js、jessibuca
├─ deploy/
│  ├─ README.md                        # 部署总览
│  ├─ docker/                           # Dockerfile、compose ×2、install.sh、config.ini、.env.example、README.md
│  └─ windows/                          # publish-app / fetch-payload / build-installer / installer.nsi / install / uninstall / README
└─ .github/workflows/                   # docker-image.yml、windows-installer.yml
```

### 分层约定（必须遵守）

- 命名空间统一 `Apiext.MediaKit.Server.*`，按目录分层（`.Controllers` / `.Contracts` / `.Apis` / `.Services` / `.Persistence` / `.Auth` / `.Components`）。
- **HTTP 接口一律写成 MVC Controller，放 `Controllers/`**，由 `Program.cs` 的 `AddControllers()` + `app.MapControllers()` 挂载；不要再新增 `XxxEndpoints.cs`（minimal API `Map*` 已全部移除）。Controller 只做参数校验 + 调 `Services/`，不写业务逻辑。
- 请求/响应 DTO 放 `Contracts/`；领域服务内部模型留在各层目录。DTO/模型不单独建 `Shared` 项目（InteractiveServer 下前后端同进程，无跨边界需求）。
- 鉴权统一走特性：JWT 用 `[Authorize(Policy = "Bearer")]`；第三方访问令牌用 `[ApiTokenAuth]`（`Auth/ApiTokenAuthAttribute.cs`）。
- 数据访问层目录名用 `Persistence`，**不要用 `Data`**（与运行时 `data`/`APP_DATA` 在 Windows 大小写不敏感下冲突）。
- 系统级键值设置存 DB 的 `AppSetting`（`AppSettings` 表，Key 唯一），经 `AppSettingsService` 读写，不要往 `appsettings.json` 塞业务数据。初始化向导完成标记为 `wizard.completed`；改密码统一走 `AccountService.ChangePasswordAsync`。

### 常见开发任务

| 任务 | 要动的地方 |
|---|---|
| 新增一个 REST 接口 | `Contracts/` 加 DTO → `Controllers/` 加 Action（校验 + 调服务）→ `Services/` 写逻辑 → 按需加 `[Authorize]` / `[ApiTokenAuth]` |
| 新增一个页面 | `Components/Pages/` 加 `.razor` → `_Imports.razor` 确认 using → 菜单在 `BasicLayout.razor` → 样式只写 `wwwroot/css/theme.css` |
| 加一张表 / 加字段 | 实体 + `AppDbContext` + `DbInitializer` 的 `CREATE TABLE IF NOT EXISTS` / 幂等 `ALTER TABLE ADD COLUMN`（旧库必须能直接升级，无需手工迁移） |
| 加一个配置项 | `appsettings.json` 给默认值 → 需要环境相关的留空并走 `NetworkInfo.DetectOutboundIPv4()` → 同步 `deploy/docker/docker-compose.yml`、`.env.example`、`install.sh` |
| 改端口 | `deploy/docker/{.env.example,docker-compose.yml,config.ini,install.sh,README.md}` 五处同步 + `deploy/windows/install.ps1` 模板 |
| 加 ZLM 调用 | `Apis/IZlMediaKitApi.cs` 声明 → `Services/ZlmService.cs` 包 `GuardAsync`（含 30 秒节流）→ 核对接口名用 `/index/api/getApiList?secret=` |
| 调 WebHook | `deploy/docker/config.ini` 的 `?cap=` 按 7.1 算法重算；本地用 `curl` 直接打 `/api/zlm/hook/{event}` |

---

## 4. UI 与交互规范

**定位：简约、素雅、克制留白。** 主色钢青灰蓝 `#4a6b8a`（hover `#5d7f9e` / active `#3a5570`），背景 `#f5f6f8`，文字主 `#2d3748` / 次 `#718096` / 弱 `#9aa5b1`；侧边栏 ProLayout dark + 固定顶栏，卡片白底圆角 8px + 柔和阴影，Logo 为 `wwwroot/logo.svg`。主题只改 `wwwroot/css/theme.css` 里的 `--ant-primary-*` 变量（基于 `ant-design-blazor.variable.css`），不要改 AntDesign 预设色。

**优先用 Ant Design Blazor 内置组件，不自造轮子**：数据展示 `Table` / `Descriptions` / `Statistic` / `Card` / `Tag` / `Badge` / `Timeline`；录入 `Form` / `Input` / `InputPassword` / `InputNumber` / `Select` / `Switch` / `DatePicker` / `Upload`；反馈 `Message` / `Notification` / `Modal` / `Popconfirm` / `Drawer` / `Spin` / `Empty` / `Result` / `Alert`；布局 `Row` / `Col` / `Space` / `Divider` / `Tabs` / `Menu`；图标用内置 `Icon`。全局反馈依赖 `Routes.razor` 的 `<AntContainer />`。

### AntDesign Blazor 1.6.2 语法坑

- 枚举属性必须显式枚举表达式：`Type="@ButtonType.Primary"`、`Size="@InputSize.Large"`、`Type="@AlertType.Error"`、`Direction="@SpaceDirection.Vertical"`；裸字符串 `Type="primary"` 直接编译报错。
- 布尔属性不能空值：`ShowIcon="true"`，不是 `ShowIcon`。
- 与 HTML 同名的组件必须全限定：`<AntDesign.Row>`、`<AntDesign.Col>`、`<AntDesign.Column>`。
- Table 列绑定 `@bind-Field="@context.Xxx"`（`Column.Field` 的 CS0612 过时警告属库内部弃用，忽略）。
- 密码框用 `InputPassword`，回车 `OnPressEnter`。
- **Table 数据源禁止直接用 `string`**（`Table<string>` 静态构造器对 string 调 `GetUninitializedObject` 抛异常，整个 circuit 崩溃），一律用包装类（见 `PullProxyDetail.razor` / `RecordPlayback.razor` 的 `RecordFileRow`）。
- `Drawer` / `Modal` 的 `Title` 是 `OneOf<string, RenderFragment>`，裸字符串报错，写 `Title="@("...")"`；页面 `@inject` 属性名不能与组件类同名（`Advanced.razor` 注入 `IConfiguration` 用 `Config`）；`Dropdown`/`Popover` 触发元素包 `<ChildContent>`，内容放 `<Overlay>` / `<ContentTemplate>`；`Menu` 点击事件是 `OnMenuItemClicked`（`EventCallback<MenuItem>`），**没有** `OnClick`；`<Text Type=...>` 写在 `@if`/`@else` 直接体内会触发 RZ1023，改用 `<span style="...">`；`Tag` 的 `Color`、`Popover` 的 `Placement` 要写 `@("orange")` / `@Placement.RightTop` 显式表达式。

### 交互约定

- **三态齐全**：列表/详情都要有加载态、空态、失败提示；接口失败禁止静默置空（会伪装成「暂无数据」），至少 `Message.Error` 或页面内 `Alert`。
- **危险操作确认**：删除设备/令牌、断流、跳过安全设置一律 `Popconfirm`。
- **样式归位**：全部写 `wwwroot/css/theme.css`，不要在 `.razor` 内联 `<style>`（只在组件首次渲染时生效，别的组件引用同名 class 会丢样式）。
- **云台 UI 统一**用 `Components/PtzPad.razor`，按能力开 `ShowDiagonal` / `ShowFineControls`（ONVIF 两者都关，只留上下左右与变倍），不要另画方向键。
- **播放器接入统一**：`apiextPlayer.create(id, containerId, dotNetRef, codec)` 第三个参数（`DotNetObjectReference`）必须传，页面实现 `[JSInvokable] public Task OnPlayerError(string id, string message)` 并在 `Dispose` 释放；不传则播放失败只有黑屏、无诊断信息。
- **按线上实际编码播放**：`ZlmService.GetVideoCodecAsync(vhost, app, stream)` 从 `getMediaList` 的 `tracks[].codec_id_name` 取（`codec_type=0` 为视频；兜底枚举 ZLM 是 `0=H264 / 1=H265`，**不是** FLV/Jessibuca 的 7/12）。`H265` → `useMSE/useWCS=false` + `videoBuffer=0.5`（WASM 软解），`H264` → MSE/WebCodecs 硬解，未知 → 默认 MSE + `autoWasm`。国标点播流在 ZLM 侧固定 `rtp/{通道号}`。H.265 是软解，16 分屏吃 CPU，别承诺满屏 H.265。
- **轮询统一**：`PeriodicTimer` + `CancellationTokenSource`，页面实现 `IDisposable` 取消；轮询 silent（不闪 loading）；手动刷新与轮询用 `_busy` 互斥；标签页隐藏（`document.visibilityState === 'hidden'`）时跳过请求。
- **响应式**：栅格 `Xs="24" Sm="12" Lg="6"`，不要固定 `Span="6"`。
- 页面分层：不常用功能收纳 `/advanced`（Tabs：国标级联 / SIP 接入 / 端口清单 / ZLM 配置 / 录像备份 / 访问令牌）；`/devices` 是唯一设备入口（`DeviceListService` 聚合三类设备，详情走 `Components/DeviceDetail/` 抽屉），**没有**独立协议管理页；流添加对话框按协议切三个页签，流 ID 留空时自动从地址提取，GB28181 页签没有「确定」按钮（设备靠主动注册）。

---

## 5. 日志、配置与开箱即用

- **失败必留痕**：禁止 `catch { }` 静默吞异常（按需拉流曾因此变成排查黑洞），至少 `LogDebug(ex, ...)`，影响用户操作的用 `LogWarning`。
- **结构化参数**：命名占位符 `{App}` `{Stream}` `{Device}`，不要字符串拼接；异常走 `LogWarning(ex, ...)` 重载。
- **脱敏**：`secret`、访问令牌、播放令牌、源流地址账号密码不能进日志——用 `ZlmService.SafeUrl` / `SafeSourceUrl`，令牌只打前 6 位；`BuildSnapUrl` 这类含 secret 的 URL 禁止记录。
- **节流**：ZLM 失败日志 30 秒窗口节流（`ZlmService.NoteFailure`）；轮询/高频路径只记 `LogDebug`，成功路径不刷 `LogInformation`。
- **环境相关项不写死**：`ZlMediaKit:BaseUrl/PlayBaseUrl/PublicHost`、`Gb28181:LocalIp/MediaIp` 一律留空或通用值，由 `NetworkInfo.DetectOutboundIPv4()`（默认路由网卡优先）探测；机器实际值放 `appsettings.Development.json` 或环境变量。容器里探测到的是 `172.x`，必须由 `deploy/docker/.env` 的 `PUBLIC_HOST` 注入 `ZlmMediaKit__PublicHost` / `Gb28181__LocalIp` / `Gb28181__MediaIp`（compose 缺失会直接报错，不静默用错地址）。
- **默认值可开箱用但必须自检**：ZLM secret、Hook 能力令牌、播放令牌密钥、SIP 密码允许内置默认值，`Program.cs` 启动时 `[Startup]` 打印 Warning 提示更换，不阻断启动。
- ZLM secret 统一 `7f3a9c2e-1b4d-4e8f-9a5c-3d6e2b7a1c8f`（appsettings、`deploy/docker/config.ini`、compose 环境变量一致）。**坑**：`035c73f7-bb6b-4889-a715-d9eb2d1925cc` 是 ZLM 内置默认 secret，启动时会自动轮换并写回 config.ini，用它永远 `Please login first`。

---

## 6. 认证与播放

- **Cookie**：登录 `SignInAsync` 写 `.AspNetCore.Cookies`，页面 `[Authorize]` + `AuthorizeRouteView`。
- **JWT**：登录额外签发 JWT 供独立 API Bearer 鉴权。单管理员，默认 `admin / admin123`（`appsettings.json` 的 `Admin` 段），PBKDF2 散列存储。
- **关键坑**：InteractiveServer 下登录/登出必须由浏览器 JS `fetch` 发起（`wwwroot/js/auth.js` 的 `apiextAuth.login/logout`），不能走服务端 HttpClient，否则 Cookie 到不了浏览器；`AuthService` 通过 `IJSRuntime` 调 JS。
- **访问令牌**（`?token=`）：第三方集成，用于 `/play/*` 播放代理、`/api/v1/*`、`/api/onvif/*` 和部分 GB 端点，32 字节随机 hex 落库，在「高级配置 → 访问令牌」管理（`TokenService` / `ApiTokens` 表），可设永久或 7/30/90 天。与 JWT 并列，不替代页面 Cookie。
- **播放令牌**（`?play_token=`）：`PlayTokenService` 无状态 HMAC（`base64url(payload).base64url(sig)`，**签名对象是 base64url 字符串本身**，不是 JSON 原始字节），默认 30 分钟有效、过期后 12 小时宽限覆盖播放器断线重连；绑定 `app/stream/node`（文件场景 `path`），可选绑定客户端 IP（NAT 后不要开）。`ZlmService.BuildFlvUrl/BuildWsFlvUrl/BuildHlsUrl/BuildRtpFlvUrl/BuildWebRtcPlayUrl` 自动追加（`BuildWebRtcUrl` 是裸信令地址，不自带令牌），`BuildRecordUrl` 用路径级令牌；RTSP/RTMP 不附令牌。`PlayToken:Secret` 少于 32 字节时播放鉴权自动关闭（地址不带令牌、`on_play` 放行），日志打 Warning。
- **两套令牌不要混用**：访问令牌管「应用代理 + 对外 API」，播放令牌管「ZLM 直连」。RTSP/RTMP 不受令牌保护，只应内网使用。
- **WebRTC / HLS 的鉴权细节**：ZLM 播 WebRTC 时 `on_play` 上报 `schema=rtc`（实测 params 形如 `app=live&stream=101&type=play&token=xxx&session=18-28`，403 时 ZLM 回 `code:-400 [auth failed]`），`IsTokenProtectedSchema` 必须包含 `rtc`，漏了直连 WebRTC 会完全绕过鉴权；HLS 索引同样回调 `on_play`（`schema=hls` / `hls.fmp4`，首个请求 params 带 token），但 ZLM HlsPlayer 是**流级复用**（存活约 segDur×segNum，实测 3~5 秒窗口），有人正在看时新客户端不再回调 `on_play`，所以 HLS 只是播放器级弱校验；严格逐客户端鉴权用 FLV/WebRTC 或平台代理 `/play/{app}/{stream}/hls.m3u8`（每次校验访问令牌）。测 `params` 时务必让 HlsPlayer 先静默超时 5 秒以上再发首个请求，否则会误判 params 为空。

---

## 7. 架构决策与踩坑清单

### 7.1 ZLM WebHook

`Controllers/ZlmHookController.cs` + `Services/ZlmHookModels.cs` / `ZlmHookService.cs`：**单一入口 `POST /api/zlm/hook/{event}`，按 route 参数 `event` 分发**（`event` 必须原样参与 HMAC 验签，因此 config.ini 里的 URL 与 `?cap=` 不用改）。未知事件回 `code:404`、body 非法回 `code:400`，均为 HTTP 200；body 手工 `JsonSerializer.DeserializeAsync` 而不用 `[FromBody]`，就是为畸形 JSON 也能回 200 + `code:400`。ZLM 以 **POST + JSON body** 投递，字段命名混用（`mediaServerId` 驼峰，`start_time`/`file_size`/`file_path`/`local_port`/`stream_id` 下划线，必须显式映射）。

- ZLM 自身无签名能力，每个 URL 带该事件专属 `?cap=`：`base64url(HMAC-SHA256(ZlmHook:CapabilitySecret, mediaServerId + "\n" + event))`；算法：`printf '%s\n%s' <mediaServerId> <event> | openssl dgst -sha256 -hmac <secret> -binary | openssl base64 -A | tr '+/' '-_' | tr -d '='`。**换密钥或换 mediaServerId 必须重算全部 17 个 URL**；再叠加 mediaServerId 校验、来源 IP 白名单与限流。响应固定 HTTP 200 + `code`（**非 0 会被 ZLM 判为鉴权失败**，通知型 hook 也必须 `code:0`）。`deploy/docker/config.ini` 的 `[hook] enable=1` 是 ZLM 侧总开关，改完重启容器。
- 17 个事件（以 `getServerConfig` 的 `hook.*` 键为准；`on_proxy_publish` / `on_proxy_connect` / `on_stream_not_found_ffmpeg` 当前版本不存在，写进去会被 `setServerConfig` 拒）。业务直接用 9 个：`on_server_started`（清在线缓存）、`on_stream_changed`（`ZlmOnlineCache` 补位，10 分钟有效）、`on_stream_none_reader`（按需断流；`ReplaceOnDemandPolling=true` 时 `OnDemandPullService` 不启动）、`on_stream_not_found`（按需拉流兜底；**ZLM 只读响应里的 `close`，不支持由它自行拉流**）、`on_publish`（推流鉴权，`app=rtp` 国标收流无条件放行）、`on_play`（播放令牌）、`on_record_mp4`（写 `RecordingFiles`，`Source=hook`）、`on_rtp_server_timeout`（收尾 SIP 会话）、`on_http_access`（只拦 `HttpAccessGuardedPrefixes`，默认 `/record/`；HLS 分片是相对路径不带 query，不能拦）。其余只记录或按安全默认拒绝：`on_rtsp_realm` 必须返回**空 realm**（否则抓拍 `getSnap` 的 RTSP 内部拉流与 VLC 被 401）、`on_rtsp_auth` 一律拒绝、`on_shell_login` 默认拒绝（`ZlmHook:AllowShellLogin=true` 才放行）、`on_server_keepalive` 维护 `LastKeepaliveUtc`、`on_server_exited` 记 Warning、`on_flow_report` / `on_record_ts` / `on_send_rtp_stopped` 只打 Debug/Info。
- **坑**：`on_rtp_server_timeout` 的 `ssrc` / `tcp_mode` 是**数字**（`"ssrc":0`），声明成 string 会让整个事件反序列化失败；`on_http_access` 的 `header.*`、`on_server_keepalive` 的 `data`、`on_stream_changed` 的 `tracks/originSock` 用 `[JsonExtensionData]`（`ZlmHookRequest.Unknown`）兜底。

### 7.2 拉流、录像与播放代理

- 取流模式 `PullProxy.PullMode`：`native` 走 `addStreamProxy`，`ffmpeg` 走 `addFFmpegSource`。**ZLM 只接受 `src_url` + `dst_url`**（曾误写 `ffmpeg_cmd`，ZLM 回 `Required parameter missed: "src_url","dst_url","timeout_ms"`），ffmpeg 命令由 `deploy/docker/config.ini` 的 `[ffmpeg] cmd` 生成，平台侧 `PullProxy:FfmpegCmd*` 仅作排障参考。默认模板 `%s -re -rtsp_transport tcp -i %s -c:v copy -c:a aac -ar 44100 -ac 2 -f flv %s`，`-rtsp_transport tcp` 不可省（公网 RTSP 走 UDP 实测拉不到包）。
- **坑（转码能力）**：本镜像 ZLM 未链接 libavcodec（`ldd` 看不到，镜像里的 ffmpeg 只是命令行工具），`addStreamProxy` 的 `enable_audio` / `protocol.enable_audio` **静默无效**——G.711 转 AAC 只能走 ffmpeg 模式；`OutputCodec=h264/h265` 重编码同理，2 核机 2.5K 实时编码实测 ~2fps，不要承诺视频转码，让对方用子码流。
- **坑（HLS 代理）**：分片路由必须写 catch-all `/play/{app}/{stream}/hls/{**segment}`——ZLM 分片名带日期子目录（`2026-11-12/20/39-19_6.ts`），单段 `{segment}` 会让 m3u8 正常但分片全 404（黑屏）。
- `/play/*` 必须回 CORS 头（`Access-Control-Allow-Origin: *` + `OPTIONS → 204`），否则第三方网页用 hls.js / flv.js 跨域拉流被拦。
- 启动恢复（`PullProxyRestoreService`）：ZLM 重启会丢内存里的拉流任务，启动后自动把「非按需」「按需但开录像」的流重新拉起（等 ZLM 就绪最多 90s，逐路 500ms）。详情页「重新拉流」= 停 → 轮询等流消失（≤20s）→ 重建（失败重试 1 次）；停完只等 500ms 会被判「等待超时」。
- **坑（ZLM 接口名）**：录像文件列表是 `getMp4RecordFile`（返回 `data.paths` + `data.rootPath`），**没有 `listMp4Record`**（404）。曾按错误名封装，导致 hook 索引为空时回放页看不到历史录像。核对接口用 `/index/api/getApiList?secret=`。
- 录像索引对账（`RecordingReconcileService`）：`on_record_mp4` 是主链路，对账每 30 分钟扫最近 7 天——索引有但文件没了累计 `ReconcileMissCount`，达阈值标记 `MissingAtUtc`（`GetRecordFilesAsync` 过滤）；文件在但索引没有则补录（`Source=reconcile`，时长未知记 0）。可调 `Recording:ReconcileEnabled/ReconcileDays/ReconcileIntervalMinutes/ReconcileMissThreshold`。
- 录像备份（`BackupTarget` 表 + `RecordingBackupService` / `RecordingBackupHostedService`）：SMB/NFS 挂载目录增量复制，S3 走 `S3StorageClient` 原生直传（AWS SigV4，自实现不引 SDK）；源目录由 `Recording:SourcePath` 配置（Docker 下 `/opt/media/bin/www/record`）。

### 7.3 GB28181 / ONVIF

- 接入双模式（`AppSetting` 键 `gb28181.access.mode`，热生效）：`auto` 注册即接入（`EnableAuth` 决定是否鉴权，**默认 true**）；`whitelist` 预登记 + 白名单鉴权（`GbPresetDevice` 按国标 ID 匹配、独立密码，未登记回 403）。切换在「高级配置 → SIP 接入」，预登记在「设备列表 → 添加 → GB28181」。
- 级联的**上级平台列表是业务数据**，持久化在 `CascadeUpper` 表，经 `/advanced` 或 REST 动态增删改热生效，**不要写回 appsettings.json**；级联开关、`LocalId`、注册/同步周期等基础设施仍在 `Gb28181:Cascade` 配置段。
- **国标点播排障三条**：① `Gb28181:MediaIp` 必须是**设备可达的 ZLM 主机地址**（分离部署最易配成应用本机 IP，表现为点播 200 OK 却黑屏、ZLM `getMediaList` 查不到 `rtp/{通道号}`）；② INVITE 的 SDP 必须带 `a=rtpmap:96 PS/90000` 等 rtpmap，缺了部分设备直接回 500；③ 平台重启后内存注册状态丢失，设备的 `MESSAGE KEEPALIVE` 会凭 DB 记录自动恢复在线；目录/录像查询的等待槽必须**先登记再发送**，否则设备秒回会误判超时。
- 分离部署时 `LocalIp` = 应用所在主机 IP（SIP `From` 用），`MediaIp` = **ZLM 所在主机 IP**（设备往这里推 RTP）；留空会自动探测应用本机，容易指错主机。多网卡（VMware/Hyper-V 虚拟网卡）务必显式配置。

### 7.4 其它

- 设备自定义分组：`DeviceGroups`（名称唯一 + SortOrder）、`DeviceGroupMembers`（`Kind`=pull/gb + `App` + `Stream` 三者唯一，一个设备只归一组），经 `DeviceGroupService` 读写；播放页设备树按分组展示，未归属进「未分组」，另一个 Tab「按协议」保留。两表由 `DbInitializer` 的 `CREATE TABLE IF NOT EXISTS` 兜底。
- 部署加固：`deploy/docker/install.sh` 启动前做端口预检（报占用端口/进程、提示内核 `ip_local_port_range` 重叠）；WebRTC 必须 **UDP + TCP 同时映射**（`[rtc] port` 与 `[rtc] tcpPort` 两个键）；bridge 网络下 `Gb28181__LocalIp/MediaIp` 必须是宿主可达 IP，否则 SDP 写容器 `172.x`，设备推流连不进来。

---

## 8. 构建、打包与发布

### 本地

```bash
dotnet build Apiext.MediaKit.slnx                 # 常规构建（0 错误；CS0612 过时警告属 AntDesign 内部，可忽略）
```

```powershell
# Windows 安装包（需要 .NET 10 SDK + NSIS）
./deploy/windows/publish-app.ps1                 # 自包含发布 → deploy/windows/app
./deploy/windows/fetch-payload.ps1                # 下载 zlm / ffmpeg / nssm（默认 nightly.link 免登录）
./deploy/windows/build-installer.ps1              # → Apiext-MediaKit-Setup-<版本>.exe
```

`fetch-payload.ps1` 的 ZLM 产物优先 `-ZlmArtifactUrl`（本地 zip / 直链），其次 `-Token` 走 GitHub API（需能读 ZLMediaKit 仓库 Actions 的 PAT，仓库自带 `GITHUB_TOKEN` 不行），默认走 nightly.link 免登录直链；ffmpeg 来自 gyan.dev，NSSM 来自 nssm.cc 2.24。`app/` `zlm/` `ffmpeg/` `nssm.exe` 与安装包都在 `deploy/windows/.gitignore` 里，**不入库**。

VC++ 运行库按需处理：`vc_redist.x64.exe` 只是可选离线载荷（`File /nonfatal`，不入库、默认不内置），向导检测 `VCRUNTIME140*.dll` / `MSVCP140.dll` 后才显示第 3 页；`install.ps1 -InstallVcRuntime` 控制是否下载安装，下载/安装/服务启动失败都只记警告与 `install-report.txt` 的「安装警告」，不阻断安装（卸载入口在调用 `install.ps1` **之前**写入）。

### CI（发布只走 GitHub Actions，且只手动触发）

| 工作流 | 触发 | 产物 |
|---|---|---|
| `.github/workflows/docker-image.yml` | 手动（可指定平台 / 是否推送） | `ghcr.io/<owner>/<repo>`：`yyMMdd-HHmm`、`sha-xxxx`；默认分支另打 `latest` |
| `.github/workflows/windows-installer.yml` | 手动（`publish=true` 默认发布 / `false` 只出 artifact） | Actions artifact；发布时创建/更新 Release 并附安装包 |

- 版本号统一为 `yyMMdd-HHmm`（Asia/Shanghai，如 `261117-1140`），两个工作流自动生成，不再依赖 git 标签；`windows-installer.yml` 把版本写进 `installer.nsi` 的 `APP_VERSION`（**文件是 UTF-8 BOM 且 `Unicode true`，改写必须保留 BOM**，否则中文乱码）。
- Docker 构建上下文是仓库根：`deploy/docker/docker-compose.yml` 的 `context: ../..`、`dockerfile: deploy/docker/Dockerfile`；GHCR 用 `GITHUB_TOKEN` 登录，无需额外 secret。
- 新增发布目标就再加一个 workflow，不引入其它自建仓库 / 自建 CI 脚本。

---

## 9. 端口与部署速查

| 用途 | 端口 |
|---|---|
| 应用（dev / 容器内 / Windows 原生） | 5080 / 8080（宿主 28080）/ 28080 |
| ZLM HTTP / RTMP / RTSP | 28180 / 28335 / 28554（1:1） |
| WebRTC / SRT | 28000（udp+tcp）/ 29000（udp） |
| RTP（GB over TCP/UDP） | 21000 |
| GB28181 SIP / RTP 收流 | 28060（udp）/ 24000-24500（tcp+udp） |

- 全部小于 32768（避开内核临时端口段），定义在 `deploy/docker/config.ini`（ZLM 侧）与 `deploy/docker/docker-compose.yml`（映射侧），改端口两处必须一致；`.env.example` 与 `install.sh` 也要同步。
- ① 镜像模式 `bash deploy/docker/install.sh`（GHCR 拉 `ghcr.io/apiext-labs/apiext-mediakit:latest`，自动生成 `.env` / `config.ini` / `appsettings.Production.json` / `docker-compose.yml`、预检、启动）；② 源码构建 `cd deploy/docker && cp .env.example .env && docker compose up -d --build`。db 持久化在宿主 `APP_DATA/`，录像在 `zlm-record` 卷。
- 详情与升级回滚、备份、安全清单见 `deploy/docker/README.md`；Windows 原生部署见 `deploy/windows/README.md`。

---

## 10. 里程碑 / 待办

- [x] M1 脚手架 + 登录（Cookie+JWT）+ ZLM 健康检查 + UI 主题
- [x] M2 拉流代理 CRUD + 流管理 + 配置读写
- [x] M3 直播播放（jessibuca，FLV/WebRTC/HLS + H.265 软解）+ 分屏
- [x] M4 服务器监控图表 + 在线观众
- [x] M5 GB28181 双模式 / 级联 / 云台 / 录像回放
- [x] M6 ONVIF 接入 + 录像备份（SMB/NFS/S3）+ 录像计划
- [x] M7 取流模式、高级配置整合
- [x] M8 播放令牌 + WebHook 鉴权 + 录像索引对账 + 端口预检
- [x] M9 界面质量（云台 UI 统一、播放失败提示、三态、样式归位）
- [x] M10 Windows 原生交付 + GitHub Actions 手动发布
- [ ] 后续：RBAC 多用户、录像回放时间轴拖拽、云台预置位 UI 增强、移动端适配、表单内联校验
