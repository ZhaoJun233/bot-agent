# BotAgent 模块化重构与功能插件化演进方案

> **文档标识**：`docs/plans/modular-monolith-refactoring-plan.md`\
> **更新时间**：2026-10-06\
> **关联基线**：`mgyanik/bot-agent` `main`（提交：`1f3181e`，已解决 PR #86 冲突并对齐 PR #85 五档思考深度）\
> **核心目标**：融合 **PR #86 遗留待办**、**系统功能插件化深化需求** 与 **项目现状物理约束**，将 2.5 万行核心单体平稳演进为高内聚、易维护、零运行时额外开销的模块化单体（Modular Monolith）与微内核插件系统。

---

## 一、 最新项目现状与背景回顾 (Current Reality & Context)

### 1.1 PR #86 冲突解决与最新基线
在刚刚完成的工作中，针对 `ZhaoJun233/bot-agent/pull/86`（来自 `mgyanik:main`）与主仓库近期合并的 PR #85（五档思考深度设置）之间的核心文件冲突，已完成全量解决并成功推送至远端：
1. **解决冲突并对齐契约**：
   - `ModelTransport.cs`：统一融合了五档思考深度（`none`/`low`/`medium`/`high`/`xhigh`）与非数字自定义档位支持，同时为快速档保留 Token 预算；
   - `AppSettings.cs`：保留 18245 默认健康/面板端口迁移，同时修复了思考深度 `ResolveThinkingBudget` 缺失默认 Token 参数的问题；
   - `OpenAiClient.cs`：确保 `reasoning_effort: "none"` 显式透传，避免安全探针与集成测试空指针异常；
   - `index.html` & `app.js`：将主模型思考预算滑块升级为 `<select id="setThinkingBudget">` 下拉框，同时保留移动端 Tab Bar 流动胶囊、卡片头 `.card-head-text` 包裹与 Agent 运行配置模态框（`#agentConfigDialog`）；
   - `probe.mjs`：同时纳管五档思考深度自动化断言与移动端 UI 规范断言。
2. **测试验证全绿凭证**：
   - 前端探针 `node probe.mjs`：**350 项全通过**（0 失败）；
   - 架构棘轮探针 `ArchitectureProbe`：**92 项全通过**（0 失败）；
   - 核心安全探针 `SafetyProbe`：**594 项全通过**（0 失败）；
   - 集成测试：S21（模型思考配置 37 项）、S36（脱敏提示词 19 项）、S52（统一平台策略 119 项）全部通过。
   - GitHub Actions CI 检查通过。

---

## 二、 PR 遗留待办矩阵 (PR #86 Backlog Matrix)

根据 PR #86 的交接事实与审查记录，目前项目中存在以下明确搁置与待办事项，必须作为本次重构规划的正式输入：

| 分类 | 编号 | 事项名称 | 现状与核心需求 | 目标归宿模块 |
| :--- | :--- | :--- | :--- | :--- |
| **功能类** | **P1** | **底栏显隐机制重构** | 需求已完整设计，待施工：<br>1. 隐藏触发：向上滑、输入框获焦、静置 10s（首次）/ 5s（滚动后）；<br>2. 呼出触发：向下滑、点击非底栏空白区；<br>3. 切页复位：切换 5 个主页面时强制重置为显示；<br>4. 引导提醒：首次隐藏弹出常驻提醒条（Cookie 记录）。 | `BotAgent.Panel`<br>(`app.js`/`app.css`) |
| **功能类** | **P5** | **双保存按钮交互收敛** | 目前存在卡片独立保存与全局常驻保存按钮并存的冗余，容易让用户对保存生效范围产生歧义。统一收敛为底部常驻大保存条。 | `BotAgent.Panel`<br>(`index.html`) |
| **视觉类** | **P6/P7**| **插件中心卡片视觉优化** | 插件卡片贴边、外框内边距过紧、淡灰底框单调。需重新设计网格呼吸感、状态指示灯与分类徽标。 | `BotAgent.Panel`<br>(`app.css`) |
| **技术债** | **T1** | **探针与代码排版约定脆弱** | 已在本次冲突解决中修复（支持多空格匹配并对齐格式），后续需建立工程物理边界，摆脱纯正则脆弱测试。 | `ArchitectureProbe` |
| **技术债** | **T2** | **全站媒体查询重复** | `app.css` 中分散存在大量 `@media (max-width: 760px)` 与触屏规则，缺乏设计系统级断点变量收敛。 | `BotAgent.Panel` |
| **技术债** | **T3** | **配置关注点混杂** | `AppSettings.cs` 依然是个 100+ 字段的巨石类，思考预算、端口、平台、插件开关挤在同一文件。 | `BotAgent.Core` (垂直切片) |
| **验证类** | **V1-V3**| **Agent Dialog 模态全链路闭环** | 需补充自动化测试与真实交互验证：移动端与桌面端在模态打开、多次关闭归还 DOM、提交保存时的无损状态保持。 | `FrontendProbe` |

