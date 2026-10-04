# Bot Agent Headless

[English](README.en.md) | 简体中文

**无界面、轻量化、支持多平台的聊天机器人服务**：这是 Bot Agent 的常驻后台核心服务，支持连接 QQ 私域（NapCat / OneBot v11）、QQ 开放平台官方通道、飞书应用机器人及本地无依赖测试通道。通过接入 OpenAI 兼容大模型（如 DeepSeek、ChatGPT、通义千问、Ollama 或自建中转接口），实现私聊与群聊的拟人化智能交互，并自带一个优雅易用的轻量 Web 管理面板。

```
聊天平台 (QQ / 飞书 / 本地)  ←——接收与发送——  Bot Agent (核心服务)  ——→  模型 API (思考与回复)
```

- **资源占用极低**：Docker 镜像仅约 80MB，无桌面 UI 依赖，Linux / macOS / Windows 服务器均可稳定常驻。
- **数据持久可靠**：非 root 安全运行，所有聊天记录、人物记忆与系统设置统一保存在 `/data` 目录（单个 SQLite 数据库，迁移备份极简）。
- **运维开箱即用**：自带轻量 Web 管理面板，同时提供 `/healthz`（存活状态）、`/readyz`（协议端连接就绪）及 `/status`（详细状态 JSON）。

---

## 快速开始（推荐：Docker Compose）

只需要简单的 3 步即可跑起机器人：

### 1. 准备配置文件
```bash
cp .env.example .env
vim .env                      # 至少填写 MODEL_API_KEY（模型密钥）和 WHITELIST（白名单）
```

### 2. 启动服务容器
```bash
docker compose up -d
```

### 3. 扫码登录与验证连通

**① 登录 QQ 账号**：
- **方式 A（最直观）**：在浏览器打开机器人面板 `http://<主机IP>:8080/`，账号未登录时面板顶部会自动展示登录二维码，手机 QQ 扫码即可。
- **方式 B（终端查看）**：通过容器日志查看终端二维码：
  ```bash
  docker compose logs -f napcat
  ```

**② 确保 NapCat 消息能转发给机器人（首次配置一次即可）**：
1. 浏览器打开 NapCat 控制台：`http://<主机IP>:6099`；
2. 进入 **网络配置 → 添加「WebSocket 客户端」（正向 WS）**：
   - **URL**：`ws://qqchat:3001`（注意：容器同在一个 Docker 内部网络，使用服务名 `qqchat`，不要填 `127.0.0.1`）；
   - **Token**：与 `.env` 中的 `ONEBOT_TOKEN` 保持一致（若留空则都不填）。
3. 在宿主机或命令行检查是否连通：
   ```bash
   curl -s http://127.0.0.1:8080/readyz       # 返回 {"ready":true,...} 说明连接就绪！
   curl -s http://127.0.0.1:8080/status       # 查看运行状态概览
   ```
4. 现在去白名单内的群聊里发送 `@机器人 你好`，机器人就会为你回复啦！

---

## 配置说明与环境变量

> 💡 **小白上手建议（面板热更新优先）**：
> - **面板随时改，立即生效**：大模型的 API Key、接口地址、机器人人设、发言欲望、白名单等绝大多数配置，启动后都可直接在 Web 面板（`http://<主机IP>:8080/`）的「设置」页可视化修改，**保存后立刻生效，不需要重启容器**。
> - **环境变量的用途**：主要作为初次启动时的默认种子；一旦在面板中修改并保存过某项设置，系统将以数据库中的面板配置为准。
> - 带 `_FILE` 后缀的环境变量可从指定文件读取敏感内容，方便搭配 Docker secrets 使用：
>   ```yaml
>   environment:
>     QQCHAT_API_KEY_FILE: /run/secrets/model_key
>   secrets:
>     model_key:
>       file: ./model_key.txt
>   ```

### 核心必备项（只要配好这几项就能跑起来）

| 变量 | 默认值 | 通俗说明 |
| --- | --- | --- |
| `QQCHAT_API_KEY` / `OPENAI_API_KEY` | — | **必填**。大模型 API Key（也支持面板直接填，自动加密存库；面板配置优先） |
| `QQCHAT_BASE_URL` / `OPENAI_BASE_URL` | `https://api.openai.com/v1` | 大模型 API 基础地址（支持 DeepSeek / 通义 / Ollama / 第三方中转，面板可随时切换） |
| `QQCHAT_MODEL` / `OPENAI_MODEL` | `gpt-4o-mini` | 所用模型名称（如 `deepseek-chat` 等，面板可随时切换） |
| `QQCHAT_WHITELIST` | 空 | ⚠️ **白名单**：**留空默认不理任何人（防止被乱拉群打扰）**。填允许生效的群号或私聊 QQ 号（英文逗号隔开），填 `*` 代表对所有群和私聊开放 |

### QQ 通道配置

| 变量 | 默认值 | 通俗说明 |
| --- | --- | --- |
| `QQCHAT_ONEBOT_PROTOCOL` | `ForwardWebSocket` | 协议类型：正向 WebSocket (`ForwardWebSocket`)、反向 (`ReverseWebSocket`) 或 `Http` |
| `QQCHAT_ONEBOT_URL` | `ws://127.0.0.1:3001` | 协议端地址。Docker Compose 内部正向连接填 `ws://napcat:3001` |
| `QQCHAT_ONEBOT_TOKEN` | 空 | 协议通信密钥 Access Token（与 NapCat 保持一致） |
| `QQCHAT_UIN` | 空 | 机器人的 QQ 号（用来准确识别别人 `@机器人`；留空连上后会自动获取） |
| `QQCHAT_NAPCAT_WEBUI_URL` | `http://napcat:6099` | NapCat WebUI 地址（用于面板内直接显示扫码登录） |
| `QQCHAT_NAPCAT_WEBUI_TOKEN` | 空 | NapCat WebUI 登录 Token（配置后，面板未登录时会直接内嵌登录二维码） |

