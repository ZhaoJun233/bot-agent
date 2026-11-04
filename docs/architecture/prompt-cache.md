# 模型输入缓存：稳定前缀、动态尾巴与三态观测

供应商侧的「输入缓存」是按 **token 前缀** 命中：一次请求的开头如果能和上一次逐字相同，那一段就可能复用对方的 KV 缓存；中途第一个变化的位置之后，全部重算。#98 做的就是把这个事实落到本仓的请求拼装与统计上。

这一页回答三个问题：请求里哪些内容属于稳定前缀、哪些是每轮必变的动态尾巴；缓存用量从哪里读、怎么看；以及现在**还做不到**什么。

## 1. 边界（先说不做什么）

- **只搬家，不改字**：分层没有改任何一条指令的措辞、优先级、权限判定、脱敏或会话隔离。SafetyProbe 的措辞断言与集成场景的链路断言都在守着这一点。
- **不承诺命中率**：命中多少取决于上游的缓存策略、最小可缓存长度、TTL 与冷启动，本仓不做保证，也不拿「复用旧答案」之类的手段伪造命中。
- **缓存控制参数默认关**：#138 起可按通道打开（供应商 options 的 `promptCache`，默认 `false`），关着时请求体与 #135 之后逐字节一致；打开后 OpenAI 兼容通道带 `prompt_cache_key`、Anthropic 在稳定前缀边界挂 `cache_control`（见 6.1）。
- **没有真实供应商证据**：合成桩只能证明「请求形状稳定、统计读得对」，不能证明「对方真的命中了」；计划中的 live 探针（6.2.1）尚未运行。这条写在第 8 节，不藏。

## 2. 一次请求是怎么拼出来的

普通聊天（非 `//` 任务）的链路是：

```
BotAgentHost -> ReplyPipeline -> SourceScheduling -> ReplyTurnContext.BuildAsync
  -> AgentTurnLoop.RunAsync（最多 3 步）
  -> OpenAiClient.CompleteAsync
  -> PromptBuilder.BuildParts（Stable / Dynamic 系统提示）
  -> ModelTransport.BuildAsync -> BuiltRequest（非 wire：Payload + 图片元数据 + StableSystemPrefixLength）
  -> ModelProtocolAdapter.CreateRequest（每次尝试才生成 wire JSON、认证头与缓存字段）
```

关键事实：

- 整条系统提示是 **唯一一条 `system` 消息**（`ModelTransport.BuildAsync` 组装；带图时的那句尾注会追加进同一条 `system`）。
- 历史消息由 `ModelTransport.BuildAsync` 渲染，最后补一条 `user`。
- 请求体里没有会话 id / trace id / 随机 id / `user` / `metadata`，聊天也从不带 `stream` —— 也就是说，**不存在「每轮随机的标识符」在破坏前缀**。
- 工具定义是**文本清单**（`[可用工具]`），不是 JSON schema；源是静态只读数组，两处 `Where` 保序，没有排序抖动。
- 组装结果是 `BuiltRequest`：`Payload`（canonical JSON）+ 附加图片计数/ID + `StableSystemPrefixLength`。它**不是 wire 边界**：`StableSystemPrefixLength` 明确不进 wire JSON；真正发出去的 JSON 由 `ModelProtocolAdapter.CreateRequest` 在**每次尝试**生成，`OpenAiClient` 在 `BuildAsync` 之后用 `built with { StableSystemPrefixLength = promptParts.Stable.Length }` 补上该元数据。
- 超时/5xx 重试、去图重试与 provider 故障转移都复用同一个 `BuiltRequest`（transport 只在请求本地深拷贝上覆盖 `model`，不改调用方 `Payload`）；协议级改写（剥离 `prompt_cache_key`、Anthropic 断点标记）因此每次尝试都按当前 route/option 重新收口，不会把上一个通道的 wire 形状带过去。

因此 #98 的优化点只有一处：**系统提示内部，谁在前面**；#138 的缓存控制参数则是在这个前缀已知之后，才在边界上做标记（6.1）。

## 3. 稳定前缀与动态尾巴（本轮改动）

`src/BotAgent.Model/Model/PromptBuilder.cs` 的 `BuildParts` 返回 `PromptParts`：`Stable` 与 `Dynamic` 两段（`Text` 只是 `string.Concat(Stable, Dynamic)`），`Build` 仍返回 `BuildParts(...).Text`。所有「每轮会变」的段落只往 `Dynamic` 写，静态段落只往 `Stable` 写；两段的文字内容与 #135 之后逐字一致（只搬家，不改字）。

