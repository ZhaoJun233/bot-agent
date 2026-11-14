# QQ 私域 / 官方通道功能与证据对照

日期：2026-11-12。配套：[主计划](qq-official-parity-2026-11.md)、[执行清单](qq-official-parity-execution-2026-11.md)。源码固定于 `afbda550d44df6191e173561808eb280f83e6f46`。

“已有”仅表示此基线存在对应实现；没有在本次运行实际机器人验证。“未查到”仅限本次核对的 QQ 群 / C2C 官方资料，不能写成平台永久不支持。

## 1. 功能矩阵

| 能力 | 私域 OneBot / NapCat 当前实现 | QQ 官方当前实现 | 官方现行资料 / 完善目标 | 优先阶段 |
| --- | --- | --- | --- | --- |
| 群聊 / 单聊文本 | 有 | 有 | 保持共用会话、白名单和回复管线，修投递结果 [O1–O3] | P2 |
| 群内所有消息 | 接收协议端事件 | 有 `GROUP_MESSAGE_CREATE` 分支 | 取决于群接收设置；不能以“连接在线”推断全量 [O4/O9] | P1/P2 |
| 点名与机器人自我识别 | OneBot 自身 ID 与协议事件 | @事件当作点名；全量依赖 `mentions[].is_you` | 当前 schema 提供成员标识、`bot`，未列 `is_you`；需已验证的自我身份匹配，不把其它机器人当自己 | P1 |
| 群成员身份 | 数字 ID + role / 名片等，支持成员查询 | `member_openid` 转别名；缺失时退用群 ID | 禁止群冒充用户；按应用 / 类型 / 群保存，消费角色与名称字段 [O4] | P1 |
| 单聊身份 | 协议数字 ID | `user_openid` 转别名 | 稳定但不等于真实 QQ 号；不能与群成员 / 私域自动合并 [O4] | P1 |
| 昵称、群名、机器人群角色 | 查询入口已有 | 固定后缀显示名；群名返回 null；未查询 bot_state | 事件可带昵称与角色；群信息 / bot_state 有接口但有访问权限要求 [O7/O9] | P1/P4 |
| 群成员列表 / 详情 | 查询详情已有；不能因此推定所有 UI 均有列表能力 | 未接入 | 官方列表每页最多 30，详情含角色；**内邀 / 白名单** [O7]；默认不拉全量成员 | P4 |
| 发送图片 | 有 | 上传 + `msg_type=7` | 补 TTL、回执、场景隔离、引用和预算 [O2/O3] | P2 |
| 接收图片 | 有；有图片地址刷新入口 | 只提取 `attachments` 中 image 类型 | 延续受控下载，失效不伪造内容；未确认官方等价刷新接口 | P2 |
| 发送语音 | TTS URL 交协议端，协议端转码 | 下载 SILK + base64 上传后发富媒体 | 上传和投递分开计状态；保留明确失败 / 降级 | P2 |
| 接收语音 / 识别文本 | 依协议解析及宿主能力；本次未单独验收 ASR | 当前附件解析只取图片，未读语音 / ASR 字段 | 官方事件列 `voice_wav_url`、`asr_refer_text`；先明确输入契约、隐私和限额再接入 [O4] | P2 后续切片 |
| 可见引用 / 被引用消息识别 | `reply`、`get_msg` 等入口已有 | `SupportsQuote=true`，但出站忽略指定引用、入站不解析引用结构 | `message_reference` 与 `msg_type` 分开；入站有 `message_type=103` / `msg_elements` / 引用索引 [O2–O4] | P2 |
| 拉取历史 / 任意消息原文 | 有查询入口 | `GetMessageInfoAsync` 返回空 | 未确认群 / C2C 任意 get_msg 等价接口；优先本地已有引用，缺失明确不可取 | 保持降级 |
| 自己发的消息撤回 | 有，受协议权限影响 | 未接入，能力声明 false | 群 / C2C 已有 DELETE，时限两分钟 [O5] | P3 |
| 撤回其他成员消息 | 私域动作可调用，角色仍受协议限制 | 未接入 | 官方群管理员可撤普通成员消息；与撤自己消息分权限 [O5] | P4 可选 |
| 禁言 / 解禁 | `set_group_ban` 有 | 未接入 | 官方 `restrict_chat_setting`；机器人需管理员，不能禁群主 / 管理员 / 机器人，最多 30 天 [O6] | P4 |
| 移除群成员 | `set_group_kick` 有 | 未接入 | 官方批量移除属于内邀，单次最多 20；本项目首批限制一人、不附加黑名单 [O8] | P4 条件项 |
| 戳一戳 / 资料点赞 / 表情回应 | 有动作入口 | 无 | 本次未找到 QQ 群 / C2C 等价接口；消息反馈事件不是“给别人点赞”动作 | 保持不可用 |
| 群名片 / 群改名 / 主动退群 | 有动作入口 | 无 | 本次未找到等价接口；不能用群信息查询冒充编辑能力 | 保持不可用 |
| 表情包 | 收藏表情拉取和图片发送入口 | 无收藏表情入口，`SupportsStickers=false` | 若是授权静态图片，可后续复用图片发送；不承诺 QQ 收藏表情同步 | P5 可选 |
| 音乐分享 | 原生 music 卡片 | 网关可降级为歌名文字；平台 Music 能力 false | 某些调用会在策略层被禁用，不能宣称所有路径已降级成功；后续明确“文字推荐 / 图片卡片”与原生音乐卡片差别 | P5 |
| 链接 / 搜索结果 | 文本可携链接 | 全部 http(s) 链接被替换 | 按实际链接权限说明原因；允许域需有证据，不能自己推断 [O2/O3] | P2 |
| Markdown / 图文卡片 / 按钮 | 不按官方模板接口发送 | 未接入 | 官方已有 Markdown / 键盘，10-28 新增图文卡片；各自申请与模板权限独立 [O2/O3/O10/O13] | P5 |
| C2C 流式消息 | 不按官方流式接口发送 | 未接入 | 官方 C2C 有独立流式接口；群发送页明确不支持流式参数 [O1/O2] | 后续选项 |
| 内置插件 / 检索 / 画像 / 记忆 | 共用插件、工具和记忆设施 | 共用设施，但发送与身份能力不足 | 纯处理能力可复用；记忆可靠身份、工具可见性、媒体降级需端到端验证 [C6/C7] | P1–P3 |
| Agent `qq` 动作 | 10 动作目录与会话宿主已有 | 宿主无法构造且私域限定 | 复用目录 / 白名单 / 审批，按官方逐动作接线 [C5] | P3/P4 |
| Python / MCP 插件 | 发现、允许表、超时与世代租约已有 | 工具结果可经共用宿主处理；无官方动作回调契约 | 不复制插件宿主，首轮结果经宿主投递；后续动作申请须版本化协议及宿主裁决 [C8] | P3，主动动作后续 |
| 主动 / 定时消息 | 有共用上层机制，受开关 / 预算约束 | 无被动凭据时可主动发；错误时也会主动重试 | 按群 / 用户许可、主动频控和新操作意图裁决，不能保证定时任务必达 [O1–O3] | P2 |
| 连接与回调 | 现有 OneBot transport | WS token / identify / heartbeat / resume | 官方仍有 WS 和 Webhook 文档；不用“必须弃用 WS”作为计划前提 [O11/O12] | P2/P6 |
| 去重、权限变化、脱敏诊断 | 既有平台生命周期设施 | 进程内 200 条 raw id 去重；移群仅记日志；REJECT 可打印原文片段 | 按事件契约去重、按关系失效任务和缓存，仅输出原因 / 计数 | P1/P2 |