### 机器人个性与回复行为

| 变量 | 默认值 | 通俗说明 |
| --- | --- | --- |
| `QQCHAT_PERSONA` | 空 | **机器人人设**：设定它的性格、口癖与说话风格（如“你是毒舌傲娇群友，说话简短口语化”），每次对话自动注入上下文 |
| `QQCHAT_AI_DESIRE` | `50` | **发言欲望 (0~100)**：数值越低越克制沉稳，数值越高越容易在群聊中主动搭话接梗（日常建议 30~60） |
| `QQCHAT_SUITABILITY_THRESHOLD` | `10` | **发言门槛**：模型评估该话题适不适合接话的分数门槛，低于此分保持沉默 |
| `QQCHAT_AI_MODE` | `1` | `1` 为正常对话模式；`0` 为静默观察模式（只记录消息不回复，适合调试） |
| `QQCHAT_GROUP_COOLDOWN` | `8` | **群聊回复冷却（秒）**：同一群两次发言之间的最小时间间隔，防止连续刷屏 |
| `QQCHAT_PRIVATE_COOLDOWN` | `3` | **私聊回复冷却（秒）**：私聊回复最小间隔 |
| `QQCHAT_IDLE_FALLBACK` | `60` | **冷场兜底（秒）**：群里一段时间没人说话时，机器人是否主动判断冷场并接话（`0` 代表关闭此功能） |
| `QQCHAT_SPLIT_REPLIES` | `1` | **仿真人打字分句**：将较长的回复按标点切分成 2~4 段分批发送，体验更像真人（不会把小数、网址或引号切碎） |
| `QQCHAT_SEGMENT_DELAY_MS` | `700` | 分句发送之间的时间间隔（毫秒） |
| `QQCHAT_IGNORE_BRACKETS` | `0` | **括号旁白识别**：设为 `1` 后，群友发的「（笑）」「（叹气）」会被识别为动作说明而非正文，避免机器人误解原意 |
| `QQCHAT_MAX_CONTEXT` | `200` | 喂给模型的最大上下文条数 |
| `QQCHAT_PROFILE_LOOKUP` | `8` | 附带的人物档案数量上限 |
### 运维与日志相关

| 变量 | 默认值 | 通俗说明 |
| --- | --- | --- |
| `QQCHAT_DATA_DIR` | `/data` | 容器内的数据目录，必须挂载宿主机卷以实现数据持久化 |
| `QQCHAT_HEALTH_PORT` | `8080` | 内置 Web 面板与健康检查监听端口，`0` 为关闭 |
| `QQCHAT_HEALTH_BIND` | `+` | 监听绑定地址（Windows 环境下若无管理员权限会自动回退至 `127.0.0.1`） |
| `QQCHAT_VERBOSE` | `1` | 日志详细程度：`1` 输出完整流程，`0` 仅输出关键提示 |
| `QQCHAT_LOG_FILE` | `1` | 是否输出日志到文件：`1` 写入 `logs/qqchat.log`，`0` 仅输出到终端标准输出 |
| `TZ` | `Asia/Shanghai` | 系统时区，直接影响群聊消息时间戳与机器人对“当前时间”的感知 |

### 丰富互动特性（表情包 / 戳一戳 / 语音 / 联网搜索）

| 变量 | 默认值 | 通俗说明 |
| --- | --- | --- |
| `QQCHAT_STICKERS` | `1` | **表情包总开关**：自动收集群友表情包、按聊天语境智能选图回复、定期自巡检清理 |
| `QQCHAT_STICKER_MAX` | `120` | 本地表情包图库容量上限（张） |
| `QQCHAT_STICKER_CANDIDATES` | `6` | 每次回复时供大模型挑选的候选表情包数量 |
| `QQCHAT_STICKER_COOLDOWN` | `120` | 同一会话两次发表情包的最小间隔（秒），避免滥发刷屏 |
| `QQCHAT_ENABLE_POKE` | `1` | **戳一戳总开关**：群友戳机器人时能拟人化回复甚至反戳回去（别人互戳只作记录不插话） |
| `QQCHAT_POKE_COOLDOWN` | `45` | 戳一戳冷却时间（秒）：防止同一个人连续双击恶搞触发刷屏 |
| `QQCHAT_ENABLE_VOICE` | `0` | **语音消息开关**：允许大模型偶尔用语音回复（需配合 TTS 语音容器使用） |
| `QQCHAT_VOICE` | `zh_CN-huayan-medium` | 语音合成音色名称 |
| `QQCHAT_WEB_SEARCH` | `1` | **联网搜索开关**：大模型可在需要时自动搜索最新事实与时事资讯 |
| `QQCHAT_SEARCH_COOLDOWN` | `30` | 同一会话两次联网搜索的最小冷却间隔（秒） |

### 高级 Agent 循环与本地通道

| 变量 | 默认值 | 通俗说明 |
| --- | --- | --- |
| `QQCHAT_MAX_AGENT_STEPS` | `1` | **单轮工具循环步数上限**（1~3步）：设为 2~3 时，模型可在同一轮内先联网查询资料再结合结果组织回复 |
| `QQCHAT_AGENT_SERVER_GATE` | `0` | `1` = 启用 `//` 远程指令的统一安全权限闸门 |
| `QQCHAT_LOCAL_CHANNEL_IDS` | 空 | **本地 HTTP 虚拟测试通道**白名单：填入虚拟群号（如 `1`），即可在无需连接 QQ 的情况下通过演练场调试机器人 |

