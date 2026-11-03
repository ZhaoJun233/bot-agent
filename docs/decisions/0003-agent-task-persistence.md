# ADR-0003：任务台账持久化与重启恢复（#116）

- 状态：已接受（2026-11-03）。实施分支 `feat/task-persistence`，父条目 #100。
- 影响面：`src/BotAgent.Storage/AppDatabase.cs`（`agent_tasks` 表、schema 11 → 12）、`src/BotAgent.Engine/Runtime/AgentTaskLedger.cs`（新端口）、`src/BotAgent.Engine/Runtime/AgentTaskRuntime.cs`（写入点与启动恢复）、`src/BotAgent.Headless/Adapters/Persistence/AgentTaskLedgerStore.cs`（新 DAO）、`src/BotAgent.Core/Domain/Agent/AgentTaskTransitions.cs`（`Unknown` 新增入边）。
- 相关条目：#114（运行时本体，已完成）、#115（工具注册表）、#117（审批与隔离）、#119（部署与回滚文档）、#100（总纲）。

## 1. 背景

### 1.1 #114 留下的洞

ADR-0002 把任务状态收进了 `AgentTaskRuntime`，但记录只在内存里。进程重启（部署、崩溃、OOM 被杀）之后：

- 受理过的任务一条也查不到 —— `GET /zba/v1/tasks/{id}` 返回 404，面板列表为空；
- `AgentTaskState.Unknown` 虽然定义了，但转移表里**没有任何入边**，实际上不可达（`AgentTaskTransitions` 的四张目标表都不含它），ADR-0002 写的「#116 恢复用」是一句承诺而不是事实；
- 崩溃窗口内的任务处于「不知道有没有副作用」的状态：可能已经调用过工具、发过消息，也可能一条都没发。

### 1.2 #116 要求

1. 任务状态、取消/超时/失败结果可持久化，重启后能恢复或明确标记失败；
2. schema 迁移可前向、可预期，回滚路径写清楚；
3. **绝不重做**已经可能做过副作用的执行；
4. 不落提示词与正文（AGENTS.md 的隐私红线 + ADR-0002 的「只存形状与计数」）；
5. 有合成用例覆盖崩溃-恢复（s65）。

## 2. 决策

### 2.1 一张表、只存形状，`user_version` 11 → 12

`agent_tasks` 一行一个任务，列全部是形状与计数：

| 列 | 用途 |
| --- | --- |
| `id` TEXT PRIMARY KEY | 任务 id（通道生成） |
| `state` INTEGER | `AgentTaskState` 数值（与 `ZbaTaskState` 前八位一一对应） |
| `error_code` TEXT | 原因码（`restart_state_unknown` / `cancelled_before_dispatch` / `timeout` / `agent_failed` …） |
| `source_key` / `session_id` / `target` | 归属与去处，用于列表与追溯 |
| `created_at` / `updated_at` | 带偏移的 ISO 往返时间戳 |
| `duration_ms` / `tool_calls` / `cancel_requested` / `result_chars` / `result_truncated` | 计数与标志 |
| `boot_seq` | 写入进程的启动序号，用于跨进程归属判断 |

**没有** `prompt`、`result_summary`、工具参数、正文列。会话正文仍在会话存储里，台账只回答「这个任务怎么样了」。

DDL 放在 `AppDatabase.CreateAgentTaskLedger`，从 `Migrate` 调用（**不能**塞进 `CreateSchema`：会把最长方法从 198 行顶到 199，触发 ArchitectureProbe 的「全 src 最长方法 ≤198」红线）。迁移分支写在 `ApplyMigrations` 的 v11 之后，`SetLegacySchemaVersion` 白名单加 12，版本闸从 `> 11` 改成 `> 12`。

### 2.2 端口在 Engine、实现在 Headless 的持久化前缀

`IAgentTaskLedger`（`src/BotAgent.Engine/Runtime/AgentTaskLedger.cs`）三个方法：`Save` / `LoadRecent(int)` / `Prune(int)`。三条约定写进注释且被 `LedgerFailureIsolationTests` 钉住：

- `Save` 必须幂等（状态每变一次写一次，用 `INSERT OR REPLACE`）；
- 任何存储异常都不许抛给状态机 —— 存储问题只影响可追溯性，不许决定任务成败；
- `LoadRecent` 只返回一批（恢复上限 200 条），不做全表扫描。

DAO 放 `src/BotAgent.Headless/Adapters/Persistence/`，因为 ArchitectureProbe R2 只允许这个前缀写 SQL；放 `BotAgent.Storage` 会红（那条规则是「存储层不新增长期不登记的文件」）。

### 2.3 写入点是状态机自己，不是通道

`AgentTaskRuntime` 在四个位置（都在锁外）调 `Persist`：Submit 受理后、Cancel 变更前、Finish 与 MoveTo 变更前。这样任何通道自动获得持久化，通道不需要知道台账存在。通道侧只多了一件事：构造时把 `bootSeq` 传给运行时。

### 2.4 恢复策略：收口，不重做

启动时 `Restore()` 读最近 200 行，只处理**非终态**：

