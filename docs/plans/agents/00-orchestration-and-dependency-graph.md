# Agent-00：重构执行总纲与证据登记

> 修订：2026-10-07，第二版。先读[总规划](../modular-monolith-refactoring-plan.md)。
> 本文件是执行状态、共享写权限、基线验证和交付记录的唯一来源。
> 执行登记更新：2026-10-19。**Wave5 完整准出已由 Agent-01 独立复验 Accepted（当前未提交工作树/Windows 合成范围）：W5-M5、W5-G1、W5-G3 保持 Accepted；W5-G2 最终产物重测在负责人确认预算内；W5-G4 的 18 条全部修复（8 场景定向 109/0、全量 1086/0）。未发现 High/Medium。Wave5 到达 Wave6 入口；Wave6（Web）仍锁定，需负责人明确解锁。** 详细补验记录见 §27.10；§27.9 保留 11:45 只读核对的原始缺口清单。2026-10-19 11:02–11:39 的独立 Accepted 只覆盖 W5-M5／当时合成范围。Wave4 与 Wave5 的历史验收、退回及整改分别保留在第二十六、二十七节。

## 一、为什么（Why）

防止拟建接口冒充现有接口、孤立工程冒充宿主集成、历史成绩冒充当前验收，以及多个 Agent 覆盖共享文件。任务手册是施工约束，不是自动开工指令。

## 二、做成什么样（What）

### 2.1 当前证据与状态

当前核对日期：2026-10-19；对象：当前仓库工作区，不是干净发布提交。既有改动保留，不计入本轮文档任务成果，不整理、不提交这些代码。各节均为带时间与范围的历史记录；当前 Wave5 完整准出结论以 §27.9 为准，不将 W5-M5 局部复验 Accepted 或历史阶段准入扩大为全部后端门禁通过。

| 波次 | 源码存在 | 宿主接入证据 | 验收状态 |
| --- | --- | --- | --- |
| Wave 1 | Core、Storage工程存在；C3/C4类型及Core surface已实现 | Headless→Core/别名Storage；21个旧Persistence入口为兼容类型/转发，实际逻辑采用Storage | 已发布并验收（源 checkpoint `9b60769`） |
| Wave 2A | Platforms 工程存在 | Headless 直接引用 Platforms；四平台生命周期/来源路由已接入 | 已技术准出（2026-10-17） |
| Wave 2B | Model 工程存在 | Headless 通过薄 facade 采用 Model 传输 | 已技术准出（2026-10-17） |
| Wave 3 | Engine 工程存在（只引用 Core） | 回复主链、工具目录、插件注册/门禁与有界后台队列在 Engine；Headless 保留 I/O 适配与装配 | 已独立验收（2026-10-18 第五轮，R10 关闭）；停在 Wave4 入口，Wave4 未解锁 |
| Wave 4 | Mcp 工程已建立（只引用 Core；MCP 2025-11-25 / SDK 2.2.0 / Streamable HTTP） | Engine 动态工具治理 + Headless 装配/出网复用 + S53 宿主真链路 | 整改后 19:25 增量独立复验 **Accepted**；到达 Wave5 入口 |
| Wave 5 | `BotAgent.Panel` 已建立（只引用 Core）；Headless 同进程承载 ZBA + 只读 CLI；wwwroot 改为发布目录 | W5-M5：ZbaProbe 152/0 ×3、S54 57/0、Architecture 171/0；W5-G1：S55 17/0 ×3、真实重启审计 10/0 ×3、独立复验 Accepted；W5-G3：最终 G4 产物 64 文件回退 drill pass=True（Headless `6A5DDD70…2FD89D`）；W5-G2：最终产物重测在负责人确认预算内；W5-G4：18 条全部修复，定向 109/0、全量 1086/0 | **Wave5 完整准出独立复验 Accepted（无 High/Medium）；到达 Wave6 入口；Wave6 未解锁** |
| Wave 6 | 独立 web 工程尚未建立 | 新面板尚未交付 | 未解锁；需负责人明确批准 |

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









## 二十、拒绝项收敛（2026-10-11）

### 20.1 授权、写权及本批输入

- 依据负责人持续推进至阶段一可开工的授权，00继续独占本总纲及`docs/plans/agents/00-c3-c4-contract-candidate.md`。本批修正01的条件发布/错误枚举/秘密归一，以及06的Fast兼容与Validate秘密输入缺口；其余异议保持待办，不扩展波次、不授予源码写权。
- 14:17:00.2717946+08:00：HEAD仍为1f3181e；展开状态354项、暂存20项。候选输入SHA 87BAEBE4E070B38A8F006F6D4117E24C83B045F67B7D20FEB8E405C973ACBD2D；总纲输入SHA 8EE96784258EC649E20074C48F5E0C7FA17B87AEB3B77E79B9A1847F94EC5674。已保存本批候选原文、状态/index清单和五份相关源码SHA作前后核对，不覆盖既有变更。
- 本轮list_agents仅root，旧角色handle不再存活；旧报告不作为新版本确认，未重复启动全波次。上一轮状态说明属于no progress，本批实际修订纠正该停滞。
- 调用链重新核对：WebUiServer.Settings.cs:131 → Services/Settings/SettingsHotReload.cs:73～85 → SettingsBox.cs:46～64。当前persist正常返回后无条件Exchange；新语义不能靠persist内return拒绝。Storage/SecretsStore.cs:95～120的空白删除/Trim规则也不能直接当新Replace规则。
- 辅助失败：误将SettingsHotReload放在Services根目录，pwsh1停止后按实际Services/Settings路径读取0；全总纲JSON快照输出截断导致解析失败，未写文件，改为候选原文+总纲hash成功；两次不匹配apply_patch校验拒绝，无文件写。均不是项目测试失败，没有继续错误链。

### 20.2 0.3.1实际修订与窄复核

- 本批实改候选：条件发布的明确谓词及同writer串行顺序；mg1新增ClockRegression/UnsupportedContractRevision及非强转映射；Replace严格UTF8且Trim后非空；Fast保留HotSave用户值、Startup环境别名优先的旧分阶段行为；Validate请求包含revision/version/patch/secrets且无持久化副作用。完整142输入及第3/4节typed分区逐字未变，没有缩小目标字段范围。
- 已登记两个新只读评审执行者c3_031_storage_review、c3_031_host_review：仅01/06本批拒绝项，无任何源码写权/构建/测试。旧handle缺失已由list_agents证实，不是因观察超时而重复启动。
- 14:20:39.7356484+08:00：pwsh静态检查0，FieldId表142连续，围栏闭合、相对链接存在、无尾空格；候选SHA D09594B4D86374C1CF4EBAE23CDA99C9265ECF27827DAD778C55C96335AAA3BB。前后五份相关源码SHA、354状态及20暂存条目完全相同。检查覆盖文档结构和本批源码输入稳定，不是编译/项目测试或全仓库内容无漂移证明。
- 01执行者c3_031_storage_review截至14:20:19+08:00对该SHA四项窄复核接受：条件发布、mg1错误映射、Replace、含秘密Validate；这些范围无剩余文档阻断，但条件发布seam尚未实现。一次中文章节标题匹配错误pwsh1，修正后0；其它查询0。不能以01签字代06或其它消费者。
- 覆盖与未覆盖：本批没有构建、项目测试、Host、数据库、真实环境值或日志读取；没有代码提交/推送/部署。既有工程变更保留，不记作本批新实现。0.3.1仍非整版冻结，输入codec、Device回执、独立管理授权、Policy/legacy/实例会话以及Token/设备兼容选择仍待解决，03/04/05尚需实际评审。
- 回退：仅逆向本批候选差异、本总纲第20节及顶部状态行，先核后续依赖与当前hash；不得恢复整个工作区或原index。原候选字节与输入hash已留作本轮差异比对。没有数据/schema变更，未声称生产回退演练完成。下一步针对尚存拒绝项继续修订，不重复要求例行批准，也不以本次局部接受解锁阶段一。
- 06执行者c3_031_host_review，14:20:00～14:20:35+08:00，候选前后SHA同D09594…；接受Fast分阶段兼容、含秘密Validate、条件发布Host边界，原“统一环境优先”和“仅Patch全批校验”异议可关闭。仅这些修订通过窄复核，不代03或关闭codec/管理授权/Token/Device待办。一次章节检索引起非终止Select-Object参数错误，改真实中文标题后完整读取成功；报告其余pwsh退出0，无构建/测试/写文件。
- 00结论：本批五项文档矛盾已有实际修订，01/06相关项按同一hash接受；保留其它明确阻断，不宣告统一保存实现、当前基线验收或阶段一开工。下一批应直接解决剩余codec/授权/Device回执的精确契约并完成03/04/05消费核对，而不是重审已关闭的同一问题。

### 20.3 0.3.2写权与消费评审

- 持续授权及原00独占两文档写权延续；本批拟补strict closed input codec、DeviceKeyAssignment回执和提交前编码预算、独立管理授权seam；不改源码/共享工程/测试，不跳过旧兼容业务裁决。输入候选D09594B4D86374C1CF4EBAE23CDA99C9265ECF27827DAD778C55C96335AAA3BB，总纲FED303595E6AA2E6039D4C64B6C0B604A0A370BA9BD6ABF8268519CA4E661147；HEAD仍1f3181e，前版候选原文保留供仅本批逆向。
- 前一goal turn判定progress：五项实际修订且01/06窄复核已接受。新启动三个只读实际消费者c3_model_consumer（03）、c3_engine_consumer（04）、c3_mcp_consumer（05）；只取真实源码事实和自身手册的契约消费意见，无施工写权、构建/测试/Host/数据/env值读取。0.3.2完成后发送精确hash再确认，不冒称修改中旧hash已确认新版。
- 真实入口补核：WebUiServer.IsAuthorized在无面板密码时直接allow，不能继承为新管理授权；旧Settings的agentDevices仍是字符串全表，不存在可直接回传的新DeviceKey映射。新C4 grant/codec/receipt均是拟建契约而不是猜测已有API。

### 20.4 0.3.2与review-1实际证据

- 中断后list_agents仅root，确认前批03/04/05句柄已缺失，不是观察timeout；候选仍D09594…，没有未观察的候选写入。前批只读事实推进了后续修订选择。本批追加前版五项之外的closed codec、Device新key回执及precommit最坏编码预算、独立管理授权/Validate grant；并按03/04/05实际异议明确凭据阶段与禁止resolver缓存复活、插件关门、mood不可凭bool报Applied、MCP稳定RegisteredId。本批仍只写原认领的两个文档，无源码/测试/工程写权。
- 20:56:21.0516234+08:00静态检查pwsh0：142字段连续、围栏/尾空白/相对链接通过，0.3.2候选SHA 3A9677097B8338165ABCFACCE0750388384D794EFDDC0F4E50702AEE48769DF0。仅文档结构检查，不是codec运行证据。
- 新实际评审者：01 c3_032_storage_contract_review（截至20:58:52），06 c3_032_host_contract_review（现场窗口20:57:52～20:58:45），03 c3_032_model_contract_review（20:57:37～20:58:54）；均绑定3A967709…只读。01接受新增三组；03首次正式接受自己的Core消费范围、明确primary Priority/Enabled ReadOnly与nonprimary RestartRequired；06仅拒ObjectKey/Command合写device kind，其它本批指定范围接受。01一次错找Storage下SettingsStore/AuditLogStore退出1后按实际Headless适配器修正0，06/03正常查询0；不算产品失败，不代其他角色确认。
- 负责人本次业务答复明确Token“旧空值整批拒绝并提示刷新”。00据此将旧Token空/纯空白在任何配置/秘密/mood/TTS副作用前整批LegacyNeedsReview；Missing/nullKeep，非空Replace，新strict显式Delete，GET configured/source与UI删除/刷新通路同批迁移。本裁决只解决Token业务选择，不自动批准设备删除、实例会话、新认证或资源预算。
- 21:00:51.3950233+08:00生成review-1 SHA B6ACC22844BFB632217A1E1F3F90DC62006D943519CB69710015DF80EDBEB19E：只另拆ObjectKey五kind/Command四kind、澄清实际Headless与Model副本、落实Token裁决。01在21:01:16～21:01:27、06在21:01:15～21:01:24、03在21:01:25逐差异窄确认接受并重绑定该hash；不重新扩张已接受设计，不将消费者签字作为源码接入。
- 20:59:33.1895161+08:00按本仓库已有封闭安全manifest的557行逐文件哈希/MISSING核对，对比10月10日已发布源输入仅候选与总纲两路径不同，HEAD仍1f3181e、暂存20项。此核对不读真实数据或env值，不替代测试基线；不覆盖manifest外未知文件。原工作区其它既有改动保留。
- 04/05/02新正式只读复核者c3_032_engine_contract_review、c3_032_mcp_contract_review、c3_032_platform_contract_review已派发；固定review-1 hash，分别核自身消费范围。02只提交Policy/目录等最小补充建议，00最终合入；各角色没有源码写权，不启动Wave2～4。
- 辅助失败：root误读不含Governance的PlatformPolicyResolver路径，pwsh1停止后按rg真实路径修正0；一次过窄QqAction glob无匹配rg1，后用实际动作字面量定位Services/Agent/QqActionTool.cs退出0。只读失败不通过造平行文件修复。
- 中断前临时内存原文在新tool会话不再可用；本批不冒称已有逐字恢复快照。文档回退须按本批明确片段做逆向，核相应输入hash及后续写入，不覆盖整个文件/工作区；没有数据迁移、构建/项目测试/生产恢复演练。未提交或再次推送，旧47e6607远端不包含本批文档。阶段一仍待其它技术闭合、业务裁决和当前准入证据。
- 为后续只回退明确本任务片段，00另认领仅`.git/agent00-contract-20261011-2104/**`保存本批自有文档安全快照与元数据（不含源码或运行数据，不公开推送）；创建前校验解析在本仓库.git且目标原不存在，保留旧发布派生目录，不清理他人文件。

### 20.5 当前源码基线刷新（不启动Wave1实现）

- 复用4.1已批准的合成基线验证授权与派生产物认领，本次00仅执行真实Headless Release构建、ArchitectureProbe Release构建及普通架构验收入口；写入仅各对应已忽略bin/obj。20:59安全manifest证明与已发布源码输入相同，但不将旧测试自动称当前通过，故主动刷新这两个前置证据。
- 先核ArchitectureProbe.csproj无主工程引用、Program/SourceIndex为只读源码扫描，不起Host/数据库/真实数据/网络；不使用--print或--self-test替代普通架构验收。不改Baseline/阈值/断言，不跑集成测试、生产、容器或无筛选日志；每个原生命令独立检查退出码，失败即停后续并登记归属。安全输出仅构建状态、警告错误计数和架构计数。
- 00继续复用同一既有合成基线授权，扩展本次刷新到SafetyProbe与solution构建，以及既有s21/s36/s52三个定向harness入口（不是全矩阵，S42仍未验收）。追加对应忽略bin/obj及本任务派生目录中新UUID合成根认领。安全探针清除继承配置变量，仅按名称操作，不读取/回显值；两套DATA_DIR显式同一新根、关闭文件日志。集成前再独立Headless Rebuild，父harness不带数据别名，按源码场景自设新根与本机mock，固定本仓库新DLL/单个ONLY，不使用外部模式。

### 20.6 最新消费签署与实际基线结果

- 唯一当前候选0.3.2-review-2 SHA B517D3F58E54D3DE31970C82F7D22C438A7E612EBA6F7396B1FA88198763C198。02提出的逐字段Instance→AccountDefault→canonical/newscope baseline、无损legacy/local旧union、canonical动作交global/hard/runtime、配置声明目录与loaded/NotLoaded区别已一次补齐，typed签名/142FieldId/01和06seams未删减。

| 实际角色执行者 | 同一review-2 hash的签署时间（+08:00） | 结论 |
| --- | --- | --- |
| 01 c3_032_storage_contract_review | 21:10:59～21:11:29 | Core/Storage技术范围接受 |
| 02 c3_032_platform_contract_review | 21:08:45～21:08:56 | 四组精确异议关闭，平台消费接受 |
| 03 c3_032_model_contract_review | 21:11:16 | Model范围保持接受 |
| 04 c3_032_engine_contract_review | 21:12:39～21:12:55 | Engine技术范围接受，设备/会话业务不代答 |
| 05 c3_032_mcp_contract_review | 21:12:48 | MCP预留消费接受，不免Wave4实体验收 |
| 06 c3_032_host_contract_review | 21:11:06～21:11:47 | HTTP消费/Policy/目录窄复核接受 |

- 六角色为技术消费确认，均无源码写入/构建/项目测试/运行数据读取；01/04/06本轮各有一次旧路径误查后定位修正，错误退出1/2与成功退出0已报告，未形成源码缺失结论。02拒绝项不是00代签，当前设备删除与同账号实例会话仍等待负责人实际答案。

| 00真实命令（仓库根pwsh） | 2026-10-11时间（+08:00） | 退出码/结果 |
| --- | --- | --- |
| dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release --nologo | 21:05:12～21:05:18 | 0；增量0警告/0错误 |
| dotnet build tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release --nologo | 21:05:32～21:05:34 | 0；0警告/0错误 |
| dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll | 21:05:34～21:05:35 | 0；302源码文件/53182行，159通过/0失败 |
| dotnet build tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release --nologo | 21:06:55～21:07:09 | 0；0警告/0错误 |
| dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll | 21:07:09～21:07:21 | 0；594/0 |
| dotnet build BotAgent.slnx -c Release --nologo | 21:10:22～21:10:27 | 0；增量0警告/0错误 |
| dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild --nologo | 21:10:27～21:10:36 | 0；8既有CS0108/0错误，不称全构建无警告 |
| dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release --nologo | 21:10:36～21:10:37 | 0；增量0警告/0错误 |
| QQCHAT_IT_ONLY=s21；dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll | 21:10:37～21:10:46 | 0；37/0 |
| 同harness，ONLY=s36 | 21:10:46～21:10:54 | 0；98/0 |
| 同harness，ONLY=s52 | 21:10:54～21:11:01 | 0；119/0 |

- 本次定向真实调用链：Headless独立Rebuild后，以QQCHAT_BOT_DLL绑定该仓库新DLL；s21真实HTTP模型设置保存/换mock端点/重启，s36真实官方Secret拒收及显示脱敏/raw key/nameRaw，s52真实平台开关/策略/本地入站/mock/outbox/重启。不是仅程序集编译，也不是未来C3统一保存已实现；新TX/版本/幂等/回执和条件发布仍为Wave1实现后的验收。
- 父验证进程清除继承BOTAGENT/QQCHAT/OPENAI/MINIMAX/TTS/HARNESS配置及HEALTH_PORT，仅列名称、不读取或打印值。Safety两DATA_DIR同一新UUID合成根；harness父无数据别名，各单场景自行新根和本机mock，未用外部模式/真实协议端。输出仅计数/状态，没有读取真实数据/数据库正文/未筛选日志。
- 日志声明更正：Safety调用曾设置QQCHAT_FILE_LOG/BOTAGENT_FILE_LOG，但它们不是真实关闭开关，不能宣称全探针文件日志已关闭；真实Host开关为QQCHAT_LOG_FILE且由CompositionRoot处理，Safety自身仅部分测试直接关闭FileLog.WriteToFile。该Safety根始终是新合成目录，未读取任何日志正文；本次安全结论依赖隔离而不是错误开关。harness StartBot清QQCHAT父变量，不能假设父日志开关会生效；各场景只产生其合成数据，仍不读取未经筛选日志。
- 8个CS0108是HttpFetcher继承接口的既有成员隐藏；当前文件SHA 0D1FC3B8F07EC880D38599591A14F4B7A49DD27F30839E47A71003EEF0CD8984与封闭源清单一致，历史743/927/1408行已登记同类8警告。增量0警告不表示已消除。未改阈值/断言、不将警告当新绿证据。
- DLL指纹：Headless 5E20031B7914D61D8FB2AFFFF05642F7893FBEE2694CC103ABE811E8879FB652；harness 40268FF4F2F13D0A6A1E4CBB396397EDCDC2E71C0F8F74269FF5DF0C04E8D5C8；Safety 62B5FACFB39161BEC887AFB0A81C9BCBBBD3632D025D26AF6708792930FE7B44；Architecture 664342F7E07AAEFA20EC38ABDCC57D48D476BA3A451CBF89E0AD6C55CB683B56。一次hash查询遗漏Architecture bin/Release路径退出1，修正后全部hash读取0；一次模型场景路径误用ModelSettingsScenario退出2，定位真实ModelConfigScenario后读取0，均未继续错误链。
- 未覆盖：全探针/全harness矩阵、S42真实官方协议、s50本次重跑、未来统一保存/C4真实动作、MCP互通、资源负载和产物/数据库恢复；没有部署、生产或本批代码提交/推送。阶段一仍非已开工；当前新的基线结果不是Wave1准出。

### 20.7 阶段一准入核对与下一交接

| 开工条件 | 当前权威证据 | 当前结论 |
| --- | --- | --- |
| 持续推进/基线授权 | 当前聊天目标、4.1及本节验证认领 | 有效；不是自动生产/提交权限 |
| 当前源码/引用/旧改动保留 | 557封闭路径核对、HEAD/index/status复查，变化仅00两文档 | 已核；未经本批认领的旧源码不归本批成果 |
| 01与02～06技术消费确认 | 20.6六角色同B517D3…签署，包含06早期协作 | 技术范围已齐，不再把这些角色一概写成“从未评审” |
| Token兼容选择 | 负责人实际答复，review-1技术窄核 | 已明确；同批实现和负例仍为Wave1义务 |
| 设备删除/改名兼容 | 已询问负责人，尚无实际答复 | 未完成，不代答 |
| 同账号多实例会话共享/隔离 | 已询问负责人，尚无实际答复 | 未完成，不靠DriverKey三元声称会话已隔离 |
| 当前基础验证 | Headless/solution/Safety/Architecture及s21/s36/s52结果见20.6 | 定向当前基线通过；非全矩阵、新保存验收、资源或回退验收 |
| 代码认领/接入方案 | 下表是精确拟交接，尚未发源码写权 | 准入前不能视为已授予01写权 |
| 统一保存/迁移/回退合成用例 | 候选9.2及20.6未覆盖项 | 明确实现验收义务，不冒称开工前已实现通过 |

- 第一阶段01拟认领（仍未准入）：Core `src/BotAgent.Core/Domain/Configuration/ConfigurationContracts.cs`、`Domain/Ports/IConfigurationChanges.cs`、`Domain/Management/ManagedCapability.cs`、`Domain/Ports/IManagedCapabilities.cs`；Storage `src/BotAgent.Storage/ConfigurationStore.cs`及现有`AppDatabase.cs`/`SecretsStore.cs`的同连接原语与schema/version/ledger变更；测试仅新`tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs`，公共Program入口由00合入。实际拆分/namespace先与唯一已冻结surface对齐，新增friend/internal文件须在写前补登记，不从此表推导任意文件写权。
- 01不写/不删除旧Headless文件。旧`Adapters/Persistence/SettingsStore.cs`、`AuditLogStore.cs`、`BotConfig.cs`、`ModelProviderStore.cs`及共享`Services/SettingsBox.cs`、`Services/Settings/SettingsHotReload.cs`、`Services/AppSettings.cs`、`Adapters/Panel/WebUiServer.Settings.cs`/`WebUiServer.cs`、`wwwroot/app.js`、`Host/CompositionRoot.cs`均由00最终合入；先保留旧调用，按同TX与条件发布/Token UI同批方案逐补丁验收，再移交/删除，不能并发写。方案共享范围不等于本批新增源码写权。
- 02/03只读确认不是Wave2写权；Wave1准出+无交叉认领后才并行。04/05只读确认也不是Engine/MCP准入。06仅早期协作，Host/解决方案/引用/共享设置/护栏/公共测试/CI/部署由00最终写。07仍锁定，Web必须所有前置门禁及负责人明确解锁。
- 21:15:20.9565910+08:00两文档围栏/链接/尾空白、142FieldId连续及scoped git diff --check退出0；候选保持B517D3…。原生Git stdout字节口径index SHA B88D046BA5B191EFD3A4E8F98974FC1395B9BB2ABA5CC7D691F41ED5EA611A1B、status SHA 4F36ACD5746F7541F6A8CDA3A18D9EB816FCB819B449468A3E6F19D121C79033，分别与原发布输入完全相等（与LF-join旧口径不混比）。候选review-1安全原文快照在已认领.git派生目录，若回退只逆向本批明确片段并先查后续依赖，不做整仓恢复。
- 00结论：本轮新增技术异议已收敛并取得真实六角色消费签署，且真实宿主定向基线已刷新；当前不再以未知的技术评审队列重复绕圈。剩余两项具体业务答复尚缺，完整契约未冻结，阶段一未开工，goal保持active；不把未来统一保存/资源/回退验收降格成此次定向基线。

### 20.8 专项准入证据补齐

- 前一goal turn为progress：六消费者同hash接受、当前基线与真实宿主三场景通过；本轮继续补齐已经批准的合成基线中的ReviewRemediationProbe、SettingsScopeProbe及S50，不重复契约设计。原00两文档独占和忽略bin/obj/独立合成根认领延续；不修改源码/阈值/断言，不增加其它波次写权。
- 先核真实入口：Review Program按新UUID双DATA_DIR初始化，含共享DB/独立child/旧格式导入/写事务/恢复/clock用例；SettingsScope Program同样新UUID双DATA_DIR；S50实际为Program:122→RunFeishuPlatformScenarioAsync→FeishuPlatformScenario.cs，不是猜测的独立Dedup文件。一次不存在FeishuDedupScenario路径查询退出1，立即停止，按真实注册入口重查0；没有创建猜测文件。只执行这些新合成目录的代码，不读取真实数据或任何日志正文。
- 所有测试前清继承配置变量，仅按名称处理，不回显值；Review/Settings自行设置合成根，IT再独立重建Headless及harness并固定本仓库新DLL。失败即停后续准出，已完成其它证据仍保留但不隐瞒失败。

| 本批00真实命令 | 2026-10-11时间（+08:00） | 退出码/结果 |
| --- | --- | --- |
| dotnet build tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release --nologo | 21:19:45～21:19:49 | 0；0警告/0错误 |
| dotnet tests/BotAgent.ReviewRemediationProbe/bin/Release/net8.0/BotAgent.ReviewRemediationProbe.dll | 21:19:49～21:19:51 | 0；主汇总192/0，独立child另列，不叠加冒充192范围 |
| dotnet build tests/BotAgent.SettingsScopeProbe/BotAgent.SettingsScopeProbe.csproj -c Release --nologo | 21:19:51～21:19:54 | 0；0警告/0错误 |
| dotnet tests/BotAgent.SettingsScopeProbe/bin/Release/net8.0/BotAgent.SettingsScopeProbe.dll | 21:19:54～21:19:55 | 0；failures=0，未臆造成功计数 |
| dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild --nologo | 21:23:45～21:23:52 | 0；8既有CS0108/0错误 |
| dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release --nologo | 21:23:52～21:23:53 | 0；增量0警告/0错误 |
| QQCHAT_IT_ONLY=s50；dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll | 21:23:53～21:23:58 | 0；14/0 |
| dotnet --version | 21:25:07 | 0；SDK10.0.101，工程target net8.0，pwsh7.6.3/Windows |

- Review覆盖实际共享数据库门面、跨门面写失败回滚、禁止嵌套写、future schema拒绝、bound-root旧导入、合成备份恢复及clock初始化/竞态等；独立child形状：storage-uninitialized4/0、typed-import5/0、future4/0、restore3/0、clock-uninitialized10/0、binding-race12/0，主统计192/0不包括把这些强行累加。SettingsScope现有失败前不publish/提交后失败不伪回滚/秘密runtime JSON/审计原语/旧own-message迁移通过。S50使用本仓库新Headless DLL走真实飞书入站→Core dedup端口→共享SQLite事务的故障/重试/重启链，协议和模型均本机合成mock，非真实平台。
- 本次四个定向IT（s21/s36/s50/s52）证据已齐；无新增配置版本/全批秘密/幂等key/DeviceKey/C4动作实现，因此不把这些旧调用链测试当未来统一保存验收。S42及完整矩阵/资源/部署恢复仍未覆盖，不降低下游准出门禁。
- DLL SHA：Review 891C4803182211C44869134FF58E08976DE0B23147D94EA298D41026B6E57652；SettingsScope 3F34500AA20BE52104540885C3B45997057FF26FE6C65A53EE1883B8ECCCF2FB；Headless与harness仍为20.6相同5E20031B…/40268FF4…。源码/候选未改，B517D3…仍唯一技术确认版本；本批只追加自有总纲及忽略式派生产物，无代码提交/推送/生产。
- 00准入结论：既有C1/C2前置技术路径和兼容恢复新增当前合成证据，阶段一开工阻断没有扩大。设备删除/改名与会话共享两项仍无负责人实际答复，不因持续工作授权推导业务答案；完整freeze及源码认领未放行。下一步在实际答复到达后只处理其确切语义，不重开已签署技术范围。

- 本批安全证据另封存于已认领`.git/agent00-contract-20261011-2104/candidate-review-2.md`及`baseline-readiness-20261011.json`：仅契约原文、提交/文件hash、命令/时间/退出码/计数、待决项，无运行正文或秘密；不纳入公开仓库。

### 20.9 完成与阻断审计

- 本轮重新核验候选B517D3…与封存证据一致，HEAD仍1f3181e；六角色技术确认及基线结果仍有效。list_agents只有root运行，仍列出的03/04/05执行者均completed，没有可等待的测试或评审句柄；不把聊天意图/文件标记当verified wait。
- 前一goal turn为progress（Review/SettingsScope/S50真实通过并封存安全证据）。本轮核验未改变准入结论，应归no progress，而不是靠重复检测/写状态制造进展。剩余阻断在连续三个goal turn保持同一条件：20.7/20.8/本次审计均缺设备删除/改名与同账号实例会话两项业务实际答复；Token裁决已经收到，不重复询问。
- 依完成审计，完整契约冻结尚不成立，源码写权未发，阶段一可开工没有被证明；不能改目标为“基线绿/技术评审完”并标complete。依阻断审计，所有不依赖该两项业务裁决的准入技术工作已完成，无可安全继续的受影响实现；新启Agent、重跑同一绿测试或重复扩写文档不能解决该条件。按goal规则申请blocked状态，最终状态以工具返回为准，不是负责人主动暂停，也不是任务完成。
- 恢复条件只需实际业务答案：设备删除后禁止连接且按稳定Key改名，或保留旧默认启用语义；同账号实例共享会话，或每实例隔离。收到后只落实这两项所影响的精确契约/兼容与写权，不重开已确认技术项。不默认代答、不降低门禁、不推送/部署，不触碰其它旧工作区改动。
## 二十一、业务裁决闭合与阶段一准入（2026-10-11）

### 21.1 授权、输入与本轮认领

- 负责人本轮实际答复：**禁止连接，共享会话**。对应20.9两个恢复条件：删除设备后拒绝连接；同平台同账号多实例共享会话上下文。Token旧空值整批拒绝刷新裁决延续。本答复不授予生产、提交、推送、部署或Web解锁。
- 00在2026-10-11T21:37:52.0187180+08:00 延续两文档独占：00-c3-c4-contract-candidate.md输入B517D3F58E54D3DE31970C82F7D22C438A7E612EBA6F7396B1FA88198763C198；本总纲输入D5B94B313C7309AB92187504728B8366DD095F557B130939AA5EC3BC60583160；各原文封存于已认领.git/agent00-contract-20261011-2104/*-before-business-close.md。本轮只闭合两项确切语义并审计准入，不重开20.6已签技术范围、不启动下游、不改源码。
- 当前安全manifest557行复核仍只此两文档不同，HEAD仍1f3181e21e8d1f9531e41066b033d8d60218d580。git status --short186行是目录折叠口径，原--untracked-files=all354行是展开口径，不把口径差当新增改动；准入封存再比较原生字节hash。既有源码和20暂存删除不计本任务成果，不整理或清理。
- 两次只读路径误查（InboundMessage独立文件、Storage GlobalUsings.cs）及一次不存在Headless Properties目录rg均立即非零停止；按真实ConversationId.cs内含消息类型、Storage.csproj Using和实际文件定位修正，只读错误不作为接口缺失证据。本轮未构建/项目测试，20.6/20.8有效基线按源码指纹绑定沿用。
### 21.2 精确阶段一认领与旧文件交接安排

- 唯一实施席位为Agent-01，认领在21.3冻结/准入核对通过时生效；本次stage1_01/02/06_business_close等窄评审执行者仅只读，没有源码写权。尚未派发实施，不把reserved/可开工写成施工中。执行者绑定实施席位后，先逐文件复核此表输入、全部适用AGENTS和冻结hash；不需重问本轮例行批准，但任何真实漂移/碰撞先停相应文件并报00。

| 写入所有者/交付 | 仓库根相对确切路径 | 本轮输入/限制 |
| --- | --- | --- |
| 01→00 | src/BotAgent.Core/Domain/Configuration/ConfigurationContracts.cs | NEW；完整142FieldId及C3类型，不删字段换绿 |
| 01→00 | src/BotAgent.Core/Domain/Ports/IConfigurationChanges.cs | NEW；物理Ports但FQN保持BotAgent.Domain.Configuration.IConfigurationChanges |
| 01→00 | src/BotAgent.Core/Domain/Management/ManagedCapability.cs | NEW；C4最小跨模块类型，非所有真实动作实现 |
| 01→00 | src/BotAgent.Core/Domain/Ports/IManagedCapabilities.cs | NEW；BotAgent.Domain.Management的IManagedCapabilities及IManagedAuthorization |
| 01→00 | src/BotAgent.Storage/ConfigurationStore.cs | NEW；同一AppDatabase协调器，C3实现，不嵌套旧Save |
| 01→00 | src/BotAgent.Storage/AppDatabase.cs | SHA033C222A9FB5B5D282A04F798EC25E0F1C8043A6199B91629B14AC25AE2E22C6；增量schema/version/ledger/tombstone，保留旧改动 |
| 01→00 | src/BotAgent.Storage/SecretsStore.cs | SHA074D4701B5EF8C045D47FFAD36560F32308900F7BEE144B25A9335AA9118CE15；同连接内部原语/错误传播，不扩大Secret范围 |
| 01→00 | src/BotAgent.Storage/ConfigurationStorageBridge.cs | NEW；仅确需Host同连接过渡的internal窄桥，不开放任意secret/env/独立DB |
| 01→00 | src/BotAgent.Storage/Properties/AssemblyInfo.cs | NEW；仅BotAgent.Headless、BotAgent.ReviewRemediationProbe精准friend，不能泛授权 |
| 01→00 | tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs | NEW；仅新合成测试实现；Program公共入口由00合入 |

- 9.3文件/FQN划分先取得01/06确认；真实Core/Storage csproj均net8.0/TreatWarningsAsErrors，Storage只引Core，Headless当前Core/Platforms/Storage引用，无Model直接引用。本表没有csproj写权；增工程/引用/新额外文件或不同friend使用者先报00登记，不从模块名扩张认领。
- **旧Host文件不交给01写或删。** 00独占以下最终合入队列（各前缀均src/BotAgent.Headless）：Adapters/Persistence/SettingsStore.cs、AuditLogStore.cs、BotConfig.cs、ModelProviderStore.cs；Services/AppSettings.cs、SettingsBox.cs、Settings/SettingsHotReload.cs、Agent/AgentBridgeServer.cs、Agent/AgentCommandService.cs；Adapters/Panel/WebUiServer.Settings.cs、WebUiServer.cs；wwwroot/app.js；Host/CompositionRoot.cs。另tests/BotAgent.ReviewRemediationProbe/Program.cs公共入口仅00。14条真实文件本轮逐条存在检查通过；其它AgentCommandService partial若需要必须另记准确路径，不自动授权。
- 00只在本阶段准入后按独立补丁批次核输入hash→登记具体改段→合入，不能把整队列当已修改。批1同TX/版本/回执/条件发布与新公共测试；批2完整legacy writer与Token GET/UI/旧空整批拒绝/显式Delete；批3Device同Key与撤销/hello/在线选择/worker/control及回退；每批真实宿主调用链和合成负例通过再交接。涉及后续Platforms/Model/Engine/MCP时先等对应波次准入并另登记；不让临时并行写共享文件。
- Agent-02～07本轮**确切源码写入集合均为空**。02/03到Wave1准出后才逐文件登记Platforms/Model及旧Headless交接，无交叉才并行；04随后登记Engine与旧回复/工具/Plugin交接；05登记MCP真实SDK/适配与Core消费；06本轮仅早期契约协作，未来Panel/CLI独立路径另登记，Host/解决方案/引用/设置/护栏/公共入口/CI/部署仍00最终写；07锁定直到Wave1～5、必选ZBA、安全、资源、回退齐备且负责人明确解锁。未来未建立文件不预授无限目录写权。
- 回退只逆向此批拥有的确切文档/源码片段并核后续依赖；保留已有未提交改动、暂存20删除、未跟踪文件和旧兼容通路。新数据库迁移/产物回退是实现准出义务：合成前镜像与相容整套版本，旧binary不识Device撤销须先关闭桥入口，不将本轮文档备份当生产回退演练。

### 21.3 实际消费签署、唯一冻结与写权生效

- 之前stage1_01/02/06_business_close在中断后live list仅root、三个句柄均missing，尚无可登记报告；据真实终止状态续派窄评审，没有把观测超时当终止重启。新执行者实际报告如下；全部无写入/build/test/运行数据读取，git no-index退出1为检查后的预期差异，不是构建/测试失败。
- 本次01/06只提出同一两处最小澄清，00一次补齐：删除commit后publish/重建/撤销同步或读取失败均fail-closed，旧SettingsBox允许快照不能继续放行；共享Token+自报host不是物理身份，不将自报Key变grant，不夸称防改名/冒用另一授权名。01/02/06随后窄绑定最终hash，没有重新扩展设计。

| 正式角色与实际执行者 | 2026-10-11最终签署时间（+08） | 对最终hash结论/命令退出码 |
| --- | --- | --- |
| 01 stage1_01_narrow_resume | 22:03～22:05:12 | 接受；存储/Key/会话/FQN/十文件范围，pwsh0、差异1 |
| 02 stage1_02_narrow_resume | 22:05:28 | 接受；共享上下文而实例权限/路由独立，pwsh0 |
| 03 stage1_03_unchanged_binding | 22:06:29～22:06:44 | 接受；Model/凭据/Fast/签名表面未变，读取/哈希0、差异1 |
| 04 stage1_04_final_narrow | 22:05～22:06:57 | 接受；真实桥/路由消费语义，读取0、差异1 |
| 05 stage1_05_unchanged_binding | 22:06:40～22:06:53 | 接受；第8节完整文本同旧版、MCP表面未变，读取0、差异1 |
| 06 stage1_06_narrow_resume | 22:05:40 | 接受；两处补文关闭、共享Host/HTTP范围成立，pwsh0 |

- **00于2026-10-11T22:09:26.7438610+08:00 冻结C3/C4-0.3.2**。权威文件docs/plans/agents/00-c3-c4-contract-candidate.md，SHA256 **7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203**；strict请求ContractRevision仍cfg-0.3.2，不因评审后缀造新协议；字段矩阵SHA2FC8A1C55AA1E62A741EFC0D4B4F44DFC20CC86EB19584F355BA37F9CF1BC7E1，完整142非秘密/9秘密兼容/48响应/170属性保留。本版签名/语义修改必须登记新版本并窄核实际消费者；不能自行改已冻接口或造HTTP路由。
- **阶段一准入通过；21.2的01十文件认领现生效，单一实施席位reserved/尚未派发，未开始源码施工。** 只读复核者不自动转为实施者；后续00将实际实施执行者绑定席位，核输入后可开工，不再重复询问本轮业务/例行授权。00十四旧Host/公共入口队列独占有效，每批写前登记input hash/改段；无旧文件临时移交、无其它写者。Stage1准入与Wave1准出是两道不同门禁。
- 01/02/04/06各只读路径定位错误已纠正，01/03/05预期diff1已显式检查；04两次错误路径1、02错误查询1/2、06rg通配2/shell1后成功0。root另一次误猜Headless GlobalUsings.cs引起rg2/shell1，立刻停止，改只查真实CompositionRoot和实际csproj Using成功0；未因误查创造平行文件。上述不隐藏、也不虚报成当前产品行为故障。

### 21.4 完整准入审计、实施验收与下游门禁

| 具体开工要求 | 本轮当前权威证据 | 00核对 |
| --- | --- | --- |
| 本次任务/业务授权明确 | 当前持续推进到阶段一可开工目标、21.1实际两答案及已裁决Token | 满足；无生产/推送权限扩张 |
| 当前源码/项目引用/真实旧调用链 | 557安全路径逐hash仅00两文档变；Core无业务引用，Storage只引Core，Headless引Core/Platforms/Storage且未引Model | 满足；旧改动保留，非本轮源码成果 |
| C1/C2现有前置接入和运行基线 | 真实CompositionRoot:69～72先绑定Platforms/Storage clock→唯一AppDatabase→typed旧导入；Review现有共享DB/迁移/future schema/clock及备份恢复用例20.8；Safety/Architecture20.6 | 满足当前开工前置；既有局部契约不冒称全部重构已完成 |
| C3/C4完整业务面及消费确认/06早期协作 | 本节六正式角色同7B7CB92A…、142FieldId连续唯一；Token/设备/会话裁决已闭合 | 满足；统一保存/C4真实implementation仍未实现 |
| 唯一写权/旧文件交接/共享合入 | 21.2十01路径+十四00队列，01两个现有源输入hash匹配，八NEW路径未存在，无并发源码写者 | 满足；新额外文件或引用必须先补登记 |
| 适用当前基线实际执行有效 | 原安全baseline receipt SHA52001EED153C764F5B9C282A59D9CFBCA3E0F4E3D8384544A5BA25558EBCEE8C及20.6/20.8逐命令/时间/exit；本轮源码与六DLL指纹均未漂移 | 满足定向准入基线；不重跑同一绿测试制造进展 |
| 实施用例/兼容/回退/失败归属可执行 | 冻结契约9.2/9.3及下列准出要求、明确角色与旧补丁队列 | 满足开工计划；不把未实现用例当已验收 |

- 22:07:00+08 pwsh退出0：557封闭源路径对发布源输入仅两个owned文档不同，六DLL逐hash与20.6/20.8一致；142FieldId完整唯一、两文档围栏/尾空白和scoped git diff --check通过，字段矩阵hash未变。21:41:22原生git stdout字节口径index B88D046BA5B191EFD3A4E8F98974FC1395B9BB2ABA5CC7D691F41ED5EA611A1B、status4F36ACD5746F7541F6A8CDA3A18D9EB816FCB819B449468A3E6F19D121C79033与封存源相同，展开354项/暂存20删除；不混用186目录折叠或LF-join摘要。封闭清单外未知未跟踪文件不读取、不清理、不算覆盖。
- 当前基线实际结果沿用20.6/20.8的原命令/时间而不是称本轮新执行：Headless/solution构建退出0，独立Headless Rebuild八既有CS0108/零错误；Architecture159/0、Safety594/0、Review主192/0（children另列）、SettingsScope failures0（无成功计数）、真实宿主s21=37/0、s36=98/0、s50=14/0、s52=119/0。仅CLI版本查询/静态核对不算新的行为验收，未启动容器/协议端/生产或查看日志。
- **Wave1准出仍须**：01完整统一保存/全部分区+秘密同TX、CAS/幂等/receipt/floor/容量与故障/并发；00同锁条件发布（仅首次Changed+Committed）、所有legacy writer/版本参与、Token write-only GET/UI/旧空整批拒绝与显式Delete；Device stableKey/tombstone/hello/任务/control及commit后failclosed；安全Read/masking/raw key/nameRaw；新测试公共入口真实注册、独立Headless Rebuild后的真实Host调用链、完整适用回归及合成迁移/整套产物数据库回退。不以孤立Storage编译、预期diff1或旧四场景替代。
- **失败处理**：基线与源码指纹一一绑定；实际新失败按改段/接口/调用链定位owner，不能仅按时间甩给某Agent。失败/未知兼容或证据漂移冻结受影响接入/下游、保留原断言/棘轮，先定位再最小修复；确认是既有失败也记录未覆盖/影响，不降低门禁。Shared补丁按21.2分批核对和集成；无真实调用链证据不删除旧实现。
- **下游解锁**：Wave1验收后02/03非交叉逐文件认领才并行；02的来源路由envelope/dedup实例范围与03真实传输接入仍需对应准入/验收，03不得动态resolver null回落启动缓存。Wave1/2后04、随后05、最后06后端/ZBA集成；各波次真实生命周期/管理/取消/资源释放与回退门禁不免。Web必须Wave1～5、必选ZBA、认证安全、资源实测与回退完整，再由00提报负责人明确批准，00本轮不能解锁。
- 未覆盖且不冒称通过：新增统一保存/Device/C4实现行为、全probe/harness矩阵及S42官方协议、MCP SDK/StreamableHTTP互通、代表性资源测量、生产部署/回退。源码存在、旧Host采用、已有行为基线通过和新阶段可开工四种事实分别报告。本轮仅两文档修改及ignored安全证据，未提交/推送；远端旧47e6607不含本轮闭合。
- 完成审计结论：原目标“阶段一可开工”现有逐要求正面证据，不改目标为小型基线任务；本轮结束Agent00准入准备，后续实施从21.2明确席位/路径和冻结hash开始。未宣称Wave1验收、其它波次启动或项目完工，实际goal状态以工具返回为准。
- 最终安全封存路径（既有00 ignored派生认领）：.git/agent00-contract-20261011-2104/contract-frozen-0.3.2.md、stage1-admission-20261011.json。仅文档、输入hash、实际签署及基线命令/时间/exit/计数；旧baseline-readiness-20261011.json保留历史原样，不能把原phase1Open=false覆写成从未阻断。

## 二十二、Agent-01 实施席位绑定与共享集成（2026-10-12）

### 22.1 本次授权、准入与写权

- 负责人本次明确答复：**授权 Agent-01 十文件实施与合成验证，协调 Agent-00 合入共享补丁**。当前主聊天/root 绑定 Agent-01 唯一实施席位；/root/agent00_integration 绑定 Agent-00 共享最终集成席位。21.2 十文件范围与十四共享队列保持，未移交共享源码给01；无提交、推送、部署、发布、生产或下游源码写权扩张。
- 00于 2026-10-12T10:19:00.1917597+08:00 复核：唯一仓库 %WORKDIR%\bot-agent；HEAD=1f3181e21e8d1f9531e41066b033d8d60218d580；契约 SHA256=7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203；AppDatabase 输入033C222A9FB5B5D282A04F798EC25E0F1C8043A6199B91629B14AC25AE2E22C6、SecretsStore 输入074D4701B5EF8C045D47FFAD36560F32308900F7BEE144B25A9335AA9118CE15 均匹配；八NEW路径均不存在。当前status既有改动/20暂存删除保留，不归本任务成果。三次pwsh只读核对退出0；没有运行build/test。适用src/tests/docs嵌套AGENTS搜索为空；两级AGENTS、总规划/执行总纲/01手册及冻结契约已读取。
- 本总纲本批输入SHA256=A370A1F157596A8C8B45733F1199DC48887596BA3A522203AE26A7A9761E1F92。只增本节，当前仍无源码改动；Agent-01原十文件准入生效，可在原有约束下实施。各共享补丁实际写前另登记输入hash/改段/协商接口，串行验证防共享构建产物碰撞；实现准入不等于Wave1准出。

### 22.2 待批准的精确范围调整

- 01发现142 Changes+142 View属性、145 FieldId及其它契约按正常格式难以在唯一ConfigurationContracts.cs内满足Domain每文件≤300棘轮，不能压行或放宽阈值。请求额外六路径均为**待负责人授权，尚无写权**：src/BotAgent.Core/Domain/Configuration/ConfigurationFields.cs、ConfigurationChanges.cs、ConfigurationViews.cs、ConfigurationObjects.cs、ConfigurationEdits.cs、ConfigurationSecrets.cs。namespace/FQN/冻结成员保持；不改契约hash。
- SensitiveInput秘密无public借用接口。拟在额外ConfigurationSecrets.cs放精准assembly InternalsVisibleTo(BotAgent.Storage)，仅私有复制/消费seam；Storage既认领Properties/AssemblyInfo.cs仍只授权BotAgent.Headless及BotAgent.ReviewRemediationProbe。此额外Core路径同样待批准，不能因需要friend自行越权。00审查接受最小私有seam方向，但具体代码须核仅秘密接管副本/无公开逃逸、finally清零与合成Dispose负例。

### 22.3 实际六路径批准与扩展认领

- 2026-10-12T10:19:14.4617092+08:00 收到负责人实际答复：**授权六个精确拆分文件，保持契约和护栏不变**。22.2六个Core精确路径现授予/root Agent-01；加原21.2十路径，共16文件完整独占写权。ConfigurationContracts.cs保留请求/结果及ReadField，其它分别Fields enum、Changes、Views、Objects、Edits、Secrets；FQN/成员/namespace及冻结hash不变，非无限目录写权。00确认Core精准friend BotAgent.Storage仅为SensitiveInput私有复制/接管，不新增公开原值/任意callback；Storage friend保持两准确使用者。每文件≤300规则照旧；实际内容超限须继续拆分授权，不能压行绕过。
- 席位绑定与新增认领完成。01可实施上述16路径；00只写本总纲及原共享队列独立登记批次，原旧Host/共享设置及公共入口不移交。未构建/测试/提交；Wave1未验收，下游仍锁定。

### 22.4 当前源码写入入口与Host接入缺口（只读）

- SettingsStore仅Headless实现：Adapters/Persistence/SettingsStore.cs:82整JSON与AuditLogStore.AppendInTransaction同连接；SettingsHotReload.cs:78→SettingsBox.ApplyPersisted正常return必Exchange，现无结构结果条件发布。新保存需同_gate读取/持久/条件publish，不能旧锁外判断。
- Legacy参与者：BotConfig.Load:44把environment overlay重写settings（新真源必须停止该overlay回写）；CompositionRoot:105→ModelProviderStore.EnsurePrimary→UpsertMetadata写primary，重启不能覆盖用户元数据；Storage/LegacyJsonImporter.cs:95首次settings INSERT在受控Host激活前。SecretsStore公开旧Save也须版本参与；SettingsStore direct Save及PanelDeploy.cs:271经ApplyRuntimeSettings的稀疏URL写需统一下层覆盖。PanelDeploy不在共享改动队列，不因找到调用自行扩写。
- WebUiServer.Settings.cs:131旧mutate内五种允许secret独立Save（FeishuSecret/EncryptKey、TtsKey、AgentServerKey、ApiKey），Token明文settings字段291、GET991；afterPersist另含TTS/mood。实施应先纯LegacyPlan，全批校验→同TX配置/秘密/version/audit/receipt，外部步骤真实账本分开；TTS target注入不得合成测试fallback生产挂载。
- Device当前AppSettings.DeviceConfig无Key，ParseDeviceConfigs六旧字段，IsDeviceEnabled未匹配true；AgentBridgeServer hello453按host登记，PickBridge153、worker692及SendAsync827无持久撤销检查。需稳定Key/active+desired/tombstone同TX、hello/在线选择/每次worker/control派发均查已提交事实且Unavailable fail-closed；不得信自报key或旧SettingsBox授权。原AgentCommandService.cs路由需消费同门禁，不因任务已入队重放允许。
- 本轮两只读误查：Storage/SettingsStore.cs不存在、rg带Windows路径通配AgentCommandService*.cs退出1/2；立即停止并改实际Headless文件及rg目录-g过滤，后续退出0。不用误查创造重复接口。上述为当前源码差距，不是已运行复现故障；没有新build/test成绩。

### 22.5 共享批1a：条件发布seam登记（实施前）

- 2026-10-12T10:22:01.8397449+08:00 Agent-01确认00先合入硬编码SettingsBox.ApplyConfiguration：唯一改路径src/BotAgent.Headless/Services/SettingsBox.cs；输入SHA256=04F0FABCC7AA5AB341AF97C5E4CDB0DC36C1D637522513A41D2B5F46E4BFF20E。改段只新增Core.Configuration using及新的条件提交方法，旧Apply/ApplyPersisted与既有改动保留。范围无扩大、不删除旧通路。
- 同_gate内fork当前→persist返回候选/真实result→仅Ok且首次非Replay且Receipt Changed+Committed才Exchange/published，其余全不发布。published回传真实步骤结果；意外throw不冒称NotCommitted，保留提交事实并报告Failed，具体步骤由Host应用adapter识别及账本持久化。本批只是seam存在，待真实HTTP/Storage接入及合成验收，不称宿主已采用。
- 当前Core尚在01实施，00本批不build/test；后续与01串行协调。回退只逆向此新增方法和using，不复盖整个文件，不逆向其它人的既有改动。

- 共享批1a实施完成：SettingsBox新增硬编码ApplyConfiguration，输出SHA256=1267FD05D1E9B0BBD64D42CDAB05DE38898EAF1371CEB9EC78223F555B2A9A14，scoped git diff --check退出0；旧路径保留。尚无build/test/真实采用证据。published异常返回Failed并保留Committed；此内存兜底不声称ledger已写，Find仍需真账本。
- 补充已定位旧writer：Storage/PluginStore.cs:SetPluginEnabled→AppDatabase.SetMeta(plugins_state)，per-instance锁、IgnoreCase/Trim；src检索仅定义和interface，无真实消费者。该文件不在01/00已认领源码路径，不擅自改，需通过统一DB参与机制或新增逐文件批准解决。不存在的PluginConfigStore误查已纠正到真实PluginStore；未据误查造文件。新test文件当时尚未存在，不能提前注册猜测方法。

### 22.6 共享批1b：新Core surface合成测试公共注册（实施前）

- 2026-10-12T10:24:49.8858153+08:00 01报告真实Core构建10:22:24～10:22:26+08退出0、零警告/错误（本条为01交回证据，不是00另执行）。现存新test精确SurfaceAsync(Action<bool,string>)入口已读；00唯一路径tests/BotAgent.ReviewRemediationProbe/Program.cs输入SHA256=D43AF626ECD80366133950362DF9D3B867CB0A29CB6D53F02128516CAA5E2191。仅Main的现有RunAsync队列增加configuration Core surface注册，不新增child猜测、不删旧断言/入口。
- 本批注册后由01串行build/run；version wire三负例当前预期红待01converter实现，不跳过或改断言换绿。回退只去掉本批新增一行，保留旧共享改动。

### 22.7 Core/条件发布合成结果与模块schema决定

- 01交回本批真实执行证据（00未另并行构建）：Core 10:22:24～10:22:26+08 build退出0、0警告/错误。Review首次10:25:26～10:25:34 build0（8既有CS0108）；10:25:34～10:25:37 run退出1、主228/1，新增Version wire负例红、旧192通过；保留该红，不删断言。
- 01补非负Int64 string converter及privacy/SettingsBox条件发布/并发测试后，Review 10:26:57～10:27:03 build0（8既有CS0108）；10:27:03～10:27:06 run0、主241/0（新增49；children另列future4/restore3/clock10/12）。Architecture 10:27:21～10:27:22 build0、0warn；10:27:22～10:27:24 run0、312 files/54439 lines、159/0，Baseline/原断言未改。仅这些当前覆盖，不写全Host IT或Wave1验收。
- 00评审接受01新配置模块独立schema revision=1：物理user_version保持9，新增配置version/generations/ledger/device/tombstone闭集表，唯一AppDatabase同writer/同TX建表与module revision。模块revision>1在任何配置写、受控激活或桥入口前拒绝；初始化故障无半提交，重入幂等。原future physical10拒绝/恢复9断言保留，不通过改旧断言换绿。新增future-module2/初始化故障/重启测试由01在已认领新test实现。
- 独立module revision不宣称旧binary支持新撤销：回退仍需先关闭桥入口及任务派发，恢复整套旧产物与合成前镜像，不能混DLL或旧binary读新库。Storage新实现/HTTP真实接入、Token/UI/legacy各writer、Device全链/应用ledger、完整兼容与全矩阵回退仍待实施验收；Wave1施工中，下游锁定。
- 共享批1b实际注册完成：Program输出SHA256=7F95CCF3BA63ABA78D36FF59EAD0A5D60394BC8ACBAB2FE1F44DB17C9453109E、scoped diff --check0；仅新增真实SurfaceAsync入口，未删旧测试。

### 22.8 共享批1c：现有同连接原语抽取（实施前）

- 00唯一修改两路径：src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs输入SHA256=B113A3372A61F1339EB73A6BCBA6A85A3AEDE3CA71D4A43EF3145435FD23EDB9；src/BotAgent.Headless/Adapters/Persistence/ModelProviderStore.cs输入SHA256=8C565C1E96A872190A983F4DFBDDFCF289B20676C46E4082F2B956D91D1C17D8。
- 抽取internal SaveInTransaction(SqliteConnection,AppSettings,AuditEvent?,IAuditChain?)与UpsertMetadataInTransaction(SqliteConnection,ModelProviderDefinition)，旧Save/UpsertMetadata以唯一AppDatabase.Write调用这些原语。保持serialization/校验/Trim/circuit不变，供后续已授权Host participant复用；不另new库、不嵌套Write。AuditLogStore已有AppendInTransaction不改。
- 本批不修改EnsurePrimary、启动overlay或旧HTTP，也不冒称全部legacy版本参与已接入；这些待真实store接口。回退逆向本批原语抽取；已有差异保留，build/run继续由01串行统一执行，00不并发产物。

- 共享批1c实际抽取完成：SettingsStore输出F7754D6A4196D7C1DAC1FC253E52EA1D115905E6F8124E97F3001F0F648A085E；ModelProviderStore输出E1BF0401A6F4F5E0E98578D38CD5E58A1D110AF51B78EEF1512BD2B3B3607B4C；scoped diff --check0。旧wrapper暂保留、Audit文件未变；00未build/test，待01串行验证。
- 01/00已评审Storage internal窄参与者方案：唯一受控Activate(IClock,participant)绑定进程唯一store，同clock/participant重入幂等，不同绑定拒；新generation/HMAC/floor/module schema同TX。SecretSources用具名闭集SecretMutation(Slot,Kind)仅投影metadata，不给Host秘密正文；Prepare依owned规范意图纯校验，Persist用上述同conn原语，Changed依最终规范事实且Device映射同TX，fingerprint保原null-key意图。精确FQN/方法以01随后实际代码为准，00不猜调用或造public接口。

- 批1c审查后小修保持旧provider校验时序：wrapper在开writer前ValidateMetadata，same-conn原语也验证；最终ModelProviderStore SHA256=DA50EB8D02E4F194F32B270D6861CE0C2C12068E7819199DB54341C19D3ABDC6，之前E1BF为本批中间指纹，不当最终。原SQL/Trim/circuit不变，diff --check0。

### 22.9 共享批1d：140 scalar纯映射（实施前）

- 01确认00独占SettingsStore，允许本批helper；唯一路径src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs，输入SHA256=F7754D6A4196D7C1DAC1FC253E52EA1D115905E6F8124E97F3001F0F648A085E。新增ApplyScalarConfigurationFields(persistedBaseline,ConfigurationPatch)及typed scalar编辑helper，正常每字段一行；来源为已存在142精确Changes成员及AppSettings/PlatformOptions真实setter逐项核对，140 scalar（含Model/BaseUrl两特殊）与Device/Policy两复合分别记账。
- EnableAgentMask实际setter是AgentMaskSensitive（字段矩阵62及旧Settings561），不创造同名AppSettings接口。Model/BaseUrl Clear移除用户override并使用既有AppSettings缺省，不把environment正文写入用户JSON；首次Set两旧属性归同意图。bool/int/double Clear守现有拒绝，ordinary string Clear显式空。
- 本批仅pure helper、保19旧carry-forward，不声称Prepare/Read/Device/Policy/Registered、真实Host采用或Wave1完成；后续participant从同conn用户JSON调用并完成复合/alias/秘密/最终跨对象校验。无独立Save/IO/DB协调器。回退仅逆向新增helper，不撤旧原语；构建继续由01串行统一执行。
- 本批静态property核对先误把EnableAgentMask当旧setter，查真实矩阵/源码后纠正到AgentMaskSensitive；该名不存在不作为新接口需求。scalar完整140核对成功，预检查未写入。

### 22.10 共享批1e：真实Storage合成测试注册（实施前）

- 唯一路径tests/BotAgent.ReviewRemediationProbe/Program.cs输入SHA256=7F95CCF3BA63ABA78D36FF59EAD0A5D60394BC8ACBAB2FE1F44DB17C9453109E；01新test的StorageAsync(Action<bool,string>)实际入口已存在，00仅增加configuration transaction module RunAsync行，其余Core/旧入口断言保留。测试以真实Store/DB和synthetic participant验证同TX/CAS/NoChanges/replay/Find/secret/fault/clock/并发，不声称production Host采用。
- 本批首次登记pwsh括号语法错误退出1未执行，但编排未检查shell exit即加一行，已立即仅逆向本批一行并复核回到原输入hash；现补登记后再合入。不隐藏该流程错误，后续编排先检查exec返回exit_code再执行写入。未动其它既有改动，未构建或测试。
- 仅01串行build/run，00不并发；回退只新增注册一行。RecordApply/LegacyPlan当前还未ready，不猜调用。
- 批1d scalarhelper实际输出SHA256=6A26BD9F4BD87566E4E5F45A4527CAF52F6977CBE08AE73FAB19F1A4E5B0913A、289行、scoped diff --check0；尚无独立build/test/Host真实采用证据。

### 22.11 本批Storage行为通过、架构门禁失败（下游冻结）

- 01交回当前命令证据：Review 10:48:06～10:48:30+08 build0（8既有CS0108）；10:48:30～10:48:34 run0、主268/0（新增76，children原另列）。这是synthetic participant/真实Store行为，不是实际production Host采用。
- 随后Architecture 10:48:47～10:48:50退出1，315 files/56046 lines、157/2。R2精确SQL位置未允许新的owned ConfigurationStore.cs共21命中、ConfigurationStorageBridge.cs1命中；全域SQL总数166>137。新增本批实现导致，不归旧基线；不能用行为绿掩盖架构红。
- 01/00共同停止受影响新接入/Read helper批1f与下游准出；不得放宽137/删断言、拆SQL碎片绕扫描、盲删其它模块SQL凑数。需要为新Storage真实责任位置精确批准最小允许范围及负例（守Core/Panel SQL禁止），护栏源码当前不在本次共享队列，先提报负责人逐路径授权。总数由实际重复SQL复用/单一实现消重解决；其它既有Storage路径改动仍需精确追加授权。
- RecordApply已存在internal(caller,key,ImmutableArray<ApplyStepReport>)及closed安全错误码，但尚无真实外部steps消费者。本条不将方法存在称已应用恢复。既有失败和新增未覆盖继续保留，下游冻结。

### 22.12 共享批1g：负责人精确批准的SQL职责护栏（实施前）

- 负责人已实际批准护栏两文件精确登记及负例，棘轮保持。00新增独占两准确路径：tests/BotAgent.ArchitectureProbe/Baseline.cs输入E6944A4194645B79E5FFDF62ABC410002F7272CB758FC2512916CD713D9AEA73；tests/BotAgent.ArchitectureProbe/Program.cs输入41495488C2F09D9FFC90752D2272FD6DF23995D3EE134409639CAED5B6E6B288。
- Baseline只增加独立exact配置SQL职责登记两项src/BotAgent.Storage/ConfigurationStore.cs与ConfigurationStorageBridge.cs及AllowsSql精确membership，不假造旧迁移映射、不给全Storage前缀/Services/Core/Panel放行、不增加IO/引用/时钟例外。SqlLiteralTotal137及其它棘轮原值、原所有断言保持。
- Program在现有scanner自检增加两真实职责允许与Core/Panel/其它Storage/Headless Services/Model同名SQL拒绝，负例执行真实RatchetChecks并捕获R2拒绝，不仅字符串查表。已授权两文件之外零护栏写权，不改Metrics/SourceIndex扫描口径。
- SQL总数失败继续如实红；01只在已认领AppDatabase/Secrets/Store/Bridge复用真实重复原语，其它八旧Store路径尚未获本次授权，不越界。1f Read及下游仍冻结，待Architecture所有真实门禁恢复。00本批不build/run，交01串行执行；回退仅逆向本批exact登记与新增测试，不撤既有护栏。

- 共享批1g实际实施：Baseline输出SHA256=6744D1B10A2E401C3C2DEDF5CBC8682B83901FD1457E95E4FE613485216CBAFB；Program最终输出5B8C0484327ED637ED68E548113652626D6EF4B5968A69A9D2B29BA8EB9C89DA。六新增断言（准确allow两文件合一及五个真实R2负例）由默认scanner自检和--self-test共同执行；fixture的Tightenable副作用单独隔离，不污染真实阈值建议。scoped diff --check0，SqlLiteralTotal仍137；00未build/run，等待01串行实际结果，不能把许可位置登记等于SQL总数恢复。

### 22.13 共享批1g实际自检失败与fixture修正（实施前）

- 01实际10:55:16～10:55:23+08 Architecture build0/0warning，run退出1：仅执行准确两路径allow后因Sequence contains no elements终止，未到SQL总数。诊断重复同终止，不能冒称已测total或SQL护栏全绿。
- 归00新负例fixture：单行class中的expression-bodied Value未被真实Metrics.LongestMemberBlock认作有块方法，RatchetChecks最长方法查询.Where(nonnull).First为空。保持原Ratchet与新增五拒绝目标，改fixture为正常多行class及braced Value方法，不删断言、不改First为空逻辑掩盖真实源缺失。
- 唯一路径tests/BotAgent.ArchitectureProbe/Program.cs本补丁输入5B8C0484327ED637ED68E548113652626D6EF4B5968A69A9D2B29BA8EB9C89DA；只改本批合成source字符串，回退仅该字符串。01合法SQL去重继续由其认领范围实现，八额外Store尚未答复，00不改。1f/下游继续冻结，修后由01串行实际重跑。

### 22.14 负责人实际批准八Storage路径的限定SQL复用（实施前）

- 负责人实际答复：**授权这八文件的限定复用与合成回归**。00将以下准确路径追加给/root Agent-01，原16文件+8准确路径共24文件；00仍最终写共享队列与已批准两护栏路径，未移交互写。每项仅既有重复SQL投影/同conn原语复用，保原mapper/排序/filter/存储及隐私语义；没有目录级无限写权或新路径授权。

| 写入所有者 | 精确路径 | 当前输入SHA256 | 限定改段 |
| --- | --- | --- | --- |
| /root Agent-01 | src/BotAgent.Storage/JargonStore.cs | A0754E717C1A4F60F466442E78F38CBEB340C67A4E584C747A4DAEFE58819D04 | 限定已有完整SQL投影复用及同连接原语，不改存储/隐私/扫描语义 |
| /root Agent-01 | src/BotAgent.Storage/PromptTemplateStore.cs | 747C3FB0388729FB6695A03C9D9368975552310B3623BEAA8F5462F8706A6FB7 | 限定已有完整SQL投影复用及同连接原语，不改存储/隐私/扫描语义 |
| /root Agent-01 | src/BotAgent.Storage/EpisodeStore.cs | 1A99D7059CC310E791B419D90057D0DCDF71C3C1B166587AD4EA29367BC7A42C | 限定已有完整SQL投影复用及同连接原语，不改存储/隐私/扫描语义 |
| /root Agent-01 | src/BotAgent.Storage/ConversationStore.cs | D562B11B8E6A3AD6222F7A8633931AA5DFBCDFF6695B1AF79F1C4A7582AA21C7 | 限定已有完整SQL投影复用及同连接原语，不改存储/隐私/扫描语义 |
| /root Agent-01 | src/BotAgent.Storage/MemberProfileStore.cs | 07C9A18B2B360B95A050575205ACB0C5E0253B6AC75108AF192D681F73009758 | 限定已有完整SQL投影复用及同连接原语，不改存储/隐私/扫描语义 |
| /root Agent-01 | src/BotAgent.Storage/MoodStore.cs | 6918F5C076501E9A49692EB1BF3BD83C502AE47406B67DB967E08236A7719B24 | 限定已有完整SQL投影复用及同连接原语，不改存储/隐私/扫描语义 |
| /root Agent-01 | src/BotAgent.Storage/LegacyJsonImporter.cs | F940A244FA03EBF6586A0B3E01435366AE70FE59ED29B587A1963E910F34FCF3 | 限定已有完整SQL投影复用及同连接原语，不改存储/隐私/扫描语义 |
| /root Agent-01 | src/BotAgent.Storage/StickerStore.cs | 25207C238BB5A19D1FC9786D30A6C761A0FFE0F94DFB871AA114B8799D14F0CC | 限定已有完整SQL投影复用及同连接原语，不改存储/隐私/扫描语义 |

- 01先读真实调用点，仅改最少足以恢复137棘轮的实际重复查询；不为授权覆盖而全改八文件。AppDatabase已认领同conn闭集原语复用；完整SQL保持可读且scanner仍能计数，不拼断关键字/盲删IO/改业务保绿。其余未改文件保持原指纹。相关合成回归仍由01串行执行；旧Headless不会因此获得新增互写权。回退只逆向本批实际拥有的改段，不whole-file覆盖既有源码改动。
- 当前01诊断--print SQL合计146（166→146），仅诊断不是验收。11:00:10～11:00:18+08 Review build0（8既有CS0108），11:00:18～11:00:21 run0、主283/0，新增ownedsecret/array冻结及closed编辑Unicode等，旧future10/recovery9/migration仍通过（children另列）。原生Python初次默认gbk读源码UnicodeDecodeError1且未写；纠正显式utf8后成功，未丢其它apply_patch改段。架构仍未恢复至137，下游冻结，不能用print0或定向Review绿解除。

### 22.15 共享批1h：限定复用专项真实合成入口（实施前）

- 01已实际只改八授权路径中的五个：Jargon四相同SELECT row projection归一、Prompt三snapshot projection与两Deactivate全文归一、Episode两rowprojection、Conversation两DeleteMessages全文、Member两ReadFoldedSequence全文归一；filters/order/mapper/scope原语义不变，共减少九个重复SQL定义。Mood/Importer/Sticker不因获授权全改，保持原状态。完整SELECT/FROM或DELETE/UPDATE全文在可读常量/同conn原语，不拆关键词规避扫描。
- 新test精确QueryReuseAsync(Action<bool,string>)已存在且00已读入口，覆盖五真实仓储查询/排序/状态/作用域/删除/折叠边界；00唯一路径tests/BotAgent.ReviewRemediationProbe/Program.cs本批输入027158343E0F3267AF6128EBE9323C7086BB4D14C90F671BA0485AE3C38FFED0，只追加独立configuration query reuse注册，不删旧/Core/transaction断言。
- 随后01串行先普通Architecture真实门禁，再Review；当前尚无修fixture后的真实Architecture结果，不拿print合计代验收。1f/下游继续冻结直到适用护栏真实恢复。00不并发build/run；回退仅新增注册行。

### 22.16 当前架构门禁真实恢复、恢复批1f但不准出

- 01实际11:05:41～11:05:45+08 Architecture build0/0warning；11:05:45～11:05:47 default run0，315 files/56076 lines、165/0（原159+本批六真实自检），SQL total实测137。数值棘轮保持，五跨层exact SQL拒绝负例实际通过；此前Sequence故障未被隐藏/删断言。
- 解除本次新增架构失败，允许恢复00安全Read purehelper/participant typedmapping；不是Wave1准出或下游解锁。Store真实LegacyPlan/strictcodec/devicegate/完整多对象最终校验及Host接入/重启回退仍待实施验收；CurrentRecordApply存在不等于外部steps已应用。
- 00恢复批1f唯一路径src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs，当前input=6A26BD9F4BD87566E4E5F45A4527CAF52F6977CBE08AE73FAB19F1A4E5B0913A。只增exact Storage private bridge alias与typedRead purehelper：同conn ReadFieldState、ProjectField，142七分区scalar+已安全Device/Policy/Registered子投影；不新公共接口/任意JSON/object map，不读取或保存Environment正文。
- Read输入是persisted user facts与真实effective facts/provenance，旧未tracked字段LegacyCompatibility/LegacyNeedsReview；Overview Hidden不持raw，Display string复用AgentMask.Shorten，允许编辑值按UserMode选择用户/实际effective，nameRaw由安全子投影负责。FieldEffect须可信Host实际确认，未知保守Restart。这里只是helper源码、尚无HTTP/Host真实调用，不能冒称管理读取验收。
- 回退仅本批新增helper/alias，不撤前批scalar/同conn原语；01仍串行build/run。当前SQL137空间不通过新增Host重复SQL再破坏，pureRead只复用Bridge已有SQL。

- 批1f实际完成purehelper源码：SettingsStore最终SHA256=8DB5C9D23CCE72493FE51FD657FD96877817DCD7EE3CFBE5682D1A78DBA75D86、533行；七分区独立private方法与六字段context，避免整142映射单大方法破198棘轮。Overview Hidden raw为default、display string过AgentMask.Shorten；effectiveSource必须可信真实投影提供，未tracked UserConfiguration纠正到LegacyCompatibility/Default。scoped diff --check0，00未build/run；还需participant/HTTP实际调用及合成原生验收。

### 22.17 共享批1i：配置安全审计同连接原语（实施前）

- 01已确认固定FieldID/版本安全AppendAudit方案；00唯一路径src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs，本批输入8DB5C9D23CCE72493FE51FD657FD96877817DCD7EE3CFBE5682D1A78DBA75D86。新增internal AppendConfigurationAuditInTransaction(conn,ConfigurationReceipt,ImmutableArray<ConfigurationFieldId>)，复用真实AuditLogStore.AppendInTransaction同conn，不新增SQL/另一writer。
- 输出仅固定ASCII事件/actor/resource、允许数值FieldId、Observed/CommittedVersion、Decision/CommitState/ApplyState及已冻结revision；不含rawcaller/operation key/name/URL/秘密/body字段名，不预写applied。保原旧Audit与保存入口；此原语尚待真实participant.AppendAudit调用与故障注入验证，不称真实链已采用。
- 回退仅本批新增方法；01继续串行build/run，00不并发；不修改AuditLogStore已有改动。

### 22.18 中断恢复与当前证据边界（2026-10-12）

- 2026-10-12T15:29:03.2865075+08:00 00恢复只读核验：HEAD仍1f3181e21e8d1f9531e41066b033d8d60218d580；冻结契约仍7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203；SettingsStore最终F35C4D7406D8996C910A2EB24A0BD93604E5F3433331745B3279F5B63DDA77A6匹配。该值为批1i最终指纹，批1f中间值不替代当前源。
- 01/root的24准确路径写权仍21.2+22.3+22.14；24源码均存在，不整理既有改动/20暂存删除。00原十四共享队列和22.12两准确guard写权有效；guard当前Baseline=6744D1B10A2E401C3C2DEDF5CBC8682B83901FD1457E95E4FE613485216CBAFB、Program=A8A6F1A537EEA7393DBB16BCECA639AB637AF3F96556CF1082896848D3A47662。展开git status370项是当前带新路径口径，不直接与旧目录折叠数比较。
- 01确认旧Review handle34793已missing、无该旧运行进程，最终run结果未取回，**不当通过**；本次00观察当前dotnet四进程，仅视为01新串行验证活动，不当旧handle复活或新成绩。等待01交回当前Review/Architecture终态；00不并行build/run，也不在当前源码验证窗口编辑共享源码。
- 已重新读真实Key scalar converter、Bridge ReadFieldState/ProjectField/Activate及participant成员、Store Read/Save/Find/internalRecordApply。Core keycodec更新不代表Host strictparser完成；Store暂未有实际LegacyPlan/devicegate。现有140 scalar/142安全Read/同connaudit helper与条件发布只有源码及部分合成证据，尚无runtime Hostparticipant采用，不授下游/阶段二准入。
- 本总纲本次输入70FD226E57B7E5E91F6F901C65DE409A4843B4D71435943048AC7C3CF2FBBC96，仅增恢复记录，不覆盖旧失败记录或他人改动；source补丁窗口下一批必须先与01新运行结束确认。下一确切共享批拟在原SettingsStore内完成participant Prepare/Read/AppendAudit并用真实方法接CompositionRoot/SettingsHotReload/旧HTTP；完整legacy writer、Token/UI及Device每次dispatch仍后续登记，不造未知桥成员/HTTP路径。需额外文件/接口先提报，不扩大队列。
- 第一次恢复批只读结尾rg嵌套AGENTS为无匹配退出1（预期no AGENTS，不是产品失败）；没有借此写source。随后显式hash/24路径/真实桥与key读取pwsh0。全部是源/安全元数据，没有真实群聊/数据库正文/日志/协议端或生产操作。

### 22.19 恢复后真实串行验证、部分通过不冒称准出

- 01/root实际Review build 2026-10-12T15:26:44.8708709～15:26:51.8693414+08退出0；run至15:26:54.4549123退出0、主298/0，children另列4/0、3/0、10/0、12/0。源哈希before/after无变；覆盖当前Core surface/事务治理/五Store查询复用和旧基线。旧34793缺终态不计通过，未将源编辑窗口结果混入新receipt。
- Architecture实际build+default run 15:28:14.8651912～15:28:17.1356446+08两退出0、165/0，SQL137、315源文件/56427行、Domain max268。数值棘轮/原断言保持；绑定SettingsStore F35C4D7406D8996C910A2EB24A0BD93604E5F3433331745B3279F5B63DDA77A6及当前两个guard。这不是全Host IT、Wave1验收或阶段二解锁。
- 00可继续下一登记共享source批，但任何新改动使受影响证据需重新绑定；Root24和00共享队列继续独占。新增Configuration module future2/初始化失败重试由01新test的child验证，公共Program由00先查询真实模式后按准确method登记分派，不猜新文件或改旧future10/restore9断言。
- Strict/Legacy最后model key Delete与primaryEnabled矛盾由01依据冻结证据核对，未确认前不隐改只读Enabled、回落缓存凭据或宣称真实Host cross-domain已验收。

### 22.20 共享批1j：模块schema恢复实际child注册（实施前）

- 01实际新test已定义ConfigurationContractTests.TryRunChildAsync(args)识别两个closed modes --configuration-future-module及--configuration-module-atomic-fault；ModuleRecoveryAsync(Action<bool,string>)主入口启动两child，预期4/0与6/0，未执行不作为成绩。
- 00准确唯一路径tests/BotAgent.ReviewRemediationProbe/Program.cs输入14E8E10F7BE8C762216846CDFE202FB63FFE470F5E11F5A64B6F179DB86ACE94；Main在已有Clock/Database/Storage child分派旁增加这一实际factory，在已有配置surface/transaction/query队列增加ModuleRecoveryAsync独立注册。不删旧入口/断言，不扩工程或新路径；Root本批只有新test修改、无module源码修改。
- 覆盖未来configuration_schema2拒绝且不改、module初始化注入失败全部新表rollback/移除trigger后能重试；旧physical future10/recovery9保持。仅01串行build/run，00不并发；回退只这两新增行。
- 删key边界按当前唯一冻结495+700核对：strict enabled需key provider无配置最终拒绝；旧Legacy按历史Delete和HotSave fallback保留，不能隐式改primary.Enabled只读项或复活cachedkey。01继续核已签03/06/完整字段矩阵是否另有矛盾，未做未经批准裁决。当前以上都不是Host实际采用证明。

### 22.21 Root RecordApply终态保留修正与Strict/Legacy查核

- 01核冻结495/700/718与完整矩阵330：Strict enabled需key provider最终不可Unconfigured；旧Legacy保历史Delete/fallback/无CAS。03/06手册无相反已签override。本次没有改冻结语义、隐改primary.Enabled只读项；真实Hostparticipant各profile最终校验与兼容负例仍必需，synthetic Delete绿不替代该验收。
- 01发现现自有src/BotAgent.Storage/ConfigurationStore.cs RecordApply任何一步Failed/RestartRequired就terminal=1，即使其它步骤NotStarted，违反冻结731/749过期Pending/Unknown保留与未终态不驱逐。输入SHA256=4380E07DC4481F6C15D8DB38D8F31B5E4141F484BB3664E393428B65E2F14398；在原24准确写权内只修terminal判断为八步骤均具Applied/Failed/RestartRequired真实终态才terminal1，聚合ApplyState如实保持，不增SQL/接口/新路径。
- 01在已认领ConfigurationContractTests补ledger故障/partial/Unknown/expiry保留真实同DB合成测试，公共入口若现已注册同方法无需新注册；新增method则00另记准确签名。不删除原断言换绿。回退仅本批判断片段，不能回退其它Root/00旧改动；所有构建与验证仍01串行。该缺口归新增实现自身，未假定生产已触发。
- 批1j公共Program实际输出SHA256=7878935331645BDDE3B2B5D4635E0464F62944CBA00F0C10EE0A32384476A2B9、scoped diff --check0；新module children还待当前串行结果，不预写通过。

### 22.22 ledger真实红绿与当前restore验证窗口

- 01实际新红：15:36:45.9057～15:36:59.439+08 Review build0，run至15:37:03.625退出1、主284/1；筛选重跑15:37:30.012～15:37:31.526退出1，精确incomplete failure remains nonterminal，新module children4/0+6/0。原断言和该新红均保留，不凭编译成功跳过。
- 只修22.21 Store terminal条件后，15:37:49.576～15:37:55.718 build0（8既有CS0108），run至15:37:58.908退出0、主310/0；新module4/0+6/0，旧future4/0、restore3/0另列。覆盖ledger注入故障、Unknown/partial/完整Failed/Replay不重做及过期未完成Restart保留；不虚称外部actions完成或Host采用。
- Architecture15:38:26.219～15:38:31.606 build/default run两0、165/0，全部数值棘轮不变，scoped diffcheck0。Store输出C252C3C7EE134DF0EE9A06855D6FBD6CAE80FE44AD355B00E629E837B010695B；test7401AE9FAA05B4606E8521A4117B6B4896EED487EA482801E27439DA7C20A6AF（后续restore child改动会使这份test指纹过期），Program仍7878935…。
- 01在已owned test扩现factory支持closed --configuration-restart及8assert，从真实VacuumIntoBackup复制至现UUID合成根子目录，独立进程Activate新generation恢复version/secretmode，旧receipt可Find/replay但缺旧key拒绝，persist/application不重做。预期不是成绩，未初始化真实路径或生产操作；现有公共factory无需新增Program写入。
- 00当前只读及总纲写，逐hash复核SettingsStore F35C4D7406D8996C910A2EB24A0BD93604E5F3433331745B3279F5B63DDA77A6、Program7878935331645BDDE3B2B5D4635E0464F62944CBA00F0C10EE0A32384476A2B9，确认01短串行build/run窗口。窗口终态前00不编辑共享源码、不并发构建；下一Hostparticipant/Legacy privateprofile仍先讨论并登记准确改段。

### 22.23 Root private Strict/Legacy保存profile（实施前）

- 00窄评审准入：仅原24准确路径中的ConfigurationStore/ConfigurationStorageBridge/新ConfigurationContractTests，Root仍唯一写入者；不改公开C3/FQN/新路径/其它波次。当前输入：
- src/BotAgent.Storage/ConfigurationStore.cs 输入SHA256=C252C3C7EE134DF0EE9A06855D6FBD6CAE80FE44AD355B00E629E837B010695B
- src/BotAgent.Storage/ConfigurationStorageBridge.cs 输入SHA256=38A8115AB6DD9AA7B7DFBB90D11E17CB8807AA18BCBDB5D866666564865B002D
- tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs 输入SHA256=4B633805FF900F2AD38D3BE493BB7187BCA7134765B1CE94480EB969CF11C42F

- Bridge增加internal ConfigurationStorageProfile {Strict,Legacy}；实际participant.Prepare最后参数profile。public SaveAsync恒定Strict，抽私有SaveCore而保持Strict原fingerprint字节；internal Store SaveLegacyAsync(caller,key,typed ConfigurationPatch,typed ImmutableArray<SecretEdit>,ct)固定Legacy，Bridge同签名窄转发，无public profile/body grant/keyfactory。
- Legacy同一Write内读取ObservedVersion并跳过外CAS；fingerprint的原expectedNumber位置换两个固定Legacy/NoExpectedVersion marker，其它caller/revision/key/ownedpatch/slots绑定保留，同key跨profile Conflict。Host旧pureparser在writer前只产稀疏missing/null/Clamp/clear意图，不读锁外version或合并runtime snapshot；Prepare同conn最新用户facts合并，19内部/未知旧carry-forward只源自DB。
- 新test更新实际synthetic override并测无外CAS/同TX原子/replay/跨profile冲突/structuredReject/并行Legacy，原Strict所有断言保留。Legacy的Device/Policy兼容不是假定完全规范可复用，未知兼容仍LegacyNeedsReview；Token/mood/Device真实行为和生产Host链仍须后续验收。
- 00新的真实SettingsStore participant将在此已登记private profile成员落地后消费；Root不写共享SettingsStore、00不写Root三输入，串行构建先同步两个source窗口完整，不拿中间编译或fake participant成绩作宿主验收。回退仅profile本批改段，保留已有源码/断言/棘轮。
- Restore短窗口15:44:40.451～15:44:43.407+08 Review增量build0/0新警告（不称8既有警告已修），run至15:44:45.962退出0、主312/0，CFG restart8/0、future4/0、atomic6/0、C2 future4/0 restore3/0。before/after四输入hash无变；证明新代version/mode/tokenDeleted/历史Failed保留，oldreceipt等参重放/异参冲突、旧代缺record拒绝、synthetic写计数0，不冒称旧产物回退或生产Host采用。
- Root15:49:24.175～15:49:26.450增量build0，run至15:49:29.060主322/0及既有children，新增300/301s/Tick倒退/预取消/version溢出/subject256容量、Replay/异参优先与其它主体准入。Root未先确认新短窗口流程疏漏已报告；00确认同期无共享source编辑，仍由Root actual before/after hash核绑定，不因未询而自动虚报通过。当前两共享source依旧F35C4D.../7878935...。

### 22.24 共享批2a：实际Host scalar participant与安全Read（实施前）

- 01确认00立即实现已评审7参数internal ConfigurationParticipant，只有以下两既有共享队列路径；不加Headless friend、新路径或公开C3接口。当前输入：
- src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs inputSHA256=F35C4D7406D8996C910A2EB24A0BD93604E5F3433331745B3279F5B63DDA77A6
- src/BotAgent.Headless/Adapters/Persistence/ModelProviderStore.cs inputSHA256=DA50EB8D02E4F194F32B270D6861CE0C2C12068E7819199DB54341C19D3ABDC6

- SettingsStore新增嵌套真实Storage.ConfigurationStorageParticipant实现，ctor接唯一SettingsBox、可信authorization、6slot metadata、安全RegisteredRead、effectiveSource/effect及primaryRequiresCredential；override按已登记Root private profile区分Strict最终key拒绝/Legacy历史规则。同conn用户facts与opaque旧属性保留，140 scalar闭集映射、实际provider primary.IsEnabled判断（不能单靠bool虚构启用元数据）、已批准同connAudit；Read142安全投影。
- 固定Model/BaseUrl非秘密fallback只在trusted构造时按当前BotConfig三个固定别名优先级捕获，不在Read动态查环境、不读任意env名、不回写用户JSON。primary元数据在同TX由既有完整SQL原语更新，旧公开LoadAll在同conn原语委托，SQL定义仅一处，不增加137计数。
- Device/Policy/Registered复杂修改暂固定安全错误、不silent-save；非空旧复合未迁移Read返回LegacyNeedsReview并保真实header/version/generation，不虚构空数组或raw JSON。该缺口是临时待完成，不将此batch称完整C3、生产Host采用、Wave1准出或下游解锁；旧HTTP仍保持直到完整Legacy通路验收。
- 真实private ctor测试由01现owned新test反射调用并独立child Activate实际adapter，非fake participant；Owner真实DI在后续CompositionRoot接入，不因程序集通过就冒称host采纳。Root私有profile与此batchsource窗口均完成通知后仅01串行build/run；00不并发构建。回退只新增adapter/同conn抽取改段，保前批及所有旧改动。

### 22.25 共享批2a实际源码完成（待串行编译与真实adapter测试）

- 00只在22.24两共享路径完成：SettingsStore嵌套实际ConfigurationParticipant（538行，7参数internal ctor552行），Prepare消费Root实际privateprofile；ModelProviderStore.LoadAllInTransaction125行复用既有完整SELECT。源码不是生产CompositionRoot采用证据，00未运行build/test。
- 同conn读最新DB用户JSON并保原未知属性，140scalar mapping、Strict真实primary.IsEnabled与可信proposedSources判断credential_required；缺primary/未分类binding固定拒审，Legacy不隐改Enabled、不重映射未知binding。primary模型/URL/source metadata同conn原语持久，固定非秘密Model/BaseURL环境别名fallback在trusted ctor捕获，空值/trim同BotConfig.Str规则，不进入用户JSON。
- 安全Read使用实际142 typed view.Rule显式闭集，无反射投影/任意JSON正文输出；授权回调、安全RegisteredRead与source/effect均trusted DI，不从请求自报。非空旧Device/Policy复合临时LegacyNeedsReview保真实header/version/generation，complexedit固定错误并上限32，非silent commit。本批未实现全部provider/plugin/policy/device、Token启动迁移、Legacy HTTP纯parser与生产DI，Wave1/下一阶段保持未准出。
- 输出SettingsStore SHA256=0C5C49BDA2D33FE456F1285E8F5B5AFE4E490DB9DCF41EC7124804DB627D1203；ModelProviderStore SHA256=4E36E7B8DD3E698395A3490861BB68F7E9227FA14358B66B94C7FD225E081351；scoped git diff --check退出0，SettingsStore当前879行低于既有1765门槛（正式Architecture仍待01串行）。00已通知Rootsource窗口结束，后续编译/真实private ctor合成adapter验证只由01执行。
- Root22.23私有profile输出reported：Store14C9F88ACA94A1247937D6C41DDF59E141F80918F4CA19F2BF28A2D4B65C5903，Bridge846F858086C8E80CC2A6019435904AC0613210F7D3C70FDFE5EE5CD61B0771AB，test0073A4089B972B6A4D4AE932B952384A3B2F358AB6F46757198E252EEBC989F2，Root scoped diffcheck0；golden Strict HMAC/Legacy最新观察与跨profile原子并发测试源码存在，尚未build，不能写成通过。
- 本轮先读不存在Core根路径导致Get-Content退出1（只读，无写），已用真实目录纠正。全部后续原生命令退出码检查；保留冻结契约/棘轮/旧改动。回退仅22.24新增adapter/同conn抽取，不回滚前批或Rootprofile。

### 22.26 共享2a与Root privateprofile联合编译/回归（Host行为待验）

- 01串行真实Review build16:11:18.171～16:11:29.192+08退出0（8既有CS0108）；run至16:11:32.065退出0、主332/0，新增Legacy9assert与严格旧HMACgolden通过，CFG restart8/0、future4/0、atomic6/0，原children另列。六源码输入before/after一致（含22.25两shared输出），这只证明源码可编译、治理/合成participant，不是production Host链采用。
- Architecture16:12:48.395～16:12:50.662 build/default run两0、165/0、315files/56795lines，全部数值棘轮/SQL137保持，不删除断言、改阈值或拆SQL隐藏。此批解除编译/棘轮门禁，不解除完整管理/保存行为与Wave1准出。
- 00主动标记2a PrimaryProjection潜在空Keep假Changed：旧primary env:QQCHAT_API_KEY且实际None时会校正secretRef。01先在现owned测试真实private ctor/独立child重现，再最小注册修，不在红前改源码、不把fakeparticipant空Keep验收冒称actualHost。
- 当前00只读/总纲写，暂停两shared源码供01真实Hostadapter child验证窗口；回归红/终态后再登记精确修段，未扩写权/Token hook/旧HTTP。

### 22.27 共享批2b：真实Host空Keep绑定假Changed红与最小修（实施前）

- 01实际Host ctor child红：16:20:18.450～16:20:21.379 build0、run至16:20:22.629退出1 host0/1；固定assert label再build0，16:20:56.343～16:20:57.404 run1，精确all Keep cannot rewrite empty primary binding。真实7参数Hostparticipant及合成DB六槽，不是fake participant；空KeepLegacy必须NoChanges/version0/保原envref。21assert为计划，尚未通过。
- 00只在SettingsStore现shared写权，输入0C5C49BDA2D33FE456F1285E8F5B5AFE4E490DB9DCF41EC7124804DB627D1203：同conn读当前六槽metadata，PrimaryProjection只在proposed primary credential元数据与current真实facts不同才变已分类binding；Keep不为校正旧binding制造变更，不读secret/env正文。用户scalar和metadata与审计同TX、未知binding保留并Strict拒审、原public及profile不变。不增加SQL或路径。
- 01另加冻结494/904 credential-bearing changed endpoint在缺可信policy时安全拒绝负例，00不在该红出现前混入这批或臆造endpoint允许表。2b只修空Keep，production DI/HTTP仍未采用；后续endpoint/secrets完整授权仍门禁，stage2保持锁。回退仅此current/proposed比较片段，保旧断言与其它Source。


### 22.28 私有Prepare显式六槽mutation意图：真实第二红后最小联合改签（实施前）

- src/BotAgent.Storage/ConfigurationStore.cs inputSHA256=14C9F88ACA94A1247937D6C41DDF59E141F80918F4CA19F2BF28A2D4B65C5903
- src/BotAgent.Storage/ConfigurationStorageBridge.cs inputSHA256=846F858086C8E80CC2A6019435904AC0613210F7D3C70FDFE5EE5CD61B0771AB
- tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputSHA256=964A43400E05819E631CE03548FCC534CFAFFBB4A201A47D1A5A0DE9C9426230
- src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs inputSHA256=CAE305B7BE060C51A582EBCD430550D1E5C062C045C0993B757D77823B0F561A

- 01真实第二红：16:23:52.009～16:23:57.404 build0（8既有CS0108）/run至16:23:58.777退出1、actualHost child9/1；空Keep第一红已绿。既有DB凭据+合法旧env primaryRef，显式Replace新DB值时current/proposed Configured/Source相同却没有选择DB，精确explicit Replace selects primary DB binding even when source metadata is unchanged。Source比较无法还原Keep/Replace/Delete意图，不缓存或猜秘密动作。
- Root只改原owned Store/Bridge/test：Prepare(conn,ownedPatch,proposedMetadata,ImmutableArray<SecretMutation> proposedMutations,profile)，profile仍最后；Store同一owned6槽闭集mutation数组同时给SecretSources与Prepare，无secret正文、Core/public形状或FQN变化。更新实际synthetic和反射actualHost测试签名，旧断言保留。Ctor7param不变。
- 00只改现shared SettingsStore override与PrimaryProjection：消费同owned意图Keep保旧ref、Replace选固定DB ref、Delete按可信proposedSource选旧HotSave环境fallback/未配置槽；未知ref仍Strict拒审、Legacy保留，不反推意图、不新增SQL/路径。Root独占三输入、00独占SettingsStore，先互相通知source改段完成再串行build。
- Endpoint trusted policy拒绝仍下一红，未混入这批、未臆造allowlist。此前2b空Keep改段此批仅替换意图识别实现，保所有其它既有修改。通过编译不等于生产Host采用/Wave1准出。

### 22.29 共享批2c：无可信endpoint授权时Strict凭据携带端点变更fail-closed（实施前）

- 22.28意图修实际绿到Host child10assert；下一红16:25:58.359～16:26:04.384+08 build0（8既有CS0108），run至16:26:06.120退出1、child10/1，精确missing trusted endpoint authorization rejects credential-bearing endpoint change。Root三输出Store4919A4C99717156ECB9232E221C0EEA8891CE4A34CDFC51FCE7739E962E4CF54，Bridge58CD0B94A179700E7358A64FC435865DBB1B3C4905B8863592FBCA9CF5E79DA9，test2E8EE42DD86E6EB8C8A576F9DB6438AFC9B1AB2E79D2E295A2742C9467F87851；scoped diffcheck0，不提前将全child称通过。
- 00只改SettingsStore现shared路径，输入043C71BE1E978F5CC03A967F643172DE2CCF65704EF0CE6187A5D1642F4E3A85。Prepare Strict判断primary已登记、可信proposed ModelApiKey Configured且ModelBaseUrl Set/Clear的最终新URL不同已登记BaseUrl时固定安全endpoint_authorization_required。没有trusted endpoint/caller用途授权seam前拒绝，Legacy明确保持旧规则；不网络IO、不硬编allow域名、不把SafeUrl IP限制冒称凭据端点授权。
- 原七参数ctor/profile/publicshape/FQN不变，不增加SQL/路径。该临时failclosed不等于用户新端点已可管理，不假称全scalar/跨域C3完整。后续真正actor-specific endpointseam须先评审把caller传私有Prepare及复用实际网络policy；未签方案不造接口。保已登记alias目录校验/Strict read-only项/原断言，回退仅此守卫改段，Host生产接入仍禁。

### 22.30 00恢复与验证source窗口冻结

- 00于2026-10-12T17:10:22.9001378+08:00 恢复唯一shared最终写/总纲登记席位，未派新agent、未扩权。实际只读重新核HEAD1f3181e21e8d1f9531e41066b033d8d60218d580，Root Store4919A4C99717156ECB9232E221C0EEA8891CE4A34CDFC51FCE7739E962E4CF54、Bridge58CD0B94A179700E7358A64FC435865DBB1B3C4905B8863592FBCA9CF5E79DA9、test2E8EE42DD86E6EB8C8A576F9DB6438AFC9B1AB2E79D2E295A2742C9467F87851、shared SettingsStore8FD01EE4F5AEB034DADD6BACA93D9C5B4B6FDB11DC4C055D798B93E0EB8B3D68均匹配，冻结契约7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203未变。
- 上轮中断的是只读Get-Content/rg调用，00未启动dotnet或写source；当前只读/总纲写并保持source冻结供01串行actualHost child/完整Review/Architecture。当前Get-Process仅计数dotnet5与01开始验证窗口相符，不终止或读取数据/日志。
- 22.29 endpoint临时failclosed尚未验证，未登记为绿；完整允许端点seam、复杂对象、旧Legacyparser、生产采用/Token/Device行为仍缺，不因临时deny或编译就准出/打开第二阶段。后续仍按已有24Root+shared精确路径和每批input登记，缺新路径/契约先提报。

### 22.31 2c真实actualHost绿及Token下一批只读核实

- 01真实17:09:33.698～17:09:53.129 Review build0（8既有CS0108），actualHost child至17:09:54.090退出0、22/0；六输入before/after不变。全Review17:10:40.328～17:10:43.387退出0、主333/0（CFG restart8/0、future4/0、atomic6/0、actualHost22/0；C2 future4/0、restore3/0另列）。Architecture17:10:43.391～17:10:45.458 build/default两0、165/0、315files/56815lines、Domain max268，数值棘轮/SQL137不变。
- 此为实际private Hostparticipant合成调用链而非fake participant，仍没有production CompositionRoot/HTTP采用、完整复杂对象/授权endpointseam；临时safe拒绝不等于管理要求完成，Wave1/第二阶段保持未准出。
- Token最新真实时序：CompositionRoot.BootstrapSettings Initialize→LegacyJsonImporter.ImportIfNeeded→BotConfig.Load；BotConfig.Load先HasStored/Load→ApplyInfrastructureEnvironment51～85→ApplyBehaviorEnvironment88～99→ApplyPanelOverrides420→Normalize→hasStored Save44。Token399在SeedBehaviorFromEnvironment273～417；hasStored=true时Warn后return，不每启env覆盖。00先前分段读误认infrastructure的消息已撤回，此处以全方法重新核为准。
- 真正Token秘密显示缺口：BehaviorEnvTable194把Token列入行为环境表，WarnIgnoredBehaviorEnvVars247～266的Truncate(envValue/fileValue)会保留两秘密片段；后续BotConfig只固定环境名称/configured/source，不回显片段。Token声明来自Platforms.PlatformOptions47继承且未JsonIgnore，不在此写权，不能盲改Platforms或隐藏同名新属性。旧HTTP291明文Setter/GET991和app.js仍须替代管理同批保持有效。
- 下一private Initialize闭集seam尚未定签/登记源码批：原Store Activate唯一Write内迁Token slot/marker/移旧JSON/用户version、安全audit，default no-op；仅未初始化允许固定首次种子，deleted不复活，旧空不是显式Delete。初始化失败全rollback、未来module不变、重启幂等必须actual合成测试；未迁移入口不能开放。迁移来源真实JSON但仅合成验证，不读现库。
- 本轮另一次只读Get-Content猜错Storage AssemblyInfo直属位置退出1，无写文件或构建，后续应目录查询精确路径，不把此只读工具错记产品失败。


### 22.32 Token私有受控初始化、版本审计与marker-aware启动安全批（实施前）

- src/BotAgent.Storage/ConfigurationStore.cs inputSHA256=4919A4C99717156ECB9232E221C0EEA8891CE4A34CDFC51FCE7739E962E4CF54
- src/BotAgent.Storage/ConfigurationStorageBridge.cs inputSHA256=58CD0B94A179700E7358A64FC435865DBB1B3C4905B8863592FBCA9CF5E79DA9
- tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputSHA256=2E8EE42DD86E6EB8C8A576F9DB6438AFC9B1AB2E79D2E295A2742C9467F87851
- src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs inputSHA256=8FD01EE4F5AEB034DADD6BACA93D9C5B4B6FDB11DC4C055D798B93E0EB8B3D68
- src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs inputSHA256=49EF637419221B0FB257640CC9C2E2CF9441F99B1E222036E54A284837DB025E

- Root确认最少private hook internal virtual bool Initialize(SqliteConnection conn,long serverUnixSeconds)=>false；internal virtual void AppendInitializationAudit(conn,ConfigurationVersion before,ConfigurationVersion after){}。只Root原owned Store/Bridge/test，公开C3、FQN、接口revision不变，不新造用户receipt/operation ledger或独立writer。Activate既有Write内EnsureModule→校验floor→Initialize(floor)→Changed时checked before+1复用现完整UPDATE version常量→AppendInitializationAudit→floor/generation。异常全TX回滚，_active仅commit后赋值；tests真实fault/future模块/clock/重启slot marker和版本恢复，旧断言保留。
- 00只有现shared SettingsStore/BotConfig：嵌套实际Host初始化只Token槽5、fixed verification_token_initialized标记、旧用户JSON Token明文剔除（保其它/未知属性）、用户version与安全固定slot/version审计同TX；不发布/外IO/Read迁移，不触其它五槽/Device/动作。不读真实DB/真实Env验证，只有Root独立synthetic child。
- 已deleted不回种，initialized不再播种；legacy有用户settings行但Token空/缺省时既不按首次env播种，也不伪装显式Delete，标初始化完成无值；只有未初始化且没有旧用户settings行才固定BOTAGENT_FEISHU_VERIFICATION_TOKEN→QQCHAT_FEISHU_VERIFICATION_TOKEN首次种子。非空legacy值严格UTF8/16384字节/Trim/禁零，别名碰撞或非法形状固定安全拒审，不输出正文/片段。已有slot/marker在对应已初始化规则下保持真源。
- BotConfig重新核seed273～417/Token399，marker-ready时实际DB slot投影覆盖seed/旧plain，deleted保持空、不回种；停止把runtime-overlay普通Save回写用户JSON（只ready路径，未ready legacy当前通路保持）。WarnIgnoredBehaviorEnvVars Token只固定环境名称/configured状态，不能Truncate两端秘密。现继承Platforms属性/旧GET/UI不单边忽略/隐藏，生产Host/HTTP尚不Activate此实现，替代管理与serializer/UI切换必须后续完整同批验收。
- 五路径精确分属Root3/00两shared，不改其它源/扩路径/放宽护栏。Root及00双方完成source窗口再由Root串行编译/actual adapter child/Review/Architecture；新环境测试只设置合成值或按名移除，不输出开发环境秘密。回退只本初始化hook/Token改段，保既有2a～2c与全部旧改动，初始化前整套DB镜像回退未演练不说兼容已通过。

### 22.33 Token共享源码就绪（未编译/未生产采用）

- 00已完成22.32两shared源码并通知01source窗口结束，未自行build/run。SettingsStore输出A64C8B219F866A421B107FBEB08D5F856E2F650C4465A74068FC2EC7E45E7614、989行；BotConfig输出D9BBC84FEC4AC595CA8DF0F77A1A5B41151A28B898678F774CB85F879ADD86F6；scoped git diff --check退出0，不将此静态检查称编译通过。
- SettingsStore actual Host Initialize622行、AppendInitializationAudit664行只同conn Token5/marker/剔旧JSON；marker已初始化不seed，deleted有秘密行冲突拒绝，不回种；legacy Token空/缺省但有用户行不seed、不伪造Delete，首次无行才固定二aliasseed。旧未知用户属性保留，slot/legacy非空不一致无marker时固定安全拒审；严格UTF8/16384字节/零/type/别名碰撞安全拒绝，不返回原文。
- markerReady启动投影真实DB-only，BotConfig ready路径不Save runtime overlay，Token警告只固定env名/状态。只有私有新participant的Merge剔Token明文；没有单边改旧public Save serializer/GET/UI或未经授权Platforms继承属性。生产CompositionRoot未Activate此实现，尚不声称Token管理替代/重启行为验收通过。
- Root真实private ctor旧NoChanges/version0断言不能放松：新未初始化fixture会产生初始化version事实，旧行为专用fixture先显式种已初始化marker，新初始化测试则证明准确version增量。双方source完成后仍仅01串行编译/真实child/Review/Architecture，临时pending不是绿。

### 22.34 Token初始化基础hook真实门禁（旧Token/真实启动投影专项待验）

- 01真实17:30:12.398～17:30:23.128 Review build0（8既有CS0108），run至17:30:26.896退出0、主334/0；CFG initialization8/0、actualHost22/0、future4/0、atomic6/0、restart8/0，C2 future4/0、restore3/0另列。五源码input before/after不变，01报告Store156336E1…、Bridge5433D533…、shared SettingsStoreA64C8B…/BotConfigD9BBC…、testAFB606…，后续test新增会使该test指纹过期。
- Architecture17:31:46.272～17:31:48.163 build/default两0、165/0，SQL实测137、315files/56920lines、Domain max268；全部棘轮/旧断言不变。支持hook同TXaudit故障/overflow/clock/version trigger/retry/generation事实；此时还未证明真实旧Token迁移、首次seed/initialized/deleted对真实BotConfig.Load和warning不泄露、无runtime overlay回写。
- actualHost旧NoChanges用activation后真实Read.Version基准证明用户Save不增量，未放松NoChanges业务断言；初始化版本精确增量另由真实初始化8断言证明，不把相对基准假当迁移完成。00此前preseed-marker仅fixture建议，现按01实际相对版本方法登记。
- 01继续只在原owned新test添加真实Token旧格式/seed/marker/deleted/重启和BotConfig warning合成cases，00两shared source保持冻结，未接production Activate/旧HTTP。专项红需独立记录后最小修；hook基本绿不自动Wave1验收或开启第二阶段。

### 22.35 真实Token迁移/启动投影专项通过（生产接入仍待）

- 01实adapter专项17:36:38.444～17:36:43.076 Review build0、run至17:36:51.048主350/0，16cases（valid6×11、invalid10×3）。补真实Strict Token Delete→Vacuum备份→独立进程新代与BotConfig不同env不回种后，17:41:52.820～17:41:56.471 build0、17:41:57.827 child14/0（restart5/0）；全Review17:42:38.117～17:42:45.021退出0主351/0，17Token cases、新initialization8/0、Host22/0及既有children另列。
- 五source before/after无变，当前test60167587D95217811D19A2B83EC902C9ACBA8A34E5F13BFECE9EB4388F4A4212。证明actual Host initializer旧JSON去Plain/保opaque、marker/slot优先、仅无用户row才env首次seed、旧empty/missing不seed或伪装Delete、非法形状/audit/version/generation/overflow全rollback；真实BotConfig.Load无Token片段警告且不回写runtime overlay。真实Delete备份跨进程恢复V2/原Find/无重复audit、env不复活，不只是合成participant marker恢复。
- 此为合成根/进程的实际adapter链，不是production CompositionRoot/HTTP/UI采用，不是旧产物回退证明，不能据此解除管理替代/设备/复杂对象/完整Wave1门禁。source窗口现结束，下一WebUiServer.Settings纯LegacyPlan/6slot/action分离先窄评审并登记，再真实小批实施；Root设备stableKey/tombstone接口也须先精确评审，SQL137不得放宽。

### 22.36 共享批3a：pure Legacy六槽秘密与独立动作意图解析（实施前）

- Root窄确认00只写已认领src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs，inputSHA256=B5071F26A303C41152F8673E929A11736B9FF5A01995ABF8EB694ADA3F6253AB；不新路径/公开Core接口/生产HTTP接入。新增private static ParseLegacySecrets(JsonObject)返回private IDisposable LegacySecretPlan，固定六slot owned SensitiveInput，结果Code、独立typed Mood意图与TtsProjectionRequested。不读取Input internal正文getter，外部持有inputs仅Dispose，临时编码数组清零。
- 完整9旧秘密/clear输入：Missing/null Keep；apiKey/agentServerKey空Delete；tts/FeishuSecret/Encrypt空Keep，clear bool TryGetValue==true优先，否则保持旧容错；非空Token Replace、空/纯空白Token整批LegacyNeedsReview；官方Secret/clear任意case含null/false存在都副作用前拒绝；旧ttsKey只忽略审计输入，不成为slot别名。duplicate/未知strictRootCodec待后续HTTP解析，不冒称JsonObject已证明wire重复拒绝。
- parser不Save/env/IO/log/action，不调用TTS/Mood，不拿runtime snapshot合并用户配置；Model/Agent/Behaviour/Platform等142 puretyped组映射继续下一小批，19只DB carryforward，不能silent丢复杂对象或临时deny当目标完成。现旧HTTP入口继续原路直到所有pureplan与设备/策略/应用ledger完整验收。
- 独立Mood intent只有Keep/Set/Reset（FieldEdit<string>），TTSflag仅代表计划外部投影而非已执行/原子。实际动作幂等/可观察持久结果与安全回执后续按冻结719实施，不能由旧bool推断Applied。
- 00落真实成员后通知Root，Root只在现ownedtest反射实际private parser并用合成数据验证输入边界/六slots/清零/无side-effect。仅Root串行build/run，保全部旧断言/SQL137/冻结hash；回退只本parser新片段，未授权提交/部署/真实数据。

### 22.37 pure秘密parser真实回归与限定MemberProfile查询复用准入

- 01真实17:56:28.2210641～17:56:36.9213965+08 Review build0（8既有CS0108），run至17:56:46.7501098退出0主444/0，较351新增93 actual private parser断言；六源码before/after无变，test FC204A4BC9AD15613DEDF8D890C3FE8BDAD6691271951CCB699BD36D269AC07A、WebSettings6132B913…。Architecture17:56:53.8172663～17:56:56.3075752 build/default两0、165/0、315files/57021lines；筛选重跑退出0、SQL137/Domain268。不是HTTP/production采用或完整LegacyPlan。
- Root在原已获批8Store范围内查出真实MemberProfile重复查询，只该owned路径inputSHA256=C7C86A3B5B7CC504C26A8651E0989AD6720181E903A0438BD2E8A14A607A91BA；owned ConfigurationContractTests inputSHA256=FC204A4BC9AD15613DEDF8D890C3FE8BDAD6691271951CCB699BD36D269AC07A。Root准入最小真实复用：既有ReadFoldedSequenceSql扩同row through_seq/text/folded_count projection，loop一次读原同uid/scope row，原Scalar保首列through_seq；ForPrompt两个相同完整SELECT uid抽一个完整constant，各自scope WHERE suffix原样。预计减3定义/2往返是计划不是实测，保所有SQL语义、完整可读关键词/scanner/137棘轮，不split逃逸。
- 不为授权覆盖而动其它7Store；Root只该MemberProfile/ownedtest，00不写。新增回归证明scope/through_seq/折叠计数/文本shape/ForPrompt排序保持，只合成文本，不读真实成员资料。source完成后由Root与00纯mapper窗口同步串行build/Architecture，真实后置SQL度量再记headroom，不据计划宣称134。


### 22.38 Device私有闭集持久计划与每次运行派发门禁（实施前）

- src/BotAgent.Storage/ConfigurationStore.cs inputSHA256=156336E1F2D19E7F96B6F8E9D41BDD89B774CC733AF102D300840CEEB0322E61
- src/BotAgent.Storage/ConfigurationStorageBridge.cs inputSHA256=5433D5338DF7746D89C13842756BC7B3445E79726A4B28D65056A58D0B5E1058
- tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputSHA256=A822581E91560CF9ED57A5A01CF89F5FDD5DC519F46531E6C2BEE20287500853

- 00核冻结518/520及真实AppSettings.ParseDeviceConfigs615：NameRaw.Trim需在owned规范化/HMAC前统一，保真名不mask，OrdinalIgnoreCase碰撞/撤销名拒绝。Root只三owned路径：Bridge闭集 ConfigurationDeviceState(Key,ActiveName,DesiredName,DesiredEnabled,Deleted,RenamePending,RevokedNames)，LoadDevices(conn)；PrepareDevices(conn,desired fulltable,profile)→PreparedDeviceChange(Changed,Errors,Assignments,DesiredWithKeys,Persist)。纯同conn读/计划，Persist才复用UPSERT，assigned keys不进入原请求fingerprint；prepared安全ToString，不输出名称。
- Strict仅nullKey create，未知给定Key/重复trim+IgnoreCase/active-desired跨Key碰撞/revoked整批拒绝。Legacy无key只唯一旧active/desired保key，省略delete，改名delete-create；歧义/tombstone LegacyNeedsReview副作用0。PreparedConfigurationChange新增可选FailureCode闭集仅ValidationFailed/LegacyNeedsReview，Store准确返回，不把兼容拒审假称普通validation或silent loss。
- AuthorizeDeviceHello(name)仅Host已Token认证后调用、不收clientKey；同AppDatabase.Write查gate，唯一pending desired匹配复用UPSERT确认active并INSERT旧active revoke。CanDispatchDevice(name,DeviceKey? serverBoundKey)每真正send前查持久gate，registered需active名一致/desired_enabled/notdeleted/notrevoked，serverBoundKey只能服务端关联且如有必须相同；pending新desired未确认不派、已enabled原active可派，disabled/deleted立即撤销。未知从未登记且无boundKey才保旧legacy规则，不借未知默认复活被撤销key/name；DB异常deny不回显异常正文。
- 新private participant virtual AppendDeviceRuntimeAudit(conn,DeviceKey)默认no-op，Host后续固定安全事件同TX（不名字、不client身份值、不造配置receipt），active确认属runtime事实不递增用户配置version，如circuit。初始旧Devices在Host Initialize后续同TX导入并按现一次version+audit，不在Read迁移；本批Root只计划/gate+合成，Host JSON/opaque保留与全部hello/选择/worker/control后续shared批。
- 原已批MemberProfile真实去重预计3SQL余量，此批Device仅3完整SQL snapshot LEFT JOIN revokednames、UPSERT、revoked INSERT，各操作复用，不额外拆关键词或新增数字容量。SQL137棘轮保持，Member去重和本批仍须真实defaultArchitecture验收，不拿schema存在/临时deny当功能完成。不增publicCore/FQN/新路径、冻结contract hash不改；Root source完与00mapper同步串行build，原断言保留。

### 22.39 Member查询余量实测与共享批3b pure140 scalar映射（实施前）

- Root22.37真实18:05:34.8515752～18:05:41.1032465 Review build0（8既有CS0108），run至18:05:52.4779805退出0主451/0，新7回归绿，3source before/after无变，Member8D9ECFB70B6124D6B4C618745CBC57D968B254C238882BB8EB8F22491CDFEF69/testA822581…绑定。Architecture18:06:07.8358608～18:06:09.9050050 build/default两0、165/0、SQL实测134/137、Domain268。headroom是真实而非计划；保已定137棘轮供22.38三完整Device原语，不放宽或以拆SQL换绿。
- 00只原shared WebUiServer.Settings.cs inputSHA256=6132B913A8BB8F46D082C3F7CE8F173C8609C4538E768A502DA1861735DFCCA2，Root已同意并行pure140 scalar source；新增private ParseLegacyScalarFields(JsonObject)→七组ConfigurationPatch，116 simple scalar复制真实旧setter GetValue/Trim/Clamp/Max，24特殊保旧missing/null Keep、budget原空忽略/空白Set、agentreasoning/动作catalog、agentURL无效Keep、agentPrompt/levels截断、healthclock旧ToString/宽松HH:mm规则、model/base空Clear。无I/O/env/log/Save/runtime baseline读取。
- 此helper明确只140 scalar；剩两AgentDevices/PlatformPolicies通过下一专门typed复合/legacy适配完成，不将默认Keep冒称已经处理输入。9秘密由3a独立拥有，19旧属性仅从同TX DB carry-forward，不能反射public setter或造额外可写字段。没有新增publicCore/FQN/路径、没改旧HTTP调用或替代管理通路。
- Role legacyClamp字段与Strict最终validation的兼容仍需实际Root privateLegacy profile测试，direct model/base Clear旧HotSave只QQCHAT别名→默认不同Startup，另红再最小修；复合策略/设备及prelude Normalize/Synchronize必须基于同TX最新用户facts，不能锁外runtime snapshot拼patch。此batch只pure parser源码与测试，不称完整LegacyPlan/原子旧HTTP。
- 双方source窗口都完成后由Root串行build/实际parser/Device/Review/Architecture，00不build；现旧路保留、原断言全部保留，回退仅3b新增mapper/纯helpers，不能倒退3a/Token/Root已写Device或旧工作区修改。

### 22.40 Root Device真实红绿及shared140源码窗口冻结

- 00批3b pure140源码输出WebSettings C1F8186E033D764219BA608F89955026D7E4488154B027835ECA616095BA1F66、1396行、scoped diffcheck0；真实private ParseLegacyScalarFields25行与七typed helper已就绪，未接旧HTTP。Root source窗口完成后00保持冻结供Device与actual mapper测试，未自行build。
- Root Device实际18:15:22.8077688～18:15:29.5445405 Review build0（8既有CS0108），child至18:15:30.8030246退出1、27/1；18:16:04.1208281～18:16:08.6274925再次build0/child1。安全code/count证明触发回滚完整，真实CommitFailed；测试错期待StorageUnavailable。查既有Store callback/原子负例后仅纠精确测试枚举，保version/rows/secretAbsent/FindNotFound全部断言，未改产品掩红。
- 18:16:36.9027391～18:16:42.2526767 build0/child0、30/0，覆盖singlewriter gate/keys分配及replay/trimHMAC/跨key/disable与re-enable/pending-confirm rename及fault/runtime确认不增用户version/旧名撤销/Legacy删除创建/full Clear/storage fault关闭fallback/部分upsert秘密与version整体rollback和重试。该Storage计划/持久gate合成不是production Host hello/selection/worker/control采用。
- 00只读发现Root新FieldError调用与Core真实FieldAddress ctor不匹配已主动报告，由Root在ownedBridge修正，没有扩Core接口；Root另查真实Nonce/ReadEditable/security_audit_log/event_type修正测试位置，不臆造接口或表。Source现待Root actual140mapper测试/全Review/Architecture终态，SQL三新定义及Member134余量需合成后真正default实测，不预报棘轮通过。

### 22.41 140mapper与Device联合真实门禁783及23:11恢复

- 01真实18:24:13.1212076～18:24:18.5137495 build0，run至18:24:30.6555674退出1、主779/1；新fixture用defaultTopP JsonValue<int>(99)，不匹配真实旧GetValue<double>节点类型。只纠fixture为99.0，未改mapper/产品或断言；18:25:03.4018638～18:25:07.4767186 build0，run至18:25:21.0963747退出0、主783/0，Device30/0。Architecture18:25:41.5506922～18:25:45.4413814 build/default两0、165/0、SQL137、315files/57474lines、Domain268。
- 七输入hash绑定重跑18:26:10.2049171～18:26:20.3217433退出0主783/0，before/after全无变、diffcheck0。Root Store D97A67A6EA499B5A9C95BA252C1E3ECC5822BC39BBEBD5AFA37709EF4331FEB2、Bridge19F1F4BCACE0BB6A20FD80F18EB78F464E3F7DB0640F66FD21F3779F82887961、Member8D9ECFB70B6124D6B4C618745CBC57D968B254C238882BB8EB8F22491CDFEF69、test AF77CA2C013E65BF9F58544BF6673F2A98B8BDF79437DBD6124D0462939E773E；WebSettingsC1F8186E…、SettingsStoreA64C8B…、BotConfigD9BBC84F…无变。这仅actual private140mapper/六槽/Storage gate与adapter，非HTTP/production采用。
- 23:11恢复00唯一shared集成/总纲登记席位，重新只读HEAD1f3181e21e8d1f9531e41066b033d8d60218d580与Store/Bridge/shared上述hash全匹配；未重派/扩权/并发build，保370展开状态与20暂存删除。未完成Device Host Initialize/Prepare/Read、剩两复合mapper、生产Bootstrap/legacyHTTP/UI/实际dispatch链，不能phase2 ready。

### 22.42 Root Device备份跨进程恢复测试增量与短验证窗口

- Root原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputAF77CA2C013E65BF9F58544BF6673F2A98B8BDF79437DBD6124D0462939E773E，新source报告完成，当前SHA256=7DE52F1DA2B6E93859944DCDB5B85984273D3F2520B48AD32ED6FE313C18A146。只此原授权test新增真VacuumIntoBackup至同UUID父根/device-restore、closed --configuration-device-restart独立child，由现TryRunChild factory接入，无Program/新路径改写。DeviceChild32/Restart7是测试计划，尚未build，不能登记为绿。
- 先校验准确父根和限定子目录，不操作真实库/群聊/成员；重放旧Clear不删除恢复后新active2，deletedKey与active/desired历史名不defaultAllow，newgeneration/原receipt/slot/audit/version不重写。不是旧binary回退/混DLL或production行为。
- 因00上轮中断，Root18:26派发的本批登记本轮补齐在Rootsource完成之后，未假称前置登记已发生；路径/验证本身仍既有明确授权，后续source batch必须先登记。00确认shared SettingsStore=A64C8B219F866A421B107FBEB08D5F856E2F650C4465A74068FC2EC7E45E7614、WebSettings=C1F8186E033D764219BA608F89955026D7E4488154B027835ECA616095BA1F66，准许Root短串行build+Devicechild，00不写sharedsource/不build；新HostDevice方案先评审后登记，不抢本窗口。原空指纹为PowerShell插值误吞紧邻中文，恢复后实测补正，未假定未知hash。

### 22.43 Device备份跨进程真实门禁32/7

- Root23:14:40.0685209～23:15:03.5912286 Review build0（增量0warn不等于修既有警告），Device child至23:15:06.1999135退出0，parent32/0+nested restart7/0；exec15661最后poll确认为exit0已关闭，五input before/after无变，test7DE52F1DA2B6E93859944DCDB5B85984273D3F2520B48AD32ED6FE313C18A146绑定。证明新代/旧Clear Find replay不删除后续2active/slot原样/version audit不重做/稳定deletedKey与active-desired历史名deny；非旧binary回退或production Host采用。

### 22.44 shared Device Host同TX初始化/Prepare/Read与pureLegacy Device（实施前）
- 00 src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs inputSHA256=A64C8B219F866A421B107FBEB08D5F856E2F650C4465A74068FC2EC7E45E7614
- 00 src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs inputSHA256=C1F8186E033D764219BA608F89955026D7E4488154B027835ECA616095BA1F66

- Root已窄确认，00只两existing shared。SettingsStore internal pure ParseLegacyDevices(string?,bool readStoredKeys)全表六旧field，旧新body未知nested项含key/secret/password拒审，已DB opaque只原样carry；Name trim/IgnoreCase collision整批拒审。实际旧UI app.js2493～2500只发送name/enable/model/workdir/tools/timeoutSec，user JSON保持六fields不新增key；stableKey只schema，trusted同conn唯一desired/active关联。已导入关联Key后Root Strict初始化保身份，首导入Legacy；Read不生成Key。初始化不改原Device JSON或旧rawtimeout。
- 旧Timeout Num仅Int32，否则0；实际NewTask304/worker只正值override，<=0 typed归0，>0 Clamp30..7200；legacy保存已DB rawtimeout规范等效则原样保留，不误把负值Clamp30。strict明确编辑存规范值。Device Changed除Root gateFacts还比较Model/workdir/tools/timeout用户JSON事实，不能遗漏同key参数变化。
- Host Initialize同现唯一TX Token+Device导入，现一次版本/audit，Key/opaque不进SafeDTO；Prepare Set/Clear真正Root plan，合并Changed/Assignments/FailureCode，用户JSON+device+secret/provider/audit/receipt一TX，无新SQL。Read desired/effective分开，NameEditable只授权nameRaw、Display mask、Overview Hidden不持raw/opaque；enable来自持久gate，effective model等仅实际runtime快照；persistent pending对应RenameState RestartRequired，不能伪称whole remote行为已Applied。AppendDeviceRuntimeAudit固定key/safe事件同TX不增userVersion。
- WebSettings private ParseLegacyDeviceFields(JsonObject)只pure typed Device，missing/null Keep，空字符串=空全表；不接HTTP/productionActivate，Policy第二复合先真实catalog/基线评审另批。不放宽护栏/新增路径，source完成后Root串行actualHost合成测试。回退仅本batch片段，保Token/140/parser/Root Device。

### 22.45 Root private Started claim与当前代真实global4096（实施前）
- Root src/BotAgent.Storage/ConfigurationStore.cs inputSHA256=D97A67A6EA499B5A9C95BA252C1E3ECC5822BC39BBEBD5AFA37709EF4331FEB2
- Root src/BotAgent.Storage/ConfigurationStorageBridge.cs inputSHA256=19F1F4BCACE0BB6A20FD80F18EB78F464E3F7DB0640F66FD21F3779F82887961
- Root tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputSHA256=7DE52F1DA2B6E93859944DCDB5B85984273D3F2520B48AD32ED6FE313C18A146

- Root只Store/Bridge/test新增internal TryStartApplyStep(caller,key,step)→(CanRun,ConfigurationFindResult)，现同writer。first Changed+Committed、现Version==CommittedVersion、step缺省或NotStarted才claim，先持久Unknown/apply_unknown再true。非NotStarted一律不二次执行，ledger故障false；未开始而superseded记Failed/configuration_superseded（真实被阻断，不表示动作已执行失败），其它历史steps不变。共用现完整UPDATE receipt/terminal helper，不新SQL/Core枚举/FQN；Host未来同SettingsBox串行Save→publish，claim不假称无锁永不被新版本改变，mood不塞八steps。
- 当前代global4096仅Root原ownedtest：隔离bounded child用16实际caller×256真实NoChanges，17subject新Save cap，原key replay/异参Conflict优先；expiry仅terminal prune、Unknown/Pending保留。不是伪SQL造ledger，不造mg1/C4记录、不放宽4096/timeout或删断言；跨代预算另child后续，不预报绿。现factory足够，无Program新改。
- Root两增量同test单owner顺序编辑，00shared独占两source；双方source完成通知后统一串行build/Review/Architecture。所有验证合成根/环境/进程，未授权生产/发布/commit/push。

### 22.46 Started退态窗口修准入与Strict端点目录负责人裁决

- Root22.45 Started原source8FDBE0BAC6044BE1A7457F30FC98ACA8F4604645F5100313540DB876DEB9AB83、Bridge0BE0B31044324FE194F759681D72E6540802B871FB28D30D60CA840CE4EB02B5、testED8D78CE1FA9FF44266F4AF480BF5527482CFDDDF3E2F4B89747D8CA29A1DC70均已source完成/diffcheck0，待短串行。00准入Root原owned Store（当前hash=8FDBE0BAC6044BE1A7457F30FC98ACA8F4604645F5100313540DB876DEB9AB83）真实red后最小guard：已有非NotStarted step不能通过RecordApply退回NotStarted解除持久claim，不抹其它历史结果，不增SQL/Core接口；保当前13applyclaim和global4096真实断言，pending不能预报绿。
- 负责人本次明确Strict新模型端点用途授权仅复用已登记Provider端点。用途=模型Provider既有目录；验证同TX已存在元数据快照，不允许body本批先改Provider URL给自己登记/授权。URI规范匹配、现网络安全规则保留，不硬编域名/任意env。未经登记目标继续endpoint_authorization_required；不扩大代码路径/秘密用途、公有C3/FQN/冻结hash，此业务确认登记于此，不擅改冻结candidate原文件。
- 下一00独立shared批以实际已登记端点集合替代临时全deny，先核实际URI/出网原语与Host消费再登记输入/改段、合法/未登记/同批自授权/规范化负例。此窗口不得改当前endpoint守卫，shared SettingsStoreAFBD67DCABE835D63BD6EEA09477E555EE8788FC41D33A56381FE9792A00DA95/WebSettings005E5B3EB9D7B8D90B1F9AA3C084559D21ACF3EF52DACA59A345583ACE3E3398冻结供Root短build/applyclaim真实redgreen/global4096/actualHostDevice。

### 22.47 shared Device源码就绪、Started真实红绿与global4096

- 22.44仅两shared完成：SettingsStoreAFBD67DCABE835D63BD6EEA09477E555EE8788FC41D33A56381FE9792A00DA95/1168行，WebSettings005E5B3EB9D7B8D90B1F9AA3C084559D21ACF3EF52DACA59A345583ACE3E3398/1405行，scoped diffcheck0；internal pure ParseLegacyDevices与private Web ParseLegacyDeviceFields、实际Host Initialize/Prepare/Read/RuntimeAudit已落，旧JSON不新增key、不重写初始化rawtimeout，schema trusted关联。编译基本可用尚需actualHostDevice专验；未HTTP/production启动采用，Policy仍待实际catalog方案。
- Started真实新红23:28:27.9961035～23:28:59.4986855 Review build0（8既有CS0108），claimchild至23:29:01.8242483退出1、6/1，精确Unknown→NotStarted reset原RecordApply可解除claim。只RootStore加22.46已登记退态guard，不增SQL/改Core/删断言；23:29:28.0686295～23:29:56.7267961 build0、child至23:29:59.0046779退出0、13/0，原红与其它原断言保留。
- 当前代global4096真实child23:30:07.7223852～23:30:20.3043007退出0、9/0，12.6s在原parent60s以内，未改timeout/容量。真实16caller×256 NoChanges达满额后replay/conflict仍优先，TTL只terminal清理、Unknown保留；无伪SQLledger/mg1或Scanner逃逸。这不是跨代全容量/C4验收。
- 尚未全Review/default Architecture或新增actualHost Device Initialize/Prepare/Read专项，00两shared AFBD/005E继续冻结，Root只ownedtest扩实际反射调用。Only oneRoot build窗口、00不并行build，不用13/9单child绿提前phase2 ready。

### 22.48 2026-10-13恢复与actualHost Device专项红窗口

- 00恢复唯一shared集成/登记席位，重新核HEAD1f3181e21e8d1f9531e41066b033d8d60218d580、冻结contract7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203；无新增嵌套AGENTS。shared SettingsStoreAFBD67DCABE835D63BD6EEA09477E555EE8788FC41D33A56381FE9792A00DA95/WebSettings005E5B3EB9D7B8D90B1F9AA3C084559D21ACF3EF52DACA59A345583ACE3E3398实测匹配，继续源码冻结，00不build。
- Root原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputSHA256=ED8D78CE1FA9FF44266F4AF480BF5527482CFDDDF3E2F4B89747D8CA29A1DC70，只新增actualHostDevice18专项，既有TryRunChild/ModuleRecovery接入，无Program/新路径。Root恢复后已在00本轮登记前完成source，当前实测6C538A64FDF24F9EC9B7607413F7EC08D76FE64DDDC7FE65A98B71EE8701EAF6；此处如实补登记既有授权test增量，不假称已前置登记。
- 固定安全label收集两待验负例：requested空Inherited需实际runtime有效值；field3预Explicit且compact六facts语义相同时必须NoChanges，不用模式迁移掩格式假Changed。其余Initializer版本/稳定key/原JSON/opaque/rawtimeout/参数真实Changed/desired-vs-runtime/renameaudit/secret故障整批rollback/Clear/replay/tombstone/newKey均保断言。18是计划数不是通过。
- 准入Root唯一短串行Review build及--configuration-host-devices实际红窗口，00保持AFBD/005E，不在本窗口改endpoint或Policy。Root发真实红后00另登记SettingsStore精确最小修再合入；专项绿不等于生产DI/HTTP/dispatch或Wave1验收。

### 22.49 actualHost Device两真实红与共享最小修（实施前）

- Root12:04:17.0618855～12:04:30.2839689首Review build1：fixture错用Core EnableAgentMask作Host属性；查询真实AppSettings后仅纠fixture AgentMaskSensitive，不造接口。12:04:46.7000686～12:04:49.5839492 build0，child至12:04:51.2217143退出1、host-devices16/2。两固定红：inherited empty requested fields expose actual effective values；compact format alone is not a configuration change。其余16初始化/opaque/rawtimeout/参数Changed/rename/audit/原子/tombstone/assignment真实断言通过，test实测7ACB17A91D0F2A5CFD7B6D34A9A1B29A0BCEDA42C1D1DB9EA2E4786DCB9BABB5冻结。
- 00只已认领src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs，inputSHA256=AFBD67DCABE835D63BD6EEA09477E555EE8788FC41D33A56381FE9792A00DA95实测匹配。改段限SerializeDesiredDevices末尾：完整JsonNode.DeepEquals(original,result)时返回原originalJson，避免仅重排indent生成假Changed；DeviceViews三个model/workdir/tools requested取值非空白才用用户项，Inherited空/null/空白使用真实runtime有效值。保用户JSON/opaque/rawtimeout、真实参数Changed、mode/version/赋Key行为，未混endpoint/Policy或生产接入，不动Root测试/旧断言/SQL/护栏。
- Source完成后00冻结并交Root唯一串行重建/实际child红绿，再全Review/defaultArchitecture；当前不预报通过。回退仅本四小改段，保22.44和工作区既有变更，禁止强制reset/清理/提交发布。

### 22.50 actualHost Device最小修绿与下一端点边界

- 00四段最小修输出SettingsStore85153CD330A18682DE5C11F9BB1E07CD28B99A15B3C4669E1EB4C73B5E1E03F4，Web005E5B3EB9D7B8D90B1F9AA3C084559D21ACF3EF52DACA59A345583ACE3E3398未改、scoped diffcheck0。Root12:05:47.3146666～12:05:50.5768083 Review build0（8既有CS0108），actualHostDevice child至12:05:51.2627900退出0、18/0；五input before/after不变。保原16及两红断言，未用模式迁移或format整写放松NoChanges。
- Root继续唯一全Review/defaultArchitecture窗口，00shared8515/005E保持冻结不build，不提前改endpoint。真实production DI/HTTP/设备派发尚未接入；actual private Host adapter十八断言不是Wave1验收。
- 下一独立SettingsStore端点批窄评审：复用Prepare同TX providers原快照，完整Uri.AbsoluteUri规范相等（scheme/host/defaultPort标准化，path/query/fragment保精确），不按host授权/不由本批body登记端点，不增SQL或网络IO。保Core已有userinfo/长度/UTF安全，不自创未冻结query/fragment禁令。SafeUrl是已有静态URL/IP防护，模型transport尚未采用其DNS/redirect安全连接；Save源码不能冒称整条出网链通过，transport文件未授权不扩写。

### 22.51 合法Device实现后旧暂拒fixture迁移（实施前）

- 全Review12:06:51.4637246～12:06:54.4530745退出1、main764/1，五input哈希无变。旧actualHost22曾以合法Device输入要求adapter_unavailable，现22.44真正支持Device后该临时缺口断言与新目标矛盾；其空数组Read也不应永远LegacyNeedsReview。不能为了旧临时拒绝恢复产品缺口。
- Root只原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs，inputSHA256=7ACB17A91D0F2A5CFD7B6D34A9A1B29A0BCEDA42C1D1DB9EA2E4786DCB9BABB5。仅两fixture按真实永久安全边界更新：写入unknown supplied DeviceKey→invalid_device_identity，保整批ValidationFailed、AiDesire43与versionBefore不变；Read种合法但schema未分配Key的新legacy名→LegacyNeedsReview，保Viewnull、真实version+generation、Read不隐迁移/不泄露。没有删除原子性/版本/header/安全断言，合法Device成功另有actualHostDevice18证明，不放宽数值棘轮。
- Root完成后仍唯一串行全Review/defaultArchitecture，008515/005E冻结不改源码/不build。此为功能实施后旧临时fixture迁移而非删断言换绿；实际结果后置登记，仍不宣称production或Wave1通过。

### 22.52 统一门禁786/165与Strict既有Provider端点批（实施前）

- Root12:08:27.5149570～12:08:30.2435659 Review build0增量，run至12:08:42.0741175退出0、main786/0；actualHost22、HostDevice18、Device32/restart7、Started13、真实Global4096/9、Token17cases均过。22.51只两旧临时拒绝fixture迁永久非法/未迁移事实，原子性/版本/header/安全断言保留，合法Device18另证功能。Architecture12:09:22.4295941～12:09:24.7053471 build/default两0、165/0、SQL137、315files57713lines、Domain268；scoped diffcheck0。test D577ABF1FBEDF36AFD42A15ECA5293D156EC329026A40600FD366B2A63BCBEDD。不是production采用/Wave1验收。
- 下一00仅已认领src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs，inputSHA256=85153CD330A18682DE5C11F9BB1E07CD28B99A15B3C4669E1EB4C73B5E1E03F4实测匹配。依22.46负责人确认，Strict显式ModelBaseUrl Set/Clear无论当前凭据Configured与否，都以原同TX providers快照授权；不能先冷凭据改未知端点再Replace绕用途授权。本批body不改目录，不新增SQL/networkIO/构造public接口。未改旧endpoint与Legacy旧行为保持。
- 规范完整Uri.AbsoluteUri相等，仅标准URI scheme/host/defaultPort规范化，path/query/fragment保精确；候选164/743已拒UserInfo并保合法旧query，不自创query/fragment禁令。实际目标变化复用现SafeUrl.TryValidate(url,false,...)纯静态公共地址政策，不读图片私网env扩大模型权限；已原primary规范同端点不表示新增目的地。错误只固定endpoint_authorization_required，不吐reason/URL。DNS/redirect安全传输未接，不能把Save静态检查称整条出网通过；transport源码未获写权不扩写。
- Root下一ownedtest实际Host目录已登记允许、未登记拒绝、冷凭据拒绝、URI规范/路径query精确/私网-userinfo拒绝、同批body自授权拒绝后唯一串行重建/全Review/Architecture。00source完成交窗口，暂不接HTTP/productionActivation，不改Policy/Core/hash/棘轮。回退限本目录检查替换与私有helper，原Device/Token均保。

### 22.53 端点source输出与Policy真实消费者待授权清单

- 00端点source已完成SettingsStore28874373DF5933DECDE3E95F1D45AD73C3C09BD33BC997FA48E799B940816688、scoped diffcheck0，仅该文件，不build。Root实际endpoint合成验证待结果；新源码不凭目录/静态SafeUrl检查自称DNS/redirect安全或production采用。
- 仅只读评审，以下八existing路径是Policy前置整改候选、**尚未授权/未认领，00与Root都不得据此写入**：src/BotAgent.Platforms/PlatformOptions.cs（旧List<PlatformPolicySettings>二元config）；Governance/PlatformPolicyResolver.cs（Resolve46二元IgnoreCase+channel缺实例）；Governance/PlatformRegistry.cs（字典14/15二元、GetSnapshots51丢Instance）；Governance/PlatformSwitchSettings.cs（Read/Find旧scope任意fallback）；src/BotAgent.Core/Domain/Platforms/PlatformPolicy.cs（bool?/字符串旧覆盖与feature字典）；src/BotAgent.Core/Domain/Ports/IPlatformAdapter.cs（snapshot/registry二元surface）；src/BotAgent.Headless/Services/Qq/WhitelistGate.cs（AllowsKey130只channel，飞书直接true）；src/BotAgent.Platforms/Feishu/FeishuBotGateway.cs（IsAllowed477只platform首命中，显式空回legacy且不隔account）。Core PlatformId.cs已有PlatformContext三元，不需为名称猜新接口。
- 真实当前链：CompositionRoot110构造Resolver→WhitelistGate139/ReplyPipeline467、555/PlainSender227、276/QqActionTool280按channel ResolveForChannel→Resolver36构造缺Instance context→Registry二元snapshot。Feishu自己的IsAllowed477另绕policy scope。最小配置整改需typed规则容器+AccountDefault/Instance逐字段继承、可信配置目录含disabled实例、三元Ordinal注册与唯一旧二元匹配、Switch/Whitelist/Feishu实际同context策略消费；仅两个shared puremapper或RestartRequired无法证明隔离。现消息envelope来源Instance传递及回复/工具链是否属此次前置整改须02冻结，不在本清单暗授权Wave2全改。
- 需负责人明确Agent02限定前置整改或精确shared临时移交写权，00登记后实施。Policy受影响接入停止，其他已授权endpoint/Device/Token合成继续；不声称Wave1或Wave2准入。本轮一个只读rg查QqActionCatalog声明在猜测目录无命中exit1已停止该command，无源码写入/测试失败，不以文件名猜不存在接口。

### 22.54 Root actualHost endpoint合成测试增量（实施前）

- Root只原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs，inputSHA256=D577ABF1FBEDF36AFD42A15ECA5293D156EC329026A40600FD366B2A63BCBEDD；原public TryRunChild/ModuleRecovery factory接actualHost endpoint child，无Program/新路径。覆盖冷/已配置凭据仅prior目录、完整URI规范匹配、path/query/fragment不同拒绝、UserInfo/新私网安全拒绝、Legacy兼容、同批body自授权reject；不读取原secret正文，错误仅安全Code/field/version与行计数。不把未登记fragment变化拒绝写成全面禁止合法fragment。
- 00shared SettingsStore28874373DF5933DECDE3E95F1D45AD73C3C09BD33BC997FA48E799B940816688/Web005E保持源码冻结、不build，test增量只Root写，终态后唯一串行build/child/全Review/defaultArchitecture。22.53八Policy路径在负责人答复前仍未授权/不可写，02先只读评审不是下游准入。

### 22.55 actualHost endpoint专项16通过（统一回归待终态）

- Root2026-10-13 14:31:09.737～14:31:34.489 Review build退出0（8条既有HttpFetcher隐藏成员警告），--configuration-host-endpoints至14:31:37.287退出0、16/0，五源码before/after哈希不变。SettingsStore28874373…/Web005E…冻结，test报告293EB15B…；详细全量test指纹与统一回归终态后置登记。
- 专项证明实际Host participant对冷/暖未知端点拒绝、既有目录规范完整URI匹配、path/query/fragment精确、UserInfo/新私网拒绝、已登记Clear fallback、同批自授权拒绝、Legacy及未改变旧private兼容、安全错误无URI/credential。没有网络IO/DNS/重定向验证，不是生产DI/HTTP采用，不是Phase2准入。
- Root继续唯一全Review/defaultArchitecture，00保持shared source冻结仅登记不build；Policy八候选仍未获明确写权，不能本专项绿擅自接入或解锁Wave2。

### 22.56 Strict Clear旧fixture补新目录授权前提（实施前）

- 全Review14:35:12.689～14:35:15.705退出1、764/1，actualHost child16/1；唯一旧Clear正例的环境fallback端点未登记，现按负责人22.46规则正确拒绝。五哈希无变，不放宽产品校验。一次旧summary路径Get-FileHash错误exit1未启动dotnet，Root随后查询真实Adapters/Persistence；非产品失败。
- Root仅原owned ConfigurationContractTests.cs（端点16输出指纹293EB15B…），保原Clear所有结果/用户/primary/版本断言，先追加未登记Clear整批拒绝且版本/用户/primary不变，再以既有ModelProviderStore同合成fixture登记fallback端点，用新operation key重试原Clear正例。不是本批body自授权，不删原安全断言，增加负例锁住新门禁。00本轮实测25A7A5B8F4203E850955AD463AC6E3C93FB2F73DD12C194CD84CF44C7C20B123已是Root限定小修之后而非前置input，Root已确认；此处如实记录登记晚于该test编辑，不能把output当input。
- 00shared2887/005E继续冻结，只Root该test小改与串行重建/全Review/Architecture；真结果后置登记，不宣称生产/Wave1或放宽任何阈值。

- Root报告14:36 build0、独立actualHost23/0（具体起止时间待完整终态）；父全Review仍764/1因原精确子汇总期待passed22。准入仅父精确汇总期待22→23以计入新增拒绝断言，不删/减断言或放宽产品阈值；已按Root报告修正，重新完整回归待终态。子专项绿不替代尚红的父工程门禁。

### 22.57 端点统一Review787与八文件Platform条件授权

- Root14:36:56.116开始build→14:36:59.555 build0/增量0warn（不等于既有warning已修）→14:37:14.078 Review退出0、main787/0，actualHost23/0、Endpoints16/0及全部既有children通过，五hash无变。test C4809B8191EB30276DF26C2D69F96BC807698A3CF4DA9388C187C471EC728731。主786→787为新增endpoint父模块入口；Host22→23为新增未登记Clear拒绝断言，保原全部正例/原子/版本/header断言。产品未放宽目录授权。
- Root传达负责人本次明确授权22.53列八文件平台限定前置整改：**Agent02先只读评审，之后00登记最小整改写权**；Core/Headless共享由00最终合入。这不是当前即准入八文件源码编辑，也不是Wave2整体准入；准确批次/接口/consumer改段及input哈希待02报告冻结。Root将启动已明确角色02只读席位，00不得抢先写八路径/扩大消息链或其它平台文件。
- 此刻shared2887/005E仍冻结供唯一Architecture窗口，00仅登记不build；DefaultArchitecture终态与02只读报告分别后置，Review787不能代替架构或production真实采用。

### 22.58 Architecture与Safety实际终态、Root Started跨进程恢复测试准入

- Root14:38:03.452～14:38:09.295 Architecture build/default run均退出0，SQL137、Domain268，scoped diffcheck0（仅既有CRLF提示）；初筛未捕获中文passed汇总，精确总计待补，不将历史165冒称本次已捕获。源码冻结窗口可结束，但产品采用/Wave1仍未验收。Root20:16:56.535～20:17:19.634真实Safety工程build/run退出0、594/0，全部合成无真实数据，00未并行构建。
- Agent02首次启动usage-limit终止，无只读报告、无源码修改、无已登记源码写权；Root已同席位followup重试八文件只读（非新线程/抢写）。02前置评审未完成不能据此直接改八文件或冒称Wave2准入。
- Root下一仅原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs，inputSHA256=C4809B8191EB30276DF26C2D69F96BC807698A3CF4DA9388C187C471EC728731实测匹配。现apply-claims追加真实VacuumIntoBackup及fresh dotnet闭合--configuration-claim-restart子路由，同文件factory/no Program。证明Unknown ContextWindow/Schedulers与Applied TTS跨重启不二次执行、旧generation Replay保原ledger、NotStarted降级拒绝、version/audit/secret/userwrites无副作用；只合成UUID受控目录，先核父根/目标，仍原SingleDB/Clock/SyntheticParticipant，不碰真实路径。
- 父13→14为新增恢复子结果断言，子预计8～9未运行不能当通过。Root获本条登记后编辑，实际红后查真实原语不删断言；不改public契约/Store/共享源码，不冒称生产application恢复。完成后与00/02source状态确认唯一串行验证窗口，Root仍唯一builder。

### 22.59 Started跨进程恢复真实通过与兼容基线窗口

- Root20:19:25.611～20:19:29.182 Review build0/增量0warning，child至20:19:30.830退出0、parent14/0+nested9/0。真备份持久Unknown ContextWindow/Schedulers、AppliedTTS与supersession ModelFailed，freshprocess新generation、不重做Unknown/Applied、不降NotStarted，旧Replay保ledger、version2/audit不变、writes0、nonterminal2（两个Changed都未全八step终态，不伪造terminal）。
- 完整Review20:19:49.227～20:20:01.459退出0、main787/0，到20:20:02.580 defaultArchitecture退出0、165/0；scoped diffcheck0、五hash无变，test1F06E1DA9857D14113BE6A6DA3C3F402F5DD9C13C18EDCEF1B0528F1904A9C9D。子新增恢复断言未新增父module，所以主数保持787。非production pipeline/自动未知恢复重做证明。
- 下一Root唯一SettingsScope/ProductionSpec/Frontend兼容baseline合成窗口准入，00shared2887/005E source继续冻结不build。先清子shell BOTAGENT/QQCHAT/OPENAI环境名称不读取/回显值，确保DataDir仅合成UUID根。后续IT S21/S36/S52须先读真实StartBot mock隔离/非external模式并真实Headless Rebuild，另确认窗口；baseline绿也不是新C3宿主采用。

### 22.60 Registered真实接口评审与未冻结秘密facts阻断

- 00仅只读盘点：ModelProviderStore.LoadAllInTransaction/UpsertMetadataInTransaction已可同TX复用，无新增SQL；SettingsStore Prepare当前RegisteredProviders/Plugins暂adapter_unavailable，Read目录委托可信_registeredRead但未证明production绑定。IPluginRegistry真实GetAll/Find/IsEnabled；PluginManager已CompositionRoot224～231登记7preset，具体SetEnabled存在但未证明各功能消费者gate/生命周期采用。PluginStore全src无consumer，现自有_gate与GetMeta/SetMeta wrapper不可在配置TX内再开另一writer。纠正00初核误称PluginManager无CompositionRoot引用，以此实际源码为准。
- 最小Provider产品方向已Root认可但**未准入源码**：sameconn prior注册目录，已登记对象metadata规范比较/全批校验/同TXupsert，safeRead来自真实metadata，primary Priority/Enabled按冻结497 ReadOnly，非primary无热切换证据RestartRequired；新端点沿prior目录授权、未知key整批拒绝、disabled Unconfigured合法。primaryModel/BaseUrl别名需Rootowned canonicalization并保已登记验证，不可dropPrimary使未登记绕过。
- 真阻断：当前main槽SecretMetadata不足以证明每个旧env:QQCHAT_API_KEY及secret:apiKey的实际ready。CompositionRoot635～657前者QQCHAT→OPENAI→函数settings.ApiKey、后者DB-only；03真实caller后核确认该参数来自settingsBox.Current而非仅startup snapshot，00初核startup措辞已纠正。非primary候选/runner仍启动捕获，但凭据callback当前snapshot不能混。Startup/HotSave/proposed删除行为需03实际resolver冻结，只闭集known refs/binding/configured、无secret原值，不能任意env命名或以mainSource推ready。未新增ctor/callback/source；03本次仅只读授权，不授实现权。
- Plugin后续候选额外路径src/BotAgent.Storage/PluginStore.cs（同connmeta原语，不新增SQL/独立gate）及src/BotAgent.Headless/Services/Plugins/PluginManager.cs（持久状态初始化/实际启用消费），**目前未授权/未认领**；IPluginStore优先不改公共面。Root将向负责人提精确限定扩权，不能把先存bool或插件登记存在当持久化+宿主行为验收。00不擅改这两文件或Core插件端口。

### 22.61 旧宿主兼容baseline与真实重建后IT终态

- Root SettingsScope20:22:12.701～20:22:16.187 build/run0、17PASS failures0；ProductionSpec20:22:16.188～20:22:19.811 build/run0、62/0；Frontend20:22:19.812～20:22:27.429 node0、356/0。验证前仅按环境名称清继承敏感环境，不读取回显值，测试根均合成隔离。
- 真Headless -t:Rebuild20:26:33.841～20:26:39.677退出0（8既有warnings），Harness -t:Rebuild20:26:39.678～20:26:42.771退出0（2既有warnings）；随后s21 20:26:42.772～20:26:50.597退出0/37/0，s36 20:26:50.598～20:26:57.661退出0/98/0，s52 20:26:57.661～20:27:04.205退出0/119/0。四sourcehash无变，Headless产物SHA256=4A3F073EA3077CE84B55C6F2FF03494C9CE3C80801D602BB8EBF094FF5044175。
- 提前查实际StartBot仅清QQCHAT，子shell另按名字清BOTAGENT/QQCHAT/OPENAI/FEISHU/MINIMAX/TTS，mock+NewDataDir UUID临时根、非external；未读真实日志/DB/协议。一次只读误猜RuntimeSettingsScenario.cs命令exit1后查真实ModelConfigScenario/PlatformSwitchScenario纠正，无测试源码修改/产品失败。真实旧HTTP兼容通过，不是新C3 HTTP/DI采用/Phase2准入。
- 串行source窗口已结束。下一产品batch需另登记输入/接口，Agent02与03前置未冻结仍不能抢写；00未运行构建或验证，全部真实成绩由Root提供，工作区旧改动/20暂存删除保留。

### 22.62 Root真实满4096跨generation容量恢复测试（实施前）

- Root仅原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs，inputSHA256=1F06E1DA9857D14113BE6A6DA3C3F402F5DD9C13C18EDCEF1B0528F1904A9C9D实测匹配。在现4096真实满额处VacuumIntoBackup至合成UUID父根已验证子目录、fresh dotnet closed --configuration-capacity-restart路由同factory，恢复4096旧代真实ledger；新代第17subject新key须CapacityExceeded，旧代Replay/异参Conflict优先，HMAC两generation仅形状计数保留；TTL仅终态prune后新代NoChanges准入。
- 不伪插mg1/receipt、不减4096/256实际Save构造，不删原9断言/改timeout，父9→10新增子结果断言，子约8为计划非绿。ProductStoreA0B8F70E…/Bridge0BE0…不改、公有合同/Program不改。00shared不抢源码，Rootsource完成后先协调唯一串行验证，真实结果后置。

### 22.63 Agent02八文件只读评审完成（局部整改未源码准入）

- Agent02 2026-10-13 20:26+08报告已按序补读两级AGENTS、无嵌套、规划/总纲截断均补分段及02手册；未写/未build/test/未读真实数据。HEAD1f3181…、370展开dirty/20暂存删除、冻结7B7CB…不变。八hash与00既核输入一致：Options90E7A6…、Resolver551AC5…、RegistryB09BC3…、SwitchB3C956…、CorePolicyC89183…、CorePorts28B856…、WhitelistB0F870…、FeishuC9A9C5…；实际完整hash由02报告和00前述核验保留，源码批仍须逐文件前置登记。
- 八文件可局部exact三元索引、typed逐字段继承与飞书自身真实Context gate，**不足完整来源/出站隔离**。生产BotAgentHost100/210→ReplyPipeline467/486/555/1203按channel→PlainSender150/159/167/178 _source.SendTextAsync→ChannelRouter175/197按target-id/channel，没有经过Registry.GetMessenger。QqChatSourcePlatformAdapter62/160只platform/account且丢instance。QQ/official/local当前Context空instance，Feishu真实feishu-main。仅新增重载不能谎称生产调用已采用。
- 拟最小slice只CorePorts additive exact PlatformContext查询及Snapshot init InstanceId保旧位置ctor、Registry三元Ordinal拒重复/旧二元唯一兼容。Options/CorePolicy保旧序列化DTO，未来JsonIgnore可信immutable typed运行投影复用既有C3类型，不塞旧body可写Target，不从policy或loaded snapshots授予配置目录。Switch逐field Instance→AccountDefault→仅四canonical baseline；Whitelist/Feishu显式空止fallback、完整context，未来源旧入口不能替任意实例授权。具体签名/归属待Root确认后00登记才写，八总授权不等于本条自准入。
- 02定位额外consumer路径（未授权）包括Platforms/OneBot/OneBotModels.cs；Core/Domain/Messaging/ConversationId.cs；Headless/Adapters/Platforms/QqChatSourcePlatformAdapter.cs；Services/Qq/ChannelRouter.cs；Services/BotAgentHost.cs；Services/Reply/ReplyPipeline.cs、ReplyPipeline.Delivery.cs、ReplyPipeline.Proactive.cs、PlainSender.cs；Services/Agent/QqActionTool.cs、AgentCommandService.cs；Services/Conversations/ConversationRegistry.cs。CompositionRoot/SettingsHotReload属00旧shared仍需另批实际改段；消息五元ConversationId不加instance，不能用共享会话单一lastSource覆盖并发来源。仅consumer盘点，不暗授权后续Wave2/media全链。
- 02只读路径误猜/QqChatMessage等已据真实src纠正；一次rg路径exit2未立即终止后续只读的流程疏漏已透明报告，随后逐exit复查，无写/运行产品。接受局部源码slice必须明确未覆盖，不能借其green称C3政策全部/Instance隔离/production/Wave1或Wave2准出。

### 22.64 Registry三元最小两文件产品slice（登记后仍等待Root窗口）

- Root已窄确认。00最终写src/BotAgent.Core/Domain/Ports/IPlatformAdapter.cs，inputSHA256=28B856FE3B3AB7DC59A7B288C903870FB668BF17EBD9F0BA8495E3A63374E058；02独占临时写src/BotAgent.Platforms/Governance/PlatformRegistry.cs，inputSHA256=B09BC352403F1AA31DEE8EC8B71382884D3CA5133D4674B2BAD357FDF7F8D884。两input本轮实测匹配；02不得写其他七文件/00文件。Registry仍属00协调最终合入队列，02该单路径移交明确登记于此，完成后交回00审查。
- Core additive exact签名IPlatformAdapter? GetAdapter(PlatformContext context)、IPlatformMessenger? GetMessenger(PlatformContext context)；保旧二元签名/defaults，PlatformStatusSnapshot保原位置ctor并加init string InstanceId默认空。Default implementation用于旧Synthetic消费者：adapter仅完整三元唯一匹配，messenger不能枚举时仅同账号Adapters唯一且返回旧messenger.Context完整三元匹配才给，歧义deny，不能绕回二元任取。非空platform先Normalize，scope/instance保有效原文Ordinal；空Instance是旧具体实例，不是AccountDefault。
- Registry concrete adapter/messenger分别完整三元索引，重复键显式拒绝不覆盖，exact查询不回退；旧二元查询同platform/account候选唯一才返回，否则null，保已登记旧单实例调用/ctor。Snapshot带真实Context.InstanceId，暂不伪造管理desired/capability/连接；目录仍非loaded集合。Core该文件≤300必须真正Architecture检验，不改阈值/旧断言。
- Root同文件追加受控输入freeze：constructor两个输入先过滤null→ToArray→Array.AsReadOnly，字段IReadOnlyList，public Adapters不返回可改内部List，避免input/public集合修改与三元索引不一致。02先查src/tests未发现Adapters cast/Add/Clear消费者；空/空白显式scope无效不以Default授权，原无account参数的既有Default正常；该改段仍仅Registry，不扩文件/接口。
- 当前Root容量窗口尚未终态，**源码仍冻结，此条登记不是立即开写**；收到Root窗口结束后00/02按两归属各写，完成通知Root，再其ownedtest真实collision/exact/二元ambiguity与默认interface安全负例串行build。只局部Registry覆盖/任取修复，不声称C3 Policy、源Instance透传/完整dispatch或Wave2准入。

### 22.65 跨代容量恢复真实红的审计fixture精确修

- Root20:34:33.481～20:34:37.280 build0，专项至20:34:41.973退出1、nested7/1 parent5/1，未继续全Review。第8fixture错期待TTL准入新NoChanges仍audit零增；真实Store219每个已批准Changed/NoChanges均AppendAudit，新的合法NoChanges准确新增一次安全决策审计，不应改产品隐藏行为。
- 仅Root原owned ConfigurationContractTests.cs，红inputDBF671…完整SHA未随summary提供；本轮实测90C5…已为修后输出，见22.66透明记录。保原user/secret/version/HMAC不变断言，新增TTL前replay/conflict/reject审计不变，最后精确audit=before+1。不删断言/放宽写入或SQL阈值；source产品A0B/0BE/2887/005E仍冻结不改，修后由Root重建专项真实红绿。此次登记晚于test编辑不能冒称前置，22.64两file源码仍等窗口终态。

### 22.66 容量跨代恢复红绿与CorePorts源码输出

- Root确认20:34红inputDBF671…、00本轮实测90C5BD2C782720D913E1DE7BF96401435F6B553095491C0888BFFEA2213E4785已是审计fixture精确修后输出，登记晚于本次test编辑，如实区分input/output、不伪造前置顺序。产品Store不改；保version/user/secret/HMAC无写，TTL前reject/replay/conflict audit不增，TTL准入新NoChanges仅增1audit。
- 20:35:41.453～20:35:44.269 build0，容量专项至20:35:48.131退出0、nested8/0 parent10/0；全Review至20:36:00.076退出0 main787/0，test90C5…绑定。真实4096旧代ledger新进程新代仍满额，Replay/Conflict优先、TTL只终态清理，不伪SQL/减构造/改timeout；非生产跨介质恢复或mg1管理验收。
- Root解除冻结后00按22.64仅CorePorts写实际additive默认查询与Snapshot.InstanceId，输出SHA256=7D514028526F8A26790ED23714102B0C9E49704D256D6E1ED2E8A3A421935C79、108行、scoped diffcheck0。只是source/行计数，未编译/真实Architecture通过；保旧ctor/二元signatures。02仅Registry独占已通知开始，完成后Root唯一构建。其它六文件/生产来源链未写。

### 22.67 Root Registry两文件slice真实合成测试准入

- Root仅原owned ConfigurationContractTests.cs，inputSHA256=90C5BD2C782720D913E1DE7BF96401435F6B553095491C0888BFFEA2213E4785。允许按刚落地CorePorts签名与02实际Registrysource补真实三元collision/exact adapter-messenger匹配、旧二元ambiguity/唯一兼容、platformNormalize与scope-instance Ordinal、空legacyInstance与AccountDefault不混、默认interface无法枚举messenger时failclosed、Snapshot真实instance/连接facts及duplicate不覆盖负例。预计15+而未运行不能报绿，不新增路径/公共Program/架构阈值。
- Root先查实际source成员再编写，02source完成和00冻结确认后唯一build/Review/defaultArchitecture/Safety，真实Headless重建后legacy S52另串行窗口。00/02不build、互不写对方路径；本slice只Registry，不能借其成功声称整个Policy目录/来源与派发隔离或第二阶段准入。
- Root明确收口本test输入90C5…不变，落现SurfaceAsync，合成endpoint实现IPlatformAdapter/IPlatformMessenger、零IO，18～20为计划：同账号a/b exact、二元ambiguity/missing不fallback、Normalize后adapter/messengerduplicate分别拒、scope/instance大小写Ordinal、旧空instance唯一兼容、snapshotinstance、inputlist/public Adapters不可变、旧registry default exactadapter及messenger多实例/错实例拒/单例兼容、零Send。仅原test不动Program；本条前置登记后准入Root实施。

### 22.68 两文件Registry slice源码就绪/临时写权交回

- 02 20:40:26.814+08报告Registry单文件完成，outputSHA256=F7F83C4D59DD9865D35060EA8EB363BF27C5C20F2135DA57BDC36E0480ED581B、110行、scoped diffcheck0，untracked尾空白直接检查0；其它七文件不写。02主动交回该单路径临时写权，后续冻结不再写、不build；00实际读完整新source与hash复审匹配，接受明确fullKey去重/逐集合冻结/exact查询/旧二元唯一/安全错误固定code，不作调用链验收。
- CorePorts7D514028526F8A26790ED23714102B0C9E49704D256D6E1ED2E8A3A421935C79/108行，00最终合入。两input→output可核、旧快照位置ctor/default二元签名保持，显式无效query过去借default改null属于已批准failclosed收紧；Enabled=true只legacy loaded事实不冒管理desired。scoped diffcheck0，待真正构建/架构棘轮。
- 两source均冻结，Root根据22.67实际test source完成后唯一build/Review/defaultArchitecture/Safety/先真Headless重建后的legacy S52。00/02不得抢test/build或擅改其它六文件；局部索引源码成立仍不证明完整来源/消息路由、C3 Policy或production/Wave准出。回退仅本两路径四改段及依赖测试，保原迁移输入/工作区，禁止全文件restore/reset/clean。

### 22.69 Registry实际20断言/Review807与Architecture、Safety通过

- Root20:42:30.695 Review build退出0（8既有warnings），fullrun至20:42:51.377退出0、807/0，新增20实际Registry断言全部PASS。Architecture20:42:51.378～20:42:53.052 build/default run0、165/0、SQL137、Domain268；Safety20:42:53.052～20:43:08.750 build/run0、594/0。六source哈希窗口未变，test6E815D4768E97649A69F971FC120F25975E44ED8DE2EF7D288B19E68CF275279。
- Root第一次test apply_patch因多余逆序context验证失败未改任何文件，随后最小hunks正确应用，无编造接口。新增20锁定exact/ambiguity/duplicate/Ordinal/immutable/default-interface安全/zeroSend，原assert保留，棘轮未放宽；CoreDomain≤300通过真实defaultProbe，不只108行口头断言。
- 局部Registry实现已由真实HostBuild constructor引用，尚未source Instance调用链采用exact重载，不能称完整隔离/C3 Policy/Phase2。Root继续真Headless Rebuild→旧S52，00shared及Core/Registry仍冻结不build；终态后才可新独立source批。

### 22.70 Registry旧宿主S52通过与alias-1三文件准入（实施前）

- Root20:44:33.260～20:44:38.336 Headless -t:Rebuild退出0（8warnings），S52至20:44:46.438退出0、119/0；scoped diffcheck0、四sourcehash无变，产物SHA256=E331180E79E582780FEDD9876C34D45C03365C59E886702AD629B258C69D48B0。20:46:32工作区HEAD1f3181…、git status -uall370/20暂存删除；该status字符串UTF8/LFjoin SHA256=6DBE8C0E86BD0AE76EC8418BD28B8983BDDA99A0733C015A5A06814EEDF70ACE。旧compat通过不是exact来源派发采用，Source窗口已终态。
- alias-1仅三existing路径：Root src/BotAgent.Storage/ConfigurationStore.cs inputSHA256=A0B8F70EC8B9AB5102AA46667C19F54FA0AB779E5B93C31E78AA95F38479DD92；Root tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputSHA256=6E815D4768E97649A69F971FC120F25975E44ED8DE2EF7D288B19E68CF275279；00 src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs inputSHA256=28874373DF5933DECDE3E95F1D45AD73C3C09BD33BC997FA48E799B940816688。本轮实测匹配、各owner独占，Bridge/Core/Registry/publiccfg/冻结hash不改。
- 新内部codec_revision cfg-0.3.2/alias-1；同TX按request.Key.Generation所存codec选择旧raw冻结/HMAC或新alias canonical Patch/Fields/Modes，不能Own锁外先施新alias_conflict使旧代request失配。旧Strict/Legacy HMAC bytes及golden保真，旧generation Replay按其codec；unknown codec安全failclosed/Find不按最新默认解释，原两SELECT扩codec列不新增SQL。新primary ModelName/BaseUrl采用真实direct Model/Base规范器并归direct，同种同规范意图合并，Set与Clear/继承冲突返回ValidationFailed不是CommitFailed；Canonical fingerprint剔空primaryaliasmarker而Host仍查已登记，不丢非aliasmetadata。
- 00仅Host私有helper允许Key=primary、Name/Priority/BaseUrl/ModelName/Enabled全部Keep、Credential KeepExisting的空marker且同TXprimary确已登记；其余非Keep仍暂adapter_unavailable，marker缺登记整批拒绝。主固定known凭据/原endpoint prior目录守卫仍现有Prepare校验，不新增providerCredentialFacts、不依03未冻结新binding/不假非primary完整。Legacy marker也不得凭body造注册；当前有效旧路保留。
- Root合成旧codec/golden跨代Replay、新单/双alias等价与冲突、primary缺登记拒绝、审计Fields/Modes一致、unknowncodec及原子fault后验收；00只Hostmarker不build，双方source完成后Root唯一串行编译/真实调用/Review/defaultArchitecture。不混Registry/Policy/plugin路径，未全Provider支持/productionDI/HTTP/Wave1准出，回退仅本新codec/helper片段并保旧reader/generation兼容，不混旧binary新DB。

### 22.71 Agent03凭据实际调用链只读评审（facts签名/源码准入待）

- 负责人本次只授权Agent03只读契约评审，03完整补分段总纲/手册/冻结493～497/704～706，无源码/测试动作。读取输入SettingsStore06C9F3EFD333C68CE0050952DB5F91AAE972359213CF5ECE977231F693B55A23（00本marker输出）、CompositionRoot1819F4647F19EB5E1B6344F201AD21235076C64034F1EAECAAC8B3F9EA59C1FC、ModelProviderStore4E36E7B8DD3E698395A3490861BB68F7E9227FA14358B66B94C7FD225E081351。Route函数每次caller settingsBox.Current，startup捕获的是metadata候选/runner/cache，纠正原startup参数误推断。
- 拟可信私有ProviderFacts必须区别main槽metadata：输入同TXfinalDB ModelApiKey无值Configured/KeyUsable、实际Current或ProposedHotSave/Startup main snapshot无值facts、明确phase/KeepReplaceDelete；输出仅Binding/Source/Configured/RouteReady固定enum/bool。精确knownrefs仅secret:apiKey(DB-only)、env:QQCHAT_API_KEY(固定QQCHAT非空白→OPENAI非空白→对应phase mainSnapshot)，其他任意ref Unclassified未读值即deny，无BOTAGENT动态env选择。Keep不换ref，nonprimary不跟primary自动改ref；primaryReplace→DB，Delete保已冻主HotSave回落/规范binding。
- 真实差异不可统一猜first-nonblank：BotConfig.Secret先三个_FILE再三个direct；HotSave Delete用??，空白QQCHAT shadow OPENAI后Trim空，旧provider env resolver跳空白。Configured与RouteReady分离，现TryBuildProviderRoute还拒http(s)形状key；Replace KeyUsable由Root Store对已owned规范值投影bool，callback不得读SensitiveInput/开放secret getter，Keep需复用可信同conn现原语事实，不加猜SQL。
- 目前仅接受评审方向，**准确私有签名/input/write batch未冻结，不新增ctor/callback或全Provider源码**。Host/Model两个OpenAiClient副本三条null→启动cache仍在，需额外精确路径授权后03/00真实拒绝测试；DNS/redirect安全transport也超当前14shared，不暗扩大ModelTransport/HttpFetcher写权。修前production采用受影响停止，facts/metadata局部绿不能证明真实routing安全。

### 22.72 alias专项真实通过与OpenAiClient六处有限写权（仍等窗口）

- Root21:00:56.250～21:00:59.212 Review build0/增量0warning，actualHostprimary-aliases至21:01:00.582退出0、17/0；oldcodecs+unknown至21:01:00.886退出0、9/0。Fields75/Modes继承/单-双alias等参replay/冲突原子/缺登记marker拒/新未知endpoint拒/未知codec Save-Find-claim拒通过。oldprotocolfixture明确模拟留存codec及独立旧字节重建，不冒旧binary回退。Root全Review/defaultArchitecture尚运行，源码冻结，不能以专项代替统一终态。
- Root传达负责人新增精确授权两existing文件**仅六处dynamicResolver存在且返回null不得回缓存**及合成测试：00最终写src/BotAgent.Headless/Services/Agent/OpenAiClient.cs，inputSHA256=3AF3845ED6CBB0F5F3EA82D282935A8EAC19BB73E55DDBE44A8372316A387D87；03临时独占写src/BotAgent.Model/OpenAiClient.cs，inputSHA256=6BEA54EB5C888DBE182901651023230B9D9B5F200402B8AF58958E3996AF4AF4，03完成交回00协调最终审查。两input本轮实测匹配，Root不写这两文件。
- 改段限定两副本原283/371/595每条route选择：仅_providerRouteResolver为null才允许原_providerRoutes缓存；resolver已存在的null即该candidate不可路由，沿现不可用分支，不发该cached route，不新fallback/credential env读取或接口。真实独立3×2条拒绝负例+无resolver旧兼容由Root原ownedtest负责，读取实际成员后前置登记，不改其它transport/Model源码/FQN/Core。没有Wave3整体准入。
- **当前alias全回归窗口未终态，不得开始写**；收到Root解除后00只Headless三处、03只Model三处，同步完成冻结给Root唯一builder。未知codec/alias批与本routing批分开证据，不把六处安全修称全部模型凭据/ProviderFacts或DNS/redirect已完成；回退仅本六处branch，但保已登记安全策略，不能把回缓存恢复当可运行生产回退。

### 22.73 alias统一809/165通过与routing Source窗口释放

- Root21:04:00.335～21:04:13.276完整Review退出0、809/0；Architecture至21:04:14.295退出0、165/0、SQL137、Domain268；scoped diffcheck0，五source哈希不变。Store688794FF0AC5A737B3094A85642D124E5761AF05274009992DDCECC48713D1AA、test4BB0FAD32766BDAAFCBB5137041AFE1A05771CE725A578BD128D0E4A534BC936、Bridge0BE…、Host06C9…/Web005E…；publiccfg-0.3.2/冻结7B7C…未改，internal代选择codec/旧golden/unknown failclosed真正通过。旧codec模拟不是旧binary回退/productionDI采用。
- Root解除alias冻结，00按22.72只HeadlessOpenAiClient三处guard完成，输出SHA256=C98564A9C34DC5FE057A73E8DC9FF9B55EAF909E0C4DB3023EAA0A56445CCDE8，直接检索新guard三处/scoped diffcheck0。03获Root通知仅Model三处同guard source，尚待output/最终交回。双方无build，其他Model/出网transport/fullProvider与Policy仍未准入。
- Root下一ownedtest routing负例输入4BB0…，具体实现前登记为下一独立test批；不可Root源码/publicProgram抢写两OpenAiClient，Source完成后同步冻结唯一builder。六处零cached发送与无resolver兼容证据待真实运行，不能以条件源码存在当行为已验收。

### 22.74 2026-10-14恢复、Model三guard输出及Root routing测试准入

- Root07:52恢复原角色工作，报告HEAD1f3181…/status-uall371/20暂存删除（比前370新增一未跟踪项，未清理），此刻无其他live席位；00原共享席位恢复，不建新角色、不接管Root24source。00重新实测冻结7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203、HEAD完整1f3181e21e8d1f9531e41066b033d8d60218d580、Store688794…/test4BB0…/Settings06C9…全部匹配。
- Model outputSHA256=977EF9E21CE386DA7D6E2CB906F99EFFB389F1BAFE3E87279FFC182D9C1B7D98，与HeadlessC98564A9C34DC5FE057A73E8DC9FF9B55EAF909E0C4DB3023EAA0A56445CCDE8两副本各284/372/596新guard三处实测一致，scoped diffcheck0。03本轮未重新执行/构建，Root转述该单文件source已完成；00接受实际源码复审输出，Model临时源码写权收回00协调队列并冻结，后续改段必须再登记，不捏造新的03测试/交付消息。仍仅六guard，不扩其它Model或transport。
- Root下一仅原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs，inputSHA256=4BB0FAD32766BDAAFCBB5137041AFE1A05771CE725A578BD128D0E4A534BC936实测匹配。按真实两个OpenAiClient×三path（SendThroughProviders/CompleteChat/providerPayload）构造resolver返回null而cache有值的负例，counter必须0；无resolver的原cached兼容正例/合法其他candidate继续可用，fixed安全code/统计不带URL/key值。不造接口，先查ctor/实际http mock原语；existingfactory/Surface入口优先，不新Program或路径。
- 本条前置登记后Root准入编辑该test，双方client/全部shared保持冻结，Root唯一builder。source完成后先短专项真实红绿，再全Review/defaultArchitecture/Safety及先重建Headless的合成IT另窗口；六处zeroSend证明仅本安全门禁，不是fullProvider/Facts/出网DNS/Policy或第二阶段准入。新用户目标持续推进到具备第二阶段条件但不擅自开启，与既有权限一致。

- Agent03随后正式消息交回22.72 Model单文件并冻结：10-13 21:07:11仅三guardsource完成，10-14 07:52:54只读再核977EF9E2…。三条件于内存逆向替换后SHA精确等旧input6BEA54EB…，保所有其它既有输入；pwsh静态核及scoped diffcheck退出0，无build/test。00另对Headless同样内存逆向SHA精确等3AF3845ED6CBB0F5F3EA82D282935A8EAC19BB73E55DDBE44A8372316A387D87、命令退出0，未写文件，锁定恰三改段。两副本临时移交与保旧工作区证据完整；逆回会复活已撤销凭据cache，不能自动作安全production回退。

### 22.75 routing实际测试源码就绪与known-ref下一批窄确认

- Root按22.74仅owned ConfigurationContractTests.cs已落closed --configuration-provider-routing factory；实际两IModelClient/ProviderFailoverRunner×CompleteAsync、CompleteChatAsync、SummarizeSessionTitleAsync(aux)×resolver-denied(cache有值)、legacy无resolver、liveResolver、合法secondary共24实际断言。复用csproj既有extern ModelModule与真实IHttpFetcher/IModelTransport合成计数，无socket；Model仅child日志handler noop防stderr污染。FQN/工程引用未增，尚未运行不能预报24通过。
- Root07:57:42.266开始唯一build+专项session13617仍运行；00两clients/sharedsource冻结不build，只登记。终态后真实哈希/完整回归后置。
- Root窄确认下一独立00原shared CompositionRoot.ResolveProviderSecret闭集仅精确secret:apiKey/env:QQCHAT_API_KEY，其它ref在读取任何值之前null、metadata旧row不删，固定env QQCHAT非空白→OPENAI→当前settings.ApiKey保持。限该函数、不混EnsurePrimary/DNS/transport/完整ProviderFacts。需本routing窗口结束后前置登记input1819F…，Rootownedtest反射真实privateResolve/TryBuild合成mockSecrets noLoad/knownrefs/phase/keyusable边界；此条只是方向确认，当前不得写新source。

- 首实际终态07:57:42.266～07:57:58.021 Review build0（8既有warnings），child至07:58:00.905退出0、24/0。两实际client六path resolver-denied计数0，legacy/live/secondary计数1。Root随后同已准入ownedtest批加强原24断言以排除setup提前失败假零IO：动态resolver确被调1或2、legacy0、精确candidate顺序、main transport计数1/两aux-chat路径http计数1。只补真实调用证据、不减/删原assert或改24数；强化后重新build/专项/全Review/Architecture/Safety待终态，旧24绿不替强化验收。

### 22.76 routing父stderr门禁真实红与synthetic日志fixture收口

- 增强24专项08:00:46.755～08:00:49.781 build0，child至08:00:50.830退出0、24/0；全Review至08:00:55.485退出1、789/1。父要求child stderr空的原门禁命中真实Headless FileLog.Warn两条纯synthetic warning；初判断第二条是fake旧模型结构提示被后续实际定位反证，见下述更正，不是六路径行为断言失败。
- Root只原owned ConfigurationContractTests.cs，实测inputSHA256=C2B5D2C90FB77295A58CAE2570EE6900838CA2261F5AB06C9AA94456E2A78D10；保父stderr空/其它原assert，不改产品/阈值。fake模型输出补合法action=reply减少非本目标旧协议warning；child局部capture Console.Error，新增严格只1条预期固定Provider拒绝reason/hops安全形状断言，任何多/未知stderr仍fail，finally恢复原writer。不显示capture原文/URL/credential，失败仅固定label；新增一assert child24→25、父精确期待25，不把捕获当丢日志或静默换绿。
- 本条准入Root该限定fixture修后重建专项+全Review/defaultArchitecture/Safety，真实结果后置；客户端C985/977/Host06C9/Web005E仍冻结不改，00不build，不拿独立child24绿代父工程红门禁。

- 新真实红08:05 build0/child24/1；只形状临时诊断08:07、08:08均build0/child1，原24路径保持全绿，新增第25日志断言红。Root查真实Host SummarizeSessionTitleAsync854～858：denied Aux返回null触发既有固定标题不改名WARN；实际仅Host path0/mode0的Provider不可用(reason/hops1)及Host path2/mode0的标题不改名两条。准入仅ownedtest收口严格精确有序这两条既有固定日志，任何额外/不同仍fail，finally恢复Console.Error；父stderr空/child25/原24业务assert全保。移除临时case形状诊断，不打印原文/secret/url；fakeaction=reply保持合法合成schema，不能把误判当产品问题。inputSHA256=055AB918DF5AB2FE98432E86D33D6D0A71FDA8E6763181A7E664E2A4E46DD1CE实测匹配，本条确认后再Root编辑。

### 22.77 routing六处真行为25/Review810通过与legacy验证窗口

- Root08:10:42.834～08:10:45.800 Review build0/增量0warning，routing child至08:10:46.555退出0、25/0；全Review至08:10:59.220退出0、810/0。Architecture08:10:59.221～08:11:01.212 build/default run0、165/0、SQL137、Domain268；Safety08:11:01.212～08:11:17.116 build/run0、594/0。五sourcehash无变，testE86B6AC0AC307947F2B5CE93F3E5C46E037C080A1F5E8245C63C03DFB362693B。
- 保原24实际path×mode断言+严格有序两既有固定WARN的1条诊断断言，父stderr空门禁原样，无未知日志静默丢弃。08:10:12 build1是fixture同scope expected变量重名，仅rename expectedDiagnostics，未改产品/接口/安全阈值。resolver确调用/candidate顺序/HTTP-transport分别计数证明非setup假零IO，cached-positive而dynamic-deny真正零发送；无resolver/live/secondary compatibility有一次发送。
- Root下一唯一真Headless Rebuild→合成S21/S36/S52旧宿主链复验，00clients/其他shared继续冻结不build；25纯mock通过不替旧真实HTTP兼容。终态后才前置登记CompositionRoot1819F…闭集单函数+ownedtestE86…批，不混其他Model/transport/ProviderFacts。当前六guards行为验收仅该安全前置，非fullC3生产DI/HTTP或第二阶段准入。

### 22.78 routing旧宿主Rebuild/IT通过与knownrefs真实红前置准入

- Root08:16:31.061～08:16:36.273 Headless -t:Rebuild退出0，Harness Rebuild08:16:36.292～08:16:39.465退出0；s21 08:16:39.469～08:16:47.302退出0/37/0，s36 08:16:47.325～08:16:54.354退出0/98/0，s52 08:16:54.356～08:17:00.933退出0/119/0。先按名字清六类环境、mock/UUID隔离/非external，安全计数输出。四sourceC985/977/Comp1819/testE86无变，Headless artifactSHA256=54EC2A5DE9754B7112B95F469F1333C171C12BA19CD81A89AAE362EF9AD7DBC6。
- HEAD不变，status-uall371/20暂存删除，LFjoin/UTF8状态SHA256=46BB16A133ADAEB7353396A114746EA6F98F8181661A8A87F268DA951178B2DD。只旧真实HTTP兼容与六guards行为，不是新C3管理/阶段二准入；窗口终态。
- 下一两exact路径准入：00 src/BotAgent.Headless/Host/CompositionRoot.cs inputSHA256=1819F4647F19EB5E1B6344F201AD21235076C64034F1EAECAAC8B3F9EA59C1FC；Root tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputSHA256=E86B6AC0AC307947F2B5CE93F3E5C46E037C080A1F5E8245C63C03DFB362693B。本轮实测匹配，Root先添真实private ResolveProviderSecret/TryBuildProviderRoute synthetic mock边界并build专项锁红，**00此刻仍不写函数，Root真实红终态通知后才写**。
- 目标仅精确secret:apiKey/env:QQCHAT_API_KEY，其它ref在任何secret/env读取前null；固定DB-apiKey/QQCHAT非空白→OPENAI→当前settings.ApiKey及HTTPkey RouteReady旧边界保持，unknown旧metadata不删/不重写。Root测试noLoad计数/route不可用+known相同phase，case/前后缀伪装不授任意秘密，不记录输出值。复用现factory/合成子环境/真实ISecretsRepository，不添接口/路径/publicProgram。00后续仅该函数，不混EnsurePrimary/Facts/DNS/transport，两个client六guard不回缓存保持。
- Root获本条登记可立即编辑ownedtest、唯一红验证；初始可能红是目标未实施，不能改fixture变允许。测试完成后00明确小改段/hash交Root统一验证，当前所有其它shared冻结、不build。回退只闭集函数片段且旧任意秘密回读将重引安全风险，不当自动生产回退。

- Root细化本条已准入test：仅E86原ownedfile增closed --configuration-provider-refs及父入口，反射真实CompositionRoot两个private方法，12unknownref→null且genericLoadCalls0（mock任意Load返回synthetic值捕捉越界）；3DB-only missing/blank不env回退；5fixed env/current snapshot；9实际route primary/secondary metadata-current差异/URL形key/空值。29+父1是计划，不预报通过。已查真实ISecretsRepository全方法/ModelTransportContract route/ProviderCircuitBreaker枚举；误猜独立ModelProviderRoute文件只读exit1后实查ModelTransportContract.cs，无source改动。保持unknown无值输出，不造接口，00source继续等待真实红后才写。

### 22.79 knownrefs真实红后单函数source完成

- Root08:19:09.643～08:19:13.051 Review build0/增量0warning，--configuration-provider-refs 08:19:13.055～08:19:14.808退出1、passed0 failed1 InvalidOperationException；首unknown secret:agentServerKey旧StartsWith确读mock非空而触发closeddeny断言，不是setup/编译失败。test输出2A6E64212820BC9B2B35DCCCF53CCE12398A2DA21217F57039A6F10919CF0E6C冻结，Compinput1819F…保持，Root明确解除红窗口准00仅ResolveProviderSecret函数。
- 00已写该函数两Ordinal exactrefs，未知先null且不读任何secret/env；DB固定Load(apiKey)，env固定QQCHAT非空白→OPENAI→当前settings.ApiKey，未删metadata或改其他函数/EnsurePrimary/Facts/transport。CompositionRoot输出SHA256=2552BF1416D8F46F1204BADC1D45318A7D81C6C0247AFC7785AB8ADB5C567639，scoped diffcheck0。
- 00在内存把该唯一method片段逆回原完整片段，全文SHA精确重现1819F4647F19EB5E1B6344F201AD21235076C64034F1EAECAAC8B3F9EA59C1FC，pwsh退出0、未写任何逆回文件，证明外部所有既有改动保留。Source现冻结交Root唯一真实build/专项29/全Review/defaultArchitecture/Safety；00未build，29仍待终态，不凭source称行为通过/全ProviderFacts签名已冻或第二阶段准入。

### 22.80 knownrefs实际29/统一811及架构Safety通过

- Root08:21:32.360～08:21:35.360 Review build0（8既有warnings），provider-refs至08:21:35.461退出0、29/0；全Review至08:21:48.983退出0、811/0（810+父入口1）。Architecture build08:21:49.006～08:21:49.937、run08:21:49.946～08:21:50.695均0、165/0、Domain268；本次tail过滤未保SQL行，精确SQL待Rootdefault重跑，不能借历史137冒本次已捕获。Safety build08:21:50.698～08:21:54.216、run08:21:54.217～08:22:06.470均0、594/0。
- 四hashComp2552…/test2A6E…/clientsC985…与977…不变。原unknown secret读值真红→fixedknownrefs且unknownnoLoad为29真绿，fixedenv当前snapshot与DBonly/actualroute边界保持；既有旧metadata不删。整条新C3生产保存/ProviderFacts/Policy/第二阶段仍未验收。
- Root后续唯一true Headless Rebuild+S21/S36/S52，source保持冻结，00不build。03facts仍只读未签/未写，新增SecretMutation无值facts只最小提案待实际原语与phase冻结；插件两额外路径尚未授权，Agent04只读gates授权请求未答前不得启动/扩写。不得由本批29推断fullProvider/Plugin或出网transport成立。

### 22.81 knownrefs defaultSQL实测及真实重建后旧chain通过

- RootArchitecture default重跑08:22:37.968～08:22:38.693退出0、165/0，当前SQL137/阈137本次实际捕获（补22.80缺行）。Headless Rebuild08:22:38.730～08:22:43.627退出0（8warnings），Harness08:22:43.629～08:22:46.626退出0（2warnings）；s21 08:22:46.628～08:22:54.338退出0/37/0，s36 08:22:54.340～08:23:01.427退出0/98/0，s52 08:23:01.428～08:23:08.013退出0/119/0。
- 四hashComp2552…/test2A6E…/clientsC985…、977…无变，Headless artifact0A37611206EC44DE0DD336F25D5A47A6C809BF539C95E266AEEB619A59F2B100。Root唯一builder且env名字清除/UUIDmock非external边界保持；不是新C3 HTTP/DI/完整Provider或第二阶段准入，source验证窗口终态。

### 22.82 Model-only primary无值facts四文件与真红测试准入（实施前）

- Root src/BotAgent.Storage/ConfigurationStore.cs inputSHA256=688794FF0AC5A737B3094A85642D124E5761AF05274009992DDCECC48713D1AA；Root src/BotAgent.Storage/ConfigurationStorageBridge.cs inputSHA256=0BE0B31044324FE194F759681D72E6540802B871FB28D30D60CA840CE4EB02B5；Root tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputSHA256=2A6E64212820BC9B2B35DCCCF53CCE12398A2DA21217F57039A6F10919CF0E6C；00 src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs inputSHA256=06C9F3EFD333C68CE0050952DB5F91AAE972359213CF5ECE977231F693B55A23。全由00本轮实测原input匹配，四owner独占；无其它source/publiccfg/hash改动。
- 03 final只读确认后冻结准确最小面：existing internal readonly record struct SecretMutation(Slot,Kind)保2arg ctor/FQN，新增init bool FactsKnown/DatabaseConfigured/DatabaseKeyUsable，仅ModelApiKey补。Bridge internal ObserveModelApiKey(SqliteConnection)→同一SecretMutation(ModelApiKey,Keep)安全bits，调用真实SecretsStore.LoadInTransaction(conn,ModelApiKey)，white→null否则Trim，复用现SQL。Root可internal ProjectModelApiKeyMutation(SecretEditKind,string? normalizedOwnedValue)仅Storage消费owned值；Store治理/CAS后、SecretSources前同conn Keep Observe、Replace从owned.NormalizedValue、Deleteknown true/false/false，两个现消费者收到同owned数组。不新Prepare/SecretSources签名或secret value getter，不把其它五slotdefaultUnknown误当未配置。
- 00 nested private readonly record struct PrimaryCredentialFacts(bool Known,bool Configured,bool KeyUsable,CredentialSource Source)；private static ProjectProposedHotSavePrimaryFacts(string finalRef,SecretMutation modelMutation,SecretMetadata proposedMain,AppSettings current)。仅Strict primarySave，现PrimaryProjection先决定Keep/Replace/Delete finalRef再评价；DB-only依据sameconnDBbits，不以mainEnvConfigured反推DB。Source enum真实仅None/Database/Environment，Unknown=Knownfalse/SourceNone，FactsKnownfalse/KeyUsable&&!Configured/非法Sourceenum failclosed。当前snapshot.ApiKey Keep一次，不新ctor/callback/Startup框架。
- 两fixedenv各只观测一次：实际env-ref QQCHAT非空白→OPENAI非空白→ProposedHotSaveMain；main Keep current.ApiKey raw谓词，Replace DBbits，Delete保QQCHAT??OPENAI的空白shadow再Trim。DB/publicLoad与ownedReplace对Trim后谓词；fixedenv/current fallback保现TryBuild先raw.StartsWith http(s)拒后才route.Trim，不悄改前导白URL旧行为/任何其它函数。KeyUsable仅当前key谓词不是实际鉴权/整体RouteReady。未知ref在读取值前Unknown，nonprimary/Read全catalog/MainBinding/Unconfigured/未来Startup投影仍未施工。
- **先仅Roottestsource，Storage/Bridge/00Settings仍冻结不写**：test2A6E增closed --configuration-primary-facts+父入口，actualHost privateparticipant/唯一Store真实publicSave。首Keep DBref/DBmissing但可信mainMetadataEnvConfiguredtrue应整批ValidationFailed且version/audit/DB/runtime不动；再goodDB Keep、URLshapeKeep/Replace原子拒、GoodReplace、Delete finalenv合法、QQblank main shadow与env-refskipwhite差异、Current fallback、ModelOnly安全bits/其它槽Unknown。18～20为计划非通过，不改旧DB-only Token fixture/构造接口。
- Root真实build专项锁红后才开四paths产品source并通知00；Root唯一builder，00不build、不越写Rootfile。最终Read/全Provider/路由热采用与生产保存链仍待后续，不能拿最小primary校验当完整Wave1或第二阶段准入。回退仅本bits/helper/gate改段并保其安全风险说明，现closedrefs/六cacheguard/aliascodec全保。

### 22.83 primaryfacts真实红后四paths source完成冻结

- Root08:30:37.830～08:30:40.251 compile1因fixture猜GeneralChanges，查询真实ConfigurationChanges.cs39后仅纠为BehaviorChanges；08:31:00.569～08:31:03.637 build0/增量0warning，child08:31:03.641～08:31:05.943退出1、passed0/failed1，固定missing_database_not_main_environment：DBref missing/main envmetadata true旧Host错误允许，非setup失败。testA7F385821D37E5FB04BB3CB7C6F9B0A26358142B520172C92B6AFF2912341F8F，原20asserts保留。
- Root明确解除红窗口，owned Store/Bridge仅Modelbits/Observe/project完成，输出Store1F238419E8F56CE88A70AA7EAB0519849396FB6AFC6593F89E594F4FD2E9A703、BridgeEB2D7734B17DBE0E7CE8AFCD985BD57538B231AECFCBBBC8E91B661937C6A260，scoped diffcheck0，无新SQL/Prepare签名/值输出。Root内存逆片段全文hash精确回原688794…/0BE0…、命令exit0，无任何逆写，保所有外部既有改动。
- 00只SettingsStore移除旧mainMetadata.Configured假ready门禁→现PrimaryProjection finalRef之后private Model-only facts严格校验；DB bool/fixedenv raw/HotSave shadow/Current一次/Unknown_knownfalse，ctor/Legacy/nonprimary/Read/TryBuild均未改。输出SHA256=58899E85931AF792F265C90C96B7EC86FBEBB28AD17C7D78B443D1F25F886AF8、1250行/scoped diffcheck0。00内存逆旧3改段全文hash精确回06C9F3EFD333C68CE0050952DB5F91AAE972359213CF5ECE977231F693B55A23、pwsh退出0，未写逆回文件，保Device/alias/Token全部既有改动。
- 四source正式冻结，Root唯一build20专项/统一Review/defaultArchitecture/Safety，00无build；Source存在不预报20绿，也不宣称完整Provider/Startup/Read目录/production采用。异常facts_knownfalse不当Unconfigured，SourceNone是合法enum而Known决定未知，未造public enum成员。

### 22.84 primary真实20/统一812及重建兼容全部通过

- Root Review build08:35:38.803～08:35:45.363退出0（8warnings），primary20专项至08:35:46.981退出0、20/0；full至08:36:01.210退出0、812/0。Architecture build08:36:01.228～08:36:02.173/run08:36:02.174～08:36:02.999均0、165/0、Domain268；Safety build08:36:03.003～08:36:06.609/run08:36:06.610～08:36:18.885均0、594/0。四hashStore1F238…/BridgeEB2…/Settings588…/testA7F385…完全不变。实际publicSave→真实Host私有participant DBmissing/mainEnv错误读真红→原子拒、URL不可用拒与finalenv合法真正通过，不是纯声明。
- defaultArchitecture08:38:06.181～08:38:06.932退出0、165/0、SQL本次精确当前137/阈137。SettingsScope build08:38:06.967～08:38:08.726/run08:38:08.728～08:38:10.119均0、failures0，输出未给pass总计不借旧17；ProductionSpec build08:38:10.122～08:38:11.855/run08:38:11.857～08:38:13.352均0、62/0；Frontend08:38:13.353～08:38:20.887 node0、356/0。
- 真Headless Rebuild08:38:20.892～08:38:25.606退出0（8warnings），Harness08:38:25.607～08:38:28.752退出0（2warnings）；s21 08:38:28.762～08:38:36.468退出0/37/0、s36 08:38:36.469～08:38:43.489退出0/98/0、s52 08:38:43.490～08:38:50.090退出0/119/0。四hash无变，Headlessartifact5A04E2742CDF3773BAC788487DD839202FC3B573EA85255D598C44C3B37C02E1；HEAD/status371/20D/statusSHA46BB…/cfg0.3.2与冻结7B7C…不变。一次只读误猜ProductionSpec无Probe路径exit1后实际Directory更正，native本批全部0，不是产品失败。
- source窗口终态但下一fullBinding未准入。内部model:unconfigured/model:primary仅表达候选未冻结字串，不是已批准新knownrefs。00最新Find建议后明确撤回为不完整原子/应用方案：Find metadata一conn+Secrets.Load另一conn可混版本，且把desiredURL/model提前热用违反Started/发布与nonprimaryRestartRequired。必须先03只读freeze coherent安全gate/read/use及真正consumer batch；保持captured路由metadata，live只能revocation/binding安全gate，不得cached复活或未Model应用偷换endpoint。当前所有source仍冻，无ctor/newbinding/EnsurePrimary/transport改动，不作fullProvider/production/第二阶段宣称。

### 22.85 本批交付checkpoint后03 coherent route只读结论（未源码准入）

- Root确认22.84为本批交付证据入口，goal未完成/第二阶段锁定，本批无更多编辑/构建/提交发布。03只读建议复用真实AppDatabase.Open+BeginTransaction(deferred:true)（Store98范例）、sameconn ModelProviderStore.LoadAllInTransaction与fixed StorageSecrets.LoadInTransaction；current row缺失/disabled/unclassified/Unconfigured或binding≠captured拒路由，nonprimary desiredURL/model≠captured也拒，避免新key送旧目的地。继续capturedURL/model/priority/候选，不freshFind整行偷热/不选另一实例，六guards已防deny后旧cache复活。
- 该方案仍**候选未准入**：FindAsync写floor且依caller/key不可滥作每route只读gate；LatestApply仅private最新receipt聚合非每providerModel。DB readTX一致不等于DB+SettingsBox.Current跨介质一致，Current主snapshot需一次捕获并如实报告阶段，未建立原子bundle不能宣称已建立。
- 现已冻旧known legacy DBref每次读取committed DB值是live事实，可能早于ApplyStep.ModelProjection Started；若保留必须明确是committed credential安全消费，不作ModelProjection Applied。若要求所有key更新也Started之后才可用，需另审runtime epoch/binding-version契约，不能偷偷新增。实际Core ApplyStep.ModelProjection=2（不是误名ModelRuntime）；TryStartApplyStep/RecordApply已存在但现SettingsBox先Exchange再published，并未完成claim→完整routing snapshot构造→单引用交换→真实ledgerResult的应用链。
- 需00/03/06或负责人明确分维度语义/准确consumer batch后冻结sentinel字串与source；Unconfigured不伤共享DBslot、UsePrimary必须live-main不复制env仅表达方向。当前无新增helper/ctor/ref码/metadata/gate source，plugin两个额外paths与sourceInstance消费者扩权尚未批准，Agent04只读授权未答不得开席。局部green不解除全Provider/生产/第二阶段门禁。

### 22.86 2026-10-14恢复与负责人第二阶段/凭据维度裁决

- 00恢复原登记/共享席位，仅只读+本文，不build；实测HEAD1f3181e21e8d1f9531e41066b033d8d60218d580、status-uall371/20暂存删除、LFjoinUTF8状态SHA46BB16A133ADAEB7353396A114746EA6F98F8181661A8A87F268DA951178B2DD原样。五source完整hash仍Store1F238419E8F56CE88A70AA7EAB0519849396FB6AFC6593F89E594F4FD2E9A703、BridgeEB2D7734B17DBE0E7CE8AFCD985BD57538B231AECFCBBBC8E91B661937C6A260、Settings58899E85931AF792F265C90C96B7EC86FBEBB28AD17C7D78B443D1F25F886AF8、Comp2552BF1416D8F46F1204BADC1D45318A7D81C6C0247AFC7785AB8ADB5C567639、testA7F385821D37E5FB04BB3CB7C6F9B0A26358142B520172C92B6AFF2912341F8F，公开cfg0.3.2与7B7C冻结不变。
- 负责人本次明确“第二阶段”指**Wave2A/2B平台与模型迁移**，不是独立Web；目标是完整Wave1验收后具备开启资格，不授权直接启动2A/2B。Web仍依Wave1～5+必选ZBA+安全/资源/回退+明确解锁，不能以此新释义提前打开Web，也不能把全Wave5义务倒塞Wave1。
- 负责人确认保留旧精确secret:apiKey每调用消费已committed DB凭据及立即安全撤销门禁这一实际语义；URL/model/enable/binding配置的明确应用/需重启另维度，不把前者说成ApplyStep.ModelProjection Applied。same-readTX安全观察/保captured路由/拒不匹配是下一实际consumer冻结内容；不是批准freshFind整行热采用或未签sentinel字符串。若以后改成所有key必须Started后才使用，须新明确契约，当前不暗引runtime epoch。
- 00重新按现21.4完整准出条款核范围（当前标题行1849；历史1170行号已漂移），不以局部primary/旧4scenario代替：全部C3分区及允许秘密同TX、CAS/幂等/版本/所有旧writers、首次ChangedCommitted条件发布、Token write-only GET/UI/旧空整批拒与明确Delete、Device稳定key/tombstone与真实hello/任务/control/commit后failclosed、安全Read/rawkey/nameRaw、真实注册测试入口/独立Headless Rebuild/完整适用回归/合成迁移与整套产物+DB回退。当前源码局部通过不满足这整组要求，Wave1仍未验收。

### 22.87 下一Wave1必要消费依赖图（不扩成整后端）

1. **C3全部配置域闭合**：现SettingsStore Prepare/Read仍对Field102/Registered.Policies与Plugins/非primaryProviders拒或未可信投影，不能以primary20充全部分区。完整Registered provider先freeze两个内部binding语义、sameTX最终校验/metadata与真实safeRead，再接真实coherent安全consumer；Policy依02可信declared catalog/purelegacy+typedmeta方案，Core既有DTO/Read seam复用；Plugin依明确的限定持久状态/既有preset状态consumer事实与授权，不能存bool称全部Engine已迁移。Wave3完整Engine生命周期不作为本条偷扩范围。
2. **一条统一writer/版本通路**：CompositionRoot受控初始化/一次Activate→BotConfig恢复用户/秘密→SettingsBox同_gate save基准→真实Store事务→首次ChangedCommitted publish；现生产未Activate，SettingsHotReload75→旧ApplyPersisted/SettingsRepo.Save、WebSettings453/494旧setter内秘密先写、PanelDeploy经同下层，以及direct SettingsStore.Save/Secrets旧wrapper/EnsurePrimary启动覆盖仍须收口到同协调器。先保证旧有效路径，再替换，不直接删旧setter或放行任意JSON。
3. **真实commit/apply/action结果**：SettingsBox83现先Exchange再published；八ApplyStep须复用已落TryStart/RecordApply将claim-before-work与真实结果接入HotReload实际runtime-policy/model/concurrency/context/scheduler，Replay/Find不重做，已commit应用失败不假回滚。TTS文件与Mood独立状态不得冒sameSQLite原子；MoodStore吞错语义整改请求未批准前不写，该兼容动作怎样关联安全结果需精确batch而非整个Wave5 ZBA。
4. **管理替代安全面与设备采用**：oldGET仍回FeishuVerificationToken明文（WebSettings1354）及旧JS回送，write-only metadata/显式ReplaceDelete必须GET/POST/UI同批保持唯一保存通路；TokenSlot5 startup恢复已有synthetic证明仍需生产Bootstrap采用。Device已有同TX计划/gate，AgentBridgeServer hello/selection/worker/每次SendGate后真send与AgentCommandService control必须接持久门禁，NameRaw/显示mask不改key。与Wave2来源envelope/跨实例消息路由完整迁移分开，后者不得伪称已实现也不反塞整个Wave2工作作为Wave1。
5. **准出证据闭合**：完整域+实际Host采用之后最终绑定source/产物指纹、真实工程/逐nativeexit、保既有断言/棘轮全适用回归，旧格式/并发/fault与整套旧artifact+对应合成preimage DB回退（不是codec模拟/新binary恢复）。当前已验证bundle可先按精确资产白名单捕获作未来preimage，未登记不得创建/复制；不读真实data/log、不git archive未知用户文件、不开第二仓库、不构建旧HEAD代替当时已验产物。
- 门禁依赖：完整域→统一writer/启动采用→GET/UI+设备/真实apply消费→最终回归/整套回退→00 Wave1验收核对→负责人明确授权Wave2A/2B。02/03只读/有限前置安全修不是2A/2B整体准入；04只读授权未答前不建席、plugin额外paths和完整SourceInstance消费者未授写。ZBA完整管理端点/MCP SDK互通/整机资源/独立Web属于后续，不用缩小Wave1子集偷换完成，也不用后续全部工作无限扩Wave1。

### 22.88 合成回退baseline bundle捕获前置登记（不是回退验收）

- Root独占原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs，13:14本轮实测inputSHA256=A7F385821D37E5FB04BB3CB7C6F9B0A26358142B520172C92B6AFF2912341F8F。仅既有closed factory新增 --configuration-capture-bundle <repoRoot> <captureRoot> <expectedHeadlessHash> 合成证据入口；无新源码路径/公共Program/业务接口/契约修改，00只本文登记、不build。Root获本条可编辑该测试并独占串行真实Review构建/捕获与Headless -t:Rebuild；所有shared及Storage产品源码继续冻结，不并行build。
- 唯一证据根 %WORKDIR%\bot-agent\.git\agent01-wave1-rollback-b3a84e32821f442cb72d0d7aee14d670，Root独占，13:14实测不存在；本次仅其 bundle 子目录资产与安全manifest。不得创建第二工作仓库、复制真实DB/data/log/env/config或全目录archive。未来preimage.db只准现合成AppDatabase fixture与真实backup primitive产生，须下一明确登记后再创建，本条不准复制任何已有用户DB。
- 00实读当前Headless.deps：12 managed runtime资产（含Headless/Core/Platforms/Storage）+22 declared native runtimeTargets+deps/runtimeconfig/apphost三项=37精确白名单；managed按真实输出leaf映射、native保持deps相对路径，需集合去重/实际存在/路径约束验证后才复制。拒absolute/traversal/重名/额外文件；source真实路径严格在现 src/BotAgent.Headless/bin/Release/net8.0，target严格在该nonce bundle，不跟随越界reparse/symlink。不收PDB、任意JSON、用户文件、日志或未声明资产；manifest只相对路径/SHA/sourceREV/冻结contract/captureUTC等安全形状，不写环境值。
- 本轮当前Headless SHA256=5A04E2742CDF3773BAC788487DD839202FC3B573EA85255D598C44C3B37C02E1，deps=C5D165910A5ACA35AEB131848C24AA13C5F24FEF40FDF8E1F79DE3C24D5209F8、runtimeconfig=95505548C838F72A6A829FD5332DBC674C715CE26A216E9EB32BEC8B5821EAE2、apphost=0F4200FFE028C384C2B3FAC39015DD3DC5D136DCD550497418F2E9BC4B0270C6。先记录37asset逐项hash与五source指纹，再真实Headless Rebuild；期望Headless及所有来源asset前后不变，变动即停止绑定旧22.84已验baseline，说明并重新准入/验证，不能悄以新产物替旧。复制后逐项hash/完整集合核验，拒未知已有内容，不覆盖非本任务资产；记录命令/时间/exit及manifesthash，无失败自动清理。
- 保存该baseline直到完整Wave1验收或负责人明确批准证据退役，不自动删除。后续旧bundle+对应合成preimage进程运行/比较才可证明整套回退；本批capture成功最多证明资产取证，不证明新宿主采用、旧binary兼容新schema、生产回退、Wave1或2A/2B准入。framework-dependent .NET8运行时/OS记录为外部环境，不假称bundle包含整机回退。

### 22.89 Mood单路径追加授权事实（待精确batch，不准此刻写源码）

- Root转达负责人已明确新增 src/BotAgent.Storage/MoodStore.cs 可观察持久结果限定整改及合成验证授权，扩展原八Store仅查询复用限制中该单路径，不扩其余七Store语义或其它源码。本轮实测inputSHA256=6918F5C076501E9A49692EB1BF3BD83C502AE47406B67DB967E08236A7719B24；后续Root原Storageowner实施，00只登记/共享最终消费。本条仅授权事实/候选界限，准确成员/测试/改段另登记后才source，现捕获窗口继续全source冻结。
- 冻结候选719明确 LegacySaveOutcome(ConfigurationResult,LegacyActionResults)：Mood独立namespace key/NotStarted/Applied/Failed/Unknown，配置NoChanges不绕动作去重；旧SetText false混合invalid/nochange，SaveLocked吞持久异常，true不证明persist成功。不得改旧bool含义换Applied，不把Mood和configurationVersion绑定或声称一次SQLite回滚，不由无异常推成功。
- 已核实际MoodStore133～153先clean/相等判false、先更新内存再SaveLocked；SaveLocked250～270同Write写text及保现poke列表后catch。候选最小整改是可观察同conn文本原语+确认commit后的projection seam，保持SetText/Reset/RecordPoke/Ttl旧签名、规范意图及poke历史，复用真实完整SQL不加扫描逃逸。完整独立receipt/dedup、HTTP组合真实结果另批冻结，不先造public接口或把Mood偷装八ApplyStep。
- 00一次只读rg对ReplyPipeline*直接文件wildcard触发exit2（Windows路径非法），随后改真实目录+glob检索核消费，无写/构建，不算产品失败。本轮读取仅源码/冻结条款，不读真实心情或数据库。

### 22.90 02完整Policy只读报告接收（候选，不授源写）

- 原02席位13:07交回完整C3配置域只读方案，无写/build/test；四输入00再核仍PlatformOptions90E7A6AE0D94A93F5B7B6BBA457710A6B4BBCAE2E1A63315DACBB89C342D57AB、Resolver551AC5E975F7E6A7542769B3330F29E6DFB478F385F51F93F93ED2BE32490588、SwitchB3C9565C5C3848CEC9F77DBB67EBEDAFAEBD55446091F4C58ACE0914CD0E8BDE、Core旧PolicyC8918359BE39502DF1C2095FAF58E74FE783120483B507CBAD0140735AC365B6。Registry/CorePorts现三元局部输出不再修改，02暂时写权已交回。本条接收报告而非开放八文件源码，保持capture冻结。
- 最小可信配置域方向：复用当前sameconn GetMetaInTransaction/SetMetaInTransaction及既有typed规则，单versioned typed-policy真源（键名/revision待准确冻结）；存在且empty也权威，Clear不能旧DTO复活。保原PlatformPolicies JSON类型作兼容，不把typedTarget塞旧反序列化DTO；Options只承载JsonIgnore immutable runtime/catalog；Switch集中pure逐字段Instance→AccountDefault→四canonical scalarbaseline，Features逐项、Explicit空/AllowAll不混；旧local union与未消费wildcard、不分类名单、schema交集/覆盖及旧IgnoreCase歧义保真实兼容/拒审。
- Host可信声明目录须含qq.private/legacy空instance、qq.official/legacy空instance、local/legacy空instance与feishu/default/feishu-main，在disabled/credential/notloaded过滤前登记，不从loadedRegistry或请求policy自授注册。sameTX初始化/整表或稀疏保存、安全Read复用当前PolicyViews seam；Read不迁移、NoChanges不写格式/marker。未来准确payload/纯函数/目录signature与改段先Root/00冻结再认领。
- 上述可形成完整C3 Policy配置域，不等于现源message/工具/dispatch采用exact来源；ResolverForChannel及_plain/_source→ChannelRouter仍旧链。完整来源envelope/后台并发来源/媒体动作隔离属明确后续Wave2路径与权限，不因八文件小改宣称隔离成立；已有受授权Whitelist/Feishu门禁局部消费亦需后续精确batch/真实测试，不以目录/DTO存在替验收。

### 22.91 rollback baseline capture真实终态（仅取证）

- Root报告2026-10-14 +08：Headless -t:Rebuild13:19:53.621～13:20:11.696退出0（8既有warnings）；Review build13:20:11.730～13:20:16.605退出0（增量0warning，不宣称既有warning修复）；capture13:20:16.840～13:20:22.304退出0、41/0，资产37。37asset重建前后及复制后全部hash一致，含22.84已验Headless5A04…，五产品source重建前后无变；仅ownedtest新增capturefactory，testoutputD4CD1561006E2C91A15CA8A91F337CD2C75FC7644A171B7013CB47116C3DFA89。
- nonce根仍22.88唯一 .git/agent01-wave1-rollback-b3a84e32821f442cb72d0d7aee14d670，manifestSHA256=3FC9BC4DC7E8B91BAAFFC1604B0E8B8A47E51F62003720B28494F02E37279542；00后置只读独立核test及manifest完整hash匹配，未读真实数据/秘密，未运行build/capture。资产完整集合/reparse/traversal边界依本批41assert，不把仅manifesthash当执行证据。
- Preimage=false、RollbackVerified=false；不自动删baseline，尚未任何旧bundle+对应合成DB运行比较。捕获结果只说明已验产物证据保全，非生产回退/旧binary理解新schema/fullC3采用/Wave1验收。Root可串行完整Review验证新factory无业务回归；00共享source继续冻结，仅readonly设计下一Provider origin/binding批，待Root具体Read-origin方案及准确input冻结后才source。

### 22.92 capture factory完整回归与Provider业务收口

- Root报告13:30:41.784～13:30:56.164 Review退出0、812/0；Architecture13:30:56.195～13:30:57.121退出0、165/0、SQL137、Domain268；testD4CD…与Headless5A04…保持，scoped diffcheck0。仅新合成证据factory回归，不是新的产品功能验收，37资产baseline及manifest3FC9…保留。
- Root确认03/00本批准确语义：Ordinal model:primary为LIVE单次Current主凭据，不复制env；model:unconfigured永deny、不Delete共享槽；两旧ref兼容不改。所有final-enabled目录检查known/Configured/KeyUsable（不只edited对象）；disabled旧Unclassified且Keep原ref可保内部事实但不读值/路由，Read标unclassified/legacy，显式UsePrimary/Unconfigured可以修复。UseRegisteredModelCredential仍Unsupported。primary Priority/Enabled继续ReadOnly；Name可Clear空；nonprimaryURL/model Clear按required拒绝；bool/int Clear沿现Store field_cannot_clear，不造默认或priority范围。
- captured或desired任一false不可路由；nonprimary desired URL/model/binding变化拒绝等待restart，不freshFind热采用；primary已有Current URL/model选择兼容保留，不假称metadata已Applied。负责人批准的committedDB key实时消费单独报告。Read origin不能由row存在推UserConfiguration；精确v1模式方案及activation writer fence纳入下条完整批，不以仅gate小修充全Provider。

### 22.93 完整Registered Provider六路径实施前准入（先真实红）

- 13:37 +08本轮00实测六input：Root src/BotAgent.Storage/ConfigurationStore.cs=1F238419E8F56CE88A70AA7EAB0519849396FB6AFC6593F89E594F4FD2E9A703；Root src/BotAgent.Storage/ConfigurationStorageBridge.cs=EB2D7734B17DBE0E7CE8AFCD985BD57538B231AECFCBBBC8E91B661937C6A260；00 src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs=58899E85931AF792F265C90C96B7EC86FBEBB28AD17C7D78B443D1F25F886AF8；00 src/BotAgent.Headless/Adapters/Persistence/ModelProviderStore.cs=4E36E7B8DD3E698395A3490861BB68F7E9227FA14358B66B94C7FD225E081351；00 src/BotAgent.Headless/Host/CompositionRoot.cs=2552BF1416D8F46F1204BADC1D45318A7D81C6C0247AFC7785AB8ADB5C567639；Root tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs=D4CD1561006E2C91A15CA8A91F337CD2C75FC7644A171B7013CB47116C3DFA89。五产品+一原ownedtest，无新增文件/Core公共shape/契约hash/工程依赖/数值棘轮改动；Root两产品与test独占，00三shared最终写。
- 先Root仅test新增真实actualHost同TX保存/目录Read、实际runtime gate、startup-existing不覆盖/publicwriter/fence负例及闭合factory，锁定目前缺少合法Provider支持的真实红。此刻产品源全部冻结，Root唯一build；Root明确红终态后才同批分别写各自产品文件，不让reflection缺成员或compile失败冒缺功能真红。计数待实测，不预报通过。既有adapter/primary/alias/circuit/旧HTTP兼容断言保留；仅有明确功能升级时改变旧临时adapter_unavailable正例输入为永久非法对象并额外证明合法输入已支持。
- Root私有fence：Store新增internal static bool RequiresCoordinatedConfigurationWrites，先Volatile.Read(activationInProgress)再Volatile.Read(_active)；Activate已有try在进入唯一AppDatabase.Write前设inProgress=1，finally既ZeroMemory(key)又复位0，_active仅真实commit返回后赋值；已激活幂等分支保留。Bridge只同名转发bool，无IO/新writer/public接口。00 publicUpsert在当前Write内部先检查，true抛固定configuration_writer_required；不拿ActivationGate，避免与唯一DBwriter反序死锁。若读false先占DBwriter，则写先于Activate事务；若Activate已commit必有active，不能趁赋值窗口绕过。
- ModelProviderStore在原文件加入精确固定binding常量model:primary/model:unconfigured，分类两旧ref为主模型用途、新LIVE与Unconfigured及其它Unclassified，禁止动态环境/秘密名。Prepare现LoadAllInTransaction修改前目录、全部对象同TX稀疏合并，Name/Priority/Enabled/model/url/binding最终事实逐对象校验；metadata只写实际变化项且circuit不改，origin变化也算Changed。新目的地冷暖均使用prior目录+现完整URI/SafeUrl，不允许同批ProviderURL自授权；未变旧private保兼容，不冒DNS/redirect已验。
- Host helper准确private tuple PrepareRegisteredProviders(conn,prior,edits,primaryProjection,current,modelMutation,proposedMain)返回MetadataWrites:ImmutableArray<ModelProviderDefinition>、Errors:ImmutableArray<FieldError>、FieldModes:ImmutableDictionary<string,int>、FieldModesChanged:bool。current在Prepare只捕获一次；ObserveProposedHotSaveProviderFacts(modelMutation,proposedMain,current)一次返回仅bool/source的DB/LegacyEnv/LivePrimary三facts，fixedQQCHAT/OPENAI各观察一次，保持raw-env/current谓词及Delete ??空白shadow；旧privatePrimaryhelper签名可同义委托，不泄值/造Startup框架。只当前真实需key能力例外参数保持旧primaryRequiresCredential语义，生产两transport需key不变。
- origin唯一key configuration_provider_field_modes，closed v1结构精确 {version:1,entries:[{key:<exactID>,explicitFields:<bits>}]}；bits Name=1/BaseUrl=2/ModelName=4，mask仅0..7，exactOrdinal key唯一，root/entry未知或重复属性、非法类型/未来revision安全拒绝。ModelProviderStore internal LoadFieldModesInTransaction(conn)及WriteFieldModesInTransaction(conn,ImmutableDictionary<string,int>)复用现GetMeta/SetMeta完整SQL；不新schemaDDL/SQL/writer。无meta/entry字段按LegacyCompatibility+Explicit旧值事实，不称新User；明确Set/Clear合并bits，mode变化也是Changed并同TX写，Keep/NoChanges/Read不隐迁移或格式写。primary Model/Base用原direct75/76 user-mode真源，不用provider模式重复造来源。
- Prepare/Read捕获固定origin JsonException→LegacyNeedsReview（保真实Read version/generation，不隐修）；DBIO失败沿真实StorageUnavailable/CommitFailed，不混格式与storage失败。public Upsert在激活前原wrapper如真变化对应Name/Base/Model，则同TX只清对应origin bits，未变字段不抹、等值无新mode写；激活中/后禁绕统一协调器。源码callers已核仅CompositionRoot105 EnsurePrimary、wrapper本身、SettingsStore813同TXprimitive，其它为合成tests。EnsurePrimary保持public签名，唯一Write同conn查existing，存在不覆用户metadata/binding/circuit，缺失才原upsert且不造user mode；其受控Bootstrap最终进入Initialize尚待统一启动批，不宣称本wrapper已完整初始化消费。
- internal static ProjectRegisteredProviderViews(conn,desired,captured,kind)复用ProviderConfigurationView与ReadField：Name受授权NameEditable+masked Display，Overview Hidden不持raw；nonprimaryURL/model按实际模式投影，不回ref/secret字串。Credential仅safe enum；缺captured证据不报Applied，变化实际RestartRequired；primary在最终ProjectConfigurationView后以view.Model.Model及ModelBaseUrl两个现ReadField作with替换，避免另读Current/假User来源。保现registeredRead callback/7参数ctor，Read codec失败整体safe拒，不把callback声明本身当生产采用。
- CompositionRoot原启动routes与dynamicResolver采用唯一private TryBuildCurrentProviderRoute(PersistedModelProvider captured,AppSettings current)：captured enabled检查→existing Open+BeginTransaction(deferred:true)→同conn LoadAllInTransaction及固定StorageSecrets.LoadInTransaction(ModelApiKey)，latest缺失/disabled/Unclassified/Unconfigured/binding不匹配拒；nonprimary URL/model不匹配拒，primary既有Current选择保持。DB异常failclosed且两client六guard不回cache。private ResolveProviderSecretInTransaction(knownRef,current,connection)只可信row固定ref；原ResolveProviderSecret/启动route同步识别两新增binding，未知先deny后读值，不修改transport/其它Model。newbinding/URL/model/enable未真实apply仅RestartRequired；legacy同refDB key按负责人裁决实时读commit而非ModelProjection Applied。
- 合成回归目标：实际publicSave全部provider末段invalid/fault原子拒、secret/mode/metadata/version/audit/ledgerrollback、等值显式modeChanged/NoChanges/replay、disabledUnconfigured保slot、unknownrefsnoLoad、priorURL授权与self-registration拒、safeRead无raw/两主fields模式、corrupt/futurecodec不隐修、existingEnsurePrimary不覆、activating/activepublicwriter拒及失败激活fence恢复、sameReadTX gate不混metadata/key与captured事实/否决无cache、恢复mode/binding/circuit与并发。Root最终真实Review/defaultArchitecture/Safety/适用Probe/真HeadlessRebuild后旧IT逐exit/sourcehash；00不build。不包含Policy/Plugin/credentialregistry/DNS或完整Model热apply，不作生产新C3DI/HTTP/Wave1或Wave2宣称；回退仅本批精确片段且需保持原alias/事实/guards，不删除22.88已保baseline。

### 22.94 完整Provider真实红后产品source完成冻结（尚未验证）

- Root首次fixture13:48:32～13:48:39 compile1误用EnableAgentMask/AppSettings with，查真实AgentMaskSensitive及class后只纠test，不算功能红。13:48:56.920～13:49:00.512 Review build0/增量0warning；actual Save child13:49:00.516～13:49:01.932退出1、passed0 failed1固定registered_name_save_supported，合法已登记secondary Name仍旧unsupported；真实功能红而非reflection缺成员。Root明确释放产品窗口，仅各owner写22.93路径。
- Root报告fence两owned source输出Store17FAFD207E5BFB827E1E91A26D615BE1692B96AAE173B41BC70FD3E27AAFD9E3、Bridge4AF961649D2404661DE814011648DFFBC80502B5653AD6FB4C39ACDC64FC03CC，scoped diff0；在内存仅逆本fence片段全文hash精确回22.93原1F238/EB2，不回写。Root继续原ownedtest两closed factories，已确认sameconn callback与outer SettingsStore static真实helper；四激活后publicUpsert仅synthetic setup改用已有sameTXprimitive反射，保旧全部行为assert，新真实publicwriter拒绝assert不放松。Mood仅提案无source。
- 00三shared13:57source完成并冻结：SettingsStoreSHA256=D3DCC93AB72CA9E3254C3F06BA5630C0D5C6890FAC48292C76726963EE15F009（1407行）；ModelProviderStoreSHA256=47306880347919572B1C709BA5FC59D06ADE605516810EE2E2533CE4AFDA3E14（295行）；CompositionRootSHA256=A9E5D61BECC4A178F7DD300DADA34B3BC6F63E6BD26FD25495139D680C5A6BB8（730行）。pwsh逐sourcehash/linecount与scoped git diff --check退出0（已有CRLF提示，不是代码失败），00未build/test。shared采用完整Prepare tuple/modes事实、全final-enabled与legacy例外、同TXmetadata/origin与可信Read；private一次三bindingfacts、公开wrapper fence及missingonlyEnsurePrimary落源码，尚不凭存在称已编译/行为通过。
- ProjectRegisteredProviderViews准确放外层SettingsStore internal static(conn,desired,captured,kind)，不新ctor；primary两ReadField最终with复用view.Model。Root补安全要求后同scope校验legacy userinfo：stored/effective主ModelBaseUrl及Override、目录desired/capturedURL存在userinfo时Read整体LegacyNeedsReview，保header、无viewraw，不能maskDisplay后仍回凭据。Strict最终目录同时校验http(s)/userinfo合法；旧同目的private兼容与Legacy保存规则不变。注册凭据registry未有，C3真实ResultCode无Unsupported成员，固定credential_registry_unavailable校验拒而非造enum或假支持。
- actual runtime closure已调用TryBuildCurrentProviderRoute，sameReadTX固定DBkey+desired gate与captured路由维度；原启动/Resolve同步识别新两literal，unknownref不读。旧模型transport/DNS/redirect未改，主Current兼容不冒metadata热Applied。全部source现交Root唯一真实build/专项/全Review/Architecture等，未知编译或新红先归因再登记修，不放宽原断言/SQL137/Domain300。保baseline37资产及manifest，不把产品变更当已完成回退或Wave1/2A/2B准出。

### 22.95 Provider57/fence11实际专项通过（全回归待执行）

- Root首产品build13:58:06.807～13:58:12.974退出0（8既有warnings）；Provider child13:58:12.979～13:58:14.491退出1，先passed41后provider_fault_rolls_back_all_participants失败。初“meta已bits7故不写”的定位仅假设，后实际诊断真实fixtureURL旧secondary已不在prior授权目录→ValidationFailed/unchangedTrue，根本未进入注入fault；不当产品漏回滚或靠额外写meta换绿。
- 仅ownedtest修为current已登记URL并显式Set此前未tracked legacy Model字段等值以形成真实origin变化，保全部五触发故障/用户秘密版本审计回执断言。writer首次7/1是Task.Run在Initialize内继承WriteDepth AsyncLocal，造成测试成嵌套writer；改在writer外创建Task、event接入激活阶段，原真实外部writer拒绝断言保留，不改产品锁/反而放开嵌套Write。
- Root14:00:27.833～14:00:31.128 Review build退出0（增量0warning，不宣称既有warning消失）；14:00:31.130～14:00:32.782 actualProvider退出0、57/0；14:00:32.782～14:00:33.023 fence退出0、11/0。五trigger实际CommitFailed/unchangedTrue，metadata/modes/secrets/version/audit/receipt无部分提交；实际Save/Read/coherent gate/CAS/origin及before/activating/activewriter通过，不靠syntheticfakeparticipant替Host。
- 当前三shared与Root两产品保持22.94冻结未改；Root只原ownedtest父ModuleRecovery添加两closedfactory精确57/11注册，再唯一全Review/defaultArchitecture/Safety。03可按原许可只读审新source，未赋新源码写权。专项通过不是尚未跑的全回归、最终统一生产writer/HTTP采用/Model热apply或Wave1验收，baseline/preimage回退亦未验。

### 22.96 完整Provider回归终态与URI静态门禁最小后继准入

- Root完整Review14:01:18.716～14:01:34.449退出0、814/0，新父两factory57/11纳入且旧primary20/alias17/refs29/host23等保绿，testoutput34B8DE945BB43DAEAEFB071862D8BE85F9919749F38D8EB4B6C8ED27DD42A788。14:02:02～14:02:27四真实probe各build/run退出0，Safety594/0、Scope failures0、Production62/0；Architecture实际SQL137、Domain≤300通过（本消息未给精确passedsummary/linecount，不借历史165充本次捕获）。Frontend14:02:59.796～14:03:07.352退出0、356/0。
- 真Headless -t:Rebuild→Harness -t:Rebuild→mock/隔离S21/S36/S52逐nativeexit0，脚本至14:03:36.676退出0，S52实119/0；部分中段stdout工具截断，本条不捏细分时间/S21或S36本次计数，后续原始安全汇总可补。五产品sourceD3/473/A9/17FA/4AF保持，旧baseline37及manifest不换为新产物。当前窗口结束；本批完整typedProvider同TX/Read/coherentgate与旧宿主兼容有行为证据，生产统一Activate/新HTTP/publish八steps尚未采用，不把它说成Wave1准出。
- 00静态发现安全边界：Read/Strict保存已拒userinfo，但旧startup/runtime BuildProviderRoute仍可能构造legacy含userinfo目的地。Root明确批准原Comp同scope最小收口：选择primary Current或nonprimary captured effective baseUrl后静态HTTP(S)/长度≤2048/绝对URI/UserInfo==0，再沿原Trim输出；不改变旧unchanged私网兼容，不新增域名/query/fragment白名单，不冒DNS/redirect已覆盖。
- 后继仅00 src/BotAgent.Headless/Host/CompositionRoot.cs，14:05实测inputA9E5D61BECC4A178F7DD300DADA34B3BC6F63E6BD26FD25495139D680C5A6BB8；Root原ownedtest实测input34B8DE945BB43DAEAEFB071862D8BE85F9919749F38D8EB4B6C8ED27DD42A788。Root可先添加actual startup/gate userinfo与invalidscheme真实负例（非fakeIO或反射缺成员）并唯一build锁红；00暂不写该段，Root红终态通知后才最小补guard，其它产品source保持冻结。保原sameTX/credential/rawtrim/privatecompat与57/11全部assert，未授权额外transport/source路径。

### 22.97 03发现本批Legacy typed Provider遗漏（先真红，责任不归旧fixture）

- 03只读五input匹配，指出新RejectUnimplementedObjects删除所有Provider分支而PrepareRegisteredProviders只Strict，Legacy分支只primaryProjection/modes，内部SaveLegacyAsync接受typed Registered Provider意图后静默忽略，可能提交其它字段；原primary全Keep marker缺登记拒绝亦丢失。根因属于本批00实现遗漏，不能因旧HTTP parser不产生此对象或专项57绿就免责；不作为生产使用旧API输入的已验证漏洞泛化，先实际桥真红证明。
- Root原ownedtest可追加两实际Legacy桥整批负例：nonprimary/未知Provider+Behavior必须整批拒，missing-primary全Keep marker须registration_required。既有NoCAS、原HMAC与旧primary Delete允许正例全部保留；不把Legacy升级Strict最终key/CAS行为。
- 待修仅00 SettingsStore，input仍D3DCC93AB72CA9E3254C3F06BA5630C0D5C6890FAC48292C76726963EE15F009，准确段在现Prepare按profile.Legacy显式检查每Registered Provider：仅原IsPrimaryAliasMarker允许且必须priorprimary已登记，所有非marker安全adapter_unavailable拒绝；marker无注册固定registration_required。unknownmarker不默认另一对象。Root真实红终态前不写该段，Comp URI同样继续等待22.96负例真红，无其它源/契约/SQL改动；整批重新回归前不宣称完整Provider准出。

### 22.98 Legacy/URI两真实红后最小修准入

- Root14:05:44.914～14:05:48.774 build0，Provider expanded child14:05:48.778～14:05:50.916退出1、passed56后legacy_registered_intent_not_silently_ignored：未知Provider+Behavior实际被忽略，确认本批00遗漏。14:06:46.127～14:06:49.530 build0，actual startuprefs14:06:49.534～14:06:50.724退出1、passed29后userinfo拒绝assert，旧29保绿而真实route仍非null；不是setup/反射问题。
- Root明确释放两source，仅00 SettingsStore现Prepare Legacy对象检查、CompositionRoot现BuildProviderRoute选择effective baseUrl后静态URIguard。00写前再次核inputSettingsD3DCC93AB72CA9E3254C3F06BA5630C0D5C6890FAC48292C76726963EE15F009及CompA9E5D61BECC4A178F7DD300DADA34B3BC6F63E6BD26FD25495139D680C5A6BB8匹配；对应22.97与22.96精确改段准入，无其它源/契约/SQL/旧Legacy Delete及CAS变更。
- Root保所有原assert，追加missing-primary marker、实际Provider匹配userinfo runtime拒等，Provider预计62/refs34仅计划计数，须真实终态后记录。00仅最小两段后hash交回冻结，不build；再Root专项及全回归，未通过前不以既有814绿掩新红。

- 00于14:07:41～14:07:42仅上述两段完成，Settings输出12FEF7A7BD073840E442AA8B59DB436912288DFA716E6EB400D5046744DFC688、Comp输出6B40AFFB4B2BA84ED4764A672336F54B165709805CC8EB4016A979E47C1B5C4F；pwsh hash/scoped diffcheck退出0（已有CRLF提示）。随后内存逆两exact段全文SHA精确重现原D3D…与A9E…，命令退出0，无逆写/覆原文件；其余既有模式/metadata/路由/秘密及工作区改动原样。正式冻结交Root唯一重建专项及回归，00无build，尚不预报62/34绿。

### 22.99 Legacy/URI修复专项真实通过（全回归仍等待）

- Root14:08:23.070～14:08:25.712 Review build退出0（8既有warnings）；Provider62/0退出0至14:08:26.386，覆盖实际Legacy非marker拒/缺登记marker拒、matching runtime userinfo与startup URI；refs34/0退出0至14:08:26.456（旧29+新增5，原断言全保）；fence11/0至14:08:26.703、primaryfacts20/0至14:08:27.114、HostAdapter23/0至14:08:27.625，均exit0。
- Root只原ownedtest父汇总期望57→62、29→34对应新增assert，未删/放宽；真实红Legacy56后/refs29后→源最小两段→专项62/34通过，是产品遗漏修复不是fixture掩红。其它14:00五trigger/CAS/codec/同TXorigin/安全门禁原assert持续通过。
- Root唯一整套Review/Architecture/Safety/Scope/Production session90157仍运行，未拿专项冒其终态；全source12FE/473/6B40/17FA/4AF冻结、SQL137阈不变，00未build/不编辑。Mood可观察持久seam准确batch仍候选，未写且不混本批，Wave1完整Host/HTTP/回退门禁仍未闭合。

### 22.100 完整Provider含两修复最终统一验证终态

- Root每build/run原生exit0：Review14:08:47.890～14:09:03.427、814/0；Architecture至14:09:05.070、165/0、SQL137；Safety至14:09:20.695、594/0；Scope至14:09:23.839、failures0；Production至14:09:27.900、62/0。前段消息仅各终点，未冒起点/分段build时间，均真工程/非历史成绩。
- Frontend14:10:45.139～14:10:52.760 exit0、356/0；Headless trueRebuild14:10:52.762～14:10:57.312 exit0（8warnings）、Harness trueRebuild至14:11:00.408 exit0（2warnings）；S21至14:11:08.129 exit0/37/0、S36至14:11:15.193 exit0/98/0、S52至14:11:21.750 exit0/119/0，均隔离mock/非external且逐nativeexit核对。
- HeadlessartifactSHA10691D63A0ABB387EE033F5D782763FBB12822B130CAE32DE5CF0D137F32118C；testAD9307B00E9D4A339B04E06B78574D4A5A0BFB761E3627257F0344BB6A4837DE；五产品17FA/4AF/12FE/473/6B40保持，旧37assetbaseline与manifest3FC9…保留未替换。不拿新binary/newDB恢复冒旧artifact回退，不把完整Provider源码/真实局部消费者/legacyIT通过冒生产C3DI/HTTP或全Wave1准出。03只读发现的Legacy遗漏与URI缺口已有真红→仅两source片段→62/34专项及全回归证明，未放宽棘轮/原断言。

### 22.101 Mood可观察独立持久结果两原owner路径准入（先真红）

- 00本轮再核Root独占src/BotAgent.Storage/MoodStore.cs input6918F5C076501E9A49692EB1BF3BD83C502AE47406B67DB967E08236A7719B24、Root原ownedtests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputAD9307B00E9D4A339B04E06B78574D4A5A0BFB761E3627257F0344BB6A4837DE匹配。依据22.89负责人单路径限定语义整改许可，不扩其余七Store/Headless/Core/公开接口；所有Provider/其它shared继续冻结。Root先仅test真实故障不publish/旧bool不能冒Applied等锁红，真实终态后才只Mood源码，Root唯一builder，00仅登记/不build。不能用反射缺新成员或编译失败冒目标真红。
- 同原MoodStore文件新增internal nested enum MoodTextWriteState {Rejected=0,Unchanged=1,Committed=2,Failed=3,Unknown=4}、internal readonly record struct MoodTextWriteResult(MoodTextWriteState State,bool IntentChanged)，ToString固定安全类型摘要无text/time/错误正文。internal SetTextObserved(string?,DateTimeOffset)、ResetObserved(DateTimeOffset)返回此结果；不是Core/C3公共shape，不装八ApplyStep或configVersion。
- SetText清洗/24字限制/相等不写不刷时间保原。旧public SetText委托Observed后仍return IntentChanged：非法/相等false，accepted-changed即使Failed/Unknown仍true，与旧bool含义兼容，但不作Applied证据。Reset void委托Observed，仍alwaysrefresh/write，不造same-now NoChanges；不清戳列表。只有真实独立AppDatabase.Write正常返回才更新Text/UpdatedAt；callback内故障Failed，callback完整后commit/dispose错误Unknown，不先发布内存或虚称回滚成功；日志只固定码/异常类型，不新增文本/时间/SQL正文回显。
- 原mood UPSERT抽完整常量并由internal static WriteTextInTransaction(conn,string? normalized,DateTimeOffset? updatedAt)共享；原唯一poke INSERT抽完整常量由private static WritePokesInTransaction(conn,IEnumerable<long>)共享，两原SQL定义复用、不拆关键字/加SQL。SaveLocked与Observed callback均同conn写text+原_pokeTimes foreach，保失败RecordPoke留内存戳在后续Set/Reset/Expire成功flush的真实恢复语义；RecordPoke/Prune控制、列表及公共签名不改，不嵌套Write。Mood _gate→独立DBwriter锁序保原，任何未来统一consumer不得在DB锁内反拿Mood _gate。
- TTL过期仍在Describe/CurrentText逻辑隐藏；新增pure IsStale(now)检查，不因清库失败重新暴露expired text。Expire清text持久成功才null内存、保oldUpdatedAt；失败保持真实内存事实但逻辑显示无主观心情；TTL0不expire，prune/自动描述/poke恢复保原。未读真实Mood/DB，只使用合成text与隔离数据库faults。
- ownedtest覆盖原invalid/equal/清洗/24边界/accepted-changedbool/Reset更新时间与戳不清/TTL0/过期故障隐藏、真正text/poke写故障回滚且未publish、pendingpoke恢复、commit阶段Unknown可观测安全结果（无法确定真实终态不伪Committed）；使用现实际MoodStore/现backup/真实工程，不新测试路径或公共Program。精确计数后置，所有旧assert/SQL137等棘轮保留。
- 此批不实现独立actionreceipt/dedup/LegacySaveOutcome HTTP消费；那些依冻结719独立namespace另准入，配置NoChanges不能绕动作去重，也不能从本Observed.Committed直接声称外部HTTP动作完整Applied。旧bool consumer日志若仍误称成功列后续精确shared批，不偷扩本路径。新模块green不自动完Wave1/开Wave2；回退仅本seam/primitive复用/commit后projection及TTL逻辑片段，保所有既有戳状态/工作区，不清理baseline证据。

### 22.102 Mood真实故障红后单产品源码窗口

- Root14:19:07.982～14:19:11.975 Review build退出0（增量0warning）；actual old public MoodStore.SetText合成UPDATE trigger fault14:19:11.977～14:19:14.150退出1、passed4后failed_text_write_never_publishes_memory。原校验/清洗/等值断言先通过，旧bool accepted-intent真而memory先变化/DB保旧，确认是真功能红，不是未存在Observed反射或编译失败。
- Root明确仅Mood原owner路径按22.101窗口实施，全部Provider/其它shared冻结，无并行build。后续原ownedtest将继续poke故障与pending恢复、TTL逻辑隐藏、真实deferred-FK commit失败Unknown、并发/恢复/配置version独立；计数与终态后置，不因已有4通过预报其余绿。00不写Mood、不build，独立actionledger/HTTP消费仍未准出。

### 22.103 2026-10-14 21:17恢复：未捕获native终态不计成绩

- Root报告原native91640句柄Unknown，恢复时CIM无dotnet进程，最终结果未捕获；不假记绿色或退出码。Root现唯一重跑Mood完整真实回归，00恢复原登记/shared只读席位，所有产品source冻结、无其它build。不得由source存在或旧成绩补齐丢失native结果。
- 00于21:18独立实核当前HEAD1f3181e21e8d1f9531e41066b033d8d60218d580、status-uall371/20暂存删除保持；Mood新sourceA38D42A82B2097D71C8ADD646C55DF26F1C1ED53F6D48EA7F7E22881EBDA7599、test5450B586E84FF0A7AC2BA24E3B7804FC129BA961699C13876D4D881EA7B396C5。五Provider产品仍Store17FAFD207E5BFB827E1E91A26D615BE1692B96AAE173B41BC70FD3E27AAFD9E3、Bridge4AF961649D2404661DE814011648DFFBC80502B5653AD6FB4C39ACDC64FC03CC、Settings12FEF7A7BD073840E442AA8B59DB436912288DFA716E6EB400D5046744DFC688、ProviderStore47306880347919572B1C709BA5FC59D06ADE605516810EE2E2533CE4AFDA3E14、Comp6B40AFFB4B2BA84ED4764A672336F54B165709805CC8EB4016A979E47C1B5C4F，pwsh查询退出0。
- Mood源码新seam仅说明已实施22.101，不替新真实命令/时间/退出码/覆盖成绩，终态后另记录。下一完整C3 Policy仍02已回的只读候选，privatepayload/catalog/pure函数signature准确冻结与owner input登记前不写，不能由本恢复开Wave2或扩大SourceInstance路径。

### 22.104 Mood恢复真实统一结果与00独立内存逆向核验

- Root报告实际Mood专项14:21:38.979～14:21:40.292退出0、22/0（原21+TTL精确边界），A38/545 source不变；不是丢失native91640终态的替代记录。21:18恢复session92787每build/run0：Review21:18:25.082～21:18:41.494、815/0，父含Mood22与Provider62/refs34/fence11；Architecture至21:18:43.685、165/0、SQL137；Safety至21:19:02.212、594/0；Scope至21:19:05.811、failures0；Production至21:19:09.296、62/0。终点之外未提供分段起点不冒写，实际工程成绩非历史拼接。
- 00只读检查新Mood observed/commit后projection、shared完整SQL/pendingpoke与TTL隐藏段；随后pwsh仅在内存逆22.101新增using、内部result/enum/常量、Observed/Reset/expiry逻辑及共享两SQL调用，全文SHA精确回原input6918F5C076501E9A49692EB1BF3BD83C502AE47406B67DB967E08236A7719B24，命令退出0且无任何逆写/临时源码落盘，证明该单路径既有其它内容保留。回退候选只本batch语义片段，退回会重引先publish及过期清库失败可见风险，不自动当生产可执行回退。
- Root真Headless Rebuild→Harness Rebuild→S21/S36/S52 session16553仍运行，本条不提前报其结果；全产品继续冻结，00不build不改产品。test向AD930的逆证据待Root实测另补。Observed模块成功只说明独立文本持久结果，无receipt/dedup/HTTP组合Applied或全Wave1验收，完整Policy仍先readonly准确冻结。

### 22.105 Mood真宿主重建/legacyIT终态与两路径保留证据闭合

- Root session16553每nativeexit0：Headless trueRebuild21:20:21.491～21:20:27.909（8warnings）；Harness trueRebuild至21:20:32.071（2warnings）；S21 21:20:32.075～21:20:39.932、37/0；S36至21:20:47.105、98/0；S52至21:20:53.761、119/0。仅隔离mock、真实宿主新产物，不external或真实用户数据。
- HeadlessartifactEC8A8D65835FD5D3C270E5F7143DD60844EFD42D7E6356DAEE5F82CDB7897D9A、Storageartifact07EEC6F1B73CF6915C23FE8BB8470DD507C21702849A3E37AD67562F7DD7C5E4；MoodA38/test545保持，scoped diff0；旧baseline37及manifest3FC9未替换。Root在内存只移本Mood factory/父入口/方法，test全文SHA精确回AD9307B00E9D4A339B04E06B78574D4A5A0BFB761E3627257F0344BB6A4837DE，pwsh0无任何逆写；与00Mood精确6918逆证明两原owner路径其它旧改动完整保留。
- 本批producer可观察文本持久结果及旧宿主bool副作用已行为验收，仍无独立actionreceipt/dedup/HTTP组合真实结果，不能把Committed producer当用户动作完整Applied或Wave1完成。当前无活native，所有产品冻结，下一完整C3 Policy由02原只读席精化privatepayload/catalog/pure signature，00仅read/doc，不又以局部gate替全域也不提前源码或Wave2。

### 22.106 Policy origin只读形状纠正（未冻结新增源码）

- Root与02交叉核实际Core ConfigurationObjects89～90：PolicyEdit(Target,PlatformPolicyConfiguration)，六Configuration成员均mandatory，Registered.Policies仅target稀疏，不存在六个FieldEdit/字段wirepresence。00先前依据六ReadField提“仅dirty字段bits”的臆测不成立，已明确撤回；当时只是只读候选，未改Core/协议/产品源码，不把该猜想落实。
- 待冻结准确origin方向：同一private typedpayload每entry wrapper {policy:<原CoreEntry>,explicitFields:0..63}，legacy初始化0，全表Field102 Set所有entries63，Registered.PolicyEdit每个被列target完整Replace→63（包括有意Inherit/Features空），未列targets及bits原样；只有真实legacy scalar alias桥局部意图可OR对应bit，不能按值相等猜用户没编辑。Features实际ImmutableArray整map，不新造FieldEdit或每featurepatch。wrapper私有额外属性不交Coreentry反序列化，键名/revision/codec/pure函数仍下一批准确冻结后才源。
- FieldId实际PlatformPolicies=102，135是VoiceVol，不按错名写。Root DesiredModes现只direct102，未来原ownedStore对非空Registered.Policies补102 Explicit须独立前置登记、无新SQL；整体table mode不意味着未编辑的legacy entry全是新User来源。Read六字段的UserMode与effectiveSource按原163：有意Inherit模式与实际父/基线effective来源分开；Explicit有用户值、Inherited/Absent编辑值是安全effective，不能为了origin伪造公共字段。逐entrybit只作来源事实，具体Inherited/Absent与legacy规则在准确signature表一并冻结，源仍全冻。

### 22.107 完整C3 Policy十一原路径准入与确切私有接口（先真实红）

- 依据负责人既有八路径限定前置整改许可、02两轮只读评审及Root本次明确完整Policy施工要求，00于22:11 +08实核如下input；无新增文件/publicC3 shape/冻结hash/SQL/schemaDDL/数值棘轮改动。Root仅三原owned：src/BotAgent.Storage/ConfigurationStore.cs=17FAFD207E5BFB827E1E91A26D615BE1692B96AAE173B41BC70FD3E27AAFD9E3、ConfigurationStorageBridge.cs=4AF961649D2404661DE814011648DFFBC80502B5653AD6FB4C39ACDC64FC03CC、tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs=5450B586E84FF0A7AC2BA24E3B7804FC129BA961699C13876D4D881EA7B396C5。
- 02临时认领四Platform源码，仅本条改段（shared最终00复审合入）：src/BotAgent.Platforms/PlatformOptions.cs=90E7A6AE0D94A93F5B7B6BBA457710A6B4BBCAE2E1A63315DACBB89C342D57AB、Governance/PlatformSwitchSettings.cs=B3C9565C5C3848CEC9F77DBB67EBEDAFAEBD55446091F4C58ACE0914CD0E8BDE、Governance/PlatformPolicyResolver.cs=551AC5E975F7E6A7542769B3330F29E6DFB478F385F51F93F93ED2BE32490588、Feishu/FeishuBotGateway.cs=C9A9C5DFED46E44DB205820D2A5CD2902FD880B6AB38A81648140E91383AB24F。02须Root真实红通知后才写、不build；完成全文hash/改段交回写权，不改Registry/CorePorts/其余路径。
- 00独占四Headless共享：Adapters/Persistence/SettingsStore.cs=12FEF7A7BD073840E442AA8B59DB436912288DFA716E6EB400D5046744DFC688、Host/CompositionRoot.cs=6B40AFFB4B2BA84ED4764A672336F54B165709805CC8EB4016A979E47C1B5C4F、Adapters/Persistence/BotConfig.cs=D9BBC84FEC4AC595CA8DF0F77A1A5B41151A28B898678F774CB85F879ADD86F6、Services/Qq/WhitelistGate.cs=B0F870451410C52873CB4D23D5DFC7D9E993F088031F1B171470E996EA204D84。当前所有产品仍冻结，先Root只test真实C3合法Policy拒/legacy缺口/现名单门禁负例；不得reflection缺新成员或compile失败冒功能红。Root唯一builder，红终态后分别放行已认领产品小段，并在所有source完成freeze后串行验证。
- **Root Storage producer**：existing ConfigurationStorageBridge internal LoadPlatformPolicies(SqliteConnection)→internal readonly record struct ConfigurationPlatformPolicySnapshot(bool Stored,ImmutableArray<PlatformPolicyEntry> Entries,ImmutableDictionary<PolicyTarget,int> ExplicitFields)，ToString固定安全类型；internal WritePlatformPolicies(SqliteConnection,ImmutableArray<PlatformPolicyEntry>,ImmutableDictionary<PolicyTarget,int>)。fixed唯一meta键configuration_platform_policies，closed v1 {version:1,entries:[{policy:<原Core PlatformPolicyEntry>,explicitFields:int0..63}]}；absent→Storedfalse，存在empty→Storedtrue权威空，空串/未来rev/缺失或duplicate/unknown属性/非法enum/Unicode/重复target安全固定JsonException，不自动重建/迁移。writer稳定TargetOrder，不另schema/SQL/writer，复用真实GetMetaInTransaction/SetMetaInTransaction。
- Root可将现Store FreezeTarget/FreezePolicy/TargetOrder从private精准internal供同Storage producer复用，方法规则/旧HMAC byte行为保持，不另造规范器，不依赖PlatformOptions/Headless。这里只closed结构与Core不变量；trusted driver/action语义目录由Platforms/Host验证。DesiredModes对非空Registered.Policies补Field102 Explicit（已有directSet/Clear仍一次、重叠仍原AliasConflict），同既有modeSQL无新增定义。Read/Keep/NoChanges/replay不写codec/marker。Root不改Headless/Platforms。
- **02 Options运行事实**：五个[JsonIgnore] public immutable属性：RuntimePlatformPolicies:ImmutableArray<PlatformPolicyEntry>?（null未采用，Empty已权威Clear）；RuntimePlatformPolicyExplicitFields:ImmutableDictionary<PolicyTarget,int>=Empty；RuntimePlatformPolicyBaselineSources:ImmutableDictionary<ConfigurationFieldId,ConfigurationValueSource>=Empty；DeclaredPlatformDrivers:ImmutableArray<DriverKey>=Empty；DeclaredPlatformActions:ImmutableArray<ActionId>=Empty。CopyPlatformPropertiesTo逐项复制immutable引用，保旧PlatformPolicies DTO/JSON类型及深clone，不把typedTarget/runtime目录反序列化进旧body。
- **02 Switch pure接口**：ImportLegacyPolicies(PlatformOptions legacy,ImmutableArray<DriverKey> declaredDrivers,ImmutableArray<ActionId> qqActionBaseline)返回(Code:ConfigurationResultCode,Entries:ImmutableArray<PlatformPolicyEntry>,Errors:ImmutableArray<FieldError>)；ValidatePolicyEntries(entries,declaredDrivers,registeredActions)返回ImmutableArray<FieldError>，第三参真实canonical动作目录，与global baseline子集分开。ResolvePolicyRules(settings,PolicyTarget,qqActionBaseline)及(settings,PlatformContext,qqActionBaseline)返回现PlatformPolicyConfiguration，后者只构造真实Instance target；invalid/undeclared Context安全deny，不补默认scope/另实例。ResolvePolicySources(settings,PolicyTarget)返回六named ConfigurationValueSource tuple(Enabled,ChatEnabled,GroupWhitelist,PrivateWhitelist,AllowedActions,Features)，与同规则递归共享算法，不复制第三份查找。
- pure规则Instance→同account AccountDefault→仅四canonical scalarbaseline；Explicit空终止继承，AllowAll只名单，不越enabled/chat/global/connected/capability；features逐项overlay，empty继承parent false；新scope默认disabled/chat-disabled/explicit-empty/actions-empty。Source对explicit叶按bit1 UserConfiguration/bit0 LegacyCompatibility，Inherited递归父/实际baseline source map，缺proof Unclassified；Features混合Unclassified不造Mixed枚举。Mode无entry→Absent、rule继承→Inherited、explicit→Explicit，editable inherited/absent值为安全effective，不用origin冒旧runtime已经应用。
- LegacyImport用未被Normalize变更的旧事实，schema<1原交集/≥1原覆盖；旧二元/IgnoreCase只对可信目录唯一映射，重复/歧义/未知动作features/不可无损旧scopeLegacyNeedsReview。私域分区→MessageWhitelist/空deny，官方分区/空AllowAll，飞书raw+alias，local旧两分区union→LocalChannelIds且“*”all位原不消费，不升AllowAll。Normalize/SynchronizeLegacy在RuntimePolicies.HasValue（含Empty）时旁路旧DTO恢复；旧Read(string,string)只兼容旧profile，不把channel-only当可信来源。
- **02 Resolver/Feishu**：Resolver保原2argctor并add 3argctor(IPlatformSettingsBox box,IPlatformRegistry? registry,Func<PlatformOptions,ImmutableArray<ActionId>> qqActionBaseline)，exact Resolve(Context)一次Current→三元Ordinal snapshot→sameSnapshot pure rules，与connect/desired/chat/hardcap分开；global delegate只真实ParseAllowed。Feishu真实Context已有feishu-main，入站IsAllowed按exact rules/category/raw+alias，Explicit空止fallback，拒绝不消费dedup/回调；Send先比platform/account/instance Ordinal再token/HTTP，跨实例零IO。保无typedlegacy旧兼容、原凭据/cache/dedup/ConversationId/协议语义不扩。
- **00 Host prepare/init/read**：SettingsStore用Root Bridge唯一codec，无Headless第二codec；private PreparePlatformPolicies(conn,persistedUser,scalarCandidate,intent,profile)返回(Changed,Entries,ExplicitFields,Errors,Code)。trusted目录从单次可信runtimeOptions snapshot取得，不改已有7arg participantctor。Initialize仅当Storedfalse pure legacyImport并同TX写一次，迁移bits0/auditField102、一次总version；Read不迁移。direct102 Set完整table所有entry63、Clear权威empty；每Registered.PolicyEdit完整target Replace63，未列原样；mode-only变化亦Changed，canonical排序/格式不制造Changed。任何末段target/action/features/list校验失败整批拒，persist与user/secret/provider/device/version/audit/receipt一TX、无独立Save。
- Legacy scalar名单只baseline，原非空policy override继续盖住，不OR bits替换该override；旧toggle仅按真实body出现/原ApplyLegacy同步canonical AccountDefault并OR位，实际现键officialEnabled/officialChatEnabled/privateChatEnabled/feishuEnabled/localChannelIds等，不创造不存在的ApplyExplicit/qqPrivateEnabled。仅真legacy wholeDTO table输入意图才替换，若已有typed含Instance/AllowAll/Explicit-empty等不能无损成旧二元表，整批LegacyNeedsReview，不删/压缩。已Stored时Keep/Read/NoChanges绝不从留存旧DTO重建。旧HTTP当前统一writer采用另后继批，不把未接API说成已接。
- internal static ProjectPlatformPoliciesInTransaction(conn,AppSettings settings)从同TXsnapshot填运行Entries/bits及baselineSource事实；SettingsStore.Load userJSON+typedmeta用同读TX，typedfuture/corrupt不进入旧catch返回defaults冒授权；BotConfig环境/用户overlay之后保护typed事实、runtimeSource来源如实捕获，不回写任意runtimeJSON。Read填现ProjectConfigurationView requested/effectivePolicies seam；requested用最新TX source/modes，effective只用单次Current捕获的Entries/bits/baselineSources，不借最新DB mode称旧runtimeUser。Overview不持raw名单，Display mask、Key原样、Inherited/Absent safe editable、未加载或未真正应用不报Applied。
- **00 boot/gate**：CompositionRoot在optional enabled/credential/adapter筛选之前可信声明四key(qq.private,legacy,空instance)/(qq.official,legacy,空instance)/(local,legacy,空instance)/(feishu,default,feishu-main)，动作目录从真实QqActionCatalog.All/spec.Name捕获immutable，不能ParseAllowed子集充目录；globalActions delegate从同snapshot AgentServerQqActions.ParseAllowed，已批准Action实现/平台权限仍实际交集，不由text cap获得QQ管理动作。Whitelist新增exact PlatformContext名单判断复用Switch返回规则及旧实际匹配，不从key反推instance/另一scope；原channel/key入口只保已证明canonical legacy兼容，来源缺失不假装exact adopted。
- **驗证/边界/回退**：Root既有test闭合factory/实际Store+Hostparticipant/真实Switch Resolver Gateway，无新路径或公共Program；初始化/全table/targetReplace/modeonly/NoChanges/Clear不复活、可信disabled/notloaded目录、Ordinal/casecollision、Explicit空/AllowAll/feature父false/globalActionsubset/legacy矩阵与local*、同TX末写fault/rollback/replay/CAS/恢复、SafeRead sources含Inherited/runtime旧bits与未知codec、Feishu跨context零tokenHTTP、旧有效名单不被新scalar覆盖均需真实证据。全producer/Host消费/Platforms source冻结后Root唯一真实build/Review/defaultArchitecture/Safety/Scope/Production/Frontend及真Headless Rebuild→适用S50/S52及旧场景，保原断言/SQL137/Domain300/file1765。00 SettingsStore当前1418仅余347行，结构codec已自然放Storage，不压多成员一行或放宽阈值；若仍真超预算须停止精确提路径/复用，不塞ModelProviderStore。
- 本批完整C3 Policy配置域及已真实Context局部门禁，不包含未授权消息来源envelope/全工具与dispatch纵向迁移，不开Wave2A/2B，不把declared目录/metadata/局部Resolve当完整来源隔离。生产新C3 DI/HTTP/八step实际应用与整套oldbundle+preimage回退仍后续完整Wave1义务。回退仅本11paths认领片段、反核后续依赖，保oldDTO/public构造/字段/所有既有改动及37asset证据，禁止reset/checkout/clean；未知新typedSchema不靠旧JSON投影假回退。

### 22.108 Policy真实红后十一owner源窗口与00四共享冻结（未build）

- Root报告22:25:04.127～22:25:19.996 Review build退出0/增量0warning；实际C3Store+真实SettingsParticipant对已声明qq.private/legacy AccountDefault合法whole typed table Save，22:25:19.998～22:25:22.375退出1、passed0，固定whole_typed_policy_save_supported。是真实Save拒绝，不是新成员反射缺失或编译失败。Root明确释放22.107各owner产品source，Root只Store/Bridge，02临时四Platforms，00四Headless，彼此不抢路径、不build，待全部freeze后Root唯一builder。
- Root producer输出StoreCC823310DA9A6FD4CFCA87CE3A5D97AEE5F5D48221DBB16C389DD9E2EE66022A、Bridge3783F283B6998BA6D9A228A1A52003D9D677C04EF05128FDFC97F949AE0F8B6F；00已实际读新增producer，closed meta/snapshot/loadwrite、现freeze复用及sameconn原meta原语与登记对应，未改Root源码，不把静态检查冒行为通过。
- 2026-10-15 11:42 +08恢复只读实核00四shared已落，正式冻结不重复实现：SettingsStore1608行/SHA521A835248A33AADDE4114E47206F120ACA423A2AB607A8487D4055E3978CEDA，CompositionRoot746行/SHA8C311CDD879DC54F5947B6FFFF6CA5424ECD5845F8201EF3F341D91756F4CC26，BotConfig742行/SHA7D9BE0BE14207BC2425FC9D7D10FD3274AB47AE04C48872FDA359D70B2B0855A，WhitelistGate257行/SHAF4085548C3974DC3B4AD406E909DF2A4D94886FF7233C2D0023764F1769D5108。pwsh hash/linecount/scoped diffcheck退出0（原CRLF提示保留），Settings未超1765、不压成员/不放宽阈值；00未build/test，02Resolver3arg尚待完成交回，不能现称整批可编译。
- 源码包括同TX初始化/prepare/可信Read与origin、权威empty不复活、tuple/canonical模式事实、声明目录在Box前、实际All动作目录与global子集分开、现7ctor保持、typed startup不会由旧DTO恢复、exact白名单overload与channel旧入口边界。Root确认channel-only兼容仅canonical AccountDefault、同账号declared/loaded唯一且无Instance override，否则source_context_required，不从channel装成真实instance；Feishu真实Context走exact。rootfixture对standaloneLoad明确再注入受控目录或调用Comp.DeclarePlatformCatalog，不能typed DB对象自行授目录。
- Headless首patch因重复context校验失败无任何写，随后准确hunk成功；只读查询曾误猜Core PlatformContext/Headless AppSettings/WhitelistPolicy独立路径退出1/2，已按真实src查Domain.Platforms.PlatformId、Services.AppSettings与Core.Domain.Qq.WhitelistPolicy；BotConfig BehaviorEnvTable实际方法已按真实调用纠正，不创造属性。均未运行产品或当产品故障。11:42:49实核HEAD1f3181e21e8d1f9531e41066b033d8d60218d580、20暂存删除与冻结契约7B7C保持，status-uall实际372（上批371），未擅清或冒未查原因，已通知Root。
- 负责人新增“不做过量测试”：本批后续仅必要Policy专项、架构与实际影响链，保原断言，不再默认无关全套。计数/退出码/准出必须后置真实运行；现只有Source完成与静态差异证据，未预报专项绿、生产C3DI/HTTP采用、完整来源隔离、旧产物回退或Wave1/Wave2准入。
- 随后Root/00同读发现ImportLegacyPolicies内部用legacy.DeclaredPlatformActions验证canonical目录，而JSON忽略运行目录；仅传global允许子集不足。Root明确只解除Settings两个Import前四赋值窗口，00向受控DB snapshot传runtime可信Drivers+AllActions，未改pure/ctor/公共shape；Settings新1612行/SHAC7223BCE9D3BB45B098AAFD4E79985B8867F4AB2FCD528F293F7AF5BA6E3FEE9、scoped diffcheck0，立即重新freeze，其余三Host未动，无build。Root必要专项新增真实受控Declare调用、Inherited source/Overview/未假Applied、13坏codec与Feishu跨Context零fetch，不扩大无关测试。
- Root报告纯内存逆本Policy段命令0：Store精确回17FAFD…、Bridge回4AF961…、最新test4D936861C3CC3E196A9EAD446393D904430EF89448D126FCAEADE42F648E9C6A精确回5450B5…，未写逆文件/临时源码，保全部既有改动；Root源亦冻结，CIM无dotnet，等待02四source交回后唯一必要验证。该证据非产品行为验收。

### 22.109 Policy四Platforms临时写权交回与Local构造gate最小采用

- 2026-10-15 12:04 +08恢复，00实核四Host仍C722/1612、8C311/746、7D9/742、F408/257，未重复实现或额外产品写。00尝试只在内存逆本Policy Settings片段，首次全文hash未匹配、命令退出1，无任何源码逆写/临时文件；因此仅记“未证明”，不冒精确input保留证明。Root要求不为凑证据重复过量尝试，停止该诊断，以已登记input/确切改段供后续审查。
- 02正式交回四路径临时写权并冻结，00实核输出：PlatformOptions144行/AE0B0A7A7BD440F52DFDAEEBDCC3EFA5140D22A2AE97BF204B0DE8CC41C6E42A；PlatformSwitchSettings430行/A06C3D3CEFA020DA6B6442E2B433A1C232EFABA73DA9CA611F3798A673B07A5C；PlatformPolicyResolver196行/020231C5523899237D6F247303768B4C50ACBCD4312A09E72B59792CDF949223；FeishuBotGateway742行/AC3339B0A86B6ECEB992D634CFABC8140033C2FEFCBE3BF243359A0362D78180。00接收最终复审职责，02不再改源/build；Registry/CorePorts未混改。
- 02静态发现Comp原Local构造gate使用旧Switch.Read，而新Read对typed旁路返回false会误禁用真实typedLocal。Root明确只解除同Comp单gate：typed调用已冻结ResolvePolicyRules(AccountDefault(local,legacy),真实ParseAllowed baseline)判断Enabled；未typed分支精确保留旧Read.Enabled且LocalChannelIds非空，不将旧scalar名单非空再附加到新typed表。00仅该处完成后750行/SHA177A21CFD1ADF3266513B5FB62A1AD58DF1788593D816F057C01FAFC12A79304，scoped diffcheck0、立即重冻，SettingsC722/Bot7D9/WhitelistF408不变。
- 当前11源均交Root唯一必要Review build+Policy专项，之后仅Architecture与必要S50/S52影响链；00没有build/test，不预报可编译/专项绿，不把完整配置域/typed本地构造消费变成完整来源隔离或生产统一保存采用。

### 22.110 Policy首轮66真实通过与旧白名单入口loaded门禁补全

- Root首轮真实Review build12:04:56.001～12:05:17.313退出0（8既有warning），Policy专项至12:05:20.815退出0、66/0。这是实际C3Store/Host/pure/Gateway专项，未用compile代行为；后续架构/影响IT仍待，不冒全Wave1准出。
- 00静态指出自己的AllowsCanonical仅核declared唯一与无Instance override，AllowsKey会先Resolver门禁但public AllowsSource(bool,id)/IsOfficialPrivateExplicit没有同样loaded核验；不能用另一路绿替代。Root确认同22.107停止线必须闭合，明确仅解冻Whitelist此helper，input为已登记F4085548C3974DC3B4AD406E909DF2A4D94886FF7233C2D0023764F1769D5108，复用现Resolver.ResolveForChannel安全canonical视图并要求Registered/Connected/Enabled/ChatEnabled，无新增instance推断/目录/公共接口。
- 00仅该helper两行补全，输出259行/SHA0B072FBA00EACA5E3BEE0856B8416D406B637AFE7D1EEAD7AF6F4E36E90CA57F、scoped diffcheck0、立即重冻，无build；其余10源保持。Root仅ownedtest补缺loaded负例与必要legacy迁移/source断言，后续真实结果后置，不拿首轮66冒新断言已通过，不加无关全套。

### 22.111 Policy必要专项72与实际架构通过（影响IT尚运行）

- Root最终必要Policy专项12:06:53.422～12:06:56.802退出0、72/0，覆盖legacy schema交集/覆盖、大小写歧义、local“*”不升级、真实初始化knownAction目录与bits0、mode-only版本及loaded缺失deny；新增loaded负例通过，不用首轮66代替。原Host adapter23/0至12:06:57.293退出0，原断言保留。
- Root Architecture build退出0至12:06:58.397；真实默认运行至12:07:03.104退出0、165/0，原数值棘轮未放宽。本消息未附本次SQL具体扫描计数，不把历史137冒作当前捕获，必要终态后按真实汇总补。
- Root仅原ownedtest把72加入既有ModuleRecovery工厂列表，无公共Program或新测试路径。当前唯一session87057执行真Headless Rebuild→Harness build→最终test注册编译→S50/S52，仍运行；只受影响链，不其它无关全套，不提前记IT通过或退出码。全部产品source保持22.109/110冻结，00只登记、不build/改产品；生产统一C3DI/HTTP与完整Wave1准出仍未宣称。

### 22.112 Policy真宿主重建与必要S50/S52终态

- Root session87057最终退出0：Headless trueRebuild12:07:27.993～12:07:38.861退出0（8既有warning），Harness build至12:07:40.005退出0；最终test工厂注册编译退出0，中段工具输出截断，不捏精确结束时间。S50 command返回0且脚本继续S52，S50计数/精确分段时间输出截断，保留此证据缺项、不重跑凑数也不借旧成绩填；S52 12:07:47.830～12:07:54.429退出0、119/0。
- 本次真实HeadlessartifactSHA37FAAB7EE31ABB5A9C8D7758E0A2DF3415457EDD0A3F8199B344270A4892621F、Storageartifact0B54EFB9A0D5323041887CCEE5139455965E1953925139EF9443A832BCF2DF65；test353081E7543706EFA5969C7DAE576AC27F8BC0A1D4B9DF4659955851BD9E04A4仅原ModuleRecovery增72入口，原owned路径不扩。旧37assetbaseline/manifest3FC9仍保留，不把本新binary当旧产物回退。
- 本批具实际C3Store/真实Hostparticipant/pure Resolver/局部可信Context门禁及新宿主旧链兼容证据；新统一C3生产DI/HTTP未采用，channel-only兼容不是完整来源实例隔离，未宣称Wave1完成或开启Wave2A/2B。当前产品source继续freeze，下一按21.4真实门禁只读选择最短已有写权关键批，不扩源/不先build。

### 22.113 迁移前合成数据库前镜像捕获单步准入（不是回退验收）

- Root明确同意本最短前镜像单步，00实核唯一源码路径为Root原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs，inputSHA353081E7543706EFA5969C7DAE576AC27F8BC0A1D4B9DF4659955851BD9E04A4。仅同文件closed child/factory复用既有真实仓储/备份，不新增源码/测试路径、公共Program、公开C3接口或产品patch；全部产品保持冻结，00不build，Root唯一必要捕获命令，不顺带无关全套。
- Root独占原ignored证据根 %WORKDIR%\bot-agent\.git\agent01-wave1-rollback-b3a84e32821f442cb72d0d7aee14d670 下精确派生写权：preimage.db、preimage-manifest.json、backup-staging目录及本child产生的合成DB/日志性SQLite sidecar/备份暂存文件。只用于此一次UUID隔离fixture和真实备份，路径必须解析后仍在该nonce根/明确staging内；目标已存在、重复或越界即拒，不覆盖/删除未知文件，不复制真实data/env/config/log。旧37asset bundle及manifest3FC9BC4DC7E8B91BAAFFC1604B0E8B8A47E51F62003720B28494F02E37279542一字不改、不替换为当前新binary、不自动退役证据。
- closed child只初始化隔离synthetic AppDatabase，复用原SettingsStore.Save、固定可写slot、provider真实原语及旧六字段device用户JSON形成与旧Headless5A04格式预期兼容的基准事实；不调用ConfigurationStore.Activate、不写新typed policy/provider sentinel/origin，不把当前module声明当旧binary实读证明。不读真实环境或数据库；子进程按名字清继承环境再仅设合成路径/值，输出仅固定码、计数/hash/版本，不回显fixture秘密或配置内容。
- 实际备份接口已查询为AppDatabase.VacuumIntoBackup(string? backupDir=null,int maxRetained=3)，本次maxRetained=int.MaxValue，backupDir仅nonce内backup-staging，避免retention删除其它备份；备份正常返回后核返回路径在staging且为本次产物，再仅精确移到不存在的preimage.db，不递归move/delete。文件hash与预期表形状/版本形成preimage-manifest.json，记录固定baseline artifact5A04、契约/源版本、相对名、安全hash/计数/版本；不写秘密/JSON正文/真实路径配置。
- 本次RollbackVerified=false，旧bundle+preimage联合实际恢复尚未执行，格式预期兼容不作BaselineCompatibilityVerified。后继统一writer/迁移完成后须独立真正新版本→恢复这份前镜像+原整套旧bundle的行为演练，不能用本捕获、新binary重启或codec fixture代替。源完成后Root只必要build+closed capture取真实时间/exit/hash/形状，结果后置；失败保留证据并拒绝伪造成功，不为凑验收重跑无关probe。

### 22.114 合成前镜像捕获真实终态（旧产物兼容/回退仍未验）

- Root首次Review编译12:13:40退出1，test误用没有Initialize(path)重载的Headless facade，查实际StorageDatabase.Initialize(path)后仅纠ownedtest；不是产品失败或臆造新重载。12:13:49.309～12:13:53.252 Review build退出0/增量0warning，真实closed preimage capture12:13:53.256～12:13:59.038退出0、18项，未改产品源码。
- preimage.db SHA954F62E5721B3BA96EC1DBC377A2DEA1000DA589F5D3E3790DDF1717D448238D；独立preimage-manifest.json SHAE925741572580BE05DC685A1BF39C799B7B2636426D055622CCAB3AA1A54F955。旧37asset bundle及原manifest3FC9前后完整核验，未覆盖/换新产物。OldBinaryCompatibilityVerified=false、RollbackVerified=false如实保留，本次只是真实合成旧格式前镜像和备份捕获，不是旧binary已读取或整套回退演练。
- Root同意下一原ownedWeb.Settings纯完整LegacyPlan方向，当前仅冻结真实parser复用签名/准入准备；无生产Activate、不改实际POST、不创建重复公开接口，产品source仍冻。具体输入/私有成员另前置登记，再仅该既有路径施工和必要合成验证，不扩大旧writer范围或开启Wave2。
- Root后置testSHA为BC46EEF0CBC10810915829BA0DE880C08199BEB9F4044049FDC02E42E6B6F16D；纯内存仅移新capture factory与CaptureLegacyPreimage方法后全文精确回353081E7543706EFA5969C7DAE576AC27F8BC0A1D4B9DF4659955851BD9E04A4，pwsh退出0、无任何逆写，scoped diff0；原preimage954F与独立manifestE925实核不变。此证明ownedtest其它既有改动保留，不把18项捕获当旧binary兼容通过。

### 22.115 原Web单文件纯完整LegacyPlan组装准入（未接实际POST）

- Root明确确认复用方案；本批两原owner路径：00 src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs input005E5B3EB9D7B8D90B1F9AA3C084559D21ACF3EF52DACA59A345583ACE3E3398（实核1405行），Root tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs inputBC46EEF0CBC10810915829BA0DE880C08199BEB9F4044049FDC02E42E6B6F16D。只原文件私有纯函数/既有ownedtest closed factory，不新source/公共Program/公开C3接口、SQL、环境/DBIO/日志/动作或生产Activate。其余所有产品继续freeze，00不build；单Web source冻结后Root仅一次必要编译+组合专项。
- exact私有入口 private static LegacySecretPlan ParseLegacyConfiguration(JsonObject body,BotAgent.Platforms.PlatformOptions trustedOptions)。沿用唯一现LegacySecretPlan及原六参ctor、Code/ErrorCode/Secrets/Mood/TtsProjectionRequested/Dispose/ToString，不复制_owned/不建第二秘密owner；新增nullable ConfigurationPatch Changes只可由同private plan受控AttachChanges一次装配，二次拒，原ParseLegacySecrets签名/独立行为保留，Rejected计划Changes为空。
- 原ParseLegacyScalarFields(JsonObject)签名保留，委托同file ParseLegacyFields(body,devices,policies)复用同一七分区构造表；原Agent/Platform单参mapper保留，委托WithDevices/WithPolicies原initializer新增复合slot，不复制140映射或对非record class用with。devices仍现ParseLegacyDeviceFields→SettingsStore.ParseLegacyDevices，无DB/自报identity授权；typedtimeout规范与未知body字段拒审照旧。全部非秘密mapper在分配SensitiveInput前完成，坏末段policy/device没有秘密owner分配/Changes，无副作用。
- 官方Secret任何case presence（包括null/空/false）优先固定official_secret_environment_only拒绝：只把现ParseLegacySecrets的presence谓词提成同file共享private helper，复合入口先复用，再纯解析非秘密。合法非秘密完整后调用原ParseLegacySecrets一次、成功才AttachChanges；旧空Token整批LegacyNeedsReview，绝不新增clearToken或别名秘密字段，旧六槽Keep/Replace/Delete、Mood独立intent/TTS标志原样，ToString/诊断不含秘密。
- Policy按真实旧handler：body缺省或platformPolicies显式null均Keep，非null仅经现TryParsePlatformPolicies旧DTO解析；数组[]为Set(empty)而不是臆造Clear。临时旧PlatformOptions只承载解析新DTO、SchemaVersion=1和由trustedOptions一次捕获的真实Driver/AllAction目录，再用现PlatformSwitchSettings.ImportLegacyPolicies；这是新旧HTTP whole table的覆盖规则，不冒存量schema0初始化；Inherit保持规则，不在parser计算或写运行基线。global allowed子集不冒注册目录，不能请求对象自授目录，未知/歧义/不可无损对象安全拒审。既有field无body时Keep，body/trusted对象均不得修改。
- Root组合专项约10项，复用原140/六槽细粒度证据而不再重复全矩阵：七分区+旧六字段device+真实wholepolicy+秘密/Mood/TTS同一plan、未初始化Storage时零IO、body/trusted不变、null与[]区别、坏末段policy/device无secret owner与Changes、官方presence优先、空Token拒、唯一ownerDispose失效。新函数本身尚未存在，不把反射缺成员或编译失败当功能红；产品不接HTTP，目标纯组装行为可在source落地后必要验证。实际POST453～520旧链仍未改，不宣称统一writer采用或完整Wave1/2准出。

### 22.116 纯完整LegacyPlan单Web源码冻结（尚未验证）

- 00仅22.115 Web原路径完成，输出1475行/SHA78E26E49C4E31D9C78F878A033A078229DC98CF42A023E8467B824C59172903A，scoped diffcheck退出0（原CRLF提示），立即freeze；无build/test、无其它产品写入、未改变旧实际POST或生产Activate。
- ParseLegacyConfiguration准确使用已登记签名与同一LegacySecretPlan，原六参ctor/ParseLegacySecrets保留；Changes由AttachChanges受控一次装配，拒绝plan为空；官方presence共享原谓词优先拒，device/wholepolicy/七分区全部解析后才原sixslot分配一次。ParseLegacyFields承载唯一七分区构造表，Scalar原入口仍仅140字段，WithDevices/WithPolicies复用原initializer，不复制_owned/重复秘密owner、不对class使用with。nullablePolicy Keep/数组空Setempty按真实旧handler，临时Schema1+受控目录仅纯legacy整表转换，不执行运行求值/动作/IO。
- 当前只新增私有纯组装能力，未把它接到HTTP，不把源码存在冒采用。Root仅原ownedtest必要组合专项，源码冻结后唯一一次必要build+target；具体计数/时间/退出码/未覆盖后置记录，原140/sixslot细粒度assert无需重复扩大无关全套。

### 22.117 纯完整LegacyPlan必要组合专项14真实通过

- Root首Review build12:18:12.150～12:18:17.566退出0；first probe至12:18:19.006退出1、passed0。实际原因是Root新fixture臆用camelCase policy键，而现旧Web.Json无naming/case-insensitive、app.js:2037及S52真实wire为PascalCase；仅纠fixture按既有wire，不改产品解析器/兼容语义，不把该fixture失败计产品红或以放宽解析器换绿。
- 12:19:09.840～12:19:13.528 Review build退出0/增量0warning；组合probe12:19:13.532～12:19:14.847退出0、14/0，产品Web78E26保持未改。Root仅原ownedtest ModuleRecovery登记该14入口，无公共Program/新增路径，无无关全套、未重跑preimage捕获。
- 证明真实私有纯组合入口与既有mapper/单秘密owner工作，不是实际POST采用。S50/S52对应旧37FA Policy批产物，早于本次新增unused parser，不能冒成本次parser宿主IT；由于未接运行入口，本批不为未触及的链重复凑数。当前产品继续freeze，实际统一writer/publish、Token GET/UI、Mood独立receipt与设备运行门禁等21.4余项仍未完成，不开启Wave2A/2B。
- Root最终组合testSHA99FEEDB05F876AB2B3FE65FBC6DEDD6E0D026C48829FD471C9380AE0B8392190；纯内存仅移组合factory、父14入口及LegacyConfigurationPlanChild后全文精确回BC46EEF0CBC10810915829BA0DE880C08199BEB9F4044049FDC02E42E6B6F16D，pwsh0/scoped diff0，无任何逆写。Web78E26/冻结契约7B7C与status372/20暂存删除保持，未把既有改动归入本批。

### 22.118 Wave1注册插件保存缺口与待权限路径（不授施工）

- Root实际只读指出PluginStore旧plugins_state仍IgnoreCase/坏格式吞错，PluginManager.SetEnabled仅内存、CompositionRoot七preset不消费PluginStore；Registered.Plugins当前Host明确adapter_unavailable，不能以其它分区通过说完整C3保存已完成。此缺口仅是Wave1必要注册对象保存/可信目录/后续应用契约，不把Wave2/3/5全C4生命周期反推为本批额外全部任务。
- 00按真实rg文件枚举核两待授权路径及当前input：src/BotAgent.Storage/PluginStore.cs=F04EBC288B89E1DADB0D417C50F8C9D6B4DBA17792E984E3318A6980061D1D20；src/BotAgent.Headless/Services/Plugins/PluginManager.cs=549D3B385A31C5FFE0D36C6393A4488924D5959D9746B6447E8BB9C5A99C0284。两路径不在Root本轮已认领/00原14共享范围，当前仅待权限与只读事实，不可因本登记取得source写权；IPluginStore虽真实存在亦未请求改动，不扩接口文件。04只读/实施席位是否获授以负责人答复为准，本条不新开席位或冒已授权。
- 下一Device两个原14路径只读方案已向Root提交：AgentBridgeServer真实BridgeConnection、已存在HTTP token边界、Root现AuthorizeDeviceHello/CanDispatchDevice消费；精确准入仍待Root确认另条登记，不先源写或build，不把该方向当统一POST已采用。

### 22.119 Device真实hello/dispatch/control两个原共享路径准入（先实际server红）

- Root明确确认本最小批，00已按真实文件核input：仅00 src/BotAgent.Headless/Services/Agent/AgentBridgeServer.cs=7FF57D85A9C0CEFD93512A62E1039714C60857488736103A8BAE57229877F1E5、AgentCommandService.cs=CF634BDC52DA1D64C664E5ADDF5F30A589C880F42BBE8E4BCC7FC667DC9911B9；仅Root原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs=99FEEDB05F876AB2B3FE65FBC6DEDD6E0D026C48829FD471C9380AE0B8392190。两个产品皆21.2原14共享队列，不新增路径/公开Core/Storage接口，Root不写shared，00现仍冻结；先仅Root原test真实server+合成WebSocket锁功能红，不能以缺新helper/编译失败冒红。终态红后再单独释放两source，00不build。
- 真实连接类型为AgentBridgeServer内private BridgeConnection（现Socket/Lifetime/Token/Id/SendGate/Registered/Name），保原两ctor签名、队列/单工人/单Receive/心跳与同名replacement清理。新增仅server赋值DeviceKey? BoundDeviceKey，hello当前453段在入_bridges之前调用已有StorageBridge.AuthorizeDeviceHello(name)，成功才绑定returnedKey与现concrete连接Id；任何自报key都不能进入授权或赋值。无active的pending改名确认会因既有安全audit要求而拒并同TX回滚，不冒确认成功或另开writer；空表未知legacy名保原兼容，不靠注册表空默认拒全部。
- 已只读核真实认证链在未认领Adapters/Panel/WebUiServer.Agent.cs:24～68先固定AgentToken验证→AcceptWebSocket→HandleAsync；本批不改该partial、不新增认证参数/协议，sharedToken+自报名不是物理设备身份，不能夸称防冒用其它已授权名。Storage当前两现成gate已具失败关闭/稳定Key/tombstone/历史名规则，复用不复制SQL或派生目录；DB异常不得旧SettingsBox继续放行。
- 准确消费改段：Connected/BridgeNames/IsDeviceOnline/AnyBridge/DeviceInfo/PickBridge等选择与在线投影仅返回持久gate允许的具体conn，现runtime旧enable限制保留；worker依旧固定target及ConnectionId，最终SendGate内task/control/ping在Socket.SendAsync前再次CanDispatchDevice(Name,BoundDeviceKey)，不在队列早期检查一次冒最终检查。nonhello回包在现concrete/任务Id所有权外再持久gate，撤销后晚结果不更新任务/回调；拒绝只固定safe码/本地stop与原cleanup，无新增rawname/秘密/logbody。Disconnect CloseOutput前也核门禁，失败仅本地停止不外发。
- 现Cancel按DeviceName重新选择可能把旧任务control转发到同名replacement；本批限定只向原ConnectionId匹配conn发送，不改Cancel公共签名或后台语义。AgentCommandService现ResolveRoute保原runtime IsDeviceEnabled/global开关/server fallback，额外调用AgentBridgeServer新增internal bool CanDispatchToDevice(string name)（现gate安全薄封装、只server-held key、不新catalog），撤销/DB失败不能被旧snapshot标签与自动选择绕过。所有gate查询不在_stateLock内形成DB写锁倒置；不将user配置version用于每次hello运行事实确认。
- Root仅约8～12必要真实consumer断言：disabled/deleted hello拒且clientkey无grant、选桥/worker任务及control最终检查、sameName replacement取消不重定向、撤销晚结果拒、empty表旧兼容、DBfault关闭fallback与pending确认/审计边界；实际constructor/HandleAsync/队列/Socket方法先查询再写fixture，未获权partial不动，无新认证协议。不重复既有Storage30/备份7全矩阵，不无关全套。产品全部freeze后Root唯一必要build/target与实际桥影响场景；本批不等于生产统一保存/HTTP或完整Wave1准出，不开启Wave2。

### 22.120 Device实际server红后两个共享消费者源码冻结

- Root真实Review build12:27:07.492～12:27:11.178退出0/增量0warning；AgentBridgeServer.HandleAsync+loopbackTCP WebSocket pair在既有HTTP认证之后的实际server边界，先过设备commit与actual models控制正例2项，随后Save committed disabled而真实IsDeviceOnline/Connected/BridgeNames/AnyBridge仍露在线；12:27:11.182～12:27:12.862退出1、passed2，固定committed_disable_removes_real_bridge_from_all_selectors。是真实调用链红，不是缺新成员或编译失败，不修改认证partial。Root明确释放22.119两个产品source，Root仅续原ownedtest必要8～12断言。
- 00仅两个原路径完成并冻结：AgentBridgeServer1034行/SHAC862B2AC9564588EDDC6A5C245D5C8BD2EAC7F8E6B501D76507EB5A80DE3FC17；AgentCommandService845行/SHA1349E5F25B98771B3165519E1F762B2ADB0A99F0B244BD3F3E79CAA81B2E232D；scoped diffcheck0，无build。其余产品保持既有冻结输入，未改WebUiServer.Agent/Storage/认证/公开Core接口。
- Conn新增server-only BoundDeviceKey，hello入字典前Authorize返回成功才绑定；selector/在线与Describe过滤IsDispatchAllowed；worker沿原concrete conn+Id，private SendAsync(conn,…)成为instance以在SendGate内同时核当前dictionary仍同一conn与真实持久gate，所有task/control/ping统一经过该最终点；incoming晚帧在任务Id所有权前再gate否决。未知legacy空表保持原行为，DB失败/root gate拒则本地Stop，不让Current旧快照放行，不新增rawname/secret拒绝日志。
- Cancel仅捕获原ConnectionId对应conn，绝不按同名重新选择；实际Send与Finished通知置于_stateLock外，保原本地取消收尾/计数与队列语义，防新增DB gate置于任务state锁内。Disconnect显式name取原conn，仅持久gate允许时CloseOutput，拒绝通过finally本地Stop无外发；Any/name为空仍只挑允许conn。Command仅在旧runtime DeviceEnabled外加真实internal CanDispatchToDevice约束，原全局后端/server fallback保留。源码存在尚不冒行为通过，已交Root唯一必要编译+consumer专项，实际计数与回归终态后置。

### 22.121 Device真实WebSocket消费专项12通过（唯一影响IT仍运行）

- Root Review build12:29:49.139～12:29:54.389退出0（8既有warning）；真实loopback WebSocket consumer child12:29:54.392～12:29:57.677退出0、12/0，覆盖serverKey绑定/selector/control/tombstone、SendGate最终revocation竞态零task帧、撤销晚done不能成功、rename+安全audit且不增加用户version、未知legacy兼容及真实缺表读故障failclosed。不是只有Storage单元或编译证据；HTTP认证原边界未改，本批不冒新认证协议验收。
- Cancel采用受控合法replacement窗口（旧current+新socket字典索引）证明控制不被重定向，不声称自然时序穷举或物理身份认证。关键生产消费者现确实调用Bridge gate，但统一C3生产Activate/保存入口仍未接，不把该consumer采用泛化为完整Wave1。
- Root仅原ownedtest父入口注册12，当前唯一session83703真实Headless Rebuild→Harness build→父注册编译→S33影响链，仅此适用场景，不无关全套。终态未知不预报；两个shared C862/1349与其它产品继续freeze，00只登记、不build或额外产品修改。

### 22.122 Device真宿主S33终态与两共享输入保留核验

- Root session83703最终退出0：Headless trueRebuild12:30:27.795～12:30:32.574退出0，Harness build至12:30:33.471退出0，父12注册编译至12:30:37.895退出0；S33 12:30:37.896～12:30:54.315退出0、57/0。仅实际影响桥场景，不无关全套；本条取代22.121未终态状态，不把专项12当S33替代证据。
- 本次新HeadlessartifactAE32C719F846332109A7C77397F8E22816E658C66557ECC00402EC4642809A71，Storage仍0B54EFB9A0D5323041887CCEE5139455965E1953925139EF9443A832BCF2DF65；testB274926D1583B7849FEE9FF67BE4AD6854AB61084EB83BDC53CAB0FA01309D5F。Root纯内存移bridge factory/父12/方法与Peer类，全文精确回99FEEDB05F876AB2B3FE65FBC6DEDD6E0D026C48829FD471C9380AE0B8392190，pwsh0/scoped diff0、无逆写。
- Root要求最多一次00两产品内存逆核，00本次一次pwsh/Python只在内存逆22.119改段，两全文SHA分别精确回原AgentBridgeServer7FF57D85A9C0CEFD93512A62E1039714C60857488736103A8BAE57229877F1E5及AgentCommandServiceCF634BDC52DA1D64C664E5ADDF5F30A589C880F42BBE8E4BCC7FC667DC9911B9，命令退出0，未创建临时源码/未逆写文件，证明各路径其它既有改动保留。回退只能逆本片段并核后续依赖，退回会重引撤销绕过/同名control重定向风险，不自动授权生产回退。
- 旧37assetmanifest3FC9与preimage954F保留未变；整套旧产物+前镜像实际回退仍未验。Root末次原生指纹0：HEAD1f3181e21e8d1f9531e41066b033d8d60218d580、status374/20暂存删除、LFjoinSHACB6F497C2AF70381C9A85A397DCA0DF0B3EAD9CA572505F50AEB4C62B1F6A111；两个shared实际均M，不随口称状态无变化，不清理。Root当前无活native，所有产品冻结；下一fullHTTP同锁writer/允许secret版本+Token GET/UI批只读冻结实际接口/范围后另准入，不提前施工或扩大权限，不开启Wave2A/2B。

### 22.123 现SettingsBox单锁显式发布许可最小准入

- Root明确确认并恢复指令，本批仅00原共享src/BotAgent.Headless/Services/SettingsBox.cs，实核input1267FD05D1E9B0BBD64D42CDAB05DE38898EAF1371CEB9EC78223F555B2A9A14；仅Root原ownedtestConfigurationContractTests.cs inputB274926D1583B7849FEE9FF67BE4AD6854AB61084EB83BDC53CAB0FA01309D5F。无新文件/Core公开形状/数据库或第二协调器；不改其它产品，不生产Activate，不把许可值来自不可信body。
- 保原public ApplyConfiguration(Func<AppSettings,(AppSettings Snapshot,ConfigurationSaveResult Result)> persist,Func<AppSettings,ConfigurationSaveResult,ConfigurationSaveResult>? published=null)完整CLR/source签名，其仅包装现tuple2结果为tuple3且PublishAllowed=true。新增仅internal同名overload，persist类型为Func<AppSettings,(AppSettings Snapshot,ConfigurationSaveResult Result,bool PublishAllowed)>，published签名同原；实际lock(_gate)与现首次CodeOk、非Replay、DecisionChanged且Committed条件/Exchange/published逻辑唯一放在该overload，不再造Box/DBwriter或新公开接口。原tuple2直接调用仍兼容，不改其既有行为断言。
- internal overload在现原保存eligibility之外额外PublishAllowed才Exchange；false原样return真实Result、完全不Exchange/published，不伪造Replay/NoChanges/错误码或提交事实。Host未来persist回调须同锁先Save，再仅首次Changed+Committed TryStart(PublishSnapshot)；真实StorageUnavailable保Code及Committed/CommittedVersion，已有claim/superseded等CanRun=false但CodeOk也能携原真实Receipt而许可false，不假设不可达。claim成功才许可true，结果账本失败保Unknown且不得自动重做；此Box不声称自身持久化任何step，原publish异常兜底仍只是响应提交事实，不能冒Find已写新ledger。
- Root仅必要定向断言验证许可false/true与真实Result保留、claim之前不Exchange、已有claim/账本故障/superseded不发布，复用原public条件发布断言，不重复全套或无关IT。新internal入口尚未存在，不用反射缺成员冒功能红；source冻结后Root唯一必要build+target。完整HTTP/允许秘密统一版本与Token GET/UI实际采用仍后续整批，不能先开部分Activate绕过旧唯一保存路径。

### 22.124 SettingsBox发布许可真实14专项终态（生产HTTP尚未接线）

- 00仅22.123原SettingsBox小段完成，输出117行/SHAEEAE3ABCD8A6727DBB60844A896E92EBE19F99BC590986E3839FDF6304624C6C；原public tuple2完整签名保留，新增internal tuple3许可同_gate唯一实现，false原样返回真实Result且不Exchange/不published，没有伪造Replay/NoChanges/错误码或第二writer。产品现freeze，00未build/test。
- Root真实Review build14:22:56.320～14:23:22.803退出0（8既有warning）；实际Store+Box专项14:23:22.815～14:23:25.099退出0、14/0。原public七断言保留；新增真实已保存/首claim后重复Unknown claim保持CodeOk+Committed且不Exchange、真实UPDATE触发器导致claim账本失败保持StorageUnavailable+Committed、首次许可发布及RecordApply真实落账、Replay/CAS并发/superseded闭集，不能用纯bool模拟代替真实Store分支。
- Root最新ownedtestSHA为CF4718DF73497E8715ACE0BD1BF65BB1F56BB524D1E514BF3A5C0F0D051B439D，00本次实核同值。Root纯内存移factory/父14项/method精确回B274926D1583B7849FEE9FF67BE4AD6854AB61084EB83BDC53CAB0FA01309D5F，pwsh0/scoped diff0，无逆写；父入口14字面量追加尚未单独重新编译，留下一必要编译顺带，不冒其已编译或重复无关全套。该专项不证明生产POST/DI采用，生产Activate仍未调用。
- Root恢复核对默认status193是未跟踪目录折叠；按旧--untracked-files=all仍374/20暂存删除、LFjoinSHACB6F497C2AF70381C9A85A397DCA0DF0B3EAD9CA572505F50AEB4C62B1F6A111，未发现丢改动。HEAD仍1f3181e21e8d1f9531e41066b033d8d60218d580，冻结契约7B7C不变。00本次只读Get-FileHash误用不存在ReviewRegressionProbe路径退出1，按本总纲真实ReviewRemediationProbe修正后退出0，不算产品失败；只读rg使用Windows wildcard/猜Netease partial路径失败亦未写产品，改用真实目录检索定位WebUiServer.Music.cs。
- PluginStore/PluginManager额外路径限定写权仍待负责人答复，未授不写。下一仅只读冻结现POST兼容+新strict profile Read/Find/Save、六slot旧writer与非C3音乐cookie边界、同锁Save/claim/publish及Token GET/UI与Mood独立action receipt；不得提前部分Activate或在未认领PanelRoutes绕集中router，也不以此Box绿宣称完整Wave1/开启Wave2A/2B。

### 22.125 完整HTTP/Token闭合接入私有提案与实际停止项（待Root确认，不授产品写）

- 00本轮只读核现PanelRoutes.cs:124～126为*方法/api/settings，只有POST进入HandleSettingsSaveAsync，其余直接无context BuildSettingsPayload。该router未认领，禁止在已领WebUiServer.RouteAsync前偷塞分叉绕集中路由。最小候选只在现POST内用显式configuration=read/save/find query：无query旧CompatibilityProfile，无ExpectedVersion；新save直接采用冻结2.2五成员输入，新read为kind、新find为key闭集对象。具体query尚待Root技术确认，不称已有HTTP契约/实现；不新增任意secret JSON、clearToken或caller/access。2.2已精确约定六slot/kind/value私密输入，须手工接管SensitiveInput，不得Deserialize整ConfigurationSaveRequest绕Core OutputGuard；未知/重复/必需/Unicode预检须在JsonNode折叠前完成。候选1MiB/depth16/4解析准入仅新profile待确认，不无声套旧合法body。
- 真实统一接入拟同批连接Comp唯一Activate、SettingsParticipant真实同conn投影、HotReload单Box锁Save→同提交version读回→durable stage claim→许可Exchange→真实逐step报告、原POST纯LegacyPlan、Token GET安全元数据与新UI原key/Find/显式Delete。不能拆先Activate再补唯一管理通路；读Current即有效的Policy/Model必须在Exchange前有真实Started，不能交换后再补假Started。NoChanges/Replay/Find/Unknown不发布/重建；Box许可false保真实Code/Committed。固定DB模型credential实时消费与路由metadata apply/restart分维度保持已裁决事实，不冒ModelProjection Applied。
- 已核旧writers准确路径：WebUiServer.Settings旧ApplyRuntimeSettings及直接五secret wrapper；另唯一PanelDeploy.cs:271只PanelDeployUrl，路径未认领不可写。可在原HotReload public签名内部把该实际caller的唯一field96变化归一成typed Legacy patch，其它变化拒绝，避免任意JSON绕写。SettingsStore.Save与Storage SecretsStore.Save(name,value[,throwOnError])需在各原Write内核同activation fence；后者固定六slot名字阻断，不误伤非C3 NeteaseCookie/panelPassword等。官方历史API任何时候拒写且不自动删旧row。真实WebUiServer.Music.cs SaveNeteaseCookie→PatchSettings只patch runtime cookie保原兼容、不扫入C3版本或改未领Music partial。BotConfig启动环境overlay不得普通Save回写用户事实，controlledInitialize承担真正迁移/版本。
- Token实际泄露/回写链仍在BuildRuntimePayload输出feishuVerificationToken原值、app.js加载原值并始终post空/原值；必须同批移除raw/mask头尾，仅configured/source，输入总空/未改omit、新Delete用slot5 kind2，旧空/白请求副作用前LegacyNeedsReview+固定刷新提示。新UIstrict保存需真正dirty typed意图，保原version string/generation/serverTime/key及同key Find恢复，不全表回写旧二元Policy/丢Device stableKey，超时/Unknown不盲换key；旧GET兼容投影不能从旧Read误恢复typed-empty后的授权。
- Mood现只有configuration_operations表，现ReadReceipt/InsertReceipt/Scope带cfg1假设，不能直接调用现Find读Mood或塞八apply steps。候选在Root原Store/Bridge复用现完整SELECT/INSERT/UPDATE定义为闭集namespace原语（不新增SQL/表/第二writer，cfg1旧codec/HMAC字节不动），私有legacy-mood1 namespace和由同cfg key派生mood1存储键；规范Mood意图单独HMAC/无text正文receipt，共享全代4096/每主体256/TTL/8192预算。配置TX同时校验并预留独立NotStarted actionreceipt，NoChanges亦预留，防同key异Mood在配置已提交后才发现冲突或Replay追加动作。释放DB锁后持久claim Unknown→现Mood_gate→独立ObservedWrite→真实结果再落账；绝不DB锁内反拿Mood_gate。Replay/Find只读原动作事实不补执行Unknown，配置与Mood不是同一个回滚TX；Host私有LegacySaveOutcome/LegacyActionResults复用冻结719。此精确内部签名/预留与重放停止线尚待Root冻结，不称已有action API/已实现。
- 真正未决：上述POST query/new-only budgets和Mood私有reservation语义需Root窄确认；PluginStore/PluginManager额外写权仍等负责人，Registered.Plugins不可静默忽略。现已领SettingsStore/BotConfig/Comp/HotReload/Web.Settings/WebUiServer/app.js及Root原Store/Bridge/Secrets/Mood/test可承担其余连通批，逐文件input/改段正式准入尚未登记，当前全部产品继续freeze/00不build。整套旧37bundle+preimage回退仍必须后续真实演练；不以Box14或pure parser绿冒生产统一HTTP采用/Wave1准出，不开启Wave2A/2B。
- 本轮只读路径错误透明：误猜PanelAuth.cs导致rg退出2后已停止该命令，按实际目录定位WebUiServer.Auth.cs再核session撤销/过期；没有产品/数据读取或写入。授权复用现IsAuthorized/IsSameOrigin且participant提交前重新校验受控request scope，不缓存无限主体/token、不从body自授caller/access，不因外层入口通过就省略事务内重授权。

### 22.126 POST私有wire技术确认与Mood独立reservation四原路径准入

- Root明确技术接受22.125现POST内精确query configuration=read/save/find，无query旧兼容；六槽输入只冻结2.2 slot/kind/value，Core OutputGuard/Router/未领PanelDeploy/Music不改。新profile1MiB/depth16/同时4解析准入仍是候选，Root已普通文字询负责人，答复前不记批准；PluginStore/PluginManager额外写权亦仍待答。此条仅给下面Root四路径产品准入，不释放任何Host/前端产品source或提前生产Activate。
- 00实核HEAD仍1f3181e21e8d1f9531e41066b033d8d60218d580；Root原owner精确input：src/BotAgent.Storage/ConfigurationStore.cs=CC823310DA9A6FD4CFCA87CE3A5D97AEE5F5D48221DBB16C389DD9E2EE66022A；src/BotAgent.Storage/ConfigurationStorageBridge.cs=3783F283B6998BA6D9A228A1A52003D9D677C04EF05128FDFC97F949AE0F8B6F；src/BotAgent.Storage/MoodStore.cs=A38D42A82B2097D71C8ADD646C55DF26F1C1ED53F6D48EA7F7E22881EBDA7599；tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs=CF4718DF73497E8715ACE0BD1BF65BB1F56BB524D1E514BF3A5C0F0D051B439D。只这四原路径，保原全部改动/公开签名/旧语义、SQL137及其它棘轮；00不写四路径/不build，Root唯一实施与必要定向验证。
- ConfigurationStorageBridge.cs准确新增internal API：SaveLegacyWithMoodAsync(ConfigurationStore store,ConfigurationCaller caller,ConfigurationOperationKey key,ConfigurationPatch changes,ImmutableArray<SecretEdit> secrets,FieldEdit<string> mood,CancellationToken ct=default)返回ValueTask<LegacySaveOutcome>，仅配置保存+同TX预留，不执行Mood；FindLegacyAsync(ConfigurationStore store,ConfigurationCaller caller,ConfigurationOperationKey key,CancellationToken ct=default)返回ValueTask<LegacyFindOutcome>，同治理TX读两事实，不apply；ApplyLegacyMood(ConfigurationStore store,ConfigurationCaller caller,ConfigurationOperationKey key,FieldEdit<string> mood,BotAgent.Adapters.Persistence.MoodStore instance)返回LegacyActionResult。原SaveLegacyAsync参数/返回ConfigurationSaveResult完整保留，不改Core公共shape/契约hash，不另造writer或任意JSON/secret入口。
- 同Bridge文件internal类型：LegacyActionState {NotStarted=0,Applied=1,Failed=2,Unknown=3}；LegacyActionResult(string Action,LegacyActionState State,string? ErrorCode)，Action固定mood；LegacySaveOutcome(ConfigurationSaveResult ConfigurationResult,ImmutableArray<LegacyActionResult> LegacyActionResults)；LegacyFindOutcome(ConfigurationFindResult ConfigurationResult,ImmutableArray<LegacyActionResult> LegacyActionResults)。ToString固定安全摘要，receipt/error不存text/请求原文/异常正文，不塞八配置apply steps或新mg1。
- Store复用现configuration_operations与原完整SELECT/INSERT/UPDATE有限namespace原语，cfg1原codec/HMAC bytes保真；仅legacy-mood1 namespace、mood1派生存储键和独立规范意图HMAC。所有带/不带Mood请求均检查原key是否已有action reservation：原带Mood重放省略、原纯配置后来追加Mood、同key不同规范意图均在用户配置写前冲突；配置TX同时预留必要两条容量再落configuration+Mood NotStarted，NoChanges亦预留，不以配置NoChanges逃动作幂等。共享所有代/命名空间4096/每主体256/24h/8192预算与同授权/治理，不新增SQL定义/表或放宽扫描。
- ApplyLegacyMood先同writer重验权限/原reservation/规范HMAC及治理并durable claim Unknown，事务结束后才取Mood_gate执行实际Observed，最后独立结果UPDATE；不能DB锁内拿Mood_gate。原Unknown/终态不再执行，结果账本故障保Unknown，不把配置/Mood宣称一个回滚TX。Root确认此key/mood签名不能独自识别本次Host是否Replayed，首次非Replay资格由可信Host真实ConfigurationResult消费点保证；模块只声称持久claim/终态NoRedo。Host同Box范围只对首次成功配置决定（Changed已知Committed或NoChanges合法决定）调用，替换LegacyActionResults返回真实结果；Find/Replay只读原action，不补执行，不伪Applied。
- MoodStore仅抽原Trim().Trim('。','.','！','!','，',',')及1～24UTF16纯校验给reservation/执行复用，清理后非法在配置写前整批拒绝（Root明确确认）。原SetText/Reset/TTL/poke/SetTextObserved及旧bool含义保留，不要求其内存Unchanged承担新增durable承诺。新legacy专用Observed入口在同Mood_gate内复用原Load SQL核DB已达成才可报Applied；不一致则真实写且commit后projection，IO失败真实Failed/Unknown，动作时间只Store已注入clock，不读第二clock。新结果持久失败仍无自动重做，pending poke原恢复语义不丢。
- Root明确本批新reservation API尚不存在，不以反射缺成员/编译失败或旧Observed未有新承诺冒功能红；允许先实现上述准确私有面，再真实合成reservation/原语故障/NoChanges/冲突/claim/restart必要验证，不无关全套。真正HTTP功能红留后续旧实际POST连通批。source完成交hash/freeze后Root唯一必要build/target；所有其它Host产品继续freeze，完整Wave1/第二阶段门禁不变。

### 22.127 Mood独立reservation实现、两真实审查红修复与必要专项终态

- Root仅22.126四原路径完成，首冻结输出Store2DDDA94C95F4E614BF4DAAC5581E46621E1E03C0631BD847415C00CA49234773（1506行）、BridgeC8EDB471444A462E2582636CD6605EAEFE109328A60011151D4BA7FD6C131BDB、Mood489C53C427B60E2645BFD8686464C31444467ED26527F87D5C80EC46F823519D、test17A83E556BB77991E401BEA9F321BFD27C5D8C0A0A9547EE0F201739153A2422，00只读实核匹配。三新增Bridge签名/四internal结果与原SaveLegacyAsync保留，同TX两reservation/NoChanges、原cfg指纹分支不加Mood、DB事务结束再取Mood_gate、固定输出无text/secret静态符合；没有生产Host/HTTP消费或Activate，不冒源码即行为。
- 00集中审查提出两项可复现阻断：其一在用户写/version/config receipt之后MoodFingerprint失败抛ReceiptCodecException，被旧catch正常转Rejected而可能提交缺action的部分事实；其二新Mood receipt缺省或重复state可经默认Deserialize重开已终态为NotStarted。Root用实际合成UPDATE OF version触发器损坏generation codec，以及已Applied动作receipt缺state/重复state验证；不是接口缺失或编译失败。audit build14:45:58.044～14:46:05.263退出0；真probe14:46:05.292～14:46:06.996退出1，passed7/failed3，精确三条均红。
- Root仅原Store最小修：writesStarted在任何用户/audit/receipt写前置true，ReceiptCodecException仅在!writesStarted可返回安全拒绝，写后必须抛出由唯一Write整体回滚；NoChanges的receipt/action写亦覆盖。新private DecodeMoodReceipt闭集恰action/state/errorCode三个必需唯一属性、state整数enum、state-code配对；原ReadOperation完整SELECT追加namespace/terminal列并核真实namespace及动作terminal一致，不新增SQL定义/表，不改旧cfg HMAC/公开contract。00本次已只读精确复核这两个实际修段，无产品写或build。
- 修后Review build14:46:42.111～14:46:49.969退出0（8既有warning）；audit14:46:49.975～14:46:51.496退出0、10/0；主legacy-mood14:46:51.497～14:46:52.156退出0、21/0，其中实际fresh-process reopen3/0计父1。首轮主在17之后失败是fixture只AFTER INSERT计数遗漏UPSERT UPDATE，Root补实际AFTER UPDATE计数，未改产品门禁/删断言；不把该fixture错归产品红。两条预留容量边界使用合成预填ledger，仅证明本批required=2算术门禁，不替代此前真实16×256/4096及跨代容量证据，也不为凑数重复全场。
- 当前冻结Store98635145F78053064022CE0C5967D279C8B0AAC3BAA5D0DFFC89D8F57C08FF64（1526行），BridgeC8ED及Mood489C不变；ownedtestAF8D6A5152C96791EB621EA69589A6BACE89906E5F2A96C7836CF47EA8867DD3。00实核四hash匹配。Root下一仅必要父入口编译+旧cfg/alias codec和Box许可两影响链、实际架构SQL137，仍待终态，不预报其退出或把旧成绩冒本次；无HostIT/无关全套。全Host/前端产品继续freeze，Plugin额外权与新profile预算仍待负责人，完整Wave1/第二阶段门禁不变。

### 22.128 Mood独立reservation必要影响链与实际架构终态

- Root session47552最终退出0：父注册Review build14:47:37.286～14:47:42.414退出0、增量0warning（不冒既有warning已修）；retained alias-codecs14:47:42.419～14:47:44.281退出0、9/0；真实Box/storeclaims14:47:44.282～14:47:44.675退出0、14/0。新Mood21和audit10两个父入口已真实编译，取代此前父字面量尚未重编状态。仅必要影响链，不重复无关全套。
- Architecture build14:47:44.676～14:47:46.169退出0，真实默认run14:47:46.170～14:47:47.796退出0、165/0，护栏未改、SQL阈137不放宽；本消息未附实测SQL具体总数，不把阈值或历史计数冒当前捕获。scoped diffcheck0，无删断言/放宽棘轮换绿。
- 最终ownedtestSHA4F31962026D6925A77E486EC19EE5F7A54E59A0E5A51DB3F8935D70B17FD9644，00本条实核匹配；三个产品Store98635145F78053064022CE0C5967D279C8B0AAC3BAA5D0DFFC89D8F57C08FF64、BridgeC8EDB471444A462E2582636CD6605EAEFE109328A60011151D4BA7FD6C131BDB、Mood489C53C427B60E2645BFD8686464C31444467ED26527F87D5C80EC46F823519D保持。Root实核HEAD仍1f3181e21e8d1f9531e41066b033d8d60218d580、展开status374/20暂存删除、LFjoinSHACB6F497C2AF70381C9A85A397DCA0DF0B3EAD9CA572505F50AEB4C62B1F6A111，公共cfg-0.3.2/冻结7B7C不变。
- 当前无活native；全部产品继续freeze，00只登记未build/改产品。本批未连接实际HTTP/生产Activate，未做Host Rebuild+IT、未整套旧bundle+preimage回退，不把模块21或影响链绿冒统一宿主采用/Wave1准出。预算与Plugin额外权两个负责人问题仍待答；不因本终态绕过待授权或开启Wave2A/2B。

### 22.129 完整HTTP闭合批私有Host契约与精确input冻结（技术接受，仍待施工准入）

- 本轮仅按Root要求有界只读实核/接口结论，无产品修改、build、项目测试或后台任务。Root接受以下技术契约；不是source施工授权。新profile预算1MiB/depth16/4解析准入与PluginStore/PluginManager额外写权仍待负责人，受影响实现保持停止，全source继续freeze，禁止提前Activate/未Bind fallback或开启Wave2。
- 已确认wire：仅现POST HandleSettingsSaveAsync内精确query configuration=read/save/find；无query仍旧兼容。新main Save恰冻结2.2五成员，秘密按六slot/kind/value手工接管，不改Core guard。旧POST可选X-BotAgent-Configuration-Key header携原cfg1 key，非法/重复header安全拒绝；无header只单请求分配key，不冒网络重试幂等，header不作caller/授权。strict主保存成功或NoChanges且已知首次决定后，dirty Mood另新key旧POST {mood}，UI保两key/两事实分别Find，主失败/Unknown不盲发或新key重试，不称跨请求/动作原子。secret值及frozen payload只限浏览器内存，禁止local/sessionStorage、URL、日志；跨刷新如需恢复最多有界非秘密key元数据。
- 已确认Root桥窄seam：internal ConfigurationStorageBridge.ReadConfigurationVersion(SqliteConnection)返回ConfigurationVersion，仅复用现Store.Version方法/完整SQL，不新增SQL或新连接；尚未实施，Root两个准确input见下表，source本条不释放。Host同conn runtime读回必须核expected，不能独立ReadAsync后再另一连接拼user/metadata/secret快照。
- HotReload同文件internal nested ConfigurationRequestAuthorization：IDisposable Enter(ConfigurationCaller,Func<bool> reauthorize)、bool TryGetCaller(ConfigurationAccess required,out ConfigurationCaller caller)、bool Reauthorize(ConfigurationCaller,ConfigurationAccess required)。唯一AsyncLocal request scope，finally恢复，disposed闭包失效，无无限主体/key/token字典；仅Web既有认证/同源门禁后的可信caller与当前IsAuthorized闭包，事务前/内重授权，不接受body caller/access。Comp可先创建该scope holder给现participant授权回调，避免构造循环，原participant七参ctor保留。
- 原HotReload public11参ctor不改；internal一次性BindConfiguration(ConfigurationStore store,ConfigurationRequestAuthorization authorization,Func<ConfigurationVersion,AppSettings,ImmutableArray<SecretMutation>,AppSettings> readCommittedSnapshot,MoodStore mood,Action reapplyHealthReports,Func<ConfigurationVersion,AppSettings,ApplyStepReport> projectTts)。同绑定只同引用幂等、不同绑定固定拒；Comp同一次Build且任何HTTP/bridge/task开放前真实完成，不把未Bind兼容fallback当可运行入口，不另造Store/Box/writer。
- HotReload直接消费成员：ValueTask<ConfigurationReadResult> ReadConfigurationAsync(ConfigurationReadKind,CancellationToken ct=default)；ValueTask<LegacyFindOutcome> FindConfigurationAsync(ConfigurationOperationKey,CancellationToken ct=default)，复用现FindLegacyAsync、strict key的actions为空；ValueTask<ConfigurationSaveResult> SaveConfigurationAsync(ConfigurationSaveRequest,CancellationToken ct=default)；ValueTask<LegacySaveOutcome> SaveLegacyConfigurationAsync(ConfigurationOperationKey? key,ConfigurationPatch changes,ImmutableArray<SecretEdit> secrets,FieldEdit<string> mood,bool ttsProjectionRequested,CancellationToken ct=default)。caller从有效scope取得，原ApplyRuntimeSettings签名保留但激活后仅实际PanelDeployUrl单field diff归typed Legacy，未知mutation拒，未领PanelDeploy不写。必要提交后账本收尾不依赖HTTP取消、scope仍在真实完成前有效。
- 同Box唯一_gate内：Storage Save→首次实际决定→同conn/CommittedVersion候选→preclaim PublishSnapshot/RuntimePolicy/ModelProjection→tuple3许可；任一preclaim失败不Exchange、不Mood，原Result/Committed事实保留，不fakeReplay/错误。NoChanges+Mood仅首次真实合法决定可在persist返回前执行现ApplyLegacyMood，Changed在published真实步骤后消费；Replay/Find只查询、Unknown不补执行，独立结果回现LegacySaveOutcome。不得在Box锁外补动作造成倒序，也不新增Box接口。原RebuildRuntime不得再整段重复运行。
- 八step精确已有消费者：PublishSnapshot=唯一Exchange成功再RecordApply；RuntimePolicy=现Approvals.RebuildPolicy→Participation.Rebuild→Whitelist.Rebuild/PruneNonWhitelisted→AgentCmds.RebuildAgentUsers；ModelProjection=现SyncBrainFields，primary Current与captured nonprimary路由按真实维度，非热metadata如实RestartRequired，legacy committed DB凭据不冒ModelApplied；ConcurrencyGate=Reply.ResizeGate；ContextWindow=Registry.Snapshot逐会话MaxMessages/TrimToMax；Schedulers=Scheduler.RebuildIfNeeded+HealthReports.Reapply；TtsFileProjection=本version相关意图才claim→版本核对同conn固定Tts槽+该snapshot，释放readTX后现文件原语一次，失败/不确定Unknown、不据旧void/catch推Applied；RegisteredConfiguration=仅真实支持投影Applied，其余连接/路由重建/未批准plugin消费者不伪热应用、真实RestartRequired，不借此完成C4生命周期。每个动作Started在副作用前，结果账本失败Unknown、不自动重做；实际无工作step可真实no-op Applied，通知最后一次。
- SettingsStore拟直接消费入口internal AppSettings LoadCommittedRuntimeSnapshot(ConfigurationVersion expected,AppSettings previous,ImmutableArray<SecretMutation> mutations)：单deferred readTX内复用ReadSettingsSql/typedPolicy/provider/固定六slot，Root版本桥核expected，BotConfig现HotSave overlay纯复用，不调用Load()/Save()/另一连接、不把环境runtime写回user；mutations只固定Slot/Kind且Save前捕获，不从source相等猜Keep/Replace/Delete。restart-only当前值逐字段按现FieldEffect/真实消费者保留，不一刀切所有Model/Platform。Token已有投影只因子同conn overload复用原签名，不能重复播种或Read时迁移。
- UI可复用typed Read member名而非第二张140mapper。只读源码结构核对Views142个ReadField，原mapper134显式property/flatkey对camelCase差异0；旧save首payload对象136键不等于140 scalar全有控件。typed未在该首对象的10 scalar为agentMaxQueued/agentServerUseGate/agentTools/defaultTemperature/defaultTopP/feishuEnabled/localChannelIds/officialChatEnabled/officialEnabled/privateChatEnabled，不臆造空控件写入。将现唯一getter对象原样提collectSettingsInputs供渲染后baseline/保存复用；遍历固定七typed partition成员，精确own-key相交、rule.id/visibility/editable核准，3/102走专用路；仅真实dirty或显式reset生成FieldEdit，Inherited/Absent未触不变Explicit、Hidden不回填display，数字不靠字符串/NaN转null，model/base明确编辑空才Clear，其它空string按Set，reset须CanClear。这是源码形状检查不是额外项目/行为测试。
- Device专用：Read Key及nameRaw进本地表，新项nullKey，原项rename保Key，只有表dirty才Set完整desired；只key/nameRaw/enable/model/workDir/tools/timeoutSeconds，不夹online/cwd/models/runtime，未可信加载不允许省略造成delete，显示mask不写回。Policy专用：每row持原Target，不从显示key/loaded snapshot注册；只dirty target发Registered.PolicyEdit完整六成员，其它target不动。未编辑成员按Read.userMode重建原Inherit（Inherited/Absent）或明确value，Features inherited为空、explicit保原全部override只改真点击项，不用capSupported造false；typed row不再同步flat名单制造双意图。旧collectPlatformPolicies不能作为strict全表来源。

下一闭合批拟改input（00本次实核；仅冻结、没有产品准入）：

| Owner | 精确路径（仓库根%WORKDIR%\bot-agent） | SHA256 |
| --- | --- | --- |
| 00 | src/BotAgent.Headless/Services/Settings/SettingsHotReload.cs | 459F4DE9F8A17A4DD5F1C95DFC6E89D8EECE6EE403B118EC63FFD1420DAE42AF |
| 00 | src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs | C7223BCE9D3BB45B098AAFD4E79985B8867F4AB2FCD528F293F7AF5BA6E3FEE9 |
| 00 | src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs | 7D9BE0BE14207BC2425FC9D7D10FD3274AB47AE04C48872FDA359D70B2B0855A |
| 00 | src/BotAgent.Headless/Host/CompositionRoot.cs | 177A21CFD1ADF3266513B5FB62A1AD58DF1788593D816F057C01FAFC12A79304 |
| 00 | src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs | 78E26E49C4E31D9C78F878A033A078229DC98CF42A023E8467B824C59172903A |
| 00 | src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs | 7C77654516BFB4DB26AF03466AB0E55577CF2E19BEE0788999C72607BA1A578F |
| 00 | src/BotAgent.Headless/wwwroot/app.js | E01AC150E7344CB772B843BB3FEF9B02AB5820D3FA78704C25919C53D4387554 |
| Root | src/BotAgent.Storage/SecretsStore.cs | C45C9AC0684D462ACC8786187BDF346F76874AC75605869469A5896ABE986D84 |
| Root | src/BotAgent.Storage/ConfigurationStore.cs | 98635145F78053064022CE0C5967D279C8B0AAC3BAA5D0DFFC89D8F57C08FF64 |
| Root | src/BotAgent.Storage/ConfigurationStorageBridge.cs | C8EDB471444A462E2582636CD6605EAEFE109328A60011151D4BA7FD6C131BDB |
| Root | tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs | 4F31962026D6925A77E486EC19EE5F7A54E59A0E5A51DB3F8935D70B17FD9644 |

- 只读复用无拟改：SettingsBox EEAE3ABCD8A6727DBB60844A896E92EBE19F99BC590986E3839FDF6304624C6C、MoodStore489C53C427B60E2645BFD8686464C31444467ED26527F87D5C80EC46F823519D。PanelRoutes/PanelDeploy/Music不改；Plugin两个未授权路径不列已批准input。六slot公开writer fence只固定允许槽，官方历史写API环境红线拒，非C3音乐cookie/PanelPassword路径保兼容；不得以禁止绕写误伤音乐登录。
- Root接受上述Host方向及停止线。当前仅doc落账，source均冻结，等待两个负责人答复后再按准确改段/产品准入释放，不为等待继续拆unused helper/额外测试或后台工作。整套旧bundle+preimage回退仍后继真实验收义务，未以此提案宣称生产采用或Wave1完成。

### 22.130 负责人两项批准与完整HTTP/Token闭合批source正式准入

- 负责人新明确“都授权和批准”，Root确认仅最后两项：Root新增src/BotAgent.Storage/PluginStore.cs限定严格读取/同conn统一保存/activation fence及合成验证（不授权PluginManager）；新strict-only累计UTF8 1MiB/depth16/最多4解析准入及合成验证。不是额外Wave2/3、部署或生产授权，旧CompatibilityProfile不套新body限额，所有输入/秘密/权限闭集保持冻结2.2。此前22.128/129待批准状态由本条仅这两项更新。
- 00实核22.129十一input逐项完全相同，正式释放该完整闭合批source：00独占原七shared HotReload/SettingsStore/BotConfig/CompositionRoot/WebUiServer.Settings/WebUiServer/app.js，Root独占原Store/Bridge/Secrets/test；新增Root单路径PluginStore inputF04EBC288B89E1DADB0D417C50F8C9D6B4DBA17792E984E3318A6980061D1D20。总十二拟改路径，没有转交对方source权。SettingsBox/Mood只复用无本批新改权；PluginManager/Router/PanelDeploy/Music/工程引用不写，新路径先报。所有既有改动/20暂存删除保留，不提交/推送/部署/生产操作。
- 执行采用22.129已接受的私有Host API与wire：可信request-local scope及事务内再授权、同Box锁Save→同conn版本投影→preclaims许可→八step真实消费者、旧POST纯LegacyPlan/无ExpectedVersion/可选原key header、strict新read/save/find codec与body准入、Token GET只configured/source且UI dirty/显式Delete/key recovery、旧writer精确fence与音乐cookie兼容。Root增加ReadConfigurationVersion(conn)仅复用现Store.Version SQL，不新连接/SQL定义；Root实现六槽公开Save同activation fence，official历史写拒，非C3音乐cookie/PanelPassword不误伤。
- Plugin同conn准确需求：保原Storage.IPluginStore与public LoadPluginStates/SetPluginEnabled签名，新增internal static IReadOnlyDictionary<string,bool> LoadPluginStatesInTransaction(SqliteConnection connection)、internal static void SavePluginStatesInTransaction(SqliteConnection connection,IReadOnlyDictionary<string,bool> states)，只复用plugins_state现Get/SetMeta原语。严格object/bool/解码后重复/损坏拒、Ordinal stable key不trim合并、不吞错误重建允许；Host按真实七preset目录exact验证稀疏合并，未列保留，missing entry沿现Register默认true，启动用其既有defaultEnabled参数，不改PluginManager。public Set在原Write内fence，同conn保存不嵌套Write；无新Core public接口/任意JSON写入口/SQL。
- 00可按源码逻辑段实施七shared连通批，不另拆unused helper进度；Root并行只其五路径原语/必要真实HTTP合成test。全部source完成并冻结通知后才由Root唯一builder执行必要定向验证；00不build/test，不并行碰产物。本批最后一次闭合交付前产物不启动，不提前对外Activate不完整通路；任何接口/文件冲突/预算无法保持即停受影响部分并报，不放宽SQL137/Domain300/文件1765或删断言换绿。新能力只有后续真实Host/HTTP与完整回退证据后验收，source准入不等于Wave1完成或Wave2A/2B开启。


### 22.131 完整HTTP/Token闭合批首次源码冻结（尚未构建或验收）

- 2026-10-15 21:44 +08 本轮只读/源码静态核验：HEAD仍1f3181e21e8d1f9531e41066b033d8d60218d580，展开status376/20暂存删除，LFjoin SHA D5E4C54AB20A7A7AE5481A1719D178DA16D1BAA43A1B265FC0CE9AD110DD4850；不同于上一374，不清理/不伪称状态未变。冻结候选全文SHA7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203不变。00只改22.130七shared与本总纲，未写Root源/新增路径/未授权文件，未build/test/启动任何产物。
- Root四产品实际已落、00只读复核：ConfigurationStore211C446693AEF49077C16E61A963A888B66F6171DA2F85AF0A0B8FBBE27263F9；BridgeCF8FD677C840A2FB78BADCCD7CF58326BAAEE36BDAD99AC996274B85D85AA34D；Secrets637F3ACF68CB058B5A5A8A7C157FAD05D7B1A7700EFE0089E527BED8DDB6E914；Plugin44CB2730C0007F50575A34E6406C3E803401058323536E43BD0DB0FCDA865548。版本桥复用原同conn SQL；六槽公开writer在原Write内fence，official历史API拒/不读旧row，非C3cookie/password保兼容；Plugin复用唯一meta SQL、strict decodedduplicate/坏格式/Ordinal拒审，同conn API已存在且公开Set fence。Root test最新56BDEBE8BE3E8A8B34453AE3AC4B3199F399827617927F85E601E0FF226FDCBC，本批writer-boundaries预期27与真实独立进程HTTP factory均未实测，不报绿。旧HostAdapter故意污染DB fixture改用限定同conn原语，保原NeedsReview/版本断言，不关闭产品fence换绿。

| 00完整路径（均在%WORKDIR%\bot-agent\） | 冻结SHA256 | 行数 |
| --- | --- | --- |
| src/BotAgent.Headless/Services/Settings/SettingsHotReload.cs | 85DA3A17D83C5AFED1BF756DF6DA4DD008B270AA5AB507B4311492052AEF2549 | 451 |
| src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs | AF7590D88E31B98CADEC585A43F2EA0460EB56325338982DF39CA2796830F25A | 1732 |
| src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs | CFF7A9E02DE5ABC9BC8C965CE22A655A7506788406EF95BD9C792BDE51564156 | 806 |
| src/BotAgent.Headless/Host/CompositionRoot.cs | 1516E646937C0B8F672F346DF09995214FCE9E833959E149D1D6CCDB4943D949 | 818 |
| src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs | A0C450831F72A69C042C0886AFDCE66CED2870B24960A8D092525B5CF584C1DC | 1108 |
| src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs | A0BCE3E8C5A557B344FB93C362B37831F3D355D8C9997918C15C9A1A443FA710 | 945 |
| src/BotAgent.Headless/wwwroot/app.js | FBACDC4E6F63FC0353D64EC009D522223E3CA5CE5FEE52EB9C52F02B4481A377 | 5738 |

- 实际接线：CompositionRoot同次Build在构造任何gateway/bridge/HTTP消费者之前唯一Activate，原七参participant保持；缺失primary元数据/首次behavior seed/Token/typedpolicy/devices都在受控Initialize原TX。初始真实同Box再投影Token和typed表，启动PluginManager只用既有Register(defaultEnabled)消费受控exact七preset目录/strict存量状态，不改PluginManager。callback允许声明目录与loaded runtime分离；没有从body或快照注册新对象。
- HotReload绑定唯一store/授权/同conn投影/Mood/health/TTS六引用，重复Bind必须六引用全同。request-local lease执行finally恢复、disposed标记不可复活，实际事务权限来自原Web认证/同源再次检查。Save持原Box_gate→提交→同conn核精确CommittedVersion→唯一现scalar getter/setter规范事实投影（含两条primary ModelName/BaseUrl alias，非第二140映射）→Publish/RuntimePolicy/Model preclaim→许可Exchange→八step真实执行/落账。preclaim拒/账本失败不Exchange、不Mood，不伪Replay/NoChanges。RuntimePolicy/Model/Concurrency/Context/Schedulers沿原消费者，TTS核同版本固定DB槽后TryWrite真实返回，失败不以旧void/catch冒Applied；Registered stage覆盖真正restart-only字段、nonprimary元数据/插件及三个startup捕获gateway secret，不一刀切所有Model/Platform。模型固定DB credential实时安全消费仍分维度保原裁决，不据此宣称ModelProjection已应用metadata。
- exact POST wire保持22.129确认：现/api/settings只在HandleSettingsSaveAsync内configuration=read/save/find；read body仅kind，成功返回原ConfigurationReadResult（code/version/generation/contractRevision/serverUnixSeconds/view/secrets/fields/latestApplyState）；无合法generation的拒绝固定error/code/refreshRequired，绝不serialize Guid.Empty。save原五成员，手工闭集六slot秘密所有权，不改Core OutputGuard，返回原code/replayed/receipt/currentVersion/errors；find仅key，返回configurationResult与legacyActionResults，绝不执行动作。旧无query仍纯LegacyPlan/无外CAS，单key header仅旧POST，非法/重复拒，缺header单请求自分配不冒网络幂等。旧成功保runtime/env/settingsFile并加configurationResult/legacyActionResults；失败固定safe error，旧空Token409/refresh_required整批拒。
- 新profile仅1MiB累计UTF8/depth16/4解析准入，decoded重复/未知/必需/非法enum/Unicode/type在Storage前拒。storageStarted之后异常不转换为“非法JSON”遮盖已提交事实。成功200、validation/revision400、forbidden403、conflict/expired/generation/needsreview409、capacity429、storage/commit/unknown503、find-missing404、过大413。旧profile不套新限额；Router不改/不绕路。
- UI使用原唯一control getter表、typed Read member名定位七分区，渲染后baseline只发dirty；缺少的十控件Keep，不造空值；设备用真实stableKey/nameRaw/只dirty完整目标表，Policy只发被改target完整六成员Registered.PolicyEdit，保未改target与Inherit/Explicit空/AllowAll/Features原事实。Token不装载明文，六槽显式Replace/Delete，old GET只configured/source/无prefix/suffix。strict main与独立dirtyMood各一个原key/事实，主首次已知成功/NoChanges且发布已知才发Mood；Unknown先Find，用户明确同意且NotFound才原key/原内存payload重发，不新key盲重试。pending payload仅内存，历史有界非秘密key/结果，无local/sessionStorage/URL/log秘密。callback兼容停止线：bound旧ApplyRuntimeSettings唯一现PanelDeploy caller只field96归typed；未识别额外变化/非空published callback明确拒而不绕过单锁；非C3 music仅cookie投影，不把任意mutator带入C3。
- 本次pwsh/Python编辑与scoped git diff --check均退出0（仅现CRLF提示），只是源码/静态形状，不是编译、行为或性能成绩；误猜一次Token facade方法无匹配exit1，未调用产品/数据，已改用实际固定槽LoadInTransaction。七source现全freeze并通知Root唯一必要编译/真实HTTP与writer专项；红时只释放准确受影响段。此表不宣称通过原1765/SQL137架构probe，须Root实际验证，不增阈值/删断言。旧37bundle/前镜像仍保原，整套artifact/preimage回退尚未演练；Wave1未准出、Wave2A/2B未开，禁止提交/推送/部署/生产。


### 22.132 完整HTTP首次真实27/24通过与UI集中审查待修段

- Root唯一首窗真实终态：Headless trueRebuild21:45:02.919～21:45:20.923退出0/8既有warning；Review21:45:20.928～21:45:25.472退出1仅Root fixture三处误用facade.SetMetaInTransaction，原语实际StorageDatabase；只改测试alias后21:45:41.130～21:45:45.536 build退出0，不算产品红。writer-boundaries21:45:45.540～21:45:47.636退出0、27/0；Harness build21:45:47.637～21:45:49.030退出0；actualHTTP21:45:49.031～21:45:51.500退出0、24/0。包含两次真实独立Headless启动/重启、HTTP/CAS/秘密+提交/发布/原key恢复，以及chunked累计限额/depth/八partial连接→四解析准入等；不能以组件编译替代该实际调用链。
- Root session1145已真实exit0；当前全部产品保持22.131冻结。Root仅下一必要普通record必需成员负例/父24注册及一次完整适用Review/Architecture/Safety/Scope、Frontend和影响S50/S52，尚无终态不预报，00不构建/不启动。不重复无关IT矩阵，旧bundle37/preimage仍未实际rollback，不准Wave1/不开放Wave2A/B。
- 00依Root要求仅集中只读UI发现两真实候选：projectTypedDevices当前直接取Inherited的effective value装whole-table，会将未编辑继承空值/0物化；status失败可能旧device draft搭新strict版本。应按userMode保requested空串/0，先用本次已成功strict snapshot建立authoritative draft，再把status仅作在线信息。另Save成功后六secret输入/删除标记未顾lateEdits，会抹在途新输入；Unknown→Find后没有明确ack已知commit秘密，下一新key可能重新Replace。建议pending保存原main frozen/seq，只有已知commit才按原输入对照清理、保后续编辑，两个key/事实分开；Find不自动补Mood。typed Policy规则目前Inherit/Explicit空/AllowAll/Features及稀疏target保真未发现反向覆盖。以上仅审查，不在活验证窗修；等待Root终态再释放app.js准确段，不新增路径/测试范围。


### 22.133 仅UI两处真实保真缺口准入与源码重冻

- Root确认首窗session1145已终态，明确只释放app.js，准确input FBACDC4E6F63FC0353D64EC009D522223E3CA5CE5FEE52EB9C52F02B4481A377；只修22.132两个消费缺口，其它六shared和Root产品仍冻结。00实核该input后才写，无新文件/接口/解析预算/构建或测试。
- 设备先由本次成功strict Read建authoritative draft及saved基线，online status只补当前状态；Inherited/Absent的model/workdir/tools还原请求空串，timeout还原0，不把effective值写成显式覆盖；稳定Key/nameRaw保留。status失败不把旧表同新version混合后全表提交。
- 秘密ack保pending.mainFrozen/mainKey/mainResult及各槽发送时revision，revision由新Delete/input与现两旧Clear按钮共同推进。只有真实Committed或真实NoChanges回执，且当前input与原发送值、revision均相同才清输入/本次标记；在途新编辑不被一刀清空。Unknown先Find原main事实再做同一ack，不补执行Mood；独立Mood的key/frozen变更不丢main事实。两key依然分别可Find，秘密与冻结payload仅内存、不写local/sessionStorage/URL/log。
- 输出app.js 5769行/SHA6F5EDEC68206EEBF029F3F955AEC1CD397860F41EBF83A768D8C80885E582215；scoped diffcheck退出0，只有现CRLF提示。已重冻通知Root唯一必要编译/消费者验证，尚不报UI行为通过。Root只读已发现原FrontendProbe/probe.mjs和browser-fixture.mjs仍将任意POST当flat旧协议且静态硬匹配payload.agentDevices=JSON.stringify；未获两测试路径写权不改、不绕fixture造假绿，也不让新UI退回旧全表覆盖。若实际probe因此失败，须按真实问题分类并向负责人单独提权，保原保护目标，不删断言换绿。


### 22.134 HTTP26真实通过、Frontend旧fixture失败及完整Review预期诊断真红

- Root session30641已真实终态：Headless Rebuild21:50:04～21:50:09退出0，Review build至21:50:14.460退出0，actualHTTP26/0至21:50:19.193退出0（原24加ordinary record必需bool与null partition闭集用例）。同原命令node tests/BotAgent.FrontendProbe/probe.mjs于21:50:19.194～21:50:20.677退出1，原mock未实现configuration=read的code/view，仍将所有POST当flat旧保存；静态仍硬匹配payload.agentDevices=JSON.stringify和旧全量字段，不把该失败冒产品红/新UI已过。产品全保持22.131加22.133 app冻结，未为旧fixture降回legacy全表写。
- 00只读实核probe.mjs 364～418静态表、675起fetch、makeEl/VM和999起旧flat断言；最小迁移只需该文件，browser-fixture.mjs目前只独立抽api/session片段，本批未受影响，不顺带扩大授权。保原DOM覆盖/未加载拒写/名与XSS/官方secret/lateEdit/设备保全/策略继承全部保护，将flat全表断言更新为真实typed sparse/no-op与原key/Find事实，DOM补真实最低Select.add/Option/insertAdjacentElement/动态ID和webcrypto，不屏蔽真实生产异常。作用域旧断言失败来自其硬要求afterSettingsSaved；新save实际调用顶层loadSettings，无refreshAgentDevicesFull私有跨作用域调用，不能为通过静态实现形式而改生产回原旧hook。Root已向负责人普通文字申请probe.mjs精确权限，未答不写测试路径。
- 两UI修补必要用例限设备Inherited保持空/0与Key、status失败不混version、已提交秘密ack不抹在途编辑、Delete/Replace revision保后意图、Unknown→Find只ack不补Mood、main/Mood两key分事实等；不重复140字段矩阵或扩全套。
- 完整Review21:53:13.142～21:53:24.871退出1、799/1，ModuleRecovery到writer-boundaries停止：其子27/0但stderr实际恰7条预期安全拒绝WARN（官方1+固定槽6），父原stderr必须空规则仍保。Root独立受控捕获21:53:56.740～21:53:56.989退出0确认形状；只拟对预期bool拒绝逐次局部捕获且严格固定单WARN，绝不放宽父空stderr/计数。不是产品红；Arch/Safety/Scope因串行fail-fast尚未执行，不冒旧成绩充本次绿。00不构建/不改产品/不改Root测试，只后置登记。
- Root后继提出真旧37产物+前镜像演练。00已只读确认候选可沿原Root测试路径与ignored安全证据范围，精确source/input和新临时派生范围仍须下一前置条目；当前不创建/复制/启动。原bundle/preimage两manifest不改、false仍false，需真旧binary与前镜像启动→新宿主副本迁移提交→停净→恢复旧资产+前镜像副本→真旧宿主比较，并另写安全hash/count证明，不以codec fixture代替。Wave1及Wave2门禁继续保持。


### 22.135 R9真实架构红与两既有shared组合seam最小修复准入

- Root Architecture真实21:55:54 build退出0；21:55:55.501～21:55:57.607 default run退出1、164/1，21:56:06.453～21:56:07.308诊断仍退出1；唯一R9 Services引用Adapters命中本批SettingsHotReload85DA。SQL137/IO62/Domain268/maxsource1764仍守住，Safety/Scope未跑，不放宽任何护栏。Root另Mood父恢复子21/0但stderr一条真实预期mood_text_write_failed，只故障点捕获并严核固定诊断、不动父空stderr原则。源缺陷与fixture诊断分开归因，不拿旧架构绿充本批。
- 00只读查实际ArchitectureProbe/Program.cs599～604与HotReload：using BotAgent.Adapters.Persistence只为BotConfig.ConfigurationEffect，另两个StorageModule::BotAgent.Adapters.Persistence.MoodStore具体字段/Bind参数是新反向依赖。不是调namespace alias隐藏字符串的修复；不改扫描、allowlist或阈值。一次只读误猜DependencyChecks.cs不存在退出1，已定位真实Program，无产品/数据操作。
- Root明确释放仅原HotReload输入85DA3A17D83C5AFED1BF756DF6DA4DD008B270AA5AB507B4311492052AEF2549与Comp输入1516E646937C0B8F672F346DF09995214FCE9E833959E149D1D6CCDB4943D949。Bind原MoodStore实例参数改为Func<ConfigurationCaller,ConfigurationOperationKey,FieldEdit<string>,LegacyActionResult>，实际CompositionRoot闭包持唯一真实Mood并调用现Bridge.ApplyLegacyMood；同internal Bind末尾追加Func<ConfigurationFieldId,FieldEffect>，Comp传真实BotConfig.ConfigurationEffect。HotReload只消费两个typed能力函数，不认识具体adapter；既有RequiresRestart语义、流程/原public ctor/participant七ctor/Core公开契约不变。七个绑定实参逐一ReferenceEquals，disposed lease保护保留。不新文件/第二接口/SQL或控制器，不放宽R9。
- 先本条登记才实施两准确段，完成重冻报全文hash；Root原测试若有Bind反射只更真实私有签名，无公共入口变更。其它产品继续freeze、00不build/test，Root唯一必要验证。后续whole rollback仍另前置输入登记，当前未演练。


### 22.136 真实旧产物加前镜像三阶段回退演练准入（尚未执行）

- Root确认仅原owned tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs添加唯一closed factory --configuration-whole-rollback <repo> <capture>；00当前实测input821BF19B33AA084C4E4205F43315EA1BC4C2608174FD87C255573E35C584CD06。Root写前核input，若自有前序fixture更改已不同，先续登记新指纹，不逆写/覆盖。无新测试路径/公共Program/产品源码，本条后才实施；00不构建/启动。
- 派生写权精确仅Path.GetTempPath()/botagent-configuration-whole-rollback-<32hex>首次全新staging，及原capture %WORKDIR%\bot-agent\.git\agent01-wave1-rollback-b3a84e32821f442cb72d0d7aee14d670/rollback-proof-<同32hex>.json（exclusive create、拒已有目标/reparse/越界）。原bundle37与manifest3FC9BC4DC7E8B91BAAFFC1604B0E8B8A47E51F62003720B28494F02E37279542、preimage954F62E5721B3BA96EC1DBC377A2DEA1000DA589F5D3E3790DDF1717D448238D、preimage-manifestE925741572580BE05DC685A1BF39C799B7B2636426D055622CCAB3AA1A54F955全只读，原两false布尔不改。37asset按manifest相对路径逐hash复制/核验，不全目录复制或混用新Storage/DLL；不得复制真实DB/env/config/log。
- 固定三阶段：先旧37bundle+前镜像副本真实启动捕获safe基线→停止且实际exit可见→同staging的新完整候选启动并迁移/合成typed配置与秘密提交→停止所有自有进程/非pool连接关闭→精确恢复旧37产物与原前镜像副本→再次真实旧Headless启动比较settings/device/provider/六固定槽受控hash/shape。旧产物须dotnet旧DLL/真实native依赖启动，不用新C3/codec fixture冒兼容；初次旧artifact兼容也必须真验，当前仍false。只允许本次staging内RuntimeRoot/data/qqchat.db及其-wal/-shm恢复处置，先实际路径边界验证、进程全停/自有连接关闭与必要清池后才能覆盖；绝不全局kill或递归清未知目录。恢复不碰原前镜像/真实数据。
- 每个子进程清继承BOTAGENT/QQCHAT/OPENAI/FEISHU/MINIMAX/TTS名称后仅固定synthetic/mock/loopback环境，关闭真实协议/主动任务/bridge外发；原preimage是physical9/version0、六slot6、primary disabled、旧六字段device且无typedpolicy/provider-origin，不提前灌新meta/sentinel。旧GET可能保旧plain Token路径，测试只在内存比hash/shape，绝不回显/写证据正文或secret。新proof只有相对名/hash/计数/版本/exit/时间/布尔，真通过再独立记事实，不预置RollbackVerified=true。
- Root两次只读误猜DataRoot.cs/Storage ModelProviderStore.cs导致rg退出2立即停，后已枚举定位真实Storage/AppPaths.cs及Headless适配器ModelProviderStore，不算产品失败。Root唯一builder等22.135两shared重冻与本test源就绪才验证；当前本条只是准入，不宣称回退通过/Wave1准出/Wave2开启。


### 22.137 R9两原共享source重冻（待真实架构验证）

- 22.135准入后00只改两个释放片段并重冻：SettingsHotReload455行/SHA592FCC5896FB41930B71E1D19D0FA8C26C685014DCB31A3EF1D3152011B883B5；CompositionRoot819行/SHAE928C7352989702AB12239B89F8F79C536CFCFD32DF5C7A834DD4A3B80FF46A3。using具体Persistence、MoodStore字段/参数、BotConfig反向依赖全部真正移出用例层；实际Mood调用在既有唯一Comp闭包，effect来自同一真实BotConfig函数，不只是改alias/分割关键词规避扫描。Bind七个实参全null-check/ReferenceEquals，原公开ctor/participant七ctor/ApplyState与业务流程保持。
- 源静态pwsh/Python/scoped diffcheck退出0（仅现CRLF提示），Root测试实搜没有BindConfiguration反射调用，rg无匹配退出1为预期空结果，无需猜造fixture改签。00未build/test/启动，真实R9/HTTP回归仍由Root唯一执行后记，不拿“No Adapters字符串”静态事实代替架构通过。
- Root whole rollback输入821BF19B33AA084C4E4205F43315EA1BC4C2608174FD87C255573E35C584CD06与22.136闭合路径/三阶段已通知准入；原37bundle/preimage四对象只读，未把准入当执行。其余五shared加app6F5E、Root产品保持冻结，Frontend单路径授权仍待负责人。不额外开SourceInstance/PluginManager或Wave2任务。


### 22.138 真三阶段whole rollback与R9恢复（其余门禁仍待终态）

- Root session16745当前已完事实：Headless trueRebuild22:00:52.281～22:00:57.186退出0/8既有warning，Review build至22:01:02.703退出0；whole-rollback22:01:02.703～22:01:23.092退出0、23/0。实际三次独立宿主旧baseline→新候选迁移提交→精确恢复旧37产物与原前镜像再启动，不是codec模拟或只重开新binary。Architecture22:01:23.092～22:01:23.921 default实际退出0、165/0，SQL137/Domain268、R9不放宽；Safety build至22:01:27.801退出0，后续run/Scope尚待终态，不预报。
- 新ignored proof精确capture/rollback-proof-ae8f5e10651541d0a33690825f53c1d5.json，SHA22DE923A60A00F86141877DDAE22290E5754C29E15D24EB6047C2C8CC89A22D1。00只读复核hash/JSON形状，字段仅Format/time/前后asset和DB hash、版本/计数/ProcessExits、两布尔及settings/fixedslots/provider/device观察hash，无已知合成秘密前缀，不读取DB正文/GET响应。新proof OldBinaryCompatibilityVerified=true/RollbackVerified=true；原manifest3FC9BC4DC7E8B91BAAFFC1604B0E8B8A47E51F62003720B28494F02E37279542、preimage954F62E5721B3BA96EC1DBC377A2DEA1000DA589F5D3E3790DDF1717D448238D、preimage-manifestE925741572580BE05DC685A1BF39C799B7B2636426D055622CCAB3AA1A54F955实测仍原hash，原历史false不改。此为明确本候选/合成前镜像证据，不自动授权生产回退/部署。
- C4只读范围核对：当前src仅Core/Domain/Ports/IManagedCapabilities.cs:11声明，无实现/真实consumer；ManagedCapability surface只证类型。总规划94明确Wave1为Core/Storage接入与统一管理/保存契约；冻结候选912明确C4真实动作implementation按各波次，871明确后续真实adapter/mocks/取消/释放才Supported=true；本总纲21.4下游项也把真实生命周期/管理/取消/释放放各波次。21.4准出项明确C3全保存、secret、legacy发布、Token/Device、安全Read、Host/回归及整套回退，未写Wave1实现全部IManagedCapabilities runtime/新ZBA，不能倒灌后续全管理范围。
- 也不能据此冒C4已采用：冻结候选867～875的安全List/Inspect目录+auth/真实state、Validate独立权限与同Save只读归一、Unsupported零IO、mg1总预算/持久受理/query/cancel仍缺真实宿主证据。§8末条保留“C4协议/type是否足以01管理准入及谁实现unsupported adapter由真消费者确认”；六角色同hash签署并非实现成绩。建议Wave1最终报告明确C4契约/签署已交付、runtime按后续波次并请负责人确认准出边界，不自行补假readonly catalog/擅扩当前source，不提前开Wave2。00此次仅只读/文档，误猜一次ManagedCapabilities.cs复数路径退出1，实际已枚举定位ManagedCapability.cs与Ports声明，不算缺接口或产品失败。


### 22.139 完整Review823、Safety/Scope通过；S50真实链回归失败继续阻断

- Root session16745最终退出0：Safety22:01:27.802～22:01:40.161为594/0；Scope build22:01:40.161～22:01:42.219退出0，Scope22:01:42.220～22:01:44.148 failures0。whole rollback proof三个自有宿主被夹具主动停止，ProcessExits各-1，不冒自然退出0；安全事实版本0→2→0，原37assets/精确前镜像不改。本次wrapper23/0/exit0只表示该预期受控停止与比较真实通过。
- Root完整Review22:02:28.885～22:02:52.526退出0、主823/0，HTTP26/writer27/Mood21/audit10/Device32/Policy72/Token17 modes等children均由父真实注册且通过，不把子数加主数。Root test FE924ABB7C7017A611F462A070B190933147746F3D1A1B2C12304402C0CE7AB1；Harness build至22:02:53.579退出0。22:03实测HEAD原值、展开376/20暂存删除、LFjoinSHAD5E4C54AB20A7A7AE5481A1719D178DA16D1BAA43A1B265FC0CE9AD110DD4850，契约7B7C不变。
- session27182最终退出1：S50 22:02:53.592～22:04:26.505真实10/4。Webhook接收/鉴权/超限/16槽/dedup通过，但无Feishu REST回复/内容目标、真实synthetic storage故障修复后相同事件不回复及重启fresh事件不回复；S52按非零立即停止未执行。不把旧fixtureflat POST问题泛化为此项：S50不保存设置，是当前产品真实链回归，故不能称唯一Frontend阻断。全产品继续冻结，00只读定位不修源/不build。
- 00实际链候选：Activate后typed RuntimePlatformPolicies.HasValue→ReplyPipeline/WhitelistGate.AllowsKey typed→AllowsCanonical通用Matches/WhitelistPolicy.ParseWhitelist把Feishu raw chat whitelist当QQ数字解析；Gateway已按真实Context+raw名单接受，主链却按alias数字拒绝，吻合Webhook200/dedup成功但不回复。Model/固定env凭据路径暂未见首要异常；未经真实反例/恢复验证不称根因已闭合。最小建议三原路径Gateway窄只读OriginalOf+复用IsAllowed raw/alias、WhitelistGate trusted Func gate、Comp真实实例注入，保canonical唯一/noInstance override/loaded/开关/名单空否决，禁止typed Feishu直接return true/伪来源envelope；Gateway旧临时写权已交回，未本批释放前不写。Root并行只读取合成证据，不读真实日志/数据。
- C4范围Root已确认冻结912按各wave实现，最终报告分层：契约/六签署是本轮证据，当前IManagedCapabilities只有Core定义不是runtime采用；后续各wave仍要实际producer/consumer/生命周期/query-cancel与取消释放验收，当前不扩大做Unsupported catalog/不先开Wave2。Frontend单测试授权仍待负责人且红未修，S50真产品红及未执行S52同样阻断21.4最终准出。


### 22.140 S50真实Feishu raw/alias名单消费修复方案与先红准入（产品暂不释放）

- Root接受三原已授权路径最小方案、明确此刻只登记不编辑产品：Platforms/Feishu/FeishuBotGateway.cs inputAC3339B0A86B6ECEB992D634CFABC8140033C2FEFCBE3BF243359A0362D78180；Headless/Services/Qq/WhitelistGate.cs input0B072FBA00EACA5E3BEE0856B8416D406B637AFE7D1EEAD7AF6F4E36E90CA57F；Headless/Host/CompositionRoot.cs inputE928C7352989702AB12239B89F8F79C536CFCFD32DF5C7A834DD4A3B80FF46A3。先Root原owned ConfigurationContractTests.cs inputFE924ABB7C7017A611F462A070B190933147746F3D1A1B2C12304402C0CE7AB1新增closed --configuration-feishu-whitelist及必要父入口，无公共Program/新测试路径。产品释放须该真实红终态后Root另明确指令，不自行开工。
- 精确新增Gateway只读public bool AllowsMappedTarget(bool isGroup,long alias)：一次Current快照，disposed/alias<=0/未配置拒，typed enabled/chat再核，现private_ids.OriginalOf(alias,同app,真实group/participant kind)失败拒，再复用private IsAllowed(settings,isGroup,raw)的raw/alias规则；不返回raw/秘密，不新增身份分配/HTTP/dedup/生命周期/route。WhitelistGate保原两参ctor，再加三参可信Func<bool,long,bool>，仅canonical Feishu在原声明唯一、无Instance override、loaded Registered/Connected/Enabled/Chat和规则开关门禁后调用；无delegate或异常failclosed，不走QQ数字ParseWhitelist，也不typed Feishu unconditional true。CompositionRoot仅同次真实gateway实例methodgroup注入，未加载null；不从channel构造authenticated来源envelope，Wave2真实来源传递仍未覆盖。
- Root先有界16项真实FeishuIdMap+Gateway+Gate合成：raw group/private匹配；不同raw/Explicit空/未映射/跨app/身份kind/disabled/chatfalse/notloaded/无委托/多实例/Instance override拒；AllowAll仍需真实mapping、零HTTP等。只在新ctor/方法未落时回用原两参ctor，让既有行为形成红，不把缺成员或编译失败冒红；真实S50已10/4仍保，修后独立Headless重建+影响S50/S52，不重复无关矩阵。
- 最终whole rollback只在所有源（含已获批前端修）稳定后唯一复验，现23 proof为历史候选不改字冒最终；新nonce proof保留旧证据。当前Frontend测试精确授权仍未新答，产品全freeze，00不build/test/不写以上三源。


### 22.141 Feishu真实typed Gate红与三原路径source释放

- Root first Review build22:09:51～22:09:54.958退出1仅测试漏DQ.Channels using两错误，改using未动产品；Review build22:10:15.999～22:10:20.382退出0。真实closed --configuration-feishu-whitelist22:10:20.386～22:10:21.890退出1、10通过6失败，原typed gate确走旧两ctor，raw group/private被canonical数字比名单拒绝，AllowAll对未映射/跨app/kind及无trusted mapper却可意外允许。不是缺新成员的假红，不替换原产品为mock。
- Root明确释放22.140三个精确input AC3339/0B072/E928；00此刻再次实核全文SHA后才实施。严格仅新增Gateway真实alias→raw→原IsAllowed的只读helper、Whitelist两ctor兼容+可信delegate三ctor/canonical分支、CompositionRoot同次实际gateway注入；typed enabled/chat核actual Context、disposed/配置/身份/Explicit空名单均保拒绝。Root16必要用例及父入口已源注册，当前无native；其它产品/test不编辑，不改guard/阈值/公开Core契约/来源envelope/协议。完成重冻后只Root唯一构建/目标+受影响S50/S52。


### 22.142 Feishu三原路径最小修复源码重冻（行为仍待终态）

- 恢复原00席位后再次逐hash核22.140三input完全匹配，确认22.141已源释放且此前尚未产品编辑；只同三准确段实施，不重复旧红/不写Root或Frontend/不build/test。Gateway输出769行/SHA3E79AF6EA678A3D7A978BBF3E57A0D5566B5489EC7889FAD5B4420FAD32DB6E1；WhitelistGate271行/SHA19FBC1154359E990019D1F4B6911D22C9CF75B77CFF1E60F964CDACCAF5DC3E9；CompositionRoot820行/SHAA1B0A106F46D680CA006515F5F776B2E49C7AA8104704B741A9E36518C2AB590。
- 实际共享接线为同次Build持有的真实feishu.AllowsMappedTarget methodgroup→Whitelist三ctor可信函数；原两ctor保留委托null，canonical原声明唯一/无Instance override/loaded/enable/chat后才调用，缺mapper/异常拒。Gateway一次Current，disposed/alias<=0/Configured拒，typed实际Context rules或旧Read enable/chat再次核验，OriginalOf同app+真实group/participant kind失败拒，再复用现IsAllowed raw/alias匹配；不typed无条件true、不制造identity/网络/新dedup/来源envelope/协议。不修改QQ/local/official的原名单匹配。
- scoped diffcheck退出0（仅既有CRLF提示）。00额外一次纯内存逆向静态尝试在首个Gateway全文SHA未精确回AC3339（得到350A9F973598833F2A363D5C8C07AE4C0CD41C40E94DD5DA0E40B438BBF012C4）即assert退出1，后两个未执行；无产品逆写/临时源码/总纲部分写，不反复试凑证据、不称该逆向通过。输入原hash断言和限定替换成立，但逐字节逆证目前缺；不把其猜归行尾/BOM或冒产品行为失败。回退只可逆本段且须另核后续依赖，逆回会重引真实raw/alias拒与未映射AllowAll风险，不自动授权生产回退。
- 三源已重冻并通知Root，Root只读复核seam/diff0后单一session11757按Headless Rebuild→Review build→16专项→Harness build→S50→S52逐exit/红即停；只影响链，尚无终态不预报。其它源/护栏/Core合同保持，Frontend精确probe.mjs授权仍待负责人、未擅自认领；旧whole-proof仅历史候选，所有源稳定后最终唯一回退与其余21.4证据另记，Wave1/2门禁未解锁。

### 22.143 S50真实恢复、S52真实行为红及兼容修复只读结论

- Root session11757已终态退出1：2026-10-16 Headless trueRebuild12:30:31.373～12:30:49.151退出0/8既有warning；Review build49.152～55.313退出0；真实mapped-gate专项55.314～58.676为16/0退出0；Harness build58.677～12:31:00.065退出0；S50 12:31:00.067～12:31:05.169为14/0退出0，原10/4恢复。S52 12:31:05.170～12:31:29.321为79/40退出1，119原断言保留；本轮不是仅Frontend fixture阻断，不能称Wave1准出。三产品保持22.142输出冻结，00不重复测试或重修已恢复S50。
- 只读真实根因：Platforms/Governance/PlatformSwitchSettings.cs:15 typed存在即Read返回false，EditablePolicies:72/112、Panel WebUiServer.Local.cs:25/58及Platforms.cs:69真实消费，造成四canonical旧GET错误以及local已构造但inject403。SettingsStore.cs:918～920遗漏Feishu同时Chat/local roster双toggle，且whole table已替换后仍无条件Sync导致legacy scalar反盖canonical；Sync缺entry直接return也丢合法旧alias。Web.Settings.cs:405～419 pure whole DTO导入与scalar装配未恢复旧优先级。已有Bootstrap Normalize只处理legacy snapshot，Activate后typed权威并未做安全兼容投影，不能改回从旧DTO重建typed或把resolved值写回inherited baseline。
- Panel WebUiServer.Platforms.cs:36/62仍构造empty Instance上下文做status Resolve，飞书实际声明feishu-main因此exact拒绝。该显示链应按真实snapshot.InstanceId或唯一可信declared key构造完整Context，unknown/歧义/未声明拒；不得为UI改Resolver.Resolve(empty)推断来源。status只显示desired/loaded/capability事实，不给消息/工具授权。普通exact及channel-only安全停止线保持。
- 推荐同一闭合补丁（尚未产品源释放）：Switch.Read仅四canonical、可信声明唯一、无同account Instance override时投影AccountDefault typed规则；未typed走原pre-v1 AND/schema1覆盖，未知/多实例/Instance override拒。EditablePolicies typed直接构造只读四canonical兼容DTO，不回查/恢复旧DTO且不改输入。Store Legacy whole-table Keep才Sync真正出现的旧toggle：Feishu双开关、Local roster双开关，目标缺失时创建其余规则Inherit的最小AccountDefault；whole表本身优先，Strict语义不变。pure LegacyPlan在body显式wholeDTO时做旧alias优先归一/名单映射，不套Strict冲突规则。Web旧flat payload只做兼容投影：canonical开关来自typed requested规则、restart scalar需要同TX desired事实，不回写runtime baseline、不谎称热应用；strict Read仍保requested/effective/Apply真实分离。Comp启动工厂按typed真实规则决定构造optional adapters，未typed保旧分支，不更改FieldEffect.Restart/secret捕获规则。Panelstatus使用完整可信Context，保原reasons/connected/restart语义。具体源段须新定向红后再释放，不在本次只读中开工。
- 一次只读误猜tests/BotAgent.ReviewProbe路径导致Get-FileHash退出1，随后rg --files查实ReviewRemediationProbe；一次git旧方法匹配误带static使Select-Object Skip失败，后用真实方法名查询纠正。均无产品/测试写入，不当功能失败；后续显式Stop与native退出码检查。

### 22.144 负责人更新范围：两精确测试/显示路径及先红、单文件临时移交

- 负责人明确后续按推荐方式执行无需逐次再问授权，仍不自动开Wave2/提交/推送/部署/生产；任何新增实际路径须00先登记。Root转述此前两精确补批生效：tests/BotAgent.FrontendProbe/probe.mjs input1D3FE07B83DCE2467BA3BFA145C67612263C74B68B55780677374002B4869305；src/BotAgent.Headless/Adapters/Panel/WebUiServer.Platforms.cs input0714D1404EB7A45CDFF43805E215E22865BCE8C4F7451D6E04331CA27812D1AF。00本轮已实核二hash匹配。
- probe.mjs仅该单文件临时交Root实施已说明的真实configuration read/save/find mock与typed dirty消费者fixture迁移；00最终复核/合入。保原保护语义/父stderr/全部实际场景，不降低产品回旧flat、不删断言换绿；不得修改browser-fixture或其它未登记测试。Root自有ConfigurationContractTests.cs实核input36DE6A81A3F4C0D63C68D5C0862EDEC9F16ED1669872228151BE42A57D1DAFFF，准入新增closed --configuration-platform-compatibility与必要父入口，约18有界真实断言：迁移AND/Local baseline、canonical只读及未知/多实例/Instance override拒、Editable不修改事实、真实LegacyPlan→Host participant→Store Feishu/Local/table优先、Strict Inherit/Clear不污染baseline，empty Feishu Context仍拒。不新测试路径/工程引用。
- 00可准备现已授权五产品准确input：Switch A06C3D3CEFA020DA6B6442E2B433A1C232EFABA73DA9CA611F3798A673B07A5C；SettingsStore AF7590D88E31B98CADEC585A43F2EA0460EB56325338982DF39CA2796830F25A；Web.Settings A0C450831F72A69C042C0886AFDCE66CED2870B24960A8D092525B5CF584C1DC；CompositionRoot A1B0A106F46D680CA006515F5F776B2E49C7AA8104704B741A9E36518C2AB590；新增Panelstatus 0714D1404EB7A45CDFF43805E215E22865BCE8C4F7451D6E04331CA27812D1AF。原其它产品冻结，当前仅测试先红准入；源实施仍待Root定向红终态后明确释放，00不build/test。SettingsStore现1732行，仅33行预算，实施须自然精简复用/不压成员绕棘轮或放宽1765。若真实预算/语义需更多路径，先提报登记，不偷写Local/BootReport/HealthReport或Core。

### 22.145 平台兼容20项真实红与五产品精确段释放

- Root Review build12:42:44.920～12:42:54.050退出0；真实--configuration-platform-compatibility12:42:54.054～12:42:56.058退出1、12/8（20项），Read QQ/local、Editable四行、Feishu双toggle、local roster两case与whole优先/requested roster失败；Strict scalar/Clear基线、只读纯净、exact Feishu空Instance拒等原安全边界保留。非缺成员/编译假红，00不重跑。
- Root现明确释放22.144五产品原inputs，仅22.143闭合方案；Node单file仍Root临时写，测试其它Root独占，00不build。whole DTO body归一只克隆body内明确事实，当前requested依据留participant同TX。兼容JSON单独从同一受控读事务的requested JSON+typed表取得显示事实并补可信catalog，不写回DB/Box baseline；启动真实optional构造用typed canonical gate+原credential条件。Panelstatus完整三元snapshot及无loaded AccountDefault显示不作为认证来源；Read仍canonical唯一/noInstanceOverride拒绝边界。Restart/SourceMode/Strict/Public C3/SQL及棘轮均不改。

### 22.146 平台兼容五产品源码重冻（待必要行为验证）

- 仅22.145五精确路径实施并重冻：Switch456行/SHA BF662B59CC811EBC18FC70A0125C8A8EF569FE3880676686EE7459B7BBFAA039；SettingsStore1740行/SHA C9DD37457BE27B0539296C4A4E83E58924CAB9AF23C6C78F3A5E06DF079D07E3；Web.Settings1147行/SHA 07AA94A0475A3DC50F654F868DDABEF7292398B268ACB63C0540417689941067；CompositionRoot822行/SHA 0F16F7CA041CF7CDE1E3CC677B8E7FC9A89C2B3E4D14C52D0A0104C2B2616CC7；Panelstatus206行/SHA 8B37E0D9E5D74ED4CDC397E6373031D9608EF1EF389A82B4ABA3B3749F180F8A。所有起始SHA先逐一assert，未改Root测试/Node/其它产品。SettingsStore仍低于1765，无SQL/Domain/护栏变化。
- Switch typed Read仅canonical+可信有效catalog同account唯一且无Instance override→AccountDefault规则；未知/歧义拒，不改变exact Resolver。typed Editable构造独立四canonical DTO，绝不更新旧DTO/meta或输入。Legacy Plan克隆body仅在显式wholeDTO时按该body可证明的toggle/名单事实归一（含Local roster），保caller body/原secret owner；Store只在Legacy wholeTable Keep时同步旧出现字段，Feishu/Local双toggle、目标缺省建其余Inherit的目标。Strict原分支、Restart效果与用户基线均保持。
- 旧flat输出在PopulatePlatformSettings复用原_settingsRepo.Load同读TX JSON+typed权威表，补同次可信catalog，单独覆盖JSON显示四开关/roster；其余秘密/基础设施仍当前runtime，不把该显示snapshot写入Box或当Applied。Comp optional构造用安全canonical typed规则加原凭据条件；no typed仍旧Read。Panelstatus不再二元map覆盖同账号实例，保每真实snapshot及InstanceId，以真完整Context作exact显示解析；无loaded只AccountDefault显示，connectedfalse/restart按desired，duplicate exact failclosed。不把显示Context授予入站/工具/dispatch。
- 00只做源码与scoped diffcheck（退出0，仅现CRLF提示），未build/test/运行产物，行为成绩仍待Root。Node probe.mjs临时Root修改及其最终交回另记，最终whole rollback仍待所有源稳定后唯一演练；不冒Wave1/2准出。

### 22.147 wholePlan局部变量唯一命名收尾（无行为改动）

- Root仅释放Web.Settings input07AA94A0475A3DC50F654F868DDABEF7292398B268ACB63C0540417689941067的wholePlan pattern局部命名，避免不同if作用域歧义。00先assert原SHA，四名精确改为officialEnabled/officialChat/privateChat/feishuEnabled，无控制流/表达式/输入/副作用变化，其它四产品不写。新输出1147行/SHA待本次命令实测如下；Node尚Root施工，无native窗，00不编译测试。
- Web.Settings重冻SHA D4D314DB4A115A1687C410D6C6042EB2F4C9A06D90829AA59E2A47970536569D，scoped diffcheck见本次退出码；语义证据仍待Root唯一窗口。

### 22.148 typed Policy UI状态显示真实缺口与先红停止线

- Root负责人再次确认按已登记推荐范围执行，无需重复问授权；当前五产品冻结，00仅只读。app.js实测input6F5EDEC68206EEBF029F3F955AEC1CD397860F41EBF83A768D8C80885E582215。loadSettings:2300只调renderTypedPlatformPolicies；旧loadPlatformPolicies:1882只有定义无调用，其/api/platforms获取、在线/未连接/状态暂不可用/需重启标签不再被真实typed UI消费。typed2595～2660只有target标题与六规则编辑，没有状态标签，真实旧可观测保护丢失，不是只probe静态字符串过时。
- 不可直接再调用旧loader：它replaceChildren并创建flat checkbox/whitelist，会破坏typed configurationTarget/Value/Original及六规则Inherit/dirty保真。最小候选仅抽共享status fetch/文案（旧loader与typed复用），typed async renderer由loadSettings await；插入只读状态span，任何成功/失败状态均不得写configurationValue/Original或标dirty。Instance按platform/account/instance完整Ordinal键唯一匹配；AccountDefault显示同账号真实loaded/online汇总而非二元map覆盖或任选。缺行未加载/未连接，获取失败状态暂不可用，重复exact状态不可判定。restart仅来自真实/api/platforms.restartRequired，不用现PolicyViews请求态粗粒度RestartRequired推断实际需重启或热采用；能力缺失显示未知，不当false。不得新建路由/放宽权限/改变保存wire。
- Root将在原probe.mjs临时席位补真实typed DOM行为红，00此刻未改app.js/其它产品、未build/test；只读方案不是源释放。Root明确红终态后另释放app.js该准确段，完成重冻后唯一Root串行必要验证。Node单路径迁移仍Root施工，00最后复核交回，不能把新fixture存在当Frontend已通过。

### 22.149 Wave1到Wave2A/2B最终门禁只读矩阵（当前仍关闭）

- 21.4真实准出范围未缩：全七分区/registered对象/允许秘密同TX、CAS/幂等与receipt/floor/预算/故障并发；唯一writer与所有legacy写者参与版本、同锁条件发布和8steps真实结果；Token GET/UI write-only及旧空整批拒/显式Delete；Device stableKey/tombstone/hello/task/control持久闸；安全Read/脱敏/nameRaw原key；公共测试真实注册、独立Headless Rebuild真实宿主链、完整适用回归及合成迁移/整套artifact+DB回退。当前已有分批真实证据见22.123～147，不能拿旧指纹通过作为新五源/最终app候选已验。
- 当前必要缺证/阻断：①22.145红后五产品兼容修仅source/diff0，20专项、保原119 S52及其必要影响链尚无新绿；②Frontend旧probe仍历史红，Root迁移中，并发现148真实typed状态缺口待红→最小app修→Node真实行为绿；③所有源/test稳定后的单次完整适用Review/Architecture/Safety/Scope与相关Headless Rebuild/宿主影响回归，命令/时间/exit/计数须绑定最终hash；④已有whole rollback23 proof绑定旧候选，仅历史证明，最终候选仍需唯一三阶段旧37asset+原preimage→新迁移提交→停净恢复旧产物+原preimage的独立proof；⑤00最终逐21.4汇总工作区/提交/契约/全部写者/源与宿主采用/覆盖未覆盖/回退局限，明确父入口和原断言/棘轮未放松后再提Wave1准出。无证据的项不预报，既有真实通过不反复无关重测凑数。
- C4边界按冻结912及Root已确认：本轮交付契约/type/六角色签署；IManagedCapabilities仅声明，无runtime producer/consumer，不冒采用。真实生命周期/管理/取消释放按各后续wave验，不能将整个C4/新ZBA/Web倒灌成Wave1额外实现，也不能称interface存在满足后续动作。Wave2A/B只在Wave1真实验收完成后由00登记非冲突02/03路径/接口准入；平台真实source-envelope/跨实例入出站授权和模型真实传输/路由与取消等仍其wave义务，当前新增exact registry/安全gate不等完整迁移。Wave5/Web仍按总规划独立后端/ZBA/安全/资源/回退门禁，当前不提前开启Wave2或Web、不授权commit/push/生产。

### 22.150 typed状态真实DOM红与app.js单段释放

- Root node原命令2026-10-16 12:53:46.374～12:53:54.379退出1、350/7，其中4项是真typed DOM缺状态（不可用/断线/已连/需重启），另外3项Root独占旧全表fixture残余迁移，不混为产品失败。现释放app.js input6F5EDEC68206EEBF029F3F955AEC1CD397860F41EBF83A768D8C80885E582215，仅共享status fetch/文案、typed async/span及loadSettings await。保旧loader兼容，不调用其replaceChildren重建typed；不触六规则/Original/dirty或Root测试，不build/test。
- 实读Root新fixture初用requiresRestart/loaded，但真实status只restartRequired（无loaded字段），已通知并由Root更正fixture，产品不得添加虚构wire兼容。只读state置于title内，不改变三态编辑控件结构。完整Instance exact唯一匹配，duplicate不可判定；AccountDefault汇总数量/在线/any真实restart不任选。能力未知不冒不支持。

### 22.151 typed状态最小修复重冻（待真实Node验证）

- app.js仅22.150释放段完成并重冻5792行/SHA 72DC3052647B35A2678AC40B166F17A267A89AC018F7780174C5AE3617667BCE。fetchPlatformPolicyStatus共享同一/api/platforms获取，固定诊断不回显错误正文；旧loader保原编辑逻辑，仅共用获取/状态文案。typed renderer变async且loadSettings await，状态span放title内，不改变两个三态/名单/action/features控件、configurationValue/Original、collectDirty、基线、key/secrets/Mood流程。
- Instance完整三元唯一匹配；AccountDefault保全同account行汇总实例/在线数量及any真实restartRequired，不二元覆盖/任选；重复exact显示状态不可判定（不可用），不冒在线。断线/在线/需重启/不可用恢复真实只读显示，能力字段缺失或各实例不一致显示未知，不把部分支持说成全不支持。未为fixture添加requiresRestart/loaded假wire兼容。
- scoped diffcheck与输出hash为静态证据，00未node/build/test；实际原probe及20兼容专项/受影响S52等待Root唯一窗口。Root当前实核工作区377/20暂存删除，LFjoin SHA21C590A9A9733B4EBFA04F60D758AD5CA93B216448655DCDCE946F2B50F47E8F，HEAD原值；不继续冒旧376当前。前五产品仍22.146/147冻结，Node单文件Root临时修改尚待交回及00最终审查。

### 22.152 Frontend fixture差分复核与最终有界验证计划

- Root临时probe.mjs 5822F5CE76DC85FA035846A93D0903533B05215BFC14E61C87E16C6E19493970曾暂冻结，00只读抽核429增/221删：旧flat全表传值断言迁为真实strict闭集五成员、142字段目录/dirty/Keep；不改控件就不发送的断言仍证明原值保留，不从snapshot填补实际未发送请求。设备不完整拒/稀疏目标六字段/Explicit空与AllowAll/Inherit/无额外动作授权、官方Secret只读以及late secret ack和独立main/Mood Find事实均有新消费者断言，未让新UI退全表。只抽核非全量审完，不提前接收未终态文件为最终通过。
- Root最新真实node 12:56:14～12:56:22退出1、351/16；其报告2项状态fixture把Inherited resolved value错误设0、其余race之间未显式刷新而沿旧ExpectedVersion引发409，现仅Root修fixture，不要求产品绕CAS/自动覆盖late draft或status生成权限。原2条disabled/muted区别在线/断线的文案保护仍建议在现typed段保真补回，非新测试矩阵。00读取测试含符号时Python默认gbk stdout编码错误退出1，无写；随后显式utf8只读成功，不当产品失败。
- Root提出最终一次顺序证据计划，00按21.4认可有界范围：先Frontend真实probe；全源冻结后Headless Rebuild→Review build→compat20→mapped16→Harness build→S52/S50/S21/S36；定向绿后一次完整Review+Architecture+Safety+Scope，最后唯一新nonce整套wholeRollback23。逐native exit/失败停止；不扩无关IT矩阵，不把父注册编译/数值棘轮声明当真实run，最终回退proof必须绑定最终候选。既有Production等不受此小批影响的历史证据原样注明，不伪报本次重跑。
- 完整准出仍须22.149逐项事实汇总（含全部旧writer/fence、秘密/Token GET/UI、设备、统一保存/发布真实Host链、覆盖与局限、冻结hash/状态及旧37/preimage最终回退），不能先宣布Wave2。02/03后续真实source/transport及C4runtime按各wave；无需为Wave1自行增加新fake capability实现。当前所有产品仍freeze，Node尚Root修复未交回，00无测试/构建/新产品写入。

### 22.153 Frontend真实370恢复与临时单文件交回

- Root node --check 2026-10-16 13:01:03.724～13:01:03.817退出0；原probe13:01:03.818～13:01:11.913真实370/0退出0，源码app72DC未变。补回停用/静音两文案行为；“无getter字段Keep”和“已登记必需DOM被移除则整批拒/零POST/保dirty/draft”分开证明，不误称支持任意partial DOM。probe最终SHA00D0DFB9067B09FC3EF4884B569DC9B1AD069B7CDD85073DB5748CB4CC2319AD、scoped diff0；Root正式交回本单路径，00接收最终集成写权，不含其它Frontend文件。先前366/1剩余requiredDOM结构破坏测试口径经实际source2502/2759确认，不修改产品或删除保护换绿。

### 22.154 平台影响链恢复、S21绑定维度真红与三路径准入

- Root唯一C#窗口：Headless Rebuild12:59:05.501～12:59:25.546退出0/8既有warning，Review build25.547～31.948退出0；compat20/0至34.469、mapped16/0至34.596均退出0；Harness build至35.806退出0；S52 35.808～43.372真实119/0退出0（原79/40恢复），S50 43.373～46.636真实14/0退出0；S21 12:59:46.636～13:00:19.267为35/2退出1，S36按失败停止未执行。不是仅Frontend旧fixture阻断，亦不把S21旧热期望直接改名当绿。
- 真实链：Comp启动ProviderRunner捕获metadata；当前TryBuildCurrentProviderRoute719～725 sameReadTX对desired binding与captured binding不同即deny，primary URL/model仍由BuildProviderRoute760/763读Current动态字段，legacy相同secret:apiKey读取已commit值仍即时。S21从env起步且一次POST同时URL/model/APIkey Replace，primary env→DB绑定变化触真拒绝，B0/A1（A1保存前）。owner允许binding显式apply或重启，不允许伪ModelApplied。现HotReload249对只SyncBrainFields（人设等）直接报告ModelProjection Applied，RequiresRestart181不覆盖该实际主binding变化，是真结果缺口。
- Root自有test原B977376A2EA3081EC79679D67745151C03447597FE4BB22B79A68456C1E6EDDB准入新增唯一真实HTTP断言（父26→27），实际输出E1CC40F82CDD3002230078AF2D76C5928F7769F745FD71213506F17E25C2F969。Review build13:02:56.821～13:03:01.756退出0；HTTP13:03:01.757～13:03:04.221退出1、passed5/failed1，固定actual_env_to_database_binding_commit_reports_model_restart_not_false_applied，原首strict env起步slot0 Replace真实路径，非缺接口红。
- Root现释放00原Comp input0F16F7CA041CF7CDE1E3CC677B8E7FC9A89C2B3E4D14C52D0A0104C2B2616CC7、HotReload input592FCC5896FB41930B71E1D19D0FA8C26C685014DCB31A3EF1D3152011B883B5，以及新限定测试路径tests/BotAgent.IntegrationHarness/ModelConfigScenario.cs input61A8369AA50E56A23F2390D5792283D2E760CD03F4DA4E054F0350EFB5D1204A。00最终写三路径；Root测试仍Root独占、app/Node与其余产品冻结，00不build/test。
- 精确私有seam：BindConfiguration仅新增Func<ConfigurationVersion,ApplyStepReport> modelProjectionState，同引用/null完整核验，原public ctor/公共C3不改。Comp复用同conn Version+LoadAllInTransaction，以本提交version及真实runner同份captured metadata对比binding/启用/priority与非primary URL/model；primary URL/model仍Current，不读秘密值/新SQL/新连接协调器。版本漂移或观察失败报Unknown，不先Applied再覆盖；RestartRequired表示捕获routing待重启，不新建runner生命周期。HotReload仅在原Model Started后作真实判断+SyncBrainFields、一次记录真实结果；registered结果亦保该未完成维度，不由DB凭据实时消费冒metadata Applied。
- S21只作该新契约适配：env→DB一次绑定变化保持commit/持久/不泄密，新增真实receipt Restart及路由拒绝旧/新端点零发送证据；原两热URL/model保护不是删除，移到第二次真实启动已captured secret:apiKey的稳定ref前提，仍检验即时正确新URL与模型名，之后恢复原B/model继续快速档/思考/重启原断言。无其它Harness源/新测试矩阵/阈值改动。最终全绿与整套回退仍待真实终态，不宣告Wave1/2准出。

### 22.155 主模型捕获差异真实报告与S21稳定引用保护重冻

- 仅22.154三个准入路径完成重冻：HotReload467行/SHA15B25EC9B4280503245A5731A1F2E7E63DACFBA31EF2F6BAE2F33A460EF227B6；CompositionRoot836行/SHA859229747BDAA85E9B935F6F4C89F01AE7DBBA33ED5E8559627DE8BA895E54B7；ModelConfigScenario321行/SHA54F072E0EAEBD5DBE325787E5513E6E63E0E936E017CCA29988B07A80AFDBEBE。三input逐hash assert，未写Root测试/app/Node/其它产品；scoped diffcheck退出0（原CRLF提示），00不编译测试。
- Bind新增内部modelProjectionState可信delegate，null/重复绑定逐引用一致检查，原public ctor/所有C3类型不变。Comp真实runner/route构造现在直接消费同份capturedProviders数组，去掉原第二次LoadAll，报告与真实consumer不分别猜两份启动记录。InspectModelProjection复用现sameReadTX/Version/LoadAllInTransaction，对本提交version和captured元数据ID/引用/enable/priority以及nonprimary URL/model差异判断Restart；primary URL/model仍Current动态，DBslot值不读取/不比较，不把相同DBref已提交credential安全读取当metadata应用。无新SQL/新writer/新生命周期。
- Model已经原preclaim后才判断；判断失败/版本漂移/不合法结果一次真实Unknown，不先Applied再覆盖；SyncBrainFields仍原真消费者，仅成功判断后执行；Model真实Restart也传递Registered未完成维度。未构建新runner，不擅热切换binding，门禁deny仍保。其它七step/条件Exchange/Replay/NoChanges/Find/权限/原效果保持。
- S21不是删除两热保护：env→DB一次保存新增真实Committed+ModelRestart回执检查，随后发送合成消息并等固定provider_secret_unavailable安全日志形状，核旧A请求无增/新B为0；原持久化、不泄密、重启B与快速模型/思考原断言保持。原两个立即新URL/新模型断言移到第二次真实进程已captured secret:apiKey的稳定引用前提，实际POST改到A/model-hot-stable-ref、不改key，核Model Applied且真A请求/模型正确，再恢复原B/model继续后续原检查。只本Scenario、原mock/隔离路径，无新Harness文件或真实服务。
- 预期由Root唯一一次必要窗口：原HTTP新增27反例先绿；独立Headless重建后S21应保原37断言并新增提交/拒绝/stableApplied/恢复B等检查（实际总计以运行输出为准），S36随后；已绿Frontend370、compat20、mapped16、S52/S50119/14不为文档重跑。之后最终一次完整适用guards/Review、唯一新nonce整套回退绑定本候选。当前尚无新行为成绩，不预报Wave1/2准出。

### 22.156 模型结果修复真实目标窗口全绿（最终guards/回退待终态）

- Root唯一target窗口2026-10-16所有native退出0：Headless trueRebuild13:07:04.596～13:07:09.624（8既有warning）；Review build09.625～14.720；实际HTTP27/0为14.720～18.137；Harness build18.137～21.554（2既有warning）；真实S21 41/0为21.556～30.037，原两个稳定ref热URL/model保护保留、env→DB真实Restart/零旧新IO新增均通过；S36 98/0为30.038～38.029。与22.154原35/2闭合，不靠删除原保护或把commit冒Applied换绿。
- 绑定最终三hash仍22.155：Comp859229747BDAA85E9B935F6F4C89F01AE7DBBA33ED5E8559627DE8BA895E54B7；HotReload15B25EC9B4280503245A5731A1F2E7E63DACFBA31EF2F6BAE2F33A460EF227B6；S21Scenario54F072E0EAEBD5DBE325787E5513E6E63E0E936E017CCA29988B07A80AFDBEBE。Frontend370实测并交回00、app72DC未改；平台compat20/mapped16与S52 119/S50 14已绿和本模型改段不交叉，保前轮真实影响覆盖而不为记录再跑。完整Review/Architecture/Safety/Scope与唯一新nonce整套回退此刻仍Root执行中，未终态不预报、不先开Wave2。

### 22.157 21.4最终源码/真实入口与写者审计（等待最后行为凭据补齐）

- 00仅只读核当前实际接线：Comp131唯一Host ConfigurationBridge.Activate→真实SettingsStore.ConfigurationParticipant→同一Storage ConfigurationStore/AppDatabase；Comp260在HTTP/bridge/task对外前Bind原同一Box/HotReload。HotReload119/127/134与167连接read/find/strictSave/LegacyWithMood；Web.Settings656/666/674通过同一request-local权限scope真实调用；SaveAndApply持原Box_gate，sameconn核提交version及真实规范事实→Publish/RuntimePolicy/Model Started→条件Exchange→八step。Model已按22.155分清Captured待重启和DB凭据实时安全消费，不用独立Model实例/写协调器伪原子。
- 实核激活/激活中fence仍位于真实原Write内：SettingsStore173、ModelProviderStore59/71、Storage.PluginStore31、Storage.SecretsStore148，六固定槽与官方Secret闭集分开；official历史接口固定拒且不读旧row，Netease Cookie/password非C3不误封。真正旧ApplyRuntimeSettings唯一caller为PanelDeploy.cs271；HotReload325只允许field96 typed Legacy patch，额外mutator/published拒绝；Music63/66沿原非C3 cookie保存+PatchSettings，398仅允许该非JSON cookie投影，不把任意runtime JSON写入宣称统一保存。Mood独立namespace reservation/claim/result保持，不塞配置八steps或夸跨介质原子。每个真实故障/并发/预算/重启证据仍引用Root此前合成tests，不将静态fence搜到当行为绿。
- 公共入口实核ReviewRemediationProbe/Program.cs42真实注册ConfigurationContractTests.ModuleRecoveryAsync；该原factory包含HTTP27、platform-compatibility20、feishu-whitelist16、Mood21/audit10等精确汇总，closed whole-rollback接受已登记repo/capture参数。最终完整run仍待Root终态，不能只用父字面量/编译冒子执行。
- 当前git原生实测HEAD1f3181e21e8d1f9531e41066b033d8d60218d580、展开status378/20暂存删除、LFjoinSHA F48B937C366724AFAF9630EF653E1735550E820B2C0B5034AC9B64DC5576B352。新增S21限定M与此前probe M属已登记；保所有迁移/untracked/原暂存删除，未提交/推送/部署/生产。各批源input SHA先assert/限定段替换与diffcheck记录，Root owned逆向保留证据与00已注明失败/未证明逆向不混；不声称全部既有文件逐字节逆向证明。原37asset/preimage及历史proof不覆盖，最后新proof须独立绑定最终候选。
- 21.4待补仅最终完整适用验证与新nonce whole rollback终态、00按最终hash/工作区汇总覆盖未覆盖与回退局限；C4真实producer/consumer按后续wave不冒实现，SourceInstance真通路及Model完整迁移仍Wave2A/B准入后各角色路径/接口/真实生命周期验收。当前没有“已开启第二阶段”决定。00此次只读误猜Deploy/SettingsBox路径rg退出2后停止，随后rg --files定位真实PanelDeploy.cs与Services/SettingsBox.cs；无产品/测试写或额外执行。

### 22.158 最终完整Review/架构/安全/作用域真实终态

- Root所有命令pwsh/真实工程、逐native退出0：完整Review13:07:59.680～13:08:23.859为825/0，所有已注册child含HTTP27/compat20/mapped16/Token等真实执行，子数不混加主数；Architecture build23.860～25.272退出0，default25.272～27.548为165/0，SQL137/Domain268/maxsource1764，原棘轮/断言未放宽；Safety build27.548～31.687退出0，run31.687～44.827为594/0；Scope build44.827～46.845退出0，run46.846～49.140 failures0，不编造成功计数。全产品/test继续冻结，00仅总纲写入。

### 22.159 最终候选whole proof只读绑定（外层exit未观测，不冒0）

- Root原session53075在新用户消息后句柄Unknown process id，外层exit输出丢失；00不把proof文件存在等同原生命令exit0。只读精确ignored新proof rollback-proof-70d7b41eda544d02987ef76aa8b42ad6.json，SHA0AB7FAD91E3638A141F703988372850593BBC1CB8F43796D7F866C452FD528C3。安全字段仅hash/计数/版本/布尔/退出形状，WholeAssetCount37、版本0→2→0、两verified=true；三个自有宿主ForcedStop=true/ExitCode=-1，是刻意停止事实，不自然退出0。
- proof CandidateHeadlessSha256=5E477261605EC318683C89537DAACED2616EFC10C24E0B98A7A6FC7846C1271B，与当前最终重建Headless真实hash一致；BaselineHeadless5A04、manifest3FC9BC4DC7E8B91BAAFFC1604B0E8B8A47E51F62003720B28494F02E37279542、preimage954F62E5721B3BA96EC1DBC377A2DEA1000DA589F5D3E3790DDF1717D448238D、preimage-manifestE925741572580BE05DC685A1BF39C799B7B2636426D055622CCAB3AA1A54F955均实测不变，37asset逐一SHA精确匹配manifest。对应Temp nonce目录现不存在；源码finally先停止并观察自有process退出/Dispose、关闭mock与SQLite pools，绝对父目录+32hex限定、拒reparse后才删自有staging，不清未知文件/真实data。
- 已证明proof内容/最终candidate绑定与安全清理形状，但缺外层native exit终态仍如实留门。Root为逐命令exit要求建议只必要复验whole一次并将原生结果持久到22.160派生native-results，00同意不重复其余已绿矩阵；新proof另nonce且原历史证据只读。21.4最后准出仍待此终态及最终来源/覆盖/发布边界汇总，不提前开启Wave2。

### 22.160 用户明确review分支落点与隔离发布准备准入（未提交/未发布）

- 负责人新增明确要求可落点提交至GitHub ZhaoJun233/bot-agent 的review/core-platforms，非main/dev/部署/Wave2。Root已查询/fetch真实review tip47e66072419808b219430693908acaadd0598b50，已有Stage1 Core/Storage/Platforms/文档；dev26227b7、main7cc69d不触碰。必须按已存在远端review树与当前脱敏后差异形成白名单，不按本地旧HEAD378项盲stage或纳入所有既有修改。
- 精确派生目录.git/agent01-wave1-landing-0355336c81ff4a7897eb17425f45bb6b/认领给Root独占，可写publish_checkpoint.py、manifest.json、source.index、native-results.json、publish-result.json及其封闭publish/verify子目录；不写外部工作目录/原%WORKDIR%发布脚本/产品/真实git index/HEAD。source.index仅子进程GIT_INDEX_FILE基于已fetch47e树叠经审定白名单，commit-tree创建明确本地checkpoint sourcecommit、导出该commit，不能把旧HEAD/任意未提交工作区假称发布来源；原20暂存删除/index原样保留，禁止stageall/reset/checkout/clean。Root负责实际commit/push，00本席只审计与总纲。
- 原publish-qqchat.py的REPO仍旧qqchat-src、clone硬dev，不能直接跑；允许新nonce adapter只注入明确源repo/sourcecommit/目标review clone与普通push分支，复用原完整脱敏REPLACEMENTS、黑名单与类别双审计以及推后独立完整克隆内容/历史复检，任何审计失败停止。禁止借adapter放宽扫描器、强推、改main/dev或泄露秘密/本地证据；日志只安全码/hash/count，原bundle/DB/proof/.git内证据不入sourcearchive。
- 00只读raw-LF启发比较远端review树与真实存在文件，src/tests37差异+14新增、0缺失，与Root原56路径（另5docs）吻合；该比较明确非最终脱敏diff，git diff远端→本地旧index会把仍untracked的新模块误报删除，不能作为候选清单。实际闭合集包括新Core配置10文件、Storage Store/Bridge/friend和统一治理/必要查询复用、14原Headless共享/Model三guard/五Platforms、架构2精确登记+负例、Frontend与S21/Review真实测试入口；既有Stage1项目引用/旧平台迁移已在47e，不重纳20暂存删除。
- 一项应剔除的纯既有噪声：Core/Domain/Ports/IQqActions.cs与47e真实文件比较仅末尾多一空行，非本次接口变更/闭合依赖；建议不叠入独立index（工作区不改）。README/CONTRIBUTING/.github及其它全部已有修改不可盲纳；五docs须明确选择公开安全交付/契约文档，内部执行台账和真实%WORKDIR%运维内容不按当前总纲可写即自动公开。最终白名单仍以原替换表内存脱敏后的实际diff逐项审定，原始路径/时间/hash证据只放ignored manifest，不宣称已commit/push或发布审计已过。

### 22.161 最终whole rollback原生命令终态补齐

- Root唯一必要补验2026-10-16 17:40:45.876～17:41:00.504 +08原生退出0，whole-rollback23/0、assets37；结果持久于22.160隔离目录native-results.json。00只读实核其exitCode=0与SHA98C7B698002F685708F10FDCE242E516B65D9518B40711F626C1B93C19B003CA。原13:09 proof保留且原外层exit仍未知，不倒写成0，不重复其余已绿矩阵。
- 新独立proof精确rollback-proof-585bdeea1cd54f28bfbe0116793899fe.json，SHA8F2A37B631CF12A2FC2CF18BB6B7A0451BC510B5196D008D2ABDF5ADFAE9B09F，00实核37assets、两verified=true、版本0→2→0；Candidate Headless5E477261605EC318683C89537DAACED2616EFC10C24E0B98A7A6FC7846C1271B与当前真实产物相同，原37bundle manifest3FC9/preimage954F/manifestE925哈希保持。三阶段自有process均ForcedStop=true/-1，不冒自然退出0；新nonce Temp staging现不存在，原capture不修改。该最终候选完成旧whole artifact+原前镜像基线→新迁移提交→停净恢复旧artifact+原DB并真启动比较，不是codec伪旧产物或仅新binary重开。

### 22.162 Wave1技术准出核对与review落点冻结交付（未开启Wave2）

| 21.4义务 | 当前真实结论与证据边界 |
| --- | --- |
| Core/Storage实际接入、全分区/registered/允许秘密、同TX | 唯一Host Activate/同Store/同AppDatabase、真实participant/同conn原语、7分区/6秘密/registered Provider与Plugin/typed Policy/Device已经HTTP与完整825父子入口验证；CAS/去重/冲突/clock/floor/容量/故障/并发/跨代恢复在原合成Store测试保留，不把任意JSON或跨介质称原子。 |
| 所有旧writer/统一版本/条件发布 | 原Settings/Provider/Plugin/六槽Secret写者激活fence；旧POST无CAS走同锁同TX latest事实与原操作key，PanelDeploy96受控typed桥、Netease非C3例外；唯一Box持锁Save→Started→许可Exchange，NoChanges/Replay/Find不重新发布，8step真实结果；Model binding/captured变化Restart、相同DBref已commit凭据即时读取不冒metadata Applied。 |
| Token/Secret边界与UI | Token旧plain受控同TX迁移+marker，GET不载原值，旧空整批拒/Strict Delete/删除重启不回种；官方Secret仅环境、presence拒。Frontend370及HTTP27真实进程覆盖dirty/六规则/稳定key/nameRaw、晚编辑ack、Find/两key Mood不补执行、秘密不入持久浏览器存储；requiredDOM结构损坏拒而无getter字段Keep分开。 |
| Device和安全Read | 稳定Key/tombstone/rename pending-confirm、Hello/selectors/task/control/send gate/late结果/具体Connection绑定真WS与S33旧影响覆盖，重启备份恢复保撤销；安全Read权限/Hidden无值/显示mask与raw存储/key、nameRaw分离；不冒Wave2完整SourceInstance入站/出站路由已迁移。 |
| 宿主/公共测试/架构与必要回归 | 最终独立Headless Rebuild→HTTP27/S21 41/S36 98；平台批compat20/mapped16/S52 119/S50 14在不交叉hash下真实绿保留；Frontend370、完整Review825/0、Architecture165/0（SQL137/Domain268/maxsource1764原棘轮）、Safety594/0、Scope failures0，命令/时间/exit见158/156/153，不混子数或造Scope成功计数。 |
| 迁移/兼容/回退 | 最终161的三阶段whole rollback23/0与持久native exit0绑定当前5E477候选及旧37完整asset/原前镜像；原format兼容/future拒绝/模块初始化原子/DBsecretmode/账本重开证据保留。是合成闭合回退演练，不是生产部署/混旧binary新DB获准。 |

- **00结论**：按21.4本轮Wave1技术准出义务已具备逐项真实证据，可以提报Wave2A/2B的正式准入准备；当前并未启动02/03迁移、未认领其后续源、未部署。C4-0.3.2契约/type/六角色评审属于本轮交付，冻结912明确真实管理/生命周期动作按各wave；当前IManagedCapabilities只有接口而非实现，不冒runtime采用。后续SourceInstance真实envelope/路由隔离、模型完整transport/取消、各wave C4生产者消费者、MCP/资源/ZBA/Web等仍按后续波次单独登记与验收，不能将本轮安全seam冒全部后端完成，亦不倒灌为本轮额外实现。
- **授权与写权**：Root=01本轮owned Core/Storage/测试，00=总纲/逐批共享最终合入；02/03限定共享曾临时移交均已交回，Frontend probe单文件Root迁移00已接收终审。最终source冻结：Comp859229747BDAA85E9B935F6F4C89F01AE7DBBA33ED5E8559627DE8BA895E54B7、HotReload15B25EC9B4280503245A5731A1F2E7E63DACFBA31EF2F6BAE2F33A460EF227B6、app72DC3052647B35A2678AC40B166F17A267A89AC018F7780174C5AE3617667BCE、S21Scenario54F072E0EAEBD5DBE325787E5513E6E63E0E936E017CCA29988B07A80AFDBEBE、Frontend00D0DFB9067B09FC3EF4884B569DC9B1AD069B7CDD85073DB5748CB4CC2319AD、Root testE1CC40F82CDD3002230078AF2D76C5928F7769F745FD71213506F17E25C2F969，00本次再次实核；其余各路径沿相应批次input/output记录，无文件全量回退。
- **提交/工作区与接口版本**：当前唯一源仓库HEAD1f3181e21e8d1f9531e41066b033d8d60218d580、展开378/20暂存删除、LFjoin SHA F48B937C366724AFAF9630EF653E1735550E820B2C0B5034AC9B64DC5576B352。此时尚未创建checkpoint commit/普通push，不能假称已GitHub落点；旧未提交/未跟踪与20暂存删除均保留。本轮public C3/C4 cfg-0.3.2/SHA7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203不变，内部module1/alias codec分支有旧byte兼容，未放宽架构/删断言换绿。
- **发布范围与停止线**：负责人明确仅review/core-platforms落点，Root准备22.160新nonce适配器/独立index与已fetch47e基线，最终脱敏差异52路径待00manifest终审，原IQqActions/field-review空行及2历史规划不纳，不能378项stageall；原真实index/HEAD/20删除不动。原REPLACEMENTS/黑名单/类别双审计/推后完整克隆复检均必须真实通过；源checkpoint只供导出，严禁将未脱敏原始checkpoint直接push。review普通快进，不碰dev/main、无force/部署/生产。任何source/hash漂移或审计失败停止。最终发布结果后只写ignored证据/聊天，不为结果偷改本冻结总纲而导致source manifest漂移。
- **兼容与未覆盖**：旧合法单例/原POST/已有运行链经保原断言验证；新的权限/fence/撤销/端点用途限制按冻结明确收紧。没有读取真实群聊/成员/DB正文/原始协议报文或未经筛选日志；测试仅mock与合成隔离根。未证明实际QQ/飞书生产服务、DNS/redirect全出网防护、完整多实例来源迁移、全C4管理动作、生产资源/生产发布与生产回退；不得借review分支提交声称这些已完成。
- 本总纲至此源码/文档内容冻结，00将单独向Root发送全文SHA作为制作最终52路径manifest输入。待办仅Root安全发布准备与00最终manifest审查/实际发布结果回报；不再改产品、不运行构建/测试、不擅自开启第二阶段。

### 22.163 冻结前最后派生发布命令退出证据路径登记

- 在Root prepare尚未执行、最终总纲SHA尚未发出前，补其明确请求的唯一派生路径：22.160准入目录内checked-commands.json由Root独占，只允许固定安全命令argv/exitCode，不保存stdout/原审计命中/替换表值/环境正文/认证token或带凭据URL；如argv本身含秘密则不记录原值、须先拒绝该命令形式。无产品/外部发布脚本/真实git index/HEAD写权扩张。
- Root适配器已落但尚未执行，prepare/publish分离，待本总纲最终SHA冻结后才制作52-path manifest交00终审，再普通推review；没有审计通过/提交/发布成功事实。原archive/subject显式sourceCommit、clone目标review的3种参数重定向之外，不得删双审计/完整克隆复检或改发布保护。此为本冻结总纲最后内容，后续运行证据仅ignored派生文件/聊天，全文最终SHA以此后实测向Root发送值为准。

### 22.164 发布快照根安全纠正（prepare前，未提交/推送）

- Root执行前预检发现原publisher sanitize/inlinecheck/prune按完整绝对路径识别.git段；实际快照若位于22.160的.git祖先下会跳过这些闸，不能运行或放宽扫描器掩盖。此刻prepare/commit/push均未执行；此前.git目录发布/verify子目录准入仅作为历史提案，实际clone/导出不再使用它们。
- 新精确生成根认领给Root独占：%WORKDIR%\bot-agent\publish\wave1-0355336c81ff4a7897eb17425f45bb6b\，仅release/verify两个闭合子目录供实际快照/clone与完整复检；仍位于唯一工作仓库内，git原生check-ignore确认现有.gitignore的publish/规则覆盖，无需修改.gitignore或info/exclude。adapter/manifest/source.index/native-results/checked-commands/publish-result仍原.git/agent01-wave1-landing-0355336c81ff4a7897eb17425f45bb6b，所有原证据/bundle/preimage保持。
- Root adapter仅重定向PUBLISH_ROOT及自有临时删除boundary到新精确nonce根，不改任何sanitize/inline/prune/黑名单/类别扫描规则，不改变显式sourceCommit/review分支/普通push/完整克隆复检。删除前须解析绝对路径确认位于该精确生成根内并拒reparse/越界，不清理其它publish内容。Root已核双审计按root相对路径扫描，不因祖先publish忽略；必须实际覆盖release/verify内容，不能据check-ignore误当审计允许跳过。
- 此为必要发布安全修正的最后文档增量，重新冻结全文SHA向Root发送，52路径manifest必须使用新的文档hash；未伪称原93483184版本仍为当前输入。之后保持源/文档冻结，00不运行adapter/构建/测试/commit/push；prepare完成仍先manifest终审，任何扫描/差异/来源异常停止。

### 22.165 prepare首轮空白门禁真实失败及三精确blob格式准入

- Root首次prepare2026-10-16 17:45:26.286～17:45:47.309退出1，真实停在独立index git diff --cached --check 47e（该native退出2），尚无checkpoint commit/clone/push；checked-commands与失败结果历史追加保留，不覆盖为后续成功。失败仅三个new EOF blank，不是行内空白或产品行为失败；不删diffcheck/放宽规则/改实际工作文件凑绿。
- 仅允许独立GIT_INDEX_FILE中的三个精确source blob作发布格式归一，保一个最终LF：docs/plans/agents/00-c3-c4-contract-candidate.md、src/BotAgent.Core/Domain/Management/ManagedCapability.cs、src/BotAgent.Storage/LegacyJsonImporter.cs。00只读实核当前原工作文件SHA依次7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203、FC7B5DB4FFD1CB01B52570E40842D0A38ACD3444F8D3288E597FC0EDFEB932FC、F940A244FA03EBF6586A0B3E01435366AE70FE59ED29B587A1963E910F34FCF3，按LF观察尾部换行数2/3/2。禁止实际文件逆写、改冻结7B7原字节/编译产物、泛化任意文件rstrip或清其它空白。
- 适配器必须在这3项上断言仅EOF空行差异（LF正文精确相同，仅尾部多余LF→单LF），manifest逐项记录工作文件原SHA、sourceBlob、commitBlob、eofOnlyNormalized以及原/后尾LF数；其它路径不得被该规则改写。仍运行原独立index diff --check，任何其它空白错误/正文变化都停止。该提交blob只是可逆发布格式镜像，不是新C3/C4接口或重新签署；公开镜像candidate提交blob哈希可能因EOF与7B7源字节不同，不能误称公开blob逐字节等于源冻结hash。源冻结7B7/既有签署/原工作区仍不变，核验应追manifest原源hash与明确EOF映射，不伪造签署字节。
- 本必要说明发生于Root尚未下一次prepare之前，重新冻结总纲全文SHA再发送，取代164的文档hash供52路径manifest制作；不改变52白名单、sourcecommit明确导出、review唯一分支、原真实index/HEAD/20删除保全、实际release/verify无.git祖先及完整脱敏/双审计/克隆复检。00没有执行adapter/commit/push/build/test，原失败命令exit不改。

### 22.166 发布审计输入根隐私校正（审计规则不变，prepare尚未重试）

- Root执行前核实既有_audit_publish.py还递归BOTAGENT_PUBLISH_SOURCE工作树，默认原仓库只排docs/.git/bin/obj而不排真实data/runtime；对原工作树整树运行会违反禁止读取真实群聊JSON/运行数据。首次prepare已在diffcheck停止，尚未执行该审计；不得为了发布便利扫描原工作仓库任意data/runtime。
- 仅通过现有BOTAGENT_PUBLISH_SOURCE审计子进程参数明确每次输入根等于该同次--snapshot的封闭脱敏clone：prepare为已登记publish/wave1-0355336c81ff4a7897eb17425f45bb6b/release，推后独立完整复检为同nonce verify。仅此精确生成根可作为workingtree/snapshot输入，HEAD/历史读取其公开review克隆；不改扫描器/排除规则/REPLACEMENTS/FORBIDDEN，全部候选字节仍完整inline、黑名单与类别扫描，完整远端克隆内容/历史复检不得省略。适配器module.REPO仍原源仓库，仅导出明确sourceCommit/读取提交标题；本轮源读取限定52白名单及git归档，不递归真实运行数据，不把原工作树搬进候选。
- Root须在每个审计调用前解析/断言环境SOURCE与同次snapshot精确一致且位于release/verify边界内，子进程不得回落原Root；任何不一致/未覆盖候选字节则失败停止。审计输出含命中原值仅内存、安全码计数/hash落证据，不落秘密或未经脱敏内容。此是审计输入边界纠正，不是缩减候选扫描或放宽双审计。
- 三EOF index-only准入仍165，manifest补原/后尾LF计数与正文完全相同断言，实际工作文件/7B7不改；失败命令历史追加保留。总纲再次最终冻结后发送SHA，prepare仍先manifest终审，尚无commit/push/成功审计事实。00不执行审计/扫描原仓库data、适配器、构建或测试；后续记录仅ignored派生证据与聊天。

### 22.167 Wave1实际发布补记及Wave2A/2B正式授权（2026-10-17）

- 本条是负责人新目标下的后续执行记录，不改写22.162–166发布前历史或其冻结字节。已只读复核`.git/agent01-wave1-landing-0355336c81ff4a7897eb17425f45bb6b/publish-result.json`：`published=true`、目标`review/core-platforms`、提交`95a466b757be9b1e1d0ccef880f2018eb733452f`、源checkpoint`9b60769b51e6d0609ed6538580f6f3909eed099e`、原index/HEAD保留、独立完整clone复检通过，记录时间`2026-10-16T10:08:23.013728+00:00`。本条只核既有安全结果，不重跑发布、不将review快照当作生产部署。
- 负责人已明确授权根据文档持续推进工程、减少不必要测试、不再逐步询问确认/授权，直至第三阶段门口；本阶段按已澄清波次完成Wave2A平台及Wave2B模型，停在Wave3 Engine门口。此授权取代旧的“尚未开启Wave2”当前状态，不追改旧证据；Wave3/4/5/6、Engine/Mcp/Web新工程、提交/推送/部署/生产操作均未获本轮启动许可。
- 唯一仓库`%WORKDIR%\bot-agent`，Windows命令使用pwsh。2026-10-17 10:57 +08:00复核：分支main、HEAD`1f3181e21e8d1f9531e41066b033d8d60218d580`、`git status --porcelain=v1 -uall`展开378项、原20项暂存删除保留；index SHA-256 `D60C435A419AB1D2567C2FC886466CA1FD2C02DB5B456DA44D139C615D2A87FD`。不整理、暂存、恢复或清理任何原有改动。根及仓库AGENTS已重读，`src/tests/docs`未发现nested AGENTS；总规划、总纲职责/22.162–166及01/02/03手册已核。
- C3/C4 cfg-0.3.2源冻结仍`7B7CB92AC50AF2441B1759E2190BCEC7C65885155B2581E6C2D2E18C3A3C1203`，现有FQN/签名不改；真实后续缺口分批认领、实现、以必要合成验证准出。Root兼Agent-01负责Core/Storage及唯一builder；Agent-00负责Host/projects/公共测试Program/本总纲等共享最终写入，其余角色不得直接改共享源，临时移交必须逐文件登记。
- 当前只完成本条及22.168最小独立准入，不宣称Wave2A/B或整C4管理已完成。平台SourceInstance入站至queue/approval/tools/proactive/media出站仍须真实闭合；五元会话共享不能代替DriverKey三元路由、授权及策略隔离，不允许全局last-source或任选同账号实例。Model仍需真实Host采用唯一算法并保留兼容薄facade，不引入未有SSE。Host可信catalog/auth、同Store同账本管理及真实lifecycle/Provider连接检查仍为后续冻结登记义务。

### 22.168 Agent-01独立只读Validate最小施工准入（2026-10-17）

| 唯一写入者 | 精确路径 | 已复核输入 / 本批边界 |
| --- | --- | --- |
| Root / Agent-01 | `src/BotAgent.Storage/ConfigurationStore.cs` | SHA-256 `211C446693AEF49077C16E61A963A888B66F6171DA2F85AF0A0B8FBBE27263F9`，物理1526行；仅将原类变partial以复用既有私有Own、CanonicalizePrimaryAliases及participant.Prepare，不借本批重构其他保存逻辑。 |
| Root / Agent-01 | `src/BotAgent.Storage/ConfigurationStore.Validation.cs` | 新文件，准入前不存在；实现独立只读校验，不新增SQL，仅现有只读connection及事务。 |
| Root / Agent-01 | `src/BotAgent.Storage/ConfigurationStorageBridge.cs` | SHA-256 `CF8FD677C840A2FB78BADCCD7CF58326BAAEE36BDAD99AC996274B85D85AA34D`；添加internal Validate转发，不扩大公开Core接口。 |
| Root / Agent-01 | `tests/BotAgent.ReviewRemediationProbe/ManagedCapabilityContractTests.cs` | 新文件，准入前不存在；只用合成隔离数据，覆盖本批Validate安全/一致性/取消及无副作用。 |
| Agent-00 | `tests/BotAgent.ReviewRemediationProbe/Program.cs` | 只登记接收上述测试入口，Root交付后读实源再最小合入；本次准入尚未编辑该文件。 |
| Agent-00 | `docs/plans/agents/00-orchestration-and-dependency-graph.md` | 本次唯一实际文件写入；追加22.167–168，原全文SHA-256 `62D748F26BA89B555BBDB9F6D0314F01D40B7EEE0B2098AFDE64CAA626E7FF9D`。 |

- Validate返回既有`ManagedValidationResult`，不新建Core接口；独立检查`ValidateConfiguration`权限，含非Keep秘密另要求`EditAllowedSecrets`，校验`ExpectedVersion`。复用Save同等typed/alias/secret/cross-object Prepare约束，不能以Save权限替代Validate权限、也不能实际调用Save。
- 严禁调用Persist/Govern，或写floor/receipt/audit/secret/Token种子；同一Store、同一participant、只读connection/事务路径，不构建第二数据库单例或账本。秘密同步owned并于finally清零，取消抛`OperationCanceledException`，不吞成普通失败。错误仅安全代码/字段，不输出秘密或原值。
- 本批准入后Root可立即仅在上述四个owned文件施工，不必等待Wave2其余设计。Agent-00本次不构建、不跑测试、不预先改这四个源；后续Root单一builder执行必要定向合成验证，测试结果以当时源hash/命令/时间/exit另登记，不引用旧825/0当本批通过。
- 回退只针对本批新增文件及精确owned差异；先核当前内容/并行写入，再撤销能证明归属于本批的片段，不恢复整文件、不碰原index/20删除或其他既有改动。接口/数据写入/秘密安全或来源隔离出现未闭合问题时停止对应接入，不降低门禁或擅自开启Wave3。
### 22.169 Wave2A/B首批独占文件登记（2026-10-17，只源码）

- 继22.167的负责人持续推进授权，以下仅授予已列模块内文件写入；共享Host/projects/公共测试Program/总纲仍由00最终合入，01/Root为唯一builder。02/03不构建、不运行测试、不提交推送部署，不改冻结Core既有FQN/签名，不启动Wave3；此处不是各波次完成登记。

| 执行者 | 精确路径 | 只读核对与交付边界 |
| --- | --- | --- |
| 02 | `src/BotAgent.Platforms/Governance/QqChatSourcePlatformAdapter.cs` | 新文件；迁移复用现有Host `src/BotAgent.Headless/Adapters/Platforms/QqChatSourcePlatformAdapter.cs`，保原FQN `BotAgent.Adapters.Platforms`。以真实来源Context闭包、实际lifecycle委托、stop退订/取消/释放/状态为目标，不新造平行LegacyPlatformAdapter；Host原文件由00在接入批核对后删除，不提前删除或并列编译同FQN。 |
| 02 | `src/BotAgent.Platforms/Governance/PlatformDriverOperations.cs` | 新文件；仅真实C4 driver目标解析/描述/dispatch，账本复用01后续同Store mg1实现，不自建ledger、不以描述符假装动作真实完成。 |
| 02 | `tests/BotAgent.ReviewRemediationProbe/PlatformLifecycleTests.cs` | 新文件；合成adapter/lifecycle测试，入口待00合入，真实网关行为另外逐文件认领后验证。 |
| 03 | `src/BotAgent.Model/IHttpFetcher.cs` | 输入SHA-256 `5984FB0591D533E9B42F8DC62DDBDC50543F13D3A087C3F8564A67E664B8ED5D`；唯一改名FQN `BotAgent.Model.Net.IModelHttpFetcher`，不得与Host现`BotAgent.Adapters.Net.IHttpFetcher`重名。 |
| 03 | `src/BotAgent.Model/OpenAiClient.cs` | 输入SHA-256 `977EF9E21CE386DA7D6E2CB906F99EFFB389F1BAFE3E87279FFC182D9C1B7D98`。 |
| 03 | `src/BotAgent.Model/ModelTransport.cs` | 输入SHA-256 `7852B979B8F333434601ECF1B90685A3B18AB63E49D74F091FD9517A11D9BE64`。 |
| 03 | `src/BotAgent.Model/ImageDownloader.cs` | 输入SHA-256 `BE319AABD617B629BC5B9C5E040105066F2F0FA935A0FA64B5338B7AF2B1F737`。 |
| 03 | `src/BotAgent.Model/ModelOptions.cs` | 输入SHA-256 `4078B7046AEA8CD3BDF49EA5382C92C298E4DDB22548ACBC3B823885F0762CAD`。 |
| 03 | `src/BotAgent.Model/ThinkingBudgetResolver.cs` | 输入SHA-256 `80A4C8E8DA34A91A3F06CA73722CF9C95C66A1563276F609F7AE85034476DCC9`。 |
| 03 | `src/BotAgent.Model/Net/SafeUrl.cs` | 输入SHA-256 `963EBF87678A363377506929760B01E552BCBA5945DD45315C59081D977836BF`。 |
| 03 | `src/BotAgent.Model/Net/OutboundAddressPolicy.cs` | 输入SHA-256 `A090BD976191E856299851EFCE2F0BCC21D21A358952F0A359F68A103C3AA355`。 |
| 03 | `src/BotAgent.Model/ProviderConnectionTester.cs` | 新文件；一次合成chat、`max_tokens=64`、无重试，不把Query/replay变为再次调用；Host可信配置及管理权限/幂等在共享接入另冻结。 |
| 03 | `src/BotAgent.Model/ModelFault.cs` | 新文件；只含安全分类，不泄露原响应/URI秘密/凭据。 |
| 03 | `tests/BotAgent.ReviewRemediationProbe/ModelMigrationTests.cs` | 新文件；合成上游记录请求形状与故障/取消，公共Program由00最小合入。 |

- 00已独立复核03八输入SHA全部匹配，六个当时报名的新文件均不存在；02在正式登记前更正为迁移现QqChatSourcePlatformAdapter，原拟LegacyPlatformAdapter不得创建。`PlatformRegistry.cs`暂不认领、不改；其它Official/OneBot/Transport/Feishu/Local现有源须待实际逐路径hash、最小接口与生命周期方案冻结后另批准入，不因属于Platforms自动获得写权。
- Model目标是唯一算法，Host薄facade保既有FQN；真实Host Model工程引用、唯一HostHttpFetcher适配、Clock/Log绑定、完整thinking语义及主/fast路由均由00分批读实源接入，不能仅Model构建冒已采用，不引SSE。SafeUrl/Outbound安全规则不能放宽；未知、超时、调用者取消分开，敏感模型报文不落诊断。
- 02需先交真实adapter closure/lifecycle和driver操作最小接口供00/01对齐，03需交Host facade最小兼容签名；后续新增/旧源删除及公共入口合入另登记。只回退能够证明归属本批的精确片段，不覆盖原有改动或清理未知文件。
### 22.170 来源envelope及Model共享接入首批登记（2026-10-17）

- Root/01唯一新源写权：`src/BotAgent.Core/Domain/Messaging/PlatformMessageEnvelope.cs`（已核不存在）。新增getter-only不可`with`替换identity的来源envelope，Origin含`DriverKey`及可空`MessageRef`；正常message factory校验driver平台/scope与conversation/sender一致且Trigger等于Ref，poke允许null而不编造消息引用；入站ImageUrls防御复制，ToString固定安全标签。保既有FQN/签名，非破坏性新增；Core类型本身不构成可信来源，Host必须闭包捕获actual adapter并绑定具体lifetime，旧message origin字段需JsonIgnore防body注入。Core origin合成检查归02已认领`PlatformLifecycleTests.cs`，不新增并行公共入口。
- 00认领下列共享最小接入，读实源后精确修改，不改变架构阈值或其他配置。`src/BotAgent.Model/BotAgent.Model.csproj`输入SHA `4F470A5021EB3B2718A74AEDDC13DE1A2ACFFE4D02BC029F25089C25807D21D7`，仅将失效的`BotAgent.Adapters.Net`/`BotAgent.Services.Net`全局Using合为`BotAgent.Model.Net`，Model引用仍仅Core；`src/BotAgent.Headless/BotAgent.Headless.csproj`输入SHA `329185BA1002F1B3FBA5B72916CFCAE2FB0C478604E1C39262AA58183CD03707`添加实际Model引用；`src/BotAgent.Headless/Adapters/Net/HttpFetcher.cs`输入SHA `0D1FC3B8F07EC880D38599591A14F4B7A49DD27F30839E47A71003EEF0CD8984`的Host接口继承唯一`IModelHttpFetcher`，不新增socket/第二fetcher实现。
- 00保FQN薄facade认领：`src/BotAgent.Headless/Services/Agent/OpenAiClient.cs`输入SHA `C98564A9C34DC5FE057A73E8DC9FF9B55EAF909E0C4DB3023EAA0A56445CCDE8`；`src/BotAgent.Headless/Adapters/Model/ModelTransport.cs`输入SHA `0F91E80C47CF09A9F14931BC5F649444D1849A6EA45FB2C475B2BD666815C9B4`；`src/BotAgent.Headless/Adapters/Model/ImageDownloader.cs`输入SHA `F5DE93BEB0E8533C4CD15242BBEC5DCE4A9C521D46E31A99AB11F23F6DDF6D98`。仅转发Model唯一算法，保现构造与兼容使用面，不在Host保第二份模型算法。确需新增Host options投影文件时先登记精确路径；配置仍每次读取SettingsBox当前发布版，不缓存秘密或绕过现C3路径。
- 00共享绑定及唯一thinking委托：`src/BotAgent.Headless/Adapters/Time/ClockBindings.cs`输入SHA `715572F6AF62626AF3BFEFDECD43F8ADB4EF3D1BBCE64B988BDC68A5D91EA23E`；`src/BotAgent.Headless/Host/CompositionRoot.cs`输入SHA `859229747BDAA85E9B935F6F4C89F01AE7DBBA33ED5E8559627DE8BA895E54B7`；`src/BotAgent.Headless/Services/AppSettings.cs`输入SHA `AC59A571A97C37F202DE7AC5CA21DC0E0EFA040010729ED219882174B8171224`。需明确ModelClock/ModelLogging绑定，AppSettings解析仅委托03完整ThinkingBudgetResolver，不改其保存字段/默认值或主fast选择。以上是施工范围，不是当前宿主采用/行为验收证据；待完整源就绪由Root唯一builder执行必要验证。
### 22.171 Wave2A真实生命周期八路径最小准入（2026-10-17）

- 02唯一写权仅以下八现有路径，00已独立核其输入SHA一致；不改设置/业务/存储/Host/公共测试入口。01/Root唯一builder，02仍只源码。

| 精确路径 | 输入SHA-256 |
| --- | --- |
| `src/BotAgent.Platforms/OneBot/OneBotGateway.cs` | `8E144FE83340E272FEDFF28FE65682FB94D3DFCDE4058918B80ABA9584563270` |
| `src/BotAgent.Platforms/OneBot/IOneBotTransport.cs` | `F6E19F0D9026A932E2646D485A4E43E9E7AFCDE42A6367A6C6D2D8EB4B3C5E20` |
| `src/BotAgent.Platforms/OneBot/ForwardWsTransport.cs` | `4EA51F14E2B4779720207A4784AA0B6C145520B032216A3EF494BE94BCED5DDB` |
| `src/BotAgent.Platforms/OneBot/ReverseWsTransport.cs` | `E75B4040E7EE07A8FEBCD0B8DDFF6AB5FF209A5178A9B27E32282E91AFCDA3CD` |
| `src/BotAgent.Platforms/OneBot/HttpTransport.cs` | `5CAAF8DFE383CA93FC1BF98B3B94A74FE8080B2A85F5E73BD9CD099EB1A8C227` |
| `src/BotAgent.Platforms/Official/OfficialBotGateway.cs` | `3E375E1CD3D85F13E2C4E0825021850C573856D69DD656F7933ACAF31785FFD9` |
| `src/BotAgent.Platforms/Feishu/FeishuBotGateway.cs` | `3E79AF6EA678A3D7A978BBF3E57A0D5566B5489EC7889FAD5B4420FAD32DB6E1` |
| `src/BotAgent.Platforms/Local/LocalChannelSource.cs` | `E23A27E8D809D54EB3B2E24865C759119B5DFDC9F6FF097743B781EF1FF59AD4` |

- 最小API：`IOneBotTransport.StopAsync(CancellationToken)`允许默认转原Stop以兼容既有fake，三个真实transport必须真实cancel/close/await owned worker，Reverse追踪handler；OneBot/Official提供异步Start/Stop并保旧void兼容面，重启不得继承已停旧worker。Feishu/Local提供Start/Stop及取消lease、停后拒new callback/IO，飞书保原生Context/ref。不以等待超时当已完全停止，不允许restart叠worker；取消/未知结果如实报告。
- 来源适配层保原构造前5参数，可追加真实enabled/credentialsReady及start/stop/test delegates；state必须来自实际实例，不硬写Enabled=true。不支持TestConnection声明unsupported且零IO，不伪造在线成功。Start新lease，Stop使旧lease取消，旧消息不得借重启后的同实例/同账号来源发出。具体Core seam与Host actual adapter/lifetime闭包仍需00/01联合接入，新增类型不是可信来源认证。
- 以上不覆盖生产账号/真实网络验证，不使用真实协议报文；只owned合成测试及后续Root必要验证，不擅自启动第二真实协议端。源漂移/停机未闭合/身份不明时停止对应接入并回报，不清理原有状态或放宽权限。
### 22.172 Model唯一算法的共享精化与旧重复源删除认领（2026-10-17）

- Root已明确认可：Host对Model使用全局项目引用，删除Host重复`src/BotAgent.Headless/Services/Model/PromptBuilder.cs`（输入SHA `16E0EF72989A4E9348483FC99B31914395243AF04FA96991418EBD12DA8F4C30`）及`src/BotAgent.Headless/Services/Resilience/ProviderCircuitBreaker.cs`（输入SHA `C9627F6717CACA4B021F464E65CBFA738EFC8F6E5ADA2752D498913E3A400CAB`），仅00最终执行文件级删除，不改真实index，不强checkout。这两份是当前迁移输入；删除前原字节保存在本次只读源码证据中，删除不代表丢弃其兼容行为。
- 已检索Host消费者：PromptBuilder实际只有旧OpenAi算法内部调用，Model提供相同公开FQN/PromptRequest；Provider公共FQN在Model保留，Host及Chaos/Safety/Review/Deadline既有消费者应采用唯一Model类型，无需第二套Host wrappers/runner桥。已查相关测试，未发现将这两个公开类型硬绑定Host Assembly的反射；既有SSRF反射仍依赖Host ImageDownloader FQN，必须保薄facade。结果只属静态盘点，不称测试通过。
- 00新增认领`src/BotAgent.Headless/Adapters/Model/HostModelOptions.cs`（新文件）用于每次从SettingsBox.Current读取并投影ModelOptions，不持久保存秘密、不改变C3源；现Host OpenAi/Transport/ImageDownloader改成继承Model的保原FQN/构造薄facade。00按22.170其余路径接入ModelClock/Log、HostHttpFetcher接口与thinking委托。
- 00认领`tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj`仅将已有ModelModule引用别名改`global,ModelModule`以在公共FQN迁出后保持旧测试消费者与显式module测试同一实际程序集；原专项断言保留。`tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs`输入SHA `E1CC40F82CDD3002230078AF2D76C5928F7769F745FD71213506F17E25C2F969`只改RoutingFetcher已失效旧Module IHttpFetcher FQN为新IModelHttpFetcher；`Program.cs`输入SHA `7878935331645BDDE3B2B5D4635E0464F62944CBA00F0C10EE0A32384476A2B9`添加已owned Managed验证child/parent入口，不移除任何原场景。
- 上述认领后仅源码合入，Root等待02/03稳定后唯一构建；若重复FQN/旧反射/全局using产生新问题，按真实错误定位，不放宽架构或删断言。旧源码删除只涉及两精确文件，原既有20暂存删除及其它状态全部保留。
- 22.172共享接入前镜像限定上述14安全源码/工程/测试路径，00已独占保存于ignored精确根.git/agent00-wave2-shared-6a2036dc6dde4edf9076716cf62df593/并记录逐路径SHA；该根只用于本批精确回退核对，不含真实data/logs/环境值，不清理或改写既有证据。

### 22.173 C4同Store管理服务与mg1独立准入（2026-10-17）

- 00/01已核冻结candidate管理语义并共同确认：Storage内部`ManagedCapabilityService`实现既有Core `IManagedCapabilities`，只依Core，不引用Host/Platforms/Model；可信Host提供`IManagedAuthorization`、catalog及dispatch闭包。既有Save/EditAllowedSecrets绝不自动授管理动作权；未获授权不泄露对象/receipt存在性。
- 01新增独占`src/BotAgent.Storage/ConfigurationStore.Management.cs`（同Store journal，复用既有SQL常量，不新增SQL）、`src/BotAgent.Storage/ManagedCapabilityService.cs`（服务与两个internal records）、`tests/BotAgent.ReviewRemediationProbe/ManagedOperationContractTests.cs`（独立合成管理测试）；既owned Store/Bridge及ManagedCapabilityContractTests只允许相关最小mg1/桥接扩展。公共Program仍00合入，不改变Core旧FQN/签名，不新增DB/schema/独立账本。
- 内部factory精确冻结：`ConfigurationStorageBridge.CreateManagedCapabilities(ConfigurationStore store, IManagedAuthorization authorization, Func<ManagedCatalogSnapshot> catalog, Func<ManagedCommand,CancellationToken,ValueTask<ManagedDispatchResult>> dispatch)`返回`IManagedCapabilities`。`ManagedCatalogSnapshot(string DescriptorRevision, ImmutableArray<ManagedDescriptor> Items)`与`ManagedDispatchResult(ManagedOperationState State, string? ErrorCode)`均为Storage internal，非公开Core扩展；Host只能通过同Store可信组装，不构建第二Database/Store。
- mg1同现generation/TTL/floor/HMAC及`configuration_operations`总预算4096/主体256，回执≤8192byte；绑定caller/resource/target/action/descriptor revision。授权在持久受理前和外部dispatch前分别重验；Accepted持久前绝不外发。Query/replay不dispatch，重启在途/旧generation在途如实Unknown不自动重发。闭集unsupported/validation未接受、零IO。源码新增不冒已管理支持，必须后续真实driver/provider producer与consumer、取消释放的合成闭合。
- 服务独立拥有运行CTS并实现`IAsyncDisposable`，请求ct仅停止等待，不等于Cancel远端；Cancel按原receipt原target/action重验后只取消原CTS，CancelRequested不冒Cancelled，已完成结果不可被迟到Cancel覆盖。不得用caller取消伪造副作用已回滚。
- cursor本进程随机HMAC key，closed payload≤512byte，绑定callerhash/resource/kind/可见catalog revision/offset；合法签名且目录变更返回StaleCursor。重启后旧签名无key不能验证时返回InvalidCursor，不默认保留旧签名或建立无界paging session。
- Query/Cancel先内部只读原receipt获取target/action，再核专属权限；未经授权不返回receipt。无receipt与缺权限统一Forbidden防存在性泄露；仅记录存在且授权通过后返回KeyExpired/GenerationMismatch等治理结果。不将配置Find的NotFound语义直接套入管理未授权场景。
- 00负责真实Host组装、相同实例的DriverOperations与Provider一次chat TestConnection接入、服务释放/可信catalog/auth及公共测试入口；不创建新HTTP/ZBA路径，后续06另冻结。01为唯一builder，02/03/00不同时构建；本条只授权源码实施，不宣称已完成C4/Wave2验收。
### 22.174 首批交接、来源rich桥及回退证据补齐（2026-10-17）

- 22.173 factory细化：同一ConfigurationStore只持有一个ManagedCapabilityService协调器；重复相同可信绑定允许返回原对象，不允许第二协调器并行操作同账本。Dispose后不得静默新建服务或重发旧操作；01据此继续实现。
- 01首批准入结果已接收：Store SHA `95B4725B3FFD9E581A69C64B639ABE4C5969FEAF13B91582ACA59A13374F685A`；Validation `48CC37096AF9AF0AD82A1173C15DB5A44DF9B4EF3D3C801A0F5FA5EA62DBD00E`；Bridge `B6457780B2CF9ABE8ACE09174AD4B7136D70CD6AA7FE0F85DDE80DEBDA56DE27`；ManagedCapabilityContractTests `AA2B7FDE62ACE0EE2ED8E35886D3821C7BE301426C8A718E887931AA5B76D6C4`；Core envelope `65FF5747CDA074D9D33ECEF48099C95FFF86C6034F3D2A5F753264AD01D1E944`。01仅在2026-10-17 11:03:52.390～11:04:08.135 +08:00执行`dotnet build src/BotAgent.Storage/BotAgent.Storage.csproj -c Release --no-restore`，exit0、0warning/0error；独立编译不是Host采用/行为验收，mg1后续扩展会使部分hash更新。
- 00已合入22.172精确Model共享源，14路径前镜像真实存在。`ConfigurationContractTests.cs`的RoutingFetcher旧Model端口FQN仅一行由00最终接收/替换为`ModelModule::BotAgent.Model.Net.IModelHttpFetcher`，不是ClockContractTests；该精确合入后此测试文件写权交回01，00不再写、01不重复改此行。Managed child/parent及ModelMigrationTests入口已在公共Program登记，尚未运行测试。
- 03原Model八输入为untracked，不可git checkout恢复；此前只有输入hash、不能谎称已备原字节。补证仅准入03独占新ignored精确根`.git/agent03-wave2-model-preimages-5b9fe178fc9e4fa990e6b695ecaa73d9/`：从已知安全source checkpoint `9b60769b51e6d0609ed6538580f6f3909eed099e`只`git show`八精确Model路径保存前像，与22.169原SHA逐项完全匹配才标verified；不匹配只能在同证据根重构候选并以原SHA精确验证，记录raw mismatch/方法，不覆盖工作源、不读取data/runtime，不把不匹配字节冒成前像。00的旧Host三前镜像同14路径精确根可作重构输入，缺失如实报告。
- 来源桥精确冻结：在02 owned迁移Qq适配器文件内定义Platforms具体接口`IPlatformInboundChatSource`及`RichInboundReceived(QqChatMessage, InboundMessage)`事件，Qq适配器/native Feishu均实现；保原raw/native事件供兼容consumer，但Host生产主流只订rich pair一次，不并订raw及native造成双处理。闭包捕获实际adapter/source/Context，重核registry exact对象与registration活跃且捕获当时lifetime，再包装Core envelope；保原rich MusicShares/role/title等字段、飞书native Ref，不以QQ alias重新造native Ref。Core envelope保持平台中立，不加入QQ rich内容。
- 00下一平台共享认领：`src/BotAgent.Headless/Services/Qq/ChannelRouter.cs`（真实closure/精确路由）、既owned CompositionRoot（只收集sources一次构router，移除r1/r2孤悬订阅）、`src/BotAgent.Platforms/OneBot/OneBotModels.cs`（只additive JsonIgnore envelope/lifetime等来源承载，02不得写该文件）及旧`src/BotAgent.Headless/Adapters/Platforms/QqChatSourcePlatformAdapter.cs`（同FQN迁出后由00精确删除，留原字节前镜像）。02仅在ignored `.git/agent02-wave2-20261017/host-platform-routing.patch`提供上述shared建议文本，不实际改共享源；00读当前源后最终合入。
- 后续queue/approval/tools/proactive/media必须显式承载每任务来源与旧lease，不能按全局last-source、同账号任意实例或重新取得新lease发送；生产路由迁移未完不得称Wave2A已完成。Agent-00当前未构建/测试、未提交推送部署，Root仍唯一builder并待产品源码稳定后运行必要验证。
### 22.175 平台共享来源执行链逐文件准入（2026-10-17，00最终合入）

- 00新增`src/BotAgent.Headless/Services/Qq/PlatformSourceExecution.cs`：不可从请求body创建的Host lease，内部构造绑定actual router/source/adapter/registration、Core origin与捕获的旧lifetime；只读身份，AsyncLocal仅每执行流scope，不存全局last-source。callback每次覆盖不可信origin，worker从显式任务状态恢复scope；源停止取消旧token，重启不能让旧任务借新token发送。无scope自主任务只在同会话当前实际实例唯一且真实策略允许时选取，否则拒绝。
- 精确现源输入：ChannelRouter SHA `DC0CB86DC990F6191DD28E8CBB33103BC471DE39D3FEAECB5D8051744B76D7DD`；OneBotModels SHA `8830ACA9D7A829EE21A3355750EFA4BD60C5342A696EDDC32C6B623F7BBBCBC4`；ReplyPipeline SHA `A5AAF5B4C97B4BBFBE017C7D80132101410A9FC20AFA82C5733FE4A4D52A1E0A`；TenantReplyQueue SHA `581956AE3AF753592FEBC47C12E84C4632E3A952E62411954AF38204C60DE77A`；ApprovalUseCase SHA `90777CF6B197C1C8F3B246B2ADA9E06FE07E56D597D0078E2F0C2367FEE5875A`；AgentBridgeServer SHA `C862B2AC9564588EDDC6A5C245D5C8BD2EAC7F8E6B501D76507EB5A80DE3FC17`；AgentCommandService SHA `1349E5F25B98771B3165519E1F762B2ADB0A99F0B244BD3F3E79CAA81B2E232D`。02仅建议patch，00在原字节前镜像后按真实context最小合入。
- 00来源贯穿确切共享范围：`src/BotAgent.Headless/Services/Reply/ReplyPipeline.cs`、`ReplyPipeline.Delivery.cs`、`ReplyPipeline.Proactive.cs`、`TenantReplyQueue.cs`、`PlainSender.cs`、`src/BotAgent.Headless/Services/Poke/PokeUseCase.cs`、`src/BotAgent.Headless/Services/Permissions/ApprovalUseCase.cs`、`src/BotAgent.Headless/Services/Agent/AgentBridgeServer.cs`、`AgentCommandService.cs`、`AgentCommandService.Commands.cs`、`src/BotAgent.Headless/Services/Settings/SettingsHotReload.cs`、`src/BotAgent.Headless/Host/Program.cs`；仅来源/旧lease/精确实例策略、实际生命周期治理与停机收口，不迁移Engine算法，不改无关业务设置。
- Queue PendingReply/DeferredTrigger显式固定来源；审批request固定最初来源，执行不能继承另一实例审批者/Panel当前flow。AgentBridge独立队列和WS progress/finished回调需从原AgentTask恢复来源，body JsonIgnore；不能只依Task.Run碰巧继承。媒体/工具异步流默认继承当前scope，但独立持久/计时工作另核断口；不因为源码类型已有Origin就冒完整链已迁移。
- 00可对上述owned路径做必要最小调整，额外媒体/工具源如确需变动仍先列具体路径。02在owned PlatformLifecycleTests添加Core/实际router synthetic origin及生命周期/多实例拒绝验证，公共入口00合。现未运行这些行为测试，Wave2A未准出。
### 22.176 Wave2共享恢复与C4 Host精确组装（2026-10-17）

- 00恢复后实核原HEAD/index未变；前任CompositionRoot的AppGraph/BuildChannelLayer中断声明已按真实调用补闭，单router、真实registrations初态false，公共Program补ManagedOperationContractTests child/parent及PlatformLifecycleTests parent。00不构建，Root唯一builder；不是Wave2验收。
- 00新增精确owned `src/BotAgent.Headless/Host/ManagedCapabilityBinding.cs`：复用同Store factory和同一PlatformDriverOperations、已捕获registered Provider及原可信route函数。reference-only bootstrap caller只管理实际driver Start/Stop（及其Inspect/Query/Cancel）；不从cfg Access授管理权，其他caller默认deny，不新HTTP/ZBA。Provider一次合成chat producer与独立授权绑定，未开放外部调用。AppGraph/Program真实消费IManagedCapabilities并await停机/释放，异常取消不伪报远端已撤回。
- 额外00 owned精确 `src/BotAgent.Headless/Services/Agent/QqActionTool.cs`，仅SessionQqActionHost绑定初始lease/actual IQqActions/context与原生命周期，阻止直连primary Gateway跨实例旁路。不是新增Engine或改变动作目录/权限模型。其余来源路径仍22.175。回退仅精确本批片段，不碰index或他人源码。

### 22.177 native会话键和有界来源队列补闭准入及实测记录（2026-10-17）

- Root真实窗口：首次Headless 21:20:30.999–50.067 exit1/5syntaxerror（CompositionRoot半截声明）；补闭后21:22:58.595–21:23:03.979 exit0/8既有warning。Review首次21:23:03.999–07.448 exit1/3 targettyped params错误，由01在owned测试改显式ActionCapability；21:23:21.081–25.369 exit0/0warning。隔离仅synthetic环境managed-validation21:23:33.698–37.528 exit0/41项；managed-operations37.542–37.805 exit0/34项。
- 第二共享批Headless21:26:53.801–57.732 exit1，PlainSender:300误插void返回表达式；00仅移除该错行。21:27:35.876–40.660 Headless Rebuild exit0/8既有warning；Review40.676–44.999 exit0/0warning；managed-operations45.008–48.174 exit0/41项，含真实C3共同256预算双方拒额、重放零IO、独立进程强停恢复Query/Replay Unknown零dispatch、cursor重启Invalid。未跑全Review/IT，不冒Host C4或平台来源全部已验。
- 00新增精确owned `src/BotAgent.Headless/Services/Conversations/ConversationRegistry.cs`、`src/BotAgent.Headless/Services/BotConversation.cs`、`src/BotAgent.Headless/Services/Qq/WhitelistGate.cs`：真实可信envelope五元key/旧原key兼容及只读alias证明复用、runtime TargetResolver和exact飞书raw名单；同账号不同instance共享会话但来源路由独立，不改Core codec/存储schema。旧Feishu记录必须同actual app/kind/native只读映射证明，绝不用当前app猜旧key或创建alias后宣称证明；不覆盖旧key。
- 02新增精确owned `src/BotAgent.Platforms/Common/OfficialIdMap.cs` inputSHA `9D83A4275AFCDA83FF0A4B913F99653194AB8DEEE0BD1D5F12224649EF3452DA` 与 `src/BotAgent.Platforms/Common/FeishuIdMap.cs` inputSHA `ABCBF5998392D9271549C56E47AA3ADEAFD7239848DA007E359606FC1D12A196`，只加只读TryGetAlias（lock字典查找/相同v2 key，无分配/Save）。已owned Feishu gateway新增TryGetTargetAlias只读同app/kind/native并反查一致，停止/身份不符拒。02不改Host或公共Program。
- PendingReply普通不同来源满额显式RejectedCapacity，真实caller记录queue_capacity_rejected而非伪CoalescedIncoming；保序溢出封顶256，超限亦拒，不新无限backpressure任务。Deferred按conversation+DriverKey+捕获旧lifetime分桶，仅同源同代合并，finally逐源恢复；无来源自主仍只唯一actual source+策略允许。后续必要真实tests由Root执行，当前非准出。

### 22.178 C4真实消费者可信授权注入接缝（2026-10-17）

- 00/01确认精确共享签名：现`CompositionRoot.Build(AppSettings settings, IManagedAuthorization? managedAuthorization = null)`、`ManagedCapabilityBinding`构造最后同一可选IManagedAuthorization；只通过已冻结Core端口显式DI。bootstrap仍独立reference-only Driver Start/Stop最小权限；其他caller只委托该可信依赖，null默认deny，绝不从ConfigurationAccess推mg1权。未新建HTTP/ZBA、环境测试后门或自动provider花token；已有无参依赖生产默认保持拒外部。
- Root可在既owned ManagedOperationContractTests内添加独立synthetic child，经实际CompositionRoot.Build/AppGraph.Management.Capabilities→同Store mg1→真实Host注册provider route→一次固定chat tester，窄授权+Mock上游；不能反射调用private producer绕过管理授权，不构第二Store/DB。当前只source登记，不冒已行为通过。

### 22.179 真实来源共享持久去重及Ordinal兼容边界（2026-10-17）

- Root第三窗口2026-10-17 21:34:33.962–38.670 Headless Rebuild exit0/8旧warning，Review38.692–42.777 exit0；真实platform-lifecycle child exit0（中段输出截断，不伪报具体计数），model-migration21:34:45.613–46.164 exit0/119项。覆盖仍限synthetic专项，不冒整波验收。
- 00仅既owned CompositionRoot/ReplyPipeline接入同一IWebhookDedupStore实例（已有FeishuWebhookDedupStore，仍唯一Storage DB/write协调器），native gateway webhook与Host平台消息分别命名空间。后者在实际DriverKey exact策略/名单授权后、append/approval/queue前，以闭合原五元ConversationId+NativeMessageId UTF8 JSON SHA256 key注册，不含InstanceId。复用已有10min TTL，现DB实现仅TTL清理无行数硬cap，未新增/抬阈值/SQL或第二HashSet。false才是重复，持久异常独立安全码拒；可信来源缺端口必须拒，optional只保旧无来源mock构造兼容。
- Root/01新增精确owned `src/BotAgent.Core/Domain/Messaging/ConversationIdCodec.cs` inputSHA `093C5FC0D000CF5EC31A829E662382D9ABCE64807EEF6F56D25774964A5DCAFF`，仅Encode对AccountScope.Legacy判断OrdinalIgnoreCase→Ordinal，canonical lowercase legacy原rawkey不变，uppercase不同scope structured保存防碰撞；02已owned测试补case。不改codec其它格式/旧原key或迁移真实历史。
- 00新owned `src/BotAgent.Headless/Services/Reply/ReplyPipeline.SourceScheduling.cs`：现ReplyPipeline新增来源排队/延迟桶后物理1815行超过冻结1765；按已有partial模式把完整来源绑定调度单元（DeferredTrigger/RequestReply/Enqueue/Drain/Cleanup/RunReply）机械迁出，行为与成员不改，不搬到Engine、不放宽棘轮、不压空行绕指标；保原类/接口和既有调用点。

### 22.180 Host实际管理消费者测试与停机所有权（2026-10-17）

- Root/01新owned `tests/BotAgent.ReviewRemediationProbe/ManagedHostCapabilityTests.cs`（新文件），入口TryRunChildAsync唯一`--managed-host-capabilities`及父RunAsync，00公共Program读实源后注册。仅新synthetic根、既有IntegrationHarness.MockOpenAi反射先例、真实CompositionRoot.Build可信窄授权→IManagedCapabilities→同Store ledger→真实registered Provider route→一次chat tester；不启动真实协议。验证首64token/幂等查询零IO、cfg flags不授管理、已提交DBsecret读取、secondary漂移重启拒、slot删除configuredfalse及await释放。
- provider-test HttpFetcher明确不跟重定向，所有权交ManagedCapabilityBinding；await同服务owned任务完成后finally Dispose HTTP owner，多次Dispose共享同Task，不将response.Dispose冒client释放。AppGraph同样幂等await并在各cleanup异常时继续收口，最终仅报告shutdown不完整，不谎称已停。
- IdleFallback只flush已固定origin+旧lifetime的deferred桶，actual router不再从HasPendingReply布尔重新获取新lease；独立proactive仍按当前唯一actual source与权限选择。PlainSender无ambient独立入口只按完整SourceKey唯一Capture，保原task已有旧lease绝不续代。
- 七来源原input前镜像已在ignored原00共享证据根`source-chain-preimages/manifest.json`补齐：从private source checkpoint 9b60769b51e6d0609ed6538580f6f3909eed099e只读精确七paths，逐原SHA全部7/7匹配；未恢复工作文件/index。Model第八前镜像不在此证明范围，不把部分回退证据冒整套回退已验。

### 22.181 可信已登记未加载目录与凭据就绪分离（2026-10-17）

- C4 Host driver catalog改为可信settingsBox.Current.DeclaredPlatformDrivers并实际ops键的精确并集。未构造/disabled可Inspect为NotLoaded，不再NotFound；其Start/Stop/TestConnection标unsupported，不受理不IO，不能因具备期望配置就冒加载。实际loaded仍消费原runtime facts。
- CredentialConfigured独立于loaded/connected/StartReady：Local无凭据明确false但合法ready；OneBot实际optional token presence布尔（无token可合法启动）；Official看实际secret presence；Feishu同现配置SecretMetadata只读TX观察appSecret及签名/验证凭据presence。ready则按actual adapter存在+启动身份未变+各协议现配置要求，Feishu还复用IsConfigured；不再拿“loaded”假称有凭据或以Local无secret禁启动。
- 身份lease同时核Official API/token endpoint及Feishu签名/验证绑定；改变后旧scope/queued回调不能借新绑定。普通同来源同代满额仍保原CoalescedIncoming语义，不同来源/代明确RejectedCapacity，保序hard256不变。

## 二十三、2026-10-17 Wave2A/2B 收口与 Wave3 门前状态（Agent-00）

### 23.1 本轮授权与边界

- 负责人明确：根据文档多 Agent 协作持续推进至第三阶段入口，不擅自开启第三阶段。本轮只做 Wave2A（平台）/Wave2B（模型）及必要共享收口。
- 未创建 `Engine` / `Mcp` / `Panel` / `web` 独立工程；未启动 Wave3/4/5/6；未提交、未推送、未部署；未读取真实群聊、成员、DB 正文或生产日志。测试全部使用合成数据/隔离临时根。
- 唯一仓库 `%WORKDIR%\bot-agent`；HEAD 仍为 `1f3181e21e8d1f9531e41066b033d8d60218d580`；原 20 项暂存删除保留；未执行 `add -A`、reset、clean、checkout 强覆盖。
- 本轮结束时间约 2026-10-17 23:22 +08:00；工作树 415 项（87 修改、20 已暂存删除、95 工作区删除、213 未跟踪），无 stash。

### 23.2 本轮产品源码修复（逐文件）

| 文件 | 变更 | 触发证据 |
| --- | --- | --- |
| `src/BotAgent.Headless/Host/ManagedCapabilityBinding.cs` | `StartAsync` 的启动判定由 `_credentials` 改为 `_ready`；`driver_lifecycle_incomplete` 增加平台/动作/结果码/驱动安全错误码 | S36/S52 在 OneBot token 为空时私域驱动不启动，反向 WS 端口不监听 |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `Build` 拆出 `BuildConversationState`、`BindConversationDeletion`、`RegisterPresetPlugins`、`BuildManagedCapabilityBinding`；Build 由 241 行降至 182 行 | ArchitectureProbe 最长方法 198 行棘轮 |
| `src/BotAgent.Headless/Services/Agent/HostModelOptions.cs`（新）与删除 `Adapters/Model/HostModelOptions.cs` | 设置投影迁入 Services；去掉 `ClockBindings.InitializeModel()` 隐式副作用；`OpenAiClient` / `ModelTransport` 改用新命名空间 | R9：Services 不许认识 Adapters |
| `src/BotAgent.Platforms/Governance/PlatformConsumerContracts.cs`（新）及 `QqChatSourcePlatformAdapter` / `FeishuBotGateway` / `ChannelRouter` / `WhitelistGate` | 新增 `IPlatformInboundChatSource`、`IPlatformSourceBinding`、`IFeishuTargetMap`；服务层改为平台中立契约，删除 Services 内全部 `BotAgent.Adapters` 引用 | R9 与平台契约收口；具体适配器 FQN 保持 |
| `src/BotAgent.Platforms/Feishu/FeishuBotGateway.cs` | Webhook URL challenge 抽为 `TryHandleUrlVerification`；实现 `IFeishuTargetMap` | 方法 200 行超过 198 棘轮；平台契约 |
| `src/BotAgent.Headless/Services/Agent/AgentBridgeServer.cs` | 连接存活与派发权限分离：non-hello 仍按持久门禁拒绝撤销后的晚结果并本地 stop；`SendAsync` 只对 task frame 做 `requireDispatch`，ping/control 不再因撤销停连接 | 文档撤销语义“撤销后晚结果不更新任务/回调”；S33 |
| `src/BotAgent.Headless/Services/Agent/AgentCommandService.Commands.cs` | `//new` 在 `route.Backend == "none"` 时回复无路由，不创建幽灵会话 | S33 面板会话 `current:false` |
| `src/BotAgent.Storage/AgentSessionStore.cs` | `Create` 先归一化 backend（server/host）再写 `Current`，与 `CreateLocked` 存储值一致 | 防止 `Current` key 与 `Backend` 不一致 |
| `tests/BotAgent.ReviewRemediationProbe/ManagedHostCapabilityTests.cs`（新）与 `Program.cs` 注册 | C4 Host 合成测试：真实 `CompositionRoot.Build`、同 Store 管理、Forbidden、无 token OneBot 启动/重停/释放 | 台账 §22.180 的 C4 Host 门禁 |
| `tests/.../ConfigurationContractTests.cs` | provider-routing 诊断改为捕获 `ModelLogging.WarnHandler`，保留安全静默/标题失败与 secret 不泄露断言 | Host facade 迁入 Model 后旧 Console.Error 精确断言失效 |
| `tests/BotAgent.ChaosFaultProbe/Program.cs` | 重复 `EnsurePrimary` 断言改为“不覆盖已有元数据与熔断状态” | 冻结行为：existing EnsurePrimary 不覆 |
| `tests/BotAgent.FeishuRemediationProbe/Program.cs` | 凭据切换断言改为旧快照拒绝、仅新快照发送 | §22.181/22.171 token await 后身份变化拒绝 |
| `tests/BotAgent.IntegrationHarness/PrivacySettingsScenario.cs` | S36 的 bot WS 端口改用 `FreePort(13095)` | Windows 保留端口导致硬编码 bind 失败 |
| `tests/BotAgent.IntegrationHarness/PlatformSwitchScenario.cs` | S52 重启在私域禁用时只等面板端口；超时放宽 | 禁用平台不启动驱动是正确行为 |
| `tests/BotAgent.IntegrationHarness/AgentBridgeScenario.cs` | S33 撤销后断言晚结果不发送；重新启用后重连 | 与 §22 撤销语义对齐 |

### 23.3 C4 Host 证据

- `ManagedHostCapabilityTests.cs` 使用反射调用真实 `BotAgent.Host.CompositionRoot.Build(settings, authorization)`，不新增 `InternalsVisibleTo`、不改产品可见性。
- 覆盖：`AppGraph.Management` 非空；与 `ConfigurationStore` 同一管理服务；允许 caller 可 `ListAsync`/`InspectAsync` 看到 `qq.private`；未授权 caller 得到 Forbidden；OneBot token 为空时 `StartAsync` 成功、反向 WS 端口可连接；`StopAsync` 退租并可重新启动；`AppGraph.DisposeAsync` 关闭端口并停止驱动；临时根删除。
- 实跑：`MANAGED_HOST failed=0 passed=23`，exit 0。
- 未覆盖：provider `TestConnection` 的网络 chat tester；该路径由 `--managed-operations` 41/0 与 `--model-migration` 119/0 在服务/传输层覆盖，本 C4 测试不发起外网/真实 provider 调用。

### 23.4 最终验证矩阵（2026-10-17 22:00–23:22 +08:00）

| 验证 | 结果 | 备注 |
| --- | --- | --- |
| `dotnet build src/BotAgent.Headless/... -c Release` | exit 0，8 个既有 warning | 最终 Headless 构建 |
| `BotAgent.ReviewRemediationProbe`（无参数） | **1056 / 0**，exit 0 | 含新 `managed host capabilities` 父入口 |
| `BotAgent.ArchitectureProbe` | **165 / 0**，exit 0 | R9=0、最长方法 198 内 |
| `BotAgent.SafetyProbe` | **594 / 0**，exit 0 | |
| `BotAgent.ChaosFaultProbe` | **43 / 0**，exit 0 | |
| `BotAgent.SettingsScopeProbe` | failures=0 | |
| `BotAgent.FeishuRemediationProbe` | 全 PASS，exit 0 | 快照语义已更新 |
| `BotAgent.ReverseTransportProbe` | failures=0 | |
| `BotAgent.FrontendProbe` | **370 / 0**，exit 0 | |
| `--platform-lifecycle` | **109 / 0** | Feishu 撤销/身份复检 |
| `--model-migration` | **119 / 0** | Host facade/唯一 Model 算法 |
| `--managed-validation` | **41 / 0** | |
| `--managed-operations` | **41 / 0** | 含崩溃恢复 |
| `--managed-host-capabilities` | **23 / 0** | C4 Host 新证据 |
| IT S21 | **41 / 0** | 模型配置/思考档位/快速档 |
| IT S36 | **98 / 0** | 脱敏/附加提示词/秘密边界 |
| IT S50 | **14 / 0** | 飞书端到端/去重/隔离 |
| IT S52 | **119 / 0** | 平台开关/迁移/静音/重启 |
| IT S33 | **59 / 0** | AgentBridge/设备撤销/会话/图片 |

### 23.5 未覆盖与停止线

- S42 官方通道端到端场景仍在 `IntegrationHarness/Program.cs:130` 注释；官方真机凭据/端到端未验。
- 未做真实 QQ/飞书/NapCat 生产验证、未读取真实群聊/DB、未部署、未提交、未推送。
- 未启动 Wave3（Engine）、Wave4（MCP）、Wave5（ZBA/CLI）、Wave6（独立 Web）。本轮到此为止；Wave3 需要负责人明确解锁后另行登记。
- `PROJECT_STATUS.md` 中既有的 P0/P1 项（#78 面板鉴权/绑定、compose 重复 volumes、脚本原始日志 tail、#79 精确状态、S42、备份定时接线）未被本轮改变；它们仍是生产前待办，不影响“Wave2 技术收口”结论，但不能据此宣称生产安全完成。

### 23.6 关键文件 SHA-256（工作树，供复核）

| 文件 | SHA-256 |
| --- | --- |
| `src/BotAgent.Headless/Host/ManagedCapabilityBinding.cs` | `80D94BBB357F2E3EF5B7FD9B2EB8239C105554F6294BF59FA9E028F47FD0C165` |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `B55F848B5EEAC0B1D1640A15E4ACC2AEAEE15357C1630012500A8ABB5796620F` |
| `src/BotAgent.Headless/Services/Agent/AgentBridgeServer.cs` | `7C1932F0B6E426CE20FE0BC8734D777DB9A06A3D925F0EADF097293CC4BB96BA` |
| `src/BotAgent.Storage/AgentSessionStore.cs` | `D363F922A11E4AC4D78A40118E24D9029B142037E0CAB296B351574365A0353C` |
| `src/BotAgent.Platforms/Feishu/FeishuBotGateway.cs` | `13F73B475C2118C77D8A4CEB838692C69B3550C669B9DF29A32B6FAAEA151A14` |
| `src/BotAgent.Platforms/Governance/PlatformConsumerContracts.cs` | `7D8FDA2BF8E5872A4A0E1CA70D409B3B8C0C6EB2D25499D9D29A233BDA8AAF10` |
| `tests/BotAgent.ReviewRemediationProbe/ManagedHostCapabilityTests.cs` | `12216F7C3CF3A16BE8EE2BE7BF443E65F9778D655404EDC5FAA6AA2EB208C039` |
| `tests/BotAgent.ReviewRemediationProbe/Program.cs` | `95A3C8529F950DCB79B0E93E834E034D4E6DFB36426016B139845E21F5396B24` |

### 23.7 Wave3 门前判定

- Wave2A/2B 的源码、Host 组装、C4 Host 消费者、平台/模型回归与相关 IT 已具备逐项真实证据，可提交 Wave3 入口评审。
- 本轮**不**宣称 Wave2 全生产完成、不宣称 Wave3 已获准；未创建 Engine/Mcp/ZBA/Web，未提交/推送/部署。
- 下一步由负责人决定是否解锁 Wave3；解锁后仍需按 04/05/06/07 手册重新登记写权、逐文件准入和门禁。

### 23.8 文档层命名中立化（仅文档，2026-10-17 23:28）

- 按负责人选择：只做展示层中立化，`PROJECT_STATUS.md` 的 §1 概述、§1.1 接入通道表、§2.1 架构图改为平台中立展示名（私域/官方/飞书/本地平台适配器、`SQLite 主库 + 文件仓储`）；§6.3 增加运行时文件名说明。
- 未改 `qqchat.db`、`qqchat.log`、`QQCHAT_*` 环境变量、`qq.private` / `qq.official` / `feishu` / `local` 平台 ID、会话 key 前缀或任何代码、配置、数据文件。
- `botagent.db` 规范名迁移仍待独立排期：需双读/回退、迁移标记、备份/恢复脚本更新与回滚演练；不纳入 Wave2 收口，也不据此宣称运行时已更名。
- 纯文档变更，无构建/测试影响；§23.2–23.7 的 Wave2 证据不变。

## 二十四、2026-10-18 N0/N1 命名兼容层（Agent-00）

### 24.1 N0 基线冻结

- 负责人确认“阶段二结束，可按推荐做法开始命名规范化”。先冻结 Wave2 工作树，未改 index/HEAD、未提交/推送。
- 本地证据根：`.git/agent00-naming-20261017/`；归档 `source-baseline.tar.gz`（494 文件，1.9MB，SHA-256 `5404A8A51DBB7A816D9F385B08A099B09F8297D3DEBD96EE56B38D95AD7AEC9E`），逐文件清单 `manifest.sha256`（SHA-256 `84640C9A93EE7B3B039812A83A7A27E65C9190403BE9B48F0D1F2C3D1A7523ED`），`baseline.json` 记录 HEAD `1f3181e` 与当时 415 项工作树；未包含真实 data/runtime/logs/私钥。
- N0 清单：`docs/plans/agents/naming-normalization.md`，明确展示名、规范别名、内部类型、持久化契约、运维名的分层。

### 24.2 N1 实施（仅兼容别名/回退）

| 文件 | 变更 |
| --- | --- |
| `src/BotAgent.Core/Domain/Configuration/EnvironmentAliases.cs`（新） | 规范环境变量优先、旧名回退的统一解析 |
| `src/BotAgent.Storage/AppPaths.cs` / `AppDatabase.cs` | `botagent.db` 规范名；`ResolveDatabasePath` 仅在无 canonical 时回退旧 `qqchat.db`，不自动复制 |
| `src/BotAgent.Headless/Adapters/Persistence/AppDatabase.cs` | Host 通过 Storage 解析库路径；新增公开的路径解析转发 |
| `src/BotAgent.Storage/StorageOptions.cs` | 默认 `botagent.db` |
| `src/BotAgent.Core/Domain/Rendering/ReplyAuditRules.cs` | 出站审计同时拦截 `botagent.db` / `qqchat.db` / 日志名 |
| `src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs` | 补齐 `BOTAGENT_AGENT_SERVER_KEY`、`BOTAGENT_OFFICIAL` / `BOTAGENT_OFFICIAL_APP_SECRET`、`BOTAGENT_VOICE_EMOTION` 的规范优先/旧名回退 |
| `CompositionRoot.cs` / `WebUiServer.cs` / `LinkPreviewer.cs` / `WebSearchService.cs` / `Model/ImageDownloader.cs` / `Model/OpenAiClient.cs` | 补齐 `BOTAGENT_LOG_FILE`、`BOTAGENT_HEALTH_BIND`、`BOTAGENT_ALLOW_PRIVATE_IMAGE_HOSTS`、`BOTAGENT_MODEL_TIMEOUT_SECONDS` 的别名 |
| `src/BotAgent.Storage/LegacyJsonImporter.cs` | 六处重复前置检查收敛为 `ShouldImport(path, hasRows)`，文件 IO 计数 62→59 |
| `tests/BotAgent.ArchitectureProbe/Baseline.cs` | `FileIoTotal` 棘轮 62→59（只下调） |
| `tests/.../NamingCompatibilityTests.cs`（新）与 `Program.cs` | 覆盖 canonical/legacy 四场景、env 优先级、ModelTimeout 别名、出站审计 |
| `tests/BotAgent.IntegrationHarness/DbProbe.cs` / `FeishuPlatformScenario.cs` | 测试探针按 canonical/legacy 解析库路径 |
| `AGENTS.md` / `README*` / `src/BotAgent.Headless/README*` / `docker-compose.yml` / `.env.example` | 文档标注规范名与旧名兼容；日志文件名 `qqchat.log` 在 N1 保持不变 |

### 24.3 N1 验证

| 验证 | 结果 |
| --- | --- |
| Headless build | exit 0（8 个既有 warning） |
| Review（含 NamingCompatibility） | **1069 / 0** |
| Architecture | **165 / 0**；文件 IO 总处数 **59**（棘轮下调） |
| Safety / Chaos | **594 / 0**、**43 / 0** |
| SettingsScope / FeishuRemediation / ReverseTransport | failures=0 / 全 PASS / failures=0 |
| FrontendProbe | **370 / 0** |
| IT S21 / S36 / S50 / S52 / S33 | **41/0、98/0、14/0、119/0、59/0**；S36 内包含旧 `qqchat.db` 合成回退场景 |

### 24.4 边界与后续

- 未做：`botagent.db` 自动复制/一次性迁移、`qqchat.log` → `botagent.log`、compose 服务/网络/`/opt/qqchat` 改名、`Qq*` 类型/命名空间重命名。
- 不改：`qq.private` / `qq.official` / `feishu` / `local` 平台 ID、会话 key 前缀、DB schema/表名、`secret:apiKey` / `env:QQCHAT_API_KEY` 等持久化 secret ref。
- N2（内部类型/命名空间）与 Wave3 文件移动合并规划；N3（持久化契约）原则上不做。
- 工作树：428 项（97 修改、20 已暂存删除、95 工作区删除、216 未跟踪）；HEAD 仍 `1f3181e`；无提交/推送/部署。
- 本轮只完成 N1 兼容层；Wave3 仍未开启，需负责人明确解锁。

### 24.5 N1 关键文件 SHA-256（工作树）

| 文件 | SHA-256 |
| --- | --- |
| `src/BotAgent.Core/Domain/Configuration/EnvironmentAliases.cs` | `774DD4A41A3868215E0447F70ED10BD5840AD5C5706D34029D0DE348FD49CA8B` |
| `src/BotAgent.Storage/AppPaths.cs` | `BAE8E64AA5F1C0841D5095C43DD812CF46562C180AFE4C58A3A2D6AB13A02604` |
| `src/BotAgent.Storage/AppDatabase.cs` | `4282A036E164F7696AC1A4B34FE378AF710EA4C9E15C3690CE30579B2FF473C9` |
| `src/BotAgent.Storage/LegacyJsonImporter.cs` | `946B5D3EA8E5C085D4C6D4AB864280C960600C2EDCA30ECD2B1D843318D00BD1` |
| `src/BotAgent.Headless/Adapters/Persistence/AppDatabase.cs` | `004315ED7CAE50A42D85C7CC446BFDCB03D64163DDFB44C77C64CEA63F64DF22` |
| `src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs` | `CD5D433AE1A9B44E76D9F1FCBDCDEFA5A5295EE192D42B058BCF963C7B5CE9D5` |
| `src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs` | `AAA06C32AB231D236A5D7E06F7F0363E757B5DA3682A019E8684D6CDD4DBF2B5` |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `FB569F152F7F81C9FC000F80AC1AFBE45287DF548C459ADEE98F252EBA925583` |
| `tests/BotAgent.ReviewRemediationProbe/NamingCompatibilityTests.cs` | `E39931A7B6FB07237A5F3B5CBCA072ACE022FE628F056A52B79A27561CCC7726` |
| `tests/BotAgent.ArchitectureProbe/Baseline.cs` | `386B5BF159E2A70C39464E49BA70FE9ADC50D9869198D1C153841A9B4324523C` |

## 二十五、2026-10-18 Wave3（Engine）施工登记（Agent-00）

### 25.1 授权与停止线

- 负责人本轮目标：「持续推进至第四阶段入口，不要擅自开启第四阶段」。该授权取代二十四节末尾“Wave3 仍未开启”的旧状态，**仅解锁 Wave3（Engine）**；Wave4（Mcp）、Wave5、Wave6 仍未解锁。
- 停止线：Wave3 准出（回复/工具/插件编排在 Engine，宿主只做组装与 I/O 适配；插件门禁、审批负例、后台有界队列与停机收口有合成证据）后立即停在 Wave4 入口；未创建 `BotAgent.Mcp`、未改 MCP 协议/工具登记、未提交/推送/部署。
- **2026-10-18 12:25 更正**：独立验收（Agent-01）判定 Wave3 不通过并退回；上一条的准出描述仅作为当时的停止线定义，不再是已达成事实。整改与复跑见 §25.8，Wave4 仍需独立复验通过后由负责人解锁。
- 未读取真实群聊、成员、DB 正文、协议原始报文或生产日志；所有新增测试使用合成数据与隔离临时根。

### 25.2 施工边界

- 新工程 `src/BotAgent.Engine/BotAgent.Engine.csproj` **只引用 Core**。Headless 增加对 Engine 的引用；Engine 不引用 Platforms/Model/Storage。
- Engine 承载：工具目录与执行登记、插件注册/启停与门禁、回复决策与有限步进循环、审批/参与/角色/事件用例、后台 Evaluator 有界队列。
- Host（Headless）保留：数据库/模型/平台/HTTP/音频等 I/O 适配器、发送实现、面板事件、装配根；Engine 只经 Core 端口调用它们。
- 持久化契约不变：平台 ID、会话 key、DB schema/表名、`secret:*` / `env:QQCHAT_*` 引用均不改；N2 的类型/命名空间整理仅在 Engine 文件移动范围内进行，不做全仓库重命名。
- 每个批次先构建 Engine/Headless，再跑受影响探针；发现行为退化即停止该批并回退本批文件，不放宽门禁。

### 25.3 批次计划

| 批次 | 内容 | 准出证据 |
| --- | --- | --- |
| W3-1 | Engine 工程骨架、架构护栏/解决方案登记；迁入纯工具目录、工具执行登记、插件注册表与预设插件、有限步进循环 | 构建 0；Architecture 0；Review 现有用例不退化 |
| W3-2 | Core 端口补齐（设置快照、入站源、策略/面板/发送/能力端口）与回复主链迁入 Engine | Headless 只保留适配器；S36/Review 回复相关用例通过 |
| W3-3 | 插件门禁接入工具执行与回复动作；禁用插件不产生新工具调用/外部 IO/后台作业 | 合成负例（聊天/显式命令两条入口） |
| W3-4 | 后台 Evaluator 有界队列：容量、并发、满载、重试、幂等、取消、停机排空 | 确定性合成测试（满载/异常/重试/重复/停机） |
| W3-5 | 全量回归与证据登记，停在 Wave4 入口 | build + Review/Architecture/Safety/Chaos + S36 及适用 IT |

### 25.4 开工基线

- HEAD `1f3181e`；工作树保留既有 97 修改、20 已暂存删除、95 工作区删除、24 未跟踪；不整理、不提交、不推送。
- 基线成绩沿用二十四节 N1：Review 1069/0、Architecture 165/0（IO 59）、Safety 594/0、Chaos 43/0、S36 98/0。
- 本批新增/修改文件的写权：Engine 新文件、Headless 适配器与装配根、Core 必要端口、架构护栏、对应测试；旧回复/工具文件删除须在接入验证后逐文件登记。

### 25.5 W3-1～W3-5 实施结果

| 项 | 结果 |
| --- | --- |
| `src/BotAgent.Engine` | 51 个源文件，只引用 Core；Architecture 项目引用检查通过 |
| 回复主链 | `ReplyPipeline` 及 Delivery/HostAccess/Proactive/SourceScheduling、`PlainSender`、`InlineTurnTools`、`AgentTurnLoop`、队列/并发闸门迁入 Engine |
| 工具 | `ToolDirectory`、`ToolExecutorRegistry`、`ServerToolGate`、Qq/Server ToolSpecs、`QqActionCatalog` 迁入 Engine |
| 插件 | `PluginManager`、7 个预设插件、`PluginGate` 在 Engine；回复/投递/内联工具/定时器按插件开关门禁 |
| Core 契约 | `AppSettings`/`PlatformOptions`/`ThinkingBudgetResolver`、入站 DTO、`IQqChatSource`、平台策略/路由/回溯模型、`VoiceCadence` 等下沉 Core；Core 仍无 SQL/IO/系统时间/网络 |
| 有界队列 | `BackgroundEvaluatorQueue`：容量 64、并发 2、单次超时 30s、停机排空 3s；贴图收集、音乐听/分享接入；Host 停机先 Stop |
| 验证 | Review **1075/0**（含 engine wiring 6 项）；Architecture **169/0**；Safety **594/0**；Chaos **43/0**；EngineProbe **14/0**；Frontend **370/0**；SettingsScope/Feishu/Reverse failures=0；S21 **41/0**、S36 **98/0**、S50 **14/0**、S52 **119/0**、S33 **59/0** |
| 命名 | 测试项目与新增代码统一 `BotAgent.*`；测试基础设施支持 `BOTAGENT_IT_ONLY`/`BOTAGENT_SRC_ROOT` 优先、`QQCHAT_*` 兼容；行为变量仍按 N1 保留 `QQCHAT_*`；DB canonical/legacy 由既有 NamingCompatibility 覆盖 |

### 25.6 边界与 Wave4 停止线

> **2026-10-18 12:25 状态更正**：Agent-01 独立验收判定 Wave3 **不通过**，并撤回本节与 §25.5 的“技术准出”表述。F1–F9 的整改、复跑与新的边界登记见 §25.8；Wave4 继续锁定，需独立复验通过后才谈入口。

- Engine 仍通过端口消费下列 Host 适配器：`IAgentCommandService`（`//` 任务/桥接）、`IVoiceUseCase`/`IMusicUseCase`/`IResearchUseCase`（外部服务编排）、`IVoiceSynthesis`/`IMusicCatalog`/`IWebSearch`/`ILinkPreviewer`（网络/音频实现）。这些实现留在 Headless 是 I/O 归属，不是 Engine 反向依赖。
- 未创建 `BotAgent.Mcp`；未登记 MCP server/tool，未修改协议/SDK/出网；未提交、未推送、未部署。
- 未做 DB 自动复制迁移、日志文件改名、compose/`/opt/qqchat` 改名、`Qq*` 类型全仓库重命名；平台 ID、会话 key、schema/表名、secret ref 均未改。
- Wave3 准出证据为合成测试与构建结果；真实生产群聊、生产日志、真实模型/协议互通未在本轮验证。

### 25.7 收尾状态与关键文件指纹（整改前快照）

> 本节是 F1–F9 整改前的快照，数字与指纹已被 §25.8 取代；保留它是为了对照独立验收时的现场。

- HEAD 仍 `1f3181e`；工作树 281 条状态（96 修改、20 已暂存删除、140 工作区删除、25 未跟踪）；无 commit/push/deploy。
- `src/BotAgent.Engine` 51 个源文件；Architecture 扫描 343 个源文件 / 60,712 行，项目引用检查显示 Engine 只引用 Core。
- 测试命名：新增/修改的探针统一 `BotAgent.*`；`BOTAGENT_IT_ONLY` / `BOTAGENT_SRC_ROOT` 为规范名，旧 `QQCHAT_*` 兼容；场景内行为变量仍走 `QQCHAT_*`（N1 兼容面）。

| 文件 | SHA-256 |
| --- | --- |
| `src/BotAgent.Engine/Plugins/PluginGate.cs` | `3E9F2D92106373C93BBDEE431C80B0C16CF663DA91B61BA00A1100B8BB010199` |
| `src/BotAgent.Engine/Evaluation/BackgroundEvaluatorQueue.cs` | `A61C883229E0176A1D424999CA6401DAAB59739BCD3FD16324E7460D41D390B7` |
| `src/BotAgent.Engine/Reply/ReplyPipeline.cs` | `1D30E388EF08E48DC51344C4C06249A602E72626F53FEB499ABBBC4FD7F12DF1` |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `ADB59215614A42E5E01E94B6463FFB02D0AF864296D3DEF8E2F91F6C91789583` |
| `tests/BotAgent.EngineProbe/Program.cs` | `B66D4F0F86467B8B8E664A0FC895A2B777D8021350DE529A921065F963062FB7` |
| `tests/BotAgent.ReviewRemediationProbe/EngineWiringTests.cs` | `9D05E095BE30AF9FB1A36EC46D13173036FE1F66DC42C4FED82D6A8023324231` |

### 25.8 独立验收退回与 F1–F9 整改（2026-10-18，Agent-00）

#### 25.8.1 验收结论与状态

- Agent-01 独立验收（证据目录 `.git/agent01-wave3-acceptance-20261018-121328/`）结论：**不通过；退回整改/复验；Wave4 继续锁定**。本波不再使用“已技术准出”的无条件表述。
- 退回项：F1 语音插件条件写反；F2 显式 QQ 动作未消费插件门禁；F3 排队音乐开始前不复核；F4 超时/停机结果不真实；F5 研究后台绕过有界队列；F6 DeadlineGate 探针因类型迁出而跳过租约分支；F7 整包回退探针仍按 38 资产；F8 S18 文字回退断言红；F9 IWebSearch nullable 不一致。
- 本轮只做整改、合成复跑与证据登记；未提交、未推送、未部署，未创建 `BotAgent.Mcp`。Wave4 需另行授权，且以独立复验通过为前提。

#### 25.8.2 逐项整改

| 项 | 整改 | 证据 |
| --- | --- | --- |
| F1 | `ReplyPipeline.Delivery.cs` 语音拒绝条件改为逐项否决：`!平台启用 \|\| !插件允许 \|\| 服务为空`，不再把插件条件塞进平台否定的 AND | `EngineWiringTests` 两条投递级负例（插件关、平台+插件关），Builds/SendVoice 均 0 |
| F2 | `SessionQqActionHost` 增加可选 `pluginAllows`；`AgentCommandService` 两条构造（面板试跑 / 会话现场）透传；`CompositionRoot` 先建插件门禁再建 agent 命令，显式动作在平台/会话检查前 fail closed；**未接线门禁时对插件管理的能力同样拒绝**，不允许漏传参数变成默认放行 | `EngineWiringTests`：插件关 → Poke=0；插件开 → Poke=1；原始独立合成夹具（未传门禁）与更新构造夹具均 **6/6** |
| F3 | 音乐 listen/share 的排队作业在开始前复核插件、平台上下文（用 `turnPolicy.Context` 重解析，不用 channel-only）与当前功能开关；分享后的第二个副作用前再复核；`IMusicUseCase.ListenAsync/ShareAsync` 增加可选 `CancellationToken` 并透传到 HTTP/发送 | `EngineWiringTests`：正控制执行 1 次；排队中关插件 → 不再调用 Listen；独立夹具 `queued-music-revoked-before-start` PASS |
| F4 | 队列超时改为 `Task.WhenAny(work, Task.Delay(timeout))` 真正限定等待；超时先显式 `Cancel()`（Dispose 不取消），迟到成功仍记 `TimedOut`；新增 `BackgroundJobStatus.Abandoned`，停机时对在途未确认工作记 Abandoned；worker 槽位与去重键保留到工作真正结束；`QueueSnapshot.Abandoned` 计入 `Outstanding`。**取舍**：完全忽略取消且永不返回的工作会永久占住一个 worker 槽位（最多 `MaxConcurrency` 个），快照持续显示 Abandoned，不启动替代作业 — 这是显式的“结果未知/待核对”，不是伪造有界性 | EngineProbe 16/0：超时预算、槽位占用、去重保持、停机未知态、迟到副作用观察；独立夹具 `queue-ignores-timeout-token` / `queue-stop-terminal-truth` PASS |
| F5 | `QueueWebSearchAsync` / `QueuePageReadAsync` 改走 `QueueBackground` 并透传令牌；链接预览也迁入同一有界队列（先登记等待句柄再入队）；`QueueBackground` 在未接线时拒绝启动，不再回落 `Task.Run` | `EngineWiringTests`：研究作业在队列占满时不内联执行、释放后执行一次且持可取消令牌；S36/S18 复跑通过 |
| F6 | DeadlineGateProbe 从 `typeof(ReplyPipeline).Assembly` 定位 Engine 的 `ResizableReplyGate`，租约/取消/退休资源断言恢复执行 | DeadlineGate **53/0**（原 30/1，且分支被跳过） |
| F7 | 整包回退闭包改为旧 37 资产 + `BotAgent.Model.dll` + `BotAgent.Engine.dll` = **39**；切回旧包时按候选哈希逐项清理候选专属资产，不按数量放宽、不忽略未知资产 | 整包回退 **24/0**，`candidateAssets=39`，旧 37 项与前镜像哈希不变 |
| F8 | 根因为 mock 脚本 FIFO 错位：前序图片消息被合并成更少请求，残留“沉默”脚本把第 7 步挤成空回复（日志：`empty_reply` → Silent；不是频率门拦文字）。场景改为 `AddRule("再来一句", …)` 按提示词匹配，保留原断言 | S18 **24/0**（原 23/1） |
| F9 | `WebSearchService.SearchAsync` 返回类型对齐 `IWebSearch` 的 `Task<WebSearchResult?>`；`ResearchUseCase` 增加 null 守卫与面板回退值 | Headless Rebuild 警告 9 → **8**，剩余 8 条均为既有 `IHttpFetcher` CS0108；无新增警告 |

**F5 后台入口清点**：研究（搜索/读页）、贴图收集、入站音乐分析、音乐听/分享、链接预览全部走 `BackgroundEvaluatorQueue`（容量 64、并发 2、单次 30s、停机排空 3s；按 `BackgroundEvaluatorQueue` 的容量/去重/停机语义）。仍保留 `Task.Run` 的两处是**一次性只读补全**，不产生发送副作用，也不进入研究/发送队列：

- `ReplyPipeline.EnsureGroupContext`：每群首次消息时补群名/历史，`_historyRequested` 去重，每群至多一次；失败只记日志，停机不等待。
- `MemberRoleUseCase`：每（群, 成员）一次群身份查询，`_pending` 去重；失败只记日志，停机不等待。

这两类没有无限增长风险（受群数/成员数约束），但没有队列的容量/超时追踪；后续若接入有界调度，需先登记其顺序与停机语义。

> **2026-10-18 14:00 更正**：上面的清点漏了 `ReplyPipeline.EnrichQuotedFromProtocolAsync`（每条未解析引用一次协议端查询，确认引用机器人时还会补一次回复请求）。Agent-01 第二次复验把它列为 R4；整改后该路径已进入同一有界队列，最新清点见 §25.9.4。

#### 25.8.3 契约与语义变更登记

- `IMusicUseCase.ListenAsync/ShareAsync` 增加可选 `CancellationToken ct = default`（向后兼容；Host 实现 `MusicUseCase` 透传到搜索/发送）。
- `BackgroundJobStatus` 新增 `Abandoned`；`QueueSnapshot` 新增 `Abandoned` 字段，`Outstanding` 现为 `Queued + Running + Abandoned`；`Failed` 只汇总已确认的 Failed/TimedOut/Canceled。
- `SessionQqActionHost` / `AgentCommandService` 增加可选 `Func<string,bool>? pluginAllows`；生产装配由 `CompositionRoot` 传入 `pluginGate.Allows`。
- 未改平台 ID、会话 key、DB schema/表名、secret ref、日志文件名或任何持久化契约。

#### 25.8.4 整改后复跑矩阵（合成/隔离环境）

| 项 | 结果 |
| --- | --- |
| 构建 | Headless `-t:Rebuild` exit 0，**8 警告 / 0 错误**（8 条均为既有 CS0108）；Solution build exit 0；Headless 运行闭包 **39** 个非 PDB 资产 |
| 静态/单元 | EngineProbe **16/0**；Review **1086/0**；Architecture **169/0**（343 文件 / 61,058 行，文件 IO 59）；Safety **594/0**；Chaos **43/0**（43 assertions, status passed）；DeadlineGate **53/0**；Participation **48/0**；ProductionSpec **62/0**；SSRF **77/0**；Concurrency **12/0**（failures=0）；PipelineEval **68/0**；Frontend **370/0** |
| 其他探针 | SettingsScope failures=0；FeishuRemediation 全 PASS；ReverseTransport failures=0 |
| 回退 | 整包回退 **24/0**，assets=37 / candidateAssets=39，proof 落盘；旧证据哈希未改 |
| 独立合成负例 | Agent-01 的原始夹具（未修改）**6/6 PASS**；更新门禁构造的副本同样 **6/6 PASS**（F1×2、F2、F3、F4×2） |
| 集成 | S21 **41/0**、S36 **98/0**、S50 **14/0**、S52 **119/0**、S33 **59/0**、S18 **24/0**、S14 **3/0**、S20 **20/0**、S24 **23/0**、S26 **37/0**、S38 **24/0**、S43 **26/0**、S45 **17/0**、S47 **8/0** |

#### 25.8.5 最终状态与限制

- HEAD 仍 `1f3181e`；工作树 **283** 条（98 修改、20 已暂存删除、140 工作区删除、25 未跟踪）；无 commit/push/deploy；未创建 `BotAgent.Mcp/Panel/web`。
- 证据目录：`.git/agent00-wave3-remediation-20261018-131500/`（日志、探针输出、更新后的独立合成夹具）；不进入公开快照。
- 未做真实生产群聊/日志、真实模型/协议互通、生产压力/延迟/内存验收；S42 官方通道 E2E 仍注释。
- **Wave3 仍处于“整改完成、待独立复验”状态；本记录不代替 Agent-01 复验签字，也不解锁 Wave4。**

| 文件 | SHA-256 |
| --- | --- |
| `src/BotAgent.Engine/Evaluation/BackgroundEvaluatorQueue.cs` | `F57E35C926B6C399CC6035B7F1C70EF5E59199DB10C45B84754DD8B0A561094A` |
| `src/BotAgent.Engine/Reply/ReplyPipeline.cs` | `36DA23DB8A67F2E2085D52CB042987D1CFA9D6163F6866DC40606BD4A2E1C140` |
| `src/BotAgent.Engine/Reply/ReplyPipeline.Delivery.cs` | `622268EAE78E9C97A6BABE3D33B8B57168CE3EB5068668E725D6C6AE87054EB1` |
| `src/BotAgent.Engine/Ports/EnginePorts.cs` | `3FBB5A57BE6CF5FDB5F47C170261B7DCF7D78CC696429AC900A60AFC42A0A485` |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `20426C3B8958699A67F3251B37E506748CCEA86E132BF6D8D3D4C0149CEA7AD0` |
| `src/BotAgent.Headless/Services/Agent/QqActionTool.cs` | `02806626EBBB3F73BDD99E371DCC4E6765BBA51D2871D33FBE051D2B40D2C925` |
| `src/BotAgent.Headless/Services/Agent/AgentCommandService.cs` | `E45B97C74F82963C85C1BBD7653C8A99BEE52B5959272752874A16355A195E8B` |
| `tests/BotAgent.EngineProbe/Program.cs` | `94F696513CEC90982833906ED6E522ED4C40AFB8C370EFC69F3802B115ED1EA4` |
| `tests/BotAgent.ReviewRemediationProbe/EngineWiringTests.cs` | `5F59D4D41DFFBC436064588D05094C1EFB86E10F5C86175431BEC61946F51759` |
| `tests/BotAgent.DeadlineGateProbe/Program.cs` | `83A555C9358E828941914D013F3A05D5741895C12619E190A5B099136E5B84B3` |
| `tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs` | `9C5571B0E22AFE5B8736609C03F5C865403F4BC54D96333DF5E1F41C0E478E92` |
| `tests/BotAgent.IntegrationHarness/StickerScenario.cs` | `6A0596DDE0282B201D533A1A44EE4FD2112796E48B5F8AEB5FE2E2384B93A33F` |

### 25.9 第二次独立复验（R1–R5）与整改（2026-10-18，Agent-00）

#### 25.9.1 复验结论与状态

- Agent-01 第二次独立复验（证据目录 `.git/agent01-wave3-reacceptance-20261018-132159/`，2026-10-18 13:21）结论：**仍未通过；Wave4 / Mcp 继续锁定**。
- 原 F1–F9 的整改被确认有效：原始合成夹具 **6/6**、DeadlineGate **53/0**、S18 **24/0**、整包回退 **24/0（39 资产）** 均恢复。但复验发现 5 个剩余问题：R1 队列超时取消竞态、R2 同步委托绕过预算、R3 后台作业丢失来源租约、R4 引用补查仍是 `Task.Run`、R5 SSRF 探针新增 CS8602。
- 本轮只做 R1–R5 整改、合成复跑与证据登记；**不宣布 Wave3 通过**，不替代 Agent-01 复验签字；未提交、未推送、未部署，未创建 `BotAgent.Mcp`。

#### 25.9.2 逐项整改

| 项 | 根因与整改 | 证据 |
| --- | --- | --- |
| R1 | 超时计时器可能已进入“正在通知”状态，`Cancel()` 立即返回，而 linked CTS 的父回调尚未执行；此时方法返回并 `using` 释放链接源，会摘掉注册，真实工作永远收不到取消（`token=False`、Task 停在 `WaitingForActivation`）。改为：超时分支**不**在返回时释放两个 CTS；取消后把释放挂到工作真正结束的 continuation 上。停机令牌的链接也因此保留到工作结束 | 独立 QueueAudit `cooperative-accounting` 100 轮 **10/10 进程全过**（整改前约 1%–2% 概率卡死）；EngineProbe 新增 40 轮竞态复现测试 **18/0** |
| R2 | `job.Work(...)` 在 `WhenAny` 之前同步调用；同步前缀超预算后返回已完成 Task，会被 `ClassifyCompletedAsync` 记成 `Succeeded`。改为在独立 Task 上调用委托，`Task.WhenAny` 从尝试开始同时约束“调用委托 + 异步等待”；worker 仍占槽位观察真实结束，不启动替代执行 | 独立 QueueAudit `synchronous-delegate-deadline`：预算 25ms、委托 `Thread.Sleep(160)`，整改前 `Succeeded` 约 165–174ms，整改后 `TimedOut` 27–33ms；EngineProbe 新增同步前缀测试 |
| R3 | `QueueBackground` 只把委托放进队列，没有捕获入队方的 `PlatformSourceExecution.Current`；worker 上下文没有每次入站的异步本地租约，`ChannelRouter` 缺租约时会回退到通道默认源，绕过来源退休。改为：入队时捕获租约、作业内 `Enter` 恢复；当 pipeline 的 source 是 `IPlatformSourceRouter` 时，要求租约存在且 `Owner` 就是该路由器，否则拒绝入队（不回退默认源/全局最后来源）。发送边界仍逐次检查 `IsCurrent`；排队音乐在开始与串联副作用前也检查来源是否已退休 | 独立 OriginAudit（真实 `QueueBackground` + 真实 `ChannelRouter`，来源退休后释放 worker）：整改前 `leaseMissing=True canonicalSends=1`，整改后 `leaseMissing=False canonicalSends=0 originalSends=0`，**5/5 PASS**；`EngineWiringTests` 新增“缺租约拒绝 / 恢复同一租约”两项；S24/S36 复跑通过 |
| R4 | `EnrichQuotedFromProtocolAsync` 仍是 `_ = Task.Run(...)`，每条未解析引用一次协议端查询，且确认引用机器人后会 `RequestReply`。改为走 `QueueBackground`（job/dedup id 含会话、入站消息 id、被引用 id），取消时静默退出；容量满/去重时只记日志，不启动无界查询 | `EngineWiringTests`：占住唯一 worker 后调用补查 → 协议端调用 **0**、队列 **Queued=1**；释放后执行 **1 次**；§25.8 清点已在 §25.9.4 更正 |
| R5 | `WebSearchService.SearchAsync` 变为 nullable 后，SSRF 探针直接解引用 `modelResult.Answer`。改为先判非空再访问字段，保留原断言，不 suppress、不降级警告 | SsrfProbe 构建 **0 warning**（整改前 CS8602），运行 **77/0** |

#### 25.9.3 契约与语义变更登记

- 队列执行语义：委托调用本身进入超时预算；超时/停机后 worker 槽位、去重键与“未确认”计数保留到真实工作结束；CTS 生命周期覆盖未结束工作。完全忽略取消且永不返回的工作仍会永久占住一个 worker 槽位（最多 `MaxConcurrency` 个）并持续显示 `Abandoned` — 这是显式的“结果未知”，不是伪造吞吐。
- `QueueBackground` 的可信来源约束：pipeline 绑定 `IPlatformSourceRouter` 时，后台作业必须在入队方持有**该路由器签发**的租约；缺租约或 owner 不匹配直接拒绝。合法无来源系统工作不受影响（source 不是路由器时行为不变）。
- 引用补查从 fire-and-forget 变为有界、可取消、可停机收口的后台作业；被拒绝时只丢失“从协议端补原文/补一次参与评估”的兜底，不阻塞入站，也不产生发送副作用。
- 未改平台 ID、会话 key、DB schema/表名、secret ref、日志文件名或任何持久化契约。

#### 25.9.4 更正后的后台入口清点

- **走 `BackgroundEvaluatorQueue`**：联网搜索、读页、链接预览、贴图收集、入站音乐分析、音乐听/分享、**引用原文补查（本轮新增）**。
- **仍保留 `Task.Run`**：
  - `ReplyPipeline.EnsureGroupContext`：每个群首次消息补群名/历史，`_historyRequested` 去重，只读、不触发回复；
  - `MemberRoleUseCase` 的群成员头衔查询：`_pending` 去重，并由 `WaitForPendingAsync` 以超时等待，只读、不触发回复。
- `BackgroundEvaluatorQueue` 内部也用 `Task.Run` 调用委托（R2）：这是执行器把同步前缀纳入预算的机制，worker 会等待并观察该 Task，不是未登记的 fire-and-forget。

#### 25.9.5 整改后复跑矩阵（合成/隔离环境）

| 项 | 结果 |
| --- | --- |
| 构建 | Headless `-t:Rebuild` exit 0，**8 警告 / 0 错误**（均为既有 `IHttpFetcher` CS0108）；Solution build exit 0；运行闭包 **39** 个非 PDB 资产 |
| 静态/单元 | EngineProbe **18/0**；Review **1091/0**；Architecture **169/0**（src 343 文件 / 61,078 行）；Safety **594/0**；Chaos **43/0**；DeadlineGate **53/0**；Participation **48/0**；ProductionSpec **62/0**；SSRF **77/0**；Concurrency **12/0**；PipelineEval exit 0；Frontend **370/0** |
| 其他探针 | SettingsScope failures=0；FeishuRemediation 全 PASS；ReverseTransport failures=0 |
| 回退 | 整包回退 **24/0**，assets=37 / candidateAssets=39，proof 落盘；旧 37 资产与前镜像哈希未改 |
| 独立复验审计 | QueueAudit **10/10 进程**（每题 100 轮 + 同步前缀）；OriginAudit **5/5**（`leaseMissing=False canonicalSends=0 originalSends=0`）；Agent-01 原始合成夹具（未修改）**6/6** |
| 集成 | S14 **3/0**、S18 **24/0**、S20 **20/0**、S21 **41/0**、S22 **35/0**、S23 **12/0**、S24 **23/0**、S26 **37/0**、S33 **59/0**、S36 **98/0**、S38 **24/0**、S43 **26/0**、S45 **17/0**、S47 **8/0**、S50 **14/0**、S52 **119/0** |

**范围外已知红**：`BridgeProbe` 在 hello 注册等待阶段超时（`Wait` 4s）。`AgentBridgeServer.cs` 与探针文件均不是本轮改动（最后修改 2026-10-17 23:00 / 2026-10-06），也不在 Wave3 验收矩阵；本轮未修、未用它充当证据，登记为独立待办。

#### 25.9.6 最终状态与限制

- HEAD 仍 `1f3181e`；工作树 **284** 条（99 修改、20 已暂存删除、140 工作区删除、25 未跟踪）；无 commit/push/deploy；未创建 `BotAgent.Mcp/Panel/web`。
- 证据：`.git/agent01-wave3-reacceptance-20261018-132159/`（复验原始证据）、`.git/agent00-wave3-r1r5-remediation-20261018-1400/`（本轮复跑日志）。
- 未做真实生产群聊/日志、真实模型/协议互通、生产压力/延迟/内存验收；S42 官方通道 E2E 仍注释；`BridgeProbe` 红见 §25.9.5。
- **Wave3 仍处于“R1–R5 整改完成、待第二轮独立复验”状态；本记录不代替 Agent-01 复验签字，也不解锁 Wave4。**

| 文件 | SHA-256 |
| --- | --- |
| `src/BotAgent.Engine/Evaluation/BackgroundEvaluatorQueue.cs` | `8A19799361D5CE7E87809F9AD30AC2AE13E8F8F6E098C225D8D96C9E0B0C25B0` |
| `src/BotAgent.Engine/Reply/ReplyPipeline.cs` | `14F8809D973C6C992BE24362C25023676B9425490D48E9FFA48C7C3F7E8AFA09` |
| `src/BotAgent.Engine/Reply/ReplyPipeline.Delivery.cs` | `95B863647A580CC76E8C87D6C4FC7EB0B4E980F10C8D1040C196BBAE1AE9A7EA` |
| `tests/BotAgent.EngineProbe/Program.cs` | `49E3B5B9861868AA3588442A1EBA84D31B46C02AAC3E4E74476F6D4705AD016A` |
| `tests/BotAgent.ReviewRemediationProbe/EngineWiringTests.cs` | `629A279B6012C5C0247445357CC86DF46B1664CA9F6FB307950292F759C73BA4` |
| `tests/BotAgent.SsrfProbe/Program.cs` | `26430789DD31506F10F43CBDDB3B25E32B2F4EDEAC1B213D5D1C0D7388DEBB11` |

> 后续 R7/R8 整改改动了 `ReplyPipeline.cs` / `ReplyPipeline.Delivery.cs`，上表两项指纹已被 §25.10.5 取代；`BackgroundEvaluatorQueue`、EngineProbe、SSRF 未再变。

### 25.10 第三轮独立验收（R7/R8 通过，R9 整改）（2026-10-18，Agent-00）

#### 25.10.1 验收结论与登记

- Agent-01 第三轮独立验收（证据目录 `.git/agent01-wave3-r7r8-acceptance-20261018-145231/`，2026-10-18 14:52）结论：**R7、R8 指定复现通过；R9（定时贴图审核在停用后仍开始下一项模型 I/O）为 High 阻断，Wave3 NotAccepted，Wave4 / Mcp 继续锁定。**
- 报告列出的四项当前源码指纹中，`ReplyPipeline.cs`（`B651DBFF…`）与 `ReplyPipeline.Delivery.cs`（`DB1BE363…`）是 R7 的整改；`StickerService.cs` / `EngineWiringTests.cs` 在 R9 整改后已更新，最新指纹见 §25.10.5。
- R7（通用媒体门禁）与 R8（贴图批次每次下载前复核）由 Agent-01 的 InboundGateAudit **6/6**、MultiCallGateAudit **4/4** 独立确认；本轮未回退这两项，只在其上补 R9。
- 本轮仅做 R9 最小整改、合成复跑与证据登记；未提交、未推送、未部署，未创建 `BotAgent.Mcp`。

#### 25.10.2 R9 根因与整改

- 根因：`StickerService.EnsureTimer(false)` 只 `Dispose` 定时器，没有让**已经运行中的 TickAsync/DrainDescribeAsync** 知道停用；循环只检查 `_disposed`，所以第一张图在途的 `DescribeStickerAsync` 返回后，会继续出队第二张并开始第二次模型 I/O。Tick 随后的策展调用也没有停用复核。
- 整改（`src/BotAgent.Engine/Stickers/StickerService.cs`）：
  - 新增巡检代际 `_generation` 与 `_workEnabled`。`EnsureTimer(enabled)` 每次调用都递增代际并记录当前是否允许工作；`Dispose` 也递增代际并清零。
  - `TickAsync` 在入口捕获代际；`DrainDescribeAsync(generation)` 在每轮出队前、每次 `ReadBytesAsync` 后 / `DescribeStickerAsync` 前复核 `未释放 && 当前代际允许 && 代际未变`；停用时把尚未开始的那张重新入队而不是丢弃。
  - 策展（`CurateAsync(force:false)`）是同一 tick 里的下一项模型 I/O，调用前单独复核；手动 `force:true` 的面板动作不受此路径影响。
  - 语义：**已经发出的那一次请求允许完成**（可能已产生外部 I/O），但停用后不再开始下一项；重新启用是一次新代际，旧代际的在途循环不会继续；未处理的排队项保留，重新启用后由新代际继续。
- 相邻循环盘点：`BotScheduler.RunProfileSummarizationAsync` 原来只在整轮入口检查插件，现在每个候选模型调用前都复核“宿主未释放 + 画像开关 + profiles 插件”；本轮的独立夹具未覆盖画像路径，不冒报其已通过。
- 调度侧无需改 `BotScheduler` 的定时器重建逻辑：插件关闭 → `stickersEnabled=false` → `RebuildIfNeeded` → `EnsureTimer(false)`，现在会同时停住运行中的 tick。

#### 25.10.3 回归覆盖

- `EngineWiringTests` 新增 R9 两条用例（+8 断言）：真实 `BotScheduler.RebuildIfNeeded` + 真实 `StickerService` + 假仓储/模型；正例 **2 次**模型调用；第一项在途时关闭 stickers 插件并重建 → **1 次**，定时器已撤，第二项保留在队列。
- Agent-01 的 SchedulerGateAudit（真实调度路径）：
  - 反射调用 Tick：正例 2/2、撤权负例 **1/1**，`failures=0`；
  - `--real-timer` 真实 10 秒定时器：正例 2/2、撤权负例 **1/1**，`failures=0`；连跑 3 次全过。
- R7/R8 复核：InboundGateAudit **6/6**；MultiCallGateAudit **4/4**（音乐两首、贴图两张，在途撤权均停在 1 次）。
- 其他独立审计：QueueAudit 3 个进程 × 100 轮协作取消 + 同步前缀预算全过；OriginAudit **5/5**；原始 F 夹具 **6/6**。

#### 25.10.4 整改后复跑矩阵（合成/隔离环境）

| 项 | 结果 |
| --- | --- |
| 构建 | Headless `-t:Rebuild` exit 0，**8 警告 / 0 错误**（既有 CS0108）；Solution exit 0；Harness exit 0；运行闭包 **39** 个非 PDB 资产 |
| 静态/单元 | Review **1133/0**；EngineProbe **18/0**；Architecture **169/0**（src 343 文件 / 61,207 行）；Safety **594/0**；Chaos **43/0**；DeadlineGate **53/0**；SSRF **77/0**；Concurrency **12/0**；Participation **48/0**；ProductionSpec **62/0**；PipelineEval exit 0；Frontend **370/0** |
| 其他探针 | SettingsScope failures=0；FeishuRemediation 全 PASS；ReverseTransport failures=0 |
| 回退 | 整包回退 **24/0**，assets=37 / candidateAssets=39，proof 落盘；旧 37 资产与前镜像哈希未改 |
| 独立审计 | SchedulerGateAudit 常规 2/2 + 真实定时器 3/3；InboundGateAudit 6/6；MultiCallGateAudit 4/4；QueueAudit 3/3；OriginAudit 5/5；原始 F 夹具 6/6 |
| 集成 | S14 3/0、S18 24/0、S20 20/0、S21 41/0、S22 35/0、S23 12/0、S24 23/0、S26 37/0、S33 59/0、S36 98/0、S38 24/0、S43 26/0、S45 17/0、S47 8/0、S50 14/0、S52 119/0 |

#### 25.10.5 最终状态与限制

- HEAD 仍 `1f3181e`；工作树 **284** 条（99 修改、20 已暂存删除、140 工作区删除、25 未跟踪）；无 commit/push/deploy；未创建 `BotAgent.Mcp/Panel/web`。
- 证据：`.git/agent00-wave3-r9-remediation-20261018-1505/`；Agent-01 第三轮原始证据留在其验收目录。
- 未做真实生产群聊/日志、真实模型/协议互通、生产压力/延迟/内存、完整 50 场景集成或 S42 官方 E2E；`BridgeProbe` 仍是 §25.9.5 的既有红。画像巡检的逐项复核只有代码层改动，未由独立夹具覆盖。
- **Wave3 仍处于“R9 整改完成、待第三轮独立复验”状态；本记录不代替 Agent-01 复验签字，也不解锁 Wave4。**

| 文件 | SHA-256 |
| --- | --- |
| `src/BotAgent.Engine/Stickers/StickerService.cs` | `253D3A53CC6429B0D91B9E4DC17F71A2060A19DF97E8B491C314BB2F60B00CC3` |
| `src/BotAgent.Headless/Services/Ops/BotScheduler.cs` | `46372021DA376370F9C5F46E0D420FB3ED4FC459C8202E3E22C34C39D02E7758` |
| `src/BotAgent.Engine/Reply/ReplyPipeline.cs` | `B651DBFFA1767BF9F5481657AF30FD19CD41C95FE3C3747EEF5E349F23110820` |
| `src/BotAgent.Engine/Reply/ReplyPipeline.Delivery.cs` | `DB1BE3635731846B59332197E22490CA11A50D6E808A30E6F56A850017D9C044` |
| `tests/BotAgent.ReviewRemediationProbe/EngineWiringTests.cs` | `FE854D6E046B105474CC0DDF8510C9E56AC7E95DC55805C3F972223D5E3C638D` |

> R10 整改改动了 `StickerService.cs`、`WebUiServer.*`、`CompositionRoot.cs` 与 `EngineWiringTests.cs`；§25.10.5 的对应指纹已被 §25.11.5 取代。

### 25.11 第四轮独立验收（R9 通过、R10 整改）（2026-10-18，Agent-00）

#### 25.11.1 验收结论与登记

- Agent-01 第四轮独立验收（证据目录 `.git/agent01-wave3-r9-acceptance-20261018-151737/`，2026-10-18 15:27）结论：**R9 指定复现已通过；R10（禁用 stickers 插件后，既有贴图面板“导入/立即巡检”仍启动协议/模型调用）为 High 阻断，Wave3 NotAccepted，Wave4 / Mcp 继续锁定。**
- 报告位置：`PanelStickerAudit/Program.cs` 经真实 `RouteAsync` → 真实 handler；停用且调度重建后 import 仍下载 2 次、fetch 1 次，curate 仍调用模型 1 次；五个独立进程一致。
- 本轮只做 R10 最小整改、合成复验与证据登记；未提交、未推送、未部署，未创建 `BotAgent.Mcp`。

#### 25.11.2 R10 根因与整改

- 根因：面板 curate/import 分支各自 `Task.Run`，没有查询插件注册表；`StickerService.CurateAsync` / `ImportFromAlbumAsync` 不经过 R9 的 `WorkAllowed`/代际；`ChannelRouter` 的 `customfaces` 只检查平台注册/连接/启用/贴图能力，模型客户端也没有插件上下文。`force:true` 不是停用插件的授权例外。
- 服务边界（`src/BotAgent.Engine/Stickers/StickerService.cs`）：
  - 新增 `PluginEnabled`（装配方接入 `PluginGate.Allows("stickers")`）与 `CanOperate`（`EnableStickers && StickerLibraryMax > 0 && 插件`）。
  - `CurateAsync` / `ImportFromAlbumAsync` 入口先判定；`FetchCustomFacesAsync`、每次 `DownloadImageAsync`、`CurateStickersAsync` 前逐项复核；导入中途撤权停在下一张，已发出的请求允许完成，但撤权后的策展结果不落成删除。
  - 两个手动方法接收 `CancellationToken`；`CollectAsync` 也补了每次下载前后的同一判定，避免直接调用绕过。
- 面板边界（`WebUiServer.Stickers.cs` / `WebUiServer.cs`）：
  - `StickerOperationsEnabled()` = 功能开关 + 库上限 + 插件注册表 + 服务判定；停用时直接 **409**，不创建后台作业。
  - 通过宿主注入的既有 `BackgroundEvaluatorQueue` 接纳（dedup key `panel-sticker-{curate|import}`）；队列未接线时 **503 fail closed**，移除独立 `Task.Run` 旁路。
  - 列表响应 `enabled` 使用同一判定；`app.js` 展示服务端错误文案。
- 装配（`CompositionRoot.cs`）：插件门禁建立后设置 `stickers.PluginEnabled`，把既有 `background` 传给 `WebUiServer`；未新增接口或持久化协议。

#### 25.11.3 回归覆盖

- `EngineWiringTests` 新增 R10 七条断言：curate/import 正例、停用负例、导入首张在途撤权停在 1 张并报告提前结束。
- 本目录 `PanelStickerAudit`（合成仓储/模型/协议 + 真实路由 + 真实有界队列）：import 正例 200/下载 2/fetch 1；停用 409/0/0；curate 正例 200/模型 1；停用 409/0；重复 import 首 200、次 409、下载仍 2。三个独立进程 `failures=0`。
- 复制并重建 Agent-01 的独立审计：Lifecycle 5/5、SchedulerGate 常规与真实定时器、ProfileScheduler 4/4、InboundGate 6/6、MultiCallGate 4/4、Queue 3/3、Origin 5/5，均 `failures=0`。PanelConcurrencyAudit 的旧夹具未注入 `_background`，本轮 fail-closed 后会得到 503，不再作为通过证据；队列接纳由去重用例覆盖。

#### 25.11.4 整改后复跑矩阵（合成/隔离环境）

| 项 | 结果 |
| --- | --- |
| 构建 | Headless `-t:Rebuild` exit 0，**8 警告 / 0 错误**（既有 CS0108）；Solution **0 警告 / 0 错误**；Harness exit 0 |
| 静态/单元 | Review **1140/0**；EngineProbe **18/0**；Architecture **169/0**（src 343 文件 / 61,374 行）；Safety **594/0**；Chaos **43/0**；DeadlineGate **53/0**；SSRF **77/0**；Concurrency **12/0**；Participation **48/0**；ProductionSpec **62/0**；PipelineEval **68/68**；Frontend **370/0** |
| 其他探针 | SettingsScope failures=0；FeishuRemediation 全 PASS；ReverseTransport failures=0 |
| 回退 | 整包回退 **24/0**，assets=37 / candidateAssets=39；proof `rollback-proof-af6a68e85a034b4e84cee4335be8bf74.json`，SHA-256 `D3284B03FD7EACD9DC1ED26A43A93E0533E96B16910881B3E710F4A43115DA36` |
| 集成 | S14 3/0、S18 24/0、S20 20/0、S21 41/0、S22 35/0、S23 12/0、S24 23/0、S26 37/0、S33 59/0、S36 98/0、S38 24/0、S43 26/0、S45 17/0、S47 8/0、S50 14/0、S52 119/0 |

#### 25.11.5 最终状态与限制

- HEAD 仍 `1f3181e`；工作树 **285** 条（100 修改、20 已暂存删除、140 工作区删除、25 未跟踪）；无 commit/push/deploy；未创建 `BotAgent.Mcp/Panel/web`。
- 证据：`.git/agent00-wave3-r10-remediation-20261018-1540/`；Agent-01 第四轮原始证据留在其验收目录。
- 未做真实生产群聊/日志、真实模型/协议互通、生产压力/延迟/内存、完整 50 场景集成或 S42 官方 E2E；`BridgeProbe` 仍是 §25.9.5 的既有红。PanelConcurrencyAudit 只覆盖历史 admission 观察，不声称真实外部网络并发。
- **Wave3 仍处于“R10 整改完成、待独立复验/负责人签字”状态；本记录不代替 Agent-01 复验，也不解锁 Wave4。**

| 文件 | SHA-256 |
| --- | --- |
| `src/BotAgent.Engine/Stickers/StickerService.cs` | `C4EE80A911F5E19016D62AD007D56E63A374EC0FABAC5DDF5CE6A5F15481E4B9` |
| `src/BotAgent.Headless/Adapters/Panel/WebUiServer.Stickers.cs` | `83D8FE81C795DB8592F5C5C721694B44FC7660BAB0916C2EEF9C9065A81532E5` |
| `src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs` | `08157D1F699C2D28E631D693DF4B021954B01580C5F335ADE3E7A16F4CC54524` |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `37E2C10965CC0EFD211F9B0316B89B87F62000658772B196A124CBAF8A757417` |
| `tests/BotAgent.ReviewRemediationProbe/EngineWiringTests.cs` | `7587A9AE66DCF3CEE4E020707AF6259E65E4FC69D392826D650F60F2658925A3` |
| `src/BotAgent.Headless/wwwroot/app.js` | `78DFBB9EA786016D3AAB3CD3F48932286516E50D01F990BF2C23D4DBDEC7CE56` |

### 25.12 第五轮独立验收（R10 关闭）（2026-10-18，Agent-01）

#### 25.12.1 验收结论与登记

- Agent-01 第五轮独立验收（证据目录 `.git/agent01-wave3-r10-acceptance-20261018-155953/`，2026-10-18 16:24）结论：**Accepted（仅限当前未提交工作树与合成验证范围）；R10 已关闭；Wave3 到达第四阶段入口。Wave4 / Mcp 仍锁定，本结论不构成 Wave4 施工授权。**
- 独立面板审计 **40/40 断言**，3 个 fresh process 逐行一致：启用 import 200/协议 fetch 1/下载 2；停用 import 409/0/0；启用 curate 200/模型 1；停用 curate 409/0；导入首张在途撤权停在第 1 张；重复 import 首 200/次 409/下载仍 2；队列未接线 503 fail closed。
- 源码核对确认服务入口与每次 `FetchCustomFacesAsync` / `DownloadImageAsync` / `CurateStickersAsync` 前均有插件 + 功能开关复核；面板停用即 409、走既有有界队列、无独立 `Task.Run` 旁路；`CompositionRoot` 真实接线 `PluginEnabled` 与 `background`。
- 未提交、未推送、未部署；未创建 `BotAgent.Mcp`；未启动 Wave4。

#### 25.12.2 全矩阵与漂移

| 项 | 结果 |
| --- | --- |
| 构建 | Headless `-t:Rebuild` exit 0；Solution exit 0；Harness exit 0 |
| 静态/单元 | Review **1140/0**；Engine **18/0**；Architecture **169/0**（343 文件 / 61,375 行，独立报告口径）；Safety **594/0**；Chaos **43/0**；DeadlineGate **53/0**；SSRF **77/0**；Concurrency **12/0**；Participation **48/0**；ProductionSpec **62/0**；Frontend **370/0**；PipelineEval **68/68** |
| 其他探针 | SettingsScope failures=0；ReverseTransport failures=0；FeishuRemediation 24 PASS / 0 FAIL |
| 回退 | 整包回退 **24/0**，assets=37 / candidateAssets=39；proof `rollback-proof-d6fcb065606d4145835ffde4e63ffb9e.json`，SHA-256 `98D9FFE5B49610D6E2B664A49C33752D6EB93CC4F711F0FA3B7376A30CB523AD` |
| 集成 | S14 3/0、S18 24/0、S20 20/0、S21 41/0、S22 35/0、S23 12/0、S24 23/0、S26 37/0、S33 59/0、S36 98/0、S38 24/0、S43 26/0、S45 17/0、S47 8/0、S50 14/0、S52 119/0 |
| R9/lifecycle | Lifecycle 5/5、SchedulerGate 2/2 + real-timer 2/2、ProfileScheduler 4/4、InboundGate 6/6、MultiCallGate 4/4、Queue 2/2、Origin 1/1；均 failures=0 |
| 漂移 | HEAD 仍 `1f3181e`；工作树 **285** 条（100 M、20 staged D、140 worktree D、25 untracked）；验收前后 six-file SHA-256 与 `git status` 逐行一致；无产品源码漂移 |

> 独立报告登记 Architecture 为 343 文件 / 61,375 行；Agent-00 上一轮本地扫描为 61,374 行，差 1 行未影响任何断言，且六个关键文件哈希与工作树清单前后一致。

#### 25.12.3 当前状态与限制

- **Wave3 已通过第五轮独立验收，停在 Wave4 入口。** Wave4 / Mcp 仍未解锁：负责人尚未明确授权第四阶段施工；`src/BotAgent.Mcp` 不存在；MCP 协议/SDK 冻结与工具投影未登记。
- 本结论只绑定当前未提交工作树与合成验证，不代表公开分支、完整 CI、生产或部署验收；不代替负责人签字。
- 未覆盖：真实生产群聊/日志、真实模型/协议互通、生产压力/延迟/内存、完整 50 场景集成、S42 官方 E2E、浏览器级面板 E2E；`BridgeProbe` 仍是 §25.9.5 的既有红；PanelConcurrencyAudit 旧夹具未注入 `_background`，不作为通过证据。
- 停止线：不创建 `BotAgent.Mcp`、不登记 MCP server/tool、不修改协议/SDK/出网；Wave4 只有在负责人明确解锁并另行登记写权/契约后才可开始。

#### 25.12.4 Agent-00 最终核收（2026-10-18 16:27）

负责人本次指令“对第三阶段进行验收”作为验收请求；Agent-00 对第五轮独立验收做了只读核收，未重复运行已通过的测试（源码自 16:24 验收后未变）。

| 核收项 | 证据 | 结论 |
| --- | --- | --- |
| 独立结论 | `.git/agent01-wave3-r10-acceptance-20261018-155953/acceptance-report.md` 第 3–11 行 | Accepted（限定当前工作树/合成验证） |
| R10 正负例 | 独立 `PanelStickerAudit`：启用 import 200/fetch 1/下载 2；停用 409/0/0；启用 curate 200/模型 1；停用 409/0；首张在途撤权停 1；重复请求 200/409、下载仍 2；队列未接线 503 | **40/40 断言，3 个 fresh process 一致** |
| 全矩阵 | Review 1140/0、Engine 18/0、Architecture 169/0、Safety 594/0、Chaos 43/0、DeadlineGate 53/0、SSRF 77/0、Concurrency 12/0、Participation 48/0、ProductionSpec 62/0、Frontend 370/0、PipelineEval 68/68；16 个 IT 全绿；整包回退 24/0；Lifecycle/Scheduler/Profile/Inbound/MultiCall/Queue/Origin 全过 | 全部通过 |
| 源码漂移 | HEAD `1f3181e`；工作树 285 条；六个关键文件 SHA-256 前后一致（当前复核仍为 `C4EE80A9…` / `83D8FE81…` / `08157D1F…` / `37E2C109…` / `7587A9AE…` / `78DFBB9E…`） | 无产品源码漂移 |
| 写权/边界 | Agent-01 仅写私有证据目录；未改 src/tests/docs/wwwroot；未 commit/push/deploy；未创建 `BotAgent.Mcp` | 边界成立 |
| 限制 | 合成验证；无真实生产/模型/协议互通；无 S42、浏览器/认证同源 E2E；`BridgeProbe` 既有红；PanelConcurrency 旧夹具不计入；`PluginEnabled` 未接线时库级 fail-open，但生产唯一装配已接线 | 已登记，不构成 R10 阻断 |

**最终结论：第三阶段（Wave3）验收完成，Accepted。** 该结论绑定当前未提交工作树与合成验证范围，不代替生产/部署验收。**Wave4 / Mcp 仍未解锁**：负责人尚未明确授权第四阶段施工；`src/BotAgent.Mcp` 不存在，未登记 MCP server/tool，未修改协议/SDK/出网。下一步只有负责人明确解锁并另行登记后，才可开始 Wave4。

---

## 二十六、Wave4（Mcp）冻结、施工与停止线（2026-10-18 16:41 起）

### 26.1 授权与波次边界

- 负责人本次目标原文：**“根据文档，多agent协作推进至第五阶段入口”**。该指令明确解锁 **Wave4（Mcp）**，并要求完成 Wave4 后**停在 Wave5 入口**；Wave5（ZBA/Panel/CLI）与 Wave6（独立 Web）仍未启动。
- 本批只做 MCP 客户端/治理/宿主接入/合成契约验证；不创建 `BotAgent.Panel`/`web`，不修改面板 ZBA 路由，不提交、不推送、不部署，不读取真实群聊/成员/DB/日志正文。
- 多 agent 分工：Agent-00 负责总纲、共享文件（`BotAgent.slnx`、Headless 宿主/装配/设置）与最终核收；Agent-05 负责 `src/BotAgent.Mcp/**` 与 MCP 合成测试；Agent-04 负责 Engine 侧工具治理用例；Agent-06 只做早期宿主/出网评审，不写共享文件；Agent-01 在实现后做独立验收。

### 26.2 版本、SDK 与传输冻结

| 项 | 冻结值 | 依据 / 说明 |
| --- | --- | --- |
| 协议版本 | **MCP `2025-11-25`** | `05-agent-mcp-ecosystem.md` 指定的参考版本；SDK 2.2.0 支持该日期版本 |
| C# SDK | **`ModelContextProtocol.Core` 2.2.0**（Apache-2.0，net8.0 目标） | 官方 SDK；`McpClient` + `HttpClientTransport`，显式 `ProtocolVersion = 2025-11-25` |
| 传输 | **仅 Streamable HTTP**（`HttpTransportMode.StreamableHttp`） | 首版不启用 HTTP+SSE 兼容路径；`Stdio` 不在本批批准范围；Docker daemon/任意本地高权限承载不纳入 |
| 出网 | 复用 `HttpFetcher` 的唯一 socket 构造点；MCP 专用实例 `allowAutoRedirect:false`、默认 `rejectPrivateDestinations:true` | SDK 使用注入的 `HttpClient`，不自行建连；端点先过 `SafeUrl.TryValidate`；重定向不自动跟随 |
| 认证 | 每服务器 `Authorization: Bearer <secret>`；secret 由 `ISecretsRepository` / 环境变量按名字解析 | 不回显、不写 settings、不进日志/审计/异常文本 |
| 服务端标识 | 配置中的稳定 `id`（`[A-Za-z0-9_-]{1,32}`），工具名投影为 `mcp.<id>.<tool>` | 不同服务器命名空间隔离；重名/非法名丢弃 |
| 兼容矩阵 | 接受：2025-11-25 Streamable HTTP JSON 响应；拒绝：2025-06-18、2024-11-05、旧 HTTP+SSE、stdio | 负例由合成服务器返回不同版本/模式验证；不声称第三方服务器互通 |
| 配置入口 | `BOTAGENT_MCP_ENABLED`、`BOTAGENT_MCP_SERVERS`（JSON 数组：`id`/`url`/`tokenSecret`/`allowedTools`/`callTimeoutSeconds`/`maxResultBytes`）、`BOTAGENT_MCP_ALLOW_PRIVATE` | Wave4 不接面板；配置变更需重启；`allowedTools` 为空 = 该服务器一个工具都不授权 |

### 26.3 施工门禁与写权

- Agent-05 只写 `src/BotAgent.Mcp/**` 与 `tests/BotAgent.McpProbe/**`；Agent-04 只写 `src/BotAgent.Engine/Mcp/**`；Agent-00 写 `BotAgent.slnx`、`src/BotAgent.Headless/**`（共享宿主/装配）与总纲/状态文档；Agent-06 只读评审；Agent-01 只写私有证据目录。
- 不新增 `BotAgent.Panel`/`web`；不把 MCP 工具塞进静态 `ToolDirectory`（避免触碰 10/10/6 与 26/16 硬计数），由 Engine 的 `McpToolUseCase` 维护独立动态目录与 `ToolGate` 策略。
- 验收必须同时具备：版本/传输负例、发现/Schema/未知工具/越权/秘密不回显、调用者取消与超时区分、结果过大/畸形、重连不重放副作用、停机释放、SSRF/认证边界、宿主真实调用链与 Engine 审计。

### 26.4 实施与验收记录

#### 26.4.1 实施内容（Agent-05 / Agent-04 / Agent-00）

- **Core**：`src/BotAgent.Core/Domain/Mcp/McpContracts.cs`（`IMcpToolSource`、`McpServerOptions`、`McpToolInfo`、`McpCallResult`、上限；`ToString` 遮 token/query/userinfo/内容）、`McpSchemaValidator.cs`（有界 JSON Schema 子集；默认拒绝未知字段；296 行）。
- **Mcp**：`src/BotAgent.Mcp/McpToolSource.cs`（官方 SDK `McpClient` + 注入的 `HttpClientTransport`；`ProtocolVersion=2025-11-25`、`StreamableHttp`、无 SSE/stdio；发现过滤、`ConcurrentDictionary` 路由、调用超时、调用者取消区分、未知结果不重放、结果字节/类型上限、DELETE 收口）。
- **Engine**：`src/BotAgent.Engine/Mcp/McpToolUseCase.cs`（动态工具快照、`ToolGate` 的 `RemoteAgent` 类别 + 显式 `HighRiskExceptions`、预算、Schema、形状审计）、`NullMcpToolSource.cs`。
- **Host**：`src/BotAgent.Headless/Host/McpConfiguration.cs`（`BOTAGENT_MCP_*`、密钥库/环境变量 secret、`SafeUrl`、默认拒绝私网）、`CompositionRoot.cs`（唯一装配 + `AppGraph` 停机）、`ServerAgentRunner.cs`（`//` 路径；MCP 内容不回显；统一执行入口 `no_conversation`）、`HttpFetcher.cs`（`SharedClient` 复用唯一连接策略）。
- **测试**：`tests/BotAgent.McpProbe/**`（真实客户端 + 进程内合成服务器、Engine 治理、Host 配置、`ToString`/Schema 边界）；`tests/BotAgent.IntegrationHarness/McpScenario.cs` + `MockMcpServer.cs`（S53 宿主真链路）；CI/`CONTRIBUTING`/`.env.example` 同步。

#### 26.4.2 最终矩阵（2026-10-18 17:40 前后，当前工作树/合成范围）

| 项 | 结果 |
| --- | --- |
| Headless `-t:Rebuild` | exit 0；8 个既有 CS0108 警告 / 0 错误 |
| Solution build | exit 0；0 警告 / 0 错误 |
| McpProbe | **118/0**，3 个 fresh process 一致（A 59 / B 33 / C 26） |
| Architecture | **169/0**（349 文件 / 62,947 行；最长方法 `Build` 198） |
| Safety / Engine / Review | **594/0** / **18/0** / **1140/0** |
| SSRF / Production / SettingsScope | **77/0** / **62/0** / failures=0 |
| Chaos / DeadlineGate / Concurrency | 43/0 / 53/0 / 12/0 |
| Participation / Frontend / PipelineEval | 48/0 / 370/0 / 68/68 |
| Feishu / ReverseTransport | 24 PASS / failures=0 |
| Integration（17） | S14 3/0、S18 24/0、S20 20/0、S21 41/0、S22 35/0、S23 12/0、S24 23/0、S26 37/0、S33 59/0、S36 98/0、S38 24/0、S43 26/0、S45 17/0、S47 8/0、S50 14/0、S52 119/0、**S53 11/0**；全部 exit 0 |

McpProbe 覆盖：协议 2025-11-25 正例；2025-06-18 / 2024-11-05 负例；仅 SSE 的 405 负例（无静默回退）；Bearer/协议头；发现过滤；echo 成功；未知/未授权工具；超大结果；非文本内容省略；tool error；畸形 JSON；超时 vs 调用者取消；401；404 重连不重放；DELETE 停机；Engine 授权/Schema/预算/审计；Host 私网拒绝/放行；secret/URL query/userinfo 日志脱敏；`no_conversation`；信封剥离；限定名小写化；无 `properties` 的 `additionalProperties=false`。

#### 26.4.3 独立验收（Agent-01）

- 证据目录：`.git/agent01-wave4-acceptance-20261018-171325/`；首轮报告结论 **Accepted**；最终树 `reverify2` addendum 结论 **Accepted**（仅限当前未提交工作树与合成验证范围）。
- 首轮矩阵：McpProbe 113/0 ×3、17 个 IT 571/0、全探针通过、20 个关键文件哈希与 `git status` 前后一致；发现 8 项非阻断问题。
- Agent-00 处理后复验：限定名统一小写（A11b）、`_routes` 改 `ConcurrentDictionary`、`ToString` 去 userinfo、无 `properties` 的 additionalProperties 负例（C9）；最终树 McpProbe **118/0 ×3**、Architecture **169/0**、Safety **594/0**、Engine **18/0**、Review **1140/0**、SSRF **77/0**、S53 **11/0**、S21/S36/S38/S50/S52 全绿；20 个关键文件哈希与 `git status` 前后一致。
- 复验期间曾出现一次瞬态 `McpSchemaValidator.cs(311)` Domain 行数超标（Architecture 168/1）；当场压缩到 296 行并重新基线，最终树 169/0。该瞬态事件保留在 Agent-01 报告中，不作为最终失败。
- **2026-10-18 17:58 状态更正**：Agent-01 复验（证据目录 `.git/agent01-wave4-reacceptance-20261018-175027/`）判定 **NotAccepted**。W4-R1（初始化中释放后仍发布迟到客户端并继续 `tools/list`）、W4-R2（缓存连接成功调用后状态停在 `Connecting`）、W4-G1（回退夹具只接受 39 资产，当前 48 资产运行闭包无 proof）阻断准出；本节 Accepted 仅指 17:13 首轮范围，不再代表当前树。整改与复跑见 §26.5。
- 结论（历史，已被 17:58 复验覆盖）：**Wave4（Mcp）首轮验收曾判 Accepted**；绑定当时未提交工作树与合成验证，不代替生产/部署验收。**Wave5（ZBA/CLI/Panel）未启动。**

#### 26.4.4 关键文件哈希（首轮最终树；历史，当前见 §26.5.3）

| 文件 | SHA-256 |
| --- | --- |
| `src/BotAgent.Core/Domain/Mcp/McpContracts.cs` | `EA36EC883F5D3B3EBD5734314863BCB6F3A4AA2F3DE4EE801D96A947D1159084` |
| `src/BotAgent.Core/Domain/Mcp/McpSchemaValidator.cs` | `C77B61651468436E7E80BFE8B57EA11F6F0D56AAB40EF61056AE2877C46F3E46` |
| `src/BotAgent.Mcp/McpToolSource.cs` | `622FA7F71D513C074F8C0490C94D5B9E6EC7B20FAC00953210EAF62AF88CD9ED` |
| `src/BotAgent.Engine/Mcp/McpToolUseCase.cs` | `A749E0E5F0CB6F877147C081643A3E662B5905EAA3FA49F940609540B7FC7CA7` |
| `src/BotAgent.Headless/Host/McpConfiguration.cs` | `8C77101CE0D0E209C885389A5665AD245BE5DAD6AEE54AFB0AAB8CBD4A68211C` |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `E88475F6F0705377F9F7A69EA045679E24F36BC6A1F80DDF780A3DEC6799EBFB` |
| `src/BotAgent.Headless/Services/Agent/ServerAgentRunner.cs` | `A674DADBFB7E4971D269C1B1A568121C3A7F5D3BC7A59F3C30727E11704E201C` |
| `tests/BotAgent.McpProbe/Program.cs` | `9ABBFEC6D1D724047C0E3168910AFFDDFEAFB6489A453E189FBCE73F425FACD7` |
| `tests/BotAgent.IntegrationHarness/McpScenario.cs` | `9FE185BD6BC5E31E61CB9647A6A2072432D01B8606088DFF7FED15868E525728` |

#### 26.4.5 已知限制与停止线

- 配置仅 `BOTAGENT_MCP_*`（需重启），无面板/ZBA/hot reload；工具目录不进入静态 `ToolDirectory`/`/api/tools`。
- 仅验证官方 SDK + 进程内合成 2025-11-25 服务；未验证第三方真实 MCP 服务器互通、生产部署、真实模型/协议、生产压力/延迟/内存、S42、浏览器面板 E2E。
- `maxResultBytes` 在 SDK 反序列化后生效，不是 wire 级上限；`$ref`/`oneOf`/`anyOf`/`allOf`/`pattern` 等 Schema 关键字不执行；总调用预算默认 8（`McpToolUseCase` 构造参数可调，当前装配未暴露 env），预算使用调用方传入的 `callIndex`（`//` 路径传 `step - 1`）。
- HEAD 仍 `1f3181e`；工作树 290 条；未提交、未推送、未部署；未创建 `BotAgent.Panel`/`web`/ZBA。**本小节记录首轮验收后的整改前状态；整改后已于 19:25 独立复验 Accepted，当前状态见 §26.5。**

### 26.5 复验退回与整改（2026-10-18 17:58–19:25，整改后 Accepted）

#### 26.5.1 退回结论

- 证据目录：`.git/agent01-wave4-reacceptance-20261018-175027/`；Agent-01 结论 **NotAccepted**；67 条构建/测试命令中 7 条非零，均已保留。
- **W4-R1（High）**：`McpToolSource.DisposeAsync` 只处理已发布的 `state.Client`，没有取消/等待正在进行的 `EnsureClientAsync`；初始化中释放后，迟到的客户端仍被发布并继续 `tools/list`，且没有 DELETE 收口。
- **W4-R2（Medium）**：`EnsureClientAsync` 在缓存命中前无条件写 `Connecting`；成功调用后再次 Ensure 命中缓存直接返回，状态长期停在 `Connecting`。
- **W4-G1**：整包回退夹具硬编码候选闭包为旧 37 + Model/Engine = 39；当前真实运行闭包包含 Mcp 与 SDK 依赖，共 48 个非 PDB 资产，夹具在候选闭包检查处退出，未生成当前 proof。
- 旧 §26.4.3 的 Accepted 只代表 17:13 首轮测试范围；新增确定性负例证明其不覆盖释放/初始化竞态与状态恢复，不能继续代表当前准出。

#### 26.5.2 整改内容

- `src/BotAgent.Mcp/McpToolSource.cs`
  - `EnsureClientAsync` 用 `CreateLinkedTokenSource(ct, _lifetime.Token)` 覆盖门闩等待与 SDK 初始化；释放先取消 lifetime，再最多等 2 秒让在途初始化退出。
  - 创建返回后复核 `_disposed`：迟到客户端立即有界释放，绝不写入 `state.Client`、不发布工具路由。
  - 只有真正创建连接的分支才置 `Connecting`；缓存命中恢复 `Ready`。
  - `DiscoverAsync` 的 `tools/list` 与路由发布同样链接 lifetime；释放后返回空目录并清空路由。
  - 释放与在途调用并发时统一映射 `mcp_disposed` + `UnknownOutcome=true`；重复 `DisposeAsync` 幂等。
  - 停机不再释放 `_lifetime`，避免晚到操作读取 Token 时抛 `ObjectDisposedException`（该 CTS 无定时器）。
  - 补强（并行只读审查发现）：每个服务器用 `state.Sync` 锁保护客户端发布/清除；即使 2 秒门闩等待超时，释放后的清理仍与发布互斥。缓存命中与发布都在锁内复核 `_disposed`。
  - 调用者取消初始化时恢复到尝试前状态（通常 `Disconnected`），不再永久停在 `Connecting`；`ServerState.Current` 改为 volatile。
  - `ReplaceRoutes` 与停机清路由共用 `_routeGate`，释放返回后不会被迟到发现重新填满；`_lifetime.Cancel()` 的任意回调异常不再跳过清理。
- `tests/BotAgent.McpProbe/SyntheticMcpServer.cs`：增加 initialize / tools/list / tools/call 的确定性观察点与可选闸门。
- `tests/BotAgent.McpProbe/Program.cs`：新增 A14–A18（A 组 59→78，总计 118→137）：初始化中释放、`tools/list` 中释放、调用中释放、重复释放、缓存调用/刷新保持 `Ready`、调用者取消后不残留 `Connecting` 且下一次发现恢复 `Ready`。
- `tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs`：候选闭包改为从真实 `BotAgent.Headless.deps.json` 的 `runtime`/`runtimeTargets` 推导（加宿主三文件），逐项核对；保留旧 37 资产完整性、候选专属资产哈希校验与清理；不再写死 39/48。
- 写权登记：本轮由 Agent-00 直接执行最小整改，范围仅上述 4 个文件 + 总纲 / 05 / 状态 / CONTRIBUTING；未扩到 Engine/Core/宿主配置或其他公共测试。

#### 26.5.3 整改后自验与独立复验（当前工作树 / 合成范围）

| 项 | 结果 |
| --- | --- |
| 构建 | Headless `-t:Rebuild` exit 0（8 个既有 CS0108 / 0 错误）；Solution 0/0；Harness 0/0 |
| McpProbe | **137/0 ×3**（A 78 / B 33 / C 26） |
| Architecture / Safety / Engine | **169/0** / **594/0** / **18/0** |
| Review / SSRF / SettingsScope | **1140/0** / **77/0** / failures=0 |
| Production / Chaos / DeadlineGate | 62/0 / 43/0 / 53/0 |
| Concurrency / Participation / Frontend | 12/0 / 48/0 / 370/0 |
| PipelineEval / Feishu / Reverse | 68/68 / 24 PASS / failures=0 |
| Integration（17） | S14 3/0、S18 24/0、S20 20/0、S21 41/0、S22 35/0、S23 12/0、S24 23/0、S26 37/0、S33 59/0、S36 98/0、S38 24/0、S43 26/0、S45 17/0、S47 8/0、S50 14/0、S52 119/0、**S53 11/0**；全部 exit 0 |
| 整包回退 | **26/0**；`assets=37`、`candidateAssets=48`（按 deps 推导）；proof `rollback-proof-2134836bf701464e8968a04be89cbacd.json`，SHA-256 `38CF2E50399F242A6FEE770D50A882E8599DFAC756400ABF6A495E2FBF8D31BB` |
| Agent-01 原始失败夹具 | DisposeAudit **3/3 PASS**（含初始化中释放与调用者取消）；StateAudit **PASS**（初始/调用后/刷新后均 `Ready`） |
| `git diff --check` | exit 0；10 项既有 CRLF 提示 |
| 独立复验（Agent-01） | 首轮报告 **Accepted**（81/81 命令 exit 0；生命周期 67/0 ×4）；addendum **Accepted**（27/27 exit 0；原夹具 3/3 ×3、McpProbe 137/0 ×3、S53 11/0 ×3、Architecture 169/0、扩展生命周期 125/0 ×4、回退 **26/0**（addendum 1 次；Agent-00 最终树另 1 次），新 proof `0dd8fb04…`/`7FC510D1…`）；HEAD/index/status/481 文件无漂移 |
| 关键文件 SHA-256 | `McpToolSource.cs` `65E901ACC7E15F990DE02294748F79FF5D760591ADF993813B6BE9E85446FE5A`；`McpProbe/Program.cs` `26612C0E162B609B64E1E5D4E0B63A1A0F7DF6166354A1E7AE4B77B88C8BF1F9`；`SyntheticMcpServer.cs` `F5A5483E4AB167A375BAE391E0D8206E97A04530641A1505AF3D2F0FFAD24C13`；`ConfigurationContractTests.cs` `EDC99225DCA0B109F32498484C481A84363E260F767A67A14B71216DB43D553B` |

- 限制：以上仅为当前工作树/合成验证；未验证第三方真实 MCP 服务器、生产部署、真实模型/协议、生产压力/延迟/内存、S42、浏览器面板 E2E；2 秒是本地停机收口上限，不表示远端副作用已撤销；回退 proof 是合成运行闭包，不是生产恢复。
- 残余非阻断（独立只读审查）：`mcp_result_too_large` 仍返回 `UnknownOutcome=false`（远端已执行但结果被丢弃，模型可能重试有副作用的工具）；缓存命中与并发 `MarkFaulted` 仍可能瞬时覆盖状态；路由替换仍是 Clear+fill（可能瞬时 fail-closed）；停机上限按服务器计（每台最多 2s 门闩 + 2s 客户端释放）；极端大小写/哈希碰撞仍可能使路由被 `McpToolUseCase` 丢弃。以上未在本次矩阵触发，登记为 Wave5 前待办。

#### 26.5.4 独立结论与停止线

- Agent-01 最终 addendum（`.git/agent01-wave4-reacceptance-20261018-1900/acceptance-addendum.md`，2026-10-18 19:25）结论：**Accepted（仅当前未提交工作树与合成验证范围）**。
- W4-R1/R2/G1 闭合：2 秒门闩超时后无迟到 `client`/`tools/list`/路由；调用者取消恢复 `Disconnected`，下一次发现新建 initialize 并 `Ready`；回退 proof 为 37 旧资产 + 48 候选闭包（按 deps 推导）。
- 结论：**Wave4（Mcp）整改后验收完成，到达 Wave5 入口。** Wave5（ZBA/Panel/CLI）与 Wave6（Web）仍锁定；未提交、未推送、未部署；未创建 `BotAgent.Panel`/`web`/ZBA。
- 残余非阻断项（§26.5.3 末段）保留为 Wave5 前待办，不构成本轮阻断。

## 二十七、Wave5：ZBA、只读 CLI 与静态资源解耦（2026-10-18）

### 27.1 授权、范围与停止线

- 负责人本轮目标原文：**“根据文档多Agent协作推进至第六阶段入口”**。该指令明确解锁 **Wave5（ZBA/Panel/CLI）**，并要求完成 Wave5 后停在 **Wave6 入口**；Wave6（独立 Web）仍未启动。
- 本轮只做后端与契约，不创建 `src/web`，不实现 Web 页面，不部署、不提交、不推送。
- 首版 ZBA 范围按 [面板规划](../z-bot-panel-v0.1-beta-plan.md) 冻结：状态/健康、能力管理、配置读取与整批保存、模型管理（经 provider 能力目录）、Agent 任务/审批/取消、事件恢复语义、认证授权。
- 明确不纳入：MCP 管理页（Wave4 后端仍有效，但本版 ZBA 不暴露管理动作）、SSE 传输（只做可恢复事件查询）、Docker/本地高权限承载。
- 写权：Agent-00 直接执行并合入共享文件；ZbaProbe 由独立子代理按契约编写；独立验收交 Agent-01。没有把子代理的测试结果当成宿主验收。

### 27.2 ZBA v1 契约（冻结）

| 项 | 决定 |
| --- | --- |
| 工程 | 新建 `src/BotAgent.Panel`，只引用 `BotAgent.Core`；HTTP 适配层不访问数据库/配置仓储 |
| 承载 | Headless 同进程、同一 `HttpListener` 端口；`WebUiServer` 把 `/zba/*` 交给 Panel，旧 `/api/*` 保持原状 |
| 认证 | 仅 Bearer：`BOTAGENT_ZBA_TOKEN`（admin）/`BOTAGENT_ZBA_READ_TOKEN`（read）；兼容 `QQCHAT_ZBA_*`。无 Cookie，因此写路径无 CSRF 面 |
| 未配置 | 除 `/zba/v1/health` 外一律 503 `zba_not_configured`；明确打开 `BOTAGENT_ZBA_ALLOW_LOOPBACK=1` 才允许本机回环免令牌 |
| 授权 | 路由级 read/admin；应用层再检查调用者权限位；read token 不能执行/取消/审批/提交任务 |
| OpenAPI | `GET /zba/v1/openapi.json`，由 `ZbaRoutes.All` 同一张路由表生成；ZbaProbe 双向断言 path+method |
| 配置 | 复用 Core `ConfigurationSaveRequest/Result` 与结构化事务：`expectedVersion`、`cfg1:` 操作键、幂等/代际/过期、秘密槽、`ApplyState`、重启项 |
| 能力 | 复用 `IManagedCapabilities`：Driver Start/Stop/TestConnection、Provider TestConnection；插件全量登记但启停明确 `configuration_required`，必须走整批保存 |
| 任务 | 稳定 id + `Idempotency-Key`；有界队列；取消只报 `cancel_requested`；外部设备取消终态 `Unknown`；台账不存 prompt/结果正文 |
| 事件 | 512 条有界环 + `after` 序号；`resyncRequired` 时客户端重读状态/任务/审批；无 SSE |
| 静态资源 | `wwwroot/**` 不再嵌入 Headless，复制到输出/发布目录；缺失只影响旧页面，API/健康/ZBA 照常 |
| 旧面板安全 | 修复 Issue #78 的免密分支：无密码但配了 `PanelToken` 时必须校验令牌；绑定仍为回环 |

启动参数与 OpenAPI 的实际请求/响应见 [ZBA v1 交接包](06-zba-handoff.md)。

### 27.3 实现落位

- `src/BotAgent.Panel/`：`ZbaContracts.cs`、`ZbaRoutes.cs`、`ZbaEndpoint*.cs`、`ZbaSecurity.cs`、`ZbaJson.cs`、`ZbaQuery.cs`、`ZbaRules.cs`、`ZbaOpenApi.cs`、`ZbaHttpListenerAdapter.cs`。
- `src/BotAgent.Headless/Adapters/Panel/Zba/`：`ZbaHostBackend.cs`、`ZbaTaskRegistry.cs`、`ZbaEventBuffer.cs`、`ZbaCallerAuthorization.cs`、`ZbaRuntime.cs`。
- `src/BotAgent.Headless/Host/CompositionRoot.cs`：管理面绑定增加插件描述符；`BuildZba` 创建/挂载 ZBA；`AppGraph` 停机先收 ZBA 任务。
- `src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs`：`/zba/*` 独立分派；`IsAuthorized` 修复 Issue #78。
- `src/BotAgent.Headless/Adapters/Panel/PanelAssets.cs`：静态资源从发布目录读取；`PanelDeploy` 合并一次 `File.Exists`，把例外 IO 棘轮从 15 降到 14。
- `src/BotAgent.Headless/Host/Program.cs` + `ZbaCli.cs`：只读 CLI `--status/--capabilities/--tasks/--events`；令牌只从环境变量读，输出仅安全投影。
- `src/BotAgent.Headless/Services/Agent/AgentCommandService.cs`：`RunPanelAgentDirectAsync` 增加可选取消令牌，任务取消才真正传到底层；原有调用不变。
- `BotAgent.slnx`、`ArchitectureProbe`、`.github/workflows/ci.yml`、`CONTRIBUTING.md`：纳入 Panel/ZbaProbe/S54。

### 27.4 验证矩阵（当前工作树 / 合成范围）

| 项 | 结果 |
| --- | --- |
| 构建 | Solution 0 错误；Headless 既有 8 个 CS0108 / 0 错误 |
| ZbaProbe | **146/0 ×3**（路由/OpenAPI、认证授权与限流、闭包 JSON 含大小写别名/嵌套重复、秘密形状、配置/能力码映射、任务幂等/取消、审批、事件、真实 HttpListener 冒烟） |
| Architecture | **171/0**（Panel 只引用 Core；Headless 直接引用 Panel；Panel 文件 IO=0；最长方法 198；IO 58 / HttpClient 3） |
| Safety / Engine / Review | 594/0、18/0、1140/0 |
| SSRF / SettingsScope / ProductionSpec | 77/0、failures=0、62/0 |
| Chaos / DeadlineGate / Concurrency | 43/0、53/0、12/0 |
| Participation / Frontend / PipelineEval | 48/0、370/0、68/68 |
| Feishu / ReverseTransport | 24 PASS、failures=0 |
| S54 宿主真链路 | **54/0**：静态资源、Issue #78、Bearer/授权、状态/能力目录、配置读取/陈旧冲突/真实提交/二次保存、需重启字段回执与 `configuration.saved` 真实 state、超前游标 resync、capabilities/validate、插件 UnsupportedAction、任务/审批/事件、任务提交/完成/幂等/取消、host 取消/超时向桥发 cancel 并释放串行 current、CLI、OpenAPI、缺字段负例 |
| 全量 Integration | 54 场景 **1027/18**（S42 仍排除）；18 项失败在 **Wave4 公共基线 `ee56c92` 上逐项完全相同**（同一 harness、同一子集；候选与基线都 90/18，失败集合相同），不是 Wave5 回归；失败集中在 S1/S12/S16/S31/S32/S35/S37/S49 的既有日志文案/在线状态/历史补录断言。建立门禁的 **17 个定向场景 571/0**（S14/S18/S20/S21/S22/S23/S24/S26/S33/S36/S38/S43/S45/S47/S50/S52/S53，exit 0） |
| 独立复验（首轮） | Agent-01 2026-10-18 21:21 **NotAccepted**：H-1（ZBA host 取消未向桥发 cancel，远端继续运行）、M-1（host 超时报 Failed 且远端仍在运行）；L-1（handoff 把 find 写成 read 可用）、L-2（FileIoTotal 文档基线混记）。报告 `.git/agent01-wave5-acceptance-20261018-210023/acceptance-report.md` |
| 整改后增量复验（历史） | Agent-01 2026-10-18 21:45 **Accepted**（仅代表当时测试范围，已被 21:54 终验覆盖）：H-1 取消按 task id 发 cancel 并释放 current、M-1 host 超时 Unknown 闭环；独立 A-H **88/0**、S54 **49/0**、ZbaProbe 143/0 ×3、Architecture 171/0、Review 1140/0、17 定向 571/0；L-1/L-2 处理；无 High/Medium 阻断，仅 Low 顺序窗口残余 |
| 终验（第二轮） | Agent-01 2026-10-18 21:54 **NotAccepted**：W5-M2 同一 DTO 成员的大小写别名重复输入被接受（`kind`/`Kind` → 200）、W5-M3 需重启保存的事件 state 仍为 `applied`、W5-M4 重启后旧游标 `resyncRequired=false` 且新事件被静默跳过；既有 88/0、S54 49/0、ZbaProbe 143/0 ×3 仍通过，新增 EdgeAudit **8/4 ×3**。报告 `.git/agent01-wave5-final-acceptance-20261018-215401/acceptance-report.md` |
| W5-M2/M3/M4 整改 | `ZbaJson` 请求 DTO 精确大小写（大小写别名按未知字段 400，ordinal 重复检查不变）；`configuration.saved` state 投影真实 ApplyState（`restart_required` 等）；事件 `after > nextSequence` 要求 resync。ZbaProbe +3 → **146/0 ×3**；S54 +5 → **54/0 ×2**（重启字段回执/state、超前游标）；Agent-01 原 EdgeAudit **12/12 ×3**（fix1–fix3，含真实 restart）；Architecture **171/0**；独立复验 **Accepted**（下条） |
| 终验整改独立复验（历史） | Agent-01 2026-10-18 22:18–22:42 **Accepted**（当前工作树/合成范围；已被 22:49 第三轮验收覆盖）：W5-M2/M3/M4 均闭合，无新 High/Medium。ZbaProbe **146/0 ×3**（clean；首轮另 3 次）、Architecture **171/0**、S54 **54/0 ×2**（共 4 次）、EdgeAudit **12/12 ×6**（reaccept1–3 + clean1–3，含真实 stop/restart）、17 定向 **571/0** + S34 **20/0**、独立宿主 A/B/C/E/G/H **79/0**、分页/NoChanges/秘密认证回归 **13/13**；74 条命令 exit 0。M4 跨重启代际盲区仍存但已登记、非本轮阻断；报告 `.git/agent01-wave5-reacceptance-20261018-2230/acceptance-report.md` |
| 第三轮独立验收 | Agent-01 2026-10-18 22:49–23:01 **NotAccepted**（High 0 / Medium 1）：`secrets` 数组含 `null` 元素时 `ZbaRules.ValidateSecrets` 直接访问 `secret.Slot`，save 与 validate 两个端点均返回 **500 internal_error**，而不是契约要求的 400；三次独立合成宿主一致（6 通过/2 失败）。其余矩阵通过：ZbaProbe 146/0 ×3、McpProbe 137/0 ×3、Architecture 171/0、Safety 594/0、Review 1140/0、原 EdgeAudit 12/0 ×3、独立宿主 88/0、19 定向 645/0 + S54 54/0、回退/资源短测通过。报告 `.git/agent01-wave5-verification-20261018-224945/acceptance-report.md` |
| W5-M5 整改 | `ZbaRules.ValidateSecrets` 在成员访问前拒绝 null 元素，复用 `400 invalid_json`；无需新 DTO/接口。ZbaProbe 新增 6 项（save/validate 的 `[null]`、混合/集合 null、后端不调用）→ **152/0 ×3**；S54 新增 3 项（真实 HTTP 400、版本不变、validate 400）→ **57/0**；Agent-01 null 夹具 **7/0 ×3**（fix1–fix3）；19 定向 **648/0**、Architecture **171/0**；独立复验 **Accepted**（下条） |
| W5-M5 独立复验 | Agent-01 2026-10-19 11:02–11:39 **Accepted**（当前工作树/合成范围）：W5-M5 闭合，无新 High/Medium；`ZbaRules.cs` 相对失败版仅一行空值检查，删掉该行即回到旧失败哈希；无 DTO/OpenAPI 扩张。ZbaProbe **152/0 ×3**、Architecture **171/0**、S54 **57/0 ×3**、null 夹具 **7/0 ×3**（save/validate 400 `invalid_json`、版本不变、health 200）、19 定向 **648/0**（Windows 18 场景 624/0 + WSL S38 24/0；Windows 上 S38 因本机代理占用固定端口 13071 未能启动，保留失败）、真实宿主秘密生命周期 **24/0**。报告 `.git/agent01-wave5-m5-reacceptance-20261018-2320/acceptance-report.md` |
| H-1/M-1 整改 | `AgentBridgeServer.RunDirectAsync` 取消/超时按 task id 发送 `cancel`、清理 `_current`/`_outstanding`、触发 Finished；`ZbaTaskRegistry` 对 host 的 `CancelRequested` 统一报 Unknown + `cancellation_outcome_unknown`；S54 新增 9 项假桥断言（cancel 帧、current 释放、下一条可下发、超时 Unknown） |
| 资源测量 | 启动 656 ms；空闲 WS 57.0 MiB；峰值 WS 81.4 MiB；health p95 12.3 ms @16；status p95 27.6 ms @8；0 失败；暂定预算见 `.git/agent00-wave5-20261018-2015/resource-measurement.md` |
| 产物回退 | Wave4 公共快照 `ee56c92` 发布基线 → Wave5 候选 → 回退基线 → 破坏 Panel 后启动失败 → 无 wwwroot 运行（API/ZBA 200、静态 404）→ 恢复候选；`pass=true`；55/64 文件清单与 Panel SHA-256 见 `.git/agent00-wave5-20261018-2015/rollback-drill.json` |
| 契约探针缺陷 | 子代理首版发现 4 项并已修复：未解析 query 的路由静默接受未知参数、OpenAPI 把键写成对象而实际是 `cfg1:/mg1:` 字符串、required 字段未强制、OpenAPI 缺 405/410/422；均补了回归断言 |

### 27.5 关键文件 SHA-256（登记时）

| 文件 | SHA-256 |
| --- | --- |
| `src/BotAgent.Panel/ZbaEndpoint.cs` | `2B5E1FD22969AB6B7733A4F4950C13F3A375D6C92A6739B961A79E04739377AD` |
| `src/BotAgent.Panel/ZbaContracts.cs` | `5E2746666154BAAEC127BD1B4B2E34055EB35BB6757C7D6D60064A9F2CE69ED7` |
| `src/BotAgent.Panel/ZbaOpenApi.cs` | `FB2F08DC938DF64FDF4CCC931B4BFDACA3D2769FA187A3C55993F4CD0AD4ABA1` |
| `src/BotAgent.Panel/ZbaJson.cs` | `73123E6C4C47799BFD31CF551C5D920DCB47D7E904FA570D433A8DD8A4E1373E` |
| `src/BotAgent.Panel/ZbaRules.cs` | `C8BFA72738279536F035A30FFE5FC6036AA43FFDE3D8EB699F8914037625B81F` |
| `src/BotAgent.Headless/Adapters/Panel/Zba/ZbaHostBackend.cs` | `F3758FB72BB1504D74386BC20738A220E770C7723350FC5B9BC67C1C2B46B1AA` |
| `src/BotAgent.Headless/Adapters/Panel/Zba/ZbaEventBuffer.cs` | `02361D2B3B29325966101E54FB6C37D781B582D07842167F143B0E328ED849BF` |
| `src/BotAgent.Headless/Adapters/Persistence/ZbaEventSequenceStore.cs` | `422C127A108660F26847253FB9B8BAFC21FBA0DA1EB8964DEC93D4E560B18F85` |
| `src/BotAgent.Headless/Adapters/Panel/Zba/ZbaRuntime.cs` | `8659DDED17FDA0B11940F7B49AE56B7FA9FBB475F3B837B5B14BB37C10899173` |
| `src/BotAgent.Headless/Adapters/Panel/Zba/ZbaTaskRegistry.cs` | `FA147F36E80F18ED8D7C35CAC446A1C6BCBBDC6A9D4FF421E16D7ACE5F053881` |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | `1E5DAE19BB25CD3B724C332FAD4272A3A24973C7A658AC2043C96B38743CE65D` |
| `src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs` | `7FF7C0406BBAF57AB527AC8C0B2E332397788848C6071CBD044B82AC76ABFAF0` |
| `src/BotAgent.Headless/Services/Agent/AgentBridgeServer.cs` | `729BB5EDAC30D8243C9820C719444BF30E48A0C636ED479FE4ABDAAD52EA61E0` |
| `src/BotAgent.Headless/Host/ZbaCli.cs` | `F462198A05F9D9011B4F60A7B22C96B0112B0CCB7D88350C76D0AF090CFEE1DE` |
| `tests/BotAgent.ZbaProbe/Program.cs` | `D3BEABFAA2069CC96AC12EE6ADF836D7F9B209B002DD7926D6513E4A13D144EA` |
| `tests/BotAgent.IntegrationHarness/ZbaScenario.cs` | `4E854B5715C9E475D8163E3B7BDC7170B3E2E7321ED434D716FD37F87223EF56` |
| `tests/BotAgent.IntegrationHarness/ZbaRestartScenario.cs` | `F9DAC3E1017B0A7C5C99E5AA4EFEFC987C6F97C9C7B1812A8CE72CDD13AE68E6` |
| `tests/BotAgent.IntegrationHarness/Program.cs` | `C5853B71C10E7E09578473A3CC2371993DCFFC3A59B72EAFBD411F2762FCCE87` |
| `tests/BotAgent.ArchitectureProbe/Baseline.cs` | `E7621D4747A796C988B2FA0469F3EAB5460EB2710C3A3BDD176F826083173B1D` |
| `docs/plans/agents/06-zba-handoff.md` | `98598E8CEA7EA103A29EA581B27C19C4E444E3BBBD73D573E8B82B9E7EE90E64` |

> 22:42 复验时交接包哈希为 `502276F6…CF489B07`；W5-M5 复验时交接包为 `4339362D…57058204`；G1/G3 复验后仅补登记 Accepted 状态与最终候选数据；产品/测试源码未在验收期间被改动。上表为最终登记哈希。
> 本轮产品/测试整改对象为 `ZbaJson.cs`、`ZbaRules.cs`、`ZbaHostBackend.cs`、`ZbaEventBuffer.cs`、`ZbaEventSequenceStore.cs`、`ZbaRuntime.cs`、`ZbaProbe/Program.cs`、`ZbaScenario.cs`、新增 `ZbaRestartScenario.cs`、`Program.cs`、`Baseline.cs`；W5-M5 只改 `ZbaRules.cs` 与两份测试，W5-G1 只新增持久化序号与 S55。

### 27.6 已知限制与残余

- ZBA 任务台账在内存，重启即丢；重启后旧任务不可查询，客户端应视为 unknown，不能重做副作用。
- 无 SSE；事件只保留 512 条，超出后要求 resync。
- 无 MCP 管理页/Docker 高权限；插件启停必须走配置保存，不是即时内存动作。
- OpenAPI 是路由/高层形状契约，不是完整 JSON Schema 校验器；未知字段/重复字段由服务端闭包解析拒绝。
- 未验证真实第三方客户端、生产 Linux/容器资源、浏览器 E2E、真实模型/协议负载、生产部署或生产回退。
- 首轮独立验收 H-1/M-1 已整改：host 取消/超时现在按 task id 向桥发 cancel 并清理串行 current；ZBA 仍只报 `Unknown`，不宣称远端副作用已撤销。
- 终验 W5-M2/M3/M4 已整改：请求 DTO 精确大小写、配置事件投影真实 ApplyState、超前事件游标要求 resync。
- W5-G1 已整改：事件序号高水位持久化到数据目录（只存整数），宿主重启后从上次序号继续；旧游标不再静默漏事件。
  残余只在数据文件被删除/损坏或单数据目录多进程并发时退化为旧行为；该文件不是事件内容，不改变任务台账在内存的边界。
- 残余 Low（Agent-01 addendum）：取消/超时回调先完成等待者、后设置 `task.CancelRequested` 的顺序窗口；显式取消由 `record.Cts.IsCancellationRequested` 兜底，本轮 E/G/S54 未复现；后续可把标记移到 `TrySetResult` 之前。
- 客户端契约收紧（W5-M2）：请求 DTO 成员名按精确大小写；未登记的 PascalCase 客户端会收到 400。OpenAPI/交接包均为 camelCase，第三方客户端未验证。
- W5-G1 的序号文件读/写失败只记安全日志并继续内存事件；跨重启连续性此时降级，需由数据目录健康检查发现，不阻塞任务。
- 旧面板 `QQCHAT_HEALTH_BIND` 在免密模式下仍强制回环（安全约束，不是本轮回归）；配置密码后可按既有规则绑定。
- W5-G2 已补 1000 health/16 + 300 status/8 及 GC/Gen0/Gen1/Gen2 计数（见 §27.10）；候选预算已获负责人 2026-10-19 确认；未采容器/Linux/整机/Swap。

### 27.7 历史独立结论（2026-10-18 21:45，已被 21:54 终验覆盖）

- 历史结论：Wave5 整改后增量独立复验 2026-10-18 21:45 Accepted（当前未提交工作树/合成范围）。
- Agent-01 首轮 **NotAccepted**（H-1/M-1 阻断、L-1/L-2 记录）；整改后 addendum **Accepted**：H-1/M-1 闭环，L-1/L-2 处理，无 High/Medium 阻断。
- 该 Accepted 仅覆盖当时测试范围，不覆盖 21:54 新增的真实宿主负例。

### 27.8 多轮终验 NotAccepted 与整改（2026-10-18 21:54 起）

- Agent-01 终验 **NotAccepted**，新增三项 Medium 阻断：
  - **W5-M2**：请求 DTO 大小写不敏感绑定与 ordinal 重复检查不一致；`{"kind":0,"Kind":1}` 返回 200，违反闭包请求契约。
  - **W5-M3**：`configuration.saved` 对所有成功保存固定写 `state=applied`；需重启字段的真实 `applyState=3` 只放在 reasonCode。
  - **W5-M4**：宿主重启后事件序号从 0 重来；`after` 超前时 `resyncRequired=false`，旧游标会静默漏掉新事件。
- 整改（本次，未扩大 ZBA DTO/OpenAPI 字段）：
  - M2：`ZbaJson.Wire.PropertyNameCaseInsensitive=false`，契约按精确大小写；大小写变体按未知字段 400，字典/业务 key 大小写不变。
  - M3：`ZbaHostBackend.ConfigurationEventState` 投影真实 ApplyState（`applied`/`partially_applied`/`restart_required`/`failed`/`unknown`/`not_started`，无变化 `no_changes`）。
  - M4：`ZbaEventBuffer.Read` 对 `after > nextSequence` 返回 `resyncRequired=true`；空环同样判断。跨重启代际限制按 §27.6 登记。
- 自验证据：ZbaProbe **146/0 ×3**、S54 **54/0 ×2**、Architecture **171/0**、Solution 0 错误；Agent-01 原始 EdgeAudit 新标签 **12/12 ×3**（fix1–fix3，含真实 stop/restart）。
- 独立复验（22:42，历史范围）：2026-10-18 22:18–22:42 Agent-01 **Accepted**（当前工作树/合成范围）。W5-M2/M3/M4 均闭合，无新 High/Medium；ZbaProbe **146/0 ×3**（clean）、Architecture **171/0**、S54 **54/0 ×2**、EdgeAudit **12/12 ×6**（reaccept1–3 + clean1–3）、17 定向 **571/0** + S34 **20/0**、独立宿主 **79/0**、回归 **13/13**、74 条命令 exit 0。M4 跨重启代际盲区仍存，已登记为 Wave6 前契约决策项，不构成本轮阻断。
- **第三轮验收（22:49–23:01）NotAccepted，新增 W5-M5 Medium**：`secrets` 数组含 `null` 元素时，`ZbaRules.ValidateSecrets` 在成员访问前未判空，save 与 validate 两个端点均返回 **500 internal_error** 而非 400；三次独立合成宿主一致（6 通过/2 失败）。其他矩阵通过（ZbaProbe 146/0 ×3、McpProbe 137/0 ×3、Architecture 171/0、Safety 594/0、Review 1140/0、原 EdgeAudit 12/0 ×3、独立宿主 88/0、19 定向 645/0 + S54 54/0、回退/资源短测）。报告 `.git/agent01-wave5-verification-20261018-224945/acceptance-report.md`。
- **W5-M5 整改（本次）**：`ZbaRules.ValidateSecrets` 对 null 元素复用 `400 invalid_json`，不新增 DTO/接口；ZbaProbe 新增 6 项（save/validate 的 `[null]`、混合/集合 null、后端不调用）→ **152/0 ×3**；S54 新增 3 项（真实 HTTP 400、版本不变、validate 400）→ **57/0**；Agent-01 null 夹具 **7/0 ×3**（fix1–fix3）；19 定向 **648/0**、Architecture **171/0**。
- **W5-M5 独立复验（2026-10-19 11:02–11:39）Accepted**：W5-M5 闭合，无新 High/Medium；唯一产品改动为 `ZbaRules.ValidateSecrets` 一行空值检查，无 DTO/OpenAPI 扩张。ZbaProbe **152/0 ×3**、Architecture **171/0**、S54 **57/0 ×3**、null 夹具 **7/0 ×3**、19 定向 **648/0**（Windows 18 场景 624/0 + WSL S38 24/0；Windows 上 S38 因本机代理占用固定端口 13071，保留三次环境失败）、真实宿主秘密生命周期 **24/0**。
- 历史停止线（11:39 范围）：**Wave5 已独立复验 Accepted（当时工作树/合成范围），到达 Wave6 入口；Wave6（独立 Web）保持锁定，不得启动。** 未提交、未推送、未部署；未创建 `src/web`；未改生产数据；公开快照仍是 Wave4 `review/core-platforms@ee56c92`。该结论不覆盖下列完整准出核对；后续整改与最终状态以 §27.10 为准。

### 27.9 Agent-00 完整准出核对与补验清单（2026-10-19）

#### 授权、写权与结论

- 负责人先要求“对第五阶段验收”，随后授权“更新验收手册”。Agent-00 本批仅认领三份共享文档：本执行总纲、`06-agent-headless-cli-and-deploy.md`、`06-zba-handoff.md`；不认领产品、测试、工程、CI 或部署文件，不启动下游施工。
- **W5-M5 整改及已测合成功能证据有效；第五阶段完整准出暂不通过，状态为待验收。Wave6 保持锁定。** 不撤销历史局部通过事实，也不把“非本轮回归”“已登记限制”自动当成强制门禁豁免。

#### 本轮输入与只读证据

- 2026-10-19 11:45–11:46（+08:00）只读核对，原生命令退出码均为 0；未构建、未运行项目测试。HEAD 为 `1f3181e21e8d1f9531e41066b033d8d60218d580`；展开状态 581 条（101 修改、20 已暂存删除、140 未暂存删除、320 未跟踪），index SHA-256 为 `D60C435A419AB1D2567C2FC886466CA1FD2C02DB5B456DA44D139C615D2A87FD`，与 11:39 记录一致；既有改动不计入本任务成果。
- 对照 `.git/agent01-wave5-verification-20261018-224945/after.json` 的文件清单，并用 M5 `metadata-after.json` 覆盖已登记的整改哈希：506 个 src/tests 文件无新增、缺失或内容漂移。M5 目录下报告、前后元数据、命令清单、矩阵、null 与秘密生命周期等 11 份安全证据文件均匹配 `evidence-sha256.txt`；未读取日志正文或真实数据。
- 可承接的是历史结果：ZbaProbe 152/0 ×3、S54 57/0 ×3、Architecture 171/0、null 夹具 7/0 ×3、秘密生命周期 24/0；19 定向 648/0 来自 Windows 18 场景 624/0 加 WSL S38 24/0，非一次 Windows 全量通过。本轮没有产生新的行为测试成绩。
- 已核对真实入口：`CompositionRoot.BuildZba` → `ZbaRuntime`／`ZbaHostBackend` → `WebUiServer` 的 `/zba/*` 分派 → Panel 认证与路由；配置保存继续进入 `SettingsHotReload.SaveConfigurationAsync`，任务进入 `RunPanelAgentDirectAsync`／桥取消路径。接口仍为 `zba-1.0`，本批不变更 DTO、OpenAPI 或业务契约。

#### 尚未闭合的门禁

| 编号 | 实际位置／事实 | 完整准出条件 |
| --- | --- | --- |
| W5-G1 事件恢复 | `src/BotAgent.Headless/Adapters/Panel/Zba/ZbaEventBuffer.cs` 的 `_sequence` 随进程重置；`Read` 仅检查超前游标或环溢出。静态推演：旧游标 5，重启后新环已有 1–10，读取只返 6–10 且 `resyncRequired=false`，新事件 1–5 被静默跳过；本轮未做动态复现 | 先评审冻结代际游标或等价恢复方案及客户端兼容策略，再以真实宿主重启合成测试覆盖旧游标小于、等于、大于新序号及环溢出，确保恢复不足时明确 resync；不得仅把已登记限制改写为通过 |
| W5-G2 资源预算 | `06-agent-headless-cli-and-deploy.md` 第四节要求测量前确认方案与口径；`.git/agent00-wave5-20261018-2015/resource-measurement.md` 仅提供 Windows 合成短测和候选预算，未采托管堆／GC，无负责人确认记录 | 负责人确认代表性合成负载、采样与预算；按确认口径补验，记录未覆盖的容器／整机／同机服务／Swap 范围，不把本机短测写成生产达标 |
| W5-G3 当前产物回退绑定 | 原 `rollback-drill.json` 为历史 pass，所列候选 Panel SHA-256 `EB5E67A1893C2BC300E5C4846209F198B9DB3F234F7D55EC405C4AECD823E366`；当前 Headless 输出目录中 Panel DLL 为 `E82E979C243902D47D146F83C9B567CEF35AF427CC01481E459BA1A957923FAA`，不是同一产物 | 为最终候选生成完整产物清单及源码／依赖／配置和 DB 版本绑定，再补做兼容、升级、回退、缺失／损坏资源与恢复演练；哈希差异说明证据未覆盖当前产物，不代表已证明回退失败 |
| W5-G4 全量失败处置 | §27.4 保留全量 1027/18（S42 排除）；18 项与 Wave4 基线相同只能证明非 Wave5 新增回归。Windows S38 环境中止及 WSL 补充失败均保留 | 对每项失败、排除与环境限制登记归属、影响和处置；修复后重跑，不能以基线同失败、拼接定向通过或删除断言替代全量门禁。涉及验收范围变更须由负责人确认，Agent-00 不自行豁免 |

#### 实施顺序、验证与回退

1. 下一批先登记最终候选产物与资源／回退复验计划，确认预算；本次文档授权不自动启动构建或演练。
2. 事件恢复先做契约评审及精确路径认领；可能受影响的事件 DTO／OpenAPI／宿主与客户端须逐项冻结，未冻结前不写代码。
3. 获准验证后仅使用隔离合成目录、令牌、回环替身；集成测试前独立重建 Headless，逐命令记录开始／结束时间、退出码、文件与产物哈希。复验 ZbaProbe、Architecture、S54、事件重启负例、适用全矩阵及当前产物资源／回退；保留所有失败，不降低棘轮。
4. 本批仅文档修订；回退只逆向本次三份文档的确切补丁，先核对当前差异，保留既有内容及后续改动。未执行数据库恢复、生产操作、提交或推送。
5. G1–G4 证据闭合后由 00 重新核对完整准出；Web 开工仍须负责人单独明确批准，不由本节或历史 Accepted 自动解锁。

#### 本次文档交付核对

- 2026-10-19 11:48–11:49（+08:00）：三份文档的代码围栏、尾随空白、相对 Markdown 文件链接、唯一 §27.9 与 G1–G4 条目检查通过；`git diff --check` exit 0。再次核对 506 个源码／测试文件哈希均未变，HEAD、index 与 581 条工作区状态计数保持原样；这些计数不表示三份文档内容未变化。
- 独立只读文档读者 `/root/wave5_acceptance_doc_reader` 确认：历史 Accepted／当前待验收的区别、G1–G4 补验条件、Web 停止线与“哈希不同不等于回退失败”的说明无歧义。该检查不构成新的产品行为验收；本批未修改产品或测试、未运行构建／项目测试、未提交／推送／部署。

### 27.10 W5-G1–G4 整改与补验（2026-10-19）

#### G1 事件恢复：跨重启序号高水位
- 新增 `src/BotAgent.Headless/Adapters/Persistence/ZbaEventSequenceStore.cs`：数据目录只持久化一个非负整数
  `zba-event-sequence`；`ZbaEventBuffer` 启动读取、`Append` 回写，`ZbaRuntime` 接 `AppPaths.DataDir`。
  不新增 DTO/OpenAPI 字段；读/写失败只记安全日志并继续，不存事件内容。
- 新进程序号从上次高水位继续：旧游标要么收到后续事件，要么在环淘汰/超前时明确 `resync`，不再因序号重叠静默漏事件。
- 新公共场景 S55：真实宿主 stop/restart 覆盖旧游标 == 高水位、空环旧游标/零游标 resync、重启后收到 `last+1`、
  超前游标 resync、旧于高水位且旧事件不在环内 resync、损坏序号文件开新代际并记日志；
  反射构造真实 `ZbaEventBuffer` 追加 520 条覆盖 512 环溢出。
- 计数：S55 **17/0 ×3**；G1 私有真实宿主审计 **10/0 ×3**；ZbaProbe **152/0 ×3**；S54 **57/0**；Architecture **171/0**；
  19 定向（含 S38，Windows 端口可用）**665/0**。
- 首轮独立复验发现空环旧游标漏 resync（High）与损坏文件静默归零（Medium）；已修复：
  空环 `after != _sequence` 即 resync；损坏/读失败用注入时钟开新代际（≥1e12）并写安全日志。
- **独立复验（2026-10-19 13:02–13:15）Accepted**：上轮 gap 探针改写后 **11/0**、corrupt 探针改写后 **14/0**；
  S55 **17/0 ×3**、ZbaProbe **152/0 ×3**、S54 **57/0 ×2**、Architecture **171/0**、19 定向 **641/0**、S38 **24/0**。
  报告 `.git/agent01-wave5-g1-fix-reacceptance-20261019-1300/acceptance-report.md`。
- 旧的 21:54 EdgeAudit 中两条“重启后必须 resync”断言是为“只做 resync”的最小整改写的；高水位方案会继续投递新事件
  （`items=1`、`resync=false`），不静默漏，因此由 S55 的完整恢复矩阵取代；其余 10 条仍通过。

#### G3 当前候选产物绑定与回退
- G1 修复后的候选（历史绑定）：64 文件；Panel `E82E979C…923FAA`，Headless `B647DBE8…AED00F9F`；独立复验 Accepted。
- **G4 最终产物重新绑定（2026-10-19 14:15–14:35，Agent-01）**：候选 **64 文件**、Wave4 基线 **55 文件**；
  Headless DLL `6A5DDD70EC6AD1950CEE393AF424495A60343779B5D5E090210A4E4C712FD89D`，
  Panel DLL `E82E979C243902D47D146F83C9B567CEF35AF427CC01481E459BA1A957923FAA`，
  `deps.json` `4A6E070E493F20B0ECE135322795F8338177330C09B2C78C43C44908480DFEE3`。
- 流程：Wave4 基线 → 当前候选 → 回退基线 → 破坏 Panel → 移除 wwwroot → 恢复候选，`rollback-drill-g3.json pass=True`；
  绑定 HEAD `1f3181e`、status 303、关键源码/程序集/deps；合成 DB `botagent.db` user_version 9、WAL、61 schema 对象。
- 证据：`.git/agent01-wave5-full-exit-20261019/rollback-drill-g3.json`（最终）；
  `.git/agent00-wave5-g1-20261019/rollback-drill-g3.json`（G1 历史）。

#### G2 资源测量（负责人已确认候选预算）
- G4 前候选 Windows 合成（第二次稳定运行）：启动 **940.5 ms**（首次冷启动 3,702.8 ms）；
  空闲 RSS **57,491,456 B（约 54.8 MiB）**；峰值 RSS **91,451,392 B（约 87.2 MiB）**；
  health 1000/16 p95 **7.076 ms**，status 300/8 p95 **21.19 ms**，失败 0；GC Heap 峰值 14.26 MB、Gen2 0。
- **G4 最终产物重测（2026-10-19 14:32，Agent-01）**：启动 **943.6 ms**；空闲 RSS **59,731,968 B（约 56.96 MiB）**；
  峰值 RSS **92,344,320 B（约 88.07 MiB）**；health 1000/16 p95 **9.519 ms**，status 300/8 p95 **17.558 ms**，
  失败 0；GC Heap 峰值 **2.548 MB**、Gen0/Gen1/Gen2 0、LOH 96 KB、最大 %Time in GC 16%。
- 候选预算：启动 ≤5 s、空闲 RSS ≤120 MiB、峰值 RSS ≤160 MiB、GC Heap 峰值 ≤128 MiB、health p95 ≤100 ms、
  status p95 ≤250 ms、失败 0；不含容器/Linux/整机/Swap。
- **负责人确认（2026-10-19）**：接受上述候选预算作为 Wave5 资源准出预算；测量边界仍为 Windows 合成，
  不据此声称生产 Linux/容器资源已验收。
- 证据：`.git/agent01-wave5-full-exit-20261019/resource-measurement-g2.json`（最终）；
  `.git/agent00-wave5-g1-20261019/resource-measurement-g2.json`（G4 前）。

#### G4 全量失败处置（18 条全部修复）
- **S12 ×6 / S16 ×2 / S35 ×1（真实功能回归）**：`ChannelRouter` 迁移后，`ReplyPipeline` 的群历史能力判断、
  `BotScheduler` 的账号在线探测、`BotAgentHost` 的登录号确认仍用 `_source is OneBotGateway` 判定；路由对象不是
  网关，导致群历史补录、`get_status` 探测和登录号确认被静默跳过。现统一从 `IChannelRegistry` 取私域 OneBot；
  `ChannelRouter` 实现 `IPlatformHistorySource`，按目标号把 `GetGroupMsgHistoryAsync` 委派给支持该能力的通道。
- **S1 ×4 / S31 ×1 / S32 ×2（诊断日志不可读）**：重试/回退行为本身已正确，但日志是英文键或缺少可读行，
  面板与排障看不到“发生了什么”。改为中文可读日志：503/超时/空 choices 的重试、图片过期→重签取回、
  图片触发上游过滤后的去图回退、补评估使用的触发消息号。
- **S37 ×1（安全收紧后的旧断言）**：面板密钥掩码已统一为 `***`（不回显任何前缀）；测试改为断言该掩码，
  仍同时断言 `set=true`、来源 `panel`、响应不含明文。
- **S49 ×1（测试前置条件过期）**：`QQCHAT_WHITELIST` 只在首次部署时作为种子写入配置，重启改环境变量不会覆盖
  已持久化设置。测试改为在重启后经面板保存白名单，再验“非白名单历史会话禁止代发”。
- **S48 ×1（新探测引入的测试敏感）**：账号在线探测恢复后，后台 `get_status` 计入“协议端动作数”，
  使“名单外本地 id 一封都不回”误报。断言改为只数实际发送（`send_group_msg` / `send_private_msg`），
  模型请求数与 fail-closed 语义不变。
- 定向回归（8 个原失败场景，最终构建）：S1 **33/0**、S12 **9/0**、S16 **4/0**、S31 **10/0**、S32 **6/0**、
  S35 **20/0**、S37 **13/0**、S49 **14/0**，合计 **109/0**；S48 **10/0**。
- 全量：**1086/0**（S1–S55，含 S38，Windows 端口可用，最终构建）；架构 **171/0**；ZbaProbe **152/0**；
  Safety **594/0**；Review **1140/0**；Frontend **370/0**。G4 整改后工作树 **303** 条（105 修改 / 20 暂存删除 /
  140 工作区删除 / 38 未跟踪），HEAD 仍 `1f3181e`，未创建 `src/web`。
- **独立复验（2026-10-19 14:15–14:35，Agent-01）Accepted**：9 场景定向 **119/0**（8 个原失败场景 109/0 +
  S48 10/0）、全量 **1086/0**、Architecture 171/0、ZbaProbe 152/0、Frontend 370/0、Safety 594/0、Review 1140/0；
  未发现测试删除/弱化或新的未授权发送路径；G3/G2 用最终产物重新绑定/重测。报告
  `.git/agent01-wave5-full-exit-20261019/acceptance-report.md`。
- 关键整改文件哈希（SHA-256）：
  `BotScheduler.cs` `B58652ADF7FE268F0CED6B5BB629054E0395376C35898CE5E4A0B8E581F63CBE`、
  `BotAgentHost.cs` `F1FEDBA0726355A03D0AA279F0C069A88A5AB573FB7C5B3F0B9BFDB859FEF4B6`、
  `ChannelRouter.cs` `90BEDF9BF6F904DABFC3DFCB13A7A49BA0533C6AC7C253C6C20125B62FDCC8CC`、
  `ImageDownloader.cs` `483AD7A8744C2F246D9A302D545B36AF0F29167ABD86C732B0AC89B441D851DA`、
  `ModelTransport.cs` `08B45DB0AB0269B47FAFE6061E505F749EC821F8855CAEF24923D060E28E66B2`、
  `ReplyPipeline.SourceScheduling.cs` `E8E530D675D968403C8BF09DD2D2D4652D8B8E65F7FB47323D3B326A28044265`；
  测试 `ConversationManagementScenario.cs` `3FF6370ABE0DF2BAD15DAD59E2ABA9C20B4421BBAA7831D4F6109D3C5E0B6B25`、
  `AgentServerKeyScenario.cs` `235EA67051573BDF59B177F10BAFB786AC47828F5E2D6C9860B23B221CADA0D1`、
  `LocalChannelScenario.cs` `3A9AD4B4737222CC17A9B968CCC3D77159E47633C295A5670E0894CE7E64EAB9`。
- 证据：`.git/agent00-wave5-g4-20261019/full-it-g4-final.log`（全量）、`full-it-g4.log`（首轮 1085/1，
  仅 S48 测试敏感，已修）、`arch-g4.log`；首轮登记 `.git/agent00-wave5-g1-20261019/full-it.log`、
  `full-it-after38.log`、`g4-failures-final.log`。

#### 当前停止线
- **Wave5 完整准出已由 Agent-01 独立复验 Accepted**（当前未提交工作树 / Windows 合成范围，无 High/Medium）：
  W5-M5/G1/G2/G3/G4 全部闭合；报告 `.git/agent01-wave5-full-exit-20261019/acceptance-report.md`。
- **Wave5 到达 Wave6 入口；Wave6（Web）仍锁定，需负责人明确解锁并确认范围/资源。** 未提交、未推送、未部署；
  未创建 `src/web`；公开快照仍是 Wave4 `review/core-platforms@ee56c92`。
