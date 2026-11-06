# #151 回复节奏：本地验收记录

日期：2026-11-06。范围仅为本工作树的 #151；基线 `b80d7b271bae58bf7f0b8f4261b03f6c4ffe0199`。未提交、推送、部署或启用线上开关，也未修改并行的 #152 工作树。

## 结论与交付边界

本地实现与下表合成检查通过。最终候选是否可交付，以末尾冻结门禁的实际输出为准：所有命令退出 0、源码差异为空；缺少记录或任一不满足时，不得视为最终完成。本报告与源码一起冻结，不在运行后修改结论来改变候选哈希。默认关闭；启用后的参数默认值为群参与概率 20%、群冷却 30 秒、群连续输入等待 3 秒、私聊等待 1.5 秒。

- 单次准入抽样；拒绝/冷却期间的连续输入不会重新抽样。
- 每人独立静默窗口、显式请求优先、有界队列；其他人的输入不会无限推迟已接纳请求。
- 12 秒文本草稿、至多一次立即重算；首发前检查时效，已开始发送或执行的动作不重放。
- 最后一段确认完成后开始冷却；部分、失败或未知发送结果不计作完整完成。
- 运行时开关迁移区分普通请求、显式请求、已接纳结果，保留必要的恰好一次交接。
- Stop 同时覆盖队列及暂时脱离队列的交接所有权；取消回调在锁外执行。

参数范围、256 槽上限、溢出提示、平台提交边界和配置升级见[功能说明](<reply-pacing.md>)。配置字段 148–152、物理数据库 schema 14；升级保持原字段模式并具备原子性。不要把旧二进制直接用于新 schema 数据库。

## 已执行证据

以下原生运行绑定第三版产品候选，所有命令逐项退出 0：

| 检查 | 结果 | 原始记录 |
| --- | --- | --- |
| 完整解决方案强制重建及 30 次探针调用 | 构建成功，30/30 | 命令、退出码、耗时 (evidence E01) |
| 纯状态 / 真实管线 | 34/34；18 场景、60 断言 | 状态 (evidence E02)、管线 (evidence E03) |
| 模式迁移 / 调度 | 17/17；13/13 | 迁移 (evidence E04)、调度 (evidence E05) |
| 发送 / 生成边界 | 61 项；10/10 | 发送 (evidence E06)、生成 (evidence E07) |
| 既有配置回归 / Engine 回归 | 1679/1679；65/65 | 配置 (evidence E08)、Engine (evidence E09) |
| 架构门禁 | 通过；未上调阈值 | 原始输出 (evidence E10) |
| 旧默认路径 s56 | 主机与 harness 分别强制重建，场景通过 | 命令及二进制哈希 (evidence E11)、场景输出 (evidence E12) |
| 前端 | typecheck、55 文件/644 单测、生产构建均通过 | 类型 (evidence E13)、单测 (evidence E14)、构建 (evidence E15) |
| 真实 Chromium 有头浏览器 | 本地合成宿主：保存、重载、切换、旧配置保留及越界拒绝通过 | 原始输出 (evidence E16)、[测试](<../src/web/tests/e2e/reply-pacing.spec.ts>) |

模式迁移还由作者重复五次，并由独立验收方重新构建、运行。表内第三轮管线为修正前的历史 18 场景/60 断言；最新管线为 18 场景/62 断言，保留全部原断言并新增两项精确截止时刻检查。修正后定向 100 轮、完整 50 轮通过，独立验收方又强制构建并复跑定向及完整场景；[定时同步诊断](<../tests/BotAgent.ReplyPipelinePacingProbe/SOFT-DEADLINE-DIAGNOSIS.md>)保留计数、日志、指纹和原始反例。不同探针的计数单位不同，不合并成一个夸大的总数。

## 独立反例闭环

验收方保留原始失败输入和断言，修复后原样复跑，不以改断言代替修复：

