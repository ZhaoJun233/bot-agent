using System;
using System.Threading;
using System.Threading.Tasks;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Services.Model;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    /// <summary>
    /// #98 提示词分层：稳定前缀 + 动态尾巴。
    ///
    /// 供应商侧的输入缓存按 **token 前缀** 命中 —— 只要请求开头那段与上一轮相同就能复用。
    /// 所以这里钉的不是"缓存有没有真的命中"（合成桩看不到供应商），而是**我们真正能控制的那一半**：
    /// 同一份输入下，每轮必变的东西（时间、心情、气氛、资料、档案…）必须全部落在最后，
    /// 前面的写作规则、人设、动作契约与工具清单必须逐字可复用。
    /// </summary>
    private static void PromptCacheStabilityTests()
    {
        Section("提示词缓存前缀稳定性（#98：稳定前缀 + 动态尾巴）");
        var originalClock = Clock.Current;
        var clock = new MutablePromptClock(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(8)));
        Clock.Use(clock);
        try
        {
            var request = new PromptBuilder.PromptRequest(
                SystemPrompt: "合成系统提示", BotIdentity: "10001", Persona: "你是一只话不多的猫", AiDesire: 80,
                Window: Array.Empty<ChatMessage>(), QuotableIds: Array.Empty<long>(), ProfilesText: null,
                Stickers: null, PokeContext: false, Proactive: false, MoodText: null, MusicText: null,
                LinkText: null, RecallText: null, GroupRolesText: null, VibeHint: null, SearchText: null,
                SuitabilityThreshold: 10, EnableListen: false, EnableVoice: false, VoiceMaxChars: 30,
                VoiceEagerness: 50, EnableWebSearch: false, EnableAsk: false, EnableToolRequest: false,
                ToolList: "\n\n[可用工具]\n- web.search\n- voice.speak");
            var first = PromptBuilder.Build(request);
            var parts = PromptBuilder.BuildParts(request);
            Check("显式边界保留原文本，Build 与稳定段加动态段逐字相同",
                parts.Stable.Length > 0 && parts.Dynamic.Length > 0 && parts.Text == first
                && parts.Stable + parts.Dynamic == first);
            var changedDynamic = PromptBuilder.BuildParts(request with
            {
                MoodText = "合成心情", ProfilesText = "群友A：合成档案",
                RecallText = "合成撤回", SearchText = "合成搜索资料",
                LinkText = "合成链接摘要", EpisodesText = "合成记忆", JargonText = "合成黑话"
            });
            Check("动态记忆、时间与资料不污染声明的缓存前缀",
                changedDynamic.Stable == parts.Stable && changedDynamic.Dynamic != parts.Dynamic);
            var changedPersona = PromptBuilder.BuildParts(request with { Persona = "合成新角色" });
            Check("人设变化确实使稳定前缀失效，不错误复用旧身份", changedPersona.Stable != parts.Stable);
            var privateParts = PromptBuilder.BuildParts(request with { IsPrivateConversation = true });
            Check("私聊和群聊指引仍有独立稳定前缀", privateParts.Stable != parts.Stable
                && privateParts.Text == PromptBuilder.Build(request with { IsPrivateConversation = true }));

            // ① 时间块落在最末尾：它每轮都变，放在前缀里会让后面所有内容一起失效。
            var timeStart = first.IndexOf("\n\n[现在的时间]", StringComparison.Ordinal);
            Check("★ 当前时间落在提示词最末尾（动态尾巴），不再紧跟在身份后面",
                timeStart > 0 && first.EndsWith("基准仍是上面这条。", StringComparison.Ordinal),
                $"timeStart={timeStart}，总长 {first.Length} 字");

            // ② 只有时钟变：时间块之前必须逐字相同（这就是可复用的前缀）。
            clock.Now = clock.Now.AddMinutes(37);
            var later = PromptBuilder.Build(request);
            var laterTimeStart = later.IndexOf("\n\n[现在的时间]", StringComparison.Ordinal);
            Check("★ 只有时钟变时，时间块之前的全部内容逐字相同（前缀可复用）",
                timeStart == laterTimeStart && first[..timeStart] == later[..laterTimeStart],
                $"两次前缀长度 {timeStart} / {laterTimeStart}");

            // ③ 前缀里必须有「跨轮不该变」的东西，否则分层没意义。
            var stable = first[..timeStart];
            Check("★ 稳定前缀里含人设、发言规则与工具清单",
                stable.Contains("[机器人人设档案]", StringComparison.Ordinal) &&
                stable.Contains("[可用工具]", StringComparison.Ordinal) &&
                stable.Contains("suitability 就是“该不该开口”分数", StringComparison.Ordinal),
                $"稳定前缀 {stable.Length} 字");
            Check("稳定前缀占提示词主体（≥ 70%）", stable.Length * 10 >= first.Length * 7,
                $"稳定 {stable.Length} / 总 {first.Length} 字（{stable.Length * 100 / first.Length}%）");

            // ④ 工具清单顺序跨次构建稳定：同输入两次构建，前缀逐字相同。
            var again = PromptBuilder.Build(request);
            Check("同一输入两次构建，稳定前缀逐字一致（工具顺序不抖）",
                again[..again.IndexOf("\n\n[现在的时间]", StringComparison.Ordinal)] == stable);

            // ⑤ 动态字段只影响尾巴：加了心情/气氛后，前缀一直到「发言意愿偏置」那一句都不变。
            var withDynamic = PromptBuilder.Build(request with { MoodText = "有点困", VibeHint = "群里在聊加班" });
            var desireAt = withDynamic.IndexOf("你非常健谈主动", StringComparison.Ordinal);
            Check("动态字段不污染稳定前缀（心情/气氛只追加在叙述之后）",
                desireAt > 0 && withDynamic[..desireAt] == first[..desireAt],
                $"稳定边界 {desireAt} 字");
            Check("★ 每轮会变的内容全部落在动态区（心情/气氛/资料/档案/撤回/身份/记忆/黑话）",
                withDynamic.IndexOf("[你此刻的心情]", StringComparison.Ordinal) > desireAt &&
                withDynamic.IndexOf("[你上一条消息时的感觉]", StringComparison.Ordinal) > desireAt &&
                PromptBuilder.Build(request with { SearchText = "合成资料" }).IndexOf("[刚查到的资料]", StringComparison.Ordinal) > desireAt &&
                PromptBuilder.Build(request with { ProfilesText = "群友A：合成档案" }).IndexOf("[会话参与者档案", StringComparison.Ordinal) > desireAt &&
                PromptBuilder.Build(request with { RecallText = "群友A 撤回了一条" }).IndexOf("[有人撤回了消息]", StringComparison.Ordinal) > desireAt &&
                PromptBuilder.Build(request with { GroupRolesText = "群友A 是群主" }).IndexOf("[本群身份]", StringComparison.Ordinal) > desireAt &&
                PromptBuilder.Build(request with { PokeContext = true }).IndexOf("[戳一戳怎么回]", StringComparison.Ordinal) > desireAt);

            // ⑥ 开关类段落仍在前缀里（它们由配置决定，不随轮次变）：开/关要能正确反映到请求。
            var withSearch = PromptBuilder.Build(request with { EnableWebSearch = true });
            var searchAt = withSearch.IndexOf("\n\n[联网搜索]", StringComparison.Ordinal);
            Check("配置开关段落进稳定前缀：打开联网就出现在尾巴之前，关闭时不出现",
                searchAt > 0 && searchAt < withSearch.IndexOf("你非常健谈主动", StringComparison.Ordinal) &&
                !first.Contains("[联网搜索]", StringComparison.Ordinal));

            // ⑦ 时间基准的措辞与 #97 一致（搬家不改字）。
            Check("时间块搬家不改字（基准措辞与 #97 一致）",
                first.Contains("这是本机配置时区，也是唯一的时间基准。", StringComparison.Ordinal) &&
                first.Contains("知识截止时间", StringComparison.Ordinal));

            // ⑧ #135：历史消息的时间渲染只取决于消息本身 —— 跨午夜也不许改写法。
            // 这一条是「前缀稳不稳定」的另一半：提示词分好层了，历史行自己每跨一天变一次样，
            // 前面的历史照样全部作废。做法是**把时钟拨过午夜再渲染一次**，两次必须逐字相同
            // （渲染实现只要重新开始读 Clock，这条就会红）。
            var messageAt = new DateTimeOffset(2026, 9, 26, 23, 50, 0, TimeSpan.Zero);
            var justBeforeMidnight = new MutablePromptClock(new DateTimeOffset(2026, 9, 26, 23, 59, 0, TimeSpan.FromHours(8)));
            Clock.Use(justBeforeMidnight);
            var renderedBefore = ChatHistoryTime.Render(messageAt);
            var justAfterMidnight = new MutablePromptClock(new DateTimeOffset(2026, 9, 27, 0, 1, 0, TimeSpan.FromHours(8)));
            Clock.Use(justAfterMidnight);
            var renderedAfter = ChatHistoryTime.Render(messageAt);
            Clock.Use(clock);
            Check("★ #135 历史时间渲染与“现在几点”无关（跨午夜后同一条消息逐字相同）",
                renderedBefore == renderedAfter,
                $"23:59 → {renderedBefore} / 00:01 → {renderedAfter}");

            Check("★ #135 历史时间是绝对形式（月-日 时:分），不再按“今天”省略日期",
                renderedBefore == messageAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                && renderedBefore.Contains('-', StringComparison.Ordinal)
                && renderedBefore.Contains(':', StringComparison.Ordinal),
                renderedBefore);

            // 相对信息没有丢：它由时间块承担（模型仍知道“现在是什么时候”）。
            Check("★ #135 相对时间信息没丢：提示词末尾仍有 [现在的时间] 与绝对日期",
                first.Contains("[现在的时间]", StringComparison.Ordinal)
                && first.Contains("2026-09-26 12:00", StringComparison.Ordinal));
        }
        finally
        {
            Clock.Use(originalClock);
        }
    }

    /// <summary>可改时间的假时钟：用来验证「只有时间变时前缀不动」。</summary>
    private sealed class MutablePromptClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset Now { get; set; } = start;
        public DateTimeOffset UtcNow => Now.ToUniversalTime();
        public DateTime LocalDateTime => Now.DateTime;
        public long TickCount => 0;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.CompletedTask;
    }
}
