# Bot Agent

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
- **统一平台策略总控**：支持 QQ 私域通道、QQ 官方开放平台、飞书机器人及本地通道。设置页「平台实例策略」是各平台「启用平台」「启用聊天」的唯一入口，同时配置独立群聊/私聊白名单及特性覆盖。关闭聊天可保持连接，关闭平台会阻止聊天；凭据、连接状态、白名单和全局 AI 开关仍是独立安全前置。
- **每日 Token 配额账本**：按平台与会话来源（SourceKey）划分 UTC 日预算管理，支持按平台筛选租户、查看用量与剩余额度，超额后自动进入节能静默。
- **零依赖本地演练场通道**：内置进程内独立测试通道，通过面板「演练沙箱」页签（`POST /api/playground/inject`）注入测试消息，完整走过模型决策与六步治理流水线。

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

# 生成并编辑配置文件（可全部留空，之后在面板里填）
cp .env.example .env
vim .env
```

构建分三步。解决方案文件是 `BotAgent.slnx`，.NET 8 SDK 不支持整解决方案构建，**必须逐工程执行**：

```bash
# ① 核心（纯无头宿主）
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# ② 预设功能插件 → 输出到 runtime/plugins
dotnet build src/BotAgent.Plugins.Presets/BotAgent.Plugins.Presets.csproj -c Release -o runtime/plugins

