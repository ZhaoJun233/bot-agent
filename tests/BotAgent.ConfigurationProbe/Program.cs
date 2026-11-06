using System.Collections.Immutable;
using System.Text.Json;
using System.Reflection;
using System.Linq.Expressions;
using BotAgent.Adapters.Time;
using Microsoft.Data.Sqlite;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var root = Path.GetFullPath(Path.Combine(".artifacts", "pacing-config", "synthetic-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
var env = new[] { "QQCHAT_REPLY_PACING", "QQCHAT_GROUP_INTERJECTION_PROBABILITY", "QQCHAT_GROUP_INTERJECTION_COOLDOWN",
    "QQCHAT_GROUP_CONTINUATION_WAIT", "QQCHAT_PRIVATE_CONTINUATION_WAIT" };
foreach (var name in env) Environment.SetEnvironmentVariable(name, null);
var count = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
void Defaults(AppSettings settings)
{
    Check(!settings.ReplyPacingEnabled && settings.GroupInterjectionProbability == 20
        && settings.GroupInterjectionCooldownSeconds == 30 && settings.GroupContinuationWaitSeconds == 3
        && settings.PrivateContinuationWaitSeconds == 1.5, "backward compatible defaults");
}
AppSettings Seed() => (AppSettings)typeof(BotConfig).GetMethod("CreateBehaviorSeed", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
try
{
    Defaults(new AppSettings());
    Defaults(JsonSerializer.Deserialize<AppSettings>("{\"AiDesire\":42}")!);
    Defaults(Seed());
    Environment.SetEnvironmentVariable(env[0], "true");
    Environment.SetEnvironmentVariable(env[1], "45");
    Environment.SetEnvironmentVariable(env[2], "60");
    Environment.SetEnvironmentVariable(env[3], "4");
    Environment.SetEnvironmentVariable(env[4], "1.5");
    var seeded = Seed();
    Check(seeded.ReplyPacingEnabled && seeded.GroupInterjectionProbability == 45 && seeded.GroupInterjectionCooldownSeconds == 60
        && seeded.GroupContinuationWaitSeconds == 4 && seeded.PrivateContinuationWaitSeconds == 1.5, "environment seeds");
    foreach (var bad in new[] { "-1", "31", "NaN", "Infinity", "1,5", "invalid" })
    {
        Environment.SetEnvironmentVariable(env[4], bad);
        Check(Seed().PrivateContinuationWaitSeconds == 1.5, "invalid private seed ignored");
    }
    foreach (var (index, maximum) in new[] { (1, 100), (2, 3600), (3, 30) })
    {
        foreach (var bad in new[] { "-1", (maximum + 1).ToString(), "1.5", "NaN", "Infinity" })
        {
            foreach (var name in env) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(env[index], bad);
            Defaults(Seed());
        }
    }
    foreach (var name in env) Environment.SetEnvironmentVariable(name, null);
    var sampling = new AppSettings { ReplyPacingEnabled = true }.ToSamplingConfig();
    Check(sampling.ReplyPacingEnabled, "sampling projection enables pacing");
    Check(!new AppSettings().ToSamplingConfig().ReplyPacingEnabled, "legacy sampling remains default");
    Check(ConfigurationRuntimeTracker.Resolve(ConfigurationFieldId.ReplyPacingEnabled, FieldEffect.Hot, true,
        ImmutableDictionary<ApplyStep, ApplyState>.Empty.Add(ApplyStep.ModelProjection, ApplyState.Failed))
        == ConfigurationRuntimeState.ApplyFailed, "pacing mode depends on successful model projection");

    ClockBindings.InitializeStorage();
    ClockBindings.InitializePlatforms();
    var legacySchema = args.Contains("--legacy-schema", StringComparer.Ordinal);
    if (legacySchema)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppDatabase.FilePath)!);
        using var legacy = new SqliteConnection($"Data Source={AppDatabase.FilePath}");
        legacy.Open();
        using var command = legacy.CreateCommand();
        command.CommandText = """
            CREATE TABLE configuration_field_modes(
              field_id INTEGER PRIMARY KEY CHECK(field_id BETWEEN 1 AND 147),
              user_mode INTEGER NOT NULL CHECK(user_mode BETWEEN 0 AND 2));
            INSERT INTO configuration_field_modes VALUES(26,1),(147,2);
            PRAGMA user_version=13;
            """;
        command.ExecuteNonQuery();
    }
    AppDatabase.Initialize();
    Check(AppDatabase.Scalar<long>("PRAGMA user_version") == 14, "fresh and legacy databases support reply pacing schema v14");
    if (legacySchema)
        Check(AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_field_modes WHERE (field_id=26 AND user_mode=1) OR (field_id=147 AND user_mode=2)") == 2,
            "v13 migration retains existing field provenance");
    var settings = new SettingsStore();
    var initial = new AppSettings { AiDesire = 42, Model = "synthetic-model", ModelOverride = "synthetic-model",
        ModelBaseUrl = "https://example.com", ModelBaseUrlOverride = "https://example.com", AgentDevices = "" };
    settings.Save(initial);
    AppDatabase.Write(connection => AppDatabase.Exec(connection,
        "UPDATE settings SET json=json_remove(json,'$.ReplyPacingEnabled','$.GroupInterjectionProbability','$.GroupInterjectionCooldownSeconds','$.GroupContinuationWaitSeconds','$.PrivateContinuationWaitSeconds') WHERE id=1"));
    var providers = new ModelProviderStore();
    providers.EnsurePrimary(initial);
    var runtime = new SettingsBox(initial);
    // Exercise the actual host adapter without adding a production test-only visibility grant.
    var adapterType = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)!;
    var constructor = adapterType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
    var secretDelegateType = constructor.GetParameters()[2].ParameterType;
    var secretParameters = secretDelegateType.GetMethod("Invoke")!.GetParameters()
        .Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
    var secretMetadata = Enum.GetValues<WritableSecretSlot>()
        .Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray();
    var secretDelegate = Expression.Lambda(secretDelegateType, Expression.Constant(secretMetadata), secretParameters).Compile();
    Func<ConfigurationCaller, ConfigurationAccess, bool> authorize = (_, _) => true;
    Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) => new(
        ImmutableArray<PluginConfigurationView>.Empty, ImmutableArray<ProviderConfigurationView>.Empty, ImmutableArray<ModelCredentialView>.Empty);
    Func<ConfigurationFieldId, ConfigurationValueSource> source = _ => ConfigurationValueSource.UserConfiguration;
    Func<ConfigurationFieldId, FieldEffect> effect = _ => FieldEffect.Hot;
    var adapter = constructor.Invoke(new object[] { runtime, authorize, secretDelegate, registered, source, effect, false });
    var bridge = Assembly.Load("BotAgent.Storage").GetType("BotAgent.Storage.ConfigurationStorageBridge")!;
    var facade = bridge.GetMethod("Activate", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new[] { ClockBindings.Source, adapter })!;
    var caller = new ConfigurationCaller("synthetic-authority", "synthetic-owner", "bot",
        ConfigurationAccess.ReadEditable | ConfigurationAccess.ReadOverview | ConfigurationAccess.SaveConfiguration);
    ValueTask<ConfigurationReadResult> Read() => (ValueTask<ConfigurationReadResult>)facade.GetType().GetMethod("ReadAsync")!
        .Invoke(facade, new object[] { caller, ConfigurationReadKind.Editable, CancellationToken.None })!;
    var read = await Read();
    Check(read.Code == ConfigurationResultCode.Ok, "synthetic facade read");
    Check(read.View!.Behavior.PrivateContinuationWaitSeconds.EffectiveSource == ConfigurationValueSource.Default, "missing legacy property has default provenance");
    ConfigurationSaveRequest? lastRequest = null;
    ValueTask<ConfigurationSaveResult> Persist(ConfigurationSaveRequest request) => (ValueTask<ConfigurationSaveResult>)facade.GetType().GetMethod("SaveAsync")!
        .Invoke(facade, new object[] { caller, request, CancellationToken.None })!;
    async Task<ConfigurationSaveResult> Save(BehaviorChanges changes)
    {
        var current = await Read();
        var request = new ConfigurationSaveRequest(ConfigurationContract.Revision, current.Version,
            new ConfigurationOperationKey(current.Generation, current.ServerUnixSeconds, Guid.NewGuid()),
            new ConfigurationPatch(changes, null, null, null, null, null, null, null), ImmutableArray<SecretEdit>.Empty);
        lastRequest = request;
        return await Persist(request);
    }
    var saved = await Save(new BehaviorChanges { ReplyPacingEnabled = FieldEdit<bool>.Set(true),
        GroupInterjectionProbability = FieldEdit<int>.Set(45), GroupInterjectionCooldownSeconds = FieldEdit<int>.Set(60),
        GroupContinuationWaitSeconds = FieldEdit<int>.Set(4), PrivateContinuationWaitSeconds = FieldEdit<double>.Set(2.5) });
    Check(saved.Code == ConfigurationResultCode.Ok && saved.Receipt is not null, $"five field save commits: {saved.Code}; {string.Join(",", saved.Errors.Select(error => error.Address.Field + ":" + error.Code))}");
    var replay = await Persist(lastRequest!);
    Check(replay.Replayed && replay.Receipt?.CommittedVersion == saved.Receipt?.CommittedVersion, "pacing save preserves idempotent receipt");
    var conflicting = lastRequest! with { Changes = new(new BehaviorChanges { PrivateContinuationWaitSeconds = FieldEdit<double>.Set(2.6) },
        null, null, null, null, null, null, null) };
    Check((await Persist(conflicting)).Code == ConfigurationResultCode.IdempotencyConflict, "fractional pacing participates in fingerprint");
    var reloaded = settings.Load();
    Check(reloaded.ReplyPacingEnabled && reloaded.GroupInterjectionProbability == 45 && reloaded.GroupInterjectionCooldownSeconds == 60
        && reloaded.GroupContinuationWaitSeconds == 4 && reloaded.PrivateContinuationWaitSeconds == 2.5 && reloaded.AiDesire == 42,
        "round trip preserves fractions and old desire");
    var after = await Read();
    Check(after.View!.Behavior.PrivateContinuationWaitSeconds.EffectiveSource == ConfigurationValueSource.UserConfiguration,
        "save projection shows user source");
    var baselineVersion = after.Version;
    foreach (var value in new[] { -0.1, 30.1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        var rejected = await Save(new BehaviorChanges { PrivateContinuationWaitSeconds = FieldEdit<double>.Set(value) });
        Check(rejected.Code == ConfigurationResultCode.ValidationFailed, "invalid private value rejected");
        Check(rejected.Errors.Any(error => error.Address.Field == ConfigurationFieldId.PrivateContinuationWaitSeconds), "private validation identifies field");
    }
    foreach (var changes in new[] {
        new BehaviorChanges { GroupInterjectionProbability = FieldEdit<int>.Set(101) },
        new BehaviorChanges { GroupInterjectionProbability = FieldEdit<int>.Set(-1) },
        new BehaviorChanges { GroupInterjectionCooldownSeconds = FieldEdit<int>.Set(-1) },
        new BehaviorChanges { GroupInterjectionCooldownSeconds = FieldEdit<int>.Set(3601) },
        new BehaviorChanges { GroupContinuationWaitSeconds = FieldEdit<int>.Set(-1) },
        new BehaviorChanges { GroupContinuationWaitSeconds = FieldEdit<int>.Set(31) } })
        Check((await Save(changes)).Code == ConfigurationResultCode.ValidationFailed, "integer range rejected");
    Check((await Read()).Version == baselineVersion,
        "failed validation does not advance version");
    foreach (var changes in new[] {
        new BehaviorChanges { GroupInterjectionProbability = FieldEdit<int>.Set(0),
            GroupInterjectionCooldownSeconds = FieldEdit<int>.Set(0), GroupContinuationWaitSeconds = FieldEdit<int>.Set(0) },
        new BehaviorChanges { GroupInterjectionProbability = FieldEdit<int>.Set(100),
            GroupInterjectionCooldownSeconds = FieldEdit<int>.Set(3600), GroupContinuationWaitSeconds = FieldEdit<int>.Set(30) } })
        Check((await Save(changes)).Code == ConfigurationResultCode.Ok, "integer boundaries accepted");
    foreach (var value in new[] { 0.0, 1.5, 30.0 })
        Check((await Save(new BehaviorChanges { PrivateContinuationWaitSeconds = FieldEdit<double>.Set(value) })).Code == ConfigurationResultCode.Ok,
            "private boundary or fraction accepted");
    Check((await Save(new BehaviorChanges { ReplyPacingEnabled = FieldEdit<bool>.Set(false),
        PrivateContinuationWaitSeconds = FieldEdit<double>.Set(1.5) })).Code == ConfigurationResultCode.Ok, "reset pacing fields");
    reloaded = settings.Load();
    Check(!reloaded.ReplyPacingEnabled && reloaded.PrivateContinuationWaitSeconds == 1.5 && reloaded.AiDesire == 42, "reset defaults retains legacy desire");
    Environment.SetEnvironmentVariable(env[4], "9.5");
    Check(BotConfig.Load().PrivateContinuationWaitSeconds == 1.5, "stored configuration wins over environment seed");
    Console.WriteLine($"PASS configuration pacing: {count} synthetic checks");
}
finally
{
    foreach (var name in env) Environment.SetEnvironmentVariable(name, null);
    // The isolated synthetic directory is retained for inspecting failed probes; no live data is used.
}
