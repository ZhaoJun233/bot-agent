namespace BotAgent.Domain.Ports;

/// <summary>
/// 心情 / 被戳台账的存取端口（由 <c>Adapters/Persistence/MoodStore</c> 实现，见 §6.4）。
///
/// 为什么要有它：用例层（回复链读心情、戳一戳决定要不要戳回去）只该说“现在什么心情”“刚才被戳了几次”，
/// 不该知道它是落库还是落内存、TTL 从哪儿读。时间一律**从外面给**（<paramref name="now" />）——
/// 于是“过期 / 冷却”这类规则可以毫秒级确定性测（§6.3）。
/// </summary>
public interface IMoodRepository
{
    /// <summary>写心情文字（空值 = 清掉）；返回是否真的变了。</summary>
    bool SetText(string? text, DateTimeOffset now);

    /// <summary>给提示词用的一句话（含“什么时候写的、还算不算数”）。</summary>
    string Describe(DateTimeOffset now);

    /// <summary>当前心情文字（过期 / 空 = null）。</summary>
    string? CurrentText(DateTimeOffset now);

    /// <summary>该不该戳回去（顺着“刚被戳过”的风）；<paramref name="reason" /> 给日志用。</summary>
    bool WillPokeBack(DateTimeOffset now, out string reason);
}
