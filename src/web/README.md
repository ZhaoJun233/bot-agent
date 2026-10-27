# BotAgent Web（阶段 6 S1–S5）

Bearer 接入、契约兼容检查、概览，以及 S2 的配置读取与整批编辑。仅使用 ZBA `zba-1.0`（`cfg-0.3.3`），不调用旧 `/api`，不读取聊天正文。

已覆盖能力管理（含插件设置弹窗与启用开关）、任务提交/审批/取消与事件恢复（events 轮询订阅，`resyncRequired` 时整份补读）。

## 命令

```bash
npm ci
npm run typecheck
npm run test:unit
npm run build
npx playwright install chromium   # 首次
npm run test:e2e                  # 先 build，再启动真实隔离 Headless + Vite preview
```

`test:e2e` 使用 `src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll`，在系统临时目录创建两个合成数据根：一台配置合成 ZBA 令牌，另一台**不配置** ZBA 令牌（验证真实 503 `zba_not_configured`）；模型地址指向 mock。不会读取真实数据。可用 `BOTAGENT_HEADLESS_DLL` 指向另一份 Release DLL。

## 安全边界

- Bearer 只保存在当前页面内存；锁定/刷新即丢失，不写 localStorage/sessionStorage/URL/日志。
- 默认只读；管理员模式通过只读 admin 端点 `GET /tasks/{id}/result` 确认（404 `task_not_found`=admin，403=read；其它 404 不视为 admin），不用写请求试探。
- 错误只保留安全 code/HTTP 状态/`X-Request-Id`；不显示原始响应体、prompt、结果正文或秘密。
- 概览刷新失败保留上次成功数据并标记 stale；503/401/429 不伪装成功。

## 请求与运行时解码

- 每个请求只有一个 deadline，覆盖“等响应头 + 读完整个响应体”；慢速/截断响应会以 `ZbaNetworkError` 结束并释放 loading，可重试。`response.text()` 的错误同样归一化，不会留下未处理拒绝。
- `health`/`meta`/`status` 在 API 边界做运行时 wire 校验：缺字段、`null`、错误类型或错误嵌套一律 `invalid_json`；刷新失败保留上次有效快照并显示 stale。未知附加字段忽略，便于后端向后兼容追加字段。
- 401 与未配置 503 会清空会话并释放连接 busy；修改令牌后可直接重试，无需刷新整页。连接 busy 按连接尝试计数，旧连接不能清掉新连接的 busy。
- 配置链路上的 401 视为整个管理会话失效：由页面统一退回认证门，并清空草稿与秘密输入，不在配置页内吞掉。
- 管理员探测返回后再次核对会话、令牌和当前契约兼容性；探测期间契约变为不兼容时丢弃结果，不进入管理员模式。

## 视觉与可访问性（A1/A3）

- `src/shared/ui/BrandMark.vue`：负空间机器人头（双眼、天线、微笑），`currentColor`、16–64px 可缩放、固定 `aria-hidden`；圆角方片由 `.brand-badge` 或 favicon 提供，不在字形里再套一层框。
- `public/favicon.svg`：`#1f6f9e` 圆角方片 + 白色字形；无外部字体、图片或 CDN。
- 认证页：桌面浅色双栏（品牌信息 + 表单），390 收敛为顶部短头部；令牌输入仍为 `type=password`、`autocomplete=off`、只存内存。
- 概览：390 侧栏折叠为顶栏，组件表收敛为「组件 / 状态」两列；不新增图表、请求或假数据；F8 的卡片式/粘性列留 S4。
- 焦点环 3px `#1f6f9e`（输入 2px）；关键文本对比度 ≥4.5:1；`prefers-reduced-motion` 下关闭过渡；整页只请求同源资源。

## S2：配置读取与编辑

- 读取：`POST /configuration/read`（kind=1 可编辑投影）；标准模式只读，管理员模式才能改字段与秘密槽位。
- 草稿：只在本页内存；离开配置、锁定、降级或 401 时才清空秘密输入，普通字段草稿保留到明确放弃或会话结束。
- 保存：`POST /configuration/save` 每次用户意图生成新操作键，整批提交字段与秘密；校验走 `/capabilities/validate`，不落盘。
- 单飞：保存或校验在读时不允许再发第二个写请求，离开对话框里的“保存并离开”同样被拦住。
- 回执：成功码（0/13）必须带回执才清草稿；`code 12`、网络中断、2xx 但缺少回执都进入“提交结果未知”，冻结编辑并保留草稿与操作键，只能用 `/configuration/find` 核对，不能直接重发。
- 核对结果：找到回执=已提交；NotFound(14)=未落盘，可换新键重存；代际变化(6)/键过期(5) 无法核对时重读快照；其它错误继续保留未知态。
- 状态配对：保存与核对都只在 HTTP 状态与业务码构成契约配对时采纳结果（NotFound 必须 404，UnknownCommit 必须 503）。HTTP 503 上带 `code 14` 这类矛盾响应会抛安全错误，页面保留操作键与未知态，不据此断言“没有落盘”。

