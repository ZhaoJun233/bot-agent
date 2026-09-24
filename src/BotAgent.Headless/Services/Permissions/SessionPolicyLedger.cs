using System;
using System.Collections.Generic;
using System.Linq;
using BotAgent.Domain.Permissions;

namespace BotAgent.Services.Permissions;

/// <summary>
/// 会话级权限元数据的**内存台账**（批次 B）：会话 key → <see cref="SessionPolicyStamp" />。
///
/// 三条性质：
///   · **有界**：超过 <see cref="MaxSessions" /> 就丢最旧的那几条（内存台账不能长成漏洞）；
///   · **不落库**：它是观测数据不是事实数据 —— 事实（设置、审批单）各自有出处，重启丢戳不影响任何判定；
///   · **无副作用**：<see cref="Stamp" /> 只写这张表，不改预算、不改台账、不发消息（批次 B 的“零行为变化”）。
/// </summary>
public sealed class SessionPolicyLedger
{
    /// <summary>最多记多少条会话（超出丢最旧的）。</summary>
    public const int MaxSessions = 500;

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionPolicyStamp> _byKey = new(StringComparer.Ordinal);
    private int _rebuilt;

    /// <summary>
    /// 记一条戳。返回 true = 这条会话**原先的戳与现在对不上**（策略换过 → 已按新策略重建映射）。
    /// 第一次记不作重建（没有旧戳可作废）。
    /// </summary>
    public bool Stamp(SessionPolicyStamp stamp)
    {
        lock (_gate)
        {
                var rebuilt = _byKey.TryGetValue(stamp.ConversationKey, out var previous)
                          && previous.IsStale(stamp.PolicyFingerprint);
            _byKey[stamp.ConversationKey] = stamp;
            if (rebuilt)
            {
                _rebuilt++;
            }

            Trim();
            return rebuilt;
        }
    }

    /// <summary>读一条（没有就是没记过 —— 调用方据此判断“这条会话还没在当前策略下走过”。）</summary>
    public bool TryGet(string? conversationKey, out SessionPolicyStamp stamp)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(conversationKey ?? string.Empty, out var found))
            {
                stamp = found;
                return true;
            }
        }

        stamp = null!;
        return false;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byKey.Count;
            }
        }
    }

    /// <summary>累计重建过多少次（策略换过几次就把几条会话的戳刷新了）。</summary>
    public int RebuiltCount => _rebuilt;

    /// <summary>还挂在**旧**策略上的会话数（面板看这个数就知道“策略变更有没有落地”）。</summary>
    public int StaleCount(string currentFingerprint)
    {
        lock (_gate)
        {
            return _byKey.Values.Count(s => s.IsStale(currentFingerprint));
        }
    }

    /// <summary>快照（只读；面板按通道统计用）。</summary>
    public IReadOnlyList<SessionPolicyStamp> Snapshot()
    {
        lock (_gate)
        {
            return _byKey.Values.OrderBy(s => s.ConversationKey, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>会话被删 / 移出白名单 → 戳一起清掉（与工具预算的 Forget 同一个调用点）。</summary>
    public void Forget(string? conversationKey)
    {
        lock (_gate)
        {
            _byKey.Remove(conversationKey ?? string.Empty);
        }
    }

    private void Trim()
    {
        if (_byKey.Count <= MaxSessions)
        {
            return;
        }

        foreach (var key in _byKey
                     .OrderBy(kv => kv.Value.StampedAt)
                     .Take(_byKey.Count - MaxSessions)
                     .Select(kv => kv.Key)
                     .ToArray())
        {
            _byKey.Remove(key);
        }
    }
}
