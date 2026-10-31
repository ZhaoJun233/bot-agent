# 贡献指南

[English](CONTRIBUTING.en.md) | 简体中文

这个仓库是一份**隐私过滤后**的聊天应用公开镜像。**不要**把生产会话、成员档案、协议报文、日志正文、凭据或截图当成测试夹具或 issue 附件；只用合成数据。怀疑密钥泄露或隐私暴露时，请**私下**联系维护者，不要开公开 issue（见 [SECURITY.md](SECURITY.md)）。

## 分支与评审

- 从 `dev` 切一个短命的 `fix/*`、`feat/*` 或 `docs/*` 分支。
- 向 `dev` 开聚焦的 PR，关联 issue，并写清行为变化、风险、测试、迁移与回滚。
- 评审通过且检查全绿后，用**另一个**经过评审的 PR 把 `dev` 提升到 `main`。不要绕过必需检查合并，不要强推 `main`。
- 提交标题写清类型与范围，例如 `fix(persistence): reuse settings read for presence check`；必要时在正文写原因与验证方式。不要把不相关的工作塞进同一个提交。
- `Zhaodev` 是集成验证分支，不对外接受贡献；分支角色与合并路径见 [docs/architecture/branching-and-merge-path.md](docs/architecture/branching-and-merge-path.md)。

## 本地验证

在仓库根目录执行，需要 .NET 10、Node.js 22、Python 3.12。Windows 上用 PowerShell 7（`pwsh`），每条原生命令之后都要检查 `$LASTEXITCODE`，失败就停掉整条验证链。**不要**把生产配置或凭据带进合成运行：在隔离的验证进程里清空 `BOTAGENT_*`、`QQCHAT_*`、`OPENAI_*`、`MINIMAX_*`、`TTS_*`、`HARNESS_*`、`HEALTH_PORT`。依赖数据的独立探针必须用全新的合成临时根目录；集成场景会自建根目录，所以不要给它传一个会覆盖掉它的父级数据根别名。

```sh
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet run --project tests/BotAgent.ParticipationProbe/BotAgent.ParticipationProbe.csproj -c Release
dotnet run --project tests/BotAgent.EngineProbe/BotAgent.EngineProbe.csproj -c Release
python tests/BotAgent.ServerFilesProbe/test_server_files.py
dotnet run --project tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release
dotnet run --project tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release
dotnet run --project tests/BotAgent.ReverseTransportProbe/BotAgent.ReverseTransportProbe.csproj -c Release
dotnet run --project tests/BotAgent.DeadlineGateProbe/BotAgent.DeadlineGateProbe.csproj -c Release
dotnet run --project tests/BotAgent.FeishuRemediationProbe/BotAgent.FeishuRemediationProbe.csproj -c Release
dotnet run --project tests/BotAgent.SettingsScopeProbe/BotAgent.SettingsScopeProbe.csproj -c Release
dotnet run --project tests/BotAgent.SsrfProbe/BotAgent.SsrfProbe.csproj -c Release
dotnet run --project tests/BotAgent.McpProbe/BotAgent.McpProbe.csproj -c Release
dotnet run --project tests/BotAgent.ZbaProbe/BotAgent.ZbaProbe.csproj -c Release
dotnet run --project tests/BotAgent.ConcurrencyStressProbe/BotAgent.ConcurrencyStressProbe.csproj -c Release
dotnet run --project tests/BotAgent.ChaosFaultProbe/BotAgent.ChaosFaultProbe.csproj -c Release
dotnet run --project tests/BotAgent.ProtocolProbe/BotAgent.ProtocolProbe.csproj -c Release
dotnet run --project tests/BotAgent.PluginPackageProbe/BotAgent.PluginPackageProbe.csproj -c Release
dotnet run --project tests/BotAgent.PluginMarketProbe/BotAgent.PluginMarketProbe.csproj -c Release
dotnet run --project tests/BotAgent.PluginSettingsProbe/BotAgent.PluginSettingsProbe.csproj -c Release
dotnet run --project tests/BotAgent.SnapshotProbe/BotAgent.SnapshotProbe.csproj -c Release
dotnet run --project tests/BotAgent.PipelineEval/BotAgent.PipelineEval.csproj -c Release
(cd src/web && npm run typecheck && npm run test:unit)   # 面板前端检查（旧 FrontendProbe 已随 S6 退役）
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll
```