## 2. 插件动作逐项对齐

业务动作名继续复用既有目录；表中的“候选适配”不是已实现功能。

| 动作 | 私域实现 | 官方候选适配 | 约束 / 不等价点 |
| --- | --- | --- | --- |
| `send` | `SendTextAsync` | POST 群 / 用户 messages | 绑定当前目标、主动 / 被动规则；Agent 风险档不随普通聊天改变 |
| `recall` | `delete_msg` | DELETE 群 / 用户 messages | 已知会话、原生消息 ID、两分钟；撤自己与撤别人分权限 |
| `ban` | `set_group_ban` | POST 群 restrict_chat_setting | 秒数转到期时间，解禁为 del；管理员和目标身份实时校验 |
| `kick` | `set_group_kick` | POST 群 batch_remove_members | 内邀；首批一人；黑名单独立，不静默映射 `rejectAdd` |
| `like` | `send_like` | 暂无等价证据 | 官方 LIKE 反馈表示用户评价机器人消息，不等同此动作 |
| `poke` | 群 / 私聊 poke | 暂无等价证据 | 不用发一句“戳了你”冒充执行成功 |
| `emoji_like` | message emoji like | 暂无等价证据 | 同上，客户端表情互动不能凭字段名称类比 |
| `card` | set group card | 暂无等价证据 | 可读 nickname 不代表可改群名片 |
| `group_name` | set group name | 暂无等价证据 | `/info` 是读接口 |
| `leave` | set group leave | 暂无等价证据 | DEL_ROBOT 是被移出事件，不是退群动作 |

