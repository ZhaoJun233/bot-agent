# Agent Runtime 部署、验证与回滚（#119 / #137）

适用于按 #114 / #116 引入的 **Agent Runtime**（任务状态机 + 任务台账）。目标：一个没参与开发的人，照着这一页能把
Runtime 上线、确认它真的在工作、并在出问题时把它撤掉或回退到旧二进制。

> 本文所有示例都用合成数据（`zj-` 前缀的任务 id、假模型地址、临时目录）。
> 禁止把真实群号、会话 key、提示词或模型凭据贴进任务提交体与日志。

---

## 1. 部署

### 1.1 数据库 schema（11 → 12，**只前向**）

| 项 | 值 |
| --- | --- |
| 旧版本 | `PRAGMA user_version = 11` |
| 新版本 | `PRAGMA user_version = 13`（#119 交付时是 12；#135 加了轨迹归档的缓存三列后变成 13） |
| 新增表 | `agent_tasks`（一行一个任务，**只存形状与计数**，不落提示词/模型输出/结果正文） |
| 新增索引 | `idx_agent_tasks_updated(updated_at)` |
| 版本闸 | 宿主启动时读 `user_version`：**大于 13 直接拒绝启动**（防止旧二进制读到新库后写坏） |

升级是**自动**的：新二进制首次启动时 `AppDatabase` 会把版本推到 12 并建表，不需要手工执行 SQL。
但**升级前必须先按 [回滚预案](rollback-playbook.md) 第 1 节备好快照** —— 这一步是回滚的前置条件。

### 1.2 配置项

Runtime 本身**没有新增环境变量**，沿用现有的 ZBA 契约：

| 变量 | 作用 | 不配时 |
| --- | --- | --- |
| `BOTAGENT_AGENT_TOKEN`（兼容 `QQCHAT_AGENT_TOKEN`） | ZBA `/zba/*` 令牌，任务端点用它的 `Read` / `Admin` 两个作用域 | 除 `/zba/v1/health` 外全部 **503**（默认失败关闭） |

`agent_tasks` 的容量与保留策略是**代码常量**，不是配置：

- 台账保留 **256** 行（`AgentTaskLedgerStore.Retain`），启动时 `Prune` 到该上限；
- 启动恢复最多读 **200** 行（`AgentTaskRuntime.MaxRestore`）；
- 通道侧并发上限 **4**、台账上限 **200** 条（`ZbaTaskRegistry.MaxActive` / `MaxRecords`）—— 超了返回 `429 task_capacity_exceeded`。

### 1.3 容器与镜像

- **不新增依赖、不新增卷**：`agent_tasks` 就在现有的 `data/botagent.db` 里，`data/` 卷照旧；
- 不新增端口，不新增进程，不需要额外的 sidecar；
- 镜像与 .NET 版本要求与 #105 验收时的基线一致（`.NET 10`；`Dockerfile` 里的 `DOTNET_VERSION` 跟 `global.json` 对齐）；
- 若走「解压产物」路线（AWS 那台 ~1GB 内存的机器），照旧用仓库根的 `Dockerfile.app`，**不要**用 `src/BotAgent.Headless/Dockerfile`。

---

## 2. 验证（Runtime 真的生效了吗）

### 2.1 三个最省事的观察点

1. **健康检查**：`GET /healthz` 返回 200，`version` 与你要部署的版本一致；`GET /readyz` 在协议端未连上时是 503，这是正常语义（不是 Runtime 的问题）。
2. **启动日志里有台账恢复行**（有历史任务时）：
   > `[Task] 恢复 N 条历史任务（其中 M 条按 Unknown 收口，不重做）`
   没有历史任务时这行不出现，属正常。
3. **任务列表接口**：`GET /zba/v1/tasks?limit=10` 用 `BOTAGENT_AGENT_TOKEN` 取，返回 200 与 `items: []`（新装）或历史任务摘要。

### 2.2 走一遍提交 → 运行 → 终态（合成任务）

