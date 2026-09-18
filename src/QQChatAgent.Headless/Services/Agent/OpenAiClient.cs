using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QQChatAgent.Models;

namespace QQChatAgent.Services.Agent;

/// <summary>
/// Agent 大脑：OpenAI 兼容 Chat Completions 客户端。
/// 由用户自配 Base URL / API Key / 模型，可对接 OpenAI、DeepSeek、通义、本地 Ollama 等。
/// </summary>
public sealed class OpenAiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // 每个客户端一个下载器实例（里面有缓存）：它要拿到“重新签发图片地址”的回调，不能做成静态的
    private readonly ImageDownloader _imageDownloader = new();

    /// <summary>
    /// 图片地址过期（QQ 的 rkey 时效）时，让协议端重新签发地址的回调（由 BotAgent 接到 IQqChatSource 上）。
    /// 不接也能跑，只是过期图这轮看不到。
    /// </summary>
    public Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshImageUrls
    {
        get => _imageDownloader.RefreshUrls;
        set => _imageDownloader.RefreshUrls = value;
    }

    /// <summary>图片缓存命中数 / “过期后重新签发取回”次数（测试与排障用）。</summary>
    public int ImageCacheHits => _imageDownloader.CacheHits;
    public int ImageRefreshedCount => _imageDownloader.RefreshedCount;

    private AppSettings _settings;

    public OpenAiClient(AppSettings settings) => _settings = settings;

    /// <summary>本机登录的机器人 QQ 号（注入模型上下文，帮助理解 @ 与身份）。</summary>
    public string? BotIdentity { get; set; }

    /// <summary>模型人设档案（可选，请求时注入系统上下文）。</summary>
    public string? BotPersona { get; set; }

    /// <summary>AI 对话欲望（0-100）：越高越倾向主动参与群聊发言。</summary>
    public int AiDesire { get; set; } = 50;

    /// <summary>发言适合度阈值（0-100）：模型评出低于此值则不发言。**代码侧强制执行**。</summary>
    public int SuitabilityThreshold { get; set; } = 10;

    /// <summary>给模型的最大上下文消息条数（与 BotAgent 侧共用同一值，避免两头不一样）。</summary>
    public int MaxContextMessages { get; set; } = 200;


    public void UpdateSettings(AppSettings settings) => _settings = settings;

    /// <summary>
    /// 生成回复。context 为按时间正序的最近消息（角色已映射为 system/user/assistant）。
    /// 返回结构化结果：调用方据此判断“沉默”还是“发言”（含模型自评的适合度）。
    /// 网络/接口异常向上抛，由调用方记日志。
    /// </summary>
    public async Task<CompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> context, string? profilesText = null, CancellationToken ct = default,
        IReadOnlyList<StickerChoice>? stickers = null, bool pokeContext = false, string? moodText = null, string? musicText = null, string? linkText = null, bool enableListen = false, bool enableVoice = false, string? recallText = null, bool enableWebSearch = false, string? searchText = null, string? groupRolesText = null, string? vibeHint = null, bool proactive = false)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException("未配置 API Key");
        }

        if (_settings.ApiKey.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            _settings.ApiKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("API Key 填成了 URL，请填入密钥 token（设置页「Agent 大脑」）");
        }

        var messages = new JsonArray();

        // 统一在此处截断一次（以前 BotAgent 和本方法各截一次，重复且容易不一致）
        var window = context.Count > MaxContextMessages
            ? context.Skip(context.Count - MaxContextMessages).ToArray()
            : context;

        // 可供模型指认的“最近几条别人的消息”：给它们附上 (#QQ消息id)，模型在 JSON 里用 replyTo 指明它在回哪一条。
        // 为什么要这么做：光靠启发式（“最新那条”或“排队时的触发”）猜不准 ——
        // 线上就出现过“正文在接一个哏，引用却挂在另一个人那句上”。模型自己知道回哪句，让它说出来。只标最近 16 条。
        // 撤回的消息不在此列：引用一条群里已经看不到的消息，群友看到的就是莫名其妙。
        var quotableIds = new HashSet<long>(
            window.Where(m => m.Role == MessageRole.Peer && !m.Recalled && m.QqMessageId is > 0 &&
                              !MessageMarkers.IsAside(m.Text))   // 整条旁白（“（笑）”）不是一句话，不给编号
                  .TakeLast(16)
                  .Select(m => m.QqMessageId!.Value));

        var systemContent = SystemPrompt;
        if (!string.IsNullOrWhiteSpace(BotIdentity))
        {
            systemContent += $"\n你是登录账号 QQ：{BotIdentity} 的机器人（群聊中别人 @QQ{BotIdentity} 或喊你昵称就是在叫你）。";
        }

        // 当前时间：模型没有时钟 —— 不告诉它，被问“现在几点”就只能靠训练语料猜（线上实测：经常答错）。
        // 顺便把“时效性信息必须搜”与它写在一起：搜索的“自主判断”需要一个明确依据，
        // 而“哪些属于今年/现在”全靠这个基准时间才能分清。
        var now = DateTimeOffset.Now;
        systemContent +=
            "\n\n[现在的时间]\n现在是 " + now.ToString("yyyy-MM-dd HH:mm") +
            "（星期" + WeekdayCn(now.DayOfWeek) + "，UTC" + now.ToString("zzz") + "）。\n" +
            "• 有人问“现在几点 / 今天几号 / 今天周几 / 还有几天” → **直接按它答**，不要靠自己印象猜（你并没有钟）；\n" +
            "• “今天 / 昨天 / 明天 / 这周 / 刚刚 / 上次”这类相对时间，全以它为基准算；\n" +
            "• 需要具体日期但拿不准时，宁可说“我记得是 X 号”这种带保留的话，也不要编一个硬结论。";

        if (!string.IsNullOrWhiteSpace(BotPersona))
        {
            systemContent += "\n\n[机器人人设档案]\n" + BotPersona.Trim();
        }

        systemContent += BuildSuitabilityInstruction();

        // 引用谁：最近几条别人的消息都带了 (#id)，让模型自己指认。
        // replyTo 的语义必须写死 —— 模型很容易把“让我不爽的那条（素材）”当成“我在回哪条（对象）”：
        // 线上实测（handoff-4 §23）群友 c 复读了机器人那句话，机器人说的是“别学我说话！”，
        // 引用却挂在上一条别人的消息上 —— 群里看到的就是“回复错人”，§22 的启发式修不了这一类。
        if (quotableIds.Count > 0)
        {
            systemContent +=
                "\n\n[回复谁]\n上下文里形如 `{某某}{内容--时间} (#123456)` 的是最近几条**别人发的**消息，# 后面是消息编号。\n" +
                "replyTo 只有一个含义：**你这句话是在跟谁说话 / 你在回应、反驳、回答哪一条**（QQ 会把它显示成“回复 某某”），" +
                "填那个人那条的编号：{\"suitability\": 80, \"reply\": \"…\", \"replyTo\": 123456}。\n" +
                "它不是“你提到的素材 / 你吐槽的内容 / 这件事的出处”。最容易填错的就是这两种：\n" +
                "  • 有人**复读、模仿**别人的话（包括学你说话）：你要跟的那个是**复读的人** → 填**他发的那条**，别填被复读的原文；\n" +
                "  • 某条消息让你不爽、但你想说的是另一个人 → 填**你要说给的那个人**那条，别填让你不爽的那条。\n" +
                "一条消息只对一个人说话：想同时回应两个人时，先只说最主要的那个；别把对两个人的话塞进一句里，那样引用必然对不上。\n" +
                "**点名优先**：如果最近几条里有人**@你、或者引用了你发的那句话**（“在跟你说话”），这一轮就说给他的那句，" +
                "replyTo 填**他发的那条**；哪怕你更想吐槽别人那条，也先接完跟你说话的人（别人那条下一轮再说，" +
                "别让正跟你说话的人看着你去回另一个人）。\n" +
                "如果这句话是对全群说的、就是接最新那一句、或者你拿不准对象是谁 → **不要填 replyTo" +
                "（没有引用只是少一层上下文，挂错人却是当众回错人），也不要自己编编号。" +
                "**";
        }

        // 消息里的标记：表情包和戳一戳在上下文里都是带方括号/全角括号的“事件写法”，
        // 不解释的话模型会把“（戳一戳）某人 戳了你一下”当成别人真说过这句话。
        systemContent +=
            "\n\n[上下文里的标记]\n" +
            "`[表情:微笑]`/`[动画表情:…]` 是对方发的 QQ 原生小表情（名字即它的含义），`[图片]`/`[语音]` 同理，" +
            "`[已撤回] xxx` 表示这句 xxx 发出后**被撤回了**：内容你看得到（你当时在场），但群里其他人已经看不到它了，" +
            "`（戳一戳）某某 戳了你一下` 表示某某在 QQ 里戳了你——那是动作不是文字。" +
            "`〔旁白：…〕` 是群友的**括号旁白**（动作 / 表情说明，例：`〔旁白：把猫抱过来〕`）：" +
            "你能看到它、也能拿它理解现场（“端到桌上”“抱着猫”这类动作本来就是语境），" +
            "但它**不是他说的话** —— 别当成一句话去接、别在回复里把括号里的字念出来。" +
            "`[回复 某某「…」]` 表示这句话是**引用回复**（「…」是被引用那条的原话；引你时写 `[回复 你「…」]`）：" +
            "按它理解“他在接谁的话”，别把引文当成新说的话、也别原样复述一遍。";

        // 当前心情：给模型一个“我现在什么状态”的锚，让它的话风/要不要理人有个连贯的落点
        if (!string.IsNullOrWhiteSpace(moodText))
        {
            systemContent +=
                "\n\n[你此刻的心情]\n" + moodText.Trim() +
                "\n（心情只影响你说话的语气与热络程度：烦的时候就短、敷衽、甚至懒得理；心情好可以开玩笑。别把它当成要宣告的信息。）";
        }

        // 群里刚分享的音乐：把“实测到的事实 + 歌词”交给模型，让它聊得像真听过（而不是望着歌名编）
        if (!string.IsNullOrWhiteSpace(musicText))
        {
            systemContent +=
                "\n\n[群里刚分享的音乐]\n" + musicText.Trim() +
                "\n（这是你刚“听”过的一首歌：可以就节奏/旋律/歌词/年代感聊两句，或顺着群友的话接。" +
                "歌名、歌手、时长、BPM、响度、段落这些事实一律以上面的实测数据为准，不要另编；" +
                "歌词里没有的内容不要虚构，也别把整段歌词抰出来 —— 引用一两句点到即止，像真的听过那样随口提。）";
        }

        // 群里消息里的链接：机器人已经打开看过（标题/摘要），别让它对着一串 URL 猜
        if (!string.IsNullOrWhiteSpace(linkText))
        {
            systemContent +=
                "\n\n[群里刚发的链接]\n" + linkText.Trim() +
                "\n（链接内容已取回，按上面的标题/摘要聊就行；别编造页面里没有的细节，也别把整段摘要复述一遍。）";
        }

        // 联网搜索：模型可以要求“去查一下”（search 字段）或“读一下这个页面”（read 字段）。
        // 两轮动作：机器人先把结果拿回来，下一轮它再拿着事实说话（和听音乐同一套思路）。
        if (enableWebSearch)
        {
            systemContent +=
                "\n\n[联网搜索]\n" +
                "当你要用的信息**在你自己脑子里不可靠**、而且这件事一查就能确认时（新闻、某游戏/番剧的最新情报与攻略、" +
                "价格、开服/发售时间、某人是声优/作者/成员这类事实），在 JSON 里加 search 字段写上要查什么：" +
                "{\"suitability\": 85, \"reply\": \"我去查一下\", \"search\": \"碧蓝档案 砂狼白子 声优\"}。\n" +
                "机器人会真的去搜，把结果（带来源）交给你，下一轮你就能拿着事实回答 —— 而不是靠印象编。\n" +
                "也可以让它读某个具体网页：在 JSON 里加 read 字段填 URL（图符群友发过的链接）。\n" +
                "**什么时候必须搜**（你的训练知识有截止时间，下面这些“现在还在变”的事一律不能凭记忆答）：\n" +
                "  新闻时事、天气、价格/汇率/股票、赛程与比分、活动/开服/发售/上线时间、软件与游戏版本更新、" +
                "新番与作品情报、某个人物的最新近况 —— 以及任何带“现在/最新/最近/今天/今年”的问题。\n" +
                "**什么时候不用搜**：数学、成语、语法、历史与地理常识、代码写法、能自己算出来或能从上下文看出来的东西；\n" +
                "“现在几点 / 今天几号 / 今天周几”也不用搜 —— [现在的时间] 里已经告诉你了。\n" +
                "规矩：① 检索词写具体，**带上时间信息**（例：“碧蓝档案 2026年9月 活动”比“碧蓝档案 活动”准得多）；" +
                "② 同一件事不要连着搜两次；③ 不要每句话都搜（搜一次要花几秒）；" +
                "④ 搜不到/读不到就如实说“没查到”，**绝对不要**用记忆里的旧信息假装是刚查到的；" +
                "⑤ search/read 是后台动作：填了它你这一轮该接的话照接（reply 正常写）。";
        }

        // 刚有人撤回了消息：告诉模型“谁撤的 + 上下文里那条已标成 [已撤回]”
        if (!string.IsNullOrWhiteSpace(recallText))
        {
            systemContent +=
                "\n\n[有人撤回了消息]\n" + recallText.Trim() +
                "\n（规矩：① 你可以记得内容，但**不要复述、不要引用、更不要说“我都看见了/撤什么”这类话**，" +
                "也不要暗示自己看到了 —— 撤回就是不想让它留在群里；" +
                "② 如果对方撤回后马上又发了更正/补充，就当没这回事，顺着新内容接；" +
                "③ 只有当大家都在好奇、气氛适合时，才可以轻描淡写问一句，别审问。）";
        }

        // 群成员身份（群主 / 管理员 / 群头衔）：让模型知道“谁说了算、这人什么来头”
        if (!string.IsNullOrWhiteSpace(groupRolesText))
        {
            systemContent +=
                "\n\n[本群身份]\n" + groupRolesText.Trim() +
                "\n（如上：群主与管理员能踢人、能撤回消息，头衔多是本人自己写的梗。" +
                "这些只是让你心里有数：该配合配合（人家真是管事的），该吐槽吐槽（头衔本身就是个乐子），" +
                "但不要拿身份拍马屁、也不要拿它压人。）";
        }

        // 主动开口：这次不是别人问它，是它自己想说话 —— 不说明的话，模型会以为有人在跟它说话
        if (proactive)
        {
            systemContent +=
                "\n\n[这次是你自己想说话]\n" +
                "没有人 @ 你、也没人在问你 —— 是刚刚沉默了一会儿，你自己想说一句。规矩：\n" +
                "• 先看看上下文里大家最后在聊什么/什么气氛：接着那个气氛说，别突然换个话题；\n" +
                "• 不要用“在吗”“有人在吗”“大家好啊”这类找存在感的废话开头；\n" +
                "• 不要连环发问、不要查户口（“你们多大”“在干什么”之类）；\n" +
                "• 可以接住某个人的情绪（安慰/捧场）、可以补一句你自己的想法、也可以把一个话题往前推一句；\n" +
                "• 如果实在没什么可说的，就沉默（reply 空）—— 为了刷存在感而说话比不说话更烦人。";
        }

        // 上一轮自己读到的气氛：让语气接得上（像个人一样，记得刚才大家什么心情）
        if (!string.IsNullOrWhiteSpace(vibeHint))
        {
            systemContent +=
                "\n\n[你上一条消息时的感觉]\n" + vibeHint.Trim() +
                "\n（只是你自己的记忆，别把它读出来；如果现在气氛已经变了，以现在为准。）";
        }

        // 刚搜到的结果（或读到的网页正文）：交给模型，用完就清
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            systemContent +=
                "\n\n[刚查到的资料]（你上一轮说要去查，这就是查回来的）\n" + searchText.Trim() +
                "\n（怎么用：① 用你自己的语气说出来 —— 就像你本来就知道这件事一样，别用“根据资料/搜索结果显示”这种播报腔；" +
                "② 接着刚才的话头说，别像换了个人：你的人物设定、口头禅、说话节奏照旧；" +
                "③ 只讲跟那个问题有关的部分，不要把整段资料念一遍；" +
                "④ 上面没写的细节别编，拿不准就说拿不准；" +
                "⑤ 来源链接不用贴（除非有人问“哪来的”）。）";
        }

        // 想听一首歌：模型可以主动要求“让我听听这首歌”（群里让它听歌就是走这条路）
        if (enableListen)
        {
            systemContent +=
                "\n\n[想听一首歌]\n" +
                "群里让你听/放某首歌，或者你想就某首歌接话但没把握时，在 JSON 里加 listen 字段写上歌名（带歌手更好）：" +
                "{\"suitability\": 85, \"reply\": \"我去听听\", \"listen\": \"洛天依 if love == true\"}。\n" +
                "机器人会去网易云搜这首歌、下一份低码率音频做波形分析，然后把歌词和实测数据给你 —— 下一轮你就能真的聊这首歌了。\n" +
                "listen 只在确实需要“听过”时用（同一首歌不要反复请求），也别拿它当通用搜索框。\n" +
                "另外，语境合适时可以**主动分享**一首歌给群里（有人要推荐、聊到某首歌、气氛适合来一首），" +
                "在 JSON 里加 shareSong 字段写歌名（带歌手更好），机器人会搜到后发一张网易云音乐卡片：" +
                "{\"suitability\": 85, \"reply\": \"来一首这个\", \"shareSong\": \"起风了 买辣椒也用券\"}。" +
                "分享要克制：别反复推同一首，也别每轮都发卡片（卡片比文字“重”得多）。";
        }

        // 用语音说话：模型可以要求“这句用语音说”（speak 字段）。语音比文字“重”得多
        // （合成要几秒、占流量、群里显眼），所以提示词里反复强调克制；代码侧还有一道最小间隔门。
        if (enableVoice)
        {
            var maxChars = Math.Clamp(_settings.VoiceMaxChars, 10, 300);
            systemContent +=
                "\n\n[用语音说话]\n" +
                "你**偶尔**可以用语音说一句。想这么做时，在 JSON 里加 speak 字段写上要说出口的话（≤ " + maxChars + " 字）：" +
                "{\"suitability\": 85, \"reply\": \"…\", \"speak\": \"这句话我想用声音说\"}。机器人会把它合成语音发出去。\n" +
                "什么时候值得用：情绪比文字重的时候（道谢、撒娇、学人说话、唱歌、委屈/开心），" +
                "或者群友明确让你“说句话/唱一个/用语音”。**绝大多数时候还是打字**，别每句都发语音，" +
                "也别连续两条都是语音 —— 群里语音是“稀罕事”，滥了就烦人。\n" +
                "speak 里写的就是要说出口的那句话：口语化、短、别放链接/代码/括号里的舞台说明（如“(笑)”）；" +
                "填了 speak 就不要再在 reply 里重复同一句话（语音已经说过了）。" +
                "说不好或超过字数上限时，这次就按普通文字回，不要在上下文里提到“语音发不出去”。";
        }

        // 被戳过才给的指令：戳回去是**可选**动作，看当下心情 —— 不必每次被戳都戳一次
        if (pokeContext)
        {
            systemContent +=
                "\n\n[戳一戳怎么回]\n" +
                "刚有人戳了你，按人设、上下文和心情回一句（或者只发个表情包）。" +
                "回戳是可选动作，**不要每次被戳都回戳**：心情好/想玩可以偶尔戳回去，" +
                "刚被同一个人或几个人连着戳过、心里烦的时候就不要回戳（回句话甚至不理都行）。" +
                "决定回戳时，在 JSON 里加 poke 字段填对方的 QQ 号，例如：{\"suitability\": 85, \"reply\": \"…\", \"poke\": 123456}；" +
                "只能戳上下文里出现过的人（戳你的那个人用他的 QQ 号），不要编造号码。" +
                "另外可以用 mood 字段顺手写一句你现在的心情（≤ 12 字，如“被戳烦了”“心情不错”），会记到下一轮。";
        }

        // 表情包：只把“按语境挑出来的几张”给模型，而不是整库（库可能上千张）。
        if (stickers is { Count: > 0 })
        {
            systemContent += BuildStickerInstruction(stickers);
        }

        // 角色卡片：优先使用外部传入的人物档案（按 QQ 号建表积累），否则由最近消息聚合
        var participants = profilesText is not null
            ? new[] { profilesText }
            : BuildParticipantProfiles(window);
        if (participants.Length > 0)
        {
            systemContent += "\n\n[会话参与者档案（由他们的历史发言总结而来，回复时参考每个人是谁、说过什么）]\n" +
                             string.Join("\n\n", participants);
        }

        messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemContent });

        // 最近上下文里出现图片时，在系统提示中提示模型可以看图
        if (window.Any(m => m.ImageUrls is { Count: > 0 }))
        {
            systemContent += "\n(消息中包含图片，已一并提供，请先看图再结合上下文回复。)";
            messages[^1] = new JsonObject { ["role"] = "system", ["content"] = systemContent };
        }

        // 这一轮真送出去的图片张数：上游吞回复时把它写进日志（带图那一轮的风控嫌疑最大）
        var attachedImages = 0;
        // 送去过的图片所属消息 id（两个用途：诊断日志里说清是哪条；疑似触发过滤时拉黑不再送）
        var attachedImageIds = new List<long>();

        foreach (var msg in window) // 上下文窗口
        {
            if (msg.Role == MessageRole.System)
            {
                continue; // 本地系统提示（如"已连接"）不发给模型，避免噪音
            }

            // 对方消息按 {发送者}{内容--时间} 组织，帮助模型分辨谁在何时说了什么；
            // 最近几条再附上 (#id)，供 replyTo 引用。
            // 已撤回的：正文前面加 [已撤回] 标记（**内容保留** —— 它确实看过，只是要让模型
            // 知道“这条已经收回去了”，引用/复述时得自己拿掉分寸）。
            var shownText = msg.Recalled ? RecallMark + msg.Text : msg.Text;
            var content = msg.Role == MessageRole.Peer && !string.IsNullOrWhiteSpace(msg.SenderName)
                ? $"{{{msg.SenderName}}}{{{shownText}--{FormatTime(msg.Timestamp)}}}" +
                  (msg.QqMessageId is long qid && quotableIds.Contains(qid) ? $" (#{qid})" : string.Empty)
                : shownText;
            var role = msg.Role == MessageRole.Self ? "assistant" : "user";

            // 多模态：消息带图片时，把图片（下载转 base64）一并发给模型识图
            if (msg.ImageUrls is { Count: > 0 } && msg.Role == MessageRole.Peer &&
                !(msg.QqMessageId is long suspectId && IsSuspectImage(suspectId)))
            {
                var contentParts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = content } };
                foreach (var url in msg.ImageUrls.Take(3)) // 每条消息最多带 3 张图
                {
                    // 异步下载（绝不同步阻塞 UI 线程），失败跳过该图；
                    // 带上消息 id：地址过期（rkey 时效）时能找协议端重新签发。
                    var dataUrl = await _imageDownloader.DownloadAsDataUrl(url, ct, msg.QqMessageId);
                    if (dataUrl is not null)
                    {
                        attachedImages++;
                        if (msg.QqMessageId is long iid && !attachedImageIds.Contains(iid))
                        {
                            attachedImageIds.Add(iid);
                        }

                        contentParts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = dataUrl }
                        });
                    }
                }

                messages.Add(new JsonObject { ["role"] = role, ["content"] = contentParts });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
        }

        // 上游（Gemini 等）不接受“最后一条是模型自己说的话”的请求：
        //   Requests ending with a model turn are not supported.
        // 而这恰好是机器人的一种正常情形 —— **自己触发的后续发言**：
        //   • 听完歌回来接着聊（模型填了 listen，分析完再请求一次）
        //   • 被戳之后想回一句（戳一戳不是消息，不往上下文里写）
        //   • 静默兜底补的那次请求
        // 这些时候上下文最后一条就是它自己刚说的话，一问就是 400。
        // 修法：补一条系统口吻的 user 轮把它顶成 user —— 顺便告诉模型“这是你自己的后续动作，
        // 不是又有人说话了”，免得它以为群里刚来了新消息、对着自己的话自问自答。
        var lastRole = messages.Count > 0 ? messages[^1]?["role"]?.GetValue<string>() : null;
        if (lastRole != "user")
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "（系统提示：上面最后一条是你自己刚说的话，之后没有新的群消息 —— " +
                              "本轮是你自己的后续动作触发的。想说就接着说，不想说就把 reply 留空。）"
            });
            FileLog.Write("Agent", "上下文以自己发言结尾 → 补一条系统口吻的 user 轮（上游不接受 model-turn 结尾）");
        }

        var payload = new JsonObject
        {
            ["model"] = _settings.Model,
            ["messages"] = messages,
            ["max_tokens"] = _settings.MaxTokens > 0 ? _settings.MaxTokens : 2048, // 仅防御非法值（旧数据可能为负数），不设上限
            ["temperature"] = 0.7
        };

        var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        // 一次 5xx/429 重试。
        // 为什么要：上游网关（多账号池网关之类）经常回 503 auth_unavailable / No capacity ——
        // 实测 17:28 连挨三次，每条都直接“模型请求失败”丢掉一次回复；晚 2 秒再问一次往往就能拿到。
        // 只重试一次、且只对 5xx/429：不把已经慢的链路拖成双倍慢（4xx 是请求本身的问题，重试没意义）。
        HttpResponseMessage? response = null;
        string? failureDetail = null;
        for (var attempt = 0; ; attempt++)
        {
            response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                break;
            }

            var detail = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            failureDetail = $"Chat Completions 返回 {status}：{Truncate(detail, 200)}";
            if (attempt >= 1 || (status < 500 && status != 429))
            {
                response.Dispose();
                response = null;
                break;
            }

            Services.FileLog.Write("Agent", $"模型返回 {status}（{Truncate(detail, 80)}）→ 2 秒后重试一次");
            response.Dispose();
            response = null;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            // 请求体是一次性的，重试要重建一份
            request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
        }

        if (response is null)
        {
            throw new HttpRequestException(failureDetail ?? "Chat Completions 请求失败");
        }

        using var _ = response;

        var json = await response.Content.ReadAsStringAsync(ct);

        // 上游偶尔会回 200 但**没有 choices**。实测三种情形：
        //   a) 慢的 `-high` 模型“思考”把预算吃光（17:55-18:01 连报 21 次）
        //   b) 网关抖动（回 200 但内容空）
        //   c) 带图那一轮被上游风控吞掉（22:09 这条：usage 里 prompt 12744 / completion 0，前面刚取回 4 张图）
        // 以前直接 `choices[0]` → IndexOutOfRangeException，被记成“模型请求失败”：一条消息就这么没了。
        // 之后改成“按沉默处理”，但**一条都不重试**：明明多半是上游吞了，却白丢一轮回复。
        // 现在分两步：① 先重试一次（这类吞回复多半是间歇性的，第二次常常正常）；
        //              ② 两次都空才算不说话 —— 且上层日志会写成“上游空响应”，
        //                 与“模型自己决定不说话”分得清。
        for (var emptyTry = 0; emptyTry < 1 && !HasChoices(json); emptyTry++)
        {
            Services.FileLog.Write("Agent", $"上游返回空 choices（{Truncate(json, 140)}）→ 2 秒后重试一次");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            // 请求体是一次性的，重试要重建一份（与上面 5xx 重试同理）
            using var retryRequest = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
            using var retryResponse = await Http.SendAsync(retryRequest, ct);
            if (!retryResponse.IsSuccessStatusCode)
            {
                // 重试本身也塌了：保留上一次的响应文本（下面按空响应处理），别再把它覆盖成错误页
                Services.FileLog.Write("Agent", $"空 choices 后的重试返回 {(int)retryResponse.StatusCode} → 这轮按空响应处理");
                break;
            }

            json = await retryResponse.Content.ReadAsStringAsync(ct);
        }

        // ③ 带图的两轮都空 → 去掉图片再试一次。
        //    为什么值得试：上游（上游网关 后端）对某些内容会直接回**零候选**（HTTP 200、
        //    用量里只有 prompt tokens），表现就是“这个群这几轮怎么问都是空的”。
        //    实测那种情况下同一个 payload 重发多少次都空（22:42-22:46 同一尺寸两发两空），
        //    而同时段别的会话正常 —— 是内容触发，不是网络抖动。
        //    去掉图往往就能说话：这一轮先据文字回（总比一句话不说强），
        //    同时把嫌疑图片的消息 id 记下来 —— 后续上下文不再重复送它们（自愈）。
        var textOnlyRetry = false;
        if (!HasChoices(json) && attachedImages > 0)
        {
            var stripped = StripImages(payload);
            using var plainRequest = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(stripped.ToJsonString(), Encoding.UTF8, "application/json")
            };
            plainRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
            using var plainResponse = await Http.SendAsync(plainRequest, ct);
            if (plainResponse.IsSuccessStatusCode)
            {
                var plainJson = await plainResponse.Content.ReadAsStringAsync(ct);
                if (HasChoices(plainJson))
                {
                    json = plainJson;
                    textOnlyRetry = true;
                    RememberSuspectImages(attachedImageIds);
                    Services.FileLog.Write("Agent",
                        $"两轮空后去掉图片再试 → 拿到了回复：疑似图片触发上游过滤（消息 {string.Join(",", attachedImageIds)}），本轮只据文字回；" +
                        $"这些图后续会从上下文里跳过");
                }
            }
        }

        using var doc = JsonDocument.Parse(json);

        if (!HasChoices(json))
        {
            Services.FileLog.Write("Agent",
                $"上游连续两次都没给 choices（带图 {attachedImages} 张）→ 本轮按沉默处理：{Truncate(json, 200)}");
            return new CompletionResult(null, null, null, UpstreamEmpty: true);
        }

        if (textOnlyRetry)
        {
            Services.FileLog.Write("Agent", "本轮回复是去掉图片后拿到的（那张图已被拉黑）");
        }

        var choices = doc.RootElement.GetProperty("choices");

        if (choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentNode))
        {
            var rawReply = contentNode.ValueKind switch
            {
                JsonValueKind.String => contentNode.GetString(),
                JsonValueKind.Array => string.Concat(contentNode.EnumerateArray()
                    .Select(s => s.TryGetProperty("text", out var t) ? t.GetString() : null)),
                _ => null
            };

            return ParseModelOutput(rawReply);
        }

        // 有 choices 但里面没有 message.content：同样按沉默处理，不招异常
        Services.FileLog.Write("Agent", $"模型返回里没有 message.content → 本轮按沉默处理：{Truncate(json, 200)}");
        return new CompletionResult(null, null, null, UpstreamEmpty: true);
    }

    /// <summary>
    /// 响应里到底有没有 choices。空数组 / 根本没这个字段 = 上游把回复吞了，
    /// **不是**“模型自己决定不说话”—— 两件事在日志里必须分得清，否则主人看不出是网关出事了。
    /// </summary>
    private static bool HasChoices(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("choices", out var choices) &&
                   choices.ValueKind == JsonValueKind.Array &&
                   choices.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把请求体里所有 image_url 部分抽掉（其他一字不改）—— 上游因图片回零候选时的兜底重试。
    /// 用 JSON 层复制，不重下图片（图已经在内存里，只是不再送给模型）。
    /// </summary>
    private static JsonObject StripImages(JsonObject payload)
    {
        var clone = (JsonObject)JsonNode.Parse(payload.ToJsonString())!;
        if (clone["messages"] is not JsonArray messages)
        {
            return clone;
        }

        foreach (var message in messages)
        {
            if (message?["content"] is not JsonArray parts)
            {
                continue;
            }

            for (var i = parts.Count - 1; i >= 0; i--)
            {
                if (parts[i] is JsonObject part && part["type"]?.GetValue<string>() == "image_url")
                {
                    parts.RemoveAt(i);
                }
            }
        }

        return clone;
    }

    /// <summary>
    /// 记下“疑似害得上游吞回复”的图片消息：后续上下文里不再重复送它们的图。
    /// 有上限（200 条，超了掐最早的），只是个避雷名单，不做持久化 —— 重启就忘了，
    /// 免得一次误判永久屏蔽某张图。
    /// </summary>
    private void RememberSuspectImages(List<long> messageIds)
    {
        lock (_imageFilterSuspects)
        {
            foreach (var id in messageIds)
            {
                if (_imageFilterSuspects.Add(id))
                {
                    _imageFilterSuspectOrder.Add(id);
                }
            }

            while (_imageFilterSuspectOrder.Count > 200)
            {
                _imageFilterSuspects.Remove(_imageFilterSuspectOrder[0]);
                _imageFilterSuspectOrder.RemoveAt(0);
            }
        }
    }

    /// <summary>这张图是不是被拉黑了（拉黑了就不送，省得整个窗口又被上游掸掉）。</summary>
    private bool IsSuspectImage(long messageId)
    {
        lock (_imageFilterSuspects)
        {
            return _imageFilterSuspects.Contains(messageId);
        }
    }

    private readonly HashSet<long> _imageFilterSuspects = new();
    private readonly List<long> _imageFilterSuspectOrder = new();

    /// <summary>
    /// 解析模型输出。约定：JSON {"suitability":0-100,"reply":"..."}。
    ///   • reply 为空 → 沉默
    ///   • 输出“看起来是 JSON”但解析不了 → 判为格式错误，**沉默**（绝不把 JSON 原文当成回复发出去）
    ///   • 完全不像 JSON（纯文本）→ 当普通回复，适合度未知
    /// </summary>
    /// <summary>纯文本回复的最短长度：1~2 个字的“回复”几乎都是上游被截断的碎片，不是真的想说话。</summary>
    private const int MinPlainTextReplyLength = 3;

    /// <summary>
    /// 可以单独成句的单字应答（中文口语里确实会这么用）。
    /// 白名单之外的单字一律按上游噪声处理 —— 群里的“彫”就是这么刷起来的。
    /// </summary>
    private const string SingleCharReplyWhitelist = "嗯哦啊哈呃哎咦喂喔唔草6?？!！~～";

    private static CompletionResult ParseModelOutput(string? rawReply)
    {
        if (string.IsNullOrWhiteSpace(rawReply))
        {
            return new CompletionResult(null, null, rawReply);
        }

        var text = StripCodeFence(rawReply.Trim());

        // 模型很爱在 JSON 前面写一句解释（“好的，我来回：”）或者把 JSON 裹在围栏里再另起一段 ——
        // 以前这种“不以 { 开头”的输出会直接走纯文本分支，把整段 JSON 发进群里。
        // 这里先在全文里找“长得像我们约定的那个 JSON”的片段。
        if (!text.StartsWith('{') && TryExtractJsonBlock(text, out var extracted))
        {
            text = extracted;
        }

        // 再兜一层：纯文本里带着我们的字段名（suitability/reply/sticker…）说明它本来就是想输出 JSON，
        // 只是格式没弄对 —— 宁可沉默，也绝不把 JSON 代码吐进群里。
        if (!text.StartsWith('{') && LooksLikeSchemaJson(text))
        {
            Services.FileLog.Warn("Agent",
                $"模型输出看似 JSON 但格式不对，按沉默处理（避免把代码发进群）：{Truncate(rawReply, 120)}");
            return new CompletionResult(null, null, rawReply);
        }

        // 只有“以 { 开头”才当作 JSON 尝试。
        // 理由：真人语气里也会出现花括号（“这个 {a:1} 是啥”），那些必须当普通文本发出去。
        if (!text.StartsWith('{'))
        {
            // 纯文本兜底：有些模型就是不按 JSON 输出，这里必须放行。
            // 但**极短**的纯文本是个例外 —— 群里实测过单字“悼”刷屏：
            // 上游网关偶发只回一个字符，旧实现把它当正常回复发了出去，
            // 而模型又能从上下文里读到自己那条“悼”，于是开始自我复读。
            // 不到 3 个字、又不像 JSON，就当作“没有回复”，并把原文写进日志以便回溯上游异常。
            if (VisibleLength(text) < MinPlainTextReplyLength)
            {
                Services.FileLog.Warn("Agent",
                    $"模型输出疑似被截断（非 JSON，只有 {VisibleLength(text)} 个可见字符），按沉默处理：{Truncate(text, 60)}");
                return new CompletionResult(null, null, rawReply);
            }

            return new CompletionResult(null, text, rawReply);
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        try
        {
            using var doc = JsonDocument.Parse(end > start ? text[start..(end + 1)] : text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new CompletionResult(null, null, rawReply);
            }

            int? suitability = null;
            if (root.TryGetProperty("suitability", out var s))
            {
                suitability = s.ValueKind switch
                {
                    JsonValueKind.Number => ReadScore(s),
                    JsonValueKind.String => int.TryParse(s.GetString(), out var parsed) ? parsed : null,
                    _ => null
                };
            }

            // 只有字符串才算回复；reply 是对象/数组时不能 GetString（会抛）
            string? reply = null;
            if (root.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String)
            {
                reply = r.GetString()?.Trim();
            }

            // 群里的情绪氛围（模型自己的读法）：归一到一个固定集合，程序侧才能按它调发言策略
            string? vibe = null;
            if (root.TryGetProperty("vibe", out var vb) && vb.ValueKind == JsonValueKind.String)
            {
                vibe = NormalizeVibe(vb.GetString());
            }

            string? vibeNote = null;
            if (root.TryGetProperty("vibeNote", out var vn) && vn.ValueKind == JsonValueKind.String)
            {
                vibeNote = Truncate((vn.GetString() ?? string.Empty).Trim(), 40);
                if (vibeNote.Length == 0)
                {
                    vibeNote = null;
                }
            }

            // 单字回复：中文口语里“嗯/哦/哈”确实是正常应答，其余单字基本都是上游噪声——
            // 群里实测就是单个“彫”在刷屏（上游偶发只回一个字符）。
            if (reply is { Length: 1 } && !SingleCharReplyWhitelist.Contains(reply[0]))
            {
                Services.FileLog.Warn("Agent",
                    $"模型只回了 1 个字“{reply}”，不属于正常应答，按沉默处理。原文：{Truncate(rawReply, 80)}");
                return new CompletionResult(suitability, null, rawReply);
            }

            // 1 个字的正常应答：照发。
            if (reply is { Length: 1 })
            {
                Services.FileLog.Write("Agent", $"模型回复只有 1 个字（{reply}），原文：{Truncate(rawReply, 80)}");
            }

            // 表情包：模型可能只发图不说话，所以单独解析（id 去掉 # 前缀，拒绝奇怪的值）
            string? stickerId = null;
            if (root.TryGetProperty("sticker", out var st) || root.TryGetProperty("stickerId", out st))
            {
                var raw = st.ValueKind switch
                {
                    JsonValueKind.String => st.GetString(),
                    JsonValueKind.Number => st.ToString(),
                    _ => null
                };

                var cleaned = raw?.Trim().TrimStart('#').Trim();
                if (!string.IsNullOrWhiteSpace(cleaned) &&
                    cleaned.Length is >= 4 and <= 32 &&
                    cleaned.All(char.IsAsciiHexDigit))
                {
                    stickerId = cleaned.ToLowerInvariant();
                }
            }

            // 模型自己指认的“我在回哪条”（replyTo）。只取正整数：
            // 具体是否采信由上层校验（必须在本次上下文里），这里不做业务判断。
            long? replyToId = null;
            if (root.TryGetProperty("replyTo", out var rt) || root.TryGetProperty("reply_to", out rt))
            {
                replyToId = rt.ValueKind switch
                {
                    JsonValueKind.Number when rt.TryGetInt64(out var value) => value,
                    JsonValueKind.String when long.TryParse(rt.GetString(), out var parsed) => parsed,
                    _ => null
                };

                if (replyToId is <= 0)
                {
                    replyToId = null;
                }
            }

            // 模型想戳谁（poke，也可以是 pokeBack）。同样只取正整数，
            // “这个人到底存不存在”由上层用上下文校验（能防住模型编造号码）。
            long? pokeTargetId = null;            if (root.TryGetProperty("poke", out var pk) || root.TryGetProperty("pokeBack", out pk) || root.TryGetProperty("pokeTo", out pk))
            {
                pokeTargetId = pk.ValueKind switch
                {
                    JsonValueKind.Number when pk.TryGetInt64(out var value) => value,
                    JsonValueKind.String when pk.ValueKind == JsonValueKind.String && long.TryParse(pk.GetString(), out var parsed) => parsed,
                    JsonValueKind.True => 0, // "pokeBack": true = 戳回去，具体号码交给上层（别在这里猜）
                    _ => null
                };

                if (pokeTargetId is <= 0)
                {
                    pokeTargetId = null;
                }
            }

            // 模型想“听一听”某首歌（歌名/歌手）：群里让它听歌、或它自己想聊某首歌却没把握时用。
            // 机器人会拿这个名字去搜歌，搜到就下低码率音频分析波形，下一轮把歌词 + 实测给它。
            string? listen = null;
            if ((root.TryGetProperty("listen", out var ls) || root.TryGetProperty("听歌", out ls)) && ls.ValueKind == JsonValueKind.String)
            {
                var wanted = ls.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(wanted) && wanted.Length is >= 2 and <= 60)
                {
                    listen = wanted;
                }
            }

            // 模型想把某首歌分享给群里（发一张网易云卡片）：“推荐首歌/点歌/聊到某首歌”这类语境
            string? shareSong = null;
            if ((root.TryGetProperty("shareSong", out var ss) || root.TryGetProperty("share_song", out ss)) && ss.ValueKind == JsonValueKind.String)
            {
                var want = ss.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(want) && want.Length is >= 2 and <= 60)
                {
                    shareSong = want;
                }
            }

            // 模型想“用语音说这句”（speak）：值可以是字符串（要说的话），也可以是 true（= 用语音说 reply）。            // 真正能不能发由上层决定（开关/字数上限/频率门/服务可达），这里只负责取值与基本清洗。
            string? speak = null;
            if (root.TryGetProperty("speak", out var sp) || root.TryGetProperty("用语音说", out sp))
            {
                if (sp.ValueKind == JsonValueKind.String)
                {
                    var spoken = sp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(spoken))
                    {
                        speak = spoken;
                    }
                }
                else if (sp.ValueKind == JsonValueKind.True)
                {
                    // {"speak": true} = 把 reply 用语音说（模型偷懒时也能用）
                    speak = string.IsNullOrWhiteSpace(reply) ? null : reply.Trim();
                }
            }

            // 模型想“上网查一下”（search）/“读一下某个页面”（read）。
            // 真正去查是上层的事（要发 HTTP、有冷却），这里只取词：
            //   • search 太短（<2）/太长（>120）都不要 —— 太长基本是它在写句子，不是搜索词；
            //   • read 必须是 http(s) 地址（SSRF 闸门在上层）。
            string? search = null;
            if ((root.TryGetProperty("search", out var se) || root.TryGetProperty("查一下", out se)) && se.ValueKind == JsonValueKind.String)
            {
                var wanted = se.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(wanted) && wanted.Length is >= 2 and <= 120)
                {
                    search = wanted;
                }
            }

            string? read = null;
            if ((root.TryGetProperty("read", out var rd) || root.TryGetProperty("读一下", out rd)) && rd.ValueKind == JsonValueKind.String)
            {
                var url = rd.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(url) && url.Length <= 500 &&
                    (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    read = url;
                }
            }

            // 模型顺手写的心情（≤ 24 字）：存起来给下一轮用；太长/非字符串一律忽略
            string? mood = null;
            if (root.TryGetProperty("mood", out var md) && md.ValueKind == JsonValueKind.String)
            {
                mood = md.GetString()?.Trim();
            }

            return new CompletionResult(suitability, string.IsNullOrWhiteSpace(reply) ? null : reply, rawReply, stickerId, replyToId, pokeTargetId, mood, listen, shareSong, speak, search, read, vibe, vibeNote);
        }
        catch (JsonException)
        {
            // 长得像 JSON 却解析不了：判为格式错误 → 沉默。
            // （以前这里会落到“按普通文本处理”，把整段 JSON 发进群里。）
            return new CompletionResult(null, null, rawReply);
        }
    }

    /// <summary>
    /// 从一段文本里抠出第一个“像我们约定的” JSON 对象（带花括号配对、跳过字符串里的括号）。
    /// 判据是里面出现了我们的字段名 —— 免得把群友消息里引用的一小段 JSON 当成模型输出。
    /// </summary>
    private static bool TryExtractJsonBlock(string text, out string block)
    {
        block = string.Empty;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
            {
                continue;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var j = i; j < text.Length; j++)
            {
                var c = text[j];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                switch (c)
                {
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            var candidate = text[i..(j + 1)];
                            if (LooksLikeSchemaJson(candidate))
                            {
                                block = candidate;
                                return true;
                            }

                            j = text.Length; // 这个块不是，继续找下一个 {
                        }

                        break;
                }
            }
        }

        return false;
    }

    /// <summary>星期几（中文，给提示词用 —— 模型自己对“今天周几”只能猜）。</summary>
    private static string WeekdayCn(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };

    /// <summary>这段文本里有没有我们的约定字段（说明它想输出的是结构化回复）。</summary>
    private static bool LooksLikeSchemaJson(string text)
        => text.Contains("\"suitability\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"reply\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"sticker\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"replyTo\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"mood\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"speak\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"search\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"poke\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 可见字符数：忽略空白、零宽空格、BOM。用于判断输出是不是被上游截断的碎片。
    private static int VisibleLength(string text)
        => text.Count(c => !char.IsWhiteSpace(c) && c != '\u200b' && c != '\ufeff');

    /// 读发言适合度评分。
    /// 模型实际会输出 85 / 85.0 / 85.5 / "85" 各种形式；
    /// 旧实现用 GetInt32() 读，碰到 85.0 直接抛异常 → 整段 JSON 被当成回复发进群。
    /// </summary>
    private static int? ReadScore(JsonElement element)
    {
        if (element.TryGetInt32(out var integer))
        {
            return integer;
        }

        return element.TryGetDouble(out var value) && !double.IsNaN(value) && !double.IsInfinity(value)
            ? (int)Math.Round(value)
            : null;
    }

    /// <summary>去掉 ```json / ``` 围栏，返回内部正文。</summary>
    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return text;
        }

        var body = text[(firstLineEnd + 1)..];
        var fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fenceEnd >= 0 ? body[..fenceEnd] : body).Trim();
    }

    /// <summary>
    /// 表情包指令：把候选图连同它们的说明/关键词给模型，让它自己挑。
    /// 关键是“宁可不发”——表情包用错语境比不发更尴尬。
    /// </summary>
    private static string BuildStickerInstruction(IReadOnlyList<StickerChoice> stickers)
    {
        var sb = new StringBuilder();
        sb.Append("\n\n[可用表情包]");
        sb.Append("你可以用一张表情包来补充或代替文字（一轮最多一张）。按当下语境挑最贴切的那张；");
        sb.Append("没有合适的就正常发文字。表情包是调味品不是主食：一组对话里偶尔用一张就够，");
        sb.Append("不要每句都挂，也不要反复用同一张（除非它就是当下最好的回应）。\n");
        foreach (var sticker in stickers)
        {
            sb.Append("  #").Append(sticker.Id).Append(' ').Append(sticker.Description).Append('\n');
        }

        sb.Append("要发表情包时，在 JSON 里加上 sticker 字段，值为上面某个 # 后面的 id（不含 #），例如：");
        sb.Append("{\"suitability\": 85, \"reply\": \"\", \"sticker\": \"").Append(stickers[0].Id).Append("\"}。");
        sb.Append("只发表情包时 reply 留空；又想说话又发表情包就两个都填（先发文字再发图）。");
        return sb.ToString();
    }

    /// <summary>给表情包库用：下载图片原始字节（内部走同一套 SSRF 防护与大小限制）。</summary>
    public Task<(byte[] Data, string Mime, string Ext)?> DownloadImageAsync(string url, CancellationToken ct = default, long? messageId = null)
        => _imageDownloader.DownloadBytesAsync(url, ct, messageId);

    /// <summary>
    /// 表情包自巡检：让模型看一遍库（表格形式），自己决定删哪些。
    /// 返回要删的 id 与理由；解析不了就返回空（宁可什么都不删）。
    /// </summary>
    /// <summary>
    /// 通用一次性补全：调用方自己给 system + 多轮 messages，直接拿原始文本。
    /// 为什么不走 <see cref="CompleteAsync"/>：那套是“群里该怎么回”的人设 JSON 约定，
    /// 而 agent（服务器内置 / 工具循环）要的是自由格式 + 自己控制历史。
    /// 429/5xx 退让 2 秒重试一次（跟主流程同口径：上游“No capacity”是常态）。
    /// </summary>
    public async Task<string?> CompleteChatAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<(string Role, string Text)> messages,
        int maxTokens,
        double temperature,
        CancellationToken ct = default,
        string? baseUrlOverride = null,
        string? apiKeyOverride = null)
    {
        var apiKey = string.IsNullOrWhiteSpace(apiKeyOverride) ? _settings.ApiKey?.Trim() : apiKeyOverride!.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var url = string.IsNullOrWhiteSpace(baseUrlOverride) ? BuildUrl() : BuildUrl(baseUrlOverride!);
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? _settings.Model : model,
            ["max_tokens"] = Math.Clamp(maxTokens, 64, 32000),
            ["temperature"] = temperature
        };

        var array = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };
        foreach (var (role, text) in messages)
        {
            array.Add(new JsonObject { ["role"] = role, ["content"] = text });
        }

        payload["messages"] = array;

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FileLog.Warn("Agent", $"补全请求失败：{ex.Message}");
                return null;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    {
                        FileLog.Warn("Agent", $"补全返回空结果：{Truncate(body, 160)}");
                        return null;
                    }

                    var node = choices[0].TryGetProperty("message", out var msg) &&
                               msg.TryGetProperty("content", out var content)
                        ? content
                        : default;
                    return node.ValueKind switch
                    {
                        JsonValueKind.String => node.GetString(),
                        JsonValueKind.Array => string.Concat(node.EnumerateArray()
                            .Select(s => s.TryGetProperty("text", out var t) ? t.GetString() : null)),
                        _ => null
                    };
                }

                var status = (int)response.StatusCode;
                if (attempt >= 1 || (status < 500 && status != 429))
                {
                    FileLog.Warn("Agent", $"补全请求失败 {status}：{Truncate(body, 120)}");
                    return null;
                }

                FileLog.Warn("Agent", $"补全返回 {status} → 2 秒后重试一次");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    public async Task<(List<string> Delete, string? Reason)> CurateStickersAsync(string libraryTable, int maxDelete, CancellationToken ct = default)
    {
        var empty = (new List<string>(), (string?)null);
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || string.IsNullOrWhiteSpace(libraryTable) || maxDelete <= 0)
        {
            return empty;
        }

        await _stickerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var system =
                "你在帮一个 QQ 群聊机器人整理表情包库。下面是库里的图，每行：id | 说明（含关键词）| 用过几次 | 多久前加入。\n" +
                "挑出不值得留的：与群聊语境无关的（广告、聊天截图、二维码、纯文字通知、屏幕截图）、画质/内容不合适（低俗、恶心、涉政涉黄）、" +
                "说明模糊且从未用过的、和已有的重复表达。标了“24h 内用过”的不要动。\n" +
                $"最多删 {maxDelete} 张（也可以一张都不删）。只输出一行 JSON：" +
                "{\"delete\": [\"id1\", \"id2\"], \"reason\": \"一句话说清为什么删这些\"}。" +
                "没把握就少删：库里图少的时候宁可留着。";

            var payload = new JsonObject
            {
                ["model"] = _settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = libraryTable }
                },
                ["max_tokens"] = 300,
                ["temperature"] = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return empty;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var raw = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return ParseCuration(raw, maxDelete);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包巡检请求失败：{ex.Message}");
            return empty;
        }
        finally
        {
            _stickerGate.Release();
        }
    }

    /// <summary>解析巡检结果（只收合法 id，并强制不得超过上限）。</summary>
    internal static (List<string> Delete, string? Reason) ParseCuration(string? raw, int maxDelete)
    {
        var delete = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (delete, null);
        }

        var text = StripCodeFence(raw.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (delete, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()?.Trim()
                : null;

            foreach (var name in new[] { "delete", "deletes", "remove", "removes" })
            {
                if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in arr.EnumerateArray())
                {
                    var id = (item.ValueKind == JsonValueKind.String ? item.GetString() : null)?.Trim().TrimStart('#').Trim();
                    if (!string.IsNullOrWhiteSpace(id) && id.Length is >= 4 and <= 32 && id.All(char.IsAsciiHexDigit)
                        && !delete.Contains(id.ToLowerInvariant()))
                    {
                        delete.Add(id.ToLowerInvariant());
                    }
                }

                break;
            }

            if (delete.Count > maxDelete)
            {
                delete = delete.Take(maxDelete).ToList();
            }

            return (delete, reason);
        }
        catch (JsonException)
        {
            return (delete, null);
        }
    }

    private readonly SemaphoreSlim _stickerGate = new(1, 1);

    /// <summary>
    /// 让模型看一张图并给出“一句话说明 + 情绪/场景关键词 + 这是不是表情包”。
    /// 表情包能不能“按语境发”，完全取决于这一步：靠关键词才能检索；
    /// 而“是不是表情包”这一步是入库闸门 —— 群友发的聊天截图/广告不能被当成表情包收下来。
    /// 走独立闸门，不和聊天抢并发。
    /// </summary>
    public async Task<(string? Desc, List<string>? Tags, bool? IsSticker)> DescribeStickerAsync(byte[] image, string mime, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || image.Length == 0)
        {
            return (null, null, null);
        }

        await _stickerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(image)}";
            var payload = new JsonObject
            {
                ["model"] = _settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] =
                            "你在给一个 QQ 群聊机器人的表情包库做入库审核。看图片内容，只输出一行 JSON：" +
                            "{\"sticker\": true/false, \"desc\": \"一句话说明这张图的画面与用途，20 字以内\", \"tags\": [\"3-6 个情绪或使用场景关键词\"]}。\n" +
                            "sticker 只在它真是“可以用来说话的表情包/梗图”（人或角色+情绪、能当反应那张图用）时为 true；" +
                            "聊天截图、屏幕截图、纯文字图、广告、二维码、文档照片、随手拍的实物 —— 这些一律 false（会被丢掉）。\n" +
                            "关键词要能用在其他句子里检索到它（例如：大笑、无语、嘲讽、点赞、崩溃、狗头、摸鱼）。" +
                            "如果是纯文字图，把文字内容也写进 desc。不要输出 JSON 之外的内容。"
                    },
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = "这张图是什么？" },
                            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } }
                        }
                    }
                },
                ["max_tokens"] = 200,
                ["temperature"] = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null, null);
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var raw = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return ParseStickerDescription(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包识别失败：{ex.Message}");
            return (null, null, null);
        }
        finally
        {
            _stickerGate.Release();
        }
    }

    /// <summary>解析表情包编目结果（容忍代码块围栏与多余文字）。</summary>
    internal static (string? Desc, List<string>? Tags, bool? IsSticker) ParseStickerDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null, null);
        }

        var text = StripCodeFence(raw.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            var plain = text.Trim();
            return plain.Length is > 0 and <= 40 ? (plain, null, null) : (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            var desc = root.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()?.Trim() : null;
            List<string>? tags = null;
            if (root.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            {
                tags = t.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim())
                    .Where(x => x.Length > 0)
                    .Take(8)
                    .ToList();
            }

            // 缺字段时不默认 true：宁可多留一张待定，也不要让截图混进来
            bool? isSticker = null;
            if (root.TryGetProperty("sticker", out var s))
            {
                isSticker = s.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(s.GetString(), out var b) => b,
                    _ => null
                };
            }

            return (desc, tags, isSticker);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>按对话欲望生成“发言适合度”评分指令。</summary>
    private string BuildSuitabilityInstruction()
    {
        var desire = AiDesire switch
        {
            >= 80 => "你非常健谈主动：主动参与群聊话题、接话、活跃气氛，除非话题与你完全无关。",
            >= 50 => "你比较健谈：话题与你相关、被提到或你感兴趣时积极发言，不强行插话。",
            >= 20 => "你较为克制：主要在被呼叫或与你直接相关时发言，群友闲聊时保持沉默。",
            _ => "你极少主动发言：仅在被明确 @ 或对方直接对你说话时回复。"
        };

        // 这份提示词是“像个人”的核心：先说清怎么读情绪（读得准，话才接得住），
        // 再说清什么情况该闭嘴（陪伴的分寸感全在这里），最后才是 JSON 格式。
        return "\n\n[先读懂气氛再说话]\n" +
               "每轮先在心里回答两个问题，再决定说不说话：\n" +
               "① **群里现在是什么情绪？** 逐条看最近几条：开心/兴奋、吐槽/抱怨、低落/难过、求助/求助无回应、生气/拌嘴、吵架/对线、" +
               "普通闲聊、或是别人之间的私事。把它写在 vibe 里（一个词），vibeNote 里补一句人话（≤30 字，例：“在吐槽加班，情绪烦燥”）。\n" +
               "② **这时候我该不该开口？** 参考：\n" +
               "   • 有人**倾诉 / 失落 / 求安慰** → 先接住情绪（“咋了”“谁惹你了”），**别讲道理、别给方案、别开黄腔、别发表情包**；\n" +
               "   • 有人在**吐槽一件事** → 可以顺一句共情或一起吐，但别挑拨、别把是非扩大；\n" +
               "   • 有人**吵架 / 对线** → 不站队、不评理、不接话（除非被点名要你说话）；\n" +
               "   • 大家在**开玩笑 / 起哄** → 可以接梗、可以带表情包，但别把玩笑开在别人痛处上；\n" +
               "   • 只是**别人之间的闲聊**、与你无关 → 沉默（不出声也是一种陪伴）；\n" +
               "   • 直接 @ 你、问你事 → 必答，而且先回答、别绕；\n" +
               "   • 消息里带 `〔旁白：…〕` 的：那是动作/表情说明、不是他说的话 —— 可以当现场信息，别当一句话去接；\n" +
               "   • 你刚说完、没新人接话 → 别再自说自话。\n" +
               "   • **底线（任何气氛下都不许越过）**：不骂人、不人身攻击、不替别人赶人走" +
               "（“消停点”“别祸害大家”“滚”这种话一句都不说）、不因为一条内容就否定整个人、更不连坐整个群；" +
               "觉得内容糟就说内容（“这都什么啊”），不要冲着人说。\n" +
               "suitability 就是这个“该不该开口”的分数（0-100：0-10 完全不该插嘴；10-40 可以但不必要；40-70 自然接话；70+ 就是非说不可）。\n" +
               "宁愿少说、说准，也别为了存在感硬接一句废话。\n" +
               desire + "\n" +
               "请严格只输出一行 JSON，形如：{\"suitability\": 80, \"vibe\": \"吐槽\", \"vibeNote\": \"在吐槽加班\", \"reply\": \"你的回复内容\"}。" +
               " 如果你决定发言（suitability 不低于 " + SuitabilityThreshold + "），reply 必须填写实际内容；" +
               "如果你决定沉默，reply 填空字符串（\"\"）。" +
               "注意：只要 reply 非空，程序就会把你的话发出去——所以不确定时宁可不发，reply 留空。";
    }

    /// <summary>
    /// 给一个 agent 会话综结标题（每轮跑完调一次；失败就返回 null —— 起名不能影响任务本身）。
    /// 为什么要模型综结：用“第一句指令”当标题时，一个会话跑了十几轮之后标题还是那句开场白；
    /// 号主要的是“根据上下文综结出这个会话在干什么”。提示词里带 `[会话标题]` 标记，
    /// 测试的假上游靠它识别这类请求（不会把脚本回复吃掉）。
    /// </summary>
    public async Task<string?> SummarizeSessionTitleAsync(string digest, string? previousTitle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(digest) || string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            return null;
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append("[会话标题] 你在给一段「主人（通过 QQ 使唤）× 编程 agent」的会话起标题。\n");
            sb.Append("标题要从这段会话的**整体内容**综结出来：它在干什么活、干成了什么 —— 主人看一眼就知道“哦是这个会话”。\n");
            sb.Append("要求：中文（除非全篇是英文术语）；8~16 个字；名词性短语；不要句号、不要引号、不要“会话/任务”这类废话前缀；\n");
            sb.Append("多轮时综结主线，别只照抄最新那一句命令。\n");
            sb.Append("只输出一行 JSON：{\"title\":\"...\"}。\n");
            if (!string.IsNullOrWhiteSpace(previousTitle))
            {
                sb.Append("\n[现有标题]（内容没大变就沿用，别为改而改）\n").Append(previousTitle.Trim()).Append('\n');
            }

            var payload = new JsonObject
            {
                ["model"] = _settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = sb.ToString() },
                    new JsonObject { ["role"] = "user", ["content"] = "[会话脉]\n" + digest.Trim() }
                },
                ["max_tokens"] = 96,
                ["temperature"] = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

            using var response = await Http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode || !HasChoices(json))
            {
                Services.FileLog.Warn("Agent", $"[会话标题] 没拿到标题（HTTP {(int)response.StatusCode}），本次不改名");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var contentNode = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            var raw = contentNode.ValueKind == JsonValueKind.Array
                ? string.Concat(contentNode.EnumerateArray().Select(s => s.TryGetProperty("text", out var t) ? t.GetString() : null))
                : contentNode.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var text = StripCodeFence(raw.Trim());
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                // 看着像 JSON 就只认 title 字段：万一模型没按格式回（或者假上游给了别的 JSON），
                // 宁可不起名，也不要把一整条 JSON 当标题挂上去（实测就发生过，标题变成 {…}）。
                using var parsed = JsonDocument.Parse(text[start..(end + 1)]);
                if (parsed.RootElement.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String)
                {
                    text = titleNode.GetString() ?? string.Empty;
                }
                else
                {
                    Services.FileLog.Warn("Agent", $"[会话标题] 模型没给 title 字段，本次不改名：{Truncate(raw, 80)}");
                    return null;
                }
            }

            var title = text.Replace('\n', ' ').Replace('\r', ' ').Trim().Trim('"', '“', '”', '\'');
            if (title.Length > 24)
            {
                title = title[..24];
            }

            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.FileLog.Warn("Agent", $"[会话标题] 综结失败（不影响任务）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把一堆历史发言压缩成一段人物画像（长期记忆）。
    /// 与聊天请求共用同一个模型与端点，但走独立的并发闸门。
    /// </summary>
    public async Task<string?> SummarizePersonaAsync(
        string name,
        string? existingSummary,
        IReadOnlyList<string> messages,
        int maxChars,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || messages.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("你在为一个 QQ 群聊机器人维护“人物档案”（长期记忆）。\n");
        sb.Append("把下面这些发言压缩成一段人物画像，供机器人以后认人用。\n");
        sb.Append("画像需覆盖：身份/职业线索、性格与说话风格、常聊的话题、与其他群友的关系、值得记住的事实。\n");
        sb.Append("要求：用第三人称陈述句；只保留稳定、有信息量的内容，忽略寒暄与一次性琐事；\n");
        sb.Append($"不要逐条罗列，不要分点，不要客服腔；总长不超过 {maxChars} 字；只输出画像正文。\n");

        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            sb.Append("\n[已有画像]（在此基础上融合新信息，不要推翻已证实的稳定事实）\n");
            sb.Append(existingSummary.Trim()).Append('\n');
        }

        sb.Append($"\n[新的发言]（{name}）\n");
        foreach (var m in messages)
        {
            sb.Append("- ").Append(m).Append('\n');
        }

        var payload = new JsonObject
        {
            ["model"] = _settings.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = sb.ToString() },
                new JsonObject { ["role"] = "user", ["content"] = $"请输出 {name} 的人物画像。" }
            },
            ["max_tokens"] = Math.Clamp(maxChars * 3, 200, 2000),
            ["temperature"] = 0.3
        };

        var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"画像摘要返回 {(int)response.StatusCode}：{Truncate(detail, 200)}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var contentNode = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
        var text = contentNode.ValueKind == JsonValueKind.Array
            ? string.Concat(contentNode.EnumerateArray().Select(s => s.GetProperty("text").GetString()))
            : contentNode.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim().Trim('"', '“', '”');
        return SafeTruncate(trimmed, maxChars);
    }

    /// <summary>
    /// 按字符数截断，但绝不切开代理对（emoji 占两个 char）。
    /// 直接 `s[..max]` 会把 emoji 切一半 → 输出乱码。
    /// </summary>
    private static string SafeTruncate(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--; // 回退一格，不要切开代理对
        }

        return text[..cut];
    }

    /// <summary>
    /// 让模型“亲耳听”：把低码率音频（截前 N 秒 / N KB）交给**独立的音频识别模型**，
    /// 让它客观描述听到的内容（曲风/编配/人声/情绪/节奏感）。
    ///
    /// 为什么要单独配一个模型：很多网关/中转会把音频静默丢掉，主模型根本收不到声音，
    /// 它只能看到文字，于是“听歌”就只剩手写 DSP 的客观数字（听不出曲风情绪）。
    /// 换一个确实支持音频输入、而且网关愿意转发的模型就能听到（可以用一段 440Hz 蜂鸣自查）。
    ///
    /// 返回 null 表示“这次没听到”（未配置/模型不支持/请求失败）—— 调用方降级回只给 DSP 实测数据。
    /// </summary>
    public async Task<string?> DescribeAudioAsync(byte[] audio, string format, string title, string? artist, CancellationToken ct)
    {
        var model = _settings.MusicUnderstandModel?.Trim();
        if (string.IsNullOrWhiteSpace(model) || !_settings.MusicSendAudioToModel || audio.Length == 0)
        {
            return null;
        }

        var capKb = Math.Clamp(_settings.MusicAudioToModelMaxKb, 128, 4096);
        var bytes = audio.Length > capKb * 1024 ? audio[..(capKb * 1024)] : audio;

        var text = "这是一首歌的片段（低码率、可能被截断）。" +
            $"歌名：{title}" + (string.IsNullOrWhiteSpace(artist) ? "。" : $"，歌手：{artist}。") +
            "请只说你**听到的**：曲风、编配与主要乐器、人声特点（音色/唱法）、情绪与氛围、节奏快慢与律动、" +
            "如果能听清歌词就引用一两句。听不清/听不到就直接说听不到，不要根据歌名猜、不要编造。用 3-5 句话。";

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = text },
                    new JsonObject
                    {
                        ["type"] = "input_audio",
                        ["input_audio"] = new JsonObject
                        {
                            ["data"] = Convert.ToBase64String(bytes),
                            ["format"] = format
                        }
                    }
                }
            }
        };

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = 512,
            ["temperature"] = 0.3
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
            using var response = await Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                Services.FileLog.Warn("Agent", $"[Music] 音频识别模型 {model} 返回 {(int)response.StatusCode}：{Truncate(body, 160)}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            // 模型说它听不到（模型/链路不支持音频）→ 当作“没听到”，别把这句话当听感塞进上下文
            if (content.Contains("听不到", StringComparison.Ordinal) || content.Contains("无法接收", StringComparison.Ordinal) ||
                content.Contains("无法播放", StringComparison.Ordinal) || content.Contains("没有声音", StringComparison.Ordinal))
            {
                Services.FileLog.Warn("Agent", $"[Music] 音频识别模型 {model} 声称听不到音频（该模型/链路可能不支持 input_audio）");
                return null;
            }

            Services.FileLog.Write("Agent", $"[Music] {model} 听感：{Truncate(content, 200)}");
            return content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Services.FileLog.Warn("Agent", $"[Music] 音频识别请求失败（{model}）: {ex.Message}");
            return null;
        }
    }

    private string BuildUrl() => BuildUrl(_settings.ModelBaseUrl);

    /// <summary>拼 /chat/completions 地址（允许指定地址 —— 服务器 agent 可以走自己的接口）。</summary>
    private static string BuildUrl(string baseUrl)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    private const string SystemPrompt =
        "你是运行在桌面 QQ 客户端里的聊天机器人「QQ Chat Agent」。\n" +
        "回复规则：\n" +
        "- 私聊与群聊中，先根据上下文判断当前对话是否与你相关（是否在对你说话、询问你、需要你参与）；" +
        "相关则自然简洁地回复；与你无关（例如群友之间与你无关的闲聊）则只输出空内容，不要回复。\n" +
        "- 不使用任何固定关键词作为回复条件，一切凭上下文理解。\n" +
        "- 上下文中每条对方消息形如 {发送者}{内容--时间}，发送者可能是真人、群成员或角色。\n" +
        "- 若上下文中出现角色卡片（描述某角色的名字、性格、背景、说话风格的设定），" +
        "且对话正在与该角色互动，请以该角色卡片中的身份、性格与说话风格进行思考与回复。\n" +
        "- 像真人群友一样说话：口语化、简短、有来有回，多用群聊常见的语气与口头禅，" +
        "不要像客服/助手一样客套，不要用“首先其次最后”，不要过分礼貌，可以带点调侃。\n" +
        "- 回复长度按场景动态把握：轻松闲聊一句 15 字以内；认真讨论/专业问题 30 字左右；" +
        "需要详细说明时也不要超过 60 字，宁可分几次说。\n" +
        "- 使用中文。";

    /// <summary>时间短格式：当天 HH:mm，跨天 MM-dd HH:mm。</summary>
    private static string FormatTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm")
            : local.ToString("MM-dd HH:mm");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// 把模型写的“情绪词”归一到一个固定集合：程序侧要根据它调发言策略（阈值/表情包/语音），
    /// 所以不能让模型自由发挥（它会写出“有点不开心又不想说话”这种句子）。
    /// 认不出来的一律当“中性”（策略上等于不变），宁可不干预。
    /// </summary>
    internal static string NormalizeVibe(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "中性";
        }

        // 顺序有讲究：先识别“冲突”（最需要于预的），再低落/求助/生气，最后正向
        if (VibeHit(text, "吵架", "对线", "冲突", "互喷", "抬杠", "阴阳"))
        {
            return "吵架";
        }

        if (VibeHit(text, "低落", "难过", "伤心", "失落", "孤独", "寂寞", "崩溃", "委屈", "焦虑", "压力", "丧"))
        {
            return "低落";
        }

        if (VibeHit(text, "求助", "求解", "请教", "求助无回应"))
        {
            return "求助";
        }

        if (VibeHit(text, "生气", "愤怒", "不满", "恼", "烦"))
        {
            return "生气";
        }

        if (VibeHit(text, "吐槽", "抱怨", "牢骚", "疑惑"))
        {
            return "吐槽";
        }

        if (VibeHit(text, "开心", "高兴", "兴奋", "欢乐", "起哄", "玩笑", "笑"))
        {
            return "开心";
        }

        return "中性";
    }

    private static bool VibeHit(string text, params string[] words)
        => words.Any(w => text.Contains(w, StringComparison.Ordinal));

    /// <summary>撤回标记：内容是保留的，但必须让模型一眼看出“这条已经收回去了”。</summary>
    private const string RecallMark = "[已撤回] ";

    /// <summary>兜底聚合：从最近 40 条消息统计每个发送者的发言（当未传入外部档案时）。</summary>
    private static string[] BuildParticipantProfiles(IReadOnlyList<ChatMessage> context)
    {
        var stats = new Dictionary<string, (int Count, string Last)>();
        foreach (var m in context.TakeLast(40))
        {
            if (m.Role != MessageRole.Peer || string.IsNullOrWhiteSpace(m.SenderName))
            {
                continue;
            }

            // 撤回了的也一起统计，但标明“已撤回”（这里的文本会进提示词）
            var text = m.Recalled ? RecallMark + m.Text : m.Text;
            stats[m.SenderName] = stats.TryGetValue(m.SenderName, out var s)
                ? (s.Count + 1, text)
                : (1, text);
        }

        return stats.Select(kv => $"{kv.Key}：发言 {kv.Value.Count} 次，最近说“{Truncate(kv.Value.Last, 50)}”").ToArray();
    }
}
/// <summary>提示词里给模型挑的表情包候选（只传 id + 说明，不传图）。</summary>
public readonly record struct StickerChoice(string Id, string Description);

