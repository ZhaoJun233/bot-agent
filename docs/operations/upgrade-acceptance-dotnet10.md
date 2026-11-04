# .NET 10 升级回归验收报告（#105）

本文件是 #102 升级的**独立验收证据**：用与 [基线报告](../baseline/report-2026-10-31.md) 同一套命令复跑，
逐项给出「升级前 vs 升级后」对照与判定，并记录一次完整的回滚演练。
**只记录形状与计数**，不含真实群聊内容、成员隐私或生产凭据；文中 QQ 号、令牌均为合成值。

---

## 0. 验收窗口与锚点

| 项 | 值 |
| --- | --- |
| 验收锚点（升级后） | 提交 `316166aff5ffdb5c9232b336402c80f161bdb251`（分支 `chore/api-surface`） |
| 对照基线（升级前） | 提交 `49a20b4fd729ed677b46aa1443b5c9a5ae02c26b`（见基线报告 §1） |
| 复跑开始时间 | 2026-10-31T20:42:32+08:00 |
| 环境 | Windows 开发机，SDK 10.0.101（唯一 SDK），node v24.22.1，Python 3.13.12 |
| 工作树 | 干净（复跑期间除验收修复外无改动） |

升级窗口内 `src/` 的净变更（`git diff --name-only 49a20b4 316166a -- src`）只有 18 个文件：
8 个 `csproj` 的 `TargetFramework`、插件 TFM 契约两个文件、`Dockerfile`、
`PanelRoutes.cs`（#125 的注释对齐）、`src/web` 四处路径常量与两份 README。
**`src/BotAgent.Storage/AppDatabase.cs` 零改动** —— 这就是 §4 回滚演练里「.NET 8 旧二进制能直接读 .NET 10 库」的原因。

---

## 1. 对照表总览

| # | 检查项 | 升级前（`49a20b4` / net8.0） | 升级后（`316166a` / net10.0） | 判定 |
| --- | --- | --- | --- | --- |
| 1 | 全部工程构建 | 只记录 Host + harness 两项 | **30 / 30 成功**，0 错误 | 一致（覆盖面扩大） |
| 2 | 构建警告 | 0（Host）+ 1（harness CS8602） | 5 条（CS0067 ×1、CS8602 ×1、CS0649 ×3） | 预期内（多编了 28 个工程） |
| 3 | 22 个探针 | 全部通过 | **全部通过** | 一致 |
| 4 | `ArchitectureProbe` 断言数 | 172 | **186** | 预期内（#123/#124 新增 14 条） |
| 5 | `PluginPackageProbe` 断言数 | 28 | **34** | 预期内（#102 新增 6 条 TFM 白名单断言） |
| 6 | `ConcurrencyStressProbe` p95 | 366.647 ms | **342.827 ms**（基线 500 ms） | 一致（更好，非回归） |
| 7 | 集成 harness 全量 | 1181 通过 / 0 失败 | **1181 通过 / 0 失败** | 一致（见 §3 的 flaky 定性） |
| 8 | 前端 typecheck | 通过 | 通过 | 一致 |
| 9 | 前端单测 | 37 文件 / 469 测试 | **37 / 469** | 一致 |
| 10 | 前端 build | 3.73 s | **2.11 s** | 一致 |
| 11 | e2e chromium | 96 passed（2.9 m） | **96 passed（2.6 m）** | 一致 |
| 12 | 隔离回滚演练 | 5 步（同版本内） | **5 步（.NET 10 → .NET 8 → .NET 10）** | 一致（更严格） |
| 13 | 受版本管理文件数 | 760 | **774** | 预期内（#123/#124/#125 新增治理与文档文件） |
| 14 | `src/**/*.cs` 行数 | 70 008 | **70 025** | 一致（+17 行） |
| 15 | `tests/**/*.cs` 行数 | 44 328 | **44 696** | 预期内（+368 行，探针断言） |
| 16 | 工程数 | 30 个 `csproj`（全 net8.0） | **30 个（全 net10.0）** | 预期内（升级本身） |

**真实回归：0 项。** 差异项全部归入「预期内的工具链变化」或「测试本身不稳定」（后者已修复，见 §3）。

---

## 2. 逐项明细

### 2.1 构建

```sh
for p in $(git ls-files '*.csproj' | sort); do dotnet build "$p" -c Release; done
```

