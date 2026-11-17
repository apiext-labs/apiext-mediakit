# Apiext MediaKit — Docker 部署手册

面向运维/交付：从零把平台跑起来，并完成国标接入、播放、录像与排障。Windows 原生部署见 [`../windows/README.md`](../windows/README.md)，两种形态的选型见 [`../README.md`](../README.md)。

- 适用版本：.NET 10 + ZLMediaKit（`zlmediakit/zlmediakit:master`）
- 文档与代码同源：端口、环境变量均来自 `deploy/docker/docker-compose.yml`、`deploy/docker/config.ini`，改端口时两边同步改

---

## 1. 部署形态

| 形态 | 组成 | 适用场景 | 配置要点 |
|---|---|---|---|
| **A. 单容器（推荐）** | 一个 `apiext-mediakit` 容器内同时跑 ZLM + 应用（`entrypoint.sh` 拉起两个进程） | 新建项目、一台服务器交付 | ZLM 用 `127.0.0.1:80` 内部访问，无需额外网络配置 |
| **B. 应用 + 独立 ZLM** | 应用容器（或宿主机进程）+ `docker-compose.zlm.yml` 起的独立 ZLM | 已有流媒体服务器、多应用共用 ZLM、应用与媒体分开扩容 | 必须显式配置 `ZlMediaKit__BaseUrl`、`Gb28181__MediaIp`、`config.ini` 的 hook 地址 |

> 国标播放的关键约束：SDP 里 `c=` 必须是「**设备能访问到 + RTP 收流端口所在主机（ZLM）**」的地址。形态 A 下两者都是宿主机；形态 B 下 `MediaIp` 必须写 **ZLM 主机**地址，不是应用主机。

---

## 2. 前置条件

| 项 | 要求 |
|---|---|
| 操作系统 | Linux（x86_64），内核 ≥ 4.x；Windows 仅建议开发环境 |
| Docker | 24+，Compose v2（`docker compose` 子命令） |
| 资源 | 建议 ≥ 4 核 / 8 GB 内存；每路 1080p 转码另计 CPU |
| 磁盘 | 录像按 `码率 × 时长 × 路数` 估算；`zlm-record` 卷与 DB 分离 |
| 时区 | 建议容器加 `TZ=Asia/Shanghai`，否则录像按 UTC 日期分目录 |
| 网络 | 设备/客户端能访问宿主机的 28080、28180、24000-24500/udp 等端口（见第 3 节） |

---

## 3. 端口清单

默认端口全部是**非常用端口**（避开 80/443/1935/5060/8000/8080/8554/9000/10000 等易冲突端口），可在 `deploy/docker/.env` 中统一改。

| 宿主机端口 | 容器端口 | 协议 | 用途 | 是否需对设备/客户端开放 |
|---|---|---|---|---|
| **28080** | 8080 | TCP | Web UI / REST API / 播放代理 `/play/*` | 是（客户端） |
| **28060** | 28060 | UDP | GB28181 SIP 信令（设备端填这个，不是 5060） | 是（国标设备） |
| **28180** | 28180 | TCP | ZLM HTTP：HTTP-FLV / HLS / WebRTC 信令 | 是（客户端） |
| **28554** | 28554 | TCP | RTSP 播放 | 按需 |
| **28335** | 28335 | TCP | RTMP 推拉流 | 按需 |
| **21000** | 21000 | TCP+UDP | RTP（GB28181 over TCP/UDP） | 按需 |
| **28000** | 28000 | **UDP + TCP** | WebRTC 媒体面 | 是（用 WebRTC 时） |
| **29000** | 29000 | UDP | SRT | 按需 |
| **24000-24500** | 24000-24500 | **UDP + TCP** | GB28181 点播收流 | 是（国标设备） |

端口策略说明：