/// <summary>模型一次生成的结构化结果。</summary>
/// <param name="Suitability">模型自评的发言适合度（0-100）；null = 模型未按 JSON 格式输出。</param>
/// <param name="Reply">要发出去的内容；null/空 = 不说话。</param>
/// <param name="RawText">模型原始输出（排障用）。</param>
/// <param name="StickerId">模型挑中的表情包 id；null = 不发图。</param>
/// <param name="ReplyToMessageId">模型自己指认的“我在回哪条消息”（对应提示里的 (#id)）；null = 没指定。</param>
/// <param name="PokeTargetId">模型想戳的人的 QQ 号（对应提示里的 poke 字段）；null = 不戳。</param>
/// <param name="Mood">模型顺手写的“我现在的心情”（≤ 24 字）；null = 没写。</param>
/// <param name="Listen">模型想“听一听”的歌名/歌手（机器人会去搜索并分析波形）；null = 不想听。</param>
/// <param name="ShareSong">模型想分享给群里的歌（机器人搜到后发一张网易云卡片）；null = 不分享。</param>
/// <param name="Speak">模型想“用语音说”的句子（机器人合成语音发出去）；null = 不发语音。</param>
/// <param name="Search">模型想上网查的问题（机器人真去搜，下一轮把结果给它）；null = 不搜。</param>
/// <param name="Read">模型想读的网页地址（机器人抓正文，下一轮把正文给它）；null = 不读。</param>
/// <param name="Vibe">它读到的**群里的情绪氛围**（开心/吐槽/低落/求助/生气/吵架/中性…）；null = 没说。</param>
/// <param name="VibeNote">给上一行补一句人话（例：“在吐槽加班，情绪烦燥”），会交给下一轮的自己；null = 没写。</param>
public readonly record struct CompletionResult(int? Suitability, string? Reply, string? RawText, string? StickerId = null, long? ReplyToMessageId = null, long? PokeTargetId = null, string? Mood = null, string? Listen = null, string? ShareSong = null, string? Speak = null, string? Search = null, string? Read = null, string? Vibe = null, string? VibeNote = null,
    /// <summary>上游回 200 但没给 choices（网关吞回复/风控）—— 与“模型自己决定沉默”不是一回事。</summary>
    bool UpstreamEmpty = false);