| 层 | 内容 | 为什么放这里 |
| --- | --- | --- |
| 稳定前缀 | 机器人身份档案、人设、可发言判断规则、`[可用工具]` 清单、联网搜索段（开关开时才有）、表情包指令、动作契约 | 同一份配置下逐轮逐字相同 |
| 动态尾巴 | 心情、上一轮的「感觉」、刚查到的资料、会话参与者档案、撤回提示、本群身份、戳一戳上下文、表情包候选、**发言意愿数值**、**[现在的时间]** | 每轮或每分钟会变 |

两个具体动作：

- **时间块搬到最末尾**：`[现在的时间]` 原来紧跟身份，现在由 `BuildTimeSection` 单独产出、在 `BuildParts` 的最后追加（顺序仍是 Stable 在前、Dynamic 在末尾）。它是分钟级变化的内容，放在越后面，前面能命中的部分越多。
- **发言意愿数值拆成动态**：`BuildSuitabilityInstruction` 的静态规则留在稳定前缀，末尾那句随气氛阻尼变化的意愿值改为写入 `dynamic`。

要点：**时间仍然注入、仍然只有一个基准**（见 `docs/architecture/temporal-awareness.md`），只是位置后移，措辞没动。

`PromptParts.Stable.Length` 会作为 `BuiltRequest.StableSystemPrefixLength` 交给发送层（只是元数据，**不进 wire JSON**）；Anthropic 的缓存断点只在这个已知边界上标（见 6.1）。

## 4. 还会破坏前缀的三件事（未做）

这些是调查里确认存在的，按收益从大到小：

- ~~**历史时间戳渲染**~~ —— **#135 已修**。原来是「今天 `HH:mm`、更早 `MM-dd HH:mm`」：跨午夜时
   **每一条今天的消息都会换一种写法**，整段历史前缀作废。现在历史行一律走绝对形式 `MM-dd HH:mm`
   （`src/BotAgent.Model/ChatHistoryTime.cs`，由 `src/BotAgent.Model/ModelTransport.cs` 的 `FormatTime` 调用）：
   渲染结果**只取决于消息本身**，与「现在几点」无关；「今天是哪天/现在几点」由提示词末尾的
   `[现在的时间]` 一段承担（`docs/architecture/temporal-awareness.md`）。守护测试：
   `tests/BotAgent.SafetyProbe/PromptCacheStabilityTests.cs` 的两条 `★ #135` 断言。
1. **`(#id)` 只加在最近 16 条**（`ModelTransport.cs:106-109`，配合 `OpenAiClient` 的 `TakeLast(16)`）。窗口滑动时第 17 条前后的渲染标记会变一次。
2. **历史滑窗左移**：`ReplyTurnContext.BuildAsync` 与 `OpenAiClient` 各有一处同义裁剪（阈值分别来自面板字段与默认值，取小者）。窗口满了之后每轮丢最旧一条，前缀结构必然变。要改需要「前缀冻结」策略，风险最高。
3. **`[可用工具]` 随集合内容变**：顺序稳定，但策略开关、平台源、待审批状态一改，清单内容就变。这是语义要求，不做优化。

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
| anthropic | **不发**顶层字段（Anthropic 拒绝未知顶层字段）。开启且拿到**已知稳定边界**时：首条 system 的简单文本被拆成 stable / dynamic 两个 text block，**只给 stable 块**挂 `cache_control: {"type":"ephemeral"}`（动态 system 与最后一条消息都不标）；没有边界（`StableSystemPrefixLength` 为 null）时退回旧形状（首个 system 末尾与最后一条消息最后一个 content block）；传了边界但无法安全拆分（非简单文本、边界越界）时 **fail closed**，一个断点都不加；关闭时与旧形状逐字节一致。 | 这是 Anthropic 协议里真实的断点标记；标在动态内容上会让断点失去复用意义 |
| gemini / ollama | 两种都不发 | 两个协议没有等价字段，发了只是噪声 |

开关落在 `ModelProtocolOptions.PromptCache`（默认 `false`，随供应商配置持久化）。payload 里只放**规范字段**，由 `ModelProtocolAdapter.CreateRequest` 按路由决定发不发 —— 这样不必给 20+ 参数的 `IModelClient.CompleteAsync` 增加通道特判。

#### 6.1.1 开关与热应用