---

## 三、 功能插件化专项演进规划 (Feature Pluginization & Microkernel)

### 3.1 插件微内核现状勘测
在提交 `47afbdf` 中，系统初步搭建了微内核骨架（`Domain/Plugins/IBotPlugin.cs`、`IPluginRegistry.cs`、`PluginManager.cs`），并封装了 7 个预设插件（`music`、`voice`、`stickers`、`poke`、`vibes`、`profiles`、`research`）。

### 3.2 现有插件系统存在的“四大脱节”
目前插件系统仅完成了**只读展示（Read-Only Shell）**，未真正接管系统核心执行链：
1. **生命周期未挂载**：`PluginManager.StartAllAsync()` 和 `StopAllAsync()` 在系统启动（`Host/Program.cs`）与停止时从未被真正调用；预设插件内的 `InitializeAsync` 和 `ShutdownAsync` 为空实现；
2. **回复主链未做物理短路 (No Pipeline Gating)**：`ReplyPipeline.cs` 处理消息时，依然是无条件执行点歌、语音合成、戳一戳、表情包逻辑，**完全没有根据 `IPluginRegistry.IsEnabled(id)` 进行短路阻断**；
3. **管理面缺乏交互写路径**：面板的「插件与扩展」卡片仅为只读展示，没有暴露 `POST /api/plugins/{id}/toggle` 控制端点，用户无法在面板动态开关插件；
4. **代码依然紧耦合在核心单体中**：外围依赖（如网易云 API、TTS 客户端、表情包图片校验等）依然和核心对话流水线编译在一起，未能物理分包。

### 3.3 插件化落地目标设计

```
                            ┌────────────────────────┐
                            │    IPluginRegistry     │  <-- 核心只读注册表契约
                            └───────────┬────────────┘
                                        │
           ┌────────────────────────────┼────────────────────────────┐
           ▼                            ▼                            ▼
┌──────────────────────┐    ┌───────────────────────┐    ┌──────────────────────┐
│    ReplyPipeline     │    │     WebUiServer       │    │    CompositionRoot   │
│(收到消息前先查开关，   │    │(提供开关 API：        │    │(宿主启动统筹：        │
│未开启插件直接物理短路)│    │POST /api/plugins/...) │    │StartAll / StopAll)   │
└──────────────────────┘    └───────────────────────┘    └──────────────────────┘
```

#### 1) 领域契约与短路拦截设计
在 `BotAgent.Core` 中规范插件注册表与上下文扩展：
```csharp
namespace BotAgent.Domain.Plugins;

public interface IBotPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }
    PluginCategory Category { get; }
    bool IsPreset => true;

    Task InitializeAsync(PluginContext context, CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);
}
```
在 `ReplyPipeline.cs` 内部建立标准短路守卫：
```csharp
// 示例：戳一戳功能短路
if (!_plugins.IsEnabled("preset.feature.poke"))
{
    // 插件已禁用，彻底跳过心情状态机与戳一戳反馈
    return;
}
```

#### 2) 控制面板写路径与持久化联动
- 新增端点：`POST /api/plugins/{id}/enable`（接收 `{ "enabled": bool }`）；
- 持久化：在 SQLite `settings` 表中新增 `plugins_state` 键，或直接写入扩展字段；
- 热生效：调用 `PluginManager.SetEnabled(...)` 后即刻在内存生效，无需重启进程。

---

## 四、 整体重构架构：模块化单体 (Modular Monolith)

坚决坚守 **AWS EC2 913MB 内存与单进程部署** 的物理红线，将工程结构在编译期拆解为单向无环依赖图（DAG）：

