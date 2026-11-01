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

当前 `<Version>` 为 **1.3.0**（开发中，尚未打 tag）。

### 新增

- 插件市场与自定义模型提供商端到端落地：宿主侧注册表 + 面板安装/启停/凭据录入。
- 自然对话驱动 agent（`//` 指令保留），含停止/进度归一匹配与日志脱敏。
- Web 控制台：能力管理收敛、任务提交与审批事件恢复、插件中心、Token 配额插件与主题、
  移动端适配与焦点/对比度修复。
- 平台策略结构化编辑器（#104）：在面板里直接编辑策略条目的目标、开关、名单与功能覆盖，
  只提交改过的行。
- 发布与版本治理（#122）：`/healthz` 暴露 `version`、`--version` 与启动横幅都读程序集、
  `CHANGELOG.md`、dependabot、基镜像钉 digest、CI 产出依赖快照。
- 文档：接口面地图 `docs/api-surface.md`（37 条 ZBA + 50 条旧 `/api/*`）、
  `CONTRIBUTING`/`SECURITY` 中英双版、基线报告与回滚预案、.NET 10 升级验收报告。
- 核心不变量回归（#126）：S56 每会话 FIFO / S57 静默短路在发送前 / S58 引用只挂第一条 /
  S59 语音与文字不重复播报，四个场景进 CI 矩阵；`PipelineEval` 新增确定性决策链性能基线
  （`tests/fixtures/pipeline/baseline.json`，口径见 `docs/baseline/pipeline-performance-baseline.md`）。
- 运维基线（#121）：日志按天轮转并保留 7 天（单文件 10 MiB 或跨天滚动、旧档 gzip，
  规则抽成 `LogRotation` 并有 10 条探针用例）；库与热备产物一律 `600`；宿主启动 10 分钟后自动每日热备
  （保留 3 份、同一天不重复、同一时刻只跑一个，结果写 `meta` 表），面板新增 `GET/POST /api/backup`；
  `/status` 与 ZBA status 暴露库/日志/最近备份/内存与 swap/启动次数，总览页对「备份过期或失败」显示红条；
  compose 给 `qqchat`（768m/512）与 `napcat`（512m）加上限，并修掉重复 `volumes:` 键导致的
  `docker compose config` 解析失败（特权挂载见 #131）。恢复演练落成集成场景 S60
  （备份能独立打开、`user_version` 一致、放回去能起来）。

### 变更

- **升级到 .NET 10**（#102）：30 个工程 `TargetFramework=net10.0`，`global.json` 钉 SDK 10.0.101，
  插件 TFM 白名单同步放宽。
- 构建基线收敛到 `Directory.Build.props`（#123）：LangVersion / Nullable / ImplicitUsings /
  Deterministic / NuGet 审计，并由探针强制「只接受探针解释得了的属性」。
- 模型配置（#103）：凭据来源改为**请求级**材料，面板里改 API Key 或换供应商后不再进入安全静默，
  下一次请求即打到新端点。
- 平台策略（#104）：面板改的标量（开关、白名单文本、本地通道）会同步进策略条目，
  旧面板与新 ZBA 面板两条保存路径都生效。
- 旧内置前端随 S6 删除，面板只剩 `src/web` 的 Vue 工程；宿主 `/` 不再提供页面。

### 修复

- 集成 harness 的两类偶发失败：S12 写死 `18085` 被别的进程占成临时端口导致面板没起来（#105）、
  S26 假模型服务端口被占导致整轮 harness 崩溃（#104 S2 期间）。
- B24 探针的子进程清理改成有界等待，修掉 Linux CI 上的偶发假红（#105）。
- 文档死链归零；README 配置参考重写成「compose 层 / 宿主层 / 面板与库」三层模型（#125），
  删掉不存在的 `/playground.html` 与「宿主自动生成证书」两处过期声明。

### 安全

- 新增 `SECURITY.md` / `SECURITY.en.md`：写明插件无沙箱、SHA256 只证字节完整性、
  没有 ZBA 令牌时管理端点失败关闭（仅 `/zba/v1/health` 可读）。
- NuGet 审计 `high`/`critical`（NU1903/NU1904）作为构建错误，作用于全部工程。

## [1.2.0] - 2026-10-03

插件注册表契约与微内核插件架构（#70）、主模型与快速档独立的思考预算与滑块（#72/#74）、
控制台免密快速启动、Linux 原生常驻进程管理、控制台视觉与移动端适配。

完整说明见 [v1.2.0 Release](https://github.com/ZhaoJun233/bot-agent/releases/tag/v1.2.0)。

## [1.1.1] - 2026-10-02

动作描写语义过滤修复、公式保护、运维文档重构。

完整说明见 [v1.1.1 Release](https://github.com/ZhaoJun233/bot-agent/releases/tag/v1.1.1)。

[Unreleased]: https://github.com/ZhaoJun233/bot-agent/compare/v1.2.0...HEAD
