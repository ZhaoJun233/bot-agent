using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Ops;
using BotAgent.Services.Ports;
using BotAgent.Services.Settings;
using BotAgent.Services.Conversations;
using BotAgent.Services.Qq;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Services.Reply;
using BotAgent.Services.OneBot;
using Microsoft.Data.Sqlite;
using BotAgent.Platforms;
using BotAgent.Services.Platforms;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "bot-settings-scope-probe-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
var failures = 0;
try
{
    // Upgrade a synthetic legacy database rather than relying only on a fresh schema.
    Directory.CreateDirectory(AppPaths.DataDir);
    using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = AppDatabase.FilePath }.ToString()))
    {
        legacy.Open();
        using var command = legacy.CreateCommand();
        command.CommandText = "CREATE TABLE own_messages(message_id INTEGER PRIMARY KEY, text TEXT NOT NULL, at_unix INTEGER NOT NULL); " +
            "INSERT INTO own_messages VALUES(10001, 'synthetic legacy', 0);";
        command.ExecuteNonQuery();
    }
    var oldJson = "[{\"id\":10004,\"text\":\"synthetic legacy JSON\",\"at\":\"1970-01-01T00:00:00Z\"}]";
    File.WriteAllText(Path.Combine(AppPaths.DataDir, "own-messages.json"), oldJson);
    AppDatabase.Initialize();
    Check("host settings and platform interface share platform values in both directions", () =>
    {
        var settings = new AppSettings
        {
            OfficialEnabled = true, OfficialChatEnabled = false,
            OfficialAppId = "synthetic-official", OfficialAppSecret = "synthetic-official-secret",
            OfficialSandbox = true, OfficialWhitelistGroups = "10001", OfficialWhitelistPrivates = "10002",
            OfficialApiBase = "https://example.com/official", OfficialTokenUrl = "https://example.com/token",
            FeishuEnabled = true, FeishuAppId = "synthetic-feishu", FeishuAppSecret = "synthetic-feishu-secret",
            FeishuVerificationToken = "synthetic-verification", FeishuEncryptKey = "synthetic-encrypt-key",
            FeishuWhitelist = "synthetic-chat", FeishuApiBase = "https://example.com/feishu",
            LocalChannelIds = "10003", PrivateChatEnabled = false, PlatformSwitchSchemaVersion = 1,
            PlatformPolicies = new()
            {
                new() { PlatformId = PlatformId.QqOfficial, AccountScope = AccountScope.Legacy,
                    Enabled = true, ChatEnabled = false, InheritActionAllowlist = false }
            }
        };
        var box = new SettingsBox(settings);
        var platform = ((IPlatformSettingsAccessor)box).Current;
        Require(platform.OfficialEnabled && !platform.OfficialChatEnabled
            && platform.OfficialAppId == "synthetic-official" && platform.OfficialAppSecret == "synthetic-official-secret"
            && platform.OfficialSandbox && platform.OfficialWhitelistGroups == "10001" && platform.OfficialWhitelistPrivates == "10002"
            && platform.OfficialApiBase == "https://example.com/official" && platform.OfficialTokenUrl == "https://example.com/token"
            && platform.FeishuEnabled && platform.FeishuAppId == "synthetic-feishu" && platform.FeishuAppSecret == "synthetic-feishu-secret"
            && platform.FeishuVerificationToken == "synthetic-verification" && platform.FeishuEncryptKey == "synthetic-encrypt-key"
            && platform.FeishuWhitelist == "synthetic-chat" && platform.FeishuApiBase == "https://example.com/feishu"
            && platform.LocalChannelIds == "10003" && !platform.PrivateChatEnabled && platform.PlatformSwitchSchemaVersion == 1
            && ReferenceEquals(platform.PlatformPolicies, settings.PlatformPolicies),
            "host-assigned platform values were lost through the platform settings interface");
        var policy = new PlatformPolicyResolver(box).ResolveForChannel("official");
        Require(policy.Enabled && !policy.ChatEnabled && policy.ActionAllowlistConfigured && policy.AllowedActions.Count == 0,
            "real policy resolver missed the host configuration or opened an explicit empty allowlist");
        var replacement = new PlatformOptions
        {
            OfficialAppId = "synthetic-other-official", OfficialAppSecret = "synthetic-other-official-secret",
            OfficialWhitelistGroups = "10004", OfficialWhitelistPrivates = "10005",
            OfficialApiBase = "https://example.com/other-official", OfficialTokenUrl = "https://example.com/other-token",
            FeishuAppId = "synthetic-other-feishu", FeishuAppSecret = "synthetic-other-feishu-secret",
            FeishuVerificationToken = "synthetic-other-verification", FeishuEncryptKey = "synthetic-other-encrypt-key",
            FeishuWhitelist = "synthetic-other-chat", FeishuApiBase = "https://example.com/other-feishu",
            LocalChannelIds = "10006"
        };
        replacement.CopyPlatformPropertiesTo(platform);
        Require(!settings.OfficialEnabled && settings.OfficialChatEnabled && !settings.OfficialSandbox
            && settings.OfficialAppId == "synthetic-other-official" && settings.OfficialAppSecret == "synthetic-other-official-secret"
            && settings.OfficialWhitelistGroups == "10004" && settings.OfficialWhitelistPrivates == "10005"
            && settings.OfficialApiBase == "https://example.com/other-official" && settings.OfficialTokenUrl == "https://example.com/other-token"
            && !settings.FeishuEnabled && settings.FeishuAppId == "synthetic-other-feishu" && settings.FeishuAppSecret == "synthetic-other-feishu-secret"
            && settings.FeishuVerificationToken == "synthetic-other-verification" && settings.FeishuEncryptKey == "synthetic-other-encrypt-key"
            && settings.FeishuWhitelist == "synthetic-other-chat" && settings.FeishuApiBase == "https://example.com/other-feishu"
            && settings.LocalChannelIds == "10006" && settings.PrivateChatEnabled && settings.PlatformSwitchSchemaVersion == 0
            && settings.PlatformPolicies.Count == 0,
            "platform-assigned values were lost through the host settings view");
    });
    Check("platform credentials are runtime only in either JSON view and settings persistence", () =>
    {
        var settings = new AppSettings
        {
            OneBotToken = "synthetic-onebot-secret", OfficialAppSecret = "synthetic-official-secret",
            FeishuAppSecret = "synthetic-feishu-secret", FeishuEncryptKey = "synthetic-encrypt-key",
            FeishuVerificationToken = "synthetic-verification", OfficialAppId = "synthetic-official"
        };
        var secretNames = new[] { "OneBotToken", "OfficialAppSecret", "FeishuAppSecret", "FeishuEncryptKey" };
        foreach (var json in new[] { JsonSerializer.Serialize(settings), JsonSerializer.Serialize<PlatformOptions>(settings) })
        {
            using var document = JsonDocument.Parse(json);
            Require(secretNames.All(name => !document.RootElement.TryGetProperty(name, out _)),
                "runtime platform credentials entered a serialized configuration view");
            Require(!document.RootElement.TryGetProperty("NormalizedUin", out _)
                && !document.RootElement.TryGetProperty("UinOrZero", out _),
                "derived login values entered a serialized configuration view");
        }
        const string injected = "{\"OneBotToken\":\"synthetic-injected\",\"OfficialAppSecret\":\"synthetic-injected\","
            + "\"FeishuAppSecret\":\"synthetic-injected\",\"FeishuEncryptKey\":\"synthetic-injected\"}";
        foreach (var restored in new PlatformOptions[]
        {
            JsonSerializer.Deserialize<AppSettings>(injected)!, JsonSerializer.Deserialize<PlatformOptions>(injected)!
        })
            Require(restored.OneBotToken == "" && restored.OfficialAppSecret == ""
                && restored.FeishuAppSecret == "" && restored.FeishuEncryptKey == "",
                "JSON credential injection replaced runtime-only values");
        var store = new SettingsStore();
        store.Save(settings);
        var loaded = store.Load();
        Require(loaded.OneBotToken == "" && loaded.OfficialAppSecret == ""
            && loaded.FeishuAppSecret == "" && loaded.FeishuEncryptKey == "",
            "runtime platform credentials survived settings persistence");
        Require(loaded.OfficialAppId == "synthetic-official" && loaded.FeishuVerificationToken == "synthetic-verification",
            "non-excluded legacy configuration was lost while excluding credentials");
    });
    Check("legacy platform JSON and defaults survive normalization save publish and reload", () =>
    {
        var defaults = new AppSettings();
        Require(!defaults.OfficialEnabled && defaults.OfficialChatEnabled && !defaults.OfficialSandbox
            && !defaults.FeishuEnabled && defaults.PrivateChatEnabled && defaults.LocalChannelIds == ""
            && defaults.PlatformSwitchSchemaVersion == 0 && defaults.PlatformPolicies.Count == 0,
            "legacy platform defaults changed");
        const string legacyJson = """
            {"OneBotProtocol":"ReverseWebSocket","OneBotAddress":"https://example.com/onebot","QuickLoginUin":" 10001 ",
             "WhitelistGroups":"10002","WhitelistPrivates":"10003","MessageWhitelist":"synthetic-rule",
             "OfficialEnabled":false,"OfficialChatEnabled":true,"OfficialAppId":"synthetic-official","OfficialSandbox":true,
             "OfficialWhitelistGroups":"10004","OfficialWhitelistPrivates":"10005",
             "OfficialApiBase":"https://example.com/official","OfficialTokenUrl":"https://example.com/token",
             "FeishuEnabled":true,"FeishuAppId":"synthetic-feishu","FeishuVerificationToken":"synthetic-verification",
             "FeishuWhitelist":"synthetic-chat","FeishuApiBase":"https://example.com/feishu",
             "LocalChannelIds":"10006","PrivateChatEnabled":false,"PlatformSwitchSchemaVersion":0,
             "PlatformPolicies":[{"PlatformId":"qq.official","AccountScope":"legacy","Enabled":true,"ChatEnabled":true,
               "AllowedActions":["read"],"FeatureOverrides":{"image":false}}]}
            """;
        var original = JsonSerializer.Deserialize<AppSettings>(legacyJson)!;
        var box = new SettingsBox(original);
        var accessor = (IPlatformSettingsAccessor)box;
        var store = new SettingsStore();
        var migrated = box.ApplyPersisted(PlatformSwitchSettings.Normalize, store.Save);
        Require(migrated.PlatformSwitchSchemaVersion == 1 && !migrated.PlatformPolicies.Single().Enabled!.Value
            && migrated.PlatformPolicies.Single().ChatEnabled == true && !migrated.OfficialEnabled,
            "legacy disabled switch intersection was lost in migration");
        Require(original.PlatformSwitchSchemaVersion == 0 && original.PlatformPolicies.Single().Enabled == true,
            "normalization changed the in-flight legacy snapshot");
        var next = box.ApplyPersisted(s =>
        {
            var row = s.PlatformPolicies.Single();
            row.Enabled = true;
            row.AllowedActions.Add("search");
            row.FeatureOverrides["image"] = true;
            PlatformSwitchSettings.Normalize(s);
        }, store.Save);
        var loaded = store.Load();
        var policy = new PlatformPolicyResolver(box).ResolveForChannel("official");
        Require(ReferenceEquals(accessor.Current, next) && policy.Enabled && policy.ChatEnabled
            && policy.CanUseAction("search") && loaded.OfficialEnabled && loaded.PlatformSwitchSchemaVersion == 1
            && loaded.PlatformPolicies.Single().AllowedActions.SequenceEqual(new[] { "read", "search" }),
            "persist publish reload and the real platform resolver disagreed");
        Require(migrated.PlatformPolicies.Single().Enabled == false
            && migrated.PlatformPolicies.Single().AllowedActions.SequenceEqual(new[] { "read" })
            && !migrated.PlatformPolicies.Single().FeatureOverrides["image"],
            "nested policy mutation escaped the candidate snapshot");
        Require(loaded.OneBotProtocol == "ReverseWebSocket" && loaded.OneBotAddress == "https://example.com/onebot"
            && loaded.QuickLoginUin == " 10001 " && loaded.NormalizedUin == "10001" && loaded.UinOrZero == 10001
            && loaded.WhitelistGroups == "10002" && loaded.WhitelistPrivates == "10003" && loaded.MessageWhitelist == "synthetic-rule"
            && loaded.OfficialChatEnabled && loaded.OfficialAppId == "synthetic-official" && loaded.OfficialSandbox
            && loaded.OfficialWhitelistGroups == "10004" && loaded.OfficialWhitelistPrivates == "10005"
            && loaded.OfficialApiBase == "https://example.com/official" && loaded.OfficialTokenUrl == "https://example.com/token"
            && loaded.FeishuEnabled && loaded.FeishuAppId == "synthetic-feishu" && loaded.FeishuVerificationToken == "synthetic-verification"
            && loaded.FeishuWhitelist == "synthetic-chat" && loaded.FeishuApiBase == "https://example.com/feishu"
            && loaded.LocalChannelIds == "10006" && !loaded.PrivateChatEnabled,
            "legacy platform property names or values failed to roundtrip");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(loaded));
        using var legacyDocument = JsonDocument.Parse(legacyJson);
        foreach (var property in legacyDocument.RootElement.EnumerateObject())
            Require(document.RootElement.EnumerateObject().Count(p => p.Name == property.Name) == 1,
                "a legacy platform field was missing or serialized twice");
        var copy = next.Snapshot();
        copy.PlatformPolicies.Single().AllowedActions.Clear();
        copy.PlatformPolicies.Single().FeatureOverrides.Clear();
        copy.PlatformPolicies.Clear();
        Require(next.PlatformPolicies.Count == 1 && next.PlatformPolicies.Single().AllowedActions.Count == 2
            && next.PlatformPolicies.Single().FeatureOverrides["image"], "explicit snapshot shares nested collections");
    });
    Check("failed platform save does not mutate either runtime view or nested policy collections", () =>
    {
        var original = new AppSettings
        {
            PlatformSwitchSchemaVersion = 1,
            PlatformPolicies = new() { new() { PlatformId = PlatformId.Local, AccountScope = AccountScope.Legacy,
                Enabled = true, ChatEnabled = true, AllowedActions = new() { "read" }, FeatureOverrides = new() { ["image"] = false } } }
        };
        var box = new SettingsBox(original);
        var observed = false;
        try
        {
            box.ApplyPersisted(s =>
            {
                s.LocalChannelIds = "10001";
                s.PlatformPolicies.Single().ChatEnabled = false;
                s.PlatformPolicies.Single().AllowedActions.Add("search");
                s.PlatformPolicies.Single().FeatureOverrides["image"] = true;
            }, _ => throw new IOException("synthetic platform save failure"));
        }
        catch (IOException) { observed = true; }
        var platform = ((IPlatformSettingsAccessor)box).Current;
        Require(observed && ReferenceEquals(box.Current, original) && ReferenceEquals(platform, original)
            && platform.LocalChannelIds == "" && platform.PlatformPolicies.Single().ChatEnabled == true
            && platform.PlatformPolicies.Single().AllowedActions.SequenceEqual(new[] { "read" })
            && !platform.PlatformPolicies.Single().FeatureOverrides["image"],
            "failed persistence changed platform state or an in-flight nested collection");
    });
    Check("save failure propagates and rolls settings back", () =>
    {
        var store = new SettingsStore();
        store.Save(new AppSettings { AiDesire = 17 });
        var threw = false;
        try
        {
            store.Save(new AppSettings { AiDesire = 29 },
                new AuditEvent("", "actor-test", "tenant-test", "synthetic", "2.1"), new AuditLogStore());
        }
        catch (ArgumentException) { threw = true; }
        Require(threw, "failed audit/save returned success");
        Require(store.Load().AiDesire == 17, "failed transaction changed stored settings");
    });
    Check("failed hot reload leaves runtime unpublished", () =>
    {
        var original = new AppSettings { AiDesire = 17 };
        var box = new SettingsBox(original);
        var repo = new ThrowingSettingsRepository();
        var reload = new SettingsHotReload(box, repo, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        Exception? observed = null;
        try { reload.ApplyRuntimeSettings(s => s.AiDesire = 29); }
        catch (Exception ex) { observed = ex; }
        Require(ReferenceEquals(box.Current, original), "failed save published runtime snapshot");
        Require(observed is IOException && repo.Attempts == 1, "persistence must fail before runtime rebuild");
    });
    Check("recalled survives real SQLite record roundtrip", () =>
    {
        var conversation = new BotConversation { SourceKey = "group:10001", Kind = ConversationKind.GroupChat };
        conversation.Append(new ChatMessage { Role = MessageRole.Peer, Timestamp = DateTimeOffset.UnixEpoch, Text = "synthetic recalled", Recalled = true, QqMessageId = 10001 });
        var store = new ConversationStore();
        store.RequestSave(new[] { conversation.ToRecord() });
        var record = store.LoadAsync().Single(r => r.SourceKey == conversation.SourceKey);
        var restored = BotConversation.FromRecord(record);
        Require(restored.Messages.Single().Recalled, "recalled flag lost after record/database/reload");
        Require(restored.Messages.Single().Text == "synthetic recalled", "recalled content must be retained");
    });
    Check("legacy own-message identity fails closed and data remains", () =>
    {
        var store = new OwnMessageStore();
        var ledger = new OwnMessageLedger(store, _ => { });
        ledger.EnsureLoaded();
        Require(!ledger.TryGet(10001, out _), "bare native id was treated as a scoped identity");
        Require(store.LoadRecent(200).Any(r => r.Id == 10001), "legacy data deleted");
        Require(!ledger.TryGet("group:10004", 10004, out _), "legacy JSON row was assigned a guessed scope");
        Require(store.LoadRecent(200).Any(r => r.Id == 10004), "legacy JSON data not retained/imported");
        var archive = Path.Combine(root, "legacy-json", "data", "own-messages.json");
        Require(File.Exists(archive) && File.ReadAllText(archive) == oldJson, "legacy JSON archive lost original bytes");
        store.LoadRecentScoped(200);
        AppDatabase.Initialize();
        Require(store.LoadRecent(200).Count == 2, "repeat initialization/import duplicated or lost legacy data");
    });
    Check("full scoped identity survives restart without any cross-scope hit", () =>
    {
        var store = new OwnMessageStore();
        var original = new ConversationId(PlatformId.QqPrivate, "account-test", ConversationKind.GroupChat, "target-test");
        var scopes = new[]
        {
            original,
            original with { PlatformId = PlatformId.QqOfficial },
            original with { AccountScope = "account-other" },
            original with { AccountScope = "Account-test" },
            original with { NativeTargetId = "target-other" },
            original with { Kind = ConversationKind.PrivateChat },
            original with { ThreadId = "thread-test" },
            original with { ThreadId = "thread-other" }
        };
        var ledger = new OwnMessageLedger(store, _ => { });
        for (var i = 0; i < scopes.Length; i++)
            ledger.Remember(new MessageRef(scopes[i], "Native-Case-10001"), "synthetic-" + i, DateTimeOffset.UnixEpoch.AddSeconds(i));
        var reloaded = new OwnMessageLedger(new OwnMessageStore(), _ => { });
        for (var i = 0; i < scopes.Length; i++)
        {
            Require(reloaded.TryGet(new MessageRef(scopes[i], "Native-Case-10001"), out var hit) && hit.Text == "synthetic-" + i,
                "scoped identity collided or failed reload at index " + i);
            Require(!reloaded.TryGet(new MessageRef(scopes[i], "native-case-10001"), out _), "native id case was changed");
        }
        Require(!reloaded.TryGet(new MessageRef(original with { AccountScope = "missing-test" }, "Native-Case-10001"), out _),
            "unknown account read an existing message");
        Require(!reloaded.TryGet("malformed-scope", 10001, out _), "malformed scope guessed a default identity");
        Require(!reloaded.TryGet("group:10001", 10001, out _), "retained ambiguous legacy row became scoped");
        ledger.Remember("group:10002", new SendResult(true, 10001), "synthetic explicit legacy-scope");
        Require(new OwnMessageLedger(store, _ => { }).TryGet("group:10002", 10001, out var legacyHit)
            && legacyHit.Text == "synthetic explicit legacy-scope", "explicit legacy conversation key did not canonicalize");
    });
    Check("real cadence sender preserves supplied account and thread when remembering sends", () =>
    {
        var store = new OwnMessageStore();
        var ledger = new OwnMessageLedger(store, _ => { });
        var source = new SyntheticChatSource();
        var sender = new PlainSender(new SettingsBox(new AppSettings { SplitReplies = false }), source,
            null!, null!, ledger, _ => { }, new TurnTraceStore());
        var conversation = new ConversationId(PlatformId.QqPrivate, "sender-account-test",
            ConversationKind.GroupChat, "10005", "thread-test");
        var key = ConversationIdCodec.EncodeStructured(conversation);
        var report = sender.SendWithCadenceAsync(key, true, 10005, "synthetic sender", null).GetAwaiter().GetResult();
        Require(report.AnySent && source.Sends == 1 && source.LastTarget == 10005, "synthetic transport did not send");
        var reloaded = new OwnMessageLedger(store, _ => { });
        Require(reloaded.TryGet(key, 10005, out var hit) && hit.Text == "synthetic sender",
            "real sender lost account/thread scope while writing the ledger");
        foreach (var other in new[] { conversation with { ThreadId = "thread-other" },
            conversation with { AccountScope = "sender-account-other" }, conversation with { NativeTargetId = "native-target-test" } })
        {
            var otherKey = ConversationIdCodec.EncodeStructured(other);
            sender.SendWithCadenceAsync(otherKey, true, 10005, "synthetic other " + otherKey, null).GetAwaiter().GetResult();
            Require(new OwnMessageLedger(store, _ => { }).TryGet(otherKey, 10005, out var otherHit)
                && otherHit.Text == "synthetic other " + otherKey, "same native id collided in real sender scope");
        }
        Require(new OwnMessageLedger(store, _ => { }).TryGet(key, 10005, out hit) && hit.Text == "synthetic sender",
            "real sender overwrote first scope with a sibling account/thread/target");
        var invalid = sender.SendWithCadenceAsync("malformed-scope", true, 10005, "synthetic ignored", null).GetAwaiter().GetResult();
        Require(!invalid.AnySent && source.Sends == 4 && source.LastTarget == 10005,
            "invalid source scope sent anyway or fullscope changed transport routing");
    });
    Check("invalid kind and incomplete account are rejected rather than assigned a scope", () =>
    {
        var store = new OwnMessageStore();
        var original = new ConversationId(PlatformId.QqPrivate, "account-test", ConversationKind.GroupChat, "target-test");
        foreach (var bad in new[] { original with { Kind = (ConversationKind)999 }, original with { AccountScope = "" } })
        {
            var rejected = false;
            try { store.Upsert(new MessageRef(bad, "invalid-test"), "synthetic", DateTimeOffset.UnixEpoch); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "invalid scope was silently assigned a valid default");
        }
    });
    Check("unsupported audit chain fails before any settings write", () =>
    {
        var store = new SettingsStore();
        store.Save(new AppSettings { AiDesire = 17 });
        var audit = new FakeAuditChain();
        foreach (var chain in new IAuditChain?[] { null, audit })
        {
            var rejected = false;
            try { store.Save(new AppSettings { AiDesire = 29 }, Event("unsupported"), chain); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && store.Load().AiDesire == 17, "non-atomic audit was silently accepted");
        }
        Require(audit.Appends == 0, "external audit side effect ran before transaction rejection");
    });
    Check("mutation failure has no persistence or publication side effects", () =>
    {
        var original = new AppSettings { AiDesire = 17 };
        var box = new SettingsBox(original);
        var persisted = false;
        var published = false;
        try { box.ApplyPersisted(s => { s.AiDesire = 29; throw new ArgumentException("synthetic"); },
            _ => persisted = true, _ => published = true); }
        catch (ArgumentException) { }
        Require(!persisted && !published && ReferenceEquals(box.Current, original), "failed mutation escaped staging");
    });
    Check("concurrent settings writers persist publish and rebuild in one order", () =>
    {
        var store = new SettingsStore();
        var box = new SettingsBox(new AppSettings { AiDesire = 0, MaxMessagesPerConversation = 100 });
        var published = 0;
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            start.Wait();
            box.ApplyPersisted(s =>
            {
                if (i % 2 == 0) s.AiDesire++;
                else s.MaxMessagesPerConversation++;
            }, store.Save, next =>
            {
                var disk = store.Load();
                Require(ReferenceEquals(box.Current, next), "a later writer raced runtime rebuild");
                Require(disk.AiDesire == next.AiDesire && disk.MaxMessagesPerConversation == next.MaxMessagesPerConversation,
                    "disk version differs from published candidate");
                published++;
            });
        })).ToArray();
        start.Set();
        Require(Task.WaitAll(tasks, TimeSpan.FromSeconds(15)), "settings writers timed out");
        Require(published == 20 && box.Current.AiDesire == 10 && box.Current.MaxMessagesPerConversation == 110,
            "concurrent writers lost a sibling update");
    });
    Check("postcommit callback failure is visible but committed settings stay published", () =>
    {
        var store = new SettingsStore();
        var box = new SettingsBox(new AppSettings { AiDesire = 17 });
        var threw = false;
        try { box.ApplyPersisted(s => s.AiDesire = 29, store.Save, _ => throw new IOException("synthetic rebuild failure")); }
        catch (IOException) { threw = true; }
        Require(threw && box.Current.AiDesire == 29 && store.Load().AiDesire == 29,
            "postcommit failure was hidden or falsely rolled back");
    });
    Check("ordinary audit and settings audit serialize without lock inversion or chain forks", () =>
    {
        var store = new SettingsStore();
        var audit = new AuditLogStore();
        var before = audit.Verify().CheckedCount;
        using var start = new ManualResetEventSlim(false);
        var regular = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 30; i++) audit.Append(Event("ordinary-" + i));
        });
        var settings = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 30; i++) store.Save(new AppSettings { AiDesire = i }, Event("settings-" + i), audit);
        });
        start.Set();
        Require(Task.WaitAll(new[] { regular, settings }, TimeSpan.FromSeconds(15)), "audit writers timed out");
        var result = audit.Verify();
        Require(result.Valid && result.CheckedCount == before + 60, "concurrent audit chain forked or lost events");
        Require(store.Load().AiDesire == 29, "settings audit did not commit its settings");
    });
    Check("legacy scoped retention and pruning remain independent", () =>
    {
        var store = new OwnMessageStore();
        store.Upsert(10003, "synthetic ignored bare-id write", DateTimeOffset.UnixEpoch);
        Require(!store.LoadRecent(200).Any(r => r.Id == 10003), "bare-id write created ambiguous new data");
        store.PruneTo(2);
        Require(store.LoadRecentScoped(200).Count == 2, "scoped pruning failed");
        Require(store.LoadRecent(200).Any(r => r.Id == 10001), "scoped pruning deleted retained legacy data");
    });
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
Console.WriteLine($"SettingsScopeProbe failures={failures}");
return failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static AuditEvent Event(string detail) => new("synthetic", "actor-test", "tenant-test", detail, "2.1");

sealed class FakeAuditChain : IAuditChain
{
    public int Appends { get; private set; }
    public void Append(AuditEvent auditEvent) => Appends++;
    public AuditVerification Verify() => new(true, null, null, Appends);
}

sealed class SyntheticChatSource : IQqChatSource
{
    public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
    public event Action<QqPokeEvent>? Poked { add { } remove { } }
    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
    public event Action<bool>? ConnectionChanged { add { } remove { } }
    public bool IsConnected => true;
    public int Sends { get; private set; }
    public long LastTarget { get; private set; }
    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default,
        long? replyToMessageId = null, bool directAddress = false)
    {
        Sends++;
        LastTarget = targetId;
        return Task.FromResult(new SendResult(true, 10005));
    }
    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<(string?, long)>((null, 0));
    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => Task.FromResult(false);
    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

sealed class ThrowingSettingsRepository : ISettingsRepository
{
    public int Attempts { get; private set; }
    public string FilePath => "synthetic";
    public bool ExistsOnDisk => false;
    public bool HasStoredSettings() => false;
    public AppSettings Load() => new();
    public void Save(AppSettings settings) { Attempts++; throw new IOException("synthetic save failure"); }
}
