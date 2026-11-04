using System;
using System.Linq;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Services.Model;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    /// <summary>
    /// #97 普通对话时效感知的提示词契约：时间基准、知识截止未知、不否认新事实、用户说法未被确认、
    /// 联网时区分「事件发生时间」与「检索时间」。
    ///
    /// 这里钉的是**我们真正能控制的东西** —— 模型看到的那段字。模型拿到这些字以后回什么，
    /// 合成桩（MockOpenAi）答不了，只能在真实模型上观察；所以本组断言同时保证「不伪造截止日期」
    /// 与「不因为怕说错而每句都加免责声明」这两条写进提示词。
    /// </summary>
    private static void TemporalAwarenessPromptTests()
    {
        Section("时效感知与不确定性（#97：时间基准 / 知识截止未知 / 不否认新事实）");
        var originalClock = Clock.Current;
        Clock.Use(new FrozenPromptClock());
        try
        {
            var request = new PromptBuilder.PromptRequest(
                SystemPrompt: "合成系统提示", BotIdentity: null, Persona: null, AiDesire: 0,
                Window: Array.Empty<ChatMessage>(), QuotableIds: Array.Empty<long>(), ProfilesText: null,
                Stickers: null, PokeContext: false, Proactive: false, MoodText: null, MusicText: null,
                LinkText: null, RecallText: null, GroupRolesText: null, VibeHint: null, SearchText: null,
                SuitabilityThreshold: 10, EnableListen: false, EnableVoice: false, VoiceMaxChars: 30,
                VoiceEagerness: 50, EnableWebSearch: false, EnableAsk: false, EnableToolRequest: false,
                ToolList: null);
            var plain = PromptBuilder.Build(request);
            var withSearch = PromptBuilder.Build(request with { EnableWebSearch = true });

            var timeStart = plain.IndexOf("\n\n[现在的时间]", StringComparison.Ordinal);
            var nextSection = timeStart < 0 ? -1 : plain.IndexOf("\n\n[", timeStart + 2, StringComparison.Ordinal);
            var timeSection = timeStart < 0
                ? string.Empty
                : nextSection < 0 ? plain[timeStart..] : plain[timeStart..nextSection];

            Check("★ 时间基准来自注入时钟（格式含日期时刻、时段词、星期与 UTC 偏移）",
                timeSection.Contains("2026-09-26 12:00", StringComparison.Ordinal) &&
                timeSection.Contains("中午", StringComparison.Ordinal) &&
                timeSection.Contains("星期六", StringComparison.Ordinal) &&
                timeSection.Contains("UTC+08:00", StringComparison.Ordinal),
                (timeSection.Length <= 200 ? timeSection : timeSection[..200]));
            Check("★ 时间基准被声明为唯一基准（不让模型拿环境或用户说法替换）",
                timeSection.Contains("唯一的时间基准", StringComparison.Ordinal));
            Check("★ 知识截止标记为未知且不伪造截止日期",
                timeSection.Contains("知识截止时间", StringComparison.Ordinal) &&
                timeSection.Contains("不确定是哪天", StringComparison.Ordinal) &&
                !System.Text.RegularExpressions.Regex.IsMatch(timeSection, "截(止|至)\\s*20\\d\\d"),
                (timeSection.Length <= 200 ? timeSection : timeSection[..200]));
            Check("★ 不因为没印象就否认新事实（禁止“没有这回事/假的”式断言）",
                timeSection.Contains("没有这回事", StringComparison.Ordinal) &&
                timeSection.Contains("是假的", StringComparison.Ordinal) &&
                timeSection.Contains("还没更新", StringComparison.Ordinal));
            Check("★ 用户说的新消息只算未确认说法，不替用户升级成官方确认",
                timeSection.Contains("不是已确认事实", StringComparison.Ordinal) &&
                timeSection.Contains("官方确认", StringComparison.Ordinal));
            Check("★ 用户报的时间不等于系统时间（跨时区/跨日不拿用户文本当基准）",
                timeSection.Contains("他报的时间也只是他说的", StringComparison.Ordinal));
            Check("时段词口径仍保留（凌晨/早上/上午/中午/下午/傍晚/晚上/深夜）",
                timeSection.Contains("凌晨/早上/上午/中午/下午/傍晚/晚上/深夜", StringComparison.Ordinal));
            Check("关闭联网时同样注入时效边界（不是只有联网才有时效感）",
                !plain.Contains("[联网搜索]", StringComparison.Ordinal) &&
                plain.Contains("知识截止时间", StringComparison.Ordinal) &&
                plain.Contains("唯一的时间基准", StringComparison.Ordinal));

            var searchStart = withSearch.IndexOf("\n\n[联网搜索]", StringComparison.Ordinal);
            var searchSection = searchStart < 0 ? string.Empty : withSearch[searchStart..];
            Check("★ 联网时区分事件发生时间与检索时间（旧资料不说成新消息）",
                searchSection.Contains("事件发生时间", StringComparison.Ordinal) &&
                searchSection.Contains("你检索的时间", StringComparison.Ordinal) &&
                searchSection.Contains("查到旧资料就说清是旧的", StringComparison.Ordinal));
            Check("时效段落有界（时间边界 + 两条时效规则 ≤ 900 字）",
                timeSection.Length is > 200 and <= 900, $"时效段 {timeSection.Length} 字");
            Check("非时效闲聊不被迫搜索（“什么时候不用搜”与日期按时钟答仍在）",
                searchSection.Contains("什么时候不用搜", StringComparison.Ordinal) &&
                searchSection.Contains("日期/时刻按[现在的时间]答", StringComparison.Ordinal));
        }
        finally
        {
            Clock.Use(originalClock);
        }
    }
}
