using System.Text;
using BotAgent.Domain.Stickers;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Conversation;
using BotAgent.Services.Agent;

namespace BotAgent.Services.Model;

/// <summary>
/// 提示词装配（批次 6 从 <c>OpenAiClient.CompleteAsync</c> 搬出来）：把"这一轮要告诉模型什么"拼成**一段字符串**。
///
/// 为什么单独一层（§13.4）：读代码的人现在能一眼看到"模型到底被告知了什么"，改一句话也不用再翻那个 570 行的大方法；
/// 而它**只输出字符串** —— 发不发、怎么发、怎么重试都属于传输层（<c>ModelTransport</c>），两件事不再缠在一起。
///
/// 三条不变量（每一条都直接改变群里的观感，别在重构里松掉）：
///   ① **顺序即语义**：先人设、再气氛与分寸、再动作契约、再对话对象 —— 段落顺序变了模型的行为就会飘；
///   ② **按开关追加**：`[可选的结构化动作]`/`[联网搜索]`/`[想听一首歌]`/`[用语音说话]` 都只在对应开关打开时才出现，
///      开关全关时拼出来的提示词与改造前**逐字一致**（§5.3 兼容红线）；
///   ③ **措辞是对外契约**：这些字不是随便写的（harness 里"系统提示 < 4000 字"那条软线就是它们的哨兵），搬家不改字。
/// </summary>
public static class PromptBuilder
{
    /// <summary>一次请求要用的提示词素材（都是值或只读引用，装配过程不改它们）。</summary>
    public readonly record struct PromptRequest(
        string SystemPrompt,
        string? BotIdentity,
        string? Persona,
        int AiDesire,
        IReadOnlyList<ChatMessage> Window,
        IReadOnlyCollection<long> QuotableIds,
        string? ProfilesText,
        IReadOnlyList<StickerChoice>? Stickers,
        bool PokeContext,
        bool Proactive,
        string? MoodText,
        string? MusicText,
        string? LinkText,
        string? RecallText,
        string? GroupRolesText,
        string? VibeHint,
        string? SearchText,
        int SuitabilityThreshold,
        bool EnableListen,
        bool EnableVoice,
        int VoiceMaxChars,
        int VoiceEagerness,
        bool EnableWebSearch,
        bool EnableAsk,
        bool EnableToolRequest,

        /// <summary>
        /// 批次 D：服务端按策略裁剪过的工具清单（由 <see cref="Tools.ToolPromptText" /> 生成）。
        /// 空 = 这次一个工具都没开 → 整段不出现（与“开关全关时提示词逐字不变”的纪律一致）。
        /// </summary>
        string? ToolList);

    /// <summary>把这一轮的系统提示词拼出来（纯字符串拼接，不发任何请求）。</summary>
    public static string Build(PromptRequest request)
    {
        // 四段分开拼（顺序即语义，§5.3 兼容红线）：身份与时间 → 现场指引 → 表达手段 → 参与者。
        return string.Concat(
            BuildIdentitySection(request),
            BuildConversationSection(request),
            BuildExpressionSection(request),
            BuildParticipantsSection(request));
    }

    /// <summary>撤回标记：内容是保留的，但必须让模型一眼看出“这条已经收回去了”。</summary>
    /// <summary>① 身份、时间、人设、发言阈值、结构化动作契约（§5.3：开关全关时与改造前逐字一致）。</summary>
    private static string BuildIdentitySection(PromptRequest request)
    {
        var systemContent = request.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(request.BotIdentity))
        {
            systemContent += $"\n你是登录账号 QQ：{request.BotIdentity} 的机器人（群聊中别人 @QQ{request.BotIdentity} 或喊你昵称就是在叫你）。";
        }