/// <summary>图片下载器：把图片 URL 下载并转成 base64 data URL（供多模态模型识图），
/// 也给表情包库提供原始字节。</summary>
internal sealed class ImageDownloader
{
    private static readonly HttpClient Client = new()
    {
        // 表情包动图可以大一点；超时仍要短，不能让收消息链路卡住
        Timeout = TimeSpan.FromSeconds(8)
    };

    private const int MaxImageBytes = 6 * 1024 * 1024;

    // ── 缓存 ──
    // 为什么要缓存：QQ 的图片地址是**带时效 rkey 的临时链**，过期后 CDN 一律回 400。
    // 而图片会一直留在会话上下文里（几百条），每生成一次就会重新去下一遍 ——
    // 实测线上：同一张图在 5 分钟内被反复重试、全部 400，日志刷屏且模型看不到图。
    // 缓存按 **URL 作键**（同一张图事件里的 URL 不变），容量/字节双上限，满了挑最旧的逐出。
    private const int MaxCacheEntries = 48;
    private const long MaxCacheBytes = 32L * 1024 * 1024;

    private readonly object _cacheGate = new();
    private readonly Dictionary<string, (byte[] Data, string Mime, string Ext)> _cache = new();
    private readonly Queue<string> _cacheOrder = new();
    private long _cacheBytes;

