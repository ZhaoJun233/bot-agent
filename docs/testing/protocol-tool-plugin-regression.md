# 协议 / 工具 / 插件回归测试矩阵（#112）

> 这一页回答：**OneBot 协议收发、消息排队与并发、Agent 工具调用、插件生命周期、异常恢复与持久化**
> 现在分别由哪些自动化用例钉住，失败时该看哪一条断言。
> 背景：#106（回复管线）与 #108（OneBot 网关）重构时用的安全网；重构本身不改这些断言。

## 1. 判定口径

| 层 | 用什么 | 放哪 | 进 CI 的方式 |
| --- | --- | --- | --- |
| 黑盒端到端 | `IntegrationHarness` 的 S 场景（另起机器人进程 + MockProtocol + MockOpenAi） | `tests/BotAgent.IntegrationHarness/` | 3 个 `Synthetic integration test` 矩阵 |
| 纯函数 / 传输层 | 合成探针（源链接或反射，不启动宿主） | `tests/BotAgent.*Probe/` | `Build and synthetic probes` |
| 前端 | Playwright | `src/web/tests/e2e/` | 4 个 `Browser end-to-end` 矩阵 |

全部用例只使用合成数据与 Mock 协议端，不接触真实群聊、真实模型或真实开放平台凭据。

## 2. 覆盖矩阵

### 2.1 OneBot 协议收发

| 行为 | 用例 | 断言要点 |
| --- | --- | --- |
| 文本消息 | S1 | 群消息进模型、回复按预期发出 |
| 引用回复 | S19、S58 | 引用只挂第一条；排队积压时引用不挂到旧消息 |
| 音乐卡片 | S22、S23 | 识别分享 → 歌词/音源 → 交给模型；分享卡片不误判 |
| 撤回 | S25 | 内容保留但标 `[已撤回]`、不可再引用、可评论（带冷却） |
| 戳一戳 | S20 | 回话 / 戳回去 / 防拉锯 |
| 动作响应 `message_id` 两种写法 | **S64（新）** | 数字、字符串都能读出；缺失/非数字退化成 0 而不抛异常 |
| 反向 WS 传输/分帧/断连 | `ReverseTransportProbe` | `failures=0`：分片重组、关闭时待决动作清理 |
| 掉线与重连 | S16、S42 | 协议端连着但账号掉线 → 面板说实话；官方通道掉线自愈 |

### 2.2 消息排队与并发

| 行为 | 用例 | 断言要点 |
| --- | --- | --- |
| 多会话并发与租户隔离 | `ConcurrencyStressProbe` | 1000 条合成事件 3 秒内注入；单租户队列有界；噪声租户与普通租户互不阻塞 |
| 每会话 FIFO | S56 | 同一会话回复按到达顺序串行，不并发乱序 |
| 舱壁（单租户积压不影响其它） | `ConcurrencyStressProbe` | 非噪声租户仍能推进；安全合并可观察 |
| 取消语义 | `DeadlineGateProbe` | 外部取消传播、排队等待可取消、预取消不获取闸门 |
| 并发上限热变更 | S14 | 面板改上限后会话不卡死 |
| 连珠炮补偿 | S32 | 被限流挡下的消息不丢，补一次评估 |

### 2.3 Agent 工具调用

| 行为 | 用例 | 断言要点 |
| --- | --- | --- |
| 有限步进循环 | S47 | 聊天侧显式步进：当场查完再问一次；默认 1 步不变 |
| 工具可见性 | S40、S46 | docker 工具表与提示词同步；面板工具目录只读端点与策略跟随 |
| 插件门禁（工具/能力允许位） | S40、S51、`PluginSettingsProbe` | 未允许的工具不出现在提示词与执行面；配额插件准入 |
| 人工审批 | S43 | 模型想调工具 → 群主同意才执行，且只执行固定假工具 |
| 服务器 agent 上下文 | S39 | 默认单指令隔离、`//接着` 续、`//reset` 清干净 |
| 外部 MCP 工具 | S53 | 显式白名单调用回环 Streamable HTTP；被拒绝的工具负向用例 |

