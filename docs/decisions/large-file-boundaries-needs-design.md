# 大文件职责边界预审（`needs-design`，Phase 0 基线冻结）

- 状态：设计预审已冻结（2026-11-21），**本文件不修改任何业务源码，仅冻结 Phase 1–5 演进期间的职责边界与拆出规则**。
- 依据：[大文件治理标准](../architecture/large-file-policy.md)、[ArchitectureProbe 基线](../../tests/BotAgent.ArchitectureProbe/Baseline.cs)（`MaxFileLines = 1643`、`LongestMethodAnywhere = 198`）。
- 编号说明：本文刻意不占用 `0005` / `0006` 编号，预留给后续 Phase 3（记忆-工具污点分级）与 Phase 5（可选本人跨端绑定）的正式 ADR。

---

## 1. 预审背景与 Phase 1–5 硬约束

截至 2026-11-21（Phase 0），`ArchitectureProbe` 实测全 `src/` 最大文件为 `SettingsStore.cs`（**1605 行**，距棘轮上限 `1643` 仅余 **38 行**），次大为 `ConfigurationStore.cs`（**1583 行**，距上限仅余 **60 行**）。前端同样存在两处超过 1900 行的核心模块（`CapabilitiesPage.vue` 1991 行、`zba.ts` 2453 行）。

若 Phase 1（`ConversationProfile`）、Phase 3（分层记忆与轻量知识库）、Phase 4（统一任务运行时与 MCP 控制台化）继续向 `SettingsStore.cs` 或 `ConfigurationStore.cs` 主文件追加字段映射或参与者逻辑，不仅会直接撞破 `MaxFileLines` 棘轮，还会让单次改动横跨持久化、投影、密钥绑定与事务治理四条链路。

因此冻结以下两条**Phase 1–5 施工硬约束**：

