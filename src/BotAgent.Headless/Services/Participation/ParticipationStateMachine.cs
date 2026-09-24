using System;
using System.Collections.Generic;

namespace BotAgent.Services.Participation;

/// <summary>
/// 会话级「参与状态」的取值。**服务端定义**，模型不能直接写状态名（V3 §7.3）。
/// </summary>
public enum ParticipationState
{
    /// <summary>观望（默认、也是重启后的安全默认）：除明确触发外不发言。</summary>
    Observing = 0,

    /// <summary>试探：只允许极有限的一次短回复，然后退回观察，避免连续刷屏。</summary>
    Probing = 1,

    /// <summary>当前话题中已参与：受冷却、连续回复上限、状态寿命约束。</summary>
    Active = 2,

    /// <summary>退场：话题相关性下降或连续静默后进入；下一次明确触发可重新试探。</summary>
    Exiting = 3,
}

/// <summary>喂给状态机的事件（由调用方按既有消息链路判定，**不含正文**）。</summary>
public enum ParticipationEvent
{
    /// <summary>明确 @ 机器人（最强触发）。</summary>
    Mentioned = 0,

    /// <summary>回复机器人自己的消息（次强触发：视为在跟它说话）。</summary>
    RepliedToBot = 1,

    /// <summary>会话里出现的、与机器人无关的普通消息（交叉发言/旁白）。</summary>
    IrrelevantMessage = 2,

    /// <summary>机器人成功发送了一条回复（含引用/语音/表情包等任一形式）。</summary>
    Replied = 3,

    /// <summary>这一轮决定不发言（静默 / 被服务端拦下）。</summary>
    Silent = 4,

    /// <summary>工具执行失败。</summary>
    ToolFailure = 5,

    /// <summary>模型超时或调用失败。</summary>
    ModelTimeout = 6,

    /// <summary>话题明显切换（例如被 @ 之外的长时间间隔或话题标记变化）。</summary>
    TopicShift = 7,
}

/// <summary>
/// 服务端硬上限（V3 §7.3「上下限必须由服务端校验」）。
/// 一次性快照交给状态机；**不从模型输出里取任何数值**。
/// </summary>
public sealed record ParticipationPolicy(
    int MaxConsecutiveReplies = 3,
    int CooldownSeconds = 20,
    int ProbingMaxReplies = 1,
    int MaxActiveLifetimeSeconds = 900,
    int MaxExitingLifetimeSeconds = 180,
    int PolicyVersion = 1)
{
    /// <summary>钳制到安全范围：配置写错也不至于放开（Fail-Closed 的口径）。</summary>
    public ParticipationPolicy Clamped() => new(
        MaxConsecutiveReplies: Math.Clamp(MaxConsecutiveReplies, 1, 10),
        CooldownSeconds: Math.Clamp(CooldownSeconds, 0, 600),
        ProbingMaxReplies: Math.Clamp(ProbingMaxReplies, 1, 3),
        MaxActiveLifetimeSeconds: Math.Clamp(MaxActiveLifetimeSeconds, 30, 3600),
        MaxExitingLifetimeSeconds: Math.Clamp(MaxExitingLifetimeSeconds, 10, 3600),
        PolicyVersion: Math.Max(1, PolicyVersion));
}

/// <summary>一次参与判断的结果（**结构化**，不含任何正文）。</summary>
public readonly record struct ParticipationDecision(
    bool Allow,
    ParticipationState State,
    string ReasonCode)
{
    /// <summary>只给日志用的单行摘要（不含正文、不含提示词）。</summary>
    public string Describe() => $"state={State} allow={Allow} reason={ReasonCode}";
}

/// <summary>
/// 群聊参与状态机（V3 §7 的 P1 实现）。
///
/// 设计口径（都来自 V3，逐条对应）：
///   · **模型只能建议，状态与上限由服务端定**：这里不接受任何来自模型输出的状态名/时间/计数。
///   · **不轮询、不定时唤醒**：状态只在事件到达时推进（<see cref="OnEvent"/> 是唯一入口）。
///   · **有界**：连续回复数、冷却、状态寿命都有硬上限；超时/失败只会降级，绝不升级权限。
///   · **不保存正文**：只记状态、时间、计数、原因码、参与者标识的哈希（见 <see cref="LastTriggerFingerprint"/>）。
///   · **重启回安全默认**：本对象默认在内存里（<see cref="BotAgentHost"/> 侧不持久化）；
///     进程重启后由调用方重新 new，状态即 <see cref="ParticipationState.Observing"/>。
///   · **不碰权限**：这里只回答“要不要发言”，不授予任何工具/通道/Markdown 能力。
/// </summary>
public sealed class ParticipationStateMachine
{
    private ParticipationPolicy _policy;

