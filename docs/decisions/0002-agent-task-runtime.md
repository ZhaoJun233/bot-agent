# ADR-0002：任务与执行状态模型（#114）

- 状态：已接受（2026-11-03）。实施分支 `feat/agent-runtime`，父条目 #100。
- 影响面：新增 `src/BotAgent.Core/Domain/Agent/AgentTaskState.cs`、`AgentTaskTransitions.cs`、`AgentTaskText.cs`、`src/BotAgent.Core/Domain/Ports/IAgentTaskExecutor.cs`、`src/BotAgent.Engine/Runtime/AgentTaskRuntime.cs`；改写 `src/BotAgent.Headless/Adapters/Panel/Zba/ZbaTaskRegistry.cs`；新增 `src/BotAgent.Headless/Adapters/Agent/AgentTaskExecutor.cs`。
- 相关条目：#114（本条目）、#115（工具注册表）、#116（持久化与重启恢复）、#117（审批与隔离）、#119（部署文档）、#106/#110（已完成的前置）。

## 1. 背景

### 1.1 现状：没有「任务」这个一等公民

全仓 `ITaskRuntime` / `ITaskStore` / `TaskState` 零命中。执行状态分散在六条路径上，各自隐式：

| 路径 | 入口 | 状态在哪 | 取消/超时 | 重启后 |
| --- | --- | --- | --- | --- |
| A 普通聊天 | `BotAgentHost.cs:210` → `SourceScheduling.cs:34` | 通道队列 | lease 生命周期 | 丢失 |
| B `//` 命令 | `ReplyPipeline.cs:283-287`（fire-and-forget） | `agent-sessions.json` 的 `SessionRun` | 通道 lease 的 CTS | 残留 `Ok=null` 的 run |
| C 面板 / ZBA | `ZbaTaskRegistry` | 内存台账 | `CancellationTokenSource` | 不可查询（unknown） |
| D 插件运维 | `ManagedCapabilityService.cs:195` | `configuration_operations` 表 | 无 | 可查 |
| E MCP 子步骤 | `McpToolUseCase.cs` | 轮内局部变量 | 轮内 CTS | 无 |
| F 定时/主动 | `BotScheduler.cs:137` → `ReplyPipeline.cs:641` | 调度在内 | 进程退出即止 | 丢失 |

`ZbaTaskRegistry` 最接近任务模型（Queued→Running→终态、终态防覆盖、取消请求），但它把执行、状态、幂等、回执、事件、结果投影全塞在一个类里，并且 id 与实例 `instanceId` 绑定。

### 1.2 最硬的通道耦合不是 QQ 类型

`PlatformSourceLease.Lifetime` + `SourceScheduling.cs:228` 的 `IsCurrent` 门：**执行资格由「通道当前代」裁决**，通道换代即丢弃任务；`//stop` 的 `CancellationToken` 源头也在通道 lease。#114 要切断的正是这一刀 —— 状态属于任务，不属于通道代次。

### 1.3 #114 要求的验收

1. 状态模型有明确的状态转移表，非法转移被拒绝；
2. 通道层不再持有执行逻辑，只做请求转换；
3. 取消、超时、失败三条路径有合成用例；
4. 聊天行为不变；
5. 有设计文档（本文）。

## 2. 决策

### 2.1 状态模型放 Domain，运行时放 Engine，不新建工程

- `BotAgent.Core/Domain/Agent/` 放 `AgentTaskState`（9 个状态）、`AgentTaskTransitions`（显式允许表 + `CanTransition` / `EnsureTransition`）、`AgentTaskText`（无副作用的正文清洗与有界截断）。
- `BotAgent.Core/Domain/Ports/IAgentTaskExecutor` 是执行端口：`Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest, CancellationToken)`。Core 只描述「谁能执行」，不知道 agent 怎么跑。
- `BotAgent.Engine/Runtime/AgentTaskRuntime` 是内存实现：准入、状态推进、取消、超时、终态保护、快照、变更事件。它只依赖 `BotAgent.Core`（Engine 工程的唯一引用）；时间从构造函数注入的 `Func<DateTimeOffset>` 来，**不读** `EngineClock` 或系统时钟，便于合成测试固定时间。
- **不新建工程**：`tests/BotAgent.ArchitectureProbe/Baseline.cs` 的 `AllowedProjectReferences` 是封闭白名单，加工程要改基线；本轮不需要新工程。

Domain 的硬规则（ArchitectureProbe 钉着）：单文件 ≤300 行、类型 ≤10 字段 / ≤25 方法、不读时钟。所以状态枚举与转移表拆两个文件，时间一律走显式 `now` 参数：运行时的 `Func<DateTimeOffset>` 由宿主注入，探针传固定时间。

### 2.2 状态集合：9 个，比 ZBA 契约多一个 `TimedOut`

