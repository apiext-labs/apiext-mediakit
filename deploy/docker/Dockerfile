# ============================================================================
# 单一镜像：ZLMediaKit（含 ffmpeg）+ .NET 应用（容器内编译 + 同时运行）
#
# 阶段：
#   1. zlm            ZLMediaKit 官方镜像（Ubuntu 24.04）作为运行基础，补装 ffmpeg/libicu
#   2. build          .NET 10 SDK，容器内编译当前项目源码
#   3. dotnet-runtime aspnet:10.0，仅用于提取 dotnet 运行时文件
#   4. final          最终镜像 = ZLM + .NET runtime + 应用，由 entrypoint.sh 同起两进程
#
# 最终镜像以 ZLM 镜像为基础（而非 aspnet）：MediaServer 在 Ubuntu 24.04 上编译，
# 链接了该系统的 ffmpeg / libssl 动态库，沿用同一发行版可保证其开箱即用；
# .NET 运行时则从 aspnet 镜像复制 /usr/share/dotnet 叠加进来。
# ============================================================================

# 基础镜像可通过 build-arg 覆盖（国内网络直连 mcr.microsoft.com 很慢时，
# 在 deploy/docker/.env 里指定加速源，例如 DOTNET_SDK_IMAGE=mcr.m.daocloud.io/dotnet/sdk:10.0）
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
ARG ZLM_IMAGE=zlmediakit/zlmediakit:master

# ---- 阶段 1：ZLMediaKit 运行环境（同时作为最终镜像基础） ----
FROM ${ZLM_IMAGE} AS zlm
# 官方镜像已带 ffmpeg；补装 ICU（.NET 全球化依赖），并自检 H.264/H.265 转码编码器
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg libicu74 && \
    rm -rf /var/lib/apt/lists/*
RUN ffmpeg -hide_banner -encoders 2>/dev/null | grep -q "libx264" || \
    { echo "ERROR: ffmpeg 缺少 libx264 编码器（H.264 转码不可用）"; exit 1; } ; \
    ffmpeg -hide_banner -encoders 2>/dev/null | grep -q "libx265" || \
    { echo "ERROR: ffmpeg 缺少 libx265 编码器（H.265 转码不可用）"; exit 1; }

# ---- 阶段 2：.NET 编译 ----
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY src/Apiext.MediaKit.Server/Apiext.MediaKit.Server.csproj src/Apiext.MediaKit.Server/
RUN dotnet restore src/Apiext.MediaKit.Server/Apiext.MediaKit.Server.csproj
COPY src/ src/
WORKDIR /src/src/Apiext.MediaKit.Server
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ---- 阶段 3：.NET 运行时（仅用于提取 dotnet 运行时文件） ----
FROM ${DOTNET_RUNTIME_IMAGE} AS dotnet-runtime

# ---- 阶段 4：最终镜像 = ZLM + .NET runtime + 应用 ----
FROM zlm
# 叠加 .NET 10 运行时
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH="/usr/share/dotnet:/opt/media/bin:${PATH}" \
    ASPNETCORE_URLS=http://0.0.0.0:8080

# .NET 应用产物
WORKDIR /app
COPY --from=build /app/publish .

# 启动脚本（同起 ZLM 与应用）；清理可能的 CRLF 再赋予执行权限
COPY deploy/docker/entrypoint.sh /entrypoint.sh
RUN sed -i 's/\r$//' /entrypoint.sh && chmod +x /entrypoint.sh

# ZLM 默认配置模板：宿主挂载的 config.ini 缺少某个段时，entrypoint 从这里补齐默认值，
# 保证「升级后新增的段（如 [ffmpeg]）不用手改宿主文件也能生效」。
COPY deploy/docker/config.ini /opt/media/conf/config.default.ini

# 应用 8080（宿主 28080）；ZLM 对外端口见 deploy/docker/config.ini，默认均为非常用端口：
# HTTP 28180 / RTSP 28554 / RTMP 28335 / RTP 21000 / WebRTC 28000 / SRT 29000 / 国标 RTP 24000-24500
EXPOSE 8080 28180 28554 28335 21000/tcp 21000/udp 28000/tcp 28000/udp 29000/udp 24000-24500/tcp 24000-24500/udp

ENTRYPOINT ["/entrypoint.sh"]
