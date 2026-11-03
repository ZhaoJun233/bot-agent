# 模型输入缓存：稳定前缀、动态尾巴与三态观测

供应商侧的「输入缓存」是按 **token 前缀** 命中：一次请求的开头如果能和上一次逐字相同，那一段就可能复用对方的 KV 缓存；中途第一个变化的位置之后，全部重算。#98 做的就是把这个事实落到本仓的请求拼装与统计上。

这一页回答三个问题：请求里哪些内容属于稳定前缀、哪些是每轮必变的动态尾巴；缓存用量从哪里读、怎么看；以及现在**还做不到**什么。

## 1. 边界（先说不做什么）

- **只搬家，不改字**：分层没有改任何一条指令的措辞、优先级、权限判定、脱敏或会话隔离。SafetyProbe 的措辞断言与集成场景的链路断言都在守着这一点。
- **不承诺命中率**：命中多少取决于上游的缓存策略、最小可缓存长度、TTL 与冷启动，本仓不做保证，也不拿「复用旧答案」之类的手段伪造命中。
- **不默认发缓存控制参数**：没有给请求加 `cache_control` / `prompt_cache_key` 之类的东西；是否启用属于后续条目，且必须按通道能力开关。
- **本轮没有真实供应商证据**：合成桩只能证明「请求形状稳定、统计读得对」，不能证明「对方真的命中了」。这条写在第 8 节，不藏。

## 2. 一次请求是怎么拼出来的

普通聊天（非 `//` 任务）的链路是：

```
BotAgentHost -> ReplyPipeline -> SourceScheduling -> ReplyTurnContext.BuildAsync
  -> AgentTurnLoop.RunAsync（最多 3 步）
  -> OpenAiClient.CompleteAsync
  -> PromptBuilder.Build（系统提示）
  -> ModelTransport.BuildAsync（payload：messages / 图片 / 采样参数）
  -> ModelProtocolAdapter（按协议加认证头并改写形状）
```

关键事实：

- 整条系统提示是 **唯一一条 `system` 消息**（`src/BotAgent.Headless/Adapters/Model/ModelTransport.cs:80` 起）。带图时的那句尾注会追加进同一条 `system`（`:83-87`）。
- 历史消息从 `:94-146` 渲染，最后在 `:148-167` 补一条 `user`。
- 请求体里没有会话 id / trace id / 随机 id / `user` / `metadata`，聊天也从不带 `stream` —— 也就是说，**不存在「每轮随机的标识符」在破坏前缀**。
- 工具定义是**文本清单**（`[可用工具]`），不是 JSON schema；源是静态只读数组，两处 `Where` 保序，没有排序抖动。

因此本轮的优化点只有一处：**系统提示内部，谁在前面**。

## 3. 稳定前缀与动态尾巴（本轮改动）

`src/BotAgent.Model/Model/PromptBuilder.cs` 的 `Build` 现在按两个 `StringBuilder` 拼：`stable` 与 `dynamic`，最后把 `dynamic` 接在 `stable` 后面。所有「每轮会变」的段落只往 `dynamic` 写，静态段落只往 `stable` 写。

| 层 | 内容 | 为什么放这里 |
| --- | --- | --- |
| 稳定前缀 | 机器人身份档案、人设、可发言判断规则、`[可用工具]` 清单、联网搜索段（开关开时才有）、表情包指令、动作契约 | 同一份配置下逐轮逐字相同 |
| 动态尾巴 | 心情、上一轮的「感觉」、刚查到的资料、会话参与者档案、撤回提示、本群身份、戳一戳上下文、表情包候选、**发言意愿数值**、**[现在的时间]** | 每轮或每分钟会变 |

两个具体动作：

- **时间块搬到最末尾**：`[现在的时间]` 原来紧跟身份，现在由 `BuildTimeSection` 单独产出、在 `Build` 的最后追加。它是分钟级变化的内容，放在越后面，前面能命中的部分越多。
- **发言意愿数值拆成动态**：`BuildSuitabilityInstruction` 的静态规则留在稳定前缀，末尾那句随气氛阻尼变化的意愿值改为写入 `dynamic`。