| 状态 | 含义 | 谁会写 |
| --- | --- | --- |
| `Queued` | 已受理、未开始 | Runtime |
| `Running` | 执行器已启动 | Runtime |
| `WaitingApproval` | 等人工审批（#137 起有真实写入方） | #137 |
| `CancelRequested` | 取消已受理、执行器未停 | Runtime（Cancel） |
| `Succeeded` / `Failed` | 终态 | Runtime |
| `Cancelled` | 终态：用户取消 | Runtime |
| `TimedOut` | 终态：超时（**与取消区分**） | Runtime |
| `Unknown` | 终态：本实例无法确认结果（#116 恢复用；本轮不产生） | #116 |

ZBA 面板契约（`ZbaContracts.cs:185-195`）**保持 8 个值不变**：`TimedOut` 在 ZBA 投影为 `Failed` + `errorCode="timeout"`。理由：往对外 OpenAPI 枚举里加值会让生成的客户端与面板状态标签全部要跟进，而审批（#117）才是真正需要扩契约的地方；把「超时」先作为失败原因暴露是可接受且诚实的。这是本轮**唯一可观察的行为变化**：此前超时被上报为 `Cancelled`（`cancelled`），现在为 `Failed`（`timeout`）。

### 2.3 转移表

```
Queued          -> Running | CancelRequested | Cancelled | TimedOut | WaitingApproval
Running         -> Succeeded | Failed | Cancelled | TimedOut | CancelRequested | WaitingApproval | Unknown
WaitingApproval -> Running | Cancelled | Failed | TimedOut
CancelRequested -> Cancelled | Succeeded | Failed | TimedOut | Unknown
Succeeded | Failed | Cancelled | TimedOut | Unknown -> （无，终态）
```

要点：
- **终态不可覆盖**（原来靠 `Finish` 里一句 `if (Terminal(record.State)) return;`，现在是表的一部分）。
- `CancelRequested -> Succeeded/Failed` 允许：取消是**请求**不是事实，执行器可能在取消生效前已经干完；这时如实上报完成，不冒报「取消成功」。
- `Queued -> TimedOut` 允许：超时从受理开始算，包含排队时间。
- `WaitingApproval` 的生产者是任务级前置审批（#137）：受理时有效工具作用域包含 `bash` / `write` / `docker` 且审批总开关打开就停在这里；省略或空作用域仍继承配置，
  不用 `Resume` 手动推进（工具级的执行器挂起与恢复仍不在范围里）。
- `Running | CancelRequested -> Unknown` 是 **#116 的恢复策略**：进程重启时已派发但结果不明的任务，
  本地不能证明远端副作用被取消，也绝不重做，只能记「无法判定」（决策与对账细节见 ADR-0003）。

### 2.4 双超时语义

- **Runtime 的 `Timeout` 是硬截止**：到点先记下超时意图（`TimedOut`），再取消执行器令牌，最终态 `TimedOut` / `errorCode="timeout"`。
- `ServerAgentRunner.TaskMaxSeconds = 240` 的**软预算不改**：它是「到点让模型先把已有结论说出来」的提示，不是取消。两者不冲突：软预算通常先到，任务正常完成；硬截止兜底卡死的任务。
- 显式取消（`Cancel`）与超时在 Runtime 里是两条路径、两个状态；执行器抛 `OperationCanceledException` 时由 Runtime 根据「是不是自己触发的超时」决定归宿，**不让异常自己猜**。

### 2.5 正文与隐私

- Runtime **不保存提示词**：`AgentTaskRequest.Prompt` 只在提交时活着，交给执行器后不写进快照。
- 结果正文只在 `CaptureResult=true` 时保存，且先清洗（去控制字符、CRLF 归一、Trim）再截断到 `MaxResultChars`（默认 4000，截断点不落在代理对中间）。谁设 `CaptureResult` 由通道决定：ZBA 只对 `panel:` 源设 true（与今天一致），非工作区来源永不保存正文。
- #116 落库时只存哈希、状态、耗时、计数，不存正文。

### 2.6 事件与查询

Runtime 提供 `Changed`（状态变化回调）与快照查询：`Get(id)`、`List(offset, limit, out total)`、`Counts()`。通道仍负责把事件投影成自己的形状（ZBA 的 `task.updated` 事件与 `task` 错误桶），Runtime 不认识 `ZbaEventBuffer`、不认识 HTTP。

### 2.7 容量与淘汰的分工

- **并发准入在 Runtime**（`MaxActive`，默认 4）：`Submit` 在「查容量 + 入账」这一临界区内完成，避免两个调用方同时看到 3 个活跃任务。
- **台账保留、幂等键、回执在通道**：ZBA 的 200 条上限、淘汰最旧终态、`idempotency_unverifiable`、`record_evicted` 都是通道语义。通道淘汰一条记录时调 `runtime.Forget(id)` 释放内存，Runtime 不自己决定保留策略。

### 2.8 第一个迁移的通道是 ZBA /tasks，不是 `//`

调查建议先接 `//`（`AgentCommandService.Commands.cs:431` 的 `ServerAgentAdmission`），本轮**反过来先接 ZBA**，理由：