| 项 | 升级后实测 |
| --- | --- |
| 成功工程 | **30 / 30**（src 8 + tests 22） |
| 错误 | **0** |
| 警告 | **5** 条，分布在 3 个工程 |
| 逐工程耗时合计 | **74.12 s**（最慢 `BotAgent.Headless` 9.92 s，其次 `BotAgent.Core` 5.34 s） |
| 产物路径 | `bin/Release/net10.0/*.dll`（升级前为 `net8.0`） |

警告明细（全部是**既有**问题，升级前就存在，与 TFM 无关）：

| 警告 | 位置 | 说明 |
| --- | --- | --- |
| `CS8602` ×1 | `tests/BotAgent.IntegrationHarness/MockOpenAi.cs(463,39)` | 解引用可能为空（基线报告 §3 已记录同一条） |
| `CS0067` ×1 | `tests/BotAgent.ChaosFaultProbe/Program.cs(500,36)` | 事件 `OnStateChanged` 从未使用 |
| `CS0649` ×3 | `tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs(4984/4989/4990)` | `SyntheticParticipant` 三个字段从未赋值 |

> 基线只编了 Host 与 harness 两个工程，所以「升级前 1 条警告」与「升级后 5 条警告」**不是同一口径**：
> 后者的分母是 30 个工程。按同一口径（Host + harness）对比：升级前 1 条、升级后 1 条（同一条 CS8602）。

### 2.2 探针（22 个）

```sh
dotnet tests/<Probe>/bin/Release/net10.0/<Probe>.dll
python tests/BotAgent.ServerFilesProbe/test_server_files.py
```

| 探针 | 升级前 | 升级后 | 判定 |
| --- | --- | --- | --- |
| ArchitectureProbe | 172 | **186** | 预期内（+14，见 §2.3） |
| SafetyProbe | 609 | 609 | 一致 |
| ParticipationProbe | 48 | 48 | 一致 |
| ServerFilesProbe（Python） | 15 | 15 | 一致 |
| ProductionSpecProbe | 76 | 76 | 一致 |
| ConcurrencyStressProbe | 12（p95 366.647） | 12（**p95 342.827**） | 一致 |
| ChaosFaultProbe | 43 | 43 | 一致 |
| PipelineEval | 68 | 68 | 一致 |
| ReviewRemediationProbe | 1173 | 1173 | 一致 |
| ReverseTransportProbe | 44 | 44（`failures=0`） | 一致 |
| DeadlineGateProbe | 53 | 53 | 一致 |
| FeishuRemediationProbe | 24 | 24 | 一致 |
| SettingsScopeProbe | 17 | 17 | 一致 |
| SsrfProbe | 77 | 77 | 一致 |
| McpProbe | 137 | 137（A 78 / B 33 / C 26） | 一致 |
| ZbaProbe | 303 | 303 | 一致 |
| ProtocolProbe | 268 | 268 | 一致 |
| PluginPackageProbe | 28 | **34** | 预期内（+6，见 §2.4） |
| PluginMarketProbe | 82 | 82 | 一致 |
| PluginSettingsProbe | 48 | 48 | 一致 |
| SnapshotProbe | 19 | 19 | 一致 |
| EngineProbe | 20 | 20 | 一致（已随 #123 纳入 CI） |

**22 / 22 全部通过**，0 失败。断言总数由 **3336** 增至 **3356**（+20 = 架构 +14、插件包 +6）。

### 2.3 ArchitectureProbe 的 +14

| 来源 | 条数 |
| --- | --- |
| #123 仓库治理骨架（`RepositoryGovernanceChecks` 7 条 + 4 条 props 负向夹具） | +11 |
| #124 文档一致性（`DocumentationChecks` 2 条：内链存在、中英成对） | +2 |
| #124 治理文件清单从 5 项扩到 7 项（`CONTRIBUTING.en.md` / `SECURITY.md` / `SECURITY.en.md`） | +1 |

升级后探针仍提示两项可下调的棘轮，**本次不动**（下调属重构收益，留给 #106/#107）：

- `MaxFileLines` 1740 → **1727**（`src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs`）
- `PanelExemptFileIo` 14 → **13**（`src/BotAgent.Headless/Adapters/Panel/PanelDeploy.cs`）

### 2.4 PluginPackageProbe 的 +6

`src/BotAgent.Core/Domain/Plugins/PluginPackageManifest.cs` 在 #102 里引入：

