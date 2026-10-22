using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Messaging;
using BotAgent.Services.Qq;
using System.Globalization;

namespace BotAgent.Services.Conversations;

/// <summary>
/// 「机器人自己发出去的消息」台账（完整 MessageRef → 原话 + 时间）：引用识别不能跨平台/账号/会话。
///
/// 为什么要落库（<see cref="OwnMessageStore" />）：以前只有内存表（上限 200、重启清空），
/// 而每次部署都会重启 —— 于是"引用机器人上一句"在部署后全部认不出来（管理员 2026-09-19 反馈被吞）。
/// 首次使用懒加载最近 scoped 记录；存取在同一 gate 内串行，旧裸 id 数据保留但不当作命中。
///
/// ⚠ 懒加载必须在**查询之前**发生：重启后第一件事往往就是"有人引用了上一句"，
/// 那时候机器人还没发过任何消息 —— 只在发送时加载的话，这里会永远查不到（2026-09-19 差点踩到）。
/// </summary>
public sealed class OwnMessageLedger
{
    private readonly Dictionary<MessageRef, (string Text, DateTimeOffset At)> _messages = new();
    private readonly object _gate = new();
    private readonly Action<string> _log;
    private bool _loaded;

    private readonly IOwnMessageRepository _store;

    public OwnMessageLedger(IOwnMessageRepository store, Action<string> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>首次用到时把库里的"我发过哪些消息"读回内存（幂等；首次运行顺带导入老 JSON，见 OwnMessageStore）。</summary>
    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            try
            {
                var rows = _store.LoadRecentScoped(_store.MaxEntries);
                foreach (var row in rows)
                    if (row.Ref is not null && TryCanonical(row.Ref, out var key))
                        _messages[key] = (row.Text, row.At);
                _loaded = true;
                if (_messages.Count > 0)
                    _log($"记起了 {_messages.Count} 条自己发过的消息（引用回复识别用）");
            }
            catch (Exception ex)
            {
                // A later lookup can retry; never expose partially initialized legacy identities.
                _messages.Clear();
                _log("读取自己发过的消息台账失败（当空表继续）：" + ex.Message);
            }
        }
    }

    /// <summary>裸 id 无法证明身份：保留旧签名，始终 fail-closed。</summary>
    public bool TryGet(long messageId, out (string Text, DateTimeOffset At) entry)
    {
        entry = default;
        return false; // Bare ids are ambiguous across platform/account/conversation.
    }

    public bool TryGet(string sourceKey, long messageId, out (string Text, DateTimeOffset At) entry)
    {
        entry = default;
        return messageId > 0 && ConversationIdCodec.TryParse(sourceKey, out var conversation)
            && TryGet(new MessageRef(conversation, messageId.ToString(CultureInfo.InvariantCulture)), out entry);
    }

    public bool TryGet(MessageRef messageRef, out (string Text, DateTimeOffset At) entry)
    {
        lock (_gate)
        {
            EnsureLoaded();
            entry = default;
            return TryCanonical(messageRef, out var key) && _messages.TryGetValue(key, out entry);
        }
    }

    /// <summary>
    /// 旧无 scope 签名仅保留编译兼容，不记录消息；新发送路径必须显式提供会话。
    /// </summary>
    public void Remember(SendResult sent, string text)
    { } // Compatibility only: unknown scope must not enter the identity ledger.

    public void Remember(string sourceKey, SendResult sent, string text)
    {
        if (ConversationIdCodec.TryParse(sourceKey, out var conversation)) Remember(conversation, sent, text);
    }

    public void Remember(ConversationId conversation, SendResult sent, string text)
    {
        if (!sent.Ok || sent.MessageId <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Remember(new MessageRef(conversation, sent.MessageId.ToString(CultureInfo.InvariantCulture)), text, EngineClock.Now);
    }

    public void Remember(MessageRef messageRef, string text, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(text) || !TryCanonical(messageRef, out var key)) return;
        lock (_gate)
        {
            EnsureLoaded();
            _store.Upsert(key, text, at);
            _messages[key] = (text, at);
            if (_messages.Count > _store.MaxEntries)
            {
                foreach (var stale in _messages.OrderBy(kv => kv.Value.At)
                    .Take(_messages.Count - _store.MaxEntries).ToList()) _messages.Remove(stale.Key);
            }
            _store.PruneTo(_store.MaxEntries);
        }
    }

    private static bool TryCanonical(MessageRef messageRef, out MessageRef key)
    {
        key = null!;
        if (messageRef?.Conversation is not { } conversation || !Enum.IsDefined(conversation.Kind)
            || string.IsNullOrWhiteSpace(messageRef.NativeMessageId)
            || string.IsNullOrWhiteSpace(conversation.PlatformId) || string.IsNullOrWhiteSpace(conversation.AccountScope)
            || string.IsNullOrWhiteSpace(conversation.NativeTargetId)) return false;
        if (!ConversationIdCodec.TryParse(ConversationIdCodec.EncodeStructured(conversation), out var canonical)) return false;
        key = new MessageRef(canonical, messageRef.NativeMessageId);
        return true;
    }
}