- 除管理平台（宿主 28080 → 容器 8080）外，**其余端口都是 1:1 映射**。因为 GB28181 的 SDP 与 WebRTC 的 ICE 里写的就是 ZLM 的端口号，做成「宿主端口 ≠ 容器端口」的 NAT 转换，设备/浏览器会往错误端口发包，表现为信令成功但无画面。
- 端口定义同时存在于 `deploy/docker/config.ini`（ZLM 侧）与 `deploy/docker/docker-compose.yml`（映射侧），改端口必须两处一致；`deploy/docker/install.sh` 内置的配置模板也要同步。
- WebRTC 的 `[rtc] port`（UDP）与 `[rtc] tcpPort`（TCP）是两个键，**必须同时映射**，只开 UDP 会让走 TCP 候选的浏览器连不上。
- 所选端口均小于 32768，避开 Linux 内核临时端口段（`ip_local_port_range` 默认 32768-60999），减少偶发 bind 失败。
- **必须关闭 Docker 的 userland proxy**：`24000-24500` 段是 1001 个端口 × TCP/UDP，Docker 默认的 userland proxy 会为每个映射拉起一个 `docker-proxy` 进程（2000+ 个），在小内存机器上表现为 `docker compose up` 卡住、`failed to start userland proxy for port mapping ... timed out`、load 飙到几十。在 `/etc/docker/daemon.json` 加 `"userland-proxy": false` 后 `systemctl restart docker`（改由 iptables 做 DNAT，功能等价、无进程开销）。
- ZLM 的 **RTP over TCP 固定端口写在 `[rtp_proxy] port`**（不是独立的 `[rtp]` 段，写了不生效且不报错），`[rtp_proxy] port_range` 才是 GB28181 点播收流端口段。

---

## 4. 部署步骤（形态 A）

### 4.1 一键安装（镜像模式，推荐）

目标机只需要 Docker，不下载源码、不编译（镜像已由 GitHub Actions 推送到 GHCR）。脚本是**交互式**的，逐项确认后执行：

```bash
bash install.sh            # 交互式：安装目录 / 对外 IP / 时区 / 端口方案
bash install.sh --dry-run  # 只做环境检测与参数确认，不写文件、不启动
bash install.sh --yes      # 非交互：全部用默认值/环境变量，适合批量/自动化
```

交互流程（8 步）：

| 步骤 | 内容 |
|---|---|
| 1 | 环境检测：发行版、内存/磁盘、Docker 与 compose（**缺失时可现场安装**）、工具探测 |
| 2 | 参数确认：安装目录、对外 IP（自动探测并校验）、时区、端口方案（默认或逐项自定义） |
| 3 | 配置汇总确认，确认后才开始改动系统 |
| 4 | 端口预检（占用即报出端口与进程）、内核临时端口段重叠提示、firewalld/ufw 放行命令 |
| 5 | `dockerd` 的 `userland-proxy` 检查与修正（`24000-24500` 段 1001×2 个映射不关闭会让 dockerd 拉起 2000+ 进程） |
| 6 | 在安装目录生成 `.env`、`config.ini`、`appsettings.Production.json`、`docker-compose.yml`、`APP_DATA/` |
| 7 | `docker pull` 镜像（首次约 1.2GB） |
| 8 | 启动 + 健康检查（`/login` 200 与 ZLM `version` 接口），失败自动打印日志 |

**发行版兼容**：Debian/Ubuntu（apt）、RHEL/CentOS/Rocky/AlmaLinux/Fedora/openEuler/Anolis（dnf/yum）。工具缺失自动降级——`ss` → `netstat` → 跳过；`curl` → `wget` → 仅做 TCP 探测；`systemctl` → `service`；`docker compose`（v2）→ `docker-compose`（v1）；修改 `daemon.json` 用 `python3` → `jq` → `sed` 兜底。

> 镜像默认取自 `ghcr.io/apiext-labs/apiext-mediakit:latest`（公开包无需登录）。私有包或指定版本时：先 `echo <PAT> | docker login ghcr.io -u <用户名> --password-stdin`，再 `IMAGE=ghcr.io/apiext-labs/apiext-mediakit:261117-1140 bash install.sh`；也可用 `REGISTRY_USER` / `REGISTRY_PASSWORD` 让脚本代登录。
>
> 非 root 用户请 `sudo bash install.sh`（需要写 `/etc/docker/daemon.json` 与安装目录）。

