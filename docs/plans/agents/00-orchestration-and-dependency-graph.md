# Agent-00：重构执行总纲与证据登记

> 修订：2026-10-07，第二版。先读[总规划](../modular-monolith-refactoring-plan.md)。
> 本文件是执行状态、共享写权限、基线验证和交付记录的唯一来源。
> 执行登记更新：2026-10-10。授权、认领和证据见第四至第十七节；负责人已授权00持续推进到阶段一可开工。C2-A/B宿主/合成证据及10月9日架构159/0见第十四节；完整字段参考见十六节，精确0.2类型及三方案裁决见十七节/候选第10节。0.2已拒绝冻结，正式消费者仍未确认，阶段一/下游仍未解锁。

## 一、为什么（Why）

防止拟建接口冒充现有接口、孤立工程冒充宿主集成、历史成绩冒充当前验收，以及多个 Agent 覆盖共享文件。任务手册是施工约束，不是自动开工指令。

## 二、做成什么样（What）

### 2.1 当前证据与状态

当前核对日期：2026-10-10；对象：当前仓库工作区，不是干净发布提交。负责人允许将既有改动作为迁移输入；其原作者归属未核实，不计入本任务成果，不整理、不提交这些代码。第四至第十七节是各批次带时间的记录；第十四节为10月9日C2-B实际接入/合成证据，十五节为只读候选及20DLL/17正文绑定，十六节为完整字段差集，十七节为精确类型候选及实际缺陷裁决，没有新的项目运行，不能将局部接入或静态矩阵称为整体Wave1验收。

| 波次 | 源码存在 | 宿主接入证据 | 验收状态 |
| --- | --- | --- | --- |
| Wave 1 | Core、Storage工程存在 | Headless→Core/别名Storage；21个旧Persistence入口为兼容类型/转发，实际逻辑采用Storage；六个Host独有适配器保留；类型校验仍由Host传入 | 未验收；C2局部接入/仓储合成通过，当前架构159/0，SQL137/IO62/Clock3且阈值不变；C3/C4及消费者整体冻结、整批保存/完整兼容恢复门禁未完 |
| Wave 2A | Platforms 工程存在 | Headless 直接引用 Platforms；行为接入仍需回归 | 未验收；下游冻结 |
| Wave 2B | Model 工程存在 | Headless 未直接引用 Model；不能声称已采用新传输 | 未验收；下游冻结 |
| Wave 3 | Engine 工程尚未建立 | 回复及工具路径仍在 Headless | 未解锁 |
| Wave 4 | Mcp 工程尚未建立 | 未核实 MCP 运行链路 | 未解锁 |
| Wave 5 | 现有宿主及旧管理接口存在 | wwwroot 仍内嵌；新 ZBA 未验收 | 未解锁 |
| Wave 6 | 独立 web 工程尚未建立 | 新面板尚未交付 | 未解锁 |

状态流：待核对 → 待认领 → 施工中 → 待验收 → 已验收；证据过期或出现失败时退回待验收。当前事实变化后更新本表，不在总规划和七份手册复制进度。

历史[第二轮评审](../modular-monolith-review-2026-10-07.md)记录：构建通过；SafetyProbe 578 通过、16 失败；ArchitectureProbe 86 通过、6 失败。这些数字只属于历史评审，不能代替第四节的当前执行记录，也不能据此推断线上已发生故障。

### 2.2 接口事实索引

路径均相对仓库根；若路径变化应重新检索，不根据旧名字创建平行接口。

| 已有入口 | 静态核对位置 | 使用边界 |
| --- | --- | --- |
| IPlatformAdapter / IPlatformMessenger / IPlatformRegistry | src/BotAgent.Core/Domain/Ports/IPlatformAdapter.cs | 优先复用；管理能力不足时评审扩展 |
| IBotPlugin / IPluginRegistry | src/BotAgent.Core/Domain/Plugins/ | 已有插件契约，不等于全部业务已插件化 |
| SettingsBox.ApplyPersisted | src/BotAgent.Headless/Services/SettingsBox.cs | 先持久化后发布；不等于已提供外部版本冲突控制 |
| SettingsStore | src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs | 行为配置当前在 SQLite settings 表 |
| ReplyAuditRules.Judge | src/BotAgent.Core/Domain/Rendering/ReplyAuditRules.cs | 判定允许/拦截，不是任意文本脱敏器 |
| ThinkingBudgetResolver | src/BotAgent.Model/ThinkingBudgetResolver.cs | 已有解析代码，宿主使用和报文仍需验证 |
| InlineTurnTools | src/BotAgent.Headless/Services/Reply/InlineTurnTools.cs | 已有工具循环，不应重复实现后再迁移 |

本轮源码检索未找到 `IUnifiedConfigurationStore`、`IPlatformDriver` 定义。它们是旧版拟建名，不是可直接调用的 API。统一管理/保存需求保留，具体签名由 Agent-01 评审；新增 ZBA 路径、DTO、错误码和认证由 Agent-06 冻结，前端不能先猜。

### 2.3 责任与写权限

| 角色 | 任务所有权 | 写权限规则 |
| --- | --- | --- |
| 00 | 状态、依赖、接口批准、共享文件合入、验收核对 | 共享文件唯一最终写入者 |
| 01 | Core / Storage，配置及管理契约 | 契约经消费者确认后修改；测试文件按任务明确认领 |
| 02 | Platforms | 平台迁出文件须逐文件交接 |
| 03 | Model | 模型迁出文件须逐文件交接 |
| 04 | Engine，含内部 Plugins | 回复/工具等旧路径删除须经接入验证和交接 |
| 05 | Mcp | 不修改宿主或 Engine 的共享接入文件 |
| 06 | ZBA / Panel、CLI、宿主集成设计、部署验证 | 对共享文件提供变更清单或补丁，由 00 合入 |
| 07 | 独立 web | 测试、代理/部署文件经认领后修改，不写后端实现 |

共享文件包括 `BotAgent.slnx`、全部跨工程项目引用、Headless 的 `Host/**`、`AppSettings*`、`SettingsBox*`、架构护栏、公共测试入口、CI 和部署配置。Agent-00 合入前核对当前内容，不能覆盖其他人的工作。Agent-06 负责集成方案不等于可以同时写这些文件。

每次认领记录：执行者、确切路径、基线提交/工作区摘要、开始时间、交付对象。测试路径和旧源码不因“属于某功能”自动获得写权。00 暂时移交共享写权时先登记接收者和范围，暂停其他写入；接收者交回差异和验证结果后解除移交。依赖争议或同文件冲突时停止该文件写入，等待协调。

## 三、如何执行（How）

### 3.1 准入与顺序

1. 取得负责人对当前代码任务的授权；只获文档授权时到此停止。
2. 核对工作区、适用 AGENTS.md、源接口及当前测试基线；保留既有改动。
3. 00 确认 Wave 1 的契约和接入；Wave 2A/2B 在契约稳定且写权限不交叉时可并行。
4. Wave 1/2 验收后推进 Wave 3，再 Wave 4，最后 Wave 5 集成验收。06 从前期参与契约设计。
5. Wave 1–5 和面板规划的 ZBA 必选项验收后，由 00 核对、负责人明确解锁 Wave 6。
6. 模块负责人交付不等于整项目验收；任一门禁失败均不能把下游标为已解锁。

### 3.2 验证命令基线

下面是**现有工程**的候选基线命令，不是本轮执行记录。从仓库根使用 pwsh；新增工程先核对路径。每个原生命令后检查退出码，失败即停；不能仅靠 PowerShell 的错误偏好处理外部程序失败。

```pwsh
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Headless build failed' }
dotnet build BotAgent.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw 'Solution build failed' }

foreach ($probe in @('SafetyProbe', 'ArchitectureProbe')) {
    dotnet run --project "tests/BotAgent.$probe/BotAgent.$probe.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "$probe failed" }
}

dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Harness build failed' }
$hadOnly = Test-Path Env:QQCHAT_IT_ONLY
$oldOnly = $env:QQCHAT_IT_ONLY
try {
    foreach ($scenario in @('s21', 's36', 's52')) {
        $env:QQCHAT_IT_ONLY = $scenario
        dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
        if ($LASTEXITCODE -ne 0) { throw "$scenario failed" }
    }
}
finally {
    if ($hadOnly) { $env:QQCHAT_IT_ONLY = $oldOnly }
    else { Remove-Item Env:QQCHAT_IT_ONLY -ErrorAction SilentlyContinue }
}
```

Harness 另启 Headless 进程，必须先构建 Headless。S21/S36/S52 只是定向基线，不是全矩阵；各手册另列专项行为。完整验收依据当时的 [CONTRIBUTING](../../../CONTRIBUTING.md)、CI 和 harness 实际入口核对覆盖与排除项，旧记录里的 S42 排除不能被“全绿”掩盖。修改旧面板时仍须运行现有前端探针；新增 Web 需要独立浏览器验收，不能只沿用旧正则扫描。

编译无错误；新警告必须处理，历史警告单列；安全/架构/适用行为测试必须通过，架构阈值不得调高或删断言换绿。断言数量用于描述覆盖，不是目标 KPI。源码扫描通过不等于部署、发布、生产资源或协议互通已经验证。

### 3.3 每次交付记录模板

```text
任务 / 波次 / 执行者：
授权范围 / 写权交接：
提交标识 + 工作区差异指纹（未提交时说明）：
接口版本 / 变更文件 / 迁移旧路径：
宿主调用链证据：
命令 / 环境 / 时间（含时区）/ 退出码：
通过与失败数量 / 安全证据位置：
未运行、排除项与剩余风险：
配置与数据兼容 / 回退步骤及演练：
00 核对结论：
负责人批准与下一步：
```

证据只含合成数据结果、安全元数据和摘要，不能把会话、真实标识、密钥或原始工具报文附进报告。源码变化后，受影响验收重新执行；提交号相同但工作区不同也不能复用“已通过”。

### 3.4 回退与停止条件

- 执行前盘点已有修改并保存本次允许范围的差异；不把工作区全量敏感数据复制到快照。
- 失败时先冻结下游，定位本次变更。未提交改动只撤销能够明确归属于本任务的片段；归属不明或有并行修改时请负责人处理。
- 已提交变更可在确认依赖后生成新的逆向提交；部署回退只切换已记录兼容版本的产物。均须先确认作用范围，不能恢复整个目录覆盖别人修改。
- 数据迁移须先定义版本兼容和备份恢复；旧二进制能启动不证明旧版本能读新数据。
- 禁止使用强制 checkout、hard reset 或清理未跟踪文件作为自动回退。
- 接口未冻结、权限不明确、测试失败未定位、保存通路被切断、秘密可能外泄，均停止相应施工，不自行降低门禁。

### 3.5 文档交付的验收

只检查文档链接、现状/目标标记、跨文件一致性及无上下文读者能否找到执行边界；代码基线不由文档检查替代。文档通过后仍维持本节 2.1 的未验收状态，直到代码任务获得授权并提交新证据。

## 四、2026-10-08 基线阶段执行登记

### 4.1 授权、认领与输入指纹

- 负责人在本次聊天答复「1.允许 / 2.是 / 3.是」：允许既有改动作为迁移输入，批准登记认领、运行合成基线及契约评审；基线修复另批逐文件写权。该答复不授权提交、推送、部署、发布或生产操作。
- 本阶段任务：B0 基线登记与契约评审准备；执行者 / 交付对象：Agent-00 / 负责人及后续执行 Agent。
- 唯一工作仓库：`%WORKDIR%\bot-agent`；基线分支 `main`，HEAD `1f3181e21e8d1f9531e41066b033d8d60218d580`。
- 开始指纹时间：`2026-10-08 12:03:01 +08:00`；Windows 原生命令均从仓库根使用 pwsh。
- 既有状态共 292 条：已暂存删除 20、未暂存删除 92、未暂存修改 6、未跟踪 174。原执行者尚未核实；允许使用不等于将其归属于 00。
- 状态及对应文件 SHA-256：`495FD73BD62A42A6F8EFA27C229036006EC154C8FD53704CBEF54B7D281C36DB`。
- 暂存差异 `git diff --cached --raw` SHA-256：`65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC`。
- 安全源码 / 测试 / 文档 / 工程配置清单共 529 项；按相对路径排序后连接「路径|SHA-256 或 MISSING」，以 LF 分隔、UTF-8 编码计算摘要：`5240D2D505BC0BC1C9455B1DACDBA4A7CE99C142C33EBEBF16F770955C6970E4`。不读取运行数据、真实数据库或日志。

| 执行者 | 本次认领的确切写入路径 | 状态 / 交接 |
| --- | --- | --- |
| 00 | `%WORKDIR%\bot-agent\docs\plans\agents\00-orchestration-and-dependency-graph.md` | 已认领；保存原文件内容及哈希用于本任务片段回退；不暂存 |
| 00 | 各已存在工程的忽略式 bin/obj 构建产物；本次验证的独立合成临时目录 | 仅限获准验证的派生产物；临时目录使用新 UUID，测试进程的两套数据根均指向该目录；不构成源码修复写权，不清理其他产物 |
| 01～06 | 尚无源码或测试文件认领 | 本阶段只准备契约评审；共享文件补丁及旧文件移交须另列确切路径、取得批准并登记 |
| 07 | 无 | Wave 6 锁定，无 Web 施工授权 |

本阶段不启动全部波次、不创建执行 Agent，不把契约草案登记为冻结。后续源码修复只在逐文件批复后登记；同文件已有修改先核对补丁基准及归属，临时移交期间只有一个写入者。

### 4.2 当前源码缺口与失败归属候选

| 编号 | 已核对的文件 / 调用点 | 当前结论及门禁 |
| --- | --- | --- |
| B0-01 | `Headless/Host/CompositionRoot.cs`、`Headless/BotAgent.Headless.csproj`；路径均位于 `src/` | Core / Platforms 已被引用；Storage / Model 未被宿主引用，旧仓储与模型链路仍在使用；Wave 1 / 2 未验收 |
| B0-02 | `src/BotAgent.Headless/Services/AppSettings.cs`、`SettingsBox.cs`；`src/BotAgent.Platforms/PlatformOptions.cs` | 基线存在派生 / 基类状态割裂；00 已完成获批 R1，双类型、快照及秘密 JSON 排除的定向证据见第五节；不等于统一保存契约冻结或 Wave 1 准出 |
| B0-03 | `src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs`、`Adapters/Persistence/BotConfig.cs`；`src/BotAgent.Headless/wwwroot/app.js` | 基线官方 Secret 红线冲突已在获批 R2 修复并定向复验，见第六节；旧记录保留，旧客户端需刷新，生产配置切换及整体门禁未验收 |
| B0-04 | `src/BotAgent.Storage/AppDatabase.cs`、旧 `Headless/Adapters/Persistence/AppDatabase.cs`；Core `IWebhookDedupStore`、Headless `FeishuWebhookDedupStore` | R5-A 已将平台去重接入宿主现有事务入口、移除平台自写库，见第九节；新旧存储仍重复，未接入独立 Storage，不把本批称为全局保存 / writer 验收 |
| B0-05 | `tests/BotAgent.ReverseTransportProbe/BotAgent.ReverseTransportProbe.csproj` | 原 Compile Include 指向两份已删除源码；获批 R4 已等价迁到 Platforms 真实文件，原合成探针 13 / 0，见第八节；不代表平台波次准出 |
| B0-06 | `tests/BotAgent.ArchitectureProbe/SourceIndex.cs`、`Program.cs`、`Baseline.cs`、`Metrics.cs` | B0 仅扫描 Headless；获批 R3 覆盖五工程、迁移锚点并拒绝空扫描 / 非法引用；R5-A 复验 120 / 4，剩余重复存储与系统时钟违规见第九节；护栏及阈值未改 |
| B0-07 | `tests/BotAgent.IntegrationHarness/Program.cs`、`.github/workflows/ci.yml`、`CONTRIBUTING.md` | 获批 R4 已补 CI 的 S52 声明；50 个注册项与矩阵一致，未实际运行 GitHub CI。S42 仍明确排除；S36 是隐私设置、S19 是引用、S47 是工具循环，分别报告覆盖 |
| B0-08 | `src/BotAgent.Headless/Dockerfile` | R4 的输入修复及当时源码的 Windows 发布 / Linux 镜像构建通过，见 8.7；R5 产品源码已变化，本批未重建镜像，不把 R4 镜像当作当前产物；未运行应用容器或操作生产，低内存产物路线未改 |

上述问题均在本任务修改文档前存在。静态缺口不冒充测试失败；动态失败记录在 4.4，历史失败不自动视为本任务回归。新失败归属对应变更批次；未定位或接口不兼容时冻结受影响下游。

### 4.3 契约评审队列（全部待确认，未冻结）

| 议题 | 提交者 / 消费者 | 复用入口及评审完成条件 |
| --- | --- | --- |
| 配置与统一保存 | 01；02/03/04/05/06 消费；00 登记 | 先复用 `SettingsBox.ApplyPersisted`、`ISettingsRepository`、`SettingsStore` 的持久化后发布及配置/审计事务；明确快照版本、整批校验、并发冲突、幂等作用域/有效期、空值及未知字段、提交/应用/需重启、响应丢失查询；不把跨介质操作伪装成单一事务 |
| 秘密与配置一致性 | 01/02/06；00 协调 | 官方 Secret 只来自环境变量，面板不接收、保存或返回值片段；其它秘密显式保留/替换/删除；避免原始秘密进入快照 DTO 或测试输出；统一平台属性所有权并保留旧非秘密配置字段兼容 |
| 统一写入与兼容 | 01/02；04/06 消费 | 盘点旧 SettingsStore、BotConfig、ModelProviderStore、AuditLogStore、TraceArchiveStore；明确单一数据库写入协调、飞书持久化去重、审计端口、 schema 版本与合成迁移恢复；先验证再删除旧实现 |
| 管理与能力 | 01；02/03/04/05/06 消费 | 复用 `IPlatformAdapter` / `IPlatformMessenger` / `IPlatformRegistry`、`IBotPlugin` / `IPluginRegistry`、模型端口；冻结稳定标识、实例隔离、真实状态、支持动作、校验和启停/释放语义；不支持的动作明确拒绝 |
| 工具、审批与后台任务 | 04/05；01/06 协作 | 复用 `ToolDirectory`、门禁、`AgentTurnLoop` / `InlineTurnTools`；明确插件禁用入口、审批绑定与过期、预算、队列满载及停机收口；取消不撤销远端副作用、断线不重放工具、未知终态可查询 |
| ZBA 与兼容 | 06 从本阶段参与；01～05 提供能力；07 待解锁 | 先对照面板规划必选范围，提交实际路由/DTO/错误、认证授权/CSRF、恢复方式及版本策略；OpenAPI 路径及真实测试入口落地后登记，不预造接口；资源测量方案由负责人确认 |

每项冻结记录必须包括：实际文件与接口版本、参与消费者确认、兼容/迁移与回退、合成用例及 00 核对。当前只有评审队列，尚无消费者签署或施工准入。02/03 仅在 Wave 1 准出且无交叉写权时并行；Web 仍须 Wave 1～5、必选 ZBA、安全、资源、回退证据齐全并由负责人明确批准。

### 4.4 当前基线执行记录

执行环境：Windows / pwsh；SDK `10.0.101`，目标 `net8.0`，可用 .NET 8 运行时 `8.0.10`；Node `24.14.1`、Python `3.13.12`。该本地环境不同于 CI 的 SDK 8 / Node 22 / Python 3.12；结果不等于 CI 或生产验收。

验证顺序：先 Headless，再 solution；适用安全/架构探针；成功重建 Headless 后才能启动 harness。独立的纯源码架构检查可以单独登记，不绕过已失败的宿主门禁。旧二进制、零场景及未纳入场景不能算通过。执行前仅审查配置来源及测试隔离，不读取实际环境秘密值。

| 编号 | 命令（仓库根 / pwsh） | 时间（UTC+08:00）/ 退出码 | 结果 / 覆盖 |
| --- | --- | --- | --- |
| V0 | `dotnet --version`、`node --version`、`python --version`；SDK / runtime 列表 | 2026-10-08；各 0 | 环境能力核对，不是项目测试 |
| V1 | `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release` | 2026-10-08 12:05:04～12:05:09；0 | 构建成功，0 警告 / 0 错误；不等于行为验收 |
| V2 | `dotnet build BotAgent.slnx -c Release` | 2026-10-08 12:05:23～12:05:25；0 | 构建成功，0 警告 / 0 错误；解决方案未包含所有专项探针 |
| V3 | `dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release` | 2026-10-08 12:06:50～12:07:06；1 | 当前运行 578 通过 / 16 失败；平台策略、开关迁移、空动作名单及本地白名单；不是历史成绩复用 |
| V4 | `dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release` | 2026-10-08 12:07:32～12:07:35；1 | 独立纯源码检查，86 通过 / 6 失败；仅扫描 Headless 的 141 个源文件，Domain 文件数为 0；通过项不证明迁出模块覆盖 |
| V5 | `dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll` | 2026-10-08 12:09:53～12:10:03；1 | 另一个合成临时目录重复复现，578 通过 / 16 失败；16 条失败检查与 V3 相同 |

V3 / V5 在独立 pwsh 子进程内移除继承的 `BOTAGENT_*`、`QQCHAT_*`、`OPENAI_*`、`MINIMAX_*`、`TTS_*`、`HARNESS_*` 及 `HEALTH_PORT` 配置，仅设置本次合成数据根和关闭文件日志的环境开关；不读取秘密值。V4 显式设置源码根为唯一仓库。V1/V2 原生命令退出码逐项检查；V3/V4/V5 失败分别终止该项，没有继续执行其下游。

当前失败归属与诊断边界：

- V3/V5 的 14 项位于 `tests/BotAgent.SafetyProbe/PlatformPolicyTests.cs`，另 2 项在 `Program.cs` 的本地白名单检查。失败在本任务源码修改前发生，归属既有迁移基线，00 协调 01/02/06；不能记为本任务引入或已修复。
- 首要假设：`AppSettings` 的同名派生属性与 `PlatformOptions` 基类属性割裂；源码证明测试向派生属性赋值，而 `SettingsBox` 显式接口 → `PlatformPolicyResolver` / `PlatformSwitchSettings` 使用基类，快照仍保留两套字段。预测是统一属性所有权后两种静态类型读取一致、原失败消失；目前未通过修改验证，不声称全部 16 项只有一个原因。
- 次要假设：版本化策略迁移 / 显式空动作名单语义存在独立缺口；若统一属性后仍失败，则在同一合成用例逐项定位，不能放宽 fail-closed。快照的嵌套列表隔离也是候选；用旧快照不变性回归区分，未另加探针或插桩。
- V4 的 6 项分别涉及迁走的 ToolSpec、工具提示词锚点、TurnTrace、LocalBase、ToolPolicy 与 ToolGate。源码位置迁移可解释扫描盲区，但移动断言不代表原行为已验证；01/04 提供类型映射，06 提供跨工程检查方案，00 合入。

尚未运行：其它专项探针、ReverseTransportProbe 构建、旧前端探针、harness 定向/全矩阵、ZBA、安全专项负例、资源与回退演练。因强制安全 / 架构基线失败，停止当前验收链并冻结所有受影响下游；构建通过不豁免失败。S42 仍排除、S52 的 CI 覆盖缺口未修复。harness 只清理 `QQCHAT_*`，其它配置别名隔离仍须在执行前确认；没有运行旧 DLL 冒充集成验收。

### 4.5 本阶段回退与交付

本次只写执行总纲，回退时恢复其本任务片段或执行前保留的确切内容；存在后续并行改动则先协调，不覆盖整份文件。构建派生产物不自动清理。源代码、暂存区及其它未跟踪文件保持原状。未做数据迁移、秘密清理、发布或生产回退演练。

2026-10-08 12:12:59 +08:00 核对：529 项安全文件清单中只有本执行总纲改变，其余 528 项（含既有缺失文件记录）一致；HEAD、292 条工作区状态及暂存差异指纹未变。`git diff --check` 退出码 0；执行总纲本身未跟踪，另直接检查本地链接、尾随空白与代码围栏，均通过，不能用 tracked diff 检查替代它。

本次产物 SHA-256（不代表宿主采用独立 Storage / Model）：

| 产物路径（仓库相对） | SHA-256 |
| --- | --- |
| `src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll` | `2F0CB8166A84B744C89F7C5E794708AFD4A7243387299E82E040E023012A09DF` |
| `tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll` | `87DDAC77FB8858D4A2E589F28E8ED67EBFC7974196F862B4D4FEA0E735FE7F00` |
| `tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll` | `619EB4BF2FFED72A3CBEAC468E65DF5B2C68A39F8F67B2F59A3EDD64D4E5642D` |

交付须按 3.3 模板列出实际命令/时间/退出码、基线失败归属及源码清单前后核对；补丁修复、契约冻结、模块准出和下游解锁分别签署。当前下一步是负责人批准明确修复路径；批准前只保留已登记文档写权。

### 4.6 待批准的逐文件修复批次

以下路径均相对唯一仓库 `%WORKDIR%\bot-agent`；R1 / R2 / R3 / R4 已单独获批并分别在第五 / 六 / 七 / 八节登记，不因列入此表自动获得额外写权。00 最终合入共享路径；临时移交前另登记接收者、基准、开始时间及交回条件。

| 批次 | 拟修改的确切路径 | 修复目标 / 必须复验 |
| --- | --- | --- |
| R1 配置属性所有权 | `src/BotAgent.Headless/Services/AppSettings.cs`；`src/BotAgent.Platforms/PlatformOptions.cs`；`tests/BotAgent.SettingsScopeProbe/Program.cs` | 统一同名平台字段，保持旧非秘密字段名与默认值；迁移 JsonIgnore 等秘密序列化约束；先补两种静态类型读写、快照及合成持久化负例，再修复。复验 SettingsScopeProbe、SafetyProbe、ProductionSpecProbe 及成功重建后的 S52；不改原失败断言或权限语义 |
| R2 官方 Secret 红线 | `src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs`；`src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs`；`src/BotAgent.Headless/wwwroot/app.js`；`src/BotAgent.Headless/wwwroot/index.html`；`tests/BotAgent.IntegrationHarness/PrivacySettingsScenario.cs`；`tests/BotAgent.FrontendProbe/probe.mjs` | 拒绝官方 Secret 面板写入/清空请求，不返回值片段；启动只取环境变量、无环境变量时不回填仓储；旧记录不自动删除。移除旧页面输入与提交，补合成拒收、无值回显、忽略旧存储、环境恢复及真实保存链路用例；配置切换由负责人确认，不操作生产 |
| R3 等价架构护栏 | `tests/BotAgent.ArchitectureProbe/SourceIndex.cs`；`tests/BotAgent.ArchitectureProbe/Program.cs`；`tests/BotAgent.ArchitectureProbe/Baseline.cs`；`tests/BotAgent.ArchitectureProbe/Metrics.cs` | 扫描实际业务工程、迁移原锚点、核对真实项目引用并拒绝空模块/空扫描；维护旧结构约束等价性，阈值只许不变或调低；复验 ArchitectureProbe 与安全/行为覆盖 |
| R4 测试与构建入口 | `tests/BotAgent.ReverseTransportProbe/BotAgent.ReverseTransportProbe.csproj`；`.github/workflows/ci.yml`；`CONTRIBUTING.md`；`src/BotAgent.Headless/Dockerfile` | 逐项核对新传输依赖后迁移等价测试入口、CI 纳入 S52、文档披露实际矩阵；修复源码构建上下文，仍保留低内存产物式路线。需分别验证对应探针及本地构建，不声称 CI 或生产已通过 |

R1 / R2 / R3 / R4 已单独批准执行；R5-A 随后独立获批收口飞书去重，十路径及证据见第九节；R3 剩余重复存储 / 时钟业务违规仍须另批。各批次不以测试迁移掩盖行为或安全失败。新发现清单外文件需求时先停并补批；资源测量、ZBA 实施、新 Web、生产切换仍不在本阶段授权内。

## 五、R1 配置属性遮蔽修复（Agent-00）

### 5.1 授权与写权

- 负责人本次明确「批准 R1 配置属性遮蔽修复；记住你是 agent00」。执行者、最终共享合入者、交付对象：Agent-00、Agent-00、负责人。未启动其它 Agent，不移交写权。
- 准入核对：2026-10-08 12:23:55 +08:00；HEAD 与第四节相同，292 条状态及暂存差异均未变化；原 529 项清单只有第四节已登记的总纲变更，三个 R1 文件仍与原基线一致。
- 认领下列三个文件及本执行总纲；保留既有编辑，仅追加本任务补丁，不暂存。清单外需求必须先补批。

| 执行者 | 确切写入路径 | R1 开始 SHA-256 / 交接 |
| --- | --- | --- |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\AppSettings.cs` | `0C70B47DE30AF695D47E8F6C9D6C78F25A6A6D55C6B9FDA6640E1E4DBD62FE7F`；保留原有修改 |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Platforms\PlatformOptions.cs` | `B133D13CC7B690714039B8E50DA36295FD16FEE03CC57790A96FFD2BCA4F5A52`；保留原有未跟踪实现 |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.SettingsScopeProbe\Program.cs` | `4285BBD1E29CCA3473E7FD04CB8EBA8113EE65BF469A889AF5F6B1B4415BA046`；只追加合成回归 |

- 已在会话中保存四个认领文件的原始字节压缩副本及哈希；不复制生产数据、不创建全仓库备份。回退只撤销 R1 片段；有后续修改则先协调。
- 已批准的回归边界：`AppSettings` / `PlatformOptions` 双静态类型、`SettingsBox` 平台接口、真实策略解析与开关归一化、`Snapshot`、`SettingsStore.Save/Load` 及 JSON 秘密排除。保持签名、旧非秘密字段名、默认值及 fail-closed 语义；不新增统一保存或 ZBA 契约，不将现有接口登记为消费者已签署冻结。
- 只进行 R1 定向诊断 / 复验；原安全、架构及 R2 阻塞不因定向测试而豁免。成功重建 Headless 后可执行 R1 已列的 S52 合成场景，但不是波次准出；其它波次、Web、提交、推送、部署、发布及生产操作仍未授权。

### 5.2 实施、接口及真实调用链

- 根因证据：修复前新增双类型回归失败，宿主赋值在 `IPlatformSettingsAccessor.Current` 丢失；新增凭据回归失败，配置 JSON 包含应排除字段。统一属性后两项通过，未经修改的 SafetyProbe 原 16 项失败全部消失；没有调整开关迁移、空名单或权限语义。
- `AppSettings.cs`：移除 20 个重复可写平台属性及 2 个重复派生登录值，继承 `PlatformOptions` 的单一所有权；保留 `Snapshot` 对策略、动作列表和特性字典的独立复制，修正原「只有标量」注释。
- `PlatformOptions.cs`：保留原字段名与默认值；统一维护 `OneBotToken`、`OfficialAppSecret`、`FeishuAppSecret`、`FeishuEncryptKey` 的 `[JsonIgnore]`，派生登录值也排除。前三个相关派生秘密约束移到基类；恢复原 OneBot 令牌约束。`FeishuVerificationToken` 保持原兼容序列化行为，本批不扩大秘密策略范围。
- `SettingsScopeProbe/Program.cs`：只添加 using 和四个合成检查；原 13 个检查、断言及其后实现逐字保留。覆盖双向配置视图、真实解析器、双 JSON 视图凭据排除及注入拒绝、旧字段 / 默认值往返、规范化、保存后发布、显式快照和失败保存的嵌套隔离。
- 接口口径：R1 源码契约，仍为现有 `AppSettings : PlatformOptions`、`IPlatformSettingsAccessor.Current`、`SettingsBox.ApplyPersisted`、`Snapshot`、`SettingsStore.Save/Load`；未新增接口或路由，未声明跨版本二进制兼容或消费者契约已冻结。
- 静态链路：`Program → CompositionRoot.BootstrapSettings → BotConfig.Load`；`CompositionRoot.Build` 用同一 `SettingsBox` 构造平台驱动及 `PlatformPolicyResolver`。保存经 `WebUiServer.Settings.HandleSettingsSave → SettingsHotReload.ApplyRuntimeSettings → SettingsBox.ApplyPersisted → SettingsStore.Save`，持久化后发布和重建；Platforms 通过平台接口读取同一版本。
- 动态链路：S52 启动真实重建 Headless，读取 / 保存 settings 与 platforms；本地入站正例进入真实回复链、请求 MockOpenAi 并产生 outbox；静音不请求模型、不发送，禁用拒绝入站；重启继续保持配置及静音。官方 / 飞书无凭据，仅证明配置投影及断开状态，不声称真实协议互通。

### 5.3 当前 R1 命令与证据

环境沿用 4.4 的本地 SDK / net8.0，均在唯一仓库根使用 pwsh。每项记录原生命令退出码；失败项单独结束，不继续其下游。下列 S52 是获批 R1 的定向诊断，不绕过架构失败解锁波次。

| 编号 | 命令 | 时间（2026-10-08 / UTC+08:00）/ 退出码 | 结果 |
| --- | --- | --- | --- |
| R1-V1 | `dotnet run --project tests/BotAgent.SettingsScopeProbe/BotAgent.SettingsScopeProbe.csproj -c Release` | 12:26:01～12:26:22；1 | 修复前：原 13 检查通过，新增双类型检查失败 |
| R1-V2 | 同上 | 12:27:00～12:27:03；1 | 修复前：13 通过 / 2 失败，新增 JSON 凭据排除亦失败 |
| R1-V3 | 同上 | 12:27:40～12:27:46；0 | 最小修复后：15 通过 / 0 失败；发现尚重复的派生登录值，随后同批统一 |
| R1-V4 | 同上 | 12:29:04～12:29:10；0 | 最终三文件源码：17 通过 / 0 失败；包括旧配置及失败保存隔离 |
| R1-V5 | `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild` | 12:29:24～12:29:29；0 | 强制重建；8 警告 / 0 错误，见下方归属 |
| R1-V6 | `dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release` | 12:29:39～12:29:56；0 | 594 通过 / 0 失败；原 16 失败消失，断言不变 |
| R1-V7 | `dotnet run --project tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release` | 12:30:21～12:30:26；0 | 62 通过 / 0 失败；不是生产操作或 R2 环境专属门禁完整验收 |
| R1-V8 | `dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release` | 12:30:47～12:30:49；0 | 0 警告 / 0 错误；Headless 已在 V5 独立重建 |
| R1-V9 | `dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll`，`QQCHAT_IT_ONLY=s52` | 12:30:57～12:31:04；0 | 单场景 119 通过 / 0 失败；显式 `QQCHAT_BOT_DLL` 指向本次 Release DLL |
| R1-V10 | `dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release` | 12:31:31～12:31:33；1 | 独立源码复核：86 通过 / 6 失败；仍仅扫描 Headless 141 文件 / 36017 行，原六个失败点未变 |
| R1-V11 | `git diff --check` | 12:32:18；0 | 已跟踪补丁空白检查；未跟踪基类与文档另做显式校验 |

- 安全隔离：各诊断子进程清除 4.4 所列继承配置，仅向新 UUID 合成根设置两套数据路径，关闭文件日志；不查看实际秘密值。S52 的两个宿主进程均由场景重新设置合成路径及本地 mock 端点，凭据为未配置状态；未运行外部模式、生产接口或线上日志。
- 警告归属：强制重建显露 `Headless/Adapters/Net/HttpFetcher.cs:25,27,29,31,33,35,37,39` 的 8 个 CS0108；该文件内容与执行前一致，来自既有迁移输入，未擅自修复。B0 的 0 警告是增量构建记录，不能据此宣称此前源码无警告；R1 不新增警告，重复登录值的 2 个警告已消除。
- 架构棘轮、原 SafetyProbe、S52、ProductionSpecProbe 均未修改；没有降低阈值、删除断言或修改 CI 掩盖失败。R1-V10 仍阻塞波次准出。

### 5.4 兼容、回退与 00 结论

- 合成兼容：旧非秘密 JSON 字段名及值保留且各序列化一次；默认开关不变。旧策略按禁用交集迁移，保存后发布、SQLite 重载及 Headless 重启均通过。运行时平台凭据不会进入两种配置 JSON 或 SettingsStore，JSON 不能注入这些字段。
- 合成失败回退：保存前失败保持两个配置视图及原嵌套策略不变；现有事务失败回归仍通过。没有改变数据库 schema、删除既有秘密记录或进行生产迁移。
- 补丁回退证据：2026-10-08 12:32:18 +08:00，三个开始副本从会话压缩字节还原后的 SHA-256 均与 5.1 一致，原 13 项测试正文完整保留。需要回退时仅逆转这三个文件中的 R1 补丁和总纲的 R1 状态；若文件又有他人修改，先停止协调。未实际回退已通过源码、未做旧二进制 / 部署回滚演练，不能声称所有回退门禁已过。
- 00 核对：R1 定向修复验证通过，提交负责人核收；并非 Wave 1 验收。R1 交付时 R2 官方 Secret 面板写入 / 片段回显 / 仓储回填问题仍未改，当前处理结果见第六节；架构六项失败、Storage/Model 宿主迁移、契约冻结、CI 全矩阵、资源与回退门禁仍未满足。
- 未覆盖：其它 harness 场景（包括明确排除的 S42）、全矩阵、前端、真实官方 / 飞书协议、ZBA、SDK 8 CI、部署、生产资源及部署回滚。没有启动 01～07，没有提交、推送、发布或生产操作。
- R1 交付时待负责人：核收 R1；另批 4.6 的 R2（六文件）及 R3/R4。R2 / R3 随后已获批，见第六 / 七节；R4 仍须单独授权，不解锁 02/03 或 Web。

本次证据绑定的 SHA-256：

| 源码 / 产物 | SHA-256 |
| --- | --- |
| `src/BotAgent.Headless/Services/AppSettings.cs` | `AC59A571A97C37F202DE7AC5CA21DC0E0EFA040010729ED219882174B8171224` |
| `src/BotAgent.Platforms/PlatformOptions.cs` | `90E7A6AE0D94A93F5B7B6BBA457710A6B4BBCAE2E1A63315DACBB89C342D57AB` |
| `tests/BotAgent.SettingsScopeProbe/Program.cs` | `E6B00BE7CBF9AA0C36EDDC5B2D49A8F5EC4DAABE348261D7D271C437552299B1` |
| `src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll` | `2B464EBDBAAFCEE444B7548200C3C33A80F4F1B1367466BE08EB196FF1C3D820` |
| `tests/BotAgent.SettingsScopeProbe/bin/Release/net8.0/BotAgent.SettingsScopeProbe.dll` | `D29344C49C13E8BC0A435EC89C0844DC9E50B743C89746DB7D8B94F29C33DE47` |
| `tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll` | `45CCD02CFF9E83AE1423FF7C40D84649A523D497958F4C1DD78034B758BCAE21` |
| `tests/BotAgent.ProductionSpecProbe/bin/Release/net8.0/BotAgent.ProductionSpecProbe.dll` | `3F711166FE4227CE5E43EDA28A76598CA13BB79D1F03C18C7639128904AE4798` |
| `tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | `AB98D659E1AE1F2CCA8F9249112AD4F5BFE1506551E5B69D23CBBA033E9D741A` |

