# 插件热重载：候选、发布与排空

插件曾经是「装完要重启宿主才生效」的：市场把版本暂存成 `Pending`，下一次进程启动才加载。现在市场安装 / 更新 / 卸载、插件设置保存、以及全局的 `registered.plugins` 启停，都会在**不重启进程**的前提下生效；代价是必须把「什么时候换引用、旧实例什么时候退役」讲清楚，否则插件会在调用中途被卸掉。

这一页描述**当前实现**的语义与不变量：一次热重载经过哪些步骤、失败时旧实例怎么办、哪些东西热不了。协议与状态码的对外契约见 [API 表面](../api-surface.md)，威胁模型见 [SECURITY.md](../../SECURITY.md)。

## 1. 边界（先说不做什么）

- **不是沙箱。** 插件是宿主进程内的 .NET 程序集，安装后包里的代码会被实例化（默认停用也不等于不加载，见 §5）；只有启用时才会调用 `InitializeAsync`。热重载只换实例与设置，不改变信任边界（见 SECURITY.md 威胁模型第 1 条）。
- **不保证旧实例真的被卸载。** 可回收 `AssemblyLoadContext` 只是「可回收」，不是「已回收」：native 库、插件自己起的线程 / 计时器、宿主或其它插件持有的引用，都会让旧 generation 留在内存里。没有强制 GC、没有强杀。
- **不承诺零中断，只承诺不撕裂缝。** 发布是短锁内的原子换引用：新调用立刻走新实例，正在执行的旧调用继续跑完（租约排空）。
- **不改协议、权限与隔离。** 热重载不动插件的权限声明、白名单、脱敏与会话隔离；这些仍按原有路径判定。
- **没有公开的「立刻重载这个插件」端点。** 截至本页，ZBA 与面板都没有 reload 路由；唯一入口是市场操作、设置保存，或下一次宿主启动（内部方法 `PluginRuntime.ReloadAsync` 不对外暴露）。
- **生命周期与业务回调是分开的。** 生命周期只有 `IBotPlugin.InitializeAsync` / `ShutdownAsync`；业务回调只有 `IReplyPolicyPlugin.BlockReason`（本仓没有 OnMessage / ExecuteTool / BeforeReply 之类的额外 hook）。热重载要换的就是一个策略实例、它的设置与这两个生命周期方法。

## 2. 术语与生命周期

| 词 | 含义 |
| --- | --- |
| 插件包（package） | 从市场安装的 zip（manifest + 程序集 + 声明文件），持久态在 `PluginPackageStore` |
| `Active` / `Pending` / `Previous` | 当前生效版本 / 已暂存待应用版本 / 上一个生效版本；`PendingUninstall` 表示已请求卸载 |
| 预设插件（preset） | 宿主内置插件（音乐、语音、表情包、戳一戳、氛围、档案、联网、Token 配额），不经过包加载，只能热切换 enabled |
| generation | 注册表里同一个插件 id 的一次具体实例；每次重载产生新 generation |
| 租约（lease） | 一次 `BlockReason` 调用期间对某 generation 的占用；租约没归零，旧实例不会被 Shutdown |
| reconcile | 一次「让运行态对齐持久态」的完整遍历，见第 4 节 |

一次成功的重载，按下面的顺序发生（顺序本身是语义的一部分，第 6 节解释为什么不能交换）：

```
暂存（stage）→ 候选（shadow 解包 + 哈希校验 + 加载 + 按需 Initialize）
  → 先落盘持久态 → 原子换注册表引用 → 排空旧租约 → 旧实例 Shutdown + ALC 卸载
  → 最后才发布只读快照（InstalledSnapshot / catalog）
```

## 3. 入口：谁触发一次热重载