```csharp
public const string CurrentTargetFramework = "net10.0";
public static readonly IReadOnlyList<string> SupportedTargetFrameworks = ["net8.0", CurrentTargetFramework];
```

`PluginPackageValidation.cs` 的硬校验从「必须等于 net8.0」改成「必须在这个白名单里」。新增断言覆盖：

- 接受：`net8.0`、`net10.0`（2 条）
- 拒绝：`net6.0`、`net9.0`、`net11.0`、空串、`net10`（5 条）

语义决定：**.NET 8 程序集在 .NET 10 运行时上前滚运行**，升级宿主不淘汰已发布的 net8.0 插件。

### 2.5 前端（`src/web`）

| 命令 | 升级前 | 升级后 |
| --- | --- | --- |
| `npm run typecheck` | 通过 | **通过**（rc=0） |
| `npm run test:unit` | 37 文件 / 469 测试，23.87 s | **37 / 469**，8.85 s |
| `npm run build` | 3.73 s | **2.11 s**；`index-CnhQDeFk.js` 349.84 kB（gzip 115.67 kB）、`index-C-9QtE7s.css` 69.22 kB |
| `npx playwright test --project=chromium` | 96 passed（2.9 m） | **96 passed（2.6 m）**，0 failed |

产物哈希与基线报告 §4.3 **完全相同**（`index-CnhQDeFk.js` / `index-C-9QtE7s.css`），说明前端源码未被升级改动 —— 与 §0 的「`src/web` 只改了路径常量」吻合。

### 2.6 容器路径（基线未覆盖项，本轮补齐）

| 项 | 结果 |
| --- | --- |
| `docker build -f src/BotAgent.Headless/Dockerfile` | **成功**，镜像 `qqchat-agent:net10-test` 420 MB |
| 镜像内运行时 | `DOTNET_VERSION=10.0.14`；`dotnet /app/BotAgent.Headless.dll --version` → `BotAgent.Headless 1.1.0` |
| 容器内启动冒烟 | `docker exec … --health` **exit 0**；日志 `[Web] 面板已监听` + `[Host] 运行中。数据目录 /data` |
| SQLite | 容器内正常建库（`AppDatabase` 无 TFM 相关改动） |

> 基线报告 §6 第 3 条「容器化路径未验证」**已在本轮补齐**。
> 注意：`docker.io` 在此开发机上不可达（daemon 不走宿主代理），需先 `docker pull` 落缓存 `mcr.microsoft.com/dotnet/{sdk,runtime}:10.0` 与 `docker/dockerfile:1`，再构建。

---

## 3. 差异项判定：S12 的偶发失败（测试本身不稳定，已修复）

### 3.1 现象

升级后**第一次**全量集成跑出 `通过 1177，失败 4`，四条全部落在 `S12 群历史补录`：

```
✗ 会话消息接口可访问 → HTTP -1
✗ ★ 补录的 8 条历史全部留在会话里（H3 回归） → 只找到 0/8 条
✗ 成员档案接口可访问 → HTTP -1
✗ ★ 面板能看到群聊里的档案（M1 回归） → 由于目标计算机积极拒绝，无法连接。 (127.0.0.1:18085)
```

`HTTP -1` 来自 `tests/BotAgent.IntegrationHarness/Program.cs:2192` `HttpGetAsync` 的 catch（连接异常，不是 401）。

### 3.2 根因（实测确认，不是「没等面板端口」而是「面板根本没起来」）

扫 `%TEMP%/qqchat-it/` 下 250 个 `s12-*` 数据目录的 `logs/qqchat.log`，**3 个**没有「面板已监听」行，日志原文：

```
[Web] 绑定 http://+:18085/ 失败：拒绝访问。
[Web] 绑定 http://127.0.0.1:18085/ 失败：另一个程序正在使用此文件，进程无法访问。
[Web] WARN 面板无法启动（端口 18085）。可设 QQCHAT_HEALTH_BIND=127.0.0.1，或 QQCHAT_HEALTH_PORT=0 关闭。
```

`src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs:298-326` 在两种 bind 都失败时**只 warn 不退出**，
于是宿主活着、协议端活着，但 `/api/*` 全部连不上。

