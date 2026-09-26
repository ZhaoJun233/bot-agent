namespace BotAgent.ArchitectureProbe;

/// <summary>
/// 棘轮基线：**只允许往下调，不允许往上抬**（往上抬 = 放宽架构约束，等于把护栏拆了）。
/// 每次重构变小了，跑 <c>--print</c> 拿到实测值，把这里的数字改小（探针会在“可下调”里提示）。
/// 基线日期：2026-09-22（architecture-optimization.md 批次 0 建立）。
/// </summary>
internal static class Baseline
{
    // ── 规模 ──
    // ⚠ 批次 2（配置快照）如实登记：引入 SettingsBox 后 BotAgentHost 长了 25 行、+1 个字段、+1 个方法、
    //    面板 +1 处调用。这是机制成本，**批次 4（用例层拆分）必须还回去**，不许当成新常态。
    // 2026-09-23 批次 6：OpenAiClient 从 2686 行降到 ~1450（prompt/transport/parse 三段搬出去），
    // 新的最大文件是 ReplyPipeline（2313 行）—— 它是回复主链，本轮没动，棘轮如实跟着实测走。
    /// <summary>
    /// 2026-09-23 R9 收口如实登记：ReplyPipeline 2313 → **2316**（+3 = 三条 `using`：
    /// `Domain.Ports` / `Domain.Stickers` / `Services.Ports` —— 依赖倒置那一刀把具体类型换成端口带来的）。
    /// 2026-09-24 落位收口：2316 → **2314**（ReplyPipeline 少了一行 `using BotAgent.Models;` ——
    /// 那批数据类型（ChatMessage / MessageRole / ConversationKind / MessageMarkers）下沉进
    /// `Domain/Conversation/`，而它本来就 import 了 `Domain.Conversation`，于是与 `Domain.Ports` 合并）。
    /// 这一格只许往下调；下次谁把它拆小，记得同步这里。
    /// </summary>
    /// <summary>
    /// 2026-09-24（通用 Agent 平台 · 批次 C 决策轨迹）：2314 → **2328**（+14）。
    /// 明细：五个埋点调用（Begin / Context / Model / Outbound / Complete）、轨迹字段与构造参数、两条 using。
    /// 这是**机制成本**，如实登记（同批次 2 的先例）——**批次 E（聊天侧有限步进循环）必须还回去**：
    /// 那一批本来就要重构回复主链，届时把这些行收进循环骨架里。
    /// </summary>
    /// <summary>
    /// 2026-09-24（批次 E 有限步进循环）：2328 → **2320** —— 上面登记的债**当场还了一部分**：
    /// 模型调用（20 行具名实参 + 轨迹埋点）搬进 `Services/Reply/AgentTurnLoop.cs`，
    /// “当场做掉只读工具”搬进 `Services/Reply/InlineTurnTools.cs`。
    /// 这格仍然只许往下调：回复主链里动作分派那一段还能拆，谁接着拆记得同步这里。
    /// </summary>
    public const int MaxFileLines = 2320;
    public const string BotAgentHostPath = "Services/BotAgentHost.cs";
    // 2026-09-23 批次 5 第 3 步达成 DoD：面板直连组件 + 删掉 façade 转发之后，
    // 807 → 242 行 / 43 → 10 字段 / 48 → 10 方法（同时把"启动自述"搬去 Services/Ops/BootReport.cs）。
    /// <summary>
    /// 2026-09-23 R9 收口如实登记：242 → **243**（+1 = `using BotAgent.Domain.Ports;`，
    /// 它现在吃的是 <c>IModelClient</c> 而不是具体的 `OpenAiClient`）。DoD：≤ 400 ✅
    /// </summary>
    public const int BotAgentHostLines = 243;                  // DoD：≤ 400 ✅
    // ⚠ 2026-09-23 口径修正：字段类型里以前不许有括号，于是 `ConcurrentDictionary<string, (long A, DateTimeOffset B)>`
    //    这种**元组状态**在计数里凭空消失（本文件当时漏数 3 条）。修正后 HEAD 实测 96，本批（戳一戳用例）95。
    public const int BotAgentHostFields = 10;                  // 只数实例字段；DoD：≤ 10 ✅
    public const int BotAgentHostMethods = 10;                 // DoD：≤ 25 ✅
    public const int LongestMethodLines = 30;              // DoD：≤ 200（SendAsBotAsync —— 代发那一路留在它身上）

