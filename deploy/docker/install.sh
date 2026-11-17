#!/bin/bash
# =============================================================================
# Apiext MediaKit 一键安装（镜像模式，交互式）
#
#   从 GHCR 拉取已构建镜像 → 交互式确认参数 → 生成配置 → 启动 → 健康检查
#
# 用法：
#   bash install.sh                  # 交互式（默认，逐项确认）
#   bash install.sh --yes            # 非交互：全部用默认值/环境变量，适合自动化
#   bash install.sh --dry-run        # 只做检测与参数确认，不落地、不启动
#
# 常用覆盖（交互模式下作为默认值；--yes 模式下直接生效）：
#   bash install.sh --dir /opt/apiext-mediakit --public-host 10.0.0.5
#   PUBLIC_HOST=10.0.0.5 APP_PORT=18080 bash install.sh --yes
#   IMAGE=ghcr.io/apiext-labs/apiext-mediakit:1.2.3 bash install.sh --yes
#   # 镜像为私有包时先 docker login ghcr.io -u <用户名> --password-stdin
#
# 支持发行版：Debian/Ubuntu、RHEL/CentOS/Rocky/AlmaLinux/Fedora、openEuler/Anolis、
#             以及 Docker 可用的其他 Linux（工具缺失会自动降级探测）。
# =============================================================================
set -uo pipefail

# ---------------------------------------------------------------------------
# 默认值（均可被环境变量/命令行覆盖）
# ---------------------------------------------------------------------------
IMAGE="${IMAGE:-ghcr.io/apiext-labs/apiext-mediakit:latest}"
INSTALL_DIR="${INSTALL_DIR:-/data/apiext-mediakit}"
CONTAINER_NAME="${CONTAINER_NAME:-apiext-mediakit}"
PUBLIC_HOST="${PUBLIC_HOST:-}"
APP_PORT="${APP_PORT:-28080}"
SIP_PORT="${SIP_PORT:-28060}"
ZLM_HTTP_PORT="${ZLM_HTTP_PORT:-28180}"
RTSP_PORT="${RTSP_PORT:-28554}"
RTMP_PORT="${RTMP_PORT:-28335}"
RTP_PORT="${RTP_PORT:-21000}"
RTC_PORT="${RTC_PORT:-28000}"
SRT_PORT="${SRT_PORT:-29000}"
RTP_MIN="${RTP_MIN:-24000}"
RTP_MAX="${RTP_MAX:-24500}"
TZ_VALUE="${TZ:-Asia/Shanghai}"
SECRET="${ZLM_SECRET:-7f3a9c2e-1b4d-4e8f-9a5c-3d6e2b7a1c8f}"

AUTO_YES=0
DRY_RUN=0
INTERACTIVE=0
COMPOSE_CMD=""
DISTRO_ID="unknown"
DISTRO_PRETTY="unknown"
PKG_MGR=""

# ---------------------------------------------------------------------------
# 输出
# ---------------------------------------------------------------------------
if [ -t 1 ]; then
    C_INFO=$'\033[36m'; C_OK=$'\033[32m'; C_WARN=$'\033[33m'; C_ERR=$'\033[31m'
    C_DIM=$'\033[2m'; C_OFF=$'\033[0m'
else
    C_INFO=""; C_OK=""; C_WARN=""; C_ERR=""; C_DIM=""; C_OFF=""
fi

step()  { printf '\n%s[%s/8]%s %s\n' "$C_INFO" "$1" "$C_OFF" "$2"; }
say()   { printf '  %s\n' "$*"; }
ok()    { printf '  %s✓%s %s\n' "$C_OK" "$C_OFF" "$*"; }
warn()  { printf '  %s!%s %s\n' "$C_WARN" "$C_OFF" "$*"; }
ding()  { printf '  %s✗%s %s\n' "$C_ERR" "$C_OFF" "$*"; }
die()   { printf '\n%s[失败]%s %s\n' "$C_ERR" "$C_OFF" "$*" >&2; exit 1; }
hr()    { printf '%s%s%s\n' "$C_DIM" "-----------------------------------------------------------------------------" "$C_OFF"; }

# 交互提问：ask <提示> <变量名> <默认值>
ask() {
    local prompt="$1" __var="$2" def="${3:-}" ans=""
    if [ "$INTERACTIVE" -eq 0 ]; then
        eval "$__var=\$def"
        return 0
    fi
    if [ -n "$def" ]; then
        printf '  %s %s[%s]%s: ' "$prompt" "$C_DIM" "$def" "$C_OFF"
    else
        printf '  %s: ' "$prompt"
    fi
    read -r ans || ans=""
    [ -z "$ans" ] && ans="$def"
    eval "$__var=\$ans"
}