要点：**时间仍然注入、仍然只有一个基准**（见 `docs/architecture/temporal-awareness.md`），只是位置后移，措辞没动。

## 4. 还会破坏前缀的三件事（未做）

这些是调查里确认存在的，按收益从大到小：

0. ~~**历史时间戳渲染**~~ —— **#135 已修**。原来是「今天 `HH:mm`、更早 `MM-dd HH:mm`」：跨午夜时
   **每一条今天的消息都会换一种写法**，整段历史前缀作废。现在历史行一律走绝对形式 `MM-dd HH:mm`
   （`src/BotAgent.Model/ChatHistoryTime.cs`，由 `src/BotAgent.Model/ModelTransport.cs` 的 `FormatTime` 调用）：
   渲染结果**只取决于消息本身**，与「现在几点」无关；「今天是哪天/现在几点」由提示词末尾的
   `[现在的时间]` 一段承担（`docs/architecture/temporal-awareness.md`）。守护测试：
   `tests/BotAgent.SafetyProbe/PromptCacheStabilityTests.cs` 的两条 `★ #135` 断言。
2. **`(#id)` 只加在最近 16 条**（`ModelTransport.cs:106-109`，配合 `OpenAiClient` 的 `TakeLast(16)`）。窗口滑动时第 17 条前后的渲染标记会变一次。
3. **历史滑窗左移**：`ReplyTurnContext.BuildAsync` 与 `OpenAiClient` 各有一处同义裁剪（阈值分别来自面板字段与默认值，取小者）。窗口满了之后每轮丢最旧一条，前缀结构必然变。要改需要「前缀冻结」策略，风险最高。
4. **`[可用工具]` 随集合内容变**：顺序稳定，但策略开关、平台源、待审批状态一改，清单内容就变。这是语义要求，不做优化。

## 5. 缓存用量：三态，未知不是 0

这是本轮的第二块改动。统计口径写死在三个状态上：

| 取值 | 含义 |
| --- | --- |
| `null` | 上游**没提供**这个字段（未知；可能不支持、可能没透传） |
| `0` | 上游明确说这次**没命中** |
| 正数 | 实际命中的输入 token 数 |

读入口是 `src/BotAgent.Core/Domain/Model/ModelJson.cs` 的 `ReadUsageDetail`，它把几种上游形状收敛成同一个结构：

- OpenAI 兼容：`usage.prompt_tokens_details.cached_tokens`
- DeepSeek：`usage.prompt_cache_hit_tokens`
- Anthropic：`usage.cache_read_input_tokens` / `usage.cache_creation_input_tokens`
- Gemini：`usage.cachedContentTokenCount`
- 本仓协议适配器**归一化后**的形状：`prompt_tokens_details.cached_tokens` / `cache_write_tokens`（`ProtocolJson.Usage` 只在源字段真的存在时才写这两项，所以「缺」到下游仍然是「缺」）

传播链：`CompletionResult.CachedPromptTokens` / `CacheWriteTokens`（可空）→ `AgentTurnLoop` 在步进循环里逐轮累加 → `TurnTraceStore.RecordTokens` → `TurnTrace.CachedPromptTokens` / `CacheWriteTokens` / `CacheUsageUnknown` → `/metrics`。

`/metrics` 上的三个新指标（注释里写明口径）：

| 指标 | 含义 |
| --- | --- |
| `botagent_prompt_cached_tokens_total` | 累计命中供应商缓存的输入 token（只累加上游真报了的轮次） |
| `botagent_prompt_cache_write_tokens_total` | 累计写进供应商缓存的 token（只有部分上游提供） |
| `botagent_prompt_cache_unknown_total` | 缓存统计未知的已完成轮数 —— **分母**，不能假装是 0 命中 |

