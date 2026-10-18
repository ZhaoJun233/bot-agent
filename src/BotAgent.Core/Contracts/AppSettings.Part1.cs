using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Platforms;

namespace BotAgent.Services;

/// <summary>
/// 机器人配置（headless）。
/// 字段与旧版 <c>settings.json</c> 兼容：老的 runtime/data/settings.json 直接挂进容器即可复用。
///
/// ⚠ 所有权规则（避免“改了却没生效”）：
///   • **密钥**（ApiKey / OneBotToken / PanelToken）：只从环境变量读，**不落盘**，因此不进 settings.json。
///     好处：配置文件可以随便备份/分享/贴日志，不会泄密。
///   • **基础设施**（模型地址、模型名、OneBot 地址、端口…）：环境变量始终覆盖。
///   • **行为**（人设、白名单、阈值、限流…）：settings.json 是唯一所有者；环境变量只在首次部署当种子。
/// </summary>
public sealed partial class AppSettings : global::BotAgent.Platforms.PlatformOptions
{
    // ---------- Agent 大脑（OpenAI 兼容） ----------

    public string ModelBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>模型密钥。环境变量专属（QQCHAT_API_KEY），不写入 settings.json。</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// 面板里改过的模型端点；空 = 用环境变量 QQCHAT_BASE_URL。
    /// 为什么要这层“覆盖”：以前这三项只读环境变量，想换个中转/换模型就得改 .env 重启容器；
    /// 现在面板是“最新意图”，环境变量退居“首次部署的种子”（面板没改过时才用它）。
    /// </summary>
    public string? ModelBaseUrlOverride { get; set; }

    /// <summary>面板里改过的模型名；空 = 用环境变量 QQCHAT_MODEL。</summary>
    public string? ModelOverride { get; set; }

    /// <summary>
    /// 快速回复档（面板开关）：开 = 聊天回复改用 <see cref="FastModel"/>（轻量模型）。
    /// </summary>
    /// <remarks>
    /// 2026-09-19 实测（同一条中转链路，1.5k 字提示词）：
    /// 主模型（高档 / <c>-high</c>）首字 ~6.6s、难题 8.7~11.5s；轻量档（<c>-lite</c>）3.7~4.5s。
    /// 而“关思考”的参数（<c>reasoning_effort</c> / <c>thinking.type=disabled</c> / <c>thinking_budget=0</c>）
    /// 在这条链路上**实测全部无效**（首字一样 ~6.6s）—— 要快只能换档，所以开关做成了换模型。
    /// 代价：轻量档在硬推理上会弱一些（同题实测有错），群聊闲聊无碍。
    /// 只影响**聊天回复**这一路；标题综结/画像/表情包描述这些后台活儿仍用主模型。
    /// </remarks>
    public bool FastReply { get; set; }

    /// <summary>
    /// 快速档用哪个模型（面板可改）。
    /// 故意**不内置默认值**：具体模型名跟着中转走（各家叫法不同，换中转就变），
    /// 写死一个只会让人误以为“开了开关却没生效”。留空 = 快速档不生效（仍旧用主模型）。
    /// </summary>
    public string FastModel { get; set; } = string.Empty;

    /// <summary>聊天回复实际发给模型的名字：快速档开且填了模型 → 用它，否则主模型。</summary>
    [JsonIgnore]
    public string ReplyModel =>
        FastReply && !string.IsNullOrWhiteSpace(FastModel) ? FastModel.Trim() : Model;

    /// <summary>面板里填的 API Key（**不落 settings.json**，单独存 secrets 表，库文件权限 600）。</summary>
    [JsonIgnore]
    public string? ApiKeyOverride { get; set; }

    public int MaxTokens { get; set; } = 2048;

    /// <summary>
    /// 主模型思考深度：off / low / medium / high / xhigh，默认 medium。
    /// 保留 ThinkingBudget 字段名和 custom 以兼容旧配置。
    /// </summary>
    public string ThinkingBudget { get; set; } = "medium";

    /// <summary>
    /// 主模型自定义思考深度/预算（当 ThinkingBudget 为 custom 时生效，可为 Token 数字如 "8192" 或自定义档位名如 "minimal"）。
    /// </summary>
    public string ThinkingCustomBudget { get; set; } = "4096";

