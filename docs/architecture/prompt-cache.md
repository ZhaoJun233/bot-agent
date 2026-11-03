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

传播链：`CompletionResult.CachedPromptTokens` / `CacheWriteTokens`（可空）→ `AgentTurnLoop` 在步进循环里逐轮累加 → `TurnTraceStore.RecordTokens` → `TurnTrace.CachedPromptTokens` / `CacheWriteTokens` / `CacheUsageUnknown` → 两个出口：`/metrics`（进程内累计）与慢轨迹归档（落库，见下）。

`/metrics` 上的三个新指标（注释里写明口径）：

| 指标 | 含义 |
| --- | --- |
| `botagent_prompt_cached_tokens_total` | 累计命中供应商缓存的输入 token（只累加上游真报了的轮次） |
| `botagent_prompt_cache_write_tokens_total` | 累计写进供应商缓存的 token（只有部分上游提供） |
| `botagent_prompt_cache_unknown_total` | 缓存统计未知的已完成轮数 —— **分母**，不能假装是 0 命中 |

「命中占比」的诚实算法：分子只用已知轮次的命中量，分母只用同一批已知轮次的输入量，并把未知轮数单独报出来。把未知当 0 会让「供应商不支持」看起来像「一次都没命中」，这正是 #98 要区分的三种情况。

### 5.1 落库（v13，#135）

慢/异常轨迹归档 `trace_archive` 从 schema **v13** 起多了三列：`cached_prompt_tokens`、`cache_write_tokens`（都可空）、`cache_usage_unknown`（NOT NULL DEFAULT 0）。写入点是 `src/BotAgent.Headless/Adapters/Persistence/TraceArchiveStore.cs`：

- 上游报了缓存字段 → 写数值、`cache_usage_unknown = 0`；
- 上游没报 → 两列写 NULL、`cache_usage_unknown = 1`；
- **v13 之前归档的旧行**三列都是默认值（NULL / NULL / 0），读取时按 `cached_prompt_tokens IS NULL || cache_usage_unknown != 0` 判为未知 —— 旧行不会被误算成「命中 0」。

汇总出口两处：

| 出口 | 字段 | 语义 |
| --- | --- | --- |
| `GET /metrics` | `botagent_trace_archive_cached_prompt_tokens` / `…_cache_write_tokens` / `…_cache_unknown_turns` | 近 7 天归档的汇总；无任何已知行时命中量取 0（配合 unknown 分母读） |
| `GET /api/dashboard` | `traces.cachedPromptTokens` / `cacheWriteTokens` / `cacheUnknownTurns` | 进程内台账；`null` 表示这一进程还没有一轮带缓存统计 |

迁移是**只前向**的：v13 的库用旧二进制会因 `database_schema_newer` 拒绝启动（回滚要先恢复快照再退二进制，见 `docs/operations/rollback-playbook.md`）。升级路径有独立用例：`tests/BotAgent.ReviewRemediationProbe` 的 `--database-legacy-cache` 子进程造一个 v12 老库（里面已经有一行旧轨迹），断言补列成功、版本到 13、旧行仍然是「未知」。

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

### 6.1 供应商缓存控制参数（#138，默认关）

现在可以按通道打开缓存控制，但**默认全关**：关着的时候请求体与 #135 之后**逐字节一致**（探针断言的是序列化字节相等，不是「字段值为假」）。

| 通道 | 打开后发什么 | 为什么 |
| --- | --- | --- |
| openai-compatible / responses / azure | 顶层 `prompt_cache_key`，值是**稳定键的 SHA-256 前 32 位小写 hex**，由调用方给的 scope（会话 key）派生 | 原始会话 key 不出模型层；同一会话每一轮键相同，跨会话不同 |
| anthropic | **不发**顶层字段（Anthropic 拒绝未知顶层字段），改在 system 块与最后一条消息的最后一个 content block 上挂 `cache_control: {"type":"ephemeral"}` | 这是 Anthropic 协议里真实的断点标记，不是自定义扩展 |
| gemini / ollama | 两种都不发 | 两个协议没有等价字段，发了只是噪声 |

