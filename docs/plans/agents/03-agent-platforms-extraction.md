# Agent 3 执行手册：通信平台适配器独立分包

> **文档标识**：`docs/plans/agents/03-agent-platforms-extraction.md`\
> **执行代号**：`Agent-03` (Platforms)\
> **所属波次**：**Wave 2（双轨并行轨 B，与 Agent 2 并行）**\
> **所属方案**：BotAgent 模块化重构与功能插件化演进方案 (`modular-monolith-refactoring-plan.md`)\
> **更新时间**：2026-10-06\
> **当前状态**：暂停于“平台源码物理迁移与宿主配置桥接完成，最终验收待补齐”；续接入口见 §8。\
> **推进门禁**：尚未宣布 Wave 2 轨 B 完成，不据此解锁 Wave 3。

---

## 1. 任务目标与范围 (Objective & Scope)

Agent 3 负责对所有外部聊天协议端进行解耦和物理分包：
1. **创建 `BotAgent.Platforms` 工程**：封装 QQ 私域 (OneBot v11 / NapCat)、QQ 官方开放平台 (OfficialBotGateway)、飞书 Bot (FeishuBotGateway) 以及本地测试通道 (LocalChannelSource)；
2. **迁移与收敛平台统一策略引擎**：集中管理 `PlatformPolicyResolver`、`PlatformRegistry`、`PlatformSwitchSettings` 与统一白名单治理逻辑；
3. **平台与业务主链路契约隔离**：平台适配器直接引用 `BotAgent.Core` 导出的 `IPlatformAdapter`、`IPlatformMessenger`、`IQqActions` 端口；兼容外观 `IQqChatSource` 目前位于 Platforms，统一使用 Core 导出的 `BotAgent.Services.Qq.SendResult`，不得重复定义。各网关端口实现的当前差异见 §8，严禁与核心回复逻辑直接深度强耦合；
4. **保留并对齐通道隔离红线**：严格保留官方通道、飞书通道与私域通道的会话隔离与白名单独立判定机制。

---

## 2. 前置依赖与输入条件 (Prerequisites)

- **前置任务**：**Agent 1 (Wave 1) 必须已交付完成**。
- **输入契约**：已存在并编译通过的 `src/BotAgent.Core/bin/Release/net8.0/BotAgent.Core.dll`。
- **并行约束**：与 **Agent 2 (Model-Storage)** 并行执行，互不触碰对方代码。

---

## 3. 文件读写权属清单 (File Ownership)

### 3.1 独占写权限（创建 / 迁移 / 修改）
- `src/BotAgent.Platforms/`（新建工程）：
  - `src/BotAgent.Platforms/BotAgent.Platforms.csproj`
  - 迁移自 `Services/OneBot/**`（`OneBotGateway.cs`, `HttpTransport.cs`, `ReverseWsTransport.cs` 等）
  - 迁移自 `Services/Official/**`（`OfficialBotGateway.cs`）
  - 迁移自 `Adapters/Platforms/Feishu/**`（`FeishuBotGateway.cs`）
  - 迁移自 `Services/Local/**`（`LocalChannelSource.cs`）
  - 迁移自 `Services/Platforms/**`（`PlatformRegistry.cs`, `PlatformPolicyResolver.cs`, `PlatformSwitchSettings.cs`）
- `BotAgent.slnx`（挂载 `BotAgent.Platforms`）
- `src/BotAgent.Headless/BotAgent.Headless.csproj`（引用 Platforms）

### 3.2 只读 / 严禁触碰目录
- `src/BotAgent.Core/**`（仅只读引用）
- `src/BotAgent.Headless/Adapters/Model/**`、`Services/Agent/OpenAiClient.cs`（归属 Agent 2）
- `src/BotAgent.Headless/Adapters/Persistence/**`（归属 Agent 2）
- `src/BotAgent.Headless/Services/Reply/**`（归属 Agent 4）
- `src/BotAgent.Headless/wwwroot/**`（归属 Agent 5）

---

## 4. 核心契约与设计规范 (Specifications)

### 4.1 `BotAgent.Platforms.csproj` 项目定义
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>BotAgent.Platforms</RootNamespace>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\BotAgent.Core\BotAgent.Core.csproj" />
  </ItemGroup>
