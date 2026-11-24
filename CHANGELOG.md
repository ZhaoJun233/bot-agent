# 变更日志

本文件记录仓库对外可见的变化，格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

- 只维护 **Unreleased** 与**最近两个已发布版本**；更早版本的完整说明在
  [GitHub Releases](https://github.com/ZhaoJun233/bot-agent/releases)。
- 版本号的**唯一真源**是 `Directory.Build.props` 的 `<Version>`（#122）。csproj 里再写 `<Version>`
  会悄悄压过它，ArchitectureProbe 与 CI 都会判红。
- 发版顺序固定：改 `<Version>` → 把 Unreleased 段改成新版本段 → 打 `vX.Y.Z` tag → CI 在 tag 上断言
  tag 与 `<Version>` 一致 → 发布。详见 `docs/operations/release-checklist.md`。

## [Unreleased]

### Changed

- **Web 管理控制台领域化重构**：按「工作台 / 接入与扩展 / 运行与观测」重塑 3 组 8 项一级导航，独立拆分为 **模型供应商**、**消息平台**、**插件中心** 与五分区 **系统配置**（含 `system.*` 核心系统包只读总览表），不再将所有后端包平铺至单一能力卡片墙；统一模型配置术语为「模型供应商」。
- **模块化设计系统与沙箱主题同步**：将前端样式拆分为 6 层（`tokens` / `reset` / `layout` / `components` / `responsive` / `base`），新增 `UiSwitch`（44×24px WCAG 2.2 达标）、`UiTabs`、`UiBanner`、`UiConfirmDialog`、`UiEmptyState` 与右侧抽屉详情；运行时插件沙箱 iframe（表情包、Token 配额等）自动同步宿主深浅主题，并支持从插件卡片一键「打开界面」。
- **平台驱动「接入与策略」包级前端解耦**：将 QQ 私域、QQ 官方开放平台、飞书与本地调试 4 个驱动的标量字段、秘密槽位（飞书专属槽位 3/4/5）、字段标签/提示、凭据说明与策略输入文案全部下沉至各自 `packages/platform-*/frontend/presentation.ts`；「消息平台」页移除页头聚合按钮，每张驱动卡片的「接入与策略」直达该驱动专属配置抽屉，并按 `platformId` 切片展示与保存 `platformPolicies` 条目（系统配置页的 `system.platform-*` 跳转同步直达对应驱动抽屉）。
- **模型供应商 API 地址与密钥集成**：将模型 `API 根地址`（`modelBaseUrl`）、`默认模型`（`model`）与 `API Key`（`Slot 0` 与独立凭据）全部收归对应模型供应商卡片与 `ProviderEditorDialog` 内联管理；`primary` 直接管理主模型地址、默认模型与 Slot 0 密钥并锁定优先级/启用态/删除，非主供应商默认采用独立 API Key 并自动派生 `credentialKey`（支持在折叠高级区自定义或复用已有键）；页头「路由与采样参数」（`scope="models"`）精简为纯路由与采样设置，移除重复的主模型 URL/密钥及旧注册表。
- **13 项对话功能插件迁出宿主至官方市场**：将 13 个 `preset.feature.*` 功能插件（`digest`、`documents`、`knowledge`、`music`、`personal-todo`、`poke`、`profiles`、`scheduler`、`stickers`、`token-quota`、`vibes`、`voice`、`websearch`，首版 `1.1.0`）迁出主仓库并移交官方市场仓库 `ZhaoJun233/bot-agent-plugins` 独立发布；宿主内建发行包精简为 **9 个 `system.*` 核心与平台包**，新实例默认 0 预装功能插件且启动零自动下载，需在 `/web/`「插件中心」按需安装；新增 `OfficialPresetPolicy` 保持对既有 `1.0.0` `host-distribution://` 状态封套、历史配置与 `tenant_quotas` 账本的平滑兼容与升级/卸载，并将历史配额迁移 SQL（`preset.token-quota.14-15.{up,down}.sql`）迁入 `src/BotAgent.Storage/Schema/`，静态前端呈现注册与配额共享类型迁入 `contracts/ui/{presentations,quotas}/`。
- **全站操作与观测打磨**：顶栏常驻全局管理员模式切换；系统配置提供多行文本域与字符计数；系统概览组件表支持按领域直达下钻；轨迹页新增节点级耗时明细抽屉；运行日志终端新增自动换行切换与同步状态提示。

### Fixed

- 修复 `SettingsStore.PrepareRegisteredProviders` 在同一配置事务中校验新登记或更新的模型供应商时未先观测本次 `proposedMutations` 新写入的独立 `ModelCredentialKey` 导致单步新建/换键必报 `credential_required` 的问题，并对齐 `Ollama` 无密钥启用的校验规则；同步修复前端配置提交错误摘要未透传结构化 `result.errors`（如 `endpoint_authorization_required` / `credential_required`）的问题。

- 修复平台访问策略编辑器在禁用规则（`actionKind` 1→0）时的校验死锁问题、嵌入配置页重复元信息渲染、「消息平台」未决提交恢复横幅 target 匹配遗漏及空 `instanceId` 卡片尾部悬空斜杠展示。
- 修复右侧抽屉入场动画瞬时越过视口右边界、Firefox 品牌标识次像素高度偏差与 `restart.spec.ts` 等待宿主完全就绪竞态。

## [1.4.0] - 2026-11-10

### Added

- 统一记忆首版：本人确认有限偏好、逐目标共享授权、版本修改、撤销和遗忘；功能默认关闭，治理身份单独配置（#153）。
- 可选回复节奏：群聊与私聊的连续发言等待、群聊参与概率与冷却、全局容量约束；保存设置后启用（#151）。
- 扩展目录支持统一展示及受控的托管/Python 包安装、启用、停用、更新、回退和卸载；Python 包使用标准库依赖模式（#152）。
- Web 任务页可导出有容量上限的本次客户端任务统计，并标明受理、重放、未知结果和测量缺口。

### Changed

- 系统设置与插件设置统一到对应能力入口，补齐运行反馈、独立前端部署说明和任务恢复操作指南。
- 整合主分支依赖：ModelContextProtocol.Core 2.4.0、Vue 3.5.45、Vite 8.3.6、Vitest 5.1.0、Playwright 1.65.0；CI 使用 setup-dotnet 6.1.0。
- 前端开发环境要求 Node.js ^22.22.2、^24.15.0 或 >=26.0.0。TypeScript 更新至 6.0.3，配合 vue-tsc 3.3.14；类型检查和构建保留编译器兼容性检查及 TypeScript <7 约束（#142、#160）。
- 纳入 .NET 依赖补丁及 GitHub Actions 更新；常规依赖更新统一向 dev 提交（#159、#161、#162）。

### Fixed

- 配置提交遇到不完整或矛盾响应时保留草稿与原操作键；可在刷新后手动查询既有回执，避免误判保存状态。
- 任务回执文件无法读取或已初始化记录丢失时，停止新的任务受理并显示可判断的恢复状态。
- 任务取消和结果读取分别维护进行状态，一方完成不会提前开放另一方的重复请求。
- 决定请求期间切页、切换模式或写入状态后保留结果未知；锁定或登录失效后，同身份重连也保留限制，等待原请求结束、重新读取队列后才允许新的决定（#162）。
- 扩展操作及声明式设置遇到含糊 HTTP 响应时保留原操作键，先查询原回执，再判断后续操作（#162）。
- 扩展依赖校验覆盖运行时、精确版本与待处理状态，避免接受不符合运行条件的包组合（#162）。
- Docker 健康检查与宿主端口配置保持一致；源码镜像构建引入根版本属性，避免程序集版本回落（#162）。
- 已结束的扩展操作允许正常离开页面；统一记忆遗忘与回复节奏共享容量的处理保持一致。

### Upgrade notes

- 后端继续使用 .NET 10；Linux x64 + Docker 采用预构建产物及独立 Web 资源。升级前按[发布检查单](docs/operations/release-checklist.md)留存可恢复的应用、数据库与前端版本。
- 从公开 v1.3.0 升级时，物理数据库结构由 13 迁移至 14。旧宿主不能直接打开新版结构；回退必须恢复匹配的数据库快照，不能只替换二进制。
- 统一记忆和回复节奏的启用仍由各自设置决定。固定示例包验证不代表完整市场确认；Python 进程生命周期管理不提供文件或网络隔离。
- 实际使用观察暂缓。客户端统计、任务完成状态与合成回归不代表实际用途、费用或维护时间结论。

## [1.3.0] - 2026-11-03

.NET 10 LTS 升级与跨版本回滚验收、仓库治理与文档基线（#101–#125、#127）、模型配置与白名单两个「改了不生效」
的端到端修复（#103/#104）、三条核心链路职责解耦与四条不变量回归（#106/#108/#112/#126）、Agent Runtime
（任务生命周期与台账、统一工具注册表、任务级前置审批与票据验签、取消/超时/恢复、部署验证回滚文档，#114–#119）、
输入缓存观测与控制参数、面板只读轨迹页（#98/#135/#138）。

完整说明见 [v1.3.0 Release](https://github.com/ZhaoJun233/bot-agent/releases/tag/v1.3.0)。

[Unreleased]: https://github.com/ZhaoJun233/bot-agent/compare/v1.4.0...HEAD
[1.4.0]: https://github.com/ZhaoJun233/bot-agent/compare/v1.3.0...v1.4.0
