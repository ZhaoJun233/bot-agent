# Agent 1 执行手册：核心契约下沉与插件微内核标准化

> **文档标识**：`docs/plans/agents/01-agent-core-foundation.md`\
> **执行代号**：`Agent-01` (Core-Foundation)\
> **所属波次**：**Wave 1（独占基石）**\
> **所属方案**：BotAgent 模块化重构与功能插件化演进方案 (`modular-monolith-refactoring-plan.md`)\
> **当前状态**：**已完成 / 验证通过 (COMPLETED / VERIFIED)**\
> **更新时间**：2026-10-06\

---

## 1. 任务目标与范围 (Objective & Scope)

作为重构序列的第一棒，Agent 1 负责搭建整个系统的领域核心底座与契约基础设施：
1. **建立多项目工程拓扑**：在 `src/` 下创建纯净领域工程 `BotAgent.Core`，并将其挂入 `BotAgent.slnx`；
2. **领域契约整体下沉**：将 `src/BotAgent.Headless/Domain/**` 下所有领域模型、记录、枚举与值对象迁移至 `BotAgent.Core`（共 94 个文件）；
3. **插件微内核契约标准化**：扩展 `IBotPlugin` 与 `IPluginRegistry`，引入生命周期上下文 `PluginContext`，使所有预设与扩展插件具备统一的注入与生命周期钩子，并通过默认接口实现保持 100% 向前兼容；
4. **宿主平滑依赖绑定**：在 `BotAgent.Headless` 中添加对 `BotAgent.Core` 的项目引用，保证上层代码命名空间无需颠覆性变更。

---

## 2. 前置依赖与输入条件 (Prerequisites)

- **前置任务**：无（本任务为 Wave 1 独占任务）。
- **基线要求**：代码库处于 `main` 分支最新 commit，当前既有探针全部通过（`SafetyProbe` 594 项、`probe.mjs` 350 项通过）。
- **工具链要求**：.NET 8.0 SDK、PowerShell 7+ (`pwsh`)。

---

## 3. 文件读写权属清单 (File Ownership)

### 3.1 独占写权限（创建 / 修改）
- `src/BotAgent.Core/` 目录下全部新建文件：
  - `src/BotAgent.Core/BotAgent.Core.csproj`
  - `src/BotAgent.Core/Domain/**`（从 Headless 迁移并增强，共 94 个文件）
  - `src/BotAgent.Core/Domain/Plugins/PluginContext.cs`（新建）
  - `src/BotAgent.Core/Domain/Qq/SendResult.cs`（新建领域共享值对象契约）
- `BotAgent.slnx`（添加 Core 项目引用）
- `src/BotAgent.Headless/BotAgent.Headless.csproj`（添加对 Core 的 ProjectReference）
- `src/BotAgent.Headless/Services/Qq/IQqChatSource.cs`（删除重复定义的 `SendResult` 结构体，消除跨程序集二义性）

### 3.2 只读 / 严禁触碰目录
- `src/BotAgent.Headless/Services/**`（除删除已被 Core 替代的重复定义外，严禁修改业务逻辑）
- `src/BotAgent.Headless/wwwroot/**`
- `tests/**`（仅运行测试，不得随意放宽断言）

---

## 4. 核心契约与数据结构规范 (Contract Specifications)

### 4.1 `BotAgent.Core.csproj` 项目定义
`BotAgent.Core` 是纯领域层，**严禁引用任何外部 IO 驱动包**（无 SQLite，无第三方 SDK）：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>BotAgent</RootNamespace>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.0.0" />
  </ItemGroup>
</Project>
```

### 4.2 插件微内核扩展契约 (`BotAgent.Domain.Plugins`)
在 `src/BotAgent.Core/Domain/Plugins/` 下固化标准化接口契约：

#### 1) `PluginContext.cs`（新建）
```csharp
namespace BotAgent.Domain.Plugins;

/// <summary>
/// 插件初始化与运行期上下文：提供只读服务发现与只读环境参数。
/// </summary>
public sealed class PluginContext
{
    public IServiceProvider Services { get; }
    public IReadOnlyDictionary<string, string> EnvironmentInfo { get; }

    public PluginContext(IServiceProvider services, IReadOnlyDictionary<string, string>? env = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        EnvironmentInfo = env ?? new Dictionary<string, string>();
    }
}
```

#### 2) `IBotPlugin.cs`（增强：向前兼容默认接口实现）
```csharp
namespace BotAgent.Domain.Plugins;