> 💡 **平台实例策略与本地通道面板操作指引**：
> 1. **集中治理**：Web 面板设置页提供「平台实例策略」表格，全平台（QQ私域、QQ官方、飞书、本地通道）的总开关、聊天开关、独立群聊/私聊白名单及特性功能在此一站式配置，支持原子级同步与清空保存。
> 2. **为什么本地通道初始没有会话**：本地通道为纯进程内/HTTP 模拟通道，不依赖外部公网长连，不会有外部用户自发推消息。会话在**首次注入消息时按需动态建立**。
> 3. **如何进行面板操作**：
>    - **方式一（可视交互演练场，推荐）**：在面板设置页「本地通道」卡片中点击 `🧪 打开交互演练场 (Playground)`（或访问 `/playground.html`），选择场景群组与触发模式（@机器人/引用/普通发言），输入内容点击「注入演练 ↵」即可触发端到端回复并实时观察六节点治理分析；
>    - **方式二（主面板会话管理）**：在演练场或接口注入第一条消息后，主面板会话列表立即出现带有紫色 `本地` 徽标的会话。点击左侧会话侧边栏「本地」标签即可专一查看聊天气泡、历史记录并继续进行人工回复；
>    - **方式三（HTTP API 自动化）**：发送 `POST /api/local/message`（附带面板认证令牌），参数如 `{"id": 1, "text": "你好", "sender": "测试员", "isGroup": true}`，消息走完整回复流水线，回复自动落入内存出箱（`/api/local` 可查）。
> 4. **安全前置（Fail-Closed）**：本地通道白名单为空时报 `403 local_channel_disabled`；未配置面板令牌时报 `403 panel_token_required`。

---

## 数据目录与持久化（`/data`）

机器人运行时产生的所有数据都存放在 `/data` 目录（对应宿主机挂载的 `./data` 文件夹）。

| 路径 | 内容 | 说明 |
| --- | --- | --- |
| `data/qqchat.db` | **SQLite 核心数据库** | 设置、群聊与私聊会话、聊天历史与归档、人物画像档案、心情记录、听歌记录、表情包索引及加密密钥。采用 WAL 模式（伴随 `-wal`/`-shm` 文件） |
| `data/legacy-json/` | 旧版本数据归档 | 早期旧版本 JSON 数据在首次启动自动导入数据库后安全保留于此，不影响日常读写 |
| `data/feishu-ids-v2.json` | 飞书身份映射表 | 仅在使用飞书通道时生成，用于飞书用户与会话映射，需随同数据库一起备份 |
| `stickers/*.png` | 表情包图片本体 | 机器人收集和使用的表情包真实图片文件（索引与情绪关键词保存在数据库中） |
| `logs/qqchat.log` | 运行日志文件 | 机器人后台日志，Web 面板「日志」页面展示的就是该文件的实时最新输出 |

### 💡 极简备份与迁移指南

- **数据全量备份/迁移（仅需 1 步）**：将宿主机上的 `./data` 整个文件夹复制打包到新服务器相同目录下即可！所有聊天历史、用户记忆、设置与表情包将完整保留，开箱即用。
- **为什么使用单个 SQLite 数据库**：
  1. **防止异常断电/崩溃损坏**：支持完整 ACID 事务，彻底杜绝以前多文件并发写入导致数据损坏的风险；
  2. **高效追加读写**：聊天记录是时间线数据，SQLite 的 WAL 机制使得追加消息极度轻快，不再需要每次重写几千行的 JSON 文件；
  3. **检索秒级响应**：面板翻阅历史聊天、调取人物画像均有索引加速，毫秒级加载。
- **密钥安全**：在面板中录入的大模型 API 密钥自动存放在数据库中隔离的 `secrets` 表内，数据库文件权限受严格控制，不随常规配置导出，保障安全。

---

## 修复兼容性与迁移

- **平台开关统一**：设置页仅在「平台实例策略」保留各平台的「启用平台」「启用聊天」。保存 `platformPolicies` 后对应账号的策略开关是权威来源，官方/飞书等旧字段仅作兼容映射；旧客户端单独提交旧开关键仍可更新对应策略（`feishuEnabled` 与 `localChannelIds` 的旧单开关语义会同时映射启用和聊天），同时提交时以 `platformPolicies` 为准。升级首次加载按旧开关交集迁移并记录 `PlatformSwitchSchemaVersion=1`，不会自动开启原本关闭的平台。聊天静音立即生效且不关闭连接；停用平台立即阻止聊天，不强制断开已存在的协议连接。首次启用未装配的官方、飞书或本地适配器可能需要重启，凭据与名单仍须配齐。降级前请备份配置，旧版本可能重新引入旧开关交集。
- **飞书身份映射**：运行时装配持久化映射 `data/feishu-ids-v2.json`，使用与旧 32 位别名分离的新号段（`FeishuBase + 1e12` 至 `FeishuBase + 2e12`）。旧会话保留，但不自动关联原生身份或继承历史；旧数字白名单需重新配置为飞书原生 ID。
- **OwnMessage 台账**：新记录按平台、账号、会话与原生消息 ID 隔离，写入 `own_messages_scoped`；旧裸数字 ID 行和旧 JSON 导入留档仍保留，但无法证明 scope 时 fail-closed，不猜归属、不作为 scoped 命中。旧版本读不到新增 scoped 记录，**不提供无损降级**，请保留升级前备份。
- **工具硬超时边界**：限制的是调用方等待时长，并发出取消请求；不等于强制终止底层操作。不响应取消的操作仍可能继续并产生外部副作用。

---

## 不用 Compose 的裸 docker 命令

