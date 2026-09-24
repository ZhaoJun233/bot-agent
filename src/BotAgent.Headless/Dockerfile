# syntax=docker/dockerfile:1

# ══════════════════════════════════════════════════════════════
#  Bot Agent（Headless）—— 多阶段构建（容器里现场编译源码）
#  构建上下文必须是仓库根目录：
#     docker build -f src/BotAgent.Headless/Dockerfile -t qqchat-agent .
#
#  ⚠ 内存小的机器（比如 1GB 的 VPS）**别用这个**：SDK + Roslyn 编译会把内存挤进 swap
#    （实测：956MB 的部署机被撑到 swap 97%，而且构建完也不还）。那种机器用仓库根目录的
#    `Dockerfile.app` —— 开发机先 `dotnet publish`，服务器只解压产物。
#    取舍写在两个 Dockerfile 的头部注释里，照着用就行。
# ══════════════════════════════════════════════════════════════

# ---------- 构建阶段 ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先只拷 csproj：本项目零 NuGet 依赖，这一层可长期命中缓存
COPY src/BotAgent.Headless/BotAgent.Headless.csproj src/BotAgent.Headless/
RUN dotnet restore src/BotAgent.Headless/BotAgent.Headless.csproj

# 再拷源码
COPY src/BotAgent.Headless/ src/BotAgent.Headless/

# UseAppHost=false：镜像里直接用 dotnet 启动，免得多带一个 apphost 可执行文件
RUN dotnet publish src/BotAgent.Headless/BotAgent.Headless.csproj \
        -c Release \
        --no-restore \
        -o /app/publish \
        /p:UseAppHost=false \
 && rm -rf /app/publish/*.pdb

# ---------- 运行阶段 ----------
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

# 时区数据：消息时间戳按本地时区显示（否则容器 UTC 会让模型的“现在几点”判断错位）
RUN apt-get update \
 && apt-get install -y --no-install-recommends tzdata \
 && rm -rf /var/lib/apt/lists/*

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    TZ=Asia/Shanghai \
    QQCHAT_DATA_DIR=/data \
    QQCHAT_HEALTH_PORT=8080 \
    QQCHAT_HEALTH_BIND=+ \
    QQCHAT_LOG_FILE=1

WORKDIR /app
COPY --from=build /app/publish/ /app/

# 非 root 运行（uid 固定，便于宿主机挂载目录授权）
RUN useradd --create-home --uid 10001 --shell /usr/sbin/nologin qqchat \
 && mkdir -p /data \
 && chown -R qqchat:qqchat /data /app
USER qqchat

# /data：设置、会话、人物档案、日志
VOLUME ["/data"]
# 8080=健康检查；3001=反向 WS 模式下机器人监听协议端连入
EXPOSE 8080 3001

# 健康探测：bash 的 /dev/tcp 直连面板端口（零额外进程内存）。
# 原来是 `dotnet BotAgent.Headless.dll --health`：每 30 秒冷启动一遍 .NET 运行时，
# 在 1 核小机器上是白白浪费（改用产物式镜像 Dockerfile.app 的部署机尤其明显）。
HEALTHCHECK --interval=60s --timeout=5s --start-period=15s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${QQCHAT_HEALTH_PORT:-8080} && printf 'GET /healthz HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n' >&3 && head -n 1 <&3 | grep -q '200'"]

ENTRYPOINT ["dotnet", "/app/BotAgent.Headless.dll"]