1. ZBA 是唯一**对外暴露任务语义**的通道（list/get/result/cancel/find + OpenAPI），「状态独立于通道」在这里才有可观察的验收面；
2. ZBA 已有 303 条探针断言 + 集成场景 s54/s55，改写有护栏；`//` 侧没有任何任务查询面，搬过去只改善内部结构、验收上看不见；
3. ZBA 的四个通道级语义（幂等键、回执、事件、结果投影）正好演示「通道只做请求转换与投影」的边界；
4. `//` 的命令执行是 fire-and-forget + 文本回执，一次搬迁会同时动 `ReplyPipeline`（243 行/10 字段/最长方法 30 的棘轮）与 `ServerAgentRunner`，与「聊天行为不变」这条验收正面冲突；
5. 聊天轮明确**不是**目标（#114 原文：只有显式任务走 Runtime）。把聊天塞进 Runtime 会撞 s36/TurnLoop 与 InFlight/Queued 健康指标。

`//` 的收敛与面板直跑留给后续条目（见 §6）。

## 3. 不可破坏的语义（与守护它的测试）

| 语义 | 守护者 |
| --- | --- |
| id 稳定（`zt-<ms>-<8>`）、同幂等键同任务 | ZbaProbe 任务组、集成 s54/s55 |
| 终态不可再转移（迟到的完成不覆盖取消） | `AgentTaskTransitions` 表 + EngineProbe 全矩阵 |
| 取消是请求：`outcome=requested`，终态`Cancelled` | ZbaProbe `task cancel`、s54 |
| 取消未知任务 404、重复取消不再变更 | ZbaProbe |
| 超时不冒充取消：`Failed` + `timeout` | 新增 EngineProbe 超时用例 |
| 结果正文只对 `panel:` 源、≤4000 字、含截断标记 | ZbaProbe 结果组、s54 `结果摘要只含单次任务最终答复` |
| 幂等键冲突 409 / 回执不可核对 409 | ZbaProbe 幂等组 |
| 并发/台账上限 429 `task_capacity_exceeded` | ZbaProbe 容量组 |
| 重启后旧任务不可查询、不重做 | s60 与回执逻辑（#116 再加强） |
| 聊天链路完全不变 | 全量集成 1255/0 基线 |

## 4. 分步实施与验证

0. **本 ADR**（审核通过后才动代码）。
1. **Core 状态机**：`AgentTaskState` + `AgentTaskTransitions` + `AgentTaskText` + `IAgentTaskExecutor`。验证：`dotnet build src/BotAgent.Core`、ArchitectureProbe（Domain 行数/字段数规则）。
2. **Engine Runtime**：`AgentTaskRuntime`（内存台账、执行、取消、超时、容量、事件）。验证：新增 `tests/BotAgent.EngineProbe/AgentTaskRuntimeTests.cs`，覆盖转移表全矩阵 + 取消 + 超时 + 失败 + 并发上限 + 终态保护。
3. **ZBA 适配器改写**：`ZbaTaskRegistry` 只保留幂等/回执/事件/结果投影/错误映射；执行与状态交 Runtime。验证：`ZbaProbe`、`BOTAGENT_IT_ONLY=s54`、`BOTAGENT_IT_ONLY=s55`、ArchitectureProbe。
4. **文档与 CHANGELOG**：`docs/architecture/agent-task-runtime.md`（状态表、责任边界、如何加通道）、CHANGELOG 条目。
5. **全量集成 + 发布**：`temporal`；跑全量 harness（预计 1255/0 基线不变），发布到 `Zhaodev`，CI 9/9。

## 5. 风险与实际取舍

| 风险 | 取舍 |
| --- | --- |
| ZBA 超时从 `Cancelled` 变 `Failed(timeout)` 是行为变化 | 保留映射表一行即可回退；ADR 与 CHANGELOG 明确写出 |
| Runtime 不落库，重启仍丢状态 | 与今天一致（ZBA 旧台账也只存内存，重启后 unknown）；#116 才做持久化，避免本轮同时动 schema（user_version 11 → 12 的回滚面） |
| 通道仍握着「会话是否存在 / server 是否启用 / target 是否合法」的校验 | 这些是**通道输入校验**，不是执行逻辑；执行器只接收已校验的请求。若将来需要，可由 #115 统一权限面 |
| `//` 路径的隐式状态没有收敛 | 明确记账：见 §6 与 follow-up 条目；#114 的验收面集中在 ZBA |
| 结果正文进内存 | 只对 `panel:` 源、≤4000 字、随台账淘汰；不落库、不进日志 |
| Runtime 需要读时间 | 通过构造函数注入的 `Func<DateTimeOffset>`，不读 `EngineClock` 或系统时间（R1 钉着） |

## 6. 不做的事

- 不新建工程、不改 `BotAgent.slnx`、不改 `Baseline.cs` 的工程白名单。
- 不写 `tasks` 表、不改 `user_version`（#116）。
- 不迁移聊天轮、`//` 命令、面板直接试跑、插件运维、MCP 子步骤、定时任务。
- 不做工具注册表（#115）、不做审批与隔离（#117）、不做部署回滚文档（#119）。
- 不把 Runtime 暴露到 ZBA OpenAPI：管理面继续只用 `/zba/v1/tasks` 四个端点。
- 不提供远程重启按钮。
