using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Platforms;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Platforms;
using BotAgent.Services;
using BotAgent.Adapters.Time;
using Microsoft.Data.Sqlite;

using var databaseTemp = new TempDirectory();
Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", databaseTemp.Path);
AppDatabase.Initialize();

var failures = 0;
async Task Test(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
SettingsBox Settings() => new(new AppSettings { FeishuEnabled = true, FeishuAppId = "app-a", FeishuAppSecret = "secret-a", FeishuApiBase = "https://a.example.com", FeishuVerificationToken = "verify-synthetic", FeishuWhitelist = "*" });
OutboundMessage Message() => new(new ConversationId(PlatformId.Feishu, AccountScope.Default, ConversationKind.GroupChat, "chat-synthetic"), "synthetic");
string Webhook(string id) => new JsonObject
{
    ["token"] = "verify-synthetic",
    ["header"] = new JsonObject { ["event_id"] = id, ["event_type"] = "im.message.receive_v1" },
    ["event"] = new JsonObject
    {
        ["sender"] = new JsonObject { ["sender_id"] = new JsonObject { ["open_id"] = "user-synthetic" } },
        ["message"] = new JsonObject { ["message_id"] = id, ["chat_id"] = "chat-synthetic", ["chat_type"] = "group", ["content"] = "{\"text\":\"synthetic\"}" }
    }
}.ToJsonString();

AppSettings PlatformSettings() => new()
{
    OneBotProtocol = "Http", OneBotAddress = "https://onebot.example.com", OneBotToken = "onebot-synthetic",
    QuickLoginUin = " 10001 ", OfficialEnabled = true, OfficialChatEnabled = false,
    OfficialAppId = "official-synthetic", OfficialAppSecret = "official-secret-synthetic", OfficialSandbox = true,
    OfficialWhitelistGroups = "10002", OfficialWhitelistPrivates = "10003",
    OfficialApiBase = "https://official.example.com", OfficialTokenUrl = "https://official.example.com/token",
    FeishuEnabled = true, FeishuAppId = "feishu-synthetic", FeishuAppSecret = "feishu-secret-synthetic",
    FeishuVerificationToken = "verify-synthetic", FeishuEncryptKey = "encrypt-synthetic",
    FeishuWhitelist = "chat-synthetic", FeishuApiBase = "https://feishu.example.com",
    LocalChannelIds = "local-synthetic", PrivateChatEnabled = false, PlatformSwitchSchemaVersion = 1,
    WhitelistGroups = "10004", WhitelistPrivates = "10005", MessageWhitelist = "10006",
    PlatformPolicies = [new PlatformPolicySettings
    {
        PlatformId = "feishu", AccountScope = "feishu-synthetic", Enabled = true, ChatEnabled = false,
        GroupWhitelist = "chat-synthetic", PrivateWhitelist = "user-synthetic",
        FeatureOverrides = new() { ["image"] = false }, AllowedActions = ["send"]
    }]
};
void RequireSamePlatformView(AppSettings settings, PlatformOptions view)
{
    foreach (var property in typeof(PlatformOptions).GetProperties())
        Require(Equals(property.GetValue(view), typeof(AppSettings).GetProperty(property.Name)!.GetValue(settings)),
            $"base/derived property differs: {property.Name}");
}

await Test("platform accessor shares derived settings, policies and all white lists", () =>
{
    var settings = PlatformSettings();
    var box = new SettingsBox(settings);
    var accessor = (IPlatformSettingsAccessor)box;
    Require(ReferenceEquals(settings, accessor.Current), "accessor replaced the published settings");
    RequireSamePlatformView(settings, accessor.Current);
    Require(ReferenceEquals(settings.PlatformPolicies, accessor.Current.PlatformPolicies), "policy list differs by view");
    accessor.Current.FeishuWhitelist = "changed-synthetic";
    accessor.Current.OfficialChatEnabled = true;
    Require(settings.FeishuWhitelist == "changed-synthetic" && settings.OfficialChatEnabled, "base mutation invisible to derived view");
    Require(settings.NormalizedUin == "10001" && settings.UinOrZero == 10001, "derived login values changed");
    var defaults = new AppSettings();
    Require(defaults.PrivateChatEnabled && defaults.OfficialChatEnabled && !defaults.OfficialEnabled && !defaults.FeishuEnabled
        && defaults.OneBotProtocol == "ForwardWebSocket" && defaults.OneBotAddress == "ws://127.0.0.1:3001"
        && defaults.PlatformPolicies.Count == 0 && defaults.PlatformSwitchSchemaVersion == 0, "platform defaults changed");
    return Task.CompletedTask;
});

await Test("platform JSON preserves original secret exclusions and persisted fields for both views", () =>
{
    var settings = PlatformSettings();
    var ignored = new[] { "OneBotToken", "OfficialAppSecret", "FeishuAppSecret", "FeishuEncryptKey", "NormalizedUin", "UinOrZero" };
    foreach (var camelCase in new[] { false, true })
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = camelCase ? JsonNamingPolicy.CamelCase : null };
        var platformJson = JsonSerializer.Serialize<PlatformOptions>(settings, options);
        var headlessJson = JsonSerializer.Serialize(settings, options);
        var platform = JsonNode.Parse(platformJson)!.AsObject();
        var headless = JsonNode.Parse(headlessJson)!.AsObject();
        foreach (var name in ignored)
        {
            var key = options.PropertyNamingPolicy?.ConvertName(name) ?? name;
            Require(!platform.ContainsKey(key) && !headless.ContainsKey(key), $"excluded property serialized: {name}");
        }
        foreach (var property in typeof(PlatformOptions).GetProperties().Where(p => !ignored.Contains(p.Name)))
        {
            var key = options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
            Require(platform.ContainsKey(key) && headless.ContainsKey(key) && JsonNode.DeepEquals(platform[key], headless[key]),
                $"persisted property missing or differs: {property.Name}");
        }
        var reloaded = JsonSerializer.Deserialize<AppSettings>(headlessJson, options)!;
        RequireSamePlatformView(reloaded, reloaded);
        Require(reloaded.FeishuVerificationToken == "verify-synthetic" && reloaded.PlatformPolicies.Single().GroupWhitelist == "chat-synthetic",
            "verification token or nested policy did not round trip");
        var injected = "{\"OneBotToken\":\"injected-synthetic\",\"OfficialAppSecret\":\"injected-synthetic\",\"FeishuAppSecret\":\"injected-synthetic\",\"FeishuEncryptKey\":\"injected-synthetic\"}";
        foreach (var value in new PlatformOptions[] { JsonSerializer.Deserialize<AppSettings>(injected)!, JsonSerializer.Deserialize<PlatformOptions>(injected)! })
            Require(value.OneBotToken == "" && value.OfficialAppSecret == "" && value.FeishuAppSecret == "" && value.FeishuEncryptKey == "",
                "excluded secret accepted from persisted JSON");
    }
    var store = new SettingsStore();
    store.Save(settings);
    var stored = JsonNode.Parse(AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id = 1")!)!.AsObject();
    Require(ignored.All(name => !stored.ContainsKey(name)), "settings store persisted excluded platform properties");
    var loaded = store.Load();
    RequireSamePlatformView(loaded, loaded);
    Require(loaded.FeishuWhitelist == "chat-synthetic" && loaded.WhitelistGroups == "10004"
        && loaded.PlatformPolicies.Single().AllowedActions.SequenceEqual(["send"]), "settings store lost whitelist/policy values");
    return Task.CompletedTask;
});

