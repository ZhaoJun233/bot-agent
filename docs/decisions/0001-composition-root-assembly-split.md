# ADR-0001：装配根按能力域拆分（#110）

- 状态：已接受（2026-11-02）。实施分支 `refactor/composition-root-split`，父条目 #100。
- 影响面：`src/BotAgent.Headless/Host/CompositionRoot.cs`（拆分前 1223 行）、`tests/BotAgent.SsrfProbe`、`tests/BotAgent.ReviewRemediationProbe` 的源码指纹清单。
- 相关：#106（回复主链拆分）、#109（面板设置分层）、#128（模型供应商注册表）、#133（库 schema v11）。同类拆分的先例见 `docs/architecture/panel-settings-boundaries.md` 与 `docs/architecture/onebot-gateway-boundaries.md`。

## 1. 背景

`CompositionRoot` 是 Headless 唯一的装配根，也是全仓唯一允许 `new` 具体 IO 组件的地方（ArchitectureProbe 的 `ConcreteIoNews == 0` 就是这么守住的）。拆分前它在**一个文件、一个 `Build` 方法**里完成了全部装配：

| 事实 | 拆分前 |
| --- | --- |
| 文件行数 | 1223 行（单文件） |
| `Build` 方法 | 约 200 行（ArchitectureProbe 的 `LongestMethodAnywhere` 上限 198，已贴着棘轮线） |
| 局部变量 | 40 余个，按书写顺序隐式表达装配顺序 |
| 装配域 | 配置发布点、通道、存储、模型、工具/MCP、后台队列、回复链、面板与 ZBA 全部混在一条直线里 |

由此带来三个具体问题：

1. **装配顺序只存在于阅读顺序里。** 「插件门禁必须先于 agent 命令与回复链」「配置发布点必须是第一个」「`settingsHotReload` 必须在 `web` 之前」这类约束没有名字，改动时只能靠通读 200 行推断。
2. **任何一处改动都要碰同一个文件。** #128 把 `ModelProviderRegistry` 塞进来、#133 加了库 schema 迁移后的初始化，都在同一个 200 行函数上叠加，冲突面最大。
3. **行数棘轮贴线。** 每加一层装配就要在 `Build` 里找位置，方法长度与文件长度都在上限边缘。

## 2. 决策

分两步拆，**两步都可以独立合入、独立回滚**：

| 步骤 | 做什么 | 行为变化 | 回滚 |
| --- | --- | --- | --- |
| 第 1 步（机械拆分） | 把 `CompositionRoot` 改成 `partial class`，按能力域拆成 8 个文件，**方法体原样搬，`Build` 不动** | 无 | `git revert` 该提交即可；只涉及文件与 `partial` 关键字 |
| 第 2 步（装配分段） | 把 `Build` 的直线代码拆成 7 个 `AssembleXxx` 阶段方法，阶段之间用一个只写一次的装配容器串联 | 无（同样的顺序、同样的实例、同样的图） | `git revert` 该提交；`Build` 回到直线版本，容器类随提交一起消失 |

第 2 步引入的装配容器刻意做成**私有嵌套类 `CompositionRoot.Assembly`**：

- 只在 `Build` 内部可见，进程里没有第二个实例，不构成新的全局状态；
- 每个成员由**唯一一个阶段**写入（写入点可 grep 到），阶段只读自己声明的上游成员；
- `Assembly.Complete()` 在返回 `AppGraph` 之前按依赖顺序逐个判空，漏写会让启动立刻抛 `assembly_incomplete:<stage>:<member>`，而不是让某个下游组件晚几百毫秒才炸；
- 容器保存的是**已构造好的实例**，不是工厂/延迟委托 —— 构造顺序仍然是严格的书写顺序，插件的构造期副作用不会被推迟到第一次使用。

### 2.1 阶段划分与顺序契约

`Build` 拆分后只有 9 行，顺序就是契约：

```
InitializeAssemblyPrerequisites(settings);
var assembly = new Assembly(settings);
AssembleCore(assembly);      // 配置发布点、密钥/配置库、供应商注册表、面板通知、配额、插件运行时、配置参与者
AssembleChannels(assembly);  // 协议端/通道层 + 平台策略解析器
AssembleModel(assembly);     // 工具熔断器 + 模型客户端与媒体层（语音/音乐/链接/研究）
AssembleStores(assembly);    // 会话/档案/追踪/审计/MCP/明文发送 + 权限台账/审批/戳一戳
AssembleTools(assembly);     // 插件门禁 + agent 命令 + 后台评估队列
AssembleReply(assembly);     // 回复主链 + 调度器 + 配置热更新扇出 + 删除会话绑定 + 宿主
AssembleSurface(assembly);   // 启动自述、扫码登录、热备、健康日报、面板、托管能力、ZBA
return assembly.Complete();
```

这些先后关系**必须保持**（每条都能说出为什么）：