```bash
docker build -f src/BotAgent.Headless/Dockerfile -t qqchat-agent .

docker run -d --name qqchat --restart unless-stopped \
  -e QQCHAT_API_KEY=sk-xxxx \
  -e QQCHAT_BASE_URL=https://api.deepseek.com/v1 \
  -e QQCHAT_MODEL=deepseek-chat \
  -e QQCHAT_ONEBOT_URL=ws://172.17.0.1:3001 \
  -e QQCHAT_ONEBOT_TOKEN=your-token \
  -e QQCHAT_WHITELIST=123456789 \
  -e QQCHAT_UIN=10001 \
  -e QQCHAT_PERSONA='你是群里的老群友，说话简短口语化。' \
  -v ./data:/data \
  -p 127.0.0.1:8080:8080 \
  qqchat-agent
```

> `--network host` 时 `QQCHAT_ONEBOT_URL` 可以直接写 `ws://127.0.0.1:3001`；
> 用默认 bridge 网络时用 `host.docker.internal`（Docker Desktop/macOS）或宿主机内网 IP（Linux）。

---

## 不用 Docker（裸跑 .NET）

```bash
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# 看全部选项
dotnet run --project src/BotAgent.Headless -- --help

# 跑起来
QQCHAT_API_KEY=sk-xxxx \
QQCHAT_ONEBOT_URL=ws://127.0.0.1:3001 \
QQCHAT_WHITELIST=123456789 \
dotnet run --project src/BotAgent.Headless
```

反向 WS 模式典型用法（本程序监听 3001，NapCat 主动连入）：

```bash
QQCHAT_ONEBOT_PROTOCOL=ReverseWebSocket \
QQCHAT_ONEBOT_URL=http://0.0.0.0:3001 \
QQCHAT_ONEBOT_TOKEN=your-token \
dotnet run --project src/BotAgent.Headless
```

---

## 健康检查与可观测性

```bash
curl -i http://127.0.0.1:8080/healthz    # 200：进程存活
curl -i http://127.0.0.1:8080/readyz     # 200：已连上协议端；503：未连接
curl -s  http://127.0.0.1:8080/status    # 状态快照
curl -s  http://127.0.0.1:8080/api/logs  # 最近 300 行运行日志（面板日志页首屏就用它）
curl -s  http://127.0.0.1:8080/api/qqlogin   # 当前登录二维码（面板里的扫码卡片用）
```

`/status` 样例：

```json
{
  "status": "running",
  "uptimeSeconds": 3721,
  "onebot": { "connected": true, "protocol": "ForwardWebSocket", "address": "ws://napcat:3001" },
  "account": { "uin": "10001", "selfId": 10001, "online": true },
  "login": { "qrAvailable": true, "napcatWebUi": "http://napcat:6099" },
  "agent": { "enabled": true, "model": "deepseek-chat", "desire": 50, "personaConfigured": true },
  "conversations": 7
}
```

镜像内置 `HEALTHCHECK`（调用 `--health`，不依赖 curl），所以 `docker ps` 能直接看到 `healthy`：

```bash
docker inspect --format '{{.State.Health.Status}}' qqchat-bot
```

---

## 面板：工具目录 / 追踪页 / 仪表盘（只读）

三块都是**只读**的，用来回答“机器人现在能干什么 / 刚才那一轮经过了什么 / 这台机器现在累不累”：

| 页面 / 区块 | 数据源 | 看什么 |
| --- | --- | --- |
| 工具目录 | `GET /api/tools` | 一份目录三条通道复用（聊天 10 / QQ 动作 10 / 服务器 6 = **26 条**），外加四项自检（重复 id / 缺执行者 / 缺高风险例外 / 多余的执行者）——`healthy:true` = 没有对不上的 |
| 追踪页 | `GET /api/traces` | 一轮一条轨迹、**六个节点**（参与判断 / 上下文组装 / 模型决策 / 工具闸门 / 工具执行 / 净化发送）；只给**形状**（状态码 / 原因码 / 耗时 / 计数 / 工具名），**不含正文**；内存里留最近 **50 轮**，重启即空 |
| 健康仪表盘 | `GET /api/dashboard` | 活跃会话 / 在途与排队 / 平均延迟 / 内存与负载 / 当前工具数 / 轨迹数 —— 一屏数字，不新造统计 |

> 追踪页上唯一一条**写路径**是审批决定（`POST /api/approvals/decide`）：两道前置 fail-closed（审批开关关着不许用；未配面板令牌一律 403），
> 判定复用群内审批那**同一份**校验，一条都不放宽。

## 手机端

面板是响应式的，用手机浏览器打开就能用（≤ 760px 自动切换布局）：

| 桌面 | 手机 |
| --- | --- |
| 左侧导航 rail | 底部标签栏（拇指能碰到的位置，带 iOS 安全区内边距） |
| 会话列表 + 聊天区并排 | 主从式：先列表，点进会话才看聊天，头部有返回键 |
| 右键菜单 | 长按 450ms 弹同一张菜单（iOS 不触发 contextmenu） |
| 输入框 14px | 16px（小于 16px 时 iOS Safari 聚焦会自动放大页面） |
| 扫码卡片横排 | 竖排、二维码按 `min(58vw, 26vh, 200px)` 自适应（矮屏也不会把输入框挤出屏幕） |

细节：`viewport-fit=cover` + `env(safe-area-inset-bottom)` 处理刘海/手势条，
`body { height: 100dvh }` 防止地址栏收起时输入框被切掉，长文本 `word-break` 不横向溢出。
桌面布局完全不变。

> 改这块时注意两个容易踩的点：窄屏下**不要自动打开第一个会话**
> （一上来就进会话会让人失去方向），以及**不要靠 JS 读 `classList` 决定布局** ——
> 布局只看 CSS 媒体查询，JS 只负责 `body.m-chat-open` 这一个视图状态。

## 识图（图片下载）

