# C3/C4字段及秘密契约评审输入

> 版本：C3/C4-field-review-0.1-review-1，2026-10-10；第十七节修正旧官方空名单、真实PluginManager及相关消费事实，字段集合保持。**静态事实和候选规则，不是契约冻结、代码授权、Wave 1验收或新ZBA OpenAPI。**
> 授权、写权、工作区、波次及最新证据唯一来源：[执行总纲](00-orchestration-and-dependency-graph.md)第十六/十七节。本文件承载完整字段参考，不维护第二份进度。
> 输入只有当前源码；未读取配置实例、环境变量值、数据库内容、真实消息/人物档案/日志。源码表达式中的变量是字段形状，不是秘密值。

## 1. 核对范围和完成边界

- 当前提交main/1f3181e21e8d1f9531e41066b033d8d60218d580，工作区不是干净提交。保留所有既有改动，不归为本批成果。
- 逐行审读旧保存处理90–759和响应761–1041：153个不同的body字段引用中，142个非秘密配置输入、9个秘密/清除输入、1个即时动作mood、1个仅审计ttsKey。另有officialAppSecret/clearOfficialAppSecret的case-insensitive存在性拒绝，共155项输入事实；**153不是153个合法可写配置项**。
- 两个配置类的170个public get/set属性逐项归属：142项非秘密输入映射及秘密输入共同关联151个属性，余19项在第3.2节单列；DeviceConfig六个构造参数不算AppSettings属性。多输入关联同属性/单输入关联两属性均非一对一，不能以计数替代逐项对应。
- 48个仅响应字段在第3.1节单列；响应env容器中仍有modelBaseUrl/model等可保存字段，**不是所有env子字段都环境只读**。
- 2026-10-10第十七节复核修正：官方旧空白名单不是fail closed，而是全允许；列表显式清空与继承须按平台分别建模。PluginManager是真实类型，HostPluginManager旧简称不是接口名；本轮字段集合及输入源SHA不变，补充相关真实调用见4.1/4.5/5。
- 本文件证明完整旧入口/属性形状覆盖，不证明任一新保存行为已实现。类型与归一化列记录旧处理事实；“新”“待确认”“消费者”列均是评审需求，没有伪造角色签字。所有消费代码仍等待精确契约冻结。

## 2. 输入矩阵

### 2.1 非秘密配置输入（142项）

共同旧规则：body[key]为JsonNode时才处理；缺省/JSON null一般保留；GetValue类型不匹配可能抛错，TryGetValue分支可能忽略。旧并非严格schema验证，agentPrompt/日报等ToString例外见各行。数字Clamp是旧语义，不等于所有新接口都接受任意数值；新double还须明确有限值/NaN/Infinity拒绝。

共同候选新规则（待01/消费者冻结）：稀疏强类型Patch；Missing=Keep，Set(value)严格类型，Clear只允许明确可清空字段；bool的false是Set(false)，非Keep。numeric null/Clear拒绝；string Clear是否空、继承或删除按字段定义，不能用全局空规则。数组Missing保留，Set([])显式整表替换；不提供任意object/反射透传。未知分区、属性、嵌套属性拒绝；重复JSON属性/大小写变体及旧HTTP兼容差异需06明确。

候选配置投影仍由Storage一次事务持久化，Host提交后应用；表中“消费者”是需要真实评审的角色，**不是已确认或代码认领**。非秘密不等于可公开：白名单、工作目录、提示词、URL、身份字段显示需隐私/授权投影，存储与key保持原样，原名编辑遵守nameRaw；不把掩码写回。

