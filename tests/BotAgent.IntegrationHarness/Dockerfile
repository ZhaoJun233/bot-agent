# syntax=docker/dockerfile:1
# 集成测试容器：在容器网络里扮演「假 NapCat + 假大模型」，验证真正跑在容器里的机器人。
#   docker build -f tests/BotAgent.IntegrationHarness/Dockerfile -t qqchat-harness .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj tests/BotAgent.IntegrationHarness/
RUN dotnet restore tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj
COPY tests/BotAgent.IntegrationHarness/ tests/BotAgent.IntegrationHarness/
RUN dotnet publish tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj \
        -c Release --no-restore -o /app /p:UseAppHost=false \
 && rm -rf /app/*.pdb

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/ /app/
ENTRYPOINT ["dotnet", "/app/BotAgent.IntegrationHarness.dll"]
