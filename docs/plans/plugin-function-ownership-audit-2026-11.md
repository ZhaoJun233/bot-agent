# 插件与系统功能归属审查

日期：2026-11-14。性质：只读代码审查，服务于[解耦总计划](plugin-decoupling-plan-2026-11.md)和[执行清单](plugin-decoupling-execution-2026-11.md)。没有实施迁移，没有连接生产或读取真实会话。

## 1. 基线与口径

- 审查公开 dev 提交：35de38d766f205348dffd51640a44176b1775253；Git tree：d47fa6335c46078443574e5979f5e3e3c78a7ceb。
- 用户确认范围：8 个内置插件、扩展插件，以及平台接入、模型服务、Agent、对话策略、健康报告。最终宿主只保留装配、通用安全和运行基础设施。
- 当前技术是 .NET 10、Vue、独立 Web 和单宿主模块化单体。旧规划中的 .NET 8、内嵌 wwwroot、首阶段插件仅留 Engine 是历史阶段约束；本计划承接其安全与存储原则，推进下一阶段归属，不重做已完成的 Web 分离。
- 这里的“插件拥有功能”包括业务执行、配置规则、工具声明、提示词、接口处理、页面、状态与迁移、后台任务及测试；仅拥有标题和 enabled 不满足目标。
- 审查覆盖仓库内已注册实现及扩展加载机制。未枚举线上安装包，也未审查仓库外每个市场作者的源代码；不能据此声称所有第三方包已经兼容新契约。

### 分级标准

| 级别 | 判据 |
| --- | --- |
| 很高 | 修改或移除功能必须同步改主体的执行流程、配置、提示词/协议、页面等多个知识点；插件入口不能代表完整功能 |
| 高 | 已有局部服务或接口，但生命周期、数据或前端仍由主体拥有；无法独立交付 |
| 中 | 主要实现已封装，依赖稳定公共契约；剩余耦合集中在特定接入点 |
| 低 | 功能包独立拥有前后端与状态，宿主只做通用加载；本次未把任何完整内置功能评为低 |

分级来自下面的调用与归属证据，不是代码行数打分。词项命中文件数只衡量检索散布，不能比较功能质量或充当“耦合百分比”。

## 2. 总体结论

1. PluginRuntime 固定 new 出 8 个预设；其中音乐、语音、表情包、戳一戳、氛围、画像、联网这 7 个类只有元数据和空生命周期，实际执行不在插件类内。TokenQuotaPresetPlugin 实现了真正的 BlockReason 策略，但管理页面、接口和台账仍散在主体。
2. 前端 plugin-settings.ts 明确说明“字段仍然存在原来的配置分区，只决定在哪里编辑”。8 个插件加 5 个系统能力在该表硬编码；它是编辑位置登记，不是前后端完整模块的发现机制。
3. ReplyPipeline、ReplyDelivery、AgentTurnLoop、PromptBuilder 等直接认识多个插件的类型、参数和行为。把 Services 目录搬进插件而留下这些业务分支，仍不能完成解耦。
4. .NET 扩展当前只有生命周期与 IReplyPolicyPlugin 回调，PluginContext 实际只暴露日志服务。Python 包经 MCP 工具来源接入，拥有调用租约，但不是任意 QQ 消息处理扩展。不能把不存在的 OnMessage、ExecuteTool 或前端加载接口写成可直接复用。
5. 现有统一授权、来源租约、工具可见性、配置事务、操作回执、包校验和 generation 排空都有保留价值。它们应成为通用基础设施，不能在每个插件内重新实现。

## 3. 可复核的静态散布

扫描 819 个 git 跟踪的 src 下 .cs/.ts/.vue/.csproj 文件，大小写不敏感的功能词项匹配，包含注释。当前集合不含测试、文档、生成目录及运行数据；文件可同时命中多个功能。结果不是 AST 依赖图，未证明命中处都要迁移。完整模式、命中行和文件 SHA256 留在 `<workspace-review-artifacts>/plugin-decoupling-plan-20261114/coupling-scan.json`；同目录 `scan-coupling.py` 接受本工作树绝对路径即可复跑。

