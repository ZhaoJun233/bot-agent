# 模块化单体总规划与 Agent 执行手册：第二轮评审

评审日期：2026年10月7日<br>
评审对象：总规划与 Agent 00–07 八份执行手册<br>
评审范围：现状真实性、接口与数据流、执行可行性、隐私与权限、验收及回退<br>
结论：**保留总体方向，修正高严重度问题后再推进施工与阶段验收。当前不具备“全部已验证”或“可直接执行”的证据。**

此前给出的 `100/100`、`98/100` 与 `APPROVED FOR EXECUTION` 缺乏充分验证，应撤回。本报告评分为 **6/10（规划质量的定性判断，不是代码质量或生产可靠性测量）**。优点是 Why/What/How、波次和后端先行方向清晰；扣分集中在现状标记失真、ZBA 交付缺口、保存与协议契约不完整，以及不可直接执行的回退规程。

## 一、证据范围与本次验证结果

完整读取了八份手册，并核对总规划、项目引用、关键源接口和测试实现。只使用源码与合成输入，没有读取生产会话、成员档案、真实协议报文或线上日志。

| 本次实际执行 | 结果 | 能证明的范围 |
| --- | --- | --- |
| `dotnet build BotAgent.slnx -c Release --nologo` | 0 警告、0 错误；本次耗时 18.03 秒，含还原 | 当前解决方案能够构建；不能证明所有模块被运行宿主采用 |
| `dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release` | **578 通过、16 失败；退出码 1** | 当前安全和策略探针存在失败，不满足文档全绿门禁 |
| `dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release` | **86 通过、6 失败；退出码 1** | 当前架构护栏未全绿，且扫描范围尚未覆盖拆出的所有工程 |
| 重跑已构建的 SafetyProbe DLL，仅提取失败项及统计 | 再次 578 通过、16 失败 | 失败在本次合成探针中可重复出现 |
| S21/S36/S52、完整集成矩阵、生产内存与延迟基准 | 本次未运行 | 历史成绩不能作为本次验收结果 |

安全探针失败集中在显式空动作名单、平台实例白名单覆盖、旧开关向统一策略迁移、启用与白名单就绪分离，以及本地通道名单行为。失败说明当前实现和既有断言不一致；尚未定位到每个失败的根因，不能据此断言生产环境已经发生越权。

下表记录本次 **16 项安全失败断言**；名称按断言含义整理，长编号等合成样例不重复抄入报告。