        // 当前时间：模型没有时钟 —— 不告诉它，被问“现在几点”就只能靠训练语料猜（线上实测：经常答错）。
        // 顺便把“时效性信息必须搜”与它写在一起：搜索的“自主判断”需要一个明确依据，
        // 而“哪些属于今年/现在”全靠这个基准时间才能分清。
        var now = Clock.Now;
        systemContent +=
            "\n\n[现在的时间]\n现在是 " + now.ToString("yyyy-MM-dd HH:mm") + "（" + PeriodCn(now) + "）" +
            "（星期" + WeekdayCn(now.DayOfWeek) + "，UTC" + now.ToString("zzz") + "）。\n" +
            "• 有人问“现在几点 / 今天几号 / 今天周几 / 还有几天” → **直接按它答**，不要靠自己印象猜（你并没有钟）；\n" +
            "• “今天 / 昨天 / 明天 / 这周 / 刚刚 / 上次”这类相对时间，全以它为基准算；\n" +
            "• 需要具体日期但拿不准时，宁可说“我记得是 X 号”这种带保留的话，也不要编一个硬结论；\n" +
"• 说到“时段”时按上面那个词来（凌晨/早上/上午/中午/下午/傍晚/晚上/深夜）——别把下午说成早上、把晚上说成上午（这是纯看错 24 小时制的低级错）。";

        if (!string.IsNullOrWhiteSpace(request.Persona))
        {
            systemContent += "\n\n[机器人人设档案]\n" + request.Persona.Trim();
        }

        systemContent += BuildSuitabilityInstruction(request.AiDesire, request.SuitabilityThreshold);

        // 结构化动作契约（V3 §8.1）：**只在对应开关打开时追加**。
        // 两个开关都关着 → 这段是空串，提示词与改造前逐字一致（§5.3 兼容红线）。
        if (request.EnableAsk || request.EnableToolRequest)
        {
            systemContent += BuildActionContract(request.EnableAsk, request.EnableToolRequest);
        }

        // 批次 D：工具清单（只列这次开着的；参数说明已被渲染层截短）。
        if (!string.IsNullOrWhiteSpace(request.ToolList))
        {
            systemContent += request.ToolList;
        }


