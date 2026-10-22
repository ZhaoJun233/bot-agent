namespace BotAgent.Domain.Ports;

/// <summary>
/// 官方通道的**别名号映射**端口（由 <c>Adapters/Persistence/OfficialIdMap</c> 实现）。
///
/// 官方（QQ 开放平台）只给 per-app 的 openid，而机器人内部处处用“像 QQ 号的数字”。
/// 这个端口就是那层翻译：openid ⇄ 别名号（8e15 起，与真实 QQ 号永不冲突），并落盘记住映射。
/// 为什么要有它：用例只该说“这个 openid 换成号”“这个号是谁”，不该知道映射存在哪个 JSON、怎么分配号段。
/// </summary>
public interface IOfficialIdMap
{
    /// <summary>已经记下来的映射条数（启动横幅 / 排障用）。</summary>
    int Count { get; }

    /// <summary>openid → 别名号（没有就分配一个并记住）。</summary>
    long AliasFor(string openId);

    /// <summary>别名号 → openid（没有 = null）。</summary>
    string? OriginalOf(long alias);

    /// <summary>把内存里的映射写盘（变更后调用一次）。</summary>
    void Flush();
}