await Test("platform snapshot, copy and hot reload isolate nested policies and publish one complete view", () =>
{
    var old = PlatformSettings();
    var snapshot = old.Snapshot();
    var copied = new PlatformOptions();
    old.CopyPlatformPropertiesTo(copied);
    RequireSamePlatformView(snapshot, snapshot);
    foreach (var copy in new PlatformOptions[] { snapshot, copied })
    {
        Require(copy.FeishuAppSecret == old.FeishuAppSecret && copy.WhitelistPrivates == old.WhitelistPrivates, "copy lost scalar/secret values");
        Require(!ReferenceEquals(copy.PlatformPolicies, old.PlatformPolicies)
            && !ReferenceEquals(copy.PlatformPolicies[0], old.PlatformPolicies[0])
            && !ReferenceEquals(copy.PlatformPolicies[0].FeatureOverrides, old.PlatformPolicies[0].FeatureOverrides)
            && !ReferenceEquals(copy.PlatformPolicies[0].AllowedActions, old.PlatformPolicies[0].AllowedActions), "nested policy copy aliases old snapshot");
        copy.PlatformPolicies[0].FeatureOverrides["image"] = true;
        copy.PlatformPolicies[0].AllowedActions.Add("recall");
    }
    Require(!old.PlatformPolicies[0].FeatureOverrides["image"] && old.PlatformPolicies[0].AllowedActions.SequenceEqual(["send"]), "copy changed old policy");
    var box = new SettingsBox(old);
    var accessor = (IPlatformSettingsAccessor)box;
    var persisted = false;
    var published = false;
    var next = box.ApplyPersisted(s =>
    {
        s.FeishuAppSecret = "reloaded-synthetic";
        s.FeishuWhitelist = "new-chat-synthetic";
        s.WhitelistGroups = "10007";
        s.PlatformPolicies[0].GroupWhitelist = "new-chat-synthetic";
        s.PlatformPolicies[0].FeatureOverrides["image"] = true;
        s.PlatformPolicies[0].AllowedActions.Add("recall");
    }, candidate =>
    {
        Require(ReferenceEquals(accessor.Current, old), "published before persist");
        RequireSamePlatformView(candidate, candidate);
        persisted = true;
    }, candidate =>
    {
        Require(persisted && ReferenceEquals(accessor.Current, candidate), "published callback sees different settings");
        published = true;
    });
    Require(persisted && published && ReferenceEquals(next, box.Current) && ReferenceEquals(next, accessor.Current), "publication split base and derived view");
    RequireSamePlatformView(next, accessor.Current);
    Require(accessor.Current.FeishuAppSecret == "reloaded-synthetic" && accessor.Current.FeishuWhitelist == "new-chat-synthetic"
        && accessor.Current.WhitelistGroups == "10007" && accessor.Current.PlatformPolicies[0].GroupWhitelist == "new-chat-synthetic", "reload lost updated platform fields");
    Require(old.FeishuAppSecret == "feishu-secret-synthetic" && old.FeishuWhitelist == "chat-synthetic"
        && old.PlatformPolicies[0].GroupWhitelist == "chat-synthetic" && !old.PlatformPolicies[0].FeatureOverrides["image"]
        && old.PlatformPolicies[0].AllowedActions.SequenceEqual(["send"]), "hot reload mutated in-flight snapshot");
    var failed = false;
    try { box.ApplyPersisted(s => s.PlatformPolicies[0].AllowedActions.Clear(), _ => throw new IOException("synthetic failure")); }
    catch (IOException) { failed = true; }
    Require(failed && ReferenceEquals(accessor.Current, next) && next.PlatformPolicies[0].AllowedActions.Count == 2,
        "failed persistence published or changed current policy");
    return Task.CompletedTask;
});

