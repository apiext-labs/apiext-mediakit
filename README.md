# Apiext MediaKit

一个轻量的流媒体管理平台：把 NVR/DVR、网络摄像机的 RTSP / RTMP / HTTP 源流，转成浏览器可以直接播放的直播流，并提供设备接入、视频调阅、录像回放与备份、云台控制和服务监控。

典型场景：已有一批摄像机或录像机，希望用一个网页统一「接进来、看得到、录得下、调得动」，而不想自己拼一套流媒体服务 + 前端播放器 + 权限体系。

## 它能做什么

| 能力 | 说明 |
|---|---|
| 设备接入 | RTSP / RTMP / HTTP-FLV 拉流代理（`native` / `ffmpeg` 两种取流模式、按需拉流）、ONVIF（发现 / 取流 / 云台 / 预置位）、GB28181（注册即接入 / 预登记白名单两种模式、级联、报警） |
| 视频调阅 | 设备树（自定义分组 / 按协议）、1 / 4 / 9 / 16 分屏、云台、截图、全屏、播放失败原因提示 |
| 录像 | 本地录像 + 录像计划、按日期回放、国标设备侧录像查询与回放、录像文件索引对账 |
| 录像备份 | SMB / NFS 挂载目录增量同步，S3 兼容对象存储原生直传（MinIO / OSS / COS） |
| 监控与配置 | 在线流 / 观众数 / 流量图表，ZLM 配置查看，端口清单，访问令牌管理，国标级联与 SIP 接入 |
| 播放格式 | HTTP-FLV / WS-FLV / HLS / WebRTC，H.264 硬解、H.265 WASM 软解（jessibuca） |

## 怎么工作

```
   摄像机 / NVR / 国标设备
        │  RTSP / RTMP / GB28181 / ONVIF
        ▼
  ┌──────────────────────────┐
  │  ZLMediaKit（流媒体引擎） │◄── HTTP API 管理 ──┐
  │  拉流 / 转封装 / 录像     │── WebHook 回调 ──► │
  └──────────────────────────┘                  │
        │ FLV / HLS / WebRTC                      │
        │                                  ┌──────┴───────────────┐
        ▼                                  │ Apiext MediaKit 应用  │
     浏览器播放器 ◄── 页面 / 播放代理 ───── │ Blazor Server + Web API│
                                          │ SQLite（设备/录像/令牌）│
                                          └────────────────────────┘
```

应用本身是一个 ASP.NET Core 单进程（Blazor InteractiveServer 页面 + Web API + 后台服务），通过 ZLMediaKit 的 `/index/api/*` 管理流，通过 ZLM WebHook 感知上下线、无人观看、录像完成等事件。数据落在 SQLite，默认单管理员登录。

两种交付形态功能一致：**Docker 单容器**（应用 + ZLM 同容器）或 **Windows 原生双服务**（NSIS 安装包，目标机无需 Docker / .NET）。

## 技术栈

| 层 | 选型 |
|---|---|
| 后端 | ASP.NET Core Web API / .NET 10 |
| 前端 | Ant Design Blazor 1.6.2（InteractiveServer） + ProLayout |
| 流媒体 | ZLMediaKit（HTTP API + WebHook） |
| 播放器 | jessibuca（H.265 WASM 软解） |
| 数据库 | EF Core + SQLite |
| 认证 | Cookie（页面）+ JWT（API）+ 访问令牌 / 播放令牌 |
| 打包 | Docker Compose、NSIS、GitHub Actions |

## 五分钟上手

**Docker（推荐，目标机只需 Docker）**

```bash
bash deploy/docker/install.sh       # 交互式：确认对外 IP / 端口 / 时区，自动拉镜像并启动
```

镜像来自 `ghcr.io/apiext-labs/apiext-mediakit:latest`。也可源码构建：

```bash
cd deploy/docker && cp .env.example .env   # 把 PUBLIC_HOST 改成宿主机对客户端可达的 IP
docker compose up -d --build --force-recreate
```

**Windows（不依赖 Docker）**

从 GitHub Releases 下载 `Apiext-MediaKit-Setup-<版本>.exe`，双击按向导填对外 IP / 端口即可；无人值守：

```powershell
Apiext-MediaKit-Setup-1.0.0.exe /S /D=C:\apiext-mediakit
```

**本地开发**

```bash
cd src/Apiext.MediaKit.Server
dotnet run --launch-profile http     # http://localhost:5080，默认 admin / admin123
```

本地需要一个可访问的 ZLMediaKit（在 `appsettings.Development.json` 配 `ZlMediaKit:BaseUrl` / `Secret`，或用 `deploy/docker/docker-compose.zlm.yml` 单独起一个）。

> 装完打开 `http://<对外 IP>:28080`，首次登录会走三步初始化向导。账号密码与各项目密钥同时写在 Windows 安装目录的 `install-report.txt`。

## 页面一览