集成 harness 会另起一个机器人进程；跑集成验证前必须先重建 Headless（`dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild`），再构建 harness —— **只构建 harness 不会重建机器人**。合成集成套件跑 S1–S55 里**已注册的 53 个场景**：S2 并入 S1，S33 随 pi 桥退役，S42（官方通道）在 harness 与 CI 里都用假网关 + 回环 mock 注册，不碰真实开放平台凭据、也不证明生产互通。S52 覆盖统一平台开关、迁移、优先级、静音/停用、持久化与重启（合成夹具）；S53 覆盖服务器 agent 通过显式白名单调用回环 Streamable HTTP MCP 工具，并含一条被拒绝的负向用例；S54 覆盖真实宿主的 ZBA Bearer 边界、能力目录、结构化配置读写/冲突、任务提交/完成/幂等/取消、审批与事件、只读 CLI、磁盘静态资源，以及旧面板令牌的 fail-closed 分支。**harness 全绿不等于真实开放平台端到端覆盖**。`.github/workflows/ci.yml` 只显式跑它 `verify` job 里列出的探针与确定性评测，并把启用的集成场景切成并行矩阵（`Core & Chat`、`Media & Tools`、`Governance & Multi-Platform`，含 S42 与 S52）挂在 `integration` job 上，覆盖 `dev`、`main` 与 PR。`integration` 依赖 `verify` 通过；**调用「绿了」之前先看真实运行**。本地定向跑不能算作整个矩阵或 GitHub CI 已通过的证据。

`pwsh` 里做定向运行时，把 `BOTAGENT_IT_ONLY`（兼容旧 `QQCHAT_IT_ONLY`）设成已存在的场景标签（例如 `s1,s36,s52`），把 `QQCHAT_BOT_DLL` 指向本检出重建后的 Release Headless DLL 绝对路径。除了退出码 0，还要检查**实际执行的断言数不为零**，并报告到底跑了哪些场景。日常合成验证不要用 `--serve-external`。

Windows 上全量本地 harness 当前报告 **1181/0**（53 个启用场景）。同一份测试快照在 Linux 容器里会多 1 条断言 —— S21 的 Unix `600` 权限检查在 Windows 上跳过；本次基线**没有**在 Linux 上重跑，按差值推算应为 1182/0。这些都是本地合成结果，**不是**远程 GitHub CI 运行，也不是裸机 Linux 验证。全量红了必须先和上一份基线对比，才能归因到某次改动。完整基线见 [docs/baseline/report-2026-10-31.md](docs/baseline/report-2026-10-31.md)。

`verify` job 还会恢复并运行 `ReviewRemediationProbe`、`ReverseTransportProbe`、`DeadlineGateProbe`、`FeishuRemediationProbe`、`SettingsScopeProbe`、`SsrfProbe`、`McpProbe`、`ZbaProbe`。`McpProbe` 当前报告 **137/0**，分三组（A 78 / B 33 / C 26），含初始化期间 dispose、`tools/list` 期间 dispose、调用期间 dispose、重复 dispose、缓存连接的 `Ready`、调用方取消后的状态恢复等确定性回归。`ZbaProbe` 当前报告 **303/0**，覆盖路由/OpenAPI 一致性、Bearer 读写授权、认证失败限流、封闭式 JSON 解析（成员名精确大小写、拒绝大小写别名与嵌套重复）、密钥形状与非回显规则（含 null 元素）、配置与能力结果映射、任务幂等/取消语义、审批、事件恢复，以及一次真实回环 `HttpListener` 冒烟。配置整体回滚的子进程**不再写死资产数量**，而是从 `BotAgent.Headless.deps.json` 推导候选闭包（当前 43 个非 PDB 资产），并在移除前逐项校验候选专属资产的哈希。这些聚焦的合成检查**不**构成生产或真实开放平台互通的证据；S42 只用回环假网关。Core 与独立的集成 harness 各自显式打开传递性 NuGet 审计（`all`，阈值 `high`）并把高危/严重发现（`NU1903`/`NU1904`）当成还原错误；这是**按工程**的门，不是全仓包图都已审计的证明；`dotnet list package --vulnerable` 的输出仅供参考，它的退出码本身不是审计门。`src/web/tests/e2e` 的面板浏览器矩阵通过 `npm run test:e2e` 本地跑；本地 Playwright 运行**不**是 GitHub 浏览器 job 已通过的证据。

## ZBA 后端与只读 CLI

`src/BotAgent.Panel` 是 ZBA v1 的 HTTP 适配层，只引用 `BotAgent.Core`；Headless 在既有面板端口上进程内托管它。管理访问用 Bearer 令牌：`BOTAGENT_ZBA_TOKEN`（admin）与 `BOTAGENT_ZBA_READ_TOKEN`（read），兼容 `QQCHAT_ZBA_*`。没有令牌时所有管理端点 fail-closed（`503`）；`/zba/v1/health` 保持可读。真正的 OpenAPI 由 `GET /zba/v1/openapi.json` 提供，与分发器共用同一张路由表生成。

只读操作不用浏览器也能验证：

```sh
BOTAGENT_ZBA_URL=http://127.0.0.1:8080/zba/v1 BOTAGENT_ZBA_READ_TOKEN=... \
  dotnet src/BotAgent.Headless/bin/Release/net10.0/BotAgent.Headless.dll --status
# 还有：--capabilities、--tasks、--events --after 0、--zba-meta
```