1. 延后入口在 Stop 后重新捕获新 epoch：入口传递原 epoch 并在 pacing 锁内复查，最终 readmitted=0。
   原探针 (evidence E17) · RED (evidence E18) · GREEN (evidence E19)。
   原探针 SHA-256：`c1096162599d838f2fc483af7d3fae8e83e2aa6abd1e72b0084dab76b06e0902`。
2. 开关交接暂时脱离队列，Stop 漏掉所有权：统一 pacing → deferred → pending 原子边界，退役已有弱引用所有权；最终 pacedSurvivors=0。
   原探针 (evidence E20) · RED (evidence E21) · GREEN (evidence E22)。
   原探针 SHA-256：`3da12ff643e018c4a094f695441b4d1ab8cfdb9dc319042d662a62e26f1ef329`。

独立复核前后源码指纹 (evidence E23)一致；复跑脚本 (evidence E24)保留。复核范围内没有未解决问题；这不是对所有可能交错的穷尽证明。

## 测试夹具修复与快照收口

- 源链接 transport 探针增加真实 Core 引用，恢复整体构建；没有修改产品行为。
- 配置回归严格计数随五个新字段同步，schema 13→14；原子回滚断言完整保留，并收紧为 SQLite 扩展错误码 1811。[修复证据](<../tests/BotAgent.ReviewRemediationProbe/CONFIG_REGRESSION_REPAIR.md>)。
- 反射夹具适配新的工作对象与请求意图参数；仍检查旧队列不能占用新会话，且加强空工作对象断言。修复证据 (evidence E25)。
- 合成数据库产物保留在原处，仅从候选源码集合排除；没有读取或删除业务数据。
- 第三轮 30 项调用全部通过；该轮总包装器退出 1，仅因为并行验收说明在运行中新增。其完整差异 (evidence E26)只有一份说明文档，产品与测试代码无漂移。原记录保留，不将退出 1 写成退出 0。

- 第四轮无源码漂移，但原始管线记录 (evidence E27)出现一次等待退出超时。确定性暂停定时器注册后可重现：资源已释放，旧断言误把无关工作定时器当成静默等待已注册；虚拟时间先到终点，真正的 50ms 等待随后才登记。改为等待指定定时器注册，在 14998ms 检查仍等待，在 14999ms 完成，不延长超时、不越过截止点、不修改产品代码。该场景本就要求无动作、无发送，不能把 sends=0 解读成漏发。原反例及三次受控 GREEN 都保留在上述诊断记录。

- 第五轮传输探针在已观察到断连后再次获取流，触发测试夹具的状态约束；没有修改产品。修复为连接成功时保留同一流，原断言、期限、异常过滤全部保留。受控断连重现原异常，同一重放程序保留流后 5 轮通过；关闭语义负对照仍拒绝超时并传播本地释放异常。强制构建成功，20 次完整原生运行均为 44 项通过；详见[传输诊断与原始证据索引](<../tests/BotAgent.ReverseTransportProbe/README.md>)。

- 第五轮的文档检查先于三个证据文件生成，因提前链接尚未存在的输出而失败；原始记录 (evidence E28)及原报告 (evidence E29)保留。将待生成路径写在下列命令块中，避免文档检查与其自身输出形成循环依赖；不修改或放宽架构检查。同时确认干净检出不会带有本地证据：四份报告的 75 个本地证据超链接已转换成带标签的代码块索引，仓库源码链接和原始记录保持不变；所有非忽略 Markdown 不再链接本地临时产物。

本报告先定稿，再运行下列完整冻结复跑。最终收口要求全部命令退出 0，候选差异为空；源码、测试和本报告均纳入运行前后逐文件内容哈希。下列文件在相应阶段才生成，完成之前不能当作通过证据：

```bash
python .tmp/run-final-validation.py .tmp/final-verification-6
python .tmp/hash-candidate.py .tmp/final-verification-6/candidate-final.json
python .tmp/summarize-final-validation.py .tmp/final-verification-6
# 验收：results.json 全部 exitCode=0；candidate-drift.json=[]
# final-inspection.json: passed=true，运行后无漂移，src/ 与第五轮辅助检查一致
# .tmp/final-verification-6/results.json
# .tmp/final-verification-6/candidate-before.json
# .tmp/final-verification-6/candidate-after.json
# .tmp/final-verification-6/candidate-drift.json
```