| 入口 | 代码路径 | 传给 `ReloadAsync` 的过滤条件 |
| --- | --- | --- |
| 市场安装 / 更新 / 卸载 | `PluginMarketService.MutateAsync`（`src/BotAgent.Headless/Adapters/Plugins/PluginMarketService.cs`）先 stage，提交后 `await _reload(pluginId, CancellationToken.None)` | 单个 `pluginId` |
| 插件设置保存 | `PluginMarketService.SaveSettingsAsync` 先 `PluginSettingsStore.Save`，再触发 `_reload(pluginId, …)`；失败回 `503 plugin_reload_failed` | 单个 `pluginId` |
| 全局 `registered.plugins` 启停 | `SettingsHotReload` 在 `registered.plugins` 有编辑且注入了 `_reloadPlugins` 时，在 `SettingsBox._gate` **之外**调用它（插件代码可能回调配置，绝不能在 gate 下跑） | 全量（`null`） |
| 宿主启动 | `PluginRuntime.StartAsync` → `StartCoreAsync` → `ReconcileAsync(null, startup: true, ct)` | 全量（startup 模式） |

`PluginRuntime.ReloadAsync(string? pluginId = null, CancellationToken ct = default)`（`src/BotAgent.Headless/Host/PluginRuntime.cs:111`）的约束：

- 先 `StartAsync`（幂等；首次会加载预设并做一次 startup reconcile），因此「没启动就 reload」是安全的。
- 用 `_reloadGate` 把 reload / start / stop 串行化：同一时刻只跑一个 reconcile，不会两个事务同时改注册表。
- 在插件生命周期回调里重入 → `InvalidOperationException("plugin_reload_reentrant")`；已停止 → `ObjectDisposedException`。这两个都是刻意 fail-fast，不排队。

设置保存路径有一个细节：`SettingsHotReload` 把 `registered.plugins` 的 apply 步骤记成 `RegisteredConfiguration`，失败时该步骤为 `Failed` + 错误码 `plugin_reload_failed`，其余设置项不回滚（它们各自独立成功）。

## 4. 一次 reconcile 做什么

`ReconcileAsync`（`src/BotAgent.Headless/Host/PluginRuntime.cs:126`）按顺序：

1. 读期望启用状态（DB `PluginStore`）。
2. 预设插件：逐个 `SetEnabled`，再 `Registry.StartAllAsync`（只对 enabled 且尚未 Initialize 的实例调 `InitializeAsync`，失败只记日志并置 disabled，不抛）。
3. 遍历已安装包（可按 `pluginId` 过滤）：**fast path** 直接跳过——无 `Pending`、无 `PendingUninstall`、`Active` 已加载、加载实例的 enabled 与期望一致、且设置不 `restart_required`。
4. 其余走 `PluginPackageService.ApplyAsync`（第 5、6 节）。
5. 需要时做设置代次 ack（第 8 节），并把成功/失败记进 `_failures`。

失败语义分两种：

- **startup 模式**：单个包失败不拖垮整台宿主。若该包有 `Active` + `Pending` 但没有任何已加载实例，会以 `activeFallback: true` 再用旧 `Active` 顶一次（`PluginRuntime.cs:174` 附近），并保留 `Pending` 待修；仍失败则记 `plugin_reload_failed`。
- **非 startup（市场 / 设置触发的）**：包失败会记 `plugin_reload_failed`，遍历结束后抛 `InvalidOperationException("plugin_reload_failed")`，由调用方映射成 503（市场）或 `RegisteredConfiguration` 步骤失败（设置）。已经成功应用的其它插件不回滚。

## 5. 候选：在发布之前就准备好

`PluginPackageService.ApplyAsync`（`src/BotAgent.Headless/Adapters/Plugins/PluginPackageReload.cs:13`）负责把「盘上的版本」变成「可发布的实例」，顺序是：

1. **选版本**：`Pending ?? Active`；`activeFallback` 模式强制选 `Active` 并保留 `Pending`。
2. **校验**：`PluginPackageValidation.Validate(manifest)`；同 id 的预设插件拒绝（`Package ID already registered.`）。
3. **shadow 解包 + 哈希复验**：从按固定版本保存的归档读出字节，解到 `.runtime-<guid>`，再走一遍 `PluginPackageArchive.ExtractAsync`（`src/BotAgent.Headless/Adapters/Plugins/PluginPackageArchive.cs:12`）——归档 SHA-256 必须等于 manifest 的 `sha256`，限制 32 MiB 归档 / 128 MiB 展开 / 2048 条目，拒绝绝对路径、`..`、符号链接、特殊文件与重复条目。
4. **加载**：`PluginPackageLoader.StartAsync`（`src/BotAgent.Headless/Adapters/Plugins/PluginPackageLoader.cs:17`）在可回收 `AssemblyLoadContext` 里 `LoadFromAssemblyPath` + `Activator.CreateInstance`，并核对 `Id` / `Version` 与 manifest 一致。
5. **按需 Initialize**：只有**期望启用**时才构建 env 并调 `InitializeAsync`；禁用候选只做元数据校验，绝不 Initialize。设置值来自宿主 `ResolveRuntimeValues`（第 8 节）。
6. 返回一个「已初始化」的包装实例（`PluginPackageLoader.StartedPlugin` 一类），其 `InitializeAsync` 是 no-op，防止注册表在 `StartAllAsync` 时二次初始化。