```
群友发图 → 事件里的 url（QQ 多媒体 CDN，**带时效 rkey** 的临时链）
   └─→ 下载器：先查缓存 → 直接下 → 失败（4xx）时让协议端 get_msg 重新签发地址，
         按 fileid 对上同一张图再下一次
         └─→ base64 data URL → 多模态模型（每条消息最多 3 张）
```

- **缓存按 URL 作键**（48 条 / 32MB 双上限，满了逐最旧的）：同一张图只下一次。
  为什么要缓存：图片会长期留在会话上下文里，以前**每轮生成都重下一遍** ——
  rkey 一过期就是每轮一条 `[Vision] 图片下载失败 400`（线上实测 80 条全 400，日志刷屏且模型看不到图）。
- **过期兜底**：`IQqChatSource.RefreshImageUrlsAsync`（OneBot `get_msg`）——
  实测同一条消息重新签发后就能下到（旧 rkey 失效、新 rkey 200）。协议端不支持就退化成“这张图这轮看不到”，不影响回复。
- **安全**：图片 URL 来自 QQ 事件，属不可信输入，先过 SSRF 闸门（默认拦回环/内网；
  `QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1` 仅供自建/测试）。

## 表情包（全库共用一份）

它是一条完整链路，每个环节坏了都只会“静默地不好用”，所以下面写清每段到底做了什么：

```
群友发图 ──下载─→ 按内容 sha256 去重存盘（stickers/）
                    └─→ 排队让模型看图，生成「一句话说明 + 情绪/场景关键词」
回复前    ──用最近几条对话当检索词，从库里挑 N 张候选（默认 6）塞进提示词
              └─→ 模型在 JSON 里加 sticker 字段挑选；只发图也行（reply 留空）
                      └─→ 机器人读文件 → base64 段 → 发图（可带引用），并记一次使用
容量      ──超出上限按“用得少 ×3 − 闲置天数”淘汰（常用的留着，新增的不会立刻被删）
审核      ──入库时模型同时判定“是不是表情包”：聊天截图/广告/纯文字图 → 直接丢掉，不当候选
频率门    ──同一会话两次发表情包至少隔 StickerCooldownSeconds（默认 120），
              同一张 10 分钟内不重复（库小时模型会“每句都挂同一张”，群里会开愤）
巡检      ──每 N 秒把库（id | 说明 | 用过几次 | 多久前加的）交给模型，
              让它自己决定删哪些；一次最多删 1/5，24 小时内用过的代码侧直接拦下
```

面板「设置 → 表情包」可以改开关 / 上限 / 候选数 / 巡检间隔，并能打开库看缩略图、
手动删除、立即巡检、从登录账号的 QQ 收藏表情导入（NapCat 的 `fetch_custom_face`；
收藏夹为空时会明确告诉你，不会当成错误）。

相关接口：`GET /api/stickers`、`GET /api/stickers/{id}/img`、`POST /api/stickers/{id}/delete`、
`POST /api/stickers/curate`、`POST /api/stickers/import`。

> 两个容易踩的点：图片 URL 来自 QQ 事件，属不可信输入，下载默认拦回环/私有 IP（SSRF）；
> 模型给的是“说明 + 关键词”而不是图片本身，候选必须是检索出来的几张，不能把整库塞进提示词。

## 语音消息（可选：模型偶尔用声音说一句）
```
模型输出 JSON 里带 speak（要说出口的话；也可以是 true = 把 reply 用语音说）
   └─→ 机器人拼出 http://tts:5000/speak?text=…&voice=…&speed=…
         └─→ 发 OneBot record 段，data.file 就是这个 URL
               └─→ NapCat 自己下载 → 转 silk（native 转换器）→ 上传成语音
机器人这边：不下载音频、不碰 silk、不把音频塞进 WebSocket
```

为什么这么设计（都是踩过的坑）：

- **音频编码交给协议端**：silk 是 QQ 私有格式，自己接编码器版本很容易对不上；
  NapCat 内置了 native 转换器（`convertToNTSilkTct`），且它能直接吃 `http(s)://` / `base64://` / 本地路径。
- **代价是网络可达**：NapCat 必须能访问这个 URL。两个容器同在 `qqchat-net` 时就是 `http://tts:5000`；
  若把 NapCat 放到别的机器上，要用宿主机 IP / 域名，并且别把 `tts` 只绑在 `127.0.0.1`。
- **语音要克制**：提示词反复要求“偶尔用”（道谢/撒娇/唱歌/情绪重的时候），
  代码侧再加一道**同会话 45 秒**的闸门（`VoiceMinIntervalSeconds`）——
  模型不听话也刷不了屏，而且 Piper 是 CPU 串行推理，一条要几秒。
- **一律可降级**（任一环节失败都退化成打字，内容不丢）：开关关闭、超过 `VoiceMaxChars`、
  频率门、`QQCHAT_TTS_URL` 没配、TTS 容器挂了、协议端 retcode≠0（如不支持 record 段）。
- **落库记的是 `[语音] 说的内容`**：模型下一轮才知道“我刚才是用声音说的什么”，不会当自己没说过。

面板「设置 → 语音消息」可以：开关、音色（datalist 给出 4 个中文音色）、语速、字数上限、TTS 地址，
以及**试听一句**（真去 `/speak` 拿回 wav 在浏览器里播，音色/语速可以当场改当场听）和
**检查 TTS 服务**（问对方 `/health`，列出可用音色）。对应接口：`POST /api/voice/test`、`GET /api/voice/health`。

> 音色就是模型文件：换音色 = 往 `/opt/qqchat/tts/` 放一个 `<name>.onnx`（+ 同名 `.json`）。
> 要复现**某个真人的嗓音**需要先把声音用在自己身上得到授权，再单独训练或调用云端克隆 API ——
> 没授权的真人音色不要做。

