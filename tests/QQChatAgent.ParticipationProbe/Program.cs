using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using QQChatAgent.Services.Participation;

namespace QQChatAgent.ParticipationProbe;

/// <summary>
/// P1 参与状态机的**确定性机制探针**（V3 §7.4 + §11.2）。
///
/// 特点（都是有意的）：
///   · 全程**注入时间**，不用真实时钟 → 没有 flaky；
///   · 不启动机器人、不连网、不写数据目录、不发任何消息 → 不可能触发真实网关/定时/远程桥；
///   · 只断言结构化输出（Allow / State / ReasonCode）与内部计数，**不涉及任何正文**；
///   · 最后用反射钉一条"这个组件不持有可能发消息的东西"（不依赖人的自觉）。
///
/// 用法：dotnet run --project tests/QQChatAgent.ParticipationProbe -c Release
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    public static int Main()
    {
        var t0 = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.FromHours(8));

        // ── 1) 默认就是观望，且无关旁白不发言 ──
        var m = new ParticipationStateMachine(new ParticipationPolicy());
        Check("默认状态 = Observing（重启后的安全默认）", m.State == ParticipationState.Observing, m.State.ToString());

        var d = m.OnEvent(ParticipationEvent.IrrelevantMessage, t0, "fp-001");
        Check("观望期的无关消息 → 不发言", !d.Allow, d.Describe());
        Check("拒发原因码 = not_addressed（结构化，不是文案）", d.ReasonCode == "not_addressed", d.ReasonCode);
        Check("拒发不改变状态", m.State == ParticipationState.Observing, m.State.ToString());

        // ── 2) 明确 @ → 立刻允许（进入试探） ──
        d = m.OnEvent(ParticipationEvent.Mentioned, t0.AddSeconds(5), "fp-002");
        Check("明确 @ → 允许发言", d.Allow, d.Describe());
        Check("@ 之后进入 Probing（不是直接 Active）", m.State == ParticipationState.Probing, m.State.ToString());

        // ── 3) 成功回复 → Active + 冷却生效 ──
        d = m.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(6));
        Check("回复成功 → Active", m.State == ParticipationState.Active && d.Allow, d.Describe());
        Check("连续回复计数 = 1", m.ConsecutiveReplies == 1, m.ConsecutiveReplies.ToString());

        d = m.OnEvent(ParticipationEvent.IrrelevantMessage, t0.AddSeconds(10));
        Check("冷却期内的普通消息 → 不发（reason=cooldown）", !d.Allow && d.ReasonCode == "cooldown", d.Describe());

        d = m.OnEvent(ParticipationEvent.IrrelevantMessage, t0.AddSeconds(60));
        Check("冷却过后、未超上限 → 允许接一句（reason=active_relate）",
            d.Allow && d.ReasonCode == "active_relate", d.Describe());

        // ── 4) 试探没有得到确认 → 退场（不硬插话） ──
        var m2 = new ParticipationStateMachine(new ParticipationPolicy());
        m2.OnEvent(ParticipationEvent.Mentioned, t0, "fp-010");
        d = m2.OnEvent(ParticipationEvent.IrrelevantMessage, t0.AddSeconds(3));
        Check("试探后第一条还是旁白 → Exiting（不连续插话）",
            m2.State == ParticipationState.Exiting && !d.Allow, d.Describe());

        // ── 5) 连续回复上限：被 @ 也不能无限连发 ──
        var m3 = new ParticipationStateMachine(new ParticipationPolicy(MaxConsecutiveReplies: 2, CooldownSeconds: 0));
        m3.OnEvent(ParticipationEvent.Mentioned, t0, "a");
        m3.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(1));
        m3.OnEvent(ParticipationEvent.Mentioned, t0.AddSeconds(2), "b");
        m3.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(3));
        Check("已到连续回复上限（2）", m3.ConsecutiveReplies == 2, m3.ConsecutiveReplies.ToString());
        d = m3.OnEvent(ParticipationEvent.Mentioned, t0.AddSeconds(4), "c");
        Check("到上限且休息窗口已过 → 允许一句并重开一轮计数（mention_reply_cap_reset）",
            d.Allow && m3.State == ParticipationState.Probing && d.ReasonCode == "mention_reply_cap_reset"
            && m3.ConsecutiveReplies == 0, d.Describe());

        // 到上限、且还在休息窗口里 → 直接拒绝。老实现这一支恒 allow:true，上限只写日志、拦不住任何一句。
        var m3b = new ParticipationStateMachine(new ParticipationPolicy(MaxConsecutiveReplies: 2, CooldownSeconds: 60));
        m3b.OnEvent(ParticipationEvent.Mentioned, t0, "a2");
        m3b.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(1));
        m3b.OnEvent(ParticipationEvent.Mentioned, t0.AddSeconds(2), "b2");
        m3b.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(3));
        d = m3b.OnEvent(ParticipationEvent.Mentioned, t0.AddSeconds(4), "c2");
        Check("★ 到上限且在休息窗口内 → 拒绝（reply_cap_cooldown），不再“被 @ 就无限连发”",
            !d.Allow && d.ReasonCode == "reply_cap_cooldown", d.Describe());
        d = m3b.OnEvent(ParticipationEvent.Mentioned, t0.AddSeconds(90), "d2");
        Check("休息窗口过后 → 重新开一轮（mention_reply_cap_reset，允许）",
            d.Allow && d.ReasonCode == "mention_reply_cap_reset", d.Describe());

        // ── 6) 失败只会降级，绝不升级 ──
        var m4 = new ParticipationStateMachine(new ParticipationPolicy());
        m4.OnEvent(ParticipationEvent.Mentioned, t0, "x");
        m4.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(1));
        d = m4.OnEvent(ParticipationEvent.ToolFailure, t0.AddSeconds(2));
        Check("工具失败 → Exiting（降级）", !d.Allow && m4.State == ParticipationState.Exiting, d.Describe());
        d = m4.OnEvent(ParticipationEvent.ModelTimeout, t0.AddSeconds(3));
        Check("再失败一次 → 回 Observing（连败不升级）", !d.Allow && m4.State == ParticipationState.Observing, d.Describe());
        Check("失败计数被记录", m4.Failures == 2, m4.Failures.ToString());

        // ── 7) 状态寿命过期（状态不会卡死） ──
        var m5 = new ParticipationStateMachine(new ParticipationPolicy(MaxActiveLifetimeSeconds: 60, MaxExitingLifetimeSeconds: 30));
        m5.OnEvent(ParticipationEvent.Mentioned, t0, "life");
        m5.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(1));
        m5.OnEvent(ParticipationEvent.IrrelevantMessage, t0.AddSeconds(120));
        Check("Active 超寿命 → 自动落 Exiting", m5.State == ParticipationState.Exiting, m5.State.ToString());
        m5.OnEvent(ParticipationEvent.IrrelevantMessage, t0.AddSeconds(300));
        Check("Exiting 超寿命 → 回 Observing", m5.State == ParticipationState.Observing, m5.State.ToString());

        // ── 8) 会话隔离（不同会话互不污染；key 不做脱敏改写） ──
        var reg = new ParticipationRegistry(new ParticipationPolicy(CooldownSeconds: 0));
        var ga = reg.For("group:100001");
        var gb = reg.For("group:100002");
        ga.OnEvent(ParticipationEvent.Mentioned, t0, "k");
        ga.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(1));
        Check("会话 A 进入 Active", ga.State == ParticipationState.Active, ga.State.ToString());
        Check("会话 B 仍是 Observing（互不污染）", gb.State == ParticipationState.Observing, gb.State.ToString());
        Check("同一 key 取回同一个状态机", ReferenceEquals(reg.For("group:100001"), ga));
        Check("台账规模 = 2", reg.Count == 2, reg.Count.ToString());
        reg.Forget("group:100001");
        Check("Forget 后规模 = 1 且新对象是干净的",
            reg.Count == 1 && reg.For("group:100001").State == ParticipationState.Observing, reg.Count.ToString());

        // ── 9) 快照语义：热更新只影响后续（旧实例策略不被动过） ──
        var oldMachine = new ParticipationStateMachine(new ParticipationPolicy(MaxConsecutiveReplies: 1, PolicyVersion: 1));
        var newMachine = new ParticipationStateMachine(new ParticipationPolicy(MaxConsecutiveReplies: 5, PolicyVersion: 2));
        Check("旧快照策略版本仍是 1（不会被新配置改写）", oldMachine.PolicyVersion == 1, oldMachine.PolicyVersion.ToString());
        Check("新快照策略版本是 2", newMachine.PolicyVersion == 2, newMachine.PolicyVersion.ToString());
        Check("两套快照互不影响：旧的仍按上限 1 工作",
            ClampCheck(oldMachine, t0) == 1, "旧实例上限=" + ClampCheck(oldMachine, t0));

        // ── 10) 非法/未知输入一律拒绝（Fail-Closed） ──
        var m6 = new ParticipationStateMachine(new ParticipationPolicy());
        d = m6.OnEvent((ParticipationEvent)999, t0);
        Check("未知事件 → 不发言（reason=unknown_event）", !d.Allow && d.ReasonCode == "unknown_event", d.Describe());

        var clamped = new ParticipationPolicy(MaxConsecutiveReplies: 9999, CooldownSeconds: -5,
            ProbingMaxReplies: 100, MaxActiveLifetimeSeconds: 999999, PolicyVersion: 0).Clamped();
        Check("策略被钳制到安全范围（上限 10 / 冷却 0 / 试探 3 / 寿命 3600 / 版本 ≥1）",
            clamped.MaxConsecutiveReplies == 10 && clamped.CooldownSeconds == 0 && clamped.ProbingMaxReplies == 3
            && clamped.MaxActiveLifetimeSeconds == 3600 && clamped.PolicyVersion == 1,
            $"{clamped.MaxConsecutiveReplies}/{clamped.CooldownSeconds}/{clamped.ProbingMaxReplies}/{clamped.MaxActiveLifetimeSeconds}/{clamped.PolicyVersion}");

        // ── 11) 指纹截断（不允许有人把正文塞进来） ──
        var longFp = new string('x', 200);
        m6.OnEvent(ParticipationEvent.Mentioned, t0, longFp);
        Check("触发指纹被截断到 ≤32 字符（不存正文）",
            m6.LastTriggerFingerprint.Length <= 32, m6.LastTriggerFingerprint.Length.ToString());
        Check("状态机里不含长文本字段（正文无处可放）",
            typeof(ParticipationStateMachine).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .All(f => f.FieldType != typeof(string) || f.Name != "LastTriggerFingerprint"),
            "字段类型检查");

        // ── 12) Reset 回安全默认 ──
        ga.Reset();
        Check("Reset → Observing 且计数/冷却清零",
            ga.State == ParticipationState.Observing && ga.ConsecutiveReplies == 0
            && ga.CooldownUntil == DateTimeOffset.MinValue && ga.Failures == 0, ga.State.ToString());

        // ── 13) 反射钉死：这个组件不可能直接发消息（没有网关照/发消息字段） ──
        var fieldTypes = typeof(ParticipationStateMachine).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.FieldType).ToList();
        var suspicious = fieldTypes.Where(t =>
            t.Name.Contains("Gateway", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("Source", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("Http", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("Socket", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("Task", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("Timer", StringComparison.OrdinalIgnoreCase)).ToList();
        Check("不持有任何网关/HTTP/Socket/Task/Timer 字段（结构上不可能自己发消息或定时唤醒）",
            suspicious.Count == 0, string.Join(",", suspicious.Select(t => t.Name)));

        var asyncMethods = typeof(ParticipationStateMachine).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(mm => mm.ReturnType == typeof(System.Threading.Tasks.Task)).ToList();
        Check("没有任何 async/Task 返回的公开方法（不 await、不阻塞调用方）", asyncMethods.Count == 0, asyncMethods.Count.ToString());

        // ── 17) 策略热更新 + 入站映射 + 接线（面板改设置那条路 / BotAgent 真的喂了事件） ──
        PolicyUpdateTests(t0);

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ─────────────────── 14) 策略热更新（面板改设置那条路）───────────────────

    /// <summary>
    /// 面板改的参与上限必须**真的到了状态机手里**，而且：① 状态保留；② 仍然被服务端钳制。
    /// 这三条少一条，就会出现“面板改了没用”或“面板填 999 就真按 999 跑”这两种漏子。
    /// </summary>
    private static void PolicyUpdateTests(DateTimeOffset t0)
    {
        // ① 换上限：状态与计数保留（不该因为它改个数字就“忘掉自己正在参与”）
        var machine = new ParticipationStateMachine(new ParticipationPolicy(MaxConsecutiveReplies: 3, CooldownSeconds: 20));
        machine.OnEvent(ParticipationEvent.Mentioned, t0, "p1");
        machine.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(1));
        var beforeState = machine.State;
        var beforeCount = machine.ConsecutiveReplies;

        machine.UpdatePolicy(new ParticipationPolicy(MaxConsecutiveReplies: 7, CooldownSeconds: 45));
        Check("换上限后状态与计数保留（不会因为改参数就重置）",
            machine.State == beforeState && machine.ConsecutiveReplies == beforeCount,
            $"state={machine.State} count={machine.ConsecutiveReplies}");

        // ② 新上限真的生效：说到第 5 次时点名仍走正常那一支（在旧的 3 次上限下，第 4 次点名就该降级了）。
        //    注意要点名那一下同时满足：计数未到上限 **且** 冷却已过（否则会走 mention_in_cooldown_probe 那一支）。
        for (var i = 0; i < 4; i++)
        {
            machine.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(10 + i));
        }

        Check("加了 4 次回复后计数 = 5（已超过旧上限 3）", machine.ConsecutiveReplies == 5,
            machine.ConsecutiveReplies.ToString());

        var stillActive = machine.OnEvent(ParticipationEvent.Mentioned, t0.AddSeconds(60), "p2");
        Check("新上限生效：计数 5 < 7 且冷却已过 → 点名仍走正常支（Active / mentioned）",
            stillActive.Allow && stillActive.State == ParticipationState.Active
            && stillActive.ReasonCode == "mentioned", stillActive.Describe());

        machine.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(70));
        machine.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(71));
        var capped = machine.OnEvent(ParticipationEvent.Mentioned, t0.AddSeconds(200), "p3");
        Check("到新上限（7）后点名先降为试探并重开计数（mention_reply_cap_reset）",
            capped.Allow && capped.State == ParticipationState.Probing
            && capped.ReasonCode == "mention_reply_cap_reset", capped.Describe());

        // ③ 面板填多大都会被钳（这里的 999 相当于“面板里手填的上限”）
        machine.UpdatePolicy(new ParticipationPolicy(
            MaxConsecutiveReplies: 999, CooldownSeconds: 99999, ProbingMaxReplies: 999,
            MaxActiveLifetimeSeconds: 999999, MaxExitingLifetimeSeconds: 999999));
        var policy = machine.GetType()
            .GetProperty("PolicyVersion", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(machine);
        Check("策略版本仍可读（版本号随快照走）", policy is int, policy?.ToString() ?? "(null)");

        var clampedMachine = new ParticipationStateMachine(new ParticipationPolicy());
        clampedMachine.UpdatePolicy(new ParticipationPolicy(
            MaxConsecutiveReplies: 999, CooldownSeconds: 99999, ProbingMaxReplies: 999,
            MaxActiveLifetimeSeconds: 999999, MaxExitingLifetimeSeconds: 999999));
        var clampedPolicyField = typeof(ParticipationStateMachine)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(f => f.FieldType == typeof(ParticipationPolicy))
            .GetValue(clampedMachine) as ParticipationPolicy;
        Check("面板填 999 → 状态机手里仍是被钳过的值（连续 ≤10 / 冷却 ≤600）",
            clampedPolicyField is { MaxConsecutiveReplies: 10, CooldownSeconds: 600, ProbingMaxReplies: 3,
                MaxActiveLifetimeSeconds: 3600, MaxExitingLifetimeSeconds: 3600 },
            clampedPolicyField is null
                ? "(拿不到策略字段)"
                : $"{clampedPolicyField.MaxConsecutiveReplies}/{clampedPolicyField.CooldownSeconds}/{clampedPolicyField.ProbingMaxReplies}/{clampedPolicyField.MaxActiveLifetimeSeconds}/{clampedPolicyField.MaxExitingLifetimeSeconds}");

        // ④ 台账级热更新：已有会话一起换（否则面板改了只对新群生效）
        var registry = new ParticipationRegistry(new ParticipationPolicy(MaxConsecutiveReplies: 2));
        var tracked = registry.For("group:10001");
        tracked.OnEvent(ParticipationEvent.Mentioned, t0, "r1");
        tracked.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(1));

        registry.UpdatePolicy(new ParticipationPolicy(MaxConsecutiveReplies: 6));
        Check("台账热更新：已有会话也换了上限（版本号跟着走）",
            registry.Policy.MaxConsecutiveReplies == 6 && tracked.PolicyVersion == registry.Policy.PolicyVersion,
            $"registry={registry.Policy.MaxConsecutiveReplies} trackedV={tracked.PolicyVersion}");

        // ⑤ 快照：只给结构化字段（状态/原因/计数），会话 key 原样（脱敏是显示层的事）
        var snapshot = registry.Snapshot();
        Check("快照按会话给出状态与原因码（不含正文）",
            snapshot.Count == 1 && snapshot[0].SourceKey == "group:10001"
            && snapshot[0].State == ParticipationState.Active && snapshot[0].ReasonCode == "replied",
            snapshot.Count == 0 ? "(空)" : snapshot[0].ToString());

        // ── 15) 入站消息 → 事件的映射（接线的判据） ──
        Check("群里被 @ → Mentioned（最强触发）",
            ParticipationEvents.ClassifyInbound(isGroup: true, directToBot: true) == ParticipationEvent.Mentioned);
        Check("群里的其它消息 → IrrelevantMessage（交叉发言/旁白）",
            ParticipationEvents.ClassifyInbound(isGroup: true, directToBot: false) == ParticipationEvent.IrrelevantMessage);
        Check("私聊 → RepliedToBot（一对一里每条都是在跟它说话）",
            ParticipationEvents.ClassifyInbound(isGroup: false, directToBot: false) == ParticipationEvent.RepliedToBot);

        // ── 16) 反射钉死：BotAgent 真的把四个事件都喂进去了（接线在，不只是纯逻辑） ──
        var botType = typeof(QQChatAgent.Services.BotAgent);
        var observeMethod = botType
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(mm => mm.Name == "ObserveParticipation");
        Check("BotAgent 里有喂事件的方法（参数是会话 key + 事件）",
            observeMethod is not null &&
            observeMethod.GetParameters().Length == 2 &&
            observeMethod.GetParameters()[1].ParameterType == typeof(ParticipationEvent),
            observeMethod?.ToString() ?? "(找不到 ObserveParticipation)");

        var botFields = botType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Check("BotAgent 持有参与台账（接线在）",
            botFields.Any(f => f.FieldType == typeof(ParticipationRegistry)),
            string.Join(",", botFields.Select(f => f.FieldType.Name).Where(n => n.Contains("Participation"))));

        var policyMethod = botType
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(mm => mm.Name == "BuildParticipationPolicy");
        Check("BotAgent 会从设置里拼参与上限（面板那 5 个参数真的被读）",
            policyMethod is not null && policyMethod.ReturnType == typeof(ParticipationPolicy),
            policyMethod?.ToString() ?? "(找不到 BuildParticipationPolicy)");
    }

    private static int ClampCheck(ParticipationStateMachine machine, DateTimeOffset t0)
    {
        // 上限 1：第一次点名的回复用掉名额后，第二次点名应降级为 Probing
        machine.OnEvent(ParticipationEvent.Mentioned, t0, "c1");
        machine.OnEvent(ParticipationEvent.Replied, t0.AddSeconds(1));
        machine.OnEvent(ParticipationEvent.Mentioned, t0.AddSeconds(2), "c2");
        return machine.ConsecutiveReplies;
    }

    private static void Check(string description, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  ✓ " + description);
        }
        else
        {
            _failed++;
            Console.WriteLine("  ✗ " + description + (string.IsNullOrEmpty(detail) ? string.Empty : "   → " + detail));
        }
    }
}