| # | 失败断言 | 源码位置 |
| --- | --- | --- |
| 1 | 显式空动作名单保持 fail-closed，实际 configured=False | [PlatformPolicyTests:49](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L49>) |
| 2 | QQ 官方实例策略白名单优先覆盖 | [PlatformPolicyTests:115](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L115>) |
| 3 | 本地通道实例策略白名单优先覆盖 | [PlatformPolicyTests:117](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L117>) |
| 4 | switch migration preserves the old disabled intersection | [PlatformPolicyTests:151](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L151>) |
| 5 | canonical policy switches override stale legacy switches | [PlatformPolicyTests:158](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L158>) |
| 6 | migration is idempotent and mirrors the canonical switches | [PlatformPolicyTests:161](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L161>) |
| 7 | chat mute takes effect immediately without disabling the platform | [PlatformPolicyTests:165](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L165>) |
| 8 | platform on restores the configured chat switch | [PlatformPolicyTests:173](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L173>) |
| 9 | local enablement is separate from whitelist readiness | [PlatformPolicyTests:197](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L197>) |
| 10 | legacy-only configurations retain fallback without new action policies | [PlatformPolicyTests:201](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L201>) |
| 11 | synthesized standard rows inherit legacy whitelists to prevent roundtrip clearing | [PlatformPolicyTests:215](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L215>) |
| 12 | explicit empty action allowlist remains configured and fail closed | [PlatformPolicyTests:233](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L233>) |
| 13 | nonempty explicit actions are always configured even with inherited switch metadata | [PlatformPolicyTests:236](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L236>) |
| 14 | nonstandard accounts project resolved null fallback without unmuting | [PlatformPolicyTests:280](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs#L280>) |
| 15 | 名单中的本地短 id 换算后应接收 | [SafetyProbe Program:567](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/Program.cs#L567>) |
| 16 | 超范围配置项丢弃，避免撞入官方号段 | [SafetyProbe Program:578](<E:/bot/bot-agent/tests/BotAgent.SafetyProbe/Program.cs#L578>) |


架构探针的六项失败都表现为旧扫描根下找不到类型或锚点：工具声明、可用工具提示词、轨迹类型、本地号段、工具策略及门禁。当前 [SourceIndex](<E:/bot/bot-agent/tests/BotAgent.ArchitectureProbe/SourceIndex.cs#L115>) 只解析 Headless 源码根；Core 等模块迁出后，零命中不能证明它们没有 IO 或权限问题。这是已确认的覆盖缺口，不能通过删除失败断言来“修复”。

历史的 SafetyProbe `594/0`、ArchitectureProbe `92/0`、S21 `37`、S52 `119` 只保留为历史记录。新增模块、宿主引用和路径迁移必须获得新的运行证据。

## 二、按优先级排序的问题

### R01 · High：当前验收失败，文档仍声明全绿并解锁后续波次

**位置：** [总调度:82–85、99](<E:/bot/bot-agent/docs/plans/agents/00-orchestration-and-dependency-graph.md#L82>)、[Agent 01:59–62](<E:/bot/bot-agent/docs/plans/agents/01-agent-core-and-unified-storage.md#L59>)、[总规划:267–273、362–368](<E:/bot/bot-agent/docs/plans/modular-monolith-refactoring-plan.md#L267>)。

**问题与影响：** 本次安全探针 16 项失败、架构探针 6 项失败，直接反驳当前“594 项全绿”和全面完成的表述。以这些表述解锁新波次，会把未完成的策略迁移与护栏覆盖问题交给下游。

**修正：** 更新为“程序集抽离已发生，宿主集成和现状验收待完成”；新增证据表记录目标仓库、版本或工作区状态、命令、时间、通过/失败和覆盖范围。优先调查安全失败；架构护栏扫描新模块并保留原断言语义。修复并重新验证后才恢复完成状态。

### R02 · High：把拟建接口和独立程序集误写成已完成的宿主集成

**位置：** [Agent 01:7、30–46、53–57](<E:/bot/bot-agent/docs/plans/agents/01-agent-core-and-unified-storage.md#L30>)、[Agent 02](<E:/bot/bot-agent/docs/plans/agents/02-agent-platform-drivers.md>)、[Agent 03](<E:/bot/bot-agent/docs/plans/agents/03-agent-model-and-llms.md>)。

**源码证据：** [Headless 项目:54–57](<E:/bot/bot-agent/src/BotAgent.Headless/BotAgent.Headless.csproj#L54>) 直接引用 Core、Platforms，没有直接引用 Storage、Model；宿主仍保留相应旧实现。源码搜索未找到规划声明的 `IUnifiedConfigurationStore`、`IPlatformDriver` 或 `IDriverRegistry`。已有真实契约是 [IPlatformAdapter](<E:/bot/bot-agent/src/BotAgent.Core/Domain/Ports/IPlatformAdapter.cs>)、[IBotPlugin](<E:/bot/bot-agent/src/BotAgent.Core/Domain/Plugins/IBotPlugin.cs>)、[IPluginRegistry](<E:/bot/bot-agent/src/BotAgent.Core/Domain/Plugins/IPluginRegistry.cs>) 和 [ISettingsRepository](<E:/bot/bot-agent/src/BotAgent.Headless/Services/Ports/ISettingsRepository.cs>)。

**问题与影响：** 单独构建 Storage/Model 不证明生产路径调用它们。规划中的新名字不能作为可依赖的既有 API。没有直接项目引用也不单独证明程序集绝不可能被间接使用，应结合组装与调用路径验证。

**修正：** 分开记录“文件抽离”“独立构建”“宿主接入”“旧实现清理”“回归验收”。优先复用已有端口；确需新增或改名时列出兼容策略、适配层、组装根修改和替身测试。模型预算解析器本身存在，但没有找到其实际调用点，不能把新解析器当作已接线证明。

### R03 · High：后端先行门禁没有包含 ZBA 的负责人和交付

**位置：** [总调度:79–88](<E:/bot/bot-agent/docs/plans/agents/00-orchestration-and-dependency-graph.md#L79>)、[总规划:317–328](<E:/bot/bot-agent/docs/plans/modular-monolith-refactoring-plan.md#L317>)、[Agent 06:56–62、84–88](<E:/bot/bot-agent/docs/plans/agents/06-agent-headless-cli-and-deploy.md#L56>)、[Agent 07:22、32、68–70](<E:/bot/bot-agent/docs/plans/agents/07-agent-modern-frontend.md#L22>)。

**问题与影响：** Wave 5 交付 CLI 和宿主纯化后就释放 Web，Wave 6 却假定统一保存及 REST/SSE 已准备好。没有负责 ZBA 的 Agent，也没有逐插件、逐驱动、统一保存、认证和任务流的验收。当前已有旧面板接口，问题是目标版 ZBA 契约没有交付闭环，而非项目完全没有 HTTP API。

**修正：** 将 ZBA 纳入 Wave 5，明确负责人、API 适配层写权限、复用旧路由的迁移表、OpenAPI、DTO、认证授权和契约测试。建议拟建 `BotAgent.Panel` 为后端 API 程序集，由 Headless 承载；它与拟建 `src/web/` 静态前端分开。Web 准入要求首版 ZBA 全部验收，而不是只检查 CLI 可以启动。

### R04 · High：过渡配置流程与真实配置真源冲突

**位置：** [总规划:59–74、147](<E:/bot/bot-agent/docs/plans/modular-monolith-refactoring-plan.md#L59>)、[Agent 06:20、34](<E:/bot/bot-agent/docs/plans/agents/06-agent-headless-cli-and-deploy.md#L20>)。

**源码证据：** [SettingsStore:8–24](<E:/bot/bot-agent/src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs#L8>) 明确 SQLite 是配置真源，旧 JSON 由导入器迁移并归档，环境变量在未存过配置时作种子；[SettingsBox:46](<E:/bot/bot-agent/src/BotAgent.Headless/Services/SettingsBox.cs#L46>) 已有先持久化再发布的入口。

**问题与影响：** 按“修改 JSON 或环境变量后重启”操作，不保证覆盖已有数据库配置。SQLite 与文件镜像同时承担真源又没有冲突规则，会造成保存成功但运行值不同。重新运行与重新编译被混用，也使运维流程失真。

**修正：** 明确数据库为权威配置；导入、导出、备份和环境秘密分别定义。若仍要求过渡期人工编辑文件，必须先实现并验收带版本的显式导入流程，再让运行宿主消费结果。复用持久化后发布语义，按字段返回热生效或待重启，不承诺所有设置即时生效。

### R05 · High：MCP 传输分类和生命周期缺少现代协议契约

**位置：** [Agent 05:20、29–45、83–87](<E:/bot/bot-agent/docs/plans/agents/05-agent-mcp-ecosystem.md#L20>)、[总规划:185–189、299–308](<E:/bot/bot-agent/docs/plans/modular-monolith-refactoring-plan.md#L185>)。

**问题与影响：** `Sse 或 Docker` 混合了协议传输与进程启动方式。Streamable HTTP 可以返回 SSE，不能因为用了 SSE 就等同于旧 HTTP+SSE；Docker 本身也不是 MCP 传输协议。只实现 initialize 与 tools/list 不足以完成客户端生命周期。

**修正：** 区分 Streamable HTTP、可选 legacy HTTP+SSE、受控 stdio，以及 Docker 启动器等承载方式。定义协议版本、会话、初始化完成通知、认证、工具变化、消息大小、断线恢复、取消和清理；握手、工具发现、调用分别有验收场景。重连不自动重试可能产生副作用的工具调用。

**协议依据：** [MCP 2025-11-25 Transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)。该规范明确 HTTP 断连不应被解释为客户端取消请求。

### R06 · High：把 Docker Socket 描述成天然隔离与轻量执行通道

**位置：** [Agent 05:18–20、30](<E:/bot/bot-agent/docs/plans/agents/05-agent-mcp-ecosystem.md#L18>)、[总规划:234–236](<E:/bot/bot-agent/docs/plans/modular-monolith-refactoring-plan.md#L234>)。

**问题与影响：** 访问默认 rootful Docker daemon 的 socket 可授予宿主级高权限，不能作为沙箱保证。容器中的 Node/Python 进程仍消耗同一台主机的内存，不能用“机器人容器没有该运行时”推导总体资源安全。当前镜像及生产资源未重新测量，不能断言裸跑某运行时必然 OOM。

**修正：** 远程 MCP 作为低本机资源开销的候选方案；本地 Docker 承载作为需单独验收的高权限可选能力。明确可信镜像、挂载和网络限制、资源预算及清理策略，禁止把任意 Docker 控制暴露成普通工具。隔离能力必须根据实际运行配置评估。

**官方依据：** [Docker daemon 远程访问风险](https://docs.docker.com/engine/daemon/remote-access.md)、[Docker socket 访问保护](https://docs.docker.com/engine/security/protect-access.md)。本报告没有验证生产 daemon 的具体权限配置。

### R07 · High：取消令牌被当作硬终止保证，且用户取消被误报为超时

**位置：** [Agent 05:50–65](<E:/bot/bot-agent/docs/plans/agents/05-agent-mcp-ecosystem.md#L50>)、[Agent 04:31、90](<E:/bot/bot-agent/docs/plans/agents/04-agent-engine-and-gating.md#L31>)。

**问题与影响：** `CancelAfter` 依赖被调方响应取消，不保证远端副作用停止。统一捕获 `OperationCanceledException` 返回 `mcp_timeout` 会吞掉调用方取消；“超时”也不能单独证明动作没有完成。现有 [AgentTurnLoop](<E:/bot/bot-agent/src/BotAgent.Headless/Services/Reply/AgentTurnLoop.cs>) 没有 CancellationToken 参数，迁移不能自动获得端到端取消。

**修正：** 传递任务级取消令牌，分别定义调用方取消、客户端超时、服务器错误与结果未知；HTTP/MCP 取消按协议发送并跟踪收口。副作用操作定义幂等或状态核验方式，不盲目重试。预算配置与停机预算分开验证，8 秒作为候选值而非未经验证的硬终止事实。

### R08 · High：示例调用了不存在的 Sanitize，并混淆审计与脱敏

**位置：** [Agent 05:58–60、86](<E:/bot/bot-agent/docs/plans/agents/05-agent-mcp-ecosystem.md#L58>)。

**源码证据：** [ReplyAuditRules:52](<E:/bot/bot-agent/src/BotAgent.Core/Domain/Rendering/ReplyAuditRules.cs#L52>) 导出 `Judge` 与 `Code`，没有 `Sanitize`。

**问题与影响：** 示例无法按既有接口编译。回复审计判断能否发送，与显示层脱敏不是同一种行为；单一字符串清洗不能覆盖工具返回的其它内容形态。外部工具输出还应作为不可信数据处理。

**修正：** 保留发送审计，在明确的数据边界复用 AgentMask 等显示脱敏方式；定义结构化工具结果及允许展示的字段。若新建工具结果处理端口，先记录真实接口、允许内容类型、截断和拒绝规则，再写使用示例，并用合成敏感字段验证。

### R09 · High：前端计划默认公开隐藏推理与原始工具载荷

**位置：** [总规划:146、338](<E:/bot/bot-agent/docs/plans/modular-monolith-refactoring-plan.md#L146>)、[Agent 07:35–39、60、68](<E:/bot/bot-agent/docs/plans/agents/07-agent-modern-frontend.md#L35>)。

**问题与影响：** “思维链折叠”“展开原始返回”可能公开隐藏推理、凭据、群聊内容或文件内容，与项目隐私红线冲突。折叠显示不构成访问控制。

**修正：** 改成动作摘要、工具状态、审批状态、耗时、计数与已审计的结果摘要。ZBA 使用安全 DTO；不把原始载荷先发给浏览器再隐藏。诊断数据独立授权、限量并脱敏；回放、断线恢复和错误消息遵循相同规则。

### R10 · High：回退 SOP 会丢弃当前工作区修改

**位置：** [总调度:103–107](<E:/bot/bot-agent/docs/plans/agents/00-orchestration-and-dependency-graph.md#L103>)。

**问题与影响：** `git checkout -f` 不只针对“本次波次”，可能清除用户或其它 Agent 的未提交修改。当前工作区已有多项更改，不能把它当作干净基线。

**修正：** 使用独立 worktree 或清晰提交边界；保存任务前后的变更清单。已提交变更使用限定提交的 revert；未提交变更按本次补丁和具体文件审阅回退，任何无法区分归属的修改保持原样。产物回退与配置、数据迁移回退分别设计。

### R11 · High：统一保存只有分区方法，没有全局事务、版本与秘密语义

**位置：** [Agent 01:36–45](<E:/bot/bot-agent/docs/plans/agents/01-agent-core-and-unified-storage.md#L36>)、[Agent 07:45、70](<E:/bot/bot-agent/docs/plans/agents/07-agent-modern-frontend.md#L45>)、[Agent 05:44](<E:/bot/bot-agent/docs/plans/agents/05-agent-mcp-ecosystem.md#L44>)。

**问题与影响：** 多次调用 SaveSettingsSlice 不能自然形成“全局原子提交”。`object options` 加自由 sectionKey 缺少允许分区与字段校验；没有版本冲突和缺省/清空语义，可能覆盖并发更新。AuthToken 进入普通描述对象而没有存储、序列化边界，容易被列表或日志回显。

**修正：** 定义一次提交多个修改的命令、基准版本、结果版本、事务与审计一致性；允许分区映射到类型明确的验证器，未登记分区拒绝。定义凭据保留、更换、删除和无权限读取。使用已有秘密存储；QQ 官方 Secret 保持环境变量来源，面板不得接收或回显。持久化成功后发布，运行时应用失败明确报告而非伪造回滚。

### R12 · Medium：后置 Evaluator 的 Fire & Forget 缺少队列和停机语义

**位置：** [Agent 04:21、32、90、112](<E:/bot/bot-agent/docs/plans/agents/04-agent-engine-and-gating.md#L21>)。

**问题与影响：** 后台 Task 没有容量、并发、顺序、失败处理和停机预算，会积压内存或让异常失去观察；消息更新与画像归档可能乱序。异步化本身不证明用户延迟降至 1–2 秒。

**修正：** 复用现有后台执行基础设施或设计有界队列；明确按会话顺序、全局并发、降载、重试与取消规则，以及宿主关闭时的收口策略。验收积压、异常与关闭场景，再测量用户响应及后台完成时间。

### R13 · Medium：工程图、插件归属与实际工具迁移边界不一致

**位置：** [总规划:169–201](<E:/bot/bot-agent/docs/plans/modular-monolith-refactoring-plan.md#L169>)、[Agent 04:28–34、75–83](<E:/bot/bot-agent/docs/plans/agents/04-agent-engine-and-gating.md#L28>)、[Agent 05:85](<E:/bot/bot-agent/docs/plans/agents/05-agent-mcp-ecosystem.md#L85>)。

**问题与影响：** 图中有独立 `BotAgent.Plugins`，手册则把插件写入 Engine；真实 [ToolDirectory](<E:/bot/bot-agent/src/BotAgent.Headless/Services/Tools/ToolDirectory.cs>) 仍在 Headless，而 Mcp 计划直接注入它，缺少端口和目录更新的归属。开发者不能据此确定依赖方向与哪个实现是权威。

**修正：** 确定预设插件位于 Engine 还是独立程序集，只保留一份目标图。将 ToolSpec、工具目录抽象、执行注册与 MCP 投影的依赖写清；组装根接入，底层不得引用 Headless。迁移后全仓扫描重复实现与实际调用点，不能靠新增同名类完成抽离。

### R14 · Medium：多份手册对共享文件声明独占写权限，缺少交接机制

**位置：** [总调度:79–95](<E:/bot/bot-agent/docs/plans/agents/00-orchestration-and-dependency-graph.md#L79>)、[Agent 04:81–82](<E:/bot/bot-agent/docs/plans/agents/04-agent-engine-and-gating.md#L81>)、[Agent 05:79–80](<E:/bot/bot-agent/docs/plans/agents/05-agent-mcp-ecosystem.md#L79>)、[Agent 06:49、54](<E:/bot/bot-agent/docs/plans/agents/06-agent-headless-cli-and-deploy.md#L49>)。

**问题与影响：** 多名 Agent 均修改解决方案和宿主项目文件。严格串行时不必然冲突，但“独占”没有波次内和波次间交接定义，不能直接用于并行执行。

**修正：** 公共文件由一名集成负责人统一合入，其它 Agent 提供接口、项目引用及注册变更清单；明确每个波次的读写矩阵和提交边界。只并行不共享写路径且没有未交付接口依赖的任务。

### R15 · Medium：资源、构建和用户延迟数字被当作已证明收益

**位置：** [总规划:23、33–44、57、219–220、232、288、350](<E:/bot/bot-agent/docs/plans/modular-monolith-refactoring-plan.md#L33>)、[Agent 06:17、28–34、62](<E:/bot/bot-agent/docs/plans/agents/06-agent-headless-cli-and-deploy.md#L17>)。

**问题与影响：** 常驻 45–65MB、峰值 95MB、启动 0.8s、构建 1–2s、延迟减半没有测量条件。嵌入资源影响构建和产物，不等同于全部资源必然常驻 RAM；删除资源也不保证总解决方案构建骤降。框架语言本身不能证明某项目必然泄漏或 OOM。

**修正：** 将数字分为目标、估算与测量值。资源基准记录同机服务、cgroup/进程口径、并发和持续时间；分别测冷/热构建、启动、首字及完整回复延迟。本次 18.03 秒构建含还原，仅作为本次事实，不能用来推导未来目标必然失败。

SQLite 的“禁止跨进程写库”也需改正：WAL 可支持同一主机多进程读写协调，写入并发仍受锁约束；本项目的进程内写锁只能串行本进程调用。若项目选择单写宿主，应写成架构政策而非 SQLite 能力限制。依据：[SQLite WAL 官方文档](https://www.sqlite.org/wal.html)。

### R16 · Medium：测试数量和旧扫描根不能作为新架构验收标准

**位置：** [总调度:99](<E:/bot/bot-agent/docs/plans/agents/00-orchestration-and-dependency-graph.md#L99>)、[Agent 06:69–78](<E:/bot/bot-agent/docs/plans/agents/06-agent-headless-cli-and-deploy.md#L69>)、[Agent 07:64](<E:/bot/bot-agent/docs/plans/agents/07-agent-modern-frontend.md#L64>)。

**问题与影响：** 固定 594、95+、365+ 并不证明关键行为覆盖。现有架构探针只扫描 Headless，迁出去的 Domain 零违规可能是假空集；解决方案构建也不代替未纳入解决方案的探针执行。IntegrationHarness 从独立 DLL 启动宿主，仅构建 Harness 可能得到旧宿主结果。

**修正：** 使用行为矩阵：秘密不回显、空名单 fail-closed、平台实例隔离、插件禁用无外部调用、保存失败不发布、并发版本冲突、事件恢复不重复执行、取消及停机收口。扫描所有目标程序集与项目引用，未找到应存在的模块时失败；棘轮阈值不放宽。集成场景前单独重建 Headless。Vue 与 TSX、EventSource 认证及 API 基址等前端细节待契约冻结后选择，不把两套文件结构并列成执行指令。

## 三、可以保留的方向与应调整的任务顺序

可以保留 .NET 8 模块化单体、Core 纯领域端口、后台组装根、独立静态 Web、已有有限步进循环、插件门禁和 MCP 能力治理。优先复用 [ToolDirectory](<E:/bot/bot-agent/src/BotAgent.Headless/Services/Tools/ToolDirectory.cs>)、[AgentTurnLoop](<E:/bot/bot-agent/src/BotAgent.Headless/Services/Reply/AgentTurnLoop.cs>)、[SettingsBox](<E:/bot/bot-agent/src/BotAgent.Headless/Services/SettingsBox.cs>)、[SettingsStore](<E:/bot/bot-agent/src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs>)、平台策略与回复审计，避免在重构中重造相同功能。

建议将施工依赖整理为：

1. **修正基线与规划事实。** 解决安全探针失败，修复架构扫描覆盖；校正完成状态、配置真源、接口名称与回退流程。
2. **完成 Wave 1–2 宿主接入。** 配置保存及插件/驱动端口、平台/模型独立实现真实接入；统一策略与 `none` 行为回归。
3. **Wave 3 交付 Engine。** 在既有自然工具循环上迁移、分层并补齐门禁、审批、取消、有界后台评估。
4. **Wave 4 交付 MCP。** 正确传输与生命周期、工具投影、治理、秘密和资源边界逐项验收。
5. **Wave 5 交付完整后端。** CLI、组装根纯化、逐能力 ZBA、统一保存、任务与事件契约、OpenAPI 和回归证据共同验收。
6. **Wave 6 才启动 Web。** 冻结后的 ZBA 驱动独立前端，分别构建和发布，完成 Beta 验收。

以上为修订建议，**没有在本次评审中执行重构或修复生产代码**。

## 四、下一版规划必须补齐的交付清单

| 交付物 | 负责人建议 | 可检查结果 |
| --- | --- | --- |
| 当前状态与证据表 | Orchestrator | 完成/进行中/待实现与当前实测一致 |
| 接口和数据真源说明 | Core/Storage | 真实已有端口、拟建端口、事务和发布顺序明确 |
| 运行链路迁移表 | 集成负责人 | 旧实现→新程序集→组装注册→场景证据可追踪 |
| 插件/驱动管理能力矩阵 | Platforms/Engine | 每个能力的查询、配置、状态、启停及支持限制明确 |
| ZBA OpenAPI 与契约测试 | 新增 ZBA 负责人 | 保存、版本、秘密、审批、事件与错误行为闭环 |
| MCP 协议与权限说明 | MCP 负责人 | 传输、取消、恢复、治理及 Docker 特权边界明确 |
| 后台任务生命周期 | Engine 负责人 | 队列、并发、异常、取消和停机预算可验证 |
| 跨工程架构护栏 | QA/集成负责人 | 扫描新模块及依赖 DAG，棘轮不放宽 |
| Beta 发布条件 | 项目组 | 后端先验收；前端独立交付；时间窗口受验收约束 |

项目组需要明确角色分配与待定契约，但不必等这些决定才能修正文档中的事实错误。新面板的用户目标、四部分规划和建议 ZBA 契约已另存为 [Z-bot Panel v0.1 Beta 草案](<E:/bot/bot-agent/docs/plans/z-bot-panel-v0.1-beta-plan.md>)。

## 五、本次交付与限制

本次新增评审报告及面板规划草案；原总规划、八份手册、README、架构阈值和生产代码均未改写。报告列出的修改是下一次精准同步的依据，不能因本报告存在就认为旧手册中的错误已经修复。

未进行 commit、push、部署、线上数据读取或 Web 实现。未重新核对镜像内运行时清单，未验证镜像副本与另一个源码目录完全一致，未运行当前集成场景或生产性能基准。这些限制不会消除已确认的源码契约缺口与探针失败，但必须在后续验收中补证。