| 功能 | 命中文件 | Headless | Engine | Core | Model | Platforms | Storage | Panel | Web |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 音乐 | 96 | 28 | 14 | 23 | 3 | 8 | 4 | 0 | 16 |
| 语音 | 101 | 31 | 12 | 27 | 3 | 6 | 3 | 0 | 19 |
| 表情包 | 75 | 24 | 14 | 24 | 2 | 0 | 5 | 0 | 6 |
| 戳一戳 | 84 | 19 | 16 | 25 | 2 | 9 | 4 | 0 | 9 |
| 氛围/心情 | 59 | 17 | 12 | 16 | 3 | 0 | 5 | 0 | 6 |
| 画像 | 29 | 12 | 2 | 8 | 2 | 0 | 3 | 0 | 2 |
| 搜索/链接 | 55 | 14 | 16 | 16 | 2 | 0 | 1 | 0 | 6 |
| Token 配额 | 25 | 11 | 1 | 4 | 0 | 0 | 1 | 2 | 6 |
| 平台接入 | 146 | 53 | 18 | 27 | 2 | 34 | 4 | 0 | 8 |
| 模型服务 | 61 | 25 | 0 | 12 | 12 | 0 | 4 | 0 | 8 |
| Agent/MCP | 83 | 34 | 12 | 13 | 0 | 0 | 2 | 3 | 12 |
| 对话策略 | 68 | 25 | 24 | 15 | 1 | 0 | 1 | 0 | 2 |
| 健康报告 | 21 | 13 | 0 | 5 | 0 | 0 | 1 | 0 | 2 |

Agent/MCP 另有 7 个 BotAgent.Mcp 文件，表内未单列。画像采用 MemberProfile/ProfileSummary 等窄词项，其 29 不能解释成画像比其他功能更简单。平台基础发送契约中的 music/voice 等字段也会命中，迁移前必须做语义分类。

## 4. 八个内置插件逐项归属

