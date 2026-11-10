# 模型配置的生效路径（#103 / #128 / #132 / #134）

回答一个问题：**在面板里改一个模型配置，什么时候立刻生效，什么时候必须重启？**
这份文件是那次 P1 故障（「改了模型配置但实际调用还是旧的」）的结论沉淀，
改动这条链路前请先读一遍这里的判定规则。

---

## 1. 三段链路

```
面板/ZBA 提交  →  持久化（一个事务）  →  回读投影 + 候选表热替换  →  运行期每请求解析路由
                  settings +              ProviderConfigurationView     TryBuildCurrentProviderRoute
                  model_providers +       + ModelProviderRegistry       + ResolveProviderSecret*
                  secrets                 （#128：整体换候选表）
```

| 段 | 代码位置 | 说明 |
| --- | --- | --- |
| 持久化 | `src/BotAgent.Storage/ConfigurationStore.cs` | Read/Save/Find 由唯一配置事务协调者处理；同一事务写配置、秘密、版本、审计与回执；密钥正文不进回执 |
| 回读 | `SettingsStore.ProjectRegisteredProviderViews`（`src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs`）与运行效果投影 | 供应商字段声明为热应用；显示值与实际运行状态分别投影，热应用声明不等于本次应用已成功 |
| 候选表 | `CompositionRoot.InspectModelProjection`（`CompositionRoot.Model.cs:199`）+ `ModelProviderRegistry.Replace`（`Host/ModelProviderRegistry.cs`）+ `ProviderFailoverRunner.ReplaceCandidates`（`src/BotAgent.Model/Resilience/ProviderCircuitBreaker.cs:298`） | 保存配置时读一次新候选表并**整体替换**（#128）；执行器只造一次，熔断器按供应商 id 复用 |
| 运行期 | `src/BotAgent.Headless/Host/CompositionRoot.Model.cs:289` `TryBuildCurrentProviderRoute` | **每次真正发请求前**打开数据库读一次 provider 行，现取密钥、现拼路由 |
| 发现模型 | `CompositionRoot.TryBuildProviderTestRoute`（`CompositionRoot.Model.cs:258`） | 与运行期共用 `ProviderRouteBoundary`（#132）：面板能测通的端点，线上也一定接受；只多放行「未启用」这一条 |
| 发送 | `src/BotAgent.Model/OpenAiClient.cs:278` `SendThroughProvidersAsync` | 拿不到路由就 `provider_secret_unavailable`，按设计**不降级到旧配置** |

关键点：**没有「已建好的模型客户端」这层缓存**。路由是每次请求现拼的

---

## 2. 判定规则：请求级 vs 装配级

| 字段 | 级别 | 为什么 |
| --- | --- | --- |
| API Key / 凭据来源（`env:QQCHAT_API_KEY` ↔ `secret:apiKey` ↔ `model:<key>`） | **请求级 · 热** | 密钥每请求现取；换来源不改变任何结构 |
| Base URL、模型名（含 primary 的标量覆盖） | **请求级 · 热** | 只是拼 URL 与请求体 |
| 采样参数、思考档位、快速回复档位 | **请求级 · 热** | 请求体字段 |
| 协议（`ModelProtocol`）、`Options` | **候选表热重建** | 保存后模型投影步重建候选；运行请求仍核对当前行与已采用快照 |
| 供应商集合、顺序（`Priority`）、启用位、显示名 | **请求级 · 热**（#128） | 候选表整体热替换；熔断状态不在表里，按 id 复用 |

判定的落点：

- `CompositionRoot.InspectModelProjection`（`src/BotAgent.Headless/Host/CompositionRoot.Model.cs`）核对已提交版本，读取供应商并调用 `registry.Replace`；重建成功报告 `Applied`，失败报告 `Failed + model_projection_rebuild_failed`；
- `CompositionRoot.Surface.cs` 的 `HotReload.BindConfiguration` 把这一步接入实际保存链；
- `SettingsHotReload.RequiresRestart`（`src/BotAgent.Headless/Services/Settings/SettingsHotReload.cs`）明确排除供应商启用、优先级、地址、模型、协议、选项与密钥来源；其他装配级字段仍遵循各自 FieldEffect。

> **曾经的错误**：凭据来源被当成装配级。面板第一次填 API Key 时 `SecretKeyRef` 从
> `env:QQCHAT_API_KEY` 变成 `secret:apiKey`，运行期比对不相等 → 路由被拒 → **所有模型调用安全静默**，
> 机器人一个字都不回。表现是「保存成功了、回读也是新值，但就是没反应」。
> 更糟的是当时有测试把这段静默当成期望行为断言，等于把 bug 固化成规范。
### 2.1 候选表为什么现在能热换（#128）

