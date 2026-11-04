# 日志保留、每日热备与恢复演练（#121）

这一页回答三个运维问题：**日志会自己变小吗**、**库有没有人备份**、**备份能不能真的拿回来用**。
只写已经跑在代码里的行为；没做到的写在最后一节「明确不做的」。

---

## 1. 日志：按天轮转，保留 7 天

实现：`src/BotAgent.Headless/Services/FileLog.cs`。

| 项 | 值 | 说明 |
| --- | --- | --- |
| 活跃文件 | `logs/qqchat.log` | 面板「日志」页与 `/api/logs` 读的就是它 |
| 滚动触发 | 单文件 ≥ **10 MiB** 或**跨天** | 两个条件谁先到都滚，滚动本身不改写内容 |
| 归档名 | `qqchat-YYYY-MM-DD.log` → `qqchat-YYYY-MM-DD.log.gz` | 同一天滚多次会加 `-2`/`-3` 后缀，不覆盖 |
| 保留期 | **7 天**（按天算，不是按份数） | 每天最多清理一次，删的是「最后写入时间早于 7 天前」的归档 |

为什么是「按天」而不是「保留 N 份」：日志里有群聊正文（见 `AGENTS.md` 第 1 节），
能用一句话说清的上限是「最多留 7 天」，而「留 5 份」在消息量暴增时会变成「留 5 小时」。
压缩用 `GZipStream(SmallestSize)` —— 以 CPU 换磁盘，这台机器盘比核紧张。

滚动与清理都在写日志的同一把锁里完成，失败只吞掉（日志写不进去绝不影响机器人干活）。
清理失败走 `Console.Error`，**不**回写 FileLog —— 否则「写日志失败」会递归触发新的写日志。

这套规则被拆成纯文件操作 `src/BotAgent.Headless/Services/LogRotation.cs`（`ShouldRoll` / `Roll` / `Sweep`），
由 `tests/BotAgent.ProductionSpecProbe` 的 `LogRotationTests` 用临时目录逐条钉住（10 条：写满滚 / 没满不滚 /
跨天滚 / 归档被 gzip / 活跃文件被改名 / 解压内容完整 / 同日第二次滚动不覆盖 / 超 7 天被删 / 7 天内保留 /
不误删活跃文件）。抽出来的理由很实际：等线上真写满 10 MiB 再验一次要几天，而纯函数用临时目录毫秒级。

---

## 2. 数据库与备份权限：600

- 库：`AppDatabase.Initialize` 迁移完成后对 `data/botagent.db` 设 `600`（仅属主可读写）。
- 备份：`VACUUM INTO` 产出后对备份文件也设 `600`。
- Windows 上直接跳过（ACL 由部署方负责）；设置失败只 `WARN`，不阻断启动。

这一条重要是因为 `secrets` 表是明文 `value TEXT`（库被 644 就等于密钥被读走）。

---

## 3. 每日热备：宿主自己跑，不再「代码存在但没人调」

实现：`src/BotAgent.Headless/Services/Ops/DatabaseBackupService.cs`，由 `CompositionRoot` 装配、
`Program.Main` 在 `graph.HealthReports.Start()` 之后调 `graph.Backups.Start()`。

| 项 | 值 |
| --- | --- |
| 首次执行 | 启动后 **10 分钟**（避开重启风暴与升级窗口） |
| 检查频率 | 每小时一次；同一天已成功就不再备 |
| 备份手段 | `AppDatabase.VacuumIntoBackup`（`VACUUM INTO`，在线、不锁读） |
| 产物位置 | `data/backups/qqchat_daily_YYYYMMDD_HHmmss_fff_xxxxxx.db` |
| 保留份数 | **最近 3 份**（按创建时间，超出即删） |
| 结果记录 | `meta` 表两个键：`backup_last_success_utc_ms`、`backup_last_failure` |
| 并发保护 | 同一时刻只跑一个（`RunOnce` 在 `_runGate` 锁内 + `IsRunning` 标记），定时器/面板按钮/重启后的新实例同时点火也只备一份 |

手动触发（不改代码、不用登机器）：

```sh
# 读状态
curl -s -H "X-Panel-Token: $TOKEN" http://127.0.0.1:18245/api/backup

# 立刻备一份
curl -s -X POST -H "X-Panel-Token: $TOKEN" http://127.0.0.1:18245/api/backup

```

`POST` 是「立即备一份」的强制入口（`RunNowAsync(force: true)`）：它会绕过「今天已经备过」的去重，
但仍然走同一把锁 —— 连点几次不会同时产出多份。去重与互斥都有探针用例（`BackupSingleFlightTests`、
`OnlineBackupTests` 里的「今天已经备过时不再重复备份」）。

---

## 4. 水位在哪看

