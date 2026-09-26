using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Services.Qq;
using System.Collections.Concurrent;

namespace BotAgent.Services.Conversations;

/// <summary>
/// 「机器人自己发出去的消息」台账（id → 原话 + 时间）：认出"别人引用回复了我说的哪一句"。
///
/// 为什么要落库（<see cref="OwnMessageStore" />）：以前只有内存表（上限 200、重启清空），
/// 而每次部署都会重启 —— 于是"引用机器人上一句"在部署后全部认不出来（管理员 2026-09-19 反馈被吞）。
/// 内存表是落库表的**懒加载缓存**：查不到的（比如老消息）就去库里找；写的时候两边一起写。
///
/// ⚠ 懒加载必须在**查询之前**发生：重启后第一件事往往就是"有人引用了上一句"，
/// 那时候机器人还没发过任何消息 —— 只在发送时加载的话，这里会永远查不到（2026-09-19 差点踩到）。
/// </summary>
public sealed class OwnMessageLedger
{
    private readonly ConcurrentDictionary<long, (string Text, DateTimeOffset At)> _messages = new();
    private readonly Action<string> _log;
    private int _loaded;

    private readonly IOwnMessageRepository _store;

    public OwnMessageLedger(IOwnMessageRepository store, Action<string> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>首次用到时把库里的"我发过哪些消息"读回内存（幂等；首次运行顺带导入老 JSON，见 OwnMessageStore）。</summary>
    public void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1)
        {
            return;
        }

        try
        {
            var rows = _store.LoadRecent(_store.MaxEntries);
            if (rows.Count == 0)
            {
                return;
            }

            foreach (var row in rows)
            {
                _messages[row.Id] = (row.Text, row.At);
            }

            _log($"记起了 {_messages.Count} 条自己发过的消息（引用回复识别用）");
        }
        catch (Exception ex)
        {
            _log("读取自己发过的消息台账失败（当空表继续）：" + ex.Message);
        }
    }

    /// <summary>按协议端给的消息 id 找原话（找不到 = 不是机器人发的，或已经超出保留范围）。</summary>
    public bool TryGet(long messageId, out (string Text, DateTimeOffset At) entry)
        => _messages.TryGetValue(messageId, out entry);

    /// <summary>
    /// 记住"这句话是我哪条消息发出去的"。
    /// 拿不到 id 的协议端（<c>MessageId &lt;= 0</c>）记不了 —— 不是错误，静默跳过。
    /// 只留最近 <see cref="OwnMessageStore.MaxEntries" /> 条（引旧消息的情况极少）。
    /// </summary>
    public void Remember(SendResult sent, string text)
    {
        if (!sent.Ok || sent.MessageId <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        EnsureLoaded();
        var now = Clock.Now;
        _messages[sent.MessageId] = (text, now);
        if (_messages.Count > _store.MaxEntries)
        {
            // 简单剪枝：把最老的一半丢掉（不做 LRU，没必要）
            foreach (var stale in _messages.OrderBy(kv => kv.Value.At).Take(_messages.Count / 2).ToList())
            {
                _messages.TryRemove(stale.Key, out _);
            }
        }

        // 落库：单条 upsert + 按时间剪枝（以前是每次整份重写 JSON）
        _store.Upsert(sent.MessageId, text, now);
        _store.PruneTo(_store.MaxEntries);
    }
}