    /// <summary>
    /// 快速档模型思考深度预设：off / low / medium / high / xhigh / custom。
    /// 默认 low。
    /// </summary>
    public string FastThinkingBudget { get; set; } = "low";

    /// <summary>
    /// 快速档模型自定义思考深度/预算（当 FastThinkingBudget 为 custom 时生效，可为 Token 数字如 "1024" 或自定义档位名如 "minimal"）。
    /// </summary>
    public string FastThinkingCustomBudget { get; set; } = "1024";

    /// <summary>
    /// 解析当前生效的思考程度档位与 Token 预算（可指定是否为快速档模型）。
    /// </summary>
    public (string Effort, int BudgetTokens) ResolveThinkingBudget(bool isFastModel = false)
        => BotAgent.Model.ThinkingBudgetResolver.ResolveBudget(
            isFastModel ? FastThinkingBudget : ThinkingBudget,
            isFastModel ? FastThinkingCustomBudget : ThinkingCustomBudget, isFastModel);
    /// <summary>
    /// 解析快速档模型生效的思考程度档位与 Token 预算。
    /// </summary>
    public (string Effort, int BudgetTokens) ResolveFastThinkingBudget() => ResolveThinkingBudget(isFastModel: true);
    // ---------- 自适应采样超参数 (理性/感性动态温度与 top_p) ----------
    /// <summary>是否开启动态自适应采样（理性问题降温提准确率，感性互动升温保灵动）。默认开。</summary>
    public bool AdaptiveSamplingEnabled { get; set; } = true;
    /// <summary>理性问题目标温度（0.0 ~ 2.0），默认 0.3。</summary>
    public double RationalTemperature { get; set; } = 0.3;
    /// <summary>理性问题收窄采样 Top_P（0.0 ~ 1.0），默认 0.3。</summary>
    public double RationalTopP { get; set; } = 0.3;
    /// <summary>感性互动目标温度（0.0 ~ 2.0），默认 0.85。</summary>
    public double EmotionalTemperature { get; set; } = 0.85;
    /// <summary>感性互动放宽采样 Top_P（0.0 ~ 1.0），默认 0.9。</summary>
    public double EmotionalTopP { get; set; } = 0.9;
    /// <summary>基准默认温度（未开自适应或中性时的温度），默认 0.7。</summary>
    public double DefaultTemperature { get; set; } = 0.7;
    /// <summary>基准默认 Top_P（未开自适应或中性时），默认 0.85。</summary>
    public double DefaultTopP { get; set; } = 0.85;

    /// <summary>构造当前生效的自适应采样配置契约。</summary>
    public Domain.Reply.AdaptiveSamplingConfig ToSamplingConfig() => new(
        AdaptiveSamplingEnabled,
        RationalTemperature,
        RationalTopP,
        EmotionalTemperature,
        EmotionalTopP,
        DefaultTemperature,
        DefaultTopP);

    // ---------- 行为 ----------

    /// <summary>AI 对话欲望（0-100：越高越主动参与群聊；默认 50）。</summary>
    public int AiDesire { get; set; } = 50;

    /// <summary>发言适合度阈值（0-100）：模型评分低于此值则沉默，默认 10。</summary>
    public int SuitabilityThreshold { get; set; } = 10;

    /// <summary>
    /// 场景预设（V3 §9.3）：<c>on-demand</c> / <c>research</c> / <c>social</c>；**留空 = 跟随现有开关**。
    /// </summary>
    /// <remarks>
    /// 为什么默认留空：这是设计规范要求的“同一引擎通过配置适应不同场景”，但也是**新行为**。
    /// 留空时能力白名单完全由既有开关（联网/音乐/语音/表情包/戳一戳）拼出来 —— 与改造前逐字一致；
    /// 填了场景名才启用预设，而且预设只能**收紧**（与开关取交集），不会把面板关掉的能力打开。
    /// 名字不认识 → 最低权限（Fail-Closed），不是“默认全开”。
    /// </remarks>
    public string ScenarioPreset { get; set; } = string.Empty;