`PromptCache` 是供应商 options 里的一个布尔，默认关闭。已确认支持热应用的 **7 个普通行为字段** 是：`aiModeEnabled`、`botPersona`、`groupCooldownSeconds`、`privateCooldownSeconds`、`maxConcurrentReplies`、`maxContextMessages`、`maxMessagesPerConversation`。改完后 **新的普通请求** 使用新配置，**在途 turn 不重写**。`PromptCache` 不属于这 7 个字段；它通过供应商注册表重建热应用，开、关后的下一条请求分别带上、移除协议对应的缓存控制字段，不需要重启宿主。

配置保存入口只额外允许明确声明的 `ModelProtocolOptions` 叶类型进入字段校验；其它模型类型及未知 options 字段仍拒绝。S61 在同一宿主进程中验证默认关闭、开启、再关闭后的下一条实际 HTTP 模型请求，并验证未知 options 字段返回 400。

失败边界：模型候选表加载、构建或替换失败时，模型应用步骤回执为 `Failed` + `model_projection_rebuild_failed`，而不是 `RestartRequired`。持久配置已经提交与运行期应用成功是两件事。旧候选表仍在不代表它可以继续接收请求：desired options/credentials 与运行期快照不一致时，路由边界仍按 **fail closed** 拒绝，不能承诺旧实例继续服务，也不能把“重启即可”当作成功回执。

### 6.2 真实通道证据：拿到的是「链路会怎么处理」，不是命中数（#138）

本轮把部署链路的两跳按源码读完了（handler 入口 → 上游 provider 池），能确定**字段会不会被改写**，不能确定**上游到底命中多少**：

- **第一跳（自建网关，Go）**：`prompt_cache_key` 在请求结构体里有显式字段（不是被结构体解码丢掉的未知字段），转发时确实写回上游 body。但它会**改写**这个键：客户端没给、而 provider 需要时由网关按会话自己派生；客户端给了也可能被网关的键覆盖；provider 能力表不支持时直接从报文里删掉；上游以 400 明确点名该字段时，网关会把它标记为「此 provider 不支持」并**此后永久剥离**、重发一次不带它的请求。Responses 通道同理。
- **第二跳（可选 cli-proxy 层）**：配置 `support-prompt-cache-key` **默认关**。关着时不动请求；开着时**保留客户端已给的键**，没有才自己派生。另有一条搜索路由会显式删除 `prompt_cache_key` / `prompt_cache_retention`。
- **命中口径也是网关归一化的**：`usage.prompt_tokens_details.cached_tokens` 是网关把各家字段（DeepSeek 的 `prompt_cache_hit_tokens`、Moonshot 的 `cached_tokens`、Anthropic 的 cache_read 等）统一后的结果；网关自己还有 Anthropic 缓存断点与亲和路由。所以宿主观测到的「命中」不必然等于上游原生命中。

结论：**宿主发的键最多是建议值**。要拿到真实命中证据，必须「中间层不覆盖 + 上游真支持 + 前缀真稳定」三件事同时成立，并且用网关归一化后的口径读数。

**#138 初次交付未做真实冷/热对比**；2026-11-04 经授权完成了后文所述的有界合成实测，但仍未取得真实命中证据。模型调用会产生用量，不存在通用的“只读模型调用凭据”；下面的配方也必须先取得单独调用授权与预算（自动探针见 6.2.1）：

1. 同一前缀（同 system + 同一段历史）连打两次，`temperature=0`、`max_tokens` 固定，中间不换模型/provider；
2. 读第二次的 `usage.prompt_tokens_details.cached_tokens`（或通道原生字段）与首 token 延迟，和第一次比；
3. 增加前缀负控并观察命中量是否变化；缓存可能按块共享部分前缀，不能仅凭改一个字就要求一定归零，也不能把单个零值当成路由稳定性的证明；
4. 每次只改一个变量（先开关缓存控制、再换 provider、再改前缀长度），记录最小可缓存长度与 TTL（同前缀间隔 5 分钟 / 1 小时各打一次）；
5. 结论只写「命中 token / 首 token 延迟 / 是否透传」，不写任何正文。

### 6.2.1 自动化 live 探针 `--live-cache` 与实测停止记录

手动配方容易变成「看一次数字就下结论」，所以另有可审计的合成探针 `tests/BotAgent.ProtocolProbe/LiveCacheChecks.cs`。**2026-11-04 实际发出 4 次请求后触发预算护栏，立即停止；没有重试，也没有追加第 5–7 次请求。真实缓存效果未验证通过。**