交付指纹核对（2026-10-08 12:34:58 +08:00，追加本段前的文档快照）：HEAD 保持 `1f3181e21e8d1f9531e41066b033d8d60218d580`；529 项安全清单只有本总纲及 R1 三文件发生任务变更，其余 525 项与开始一致；既有 292 条状态全部保留，仅新增 SettingsScopeProbe 的修改状态，共 293 条；暂存差异 SHA-256 仍为 4.1 的值。本快照清单摘要 `4A46844F0AF3B00BD8D3DC947F650AA37142B60880EC83B89C1EB1F559F19B13`，状态及对应内容摘要 `893EBADA4F6274CA66A8CF468A60FEF62CF3B3B2BA47A68A632EC6433C1C7F86`；不是干净提交或发布产物身份。12:34:59，四文件显式空白检查、总纲链接 / 表格 / 围栏检查通过，`git diff --check` 退出 0；本段追加后仅复核文档，代码证据不变。

## 六、R2 官方 Secret 红线修复（Agent-00）

### 6.1 授权、认领与准入

- 负责人对「R2 六文件写权及合成验证；环境变量专属、面板拒收且不回显；不删除旧秘密记录，不操作生产」明确答复「批准」。执行者、共享最终写入者及交付对象：Agent-00、Agent-00、负责人；不启动其它 Agent，不移交写权。
- 开始核对：2026-10-08 12:38:40 +08:00；HEAD 保持 `1f3181e21e8d1f9531e41066b033d8d60218d580`，293 条状态和暂存差异与 R1 交付一致；529 项清单除 R1 最后追加的文档段落外无变化，六个 R2 文件仍是原基线内容。R1 改动全部保留。
- 只认领以下六文件及本执行总纲；各原始字节压缩副本 / 哈希保存在会话中，不复制运行数据。共享文件由 00 自行最终写入；清单外需求先停并补批。

| 执行者 | 确切写入路径 | R2 开始 SHA-256 |
| --- | --- | --- |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Panel\WebUiServer.Settings.cs` | `57F36BE09FB8961BB0640A8B1BB28EBECEF9A06C19FCD0CF7F642AF2844174EC` |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\BotConfig.cs` | `B1E5982E95E6EFAAD720B11673A0E0557CAA93A05B00FD9076B9C33F158C28D6` |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Headless\wwwroot\app.js` | `19D962381A80482BDBB2F89F6597F5D89BAB05A6CA910307863A8950C547080F` |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Headless\wwwroot\index.html` | `BCB187B561F0C1A8308FDB61E8751205E8902BBAF549CBEF81718533AAF4E453` |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.IntegrationHarness\PrivacySettingsScenario.cs` | `48A57CC33079897BE994D8290C8E96673D98B521E597C2F828569D3087926F47` |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.FrontendProbe\probe.mjs` | `B1283612402587F2A4554E62430E31C7626456C497AE68AB81EFB7703D5BBA28` |

- 已批准的合成回归边界：真实 Headless 的 `GET/POST /api/settings`、重启 / 环境配置恢复、既有秘密仓储不变性、旧页面实际保存请求及安全状态显示。复用当前保存 / 发布服务，不新增 ZBA 路由或统一保存契约。
- 政策与兼容口径：官方 Secret 仅从 `QQCHAT_OFFICIAL_APP_SECRET` 的直接环境值读取，忽略旧仓储、设置 JSON 及 `QQCHAT_OFFICIAL_APP_SECRET_FILE`；面板提交 secret / clear 字段（包括空、null、false）拒绝整批请求，不忽略后继续保存。响应只允许 configured 布尔及 env/none 来源，不含值片段。此为已批准红线的兼容收紧，不自动配置生产环境或删除旧记录。
- R3 / R4、其它波次及 Web 仍冻结；不授权提交、推送、部署、发布、生产操作或真实数据验证。定向合成诊断不代表 Wave 1 验收。

### 6.2 实施、过渡契约与调用链

- `WebUiServer.Settings.cs`：JSON 对象中只要出现 `officialAppSecret` 或 `clearOfficialAppSecret`（不区分大小写、与值类型无关），在模型校验、审计事件构造、设置修改和持久化之前返回 HTTP 400 / `official_secret_environment_only`。整批拒绝，不把其它字段部分保存。删除官方 Secret 保存 / 清空及轮换审计分支。
- GET / 成功 POST 的现有 settings DTO：删除 `officialSecretMasked`；保留 `officialSecretConfigured: bool` 和 `officialSecretSource: env|none`，同一运行快照决定状态；不读取旧官方秘密仓储。不改变其它秘密政策，不修改统一保存 / 发布实现。
- `BotConfig.cs`：复用 `Str` 读取直接环境变量，空或空白值变为空配置；不使用支持文件读取的 `Secret`，不回填旧 settings 值或秘密仓储。不改数据库 schema，也不删除旧行。
- `app.js / index.html`：移除官方 Secret 输入、掩码占位及请求字段；增加只读状态和环境设置说明，使用固定文本节点，不显示值片段。旧客户端继续提交该字段（即便空）会收到 400，必须刷新新资源；这是批准的安全收紧，不承诺旧写入行为兼容。
- `PrivacySettingsScenario.cs`：S36 内增独立 R2 合成子场景，不改公共注册入口；复用现有 `StartBot`、HTTP helper 和 `MockOfficialGateway`。原 19 检查及原方法正文除子场景调用、两项环境隔离外保持一致；六次独立宿主启动、九类禁止请求、普通保存和真实静态资源均有验证。
- `FrontendProbe/probe.mjs`：保留原 350 检查，增输入 / 掩码缺席、实际请求无字段、三种状态只显示安全文案、全部保存请求无官方秘密字段，共 356 通过。最小 DOM / VM 测试，不冒充完整浏览器验收。
- 版本口径：R2 现有 settings API 的局部过渡契约，以上错误码、字段移除及来源枚举已在当前实现 / 合成测试固定；不是新 ZBA 版本或 01～06 消费者契约的全局冻结。
- 启动真实链路：`Program → CompositionRoot.BootstrapSettings → BotConfig.Load → ApplyInfrastructureEnvironment → Str`；`CompositionRoot.BuildChannelLayer → OfficialBotGateway → token POST`。本地 mock 捕获的合成请求只在测试内比对 Secret 相等性，输出计数 / 布尔，未打印协议正文或凭据。
- 管理真实链路：现有认证后的 `POST /api/settings → HandleSettingsSaveAsync`，禁止请求提前返回；普通请求仍走 `SettingsHotReload → SettingsBox.ApplyPersisted → SettingsStore.Save` 后发布。GET / POST 共用 `BuildSettingsPayload`。真实宿主 HTTP 返回新的内嵌脚本 / 页面，节点已只读；源文件或孤立程序集存在不是本批证据终点。

### 6.3 命令、时间和覆盖

环境同 4.4；仓库根 / pwsh，逐项检查原生命令退出码。表中时间均为 2026-10-08 / UTC+08:00；每个失败诊断单独结束，下游门禁始终冻结。

| 编号 | 命令 / 入口 | 时间 / 退出码 | 结果 |
| --- | --- | --- | --- |
| R2-V1 | `node tests/BotAgent.FrontendProbe/probe.mjs` | 12:40:23～12:40:31；0 | 修改前 350 / 0；当前基线，不复用历史成绩 |
| R2-V2 | 同 V1 | 12:42:18～12:42:26；1 | 新回归红灯：350 / 2，原检查通过 |
| R2-V3 | 同 V1 | 12:42:59～12:43:06；0 | UI 最小修复后 352 / 0 |
| R2-V4 | `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild` | 12:46:03～12:46:12；0 | 后端修复前强制重建，8 警告 / 0 错误 |
| R2-V5 | `dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release` | 12:46:13～12:46:16；0 | 2 警告 / 0 错误 |
| R2-V6 | `dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll`，S36 | 12:46:31～12:46:38；1 | 后端修复前：35 / 51；原 19 隐私检查通过，新增边界复现失败 |
| R2-V7 | 同 V4 | 12:47:34～12:47:37；0 | 最终宿主源码 / 前端资源强制重建，8 警告 / 0 错误 |
| R2-V8 | 同 V6，S36 | 12:47:38～12:47:46；0 | 初步修复 86 / 0 |
| R2-V9 | 同 V1 | 12:49:27～12:49:34；0 | 最终前端 / 探针 356 / 0 |
| R2-V10 | 同 V5 | 12:49:35～12:49:38；0 | 扩展空白、环境保存负例的 harness，2 警告 / 0 错误 |
| R2-V11 | 同 V6，S36 | 12:49:59～12:50:07；0 | 扩展负例 96 / 0 |
| R2-V12 | `dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release` | 12:50:48～12:51:05；0 | 594 / 0，源断言未改 |
| R2-V13 | `dotnet run --project tests/BotAgent.SettingsScopeProbe/BotAgent.SettingsScopeProbe.csproj -c Release` | 12:51:27～12:51:30；0 | 17 / 0，R1 及原回归仍通过 |
| R2-V14 | `dotnet run --project tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release` | 12:51:32～12:51:35；0 | 62 / 0；不是生产操作 |
| R2-V15 | 同 V6，S52 | 12:51:50～12:51:57；0 | 119 / 0，R1 实际宿主链复验 |
| R2-V16 | `dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release` | 12:51:58～12:52:00；1 | 86 / 6，原六个扫描盲区失败未变；141 文件 / 35999 行 |
| R2-V17 | 同 V5 | 12:53:18～12:53:22；0 | 最终 harness 增真实 HTTP 资源检查，2 警告 / 0 错误 |
| R2-V18 | 同 V6，S36 | 12:53:22～12:53:30；0 | 最终 98 / 0；含新的内嵌页面 / 脚本真实宿主证据 |
| R2-V19 | 同 V6，S52 | 12:53:31～12:53:38；0 | 最终 harness 119 / 0 |
| R2-V20 | `git diff --check` | 12:55:08；0 | 已跟踪补丁空白检查；文档单独复核 |

- 验证配置：pwsh 子进程先按名称清除继承的 BOTAGENT / QQCHAT / OPENAI / MINIMAX / TTS / HARNESS 配置及 HEALTH_PORT，不查看值。纯探针新 UUID 两套数据根一致；harness 父进程不设置可能覆盖场景的数据别名，S36 / S52 自行设置两套合成根。真实宿主的模型 / 官方 token / 网关全部固定到本地 mock；旧 JSON 的行为种子也显式固定 mock 地址，不依赖环境覆盖旧配置。
- harness 显式 `QQCHAT_IT_ONLY=s36` 或 `s52`，`QQCHAT_BOT_DLL=%WORKDIR%\bot-agent\src\BotAgent.Headless\bin\Release\net8.0\BotAgent.Headless.dll`；V7 独立强制重建在所有绿色集成运行前。未运行外部模式，没有访问真实协议、数据库或日志。
- 覆盖：旧仓储不回填且行哈希保留、仅文件不生效、空白直接环境值不回填、直接值去空白恢复、重启轮换、移除环境值后关闭；真实适配器仅把直接环境值交给本地 mock。九类 secret / clear 请求涵盖非空、空、null、对象、true/false、大小写变体；配置 JSON / 秘密行哈希、审计数量、运行时值均不变。普通保存保持有效、重启可恢复，环境凭据不被普通保存清空。
- 警告归属：Headless 的 8 个 CS0108 仍是未改的 HttpFetcher；harness 重新编译显露 `MockOpenAi.cs:439`、`AgentBridgeScenario.cs:417` 的 2 个 CS8602，两文件与开始一致。原增量构建没有显示这些警告，不代表原源码无警告。本批无新增警告，不修改清单外文件消警。
- 不覆盖：完整浏览器 / 缓存及滚动发布兼容、全矩阵和明确排除的 S42、真实官方消息协议互通、生产环境配置切换、ZBA、资源测量、部署回滚。局部官方取 token mock 证据不是 S42 已解锁或完整协议验收。

### 6.4 兼容、回退与 00 核对

- 有意收紧兼容：旧面板秘密 / `_FILE` 现在不参与运行配置，依赖它们的部署须由负责人先安排直接环境变量再批准切换；旧客户端发送被禁字段会整批失败，不能用静默忽略伪造保存成功。未替负责人读取、复制或配置任何真实 Secret。
- 保存兼容及失败证据：一般行为配置、平台开关、显示脱敏与 nameRaw 仍通过；九类拒收均无任何配置 / 秘密 / 审计写入或部分运行发布。独立合成旧数据库行在六个宿主启动后保持原哈希；不清理旧行、不变 schema。
- 回退边界：保留七文件开始字节副本；只可撤销明确属于 R2 的六文件补丁和总纲 R2 状态，不能撤销 R1 或既有迁移输入。官方 Secret 安全修复直接回退会重新引入旧行为，必须先报告风险并获得负责人明确决定；不能自动恢复旧秘密使用。未做源码实际回退、旧产物或生产部署回滚演练，不把预案当验收。
- 00 核对：R2 定向验证通过，提交负责人核收；R1 仍通过。只有六个认领文件及总纲发生本批变更，R1、其它 522 项安全清单及暂存区保持一致。无提交、推送、发布、部署或生产操作；其它 Agents 未启动。
- R2 交付时的波次结论：架构 86 / 6 仍阻塞，Core/Storage 统一接入及契约、Model 采用、其它波次、安全全矩阵、必选 ZBA、资源及回退门禁未齐。02 / 03 / Web 不解锁；当时 R3 / R4 未授权。R3 随后单独获批，当前结果见第七节；不将 R2 的历史建议当作现时授权。

证据绑定的源码及产物 SHA-256（2026-10-08 12:55:07 +08:00；后续仅更新总纲）：

| 源码 / 产物 | SHA-256 |
| --- | --- |
| `src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs` | `B5071F26A303C41152F8673E929A11736B9FF5A01995ABF8EB694ADA3F6253AB` |
| `src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs` | `49EF637419221B0FB257640CC9C2E2CF9441F99B1E222036E54A284837DB025E` |
| `src/BotAgent.Headless/wwwroot/app.js` | `E01AC150E7344CB772B843BB3FEF9B02AB5820D3FA78704C25919C53D4387554` |
| `src/BotAgent.Headless/wwwroot/index.html` | `25D898592CF92F870F4555A76DF01B47DB2EA9F69506D6EEEF6F425AD2B2C71B` |
| `tests/BotAgent.IntegrationHarness/PrivacySettingsScenario.cs` | `E16714FBA51E12FF80D537AC912466C9E528C74EF35CAB4899B5EB262770063C` |
| `tests/BotAgent.FrontendProbe/probe.mjs` | `1D3FE07B83DCE2467BA3BFA145C67612263C74B68B55780677374002B4869305` |
| `src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll` | `81965A61E5F3B1D11759F1692C69AECFF7FC7BA6C70E544AAD4675659CBD9948` |
| `tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | `405B14976291D4372332235AA0BE42C65D608F03523594AFB6B4C499991D8942` |
| `tests/BotAgent.SettingsScopeProbe/bin/Release/net8.0/BotAgent.SettingsScopeProbe.dll` | `7297056034B75F2523B74CD0F6BE6F29495E5DECAA8C409E19A28310B1900FB7` |
| `tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll` | `4A9583D4E8D03E1EEFF12F2E8B7D144DB678B0957CD4D127350E8980A2FB1FAB` |
| `tests/BotAgent.ProductionSpecProbe/bin/Release/net8.0/BotAgent.ProductionSpecProbe.dll` | `B0D48FF049CB5E63D54BB1572225483508648B8B2EBB3C6320F210BA74F94586` |

交付指纹核对（2026-10-08 12:59:28 +08:00，追加本段前的文档快照）：HEAD 未变；529 项安全清单仅 R2 六文件及总纲改变，其余 522 项（含 R1）与 R2 开始一致；293 条开始状态全部保留，仅新增六项修改状态，共 299 条；暂存差异保持 4.1 的 SHA-256。清单摘要 `6F4766992890B9422C547B6A8165EBF51B0EF9919B2EBA65EA642BB087E5BEED`，状态及对应文件内容摘要 `414B597FB7837CE854F90E34A1D88FD25315C0BBBF498E35AFFAF231677D2113`。测试完成后仅总纲发生登记变更，代码 / 测试源指纹不变。

文档检查：12:59:54 +08:00，总纲链接 / 表格 / 围栏 / 空白检查通过，`git diff --check` 退出 0。先前全文件空白扫描因前端探针已有尾空白报错；不删除既有内容换绿，随后用开始内容 / HEAD 对照确认该项未变，补丁新增 / 修改行仍由 git diff 检查。本段追加后仅复核文档，不将文档通过冒充源码验收。

## 七、R3 等价架构护栏修复（Agent-00）

### 7.1 授权、认领与准入

- 负责人对本聊天的「R3 四文件 + 执行总纲写权及定向合成验证；清单外业务修复另批」明确答复「批准」，中断后答复「继续」。执行者 / 共享最终写入者 / 交付对象：Agent-00 / Agent-00 / 负责人；01～07 无本批写权，不启动其它 Agent，不移交旧文件。
- 中断前仅保存会话快照及运行架构基线，未写文件。恢复时会话工具临时存储不可用，已重新保存五文件的开始字节压缩副本；2026-10-08 18:01:14 +08:00 核对：五文件哈希均与中断前一致，HEAD 仍为 `1f3181e21e8d1f9531e41066b033d8d60218d580`；299 条状态，暂存差异摘要仍为 4.1 的值。509 项安全清单口径与历史 529 项不同，不直接比较；后续用本批同口径逐项核对。
- 只认领下表五文件；保留全部既有改动，不暂存。验证可生成相应 bin/obj 与新 UUID 合成临时目录；不授权业务源码、工程引用、Host、公共测试入口、CI 或部署修复。

| 执行者 | 确切写入路径 | 开始 SHA-256 |
| --- | --- | --- |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.ArchitectureProbe\SourceIndex.cs` | `544085A66C9F079A051C642D9337B549BF27A0C2492FB219424FCBF8B80A2851` |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.ArchitectureProbe\Program.cs` | `23D20D86B1C1A792364A46F7318A9D7EF69AC9C06F0842549D0D902CE7AEFD9D` |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.ArchitectureProbe\Baseline.cs` | `5CD5780785097C0E96B736800E37AABC4E5C6B260C9CB1F64BEBF4390815A667` |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.ArchitectureProbe\Metrics.cs` | `2F900029598F455D1C3CA5A576F83B34DAF5BD43F85CCE0E4E6429DECC4E0E58` |
| 00 | `%WORKDIR%\bot-agent\docs\plans\agents\00-orchestration-and-dependency-graph.md` | `92FB727CDCE5ECFCC44ADA883A9DFBF76C0DF823242E30737275B09EC602C720` |

### 7.2 本批护栏契约与停止边界

- 本批内部契约标识 `R3-source-guard-v1`：工程身份 + 仓库相对路径定位；当前五个业务工程及 Core 领域扫描必须非空；实际项目声明参与依赖检查，不能把声明检查当作 MSBuild 完整求值或宿主采用证据。未知工程、无法解释的引用声明及空扫描 fail-closed。
- 迁出锚点随真实 Core 路径迁移；旧领域、用例、面板约束继续检查。所有源码均计数，包括尚未被宿主采用或重复的实现；不按内容去重。partial 类型按工程、命名空间及类型身份合并，不跨工程误合并。
- 棘轮数值只允许不变或调低；允许位置只能有逐文件、等价迁移依据，不能给新工程整目录豁免。新增发现的重复时钟、仓储、平台写 SQL 等业务违规保留红灯并另批，不能为了四文件验收扩大业务写权。
- 此为负责人批准范围内的护栏契约，不代表 01 与消费者、06 的统一保存 / 管理 / ZBA 契约已评审冻结。R3 当批不授权波次、R4、Web、提交、推送、发布、部署或生产操作；后续 R4 独立授权见第八节。

### 7.3 执行记录（持续追加）

| 编号 | 命令（仓库根 / pwsh） | 时间（2026-10-08 / UTC+08:00）/ 退出码 | 结果 |
| --- | --- | --- | --- |
| R3-V1 | `dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release` | 17:55:27～17:55:29；1 | 修复前重新复现 86 / 6；Headless 141 文件 / 35999 行，Domain 空扫描；仅本地源码检查 |

V1 失败结束该项，不作为波次准出。R3 内部合成负例及定向复验获批，但原架构门禁和下游冻结不豁免。

### 7.4 实施、真实调用链与兼容

- `SourceIndex.cs`：快照根改为仓库，源码路径含工程身份；扫描实际业务工程，不按内容去重。核对当前五个必需工程、非空源码定义、非空 Core Domain、解决方案列项和直接项目引用。未知工程、悬空 / 越界 / 条件引用、未解释的引用元数据、显式编译输入 / Import / 仓库内继承构建设置均拒绝。除 bin/obj 外的源码树重解析点拒绝，既不跟随到树外，也不静默漏扫。显式无效源码根不再回退到另一份 checkout。
- `Program.cs`：保留原 92 个检查的职责，迁移 ToolSpec、ToolPolicy、ToolGate、TurnTrace 的确切 Core 路径；全工程扫描使工具提示词段和 LocalBase 可达。Core 全工程及其它工程 Domain 纳入领域护栏，Services 和面板视图保留旧规则；默认执行扫描器合成自检与项目声明检查。新增 `--self-test` 仅验收扫描器契约，输出明确不是仓库架构准出；`--print` 等原诊断仍不是验收命令。
- `Metrics.cs`：保留 SQL / 文件 IO / 时钟 / HttpClient / 配置赋值的原计数表达式；成员块继续用原行数口径。增加字符位置定位，修正同文件同名 / 嵌套类型计数及 partial 身份；合并按工程 + 命名空间 + 外围类型 + 泛型元数，不跨程序集误合并。合成负例调用真实领域断言，证明拆 partial 文件不能绕过字段上限。
- `Baseline.cs`：20 个原数值阈值全部不变。路径改为仓库相对身份；HTTP 协议例外仅从旧 OneBot 文件迁到真实 Platforms 文件。21 个 Storage 持久化文件及迁出的 OfficialIdMap 逐文件登记旧角色映射，不给 Storage / Platforms 整目录豁免，不豁免重复实现的全局计数，也不豁免 PlatformDedupStore 或三份 DefaultClock。
- 护栏实际链路：现有 ArchitectureProbe 工程 / `Program.Main → SourceIndex.Load → 项目与解决方案校验 → ScannerContractTests / ProjectChecks → 原棘轮、布局及红线断言`。没有新建工程或修改公共 CI 入口；现有 CI 本来调用默认命令，默认命令现会检查全部五工程。没有声称 CI 已实际运行通过。
- 产品实际链路没有改动：S21 使用真实重建 Headless 的旧模型调用 / 保存 / 重启链；S36 使用真实 settings HTTP 保存 / 拒收 / 重启与内嵌资源链；S47 走真实有限步进工具循环；S52 走真实本地平台入站、策略、模型 mock、outbox 与重启。这些结果不是独立 Storage / Model 已被宿主采用的证据。
- 接口版本：`R3-source-guard-v1` 仅是本批内部探针契约。消费者的统一保存 / 管理 / 时钟注入 / ZBA 契约仍在 4.3 队列，未替 01、06 或负责人签署业务冻结。显式拒绝先前会静默漏扫的源码树，是护栏兼容收紧，不改变数据库 schema、设置或秘密运行语义。

### 7.5 当前命令、覆盖与失败归属

全部在唯一仓库根使用 pwsh，SDK `10.0.101`、目标 `net8.0`；本地结果不等于 SDK 8 的 CI 或生产验收。每个原生命令单独核对退出码；架构失败结束该项，下游继续冻结。下面的安全 / 行为项目是本次明确批准的独立定向复验，不作为绕过架构门禁的下游施工。

| 编号 | 命令 | 时间（2026-10-08 / UTC+08:00）/ 退出码 | 结果 |
| --- | --- | --- | --- |
| R3-V2 | 同 V1，附 `-- --self-test` | 18:07:14～18:07:16；1 | 先补负例，修复前 0 / 13，证实原扫描及项目门禁缺口 |
| R3-V3 | 同 V2 | 18:12:22～18:12:25；0 | 初步恢复后扫描器自检 13 / 0 |
| R3-V4 | 同 V1 | 18:12:47～18:12:49；1 | 五工程 300 文件 / 58020 行，102 / 6；尚未登记逐文件持久化位置映射 |
| R3-V5 | 同 V2 | 18:19:32～18:19:36；0 | 扩展身份、类型及真实领域负例，自检 29 / 0 |
| R3-V6 | 同 V1 | 18:21:11～18:21:12；1 | 等价位置映射后 118 / 6；六项已是实际业务违规而非原路径盲区 |
| R3-V7 | 同 V1 | 18:23:32～18:23:36；1 | 最终四文件源码 118 / 6；包含全部 29 项扫描器自检；300 文件 / 58020 行，Core 94 文件；无架构探针新增警告 |
| R3-V8 | `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild` | 18:23:38～18:23:45；0 | 真实宿主独立强制重建，8 既有警告 / 0 错误 |
| R3-V9 | `dotnet build BotAgent.slnx -c Release` | 18:24:47～18:24:50；0 | 0 警告 / 0 错误；不能替代未列入 solution 的专项探针或宿主采用证据 |
| R3-V10 | `dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release` | 18:24:51～18:25:07；0 | 594 / 0；源断言未改 |
| R3-V11 | `dotnet run --project tests/BotAgent.SettingsScopeProbe/BotAgent.SettingsScopeProbe.csproj -c Release` | 18:25:08～18:25:11；0 | 摘要过滤未保留 PASS 明细，随后 V13 单独复跑，不从历史推断检查数量 |
| R3-V12 | `dotnet run --project tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release` | 18:25:12～18:25:15；0 | 62 / 0；不是生产操作 |
| R3-V13 | 同 V11 | 18:26:24～18:26:26；0 | 保留全部检查明细，17 PASS / failures=0 |
| R3-V14 | `dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release -t:Rebuild` | 18:26:27～18:26:30；0 | 2 既有警告 / 0 错误；没有以它替代 V8 |
| R3-V15 | `dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll`，S21 | 18:26:32～18:26:39；0 | 37 / 0 |
| R3-V16 | 同 V15，S36 | 18:26:40～18:26:47；0 | 98 / 0；R2 边界与真实 HTTP 保存 / 页面资源仍通过 |
| R3-V17 | 同 V15，S47 | 18:26:49～18:26:51；0 | 8 / 0；有限步进工具循环 |
| R3-V18 | 同 V15，S52 | 18:26:52～18:26:59；0 | 119 / 0；真实本地平台调用链，非官方完整协议验收 |
| R3-V19 | `dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll`，显式无效合成源码根 | 18:31:06；1（负例预期） | 真实 CLI 返回 `source_guard:source_root_missing`，不回退真实 checkout 假绿 |
| R3-V20 | `dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll --self-test`，绑定唯一仓库根 | 18:36:32～18:36:33；0 | 最终已编译探针扫描器契约 29 / 0；不是仓库架构通过 |
| R3-V21 | `git diff --check`；总纲本地链接 / 表格 / 围栏 / 空白及源码产物哈希核对 | 18:41:32～18:41:33；0 | 文档检查通过；四源码及六 DLL 与 7.7 全部一致；不是新增产品测试 |

- 安全隔离：每个验证 pwsh 子进程只按名称清除继承的 BOTAGENT / QQCHAT / OPENAI / MINIMAX / TTS / HARNESS 配置及 HEALTH_PORT，不输出实际值。纯探针设置新 UUID 的两套合成根并关闭文件日志；harness 父进程不设置可能压过场景根的别名，显式选择一个场景并绑定本仓库 Release Headless DLL。S21 / S47 自建 QQCHAT 合成根且父进程 BOTAGENT 别名已清除；S36 / S52 自行设置两套根。本地 mock 与合成配置恢复沿用已核实的真实场景，没有外部模式、真实协议、数据库或日志访问。
- 警告归属：V8 的 8 个 CS0108 均在未改的 HttpFetcher；V14 的 2 个 CS8602 在未改的 MockOpenAi / AgentBridgeScenario。它们与本批开始哈希一致，不在清单外消警。V9 增量的 0 警告不能抹掉强制重建显露的历史警告。
- 等价性证据：原 Headless 范围的 SQL=137、IO=58、系统时钟=3、HttpClient=3，与 V1 一致；新增计数来自迁出 / 重复模块，不改变原计数表达式。原 92 个护栏职责保留，增加 29 个合成自检及 3 个项目检查；最终 124 项检查中 118 通过、6 失败。先前六个锚点 / 路径失败全部消失，不能把当前也有六项失败说成「仍是原六项」。

| 当前失败 | 数量 / 阈值或位置 | 归属与停止处理 |
| --- | --- | --- |
| SQL 位置越界 | PlatformDedupStore 3 处 | 既有 Platforms 实现；01/02/06 评审单一写入端口，00 合入；本批不改 |
| SQL 全局总量 | 268 > 137（Headless 137 + Storage 128 + Platforms 3） | 新旧存储重复及平台自写库暴露；先宿主接入与兼容验收，再逐文件处理旧实现；不去重或抬阈值 |
| 文件 IO 位置越界 | PlatformDedupStore 1 处 | 不因迁到 Platforms 自动获得持久化豁免；另批 |
| 文件 IO 全局总量 | 99 > 62 | 重复仓储仍全部计数；迁出的 OfficialIdMap 仍有 4 处，不能靠删除旧目录覆盖问题 |
| 系统时间位置越界 | Model/Clock、Platforms/Common/Clock、Storage/Clock 各 3 处 | 01/02/03 与 06 确认现有 IClock 注入 / 组装契约及测试；清单外业务修复另批 |
| 系统时间全局总量 | 12 > 3 | 三份额外 DefaultClock 保留红灯；不能扩时钟白名单或按工程各给 3 的预算 |

未覆盖：完整 CI / harness 矩阵、明确排除的 S42、真实官方 / 飞书互通、Windows 源码符号链接的实际创建演练、完整 MSBuild / NuGet 隐式导入求值、ZBA、独立 Web、生产配置、资源测量及部署回退。声明图检查不是完整编译依赖证明；模块源码存在不等于宿主采用。旧前端未改，未另跑浏览器或前端探针；S36 资源检查不是完整浏览器验收。

### 7.6 回退与 00 核对结论

- 四文件原本与 HEAD 一致；保存五文件开始字节压缩副本，仅供本批片段逆转。源码回退只能逆转 R3 四文件差异，文档只撤销 R3 登记及本批状态修订；有后续修改先协调，不强制 checkout、hard reset、清理未跟踪文件或回退 R1/R2。
- 本批不改变配置 / 数据 schema，不删除仓储或秘密行；行为兼容由上述真实合成宿主链复验。回退探针会恢复扫描盲区，不能把回退到 86 / 6 当成架构通过。未实际回退当前源码或进行旧产物 / 生产演练；只验证开始副本可恢复，不把预案写成部署回退验收。
- 00 交付结论：R3 护栏恢复补丁与定向回归已交付，待负责人核收；仓库架构验收仍失败，Wave 1 未准出，02 / 03 / Web 不解锁。未启动其它 Agent、提交、推送、发布、部署或生产操作。
- 待批准事项：核收本批护栏补丁；另行评审并授权重复存储接入 / 旧文件交接、平台去重写入与统一时钟的确切业务路径。不能将本批授权延伸为这些业务修复或 R4 写权。

### 7.7 交付指纹与回退副本核对

- 2026-10-08 18:38:44～18:38:46 +08:00 核对（追加本段前的文档快照）：HEAD 未变；509 项同口径安全清单仅 R3 四文件及总纲改变，其余 504 项（含 R1/R2）全部与本批开始一致。299 条既有状态全部保留，仅新增四个护栏文件的修改状态，共 303 条；暂存差异摘要仍为 `65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC`。没有干净提交或发布产物身份。
- 本文档快照的清单摘要 `8A82C8A378AB24250FCD63ED74C7E776F3F6DF3BA18C3855CA5EEE56D43072A9`，状态及安全文件内容摘要 `E6E17FF38EFA7D3AE5FD2448890FF653D542BA8DB425DB868DDA9C750D2E32B6`；摘要算法同 4.1，但安全清单采用本批 509 项口径。随后仅追加文档登记，代码 / 测试源及下表二进制不变。
- 18:36:33 之后在内存逐份解压五个开始副本，SHA-256 均与 7.1 一致；未覆盖工作区文件、未实际回退产品或清理旧产物。后续若有他人修改，不得直接用开始副本覆盖整文件。
- Headless、harness、SafetyProbe、SettingsScopeProbe、ProductionSpecProbe 的本次二进制哈希还与 R2 交付值一致；它们是本轮重新执行的结果，不能因为哈希一致省略本轮命令记录，也不证明独立 Storage / Model 已采用。

| 最终源码 / 产物 | SHA-256 |
| --- | --- |
| `tests/BotAgent.ArchitectureProbe/SourceIndex.cs` | `803663A9DD87C9AC422FA6E6D05FDF0317A906071BE8CF0673D418268D906FF3` |
| `tests/BotAgent.ArchitectureProbe/Program.cs` | `6AD0159DAC07A5B411114D304249BAC8BB8ABDEE63A3F5348E5DEE1DAA3F9407` |
| `tests/BotAgent.ArchitectureProbe/Baseline.cs` | `E6944A4194645B79E5FFDF62ABC410002F7272CB758FC2512916CD713D9AEA73` |
| `tests/BotAgent.ArchitectureProbe/Metrics.cs` | `6CE5838E6D3F8C2703E986BE139FE47C473B04DB341F4FDC7D001B0F8B96DE20` |
| `tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll` | `820223104B113B4B9DF4C24BEF6CF3EE3CED77E7587A741684977BB7F64505FF` |
| `src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll` | `81965A61E5F3B1D11759F1692C69AECFF7FC7BA6C70E544AAD4675659CBD9948` |
| `tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | `405B14976291D4372332235AA0BE42C65D608F03523594AFB6B4C499991D8942` |
| `tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll` | `4A9583D4E8D03E1EEFF12F2E8B7D144DB678B0957CD4D127350E8980A2FB1FAB` |
| `tests/BotAgent.SettingsScopeProbe/bin/Release/net8.0/BotAgent.SettingsScopeProbe.dll` | `7297056034B75F2523B74CD0F6BE6F29495E5DECAA8C409E19A28310B1900FB7` |
| `tests/BotAgent.ProductionSpecProbe/bin/Release/net8.0/BotAgent.ProductionSpecProbe.dll` | `B0D48FF049CB5E63D54BB1572225483508648B8B2EBB3C6320F210BA74F94586` |

最后文档登记后只复核文档、工作区边界及指纹，不再改源码或复用旧产物冒充新验收。7.7 摘要标记的是 18:38 的文档快照，不是此最终追加版本的自引用哈希；所有源码 / 产物绑定仍有效。

## 八、R4 测试与构建入口修复（Agent-00）

### 8.1 授权、认领与准入