候选阶段的任何失败（校验、哈希、加载、Initialize）都会清理候选（卸载 ALC + best-effort 删除 shadow 目录），**旧实例保持运行、不被替换**，`Pending` 保留待修。这就是「坏更新不会拆掉好实例」的来源。禁用候选只做元数据加载，不 Initialize；之后启用时会重新准备一个新实例。

## 6. 发布：先落盘、再换引用、再排空

候选就绪后，`ApplyAsync` 继续（`PluginPackageReload.cs:44-54`）：

1. **先写持久态**：把 `Pending` 提升为 `Active`（`Previous` 接替旧版，`Pending` 清空）并 `SaveAsync`。写盘失败 → 不动内存，候选清理，旧实例继续服务。只有真正提升时才改状态；`activeFallback` 不推进 `Pending`。
2. **取消点**：`ct.ThrowIfCancellationRequested()` 在落盘之前；落盘之后、发布之前仍可能被取消（`SaveAsync` / `ReplaceAsync` 会观察 `ct`），此时 catch 会把持久态回滚成原样并清理候选，所以不会留下「盘上已改、内存没换」的中间态。**发布之后**才不再响应取消（不放弃排空）。
3. **原子换引用**：`PluginManager.ReplaceAsync`（`src/BotAgent.Engine/Plugins/PluginManager.cs`）在短锁内 `previous.Retire()` + 字典指向新 entry；`Find` 返回 generation-bound facade，`BlockReason` 入口锁内复查 `Enabled` / `Starting` / `Retired` 并 `InFlight++`。
4. **排空旧租约**：发布后等待旧 generation 的 in-flight 归零（无强制超时）。持有旧 facade 的调用不会拿到已退役实例——它会按 id 找到 current generation 拿租约，因此**不绕过新 policy**。
5. **Shutdown + 卸载**：排空后对旧实例 `ShutdownAsync(CancellationToken.None)`，再 `AssemblyLoadContext.Unload()` 并 best-effort 删除 shadow 目录；异常只记日志，不回滚发布。
6. **发布快照**：最后才 `Publish(next)`，把新的 `InstalledSnapshot` 换给读路径，因此 catalog / installed 列表看到的状态不会早于真实运行状态。

由此得到四条不可交换的不变量：

- **先持久化，后换引用**：崩溃最多留下「盘上是新的、内存是旧的」，下次启动 reconcile 会追上；反过来则会「内存是新的、盘上还是旧的」且无法自愈。
- **候选必须在发布前初始化**：`ReplaceAsync` 只接住已准备好的候选，自己绝不 Initialize；注册表的短锁（`_sync`）内绝不跑插件代码，`Initialize` / `Shutdown` 是在异步生命周期信号量保护下调用的，并靠 callback-depth 守卫拦截重入。若把初始化挪进发布流程，插件 `Initialize` 里的回调会直接把重载打成 `plugin_lifecycle_reentry`。
- **先换引用，后排空**：排空期间新调用必须能立刻走新实例，不能留下「旧实例还在跑但已经没人能路由到它」的空窗。
- **先排空，再 Shutdown，最后才发布快照**：让观测口径与运行态一致。

`ct` 也有边界：它只在取得生命周期串行许可之前 / 发布之前有效；发布之后取消**不放弃**排空，否则会留下无人管理的旧实例。

## 7. 失败、取消与卸载