await Test("token changes after secret hot reload", async () =>
{
    var box = Settings();
    var http = new FakeHttp();
    using var gateway = new FeishuBotGateway(box, http);
    Require((await gateway.SendAsync(gateway.Context, Message())).IsSuccess, "first send failed");
    box.Apply(s => s.FeishuAppSecret = "secret-b");
    Require((await gateway.SendAsync(gateway.Context, Message())).IsSuccess, "second send failed");
    Require(http.TokenCalls == 2, $"expected two token refreshes, actual {http.TokenCalls}");
});

await Test("token credential key preserves the exact secret", async () =>
{
    var box = Settings();
    var http = new FakeHttp();
    using var gateway = new FeishuBotGateway(box, http);
    Require((await gateway.SendAsync(gateway.Context, Message())).IsSuccess, "first send failed");
    box.Apply(s => s.FeishuAppSecret = "secret-a ");
    Require((await gateway.SendAsync(gateway.Context, Message())).IsSuccess && http.TokenCalls == 2, "changed secret was trimmed into stale cache key");
});

await Test("identity write failure gives controlled webhook failure", async () =>
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "map.json");
    var ids = new FeishuIdMap(path);
    Directory.CreateDirectory(path);
    using var gateway = new FeishuBotGateway(Settings(), new FakeHttp(), ids: ids);
    var received = 0;
    gateway.MessageReceived += _ => received++;
    var result = await gateway.HandleWebhookAsync(Webhook("event-write-failure"), null, null, null);
    Require(result.StatusCode == 503 && received == 0, "must reject unavailable binding with 503, no callback");
    Directory.Delete(path);
    var retry = await gateway.HandleWebhookAsync(Webhook("event-write-failure"), null, null, null);
    Require(retry.StatusCode == 200 && received == 1, "identity failure consumed dedup marker and prevented recovery retry");
});