- 负责人对本聊天的「五文件写权 + 定向合成验证 + 本地 Docker 构建」明确答复「批准」。执行者 / 共享最终写入者 / 交付对象：Agent-00 / Agent-00 / 负责人；不启动其它 Agent、不移交写权、不授权提交、推送、发布、部署或生产操作。
- 2026-10-08 18:57:36 +08:00 准入：HEAD `1f3181e21e8d1f9531e41066b033d8d60218d580`；303 条状态（暂存删除 20、未暂存删除 92、未暂存修改 17、未跟踪 174），暂存区与 R3 交付一致。509 项安全清单全部与 R4 只读核对一致；不把既有迁移及 R1～R3 改动记作 R4 成果。
- 四个实施文件开始无既有改动，总纲已有未跟踪登记；五文件开始字节压缩副本及哈希保存在本会话，仅供明确属于 R4 的片段回退。验证可生成相应 bin/obj、本批 UUID 合成临时目录和本地唯一标记构建产物；不清理其它产物。

| 执行者 | 确切写入路径 | 开始 SHA-256 |
| --- | --- | --- |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.ReverseTransportProbe\BotAgent.ReverseTransportProbe.csproj` | `2B6EA30C73E5B5534749B0DC631201E4042DB5EEAE5515B73845610244B4CDF5` |
| 00 | `%WORKDIR%\bot-agent\.github\workflows\ci.yml` | `0EBA96B371DAC8E07D3CD6841C45CC0A8E0E982FF6C9A86BCBC8E41A5D10F566` |
| 00 | `%WORKDIR%\bot-agent\CONTRIBUTING.md` | `49B5B08DA73F6ED694B92AED548F813CE7B3DB4A4A6C67B7B9B0EA7A16471C87` |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Headless\Dockerfile` | `93E492B4E0FC44788C81CA44B584E7EDB1B73252197125934463C567B15BCB7C` |
| 00 | `%WORKDIR%\bot-agent\docs\plans\agents\00-orchestration-and-dependency-graph.md` | `0BED989FADD80CDC474171BCF367AFA0EF83E3050F12DC7BEBF7AF91D5E9B1A2` |

### 8.2 范围、接口与停止边界

- 本批入口契约 `R4-entry-v1`：ReverseTransportProbe 保持两份真实传输源码链接及原断言；CI 场景集合对应 harness 实际注册项，仅补 S52，不接回 S42；Docker 仅补 Headless 实际 Core / Platforms 依赖及两个已声明的嵌入脚本，不借机接入 Storage / Model。
- 两份迁出传输与 HEAD 旧源码逐行等价，命名空间及签名不变。宿主实际路径为 `CompositionRoot.BuildChannelLayer → OneBotGateway.Start / CreateTransport → ReverseWsTransport.StartAsync`；独立探针不等于整链路，必须分别提交重建后的 S1 / S36 / S52 合成行为证据。
- 不修改业务源码、探针断言、公共 harness 入口、工程依赖、架构阈值、隐私或 NuGet 审计门禁。清单外修复、接口变动或新失败归属不明时停止受影响项并补批。
- R3 六项架构违规保留红灯；获批独立定向复验不是 Wave 1 准出，02 / 03 / Web 仍冻结。CI 本地声明核对不能声称 GitHub 已执行通过；本地镜像构建不能声称生产、资源或回退门禁已通过。
- Docker 先核对本地引擎边界，再用白名单源码与资源组成的本批临时上下文构建；不把整个工作区作为验证上下文、不包含数据 / 日志 / 环境秘密 / 未筛选产物，不启动业务容器或执行部署。引擎不可用或不是本机端点则停止容器验证、登记缺失证据，不擅自安装或改 Docker 配置。

### 8.3 执行记录

环境：唯一仓库根 / pwsh，本机 SDK `10.0.101`、目标 `net8.0`；本节本地结果不冒充 Linux SDK 8 / GitHub CI，后续真实 Linux SDK 8 镜像证据单列于 8.7。下表时间均为 2026-10-08 / UTC+08:00；每个原生命令核对退出码。失败结束受影响项目，独立获批验证不解锁下游。

| 编号 | 真实命令 / 对象 | 时间 / 退出码 | 结果 |
| --- | --- | --- | --- |
| R4-V1 | `dotnet build tests/BotAgent.ReverseTransportProbe/BotAgent.ReverseTransportProbe.csproj -c Release -t:Rebuild` | 18:59:09～18:59:16；1 | 修复前复现两项 CS2001，确为两条已删除源码链接；0 警告 |
| R4-D1 | 清除进程 DOCKER_HOST / DOCKER_CONTEXT 覆盖后，`docker context inspect --format '{{json .Endpoints.docker.Host}}'` | 18:59:17～18:59:18；0 | 验证端点为本机 named pipe，未输出具体端点；未改用户配置 |
| R4-D2 | `docker version --format 'client={{.Client.Version}};server={{if .Server}}{{.Server.Version}}{{end}}'` | 18:59:17～18:59:18；1 | CLI 29.7.2，本机引擎不可用；停止 Docker 构建，不启动服务、改配置、转远端或声称镜像通过 |
| R4-V2 | 同 V1，最终工程链接 | 19:01:08～19:01:11；0 | 0 警告 / 0 错误 |
| R4-V3 | `dotnet tests/BotAgent.ReverseTransportProbe/bin/Release/net8.0/BotAgent.ReverseTransportProbe.dll` | 19:01:28～19:01:38；0 | 原探针 13 PASS / failures=0；未改 Program 或断言 |
| R4-V4 | `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild` | 19:01:39～19:01:44；0 | 真实宿主独立重建，8 既有 CS0108 警告 / 0 错误 |
| R4-V5 | `dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release -t:Rebuild` | 19:01:56～19:01:59；0 | 2 既有 CS8602 警告 / 0 错误；不替代 V4 |
| R4-V6 | `dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release` | 19:02:23～19:02:25；1 | 118 / 6；扫描器 29 项自检仍在默认链；R3 业务红灯未放宽 |
| R4-V7 | `dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release` | 19:02:27～19:02:44；0 | 594 / 0；安全断言未改 |
| R4-V8 | `dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll`，S1 | 19:03:14～19:03:42；0 | 33 / 0；包含 S1 内的 S2 分支，真实反向 WS 宿主链路 |
| R4-V9 | 同 V8，S36 | 19:04:54～19:05:01；0 | 98 / 0；真实 settings HTTP 保存 / 拒收 / 重启、内嵌资源链 |
| R4-V10 | 同 V8，S52 | 19:05:02～19:05:09；0 | 119 / 0；真实平台策略、入站、mock 模型、outbox 与重启 |
| R4-V11 | pwsh here-string 经 `python -`，现有 PyYAML / XML 声明检查 | 19:04:26～19:04:27；0 | YAML 解析及重复键拒绝；50 注册项 = 50 矩阵项，缺失 / 额外 / 重复均 0，三组 16 / 14 / 20；CI 只改两个批准值，verify / actions / 审计不变；项目链接、文档入口及 Docker 静态闭包通过 |
| R4-V12 | `dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll`，窄失败摘要 | 19:05:11；1 | 补齐 V6 摘要未完整展示的六项明细，与 R3 相同，见下文 |
| R4-V13a | 本批白名单上下文中的 `dotnet restore src/BotAgent.Headless/BotAgent.Headless.csproj` | 19:06:34～19:06:44；0 | 274 个允许文件，未复制工作区数据 / 日志 / 秘密；真实三工程还原，不关闭审计 |
| R4-V13b | 同上下文 `dotnet publish src/BotAgent.Headless/BotAgent.Headless.csproj -c Release --no-restore -o <R4Context>/r4-publish /p:UseAppHost=false` | 19:06:44～19:06:48；0 | Windows 本地 SDK 发布成功，8 既有警告；不是 Linux 镜像、发布到远端或生产操作 |
| R4-V14 | 发布资源 / DLL 哈希核对；首次 PowerShell 行管道 `git diff --binary` → `git apply --reverse --check -` | 精确时刻未保留（V13 后、V15 前）；1 | 三个发布程序集、7 个 web 资源、2 个脚本及资源内容哈希均通过；逆向检查因管道换行传输失败，未实际回退，不能将该项记为通过 |
| R4-V15 | 原生命令 stdout 原样输送 `git diff --binary` → `git apply --reverse --check -`；`git diff --check` | 19:08:56；各 0 | 四文件逆向补丁适用性通过；原补丁 CR=0，不放宽空白检查，不改 / 恢复文件 |

V16 的换行诊断和最终文档 / 指纹核对见 8.6；不将验证工具问题混入产品行为计数。

- 隔离：每个产品验证 pwsh 进程仅按名称清除 BOTAGENT / QQCHAT / OPENAI / MINIMAX / TTS / HARNESS 及 HEALTH_PORT 继承值，不输出秘密值；Safety 设置本批 UUID 的两套数据根及 QQCHAT_LOG_FILE=0。harness 父进程不设置覆盖场景的 data 别名，QQCHAT_IT_ONLY 单场景、QQCHAT_BOT_DLL 显式绑定本仓库刚重建 DLL；不使用外部模式。所有请求、存储及重启只涉及场景合成数据与本机 mock。
- 原断言覆盖：反向 WS 的静默 / 部分握手绝对超时、后到合法客户端、未授权 / 过大头、认证替换、旧连接清理不移除新路由、并发握手有界和停机关闭；未调整超时或删断言换绿。S1 进一步证明宿主实际采用同一 Platforms 源码，不仅独立探针编译。
- 架构失败归属：PlatformDedupStore 的 SQL 位置 3 处与 IO 位置 1 处；全局 SQL 268 > 137、IO 99 > 62；Model / Platforms / Storage 三份 Clock 各 3 处越界，全局系统时间 12 > 3。与 R3 相同，非 R4 新回归；仍另批业务修复、冻结 Wave 1 及下游。
- 警告归属：V4 / V13 的 8 个 CS0108 在未改的 HttpFetcher；V5 的 2 个 CS8602 在未改的 MockOpenAi / AgentBridgeScenario。它们保留输入指纹，未清单外消警。
- 截至 8.6 未覆盖：Docker 引擎与完整 Linux 镜像构建、actionlint / hadolint（未安装，不擅自安装）、GitHub CI、完整 harness / 专项探针矩阵、S42、真实官方 / 飞书互通、ZBA / 独立 Web、生产配置、资源测量、数据 / 部署回退。后续仅本机引擎与镜像构建已在 8.7 补齐；其它未覆盖项保留，PyYAML / 静态 COPY 检查和 Windows publish 不替代它们。

### 8.4 实施与兼容证据

- ReverseTransportProbe 只将两个 Compile Include 迁到 Platforms；Link 名、签名、命名空间及原 13 项断言保留，未创建平行传输或拉入应用数据初始化。Headless 已实际引用 Platforms；`OneBotGateway.Start → CreateTransport → ReverseWsTransport.StartAsync` 的生产源码路径由静态核对及重建 S1 共同证明。
- CI 仅给第三矩阵组追加 s52 和更新组名；原 49 场景、S42 排除、needs: verify、动作版本、restore、审计、安全及架构命令不变。R3 架构失败仍会阻断 integration，不能把矩阵声明补齐称为实际 CI 全绿。
- CONTRIBUTING 补齐已有五个专项命令、50 场景范围、S2 / S42 排除、S52 边界、pwsh 退出码 / 合成隔离、Headless 重建与源码镜像边界；不扩充功能或授权。
- Dockerfile 先复制 Headless / Core / Platforms csproj 还原，再复制三工程源码及确切两个嵌入脚本；不用 COPY 整个仓库，不添加 Storage / Model 宿主引用。不改 runtime、端口、用户、健康检查、Dockerfile.app、.dockerignore、部署配置或生产路线。
- 本地上下文身份：`<Temp>/botagent-r4-context-aca5984bb42c41cd80fb3e7431f346d4`，初始 274 项清单 SHA-256 `A1D3BB2D8696828840313F7E81D1D94FDAB298C7A560F982AAC7D50F8474B38D`。清单仅三工程 260 个 C# + 3 个 csproj、7 个静态资源、2 个源码脚本、Dockerfile 和既有 .dockerignore；全部逐项核对复制哈希、仓库边界及重解析点，不含运行数据。后续 bin/obj 和发布目录是此 UUID 内的派生验证产物；若恢复 Docker 验证，须重新构造干净白名单上下文，不直接把派生产物作为初始上下文。
- 本批接口版本 `R4-entry-v1` 仅描述入口修复；统一保存 / 管理 / 时钟 / ZBA 业务契约仍未冻结。无配置或数据 schema 改动，官方 Secret 仍只从环境读取，显示脱敏 / key / nameRaw 语义未改。

### 8.5 回退、停止与交付结论

- 四实施文件开始与 HEAD 一致，逆向补丁已做 check-only；总纲只撤销 R4 新增及本批状态修订，保留 R1～R3 和既有登记。五个开始 gzip 副本经分块临时文件及内存解压核对 SHA-256 全部一致，未恢复工作区或操作生产。先前副本校验遇到命令行长度与非交互 stdin 限制，属于验证工具传输问题；没有代码 / 数据变更，最终副本完整性检查 5 / 0。不使用强制 checkout、hard reset 或清理未跟踪文件；后续并行变更先协调。
- 回退 R4 工程链接会重新引入两项 CS2001、撤回 CI 的 S52 声明及 Docker 输入补齐；不能把回退称为系统恢复验收。配置 / 数据兼容只由本批合成宿主链证明，未做旧二进制 / 新数据或部署演练。
- 00 结论：R4 五文件入口补丁、约定定向合成证据及后续真实 Linux 镜像构建 / 镜像资源证据已交付，待负责人核收；先前 Docker 阻塞已由 8.7 补验解除。不是仓库整体验收通过：架构仍失败，Wave 1 未准出、02 / 03 / Web 不解锁；未创建执行 Agent、提交、推送、发布、部署或生产操作。
- 负责人已自行开启 Docker Desktop，00 仅在原批准范围内补验，不代为启动服务或改配置。当前等待核收 R4 交付；实际 CI / 全矩阵、清单外业务修复、资源或部署回退须另行安排 / 授权，不能将本批批准延伸为波次开工。

### 8.6 最终指纹与文档核对

- 19:13:57 +08:00，R4-V16：`git diff --binary` 的 PowerShell 行管道经 `python` 检查原始 stdin 形状，退出 0；CR=103 / LF=103，而 V15 原生 stdout 的 CR=0。确认 V14 逆向检查失败来自管道引入 CRLF；修正只保留原始字节传输，不删断言、不放宽空白选项，V15 的 check-only 已通过。工具脚本拼接的语法错误发生在 shell 调用前、没有文件操作；不记为项目命令或产品回归。
- 19:11:32 +08:00 补核 CI 16 个实际 csproj 的明确 Compile / ProjectReference / EmbeddedResource 输入，静态缺失 0；1 个资源通配符由 V13 / V14 的真实发布与 9 资源哈希核对覆盖。这不是其它专项探针全部执行或完整 MSBuild 求值。
- 19:15:24 +08:00，R4-V17：`git diff --check` 退出 0；总纲和 CONTRIBUTING 的本地链接、表格列数、围栏及尾空白检查通过。反向探针 Program、两份 Platforms 传输、三份历史警告来源哈希均与开始一致，原断言和输入源码未改。
- 19:15:28 +08:00 工作区核对：HEAD 与开始一致，303 条开始状态全部保留，仅新增四个批准实施文件的修改状态，共 307 条（暂存删除 20、未暂存删除 92、未暂存修改 21、未跟踪 174）。暂存区 raw 差异与开始完全一致；未暂存或整理既有输入。509 项同口径安全清单仅本批五文件变化，其余 504 项（含 R1～R3）全部不变。
- 2026-10-08T19:16:27.1422042+08:00 摘要（本段追加前的文档快照）：安全清单 SHA-256 `C53CC796A5771360C481404144AD8729DD42303034FFAB005A3CB1310D715F04`，状态行 SHA-256 `F3C6F3B159C5B0440605E0D8CB04AAA62C4BAA4A4F18AD1EB88D66256FA29AA3`，暂存 raw SHA-256 `65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC`。清单算法为路径排序后「路径|SHA-256 或 MISSING」，状态 / raw 保留 git 行序；各自 LF 分隔、UTF-8 编码。不是最后追加版本的自引用文档哈希；追加后只复核文档及边界，不重新标称产品测试。

| 最终实施文件 / 被测产物 | SHA-256 |
| --- | --- |
| `tests/BotAgent.ReverseTransportProbe/BotAgent.ReverseTransportProbe.csproj` | `A38C2B6E134A13BEBF6E390A3B5936C567E08FB7CDCF85DF9C631B6080AD50A5` |
| `.github/workflows/ci.yml` | `B2B149DC877CDDABAFC32E73DEC0D31E627CBD8F7CC6B0E2976B5468289FE49D` |
| `CONTRIBUTING.md` | `4902B89AC128B62A9F3242307232FA0B22F24AA0E2C343A75E341D6378F4D5C9` |
| `src/BotAgent.Headless/Dockerfile` | `8ACD7744E376D8A6E76ABF624607AEA60C1958F6503CB5CBA05D31A11469F68C` |
| `src/BotAgent.Core/bin/Release/net8.0/BotAgent.Core.dll` | `5C7E83A752EC728A9A988FCF67A9D1A2B249A7A2BD0C3BDAB2926CB5DBA9CC3D` |
| `src/BotAgent.Platforms/bin/Release/net8.0/BotAgent.Platforms.dll` | `EA57663AFCD64E5E4502A08991151F87DD0808ACDD955080280BF2F81302B326` |
| `src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll` | `81965A61E5F3B1D11759F1692C69AECFF7FC7BA6C70E544AAD4675659CBD9948` |
| `tests/BotAgent.ReverseTransportProbe/bin/Release/net8.0/BotAgent.ReverseTransportProbe.dll` | `F12214E01A38DCDA762A29D2C65641AB7080C1DCB28BE988B31A0B6F5FDFDCEB` |
| `tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | `405B14976291D4372332235AA0BE42C65D608F03523594AFB6B4C499991D8942` |
| `tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll` | `820223104B113B4B9DF4C24BEF6CF3EE3CED77E7587A741684977BB7F64505FF` |
| `tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll` | `4A9583D4E8D03E1EEFF12F2E8B7D144DB678B0957CD4D127350E8980A2FB1FAB` |
| `<R4Context>/r4-publish/BotAgent.Headless.dll` | `427ACFF48E2AFC9CE2CBE01961ECC35E86F5CE5D324A4BE3C8AEEC06F6997939` |

以上本仓库被测 Headless / harness / Architecture / Safety 产物还与 R3 相同，但本批均重新构建或执行，不以旧结果代替本次证据。独立上下文产物只绑定 V13 / V14 的源码闭包及资源检查，不用于替换 harness 所测 Headless，也不能证明 Linux 镜像通过。本节 19:17 交付当时有 R4 镜像缺失证据；后续真实补验见 8.7，仓库架构红灯仍独立保留；没有提交、发布或生产回退成果。

### 8.7 本地 Docker 环境恢复后的补验

- 负责人在本聊天告知「我开启了 docker desktop」；继续原已批准 R4 本地镜像构建，不扩大为业务容器运行、部署、推送、生产或波次施工授权。Agent-00 继续独占总纲登记写权，本补验不修改四实施文件。
- 2026-10-08 19:29:50 +08:00 恢复准入：HEAD、307 条状态、暂存区及全部 509 项安全清单与 8.6 最终交付一致；没有证据漂移。总纲恢复前 SHA-256 `01B0CC347E0642B5D99A0431D49590BCD5EDC18BEF124F26A59BD12054252EDC`，已保存本次登记前字节副本，回退只逆转本补验片段。
- 19:29:54～19:29:55 +08:00 核对本机 named pipe、CLI / Server 29.7.2、Linux / amd64，原生命令均退出 0；指定 desktop-linux context，并确认同名 builder 是 docker driver、单节点、端点是已核实的本机 context。只清除验证进程的 Docker 端点 / builder 覆盖，不改用户配置，不 bootstrap 服务或转用远端。
- 为镜像重新构造本批干净 UUID 上下文，274 项允许源码 / 工程 / 静态资源 / 脚本 / 构建配置，清单 SHA-256 仍为 `A1D3BB2D8696828840313F7E81D1D94FDAB298C7A560F982AAC7D50F8474B38D`；不复用 V13 的 bin/obj / 发布产物，不复制数据库、会话、人物档案、日志或环境秘密。原 R3 架构失败及下游冻结不豁免。

恢复上下文：`<Temp>/botagent-r4-context-ccd06d952a2f40619dc936562097a634`。镜像仅用本机独立标签，不覆盖现有业务标签；build 不传 build-arg、secret、ssh、特权 entitlement、挂载、推送或业务启动参数。所有下面的原生命令均使用 pwsh、检查退出码；时间为 2026-10-08 / UTC+08:00。

| 编号 | 真实命令 / 对象 | 时间 / 退出码 | 结果 |
| --- | --- | --- | --- |
| R4-D3 | 本机 context / version / builder inspect | 19:29:54～19:29:55；各 0（builder 后续独立核对 0） | 本机 Linux/amd64 和单节点 docker driver；不改 Docker 配置 |
| R4-D4 | `docker --context desktop-linux build --builder desktop-linux --platform linux/amd64 --load --no-cache --progress plain --iidfile <Temp>/botagent-r4-iid-ccd06d952a2f40619dc936562097a634.txt --file <R4DockerContext>/src/BotAgent.Headless/Dockerfile --tag botagent-r4-synthetic:ccd06d952a2f40619dc936562097a634 <R4DockerContext>` | 19:32:49～19:34:02；0 | 真实完整 Linux SDK 8 restore / publish 和 runtime 阶段成功，非 Windows publish 替代；输入 context 2.81 MB；8 个既有 CS0108，未发现其它构建警告 |
| R4-D5 | `docker --context desktop-linux image inspect <本批标签> --format '{{json .}}'`，只输出核验后的元数据 | 19:35:05；0 | ID 与 iidfile 一致；Linux/amd64、User=qqchat、工作目录 /app、原入口 / 端口 / 卷 / 健康检查匹配，未烘焙 credential 环境变量 |
| R4-D6 | `docker --context desktop-linux image save --platform linux/amd64 --output <Temp>/botagent-r4-image-ccd06d952a2f40619dc936562097a634.tar <本批标签>` | 19:35:05～19:35:07；0 | 仅保存本批镜像，不枚举或导出现有业务镜像 / 容器；不启动应用 |
| R4-D7 | pwsh here-string 经 `python -`，只读取本批镜像归档的清单及白名单 /app 产物 | 19:35:59～19:36:01；0 | 归档仅一个镜像；提取三 DLL 和两个运行元数据文件，PDB=0；net8.0 / Microsoft.NETCore.App 8，依赖包含 Core / Platforms、不包含独立 Storage / Model；无 extractall 或路径越界 |
| R4-D8 | 读取镜像中 Headless DLL 的 manifest resources 并核对源码哈希，不调用 entrypoint | 19:36:41～19:36:42；0 | 7 个 web 资源和 2 个脚本全部存在且哈希一致；不是镜像内应用运行或端到端验收 |

镜像身份：本机标签 `botagent-r4-synthetic:ccd06d952a2f40619dc936562097a634`，ID / Descriptor `sha256:b95ac4783afa8d2d1748449d71f2ab0c79a657af86059c5b8d46ce6ef853e989`。SDK 基础镜像 `sha256:ec9c0a0dc5f60adc2065762050638dd5ed5facfc53716d4580a42d86027c8e80`；runtime 基础镜像 `sha256:47c36b770db8f712ceb03e9220d799b068c769b8bc89c61d62cd41600633d9c2`。镜像元数据 Size=116587064 bytes，仅描述产物大小，不是常驻内存或整机资源验收。

| 镜像内产物 | SHA-256 |
| --- | --- |
| `/app/BotAgent.Core.dll` | `9C5D6823764401B80DD5A0806832EB3DF08E6D6FE1FEE3EC3415061AE77F392D` |
| `/app/BotAgent.Headless.dll` | `E7623252129ED8A6A1EA968ADECA1B8A31CF73F3136B6BED059D1493A6B4B2C0` |
| `/app/BotAgent.Platforms.dll` | `4773CB8E49930A50EA9CF02D7A0B1E1DE5D14EF829A3939905913C3B668A0216` |

- 本补验没有修改四实施文件或产品 / 测试源码，不重新运行 Windows 产品探针或 harness；8.3 的合成调用链证据保留原命令 / 时间与源码、二进制指纹绑定，不冒充本轮新运行或 Linux 容器 E2E。R4 镜像构建输入门禁已补齐，补丁待负责人核收；R3 六项架构违规未解决，Wave 1 及下游仍冻结。
- 未使用 docker run / create / start / exec / logs / compose、未挂载真实数据、未启动应用或健康探测、未 push 镜像、未变更默认 context / builder 或现有业务标签，不清理其它镜像或缓存。仅保留本批独立镜像和白名单临时证据；它们不是部署或数据回退演练。回退本补验只逆转总纲的本次片段，有并行变化先协调，不自动清理工作区或恢复整个文件。
- 19:39:06～19:39:06 +08:00，R4-D9：总纲本地链接 / 表格 / 围栏 / 尾空白、四实施文件和七份仓库被测 DLL 哈希核对通过，`git diff --check` 退出 0。19:39:10 核对全部 509 项安全清单：相对本次恢复开始仅总纲改变，其余 508 项一致；HEAD、307 条状态及暂存 raw 不变，未修改产品源码或既有输入。原合成测试证据依旧绑定同一源码 / 二进制，但未重跑，不记为本轮新验收。
- 2026-10-08T19:40:00.8970272+08:00 摘要（本段追加前的文档快照，同 8.6 算法）：安全清单 SHA-256 `5629E12DA44F614661495D9EB0B29B35CAEA67EFE873E1DA269C2FCB6481478B`、状态 SHA-256 `F3C6F3B159C5B0440605E0D8CB04AAA62C4BAA4A4F18AD1EB88D66256FA29AA3`、暂存 raw SHA-256 `65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC`。追加后只做文档 / 范围核对；文档不记录自身最后追加版本的自引用哈希。
- 收尾：原 R4 授权范围的入口补丁、合成调用链验证及真实 Linux 镜像构建 / 静态产物检查均已提交核收，无需扩大写权继续修改代码。仓库架构门禁仍为此前已绑定指纹的六项失败，Wave 1 / 02 / 03 / Web 不解锁；实际 CI、全矩阵、Linux 容器运行、协议互通、生产资源及回退仍无验收，不由镜像成功替代。没有提交、推送、部署或生产操作。

## 九、R5-A 飞书去重写入收口（Agent-00）

### 9.1 授权、写权与准入

- 负责人先批准下一阶段，随后对本聊天列出的十个确切路径及三条去重契约明确答复「确认」。执行者 / 共享最终写入者 / 交付对象：Agent-00 / Agent-00 / 负责人；本批不启动其它 Agent，不移交写权，不授权提交、推送、发布、部署、生产、Storage 整体接入或时钟修复。
- 准入时间：`2026-10-08T20:42:33.0936730+08:00`；HEAD `1f3181e21e8d1f9531e41066b033d8d60218d580`，main；307 条状态（暂存删除 20、未暂存删除 92、未暂存修改 21、未跟踪 174）。509 项安全清单与刚完成的只读盘点完全一致；清单 SHA-256 `A63D95CBBB0CE5383EF2FFD30EB555D5F434232D164219B161197D9571F8393F`，状态 SHA-256 `F3C6F3B159C5B0440605E0D8CB04AAA62C4BAA4A4F18AD1EB88D66256FA29AA3`，暂存 raw SHA-256 `65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC`。算法沿用 8.6。
- 只认领下列十个路径，保存八个现有文件的开始字节压缩副本 / 哈希；两个新增路径开始不存在。保留未跟踪的 Platforms 实现及总纲中的既有内容，不将既有输入计作本批成果。旧 PlatformDedupStore 在等价合成验证及真实宿主接入通过前不移除；清单外需求先停并补批。

| 执行者 | 确切路径 | 开始 SHA-256（MISSING 为拟新增） |
| --- | --- | --- |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Core\Domain\Ports\IWebhookDedupStore.cs` | `MISSING` |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\FeishuWebhookDedupStore.cs` | `MISSING` |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\AppDatabase.cs` | `43A1652AB400A47C6501318285BA844A377F92262AE6A00C40DB31BC997E92E4` |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Platforms\Feishu\FeishuBotGateway.cs` | `6EDCC9DD3F9B9950F5081E04230BC2730FDD084D80B6A411681445EA9F5B36E1` |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Platforms\Persistence\PlatformDedupStore.cs` | `7C1560814DA9F692C968B215DDCE8667DF60CD55BA7246C13B4959D73546F3C4` |
| 00 | `%WORKDIR%\bot-agent\src\BotAgent.Headless\Host\CompositionRoot.cs` | `2F9F166B9763DB07E2D695D9106C9CA7C02141D6F4E03976A737CFCD40BFEB6F` |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.FeishuRemediationProbe\Program.cs` | `9EC5F1C3DBAF4275763102F510CC61315B42D7790825074331E86DA01AE23A2F` |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.ProductionSpecProbe\Program.cs` | `1E08FACA04453CB495744A096AFEEF4355CC9E7AD8C29A89345D28106E0A5F9A` |
| 00 | `%WORKDIR%\bot-agent\tests\BotAgent.IntegrationHarness\FeishuPlatformScenario.cs` | `C0821442484D6C490B0E5764BC56C4E081159F1ABEAE2C78DFFD67340666B00E` |
| 00 | `%WORKDIR%\bot-agent\docs\plans\agents\00-orchestration-and-dependency-graph.md` | `A389A1B50D73E9A838DBB1D4DC0F8023E97EB44B9743B00B66288A8539FDE86C` |

### 9.2 局部契约冻结与消费者核对

- 本批局部契约标识 `R5-dedup-v1`。负责人已确认：既有 key / 表 / TTL 保留；nonce 与事件整批原子登记，失败不留下部分标记；重复返回 200，持久化不可用返回可重试的 503。
- Core 拟落地 `IWebhookDedupStore.TryRegister(IReadOnlyList<string> keys, DateTimeOffset seenAt, TimeSpan ttl): bool`：全部 key 未登记时整批提交并返回 true；任意 key 未过期时返回 false 且不新增其它 key；写入失败抛出，由网关转换为安全错误码。key 逐字保留，不改变已有 event/message/nonce 格式；异常、HTTP 与诊断不得输出原始 key 或载荷。
- Host 过渡适配器只调用当前唯一被宿主采用的旧 `AppDatabase.Write` / 去重入口，不开第二条连接、不反向引用 Storage。扩展现有去重方法支持整批事务，保留旧单 key 调用；不声明整个应用已实现统一配置版本或单一全局 writer。
- 00 已按 01（事务 / Core 端口）、02（飞书消费者）、06（宿主组装 / HTTP）职责交叉核对真实源码、构造调用点和测试入口；未创建其它执行 Agent，未冒称它们已实际签署。此批准与冻结只覆盖本批去重契约，不覆盖 4.3 的统一保存 / 管理 / 时钟 / ZBA 契约。
- 网关保留已有四参数构造签名，另加五参数端口注入重载；未注入端口的合法普通入站 fail-closed 为 503，不自动回落到平台自写库。握手 / 出站不因缺少去重端口产生去重 IO。已核实消费者只有宿主组装、FeishuRemediationProbe 和 ProductionSpecProbe 的构造调用，分别在本批认领路径中接入。
- 预先确认的合成测试 seam：公开 HandleWebhookAsync、真实数据库去重事务入口及端口、真实宿主 HTTP / S50 重启链。先补失败与重试负例，再最小接入，随后扩原子性、并发、TTL 和重启验证；不放宽原断言、棘轮或外部访问安全政策。

### 9.3 验证与停止边界

- 原 R3 / R4 架构六项红灯保留，受影响下游冻结；本批只进行获准独立定向验证，不视为 Wave 1 准出。先独立强制重建 Headless，再重建 harness，随后运行 S50 / S36 / S52；现有飞书、配置、安全及适用反向传输 / ProductionSpec 探针记录实际覆盖。
- 所有 Windows 命令使用 pwsh；每项原生命令核对退出码。产品验证进程先仅按名称清除继承配置，不输出值；纯探针采用新 UUID 合成根或核实其自行创建的根；harness 只绑定本仓库重建 DLL 与单场景，由场景固定到本机 mock 和两套合成根。不读取真实数据、群聊、人物档案、原始协议或未筛选日志。
- 本批不运行 Docker、全矩阵、GitHub CI、真实官方 / 飞书互通、资源测量或部署回退；不修改项目引用、架构护栏、CI、设置或秘密政策。接口 / 写权冲突、兼容性未知或清单外需求出现时停止受影响部分。

### 9.4 实施与验证记录（持续追加）


命令均从唯一仓库根使用 pwsh；本机 SDK `10.0.101`、目标 net8.0，不等于 SDK 8 CI 或生产验收。每项原生命令检查退出码；失败项单独结束。本批获准的独立定向复验不绕过总体架构门禁。

真实命令索引（不是拟建工程）：

- FeishuRemediationProbe：`dotnet run --project tests/BotAgent.FeishuRemediationProbe/BotAgent.FeishuRemediationProbe.csproj -c Release`。
- Headless Rebuild：`dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild`。
- harness Rebuild：`dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release -t:Rebuild`。
- ArchitectureProbe：`dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release`。V9 / V10 根据已输出源码根和 cwd 核对目标；当时设置的 BOTAGENT_SOURCE_ROOT 不是该探针实际读取的入口，不将其声称为生效绑定。V30 查询真实入口后显式设置 QQCHAT_SRC_ROOT。
- ProductionSpecProbe、SettingsScopeProbe、SafetyProbe、ReverseTransportProbe：分别执行 `dotnet run --project tests/BotAgent.<对应探针>/BotAgent.<对应探针>.csproj -c Release`，对应工程均先核实；不改入口或断言。
- harness：`dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll`；每次 QQCHAT_IT_ONLY 为单个 s50 / s36 / s52，QQCHAT_BOT_DLL 显式绑定 `%WORKDIR%\bot-agent\src\BotAgent.Headless\bin\Release\net8.0\BotAgent.Headless.dll`；最终运行前 V20 / V21 已独立强制重建。

| 编号 | 命令 / 对象 | 时间（2026-10-08 / UTC+08:00）/ 退出码 | 实际结果 |
| --- | --- | --- | --- |
| R5-V1 | FeishuRemediationProbe（下述命令） | 20:45:16～20:45:40；1 | 原 15 / 0；新增故障 / 重试负例失败，15 / 1 |
| R5-V2 | 同 V1 | 20:46:38～20:46:43；0 | 端口接入后 16 / 0；尚未完成真实整批原子性 |
| R5-V3 | 同 V1 | 20:47:23～20:47:25；1 | 16 / 1；真实旧单 key 循环消费了新 nonce |
| R5-V4 | 同 V1 | 20:47:52～20:47:56；0 | 初步整批事务后 17 / 0 |
| R5-V5 | 同 V1 | 20:49:11～20:49:15；0 | 扩展 SQLite 中途失败、并发、TTL、缺端口等，22 / 0 |
| R5-V6 | Headless Rebuild（下述命令） | 20:51:01～20:51:04；0 | 8 既有警告 / 0 错误 |
| R5-V7 | harness Rebuild（下述命令） | 20:51:04～20:51:08；0 | 2 既有警告 / 0 错误 |
| R5-V8 | harness DLL，S50 | 20:51:26～20:51:29；0 | 14 / 0；真实宿主故障 / 重试 / 重启首次验证；非最终源码 |
| R5-V9 | ArchitectureProbe（下述命令） | 20:52:05～20:52:06；1 | 119 / 5；旧平台文件移除后位置红灯消失，但本批新增 HandleWebhookAsync 204 > 198；停止受影响后续 |
| R5-V10 | 同 V9 | 20:54:00～20:54:02；1 | 提取去重处理、复用唯一键与原 SQL 后 120 / 4；Headless SQL 回到 137 |
| R5-V11 | 同 V1 | 20:54:03～20:54:09；0 | 事务 / 网关整理后 22 / 0 |
| R5-V12 | 同 V6 | 20:54:46～20:54:49；0 | 8 既有警告 / 0 错误 |
| R5-V13 | 同 V7 | 20:54:49～20:54:53；0 | 2 既有警告 / 0 错误 |
| R5-V14 | ProductionSpecProbe（下述命令） | 20:54:53～20:54:56；0 | 62 / 0；不是生产操作 |
| R5-V15 | SettingsScopeProbe（下述命令） | 20:54:56～20:54:59；0 | 17 PASS / 0 |
| R5-V16 | SafetyProbe（下述命令） | 20:55:31～20:55:48；0 | 594 / 0；之后仅兼容构造重载及测试变更，最终另复跑 |
| R5-V17 | 同 V1 | 20:58:05～20:58:07；1 | 新增签名兼容测试缺少实际命名空间，CS0246；未运行任何断言，不能算行为红灯 |
| R5-V18 | 同 V1 | 20:58:09～20:58:12；1 | 同一测试 CS0246，未运行断言；后查到 BotAgent.Platforms.IPlatformSettingsBox 并修正 |
| R5-V19 | 同 V1，最终源码 | 20:58:58～20:59:03；0 | 24 PASS / 0；原 15 项及新增 9 项 |
| R5-V20 | 同 V6，最终源码 | 20:59:04～20:59:08；0 | 真实宿主独立强制重建；8 既有警告 / 0 错误 |
| R5-V21 | 同 V7，最终源码 | 20:59:08～20:59:11；0 | harness 独立重建；2 既有警告 / 0 错误 |
| R5-V22 | 同 V14，最终源码 | 20:59:11～20:59:14；0 | 62 / 0；原断言未改 |
| R5-V23 | 同 V15，最终源码 | 20:59:14～20:59:17；0 | 17 PASS / 0；R1 与原事务 / 发布回归 |
| R5-V24 | harness DLL，S50，最终源码 | 20:59:55～20:59:57；0 | 14 / 0；含四项本批真实故障 / 修复 / 重启检查 |
| R5-V25 | harness DLL，S36 | 20:59:57～21:00:04；0 | 98 / 0；R2 Secret、真实 settings / 静态资源链 |
| R5-V26 | harness DLL，S52 | 21:00:04～21:00:11；0 | 119 / 0；真实平台策略 / 本地入站 / mock 模型 / outbox / 重启 |
| R5-V27 | 同 V16，最终源码 | 21:00:23～21:00:39；0 | 594 / 0；安全断言未改 |
| R5-V28 | ReverseTransportProbe（下述命令） | 21:00:39～21:00:51；0 | 13 PASS / 0；原握手、认证替换及停机断言 |
| R5-V29 | pwsh here-string 经 python -，副本与语义核对 | 21:02:46；0 | 八副本内存解压哈希通过；原 15 项飞书与 HTTP/temp fixture、Production 全正文、数据库初始化 / writer / schema / 迁移、Host 非接入正文保留 |
| R5-V30 | 同 V9，最终源码，QQCHAT_SRC_ROOT 绑定本仓库 | 21:03:18～21:03:20；1 | 120 / 4；301 源文件 / 58004 行；全部原护栏及 29 个扫描器自检仍在默认链 |
| R5-V31 | 原生 git stdout 原样传输逆向 check-only；git diff --check | 21:05:16；各 0 | 五个开始无改动的 tracked 文件逆向补丁适用；CR=0，无实际回退；未跟踪及新增文件另用副本 / 范围核对 |

