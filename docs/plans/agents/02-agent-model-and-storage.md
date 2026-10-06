# Agent 2 执行手册：模型层独立与存储配置垂直切片

> **文档标识**：`docs/plans/agents/02-agent-model-and-storage.md`\
> **执行代号**：`Agent-02` (Model-Storage)\
> **所属波次**：**Wave 2（双轨并行轨 A，与 Agent 3 并行）**\
> **所属方案**：BotAgent 模块化重构与功能插件化演进方案 (`modular-monolith-refactoring-plan.md`)\
> **更新时间**：2026-10-06\

---

## 1. 任务目标与范围 (Objective & Scope)

Agent 2 承担底层数据中枢与 AI 传输中枢的解耦重任，彻底消除技术债 T3：
1. **创建 `BotAgent.Model` 工程**：封装大语言模型请求传输、OpenAI 协议客户端、流式解析及五档思考深度预算（`none`/`low`/`medium`/`high`/`xhigh`）治理；
2. **创建 `BotAgent.Storage` 工程**：封装 SQLite 数据库连接生命周期（`AppDatabase.cs`）、WAL 模式单进程锁、密钥与会话持久化、以及插件状态存储（`PluginStore`）；
3. **消除技术债 T3（巨石配置垂直切片）**：将 100+ 字段的 `AppSettings.cs` 垂直拆分为高内聚的强类型配置切片（`CoreOptions`、`ModelOptions`、`StorageOptions`），保留统一门面以实现无缝向下兼容；
4. **支持插件状态持久化**：在 SQLite `settings` 表中建立 `plugins_state` 键值驱动，为后续插件控制面（Agent 5）与门禁（Agent 4）提供可靠的存储支持。

---

## 2. 前置依赖与输入条件 (Prerequisites)

- **前置任务**：**Agent 1 (Wave 1) 必须已交付完成**。
- **输入契约**：已存在并编译通过的 `src/BotAgent.Core/bin/Release/net8.0/BotAgent.Core.dll`。
- **并行约束**：与 **Agent 3 (Platforms)** 并行执行，双方各司其职，**禁止跨边界修改对方独占文件**。

---

## 3. 文件读写权属清单 (File Ownership)

### 3.1 独占写权限（创建 / 迁移 / 修改）
- `src/BotAgent.Model/`（新建工程）：
  - `src/BotAgent.Model/BotAgent.Model.csproj`
  - 迁移自 `Adapters/Model/ModelTransport.cs`、`ImageDownloader.cs`
  - 迁移自 `Services/Agent/OpenAiClient.cs`
  - 新增 `ModelOptions.cs`
- `src/BotAgent.Storage/`（新建工程）：
  - `src/BotAgent.Storage/BotAgent.Storage.csproj`
  - 迁移自 `Adapters/Persistence/**`（`AppDatabase.cs`, `ConversationStore.cs`, `EpisodeStore.cs`, `SecretsStore.cs` 等全部仓储）
  - 新增 `PluginStore.cs`（插件开启/禁用状态持久化）
  - 新增 `StorageOptions.cs`
- `src/BotAgent.Headless/Services/AppSettings.cs`（垂直切片重构，保留对切片的统一聚合）
- `BotAgent.slnx`（挂载 `BotAgent.Model` 与 `BotAgent.Storage`）
- `src/BotAgent.Headless/BotAgent.Headless.csproj`（引用 Model 与 Storage）

### 3.2 只读 / 严禁触碰目录
- `src/BotAgent.Core/**`（仅只读引用）
- `src/BotAgent.Headless/Services/Platforms/**`、`OneBot/`、`Official/`（归属 Agent 3）
- `src/BotAgent.Headless/Services/Reply/**`（归属 Agent 4）
- `src/BotAgent.Headless/wwwroot/**`（归属 Agent 5）

---

## 4. 核心契约与设计规范 (Specifications)

### 4.1 五档思考深度与 Token 契约红线 (严禁破坏 PR #86 / #85 成果)
`BotAgent.Model` 中必须完整保留并精确实现五档思考深度映射规则：
```csharp
namespace BotAgent.Model;

public static class ThinkingBudgetResolver
{
    public static (string? Effort, int? MaxTokens) Resolve(string? depthSetting, bool isFastModel)
    {
        var raw = (depthSetting ?? "medium").Trim().ToLowerInvariant();
        return raw switch
        {
            "none"   => ("none", null),              // 必须保留显式 "none"，透传给 API
            "low"    => ("low", isFastModel ? 1024 : 2048),
            "medium" => ("medium", isFastModel ? 2048 : 4096),
            "high"   => ("high", isFastModel ? 4096 : 8192),
            "xhigh"  => ("high", isFastModel ? 8192 : 16384),
            _ => int.TryParse(raw, out var customTokens)
                ? (customTokens > 0 ? "custom" : "none", customTokens > 0 ? customTokens : null)
                : ("medium", isFastModel ? 2048 : 4096)
        };
    }
}
```
**安全红线**：`OpenAiClient.cs` 发送请求体时，`reasoning_effort: "none"` 必须被保留并在 JSON 报文中正常序列化（不得作为无效参数滤除）。

