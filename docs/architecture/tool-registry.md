# 工具注册表：声明、查询面与可见性判定（#115）

这一页回答四个问题：系统里**有哪些工具**、它们**从哪来**、**谁能让它不可见**、**谁真正执行**。
改工具相关代码前先读这一页，尤其是「两张名字表」与「Fail-Closed 方向」两节 —— 那里各踩过一次坑。

## 1. 四层职责，别混

| 层 | 位置 | 职责 | 不做什么 |
| --- | --- | --- | --- |
| 声明 | `src/BotAgent.Core/Domain/Tools/ToolSpec.cs` | 一行 = 一个工具：id / 类别 / 摘要 / 参数说明 / 默认策略 / 是否进提示词 / **开关名（FeatureKey）** / 来源（Origin） | 不判定、不执行 |
| 内建目录 | `src/BotAgent.Engine/Tools/ToolDirectory.cs` | 三族静态清单：聊天 10 + QQ 动作 10 + 服务器 6 = **26**（计数被探针钉住） | 不装 MCP（MCP 是运行时发现的外部来源） |
| 统一查询面 | `src/BotAgent.Engine/Tools/ToolInventory.cs` | 内建 + 当前 MCP 快照放进**同一张只读表**，回答“有哪些、来自哪里、有没有重名” | 不参与任何放行判定 |
| 可见性判定 | `src/BotAgent.Engine/Tools/ToolVisibility.cs` | **唯一**回答“这个工具现在能不能给模型用”的地方 | 不做预算/审批（那是 `ToolGate.Evaluate`） |

执行者登记（`IToolExecutor` / `ToolExecutorRegistry`）仍是“登记 ≠ 执行”：聊天两族登记时标 `LegacyPath: true`，
真正执行在 `ReplyPipeline` 的 if/else 里。`ToolInventory` 只投影 `Executor` 字段，不改执行路径。

## 2. 判定链（顺序即语义）

`ToolVisibility.Allows(snapshot, pinned, toolId)` 按顺序：

1. **来源租约**（`IsCurrent`）：停机或来源换代 → 所有工具一起失效（不是逐个判断）；
2. **插件门禁**（`PluginGate.AllowsTool`）：这里**只消费**答案。请注意两套相反方向是刻意保留的：
   `PluginGate` 对未知 id **fail-open**（旧行为），`ToolVisibility` 与 `ChatCapabilities` 对未知 id **fail-closed**；
3. **面板开关**：`ToolSpec.FeatureKey` → `ToolSwitches.Enabled`。未知 / 空开关名一律 `false`；
4. **本轮固化的场景策略**（`pinned`）：`EffectivePlatformPolicy.Feature(...)` 说关就关；
5. **实时平台策略解析器**：防“热重载空窗里旧快照还开着”。

没有 `pinned`、也没有来源上下文（`PlatformSourceExecution.Current`）而解析器又非空时，第 5 步无法解析 → **不可见**。
这是改造前就有的口径（`ReplyToolAvailability` 的同一行），不是 #115 引入的；探针里有专门一条钉它。

## 3. 两张名字表（踩过的坑）

| 表 | 取值 | 用在哪 |
| --- | --- | --- |
| 开关名 `ToolFeature` | `websearch` / `music` / `voice` / `stickers` / `poke` / `approval` / `question` / `text` | 面板开关、`ToolSwitches` |
| 平台能力名 | `text` / `image` / `voice` / `quote` / `recall` / `stickers` / `music` / `poke` / `linkpreview` / `websearch` | `EffectivePlatformPolicy.Feature` |

平台策略里**没有** `approval` / `question` 这两个能力 —— 审批与提问都属于 `text`。
若把开关名直接当能力名去查，`Feature("approval")` 返回 `supported=false`，结果是**审批与提问整条链被静默关掉**。
映射写在 `ToolVisibility.PlatformFeature`，探针用它当回归点（`ToolVisibility: approval and question map to the text capability`）。

## 4. MCP 是追加来源，不改内建计数

- MCP 工具**不进** `ToolDirectory`：那边是内建能力目录，10/10/6 = 26 有硬计数与多条守卫；
- `ToolInventory.Project(McpToolInfo)` 投影成 `ToolSpec`：`Origin = Mcp`、`Category = RemoteAgent`（任何审批都不放开）、
  `PromptVisible: false`（与原行为一致）、`Parameters` 固定写「由 MCP 服务器声明，调用前按远端 schema 校验」；
- 描述来自外部服务器，只留**单行短摘要**（≤120 字），不原样进面板/提示词；
- 与内建撞名不会被静默覆盖，而是进 `Duplicates` 列表（面板可见）。

面板 `GET /api/tools` 因此有两个层次：

```
count           = 26            // 内建目录，保持不变
inventory.builtin = 26
inventory.mcp     = 0..n
inventory.total   = 26 + n
inventory.duplicates / mcpTools / entries[{id,origin,category,summary}]
```

## 5. 守护测试

| 断言 | 位置 |
| --- | --- |
| 6 条可见性行为（未知 id、开关、联网就绪、停机、审批映射、策略关闭） | `tests/BotAgent.EngineProbe/ToolVisibilityTests.cs` |
| MCP 追加不改内建 26、投影字段、撞名报告 | 同上 `InventoryAppendsMcpWithoutChangingBuiltinAsync` |
| 可见性单入口、兼容壳不含开关读取、10 条 FeatureKey、查询面与面板分列 | `tests/BotAgent.ArchitectureProbe/Program.cs` 的 `ToolRegistryChecks` |
| `/api/tools` 的 `inventory` 块（内建 26 / MCP 0 / 无重名） | `tests/BotAgent.IntegrationHarness/PanelToolsScenario.cs`（s46） |

## 6. 已知边界（#115 没做的）

- **MCP 仍未进聊天提示词的可见清单**：`PromptVisible: false` 是刻意保持的现状，合并 MCP 与聊天可见性要另开条目；
- **`//` 那一路没并入 `ToolVisibility`**：它走 `ServerToolGate` / `QqActionCatalog.ParseAllowed`，与聊天那路的规则至今是两套；
- **没有任务级 ToolScope**：`AgentTaskRequest` 没有工具作用域字段，跨任务收窄工具面要等后续条目；
- **执行期还有内联复判**（发送前、动作前），那些不是可见性，是副作用前的最后一道保险，不动。

## 7. 改这里时的检查单

1. 新增一个工具 → 在 `ChatToolSpecs` / `ServerToolSpecs` / `QqToolSpecs` 里加声明，**必须给 FeatureKey**（或用 `ToolFeature.Text` 明确不挂开关）；
2. 新增一个开关 → `ToolFeature` + `ToolSwitchState` + `ToolSwitches.Enabled` + `AppSettings` 字段，四处同改；
3. 改判定 → 只改 `ToolVisibility`，并同步 EngineProbe 的六条用例；
4. 改面板投影 → 保持 `count` = 内建条数，MCP 只进 `inventory`；
5. 跑 `dotnet tests/BotAgent.ArchitectureProbe`、EngineProbe、`BOTAGENT_IT_ONLY=s46`。
