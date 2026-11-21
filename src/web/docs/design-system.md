# BotAgent Console 设计系统

BotAgent 管理控制台采用 Vue 3 / TypeScript / Vite 构建，遵循 ZBA 1.0 与 `cfg-0.3.3` 契约。不引入外部运行时依赖、外部字体、CDN 或第三方图像请求，生产构建 `base` 固定为 `/web/`。

## 视觉与领域信息架构

设计方向为克制、高密度的现代开发者工作台：冷白浅蓝底色与独立深色语义表、清晰层次边界、统一控件与状态标签。颜色只承担导航、操作与状态语义，不堆叠装饰图表或虚构数据。

### 三组八项导航（领域解耦）

控制台按“模型供应商 / 消息平台 / 插件中心 / 系统配置”四域拆分，不再将所有后端包平铺到单一能力卡片墙：

| 分组 | 页面 | 核心职责 |
| --- | --- | --- |
| 工作台 | 系统概览 | 健康结论、四项真实指标、可下钻组件表、最近错误摘要与操作恢复查询 |
| 接入与扩展 | 模型供应商 | 统一管理模型供应商（主/备协议、默认模型、优先级、凭据与连通测试）、默认路由设置与 `system.model-services` 路由引擎状态 |
| 接入与扩展 | 消息平台 | 独立展示 QQ 私域、QQ 官方开放平台、飞书与本地调试等平台驱动，聚焦连接启停、接入凭据、访问策略与消息能力声明 |
| 接入与扩展 | 插件中心 | 专注 8 个内置对话功能插件与第三方扩展，提供卡片开关、一键“打开界面”、内置抽屉详情、插件市场与仓库导入分栏 |
| 接入与扩展 | 系统配置 | 五分区纵向配置页（基础行为与人设、对话编排、内置 Agent 与工具、健康日报、高级与系统包只读总览） |
| 运行与观测 | Agent 任务 | 宽屏任务提交、工具审批队列、历史分页筛选、幂等键恢复与节点明细 |
| 运行与观测 | 轨迹与缓存 | 摘要卡片、缓存命中率、点击行展开节点级耗时与调用明细抽屉 |
| 运行与观测 | 运行日志 | 脱敏实时日志终端，支持级别筛选、搜索、自动换行切换与连接同步状态 |

### 全局壳子与响应式布局

- 桌面固定左侧栏（`244px`，支持折叠至 `60px` 图标轨）与 `60px` 粘性顶栏，主内容区最大宽度 `1420px`。
- 顶栏常驻全局管理员模式切换（`管理员模式` / `只读模式`）、连接状态、刷新、页面帮助说明、主题切换与锁定按钮；离开或降级前统一走未保存草稿保护。
- 900px 以下切换为顶栏横向导航，640px 以下采用 4×2 紧凑导航网格，图标与文字标签同时可见且触控目标 ≥ 44px。
- 认证页采用品牌说明与认证表单双栏卡片，小屏收敛为短头部；令牌仅保存在当前页面内存，默认以安全只读模式进入。

## 样式分层与 Tokens

样式按职责拆分为六层，位于 `src/web/src/styles/`：

- `tokens.css`：浅色/深色全量 CSS 变量（全部颜色使用 6 位 `#hex`，兼容浏览器对比度自动审计）
- `reset.css`：基础重置、焦点环与 `prefers-reduced-motion` 全面降级
- `layout.css`：应用壳子、侧栏/折叠轨、粘性顶栏与认证页舞台
- `components.css`：按钮、表单、表格、弹窗/抽屉、领域卡片与终端样式
- `responsive.css`：全局响应式断点（`1200 / 900 / 640px`）
- `base.css`：入口聚合层

| 类别 | 规范 |
| --- | --- |
| 表面与边框 | `--bg` / `--bg-soft` / `--surface` / `--surface-muted`；`--border` / `--border-subtle` / `--border-strong` / `--border-input` |
| 文本层级 | `--text` / `--text-muted` / `--text-soft`（浅/深色均满足 ≥ 4.5:1） |
| 品牌与强调 | `--primary` / `--primary-hover` / `--on-primary`；`--accent` / `--accent-strong` / `--accent-deep` / `--accent-brand` / `--accent-soft` / `--accent-tint` / `--on-accent` |
| 状态语义 | `--ok-bg/text`、`--warn-bg/text`、`--bad-bg/text`、`--danger`、`--neutral-bg/text` |
| 领域标识色 | `--domain-provider-bg/text`（靛蓝）、`--domain-platform-bg/text`（青蓝）、`--domain-plugin-bg/text`（翠绿）、`--domain-system-bg/text`（中性灰） |
| 终端色板 | `--term-bg/head/border/text/muted/time/info/warn/error/error-text/source/user/path/hover/scroll` |
| 字体与字号 | 本地系统无衬线/展示/等宽字体栈；全部使用 `rem`（`0.75rem` – `1.5625rem`），确保 200% 缩放无截断 |
| 控件与触控 | 桌面默认 `36px`、紧凑 `30px`、触控与主要入口 `44px`；`UiSwitch` 统一 `44×24px` 满足 WCAG 2.2 最小目标尺寸 |
| 圆角与阴影 | `6 / 9 / 13 / 18px`；`--shadow-1` / `--shadow-hover` / `--shadow-2` |
| 动效 | `120ms` / `180ms` `cubic-bezier(0.2, 0.7, 0.3, 1)`；右侧抽屉使用纯透明度淡入避免视口右边界瞬时溢出 |

## 共享 UI 原语与插件沙箱主题

共享组件位于 `src/web/src/shared/ui/`：

- 布局与导航：`PageHeader`、`UiTabs`、`UiEmptyState`、`UiBanner`
- 表单与操作：`UiSwitch`（原生 checkbox + `role="switch"`）、`UiConfirmDialog`、`ManagementDialog`（支持居中弹窗与右侧抽屉模式，含焦点陷阱与关闭焦点恢复）
- 展示与状态：`AppIcon`（全站线性 SVG 图标库）、`BrandMark`、`StatusBadge`、`ErrorNotice`、`BreakableId`、`ThemeToggle`（`useTheme` 仅将 `light`/`dark` 写入 `localStorage['botagent-appearance']`，令牌绝不持久化）

运行时插件沙箱 iframe（`PluginUiFrame.vue`）通过 `MutationObserver` 同步宿主 `<html data-theme>` 属性并经由安全消息桥下发主题状态；`packages/token-quota/web/style.css` 与 `packages/stickers/web/style.css` 使用与控制台一致的浅/深色变量体系。

## 验证与限制

- 静态与单元验证：`npm run typecheck`、`npm run test:unit`（91 个测试文件 / 1,304 项单元、组件与契约测试）、`npm run build`、隔离插件 UI 构建（`quota-ui` / `research-ui`）以及 `BotAgent.ArchitectureProbe`。
- 端到端与视觉验证：真实隔离 Headless 宿主的三浏览器 Playwright 矩阵（`chromium`、`firefox`、`webkit`、`chromium-rate-limit` 与 `BOTAGENT_WEB_TEST_RECOVERY=1` 恢复用例），以及 `playwright.visual.config.ts` 视觉与对比度矩阵（覆盖 `1440 / 1024 / 768 / 390 / 375 / 320` 六宽度 × 浅/深双主题，含 320px/200% 缩放无溢出、弹窗边界与 WCAG 对比度审计）。
- 全部测试均使用合成令牌与临时数据目录，不读取任何真实会话或成员隐私数据。视口模拟不替代真机输入法或屏幕阅读器实测。