| ID / 功能 | 前端与管理入口现状 | 后端、状态和运行现状 | 程度与应收回的责任 |
| --- | --- | --- | --- |
| P01 preset.feature.music | plugin-settings.ts 的 music 分区；全局 field-labels.ts；WebUiServer.Music.cs 留有扫码和测试处理 | Headless/Services/Music 全家；CompositionRoot.Model 创建客户端、音源、缓存和存储；Engine 入站媒体、工具执行、Model 的 MusicText/提示词；Storage/MusicStore、AudioCache | 很高。音乐包拥有音源、扫码登录、解析/分析、工具、提示、字段、页面、缓存清理；平台插件只拥有音乐消息的协议编码与降级回执 |
| P02 preset.feature.voice | 全局 voice 分区和 secret slot；WebUiServer.Voice.cs 的试听、健康、克隆管理 | VoiceUseCase/VoiceService 在 Headless；ReplyDelivery 编排分段语音及文字降级；Model 包含语音阈值/提示；Storage/TtsConfFile 与密钥投影 | 很高。语音包拥有 TTS、音色、分段/呈现策略及前端；通用提交边界保留，协议插件拥有上传和发送；不能把 Unknown 当成“未发”再补发 |
| P03 preset.feature.stickers | behavior 中 5 个字段；旧 /api/stickers 管理路由 | Engine/StickerService；Headless 创建并 Load；BotScheduler 负责巡检开关；入站收图、候选提示、ReplyDelivery；Storage/StickerStore | 很高。表情包包拥有素材库、收录/审核/候选/冷却/巡检/UI；宿主只调度已登记任务，协议层不理解表情业务 |
| P04 preset.feature.poke | behavior 开关与冷却；QQ 动作目录另有 poke | Engine/PokeUseCase；CompositionRoot.Stores 拼接多个 Hook；ReplyDelivery 访问 Mood、记回戳；QqActionAuthorization 检查插件；协议端提供 poke 原语 | 很高。戳一戳包拥有互动规则、上下文和冷却，跨心情协作只用狭窄事实接口；QQ 动作和普通回复共用执行前授权 |
| P05 preset.feature.vibes | behavior 氛围开关与 moodTtlSeconds | VibeTracker、Participation、Reply 上下文、PromptBuilder、MoodStore；戳一戳与回复完成共同更新心情 | 很高。氛围包拥有心情/氛围模型、状态与提示片段；对话包保留参与编排，不直接读写 MoodStore |
| P06 preset.feature.profiles | behavior 画像字段；旧 profiles 查询；隐私展示跨 DTO | MemberProfileStore、IProfileRepository、BotScheduler 摘要；PromptBuilder/上下文直接消费；统一记忆已有独立授权链 | 很高。画像与相关记忆实现、授权后的读取/摘要/管理页归包；通用来源证明与隐私规则留基础设施。已有被阻止的旧摘要路径不得因迁移重新开启 |
| P07 preset.feature.websearch | behavior 搜索/链接字段；提示与工具声明仍集中 | WebSearchService、ResearchUseCase、LinkPreviewer 在 Headless；InlineTurnTools、下一轮笔记和冷却在 Engine；Model 注入 SearchText/LinkText | 很高。搜索包拥有 provider、抓取、过滤、摘要、冷却、一次性笔记、工具与 UI；对话包只决定是否继续一个通用工具回合 |
| P08 preset.feature.token-quota | TokenQuotaDialog.vue 是宿主内置组件；ZbaClient 与 ZBA quota 路由单独硬编码 | 策略确实在 TokenQuotaPresetPlugin；Headless/ZbaHostBackend.Quotas 与 TenantQuotaStore 适配；Storage/TenantQuotaStore；模型用量跨链采集 | 高，局部策略已解耦。把页面、管理处理、额度状态和日界线归包；用量采集/审计基础服务不能随额度策略停用而停止 |

## 5. 系统功能的归属

| ID | 现状与耦合 | 目标包/子包 | 不应搬入业务包的通用责任 |
| --- | --- | --- | --- |
| S01 平台接入，很高 | CompositionRoot.Channels 固定构造 OneBot/Official/Feishu/Local；凭据、生命周期、白名单编辑、官方消息诊断和 NapCat 管理分散 | platform-qq-private、platform-qq-official、platform-feishu、platform-local；统一平台管理只聚合它们的描述。Local 必须在测试及开发用途保留 | 认证主体、来源/操作租约机制、统一路由接口；协议特定 openid、msg_seq、API 码和持久投递证明由对应平台包封装 |
| S02 模型服务，很高 | Model 已独立程序集；Headless 仍构造 provider、凭据解析、故障转移，Model 提示词认识各业务；ProviderEditorDialog 在主体 | model-services：模型目录、协议适配、provider 管理、路由/预算/故障转移；业务提示移回相应功能包 | 进程级 HTTP/时钟/密钥代理、调用来源审计；提供模型端口，不返回全局 SettingsBox |
| S03 Agent，很高 | ServerAgentRunner、AgentCommandService、ZbaTaskRegistry/AgentTaskRuntime、TasksPage 与 MCP/Python 租约接入跨工程 | agent：任务状态机、QQ/Web 适配、任务页、结果与操作恢复；mcp-tools 为独立工具来源子包，不能靠跨包 private 引用 | 审批授权规则、幂等/回执存储设施、通用受控进程代理。QQ 与 Web 的能力差异仍显式保留 |
| S04 对话策略，很高 | ReplyPipeline 持有几乎所有业务；BotScheduler 把画像/表情/在线探测混排；模型参数含各插件字段 | conversation：入站到回合的编排、参与/节奏、上下文预算、普通回复规则、会话页面；消费各包贡献而非具体业务类型 | 通用事件分发、资源调度器、提交授权与不可重复副作用保护。基础安全不会因禁用 conversation 而失效 |
| S05 健康报告，高 | HealthReportService 引用 ConversationRegistry/ReplyPipeline/BotScheduler/VoiceUseCase/QQ source；设置热更新定向调用 | health-reporting：日报内容、北京时间调度、目标配置和界面；从通用只读指标快照取信息 | /healthz、进程就绪语义、metrics 收集与基础审计仍属于宿主；缺少任一业务包时报告必须能降级 |