| 顺序约束 | 为什么 |
| --- | --- |
| 时钟注入 + 平台目录声明先于一切 | `ClockBindings.Initialize*` 是静态落地，晚于任何 `Clock.UtcNow` 调用会抛未初始化；平台目录决定了后续策略解析与面板的驱动列表 |
| `SettingsBox` 先于所有组件 | 它是配置的唯一发布点，组件持有的是它（读当前值），不是配置快照 |
| 配置参与者与 `ConfigurationBridge.Activate` 先于插件运行时之外的写路径 | 面板保存要靠它做重授权与投影，晚于 `SettingsStore`/`SettingsHotReload` 建好会拿到未激活的运行时 |
| 插件运行时先于插件门禁，门禁先于 agent 命令与回复链 | 显式 QQ 动作、`//` 命令、回复链的插件判定必须吃同一份注册表快照 |
| 通道层先于存储层与模型层 | 存储/模型的构造参数里带协议端与身份（`SelfId`）|
| 模型层先于工具层 | agent 命令、后台评估、健康日报都直接用 `brain` |
| 工具层先于回复链 | 回复链持有 `agentCmds`、`background`、审批与参与 |
| 回复链先于调度器与热更新扇出 | 热更新要重建回复链的注意力，调度器持有 `reply` |
| 面板与 ZBA 最后 | 它们是「把上面所有件暴露出去」的那一层，`web` 排在 `agent` 之后启动（`Program.cs` 里 `web.Start()` 晚于 `Agent.Start()`，面板晚于协议端可用） |

### 2.2 文件地图（第 1 步的结果）

| 文件 | 内容 | 不变量 |
| --- | --- | --- |
| `CompositionRoot.cs` | `Build` 配方、`BootstrapSettings`、`Dispose*`、`InitializeAssemblyPrerequisites`、`DeclarePlatformCatalog`、`Assembly` 容器 | 永远只放「编排」，不放具体接线细节 |
| `CompositionRoot.Core.cs` | `AssembleCore` + `ReadRegistered` + 配置参与者/配置运行时接线 + `ConfigurationSecretMetadata` | `SettingsBox`/`SecretsStore`/`ModelProviderRegistry` 的唯一构造点 |
| `CompositionRoot.Channels.cs` | `AssembleChannels` + `BuildChannelLayer` + 身份注册/白名单/会话台账 + `DriverCredentialConfigured`/`DriverIdentityMatches`/`ManagedProviderDescriptors` | 协议端与平台策略的唯一构造点 |
| `CompositionRoot.Model.cs` | `AssembleModel` + `BuildModelAndMediaLayer` + 模型路由族（`InspectModelProjection`、`ProviderRouteBoundary`、`TryBuild*ProviderRoute`、`ResolveProviderSecret*`、`BuildProviderRoute`、`BuildCircuitStatusProvider`） | 模型装配细节与「路由硬边界」同处一文件，改一处不用跨文件 |
| `CompositionRoot.Stores.cs` | `AssembleStores` + 会话四元组 + 追踪/审计/MCP/明文发送 + `BuildPokeUseCase`/`BuildApprovalUseCase` | 存储层只在这里被 `new` |
| `CompositionRoot.Tools.cs` | `AssembleTools` + 插件门禁接线 + `AgentSessionStore`/`ServerAgentRunner`/`AgentCommandService`/`BackgroundEvaluatorQueue` | 插件门禁与显式动作的唯一接线点 |
| `CompositionRoot.Reply.cs` | `AssembleReply` + `ReplyPipeline`/`BotScheduler`/`SettingsHotReload` 装配 + `BindConversationDeletion` + `BotAgentHost`/`BootReport` | `ReplyHooks` 的注入点只在两处（回复链、调度器） |
| `CompositionRoot.Surface.cs` | `AssembleSurface` + `CreateLoginQr`/`CreateBackups`/`RecordStartup`/`BuildWebUiServer`/`BuildManagedCapabilityBinding`/`BuildZba` | 面板与 ZBA 的依赖清单只在这里 |

## 3. 不可破坏的语义（与守护它的测试）

拆分的验收标准不是「编译过」，而是下面每条都还在：

