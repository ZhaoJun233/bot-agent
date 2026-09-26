using BotAgent.Adapters.Time;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Services.Model;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void SearchPromptBudgetTests()
    {
        Section("搜索提示词预算与契约（S9 长度哨兵）");
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
            var disabled = PromptBuilder.Build(request);
            var enabled = PromptBuilder.Build(request with { EnableWebSearch = true });
            var start = enabled.IndexOf("\n\n[联网搜索]", StringComparison.Ordinal);
            var search = start < 0 ? string.Empty : enabled[start..];

            Check("关闭搜索不注入搜索说明", !disabled.Contains("[联网搜索]", StringComparison.Ordinal));
            Check("搜索开关只追加对应段落，不改关闭时的既有提示", start >= 0 && enabled[..start] == disabled);
            Check("搜索说明不超过 300 字，保留 S9 总预算", search.Length > 0 && search.Length <= 300,
                $"搜索段 {search.Length} 字");
            Check("保留 JSON search/read、来源与下一轮回答契约",
                new[] { "JSON", "search", "read", "URL", "群友", "来源", "下一轮" }.All(search.Contains));
            Check("保留时效性必须查证的范围",
                new[] { "什么时候必须搜", "新闻", "天气", "价格", "汇率", "股票", "赛程", "比分",
                    "活动", "开服", "发售", "上线", "软件", "游戏", "版本", "新番", "作品", "人物",
                    "现在", "最新", "最近", "今天", "今年" }.All(search.Contains));
            Check("保留常识、计算、上下文及时钟无需联网的范围",
                new[] { "什么时候不用搜", "数学", "成语", "语法", "历史", "地理", "代码",
                    "上下文", "[现在的时间]" }.All(search.Contains)
                && (search.Contains("自己算出来", StringComparison.Ordinal) || search.Contains("可计算", StringComparison.Ordinal)));
            Check("保留具体检索、时间锚点及去重规则",
                new[] { "具体", "带上时间信息", "同一件事不要连着搜两次" }.All(search.Contains)
                && (search.Contains("不要每句话都搜", StringComparison.Ordinal) || search.Contains("别每句话都搜", StringComparison.Ordinal)));
            Check("保留失败诚实说明与后台动作规则",
                new[] { "没查到", "读不到", "如实", "旧信息", "假装", "后台动作", "reply 正常写" }.All(search.Contains));
        }
        finally
        {
            Clock.Use(originalClock);
        }
    }

    private sealed class FrozenPromptClock : IClock
    {
        public DateTimeOffset Now => new(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(8));
        public DateTimeOffset UtcNow => Now.ToUniversalTime();
        public DateTime LocalDateTime => Now.DateTime;
        public long TickCount => 0;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.CompletedTask;
    }
}
