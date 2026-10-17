using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Platforms;

namespace BotAgent.Storage;

internal sealed record ManagedCatalogSnapshot(string DescriptorRevision, ImmutableArray<ManagedDescriptor> Items);
internal sealed record ManagedDispatchResult(ManagedOperationState State, string? ErrorCode);

/// <summary>One coordinator over the existing configuration writer; runtime facts and dispatch come from the trusted host.</summary>
internal sealed class ManagedCapabilityService : IManagedCapabilities, IAsyncDisposable
{
    private const string Revision = "cfg-0.3.2";
    private static readonly JsonSerializerOptions Codec = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly ConfigurationStore _store;
    private readonly IManagedAuthorization _authorization;
    private readonly Func<ManagedCatalogSnapshot> _catalog;
    private readonly Func<ManagedCommand, CancellationToken, ValueTask<ManagedDispatchResult>> _dispatch;
    private readonly byte[] _cursorKey = RandomNumberGenerator.GetBytes(32);
    private readonly object _lifetimeGate = new();
    private readonly ConcurrentDictionary<OperationIdentity, Work> _work = new();
    private bool _disposed;
    private Task? _disposeTask;

    internal ManagedCapabilityService(ConfigurationStore store, IManagedAuthorization authorization,
        Func<ManagedCatalogSnapshot> catalog,
        Func<ManagedCommand, CancellationToken, ValueTask<ManagedDispatchResult>> dispatch)
    { _store = store; _authorization = authorization; _catalog = catalog; _dispatch = dispatch; }

    internal bool HasBindings(IManagedAuthorization authorization, Func<ManagedCatalogSnapshot> catalog,
        Func<ManagedCommand, CancellationToken, ValueTask<ManagedDispatchResult>> dispatch)
        => ReferenceEquals(_authorization, authorization) && ReferenceEquals(_catalog, catalog)
            && ReferenceEquals(_dispatch, dispatch);