# ③ Web 面板插件 → 输出到 runtime/plugins（含前端静态资源 wwwroot）
dotnet build src/BotAgent.Plugins.WebUi/BotAgent.Plugins.WebUi.csproj -c Release -o runtime/plugins
```

构建产物落在两个地方，二者不可混淆：

| 路径 | 内容 | 说明 |
| :--- | :--- | :--- |
| `src/BotAgent.Headless/bin/Release/net8.0/` | 核心宿主 DLL | `start.sh` 启动它 |
| `runtime/plugins/` | 两个插件工程 + `wwwroot/` | 插件目录，运行时按 DLL 动态加载 |

`runtime/` 整个目录被 `.gitignore` 忽略，属于运行期产物：删掉它不会丢失任何源码，重新构建即可恢复。

#### 3. 启动与首次登录

项目包含跨 Linux 发行版兼容的控制脚本：

```bash
./start.sh          # 后台启动
./start.sh -f       # 前台启动，直接看控制台输出
./status.sh         # 检查进程状态、端口监听与健康探针
./stop.sh           # 安全停止
tail -f runtime/logs/bot-agent.log   # 跟踪日志
```

首次启动后，面板口令是自动生成并落盘的（详见 [面板登录](#1-面板登录)）：

```bash
cat runtime/data/panel-credential.txt
```

拿到口令后，浏览器访问 `http://127.0.0.1:18245/`，在弹出的内嵌登录卡片里输入即可。端口不是 8080，默认是 **18245**（可用 `HEALTH_PORT` 覆盖）。

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
| `MODEL_API_KEY` | `sk-...` | OpenAI 兼容接口的 API Key。留空也能启动，此时 AI 回复不可用；改在面板「模型 API 密钥」填入即可（存入独立密钥库，优先于环境变量） |
| `MODEL_BASE_URL` | `https://api.deepseek.com/v1` | 模型调用 Base URL |
| `MODEL_NAME` | `deepseek-chat` | 使用的模型标识 |
| `MAX_TOKENS` | `2048` | 单次回复 Token 上限 |
| `BOTAGENT_PANEL_TOKEN` | `my-panel-pass` | 面板登录口令。留空则自动生成并写入 `runtime/data/panel-credential.txt`，详见 [1. 面板登录](#1-面板登录) |
| `QQCHAT_PANEL_TOKEN` | `my-panel-pass` | 同上，`BOTAGENT_PANEL_TOKEN` 的兼容别名（前者优先） |
| `HEALTH_PORT` | `18245` | Web 控制面板与健康探针监听端口（同义变量：`QQCHAT_HEALTH_PORT`、`BOTAGENT_HEALTH_PORT`） |
| `WHITELIST` | `*` 或 `123456,789012` | 允许响应的 QQ 群号或用户号（逗号分隔，`*` 为全量） |
| `ONEBOT_PROTOCOL` | `ForwardWebSocket` | 协议端通信模式（`ForwardWebSocket` / `ReverseWebSocket`） |
| `ONEBOT_URL` | `ws://127.0.0.1:3001` | 协议端 WebSocket 接入地址 |
| `ONEBOT_TOKEN` | `your_token` | 协议端鉴权令牌（如设置） |
| `BOT_UIN` | `10001` | 机器人 QQ 号（留空时连接后自动获取） |
| `QQCHAT_TLS_CERT` | `/etc/letsencrypt/live/.../fullchain.pem` | 自定义 TLS/HTTPS 证书链路径（可选，支持 Let's Encrypt） |
| `QQCHAT_TLS_KEY` | `/etc/letsencrypt/live/.../privkey.pem` | 自定义 TLS/HTTPS 证书私钥路径（可选） |

### QQ 官方开放平台与飞书（可选）

官方通道与私域通道**并存**：会话、上下文、白名单完全隔离（靠会话 key 的通道前缀）。两条通道都建议优先在面板里配（「配置总线 → 接入协议」），只有想用环境变量时才需要下面这些：

```ini
# QQ 官方开放平台
QQCHAT_OFFICIAL=1
QQCHAT_OFFICIAL_APP_ID=your_app_id
QQCHAT_OFFICIAL_APP_SECRET=your_app_secret
QQCHAT_OFFICIAL_SANDBOX=0
QQCHAT_OFFICIAL_WHITELIST_GROUPS=
QQCHAT_OFFICIAL_WHITELIST_PRIVATES=
# 接口地址软编码：留空用官方默认值，官方改版时在这里覆盖，不必改代码
QQCHAT_OFFICIAL_API_BASE=
QQCHAT_OFFICIAL_TOKEN_URL=

# 飞书机器人
QQCHAT_FEISHU=1
QQCHAT_FEISHU_APP_ID=
QQCHAT_FEISHU_APP_SECRET=
QQCHAT_FEISHU_VERIFICATION_TOKEN=
QQCHAT_FEISHU_WHITELIST=
QQCHAT_FEISHU_API_BASE=
```

> [!IMPORTANT]
> 变量名必须带 `QQCHAT_` 前缀。代码只认 `BotConfig` 里那张别名表，写 `OFFICIAL_ENABLED=1` 这类无前缀名字不会报错，但**也不会有任何效果**。

> [!NOTE]
> 通道开关改动后**必须重启进程**才会建立连接：通道层在装配期构建，配置热更新只重建白名单、闸门与定时器，不重建通道。

---

## 使用方法

### 1. 面板登录

面板不提供「免密访问」。首次启动时若没有配置口令，宿主会自动生成一个 24 字符随机口令，写入 `runtime/data/panel-credential.txt`（Unix 下权限 600），并在日志里记一行 `未配置面板口令，已自动生成并写入 ...`。

**口令来源优先级**（从高到低）：

| 顺序 | 来源 | 说明 |
| :--- | :--- | :--- |
| 1 | `BOTAGENT_PANEL_TOKEN` 环境变量 | 显式指定，最高优先 |
| 2 | `QQCHAT_PANEL_TOKEN` 环境变量 | 兼容别名 |
| 3 | `runtime/data/panel-credential.txt` | 上一次生成的口令，重启沿用 |
| 4 | 自动生成 | 上述都没有时随机生成并落盘 |

取口令：

```bash
cat runtime/data/panel-credential.txt
```

**登录行为**：

- 浏览器打开 `http://<服务器IP>:18245/`，若没有有效会话，页面就地弹出内嵌登录卡片，**不做页面跳转**。
- 登录成功前不会发起任何业务接口请求；登录成功后卡片关闭并立即加载面板数据。
- 会话凭据是 `HttpOnly` + `SameSite=Strict` 的 Cookie（名 `botagent_sid`），JS 读不到，有效期 12 小时且滑动续期（一直在用不会中途被踢）。
- 会话过期或服务重启后，任何 `/api/*` 返回 401 时前端会重新弹出卡片，不静默失败。
- 同一来源 5 分钟内连续失败 5 次会短暂拒绝（返回 429「尝试过于频繁」）。此时即使口令正确也会被挡，等窗口过去或重启进程即可清零。
- 登出：`POST /api/auth/logout`，服务端撤销会话并下发失效 Cookie。

**取口令时的常见坑**：`cat` 出来的内容末尾没有换行时，zsh 会补画一个反显的 `%`（`PROMPT_EOL_MARK`），复制粘贴会把 `%` 一起带进输入框，表现为「口令明明对却提示错误」。当前版本写入口令文件时已补尾随换行；若手上是旧文件，`cat` 后请只选中 24 个字符本体。

### 2. 面板页签

| 页签 | 内容 |
| :--- | :--- |
| 运行态势 | 进程状态、内存占用、首屏概览；通道列表**只显示已连接的协议端**，没有连接时为空 |
| 配置总线 | 全部配置项。分组由后端 `GET /api/settings/schema` 下发，前端零硬编码，插件存在才出现对应分组 |
| 会话通道 | 会话列表与消息流，可代发消息 |
| 插件槽位 | 已加载插件清单、启停开关、故障诊断报告 |
| 演练沙箱 | 本地通道注入测试消息，不依赖 QQ 协议端 |
| 日志总线 | 增量拉取 `runtime/logs/bot-agent.log` 尾部 |

页签是否出现由能力探针决定：例如没有本地通道时「演练沙箱」不会渲染，避免点了才发现功能不存在。

### 3. 配置优先级

| 层 | 位置 | 优先级 |
| :--- | :--- | :--- |
| 密钥库 | `secrets` 表（面板填入） | 最高 |
| 环境变量 | `.env` / 进程环境 | 次之 |
| 配置文件 | `runtime/data/settings.json` | 最低（面板保存后以它为准） |

密钥纪律：`ApiKey` 等密钥字段只存 `secrets` 表，不写入 `settings.json`，面板只回显「已配置 / 未配置」。在面板里填过的密钥会覆盖环境变量，想改回环境变量需先在面板清除密钥。

面板保存的配置走热更新扇出，多数项即时生效，无需重启。

### 4. 插件管理

- 插件从 `runtime/plugins/*.dll` 动态加载（可卸载的 AssemblyLoadContext）。新增插件只需把 DLL 放进该目录再重启。
- 启停走 `POST /api/plugins/toggle`。插件通过 `IPluginToggleBinding` 自己声明「开关对应哪个配置字段」，落库与热重载复用同一套扇出。
- 管理类插件（如 Web 面板本身）声明 `AllowDisable => false`，服务端硬拒绝禁用请求 —— 否则面板会把自己关掉。
- 功能是否可用由 `FeatureAvailability` 在装配期判定：插件 DLL 未加载或配置开关为关，功能实例以「未启用」状态构造、对外方法短路，面板也不渲染对应入口。
- 插件加载失败会被隔离并产出结构化诊断（故障码 + 根因 + 修复建议），在「插件槽位 → 诊断」里可读。

### 5. 接口一览

除 `/healthz`、`/readyz`、`/api/auth/*` 之外，所有 `/api/*` 都要求已登录，否则返回 401。

| 方法与路径 | 用途 |
| :--- | :--- |
| `GET /healthz` | 存活探针（免鉴权，容器 HEALTHCHECK 用） |
| `GET /readyz` | 就绪探针，协议端连上才返回 200 |
| `GET /api/auth/status` | 会话探测，返回 `authenticated` |
| `POST /api/auth/login` | 登录，请求体 `{"pw":"..."}`，成功下发 Cookie |
| `POST /api/auth/logout` | 登出并撤销会话 |
| `GET /api/capabilities` | 能力探针，前端据此裁剪页签 |
| `GET /api/status` | 运行时状态与通道连接 |
| `GET/POST /api/settings` | 读取 / 保存配置 |
| `GET /api/settings/schema` | 配置分组与字段描述（由插件贡献） |
| `POST /api/settings/secrets` | 写入密钥（存 `secrets` 表） |
| `GET /api/conversations` | 会话列表 |
| `GET /api/conversations/messages` | 指定会话消息 |
| `POST /api/messages/send` | 代发消息 |
| `GET /api/plugins` | 插件清单 |
| `GET /api/plugins/diagnostics` | 插件诊断报告 |
| `POST /api/plugins/toggle` | 启用 / 禁用插件 |
| `POST /api/playground/inject` | 本地演练场注入消息 |
| `GET /api/logs?lines=200` | 日志尾部 |

### 6. 官方 / 飞书通道启用

面板「配置总线 → 接入协议」已提供全部相关字段。

**官方开放平台三步**：

1. `启用 QQ 官方开放平台通道` 设为开
2. 填 `AppID` 与 `AppSecret`（AppSecret 存密钥库，不写入 `settings.json`）
3. 填 `官方通道群白名单` / `官方通道私聊白名单` —— 这里必须写 **openid**（`group_openid` / `user_openid`），写 QQ 号无效；留空 = 不响应任何消息，填 `*` = 全量放行

**飞书四步**：

1. `启用飞书机器人通道` 设为开
2. 填 `App ID` 与 `App Secret`
3. 填事件订阅的 `Verification Token`（回调验签用）；若在飞书侧开了事件加密，再填 `Encrypt Key`（存密钥库，留空 = 未加密）
4. 填 `飞书通道白名单`（`chat_id` / `open_id`，留空 = 不响应）

**接口地址软编码**：三项全部留空即用官方默认值。

| 面板字段 | 留空时的默认值 |
| :--- | :--- |
| 官方 REST 接口根地址 | 正式 `https://api.bot.qq.com`，沙箱 `https://sandbox.api.sgroup.qq.com` |
| 官方取 access_token 地址 | `https://bots.qq.com/app/getAppAccessToken` |
| 飞书 REST 接口根地址 | `https://open.feishu.cn` |

官方或飞书改版、换域名、走自建反向代理时，直接在这三项里覆盖，不需要改代码重新编译。

**三个必知的坑**：

- **沙箱模式收不到正式群消息**：`官方通道使用沙箱环境` 打开时，网关只推送开放平台「沙箱配置」里登记的沙箱群 / 沙箱单聊事件，正式群里 @ 机器人不会有任何反应，日志里也不会有一行记录。要接正式群必须关掉这个开关。
- 改开关后**无需重启**（官方通道支持热切换）；但飞书与本地通道的开关仍需重启才生效。
- 正式环境要先在开放平台把服务器的**出口 IP** 加进 IP 白名单，否则连不上。

### 7. 排障

| 现象 | 原因与处理 |
| :--- | :--- |
| 提示「口令不正确」 | 确认没有把终端行尾的 `%`、首尾空格一起粘贴；再查日志有无 `面板登录失败` 记录 |
| 提示「尝试过于频繁」 | 触发了 5 次 / 5 分钟限流，等待窗口结束或重启进程 |
| 面板打不开 | 确认端口是 18245 而非 8080；用 `./status.sh` 看监听与进程 |
| 页面空白或样式全丢 | `runtime/plugins/wwwroot/` 缺失，重新构建 WebUi 插件工程即可 |
| AI 不回复 | 检查面板「模型 API 密钥」是否已配置；未配置时启动正常但模型调用被跳过 |
| 想彻底重置运行期数据 | 停进程后删除 `runtime/`，重新构建并启动，面板口令会重新生成 |

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
                                                            |   +-- 插件宿主与功能可用性判定  |
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
# 架构护栏探针（棘轮 / 分层规则 / 红线锚点）
dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release

# 安全边界探针
dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release

# 面板前端静态检查（DOM id、鉴权链路、接口路径、配置键对齐）
node tests/BotAgent.WebUiProbe/probe.mjs

# 端到端集成套件
dotnet build tests/BotAgent.IntegrationHarness -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
```

注意：解决方案文件是 `BotAgent.slnx`，.NET 8 SDK 不支持整解决方案构建，`dotnet test` 无法一次性跑完，需按上面的工程逐个执行。架构探针自插件化起会同时扫描 `BotAgent.Headless`、`BotAgent.Plugins.WebUi`、`BotAgent.Plugins.Presets` 三处源码。

测试集涵盖 S1–S51 真实端到端场景（白名单、静默决策、分句节拍、长期记忆与档案、表情包与魔数校验、上下文引用、模型热切、语音合成、撤回感知、联网搜索、扫码登录、飞书通道隔离、多平台策略与每日 Token 配额账本等），以及微秒级架构棘轮（ArchitectureProbe）、安全边界（SafetyProbe）与生产契约探针（ProductionSpecProbe）。

---

## 开源协议

本项目源码基于 [MIT License](LICENSE) 协议开源。使用的 OneBot 协议端（如 NapCat）请遵循其各自的代码许可与平台规范。
