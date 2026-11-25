# 会话级可组合档案与平台中性提示词（Phase 1）

这页记录会话级配置覆盖（`ConversationProfile`）的三层判定路径、零迁移持久化边界、工具档位映射，以及遗留表 `prompt_templates` 的冻结状态。

## 1. 三层优先级与硬上限

配置解析统一经过 `ConversationProfileResolver.Resolve(AppSettings, EffectivePlatformPolicy, PlatformCapabilities, DriverKey, string sourceKey)`：

1. **硬上限层（永不可被会话档案绕过或复活）**：
   - 平台驱动声明的 `PlatformCapabilities`（例如飞书不支持 `Voice`/`Poke`）；
   - 平台通道启用状态（`PlatformPolicy.Enabled == false` 或 `ChatEnabled == false`）；
   - 平台策略条目中显式停用的功能（`PlatformPolicy.FeatureOverrides[f] == false`）；
   - `WhitelistGate`、`AppSettings.AiModeEnabled == false`、`PluginGate.*` 与 `TokenQuota`。
2. **会话档案层（`ConversationProfile`）**：
   - 键为 `(DriverKey, ConversationKey)`，严格按平台前缀隔离（`qq.private` 仅接受 `group:<数字>` / `private:<数字>`；`qq.official` 仅接受 `official:group:...` / `official:private:...`；`feishu` 仅接受 `feishu:group:...` / `feishu:private:...`；`local` 仅接受 `local:...`）；
   - 在硬上限允许的前提下，可覆写表达风格（`PersonaOverride` / `PersonaAppend` / `FilterActionNarration` / `VoiceMaxChars` / `VoiceEagerness`）、对话节奏（插话概率/冷却、群/私聊续话等待、适宜度阈值、上下文条数、主动发言开关与安静时长）、6 项 Phase 1 真实生效功能覆盖（`Voice`/`Stickers`/`Music`/`Poke`/`LinkPreview`/`Search`）及诚实的 4 档工具权限（`Inherit` / `ChatOnly` / `ReadOnlyTools` / `ManagedTools`）。
   - 会话档案既可把全局开启的功能在指定会话收紧关闭，也可在平台硬上限允许时为单个会话局部启用全局关闭的功能；同时经由 `EffectivePlatformPolicy.FeatureOverrides` 与 `ApprovalUseCase.CapabilitiesFor` 同步投影到提示词工具清单、入站媒体解析、动作排队与交付前实时复检，保持口径一致。
3. **全局默认层（`AppSettings`）**：未配置档案或字段留空时全部沿用全局设置。

## 2. 工具档位（`ConversationToolTier`）与真实闸门

| 档位 | 值 | 行为边界 |
| --- | --- | --- |
| `Inherit` | `0` | 沿用全局开关与能力白名单 |
| `ChatOnly` | `1` | 禁用 `web.search`、工具目录可见性、自然 Agent 与审批/提问工具；保留特征轴媒体 |
| `ReadOnlyTools` | `2` | 允许搜索与只读工具（`Allow` 类），阻断 `Approve` 类工具、自然 Agent 与 `TryHandleTurnShortcutsAsync` 的 `tool` 审批请求 |
| `ManagedTools` | `3` | 允许只读工具并遵循既有自然 Agent 与审批白名单闸门（不放宽任何安全审批边界） |

## 3. 零迁移存储与隐私投影（SQLite Schema v15）

- `ConfigurationFieldId.ConversationProfiles = 153` 属于纯 `registered` 集合（`registered.conversationProfiles`），**不写入** `configuration_field_modes` 行级表；
- 持久化通过 `ConversationProfileStorageBridge` 复用既有事务原语 `AppDatabase.GetMetaInTransaction` / `SetMetaInTransaction` 写入 `meta['configuration_conversation_profiles']`，`PRAGMA user_version` 保持 `15`，新增 SQL 字面量为 `0`；
- 写操作在 `ConfigurationStore` 单事务内与 `ConfigurationVersion` CAS、操作回执及审计日志原子提交，生效类型为热更新（`FieldEffect.Hot` → `PublishSnapshot` + `RuntimePolicy`），免重启；
- 读侧经 `ConversationProfileProjection` 区分 `Overview`（隐藏原始标识与备注真名、仅返回脱敏展示串，且 `runtimeEffects` 仅输出聚合项 `field=153, object=null` 不含明文会话键）与 `Editable`（返回 `displayLabelRaw` 供真名编辑，展示用 `displayLabel` 与 `conversationKeyDisplay` 保持脱敏）。

## 4. `prompt_templates` 表的冻结状态

SQLite 数据库中既有 `prompt_templates` 表及 `PromptTemplateStore` 属于历史预留结构，**当前处于冻结停用状态（Reserved / Inactive）**：

- 运行时提示词组装不再读取或写入 `prompt_templates`；
- 按会话定制人设与表达风格的**唯一数据源（Single Source of Truth）**为 `ConversationProfile.Style`（`PersonaOverride` / `PersonaAppend` / `FilterActionNarration` / `VoiceMaxChars` / `VoiceEagerness`）；
- 为保持 SQLite Schema v15 零迁移与老库向前兼容，暂不执行 `DROP TABLE prompt_templates`，后续如需清理将在独立迁移周期另行评估。