    /// <summary>
    /// 人在回路的审批（V3 §9.4）：**默认关**。
    /// </summary>
    /// <remarks>
    /// 开着的时候，模型说“我想调工具”不再直接静默，而是由服务端开一张**待批单**并公告到群里；
    /// 只有群主/管理员（或 <see cref="ApprovalApprovers" /> 里点名的人）回复「同意 编号」才会执行，
    /// 且**只执行一个固定假工具**（<c>demo.echo</c>：记一行日志 + 回一句演示说明，没有真实副作用）。
    ///
    /// 关着（默认）时行为与改造前**逐字一致**：模型写的 `action=tool` 仍是安全静默（`tool_not_enabled`）。
    /// 这一轮**没有**把 shell / 文件 / 进程 / 远程桥接进这条新路径 —— 审批只覆盖那个假工具。
    /// </remarks>
    public bool EnableApprovals { get; set; }

    /// <summary>
    /// 审批人名单（逗号/空格分隔的 QQ 号；留空 = 只认群里的 owner/admin）。
    /// 只有在 <see cref="EnableApprovals" /> 打开时才有意义。
    /// </summary>
    public string ApprovalApprovers { get; set; } = string.Empty;

    /// <summary>
    /// 参与状态机（V3 §7）的服务端上限。**默认值 = 状态机自己的默认值**，所以不填时行为与以前一致。
    /// </summary>
    /// <remarks>
    /// 每一条都会被 <see cref="Services.Participation.ParticipationPolicy.Clamped" /> 再钳一次 ——
    /// 面板里填多大都不会超过服务端硬上限（连续回复 ≤10、冷却 ≤600s、试探 ≤3 次、活性命 ≤3600s）。
    ///
    /// ⚠ 这一组参数**只喂观测日志**（gating 尚未打开）：状态机目前不改“发不发”的判定，
    /// 调它不会改变群聊行为 —— 那是刻意的，见 docs/engineering/progress.md 的 P1 条目。
    /// </remarks>
    public int ParticipationMaxConsecutiveReplies { get; set; } = 3;

    /// <summary>参与状态机：一次回复后的冷却秒数（服务端钳制 0..600；默认 20）。</summary>
    public int ParticipationCooldownSeconds { get; set; } = 20;

    /// <summary>参与状态机：试探阶段最多回几次（服务端钳制 1..3；默认 1）。</summary>
    public int ParticipationProbingMaxReplies { get; set; } = 1;

    /// <summary>参与状态机：活跃状态最长活多久（秒，服务端钳制 30..3600；默认 900）。</summary>
    public int ParticipationMaxActiveLifetimeSeconds { get; set; } = 900;

    /// <summary>参与状态机：退场状态挂多久（秒，服务端钳制 10..3600；默认 180）。</summary>
    public int ParticipationMaxExitingLifetimeSeconds { get; set; } = 180;

    /// <summary>
    /// 参与闸门（V3 §7.3 / §7.4）：**默认关**。
    /// </summary>
    /// <remarks>
    /// 关着 = 与改造前逐字一致：状态机只写观测日志，不影响“发不发”。
    /// 打开之后，状态机说“这一轮不参与”（观望/退场/未确认/冷却中）就**不叫模型**，
    /// 于是机器人会明显少说话 —— 这是**唯一**会真正改变群聊行为的新开关，所以刻意做成显式开关，
    /// 而且只收不放（它说参与时也只是回到原来的判定链：自评阈值/白名单/冷却照旧）。
    /// </remarks>
    public bool EnableParticipationGating { get; set; }

    /// <summary>
    /// 允许提问（V3 §8.1 的 <c>action=ask</c>）：**默认关**。
    /// </summary>
    /// <remarks>
    /// 关着 = 模型写 <c>action=ask</c> 时安全静默（<c>ask_not_enabled</c>）。
    /// 打开后：模型想问就问，但**提问不是权限** —— 服务端只是把问题包一层自己的文案
    /// （带一次性编号与有效期）发到当前会话，回答也只是一条普通消息，不触发任何动作。
    /// </remarks>
    public bool EnableQuestions { get; set; }

    /// <summary>模型人设档案（可选，定义机器人角色的性格/说话风格）。</summary>
    public string BotPersona { get; set; } = string.Empty;

    /// <summary>是否开启 AI 自动回复（容器里无人点按钮，故用配置控制）。</summary>
    public bool AiModeEnabled { get; set; } = true;

    // ---------- 限流与节奏 ----------

    /// <summary>私聊回复冷却（秒）。</summary>
    public int PrivateCooldownSeconds { get; set; } = 3;

