using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Qq;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    /// <summary>限流期间被挡下的触发：等这一轮生成结束再补一次评估（见 RunReplyAsync 的 finally）。</summary>
    /// <param name="TriggerId">要拿来当触发的那条消息 id（0 = 事件没有消息 id，例如戳一戳）。</param>
    /// <param name="Direct">这一窗口里有没有“直接跟机器人说话”的（@ 你 / 引用你的话）——那种必须被回答。</param>
    private readonly object _deferredGate = new();
    // Detached synchronous flushes retain ownership until enqueue admission finishes.
    private readonly Dictionary<object, BotConversation> _flushingDeferred = new();
    // Membership/offer/retirement share one short, no-callback critical section.
    private readonly object _pendingGate = new();
    private sealed class ReplyEpoch { public object Token = new(); }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BotConversation, ReplyEpoch> _replyEpochs = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (BotConversation Conversation, object Epoch)> _activeConversations = new();
    private object CaptureReplyEpoch(BotConversation conversation) => Volatile.Read(ref _replyEpochs.GetValue(conversation, _ => new ReplyEpoch()).Token);
    private bool IsReplyEpochCurrent(BotConversation conversation, object epoch) => ReferenceEquals(CaptureReplyEpoch(conversation), epoch);
    private void RetireReplyEpoch(BotConversation conversation) => Interlocked.Exchange(ref _replyEpochs.GetValue(conversation, _ => new ReplyEpoch()).Token, new object());
    private readonly record struct DeferredTrigger(long TriggerId, bool Direct, PlatformSourceLease? Source, BotConversation Conversation, object Epoch);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Conversation, BotAgent.Domain.Configuration.DriverKey? Driver, CancellationToken Lifetime), DeferredTrigger> _deferredTriggers = new();
    /// <summary>
    /// “要不要现在回” + “不能回就记下来”——**统一入口**。
    /// 以前各处写的是 <c>if (AllowReply(...)) EnqueueReply(...)</c>：被冷却拦下的触发就永久丢了，
    /// 群里连珠炮时表现为“怎么都不理我”（管理员 2026-09-16 反馈）。
    /// 现在被拦下会记一笔，本轮生成结束后补一次评估；@ 你 / 引用你的话那种“直接跟你说话”的，
    /// 在补评估时会带上 Direct 标记（阈值不再把它压成沉默）。
    /// </summary>
    public void RequestReply(
        BotConversation conversation,
        long? triggerMessageId,
        bool proactive = false,
        bool catchUp = false,
        bool directInWindow = false)
        => RequestReplyInEpoch(conversation, triggerMessageId, proactive, catchUp, directInWindow, CaptureReplyEpoch(conversation));

    private void RequestReplyInEpoch(BotConversation conversation, long? triggerMessageId, bool proactive, bool catchUp, bool directInWindow, object epoch)
    {
        if (!IsReplyEpochCurrent(conversation, epoch)) return;
        var source = PlatformSourceExecution.Current ?? (_source as IPlatformSourceRouter)?.CaptureForConversation(conversation.SourceKey);
        if (_source is IPlatformSourceRouter && (source is null || !source.IsCurrent)) return;
        using var sourceScope = PlatformSourceExecution.Enter(source);
        if (!_settings.AiModeEnabled)
        {
            return;
        }

        if (catchUp || AllowReply(conversation))
        {
            if (catchUp)
            {
                // 补评估是“这一轮的尾巴”，也要刷新冷却时间，否则下一句又立刻放行、连珠炮变刷屏
                _replyCooldown[conversation.SourceKey] = EngineClock.Now;
            }

            var mustPreserve = catchUp || directInWindow;
            EnqueueReplyInEpoch(conversation, triggerMessageId, proactive, mustPreserve, epoch);
            return;
        }

        // 被挡下：记一笔待补（窗口里出现过“直接跟你说话”的，优先拿它当补评估的触发）
        var key = conversation.SourceKey;
        lock (_deferredGate)
        {
            if (!IsReplyEpochCurrent(conversation, epoch)) return;
            foreach (var retired in _deferredTriggers.Where(item => item.Value.Source is { IsCurrent: false }).Select(item => item.Key).ToArray())
                _deferredTriggers.TryRemove(retired, out _);
            var deferredKey = (key, source?.Origin.Driver, source?.Lifetime ?? default);
            if (!_deferredTriggers.ContainsKey(deferredKey) && _deferredTriggers.Keys.Count(item => item.Conversation == key) >= TenantReplyQueue.HardCapacity)
            {
                _hooks.Log("queue_capacity_rejected");
                return;
            }
        _deferredTriggers.AddOrUpdate(
            (key, source?.Origin.Driver, source?.Lifetime ?? default),
            _ => new DeferredTrigger(triggerMessageId ?? 0, directInWindow, source, conversation, epoch),
            (_, old) =>
            {
                var direct = old.Direct || directInWindow;
                // 有直接跟你说话的 → 用那条当触发（那是最该回答的一条）；否则用最新的
                var id = directInWindow || old.TriggerId == 0 ? (triggerMessageId ?? old.TriggerId) : old.TriggerId;
                return new DeferredTrigger(id, direct, directInWindow || old.TriggerId == 0 ? source : old.Source, conversation, epoch);
            });
        }
        _hooks.Log($"等这一轮说完再评估（被限流挡下的新消息已记下）: {conversation.Name}");
    }
    /// <summary>把一条待回复请求排入该会话的有界 FIFO 链，并唤醒调度器。</summary>
    private void EnqueueReply(
        BotConversation conversation,
        long? triggerMessageId,
        bool proactive = false,
        bool mustPreserve = false)
        => EnqueueReplyInEpoch(conversation, triggerMessageId, proactive, mustPreserve, CaptureReplyEpoch(conversation));

    private void EnqueueReplyInEpoch(BotConversation conversation, long? triggerMessageId, bool proactive, bool mustPreserve, object epoch)
    {
        if (triggerMessageId is not null)
        {
            LastActiveRequestTime = EngineClock.LocalDateTime; // 主动请求刷新活跃时间
        }

        var source = PlatformSourceExecution.Current ?? (_source as IPlatformSourceRouter)?.CaptureForConversation(conversation.SourceKey);
        if (_source is IPlatformSourceRouter && (source is null || !source.IsCurrent)) return;
        PendingReplyOffer offer;
        lock (_pendingGate)
        {
            if (!IsReplyEpochCurrent(conversation, epoch)) return;
            _pendingConversations[conversation.SourceKey] = conversation;
            var queue = _pendingReplies.GetOrAdd(conversation.SourceKey, _ => new TenantReplyQueue());
            offer = queue.Enqueue(new PendingReply(triggerMessageId, proactive, mustPreserve, source), TenantReplyQueue.DefaultCapacity);
        }
        if (offer == PendingReplyOffer.RejectedCapacity)
        {
            _hooks.Log("queue_capacity_rejected");
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: "queue_capacity_rejected");
            return;
        }
        _ = DrainReplyQueueAsync();
    }

    /// <summary>
    /// 调度器：从各会话的 FIFO 链头取请求交给并发 worker。
    ///   • 同一会话同时只能有一个在途请求（保证回复顺序与引用正确）
    ///   • 不同会话并行执行（一个慢请求不再阻塞其它群）
    ///   • 全局并发上限由 _replyGate 控制
    /// </summary>
    private async Task DrainReplyQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _replyWorkerRunning, 1, 0) == 1)
        {
            return; // 已有调度器在跑
        }

        try
        {
            while (true)
            {
                var fired = false;

                foreach (var (key, queue) in _pendingReplies)
                {
                    if (!TryClaimPending(key, queue, out var conversation, out var pending)) continue;

                    fired = true;
                    _ = RunReplyAsync(conversation, key, pending.TriggerMessageId, pending.Proactive, pending.Source);
                }

                if (fired)
                {
                    continue; // 可能还有别的会话可跑
                }

                // 本轮一无所获：要么全在途（等释放），要么真的没活了
                var waiting = _pendingReplies.Any(kv => !kv.Value.IsEmpty && _inFlight.ContainsKey(kv.Key));
                if (!waiting)
                {
                    CleanupPending();
                    break;
                }

                await EngineClock.Delay(150);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _replyWorkerRunning, 0);
            if (_pendingReplies.Any(kv => !kv.Value.IsEmpty))
            {
                _ = DrainReplyQueueAsync();
            }
        }
    }
    // A scheduler enumeration may still hold an intentionally dropped queue; claim only its current membership.
    private bool TryClaimPending(string key, TenantReplyQueue expectedQueue, out BotConversation conversation, out PendingReply pending)
    {
        lock (_pendingGate)
        {
            conversation = null!;
            pending = default;
            if (!_pendingReplies.TryGetValue(key, out var current) || !ReferenceEquals(current, expectedQueue)
                || !_pendingConversations.TryGetValue(key, out conversation!) || _inFlight.ContainsKey(key)
                || current.IsEmpty || !_inFlight.TryAdd(key, 0)) return false;
            if (current.TryDequeue(out pending))
            {
                _activeConversations[key] = (conversation, CaptureReplyEpoch(conversation));
                return true;
            }
            _inFlight.TryRemove(key, out _);
            return false;
        }
    }

    /// <summary>清掉已排空且不在途的会话条目，避免字典无限增长。</summary>
    private void CleanupPending()
    {
        lock (_pendingGate)
        foreach (var key in _pendingReplies.Keys.ToList())
        {
            if (_inFlight.ContainsKey(key))
            {
                continue;
            }

            if (_pendingReplies.TryGetValue(key, out var queue) && queue.IsEmpty)
            {
                _pendingReplies.TryRemove(key, out _);
                _pendingConversations.TryRemove(key, out _);
            }
        }
    }
    /// <summary>执行一次回复（受全局并发闸门限制）。</summary>
    private async Task RunReplyAsync(BotConversation conversation, string sourceKey, long? triggerMessageId, bool proactive = false,
        PlatformSourceLease? source = null)
    {
        using var sourceScope = PlatformSourceExecution.Enter(source);
        _traces.Begin(sourceKey);

        // Reserve includes queued waiters in the retired generation's lifetime.
        var gate = _replyGate.Reserve();
        try
        {
            await gate.WaitAsync(source?.Lifetime ?? default);
            if (_source is IPlatformSourceRouter && (source is null || !source.IsCurrent)) return;
            conversation.HasPendingReply = false;
            await GenerateReplyAsync(conversation, triggerMessageId, proactive, source?.Lifetime ?? default);
        }
        catch (Exception ex)
        {
            _hooks.Log("回复流程异常: " + ex.Message);
        }
        finally
        {
            gate.Dispose();

            LastActiveRequestTime = EngineClock.LocalDateTime;
            _traces.Complete(sourceKey, "done");

            // 限流期间被挡下的消息**不能就这么算了**（管理员反馈“连续多人对话不回应”的根因）：
            // 以前 AllowReply 一返回 false，那条触发就彻底没人评估了 —— 群里连着说话时，
            // 只要第一轮生成完是沉默（或慢），后面那几条就要等到 60 秒静默兜底才有下一次机会。
            // 现在：这一轮说完，若中间又有消息被挡下，就**补一次评估**（每轮生成只补一次，不会打转）。
            FlushDeferred(conversation);
            lock (_pendingGate)
            {
                if (_activeConversations.TryGetValue(sourceKey, out var active) && ReferenceEquals(active.Conversation, conversation))
                    _activeConversations.TryRemove(sourceKey, out _);
                _inFlight.TryRemove(sourceKey, out _);
            }

            _ = DrainReplyQueueAsync(); // 唤醒调度器处理该会话的后续项
        }
    }
    private void FlushDeferred(BotConversation conversation)
    {
        foreach (var deferredKey in _deferredTriggers.Keys.Where(key => key.Conversation == conversation.SourceKey).ToArray())
        {
            DeferredTrigger deferred;
            var flushOwner = new object();
            lock (_deferredGate)
            {
                if (!_deferredTriggers.TryGetValue(deferredKey, out deferred)
                    || !ReferenceEquals(deferred.Conversation, conversation)
                    || !IsReplyEpochCurrent(conversation, deferred.Epoch)) continue;
                _flushingDeferred.Add(flushOwner, conversation);
                _deferredTriggers.TryRemove(deferredKey, out _);
            }
            try
            {
            if (_source is IPlatformSourceRouter && (deferred.Source is null || !deferred.Source.IsCurrent)) continue;
            using var deferredScope = PlatformSourceExecution.Enter(deferred.Source);
            if (deferred.TriggerId > 0)
            {
                _hooks.Log($"补一次评估：拿被挡下的触发 #{deferred.TriggerId} 再问一次"
                    + (deferred.Direct ? "（窗口里有 @ 机器人 / 引用它的话）" : string.Empty));
            }
            RequestReplyInEpoch(conversation, deferred.TriggerId > 0 ? deferred.TriggerId : null,
                proactive: false, catchUp: true, directInWindow: deferred.Direct, deferred.Epoch);
            }
            finally { lock (_deferredGate) _flushingDeferred.Remove(flushOwner); }
        }
    }
}
