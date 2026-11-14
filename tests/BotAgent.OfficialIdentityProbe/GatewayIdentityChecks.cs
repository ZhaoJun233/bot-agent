using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Platforms;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Platforms;
using BotAgent.Platforms.Net;
using BotAgent.Services;
using BotAgent.Services.Official;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

internal static class GatewayIdentityChecks
{
    private static PlatformOptions Options() => new() { OfficialEnabled = true, OfficialAppId = "app-A",
        OfficialAppSecret = "synthetic-secret", OfficialApiBase = "https://example.com", OfficialTokenUrl = "https://example.com/token" };
    private static OfficialIdentityKey Target(string id = "same", string app = "app-A", bool group = true)
        => new(new(app, group ? OfficialIdentityKind.Group : OfficialIdentityKind.C2cUser), id);
    private static OfficialIdentityKey Member(string group)
        => new(new("app-A", OfficialIdentityKind.GroupMember, new(PlatformId.QqOfficial, "app-A", ConversationKind.GroupChat, group)), "same");
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("gateway identity assertion"); }
    private static JsonObject Message(string group = "same", string id = "message") => new()
    {
        ["id"] = id, ["group_openid"] = group, ["author"] = new JsonObject { ["member_openid"] = "same" },
        ["content"] = "synthetic", ["timestamp"] = PlatformClock.Current.Now.ToString("O")
    };
    private static void Dispatch(OfficialBotGateway gateway, JsonObject message)
        => typeof(OfficialBotGateway).GetMethod("HandleDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gateway, ["GROUP_AT_MESSAGE_CREATE", message, null]);
    private static IDisposable Ingress(OfficialBotGateway gateway, IOfficialIdentityMap ids, long target)
    {
        QqChatMessage? received = null;
        void Capture(QqChatMessage message) => received = message;
        gateway.MessageReceived += Capture;
        Dispatch(gateway, Message());
        gateway.MessageReceived -= Capture;
        return PlatformSourceExecution.Enter(OfficialProbeContext.Source(gateway, true, target, received!.MessageId));
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("gateway: exact target type and application before HTTP", async () =>
        {
            using var dir = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(dir.Map);
            var valid = ids.AliasFor(Target());
            var wrongType = ids.AliasFor(Target(group: false));
            var wrongApp = ids.AliasFor(Target(app: "app-B"));
            var member = ids.AliasFor(Member("same"));
            var http = new IdentityHttp();
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(Options()), http, () => ids, _ => { }, () => OfficialProbeContext.OpenLedger(dir.Map));
            using var source = Ingress(gateway, ids, valid);
            Require((await gateway.SendTextAsync(true, valid, "synthetic")).Ok && http.Messages == 1);
            foreach (var denied in new[] { wrongType, wrongApp, member, valid + 100 })
                Require(!(await gateway.SendTextAsync(true, denied, "synthetic")).Ok);
            Require(http.Messages == 1 && http.Tokens == 1);
        });
        await test("gateway: app retirement cannot revive by restoring settings", async () =>
        {
            using var dir = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(dir.Map);
            var target = ids.AliasFor(Target());
            var settings = Options();
            var http = new IdentityHttp();
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(settings), http, () => ids, _ => { }, () => OfficialProbeContext.OpenLedger(dir.Map));
            using var source = Ingress(gateway, ids, target);
            settings.OfficialAppId = "app-B";
            Require(!(await gateway.SendTextAsync(true, target, "synthetic")).Ok);
            settings.OfficialAppId = "app-A";
            Require(!(await gateway.SendTextAsync(true, target, "synthetic")).Ok && http.Tokens == 0 && http.Messages == 0);
        });
        await test("gateway: app change during token preparation stops message commit", async () =>
        {
            using var dir = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(dir.Map);
            var target = ids.AliasFor(Target());
            var settings = Options();
            var http = new IdentityHttp { AfterToken = () => settings.OfficialAppId = "app-B" };
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(settings), http, () => ids, _ => { }, () => OfficialProbeContext.OpenLedger(dir.Map));
            using var source = Ingress(gateway, ids, target);
            Require(!(await gateway.SendTextAsync(true, target, "synthetic")).Ok && http.Messages == 0 && http.Tokens == 1);
        });
        await test("gateway: successful send with failed receipt indexing is never repeated", async () =>
        {
            using var dir = new SyntheticDirectory();
            var ids = new ReceiptFailure(new OfficialIdentityMap(dir.Map));
            var target = ids.AliasFor(Target());
            var http = new IdentityHttp();
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(Options()), http, () => ids, _ => { }, () => OfficialProbeContext.OpenLedger(dir.Map));
            using var source = Ingress(gateway, ids, target);
            var sent = await gateway.SendTextAsync(true, target, "synthetic");
            Require(sent.Ok && sent.MessageId == 0 && http.Messages == 1 && gateway.LastErrorCode == "identity_receipt_unavailable");
        });
        await test("gateway: unavailable identity store stays visible without network or raw error", async () =>
        {
            var logs = new List<string>();
            var http = new IdentityHttp();
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(Options()), http,
                () => throw new InvalidDataException("synthetic-sensitive-canary"), logs.Add,
                () => throw new InvalidOperationException("identity_should_fail_first"));
            using var adapter = new QqChatSourcePlatformAdapter(gateway, new(PlatformId.QqOfficial, AccountScope.Legacy),
                PlatformCapabilities.QqOfficial, "synthetic", "synthetic");
            await gateway.StartAsync();
            Require(!gateway.IsConnected && adapter.LastErrorCode == "identity_store_unavailable");
            Require(!(await gateway.SendTextAsync(true, 10001, "synthetic")).Ok && http.Messages == 0 && http.Tokens == 0);
            Require(logs.Count > 0 && logs.All(value => !value.Contains("synthetic-sensitive-canary", StringComparison.Ordinal)));
        });
        await test("gateway: malformed message and target fields allocate no identity", () =>
        {
            using var dir = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(dir.Map);
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(Options()), new IdentityHttp(), () => ids, _ => { }, () => OfficialProbeContext.OpenLedger(dir.Map));
            var callbacks = 0;
            gateway.MessageReceived += _ => callbacks++;
            foreach (var field in new[] { "id", "group_openid" })
                foreach (JsonNode? invalid in new JsonNode?[] { null, JsonValue.Create(42), JsonValue.Create(" "), JsonValue.Create("bad\nid") })
                {
                    var message = Message();
                    message[field] = invalid;
                    Dispatch(gateway, message);
                }
            Require(callbacks == 0 && ids.Count == 0);
            return Task.CompletedTask;
        });
        await test("gateway: typed identities alone produce reliable inbound callbacks", () =>
        {
            using var dir = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(dir.Map);
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(Options()), new IdentityHttp(), () => ids, _ => { }, () => OfficialProbeContext.OpenLedger(dir.Map));
            QqChatMessage? received = null;
            gateway.MessageReceived += value => received = value;
            Dispatch(gateway, Message());
            Require(received is { IsReliableIdentity: true } && received.UserId != received.GroupId && ids.Count == 3);
            return Task.CompletedTask;
        });
        await test("adapter: unknown and unreliable official input cannot reach host callbacks", () =>
        {
            using var dir = new SyntheticDirectory();
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(Options()), new IdentityHttp(), () => new OfficialIdentityMap(dir.Map), _ => { }, () => OfficialProbeContext.OpenLedger(dir.Map));
            using var adapter = new QqChatSourcePlatformAdapter(gateway, new(PlatformId.QqOfficial, AccountScope.Legacy),
                PlatformCapabilities.QqOfficial, "synthetic", "synthetic");
            var callbacks = 0;
            adapter.RichInboundReceived += (_, _) => callbacks++;
            var emit = (Action<QqChatMessage>)typeof(OfficialBotGateway).GetField("MessageReceived", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
            var message = new QqChatMessage(10001, true, 10002, 10003, "synthetic", "synthetic", DateTimeOffset.UtcNow, true);
            emit(message);
            emit(message with { IsReliableIdentity = false });
            Require(callbacks == 0 && adapter.LastErrorCode == "identity_unverified");
            emit(message with { IsReliableIdentity = true });
            Require(callbacks == 1);
            return Task.CompletedTask;
        });
        await test("gateway: profiles require participant type and exact group", () =>
        {
            using var dir = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(dir.Map);
            var group = ids.AliasFor(Target());
            var member = ids.AliasFor(Member("same"));
            var foreign = ids.AliasFor(Member("other"));
            var c2c = ids.AliasFor(Target(group: false));
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(Options()), new IdentityHttp(), () => ids, _ => { }, () => OfficialProbeContext.OpenLedger(dir.Map));
            var context = new ConversationId(PlatformId.QqOfficial, AccountScope.Legacy, ConversationKind.GroupChat, group.ToString());
            Require(gateway.ResolveProfileSubject(member, context) == "same");
            Require(gateway.ResolveProfileSubject(foreign, context) is null && gateway.ResolveProfileSubject(group, context) is null);
            Require(gateway.ResolveProfileSubject(c2c, context with { Kind = ConversationKind.PrivateChat, NativeTargetId = c2c.ToString() }) == "same");
            return Task.CompletedTask;
        });
        await test("gateway: verified migration preserves inbound aliases and profile identity", () =>
        {
            using var dir = new SyntheticDirectory();
            File.WriteAllText(dir.Map, """{"map":{"group":8000000000000011,"same":8000000000000012}}""");
            var ids = new OfficialIdentityMap(dir.Map);
            ids.ImportVerifiedLegacy(ids.SnapshotSha256!, [new(Target("group"), 8000000000000011, new string('a', 64)),
                new(Member("group"), 8000000000000012, new string('a', 64))]);
            using var gateway = new OfficialBotGateway(new PlatformSettingsBox(Options()), new IdentityHttp(), () => ids, _ => { }, () => OfficialProbeContext.OpenLedger(dir.Map));
            QqChatMessage? received = null;
            gateway.MessageReceived += value => received = value;
            Dispatch(gateway, Message("group"));
            Require(received is { IsReliableIdentity: true, GroupId: 8000000000000011, UserId: 8000000000000012 });
            Require(gateway.ResolveProfileSubject(8000000000000012,
                new(PlatformId.QqOfficial, AccountScope.Legacy, ConversationKind.GroupChat, "8000000000000011")) == "same");
            return Task.CompletedTask;
        });
    }

    private sealed class ReceiptFailure(IOfficialIdentityMap inner) : IOfficialIdentityMap
    {
        public int Count => inner.Count;
        public long AliasFor(OfficialIdentityKey identity) => identity.Scope.Direction == OfficialMessageDirection.Outbound
            ? throw new IOException("synthetic receipt failure") : inner.AliasFor(identity);
        public string? OriginalOf(long alias, OfficialIdentityScope scope) => inner.OriginalOf(alias, scope);
    }

    private sealed class IdentityHttp : IPlatformHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public int Tokens { get; private set; }
        public int Messages { get; private set; }
        public Action? AfterToken { get; init; }
        private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        { Tokens++; AfterToken?.Invoke(); return Task.FromResult(Reply("""{"access_token":"synthetic-token","expires_in":7200}""")); }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        { Messages++; return Task.FromResult(Reply("""{"id":"synthetic-ack"}""")); }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new InvalidOperationException("unexpected HTTP");
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
    }
}