| 库里状态 | 恢复后 | 原因码 | 理由 |
| --- | --- | --- | --- |
| Queued | Cancelled | `cancelled_before_dispatch` | 从未开始执行，取消无副作用（`Queued` 的转移表没有 `Failed`，所以不能记失败） |
| WaitingApproval | Cancelled | `cancelled_before_dispatch` | 同上；审批上下文已随进程消失 |
| Running | Unknown | `restart_state_unknown` | 可能已产生副作用，本地无法证明，也不能重做 |
| CancelRequested | Unknown | `restart_state_unknown` | 取消请求是否送达未知 |

**绝不重做**：重放的代价是重复副作用（重复发消息、重复调用外部工具），而重放本身也不安全 —— 通道的幂等键已经绑定旧实例，同键重放会被拒绝（`idempotency_unverifiable`），能重做的只有「自己造一条新任务」，那不是恢复而是新提交。恢复出来的记录 `Prompt` 为空、`CaptureResult=false`，引擎层面也没有可执行的输入。

恢复后状态若不等于库里的值，立刻回写一次（下一次崩溃前库里就是真话）。`updated_at` 用恢复时刻，`boot_seq` 用本次进程序号。

### 2.5 `Unknown` 的入边

`AgentTaskTransitions` 的 `RunningTargets` 与 `CancelRequestedTargets` 加 `Unknown`（其余表不动）。语义：**本地无法判定**。它不表示失败，也不表示成功；通道投影给客户端时靠 `errorCode` 说清「重启时状态未知，可能已产生副作用」。

## 3. 不可破坏的语义（与守护它的测试）

| 语义 | 守护 |
| --- | --- |
| 恢复后的任务绝不处于排队/运行中 | `ZbaRestartScenario`（s55）「重启后旧任务仍可查询且不停在排队/运行中」+ `TaskRecoveryScenario`（s65） |
| Running → Unknown、Queued → Cancelled 且带原因码 | s65 两条断言 |
| 恢复的任务出现在通道列表里 | s65 |
| 恢复的任务不会被重做（再查状态不变） | s65「不会被重做」 |
| 台账写入失败不影响任务结果 | `EngineProbe` 的 `LedgerFailureIsolationTests` |
| 状态推进都落库（往返保真） | `ProductionSpecProbe` 的 8 条台账用例 |
| 台账不含 prompt / 正文列 | `ProductionSpecProbe`「agent_tasks 无 prompt/result/summary 列」 |
| schema 版本号与契约同步 | `DatabaseContractTests`（v12）、`ConfigurationContractTests`（物理 schema 12）、`ArchitectureProbe` 的 `SqlLiteralTotal` 141 |

## 4. 分步实施与验证

1. 扩 `AgentTaskTransitions` 的 `Unknown` 入边，同步 EngineProbe 期望矩阵与两份文档的转移表；
2. 建表 + 迁移 + 版本闸 + 白名单（`AppDatabase`）；
3. 端口与运行时（`AgentTaskLedger` / `Restore` / `Persist`）；
4. DAO（`AgentTaskLedgerStore`）+ 接线（`ZbaHostBackend`）；
5. 门禁：`SqlLiteralTotal` 138 → 141、`DatabaseContractTests` 与 `ConfigurationContractTests` 的版本号、`ArchitectureProbe` 上限断言；
6. 用例：EngineProbe 3 条、ProductionSpecProbe 8 条、集成 s65 六条、s55 语义升级一条。

验证结果（2026-11-03，Release）：`ArchitectureProbe` 211/0、`EngineProbe` 37/0、`ProductionSpecProbe` 111/0、s65 6/0、s55 全绿。

## 5. 风险与取舍

- **schema 只前向**：新二进制把库写到 v12 之后，旧二进制启动会被 `database_schema_newer` 拒绝。回滚 = 先用 `VACUUM INTO` 快照恢复库，再退二进制（见 `docs/operations/rollback-playbook.md`）。这是本项目一贯策略，不是本轮新增。
- **共用写闸**：台账写入与其它写共用 `AppDatabase.WriteGate`，任务状态频繁变化时会让配置保存略微排队；实测影响可忽略（每次写入一条小行），但池化并发量的上限仍由那个闸决定。
- **不清洗历史**：`Prune` 只在启动时按 256 行裁一次，运行期只追加。与通道路径的 200 条 / 7 天回执保留策略不同量级，属刻意取舍（重启溯源优先）。
- **多实例未定义**：同一份库被两个进程打开不在本轮支持范围（`boot_seq` 只是归属线索，不是分布式锁）。多实例部署要等 #119 的部署文档一起定义。
- **不保留正文**：想复盘「任务到底说了什么」时，台账帮不上忙 —— 那是会话存储与 trace 的职责。

## 6. 不做的事

- 不新建工程、不改 `BotAgent.slnx` 与 `ArchitectureProbe.Baseline` 的工程白名单；
- 不把聊天轮、`//` 命令、面板直跑接进运行时（仍属 ADR-0002 的取舍）；
- 不做任务重放/断点续跑（见 §2.4）；
- 不做跨进程实时查库（运行期只读内存，库里是给重启后看的）；
- 不做审批持久化（`WaitingApproval` 的恢复一律按「取消了」处理，审批记录属 #117）；
- 不暴露运行时或台账到 OpenAPI。