## 撤回消息 / 联网搜索 / 括号旁白

**撤回（`group_recall` / `friend_recall`）** —— 撤回是没有正文的事件，只能靠 notice 同步：

```
群友撤回一条消息
  └─→ 会话里那条变成 [已撤回] 原内容（内容保留：机器人当时在场，直接抹掉会让它“失忆”）
        ├─→ 不再给 (#id)，也不可能被选为 replyTo（即使模型硬填也会被代码拒掉）
        ├─→ 给模型一次开口机会（“撤回了啥”），同会话 90s 冷却
        ├─→ **手误更正不点评**：撤回后同一个人又发了新消息 → 只标记、不给开口机会
        └─→ 提示词明确：可以记得，但不要复述/引用/说“我都看见了”这类话
面板：那条消息会被划掉，并注明“模型看到的是「[已撤回] 原文」”
```

**引用回复（`[回复 X「…」]`）** —— 群友点 QQ 的“回复”引用某条时，以前那个 reply 段是被**直接丢掉**的
（模型只看到一句“我也是”，不知道在回什么）：

```
消息事件里的 reply 段（数组）或 [CQ:reply,id=…]（CQ 码）→ 取出被引用消息的 id
  └─→ 从会话上下文里查出“谁、说了什么”（先查上下文，再查“机器人自己发过的消息”）
        → 正文前拼上 [回复 老王「原话」]，一起进模型上下文与聊天记录
        ├─→ 引的是机器人自己那句 → [回复 你「…」]（模型才知道是在跟它说话）
        └─→ 原文查不到（引用太旧）→ 只标“更早的消息”，**绝不编内容**
```

> 为什么要把原话也拼上：群聊里一句“我也是”全靠引用那条才能懂 —— 上下文里虽然也有那条，但可能隔着好几十条，
> 模型不一定去对（对不上就会编）。机器人自己发出去的消息 id 来自发送响应（`SendResult.MessageId`）。

**联网搜索（模型填 `search` / `read`）** —— 两轮动作，和“听音乐”同一套思路：

```
模型输出 {"reply":"我去查一下","search":"关键词"}  或  {"read":"https://…"}
  └─→ 后台真去查/去读（不阻塞本轮回复）
        ├─→ 首选：原生 /v1beta/models/<model>:generateContent + tools:[{google_search:{}}]
        │        —— Google 真去搜，答案带 groundingMetadata（检索词 + 来源标题）
        └─→ 兜底：WebSearchSources 模板（searx* = SearxNG JSON / wiki* = MediaWiki JSON / 其余抽 HTML 链接）
              └─→ 结果作为“刚查到的资料”进**下一轮**提示词，模型拿着事实再说
冷共：同一会话 30 秒只查一次；搜不到/读不到就如实告诉模型“没查到”（别让它编）
SSRF：所有出站 URL（含 read 的）都过 SafeUrl；搜索源地址也在其中
```

为什么默认是“模型自带搜索”而不是爬网页：很多部署环境的出口 IP 是机房地址，
Google/Bing/DuckDuckGo/百度 对爬虫一律回看板页或验证码；而模型订阅本身就能搜 ——
不额外要密钥、不爬虫、结果还带来源。搜索源模板留给“自建 SearxNG / 内网检索 / 干净出口 IP”的场景。

**“什么时候该搜”是提示词里写死的两份清单**（管理员要的是“模型自主判断”，但自主判断需要一个依据）：

| | 内容 |
| --- | --- |
| 必须搜 | 新闻时事、天气、价格/汇率/股票、赛程与比分、活动/开服/发售时间、软件与游戏版本、新番与作品情报、某人的最新近况 —— 以及任何带“现在/最新/最近/今天/今年”的问题（训练知识有截止时间） |
| 不用搜 | 数学、成语、语法、历史与地理常识、代码写法、能从上下文看出来的东西；“现在几点/今天几号/今天周几”也不用搜 |

另外，提示词里**无条件**注入 `[现在的时间]`（日期 + 星期 + 时区）—— 模型自己没钟，
不问它、被问“现在几点”就只能靠印象猜（线上实测：经常答错）。它跟联网开关无关：关掉 `QQCHAT_WEB_SEARCH` 依然有。

相关接口：`POST /api/search/test`（`{query}` 搜 / `{url}` 读页面）、`POST /api/voice/test`、`GET /api/voice/health`。

**括号旁白（`〔旁白：…〕`，面板里一个开关）** —— 群聊里「（笑）」「（bushi）」「行（端在桌上）」这类**动作 / 表情说明**：

```
开关打开（QQCHAT_IGNORE_BRACKETS=1）
  └─→ 首尾的括号段改写成 〔旁白：…〕，一起进聊天记录、人物档案与模型上下文（内容一字不丢）
        ├─→ 整条都是旁白（〔旁白：笑〕）→ 不单独触发一次回复（旁白不是对谁说的话），
        │     也不给 (#id)（不会被当成“一句话”引用）；HasPendingReply=false（静默兜底也不拿它去找模型）
        ├─→ 正文 + 旁白混着（行〔旁白：端在桌上〕）→ 照常触发，正文才是那句话
        └─→ 提示词里解释了这是动作/表情说明、不是他说的话 → 可以当现场信息，别当一句话去接
例外（永不动）：句子中间的括号（（2026）年的计划）、群友发表情的记录（[表情:斜眼笑]/[图片]）、
              带图 / 带 @ 机器人 / 私聊
```

> 为什么标在正文里而不是加一个字段：这跟 `[图片]`/`[表情:斜眼笑]` 是同一类东西 ——
> 入站时把“对方发了啥”改写成模型能读的形式，面板 / 归档看到的与模型看到的是同一份，也不用改库结构。