# 是/否提问：confirm <提示> <默认 y|n>
confirm() {
    local prompt="$1" def="${2:-y}" ans=""
    if [ "$INTERACTIVE" -eq 0 ]; then
        [ "$def" = "y" ]
        return $?
    fi
    if [ "$def" = "y" ]; then
        printf '  %s %s[Y/n]%s: ' "$prompt" "$C_DIM" "$C_OFF"
    else
        printf '  %s %s[y/N]%s: ' "$prompt" "$C_DIM" "$C_OFF"
    fi
    read -r ans || ans=""
    [ -z "$ans" ] && ans="$def"
    case "$ans" in y|Y|yes|YES) return 0 ;; *) return 1 ;; esac
}

is_port() { case "$1" in ''|*[!0-9]*) return 1 ;; esac; [ "$1" -ge 1 ] && [ "$1" -le 65535 ]; }

is_ipv4() {
    local ip="$1" IFS=. a b c d
    [ -n "$ip" ] || return 1
    case "$ip" in *[!0-9.]*) return 1 ;; esac
    set -- $ip
    [ $# -eq 4 ] || return 1
    for o in "$@"; do
        case "$o" in ''|0*[0-9]*) return 1 ;; esac
        [ "$o" -ge 0 ] 2>/dev/null && [ "$o" -le 255 ] || return 1
    done
    return 0
}

# ---------------------------------------------------------------------------
# 参数
# ---------------------------------------------------------------------------
while [ $# -gt 0 ]; do
    case "$1" in
        --dir)           INSTALL_DIR="$2"; shift 2 ;;
        --image)         IMAGE="$2"; shift 2 ;;
        --public-host)   PUBLIC_HOST="$2"; shift 2 ;;
        --app-port)      APP_PORT="$2"; shift 2 ;;
        --yes|-y)        AUTO_YES=1; shift ;;
        --dry-run)       DRY_RUN=1; shift ;;
        -h|--help)
            sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) die "未知参数：$1（用 --help 查看用法）" ;;
    esac
done

if [ "$AUTO_YES" -eq 0 ] && [ -t 0 ]; then
    INTERACTIVE=1
fi

# 记录脚本自身路径（后面会 cd 到安装目录，复制回装目录时需要绝对路径）
SCRIPT_PATH="$0"
case "$SCRIPT_PATH" in
    /*) ;;
    *) SCRIPT_PATH="$(pwd)/$SCRIPT_PATH" ;;
esac

# ---------------------------------------------------------------------------
step 1 "环境检测"
# ---------------------------------------------------------------------------
if [ -r /etc/os-release ]; then
    # shellcheck disable=SC1091
    . /etc/os-release 2>/dev/null || true
    DISTRO_ID="${ID:-unknown}"
    DISTRO_PRETTY="${PRETTY_NAME:-$DISTRO_ID}"
fi
DISTRO_LIKE="${ID_LIKE:-}"

case " $DISTRO_ID $DISTRO_LIKE " in
    *debian*|*ubuntu*)                                     PKG_MGR="apt" ;;
    *rhel*|*centos*|*fedora*|*rocky*|*almalinux*|*openeuler*|*anolis*|*kylin*) PKG_MGR="dnf" ;;
    *)                                                     PKG_MGR="" ;;
esac
if [ "$PKG_MGR" = "dnf" ] && ! command -v dnf >/dev/null 2>&1; then PKG_MGR="yum"; fi

ok "系统：$DISTRO_PRETTY（$(uname -m)，内核 $(uname -r)）"

mem_mb="$(awk '/MemTotal/{print int($2/1024)}' /proc/meminfo 2>/dev/null || echo 0)"
disk_gb="$(df -Pk / 2>/dev/null | awk 'NR==2{print int($4/1024/1024)}')"
say "内存 ${mem_mb}MB，根分区可用 ${disk_gb:-?}GB"
[ "${mem_mb:-0}" -lt 1900 ] && warn "内存小于 2GB，多路转码/录像时可能吃紧"
[ -n "${disk_gb:-}" ] && [ "$disk_gb" -lt 5 ] && warn "根分区可用不足 5GB，镜像约 1.2GB，构建/录像需更多空间"

if [ "$(getenforce 2>/dev/null || echo Disabled)" = "Enforcing" ]; then
    warn "SELinux 处于 Enforcing：若挂载目录读写异常，可临时 setenforce 0 或为该目录打 :z 标签"
fi

# --- Docker ---
if ! command -v docker >/dev/null 2>&1; then
    ding "未安装 Docker"
    if confirm "是否现在用官方脚本安装 Docker（get.docker.com，国内镜像源）？" "y"; then
        command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1 || die "缺少 curl/wget，无法下载安装脚本"
        say "正在安装 Docker ..."
        if command -v curl >/dev/null 2>&1; then
            curl -fsSL https://get.docker.com | sh -s -- --mirror Aliyun || die "Docker 安装失败"
        else
            wget -qO- https://get.docker.com | sh -s -- --mirror Aliyun || die "Docker 安装失败"
        fi
        if command -v systemctl >/dev/null 2>&1; then
            systemctl enable --now docker >/dev/null 2>&1 || true
        else
            service docker start >/dev/null 2>&1 || true
        fi
        sleep 3
    else
        die "请先安装 Docker 后重跑本脚本"
    fi
fi

docker info >/dev/null 2>&1 || {
    ding "Docker 服务未运行或无权限"
    say "可尝试：systemctl start docker（或 service docker start）；非 root 用户请 sudo 执行本脚本"
    exit 1
}
ok "Docker $(docker version --format '{{.Server.Version}}' 2>/dev/null)"

if docker compose version >/dev/null 2>&1; then
    COMPOSE_CMD="docker compose"
elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE_CMD="docker-compose"
else
    ding "未找到 docker compose（v2 插件）或 docker-compose（v1）"
    if confirm "是否现在安装 docker compose 插件？" "y"; then
        case "$PKG_MGR" in
            apt) apt-get update -qq && (apt-get install -y docker-compose-plugin || apt-get install -y docker-compose) ;;
            dnf|yum) "$PKG_MGR" install -y docker-compose-plugin || "$PKG_MGR" install -y docker-compose ;;
            *) die "无法自动安装，请手动安装 docker compose" ;;
        esac
        docker compose version >/dev/null 2>&1 && COMPOSE_CMD="docker compose"
        command -v docker-compose >/dev/null 2>&1 && [ -z "$COMPOSE_CMD" ] && COMPOSE_CMD="docker-compose"
        [ -n "$COMPOSE_CMD" ] || die "docker compose 安装后仍不可用"
    else
        die "缺少 docker compose，无法继续"
    fi
fi
ok "编排工具：$COMPOSE_CMD"

# 工具探测（缺失不影响安装，只是检查方式降级）
has_ss=0; command -v ss >/dev/null 2>&1 && has_ss=1
has_netstat=0; command -v netstat >/dev/null 2>&1 && has_netstat=1
has_curl=0; command -v curl >/dev/null 2>&1 && has_curl=1
has_wget=0; command -v wget >/dev/null 2>&1 && has_wget=1
[ "$has_ss$has_netstat" = "00" ] && warn "未找到 ss/netstat，将跳过端口占用预检"
[ "$has_curl$has_wget" = "00" ] && warn "未找到 curl/wget，健康检查将只做 TCP 探测"
ok "检查工具：ss=$has_ss netstat=$has_netstat curl=$has_curl wget=$has_wget"

# ---------------------------------------------------------------------------
step 2 "安装参数"
# ---------------------------------------------------------------------------
detect_ip() {
    local ip=""
    if command -v ip >/dev/null 2>&1; then
        ip="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src"){print $(i+1); exit}}')"
        [ -n "$ip" ] || ip="$(ip -4 addr show scope global 2>/dev/null | awk '/inet /{sub(/\/.*/,"",$2); print $2; exit}')"
    fi
    [ -n "$ip" ] || ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
    [ -n "$ip" ] || ip="$(ifconfig 2>/dev/null | awk '/inet /{sub(/addr:/,"",$2); print $2; exit}')"
    printf '%s' "$ip"
}