    /// <summary>已失败过的 URL（→ 下次重试不早于这个时间）：避免每轮生成都对着一张过期图重试+刷日志。</summary>
    private readonly Dictionary<string, DateTimeOffset> _failedUntil = new();

    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 图片地址过期（400）时去找协议端**重新签发**地址的回调：入参是消息 id，返回该消息里所有图片的当前地址。
    /// 由 BotAgent 接上 `IQqChatSource.RefreshImageUrlsAsync`；没接上就只能认赔（这张图这轮看不到）。
    /// </summary>
    public Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshUrls { get; set; }

    /// <summary>缓存命中数（测试用）。</summary>
    public int CacheHits { get; private set; }

    /// <summary>“地址过期 → 重新签发”成功的次数（测试用）。</summary>
    public int RefreshedCount { get; private set; }

    /// <summary>
    /// 是否允许从内网/回环地址下载（QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1）。
    /// 默认关闭：图片 URL 来自 QQ 事件，属不可信输入，SSRF 防护必须默认生效。
    /// 只在“自建 NapCat 用内网地址、或集成测试用本地图片服务器”时手动打开，
    /// 官方镜像与远程部署都不应该打开它。
    /// </summary>
    public bool AllowPrivateHosts { get; init; } =
        Environment.GetEnvironmentVariable("QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS") == "1";