## S2-3：能力管理

- 目录：`GET /capabilities` 读已登记驱动/插件/供应商，渲染真实运行时状态、期望开关、凭据与描述符版本。
- 支持矩阵：只渲染后端 `actions[]`；不可用动作显示 `reasonCode`，前端不猜能力。
- 描述符：`POST /capabilities/inspect`；NotFound(2) 是 404 的可解释结果。
- 动作：`POST /capabilities/execute` 需 admin，提交前重新 inspect 取当前描述符版本；`mg1:` 操作键来自 `configuration/read` 的真实 generation。
- 回执：显示结果码与动作状态；无回执时写“结果未知，操作键已保留”，可 `/capabilities/query` 核对、`/capabilities/cancel` 请求取消（只显示服务端真实状态，不写“已取消”）。
- 操作生命周期：每次动作是一条 `OperationRecord`（key / target / command / descriptorRevision / phase），回执只是它的一个字段。确认后确认框立即退场，结果未知时页面内的核对、取消、重试入口不会被遮罩挡住。
- 未知结果：不生成替代键。「用同一操作键重试」复用原 key、原命令与原 `expectedDescriptorRevision`，由服务端按幂等回放原回执；只有上一次已到终态（已完成 / 失败 / 已取消）或换了目标动作才产生新键。
- 跨视图：能力页与配置页一样常驻（`v-show`），待核对的操作键在切到概览/配置后仍然保留；锁定、401、换令牌会作废整个会话的操作键。
- 所有权：每个 await 之后、发出写请求之前校验 client / 视图 / 权限 / 是否卸载；**离开视图、退出管理员模式、写能力失效都会推进所有权代数**，所以切走再切回来也不会把离开前那次确认补发出去。
- 预检失败：对“结果未知”的操作再次确认只做只读核对（目标还在、动作仍受支持）。预检失败**不否定**先前那次提交——记录仍是未知，键与面板都保留，用户可以稍后核对或再重发。
- 未终态不可关闭：只有终态（已完成 / 失败 / 已取消）或服务端明确拒绝的记录才能关闭并删除；未知或运行中的记录面板会保留，避免把唯一还带着操作键的入口收起来。
- 回执可信：`/capabilities/query` 没有回执的失败（业务错误、传输错误）只写错误，不覆盖上一次可信回执；`accepted` 却没有回执一律视为未终态。
- 身份：卡片与动作使用 `kind + 完整键字段` 的结构化身份，显示标签不参与匹配，同名插件与供应商不会互相覆盖。
- 分页：仅在后端给 `nextCursor` 时出现“加载下一页”；翻页失败按真实错误展示并保留已加载列表，不绕过、不硬编码全量拉取。游标是后端签发的 Base64URL（无填充）+ 签名的既有契约，签发侧与查询侧的长度/字符集已对齐（见 `src/BotAgent.Storage/ManagedCapabilityService.cs` 的 `WriteCursor`/`ReadCursor` 与 `src/BotAgent.Panel/ZbaQuery.cs` 的 `GetCursor`）。
- 权限：read 令牌可浏览目录与矩阵，动作按钮禁用；execute 由服务端 403 兜底。

## 阶段范围

- 已实现：认证门、health/meta/status、契约兼容、概览组件/错误/空态、只读/管理员模式切换、锁定、配置读取与编辑（S2-1/S2-2）、能力目录/描述符/支持矩阵/动作回执/分页（S2-3）、插件设置与启用开关、任务提交/审批/取消/事件恢复、chromium/firefox/webkit E2E 矩阵、界面美化（S5）。
- 静态生产承载：线上由 Caddy 从静态目录托管 `dist/`（`/web/*`），与后端容器解耦，可独立发布与回退（见 `docs/plans/agents/07-pnl-evidence.md`）。
- 未实现：MCP 管理页；资源预算只有本地测量，未设门禁。