detected_ip="$(detect_ip)"
ask "安装目录" INSTALL_DIR "$INSTALL_DIR"
ask "对外访问 IP（浏览器/国标设备可达的宿主机地址）" PUBLIC_HOST "${PUBLIC_HOST:-$detected_ip}"

while ! is_ipv4 "$PUBLIC_HOST"; do
    ding "「$PUBLIC_HOST」不是合法 IPv4 地址"
    [ "$INTERACTIVE" -eq 1 ] || die "PUBLIC_HOST 非法：$PUBLIC_HOST"
    ask "请重新输入对外 IP" PUBLIC_HOST "$detected_ip"
done
case "$PUBLIC_HOST" in
    127.*) die "不能用回环地址 127.0.0.1，设备/浏览器访问不到" ;;
esac
ok "对外地址：$PUBLIC_HOST"

ask "时区" TZ_VALUE "$TZ_VALUE"

if confirm "使用默认端口方案（Web ${APP_PORT} / SIP ${SIP_PORT} / ZLM ${ZLM_HTTP_PORT} / RTSP ${RTSP_PORT} / RTMP ${RTMP_PORT}）？" "y"; then
    :
else
    ask "  Web/REST 端口"      APP_PORT      "$APP_PORT"
    ask "  GB28181 SIP 端口"   SIP_PORT      "$SIP_PORT"
    ask "  ZLM HTTP 端口"      ZLM_HTTP_PORT "$ZLM_HTTP_PORT"
    ask "  RTSP 端口"          RTSP_PORT     "$RTSP_PORT"
    ask "  RTMP 端口"          RTMP_PORT     "$RTMP_PORT"
    ask "  RTP over TCP 端口"  RTP_PORT      "$RTP_PORT"
    ask "  WebRTC 端口"        RTC_PORT      "$RTC_PORT"
    ask "  SRT 端口"           SRT_PORT      "$SRT_PORT"
    ask "  国标收流端口段起"   RTP_MIN       "$RTP_MIN"
    ask "  国标收流端口段止"   RTP_MAX       "$RTP_MAX"