    public ParticipationStateMachine(ParticipationPolicy policy)
    {
        _policy = (policy ?? new ParticipationPolicy()).Clamped();
    }

    /// <summary>当前状态（只读，服务端决定）。</summary>
    public ParticipationState State { get; private set; } = ParticipationState.Observing;

    /// <summary>策略版本（进快照，热更新只影响后续处理）。</summary>
    public int PolicyVersion => _policy.PolicyVersion;

    /// <summary>
    /// 换一份策略（面板改设置后的热更新）。**状态与计数保留** —— 策略只是一组上限，
    /// 换掉它不该让这个会话“忘掉”自己正在参与；下一次 <see cref="OnEvent" /> 就按新上限判。
    /// 仍然要过 <see cref="ParticipationPolicy.Clamped" />：面板填多大都不会超过服务端硬上限。
    /// </summary>
    public void UpdatePolicy(ParticipationPolicy policy)
        => _policy = (policy ?? new ParticipationPolicy()).Clamped();

    /// <summary>上次状态转移时间。</summary>
    public DateTimeOffset LastTransitionAt { get; private set; } = Clock.Now;

    /// <summary>连续回复计数（成功回复后 +1；静默/失败/退场清零）。</summary>
    public int ConsecutiveReplies { get; private set; }

    /// <summary>试探阶段已发言次数（进入 Probing 时清零）。</summary>
    public int ProbingReplies { get; private set; }