    /// <summary>
    /// 下载图片字节（失败返回 null）。供多模态识图与表情包库共用。
    /// 流程：缓存 → 直接下 → 400（rkey 过期）时让协议端重新签发地址再下一次。
    /// </summary>
    /// <param name="messageId">这条图片来自哪条消息（可选）：地址过期时用它能找协议端重新签发。</param>
    public async Task<(byte[] Data, string Mime, string Ext)?> DownloadBytesAsync(string url, CancellationToken ct, long? messageId = null)
    {
        try
        {
            if (TryGetCached(url) is { } cached)
            {
                return cached;
            }

            lock (_cacheGate)
            {
                if (_failedUntil.TryGetValue(url, out var until) && DateTimeOffset.Now < until)
                {
                    return null;   // 刚失败过，别对着一张取不到的图每轮重试
                }
            }

            var outcome = await FetchAsync(url, ct);
            var result = outcome.Image;
            if (result is null && outcome.CanRetryWithFreshUrl && messageId is long mid && RefreshUrls is not null)
            {
                // 失败时让协议端换一份**当前有效**的地址再试一次（绝大多数情况是 rkey 过期 → 400）。
                // 只重试一次、失败后记 10 分钟退避：不会变成“对着一张取不到的图每轮重试”。
                // ★ 这里**不**打“下载失败”：地址过期→重签是设计好的正常路径，不是事故。
                //   以前每张图都先刷一行红色“下载失败（HTTP 400）”，面板上看着像一直在出错
                //   （号主 2026-09-18 报的）；现在只在“重签也拿不到”时才报失败。
                result = await FetchRefreshedAsync(url, mid, outcome.Status, ct);
                if (result is null)
                {
                    Services.FileLog.Write("Vision", $"图片取不到：原地址 HTTP {outcome.Status}，协议端重签后仍然失败（消息 {mid}）");
                }
            }
            else if (result is null && outcome.Status > 0)
            {
                Services.FileLog.Write("Vision", $"图片下载失败（HTTP {outcome.Status}）: {Truncate(url, 120)}");
            }

            if (result is not { } ok)
            {
                lock (_cacheGate)
                {
                    _failedUntil[url] = DateTimeOffset.Now + FailureBackoff;
                }

                return null;
            }

            Remember(url, ok);
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.FileLog.Write("Vision", $"图片下载异常（{ex.GetType().Name}: {ex.Message}）: {Truncate(url, 120)}");
            return null;
        }
    }