await Test("persisted identities survive restart, reversed and fresh arrival order", async () =>
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "map.json");
    var ids = new FeishuIdMap(path);
    var expected = new Dictionary<string, long>();
    foreach (var native in Enumerable.Range(0, 100).Select(i => $"native-{i}")) expected[native] = ids.AliasFor("app-a", "group", native);
    var restarted = new FeishuIdMap(path);
    var fresh = new FeishuIdMap(Path.Combine(temp.Path, "fresh.json"));
    foreach (var (native, alias) in expected.Reverse())
    {
        Require(restarted.OriginalOf(alias, "app-a", "group") == native, "missing reverse binding before arrival");
        Require(restarted.AliasFor("app-a", "group", native) == alias, "restart changed alias");
        Require(fresh.AliasFor("app-a", "group", native) == alias, "arrival order changed alias");
    }
    await Task.CompletedTask;
});

await Test("kind/account isolation and old aliases fail closed without network", async () =>
{
    using var temp = new TempDirectory();
    var ids = new FeishuIdMap(Path.Combine(temp.Path, "map.json"));
    var aliases = new[] { ids.AliasFor("app-a", "group", "same"), ids.AliasFor("app-a", "participant", "same"), ids.AliasFor("app-a", "message", "same"), ids.AliasFor("app-b", "group", "same") };
    Require(aliases.Distinct().Count() == 4 && aliases.All(a => a >= FeishuIdMap.AliasBase && a < FeishuIdMap.AliasLimit), "identity collision or range mismatch");
    var http = new FakeHttp();
    var box = Settings();
    using var gateway = new FeishuBotGateway(box, http, ids: ids);
    Require(!(await gateway.SendTextAsync(true, aliases[1], "synthetic")).Ok, "participant used as group");
    Require(!(await gateway.SendTextAsync(true, Channels.FeishuBase + 10001, "synthetic")).Ok, "legacy alias routed");
    box.Apply(s => s.FeishuAppId = "app-b");
    Require(!(await gateway.SendTextAsync(true, aliases[0], "synthetic")).Ok, "old app alias routed with new account");
    Require(http.TokenCalls == 0, "rejected alias caused network");
});

