using System;

namespace BotAgent.Services.Model;

/// <summary>
/// 历史消息的时间渲染（#135）。
///
/// **只取决于消息本身**：同一个时间戳无论在几点、跨没跨午夜渲染出来的字都一样。
/// 这一点决定了模型输入的历史前缀稳不稳定（上游缓存按 token 前缀命中）。
/// 相对信息（今天是几号、现在几点）由提示词末尾的 [现在的时间] 一段承担。
///</summary>
public static class ChatHistoryTime
{
    /// <summary>渲染成绝对形式 MM-dd HH:mm（本地时区）。</summary>
    public static string Render(DateTimeOffset time) => time.ToLocalTime().ToString("MM-dd HH:mm");
}
