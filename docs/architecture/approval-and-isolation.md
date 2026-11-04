# 审批与执行隔离（#117 / #137）

这一页写清三件事：**谁需要人批**、**批了之后是什么在保证「没批就真的不执行」**、以及**事后能查到什么**。
它不是 API 手册 —— 面板与 ZBA 的端点形状见 `docs/api-surface.md`。

## 1. 今天覆盖到哪（先把边界说在前面）

| 执行路径 | 走不走审批 | 说明 |
| --- | --- | --- |
| 聊天轮里的高风险工具（搜索 / 点歌 / 语音 / 表情 / 戳一戳） | **不**走审批 | 走能力闸门 `ToolGate`（策略 `ToolPolicy`），由配置开关注销；它们不算「任意代码执行」 |
| 聊天轮里的「模型点名工具」 | 走审批，但只认服务端固定工具 | `ApprovalFlow.FixedToolId`（`demo.echo`，**无真实副作用**的假工具）——模型点名别的工具连单都开不出来 |
| 普通 `//` 命令、旧 `POST /api/agent` 直跑的服务器 agent 工具 | **不**走任务级前置审批；尚未接入 `AgentTaskRuntime` | 仍由 `ServerToolGate` 的 `ToolPolicy`（默认关 + 显式放行集）管；`HighRiskExceptions` 是显式放行通道，不能把它描述成人工审批。迁移这些旧入口仍是独立工作 |
| ZBA / 面板提交的**任务**（`POST /zba/v1/tasks`） | 审批开关开启且**有效工具作用域**含 bash / write / docker 时，走任务级前置审批 | `TaskApprovalGate` 按与执行器一致的配置白名单交集判定；省略或空 `toolScope` 是继承，不是无工具。批准之前执行器一次都不会被调用，批准后从同一张单继续 |

> **#137 落地的是任务级、不是工具级**：粒度是粗的 —— 任务的**有效工具作用域只要包含**高风险工具，整条任务都要先批（哪怕最后一步都没用上 bash）。
> 这是刻意的 fail-closed 取舍：工具级挂起要把 `ServerAgentRunner` 循环里的消息、步数、当前调用与租约都变成可持久化状态并跨重启恢复，成本远超本轮；任务级闸门天然可持久化（任务只有一行台账），且「批准前零副作用」是结构性的，不依赖执行器内部写对没写对。取舍见 `docs/architecture/agent-task-runtime.md` §7。

## 2. 判定顺序（唯一入口）

所有「同意 / 拒绝 编号」都走 `ApprovalFlow.Handle`（`src/BotAgent.Core/Domain/Permissions/ApprovalFlow.cs`），顺序固定：

1. 编号形状不对 / 未知编号 → 不动作；
2. **跨会话**（这张单不是这个会话开的）→ 不动作；
3. **过期**（含「已批准但过了有效期」）→ 不动作，且不发执行回执；
4. **不是名单内的人、也不是 owner/admin** → 不动作；
5. 已经决定过 → 不动作（拒绝是终局，批准也只消费一次）；
6. 批准 → **一次性消费票据**；重复消费拿不到票据。

**任务级待批单走的是另一条决策入口**（`AgentTaskRuntime.DecideApproval`）：任务没有 QQ 平台来源租约，`ApprovalUseCase.PanelDecide` 只会报 `unknown_request`，所以 `ZbaHostBackend.DecideApprovalAsync` 转而问任务运行时；判定（过期先扫、已决定不再动）由任务运行时自己的状态机保证，见 `docs/architecture/agent-task-runtime.md` §7。

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

**#137 已补上：票据验签。** `ApprovalTicket` 增第 5 个字段 `Signature`，`ApprovalStore.Consume` 签发时用 `ApprovalTicketSigner.Sign(requestId, toolId, conversationKey, grantedAt)`（进程内随机 32 字节密钥 + HMACSHA256 + `CryptographicOperations.FixedTimeEquals`），`ToolGate.Evaluate` 在 tool / 会话校验之后**多验一道签**，失败返回 `approval_ticket_unverified`。密钥随进程生成 ⇒ 重启后旧票全失效（票据本来就只在同一轮里活着）。检查顺序刻意保留 tool/conv 在前，所以既有的 `approval_tool_mismatch` / `approval_conversation_mismatch` 断言不变。负向用例：SafetyProbe「手搓票据（结构相同、无签名）→ 拒绝」。

## 5. 事后能查到什么

| 证据 | 位置 | 内容 |
| --- | --- | --- |
| 待批列表 | ZBA `GET /zba/v1/approvals` | 合并聊天 Pending 与任务级待批单；任务会话写成 `task:<任务号>`（不属于聊天会话）。旧 `GET /api/approvals` 仅列聊天待批，不合并任务级待批单 |
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
| 手搓票据被拒（#137 新增） | `tests/BotAgent.SafetyProbe/Program.cs`：`★★ 手搓票据（结构相同、无签名）→ 拒绝（approval_ticket_unverified）` |
| 任务级待批：不批不跑 / 批了才跑 / 拒了收口 / 超期收口 / 重启收口 | `tests/BotAgent.EngineProbe/AgentTaskApprovalTests.cs`（8 例） |
| 任务级审批真链路（提交→state=2→ZBA 批准→跑完并回原会话、拒绝路径） | `tests/BotAgent.IntegrationHarness/ZbaScenario.cs`（S54） |

## 7. 改这里的检查单

1. 想「让某个工具需要审批」→ 先看它属于哪条路径；`//` 里的高风险工具今天的正确做法是 `ToolPolicy` 的显式放行集，**不要**直接塞进审批（会得到「批了也不执行」的死胡同）。
2. 改判定顺序 → 同步 `ApprovalFlow.Handle` 的注释、SafetyProbe 的六条断言、这一页的第 2 节。
3. 加事件 state → 同步 `ZbaHostBackend.DecideApprovalAsync` 的映射、前端 `TasksPage.vue` 的展示与这一页第 5 节。
4. 动租约 → 两条入口（群 / 面板）与执行前复核必须一起改；只改一处等于留一条绕过路径。
5. 任务级待批的判定在 `src/BotAgent.Headless/Adapters/Agent/TaskApprovalGate.cs`（有效作用域 + 配置白名单 + 高危工具名表 + 审批总开关），
   执行侧的挂起/恢复在 `AgentTaskRuntime`（`Resume` / `Reject` / `DecideApproval` / `SweepApprovals`）。
   改任一处都要同步 S54 用例与这一页。