统一记忆、审批、文件/进程工具、备份等不能消失在“其他”里：D0 给每项分配具体所有者。当前推荐：画像语义/记忆管理归 P06，会话存档归 S04，任务业务归 S03，审批执行约束及通用备份协调归基础设施；它们的业务页面分别随所属包。出现不能明确归属的字段或路由，阻止对应切片开工。

## 6. 扩展机制的真实能力与缺口

| 机制 | 可复用 | 当前缺口与迁移约束 |
| --- | --- | --- |
| .NET 包 API 1 | manifest、哈希、依赖、Settings、生命周期、回复策略、可回收加载上下文、代次排空 | 无通用业务贡献/插件 UI/受控媒体入口。不能传整个 IServiceProvider 或宿主实例来补洞；旧 net8.0/net10.0 包继续有兼容测试 |
| Python 包 API 2 | 声明工具、受控 worker、MCP 租约、配置与包生命周期 | 不天然拥有普通 QQ 消息访问权；现有 stdlib-only/进程能力限制保持。新增附件入口要建立来源、精确工具批准和回原会话的契约，不能新起第二个协议连接 |
| 声明式设置 | 5 类字段 string/boolean/number/select/secret；修订号、回执和不回显秘密 | 不是自定义页面执行器。PluginManifestJsonConverter 仅接受当前字段白名单，新 UI/贡献声明必须版本化，不能直接往旧包塞字段 |
| 自定义前端 | 当前 registerPlugin 可加载编译进宿主的 Vue 组件 | 不等于已支持安装 ZIP 内的页面。最终要把自定义页面产物同包交付；只改 import 路径仍需重建主体，不满足最终目标 |

## 7. 关键证据锚点

以下路径相对仓库根，行号对应本页固定提交；实施时重新解析，不对移动后的行号照抄。

