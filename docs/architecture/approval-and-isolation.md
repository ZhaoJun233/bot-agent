# 审批与执行隔离（#117）

这一页写清三件事：**谁需要人批**、**批了之后是什么在保证「没批就真的不执行」**、以及**事后能查到什么**。
它不是 API 手册 —— 面板与 ZBA 的端点形状见 `docs/api-surface.md`。

## 1. 今天覆盖到哪（先把边界说在前面）

| 执行路径 | 走不走审批 | 说明 |
| --- | --- | --- |
| 聊天轮里的高风险工具（搜索 / 点歌 / 语音 / 表情 / 戳一戳） | **不**走审批 | 走能力闸门 `ToolGate`（策略 `ToolPolicy`），由配置开关注销；它们不算「任意代码执行」 |
| 聊天轮里的「模型点名工具」 | 走审批，但只认服务端固定工具 | `ApprovalFlow.FixedToolId`（`demo.echo`，**无真实副作用**的假工具）——模型点名别的工具连单都开不出来 |
| `//` 命令里的服务器 agent 工具（bash / read / write / docker …） | **不**走审批 | 由 `ServerToolGate` 的 `ToolPolicy`（默认关 + 显式放行集）管；`HighRiskExceptions` 是唯一的例外通道 |

> ⚠ **已知缺口（本轮如实记录）**：审批还没有接到真实高风险执行点上。要做到那一步，需要让执行器在「需要批准」时**挂起并在批准后恢复同一轮**（`//` 链路的 `ServerAgentRunner` 循环里暂停），属 Runtime 扩展，已单独立条目跟进。
> 因此本条目的「审批前无副作用」只对固定假工具成立 —— 假工具本来就没有副作用，所以这条验收仍然是**未满足**状态。

## 2. 判定顺序（唯一入口）

所有「同意 / 拒绝 编号」都走 `ApprovalFlow.Handle`（`src/BotAgent.Core/Domain/Permissions/ApprovalFlow.cs`），顺序固定：

1. 编号形状不对 / 未知编号 → 不动作；
2. **跨会话**（这张单不是这个会话开的）→ 不动作；
3. **过期**（含「已批准但过了有效期」）→ 不动作，且不发执行回执；
4. **不是名单内的人、也不是 owner/admin** → 不动作；
5. 已经决定过 → 不动作（拒绝是终局，批准也只消费一次）；
6. 批准 → **一次性消费票据**；重复消费拿不到票据。

有效期默认 120 秒、上限 900 秒（`ApprovalStore.DefaultTtlSeconds` / `MaxTtlSeconds`）。**批准不会延长有效期**：过期判定发生在消费时，与谁批的、批得多早无关；这条在 #117 补了专门用例（SafetyProbe「批准后过了有效期再消费 → 失败且不给票据」）。

## 3. 三条独立的 fail-closed

面板这条写路径（`/api/approvals/decide`）能让「需要批准的工具」真的走完执行，所以三道闸各自独立：

| 闸 | 位置 | 关掉时 |
| --- | --- | --- |
| 面板令牌必须已配置 | `src/BotAgent.Headless/Adapters/Panel/WebUiServer.Approvals.cs` | `panel_token_required`（未配令牌时面板对回环全开，这时放行等于交出执行权） |
| 审批总开关必须开 | 同上 + `ZbaHostBackend.DecideApprovalAsync` | `approvals_disabled` |
| 判定一条都不放宽 | `ApprovalUseCase.PanelDecide` → `ApprovalFlow.Handle` | 走的是群里同一条判定，不另写一套 |

## 4. 执行隔离（#117 补齐的两处）

平台源的租约（`PlatformSourceLease.IsCurrent`）代表「这条执行属于哪一代通道」。换代（重连 / 重启协议端）之后旧租约必须立刻失效，旧租约下的执行不能继续。原来只核了一次，现在核两次：

| 位置 | 原来 | 现在 |
| --- | --- | --- |
| 面板决策（`ApprovalUseCase.PanelDecide`） | 取不到原始租约就 `PlatformSourceExecution.Enter(null)` 后照批 | 取不到 → `source_unavailable`（失败关闭）；已换代 → `source_retired` |
| 群命令（`ApprovalUseCase.HandleCommand`） | 入口核一次 `IsCurrent` | 入口核一次，**执行前再核一次**（`执行前平台源已换代（租约失效）→ 不执行`） |
| 面板决策后执行 | 只在决策时核 | 执行前再核一次租约 |