        return systemContent;
    }

    /// <summary>② 现场指引：回复对象、上下文标记、心情、音乐、链接、搜索、撤回、群身份、主动开口、上一轮气氛、刚查到的资料。</summary>
    private static string BuildConversationSection(PromptRequest request)
    {
        var systemContent = string.Empty;
        // 引用谁：最近几条别人的消息都带了 (#id)，让模型自己指认。
        // replyTo 的语义必须写死 —— 模型很容易把“让我不爽的那条（素材）”当成“我在回哪条（对象）”：
        // 线上实测（handoff-4 §23）群友 c 复读了机器人那句话，机器人说的是“别学我说话！”，
        // 引用却挂在上一条别人的消息上 —— 群里看到的就是“回复错人”，§22 的启发式修不了这一类。
        if (request.QuotableIds.Count > 0)
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
        if (!string.IsNullOrWhiteSpace(request.MoodText))
        {
            systemContent +=
                "\n\n[你此刻的心情]\n" + request.MoodText.Trim() +
                "\n（心情只影响你说话的语气与热络程度：烦的时候就短、敷衽、甚至懒得理；心情好可以开玩笑。别把它当成要宣告的信息。）";
        }

        // 群里刚分享的音乐：把“实测到的事实 + 歌词”交给模型，让它聊得像真听过（而不是望着歌名编）
        if (!string.IsNullOrWhiteSpace(request.MusicText))
        {
            systemContent +=
                "\n\n[群里刚分享的音乐]\n" + request.MusicText.Trim() +
                "\n（这是你刚“听”过的一首歌：可以就节奏/旋律/歌词/年代感聊两句，或顺着群友的话接。" +
                "歌名、歌手、时长、BPM、响度、段落这些事实一律以上面的实测数据为准，不要另编；" +
                "歌词里没有的内容不要虚构，也别把整段歌词抰出来 —— 引用一两句点到即止，像真的听过那样随口提。）";
        }

        // 群里消息里的链接：机器人已经打开看过（标题/摘要），别让它对着一串 URL 猜
        if (!string.IsNullOrWhiteSpace(request.LinkText))
        {
            systemContent +=
                "\n\n[群里刚发的链接]\n" + request.LinkText.Trim() +
                "\n（链接内容已取回，按上面的标题/摘要聊就行；别编造页面里没有的细节，也别把整段摘要复述一遍。）";
        }

        // 联网搜索：模型可以要求“去查一下”（search 字段）或“读一下这个页面”（read 字段）。
        // 两轮动作：机器人先把结果拿回来，下一轮它再拿着事实说话（和听音乐同一套思路）。
        if (request.EnableWebSearch)
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
        if (!string.IsNullOrWhiteSpace(request.RecallText))
        {
            systemContent +=
                "\n\n[有人撤回了消息]\n" + request.RecallText.Trim() +
                "\n（规矩：① 你可以记得内容，但**不要复述、不要引用、更不要说“我都看见了/撤什么”这类话**，" +
                "也不要暗示自己看到了 —— 撤回就是不想让它留在群里；" +
                "② 如果对方撤回后马上又发了更正/补充，就当没这回事，顺着新内容接；" +
                "③ 只有当大家都在好奇、气氛适合时，才可以轻描淡写问一句，别审问。）";
        }

        // 群成员身份（群主 / 管理员 / 群头衔）：让模型知道“谁说了算、这人什么来头”
        if (!string.IsNullOrWhiteSpace(request.GroupRolesText))
        {
            systemContent +=
                "\n\n[本群身份]\n" + request.GroupRolesText.Trim() +
                "\n（如上：群主与管理员能踢人、能撤回消息，头衔多是本人自己写的梗。" +
                "这些只是让你心里有数：该配合配合（人家真是管事的），该吐槽吐槽（头衔本身就是个乐子），" +
                "但不要拿身份拍马屁、也不要拿它压人。）";
        }

        // 主动开口：这次不是别人问它，是它自己想说话 —— 不说明的话，模型会以为有人在跟它说话
        if (request.Proactive)
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
        if (!string.IsNullOrWhiteSpace(request.VibeHint))
        {
            systemContent +=
                "\n\n[你上一条消息时的感觉]\n" + request.VibeHint.Trim() +
                "\n（只是你自己的记忆，别把它读出来；如果现在气氛已经变了，以现在为准。）";
        }

        // 刚搜到的结果（或读到的网页正文）：交给模型，用完就清
        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            systemContent +=
                "\n\n[刚查到的资料]（你上一轮说要去查，这就是查回来的）\n" + request.SearchText.Trim() +
                "\n（怎么用：① 用你自己的语气说出来 —— 就像你本来就知道这件事一样，别用“根据资料/搜索结果显示”这种播报腔；" +
                "② 接着刚才的话头说，别像换了个人：你的人物设定、口头禅、说话节奏照旧；" +
                "③ 只讲跟那个问题有关的部分，不要把整段资料念一遍；" +
                "④ 上面没写的细节别编，拿不准就说拿不准；" +
                "⑤ 来源链接不用贴（除非有人问“哪来的”）。）";
        }


        return systemContent;
    }

    /// <summary>③ 表达手段：听歌/分享歌、语音（含语气参数）、戳一戳、表情包候选。</summary>
    private static string BuildExpressionSection(PromptRequest request)
    {
        var systemContent = string.Empty;
        // 想听一首歌：模型可以主动要求“让我听听这首歌”（群里让它听歌就是走这条路）
        if (request.EnableListen)
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

        // 用语音说话：模型可以要求“这句用语音说”（speak 字段）。2026-09-21 起：
        // **什么时候用语音这件事由模型自己判断**（在此之前代码侧还有一道“气氛沉就一律不发”的硬拦，
        // 已改成只把气氛作为提示给它、由它自己权衡）。代码侧只保留“技术性”的限：开关、字数上限、频率下限。
        // 所以提示词里要把这三件事说清：你有权决定 / 决定时看什么 / 硬约束在哪（别去撞门）。
        if (request.EnableVoice)
        {
            var maxChars = Math.Clamp(request.VoiceMaxChars, 10, 300);
            var eagerness = Math.Clamp(request.VoiceEagerness, 0, 100);
            var interval = OpenAiClient.VoiceIntervalSeconds(eagerness);
            var posture = eagerness switch
            {
                <= 20 => "现在调得很低：**除非有人明确让你说话/唱一个**，否则就用文字",
                <= 40 => "现在偏克制：只在情绪确实比文字重的时候才用",
                <= 60 => "现在是默认档：该用就用、不该用就用文字，看你自己的感觉",
                <= 80 => "现在偏积极：合话头就多用一点，别浪费“声音”这个手段",
                _ => "现在调得很高：能说就说，但别连着两条都带 speak"
            };
            systemContent +=
                "\n\n[用语音说话]\n" +
                "用不用语音、哪一句用语音，**都由你自己定**（没人检查你该不该用）。想用就在 JSON 里加 speak 字段，" +
                "写上要说出口的那句话（每段 ≤ " + maxChars + " 字）：" +
                "{\"suitability\": 85, \"reply\": \"…\", \"speak\": \"这句话我想用声音说\"}。机器人会把它合成语音发出去。\n" +
                "**断句由你定**：停顿与换气写进 speak 本身——小停用「，」、大停用「。」、拖长用「……」；" +
                "想分成几条语音条就用 **|** 隔开（最多 3 段，例：\"你猜猜看 | 我今天没干别的，就想你了\"）。" +
                "别写成一整句没有标点的话 ✗（念出来是平的，停顿全由机器瞎猜）。\n" +
                $"面板里的「语音积极性」现在是 {eagerness}/100：{posture}。\n" +
                "你决定时值得看的是：这句话的价值在不在“声音”上（道谢/撒娇/学人说话/唱歌/开心委屈），" +
                "在不在这段关系的熟络度上（刚说上话的人别突然发语音，熟人才“自然”），" +
                "以及当下气氛（有人难受、在吵架、在说正事时，声音往往添乱——但这也是你判断，不是硬规则）。\n" +
                $"节奏你自己把握：{interval} 秒以上一条比较自然（着急/来情绪时短一点也行）。" +
                "代码只在**几秒内连发**时才兜一下 ✗（那是防炸的，不是判断该不该发）；" +
                "但同一会话别连着两条都带 speak、也别每句都带——群里语音是“稀罕事”，滥了就烦人。\n" +
                "speak 里写的就是要说出口的那句话：口语化、短、别放链接/代码/括号里的舞台说明（如“(笑)”）；" +
                "**语音说了话，reply 里就别再说一遍**（默认它不会被发出去 ✗；「你可算回我了」和「你终于回来了！」算同一句）。" +
                "真想再给对方一段**看的**文字（链接、号码、@某人、一段说明），就在同一条 JSON 里加 \"both\": true，" +
                "那样 reply 会照发；其余情况宁可把话都说进 speak（说不完就用 | 分段）。\n" +
                "想让这句话听起来更有情绪，可以在同一条 JSON 里再带三个可选字段：" +
                "voiceEmotion（happy/sad/angry/surprised/fearful/disgusted/neutral）、" +
                "voiceSpeed（0.5~2.0，1.0 = 原速）、voicePitch（-12~12，正数更尖更亮）。" +
                "**按你这句话的语境自己定**：撒娇/开心 → happy + 稍快稍高；冷淡/敷衍 → neutral + 稍慢；" +
                "不满/凶 → angry；平静叙述 → 三个都省。省了就按默认来，别每句都堆参数，也别硬套不搭的情绪。" +
                "字数超了或太频繁时这次就按普通文字回，不要在上下文里提到“语音发不出去”。";
        }

        // 被戳过才给的指令：戳回去是**可选**动作，看当下心情 —— 不必每次被戳都戳一次
        if (request.PokeContext)
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
        if (request.Stickers is { Count: > 0 })
        {
            systemContent += BuildStickerInstruction(request.Stickers);
        }


        return systemContent;
    }

    /// <summary>④ 参与者档案（外部传入优先，否则按最近消息聚合）+ 带图那一轮的看图提示。</summary>
    private static string BuildParticipantsSection(PromptRequest request)
    {
        var systemContent = string.Empty;
        // 角色卡片：优先使用外部传入的人物档案（按 QQ 号建表积累），否则由最近消息聚合
        var participants = request.ProfilesText is not null
            ? new[] { request.ProfilesText }
            : BuildParticipantProfiles(request.Window);
        if (participants.Length > 0)
        {
            systemContent += "\n\n[会话参与者档案（由他们的历史发言总结而来，回复时参考每个人是谁、说过什么）]\n" +
                             string.Join("\n\n", participants);
        }
        return systemContent;
    }

    private const string RecallMark = "[已撤回] ";

    /// <summary>
    private static string PeriodCn(DateTimeOffset now)
        => now.Hour switch
        {
            >= 0 and < 5 => "凌晨",
            >= 5 and < 8 => "早上",
            >= 8 and < 11 => "上午",
            >= 11 and < 13 => "中午",
            >= 13 and < 17 => "下午",
            >= 17 and < 19 => "傍晚",
            >= 19 and < 23 => "晚上",
            _ => "深夜",
        };


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

    /// 这里只描述协议，不承诺任何权限 —— 能不能执行仍然由服务端闸门决定。
    /// </summary>
    private static string BuildActionContract(bool askEnabled, bool toolEnabled)
    {
        var sb = new StringBuilder();
        sb.Append("\n\n[可选的结构化动作]\n");
        sb.Append("你可以在同一条 JSON 里再写一个 action 字段，用来说明这一轮到底要做什么；**不写它就等于正常回复**。\n");
        sb.Append("• \"reply\"：正常说话（默认）。\n");
        sb.Append("• \"silent\"：这一轮什么动作都不做 —— reply 留空，并且**不要**再写 speak / sticker / poke / listen / " +
                  "shareSong / search / read，服务端会把它们全部忽略。\n");
        if (askEnabled)
        {
            sb.Append("• \"ask\"：你想先问一句再往下做 —— 把要问的话写在 reply 里；服务端会包上编号和有效期替你发出去，" +
                      "回答只是一条普通消息，**不会**给你任何权限。\n");
        }

        if (toolEnabled)
        {
            sb.Append("• \"tool\"：你想用工具 —— 把工具名写进 toolRequest。服务端只认它自己登记过的工具，" +
                      "没登记的名字不会执行；有的工具还需要群里的人批准，批准前不会有任何动作。\n");
        }

        sb.Append("控制字段只写短标识符；拿不准就别写 action，按老格式回。");
        return sb.ToString();
    }

    /// <summary>按对话欲望生成“发言适合度”评分指令。</summary>
    private static string BuildSuitabilityInstruction(int aiDesire, int threshold)
    {
        var desire = aiDesire switch
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
               " 如果你决定发言（suitability 不低于 " + threshold + "），reply 必须填写实际内容；" +
               "如果你决定沉默，reply 填空字符串（\"\"）。" +
               "注意：只要 reply 非空，程序就会把你的话发出去——所以不确定时宁可不发，reply 留空。";
    }


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

        return stats.Select(kv => $"{kv.Key}：发言 {kv.Value.Count} 次，最近说“{ModelOutputText.Truncate(kv.Value.Last, 50)}”").ToArray();
    }
}
