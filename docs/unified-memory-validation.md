# 统一记忆首版本地验收记录

日期：2026-11-06。关联 [Issue #153](https://github.com/ZhaoJun233/bot-agent/issues/153) 与 [ADR 0004](<decisions/0004-unified-memory-authorization.md>)。

## 1. 结论与授权范围

**首版本地实现及下列检查已完成；不是生产发布或对所有可能输入的安全证明。** 默认关闭，使用方法和兼容性代价见[使用指南](<unified-memory.md>)。本次未读取生产聊天、真实画像、生产数据库或密钥，未部署、推送或提交代码。

实现位于独立工作树的 `feat/issue-153-unified-memory` 分支；基线提交为 `b80d7b271bae58bf7f0b8f4261b03f6c4ffe0199`。其他工作树不作为构建或写入目标。

## 2. 冻结候选

以下 SHA-256 与最后两次真实宿主测试绑定；Lead 对源码聚合与关键 DLL 另外复核过。

| 对象 | SHA-256 |
| --- | --- |
| 全部生产源码聚合 | `e4ab627c79ad87b74c436521a4a46b6014350a50c018261eafd140e8f1d82259` |
| 73 个变更/新增实现、测试、解决方案及 CI 文件的清单 | `ac474e040953e8a64314e1f2ea8de5f75355f96e36cee44e4258c955af1888a0` |
| Headless DLL | `020d1405ab447748bc965b251141065162840f9de3d7ae9107489f579bc12df7` |
| Engine DLL | `a34e3d852452c4d9c69145877b9339022d6c4e28c2fe950baeb437507781eee5` |
| IntegrationHarness DLL | `b2b258181bef4bdd28ce7c76fbbb8e5b221e991fa09b1733215550550a291200` |
| ReviewRemediationProbe DLL | `d56b3710b6904f2652a4824fc0d0766ce9ca544d8bf2fcaa84d075b15c707516` |

生产源码聚合算法（在工作树根执行）：

```bash
git ls-files --cached --others --exclude-standard -z -- src | xargs -0 sha256sum | sha256sum
```

73 文件清单按路径排序，以每个文件的 `path/revision/bytes` 记录组成 JSON 数组，再对 `JSON.stringify(records)` 求 SHA-256；revision 为文件内容的 SHA-256。它不包含文档及生成产物，避免报告自引用。完整清单、原生命令输出、早期失败和修复后的证据保存在工作树之外的本地验收资料中，不把开发机路径写入公开文档。

## 3. 执行结果

所有下列最终命令退出码均为 0；计数是探针断言/检查次数，不是互不重复的业务场景数。

| 检查 | 结果 | 主要证据范围 |
| --- | --- | --- |
| Headless Release 构建 | 0 警告 / 0 错误 | 实际宿主接线与类型检查 |
| UnifiedMemoryProbe | 162 通过 / 0 失败 | 身份、版本、授权、撤销、遗忘、冲突、持久化和失败关闭 |
| MemoryTurnProbe | 149 / 0 | 实际调用封装、使用凭据、来源失效、空画像、命名空间隔离与无效输入 |
| MemoryCommandProbe | 145 / 0 | 本人私聊确认、管理员治理、群内不泄露、默认实例精确匹配 |
| MemoryDeliveryProbe | 33 / 0 | 模型/辅助模型、实际受控发送、来源/目标限制与分段失效 |
| ReviewRemediationProbe --memory-scheduler | 22 / 0 | 真实 Scheduler 与 OpenAiClient、非空候选、HTTP spy、停用竞态 |
| ReviewRemediationProbe --task-approval | 34 / 0 | 既有工具审批隔离回归 |
| ArchitectureProbe | 219 / 0 | 依赖、SQL/IO 归属与函数规模棘轮；未放宽 Baseline |
| IntegrationHarness s153 | 连续两次各 176 / 0 | 真实 Headless 进程、OneBot 协议和两个模型替身；每轮初次启动后再真实重启两次 |

四组记忆探针及 Scheduler、审批、架构均在最后冻结候选上由 Lead 串行复跑。s153 的两次复跑由独立执行者完成，绑定相同 Host/Harness，Lead 再核对其二进制摘要。最终增量构建无新增警告；先前 Harness 完整构建出现已有 CS8602，Review 完整构建出现已有三条 CS0649，未把这些旧警告改掉或当成新功能通过证据。

## 4. ADR 验收标准映射

“通过”仅指所列源码、探针和合成宿主证据，不表示未列出的场景已动态执行。

| 标准 | 本地判定与证据 |
| --- | --- |
| AC1 | 通过：核心/命令/宿主覆盖请求候选、准确版本与目标确认、来源内使用的显式同意。 |
| AC2 | 通过：核心与命名空间探针覆盖平台、账号、实例、成员、目标、类型和线程；宿主验证 foreign account/instance 档案不注入。 |
| AC3 | 通过（混合证据）：真实宿主拒读旧 scalar 及外来源档案，同时 exact-scoped 正控确实进入模型；源码复核证明协议补录不为画像归属、不迁移或回退。 |
| AC4 | 通过：命令确认要求群成员可见性告知，精确目标不按名字或后来的新群扩大。 |
| AC5 | 通过：管理员不能替本人确认共享；私聊治理与群内通用响应分开。 |
| AC6 | 通过：聊天/Agent 共用核心，实际模型/发送检查使用凭据；保留无记忆工具和历史正控，含记忆任务禁止外部工具与派生持久化。 |
| AC7 | 通过：编辑立即退役旧版本及授权，否定和未解决冲突停止注入。 |
| AC8 | 通过：统一记忆停用不删除管理状态；旧画像读取抑制独立生效；Scheduler 在 wired enabled/disabled 下均不扫描候选、请求模型或写摘要。 |
| AC9 | 通过（限定受控边界）：探针覆盖读取、缓存/凭据、模型和待发送/分段失效；宿主验证撤销及重启。真实 WebSocket 并发撤销竞态未单独动态覆盖。 |
| AC10 | 通过：逻辑删除统一事实与授权，保留无正文抑制标记；来源旧画像拒读，重启后不复活；不声称删除原始聊天或停止旧画像同源消息存储。 |
| AC11 | 通过：缺少身份、失效来源、无效键、损坏/写失败关闭读取；检索故障不转向旧 scalar 或更宽范围。 |
| AC12 | 通过：测试资料为合成身份、内容和本地替身；对外记录只含实现、测试标签、计数和摘要。 |

## 5. 原始反例与修复

- **空画像误限制普通 Agent：**原封装即使读到空字符串也注册凭据。保留无记忆自然 Agent 正控，改为先授权、读后仅对非空文本注册；同时保留读取期间遗忘/来源失效的反例。
- **旧画像来源混用（HIGH）：**新收据只证明当前请求人，不能证明旧 `uid+group` 数据来源。旧 Host 在新增反例下为 **174 通过 / 2 失败**，其中明确观察到未归属 scalar 档案进入模型。改为可信实时消息的完整来源命名空间、无旧键回退、协议补录不写画像、暂停无凭据的自动画像摘要。新 Host 为 **176 / 0，连续两次**；不是仅删掉旧数据或删掉失败断言。
- **后台标题导致夹具计数竞态：**普通 Agent 发完正文后仍有合法标题请求。保留命令“不触发模型”的总计数断言，改用已知串行条件下的新标题请求索引作为确定屏障；不假设被裁剪的标题 digest 必含当轮 marker。
- **键向量夹具错误：**三个新测试常量曾抄写错误，另一个断言把固定前缀单词误认作账号泄露；用独立规范字节串、Node SHA-256 和 C# 实际输出交叉验证，产品键算法与宿主固定向量未改变，原失败保留。
- **架构棘轮：**通过注册探针、复用既有元数据事务、提取连贯的失败处理等修复；没有提高阈值。

## 6. 未覆盖与首版代价

- 当前运行证据未单独覆盖“可信实时 Append 落库到下一轮读取”的完整链路、协议补录画像零写入的端到端断言、真实 WebSocket 撤销竞态及旧研究搜索/URL 缓存攻击的完整宿主用例；这些入口有源码复核或较小范围探针，不混称为已执行的全链路测试。
- 无来源证明的旧手工/自动档案仍保留，但不再注入模型；自动画像摘要暂停。新可信同源片段可以继续收集，遗忘抑制的是其模型用途，不是把所有存储删除。
- 记忆回合仅直接文本回答；不运行外部工具、自然 Agent 转交或后台媒体动作，不把派生结果写进全局心情、研究缓存或 Agent 历史。
- 已发送内容、已经开始的外部请求、原始聊天、WAL/备份及恢复旧备份不属于可追溯收回保证；不支持多宿主共享同一授权数据库。受信任同进程 DLL 不是沙箱。
- 本地源码审查范围内无未关闭 Critical/High/Medium；这不代替生产部署验收，也不代表所有遗留模块都已完成安全审计。

## 7. 复跑入口

在工作树根执行，使用 .NET 10；仅使用测试自建临时资料和替身。

```bash
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
for name in UnifiedMemory MemoryTurn MemoryCommand MemoryDelivery; do
  dotnet run --project tests/BotAgent.${name}Probe/BotAgent.${name}Probe.csproj -c Release
done
dotnet run --project tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release -- --memory-scheduler
dotnet run --project tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release -- --task-approval
dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
BOTAGENT_IT_ONLY=s153 dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll
```

四组探针、Scheduler 特定入口和 s153 已注册到 [CI](<../.github/workflows/ci.yml>)；增加 CI 配置不等于已经执行远端 CI。复跑不会自动部署或发布。

## 8. 公共 dev 增量集成复验（2026-11-06）

本节补充原候选的历史证据，不把上文旧二进制指纹冒充当前候选。公共基线为 `4bec023be0e884cf8594cbf3eccd0d0e2088ae2a`；在独立工作树上增量集成统一记忆，保留基线已有的平台拆分、回复节奏、取消语义和发送确认计数。

- 完整 solution Release 构建成功（.NET 10），0 错误；现有 4 条编译警告（合成探针中的一个未使用事件和三个未赋值字段）未作为成功测试隐去。
- 最终串行矩阵共 **37 个原生命令，37 通过 / 0 失败**：4 组记忆探针、Architecture、7 组 Reply 节奏/调度/提示词探针，以及 Safety、Participation、Engine、ProductionSpec、ConcurrencyStress、ChaosFault、Configuration、PipelineEval、ReviewRemediation、ReverseTransport、DeadlineGate、FeishuRemediation、SettingsScope、Ssrf、Mcp、Zba、Protocol、PluginPackage、PluginSettings、Snapshot、PluginMarket；另含 legacy-schema、memory-scheduler、task-approval 特定入口及重新构建宿主的 s153。只用合成数据。
- 首轮复跑误用旧 `net8.0` 产物路径，未执行有效探针；该运行失败记录保留，不计入测试通过。修正入口到 `net10.0` 后，第二轮为 35/37：GenerateReplyAsync 长度 199 超过既有阈值 198；旧反射夹具缺少 OneBotActionClient 新增来源参数。分别通过提取 BeginMemoryTurn 和向夹具传入 SyntheticSource 修复；没有提高架构阈值或让生产来源身份变成可选。随后重新构建并完整复跑上述 37 项。
- 独立只读复核检查了原候选与公共基线间的回合收据、来源恢复、最终 I/O 闸门、取消及发送确认合并，未发现具体阻断项；此结论不是全仓安全审计或生产部署验收。
- 相对公共基线共 79 个修改/新增路径，无删除或超出批准范围的路径。除了原始 77 个功能路径，集成适配仅增加拆分后的生成实现和对应 OneBot 节奏探针夹具。

最终本地源码指纹（按排序后的 `src` 相对路径与原始文件 SHA-256，以 NUL 分隔字段、LF 连接行再求 SHA-256）：

```text
src: dc9e04e3e8060da692b82ea87e5e8338f3cad61d859f42cead268fe4540eedd1
Headless.dll: 9f0fd0277e417a1c8a2462f3b82e0099e0d72cc7c72b6d4229204d4fb0527884
Engine.dll: 052a38c1957d44b1ed453128de8bbb471b9a2435872f6782b264c411f64a614b
IntegrationHarness.dll: d3283e7e81aad31554e79e7c617cddc069166e2a3656844d1bcff81c11b08a95
```

本节记录的是推送前本地验证；不预先宣称远端 CI 已通过。发布仍须走独立快照、双重脱敏审计和普通快进推送，基线移动时停止而非覆盖。上文未覆盖范围与收回边界继续有效。

## 9. 完整公开 CI 暴露的旧契约测试与修正

首轮公开提交 `6c27a022ac993d8ca05781dda3307c0dc08dc244` 的[完整 CI](https://github.com/ZhaoJun233/bot-agent/actions/runs/42179689000)保留为原始反例：Core & Chat 有 14 条失败断言，Media & Tools 有 1 条；构建与合成探针、治理/多平台及前端全部检查通过。它不是“只有网络查询失败”，也不能用第 8 节的 37 条本机命令替代。

多数失败断言仍要求旧 scalar uid 档案、无授权自动摘要或迁移画像直接注入模型，与已批准的 [ADR 0004](decisions/0004-unified-memory-authorization.md) §8.2.5 / §8.2.8 相反；S12 的上下文去重负控另行定位为当前窗口参与者速览的误判，未删除该负控。本轮只修订合成集成测试，不修改生产授权逻辑或架构阈值：

- 实时档案用完整来源摘要键验证；保留同源原文收集、异源隔离、普通回复正控。
- 开启旧摘要设置也不能恢复无撤销边界的自动摘要；记忆回合保持无工具，正常聊天/存储仍可用。
- 保留实际双宿主重启，验证聊天序号与同源档案继续增长，而不是让已停用的自动摘要进度充当持久化证明。
- 协议历史补录仍进入原始聊天/面板上下文，但不冒充实时档案、不通过 system 提示获得记忆授权。
- S27 继续验证旧 JSON 迁移、旧文件留档、幂等、旧聊天上下文和正常回复；新增旧 scalar 画像不注入模型且迁移数据不被删除的成对断言。

修订后的第一次定向复跑为 91 通过 / 28 失败，保留反例后查明合成夹具的三项假设错误：@ 消息存储原文带 `@10001 ` 前缀；上下文最小值是 10，原先 6 条种子未出窗口；数据库历史行含 `archived=1`，裁剪正控应数活动行而非要求删除历史。修正夹具后为 118 通过 / 1 失败。

最后一条 S12 反例中的重复文本精确位于 `老王：发言 9 次，最近说“@10001 补录之后的提问”`，由 [PromptBuilder 的窗口兜底](../src/BotAgent.Model/Model/PromptBuilder.cs#L375-L386)及[最近发言格式](../src/BotAgent.Model/Model/PromptBuilder.cs#L540-L558)生成，不是持久化画像回退。断言新增该合法速览恰好一次的正控，只排除这个精确合成用户/最新消息的速览行，再对其余完整 system 内容执行原去重负控；历史序号先于实时消息、禁止补录画像的断言也保留。生产代码未变，未通过禁止正常窗口上下文来掩盖失败。

最终定向复跑 **120 通过 / 0 失败**；完整本机 CI 分组 Core **205/0**、Media **319/0**、S153 **176/0**、Architecture **219/0**，四条原生命令均 exit 0。生产 `src`、Headless、Engine 指纹仍与第 8 节一致；测试程序集更新为 `8ac06160cd5459e9ce0546c5756e770bfe11bec7bc3af2a36879110a63e88763`。原始日志、分组退出码和候选指纹在工作区独立证据目录留存。

首轮及定向红灯、完整合成提示词诊断和网络查询 EOF 证据均保留，不以改写历史隐藏。以上为本地证据，追加公开提交仍须通过完整远端 CI 与内外独立脱敏复检。
