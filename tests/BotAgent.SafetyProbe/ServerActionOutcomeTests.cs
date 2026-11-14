using System.Net;
using BotAgent.Services;
using BotAgent.Services.Agent;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void ServerActionOutcomeTests()
    {
        Section("服务器 Agent · QQ 动作最终结果（合成模型 / 公共 RunAsync）");
        var oldLogSetting = FileLog.WriteToFile;
        FileLog.WriteToFile = false;
        try
        {
            RunCase("能力拒绝 + 结构化 final", "❌ 动作 poke 被平台能力拒绝（unsupported）。",
                "{\"final\":\"动作完成\"}", expectedSupplement: true);
            RunCase("能力拒绝 + 散文 final", "❌ 动作 poke 被平台能力拒绝（unsupported）。",
                "动作完成", expectedSupplement: true);
            RunCase("能力拒绝 + 畸形 final", "❌ 动作 poke 被平台能力拒绝（unsupported）。",
                "{bad final", expectedSupplement: true);
            RunCase("策略拒绝", "❌ 动作 like 被平台策略拒绝（QQ 动作仅支持私域 OneBot 会话）。",
                "{\"final\":\"动作完成\"}", expectedSupplement: true);
            RunCase("非法目标", "要指明对谁做（user_id 写 QQ 号，或者 sender / me）。",
                "{\"final\":\"动作完成\"}", expectedSupplement: true);
            RunCase("成功不误报", "✅ 给 20002 点了 1 个赞",
                "{\"final\":\"动作完成\"}", expectedSupplement: false);
            RunCase("执行失败不冒充策略拒绝", "❌ 点赞没成功（QQ 侧回绝）。",
                "{\"final\":\"动作完成\"}", expectedSupplement: false, failed: true);
        }
        finally
        {
            FileLog.WriteToFile = oldLogSetting;
        }

        static void RunCase(string name, string toolResult, string final, bool expectedSupplement, bool failed = false)
        {
            var replies = new Queue<string>(["{\"tool\":\"qq\",\"action\":\"like\",\"user_id\":\"sender\"}", final, final]);
            var fetcher = new FakeHttpFetcher
            {
                OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { message = new { content = replies.Dequeue() } } }
                    }))
                })
            };
            var settings = new AppSettings
            {
                ApiKey = "synthetic-key", Model = "synthetic-model", ModelBaseUrl = "https://example.invalid/v1",
                AgentServerModel = "synthetic-model", AgentServerTools = "qq", AgentServerMaxSteps = 3
            };
            var box = new SettingsBox(settings);
            var client = new OpenAiClient(box, fetcher, new FakeHttpFetcher(),
                new FakeImageDownloader(), new FakeModelTransport());
            var runner = new ServerAgentRunner(box, client, new FakeHttpFetcher(), _ => { });
            var task = new AgentTask
            {
                Id = "synthetic", SourceKey = "group:10001", Prompt = "synthetic request", Session = "synthetic",
                QqHost = new SyntheticQqActionHost(expectedSupplement
                    ? BotAgent.Domain.Tools.ToolOutcome.Failure("action_not_allowed", toolResult)
                    : failed ? BotAgent.Domain.Tools.ToolOutcome.Failure("action_failed", toolResult)
                    : BotAgent.Domain.Tools.ToolOutcome.Success(toolResult))
            };
            runner.RunAsync(task, CancellationToken.None).GetAwaiter().GetResult();
            var heading = "未执行的 QQ 动作：";
            var count = task.Text?.Split(heading, StringSplitOptions.None).Length - 1 ?? 0;
            Check(name, task.Ok && replies.Count == (final.StartsWith('{') && final != "{bad final" ? 1 : 0) && task.ToolCalls == 1
                && count == (expectedSupplement ? 1 : 0)
                && (!expectedSupplement || task.Text!.Contains(toolResult, StringComparison.Ordinal)),
                $"ok={task.Ok} toolCalls={task.ToolCalls} supplementCount={count} text={task.Text}");
        }
    }

    private sealed class SyntheticQqActionHost(BotAgent.Domain.Tools.ToolOutcome result) : IQqActionHost, BotAgent.Services.Tools.IToolExecutor
    {
        public string ContextLine => "synthetic QQ context";
        public string Id => BotAgent.Services.Tools.QqToolSpecs.ExecutorActions;
        public string Implementation => "synthetic";
        public bool LegacyPath => false;
        public Task<BotAgent.Domain.Tools.ToolOutcome> ExecuteAsync(BotAgent.Domain.Tools.ToolCall call, CancellationToken ct = default)
            => Task.FromResult(result);
        public Task<string> ExecuteAsync(QqActionSpec spec, System.Text.Json.Nodes.JsonObject args, CancellationToken ct)
            => Task.FromResult(result.ModelText!);
    }
}