开关落在 `ModelProtocolOptions.PromptCache`（默认 `false`，随供应商配置持久化）。payload 里只放**规范字段**，由 `ModelProtocolAdapter.CreateRequest` 按路由决定发不发 —— 这样不必给 20+ 参数的 `IModelClient.CompleteAsync` 增加通道特判。

### 6.2 真实通道证据：拿到的是「链路会怎么处理」，不是命中数（#138）

本轮把部署链路的两跳按源码读完了（handler 入口 → 上游 provider 池），能确定**字段会不会被改写**，不能确定**上游到底命中多少**：

- **第一跳（自建网关，Go）**：`prompt_cache_key` 在请求结构体里有显式字段（不是被结构体解码丢掉的未知字段），转发时确实写回上游 body。但它会**改写**这个键：客户端没给、而 provider 需要时由网关按会话自己派生；客户端给了也可能被网关的键覆盖；provider 能力表不支持时直接从报文里删掉；上游以 400 明确点名该字段时，网关会把它标记为「此 provider 不支持」并**此后永久剥离**、重发一次不带它的请求。Responses 通道同理。
- **第二跳（可选 cli-proxy 层）**：配置 `support-prompt-cache-key` **默认关**。关着时不动请求；开着时**保留客户端已给的键**，没有才自己派生。另有一条搜索路由会显式删除 `prompt_cache_key` / `prompt_cache_retention`。
- **命中口径也是网关归一化的**：`usage.prompt_tokens_details.cached_tokens` 是网关把各家字段（DeepSeek 的 `prompt_cache_hit_tokens`、Moonshot 的 `cached_tokens`、Anthropic 的 cache_read 等）统一后的结果；网关自己还有 Anthropic 缓存断点与亲和路由。所以宿主观测到的「命中」不必然等于上游原生命中。

结论：**宿主发的键最多是建议值**。要拿到真实命中证据，必须「中间层不覆盖 + 上游真支持 + 前缀真稳定」三件事同时成立，并且用网关归一化后的口径读数。

**本轮没有做真实冷/热对比**：工作区与部署机都没有可直接使用的模型凭据（部署机凭据要号主明确授权才能用于合成测试），所以如实记为「未取得真实命中证据」，不编造数字。可复现的验证配方（拿到一枚只读凭据即可执行）：

1. 同一前缀（同 system + 同一段历史）连打两次，`temperature=0`、`max_tokens` 固定，中间不换模型/provider；
2. 读第二次的 `usage.prompt_tokens_details.cached_tokens`（或通道原生字段）与首 token 延迟，和第一次比；
3. 再打第三次、把前缀改一个字（例如改 system 里一句），确认命中归零 —— 这一步排除「网关常量回填」的假命中；
4. 每次只改一个变量（先开关缓存控制、再换 provider、再改前缀长度），记录最小可缓存长度与 TTL（同前缀间隔 5 分钟 / 1 小时各打一次）；
5. 结论只写「命中 token / 首 token 延迟 / 是否透传」，不写任何正文。

### 6.3 面板轨迹页（#138）

数据面新增一个**只读**的 ZBA 出口 `GET /zba/v1/traces`（scope Read，operationId `traces.list`）：返回最近若干轮的轨迹**形状**（runId / 结局 / 耗时 / token 数 / 缓存未知标记 / 节点列表）与缓存用量合计（完成轮、活跃轮、容量、命中量、写入量、**未知轮数**）。

刻意**不返回会话 key、不返回消息正文** —— 轨迹页只回答「多少条 / 多少 token / 多少未知」。未知轮数与「命中 0」在响应里是两个不同的东西：响应里缓存用量未知时 `cachedPromptTokens` / `cacheWriteTokens` 整个字段省略（由序列化层的 `WhenWritingNull` 决定），前端必须把「字段缺失」显示成「未知」，不能当 0 参与计算。`/api/dashboard.traces` 仍在，是给旧脚本的兼容出口；面板走 ZBA。

