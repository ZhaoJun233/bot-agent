# 大文件治理标准

本文件给出**判断一个源文件该不该拆、拆到什么程度**的统一口径，供代码评审、架构探针与重构条目引用。
标准来自重构计划的既定结论，此处只做固化与补充判定细则，**不新增约束**。

---

## 1. 行数阈值

| 行数 | 结论 | 动作 |
| --- | --- | --- |
| ≤ 300 | 无需拆分 | 不动 |
| 300–500 | **检查职责** | 先判断是否承担了多个职责；单一职责就保留 |
| 500–1000 | **评估职责** | 必须写出职责清单，判断能否按职责边界拆开 |
| > 1000 | **优先专项审查** | 进入重构条目，先产出决策文档（`needs-design`）再动代码 |

**阈值是触发线，不是拆分指标**。行数少不等于设计好，行数多不等于必须拆。

---

## 2. 可例外的情况

以下类型的文件**可以超过阈值**，前提是职责确实单一：

| 类型 | 理由 |
| --- | --- |
| 数据定义（常量表、字段清单、枚举映射、schema 字面量） | 拆开只会让引用方到处跳转，降低可读性 |
| 生成代码 | 人工拆分会与生成器冲突 |
| 部分测试文件（用例表、断言集合） | 用例的线性堆叠是合理的；拆成多文件反而丢失整体视图 |
| 路由表 / 注册表（表驱动） | 一行一条，本身就是清单 |

例外必须**在文件顶部注释里写明**为什么可以例外，否则按上表处理。

---

## 3. 当前实测分布（2026-10-31）

`src/**/*.cs` 共 **426** 个文件、**70 008** 行；`tests/**/*.cs` 共 **109** 个文件、**44 328** 行。

| 区间 | `src` | `tests` |
| --- | --- | --- |
| > 1000 行 | **8** | **7** |
| 500–1000 行 | **20** | **15** |
| 300–500 行 | **29** | **22** |
| ≤ 300 行 | 369 | 65 |

超过 1000 行的 `src` 文件（按行数降序）：

| 文件 | 行数 | 对应条目 |
| --- | --- | --- |
| `src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs` | **1727** | #107 |
| `src/BotAgent.Engine/Reply/ReplyPipeline.cs` | **1659** | #106 |
| `src/BotAgent.Platforms/OneBot/OneBotGateway.cs` | **1606** | #108 |
| `src/BotAgent.Storage/ConfigurationStore.cs` | **1552** | #107 |
| `src/BotAgent.Model/OpenAiClient.cs` | **1197** | 未单列条目，随 #102 升级一并审查 |
| `src/BotAgent.Platforms/Official/OfficialBotGateway.cs` | **1155** | 未单列条目，与 #108 同属平台层 |
| `src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs` | **1127** | #109 |
| `src/BotAgent.Headless/Host/CompositionRoot.cs` | **1090** | #110 |

超过 1000 行的 `tests` 文件（按行数降序）：

| 文件 | 行数 | 说明 |
| --- | --- | --- |
| `tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs` | **5153** | 契约用例集合，属"部分测试文件"例外，但需在顶部注释声明 |
| `tests/BotAgent.SafetyProbe/Program.cs` | **2876** | 安全断言集合 |
| `tests/BotAgent.IntegrationHarness/Program.cs` | **2427** | 场景注册表，属"路由表"例外 |
| `tests/BotAgent.ZbaProbe/Program.cs` | **1359** | ZBA 契约断言集合 |
| `tests/BotAgent.ArchitectureProbe/Program.cs` | **1244** | 扫描器 + 断言 |
| `tests/BotAgent.ReviewRemediationProbe/EngineWiringTests.cs` | **1088** | 装配用例集合 |
| `tests/BotAgent.McpProbe/Program.cs` | **1059** | MCP 断言集合 |

---

## 4. 判定流程

对一个超过阈值的文件，按顺序回答四个问题：

1. **它有几个职责？** 列出职责清单。只有 1 个 → 保留并在顶部注释说明；≥ 2 个 → 进入第 2 步。
2. **职责之间是否有清晰边界？** 有（可命名、可独立测试、调用方不需要同时持有两者）→ 可拆；
   没有（共享大量状态、拆分后要暴露内部字段）→ 不拆，改为在文件内部做职责分区。
3. **拆分是否改变事务语义或公共 API？** 改变 → 必须写决策文档并给出行为等价证据；
   不改变 → 正常评审即可。
4. **拆完是否破架构棘轮？** 见 §5。

---

## 5. 与架构棘轮的配合

`tests/BotAgent.ArchitectureProbe/Baseline.cs` 维护一组**棘轮**：阈值只允许调低，不允许调高（调高等于放宽约束）。

与拆分直接相关的两条：

| 指标 | 当前基线 | 断言上限 |
| --- | --- | --- |
| `MaxFileLines`（全 `src` 最大文件行数） | 1740 | ≤ 1765 |
| `DomainFileTargetLines`（领域层单文件目标行数） | — | ≤ 300 |

规则：

- 拆分**不得**让 `MaxFileLines` 上升；拆完变小了，用 `--print` 拿到新数字**调低**基线。
- 新文件落在领域层（Domain）时必须满足 `DomainFileTargetLines ≤ 300`。
- `DomainTypeMaxFields ≤ 10`、`DomainTypeMaxMethods ≤ 25`：拆分不是把大类型换成一堆大类型。

---

## 6. 明确反对的做法

| 做法 | 为什么反对 |
| --- | --- |
| `partial class` 机械切文件 | 只搬行数不改职责，编译单元变多而耦合不变，评审成本上升、可读性下降 |
| 为减行数而拆 | 行数不是目标，职责边界才是；拆完调用链更长就是负收益 |
| 一次性重写核心模块 | 无法证明行为等价，回滚代价不可控；必须渐进 |
| 在重构 PR 里修改断言 | 断言被改过，"行为等价"就失去了证据；断言变更必须独立提交并单独评审 |
| 拆完不写职责清单 | 下一轮评审无法判断新边界是否成立 |

---

## 7. 重构 PR 的验收要求

1. 附**职责清单**：拆前有几个职责、拆后每个文件承担哪一个。
2. 附**行为等价证据**：重构前后同一组 harness 场景的输出对比（场景 ID + 通过数 + 差异项说明）。
3. **禁止在重构 PR 内修改任何断言**；确需修改的，拆成独立提交并单独说明理由。
4. 事务边界（#107）与状态模型（#114）额外走一次独立验收。

---

## 8. 相关文档

- [基线报告](../baseline/report-2026-10-31.md) —— §5 是棘轮现状的原始数据
- `tests/BotAgent.ArchitectureProbe/Baseline.cs` —— 棘轮定义的唯一真源