## 群成员身份（群主 / 管理员 / 群头衔）

机器人在群里说话时会知道“谁说了算”：提示词里会多出一段

```
[本群身份]
群主：老王（20001）
管理员：小美（20002）
群头衔：小明（20003）=活跃气氛担当
```

- **来源两条路**：① 群消息事件自带的 `sender.role`（零成本，每条都有）；
  ② 协议端动作 `get_group_member_info` —— **群头衔只有这里才有**，所以按需去问一次
  （同一人 3 天内不再问；问完就落库，重启也不丢）。
- **落库**：`member_roles` 表（主键 `uid + group_id`，同一个人在 A 群是管理员、在 B 群可能只是群友）。
- **只列“值得一提”的**：群主 / 管理员 / 有头衔的人，最多 14 人（再多就只是个名字列表了，白费 token）；
  普通群友不占这一段。私聊不给这一段。
- 提示词里还会告诉模型这些身份意味着什么（管理员能踢人/撤回，但**不要拿身份拍马屁、也不要拿它压人**）。

## 参与闸门 / 人工审批 / 提问 / 场景预设（默认都关）

四个开关都在**面板**里（不必改环境变量），打开后面板自己会如实回显"现在到底多出什么能力"：

| 开关 | 默认 | 打开之后 |
| --- | --- | --- |
| 参与闸门 | 关 | 状态机说"这轮不参与"（观望 / 退场 / 冷却）就不叫模型、也不发言；**只收不放** —— 被 @ 仍然照常回。只读接口 `GET /api/participation` 能看到每个会话的状态与原因（不含正文） |
| 允许提问 | 关 | 模型写 `action=ask` 时，服务端包一条**带一次性编号**的提问发到群里（编号与有效期由服务端给）；谁答一声都算答完，过期作废；**提问不授予任何权限** |
| 人工审批 | 关 | 模型"想调工具"先在群里发一张**待确认**（编号 / 可批的人 / 有效期）；群主 / 管理员或面板点名的人回「同意 XXXX」才执行 —— 身份 / 会话 / 有效期 / 一次性 / 策略版本全在服务端核。**只执行固定的假工具 `demo.echo`**（记一行日志 + 回一句演示说明，没有 shell / 文件 / 进程控制能力） |
| 场景预设 | 空 | 填 `on-demand` / `research` / `social` 之一：只**收紧**能力白名单与每轮预算；名字不认识 → 最低权限（Fail-Closed）。留空 = 完全按现有开关来（与以前一致） |

参与闸门另有环境变量（其余三个是纯面板项）：

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `QQCHAT_PARTICIPATION_GATING` | `0` | `1` = 打开参与闸门 |
| `QQCHAT_PARTICIPATION_MAX_REPLIES` | `3` | 参与中最多连说几句 |
| `QQCHAT_PARTICIPATION_COOLDOWN` | `20` | 说满之后的冷却（秒） |
| `QQCHAT_PARTICIPATION_PROBING` | `1` | 观望期允许的探话次数 |
| `QQCHAT_PARTICIPATION_ACTIVE_LIFE` / `QQCHAT_PARTICIPATION_EXITING_LIFE` | `900` / `180` | 参与期 / 退场期的上限（秒） |

## `//` 任务（服务器 agent）与健康日报

- **`//` 任务**：群里 / 私聊里用 `//` 前缀派活，结果回到**它来的那个会话**。开关在面板：
  前缀（默认 `//`）、用户白名单（**空 = 谁都不能用**，权限落到具体某个人）、令牌、
  服务器侧工具表（bash / 读写文件 / 搜索 / docker / QQ 动作 —— **docker 与危险动作默认关**）、
  服务器 agent 自己的模型接口与密钥（与聊天那条链路分开配）。
- **上下文卫生**：服务器 agent 默认**每个任务隔离**（不带上一轮的历史）；想续用上一轮要显式 `//接着`，
  `//reset` 清干净当前会话。
- **健康日报**：每天定时（默认 18:00，可关）私聊一条状态 —— 内存 / 负载 / 会话数 / 队列 / 语音连通性。
  **纯服务器侧**，不经过外部设备（那台机器可能根本没开）。
- **面板一键部署**：上传产物或填个地址 → 服务器自己重建镜像并替换容器；部署前自动留回滚点，
  高权限开关默认关。

## 设计取舍与运行边界

核心能力（OneBot 网关与三种传输、人物档案库、会话持久化、模型客户端（含多模态识图）、
白名单、冷却限流、请求串行队列、静默兜底、回复引用规则，以及后来加的统一工具目录 / 决策轨迹 /
有限步进循环 / 第三条通道）都有测试套件钉住，见下节。
下面这些是**刻意的取舍**，不是待办：

| 取舍 | 说明 |
| --- | --- |
| 无界面 | AI 开关改由 `QQCHAT_AI_MODE` 控制（容器里没有"AI 开 / 关"按钮） |
| 只管连、不管跑 | 协议端（NapCat）自己下载 / 启动 / 配置，本服务只负责连接与收发 |
| 群历史拉取的时机 | 没有会话列表可点，所以是"首次收到该群消息时"拉一次 |
| 分句发送 | 长回复按句末标点分句、按节奏分批发送（不丢字；不误切小数 / 域名 / 连续标点 / 收尾引号） |
| 引用目标的硬规则 | replyTo 的语义写死在提示词（跟谁说话、不是素材）；本轮触发是**复读 / 模仿**时模型指到别处就不引用；没有触发消息时也不引用 |
| 落盘保留 `SenderId` / `ImageUrls` / `QqMessageId` | 重启后人物档案与识图不会失效 |
| 通配白名单 `*`、健康检查、环境变量 / 密钥文件配置、JSON 中文不转义 | 面向容器部署 |
| **面板前端没有构建步骤** | 原生 JS + CSS（**没有 npm 依赖、没有打包器**）：面板资源随程序集嵌入，`git archive` 出来的就是全部交付物；
  新增一个页面 = 加文件 + `PanelRoutes` 一行路由 + 探针覆盖（见 §7.1 的判据：~1GB 的机器上不能让前端构建进流水线） |