## 7. 怎么复核

```bash
# 轨迹只读出口（形状、缓存三态、鉴权与参数校验）
dotnet tests/BotAgent.ZbaProbe/bin/Release/net10.0/BotAgent.ZbaProbe.dll   # M. turn traces 一节

# 提示词分层与措辞边界
dotnet tests/BotAgent.SafetyProbe/bin/Release/net10.0/BotAgent.SafetyProbe.dll

# 三态统计的解析与汇总
dotnet tests/BotAgent.ProductionSpecProbe/bin/Release/net10.0/BotAgent.ProductionSpecProbe.dll

# 缓存控制参数（默认关逐字一致 / 打开后的字段形状）
dotnet tests/BotAgent.ProtocolProbe/bin/Release/net10.0/BotAgent.ProtocolProbe.dll   # PromptCacheChecks

# 真实主链路（系统提示仍然只有一份、联网段仍可关闭）
BOTAGENT_IT_ONLY=s9  dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll
BOTAGENT_IT_ONLY=s26 dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll
```

观测侧只需要看 `/metrics` 里那三行：命中量、写入量、未知轮数。**没有未知轮数就别算占比**。

老库升级（v12 → v13）单独一条：

```bash
dotnet tests/BotAgent.ReviewRemediationProbe/bin/Release/net10.0/BotAgent.ReviewRemediationProbe.dll --database-legacy-cache
```

## 8. 已知限制（诚实清单）

1. **没有真实供应商的命中证据**。合成桩只能证明请求前缀稳定、统计字段读得对；对方是否真的缓存、缓存多久、最小长度多少，只能在真实通道上实测，本轮没做（链路静态判定见 6.2）。
2. **历史前缀仍会失效**：`(#id)` 窗口、滑窗左移、工具清单变化（第 4 节）。历史时间戳渲染已在 #135 改成绝对时间（`src/BotAgent.Model/ChatHistoryTime.cs`），跨午夜不再整段作废。
3. **缓存控制参数是否在真实链路上有效，仍未被证明**：开关只负责「把字段发出去」，中间网关可以覆盖或剥离它（6.2）。默认关就是为了不让这件事悄悄改变线上行为。
4. **流式只做了归一化，没有端到端用例**：普通聊天不带 `stream`，所以这条路径目前只有解码器级别的覆盖。
5. **「部分未知」的轮次**：同一轮多次模型调用里只要有一次没报缓存统计，这一轮就标记 `CacheUsageUnknown`；已累加的值是「已知部分之和」，不是完整值。
6. **不做前缀冻结**：滑窗是语义上的取舍（最近的对话更重要），本轮不为了缓存去改上下文选择策略。

## 9. 改这里时看什么

- 动 `PromptBuilder`：先跑 SafetyProbe（措辞、预算、时效边界全是硬断言），再跑 s9（系统提示长度哨兵）。
- 动 `ModelTransport`：历史渲染同时影响 `(#id)` 与时间戳，改完必须跑 s19 / s25 / s12。
- 动用量解析：`ModelJson.ReadUsage` 的调用方仍在，别把三态改成两态（`ProtocolProbe` 有一条「缺失仍是 0/0」的老断言，那是给旧口径用的）。
- 不要用「未知填 0」让图好看，更不要在文档里写「命中率 X%」而不同时给未知轮数。
- 动 `ModelProtocolAdapter` / `AnthropicProtocol`：先跑缓存控制探针（默认关必须逐字节一致、打开后形状正确），再看 6.2，别把「发出去」当成「上游会命中」。
- 动轨迹出口：`ZbaProbe` 的 `M. turn traces` 断言行形状集合与 `limit` 校验；改字段名会直接红。
