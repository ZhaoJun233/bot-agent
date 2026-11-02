using System.Text;
using BotAgent.Domain.Conversation;
using BotAgent.Services.OneBot;

namespace BotAgent.Services.Reply;

/// <summary>
/// 入站文本的两种标注规则：**旁白括号**与**引用回复**。都是纯函数（只看消息本身 + 文本规则），
/// 不碰会话状态、不发消息，所以从 ReplyPipeline 里搬出来单独放：
/// 改这类“给人看的话怎么拼”的规则时，不用再翻一千多行的主链。
/// </summary>
internal static class ReplyAnnotations
{
/// <summary>
/// 把群友消息**开头 / 结尾**的括号旁白转成标注（`〔旁白：笑〕`），
/// 返回（是否整条都是旁白、标注后的文本）。
///
/// 口径来自群里真实消息（管理员反馈后我拉了 700 多条带括号的群消息看过）：
///   • “（雨哗啦啦）”            整条都是旁白 → `〔旁白：雨哗啦啦〕`（只接收，不触发回复）；
///   • “行（端在桌上）”          尾巴上的旁白 → `行〔旁白：端在桌上〕`；
///   • “（放在地上）来吧，猫猫，”  开头的旁白   → `〔旁白：放在地上〕来吧，猫猫，`；
///   • 中间位置的括号**不碰**（“（2026）年的计划”里的括号是正文，群样本里也没这种旁白）。
///
/// 管理员 2026-09-14 追加的口径（§25）：**不要单纯忽略，也要接收，但要特别注明** ——
/// 旁白不再被丢掉，而是标成 `〔旁白：…〕` 一起进聊天记录与模型上下文：模型能拿它理解现场
/// （“端到桌上”“抱着猫”这类动作本来就是语境），但一眼就知道那不是“他说的话”。
///
/// 三个例外——机器人自己的内容标记（[图片] / [表情:斜眼笑] / [动画表情:…] / [语音]…）
/// 是“对方发了啥”的记录，不是旁白：一律保留（把它们一起标注就等于把群友发表情/图片的记录抹了，踩过）。
/// 私聊 / 带图 / @ 机器人的消息完全不动（宁可多回也不装死）。
/// </summary>
internal static (bool AsideOnly, string Text) AnnotateBracketAsides(QqChatMessage msg)
{
    var text = msg.Text?.Trim() ?? string.Empty;
    if (!msg.IsGroup || msg.MentionedSelf || msg.ImageUrls is { Count: > 0 } || text.Length == 0)
    {
        return (false, text);
    }

    // 开头的旁白（可能连着几段）→ 依次标在正文前面
    var leading = new StringBuilder();
    var rest = text;
    while (TextRules.TryTakeLeadingBracket(rest, out var afterLead, out var inner))
    {
        leading.Append(MessageMarkers.AsAside(inner));
        rest = afterLead;
    }

    // 结尾的旁白（可能连着几段）→ 先倒着收，再按原顺序接到正文后面
    var trailing = new List<string>();
    while (TextRules.TryTakeTrailingBracket(rest, out var afterTail, out var inner))
    {
        trailing.Add(MessageMarkers.AsAside(inner));
        rest = afterTail;
    }

    if (leading.Length == 0 && trailing.Count == 0)
    {
        return (false, text);   // 本来就没有旁白（如单一个“？”）→ 当普通消息
    }

    rest = rest.Trim();
    if (rest.Length == 0 || TextRules.IsOnlyDecoration(rest))
    {
        // 剥完只剩标点/表情（如“（真的）？”）→ 整条就是旁白，标好的旁白就是全文
        trailing.Reverse();
        return (true, leading + string.Concat(trailing));
    }

    trailing.Reverse();
    return (false, leading + rest + string.Concat(trailing));
}

/// <summary>
/// 把“引用回复”拼成给模型看的标注：`[回复 老王「你昨天说的那个 bug」]`。
/// 为什么要把原话也带上：群聊里一句“我也是”全靠引用的那条才能懂 —— 上下文里虽然也有那条，
/// 但位置可能隔着好几十条，模型不一定会自己去对（而且对不上就会编）。
/// 引的是机器人自己那句就用“你”（模型才分得清是跟它说话）。
/// </summary>
internal static string BuildReplyAnnotation(string name, string quotedText)
{
    name = MessageMarkers.EscapeExternalControlTags(name);
    if (string.IsNullOrEmpty(name) && string.IsNullOrWhiteSpace(quotedText))
    {
        return "[回复一条更早的消息（我这边已经看不到原文了）]";
    }
    var text = (quotedText ?? string.Empty).Replace('\n', ' ').Trim();
    // 引用的那条自己可能也是回复（“[回复 你「…」] xxx”）：嵌套引号只会变成噪音，剥一层
    if (text.StartsWith("[回复", StringComparison.Ordinal))
    {
        var close = text.IndexOf(']');
        if (close > 0)
        {
            text = text[(close + 1)..].Trim();
        }
    }

    text = MessageMarkers.EscapeExternalControlTags(text);
    if (text.Length == 0)
    {
        return $"[回复 {name}（引用的内容我这边取不到）]";
    }
    return $"[回复 {name}「{TextRules.Shorten(text, 40)}」]";
}
}
