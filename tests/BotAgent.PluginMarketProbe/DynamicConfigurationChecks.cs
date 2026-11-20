extern alias StorageModule;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

internal partial class Program
{
    // This original probe uses the old public API and raw new wire data. Preserve its DLL
    // before implementing the declaration so the repaired host runs the same assertions.
    internal static Task<int> RunDynamicConfigurationChecks(string root)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS dynamic configuration: " + label);
        }
        void Reject(Action action, string code)
        {
            try { action(); }
            catch (PluginSettingsException error) when (error.Code == code) { count++; return; }
            throw new InvalidOperationException("expected_" + code);
        }

        var fieldData = new (string Key, int? Id, string Type, string Default)[]
        {
            ("enableLinkPreview", 39, "boolean", "true"),
            ("enableWebSearch", 49, "boolean", "true"),
            ("linkPreviewMax", 65, "number", "3"),
            ("linkPreviewTimeoutSeconds", 66, "number", "5"),
            ("webSearchCooldownSeconds", 136, "number", "30"),
            ("webSearchMaxResults", 137, "number", "5"),
            ("webSearchSources", 138, "string", "wiki|https://example.com/search?q={q}"),
            ("webSearchTimeoutSeconds", 139, "number", "20"),
            ("webSearchUseModelSearch", 140, "boolean", "true"),
            ("webSearchReadMaxChars", null, "number", "1800")
        };
        var wire = JsonSerializer.Serialize(new
        {
            id = "preset.feature.websearch", version = "1.0.0", entryAssembly = "synthetic.dll",
            entryType = "SyntheticBindingPlugin", sha256 = new string('a', 64),
            downloadUrl = "https://example.com/synthetic.zip", apiVersion = 1, targetFramework = "net10.0",
            settings = fieldData.Where(field => field.Id.HasValue).Select(field => new
            {
                key = field.Key, label = field.Key, type = field.Type, defaultValue = field.Default,
                numberConstraints = field.Type != "number" ? null : field.Key switch
                {
                    "linkPreviewMax" => new { minimum = 0, maximum = 5, integer = true },
                    "linkPreviewTimeoutSeconds" => new { minimum = 2, maximum = 30, integer = true },
                    "webSearchCooldownSeconds" => new { minimum = 0, maximum = 86400, integer = true },
                    "webSearchMaxResults" => new { minimum = 1, maximum = 10, integer = true },
                    _ => new { minimum = 5, maximum = 60, integer = true }
                }
            }),
            configuration = new
            {
                apiVersion = 1,
                bindings = fieldData.Select(field => new { key = field.Key, legacyFieldId = field.Id,
                    readOnly = !field.Id.HasValue })
            }
        });
        var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(wire,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Check(JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Contains("\"configuration\"", StringComparison.Ordinal), "new binding declaration survives the public wire");
        var index = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = new[] { manifest } });
        var distribution = TrustedPluginDistribution.Read(index,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(index)));
        var fixture = new DynamicConfigurationFixture();
        var store = new PluginSettingsStore(distribution);
        var before = store.ReadManifest(manifest);
        Check(before.Revision == fixture.Read().Version.Value, "package token is the one configuration version");
        Check(before.Values.Count == 9 && !before.Values.ContainsKey("webSearchReadMaxChars"),
            "only nine existing editable fields are exposed");
        Check(!before.Values["enableWebSearch"].GetBoolean() && before.Values["linkPreviewMax"].GetInt32() == 4,
            "package reads prior legacy values without copying overrides");
        var changes = JsonSerializer.SerializeToElement(new { enableWebSearch = true, linkPreviewMax = 2,
            webSearchSources = "wiki|https://example.com/new?q={q}", webSearchCooldownSeconds = 0 });
        var first = store.Save(manifest, before.Revision, "synthetic-first", changes, "synthetic-admin");
        var after = store.ReadManifest(manifest);
        Check(first.Revision == before.Revision + 1 && after.Revision == first.Revision,
            "one package save commits one shared configuration version");
        Check(!first.RestartRequired && !after.RestartRequired, "existing hot settings do not request a restart");
        Check(fixture.Box.Current.EnableWebSearch && fixture.Box.Current.LinkPreviewMax == 2
            && fixture.Box.Current.WebSearchCooldownSeconds == 0, "committed package values publish immediately");
        var persisted = new SettingsStore().Load();
        Check(persisted.EnableWebSearch && persisted.LinkPreviewMax == 2
            && persisted.WebSearchSources == "wiki|https://example.com/new?q={q}", "legacy load reads the same persisted values");
        var replayVersion = fixture.Read().Version;
        Check(store.Save(manifest, before.Revision, "synthetic-first", changes, "synthetic-admin") == first
            && fixture.Read().Version == replayVersion, "original-key replay neither rewrites nor republishes");
        Reject(() => store.Save(manifest, before.Revision, "synthetic-conflict", changes, "synthetic-admin"),
            "plugin_settings_revision_conflict");
        Reject(() => store.Save(manifest, before.Revision, "synthetic-first",
            JsonSerializer.SerializeToElement(new { linkPreviewMax = 1 }), "synthetic-admin"),
            "plugin_settings_idempotency_conflict");
        foreach (var invalid in new[] { "{\"linkPreviewMax\":1.5}", "{\"linkPreviewMax\":6}",
            "{\"enableWebSearch\":\"true\"}", "{\"webSearchReadMaxChars\":99}", "{\"model\":\"synthetic\"}" })
            Reject(() => store.Save(manifest, after.Revision, Guid.NewGuid().ToString("N"),
                JsonDocument.Parse(invalid).RootElement, "synthetic-admin"), PluginSettingsContract.InvalidValues);
        Check(fixture.Read().Version == replayVersion && fixture.Box.Current.LinkPreviewMax == 2,
            "invalid bound writes have no desired or runtime side effects");
        var old = fixture.Save(new BehaviorChanges { EnableWebSearch = FieldEdit<bool>.Set(false),
            LinkPreviewMax = FieldEdit<int>.Set(5) });
        Check(old.Code == ConfigurationResultCode.Ok && !store.ReadManifest(manifest).Values["enableWebSearch"].GetBoolean()
            && store.ReadManifest(manifest).Values["linkPreviewMax"].GetInt32() == 5, "legacy writes are visible through package reads");
        Reject(() => store.Save(manifest, after.Revision, "synthetic-after-legacy", changes, "synthetic-admin"),
            "plugin_settings_revision_conflict");
        Check(store.Save(manifest, before.Revision, "synthetic-first", changes, "synthetic-admin") == first,
            "an old original receipt survives a newer legacy write");
        using (var connection = StorageModule::BotAgent.Adapters.Persistence.AppDatabase.Open())
        {
            var readMeta = typeof(StorageModule::BotAgent.Adapters.Persistence.AppDatabase)
                .GetMethod("GetMetaInTransaction", BindingFlags.NonPublic | BindingFlags.Static)!;
            var raw = (string?)readMeta.Invoke(null, [connection, "plugin_settings:" + manifest.Id]);
            Check(raw is null || JsonDocument.Parse(raw).RootElement.GetProperty("Values").EnumerateObject().Count() == 0,
                "bound values have no duplicate package overrides truth");
        }
        var resetBefore = store.ReadManifest(manifest);
        var reset = store.Save(manifest, resetBefore.Revision, "synthetic-reset",
            JsonSerializer.SerializeToElement(new { linkPreviewMax = (int?)null }), "synthetic-admin");
        Check(store.ReadManifest(manifest).Values["linkPreviewMax"].GetInt32() == 3
            && fixture.Box.Current.LinkPreviewMax == 3 && reset.Revision == resetBefore.Revision + 1,
            "package reset restores the same owner default and mode");
        return Task.FromResult(count);
    }

    private sealed class DynamicConfigurationFixture
    {
        internal SettingsBox Box { get; }
        private readonly IConfigurationChanges _configuration;
        private readonly ConfigurationCaller _caller = new("synthetic", "dynamic-configuration", "bot",
            ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
        internal DynamicConfigurationFixture()
        {
            foreach (var name in new[] { "BOTAGENT_MODEL", "QQCHAT_MODEL", "OPENAI_MODEL",
                "BOTAGENT_BASE_URL", "QQCHAT_BASE_URL", "OPENAI_BASE_URL" }) Environment.SetEnvironmentVariable(name, null);
            var initial = new AppSettings { Model = "synthetic", ModelBaseUrl = "https://example.com/v1",
                EnableWebSearch = false, LinkPreviewMax = 4 };
            new SettingsStore().Save(initial);
            Box = new SettingsBox(initial);
            var participant = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)!;
            var constructor = participant.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var sourceType = constructor.GetParameters()[2].ParameterType;
            var parameters = sourceType.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
            var sources = Expression.Lambda(sourceType, Expression.Constant(Enum.GetValues<WritableSecretSlot>()
                .Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray()), parameters).Compile();
            Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) => new([], [], []);
            var value = constructor.Invoke([Box,
                (Func<ConfigurationCaller, ConfigurationAccess, bool>)((caller, access) =>
                    caller.AuthorityId == "synthetic" && (caller.Access & access) == access),
                sources, registered, (Func<ConfigurationFieldId, ConfigurationValueSource>)(_ => ConfigurationValueSource.UserConfiguration),
                (Func<ConfigurationFieldId, FieldEffect>)(_ => FieldEffect.Hot), false]);
            _configuration = (IConfigurationChanges)typeof(StorageModule::BotAgent.Storage.ConfigurationStore)
                .GetMethod("Activate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [new DynamicProbeClock(), value])!;
        }
        internal ConfigurationReadResult Read() => _configuration.ReadAsync(_caller, ConfigurationReadKind.Editable)
            .AsTask().GetAwaiter().GetResult();
        internal ConfigurationSaveResult Save(BehaviorChanges behavior)
        {
            var header = Read();
            return _configuration.SaveAsync(_caller, new(header.ContractRevision, header.Version,
                new(header.Generation, header.ServerUnixSeconds, Guid.NewGuid()),
                new ConfigurationPatch(behavior, null, null, null, null, null, null, null), []))
                .AsTask().GetAwaiter().GetResult();
        }
    }
    private sealed class DynamicProbeClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public DateTimeOffset UtcNow => Now;
        public DateTime LocalDateTime => Now.LocalDateTime;
        public long TickCount => Environment.TickCount64;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.Delay(millisecondsDelay, ct);
    }
}
