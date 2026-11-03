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

（暂无）

## [1.3.0] - 2026-11-03

.NET 10 LTS 升级与跨版本回滚验收、仓库治理与文档基线（#101–#125、#127）、模型配置与白名单两个「改了不生效」
的端到端修复（#103/#104）、三条核心链路职责解耦与四条不变量回归（#106/#108/#112/#126）、Agent Runtime
（任务生命周期与台账、统一工具注册表、任务级前置审批与票据验签、取消/超时/恢复、部署验证回滚文档，#114–#119）、
输入缓存观测与控制参数、面板只读轨迹页（#98/#135/#138）。

完整说明见 [v1.3.0 Release](https://github.com/ZhaoJun233/bot-agent/releases/tag/v1.3.0)。

## [1.2.0] - 2026-10-03

插件注册表契约与微内核插件架构（#70）、主模型与快速档独立的思考预算与滑块（#72/#74）、
控制台免密快速启动、Linux 原生常驻进程管理、控制台视觉与移动端适配。

完整说明见 [v1.2.0 Release](https://github.com/ZhaoJun233/bot-agent/releases/tag/v1.2.0)。

[Unreleased]: https://github.com/ZhaoJun233/bot-agent/compare/v1.3.0...HEAD