---

## 测试

以下命令从仓库根目录运行，提供本地进程与隔离容器两层验证入口（真实机器人进程 + 合成协议端/模型），不代表本轮已执行容器测试、远端 CI 或生产部署：

```bash
# ① 本地集成测试：先单独构建机器人，再起真实进程 + 合成协议端/模型
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
dotnet run --project tests/BotAgent.IntegrationHarness -c Release

# ② 容器测试：验证真正跑在容器里的机器人（在容器网络里放置假协议端/假模型）
docker network create qqchat-e2e
docker build -f tests/BotAgent.IntegrationHarness/Dockerfile -t qqchat-harness .
docker run -d --name harness --hostname harness --network qqchat-e2e \
  -e HARNESS_BOT_HEALTH_URL=http://bot:8080 qqchat-harness --serve-external
docker run -d --name bot --hostname bot --network qqchat-e2e \
  -e QQCHAT_API_KEY=sk-mock \
  -e QQCHAT_BASE_URL=http://harness:18899/v1 \
  -e QQCHAT_ONEBOT_URL=ws://harness:13099 \
  -e QQCHAT_WHITELIST=12321 \
  qqchat-agent
docker logs harness      # 断言结果
```

覆盖：反向/正向 WebSocket 握手、登录号识别、群/私聊链路、白名单严格模式与 `*` 通配、
模型沉默时不发言、回复引用（被插话时才带引用；模型指认的目标要经校验；复读时不挂错人；**收方向的引用回复也认得出来**）、
分句发送不丢字、括号旁白标成 `〔旁白：…〕` 且不单独触发回复、人物档案读写、
提示词组装（人设 / 档案 / `{发送者}{内容--时间}` 格式）、重启后会话恢复、健康端点。
harness 可调度 S1、S3–S41、S43–S51；S2 的提示词断言并入 S1，S42 官方通道场景仍排除在 harness/CI 回归之外，不代表已有官方通道端到端覆盖。
2026-09-24 的历史快照为 728 通过 / 1 条软提醒线（系统提示 < 4000 字，实测约 4364 字；**是哨兵不是目标，别去改阈值**），不是本轮结果或当前固定断言总数。
`QQCHAT_IT_ONLY=s19` 可以只跑某个场景；测试工程不引用机器人工程，改源码后必须先单独构建机器人。

另有几支**秒级探针**（合成场景，不访问生产数据或服务，改完对应部分各跑一遍）：`ArchitectureProbe`（架构棘轮）、
`SafetyProbe`（机制与多平台策略安全边界）、`ParticipationProbe`、`PipelineEval`（隔离评测）、
`ProductionSpecProbe`（生产契约与降级）、[FrontendProbe](<../../tests/BotAgent.FrontendProbe/probe.mjs>)（面板静态 + 运行时）。S50 飞书 Webhook 接入与 S51 每日 Token 配额面板也在集成场景列表中；计数以本地命令输出为准：

```bash
node tests/BotAgent.FrontendProbe/probe.mjs
```

---

## 常见问题与排查指南

遇到机器人异常时，先看 `docker compose logs -f qqchat` 日志，大多数情况可通过下表快速解决：

| 异常现象 | 常见原因 | 通俗解决办法 |
| --- | --- | --- |
| `/readyz` 一直返回 503，面板显示未连通 | 机器人与 NapCat 没有连通 | 1. 确认 NapCat 容器已正常启动；<br>2. 登录 NapCat WebUI（`6099` 端口），确认已添加正向 WebSocket `ws://qqchat:3001`；<br>3. 检查双方的 Token 是否配置一致。 |
| 日志出现 `白名单为空 → 忽略所有消息` | 未配置有效白名单 | 在 `.env` 或 Web 面板「设置」中配置 `WHITELIST`：填入群号或 QQ 号，多个用英文逗号分隔；若想测试所有群，可填 `*`。 |
| 机器人已连接（connected:true）但在群里不说话 | 1. 该群不在白名单内；<br>2. 机器人判断不该接话；<br>3. 开启了静默调试模式 | 1. 确认群号在白名单内；<br>2. 检查 `QQCHAT_AI_MODE` 是否设为 `0`（静默模式）；<br>3. 在面板适当调高「发言欲望」（建议 50 左右），并在人设里明确写出机器人的性格与说话意愿。 |
| 每次重启容器都需要重新手机扫码 | NapCat 存储目录未持久化或权限不足 | 检查 `docker-compose.yml` 中 `./napcat/ntqq` 挂载目录是否存在，并确保容器内用户对该目录具有读写权限。 |
| 消息里的时间与当前时间差了 8 个小时 | 容器时区未指定中国标准时间 | 在 `.env` 或 Compose 环境变量中增加 `TZ=Asia/Shanghai` 并重启容器。 |
| Web 面板端口绑定失败报错 | Windows 环境下未以管理员权限运行 | 设置环境变量 `QQCHAT_HEALTH_BIND=127.0.0.1` 即可正常启动面板。 |
| 收不到某些语音或文件 | 协议端或网络限制 | 目前建议优先使用文字与图片互动；语音特性需开启 `QQCHAT_ENABLE_VOICE=1` 并确保 TTS 容器正常运行。 |

## 许可

本程序源码 MIT。NapCat 遵循其自有许可（非商业），另见仓库根 `README.md`。