以前的死胡同：启动时把库里的供应商行抄进 `capturedProviders`，**全进程只有那一次**。
面板再改只会让 `InspectModelProjection` 报 `RestartRequired`，而运行期
`TryBuildCurrentProviderRoute` 又拿 `desired` 去跟这份启动快照比，不等就返回 null →
路由解析拒绝 → `provider_secret_unavailable` → 安全静默。于是「保存成功、回执说要重启、
机器人不回复」，而且**根本没有重建入口**（首版不提供远程重启按钮）。

现在这条路是通的：

1. `ModelProviderRegistry`（`src/BotAgent.Headless/Host/ModelProviderRegistry.cs`）持有
   `volatile IReadOnlyList<PersistedModelProvider> Captured` 与唯一一次挂上的 `ProviderFailoverRunner`；
2. `InspectModelProjection` 每次保存先在一个只读事务里核对期望版本并读出 `desired`，
   提交后调 `Replace(desired, candidates)`：**先** `ReplaceCandidates` **再**换 `Captured`；
3. `ReplaceCandidates` 复用同名断路器（冷却中的供应商不会因为改配置被放出来），
   只为增删的 id 增删断路器 —— 熔断历史仍在库里，不随候选表一起丢；
4. 换失败时记录警告并报告 `Failed + model_projection_rebuild_failed`。旧候选快照保留，但库内已提交配置可能与之不同；请求边界可拒绝不一致的路由，不能承诺旧表继续回复，也不能承诺重启必然解决。

不变式（改这里必须守住）：

- 执行器只造一次，候选表只能经 `Replace` 换；不允许第二处抄快照；
- `Replace` 内先换执行器候选再换 `Captured`；配置提交与运行应用是两步，应用中或失败后允许出现已保存值与旧运行快照，必须用回执及运行效果说明当前状态；
- 供应商 id 相同的断路器必须复用；只有 id 消失时才丢弃。

### 2.2 发现模型与真实调用共用同一条硬边界（#132）

以前「面板里点发现模型」走的是另一条路：只读库、每次现开事务，**不比对装配快照**。
于是会出现最难解释的一类现象 —— 面板测通、线上拒绝，日志里也看不出两条路差在哪。

现在两边都过 `CompositionRoot.ProviderRouteBoundary`（`CompositionRoot.Model.cs:244`），
返回同一种原因码：

| 原因码 | 运行期路由 | 发现模型 |
| --- | --- | --- |
| `provider_id_not_trimmed` / `provider_protocol_changed` / `provider_options_changed` / `provider_endpoint_changed` | 拒绝 | 拒绝 |
| `provider_disabled` | 拒绝 | **放行**（保存前先试连未启用的供应商，这是唯一更宽的一处） |
| `provider_missing` | 拒绝 | 拒绝 |

拒绝时两条通道都写日志：运行期 `provider_route_blocked`、测试路由 `provider_test_route_blocked`，
都带 provider id 与 reason。判定表由探针 `ProviderRoutingBoundaryTests` 直接钉住（反射调私有方法），
防止两边再次分叉。

---

## 3. 失败必须可见，不能静默

- 路由解析不是失败关闭就是成功，**没有「悄悄用旧配置」这条路径**；
  但「失败关闭」必须让运维看得见：
  - 日志 `Provider 调用不可用，进入安全静默（reason=...，hops=...）`；
  - 辅助通道（`//` 任务）同一口径（#132）：`辅助模型调用不可用，进入安全静默（reason=provider_secret_unavailable）`
    —— 以前这里直接 `return null`、一行日志都没有，表现为「任务哑了、日志干净」；
  - 错误码 `provider_secret_unavailable` / `provider_route_unavailable` / `endpoint_authorization_required`
    在前端 `src/web/src/shared/friendly.ts` 有中文文案；
  - 保存回执的 `ModelProjection` 步骤如实报 `Applied` 或 `Failed`；重建失败使用 `model_projection_rebuild_failed`，不能按已应用处理；
  - 前端回执按步骤区分已提交、已应用、失败与未知；模型投影失败显示“已保存，运行应用失败；当前状态待核对”，并提供原键核对动作（`src/web/src/features/configuration/receipt-notice.ts` 与 `runtime-effects.ts`）。
- 端点授权在**保存时**校验（`IsAuthorizedModelEndpoint`），运行期只需能解析出密钥 ——
  所以放宽运行期的凭据来源比较不会把流量引到未授权端点。

---

### 3.1 首次配置与结果未知时怎样核对

