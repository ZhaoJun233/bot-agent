using System;
using System.Collections.Generic;
using System.Linq;
using BotAgent.Domain.Ops;
using BotAgent.Engine.Runtime;

namespace BotAgent.Services.Ops;

/// <summary>
/// 决策轨迹的**内存台账**（批次 C）：一轮一条，只留最近 <see cref="Capacity" /> 条（§11 第 7 条的推荐口径）。
///
/// 三条性质：
///   · **不落库**：轨迹是观测数据（重启即空），事实数据（会话、审批单、设置）各有出处；
///   · **有界**：超过容量丢最旧的；
///   · **只有形状**：记的是 id / 枚举 / 状态码 / 时长 / 计数 —— 正文与参数值不进来看（见 <see cref="TurnNode" />）。
///
/// 时序由台账自己算（<c>Begin</c> 与每次 <c>Node</c> 之间的间隔），所以调用点只要一行，不必各自计时。
/// </summary>
public sealed class TurnTraceStore
{
    /// <summary>最多留多少条已完成的轨迹。</summary>
    public const int Capacity = 50;

    private readonly object _gate = new();
    private readonly Dictionary<string, Turn> _active = new(StringComparer.Ordinal);
    private readonly List<TurnTrace> _done = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly ITraceArchive? _archive;
    private long _seq;
    private long _startedTotal;
    private long _completedTotal;
    private long _promptTokensTotal;
    private long _completionTokensTotal;

    public TurnTraceStore(Func<DateTimeOffset>? clock = null, ITraceArchive? archive = null)
    {
        // Deterministic zero clock by default: production wiring supplies the host clock explicitly.
        _now = clock ?? (() => DateTimeOffset.UnixEpoch);
        _archive = archive;
    }

    /// <summary>新的一轮开始（同一会话重复调用 = 上一轮没收尾，按新的一轮覆盖）。</summary>
    public void Begin(string conversationKey)
    {
        lock (_gate)
        {
            _seq++;
            _startedTotal++;
            _active[conversationKey ?? string.Empty] = new Turn("t" + _seq, _now());
        }
    }

    /// <summary>为当前轮记录 token 用量与故障转移跳数（可在一轮中多次调用累加）。</summary>
    public void RecordTokens(string conversationKey, int promptTokens, int completionTokens, int fallbackHops = 0)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(conversationKey ?? string.Empty, out var turn))
            {
                turn.PromptTokens += Math.Max(0, promptTokens);
                turn.CompletionTokens += Math.Max(0, completionTokens);
                turn.FallbackHops = Math.Max(turn.FallbackHops, Math.Max(0, fallbackHops));
            }
        }
    }

    /// <summary>记一个节点（时长 = 距上一个节点的间隔）。没有在跑的一轮就忽略。</summary>
    public void Node(
        string conversationKey,
        TurnNodeKind kind,
        string status,
        string? toolId = null,
        string? reasonCode = null,
        int? count = null)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(conversationKey ?? string.Empty, out var turn))
            {
                return;
            }

            var now = _now();
            turn.Nodes.Add(new TurnNode(kind, status, (int)Math.Max(0, (now - turn.LastTick).TotalMilliseconds), toolId, reasonCode, count));
            turn.LastTick = now;
        }
    }

    /// <summary>收尾并归档。没有在跑的一轮就什么也不做（返回 null）。</summary>
    public TurnTrace? Complete(string conversationKey, string outcome, int? promptTokens = null, int? completionTokens = null, int? fallbackHops = null)
    {
        lock (_gate)
        {
            if (!_active.Remove(conversationKey ?? string.Empty, out var turn))
            {
                return null;
            }

            var pt = promptTokens.HasValue ? Math.Max(0, promptTokens.Value) : turn.PromptTokens;
            var ct = completionTokens.HasValue ? Math.Max(0, completionTokens.Value) : turn.CompletionTokens;
            var hops = fallbackHops.HasValue ? Math.Max(0, fallbackHops.Value) : turn.FallbackHops;

            var trace = new TurnTrace(
                turn.RunId,
                conversationKey ?? string.Empty,
                turn.StartedAt,
                outcome,
                (int)Math.Max(0, (_now() - turn.StartedAt).TotalMilliseconds),
                turn.Nodes.ToArray(),
                pt,
                ct,
                hops);

            _completedTotal++;
            _promptTokensTotal += pt;
            _completionTokensTotal += ct;
            _done.Add(trace);
            if (_done.Count > Capacity)
            {
                _done.RemoveRange(0, _done.Count - Capacity);
            }

            if (_archive is not null && (trace.TotalMs >= 5000 || !string.Equals(outcome, "done", StringComparison.OrdinalIgnoreCase)
                || trace.FallbackHops > 0
                || trace.Nodes.Any(n => n.Status is "failed" or "error" or "blocked" or "partial" or "fallback")))
            {
                _archive.Append(trace);
            }
            return trace;
        }
    }

    /// <summary>最近若干轮（新的在前）。</summary>
    public IReadOnlyList<TurnTrace> Recent(int limit = 20)
    {
        lock (_gate)
        {
            return _done.AsEnumerable().Reverse().Take(Math.Max(0, limit)).ToArray();
        }
    }

    public ITraceArchive? Archive => _archive;
    /// <summary>进程启动以来开始处理的决策轮次数，用于请求总量 metrics 计数器。</summary>
    public long StartedTotal
    {
        get
        {
            lock (_gate)
            {
                return _startedTotal;
            }
        }
    }


    /// <summary>进程启动以来完成的轮次数，用于低基数 metrics 计数器。</summary>
    public long CompletedTotal
    {
        get
        {
            lock (_gate)
            {
                return _completedTotal;
            }
        }
    }

    /// <summary>进程启动以来累计消耗的 Prompt Tokens。</summary>
    public long PromptTokensTotal
    {
        get
        {
            lock (_gate)
            {
                return _promptTokensTotal;
            }
        }
    }

    /// <summary>进程启动以来累计生成的 Completion Tokens。</summary>
    public long CompletionTokensTotal
    {
        get
        {
            lock (_gate)
            {
                return _completionTokensTotal;
            }
        }
    }

    public int DoneCount
    {
        get
        {
            lock (_gate)
            {
                return _done.Count;
            }
        }
    }

    /// <summary>正在跑的一轮数（面板上的“进行中”）。</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active.Count;
            }
        }
    }

    /// <summary>会话被删 → 它的轨迹一起清掉。</summary>
    public void Forget(string conversationKey)
    {
        lock (_gate)
        {
            _active.Remove(conversationKey ?? string.Empty);
        }
    }

    private sealed class Turn(string runId, DateTimeOffset startedAt)
    {
        public string RunId { get; } = runId;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset LastTick { get; set; } = startedAt;

        public List<TurnNode> Nodes { get; } = new();

        public int PromptTokens { get; set; }

        public int CompletionTokens { get; set; }

        public int FallbackHops { get; set; }
    }
}