| 场景 | 结果 |
| --- | --- |
| 候选校验 / 加载 / Initialize 失败 | 旧实例保持运行，`Pending` 保留，错误码 `plugin_reload_failed`；原实例的启用状态不变（启用失败不会把原实例翻成启用） |
| 持久态写入失败 | 内存不动，候选清理，旧实例继续服务 |
| 落盘后、发布前失败（含 `ReplaceAsync` 发布前取消 / 异常） | catch 里把持久态回滚成原样并 `Publish(original)`，再重抛；候选由 `finally` 清理 |
| `ReplaceAsync` 发布后旧实例 Shutdown 失败 | 只记日志；新实例已生效，不回滚（旧版清理失败不能撤销新版） |
| 旧实例有永久卡住的调用 | 替换任务会一直等排空，但**新实例已经可用**；没有强制超时 / 强杀 |
| 卸载（`PendingUninstall`） | 先把持久态改成「已清除」（`Active = null, Pending = null, PendingUninstall = false`），再 `registry.RemoveAsync`，成功后 `Publish`；`RemoveAsync` 在发布前被拒（例如注册表已 stop → `plugin_registry_stopped`）时回滚持久态，旧实例保留 |
| 宿主停止（`StopAllAsync`） | 终态：清空注册表、退休所有实例（含 disabled），之后 `Register` / `Replace` 抛 `plugin_registry_stopped` |

`ReplaceAsync` 的契约是「所有权转移之后不再抛异常」；转移前失败时候选仍归调用方，`ApplyAsync` 的 `finally` 会把它 Shutdown 掉（含卸载 ALC、删 shadow 目录），不会泄漏一个没人管的实例。

## 8. 设置代次 ack（为什么有时仍显示 restart_required）

插件设置与运行实例之间用「代次」对齐（`src/BotAgent.Headless/Adapters/Persistence/PluginSettingsStore.cs`）：

- `ResolveRuntimeValues(manifest, out revision)` 只返回该 manifest **声明过**的值，并记下 prepared 快照（revision + schema hash + version）。
- 实例初始化成功后 `MarkApplied(manifest, revision)` 才把 prepared 变成 applied；若期间有并发保存，prepared 与当前 target 不一致，ack 失败，`RestartRequired` 保持 true。
- `restartRequired` 由 applied 与当前 target 比较派生；因此它不是「要不要重启宿主」，而是「这个插件实例看到的设置是不是最新一代」。

含义：**热重载不吞并发写**。如果保存发生在初始化之后或跟初始化竞争，面板会如实显示 `restart_required`，需要再保存一次或再触发一次 reconcile；不会假装旧 runner 已经应用了新值。

## 9. 观测：状态与错误码

| 观察点 | 位置 | 取值 |
| --- | --- | --- |
| capabilities 描述符 | `PluginRuntime.Describe` | `type="plugin"`；state `Running`（加载+启用）/ `Disabled`（加载但禁用）/ `NotLoaded`；error `plugin_reload_pending`（有 `Pending`、`PendingUninstall` 或 enabled 不一致）或 `plugin_reload_failed` |
| 托管动作 | 同上 | `Start` / `Stop` 标为不可用，原因 `configuration_required` |
| 插件目录 | `PluginMarketService` catalog | `installed` / `not_installed` / `pending_uninstall` / `reload_required`（有待应用变更）；**未接热重载的宿主**退化为 `restart_required` |
| 操作回执 | `GET /zba/v1/plugins/operations/{key}` | status `installed` / `not_installed` / `restart_required` / `failed`；`restartRequired` 布尔 |
| 插件设置投影 | `GET /zba/v1/plugins/{id}/settings` | `revision`、`schema`、`values`、`secrets`（不回显）、`restartRequired` |
| 热重载失败 | 市场 / 设置保存 | `503 plugin_reload_failed`；设置走 `RegisteredConfiguration` 步骤 `Failed` |

市场同步触发重载失败时**变更已经暂存**：不会让调用方误以为「什么都没发生」，也不会把 `Pending` 丢掉；修正后用新的操作键重试。这条在 `PluginMarketService.MutateAsync` 的注释里是显式承诺（“Once staging committed, a disconnected caller must not strand hot application”）。

