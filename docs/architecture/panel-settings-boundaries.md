# 面板设置保存与回读的职责边界（#109）

面板的"配置"这条链路有三件事容易混在一起：**把请求翻译成意图**、**把意图落成事务**、**把库里的值投影回面板**。
拆分前它们都挤在 `src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs`（1127 行）里，旁边还贴着
HTTP 状态码映射、结构化平台策略装配和 secret 内存归属。加一个标量字段要在同一文件里改三处，
读代码时很难判断"我改的这段属于哪层"。

## 文件地图

| 文件 | 行数 | 职责 | 加一个标量字段要动 | 说明 |
| --- | --- | --- | --- | --- |
| `WebUiServer.Settings.cs` | 153 | 薄 HTTP：入站许可（4 槽）→ 读体（2 MiB / 深度 64）→ 旧/新两条保存路径 → 状态码与错误回包 | 否 | 只做"请求进、回包出"，不含字段知识；两个反射接缝的转发壳也在这里 |
| `PanelConfigurationRequestValidation.cs` | 157 | 新（ZBA）保存请求的装配与形状校验：成员白名单、重复键、类型/长度/枚举、Unicode 边界、secrets 数组 ≤6 且槽位不重复 | 否 | `ConfigurationJson`（camelCase + 拒绝未知成员）的唯一所有者 |
| `PanelLegacyConfiguration.cs` | 85 | 旧面板"整批意图"的编排：结构化策略优先装配 → 标量（从副本里剥掉 `platformPolicies`）→ secret 计划 | 否 | 任何一步不通过就**整批拒绝**，不存在半接受 |
| `PanelLegacyScalarFields.cs` | 213 | 标量输入 → `ConfigurationPatch` 的七个分组（行为 / 模型 / Agent / 平台 / 语音 / 音乐 / 上报） | **是（一行）** | 探针按 134 条扁平键校验这层 |
| `PanelLegacyFieldEdits.cs` | 93 | 字段读取原语：`FieldEdit<T>` 的 Keep/Set 语义、截断、思考档位、URL 校验、名单/时钟/QQ 动作 | 否 | 13 类字段的输入规则只在这里定义一次 |
| `PanelLegacyPolicies.cs` | 75 | 结构化平台策略装配：独立反序列化 + 目标/名单/覆盖校验，不走文本字段 | 否 | 策略永远不能被扁平化成文本 |
| `PanelLegacyRemovedFields.cs` | 36 | 已移除字段的整批拒绝表（cfg-0.3.3 删掉的 host/device 编辑器） | 否 | 命中任一键 → 整批 `host_backend_removed` |
| `PanelLegacySecrets.cs` | 135 | secret 计划：槽位、清空语义、UTF-8 16 KiB 上限、owned 内存清零 | 否 | `LegacySecretPlan` 是唯一所有者，`Dispose` 负责清零 |
| `WebUiServer.SettingsProjection.cs` | 384 | 回读投影：`runtime` / `env` / `settingsFile` 三段、TTS 文件投影、密钥掩码、参与策略摘要 | **是（一行）** | 与 `GET /api/settings`、`GET /api/env` 和 TTS 容器文件绑定 |

## 加一个标量字段要改哪里

1. `PanelLegacyScalarFields.cs` 对应分组里加一行 `Xxx = LegacyField(body, "xxx", node => …)`；
2. `WebUiServer.SettingsProjection.cs` 的 `BuildRuntimePayload` 里加一行回读（面板才看得到保存后的值）。

其余七个文件不需要动。字段如果同时走新 ZBA 契约，还要在 `ConfigurationPatch` 的定义处加属性 —— 那不属于本目录。

## 保留的不变量（拆分没有动）

1. **整批拒绝的原子性**：已移除字段命中、策略非法、secret 非法都返回"整体拒绝"，不会出现"一部分生效"。
2. **结构化策略走独立装配**：`platformPolicies` 从不作为文本字段读，装配后从标量副本里剔除，避免同一字段两条来源。
3. **secret 归属与释放**：`SensitiveInput` 由 `LegacySecretPlan` 独占，事务提交/放弃后 `Dispose` 清零；错误路径返回空计划，不泄漏句柄。
4. **旧/新两条保存路径的并发语义不同**：旧路径（`POST /api/settings`）是 last-write-wins，新 ZBA 路径带 `expectedVersion` 的 CAS（陈旧版本 409）。
5. **HTTP 预算**：解析并发 4 槽、请求体 2 MiB、JSON 深度 64，超限分别映射 429 / 413。
6. **状态码映射**：`ConfigurationResultCode` → HTTP 的关系只在 `WebUiServer.Settings.cs` 的 `ConfigurationHttpStatus` 一处定义。
7. **两条反射接缝**：`WebUiServer.ParseLegacyScalarFields` 与 `WebUiServer.ParseLegacyConfiguration` 保留同名同签名的转发壳
   （`tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs` 用 `GetMethod` 调它们）。

## 风险

1. **只是搬代码，等价性靠回归**：本轮没有新增断言，等价证据是集成场景（旧面板保存/回读、ZBA 保存、备份恢复、平台策略）
   与 `ReviewRemediationProbe` 的 1173 条配置契约检查。
2. **反射接缝是已知耦合**：两个转发壳不能删；要删得先改探针（属于 #107 之后的重构债）。
3. **投影层仍是 partial 类的一部分**：回读投影要读 `SettingsBox` 当前值、secret 库和 TTS 文件路径，
   抽成独立对象会引入新的生命周期问题，本轮刻意保留为同类的另一个 partial 文件。