端口被占的来源：Windows 上 `18085` 被别的进程的一条**普通出站连接**占成临时端口（源端口恰好落在 18085）。
S12 当时把端口**写死**，因此从未走过 `FreePort` 的 `CanBind` 探测；而 `HttpListener` 在 Windows 上是向 HTTP.sys
注册 URL 前缀，撞上已存在的套接字就报 `另一个程序正在使用此文件`。`FreePort` 的注释记录了同类现象：
写死端口时机器人要 `bind 0.0.0.0:{port}` 会报「每个套接字地址只允许使用一次」。

### 3.3 修复

`tests/BotAgent.IntegrationHarness/Program.cs` 的 S12 场景（1 file，+6/−3）：

- 三个写死端口 `17811` / `13021` / `18085` 改为 `FreePort(preferred)`；
- 在 `await WaitForPortAsync(botWsPort, …)` 之后补 `await WaitForPortAsync(panelPort, …)`
  （`src/BotAgent.Headless/Host/Program.cs:91` 的 `web.Start()` 排在 `Agent.Start()` 之后，只等 ws 端口不足以说明 `/api/*` 可用）。

### 3.4 验证

| 跑法 | 结果 |
| --- | --- |
| `BOTAGENT_IT_ONLY=s12` × 3 | 3/3 全绿 |
| 全量 × 2（修复后） | **1181 / 0**、**1181 / 0**（两次各 1345 行、`✗` 计数为 0） |

### 3.5 判定

**测试本身不稳定**，与 .NET 10 升级无关。证据：

- 该失败点与 TFM 无关（面板 bind 逻辑在 `WebUiServer.cs`，升级未改）；
- 同形态的历史失败可追溯到 2026-09-11（`s12-4dae37e0`、`s12-240daea5` 两个数据目录的日志），**早于本次升级**；
- 修复只动 harness 的端口选择，不动 `src/`。

同类隐患：harness 里仍有 **23 个场景**用 `const int panelPort = …` 写死端口（9 个已改 `FreePort`）。
本报告只修 S12（它已经真实红过两次）；其余写死端口是**已记录的潜在 flaky**，建议在 #126/#127 里统一收口。

---

## 4. 回滚演练（.NET 10 → .NET 8 → .NET 10）

在隔离临时目录、合成令牌与合成 QQ 号下执行（脚本逻辑见 [回滚预案](rollback-playbook.md)），**总用时 10 s**。

| 步 | 动作 | 实测结果 |
| --- | --- | --- |
| S1 | .NET 10 宿主首次启动（新数据目录） | `/healthz` **200**（1 s）；`/readyz` **503**（未连协议端，符合语义）；`/status` 带令牌返回 `status=running`、`onebot.protocol=ReverseWebSocket`；库 `user_version=10`、表 **29** 张、4096 B |
| S2 | 面板保存一条行为配置 | **HTTP 503**（`configuration_rejected`/`code=10 CommitFailed`），回读仍是 8 —— 见 §4.1 |
| S3 | 停写 + `VACUUM INTO` 快照 | 快照 **274 432 B**，0.04 s；`user_version=10`、表 29；源库 sha256 已记录 |
| S4 | 回滚到 .NET 8 旧产物 + 恢复快照 | `/healthz` **200**（0 s）；`/readyz` 503；`/status` running；库 10 / 29 / 274 432 B —— **旧二进制直接接受新库**；`runtimeconfig` 目标框架 `Microsoft.NETCore.App 8.0.0` |
| S5 | 再次前滚回 .NET 10 | `/healthz` **200**；库仍 10 / 29 / 274 432 B |

产物对照：`.NET 10` dll **1 029 120 B**，`.NET 8` dll **1 027 072 B**（相差 2 048 B）。

### 4.1 S2 的 503 是「演练脚本口径错」，不是升级回归

对照实验：把 `GET /api/settings` 的 167 键 `runtime` 摊平后原样 `POST` 回 net10 与 net8 宿主 → **两者都返回
`code=10 error=configuration_rejected`，回读都是 8**。改成最小体 `{"groupCooldownSeconds":7}` → **HTTP 200，回读 7**。

原因：`POST /api/settings` 的旧口径只认**顶层扁平字段**（`WebUiServer.Settings.cs:229-230` 的 `LegacyField<T>` 逐字段取值），
把 GET 的整个 runtime 摊平回 POST 会被判 `CommitFailed`（`src/BotAgent.Storage/ConfigurationStore.cs:360`）。

