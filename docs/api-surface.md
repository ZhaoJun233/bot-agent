# 接口面地图

**这一页是索引，不是手册。** 逐端点的请求/响应 schema 不在仓库里手写 —— 那样会立刻过期。
权威定义只有两处，都在运行时：

| 面 | 权威来源 | 说明 |
| :--- | :--- | :--- |
| ZBA（新面板契约） | `GET /zba/v1/openapi.json` | 由 `src/BotAgent.Panel/ZbaOpenApi.cs` **运行时生成** OpenAPI 3.1；含 operationId / summary / tags / security / `x-zba-scope` / requestBody schema |
| 旧 `/api/*` | `src/BotAgent.Headless/Adapters/Panel/PanelRoutes.cs` | 表驱动、无 OpenAPI。**已冻结**，见下方声明 |

前端只认 ZBA：`src/web/src` 里对旧 `/api/*` 的引用是 **0 处**。
`src/web/src/api/compat.ts` 用 `EXPECTED_CONTRACT` 校验契约版本，版本不匹配即拒绝连接。

---

## 一、ZBA 契约（44 条，`/zba/v1/*`）

鉴权：`Authorization: Bearer <ZBA 令牌>`。作用域两档 —— `Read`（只读）与 `Admin`（写）。
**默认失败关闭**：宿主没有配置 ZBA 令牌时，除 `/zba/v1/health` 外全部返回 503。