### 4.2 SQLite 存储与单进程 WAL 锁机制
`BotAgent.Storage` 中的 `AppDatabase.cs` 必须严守单进程部署原则：
- 连接串采用 `SqliteOpenMode.ReadWriteCreate` + `PRAGMA journal_mode=WAL;`；
- 所有写入操作通过 `AppDatabase.Write` 同步排队或 `WriteAsync` 串行执行，**禁止出现多进程并发争抢锁**。

### 4.3 插件持久化仓储 (`PluginStore.cs`)
在 `BotAgent.Storage` 中提供插件状态的轻量存取：
```csharp
namespace BotAgent.Storage;

public interface IPluginStore
{
    IReadOnlyDictionary<string, bool> LoadPluginStates();
    void SetPluginEnabled(string pluginId, bool enabled);
}
```
内部基于既有 `settings` 表或独立轻量表进行持久化，读写无锁，开箱即用。

### 4.4 配置切片架构 (消灭巨石 T3)
将原巨石配置按职责垂直解耦：
```
AppSettings (聚合门面，兼容旧代码属性调用)
   ├── CoreOptions     (DataDir, LogLevel, TimeZone)
   ├── ModelOptions    (BaseUrl, ApiKey, ModelName, FastModel, ThinkingDepth, Timeout)
   ├── StorageOptions  (DbPath, BackupIntervalHours)
   └── PlatformOptions (OneBot, Official, Feishu 配置切片)
```

---

## 5. 详细执行步骤 (Step-by-Step Instructions)

### 步骤 1：创建 `BotAgent.Model` 工程并迁移代码
1. 创建 `src/BotAgent.Model/BotAgent.Model.csproj`，声明依赖 `BotAgent.Core`；
2. 迁移 `Adapters/Model/ModelTransport.cs`、`ImageDownloader.cs`、`Services/Agent/OpenAiClient.cs`；
3. 将命名空间规范为 `BotAgent.Model`（或保留部分 `BotAgent.Adapters.Model` 别名兼容）。

### 步骤 2：创建 `BotAgent.Storage` 工程并迁移代码
1. 创建 `src/BotAgent.Storage/BotAgent.Storage.csproj`，引入 `Microsoft.Data.Sqlite` (Version 8.0.31) 与 `BotAgent.Core`；
2. 将 `src/BotAgent.Headless/Adapters/Persistence/**` 全量迁移至 `src/BotAgent.Storage/`；
3. 实现 `PluginStore.cs`；
4. 命名空间对齐为 `BotAgent.Storage`。

### 步骤 3：重构 `AppSettings.cs` 为垂直切片
1. 提取 `ModelOptions.cs` 与 `StorageOptions.cs`；
2. 在 `AppSettings.cs` 中以内嵌切片对象承载字段，对外暴露现有属性 getter/setter 转发，保证既有代码 0 破坏。

### 步骤 4：挂载解决方案与宿主依赖更新
1. 在 `BotAgent.slnx` 中增加 `BotAgent.Model` 与 `BotAgent.Storage`；
2. 更新 `BotAgent.Headless.csproj`，添加对 Model 与 Storage 的项目引用；
3. 清理 Headless 中已被迁出的旧源码文件。

---

## 6. 自测命令与验收标准 (Verification & DoD)

### 6.1 验证命令集
```pwsh
# 1. 验证 Model 独立构建
dotnet build src/BotAgent.Model/BotAgent.Model.csproj -c Release

# 2. 验证 Storage 独立构建
dotnet build src/BotAgent.Storage/BotAgent.Storage.csproj -c Release

# 3. 验证宿主编译无破坏
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# 4. 执行 SafetyProbe 探针（重点测试思考深度与模型序列化）
dotnet build tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll

# 5. 执行 S21 模型配置集成测试
$env:QQCHAT_IT_ONLY='s21'
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
```

### 6.2 交付验收准则 (DoD)
- [x] `BotAgent.Model` 与 `BotAgent.Storage` 独立构建 0 错误；
- [x] `SafetyProbe` 594 项全绿（特别是思考深度序列化与空值保护）；
- [x] S21 集成测试 37 项断言全部通过；
- [x] `AppSettings.cs` 完成解耦切片，各模块只引用自己的 Options；
- [x] 未破坏 SQLite 数据库模式迁移与单进程 WAL 机制。