→ **该行为与 .NET 版本无关**，判定为「演练脚本写法错」，不是回归。回滚预案里的保存示例已按最小体书写。

### 4.2 数据完整性

| 项 | S1（升级后） | S3（快照） | S4（回滚后） | S5（再前滚） |
| --- | --- | --- | --- | --- |
| `user_version` | 10 | 10 | 10 | 10 |
| 业务表数 | 29 | 29 | 29 | 29 |
| 文件字节 | 4 096 | 274 432 | 274 432 | 274 432 |

S1 的 4096 B 是「刚建库、数据还在 WAL 里」的正常形态（`-wal` 646 872 B）；快照把 WAL 合并进主库后是 274 432 B。

---

## 5. 不可逆变更的边界

以下事项**不能**承诺「直接二进制回滚」：

1. **schema 只前向迁移，没有 down 迁移**。回滚的实质是用快照把库退回旧版本；原地降级 schema 不支持。
   （本次升级**未改 schema**：`AppDatabase.cs` 零改动，所以 .NET 8 二进制能直接读 .NET 10 的库 —— 这是特例，不是通则。）
2. **升级后新增的业务数据在回滚后不可见**（旧 schema 没有对应列/表）。
3. **插件 TFM 契约是单向放宽**：升级后宿主接受 `net8.0` 与 `net10.0` 两种插件；回滚到 .NET 8 宿主后
   **`net10.0` 插件会加载失败**。因此升级后新发布的 net10 插件，回滚前必须先撤掉。
4. **`global.json` 钉住 SDK 10.0.101**：回滚二进制不影响它，但用 SDK 10 重新构建 `net8.0` 目标仍可行（SDK 10 能编 net8.0）。
5. **配置修订号**（`configuration_*` 表）与库同进退，同样只能靠快照回退。

---

## 6. 复现命令

```sh
# 0) 固定锚点
git rev-parse HEAD            # 期望 316166aff5ffdb5c9232b336402c80f161bdb251

# 1) 构建全部 30 个工程（任一失败即中断）
for p in $(git ls-files '*.csproj' | sort); do dotnet build "$p" -c Release || echo "FAIL $p"; done

# 2) 22 个探针
for d in tests/BotAgent.*Probe; do
  n=$(basename "$d"); dotnet "$d/bin/Release/net10.0/$n.dll" || echo "FAIL $n"
done
python tests/BotAgent.ServerFilesProbe/test_server_files.py

# 3) 集成全量
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll

# 4) 前端
cd src/web && npm run typecheck && npm run test:unit && npm run build
npx playwright test --project=chromium

# 5) 回滚演练：见 operations/rollback-playbook.md
```

> 集成 harness 是**另起进程跑宿主 DLL**。改完 `src/` 必须先单独 `dotnet build` 宿主，否则会用旧二进制跑出假红。

---

## 7. 未覆盖项与遗留

1. **本报告的独立性有限**：由执行升级的同一方复跑，不是第三方签字。真正的外部验收仍需由独立验收方在
   `dev` 上重跑一遍本文 §6。
2. **e2e 只实跑 chromium**：firefox / webkit / chromium-rate-limit 以 CI 为准（CI 四矩阵均绿）。
3. **Linux 差值未实测**：Windows 1181/0；Linux 会多 1 条（`S21` 的 Unix `600` 权限检查在 Windows 被
   `if (!OperatingSystem.IsWindows())` 门掉），按差值推算应为 1182/0 —— 但**本轮没有在 Linux 重跑**。
4. **容器冒烟只在开发机 Docker Desktop 上做过**，未在部署机（约 1 GB 内存）上验证「解压产物」路线。
5. **23 个场景仍写死面板端口**（§3.5），属潜在 flaky。
6. **`PipelineEval` 仍打印已失效的产物路径** `docs/engineering/eval-results/`（该目录已被 `04cb018` 删除）。
7. **`src/BotAgent.Headless/README.md:145` 与 `README.en.md:155` 仍写着已删除的 `/playground.html`**（#125 范围外）。

---

## 8. 相关文档

- [基线报告](../baseline/report-2026-10-31.md) —— 升级前的同口径数据
- [回滚预案](rollback-playbook.md) —— 备份清单、回滚顺序、故障处置
- [分支模型与合并路径](../architecture/branching-and-merge-path.md) —— 验收后如何发版