await Test("gateway restart sends persisted target and quote; send ID uses same mapping", async () =>
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "map.json");
    var ids = new FeishuIdMap(path);
    var target = ids.AliasFor("app-a", "group", "chat-synthetic");
    var quote = ids.AliasFor("app-a", "message", "quote-synthetic");
    var http = new FakeHttp();
    http.MessageHandler = (request, _) =>
    {
        Require(request.RequestUri!.AbsolutePath.EndsWith("/quote-synthetic/reply"), "quote ID lost");
        return Task.FromResult(FakeHttp.Json("{\"code\":0,\"data\":{\"message_id\":\"sent-synthetic\"}}"));
    };
    using var gateway = new FeishuBotGateway(Settings(), http, ids: new FeishuIdMap(path));
    var sent = await gateway.SendTextAsync(true, target, "synthetic", replyToMessageId: quote);
    Require(sent.Ok, "restart send failed");
    Require(new FeishuIdMap(path).OriginalOf(sent.MessageId, "app-a", "message") == "sent-synthetic", "sent message ID differs from outbox/persisted mapping");
    Require(gateway.Outbox.Single().MessageId == sent.MessageId, "outbox ID mismatch");
});

await Test("parallel allocation is unique, immediately durable and preserves official default", async () =>
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "map.json");
    var ids = new FeishuIdMap(path);
    var aliases = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => ids.AliasFor("app-a", "group", $"native-{i}"))));
    Require(aliases.Distinct().Count() == 64, "parallel aliases not unique");
    var reopened = new FeishuIdMap(path);
    for (var i = 0; i < aliases.Length; i++) Require(reopened.OriginalOf(aliases[i], "app-a", "group") == $"native-{i}", "return preceded persistence");
    var officialPath = Path.Combine(temp.Path, "official.json");
    var official = new OfficialIdMap(officialPath);
    var old = official.AliasFor("official-synthetic");
    official.Flush();
    Require(old >= Channels.AliasBase && new OfficialIdMap(officialPath).OriginalOf(old) == "official-synthetic", "default official behavior changed");
});

await Test("damaged/ambiguous/out-of-range map is retained and never rebound", async () =>
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "map.json");
    foreach (var invalid in new[] { "broken", "{}", "{\"map\":{\"invalid\":6000000000000001}}", "{\"map\":{\"invalid\":6001000000000001}}" })
    {
        File.WriteAllText(path, invalid);
        var rejected = false;
        try { _ = new FeishuIdMap(path); } catch { rejected = true; }
        Require(rejected && File.ReadAllText(path) == invalid, "damaged map reset or accepted");
    }
    await Task.CompletedTask;
});

await Test("token binds app ID, secret, normalized API base and cleared credentials", async () =>
{
    var box = Settings();
    var http = new FakeHttp();
    using var gateway = new FeishuBotGateway(box, http);
    Require((await gateway.SendAsync(gateway.Context, Message())).IsSuccess, "first send");
    box.Apply(s => s.FeishuAppId = "app-b");
    Require((await gateway.SendAsync(gateway.Context, Message())).IsSuccess && http.TokenCalls == 2, "app ID not refreshed");
    box.Apply(s => s.FeishuApiBase = "https://b.example.com/");
    Require((await gateway.SendAsync(gateway.Context, Message())).IsSuccess && http.TokenCalls == 3, "API base not refreshed");
    box.Apply(s => s.FeishuApiBase = "https://b.example.com");
    Require((await gateway.SendAsync(gateway.Context, Message())).IsSuccess && http.TokenCalls == 3, "normalized base unnecessarily refreshed");
    box.Apply(s => s.FeishuAppSecret = "");
    Require(!(await gateway.SendAsync(gateway.Context, Message())).IsSuccess && http.TokenCalls == 3, "empty secret reused cached token");
    box.Apply(s => { s.FeishuAppSecret = "secret-b"; s.FeishuAppId = ""; });
    Require(!(await gateway.SendAsync(gateway.Context, Message())).IsSuccess && http.TokenCalls == 3, "empty app ID reused cached token");
});