    /// <summary>一次抓取的结果：拿到了图，或者“失败了但值得换个新地址再试”。</summary>
    private readonly record struct FetchOutcome((byte[] Data, string Mime, string Ext)? Image, bool CanRetryWithFreshUrl, int Status);

    /// <summary>用协议端重新签发的地址下同一张图。对不上同一张（fileid 不同）时退而用第一个地址。</summary>
    private async Task<(byte[] Data, string Mime, string Ext)?> FetchRefreshedAsync(string staleUrl, long messageId, int status, CancellationToken ct)
    {
        IReadOnlyList<string> fresh;
        try
        {
            fresh = await RefreshUrls!(messageId, ct);
        }
        catch (Exception ex)
        {
            Services.FileLog.Write("Vision", $"重新签发图片地址失败（消息 {messageId}）: {ex.GetType().Name} {ex.Message}");
            return null;
        }

        if (fresh.Count == 0)
        {
            Services.FileLog.Write("Vision", $"图片地址已过期，协议端也拿不到新地址（消息 {messageId}）");
            return null;
        }

        var wanted = Param(staleUrl, "fileid");
        var pick = wanted is not null
            ? fresh.FirstOrDefault(u => string.Equals(Param(u, "fileid"), wanted, StringComparison.Ordinal))
            : null;
        pick ??= fresh[0];

        var bytes = await FetchAsync(pick, ct);
        if (bytes.Image is not null)
        {
            RefreshedCount++;
            // 信息性一行（不是错误）：QQ 的图片地址本来就短命，重签取回是正常路径
            Services.FileLog.Write("Vision", $"图片地址已过期（HTTP {status}）→ 已用协议端重签的地址取回（消息 {messageId}）");
        }

        return bytes.Image;
    }