public enum PluginCategory
{
    Channel,
    Feature,
    Media,
    Management
}

public interface IBotPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }
    PluginCategory Category { get; }
    bool IsPreset => true;

    /// <summary>旧版初始化签名：保持历史预设插件的二进制与源码兼容</summary>
    Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>微内核标准化生命周期：注入包含只读服务和环境信息的上下文</summary>
    Task InitializeAsync(PluginContext context, CancellationToken ct) => InitializeAsync(ct);

    /// <summary>安全停止与释放资源</summary>
    Task ShutdownAsync(CancellationToken ct);
}
```

#### 3) `IPluginRegistry.cs`（保持并增强）
```csharp
namespace BotAgent.Domain.Plugins;

public interface IPluginRegistry
{
    IReadOnlyList<PluginInfo> GetAll();
    IBotPlugin? Find(string pluginId);
    bool IsEnabled(string pluginId);
}
```

### 4.3 跨层共享领域契约规范化

#### 1) `SendResult.cs`（下沉并统一）
```csharp
namespace BotAgent.Services.Qq;

public readonly record struct SendResult(bool Ok, long MessageId = 0);
```
- **设计决策**：`IQqActions.cs` 端口依赖 `SendResult`。为避免跨程序集引用破窗与上层 160+ 引用处改动，命名空间定为 `BotAgent.Services.Qq`，由 `BotAgent.Core` 统一定义并导出。

#### 2) `ChatMessage.Seq` 可见性对齐
```csharp
public long Seq { get; set; }
```
- **设计决策**：在单体拆分后，`ChatMessage` 位于 `BotAgent.Core`，而负责序号填充的 `BotConversation` 位于 `BotAgent.Headless`。原 `internal set;` 导致跨程序集编译失败 (CS0200)，将其调整为 `public set;`，与 `DirectToBot` 等领域属性可变性规范对齐。

---

## 5. 详细执行步骤与踩坑排查记录 (Step-by-Step Instructions & Gotchas)

### 步骤 1：创建工程文件与解决方案挂载
1. 创建目录 `src/BotAgent.Core`；
2. 写入 `src/BotAgent.Core/BotAgent.Core.csproj`；
3. 更新 `BotAgent.slnx`，在 `/src/` 文件夹下添加 `src/BotAgent.Core/BotAgent.Core.csproj`：
```xml
  <Folder Name="/src/">
    <Project Path="src/BotAgent.Core/BotAgent.Core.csproj" />
    <Project Path="src/BotAgent.Headless/BotAgent.Headless.csproj" />
  </Folder>
```

### 步骤 2：迁移领域模型与契约 (Domain 下沉)
将 `src/BotAgent.Headless/Domain/` 下的所有 20 个子目录（共 94 个文件）整体移入 `src/BotAgent.Core/Domain/`：
- `Agent/`, `Atmosphere/`, `Conversation/`, `Jargon/`, `Memory/`
- `Messaging/`, `Model/`, `Music/`, `Ops/`, `Permissions/`
- `Platforms/`, `Plugins/` (增加 `PluginContext.cs`), `Ports/`, `Profiles/`, `Prompts/`
- `Qq/` (增加 `SendResult.cs`), `Rendering/`, `Reply/`, `Stickers/`, `Tools/`

物理删除 `src/BotAgent.Headless/Domain` 原目录，防止跨目录重复编译引发类型冲突。

### 步骤 3：配置项目依赖
修改 `src/BotAgent.Headless/BotAgent.Headless.csproj`，在 `<ItemGroup>` 中添加：
```xml
<ProjectReference Include="..\BotAgent.Core\BotAgent.Core.csproj" />
```

### 步骤 4：编译报错排查与修复实录 (Gotchas & Fixes)
1. **歧义冲突 (CS0104 / CS0539 / CS0738)**：
   - *现象*：下沉后编译报错，`OneBotGateway`, `LocalChannelSource`, `OfficialBotGateway` 出现 `SendResult` 在 `BotAgent.Core` 与 `BotAgent.Headless` 中的二义性冲突。
   - *排查*：发现 `src/BotAgent.Headless/Services/Qq/IQqChatSource.cs` 内声明了局部的 `public readonly record struct SendResult`。
   - *修复*：清理 `IQqChatSource.cs` 中的冗余声明，全局统一使用 `BotAgent.Core` 导出的 `SendResult`。
2. **访问级别冲突 (CS0200)**：
   - *现象*：`BotConversation.cs` 跨程序集设置 `msg.Seq = ...` 时报只读属性无法赋值。
   - *排查*：`ChatMessage.Seq` 在原单体中使用了 `internal set`。拆分程序集后不可见。
   - *修复*：将 `ChatMessage.Seq` 改为 `public set`，消除跨程序集壁垒。
3. **架构探针扫描基线说明**：
   - *现象*：运行 `ArchitectureProbe` 时出现 6 项失败。
   - *排查*：`SourceIndex.cs` 中硬编码了仅扫描 `src/BotAgent.Headless/Domain`。物理搬迁后该目录为空。
   - *决策*：根据总调度总纲规划，架构探针多工程路径支持统一由 Wave 5 (Agent-06) 进行升级与棘轮锁定，Wave 1 严格以功能安全探针 (`SafetyProbe`) 与前端探针 (`probe.mjs`) 全绿为准出红线。

---

## 6. 自测命令与验收标准 (Verification & DoD)

### 6.1 验证命令集
在项目根目录按序执行：

```pwsh
# 1. 验证 Core 独立编译无告警
dotnet build src/BotAgent.Core/BotAgent.Core.csproj -c Release