fi

for v in "$APP_PORT" "$SIP_PORT" "$ZLM_HTTP_PORT" "$RTSP_PORT" "$RTMP_PORT" "$RTP_PORT" "$RTC_PORT" "$SRT_PORT" "$RTP_MIN" "$RTP_MAX"; do
    is_port "$v" || die "端口非法：$v"
done
[ "$RTP_MIN" -le "$RTP_MAX" ] || die "收流端口段起止顺序错误：$RTP_MIN-$RTP_MAX"
ok "端口：Web=$APP_PORT SIP=$SIP_PORT ZLM=$ZLM_HTTP_PORT RTSP=$RTSP_PORT RTMP=$RTMP_PORT RTP=$RTP_PORT RTC=$RTC_PORT SRT=$SRT_PORT 收流=$RTP_MIN-$RTP_MAX"

# 镜像默认公开发布在 GitHub Container Registry；私有包请在运行前自行登录：
#   echo <PAT> | docker login ghcr.io -u <用户名> --password-stdin
if [ -n "${REGISTRY_PASSWORD:-}" ] && [ -n "${REGISTRY_USER:-}" ]; then
    if printf '%s' "$REGISTRY_PASSWORD" | docker login "$(printf '%s' "$IMAGE" | cut -d/ -f1)" -u "$REGISTRY_USER" --password-stdin >/dev/null 2>&1; then
        ok "已登录 $(printf '%s' "$IMAGE" | cut -d/ -f1)（REGISTRY_USER=$REGISTRY_USER）"
    else
        warn "docker login 失败，继续尝试拉取"
    fi
fi

# ---------------------------------------------------------------------------
step 3 "配置汇总确认"
# ---------------------------------------------------------------------------
[ "$DRY_RUN" -eq 1 ] && warn "DRY-RUN 模式：只显示配置，不写文件、不启动"

hr
say "镜像      $IMAGE"
say "安装目录  $INSTALL_DIR"
say "容器名    $CONTAINER_NAME"
say "对外地址  $PUBLIC_HOST"
say "时区      $TZ_VALUE"
say "端口      Web $APP_PORT | SIP $SIP_PORT/udp | ZLM $ZLM_HTTP_PORT | RTSP $RTSP_PORT | RTMP $RTMP_PORT"
say "          RTP $RTP_PORT | WebRTC $RTC_PORT(tcp+udp) | SRT $SRT_PORT/udp | 收流 $RTP_MIN-$RTP_MAX"
say "数据目录  $INSTALL_DIR/APP_DATA（SQLite）  +  compose 卷 zlm-record（录像）"
hr

if [ "$DRY_RUN" -eq 1 ]; then
    say "参数确认完毕（DRY-RUN 结束）"
    exit 0
fi
confirm "以上配置开始安装？" "y" || die "已取消"

# ---------------------------------------------------------------------------
step 4 "端口预检"
# ---------------------------------------------------------------------------
port_busy() {
    local proto="$1" port="$2"
    if [ "$has_ss" -eq 1 ]; then
        ss -ln${proto}H 2>/dev/null | awk '{print $4}' | grep -qE "[:.]${port}$"
    elif [ "$has_netstat" -eq 1 ]; then
        netstat -ln${proto} 2>/dev/null | awk 'NR>2{print $4}' | grep -qE "[:.]${port}$"
    else
        return 1
    fi
}

busy_list=""
for p in "$APP_PORT" "$ZLM_HTTP_PORT" "$RTSP_PORT" "$RTMP_PORT" "$RTP_PORT"; do
    port_busy t "$p" && busy_list="${busy_list} tcp/${p}"
done
for p in "$SIP_PORT" "$RTP_PORT" "$RTC_PORT" "$SRT_PORT"; do
    port_busy u "$p" && busy_list="${busy_list} udp/${p}"
done
if [ -n "$busy_list" ]; then
    ding "以下端口已被占用：$busy_list"
    if [ "$has_ss" -eq 1 ]; then ss -lntupH 2>/dev/null | awk 'NR>0' | grep -E ":(${APP_PORT}|${ZLM_HTTP_PORT}|${RTSP_PORT}|${RTMP_PORT}|${SIP_PORT})\b" | sed 's/^/    /'; fi
    die "请停掉占用进程，或改用其他端口重跑（交互中可选自定义端口）"
fi

# 内核临时端口段重叠提示
if [ -r /proc/sys/net/ipv4/ip_local_port_range ]; then
    read -r sys_lo sys_hi < /proc/sys/net/ipv4/ip_local_port_range
    if [ "$RTP_MAX" -ge "$sys_lo" ] && [ "$RTP_MIN" -le "$sys_hi" ]; then
        warn "收流端口段 $RTP_MIN-$RTP_MAX 与内核临时端口段 $sys_lo-$sys_hi 重叠，可能出现偶发 bind 失败"
        say "建议预留：sysctl -w net.ipv4.ip_local_reserved_ports=\"$RTP_MIN-$RTP_MAX\""
    fi
