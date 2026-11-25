using System.Collections.Concurrent;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Messaging;
using BotAgent.Services.Conversations;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Agent;

/// <summary>
/// 聊天 <c>//</c> 与自然触发任务接入 <see cref="Engine.Runtime.AgentTaskRuntime"/> 的纯内存执行桥（ADR-0003）。
///
/// 设计约束：
///   • 提示词、消息正文、入站信封与回调仅驻留进程内存；任务入终态或重启即销毁，绝不写入 <c>agent_tasks</c>；
///   • 用 <see cref="LifecycleState"/> 双原子状态确保与 <see cref="AgentTaskRuntime.Changed"/> 在并发超时/取消/拒绝下绝不双重释放 <see cref="ServerAgentAdmission"/>。
/// </summary>
internal sealed class ChatTaskRuntimeBridge
{
    internal sealed class Entry(
        AgentTask task,
        ServerAgentAdmission admission,
        BotConversation conversation,
        PlatformInboundEnvelope? memoryEnvelope,
        bool isGroup)
    {
        private int _lifecycle; // 0 = pending, 1 = entered executor, 2 = aborted before execution, 3 = finished

        public AgentTask Task { get; } = task;
        public ServerAgentAdmission Admission { get; } = admission;
        public BotConversation Conversation { get; } = conversation;
        public PlatformInboundEnvelope? MemoryEnvelope { get; } = memoryEnvelope;
        public bool IsGroup { get; } = isGroup;
        public string? ApprovalRequestId { get; set; }
        public bool WasWaitingApproval { get; set; }
        public TaskCompletionSource AckGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryEnterExecutor() => Interlocked.CompareExchange(ref _lifecycle, 1, 0) == 0;
        public bool TryAbortBeforeStart() => Interlocked.CompareExchange(ref _lifecycle, 2, 0) == 0;
        public bool TryFinishExecutor() => Interlocked.CompareExchange(ref _lifecycle, 3, 1) == 1;
        public bool HasEnteredExecutor => Volatile.Read(ref _lifecycle) is 1 or 3;
        public bool IsAborted => Volatile.Read(ref _lifecycle) == 2;
    }

    internal readonly record struct ApprovalOrigin(
        string TaskId,
        string ConversationKey,
        PlatformSourceLease? SourceLease,
        bool IsGroup);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ApprovalOrigin> _approvals = new(StringComparer.Ordinal);

    public bool TryRegister(Entry entry) => _entries.TryAdd(entry.Task.Id, entry);

    public bool TryGet(string taskId, out Entry entry) => _entries.TryGetValue(taskId, out entry!);

    public bool TryRemove(string taskId, out Entry entry)
    {
        var removed = _entries.TryRemove(taskId, out entry!);
        if (removed && entry.ApprovalRequestId is { Length: > 0 } reqId)
            _approvals.TryRemove(reqId, out _);
        return removed;
    }

    public bool TryRemoveExact(string taskId, Entry expected)
    {
        var removed = _entries.TryRemove(new KeyValuePair<string, Entry>(taskId, expected));
        if (removed && expected.ApprovalRequestId is { Length: > 0 } reqId)
            _approvals.TryRemove(reqId, out _);
        return removed;
    }

    public bool TryRegisterApproval(Entry entry, string requestId, ApprovalOrigin origin)
    {
        if (entry.IsAborted || !_entries.TryGetValue(entry.Task.Id, out var current) || !ReferenceEquals(current, entry))
            return false;
        entry.ApprovalRequestId = requestId;
        _approvals[requestId] = origin;
        if (entry.IsAborted || !_entries.TryGetValue(entry.Task.Id, out current) || !ReferenceEquals(current, entry))
        {
            _approvals.TryRemove(requestId, out _);
            return false;
        }
        return true;
    }

    public bool TryGetApprovalOrigin(string requestId, out ApprovalOrigin origin)
        => _approvals.TryGetValue(requestId, out origin);

    public bool TryRemoveApproval(string requestId) => _approvals.TryRemove(requestId, out _);
}
