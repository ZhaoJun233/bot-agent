# Agent 4 执行手册：回复流水线与插件物理门禁短路

> **文档标识**：`docs/plans/agents/04-agent-engine-and-gating.md`\
> **执行代号**：`Agent-04` (Engine-Gating)\
> **所属波次**：**Wave 3（核心编排波次）**\
> **所属方案**：BotAgent 模块化重构与功能插件化演进方案 (`modular-monolith-refactoring-plan.md`)\
> **更新时间**：2026-10-06\

---

## 1. 任务目标与范围 (Objective & Scope)

Agent 4 承担系统对话业务核心与流水线装配重任，彻底解决插件微内核的**两大脱节**（生命周期未挂载、流水线未物理短路）：
1. **创建 `BotAgent.Engine` 工程**：封装消息回复处理流水线 (`ReplyPipeline.cs`)、多轮 Agent 循环 (`AgentTurnLoop.cs`)、内置工具调度 (`InlineTurnTools.cs`) 等核心业务流水线；
2. **实现插件物理门禁短路 (Pipeline Gating - 解决脱节 2)**：在 `ReplyPipeline` 注入 `IPluginRegistry`，对 7 大预设插件（`music`、`voice`、`stickers`、`poke`、`vibes`、`profiles`、`research`）建立物理短路守卫，当插件被禁用时，彻底拦截对应分支，零外部 IO，零计算开销；
3. **挂接插件微内核生命周期 (Lifecycle Hooking - 解决脱节 1)**：打通 `PluginManager.StartAllAsync` 与 `StopAllAsync`，在系统启动与关闭时真正触发各插件的生命周期钩子；
4. **保持既有对话决策与模型输出一致性**：完整保留系统既有的采样策略、情绪状态机与安全审核机制。

---

## 2. 前置依赖与输入条件 (Prerequisites)

- **前置任务**：**Wave 2 的 Agent 2 (Model-Storage) 与 Agent 3 (Platforms) 必须全部完成并通过 DoD 验收**。
- **输入契约**：
  - `BotAgent.Core.dll`（领域模型与 `IPluginRegistry` 契约）
  - `BotAgent.Model.dll`（`OpenAiClient` 与模型传输）
  - `BotAgent.Storage.dll`（数据库与仓储接口）
  - `BotAgent.Platforms.dll`（通道抽象）

---

## 3. 文件读写权属清单 (File Ownership)

### 3.1 独占写权限（创建 / 迁移 / 修改）
- `src/BotAgent.Engine/`（新建工程）：
  - `src/BotAgent.Engine/BotAgent.Engine.csproj`
  - 迁移自 `Services/Reply/**`（`ReplyPipeline.cs`、`ReplyPipeline.*.cs`、`PlainSender.cs` 等）
  - 迁移自 `Services/Agent/**`（`AgentTurnLoop.cs`、`InlineTurnTools.cs`、`AgentOrchestrator.cs` 等，注意排除已迁往 Model 的 `OpenAiClient.cs`）
  - 迁移自 `Services/Poke/**`、`Services/Voice/**`、`Services/Music/**`、`Services/Stickers/**`
  - 迁移自 `Services/Plugins/**`（`PluginManager.cs` 与 `Presets/**`）
- `BotAgent.slnx`（挂载 `BotAgent.Engine`）
- `src/BotAgent.Headless/BotAgent.Headless.csproj`（引用 Engine）

### 3.2 只读 / 严禁触碰目录
- `src/BotAgent.Core/**`（只读）
- `src/BotAgent.Model/**`（只读）
- `src/BotAgent.Storage/**`（只读）
- `src/BotAgent.Platforms/**`（只读）
- `src/BotAgent.Headless/wwwroot/**`（归属 Agent 5）

---

## 4. 核心契约与设计规范 (Specifications)

### 4.1 `BotAgent.Engine.csproj` 定义
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>BotAgent.Engine</RootNamespace>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\BotAgent.Core\BotAgent.Core.csproj" />
    <ProjectReference Include="..\BotAgent.Model\BotAgent.Model.csproj" />
    <ProjectReference Include="..\BotAgent.Storage\BotAgent.Storage.csproj" />
    <ProjectReference Include="..\BotAgent.Platforms\BotAgent.Platforms.csproj" />
  </ItemGroup>