## 10. 安全与原生依赖 caveat

- **无沙箱**：插件与宿主同进程、同权限，能碰宿主能碰的一切（包括凭据）。热重载让「装错版本」的恢复更快，但不降低安装本身的权限要求（SECURITY.md 威胁模型第 1 条）。
- **ALC 可回收 ≠ 已卸载**：旧实例身上的 native 库、后台线程、静态事件订阅、被可信代码持有的引用都会让它继续活着；也没有强制 GC。看到「重载成功但内存里还有旧版本」是预期现象。
- **文件锁**：shadow 目录（`.runtime-<guid>`）的清理是 best-effort。Windows 上映射中的 DLL 可能保留到 ALC 真正回收，清理失败只记日志；残留的 shadow 目录无害。固定版本的归档永远不删（`PluginPackageLoader.TryDelete` 注释明确「Never force GC or delete the pinned archive」）。
- **插件不能假设一定有优雅 Shutdown**：进程退出、崩溃、强制重启都不会走 `ShutdownAsync`；只有重载 / 卸载 / 正常停机路径会走，且 Shutdown 异常被吞掉（不影响换引用）。
- **native 依赖热不了**：插件包里的原生库（如某个 DB 引擎、图像库）一旦被加载，卸载不保证生效；这类插件升级后可能仍需要重启宿主才能完全切换。

## 11. 现在还做不到什么

- **没有公开的 reload API**：面板与 ZBA 都没有「立刻重载」路由；只能通过市场操作、设置保存、或下次启动触发。想做手动按钮，需要先补一个受管理员权限保护的端点（并明确它只做 reconcile，不承诺旧实例已被卸载）。
- **不能强杀卡住的旧实例**：排空没有超时；一个永久阻塞的 `BlockReason` 会让该次替换任务一直等下去（新实例已可用，但该次 reconcile 不返回）。
- **不能热更新预设插件代码**：预设插件编在宿主里，只能热切换 enabled。
- **不能保证插件包内 native 依赖可卸载**（见第 10 节）。
- **一次 reconcile 是串行的**：大批量更新逐个应用；某个包失败不会回滚已经成功的包，需要按失败项重试。
- **没有「回滚到上一版」的公开操作**：持久态里保留 `Previous`，但截至本页没有对外暴露的回滚按钮 / 端点。

## 12. 改这里时要看的回归

- `tests/BotAgent.EngineProbe/PluginHotReloadChecks.cs`：候选先发布再排空、卸载会排空故障调用、生命周期操作串行、发布前取消保留所有权、Stop 会排空 disabled 实例、Shutdown 失败不撤销发布；另断言过期 facade 不复活旧代、发布后取消不放弃排空、prepared replacement 不二次 Initialize、回调重入 fail-fast（`plugin_lifecycle_reentry`）、Stop 后拒绝发布（`plugin_registry_stopped`）。
- `tests/BotAgent.EngineProbe/` 中与市场 / 设置相关的探针：覆盖「staging 后热应用」「失败保留旧实例」的链路断言。

这一节只列覆盖点，不声称执行结果；具体跑数与结论随各次交付记录。

## 13. 卸载后的禁用意图与列表投影

已卸载包不一定还出现在配置列表。`PluginRuntime.ReadConfiguration` 只投影 `DeclaredIds()`：预设插件、具有 Active / Pending 版本的包，以及 PreparedInstallId。这个集合不包含所有历史存储键。卸载后看到 `registered.plugins` 没有该 ID，应结合包与运行态判断，不能据此断言禁用意图丢失。

禁用意图由 Storage 的 `PluginStore` 以 `meta.plugins_state` 字典独立保存。验收卸载后是否保持禁用，应在**合成数据副本**中确认该 ID 对应原生布尔值 `false`，并复核重启期间与退出之后仍为 `false`；同时确认安装、运行态与配置投影均无已卸载包。真实库只可查经授权的脱敏形状，不能读取或导出内容。

原先依据配置列表行存在性失败的验收记录应原样保留。重新绑定正确观察口径的补充证据要单独报告命令、候选哈希与范围，不能把原始失败改写为通过，也不能代替新的完整安装、升级和卸载回放。