    /// <summary>
    /// **全 src** 最长方法（DoD §8.3：≤ 200 行）。只钉 BotAgentHost 是不够的 —— 那些大方法
    /// 搬进用例层之后，护栏得跟着搬，否则"搬出去就不管了"。
    /// 2026-09-23 批次 6 拆完，**已达成 DoD**：实测最长 195 行（WebUiServer.Settings 的
    /// ApplyChannelAndAgentSettings），全 src 超过 200 行的成员块 **0 个**。基线按 DoD 钉死在 200。
    /// 诊断用 <c>--longest</c> 看当前最长的若干个（拆的时候从上往下走）。
    /// </summary>
    public const int LongestMethodAnywhere = 200;

    // ── 易变细节 ──
    /// <summary>
    /// 读系统时间（<c>DateTime(Offset).Now/UtcNow</c>）**只允许出现在这一个文件里** ——
    /// 它是 <c>IClock</c> 的唯一实现（<c>Adapters/Time/SystemClock.cs</c>）。
    /// 别处一律走 <c>Clock.Now</c> / 注入的 <c>IClock</c>（见 architecture-optimization.md §6.3）。
    /// </summary>
    public const string ClockAllowedPath = "Adapters/Time/SystemClock.cs";

    /// <summary>
    /// 读系统时间总处数（只数 <see cref="ClockAllowedPath" /> 里的；其余任何文件出现一处就违规）。
    /// 2026-09-23 批次 4 收尾：131 → 3（SystemClock 的 Now / UtcNow / LocalDateTime 三个属性）。
    /// </summary>
    public const int ClockReads = 3;
    /// <summary>
    /// <c>new HttpClient</c> 只允许出现在出网端口的实现里（socket 的唯一构造点），
    /// 外加三个具名例外（都是"本地/运维"性质的出网，不在业务链路上）：
    ///   · <c>Host/Program.cs</c> —— 启动自检（进程起来前，装配点还没建）；
    ///   · <c>Adapters/Panel/PanelDeploy.cs</c> —— 部署产物下载（部署自己的事，同 R3 的例外）；
    ///   · <c>Services/OneBot/HttpTransport.cs</c> —— 协议端 HTTP 传输（发 QQ 消息，不是"抓网"）。
    /// </summary>
    public static readonly IReadOnlyList<string> HttpClientAllowedFiles = new[]
    {
        "Adapters/Net/HttpFetcher.cs",
        "Host/Program.cs",
        "Adapters/Panel/PanelDeploy.cs",
        "Services/OneBot/HttpTransport.cs",
    };

    /// <summary>出网客户端构造处数（棘轮：只许往下调）。2026-09-23：15 → 5（面板那两条也收进 IHttpFetcher）。</summary>
    public const int HttpClientNews = 4;
    /// <summary>
    /// 面板主文件里直接调 <c>_agent.</c> 的次数（棘轮口径只数 <c>WebUiServer.cs</c> 这一个文件）。
    /// 2026-09-23 批次 5 第 3 步：**归零** —— 面板不再经过 BotAgentHost 这层 façade 够组件，
    /// 要什么就注什么（配置 / 事件 / 会话 / 表情包 / 心情 / 语音 / 音乐 / 搜索 / agent 命令都直连）。
    /// 整个 <c>Adapters/Panel/</c> 目录也只剩一处：代发（<c>SendAsBotAsync</c>，它真要会话 + 协议端 + 记账 + 存档）。
    /// </summary>
    public const int PanelAgentCalls = 0;
    public const int ConcreteIoNews = 0;                   // 用例层 new 具体 IO 组件（DoD：0 —— 2026-09-23 装配全收进 CompositionRoot）
    public const int SettingsAssignments = 0;              // 就地改设置（批次 2 归零：只在副本上改，改完原子换引用）

    // ── R5：面板里不许出现的三件事（SQL / 直接文件 IO / 直读系统时间）──
    // 粒度（§13.3 要求先想清楚）：**面板的"编排"部分**必须干净 —— 那正是重构想修的地方
    // （面板曾经自己写 JSON、自己存文件、自己读时钟）。例外只有一条且与 R3 同一个理由：
    /// <summary>
    /// R5 的唯一例外：部署产物的状态文件 / 日志是**部署自己的事**（与 R3 的具名例外同一条，
    /// 见 §3.4 R3 与 §7 批次 3 的偏差说明）。它的读写不经过任何仓储，也没有别的持有者。
    /// </summary>
    public static readonly IReadOnlyList<string> PanelExemptFiles = new[]
    {
        "Adapters/Panel/PanelDeploy.cs",
    };