前三次为相同 wire 的固定基线：每次 14,017 UTF-8 字节，输入上界预检 14,529；返回 input 均为 2,431，output 依次为 50 / 50 / 54，native cache-read 均为 **0**、cache-write 均为 **null**，BotAgent 解析完全一致。耗时依次为 5,705 / 4,837 / 4,705 ms，耗时变短不能替代缓存命中证据。三次 wire SHA-256 均为 `875173a0d89b97697cd711199fb4ac15ceb1b16e471a737bfc49ca5e5cd9e48d`。

第 4 次（真实构建路径首个动态样本）返回后触发 `reported_token_budget`。运行时版本先检查预算、后打印用量，因此该次只保留停止原因和累计请求数，**不知道是 input 还是 output 越限，不能推断它是否命中**。随后补充了仅输出原生计数的预算失败诊断，但没有重跑或回填缺失证据。第 5–6 次动态热样本和第 7 次负控均未执行，故“不改原文的稳定边界”和“计数解析正确”已得到离线支持，真实热命中验收仍未满足。

本次读到的是当前网关 HTTP 响应中的 usage，网关可能已归一化上游字段（见 6.2）；未独立查询上游账单或绕过网关。没有修改线上配置、切换模型路由或部署服务。

- **双门**：必须同时满足 `--live-cache` 与 `BOTAGENT_LIVE_CACHE=1`，否则进程不执行（`tests/BotAgent.ProtocolProbe/Program.cs`）。目标与凭据用 `BOTAGENT_LIVE_CACHE_BASE_URL` / `_API_KEY` / `_MODEL` / `_PROTOCOL` 传入；可选 `TIMEOUT_SECONDS`（1..120，默认 60）与 `ALLOW_INSECURE=1`（http / 回环）。不读配置文件、不读生产会话。
- **预算**：客户端最多 8 次请求，计划 7 条（第 8 槽不使用）。所有请求先按完整 wire UTF-8 字节数 + 512 framing 余量做保守输入上界预检，要求 ≤16,000；发送输出 token 上限 64，响应体限制 128 KiB。返回后另校验原生计数，越限立即停止，不能假设网关一定遵守输出上限。客户端不重试，但不能据此保证中间网关或上游没有内部重试。
- **不重试、不故障转移**：直接 `HttpClient` 单发，绕开 `ModelTransport.SendAsync` 的生产重试；任何非 2xx（含 401/429/参数不支持/重定向）立即 abort 且不读 body。
- **不记录正文**：只输出状态、请求数、阶段、长度/哈希/耗时、原生与归一化用量及一致性。预算失败只输出原生计数与停止原因；不打印请求或响应正文、URL、凭据，也不打印异常消息。
- **合成与隔离**：离线断言对 openai-chat / responses / anthropic 三个协议各组装一遍；live 时按 `_PROTOCOL` 选单一协议，计划 3 个固定、3 个动态、1 个前缀负控请求，任何护栏失败即提前停止。**禁止用 padding 凑最小可缓存长度，禁止把不同会话/不同前缀的内容拼进同一请求**。
- **离线断言**（无凭据也可跑）：开关关时 wire 逐字节一致、开时字段/断点形状正确、稳定前缀 hash 三轮相同、动态段三轮不同、未知 usage 仍为未知。命令见第 7 节。

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

# 缓存控制参数（默认关逐字一致 / 打开后的字段形状 / 离线断言）
dotnet tests/BotAgent.ProtocolProbe/bin/Release/net10.0/BotAgent.ProtocolProbe.dll   # PromptCacheChecks

# live 探针（双门 + 凭据；未获授权时不运行，见 6.2.1）
# BOTAGENT_LIVE_CACHE=1 dotnet tests/BotAgent.ProtocolProbe/bin/Release/net10.0/BotAgent.ProtocolProbe.dll --live-cache

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

1. **真实缓存命中尚未验证通过**。2026-11-04 的前三次固定基线 cache-read 均为 0；第 4 次触发预算护栏后停止，缺少两次动态热样本与负控。离线测试不能覆盖这一证据缺口，详见 6.2.1。进一步实测需要重新确认预算；不得通过增加 padding、拼接会话或静默换路由来追求命中。
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
- 动 `BuiltRequest` / `StableSystemPrefixLength`：确认它仍是非 wire 元数据（不进 JSON）、跨重试/故障转移不丢；Anthropic 边界拆分只在首条 system 是简单文本时进行。
- 动 `ModelProtocolAdapter` / `AnthropicProtocol`：先跑缓存控制探针（默认关必须逐字节一致、打开后形状正确），再看 6.2，别把「发出去」当成「上游会命中」。
- 动轨迹出口：`ZbaProbe` 的 `M. turn traces` 断言行形状集合与 `limit` 校验；改字段名会直接红。