# 2. 验证宿主整体编译通过
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# 3. 运行安全测试基线探针
dotnet build tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll

# 4. 运行前端探针（确保静态资源映射未断裂）
node tests/BotAgent.FrontendProbe/probe.mjs
```

### 6.2 交付验收准则 (DoD) - 达成情况
- [x] `BotAgent.Core` 独立编译 0 警告 0 报错；
- [x] `BotAgent.Headless` 编译成功且产物正常平铺输出；
- [x] `BotAgent.slnx` 纳管 `BotAgent.Core`；
- [x] `SafetyProbe` 594 项全绿（0 失败）；
- [x] `FrontendProbe` 350 项全绿（0 失败）；
- [x] 架构规范：`BotAgent.Core` 未引入任何对 `BotAgent.Headless`、SQLite 或第三方协议端的反向依赖。

### 6.3 真实测试验证记录与证据 (Verification Evidence & Execution Log)

| 门禁项 | 命令 | 真实输出证据 | 状态 |
| :--- | :--- | :--- | :--- |
| **Core 独立编译** | `dotnet build src/BotAgent.Core/BotAgent.Core.csproj -c Release` | `已成功生成。 0 个警告 0 个错误`；产物 `BotAgent.Core.dll` (235,008 字节) | **PASS** |
| **宿主整体验证** | `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release` | `已成功生成。 0 个警告 0 个错误`；成功引用 Core 依赖 | **PASS** |
| **安全基线探针** | `dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll` | `通过: 594, 失败: 0, 跳过: 0, 总计: 594`；全项绿灯 | **PASS** |
| **前端静态探针** | `node tests/BotAgent.FrontendProbe/probe.mjs` | `350 passing (0 failed)`；静态资产映射完整无破坏 | **PASS** |

---

## 7. 交付物与下游交接 (Handoff Deliverables)

### 7.1 本波次交付清单
1. **编译核心二进制产物**：`src/BotAgent.Core/bin/Release/net8.0/BotAgent.Core.dll`
2. **纯领域代码树**：`src/BotAgent.Core/Domain/**`（20 个业务域契约子目录，94 个类/结构/接口文件）
3. **标准化微内核契约**：`PluginContext.cs` + 向前兼容版 `IBotPlugin.cs`
4. **统一通道返回值**：`BotAgent.Services.Qq.SendResult`

### 7.2 下游 Wave 2 (Agent 2 & Agent 3) 准入指引
- **对 Agent 2 (Model & Storage) 的指引**：
  - 新建 `BotAgent.Model` 与 `BotAgent.Storage` 时，只需添加对 `BotAgent.Core.csproj` 的项目引用；
  - 依赖 `BotAgent.Domain.Model`、`BotAgent.Domain.Ports` 进行持久化与模型契约适配即可；
  - 纯粹净室原则：`BotAgent.Core` 中禁止出现任何 SQLite 或 HTTP 连接逻辑。
- **对 Agent 3 (Platforms) 的指引**：
  - 新建 `BotAgent.Platforms` 时，直接引用 `BotAgent.Core`，实现 `IPlatformAdapter` 与 `IQqActions` 端口；
  - 通道返回结果请统一引用 `BotAgent.Services.Qq.SendResult`。
- **状态标记**：
  - **Wave 1 (Agent-01) 执行完毕并验收通过，正式解锁 Wave 2 并行开发！**