S54 当前报告 **86/0**，含宿主任务取消/超时向合成桥的传播（取消帧、当前释放、下一个任务可分发、超时报告 Unknown）、需要重启的保存回执与真实 `configuration.saved` 状态、要求 resync 的前向/重启事件游标、以及被拒绝为 400 且不提交的 null 密钥元素。S55 当前报告 **72/0**，覆盖真实宿主停止/重启后持久化的事件序号高水位：等于高水位的旧游标可无损续读，下一个事件按 `last+1` 投递，前向游标要求 resync，事件环为空时更旧的游标要求 resync（不静默跳过），序号文件损坏时以新世代启动并告警，并通过真实缓冲压过 512 事件环溢出。CLI **从不**启动宿主、**从不**读数据库，只打印服务器给出的安全 JSON 投影。Web 静态资源发布到 publish 目录下的 `wwwroot/`（不再嵌进 Headless 程序集）；缺资源只让旧页面 404，health/API/ZBA 仍然可用。**不要**把生产数据、日志或真实设置塞进 ZBA 契约测试；用合成后端 `tests/BotAgent.ZbaProbe` 或 S54/S55 的集成夹具。

## 本地源码镜像构建边界

`src/BotAgent.Headless/Dockerfile` 期望**仓库根形状**的构建上下文。它按真实的 Core/Platforms/Storage 工程引用还原并发布 Headless；cfg-0.3.3 移除了内嵌的 pi/SFTP 脚本与面板下载路由。Storage 以显式 `StorageModule` 别名引用：宿主的旧数据库、store 与文件类型是 Storage 实现之上的兼容入口。仅宿主自有的 settings/audit/provider/trace 适配层保留；宿主把原有的强类型 `AppSettings` 校验传给 Storage 的旧数据导入器。Model 尚未接入。别名**不**会把任何一棵源码树排除在架构扫描之外。只允许用**已筛查**的源码上下文，排除生产数据、日志、密钥与无关产物。本地验证时，请组装一份全新的白名单临时上下文，而不是把未经审查的私有工作区丢给 Docker 引擎。

存储路径在数据库/导入/store 使用之前，显式绑定一次到宿主解析出的运行根；同根初始化幂等，换根会被拒绝，未绑定的路径读取 fail-closed。该模块**不**自行重新发现环境/工作区根。宿主启动时把 Storage 的时间与日志绑定到既有宿主源。`ReviewRemediationProbe` 检查变更环境下的导入隔离、原设置类型校验与重试/归档行为、真实的 Storage 方法归属，以及既有的仓库行为探针。架构检查保留两棵源码树，并拒绝把 SQL/文件 IO 复制回任何已迁移的宿主兼容入口。公开 store 实现允许密封的宿主兼容子类；这次源码迁移**不**构成旧程序集混装的兼容性，也不构成完整 schema 崩溃恢复。

`ReviewRemediationProbe` 覆盖合成数据库路径绑定、跨门面读写与去重回滚、嵌套写拒绝、更新版本 schema 被拒绝且不产生变更，以及隔离子进程里的备份恢复。Storage 拿到宿主解析的数据库路径与稳定宿主时钟；默认备份路径跟随那个已绑定的数据库，而不是重新读环境根。写回调里的嵌套写与备份会被显式拒绝；需要事务内工作就用传进来的那条连接。这些检查**不**构成统一/版本化配置保存、历史 schema 崩溃恢复、旧二进制兼容、生产恢复或整个 Wave 1 验收。SQLite 既有的 30 秒等待保留；**不**承诺有界队列或取消契约。

源码镜像路线需要本地 Linux Docker 引擎与编译资源。**不要**把启动服务或改动 Docker 配置当成隐式验证步骤。低内存部署机继续走既有的 `Dockerfile.app` 纯产物路线，**不要**在那些机器上编译源码。本地发布成功不是 Linux 镜像构建结果，镜像构建成功也不等于许可运行、部署或发布，更不是资源/回滚验收的证明。

## 发布与发布覆盖

私有工作区的发布器用 `git archive HEAD` 在独立临时目录里生成已提交源码的快照，在那里做隐私审计，然后用普通快进推送到公开的 `dev`。它沿用源码提交的清晰标题；**不得**直接发布或强推 `main`。`dev` 到 `main` 的提升必须走上面那条独立评审 PR 与其必需检查。独立黑名单/类别审计与内容隐私检查、远端复检都必须保持强制；**永远不要**直接把私有工作区发出去，也不要为了过发布而关掉任何扫描。

归档成功、隐私审计通过或快照推送成功，都**不**是 PR 或 CI 门已通过的证据。`git archive HEAD` 不包含未提交的工作。发布验证必须写清实际发布的修订号与它真实的 CI/PR 检查，并如实披露合成测试没覆盖的环境（真实开放平台凭据、裸机/云上 Linux、Caddy/公网反向代理、真实浏览器 CSRF、真实 QQ/NapCat/Feishu/TTS、生产环境），而不是宣称已全覆盖。

## 大文件与重构 PR

- 行数阈值与判定流程见 [docs/architecture/large-file-policy.md](docs/architecture/large-file-policy.md)：>1000 行的文件要先做专项审查再动，不允许为了减行数机械拆文件。
- 重构 PR 必须附「重构前后同一组 harness 场景的输出对比」（场景 ID、通过数、差异项说明），且**禁止在重构 PR 内修改任何断言**。PR 模板里有这一栏。