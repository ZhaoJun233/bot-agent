using BotAgent.Services.Agent;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    /// <summary>自然对话 agent：模型写进 agentTask 的“停下 / 问进度”要归一成 // 子命令，其余照常派活。</summary>
    private static void NaturalAgentTests()
    {
        Section("自然对话 agent · 停下/进度归一（不误吞真任务）");

        (string Input, string? Expected)[] cases =
        [
            ("stop", "stop"),
            ("STOP", "stop"),
            ("停止", "stop"),
            ("帮我停止任务", "stop"),
            ("请取消吧。", "stop"),
            ("停一下", null),
            ("停下", "stop"),
            ("中断！", "stop"),
            ("status", "status"),
            ("进度呢？", "status"),
            ("帮我看下状态", null),
            ("停止监控服务", null),
            ("帮我看下服务器内存还剩多少", null),
            ("取消订阅邮件", null),
            ("", null),
        ];

        foreach (var (input, expected) in cases)
        {
            var actual = AgentCommandService.NaturalSubCommand(input);
            Check($"「{input}」→ {expected ?? "派活"}", actual == expected, $"实际 {actual ?? "null"}");
        }
    }
}
