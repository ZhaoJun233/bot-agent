using System.Reflection;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S64 OneBot 动作响应的 message_id 两种写法（#112）。
///
/// NapCat 不同版本/不同动作回 `message_id` 的写法不一致：有的是 JSON 数字，有的是字符串。
/// 机器人靠它给自己的消息对号（引用原文兜底、自回显守卫），写错一侧会静默退化成 0 ——
/// 现象是「引用能挂上但引用不了原文」，没有任何报错。
///
/// 这是宿主里的私有纯函数，黑盒场景打不到；从被测机器人的输出目录加载程序集后反射调用，
/// 仍然只使用合成输入，不启动机器人进程。
/// </summary>
public static partial class Program
{
    private static Task RunOneBotWireScenarioAsync()
    {
        Section("S64 OneBot 动作响应 message_id 两种写法（数字 / 字符串 / 缺失 / 非数字）");

        var hostDirectory = Path.GetDirectoryName(BotDll)!;
        var assembly = Assembly.LoadFrom(Path.Combine(hostDirectory, "BotAgent.Platforms.dll"));
        var type = assembly.GetType("BotAgent.Services.OneBot.OneBotActions")
            ?? throw new InvalidOperationException("onebot_actions_missing");
        var method = type.GetMethod("ReadMessageId", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("read_message_id_missing");

        long Read(string json)
            => (long)(method.Invoke(null, new object?[] { JsonNode.Parse(json) }) ?? 0L);

        Check("message_id 是 JSON 数字时原样读出", Read("""{"data":{"message_id":12345}}""") == 12345);
        Check("★★ message_id 是字符串时也能读出（NapCat 的另一种写法）",
            Read("""{"data":{"message_id":"67890"}}""") == 67890,
            "字符串写法解析失败会让机器人把自己的消息记成 0，引用随之退化成找不到原文");
        Check("message_id 缺失时退化成 0 而不是抛异常", Read("""{"data":{}}""") == 0);
        Check("message_id 是非数字字符串时退化成 0 而不是抛异常", Read("""{"data":{"message_id":"not-a-number"}}""") == 0);

        return Task.CompletedTask;
    }
}