fi

if [ "$has_ss$has_netstat" = "00" ]; then
    warn "无 ss/netstat，跳过占用检查（启动后若失败请手动排查）"
else
    ok "端口均可用"
fi

# 防火墙提示
if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    warn "firewalld 正在运行，需放行端口："
    say "firewall-cmd --permanent --add-port=${APP_PORT}/tcp --add-port=${ZLM_HTTP_PORT}/tcp \\"
    say "  --add-port=${RTSP_PORT}/tcp --add-port=${RTMP_PORT}/tcp --add-port=${SIP_PORT}/udp \\"
    say "  --add-port=${RTP_PORT}/tcp --add-port=${RTP_PORT}/udp --add-port=${RTC_PORT}/tcp --add-port=${RTC_PORT}/udp \\"
    say "  --add-port=${SRT_PORT}/udp --add-port=${RTP_MIN}-${RTP_MAX}/tcp --add-port=${RTP_MIN}-${RTP_MAX}/udp"
    say "firewall-cmd --reload"
elif command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -qi active; then
    warn "ufw 已启用，需放行：ufw allow ${APP_PORT}/tcp; ufw allow ${ZLM_HTTP_PORT}/tcp; ufw allow ${SIP_PORT}/udp; ufw allow ${RTP_MIN}:${RTP_MAX}/tcp; ufw allow ${RTP_MIN}:${RTP_MAX}/udp"
fi

# ---------------------------------------------------------------------------
step 5 "配置 dockerd（端口段映射必须关闭 userland proxy）"
# ---------------------------------------------------------------------------
DAEMON_JSON=/etc/docker/daemon.json
restart_docker() {
    if command -v systemctl >/dev/null 2>&1; then
        systemctl restart docker || return 1
    else
        service docker restart || return 1
    fi
    sleep 5
    docker info >/dev/null 2>&1
}

if [ -f "$DAEMON_JSON" ] && grep -q '"userland-proxy"[[:space:]]*:[[:space:]]*false' "$DAEMON_JSON"; then
    ok "userland-proxy 已关闭"
else
    warn "收流端口段 ${RTP_MIN}-${RTP_MAX}（$((RTP_MAX - RTP_MIN + 1)) 个端口 × TCP/UDP）在开启 userland proxy 时会让 dockerd 为每个映射拉起进程，"
    warn "表现为 compose up 卡住 / failed to start userland proxy / 负载飙升。"
    if confirm "现在写入 \"userland-proxy\": false 到 $DAEMON_JSON 并重启 docker？" "y"; then
        [ -f "$DAEMON_JSON" ] && cp -a "$DAEMON_JSON" "${DAEMON_JSON}.bak.$(date +%Y%m%d-%H%M%S)"
        wrote=0
        if command -v python3 >/dev/null 2>&1; then
            python3 - "$DAEMON_JSON" <<'PY' && wrote=1
import json, os, sys
path = sys.argv[1]
data = {}
if os.path.exists(path):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            data = json.load(fh) or {}
    except Exception:
        data = {}
if not isinstance(data, dict):
    data = {}
data["userland-proxy"] = False
with open(path, "w", encoding="utf-8") as fh:
    json.dump(data, fh, indent=2, ensure_ascii=False)
    fh.write("\n")
PY
        elif command -v jq >/dev/null 2>&1; then
            if [ -f "$DAEMON_JSON" ]; then
                tmp="$(mktemp)"
                jq '. + {"userland-proxy": false}' "$DAEMON_JSON" > "$tmp" && mv "$tmp" "$DAEMON_JSON" && wrote=1
            else
                printf '{\n  "userland-proxy": false\n}\n' > "$DAEMON_JSON" && wrote=1
            fi
        elif [ -f "$DAEMON_JSON" ]; then
            # 无 python3/jq：在首个 { 后插入一行（原文件必须是合法 JSON 对象）
            if grep -q '"userland-proxy"' "$DAEMON_JSON"; then
                sed -i 's/"userland-proxy"[[:space:]]*:[[:space:]]*true/"userland-proxy": false/' "$DAEMON_JSON" && wrote=1
            else
                sed -i '0,/^{/s//{\n  "userland-proxy": false,/' "$DAEMON_JSON" && wrote=1
            fi
        else
            printf '{\n  "userland-proxy": false\n}\n' > "$DAEMON_JSON" && wrote=1
        fi

        if [ "$wrote" -eq 1 ] && restart_docker; then
            ok "已写入并重启 docker（原文件已备份）"
        else
            warn "写入或重启失败，请手动在 $DAEMON_JSON 加 \"userland-proxy\": false 后重启 docker"
        fi
    else
        warn "已跳过：容器可能因端口映射过多而启动失败"
    fi
fi