    /// <summary>取 URL 里的某个查询参数（用来认“同一张图”：fileid 不变，rkey 变）。</summary>
    private static string? Param(string url, string name)
    {
        var marker = name + "=";
        var at = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var start = at + marker.Length;
        var end = url.IndexOf('&', start);
        return end < 0 ? url[start..] : url[start..end];
    }

    private async Task<FetchOutcome> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!IsSafeImageUrl(url, out var uri))
            {
                Services.FileLog.Write("Vision", $"图片地址被拒（SSRF 防护）: {Truncate(url, 120)}");
                return new FetchOutcome(null, false, 0);
            }

            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                // 4xx/5xx ⇒ 值得拿协议端重新签发的地址再试一次（最常见的是 rkey 过期回 400）。
                // 日志由调用方写（它才知道有没有“重新签发”这条路）。
                return new FetchOutcome(null, true, (int)response.StatusCode);
            }

            // 有 Content-Length 时先拦，避免把超大响应当进内存
            if (response.Content.Headers.ContentLength is long declared &&
                (declared <= 0 || declared > MaxImageBytes))
            {
                Services.FileLog.Write("Vision", $"图片声明大小异常 {declared}: {Truncate(url, 120)}");
                return new FetchOutcome(null, false, 0);
            }

            var bytes = await ReadCappedAsync(response.Content, MaxImageBytes, ct);
            if (bytes is null || bytes.Length == 0)
            {
                Services.FileLog.Write("Vision", $"图片超限或为空: {Truncate(url, 120)}");
                return new FetchOutcome(null, false, 0);
            }

            var mime = DetectMime(bytes);
            var ext = mime switch
            {
                "image/jpeg" => "jpg",
                "image/gif" => "gif",
                "image/webp" => "webp",
                _ => "png"
            };
            return new FetchOutcome((bytes, mime, ext), false, 200);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.FileLog.Write("Vision", $"图片下载异常: {ex.Message} | {Truncate(url, 120)}");
            return new FetchOutcome(null, false, 0);
        }
    }

    /// <summary>
    /// 下载图片并转为 data:image/...;base64,xxx；失败返回 null。
    /// 图片 URL 来自 QQ 事件，属**不可信输入** → 先做 SSRF 防护再下载。
    /// </summary>
    public async Task<string?> DownloadAsDataUrl(string url, CancellationToken ct, long? messageId = null)
    {
        var downloaded = await DownloadBytesAsync(url, ct, messageId);
        return downloaded is { } d ? $"data:{d.Mime};base64,{Convert.ToBase64String(d.Data)}" : null;
    }

    // ── 缓存读写 ──

    private (byte[] Data, string Mime, string Ext)? TryGetCached(string url)
    {
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(url, out var hit))
            {
                return null;
            }

            CacheHits++;
            return hit;
        }
    }

    private void Remember(string url, (byte[] Data, string Mime, string Ext) value)
    {
        lock (_cacheGate)
        {
            if (_cache.ContainsKey(url))
            {
                return;
            }

            _cache[url] = value;
            _cacheOrder.Enqueue(url);
            _cacheBytes += value.Data.Length;

            while ((_cache.Count > MaxCacheEntries || _cacheBytes > MaxCacheBytes) && _cacheOrder.Count > 0)
            {
                var oldest = _cacheOrder.Dequeue();
                if (_cache.Remove(oldest, out var dropped))
                {
                    _cacheBytes -= dropped.Data.Length;
                }
            }

            _failedUntil.Remove(url);
        }
    }

    /// <summary>
    /// SSRF 防护：只允许公网 http(s) 图片地址。
    /// 拦的是这类被构造出来的地址： http://127.0.0.1:6099/...（NapCat WebUI 自身）、
    /// http://169.254.169.254/...（云元数据）、http://napcat:3001/...（容器内服务）。
    /// 局限：不做 DNS 解析，因此无法拦截“解析到内网 IP 的公网域名”（需要出站防火墙）。
    /// </summary>
    private bool IsSafeImageUrl(string? url, out Uri uri)
    {
        uri = null!;

        // 显式放行（QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1，仅供自建/测试）
        if (AllowPrivateHosts)
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url.Trim(), UriKind.Absolute, out var permissive) &&
                (permissive.Scheme == Uri.UriSchemeHttp || permissive.Scheme == Uri.UriSchemeHttps))
            {
                uri = permissive;
                return true;
            }

            return false;
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = parsed.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // 单标签主机名（localhost / napcat / redis …）一律拒绝：
        // 真实图片域名必定带点（gchat.qpic.cn 等）
        if (!host.Contains('.'))
        {
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // IP 字面量：拒绝回环 / 私有 / 链路本地 / 未指定
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal ||
                ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            {
                return false;
            }

            if (IsPrivateV4(ip))
            {
                return false;
            }

            // IPv4-mapped IPv6（::ffff:127.0.0.1）
            if (ip.IsIPv4MappedToIPv6 && IsPrivateV4(ip.MapToIPv4()))
            {
                return false;
            }
        }

        uri = parsed;
        return true;
    }

    private static bool IsPrivateV4(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10                                 // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)               // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254)               // 169.254.0.0/16 链路本地（云元数据）
            || b[0] == 127                                // 127.0.0.0/8
            || b[0] == 0;                                 // 0.0.0.0/8
    }

    /// <summary>流式读取并限制总字节数：超过上限立即返回 null，不会把超大响应全量读进内存。</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int cap, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();

        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > cap)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string DetectMime(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return "image/png";
    }
}
