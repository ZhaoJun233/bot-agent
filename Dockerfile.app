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

WORKDIR /app
COPY app.tar.gz /tmp/app.tar.gz
RUN tar -xzf /tmp/app.tar.gz -C /app && rm /tmp/app.tar.gz

# 关闭遥测与诊断端口，减少常驻内存
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1 \
    TZ=Asia/Shanghai

# 镜像里没有 curl，用机器人自带的健康探测（探 127.0.0.1:$QQCHAT_HEALTH_PORT/healthz）
HEALTHCHECK --interval=30s --timeout=5s --start-period=25s --retries=3 \
    CMD ["dotnet", "QQChatAgent.Headless.dll", "--health"]

ENTRYPOINT ["dotnet", "QQChatAgent.Headless.dll"]
