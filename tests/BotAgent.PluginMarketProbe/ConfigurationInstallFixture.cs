extern alias StorageModule;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

internal sealed class ConfigurationInstallFixture : IAsyncDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal readonly object Runtime;
    internal readonly Type RuntimeType = typeof(PluginPackageService).Assembly.GetType("BotAgent.Host.PluginRuntime")!;
    internal PluginPackageService Packages { get; }
    internal IConfigurationChanges Configuration { get; }
    internal readonly ConfigurationCaller Caller = new("synthetic", "install", "bot",
        ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
    internal ConfigurationInstallFixture(string root, BotAgent.Adapters.Net.IHttpFetcher http)
    {
        new SettingsStore().Save(new AppSettings());
        new StorageModule::BotAgent.Storage.PluginStore().SetPluginEnabled("probe.install.stale", true);
        Runtime = RuntimeType.GetConstructors(Hidden).Single().Invoke([root, (Action<string>)(_ => { }), new TenantQuotaStore()]);
        Packages = (PluginPackageService)RuntimeType.GetProperty("Packages", Hidden)!.GetValue(Runtime)!;
        typeof(PluginPackageService).GetField("_http", Hidden)!.SetValue(Packages, http);
        var participantType = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)!;
        var constructor = participantType.GetConstructors(Hidden).Single();
        var secretDelegateType = constructor.GetParameters()[2].ParameterType;
        var parameters = secretDelegateType.GetMethod("Invoke")!.GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        var sources = Expression.Lambda(secretDelegateType, Expression.Constant(Enum.GetValues<WritableSecretSlot>().Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray()), parameters).Compile();
        Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (connection, _) =>
            new((ImmutableArray<PluginConfigurationView>)RuntimeType.GetMethod("ReadConfiguration", Hidden)!
                .Invoke(Runtime, [connection])!, [], []);
        var participant = constructor.Invoke([new SettingsBox(new AppSettings()),
            (Func<ConfigurationCaller, ConfigurationAccess, bool>)((caller, access) => (caller.Access & access) == access),
            sources, registered, (Func<ConfigurationFieldId, ConfigurationValueSource>)(_ => ConfigurationValueSource.UserConfiguration),
            (Func<ConfigurationFieldId, FieldEffect>)(_ => FieldEffect.Restart), false]);
        Configuration = (IConfigurationChanges)typeof(StorageModule::BotAgent.Storage.ConfigurationStore)
            .GetMethod("Activate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [new ProbeClock(), participant])!;
    }
    internal bool Declared(string id) => ((IEnumerable<string>)RuntimeType.GetMethod("DeclaredIds", Hidden)!.Invoke(Runtime, null)!).Contains(id);
    internal Task Reload() => (Task)RuntimeType.GetMethod("ReloadAsync", Hidden)!.Invoke(Runtime, [null, CancellationToken.None])!;
    internal async Task<ConfigurationSaveResult> Disable(string id, CancellationToken ct)
    {
        var header = await Configuration.ReadAsync(Caller, ConfigurationReadKind.Editable, ct);
        var changes = new ConfigurationPatch(null, null, null, null, null, null, null,
            new RegisteredConfigurationChanges([new PluginEnabledEdit(new(id), false)], [], []));
        return await Configuration.SaveAsync(Caller, new(header.ContractRevision, header.Version,
            new(header.Generation, header.ServerUnixSeconds, Guid.NewGuid()), changes, []), ct);
    }
    internal bool Enabled(string id) => new StorageModule::BotAgent.Storage.PluginStore().LoadPluginStates().TryGetValue(id, out var state) && state;
    public ValueTask DisposeAsync() => ((IAsyncDisposable)Runtime).DisposeAsync();
    private sealed class ProbeClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddDays(100);
        public DateTimeOffset UtcNow => Now;
        public DateTime LocalDateTime => Now.DateTime;
        public long TickCount => 100;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.CompletedTask;
    }
}