    /// <summary>R5：面板（除例外文件外）的 SQL 字面量处数 —— 恒为 0，别让它长出来。</summary>
    public const int PanelSqlLiterals = 0;

    /// <summary>R5：面板（除例外文件外）的直接文件 IO 处数 —— 恒为 0。</summary>
    public const int PanelFileIo = 0;

    /// <summary>R5：面板（除例外文件外）直读系统时间的处数 —— 恒为 0（走 Clock / 注入的 IClock）。</summary>
    public const int PanelClockReads = 0;

    /// <summary>例外文件自己的文件 IO 处数（棘轮，只许往下调：现在全是 PanelDeploy 的部署产物读写）。</summary>
    public const int PanelExemptFileIo = 15;

    /// <summary>唯一允许持有配置实例的类（发布点）；别处都只持有 SettingsBox。</summary>
    public const string SettingsOwnerPath = "Services/SettingsBox.cs";

    // ── 目标布局的常量 ──
    // Domain 单文件行数：**目标 300 已达成**。批次 1 只挪位置，五个搬进来的文件本身就超了（最大 467），
    // 当时按实测立了棘轮；这一批（2026-09-23）按职责把它们拆开 —— **只搬位置、一个字没改**：
    //   ApprovalStore 467 → 台账 204 / 决策 152 / 策略 90 / 词汇 61
    //   ToolGate 352      → 词汇 87 / 策略 84 / 闸门 148 / 预设 58
    //   TextRules 342     → 括号与文本 203 / 分句 154
    //   ModelOutputDeclaration 379 → 声明 132 / 回复与目标 117 / 媒体与联网 157
    //   ApprovalFlow 311  → 编号与文案 129 / 模型提议 111 / 人回话 76 / 命令词汇 32
    // 现在它是**硬规则**（跟 R1 一样直接 Check，不再是棘轮）：再来一个超 300 行的 Domain 文件就红。
    public const int DomainFileTargetLines = 300;
    public const int DomainTypeMaxFields = 10;
    public const int DomainTypeMaxMethods = 25;

    // ── R9：依赖方向（§3.2 图上的 `db → service`）──
    // §3.2 要求"仓储实现 service 想要的端口"，也就是 service 不认识 db 的实现类型。
    // 2026-09-23（本轮）**已达成**：用例层不再 `using BotAgent.Adapters.*`，
    // 全部改吃端口（Domain/Ports + Services/Ports，见 architecture-optimization.md §13.9）。
    // 现在它是**硬规则**（与 R1/R2/R3/R6 同类）：再来一个引用适配层的 Services 文件就红。

    // ── R2：SQL 只允许出现在这一层（批次 3 达成，别让它再散出去）──
    public const string SqlAllowedPrefix = "Adapters/Persistence/";

    /// <summary>
    /// SQL 字面量总数（棘轮：只许往下调；新加 SQL 请加在 Adapters/Persistence 里，并在这里如实登记）。
    /// 批次 3：89 → 95（+5 = 新的 own_messages 台账三条语句；+1 = 库里新建 own_messages 表）。
    /// </summary>
    public const int SqlLiteralTotal = 98;

    // ── R3：直接文件 IO 只允许出现在 Adapters/** 与下列具名例外 ──
    public const string FileIoAllowedPrefix = "Adapters/";

    /// <summary>
    /// 三个具名例外 + 一条明说的白名单。每一条都有理由，新增条目请先想清楚"它为什么不是 IO 实现细节"。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FileIoAllowedFiles = new Dictionary<string, string>
    {
        ["Services/FileLog.cs"] = "日志本体：它就是「写文件」这件事，没有别的持有者",
        ["Services/AppPaths.cs"] = "发现运行根目录（要试着写一下才知道有没有权限）",
        ["Adapters/Panel/PanelDeploy.cs"] = "部署产物的状态文件（部署自己的事，见 §3.4 R3）",
        ["Services/Agent/ServerAgentRunner.cs"] = "服务器 agent 的文件工具：按用户指令读写任意路径，不是数据存取",
    };

    /// <summary>
    /// 直接文件 IO 总处数（棘轮：只许往下调；新加的请落在 Adapters/**）。
    /// 批次 3：61 → 62（+1 = TtsConfFile 的写入；其余是搬迁，总数几乎没动）。
    /// </summary>
    public const int FileIoTotal = 62;
}
