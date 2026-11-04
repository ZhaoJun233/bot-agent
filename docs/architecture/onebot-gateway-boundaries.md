# OneBot 网关的职责边界（#108）

`OneBotGateway` 保留公开接口与连接生命周期；报文解析、入站分发、动作发送、查询和待决响应表各有一个所有者。此次拆分以 `d32fb95` 为基线，不改变协议或业务行为。

## 职责对照

| 职责 | 拆分前 | 拆分后 |
| --- | --- | --- |
| 传输选择、启动/停止、连接状态、登录号自检 | `OneBotGateway` | 仍在 [OneBotGateway](../../src/BotAgent.Platforms/OneBot/OneBotGateway.cs) |
| CQ/消息段、引用、ID 与响应字段解析 | 网关内静态方法 | [OneBotInboundParser](../../src/BotAgent.Platforms/OneBot/OneBotInboundParser.cs) |
| 入站消息/戳一戳/撤回分发与合并转发补取 | 网关内入站处理方法 | [OneBotInboundEvents](../../src/BotAgent.Platforms/OneBot/OneBotInboundEvents.cs) |
| echo 分配、待决请求登记、响应匹配、超时及批量取消 | 网关内 `_pending` 与 `SendActionAsync` | [OneBotActionClient](../../src/BotAgent.Platforms/OneBot/OneBotActionClient.cs) |
| 消息发送、群管理动作、失败日志与风控观察 | 网关内公开动作方法 | [OneBotActions](../../src/BotAgent.Platforms/OneBot/OneBotActions.cs)；网关保留转发入口 |
| 图片地址刷新、表情、群/好友/成员/历史/消息查询 | 网关内查询方法 | [OneBotQueries](../../src/BotAgent.Platforms/OneBot/OneBotQueries.cs)；网关保留转发入口 |

生命周期留在门面的原因是它同时拥有传输、生命周期信号、操作租约和连接事件。把这些状态再拆开，会增加停止顺序和回调之间的依赖。`GetSelfIdAsync` 更新连接账号，因而也留在门面。新增组件均为内部类型；`IQqChatSource`、`IPlatformHistorySource`、`IQqActions` 与网关的公开入口保持不变。

## 依赖与状态归属

- 门面装配各组件；动作、查询和入站组件共同使用动作通道，互不引用门面类型。
- 入站组件依赖纯解析器，并通过三个回调发布领域事件；公开事件仍由网关拥有。
- 传输、已发现的登录号与 `SelfIdHint` 通过委托读取当前值；设置仍从 `_box.Current` 读取。组件不缓存启动时的配置对象或传输实例。
- `_pending` 与其锁只由动作通道持有；不支持戳一戳的探测状态只由动作组件持有；解析器无实例状态。

## 保留的边界与风险

1. **接收顺序**：普通消息仍同步处理。只有合并转发消息进入后台，因为其 `get_forward_msg` 响应必须由接收泵继续读取。不能把所有消息统一丢到线程池。
2. **响应通道**：接收入口区分事件与 echo 响应，后者交给同一个动作通道完成等待。动作通道仍使用 10 秒超时；停止时批量取消待决请求。
3. **停止顺序**：一旦请求停止，清理使用不随 HTTP 等待方取消的路径。取消生命周期、停止接收、释放传输、取消待决响应、等待操作租约退出的顺序保持原样。
4. **协议兼容**：撤回与发送响应的 `message_id` 数字/字符串兼容、戳一戳两种通知形状、`raw_info` 与 `self_id` 兜底、动作失败日志保持原样。各事件字段各自沿用原解析规则，不扩大为新的协议承诺。
5. **既有异步边界**：合并转发后台任务的取消/跨连接代际行为沿用基线。本次没有把它改成新的生命周期模型。

## 验证口径

拆分前的完整集成基线为 #106 的 **1212 通过、0 失败**。拆分各步使用同组原始场景比较，未修改 harness 或协议探针断言。最终验证重新构建宿主及全部探针，使用合成数据运行完整集成、协议/反连/生命周期与其余原生探针；候选提交、命令、退出码及 DLL 哈希保存在仓库外证据目录。

重点覆盖：文本与分句（S1/S5）、历史（S12）、图片（S18）、戳一戳（S20）、音乐（S22）、撤回（S25）、引用与风控（S36）、平台生命周期（S52）、FIFO/静默/引用/语音不变量（S56–S59）。真实账号与生产协议端不作为本次回归数据。