### 4.2 源码构建（形态 A）

```bash
git clone <仓库地址> apiext-mediakit-hub
cd apiext-mediakit-hub
cd deploy/docker
cp .env.example .env        # 改 PUBLIC_HOST 为宿主机对客户端可达的 IP
docker compose up -d --build --force-recreate
```

`.env` 内容为：

```ini
PUBLIC_HOST=192.168.1.100   # 对外 IP，同时决定播放地址、SIP 本地地址、国标媒体地址
APP_PORT=28080
SIP_PORT=28060
ZLM_HTTP_PORT=28180
RTSP_PORT=28554
RTMP_PORT=28335
TZ=Asia/Shanghai
```

改端口/IP 只需改这个文件再 `docker compose up -d --force-recreate`。**除 `PUBLIC_HOST` 外没有必填项**——ZLM secret、Hook 能力令牌、播放令牌密钥、SIP 编码/域/密码、RTP 端口段、录像目录都已在镜像与 `config.ini` 中预置为一套自洽的值。

> 端口默认取非常用值，且都小于 32768，落在内核临时端口段（`ip_local_port_range` 默认 32768-60999）之外，正常不会出现"端口被出站连接临时占用"的偶发 bind 失败。`install.sh` 启动前会做一次预检，占用时直接报出端口与占用进程。

### 4.3 运行与排查

```bash
cd deploy/docker
docker compose ps
docker logs -f --tail=200 apiext-mediakit
```

`PUBLIC_HOST` 未设置时 compose 会直接报错并提示（避免生成 `127.0.0.1` 这类不可用地址）。

启动日志会打印最终生效值，并自检默认值：

```
[Startup] Apiext MediaKit 已启动 环境=Production 监听=http://0.0.0.0:8080
[Startup] ZLM API=http://127.0.0.1:28180 播放基址=http://192.168.1.100:28180（auto，主机=192.168.1.100）
[Startup] SIP=34020000002000000001@192.168.1.100:28060 媒体地址=192.168.1.100 DB=/app/APP_DATA/apiext-mediakit.db
[Startup] ZlMediaKit:Secret 仍为内置默认值，生产环境请更换（同时改 config.ini 的 [api] secret）
```

> 已经跑过旧镜像时，`docker compose up -d --build` 之后**再加 `--force-recreate`**，避免容器复用旧镜像。
> 启动日志末尾出现「仍为内置默认值」的 Warning 属正常提示，按第 12 节安全清单处理即可，不影响功能。

### 4.4 首次登录

1. 浏览器打开 `http://<宿主机IP>:28080`
2. 默认账号 `admin / admin123`
3. 首次登录会弹三步向导：属性配置 → 开放端口 → 重置账号（**务必改密码并确认已放行端口**）
4. 进入「高级配置 → 访问令牌」生成一个令牌（对外播放/API 用），并在「SIP 接入」确认接入模式

---

## 5. 关键环境变量

变量名用双下划线映射 `appsettings.json` 层级（`ZlMediaKit__BaseUrl` → `ZlMediaKit:BaseUrl`）。

