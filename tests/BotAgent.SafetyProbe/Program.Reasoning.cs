using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Services;
using BotAgent.Services.Agent;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void ReasoningEffortTests()
    {
        Section("推理强度 · 参数独立 / 自动降级（合成传输，不连网）");
        var writeToFile = FileLog.WriteToFile;
        FileLog.WriteToFile = false;
        try
        {
            const string success = "{\"choices\":[{\"message\":{\"content\":\"synthetic-ok\"}}]}";
            var requests = new List<JsonObject>();
            var urls = new List<string?>();
            var keys = new List<string?>();
            var responses = new Queue<(int Status, string Body)>();
            var transport = new FakeHttpFetcher
            {
                OnSend = async (request, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
                    urls.Add(request.RequestUri?.AbsoluteUri);
                    keys.Add(request.Headers.Authorization?.Parameter);
                    var response = responses.Dequeue();
                    return new HttpResponseMessage((HttpStatusCode)response.Status)
                    {
                        Content = new StringContent(response.Body)
                    };
                }
            };
            var client = new OpenAiClient(new SettingsBox(new AppSettings
            {
                ApiKey = "synthetic-key", Model = "fallback-model", ModelBaseUrl = "https://example.invalid/v1"
            }), transport, new FakeHttpFetcher(), new FakeImageDownloader(), new FakeModelTransport());

            string? Run(string? effort, params (int Status, string Body)[] replies)
            {
                requests.Clear(); urls.Clear(); keys.Clear(); responses.Clear();
                foreach (var reply in replies) responses.Enqueue(reply);
                return client.CompleteChatWithReasoningAsync("selected-model", "synthetic-system",
                    new[] { ("user", "synthetic-message") }, 128, 0.3, effort,
                    baseUrlOverride: "https://example.com/v1", apiKeyOverride: "synthetic-override")
                    .GetAwaiter().GetResult();
            }

            foreach (var effort in new string?[] { null, "", "auto", "DEFAULT", " Auto " })
                Check($"默认档 {effort ?? "null"} 不发送推理参数",
                    Run(effort, (200, success)) == "synthetic-ok" && requests.Count == 1 &&
                    !requests[0].ContainsKey("reasoning_effort"));

            foreach (var effort in new[] { "none", "low", "high", "custom-level" })
                Check($"档位 {effort} 只改变参数，不改变模型",
                    Run(effort, (200, success)) == "synthetic-ok" &&
                    requests[0]["reasoning_effort"]!.GetValue<string>() == effort &&
                    requests[0]["model"]!.GetValue<string>() == "selected-model");

            foreach (var status in new[] { 400, 422 })
            {
                var result = Run("high", (status, "{\"error\":{\"param\":\"reasoning_effort\",\"message\":\"Unsupported value\"}}"), (200, success));
                var expected = requests[0].DeepClone().AsObject();
                expected.Remove("reasoning_effort");
                Check($"{status} 不支持时仅移除推理参数再试一次", result == "synthetic-ok" &&
                    requests.Count == 2 && JsonNode.DeepEquals(expected, requests[1]) &&
                    urls.All(url => url == "https://example.com/v1/chat/completions") &&
                    keys.All(key => key == "synthetic-override"));
            }
            Check("无关的参数错误不错误降级",
                Run("high", (400, "{\"error\":\"unsupported temperature\"}")) is null && requests.Count == 1);
            Check("鉴权错误不重试或降级",
                Run("high", (401, "{\"error\":\"unauthorized\"}")) is null && requests.Count == 1);
            Check("自动降级最多一次，不无限重试",
                Run("high", (400, "reasoning_effort unsupported"), (400, "reasoning_effort unsupported")) is null && requests.Count == 2);
            Check("选择推理档位仍保留原有服务端瞬时错误重试",
                Run("high", (503, "synthetic unavailable"), (200, success)) == "synthetic-ok" &&
                requests.Count == 2 && requests.All(r => r["reasoning_effort"]!.GetValue<string>() == "high"));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelled = false;
            try
            {
                client.CompleteChatWithReasoningAsync("selected-model", "synthetic-system",
                    Array.Empty<(string, string)>(), 128, 0.3, "high", cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check("取消操作向上传播，不伪装成降级成功", cancelled);

            var parse = typeof(ServerAgentRunner).GetMethod("ParseTools",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Check("非空 none 名单不授予任何工具",
                ((HashSet<string>)parse.Invoke(null, new object[] { "none" })!).Count == 0);
        }
        finally { FileLog.WriteToFile = writeToFile; }
    }
}