```
                              ┌─────────────────────────┐
                              │    BotAgent.Headless    │  <-- [Host 启动可执行 Exe]
                              │  (Program/Composition)  │      内存 40~60MB，单一进程
                              └────────────┬────────────┘
                   ┌───────────────────────┼───────────────────────┐
                   ▼                       ▼                       ▼
          ┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐
          │  BotAgent.Panel  │    │ BotAgent.Engine  │    │BotAgent.Platforms│
          │ (Web API/静态SPA/│    │ (Reply主链/Turns/│    │(OneBot/Official/ │
          │  P1手势/插件卡片) │    │  插件门禁拦截)   │    │     Feishu)      │
          └────────┬─────────┘    └────────┬─────────┘    └────────┬─────────┘
                   │                       │                       │
                   │         ┌─────────────┴─────────────┐         │
                   │         ▼                           ▼         │
                   │  ┌──────────────┐            ┌──────────────┐ │
                   │  │BotAgent.Model│            │BotAgent.Store│ │
                   │  │(OpenAI/五档  │            │(SQLite/持久/ │ │
                   │  │ 思考深度)    │            │ 插件状态持久)│ │
                   │  └──────┬───────┘            └──────┬───────┘ │
                   │         │                           │         │
                   │         └─────────────┬─────────────┘         │
                   │                       ▼                       │
                   │           ┌───────────────────────┐           │
                   └──────────>│     BotAgent.Core     │<──────────┘
                               │(契约/领域模型/插件API)│
                               └───────────────────────┘
```

### 4.1 解决方案拓扑 (`BotAgent.slnx`)
将目前仅包含 3 个工程的 `BotAgent.slnx` 扩展为全景解决方案，纳管全部 6 个核心工程与 15 个测试项目：
```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/BotAgent.Core/BotAgent.Core.csproj" />
    <Project Path="src/BotAgent.Model/BotAgent.Model.csproj" />
    <Project Path="src/BotAgent.Platforms/BotAgent.Platforms.csproj" />
    <Project Path="src/BotAgent.Storage/BotAgent.Storage.csproj" />
    <Project Path="src/BotAgent.Engine/BotAgent.Engine.csproj" />
    <Project Path="src/BotAgent.Panel/BotAgent.Panel.csproj" />
    <Project Path="src/BotAgent.Headless/BotAgent.Headless.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj" />
    <Project Path="tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj" />
    <Project Path="tests/BotAgent.FrontendProbe/BotAgent.FrontendProbe.csproj" />
    <Project Path="tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj" />
    <Project Path="tests/BotAgent.BridgeProbe/BotAgent.BridgeProbe.csproj" />
    <Project Path="tests/BotAgent.SettingsScopeProbe/BotAgent.SettingsScopeProbe.csproj" />
    <Project Path="tests/BotAgent.ChaosFaultProbe/BotAgent.ChaosFaultProbe.csproj" />
    <Project Path="tests/BotAgent.ConcurrencyStressProbe/BotAgent.ConcurrencyStressProbe.csproj" />
    <Project Path="tests/BotAgent.DeadlineGateProbe/BotAgent.DeadlineGateProbe.csproj" />
    <Project Path="tests/BotAgent.FeishuRemediationProbe/BotAgent.FeishuRemediationProbe.csproj" />
    <Project Path="tests/BotAgent.ParticipationProbe/BotAgent.ParticipationProbe.csproj" />
    <Project Path="tests/BotAgent.PipelineEval/BotAgent.PipelineEval.csproj" />
    <Project Path="tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj" />
    <Project Path="tests/BotAgent.ReverseTransportProbe/BotAgent.ReverseTransportProbe.csproj" />
    <Project Path="tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj" />
    <Project Path="tests/BotAgent.SsrfProbe/BotAgent.SsrfProbe.csproj" />
  </Folder>
</Solution>
```

---

## 五、 分步骤实施路线图 (Milestones & Roadmap)

整个演进过程划分为 5 个步步为营、严禁破窗的里程碑：

```
[M1: 契约与插件下沉] -> [M2: 模型/存储与配置切片] -> [M3: 流水线插件门禁] -> [M4: 面板P1与插件写路径] -> [M5: 架构护栏与部署收口]
        │                         │                          │                         │                         │
  Core编译与探针绿           Safety/S21全绿            流水线短路测试全绿         前端350/P1手势通过           全套CI与发布校验通过
```

