# 变更日志

本文件记录仓库对外可见的变化，格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

- 只维护 **Unreleased** 与**最近两个已发布版本**；更早版本的完整说明在
  [GitHub Releases](https://github.com/ZhaoJun233/bot-agent/releases)。
- 版本号的**唯一真源**是 `Directory.Build.props` 的 `<Version>`（#122）。csproj 里再写 `<Version>`
  会悄悄压过它，ArchitectureProbe 与 CI 都会判红。
- 发版顺序固定：改 `<Version>` → 把 Unreleased 段改成新版本段 → 打 `vX.Y.Z` tag → CI 在 tag 上断言
  tag 与 `<Version>` 一致 → 发布。详见 `docs/operations/release-checklist.md`。

## [Unreleased]

当前 `<Version>` 为 **1.3.0**（开发中，尚未打 tag）。

### 新增

- 审批与执行隔离加固（#117）：面板决策拿不到原始平台源租约时**失败关闭**（`source_unavailable`，
  以前会 `Enter(null)` 后照批），群命令与面板两条路在**执行前**再核一次租约（决策≠执行）；
  `approval.decided` 事件的 `state` 如实区分 `approved`/`rejected`/`expired`/`stale_policy`/
  `not_an_approver`/`unknown_request`/`source_retired`/`source_unavailable`（以前一律写 `rejected`）；
  补「已批准但过了有效期再消费同样失败」用例。**已知缺口**：审批尚未接到真实高风险工具
  （`//` 的 bash/read/write/docker 仍走 `ToolPolicy`），因为那需要「执行器挂起 + 批准后恢复同一轮」，
  单独立条目跟进。口径见 `docs/architecture/approval-and-isolation.md`。
- 插件市场与自定义模型提供商端到端落地：宿主侧注册表 + 面板安装/启停/凭据录入。
- 自然对话驱动 agent（`//` 指令保留），含停止/进度归一匹配与日志脱敏。
- Web 控制台：能力管理收敛、任务提交与审批事件恢复、插件中心、Token 配额插件与主题、
  移动端适配与焦点/对比度修复。
- 平台策略结构化编辑器（#104）：在面板里直接编辑策略条目的目标、开关、名单与功能覆盖，
  只提交改过的行。
- 发布与版本治理（#122）：`/healthz` 暴露 `version`、`--version` 与启动横幅都读程序集、
  `CHANGELOG.md`、dependabot、基镜像钉 digest、CI 产出依赖快照。
- 文档：接口面地图 `docs/api-surface.md`（37 条 ZBA + 50 条旧 `/api/*`）、
  `CONTRIBUTING`/`SECURITY` 中英双版、基线报告与回滚预案、.NET 10 升级验收报告。
- 核心不变量回归（#126）：S56 每会话 FIFO / S57 静默短路在发送前 / S58 引用只挂第一条 /
  S59 语音与文字不重复播报，四个场景进 CI 矩阵；`PipelineEval` 新增确定性决策链性能基线
  （`tests/fixtures/pipeline/baseline.json`，口径见 `docs/baseline/pipeline-performance-baseline.md`）。
- 任务台账与重启恢复（#116）：schema 11 → 12 新增 `agent_tasks` 表（只存形状与计数，不落提示词与正文），
  运行时在受理/变更/终态处写台账，启动时读最近 200 行但**绝不重做**：`Queued`/`WaitingApproval`
  收口为 `Cancelled(cancelled_before_dispatch)`，`Running`/`CancelRequested` 收口为
  `Unknown(restart_state_unknown)`；ZBA `GET /tasks/{id}` 重启后可查（以前是 404），
  合成场景 S65 覆盖四条不变量。决策见 `docs/decisions/0003-agent-task-persistence.md`，
  实现事实见 `docs/architecture/agent-task-runtime.md`。
- 统一工具注册表（#115）：`ToolSpec` 增加开关名与来源元数据（10 条聊天声明不再各写一份 id→开关表），
  可见性判定收敛到 `ToolVisibility` 单入口（`ReplyToolAvailability` 降为兼容壳）；
  新增 `ToolInventory` 统一查询面，MCP 作为**追加来源**进同一张表而不改内建 26 条计数，
  面板 `GET /api/tools` 新增 `inventory` 块（内建 / MCP / 重名分列）。口径见
  `docs/architecture/tool-registry.md`。
- 运维基线（#121）：日志按天轮转并保留 7 天（单文件 10 MiB 或跨天滚动、旧档 gzip，
  规则抽成 `LogRotation` 并有 10 条探针用例）；库与热备产物一律 `600`；宿主启动 10 分钟后自动每日热备
  （保留 3 份、同一天不重复、同一时刻只跑一个，结果写 `meta` 表），面板新增 `GET/POST /api/backup`；
  `/status` 与 ZBA status 暴露库/日志/最近备份/内存与 swap/启动次数，总览页对「备份过期或失败」显示红条；
  compose 给 `qqchat`（768m/512）与 `napcat`（512m）加上限，并修掉重复 `volumes:` 键导致的
  `docker compose config` 解析失败（特权挂载见 #131）。恢复演练落成集成场景 S60
  （备份能独立打开、`user_version` 一致、放回去能起来）。

- 配置与权限回归测试体系（#111）：新增 S63 覆盖白名单**运行时热生效**（面板改名单不重启即生效、
  `*` 全放行、重复/空白写法不重复处理、清空回落旧单名单、群聊与私聊名单互不影响）并进 CI 矩阵；
  `whitelist policy semantics` 钉住解析口径 14 条（空=全拦、通配、分隔符、去重、非法 token）；
  能力设置新增两条 Playwright 端到端（模型字段、群聊白名单保存并回读）。覆盖矩阵见
  [配置与权限回归测试矩阵](docs/testing/configuration-permission-regression.md)。

- 协议 / 工具 / 插件回归矩阵（#112）：新增 S64 钉住 OneBot 动作响应 `message_id` 的
  数字与字符串两种写法；矩阵 `docs/testing/protocol-tool-plugin-regression.md` 把协议收发、
  排队并发、Agent 工具、插件生命周期、异常恢复逐条对到现有场景与探针。

- 任务与执行状态（#114）：新增 `AgentTaskState` 九状态与显式转移表（Core）、`AgentTaskRuntime`（Engine：并发上限、取消、硬超时、终态不可覆盖、变更事件、正文清洗与截断），ZBA `/tasks` 转为通道适配器（幂等键、回执、事件流、结果投影仍归通道）；取消与超时是两条路径，超时对外投影为 `Failed` + `errorCode=timeout`；聊天轮、`//` 与面板直跑不迁入。设计见 [ADR-0002](docs/decisions/0002-agent-task-runtime.md)，实现说明见 [任务与执行状态](docs/architecture/agent-task-runtime.md)。
### 变更

- 装配根拆分（#110）：`src/BotAgent.Headless/Host/CompositionRoot.cs`（1223 行）按能力域拆成 8 个 `partial`
  文件（core / channels / model / stores / tools / reply / surface + 装配容器），`Build` 从一段近 200 行的线性代码
  变成 7 个显式阶段（`AssembleCore` → `…Channels` → `…Model` → `…Stores` → `…Tools` → `…Reply` → `…Surface`）
  加一个按依赖顺序判空的 `Assembly` 容器；组件构造签名、装配顺序与失败关闭语义全部不变。
  决策、阶段顺序契约与文件地图见 [ADR 0001](docs/decisions/0001-composition-root-assembly-split.md)。
- **升级到 .NET 10**（#102）：30 个工程 `TargetFramework=net10.0`，`global.json` 钉 SDK 10.0.101，
  插件 TFM 白名单同步放宽。
- 构建基线收敛到 `Directory.Build.props`（#123）：LangVersion / Nullable / ImplicitUsings /
  Deterministic / NuGet 审计，并由探针强制「只接受探针解释得了的属性」。
- 面板设置边界（#109）：`WebUiServer.Settings.cs`（1127 行）按职责拆成 8 个文件——HTTP 薄层、
  新保存请求校验、旧面板整批意图、标量输入、字段读取原语、结构化策略装配、已移除字段拒绝、回读投影；
  旧面板与 ZBA 两条保存路径、整批拒绝原子性、结构化策略独立装配与 secret 归属保持不变。
  文件地图与「加一个字段要改哪两处」见 [面板设置职责边界](docs/architecture/panel-settings-boundaries.md)。
- 模型配置（#103）：凭据来源改为**请求级**材料，面板里改 API Key 或换供应商后不再进入安全静默，
  下一次请求即打到新端点。
- 模型供应商候选表（#128）：不再只在启动时抄一次快照——保存配置时整体热替换（登记/删除供应商、
  调优先级、启停都立即生效），断路器按供应商 id 复用、不会因改配置被放出来；供应商级字段不再报
  `RestartRequired`。判定规则与不变式见
  [模型配置的生效路径](docs/architecture/model-configuration-apply-path.md)，回归场景 `S61`。
- 模型配置链路的其余缺口（#132）：运行期路由与面板「发现模型」共用同一条硬边界
  （`ProviderRouteBoundary`，拒绝原因码与日志一致，只对未启用供应商放行试连）；
  旧面板 `POST /api/settings` 支持可选的 `expectedVersion`（携带时做 CAS，冲突 409，响应多一个
  `legacyVerification` 标记）；`//` 辅助模型通道缺密钥时不再静默返回，日志与聊天通道同口径。
- `//` 任务的两条模型通道（#134）：专用上游只给一半覆盖（只改地址或只改密钥）时不再绕开熔断器——
  现在包成单候选辅助上游，用与聊天相同的 `ProviderFailoverRunner` 记账，坏端点按熔断快速失败、
  错误码与日志同口径；`baseUrl` + `apiKey` 成对给出仍是唯一允许完全绕开的路径（测试替身 / 特殊网关）。
  合成身份 `agent-server` 的熔断状态不落库（重启清零），取舍见
  [模型配置的生效路径](docs/architecture/model-configuration-apply-path.md) §4；回归 `S37` 与 ProtocolProbe B35。
- CI 质量门禁（#113）：9 项必需检查与实际 job 名逐字对齐、写明「失败与跳过」纪律并登记已知不稳定性；
  集成矩阵补上此前漏跑的 S61/S62，ArchitectureProbe 新增断言「harness 注册的场景 == ci.yml 矩阵」
  （双向、负向实测会红）；CI 改用规范名 `BOTAGENT_IT_ONLY`。清单见
  [CI 质量门禁](docs/operations/ci-quality-gates.md)。
- 平台策略（#104）：面板改的标量（开关、白名单文本、本地通道）会同步进策略条目，
  旧面板与新 ZBA 面板两条保存路径都生效。
- 平台策略的实例目标与名单文本（#129）：空 instanceId 的实例目标就是内置驱动的身份
  （qq.private / qq.official / local 的驱动标识本来就是空串），单驱动平台照常按它结算，
  不再因为「存在实例目标」把整条通道关死；启动横幅与闸门摘要会列出实例目标条数与 id。
  策略条目的名单文本在写库前归一（去空项、去重、统一逗号分隔），回读与提交一致；
  读侧把名单的三种状态分开显示：跟随基线 / 显式名单 / 全收，显式空串标成「全拦名单外所有」。
  闸门快照按配置版本惰性重建：绕过设置热更新直接发布配置时，横幅与判定也不会再停留在旧名单。
  判定顺序、三种空状态与改动落点见 [平台策略的判定路径](docs/architecture/platform-policy-resolution.md)。
- 回复主链按职责解耦（#106）：`ReplyPipeline`（1659 行）拆成**入站闸门 / 媒体入站 / 轮次上下文 /
  决策动作 / 投递 / 引用裁决**六个独立类，编排仍留在一个文件里；旧的 `ReplyPipeline.Delivery.cs`
  partial 已删除，架构探针断言这些职责类各自独立成文件。行为等价由 58 个集成场景兜底。
- 飞书通道的群名单与私聊名单拆开（#133）：新增标量 `FeishuWhitelistPrivates`（配置字段 147、
  环境变量 `QQCHAT_FEISHU_WHITELIST_PRIVATES`、旧面板 `feishuWhitelistPrivates`），群与私聊各判各的；
  **私聊留空 = 跟随群名单**，旧部署升级后行为不变，要「私聊全拦」仍用策略条目的显式空串。
  字段号超过 `configuration_field_modes` 原有的 142 上限，库 `user_version` 10 → 11
  （旧库整表重建、新库直接按新约束建），契约 Revision 不变。口径见
  [平台策略的判定路径](docs/architecture/platform-policy-resolution.md) §7。
- 旧内置前端随 S6 删除，面板只剩 `src/web` 的 Vue 工程；宿主 `/` 不再提供页面。
- OneBot 网关职责拆分（#108）：报文解析、入站事件、动作通道、发送动作与只读查询独立成内部组件；
  门面保留连接生命周期与公开接口，协议、超时、取消及普通消息到达顺序保持不变。
  职责对照与风险见 [边界说明](docs/architecture/onebot-gateway-boundaries.md)。
- 模型输入缓存（#98）：系统提示改成「稳定前缀 + 动态尾巴」两段拼装 —— `[现在的时间]` 与逐轮
  变化的发言意愿值移到末尾，写作规则 / 人设 / 动作契约 / 工具清单因此跨轮逐字可复用；
  token 用量把「已缓存输入」与「缓存写入」补成**三态**（`null` = 上游没提供、`0` = 明确没命中），
  `/metrics` 新增 `botagent_prompt_cached_tokens_total`、`botagent_prompt_cache_write_tokens_total`
  与 `botagent_prompt_cache_unknown_total`（未知轮数单独报，绝不把未知当 0）。历史时间戳渲染、
  `(#id)` 窗口与滑窗仍会让历史前缀失效，清楚写在
  [模型输入缓存](docs/architecture/prompt-cache.md) 的「还会破坏前缀的四件事」里。

### 修复

- 普通对话缺少时效感知（#97）：提示词现在明确「本机时钟是唯一时间基准」（时区偏移一起给出，用户报的时间不算
  系统时间），并声明**知识截止时间未知、不写死日期** —— 比截止更新的事不再被否认成「没有这回事/是假的」，
  用户说的新消息只算未确认说法；联网时要求分清「事件发生时间」与「检索时间」、查不到就如实说而不编结论。
  关闭联网时这套边界同样在。口径与守护测试见 [时效感知与不确定性](docs/architecture/temporal-awareness.md)。
- 改供应商集合后只剩「重启」一条路：回执报 `RestartRequired` 但没有任何重建入口，运行期又拿
  `desired` 跟启动快照比、不等就拒绝路由，于是机器人静默不回复（#128，候选表改为热替换后消失）。
- 集成 harness 的两类偶发失败：S12 写死 `18085` 被别的进程占成临时端口导致面板没起来（#105）、
  S26 假模型服务端口被占导致整轮 harness 崩溃（#104 S2 期间）。
- B24 探针的子进程清理改成有界等待，修掉 Linux CI 上的偶发假红（#105）。
- 「允许 Docker」与「面板一键部署」以前假装可用：compose 的 `qqchat` 块里有两个 `volumes:` 键，
  严格解析器整份文件报错、宽容解析器只取后者，所以 docker socket 从来没挂上（#131）。现在容器里
  没有 docker 时（默认如此）`//` 的工具表与提示词都不出现 docker，`GET /api/agent/status` 会额外
  回读 `serverDockerAvailable`，启动日志与 `SECURITY.md` 写明要部署方显式挂载 socket 才能启用。
- 文档死链归零；README 配置参考重写成「compose 层 / 宿主层 / 面板与库」三层模型（#125），
  删掉不存在的 `/playground.html` 与「宿主自动生成证书」两处过期声明。
- 面板「发现模型」能测通的供应商，线上调用却被拒绝，日志里看不出两条路差在哪（#132）；
- 旧面板保存没有版本校验，两个标签页并发保存会静默互相覆盖（#132，带 `expectedVersion` 时返回 409）；
- `//` 任务在辅助模型通道缺密钥时直接返回空、一行日志都没有，表现为「任务哑了、日志干净」（#132）；
- 一条空 instanceId 的策略条目能把整条平台通道锁死，而面板既看不出原因也改不掉（#129）；
  官方通道的「空名单」在标量基线与策略条目里含义相反，读侧以前都显示成空（#129）；
  名单文本里的重复项与多余分隔符原样落库，回读与提交对不上（#129）。
- 飞书通道的群名单与私聊名单共用一个字段，想只放行群、不放行私聊时只能把私聊塞进同一串（#133）。

### 安全

- 新增 `SECURITY.md` / `SECURITY.en.md`：写明插件无沙箱、SHA256 只证字节完整性、
  没有 ZBA 令牌时管理端点失败关闭（仅 `/zba/v1/health` 可读）。
- NuGet 审计 `high`/`critical`（NU1903/NU1904）作为构建错误，作用于全部工程。

## [1.2.0] - 2026-10-03

插件注册表契约与微内核插件架构（#70）、主模型与快速档独立的思考预算与滑块（#72/#74）、
控制台免密快速启动、Linux 原生常驻进程管理、控制台视觉与移动端适配。

完整说明见 [v1.2.0 Release](https://github.com/ZhaoJun233/bot-agent/releases/tag/v1.2.0)。

## [1.1.1] - 2026-10-02

动作描写语义过滤修复、公式保护、运维文档重构。

完整说明见 [v1.1.1 Release](https://github.com/ZhaoJun233/bot-agent/releases/tag/v1.1.1)。

[Unreleased]: https://github.com/ZhaoJun233/bot-agent/compare/v1.2.0...HEAD
