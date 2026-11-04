# 任务与执行状态：AgentTaskRuntime（#114 / #116 / #137）

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
| 2 | WaitingApproval | 等待审批（#137 起有真实写入方：`TaskApprovalGate` 在派发前命中高风险工具就停在这里） |
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
| Queued | Running / CancelRequested / Cancelled / TimedOut / **WaitingApproval**（#137） |
| Running | Succeeded / Failed / Cancelled / TimedOut / CancelRequested / WaitingApproval / Unknown |
| WaitingApproval | Running / Cancelled / Failed / TimedOut |
| CancelRequested | Cancelled / Succeeded / Failed / TimedOut / Unknown |
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
| Engine | `Runtime/AgentTaskLedger.cs` | 台账端口（`Save` / `LoadRecent` / `Prune`）与行结构 |
| Headless | `Adapters/Persistence/AgentTaskLedgerStore.cs` | 台账的 SQLite 实现（`agent_tasks` 表，只存形状与计数） |
| Headless | `Adapters/Agent/AgentTaskExecutor.cs` | 把任务转给现有的 `AgentCommandService.RunPanelAgentDirectAsync` |
| Headless | `Adapters/Panel/Zba/ZbaTaskRegistry.cs` | ZBA 通道适配器：幂等键、回执、事件流、结果投影、台账淘汰 |

Core 里的状态模型**不读时钟**（时间由运行时通过 `Func<DateTimeOffset>` 注入），
Engine 只依赖 Core（`BotAgent.Engine.csproj` 的引用白名单是封闭的），
Headless 才知道具体的 agent 命令服务——所以换通道不用动运行时。

## 4. 一次提交的生命周期

1. 通道调 `Submit(AgentTaskRequest)`。运行时在锁内判定：已停止 → `Disposed`；id 重复 →
   `DuplicateId`；活跃数已达上限 → `CapacityExceeded`。被拒时**不建记录**，返回
   `AgentTaskSubmitResult.Rejected`。
2. 受理后状态是 `Queued`，快照立刻可查，在状态锁内按推进顺序入队；单一排空者在锁外依次写台账并发出 `Changed`；执行在 `Task.Run` 里进行，提交线程不等。
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

### 台账与重启恢复（#116）

状态每变一次，就在同一状态锁内把不可变快照排入队列。单一排空者在锁外依次写入
`agent_tasks`（`INSERT OR REPLACE`）并发出 `Changed`；顺序由入队顺序保证，不能靠时间戳或 SQL 互斥推断。
因此较早的 Queued / Running / CancelRequested 快照不会在较新的终态之后覆盖台账。写入点全在运行时里，
所以任何通道都自动获得持久化。停机把待审批任务收口为 Cancelled，并等待已入队的发布排空；执行器停机等待仍有 2 秒上限。同步状态/取消回调内调用 Dispose 只发起停机、不等待自身回调；外部调用（包括重复调用）共同等待同一停机完成屏障。存储或通知失败只记录诊断，不改写任务结果，诊断回调自身失败也不会卡住后续发布。三条硬约束见
[ADR-0003](../decisions/0003-agent-task-persistence.md)：

- 只存形状与计数，**不存提示词、正文、工具参数**；
- 写失败只记日志，不影响任务结果（`LedgerFailureIsolationTests` 钉住）；
- 启动时读最近 200 行，只处理非终态，**绝不重做**：

| 库里状态 | 恢复后 | 原因码 |
| --- | --- | --- |
| Queued | Cancelled | `cancelled_before_dispatch` |
| WaitingApproval | Cancelled | `approval_lost_on_restart`（#137：待批单只活在进程内，重启即失效，不假装还能批） |
| Running | Unknown | `restart_state_unknown` |
| CancelRequested | Unknown | `restart_state_unknown` |

恢复出来的记录没有提示词（`Prompt` 为空、`CaptureResult=false`），恢复后状态若与库里的
不同会立刻回写一次。运行期只读内存，库里的行是给「重启后」和「事后追溯」看的。

## 6. 已经接上的通道：ZBA `/tasks`

`ZbaTaskRegistry` 现在只做适配，不再自己执行、也不自己存状态：

| 通道语义 | 现在怎么做 |
| --- | --- |
| 幂等键 | `_idempotency`（key → taskId）+ `_bindings`（taskId → key/hash）；命中时用运行时里的记录比对请求哈希，记录已被淘汰则 409 `idempotency_unverifiable` |
| 回执 | **先写回执再提交运行时**：回执写失败时任务还没开始，可以如实说「未受理」；反过来会出现「客户端以为失败、其实在跑」 |
| 事件流 | `Changed` → `task.updated`；失败终态再补一条 `error` |
| 台账 | 保留 200 条 / 活跃 4 的对外上限，淘汰时 `Forget` |
| 结果投影 | 仅 `panel:` 来源的成功任务给摘要，其余 `reason=scope_not_allowed`；`TimedOut` 折成 `Failed` + `timeout` |
| 工具作用域 | 提交体可带可选 `toolScope`（#136）：透传进 `AgentTaskRequest.ToolScope`，**执行前**与配置白名单取交集（只能收窄）；作用域也算进幂等哈希 —— 同一提示词、不同作用域是两件事 |
| 前置审批（#137） | 受理时先过 `IAgentTaskApprovalGate`：有效工具作用域包含 bash/write/docker 且审批开关开启的任务**不排执行**，直接落 `WaitingApproval` 并进待批列表；批准后 `Resume` 从同一条记录继续跑（`alreadyRunning: true`，不重发不新建），拒绝/超期/取消分别收口成 `approval_rejected` / `approval_expired` / `approval_cancelled` |
| 取消 | `Cancel` 映射 `Requested → (state, true, "cancel_requested")`、`TooLate → (state, false, "too_late")`、`NotFound → 404 task_not_found` |

