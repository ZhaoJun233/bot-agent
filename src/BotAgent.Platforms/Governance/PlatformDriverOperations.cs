using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Adapters.Platforms;

namespace BotAgent.Services.Platforms;

/// <summary>Runtime facts only. Desired state, credentials, authorization and receipts belong to the host.</summary>
public sealed record PlatformDriverRuntimeFact(DriverKey Key, ManagedRuntimeState State,
    ImmutableArray<ActionCapability> Actions, string? ErrorCode)
{
    public override string ToString() => "PlatformDriverRuntimeFact";
}

public sealed record PlatformDriverActionResult(ManagedResultCode Code, ManagedOperationState State, string? ErrorCode)
{
    public override string ToString() => "PlatformDriverActionResult";
}

/// <summary>One real adapter and its owned connection. A retired lifetime is never made usable again.</summary>
public sealed class PlatformDriverRegistration
{
    private readonly Func<CancellationToken, Task> _start;
    private readonly Func<CancellationToken, Task> _stop;
    private readonly Func<CancellationToken, Task>? _test;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource _lifetime = new();
    private bool _running = true;
    private ManagedRuntimeState? _transition;
    private string? _error;
    private bool _cleanupPending;

    public PlatformDriverRegistration(IPlatformAdapter adapter, Func<CancellationToken, Task> start,
        Func<CancellationToken, Task> stop, Func<CancellationToken, Task>? testConnection = null,
        bool initiallyRunning = true)
    {
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        _test = testConnection;
        var context = adapter.Context;
        if (string.IsNullOrWhiteSpace(context.PlatformId) || string.IsNullOrWhiteSpace(context.AccountScope)
            || context.InstanceId is null || (context.InstanceId.Length > 0 && string.IsNullOrWhiteSpace(context.InstanceId)))
            throw new ArgumentException("invalid_platform_context", nameof(adapter));
        Key = new DriverKey(PlatformId.Normalize(context.PlatformId), context.AccountScope, context.InstanceId);
        _running = initiallyRunning;
        if (!initiallyRunning) _lifetime.Cancel();
    }

    public IPlatformAdapter Adapter { get; }
    public DriverKey Key { get; }
    public CancellationToken Lifetime { get { lock (_gate) return _lifetime.Token; } }
    public bool IsActive { get { lock (_gate) return _running && !_lifetime.IsCancellationRequested; } }
    public PlatformDriverRuntimeFact RuntimeFact
    {
        get
        {
            lock (_gate)
            {
                var state = _transition ?? (!_running ? ManagedRuntimeState.Disabled
                    : Adapter.IsConnected ? ManagedRuntimeState.Running : ManagedRuntimeState.Disconnected);
                return new(Key, state, ImmutableArray.Create(
                    new ActionCapability(ManagedAction.Start, true, "supported"),
                    new ActionCapability(ManagedAction.Stop, true, "supported"),
                    new ActionCapability(ManagedAction.TestConnection, _test is not null,
                        _test is null ? "unsupported_action" : "supported")), _error ?? Adapter.LastErrorCode);
            }
        }
    }

