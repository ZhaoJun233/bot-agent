# QQ Chat Agent（headless）—— **产物式**运行时镜像（不在服务器上编译）
#
# 与 `src/QQChatAgent.Headless/Dockerfile` 的区别：
#   • 那个是多阶段构建：容器里拉 `dotnet/sdk:8.0`（约 800MB）**现场编译**源码 —— 适合
#     “clone 下来就能跑”的场景（别人自建、CI 里没预发布产物）。
#   • 这个只装运行时，把**开发机上已经发布好的** `app.tar.gz` 解到 /app —— 适合小内存机器。
#
# 为什么要多这一份（2026-09-18 实测踩坑）：
#   部署机只有 **956MB 内存**。用源码镜像构建时，`dotnet publish`（SDK + Roslyn）会把内存挤到
#   swap：swap 瞬间干到 1989/2047MB（97%），号主直接喊停。换产物式之后，服务器端只剩
#   `tar -xzf`，秒级完成、内存峰值几十 MB。（注意：swap 里被挤出去的页**不会自己回来** ——
#   哪怕构建结束了也一直占着；放掉的办法：重启那些无状态的服务容器（模型网关/管理器那类，
#   重启只影响几秒），但**不要重启 napcat**（登录态），也别用 `swapoff -a`
#   （可用内存比 swap 占用还小，会直接把服务拖死）。）
#
# 构建（构建上下文里必须已有 `app.tar.gz`，`deploy-qqchat.py` 会生成并上传）：
#   docker build -f Dockerfile.app -t qqchat-agent:latest .
FROM mcr.microsoft.com/dotnet/runtime:8.0

# ── docker CLI（可选能力：让容器里的 agent “透过 docker”看/改服务器）──
# 为什么要它：要用 /var/run/docker.sock 就得有个客户端，而 mcr 的运行时镜像里什么都没有。
# 只要 CLI（docker/docker 单个二进制），不带 dockerd/containerd —— 体积小很多。
# 这一段特意放在 COPY app.tar.gz **之前**：应用层重建时不会重复下载（部署机带宽/磁盘都紧）。
ARG DOCKER_CLI_VERSION=27.5.1
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends curl ca-certificates; \
    curl -fsSL -o /tmp/docker.tgz "https://download.docker.com/linux/static/stable/x86_64/docker-${DOCKER_CLI_VERSION}.tgz"; \
    tar -xzf /tmp/docker.tgz -C /tmp docker/docker; \
    install -m 0755 /tmp/docker/docker /usr/local/bin/docker; \
    apt-get purge -y curl; apt-get autoremove -y; \
    rm -rf /tmp/docker.tgz /tmp/docker /var/lib/apt/lists/*; \
    docker --version

WORKDIR /app
COPY app.tar.gz /tmp/app.tar.gz
RUN tar -xzf /tmp/app.tar.gz -C /app && rm /tmp/app.tar.gz

# 关闭遥测与诊断端口，减少常驻内存
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1 \
    TZ=Asia/Shanghai

# 镜像里没有 curl，用 bash 的 /dev/tcp 直连面板端口探活：
#   以前是 `dotnet QQChatAgent.Headless.dll --health` —— 每 30 秒把一个完整的 .NET 运行时冷启动一遍
#   （~40MB RSS 尖峰）。部署机只有 1 核，健康探测不该比业务还贵；/dev/tcp 零额外内存。
HEALTHCHECK --interval=60s --timeout=5s --start-period=25s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${QQCHAT_HEALTH_PORT:-8080} && printf 'GET /healthz HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n' >&3 && head -n 1 <&3 | grep -q '200'"]

ENTRYPOINT ["dotnet", "QQChatAgent.Headless.dll"]