### 里程碑 1：核心契约下沉与插件微内核标准化 (Core & Plugin Contract)
- **工程动作**：
  1. 升级 `BotAgent.slnx`；创建 `src/BotAgent.Core/BotAgent.Core.csproj`；
  2. 将 `Domain/**` 下沉至 `BotAgent.Core`；
  3. 将 `Domain/Plugins/**`（`IBotPlugin`、`IPluginRegistry`、`PluginInfo`）标准化并完善生命周期契约；
  4. 将基础时间与出网实现（`SystemClock.cs`、`HttpFetcher.cs`）作为内部适配器移入 `BotAgent.Core`；
  5. `BotAgent.Headless` 引用 `BotAgent.Core`。
- **验收标准**：`dotnet build` 编译成功，`SafetyProbe`（594 项）与 `probe.mjs`（350 项）全绿。

### 里程碑 2：模型层独立与配置垂直切片 (Model, Storage & Config Slicing - 解决 T3)
- **工程动作**：
  1. 创建 `src/BotAgent.Model/`，迁移 `OpenAiClient.cs`、`ModelTransport.cs` 及思考深度解析逻辑；
  2. 创建 `src/BotAgent.Storage/`，迁移 SQLite 数据库实现、密钥库、配置表操作；
  3. **消灭技术债 T3**：将巨石 `AppSettings.cs` 拆分为领域切片（`CoreOptions`、`ModelOptions`、`StorageOptions`），保留统一转发门面向下兼容；
  4. 支持插件开关持久化存储（`PluginStore`，基于 `IPluginStore` 与 SQLite `settings` 表元数据驱动）；
  5. 落地单进程 WAL 模式排队写入锁机制（`AppDatabase.Write` 使用 `lock (WriteGate)` 保护）。
- **验收结果**：`BotAgent.Model.dll` 与 `BotAgent.Storage.dll` 独立编译 0 警告 0 错误；`SafetyProbe`（594 项全绿）及 `IntegrationHarness` 的 S21 思考深度测试（37 项全部通过）。状态已就绪，进入下一阶段。

### 里程碑 3：回复主链与插件门禁联动 (Engine & Plugin Gating)
- **工程动作**：
  1. 创建 `src/BotAgent.Engine/`，迁移 `ReplyPipeline.cs`、`AgentTurnLoop.cs`、`InlineTurnTools.cs`；
  2. 在 `ReplyPipeline` 各功能节点处引入 `IPluginRegistry.IsEnabled(pluginId)` 短路检查；
  3. 挂接生命周期：在 `Host/CompositionRoot.cs` 构建期注册预设插件，在 `Program.cs` 启动时调用 `StartAllAsync`，优雅退出时调用 `StopAllAsync`。
- **验收标准**：编写针对插件停用后的流水线单元测试，验证插件关闭时对应动作 100% 物理跳过。

### 里程碑 4：面板独立与交互待办闭环 (Panel & Backlogs P1, P5, P6, P7 - 解决 V1-V3)
- **工程动作**：
  1. 创建 `src/BotAgent.Panel/`，迁移 `WebUiServer.*.cs` 与静态资源 `wwwroot/`；
  2. **落实 PR 待办 P1（底栏显隐手势）**：在 `app.js` 实现触控上滑/获焦/静置隐藏、下滑/点击呼出、主页复位及常驻提醒条；
  3. **落实 PR 待办 P5（保存按钮收敛）**：清除分散的子保存入口，统一以常驻底栏大保存条交互；
  4. **落实 PR 待办 P6/P7（插件中心美化）**：增加卡片呼吸内边距、运行态指示灯与分类色标；
  5. **插件管理写路径**：提供 `POST /api/plugins/{id}/toggle` API 与面板切换开关；
  6. **落实 V1-V3 验证**：扩充 `probe.mjs`，覆盖 Agent Dialog 模态配置保存与关闭归还生命周期测试。
- **验收标准**：`check.py` 巡检通过，`probe.mjs` 测试集扩展至 360+ 项全绿。

### 里程碑 5：宿主精简与全景架构护栏 (Host Purification & Deployment Verification)
- **工程动作**：
  1. `BotAgent.Headless` 仅保留 `Program.cs`、`CompositionRoot.cs`，代码缩减至几百行；
  2. 更新 `ArchitectureProbe`：升级为校验多工程 DAG 单向依赖（如 Core 绝不依赖 Engine、Platforms 互不依赖）；
  3. 兼容性验证：本地模拟 `deploy-qqchat.py` 执行 `dotnet publish` 和 `tar -czf app.tar.gz`，验证 DLL 平铺输出与 `web/index.html` 嵌入依然 100% 成立。
