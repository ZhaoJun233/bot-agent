using BotAgent.Engine.Runtime;
using System.Collections.Generic;

namespace BotAgent.Services.Reply;

/// <summary>
/// 单租户待回复队列。普通观望轮次在达到容量后合并，带明确交互语义的轮次不被无条件丢弃。
/// </summary>
public sealed class TenantReplyQueue
{
    public const int DefaultCapacity = 5;
    public const int HardCapacity = 256;
    private readonly object _gate = new();
    private readonly Queue<PendingReply> _items = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _items.Count == 0;
            }
        }
    }

    public PendingReplyOffer Enqueue(PendingReply item, int capacity)
    {
        if (capacity < 1 || capacity > HardCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        lock (_gate)
        {
            if (_items.Count < capacity)
            {
                _items.Enqueue(item);
                return PendingReplyOffer.Accepted;
            }

            // 新的普通观望轮次只需保留最新一次评估。会话消息已经落盘，
            // 因此合并队列项不会丢掉上下文；直接交互和明确动作仍走保序路径。
            if (!item.MustPreserve)
            {
                var values = _items.ToArray();
                var replaceAt = Array.FindLastIndex(values, value => !value.MustPreserve && SameSource(value.Source, item.Source));
                if (replaceAt >= 0)
                {
                    values[replaceAt] = item;
                    _items.Clear();
                    foreach (var value in values)
                    {
                        _items.Enqueue(value);
                    }

                    return PendingReplyOffer.CoalescedOld;
                }

                return values.Any(value => SameSource(value.Source, item.Source))
                    ? PendingReplyOffer.CoalescedIncoming : PendingReplyOffer.RejectedCapacity;
            }

            // 需要保序的事件不能无条件丢弃。优先移除最早的普通观望轮次，
            // 如果队列全是保序事件，则允许短暂超过容量，由调用方显式记录。
            var retained = _items.ToArray();
            var removeAt = Array.FindIndex(retained, value => !value.MustPreserve && SameSource(value.Source, item.Source));
            if (removeAt >= 0)
            {
                _items.Clear();
                for (var i = 0; i < retained.Length; i++)
                {
                    if (i != removeAt)
                    {
                        _items.Enqueue(retained[i]);
                    }
                }
            }

            if (_items.Count >= HardCapacity) return PendingReplyOffer.RejectedCapacity;
            _items.Enqueue(item);
            return removeAt >= 0
                ? PendingReplyOffer.AcceptedAfterCoalescing
                : PendingReplyOffer.PreservedOverflow;
        }
    }

    public bool TryDequeue(out PendingReply item)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                item = default;
                return false;
            }

            item = _items.Dequeue();
            return true;
        }
    }

    private static bool SameSource(BotAgent.Services.Qq.PlatformSourceLease? left, BotAgent.Services.Qq.PlatformSourceLease? right)
        => left is null ? right is null : right is not null && left.Origin.Driver == right.Origin.Driver
            && left.Lifetime == right.Lifetime;
}

public readonly record struct PendingReply(
    long? TriggerMessageId,
    bool Proactive,
    bool MustPreserve,
    BotAgent.Services.Qq.PlatformSourceLease? Source = null);

public enum PendingReplyOffer
{
    Accepted,
    CoalescedOld,
    CoalescedIncoming,
    RejectedCapacity,
    AcceptedAfterCoalescing,
    PreservedOverflow
}
