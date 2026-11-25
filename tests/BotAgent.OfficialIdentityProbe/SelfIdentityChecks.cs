using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Platforms;
using BotAgent.Platforms.Net;
using BotAgent.Services.Official;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

internal static class SelfIdentityChecks
{
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Dispatch(OfficialBotGateway gateway, string kind, JsonObject data)
        => typeof(OfficialBotGateway).GetMethod("HandleDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gateway, [kind, data, null]);
    private static async Task Receive(OfficialBotGateway gateway, JsonObject data, string kind = "GROUP_MESSAGE_CREATE")
    {
        var method = typeof(OfficialBotGateway).GetMethod("HandleDispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null) Dispatch(gateway, kind, data); // Same input can demonstrate the prior gateway's behavior.
        else await (Task)method.Invoke(gateway, [kind, data, null, CancellationToken.None])!;
    }
    private static JsonObject Message(string id, string group = "group-A", string sender = "human", string? mention = "bot-A")
        => new() { ["id"] = id, ["group_openid"] = group,
            ["author"] = new JsonObject { ["member_openid"] = sender, ["user_openid"] = sender, ["bot"] = false },
            ["mentions"] = new JsonArray(new JsonObject { ["member_openid"] = mention }),
            ["content"] = "<@bot-A> synthetic", ["timestamp"] = PlatformClock.Current.Now.ToString("O") };

    public static async Task RunAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("self: verified group member matches current mentions and persists typed alias", async () =>
        {
            using var f = new Fixture();
            await Receive(f.Gateway, Message("one"));
            Require(f.Messages is [{ MentionedSelf: true, Text: "synthetic" }], "current schema did not identify self");
            var key = new OfficialIdentityKey(new("app-A", OfficialIdentityKind.BotSelf,
                new(PlatformId.QqOfficial, "app-A", ConversationKind.GroupChat, "group-A")), "bot-A");
            var reopened = new OfficialIdentityMap(f.Directory.Map);
            Require(reopened.TryGetAlias(key, out _) && f.Http.StateCalls == 1, "self proof was not scoped and persisted");
            Require(f.Http.Auth == "QQBot synthetic-token" && f.Http.Method == HttpMethod.Get, "self query transport mismatch");
        });
        await test("self: is_you cannot assert self without verified identity", async () =>
        {
            using var f = new Fixture(); f.Http.Status = HttpStatusCode.Forbidden;
            var input = Message("one", mention: null); input["mentions"] = new JsonArray(new JsonObject { ["is_you"] = true });
            await Receive(f.Gateway, input);
            Require(f.Messages is [{ MentionedSelf: false }], "legacy flag granted an unverified mention");
            await Receive(f.Gateway, Message("two"), "GROUP_AT_MESSAGE_CREATE");
            Require(f.Messages[1].MentionedSelf && f.Http.StateCalls == 1, "permission denial blocked @ or retried query");
        });
        await test("self: other bot retains normal ingress and duplicate handling", async () =>
        {
            using var f = new Fixture(); var input = Message("one", sender: "other-bot"); input["author"]!["bot"] = true;
            await Receive(f.Gateway, input);
            Require(f.Messages is [{ MentionedSelf: true, Text: "synthetic" }] && f.Http.StateCalls == 1, "other bot did not retain normal ingress");
            Require(f.Logs.All(line => !line.Contains("reason=self_message", StringComparison.Ordinal) && !line.Contains("reason=bot_message", StringComparison.Ordinal)), "other bot rejected or misclassified as self");
            await Receive(f.Gateway, input);
            Require(f.Messages.Count == 1, "other bot bypassed duplicate detection");
        });
        await test("self: verified sender echo ignores bot flag and leaves retry identity free", async () =>
        {
            foreach (var flag in new bool?[] { true, false, null })
            {
                using var f = new Fixture(); var input = Message("one", sender: "bot-A");
                input["author"]!["bot"] = flag;
                await Receive(f.Gateway, input, "GROUP_AT_MESSAGE_CREATE");
                Require(f.Messages.Count == 0 && f.Ids.Count == 1, "self echo reached callback or peer/message allocation");
                Require(f.Logs.Any(line => line.Contains("reason=self_message", StringComparison.Ordinal)), "self reason missing");
                await Receive(f.Gateway, Message("one"), "GROUP_AT_MESSAGE_CREATE");
                Require(f.Messages.Count == 1, "self echo poisoned duplicate detection");
            }
        });
        await test("self: group scopes, conflicting fields and legacy flags stay separate", async () =>
        {
            using var f = new Fixture();
            f.Http.ForGroup = path => path.Contains("group-B", StringComparison.Ordinal) ? "bot-B" : "bot-A";
            await Receive(f.Gateway, Message("one"));
            await Receive(f.Gateway, Message("two", group: "group-B"));
            var conflict = Message("three"); conflict["mentions"]![0]!["id"] = "other"; conflict["mentions"]![0]!["is_you"] = true;
            await Receive(f.Gateway, conflict);
            var oldSchema = Message("four"); oldSchema["mentions"] = new JsonArray(new JsonObject { ["id"] = "bot-A", ["is_you"] = true });
            await Receive(f.Gateway, oldSchema);
            Require(f.Messages.Select(m => m.MentionedSelf).SequenceEqual(new[] { true, false, false, true }), "scope or legacy flag overrode identity");
        });
        await test("self: expiry and denied refresh discard old proof without query storms", async () =>
        {
            using var f = new Fixture(); await Receive(f.Gateway, Message("one"));
            clock.Now += TimeSpan.FromMinutes(5); f.Http.Status = HttpStatusCode.Forbidden;
            await Receive(f.Gateway, Message("two")); await Receive(f.Gateway, Message("three"));
            Require(f.Messages.Skip(1).All(m => !m.MentionedSelf) && f.Http.StateCalls == 2, "expired proof or failure retry reused");
            clock.Now += TimeSpan.FromMinutes(1); f.Http.Status = HttpStatusCode.OK;
            await Receive(f.Gateway, Message("four"));
            Require(f.Messages[^1].MentionedSelf && f.Http.StateCalls == 3, "negative cache did not expire");
        });
        await test("self: group removal and reconnect discard cached proof", async () =>
        {
            using var f = new Fixture(); await Receive(f.Gateway, Message("one"));
            f.Http.ForGroup = _ => "bot-B";
            Dispatch(f.Gateway, "GROUP_DEL_ROBOT", new() { ["group_openid"] = "group-A" });
            clock.Now += TimeSpan.FromMilliseconds(1); // A new event, after the durable relationship revocation barrier.
            await Receive(f.Gateway, Message("two"));
            typeof(OfficialBotGateway).GetMethod("SetConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Gateway, [false]);
            f.Http.ForGroup = _ => "bot-A"; await Receive(f.Gateway, Message("three"));
            Require(f.Messages.Select(m => m.MentionedSelf).SequenceEqual(new[] { true, false, true }) && f.Http.StateCalls == 3,
                "lifecycle reused old self proof");
        });
        await test("self: restart does not use persisted alias as current proof", async () =>
        {
            using var f = new Fixture(); await Receive(f.Gateway, Message("one")); f.Gateway.Dispose();
            using var restarted = new OfficialBotGateway(new PlatformSettingsBox(Fixture.Options()), f.Http, () => new OfficialIdentityMap(f.Directory.Map), _ => { },
                () => OfficialProbeContext.OpenLedger(f.Directory.Map));
            var messages = new List<QqChatMessage>(); restarted.MessageReceived += messages.Add;
            f.Http.Status = HttpStatusCode.Forbidden; await Receive(restarted, Message("two"));
            Require(messages is [{ MentionedSelf: false }] && f.Http.StateCalls == 2, "restart trusted persisted proof");
        });
        await test("self: removal during HTTP cannot reintroduce pending message", async () =>
        {
            using var f = new Fixture();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Http.BeforeReply = async () => { started.SetResult(); await release.Task; };
            var task = Receive(f.Gateway, Message("one"));
            if (task.IsCompleted) { await task; Require(false, "no scoped self lookup performed"); }
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Dispatch(f.Gateway, "GROUP_DEL_ROBOT", new() { ["group_openid"] = "group-A" });
            release.SetResult(); await task;
            Require(f.Messages.Count == 0, "retired query resurrected a message");
        });
        await test("self: malformed and oversized replies never become self proof", async () =>
        {
            foreach (var body in new[] { "{}", "{", "[]", "{\"member_openid\":42}", "{\"member_openid\":\" \"}",
                "{\"member_openid\":\"bot-A\",\"padding\":\"" + new string('x', 17000) + "\"}" })
            {
                using var f = new Fixture(); f.Http.Body = body; var input = Message("one"); input["mentions"]![0]!["is_you"] = true;
                await Receive(f.Gateway, input);
                Require(f.Messages is [{ MentionedSelf: false }], "bad response yielded a self mention");
                Require(f.Logs.All(value => !value.Contains(body, StringComparison.Ordinal)), "response leaked into log");
            }
        });
        await test("self: only verified leading self mention is stripped", async () =>
        {
            using var f = new Fixture();
            string[] inputs = ["<@other> <@bot-A> text", "<@all> text", "@label text", "<@!bot-A> <@other> text"];
            for (var i = 0; i < inputs.Length; i++) { var input = Message("m" + i); input["content"] = inputs[i]; await Receive(f.Gateway, input); }
            Require(f.Messages.Select(m => m.Text).SequenceEqual(new[] { inputs[0], inputs[1], inputs[2], "<@other> text" }), "unrelated leading text stripped");
        });
        await test("self: malformed sender has no effects while C2C bot retains ingress", async () =>
        {
            using var f = new Fixture(); var invalid = Message("one"); invalid["author"] = null;
            await Receive(f.Gateway, invalid);
            Require(f.Messages.Count == 0 && f.Http.StateCalls == 0 && f.Ids.Count == 0, "invalid subject caused activity");
            var c2c = Message("two"); c2c["author"]!["bot"] = true; await Receive(f.Gateway, c2c, "C2C_MESSAGE_CREATE");
            Require(f.Messages.Count == 1 && !f.Messages[0].IsGroup && f.Http.StateCalls == 0 && f.Ids.Count == 2, "C2C bot did not retain normal ingress");
        });
        await test("self: unknown and other-group bot identities keep normal ingress", async () =>
        {
            using var f = new Fixture(); f.Http.ForGroup = _ => "bot-B";
            var input = Message("one", sender: "bot-A"); input["author"]!["bot"] = true;
            await Receive(f.Gateway, input);
            Require(f.Messages is [{ MentionedSelf: false }], "other group's bot treated as self");
            f.Http.Status = HttpStatusCode.Forbidden;
            var unknown = Message("two", group: "group-B", sender: "bot-B"); unknown["author"]!["bot"] = true;
            await Receive(f.Gateway, unknown, "GROUP_AT_MESSAGE_CREATE");
            Require(f.Messages.Count == 2 && f.Messages[1].MentionedSelf, "unknown bot identity rejected or cross-group proof reused");
        });
        await test("self: timed out query leaves @ available and is negatively cached", async () =>
        {
            using var f = new Fixture(); f.Http.WaitUntilCanceled = true;
            await Receive(f.Gateway, Message("one"), "GROUP_AT_MESSAGE_CREATE").WaitAsync(TimeSpan.FromSeconds(6));
            await Receive(f.Gateway, Message("two"));
            Require(f.Messages.Count == 2 && f.Messages[0].MentionedSelf && !f.Messages[1].MentionedSelf && f.Http.StateCalls == 1,
                "query timeout blocked ingress or repeatedly called API");
        });
        await test("self: application changes during response prevent proof and callback", async () =>
        {
            using var f = new Fixture(); f.Http.BeforeReply = () => { f.Settings.OfficialAppId = "app-B"; return Task.CompletedTask; };
            await Receive(f.Gateway, Message("one"));
            Require(f.Messages.Count == 0 && f.Ids.Count == 0, "old application response escaped retirement");
        });
        await test("self: bounded cache rechecks evicted groups", async () =>
        {
            using var f = new Fixture();
            for (var i = 0; i < 257; i++)
            {
                await Receive(f.Gateway, Message("m" + i, group: "group-" + i));
                clock.Now += TimeSpan.FromMilliseconds(1);
            }
            await Receive(f.Gateway, Message("new", group: "group-0"));
            Require(f.Http.StateCalls == 258 && f.Messages.Count == 258, "self cache exceeded its bounded contract");
        });
    }

    private sealed class Fixture : IDisposable
    {
        public SyntheticDirectory Directory { get; } = new();
        public OfficialIdentityMap Ids { get; }
        public PlatformOptions Settings { get; } = Options();
        public SelfHttp Http { get; } = new();
        public OfficialBotGateway Gateway { get; }
        public List<QqChatMessage> Messages { get; } = new();
        public List<string> Logs { get; } = new();
        public static PlatformOptions Options() => new() { OfficialEnabled = true, OfficialAppId = "app-A",
            OfficialAppSecret = "synthetic-secret", OfficialApiBase = "https://example.com", OfficialTokenUrl = "https://example.com/token" };
        public Fixture() { Ids = new(Directory.Map); Gateway = new(new PlatformSettingsBox(Settings), Http, () => Ids, Logs.Add,
            () => OfficialProbeContext.OpenLedger(Directory.Map)); Gateway.MessageReceived += Messages.Add; }
        public void Dispose() { Gateway.Dispose(); Directory.Dispose(); }
    }
    private sealed class SelfHttp : IPlatformHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(3);
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Body { get; set; }
        public Func<string, string> ForGroup { get; set; } = _ => "bot-A";
        public Func<Task>? BeforeReply { get; set; }
        public bool WaitUntilCanceled { get; set; }
        public int StateCalls { get; private set; }
        public string? Auth { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"synthetic-token","expires_in":7200}""") });
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            StateCalls++; Auth = request.Headers.GetValues("Authorization").Single(); Method = request.Method;
            if (WaitUntilCanceled) await Task.Delay(System.Threading.Timeout.Infinite, ct);
            if (BeforeReply is not null) await BeforeReply();
            return new(Status) { Content = new StringContent(Body ?? new JsonObject { ["member_openid"] = ForGroup(request.RequestUri!.AbsolutePath) }.ToJsonString()) };
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new InvalidOperationException("unauthenticated query");
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
    }
}
