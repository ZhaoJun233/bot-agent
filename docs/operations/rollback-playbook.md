# 回滚预案（#101）

适用于**升级、迁移、重构上线**之后出现故障时的处置。原则只有一条：**先恢复服务，再查原因**。
本文件给出备份清单、回滚顺序、停写前置条件，并记录一次在隔离环境中的实测演练结果。

> 演练只用合成数据与临时目录，未接触任何生产库或真实会话。

---

## 1. 备份清单（升级前必须全部就位）

| # | 对象 | 位置 | 方式 | 校验 |
| --- | --- | --- | --- | --- |
| 1 | SQLite 主库 | `<RuntimeRoot>/data/botagent.db`（旧库 `qqchat.db` 自动回退） | `VACUUM INTO` 在线快照，**不要**直接 `cp` 正在写的库 | 快照文件能被 `sqlite3` 打开并读到 `PRAGMA user_version` |
| 2 | 运行时配置 | `<RuntimeRoot>/data/settings.json` | 复制 | 文件可被 JSON 解析 |
| 3 | 飞书身份映射（启用飞书时） | `<RuntimeRoot>/data/feishu-ids-v2.json` | 复制 | 文件存在即可 |
| 4 | 插件目录 | `<RuntimeRoot>/data/plugins/` | 整目录打包 | 包内 `manifest.json` 数量与线上一致 |
| 5 | 构建产物 | 旧版本 `bin/Release/<tfm>/` 全目录，或旧镜像 tag | 保留不删 | `BotAgent.Headless.dll` 可执行 |
| 6 | 代码锚点 | 升级前的提交 SHA | 记录到发布记录 | `git cat-file -t <sha>` 返回 commit |
| 7 | 容器镜像（若走容器） | `qqchat-agent:prev` 之类的本地 tag | `docker tag` 保留上一版 | `docker image inspect` 可读 |

**权限要求**：备份文件与目录必须收紧到仅所有者可读写（POSIX `600` / 目录 `700`）。
数据库当前实测权限是 `644`，且备份路径不会自动收紧 —— 备份完请手动 `chmod`，或按 #121 的运维基线落地后依赖自动收紧。

---

## 2. 停写前置条件

回滚**必须先停写**，否则回滚后的库与运行中的进程会互相踩：

1. 停宿主进程（`Ctrl+C` / `SIGTERM` / `docker stop`），等它优雅退出（会落盘）。
2. 确认没有残留进程占用数据库：WAL 模式下 `botagent.db-wal` / `-shm` 存在是正常的，但**不应再有活跃写入者**。
3. 确认协议端（NapCat 等）已断开或至少不会在此期间重投大量事件。
4. 只有在上述三步完成后，才允许覆盖数据库或替换二进制。

**同一 QQ 号不能两个协议端同时在线**：切机或重启前，先停一台的协议端，再起另一台。

---

## 3. 回滚顺序

按"依赖从外到内"的顺序回退，每步都可独立验证：

```
① 停宿主（停写）
② 恢复数据库（用快照覆盖）           → 验证：能打开、user_version 与备份一致
③ 恢复运行时配置 / 飞书映射 / 插件目录 → 验证：JSON 可解析、插件清单数量一致
④ 恢复二进制（旧构建产物或旧镜像 tag） → 验证：--version / --health 正常
⑤ 起宿主                              → 验证：/healthz 200、/readyz 200（已连协议端）
⑥ 观察                                → 验证：面板可登录、消息收发正常、无 ERROR 日志
```

要点：

- **数据库先于二进制**。新版本可能已写入新列或新表，先回退二进制会让旧代码读到不认识的 schema（见 §5 的 `database_schema_newer`）。
- **二进制回滚不等于 schema 回滚**。schema 迁移是**只前向**的，没有 down 迁移；回滚的实质是"用快照把库退回旧版本"。因此**快照是唯一回滚手段**。
- **容器路线**：优先回退到 `prev` 镜像 tag，再按上面顺序恢复挂载的数据卷。
- 回滚后**不要**立刻删备份：确认稳定运行至少一个完整的观察窗口再清理。

---

## 4. 隔离环境演练记录（2026-10-31 实测）

在独立临时目录、合成令牌与合成 QQ 号下跑通五步，验证预案本身可执行：

| 步骤 | 动作 | 实测结果 |
| --- | --- | --- |
| D1 | 空数据目录启动宿主 | `/healthz` 200；`/readyz` 503（未连协议端，符合语义）；`/status` 返回 `{"error":"unauthorized","mustChangePassword":false}`；产出 `botagent.db` 4096 B + `-shm` 32 768 B + `-wal` 646 872 B + `plugins/` + `zba-event-sequence.boot` |
| D2 | `VACUUM INTO` 快照 | 成功；源库 `user_version`=10、表 30 张（业务表 29 + 1 张 sqlite 内部表）；快照库同为 10/30；文件 274 432 B |
| D3 | 人为把 `user_version` 改成 11 后重启 | 宿主**拒绝启动**，日志首行 `Unhandled exception. System.InvalidOperationException: database_schema_newer` |
| D4 | 用 D2 快照覆盖后重启 | `/healthz` 200；`user_version` 回到 10、表数 30 —— **快照确实能回滚** |
| D5 | 检查数据库文件权限 | `644` —— `AppDatabase` 自身不 chmod，只有 `SecretsStore` 会收紧 |

