# 配额前端

TokenQuotaDialog.vue 持有配额页面、额度草稿、CAS 保存、原操作键核对与明确同键重试。presentation.ts 是标题、摘要与功能 ID 的来源；quotas.ts 持有 HTTP DTO、校验和三个客户端方法。组件只导入 Vue、本包源码及 contracts/ui 公开类型，不导入宿主 ZbaClient、配置页或全局状态。

宿主传入冻结的 QuotaClient 三方法视图，以及绑定身份和操作类型的 PluginUiBridge。通用弹窗负责焦点、Escape、背景隔离和回焦；插件开关仍由宿主管理配置版本与回执，包通过 description 插槽提供业务说明。开关的权限与插件 ID 由宿主固定，包只传 blocked 与 runtimeState。操作登记仅投影不可变 key/target/meta/state，保留原作用域的晚到结论，但身份或权限变化后不能登记新操作。桥中不含令牌、全局客户端或完整操作注册器。

在本目录执行 npm ci --ignore-scripts --no-audit --no-fund 和 npm run build，生成 dist/token-quota.js、token-quota.css 和 module-ownership.json。构建需要同一相对布局中的 contracts/ui；可仅复制这两处源码到空目录。Vue 3.5.45 是唯一外部运行依赖，其余公开 UI 契约为类型。构建守卫拒绝目录外源码和非 Vue 外部依赖。CI 的 quota-ui job 使用隔离目录执行同样流程。

当前 D1 使用宿主的薄适配组件组合包组件，发行构建仍收集页面。独立 JS/CSS 产物尚未进入批准归档及同代运行时 UI 加载协议，不能据此宣称单包在线升级、跨版本桥接或完整 D2 已完成。现有页面组件与三浏览器测试验证宿主适配；独立产物另以公开接口和替代壳做渲染检查。
