extern alias StorageModule;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Services;
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
    private readonly Func<ImmutableArray<ManagedDescriptor>>? _plugins;
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
        IManagedAuthorization? callerAuthorization = null, IDisposable? providerTransport = null,
        Func<ImmutableArray<ManagedDescriptor>>? plugins = null)
    {
        _store = store;
        _configurationAuthorization = configurationAuthorization;
        _drivers = drivers;
        _desired = desired;
        _credentials = credentials;
        _ready = ready;
        _declaredDrivers = declaredDrivers;
        _providers = providers;
        _plugins = plugins;
        _testProvider = testProvider;
        _callerAuthorization = callerAuthorization;
        _providerTransport = providerTransport;
        try { Capabilities = StorageConfiguration.ConfigurationStorageBridge.CreateManagedCapabilities(store, this, Catalog, DispatchAsync); }
        catch { _providerTransport?.Dispose(); throw; }
    }

    internal IManagedCapabilities Capabilities { get; }

    /// <summary>提供商测试/发现共用的受限出网客户端（所有权仍在本实例，调用方不得 Dispose）。</summary>
    internal BotAgent.Adapters.Net.IHttpFetcher? ProviderTransport => _providerTransport as BotAgent.Adapters.Net.IHttpFetcher;

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
        }).Concat(_providers())
          .Concat(_plugins?.Invoke() ?? ImmutableArray<ManagedDescriptor>.Empty)
          .ToImmutableArray();
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
        // 驱动目录的 DescriptorRevision 是整张目录（含各驱动的运行态）的哈希：
        // 启动阶段私域刚连上、官方还没起，运行态会变一次，可能正好落在
        // “Inspect 拿到 revision → Execute 校验 revision”之间。以前这里直接把
        // descriptor_changed 当致命错误，宿主在官方启动时整个退出（Linux 启动竞态，
        // 表现为 token 请求=0、进程 exit 134）。运行态变化不是配置/能力变化，
        // 用新的 revision 有界重试即可，不能放宽授权或吞掉真正的失败。
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
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
            if (result.Code == ManagedResultCode.Ok && result.Receipt?.State == ManagedOperationState.Completed)
            {
                return;
            }

            var errorCode = result.Receipt?.ErrorCode;
            var retryable = result.Code == ManagedResultCode.VersionConflict
                || string.Equals(errorCode, "descriptor_changed", StringComparison.Ordinal)
                || string.Equals(errorCode, "driver_not_ready", StringComparison.Ordinal);
            if (!retryable || attempt >= maxAttempts)
            {
                throw new InvalidOperationException(
                    $"driver_lifecycle_incomplete platform={key.PlatformId} action={action} result={result.Code} "
                    + $"state={result.Receipt?.State} error={errorCode ?? "(none)"} attempt={attempt} "
                    + $"driver={_drivers.TryGet(key)?.RuntimeFact.ErrorCode}");
            }

            FileLog.Warn("Platform",
                $"驱动 {key.PlatformId} {action} 的目录 revision 在启动时变化（{errorCode}），"
                + $"第 {attempt} 次重试（最多 {maxAttempts} 次）");
            await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), ct);
        }
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
