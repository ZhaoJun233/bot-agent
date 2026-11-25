using BotAgent.Engine.Runtime;
using BotAgent.Services.Participation;

namespace BotAgent.Services.Participation;

/// <summary>
/// 会话参与状态台账的用例层（V3 §7）：状态机被喂事件、把状态变化写进日志；
/// 只记录观测，不参与回复调度；回复频率由 ReplyPacingCoordinator 决定。
/// 观测是**旁路**：坏了也不能影响回复（见 <see cref="Observe" /> 的 catch）。
/// </summary>
public sealed class ParticipationUseCase
{
    private readonly Action<string> _log;
    private readonly ParticipationRegistry _registry = new(new ParticipationPolicy());

    public ParticipationUseCase(ISettingsBox box, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(box);
        _log = log;
    }

    /// <summary>台账里有多少个会话（日志与面板用）。</summary>
    public int Count => _registry.Count;

    /// <summary>刷新只读台账的固定观测策略，不提供回复调度限制。</summary>
    public void Rebuild()
    {
        var policy = new ParticipationPolicy();
        _registry.UpdatePolicy(policy);

        _log($"[参与] 已刷新只读观测台账（{_registry.Count} 个会话，不参与回复调度）");
    }

    /// <summary>
    /// 喂给参与状态机一个事件并返回它的决策。
    /// 结论只用于状态展示，不影响入站、工具或回复终态的调度。
    /// 只写结构化信息（状态 / 原因码 / 会话 key），不写任何正文；异常绝不影响回复。
    /// </summary>
    public ParticipationDecision Observe(string sourceKey, ParticipationEvent evt)
    {
        try
        {
            var machine = _registry.For(sourceKey);
            var before = machine.State;
            var decision = machine.OnEvent(evt, EngineClock.Now);
            if (before != decision.State)
            {
                _log($"[参与] {sourceKey} {before} → {decision.State}（{decision.ReasonCode}，"
                     + $"台账 {_registry.Count} 个会话）");
            }

            return decision;
        }
        catch (Exception ex)
        {
            // 观测是旁路，坏了也不能影响回复
            _log("参与状态记录失败（不影响回复）: " + ex.Message);
            return new ParticipationDecision(false, ParticipationState.Observing, "observe_error");
        }
    }

    /// <summary>会话没了 / 被移出白名单 → 台账里的痕迹一起清掉（内存台账要有界）。</summary>
    public void Forget(string sourceKey) => _registry.Forget(sourceKey);

    /// <summary>
    /// 只读快照（面板「参与状态」用）：每个会话现在处在什么状态、最近一次为什么转移。
    /// **只给结构化字段**；会话 key 的脱敏由调用方（显示层）做。
    /// </summary>
    public (string Policy, IReadOnlyList<(string Key, string State, string Reason, string Counters, string Age)> Rows) Snapshot()
    {
        var policy = _registry.Policy;
        var policyText = $"连续 ≤{policy.MaxConsecutiveReplies} / 冷却 {policy.CooldownSeconds}s"
                         + $" / 试探 ≤{policy.ProbingMaxReplies} / 活性命 {policy.MaxActiveLifetimeSeconds}s"
                         + $" / 退场 {policy.MaxExitingLifetimeSeconds}s / v{policy.PolicyVersion}";

        var now = EngineClock.Now;
        var rows = _registry.Snapshot()
            .Select(s => (
                Key: s.SourceKey,
                State: s.State.ToString(),
                Reason: s.ReasonCode,
                Counters: $"连续 {s.ConsecutiveReplies} / 试探 {s.ProbingReplies} / 失败 {s.Failures}",
                Age: $"{Math.Max(0, (now - s.LastTransitionAt).TotalSeconds):F0}s前"))
            .ToList();

        return (policyText, rows);
    }
}