| 变量 | 必填 | 说明 |
|---|---|---|
| `ASPNETCORE_URLS` | 是 | 应用监听地址，容器内固定 `http://0.0.0.0:8080` |
| `ConnectionStrings__Default` | 是 | `Data Source=APP_DATA/apiext-mediakit.db`（相对 `/app`） |
| `ZlMediaKit__BaseUrl` | 是 | 应用调用 ZLM API 的地址；单容器为 `http://127.0.0.1:80` |
| `ZlMediaKit__PublicHost` | **是** | 对外 IP（代替手写 PlayBaseUrl）；容器内取不到宿主机 IP，必须由 `.env` 注入 |
| `ZlMediaKit__PublicHttpPort` | 否 | 对外 ZLM HTTP 端口，默认 `28180` |
| `ZlMediaKit__PlayBaseUrl` | 否 | 直接指定播放地址前缀；留空时自动拼 `http://PublicHost:PublicHttpPort` |
| `ZlMediaKit__Secret` | 是 | 与 `config.ini` 的 `[api] secret` 一致 |
| `ZlMediaKit__RtspPort` / `RtmpPort` | 是 | 对外的 RTSP/RTMP 端口，须与端口映射一致 |
| `Gb28181__SipId` / `SipDomain` / `SipPort` | 是 | 平台编码/域/信令端口（默认 `34020000002000000001` / `3402000000` / `28060`） |
| `Gb28181__Password` | 是 | 全局注册/鉴权密码（`auto` 模式下设备填写） |
| `Gb28181__LocalIp` | 是 | 平台对设备的可达 IP（SIP `From` 用） |
| `Gb28181__MediaIp` | 是 | **设备推流目标地址 = ZLM 主机地址**（分离部署最容易配错） |
| `Gb28181__RtpPortMin/Max` | 否 | 默认 `24000` / `24500`（与 `config.ini` 的 `[rtp_proxy] port_range` 一致） |
| `ZlmHook__Enabled` | 是 | 是否启用 ZLM 回调鉴权（与 `config.ini` 的 `[hook] enable=1` 配套） |
| `ZlmHook__CapabilitySecret` | 是 | hook `?cap=` 的 HMAC 根密钥，换后必须重算所有 URL |
| `ZlmHook__MediaServerId` | 是 | 必须与 `config.ini` 中 ZLM 的 `mediaServerId` 一致（默认 `apiext-mediakit`） |
| `PlayToken__Secret` | 是 | 播放令牌签名密钥，**≥32 字节**；不足会自动关闭播放鉴权 |
| `PlayToken__TtlSeconds` / `GraceSeconds` | 否 | 默认 1800 / 43200（30 分钟 + 12 小时宽限） |
| `PlayToken__BindClientIp` | 否 | 绑定客户端 IP；NAT、多出口、反向代理环境**不要开**（未启用 `ForwardedHeaders`，代理后取到的是代理 IP） |
| `Recording__SourcePath` | 是 | 录像源目录，单容器为 `/opt/media/bin/www/record` |
| `Recording__RetentionDays` | 否 | 录像保留天数，`0` 为不清理 |
| `Recording__BackupIntervalMinutes` | 否 | 备份任务周期，默认 15 分钟 |

---

## 6. ZLM WebHook（鉴权与事件驱动）

平台侧只有一个入口 `POST /api/zlm/hook/{event}`（`ZlmHookController`），按 URL 里的 `event` 分发到各事件处理：事件名不合法回 `code:404`、body 非法回 `code:400`、验签失败回 `code:403`，响应一律 HTTP 200（非 0 会被 ZLM 判为鉴权失败）。因此 `config.ini` 里 17 个 `on_*` 的 URL 与写法完全不变。

ZLM 自身无签名能力，因此每个事件 URL 都带独立的能力令牌：

```
cap = base64url(HMAC-SHA256(CapabilitySecret, mediaServerId + "\n" + event))
```

生成（容器内/宿主机均可，一条命令算出全部 17 个事件）：

```bash
SECRET='<ZlmHook:CapabilitySecret>'
for e in on_server_started on_server_exited on_server_keepalive on_flow_report \
         on_http_access on_play on_publish on_record_mp4 on_record_ts on_rtsp_realm \
         on_rtsp_auth on_send_rtp_stopped on_shell_login on_stream_changed \
         on_stream_none_reader on_stream_not_found on_rtp_server_timeout; do
    cap=$(printf 'apiext-mediakit\n%s' "$e" | openssl dgst -sha256 -hmac "$SECRET" -binary \
          | openssl base64 -A | tr '+/' '-_' | tr -d '=')
    echo "$e=http://127.0.0.1:8080/api/zlm/hook/$e?cap=$cap"
done
```

把输出逐行贴进 `deploy/docker/config.ini` 的 `[hook]` 段，并确保 `enable=1`。