| 位置 | 内容 |
| --- | --- |
| `GET /status` 的 `ops` 段 | `database.bytes`、`logs.bytes`、`backups.lastSuccessAt`/`stale`、`memory.rssBytes`/`usedPercent`/`swapUsedBytes`/`swapTotalBytes`、`process.uptimeSeconds`/`startups`/`restarts` |
| ZBA `GET /zba/v1/status` | 多一个 `backup` 组件（`healthy` / `stale` / `faulted`，`reasonCode` 是给人看的一句话） |
| 面板「总览」顶部 | 备份过期或失败时出现红条（`data-testid="backup-stale-banner"`） |
| 健康日报 | `资源：` 段之后多一行 `备份：最近一次 MM-dd HH:mm`；失败原因进 ⚠ 列表 |

`process.startups` 是宿主启动次数（`meta` 表 `startup_count`，每次起来 +1），`restarts = startups - 1`：
容器被 OOM 杀掉重启时这个数会涨，配合 swap 用量就能分清「内存被谁吃了」。

判定口径：**最近一次成功超过 2 天，或最近一次尝试失败** → 算异常。刚部署还没到 10 分钟时也会显示
「还没成功备份过」，属正常，别慌。

---

## 5. 恢复演练（不是「文件还在」就算过）

集成场景 **S60**（`tests/BotAgent.IntegrationHarness/BackupScenario.cs`，
`BOTAGENT_IT_ONLY=s60` → 11 条断言全绿）钉住的是这条链路：

1. 面板改一条配置（`groupCooldownSeconds=7`）→ 立刻可读；
2. 连打 4 次 `POST /api/backup` → 每次成功，且目录里**恰好 3 份**；
3. `GET /api/backup` 报最近成功时间且 `stale=false`；`/status` 的 `ops` 段有库大小/日志大小/最近备份；
4. 进程退出后独立打开备份：`PRAGMA integrity_check = ok`、`user_version` 与源库一致、`meta` 行数一致；
5. 把备份复制到**全新的数据目录**、用**新进程**起起来：`/healthz` 200，
   面板读回的 `groupCooldownSeconds` 仍是 7。

第 4 步为什么必须等进程退出：源库是 WAL 模式，进程退出后 `-shm` 已被删，只读打开会直接 `CANTOPEN`；
S60 因此对源库用「只读用途但 ReadWrite 打开」的方式查形状（让 SQLite 把 `-shm` 建出来），备份文件本身没有 WAL，
只读打开即可。第 5 步才是真正的「恢复」，前四步只是「文件长得像数据库」。

线上手工恢复的顺序（与 `docs/operations/rollback-playbook.md` 一致）：

```sh
docker compose stop qqchat
cd data
cp botagent.db botagent.db.before-restore        # 先留档，别直接覆盖
cp backups/qqchat_daily_<最新>.db botagent.db
cd ..
docker compose start qqchat
curl -s http://127.0.0.1:18245/healthz            # 期望 {"status":"ok","version":"x.y.z"}
```

---

## 6. 容器资源上限

| 服务 | 上限 | 理由 |
| --- | --- | --- |
| `qqchat` | `mem_limit: 768m`、`pids_limit: 512` | 机器 1 GB；留 200 MB 给系统与 NapCat，进程数上限防 fork 风暴 |
| `napcat` | `mem_limit: 512m` | 协议端偶尔为一张大图把 RSS 顶上去 |
| `tts` | 不设 | 按需启停，不是常驻 |

改这几行要 `docker compose up -d` 重建容器才生效（重启容器不够）。

验证方式（不需要起容器）：

```sh
docker compose config --no-interpolate | grep -E "^\s+(mem_limit|pids_limit)"
```

顺带修掉一个一直藏着的坑：`qqchat` 服务块里原本有**两个 `volumes:` 键**（一个挂 docker.sock + /opt/qqchat，
一个挂 `./data:/data`），YAML 后者覆盖前者，于是「透过 docker 操作服务器」那两个挂载从来没生效过，
而现代 `docker compose` 会把整份文件判成非法（重复键）。#121 把前一个块注释掉，语义与线上实际行为保持一致，
`docker compose config` 现在 RC=0。要不要把特权挂载恢复、以及怎么划安全边界，留给 issue #131 定。

---

## 7. 明确不做的

- **不做异地备份**：备份与库同盘同目录，盘满或整机丢就一起丢。要异地得部署方自己挂载/同步。
- **不做时间点恢复（PITR）**：只有「每天一份 + 手动触发」，没有 WAL 归档。
- **不引 logrotate / Prometheus**：容器里没有 cron，也不值得为 7 天日志再起一个进程；
  指标复用现有 `/status`、健康日报与 ZBA status 三个通道。
- **不在面板里做「一键恢复」**：恢复是停机操作，做成按钮等于给误操作开了个后门。