    /// <summary>冷却截止时间（未冷却时为 <see cref="DateTimeOffset.MinValue"/>）。</summary>
    public DateTimeOffset CooldownUntil { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>连续失败次数（超时/工具失败都算；成功回复清零）。</summary>
    public int Failures { get; private set; }

    /// <summary>
    /// 最近一次有效触发的**指纹**（调用方传入的短哈希/占位串，例如消息 seq 的哈希）——
    /// 刻意不存正文与昵称，面板/日志要显示触发原因时也只显示这个。
    /// </summary>
    public string LastTriggerFingerprint { get; private set; } = string.Empty;

    /// <summary>最近一次的原因码（结构化，可直接进运行记录）。</summary>
    public string LastReasonCode { get; private set; } = "init";

    /// <summary>
    /// 唯一入口：吃一个事件，推进状态并按服务端规则回答“允不允许发言”。
    /// 这是**纯内存计算**，不阻塞、不 await、不发消息、不碰网关。
    /// </summary>
    public ParticipationDecision OnEvent(ParticipationEvent evt, DateTimeOffset now, string triggerFingerprint = "")
    {
        Expire(now);

        if (!string.IsNullOrEmpty(triggerFingerprint))
        {
            // 只留很短的指纹，防止有人把正文塞进来（V3 §7.2：不保存正文）
            LastTriggerFingerprint = triggerFingerprint.Length <= 32 ? triggerFingerprint : triggerFingerprint[..32];
        }

        switch (evt)
        {
            case ParticipationEvent.Mentioned:
            case ParticipationEvent.RepliedToBot:
                return OnExplicitTrigger(evt, now);

            case ParticipationEvent.IrrelevantMessage:
                return OnIrrelevant(now);

            case ParticipationEvent.Replied:
                return OnReplied(now);

            case ParticipationEvent.Silent:
                return OnSilent(now);

            case ParticipationEvent.ToolFailure:
            case ParticipationEvent.ModelTimeout:
                return OnFailure(evt, now);

            case ParticipationEvent.TopicShift:
                // 话题切换 → 退场（下次明确触发再回来）
                return Transition("topic_shift", ParticipationState.Exiting, now);

            default:
                // 未知事件：不改变状态、不发言（Fail-Closed）
                return Deny("unknown_event");
        }
    }

    /// <summary>进程重启 / 会话被清空时的安全重置：回到观望，清掉所有计数。</summary>
    public void Reset()
    {
        State = ParticipationState.Observing;
        ConsecutiveReplies = 0;
        ProbingReplies = 0;
        CooldownUntil = DateTimeOffset.MinValue;
        Failures = 0;
        LastTransitionAt = Clock.Now;
        LastReasonCode = "reset";
        LastTriggerFingerprint = string.Empty;
    }

    // ───────────────────────── 事件处理 ─────────────────────────

    private ParticipationDecision OnExplicitTrigger(ParticipationEvent evt, DateTimeOffset now)
    {
        // 明确点名/被回复：即使处于冷却也**允许进入试探**（但受连续回复上限与寿命约束）。
        if (State == ParticipationState.Active)
        {
            if (ConsecutiveReplies >= _policy.MaxConsecutiveReplies)
            {
                // 已经说够了：**仍然放行这一句**，但降级为试探（不延续 active 的特权）。
                //
                // 为什么上限不拦显式触发：号主的硬要求是“直接跟我说话必须回”——
                // 被 @ 了却不理，在群里看着就是坏了（这条踩过，见 handoff §27）。
                // 上限真正管的是**没人叫它的时候**：`OnIrrelevant` 在 Active 状态下到上限就退场、
                // 冷却中也一律拒绝（那才是“话题尾部硬跟”的来源）。
                // 显式触发之间的频率由全局回复冷却（GroupCooldownSeconds / PrivateCooldownSeconds）限速。
                return Transition("mention_reply_cap", ParticipationState.Probing, now, allow: true, probing: true);
            }

            if (now < CooldownUntil)
            {
                // 冷却中仍被点名：允许这一句，但状态回到试探（不延续 active 的特权）
                return Transition("mention_in_cooldown_probe", ParticipationState.Probing, now, allow: true, probing: true);
            }

            return Transition(evt == ParticipationEvent.Mentioned ? "mentioned" : "replied_to_bot",
                ParticipationState.Active, now, allow: true);
        }

        // 观望/试探/退场 → 试探（真正进入 active 要等回复成功）
        var reason = evt == ParticipationEvent.Mentioned ? "mentioned_probe" : "replied_to_bot_probe";
        return Transition(reason, ParticipationState.Probing, now, allow: true, probing: true);
    }

    private ParticipationDecision OnIrrelevant(DateTimeOffset now)
    {
        switch (State)
        {
            case ParticipationState.Probing:
                // 试探没得到确认（下一条还是旁白）→ 退场，不再硬插话
                return Transition("probing_no_confirm", ParticipationState.Exiting, now);

            case ParticipationState.Active:
                // 旁白不断：达到上限或冷却中就不再说话
                if (ConsecutiveReplies >= _policy.MaxConsecutiveReplies)
                {
                    return Transition("active_reply_cap", ParticipationState.Exiting, now);
                }

                if (now < CooldownUntil)
                {
                    return Deny("cooldown", ParticipationState.Active);
                }

                // 允许“活跃状态下偶尔接一句”，但仍是逐条判断、由上层模型再决定内容
                return Allow("active_relate", ParticipationState.Active);

            default:
                return Deny("not_addressed", State);
        }
    }

    private ParticipationDecision OnReplied(DateTimeOffset now)
    {
        ConsecutiveReplies++;
        Failures = 0;

        var cooldown = now.AddSeconds(_policy.CooldownSeconds);
        if (State == ParticipationState.Probing)
        {
            ProbingReplies++;
        }

        CooldownUntil = cooldown;
        // 成功回复 → Active（V3 §7.3 的转移表）。老实现这里的三元表达式三个分支全是 Active，
        // 顺带让 ProbingMaxReplies 成了永不生效的死配置：它目前只被记账，不影响转移结果。
        return Transition("replied", ParticipationState.Active, now, allow: true);
    }

    private ParticipationDecision OnSilent(DateTimeOffset now)
    {
        ConsecutiveReplies = 0;

        // 试探/活跃里静默 → 说明话题不在了，退场（下次明确触发再回来）
        var next = State switch
        {
            ParticipationState.Probing => ParticipationState.Exiting,
            ParticipationState.Active => ParticipationState.Exiting,
            _ => State,
        };

        return Transition("silent", next, now);
    }

    private ParticipationDecision OnFailure(ParticipationEvent evt, DateTimeOffset now)
    {
        Failures++;
        ConsecutiveReplies = 0;

        // 失败只会降级：先退场，连败则回观望（绝不升级权限，也不需要模型同意）
        var next = Failures >= 2 ? ParticipationState.Observing : ParticipationState.Exiting;
        return Transition(evt == ParticipationEvent.ToolFailure ? "tool_failure" : "model_timeout", next, now);
    }

    // ───────────────────────── 内部工具 ─────────────────────────

    /// <summary>状态寿命到期：active 太长、exiting 太久都自然回落，避免“状态卡死”。</summary>
    private void Expire(DateTimeOffset now)
    {
        var age = now - LastTransitionAt;
        switch (State)
        {
            case ParticipationState.Active when age.TotalSeconds > _policy.MaxActiveLifetimeSeconds:
                Transition("active_expired", ParticipationState.Exiting, now);
                break;
            case ParticipationState.Exiting when age.TotalSeconds > _policy.MaxExitingLifetimeSeconds:
                Transition("exiting_expired", ParticipationState.Observing, now);
                break;
            case ParticipationState.Probing when age.TotalSeconds > _policy.MaxExitingLifetimeSeconds:
                Transition("probing_expired", ParticipationState.Observing, now);
                break;
        }
    }

    private ParticipationDecision Transition(
        string reasonCode,
        ParticipationState next,
        DateTimeOffset now,
        bool allow = false,
        bool probing = false)
    {
        if (next == ParticipationState.Probing)
        {
            ProbingReplies = 0;
        }

        if (next != State)
        {
            State = next;
            LastTransitionAt = now;
        }

        LastReasonCode = reasonCode;
        return new ParticipationDecision(allow, State, reasonCode);
    }

    private ParticipationDecision Allow(string reasonCode, ParticipationState state)
    {
        LastReasonCode = reasonCode;
        return new ParticipationDecision(true, state, reasonCode);
    }

    private ParticipationDecision Deny(string reasonCode, ParticipationState? state = null)
    {
        LastReasonCode = reasonCode;
        return new ParticipationDecision(false, state ?? State, reasonCode);
    }
}

/// <summary>
/// 按会话维度的状态机台账（key = 会话 key，与 <c>ConversationStore</c> 同一套 key，**不做脱敏改写**）。
/// 刻意只在内存里：重启即回安全默认（V3 §7.2 允许，但必须写明 —— 见 <c>docs/engineering/progress.md</c>）。
/// </summary>
public sealed class ParticipationRegistry
{
    private readonly Dictionary<string, ParticipationStateMachine> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// 台账要能被**多会话并行**访问（V3 §5.3 明确“多会话并行”），而且从这一版起
    /// 事件还会从后台任务里喂进来（搜索/读页面/听歌失败都是 Task.Run）。
    /// 每个会话自己的处理是串行的，所以状态机本身不用锁；要锁的是这张**表**。
    /// </summary>
    private readonly object _sync = new();