- 全部验证清除继承的 BOTAGENT / QQCHAT / OPENAI / MINIMAX / TTS / HARNESS 配置及 HEALTH_PORT，只按名称操作，不回显实际值。飞书探针自行生成 UUID 根并设置 BOTAGENT_DATA_DIR；ProductionSpec 自行生成根并设置 QQCHAT_DATA_DIR，另一别名均已清除；Safety 明确设置两套新合成根、关闭文件日志；SettingsScope 自行设置合成根。harness 父进程不设置 data 别名，S50 新增两套同根及关闭日志，S36 / S52 自行隔离。本批没有外部模式、真实平台、群聊、数据库或生产日志访问。
- V17 / V18 是本批新增测试的编译错误，归属 Agent-00；修正只涉及已认领测试中类型的真实全名，不删除检查。源文件定位查询也曾使用错误候选路径并停止该查询，随后用实际文件枚举定位；不把查询错误混入产品计数或声称这些路径真实存在。
- 新警告为 0；Headless 强制重建的 8 个 CS0108 仍来自未改 HttpFetcher，harness 的 2 个 CS8602 仍来自未改 MockOpenAi / AgentBridgeScenario；最终指纹与开始清单一致。原护栏四文件、SettingsScope / Safety / ReverseTransport / 公共 harness 注册入口、项目引用、CI、Dockerfile 均未写入。

### 9.5 实施、调用链与兼容

- 新 Core 端口为 9.2 的真实签名。过渡 FeishuWebhookDedupStore 仅转发到现有 Headless AppDatabase；没有建立第二份数据库状态 / 连接 / 路径解析器，也不把未采用的 Storage 当成已接入。
- 原单 key 方法保留并委托整批方法；复用原参数化 DELETE / INSERT OR IGNORE / RETURNING 语句、SQLite 主键及现有 Write 事务。任意 key 命中重复，通过内部专用标记触发整批回滚后返回 false；其它写入异常仍抛出。去掉初版多余的预查询，不替换 SQL 起始词逃避计数。key 不 trim，重复输入按 Ordinal 合并；空批或空白 key 在写入前拒绝，TTL 的最小秒数和 inclusive 秒级旧边界不变。
- Gateway 保留原四参数 CLR 构造签名和默认参数，新增五参数注入重载（log / ids / dedupStore 显式提供，避免重载歧义）；没有仅凭「可选参数」宣称二进制签名兼容。旧构造仍可握手 / 出站，但普通合法入站缺端口时返回 503；外部消费者须接入端口，不能自动回落平台自写库。这是已批准的 fail-closed 收紧，不代表旧宿主产物与新 Platforms 任意混搭已验收。
- nonce 与 event/message 的现有 key 在同一次端口调用中提交；缓存只在事务成功后更新。重复返回 200，缺端口 / 持久化异常返回 503 / dedup_unavailable；错误不返回异常正文、原始 key、凭据或报文。身份绑定、白名单、签名、取消与回调的其它路径保持原约定。
- V8 已证明真实接入后，20:52 删除 PlatformDedupStore（以删除前查询 / V9 时间为准）；删除前其哈希仍为 9.1 的值且没有运行消费者引用，只有原架构负例仍引用该路径字符串。该负例保留，不修改测试换绿。旧文件是开始时未跟踪输入，八份副本含其完整字节；移除属于本批获批交接，不算清理其它未跟踪文件。
- 真实生产源码组装链：Program → CompositionRoot.BootstrapSettings → 旧 AppDatabase.Initialize；CompositionRoot.BuildChannelLayer → FeishuBotGateway 五参数重载 / FeishuWebhookDedupStore；现有 WebUiServer 的 webhook HTTP → HandleWebhookAsync → Core 端口 → 旧 AppDatabase.TryRegisterFeishuWebhooks → Write / SQLite 主键与事务 → 入站事件 → 模型 mock → 飞书 mock 发送。
- S50 在自己的合成库设置触发器制造真实写失败，宿主返回 503 且模型 / 发送计数不增长；修复后同事件触发真实模型和发送，停止原宿主再用同根重启，旧事件不重放，新事件仍回复。它证明宿主采用新端口，而不只是程序集编译。真实签名 nonce / event 中途失败和回滚由飞书探针真实 SQLite 故障及同请求恢复证明；S50 未新增签名协议完整互通声明。
- 兼容证据：既有表、schema / 迁移 / Write 正文与开始副本完全一致；旧单 key、预置旧格式合成行、未归一化 key、TTL、跨适配器并发仅一赢家、失败重试、重建网关及真实宿主重启通过。原 15 个飞书测试仅修改依赖注入，原 HTTP / temp fixture 和 Production 原断言完整保留。官方 Secret 环境专属、存储 / key 原样及 nameRaw 政策不变。
- 本批局部 R5-dedup-v1 冻结不替代统一保存、管理、时钟、ZBA 的消费者签署。当前 Host 仍只引用 Core / Platforms，Storage / Model 整体迁移未实施。
- 已查明的超时差异：原平台自建连接 DefaultTimeout=5 秒；新端口继承现有宿主数据库 DefaultTimeout=30 秒 / busy_timeout=30000，未改变数据库实现的超时策略。真实长锁拥塞、响应超时及资源口径未验证，须在后续门禁评审；本批的立即写失败 / 503 证据不能代替它们。

### 9.6 架构失败归属、回退与交付结论

| 当前门禁 | 最终实测 / 阈值 | 归属与处理 |
| --- | --- | --- |
| 平台 SQL / IO 位置 | 均通过；平台自写库文件已移除 | R5-A 消除两项 R3 违规，不调整允许位置 |
| SQL 全局 | 265 > 137（Headless 137 + Storage 128） | 新旧仓储仍重复；另批完整迁移及旧文件交接 |
| IO 全局 | 98 > 62 | 重复存储实现仍计数；另批，不删断言或去重 |
| 系统时间位置 | Model / Platforms / Storage Clock 各 3 处 | 统一 IClock 注入 / 宿主组装待单独冻结 |
| 系统时间全局 | 12 > 3 | 三份额外默认实现未修复 |
| 最长方法 | 最终 197 ≤ 198 | 本批一度新增 204 > 198；提取网关去重处理后复验通过，阈值不变 |

- 回退证据：V29 八个开始字节副本完整恢复校验通过；V31 五个 tracked 补丁 check-only 通过，无实际源码回退。副本只包含本批允许的代码 / 文档，位于本批 UUID 临时证据根，不是全仓库或生产备份。
- 回退步骤：仅逆转五个 tracked 文件中的本批改动及 Gateway / 总纲本批片段；新增两个文件须核对无后续编辑后按本任务新增处理；旧 PlatformDedupStore 只能在该路径仍空缺且无交接冲突时恢复本批保存的确切字节。任一后续改动或归属不明则停止协调。不得恢复整份总纲、强制 checkout / hard reset 或清理其它未跟踪文件。
- 直接回退会重引入平台自写库、失败冒充重复及部分 nonce 消费风险；先报告并取得负责人决定。没有改变数据 schema，没有执行旧二进制混搭、实际源码回退、资源或部署恢复演练；预案 / inverse check 不是部署回退验收。
- 00 结论：R5-A 十路径交付及约定定向合成验收完成，提交负责人核收；架构最终 120 / 4，Wave 1 未准出、02 / 03 / Web 不解锁。重复存储及统一时钟须另批确切路径，不自动推进其它波次。
- 未覆盖：全 solution / 专项探针 / harness / CI 矩阵、S42、真实官方 / 飞书互通、SQL 锁长时间拥塞 / 崩溃故障、所有外部预编译消费者、Storage / Model 接入、统一配置版本 / 全局 writer / ZBA、资源与部署回退。本批不构建 Docker；R4 镜像只绑定当时源码，不能作为当前 R5 产品镜像证据。
- 本批无提交、推送、发布、部署或生产操作；没有创建执行 Agent、临时移交共享写权、读取真实数据或修改清单外文件。

### 9.7 交付指纹、范围与证据绑定

- `2026-10-08T21:05:17.4726332+08:00` 核对（9.4～9.7 完成登记追加前的文档快照）：HEAD `1f3181e21e8d1f9531e41066b033d8d60218d580` 不变；313 条状态（暂存删除 20、未暂存删除 92、未暂存修改 26、未跟踪 175）。本批新增五个 tracked 修改状态、新增两个源码未跟踪状态、移除一个获批旧未跟踪实现，其它所有开始状态保留；没有暂存、提交或整理既有输入。
- 当前安全清单 510 项：相对开始 509 项，两个源码新增、一个获批旧实现移除；八个既有认领路径发生本批变化（含总纲和旧文件移除），其余 501 项开始安全文件逐项一致；变化恰好是 9.1 的十个认领路径。
- 本快照清单 SHA-256 `F723DEC5A08941F749057A0A1C584263E6489FF27A803B86BBB7BB594FDE0411`、状态 SHA-256 `9353FF0259B28EFB53571685EE09CF8147C503A33BADBCEBE1F76BDFE40E41CA`、暂存 raw SHA-256 `65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC`。清单 / 状态算法同 8.6；不是最后追加文档的自引用哈希。之后只追加登记、复核文档及范围，不改产品 / 测试源或重新标称项目验收。
- 下表为已通过最终定向回归的源码 / 产物；原反向探针与架构 DLL 哈希未变，但本批确实重新执行，不用历史成绩替代 V28 / V30。Core / Platforms / Headless / harness 已变化，R4 镜像不能冒充当前 R5 产物。
- 尚无干净提交 / 发布版本身份；接口版本仅为 R5-dedup-v1；负责人仍须核收本批，后续业务施工另批。后续漂移须重新确认受影响证据，不拿相同 HEAD 或仍存在的 DLL 当作验收有效。

| 最终实施 / 旧路径 | SHA-256（MISSING 为获批移除） |
| --- | --- |
| `src/BotAgent.Core/Domain/Ports/IWebhookDedupStore.cs` | `5375F1697D01ABA078F93FED906EE712BB31DA24E2855F97BAD9CBB4C634EE35` |
| `src/BotAgent.Headless/Adapters/Persistence/FeishuWebhookDedupStore.cs` | `5C8033BD0D33F57C172764736B0D6D3113696BDA8310429A4CEE906B864CD148` |
| `src/BotAgent.Headless/Adapters/Persistence/AppDatabase.cs` | `670EBDDDC9B956DFD7BCF5044B090EA02AC458EF2EF6417AD4DE71D5DA2045F1` |
| `src/BotAgent.Platforms/Feishu/FeishuBotGateway.cs` | `C9A9C5DFED46E44DB205820D2A5CD2902FD880B6AB38A81648140E91383AB24F` |
| `src/BotAgent.Platforms/Persistence/PlatformDedupStore.cs` | `MISSING` |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `7FCB88962F82A7D56F105B23AD0CDEB850765665595476E66C850B8628972810` |
| `tests/BotAgent.FeishuRemediationProbe/Program.cs` | `81B7FD7C8A857FE1C39E69FB8A6438269E2EC9BA152B6B8BBC2F43329FEAC544` |
| `tests/BotAgent.ProductionSpecProbe/Program.cs` | `30B4FF1C73B071356D706539EAE1359EF7B0646724001AC54982298BA2E657EF` |
| `tests/BotAgent.IntegrationHarness/FeishuPlatformScenario.cs` | `1330B2D784C2B62868AB8196E10BCD1EC21383FAAC6B643657E3A68E6070D33C` |