| 旧请求key | 类型 / 关联属性 | 旧归一化或特殊规则 | 消费者；生效/安全边界 | Settings.cs行 |
| --- | --- | --- | --- | --- |
| `adaptiveSamplingEnabled` | bool / `AdaptiveSamplingEnabled` | ase.GetValue<bool>() | 01/03/06；提交后运行时投影，精确生效规则待消费者确认 | [339](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L339) |
| `agentAllowedUsers` | string / `AgentAllowedUsers` | aau.GetValue<string>() ?? string.Empty | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [474](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L474) |
| `agentDevices` | string → DeviceConfig[] / `AgentDevices` | 旧：原始字符串无结构校验；新：六字段闭集，见4.2 | 01/04/06；结构快照/设备路由；禁秘密透传 | [515](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L515) |
| `agentMaxQueued` | int / `AgentMaxQueued` | Math.Clamp(amq.GetValue<int>(), 1, 20) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [481](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L481) |
| `agentModel` | string / `AgentModel` | (am2.GetValue<string>() ?? string.Empty).Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [476,571](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L476) |
| `agentPrefix` | string / `AgentPrefix` | (apx.GetValue<string>() ?? "//").Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [473](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L473) |
| `agentProgressSeconds` | int / `AgentProgressSeconds` | Math.Clamp(aps.GetValue<int>(), 0, 3600) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [480](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L480) |
| `agentPrompt` | string / `AgentPrompt` | 旧：ToString/Trim/截8000；新：严格string，空显式取消附加提示 | 01/04/06；运行时提示词；审批/权限不扩大 | [562](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L562) |
| `agentReasoningEffort` | string / `AgentReasoningEffort` | Trim/小写；空或default→auto，其他值直接存 | 01/03/04/06；推理档位；合法枚举待03确认 | [620](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L620) |
| `agentReasoningLevels` | string / `AgentReasoningLevels` | 原始string，最多1000字符；新：模型映射闭集待评审 | 01/03/04/06；后台/桥接模型档位 | [625](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L625) |
| `agentReplyMaxChars` | int / `AgentReplyMaxChars` | Math.Clamp(arc.GetValue<int>(), 200, 3000) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [479](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L479) |
| `agentServerBaseUrl` | string / `AgentServerBaseUrl` | Trim；空继承聊天端点；非http(s)绝对URL旧为日志+忽略 | 01/03/04/06；运行时客户端是否重建待确认 | [572](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L572) |
| `agentServerCommandTimeoutSeconds` | int / `AgentServerCommandTimeoutSeconds` | Math.Clamp(asct.GetValue<int>(), 5, 300) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [615](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L615) |
| `agentServerDocker` | bool / `AgentServerDocker` | asdk.GetValue<bool>() | 01/04/06；高权限配置≠动作授权，首版边界见5 | [611](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L611) |
| `agentServerKeepContext` | bool / `AgentServerKeepContext` | askc.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [610](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L610) |
| `agentServerMaxSteps` | int / `AgentServerMaxSteps` | Math.Clamp(ass.GetValue<int>(), 1, 30) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [614](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L614) |
| `agentServerModel` | string / `AgentServerModel` | (asm.GetValue<string>() ?? string.Empty).Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [513](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L513) |
| `agentServerQqActions` | string / `AgentServerQqActions` | Trim；空/all/*及中文全量别名保留；其它按QqActionCatalog规范化/去重，未知忽略 | 01/04/06；工具授权；新端口拒未知而非静默扩大 | [491](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L491) |
| `agentServerTools` | string / `AgentServerTools` | (ast.GetValue<string>() ?? string.Empty).Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [487](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L487) |
| `agentServerUseGate` | bool / `AgentServerUseGate` | useGate.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [351](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L351) |
| `agentServerWorkDir` | string / `AgentServerWorkDir` | (asw.GetValue<string>() ?? "/data").Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [609](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L609) |
| `agentTarget` | string / `AgentTarget` | (atg.GetValue<string>() ?? "auto").Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [484](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L484) |
| `agentTimeoutSeconds` | int / `AgentTimeoutSeconds` | Math.Clamp(ats.GetValue<int>(), 30, 7200) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [478](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L478) |
| `agentTools` | string / `AgentTools` | (atl.GetValue<string>() ?? string.Empty).Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [477](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L477) |
| `agentWorkDir` | string / `AgentWorkDir` | (awd.GetValue<string>() ?? string.Empty).Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [475](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L475) |
| `aiDesire` | int / `AiDesire` | Math.Clamp(desire.GetValue<int>(), 0, 100) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [337](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L337) |
| `aiModeEnabled` | bool / `AiModeEnabled` | ai.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [352](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L352) |
| `approvalApprovers` | string / `ApprovalApprovers` | ToString/Trim/截300；不是身份验证替代品 | 01/04/06；审批身份列表；显示脱敏，存储原样 | [523](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L523) |
| `botPersona` | string / `BotPersona` | persona.GetValue<string>() ?? string.Empty | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [333](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L333) |
| `defaultTemperature` | double / `DefaultTemperature` | Math.Clamp(dt.GetValue<double>(), 0.0, 2.0) | 01/03/06；提交后运行时投影，精确生效规则待消费者确认 | [344](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L344) |
| `defaultTopP` | double / `DefaultTopP` | Math.Clamp(dtp.GetValue<double>(), 0.0, 1.0) | 01/03/06；提交后运行时投影，精确生效规则待消费者确认 | [345](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L345) |
| `emotionalTemperature` | double / `EmotionalTemperature` | Math.Clamp(et.GetValue<double>(), 0.0, 2.0) | 01/03/06；提交后运行时投影，精确生效规则待消费者确认 | [342](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L342) |
| `emotionalTopP` | double / `EmotionalTopP` | Math.Clamp(etp.GetValue<double>(), 0.0, 1.0) | 01/03/06；提交后运行时投影，精确生效规则待消费者确认 | [343](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L343) |
| `enableAgentBridge` | bool / `EnableAgentBridge` | eab.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [472](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L472) |
| `enableAgentMask` | bool / `AgentMaskSensitive` | eam.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [561](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L561) |
| `enableApprovals` | bool / `EnableApprovals` | apv.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [522](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L522) |
| `enableAtmosphereDamping` | bool / `EnableAtmosphereDamping` | ead.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [363](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L363) |
| `enableHostAgent` | bool / `EnableHostAgent` | eha.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [486](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L486) |
| `enableLinkPreview` | bool / `EnableLinkPreview` | elp.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [455](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L455) |
| `enableMusic` | bool / `EnableMusic` | em.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [458](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L458) |
| `enableParticipationGating` | bool / `EnableParticipationGating` | epg.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [558](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L558) |
| `enablePoke` | bool / `EnablePoke` | ep.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [664](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L664) |
| `enableProactive` | bool / `EnableProactive` | pv.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [358](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L358) |
| `enableProfileSummary` | bool / `EnableProfileSummary` | eps.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [371](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L371) |
| `enableQuestions` | bool / `EnableQuestions` | eq.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [559](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L559) |
| `enableServerAgent` | bool / `EnableServerAgent` | esa.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [485](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L485) |
| `enableStickers` | bool / `EnableStickers` | es.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [659](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L659) |
| `enableVoice` | bool / `EnableVoice` | ev.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [377](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L377) |
| `enableWebSearch` | bool / `EnableWebSearch` | ws.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [449](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L449) |
| `fastModel` | string / `FastModel` | TryGetValue<string>/Trim；空存空，快速档回主模型 | 01/03/06；新保存/启动恢复一致性待验 | [696](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L696) |
| `fastReply` | bool / `FastReply` | GetValue<bool>；启动环境投影另见4.5 | 01/03/06；聊天模型选择；后台活儿不变 | [691](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L691) |
| `fastThinkingBudget` | string / `FastThinkingBudget` | 仅原始Length>0才写，随后Trim/小写；纯空白可变空 | 01/03/06；快速档推理参数 | [711](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L711) |
| `fastThinkingCustomBudget` | string / `FastThinkingCustomBudget` | 仅原始Length>0才写，随后Trim | 01/03/06；快速档自定义推理参数 | [715](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L715) |
| `feishuApiBase` | string / `FeishuApiBase` | fab.GetValue<string>().Trim() | 01/02/06；适配器捕获/重启规则须02确认 | [293](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L293) |
| `feishuAppId` | string / `FeishuAppId` | fai.GetValue<string>().Trim() | 01/02/06；适配器捕获/重启规则须02确认 | [290](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L290) |
| `feishuEnabled` | bool / `FeishuEnabled` | GetValue<bool> + ApplyLegacy(Enabled/ChatEnabled同值) | 01/02/06；飞书实例生命周期需重启确认 | [242,289](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L242) |
| `feishuWhitelist` | string / `FeishuWhitelist` | fwl.GetValue<string>().Trim() | 01/02/06；重建名单/清待回复台账，保留会话历史 | [292](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L292) |
| `filterActionNarration` | bool / `FilterActionNarration` | fan.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [362](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L362) |
| `groupCooldownSeconds` | int / `GroupCooldownSeconds` | Math.Max(0, gc.GetValue<int>()) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [354](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L354) |
| `healthReportEnabled` | bool / `HealthReportEnabled` | hre.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [636](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L636) |
| `healthReportTargets` | string / `HealthReportTargets` | ToString/Trim（旧可接受非string） | 01/04/06；发信目标权限；显示脱敏/原样保存 | [657](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L657) |
| `healthReportTime` | string / `HealthReportTime` | ToString/Trim；包含冒号或长度4即可接受ParseHealthReportClock结果，非法可回18:00 | 01/04/06；重排日报；新严格HH:mm校验待兼容确认 | [637](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L637) |
| `idleFallbackSeconds` | int / `IdleFallbackSeconds` | Math.Max(0, fb.GetValue<int>()) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [356](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L356) |
| `ignoreBracketMessages` | bool / `IgnoreBracketMessages` | ibm.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [361](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L361) |
| `linkPreviewMax` | int / `LinkPreviewMax` | Math.Clamp(lpm.GetValue<int>(), 0, 5) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [457](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L457) |
| `linkPreviewTimeoutSeconds` | int / `LinkPreviewTimeoutSeconds` | Math.Clamp(lpt.GetValue<int>(), 2, 30) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [456](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L456) |
| `localChannelIds` | string / `LocalChannelIds` | 原始string；非空同时设置Local开关；策略覆盖顺序见4.1 | 01/02/06；构造通道要重启，热更新名单≠新建通道 | [243,349](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L243) |
| `maxAgentSteps` | int / `MaxAgentSteps` | Math.Clamp(steps.GetValue<int>(), 1, 3) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [347](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L347) |
| `maxConcurrentReplies` | int / `MaxConcurrentReplies` | Math.Clamp(mcr.GetValue<int>(), 1, 16) | 01/04/06；ResizeGate；在途任务规则 | [370](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L370) |
| `maxContextMessages` | int / `MaxContextMessages` | Math.Clamp(mc.GetValue<int>(), 10, 1000) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [365](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L365) |
| `maxMessagesPerConversation` | int / `MaxMessagesPerConversation` | Math.Clamp(mm.GetValue<int>(), 20, 100000) | 01/04/06；立即裁剪窗口；重放不得再裁剪 | [369](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L369) |
| `maxProfileChars` | int / `MaxProfileChars` | Math.Clamp(mpc.GetValue<int>(), 0, 20000) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [368](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L368) |
| `maxTokens` | int / `MaxTokens` | Math.Clamp(mt.GetValue<int>(), 64, 32000) | 01/03/06；提交后运行时投影，精确生效规则待消费者确认 | [353](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L353) |
| `messageWhitelist` | string / `MessageWhitelist` | wl.GetValue<string>() ?? string.Empty | 01/02/06；重建名单/清待回复台账，保留会话历史 | [334](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L334) |
| `model` | string / `Model + ModelOverride` | Trim；空删除Override并回环境/内置默认 | 01/03/06；模型标识，不是启停/测试动作 | [679](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L679) |
| `modelBaseUrl` | string / `ModelBaseUrl + ModelBaseUrlOverride` | Trim；非空仅http(s)绝对URI，非法保存前400；空回环境/默认 | 01/03/06；端点配置；SSRF/完整端点显示边界待评审 | [117](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L117) |
| `moodTtlSeconds` | int / `MoodTtlSeconds` | Math.Clamp(mtt.GetValue<int>(), 0, 86400 * 7) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [666](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L666) |
| `musicBitrate` | int / `MusicBitrate` | Math.Clamp(mb.GetValue<int>(), 32, 320) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [461](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L461) |
| `musicKeepAudio` | bool / `MusicKeepAudio` | mka.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [469](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L469) |
| `musicLibraryMax` | int / `MusicLibraryMax` | Math.Clamp(mml.GetValue<int>(), 10, 5000) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [464](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L464) |
| `musicListenCooldownSeconds` | int / `MusicListenCooldownSeconds` | Math.Clamp(mlc.GetValue<int>(), 0, 86400) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [466](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L466) |
| `musicMaxAnalysisSeconds` | int / `MusicMaxAnalysisSeconds` | Math.Clamp(mma.GetValue<int>(), 20, 600) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [463](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L463) |
| `musicMaxDownloadMb` | int / `MusicMaxDownloadMb` | Math.Clamp(mmd.GetValue<int>(), 1, 64) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [462](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L462) |
| `musicNoteTtlDays` | int / `MusicNoteTtlDays` | Math.Clamp(mnt.GetValue<int>(), 1, 365) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [465](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L465) |
| `musicSendAudioToModel` | bool / `MusicSendAudioToModel` | msa.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [468](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L468) |
| `musicSources` | string / `MusicSources` | ms.GetValue<string>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [460](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L460) |
| `musicUnderstandModel` | string / `MusicUnderstandModel` | mum.GetValue<string>().Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [467](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L467) |
| `neteaseBaseUrl` | string / `NeteaseBaseUrl` | nbu.GetValue<string>().Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [459](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L459) |
| `officialAppId` | string / `OfficialAppId` | oai.GetValue<string>().Trim() | 01/02/06；适配器捕获/重启规则须02确认 | [441](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L441) |
| `officialChatEnabled` | bool / `OfficialChatEnabled` | GetValue<bool> + ApplyLegacy + SynchronizeLegacy | 01/02/06；官方聊天闸门，不授私域权限 | [238,444](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L238) |
| `officialEnabled` | bool / `OfficialEnabled` | GetValue<bool> + ApplyLegacy + SynchronizeLegacy | 01/02/06；配置Enabled≠实际连接 | [238,440](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L238) |
| `officialSandbox` | bool / `OfficialSandbox` | osb.GetValue<bool>() | 01/02/06；适配器捕获/重启规则须02确认 | [442](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L442) |
| `officialWhitelistGroups` | string / `OfficialWhitelistGroups` | owg.GetValue<string>().Trim() | 01/02/06；重建名单/清待回复台账，保留会话历史 | [447](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L447) |
| `officialWhitelistPrivates` | string / `OfficialWhitelistPrivates` | owp.GetValue<string>().Trim() | 01/02/06；重建名单/清待回复台账，保留会话历史 | [448](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L448) |
| `panelDeployEnabled` | bool / `PanelDeployEnabled` | pde.GetValue<bool>() | 01/04/06；高权限配置≠动作授权，首版边界见5 | [612](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L612) |
| `panelDeployUrl` | string / `PanelDeployUrl` | (pdu.GetValue<string>() ?? string.Empty).Trim() | 01/04/06；高权限配置≠动作授权，首版边界见5 | [613](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L613) |
| `participationCooldownSeconds` | int / `ParticipationCooldownSeconds` | Math.Clamp(int,0,600) | 01/04/06；状态保留/换上限 | [537](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L537) |
| `participationMaxActiveLifetimeSeconds` | int / `ParticipationMaxActiveLifetimeSeconds` | Math.Clamp(int,30,3600) | 01/04/06；状态保留/换上限 | [547](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L547) |
| `participationMaxConsecutiveReplies` | int / `ParticipationMaxConsecutiveReplies` | Math.Clamp(int,1,10) | 01/04/06；状态保留/换上限 | [532](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L532) |
| `participationMaxExitingLifetimeSeconds` | int / `ParticipationMaxExitingLifetimeSeconds` | Math.Clamp(int,10,3600) | 01/04/06；状态保留/换上限 | [552](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L552) |
| `participationProbingMaxReplies` | int / `ParticipationProbingMaxReplies` | Math.Clamp(int,1,3) | 01/04/06；状态保留/换上限 | [542](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L542) |
| `platformPolicies` | array&lt;PlatformPolicySettings&gt; / `PlatformPolicies` | 32项/规范platformId/trim account/复合唯一/名单4096/覆盖32/动作64；见4.1 | 01/02/04/06；替换整表与旧字段同步；当前无InstanceId | [109,256](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L109) |
| `pokeCooldownSeconds` | int / `PokeCooldownSeconds` | Math.Clamp(pcl.GetValue<int>(), 0, 86400) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [665](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L665) |
| `privateChatEnabled` | bool / `PrivateChatEnabled` | GetValue<bool> + ApplyLegacy + SynchronizeLegacy | 01/02/06；私域聊天闸门 | [240,443](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L240) |
| `privateCooldownSeconds` | int / `PrivateCooldownSeconds` | Math.Max(0, pc.GetValue<int>()) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [355](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L355) |
| `proactiveCooldownSeconds` | int / `ProactiveCooldownSeconds` | Math.Clamp(pcd.GetValue<int>(), 60, 86400) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [359](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L359) |
| `proactiveQuietSeconds` | int / `ProactiveQuietSeconds` | Math.Clamp(pq.GetValue<int>(), 1, 3600) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [360](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L360) |
| `profileLookupCount` | int / `ProfileLookupCount` | Math.Clamp(pl.GetValue<int>(), 0, 50) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [366](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L366) |
| `profileSummaryIntervalSeconds` | int / `ProfileSummaryIntervalSeconds` | Math.Clamp(psi.GetValue<int>(), 0, 86400) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [374](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L374) |
| `profileSummaryLines` | int / `ProfileSummaryLines` | Math.Clamp(psl.GetValue<int>(), 0, 50) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [367](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L367) |
| `profileSummaryMaxChars` | int / `ProfileSummaryMaxChars` | Math.Clamp(psm.GetValue<int>(), 40, 2000) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [373](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L373) |
| `profileSummaryThreshold` | int / `ProfileSummaryThreshold` | Math.Clamp(pst.GetValue<int>(), 5, 500) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [372](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L372) |
| `rationalTemperature` | double / `RationalTemperature` | Math.Clamp(rt.GetValue<double>(), 0.0, 2.0) | 01/03/06；提交后运行时投影，精确生效规则待消费者确认 | [340](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L340) |
| `rationalTopP` | double / `RationalTopP` | Math.Clamp(rtp.GetValue<double>(), 0.0, 1.0) | 01/03/06；提交后运行时投影，精确生效规则待消费者确认 | [341](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L341) |
| `scenarioPreset` | string / `ScenarioPreset` | Trim；保存层未检查名单，运行能力构造再处理 | 01/04/06；场景权限，不能把存值等于合法授权 | [516](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L516) |
| `segmentDelayMs` | int / `SegmentDelayMs` | Math.Max(0, sd.GetValue<int>()) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [364](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L364) |
| `splitReplies` | bool / `SplitReplies` | sp.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [357](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L357) |
| `stickerCandidates` | int / `StickerCandidates` | Math.Clamp(sc.GetValue<int>(), 0, 20) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [661](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L661) |
| `stickerCooldownSeconds` | int / `StickerCooldownSeconds` | Math.Clamp(scl.GetValue<int>(), 0, 86400) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [663](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L663) |
| `stickerCurateIntervalSeconds` | int / `StickerCurateIntervalSeconds` | Math.Clamp(sci.GetValue<int>(), 0, 86400) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [662](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L662) |
| `stickerLibraryMax` | int / `StickerLibraryMax` | Math.Clamp(slm.GetValue<int>(), 0, 2000) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [660](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L660) |
| `suitabilityThreshold` | int / `SuitabilityThreshold` | Math.Clamp(th.GetValue<int>(), 0, 100) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [338](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L338) |
| `thinkingBudget` | string / `ThinkingBudget` | 仅原始Length>0才写，随后Trim/小写；纯空白可变空 | 01/03/06；主模型推理参数 | [703](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L703) |
| `thinkingCustomBudget` | string / `ThinkingCustomBudget` | 仅原始Length>0才写，随后Trim | 01/03/06；主模型自定义推理参数 | [707](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L707) |
| `ttsApiBase` | string / `TtsApiBase` | Trim；旧未校验URI；追加外部文件写 | 01/04/06；SQLite提交后TTS外部投影 | [396](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L396) |
| `ttsModel` | string / `TtsModel` | Trim；追加外部文件写 | 01/04/06；SQLite提交后TTS外部投影 | [402](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L402) |
| `ttsProvider` | string / `TtsProvider` | Trim；旧未限制服务商枚举；追加外部文件写 | 01/04/06；SQLite提交后TTS外部投影，见4.4 | [390](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L390) |
| `ttsServiceUrl` | string / `TtsServiceUrl` | tts.GetValue<string>().Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [385](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L385) |
| `voiceEagerness` | int / `VoiceEagerness` | Math.Clamp(vge.GetValue<int>(), 0, 100) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [384](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L384) |
| `voiceEmotion` | string / `VoiceEmotion` | (ve.GetValue<string>() ?? string.Empty).Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [382](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L382) |
| `voiceMaxChars` | int / `VoiceMaxChars` | Math.Clamp(vmc.GetValue<int>(), 10, 300) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [383](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L383) |
| `voiceName` | string / `VoiceName` | vn.GetValue<string>().Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [378](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L378) |
| `voicePitch` | int / `VoicePitch` | Math.Clamp(vp.GetValue<int>(), -12, 12) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [380](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L380) |
| `voiceSpeed` | int / `VoiceSpeed` | Math.Clamp(vs.GetValue<int>(), 50, 200) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [379](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L379) |
| `voiceVol` | int / `VoiceVol` | Math.Clamp(vv.GetValue<int>(), 0, 1000) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [381](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L381) |
| `webSearchCooldownSeconds` | int / `WebSearchCooldownSeconds` | Math.Clamp(wsc.GetValue<int>(), 0, 86400) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [453](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L453) |
| `webSearchMaxResults` | int / `WebSearchMaxResults` | Math.Clamp(wsr.GetValue<int>(), 1, 10) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [452](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L452) |
| `webSearchSources` | string / `WebSearchSources` | wss.GetValue<string>().Trim() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [451](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L451) |
| `webSearchTimeoutSeconds` | int / `WebSearchTimeoutSeconds` | Math.Clamp(wst.GetValue<int>(), 5, 60) | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [454](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L454) |
| `webSearchUseModelSearch` | bool / `WebSearchUseModelSearch` | wsm.GetValue<bool>() | 01/04/06；提交后运行时投影，精确生效规则待消费者确认 | [450](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L450) |
| `whitelistGroups` | string / `WhitelistGroups` | wlg.GetValue<string>() ?? string.Empty | 01/02/06；重建名单/清待回复台账，保留会话历史 | [335](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L335) |
| `whitelistPrivates` | string / `WhitelistPrivates` | wlp.GetValue<string>() ?? string.Empty | 01/02/06；重建名单/清待回复台账，保留会话历史 | [336](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L336) |

### 2.2 秘密及显式清除输入（9项）

下表“存储槽”仅指真实现有槽；建议槽必须单独冻结，不凭命名自动创建。新端口候选为Keep / Replace / Delete，Replace空值拒绝；旧HTTP纯映射要保留各字段原有空含义，不能直接改成一种含义。值为write-only，DTO、日志、审计、异常、默认ToString、幂等冲突响应均不含原文/前后缀。不可用普通record(Value)声称安全；序列化与红acted ToString需合成负例。堆内字符串不能承诺完全擦除。

| 旧请求key | 旧类型/空/优先级 | 存储槽与安全快照 | 源码位置 |
| --- | --- | --- | --- |
| `apiKey` | string TryGetValue/Trim；空=删除并回QQCHAT_API_KEY/OPENAI_API_KEY；Missing/null保留 | 现有apiKey；ApiKey/Override JsonIgnore；新仅configured/source。启动允许BOTAGENT别名而热清除未使用它，需统一来源规则 | Settings.cs:720–738；BotConfig.cs:53,433,466–473 |
| `agentServerKey` | string TryGetValue/Trim；空=删除并回QQCHAT_AGENT_SERVER_KEY；Missing/null保留 | 现有agentServerKey；AgentServerApiKey/AgentServerApiKeyOverride运行时字段；新仅元数据，清除不是撤销环境值 | Settings.cs:588–607；BotConfig.cs:434,477–484 |
| `ttsApiKey` | string TryGetValue/Trim；空/空白保留；clear旗标true优先于替换 | 现有ttsKey；新仅configured/source，外部文件投影单列。不能把ttsKey审计误名作为可写别名 | Settings.cs:412–428,975–976 |
| `clearTtsApiKey` | bool TryGetValue；仅true删除槽；false/null/Missing保留 | ttsKey；数据库删除≠外部TTS已生效，也不保证环境已失效 | Settings.cs:412–416 |
| `feishuAppSecret` | string TryGetValue/Trim；空保留；clear true优先 | 现有feishuAppSecret；热保存将运行时值设新Secret，启动环境优先；新来源/需重启说明须02确认 | Settings.cs:296–309,985–990；BotConfig.cs:69,425–428 |
| `clearFeishuAppSecret` | bool TryGetValue；仅true删除并回BOTAGENT/QQCHAT飞书环境变量 | feishuAppSecret；Delete只是删除数据库覆盖，非删除环境 | Settings.cs:296–301 |
| `feishuEncryptKey` | string TryGetValue/Trim；空保留；clear true优先 | 现有feishuEncryptKey；启动环境优先；安全元数据及适配器应用状态单列 | Settings.cs:313–326,992–997；BotConfig.cs:71,429–432 |
| `clearFeishuEncryptKey` | bool TryGetValue；仅true删除并回BOTAGENT/QQCHAT环境变量 | feishuEncryptKey；Delete非环境变更 | Settings.cs:313–318 |
| `feishuVerificationToken` | 旧string GetValue/Trim；空=清空，Missing/null保留 | **当前在settings JSON可序列化且响应991明文；尚无专属SecretsStore槽**。新必须write-only；新槽、旧值迁移/回退、环境播种和旧UI编辑兼容须01/02/06明确，不能声称已修复 | Settings.cs:291,991；PlatformOptions.cs:47；app.js:2277,2474；BotConfig.cs:194,399 |

秘密保存不等于当前原子：旧ApplyBehavior/MultiPlatform/Channel/Reporting中的SecretsStore.Save都各自开AppDatabase.Write，部分发生在SettingsStore提交前，bool结果多处未检查。迁移必须用同连接内部参与者并抛错回滚；不能外包一层事务再调旧Save（嵌套事务已拒绝）。未知秘密名、官方Secret槽和别名都fail closed。旧历史officialAppSecret行保留但从不成为运行时来源，不为“清理”读取或删除。

### 2.3 非配置输入（4项，含两项拒绝字段）

| 旧请求key | 分类与实际行为 | 新边界 / 位置 |
| --- | --- | --- |
| `mood` | string TryGetValue；提交后独立MoodStore动作，空重置自动描述；不是配置属性 | 不混入声明全局原子的配置Patch；旧HTTP兼容要组合后明确动作成功/失败，幂等重放不得重做；Settings.cs:741–757 |
| `ttsKey` | 仅AuditSecretRotations读取，**没有设置/秘密写入分支**；传入可生成错误轮换审计 | 新保存拒绝，旧兼容不能当ttsApiKey别名；Settings.cs:167–170。真实ttsApiKey/clear动作审计覆盖须修正并验收 |
| `officialAppSecret` | 当前case-insensitive检查存在即400，null/空/false也拒绝且在副作用之前 | 环境专属；不出现在任何write-only秘密可选槽/快照/导出；Settings.cs:99–105；BotConfig.cs:70 |
| `clearOfficialAppSecret` | 同上；不是允许的Delete操作 | 同上，历史旧存储方法存在不构成业务授权；Settings.cs:99–105 |

## 3. 回显与配置属性差集

### 3.1 仅响应字段（48项）

这是当前旧响应事实，不是新ZBA字段清单。新快照只能明确许可元数据；旧masked字段仍含前缀/后缀不能直接复制到“零秘密”新DTO。对外端点/路径/身份不因非秘密自动公开。所有这些字段进入新Save时应拒绝，旧兼容的忽略差异由06处理。

| 旧响应key | 分类 | Settings.cs行 |
| --- | --- | --- |
| `agentPromptDefault` | 派生/状态摘要，非写入字段 | [931](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L931) |
| `agentServerKeyMasked` | 旧掩码仍含部分凭据；新仅configured/source元数据，旧兼容待06确认 | [889](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L889) |
| `agentServerKeySet` | 派生/状态摘要，非写入字段 | [888](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L888) |
| `agentServerKeySource` | 派生/状态摘要，非写入字段 | [890](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L890) |
| `agentServerQqActionsEffective` | 派生/状态摘要，非写入字段 | [881](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L881) |
| `apiKeyMasked` | 旧掩码仍含部分凭据；新仅configured/source元数据，旧兼容待06确认 | [1020](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1020) |
| `apiKeySet` | 派生/状态摘要，非写入字段 | [1021](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1021) |
| `apiKeySource` | 派生/状态摘要，非写入字段 | [1022](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1022) |
| `approvalCapabilities` | 派生/状态摘要，非写入字段 | [907](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L907) |
| `approvalTool` | 派生/状态摘要，非写入字段 | [905](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L905) |
| `approvalTtlSeconds` | 派生/状态摘要，非写入字段 | [906](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L906) |
| `channels` | 派生/状态摘要，非写入字段 | [836](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L836) |
| `dataDir` | 环境/身份/路径；旧只读，安全展示需脱敏/许可白名单 | [1027](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1027) |
| `env` | 旧响应容器/实现路径，新快照不得透传实现路径 | [769](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L769) |
| `feishuEncryptKeyConfigured` | 派生/状态摘要，非写入字段 | [992](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L992) |
| `feishuEncryptKeyMasked` | 旧掩码仍含部分凭据；新仅configured/source元数据，旧兼容待06确认 | [993](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L993) |
| `feishuEncryptKeySource` | 派生/状态摘要，非写入字段 | [994](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L994) |
| `feishuSecretConfigured` | 派生/状态摘要，非写入字段 | [985](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L985) |
| `feishuSecretMasked` | 旧掩码仍含部分凭据；新仅configured/source元数据，旧兼容待06确认 | [986](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L986) |
| `feishuSecretSource` | 派生/状态摘要，非写入字段 | [987](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L987) |
| `healthPort` | 环境/身份/路径；旧只读，安全展示需脱敏/许可白名单 | [1028](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1028) |
| `hostAgent` | 旧enableHostAgent只读别名，不是新增写字段 | [878](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L878) |
| `modelBaseUrlSource` | 派生/状态摘要，非写入字段 | [1009](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1009) |
| `modelSource` | 派生/状态摘要，非写入字段 | [1011](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1011) |
| `moodSummary` | 派生/状态摘要，非写入字段 | [954](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L954) |
| `musicAudioToModelMaxKb` | 可持久化但旧Save未接受；不能因回显就标可写 | [863](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L863) |
| `neteaseCookieSet` | 派生/状态摘要，非写入字段 | [939](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L939) |
| `officialConversations` | 会话数据形状，非配置；隐私投影/独立授权，不并入配置快照 | [842](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L842) |
| `officialSecretConfigured` | 派生/状态摘要，非写入字段 | [831](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L831) |
| `officialSecretSource` | 派生/状态摘要，非写入字段 | [832](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L832) |
| `oneBotAddress` | 环境/身份/路径；旧只读，安全展示需脱敏/许可白名单 | [1024](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1024) |
| `oneBotProtocol` | 环境/身份/路径；旧只读，安全展示需脱敏/许可白名单 | [1023](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1023) |
| `oneBotTokenMasked` | 旧掩码仍含部分凭据；新仅configured/source元数据，旧兼容待06确认 | [1025](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1025) |
| `participationGating` | 派生/状态摘要，非写入字段 | [920](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L920) |
| `participationPolicy` | 派生/状态摘要，非写入字段 | [918](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L918) |
| `questionCapabilities` | 派生/状态摘要，非写入字段 | [925](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L925) |
| `questionTool` | 派生/状态摘要，非写入字段 | [924](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L924) |
| `replyModel` | 派生/状态摘要，非写入字段 | [788,1014](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L788) |
| `runtime` | 旧响应容器/实现路径，新快照不得透传实现路径 | [767](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L767) |
| `scenarioCapabilities` | 派生/状态摘要，非写入字段 | [897](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L897) |
| `scenarioPresets` | 派生/状态摘要，非写入字段 | [895](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L895) |
| `settingsFile` | 旧响应容器/实现路径，新快照不得透传实现路径 | [770](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L770) |
| `ttsKeyConfigured` | 派生/状态摘要，非写入字段 | [975](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L975) |
| `ttsKeyMasked` | 旧掩码仍含部分凭据；新仅configured/source元数据，旧兼容待06确认 | [976](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L976) |
| `tz` | 环境/身份/路径；旧只读，安全展示需脱敏/许可白名单 | [1029](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1029) |
| `uin` | 环境/身份/路径；旧只读，安全展示需脱敏/许可白名单 | [1026](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L1026) |
| `whitelistGroupsFromLegacy` | 派生/状态摘要，非写入字段 | [783](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L783) |
| `whitelistPrivatesFromLegacy` | 派生/状态摘要，非写入字段 | [784](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L784) |

### 3.2 未经旧保存入口编辑的public get/set属性（19项）

不凭旧回显、public setter或环境种子判定已支持可写。统一保存新schema需明确这些字段是保留/只读/受控可写/内部迁移，不得丢失旧数据库合法字段，也不能顺手扩大首版高权限范围。

| 属性 | 当前类型/序列化 | 评审归属及边界 | 定义位置 |
| --- | --- | --- | --- |
| `WebSearchReadMaxChars` | int / 可序列化属性 | 旧保存未接受；非秘密行为配置；新是否可写须01/04/06明确，旧值保留 | [457](../../../src/BotAgent.Headless/Services/AppSettings.cs#L457) |
| `MusicAudioToModelMaxKb` | int / 可序列化属性 | 回显863但没有保存分支；非秘密行为配置；01/04/06确认可写边界，不当成已可编辑 | [508](../../../src/BotAgent.Headless/Services/AppSettings.cs#L508) |
| `NeteaseCookie` | string / JsonIgnore | 秘密；来自扫码/登录专用链路，不纳入全局配置Save；不回显Cookie；仅configured元数据 | [518](../../../src/BotAgent.Headless/Services/AppSettings.cs#L518) |
| `PanelToken` | string / JsonIgnore | 环境认证秘密；不纳入配置Save/Read/export；认证方案归06 | [559](../../../src/BotAgent.Headless/Services/AppSettings.cs#L559) |
| `AgentToken` | string / JsonIgnore | 环境认证秘密；不纳入配置Save/Read/export；设备连接认证归04/06 | [705](../../../src/BotAgent.Headless/Services/AppSettings.cs#L705) |
| `AgentServerMaxTokens` | int / 可序列化属性 | 旧保存未接受；非秘密模型/Agent配置；01/03/04/06决定，不默认为运行时可写 | [805](../../../src/BotAgent.Headless/Services/AppSettings.cs#L805) |
| `HealthPort` | int / 可序列化属性 | 环境基础设施/只读；修改后需宿主重启，不能经统一Save写 | [873](../../../src/BotAgent.Headless/Services/AppSettings.cs#L873) |
| `VerboseLog` | bool / 可序列化属性 | 环境基础设施；旧Save无入口；安全诊断策略由06确认 | [876](../../../src/BotAgent.Headless/Services/AppSettings.cs#L876) |
| `Theme` | string / 可序列化属性 | 源码注释遗留不用；保持兼容读取，不虚构新功能或默认删除 | [879](../../../src/BotAgent.Headless/Services/AppSettings.cs#L879) |
| `NapCatWebUiUrl` | string / JsonIgnore | JsonIgnore运行时基础设施/代理地址；旧Save无入口；安全快照禁止完整端点/凭据URL默认输出 | [886](../../../src/BotAgent.Headless/Services/AppSettings.cs#L886) |
| `NapCatWebUiToken` | string / JsonIgnore | 环境秘密；旧Save无入口；代理认证不纳入配置Save | [890](../../../src/BotAgent.Headless/Services/AppSettings.cs#L890) |
| `OneBotProtocol` | string / 可序列化属性 | 环境基础设施/只读；驱动配置可编辑范围须02/06确认，不凭新DTO扩大 | [14](../../../src/BotAgent.Platforms/PlatformOptions.cs#L14) |
| `OneBotAddress` | string / 可序列化属性 | 环境基础设施/只读；新安全快照不默认完整端点 | [15](../../../src/BotAgent.Platforms/PlatformOptions.cs#L15) |
| `OneBotToken` | string / JsonIgnore | 环境秘密；Save/Read/export不接不回；仅安全元数据 | [18](../../../src/BotAgent.Platforms/PlatformOptions.cs#L18) |
| `QuickLoginUin` | string / 可序列化属性 | 环境身份/只读；展示脱敏，存储与路由key原样 | [19](../../../src/BotAgent.Platforms/PlatformOptions.cs#L19) |
| `OfficialAppSecret` | string / JsonIgnore | 仅QQCHAT_OFFICIAL_APP_SECRET；历史数据库行不采用、不迁移进新可写槽 | [33](../../../src/BotAgent.Platforms/PlatformOptions.cs#L33) |
| `OfficialApiBase` | string / 可序列化属性 | 旧Save未接受；平台基础设施；02/06明确来源/可写/重启，不猜 | [38](../../../src/BotAgent.Platforms/PlatformOptions.cs#L38) |
| `OfficialTokenUrl` | string / 可序列化属性 | 旧Save未接受；平台基础设施；02/06明确来源/可写/重启，不猜 | [39](../../../src/BotAgent.Platforms/PlatformOptions.cs#L39) |
| `PlatformSwitchSchemaVersion` | int / 可序列化属性 | 内部迁移标记（0/1）；不能作为客户端任意可写配置 | [63](../../../src/BotAgent.Platforms/PlatformOptions.cs#L63) |

## 4. 横切兼容与事务缺口

### 4.1 平台策略与legacy别名

- TryParsePlatformPolicies在Settings.cs:184–230：最多32项；platformId规范、accountScope Trim且长度1–80/ASCII字母数字._-；按二者IgnoreCase复合去重；group/private名单最多4096字符；FeatureOverrides最多32且key≤64/许可字符；AllowedActions最多64且每项非空≤80，但并未证明每个动作是已登记真实能力。JSON嵌套未知属性当前未显式拒绝。
- 保存次序是Normalize→Behavior→Channel→ApplyLegacy→MultiPlatform→SynchronizeLegacy→Reporting。完整策略随后覆盖QQ/Feishu/Local旧白名单，当前代码不按accountScope区分回写旧字段；同平台多scope的legacy字段可能依赖列表顺序。新合同要冻结同时提供旧别名与策略时的冲突/优先级；不能通过顺序偷偷改变语义。
- 已有PlatformContext包含PlatformId/AccountScope/InstanceId，而当前策略/注册表只用前两者。C4需要完整稳定三元身份和旧二元查找歧义规则；显示名/nameRaw不是key。字段本身的Enabled/ChatEnabled与连接实态分离。
- **空值不是统一fail closed**：WhitelistGate.cs:68–77私域空分区回落MessageWhitelist；79–88官方有效空名单全允许；90–97本地空名单全拦。FeishuBotGateway.cs:477–487飞书有效空名单全拦，但首先取同平台首条Policy，没有匹配scope/instance。新合同须分别表达LegacyInherited、ExplicitEmptyDeny、允许全量，不将旧官方空值悄悄变成deny，原key/官方别名与私域隔离仍保持。
- PlatformPolicyResolver.cs:46–48/93–98仍按platform+account IgnoreCase匹配，没有InstanceId；Registry按旧二元Ordinal字典。新三元identity必须同时迁移Resolver/飞书消费/白名单/旧字段同步，单修DTO/保存回写不能证明实际隔离。合成多账号、多实例、重复identity、旧别名与结构策略同时出现、全量替换和各平台空名单测试均为待做，不计入本批验证。

### 4.2 AgentDevices不能当不透明字符串安全快照

- AppSettings.cs:593–599现有DeviceConfig只含name/enable/model/workdir/tools/timeoutSec（构造参数TimeoutSeconds）；没有Key。ParseDeviceConfigs605–659只投影已知字段，坏数组/坏元素可能跳过，**并未改变保存515/回显893的原始字符串**。
- app.js:2492–2500正常UI仅发六字段，且加载失败不写整表，但手工请求可以包含其它属性；客户端白名单不等于服务端白名单。
- 候选新schema严格array<object>，仅六字段，name非空、稳定ID/大小写/长度/数量/timeout范围待04/06确认；unknown key（例如secret/key/password）明确拒绝而非保存/回显。原始已有AgentDevices不得直接进入SafeView；迁移投影损失的未知属性需要兼容策略，不读取真实配置来猜。
- 列表全量替换需version/CAS，设备在线/当前目录/models/capabilities为独立状态，不能写回配置。原始身份存储及key保持原样，显示脱敏；改真名用nameRaw，不能保存显示占位符。

### 4.3 Commit/Published/Applied三事实

真实当前链路：WebUiServer.HandleSettingsSaveAsync→SettingsHotReload.ApplyRuntimeSettings→SettingsBox.ApplyPersisted→Host SettingsStore.Save→单例Storage AppDatabase.Write→settings和可选audit同事务→Interlocked发布→RebuildRuntime→afterPersist动作/日报重排/秘密审计。

- SettingsBox.cs:46–64先mutate快照再persist再换引用；mutate必须纯。本批事实表指出秘密旧写在mutate内，这是当前迁移缺口，不称为C2故障已复现。
- RebuildRuntime依次重建权限/参与/白名单，清待回复与审批/参与台账，同步模型字段，换并发闸门，裁已有消息窗口，重排scheduler和UI。PruneNonWhitelisted保留会话历史，但removed.Count>0会再次_registry.Save；这些不能放配置事务内或幂等重放重做。
- Host SettingsStore与AuditLogStore.AppendInTransaction已有同连接写审计，IAuditChain仅Append/Verify，不能假造事务端口。必要安全审计/版本/秘密操作元数据/幂等结果须同配置事务；值和不受控客户端字段名不能入审计。审计链现有SHA256不是HMAC，不能误称已具备受密钥保护指纹。
- BuildSettingsAuditEvent在提交前写result=applied（Audit.cs:37），即使后续运行重建失败也仍可能已入库；这不能当应用成功证据。候选分别记录commit结果和应用阶段安全结果，受控字段ID而非任意提交属性名。
- 当前catch返回通用500/settings_save_failed且注释已承认可能提交后失败。新Result须含CommittedVersion及ApplyState(未应用/部分应用/已应用/需重启/重启后恢复)，部分失败不得声称所有旧值仍生效或撤销提交。GET/Find不返回秘密值或客户端请求体。
- 旧模型元数据EnsurePrimary/Save与熔断SaveCircuit分开，配置元数据参与事务或明确postcommit边界，熔断运行态不能因配置保存/重放重置。

### 4.4 TTS/即时动作不属于SQLite原子性

- WriteTtsConfToHost(Settings.cs:30–58)从秘密库读取并写给另一进程文件；TryWrite false只记日志。现有afterPersist可因key和provider/base/model同时提交添加两次相同动作。候选按目标版本合并外部投影一次，报告Applied/Failed而不是伪造事务回滚。
- TTS外部文件不能和SQLite承诺全局原子；需安全投影记录、配置版本、失败查询/明确恢复或需重启；首次请求和重放不能重复外部IO。重启恢复允许纯运行快照重建，但mood动作/消息裁剪/一次性操作是否可重做要单列，不用“自动恢复”掩盖重复副作用。
- **测试先建立可控合成输出路径**。现有TtsConfFile优先宿主挂载目录，未冻结注入点前不跑会写挂载的全量场景；不读取/创建/清除真实宿主TTS文件或env值。本批无项目运行/动态复现。
- 旧HTTP可能把mood与配置混合，一次POST两个介质；适配层必须诚实反馈组合结果，不能把mood列成SQLite配置字段来缩小测试。

### 4.5 启动与环境优先级

- BotConfig.Load:29–44从settings读→InfrastructureEnvironment→BehaviorEnvironment(seedOnly=!hasStored)→PanelOverrides→Normalize，hasStored时还会普通Save。这是CAS迁移必须接管的旧写入口，不可让启动writer绕过版本/审计。
- 候选用户持久配置与runtime env overlay分离；启动不把环境秘密/投影值重新当用户修改。已有合法字段/迁移标记保持，旧JSON导入通过同验证/版本/保存服务，未来手改文件不自动成为真源。
- InfrastructureEnvironment:58–59对FastModel/FastReply赋环境值，而注释称种子；静态次序提示重启时覆盖可能，**不是动态恢复已失败证据**。01/03/06须决定保留旧优先级或兼容升级，不偷偷消除/重复覆盖。
- 飞书环境值启动优先，热替换可先改变runtime但已建适配器是否捕获旧凭据需02确认；现有source摘要根据环境存在可能与热runtime新值不一致。apiKey/AgentKey删除后环境回退不等于凭据彻底失效。新快照分Requested/UserVersion、EffectiveSource、ApplyState，不回显值。
- 上句启动环境优先仅指AppSecret/EncryptKey基础设施凭据；**VerificationToken是行为种子**：BotConfig.cs:88–99在!hasStored时才Seed，已存配置直接return；字段映射194及种子赋值399。迁新秘密槽须有Initialized/Deleted事实，不能每次启动重新env回填已明确删除的Token。

## 5. C4消费者评审对照

| 管理对象 | 已核接口/实现事实 | 候选必须冻结 / 当前缺口 |
| --- | --- | --- |
| 驱动实例 | Core PlatformContext三元；Platforms PlatformRegistry字典二元；GetAdapter/GetMessenger旧二参；GetSnapshots Enabled硬编码true | 三元identity、旧二参歧义fail closed、ExpectedEnabled vs ActualConnected、readonly状态；已有消息Capability不等于Start/Stop/Test配置管理能力 |
| 插件 | IPluginRegistry query-only；真实Services/Plugins/PluginManager.cs中的PluginManager.SetEnabled仅内存；StartAll/StopAll只遍历当前enabled；PluginStore尚无生产采用 | 稳定PluginId/大小写规则（manager Ordinal vs store IgnoreCase）、启停真实支持及已初始化资源收尾；禁用工具不调外部IO要行为证据 |
| 模型 | Provider.Id/primary已有；元数据与熔断分写；新Model未直接被Headless采用；CompositionRoot.cs:611–618拒绝空凭据，635–657按任意secret:/env:引用读取 | 模型/provider稳定key，配置和runtime circuit分离；新None不是现有匿名调用能力；既有credential引用也须运行allowlist，不只限制新写；测试连接为独立授权动作，不把存在类等于管理已可用 |
| Agent设备 | DeviceConfig六字段；bridge在线状态另有来源 | 不冒充通用驱动；配置ID和连接scope/instance需04/06确认；鉴权秘密不夹原始JSON |
| MCP | 未核实真实生产接口/链路；不得造SDK/连接测试API | 本轮Unsupported/未实现诚实返回；Wave4仍必选，不因当前unsupported而豁免 |
| 部署/Docker | 旧panelDeployEnabled/Url与agentServerDocker可保存 | 配置字段不授生产操作权；首版Docker高权限管理不进入必选范围；没有部署/重启/远程操作授权 |

候选管理DTO只包含ManagedKey(Kind,StableId,PlatformId?,AccountScope?,InstanceId?)和显式ManagementCapabilities/状态，准确optional字段约束按Kind冻结；不用统一Start/Stop硬套所有对象。C4精确端口/错误、known action集合、ID长度/大小写/分页/状态恢复还未冻结，不存在可直接调用的新接口。

## 6. 冻结前的决策与合成验证清单

### 6.1 接口与消费者确认（仍待完成）

1. 01提出三入口Read/Save/Find精确签名和全部DTO：独立Core类型，闭集强typed分区与第2/3節151+19属性映射，write-only敏感类型与安全View；Host/Platforms/SQLite类型不进Core。现有ISettingsRepository依赖Host AppSettings不可原样搬Core。
2. 02确认平台Policy/legacy同步、三元identity、环境可写边界及启停/捕获状态；03确认全部模型字段、空继承、枚举/自定义值、env播种和metadata原子边界；04确认权限/AgentDevices/插件/一次性动作/应用失败；05明确MCP扩展slot而非伪造当前支持。
3. 06早期确认旧HTTP纯映射、严格新schema与旧UI兼容，VerificationToken迁移/编辑，认证调用者identity、权限/秘密allowlist、无秘密安全DTO、大小限制、已提交响应和Find恢复。其最终OpenAPI/路由将在真实工程形成后，不在此虚构。
4. 00汇总冲突后登记具体版本与真实review确认，才发放消费代码写权。第十五节只读替代方案顾问不是01–06职责确认；本文件也不是任何消费者签字。

### 6.2 幂等/版本的精确合同仍欠缺

- 建议scope为受信任CallerIdentity+configuration resource，非任意客户端自报身份；同key同规范请求返回原Commit结果并读现ApplyState，不再次mutate/publish/裁剪/外部IO；同key不同参数Conflict，ledger命中优先于baseVersion冲突。秘密值不得回显或落明文请求指纹；哈希需受保护方案/密钥生命周期。
- **有效期、容量、过期tombstone、大小上限和恢复策略目前无批准精确值**：需01/06明确数值、持久计时源IClock、容量满拒绝/清理、重启后key不得无声复用规则，不能以“有界”两字代替。旧DB writer拒nested；配置+版本+安全审计+秘密+必要幂等结果同事务，apply记录后续单独事务需定义。
- 取消提交前可拒绝；开始提交后超时/断连只能Unknown或可Find，不能声称回滚；重启从已提交版本恢复但不重做外部一次性操作。CLI只读，内部startup/legacy/import写入口也要同版本协调。

### 6.3 应做测试（本批均未运行）

- 对142非秘密字段逐项类型/边界、Missing/null/空/false/0、Normalize一致性、未知字段/重复字段/嵌套未知/非object请求、有限double及整批后置验证失败。
- 九秘密输入逐项keep/replace/delete、各旧空规则、clear与replace同时存在、未知slot、大小写official存在拒绝（null/空/false）、Read/Find/export/ToString/异常/日志/审计无值/前后缀；合成VerificationToken旧JSON/数据库迁移与恢复。
- 合成AgentDevices含未知key/password等拒绝，无原始JSON回显；nameRaw显示mask与存储/稳定key原样；名单与多账号/实例隔离、旧策略别名冲突。
- 单事务错误注入覆盖配置、秘密、版本、审计、ledger任一步失败全无部分提交；CAS竞态；同key同请求/不同参数/范围隔离/容量/过期/重启、秘密指纹安全、pending结果恢复。
- 提交失败不发布；发布/运行应用/外部投影失败分别Committed+具体状态；重复请求不裁剪/清台账/发通知/写TTS/改mood；配置重启恢复，未来schema拒绝，无旧双单例。
- 实际Host HTTP/运行服务调用链负例，不能仅Storage程序集或静态regex通过。获具体代码测试认领后按真实工程测试入口执行；任何IT前单独Rebuild Headless，再build harness，检查每个原生命令退出码，保留架构棘轮。
- 旧SDK8/CI/Linux/资源/旧产物回退/完整矩阵仍由对应门禁负责；这份字段表不声称替代其证据。

## 7. 源码绑定与参考验证

2026-10-10本轮安全源码SHA如下；任何一项变化应重新核对受影响矩阵，不能继续沿用本表冻结。文档校验/工作区最后指纹及认领收口登记总纲第十六节；没有构建/探针/数据库/生产访问。

| 输入文件 | SHA256 |
| --- | --- |
| [src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs) | `B5071F26A303C41152F8673E929A11736B9FF5A01995ABF8EB694ADA3F6253AB` |
| [src/BotAgent.Headless/Services/AppSettings.cs](../../../src/BotAgent.Headless/Services/AppSettings.cs) | `AC59A571A97C37F202DE7AC5CA21DC0E0EFA040010729ED219882174B8171224` |
| [src/BotAgent.Platforms/PlatformOptions.cs](../../../src/BotAgent.Platforms/PlatformOptions.cs) | `90E7A6AE0D94A93F5B7B6BBA457710A6B4BBCAE2E1A63315DACBB89C342D57AB` |
| [src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs](../../../src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs) | `49EF637419221B0FB257640CC9C2E2CF9441F99B1E222036E54A284837DB025E` |
| [src/BotAgent.Headless/Adapters/Panel/WebUiServer.Audit.cs](../../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Audit.cs) | `F32BCD48C651115C789DFAD54FA02747AF9F1D14E998506774DBAD3F6DB06A35` |
| [src/BotAgent.Headless/wwwroot/app.js](../../../src/BotAgent.Headless/wwwroot/app.js) | `E01AC150E7344CB772B843BB3FEF9B02AB5820D3FA78704C25919C53D4387554` |
| [src/BotAgent.Headless/Services/Settings/SettingsHotReload.cs](../../../src/BotAgent.Headless/Services/Settings/SettingsHotReload.cs) | `459F4DE9F8A17A4DD5F1C95DFC6E89D8EECE6EE403B118EC63FFD1420DAE42AF` |
| [src/BotAgent.Storage/SecretsStore.cs](../../../src/BotAgent.Storage/SecretsStore.cs) | `074D4701B5EF8C045D47FFAD36560F32308900F7BEE144B25A9335AA9118CE15` |

矩阵覆盖校验仅证明源key/属性差集和行链接无遗漏，不证明类型规范已编译或运行生效。新契约精确签名、预算和消费者确认仍欠缺，**阶段一开工条件仍未满足**。回退只针对本批文档片段/新参考，核后续编辑与归属再逆转，不回退此前源码/文档或生产环境。
