# 任务与执行状态：AgentTaskRuntime

面向「一个任务从受理到终态」的统一状态模型与运行时。决策与取舍见
[ADR-0002](../decisions/0002-agent-task-runtime.md)；本文只写**当前的实现事实**：谁负责什么、
一次提交会经历哪些状态、失败与取消怎么区分、哪些通道已经接上、看护它的测试在哪。

## 1. 为什么单独有一层

在 #114 之前，本仓的「任务」不是一个一等公民：ZBA `/tasks` 有一套自己的内存台账，
`//` 命令是 fire-and-forget，面板直跑复用同一个 agent 入口。三处的「取消、超时、失败」语义
各写各的，想知道「一个任务现在到底怎么样」只能进具体通道里看。

这一层只做一件事：**把状态与生命周期收进一个地方**。通道负责协议（幂等键、回执、事件、
结果投影），运行时负责执行（排队、上限、取消、超时、终态不可覆盖）。

## 2. 状态模型

九个状态，`AgentTaskState`（`src/BotAgent.Core/Domain/Agent/AgentTaskState.cs`）：

| 值 | 状态 | 含义 |
| --- | --- | --- |
| 0 | Queued | 已受理、等待执行 |
| 1 | Running | 执行器已开始 |
| 2 | WaitingApproval | 等待审批（#117 的预留位，当前没有写入方） |
| 3 | Succeeded | 成功终态 |
| 4 | Failed | 失败终态（带 errorCode） |
| 5 | CancelRequested | 已收到取消请求，执行器尚未停下 |
| 6 | Cancelled | 已取消终态 |
| 7 | Unknown | 无法判定（例如跨实例只剩回执） |
| 8 | TimedOut | 超时终态（只活在运行时） |

前八个的数值与 ZBA 契约 `ZbaTaskState` **一一对应**（`src/BotAgent.Panel/ZbaContracts.cs`），
所以通道投影不需要查表。`TimedOut` 是运行时独有的：ZBA 对外枚举没有这一项，投影时
**折成 `Failed` + `errorCode=timeout`**，这样不改 OpenAPI、老客户端也不会收到不认识的值。

转移表是显式的纯函数（`AgentTaskTransitions`），不在运行时里散落 if：

| 从 | 允许去往 |
| --- | --- |
| Queued | Running / CancelRequested / Cancelled / TimedOut |
| Running | Succeeded / Failed / Cancelled / TimedOut / CancelRequested / WaitingApproval |
| WaitingApproval | Running / Cancelled / Failed / TimedOut |
| CancelRequested | Cancelled / Succeeded / Failed / TimedOut |
| 五个终态 | 无出边 |

两条容易写错、这里明确固化的规则：

- **同状态不算转移**（`CanTransition(X, X)` 为假），避免「重复上报 Running」被当成进度。
- **取消是请求不是事实**：`CancelRequested → Succeeded / Failed` 合法。执行器可能已经做完、
  来不及看取消；这时如实报成功/失败，比硬写一个 Cancelled 可信。

非法转移一律 `EnsureTransition` 抛 `InvalidOperationException`；运行时内部用 `MoveTo` 推进，
终态之后任何迟到的结果或状态都被静默丢弃（`AgentTaskRuntime.Finish` 第一行判终态即返回）。

## 3. 分层与文件

| 层 | 文件 | 职责 |
| --- | --- | --- |
| Core | `Domain/Agent/AgentTaskState.cs` | 状态枚举 |
| Core | `Domain/Agent/AgentTaskTransitions.cs` | 允许表 / `IsTerminal` / `CanTransition` / `EnsureTransition` |
| Core | `Domain/Agent/AgentTaskRequest.cs` | 提交方看到的输入（id / 来源 / 会话 / 提示词 / 目标 / 超时 / 是否采集正文） |
| Core | `Domain/Agent/AgentTaskExecution.cs` | 执行器返回的结果（Ok / 正文 / 原因码 / 耗时 / 工具次数 / 是否观察到取消） |
| Core | `Domain/Agent/AgentTaskText.cs` | 正文清洗与控制字符 / 代理对安全的截断 |
| Core | `Domain/Ports/IAgentTaskExecutor.cs` | 执行端口（一个 `ExecuteAsync`） |
| Engine | `Runtime/AgentTaskRuntime.cs` | 排队、并发上限、取消、超时、终态、变更事件 |
| Engine | `Runtime/AgentTaskSnapshot.cs` | 对外快照与提交/取消结果类型 |
| Headless | `Adapters/Agent/AgentTaskExecutor.cs` | 把任务转给现有的 `AgentCommandService.RunPanelAgentDirectAsync` |
| Headless | `Adapters/Panel/Zba/ZbaTaskRegistry.cs` | ZBA 通道适配器：幂等键、回执、事件流、结果投影、台账淘汰 |

Core 里的状态模型**不读时钟**（时间由运行时通过 `Func<DateTimeOffset>` 注入），
Engine 只依赖 Core（`BotAgent.Engine.csproj` 的引用白名单是封闭的），
Headless 才知道具体的 agent 命令服务——所以换通道不用动运行时。

## 4. 一次提交的生命周期

1. 通道调 `Submit(AgentTaskRequest)`。运行时在锁内判定：已停止 → `Disposed`；id 重复 →
   `DuplicateId`；活跃数已达上限 → `CapacityExceeded`。被拒时**不建记录**，返回
   `AgentTaskSubmitResult.Rejected`。
