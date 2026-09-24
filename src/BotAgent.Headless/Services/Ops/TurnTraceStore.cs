using System;
using System.Collections.Generic;
using System.Linq;
using BotAgent.Domain.Ops;

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
    private long _seq;

    public TurnTraceStore(Func<DateTimeOffset>? clock = null) => _now = clock ?? (() => Clock.Now);

    /// <summary>新的一轮开始（同一会话重复调用 = 上一轮没收尾，按新的一轮覆盖）。</summary>
    public void Begin(string conversationKey)
    {
        lock (_gate)
        {
            _seq++;
            _active[conversationKey ?? string.Empty] = new Turn("t" + _seq, _now());
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
    public TurnTrace? Complete(string conversationKey, string outcome)
    {
        lock (_gate)
        {
            if (!_active.Remove(conversationKey ?? string.Empty, out var turn))
            {
                return null;
            }

            var trace = new TurnTrace(
                turn.RunId,
                conversationKey ?? string.Empty,
                turn.StartedAt,
                outcome,
                (int)Math.Max(0, (_now() - turn.StartedAt).TotalMilliseconds),
                turn.Nodes.ToArray());

            _done.Add(trace);
            if (_done.Count > Capacity)
            {
                _done.RemoveRange(0, _done.Count - Capacity);
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
    }
}