「命中占比」的诚实算法：分子只用已知轮次的命中量，分母只用同一批已知轮次的输入量，并把未知轮数单独报出来。把未知当 0 会让「供应商不支持」看起来像「一次都没命中」，这正是 #98 要区分的三种情况。

## 6. 各通道能看到什么

| 协议 | 命中字段 | 写入字段 | 备注 |
| --- | --- | --- | --- |
| openai-compatible（默认） | `prompt_tokens_details.cached_tokens` | 一般没有 | 有的网关会整段吞掉 `usage`，那就一律未知 |
| DeepSeek 兼容 | `prompt_cache_hit_tokens` | 无 | 原生字段，已兼容 |
| anthropic | `cache_read_input_tokens` | `cache_creation_input_tokens` | 原生字段，已兼容 |
| gemini | `cachedContentTokenCount` | 无 | 原生字段，已兼容 |
| responses | `input_tokens_details.cached_tokens` | 无 | 已兼容 |
| ollama | 无 | 无 | 永远未知 |
| 流式解码 | 同上各自字段 | 同上 | 解码器已顺手归一化；但普通聊天当前不走流式，**没有端到端用例** |

明确没有做的事：没有按通道开关缓存控制参数、没有设置最小可缓存长度、没有做 TTL 探测。这些参数一旦发出去就属于「改上游行为」，需要真实通道实测，留给后续条目。

## 7. 怎么复核

```bash
# 提示词分层与措辞边界
dotnet tests/BotAgent.SafetyProbe/bin/Release/net10.0/BotAgent.SafetyProbe.dll

# 三态统计的解析与汇总
dotnet tests/BotAgent.ProductionSpecProbe/bin/Release/net10.0/BotAgent.ProductionSpecProbe.dll

# 真实主链路（系统提示仍然只有一份、联网段仍可关闭）
BOTAGENT_IT_ONLY=s9  dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll
BOTAGENT_IT_ONLY=s26 dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll
```

观测侧只需要看 `/metrics` 里那三行：命中量、写入量、未知轮数。**没有未知轮数就别算占比**。

## 8. 已知限制（诚实清单）

1. **没有真实供应商的命中证据**。合成桩只能证明请求前缀稳定、统计字段读得对；对方是否真的缓存、缓存多久、最小长度多少，只能在真实通道上实测，本轮没做。
2. **历史前缀仍会失效**：时间戳渲染、`(#id)` 窗口、滑窗左移、工具清单变化（第 4 节）。最大的一次性收益（时间戳）没做。
3. **缓存字段不落库、不进面板**。`TurnTrace` 只活在内存台账里，`trace_archive` 表没有这两列 —— 加列要 bump schema，风险与收益不匹配，本轮不做。
4. **流式只做了归一化，没有端到端用例**：普通聊天不带 `stream`，所以这条路径目前只有解码器级别的覆盖。
5. **「部分未知」的轮次**：同一轮多次模型调用里只要有一次没报缓存统计，这一轮就标记 `CacheUsageUnknown`；已累加的值是「已知部分之和」，不是完整值。
6. **不做前缀冻结**：滑窗是语义上的取舍（最近的对话更重要），本轮不为了缓存去改上下文选择策略。

## 9. 改这里时看什么

- 动 `PromptBuilder`：先跑 SafetyProbe（措辞、预算、时效边界全是硬断言），再跑 s9（系统提示长度哨兵）。
- 动 `ModelTransport`：历史渲染同时影响 `(#id)` 与时间戳，改完必须跑 s19 / s25 / s12。
- 动用量解析：`ModelJson.ReadUsage` 的调用方仍在，别把三态改成两态（`ProtocolProbe` 有一条「缺失仍是 0/0」的老断言，那是给旧口径用的）。
- 不要用「未知填 0」让图好看，更不要在文档里写「命中率 X%」而不同时给未知轮数。