</Project>
```

### 4.2 插件物理短路门禁设计 (Pipeline Gating)
在 `ReplyPipeline` 构造函数注入 `IPluginRegistry plugins`，并在各个媒体与特性处理节点加入严格门禁检查：

```csharp
// 1. 点歌处理门禁
private IReadOnlyList<MusicShare>? HandleInboundMedia(BotConversation conversation, QqChatMessage msg)
{
    var musicEnabled = _plugins.IsEnabled("preset.feature.music")
        && policy.Feature("music", _settings.EnableMusic).Enabled;
    if (!musicEnabled)
    {
        // 插件已禁用：物理短路，不执行解析，不分配异步任务
        return null;
    }
    // ... 原有点歌执行逻辑
}

// 2. 表情包库选择门禁
if (_plugins.IsEnabled("preset.feature.stickers")
    && platformPolicy.Feature("stickers", snapshot.EnableStickers).Enabled
    && snapshot.StickerLibraryMax > 0)
{
    // 允许选取表情候选
}

// 3. 戳一戳上下文门禁
var pokeContext = _plugins.IsEnabled("preset.feature.poke")
    && platformPolicy.Feature("poke", snapshot.EnablePoke).Enabled
    && _poke.RecentlyPoked(conversation.SourceKey, TimeSpan.FromMinutes(10), Clock.Now);

// 4. 云端语音合成门禁
if (!_plugins.IsEnabled("preset.feature.voice") || !snapshot.EnableVoice)
{
    // 跳过语音合成与发送
}
```

### 4.3 生命周期与启动挂载 (Lifecycle Hooking)
确保在系统编排层或引擎启动时，统一驱动生命周期：
```csharp
public async Task StartAsync(CancellationToken ct)
{
    var context = new PluginContext(_serviceProvider);
    await _pluginManager.StartAllAsync(context, ct);
}

public async Task StopAsync(CancellationToken ct)
{
    await _pluginManager.StopAllAsync(ct);
}
```

---

## 5. 详细执行步骤 (Step-by-Step Instructions)

### 步骤 1：创建 `BotAgent.Engine` 工程
1. 创建目录 `src/BotAgent.Engine` 并写入 `BotAgent.Engine.csproj`；
2. 将工程挂入 `BotAgent.slnx`。

### 步骤 2：迁移流水线与预设特性代码
1. 将 `Services/Reply/` 迁移至 `src/BotAgent.Engine/Reply/`；
2. 将 `Services/Agent/`（排除 OpenAiClient）迁移至 `src/BotAgent.Engine/Agent/`；
3. 将 `Services/Music/`、`Services/Voice/`、`Services/Poke/`、`Services/Stickers/` 迁移至 `src/BotAgent.Engine/Features/`；
4. 将 `Services/Plugins/` 迁移至 `src/BotAgent.Engine/Plugins/`。

### 步骤 3：植入插件短路检查 (Pipeline Gating)
在 `ReplyPipeline.cs` 中：
- 注入 `IPluginRegistry _plugins`；
- 在音乐、语音、表情包、戳一戳、人设档案与氛围渲染 6 处关键分支加入 `_plugins.IsEnabled(...)` 判断；
- 当返回 `false` 时，执行无开销早退。

### 步骤 4：宿主绑定与编译验证
1. 在 `BotAgent.Headless.csproj` 中添加 `<ProjectReference Include="..\BotAgent.Engine\BotAgent.Engine.csproj" />`；
2. 清理 Headless 中已被移出的源码目录。

---

## 6. 自测命令与验收标准 (Verification & DoD)

### 6.1 验证命令集
```pwsh
# 1. 验证 Engine 独立编译
dotnet build src/BotAgent.Engine/BotAgent.Engine.csproj -c Release

# 2. 验证宿主整体编译
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# 3. 运行对话与回复测试套件 (S36 场景)
$env:QQCHAT_IT_ONLY='s36'
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll

# 4. 运行安全审计与隐私探针 (594 项)
dotnet build tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll
```

### 6.2 交付验收准则 (DoD)
- [ ] `BotAgent.Engine` 独立编译 0 报错 0 警告；
- [ ] 针对已禁用插件的场景，对应业务功能 100% 物理跳过，无无效 API 调用；
- [ ] `SafetyProbe` 594 项全绿；
- [ ] S36 集成测试全部通过；
- [ ] 插件启动与停止生命周期钩子正常受控执行。

---

## 7. 交付物与下游交接 (Handoff Deliverables)

1. 产出物：`BotAgent.Engine.dll`
2. 状态标记：**Wave 3 完成，正式解锁 Wave 4（Agent 5: Panel-UX）**