| 证据 | 实际源码位置 | 支持的判断 |
| --- | --- | --- |
| E01 | src/BotAgent.Headless/Host/PluginRuntime.cs:41；同文件 PluginServices | 八个固定预设；扩展只获日志，不是完整业务容器 |
| E02 | src/BotAgent.Engine/Plugins/Presets/MusicPresetPlugin.cs:6；同目录另外六个预设 | 元数据与空生命周期 |
| E03 | src/BotAgent.Engine/Plugins/Presets/TokenQuotaPresetPlugin.cs:7 | 真实策略回调和账本注入 |
| E04 | src/web/src/features/capabilities/plugin-settings.ts:3 | 编辑位置集中登记；字段真源未迁移 |
| E05 | src/BotAgent.Headless/Host/CompositionRoot.Model.cs:130 | 宿主创建画像、表情、语音、音乐和搜索依赖 |
| E06 | src/BotAgent.Headless/Host/CompositionRoot.Reply.cs:73 | ReplyPipeline 接收多业务实例，scheduler 再次引用业务 |
| E07 | src/BotAgent.Engine/Reply/ReplyDelivery.cs:71；src/BotAgent.Engine/Reply/ReplyDelivery.Commit.cs:62 | 发送流程依赖业务实例与具体开关 |
| E08 | src/BotAgent.Model/Model/PromptBuilder.cs:31；src/BotAgent.Model/OpenAiClient.cs:146 | 业务知识穿透到模型接口与提示组装 |
| E09 | src/BotAgent.Core/Domain/Tools/ChatToolSpecs.cs:31；src/BotAgent.Engine/Tools/ToolExecutors.cs:68 | 全局工具表、聊天执行仍是 LegacyPath |
| E10 | src/BotAgent.Engine/Plugins/PluginGate.cs:52；src/BotAgent.Engine/Plugins/PluginReplyPolicies.cs:11 | 硬编码功能映射；策略调用已有代次/异常保护 |
| E11 | src/BotAgent.Headless/Adapters/Panel/PanelRoutes.cs:76；src/BotAgent.Headless/Adapters/Panel/PanelRoutes.cs:209 | 旧 API 冻结；业务路由仍在宿主 |
| E12 | src/BotAgent.Headless/Adapters/Panel/Zba/ZbaHostBackend.Quotas.cs:18 | 管理接口跨会话、台账、脱敏、事务审计 |
| E13 | src/BotAgent.Headless/Services/Ops/BotScheduler.cs:24；同文件:78 | 调度持有业务；已有记忆保护不得丢失 |
| E14 | src/BotAgent.Headless/Services/Ops/HealthReportService.cs:31 | 报告直接依赖其他业务具体实现 |
| E15 | src/BotAgent.Headless/Host/CompositionRoot.Channels.cs:129 | 平台构造、协议持久状态、身份判定在装配代码 |
| E16 | src/BotAgent.Core/Domain/Plugins/PluginPackageManifest.cs:5；src/BotAgent.Core/Domain/Plugins/PluginManifestJsonConverter.cs:20 | 包格式与白名单约束；当前没有 UI 字段 |
| E17 | src/BotAgent.Core/Contracts/EnginePorts.cs:7 | ISettingsBox 暴露全量 AppSettings，不能视作插件隔离接口 |
| E18 | src/BotAgent.Storage/Properties/AssemblyInfo.cs:3；src/BotAgent.Headless/BotAgent.Headless.csproj:45 | Storage friend bridge 与当前程序集依赖图 |
| E19 | tests/BotAgent.ArchitectureProbe/Baseline.cs:12 | 固定工程/引用护栏；新拓扑须增补精确规则，不能放宽棘轮 |

## 8. R2 复审：独立包目标与现有机制的冲突

2026-11-14 应用户要求再次对照源码复审。下面是迁移前需要补足的契约与执行步骤；没有运行故障注入，不能当成当前线上故障报告。原有 819 文件扫描和耦合判断保持不变。