await Test("parallel refresh is single flight per complete snapshot", async () =>
{
    var box = Settings();
    var http = new FakeHttp();
    using var gateway = new FeishuBotGateway(box, http);
    foreach (var secret in new[] { "secret-a", "secret-b" })
    {
        box.Apply(s => s.FeishuAppSecret = secret);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => gateway.SendAsync(gateway.Context, Message())));
        Require(outcomes.All(r => r.IsSuccess), "parallel send failed");
    }
    Require(http.TokenCalls == 2, $"expected 2 single-flight refreshes, actual {http.TokenCalls}");
});

await Test("in-flight refresh and queued sends retain entire old/new snapshot", async () =>
{
    var box = Settings();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var sent = new System.Collections.Concurrent.ConcurrentBag<(string Host, string Token)>();
    var http = new FakeHttp();
    http.TokenHandler = async (url, body, ct) =>
    {
        var node = JsonNode.Parse(body)!;
        var id = node["app_id"]!.GetValue<string>();
        var secret = node["app_secret"]!.GetValue<string>();
        var old = id == "app-a";
        Require(secret == (old ? "secret-a" : "secret-b") && new Uri(url).Host == (old ? "a.example.com" : "b.example.com"), "mixed token snapshot");
        if (old) { started.TrySetResult(); await release.Task.WaitAsync(ct); }
        return FakeHttp.Json(new JsonObject { ["tenant_access_token"] = $"token-{id}", ["expire"] = 7200 }.ToJsonString());
    };
    http.MessageHandler = (request, _) =>
    {
        sent.Add((request.RequestUri!.Host, request.Headers.Authorization!.Parameter!));
        return Task.FromResult(FakeHttp.Json("{\"code\":0,\"data\":{\"message_id\":\"sent-synthetic\"}}"));
    };
    using var gateway = new FeishuBotGateway(box, http);
    var first = gateway.SendAsync(gateway.Context, Message());
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var oldQueued = gateway.SendAsync(gateway.Context, Message());
    box.Apply(s => { s.FeishuAppId = "app-b"; s.FeishuAppSecret = "secret-b"; s.FeishuApiBase = "https://b.example.com"; });
    var newQueued = gateway.SendAsync(gateway.Context, Message());
    release.SetResult();
    Require((await Task.WhenAll(first, oldQueued, newQueued)).All(r => r.IsSuccess), "snapshot send failed");
    Require(sent.Count(p => p == ("a.example.com", "token-app-a")) == 2 && sent.Count(p => p == ("b.example.com", "token-app-b")) == 1, "token sent to wrong API base");
    Require((await gateway.SendAsync(gateway.Context, Message())).IsSuccess && http.TokenCalls == 2, "old refresh poisoned new cache");
});

await Test("signed nonce/event retry after persistence repair and normal duplicate emits once", async () =>
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "map.json");
    var ids = new FeishuIdMap(path);
    Directory.CreateDirectory(path);
    var box = Settings();
    box.Apply(s => s.FeishuEncryptKey = "signing-synthetic");
    using var gateway = new FeishuBotGateway(box, new FakeHttp(), ids: ids);
    var received = 0;
    gateway.MessageReceived += _ => Interlocked.Increment(ref received);
    var body = Webhook("event-signed-retry");
    var timestamp = Clock.UtcNow.ToUnixTimeSeconds().ToString();
    var nonce = "nonce-synthetic-retry";
    var signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(timestamp + nonce + "signing-synthetic" + body)));
    Require((await gateway.HandleWebhookAsync(body, signature, timestamp, nonce)).StatusCode == 503 && received == 0, "write failure must precede dedup consumption");
    Directory.Delete(path);
    Require((await gateway.HandleWebhookAsync(body, signature, timestamp, nonce)).StatusCode == 200 && received == 1, "same nonce/event retry did not recover");
    var duplicate = await gateway.HandleWebhookAsync(body, signature, timestamp, nonce);
    Require(duplicate.ResponseBody.Contains("duplicate") && received == 1, "normal duplicate re-emitted");
});