</Project>
```

实际落地工程另引用 `Microsoft.Data.Sqlite 8.0.31`（飞书 Webhook 持久化去重），全局导入 `BotAgent.Platforms` 与 `BotAgent.Services`。**项目引用**仍仅有 Core；NuGet 依赖与项目依赖分别核对。迁移源码保留原有 `BotAgent.Services.*` / `BotAgent.Adapters.*` 命名空间，以降低宿主调用点改动；目录迁移不等于命名空间整体改名。

### 4.2 统一平台策略契约 (Platform Policy Isolation)
确保 `PlatformPolicyResolver` 与 `PlatformRegistry` 统一注册 4 大平台通道：
- `PlatformId.QqPrivate` ("private")
- `PlatformId.QqOfficial` ("official")
- `PlatformId.Feishu` ("feishu")
- `PlatformId.Local` ("local")

**红线要求**：
- 官方通道的 `openid` 映射保持为 `8e15` 起始虚拟号段；
- 飞书通道保持 `FeishuIdMap`（`6e15` 起始虚拟号段）独立隔离；
- `official:` 与 `feishu:` 会话前缀必须严格隔离，不可与 QQ 私域会话串联或互相泄露。

---

## 5. 详细执行步骤 (Step-by-Step Instructions)

### 步骤 1：创建 `BotAgent.Platforms` 工程文件
1. 创建目录 `src/BotAgent.Platforms`；
2. 写入 `src/BotAgent.Platforms/BotAgent.Platforms.csproj`；
3. 将项目添加进 `BotAgent.slnx`。

### 步骤 2：迁移适配器源码
1. 迁移 OneBot 传输层：将 `Services/OneBot/` 移入 `src/BotAgent.Platforms/OneBot/`；
2. 迁移 Official 网关：将 `Services/Official/` 移入 `src/BotAgent.Platforms/Official/`；
3. 迁移 Feishu 网关：将 `Adapters/Platforms/Feishu/` 移入 `src/BotAgent.Platforms/Feishu/`；
4. 迁移 Local 通道：将 `Services/Local/` 移入 `src/BotAgent.Platforms/Local/`；
5. 迁移平台策略：将 `Services/Platforms/` 移入 `src/BotAgent.Platforms/Governance/`。

### 步骤 3：宿主引用与清理
1. 在 `BotAgent.Headless.csproj` 中添加 `<ProjectReference Include="..\BotAgent.Platforms\BotAgent.Platforms.csproj" />`；
2. 清理 Headless 中已被移出的旧目录；
3. 确保对外保留的类型和引用使用 `using BotAgent.Platforms;` 或对应命名空间对齐。

---

## 6. 自测命令与验收标准 (Verification & DoD)

### 6.1 验证命令集
```pwsh
# 1. 验证 Platforms 独立构建
dotnet build src/BotAgent.Platforms/BotAgent.Platforms.csproj -c Release

# 2. 验证宿主构建
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# 3. 运行 S52 统一平台策略回归测试 (119 项断言)
$env:QQCHAT_IT_ONLY='s52'
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll

