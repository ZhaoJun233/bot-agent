# Bot Agent

简体中文 | [English](README.en.md)

[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Linux%20(Ubuntu%20%7C%20Debian%20%7C%20CentOS%20%7C%20Arch)%20%7C%20Docker-green.svg)](#快速开始)
[![Protocol](https://img.shields.io/badge/Protocol-OneBot%20v11%20%7C%20QQ%20Official%20%7C%20Feishu-purple.svg)](#系统架构)
[![License](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)

Bot Agent 是面向 Linux 环境构建的无界面、高性能、可插拔多聊天平台 Agent 常驻服务。服务支持 QQ 私域（[NapCat](https://github.com/NapNeko/NapCatQQ) / OneBot v11）、QQ 官方开放平台、飞书应用机器人（Feishu Bot / WebSocket 与 Webhook）、零外部依赖本地演练场通道以及本人专属 Web 工作台（`workspace:owner`），与 OpenAI / Anthropic / Gemini 兼容格式的大语言模型对接，实现群聊、私聊与工作台场景下的自主研判、受控记忆与拟人化回复，并配套独立静态部署的 Web 管理控制台。

项目支持在标准 Linux 发行版直接运行源码/产物，支持 Docker Compose 容器编排，也提供面向低内存（1 GB）主机的纯产物容器镜像 [`Dockerfile.app`](Dockerfile.app)。

---

## 核心文档导航

| 文档 | 内容说明 |
| :--- | :--- |
| [文档总目录（`/docs`）](docs/index.md) | 业务规则、数据库设计、运维手册、架构边界、ADR 决策与术语表的统一文档入口 |
| [核心业务规则与领域说明](docs/domain/core-business-rules.md) | 多平台入站信任边界、三层配置与会话 Profile 裁决、回复节奏与静默短路、记忆敏感度矩阵、Agent 任务状态机与调度/待办/身份绑定规则 |
| [数据库设计文档](docs/storage/database-design.md) | SQLite WAL 运行参数、Schema v1–v15 演进、ER 关系图、31 张业务物理表结构、v15 隔离 `meta` JSON 账本与文件布局 |
| [运维排障与应急手册](docs/operations/runbook.md) | 核心监控指标与隐私安全查日志规范、故障“现象 → 排查 → 解决”对照表、回滚预案决策流 |
| [工程规范与 ADR 索引](docs/decisions/engineering-standards-and-adr.md) | 仓库目录拓扑、编码规范与 `ArchitectureProbe` 护栏、Git 协作/发版流程、技术选型依据与 ADR 0001–0006 索引 |
| [接口地图](docs/api-surface.md) | 面板旧接口、ZBA v1 与内置/插件路由总表（与 `PanelRoutes.cs` 同步受护栏断言约束） |
| [核心术语表](docs/glossary.md) | 领域术语、安全不变量与组件专有名词定义 |
| [Linux Docker 首次安装与配置](docs/operations/linux-docker-first-run.md) | 低内存主机产物构建、独立 Caddy 前端与受控首次验收步序 |

---

> [!CAUTION]
> ### 账号安全与风控防范警示
>
> 鉴于即时通信平台对机器人账号的严格风控策略，根据项目贡献者的实际封禁遭遇，请务必注意以下运行环境风险：
>
> 1. **避免在移动端与变动网络下运行**：切勿在安卓手机本地、Termux 终端或频繁切换的蜂窝数据基站网络下常驻运行协议端。此类环境下高频调用通信 API 极易被风控系统识别为异常客户端，导致账号面临永久封禁或高频冻结。
> 2. **推荐部署方案**：建议将服务部署在受信任的异地数据中心（云服务器 / VPS）、固定 IP 的家用小型主机或运行标准 Linux（Ubuntu / Debian 等）的独立设备上，保持网络出口纯净稳定。
> 3. **生产与调试隔离**：严禁使用个人主力生活账号或工作账号作为机器人端点，测试期间建议使用专用小号，或优先采用 [QQ 官方开放平台通道](#qq-官方开放平台可选) 以获得合规稳定的运行保障。

---

## 核心特性

### 智能研判与自然交互
- **发言时机与回复节奏统一调度**：由 [新版回复节奏](docs/reply-pacing.md) 统一管理 `GroupInterjectionProbability`、`GroupInterjectionCooldownSeconds`、群/私聊续话静默等待与适合度阈值（`SuitabilityThreshold`），明确请求直达优先，静默判定在发送前短路。
- **分层记忆与会话轻量知识库**：以 [ADR-0004](docs/decisions/0004-unified-memory-authorization.md) 与 [ADR-0005](docs/decisions/0005-memory-tool-coexistence-taint-tiers.md) 实现本人逐事实、逐版本、逐目标会话授权与敏感度 × 工具流出面 Fail-Closed 矩阵；支持同会话隔离知识库（纯托管 BM25 检索，单作用域上限 256 篇 / 4096 切片 / 1 MiB）。
- **动作描写语义过滤与多模态感知**：过滤舞台旁白同时保留公式、代码与链接；内建视觉图片缓存与时效 URL 重签、双向戳一戳、带魔数/路径校验的表情包安全库、精确上下文引用、音乐卡片歌词与波形分析、拟真分句与云端 TTS 语音条（音文不重复播报）。

### 多平台接入、插件解耦与控制台工作区
- **宿主极简内置包 + 市场按需扩展**：宿主仅内置 **9 个 `system.*` 核心与平台驱动包**（4 个平台驱动包：QQ 私域、QQ 官方、飞书、本地演练场；5 个系统能力包：模型服务、内置 Agent、MCP 工具管理、对话编排、健康日报）。其余 **13 个 `preset.feature.*` 对话功能插件**（Token 配额、联网搜索、戳一戳、氛围、表情包、点歌、语音、画像、文档、摘要、知识库、定时任务、个人待办，`1.1.0+`）已迁至官方插件市场仓库 `ZhaoJun233/bot-agent-plugins`，新实例默认 0 预装功能插件且启动不自动联网下载，在 Web 控制台 `/web/` 的「插件中心」按需安装；已有实例升级后继续通过 `OfficialPresetPolicy` 兼容历史 `1.0.0` 状态封套、设置键与配额账本，支持热装/启停/升级/回滚，失败时保留旧实例。
- **会话级 Profile 与跨平台 RBAC / 审批**：支持为单会话覆盖对话与工具分层，配置变更后实时刷新能力指纹；内置跨平台 `WorkspaceRole` 与非只读插件工具前置人工审批闭环，可选开启双向 120s HMAC 挑战确认的跨平台本人身份绑定（[ADR-0006](docs/decisions/0006-optional-cross-platform-identity-linking.md)）。
- **个人工作区与沙箱插件界面**：内置严密隔离的本人 Web 工作台（`workspace:owner`），支持定时任务、个人待办等插件以沙箱 iframe 运行并在同一主题与管理员模式下安全通信。

---

## 快速开始

首次生产/Linux Docker 部署请先阅读 [Linux Docker 首次安装与配置](docs/operations/linux-docker-first-run.md)。版本升级前务必按 [回滚预案](docs/operations/rollback-playbook.md) 留存双份回滚点；任务提交/审批结果不明时按 [任务恢复指南](docs/operations/task-recovery-guide.md) 核对。

### 环境依赖

| 组件 | 本地开发 / 构建机要求 | 服务器运行时要求 | 用途 |
| :--- | :--- | :--- | :--- |
| .NET SDK / Runtime | **.NET 10.0 SDK** | Docker 镜像内置 Runtime（或本机 .NET 10 Runtime） | 编译/运行 `BotAgent.slnx` 与 `BotAgent.Headless` |
| Node.js & npm | **Node.js `22.22.2+` / `24.15.0+` / `26+`** | 无需（仅需静态 Web 服务器） | 构建/校验 `src/web` Vue 3 控制台前端 |
| Python | **Python 3.12+** | 仅本地诊断脚本需用 | 运行 `BotAgent.ServerFilesProbe`、`mask-cjk.py`、`push-desensitized.py` |
| 容器与反代（容器路线） | Docker（可选） | Docker Compose + Caddy / Nginx | 运行容器镜像与单独托管 `/web/` 静态资源 |
| 协议端（可选） | — | [NapCat](https://github.com/NapNeko/NapCatQQ)（QQ 私域时） | 提供 OneBot v11 Forward/Reverse WS 连接 |

### 方式一：本地开发与源码调试（前后端分离）

```bash
# 1. 还原并构建完整解决方案（含高危 NuGet 审计）
dotnet restore BotAgent.slnx
dotnet build BotAgent.slnx -c Release

# 2. 复制并配置环境变量（本地开发可通过 BOTAGENT_ZBA_ALLOW_LOOPBACK=1 简化回环 ZBA 调试）
cp .env.example .env

# 3. 启动无界面后端宿主（默认监听 127.0.0.1:18245；宿主不再内置 HTML 页面）
dotnet run --project src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# 4. 在另一终端启动前端开发服务器（代理 /api 与 /zba 到后端端口）
cd src/web
npm ci
npm run dev
```

只构建静态产物时在 `src/web` 执行 `npm run build`，产物输出至 `src/web/dist`，交由 Caddy / Nginx 以 `/web/` 挂载提供服务（详见 [`src/web/README.md`](src/web/README.md)）。

### 方式二：Linux 原生控制脚本（无 Docker）

```bash
cp .env.example .env
# 编辑 .env（填入 MODEL_API_KEY、WHITELIST 等）

./start.sh        # 后台启动（日志写入 runtime/logs/bot-agent.log）
./start.sh -f     # 前台调试启动
./status.sh       # 查看 PID、/healthz 与 /readyz 状态及最近 10 行日志
./stop.sh         # 安全停止服务
```

服务启动后：健康探针 `http://127.0.0.1:18245/healthz` 返回 `{"status":"ok","version":"x.y.z"}`（版本号统一来自 `Directory.Build.props`；容器路线默认端口通常设 8080）。

### 方式三：Docker Compose / 低内存主机产物镜像部署

- **标准容器编排**（含 NapCat 与源码构建）：
  ```bash
  cp .env.example .env
  # 编辑 .env 后启动
  docker compose up -d
  docker compose logs -f napcat   # 首次登录扫码
  ```
- **低内存主机（~1 GB 内存）推荐产物路线**（开发机 `dotnet publish -r linux-x64` 打 `app.tar.gz`，服务器使用根目录下 [`Dockerfile.app`](Dockerfile.app) 解压构建，避免服务器上 `dotnet publish` 挤爆 swap）：完整步骤见 [Linux Docker 首次安装与配置](docs/operations/linux-docker-first-run.md)。

---

## 配置参考

配置采用**两层环境变量 + 一个数据库真源**的三层所有权模型，分不清这三者是“改了不生效”的最常见原因：

| 层 | 位置 | 谁拥有 | 生效时机 |
| :--- | :--- | :--- | :--- |
| ① Compose 层 | 根目录 `.env` | 部署者 | 只用于 `docker-compose.yml` 的 `${VAR}` 替换；**不会自动进容器**，新增变量必须同步写入 compose 的 `environment:` |
| ② 宿主层（环境变量） | 容器 `environment:` / 进程环境 | 宿主启动时读取 | **密钥**（永不落盘 `settings`）与**基础设施**（模型端点/协议地址/监听端口）**始终覆盖**，改完需重启；支持 `{NAME}_FILE` 读取 Docker Secret 文件 |
| ③ 数据库真源层 | `data/botagent.db`（兼 `data/settings.json`） | 面板保存后独占 | **行为类配置**（人设、白名单、阈值、节奏、开关…）首次部署以环境变量作种子，面板保存后即由库独占，之后改 `.env` 不生效（日志会打印已覆盖提醒） |

> [!WARNING]
> 严禁将仓库默认 `docker-compose.yml` 直接覆盖已有部署目录，否则可能丢失 `ACCOUNT` 等手工配置导致 NapCat 掉登录态需重新扫码。完整 200+ 键名模板见 [`.env.example`](.env.example)。

### 核心宿主环境变量说明表

规范前缀 `BOTAGENT_*` 优先级最高，`QQCHAT_*` / `OPENAI_*` 自动回落兼容；密钥类变量均优先查找 `{NAME}_FILE`。

| 规范名（优先） | 兼容旧名 | 默认值 / 示例 | 作用域 / 生效说明 |
| :--- | :--- | :--- | :--- |
| `BOTAGENT_DATA_DIR` | `QQCHAT_DATA_DIR` | `./runtime` 或容器 `/data` | 运行时数据根路径（单次绑定，下含 `data/botagent.db`、日志与插件） |
| `BOTAGENT_API_KEY` | `QQCHAT_API_KEY`、`OPENAI_API_KEY` | —（必填，支持 `_FILE`） | 主模型 API Key，只读环境或面板 `secrets` 表，不进 `settings.json` |
| `BOTAGENT_BASE_URL` | `QQCHAT_BASE_URL`、`OPENAI_BASE_URL` | `https://api.openai.com/v1` | 主模型兼容 API Base URL（面板显式覆盖未设置时由环境覆盖） |
| `BOTAGENT_MODEL` | `QQCHAT_MODEL`、`OPENAI_MODEL` | `gpt-4o-mini` | 主模型标识 |
| `BOTAGENT_FAST_MODEL` / `BOTAGENT_FAST_REPLY` | `QQCHAT_FAST_MODEL` / `QQCHAT_FAST_REPLY` | 空 / `false` | 聊天快速回复轻量档模型（首次引导后可由面板热调整） |
| `BOTAGENT_QQ_ONEBOT_PROTOCOL` | `QQCHAT_ONEBOT_PROTOCOL` | `ForwardWebSocket` | OneBot 协议模式：`ForwardWebSocket` / `ReverseWebSocket` |
| `BOTAGENT_QQ_ONEBOT_URL` | `QQCHAT_ONEBOT_URL`、`QQCHAT_ONEBOT_ADDRESS` | `ws://127.0.0.1:3001` | 协议端 WebSocket 地址 |
| `BOTAGENT_QQ_ONEBOT_TOKEN` | `QQCHAT_ONEBOT_TOKEN` | 空（支持 `_FILE`） | OneBot 鉴权令牌 |
| `BOTAGENT_QQ_UIN` | `QQCHAT_UIN`、`QQCHAT_QUICK_LOGIN_UIN` | 空 | 机器人 QQ 号（留空则在连上协议端后自动获取） |
| `BOTAGENT_HEALTH_PORT` | `QQCHAT_HEALTH_PORT`、`HEALTH_PORT` | 原生 `18245` / 容器 `8080` | 面板 API、ZBA 与 `/healthz` 监听端口（`0` = 关闭） |
| `BOTAGENT_HEALTH_BIND` | `QQCHAT_HEALTH_BIND` | `127.0.0.1` | 绑定地址（反向代理同容器/跨网络可设 `+`；无密码时强制回环） |
| `BOTAGENT_PANEL_TOKEN` | `QQCHAT_PANEL_TOKEN` | 空 | 旧面板 `/api/*` 访问令牌 |
| — | `QQCHAT_PANEL_PASSWORD` | 空 | 旧面板首次启动登录口令种子（首次写入后改为面板修改） |
| `BOTAGENT_ZBA_TOKEN` | `QQCHAT_ZBA_TOKEN` | 空 | ZBA 管理员 Bearer 令牌（写操作必需；未配置时管理端点 fail-closed `503`） |
| `BOTAGENT_ZBA_READ_TOKEN` | `QQCHAT_ZBA_READ_TOKEN` | 空 | ZBA 只读 Bearer 令牌（CLI 与只读探测用） |
| `BOTAGENT_ZBA_ALLOW_LOOPBACK` | `QQCHAT_ZBA_ALLOW_LOOPBACK` | `0`（关闭） | `1` 时允许无令牌回环访问 ZBA（**仅限本机开发调试**） |
| `BOTAGENT_ZBA_URL` | — | `http://127.0.0.1:<port>/zba/v1` | `--status` 等 ZBA 只读 CLI 命令默认连接基址 |
| `BOTAGENT_AGENT_TOKEN` | `QQCHAT_AGENT_TOKEN` | 空（支持 `_FILE`） | 内置服务器 Agent 令牌 |
| `BOTAGENT_AGENT_SERVER_KEY` | `QQCHAT_AGENT_SERVER_KEY` | 空（支持 `_FILE`） | Agent 子任务专用模型密钥覆盖 |
| `BOTAGENT_NAPCAT_WEBUI_URL` / `BOTAGENT_NAPCAT_WEBUI_TOKEN` | `QQCHAT_NAPCAT_WEBUI_*` | `http://127.0.0.1:6099` / 空 | 面板内嵌 NapCat 扫码登录代理目标与令牌 |
| `BOTAGENT_FEISHU` / `BOTAGENT_FEISHU_APP_ID` / `BOTAGENT_FEISHU_WHITELIST` | `QQCHAT_FEISHU*` | `0` / 空 | 飞书通道开关、AppId 与群聊白名单（首次部署种子） |
| `BOTAGENT_FEISHU_APP_SECRET` / `BOTAGENT_FEISHU_ENCRYPT_KEY` / `BOTAGENT_FEISHU_VERIFICATION_TOKEN` | `QQCHAT_FEISHU_*` | 空（支持 `_FILE`） | 飞书凭据与事件加密密钥（Secret 仅读宿主环境或环境变量） |
| `BOTAGENT_VOICE_EMOTION` | `QQCHAT_VOICE_EMOTION` | — | 云端语音情绪默认参数（首次种子） |
| `BOTAGENT_VERBOSE` / `BOTAGENT_LOG_FILE` | `QQCHAT_VERBOSE` / `QQCHAT_LOG_FILE` | `1` / `1` | 详细日志与是否落盘日志文件（`0` 时仅输出 stdout） |
| `TZ` | — | 系统本地或 `Asia/Shanghai` | 运行时默认时区 |

### QQ 官方开放平台（可选）

官方通道开关与凭据默认同样完全隔离（会话带 `official:` 前缀，白名单独立）：

| 键 | 说明 |
| :--- | :--- |
| `BOTAGENT_OFFICIAL` / `QQCHAT_OFFICIAL` | `1` = 启用官方通道 |
| `QQCHAT_OFFICIAL_APP_ID` | 开放平台 AppId |
| `BOTAGENT_OFFICIAL_APP_SECRET` / `QQCHAT_OFFICIAL_APP_SECRET` | AppSecret（仅从环境变量直接读取，面板不接不回显） |

---

## Web 管理控制台

基于 Vue 3 + TypeScript 构建的单页管理控制台，通过 ZBA 协议（`zba-1.0`）与后端宿主通信，以纯静态资产独立挂载于 `/web/`：

- **分域治理导航**：涵盖 **系统概览**、**个人工作区（`workspace:owner`）**、**模型供应商**（支持按供应商集成 Base URL / Key、槽位原子保存、路由优先级与熔断观测）、**消息平台**（按 4 个驱动包独立呈现接入与实例策略抽屉）、**插件中心**（统一管理 9 个内置 `system.*` 包、官方市场 `preset.feature.*` 按需插件与外部包的热装/升级/启停、配置面板及 `origin === "null"` 沙箱 iframe 扩展界面，如表情包、Token 配额、定时任务、个人待办）、**系统配置**（基础行为、会话级 Profile 编排、MCP 服务器管理、跨平台 RBAC、Agent 与健康日报）、**Agent 任务**（提交、审批流、取消与恢复核对）、**轨迹与缓存** 以及 **运行日志**。
- **纯内存会话与安全设计**：Bearer 令牌仅存放浏览器内存，刷新或锁定立即清除；写请求受 CAS 乐观锁、`singleflight` 锁与原操作键/幂等键核对保护；支持深浅主题实时同步至沙箱插件及无障碍键盘导航。

---

## SSL/TLS 与反向代理

宿主自身**只监听明文 HTTP**（`HttpListener`），**不终止 TLS、也不生成证书**。生产 HTTPS 必须由反向代理（Caddy / Nginx）负责，前端静态产物也由反代在 `/web/` 提供，未认证回退禁止把登录页 HTML 以 `200` 缓存返回给 `/web/assets/*.js`。

- **本地自签证书**：`./tools/gencert.sh [域名] [IP]` 生成有效期 1095 天的测试证书至 `runtime/certs/`。
- **生产证书**：使用 certbot / acme.sh 或 Cloudflare + Caddy 托管；证书路径配置在反代侧，宿主不读取证书环境变量。

---

## 系统架构

```text
+-------------------------+            OneBot v11            +----------------------------------+
| OneBot 协议端 (NapCat)   | <---- [Forward WS / WS] ----> | BotAgent.Headless (C# / .NET 10)  |
+-------------------------+                                 |   +-- 多平台网关 (OneBot/官方/飞书) |
                                                            |   +-- 三层配置 & 会话 Profile 解析   |
+-------------------------+          官方 Open API           |   +-- 回复节奏调度与统一记忆矩阵     |
| QQ 官方开放平台          | <----------------------------> |   +-- 任务运行时 / RBAC / 审批闸门  |
+-------------------------+                                 |   +-- 定时任务 / 待办 / 知识库(BM25) |
                                                            |   +-- ZBA v1 控制面 & 本地/工作台通道 |
+-------------------------+       WS / 事件 Webhook         |   +-- 9 内置系统包 + 市场扩展体系    |
| 飞书开放平台 (Feishu)    | <----------------------------> +-----------------+----------------+
+-------------------------+                                                   |
                                                                              v OpenAI / Anthropic / Gemini
                                                            +----------------------------------+
                                                            | 大语言模型与 MCP 外部工具生态       |
                                                            +----------------------------------+
```

---

## 常见本地启动与构建报错排查

| 报错 / 现象 | 原因 | 排查与解决方法 |
| :--- | :--- | :--- |
| 启动即退出：`database_schema_newer` 或 `configuration_schema_newer` | 数据目录中 `botagent.db` 的版本高于当前编译产物支持上限（当前上限 v15 / 配置修订 1） | 切勿手改 `PRAGMA user_version`；使用对应新版本运行，或按 [回滚预案](docs/operations/rollback-playbook.md) 用旧版兼容快照恢复 |
| 调 `/zba/v1/*` 全部返回 `503`（`/zba/v1/health` 正常） | 未设置 `BOTAGENT_ZBA_TOKEN`，触发管理端点 Fail-Closed 保护 | 设置 `BOTAGENT_ZBA_TOKEN`；纯本机开发可临时加 `BOTAGENT_ZBA_ALLOW_LOOPBACK=1` |
| 修改 `.env` 的白名单 / 人设 / 阈值不生效 | 面板或 `settings.json` 已持久化过配置，或 Docker Compose 漏配 `environment:` | 以 Web 控制台保存为准（看启动日志中的「已被面板覆盖」提示）；新增变量同步写入 `docker-compose.yml` |
| 运行 `IntegrationHarness` 时旧逻辑似乎仍在生效 | 仅重新编译 Harness 不会自动触发 `BotAgent.Headless` 重建，Harness 会拉起旧 Headless DLL | 先运行 `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild`，再跑 Harness |
| `dotnet build` 报 `NU1903` / `NU1904` 失败 | `Directory.Build.props` 开启了传递性 NuGet 高危漏洞即错误门禁 | 升级受影响依赖包版本；禁止私自降级 `NuGetAuditLevel` |
| 独立运行探针退出码 `-1073741502` 或临时目录报错 | `dotnet run` 在受限环境受临时路径或子进程环境影响 | 先 `dotnet build -c Release`，再确保 `TEMP`/`TMP` 指向可写空目录后直接 `dotnet <Probe>.dll` |
| 1 GB 机器运行源码 `Dockerfile` 出现卡死/OOM | 在低内存宿主机容器内执行 `dotnet publish` 挤爆 Swap | 改为开发机交叉发布 `app.tar.gz` + 服务器使用 [`Dockerfile.app`](Dockerfile.app) 解压构建 |
| 访问 `/web/` 白屏且控制台报 `Unexpected token '<'` | 反代/CDN 将 `/web/assets/*.js` 的未认证或 404 请求回退成了 HTML 200 并缓存 | 确保 `/web/assets/*` 缺失资源严格返回 404、`no-cache`，未认证跳转走 `303` 且带 `Cache-Control: no-store` |

---

## 测试与质量验证

> [!TIP]
> 跑合成测试前请先清理污染环境（`BOTAGENT_*`、`QQCHAT_*`、`OPENAI_*`、`HARNESS_*`、`HEALTH_PORT` 等）。集成 Harness 与独立探针的完整说明及矩阵要求见 [CONTRIBUTING.md](CONTRIBUTING.md) 与 [`.github/workflows/ci.yml`](.github/workflows/ci.yml)。

```bash
# 1. 重建 Headless 宿主（运行集成测试前必须先执行）
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild

# 2. 运行核心架构、安全、ZBA、MCP 与阶段回归探针示例（先 build 后直接运行 DLL 更稳定）
dotnet build tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net10.0/BotAgent.ArchitectureProbe.dll
dotnet build tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet tests/BotAgent.SafetyProbe/bin/Release/net10.0/BotAgent.SafetyProbe.dll
dotnet build tests/BotAgent.ZbaProbe/BotAgent.ZbaProbe.csproj -c Release
dotnet tests/BotAgent.ZbaProbe/bin/Release/net10.0/BotAgent.ZbaProbe.dll

# 3. 运行全量集成场景（支持 s1-s165 已注册合成场景；按场景 ID 定向过滤参数详见 CONTRIBUTING.md）
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll

# 4. 运行前端类型检查、单元测试与 Playwright E2E（在 src/web 下）
cd src/web
npm run typecheck && npm run test:unit
npm run test:e2e -- --project=chromium
```

---

## 开源协议

本项目源码基于 [MIT License](LICENSE) 协议开源。使用的 OneBot 协议端（如 NapCat）请遵循其各自的代码许可与平台规范。