### 2.4 插件生命周期

| 行为 | 用例 | 断言要点 |
| --- | --- | --- |
| 加载与清单校验 | `PluginPackageProbe`（34 条） | TFM 白名单、`MinimumHostVersion`、清单上限、密钥不许默认值 |
| 启用/禁用与设置 | `PluginSettingsProbe`（48 条）、S51 | 设置 schema 校验、启停后能力生效 |
| 市场安装/升级 | `PluginMarketProbe`（82 条） | 安装、版本比较、失败不落地 |
| 失败降级 | `PluginPackageProbe` | 单个插件初始化失败被隔离（`independent failure`），其它插件照常加载 |
| 端到端与重启记账 | S51 | Token 配额插件经旧面板兼容、ZBA、模型准入、重启后记账不丢 |

### 2.5 异常恢复与持久化

| 行为 | 用例 | 断言要点 |
| --- | --- | --- |
| 连接中断 | S16、S42、`ReverseTransportProbe` | 状态如实上报；断线自愈；待决动作不悬挂 |
| 重启后会话恢复 | S6b、S11、S12 | 会话/长期记忆/群历史从磁盘恢复 |
| 备份与恢复 | S60 | 备份独立可打开、`user_version` 一致、放回新目录能起来且配置还在 |
| 进程被杀后的中间态 | S60、`ConcurrencyStressProbe` | 已接受事件全部完成或可恢复；库文件句柄释放后仍可读 |

## 3. 怎么跑

```bash
# 全部协议/工具/插件相关场景（先 build Headless 与 harness）
for s in s1 s19 s20 s22 s23 s25 s32 s39 s40 s43 s46 s47 s51 s53 s56 s64; do
  BOTAGENT_IT_ONLY=$s cmd //c "chcp 65001 >nul && dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll";
done

# 纯函数 / 传输层探针
cmd //c "chcp 65001 >nul && dotnet tests/BotAgent.ConcurrencyStressProbe/bin/Release/net10.0/BotAgent.ConcurrencyStressProbe.dll"
cmd //c "chcp 65001 >nul && dotnet tests/BotAgent.DeadlineGateProbe/bin/Release/net10.0/BotAgent.DeadlineGateProbe.dll"
cmd //c "chcp 65001 >nul && dotnet tests/BotAgent.PluginPackageProbe/bin/Release/net10.0/BotAgent.PluginPackageProbe.dll"
```

## 4. 已知未覆盖 / 刻意不做的

- **真实 NapCat 互通**：所有场景用 `MockProtocol` 模拟协议端；真实 NapCat 的消息形状差异（版本、富媒体段）仍靠线上日志与 `tools/mask-cjk.py` 排查。
- **真实插件二进制**：插件探针用合成的 `AssemblyLoadContext` 包与假插件类型，不加载第三方真实插件。
- **真实开放平台 / 飞书**：S42 用假网关，S50 用合成 Webhook；需要外部凭据的互通不进 CI。
- **B24 类子进程取消竞态**：`ReviewRemediationProbe` 覆盖「调用方取消清理子进程」，但 CI 上曾偶发（已改有界等待）；跨平台（Linux）子进程语义没有单独矩阵。
- **压测**：`ConcurrencyStressProbe` 是 1000 条合成事件的调度压测，不是长时间高负载浸泡测试。

## 5. 相关

- `docs/testing/configuration-permission-regression.md`：配置与权限链路矩阵（#111）。
- `docs/testing/protocol-tool-plugin-regression.md`：本页。
- `docs/operations/ci-quality-gates.md`：9 项必需检查与不稳定登记表。
- `docs/architecture/onebot-gateway-boundaries.md`：OneBot 网关职责边界（#108）。
- `docs/architecture/large-file-policy.md`：重构 PR 的验证要求（附重构前后同组场景对比）。