    /// <summary>群聊回复冷却（秒）。</summary>
    public int GroupCooldownSeconds { get; set; } = 8;

    /// <summary>静默兜底：超过该秒数没有主动请求时，为待处理会话补一次请求。0=关闭。</summary>
    public int IdleFallbackSeconds { get; set; } = 60;

    /// <summary>长回复按句末标点分句发送（更像真人打字）。</summary>
    public bool SplitReplies { get; set; } = true;

    /// <summary>
    /// 主动开口（陪伴感）：群里安静下来、又有人情绪低落或者刚聊得热闹时，它可能自己开一句。
    /// 不是“定时发广告”：还受同会话冷却（<see cref="ProactiveCooldownSeconds" />）、
    /// “最后一条不是自己说的”、“群聊限定”等多道限制，没由头就不出声。
    /// </summary>
    public bool EnableProactive { get; set; } = true;

    /// <summary>同一会话两次主动开口的最小间隔（秒），防自说自话；默认半小时一句。</summary>
    public int ProactiveCooldownSeconds { get; set; } = 1800;

    /// <summary>
    /// 群里要安静多久才算“安静下来”（秒）：没到这个时长就不主动插话（否则就是抢话）。
    /// 默认 120；测试/自己调折腾时可以调小。
    /// </summary>
    public int ProactiveQuietSeconds { get; set; } = 120;

    /// <summary>
    /// 把群友消息里的“括号旁白”标注成 <c>〔旁白：…〕</c>（如“（笑）”“（bushi）”“行（端在桌上）”）。
    /// 为什么要这个开关：群里这类旁白很多，它们不针对任何人，却会占上下文并可能把机器人拉出来接话
    /// （“（笑）”接什么？）。但**管理员的口径是不要单纯忽略，也要接收、只要特别注明** ——
    /// 所以打开后旁白不会被丢掉，而是带标注进聊天记录与模型上下文（模型知道那是动作/表情说明，
    /// 不是他说的话），纯旁白不单独触发一次回复。
    /// 判定口径：只处理开头/结尾的括号段，剥完只剩空白与标点才算“整条旁白”；
    /// 句子中间的括号（“（2026）年的计划”）不碰。
    /// 只影响群聊；带图、带 @ 机器人、私聊的消息永远不动（宁可多回也不装死）。
    /// </summary>
    public bool IgnoreBracketMessages { get; set; }

    /// <summary>禁止回复夹带动作/心理描写。发送前由模型仅删除或局部自然改写确认的动作旁白；数学公式、普通括号、代码和链接保持原文，判断失败则保留整条回复。</summary>
    public bool FilterActionNarration { get; set; }

    /// <summary>启用群聊氛围感知与发言疲劳阻尼冷却（连续发言多次后自动衰减欲望，冷清/对线时克制，防止刷屏）。</summary>
    public bool EnableAtmosphereDamping { get; set; } = true;

    /// <summary>分段发送时每段之间的时间基准（毫秒）。</summary>
    public int SegmentDelayMs { get; set; } = 700;

    /// <summary>给模型的最大上下文消息条数。</summary>
    public int MaxContextMessages { get; set; } = 200;

    /// <summary>回复时附带上下文里出现的人物档案数量上限。</summary>
    public int ProfileLookupCount { get; set; } = 8;

    /// <summary>每个人物档案向模型注入的历史条数上限。</summary>
    public int ProfileSummaryLines { get; set; } = 8;

    /// <summary>人物档案注入的总字符预算（超出则丢弃最早发言者的档案）。</summary>
    public int MaxProfileChars { get; set; } = 1200;

    /// <summary>用模型把历史发言压缩成人物画像（长期记忆）。</summary>
    public bool EnableProfileSummary { get; set; } = true;

    /// <summary>某会话范围累计多少条新发言后重新做一次画像。</summary>
    public int ProfileSummaryThreshold { get; set; } = 20;

    /// <summary>画像字数上限。</summary>
    public int ProfileSummaryMaxChars { get; set; } = 160;

    /// <summary>画像巡检间隔（秒）。0 = 关闭。</summary>
    public int ProfileSummaryIntervalSeconds { get; set; } = 120;

    /// <summary>单个会话在内存/磁盘保留的消息条数（超出部分归档到 archive/*.jsonl）。</summary>
    public int MaxMessagesPerConversation { get; set; } = 500;


}
