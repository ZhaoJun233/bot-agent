# 通用 Agent 平台后续路线图（2026-11）

这一页回答三个问题：本轮治理**实际做完了什么**、**还剩什么没被证明**、以及下一步按什么顺序做。
口径只有一条：**写在这里的每条结论都要能对应到代码位置或实测数字**；没有验证过的能力只能进「探索」，
不许写成承诺。

数据快照：2026-11-03，公开快照 `Zhaodev` 顶端 `7930e02b9e06`（CI 9 项全绿）。
治理总纲见 [#100](https://github.com/ZhaoJun233/bot-agent/issues/100)，阶段四的实现细节见
[任务与执行状态](architecture/agent-task-runtime.md)、[工具注册表](architecture/tool-registry.md)、
[审批与执行隔离](architecture/approval-and-isolation.md)。

## 1. 复盘：四个阶段全部关闭

| 阶段 | 子任务 | 结果 |
| --- | --- | --- |
| 一 · 治理、升级与关键修复 | #101 #102 #103 #104 #105 #123 #124 #125 #127 | ✅ 全部关闭 |
| 二 · 解耦与回归/运维基线 | #106 #108 #113 #121 #122 #126 | ✅ 全部关闭 |
| 三 · 存储与装配治理 | #107 #109 #110 #111 #112 | ✅ 全部关闭 |
| 四 · Agent Runtime | #114 #115 #116 #117 #119 | ✅ 全部关闭 |
| 治理外（另行评估后落地） | #97 #98 #128–#138 | ✅ 全部关闭 |

每个阶段真正改变了什么：

- **阶段一**：运行时从 .NET 8 升到 .NET 10（30 个工程的 `TargetFramework` 单行切换，无包版本变动），
  并有可复现的跨版本回滚演练（[验收报告](operations/upgrade-acceptance-dotnet10.md)：五步 10 秒走通）；
  模型配置与白名单两个「改了不生效」按真实链路修掉（[模型配置生效路径](architecture/model-configuration-apply-path.md)）；
  仓库第一次有了治理骨架（CODEOWNERS、PR/issue 模板、`Directory.Build.props` 构建基线、分支保护）与
  接口面/配置面地图（[接口面](api-surface.md)：37 条 ZBA + 50 条旧 `/api/*` 冻结声明 + 三层配置模型）。
- **阶段二**：`ReplyPipeline` 与 `OneBotGateway` 按职责拆开，四条核心链路不变量第一次有自动化断言；
  CI 从「能跑」变成「门禁」（9 项必需检查、依赖安全、不稳定性治理）。
- **阶段三**：配置写入链路（`SettingsStore` / `ConfigurationStore`）与装配（`CompositionRoot`）
  拆到可独立阅读的粒度，并补了配置与权限的回归体系。
- **阶段四**：Agent Runtime 从「通道内的一坨循环」变成独立构件：任务生命周期与台账（#114/#116）、
  统一工具注册表与可见性判定（#115）、任务级前置审批与票据验签（#117）、部署验证与回滚文档（#119）。
  这一阶段的取舍（粒度、不重做、无远程重启）都写进 ADR 与边界章节，不是随口承诺。

## 2. 当前实测基线

| 项 | 数字 |
| --- | --- |
| 工程构建 | 30 个 csproj，Release 构建 **0 错误** |
| 合成探针 | 22 个全绿：ArchitectureProbe **219/0**、ZbaProbe **317/0**、ProtocolProbe **319** 项、SafetyProbe **637/0**、EngineProbe **50/0**、ReviewRemediationProbe **1213/0**、ProductionSpecProbe **115/0**、McpProbe 137/0、SsrfProbe 77/0、PipelineEval 68/0、ChaosFaultProbe 49/0、DeadlineGateProbe 53/0、ParticipationProbe 48/0、PluginSettingsProbe 48、PluginMarketProbe 82、PluginPackageProbe 34、SnapshotProbe 19、ConcurrencyStressProbe 12/0、ReverseTransportProbe 0 失败、SettingsScopeProbe 0 失败、FeishuRemediationProbe 全 PASS、ServerFilesProbe（Python）15/0 |
| 集成 harness | 53 个场景、**1276 / 0** |
| 前端 | typecheck 绿、单测 40 文件 **490** 用例、构建绿、chromium e2e **99 passed** |
| CI | 9 项必需检查全绿（构建+探针 / Web 构建与浏览器测试 / 集成三矩阵 / e2e 四矩阵） |
| 文档 | 63 篇、421 条仓库内链接、0 断链（`tools`/探针双重校验） |

这些数字本身就是路线图的约束：任何「新增能力」都要在不破坏 1276 条集成断言与 22 个探针的前提下落地。

## 3. 已落地能力：成熟度分级

| 能力 | 代码位置 | 守护测试 | 成熟度 |
| --- | --- | --- | --- |
| Runtime 任务生命周期与台账 | `src/BotAgent.Engine/Runtime/AgentTaskRuntime.cs` | EngineProbe 50/0（含 8 条审批用例）、S65 跨重启 | **有验收标准，已落地** |
| 取消 / 超时 / 失败恢复 | 同上 + `AgentTaskLedger`（schema v13） | S65、ReviewRemediationProbe | 有验收标准，已落地；超时折成 `Failed(timeout)` 是唯一可观察行为变化 |
| 统一工具注册表与可见性 | `src/BotAgent.Engine/Tools/{ToolDirectory,ToolInventory,ToolVisibility,ServerToolAccess}.cs` | SafetyProbe、ToolRegistry 相关断言、ArchitectureProbe | 有验收标准，已落地 |
| 任务级前置审批 + 票据验签 | `src/BotAgent.Core/Domain/Permissions/{ApprovalFlow,ApprovalStore,ToolGate,ApprovalTicketSigner}.cs`、`src/BotAgent.Headless/Adapters/Agent/TaskApprovalGate.cs` | SafetyProbe 637/0、EngineProbe 审批 8 例、S54 审批闭环 | 有验收标准，已落地（**任务级，不是工具级**） |
| MCP 工具面 | `src/BotAgent.Engine/Mcp/McpToolUseCase.cs`、`ServerToolAccess` 的 MCP 段 | McpProbe 137/0、S54 工具作用域 | 有验收标准；聊天通道**刻意不列 MCP** |
| 插件：包校验 / 市场 / 设置 | `src/BotAgent.Headless/Adapters/Plugins/*` | PluginPackageProbe 34、PluginMarketProbe 82、PluginSettingsProbe 48 | 有验收标准；**插件无沙箱**（见 SECURITY.md 威胁模型） |
| 模型配置热生效 | `CompositionRoot.TryBuildCurrentProviderRoute`（请求级凭据来源）、`SettingsHotReload` | S21 42 项、ConfigurationContractTests | 有验收标准，已落地；供应商注册表热重建仍是已知边界 |
| 输入缓存观测与控制参数 | `src/BotAgent.Model/ModelTransport.cs`、`Protocols/ModelProtocolAdapter.cs`、`TraceArchiveStore`、ZBA `/traces` | ProtocolProbe 319、ZbaProbe 317/0、S54 轨迹只读 | **观测有验收标准；控制参数默认关、真实命中未证明** |
| 安全边界 | `SECURITY.md`、SSRF/限流/审批/隔离 | SsrfProbe 77/0、SafetyProbe 637/0 | 有验收标准，但有明确红线（见第 5 节） |

## 4. 演进顺序与依赖

顺序不是按「听起来高级」排的，而是按**依赖**与**能不能被验证**排的：

### 第一步（可立即做）：Runtime 被真实流量验证

- 现状：Runtime、审批、轨迹页都已落地，但真实使用方只有合成数据；`Unknown` 状态在生产里是否会出现、
  出现频率如何、原因码分布是否可解释，都还没有数据。
- 做什么：把面板轨迹页与 `/metrics` 的三态计数当观测面板用，跑 2–4 周真实任务；只记录形状与计数。
- 完成判据：任务结局分布可解释（`Unknown` 不随正常运行增长）、没有「审批后卡在 WaitingApproval」的残留、
  超时 `Failed(timeout)` 比例有基线。
- 依赖：无（需要的是运行时间，不是新代码）。

### 第二步：工具面收口（有真实需求才做，每项都先补断言）

1. **工具级审批**：现在审批是任务级（声明高风险工具就整条先批）。做工具级要把执行器循环变成可持久化状态，
   代价高；触发条件是出现「一条任务里既有低风险又有高风险工具、整条先批影响体验」的真实用例。
2. **MCP 执行路径统一**：聊天通道目前**不列** MCP（有意的：聊天管道没有 MCP 执行能力）。要让它进聊天，
   必须先让执行路径与 `//` 共用同一判定与同一预算，否则就是「列了却调不了」。
3. **远程重启按钮**：`RestartRequired` 目前是死胡同（没有远程重启）。做它的前提是先把重启做成可恢复动作
   （台账 + 审批 + 幂等），不是加一个按钮。

### 第三步：插件生态（可独立推进）

- 现在 SHA256 只证字节完整性，permissions 只是信息披露。要变成「信任链 + 权限强约束」，
  需要签名与版本兼容策略；插件 TFM 单向放宽（net10 插件不能在 net8 宿主加载）已写进不可逆边界。
- 完成判据：篡改包被拒、越权插件被闸门拦住、跨 TFM 组合有明确失败原因。

### 第四步（仍不排期）：跨语言 Worker

- #118（Python Worker / SDK）保持 Backlog。触发条件：Runtime 有了真实使用方，且出现「必须用 Python 生态」
  的具体能力（如本地 TTS、图像/视频处理、数据管道），并且宿主内实现成本明显更高。
- 在此之前做它等于凭空造需求。

### 不承诺：多 Agent 协作

- 当前是单 Agent + 工具面 + 任务 Runtime。多 Agent 需要先有稳定的「任务边界 + 预算 + 审批」，
  并且要有真实并发协作需求；现在两者都不具备，所以**只作为探索方向，不排期、不写验收标准**。

## 5. 与总纲 #100 的完成标准对齐

总纲的验收条目里，**只剩一条未满足**：

> - [ ] 具备完整部署、验证与回滚文档，并形成 Agent Runtime 的明确实施路线。

- 部署/验证/回滚文档已就位：[Agent Runtime 部署、验证与回滚](operations/agent-runtime-deployment.md)、
  [回滚手册](operations/rollback-playbook.md)、[.NET 10 升级验收](operations/upgrade-acceptance-dotnet10.md)；
- 「明确实施路线」由本页承担（第 4 节）；
- 因此本页发布后，这一条可以勾选。其余条目（升级、治理、解耦、回归、Runtime）均已有实测证据。

### 仍未满足的「证明类」条目（诚实清单）

这些不是没做，而是**还没被证明**，不计入总纲勾选：

1. **真实供应商缓存命中证据**：无可用只读凭据，未做冷/热对比；链路静态判定见
   [prompt-cache](architecture/prompt-cache.md) §6.2，配方已写好。
2. **Linux 基线**：集成 harness 只在 Windows 跑过；Linux 会多一条 Unix 权限断言，按差值推算 1182/0，
   但没有在 Linux 重跑。
3. **跨版本回滚用的是合成数据**：演练证明「旧二进制能读新库」，没有用生产库副本演练。
4. **插件 TFM 单向**：net10 插件在 net8 宿主加载失败是已知不可逆边界，只在文档里，没有跨版本组合实测。
5. **不稳定性治理靠经验**：S12 端口、B24 子进程清理这类 flaky 已用「有界等待 + FreePort」根治，
   但 CI 仍需持续观察；目前没有「连续 N 次全绿」的自动统计。
6. **容器路径未在真实部署机重建**：#105 的容器验证走的是文档路径（解压产物），没有在 1GB 内存的部署机
   上重建镜像。
7. **`#105` 的七项未覆盖**：见验收报告 §7（真实 CI 环境、真模型、真实群聊数据等），本轮未变。

## 6. 探索 / 不承诺 / 不做

| 方向 | 状态 | 说明 |
| --- | --- | --- |
| 多 Agent 协作 | 探索 | 依赖任务边界与预算先稳定，见第 4 节末 |
| 跨语言 Worker（#118） | 条件触发 | 有真实使用方与必须用 Python 的能力再做 |
| 工具级审批 / 跨重启恢复待批 | 探索 | 任务级已够用；代价与收益见 [审批与隔离](architecture/approval-and-isolation.md) |
| 缓存真实命中率承诺 | **不做** | 中间网关可覆盖/剥离缓存键，命中口径由网关归一化；不承诺固定命中率 |
| 跨会话结果缓存 | **不做** | issue 非目标；会把「会话隔离」这个安全边界搞乱 |
| 前缀冻结（为缓存改上下文选择） | **不做** | 滑窗是语义取舍，不为缓存让步 |

## 7. 下一步的具体动作（按顺序）

1. 本页发布 → 勾选 #100 最后一条验收标准、把阶段四标为完成（看板与总纲同步）。
2. 用轨迹页 + `/metrics` 观察 2–4 周真实任务，产出「结局分布 + 原因码分布 + 未知轮数」的只读快照
   （不记录任何会话内容）。
3. 拿到只读模型凭据后按 [prompt-cache](architecture/prompt-cache.md) §6.2 补真实命中证据；
   拿不到就维持「默认关 + 未证明」的现状。
4. 有真实需求时再开工具面 / 插件生态的新 issue，每个都先写验收标准再写代码。