```sh
# 1) 提交（幂等键必填；重放同一个键会返回同一条任务）
curl -sS -X POST "$BASE/zba/v1/tasks" \
  -H "Authorization: Bearer $ZBA_TOKEN" -H "Content-Type: application/json" \
  -d '{"prompt":"synthetic: 只回一句 ok","idempotencyKey":"zj-deploy-check-1","timeoutSeconds":60}'

# 2) 看状态：queued(0) → running(1) → succeeded(3) / failed(4) / cancelled(6)
curl -sS "$BASE/zba/v1/tasks/zj-deploy-check-1" -H "Authorization: Bearer $ZBA_TOKEN"
```

要点：

- **status 是整数**（0/1/2/3/4/6/7），不是字符串；`2 = WaitingApproval`（#137 起：任务声明了 `bash` / `write` / `docker`
  且审批总开关打开时，受理即停在这个状态，**执行器一次都不会被调用**）。
- 终态之后再次 `POST /zba/v1/tasks/{id}/cancel` 会被拒绝（不能把已经完成的任务改成取消）。
- `GET /zba/v1/tasks/{id}/result` 只对 `panel:` 源的成功任务返回有界摘要（≤4000 字，已清洗控制字符），其余源返回空/受限。
- 超时：`timeoutSeconds` 会被钳到 **10–900** 秒；超时终态是 `Failed` + `errorCode=timeout`（#114 起的唯一可观察行为变化，见 [ADR-0002](../decisions/0002-agent-task-runtime.md)）。

### 2.3 重启后还在不在（这条最容易误判）

重启**不会重做**任何任务。恢复策略是「收口」：

| 重启前状态 | 重启后 | 原因码 |
| --- | --- | --- |
| `Queued`（从未开始） | `Cancelled` | `cancelled_before_dispatch` |
| `WaitingApproval`（等审批，见 #137） | `Cancelled` | `approval_lost_on_restart` —— 待批单只活在进程内，重启后没有可批的对象，不假装还能批 |
| `Running` / `CancelRequested`（可能已在远端产生副作用） | `Unknown` | `restart_state_unknown` |
| 终态（`Succeeded` / `Failed` / `Cancelled` / `Unknown`） | 保持不变 | — |

自查：提交一个长任务 → 重启宿主 → `GET /zba/v1/tasks/{id}` 应返回 **200**（不是 404），且状态落到上表；
`GET /zba/v1/tasks/{id}/result` 对 `Unknown` 不返回正文。

### 2.4 审批闭环（#137）

只有「任务有效工具作用域含高风险工具 + 审批总开关打开」时才需要走这一段。省略或空 `toolScope` 会继承配置白名单，并不代表无工具；显式只读作用域不包含高危工具时才可免审批。
此处仅指 `POST /zba/v1/tasks`；普通 `//` 与旧 `POST /api/agent` 尚未接入任务级审批，仍走显式放行策略。

`@sh
# 1) 打开审批总开关（面板设置；关着时任务根本不会停在等待审批）
curl -sS -X POST "$PANEL/api/settings" -H "Content-Type: application/json" \
  -d '{"enableApprovals":true}'

# 2) 提交一个声明了高风险工具的任务 → 期望 state=2（WaitingApproval），且模型侧一次调用都没有
curl -sS -X POST "$BASE/zba/v1/tasks" \
  -H "Authorization: Bearer $ZBA_TOKEN" -H "Content-Type: application/json" \
  -d '{"sourceKey":"panel:deploy-check","sessionId":"deploy-check","prompt":"synthetic: 审批闭环","target":"server","timeoutSeconds":60,"toolScope":["bash"],"idempotencyKey":"zj-approval-1"}'

# 3) 待批单会出现在审批列表里，session 是 task:<任务号>
curl -sS "$BASE/zba/v1/approvals" -H "Authorization: Bearer $ZBA_TOKEN"

# 4) 批准 → 任务从同一条记录继续（不重发、不新建），跑完后结果回原会话
curl -sS -X POST "$BASE/zba/v1/approvals/<requestId>/decide" \
  -H "Authorization: Bearer $ZBA_TOKEN" -H "Content-Type: application/json" -d '{"approve":true}'
`@

