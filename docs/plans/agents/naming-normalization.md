# 命名规范化（N0 清单与 N1 兼容层）

> 状态：N0/N1 完成；N2（内部类型）、N3（持久化契约）未做。只做兼容性改名，不改持久化平台 ID / 会话 key / schema。
> 基线：HEAD `1f3181e`，Wave2 工作树归档见 `.git/agent00-naming-20261017/`（`source-baseline.tar.gz`，494 文件，SHA-256 `5404a8a51dbb7a816d9f385b08a099b09f8297d3debd96ee56b38d95ad7aec9e`）。
> 不在公开快照中发布本文件。

## 1. 分类

| 类别 | 处理 | 例子 | 原因 |
| --- | --- | --- | --- |
| 展示/文档名 | N0/N1 改 | `PROJECT_STATUS.md` 架构图、概述、README 说明 | 不影响运行时；去品牌残留 |
| 规范别名 | N1 改 | `BOTAGENT_*` 环境变量优先、`QQCHAT_*` 回退；`botagent.db` 优先、`qqchat.db` 回退 | 新建使用规范名，旧部署继续可用 |
| 内部类型/命名空间 | N2 延后（与 Wave3 合并） | `QqChatMessage`、`IQqChatSource`、`QqActionCatalog`、`BotAgent.Core.Domain.Qq` | FQN/反射/测试兼容；Wave3 本来会移动文件 |
| 持久化契约 | N3 不改 | `qq.private` / `qq.official` / `feishu` / `local`、`group:` / `official:` / `feishu:` / `local:`、表名、`secret:apiKey`、`env:QQCHAT_API_KEY` | 改了就破坏已有数据、备份和跨版本兼容 |
| 运维名 | 文档说明，暂不改 | `/opt/qqchat`、compose 服务/网络 `qqchat`、镜像/容器名、旧仓库名 `qqchat-agent` | 需要部署窗口与回滚；不属于代码命名 |

## 2. 数据库文件名兼容矩阵（N1）

| 场景 | 结果 |
| --- | --- |
| 显式 `AppDatabase.Initialize(path)` | 使用给定路径（测试/运维覆盖） |
| `{DataDir}/botagent.db` 存在 | 使用 `botagent.db` |
| 仅 `{DataDir}/qqchat.db` 存在 | 使用 `qqchat.db`（只读回退，不自动复制） |
| 两者都不存在 | 新建 `botagent.db` |
| 两者都存在 | 优先 `botagent.db`；旧库保留不删 |

- `qqchat.db` 的自动迁移/复制不在 N1 做；需要独立迁移命令、备份、迁移标记和回滚演练。
- `ReplyAuditRules` 同时拦截 `qqchat.db` 与 `botagent.db`。
- 备份/恢复脚本与文档必须同时识别两个名字。

## 3. 环境变量别名（N1）

已存在 `BOTAGENT_*` 优先、`QQCHAT_*` 回退的入口：数据目录、模型 Key/Base URL/模型名、快速档、OneBot Token/协议/地址、健康端口、面板 Token、Agent Token、NapCat WebUI、飞书 Secret/EncryptKey/VerificationToken、Verbose、模型 override。

N1 补齐的 legacy-only 名称：

| 规范名（优先） | 兼容名（回退） | 用途 |
| --- | --- | --- |
| `BOTAGENT_LOG_FILE` | `QQCHAT_LOG_FILE` | 是否写日志文件 |
| `BOTAGENT_MODEL_TIMEOUT_SECONDS` | `QQCHAT_MODEL_TIMEOUT_SECONDS` | 聊天模型超时 |
| `BOTAGENT_VOICE_EMOTION` | `QQCHAT_VOICE_EMOTION` | 语音情绪 |
| `BOTAGENT_AGENT_SERVER_KEY` | `QQCHAT_AGENT_SERVER_KEY` | 服务器内置 agent 密钥 |
| `BOTAGENT_OFFICIAL` | `QQCHAT_OFFICIAL` | 官方通道开关 |
| `BOTAGENT_OFFICIAL_APP_SECRET` | `QQCHAT_OFFICIAL_APP_SECRET` | 官方平台 Secret（仍只读直接环境变量） |
| `BOTAGENT_ALLOW_PRIVATE_IMAGE_HOSTS` | `QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS` | 允许私网图片/链接（默认关） |
| `BOTAGENT_HEALTH_BIND` | `QQCHAT_HEALTH_BIND` | 面板/健康检查绑定地址 |

规则：规范名非空优先；两者都空用默认；不删除旧名；密钥不回显、不落日志。

## 4. N1 验收（已完成，2026-10-18）

- 纯函数/隔离测试：`NamingCompatibilityTests` 覆盖 DB 解析四场景（空根→canonical、仅 legacy→legacy、两者→canonical、仅 canonical→canonical）、env 规范名优先/旧名回退、`ModelTimeout` 别名、出站审计拦截两个库名。
- 实跑：Headless build exit 0；Review **1069/0**；Architecture **165/0**（文件 IO **59**）；Safety **594/0**；Chaos **43/0**；Frontend **370/0**；SettingsScope/FeishuRemediation/ReverseTransport 通过；S21 **41/0**、S36 **98/0**（含旧库回退）、S50 **14/0**、S52 **119/0**、S33 **59/0**。
- 不改 `qq.private` / `qq.official` / `feishu` / `local`、会话 key 前缀、DB schema、secret ref、API 路由。

### 4.1 测试与命名（Wave3 同步）

- 测试项目统一 `BotAgent.*`；Wave3 新增 `tests/BotAgent.EngineProbe`，覆盖插件门禁与有界后台队列。
- 测试基础设施变量支持规范名优先、旧名回退：`BOTAGENT_IT_ONLY` / `QQCHAT_IT_ONLY`（单场景选择）、`BOTAGENT_SRC_ROOT` / `QQCHAT_SRC_ROOT`（架构探针源码根）。
- 集成场景仍通过 `QQCHAT_*` 行为变量驱动子进程：这是 N1 的**行为配置兼容面**，不是遗漏；规范基础设施名已在 Host 侧优先。
- DB 相关测试同时覆盖 `botagent.db`（canonical）与 `qqchat.db`（legacy fallback）；出站审计测试同时验证两个库名被拦截。
- 测试只使用合成数据与隔离临时根；不读取真实群聊、成员、DB 正文或生产日志。

## 5. 未做

- `botagent.db` 自动迁移/复制、日志文件名 `botagent.log` 的切换、compose/容器/`/opt/qqchat` 改名。
- `Qq*` 类型/命名空间整体重命名（Wave3 已完成文件移动但未做 N2 全局改名；持久化契约仍按 N3 不改）。
- 平台 ID / 会话 key / 表名 / secret ref 重命名（N3，原则上不做）。

> 2026-10-18 独立验收整改（F1–F9）只新增可选参数 `CancellationToken`、`BackgroundJobStatus.Abandoned` 与 `QueueSnapshot.Abandoned`，没有扩大 N2/N3 范围，也没有改任何持久化名字。