# 4. 运行 Feishu 专项探针
dotnet build tests/BotAgent.FeishuRemediationProbe/BotAgent.FeishuRemediationProbe.csproj -c Release
dotnet tests/BotAgent.FeishuRemediationProbe/bin/Release/net8.0/BotAgent.FeishuRemediationProbe.dll
```

### 6.2 交付验收准则 (DoD)
- [ ] `BotAgent.Platforms` 独立编译 0 报错 0 警告；
- [ ] S52 平台策略场景 119 项断言全部通过；
- [ ] `FeishuRemediationProbe` 通过；
- [ ] 架构规范：Platforms 仅依赖 Core，绝不依赖 Engine、Panel 或 Headless。

---

## 7. 交付物与下游交接 (Handoff Deliverables)

1. 产出物：`BotAgent.Platforms.dll`
2. 最终验收通过后的状态标记：**Wave 2 轨 B 完成，与轨 A (Agent 2) 汇合，正式解锁 Wave 3 (Agent 4)**。当前暂停状态不满足此标记。

---

## 8. 暂停检查点与续接记录 (2026-10-06)

### 8.1 可落点与工作区状态

按用户要求，任务暂停在**平台源码已物理迁出、宿主已接入，最终验收尚未收口**的位置。本轮只核对源码与更新文档，不继续功能修改或重跑构建／集成测试。

当前工作区包含多个 Agent 的未提交改动。Platforms 新目录尚未跟踪，旧平台源码删除已暂存，宿主桥接修改尚未暂存；不能只提交旧文件删除而遗漏新目录。Core、Model、Storage 等其他 Agent 的改动也在同一工作区，不得整体回滚、批量暂存或将其归为 Agent-03 的交付。

### 8.2 已落地的迁移与桥接

- `src/BotAgent.Platforms/` 已包含 `OneBot/`、`Official/`、`Feishu/`、`Local/`、`Governance/`；对应 Headless 旧平台目录已移除。
- `Common/` 收纳兼容外观 `IQqChatSource`、身份映射、时钟及日志桥接；`Music/` 收纳 OneBot 音乐段模型和解析；`Resilience/` 收纳协议风险退避。
- `BotAgent.slnx` 已纳入 Platforms；`BotAgent.Headless.csproj` 已引用 Core 与 Platforms。暂停时宿主尚无 Model／Storage 项目引用，轨 A 汇合时须核对最新状态，不覆盖其他 Agent 的后续接线。
- `PlatformOptions` 承载平台配置切片；宿主 `AppSettings` 继承该类，`SettingsBox` 通过 `IPlatformSettingsBox` / `IPlatformSettingsAccessor` 提供当前配置。与轨 A 的配置切片需要共同核对序列化、快照复制与热更新兼容性。
- 平台出网依赖 `BotAgent.Platforms.Net.IPlatformHttpFetcher`；宿主 `BotAgent.Adapters.Net.IHttpFetcher` 继承该接口，避免 Platforms 反向引用 Headless。
- `Persistence/PlatformDedupStore.cs` 使用 SQLite 管理 `feishu_webhook_dedup` 表；已替换飞书网关对宿主 `AppDatabase` 的调用。它自行解析数据库路径并维护表，与轨 A 的存储初始化、路径规则和去重行为仍需回归核对。
- `SendResult` 统一由 Core 导出，命名空间保留 `BotAgent.Services.Qq`。Platforms 的唯一项目依赖是 Core。

### 8.3 端口实现现状与待核对差异

| 网关 | 暂停时直接实现的接口 |
| --- | --- |
| `OneBotGateway` | `IQqChatSource`、Core 的 `IQqActions`、`IDisposable` |
| `OfficialBotGateway` | `IQqChatSource`、`IDisposable` |
| `FeishuBotGateway` | `IQqChatSource`、Core 的 `IPlatformAdapter` / `IPlatformMessenger`、`IDisposable` |
| `LocalChannelSource` | `IQqChatSource` |

因此尚不能声称“所有网关均已直接实现中立平台端口”。续接时须核对现有转接层与宿主装配，确认是否满足用户对 `IPlatformAdapter` / `IQqActions` 的要求，再决定必要的最小修改。

### 8.4 验证证据与验收边界

| 检查 | 已有记录 | 暂停时结论 |
| --- | --- | --- |
| Platforms Release 构建 | 前序会话记录为 0 错误、0 警告 | 本轮未重跑；最终验收仍待当前源码复验 |
| S52 平台策略回归 | 前序会话记录为 119/119 通过 | 本轮未重跑；宿主重新构建后再确认 |
| Headless Release 构建 | 最近一次构建工具调用已中止，未返回结果 | 当前构建结果待确认，不计为成功或失败 |
| FeishuRemediationProbe | 暂停时没有完整成功输出可用于确认 | 待重新构建与运行，不宣称断言全部通过 |
| Platforms 仅依赖 Core | 本轮已读取项目文件核对 | 唯一 ProjectReference 为 Core；另有 SQLite NuGet 依赖 |

§6.2 验收项暂不勾选；前序通过记录不能代替当前共享工作区的最终验收。

### 8.5 下一次续接顺序

1. 先核对 `git status`、暂存／未暂存差异及轨 A 最新改动，仅处理 Agent-03 所属范围；不修改 Core、Model、Storage 或前端。
2. 核对 §8.3 的平台端口覆盖与宿主转接层，检查配置切片快照、序列化及热更新保持兼容；飞书去重检查只使用合成数据。
3. 按 §6.1 依次构建 Platforms 与 Headless。若出错，记录实际错误并定位责任范围；不得移除其他轨道引用来掩盖问题。
4. **先构建最新 Headless，再构建并运行 S52 harness**，防止 harness 启动旧宿主二进制；随后构建并运行 FeishuRemediationProbe。
5. 验证通过后补齐 §6.2、同步证据，与轨 A 汇合后再给出“Wave 2 轨 B 已就绪”报告。提交时同时包含新平台源码、旧源码迁移和必要桥接，避免不完整迁移。

### 8.6 草稿 PR 准备更新（2026-10-06）

用户在暂停检查点后授权将 **Core 前置迁移 + Platforms** 提交为面向 `ZhaoJun233/bot-agent:dev` 的草稿 PR。已从最新 `dev`（`26227b7adac467b9e85411921e381b88a187af9b`）创建独立 PR 工作树，包含两模块、旧源码迁移、必要宿主桥接、合成回归探针与计划文档。解决方案仅纳入 Core、Platforms、Headless；不纳入 Model／Storage 源码或项目引用。§8.1 记录仍描述原共享工作区的暂停状态，以下证据来自独立 PR 工作树。

PR 准备中修正两处配置迁移缺陷：

- 删除 `AppSettings` 中 22 个遮蔽 `PlatformOptions` 的重复属性，使宿主与 `IPlatformSettingsAccessor.Current` 读取同一份开关、凭据、白名单与策略。保留既有默认值、快照深复制及 `SettingsBox` 原子发布行为。
- 将原有 `JsonIgnore` 语义恢复到基类的 `OneBotToken`、`OfficialAppSecret`、`FeishuAppSecret`、`FeishuEncryptKey`、`NormalizedUin`、`UinOrZero`；`FeishuVerificationToken` 的持久化行为保持与迁移前一致。
- 宿主 `IHttpFetcher` 直接继承平台端口的成员，移除重复声明造成的隐藏告警。
- 在既有飞书探针中新增 3 个合成回归场景，覆盖基类／派生类配置视图、JSON 与 SQLite 设置持久化、嵌套策略快照隔离及成功／失败热更新。修复前均可复现失败，修复后通过。

| 当前源码验证 | 结果 |
| --- | --- |
| `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release` | Core、Platforms、Headless 构建通过；0 警告、0 错误 |
| `dotnet build tests/BotAgent.FeishuRemediationProbe/BotAgent.FeishuRemediationProbe.csproj -c Release` 后运行其 Release DLL | 18 通过、0 失败（含新增 3 项配置回归） |
| `dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release` 后以 `QQCHAT_IT_ONLY=s52` 运行其 Release DLL | 119 通过、0 失败；harness 构建有 2 个既有 CS8602 警告 |
| `dotnet build tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release` 后运行其 Release DLL | 594 通过、0 失败；构建 0 警告、0 错误 |
| `dotnet build tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release` 后运行其 Release DLL | 构建通过；探针 86 通过、6 失败，不能记为验收通过 |
| 候选发布内容的既有黑名单及类别规则检查（包含新增文档） | 0 个阻断项；正式发布继续执行原发布脚本双重审计与远端独立克隆复检 |

架构探针仍只扫描 Headless，迁出的 Core 领域目录会被当作 0 文件。六个失败涉及旧 `Domain/Tools/ToolSpec.cs`、`Domain/Ops/TurnTrace.cs`、`ToolPolicy.cs`、`ToolGate.cs` 路径，以及已迁出的 `[可用工具]`、`LocalBase` 锚点；后续须适配跨项目扫描，不能放宽基线或删除断言来通过。

本 PR 继续保持**草稿／暂停检查点**：§8.3 的中立平台端口覆盖、SQLite 去重与轨 A 的协同，以及跨项目架构护栏仍未全部收口。§6.2 不因上述部分回归通过而整体勾选；不宣布 Wave 2 完成或解锁 Wave 3。