自查要点：

| 看什么 | 期望 |
| --- | --- |
| 批之前 | `state=2`；等待 ≥1 秒后仍是 2，且模型请求计数不变（**零副作用**，执行器一次都没被调用） |
| 待批单 | `GET /zba/v1/approvals` 里 `toolId=bash`、`session=task:<任务号>`、`expiresAt` ≈ 提交时刻 +120 秒 |
| 批准 | 回执 `decision=approve`；任务随后跑到 `succeeded(3)`；`GET /tasks/{id}/result` 对 `panel:` 源返回摘要 |
| 拒绝 | 终态 `cancelled(6)` + `errorCode=approval_rejected` |
| 超期 | 不决策、等过 `expiresAt` 后再查 → `cancelled(6)` + `errorCode=approval_expired`（`Get` / `List` / 决策 / `Counts` / `Submit` 都会先扫，无需先查列表才能释放容量） |
| 取消 | 待批时 `POST /tasks/{id}/cancel` → `cancelled(6)` + `errorCode=approval_cancelled`（此前这条边不存在） |
| 重启 | 待批中重启宿主 → `cancelled(6)` + `errorCode=approval_lost_on_restart`（待批单不落库，没有可批的对象） |
| 手工造票 | 自己拼一张结构相同的 `ApprovalTicket` 过闸门 → 拒绝，原因码 `approval_ticket_unverified`（票据带进程内 HMAC 签名，重启后旧票自动失效） |

**已知粒度**：审批是**任务级不是工具级** —— 有效作用域包含 `bash` 就整条先批，哪怕最后一步都没用到 bash。
这是刻意的 fail-closed 取舍；没有多级审批，也不做跨重启恢复待批。细节见
[Agent Runtime §7](../architecture/agent-task-runtime.md) 与 [审批与执行隔离](../architecture/approval-and-isolation.md)。

---

## 3. 回滚

### 3.1 不可逆边界（先看清再动手）

| 项 | 事实 | 回滚限制 |
| --- | --- | --- |
| schema 版本 | 只前向：11 → 12 自动迁移，**没有 down migration** | 回退到 11 只能靠**恢复升级前的库快照** |
| 旧二进制读新库 | #119 时代的二进制闸是 `> 12`，遇到 v13 库直接拒绝启动；同一代二进制读 `= 12` 的库能启动但**不认识** `agent_tasks` | 回滚前必须先恢复快照（库回到旧版本）再退二进制；表里的数据不会丢 |
| 已写入的台账行 | 只增不减（除启动 Prune 到 256 行） | 回滚不清理；若要彻底复原，用快照覆盖 |
| 待批单（#137） | **不落库**：`agent_tasks` 表结构一个字节没动，审批字段只在内存快照里 | 回滚期间停在 `WaitingApproval` 的任务，旧二进制会收口成 `Cancelled` + `cancelled_before_dispatch`（新二进制是 `approval_lost_on_restart`）—— 两者都是「不执行」，只是原因码不同 |
| 任务结果摘要 | 只存 `panel:` 源、≤4000 字、不落正文到日志 | 回滚后无法从事件流重建正文（本来就不保存） |

### 3.2 回滚顺序（与 [回滚预案](rollback-playbook.md) 一致）

1. **先停写**：停宿主（`SIGTERM` / `docker stop`），等它优雅退出；确认没有残留写入者；协议端断开或确保不会在此期间重投事件。
2. **取快照**（若升级前没取）：`VACUUM INTO` 到临时文件，校验能被 `sqlite3` 打开并读到 `user_version`。
3. **换回旧二进制 / 旧镜像**（`docker tag` 保留的上一版，或旧 `bin/Release/<tfm>/`）。
4. **恢复库**（仅在需要真的退回 v11 时）：用升级前快照覆盖 `data/botagent.db`，并删掉同目录的 `botagent.db-wal` / `-shm`。
5. **起进程并验证**：`/healthz` 200、`/readyz` 语义正常、面板能打开、`/zba/v1/tasks` 按旧行为返回。