    public async ValueTask<ManagedPage> ListAsync(ConfigurationCaller caller, ManagedPageRequest request,
        CancellationToken ct = default)
    {
        ManagedPage Empty(ManagedResultCode code) => new(code, ImmutableArray<ManagedDescriptor>.Empty, null, "");
        if (!ConfigurationStore.ValidManagedCaller(caller)) return Empty(ManagedResultCode.Forbidden);
        if (request is null || request.Limit is < 0 or > 100 || request.Kind is { } kind && !Enum.IsDefined(kind))
            return Empty(ManagedResultCode.ValidationFailed);
        ct.ThrowIfCancellationRequested();
        try
        {
            var snapshot = Catalog();
            var visible = ImmutableArray.CreateBuilder<ManagedDescriptor>();
            foreach (var item in snapshot.Items.OrderBy(item => KeyOrder(item.Key), StringComparer.Ordinal))
            {
                if (request.Kind is { } filter && Kind(item.Key) != filter) continue;
                var allowed = await Authorize(caller, item.Key, ManagedPermission.Inspect, null, ct);
                if (allowed is null) return Empty(ManagedResultCode.Unknown);
                if (allowed.Value) visible.Add(item);
            }
            if (Catalog().DescriptorRevision != snapshot.DescriptorRevision) return Empty(ManagedResultCode.StaleCursor);
            var items = visible.ToImmutable();
            var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(items, Codec)));
            var offset = 0;
            if (request.Cursor is not null)
            {
                var cursor = ReadCursor(request.Cursor);
                if (cursor is null || cursor.CallerHash != CallerHash(caller) || cursor.Resource != caller.ResourceId
                    || cursor.Kind != (request.Kind is { } requested ? (int)requested : -1)) return Empty(ManagedResultCode.InvalidCursor);
                if (cursor.Revision != revision) return Empty(ManagedResultCode.StaleCursor);
                if (cursor.Offset < 0 || cursor.Offset > items.Length) return Empty(ManagedResultCode.InvalidCursor);
                offset = cursor.Offset;
            }
            var limit = request.Limit == 0 ? 50 : request.Limit;
            var page = items.Skip(offset).Take(limit).ToImmutableArray();
            var next = offset + page.Length;
            var token = next < items.Length ? WriteCursor(new(CallerHash(caller), caller.ResourceId,
                request.Kind is { } selected ? (int)selected : -1, revision, next)) : null;
            return new(ManagedResultCode.Ok, page, token, revision);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Empty(ManagedResultCode.Unknown); }
    }

    public async ValueTask<ManagedInspectResult> InspectAsync(ConfigurationCaller caller, ManagedObjectKey key,
        CancellationToken ct = default)
    {
        if (!ConfigurationStore.ValidManagedCaller(caller) || key is null) return new(ManagedResultCode.Forbidden, null);
        var allowed = await Authorize(caller, key, ManagedPermission.Inspect, null, ct);
        if (allowed is not true) return new(allowed is null ? ManagedResultCode.Unknown : ManagedResultCode.Forbidden, null);
        try
        {
            var descriptor = Catalog().Items.SingleOrDefault(item => item.Key == key);
            return new(descriptor is null ? ManagedResultCode.NotFound : ManagedResultCode.Ok, descriptor);
        }
        catch (Exception) { return new(ManagedResultCode.Unknown, null); }
    }

    public ValueTask<ManagedValidationResult> ValidateAsync(ConfigurationCaller caller,
        ConfigurationValidationRequest request, CancellationToken ct = default)
        => _store.ValidateAsync(caller, request, ct);

    public async ValueTask<ManagedOperationResult> ExecuteAsync(ConfigurationCaller caller, ManagedExecuteRequest request,
        CancellationToken ct = default)
    {
        if (!ConfigurationStore.ValidManagedCaller(caller)) return Result(ManagedResultCode.Forbidden);
        if (request is null || !ConfigurationStore.ValidManagedKey(request.Key)
            || !TryCommand(request.Command, out var command, out var target, out var action)
            || string.IsNullOrWhiteSpace(request.ExpectedDescriptorRevision) || request.ExpectedDescriptorRevision.Length > 256)
            return Result(ManagedResultCode.ValidationFailed);
        if (request.ContractRevision != Revision) return Result(ManagedResultCode.UnsupportedContractRevision);
        request = request with { Command = command! };
        ct.ThrowIfCancellationRequested();
        var permission = await Authorize(caller, target!, ManagedPermission.Execute, action, ct);
        if (permission is not true) return Result(permission is null ? ManagedResultCode.Unknown : ManagedResultCode.Forbidden);
        ManagedOperationResult accepted;
        try
        {
            var previous = _store.PeekManaged(caller, request.Key);
            if (previous is not null)
            {
                var replayPermission = await Authorize(caller, previous.Target, ManagedPermission.Execute, previous.Action, ct);
                if (replayPermission is not true)
                    return Result(replayPermission is null ? ManagedResultCode.Unknown : ManagedResultCode.Forbidden);
                return _store.AcceptManaged(caller, request, target!, action);
            }
            var descriptor = Catalog().Items.SingleOrDefault(item => item.Key == target);
            if (descriptor is null) return Result(ManagedResultCode.NotFound);
            if (descriptor.DescriptorRevision != request.ExpectedDescriptorRevision) return Result(ManagedResultCode.VersionConflict);
            if (!descriptor.Actions.Any(capability => capability.Action == action && capability.Supported))
                return Result(ManagedResultCode.UnsupportedAction);
            permission = await Authorize(caller, target!, ManagedPermission.Execute, action, ct);
            if (permission is not true) return Result(permission is null ? ManagedResultCode.Unknown : ManagedResultCode.Forbidden);
            lock (_lifetimeGate)
            {
                if (_disposed) return Result(ManagedResultCode.Unknown);
                ct.ThrowIfCancellationRequested();
                accepted = _store.AcceptManaged(caller, request, target!, action);
                if (accepted.Code != ManagedResultCode.Ok || accepted.Replayed || accepted.Receipt is null) return accepted;
                var identity = new OperationIdentity(caller.AuthorityId, caller.SubjectId, caller.ResourceId, request.Key);
                var work = new Work();
                if (!_work.TryAdd(identity, work)) { work.Cancellation.Dispose(); return Result(ManagedResultCode.Unknown); }
                var ownedRequest = request;
                work.Runner = Task.Run(() => DispatchAsync(identity, caller, ownedRequest, work));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Result(ManagedResultCode.StorageUnavailable); }
        // The caller's wait token is deliberately not linked to the owned operation cancellation source.
        if (_work.TryGetValue(new(caller.AuthorityId, caller.SubjectId, caller.ResourceId, request.Key), out var running))
            return await running.Completion.Task.WaitAsync(ct);
        return _store.FindManaged(caller, request.Key);
    }

    public async ValueTask<ManagedOperationResult> QueryAsync(ConfigurationCaller caller, ManagedOperationKey key,
        CancellationToken ct = default)
    {
        if (!ConfigurationStore.ValidManagedCaller(caller)) return Result(ManagedResultCode.Forbidden);
        if (!ConfigurationStore.ValidManagedKey(key)) return Result(ManagedResultCode.ValidationFailed);
        try
        {
            var original = _store.PeekManaged(caller, key);
            if (original is null) return Result(ManagedResultCode.Forbidden);
            var allowed = await Authorize(caller, original.Target, ManagedPermission.Query, original.Action, ct);
            return allowed is true ? _store.FindManaged(caller, key)
                : Result(allowed is null ? ManagedResultCode.Unknown : ManagedResultCode.Forbidden);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Result(ManagedResultCode.StorageUnavailable); }
    }

    public async ValueTask<ManagedCancellationResult> CancelAsync(ConfigurationCaller caller, ManagedOperationKey key,
        CancellationToken ct = default)
    {
        if (!ConfigurationStore.ValidManagedCaller(caller)) return new(ManagedResultCode.Forbidden, ManagedOperationState.Unknown);
        if (!ConfigurationStore.ValidManagedKey(key)) return new(ManagedResultCode.ValidationFailed, ManagedOperationState.Unknown);
        try
        {
            var original = _store.PeekManaged(caller, key);
            if (original is null) return new(ManagedResultCode.Forbidden, ManagedOperationState.Unknown);
            var allowed = await Authorize(caller, original.Target, ManagedPermission.Cancel, original.Action, ct);
            if (allowed is not true) return new(allowed is null ? ManagedResultCode.Unknown : ManagedResultCode.Forbidden,
                ManagedOperationState.Unknown);
            var result = _store.CancelManaged(caller, key);
            if (result.Code == ManagedResultCode.Ok && result.Receipt?.State == ManagedOperationState.CancelRequested
                && _work.TryGetValue(new(caller.AuthorityId, caller.SubjectId, caller.ResourceId, key), out var work))
                work.RequestCancellation();
            return new(result.Code, result.Receipt?.State ?? ManagedOperationState.Unknown);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(ManagedResultCode.Unknown, ManagedOperationState.Unknown); }
    }

    private async Task DispatchAsync(OperationIdentity identity, ConfigurationCaller caller,
        ManagedExecuteRequest request, Work work)
    {
        var result = Result(ManagedResultCode.Unknown);
        try
        {
            if (!TryCommand(request.Command, out _, out var target, out var action)) throw new InvalidOperationException();
            var allowed = await Authorize(caller, target!, ManagedPermission.Execute, action, work.Cancellation.Token);
            if (allowed is not true)
                result = _store.FinishManaged(caller, request.Key, allowed is null ? ManagedOperationState.Unknown
                    : ManagedOperationState.Failed, allowed is null ? "authorization_unknown" : "forbidden");
            else
            {
                var current = Catalog().Items.SingleOrDefault(item => item.Key == target);
                if (current is null || current.DescriptorRevision != request.ExpectedDescriptorRevision
                    || !current.Actions.Any(capability => capability.Action == action && capability.Supported))
                    result = _store.FinishManaged(caller, request.Key, ManagedOperationState.Failed, "descriptor_changed");
                else
                {
                    var claim = _store.ClaimManaged(caller, request.Key);
                    if (claim.Code != ManagedResultCode.Ok || claim.Replayed || claim.Receipt?.State != ManagedOperationState.Running)
                        result = claim;
                    else
                    {
                        work.Cancellation.Token.ThrowIfCancellationRequested();
                        var dispatchPermission = await Authorize(caller, target!, ManagedPermission.Execute, action, work.Cancellation.Token);
                        if (dispatchPermission is not true)
                            result = _store.FinishManaged(caller, request.Key, dispatchPermission is null
                                ? ManagedOperationState.Unknown : ManagedOperationState.Failed,
                                dispatchPermission is null ? "authorization_unknown" : "forbidden");
                        else
                        {
                            work.Cancellation.Token.ThrowIfCancellationRequested();
                            work.Dispatched = true;
                            var outcome = await _dispatch(request.Command, work.Cancellation.Token);
                            var state = outcome.State is ManagedOperationState.Completed or ManagedOperationState.Failed
                                or ManagedOperationState.Cancelled or ManagedOperationState.Unknown ? outcome.State : ManagedOperationState.Unknown;
                            result = _store.FinishManaged(caller, request.Key, state, SafeCode(outcome.ErrorCode));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
        {
            // Losing a local request does not prove that a remote side effect was cancelled.
            result = _store.FinishManaged(caller, request.Key, work.Dispatched ? ManagedOperationState.Unknown
                : ManagedOperationState.Cancelled, work.Dispatched ? "cancellation_outcome_unknown" : "cancelled_before_dispatch");
        }
        catch (Exception)
        { result = _store.FinishManaged(caller, request.Key, ManagedOperationState.Unknown, "dispatch_state_unknown"); }
        finally
        {
            work.Completion.TrySetResult(result);
            _work.TryRemove(identity, out _);
            work.DisposeCancellation();
        }
    }

    private async ValueTask<bool?> Authorize(ConfigurationCaller caller, ManagedObjectKey key,
        ManagedPermission permission, ManagedAction? action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await _authorization.IsAllowedAsync(new(caller, key, permission, action), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private ManagedCatalogSnapshot Catalog()
    {
        lock (_lifetimeGate) if (_disposed) throw new ObjectDisposedException(nameof(ManagedCapabilityService));
        var snapshot = _catalog();
        if (snapshot is null || snapshot.Items.IsDefault || snapshot.Items.Length > 4096
            || string.IsNullOrWhiteSpace(snapshot.DescriptorRevision)) throw new InvalidOperationException("invalid_managed_catalog");
        var keys = new HashSet<ManagedObjectKey>();
        foreach (var item in snapshot.Items)
        {
            if (item is null || item.Key is null || !keys.Add(item.Key) || item.Actions.IsDefault
                || !Enum.IsDefined(item.RuntimeState) || string.IsNullOrWhiteSpace(item.DescriptorRevision)
                || item.Actions.Any(capability => capability is null || !Enum.IsDefined(capability.Action))
                || item.Actions.Select(capability => capability.Action).Distinct().Count() != item.Actions.Length)
                throw new InvalidOperationException("invalid_managed_descriptor");
        }
        return snapshot;
    }

    private static bool TryCommand(ManagedCommand? input, out ManagedCommand? command,
        out ManagedObjectKey? target, out ManagedAction action)
    {
        command = input; target = null; action = default;
        switch (input)
        {
            case ManagedCommand.Driver driver when Enum.IsDefined(driver.Action)
                && !string.IsNullOrWhiteSpace(driver.Key.PlatformId) && !string.IsNullOrWhiteSpace(driver.Key.AccountScope)
                && driver.Key.InstanceId is not null && (driver.Key.InstanceId.Length == 0 || !string.IsNullOrWhiteSpace(driver.Key.InstanceId)):
                var key = driver.Key with { PlatformId = PlatformId.Normalize(driver.Key.PlatformId) };
                command = driver with { Key = key }; target = new ManagedObjectKey.Driver(key);
                action = driver.Action switch { DriverCommand.Start => ManagedAction.Start,
                    DriverCommand.Stop => ManagedAction.Stop, _ => ManagedAction.TestConnection }; return true;
            case ManagedCommand.Provider provider when provider.Action == ProviderCommand.TestConnection && ValidId(provider.Key.Value):
                target = new ManagedObjectKey.Provider(provider.Key); action = ManagedAction.TestConnection; return true;
            case ManagedCommand.Plugin plugin when Enum.IsDefined(plugin.Action) && ValidId(plugin.Key.Value):
                target = new ManagedObjectKey.Plugin(plugin.Key);
                action = plugin.Action == PluginCommand.Start ? ManagedAction.Start : ManagedAction.Stop; return true;
            case ManagedCommand.Mcp mcp when Enum.IsDefined(mcp.Action) && ValidId(mcp.RegisteredId):
                target = new ManagedObjectKey.Mcp(mcp.RegisteredId); action = mcp.Action switch
                { McpCommand.Connect => ManagedAction.Connect, McpCommand.Disconnect => ManagedAction.Disconnect,
                    _ => ManagedAction.TestConnection }; return true;
            default: return false;
        }
    }

    private Cursor? ReadCursor(string token)
    {
        if (token.Length > 1024) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var payload = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            if (payload.Length > 512 || signature.Length != 32) return null;
            byte[] expected;
            lock (_lifetimeGate) { if (_disposed) return null; expected = HMACSHA256.HashData(_cursorKey, payload); }
            if (!CryptographicOperations.FixedTimeEquals(expected, signature)) return null;
            return JsonSerializer.Deserialize<Cursor>(payload, Codec);
        }
        catch (Exception) { return null; }
    }

    private string WriteCursor(Cursor cursor)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(cursor, Codec);
        if (payload.Length > 512) throw new InvalidOperationException("managed_cursor_capacity");
        lock (_lifetimeGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ManagedCapabilityService));
            return Convert.ToBase64String(payload) + "." + Convert.ToBase64String(HMACSHA256.HashData(_cursorKey, payload));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            var owned = _work.Values.ToArray();
            foreach (var work in owned) work.RequestCancellation();
            CryptographicOperations.ZeroMemory(_cursorKey);
            _disposeTask = Task.WhenAll(owned.Select(work => work.Runner));
            return new(_disposeTask);
        }
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);
    private static string? SafeCode(string? code) => code is null ? null
        : code.Length is > 0 and <= 64 && code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? code : "managed_dispatch_failed";
    private static ManagedOperationResult Result(ManagedResultCode code) => new(code, false, null);
    private static string KeyOrder(ManagedObjectKey key) => JsonSerializer.Serialize(key, Codec);
    private static string CallerHash(ConfigurationCaller caller) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new[] { caller.AuthorityId, caller.SubjectId, caller.ResourceId })));
    private static ManagedKind Kind(ManagedObjectKey key) => key switch
    { ManagedObjectKey.Driver => ManagedKind.Driver, ManagedObjectKey.Provider => ManagedKind.Provider,
        ManagedObjectKey.Plugin => ManagedKind.Plugin, ManagedObjectKey.Device => ManagedKind.AgentDevice,
        ManagedObjectKey.Mcp => ManagedKind.McpServer, _ => throw new InvalidOperationException("invalid_managed_key") };
    private sealed record Cursor(string CallerHash, string Resource, int Kind, string Revision, int Offset);
    private readonly record struct OperationIdentity(string Authority, string Subject, string Resource, ManagedOperationKey Key);
    private sealed class Work
    {
        private readonly object _cancellationGate = new();
        private bool _cancellationDisposed;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource<ManagedOperationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Runner { get; set; } = Task.CompletedTask;
        internal bool Dispatched;
        internal void RequestCancellation()
        {
            lock (_cancellationGate)
            {
                if (_cancellationDisposed) return;
                try { Cancellation.Cancel(); }
                catch (AggregateException) { /* All callbacks ran; their raw diagnostics must not escape. */ }
            }
        }
        internal void DisposeCancellation()
        {
            lock (_cancellationGate)
            {
                _cancellationDisposed = true;
                Cancellation.Dispose();
            }
        }
    }
}