---

## 7. 交付物与下游交接 (Handoff Deliverables)

1. 产出物：`BotAgent.Model.dll` 与 `BotAgent.Storage.dll`
2. 状态标记：**Wave 2 轨 A 完成（已就绪），等待与轨 B (Agent 3) 汇合，共同解锁 Wave 3 (Agent 4)**

---

## 8. 实施与交付执行记录 (Implementation & Verification Log)

### 8.1 实际工程变更与文件清单
1. **新建 `src/BotAgent.Model/`**：
   - `BotAgent.Model.csproj`：声明目标框架 `net8.0`，严格开启 `TreatWarningsAsErrors`，依赖 `BotAgent.Core`；
   - `ModelOptions.cs`：强类型配置切片（包含 `BaseUrl`, `ApiKey`, `Model`, `FastReply`, `FastModel`, `ThinkingBudget`, `ThinkingCustomBudget`, `FastThinkingBudget`, `FastThinkingCustomBudget`, `TimeoutSeconds`, 自适应采样等）；
   - `ThinkingBudgetResolver.cs`：五档思考深度解析器（完整覆盖 `none`, `low`, `medium`, `high`, `xhigh` 与自定义 token 预算）；
   - `ModelTransport.cs`、`ImageDownloader.cs`、`OpenAiClient.cs`：完整模型传输与客户端实现下沉至 Model 模块；
   - `PromptBuilder.cs`、`ProviderCircuitBreaker.cs`、`SafeUrl.cs`：配套提示词构建、熔断器与出网安全验证实现。
2. **新建 `src/BotAgent.Storage/`**：
   - `BotAgent.Storage.csproj`：引入 `Microsoft.Data.Sqlite 8.0.31` 与 `BotAgent.Core`；
   - `StorageOptions.cs`：强类型存储配置切片；
   - `IPluginStore.cs` 与 `PluginStore.cs`：基于 SQLite `settings` 表元数据的插件状态持久化仓储；
   - `AppDatabase.cs`、`ConversationStore.cs`、`SecretsStore.cs`、`EpisodeStore.cs`、`MemberProfileStore.cs`、`StickerStore.cs`、`MusicStore.cs`、`JargonStore.cs` 等仓储实现完整平移至 Storage 模块。
3. **重构 `src/BotAgent.Headless/Services/AppSettings.cs`**：
   - 内嵌持有多维度配置切片实例 `ModelOptions` 与 `StorageOptions`；
   - 保留原有属性 getter/setter 转发门面，既有业务代码零改动；
   - 在 `Snapshot()` 方法中显式克隆 `ModelOptions` 与 `StorageOptions`，确保热更新（`SettingsBox.Apply`）原子生效与在途请求线程安全。
4. **解决方案与工程挂载**：
   - `BotAgent.slnx` 挂载 `BotAgent.Model` 与 `BotAgent.Storage`；
   - `BotAgent.Headless.csproj` 增加对 Model 与 Storage 的项目引用。

### 8.2 核心技术红线与安全约束落实
- **五档思考深度透传**：`ThinkingBudgetResolver` 与 `OpenAiClient` 严格保障 `reasoning_effort: "none"` 显式序列化，绝不被判定为空值或无效值剔除。
- **单进程 WAL 锁机制**：`AppDatabase.Write` 增加静态重入锁 `lock (WriteGate)`，确保在 WAL 模式下所有写入操作在进程内串行排队，彻底杜绝 SQLite 忙锁争抢。
- **配置热更新不变量**：`SettingsBox` 副本隔离发布机制保持 100% 正常，切片对象随快照一同深拷贝。

### 8.3 真实测试验证执行凭证
1. **独立构建凭证**：
   - `dotnet build src/BotAgent.Model/BotAgent.Model.csproj -c Release`：0 警告，0 错误；
   - `dotnet build src/BotAgent.Storage/BotAgent.Storage.csproj -c Release`：0 警告，0 错误；
   - `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release`：0 警告，0 错误。
2. **核心安全探针 (`SafetyProbe`)**：
   - 执行指令：`dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll`
   - 执行结果：**通过 594，失败 0**（P2 决策、推理强度降级、DLP 审计、工具闸门与人物画像全绿）。
3. **集成测试 (`IntegrationHarness` S21 场景)**：
   - 执行指令：`$env:QQCHAT_IT_ONLY='s21'; dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll`
   - 执行结果：**通过 37，失败 0**（模型热切、密钥隔离、快速档独立思考预算、五档深度持久化与重启回显全绿）。