# ---------------------------------------------------------------------------
step 6 "生成配置文件：$INSTALL_DIR"
# ---------------------------------------------------------------------------
mkdir -p "$INSTALL_DIR/APP_DATA" || die "无法创建目录 $INSTALL_DIR"
cd "$INSTALL_DIR" || die "无法进入 $INSTALL_DIR"

cat > .env <<EOF
# Apiext MediaKit 部署环境（由 install.sh 生成，可直接编辑后 $COMPOSE_CMD up -d）
IMAGE=$IMAGE
CONTAINER_NAME=$CONTAINER_NAME
PUBLIC_HOST=$PUBLIC_HOST
APP_PORT=$APP_PORT
SIP_PORT=$SIP_PORT
ZLM_HTTP_PORT=$ZLM_HTTP_PORT
RTSP_PORT=$RTSP_PORT
RTMP_PORT=$RTMP_PORT
RTP_PORT=$RTP_PORT
RTC_PORT=$RTC_PORT
SRT_PORT=$SRT_PORT
RTP_RANGE=$RTP_MIN-$RTP_MAX
RTP_MIN=$RTP_MIN
RTP_MAX=$RTP_MAX
TZ=$TZ_VALUE
ZLM_SECRET=$SECRET
EOF
ok ".env"

cat > config.ini <<EOF
; ZLMediaKit 配置（由 install.sh 生成；改端口时与 .env 保持一致）
[general]
mediaServerId=apiext-mediakit
streamNoneReaderDelayMS=10000

[api]
apiDebug=0
secret=$SECRET

; 对外端口全部为非常用端口，且宿主 = 容器 1:1 映射
[http]
port=$ZLM_HTTP_PORT

[rtsp]
port=$RTSP_PORT

[rtmp]
port=$RTMP_PORT

[rtc]
port=$RTC_PORT
tcpPort=$RTC_PORT

[srt]
port=$SRT_PORT

; RTP over TCP 固定端口在 [rtp_proxy] port（ZLM 没有独立的 [rtp] 段）
[rtp_proxy]
port=$RTP_PORT
port_range=$RTP_MIN-$RTP_MAX
timeoutSec=15

; WebHook 回调到同容器内的 .NET 应用（127.0.0.1:8080）
; 换 ZLM_SECRET/密钥或 mediaServerId 后需重算全部 ?cap=：
;   for e in on_server_started on_server_exited on_server_keepalive on_flow_report \\
;            on_http_access on_play on_publish on_record_mp4 on_record_ts on_rtsp_realm \\
;            on_rtsp_auth on_send_rtp_stopped on_shell_login on_stream_changed \\
;            on_stream_none_reader on_stream_not_found on_rtp_server_timeout; do
;     printf '%s\\n%s' apiext-mediakit "\$e" | openssl dgst -sha256 -hmac "\$SECRET" -binary \\
;       | openssl base64 -A | tr '+/' '-_' | tr -d '='; done
[hook]
enable=1
timeoutSec=3
retry=1
retry_delay=2.0
on_server_started=http://127.0.0.1:8080/api/zlm/hook/on_server_started?cap=lEq52Q3d1sXCGp0GsIFq3yGq0ybjQg4MDzsbkmqlLd4
on_stream_changed=http://127.0.0.1:8080/api/zlm/hook/on_stream_changed?cap=ND2fdn9qPK_pL4oMzIuhjVfJzTg_hfKCobZnctvErzo
on_stream_none_reader=http://127.0.0.1:8080/api/zlm/hook/on_stream_none_reader?cap=Qq-t0ZynQjJrs8xTYhW4SuUG_sZu-ZeLDah6XujWC_c
on_stream_not_found=http://127.0.0.1:8080/api/zlm/hook/on_stream_not_found?cap=YHYO7Nln-45tnS3I0zcbuQdqNgOcPrzZ_lW1WBhE1uY
on_publish=http://127.0.0.1:8080/api/zlm/hook/on_publish?cap=DnbleVsRpwyNf_RGYJrn1_oIweTfXszl3CI6EE1gFHs
on_play=http://127.0.0.1:8080/api/zlm/hook/on_play?cap=hzRl4dNVIQe8ehdvj-JiVaHDe1658WWEaXylhxjAF0c
on_record_mp4=http://127.0.0.1:8080/api/zlm/hook/on_record_mp4?cap=PhVfP-5ie4i_eaFXfWXIBAYb611sAQxEXppaGewDjs8
on_rtp_server_timeout=http://127.0.0.1:8080/api/zlm/hook/on_rtp_server_timeout?cap=0l16s6ES9S3g7CDcSPMKHfRNREuiDoQuPUL_RpxYD1k
on_http_access=http://127.0.0.1:8080/api/zlm/hook/on_http_access?cap=DlSzSWbBoEcde_AQ3mxtEdFxZZeRkLHG9kb9oRDCCuY
; 其余事件：只记录/默认拒绝（详见 deploy/docker/config.ini 注释）
on_flow_report=http://127.0.0.1:8080/api/zlm/hook/on_flow_report?cap=Fk2L7hvQkngnFAQzlbxxqEvUNUJb1geQgD4eiARW06M
on_record_ts=http://127.0.0.1:8080/api/zlm/hook/on_record_ts?cap=JiYhmKRnH4p_HmE3QnhJPCJgY1rBz48u9R3OOzoBL_s
on_rtsp_realm=http://127.0.0.1:8080/api/zlm/hook/on_rtsp_realm?cap=uyHymrv1FHoBG8KcI69R0Mq-olBH1g9LG6s1TZlQIyg
on_rtsp_auth=http://127.0.0.1:8080/api/zlm/hook/on_rtsp_auth?cap=wKBdaSkM9wPgKAB4ih87XQb1v4sLjg5-FF6PH82NYpM
on_send_rtp_stopped=http://127.0.0.1:8080/api/zlm/hook/on_send_rtp_stopped?cap=Ab8cL8z0kTf048uCui2wZ8-qzAVBMzjZQP6MiEto0L0
on_server_exited=http://127.0.0.1:8080/api/zlm/hook/on_server_exited?cap=o72S3Gjw3d1r4weMkIJ0iTqt-s4Koxg7MDner_AH75o
on_server_keepalive=http://127.0.0.1:8080/api/zlm/hook/on_server_keepalive?cap=5Em8Y5Njv-Znky_ABPNQKzW7AEy04dzRU7uFG7ri29w
on_shell_login=http://127.0.0.1:8080/api/zlm/hook/on_shell_login?cap=geeoymLzPajltniJICe8I3Oghq3sBKD4f39yKgWW0Fc
stream_changed_schemas=rtsp/rtmp
EOF
ok "config.ini"

