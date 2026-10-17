extern alias StorageModule;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Services.Platforms;
using BotAgent.Services.Settings;
using StorageConfiguration = StorageModule::BotAgent.Storage;

namespace BotAgent.Host;

/// <summary>Trusted in-process composition; configuration access never implies management permission.</summary>
internal sealed class ManagedCapabilityBinding : IManagedAuthorization, IAsyncDisposable
{
    private readonly ConfigurationCaller _bootstrap = new("host-lifecycle", "bootstrap", "bot", ConfigurationAccess.ReadOverview);
    private readonly StorageConfiguration.ConfigurationStore _store;
    private readonly SettingsHotReload.ConfigurationRequestAuthorization _configurationAuthorization;
    private readonly PlatformDriverOperations _drivers;
    private readonly Func<ImmutableArray<ManagedDescriptor>> _providers;
    private readonly Func<ManagedCommand.Provider, CancellationToken, ValueTask<StorageConfiguration.ManagedDispatchResult>> _testProvider;
    private readonly Func<DriverKey, bool> _desired;
    private readonly Func<DriverKey, bool> _credentials;
    private readonly Func<DriverKey, bool> _ready;
    private readonly Func<ImmutableArray<DriverKey>> _declaredDrivers;
    private readonly IManagedAuthorization? _callerAuthorization;
    private readonly IDisposable? _providerTransport;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    internal ManagedCapabilityBinding(StorageConfiguration.ConfigurationStore store,
        SettingsHotReload.ConfigurationRequestAuthorization configurationAuthorization,
        PlatformDriverOperations drivers, Func<ImmutableArray<DriverKey>> declaredDrivers,
        Func<DriverKey, bool> desired, Func<DriverKey, bool> credentials, Func<DriverKey, bool> ready,
        Func<ImmutableArray<ManagedDescriptor>> providers,
        Func<ManagedCommand.Provider, CancellationToken, ValueTask<StorageConfiguration.ManagedDispatchResult>> testProvider,
        IManagedAuthorization? callerAuthorization = null, IDisposable? providerTransport = null)
    {
        _store = store;
        _configurationAuthorization = configurationAuthorization;
        _drivers = drivers;
        _desired = desired;
        _credentials = credentials;
        _ready = ready;
        _declaredDrivers = declaredDrivers;
        _providers = providers;
        _testProvider = testProvider;
        _callerAuthorization = callerAuthorization;
        _providerTransport = providerTransport;
        try { Capabilities = StorageConfiguration.ConfigurationStorageBridge.CreateManagedCapabilities(store, this, Catalog, DispatchAsync); }
        catch { _providerTransport?.Dispose(); throw; }
    }

    internal IManagedCapabilities Capabilities { get; }

    public ValueTask<bool> IsAllowedAsync(ManagedAuthorizationRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Only the exact private bootstrap object can operate lifecycle. No serialized impersonation.
        if (!ReferenceEquals(request.Caller, _bootstrap))
            return _callerAuthorization?.IsAllowedAsync(request, ct) ?? ValueTask.FromResult(false);
        return ValueTask.FromResult(ReferenceEquals(request.Caller, _bootstrap)
            && request.Target is ManagedObjectKey.Driver driver && _drivers.TryGet(driver.Key) is not null
            && (request.Permission == ManagedPermission.Inspect
                || request.Action is ManagedAction.Start or ManagedAction.Stop));
    }

    private StorageConfiguration.ManagedCatalogSnapshot Catalog()
    {
        var loaded = _drivers.RuntimeFacts.ToDictionary(fact => fact.Key);
        var items = _declaredDrivers().Concat(loaded.Keys).Distinct().Select(key =>
        {
            var present = loaded.TryGetValue(key, out var fact);
            var actions = present ? fact!.Actions : ImmutableArray.Create(
                new ActionCapability(ManagedAction.Start, false, "driver_not_loaded"),
                new ActionCapability(ManagedAction.Stop, false, "driver_not_loaded"),
                new ActionCapability(ManagedAction.TestConnection, false, "driver_not_loaded"));
            return new ManagedDescriptor(new ManagedObjectKey.Driver(key), "platform-driver", _desired(key),
                present ? fact!.State : ManagedRuntimeState.NotLoaded, actions, _credentials(key), fact?.ErrorCode, "");
        }).Concat(_providers()).ToImmutableArray();
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items))));
        return new(revision, items.Select(item => item with { DescriptorRevision = revision }).ToImmutableArray());
    }

    private async ValueTask<StorageConfiguration.ManagedDispatchResult> DispatchAsync(ManagedCommand command, CancellationToken ct)
    {
        if (command is ManagedCommand.Driver driver)
        {
            // Start must not resurrect a disabled or credential-incomplete configured driver.
            if (driver.Action == DriverCommand.Start && (!_desired(driver.Key) || !_ready(driver.Key)))
                return new(ManagedOperationState.Failed, "driver_not_ready");
            var result = await _drivers.DispatchAsync(driver.Key, driver.Action, ct).ConfigureAwait(false);
            return new(result.State, result.ErrorCode);
        }
        return command is ManagedCommand.Provider provider
            ? await _testProvider(provider, ct).ConfigureAwait(false)
            : new(ManagedOperationState.Failed, "unsupported_action");
    }

    internal async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var fact in _drivers.RuntimeFacts)
            // Credential presence is reported separately; optional tokens (OneBot) must not block startup.
            if (_desired(fact.Key) && _ready(fact.Key)) await ExecuteLifecycleAsync(fact.Key, DriverCommand.Start, ct);
    }

    internal async Task StopAsync(CancellationToken ct = default)
    {
        var failures = new List<Exception>();
        foreach (var fact in _drivers.RuntimeFacts.Reverse())
        {
            try { await ExecuteLifecycleAsync(fact.Key, DriverCommand.Stop, ct); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0) throw new AggregateException("driver_shutdown_incomplete", failures);
    }

    private async Task ExecuteLifecycleAsync(DriverKey key, DriverCommand action, CancellationToken ct)
    {
        ConfigurationReadResult header;
        using (_configurationAuthorization.Enter(_bootstrap, () => true))
            header = await _store.ReadAsync(_bootstrap, ConfigurationReadKind.Overview, ct);
        if (header.Code != ConfigurationResultCode.Ok) throw new InvalidOperationException("lifecycle_header_unavailable");
        var descriptor = await Capabilities.InspectAsync(_bootstrap, new ManagedObjectKey.Driver(key), ct);
        if (descriptor.Descriptor is null) throw new InvalidOperationException("lifecycle_descriptor_unavailable");
        var result = await Capabilities.ExecuteAsync(_bootstrap,
            new(header.ContractRevision, descriptor.Descriptor.DescriptorRevision,
                new(header.Generation, header.ServerUnixSeconds, Guid.NewGuid()), new ManagedCommand.Driver(key, action)), ct);
        if (result.Code != ManagedResultCode.Ok || result.Receipt?.State != ManagedOperationState.Completed)
            throw new InvalidOperationException(
                $"driver_lifecycle_incomplete platform={key.PlatformId} action={action} result={result.Code} "
                + $"state={result.Receipt?.State} driver={_drivers.TryGet(key)?.RuntimeFact.ErrorCode}");
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try { await ((IAsyncDisposable)Capabilities).DisposeAsync().ConfigureAwait(false); }
        finally { _providerTransport?.Dispose(); }
    }
}
