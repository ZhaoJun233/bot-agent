using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Model.Net;
using BotAgent.Services.Model;

internal static class PrivateReplyPromptChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string marker = "[私聊：一对一对话]";
        var window = new[] { new ChatMessage { Role = MessageRole.Peer, Text = "你好", SenderId = 10001,
            Timestamp = DateTimeOffset.UnixEpoch } };
        var request = new PromptBuilder.PromptRequest("Synthetic safety policy", null, null, 0, window, [],
            null, null, false, false, null, null, null, null, null, null, null, 100,
            false, false, 80, 0, false, false, false);
        var group = PromptBuilder.Build(request);
        var direct = PromptBuilder.Build(request with { IsPrivateConversation = true });
        check(direct.Contains(marker) && direct.Contains("非空的 reply") && direct.Contains("无需 @"),
            "private prompt addresses normal greetings without mentions");
        check(direct.Contains("安全和工具权限边界") && direct.Contains("严格只输出一行合法 JSON") && direct.Contains("silent"),
            "private prompt preserves safety, structured output and explicit silence");
        check(!direct.Contains("别人闲聊、与你无关 → 沉默") && !direct.Contains("当前发言欲望 desire="),
            "private reactive prompt excludes group suitability and desire gating");
        check(!group.Contains(marker) && group.Contains("别人闲聊、与你无关 → 沉默")
            && group == PromptBuilder.Build(request with { IsPrivateConversation = false }),
            "group default and explicit group prompts are identical");
        check(PromptBuilder.Build(request with { Proactive = true }) ==
            PromptBuilder.Build(request with { Proactive = true, IsPrivateConversation = true }),
            "private guidance does not change proactive turns");

        var bodies = new List<JsonObject>();
        var completion = "{\"suitability\":100,\"reply\":\"你好\"}";
        var http = new FakeHttp(async (message, ct) =>
        {
            bodies.Add(JsonNode.Parse(await message.Content!.ReadAsStringAsync(ct))!.AsObject());
            var response = new JsonObject { ["choices"] = new JsonArray(new JsonObject {
                ["message"] = new JsonObject { ["content"] = completion } }) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content =
                new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        var options = new ModelOptions { ApiKey = "synthetic-key", BaseUrl = "https://example.com/v1",
            Model = "example-model" };
        var images = new ImageDownloader(http);
        var transport = new ModelTransport(() => options, http, http, images, http.Timeout);
        var client = new OpenAiClient(() => options, http, http, images, transport);
        var reply = await client.CompleteAsync(window, isPrivateConversation: true);
        check(reply.Reply == "你好" && reply.Suitability == 100, "private structured greeting survives normal parsing");
        check(System(bodies[^1]).Contains(marker), "private channel reaches actual HTTP system prompt");
        await client.CompleteAsync(window);
        check(!System(bodies[^1]).Contains(marker) && System(bodies[^1]).Contains("别人闲聊、与你无关 → 沉默"),
            "subsequent group request does not retain private guidance");
        completion = "{\"suitability\":100,\"reply\":\"\",\"action\":\"silent\"}";
        var silent = await client.CompleteAsync(window, isPrivateConversation: true);
        check(string.IsNullOrEmpty(silent.Reply) && silent.Action.ToString() == "Silent",
            "private explicit silent is not replaced with greeting");
        completion = "{\"reply\":{}}";
        var malformed = await client.CompleteAsync(window, isPrivateConversation: true);
        check(string.IsNullOrEmpty(malformed.Reply), "private malformed completion remains fail closed");
        completion = "{\"suitability\":100,\"reply\":\"\"}";
        var empty = await client.CompleteAsync(window, isPrivateConversation: true);
        check(string.IsNullOrEmpty(empty.Reply), "private empty output is not rewritten into synthetic reply");
    }

    private static string System(JsonObject body) => body["messages"]!.AsArray()
        .Single(message => message?["role"]?.GetValue<string>() == "system")!["content"]!.GetValue<string>();
}