> ⚠ 只换二进制、不恢复库，只在**库版本没被新版本推进**时成立（同一 schema 版本内）。schema 已经 +1 之后，旧二进制会被版本闸拒绝启动 —— 先恢复快照再退二进制。
> 只有当你必须让库也退回 v11（例如新版本写入了旧版本解释不了的数据）时，才做第 4 步。

### 3.3 回滚后要确认的三件事

- `PRAGMA user_version` 是否如预期（退回形态应回到回滚点的版本；只换二进制则仍是原版本）；
- `agent_tasks` 的行数没有变化（只换二进制时）；
- 重启后的恢复日志**不再出现**（旧二进制没有这个功能）是正常的，不是故障。

---

## 4. 运维观测点（只写形状与计数）

| 观测点 | 位置 | 看什么 |
| --- | --- | --- |
| 启动恢复 | 宿主日志，`[Task]` 前缀 | 「恢复 N 条历史任务（其中 M 条按 Unknown 收口）」—— 只关心 N/M 两个计数 |
| 台账写入失败 | 宿主日志，`[Task] 台账写入失败（任务状态不受影响）` | 出现即说明 SQLite 写不进去（磁盘/权限/锁），任务本身仍在内存里跑 |
| 任务状态分布 | `SELECT state, COUNT(*) FROM agent_tasks GROUP BY state` | **只取计数**，不要 `SELECT *`（表里没有正文，但仍遵守「只看形状」的纪律） |
| 台账行数 | `SELECT COUNT(*) FROM agent_tasks` | 稳态应 ≤ 256；持续增长说明 Prune 没跑到（只在启动时裁） |
| 任务接口 | `GET /zba/v1/tasks?limit=1` | 200 + 列表形状正常；503 = 没配 ZBA 令牌（失败关闭） |
| 事件流 | `GET /zba/v1/events` | `task.updated` 的 `state` 与 `reasonCode`；只留状态与原因，不含提示词 |
| 指标 | `GET /metrics` | 现有 trace/用量指标口径不变；本条目**不新增**指标 |

**不要做**：把 `agent_tasks` 全表导出贴到 issue；把 `/zba/v1/tasks/{id}/result` 的正文贴出来（那是模型输出）。

---

## 5. 上线检查单（照抄）

- [ ] 升级前快照已就位（`VACUUM INTO`，可被 `sqlite3` 打开），并记下 `user_version=11`；
- [ ] 旧二进制 / 旧镜像 tag 已保留（`qqchat-agent:prev` 之类）；
- [ ] 新版本启动日志里没有 schema 版本闸报错；`PRAGMA user_version` = 13；
- [ ] `agent_tasks` 表存在（`SELECT COUNT(*)` 能跑通）；
- [ ] `/healthz` 200 且 `version` 正确；`/zba/v1/tasks` 200（或按预期 503）；
- [ ] 提交一个合成任务并看到 `queued → running → 终态`；`cancel` 在终态后被拒绝；
- [ ] 重启一次，确认旧任务按上表收口、不重做（含待批任务 → `approval_lost_on_restart`）；
- [ ] 走一遍 §2.4 的审批闭环：批之前模型零调用 → 批准后跑完 → 拒绝/超期各留一个原因码；跑完把 `enableApprovals` 复原成上线前取值；
- [ ] 回滚预案的停写与恢复步骤在本次变更中被重新走过一遍（至少演练一次）。

---

## 6. 相关文档

- 设计决策：[ADR-0002 任务与执行状态模型](../decisions/0002-agent-task-runtime.md)、[ADR-0003 任务台账持久化](../decisions/0003-agent-task-persistence.md)
- 实现事实与已知边界：[Agent Runtime](../architecture/agent-task-runtime.md)
- 备份 / 停写 / 回滚顺序：[回滚预案](rollback-playbook.md)、[日志保留、每日热备与恢复演练](data-retention-and-backup.md)
- 审批与执行隔离：[审批与执行隔离](../architecture/approval-and-isolation.md)
- 版本与发版流程：[发布检查单](release-checklist.md)
