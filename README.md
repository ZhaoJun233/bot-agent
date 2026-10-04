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
- **统一平台策略总控**：支持 QQ 私域通道、QQ 官方开放平台、飞书机器人及本地通道。设置页「平台实例策略」统筹配置全局开关、聊天开关、独立群聊/私聊白名单及特性覆盖，严格遵循 Fail-Closed 安全相交原则。
- **每日 Token 配额账本**：按平台与会话来源（SourceKey）划分 UTC 日预算管理，支持按平台筛选租户、查看用量与剩余额度，超额后自动进入节能静默。
- **零依赖本地演练场通道**：内置进程内独立测试通道，通过 Web 交互演练场（`/playground.html`）或 `POST /api/local/message` 注入测试消息，完整走过模型决策与六步治理流水线。

---

## 快速开始

### 方式一：Linux 原生环境直接运行（推荐，免 Docker）

#### 1. 系统要求与环境准备
- **操作系统**：标准 Linux 发行版（Ubuntu 20.04+、Debian 11+、CentOS 8+、Arch Linux 等）
- **运行时依赖**：.NET 8.0 SDK / Runtime、Git、curl、openssl
- **OneBot 协议端**：[NapCat Linux Shell 版](https://github.com/NapNeko/NapCatQQ)（如使用 QQ 私域通道）

在 Debian / Ubuntu 上安装基础组件：

```bash
# 更新软件包列表并安装依赖
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0 git curl openssl
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

服务启动后，浏览器访问 `http://127.0.0.1:8080/` 即可进入 Web 管理面板。

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

核心运行配置位于根目录的 `.env` 文件。

### 核心参数

| 配置项 | 示例值 | 说明 |
| :--- | :--- | :--- |
| `MODEL_API_KEY` | `sk-...` | OpenAI 兼容接口的 API Key（必填） |
| `MODEL_BASE_URL` | `https://api.deepseek.com/v1` | 模型调用 Base URL |
| `MODEL_NAME` | `deepseek-chat` | 使用的模型标识 |
| `MAX_TOKENS` | `2048` | 单次回复 Token 上限 |
| `PANEL_PASSWORD` | `your_password_here` | Web 控制面板访问密码（可选，留空则免密访问；可在面板中随时修改或清除） |
| `DISABLE_PANEL_AUTH` | `1` | 强制禁用控制面板认证（可选，默认 0） |
| `HEALTH_PORT` | `8080` | Web 控制面板与健康探针监听端口 |
| `WHITELIST` | `*` 或 `123456,789012` | 允许响应的 QQ 群号或用户号（逗号分隔，`*` 为全量） |
| `ONEBOT_PROTOCOL` | `ForwardWebSocket` | 协议端通信模式（`ForwardWebSocket` / `ReverseWebSocket`） |
| `ONEBOT_URL` | `ws://127.0.0.1:3001` | 协议端 WebSocket 接入地址 |
| `ONEBOT_TOKEN` | `your_token` | 协议端鉴权令牌（如设置） |
| `BOT_UIN` | `10001` | 机器人 QQ 号（留空时连接后自动获取） |
| `QQCHAT_TLS_CERT` | `/etc/letsencrypt/live/.../fullchain.pem` | 自定义 TLS/HTTPS 证书链路径（可选，支持 Let's Encrypt） |
| `QQCHAT_TLS_KEY` | `/etc/letsencrypt/live/.../privkey.pem` | 自定义 TLS/HTTPS 证书私钥路径（可选） |

### QQ 官方开放平台（可选）

如需启用官方 Bot API：

```ini
OFFICIAL_ENABLED=1
OFFICIAL_APP_ID=your_app_id
OFFICIAL_APP_SECRET=your_app_secret
```

> [!NOTE]
> 进程完成初始化后，大多数运行时行为（如人设、回复阈值、冷却时间、表情开关等）均可在 Web 控制面板内在线动态调整并持久化，无须重启服务。

---

## Web 控制面板与移动端体验

访问 `http://<服务器IP>:8080/` 即可直接进入控制面板：

- **免密访问与安全管控**：控制面板无需强制设置密码即可直接使用；同时支持在面板「设置」页随时设置保护密码或清除已有密码。
- **两级分类导航架构**：设置页按 5 大类聚合（通道接入、模型与 Agent、聊天与互动、语音与检索、其他），宽屏手风琴折叠展开，移动端双层胶囊联动。
- **移动端与液态玻璃导航**：深度适配智能手机屏幕与触控操作，底部导航栏采用 Apple 风格的液态玻璃（Liquid Glass）胶囊浮动 Dock 样式，完美支持 iOS/Android 的 Safe Area 安全区域。设置页专属避让保存底栏，标题栏常驻一键保存入口，并在键盘弹出时自动隐藏避让。
- **全链路性能优化**：
  - **内存静态资产缓存 (Asset Cache)**：静态文件与前端资产在内存中高效驻留，规避频繁的文件系统与程序集反射流开销。
  - **动态 GZip 压缩**：对 HTML、JavaScript、CSS、JSON、SVG 等文本内容根据浏览器请求头动态进行 GZip 压缩，整体网络传输体积减少约 70%。
  - **硬件加速与渲染隔离**：运用 CSS Containment、GPU 合成层加速 (`translateZ`) 与原生弹性滚动，确保长会话列表高帧率丝滑滑动。
- **会话监视与上下文栈**：以对话气泡形式实时展示私聊与群聊会话流、用户画像与上下文堆栈。
- **参数热调与模型切换**：在线调整互动欲望、回复阈值、冷却间隔、API 地址与模型配置，即时生效。
- **状态观测与扫码接入**：实时查看 `/healthz`、`/readyz` 及系统监控指标；协议端离线时直接渲染扫码登录二维码。
- **实时日志流**：采用 Server-Sent Events (SSE) 持续推流控制台日志，首屏秒级回填历史记录。

---

## SSL/TLS 安全与证书管理

服务内建针对 Linux 平台的 X.509 证书管理机制，支持开箱即用的自签名证书与标准 Let's Encrypt 证书链：

- **自签名证书自动生成**：启动时如未检测到已有证书，服务内核将自动生成有效期为 3 年的标准自签名证书，并输出至 `runtime/certs/` 目录：
  - `runtime/certs/botagent.crt`：X.509 公钥证书 (PEM)
  - `runtime/certs/botagent.key`：RSA-2048 私钥 (PEM)
  - `runtime/certs/botagent.pfx`：PKCS#12 密钥库
  亦可执行 `./tools/gencert.sh [域名] [IP]` 手动指定 SAN 扩展生成。
- **Let's Encrypt / 生产证书加载**：若已通过 certbot 或 acme.sh 申请标准域名证书，可在 `.env` 中配置对应路径进行挂载：
  ```ini
  QQCHAT_TLS_CERT=/etc/letsencrypt/live/example.com/fullchain.pem
  QQCHAT_TLS_KEY=/etc/letsencrypt/live/example.com/privkey.pem
  ```

---

## 系统架构

```text
+-------------------------+            OneBot v11            +----------------------------------+
| OneBot 协议端 (NapCat)   | <---- [Forward WS / WS] ----> | BotAgent.Headless (C# / .NET 8)  |
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

- **飞书身份映射**：运行时装配持久化映射 `data/feishu-ids-v2.json`，使用与旧 32 位别名分离的新号段（`FeishuBase + 1e12` 至 `FeishuBase + 2e12`）；启用飞书通道时请随业务数据一同备份该文件。旧会话保留但需重新配置原生飞书 ID 白名单。
- **OwnMessage 台账隔离**：消息按平台、账号、会话与原生消息 ID 隔离并存入 `own_messages_scoped`；历史裸数字 ID 行继续留档，升级后旧版本不可向下兼容读取 scoped 记录，建议升级前完整备份 `data/qqchat.db`。
- **工具调用硬超时**：硬超时限制的是调用方等待的最大时限并发送取消信号，并不代表底层外部操作必然瞬间终止，外部不可取消的底层动作仍受宿主调用约束。

---

## 测试与质量验证

项目包含端到端集成测试套件与高频安全约束探针：

```bash
# 执行自动化单元评测与架构探针
dotnet test

# 运行集成测试套件
dotnet build tests/BotAgent.IntegrationHarness -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll

# 运行前端静态与运行时探针
node tests/BotAgent.FrontendProbe/probe.mjs
```

测试集涵盖 S1–S51 真实端到端场景（白名单、静默决策、分句节拍、长期记忆与档案、表情包与魔数校验、上下文引用、模型热切、语音合成、撤回感知、联网搜索、扫码登录、飞书通道隔离、多平台策略与每日 Token 配额账本等），以及微秒级架构棘轮（ArchitectureProbe）、安全边界（SafetyProbe）与生产契约探针（ProductionSpecProbe）。

---

## 开源协议

本项目源码基于 [MIT License](LICENSE) 协议开源。使用的 OneBot 协议端（如 NapCat）请遵循其各自的代码许可与平台规范。
