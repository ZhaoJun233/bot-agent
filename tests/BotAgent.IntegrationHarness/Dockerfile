# syntax=docker/dockerfile:1
# 集成测试容器：在容器网络里扮演「假 NapCat + 假大模型」，验证真正跑在容器里的机器人。
#   docker build -f tests/BotAgent.IntegrationHarness/Dockerfile -t qqchat-harness .
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj tests/BotAgent.IntegrationHarness/
RUN dotnet restore tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj
COPY tests/BotAgent.IntegrationHarness/ tests/BotAgent.IntegrationHarness/
RUN dotnet publish tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj \
        -c Release --no-restore -o /app /p:UseAppHost=false \
 && rm -rf /app/*.pdb

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30
WORKDIR /app
COPY --from=build /app/ /app/
ENTRYPOINT ["dotnet", "/app/BotAgent.IntegrationHarness.dll"]