结论：预案的 ②③④⑤ 步在隔离环境可执行且可验证；D3 顺带证明"新库配旧二进制"会被明确拒绝，
不会静默跑坏数据 —— 这正是必须"先恢复数据库"的原因。

### 4.1 跨版本演练（.NET 10 → .NET 8 → .NET 10，#105）

上面 D1–D5 是**同版本内**的回滚演练。#105 又在同一套脚本上做了一次**真跨版本**演练，总用时 **10 s**：

| 步 | 动作 | 实测 |
| --- | --- | --- |
| S1 | .NET 10 宿主首启（新数据目录） | `/healthz` 200（1 s）、`/readyz` 503、库 `user_version=10` / 表 29 / 4 096 B |
| S2 | 面板保存一条行为配置 | **HTTP 503**（`configuration_rejected` / `code=10 CommitFailed`）—— 见 §4.2 |
| S3 | 停写 + `VACUUM INTO` | 快照 274 432 B，0.04 s |
| S4 | 换回 .NET 8 旧产物 + 恢复快照 | `/healthz` 200、`/status` running、库仍 10 / 29 / 274 432 B |
| S5 | 再前滚 .NET 10 | `/healthz` 200、库仍 10 / 29 / 274 432 B |

**关键结论**：本次升级没有改 schema（`src/BotAgent.Storage/AppDatabase.cs` 零改动），
所以 .NET 8 旧二进制**能直接读** .NET 10 建的库。这是**特例，不是通则** —— 任何动了 schema 的升级
仍然只能靠快照回退（见 §6）。

### 4.2 回滚后验证时的一个坑：`POST /api/settings` 要发**最小体**

跨版本演练里 S2 曾返回 503，一度被误判成升级回归。实测对照：

| 请求体 | net10 宿主 | net8 宿主 |
| --- | --- | --- |
| 把 `GET /api/settings` 的 167 键 `runtime` 摊平后原样 POST | `code=10 configuration_rejected`，回读不变 | **同样 503**，回读不变 |
| 最小体 `{"groupCooldownSeconds":7}` | **HTTP 200**，回读 7 | 同样成功 |

原因：旧面板口径的 `POST /api/settings` 只认**顶层扁平字段**（`WebUiServer.Settings.cs:229-230` 的
`LegacyField<T>` 逐字段取值），整包回灌会被判 `CommitFailed`（`src/BotAgent.Storage/ConfigurationStore.cs:360`）。
→ 该行为与 .NET 版本无关。**验证回滚后的写入能力时，请只发要改的那几个字段。**

---

## 5. 常见故障与处置

| 现象 | 含义 | 处置 |
| --- | --- | --- |
| 启动即退出，日志首行 `database_schema_newer` | 库的 `user_version` 高于当前二进制支持的版本 | 用升级前快照回滚库；**不要**手改 `user_version` 骗过去 |
| 启动即退出，日志含 `configuration_schema_newer` | 配置修订号高于当前二进制支持的版本 | 同上，回滚数据库（配置也存在库里） |
| `/healthz` 200 但 `/readyz` 503 | 宿主活着但没连上协议端 | 查协议端连通性与令牌，**不是**回滚信号 |
| `/status` 返回 `{"error":"unauthorized"}` | 没带面板/ZBA 令牌 | 带令牌重试，不是故障 |
| 面板打不开但 `/healthz` 正常 | 反代/静态资源问题（常见于 CDN 缓存了错误内容） | 查反向代理与缓存头，不要急着重启宿主 |
| 插件加载失败 | 插件包或清单不合法 | 见 SECURITY / 插件文档；社区插件默认禁用 |

---

## 6. 明确不承诺的东西

- **schema 迁移不可回滚**：只承诺"用快照把数据退回去"，不承诺原地降级 schema。
- **不可逆的数据变更**（例如删表重建、数据格式重写）发生后，回滚的代价是"丢失变更之后的增量"，这一点必须在变更前告知。
- **二进制回滚 ≠ 数据无损**：升级后新增的业务数据在回滚后不可见（旧 schema 里没有这些列/表）。
- **演练不等于生产验证**：本次演练在 Windows 开发机、临时目录、合成数据上完成；Docker 守护进程未运行，
  容器内（Linux）的权限断言（如 S21 的 `600` 检查）**未**复现。生产回滚仍需在真实部署环境再做一次。

---

## 7. 相关文档

- [基线报告](../baseline/report-2026-10-31.md) —— §4.4 是 D1–D5 演练的原始数据
- [.NET 10 升级回归验收报告](upgrade-acceptance-dotnet10.md) —— §4 是 S1–S5 跨版本演练的原始数据
- [分支模型与合并路径](../architecture/branching-and-merge-path.md) —— 回滚后如何重新发版