另外执行前**必过一次能力闸门**（`Capabilities.Check(FixedToolId, conversationKey, ticket)`）：闸门才是「能不能执行」的唯一判据；高风险类别在闸门里任何审批都放不开，这一层不依赖审批流程写得对不对。

### 还没堵上的缝

`ApprovalTicket` 是公开 record（`src/BotAgent.Core/Domain/Permissions/ToolPolicy.cs`），`ToolGate` 只校验 tool + 会话，不验「这张票是不是 `Consume` 真签发的」。所以在同一进程里手搓一张票就能过闸门 —— 目前**没有实际提权路径**（生产只有 `ApprovalUseCase` 一家发票，两条入口都走 `ApprovalFlow.Handle`），但这是「靠没人这么写」而不是「结构上不可能」。已在遗留条目里跟进（票据加 nonce / 验签）。

## 5. 事后能查到什么

| 证据 | 位置 | 内容 |
| --- | --- | --- |
| 待批列表 | 面板 `GET /api/approvals`、ZBA `GET /zba/v1/approvals` | 只列 **Pending**：编号 / 工具 / 已脱敏摘要 / 脱敏后的会话 key / 剩余秒数 |
| 事件流 | `ZbaEventBuffer` 的 `approval.decided` | #117 起 `state` 如实反映结果：`approved` / `rejected` / `expired` / `stale_policy` / `not_an_approver` / `unknown_request` / `source_retired` / `source_unavailable`（以前无论哪种失败都写 `rejected`，事后分不出「被人拒绝」和「根本没等到人」）；`reasonCode` 同时带上 |
| 审计 | `SecurityAuditLog` 的 `approval_decision` | 操作者、会话、`{requestId, action, reason, shouldExecute}`、当时的策略版本 |
| 轨迹节点 | `TurnTraceStore` 的 `Gate` 节点 | 决策后执行前那道闸门放行还是拒绝、原因码 |

**看不到的**：`/approvals` 只列待批，决定历史要去事件流/审计里查（`ZbaApprovalSummary` 没有 DecidedBy/DecidedAt）。这是刻意的最小面 —— 加字段要动两份契约。

## 6. 守护测试

| 断言 | 位置 |
| --- | --- |
| 判定顺序六条（跨会话 / 过期 / 非名单 / 已决定 / 一次性 / 策略版本） | `tests/BotAgent.SafetyProbe/Program.cs` 的 P3b 段 |
| 批准后过期再消费失败（#117 新增） | 同上，`★ #117` 两条 |
| 面板决策 / 执行前复核租约在代码里（#117 新增） | `tests/BotAgent.ArchitectureProbe/Program.cs` 的 `PanelApprovalChecks` |
| 平台源换代时审批不执行 | `tests/BotAgent.ReviewRemediationProbe/PlatformLifecycleTests.cs` |
| 面板/ZBA 端点的前置校验与错误码 | `tests/BotAgent.ZbaProbe/Program.cs`、`tests/BotAgent.IntegrationHarness/ZbaScenario.cs` |

## 7. 改这里的检查单

1. 想「让某个工具需要审批」→ 先看它属于哪条路径；`//` 里的高风险工具今天的正确做法是 `ToolPolicy` 的显式放行集，**不要**直接塞进审批（会得到「批了也不执行」的死胡同）。
2. 改判定顺序 → 同步 `ApprovalFlow.Handle` 的注释、SafetyProbe 的六条断言、这一页的第 2 节。
3. 加事件 state → 同步 `ZbaHostBackend.DecideApprovalAsync` 的映射、前端 `TasksPage.vue` 的展示与这一页第 5 节。
4. 动租约 → 两条入口（群 / 面板）与执行前复核必须一起改；只改一处等于留一条绕过路径。
5. 不要把审批状态写进任务状态 `WaitingApproval` —— 那个值今天没有写入方（只有定义与投影），接它属于 follow-up，见 `docs/architecture/agent-task-runtime.md`。