| 方法 | 路径 | 作用域 | operationId | 用途 |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/zba/v1/health` | Read · **匿名** | `health` | 存活探针：进程是否起来、契约是否已加载。**唯一不需要令牌的 ZBA 端点** |
| `GET` | `/zba/v1/meta` | Read · 需令牌 | `meta` | 契约版本号与支持的能力清单（前端用它校验 `EXPECTED_CONTRACT`） |
| `GET` | `/zba/v1/openapi.json` | Read · 需令牌 | `openapi` | 本服务**运行时生成**的 OpenAPI 3.1 文档 —— 逐端点手册的权威来源就是它 |
| `GET` | `/zba/v1/quotas` | Read · 需令牌 | `quotas.list` | 读已登记 SourceKey 的每日 Token 配额上限与已用量 |
| `POST` | `/zba/v1/quotas/save` | Admin · 需令牌 | `quotas.save` | 保存单个配额上限（CAS 期望版本 + 幂等键 + 持久化回执） |
| `POST` | `/zba/v1/quotas/find` | Admin · 需令牌 | `quotas.find` | 按幂等键对账一次配额保存的最终状态 |
| `GET` | `/zba/v1/status` | Read · 需令牌 | `status` | 安全的总体与分组件状态（不含正文、不含密钥） |
| `GET` | `/zba/v1/sessions` | Read · 需令牌 | `sessions.list` | 按工作区 SourceKey 列出服务器 Agent 会话（脱敏形状） |
| `POST` | `/zba/v1/sessions/ensure` | Admin · 需令牌 | `sessions.ensure` | 确保当前服务器 Agent 会话存在（幂等） |
| `GET` | `/zba/v1/capabilities` | Read · 需令牌 | `capabilities.list` | 已登记的驱动、插件、模型供应商及其**真实**运行状态 |
| `POST` | `/zba/v1/capabilities/inspect` | Read · 需令牌 | `capabilities.inspect` | 按稳定能力键查看单个能力的详情 |
| `POST` | `/zba/v1/capabilities/validate` | Admin · 需令牌 | `capabilities.validate` | 只校验不落盘：验证一份能力配置是否合法 |
| `POST` | `/zba/v1/capabilities/execute` | Admin · 需令牌 | `capabilities.execute` | 执行一个受支持的管理动作（写路径，受 scope 与幂等键约束） |
| `POST` | `/zba/v1/capabilities/query` | Read · 需令牌 | `capabilities.query` | 按操作键查询一次管理动作的状态 |
| `POST` | `/zba/v1/capabilities/cancel` | Admin · 需令牌 | `capabilities.cancel` | 请求取消一个管理动作（不承诺远端已停止） |
| `POST` | `/zba/v1/configuration/read` | Read · 需令牌 | `configuration.read` | 读一份**已脱敏**的配置快照（秘密槽位只回形状）；可选 `runtimeEffects` 独立说明字段当前的生效状态 |
| `POST` | `/zba/v1/configuration/save` | Admin · 需令牌 | `configuration.save` | 批量保存：必须带期望版本 + 幂等键，冲突即失败 |
| `POST` | `/zba/v1/configuration/find` | Admin · 需令牌 | `configuration.find` | 按幂等键对账一次配置保存的最终状态 |
| `GET` | `/zba/v1/tasks` | Read · 需令牌 | `tasks.list` | 列出 Agent 任务摘要（不含正文） |
| `POST` | `/zba/v1/tasks` | Admin · 需令牌 | `tasks.submit` | 提交一个 Agent 任务（必须带幂等键） |
| `POST` | `/zba/v1/tasks/find` | Admin · 需令牌 | `tasks.find` | 按幂等键查提交回执 |
| `GET` | `/zba/v1/tasks/{id}` | Read · 需令牌 | `tasks.get` | 读单个任务的运行中或终态 |
| `GET` | `/zba/v1/tasks/{id}/result` | Admin · 需令牌 | `tasks.result` | 读面板任务的有界结果摘要 |
| `POST` | `/zba/v1/tasks/{id}/cancel` | Admin · 需令牌 | `tasks.cancel` | 请求取消任务（不声称远端已成功） |
| `GET` | `/zba/v1/approvals` | Read · 需令牌 | `approvals.list` | 列出待审批请求（摘要已脱敏） |
| `POST` | `/zba/v1/approvals/{id}/decide` | Admin · 需令牌 | `approvals.decide` | 批准或拒绝一条待审批请求（高权限写路径） |
| `GET` | `/zba/v1/events` | Read · 需令牌 | `events.read` | 按序号读取有序事件；断档时必须 resync |
| `GET` | `/zba/v1/traces` | Read · 需令牌 | `traces.list` | 最近若干轮轨迹的**形状**（条数 / token / 耗时 / 未知轮数）与缓存用量合计；**不含会话 key 与消息正文** |
| `GET` | `/zba/v1/logs` | Read · 需令牌 | `logs.list` | 最近运行日志的**安全摘要**：固定组件名 + 固定摘要文案，未知行只给「已隐藏 + 原始字符数」；`limit` 1..600（默认 200），最新在前；**不含日志原文**（发言/成员/路径/URL/凭据均不出宿主） |
| `GET` | `/zba/v1/logs/feed` | Read · 需令牌 | `logs.feed` | 有序安全摘要增量；仅接受可选不透明 `cursor`，首次取最近 200 条、之后每批最多 200 条，旧到新；独立 600 条缓冲，重启/过期/超前游标明确重置；不含原文，`no-store` |
| `GET` | `/zba/v1/plugins/catalog` | Read · 需令牌 | `plugins.catalog` | 浏览已配置的插件市场目录 |
| `GET` | `/zba/v1/plugins/installed` | Read · 需令牌 | `plugins.installed` | 列出已安装插件包与其真实状态 |
| `POST` | `/zba/v1/plugins/sources/resolve` | Admin · 需令牌 | `plugins.sources.resolve` | 把一个**固定版本**的公开 GitHub release 解析成插件清单 |
| `POST` | `/zba/v1/plugins/source/discover` | Admin · 需令牌 | `plugins.source.discover` | 只读解析 GitHub 不可变提交，列出插件与版本候选；不下载 ZIP、不安装 |
| `POST` | `/zba/v1/plugins/preview` | Admin · 需令牌 | `plugins.preview` | 对仓库候选、市场条目或技术 JSON 只读预览；返回调用者绑定的短期 previewId，不写设置或回执、不下载或执行包 |
| `POST` | `/zba/v1/plugins/install` | Admin · 需令牌 | `plugins.install` | 以必填 previewId 确认同一完整候选，通过配置协调器保持停用后暂存并热应用安装 |
| `POST` | `/zba/v1/plugins/lifecycle/preview` | Admin · 需令牌 | `plugins.lifecycle.preview` | 只读预览升级、上一版本回滚或卸载的完整差异、配置保留与依赖影响；不下载或执行包 |
| `POST` | `/zba/v1/plugins/update` | Admin · 需令牌 | `plugins.update` | 以必填 previewId 确认固定升级候选，保留配置、用户数据与启用意图 |
| `POST` | `/zba/v1/plugins/rollback` | Admin · 需令牌 | `plugins.rollback` | 确认 Previous 回滚或有失败回执依据的 Active 恢复，核验缓存后复用热应用／待重启路径 |
| `POST` | `/zba/v1/plugins/{id}/uninstall` | Admin · 需令牌 | `plugins.uninstall` | 以必填 previewId 确认依赖影响，保留配置与用户数据后暂存卸载 |
| `GET` | `/zba/v1/plugins/operations/{key}` | Read · 需令牌 | `plugins.operations.get` | 按幂等键读一次插件操作回执（含 `status` 与 `restartRequired`） |
| `GET` | `/zba/v1/plugins/{id}/settings` | Read · 需令牌 | `plugins.settings.read` | 读插件声明的设置项（**不回显秘密值**） |
| `PUT` | `/zba/v1/plugins/{id}/settings` | Admin · 需令牌 | `plugins.settings.save` | 按期望修订号保存插件设置 |
| `GET` | `/zba/v1/providers/{key}/models` | Read · 需令牌 | `providers.models` | 发现某个已保存供应商路由的上游模型列表 |

**安全导入迁移（#152 M2）**：新增只读发现与预览仍要求 Admin 身份。`previewId` 绑定完整技术清单、来源快照、调用者与安装状态代际，有效期 10 分钟；来源、候选、依赖或代际变化须重新预览。首次确认在回执预留和 ZIP 下载前校验预览；已完成的同幂等键请求优先回放原回执。新安装在发布 Pending 状态前通过既有配置协调器清除残留启用意图，之后须另行启用。停用不是进程隔离：加载程序集、模块初始化、构造器或属性访问仍可能执行第三方代码，权限声明不构成沙箱。旧 `plugins/sources/resolve` 仅保留只读单候选兼容，不能绕过预览。M2 当时未迁移更新协议；当前 M3 候选补充如下。

**生命周期迁移（#152 M3，本地候选）**：升级、回滚和卸载都先走 `plugins/lifecycle/preview`；新写请求必须带同一 `previewId` 与显式风险确认，已完成旧请求仍优先回放原回执。预览绑定调用者、完整新旧清单、来源、安装修订、设置修订及启用意图；所有技术字段差异、精确版本依赖和反向依赖都须核对。普通回滚只接受安装记录中的 Previous 与已保存 ZIP；已知失败升级留下 Active + Pending 时，可显式预览恢复到仍可用的 Active，但必须绑定对应调用者的终态失败回执与同一包状态。首次安装失败、仅有 Pending 而无 Active 时，也保留显式预览卸载入口；只能凭同一调用者、同一包状态的终态失败依据确认，并标明未生效安装候选。缺失、过期、未知或进行中的依据不能授权清理；不自动丢弃 Pending。确认时重新核验本地缓存完整性，不把任意旧版本或孤立目录当成可回滚候选，也不把包回滚称为用户数据回滚。配置、秘密引用、用户数据及启用意图不被生命周期操作重写；已保存配置与目标 schema 不兼容时在暂存前阻断，不自动清空或强制迁移。新增 `previousManifest` / `pendingManifest` 仅来自本地安装记录；生命周期预览另绑定并显示当时的 Pending。缺失时不能推断支持普通回滚或失败恢复。生效结果仍以原生回执、安装状态及 `restartRequired` 为准；不自动重启，未知结果必须核对原操作键。

**配置运行态元数据（#146）**：`configuration.read` 可选返回 `runtimeEffects` 数组；每项只有字段地址 `field`（含可选平台策略目标）、`state`（`current` / `restartRequired` / `applyFailed` / `unknown`）以及需重启时的 `restartTarget: "botAgent"`。不包含字段原值、密钥或异常正文，且只为该身份可见的字段提供状态。状态依据当前运行快照和已确认应用结果，不等同于最近一次保存回执；无关字段的成功保存不能清除待重启或应用失败状态。旧服务端缺失元数据、未来未知状态均显示待核对，不能推断为已生效。服务重启或配置改回运行值后，后续读取再确认状态。

**断言**：`tests/BotAgent.ZbaProbe/Program.cs` 校验 OpenAPI 文档与上表的路由表一致 ——
每条声明路由都出现在文档里、文档里没有多余路径、operationId 唯一、405/410/422 语义正确。

---

## 二、旧 `/api/*`（51 条路由条目 / 50 条唯一路径，已冻结）

> **冻结声明**
>
> 这一组接口**不是前端契约**（`src/web` 一处都没引用），只为部署、运维、兼容与外部集成保留。
> **新增接口必须走 ZBA**（`src/BotAgent.Panel/ZbaRoutes.cs` + `ZbaOpenApi.cs`），
> 不要再往 `PanelRoutes.cs` 的这张表里加行 —— 它没有 OpenAPI、没有契约版本、也没有前端类型。
> 既有行只做「随功能下线一起删」的维护，不做能力扩展。

51 条路由条目里 `/api/quotas` 的 `GET` 与 `POST` 共用同一条路径，所以唯一路径是 50 条。

匹配模式：`Exact` 精确匹配、`Prefix` 前缀匹配（`r.Rest` 拿到剩余段）、`ExactFile` 精确文件；
方法为 `*` 表示同一处理器按 `r.Method` 自行分派（通常是 GET 读 / POST 写）。

| 方法 | 路径 | 匹配 | 用途 |
| :--- | :--- | :--- | :--- |
| `*` | `/favicon.ico` | ExactFile | 返回 204 空图标（浏览器无条件请求；旧面板页面已随 S6 删除） |
| `GET` | `/api/auth/status` | Exact | 面板登录状态：是否需要改口令、是否已有会话 |
| `POST` | `/api/auth/login` | Exact | 面板口令登录，成功后写入会话 Cookie |
| `POST` | `/api/auth/change-password` | Exact | 修改面板口令（初始化后写入 `secrets` 表的哈希） |
| `GET` | `/healthz` | Exact | 存活探针：**始终 200**，容器 HEALTHCHECK 用它 |
| `GET` | `/readyz` | Exact | 就绪探针：已连上协议端 200，否则 **503** |
| `GET` | `/status` | Exact | 宿主运行状态摘要（需面板令牌） |
| `GET` | `/metrics` | Exact | 进程内计数器文本（队列、延迟、错误数等） |
| `GET` | `/api/audit/verify` | Exact | 校验 `security_audit_log` 的追加链完整性 |
| `GET` | `/api/qqlogin` | Exact | 向 NapCat WebUI 取 QQ 登录状态（容器网络里只有本进程能到 napcat:6099） |
| `GET` | `/api/qqlogin/qrcode.svg` | Exact | 同上，返回登录二维码 SVG |
| `GET` | `/api/state` | Exact | 旧面板首屏聚合状态（运行、连接、会话计数） |
| `GET` | `/api/logs` | Exact | 最近的日志行（已按隐私开关脱敏） |
| `*` | `/api/deploy` | Prefix | 一键部署：上传/拉取产物、重建镜像、留回滚点（高权限，默认关） |
| `*` | `/api/restart` | Exact | 请求宿主重启（`POST`）或读取可重启状态 |
| `*` | `/api/settings` | Exact | `GET` 读配置快照、`POST` 保存（面板保存的唯一写入口） |
| `GET` | `/api/quotas` | Exact | 读已登记 SourceKey 的每日 Token 配额与用量 |
| `POST` | `/api/quotas` | Exact | 写单个配额上限（CAS + 持久化回执） |
| `*` | `/api/health-report` | Exact | `GET` 读健康日报配置、`POST` 触发/保存 |
| `*` | `/api/backup` | Exact | `GET` 读热备状态（最近成功时间、是否过期、保留份数）、`POST` 立刻做一次热备 |
| `GET` | `/api/agent/status` | Exact | 服务器内置 Agent 的运行状态 |
| `POST` | `/api/agent/test` | Exact | 对 Agent 当前模型做一次连通性测试 |
| `*` | `/api/agent/models` | Exact | `GET` 列上游可用模型、`POST` 触发模型列表刷新 |
| `*` | `/api/agent/sessions` | Exact | `GET` 列服务器 Agent 会话（脱敏）、`DELETE` 删除 |
| `GET` | `/api/participation` | Exact | 参与状态机快照（**只读**，只观测不拦截） |
| `GET` | `/api/tools` | Exact | 工具目录：名字 / 类别 / 参数 / 是否要审批 / 执行者 |
| `GET` | `/api/plugins` | Exact | 已装插件与启用状态（**只读**） |
| `GET` | `/api/traces` | Exact | 决策轨迹：一轮一条的六节点形状（`?limit=` 1–50，默认 20） |
| `GET` | `/api/dashboard` | Exact | 健康仪表盘聚合：运行、队列、模型延迟、内存、工具、权限、轨迹容量 |
| `GET` | `/api/approvals` | Exact | 待审批单的形状（不含正文） |
| `POST` | `/api/approvals/decide` | Exact | 批准/拒绝一条待批（高权限写路径，fail-closed） |
| `GET` | `/api/local` | Exact | 本地演练场通道开关与出箱形状 |
| `POST` | `/api/local/message` | Exact | 把一条本地消息注入成入站消息（入口，fail-closed） |
| `GET` | `/api/platforms` | Exact | 多平台注册表与各实例连接状态 |
| `POST` | `/api/webhooks/feishu` | Exact | 飞书事件回调入口（验签 + 去重） |
| `POST` | `/api/ai-mode` | Exact | AI 总开关（开/关对话） |
| `*` | `/api/conversations/` | Prefix | 会话列表与消息（按会话 key；显示层脱敏、内部 key 不变） |
| `GET` | `/api/profiles/` | Prefix | 单个人物档案摘要（`allScopes` 视图不用于模型注入） |
| `GET` | `/api/archive` | Exact | 读归档会话（`?key=` 与 `?limit=` 1–2000，默认 200） |
| `*` | `/api/netease/qr` | Prefix | 网易云扫码登录（取二维码 / 轮询状态） |
| `*` | `/api/music/test` | Exact | 点歌链路自测：搜索 → 歌词 → 音源 → 波形 |
| `*` | `/api/search/test` | Exact | 联网搜索自测：把结果原样贴出 |
| `*` | `/api/voice/test` | Exact | TTS 自测：只合成、不发群 |
| `GET` | `/api/voice/health` | Exact | 云端 TTS 服务健康与配置形状 |
| `GET` | `/api/voice/clones` | Exact | 已登记的音色复刻列表 |
| `*` | `/api/voice/clone/delete` | Exact | 删除一个音色复刻 |
| `*` | `/api/voice/clone` | Exact | 创建/查询音色复刻 |
| `*` | `/api/stickers` | Exact | 表情包库：列表 / 取图 / 删除 / 巡检 / 导入 |
| `*` | `/api/stickers/` | Prefix | 同上，按表情 id 的子路径 |
| `*` | `/api/jargons` | Exact | 圈子黑话库：列表 / 审核 / 录入 / 删除 |
| `*` | `/api/jargons/` | Prefix | 同上，按词条 id 的子路径 |

**断言**：`tests/BotAgent.ArchitectureProbe` 校验 `PanelRoutes.cs` 的每条路径都出现在上面这张表里
（**只查覆盖、不禁止新增** —— 新增行不会因此变红，但漏写文档会）。

---

## 三、配置面（不是接口，但常被一起问）

配置有三个层，**优先级从低到高**：

| 层 | 位置 | 谁负责 | 说明 |
| :--- | :--- | :--- | :--- |
| ① compose 层 | `.env` → `docker-compose.yml` 的 `${VAR}` | 部署者 | `.env` **不会自动进容器**，只是 compose 的变量替换；新变量必须同时在 `environment:` 里声明 |
| ② 环境变量层 | 容器内的 `BOTAGENT_*` / `QQCHAT_*` | 部署者 | `BOTAGENT_*` 是规范名、优先；`QQCHAT_*` 兼容旧部署。同名时规范名赢 |
| ③ 面板 + 数据库 | `settings` 表 / ZBA `configuration.*` | 面板使用者 | **这才是真源**：面板保存过一次之后，同名环境变量就不再起作用（面板会提示「已被面板里的设置覆盖」） |

配置键的完整清单见 [README 的配置参考](../README.md#配置参考)；
配置读取与校验的代码事实在 `src/BotAgent.Core/Contracts/AppSettings.Part1.cs` … `Part5.cs` 与
`src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs`。

---

## 四、改这一页的规矩

1. **加 ZBA 路由** → 先改 `ZbaRoutes.cs` 与 `ZbaOpenApi.cs`，再补上表一行；`ZbaProbe` 会同时校验两边。
2. **加旧 `/api/*` 路由** → 不要加。真需要就先开 issue 讨论为什么不能走 ZBA。
3. **删路由** → 同步删上表对应行，否则 `ArchitectureProbe` 的覆盖断言会红（这是故意的）。
4. **不要**把 OpenAPI 的 schema 抄进这一页 —— 抄一次就开始腐烂，权威版永远是 `/zba/v1/openapi.json`。