1. 打开独立部署前端的 `/web/`，以管理员身份进入能力设置中的模型配置；基础配置页不提供模型与秘密的第二套写入口。
2. 只编辑所需供应商/模型/凭据字段并保存。分别查看配置版本、持久化回执和运行效果；“已保存”只证明提交，不单独证明后续请求已采用新路由。
3. 应用成功后，在授权的测试场景确认后续请求使用期望路由；没有回复时结合连接状态、应用步骤与固定原因码核对，不通过读取群聊正文排查。
4. 响应断开、应用失败或未知时，先保留原操作关联并使用“核对这次提交”。未知结论未解除前，不换键重存同一意图；普通配置回读不能替代原操作回执。
5. `model_projection_rebuild_failed` 表示提交后重建失败：核对当前回执/运行状态，再由维护者处理具体原因；不能建议自动重复保存或把重启当成保证修复。
6. 页面切换、锁定后同身份重连仍可核对内存中的原关联；整页刷新或关浏览器会丢失该页面关联，当前不承诺跨刷新自动恢复。原键已知时按对应身份查询，原键丢失时先人工核对，不能从普通回读推断原操作未执行。

## 4. 两条模型通道（#134 起共用同一个熔断执行器）

| 通道 / 情形 | 路由来源 | 熔断 |
| --- | --- | --- |
| 聊天回复 | 注册表候选表，`ProviderFailoverRunner.RunAsync` | 有，状态落库 |
| `//` 任务 · 没有任何覆盖 | 同一个注册表，同一条 `RunAsync` | 有，状态落库 |
| `//` 任务 · 只给一半覆盖（只改地址或只改密钥） | 单候选辅助上游，`RunAuxiliaryAsync`，合成 id `agent-server` | 有，状态**不落库** |
| `//` 任务 · baseUrl + apiKey 成对 | 直接调用，不查注册表 | 无（唯一豁免） |

- 只给一半覆盖为什么不并进注册表：`tests/BotAgent.IntegrationHarness/AgentServerKeyScenario.cs`（S37）钉住
  「只改地址、密钥沿用聊天那把」这个语义；并进注册表会让 `QQCHAT_AGENT_SERVER_URL` 指向的自定义上游失效、
  请求悄悄跳到注册表端点，属可见回归。改成「单候选辅助上游」既保语义，又让坏端点按熔断快速失败、
  不再按调用次数硬超时（`src/BotAgent.Model/OpenAiClient.cs` 的 `CompleteAuxiliaryAsync`）。
- 失败语义两条通道一致：`provider_circuit_open` / `provider_timeout` / `provider_exception` /
  `provider_secret_unavailable` / `chat_completion_failed`，日志同一句「…进入安全静默（reason=…，hops=…）」。
- 成对覆盖是唯一允许完全绕开注册表与熔断器的路径（测试替身 / 特殊网关），判定在 `HasExplicitUpstream`；
  `tests/BotAgent.ProtocolProbe/ReviewModelRegression.cs` 的 B35 断言钉住这几条语义。
- 合成身份不落库的取舍：`ModelProviderStore.SaveCircuit` 只 UPDATE 已注册行（未知 id 会抛异常），
  所以 `agent-server` 的熔断状态在进程重启后清零 —— 这是「配置派生身份」的已知代价，不是遗漏。

---

## 5. 改这条链路时的检查单

1. 新的配置字段先判定「请求级还是装配级」，再决定放进哪张表 / 哪个判定。
2. 改 `InspectModelProjection` 必须同步改 `ProjectRegisteredProviderViews` 与 `RequiresRestart`，三处口径要一致。
3. 回归至少要有三阶段：**保存 → 回读 → 重启后**（`tests/BotAgent.IntegrationHarness/ModelConfigScenario.cs`）。
4. 不要写「保存后必须重启才能用」这类断言，除非那一步确实是装配级。
5. 面板改动必跑 `cd src/web && npm run typecheck && npm run test:unit`。
6. 动候选表（增删供应商 / 调优先级 / 启停）回归 `BOTAGENT_IT_ONLY=s61`：它钉住「不重启就换路由」与「删除后回到 primary」。
7. 不要把断路器状态放进候选表一起换 —— 那等于让改配置顺手清掉熔断。
8. 改 `ProviderRouteBoundary` 等于同时改「运行期身份」与「面板发现」，回归要同时跑
   `BOTAGENT_IT_ONLY=s21`（运行期）与面板里的供应商测连；探针 `ProviderRoutingBoundaryTests` 会拦判定表漂移。
9. 改 `//` 任务的 override 语义（`CompleteChatWithProviderFailoverAsync` / `HasExplicitUpstream`）必须跑
   `BOTAGENT_IT_ONLY=s37` 与 `dotnet tests/BotAgent.ProtocolProbe/...dll`：S37 钉「只改地址、密钥沿用聊天」，
   B35 钉「一半覆盖经熔断器、成对覆盖才绕开」。
10. 辅助通道的静默日志不要删：`tests/BotAgent.ProtocolProbe` 的 B23 用 `ModelLogging.WarnHandler` 捕获它，
   删掉会红（这正是「任务哑了、日志干净」的回归护栏）。