2. 受理后状态是 `Queued`，快照立刻可查；执行在 `Task.Run` 里进行，提交线程不等。
3. 执行体进入 `Running`，为任务建两个令牌源：`cancelSource`（外部取消）与
   `timeoutSource`（硬截止），二者连成 `linked` 交给执行器。
4. 执行器任务与超时延迟赛跑：
   - **执行器先返回** → 停掉超时计时器 → `Finish`：写耗时/工具次数；只有 `CaptureResult=true`
     才清洗并截断正文（默认 4000 字）；`Ok=false` 时按「是否收到过取消」映射
     `Cancelled(errorCode=cancelled)` 或 `Failed(errorCode=执行器给的原因码 ?? agent_failed)`。
   - **超时先到** → 先置 `TimedOut`（原因码 `timeout`），再取消执行器令牌，迟到的结果丢弃。
   - **执行器抛 `OperationCanceledException`** → 看当前是超时还是取消，分别落 `TimedOut` /
     `Cancelled`。
   - **执行器抛其它异常** → `Failed`；`InvalidOperationException` 记 `agent_rejected`（表示请求
     被 agent 入口拒绝），其余记 `agent_failed`。
5. 任何状态变化都会触发 `Changed` 事件（**在锁外**触发），通道据此投影自己的事件流。

### 取消与超时是两条路径

- 取消：`Cancel(id)` 先记 `CancellationRequested=true`，能转移时置 `CancelRequested` 并取消令牌，
  返回 `Requested` / `AlreadyRequested` / `TooLate`（已终态）/ `NotFound`。
- 超时：运行时内部的硬截止，`CancellationRequested` **保持为假**，errorCode 是 `timeout`。
- 两者都让执行器收到取消令牌，但对外语义不同：一个是被要求停，一个是预算用尽。

## 5. 容量、正文与淘汰

- **并发上限在运行时**（构造参数 `maxActive`，默认 4），在提交临界区内判定，通道不用再数一遍。
- **结果正文默认不存**：`CaptureResult=false` 时只留状态与耗时；开启时清洗控制字符/统一换行后
  截断到 `maxResultChars`（默认 4000），并记 `ResultChars`（清洗后的原始长度）与
  `ResultTruncated`。
- **台账淘汰在通道**：运行时按 id 保存记录，通道判断某条已终态且超出保留量后调 `Forget(id)`；
  运行中的记录 `Forget` 返回 false，绝不会被删。

## 6. 已经接上的通道：ZBA `/tasks`

`ZbaTaskRegistry` 现在只做适配，不再自己执行、也不自己存状态：

| 通道语义 | 现在怎么做 |
| --- | --- |
| 幂等键 | `_idempotency`（key → taskId）+ `_bindings`（taskId → key/hash）；命中时用运行时里的记录比对请求哈希，记录已被淘汰则 409 `idempotency_unverifiable` |
| 回执 | **先写回执再提交运行时**：回执写失败时任务还没开始，可以如实说「未受理」；反过来会出现「客户端以为失败、其实在跑」 |
| 事件流 | `Changed` → `task.updated`；失败终态再补一条 `error` |
| 台账 | 保留 200 条 / 活跃 4 的对外上限，淘汰时 `Forget` |
| 结果投影 | 仅 `panel:` 来源的成功任务给摘要，其余 `reason=scope_not_allowed`；`TimedOut` 折成 `Failed` + `timeout` |
| 取消 | `Cancel` 映射 `Requested → (state, true, "cancel_requested")`、`TooLate → (state, false, "too_late")`、`NotFound → 404 task_not_found` |

面板直跑（`POST /api/agent`）与 `//` 命令**没有**接进运行时，这是 ADR-0002 的明确取舍：
聊天与 fire-and-forget 链路的行为本轮一点不改，先把唯一对外暴露任务语义的通道迁过来。

## 7. 守护它的测试

| 测试 | 钉住什么 |
| --- | --- |
| `EngineProbe` 的 7 条 `AgentTaskRuntime: …` | 转移表全矩阵与终态无出边、非法转移抛异常、成功/清洗/截断/未声明采集不存正文、取消（含二次取消 TooLate 与未知 NotFound）、超时与取消区分且 `CancellationRequested=false`、`agent_failed`/`agent_rejected`、并发上限与重复 id、`Forget` 只删终态 |
| `ZbaProbe` 303 条 | 通道语义没变：幂等重放、回执、事件、结果可见性、取消 |
| 集成 S54 / S55 | 面板任务提交与 ZBA 面板后端的端到端行为 |
| `ArchitectureProbe` | 分层与文件头注释约定（Core 不引 Engine、Engine 只引 Core） |

改状态模型时先改本表与 ADR-0002，再改代码；改通道投影时必须同时看 ZbaProbe 与 S54/S55。

## 8. 已知边界

- **不落库**：运行时全在内存，进程重启后任务记录消失（面板会看到「未知/已过期」）。
  持久化与重启恢复是 #116。
- **没有审批**：`WaitingApproval` 只是预留状态，没有写入方；审批与执行隔离是 #117。
- **没有远程重启**：`RestartRequired` 语义仍属配置链路，本层不提供重启按钮。
- **不暴露到 OpenAPI**：运行时是内部构件，唯一对外契约仍由 ZBA 路由与 `ZbaOpenApi` 定义。
- **超时折成 Failed 是本轮唯一可观察的行为变化**：以前 ZBA 超时会把取消也算成 `Cancelled`，
  现在带 `errorCode=timeout` 的 `Failed`；客户端如果按状态枚举分支，需要认这个原因码。
