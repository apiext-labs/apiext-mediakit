#!/bin/bash
set -e

ZLM_PID=""
APP_PID=""

cleanup() {
  local code=$?
  echo "[entrypoint] shutdown: terminating app & ZLM ..."
  [ -n "$APP_PID" ] && kill "$APP_PID" 2>/dev/null || true
  [ -n "$ZLM_PID" ] && kill "$ZLM_PID" 2>/dev/null || true
  wait 2>/dev/null || true
  exit "$code"
}
trap cleanup SIGTERM SIGINT EXIT

# 1) 启动 ZLMediaKit（前台模式，后台化运行；不加 -d 避免 daemonize 脱离进程树）
cd /opt/media/bin

# 配置兜底：宿主挂载的 config.ini 往往只写了少数几个段（其余依赖 ZLM 内置默认），
# 升级后新增的段（如 [ffmpeg]）或与平台强耦合的段（api secret / 各端口）若缺失，
# 会导致 ffmpeg 转码不生效、平台连不上 ZLM。这里在「运行时副本」里从镜像内默认模板
# 补齐缺失段 —— 不改宿主文件，且 ZLM 回写配置（如 secret 轮换）也只落到 /tmp。
ZLM_CONF="/opt/media/conf/config.ini"
DEFAULT_CONF="/opt/media/conf/config.default.ini"
RUNTIME_CONF="/tmp/zlm-config.ini"

if [ -f "$ZLM_CONF" ]; then
  cp "$ZLM_CONF" "$RUNTIME_CONF"
else
  cp "$DEFAULT_CONF" "$RUNTIME_CONF"
  echo "[entrypoint] 未找到 $ZLM_CONF，使用镜像内默认配置"
fi

append_section_if_missing() {
  local section="$1"
  grep -q "^\[$section\]" "$RUNTIME_CONF" && return 0
  awk -v sec="[$section]" '
    $0 == sec { found = 1; print; next }
    found && /^\[/ { found = 0 }
    found { print }
  ' "$DEFAULT_CONF" >> "$RUNTIME_CONF"
  echo "[entrypoint] config.ini 缺少 [$section] 段，已从镜像默认模板补齐"
}

for sec in api http rtsp rtmp rtc rtp_proxy hook ffmpeg; do
  append_section_if_missing "$sec"
done

./MediaServer -s default.pem -c "$RUNTIME_CONF" -l 0 &
ZLM_PID=$!

# 2) 启动 .NET 应用
cd /app
dotnet Apiext.MediaKit.Server.dll &
APP_PID=$!

echo "[entrypoint] started: ZLM=$ZLM_PID App=$APP_PID"

# 等待任一进程退出；随后 EXIT trap 会清理另一进程并沿用退出码
wait -n "$APP_PID" "$ZLM_PID"
