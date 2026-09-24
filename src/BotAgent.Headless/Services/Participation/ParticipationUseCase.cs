using BotAgent.Services.Participation;

namespace BotAgent.Services.Participation;

/// <summary>
/// 会话参与状态台账的用例层（V3 §7）：状态机被喂事件、把状态变化写进日志；
/// **要不要回复默认仍由原有逻辑决定**（白名单 / 总开关 / 自评阈值 / @ 破例）——
/// gating 默认关，这是 V3 §5.3 的兼容红线。只有打开 <c>EnableParticipationGating</c> 之后，
/// 入站那条锚点的结论才会真的被用来拦（见 <see cref="ParticipationGate" />，只收不放）。
///
/// 上限一律走 <see cref="ParticipationPolicy.Clamped" />：设置里的值只是"愿望"（面板填 999 也只会被钳到上限）。
/// 观测是**旁路**：坏了也不能影响回复（见 <see cref="Observe" /> 的 catch）。
/// </summary>
public sealed class ParticipationUseCase
{
    private readonly SettingsBox _box;
    private readonly Action<string> _log;
    private readonly ParticipationRegistry _registry = new(new ParticipationPolicy());

    public ParticipationUseCase(SettingsBox box, Action<string> log)
    {
        _box = box;
        _log = log;
    }

    private AppSettings _settings => _box.Current;

    /// <summary>台账里有多少个会话（日志与面板用）。</summary>
    public int Count => _registry.Count;

    /// <summary>
    /// 从设置里取参与状态机的上限（V3 §7.3：**上下限必须由服务端校验**）。
    /// </summary>
    private ParticipationPolicy BuildPolicy()
        => new ParticipationPolicy(
            MaxConsecutiveReplies: _settings.ParticipationMaxConsecutiveReplies,
            CooldownSeconds: _settings.ParticipationCooldownSeconds,
            ProbingMaxReplies: _settings.ParticipationProbingMaxReplies,
            MaxActiveLifetimeSeconds: _settings.ParticipationMaxActiveLifetimeSeconds,
            MaxExitingLifetimeSeconds: _settings.ParticipationMaxExitingLifetimeSeconds).Clamped();

    /// <summary>设置变了 → 把新的上限发给台账（状态保留、只换上限；只影响后续处理）。</summary>
    public void Rebuild()
    {
        var policy = BuildPolicy();
        _registry.UpdatePolicy(policy);

        _log($"[参与] 策略已更新：连续 ≤{policy.MaxConsecutiveReplies} / 冷却 {policy.CooldownSeconds}s"
             + $" / 试探 ≤{policy.ProbingMaxReplies} / 活性命 {policy.MaxActiveLifetimeSeconds}s"
             + $"（台账 {_registry.Count} 个会话，本轮不改判定）");
    }

    /// <summary>
    /// 喂给参与状态机一个事件并返回它的决策。
    /// 调用方是否**使用**这个结论取决于闸门开关：入站锚点会用它拦（闸门打开时），
    /// 其余锚点（工具失败 / 模型超时 / 回复终态）只是喂事件、顺便把结论交给闸门判定（默认关 = 放行）。
    /// 只写结构化信息（状态 / 原因码 / 会话 key），不写任何正文；异常绝不影响回复。
    /// </summary>
    public ParticipationDecision Observe(string sourceKey, ParticipationEvent evt)
    {
        try
        {
            var machine = _registry.For(sourceKey);
            var before = machine.State;
            var decision = machine.OnEvent(evt, Clock.Now);
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

        var now = Clock.Now;
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