| 路径 | 内容 |
|---|---|
| `/` | 运行概览、统计图表、初始化向导 |
| `/devices` | 统一设备列表：拉流代理 / ONVIF / GB28181 的添加、过滤、详情抽屉 |
| `/player` | 视频调阅：设备树、分屏、云台、截图 |
| `/playback` | 录像回放：本地录像、国标卡录像、录像计划 |
| `/monitor` | 在线流、观众、码率等实时监控 |
| `/advanced` | 国标级联 / SIP 接入 / 端口清单 / ZLM 配置 / 录像备份 / 访问令牌 |
| `/login` | 登录 |

## 默认端口

| 用途 | 端口 |
|---|---|
| 管理平台 | **28080**（容器内 8080） |
| ZLM HTTP / RTSP / RTMP | 28180 / 28554 / 28335 |
| WebRTC / SRT | 28000（UDP + TCP）/ 29000（UDP） |
| GB28181 SIP / RTP 收流 | 28060（UDP）/ 21000 / 24000-24500 |

全部是「非常用端口」，两端必须 1:1 对应（SDP 里写的就是这些端口，做 NAT 转换设备连不上）。完整清单与必改项见 [`deploy/README.md`](deploy/README.md)。

## 安全模型（概要）

- 页面用 Cookie，独立 API 用 JWT；第三方集成另发**访问令牌**（`?token=`，长期、落库、可在界面管理）。
- ZLM 直连播放用**播放令牌**（`?play_token=`，HMAC 签名、30 分钟有效、断线重连宽限 12 小时），由系统自动签发，ZLM 回调 `on_play` 校验。两套令牌互不替代。
- RTSP / RTMP 地址不带令牌，只应内网使用；对外集成请走平台播放代理 `/play/...?token=`。
- 推流鉴权、按需拉流、无人观看断流、录像索引，都依赖 ZLM WebHook（17 个事件，各 URL 带事件专属 `?cap=` 签名）。

细节见 [`AGENTS.md`](AGENTS.md) 与 [`deploy/docker/README.md`](deploy/docker/README.md)。

## 仓库结构

```
Apiext.MediaKit/
├─ README.md                     # 本页：了解项目
├─ AGENTS.md                      # 开发者手册（约定、踩坑、如何改代码）
├─ Apiext.MediaKit.slnx           # 解决方案
├─ APP_DATA/                      # 运行时 SQLite 数据（不入库）
├─ .github/workflows/             # 手动构建 Docker 镜像与 Windows 安装包
├─ src/Apiext.MediaKit.Server/    # 单项目：页面 + Web API + 后台服务
│  ├─ Controllers/  Contracts/  Apis/  Services/  Persistence/  Auth/
│  ├─ Gb28181/  Onvif/  Extensions/
│  ├─ Components/                 # Blazor 布局 / 页面 / 设备详情组件
│  └─ wwwroot/                    # 静态资源（主题、播放器 JS、jessibuca）
└─ deploy/                        # 交付与部署资产（详见 deploy/README.md）
   ├─ README.md                   # 部署总览
   ├─ docker/                     # Dockerfile / compose / install.sh / README.md
   └─ windows/                    # 发布、打包、NSIS 安装脚本 / README.md
```

## 发布物

在 GitHub Actions 页面手动触发工作流构建（推代码 / 推标签都不再自动触发）：

- Docker 镜像 `ghcr.io/apiext-labs/apiext-mediakit`（`yyMMdd-HHmm`、默认分支 `latest`、`sha-xxxx`）
- Windows 安装包 `Apiext-MediaKit-Setup-<版本>.exe`，勾选 `publish` 时附到同名 GitHub Release

版本号统一为 `yyMMdd-HHmm`（Asia/Shanghai，如 `261117-1140`）。

开发侧如何构建、打包、发布，见 [`AGENTS.md`](AGENTS.md)。

## 文档导览

| 你是… | 看 |
|---|---|
| 想了解项目能做什么、怎么试用 | 本页 |
| 要开发、改代码、加接口 / 页面 | [`AGENTS.md`](AGENTS.md) |
| 要部署到服务器 / 现场机器 | [`deploy/README.md`](deploy/README.md) |
| 要查容器端口、国标、WebHook、升级回滚、排障 | [`deploy/docker/README.md`](deploy/docker/README.md) |
| 要打包 / 安装 / 卸载 Windows 版 | [`deploy/windows/README.md`](deploy/windows/README.md) |

## 第三方组件

ZLMediaKit（MIT，流媒体引擎）、jessibuca（播放器）、Ant Design Blazor / ProLayout（前端组件）、WebApiClientCore（声明式 HTTP 客户端）、SIPSorcery（GB28181 信令）、ffmpeg（GPL，转码与抓拍）、NSSM（服务托管，公共领域）。Windows 安装包内含 ffmpeg，分发时需随包提供其许可与源码获取方式。

## 路线图

- [x] 设备接入（拉流代理 / ONVIF / GB28181）、视频调阅、录像回放与备份、监控、高级配置
- [x] 播放令牌 + ZLM WebHook 鉴权、按需拉流、录像索引对账
- [x] Docker 一键安装、Windows 原生安装包、GitHub Actions 手动发布
- [ ] RBAC 多用户、录像回放时间轴拖拽、云台预置位 UI 增强、移动端适配