1. **禁止继续向主文件堆叠新功能**：Phase 1 / Phase 3 / Phase 4 新增配置实体（如 `ConversationProfile`、MCP 服务器配置、定时策略）或新辅助模型能力，**绝不直接向 `SettingsStore.cs`、`ConfigurationStore.cs`、`OpenAiClient.cs`、`CapabilitiesPage.vue`、`zba.ts` 主文件追加实现**，必须落入下文界定的独立协作类或按域拆分模块，仅在主文件留最小委托接线。
2. **拆而不破、先证等价**：每个大文件的实际拆分须在对应 Phase 需要触碰该边界时作为独立前置提交完成，遵循 [大文件治理标准 §6–§7](../architecture/large-file-policy.md#6-明确反对的做法)——**禁用机械 `partial class` 凑行数、禁止在重构提交内改写任何既有测试断言**，重构完成后同步调低 `Baseline.cs` 棘轮。

---

## 2. 逐文件职责清单与边界设计

### 2.1 `src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs`（1605 行，余量 38 行）

#### 实测职责清单（5 项混合职责）

| 职责 | 当前行段（参考） | 代表成员 |
| --- | --- | --- |
| ① 快照读写与旧版设备兼容 | L42–182、L1576–1605 | `HasStoredSettings`、`Load`、`Save`、`ParseLegacyDevices`、`MergeUserFacts`、`SaveInTransaction` |
| ② 标量字段应用与运行快照水合 | L183–357、L373–497 | `ApplyScalarConfigurationFields`、`LoadInitializedRuntimeSnapshot`、`LoadCommittedRuntimeSnapshot` |
| ③ 结构化配置只读投影 | L358–372、L498–613、L635–829、L1445–1575 | `ProjectConfigurationView`、`Project*View`、`ProjectRegisteredProviderViews`、`ProjectModelCredentials`、`PolicyViews`、`CollectFieldDescriptors` |
| ④ `ConfigurationStorageParticipant` 写生命周期与供应商/凭据校验 | L830–1429 | `Reauthorize`、`Initialize`、`SecretSources`、`ResolveBindingFacts`、`ProjectProviderBindingFacts`、`PrimaryProjection`、`IsAuthorizedModelEndpoint` |
| ⑤ 配置审计写入 | L614–634、L1006–1016、L1430–1444 | `AppendConfigurationAuditInTransaction`、`AppendInitializationAudit`、`AppendAudit` |

#### 判定与目标协作类边界

- **能否按职责拆分**：**是**。职责 ③（只读视图投影）与职责 ④ 中的供应商/凭据绑定规划均为无副作用或单连接纯投影逻辑，与快照读写/事务入口边界清晰，且不需要机械 `partial class`。
- **目标拆出协作类**（均位于 `src/BotAgent.Headless/Adapters/Persistence/`，不新增 SQL 字面量与文件 IO）：
  1. `SettingsConfigurationProjector`（只读视图投影器）：承接 `ProjectConfigurationView`、`Project*View`、`PolicyViews`、`ProjectRegisteredProviderViews`、`ProjectModelCredentials`、`CollectFieldDescriptors`（预计移出 ~520 行）。
  2. `SettingsProviderBindingPlanner`（供应商与主槽位凭据绑定规划器）：承接 `SecretSources`、`ResolveBindingFacts`、`ProjectProviderBindingFacts`、`PrimaryProjection`、`IsAuthorizedModelEndpoint`、`PrepareRegisteredProviders` 辅助逻辑（预计移出 ~380 行）。
  3. `SettingsScalarMapper`（标量字段映射与旧设备解析）：承接 `ApplyScalarConfigurationFields`、`ParseLegacyDevices`、`MergeUserFacts`（预计移出 ~240 行）。
  4. `SettingsStore` 仅保留 `AppSettings` 快照存取、运行快照水合与 `ConfigurationStorageParticipant` 薄协调入口（预计回落至 ≤ 500 行）。
- **不可破坏不变量与守护**：
  - 唯一配置事务写入协调边界（`CoordinateWrite` / `Reauthorize`）不变；
  - `PrepareRegisteredProviders` 原子单步新建/换键 `ModelCredentialKey` 校验与 `Ollama` 无密钥启用规则不变；
  - 守护探针：`ReviewRemediationProbe`（`ConfigurationContractTests`、`ProviderRoutingBoundaryTests`、`ManagedHostCapabilityTests`）、`IntegrationHarness`（`s21`、`s54`、`s61`、`s62`）、`ArchitectureProbe`。
- **Phase 1 / 3 / 4 防膨胀规则**：Phase 1 的 `ConversationProfile` 持久化与投影必须新建独立 Store（`ConversationProfileStore`）并独立挂接，禁止塞进 `SettingsStore.cs`。

---

### 2.2 `src/BotAgent.Storage/ConfigurationStore.cs`（主文件 1583 行 + 5 个 partial 共 2112 行，余量 60 行）

#### 实测职责清单（4 项职责）

| 职责 | 当前行段（参考） | 代表成员 |
| --- | --- | --- |
| ① 单一事务协调核心（必须集中） | L56–135、L323–618 | `Activate`、`ReadAsync`、`SaveAsync`、`SaveCore`、`SaveInTransaction`、`FindAsync`、`RecordApply` |
| ② 历史兼容写路径（旧版 Mood 与 Legacy Save/Find） | L136–322 | `SaveLegacyAsync`、`SaveLegacyWithMoodAsync`、`FindLegacyAsync`、`ApplyLegacyMood`、`*MoodReceipt` |
| ③ 台账治理、容量与回执/密钥/模式 SQL 读写 | L619–831、L1564–1583 | `Govern`、`Version`、`Read/Insert/UpdateOperation`、`HasCapacity`、`SecretsChanged`/`PersistSecrets`、`ModesChanged`/`PersistModes`、`Fingerprint` |
| ④ 输入意图归一、主供应商别名规范化与冻结校验 | L832–1563（**732 行**） | `OwnedIntent`、`CanonicalizePrimaryAliases`、`Own`、`NormalizeText`、`FreezePolicies`/`FreezeRegistered`/`FreezeProvider`/`FreezeTarget`/`FreezePolicy`/`FreezeList`、`ValidateUrl` |

#### 判定与目标协作类边界

- **核心原则**：**事务所有权（职责 ①：BEGIN IMMEDIATE / CAS `expectedVersion` / 幂等回执 / `operation-recovery:v1:`）必须绝对集中**，绝不把事务拆散到多个类导致跨连接或失锁。
- **目标拆出协作类**（位于 `src/BotAgent.Storage/`，纯输入规划不持连接、不新增 SQL 字面量）：
  1. `ConfigurationIntentPlanner`（纯内存输入规划与冻结器）：承接职责 ④ 全部逻辑（`Own`、`CanonicalizePrimaryAliases`、`Freeze*`、`NormalizeText`、`ValidateUrl`），零 SQLite 依赖，可独立单测（预计移出 ~730 行，主文件直接降至 ~850 行）。
  2. `ConfigurationLegacyMoodCodec`（旧 Mood 兼容回执编解码）：承接职责 ② 中的 `NormalizeMood` / `DecodeMoodReceipt` / `MoodFingerprint`（仅移出纯编解码，事务执行留在 `ConfigurationStore`）。
- **不可破坏不变量与守护**：
  - CAS 冲突检测、`expectedVersion` 严格单调递增、幂等键冲突拒绝、`LegacyReceipt` 历史兼容、以及 `Baseline.ConfigurationSqlPaths` 白名单（`ConfigurationIntentPlanner` 无 SQL，不扩白名单）；
  - 守护探针：`ReviewRemediationProbe`（`ConfigurationContractTests`）、`ZbaProbe`、`ArchitectureProbe`、`IntegrationHarness`（`s54`、`s55`、`s61`）。
- **Phase 1 / 3 / 4 防膨胀规则**：所有新增配置字段的冻结与归一化规则须优先在 `ConfigurationIntentPlanner`（或独立领域 validator）实现，不再向 `ConfigurationStore.cs` 主文件继续堆叠 `Freeze*` 方法。

---

### 2.3 `src/BotAgent.Model/OpenAiClient.cs`（1263 行）

#### 实测职责清单（3 项职责）

| 职责 | 当前行段（参考） | 代表成员 |
| --- | --- | --- |
| ① 供应商路由故障转移与上游寻址 | L24–344、L357–424、L1193–1263 | `SendThroughProvidersAsync`、`CompleteChatWithProviderFailoverAsync`、`HasExplicitUpstream`、`BuildUrl`、`LogParseNotices` |
| ② 主聊天推理与思考参数降级 | L345–356、L463–672 | `CompleteChatAsync`、`CompleteChatCoreAsync`、`CompleteChatCoreUnsafeAsync`、`CompleteChatWithReasoningAsync`、`LooksLikeUnsupportedReasoning` |
| ③ 辅助模型任务（人设摘要、评估、提取等） | L425–462、L673–1192（**~520 行**） | `CompleteAuxiliaryAsync`、`SendAuxPayloadCoreAsync`、`SummarizePersonaAsync` 及各类辅助 prompt 组装 |

#### 判定与目标协作类边界

- **能否按职责拆分**：**是**。主对话推理与供应商故障转移（职责 ① + ②）属于核心传输编排；各类辅助任务（职责 ③，如人设摘要、氛围评估、后续 Phase 2/3 的文档摘要/记忆提炼）属于上层用例模板，与主对话热路径解耦。
- **目标拆出协作类**（位于 `src/BotAgent.Model/`）：
  1. `AuxiliaryModelTasks`（辅助推理任务协作器）：接收受控 `SendAuxPayload` 委托或内部执行端口，承接 `SummarizePersonaAsync` 与既有辅助调用，并作为 Phase 2（群聊纪要 `digest`）与 Phase 3（情景/黑话提炼）的新辅助调用归属（预计移出 ~500 行，`OpenAiClient.cs` 降至 ~760 行）。
- **不可破坏不变量与守护**：
  - 热切故障转移、熔断器上报、提示词缓存前缀稳定性、请求级凭据隔离、以及 `IModelClient` 公共接口签名零变化；
  - 守护探针：`ProtocolProbe`、`ReviewRemediationProbe`（`ProviderRoutingBoundaryTests`）、`IntegrationHarness`（`s21`、`s61`、`s62`）。

---

### 2.4 `src/web/src/features/capabilities/CapabilitiesPage.vue`（1991 行）

#### 实测职责清单（4 项职责）

| 职责 | 当前行段（参考） | 代表成员 |
| --- | --- | --- |
| ① 领域弹窗/抽屉协调与离开保护 | L135–336、L1331–1444 | `openSettings`、`openProvider`、`openMarketplace`、`openPluginUiById`、`requestEditorLeave`、`discardEditorSecrets` |
| ② 插件启停开关身份钉住 | L337–397 | `rememberPluginCards`、`cardVisible`、`loadSwitches` |
| ③ 能力执行动作状态机（重复实现） | L523–1330（**~800 行**） | `beginRecord`、`confirmAction`、`submitNew`、`resendUnknown`、`retryRecord`、`queryRecord`、`cancelRecord`、`abandonInFlight` |
| ④ 统一能力视图模板与样式 | L1445–1991 | `<template>` 与 `<style scoped>` |

#### 判定与目标协作类边界

- **定位核对**：自 5 阶段前端重构完成后，主导航已全面切至四域页面（`ProvidersPage` / `PlatformsPage` / `PluginsPage` / `SystemConfigurationPage`），这四页已使用抽出的共享组合式函数 `use-capability-operations.ts` 与 `CapabilityOperationPanel.vue`。`CapabilitiesPage.vue` 保留用于兼容视图与大量历史契约测试。
- **目标拆分路线（渐进，不破坏历史 `data-testid`）**：
  1. 将 `CapabilitiesPage.vue` 内联的 800 行动作状态机（职责 ③）逐步收敛到共享 `use-capability-operations.ts` 与 `CapabilityOperationPanel.vue`，消除双份维护；
  2. 将只读身份标识/标签格式化（`rawKeyLabel` / `keyLabel` / `keySlug` / `kindLabel`）收口到共享模块 `capability-labels.ts`。
- **Phase 1–5 防膨胀规则**：**禁止向 `CapabilitiesPage.vue` 新增任何新业务功能入口**；新功能按四域归属分别进入对应领域页（模型供应商 / 消息平台 / 插件中心 / 系统配置）或 Phase 5 独立工作台页。
- **不可破坏不变量与守护**：`capabilities-page.test.ts`、Playwright `capabilities.spec.ts`、秘密离开清零与操作键归属隔离测试断言零退化。

---

### 2.5 `src/web/src/api/zba.ts`（2453 行）

#### 实测职责清单（3 层混合）

| 职责 | 当前行段（参考） | 内容说明 |
| --- | --- | --- |
| ① Wire TypeScript 类型与接口定义 | L1–847（847 行） | 健康/状态、事件、轨迹、日志、会话/任务/审批、托管能力、配置读写/回执、插件市场全部 wire 类型 |
| ② 错误类型、提交失败分类器与 `ZbaClient` 传输/资源方法 | L848–1601（754 行） | `ZbaRequestError`、`ZbaNetworkError`、`classify*Failure`、`ZbaClient`（含 `fetchJson` 统一超时/取消/鉴权边界） |
| ③ 严格运行时响应结构校验器（Type Guards / Decoders） | L1602–2453（852 行） | 60+ 个 `is*` / `parse*` 响应解码守卫 |

#### 判定与目标协作类边界

- **能否按职责拆分**：**是**，且 `src/web/src/api/zba.ts` 应保留为**统一桶导出入口（barrel）**，让全仓既有 `import ... from '../../api/zba'` 零改动兼容：
  1. `src/web/src/api/zba-types.ts`（或按域拆 `zba-types-config.ts` / `zba-types-plugins.ts` / `zba-types-tasks.ts`）：纯类型清单（符合 [大文件治理标准 §2](../architecture/large-file-policy.md#2-可例外的情况) 的数据定义边界，零运行时代码）；
  2. `src/web/src/api/zba-decoders.ts`（或按域分块）：承接 L1602–2453 的纯运行时响应结构校验函数；
  3. `src/web/src/api/zba.ts`：保留 `ZbaClient` 核心传输、错误类、分类器并 `export * from './zba-types'`。
- **Phase 1–5 防膨胀规则**：Phase 1（会话档案 API）、Phase 4（MCP / 群 RBAC API）、Phase 5（定时提醒 / 待办 API）的新 wire 类型与 decoder **必须落入独立域模块**（如 `src/web/src/api/zba-profiles.ts`、`zba-scheduler.ts`），仅在 `ZbaClient` 中委托调用，不向 `zba.ts` 继续堆积数百行类型与校验器。
- **不可破坏不变量与守护**：`tests/contract/wire.test.ts`、`zba-client.test.ts`、`zba-timeout.test.ts`、`zba-plugins.test.ts`、`zba-task-observation.test.ts`、`vue-tsc --noEmit`。

---

## 3. 演进触发时机与验收对照表

| 目标文件 | 触发实际拆分的时机 | 拆分前必跑基线 | 拆分完成验收标准 |
| --- | --- | --- | --- |
| `SettingsStore.cs` | Phase 1 涉及配置投影或 Phase 4 扩展 MCP 配置之前 | `ArchitectureProbe` + `ReviewRemediationProbe` + `IntegrationHarness` (`s21,s54,s61,s62`) | 主文件 ≤ 600 行、原断言零改动、`Baseline.MaxFileLines` 同步下调 |
| `ConfigurationStore.cs` | Phase 1 / Phase 4 需要新增配置冻结规则之前 | `ArchitectureProbe` + `ReviewRemediationProbe` + `ZbaProbe` | 事务核心仍集中于单类、`ConfigurationIntentPlanner` 零 SQL、原断言零改动 |
| `OpenAiClient.cs` | Phase 2 接入 `digest` 或 Phase 3 接入记忆提炼辅助调用之前 | `ProtocolProbe` + `ReviewRemediationProbe` + `IntegrationHarness` (`s21,s61,s62`) | 辅助调用独立成 `AuxiliaryModelTasks`、主链路路由与缓存行为等价 |
| `CapabilitiesPage.vue` | 仅当需要修改能力操作状态机缺陷时顺手收敛（无新功能进入） | `npm run typecheck && npm run test:unit` + `capabilities.spec.ts` | 移除重复状态机代码、所有既有 `data-testid` 与组件测试零改动通过 |
| `zba.ts` | Phase 1 新增首个会话档案 API 之前 | `npm run typecheck && npm run test:unit` + `wire.test.ts` | 保留 `api/zba` 桶导出兼容、新增域 API 落独立类型与 decoder 模块 |
