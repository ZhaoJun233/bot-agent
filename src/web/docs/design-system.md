# BotAgent Console 设计系统

本次重设计基于 dev 当前源码（开始时源 HEAD 与 upstream/dev 文件树零差异），保持 Vue 3 / TypeScript / Vite 和 ZBA 1.0。没有新增运行时依赖、外部字体、CDN 或图像请求。

## 视觉与信息结构

方向是克制的开发者工作台：冷白与浅蓝底色、淡蓝色强调色、细分隔线、统一控件与状态标签。默认浅色；深色为独立的语义配色。颜色只承担导航、操作与状态含义，不用装饰图表或虚构数据。

桌面固定侧栏与 60px 顶栏，主内容最大宽 1400px。900px 以下使用横向导航，640px 以下四个功能入口分列、图标与标签同时可见。页面帮助收在顶栏可展开说明中，保留操作空间。

- 认证：品牌说明与认证表单双栏，小屏收敛为品牌短头部；原有密码输入、默认只读和内存会话机制保留。
- 概览：标题、权限与刷新 → 健康结论 → 四项真实指标 → 组件和异常 → 常用入口。移动端组件仍显示原因与更新信息。
- 配置：分类导航与标签/编辑值成对排列，来源、用户模式、生效状态及规则持续可见。秘密独立成区，草稿、冲突、未知回执和保存条保留。
- 集成：类别筛选、已加载条目搜索和一致卡片；功能开关与运行操作分区。搜索只过滤已加载数据，不替代后端分页。
- 任务：提交与审批并列（宽屏），历史与结果分区；所有输入、审批动作、幂等键和状态反馈保留。

## Tokens

全局变量定义在 ../src/styles/base.css，组件从语义变量取色。

| 类别 | 规范 |
| --- | --- |
| 背景 | --bg / --bg-soft / --surface / --surface-muted |
| 文本 | --text / --text-muted / --text-soft |
| 强调 | --accent / --accent-strong / --accent-deep / --accent-soft / --on-accent |
| 状态 | --ok-bg/text、--warn-bg/text、--bad-bg/text、--neutral-bg/text |
| 边界 | --border 用于容器，--border-strong 用于输入等控件 |
| 字体 | 本地 Segoe UI Variable / Segoe UI / PingFang SC / Microsoft YaHei；标题 Bahnschrift；代码 Cascadia Code / Consolas |
| 字号 | 11 / 12 / 14 / 16 / 22 / 28px，权重 400 / 500 / 600 / 700 |
| 间距 | 4px 基准，4 / 8 / 12 / 16 / 20 / 24 / 32 / 40 / 48px |
| 圆角 | 5px 标签、8px 控件、12px 容器、16px 认证容器 |
| 控件 | 桌面默认 36px、紧凑 30px、主要与触控 44px |
| 图标 | 16 / 18px 线性 currentColor SVG，品牌独立字形 |
| 阴影 | --shadow-1 小面积表面；--shadow-2 弹窗及浮动操作条 |
| 动效 | 120 / 180ms；cubic-bezier(.2,.7,.3,1)；reduced-motion 全面关闭 |
| 断点 | 全局 1200 / 900 / 640px；页面按实际内容使用 1360 / 1200 / 1000 / 767px |

## 共享组件边界

新增 PageHeader 统一标题、用途、元信息与操作位，概览首先采用。其它页面保留自己的标题节点，避免改变配置/能力页 HTMLElement 焦点引用。

新增 ThemeToggle 与 useTheme 管理外观。localStorage 只写 botagent-appearance；读取/写入不可用时保持可用浅色主题，令牌不进入任何浏览器持久化存储。

继续复用 AppIcon、BrandMark、StatusBadge、ErrorNotice、BreakableId、ManagementDialog 及既有焦点恢复实现。没有为单次使用额外包装所有按钮或表单。

## 验证与限制

运行 typecheck、308 项单元/组件/契约测试、生产 build 和真实隔离 Headless 的三浏览器 E2E。视觉矩阵在 tests/visual/redesign.spec.ts，独立于默认业务 E2E；通过现有视觉配置运行。

矩阵覆盖六种宽度（1440、1024、768、390、375、320），两套主题，认证/概览/配置/提供商/能力/任务及提供商、插件市场弹窗。测量文档宽度、弹窗边界、语义文本 4.5:1、输入边界 3:1；检查搜索、主题切换与锁定。

截图使用合成令牌与临时数据根。市场视觉使用空合成响应，安装/更新/未知回执仍由原有组件及 E2E 用例验证。视口模拟不等于真机软键盘或屏幕阅读器实测。设计 token 对比度检查不等于全站 WCAG 认证。

生产 base 仍为 /web/。本任务只改源码与本地产物，不提交、不推送、不部署。
