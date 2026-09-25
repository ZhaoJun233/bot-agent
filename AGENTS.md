# AGENTS.md —— 在这个仓库里干活之前先读

[English](AGENTS.en.md) | 简体中文

这个机器人连的是**真人 QQ 群**：日志、存储、面板接口里都有别人的昵称和发言。
**代码可以随便看，数据要先脱敏。**

> 本工作区还有一份更细的（含本机/服务器路径、部署脚本）：工作区根目录的 `AGENTS.md`。
> 那份不进公开快照，这份跟着代码走。

---

## 1. 红线：不读取群聊正文与成员隐私

**不要读取、不要复述、不要粘出来**：

- 会话与消息：`data/conversations.json`、`agent-sessions.json`、SQLite（`qqchat.db`）里的 `messages` / `member_*` 表
- 人物档案：`data/member_profiles/*.json`
- 日志正文：`logs/qqchat.log` 里的「收到 …」「已回复 …」这类行（带着发言原文）
- 协议端报文：NapCat 的 OneBot 事件 JSON（`message` / `raw_message` / `post_type` 字段）
- 面板/接口气泡：`/api/conversations/{key}/messages`、`/api/profiles/{uid}`
- 不 grep 的 `docker logs` / `tail -n 200` 整段输出

例外只有一个：用户明确让你看某一条，并且只看那一条。

---

## 2. 排查报错的正确姿势（唯一允许的方式）

只 grep 错误行，并且**把中文（发言、昵称）换成占位符之后**再看：

```sh
LOG=/opt/qqchat/data/logs/qqchat.log

grep -aE 'ERROR|Exception|Unhandled|Traceback' "$LOG" | tail -30 \
  | python3 tools/mask-cjk.py        # 仓库自带：非 ASCII 片段 → <CJK>
```

- **不许**不 grep 就 `tail` / `docker logs` 全量刷。
- 需要判断"数据长什么样"时只看**形状**：字段名、条数、长度、哈希、状态码。
- 结论写进文档 / 提交 / 回复时只写形状与计数，不写内容。
- 已经读到的内容**不外传**：不回群、不写进仓库、不发第三方接口（含"帮我看看"的外部模型）。

---

## 3. 写新功能时顺手守住隐私

- 任何**列出会话 / 成员 / 文件**的新输出，都要过脱敏开关：
  群里那条路用 `BotAgentHost.MaybeMask` / `ChatLabel`，其它地方用 `AgentMask.Text / ChatLabel / Shorten`。
- **显示层脱敏，存储与 key 保持原样**：`group:123` 这种 key 被遮了，命令与面板按钮就全废了。
- 面板"可编辑的真名"用 `nameRaw`（显示用 `name`）；改名输入框必须填真名，否则会把 `群友A` 写回去。
- 文档 / 注释 / 测试 / 探针里不许出现真实 QQ 号、群号、昵称、域名、IP、密钥 —— 用 `10001`、`群友A`、`example.com`。
- 验证隐私行为用合成数据（`tests/BotAgent.IntegrationHarness`、`MockOpenAi`），别拿线上会话试。

---

## 4. 两个开关（同一个功能的两面）

| 开关 | 字段 / 环境变量 | 默认 | 作用 |
| --- | --- | --- | --- |
| 列出会话时脱敏 | `enableAgentMask` / `QQCHAT_AGENT_MASK` | **开** | 群名、昵称、QQ 号只留前 3 后 2（`940***75`）；`//sessions` `//sessions all` `//runs` `//pi` 与面板会话列表都遵守 |
| Agent 附加提示词 | `agentPrompt` / `QQCHAT_AGENT_PROMPT` | 隐私红线 | 每个 `//` 任务都带上：外部设备（pi）拼在任务前面，服务器内置 agent 拼进**系统提示词**；默认值 = `AppSettings.DefaultAgentPrompt`（即本文件第 1、2 节） |

---

## 5. 常用命令

```sh
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
node tests/BotAgent.FrontendProbe/probe.mjs                       # 面板冒烟（无浏览器）
QQCHAT_IT_ONLY=s36 dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
# 结构护栏（只读源码，不连库不起进程）：基线在 ArchitectureProbe/Baseline.cs
dotnet build tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll
```

- 架构护栏是**棘轮**：`Baseline.cs` 里的阈值只允许**调低**，不允许调高（调高 = 放宽结构约束）。
  变小了就用 `--print` 拿新数字改基线。方案与批次见 `docs/engineering/architecture-optimization.md`。
- 部署到服务器 / 发布公开快照 / 历史记录（仓库外的内部资料）由仓库外的脚本负责，细节见工作区那份 `AGENTS.md`。
- 公开快照会做脱敏校验（域名、IP、QQ 号、昵称、模型名、密钥），**先 commit 再发布**。