| 事件 | 作用 |
|---|---|
| `on_server_started` | 清空在线缓存 |
| `on_stream_changed` | 在线流状态补位（10 分钟有效） |
| `on_stream_none_reader` | 按需断流 |
| `on_stream_not_found` | 按需拉流兜底 |
| `on_publish` | 推流鉴权（`app=rtp` 的国标收流放行） |
| `on_play` | 播放令牌校验 |
| `on_record_mp4` | 录像索引落库 |
| `on_rtp_server_timeout` | 国标收流超时后收尾 SIP 会话 |
| `on_http_access` | 拦截 `/record/` 等受保护路径 |
| `on_flow_report` | 流量统计（仅 Debug 日志） |
| `on_record_ts` | TS 切片完成（仅记录，回放走 mp4 索引） |
| `on_rtsp_realm` | 返回空 realm：不启用 RTSP 摘要鉴权（否则抓拍/VLC 被 401） |
| `on_rtsp_auth` | 仅 realm 非空时触发；平台一律拒绝 |
| `on_send_rtp_stopped` | 国标级联 RTP 转推停止（仅记录） |
| `on_server_exited` | ZLM 进程退出（Warning） |
| `on_server_keepalive` | 心跳（默认 30s，维护 `LastKeepaliveUtc`） |
| `on_shell_login` | telnet 调试口登录，默认拒绝（`ZlmHook:AllowShellLogin=true` 才放行） |

换 `CapabilitySecret` 或 `MediaServerId` 后必须重算全部 URL，否则 hook 全部 403（日志 `[Hook]` 会明确报原因）。

**分离部署额外注意**

- `config.ini` 里 hook URL 的 `127.0.0.1:8080` 必须改成**应用主机**地址，否则 ZLM 回调打不到应用。
- `ZlmHook__AllowedIps` 必须包含 **ZLM 主机 IP**（默认只有 `127.0.0.1` / `::1`），否则回调虽有正确 cap 仍会被来源 IP 白名单拒绝。
- 应用与 ZLM 不在同一主机时，`on_publish` / `on_play` 的鉴权链路依赖双向网络可达（应用 → ZLM API、ZLM → 应用 hook）。

---

## 7. GB28181 设备接入

设备端填写：

| 项 | 值 |
|---|---|
| SIP 服务器 IP | 平台 `Gb28181__LocalIp`（宿主机/应用主机 IP，不是 0.0.0.0） |
| SIP 端口 | `28060`（默认，见 `deploy/docker/.env` 的 `SIP_PORT`；不是 5060） |
| 平台 SIP 编码 | `34020000002000000001` |
| 域 | `3402000000` |
| 密码 | `Gb28181:Password`（镜像内默认 `My6moOWiyMoz5yJv`，可在 `deploy/docker/appsettings.Production.json` 覆盖） |

默认 **`Gb28181:EnableAuth = true`**：设备注册必须通过 Digest 鉴权，密码不对将被拒（401）。设备端接入密码必须与平台一致，全新部署无需兼容旧密码。

> 回退手段（仅紧急排障用，不推荐作为常态）：`deploy/docker/appsettings.Production.json` 加 `"EnableAuth": false` 后 `docker compose restart`；或在「高级配置 → SIP 接入」切到 `whitelist` 预登记模式。

接入流程与排障：

1. 设备列表 → 设备 **在线** → 详情 → **查询目录** → 「通道」出现通道（否则检查 28060/udp 与设备侧参数）
2. 通道行「播放」→ 后端日志出现 `[GB28181] 点播成功 ... 收流端口=3000x`
3. 若黑屏：确认 `Gb28181__MediaIp` 是 ZLM 主机可达 IP、`24000-24500/udp` 已放行、设备与该地址三层可达
4. 若点播超时：日志 `[GB28181] 点播失败：INVITE 未成功 ...`；设备返回 4xx/5xx 会记录状态码
5. 平台重启后设备不会立刻重注册，但心跳（`MESSAGE KEEPALIVE`）会凭数据库记录**自动恢复在线**（最多一个心跳周期）

---

## 8. 录像与存储