    internal async Task<PlatformDriverActionResult> DispatchAsync(DriverCommand command, CancellationToken ct)
    {
        if (command is not (DriverCommand.Start or DriverCommand.Stop or DriverCommand.TestConnection)
            || (command == DriverCommand.TestConnection && _test is null))
            return new(ManagedResultCode.UnsupportedAction, ManagedOperationState.NotStarted, "unsupported_action");
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (command == DriverCommand.Start && _cleanupPending)
                    return new(ManagedResultCode.Unknown, ManagedOperationState.NotStarted, "driver_cleanup_pending");
                if (command == DriverCommand.Start && _running)
                    return new(ManagedResultCode.Ok, ManagedOperationState.Completed, null);
                if (command == DriverCommand.Stop && !_running && _transition is null)
                    return new(ManagedResultCode.Ok, ManagedOperationState.Completed, null);
                _error = null;
                if (command == DriverCommand.Start)
                {
                    _lifetime.Dispose();
                    _lifetime = new CancellationTokenSource();
                    _transition = ManagedRuntimeState.Starting;
                }
                else if (command == DriverCommand.Stop)
                {
                    _running = false;
                    _transition = ManagedRuntimeState.Stopping;
                }
            }
            if (command == DriverCommand.Stop) _lifetime.Cancel();
            try
            {
                var operation = command switch
                {
                    DriverCommand.Start => _start,
                    DriverCommand.Stop => _stop,
                    _ => _test!,
                };
                if (command == DriverCommand.Stop && Adapter is QqChatSourcePlatformAdapter stoppingAdapter)
                    await stoppingAdapter.StopAsync().ConfigureAwait(false);
                await operation(ct).ConfigureAwait(false);
                if (command == DriverCommand.Start && Adapter is QqChatSourcePlatformAdapter startingAdapter)
                    await startingAdapter.StartAsync(ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (command == DriverCommand.Start) _running = true;
                    if (command == DriverCommand.Stop) _cleanupPending = false;
                    _transition = null;
                }
                return new(ManagedResultCode.Ok, ManagedOperationState.Completed, null);
            }
            catch (OperationCanceledException)
            {
                var cleaned = command == DriverCommand.Start && await CleanupFailedStartAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (command == DriverCommand.Start) _cleanupPending = !cleaned;
                    _transition = cleaned ? null : ManagedRuntimeState.Unknown;
                    _error = command == DriverCommand.Start && !cleaned ? "driver_cleanup_failed" : "driver_operation_cancelled";
                }
                return new(ManagedResultCode.Unknown, ManagedOperationState.Unknown, _error);
            }
            catch (Exception ex)
            {
                var cleaned = command == DriverCommand.Start && await CleanupFailedStartAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (command == DriverCommand.Start) _cleanupPending = !cleaned;
                    _transition = cleaned ? null : command == DriverCommand.Start ? ManagedRuntimeState.Unknown : ManagedRuntimeState.Faulted;
                    _error = command == DriverCommand.Start && !cleaned
                        ? "driver_cleanup_failed"
                        : "driver_operation_failed:" + ex.GetType().Name;
                }
                return new(ManagedResultCode.Unknown, command == DriverCommand.Start && !cleaned
                    ? ManagedOperationState.Unknown : ManagedOperationState.Failed, _error);
            }
        }
        finally { _operations.Release(); }
    }

    private async Task<bool> CleanupFailedStartAsync()
    {
        _lifetime.Cancel();
        var clean = true;
        if (Adapter is QqChatSourcePlatformAdapter legacy)
        {
            try { await legacy.StopAsync().ConfigureAwait(false); }
            catch (Exception) { clean = false; }
        }
        try { await _stop(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { clean = false; }
        lock (_gate) _running = false;
        return clean;
    }
}

/// <summary>Called only after the shared managed service has authorized and durably accepted an operation.</summary>
public sealed class PlatformDriverOperations
{
    private readonly IReadOnlyDictionary<DriverKey, PlatformDriverRegistration> _drivers;

    public PlatformDriverOperations(IEnumerable<PlatformDriverRegistration> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        var registrations = new Dictionary<DriverKey, PlatformDriverRegistration>();
        foreach (var driver in drivers)
        {
            ArgumentNullException.ThrowIfNull(driver);
            if (!registrations.TryAdd(driver.Key, driver)) throw new ArgumentException("duplicate_platform_instance", nameof(drivers));
        }
        _drivers = registrations;
    }

    public IReadOnlyList<PlatformDriverRuntimeFact> RuntimeFacts => _drivers.Values.Select(driver => driver.RuntimeFact).ToArray();
    public PlatformDriverRegistration? TryGet(DriverKey key) => _drivers.GetValueOrDefault(key);

    public Task<PlatformDriverActionResult> DispatchAsync(DriverKey key, DriverCommand command, CancellationToken ct = default)
        => _drivers.TryGetValue(key, out var driver) ? driver.DispatchAsync(command, ct)
            : Task.FromResult(new PlatformDriverActionResult(ManagedResultCode.NotFound, ManagedOperationState.NotStarted, "driver_not_found"));
}

/// <summary>Counts owned requests so a stopped driver cannot leave requests or callbacks running.</summary>
internal sealed class PlatformOperationLifetime : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _source = new();
    private TaskCompletionSource _idle = CompletedIdle();
    private int _active;
    private bool _stopped;
    private bool _stopping;
    private bool _disposed;

    internal bool IsActive { get { lock (_gate) return !_stopped; } }
    internal Lease? TryEnter(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_stopped) return null;
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Lease(this, CancellationTokenSource.CreateLinkedTokenSource(ct, _source.Token));
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_stopped) return;
            if (_stopping || _active != 0) throw new InvalidOperationException("driver_cleanup_pending");
            _source.Dispose();
            _source = new CancellationTokenSource();
            _stopped = false;
        }
    }

    internal async Task StopAsync()
    {
        CancellationTokenSource source;
        Task idle;
        lock (_gate)
        {
            if (_disposed) return;
            _stopped = true;
            _stopping = true;
            source = _source;
            idle = _idle.Task;
        }
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        await idle.ConfigureAwait(false);
        lock (_gate) _stopping = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_stopped || _active != 0) throw new InvalidOperationException("driver_cleanup_pending");
            _disposed = true;
            _source.Dispose();
        }
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.SetResult();
        return idle;
    }

    internal sealed class Lease : IDisposable
    {
        private PlatformOperationLifetime? _owner;
        private readonly CancellationTokenSource _linked;
        internal Lease(PlatformOperationLifetime owner, CancellationTokenSource linked) { _owner = owner; _linked = linked; }
        internal CancellationToken Token => _linked.Token;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            _linked.Dispose();
            lock (owner._gate)
            {
                if (--owner._active == 0) owner._idle.TrySetResult();
            }
        }
    }
}