    public ParticipationPolicy Policy { get; private set; }

    public ParticipationRegistry(ParticipationPolicy policy)
    {
        Policy = (policy ?? new ParticipationPolicy()).Clamped();
    }

    /// <summary>取（或建）某会话的状态机。会话之间互不污染。</summary>
    public ParticipationStateMachine For(string sourceKey)
    {
        lock (_sync)
        {
            if (!_byKey.TryGetValue(sourceKey, out var machine))
            {
                machine = new ParticipationStateMachine(Policy);
                _byKey[sourceKey] = machine;
            }

            return machine;
        }
    }

    /// <summary>会话被删除/清空时调用，避免台账无限增长。</summary>
    public void Forget(string sourceKey)
    {
        lock (_sync)
        {
            _byKey.Remove(sourceKey);
        }
    }

    /// <summary>
    /// 热更新策略：**新会话**用新策略，**已有会话**也一起换（状态保留，只换上限）。
    /// 为什么要动已有会话：否则面板改了参数只对新群生效，老群永远用旧值 ——
    /// 那会让人以为“改了没用”。每次 <see cref="ParticipationStateMachine.OnEvent" /> 都是一次“处理”，
    /// 所以换上限只影响后续处理（V3 §5.3），不会打断正在判的那一轮。
    /// </summary>
    public void UpdatePolicy(ParticipationPolicy policy)
    {
        var clamped = (policy ?? new ParticipationPolicy()).Clamped();
        lock (_sync)
        {
            Policy = clamped;
            foreach (var machine in _byKey.Values)
            {
                machine.UpdatePolicy(clamped);
            }
        }
    }

    /// <summary>当前台账规模（给面板/日志用；不含正文）。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _byKey.Count;
            }
        }
    }

    /// <summary>
    /// 台账快照（给面板/日志看“现在每个会话处在什么状态”）。
    /// **只有结构化字段**：会话 key、状态、原因码、计数、时间 —— 不含任何正文或昵称；
    /// key 的交由调用方按脱敏开关处理（显示层脱敏、存储与 key 保持原样）。
    /// </summary>
    public IReadOnlyList<ParticipationSnapshot> Snapshot()
    {
        lock (_sync)
        {
            return _byKey
                .Select(kv => new ParticipationSnapshot(
                    kv.Key,
                    kv.Value.State,
                    kv.Value.LastReasonCode,
                    kv.Value.ConsecutiveReplies,
                    kv.Value.ProbingReplies,
                    kv.Value.Failures,
                    kv.Value.CooldownUntil,
                    kv.Value.LastTransitionAt,
                    kv.Value.PolicyVersion))
                .OrderBy(s => s.SourceKey, StringComparer.Ordinal)
                .ToList();
        }
    }
}

/// <summary>一个会话的参与状态快照（**不含正文**；key 由调用方决定是否脱敏）。</summary>
public readonly record struct ParticipationSnapshot(
    string SourceKey,
    ParticipationState State,
    string ReasonCode,
    int ConsecutiveReplies,
    int ProbingReplies,
    int Failures,
    DateTimeOffset CooldownUntil,
    DateTimeOffset LastTransitionAt,
    int PolicyVersion);