| 项 | 说明 |
|---|---|
| 录像目录 | 容器内 `/opt/media/bin/www/record`，挂载为命名卷 `zlm-record` |
| 索引来源 | `on_record_mp4` 回调为主链路；对账服务每 30 分钟扫最近 7 天补录/标记丢失 |
| 清理 | `Recording__RetentionDays` 定时清理；`0` 不清理 |
| 备份 | SMB/NFS：宿主机挂载后映射进容器（如 `/mnt/nas:/backup/nas`），在「高级配置 → 录像备份」填目标路径；S3：填 endpoint/bucket/AK/SK 直传 |
| 备份周期 | `Recording__BackupIntervalMinutes`，日志前缀 `[Backup]` |

---

## 9. 反向代理（可选）

Nginx 关键点：`/play/*` 既是 HTTP-FLV 长连接也是 WebSocket，必须放开升级头并关闭缓冲。

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }

server {
    listen 80;
    server_name nvr.example.com;
    client_max_body_size 64m;

    location / {
        proxy_pass http://127.0.0.1:28080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;      # PlayToken__BindClientIp 依赖
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_read_timeout 3600s;
        proxy_buffering off;                          # FLV/WS 必须关缓冲
    }
}
```

> 走反向代理后，`ZlMediaKit__PlayBaseUrl` 要么继续指向 ZLM 直连端口（28180，客户端需能访问），要么由代理额外转发 ZLM（`/live/` 等），二者选一，不要混用。
>
> 注意：应用当前**未启用 `ForwardedHeaders`**，所有来源 IP（鉴权日志、hook 白名单）取的都是 `Connection.RemoteIpAddress`。因此：
> - 反向代理后日志里的来源 IP 是代理地址，`X-Real-IP` 不参与判定；
> - `PlayToken__BindClientIp` 不要开启（否则令牌绑定到代理 IP，客户端一换就 403）。

---

## 10. 升级、回滚与备份

**升级**

```bash
cd apiext-mediakit-hub
git pull                      # 或上传新源码
cd deploy/docker
docker compose up -d --build --force-recreate
docker logs --tail=50 apiext-mediakit | grep '\[Startup\]'
```

数据库结构变更由 `DbInitializer` 的 `CREATE TABLE IF NOT EXISTS` 兜底，旧库可直接升级。

**回滚**

```bash
# 建议每次构建后给镜像打 tag，回滚时改 compose 的 image 即可
docker tag apiext-mediakit apiext-mediakit:261117-1140
# 回滚
docker compose down
# 修改 docker-compose.yml: image: apiext-mediakit:261117-1140  （并注释掉 build）
docker compose up -d
```

**数据备份**

```bash
# 停机一致性备份（推荐：SQLite 为 WAL 模式，热拷 db 可能拿到不一致快照）
cd apiext-mediakit-hub
docker compose -f deploy/docker/docker-compose.yml stop apiext-mediakit
tar czf apiext-mediakit-data-$(date +%F).tgz APP_DATA/     # 含 db 与 -wal/-shm
docker compose -f deploy/docker/docker-compose.yml start apiext-mediakit
```

录像数据在命名卷里（卷名 = `<compose 项目名>_zlm-record`，项目名默认取 compose 文件所在目录名）：

```bash
VOL=$(docker volume ls -q | grep 'zlm-record' | head -1)
docker run --rm -v "$VOL":/r -v "$PWD":/b alpine tar czf /b/record-$(date +%F).tgz -C /r .
```

---

## 11. 日志与排障

```bash
docker logs -f --tail=200 apiext-mediakit
docker logs apiext-mediakit 2>&1 | grep '\[ZLM\]'
docker logs apiext-mediakit 2>&1 | grep -E '\[GB28181\]|\[Hook\]'
```

| 前缀 | 定位 |
|---|---|
| `[Startup]` | 环境、监听地址、ZLM/播放基址、SIP、DB 路径 |
| `[Request]` | `/api/*` `/play/*` 的 4xx/5xx 与慢请求 |
| `[Token]` / `[PlayToken]` | 访问令牌、播放令牌校验失败 |
| `[ZLM]` | ZLM API 失败（30 秒窗口节流） |
| `[PlayProxy]` | 播放代理转发失败 |
| `[PullProxy]` / `[OnDemand]` | 拉流代理与按需拉流 |
| `[Onvif]` | ONVIF 连接/取流 |
| `[GB28181]` / `[Cascade]` | 国标点播、目录、级联 |
| `[Hook]` | ZLM 回调（403 时看这里的 cap/mediaServerId 提示） |
| `[Reconcile]` / `[Retention]` / `[RecordSchedule]` / `[Backup]` | 录像对账、清理、计划、备份 |

需要更细的日志时（临时）：`Logging__LogLevel__Apiext.MediaKit.Server=Debug`。

### 常见问题

| 现象 | 排查顺序 |
|---|---|
| 页面打不开 | `docker compose ps` → 端口映射 → `ss -lntp` 看端口占用 → 防火墙 |
| 播放器黑屏 / 一直加载 | `[PlayProxy]`/`[ZLM]` 日志 → ZLM 是否可达（`curl http://<zlm>:<port>/index/api/getServerConfig?secret=...`）→ 流是否在线 |
| 国标点播超时 | 设备在线？`[GB28181] 点播失败：INVITE 未成功` 里的原因 → 设备侧平台参数 |
| 国标点播成功但无画面 | `Gb28181__MediaIp` 是否指向 ZLM 主机；`24000-24500/udp` 是否放行；设备能否路由到该地址 |
| 播放 403 | `[PlayToken]`：地址过期/签名不符/IP 绑定；或 `[Token]`：访问令牌失效 |
| hook 全部 403 | `[Hook]`：cap 与密钥/`mediaServerId` 不匹配 → 按第 6 节的命令重算 17 个 `?cap=` |
| 设备显示离线但设备在线 | 平台重启后等一个心跳周期（自动恢复）；检查 28060/udp 是否放行 |
| 录像列表为空 | `[Hook] on_record_mp4` 是否到达；`Recording__SourcePath` 是否指向 record 目录 |

---

## 12. 生产安全清单

开箱默认值是为了「先跑起来」，正式交付前按顺序处理（启动日志会用 `[Startup]` Warning 提醒前两项）：

1. **管理员密码**：首次登录向导强制修改（`admin / admin123` 仅用于初次登录）。
2. **ZLM secret**：改 `deploy/docker/config.ini` 的 `[api] secret`，同步改 compose 的 `ZlMediaKit__Secret`（两者必须一致）。
3. **Hook 能力令牌**：改 `ZlmHook__CapabilitySecret`，再按第 6 节的 `openssl` 命令重算 17 个事件 URL 并回填 `config.ini`，重启生效。
4. **播放令牌密钥**：改 `PlayToken__Secret`（≥32 字节），轮换后已发出的直连地址全部失效属预期。
5. **SIP 密码**：改 `Gb28181__Password`，并在设备端同步修改。
6. **对外暴露面**：RTSP/RTMP 不受令牌保护，只应内网使用；WebRTC 受 `on_play` 校验（ZLM 上报 `schema=rtc`），直连信令地址必须带 `token=` 或 `play_token=`；HLS 直连是播放器级校验（`schema=hls`，有人正在观看时新请求不再回调 `on_play`），需要严格鉴权请走平台代理 `/play/.../hls.m3u8`；对外仅暴露 28080（Web）与 28180（ZLM HTTP）并按需经过反向代理。

## 13. 交付验收清单

- [ ] `install.sh` 端口预检通过，防火墙已放行（含 `24000-24500/udp`）
- [ ] 日志出现 `[Startup]` 两行，`PlayBaseUrl` 是客户端可达地址
- [ ] 已修改管理员密码，已生成访问令牌
- [ ] RTSP/HTTP-FLV 拉流设备接入成功，播放页有画面（1/4/9/16 分屏）
- [ ] ONVIF 设备可取流、云台可控
- [ ] GB28181 设备注册在线、查询目录出通道、点播有画面（MediaIp 指向 ZLM）
- [ ] 录像生成且录像回放可播；备份目标同步一次成功
- [ ] `config.ini` 的 `secret` 非默认值，hook 17 个事件均返回 200
- [ ] 已做一次 `APP_DATA` 与录像卷备份演练