await Test("concurrent same event emits once and not-whitelisted never emits", async () =>
{
    using var temp = new TempDirectory();
    var box = Settings();
    var http = new FakeHttp();
    using var gateway = new FeishuBotGateway(box, http, ids: new FeishuIdMap(Path.Combine(temp.Path, "map.json")));
    var received = 0;
    gateway.MessageReceived += _ => Interlocked.Increment(ref received);
    var body = Webhook("event-parallel-dedup");
    var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => gateway.HandleWebhookAsync(body, null, null, null))));
    Require(results.All(r => r.StatusCode == 200) && received == 1, "concurrent event emitted more than once");
    box.Apply(s => s.FeishuWhitelist = "not-this-target");
    var rejected = await gateway.HandleWebhookAsync(Webhook("event-not-whitelisted"), null, null, null);
    Require(rejected.ResponseBody.Contains("not_whitelisted") && received == 1 && http.TokenCalls == 0, "whitelist rejection emitted or sent");
});

await Test("numeric whitelist allocation failure returns zero safely and never emits", async () =>
{
    using var temp = new TempDirectory();
    var expected = new FeishuIdMap(Path.Combine(temp.Path, "expected.json")).AliasFor("app-a", "group", "chat-synthetic");
    var path = Path.Combine(temp.Path, "unwritable.json");
    var ids = new FeishuIdMap(path);
    Directory.CreateDirectory(path);
    var box = Settings();
    box.Apply(s => s.FeishuWhitelist = $"0,{expected}");
    using var gateway = new FeishuBotGateway(box, new FakeHttp(), ids: ids);
    var received = 0;
    gateway.MessageReceived += _ => received++;
    var result = await gateway.HandleWebhookAsync(Webhook("event-numeric-write-failure"), null, null, null);
    Require(result.StatusCode == 200 && result.ResponseBody.Contains("not_whitelisted") && received == 0, "numeric whitelist write failure escaped or allowed zero alias");
});

await Test("strict candidate collision never probes to an arrival-dependent alias", async () =>
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "collision.json");
    var ids = new OfficialIdMap(path, 10001, 10002, durable: true);
    Require(ids.AliasFor("first-synthetic") == 10001, "small range first alias");
    var rejected = false;
    try { _ = ids.AliasFor("second-synthetic"); } catch (InvalidOperationException) { rejected = true; }
    Require(rejected && ids.OriginalOf(10001) == "first-synthetic", "collision replaced or reallocated identity");
    Require(new OfficialIdMap(path, 10001, 10002, durable: true).OriginalOf(10001) == "first-synthetic", "collision corrupted persisted mapping");
    await Task.CompletedTask;
});

SqliteConnection.ClearAllPools();
return failures == 0 ? 0 : 1;

sealed class FakeHttp : IHttpFetcher
{
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public int TokenCalls;
    public Func<string, string, CancellationToken, Task<HttpResponseMessage>>? TokenHandler;
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? MessageHandler;
    public static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
    {
        Interlocked.Increment(ref TokenCalls);
        var body = await content.ReadAsStringAsync(ct);
        if (TokenHandler is not null) return await TokenHandler(url, body, ct);
        var id = JsonNode.Parse(body)?["app_id"]?.GetValue<string>();
        var secret = JsonNode.Parse(body)?["app_secret"]?.GetValue<string>();
        return Json(new JsonObject { ["code"] = 0, ["tenant_access_token"] = $"token-{id}-{secret}", ["expire"] = 7200 }.ToJsonString());
    }
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        => MessageHandler?.Invoke(request, ct) ?? Task.FromResult(Json("{\"code\":0,\"data\":{\"message_id\":\"message-synthetic\"}}"));
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "feishu-probe-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
