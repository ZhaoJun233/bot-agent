# Bot Agent

简体中文 | [English](README.en.md)

[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Linux%20(Ubuntu%20%7C%20Debian%20%7C%20CentOS%20%7C%20Arch)%20%7C%20Docker-green.svg)](#快速开始)
[![Protocol](https://img.shields.io/badge/Protocol-OneBot%20v11%20%7C%20QQ%20Official%20%7C%20Feishu-purple.svg)](#系统架构)
[![License](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)

Bot Agent 是面向 Linux 环境构建的无界面、高性能、可插拔多聊天平台 Agent 常驻服务。服务支持 QQ 私域（[NapCat](https://github.com/NapNeko/NapCatQQ) / OneBot v11）、QQ 官方开放平台、飞书应用机器人（Feishu Bot）以及零外部依赖本地演练场通道，与 OpenAI 兼容格式的大语言模型（涵盖 DeepSeek、通义千问、OpenAI、本地 Ollama 等）进行交互，实现群聊、私聊与频道场景下的自主研判与拟人化回复，并内置轻量级 Web 管理控制台。

项目支持在标准 Linux 发行版（Ubuntu、Debian、CentOS、Arch 等）直接基于源码运行，无需强制依赖容器环境；同时保留了标准的 Docker Compose 容器化部署支持。

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
- **发言时机自主决策**：模型根据上下文与设定阈值评估发言必要性，低于阈值保持静默；配合限流冷却算法，避免刷屏与机械式抢话。
- **群聊氛围感知与疲劳阻尼**：自动分析消息密度与群聊情绪，连续发言多次后自适应非线性衰减发言欲望并强制冷却降温，冷清群聊减少插嘴，对线刷屏时主动克制。
- **人物画像与长效记忆**：基于发送者标识构建短期会话栈与长期用户画像（支持证据链溯源与人工设定覆盖），并持久化至嵌入式 SQLite 数据库。
- **动作描写语义过滤**：支持开启过滤模型回复中夹带的舞台旁白描写（如括号动作），由语义模型二次核验保留公式、代码与真实阐述。
- **多模态视觉感知**：自动下载群聊图片并交由视觉模型解析；内建图片字节缓存与针对时效性 URL 的自动重签机制。
- **原生互动深度适配**：
  - **戳一戳响应**：识别双向戳一戳动作，具备冷却门限与自主回戳逻辑。
  - **表情包协同与安全守卫**：自动收录审核表情包、提取语义标签，按语境检索候选；内建 `StickerSafetyGuard` 物理路径穿越防护与真实魔数伪造校验。
  - **上下文精确引用**：自动解析收到的回复引用目标，模型发送时自动关联目标原消息。
  - **音频解析与接梗**：群成员分享音乐卡片时，抓取歌词并进行波形特征分析（节拍、响度与段落），确保回复基于客观事实。
- **拟真分句节拍**：支持按标点符号切分长句并模拟打字延迟分批发送。

### 多平台接入与实例策略
- **统一平台策略总控**：支持 QQ 私域通道、QQ 官方开放平台、飞书机器人及本地通道。控制台「能力管理」中的「平台接入与访问策略」是各平台「启用平台」「启用聊天」的统一入口，同时配置独立群聊/私聊白名单及特性覆盖。关闭聊天可保持连接，关闭平台会阻止聊天；凭据、连接状态、白名单和全局 AI 开关仍是独立安全前置。
- **每日 Token 配额账本**：以预设插件 `preset.feature.token-quota` 运行，按平台与会话来源（SourceKey）划分 UTC 日预算管理，支持按平台筛选租户、查看用量与剩余额度，超额后自动进入节能静默；支持在控制台「能力管理」卡片中热调整额度与持久启停。
- **零依赖本地测试通道**：内置进程内独立通道（无外部长连），在面板「能力管理 → 平台接入与访问策略 → 本地通道」里登记虚拟群号后，用 `POST /api/local/message` 注入测试消息，完整走过模型决策与六步治理流水线。

---

## 快速开始

### 方式一：Linux 原生环境直接运行（推荐，免 Docker）

#### 1. 系统要求与环境准备
- **操作系统**：标准 Linux 发行版（Ubuntu 20.04+、Debian 11+、CentOS 8+、Arch Linux 等）
- **运行时依赖**：.NET 10.0 SDK / Runtime、Git、curl、openssl
- **OneBot 协议端**：[NapCat Linux Shell 版](https://github.com/NapNeko/NapCatQQ)（如使用 QQ 私域通道）

在 Debian / Ubuntu 上安装基础组件：

```bash
# 更新软件包列表并安装依赖
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0 git curl openssl
```

#### 2. 获取源码与编译构建

```bash
# 克隆主仓库代码（main 分支）
git clone -b main https://github.com/ZhaoJun233/bot-agent.git
cd bot-agent

# 生成并编辑配置文件
cp .env.example .env
vim .env
```

#### 3. 运维管理

项目包含跨 Linux 发行版兼容的控制脚本：

- **后台启动**：
  ```bash
  ./start.sh
  ```
- **前台启动（调试控制台输出）**：
  ```bash
  ./start.sh -f
  ```
- **检查运行状态与探针**：
  ```bash
  ./status.sh
  ```
- **安全停止服务**：
  ```bash
  ./stop.sh
  ```
- **查看日志输出**：
  ```bash
  tail -f runtime/logs/bot-agent.log
  ```

服务启动后：健康探针 `http://127.0.0.1:18245/healthz`（端口见下方配置参考，容器镜像内是 8080）；**宿主不再内置任何页面**，`/` 与 `/web/` 由反向代理提供，面板前端部署见 [`src/web/README.md`](src/web/README.md)。

---

### 方式二：Docker Compose 容器化运行

在具备容器环境的主机上，可采用容器编排方案：

```bash
cp .env.example .env
vim .env

# 构建并启动服务
docker compose up -d

# 查看协议端登录二维码
docker compose logs -f napcat
```

---

## 配置参考

配置不是「一个 `.env` 说了算」，而是**两层环境变量 + 一个真源**。分不清这三者，是「改了却不生效」的最常见来源。

### 三层模型

| 层 | 位置 | 谁拥有 | 生效时机 |
| :--- | :--- | :--- | :--- |
| ① compose 层 | 根目录 `.env` | 部署者 | 只用于 `docker-compose.yml` 的 `${VAR}` 替换；**不会自动进容器** |
| ② 宿主层（环境变量） | 容器的 `environment:` / 进程环境 | 宿主启动时读一次 | 密钥与基础设施：**始终覆盖**，改完要重启 |
| ③ 真源（面板 + SQLite） | `data/botagent.db` | 面板保存后由它独占 | 行为类配置：面板改完即时生效并持久化 |

三条所有权规则（与 `src/BotAgent.Core/Contracts/AppSettings.Part1.cs` 的类注释同源）：

1. **密钥只从环境变量读**（`BOTAGENT_API_KEY` / `QQCHAT_API_KEY`、`BOTAGENT_QQ_ONEBOT_TOKEN`、`BOTAGENT_PANEL_TOKEN`、`BOTAGENT_AGENT_TOKEN`…），**不落盘、不进数据库**，所以配置文件可以随便备份、贴日志。
2. **基础设施**（模型地址、模型名、协议端地址、监听端口）环境变量始终覆盖。
3. **行为**（人设、白名单、阈值、冷却、语音、搜索…）面板 + 数据库是唯一所有者；环境变量只在**首次部署当种子**。面板保存过之后同名环境变量不再起作用，启动日志会用「以下环境变量已被【面板里的设置】覆盖」提示。

### ① compose 层（`.env`）

`.env` 只服务 `docker-compose.yml` 里的 `${VAR}` 占位符。两个坑：

- **新加变量必须同时在 compose 的 `environment:` 里声明**，否则容器里根本看不到它。
- 别把仓库里的 `docker-compose.yml` 直接覆盖到部署目录 —— 部署目录可能有手工条目（NapCat 的 `ACCOUNT` 丢了就要重新扫码）。

键名清单以 [`.env.example`](.env.example) 为准（带注释，是唯一维护的模板）。**不带 `BOTAGENT_` / `QQCHAT_` 前缀的裸名只在 compose 替换里有效**，宿主不读它们。

### ② 宿主层（环境变量）

宿主读的是带前缀的名字，**规范名 `BOTAGENT_*` 优先，`QQCHAT_*` 兼容旧部署**（`EnvironmentAliases.Get` 依次回落）。最常用的几条：

| 规范名（优先） | 兼容旧名 | 说明 |
| :--- | :--- | :--- |
| `BOTAGENT_API_KEY` | `QQCHAT_API_KEY`、`OPENAI_API_KEY` | 模型密钥（必填，不落盘） |
| `BOTAGENT_BASE_URL` | `QQCHAT_BASE_URL`、`OPENAI_BASE_URL` | 模型 Base URL |
| `BOTAGENT_MODEL` | `QQCHAT_MODEL`、`OPENAI_MODEL` | 模型标识 |
| `BOTAGENT_QQ_ONEBOT_PROTOCOL` | `QQCHAT_ONEBOT_PROTOCOL` | `ForwardWebSocket` / `ReverseWebSocket` |
| `BOTAGENT_QQ_ONEBOT_URL` | `QQCHAT_ONEBOT_URL` | 协议端 WebSocket 地址 |
| `BOTAGENT_QQ_ONEBOT_TOKEN` | `QQCHAT_ONEBOT_TOKEN` | 协议端鉴权令牌 |
| `BOTAGENT_QQ_UIN` | `QQCHAT_UIN` | 机器人号（留空则连接后自动获取） |
| `BOTAGENT_HEALTH_PORT` | `QQCHAT_HEALTH_PORT`、`HEALTH_PORT` | 面板与健康探针端口（原生默认 **18245**，容器镜像内默认 8080） |
| `BOTAGENT_HEALTH_BIND` | `QQCHAT_HEALTH_BIND` | 监听地址（默认 `127.0.0.1`，只绑回环） |
| `BOTAGENT_PANEL_TOKEN` | `QQCHAT_PANEL_TOKEN` | 面板 API 令牌 |
| — | `QQCHAT_PANEL_PASSWORD` | 面板口令的**初始值**：首次启动写入库，之后在面板里改 |
| `BOTAGENT_AGENT_TOKEN` | `QQCHAT_AGENT_TOKEN` | 服务器 Agent 令牌 |
| `BOTAGENT_NAPCAT_WEBUI_URL` | `QQCHAT_NAPCAT_WEBUI_URL` | NapCat WebUI 地址（面板扫码登录用） |

> 面板认证只有**口令**与**令牌**两种，**没有「关掉认证」的开关**。

### ③ 真源（面板 + 数据库）

初始化之后，绝大多数运行时行为都在 Web 面板里改，保存即持久化、即时生效、**不用重启**：人设与 AI 开关、群聊/私聊白名单、发言阈值与冷却、分句与延迟、表情包与戳一戳、语音与 TTS、搜索开关、平台实例策略、插件与 MCP、模型供应商。

这些键在旧部署里还以环境变量形式存在（`QQCHAT_WHITELIST`、`QQCHAT_PERSONA`、`QQCHAT_GROUP_COOLDOWN`…），它们现在只是**首次部署的种子**。

### QQ 官方开放平台（可选）

官方通道的开关与凭据同样是宿主层变量（AppId 只认 `QQCHAT_OFFICIAL_APP_ID`，没有 `BOTAGENT_` 别名）：

| 键 | 说明 |
| :--- | :--- |
| `BOTAGENT_OFFICIAL` / `QQCHAT_OFFICIAL` | `1` = 启用官方通道 |
| `QQCHAT_OFFICIAL_APP_ID` | 开放平台 AppId |
| `BOTAGENT_OFFICIAL_APP_SECRET` / `QQCHAT_OFFICIAL_APP_SECRET` | AppSecret（只从环境变量读，面板不接不回显） |

启用后与私域通道**完全隔离**：会话 key 带 `official:` 前缀，白名单各自独立。

接口面的完整地图见 [`docs/api-surface.md`](docs/api-surface.md)。

---

## Web 管理控制台

基于 Vue 3 + TypeScript 构建的高性能单页管理控制台，通过 ZBA 协议与核心服务通信，纯前端静态资产部署，数据仅在内存中会话流转：

- **基础配置与能力归属收敛**：「基础配置」页面仅保留 AI 对话开关、人设、冷却、并发与上下文限制等 7 项核心通用参数；模型服务、服务器 Agent、平台接入策略、健康报告及插件配置全数收敛至「能力管理」对应入口与插件卡片，架构整洁且支持扩展插件动态登记。
- **安全边界与纯内存会话**：访问令牌（Bearer Token）仅保存在浏览器内存中，刷新或锁定会话即丢弃；敏感秘密槽位绝不回显，写入操作受 CAS 乐观锁、单飞限制与操作键核对严格保护。
- **能力与插件生态一站式治理**：可视化管理驱动、插件、模型供应商与 MCP；支持插件市场安装、自定义 OpenAI 兼容提供商、参数热调、连接测试及 Token 配额管理。
- **深浅主题与无障碍体验**：支持深色/浅色主题一键切换与系统偏好跟随；全键盘导航支持（Tab 焦点捕获与 Escape 快速关闭），高对比度与移动端自适应布局。
- **任务审批与实时观测**：支持后台 Agent 任务提交与交互式审批流；实时观测组件运行状态与错误信息，协议端离线时可直接在控制台内展示扫码登录二维码。

---

## SSL/TLS 与反向代理

宿主本身**只监听明文 HTTP**（`HttpListener`），**不终止 TLS、也不生成证书**。生产环境的 HTTPS 由反向代理（Caddy / Nginx）负责，面板前端也由反代以 `/web/` 提供。

- **本地自签证书**：`./tools/gencert.sh [域名] [IP]` 会用 openssl 生成一套有效期 1095 天的自签证书到 `runtime/certs/`（`.crt` / `.key` / `.pfx`），供反代或调试客户端使用。
- **生产证书**：用 certbot 或 acme.sh 签发，再在反代里引用；证书路径属于**反代配置**，不是宿主的环境变量。
- **常见误判**：旧版本文档提到过两个指向证书路径的环境变量 —— 宿主已不再读取它们，配了也不会有任何效果。

---

## 系统架构

```text
+-------------------------+            OneBot v11            +----------------------------------+
| OneBot 协议端 (NapCat)   | <---- [Forward WS / WS] ----> | BotAgent.Headless (C# / .NET 10)  |
+-------------------------+                                 |   +-- OneBot 网关与事件分发      |
                                                            |   +-- 提示词组装与研判流水线   |
+-------------------------+          官方 Open API           |   +-- 人物画像与 SQLite 持久化   |
| QQ 官方开放平台          | <----------------------------> |   +-- 飞书 Webhook 接入网关      |
+-------------------------+                                 |   +-- 本地交互演练场通道         |
                                                            |   +-- 内置 Web 控制面板服务      |
+-------------------------+          平台 Webhook           |   +-- 平台实例策略与配额账本     |
| 飞书开放平台 (Feishu)    | <----------------------------> |   +-- 熔断容灾与租户队列隔离     |
+-------------------------+                                 +-----------------+----------------+
                                                                              |
                                                                              v OpenAI 兼容 API
                                                            +----------------------------------+
                                                            | 大语言模型 (DeepSeek / OpenAI)   |
                                                            +----------------------------------+
```

---

## 兼容性与数据迁移说明

- **平台模块**：OneBot、QQ 官方、飞书、本地通道与平台策略源码位于 `src/BotAgent.Platforms/`，由 Headless 在同一进程中引用；Platforms 的项目依赖仅有 Core。
- **飞书身份映射**：运行时装配持久化映射 `data/feishu-ids-v2.json`，使用与旧 32 位别名分离的新号段（`FeishuBase + 1e12` 至 `FeishuBase + 2e12`）；启用飞书通道时请随业务数据一同备份该文件。旧会话保留但需重新配置原生飞书 ID 白名单。
- **OwnMessage 台账隔离**：消息按平台、账号、会话与原生消息 ID 隔离并存入 `own_messages_scoped`；历史裸数字 ID 行继续留档，升级后旧版本不可向下兼容读取 scoped 记录，建议升级前完整备份 `data/botagent.db`（旧版 `data/qqchat.db` 仍兼容）。
- **工具调用硬超时**：硬超时限制的是调用方等待的最大时限并发送取消信号，并不代表底层外部操作必然瞬间终止，外部不可取消的底层动作仍受宿主调用约束。

---

## 测试与质量验证

项目包含端到端集成测试套件与高频安全约束探针：

```bash
# 执行自动化单元评测与架构探针
dotnet test

# 运行集成测试套件
dotnet build tests/BotAgent.IntegrationHarness -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll

# 运行前端类型检查、单元测试与浏览器回归（在 src/web 下）
npm run typecheck && npm run test:unit
npm run test:e2e
```

测试集涵盖 S1–S55 合成端到端场景（白名单、静默决策、分句节拍、长期记忆与档案、表情包与魔数校验、上下文引用、模型热切、语音合成、撤回感知、联网搜索、扫码登录、飞书通道隔离、多平台策略、每日 Token 配额账本、官方通道假网关等；S2 并入 S1，S33 随 pi 桥退役，S42 仅使用本机假网关/合成凭据），以及微秒级架构棘轮（ArchitectureProbe）、安全边界（SafetyProbe）与生产契约探针（ProductionSpecProbe）。

---

## 开源协议

本项目源码基于 [MIT License](LICENSE) 协议开源。使用的 OneBot 协议端（如 NapCat）请遵循其各自的代码许可与平台规范。