if [ -f appsettings.Production.json ]; then
    ok "appsettings.Production.json（已存在，保留不覆盖）"
else
    cat > appsettings.Production.json <<'EOF'
{
  // 宿主可编辑的配置覆盖层（挂载到容器内 /app/appsettings.Production.json）
  // 优先级：环境变量 > 本文件 > 镜像内 appsettings.json；改完 $COMPOSE_CMD restart 生效
  // 接入密码 Gb28181:Password 与鉴权开关 EnableAuth 默认已在镜像内配置（EnableAuth=true）
  "Logging": {
    "LogLevel": {
      "Apiext.MediaKit.Server": "Information"
    }
  },
  "Gb28181": {
    "SipId": "34020000002000000001",
    "SipDomain": "3402000000",
    "Realm": "3402000000",
    "Cascade": {
      "Enabled": false,
      "LocalId": ""
    }
  },
  "Recording": {
    "RetentionDays": 0,
    "Schedule": ""
  }
}
EOF
    ok "appsettings.Production.json"
fi

cat > docker-compose.yml <<'EOF'
# Apiext MediaKit（镜像模式，由 install.sh 生成）
services:
  apiext-mediakit:
    image: ${IMAGE}
    container_name: ${CONTAINER_NAME}
    ports:
      - "${APP_PORT}:8080"                      # 应用（Web / REST / 播放代理）
      - "${SIP_PORT}:${SIP_PORT}/udp"           # GB28181 SIP
      - "${ZLM_HTTP_PORT}:${ZLM_HTTP_PORT}"     # ZLM HTTP（FLV/HLS/WebRTC 信令）
      - "${RTSP_PORT}:${RTSP_PORT}"             # RTSP
      - "${RTMP_PORT}:${RTMP_PORT}"             # RTMP
      - "${RTP_PORT}:${RTP_PORT}"               # RTP over TCP
      - "${RTP_PORT}:${RTP_PORT}/udp"
      - "${RTC_PORT}:${RTC_PORT}/udp"           # WebRTC 媒体（UDP + TCP 都必须映射）
      - "${RTC_PORT}:${RTC_PORT}/tcp"
      - "${SRT_PORT}:${SRT_PORT}/udp"           # SRT
      - "${RTP_RANGE}:${RTP_RANGE}/udp"         # GB28181 收流段
      - "${RTP_RANGE}:${RTP_RANGE}"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://0.0.0.0:8080
      - App__PublicPort=${APP_PORT}
      - TZ=${TZ}
      - ConnectionStrings__Default=Data Source=APP_DATA/apiext-mediakit.db
      - ZlMediaKit__BaseUrl=http://127.0.0.1:${ZLM_HTTP_PORT}
      - ZlMediaKit__Secret=${ZLM_SECRET}
      - ZlMediaKit__PublicHost=${PUBLIC_HOST}
      - ZlMediaKit__PublicHttpPort=${ZLM_HTTP_PORT}
      - ZlMediaKit__RtspPort=${RTSP_PORT}
      - ZlMediaKit__RtmpPort=${RTMP_PORT}
      - Gb28181__SipPort=${SIP_PORT}
      - Gb28181__LocalIp=${PUBLIC_HOST}
      - Gb28181__MediaIp=${PUBLIC_HOST}
      - Gb28181__RtpPortMin=${RTP_MIN}
      - Gb28181__RtpPortMax=${RTP_MAX}
      - Recording__SourcePath=/opt/media/bin/www/record
    volumes:
      - ./APP_DATA:/app/APP_DATA
      - ./config.ini:/opt/media/conf/config.ini
      - ./appsettings.Production.json:/app/appsettings.Production.json:ro
      - zlm-record:/opt/media/bin/www/record
    restart: unless-stopped