命令会保存逐文件 SHA-256、原生命令/退出码/耗时与原始日志；此前所有失败记录保留。文件工具保留修改时间，因此所有 .NET 验证均强制 `-t:Rebuild`，且运行对应 artifacts-path 中的 DLL；不能仅凭时间戳或旧二进制下结论。

最新辅助检查来自第五轮（产品代码与最终候选相同），默认路径、前端与真实本地浏览器均已通过，记录为：主机/harness/s56 (evidence E30)、类型检查 (evidence E31)、644 单测 (evidence E32)、前端构建 (evidence E33)、浏览器 (evidence E34)。这些检查也必须实际成功，不能仅凭本段列出了路径就宣称通过。

## 明确保留的限制

- 只使用合成数据、隔离本地宿主及本地浏览器；未验证线上账号收发，未进行人工视觉签收。
- 已独立接纳的后台任务保持既有生命周期，局部聊天 Stop 不改写其取消/恢复机制；全局停机、来源与权限检查仍保留。
- 本地实现验收不等于部署授权。上线、合并、公开推送及开启功能需要另行决定。
- 原始日志和二进制位于忽略的临时证据目录；应保留在本机，不自动纳入公开快照。

## Local evidence index

Machine-local, ignored evidence is not part of a clean checkout. Labels above map to repository-root-relative paths below; original logs and hashes are unchanged. These are evidence locations, not a claim that a clean checkout contains the artifacts.

```text
E01  .tmp/final-verification-3/results.json
E02  .tmp/final-verification-3/ReplyPacingProbe.log
E03  .tmp/final-verification-3/ReplyPipelinePacingProbe.log
E04  .tmp/final-verification-3/ReplyModeTransitionProbe.log
E05  .tmp/final-verification-3/ReplySchedulerProbe.log
E06  .tmp/final-verification-3/ReplyDeliveryPacingProbe.log
E07  .tmp/final-verification-3/ReplyGenerationPacingProbe.log
E08  .tmp/final-verification-3/ReviewRemediationProbe.log
E09  .tmp/final-verification-3/EngineProbe.log
E10  .tmp/final-verification-3/ArchitectureProbe.log
E11  .tmp/final-verification-3/compatibility-results.json
E12  .tmp/final-verification-3/integration-s56.log
E13  .tmp/final-3-frontend-typecheck.log
E14  .tmp/final-3-frontend-unit.log
E15  .tmp/final-3-frontend-build.log
E16  .tmp/browser-final-3.log
E17  .tmp/pacing-final-review-artifacts/epoch-race/Program.cs
E18  .tmp/pacing-final-review-artifacts/epoch-red.log
E19  .tmp/pacing-final-review-artifacts/fence-epoch.log
E20  .tmp/pacing-final-review-artifacts/stop-transfer/Program.cs
E21  .tmp/pacing-final-review-artifacts/transfer-bounded-red.log
E22  .tmp/pacing-final-review-artifacts/fence-transfer.log
E23  .tmp/pacing-final-review-artifacts/fence-fingerprints-after.txt
E24  .tmp/pacing-final-review-artifacts/fence-revalidate.sh
E25  .tmp/engine-regression-repair/evidence.md
E26  .tmp/final-verification-3/candidate-drift.json
E27  .tmp/final-verification-4/ReplyPipelinePacingProbe.log
E28  .tmp/final-verification-5/ArchitectureProbe.log
E29  .tmp/final-verification-5/acceptance-report-before-link-fix.md
E30  .tmp/final-verification-5/compatibility-results.json
E31  .tmp/final-5-frontend-typecheck.log
E32  .tmp/final-5-frontend-unit.log
E33  .tmp/final-5-frontend-build.log
E34  .tmp/browser-final-5.log
```