| 复审项 | 当前源码依据 | 在迁移目标下的具体问题 | 已补入计划与验收 |
| --- | --- | --- | --- |
| R01 保留 ID 与默认值 | src/BotAgent.Headless/Adapters/Plugins/PluginPackageValidation.cs:101；src/BotAgent.Headless/Adapters/Plugins/PluginPackageReload.cs:41；src/BotAgent.Headless/Host/PluginRuntime.cs:197 | 外部包禁止 preset.*，旧预设注册也禁止替换；去掉预设集合后，无显式记录的默认启用变成关闭 | D1.0b 验来源准入/迁移机制，D1.2 以实际迁移签 A32 全项；保留单一注册、显式意图及断点记录 |
| R02 管理入口仍依赖业务图 | src/BotAgent.Headless/Host/CompositionRoot.cs:144；src/BotAgent.Headless/Host/CompositionRoot.Surface.cs:88；src/BotAgent.Headless/Adapters/Panel/Zba/ZbaRuntime.cs:34 | 管理面在平台/模型/回复之后构建，并接收具体 Agent/会话实例；缺包时不能仅靠移动目录保留修复入口 | 先拆管理启动依赖；无包/坏包仍能鉴权、修复、查旧操作安全状态；D1.0a、A33 |
| R03 依赖就绪与组合切换 | src/BotAgent.Headless/Adapters/Plugins/PluginDependencyPolicy.cs:8；src/BotAgent.Headless/Adapters/Plugins/PluginMarketService.LifecycleChecks.cs:39；src/BotAgent.Headless/Adapters/Plugins/PluginPackageService.Lifecycle.cs:54 | 磁盘 Active 精确版本不代表启用/Ready；逐包应用不提供拓扑保证。C1→P1 升 C2→P2 时两种单包更新顺序均可能被现有依赖规则拒绝 | 拓扑准备/Ready 发布、状态传播、带租约代理、整体预览/采用/恢复；D1.1/D2.2、A34 |
| R04 跨包类型身份 | src/BotAgent.Headless/Adapters/Plugins/PluginPackageLoader.cs:9 | 仅 IBotPlugin 所在程序集共享，两个包自己的同名 contracts DLL 会进入不同加载上下文；旧 SDK 兼容测试不足以证明新跨包接口可调用 | 契约唯一装载所有者、版本/摘要组合与消费者租约；真实独立构建供需包验证；D1.1、A35 |
| R05 新清单字段的批准绑定 | src/BotAgent.Headless/Adapters/Plugins/PluginLifecycleManifest.cs:25；src/BotAgent.Headless/Adapters/Plugins/PluginMarketService.Lifecycle.cs:38；src/web/src/api/plugin-manifest-values.ts:7 | 差异、指纹、前端等价判断存在手写投影；仅增加 parser 字段会漏掉对批准范围的约束 | 字段覆盖表贯穿读写、规范化、预览、批准、状态与回执；单字段变更反例；D1.0b、A36 |
| R06 新磁盘状态与旧宿主 | src/BotAgent.Headless/Adapters/Plugins/PluginPackageStore.cs:38；src/BotAgent.Headless/Adapters/Plugins/PluginPackageService.cs:39；src/BotAgent.Headless/Host/PluginRuntime.cs:132 | Active/Previous/Pending 整体严格解析，全目录读取先于逐包 try；新字段或坏条目可能挡住其他包和回退入口 | 状态信封/执行清单分层、逐包隔离、最低可读宿主、降级前校验及兼容组合恢复；D1.0a、A37 |
| R07 独立版本与发行产物 | Directory.Build.props:38；.github/workflows/ci.yml:66；src/BotAgent.Headless/Adapters/Panel/PanelRoutes.cs:80 | 当前全仓 Version 唯一真源与 CI 拒绝包内 Version 覆盖；Web 独立交付，ZIP 内有 UI 不等于可从页面访问 | A38 分段验收：D1.0b 验版本/构建及精确 CI，D2.1 验运行时归档/挂载/HTTP 资源链；前段通过不替代最终 UI 交付 |
| R08 候选初始化与独占资源 | src/BotAgent.Headless/Adapters/Plugins/PluginPackageLoader.cs:17；src/BotAgent.Headless/Adapters/Plugins/PluginPackageReload.cs:42 | 候选先初始化，旧实例后替换；若直接把协议连接放进 InitializeAsync，准备时就可能与旧协议端同时在线 | 准备无副作用、排空后激活；独占资源默认冷采用，逐平台证明后才开放在线；D2.2/D4、A39 |

这轮补充没有放宽旧准入、依赖、批准或架构护栏。变化是把它们与目标架构的冲突写成明确迁移任务；具体接口签名和兼容版本仍须在 D0/D1 的决策表中冻结。

## 9. 审查限度

本轮只验证源码结构、固定提交、文件散布、现有调用和契约。没有执行构建、产品回归或性能测试，未证明独立安装/卸载已可用。既有 dev CI 成绩属于原候选功能正确性，不替代未来解耦候选的验收。执行计划要求每个包通过独立构建、只改自身、无包运行和可回滚验收后，才允许称为“已解耦”。