- **验收标准**：`ArchitectureProbe`、`SafetyProbe`、`FrontendProbe` 与集成测试全景红线全绿，常驻工作集维持在 40~60MB。

---

## 六、 多 Agent 协同执行子任务文档集 (Multi-Agent Execution Runbooks)

为支撑多个开发 Agent 或人类工程师能够并行/流水线认领执行重构工程，本方案已完成物理拆解，生成一套结构化、可独立认领执行的具体任务指南文档，位于 `docs/plans/agents/` 目录：

| 文档编号 | 文档名称与路径 | 执行角色 | 所属波次 | 核心职责与交付物 |
| :--- | :--- | :--- | :--- | :--- |
| **00** | [`00-orchestration-and-dependency-graph.md`](agents/00-orchestration-and-dependency-graph.md) | **Orchestrator**<br>(架构总调度) | 全周期 | • 多 Agent 协作总纲、依赖 DAG 拓扑图与波次推进表<br>• 单一写权属目录隔离矩阵与防冲突规范<br>• 全局不可逾越红线（脱敏/单进程 913MB/思考深度）与回退机制 |
| **01** | [`01-agent-core-foundation.md`](agents/01-agent-core-foundation.md) | **Agent-01**<br>(Core-Foundation) | **Wave 1**<br>(独占基石) | • 创建 `BotAgent.Core` 工程与更新 `BotAgent.slnx`<br>• 下沉 `Domain/**` 全量契约与值对象<br>• 扩展微内核 `IBotPlugin`，新增 `PluginContext` 生命周期上下文 |
| **02** | [`02-agent-model-and-storage.md`](agents/02-agent-model-and-storage.md) | **Agent-02**<br>(Model-Storage) | **Wave 2**<br>(双轨并行 A) | • 创建 `BotAgent.Model`（迁移 OpenAI/五档思考深度预算）<br>• 创建 `BotAgent.Storage`（迁移 SQLite、WAL 单进程锁、插件持久化 `PluginStore`）<br>• 垂直切片消除巨石 `AppSettings.cs`（技术债 T3） |
| **03** | [`03-agent-platforms-extraction.md`](agents/03-agent-platforms-extraction.md) | **Agent-03**<br>(Platforms) | **Wave 2**<br>(双轨并行 B) | • 创建 `BotAgent.Platforms` 工程<br>• 隔离迁移 OneBot (v11/NapCat)、Official (开放平台)、Feishu 与 Local 通道<br>• 集中收敛 `PlatformPolicyResolver` 与统一白名单治理 |
| **04** | [`04-agent-engine-and-gating.md`](agents/04-agent-engine-and-gating.md) | **Agent-04**<br>(Engine-Gating) | **Wave 3**<br>(核心编排) | • 创建 `BotAgent.Engine` 工程（迁移 ReplyPipeline、AgentTurnLoop）<br>• 植入插件微内核物理短路门禁（解决插件化脱节 2）<br>• 挂载系统启动/停止的插件生命周期 `StartAllAsync`/`StopAllAsync`（解决脱节 1） |
| **05** | [`05-agent-panel-and-ux-backlog.md`](agents/05-agent-panel-and-ux-backlog.md) | **Agent-05**<br>(Panel-UX) | **Wave 4**<br>(控制面交互) | • 创建 `BotAgent.Panel` 工程（WebUiServer 与嵌入式 SPA）<br>• 增加插件管理写端点 `POST /api/plugins/{id}/enable`（解决脱节 3）<br>• 闭环 PR #86 遗留待办：P1 底栏手势、P5 保存按钮收敛、P6/P7 插件中心美化<br>• 扩充 `probe.mjs` 前端测试覆盖 Agent Dialog 模态闭环 (V1-V3) |
| **06** | [`06-agent-host-guardrails-and-deploy.md`](agents/06-agent-host-guardrails-and-deploy.md) | **Agent-06**<br>(Host-QA-Deploy) | **Wave 5**<br>(终局收口) | • 净化宿主 `BotAgent.Headless` 为极简启动器（`Program.cs` / `CompositionRoot.cs`）<br>• 升级 `ArchitectureProbe` 95+ 项断言（增加 DAG 单向依赖防破窗校验）<br>• 全景探针与 S21/S36/S52 集成测试回归<br>• 验证本地 `publish` 平铺产物与 AWS 913MB 单进程部署红线 |