成员 / 机器人状态查询优先作为宿主内部的授权及显示依赖，不默认注册成可让任意插件导出全群信息的新工具。入群审批、黑名单、自动审批策略、@全体等虽然已有官方资料，属于另外的管理能力，不搭车进入首轮动作允许表。

## 3. 源码证据索引

行号固定于上述基线；后续改代码后需更新证据索引。可使用所列方法名定位，不以旧注释覆盖当前官方文档。

| 编号 | 位置与可核查事实 |
| --- | --- |
| C1 | [OfficialBotGateway](../../src/BotAgent.Platforms/Official/OfficialBotGateway.cs#L43)：只实现 `IQqChatSource` / staged reply；173 起发送文本，285 图片，310 群名返回空，317 查消息返回空，539 起事件分派，592–622 群身份 / 消息，708–738 C2C / 显示名 |
| C2 | [OfficialIdMap](../../src/BotAgent.Platforms/Common/OfficialIdMap.cs)：官方默认构造非 durable；所有 raw 字符串共表，哈希候选和冲突探测，保存失败处理；[Channels](../../src/BotAgent.Core/Domain/Qq/Channels.cs#L144)：`official:` 兼容 key；[CompositionRoot.Channels](../../src/BotAgent.Headless/Host/CompositionRoot.Channels.cs#L148)：`official-ids.json` 和 legacy 账号装配 |
| C3 | [OfficialBotGateway](../../src/BotAgent.Platforms/Official/OfficialBotGateway.cs#L195)：任意被动失败再主动；213 StripLinks；825/853 返回 bool / SendResult；922 HTTP 封装，943 将正文包含 11244 / 11253 当 token 重试；1051 最新消息 / 窗口 / 序号；1079 仅 raw ID 的内存去重；573、844、872、978 等原始片段日志 |
| C4 | [PlatformCapabilities](../../src/BotAgent.Core/Domain/Platforms/PlatformCapabilities.cs)：官方 Quote=true，Recall / Stickers / Music / Poke=false；[PlatformPolicy](../../src/BotAgent.Core/Domain/Platforms/PlatformPolicy.cs#L57) 和 [PlatformPolicyResolver](../../src/BotAgent.Core/Contracts/PlatformPolicyResolver.cs)：现有能力 / 策略分层；[平台策略说明](../architecture/platform-policy-resolution.md) |
| C5 | [AgentCommandService.BuildQqHost](../../src/BotAgent.Headless/Services/Agent/AgentCommandService.cs#L384)：来源需可转 `IQqActions`；[SessionQqActionHost](../../src/BotAgent.Headless/Services/Agent/QqActionTool.cs)：非私域拒绝，统一入口把字符串结果包成 Success；[QqActionCatalog](../../src/BotAgent.Engine/Plugins/QqActionCatalog.cs)：十动作与默认安全四项；[ServerAgentRunner](../../src/BotAgent.Headless/Services/Agent/ServerAgentRunner.cs#L43)：每任务最多五次 QQ 动作 |
| C6 | [ToolVisibility](../../src/BotAgent.Engine/Tools/ToolVisibility.cs)：来源、插件、开关与快照 / 实时策略；[PluginGate](../../src/BotAgent.Engine/Plugins/PluginGate.cs)：未知插件能力有意放行，不是总授权器；[ToolOutcome](../../src/BotAgent.Core/Domain/Tools/ToolCall.cs#L22) / [DeliveryResult](../../src/BotAgent.Core/Domain/Platforms/DeliveryResult.cs#L18)：可复用的结构化结果 |
| C7 | [QqChatSourcePlatformAdapter](../../src/BotAgent.Platforms/Governance/QqChatSourcePlatformAdapter.cs)：把 long 别名文本包装成 ParticipantId；[UnifiedMemoryTurn.AttestedContext](../../src/BotAgent.Engine/Reply/UnifiedMemoryTurn.cs#L35) 与 [UnifiedMemoryCommandService](../../src/BotAgent.Headless/Services/Memory/UnifiedMemoryCommandService.cs#L64)：来源验证后使用 reliable identity；[MemberRoleUseCase](../../src/BotAgent.Engine/Conversations/MemberRoleUseCase.cs)：群成员角色维护；[记忆管理身份](../../src/BotAgent.Headless/Host/CompositionRoot.Memory.cs#L35) 精确匹配来源，不等同 QQ 群管理员 |
| C8 | [PythonWorkerToolSource](../../src/BotAgent.Mcp/PythonWorkerToolSource.cs)：protocolVersion=1，initialize / tools/list / tools/call，调用参数没有可信会话身份回调协议；[PluginPythonSources](../../src/BotAgent.Headless/Adapters/Plugins/PluginPythonSources.cs)：包工具接入 MCP；[ServerAgentRunner](../../src/BotAgent.Headless/Services/Agent/ServerAgentRunner.cs#L603)：受控执行与 Python 世代租约 |
| C9 | [OneBotActions](../../src/BotAgent.Platforms/OneBot/OneBotActions.cs#L35) / [OneBotQueries](../../src/BotAgent.Platforms/OneBot/OneBotQueries.cs#L145)：私域发送、管理、成员 / 历史查询；[InboundModels](../../src/BotAgent.Core/Contracts/InboundModels.cs#L32)：角色、引用、图片和来源信封字段 |
| C10 | [OfficialChannelScenario](../../tests/BotAgent.IntegrationHarness/OfficialChannelScenario.cs) / [MockOfficialGateway](../../tests/BotAgent.IntegrationHarness/MockOfficialGateway.cs)：现有合成集成场景；[Program](../../tests/BotAgent.IntegrationHarness/Program.cs#L138) 当前仍注册 `s42`；[csproj](../../tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj#L5) 当前 net10.0。历史文档中的“s42 禁用 / net8.0”不能作为当前命令 |

## 4. 官方资料索引与可用性边界

均于 2026-11-12 从 QQ 机器人官方站读取；只读取公开文档，未登录开放平台、未查询真实用户或群。访问 HTTP 200 不足以证明命中正确文档：旧路径可能返回“启动接入”首页，本次按实际页面标题、接口路径、schema 和侧栏链接交叉核对。

| 编号 | 官方来源 | 本计划采用的事实 |
| --- | --- | --- |
| O1 | [消息收发概述](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/message/overview.html) | 群 / C2C / 频道区分；被动窗口与次数；主动消息可被用户关闭；上传有 TTL；不能把频道限制套到 QQ 群 |
| O2 | [发送群聊消息](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_messages.post.html) | 群 5 分钟 / 5 次；`msg_id`、`event_id`、`msg_seq`、`message_reference` 含义；群不支持流式参数；模板、URL、权限及配额错误码 |
| O3 | [发送单聊消息](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_users_user_openid_messages.post.html) | C2C 60 分钟 / 4 次；主动 / 召回消息规则；引用、媒体、拒收和无好友关系等错误 |
| O4 | [群@事件](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/group_at_message_create.html)、[群全量事件](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/group_message_create.html)、[单聊事件](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/c2c_message_create.html) | `author` 中昵称、角色、机器人字段和可空统一标识；语音附件；引用结构；去重文字存在不同索引口径，需 P0 明确 |
| O5 | [撤回群消息](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_messages_message_id.delete.html)、[撤回单聊消息](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_users_user_openid_messages_message_id.delete.html) | 两分钟；群管理员能撤普通成员消息，普通机器人只能撤自己；群接口权限 / 超时错误明确 |
| O6 | [设置群成员禁言](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_restrict_chat_setting.post.html) | 管理员要求、普通成员目标、最多 30 天、add/update/del，单次最多 20；不是 OneBot seconds 参数原样转发 |
| O7 | [成员列表](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_members.get.html)、[成员详情](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_members_member_openid.get.html) | **内邀接入**，11253 表示接口无访问权限；列表分页；详情可能有角色 / 昵称 / union_openid |
| O8 | [群成员批量移除](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_batch_remove_members.post.html) | **内邀接入**；最多 20；拉黑可单独失败，不能用 HTTP 成功掩盖步骤部分失败 |
| O9 | [群信息](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_info.get.html)、[机器人群内状态](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_bot_state.get.html) | 群名 / 成员数，机器人 member_openid、角色、主动许可、消息接收设置；有接口访问权限边界 |
| O10 | [互动事件](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/interaction_create.html) | 需要 INTERACTION 订阅位；type 11 / 12 要单次回应；应用 / 会话 / 点击者和授权变更字段，不能一概当普通聊天 |
| O11 | [WebSocket](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/event-emit/websocket.html) | 当前站点仍提供 gateway、鉴权、心跳和 resume；本次无“已废弃”的证据 |
| O12 | [Webhook](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/event-emit/webhook.html)、[签名算法](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/interface-framework/sign.html) | HTTPS 回调、验证握手、签名；新传输必须独立验收，不照抄未经检查的示例密钥 / 日志 |
| O13 | [变更记录](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html) | 2026-08 禁言 / 入群管理，09-03 成员管理，10-28 图文卡片；证明旧注释不足以代表现行能力 |
| O14 | [群关闭通知事件](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/group_msg_reject.html)、[成员退出事件](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/group_member_remove.html) | 关系 / 权限失效需要处理，不能只记日志；成员事件是独立 `1<<24` 位；失效粒度由事件语义与当前许可决定 |

未在此表列出的官方端点，不作为首轮开发已核实依据。后续如接入素材管理、单聊流式、群审批、黑名单或插件宿主回调，先补对应契约和授权证据。

## 5. 开发前必须消除的不确定项

| 问题 | 最小取证方式 | 没有答案时的处理 |
| --- | --- | --- |
| member_openid 跨群稳定范围，union 标识与 user_openid 的关联 | 官方范围说明、SDK schema、经许可的合成 / 专用测试账号案例；只留字段和比较结果 | 群成员、C2C 各自独立，不关联私域 |
| mentions 自我标识在哪些事件可用 | 当前事件 schema + 已知机器人身份来源，旧 / 新格式合成 fixtures | 全量事件自我未知，不当作点名；@事件按事件含义处理 |
| 入站 msg_seq / msg_idx 是否属于同一消息的组成部分 | 每种事件列唯一性规则；重复、变体、乱序示例 | 不用出站序号替代入站身份，不贸然持久化去重规则 |
| 当前应用获准哪些查询 / 管理能力 | 用户批准后读取权限状态或专用测试接口；报告布尔状态、原因码、时间，不贴原文 | 能力未知 / 未授权，管理动作关闭 |
| 角色能否在执行前可靠刷新 | 授权查询或有时效且可失效的可信事件；冻结最大陈旧时间 | 高风险动作拒绝；昵称等展示可以缓存 |
| 旧 alias 的应用、类型及存储引用范围 | 代码 / schema 与合成迁移，必要的受控映射工具仅输出计数和哈希 | 不改旧 key，不凭当前配置把历史重新归属 |

以上问题不妨碍计划交付，但对应实施切片在解决前不能宣称契约已冻结或验收通过。