volumes:
  zlm-record:
EOF
ok "docker-compose.yml"

cp -f "$SCRIPT_PATH" "$INSTALL_DIR/install.sh" 2>/dev/null && chmod +x "$INSTALL_DIR/install.sh" 2>/dev/null || true

# ---------------------------------------------------------------------------
step 7 "拉取镜像：$IMAGE"
# ---------------------------------------------------------------------------
say "拉取中（首次约 1.2GB，耐心等待）..."
if ! docker pull "$IMAGE"; then
    ding "拉取失败"
    say "私有包需先登录：echo <PAT> | docker login $(printf '%s' "$IMAGE" | cut -d/ -f1) -u <用户名> --password-stdin"
    say "然后重跑：bash $INSTALL_DIR/install.sh"
    exit 1
fi
ok "镜像已就绪"

# ---------------------------------------------------------------------------
step 8 "启动与健康检查"
# ---------------------------------------------------------------------------
$COMPOSE_CMD config --quiet || die "docker-compose.yml 校验失败"
$COMPOSE_CMD up -d 2>&1 | sed 's/^/  /'

http_code() {
    if [ "$has_curl" -eq 1 ]; then
        curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$1" 2>/dev/null
    elif [ "$has_wget" -eq 1 ]; then
        wget -q -T 5 -O /dev/null -S "$1" 2>&1 | awk '/HTTP\//{c=$2} END{print c}' | tr -d '\r'
    else
        printf ''
    fi
}

ready=0
for i in $(seq 1 40); do
    sleep 3
    if (echo > /dev/tcp/127.0.0.1/"$APP_PORT") >/dev/null 2>&1; then
        code="$(http_code "http://127.0.0.1:${APP_PORT}/login")"
        if [ "$code" = "200" ] || [ -z "$code" ]; then ready=1; break; fi
    fi
    state="$(docker inspect -f '{{.State.Running}}' "$CONTAINER_NAME" 2>/dev/null)"
    if [ "$state" != "true" ]; then
        ding "容器已退出，最近日志："
        docker logs --tail 30 "$CONTAINER_NAME" 2>&1 | sed 's/^/    /'
        exit 1
    fi
done

if [ "$ready" -ne 1 ]; then
    ding "120 秒内未就绪，最近日志："
    docker logs --tail 40 "$CONTAINER_NAME" 2>&1 | sed 's/^/    /'
    exit 1
fi
[ -n "$(http_code "http://127.0.0.1:${APP_PORT}/login")" ] && ok "Web 已响应" || ok "端口已监听（未装 curl/wget，未校验 HTTP 状态）"

zlm_ok="未知"
if [ "$has_curl" -eq 1 ]; then
    zlm_ok="失败"
    curl -s --max-time 5 "http://127.0.0.1:${ZLM_HTTP_PORT}/index/api/version?secret=${SECRET}" 2>/dev/null | grep -q '"code"' && zlm_ok="正常"
fi
[ "$zlm_ok" = "正常" ] && ok "ZLM 接口正常" || warn "ZLM 接口：$zlm_ok（可看 docker logs ${CONTAINER_NAME}）"

cat <<EOF

=============================================================================
 安装完成
=============================================================================
 管理平台    http://${PUBLIC_HOST}:${APP_PORT}/     （默认 admin / admin123，请立即修改）
 部署目录    ${INSTALL_DIR}
 容器名      ${CONTAINER_NAME}
 镜像        ${IMAGE}
 系统        ${DISTRO_PRETTY}

 端口        Web ${APP_PORT}  ZLM ${ZLM_HTTP_PORT}  RTSP ${RTSP_PORT}  RTMP ${RTMP_PORT}
             SIP ${SIP_PORT}/udp  RTP ${RTP_PORT}  WebRTC ${RTC_PORT}(tcp+udp)  SRT ${SRT_PORT}/udp
             国标收流 ${RTP_MIN}-${RTP_MAX}

 GB28181 设备端填写：
   SIP 服务器  ${PUBLIC_HOST}
   SIP 端口    ${SIP_PORT}（UDP，不是 5060）
   平台编码    34020000002000000001
   域          3402000000
   接入密码    My6moOWiyMoz5yJv

 常用命令（在 ${INSTALL_DIR} 下）：
   $COMPOSE_CMD logs -f --tail 100                    # 看日志
   $COMPOSE_CMD restart                               # 重启（改 config.ini/appsettings 后）
   $COMPOSE_CMD down                                  # 停止
   $COMPOSE_CMD pull && $COMPOSE_CMD up -d             # 升级到 latest
=============================================================================
EOF
