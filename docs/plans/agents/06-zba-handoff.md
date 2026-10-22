# ZBA v1 交接包（Wave5 / Agent-06 → Agent-00 / 负责人）

> 版本：`zba-1.0`（`ZbaContract.Version`，本批未改契约）。当前状态（2026-10-20）：**Wave5 完整准出已由 Agent-01 独立复验 Accepted（当前未提交工作树 / Windows 合成范围，无 High/Medium）：W5-M5、W5-G1、W5-G2、W5-G3、W5-G4 全部闭合；G4 的 18 条既有失败全部修复（定向 109/0、全量 1086/0）；最终产物 G3 `pass=True`（Headless `6A5DDD70…2FD89D`）、G2 重测在负责人确认预算内。** 阶段 6 S0 的 G01–G03 增量（会话发现/准备、结果摘要、提交回执与四态查询）已于 2026-10-20 在 Windows 合成宿主（19:35）与本地 Docker Desktop Linux/amd64（19:42）独立验收 **Accepted**；S0 已由负责人 20:01 按推荐签署（`.git/frontend-wave6-s0-20261020-1845/s0-closure-20261020-1948.md`）。**S1 只读 Web 于 2026-10-20 21:32 独立复验 NotAccepted（R1–R4）；R1–R4 已修复并重跑（unit 58/0、Chromium e2e 14/14、验收方原始反例 6/6），仍待独立复验**（`src/web`：认证、契约兼容、概览；F1–F7/F9 已修复，F8 留 S4，证据 `.git/s1-readonly-acceptance-20261020-213235/`、`.git/s1-r1-r4-fix-20261020-2218/`）；2026-10-21 09:13 已按 A1/A3 采用并行控台改版并替换产品图标为负空间机器人头（BrandMark + favicon + badge；小字/焦点对比度修正；unit 59/0、e2e 14/14、1280/390 截图与 AA 审计），第二轮 V1 已于 2026-10-21 11:38 由用户本人确认通过（`.git/s2-gate-check-20261021-1140/v1-signoff.md`）；12:09–12:22 冻结 D4 非空配置 read view（kind=0 完整脱敏样本 8 分区/134 字段/6 秘密、kind=1 可编辑样例、`ReadField` 变体与秘密无值断言、未知/缺失分区负例；`src/api/zba.ts` 类型与 `wire-guard` 的 `record/json`），dist 仍为 `393EEF02…`；当前候选在隔离副本复跑 typecheck 0、unit **62/62**、e2e **14/14**、原始探针 **2/2 + 4/4**、Architecture **172/0**、Frontend **341/0**、ZbaProbe **194/0**（`.git/s1-reaccept-20261021-1215/reacceptance-report.md`，实施侧）。**S2 已由负责人 2026-10-21 12:52 “开始s2”授权开工**：S2-1 配置只读页已接入真实合成宿主（8 分区/134 字段/6 秘密/7 插件/1 供应商；unit **71/71**、Chromium e2e **16/16**、除 read 外无写请求；dist `9B7997E3…`，`.git/s2-start-20261021-1252/S2-1-report.md`），S2-2 草稿/保存/冲突/丢响应已完成：跨分区草稿、整批校验、`configuration/save`（expectedVersion + generation + `cfg1:` 键）、409 保留草稿并标重叠、丢响应按同一键 `find` 核对（不自动重发）、秘密 Keep/Replace/Delete；unit **81/81**、真实 e2e **19/19**、dist `FB287ED9…`（`.git/s2-start-20261021-1252/S2-2-report.md`），下一步 S2-3 能力页。2026-10-21 15:05 S2-2 验收 **NotAccepted**（M1 草稿生命周期、M2 未确认态操作键）；M1–M3/L1–L3 已整改并重跑：unit **86/86**、真实 e2e **24/24**、dist `748DE0F4…`，待验收方复验（`.git/s2-fix-20261021-1550/fix-report.md`）。**S1 门禁 Pass（12:38 复验报告）；S1 完整独立签收仍待验收方确认**。证据与补验条件见[执行总纲 §27.10](00-orchestration-and-dependency-graph.md#2710-w5-g1g4-整改与补验2026-10-19)、`.git/agent01-wave5-full-exit-20261019/acceptance-report.md`、`.git/g01-g03-reacceptance-20261020-1935/`、`.git/g01-g03-linux-acceptance-20261020-1942/`。
> 本文件是交接说明，不是发布授权；不连接生产、不部署、不读取真实会话。

## 1. 入口与契约

| 项 | 位置 |
| --- | --- |
| 工程 | `src/BotAgent.Panel/BotAgent.Panel.csproj`（只引用 `BotAgent.Core`） |
| HTTP/JSON/认证 | `src/BotAgent.Panel/ZbaEndpoint*.cs`、`ZbaSecurity.cs`、`ZbaJson.cs` |
| 实际 OpenAPI | `GET /zba/v1/openapi.json`（需要 read Bearer）；由 `ZbaRoutes.All` 同一张路由表生成 |
| 宿主适配 | `src/BotAgent.Headless/Adapters/Panel/Zba/ZbaHostBackend.cs` 等 |
| 只读 CLI | `BotAgent.Headless --status/--capabilities/--tasks/--events` |
| 契约测试 | `tests/BotAgent.ZbaProbe`；宿主真链路 `tests/BotAgent.IntegrationHarness` 的 `s54` |

`ZbaRoutes.All` 同时驱动分派和 OpenAPI；`ZbaProbe` 对每条路由断言 path+method 双向覆盖，防止文档/实现漂移。
OpenAPI 对 Core 的复杂 DTO 只声明安全的高层形状，不替代 C# 序列化契约；客户端应按 DTO 字段与拒绝规则处理。

请求 JSON 的 DTO 成员名按**精确大小写**绑定（`kind` 不等于 `Kind`）；大小写变体视为未知字段并返回 `400 invalid_json`，
与 ordinal 重复字段检查（同名重复返回 `400 duplicate_field`）保持一致。`changes`/字典/业务 key 的大小写语义不受影响。

## 2. 认证与授权

- `BOTAGENT_ZBA_TOKEN`：管理员（读写、管理动作、审批、任务提交/取消、`configuration/find`）。
- `BOTAGENT_ZBA_READ_TOKEN`：只读（状态、能力列表/检查/查询、配置读取、任务读取、审批列表、事件、OpenAPI）；`configuration/find` 需要 admin。
- 兼容前缀 `QQCHAT_ZBA_*`；令牌只从环境变量读，面板/DTO/错误/日志均不回显。
- 仅 `Authorization: Bearer <token>`；ZBA 不使用 Cookie，因此写路径没有浏览器 CSRF 面。
- 未配置任何令牌时，除 `/zba/v1/health` 外一律 `503 zba_not_configured`（fail-closed）。
  `BOTAGENT_ZBA_ALLOW_LOOPBACK=1` 只用于本机开发：回环请求视为管理员，默认关闭。
- 10 次/分钟认证失败后按来源返回 `429`；`401` 带 `WWW-Authenticate: Bearer`。
- 授权在应用层再检查一次：read 调用者不能 `Execute/Cancel`；调用者由 ZBA 在验证 Bearer 后构造，不接受请求头伪造。
- 旧面板的 `X-Panel-Token` 与 ZBA Bearer 是两条独立边界；Wave5 同时修复了“无密码但配令牌仍免密”的 Issue #78 分支。

## 3. 能力管理矩阵

| 能力 | 列表/检查 | 支持动作 | 不支持时的行为 |
| --- | --- | --- | --- |
| 平台驱动 Driver | 全部已声明/已加载实例，真实运行状态 | Start / Stop / TestConnection（受配置/凭据/就绪门禁） | 未加载或不可用时动作 `supported=false` + 原因码 |
| 插件 Plugin | 全部已登记插件（稳定 id、启用状态） | 无；返回 `configuration_required` | Execute 返回 `UnsupportedAction`（HTTP 422） |
| 模型 Provider | 已登记 provider、凭据是否配置、重启要求 | TestConnection | 未配置/需重启时原因码 |
| Agent 设备 / MCP | 出现在能力目录（如绑定提供） | 无 | 明确拒绝；MCP 管理页不在本版范围 |
| Docker / 本地高权限 | 不出现在 ZBA 管理面 | 无 | 另行安全评审，不作为通用能力 |

插件启停必须走统一配置保存（`EnabledPlugins` 类字段），不能把一次内存 `SetEnabled` 冒充持久化管理。
写动作复用 `IManagedCapabilities` 的回执、描述符版本、幂等键和取消语义，不另造平行实现。

## 4. 配置读取与保存

| 端点 | 请求 | 语义 |
| --- | --- | --- |
| `POST /zba/v1/configuration/read` | `{"kind":0}` 概览 / `{"kind":1}` 可编辑 | 返回 Core `ConfigurationReadResult`：版本、generation、契约版本、视图、秘密仅元数据、字段效果、最近应用状态 |
| `POST /zba/v1/configuration/save` | `contractRevision`、`expectedVersion`、`key`、`changes`、`secrets` | 整批校验/事务提交/发布；返回 `ConfigurationSaveResult` |
| `POST /zba/v1/configuration/find` | `{"key":"cfg1:..."}` | 按操作键查最终提交/应用回执，用于响应丢失后的核对 |
| `POST /zba/v1/capabilities/validate` | 同上但无 `key` | 只校验，不写 |

- `key` 是字符串 `cfg1:<generation 32hex>:<issuedUnixSeconds>:<nonce 32hex>`；generation 必须等于当前配置 generation。
- `expectedVersion` 必须存在且为正；旧版本返回 `409`/`VersionConflict`（code 3），不覆盖新值。
- 幂等键（操作 key）重复提交返回原回执；同 key 不同内容返回 `409`/`IdempotencyConflict`；过期/代际不符分别拒绝。
- `changes` 是 Core `ConfigurationPatch`：字段用 `{"kind":0|1|2,"value":...}`（Keep/Set/Clear）；未知字段/重复字段/非法 Unicode 一律 400。
- `secrets` 最多 6 条，slot 不得重复；`Replace` 必须有非空值（≤8 KiB），`Keep/Delete` 不得带值。`secrets` 集合本身为 null 或数组元素为 null 都按 `400 invalid_json`/`missing_secrets` 拒绝。QQ 官方 Secret 没有可写 slot。
- 保存响应分别给出 `commitState`（是否已提交）、`applyState`（Applied/PartiallyApplied/RestartRequired/Failed/Unknown）和逐步骤回执；
  应用失败不回滚已提交事实，也不伪装成“未保存”。
- `configuration.saved` 事件的 `state` 投影真实应用结果（`applied`/`partially_applied`/`restart_required`/`failed`/`unknown`/`not_started`；无变化为 `no_changes`），
  不再用保存成功一律写 `applied`；`version` 表示已提交版本，`reasonCode` 保留应用状态的原因码。
- 字段级生效规则来自 `FieldDescriptor.Effect`（Hot/Restart/ExternalProjection/ReadOnly）；ZBA 不做第二套判断。

## 5. 任务、审批与取消

### 5.1 任务状态

`Queued → Running → Succeeded | Failed`；提交后可进入 `CancelRequested → Cancelled`。
cfg-0.3.3 起只有服务器目标：取消在本地确认，不冒报远端已停止；已完成/已失败的任务不会被迟到的取消结果覆盖；取消竞态返回 `too_late`。

- `POST /zba/v1/tasks` 需要 `Idempotency-Key`（8–128，`[A-Za-z0-9._~-]`）；同 key 重放返回同一稳定 id，不同请求体返回 `409`。
- 请求校验：`sourceKey` ≤256、`sessionId` ≤128、`prompt` 1–32,000、`target=server`、`timeout=10..900`；旧 `host`/设备目标统一 `400 invalid_target`。
- 未知会话 `404 session_not_found`；旧 host 会话 `400 host_backend_removed`（只读留存，不进入台账）；服务器 agent 未启用 `409 server_agent_disabled`。
- 台账上限：4 个非终态任务、200 条记录。达到 200 条时新提交仍先入账，回执写成功后再淘汰最旧终态记录
  （回执保留，因此 `find` 可报 `expired`、同键重放报 `record_evicted`）；没有可淘汰的终态记录才
  `429 task_capacity_exceeded`。4 个并发上限仍单独提前返回 429。
- 列表/详情只含 id、状态、目标、session id、时间、耗时、工具调用数、安全错误码、取消标记；
  **不返回 prompt、工具载荷或模型隐藏推理**。`GET /tasks/{id}/result`（admin）只对 `panel:` 源的成功任务返回
  ≤4000 字符的有界摘要，其余返回 `available=false` + 安全 `reasonCode`；摘要只在内存，重启/淘汰后 404。
  内存台账重启即丢；重启后旧任务不可查询，客户端应视为 unknown，不能重做副作用。
- `POST /tasks/{id}/cancel` 返回 `cancel_requested`，不冒报“已取消”；随后用 `GET /tasks/{id}` 查终态。

### 5.2 审批

`GET /zba/v1/approvals` 只返请求 id、工具 id、建单时已脱敏摘要、显示层脱敏会话、过期时间、策略版本。
`POST /zba/v1/approvals/{id}/decide` 绑定本次请求；未知 404，非审批人 403，过期/已决/策略变化 409。
审批仍走 `ApprovalUseCase.PanelDecide`（身份/有效期/一次性/策略版本 + 执行前闸门），ZBA 不放宽判定。
`EnableApprovals=false` 时 ZBA 直接 403 `approvals_disabled`；ZBA 管理员 Bearer 已替代旧面板的 `PanelToken` 前置。

### 5.3 会话发现、结果摘要与提交核对（2026-10-20 阶段 6 增量）

- `GET /zba/v1/sessions?sourceKey=panel%3Aworkspace`（read）列出工作台服务器会话：id、显示名、`current`、轮数、
  历史字符数与时间；不返回历史正文。只接受 `panel:` 前缀，其它来源 `403 source_key_not_allowed`；群聊会话不经此接口暴露。
- `POST /zba/v1/sessions/ensure`（admin）取当前工作台会话，没有则建一个；body `{"sourceKey":"panel:workspace"}`，
  返回 `{session, created}`。`created` 只表示本次观察到的当前会话变了，不作强一致承诺；停用服务器 agent 时 409。
- `GET /zba/v1/tasks/{id}/result`（admin）返回结果投影：仅 `panel:` 源成功任务有 `summary`（≤4000 字符、清洗控制字符），
  其余 `available=false`，`reasonCode` 为 `not_terminal`/`not_succeeded`/`scope_not_allowed`/`no_summary`。
- `POST /zba/v1/tasks/find`（admin）body `{"key":"<幂等键>"}`，返回 `found`/`expired`/`accepted_unknown`/`unknown`
  与当前 `instanceId`；`expired`/`accepted_unknown` 只给 `taskId`、`acceptedAt`，不给 prompt 或结果。
- 数据目录新增 `zba-task-receipts.json`：只存幂等键 SHA-256、任务 id、受理实例、时间；最多 512 条 / 7 天、原子替换。
  加载、查询、写入时淘汰过期/超限条目并**压缩磁盘文件**（过期条目不能只从内存消失）；压缩写失败只记日志。
  写失败按 `503 receipt_store_unavailable` 拒绝提交（任务不启动）；读失败/损坏按空回执继续。
- 重启后同键提交一律 `409 idempotency_unverifiable`（`previous_instance` 或 `record_evicted`），不重放、不重做；新动作必须新键。
- `meta` 增加 `instanceId` 与 `features`（`sessions.list`、`sessions.ensure`、`tasks.result`、`tasks.find`、`tasks.receipts`）；契约版本仍 `zba-1.0`。

## 6. 事件恢复

`GET /zba/v1/events?after=<seq>&limit=1..200` 返回 `{items,nextSequence,resyncRequired,generatedAt}`。
事件环 512 条，只含 `task.updated`、`configuration.saved`、`capability.executed`、`approval.decided` 等形状字段；
不含日志正文、聊天内容、prompt 或工具结果。
`after` 早于环内最旧序号、或**超前于当前 `nextSequence`** 时 `resyncRequired=true`，
客户端应重新读 `status`/`tasks`/`approvals` 后再按 `nextSequence` 追；不能把超前游标当成“暂无事件”。
序号高水位持久化在数据目录的 `zba-event-sequence`（只含一个整数），宿主重启后从上次序号继续：
旧游标仍能收到后续事件，环淘汰/超前时明确 resync；不再因新进程序号从 0 重来而静默漏事件。
序号**先落盘、后对外确认**（W5-G1-IO 整改）：序号文件写入失败时该事件不进入环、不分配序号，
持久化高水位保持不动；同时把“缺口下限”写入同目录的 `zba-event-sequence.gap`（整数，值 = 首个未证明落盘的序号）。
进程内 `after < 缺口下限` 的游标一律 `resyncRequired=true`，缺口之后恢复写入的新事件照常按 `+1` 投递。
缺口下限只增不减、不自动清除（客户端 resync 后把游标推进到 `nextSequence` 即可越过它）。
写失败只影响事件投递，已提交的配置仍然有效（不得报成配置回滚）。

**启动屏障与游标推进（W5-G1-IO-R2/R3 整改，负责人已选定）**：
判据不是“高水位 > 0”，而是“**不能证明是首次安装**”——首次事件就写失败时高水位仍是 0，
但配置可能已经提交过，旧游标同样必须 resync。
“首次安装”必须**同时**满足两条：

1. `zba-event-sequence.boot` 在本次启动前不存在，且本次原子写成功；
2. 本次进程初始化写入行为种子**之前**，配置库里没有 settings 行（组合根在 `ConfigurationStore.Activate` 前捕获）。
   激活事务在首次运行时会写入行为种子，因此“启动时该行已存在”能证明上一进程至少完成过一次装配；
   数据库文件本身在首次启动时就会被创建，不能当判据。

只看“标记缺失且现在可写”不够：上一个进程可能连启动标记都没写成就撞上存储故障，
却已经提交过配置（W5-G1-BOOT-LOSS）。恢复后重启若把它当成首次安装，离线客户端的旧游标会被误判为“已追平”。

- **高水位 > 0，或启动标记本次没写成**：按非首次启动处理，服务端在持久化高水位上**预留一个序号**
  （`max(缺口下限, 高水位+1)`，先落盘再对外返回，该序号不产生事件），并让旧游标 `resyncRequired=true`：
  - `nextSequence` 指向这个预留序号；客户端按文档“重读状态后按 `nextSequence` 追”即完成这一次屏障，
    下一次查询同一 `nextSequence` 返回 `resync=false`。每个客户端按自己的游标独立完成，不需要全局清除。
  - 后续真实事件从预留序号 `+1` 开始；客户端只看 `nextSequence` 不会漏事件。
  - 预留写不成（存储不可写）时退化为电平信号：`resync` 保持到下一次成功写入，`nextSequence` 停在旧高水位。
- **高水位 == 0、启动标记本次写成，但初始化前已有 settings 行**：上一个进程运行过且没留下标记，
  而高水位为 0 说明没有任何事件序号被持久化确认过（`> 0` 的游标会被“超前”分支要求 resync）。
  此时不预留不存在的序号：把缺口下限置为 1（落盘 `zba-event-sequence.gap`，写不成则保持电平信号），
  游标 0 一律 `resyncRequired=true`，真实事件仍从 1 开始；客户端拿到首个真实事件后按 `nextSequence`
  推进即完成重读。下一次启动仍由已提交配置重新得出结论。
- **真正首次安装**（启动标记本次写成且初始化前没有 settings 行）不设屏障，首个客户端仍从 0 开始。

代价是正常重启/升级时客户端多读一次状态、服务端多消耗一个序号；
换来的是任何“连缺口标记都没写成”的故障分支都不会再被误判为“已追平”。
缺口标记继续保留，用于同一进程内故障后的即时提示与诊断；跨重启的兜底由启动屏障负责。
缺口标记损坏或读不出时仍按“无法证明连续”处理，旧游标要求 resync。
该持久化只覆盖序号，不保存事件内容；单宿主独占数据目录的前提不变。旧 21:54 EdgeAudit 中“重启后必须 resync”的
两条断言是为 resync-only 最小整改写的，已由 S55 的“旧游标继续收到 last+1 / 环溢出 resync”矩阵取代。
不使用 SSE（本版可选传输未纳入）；断线不会重做工具调用，任务终态可查询。

## 7. 错误与状态码

| 场景 | HTTP |
| --- | --- |
| 未认证 / 错令牌 | 401 |
| 只读令牌写操作 / 非审批人 | 403 |
| 未知路径/任务/审批 | 404 |
| 方法不允许（路径存在） | 405 |
| 版本冲突/幂等冲突/关键代际不符/动作不支持（部分） | 409 |
| 请求键过期 | 410 |
| 输入/未知字段/重复字段/秘密形状 | 400 |
| 动作不支持 | 422 |
| 容量/认证失败限流 | 429 |
| 请求体过大 | 413 |
| 存储/提交/未知提交不可用 | 503 |
| 服务内部 | 500（只返回安全错误码） |

后端业务结果保留在响应 DTO 的 `code`（Core 枚举数值），HTTP 状态只做分类；错误体为 `{"error":{"code","message","reason","backendCode"}}`。

## 8. 证据与限制

- `tests/BotAgent.ZbaProbe`：**152/0 ×3**；认证/授权/限流、闭包 JSON（含大小写别名/嵌套重复）、secrets 空元素、秘密不回显、版本/幂等映射、任务/审批/事件、真实 `HttpListener` 冒烟。
- `tests/BotAgent.IntegrationHarness` `s54`：**57/0 ×3**；真实宿主 Bearer、能力目录、配置读/写/回滚、需重启字段回执与事件 state、超前游标 resync、secrets 空元素 400 且版本不变、capabilities/validate、插件 UnsupportedAction、任务提交/完成/幂等/取消、事件恢复、CLI、静态资源、Issue #78 回归。
- `tests/BotAgent.IntegrationHarness` `s55`：**17/0**；真实宿主 stop/restart 后旧游标 == 高水位、空环旧游标/零游标 resync、收到 last+1、超前/旧于高水位 resync、损坏序号文件开新代际并记日志，以及 512 环溢出/环内游标不误报。
- `BotAgent.ArchitectureProbe` **171/0**（Panel 只引用 Core；Headless 直接引用 Panel；Panel 文件 IO 为 0；最长方法 198）。
- Agent-01 首轮 NotAccepted（H-1/M-1）；整改后 2026-10-18 21:45 addendum **Accepted**（历史范围，已被 21:54 终验覆盖；独立 A-H **88/0**、S54 **49/0**、17 定向 **571/0**）。
- 2026-10-18 21:54 Agent-01 终验再次 **NotAccepted**：W5-M2（大小写别名重复输入）、W5-M3（需重启保存事件冒报 applied）、W5-M4（重启后旧游标静默漏事件）；原 H-1/M-1 整改仍通过。
  整改后本地 EdgeAudit **12/12 ×3**（fix1–fix3）；独立复验 EdgeAudit **12/12 ×6**（reaccept1–3 + clean1–3），均含真实 stop/restart。ZbaProbe **146/0 ×3**、S54 **54/0 ×2**、Architecture **171/0**；
  2026-10-18 22:18–22:42 独立复验 **Accepted**（历史范围）：W5-M2/M3/M4 闭合，无新 High/Medium；17 定向 **571/0** + S34 **20/0**、独立宿主 **79/0**、回归 **13/13**、74 条命令 exit 0。
- 2026-10-18 22:49–23:01 Agent-01 第三轮验收再次 **NotAccepted**：W5-M5，`secrets=[null]` 在 `configuration/save` 与 `capabilities/validate` 均触发空引用并返回 500，而非契约要求的 400；三次合成宿主一致。
  整改：`ZbaRules.ValidateSecrets` 在成员访问前拒绝 null 元素（`400 invalid_json`）。ZbaProbe **152/0 ×3**、S54 **57/0 ×3**、Agent-01 null 夹具 **7/0 ×3**、19 定向 **648/0**、Architecture **171/0**。
- 2026-10-19 11:02–11:39 Agent-01 独立复验 **Accepted**：W5-M5 闭合，无新 High/Medium；唯一产品改动为一行空值检查，无 DTO/OpenAPI 扩张；真实宿主秘密生命周期 **24/0**。19 定向中 Windows 18 场景 **624/0**，S38 因本机代理占用固定端口 13071 在 Windows 未能启动（三次环境失败保留），WSL 独立命名空间 **24/0**。
- 残余 Low：取消/超时回调先完成等待者、后设置 `task.CancelRequested` 的极窄顺序窗口；显式取消由 `record.Cts.IsCancellationRequested` 兜底，本轮未复现。
- W5-G1 状态：已改为数据目录持久化序号高水位（`ZbaEventSequenceStore`），空环旧游标明确 resync、损坏文件开新代际并记日志；真实重启审计 **10/0 ×3**、S55 **17/0 ×3**、gap 探针改写后 **11/0**、corrupt 探针改写后 **14/0**。**独立复验 Accepted**；不改变“任务台账本身在内存、重启后旧任务不可查询”的边界。
- 2026-10-19 W5-G1-BOOT-LOSS 整改（只读验收 High 1）：首次启动判定改为“启动标记本次写成 + 初始化前没有 settings 行”；三元元数据（序号/缺口/启动标记）全部写失败但配置已提交的恢复分支不再误判首次启动。公共 S55 **60/0 ×3**（新增健康首次安装、正常重启多客户端、全部恢复/延迟恢复、反射等价负例），验收方原负例 `boot_marker_fault.py` **144/0**（原 135/9）；全量集成矩阵 **1129/0**，其余专项全绿。未改 DTO/OpenAPI，IO 棘轮仍 61。证据目录 `.git/w5-g1-boot-fix-20261019/`。
- W5-G3 状态：最终 G4 产物 64 文件、Panel `E82E979C…923FAA`、Headless `6A5DDD70…2FD89D`，Wave4 基线 55 文件 → 候选 → 回退 → 破坏 Panel → 无 wwwroot → 恢复，`pass=True`；合成 DB `user_version=9`、WAL、61 schema 对象。**独立复验 Accepted**。
- W5-G2 状态：最终产物 1000 health/16 + 300 status/8，启动 943.6 ms、空闲 56.96 MiB、峰值 88.07 MiB、p95 9.519/17.558 ms、0 失败、GC Heap 峰值 2.55 MB；**候选预算已获负责人 2026-10-19 确认，重测在预算内**。
- W5-G4 状态：18 条既有失败已全部修复并经独立复验 **Accepted**。真实功能回归集中在 `ChannelRouter` 迁移后仍按 `OneBotGateway` 判定能力：群历史补录、账号在线探测、登录号确认被静默跳过，现统一从私域通道取；其余为诊断日志可读性（S1/S31/S32）、掩码安全收紧后的旧断言（S37）、环境变量只作首次种子（S49）与后台探测引入的动作计数敏感（S48）。定向 9 场景 **119/0**（原 18 条对应 8 场景 109/0 + S48 10/0），全量 **1086/0**（含 S38），架构 **171/0**、ZbaProbe **152/0**、Safety **594/0**、Review **1140/0**、Frontend **370/0**。
- 2026-10-20 阶段 6 S0 增量（G01–G03，工作区未提交）：ZbaProbe **194/0**、S54 **85/0**、S55 **72/0**、ArchitectureProbe **172/0**（IO 棘轮仍 61）。真实宿主覆盖 `sessions.ensure` 幂等、仅 `panel:` 来源、panel 结果摘要与非 panel 拒绝、失败任务只回安全错误码、5000 字答复经 HTTP 截断、`tasks/find` 的 `found`/`expired`/`accepted_unknown`/`unknown`；新增真宿主 200 条容量用例：填满 200 条后第 201 条继续受理，最旧记录被淘汰后 `find` 返回 `expired`、同键重放 409 `record_evicted` 且不重复执行。另有重启后同键重放 409 `previous_instance`、回执文件损坏降级为 `unknown`、8 天前回执在启动时从磁盘清理。证据目录 `.git/frontend-wave6-s0-20261020-1845/`、`.git/g01-g03-reacceptance-remediation-20261020-1928/`。
- 2026-10-20 19:35 / 19:42 独立验收：G01–G03 在 Windows 合成宿主与本地 Docker Desktop Linux/amd64 均 **Accepted**（S54 85/0、S55 72/0、ZbaProbe 194/0、Architecture 172/0；Windows 另有 Frontend 341/0、boundary/retention 7/7+7/7）。证据 `.git/g01-g03-reacceptance-20261020-1935/acceptance-report.md`、`.git/g01-g03-linux-acceptance-20261020-1942/acceptance-report.md`。S0 收口候选见 `.git/frontend-wave6-s0-20261020-1845/s0-closure-20261020-1948.md`；**不构成 S1 开工或发布批准**。
- 完整回归矩阵与资源/回退证据见执行总纲 §27.10 与 `.git/agent00-wave5-g1-20261019/`；G4 整改证据在 `.git/agent00-wave5-g4-20261019/`。
- 限制：任务台账与结果摘要在内存；回执文件只存哈希与时间（不是任务台账）；无 SSE；无 MCP 管理页/Docker 高权限；OpenAPI 不替代深度 JSON Schema 校验；
  未验证真实第三方客户端、生产 Linux/容器资源、浏览器 E2E、生产部署或真实 MCP 管理。
- 本交接包不构成 Web 开工或发布批准；S0 收口候选仍需负责人签署，S1 未开工，`src/web` 未创建，Wave6 发布/生产切换另行批准。

### 2026-10-21 S2-2 验收整改（M1/M2/M3/L1/L2/L3）

- 验收基线：`.git/s2-acceptance-20261021-1505/acceptance-report.md`（S2-1 Accepted；S2-2 NotAccepted：M1/M2 Medium，M3 Low 阻断 V2；L1/L2/L3 非阻断）。
- M1 草稿生命周期：配置页改为 `v-if` 首次挂载 + `v-show` 保活，切页不再销毁草稿，侧栏“配置”显示未保存项数角标；刷新/关页触发 `beforeunload`；锁定与退出管理员模式在 dirty 时弹“保存并锁定 / 继续编辑 / 放弃并锁定”。切页按需求允许的 keep-alive 方案保留而非拦截，不再静默丢弃。
- M2 未确认/操作键：保存进入 `unconfirmed` 后冻结字段、校验、保存、放弃与锁定；仅传输失败或 2xx 无法解码保留 `cfg1:` 键；`find` 回执 → 清键并重载，NotFound/代际不匹配/键过期(5) → 清键并要求重新保存；任何结构化响应立即清键，编辑后保存必用新键。
- M3：dist 中阶段字样改为“即将推出”“只读字段”（`effectLabels[3]`）。
- L1：新增两个浏览器上下文并发保存 e2e（第二个 409、两侧草稿均保留）。
- L2：新增 134 项字段标签字典（`field-labels.ts`），wire key 降为副标题、窄屏隐藏；旧面板可抽取 getter 130 个（54 个无显式 label），与 134 项目录无法自动一一比对，故不宣称 134/134 覆盖（`verify-labels.mjs` 可复算：旧有新无 5 键 = 4 个秘密槽位 + `mood`）。
- L3：`status.json` 更新候选指纹、交付清单（新增 7/修改 7）、验证数字与 S1 门禁状态。
- 验证：typecheck 0；unit **86/86**（10 文件）；真实双宿主 + preview e2e **24/24**（1.0m，配置 10 / 概览 13 / 限流 1）；build JS 135,483 B / CSS 22,891 B；dist `748DE0F4…` / `06D2E4D3…` / `4A3179E9…`，favicon `6E348763…`；Headless DLL `895081E3…` 未变；`src/web` 56 文件；工作树 332/672。
- 证据：`.git/s2-fix-20261021-1550/fix-report.md`、`typecheck-final.log`、`unit-final.log`、`e2e-final4.log`、`web-hashes-after.txt`、`visual/capture3.log` 与 1280/390 截图。
- 边界：未提交/未推送；S2-3 未启动；V2 视觉未签；`src/web` 仍为未跟踪目录。

### 2026-10-19 Agent-00 整改补记

本轮在 W5-M5 之后完成 W5-G1/G3 整改与独立复验、W5-G2 最终候选补测、W5-G4 失败归因与全部修复：G1 新增持久化序号高水位（不扩 DTO/OpenAPI），并修复空环旧游标/损坏文件边界，S55 **17/0 ×3**、真实重启审计 **10/0 ×3**、**独立复验 Accepted**；G3 最终候选 64 文件回退 drill `pass=true` 并绑定源码/程序集/deps/DB schema，**独立复验 Accepted**；G2 最终候选 1000/300 负载 p95 7.08/21.19 ms、GC Heap 峰值 14.26 MB，**预算获负责人确认**；G4 的 18 条既有失败已全部修复（S12/S16/S35 为路由迁移后的真实能力回归，S1/S31/S32 为诊断日志，S37/S49/S48 为测试对齐），8 场景定向 **109/0**、全量 **1086/0**（含 S38）。2026-10-19 14:15–14:35 Agent-01 **Wave5 完整准出独立复验 Accepted**（无 High/Medium；G3/G2 用最终 G4 产物重新绑定/重测）；Wave5 到达 Wave6 入口，Wave6 未启动。


