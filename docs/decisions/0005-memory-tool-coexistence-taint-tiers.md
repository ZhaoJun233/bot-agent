# ADR-0005：记忆敏感度分级与工具流出面共存矩阵

- 状态：Phase 3（v1.7）实施基线；不削弱 [ADR-0004](0004-unified-memory-authorization.md) 与 [GLOSSARY.md](../glossary.md) 任何一条安全不变量。
- 关联：`MemoryTaintPolicy`、`MemoryUseScope`、`UnifiedMemoryTurn`、`ToolVisibility`、`InlineTurnTools`、`ReplyPipeline.Generation`、`ServerAgentRunner`。

## 1. 决策摘要

取消「凡 `MemoryUseScope.HasMemory == true` 即全盘封禁全部工具、表情包、语音与搜索」的粗粒度布尔锁；改为按 **本轮实际消费到的最高记忆敏感度等级（`MemorySensitivityTier`）× 工具数据流出面（`ToolEgressClass`）** 执行 Fail-Closed 二维矩阵裁决。

## 2. 记忆敏感度等级（`MemorySensitivityTier`）

| 等级 | 枚举值 | 数据来源与内容边界 |
| --- | :---: | --- |
| `None` | `0` | 本轮未消费任何统一记忆或旧画像片段 |
| `StylePreference` (L1) | `1` | 封闭词表表达偏好：`ReplyLanguage`、`ReplyLength`、`ExplanationStyle`、`Timezone` |
| `PersonalFact` (L2) | `2` | 主体个人事实：`PreferredAddress`（有限短称呼）、`PersonalNote`（强制仅私聊同源 `PrivateOnly`） |
| `LegacyProfile` (L3) | `3` | 旧人物画像自由文本摘要或未标记来源的原始回忆凭据 |

## 3. 工具流出面分级（`ToolEgressClass`）

| 流出面 | 枚举值 | 典型动作 / 工具 |
| --- | :---: | --- |
| `LocalPresentation` | `0` | 当前限定目标的表情包发送、云端语音 TTS（发送前复用 `TryDeliver` 撤销校验）、戳一戳、同会话回复分句 |
| `SameScopeRead` | `1` | 严格同源只读工具：`doc.read`、`chat.digest`、`kb.search`、`kb.list`、`reminder.list`、`todo.list` |
| `ExternalReadEgress` | `2` | 出网只读检索：`web.search`、`web.read`、`music.*`（含 `music.listen`/`music.share`）、外部只读 MCP、未分类扩展工具 |
| `ExternalSideEffect` | `3` | 写操作与跨目标动作：自然语言转 `ServerAgent`、审批类 `tool.request` / `demo.echo`、`reminder.*` 写操作、`todo.*` 写操作、外部写、Python Worker |
| `GlobalStateWrite` | `4` | 写入全局/跨轮持久化衍生状态：全局心情 `_mood`、全局氛围持久化、跨轮检索缓存 `_research`、Agent 对话持久化历史 |

## 4. 共存裁决矩阵（Fail-Closed）

| 最高敏感度等级 | `LocalPresentation` | `SameScopeRead` | `ExternalReadEgress` | `ExternalSideEffect` | `GlobalStateWrite` |
| --- | :---: | :---: | :---: | :---: | :---: |
| `None` (0) | 允许 | 允许 | 允许 | 按常规权限 | 允许 |
| `StylePreference` (L1) | 允许（受发送前目的地与撤销锁校验） | 允许 | 允许（不写入研究缓存，不持久跨轮痕迹） | 拒绝 | 拒绝 |
| `PersonalFact` (L2) | 允许（严格限定当前目标发送） | 允许（同源只读） | **严格拒绝**（阻断提示词注入将个人事实拼入外网检索） | 拒绝 | 拒绝 |
| `LegacyProfile` (L3) | 拒绝 | 拒绝 | 拒绝 | 拒绝 | 拒绝 |

## 5. 不变量

1. 直接以原始 `MemoryUseScope.Register(callback)` 注册未指明等级的调用方，默认保守定级为 `LegacyProfile`（L3）。
2. 发送外发、模型提交与同源只读工具执行前，仍必须通过 `MemoryUseScope.CanUse` / `TryUse` / `TryDeliver` 撤销与目的地绑定锁校验；停用、撤销或遗忘发生时立即失败封闭。
3. 本决策绝不代表将上下文、身份或未授权记忆跨会话打通。