面板直跑（`POST /api/agent`）与 `//` 命令**没有**接进运行时，这是 ADR-0002 的明确取舍：
聊天与 fire-and-forget 链路的行为本轮一点不改，先把唯一对外暴露任务语义的通道迁过来。

## 7. 任务级前置审批（#137）

| 问题 | 答案 |
| --- | --- |
| 谁判定要批 | `src/BotAgent.Headless/Adapters/Agent/TaskApprovalGate.cs`：按 `ServerToolAccess` 的有效作用域判定高危工具 `bash` / `write` / `docker`，审批总开关必须开；省略或空 `toolScope` 继承配置，显式只读作用域与配置取交集后无高危工具则不需审批 |
| 批之前跑不跑 | 不跑。受理线程只建记录并落 `WaitingApproval`，**不排 `Task.Run`**；引擎用例断言 150ms 后执行器调用次数为 0 |
| 批了怎么继续 | `AgentTaskRuntime.Resume`：同一条记录 `WaitingApproval → Running`，`RunAsync(task, alreadyRunning: true)`（跳过首次状态推进），结果仍按原任务投影 |
| 谁来决定 | `DecideApproval(requestId, approve, now)`，ZBA 通过 `POST /zba/v1/approvals/{id}/decide` 触发；待批单在 `GET /zba/v1/approvals` 里以 `session = task:<任务号>` 出现 |
| 超期怎么办 | `SweepApprovals`（`Get` / `List` / `DecideApproval` / `Counts` / `Submit` 时都会先扫）把 `ApprovalExpiresAt <= now` 的收口成 `Cancelled` + `approval_expired`；有效期取 `ApprovalFlow.TtlSeconds`（120 秒） |
| 取消怎么办 | `Cancel` 命中 `WaitingApproval` 时收口成 `Cancelled` + `approval_cancelled`（此前这条边根本不存在） |
| 重启怎么办 | `Restore()` 把库里的 `WaitingApproval` 收口成 `Cancelled` + `approval_lost_on_restart` —— 待批单不落库，重启后没有任何可批的对象 |
| 为什么不落库 | 审批字段（`ApprovalRequestId` / `ApprovalToolId` / `ApprovalSummary` / `ApprovalExpiresAt`）只在快照里，台账表结构一个字节没动（`agent_tasks` 仍是 v13 的 14 列） |
| 票据防伪 | 见 [审批与执行隔离](approval-and-isolation.md) §4：`ApprovalTicket.Signature` + HMAC 验签 |

## 7.1 守护它的测试

| 测试 | 钉住什么 |
| --- | --- |
| `EngineProbe` 的 `AgentTaskApprovalTests` 8 例（#137） | 待批时不调用执行器（零副作用）、批准才跑、拒绝/超期/取消/重启四个原因码、审批关闭时照旧直跑（省略作用域是否继承高危工具由 Headless 闸门测试覆盖） |
| `EngineProbe` 的 7 条 `AgentTaskRuntime: …` | 转移表全矩阵与终态无出边、非法转移抛异常、成功/清洗/截断/未声明采集不存正文、取消（含二次取消 TooLate 与未知 NotFound）、超时与取消区分且 `CancellationRequested=false`、`agent_failed`/`agent_rejected`、并发上限与重复 id、`Forget` 只删终态 |
| `ZbaProbe` 303 条 | 通道语义没变：幂等重放、回执、事件、结果可见性、取消 |
| 集成 S54 / S55 | 面板任务提交与 ZBA 面板后端的端到端行为；S54 还断言「声明了 `toolScope` 的任务把系统提示里的工具清单收窄」（#136）；S55 断言「重启后旧任务可查且不停在排队/运行中」 |
| 集成 S65 | 任务台账跨重启：Queued→Cancelled、WaitingApproval→Cancelled(`approval_lost_on_restart`, #137)、Running→Unknown、出现在列表里、不会被重做 |
| `ProductionSpecProbe` 台账 8 条 | v12 版本（#135 起断言 v13）、建表、无正文列、字段往返保真、同 id 覆盖、`BootSeq` 递增、`Prune` 只留最近 N、清理 |
| `ArchitectureProbe` | 分层与文件头注释约定（Core 不引 Engine、Engine 只引 Core） |

改状态模型时先改本表与 ADR-0002，再改代码；改通道投影时必须同时看 ZbaProbe 与 S54/S55。

## 8. 已知边界

- **重启后不重做**：台账能让记录活过重启，但代价是「可能做过副作用的执行」只能标 `Unknown`，
  没有任何自动续跑（见 §5 的恢复表与 ADR-0003 §2.4）。
- **审批是任务级、不是工具级**（#137）：任务有效作用域含高风险工具就整条先批 —— 粒度粗是刻意取舍（工具级挂起要
  把执行器循环变成可持久化状态并跨重启恢复）；没有多级审批、不做跨重启恢复待批（重启即 `approval_lost_on_restart`）。
  口径见 [审批与执行隔离](approval-and-isolation.md)。
- **没有远程重启**：`RestartRequired` 语义仍属配置链路，本层不提供重启按钮。
- **不暴露到 OpenAPI**：运行时是内部构件，唯一对外契约仍由 ZBA 路由与 `ZbaOpenApi` 定义。
- **超时折成 Failed 是本轮唯一可观察的行为变化**：以前 ZBA 超时会把取消也算成 `Cancelled`，
  现在带 `errorCode=timeout` 的 `Failed`；客户端如果按状态枚举分支，需要认这个原因码。

## 9. 部署、验证与回滚

- 上线步骤、验收命令与不可逆边界：[Agent Runtime 部署、验证与回滚](../operations/agent-runtime-deployment.md)
