# Agent Runtime 部署、验证与回滚（#119）

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
| 新版本 | `PRAGMA user_version = 12` |
| 新增表 | `agent_tasks`（一行一个任务，**只存形状与计数**，不落提示词/模型输出/结果正文） |
| 新增索引 | `idx_agent_tasks_updated(updated_at)` |
| 版本闸 | 宿主启动时读 `user_version`：**大于 12 直接拒绝启动**（防止旧二进制读到新库后写坏） |

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

- **status 是整数**（0/1/2/3/4/6/7），不是字符串；`2 = WaitingApproval` 目前没有写入方（见 #137）。
- 终态之后再次 `POST /zba/v1/tasks/{id}/cancel` 会被拒绝（不能把已经完成的任务改成取消）。
- `GET /zba/v1/tasks/{id}/result` 只对 `panel:` 源的成功任务返回有界摘要（≤4000 字，已清洗控制字符），其余源返回空/受限。
- 超时：`timeoutSeconds` 会被钳到 **10–900** 秒；超时终态是 `Failed` + `errorCode=timeout`（#114 起的唯一可观察行为变化，见 [ADR-0002](../decisions/0002-agent-task-runtime.md)）。

### 2.3 重启后还在不在（这条最容易误判）

重启**不会重做**任何任务。恢复策略是「收口」：

| 重启前状态 | 重启后 | 原因码 |
| --- | --- | --- |
| `Queued` / `WaitingApproval`（从未开始） | `Cancelled` | `cancelled_before_dispatch` |
| `Running` / `CancelRequested`（可能已在远端产生副作用） | `Unknown` | `restart_state_unknown` |
| 终态（`Succeeded` / `Failed` / `Cancelled` / `Unknown`） | 保持不变 | — |

自查：提交一个长任务 → 重启宿主 → `GET /zba/v1/tasks/{id}` 应返回 **200**（不是 404），且状态落到上表；
`GET /zba/v1/tasks/{id}/result` 对 `Unknown` 不返回正文。

---

## 3. 回滚

### 3.1 不可逆边界（先看清再动手）

| 项 | 事实 | 回滚限制 |
| --- | --- | --- |
| schema 版本 | 只前向：11 → 12 自动迁移，**没有 down migration** | 回退到 11 只能靠**恢复升级前的库快照** |
| 旧二进制读新库 | `user_version > 12` 会拒绝启动（版本闸）；`= 12` 时旧二进制能启动，但**不认识** `agent_tasks` | 旧二进制会忽略 `agent_tasks`，任务历史在回滚后不可见（表仍在，不丢数据） |
| 已写入的台账行 | 只增不减（除启动 Prune 到 256 行） | 回滚不清理；若要彻底复原，用快照覆盖 |
| 任务结果摘要 | 只存 `panel:` 源、≤4000 字、不落正文到日志 | 回滚后无法从事件流重建正文（本来就不保存） |

### 3.2 回滚顺序（与 [回滚预案](rollback-playbook.md) 一致）

1. **先停写**：停宿主（`SIGTERM` / `docker stop`），等它优雅退出；确认没有残留写入者；协议端断开或确保不会在此期间重投事件。
2. **取快照**（若升级前没取）：`VACUUM INTO` 到临时文件，校验能被 `sqlite3` 打开并读到 `user_version`。
3. **换回旧二进制 / 旧镜像**（`docker tag` 保留的上一版，或旧 `bin/Release/<tfm>/`）。
4. **恢复库**（仅在需要真的退回 v11 时）：用升级前快照覆盖 `data/botagent.db`，并删掉同目录的 `botagent.db-wal` / `-shm`。
5. **起进程并验证**：`/healthz` 200、`/readyz` 语义正常、面板能打开、`/zba/v1/tasks` 按旧行为返回。

> ⚠ 只换二进制、不恢复库，是**最常见也最安全**的回滚形态（旧二进制读 v12 库没问题）；
> 只有当你必须让库也退回 v11（例如新版本写入了旧版本解释不了的数据）时，才做第 4 步。

### 3.3 回滚后要确认的三件事

- `PRAGMA user_version` 是否如预期（退回形态应是 11；只换二进制则仍是 12）；
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
- [ ] 新版本启动日志里没有 schema 版本闸报错；`PRAGMA user_version` = 12；
- [ ] `agent_tasks` 表存在（`SELECT COUNT(*)` 能跑通）；
- [ ] `/healthz` 200 且 `version` 正确；`/zba/v1/tasks` 200（或按预期 503）；
- [ ] 提交一个合成任务并看到 `queued → running → 终态`；`cancel` 在终态后被拒绝；
- [ ] 重启一次，确认旧任务按上表收口、不重做；
- [ ] 回滚预案的停写与恢复步骤在本次变更中被重新走过一遍（至少演练一次）。

---

## 6. 相关文档

- 设计决策：[ADR-0002 任务与执行状态模型](../decisions/0002-agent-task-runtime.md)、[ADR-0003 任务台账持久化](../decisions/0003-agent-task-persistence.md)
- 实现事实与已知边界：[Agent Runtime](../architecture/agent-task-runtime.md)
- 备份 / 停写 / 回滚顺序：[回滚预案](rollback-playbook.md)、[日志保留、每日热备与恢复演练](data-retention-and-backup.md)
- 审批与执行隔离：[审批与执行隔离](../architecture/approval-and-isolation.md)
- 版本与发版流程：[发布检查单](release-checklist.md)