| 本批被测产物 | SHA-256 |
| --- | --- |
| `src/BotAgent.Core/bin/Release/net8.0/BotAgent.Core.dll` | `D754B1B38FD92C9F14C193F1198D7612CD51E7EBDAFCAD5AA8905AC788A852FD` |
| `src/BotAgent.Platforms/bin/Release/net8.0/BotAgent.Platforms.dll` | `DC9BC6D6701DE5BF204069E0602C2B709855696C2481B65263C7F1C54C0D5508` |
| `src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll` | `802964AF7ECDBC8C25470C29FD06B62AE967552E85B15B0D467863B4A1A308BD` |
| `tests/BotAgent.FeishuRemediationProbe/bin/Release/net8.0/BotAgent.FeishuRemediationProbe.dll` | `6CCB0EBB6D0C970A084473BA54A6593F23FE4E4B1A2DAF85E507D61ADCEC260F` |
| `tests/BotAgent.ProductionSpecProbe/bin/Release/net8.0/BotAgent.ProductionSpecProbe.dll` | `FF067A05DCD29DEEDB421B3D5186FCB186761A49ECDBAA7665AC4EA504B3674A` |
| `tests/BotAgent.SettingsScopeProbe/bin/Release/net8.0/BotAgent.SettingsScopeProbe.dll` | `DD80D17F75FD854DC1E2444CAB18E8A0771D9C6CCD9B4941F7EE7C0A3485B676` |
| `tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll` | `47987AFCF1629C1A1C44212A57F4AFCFEE7F5AD5377195354B468BD85DA7FF0A` |
| `tests/BotAgent.ReverseTransportProbe/bin/Release/net8.0/BotAgent.ReverseTransportProbe.dll` | `F12214E01A38DCDA762A29D2C65641AB7080C1DCB28BE988B31A0B6F5FDFDCEB` |
| `tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | `40268FF4F2F13D0A6A1E4CBB396397EDCDC2E71C0F8F74269FF5DF0C04E8D5C8` |
| `tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll` | `820223104B113B4B9DF4C24BEF6CF3EE3CED77E7587A741684977BB7F64505FF` |

- 21:12:15 +08:00，R5-V32：本总纲本地链接、表格、围栏和尾空白检查通过，十份本批被测 DLL 哈希均与上表一致；git diff --check 退出 0。随后只补登记与复核边界，不重跑产品验收；最终源码及产物指纹不变。

## 十、R6-A Wave 1 准入、契约评审与逐文件交接准备（Agent-00）

### 10.1 本次授权、唯一写权与输入指纹

- 上轮只读盘点后提出 R6-A：仅更新本总纲，登记 Wave 1 缺口、契约评审和旧文件交接；负责人本次答复「继续 agent00 的下一个任务」，按该已提出范围执行。此为文档任务授权，不是 Wave 1 代码、构建、项目测试、创建执行 Agent 或后续批次授权。
- 执行者 / 最终共享写入者 / 交付对象：Agent-00 / Agent-00 / 负责人。唯一认领路径为 `%WORKDIR%\bot-agent\docs\plans\agents\00-orchestration-and-dependency-graph.md`；开始副本已保存为会话内 gzip 字节，仅用于本批片段回退。不创建仓库内附加文件、不移交写权，01～07 当前认领集合均为空。
- 准入核对 `2026-10-08T21:34:12.7205156+08:00`；认领及副本时间 `2026-10-08T21:36:20.8655860+08:00`。HEAD 为 `1f3181e21e8d1f9531e41066b033d8d60218d580`，分支 main；313 条状态：暂存删除 20、未暂存删除 92、未暂存修改 26、未跟踪 175。全部保留，不把既有迁移或 R1～R5 成果重记为 R6-A 成果。
- 510 项安全清单 SHA-256 为 `7A5966944CFBE70984E1D0F5DD018CBFAA736E2328C6F3F700DA6932FFE0379F`；状态行 SHA-256 为 `9353FF0259B28EFB53571685EE09CF8147C503A33BADBCEBE1F76BDFE40E41CA`；暂存 raw SHA-256 为 `65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC`。清单 / 状态算法沿用 8.6；排除 bin/obj、运行数据、日志等，清单不是全磁盘扫描。
- 本总纲开始 SHA-256 为 `5D413C7724AE7F15999D3737FECC599ABBBA22383856A2FC5228C6782D2E8A3C`，117207 字节。全部 510 项与上轮只读盘点一致，9.7 的九项源码 / 缺失旧路径及十份 DLL 共 19 个绑定均匹配；匹配只证明未漂移，不是本次重跑成绩。
- 已依次重读工作区 / 仓库 AGENTS、总规划、本手册及相关角色手册；受影响 docs/src/tests 文件清单内没有额外 AGENTS。所有 shell 使用 pwsh；未读取真实数据、环境秘密值或日志，未调用产品启动入口。

### 10.2 波次现状、缺口与前置依赖

以下为本次静态事实及门禁复核；动态成绩仍只属于第九节的原时间 / 指纹，不写成 R6 新验收。

| 波次 | 当前真实状态 / 缺口 | 准入或下游解锁条件 |
| --- | --- | --- |
| Wave 1（01） | Core 无业务引用；Storage 只引用 Core；Headless 只直接引用 Core / Platforms（csproj:55～56），未采用 Storage。21 对仓储重复、六个宿主独有实现、配置端口宿主类型耦合、时钟与保存契约未收口 | 先冻结 10.4 的适用契约、另批确切代码 / 测试写权；宿主统一接入、兼容与适用行为回归、架构门禁通过后才能准出 |
| Wave 2A（02） | Platforms 只引用 Core，宿主已引用；R5 去重走 Core 端口，但四平台完整生命周期 / 能力 / 兼容未验收，模块 Clock 仍独立 | Wave 1 验收有效；02/03 写权逐文件不交叉才可并行，不能用已有程序集 / S52 局部结果提前解锁 |
| Wave 2B（03） | Model 只引用 Core，Headless 未直接引用；宿主仍有旧 OpenAiClient / ModelTransport，模块 Clock 独立 | Wave 1 验收有效、模型 / 配置 / 时钟消费者契约冻结；一次真实合成请求证明新链路后再处理旧路径 |
| Wave 3（04） | Engine 工程未建立；回复、工具注册及任务编排仍在 Headless，内部插件迁移未验收 | Wave 1/2 准出；存储 / 模型 / 平台 / 工具端口及审批 / 队列契约明确，逐批接入，不新建独立 Plugins 工程 |
| Wave 4（05） | Mcp 工程未建立；目标协议、SDK、支持矩阵尚无本次冻结与实际互通证据 | Wave 3 准出；05 提交版本 / 官方依据及真实 SDK 接口，01/04/06 评审出网、取消、恢复后另批实现 |
| Wave 5（06） | 现有 Host / 管理接口存在；Headless 仍嵌入 wwwroot 和两脚本；新 Panel / ZBA、资源 / 产物回退未验收 | 06 提前参与接口评审；Wave 1～4 准出后汇总必选 ZBA、安全、全矩阵、资源与回退门禁，保留可用管理通路 |
| Wave 6（07） | 独立 web 尚未建立，无写权 | Wave 1～5、必选 ZBA、安全、资源及回退全部通过；00 提交核对证据，负责人明确批准。00 不代批 |

第九节最新架构实测为 120 / 4：SQL 全局 265 > 137、IO 全局 98 > 62、三份模块系统时间位置违规、系统时间全局 12 > 3。本轮没有运行 ArchitectureProbe，数字是未漂移源码对应的历史证据；四项仍按未解除处理。R4 镜像绑定旧输入，不是当前 R5/R6 产物。

### 10.3 Storage 逐文件交接候选矩阵（未移交、未授权删除）

本节路径相对唯一仓库根。新文件拟由 01 承接；旧 Headless 路径由 00 最终处置。每行均须另批、记录双方基准哈希 / 消费者 / 行为及数据兼容 / 回退，宿主验收后才可删除旧实现；字节相同不等于程序集内静态状态、路径解析和依赖上下文相同。

| 旧路径（00） | 新路径候选（01） | 本次字节对照 / 交接条件 |
| --- | --- | --- |
| `src/BotAgent.Headless/Adapters/Persistence/AgentImageStore.cs` | `src/BotAgent.Storage/AgentImageStore.cs` | 相同；统一 DB / 路径 / 时钟 / 日志上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/AgentSessionStore.cs` | `src/BotAgent.Storage/AgentSessionStore.cs` | 相同；同上 |
| `src/BotAgent.Headless/Adapters/Persistence/AppDatabase.cs` | `src/BotAgent.Storage/AppDatabase.cs` | 不同；保留 R5-dedup-v1，先核对 writer 与单一静态状态 |
| `src/BotAgent.Headless/Adapters/Persistence/AudioCache.cs` | `src/BotAgent.Storage/AudioCache.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/ConversationStore.cs` | `src/BotAgent.Storage/ConversationStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/EpisodeStore.cs` | `src/BotAgent.Storage/EpisodeStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/HostMetrics.cs` | `src/BotAgent.Storage/HostMetrics.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/JargonStore.cs` | `src/BotAgent.Storage/JargonStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/LegacyJsonImporter.cs` | `src/BotAgent.Storage/LegacyJsonImporter.cs` | 不同；新实现仅语法解析，需保持旧配置类型校验 |
| `src/BotAgent.Headless/Adapters/Persistence/MemberProfileStore.cs` | `src/BotAgent.Storage/MemberProfileStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/MemberRoleStore.cs` | `src/BotAgent.Storage/MemberRoleStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/MoodStore.cs` | `src/BotAgent.Storage/MoodStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/MusicStore.cs` | `src/BotAgent.Storage/MusicStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/OwnMessageStore.cs` | `src/BotAgent.Storage/OwnMessageStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/PanelPasswordStore.cs` | `src/BotAgent.Storage/PanelPasswordStore.cs` | 不同；internal → public 的可见性差异，需验证调用 / 认证兼容 |
| `src/BotAgent.Headless/Adapters/Persistence/PromptTemplateStore.cs` | `src/BotAgent.Storage/PromptTemplateStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/SecretFiles.cs` | `src/BotAgent.Storage/SecretFiles.cs` | 相同；统一上下文及秘密策略后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/SecretsStore.cs` | `src/BotAgent.Storage/SecretsStore.cs` | 相同；同上，不恢复官方 Secret 仓储回填 |
| `src/BotAgent.Headless/Adapters/Persistence/StickerStore.cs` | `src/BotAgent.Storage/StickerStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/TenantQuotaStore.cs` | `src/BotAgent.Storage/TenantQuotaStore.cs` | 相同；统一上下文后验证 |
| `src/BotAgent.Headless/Adapters/Persistence/TtsConfFile.cs` | `src/BotAgent.Storage/TtsConfFile.cs` | 相同；统一上下文后验证 |

18 对相同、三对不同。关键差异不能靠添加 ProjectReference 自动解决：

- 两份 AppDatabase 具有相同 FQN `BotAgent.Adapters.Persistence.AppDatabase`，但各程序集 `_ready` / 路径 / 连接串是独立状态。Storage 多了 WriteGate，仍只有单 key 去重入口；旧宿主已有 R5 整批回滚语义。不能让宿主与 Storage 各自初始化或把 R5 改回循环登记；现有 30 秒 SQLite busy timeout 也须评审拥塞 / 取消，不把立即故障测试当长锁验证。
- LegacyJsonImporter:88 从 `JsonSerializer.Deserialize<AppSettings>(json, ReadOptions)` 变成 `JsonDocument.Parse(json)`；语法合法不保证配置类型合法。Storage 的 `AppSettingsDefaults.cs` 只是内部默认提示常量，不是宿主 AppSettings 模型；需冻结校验 seam，不反向引用 Headless。
- PanelPasswordStore:5 只是类型可见性改变；不据此宣称已通过宿主认证、公开类型兼容或旧产物混搭测试。

六个宿主独有文件没有现成 Storage 对应实现；均保留，本轮不搬迁，也不授权整体删除 Persistence 目录：

| 当前确切路径 | 后续处理前提 |
| --- | --- |
| `src/BotAgent.Headless/Adapters/Persistence/AuditLogStore.cs` | 配置 / 版本 / 安全审计事务及端口评审后另列目标路径 |
| `src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs` | 行为配置真源、环境种子和秘密来源保留；00 最终合入 |
| `src/BotAgent.Headless/Adapters/Persistence/FeishuWebhookDedupStore.cs` | R5 过渡端口适配器，须先证明指向唯一迁移后数据库 |
| `src/BotAgent.Headless/Adapters/Persistence/ModelProviderStore.cs` | 01/03/06 明确模型配置保存、应用与兼容 |
| `src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs` | 依赖宿主 AppSettings / Ops；先冻结配置类型与事务，不直接复制 |
| `src/BotAgent.Headless/Adapters/Persistence/TraceArchiveStore.cs` | 审计 / 安全摘要 / 归档生命周期评审，不读取真实归档正文 |

支撑文件同样不是已完成交接：`src/BotAgent.Storage/AppPaths.cs`、`AppSettingsDefaults.cs`、`Clock.cs`、`FileLog.cs`、`IPluginStore.cs`、`PluginStore.cs`、`StorageOptions.cs`（后六项同目录）。StorageOptions 的 DbPath / 备份属性存在，但 AppDatabase.Initialize 仍无参数，不能臆造 options 初始化接口。旧 `src/BotAgent.Headless/Services/AppPaths.cs`、`FileLog.cs` 仍是宿主上下文；StorageLogging 默认直接写 Console，宿主安全日志接入 / 字段政策需要明确，本次只读源码，不判断真实日志内容。

### 10.4 契约评审安排与冻结证据

当前只建立议程；没有执行 Agent、消费者签署或新增业务冻结。00 按职责静态核对不等于 01～06 实际确认。R5-dedup-v1 保留已批准局部语义，其它项均待评审。

| 议题 / 评审顺序 | 现有准确入口与缺口 | 参与职责 / 冻结需要的证据 |
| --- | --- | --- |
| C1 时钟与启动，先评审 | Core `Domain/Ports/IClock.cs` 提供本地 / UTC / LocalDateTime / TickCount / 两个 Delay；Host `Adapters/Time/SystemClock.cs` 可替换并恢复；三份模块 Clock 为 internal 且各 new DefaultClock，未见宿主注入 | 01 提端口方案，02/03 为当前消费者，04/05 评未来需求，06 提早期组装 / 停机方案，00 登记。先确认默认 / 未注入 / null / 原子替换 / 取消 / DTO 初始化及测试恢复语义；保留唯一系统时钟实现及 Core Domain 禁止 Clock 入口护栏 |
| C2 唯一数据库及迁移 | 10.3 的重复实现 / 六个缺口；AppDatabase.Initialize / Read / Write、IWebhookDedupStore.TryRegister 已存在 | 01 + 02（去重）/03（供应商）/04（业务仓储）/06（启动 / 回退）；冻结配置 / schema 版本、单一 writer 及连接策略、旧导入、原始 key / TTL、长锁 / 失败 / 恢复和逐文件切换，先确认再接入 |
| C3 配置类型与整批保存 | `src/BotAgent.Headless/Services/Ports/ISettingsRepository.cs` 使用宿主 AppSettings、Ops.AuditEvent / IAuditChain，不能原样移入 Core；`Services/SettingsBox.cs:46` 先 persist 后 publish，published 抛错不回滚已提交版本 | 01 与 02/03/04/05 消费者、06 的 HTTP / DTO 共同评审；明确非秘密快照、版本冲突、未知字段 / 空值、整批校验、幂等作用域 / 有效期、审计事务、提交 / 应用 / 需重启及响应丢失查询。当前不存在 IUnifiedConfigurationStore，不预造名称 |
| C4 管理与能力 | `src/BotAgent.Core/Domain/Ports/IPlatformAdapter.cs` 中 IPlatformRegistry 仅查询；`Domain/Plugins/IPluginRegistry.cs` 仅 GetAll / Find / IsEnabled；IModelClient / IModelTransport 存在，不等于统一启停 / 配置 / 测试接口 | 01 与 02/03/04/05、06；逐项列稳定 ID / 实例隔离、状态、支持动作、校验、取消 / 释放，不支持动作显式拒绝。最小复用扩展而非凭统一外观造平行接口 |
| C5 ZBA 早期消费审查，贯穿 C1～C4 | 当前 `src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs` 是旧管理路径，不是新 ZBA；必选需求依据[面板规划](../z-bot-panel-v0.1-beta-plan.md) | 06 从本批议程起参与，不等 Wave 5；01～05提供端口能力，00 核对认证 / 授权 / CSRF、路由 / DTO / 错误与版本、秘密拒收、任务 / 审批 / 取消 / 恢复、资源 / 回退。07 无写权，不让前端猜接口 |

每项冻结必须登记：版本标识、实际定义及消费者文件、前后行为差异、明确确认者 / 时间、合成用例、兼容与回退条件、00 核对。新签名落地前以“候选”标记；未签署项停止对应消费代码。官方 Secret 仅直接环境变量读取，面板不写入 / 不回显；其它秘密政策不自动扩展，显示脱敏但存储与 key 原样，可编辑名称使用 nameRaw。

### 10.5 角色写权与共享补丁队列

下表是职责映射及下一批审查输入，不授予目录级写权。当前只有 00 的本总纲认领有效；旧文件交接集合为空。未存在工程不臆造具体文件；开工前必须补齐实际 / 拟新增的确切路径并获负责人批准，不能把未来目录当授权通配符。

| 角色 | 当前确切写权 | 后续确切路径候选 / 交接约束 |
| --- | --- | --- |
| 01 | 空 | 10.3 的 21 个新 Storage 路径及支撑文件逐项审查；C1 复用 `src/BotAgent.Core/Domain/Ports/IClock.cs`，C2 复用 `IWebhookDedupStore.cs`（同目录）。是否需要修改端口须评审，不自动扩大范围；旧宿主文件由 00 最终处置 |
| 02 | 空 | 时钟候选 `src/BotAgent.Platforms/Common/Clock.cs`；真实去重消费者 `src/BotAgent.Platforms/Feishu/FeishuBotGateway.cs`。其它平台逐文件盘点 / 补批，旧网关逐文件交接；不改 Storage / Host |
| 03 | 空 | 时钟候选 `src/BotAgent.Model/Clock.cs`；迁移对照 `src/BotAgent.Model/OpenAiClient.cs`、`ModelTransport.cs` 与旧 `src/BotAgent.Headless/Services/Agent/OpenAiClient.cs`、`Adapters/Model/ModelTransport.cs`；不是完整 Model 批次授权 |
| 04 | 空 | 尚无 Engine 新文件认领；旧 `src/BotAgent.Headless/Services/Reply/InlineTurnTools.cs` 为现有迁移盘点入口。回复 / 工具 / 插件可达清单、测试和旧路径须独立逐文件批准 |
| 05 | 空 | 尚无 Mcp 文件认领；版本 / SDK 冻结后列实际新文件与合成测试，Core 端口由 01/00、Engine 接入由 04 配合，不写宿主 |
| 06 | 空 | 提交 `src/BotAgent.Headless/Host/CompositionRoot.cs`、`Host/Program.cs` 及 `BotAgent.Headless.csproj`（三者同工程）的接入补丁需求，由 00 最终写入；独立 Panel / CLI 文件建立前另列路径，不写共享文件 |
| 07 | 空 | Wave 6 锁定，无 web / 前端探针 / CI / 部署写权；不接收未验收临时接口 |
| 00 | 仅 10.1 总纲 | 未来 `BotAgent.slnx`、跨工程引用、Host、AppSettings / SettingsBox、架构护栏、公共测试入口、CI、Docker / 部署配置由 00 最终合入，但本批均无写权。不得用“最终合入者”替代当次批准 |

共享补丁按“契约 / 合成负例 → 实现候选 → 宿主接入 → 真实链路 → 旧文件处置 → 全门禁”分批；每批绑定当前文件哈希和差异，先核对既有编辑，禁止覆盖整个文件。临时移交必须登记接收者、确切路径、开始 / 交回时间并暂停其它写入；本批没有移交。

### 10.6 实施步骤、准出与失败归属

1. **当前 R6-A**：完成本总纲登记与静态文档校验即收尾；Wave 1 仍未施工 / 未验收。本批不建立业务接口或修改代码。
2. **下一候选 R6-B**：先完成 C1 的只读时钟设计 / 消费者评审。选择现有 IClock 的显式注入，或经评审的共享时间 seam；不能靠把三份 internal 类改 public / 删除默认实现就假定启动和全进程假时钟有效。共享入口若确需新增，位置必须避开 Core Domain 禁区，名称 / 文件 / 签名仍待确认，不能登记为已有接口。随后提交含宿主初始化和真实测试入口的确切代码清单，另获授权。
3. **存储接入批次**：C2 / C3 冻结后逐项对齐 10.3，保留 R5 整批去重及旧校验，补合成兼容 / 并发 / 故障用例；00 合入项目引用与组装补丁时保证同 FQN 旧类型不遮蔽新实现、没有两套初始化 / writer。增引用不是验收。
4. **真实调用链要求**：当前 `Host/Program.cs:54 → CompositionRoot.BootstrapSettings:67 → 旧 AppDatabase.Initialize:69 → LegacyJsonImporter / BotConfig`；`Program:71 → CompositionRoot.Build → BuildChannelLayer:366 → FeishuWebhookDedupStore:420` 仍为 R5 过渡链。迁移后必须以真实 Headless 的保存 / 审计 / 重启 / 去重故障恢复证据及实现身份证明唯一 Storage 入口；只编译新 DLL 或同名源码不合格。
5. **准出**：合同 / 写权、目标依赖、宿主采用、适用行为 / 安全 / 架构、配置数据兼容与回退、非零测试覆盖和排除项全部完整，00 复核并提交负责人。Wave 1 准出前 02/03 冻结；准出后仍须独立授权且无交叉写权。任何新失败 / 接口漂移冻结受影响下游。
6. **失败归属**：保留第九节四项既有红灯；新失败先与开始哈希 / 原断言 / 环境比对。测试工具或查询错误单列；新回归归本批执行者，原因不明则停止，不冒称“历史问题”。不抬阈值、不去重漏扫、不删除断言 / 场景，不在清单外消警或修复。

### 10.7 后续合成基线计划（未执行、需另获验证授权）

- 每批执行前复核真实 csproj / 引用 / 测试隔离；记录 SDK / 目标框架、时间及每个原生命令退出码。新警告处理，已知 HttpFetcher 的八个 CS0108 和 harness 两个 CS8602 按当前哈希确认归属，不从增量构建零警告推断已消除。
- 真工程候选顺序：`dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild`；`dotnet build src/BotAgent.Storage/BotAgent.Storage.csproj -c Release -t:Rebuild`（存储批适用）；`dotnet build BotAgent.slnx -c Release`；现有 ArchitectureProbe / SafetyProbe、SettingsScopeProbe / ProductionSpecProbe / FeishuRemediationProbe，时钟批增加现有 ReviewRemediationProbe。每项失败停受影响链；不得为跑下游隐瞒红灯，定向诊断若需要独立运行须明确批准并标记非准出。
- 集成前再确认源码未漂移并**独立重建 Headless**，再构建 `tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj`；绑定本仓库新 Release DLL 的 QQCHAT_BOT_DLL，逐次 QQCHAT_IT_ONLY=s21 / s36 / s50 / s52，执行真实 harness DLL。覆盖模型设置 / 保存重启、官方 Secret 拒收、去重事务故障 / 重试 / 重启、平台开关 / 策略 / 模型 mock / outbox；四场景不是全矩阵。
- 时钟合成用例须覆盖同一 fake 的本地 / UTC / 单调时间、两种 Delay 及取消、DTO 默认时间、TTL / 节流、Host 切换及测试恢复、未注入启动行为；只证明删掉三份直接读点不够。现有测试入口只可复用已查询工程，新增测试文件 / 入口名称在代码清单批准后登记，不预造命令。
- 存储合成用例须覆盖旧非秘密配置与非法类型导入、原始 key / TTL、整批重复及中途失败回滚、并发单一 writer、版本冲突 / 秘密 / 审计、持久化失败不发布、提交后应用失败、响应丢失查询、重启恢复；长锁 / 崩溃与数据恢复另列明确操作和时间预算，不用立即异常替代。
- 完整验收再依当前 [CONTRIBUTING](../../../CONTRIBUTING.md)、`.github/workflows/ci.yml` 及 harness 注册项核对全部适用探针 / 50 场景。S2 属 S1 内分支，S42 仍明确排除，不能声称官方完整互通；SDK 8 CI / 当前镜像、资源和回退证据分别建立，本地成功不替代它们。本批未运行任何上述命令。
- 产品验证均在独立进程按名称清除继承配置，不输出值；纯探针使用新 UUID 两套同根，harness 父进程不设覆盖场景根的别名；本地 mock、不启外部模式、不读取真实库 / 报文 / 日志。实际群聊、真实官方或飞书、部署、生产均不在本批范围。

### 10.8 R6-A 文档验收、兼容与回退

- 本批接口版本：`R6-A-readiness-v1` 仅为协调记录，不是新运行接口版本；保留 R5-dedup-v1。C1～C5 尚无消费者签署，不标为冻结或行为验收通过。
- 文档验收检查链接、本节逐文件表与实际路径 / 哈希、现状与候选标记、表格 / 围栏 / 空白、`git diff --check`、唯一文件变化、HEAD / 暂存 / 状态 / 19 项证据绑定。文档通过不替代产品、安全 / 架构或无上下文读者测试；按本阶段不创建执行 Agent，未做独立读者测试。
- 兼容影响：无源码 / 配置 / schema / key / 名称编辑 / 秘密策略或运行产物变更；不删除旧文件、不新增项目引用、不启动 Host。不是旧二进制 / 新数据、部署或生产恢复演练。
- 回退预案：只逆转本批页首索引和第十节片段；开始副本只用于定位 / 比对，不能覆盖整份总纲。后续编辑或归属不明先协调；不回退 R1～R5、不使用强制 checkout / hard reset 或清理未跟踪文件。副本完整性核对与实际回退分开记录。
- 待负责人：核收本批文档；安排 C1～C5 的消费者 / 06 早期确认及下一批确切写权 / 合成验证。R6-A 不授权 R6-B 或其它代码，Wave 1 和下游继续冻结；Web 只能在完整后端门禁齐全后提交负责人批准。

命令、时间及最终指纹核对在本节下方追加；仅含源码 / 文档和安全元数据，不读取生产内容。

### 10.9 本批命令与交付核对

所有 shell 在唯一仓库根使用 pwsh；以下只有静态查询 / 文档检查，没有构建、项目探针或 harness 执行。原生命令检查退出码；git no-index 对照的 1 表示发现差异，不是产品测试失败。

| 编号 | 命令 / 对象 | 时间（2026-10-08 / UTC+08:00）/ 退出码 | 结果 |
| --- | --- | --- | --- |
| R6-V1 | git rev-parse / status --porcelain / diff --cached --raw / ls-files；安全清单及 19 项绑定，csproj XML | 21:34:12；各原生命令 0 | HEAD / 暂存 / 313 状态 / 510 清单与上轮一致；19 绑定匹配，五工程引用按 10.2 登记 |
| R6-V2 | 定向源码路径 / 调用点 rg、Get-Content、Get-FileHash；三对 git diff --no-index | 21:34～21:36；rg / git 文件枚举 0，三次对照均 1（预期），pwsh 查询 0 | 21 对中 18 相同 / 3 不同，六个宿主独有文件；实际端口、旧 Host 链、时钟与测试入口复核。未运行产品 |
| R6-V3 | 文档链接 / 表格 / 围栏 / 尾空白、R6 确切路径存在性；git diff --check | 21:41:12；0，git 0 | 追加交付表前 943 行；35 表、4 围栏标记、5 本地链接、62 个 R6 明列 src/tests 路径均通过 |
| R6-V4 | 文档片段逆向重建的字节哈希检查 | 21:41；pwsh 1 | 检查脚本误用 CRLF 重建，与原 LF 字节不符；不是文件丢失或产品失败，未恢复 / 修改文件 |
| R6-V5 | 内存比较原始 / LF / CRLF、结尾及 BOM 候选，检查开始 SHA-256 | 21:42:34；0 | LF、一个结尾换行、无 BOM，117207 字节与开始 SHA 完全匹配；只逆转页首及删除第十节即可恢复开始字节，原一至九节保留；无实际回退 |
| R6-V6 | 重做全部安全清单 / 状态 / 暂存 / HEAD / 19 绑定 | 21:41:56；原生命令及 pwsh 0 | 510 项仅总纲变化，其余 509 项一致；状态、暂存、HEAD 均不变，19 绑定仍匹配 |

- V6 对应追加本小节前的文档快照：清单 SHA-256 `9685480768D7F68F5888F0EB272CA87DEB53257D0F68C3CB569DC1341DE26BEE`，总纲 SHA-256 `5F68115EF261AA7852F5AF5569301999CA18256EC4C1B77BC86B794383DF324C`。状态 / 暂存摘要仍为 10.1 的值；这是明确时间的中间快照，不是最终文档自引用哈希。
- 本节追加后再做文档及边界核对，最终总纲哈希在聊天交付中报告，不写入自身。没有暂存、提交、推送、发布、部署或生产操作，没有创建执行 Agent；本轮全部成果限总纲页首索引及第十节。
- 00 结论：R6-A 的文档协调交付完成，待负责人核收；第一阶段仍不能直接启动代码实施。下一候选是 C1 时钟契约只读评审，只有完成确认并另批确切代码 / 测试路径后才能施工；不将本次“继续”扩展为 Wave 1、02/03、ZBA 或 Web 授权。
- R6-V7：`2026-10-08T21:44:21.9940637+08:00`，最终登记表完成后的文档 / 路径 / 21 对交接一致性、片段逆向哈希及 git diff --check 均通过（pwsh / git 各 0）；36 表、4 围栏标记、5 本地链接、62 明列路径，21 对 / 18 相同与当前源码一致。`21:44:23.0478694+08:00` 全清单复核：仅总纲变化，另 509 项、HEAD、313 状态、暂存及 19 绑定不变。该文档快照清单 SHA-256 `38EBD654BD803955895041186B21CFE15DD8637B3B0F385BA1AD186B79FEA87A`；追加此记录后只再核对文档 / 范围，最后哈希交付于聊天，不冒称新产品测试。

## 十一、C1 时钟显式绑定（Agent-00）

### 11.1 授权、准入与认领

- 负责人对上轮 C1 候选语义、13 个确切路径及定向合成验证答复「确认」。00 独占本批全部写权、最终合入共享文件；不创建其它 Agent、不移交旧文件，不授权提交、推送、部署、发布或生产。
- 准入 2026-10-09T12:23:15.0478084+08:00；HEAD `1f3181e21e8d1f9531e41066b033d8d60218d580`，main；313 条既有状态（暂存删除20、未暂存删除92、修改26、未跟踪175）全部保留。510 项安全清单 SHA-256 `61F69E3101B382FC5A63DE5AA9AAAE68834E057E6F66937A6F90530CCE14C93A`；状态 `9353FF0259B28EFB53571685EE09CF8147C503A33BADBCEBE1F76BDFE40E41CA`；暂存 raw `65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC`，与刚完成的只读评审一致。
- 十个既有源码/测试文件保存原始 gzip 字节，总纲保存已核对的 LF 文本与开始哈希，两个新文件开始不存在；副本仅用于本批片段逆转，不备份真实数据。全部既有改动不计为本批成果。

| 执行者 | 本批确切写入路径（仓库相对） | 开始 SHA-256 |
| --- | --- | --- |
| 00 | `src/BotAgent.Platforms/Common/Clock.cs` | `574031196898AA2D4C0DB75078963931F9DEE39CA0451A93EEBE51DD6177A693` |
| 00 | `src/BotAgent.Model/Clock.cs` | `3BECD996D39482EFEB18C2F51BEC6C42C9F614A8CF4F94CADE95BFEEC8EA8173` |
| 00 | `src/BotAgent.Storage/Clock.cs` | `34598EDDC2A12C8CCC4B76ADB7B54213DD0A07A3AE1B9449BC4142F33A6A959C` |
| 00 | `src/BotAgent.Headless/Adapters/Time/ClockBindings.cs` | `MISSING` |
| 00 | `src/BotAgent.Headless/Host/CompositionRoot.cs` | `7FCB88962F82A7D56F105B23AD0CDEB850765665595476E66C850B8628972810` |
| 00 | `tests/BotAgent.SafetyProbe/Program.cs` | `2078EE20FC97787F9384990CF183C3DA435F529B1E339DE80AD909DF849B7EBE` |
| 00 | `tests/BotAgent.ProductionSpecProbe/Program.cs` | `30B4FF1C73B071356D706539EAE1359EF7B0646724001AC54982298BA2E657EF` |
| 00 | `tests/BotAgent.FeishuRemediationProbe/Program.cs` | `81B7FD7C8A857FE1C39E69FB8A6438269E2EC9BA152B6B8BBC2F43329FEAC544` |
| 00 | `tests/BotAgent.ChaosFaultProbe/Program.cs` | `AF615140DB34FAAFE9872A34289F8C3B3C51F9CDDC7DDD57654BBCDDF495434E` |
| 00 | `tests/BotAgent.ReviewRemediationProbe/Program.cs` | `BED2286570328CFA38BBA21E6032C1465B72027EB4CF60E2913E933565906C12` |
| 00 | `tests/BotAgent.ReviewRemediationProbe/ClockContractTests.cs` | `MISSING` |
| 00 | `tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj` | `9C43DEDAC1B0F2268FC88540193374C92B1A4AFAF44D23B8EBB3E0F7A9112FF1` |
| 00 | `docs/plans/agents/00-orchestration-and-dependency-graph.md` | `C692E2F2BEC6FB470D7A75CA8A69C9078DD1F6D6D20AE6A1CD4875EC5DE0AF98` |

### 11.2 局部契约与验证边界

- 局部版本 C1-clock-v1：IClock 六个成员与宿主 Clock.Current/Use/UseSystemClock 保留；三模块各提供公开 PlatformClock/ModelClock/StorageClock 的 Initialize(IClock) 与 Current，内部 Clock 只转发，不再创建系统实现。未初始化读失败 clock_not_initialized；null 拒绝且无状态改变，同实例初始化幂等，另一实例拒绝 clock_already_initialized。
- Host ClockBindings.Source 为稳定 IClock 转发对象，每次调用捕获宿主 Clock.Current 一次，支持 Use(fake) 及恢复的传播；InitializePlatforms 在 BootstrapSettings/Build 的业务操作前绑定。全局替换仅保证单次调用原子；不承诺多个时间读取为事务快照，不在线重绑模块。
- 保持本地/UTC/单调毫秒和 Delay 取消/参数语义；Domain 字段 default 及显式 now、局部 Func<DateTimeOffset> 替身不变。Core、SystemClock、护栏、CI、Docker、Host 项目引用均不改，不用改名或搬到 Core Domain 外绕过整个 Core 的领域检查。
- 00 按01端口/存储、02/03消费者、06组装职责静态交叉核对；负责人确认仅冻结本批局部语义，不冒称其它 Agent 实际签署全局 C1～C5 或 Wave 1 准出。Model/Storage 仅在独立合成探针绑定及使用；宿主仍未采用它们。
- 已确认测试 seam：公开 IClock/Clock 替换、模块绑定与真实本地通道/模型冷却及提示/合成仓储、DTO 默认值、真实 Headless 入口。未初始化负例在独立子进程运行；不反射业务内部、不用真实数据。适用探针 Review/Safety/Production/Feishu/Chaos、Architecture；集成前独立重建 Headless，再重建 harness，S21/S36/S50/S52 为获批定向诊断而非全矩阵。
- 原架构四项保持阻塞；时钟修复后仍须保留重复存储 SQL/IO 红灯，不放宽阈值、不删断言。出现清单外需求、接口冲突、新失败未定位则停止受影响部分。

### 11.3 执行记录（持续追加）

本节登记真实命令、时间、退出码及覆盖，不先写通过。Windows 使用 pwsh，每个原生命令检查退出码；验证进程按名称清除继承配置，纯探针使用新 UUID 两套合成数据根，harness 不继承覆盖场景根的别名，固定本机 mock，不运行外部模式。

命令日期均为2026-10-09 / UTC+08:00，SDK10.0.101、目标net8.0；本地不替代SDK8 CI。下面仍属获批定向诊断，不绕过整体架构红灯解锁波次。

| 编号 | 命令/对象 | 时间/退出码 | 实测覆盖 |
| --- | --- | --- | --- |
| C1-V1 | `dotnet run --project tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release` | 12:24:47～12:25:08；1 | 修复前原34通过、新增传播负例失败；实际本地outbox不受Host fake控制 |
| C1-V2 | 同V1，最小绑定后 | 12:25:52～12:25:58；0 | 35 / 0；原34完整保留 |
| C1-V3a/b | 同V1，扩展测试编译 | 12:29:17～26；1；12:29:49～51；1 | 新测试误用User/Other枚举，两次CS0117；00归属、未执行断言；查实际Peer后修正，未删除检查 |
| C1-V4 | 同V1，最终测试 | 12:29:59～12:30:07；0 | 143 / 0，两个隔离子进程10 / 0、12 / 0；后续V8再次执行 |
| C1-V5 | `dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release` | 12:30:49～12:30:59；1 | 122 / 2；时钟位置及总量通过（3），SQL265>137 / IO98>62 保留红灯 |
| C1-V6 | `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild` | 12:30:59～12:31:03；0 | 8既有CS0108 / 0错误 |
| C1-V7 | `dotnet build BotAgent.slnx -c Release` | 12:31:04～12:31:06；0 | 0警告 / 0错误（增量）；不抹掉重建警告 |
| C1-V8 | `dotnet run --project tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release` | 12:31:28～12:31:32；0 | 143 / 0；原34 + 新109，另隔离子进程10/0、12/0 |
| C1-V9 | `dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release` | 12:31:33～12:31:49；0 | 594 / 0，原断言保留 |
| C1-V10 | `dotnet run --project tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release` | 12:31:50～12:31:54；0 | 62 / 0，原断言保留 |
| C1-V11 | `dotnet run --project tests/BotAgent.FeishuRemediationProbe/BotAgent.FeishuRemediationProbe.csproj -c Release` | 12:31:54～12:31:59；0 | 退出0但此摘要未保留全部PASS计数，随后独立复跑 |
| C1-V12 | `dotnet run --project tests/BotAgent.ChaosFaultProbe/BotAgent.ChaosFaultProbe.csproj -c Release` | 12:31:59～12:32:05；0 | 43 / 0；既有未使用事件CS0067，正文保留 |
| C1-V13 | `dotnet run --project tests/BotAgent.FeishuRemediationProbe/BotAgent.FeishuRemediationProbe.csproj -c Release` | 12:33:07～12:33:11；0 | 24 PASS / 0，原15 + R5新增9不变 |
| C1-V14 | `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild` | 12:33:11～12:33:15；0 | 8既有CS0108 / 0错误；真实独立重建 |
| C1-V15 | `dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release -t:Rebuild` | 12:33:16～12:33:19；0 | 2既有CS8602 / 0错误；不替代Headless重建 |
| C1-V16 | `$env:QQCHAT_IT_ONLY='s21'; dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | 12:33:20～12:33:27；0 | 37 / 0，真实模型设置保存/重启链 |
| C1-V17 | `$env:QQCHAT_IT_ONLY='s36'; dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | 12:33:28～12:33:35；0 | 98 / 0，官方Secret拒收/环境专属及保存重启 |
| C1-V18 | `$env:QQCHAT_IT_ONLY='s50'; dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | 12:33:36～12:33:38；0 | 14 / 0，真实飞书去重故障/重试/重启 |
| C1-V19 | `$env:QQCHAT_IT_ONLY='s52'; dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | 12:33:39～12:33:45；0 | 119 / 0，平台策略/入站/mock/outbox/重启 |

- 新源码/测试警告为0；Headless八个CS0108、harness两个CS8602的来源文件与开始安全清单一致。Chaos的CS0067属于原SyntheticOneBotTransport.OnStateChanged未使用事件；仅插入初始化一行，12:35:49逆向正文哈希确认原文件完整保留；本批不消警。
- 原生命令退出码逐项核对。工具问题单列：开始合并gzip输出被截断，JSON解析失败后改逐文件保存，没有产品修改；apply_patch曾拒绝同路径delete/add组合，随后精准Update；执行器eval作用域问题发生在shell前；旧工程文档链接指向不存在路径的rg查询退出2已停止，不据此造接口或放宽护栏。均不是产品断言结果。

### 11.4 实施、真实调用链与覆盖

- 三个模块保留命名空间和内部Clock消费者入口，只把时间来源转向已显式绑定的IClock；公开初始化用Interlocked.CompareExchange做一次性身份绑定，Current用Volatile.Read，无系统回退。稳定Host Source不读取配置/数据、不查服务注册表，每次调用转发到一次捕获的Host Clock.Current；旧Clock/SystemClock及IClock源码逐字未改。
- Host生产链为 `Program.Main → CompositionRoot.BootstrapSettings:69 → ClockBindings.InitializePlatforms → PlatformClock.Initialize(Source) → 旧数据库/配置/日志 → Build:89 → BuildChannelLayer`。初始化先于业务操作，Build直接入口同样幂等绑定。随后Platforms本地/飞书/OneBot/官方的现有Clock读点走同一Source；宿主仍不引用或采用独立Model/Storage。
- 新Review测试通过公开绑定/IClock、本地入站和出站、Model真实熔断器与提示装配、Storage真实会话创建和UTC配额日界线、Host BotConversation默认时间及ThrottledLog验证；Domain ChatMessage/AgentSession保持default。两个子进程在任何库/导入/文件日志之前返回，未初始化业务读明确失败，竞争只保留一个绑定身份。六个成员、取消/非法参数、在途旧Delay、并发Host替换、SystemClock和finally恢复均覆盖；显式局部Func时钟不被全局取代。
- Review的ModelModule/StorageModule编译别名只加在该测试项目，避免同FQN解析到Host旧实现。探针绑定新模块并调用其真实实现，不称为Headless采用。其存储与原探针均使用自己新UUID的两套同根；不是全局writer/保存事务验收。
- 真实Headless行为证据由S21/S36/S50/S52给出：独立重建后启动真实DLL，走真实管理保存及重启、平台入站/模型mock/outbox、SQLite去重故障恢复；不是仅程序集编译。harness父进程清除两套数据别名，显式QQCHAT_BOT_DLL绑定本仓库Release，单个QQCHAT_IT_ONLY，场景自行指定合成根和本机mock。没有外部模式、真实群聊/成员/协议/数据库正文/日志或环境秘密值读取。
- 架构默认124项与29扫描器自检全部保留，122 / 2：系统时间只在原SystemClock文件，总量3；重复存储SQL265>137、IO98>62未解除。此为本批重新运行结果，不复用R5历史数字；Core全工程仍按原领域护栏检查，阈值/允许位置/断言均未修改。

### 11.5 兼容、回退与准出结论

- 已确认兼容收紧：独立模块外部调用方须在首个时间读取前Initialize(IClock)，否则明确失败；不恢复静默DefaultClock。现有公共业务构造签名、Host Clock六成员/替换/恢复、领域时间默认、非秘密配置、schema、存储/key、显示脱敏/nameRaw及官方Secret环境专属政策不变。
- 回退仅逆转本批初始化行/Review接线与别名、三个Clock差异及本总纲片段；两个新增文件只有核对无后续修改后处理。12:35:49七份既有文件在内存删除本批片段后SHA完全等于开始，证明Host既有R5接入、原探针断言和项目输入保留；本总纲原一至十节同样匹配开始哈希（随后当前状态索引另作本批更新）。不得整文件恢复覆盖后续编辑，不使用强制checkout、hard reset或清理未跟踪文件。
- 回退会恢复额外系统时钟及fake割裂，必须报告风险并由负责人决定；没有实际源码回退、旧预编译产物混搭或部署/数据恢复演练。绑定静态状态需随进程重启恢复，不能在线卸载补丁。
- 未覆盖：全harness/所有专项/SDK8 GitHub CI、明确排除的S42、真实平台互通、真实长锁/全局writer/统一配置版本、Model/Storage宿主采用、CancelAfter/协议握手的BCL真实计时替换、资源/当前Docker镜像和部署回退。只读CLI/ZBA/Web仍未施工；R4镜像不代表当前产物。
- 00核对：C1局部补丁及约定合成证据完成，提交负责人核收，不是Wave1验收。Wave1及02/03/后续波次/Web继续冻结。下一候选先做C2/C3唯一数据库及配置类型/保存契约只读评审，需另批；不自动搬库、增加宿主引用或删除旧文件。

### 11.6 指纹与证据绑定

- 2026-10-09T12:35:49.2504763+08:00：512项安全清单相对510项开始仅改变11个既有认领文件、新增2个认领文件；其余499项逐项一致，清单外变化0。319条状态保留全部313开始状态，新增4个tracked修改和2个未跟踪项；HEAD及暂存raw不变。此时清单SHA `833C0D4B319D7DB5891E0A7F5527EA5408388DD46AD69AEADACD4CE23F411F6A`、状态SHA `100D94E6DEA22B06969F2B67B82632E5EE5A9948901F737951A4EEF497DC1336`，是最终登记追加前文档快照，不作自身最后哈希。
- 2026-10-09T12:36:31.3612074+08:00：以下12份DLL绑定本次实际命令；Core/Architecture/harness虽哈希与R5相同，本轮确实重建/执行，不从相同哈希推断通过；产品/探针源码随后不再修改。

| 被测产物 | SHA-256 |
| --- | --- |
| `src/BotAgent.Core/bin/Release/net8.0/BotAgent.Core.dll` | `D754B1B38FD92C9F14C193F1198D7612CD51E7EBDAFCAD5AA8905AC788A852FD` |
| `src/BotAgent.Platforms/bin/Release/net8.0/BotAgent.Platforms.dll` | `7BB869F8B1DE1DA742274D358D10F17053F42BC48DCB2DC9E6F5BC2C7D3958B6` |
| `src/BotAgent.Model/bin/Release/net8.0/BotAgent.Model.dll` | `A5823E5A4B54DCB81E1B7C7E6827E9AFF7FBE77262856C9ADA20360A8DB22A20` |
| `src/BotAgent.Storage/bin/Release/net8.0/BotAgent.Storage.dll` | `40241F2BE67DABBE322936BDBE0B3BE7B08947BEDE53A0D072DD85EC30E7D1DD` |
| `src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll` | `0171722E4641530126963F19D5D7EE23A86896081A119C1327AD8EC08977CCE8` |
| `tests/BotAgent.ReviewRemediationProbe/bin/Release/net8.0/BotAgent.ReviewRemediationProbe.dll` | `028D46281DE674F802152AA6A270C7C1CECFAF325AE13718E908170DC1CF58A6` |
| `tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll` | `820223104B113B4B9DF4C24BEF6CF3EE3CED77E7587A741684977BB7F64505FF` |
| `tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll` | `162B59BE77F57839512D596341FAD6D23E64039E30BF7D2C144BCA79C986E942` |
| `tests/BotAgent.ProductionSpecProbe/bin/Release/net8.0/BotAgent.ProductionSpecProbe.dll` | `11A8827C60DFCAA9521F9663DE3D99421880DEF10CF5C2A745A961288FBE2A64` |
| `tests/BotAgent.FeishuRemediationProbe/bin/Release/net8.0/BotAgent.FeishuRemediationProbe.dll` | `8489EF920DBF836002E6C703E8FDEB27FB0645255F931FE2C4C65C1E944E7D65` |
| `tests/BotAgent.ChaosFaultProbe/bin/Release/net8.0/BotAgent.ChaosFaultProbe.dll` | `6CF6B5D2BE9FD56F8F9D95C591EEE2C8FF4BA01F37FE003C2141A77C25DBA954` |
| `tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll` | `40268FF4F2F13D0A6A1E4CBB396397EDCDC2E71C0F8F74269FF5DF0C04E8D5C8` |

| 本批源码/测试（不含文档自引用） | SHA-256 |
| --- | --- |
| `src/BotAgent.Headless/Adapters/Time/ClockBindings.cs` | `94034E00CD6BE381255DD3D0F709C3709E1415642CB078784C1E244AE55E3EB1` |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `47763388337E6DFB218A2C6978D49A60713953BD85B9EB2C5F86C98975BF5609` |
| `src/BotAgent.Model/Clock.cs` | `EDD8BC6FACEA451A12D04714CFAFB12168741E9642D79F40D225A9BAD061FE38` |
| `src/BotAgent.Platforms/Common/Clock.cs` | `0AC7265388B844AE092D908679255EE8E46CADD56C89022D77E391B47B74CCC8` |
| `src/BotAgent.Storage/Clock.cs` | `2173901B5153309301B857077B081EAA5B009356E06503A20C36081F2CE35DD6` |
| `tests/BotAgent.ChaosFaultProbe/Program.cs` | `76F2EF2EF2C6E0BEAF8C5B82D263D466CBCE820A1C01118DA535EEDC82A0011D` |
| `tests/BotAgent.FeishuRemediationProbe/Program.cs` | `98403361FEC6C8497DD758C299A162BBA9E3FCFC7D97B5EEF33DE0C8FC05C47E` |
| `tests/BotAgent.ProductionSpecProbe/Program.cs` | `D1D26F283E4BD721243895B28812EC7085DF6691E026E0AE38B970108EB102FD` |
| `tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj` | `8D074C6270B21922777CC496180FAECB3336E74E91B9BB9F839255E0315CD9E4` |
| `tests/BotAgent.ReviewRemediationProbe/ClockContractTests.cs` | `28C9F115F93FB6171F93C948824E9782FC64761FEEF8F7F342728B2FABE72ABB` |
| `tests/BotAgent.ReviewRemediationProbe/Program.cs` | `3137DC7D7AD986C1325DEC7C13ABE68125514E63884897F27F4E44E846274011` |
| `tests/BotAgent.SafetyProbe/Program.cs` | `18EE63566596017F46FDF614D7FC8E6FB8098F5A50099977460D7AEE9E6C44CB` |

文档/路径/范围及最终指纹核对随后追加；无暂存、提交、推送、部署、发布或生产操作。开始认领副本仅用于本任务片段核对，不整理既有输入。


### 11.7 最后文档与边界核对

- C1-V20：2026-10-09T12:35:49.8194399+08:00～12:35:49.9658860+08:00，七份既有源码/测试/项目的本批片段逆向正文哈希、总纲原一至十节前缀核对及git diff --check均通过（pwsh/git 0）；没有实际回退。
- C1-V21：12:39:34.3193307～12:39:34.5010367 +08:00，三份模块Clock开始gzip副本内存解压SHA通过；总纲逆转三个当前索引修订并删除第十一节后SHA与开始完全一致；40表、4围栏标记、5本地链接、尾空白及git diff --check通过（pwsh/git 0）。原始正文核对不是旧产物/部署回退演练。
- C1-V22：2026-10-09T12:40:10.9221683+08:00，512项清单仅本批13路径变化，另499项与开始逐项一致；产品/测试源码相对最后测试后快照无漂移；HEAD、暂存raw和313既有状态保留，最终319状态（暂存删除20、未暂存删除92、修改30、未跟踪177）。本段追加前清单SHA `6BB667494F639D415A87AB88744A3A06C0E94EE2935A61BCC1A8B448785A1290`、总纲SHA `3B379C79508AEF0B31BA68A806029FA45087F025BF18F14705A9629C5D6906CF`，状态SHA `100D94E6DEA22B06969F2B67B82632E5EE5A9948901F737951A4EEF497DC1336`；均标明快照时间，不是总纲最后自引用哈希。
- 12:40:11.5411353～12:40:11.5540815 +08:00，12份被测DLL哈希全部与11.6一致；rg实际接口/Host接线/文档位置核对退出0。随后只追加此记录并复核文档/范围，不改代码、不重标产品测试。
- 00收口：本批13路径补丁及约定定向验证完成，施工写权收口，无临时交接/执行Agent；最终文档哈希交付于聊天。C1局部契约与证据待负责人核收，SQL/IO门禁及Wave1/下游冻结保留。C2/C3只读评审、后续代码或验证均须另行批准；无提交、推送、部署、发布或生产操作。

## 十二、C2/C3 只读评审落档与方向确认（Agent-00）

### 12.1 授权、唯一写权与准入

- 上轮00请求确认C2/C3候选方向并批准单文件协调登记，明确不同时授权代码施工或Wave1；负责人本轮答复「批准，并进行你最建议的方向」。按建议先收口唯一数据库/迁移边界，再处理配置类型、秘密事务及提交/应用语义；本轮只有文档写权。
- 执行者/共享最终写入者/交付对象：Agent-00 / Agent-00 / 负责人。唯一认领路径为 %WORKDIR%\bot-agent\docs\plans\agents\00-orchestration-and-dependency-graph.md；精准更新三处当前索引并追加第十二节。无源码/测试/项目引用写权，无构建/项目测试授权；不创建执行Agent、不移交写权。
- 准入2026-10-09T18:08:45.5928876+08:00：main，HEAD 1f3181e21e8d1f9531e41066b033d8d60218d580；319条状态（暂存删除20、未暂存删除92、修改30、未跟踪177）。512项安全清单全部与上轮18:00:51只读交付一致；保留既有变更，不重记为本轮成果。
- 开始清单SHA-256：97BD74BA21096A11A6D1D6D935D0D858CABE1904D752AB9E3CB024336CC024C1；状态SHA-256：100D94E6DEA22B06969F2B67B82632E5EE5A9948901F737951A4EEF497DC1336；暂存raw SHA-256：65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC。算法同8.6/11.6，同口径逐项比较，不遍历运行数据或读取日志/数据库。
- 总纲开始SHA-256：6EFE9FA4F48D36DD4640ED5C06E89F4669816F652316C3CFFDFA2F9D8E9CA1D4，163943字节；18:08:46会话保存gzip原始字节，18:10:05内存往返哈希匹配。副本只用于片段定位，不可覆盖后续编辑或全仓库回退。
- 已依次重读两级AGENTS、总规划及本手册，枚举未找到目标目录额外AGENTS；复核01/06手册。全部Windows shell使用pwsh；未读取环境秘密值、真实群聊/人物档案/协议正文/未筛选日志，未调用产品入口。

### 12.2 波次状态与证据有效性

| 波次 | 本轮静态复核 | 缺口及前置依赖 |
| --- | --- | --- |
| Wave 1 | Core无业务引用；Storage→Core；Headless→Core/Platforms，未采用Storage。21对仓储仍为18相同/3不同 | C2/C3实际签名、消费者、兼容/回退及写权未冻结；唯一数据库接入、整批保存和架构门禁未完 |
| Wave 2A | Platforms→Core，宿主已引用；C1平台时间绑定和R5过渡去重存在 | 四平台整体生命周期/能力未验收；Wave1准出且写权不交叉后02/03才可获批并行 |
| Wave 2B | Model→Core，宿主未直接引用 | 新模型真实请求链、配置/管理契约与旧路径交接；Review测试别名接入不等于生产采用 |
| Wave 3 | Engine工程未建立，回复/工具/插件仍在宿主 | Wave1/2准出、端口/门禁/任务契约；插件首阶段在Engine内部 |
| Wave 4 | Mcp工程未建立 | Wave3、实际协议/SDK冻结、出网/取消/恢复及互通证据 |
| Wave 5 | 现有Host/旧管理接口存在，web资源及两脚本仍内嵌 | 06从C2/C3参与；最终需Wave1～4、必选ZBA、安全、全矩阵、资源与回退 |
| Wave 6 | 独立web未建立，无写权 | 后端完整门禁后00核对、负责人明确批准；本轮不解锁 |

- 最近项目运行仍为C1-V5：历史122通过/2失败，SQL265>137、IO98>62，时钟总量3通过。本轮未运行探针/重算指标；受影响源码指纹未变，红灯无新解除证据，不能称本轮架构验收。
- 18:09:14核对11.6的12份DLL全部匹配，只证明未漂移，不证明新运行、宿主采用或整体通过。R4镜像绑定更早输入，不能作为当前镜像/资源/回退证据。

### 12.3 C2 当前接口、迁移差距及风险

本节是源码事实或明确标记的静态风险，不是新动态失败；第十节保留其历史日期，不覆盖成新成绩。

| 实际文件位置（绝对路径） | 当前实现与后续要求 |
| --- | --- |
| %WORKDIR%\bot-agent\src\BotAgent.Storage\AppDatabase.cs:24,27,53,82,139,170,191；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\AppDatabase.cs:24,52,120,159 | 同FQN、不同程序集静态状态，目前仅旧宿主采用。真实读入口是Query/Scalar，10.4的Read是历史泛称，不存在同名方法。Storage WriteGate不等于全进程writer已统一；Open/Exec公共表面、迁移及备份需逐项评审 |
| 两份AppDatabase去重入口 | Host已有R5整批方法，Storage仍仅单key。迁移保留原始key、Ordinal、TTL边界、任一重复整批不新增、中途失败全回滚，不改回独立事务循环 |
| %WORKDIR%\bot-agent\src\BotAgent.Storage\AppPaths.cs:5,22,27；%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\AppPaths.cs:12,23,51,65 | 环境别名顺序相同但解析语义不同：Storage每次解析、优先既存runtime、/data仅查存在；Host缓存根、找.git并检查/data可写、创建目录。须冻结一次确定的运行根/路径政策，不能假定上下文等价 |
| %WORKDIR%\bot-agent\src\BotAgent.Storage\AppDatabase.cs:266,272,557,589,671,678 | 先建表再读schema版本、迁移至9，部分DDL/version分开写，旧迁移有DROP member_messages分支；整套崩溃恢复、较新schema拒绝及合成备份恢复未证明。VacuumIntoBackup有独立连接，不伪装进写事务 |
| %WORKDIR%\bot-agent\src\BotAgent.Storage\LegacyJsonImporter.cs:88；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\LegacyJsonImporter.cs:88 | 新版仅语法解析，旧版反序列化AppSettings；保留类型校验，通过评审seam避免Storage→Host。写入/归档/完成标记非一笔全局事务，部分导入/崩溃/重试政策待确认 |
| %WORKDIR%\bot-agent\src\BotAgent.Storage\StorageOptions.cs；%WORKDIR%\bot-agent\src\BotAgent.Storage\FileLog.cs；%WORKDIR%\bot-agent\src\BotAgent.Storage\Clock.cs | Options只有定义，没有options初始化调用，不臆造Initialize(options)。未来先绑定显式时钟再导入/备份；StorageLogging默认Console，须接入宿主安全日志政策 |
| %WORKDIR%\bot-agent\src\BotAgent.Storage\IPluginStore.cs；%WORKDIR%\bot-agent\src\BotAgent.Storage\PluginStore.cs:9,44,48；%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Plugins\PluginManager.cs | IPluginStore在Storage，不是Core应用端口，未发现生产消费者。每实例锁、读写分开可能导致多实例丢更新（静态风险，未动态验证）；Host启停仅内存，不称插件配置已统一持久化 |

两份数据库DefaultTimeout30秒/busy_timeout30000，WriteAsync仅Task.Run，无取消参数或有界队列。R5立即故障不覆盖长锁响应预算；C2须定义锁顺序、嵌套事务限制、长锁/取消/关闭和恢复预算，不悄悄改超时或宣称WAL提供全局原子性。10.3的21对交接仍为候选；六个Host独有文件均保留，禁止整体删除Persistence目录。

### 12.4 C3 真实保存链及静态缺口

1. **保留正确边界**：%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\SettingsBox.cs:46～64同Box锁内候选→mutate→persist→发布→published；persist失败不发布，published失败不回滚已提交。%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\SettingsStore.cs:58～90把配置及指定AuditLogStore的一条审计写在同一SQLite事务，优先复用，不重造同义接口。
2. **不能原样移Core**：%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Ports\ISettingsRepository.cs依赖Host AppSettings/Ops；%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\AppSettings.cs:17,901～908继承Platforms，Snapshot在内存仍携带运行凭据，JsonIgnore不等于外部安全DTO。原样搬Core会反向依赖。%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Ops\IAuditChain.cs只有Append/Verify；AppendInTransaction属于具体AuditLogStore，不虚构端口方法。
3. **最高优先候选风险**：%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Panel\WebUiServer.Settings.cs:131～147,298～324,414～422,593～599,725～732的mutate会先保存Feishu/Tts/Agent/Api秘密；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\SecretsStore.cs:95～120每次独立事务、默认失败返回false，调用处不检查。后续字段校验/settings事务失败可能留下秘密更改；秘密失败也可能继续发布。仅静态风险，未在本轮复现，不归为C1引入回归。
4. **提交≠应用**：旧链HandleSettingsSaveAsync→SettingsHotReload.ApplyRuntimeSettings→SettingsBox.ApplyPersisted→SettingsStore.Save→AuditLogStore.AppendInTransaction→发布→RebuildRuntime→afterPersist。%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Settings\SettingsHotReload.cs:75～121顺序更新派生状态；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Panel\WebUiServer.Audit.cs:28～43预写result=applied；提交后失败仍可能HTTP500/settings_save_failed，无持久化操作结果查询。须区分已提交/已应用/应用失败/需重启，不能从500推断事务回滚。
5. **审计/跨介质**：轮换审计是提交后的新事务，非秘密/配置整批原子；Settings文件:167的ttsKey与:418的ttsApiKey不一致，显式清空/飞书encrypt等审计覆盖也未证明。同文件:30～58的TTS外部文件失败只记日志，不得把DB成功称为文件已应用；恢复应来自持久化结果，不伪装跨介质回滚。
6. **版本/幂等/输入**：settings仅id/json/updated_unix，无外部版本/请求结果账本；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Panel\WebUiServer.cs:854～866全量ReadToEndAsync，此处无请求体限额；旧保存无全局未知字段拒绝，null/空串随字段区别处理。新ZBA严格输入、秘密显式动作须与旧HTTP兼容一起冻结，不擅自收紧旧API。
7. **其它写入口**：%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\BotConfig.cs:29～44启动可能普通Save回写环境投影，须区分环境和用户持久配置、定义版本/审计语义；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\ModelProviderStore.cs的UpsertMetadata/SaveCircuit各自事务，01/03/06区分配置元数据与熔断运行态，不把每次熔断计成配置更新。
8. **官方Secret**：Core旧LoadOfficialSecret/SaveOfficialSecret及两个仓储定义仍在，未查到生产调用；保留旧行不允许新API写入/恢复回填。本轮不删除API或旧行，兼容清理另批。官方Secret仍只取直接环境值，面板拒收、不回显。

### 12.5 已确认方向与冻结待办

本轮协调版本为C23-review-v1，不是运行接口版本。负责人确认方向不等于01～06实际签署；00按职责静态交叉核对，继续保留R5-dedup-v1与C1-clock-v1局部语义。

| 议题 | 本轮负责人确认的方向 | 冻结前尚缺 |
| --- | --- | --- |
| C2数据库/迁移 | 唯一Storage数据库状态/写协调、Host一次初始化；保留原始路径/key/R5和旧类型校验；较新schema不静默降级/修改 | 实际初始化/端口签名和文件、路径/日志/时钟输入、锁顺序/嵌套/备份；迁移/崩溃恢复、长锁/取消预算、旧产物兼容及交接 |
| C3配置/保存 | 外部非秘密快照分离凭据；全部修改先暂存并整批校验，同一SQLite事务提交配置/版本/允许DB秘密/必要审计 | 精确类型/消费者，哪些秘密/模型/插件进入本批，环境投影政策、原审计端口兼容与事务seam、跨分区校验/大小限制/旧HTTP兼容 |
| C3版本/恢复 | 缺省保留、清空/删除显式；新契约未知字段/非法类型拒绝；乐观版本、持久化幂等与响应丢失查询；提交/应用/失败/重启区分 | 幂等主体/作用域/TTL/参数指纹/重试权限；跨介质恢复顺序、结果查询和重启恢复DTO/错误/API版本；数值及签名未冻结 |
| 消费者 | 01提契约，02平台/去重、03模型、04仓储/插件、05工具/MCP、06提前检查HTTP/DTO/启动/恢复，00登记共享合入 | 真实参与者确认、版本/文件/时间、合成用例及兼容/回退；当前无执行Agent、无消费者签署，不用文档方向替代 |

### 12.6 写权、共享补丁与下一最小切片

- 当前只有12.1的00单文档认领；01～07代码/测试集合为空，旧文件移交为空。10.3的21对、10.5角色边界仍有效，字节相同不授权删除；实际代码批次须另报确切新增/修改/删除路径、开始哈希及验证授权。
- **下一最建议任务（只读、待批准）**：准备C2唯一数据库接入最小切片，连同C3非秘密配置/审计seam的真实签名候选和消费者矩阵；先解决同FQN遮蔽、双初始化、路径/时钟/日志与R5兼容，不先删除旧文件。提交逐文件补丁方案和真实合成入口，再请求代码授权；不启动完整Wave1或全部角色。
- C2已有审查路径：%WORKDIR%\bot-agent\src\BotAgent.Storage\AppDatabase.cs、%WORKDIR%\bot-agent\src\BotAgent.Storage\AppPaths.cs、%WORKDIR%\bot-agent\src\BotAgent.Storage\FileLog.cs、%WORKDIR%\bot-agent\src\BotAgent.Storage\LegacyJsonImporter.cs；与%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\AppDatabase.cs、%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\LegacyJsonImporter.cs、%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\FeishuWebhookDedupStore.cs对照。仅审查队列，不是已冻结施工集合。
- C3共享审查路径：%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\AppSettings.cs、%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\SettingsBox.cs、%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Ports\ISettingsRepository.cs、%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Settings\SettingsHotReload.cs及12.4所列配置/秘密/审计/启动/供应商/WebUiServer路径。新Core类型/文件尚未定，不把候选名登记成现有接口。
- 01只在另批Core/Storage精确路径施工；02/03须Wave1准出、独立授权且写权不交叉才并行；04/05/07仍锁定。06提供集成方案/补丁，不与00同时写Host。%WORKDIR%\bot-agent\BotAgent.slnx、%WORKDIR%\bot-agent\src\BotAgent.Headless\BotAgent.Headless.csproj、%WORKDIR%\bot-agent\src\BotAgent.Headless\Host\CompositionRoot.cs、共享设置/护栏/公共测试/CI/部署由00最终合入，当前无这些文件写权。
- 合入顺序：契约确认/负例seam→实现→00宿主接入→真实调用链→逐文件旧实现处置→全门禁；每批绑定HEAD+工作区+文件哈希。接口未冻结、文件冲突或兼容不明停止；证据过期/新失败冻结受影响下游。临时共享移交另登接收者/路径/起止时间、暂停其它写入。

### 12.7 后续合成验证、准出与回退（本轮未执行）

- 新用例优先覆盖真实HTTP混合秘密/配置在后续校验或事务故障时零部分写入；配置/版本/允许DB秘密/审计中途故障；同基准版本竞争、幂等同参数重放/不同参数冲突、响应丢失查询/重启恢复；持久化失败不发布、提交后派生状态/TTS外部文件失败可区分可恢复。
- C2覆盖非法类型旧导入、原始key/TTL/整批重复/中途回滚/并发/重启、较新schema拒绝、迁移中断、长锁预算及合成备份恢复。旧SettingsScope的纯mutate失败/同Box并发不替代HTTP秘密副作用/外部版本用例。
- 已查询15个真实测试csproj；优先复用%WORKDIR%\bot-agent\tests\BotAgent.SettingsScopeProbe\BotAgent.SettingsScopeProbe.csproj、%WORKDIR%\bot-agent\tests\BotAgent.FeishuRemediationProbe\BotAgent.FeishuRemediationProbe.csproj，以及实际Review/Safety/Architecture/Production工程和%WORKDIR%\bot-agent\tests\BotAgent.IntegrationHarness\BotAgent.IntegrationHarness.csproj。新增用例文件/公共入口随代码清单另批，不造测试工程。
- 获准后先独立Headless Rebuild，存储批按需重建真实Storage，再solution/适用探针；每个原生命令核对退出码。集成前再次独立重建Headless，再构建真实harness，明确本仓库新DLL+单个s21/s36/s50/s52。架构失败停止准出；红灯下独立定向诊断另批且标非准出。
- 全部UUID合成根/两套路径一致/本机mock，按名称清除继承配置、不显示值；不读真实数据。完整矩阵、SDK8 CI、当前镜像、ZBA安全/资源、产物及数据恢复分别取证；S42仍排除，不以本地局部成功代替。
- Wave1准出须唯一Storage实际采用、无同名旧类型遮蔽/双初始化，真实保存/审计/去重/重启链、兼容/恢复及适用安全/架构/行为门禁完整；01交付后00核对并提交负责人。02/03仍另批；Web须Wave1～5、必选ZBA、安全、资源、回退齐全后负责人明确解锁。
- 文档回退只逆转三处索引并撤销第十二节，先核对后续编辑，不整文件覆盖、不回退C1/R1～R5/既有输入。无schema/key/秘密政策/运行产物变化；副本/逆向哈希不是旧二进制/数据/部署恢复演练；不用强制checkout、hard reset、清理未跟踪文件。

### 12.8 静态命令、文档验收与收口

全部从唯一仓库使用pwsh，原生命令逐项检查退出码。只有查询/文档检查，没有构建、项目探针或harness执行。

| 编号 | 命令/对象 | 时间（2026-10-09 / UTC+08:00）/退出码 | 证据性质 |
| --- | --- | --- | --- |
| C23-RO | 上轮git库存/引用XML/rg/源码阅读、21对/12DLL、git diff --check | 17:56:11～18:00:51；成功原生命令0，无匹配rg1预期 | 512项无变化，未写入/运行产品；已交付只读报告，不称新项目测试 |
| C23-D1 | git rev-parse/branch/status --porcelain=v1/diff --cached --raw/ls-files；512清单/单文件副本 | 18:08:45～18:08:46；各git/pwsh 0 | 上轮最终指纹匹配，登记单文件写权；副本在会话 |
| C23-D2 | csproj XML、rg/Get-Content、21对/6独有/12DLL/旧读入口；git diff --check | 18:08～18:09；成功git/rg/pwsh 0 | 依赖/真实入口和静态风险复核，无产品执行 |
| C23-D3 | 首次SettingsHotReload候选路径查询 | 18:09；pwsh 1 | 不存在后停止；git枚举找到真实Services/Settings/路径后读取/rg 0，不创建错误路径/API，不是产品回归 |
| C23-D4 | gzip内存往返哈希、BotConfig/AppPaths源码阅读 | 18:10:05；pwsh 0 | 开始副本匹配，未恢复工作区/调用业务初始化 |

工具问题单列：上轮Windows字面通配路径rg2/pwsh1，改实际目录/-g后成功；模板字符串语法错误在shell前；只读Host变量冲突pwsh1改变量名后成功。本轮登记及校验脚本各一次模板字符串反引号SyntaxError，均在shell前，未调用shell或修改文件；改字符串表示后再执行。18:17的文档校验pwsh1报告缺少“实际代码”边界标记，既有文字为“真实代码”，改正文明确“实际代码批次须另报”后重验；不删校验项。以上均不记为产品回归。

文档只验链接/表格/围栏/尾空白、明列现有路径、现状/方向/候选/历史标记、逆向片段哈希及512项范围。git diff --check不覆盖未跟踪总纲，须直接校验正文。本阶段不创建执行Agent，未做独立无上下文读者测试，不声称代码/业务验收通过。最终核对在下方追加；最后哈希交付于聊天，避免自引用。

00结论：C2/C3方向确认及单文档协调登记交付；真实接口/消费者/代码写权和验证仍待另批，Wave1和下游继续未验收/冻结。没有暂存、提交、推送、部署、发布或生产操作；本轮成果不包括既有代码或历史测试。

- C23-D5：2026-10-09T18:17:49.9338546+08:00～18:17:50.1949383+08:00，直接文档校验及git diff --check各0；44表、4围栏标记、5本地链接、32个明确现有绝对路径、尾空白0。逆转三处索引并移除第十二节后的内存SHA与12.1开始值完全一致，原一至十一节完整保留；没有实际回退、独立读者或项目测试。
- C23-D6：2026-10-09T18:18:24.1873263+08:00，全部512项同口径清单复核，仅总纲变化，其余511项逐项一致；HEAD、319状态及暂存raw全部保持。此时总纲SHA为8EE80B36F9DF5C9690AD7482365F638DF11255AB3E0904D15DDAD36938FB0B88、清单SHA为545654B30E23F3C5C01028BCF035B33F77F0B5712080C4DDA0A006D37401CCE5，均为本段追加前快照而非最终自引用哈希。随后再核21对/6独有/12DLL及git diff --check，均通过，各原生命令/pwsh退出0，未执行产品。
- 本段追加后仅重做文档/范围核对，最终指纹交付于聊天。00单文档写权本轮收口，无临时交接；下一C2最小切片只读方案、任何代码/构建/项目测试均须另批。下游门禁不因文档通过而解除。

## 十三、2026-10-09 C2-A 唯一数据库接入执行登记

### 13.1 授权、写权和局部契约

- 负责人在本次聊天答复「批准」：批准此前 C2-A 13 路径代码修改及定向合成验证；随后明确「Agent00持续推进，直到阶段一可开工，不用询问我是否批准，你持续推进即可」。本批仍不扩大以下施工集合，不自动授权提交、推送、部署、发布或生产操作；后续批次先登记确切范围，接口不明/冲突仍停止。
- 执行者/交付对象：00/负责人；无子 Agent、无临时共享移交；开始 2026-10-09T18:38:52.0232836+08:00，HEAD 1f3181e21e8d1f9531e41066b033d8d60218d580，main；319 条既有状态，暂存 raw SHA 65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC；512 项安全清单 SHA C84A64752B973D612FC5A37608CD981E3CB8B11BE5B100A3E15567384A8648C6。既有改动仅作为输入，不计为本任务成果。
- C2-A-db-v1 局部契约：Storage AppDatabase.Initialize(string databasePath) 接受一次明确完整路径；同规范路径幂等，不同路径拒绝。Host FilePath 未初始化时仍使用 Host AppPaths；Host 旧同 FQN 数据库改为无状态转发，保留原公共签名。Storage 时钟绑定 ClockBindings.Source，备份默认目录来自已绑定数据库目录，R5 整批原始 key/Ordinal/TTL/回滚保持。
- 嵌套同步 Write/备份不允许在写回调内另开连接，明确拒绝而非阻塞；保留现有 30 秒 SQLite 等待，不宣称有界队列/取消。迁移旧分支及 schema 9 不变，较新版本在 DDL/日志模式修改前拒绝；完整迁移崩溃恢复仍未验收。
- 唯一别名例外仅 Headless→Storage 且 Aliases=StorageModule；其它 metadata、global/多别名、条件引用仍拒绝；数值棘轮/全源计数不变。项目别名实际 MSBuild 行为以真实构建核验，需范围外改动时停止受影响部分。
- 消费核对：00 已查 Host 原 22 公共成员、SettingsStore/审计、SettingsScope 初始化前路径、飞书 R5、备份和 C1 child 分支；这是本批负责人确认的局部接入，不冒充 01～06签署整体 C2/C3/C4。旧 Host 导入器及其 AppSettings 类型校验不迁移。

| 执行者 | 确切认领路径 | 开始 SHA-256 / 状态 |
| --- | --- | --- |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\AppDatabase.cs | E1E5DEA68D540817853CFD50C84C21DB8AADA4A983048DCB33C500A744D6CE61；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\AppDatabase.cs | 670EBDDDC9B956DFD7BCF5044B090EA02AC458EF2EF6417AD4DE71D5DA2045F1；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Time\ClockBindings.cs | 94034E00CD6BE381255DD3D0F709C3709E1415642CB078784C1E244AE55E3EB1；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Host\CompositionRoot.cs | 47763388337E6DFB218A2C6978D49A60713953BD85B9EB2C5F86C98975BF5609；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\BotAgent.Headless.csproj | EA3EDDB3E0D49B436E3B7704C9EA9776204A26DA46C1A478E4C2E27FADDCF69C；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\FeishuWebhookDedupStore.cs | 5C8033BD0D33F57C172764736B0D6D3113696BDA8310429A4CEE906B864CD148；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Dockerfile | 8ACD7744E376D8A6E76ABF624607AEA60C1958F6503CB5CBA05D31A11469F68C；已交付收口 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ArchitectureProbe\SourceIndex.cs | 803663A9DD87C9AC422FA6E6D05FDF0317A906071BE8CF0673D418268D906FF3；已交付收口 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ArchitectureProbe\Program.cs | 6AD0159DAC07A5B411114D304249BAC8BB8ABDEE63A3F5348E5DEE1DAA3F9407；已交付收口 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ReviewRemediationProbe\Program.cs | 3137DC7D7AD986C1325DEC7C13ABE68125514E63884897F27F4E44E846274011；已交付收口 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ReviewRemediationProbe\DatabaseContractTests.cs | MISSING；已交付收口 |
| 00 | %WORKDIR%\bot-agent\CONTRIBUTING.md | 4902B89AC128B62A9F3242307232FA0B22F24AA0E2C343A75E341D6378F4D5C9；已交付收口 |
| 00 | %WORKDIR%\bot-agent\docs\plans\agents\00-orchestration-and-dependency-graph.md | 84E6E5F03A5D9B37A47FE93EF58F92992EFB8D24C81677B361B32E79E0BDD1BF；已交付收口 |

仅修改以上 13 路径。允许派生 bin/obj 与本次 UUID 合成临时根；两套数据根一致，移除继承的配置/凭据环境但不回显值。批准的验证边界为公开 Host/Storage 数据库、R5、备份恢复、路径冲突/嵌套写及精确引用扫描；先负例后实现，再独立重建 Headless/定向探针及 harness。已知架构红灯不算准出；独立定向验证记录为诊断证据。

### 13.2 C2-A1 传递引用失败与补充写权

2026-10-09 18:52:12～18:52:17 全15测试工程构建链在第三项 ChaosFaultProbe 退出1：AppDatabase 的 CS0433（三处）；立即停止。Headless 自身的别名未传递为消费者的全局引用约束。ReviewRemediationProbe 已有直接 StorageModule 引用且构建/真实读写通过，故复用该精确声明，不改公开类型或测试断言。

负责人后续「持续推进、不用询问批准」授权用于本轮补充范围；00 最终写入如下11个测试工程引用，保存字节副本和开始哈希。不是临时移交，不给01～07新写权；原13路径之外只增加这11路径，禁止扩大到其它代码。先以Chaos原失败工程复验，再全15构建及独立合成诊断；失败停止受影响链，不放宽护栏。

| 执行者 | 新增确切认领路径 | 开始 SHA-256 |
| --- | --- | --- |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.BridgeProbe\BotAgent.BridgeProbe.csproj | BEC1AAFA8F4C53565E57C1B24898D86FB81334738A46251D5FB5AF47BD427F68 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ChaosFaultProbe\BotAgent.ChaosFaultProbe.csproj | AC1DC0C36E591CF9EBF13F6BFCDEED5D3C8C39FF38A31BAD0FE143256D1DB44A |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ConcurrencyStressProbe\BotAgent.ConcurrencyStressProbe.csproj | BBB796845CB11573E2107C226723FE4C13319ADC3C964568921DB0CAA0928530 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.DeadlineGateProbe\BotAgent.DeadlineGateProbe.csproj | 969D88F2720797473FB72B5826B0CD583C32FB8BBE7265207399B57672BCC039 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.FeishuRemediationProbe\BotAgent.FeishuRemediationProbe.csproj | BEC1AAFA8F4C53565E57C1B24898D86FB81334738A46251D5FB5AF47BD427F68 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ParticipationProbe\BotAgent.ParticipationProbe.csproj | 21D125985B2AA7B00390DDFA062CD891099DA423059EA371407D943FADCB0D52 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.PipelineEval\BotAgent.PipelineEval.csproj | 0E4342072266EF907860AF375951CE85331876FF6A5DBCAA6254436220BABF4F |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ProductionSpecProbe\BotAgent.ProductionSpecProbe.csproj | 71ED481431BBD5952E32937DF63350F3B817B4D878BABB819F6563562512252A |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.SafetyProbe\BotAgent.SafetyProbe.csproj | 3F8501098D68397F9F5D82E7EAFF75E1003B522F0807722AC82777BBD8C327EA |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.SettingsScopeProbe\BotAgent.SettingsScopeProbe.csproj | BEC1AAFA8F4C53565E57C1B24898D86FB81334738A46251D5FB5AF47BD427F68 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.SsrfProbe\BotAgent.SsrfProbe.csproj | 71AA22197986675D0C32B0694FD1E38DF219AAFCBC931EAA12218173A40DC0F4 |

### 13.3 实际调用链、命令与合成证据

- 宿主启动：%WORKDIR%\bot-agent\src\BotAgent.Headless\Host\CompositionRoot.cs BootstrapSettings → ClockBindings.InitializeStorage → Host AppDatabase.Initialize → Storage.Initialize(Host FilePath) → Storage schema/WriteGate。纯仓储探针绕过CompositionRoot时，Host兼容Initialize仍先绑定同一Source。C1 child分支仍在绑定/数据库之前退出。
- 配置/审计：旧SettingsStore/AuditLogStore通过Host Write/Exec转发到同一Storage连接/事务；飞书IWebhookDedupStore→Host批次转发→Storage R5整批。备份→同WriteGate但无事务的VACUUM；默认根来自已绑定数据库，时间来自Host稳定Source。直接Open/Exec公共表面仍保留，不称全局版本化保存或跨进程writer已验收。
- 13原路径及13.2的11补充路径实际变更，未移交/删除其它旧文件。旧Host LegacyJsonImporter保留AppSettings类型校验；CreateSchema、CreateModelProviderTable、ApplyMigrations三段与批次前旧Host正文（仅归一换行）逐字相同；原22个公开成员声明逐项一致。schema仍9；新拒绝只针对较新schema。原始key、Ordinal、TTL/R5保持，不写真实数据。

下表日期均为2026-10-09、UTC+08:00，Windows shell为pwsh，SDK10.0.101/目标net8.0；每个原生命令立即核对退出码，失败链停止。运行输出只采集合成结果/计数，未使用真实数据或未筛选日志。

| 编号 | 真实命令/对象 | 时间 / 退出码 | 覆盖及归属 |
| --- | --- | --- | --- |
| C2-B0 | dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild | 18:41:23～18:41:38；0 | 接入前真实重建；8个既有CS0108 |
| C2-R1 | dotnet build tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release；dotnet该Release DLL | 18:42:14～18:42:21；build0/run1 | 143通过/1失败，新共享数据库负例确实失败 |
| C2-R2 | dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release -- --self-test | 18:42:55～18:42:59；1 | 新合法别名fixture被旧scanner拒绝，负例 |
| C2-G2 | 同上self-test | 18:43:10～18:43:15；0 | 39/0，非法别名/额外metadata/条件/子节点仍拒绝；非仓库准出 |
| C2-G1 | 独立Headless Rebuild；Review真实csproj build/当前DLL | 18:44:22～18:44:48；均0 | 共享库147/0；8旧警告，无新增同名警告 |
| C2-R3 | Review build/当前DLL，新增跨facade嵌套写 | 18:45:25～18:46:01；build0/run1 | 147/1，旧逻辑等待SQLite锁后失败；立即故障不能替代这个证据 |
| C2-G3 | 同Review build/当前DLL | 18:46:46～18:46:52；均0 | 149/0，嵌套写明确立即拒绝、可处理后外层提交 |
| C2-R4 | Review build/当前DLL，新较新schema及备份根 | 18:47:54～18:47:56；build0/run1 | 149/2；原逻辑未拒绝未来版本、备份漂到重读环境根 |
| C2-G4 | 同Review build/当前DLL | 18:48:44～18:48:51；均0 | 151/0；未来版本字节不变、ready=false及新路径重试 |
| C2-G5 | 同Review build/当前DLL | 18:50:05～18:50:08；均0 | 166/0，新增路径/回滚/去重并发/实际备份子进程恢复 |
| C2-SLN | dotnet build BotAgent.slnx -c Release -v:q | 18:50:59～18:51:01；0 | 解决方案非全部测试工程，不能替代下行15工程 |
| C2-ARCH | dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release | 18:51:33～18:51:35；1 | 132通过/2失败，SQL236>137、IO98>62；系统时钟3；保持下游冻结 |
| C2-A1-R | 枚举真实tests csproj并逐项dotnet build -c Release -v:q | 18:52:12～18:52:17；前两项0、Chaos1后停止 | 新接入导致CS0433三处；13.2补充准确写权并修复，不掩盖归属 |
| C2-A1-G | 同15工程逐项真实build（包含先前失败Chaos） | 18:54:01～18:54:30；15项均0 | 无CS0433；Chaos仅1个既有CS0067；其它0警告，此次为增量构建 |
| C2-D1 | dotnet tests/BotAgent.ReviewRemediationProbe/bin/Release/net8.0/BotAgent.ReviewRemediationProbe.dll | 18:54:48～18:54:50；0 | 168/0；C2 child未来4/0、备份恢复3/0；C1 child10/0、12/0分别运行，不混加到父计数 |
| C2-D2 | dotnet tests/BotAgent.SettingsScopeProbe/bin/Release/net8.0/BotAgent.SettingsScopeProbe.dll | 18:54:50～18:54:51；0 | 17个PASS用例、failures0；保留初始化前建旧库与类型/秘密边界 |
| C2-D3 | dotnet tests/BotAgent.FeishuRemediationProbe/bin/Release/net8.0/BotAgent.FeishuRemediationProbe.dll | 18:54:51～18:54:53；0 | 24 PASS/0失败；实际Gateway/R5兼容 |
| C2-D4 | dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll | 18:54:53～18:55:06；0 | 594/0，合成安全机制 |
| C2-D5 | dotnet tests/BotAgent.ProductionSpecProbe/bin/Release/net8.0/BotAgent.ProductionSpecProbe.dll | 18:55:06～18:55:07；0 | 62/0，审计、存储及备份保留原用例 |
| C2-D6 | dotnet tests/BotAgent.ChaosFaultProbe/bin/Release/net8.0/BotAgent.ChaosFaultProbe.dll | 18:55:07～18:55:10及18:58:32～18:58:35；均0 | 43/0；首次过滤未显示JSON计数，后次只提取assertions/failed/status，不假设旧计数 |
| C2-IT-B | 独立Headless Rebuild -c Release -t:Rebuild；再build真实IntegrationHarness csproj | 18:55:46～18:55:53；均0 | 8旧CS0108；harness增量0警告，QQCHAT_BOT_DLL明确指向本仓库重建DLL |
| C2-IT21 | QQCHAT_IT_ONLY=s21；dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll | 18:55:53～18:56:02；0 | 37/0，单场景，独立合成数据 |
| C2-IT36 | 同harness，ONLY=s36 | 18:56:02～18:56:09；0 | 98/0 |
| C2-IT50 | 同harness，ONLY=s50 | 18:56:09～18:56:12；0 | 14/0 |
| C2-IT52 | 同harness，ONLY=s52 | 18:56:12～18:56:19；0 | 119/0，不是S42官方完整协议验收 |
| C2-STATIC | 原22成员/三段schema正文对比；git diff --check及同口径库存 | 18:56:39；pwsh/git均0 | 24路径变化/范围外0，HEAD及index不变；不是项目运行 |

工具错误单列：最初一次合并GZip副本JSON被输出截断，JSON.parse失败，未写文件；改为逐文件返回后全部保存。facade写入脚本使用不存在的btoa导致工具侧ReferenceError，之前精准patch已成功但facade未写；随后核对原Host哈希并完成写入。一次SDK targets候选10.0.100路径rg2，按dotnet --version实际10.0.101查询后rg0；都不当作产品失败或验收通过。

### 13.4 兼容、回退与下一阶段

- 合成行为证据：同规范路径幂等/不同路径拒绝且保留原根；Host/Storage互读；SQL回调故障整体回滚；跨facade去重唯一赢家、重复整批不消费新key、SQL中途失败传播及回滚、TTL边界；同步/异步嵌套写和事务内备份立即拒绝；Host fake控制Storage真实备份时间；默认备份不随环境根漂移；新进程实际恢复备份内容/schema且不替换live库。
- 未覆盖/不能推断：C3全局配置版本/秘密整批/响应丢失恢复；C4管理动作及消费者整体冻结；完整旧schema迁移崩溃恢复、旧产物二进制读取恢复库、跨进程锁、30秒长锁取消/队列/停机预算；完整50场景/排除S42、其它探针仅构建的行为、SDK8 CI、Docker镜像、ZBA、安全资源预算、生产或发布回退。Docker仅等价增加Storage的csproj/源码COPY，并保留低内存预构建产物路线；未构建/运行镜像。
- 回退仅撤销本批24路径的补丁：先核对其后编辑，恢复中央DB旧片段及Host旧实现，撤销Host/11消费者Storage别名引用、ClockBindings/CompositionRoot接入、新测试/护栏fixture和Contributing改动，最后撤本节/三处当前状态索引。新建测试文件仅在确认仍纯属本任务时移除；其它21对旧文件和所有既有输入不回退、不清理。先确保相关consumer一起逆转，再重建/复验；不使用checkout强制覆盖/hard reset/未跟踪清理。当前schema9及迁移正文不变，但三段正文一致/会话字节副本不是旧二进制/生产恢复演练。
- 00核对：唯一中央数据库已由真实Host采用且定向合成通过；不是全Storage迁移、更不是整体Wave1准出。架构两项红灯仍阻塞下游；02/03不得并行、04～07未解锁。持续推进授权已收到，下一批优先解决剩余重复仓储SQL/IO及契约消费者准入，先逐文件盘点/登记后写入，不再重复询问是否批准；接口兼容不明仍停止受影响项。
- 当前24路径写权在本批收口后关闭；没有提交、暂存、推送、部署、发布或生产操作。最终文档/范围/DLL绑定核对后在聊天交付最终指纹，避免自引用哈希。

### 13.5 当前构建产物绑定

以下为本轮结束时当前Release DLL SHA-256（只读字节哈希，不运行程序集）；对应13.3的实际命令，原C1/R4构建和镜像成绩不覆盖这些新输入。修改后须重建/重验，不能据哈希相同冒称重跑。

| 真实产物路径 | SHA-256 |
| --- | --- |
| %WORKDIR%\bot-agent\src\BotAgent.Core\bin\Release\net8.0\BotAgent.Core.dll | D754B1B38FD92C9F14C193F1198D7612CD51E7EBDAFCAD5AA8905AC788A852FD |
| %WORKDIR%\bot-agent\src\BotAgent.Headless\bin\Release\net8.0\BotAgent.Headless.dll | 98864EBEF1F03AE0E53CC1310BBFF1FC4AC0809A4E53CC825034B7F7BF6418B5 |
| %WORKDIR%\bot-agent\src\BotAgent.Model\bin\Release\net8.0\BotAgent.Model.dll | A5823E5A4B54DCB81E1B7C7E6827E9AFF7FBE77262856C9ADA20360A8DB22A20 |
| %WORKDIR%\bot-agent\src\BotAgent.Platforms\bin\Release\net8.0\BotAgent.Platforms.dll | 7BB869F8B1DE1DA742274D358D10F17053F42BC48DCB2DC9E6F5BC2C7D3958B6 |
| %WORKDIR%\bot-agent\src\BotAgent.Storage\bin\Release\net8.0\BotAgent.Storage.dll | 431A52EB16616C0C7E6109D5D3828A88671C2A0555894A4ED587E661FAF5DD63 |
| %WORKDIR%\bot-agent\tests\BotAgent.ArchitectureProbe\bin\Release\net8.0\BotAgent.ArchitectureProbe.dll | E4E3C1D43901818469C36946896C650265E81F002D14179EA5855E473DEFAC2B |
| %WORKDIR%\bot-agent\tests\BotAgent.BridgeProbe\bin\Release\net8.0\BotAgent.BridgeProbe.dll | 6B846FB2544D6AECC3FBA5522139819D1DC8942EF1EDB95D8B4019D0F4630FD3 |
| %WORKDIR%\bot-agent\tests\BotAgent.ChaosFaultProbe\bin\Release\net8.0\BotAgent.ChaosFaultProbe.dll | F54883BED2D64020189359F138AA0BD69C7B5ACC1B8CFC1FEFFE8219E601F440 |
| %WORKDIR%\bot-agent\tests\BotAgent.ConcurrencyStressProbe\bin\Release\net8.0\BotAgent.ConcurrencyStressProbe.dll | 6C17F09DD0F7A6012BCB9E66A164B4BB636903857BDB606D32C4DDED0D6AEA00 |
| %WORKDIR%\bot-agent\tests\BotAgent.DeadlineGateProbe\bin\Release\net8.0\BotAgent.DeadlineGateProbe.dll | 1CCE331D1ECF17BFD183C60E8F4AFAC75F3AF570B76A774F10FC554919CE9E24 |
| %WORKDIR%\bot-agent\tests\BotAgent.FeishuRemediationProbe\bin\Release\net8.0\BotAgent.FeishuRemediationProbe.dll | 81F271B7279602F90A214A22C7D01E6C0440C0B0144A979843540972A8180C4B |
| %WORKDIR%\bot-agent\tests\BotAgent.IntegrationHarness\bin\Release\net8.0\BotAgent.IntegrationHarness.dll | 40268FF4F2F13D0A6A1E4CBB396397EDCDC2E71C0F8F74269FF5DF0C04E8D5C8 |
| %WORKDIR%\bot-agent\tests\BotAgent.ParticipationProbe\bin\Release\net8.0\BotAgent.ParticipationProbe.dll | BEC099A5202E6D7A7055C80D2C1EC4FFF9876873ABD9E3F421AF5AEE036B6618 |
| %WORKDIR%\bot-agent\tests\BotAgent.PipelineEval\bin\Release\net8.0\BotAgent.PipelineEval.dll | 1324B1F90849147767D939F16068CCA94455B543A5928F531C2E3E2135187E0C |
| %WORKDIR%\bot-agent\tests\BotAgent.ProductionSpecProbe\bin\Release\net8.0\BotAgent.ProductionSpecProbe.dll | 5736D72D26E8AAF60FB4FBD1FC07D55BF71E0AB9D3AC969195A4360D98DC056E |
| %WORKDIR%\bot-agent\tests\BotAgent.ReverseTransportProbe\bin\Release\net8.0\BotAgent.ReverseTransportProbe.dll | F12214E01A38DCDA762A29D2C65641AB7080C1DCB28BE988B31A0B6F5FDFDCEB |
| %WORKDIR%\bot-agent\tests\BotAgent.ReviewRemediationProbe\bin\Release\net8.0\BotAgent.ReviewRemediationProbe.dll | 7FA8BCCF3FF8216731416249E57E02C22E4BBB044B0426F0EB6C2DC4B2F70780 |
| %WORKDIR%\bot-agent\tests\BotAgent.SafetyProbe\bin\Release\net8.0\BotAgent.SafetyProbe.dll | 1BBCB5F43F160F1E55C47DBEA525D91E96A54F230DDB38DCB815E5AE25D8DF9B |
| %WORKDIR%\bot-agent\tests\BotAgent.SettingsScopeProbe\bin\Release\net8.0\BotAgent.SettingsScopeProbe.dll | FD009716327733FABB18515ECF533B21820E4D30A67C2AB129A66D0E2DCCB9C8 |
| %WORKDIR%\bot-agent\tests\BotAgent.SsrfProbe\bin\Release\net8.0\BotAgent.SsrfProbe.dll | 3881DB0A6D47395EF9A2E3119A341FB9EFDE9DF0AE2FFCD6DDDAAC6F8696CE59 |

- 收口静态核验：19:01:37、pwsh/git退出0，44个登记的现有绝对路径存在，Docker实际3个跨工程csproj输入齐全，正文尾空白0。19:03:52前分组验证22份原字节GZip副本解压内存哈希通过；总纲撤销三处当前索引及第十三节后的内存哈希与开始84E6E5F0一致，新增测试标MISSING；不实际覆盖/回退。一次全副本验证脚本超Windows命令长度，CreateProcess错误206、未启动进程/未写入；改分组后退出0。HEAD/index及范围外输入不变；24路径写权本批关闭，后续修改另登记范围。最终SHA交付聊天，无自引用。

## 十四、2026-10-09 C2-B 仓储迁移与剩余架构门禁

### 14.1 授权、写权、交接及局部契约

- 负责人已明确授权00持续推进到阶段一可开工、无需重复询问批准；本批00登记下列精确路径，无子Agent/临时移交，仍不授权提交/推送/部署/发布/生产操作。开始2026-10-09T19:05:31.7168082+08:00；HEAD 1f3181e21e8d1f9531e41066b033d8d60218d580，main；513项SHA 56F6F387CE3509A4E6D43408E2FC1EB8F1C50879F3C286FD01C390607119F95F，331既有状态，index SHA 65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC。原工作区归属不变。
- 目的不是删未采用Storage换绿，而是实际采用剩余20对Storage持久化/文件实现并保留Host调用兼容；C2-A数据库/原22成员不改；六个Host独有仓储仍保留，不声称C3/C4已冻结。17个Storage实现类取消sealed以允许Host sealed构造器兼容层，方法正文/字段逻辑不改，Host不再保留这些SQL/文件实现；Host public纯数据记录保留作源码兼容，实际导入采用Storage同字段记录，不改变序列化字段/key。
- C2-B-bind-v1：StoragePaths.Initialize(string runtimeRoot)一次显式绑定规范Host RuntimeRoot；同根幂等、不同根拒绝、未绑定拒绝，不保留第二套动态环境/目录扫描回退。DataDir/SettingsFile仍root/data布局。ClockBindings.InitializeStorage继续绑定稳定IClock并绑定路径及现有StorageLogging.WriteHandler/WarnHandler到Host FileLog同一sink；不新造日志接口/不修改数据key/名称。
- C2-B-import-v1：Storage LegacyJsonImporter新增ImportIfNeeded(Action<string> validateSettingsJson)；Host旧无参入口转发并传入原JsonSerializer.Deserialize<AppSettings>/大小写无关校验。无参Storage旧入口仍只作JSON语法校验兼容，不在生产采用；校验异常保留源文件、不写settings或done标记。其余逐表写/归档/恢复原语义不改变，不宣称跨表/文件全局原子或崩溃恢复。
- 消费证据：已逐项查18字节相同/PanelPassword可见性差/Importer校验差；全部Store公开构造器、Core端口、Host的Legacy MemberProfileRecord消费、C1/C2 child和生产CompositionRoot调用；当前未知旧二进制混搭兼容不宣称通过，已知源码消费者将全部真实构建/合成复验。public RoleRecord的定义程序集随基类迁移，源码访问保留；不是01～06整体契约签署。
- 转换先通过公开类型/行为及源扫描负例证明当前尚未迁移，再一组已相同实现迁移；接口/兼容失败停止受影响链，不删断言、不改变全src计数或Baseline。所有测试仅新UUID合成根，Windows用pwsh逐原生命令退出检查。

| 执行者 | 本批确切路径 | 开始SHA-256 / 交接 |
| --- | --- | --- |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\AgentImageStore.cs | 4AEFA1EF552ACBB7738B8D9AE9A6732BAE0266F423D78A0ECD7501932E7E3D49；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\AgentSessionStore.cs | 412F72EB28A97506207F8C88BA28F598EC8A8EF7BA6036E75373C898D4CD03DC；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\AudioCache.cs | E1ED7BB5B07BA98BEF357A9A3CABC25171E83DC405036FFF7CCE5E18312AFF03；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\ConversationStore.cs | AF6645EC6BA9F8BD103434FEFA412E8C93EA375B167DB81FD5B3A980DA106628；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\EpisodeStore.cs | BADFE1A22A5D9FFA2A3BB63CDB28D51430FCC54CE69B86897ACB63991FE4AEA3；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\HostMetrics.cs | 13BB69B7A416BB1AA385AAA882AA6AB5FF5FA38D4CD827F24F95D4F992963899；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\JargonStore.cs | 4049B23E1F632BB566D844A91363916D6C4F81B288DD2B815A6791B0C0DCFAAC；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\MemberProfileStore.cs | 6943E76F666566D88C2FF98D7BC6F8E54F8C9564E8C0F450F8B253169B3ABB08；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\MemberRoleStore.cs | A1511099843859EB037F24DA8CCE750ED2F1E2BA7E6A2334C08861220BE75061；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\MoodStore.cs | 88AF0430E91D9FAF492508DB75A8C71B00D9BD0CDD41EC04DE4E6C6B0029811E；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\MusicStore.cs | F64E6E3F5CC0D1185CA2AABA5DEC670559D961F2C80942164352360C498AFFB1；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\OwnMessageStore.cs | 7720D67BD50FBE298117B4002FAF791118CF869F553000D8E0647F626FDD4C79；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\PanelPasswordStore.cs | 3A22D12DDC88A4756E8C0017BC47E263EC380EF50534D8B52DD7E28295C2679E；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\PromptTemplateStore.cs | 25BD46154B136221DB32231B3BE8ADC3675AAE8CE9EB424F3DD4D34CC0977628；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\SecretsStore.cs | 4FD10E7009E8F409BC00093BC59AD29AE5641BE368E2115D83A51A2C8F2AFD7A；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\StickerStore.cs | FB3C0D2A4B3BF28DD1F951B5A64B99FD38C0010E574CF4FAFEC3686FAD341E24；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\TenantQuotaStore.cs | CBA1FFDFB45BB0A82A8E6B1EFE849A98318932A3A6A1F501A31F3D47CE99E86D；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\SecretFiles.cs | 794E68D35FF8D243615988A8CEEB270B07D5BB02691B43FC38C7774F79E90063；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\TtsConfFile.cs | 4B011261A3E54DC983F1EF41C30DED70DC2A5F234BBD9BB72515B3B98EF0B46F；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\LegacyJsonImporter.cs | F486A363E15E118E9F67474197F6258AE5F476D542CA7F4B09437D2360F50CED；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\AgentImageStore.cs | 4AEFA1EF552ACBB7738B8D9AE9A6732BAE0266F423D78A0ECD7501932E7E3D49；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\AgentSessionStore.cs | 412F72EB28A97506207F8C88BA28F598EC8A8EF7BA6036E75373C898D4CD03DC；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\AudioCache.cs | E1ED7BB5B07BA98BEF357A9A3CABC25171E83DC405036FFF7CCE5E18312AFF03；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\ConversationStore.cs | AF6645EC6BA9F8BD103434FEFA412E8C93EA375B167DB81FD5B3A980DA106628；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\EpisodeStore.cs | BADFE1A22A5D9FFA2A3BB63CDB28D51430FCC54CE69B86897ACB63991FE4AEA3；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\HostMetrics.cs | 13BB69B7A416BB1AA385AAA882AA6AB5FF5FA38D4CD827F24F95D4F992963899；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\JargonStore.cs | 4049B23E1F632BB566D844A91363916D6C4F81B288DD2B815A6791B0C0DCFAAC；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\MemberProfileStore.cs | 6943E76F666566D88C2FF98D7BC6F8E54F8C9564E8C0F450F8B253169B3ABB08；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\MemberRoleStore.cs | A1511099843859EB037F24DA8CCE750ED2F1E2BA7E6A2334C08861220BE75061；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\MoodStore.cs | 88AF0430E91D9FAF492508DB75A8C71B00D9BD0CDD41EC04DE4E6C6B0029811E；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\MusicStore.cs | F64E6E3F5CC0D1185CA2AABA5DEC670559D961F2C80942164352360C498AFFB1；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\OwnMessageStore.cs | 7720D67BD50FBE298117B4002FAF791118CF869F553000D8E0647F626FDD4C79；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\PanelPasswordStore.cs | 564B7A16D5A7156C59EF963F90C05C91E639C149CA9ADA2C5DAF5979013765AE；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\PromptTemplateStore.cs | 25BD46154B136221DB32231B3BE8ADC3675AAE8CE9EB424F3DD4D34CC0977628；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\SecretsStore.cs | 4FD10E7009E8F409BC00093BC59AD29AE5641BE368E2115D83A51A2C8F2AFD7A；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\StickerStore.cs | FB3C0D2A4B3BF28DD1F951B5A64B99FD38C0010E574CF4FAFEC3686FAD341E24；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\TenantQuotaStore.cs | CBA1FFDFB45BB0A82A8E6B1EFE849A98318932A3A6A1F501A31F3D47CE99E86D；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\LegacyJsonImporter.cs | 61E1FED11548E5913287BDAE54A624843B4E35737FE62504A6C69397A2E56C3F；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Storage\AppPaths.cs | A4151851A390EFDCA6F90B3DF2503742FCE394406F9B29CDAA419E2755123F5C；已交付收口 |
| 00 | %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Time\ClockBindings.cs | 43A35B90A69BC9BBF04629A9F4EB5E91A68A1A1AC2A32BEDA6FAB9EA8C3DD712；已交付收口 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ReviewRemediationProbe\Program.cs | 6415436DD5D43CD4649BAC0BB8898974640A71FB9369A7AA372CA0A42416EC24；已交付收口 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ReviewRemediationProbe\DatabaseContractTests.cs | A533B5C4F64BB8FB22D24DA5A1ABA2234BF57E93954FD28F99BC0294B77B81D1；未修改，认领关闭 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ReviewRemediationProbe\StorageMigrationTests.cs | MISSING；已交付收口 |
| 00 | %WORKDIR%\bot-agent\tests\BotAgent.ArchitectureProbe\Program.cs | 46C98BCA926D04830BD3369FCD3D09D7314F258CA19BA1A76A7135121AECF751；已交付收口 |
| 00 | %WORKDIR%\bot-agent\CONTRIBUTING.md | 225823991CAD51D718DEAE0BF5EBDDF4F43A299E9167FCA0C99CE88FE988A4F4；已交付收口 |
| 00 | %WORKDIR%\bot-agent\docs\plans\agents\00-orchestration-and-dependency-graph.md | 345807D80FC4398A3BA8E2631F5C6C71F2F7671F5D98FC63165B97C6C1640BE3；已交付收口 |

以上46路径构成本批唯一代码/测试/文档集合；无目录级写权，不改六个Host独有仓储、Core端口/共享设置、CI/部署。允许真实工程bin/obj及本批独立合成临时根。新StoragePaths类型与导入重载是本批明确局部接口，候选C3/C4类型仍不得假称已有。

### 14.2 宿主接入、验证及失败归属

- CompositionRoot原构造点继续new Host同名sealed兼容类，其所有实例方法实际声明于Storage；原Core仓储端口不改。17个Storage实现的正文精确保持，唯取消最外层sealed；public元数据检查16个对外Host类型均执行Storage方法，internal PanelPasswordStore由实际HTTP认证场景覆盖。静态SecretFiles/TtsConfFile转发到Storage；LegacyJsonImporter传Host原大小写无关AppSettings类型校验。Host旧纯DTO留下，不宣称所有重复声明均已删除。
- Host数据库初始化及CompositionRoot中的InitializeStorage先绑定同一稳定Clock、Host一次选定RuntimeRoot与实际FileLog sink，之后才建库/导入/构造仓储。模块没有另一套环境根/目录探测；C2-A数据库字段/迁移/R5未动。显式数据库路径和运行根是两个明确输入；无参Storage.Initialize现在要求先绑定StoragePaths，此行为变化已登记，不假称仍支持独立环境发现。
- 新护栏对21个Host兼容文件要求目标存在、实际代码引用Storage且SQL/文件IO为零；独立fixture拒绝缺实现、字符串伪引用、残余SQL、残余IO。五工程全部源码继续统计，Baseline/Metrics/SourceIndex均未改，未删断言或去重计数。

下面均为2026-10-09/UTC+08:00，唯一仓库pwsh，SDK10.0.101目标net8.0。每个原生命令立即记录退出码，失败即停止受影响链；只运行新UUID合成数据、当前工程。原先C2-A两项红灯是历史输入，不覆盖成新结果。

| 编号 | 真实命令/对象 | 时间 / 退出码 | 证据 |
| --- | --- | --- | --- |
| C2B-R1 | Review实际csproj build -c Release -v:q；当前Review DLL | 19:13:31～19:13:41；build0/run1 | 新Episode真实Storage方法采用负例；168/1，不凭同源文字假称已采用 |
| C2B-G1 | 同上，迁出Episode实现 | 19:14:12～19:14:19；均0 | 169/0，保留原Episode真实读写/ID用例 |
| C2B-R2 | 同Review build/DLL，新根漂移用例 | 19:15:17～19:15:20；build0/run1 | 169/1，旧Storage重读环境根导致实际旧消息导入失败 |
| C2B-G2 | 同Review build/DLL，显式路径/日志绑定 | 19:16:01～19:16:07；均0 | 171/0，环境别名改变后仍从原根导入/归档 |
| C2B-G3 | 同Review build/DLL，17仓储实际采用 | 19:17:14～19:17:21；均0 | 186/0，16对外类型方法所有权＋既有实际仓储行为 |
| C2B-G4 | 同Review build/DLL，静态入口/类型校验seam接入 | 19:19:23～19:19:29；均0 | 186/0 |
| C2B-ARCH1 | dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release | 19:19:37～19:19:39；0 | 134/0，SQL137、IO62、Clock3，先解除重复实现红灯 |
| C2B-G5 | Review build/DLL，绑定/类型校验子进程 | 19:22:13～19:22:16；均0 | 192/0；C2B child未绑定4/0、类型校验/归档/幂等5/0；子计数不混加父计数 |
| C2B-BUILD | 枚举实际15个tests csproj，逐一dotnet build绝对csproj -c Release -v:q | 19:24:04～19:24:30；15项均0 | 无新增同名错误；Chaos1个既有CS0067，其余本次增量0警告；不是全部程序集行为运行 |
| C2B-ARCH2 | dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll | 19:25:05～19:25:07；0 | 159/0，含21新增真实迁移约束及4反伪装fixture；源302个/53182行 |
| C2B-P1 | dotnet tests/BotAgent.ReviewRemediationProbe/bin/Release/net8.0/BotAgent.ReviewRemediationProbe.dll | 19:25:07～19:25:09；0 | 192/0；C1 child10/0、12/0；C2 child4/0、3/0另记 |
| C2B-P2 | dotnet tests/BotAgent.SettingsScopeProbe/bin/Release/net8.0/BotAgent.SettingsScopeProbe.dll | 19:25:09；0 | 17 PASS、failures0，旧schema/own-messages与配置及秘密边界 |
| C2B-P3 | dotnet tests/BotAgent.FeishuRemediationProbe/bin/Release/net8.0/BotAgent.FeishuRemediationProbe.dll | 19:25:09～19:25:10；0 | 24 PASS/0失败，真实去重注入 |
| C2B-P4 | dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll | 19:25:10～19:25:22；0 | 594/0 |
| C2B-P5 | dotnet tests/BotAgent.ProductionSpecProbe/bin/Release/net8.0/BotAgent.ProductionSpecProbe.dll | 19:25:22～19:25:23；0 | 62/0，审计/存储/备份 |
| C2B-P6 | dotnet tests/BotAgent.ChaosFaultProbe/bin/Release/net8.0/BotAgent.ChaosFaultProbe.dll | 19:25:23～19:25:26及19:28:35～19:28:37；均0 | JSON43/0；第二次只提取计数/状态，第一次过滤未显示计数 |
| C2B-IT-B | 独立Headless csproj Rebuild -c Release -t:Rebuild -v:q，再build真实IntegrationHarness csproj | 19:25:55～19:26:00；均0 | 8既有CS0108；harness增量0警告，QQCHAT_BOT_DLL显式指本仓库最新Release |
| C2B-IT6 | ONLY=s6；dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll | 19:26:00～19:26:05；0 | 19/0，实际重启/持久化 |
| C2B-IT17 | 同harness，ONLY=s17 | 19:26:05～19:26:07；0 | 22/0，真实面板认证/模拟二维码契约 |
| C2B-IT21 | 同harness，ONLY=s21 | 19:26:07～19:26:14；0 | 37/0 |
| C2B-IT27 | 同harness，ONLY=s27 | 19:26:14～19:26:19；0 | 22/0，旧JSON设置/档案/会话/归档实际生效、留档/重启幂等；全部合成 |
| C2B-IT36 | 同harness，ONLY=s36 | 19:26:19～19:26:26；0 | 98/0，显示脱敏/raw key/nameRaw与秘密拒收 |
| C2B-IT50 | 同harness，ONLY=s50 | 19:26:26～19:26:29；0 | 14/0 |
| C2B-IT52 | 同harness，ONLY=s52 | 19:26:29～19:26:35；0 | 119/0，不替代S42 |
| C2B-P7 | dotnet tests/BotAgent.BridgeProbe/bin/Release/net8.0/BotAgent.BridgeProbe.dll | 19:27:15～19:27:25；0 | 14 PASS、failures0 |
| C2B-P8 | dotnet tests/BotAgent.ConcurrencyStressProbe/bin/Release/net8.0/BotAgent.ConcurrencyStressProbe.dll | 19:27:25～19:27:26；0 | 12/0 |
| C2B-P9 | dotnet tests/BotAgent.DeadlineGateProbe/bin/Release/net8.0/BotAgent.DeadlineGateProbe.dll | 19:27:26～19:27:27；0 | 53/0 |
| C2B-P10 | dotnet tests/BotAgent.ParticipationProbe/bin/Release/net8.0/BotAgent.ParticipationProbe.dll | 19:27:27；0 | 48/0 |
| C2B-P11 | dotnet tests/BotAgent.ReverseTransportProbe/bin/Release/net8.0/BotAgent.ReverseTransportProbe.dll | 19:27:27～19:27:37；0 | 13 PASS、failures0 |
| C2B-P12 | dotnet tests/BotAgent.SsrfProbe/bin/Release/net8.0/BotAgent.SsrfProbe.dll | 19:27:37～19:27:38；0 | 77/0；明确不使用公网HTTP/DNS |
| C2B-EVAL | dotnet tests/BotAgent.PipelineEval/bin/Release/net8.0/BotAgent.PipelineEval.dll | 19:27:47；0 | 68/0确定性合成，输出默认独立temp/eval-results；未读密钥/调真模型 |
| C2B-PY | python -m unittest discover -s tests/BotAgent.BridgeProbe -p test_*.py | 19:28:26～19:28:27；0 | 13个用例；PYTHONDONTWRITEBYTECODE=1不写repo缓存 |
| C2B-NODE | node tests/BotAgent.FrontendProbe/probe.mjs | 19:28:27～19:28:34；0 | 356/0静态探针，不是独立新Web浏览器验收 |
| C2B-STATIC | 将17个Storage class的sealed声明逆向恢复于内存，核对开始正文哈希；git diff --check/安全库存 | 19:30:58～19:30:59；pwsh/git0 | 全17正文一致；514项45路径改变、范围外0、46认领中DatabaseContractTests未改，HEAD/index/Baseline不变 |

工具错误/只读失败：一次候选src/BotAgent.Storage/SettingsStore.cs读取pwsh1，实际该文件不存在，后查真实清单及六个Host独有适配器，不创建错误文件/接口；初次大输出截断，仅把已见内容作为线索并用针对性查询重读。没有其它被隐藏的项目失败；没有将历史C2-A/镜像/CI成绩当本轮新验收。

### 14.3 准出核对、兼容与回退

- C2-B局部准出：实际构造点不改却执行Storage方法，原17实现正文仅取消sealed；所有15真实测试工程编译；14个非harness原生测试/评测工程行为及Python/Node上述结果通过；7个定向真实Host子进程场景通过。不是“仅DLL编译”证据。架构159/0的扫描口径/阈值未变且新增退化负例，不再沿用此前两项红灯描述当前状态。
- 行为差异与兼容：Host同名sealed类型/构造器仍在，内部PanelPassword可见性保持；公共业务方法/字段由同FQN Storage基类提供，MemberRoleStore.RoleRecord实际定义程序集变为Storage，Host三种MemberProfile纯DTO保留。源码消费者均重编通过，JSON/key/数据库schema9不变，但旧程序集混搭/反射DeclaredOnly等外部使用不自动兼容；这里不宣称旧二进制/生产回退已经验收。Storage无参初始化不再独立找环境根，必须显式运行根绑定；所有本轮真实Host/探针路径已按此启动。
- 导入设置合法JSON但错误属性类型拒绝，源保留/不写done，可修改后重试；成功留档、重复导入不覆盖新保存；S27证明完整合成旧数据确实进入实际提示链。迁移仍逐表/文件，失败/崩溃全局原子和所有历史schema恢复未验收。TTS仍保留原外部文件应用政策，未单独验证真实/合成共享挂载副作用，不把DB成功称为文件应用成功。
- 未覆盖：完整50场景及S42（仍排除）、真实协议端/群聊/数据库、旧产物数据回退、SDK8 CI/Linux镜像、生产资源和安全预算、新ZBA及认证/幂等/响应丢失恢复、全局C3保存与C4管理；本批没有远程、生产、提交、暂存、推送、部署或发布操作。
- 回退仅撤本批45个实际变更片段：按本批字节副本还原17个Storage sealed声明、20个Host旧实现、Storage路径/导入seam、ClockBindings绑定、新测试/护栏/Contributing及本节/当前索引；DatabaseContractTests无改动不可回退。先核后续编辑，再一起重建消费者并复验，保留C2-A全部输入及既有修改；不使用force checkout/hard reset/未跟踪清理、不整体覆盖目录。内存逆向正文/副本和本轮合成新进程恢复不替代旧产物/生产恢复演练。
- 整体Wave1仍未准出，下游02/03/04/05/06执行波次和Web未解锁。阶段一开工仍需C3/C4真实契约/消费者及最终准入记录；00继续按持续授权推进，不重复询问批准。C2绑定/类型校验是本批局部冻结，不冒充01～06整体签署。

### 14.4 产物绑定与认领关闭

- 收口核对 2026-10-09T19:42:50.5437124+08:00：当前安全库存514项、45路径相较本批开始改变、范围外0；46认领中DatabaseContractTests未改并关闭认领。HEAD/index/Baseline/Metrics/SourceIndex与开始一致，无暂存/提交；手册随后仅补本段和状态，最终自引用指纹在交付消息记录。此次库存SHA 6DF75C72CAB382F4FFBEC176CB3957315888D902C77738F55E7B63AF5321C510，status352/SHA 43AB8B338E84450A291641A94A57E238A420ADBE87EB29089644BDB73818E538。
- 2026-10-09T19:40:28～19:40:35+08:00，pwsh退出0：44个既有非手册源码/文档副本分10组于内存解压，SHA逐个与认领开始值一致，不落盘、不接触运行数据。新测试原MISSING。19:42:48+08:00手册内存逆向去本节并只逆转前三个当前索引，SHA精确回到345807D80FC4398A3BA8E2631F5C6C71F2F7671F5D98FC63165B97C6C1640BE3。初次逆向只读检查退出1是辅助正则误替换历史Wave1行，已限制替换首次后退出0；没有改源文件或隐藏失败。
- 2026-10-09T19:42:49.4441882+08:00，Get-FileHash pwsh0：以下20个当前DLL绑定上述实际工程；不据此宣称未引用Model宿主采用/生产发布或旧二进制兼容。

| 实际DLL绝对路径 | SHA256 |
| --- | --- |
| %WORKDIR%\bot-agent\src\BotAgent.Core\bin\Release\net8.0\BotAgent.Core.dll | D754B1B38FD92C9F14C193F1198D7612CD51E7EBDAFCAD5AA8905AC788A852FD |
| %WORKDIR%\bot-agent\src\BotAgent.Headless\bin\Release\net8.0\BotAgent.Headless.dll | 5E20031B7914D61D8FB2AFFFF05642F7893FBEE2694CC103ABE811E8879FB652 |
| %WORKDIR%\bot-agent\src\BotAgent.Model\bin\Release\net8.0\BotAgent.Model.dll | A5823E5A4B54DCB81E1B7C7E6827E9AFF7FBE77262856C9ADA20360A8DB22A20 |
| %WORKDIR%\bot-agent\src\BotAgent.Platforms\bin\Release\net8.0\BotAgent.Platforms.dll | 7BB869F8B1DE1DA742274D358D10F17053F42BC48DCB2DC9E6F5BC2C7D3958B6 |
| %WORKDIR%\bot-agent\src\BotAgent.Storage\bin\Release\net8.0\BotAgent.Storage.dll | B90414E1B985F0AA97AB84EABFA074B5773C31B4A73CD9E530DDA313157F39D0 |
| %WORKDIR%\bot-agent\tests\BotAgent.ArchitectureProbe\bin\Release\net8.0\BotAgent.ArchitectureProbe.dll | 664342F7E07AAEFA20EC38ABDCC57D48D476BA3A451CBF89E0AD6C55CB683B56 |
| %WORKDIR%\bot-agent\tests\BotAgent.BridgeProbe\bin\Release\net8.0\BotAgent.BridgeProbe.dll | 5D53C94BA1845A48B66F97C8685BE38C69567C88F6BEA9B21A7147E1F45569A0 |
| %WORKDIR%\bot-agent\tests\BotAgent.ChaosFaultProbe\bin\Release\net8.0\BotAgent.ChaosFaultProbe.dll | 1A283CFD8522F155D10456872061455F0771C8F3AD4B4FD12CC88A26B74F0763 |
| %WORKDIR%\bot-agent\tests\BotAgent.ConcurrencyStressProbe\bin\Release\net8.0\BotAgent.ConcurrencyStressProbe.dll | 4793007D5B2A4D4AE93C33BEFA2B8E55DFF7DCAFEE16F2D6ACF1952F7B52EE6E |
| %WORKDIR%\bot-agent\tests\BotAgent.DeadlineGateProbe\bin\Release\net8.0\BotAgent.DeadlineGateProbe.dll | 343C5192D4605CD59E9795CA3B63D32D81DD1D73A3C92D7376ABF3407AD5D092 |
| %WORKDIR%\bot-agent\tests\BotAgent.FeishuRemediationProbe\bin\Release\net8.0\BotAgent.FeishuRemediationProbe.dll | 204C2F092CFB301A5939663079321E8E7C619A2A074C526EB80559B7BF20CAC3 |
| %WORKDIR%\bot-agent\tests\BotAgent.IntegrationHarness\bin\Release\net8.0\BotAgent.IntegrationHarness.dll | 40268FF4F2F13D0A6A1E4CBB396397EDCDC2E71C0F8F74269FF5DF0C04E8D5C8 |
| %WORKDIR%\bot-agent\tests\BotAgent.ParticipationProbe\bin\Release\net8.0\BotAgent.ParticipationProbe.dll | FC865CDBF49E45109C6567C2CF09CB96A349780955DAE8289F5E5824CFD940A7 |
| %WORKDIR%\bot-agent\tests\BotAgent.PipelineEval\bin\Release\net8.0\BotAgent.PipelineEval.dll | 59132A5B1F570A184144549B6980CB972EC9DD422FB38E92CF07B57457AF4D0D |
| %WORKDIR%\bot-agent\tests\BotAgent.ProductionSpecProbe\bin\Release\net8.0\BotAgent.ProductionSpecProbe.dll | 0B9D7FE0DE8C4C34A80E95DC7CE66835810F8794E886D3F25227A077A8073B53 |
| %WORKDIR%\bot-agent\tests\BotAgent.ReverseTransportProbe\bin\Release\net8.0\BotAgent.ReverseTransportProbe.dll | F12214E01A38DCDA762A29D2C65641AB7080C1DCB28BE988B31A0B6F5FDFDCEB |
| %WORKDIR%\bot-agent\tests\BotAgent.ReviewRemediationProbe\bin\Release\net8.0\BotAgent.ReviewRemediationProbe.dll | 891C4803182211C44869134FF58E08976DE0B23147D94EA298D41026B6E57652 |
| %WORKDIR%\bot-agent\tests\BotAgent.SafetyProbe\bin\Release\net8.0\BotAgent.SafetyProbe.dll | 62B5FACFB39161BEC887AFB0A81C9BCBBBD3632D025D26AF6708792930FE7B44 |
| %WORKDIR%\bot-agent\tests\BotAgent.SettingsScopeProbe\bin\Release\net8.0\BotAgent.SettingsScopeProbe.dll | 3F34500AA20BE52104540885C3B45997057FF26FE6C65A53EE1883B8ECCCF2FB |
| %WORKDIR%\bot-agent\tests\BotAgent.SsrfProbe\bin\Release\net8.0\BotAgent.SsrfProbe.dll | A3AC1BFF8EC6BEFF121E22673351ACACE78B3275FF3719FB9A655D906F8BE068 |

## 十五、C3/C4 契约准入评审（Agent-00，持续授权）

### 15.1 本批边界与认领

- 负责人已授权00持续推进至阶段一可开工，不重复请求批准；仍不授权提交/推送/部署/发布/生产。C2-B于2026-10-09T19:43:36+08:00已关闭46认领（45实际变化/1未改），架构/实际宿主证据见第十四节，不复用历史红灯状态。
- 本批只写本总纲，不改代码/项目引用/测试/CI/部署，不运行新项目构建或测试。00独占最终文档写入，三个只读设计评审助手无写权/旧文件交接/波次执行权，不将其候选意见冒充01～06整体冻结或Wave1验收。评审采用codebase-design的DESIGN-IT-TWICE流程，分别比较最小入口、跨消费者扩展、旧宿主/06默认调用；所有提案标为候选，真实接口先查定义。
- 开始输入：HEAD 1f3181e21e8d1f9531e41066b033d8d60218d580，main；安全库存514/SHA 99A076BE134C78783BF0879513E03CC7DE8FE4111E848A3503BDFC8E9CF5FC9C；352状态/SHA 43AB8B338E84450A291641A94A57E238A420ADBE87EB29089644BDB73818E538；index SHA 65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC。唯一认领 %WORKDIR%\bot-agent\docs\plans\agents\00-orchestration-and-dependency-graph.md，开始 SHA 8127E1AF4F588F15A506533803BBD8562D02DC42CED3E0D247D7A507495D6040；会话保留开始字节副本/哈希，仅作本批片段对照。既有源码及C1/C2成果不计为本批改动。
- 只看源码/计划及安全元数据，不读真实数据/环境值/日志。评审依赖分类：候选构造/校验/发布为in-process；真实SQLite、独立临时文件为local-substitutable；平台/模型远端检查为true external，仅以合成mock验证，不能混入DB事务。用户要求保留全部原断言，设计技能中replace测试不适用于本批，不删测试。
- 尚缺：C3精确非秘密类型/字段、生效及环境投影、版本/幂等/响应丢失、事务审计seam/旧HTTP兼容；C4稳定标识/能力/动作支持矩阵；C5早期06安全/组装审查。当前没有新Core业务签名，不据只读报告宣布阶段一可开工。
- 辅助只读错误单列：上一轮候选IStorage.cs查询pwsh1（真实目录无该接口），立即停止该读链，随后枚举并读实际ISettingsRepository/Secrets/Platform/Plugins定义；不创建错误文件或使用臆造接口。14.4手册逆向校验初次失败已单列，其后验证通过。

### 15.2 2026-10-10恢复核验与当前源码事实

- 上一轮已完成C2-B代码及合成验收，15.1只读契约评审中断：三个顾问的服务额度报错是终止，不是产品失败/评审通过。2026-10-10T11:48:25+08:00恢复；collaboration实时清单仅root，确认原助手已无活动句柄后才重新发起三个只读评审，不因观察超时重启。恢复任务c3_minimal_resume、c3_extensible_resume、c3_host_zba_resume均已交回只读候选，均无写权/测试/Host运行/真实数据访问；不是01～06正式整体签署。
- 初次git默认status为186条，仅因未跟踪目录折叠；按上一轮--untracked-files=all重核，2026-10-10T11:49:09+08:00仍352条、SHA 43AB8B338E84450A291641A94A57E238A420ADBE87EB29089644BDB73818E538，index20条/SHA 65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC、HEAD不变。不是外部工作区变更，已纠正初步疑虑，未回退或覆盖文件。
- 本批恢复输入2026-10-10T11:49:41.3254806+08:00：514项同类安全库存/SHA 139EE568F26F8A0D06A3A61FA6019EF443694D04AAB5FBA38D20A8D0A3D07BE6；本总纲SHA 2D85002CCFB2F07C63E8E1B60D761E6E2BF2F6B340720D322382CD1AF33151DF。15.1之前的总纲前缀逆向SHA仍精确为8127E1AF4F588F15A506533803BBD8562D02DC42CED3E0D247D7A507495D6040，C2记录未被覆盖；本批仍只认领总纲，不改源码。
- 11:54:09+08:00 pwsh0：第十四节20份实际DLL绑定全部匹配；17个Storage实现逆向仅恢复最外层sealed后，正文哈希全部等于14.1的开始值。只是绑定/正文复核，未重新运行行为，不把这次静态核对称为新的159/0或整项目无漂移证明。

| 已有定义/真实消费者 | 当前证据及C3/C4影响 |
| --- | --- |
| %WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Ports\ISettingsRepository.cs；%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\SettingsBox.cs:46～64；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\SettingsStore.cs:58～90 | Host类型耦合仍在；保存先persist后publish，配置与具体AuditLogStore事务已有可复用入口；不能原样把AppSettings/Platforms依赖搬Core，不能虚构IAuditChain的事务方法 |
| %WORKDIR%\bot-agent\src\BotAgent.Storage\AppDatabase.cs:178～209；%WORKDIR%\bot-agent\src\BotAgent.Storage\SecretsStore.cs:95～120；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Panel\WebUiServer.Settings.cs:131～147,298～324,414～422,725～732 | C2唯一writer拒绝嵌套Write；秘密Save依旧独立事务/默认false，旧HTTP mutate仍直接调用且忽略bool。外层Write包旧Save不是整批原子；这是静态缺口，本批未动态复现，不归为C2迁移新回归 |
| %WORKDIR%\bot-agent\src\BotAgent.Headless\Services\AppSettings.cs:593～642；同Settings.cs:291,515,893,991 | DeviceConfig本身没有Key，安全parser只取固定字段；但AgentDevices原字符串仍可含任意字段并原样保存/回显。feishuVerificationToken仍明文配置入出。非秘密快照须允许列表和结构化设备投影，不能仅剔apiKey或把JsonIgnore当安全证明 |
| %WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Settings\SettingsHotReload.cs:87～120,170～182；同Settings.cs:30～58 | 发布后步骤含待回复/参与/审批清理、消息窗口裁剪、后续registry.Save和外部TTS文件写入。都不等于同一配置TX；重放不得再次发布/裁剪/外部IO，部分应用/崩溃必须可查询且诚实报告未知 |
| %WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\BotConfig.cs:29～44；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\ModelProviderStore.cs:40～90 | 启动环境投影仍普通Save回写，旧入口不能绕版本；模型配置元数据与熔断运行态分开，SaveCircuit不计配置版本 |
| %WORKDIR%\bot-agent\src\BotAgent.Core\Domain\Platforms\PlatformId.cs:41～44；%WORKDIR%\bot-agent\src\BotAgent.Platforms\Governance\PlatformRegistry.cs:14～33,56～64 | 已有InstanceId，但实际索引只有PlatformId/AccountScope，不同实例覆盖；Enabled硬编码true。新增管理标识须实例隔离，不能继承这两个伪状态作为验收 |
| %WORKDIR%\bot-agent\src\BotAgent.Core\Domain\Plugins\IPluginRegistry.cs；%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Plugins\PluginManager.cs:41～79；%WORKDIR%\bot-agent\src\BotAgent.Storage\PluginStore.cs:9～48 | 查询端口可复用；SetEnabled仅内存，批量生命周期不等于热启停或禁用IO。PluginStore未发现生产采用，实例锁和先读后写不保证多个实例不丢更新；插件ID大小写与旧状态兼容须明确 |

- 父任务按真实Settings.cs写入段提取到153个body字段名引用，含审计专用ttsKey、clear与状态动作，不是153个合法可写字段/已冻结schema。只有逐setter/验证器/secret与即时动作分类后，才能成为完整字段矩阵。
- Core/Storage/Platforms/Model当前依赖维持第十四节事实；新Engine/Mcp/Panel/Web并未开工。MCP检索无匹配按查询结果登记，不作为协议互通验收。旧HTTP、新ZBA与跨介质应用必须分别确认兼容和安全边界。

### 15.3 三个候选与00推荐（设计评审，不是契约冻结）

1. **最小入口顾问**：候选IConfigurationChanges的Read/Save/Find三入口，隐藏安全投影、版本CAS、允许DB秘密、幂等、审计和提交后状态。优点是调用者很难绕开整批入口；代价是后处理仍非全局原子。报告用四个行为字段示例，但不能把四字段实现当作完整Wave1统一保存；本总纲明确拒绝用缩小覆盖解锁。
2. **扩展分区顾问**：候选IConfigurationWorkspace的Read/Validate/Commit/Query，封闭强类型Behavior/Platform/Model/Plugin变更代数，构造注入各消费者校验和事务实现；不采用object字典/任意JSON路径/反射分派。优点是多消费者扩展和跨分区错误本地化，代价是更多外部类型/入口；Validate不能代替提交时再校验/CAS。
3. **旧Host/早期06顾问**：候选OpenEdit/SaveAsync/FindResult/RestoreAsync的typed edit-session，默认保存顺序由module承担。便于兼容旧Host，但草稿生命周期/秘密驻留/重启恢复增加额外协议；其CapabilityRef示例只带accountScope，仍漏InstanceId，不能直接作为C4稳定标识。

00推荐三入口应用seam，内部复用强类型分区变更与纯旧HTTP映射：不建立持久化编辑会话，不把管理即时动作塞进配置事务。非秘密快照使用允许字段/结构投影，Core不引用Host AppSettings或Platforms、不公开SQLite连接；Storage内部抽取共享连接写原语/事务审计，Host保留运行映射与显式后处理。三个方案的新名称均尚未定义，示例不是可调用接口；下一批先确定精确Core类型/签名、完整字段矩阵及消费者确认再写消费代码。

候选共同不变量（本批确认评审方向，数值/完整类型尚未冻结）：

- trusted caller由认证adapter确定，输入不能伪造主体/授权域；读取、提交、结果查询均复核授权。幂等键不是权限，作用域含主体/授权域/操作种类；同键同参数先找原结果再判断新版本，同键异参冲突，秘密参数摘要仅内部保护保存，绝不进入DTO/审计/日志。有效期、过期墓碑及有界容量必须后续明确，过期/查无记录不得冒报未提交或静默重做。
- 先纯暂存、全部字段及跨模块校验，再在唯一SQLite事务复核版本/请求并提交本批配置、版本、允许DB秘密、结果账本、必要审计；任一失败不部分提交。不能在mutate中做secret.Save/文件/远程检查，不能嵌套已有Save；提交后才发布及应用，SQL成功不等于外部TTS文件成功。
- 缺省保留、非秘密显式清空/删除/重置与秘密Keep/Replace/Delete区分；同字段重复冲突、未知字段/分区、错误类型拒绝。秘密值采用write-only敏感输入，不让record默认ToString或序列化泄露；官方Secret在所有通路拒收/不导出/不回显，仍仅直接环境来源；其它秘密严格已有能力白名单，不泛化扩权。
- 持久化/发布/逐项应用分维度报告；部分应用、需重启、失败和未知不得合成伪失败或伪全部已应用。请求重放只读原账本，不重新换引用/清理/裁剪/外部IO；重启加载已提交版本，只有明确安全可恢复的投影可重建，不宣称事务外副作用恰好一次。取消穿越提交点时以查询核对，不由取消异常推断回滚。
- C4标识沿用既有插件Id、model provider.Id及平台PlatformId/AccountScope/InstanceId，显示名改动不改key；消息能力与管理动作能力分开，期望启用/实际连接/实例状态不混同。旧二元平台查询的歧义策略须消费者确认，不能自行破坏兼容。

| C4类别 | 必须具备的管理投影/配置契约 | 当前不能冒称的动作 |
| --- | --- | --- |
| 平台/驱动实例 | 三元身份、安全描述、配置来源/版本、期望启用、实际连接和错误；C3纯配置校验及保存意图 | 连接检查、热启停、取消/释放需真实adapter证明；不支持明确拒绝，已发远端消息取消不保证撤销 |
| 已登记插件 | Id/状态/能力/配置定义；持久化禁用意图与实际停止新调度分开 | 内存SetEnabled不证明禁用工具IO；初始化资源独立台账/停机释放，不能只遍历当前enabled项 |
| 模型 | provider稳定Id、安全元数据、配置校验、生效/重启规则、熔断作为独立运行状态 | 不把已有IModelClient/Transport当统一管理测试端口；远程测试需mock/取消/资源释放及出网门禁 |
| MCP | 后续按已批准范围登记server/tool标识及治理语义 | 当前无实现/无登记，不制造已支持项；不因unsupported免除Wave4后端准出 |

### 15.4 未冻结项、确切审查队列与下一合入条件

- **阶段一仍不能宣告可开工**：完整C3字段/秘密/生效/环境投影矩阵、精确类型/签名与消费者确认、版本及幂等TTL/容量/过期政策、旧HTTP兼容、C4实例/动作能力与取消释放、C5早期安全消费审查尚不完整。三个顾问报告完成不是正式消费者整体冻结；00不把这些缺口掩成可并行施工或Wave1准出。
- 后续01审查：拟新增（尚不存在、非认领）%WORKDIR%\bot-agent\src\BotAgent.Core\Domain\Configuration\ConfigurationContracts.cs、%WORKDIR%\bot-agent\src\BotAgent.Core\Domain\Ports\IConfigurationChanges.cs、%WORKDIR%\bot-agent\src\BotAgent.Core\Domain\Management\ManagedCapability.cs；实际事务实现候选%WORKDIR%\bot-agent\src\BotAgent.Storage\ConfigurationStore.cs、%WORKDIR%\bot-agent\src\BotAgent.Storage\AuditLogStore.cs。名字/文件划分随精确契约评审调整，不能当作已有调用入口或自动写权。
- 00共享补丁审查确切现有路径：%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Persistence\SettingsStore.cs、AuditLogStore.cs、BotConfig.cs、ModelProviderStore.cs（后三者同目录）；%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\AppSettings.cs、SettingsBox.cs；%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Ports\ISettingsRepository.cs；%WORKDIR%\bot-agent\src\BotAgent.Headless\Services\Settings\SettingsHotReload.cs；%WORKDIR%\bot-agent\src\BotAgent.Headless\Adapters\Panel\WebUiServer.Settings.cs、WebUiServer.Audit.cs、WebUiServer.cs；%WORKDIR%\bot-agent\src\BotAgent.Headless\Host\CompositionRoot.cs。均为审查队列，不因本批文档认领获得代码写权，不删除旧保存通路。
- 消费者评审队列：02对PlatformOptions/PlatformRegistry三元实例及字段生效，03对ModelProviderStore元数据/熔断及Model思考none语义，04对Plugins状态/工具调度及业务仓储，05对未实现MCP未来配置及远程失败语义，06对HTTP输入/安全DTO/恢复/启动与TTS跨介质；00登记实际确认者、时间、版本/文件并最终合入共享补丁。02/03代码仍冻结，04～07无新增写权，旧文件交接为空。
- 新ZBA具体认证/授权、Cookie-CSRF、跨域/流认证安全边界仍按面板规划由06提交、负责人批准；本批不改安全方案、不猜路由/OpenAPI。旧HTTP字段/null/裁剪行为兼容需逐项映射，不把新严格规则直接施加旧请求；CLI依旧只读，官方Secret禁令不因旧接口而豁免。
- 后续合成用例必须通过真实新保存入口/旧HTTPadapter：合法多分区及允许秘密一起提交；后段非法类型或各事务写点故障全回滚；secret.Save故障不得被吞；同版本竞争/同键同参/异参/响应丢失/重启/过期及有界容量；persist失败不发布、发布/派生/TTS文件失败保留提交事实、重放不重新清理/裁剪；设备嵌套未知secret字段和verification token不回显；启动环境投影不绕CAS；三元实例同scope隔离、插件禁用/停止/取消释放与unsupported动作。只用现有真实Review/SettingsScope/Architecture/Safety/Production工程及IntegrationHarness，新文件/公共入口认领后落地；集成前重建Headless，不造测试工程、不删原断言。
- 合入次序：完整契约/消费者及确切写权→真实负例→内部事务实现→00宿主及旧HTTP兼容接入→实际行为/兼容恢复验收→按逐文件交接处理旧实现；失败或证据过期冻结受影响下游，Baseline阈值不放宽。阶段一准入不等于Wave1准出，Web仍须Wave1～5/必选ZBA/安全资源回退齐全后负责人明确解锁。

### 15.5 本批命令、边界及回退

- 日期2026-10-10/UTC+08:00，唯一仓库pwsh。11:48:25～11:49:41的git HEAD/status/index/ls-files与文档/适用AGENTS/真实接口静态核对均退出0；11:54:09二十DLL/十七正文绑定pwsh0。没有本轮项目构建、探针/harness或业务测试，不将C2历史成绩改成新成绩。
- 辅助错误透明登记：上一轮额度中断、误读IStorage.cs、手册逆向脚本历史行匹配均已记录；恢复顾问报告最小两次IPlugin.cs/PlatformContext.cs不存在、旧Host顾问docs/engineering/IPlugin.cs不存在后停止并实际目录查真接口，修正查询0；扩展顾问MCP无匹配rg1是预期，不是产品失败。没有由错误创建文件或删除断言。
- 当前写权仍只有00本总纲；所有源码/测试/共享配置输入保留。本批回退只逆转15.1及随后第十五节/当前索引片段，先核后续编辑；不回退C1/C2/R1～R5，不整体覆盖文档或目录，不用强制checkout/hard reset/未跟踪清理。C2前缀逆向哈希可核对，但不称为旧二进制/生产恢复演练。
- 文档/所有514项范围、HEAD/index/status及本地链接/尾空白校验在本节下方追加；最终自引用SHA只在交付消息报告。00继续按持续授权完成C3/C4精确字段/类型/消费者冻结与准入，不重复请求是否批准；服务中断已解除，不把难度或未完当外部阻塞。

### 15.6 只读评审交付核对

- 2026-10-10T11:59:11.9926213+08:00，pwsh及git diff --check均0：1539行快照、本地链接5/全部可达、围栏标记4/闭合、尾空白0；五个拟新增候选文件确实不存在，不当现有接口。内存逆转本批两处当前索引并移除第十五节后的SHA精确为8127E1AF4F588F15A506533803BBD8562D02DC42CED3E0D247D7A507495D6040；原一至十四节保留，没有实际回退/生产恢复。
- 2026-10-10T11:59:12.9768587+08:00，同口径514项逐项核对：相对恢复输入仅本总纲改变，其余513项、HEAD、352展开状态与index均一致。本次追加前库存SHA E55E08E843DBAFBEF874AC31601D23B9493981EA178728C7430507EFB57C4508、总纲SHA 8439CA385D098CF9286A749BAEF8000CE2086B8F93D5C6CA328363EE97A797FB；均为带时间中间快照，不是最终自引用值。
- 00本批单文档认领收口，无共享写权移交/旧文件删除；三项只读候选实际交付，未创建执行波次、未改源或运行本批项目验证。后续契约实现、代码/测试文件及共享补丁须依持续授权先登记精确集合/开始哈希，接口未冻结/兼容不明停止受影响消费代码；不重复询问批准。
- 本轮为实质进展：完成C2记录/绑定收尾并交付三个C3/C4替代接口及消费者静态缺口；不是仅重述状态。尚未达到阶段一开工条件，目标继续active。下一最建议是把真实旧保存字段完整分类为非秘密、write-only秘密、只读环境、即时动作和不支持范围，完成精确Core类型/签名、幂等预算及真实消费者确认后作准入审计；不缩减到四字段，也不把只读候选当验收。Web仍未解锁。

## 十六、C3/C4完整字段契约盘点（2026-10-10）

### 16.1 授权与本批写权

- 延续负责人“00持续推进到阶段一可开工，无须重复询问批准”的授权。本批仅文档：00认领本总纲及新参考文件`docs/plans/agents/00-c3-c4-field-contract-review.md`；后者12:04:17核实不存在。新参考用于完整字段表，不在总纲复制另一份进度。没有源码/测试写权、旧文件交接、共享权移交或下游解锁。
- 起点2026-10-10T12:04:17.3148245+08:00：HEAD main/1f3181e21e8d1f9531e41066b033d8d60218d580；514项安全源码/文档库存SHA B9400F2042A5DFAE2FD58D5D7CB3B9A5DFD5BE2437D67F50D7A4A1639C6CBB6D；352展开状态SHA 43AB8B338E84450A291641A94A57E238A420ADBE87EB29089644BDB73818E538；index SHA 65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC；本总纲SHA 01420D77324E09FE81ED0BAEB04BB8C3CA62174086E7C37F23DC574647A3198E。与15.6交付一致，未发现外部漂移；不把既有352项计入本批成果。
- 阶段一仍未开工；字段及精确Core签名/消费者确认未冻结。本批以源码形状/表达式为证据，不读取配置实例、环境变量值、数据或日志；不构建或运行项目测试。回退只移除本节及本批新参考，删除新参考前核对哈希与后续编辑，不覆盖原十五节或既有工作区。
- 查阅工作区/仓库AGENTS、总规划、00及01/06手册，docs和src未发现额外AGENTS。使用writing-for-agents技能分离参考。12:04辅助rg对Windows字面通配路径报错，立即检查非零后pwsh抛错退出1；改为目录加-g查询成功0，不创建猜测路径，不视为产品失败。

### 16.2 完整字段参考交付与缺口

- 交付[C3/C4-field-review-0.1完整字段及秘密矩阵](00-c3-c4-field-contract-review.md)。逐行核对真实旧保存、回显、AppSettings/PlatformOptions、BotConfig、审计、UI和热更新：153个body引用并非合法写字段总数；142非秘密配置输入、9秘密/清除输入、mood即时动作、ttsKey仅审计，另2官方Secret存在性拒绝，共155项。48仅响应字段单列；170个public get/set属性由151关联属性+19旧保存未编辑属性完整覆盖，不缩成四字段。
- 差距附实际位置及输入SHA：Settings.cs:291/991飞书VerificationToken仍可序列化/明文回显且尚无Secret槽；515/893保存及回显AgentDevices原始字符串，而AppSettings.cs:593–599六字段DeviceConfig投影不等于写入口白名单；真实TTS写入418用ttsApiKey，审计167用ttsKey且无真实保存分支；Audit.cs:37提交前写applied不能证明Rebuild成功；BotConfig.cs:58–59环境覆盖FastModel/FastReply与注释种子意图不等价。这些是静态缺口，不称已动态复现或已修复。
- 真实链路仍WebUiServer→SettingsHotReload→SettingsBox→Host SettingsStore→唯一Storage AppDatabase→发布→运行重建/外部投影。矩阵完整覆盖空/保留/清除差异、名单原样与脱敏显示/nameRaw、Policy与legacy优先级、已提交/部分应用、TTS外部文件及mood非原子动作、启动writer/模型metadata/circuit分离；提出合成负例但未运行。未知大小/TTL/容量、全量精确Core DTO/端口、VerificationToken安全迁移与旧UI兼容、完整三元identity/消费者02–06真实确认仍是冻结前缺口；本文件及顾问报告不代签。
- 代码路径/共享补丁没有本批新增授权；沿十五节候选路径进行下一份精确DTO/语义评审，复用现有事务/审计/时钟/端口，不创造可直接调用的接口。优先完成01与02/03/04/05/06的具体消费映射/冲突处理，00再登记冻结、发放非交叉写权并审计阶段一准入。Wave2–6/Web保持原门禁，无部署/提交/发布。

### 16.3 校验及本批回退

- 2026-10-10T12:11:30.7134348+08:00，pwsh0/git diff --check0：155输入key覆盖、218本地链接可达及行号在范围、尾空白0。12:12:58.4786088独立分节校验pwsh0：142/9/4精确输入、155唯一、48响应差集、151+19=170属性并集均与当前源码一致；不是固定数量绿灯，先逐项集合对照再确认计数。多行属性定义扫描再次复核170唯一；没有项目构建/测试成绩。
- 初版增强校验使用PowerShell自动变量input导致响应差集误判，pwsh及时退出1；修正为requestKeys后形状差集为空/0，再执行上述带时间全量校验0。没有因此改产品代码/删断言/放宽护栏。之前弱First155截取校验已由独立分节精确集合校验替代。
- 12:13:24安全库存515项（新增参考一项）、353展开状态：相对认领起点仅本总纲与新参考改变；原其余513项、HEAD/index一致。中间库存SHA DE0CCD2FEB9CC1233FFC2D2D801EDF2EF692304F383B33683783FE9CACB8417D、状态SHA E01ADDF23F6341C9BDFAAA44F409F6D63DCB63056FC7B751C99B681F9BA2B954；新参考SHA F0734E3C0D22937FACB010E2D04C73F73A48F42C53B265381B49FC561221AD55。这是带时间快照，不自称最终总纲SHA。
- 12:12:58内存移除十六节并保持原四个LF尾换行，逆向得到认领起点总纲SHA 01420D77324E09FE81ED0BAEB04BB8C3CA62174086E7C37F23DC574647A3198E；后续两处当前索引需一并按原文本逆转。实际回退先核后续编辑，只逆本批片段；新参考仅在SHA一致且无消费者依赖时移除，不递归删除、不清理未跟踪目录、不覆盖既有十五节/源码。此为文档逆向证据，不是旧产物或生产恢复演练。
- 本批具体进展为完整旧入口及配置属性参考与源码差集校验；尚不满足阶段一准入。00两文档认领交付后收口，目标继续active；精确签名及消费者冻结需下一批单独登记。本轮未请求重复批准，没有启动执行波次。
- 2026-10-10T12:15:05.7380037+08:00收口复核pwsh0：严格分节142/9/4、48响应、151+19属性集合对照一致；内存逆转十六节及两条当前索引后仍精确得到认领前SHA 01420D77324E09FE81ED0BAEB04BB8C3CA62174086E7C37F23DC574647A3198E。12:15:07.2158296文档链接218可达/行号范围、尾空白0/git diff --check0；随后仅修正类型表尖括号显示和查询失败记录措辞，不改字段集合/源行为，最终指纹在交付消息报告。

## 十七、C3/C4精确类型与预算候选（2026-10-10）

### 17.1 本批认领及评审边界

- 持续授权不变。00本批只认领本总纲及拟新增`docs/plans/agents/00-c3-c4-contract-candidate.md`，12:17:14核实后者不存在；上一批完整字段参考为只读输入。没有源码/测试认领、旧文件删除/交接、共享写权移交或生产操作。
- 起点2026-10-10T12:17:14.6506667+08:00：main/1f3181e21e8d1f9531e41066b033d8d60218d580；515项安全库存SHA 1591C12E30BE61ACAE05E5121C4CCAD9945D6A70F6C3B6A2E46F8BB67E5957E9；353展开状态SHA E01ADDF23F6341C9BDFAAA44F409F6D63DCB63056FC7B751C99B681F9BA2B954；index SHA 65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC；总纲SHA 8CB012CC7947C01AD3A93D71F965D2E342509CED68A46BB33D988515590F20EF。与上批交付一致，无外部漂移。
- 先提交完整强类型/空值/结果/状态/预算候选，再依据codebase-design的Design It Twice要求进行三项不同interface的只读复核。顾问不写文件、不构建/测试、不读业务数据，顾问设计意见不冒充01–06的正式职责确认；未冻结契约仍不允许消费代码施工。
- 静态查询辅助错误透明登记：误将类型HostPluginManager当同名文件，Get-Content及时失败/pwsh1；首次仅Services候选名查无匹配，pwsh检查非零后停止1；扩大已存在src文件列表后找到真实`Services/Plugins/PluginManager.cs`，核对DeviceConfigFor/AgentBridgeServer的OrdinalIgnoreCase真实调用0。没有创建猜测文件或改动接口。

### 17.2 精确候选及只读复核派发

- 提交[C3/C4-candidate-0.2](00-c3-c4-contract-candidate.md)：完整142强类型字段分到七组，稳定FieldId1–142及注册对象/允许秘密3项扩展；Read/Save/Find的精确候选surface、Keep/Set/Clear、六个Secret槽（VerificationToken新槽仍待确认）、安全ReadField和结果/部分应用/Unknown、三元Driver/插件/provider/设备Key、C4查询/纯校验及Unsupported动作边界。没有新增可调用Core接口或源码。
- 预算不再只写有界：提交1MiB/深度16请求、普通字符串32768 UTF8字节、单秘密16384字节/六槽、24h key窗口/未来300秒、generation+时间戳+nonce格式、4096全局/256主体活跃条/8192字节回执及128设备/32Policy等具体候选。全部是待评审数值，非批准性能预算/现有生产能力；timestamp/epoch+clock floor/恢复仍有严格过期永久拒重做缺口，文档明确未证明，不用参数表掩盖。
- 2026-10-10T12:28:52.7781132+08:00候选SHA CE14D2B7A2B5A2F80993725112A7AE8DBD4DB989B2BADE64DD2721C8A5131B1E；在此前矩阵SHA 7FBB406C6199D9188152FB6E8BE0572B4B60A0E2ECD516F5F150F195FB630206基础上给三个既有顾问followup（最小/扩展/默认Host三个不同interface约束），只读精确修订：c3_minimal_resume、c3_extensible_resume、c3_host_zba_resume。followup已调用成功，不把意图/旧final当本批报告；观察真实live handle再等，不因timeout重新派发。顾问不拥有写权/波次，仍非01–06正式确认。
- 修正查询后真实PluginManager定义确认；Core PlatformPolicySettings定义在PlatformPolicy.cs，尝试类型同名路径失败/pwsh1后按真实定义检索0。ReadJsonAsync位于WebUiServer.cs:854–865，当前ReadToEndAsync没有该候选新body限额；不能把新预算表当旧调用已安全。候选的注册对象CRUD/凭据范围、C4动作interface不足、旧schema/secret/设备迁移、认证/预算批准均需精确反馈，消费者未签时不写消费代码。

### 17.3 核实输入错误与追加文档写权

- 12:33:43.0264053+08:00，00按真实定义定位后读源码/pwsh0：WhitelistGate.cs:79–88官方空名单实际全允许，不能沿矩阵泛化为空即全拦；FeishuBotGateway.cs:477–482取首个同平台Policy不比较scope/instance；PlatformPolicyResolver.cs:46–48/93–98仍二元IgnoreCase，与Registry三元迁移必须一起处理；CompositionRoot.cs:611–618拒绝空模型凭据，635–657允许读取任意secret:/env:引用；BotConfig.cs:88–99对已存配置不再播种VerificationToken。这些是静态事实，不是已访问真实环境或已复现安全事件。
- 00依持续文档授权追加精确认领`docs/plans/agents/00-c3-c4-field-contract-review.md`，仅纠正PluginManager真实名称、旧官方空名单/继承及新增真实Resolver/飞书scope/Token播种事实，保留142/9/4、48和151+19字段集合。追加前输入SHA 7FBB406C6199D9188152FB6E8BE0572B4B60A0E2ECD516F5F150F195FB630206；不扩为源或测试写权。当前本批总三份文档；顾问候选绑定仍CE14D2B…旧快照，修订后另记版本/指纹，不伪称旧评审批准新文本。
- 单个旧类型简称不再当真实接口；既有未知敏感credential引用不能因为新输入allowlist就算运行安全；新None不能声称旧模型支持匿名；官方空值兼容不擅自变成全拦或全放。候选0.2先拒绝冻结，待完整顾问报告比较后明确下一修订，正式消费者仍未确认。

### 17.4 三项实际交付与00裁决

- c3_minimal_resume交付12:29:19～12:32:03、c3_host_zba_resume交付12:29:52～12:32:39、c3_extensible_resume交付12:29:33～12:33:56，三个live handle均已completed，实际报告已收到。绑定CE14D2B…候选及7FBB406…矩阵旧输入，不是当前修订签字；只是Design It Twice顾问，不是01–06角色执行、负责人兼容/安全批准或波次确认。
- [候选第10节](00-c3-c4-contract-candidate.md#10-三方案复核与00裁决02拒绝冻结)依次比较三入口immutable Patch、四入口closed typed atoms及request-local editor，并推荐三入口/完整142成员、内部typed atoms、pure LegacyPlan，不建立持久draft。文件修订为0.2-review-1：保留0.2原surface供核对，明确整版拒绝冻结；裁决不是修完一致性的0.3。
- 完整修订清单明确NoChanges同TX账本、ContractRevision/秘密冻结所有权、安全typed wire、真实IClock floor持久、每次受控激活epoch及整库回退分类局限、跨代容量；Policy AccountDefault与Instance/旧官方空允许；provider Keep与runtime凭据允许列表/primary意图/非primary热路由；Fast兼容选择；VerificationToken初始化删除与缓存UI、设备Key与旧连接门禁、Plugin大小写/initialized资源；旧HTTP无CAS适配与新预算非旧兼容、mood/TTS独立事实、认证与C4动作缺口。全部是下版任务，不冒称已有调用/动态修复。
- 下一批最建议是整体一致0.3后按真实角色手册做01与06早期共同消费评审，再02/03/04/05逐对象确认；精确版本/hash/保留异议由00登记。不是再用三个设计顾问循环代签。新ZBA认证/预算/兼容行为未批准处仍停止受影响实施，不问例行批准但也不自授权业务变化。
- 本批辅助错误包括一次functions.exec JavaScript语法错误，解析时没有tool执行/写入，拆分后修正成功；顾问误猜Startup/OpenAiClient/AgentCommandRouter路径分别即时检查rg2/pwsh1/rg2停止，真实目录/定义检索后0。正常静态查询及后续校验单独记时间/退出码，不把辅助错误当产品测试，也不吞错换绿。

### 17.5 本批验证、兼容和回退收口

- 2026-10-10T12:41:32.9750577+08:00，pwsh独立分节集合对照0：142普通/9秘密/4特殊=155唯一输入、48仅响应、151关联+19未编辑=170属性。12:41:34.5088777本地链接218个可达及行号在范围、尾空白0、git diff --check0。不是仅数目断言，实际集合与真实Settings/AppSettings/PlatformOptions源码匹配。
- 12:41:47.4359353候选强类型/FieldId校验pwsh0：142唯一typed成员/142连续ID，与矩阵142key逐项一致，22围栏闭合、2文件链接可达、尾空白0/git diff --check0。结果只证明保留的0.2表覆盖，没有编译该候选，更不证明其被第10节拒绝的语义通过测试。
- 12:41:48.3527493安全库存516项/354展开状态，相对12:17起点仅本总纲、字段参考及新增候选三个认领路径改变；其余513项、HEAD/index均不变。中间库存SHA F034E0E6B32F356158DF2F900559A047A750815AB07FC72159CDEE825A0870EB，状态SHA 3F79627FF5A7F7AF3029DEF0603AA9101C3B982292949B5AF12E602732B66230，index仍65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC。最终自引用文档/库存hash只在交付报告，不能把中间hash当最终值。
- 12:42:08.5162734内存逆转两条当前索引并移除第十七节、保留原四LF尾换行，精确得到起点总纲SHA 8CB012CC7947C01AD3A93D71F965D2E342509CED68A46BB33D988515590F20EF/pwsh0，没有实际回退。字段参考仅按本批纠错段/两条header逆转并检查后续冲突，新候选仅在最终hash一致且无消费者依赖时移除；未验证整份字段参考逆向hash，不伪报。真实源码纠正通常应保留而非恢复错误事实。
- 本批三文档写权收口，无共享移交/旧文件删除，旧Host/HTTP及历史C1/C2保持；没有构建、项目测试、启动Host、数据库/环境值/日志读取、提交/推送/部署或生产动作。覆盖静态字段/类型/真实消费缺口；未覆盖0.3运行、新事务/幂等、secret/UI兼容、预算、真实管理动作及资源/恢复演练。安全/兼容待确认按候选10.2表冻结受影响消费代码，阶段一仍未可开工。
- 00持续目标保持active：本批完成完整精确0.2评审输入、三个不同方案实际比较及源码事实纠正，不仅重述状态；下一批先登记整体0.3文档写权、形成一致契约及正式角色消费评审。阶段一准入仍需冻结/无冲突写权/完整前置证据，Wave1验收与Web负责人批准是后续不同门禁，不自动启动全部波次。

## 十八、C3/C4整体0.3与早期消费评审（2026-10-10）

### 18.1 认领、输入与本批边界

- 持续授权下00本批只认领本总纲及`docs/plans/agents/00-c3-c4-contract-candidate.md`，字段参考只读；无源码/测试写权、旧文件交接或共享临时移交。目标是替换0.2的矛盾surface为整体0.3，并按01/06真实手册评审，不把设计顾问旧意见当签字。
- 起点2026-10-10T12:43:43.8082093+08:00：main/1f3181e21e8d1f9531e41066b033d8d60218d580；516项库存SHA 3D963A91F6F409D0AEE2987A5ABD96923E811FFFC6BCBA81B9ADB4910E27FD98；354状态SHA 3F79627FF5A7F7AF3029DEF0603AA9101C3B982292949B5AF12E602732B66230；index SHA 65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC。总纲SHA EBF4E9DB557D5DD468BE62EA83413142E13F366381116D316F772A4D2811BAFE；候选SHA F824B65510B9C7D65677B7CACB0C4D5C3C74830544877080855C1C60768B17DC；只读矩阵SHA 2FC8A1C55AA1E62A741EFC0D4B4F44DFC20CC86EB19584F355BA37F9CF1BC7E1。与上批最终指纹一致。
- 先读适用AGENTS/规划/00和真实01/06手册，第一条角色路径误猜01-core-and-storage.md不存在，pwsh1及时停止；随后枚举agents目录，实际01-agent-core-and-unified-storage.md、06-agent-headless-cli-and-deploy.md读取0。不创建猜测文件。后续只按真实路径和定义查询。
- 阶段一开工与Wave1准出分开：完整C3/C4契约、前置基线/授权及非交叉写权是开工门禁；新保存行为、宿主真实调用/兼容恢复是实现后Wave1验收。06前期消费评审不是Wave5全量ZBA施工或Web解锁。不可把未来测试已规划说成当前通过。

### 18.2 整体0.3与角色评审委派

- 00将唯一自有候选整体替换为0.3，原0.2-review-1文本在内存保留，历史hash仍可核。本版完整142成员/FieldId、ImmutableArray嵌套、安全wire/请求用户态与effective/apply来源、NoChanges持久决策、跨代共享容量、真实IClock floor/受控启动epoch；PolicyTarget/各平台legacy空规则、provider Keep/凭据runtime限制/primary意图、Fast保留环境优先、Token初始化删除/缓存UI异议、Device身份/旧连接门禁均在同一版，没有并存已拒绝0.2规则。
- C4本版明确List/Inspect/Validate及独立Execute/Query/Cancel闭集，配置仍仅C3写入，当前无真实adapter动作Unsupported，不把surface存在当能力验收或永久豁免MCP/后续波次。旧HTTP无版本writer适配与新strict预算分开；兼容升级、安全及资源批准项仍明确保留，不由00自行批准。
- 本次依01手册三.2和06手册三.1执行**早期只读消费评审**：既有c3_minimal_resume接01职责，c3_host_zba_resume接06职责；不是重复Design It Twice顾问工作。两者无写权/构建/测试/Host运行，只交本具体版本逐需求的接受或拒绝、实际源码调用/路径和确切阻断项，00登记真实执行者/时间/hash。角色技术确认不是负责人业务/安全批准，也不冒称02～05已确认。
- 评审输入候选SHA 87BAEBE4E070B38A8F006F6D4117E24C83B045F67B7D20FEB8E405C973ACBD2D；矩阵SHA 2FC8A1C55AA1E62A741EFC0D4B4F44DFC20CC86EB19584F355BA37F9CF1BC7E1。正式followup成功与live状态另记，未派发/未收到前不写完成。评审期间冻结候选文本，真实异议收齐后才修订新hash。
- 辅助错误透明：一次误按EffectivePlatformPolicy类型猜同名文件，pwsh1即时停止，随后rg真实定义/PlatformPolicy.cs读取0；一次长pwsh写命令被Windows CreateProcess os206拒绝，未创建进程/无文件写，核候选仍原F824…后使用已认领单文件apply_patch整体更新成功。没有因此改源码、造猜测文件或跳过退出码检查。

### 18.3 只读评审启动与范围核对

- 01/06两个followup均调用成功，12:54之后list_agents按确切path分别确认c3_minimal_resume和c3_host_zba_resume为running；待specific live handle交付，不因为timeout重启。不将旧completed设计报告重复记为本次角色结果。
- 另登记既有c3_extensible_resume执行02手册的只读消费评审：平台identity/PolicyTarget/名单继承/legacy镜像、secret环境/字段生效/C4能力，不拥有Platforms代码/测试写权、不运行Wave2。03/04/05后续分别按真实角色手册评审再登记，不把02报告代签其他消费者。本版候选冻结87BA…供评审，矩阵仍2FC8…。
- 12:52:54.9266502+08:00强类型校验pwsh0：142typed成员/连续FieldId/矩阵key集合零差，围栏22闭合/尾空白0/git diff --check0。12:52:55.5854080独立矩阵155输入(142/9/4)、48响应、151+19=170属性逐集合与源码一致/pwsh0。
- 12:54:09.8970676三文档本地链接及围栏/尾空白、八输入源码hash及git diff --check均0；12:54:11.3189647一致性校验pwsh0：七组30/53/16/12/16/3/12、C3三入口/C4六入口、五项已拒0.2规则无残留。静态检查不代行为/编译/消费者验收。
- 12:54:21.0166690库存516，相对本批起点仅候选和总纲变化，其余514项（含矩阵）、HEAD/index、354展开状态均不变。中间库存SHA A60EEB947DCEFE70C1A886D577C1FE60640F194A650D25FDFF4457CCE17D7745；最终hash另报告。本批没有构建/测试/Host/数据或env读取，无代码准入/旧文件删除/生产操作。

### 18.4 三项正式技术报告已收到（本版不能冻结）

- 01执行者c3_minimal_resume，12:53:29～12:56:21；02执行者c3_extensible_resume，12:55:23～12:58:18；06执行者c3_host_zba_resume，12:53:43～12:58:26，三者本批实际报告均已收到，起止输入候选87BAEBE4E070B38A8F006F6D4117E24C83B045F67B7D20FEB8E405C973ACBD2D/矩阵2FC8A1C55AA1E62A741EFC0D4B4F44DFC20CC86EB19584F355BA37F9CF1BC7E1一致。结论均有条件接受，包含明确拒绝项；不是无条件确认/负责人批准，03/04/05尚未本版正式审查。
- 01要求条件发布协议（现SettingsBox.Action persist正常返回会无条件Exchange，Rejected/NoChanges/Replay不得发布）、mg1缺ClockRegression/UnsupportedContractRevision enum、Replace合法UTF8且Trim后非空。源码实际核对SettingsBox.cs:46～64、SettingsHotReload.cs:73～、SecretsStore.cs:95～120；不能用“正常return治理拒绝”直接接原发布链。
- 06拒绝Fast统一env优先：旧HotSave直接用户值，Startup固定env覆盖，API key/fly凭据也有阶段/alias差异，纯重构必须保留并报告RestartRule；须补精确输入codec、Validate包含本批SecretEdit、DeviceKeyAssignment回执映射/8192预算、C4独立动作授权。Token旧空无法区分Keep/Delete，提出未声明revision旧空整批拒绝刷新；旧无Key设备全表按名称保留/新增/删除，删除是否新增deny tombstone是业务裁决，不能自称透明兼容。
- 02要求Policy继承真值表、Instance不得改legacy账号baseline、local/飞书双名单不能有损镜像再反向恢复、已声明配置不等于loaded adapter（否则关闭平台无法配置启用）、canonical真实动作授权交集及旧别名不扩大许可。新增核对ConversationId无Instance/Codec legacy IgnoreCase短key碰撞及消息源/出站实例；三元驱动key不自动证明会话实例隔离，业务共享或隔离尚需明确。
- 三者均只读、未构建/测试/运行Host/读取真实数据/env/logs；01/06正常shell退出0，02一次误枚举Platforms/Messaging不存在pwsh1停止后按实际Core定义修正0；06旧固定行提取141改按章节得到142零差。实现后Wave1行为测试仍待做，但不因尚未施工就否决一切契约设计；本版确切矛盾须修订新hash并窄复核后再推进冻结。

## 十九、当前代码与文档公开评审快照（2026-10-10）

### 19.1 本次专项发布授权与范围

- 负责人本次明确要求推送当前进度/文档到ZhaoJun233/bot-agent的review/core-platforms，随后明确“代码也推送”。授权仅当前代码/文档快照、必要本地提交对象及该分支普通快进推送；不推main/dev、不强推、不部署/发布应用或生产操作，不批准0.3契约/波次。
- 源仓库唯一%WORKDIR%\bot-agent，当前main/1f3181e21e8d1f9531e41066b033d8d60218d580及既有354状态/20暂存删除保留；源码既有改动是发布输入，不改称00本任务全部成果。17:50:06.1258108+08:00，557登记路径/465现存文件，逐路径哈希或MISSING全量库存SHA 5EDC76A1103E0599DE38CE2F0EE5638DD962BB06256AA46D6BD6B5C166FE0925；index SHA 65BAD5C332E0A9C57B0A9ABD3EE2DFF8ACF9364DCE375D0F47BFF1F5D72146BC；展开状态SHA 3F79627FF5A7F7AF3029DEF0603AA9101C3B982292949B5AF12E602732B66230。
- 远端指定分支已通过git ls-remote核实为56d3f4dd9e912ff1d7f295e12fb9d51cb2c53073；只将这个父提交作为公开评审快照的基底，源HEAD/index不移动。采用独立临时index→已提交快照对象→git archive该精确commit→独立公开临时clone的脱敏树；不直接推带源历史的提交，避免内部历史外泄。
- 00追加本总纲发布记录写权及仅本批派生目录.git/agent00-publish-20261010-175006/**（helper、临时index、快照/克隆、归档及安全计数证据）；该目录须解析后在本仓库.git内且原先不存在。不修改源码/测试/CI/部署或现有外部发布脚本，不写原暂存区；不将临时helper纳入公开文件。不清理他人目录。
- 全量源码/测试/文档及必要根文件作为封闭文件清单，排除真实data/logs/runtime/.env/密钥、构建产物及内部handoff；.env.example仅合成模板且仍受双审计。先冻结输入指纹，归档后复核无漂移。现有发布main固定dev不能盲跑，复用其REPLACEMENTS/FORBIDDEN/历史禁路径与两独立审计原实现，参数化snapshot扫描，绝不放宽RULES或删门禁。
- 推前本地快照黑名单/类别审计及git diff --check，推后指定分支完整独立clone复查内容+历史；全部native检查退出码，原始命中只在内存按计数/类别报告不回显。异常停止，不自动强推/改main。未进行本次构建/业务回归，推送不代表当前C3/C4或Wave1已验收；恢复下一阶段时仍以真实工作区/角色条件为准。

### 19.2 准备期辅助错误与快照等价格式

- 初次库存函数名H与pwsh内建Get-History alias冲突，pwsh1停止；改HashText后17:50全量库存0。一次工具参数缺cmd解析拒绝，无native执行；上一步发布逻辑只读查询Python stdout GBK编码异常1，改UTF8后查询0，未执行错误链后续fetch。以上不是项目测试失败。
- helper首次封闭路径判断将合法src/Services/NapCat误作运行目录，prepare1/未提交；按既有.gitignore根锚定纪律改为根napcat/runtime/data/vendor（bin/obj/node_modules/.git仍全路径拒绝），没有放宽类别RULES/黑名单。第二次临时index整批diff --check退出2，立即停在源快照commit之前、尚未推送。
- 17:56:24仅诊断形状确认10处：候选/矩阵/总纲及两C#文件IQqActions.cs、LegacyJsonImporter.cs的EOF空行；两规划Markdown头部五行硬换行尾空格。它们是已存在输入，不归为本任务代码成果。00只授权本批临时index/public projection：EOF归一到一LF、Markdown既有硬换行转等价br；两C#仅文件末尾闭括号之后空行，源码/业务字符串/断言/阈值不改。未知新格式问题仍停止，不全局自动删空白。
- 原工作区文件（除本总纲明确发布登记）及原index保持；快照格式差异和脱敏替换均计数登记，先重新取得diff --check0再commit/归档/审计/推送。源输入raw哈希与归档blob分别说明，公开快照不称逐字节原文件或已动态验收。
