# 部署总览

`deploy/` 是**所有交付与部署资产**的入口，按目标平台分成两个子目录：

```
deploy/
├─ README.md              # 本页：两种形态怎么选、最短路径、文档导览
├─ docker/                 # Linux / Docker 交付
│  ├─ install.sh           # 一键安装（拉 GHCR 镜像 → 生成配置 → 预检 → 启动）
│  ├─ docker-compose.yml    # 单容器编排：应用 + ZLM
│  ├─ docker-compose.zlm.yml # 独立 ZLM（本地开发 / 分离部署）
│  ├─ Dockerfile            # 容器内编译 .NET 应用，叠加 ZLM 镜像
│  ├─ entrypoint.sh         # 同容器内拉起 ZLM 与应用两进程
│  ├─ config.ini            # ZLM 配置覆盖（端口 / secret / hook）
│  ├─ .env.example           # 环境变量模板（PUBLIC_HOST 等）
│  ├─ appsettings.Production.json # 宿主可编辑的配置覆盖层
│  └─ README.md             # 容器部署手册（交付 / 运维）
└─ windows/                # Windows 原生交付（不依赖 Docker）
   ├─ publish-app.ps1       # .NET 自包含发布 → app\
   ├─ fetch-payload.ps1     # 下载 ZLM / ffmpeg / NSSM 载荷（不入库）
   ├─ build-installer.ps1   # NSIS 编译单文件安装包
   ├─ installer.nsi         # 安装包脚本（参数向导，含按需出现的 VC++ 运行库页）
   ├─ install.ps1           # 铺文件 / 生成配置 / 放行防火墙 / 注册服务
   ├─ uninstall.ps1          # 卸载（可保留数据）
   ├─ detect-ip.ps1         # 向导预填本机 IPv4
   └─ README.md             # 打包、安装、升级、排障、体积构成与第三方组件
```

## 选哪种

| | Docker（形态 A） | Windows 原生（形态 B） |
|---|---|---|
| 目标机 | Linux + Docker 24+（Windows 宿主机可用 Docker Desktop） | Windows 10 / Server 2016+（x64） |
| 交付物 | GHCR 镜像 `ghcr.io/apiext-labs/apiext-mediakit` | `Apiext-MediaKit-Setup-<版本>.exe`（约 80 MB） |
| 装完是什么 | 一个容器 `apiext-mediakit`，内含 ZLM + 应用 | 两个开机自启服务 `Apiext-MediaKit-App` / `Apiext-MediaKit-ZLM` |
| 目标机前置 | 只需 Docker，脚本可现场安装 | 无需 Docker、无需装 .NET（自包含发布）；需管理员权限 |
| 数据 | 宿主 `APP_DATA/`（SQLite） + `zlm-record` 卷（录像） | `C:\apiext-mediakit\APP_DATA` + `zlm\www\record` |
| 适合 | 服务器批量交付、容器化环境 | 内网单机、无 Docker 环境、现场交付 |

两种形态**功能一致、端口数值一致**（管理平台 28080、ZLM HTTP 28180 等），配置文件与访问地址可照搬。

## 最短路径

```bash
# Docker：目标机执行，交互式确认对外 IP / 端口 / 时区
bash deploy/docker/install.sh

# Docker：源码构建（改 PUBLIC_HOST 后启动）
cd deploy/docker && cp .env.example .env
docker compose up -d --build --force-recreate
```

```powershell
# Windows：目标机双击安装包；无人值守
Apiext-MediaKit-Setup-1.0.0.exe /S /D=C:\apiext-mediakit
```

```powershell
# Windows：自己打包（需要 .NET 10 SDK + NSIS）
.\deploy\windows\publish-app.ps1
.\deploy\windows\fetch-payload.ps1
.\deploy\windows\build-installer.ps1
```

## 三条必读约束

1. **`PUBLIC_HOST` / 向导里的「对外 IP」必须是浏览器和设备都可达的宿主机 IP**，不能是 `127.0.0.1` 或容器内网 `172.x`；它只用于对外通告（播放地址、SIP `From`、SDP `c=`），不影响监听（全部监听 `0.0.0.0`）。
2. **端口必须 1:1 映射**：改端口要同时改 `deploy/docker/.env`、`deploy/docker/docker-compose.yml`、`deploy/docker/config.ini`（ZLM 侧）与 `deploy/docker/install.sh` 的内置模板；Windows 侧在向导第 2 页或 `install.ps1` 参数里改。
3. **`24000-24500` 端口段必须关闭 Docker userland proxy**：`/etc/docker/daemon.json` 加 `"userland-proxy": false` 后重启 docker，否则 1001×2 个映射会拉起 2000+ 个 `docker-proxy` 进程，表现为 `compose up` 卡死。

## 默认端口

| 用途 | 默认端口 | 协议 |
|---|---|---|
| 管理平台（Web UI / REST / 播放代理） | **28080**（容器内 8080） | TCP |
| ZLM HTTP（FLV / HLS / WebRTC 信令） | 28180 | TCP |
| RTSP / RTMP | 28554 / 28335 | TCP |
| RTP（GB28181 over TCP/UDP） | 21000 | TCP + UDP |
| WebRTC 媒体 | 28000（**UDP 与 TCP 都要映射**，对应 `[rtc] port` / `[rtc] tcpPort`） | UDP + TCP |
| SRT | 29000 | UDP |
| GB28181 SIP | 28060 | UDP |
| GB28181 RTP 收流 | 24000-24500 | UDP + TCP |

全部为非常用端口且小于 32768，避开内核临时端口段。

## 发布物怎么来的

在 Actions 页面手动触发工作流产出（推代码 / 推标签都不再触发；不用手工推镜像 / 传安装包）：

| 工作流 | 触发 | 产物 |
|---|---|---|
| `.github/workflows/docker-image.yml` | Run workflow（可指定平台 / 是否推送） | GHCR 镜像：`yyMMdd-HHmm`、`sha-xxxx`；默认分支另打 `latest` |
| `.github/workflows/windows-installer.yml` | Run workflow（`publish=true` 默认发布） | Actions artifact；发布时创建/更新 Release 并附安装包 |

版本号统一为 `yyMMdd-HHmm`（Asia/Shanghai，如 `261117-1140`），两个工作流自动生成，无需 git 标签。

Windows 安装包的第三方载荷（ZLMediaKit Windows 产物、ffmpeg、NSSM）不进仓库，由 `fetch-payload.ps1` 在打包时下载，CI 与本地共用同一脚本。VC++ 2015-2022 x64 运行库默认不内置：目标机缺失时向导按需联网下载安装，离线场景把 `vc_redist.x64.exe` 放到 `deploy/windows/` 再打包即可（详见 `windows/README.md`）。

## 文档导览

| 想做什么 | 看哪份 |
|---|---|
| 了解项目是什么、能做什么 | 仓库根 [`README.md`](../README.md) |
| 开发、改代码、加功能 | 仓库根 [`AGENTS.md`](../AGENTS.md) |
| 容器部署：端口、国标、WebHook、反向代理、升级回滚、备份、排障 | [`docker/README.md`](docker/README.md) |
| Windows 打包、安装、升级、卸载、体积构成、第三方许可 | [`windows/README.md`](windows/README.md) |