| 语义 | 为什么会被拆分破坏 | 守护 |
| --- | --- | --- |
| 装配顺序与启动时序 | 阶段顺序被调换、或某个阶段提前 `new` 了下游 | `Assembly.Complete()` 判空 + 全量集成 harness（含 S55 重启、S60 健康日报、S61 注册表热重建） |
| 配置唯一发布点（`SettingsBox`）+ 不在原对象上就地改 | 阶段之间把 `settings` 传来传去时容易顺手改 | ArchitectureProbe 的 `SettingsOwnerPath` / `SettingsAssignments == 0`、`tests/BotAgent.SettingsScopeProbe` |
| 具体 IO 只在装配根 `new` | 拆出新文件后有人在新文件里 `new FileLog`/`HttpClient` | ArchitectureProbe 的 `ConcreteIoNews == 0` / `HttpClientNews` / `FileIoTotal` |
| 不可信出口的 HTTP 必须关自动重定向、拒私网目标 | 8 个 `HttpFetcher` 构造散到不同文件后容易被漏 | `tests/BotAgent.SsrfProbe`（按 `CompositionRoot*.cs` 扫描全部构造点） |
| 插件门禁先于 agent 命令与回复链 | 门禁在 tools 阶段建、回复链在 reply 阶段用；顺序一换就把未过门禁的动作放出去 | 集成 S14/S32/S40、`PluginPackageProbe`、`PluginMarketProbe` |
| 无队列时必须拒绝启动无界后台作业 | `BackgroundEvaluatorQueue` 的选项在 tools 阶段构造 | `tests/BotAgent.EngineProbe`、`tests/BotAgent.ReviewRemediationProbe/EngineWiringTests.cs` |
| `ReplyHooks` 注入点（日志/自取 SelfId/IsDisposed） | 拆分时容易把回调换成直接依赖 | ArchitectureProbe 的注入点锚点、集成 S1/S5 |
| 模型配置链路（热应用 + 硬边界 + 请求级密钥） | 路由族换文件时容易丢一条边界 | 集成 S21/S61/S62、`ReviewRemediationProbe` 的 `ProviderRoutingBoundaryTests`、`ZbaProbe` |
| 宿主资源释放幂等 | `DisposeCoreAsync` 与装配容器生命周期相关 | `tests/BotAgent.ChaosFaultProbe`、集成 S60、`AppGraph.DisposeAsync` 路径 |
| 启动自述与面板回读的字段来源不变 | 阶段化后容易把某个服务晚建 | 集成 S54/S55、`BootReport` 断言、`ProductionSpecProbe` |

## 4. 分步实施与验证

| 步 | 动作 | 本轮实际执行与结果 |
| --- | --- | --- |
| 0 | 本 ADR 落盘 | 文档链接校验 54 篇 / 398 条 / 0 断 |
| 1 | 机械拆文件（8 个 `partial`），`Build` 原样 | 与第 2 步合并成同一次改动执行 |
| 2 | `Build` 拆 7 阶段 + `Assembly` 容器 | `dotnet build` Headless 0 警告 0 错误；`ArchitectureProbe` 200/0；`ReviewRemediationProbe` 1212/0（含 `ManagedHostCapabilityTests` 真跑 `Build`）；定向场景 S1 33/0、S21 43/0、S54 86/0、S55 72/0、S60 11/0、S61 9/0、S62 6/0 |
| 3 | 更新源码指纹清单与 `SsrfProbe` 扫描范围 | `SsrfProbe` 77/0；30 个工程 `dotnet build` 全部通过；**全量集成 通过 1249，失败 0** |
| 4 | 发布 + 回帖 | `publish-qqchat.py --push`，CI 9 项全绿 |

**修正**：计划里第 1、2 步各一次提交，实际在一次改动里完成（拆文件与阶段化互相牵扯，分开提交的中间态要把 `Build` 写两遍、还得为一个不存在的中间态维护探针）。
因此回滚粒度是**整个提交**：`git revert` 即回到 1223 行的单文件根；第 3 步（探针扫描范围）与它强耦合，必须一起回滚。

## 5. 风险与实际取舍

| 风险 | 处理 |
| --- | --- |
| `Assembly` 变成「什么都能放」的袋子 | 每个成员只有一个写入阶段；阶段只读自己需要的上游；`Complete()` 判空；本 ADR 的职责表是评审依据 |
| 编译期不再能证明「先赋值后使用」 | 用 `= null!;` + `Complete()` 运行时判空兜底，漏写会在启动第一毫秒失败而不是运行时随机失败 |
| 新文件各自 `using` 一大堆 | 每个 `partial` 文件自带头部 `using`，编译器不共享 using；拆完以 0 警告为准 |
| 探针按路径锁源码 | 第 3 步把 `SsrfProbe` 改成扫描 `Host/CompositionRoot*.cs`、把配置捕获清单改成枚举同目录 `CompositionRoot*.cs`，意图不变（仍是「装配根里的这些约束」） |
| 行数棘轮 | 拆分后单文件最长的一段远低于 1740；唯一的风险是 `AssembleXxx` 方法长度，按阶段控制在 40 行内 |

## 6. 不做的事

- **不改任何组件的构造签名**，不引入 DI 容器（不引入 `IServiceProvider`/反射装配）——顺序与依赖仍写在代码里，可 grep、可断点。
- **不合并/搬走** `BuildChannelLayer`、`BuildModelAndMediaLayer`、`BuildWebUiServer` 的元组返回（那是 #108/#109 之后的既定形状，本轮只搬文件）。
- **不动启动时序**：`Program.cs` 里 `BootstrapSettings → Build → PluginRuntime.StartAsync → Agent.Start → Management.StartAsync → BootReport → HealthReports → web.Start` 的顺序不在本 ADR 范围内。
- **不给装配加运行时开关**（没有「跳过插件」之类的旁路）。
