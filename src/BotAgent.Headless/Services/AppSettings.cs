using System.Text.Json;
using System.Text.Json.Serialization;

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
public sealed class AppSettings
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

    // ---------- OneBot 通道 ----------

    /// <summary>ForwardWebSocket（推荐）/ ReverseWebSocket / Http。</summary>
    public string OneBotProtocol { get; set; } = "ForwardWebSocket";

    /// <summary>正向：ws://napcat:3001；反向：http://0.0.0.0:3001；HTTP：http://napcat:3000。</summary>
    public string OneBotAddress { get; set; } = "ws://127.0.0.1:3001";

    /// <summary>OneBot 访问令牌。环境变量专属（QQCHAT_ONEBOT_TOKEN），不写入 settings.json。</summary>
    [JsonIgnore]
    public string OneBotToken { get; set; } = string.Empty;

    /// <summary>机器人登录 QQ 号（用于自我识别与 @ 判断；沿用旧字段名，老配置才不失效）。</summary>
    public string QuickLoginUin { get; set; } = string.Empty;

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

    /// <summary>消息白名单（**旧字段**：群聊与私聊共用一份名单；留空 = 全部忽略，严格模式）。
    /// 2026-09-18 起拆成两份（<see cref="WhitelistGroups" /> / <see cref="WhitelistPrivates" />）——
    /// 旧值仍然生效：哪一边的新字段留空，那一边就回落到这份共用名单（老配置不用动）。</summary>
    public string MessageWhitelist { get; set; } = string.Empty;

    /// <summary>**群聊**白名单（每行/逗号分隔群号；<c>*</c> = 所有群）。留空 = 回落到旧的共用名单。</summary>
    public string WhitelistGroups { get; set; } = string.Empty;

    /// <summary>**私聊**白名单（每行/逗号分隔 QQ 号；<c>*</c> = 所有人）。留空 = 回落到旧的共用名单。</summary>
    public string WhitelistPrivates { get; set; } = string.Empty;

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
    /// （“（笑）”接什么？）。但**号主的口径是不要单纯忽略，也要接收、只要特别注明** ——
    /// 所以打开后旁白不会被丢掉，而是带标注进聊天记录与模型上下文（模型知道那是动作/表情说明，
    /// 不是他说的话），纯旁白不单独触发一次回复。
    /// 判定口径：只处理开头/结尾的括号段，剥完只剩空白与标点才算“整条旁白”；
    /// 句子中间的括号（“（2026）年的计划”）不碰。
    /// 只影响群聊；带图、带 @ 机器人、私聊的消息永远不动（宁可多回也不装死）。
    /// </summary>
    public bool IgnoreBracketMessages { get; set; }

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

    // ---------- 表情包（全局共用一个库） ----------

    /// <summary>启用表情包：自动收集群友发的图 + 按语境发出去 + 定期自巡检。</summary>
    public bool EnableStickers { get; set; } = true;

    /// <summary>表情包库存储上限（张）。超出按“用得少 + 最久没用”淘汰。</summary>
    public int StickerLibraryMax { get; set; } = 120;

    /// <summary>每次给模型看的候选张数（按语境检索出来的）。</summary>
    public int StickerCandidates { get; set; } = 6;

    /// <summary>表情包自巡检间隔（秒）：机器人自己看一遍库，决定删哪些。0 = 关。</summary>
    public int StickerCurateIntervalSeconds { get; set; } = 3600;

    /// <summary>同一会话两次发表情包的最小间隔（秒）。0 = 不限。</summary>
    public int StickerCooldownSeconds { get; set; } = 120;

    /// <summary>戳一戳：是否处理戳一戳事件（被人戳时按语境回话或戳回去）。</summary>
    public bool EnablePoke { get; set; } = true;

    /// <summary>
    /// 戳一戳冷却（秒）：同一个人连着戳时至少隔这么久才回应一次；
    /// 也是机器人主动戳人的最小间隔。0 = 不限。
    /// </summary>
    public int PokeCooldownSeconds { get; set; } = 45;

    /// <summary>
    /// 模型写的心情保留多久（秒）：超过这个时间没更新就回落到“按被戳次数自动描述”。
    /// 0 = 不过期（一直用模型写的那句）。
    /// </summary>
    public int MoodTtlSeconds { get; set; } = 7200;

    /// <summary>同时向模型发起的最大请求数（按会话串行、跨会话并发）。</summary>
    public int MaxConcurrentReplies { get; set; } = 2;

    // ---------- 对话总开关（两个通道各自可单独静音） ----------

    /// <summary>
    /// 私域通道（自建 NapCat/OneBot）收不收消息、要不要回。
    /// 与面板顶部那个「AI 开/关」不是一回事：那个是**全局**（连人都回不了），
    /// 这个是**按通道**（比如官方那条被限制/在调试时，只把官方静音，私域照旧）。
    /// </summary>
    public bool PrivateChatEnabled { get; set; } = true;

    /// <summary>官方通道（QQ 开放平台）收不收消息、要不要回。默认开。</summary>
    public bool OfficialChatEnabled { get; set; } = true;

    // ---------- 语音消息（TTS）----------

    /// <summary>
    /// 启用语音消息：机器人可以用语音说话（模型在 JSON 里填 speak 字段时）。
    /// 默认关 —— 语音比文字“重”，且需要先部署 TTS 服务。
    /// </summary>
    public bool EnableVoice { get; set; }

    /// <summary>
    /// 语音回复积极性 0~100（面板滑杆）。
    /// 它的意思不是“必须发多少条语音”，而是**把分寸交给面板**：
    ///   • 提示词会把这个数告诉模型（如“当前积极性 70/100 → 偏积极”），让它自己校准用得多不多；
    ///   • 同会话的最小间隔也跟着缩放（0 → 2 分钟、50 → 45 秒、100 → 15 秒），
    ///     所以“很积极”真能多说两句，而不是被一道写死的 45 秒门卡住。
    /// </summary>
    public int VoiceEagerness { get; set; } = 50;

    /// <summary>音色（Piper 模型名）。可选：zh_CN-huayan-medium / zh_CN-huayan-x_low / zh_CN-xiao_ya-medium / zh_CN-chaowen-medium。</summary>
    public string VoiceName { get; set; } = "zh_CN-huayan-medium";

    /// <summary>语速（百分比，100 = 原速）。</summary>
    public int VoiceSpeed { get; set; } = 100;

    /// <summary>音调（-12~+12；0 = 不传，用云端默认）。</summary>
    public int VoicePitch { get; set; }

    /// <summary>音量（10~1000 = 0.1~10.0 倍；0 = 不传，用云端默认）。</summary>
    public int VoiceVol { get; set; }

    /// <summary>情绪（happy/sad/angry/surprised/fearful/disgusted/neutral；空 = 不传）。
    /// 只有部分云端音色认这个参数，认不了的会被云端忽略（不会报错）。</summary>
    public string VoiceEmotion { get; set; } = "";

    /// <summary>单条语音的字数上限：超过就不发语音（长了又慢又费流量，不如打字）。</summary>
    public int VoiceMaxChars { get; set; } = 80;

    /// <summary>TTS 服务地址（云端 TTS 旁路容器，提供 /speak?text=… 返回音频）。</summary>
    /// <remarks>
    /// 2026-09-21 起这个地址后面接的是**云端 TTS 代理**（<c>tools/tts-cloud-server.py</c>，
    /// 转发到 MiniMax / OpenAI 兼容接口），不再是本地 Piper。容器名与服务名都没变，
    /// 所以老部署只要把 <c>tts</c> 服务换成新镜像就完事了；契约仍是
    /// <c>GET /speak?text=&amp;voice=&amp;speed=</c> 返 wav（面板试听按 RIFF 校验）。
    /// 注意：**语音名要跟着厂商走**（Piper 的 <c>zh_CN-huayan-medium</c> 云端不认，
    /// 代理会回落成它自己的默认音色；想指定就去面板里选一个云端音色）。
    /// </remarks>
    public string TtsServiceUrl { get; set; } = "http://tts:5000";

    /// <summary>语音合成走哪个云端服务商：<c>minimax</c>（默认）| <c>openai</c>（任何兼容 /v1/audio/speech 的）。</summary>
    public string TtsProvider { get; set; } = "minimax";

    /// <summary>
    /// 云端接口地址（面板可改）。留空 = 用 tts 容器环境变量里的默认值。
    /// 留这个口子是因为同一家厂商有国内站/国际站两个域名（key 与站点要配对），
    /// 不对时值得当场改，而不是去重建容器。
    /// </summary>
    public string TtsApiBase { get; set; } = string.Empty;

    /// <summary>云端模型名（面板可改；留空 = 用容器默认，如 MiniMax 的 speech-2.8-hd）。</summary>
    public string TtsModel { get; set; } = string.Empty;

    // ---------- 官方通道（QQ 开放平台） ----------

    /// <summary>
    /// 开官方通道（QQ 开放平台的机器人，与私域 NapCat 那条并存）。
    /// 默认**关**：它需要开放平台申请的 appid/secret，没配的话开了也连不上。
    /// </summary>
    public bool OfficialEnabled { get; set; }

    /// <summary>开放平台的机器人 appid（环境变量 QQCHAT_OFFICIAL_APP_ID）。</summary>
    public string OfficialAppId { get; set; } = string.Empty;

    /// <summary>开放平台的机器人 secret。**密钥**：只从环境变量读，不落盘。</summary>
    [JsonIgnore]
    public string OfficialAppSecret { get; set; } = string.Empty;

    /// <summary>用沙箱环境连（沙箱只能收/发沙箱群与沙箱单聊，调试用；正式上线关掉）。</summary>
    public bool OfficialSandbox { get; set; }

    /// <summary>
    /// 官方通道的群白名单（里的是**别名号**，见 <c>Channels.AliasBase</c>；
    /// 面板会话列表里会显示出来）。留空 = 全部接受 ——
    /// 官方平台本身有准入（只有加了机器人的群才能收到消息）与每日额度，没必要再卡一道。
    /// ⚠ 与私域那份白名单**不共用**：那份里写的是真实群号，混在一起会变成“官方通道永远被拦”。
    /// </summary>
    public string OfficialWhitelistGroups { get; set; } = string.Empty;

    /// <summary>官方通道的私聊（单聊）白名单；留空 = 全部接受。</summary>
    public string OfficialWhitelistPrivates { get; set; } = string.Empty;

    /// <summary>
    /// 官方 REST 接口根地址。留空 = 按沙箱开关自动选（正式 <c>https://api.bot.qq.com</c> /
    /// 沙箱 <c>https://sandbox.api.sgroup.qq.com</c>）。
    /// 留这个口子是为了**能测**：集成测试要把它指到本地的假官方网关上去。
    /// </summary>
    public string OfficialApiBase { get; set; } = string.Empty;

    /// <summary>取 access_token 的地址，留空 = <c>https://bots.qq.com/app/getAppAccessToken</c>（同样是为了可测）。</summary>
    public string OfficialTokenUrl { get; set; } = string.Empty;

    // ---------- 链接与分享卡片 ----------

    /// <summary>群里发的链接要不要真打开看一下（取标题/摘要）——给模型“看看里面写了什么”的根据。</summary>
    public bool EnableLinkPreview { get; set; } = true;

    /// <summary>单个链接的抓取超时（秒）。慢站点不能拖住机器人。</summary>
    public int LinkPreviewTimeoutSeconds { get; set; } = 5;

    /// <summary>一条消息里最多预览几个链接。</summary>
    public int LinkPreviewMax { get; set; } = 3;

    // ---------- 联网搜索（模型可以要求“去查一下”） ----------

    /// <summary>启用联网搜索：模型在 JSON 里填 search 字段时，机器人真去搜，拿到事实后再回。默认开。</summary>
    public bool EnableWebSearch { get; set; } = true;

    /// <summary>
    /// 优先用“模型自带搜索”（OpenAI 兼容网关背后的 Gemini 原生端点 + google_search 工具）。
    /// 为什么默认走它：机房 IP 上爬网页搜索基本拿不到结果（Google/Bing/DDG/百度 都拦），
    /// 而模型订阅本来就能搜 —— 不额外要密钥、不爬虫、结果还带来源。
    /// 关掉就只用下面的搜索源模板。
    /// </summary>
    public bool WebSearchUseModelSearch { get; set; } = true;

    /// <summary>
    /// 搜索源模板（每行一条，name|url；{q} = 查询词，会做 URL 编码）。兜底用：
    /// 模型搜索不可用（不是 Gemini、代理不转发工具）时才走这里。
    /// name 决定解析方式：searx* = SearxNG JSON；wiki* = MediaWiki JSON；其余当通用 HTML 抽链接。
    /// 默认给 Wikipedia（实测这台服务器上唯一能直接用的）。
    /// </summary>
    public string WebSearchSources { get; set; } =
        "wiki|https://zh.wikipedia.org/w/api.php?action=query&list=search&srsearch={q}&format=json&srlimit=5&utf8=1";

    /// <summary>每次给模型看几条搜索结果。</summary>
    public int WebSearchMaxResults { get; set; } = 5;

    /// <summary>
    /// 同一个会话两次联网搜索的最小间隔（秒）；0 = 不限。
    /// 为什么要有它：一次搜索 = 一次真实模型调用 + 几秒等待，群里连问几个问题就排队了。
    /// 为什么做成设置项：不同部署的网络快慢、群多少差很多，写死 30 秒众口难调。
    /// </summary>
    public int WebSearchCooldownSeconds { get; set; } = 30;

    /// <summary>搜索/读页面的超时（秒）。</summary>
    public int WebSearchTimeoutSeconds { get; set; } = 20;

    /// <summary>read 页面正文截断长度（字）。</summary>
    public int WebSearchReadMaxChars { get; set; } = 1800;

    // ---------- 听音乐（识别群里的音乐分享 + 网易云歌词 + 波形分析） ----------

    /// <summary>启用“听音乐”：有人分享歌时，自动查歌词、下一份低码率音频分析波形，再交模型接话。</summary>
    public bool EnableMusic { get; set; } = true;

    /// <summary>
    /// 音源模板（分号分隔，按顺序尝试）。占位符：{id} 歌曲 id、{br} 码率、{crc32} 标准 CRC32 的 8 位大写十六进制。
    /// 这类公开音源都是第三方服务，随时会挂、会改参数 —— 所以做成可配置：挂了就在面板里换一条，不用改代码。
    /// </summary>
    public string MusicSources { get; set; } =
        "meting|https://api.qijieya.cn/meting/?type=url&id={id}&br={br};" +
        "gdstudio|https://music-api.gdstudio.xyz/api.php?types=url&source=netease&id={id}&br={br}&s={crc32}";

    /// <summary>下载音频的码率（低码率足够分析波形，也省流量）。</summary>
    public int MusicBitrate { get; set; } = 128;

    /// <summary>
    /// 网易云接口地址（默认官方）。做成可配置是为了能指向自建代理或测试用的假接口；
    /// 服务器在海外时官方接口的音频部分会被地域限制，但歌词/详情没问题。
    /// </summary>
    public string NeteaseBaseUrl { get; set; } = "https://music.163.com";

    /// <summary>单个音频文件体积上限（MB），超过就跳过该音源。</summary>
    public int MusicMaxDownloadMb { get; set; } = 12;

    /// <summary>最多分析多少秒（超出部分截断，控制 CPU）。</summary>
    public int MusicMaxAnalysisSeconds { get; set; } = 180;

    /// <summary>“听过的歌”台账上限（条）。</summary>
    public int MusicLibraryMax { get; set; } = 300;

    /// <summary>分析结果保留天数：超过就重新分析一次（音源/算法可能变过）。</summary>
    public int MusicNoteTtlDays { get; set; } = 30;

    /// <summary>模型想听一首歌时的最小间隔（秒），防同一个话题反复搜歌；0 = 不限。</summary>
    public int MusicListenCooldownSeconds { get; set; } = 120;

    /// <summary>
    /// 专门用来“听”的音频识别模型（**留空 = 不听**，只用手写 DSP 的客观数据）。
    /// 为什么要单独配：很多网关/中转会把音频静默丢掉，主模型根本收不到声音 ——
    /// 这时配一个确实支持音频输入、而且该网关愿意转发的模型，才能“听”出曲风情绪。
    /// 默认留空：具体填哪个模型完全取决于你用的网关，不在代码里替用户指定。
    /// </summary>
    public string MusicUnderstandModel { get; set; } = string.Empty;

    /// <summary>是否把音频片段交给上面的音频识别模型（关掉 = 只用 DSP 实测数据）。</summary>
    public bool MusicSendAudioToModel { get; set; } = true;

    /// <summary>交给模型的音频最多多少 KB（默认 1MB ≈ 60 秒 128kbps）。</summary>
    public int MusicAudioToModelMaxKb { get; set; } = 1024;

    /// <summary>是否把下载的音频留在 data/music/audio（默认不留：服务器上不攒版权内容）。</summary>
    public bool MusicKeepAudio { get; set; }

    /// <summary>
    /// 网易云 Cookie（可选）：带上登录态能少踩一些接口限制。
    /// 属于密钥，环境变量专属（QQCHAT_NETEASE_COOKIE），不写入 settings.json、面板只显示是否已设置。
    /// </summary>
    [JsonIgnore]
    public string NeteaseCookie { get; set; } = string.Empty;

    // ---------- 运维 ----------

    /// <summary>
    /// 列出会话时**脱敏**（群名/昵称/QQ 号）：群里 //sessions //sessions all //runs //pi 的回复、
    /// 面板的会话列表与总览都会遮。默认开（截图/给别人看时不漏隐私）；要原样看就在面板里关掉。
    /// </summary>
    public bool AgentMaskSensitive { get; set; } = true;

    /// <summary>
    /// Agent 附加提示词（面板可改，默认 = <see cref="DefaultAgentPrompt" /> 那条隐私红线）：
    /// **每个 <c>//</c> 任务都会带上它** —— 外部设备（pi）是拼在任务前面，服务器内置 agent 是拼进
    /// 系统提示词。所以“别去读群聊内容 / 成员隐私”不是靠自觉，而是每一轮都随任务下发；
    /// 留空 = 不带任何附加提示词。
    /// </summary>
    public string AgentPrompt { get; set; } = DefaultAgentPrompt;

    /// <summary>
    /// 默认的附加提示词：开发/排查时的隐私红线。面板上的「恢复默认」按钮也读它 ——
    /// 要改默认值就改这里（别再在面板文案里抄一份，免得两处漂移）。
    /// </summary>
    public const string DefaultAgentPrompt =
        "【隐私红线（优先级最高）】\n" +
        "1. 不要读取、不要复述聊天内容与成员信息：别直接打开 conversations.json、agent-sessions.json、" +
        "member_profiles/、logs/qqchat.log 里的**对话正文**，也不要把它们粘进回复、提交、测试或文档。\n" +
        "2. 排查报错只看日志里的 ERROR / Exception 堆栈：先把中文（发言、昵称）换成占位符再看，" +
        "message / raw_message 这类文本字段一律不看。\n" +
        "3. 判断数据形状就看字段名、条数、长度、哈希，不看内容。\n" +
        "4. 已经看到的敏感内容不外传：不回群、不写进仓库、不发第三方接口。";

    /// <summary>面板访问令牌（可空）。设置后访问面板与 /api/* 需携带令牌；
    /// /healthz 与 /readyz 不受影响（留给容器健康检查）。
    /// 环境变量专属（QQCHAT_PANEL_TOKEN），不写入 settings.json。</summary>
    [JsonIgnore]
    public string PanelToken { get; set; } = string.Empty;

    // ══════════ 本机 Agent 桥（// 命令，详见 handoff-4 §31）══════════
    //
    // 为什么是“桥”而不是让机器人自己跑：机器人跑在服务器容器里，而 agent 需要的是**号主本机的能力**
    //（他的代码目录、工具链、pi 的登录态）。所以本机跑一个小进程，主动连到机器人，
    // 收到任务就把它交给本机的 pi CLI，把输出回传 —— 服务器上不装、不跑任何东西。

    /// <summary>总开关。关着的时候 // 开头的消息跟普通消息一样走人设路线（不会进 agent）。</summary>
    public bool EnableAgentBridge { get; set; }

    /// <summary>命令前缀（默认 <c>//</c>）：消息**开头**是这个才当 agent 命令。</summary>
    public string AgentPrefix { get; set; } = "//";

    /// <summary>允许使用 agent 的 QQ 号（逗号/空格/换行分隔）。
    /// **空 = 谁都不能用** —— 这是默认值：这个功能能在号主电脑上执行命令，宁可先不给任何人。</summary>
    public string AgentAllowedUsers { get; set; } = string.Empty;

    /// <summary>本机 pi 的工作目录（桥侧执行目录）。留空 = 桥自己的默认目录。</summary>
    public string AgentWorkDir { get; set; } = string.Empty;

    /// <summary>传给 pi 的模型（留空 = 本机 pi 默认模型）。这是**没在 AgentDevices 里单独配**时的默认值。</summary>
    public string AgentModel { get; set; } = string.Empty;

    /// <summary>
    /// **每台外部设备的独立配置**（JSON 数组字符串；面板里能增删改）：
    /// <code>[{"name":"ZHAOSPC","enable":true,"model":"vendor/model","workdir":"E:/bot","tools":"","timeoutSec":900}]</code>
    /// 为什么用 JSON 字符串而不是强类型列表：设置是跟面板直接对齐的文本，
    /// 而且容器是 trimmed 发布（反射序列化会抛 resolver 错）—— 字符串零风险。
    /// 泛用性：设备是任意多台、名字任意，面板里加一行就是一台新设备。
    /// </summary>
    public string AgentDevices { get; set; } = string.Empty;

    /// <summary>一面设备的配置（面板里可改；没配的字段就走全局默认）。</summary>
    public sealed record DeviceConfig(
        string Name,
        bool Enable = true,
        string? Model = null,
        string? WorkDir = null,
        string? Tools = null,
        int TimeoutSeconds = 0);

    /// <summary>
    /// 解析 AgentDevices（JSON 数组字符串）。用 JsonDocument 而不是反序列化成类型：
    /// 容器是 trimmed 发布，反射反序列化会抛 TypeInfoResolver 异常（实测踩过）。
    /// </summary>
    public static List<DeviceConfig> ParseDeviceConfigs(string? json)
    {
        var list = new List<DeviceConfig>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? (n.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (name.Length == 0)
                {
                    continue;
                }

                list.Add(new DeviceConfig(
                    name,
                    !item.TryGetProperty("enable", out var e) || e.ValueKind != JsonValueKind.False,
                    Str(item, "model"),
                    Str(item, "workdir"),
                    Str(item, "tools"),
                    Num(item, "timeoutSec")));
            }
        }
        catch (JsonException)
        {
            // 配置写坏了就当没配（不能用一条坏 JSON 让整个 agent 功能不可用）
            return list;
        }

        return list;

        static string? Str(JsonElement obj, string key)
            => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()?.Trim()
                : null;

        static int Num(JsonElement obj, string key)
            => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    }

    /// <summary>取某台设备的配置（没有就返回 null = 走全局默认）。</summary>
    public DeviceConfig? DeviceConfigFor(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        return ParseDeviceConfigs(AgentDevices)
            .FirstOrDefault(d => string.Equals(d.Name, deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>外部任务和状态使用同一目录优先级：设备配置、全局默认、桥启动目录。</summary>
    public string? ResolveAgentWorkDir(string? deviceName, string? bridgeDirectory = null)
    {
        var configured = DeviceConfigFor(deviceName)?.WorkDir;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        return !string.IsNullOrWhiteSpace(AgentWorkDir) ? AgentWorkDir.Trim() : bridgeDirectory;
    }

    /// <summary>看设备是否被面板里手动关掉（关了就不派任务，并如实告诉用户）。</summary>
    public bool IsDeviceEnabled(string? deviceName)
        => DeviceConfigFor(deviceName)?.Enable ?? true;


    /// <summary>工具白名单（逗号分隔，对应 pi 的 <c>--tools</c>）。留空 = 本机 pi 默认（全部工具）。</summary>
    public string AgentTools { get; set; } = string.Empty;

    /// <summary>单个任务最长跑多久（秒），超时就让桥把 pi 杀掉。</summary>
    public int AgentTimeoutSeconds { get; set; } = 900;

    /// <summary>回群时单条消息最多多少字（超了就分成几条发）。</summary>
    public int AgentReplyMaxChars { get; set; } = 800;

    /// <summary>任务跑太久时多久报一次“还在跑”（秒；0 = 不报）。</summary>
    public int AgentProgressSeconds { get; set; } = 90;

    /// <summary>同一个会话最多排几个任务（超出直接拒，不让它变无限队列）。</summary>
    public int AgentMaxQueued { get; set; } = 3;

    /// <summary>桥的共享密钥（环境变量专属 QQCHAT_AGENT_TOKEN）。
    /// 这个端口能让人在号主电脑上执行命令 —— 没设令牌就不接受任何桥连接（不是“默认放行”）。</summary>
    [JsonIgnore]
    public string AgentToken { get; set; } = string.Empty;

    // ── 两个后端 + 路由（号主 2026-09-17：内外 agent 公用一份白名单）──

    /// <summary>服务器**内置** agent（跑在容器里的工具循环：bash / 读 / 写 / 抓网页）。</summary>
    public bool EnableServerAgent { get; set; } = true;

    /// <summary>**外部设备** agent（号主电脑上的 pi，通过桥接接入）。
    /// 与服务器 agent 互不影响：不想用哪边就把哪边的开关关掉（不关也能用 //@server / //@host 单次指定）。</summary>
    public bool EnableHostAgent { get; set; } = true;

    /// <summary>任务走哪边（这是个**优先/指定**，两个开关都开时生效）：
    ///   <c>auto</c>（默认）= 外部设备在线就用外部，否则用服务器内置；
    ///   <c>server</c> = 只用服务器内置；
    ///   <c>host</c> = 只用外部设备（离线就如实报错）；
    ///   也可写**设备名**（如 <c>ZHAOSPC</c>）——多台设备时指定用哪台。
    /// 群里还能单条覆盖：<c>//@server 看下日志</c> / <c>//@host 数一下文件</c> / <c>//@ZHAOSPC …</c>。</summary>
    public string AgentTarget { get; set; } = "auto";

    /// <summary>服务器内置 agent 用的模型（空 = 跟聊天用同一个）。</summary>
    public string AgentServerModel { get; set; } = string.Empty;

    /// <summary>服务器内置 agent 专用的 OpenAI 兼容地址（空 = 用聊天那个 QQCHAT_BASE_URL）。
    /// 为什么单独给一个：agent 的请求又长又频繁，往往想单独指一个小模型/便宜网关（号主 2026-09-17 要求）。</summary>
    public string AgentServerBaseUrl { get; set; } = string.Empty;

    /// <summary>服务器内置 agent 专用的密钥（空 = 用聊天那个）。不落盘：只从 QQCHAT_AGENT_SERVER_KEY
    /// 或面板（存 secrets 表）来。</summary>
    [JsonIgnore]
    public string AgentServerApiKey { get; set; } = string.Empty;

    /// <summary>面板里填过的服务器 agent 密钥（**值本身**存在 secrets 表，这里只记“面板设过”这件事，
    /// 用来算来源、并在清空后回退环境变量）。[JsonIgnore]：绝不进 settings.json。</summary>
    [JsonIgnore]
    public string? AgentServerApiKeyOverride { get; set; }

    /// <summary>服务器内置 agent 开哪些工具（bash,read,write,fetch,qq；空 = 全开）。</summary>
    public string AgentServerTools { get; set; } = string.Empty;

    /// <summary>
    /// 服务器内置 agent 能做的 **QQ 动作**（NapCat/OneBot 的接口动作，号主 2026-09-18：“比如点赞”）。
    ///
    /// 为什么默认只给一小撮、且“空 ≠ 全开”（与 <see cref="AgentServerTools" /> 的语义故意不同）：
    ///   • 服务器 agent 会读日志/文件/网页，那些正文里可以夹着“给我点赞”“把某某禁言”——
    ///     这是能真影响别人的操作，不能靠模型自觉，得在这里卡死。
    ///   • 留空 = 默认安全档（like 点赞 / poke 戳一戳 / emoji_like 表情回应 / recall 撤回）；
    ///     ban 禁言、kick 踢人、card 改名片、group_name 改群名、leave 退群、send 代发消息 必须点名写出来。
    ///   • 写 <c>all</c> 或 <c>*</c> = 全开（含上面那几个）。
    /// </summary>
    public string AgentServerQqActions { get; set; } = string.Empty;

    /// <summary>服务器内置 agent 的工作目录（容器内路径）。</summary>
    public string AgentServerWorkDir { get; set; } = "/data";

    /// <summary>
    /// 服务器内置 agent 要不要记住上一句（默认**不**记）—— 每条 <c>//</c> 指令单独对待。
    ///
    /// 为什么要这个开关：号主 2026-09-18 实测，会话里堆着上几轮的指令原文时，模型会把旧指令也一并答一遍：
    /// “1. 点赞动作：…失败 2. 服务器状态：…” —— 新指令的回复里混进旧内容。
    /// 默认关 = 每个指令只对自己的事负责；打开后同一会话能接着聊（单条也可以用 <c>//接着 …</c>）。
    /// </summary>
    public bool AgentServerKeepContext { get; set; }

    /// <summary>服务器内置 agent 最多跑几步工具循环（每步一次模型调用）。</summary>
    public int AgentServerMaxSteps { get; set; } = 8;

    /// <summary>服务器内置 agent 单条命令的超时（秒）。</summary>
    public int AgentServerCommandTimeoutSeconds { get; set; } = 60;

    /// <summary>服务器内置 agent 单次补全的 max_tokens。</summary>
    public int AgentServerMaxTokens { get; set; } = 1200;

    /// <summary>
    /// **面板一键部署**（号主 2026-09-18 要的；默认关）。
    /// 打开后：面板能上传/拉取 `app.tar.gz`，并驱动宿主 docker 重建镜像 + 替换自己（容器自我更新）。
    /// 为什么默认关 + 单独一个开关：它等于把“重装机器人”的按钮放到网页上；
    /// 虽然面板本来就有口令门，但这种事应该是个显式决定。
    /// </summary>
    public bool PanelDeployEnabled { get; set; }

    /// <summary>上次用过的产物地址（URL 方式部署会记住：以后一个按钮就能拉）。空 = 没配。</summary>
    public string PanelDeployUrl { get; set; } = string.Empty;

    /// <summary>
    /// 允许服务器 agent **透过 docker 操作服务器**（号主 2026-09-18 要的能力）：
    /// 打开后它能 `docker ps / logs / exec / run -v /:/host …`，也能直接读写 /host/qqchat（部署目录）。
    ///
    /// 这是**高权限**开关（docker.sock ≈ root）：关着的时候它的工具表里没有 docker、提示词也不提这两条路径。
    /// 但 socket 与 /host/qqchat 是**常挂载**的（compose 的挂载不能运行时改）——
    /// 所以这个开关是“告诉 agent 能不能用 + 号主确认过风险”，不是硬隔离。默认关。
    /// </summary>
    public bool AgentServerDocker { get; set; }

    // ══════════ 服务器健康日报（定时私聊推送）══════════
    //
    // 为什么要它：机器人跑在服务器上，出问题时（账号掉线、协议端断开、磁盘写满、上游接口不通）
    // 号主往往几天后才发现。每天在固定时刻主动私聊一条状态，等于**机器人自己来报平安**。
    //
    // 为什么不用「外部设备 agent」来推（号主要求）：那台机器可能根本没开 ——
    // 这条链路只用机器人自己 + 协议端：定时器 → 自己采集状态 → OneBot 私聊消息，不依赖任何外部程序。

    /// <summary>总开关（默认关：没配好收件人之前不乱发）。</summary>
    public bool HealthReportEnabled { get; set; }

    /// <summary>
    /// 每天几点推（<c>HH:mm</c>，24 小时制）。**按北京时间（UTC+8）算**，与容器 TZ 无关 ——
    /// 号主在国内，容器时区怎么设都不该让推送时间漂掉。
    /// </summary>
    public string HealthReportTime { get; set; } = "18:00";

    /// <summary>推给谁：QQ 号，逗号/空格/换行分隔（只走私聊，填群号也不会发到群里）。空 = 不发。</summary>
    public string HealthReportTargets { get; set; } = string.Empty;

    /// <summary>解析 <see cref="HealthReportTime" />（容忍 <c>18:00</c>/<c>18：00</c>/<c>1800</c>）。解析不出来按 18:00。</summary>
    public static (int Hour, int Minute) ParseHealthReportClock(string? text)
    {
        var raw = (text ?? string.Empty).Trim().Replace('：', ':');
        if (raw.Length is 4 && int.TryParse(raw, out var packed))
        {
            raw = $"{packed / 100:00}:{packed % 100:00}";
        }

        var parts = raw.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out var hour) && int.TryParse(parts[1], out var minute) &&
            hour is >= 0 and <= 23 && minute is >= 0 and <= 59)
        {
            return (hour, minute);
        }

        return (18, 0);
    }

    /// <summary>健康检查 HTTP 端口（0=关闭）。用于容器 HEALTHCHECK。</summary>
    public int HealthPort { get; set; } = 8080;

    /// <summary>是否输出详细日志到 stdout。</summary>
    public bool VerboseLog { get; set; } = true;

    /// <summary>旧版遗留字段（本版本不使用）。</summary>
    public string Theme { get; set; } = "Default";

    // ---------- 派生（不参与序列化，避免污染 settings.json） ----------

    /// <summary>规范化后的登录 QQ 号（空字符串按未配置处理）。</summary>
    [JsonIgnore]
    public string NormalizedUin => QuickLoginUin?.Trim() ?? string.Empty;

    /// <summary>登录 QQ 号；解析失败返回 0。</summary>
    [JsonIgnore]
    public long UinOrZero => long.TryParse(NormalizedUin, out var u) ? u : 0;

    // ---------- NapCat WebUI（面板内扫码登录） ----------
    // 只用于把登录二维码搬进机器人面板；不影响 OneBot 消息通道。

    /// <summary>NapCat WebUI 地址（容器网络里的服务名即可）。环境变量专属。</summary>
    [JsonIgnore]
    public string NapCatWebUiUrl { get; set; } = "http://napcat:6099";

    /// <summary>NapCat WebUI 令牌（napcat/config/webui.json 的 token 字段）。环境变量专属，不写入 settings.json。</summary>
    [JsonIgnore]
    public string NapCatWebUiToken { get; set; } = string.Empty;

    // ---------- 快照 ----------

    /// <summary>
    /// 取一份配置快照（浅拷贝；这个类型只有标量字段，拷贝出来就是独立的一份）。两个用途：
    ///   • **读**：一次处理开始时固定它，处理过程中一律读快照 —— 面板热更新只影响后续处理，
    ///     不会改到在途请求（V3 §5.3）。见 BotAgentHost.GenerateReplyAsync。
    ///   • **写**：<see cref="SettingsBox.Apply" /> 在副本上改完再整体发布（review-findings #4）。
    /// 只读用途时：不要对副本调用保存 / 写回。
    /// </summary>
    public AppSettings Snapshot() => (AppSettings)MemberwiseClone();
}
