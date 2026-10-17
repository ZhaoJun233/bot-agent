extern alias StorageModule;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Ports;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;

namespace BotAgent.ReviewRemediationProbe;

internal static class ManagedOperationContractTests
{
    internal static async Task<int?> TryRunChildAsync(string[] args)
    {
        if (args is ["--managed-operations"]) return await ChildAsync();
        if (args is ["--managed-operation-crash", var crashRoot]) return await RecoveryChildAsync(crashRoot, false);
        if (args is ["--managed-operation-recover", var recoveryRoot]) return await RecoveryChildAsync(recoveryRoot, true);
        return null;
    }
    internal static Task RunAsync(Action<bool, string> report)
        => ManagedCapabilityContractTests.RunIsolatedAsync("--managed-operations", "MANAGED_OPERATIONS failed=0", report,
            "C4 durable acceptance, authorization, deduplication, cancellation and bounded private pagination");

    private static async Task<int> ChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-managed-operations-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var checks = 0;
        void Check(bool condition)
        { if (!condition) throw new InvalidOperationException("synthetic_managed_assertion_" + checks); checks++; }
        IAsyncDisposable? owner = null;
        try
        {
            ClockBindings.InitializeStorage();
            AppDatabase.Initialize();
            var clock = new SyntheticClock();
            var participant = new Participant();
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(clock, participant);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-manager", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            var provider = new ManagedObjectKey.Provider(new("synthetic-provider"));
            var other = new ManagedObjectKey.Provider(new("synthetic-other"));
            var hidden = new ManagedObjectKey.Provider(new("synthetic-hidden"));
            var driver = new ManagedObjectKey.Driver(new("local", "synthetic-account", "synthetic-instance"));
            ManagedDescriptor Describe(ManagedObjectKey key, params ActionCapability[] capabilities)
                => new(key, "synthetic-display", true, ManagedRuntimeState.Running, capabilities.ToImmutableArray(), true, null, "descriptor-one");
            var items = ImmutableArray.Create(Describe(provider, new ActionCapability(ManagedAction.TestConnection, true, "supported")),
                Describe(other, new ActionCapability(ManagedAction.TestConnection, true, "supported")),
                Describe(hidden, new ActionCapability(ManagedAction.TestConnection, true, "supported")),
                Describe(driver, new(ManagedAction.Start, true, "supported"), new(ManagedAction.Stop, true, "supported"),
                    new(ManagedAction.TestConnection, false, "unsupported")));
            var catalogRevision = "catalog-one";
            Func<StorageConfiguration.ManagedCatalogSnapshot> catalog = () => new(catalogRevision, items);
            var authorizer = new Authorization();
            authorizer.Decide = request => request.Target != hidden;
            var dispatchCount = 0;
            var persistenceObserved = false;
            Func<CancellationToken, Task>? wait = null;
            Func<ManagedCommand, CancellationToken, ValueTask<StorageConfiguration.ManagedDispatchResult>> dispatch = async (_, token) =>
            {
                Interlocked.Increment(ref dispatchCount);
                persistenceObserved = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations WHERE namespace='mg1'") > 0
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations WHERE namespace='mg1' "
                        + "AND json_extract(receipt,'$.state')=1") > 0;
                if (wait is not null) await wait(token);
                return new(ManagedOperationState.Completed, null);
            };
            var service = StorageConfiguration.ConfigurationStorageBridge.CreateManagedCapabilities(store, authorizer, catalog, dispatch);
            owner = (IAsyncDisposable)service;
            Check(ReferenceEquals(service, StorageConfiguration.ConfigurationStorageBridge.CreateManagedCapabilities(store, authorizer, catalog, dispatch)));
            var duplicateRejected = false;
            try { StorageConfiguration.ConfigurationStorageBridge.CreateManagedCapabilities(store, new Authorization(), catalog, dispatch); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            Check(duplicateRejected);
            ManagedExecuteRequest Request(ManagedCommand? command = null) => new("cfg-0.3.2", "descriptor-one",
                new(header.Generation, clock.Seconds, Guid.NewGuid()), command ?? new ManagedCommand.Provider(provider.Key, ProviderCommand.TestConnection));
            var page = await service.ListAsync(caller, new(ManagedKind.Provider, 1, null));
            Check(page.Code == ManagedResultCode.Ok && page.Items.Length == 1 && page.NextCursor is not null
                && page.Items.All(item => item.Key != hidden));
            var next = await service.ListAsync(caller, new(ManagedKind.Provider, 1, page.NextCursor));
            Check(next.Code == ManagedResultCode.Ok && next.Items.Length == 1 && next.NextCursor is null
                && next.Items[0].Key != page.Items[0].Key);
            Check((await service.ListAsync(caller with { SubjectId = "synthetic-other-manager" },
                new(ManagedKind.Provider, 1, page.NextCursor))).Code == ManagedResultCode.InvalidCursor);
            Check((await service.ListAsync(caller, new(ManagedKind.Provider, 1, page.NextCursor + "x"))).Code == ManagedResultCode.InvalidCursor);
            Check((await service.ListAsync(caller, new(null, 101, null))).Code == ManagedResultCode.ValidationFailed);
            Check((await service.InspectAsync(caller, hidden)).Code == ManagedResultCode.Forbidden);
            items = items.SetItem(0, items[0] with { DesiredEnabled = false });
            catalogRevision = "catalog-two";
            Check((await service.ListAsync(caller, new(ManagedKind.Provider, 1, page.NextCursor))).Code == ManagedResultCode.StaleCursor);
            var initial = Request();
            var completed = await service.ExecuteAsync(caller, initial);
            Check(completed.Code == ManagedResultCode.Ok && completed.Receipt?.State == ManagedOperationState.Completed
                && dispatchCount == 1 && persistenceObserved);
            Check((await service.QueryAsync(caller, initial.Key)).Receipt?.State == ManagedOperationState.Completed);
            var replay = await service.ExecuteAsync(caller, initial);
            Check(replay.Code == ManagedResultCode.Ok && replay.Replayed && dispatchCount == 1);
            Check((await service.ExecuteAsync(caller, initial with { Command = new ManagedCommand.Provider(other.Key,
                ProviderCommand.TestConnection) })).Code == ManagedResultCode.IdempotencyConflict && dispatchCount == 1);
            var count = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations");
            Check((await service.ExecuteAsync(caller, Request() with { ExpectedDescriptorRevision = "stale" })).Code
                == ManagedResultCode.VersionConflict);
            Check((await service.ExecuteAsync(caller, Request(new ManagedCommand.Driver(driver.Key, DriverCommand.TestConnection)))).Code
                == ManagedResultCode.UnsupportedAction);
            Check(AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") == count && dispatchCount == 1);
            authorizer.Decide = _ => false;
            Check((await service.ExecuteAsync(caller, Request())).Code == ManagedResultCode.Forbidden);
            Check((await service.QueryAsync(caller, initial.Key)).Code == ManagedResultCode.Forbidden);
            Check((await service.QueryAsync(caller, Request().Key)).Code == ManagedResultCode.Forbidden);
            Check((await service.CancelAsync(caller, initial.Key)).Code == ManagedResultCode.Forbidden && dispatchCount == 1);
            authorizer.Decide = _ => true;

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = async token => { started.TrySetResult(); await release.Task.WaitAsync(token); };
            var waitingRequest = Request();
            using (var waiter = new CancellationTokenSource())
            {
                var waiting = service.ExecuteAsync(caller, waitingRequest, waiter.Token).AsTask();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                waiter.Cancel();
                var callerCancelled = false;
                try { await waiting; } catch (OperationCanceledException) { callerCancelled = true; }
                Check(callerCancelled && (await service.QueryAsync(caller, waitingRequest.Key)).Receipt?.State == ManagedOperationState.Running);
                Check((await service.ExecuteAsync(caller, waitingRequest)).Replayed);
                release.TrySetResult();
                Check((await WaitTerminal(service, caller, waitingRequest.Key)).Receipt?.State == ManagedOperationState.Completed);
            }
            started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
            var cancelledRequest = Request();
            var cancelling = service.ExecuteAsync(caller, cancelledRequest).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancelled = await service.CancelAsync(caller, cancelledRequest.Key);
            Check(cancelled.Code == ManagedResultCode.Ok && cancelled.State == ManagedOperationState.CancelRequested);
            var cancellationOutcome = await cancelling;
            Check(cancellationOutcome.Receipt?.State == ManagedOperationState.Unknown
                && cancellationOutcome.Receipt.ErrorCode == "cancellation_outcome_unknown");
            Check((await service.CancelAsync(caller, cancelledRequest.Key)).State == ManagedOperationState.Unknown);
            wait = null;
            var previousDispatches = dispatchCount;
            var executeAuthorizations = 0;
            authorizer.Decide = request => request.Permission != ManagedPermission.Execute || ++executeAuthorizations < 4;
            var revoked = await service.ExecuteAsync(caller, Request());
            Check(revoked.Receipt?.State == ManagedOperationState.Failed && revoked.Receipt.ErrorCode == "forbidden"
                && dispatchCount == previousDispatches);
            authorizer.Decide = _ => throw new InvalidOperationException("synthetic_unknown_authorization");
            Check((await service.ExecuteAsync(caller, Request())).Code == ManagedResultCode.Unknown && dispatchCount == previousDispatches);
            authorizer.Decide = _ => true;
            var c3Keys = new List<ConfigurationOperationKey>();
            while (AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") < 256)
            {
                var key = new ConfigurationOperationKey(header.Generation, clock.Seconds, Guid.NewGuid());
                var saved = await store.SaveAsync(caller, new("cfg-0.3.2", header.Version, key,
                    new(null, null, null, null, null, null, null, null), ImmutableArray<SecretEdit>.Empty));
                if (saved.Code != ConfigurationResultCode.NoChanges)
                    throw new InvalidOperationException("synthetic_c3_capacity_fill_failed");
                c3Keys.Add(key);
            }
            Check(c3Keys.Count > 0 && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations WHERE namespace='cfg1'") == c3Keys.Count);
            var fullDispatches = dispatchCount;
            Check((await service.ExecuteAsync(caller, Request())).Code == ManagedResultCode.CapacityExceeded
                && dispatchCount == fullDispatches);
            Check((await store.SaveAsync(caller, new("cfg-0.3.2", header.Version,
                new(header.Generation, clock.Seconds, Guid.NewGuid()), new(null, null, null, null, null, null, null, null),
                ImmutableArray<SecretEdit>.Empty))).Code == ConfigurationResultCode.CapacityExceeded);
            Check((await service.ExecuteAsync(caller, initial)).Replayed && dispatchCount == fullDispatches);
            // Remove only the C3 rows created above, from this isolated synthetic fixture, not any runtime store.
            AppDatabase.Write(connection =>
            {
                foreach (var key in c3Keys)
                    AppDatabase.Exec(connection, "DELETE FROM configuration_operations WHERE authority=$a AND subject=$s "
                        + "AND resource=$r AND operation_key=$key AND namespace='cfg1'",
                        ("$a", caller.AuthorityId), ("$s", caller.SubjectId), ("$r", caller.ResourceId),
                        ("$key", $"cfg1:{key.Generation.Value:N}:" + key.IssuedUnixSeconds.ToString(CultureInfo.InvariantCulture) + $":{key.Nonce:N}"));
            });
            await CheckRestartAsync(root, page.NextCursor!, Check);
            var future = Request() with { Key = new(header.Generation, clock.Seconds + 301, Guid.NewGuid()) };
            Check((await service.ExecuteAsync(caller, future)).Code == ManagedResultCode.ValidationFailed);
            clock.Seconds += 1000;
            Check((await service.ExecuteAsync(caller, Request())).Receipt?.State == ManagedOperationState.Completed);
            clock.Seconds -= 600;
            Check((await service.ExecuteAsync(caller, Request())).Code == ManagedResultCode.ClockRegression);
            clock.Seconds += 600;
            clock.Seconds += 86400;
            Check((await service.QueryAsync(caller, initial.Key)).Code == ManagedResultCode.KeyExpired);
            var beforeDispose = dispatchCount;
            await owner.DisposeAsync();
            Check(ReferenceEquals(service, StorageConfiguration.ConfigurationStorageBridge.CreateManagedCapabilities(store, authorizer, catalog, dispatch)));
            Check((await service.ExecuteAsync(caller, Request())).Code != ManagedResultCode.Ok && dispatchCount == beforeDispose);
            Console.WriteLine($"MANAGED_OPERATIONS failed=0 passed={checks}");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"MANAGED_OPERATIONS failed=1 passed={checks} type={error.GetType().Name}");
            return 1;
        }
        finally
        {
            if (owner is not null) await owner.DisposeAsync();
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("botagent-managed-operations-", StringComparison.Ordinal)
                && Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(full, recursive: true);
        }
    }

    private static async Task CheckRestartAsync(string root, string cursor, Action<bool> check)
    {
        File.WriteAllText(Path.Combine(root, "synthetic-owner.txt"), "managed-operation-fixture-v1");
        File.WriteAllText(Path.Combine(root, "synthetic-cursor.txt"), cursor);
        var marker = Path.Combine(root, "synthetic-crash-request.json");
        Process Start(string mode)
        {
            var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(ManagedOperationContractTests).Assembly.Location);
            start.ArgumentList.Add(mode); start.ArgumentList.Add(root);
            return Process.Start(start) ?? throw new InvalidOperationException("synthetic_recovery_start_failed");
        }
        var crashStarted = DateTimeOffset.UtcNow;
        using (var crash = Start("--managed-operation-crash"))
        {
            var output = crash.StandardOutput.ReadToEndAsync();
            var errors = crash.StandardError.ReadToEndAsync();
            try
            {
                for (var attempt = 0; attempt < 500 && !File.Exists(marker) && !crash.HasExited; attempt++) await Task.Delay(10);
                check(File.Exists(marker) && !crash.HasExited);
                crash.Kill(entireProcessTree: true);
                await crash.WaitForExitAsync();
                await output; await errors;
                check(crash.ExitCode != 0); // Deliberate crash of this owned synthetic child, not a claimed successful exit.
                Console.WriteLine($"MANAGED_CRASH forcedStop=true exitCode={crash.ExitCode} startedUtc={crashStarted:o} endedUtc={DateTimeOffset.UtcNow:o}");
            }
            finally
            {
                if (!crash.HasExited) { crash.Kill(entireProcessTree: true); await crash.WaitForExitAsync(); }
            }
        }
        var recoveryStarted = DateTimeOffset.UtcNow;
        using var recovery = Start("--managed-operation-recover");
        var recoveryOutput = recovery.StandardOutput.ReadToEndAsync();
        var recoveryErrors = recovery.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await recovery.WaitForExitAsync(timeout.Token);
            var output = await recoveryOutput; await recoveryErrors;
            check(recovery.ExitCode == 0 && output.Contains("MANAGED_RECOVERY failed=0", StringComparison.Ordinal));
            Console.WriteLine($"MANAGED_RECOVERY exitCode={recovery.ExitCode} startedUtc={recoveryStarted:o} endedUtc={DateTimeOffset.UtcNow:o}");
        }
        finally
        {
            if (!recovery.HasExited) { recovery.Kill(entireProcessTree: true); await recovery.WaitForExitAsync(); }
        }
    }

    private static async Task<int> RecoveryChildAsync(string root, bool recover)
    {
        var full = Path.GetFullPath(root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        const string prefix = "botagent-managed-operations-";
        var name = Path.GetFileName(full);
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[prefix.Length..], "N", out _) || !Directory.Exists(full)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0
            || File.ReadAllText(Path.Combine(full, "synthetic-owner.txt")) != "managed-operation-fixture-v1") return 1;
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", full);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", full);
        Environment.SetEnvironmentVariable("BOTAGENT_FILE_LOG", "0");
        Environment.SetEnvironmentVariable("QQCHAT_FILE_LOG", "0");
        ClockBindings.InitializeStorage(); AppDatabase.Initialize();
        var clock = new SyntheticClock();
        var store = StorageConfiguration.ConfigurationStorageBridge.Activate(clock, new Participant());
        var caller = new ConfigurationCaller("synthetic-authority", "synthetic-manager", "bot", ConfigurationAccess.ReadEditable);
        var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
        var provider = new ManagedObjectKey.Provider(new("synthetic-provider"));
        Func<StorageConfiguration.ManagedCatalogSnapshot> catalog = () => new("recovery-catalog", ImmutableArray.Create(
            new ManagedDescriptor(provider, "synthetic-display", true, ManagedRuntimeState.Running,
                ImmutableArray.Create(new ActionCapability(ManagedAction.TestConnection, true, "supported")), true, null, "descriptor-one")));
        var calls = 0;
        ManagedExecuteRequest? original = null;
        Func<ManagedCommand, CancellationToken, ValueTask<StorageConfiguration.ManagedDispatchResult>> dispatch = async (_, token) =>
        {
            calls++;
            if (!recover)
            {
                var temporary = Path.Combine(full, "synthetic-crash-request.pending");
                File.WriteAllText(temporary, JsonSerializer.Serialize(original));
                File.Move(temporary, Path.Combine(full, "synthetic-crash-request.json"));
                await Task.Delay(Timeout.Infinite, token);
            }
            return new(ManagedOperationState.Completed, null);
        };
        var service = StorageConfiguration.ConfigurationStorageBridge.CreateManagedCapabilities(store,
            new Authorization { Decide = _ => true }, catalog, dispatch);
        await using var owner = (IAsyncDisposable)service;
        if (!recover)
        {
            original = new("cfg-0.3.2", "descriptor-one", new(header.Generation, clock.Seconds, Guid.NewGuid()),
                new ManagedCommand.Provider(provider.Key, ProviderCommand.TestConnection));
            await service.ExecuteAsync(caller, original);
            return 1; // The parent must observe Running and deliberately terminate this process first.
        }
        original = JsonSerializer.Deserialize<ManagedExecuteRequest>(File.ReadAllText(Path.Combine(full, "synthetic-crash-request.json")))
            ?? throw new InvalidOperationException("synthetic_recovery_intent_missing");
        var query = await service.QueryAsync(caller, original.Key);
        var replay = await service.ExecuteAsync(caller, original);
        var staleCursor = await service.ListAsync(caller, new(ManagedKind.Provider, 1,
            File.ReadAllText(Path.Combine(full, "synthetic-cursor.txt"))));
        var passed = query.Code == ManagedResultCode.Ok && query.Receipt?.State == ManagedOperationState.Unknown
            && query.Receipt.ErrorCode == "restart_state_unknown" && replay.Replayed
            && replay.Receipt?.State == ManagedOperationState.Unknown && calls == 0
            && staleCursor.Code == ManagedResultCode.InvalidCursor;
        Console.WriteLine(passed ? "MANAGED_RECOVERY failed=0" : "MANAGED_RECOVERY failed=1");
        return passed ? 0 : 1;
    }

    private static async Task<ManagedOperationResult> WaitTerminal(IManagedCapabilities service,
        ConfigurationCaller caller, ManagedOperationKey key)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var result = await service.QueryAsync(caller, key);
            if (result.Receipt?.State is ManagedOperationState.Completed or ManagedOperationState.Cancelled or ManagedOperationState.Failed) return result;
            await Task.Delay(10);
        }
        throw new TimeoutException("synthetic_managed_wait_timeout");
    }

    private sealed class Authorization : IManagedAuthorization
    {
        internal Func<ManagedAuthorizationRequest, bool> Decide = _ => false;
        public ValueTask<bool> IsAllowedAsync(ManagedAuthorizationRequest request, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Decide(request)); }
    }
    private sealed class SyntheticClock : IClock
    {
        internal long Seconds = 1900000000;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(Seconds);
        public DateTimeOffset UtcNow => Now;
        public DateTime LocalDateTime => Now.DateTime;
        public long TickCount => 0;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.Delay(millisecondsDelay, ct);
    }
    private sealed class Participant : StorageConfiguration.ConfigurationStorageParticipant
    {
        internal override bool Reauthorize(ConfigurationCaller caller, ConfigurationAccess required) => true;
        internal override ImmutableArray<SecretMetadata> SecretSources(SqliteConnection connection,
            ImmutableArray<StorageConfiguration.SecretMutation> proposed)
            => Enum.GetValues<WritableSecretSlot>().Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray();
        internal override StorageConfiguration.PreparedConfigurationChange Prepare(SqliteConnection connection,
            ConfigurationPatch ownedIntent, ImmutableArray<SecretMetadata> proposedSecrets,
            ImmutableArray<StorageConfiguration.SecretMutation> proposedMutations, StorageConfiguration.ConfigurationStorageProfile profile)
            => new(false, ImmutableArray<FieldError>.Empty, ImmutableArray<DeviceKeyAssignment>.Empty, _ => throw new InvalidOperationException());
        internal override ConfigurationReadResult Read(SqliteConnection connection, ConfigurationCaller caller,
            ConfigurationReadKind kind, ConfigurationVersion version, ConfigurationGeneration generation,
            long serverUnixSeconds, ApplyState latestApplyState)
            => new(ConfigurationResultCode.Ok, version, generation, "cfg-0.3.2", serverUnixSeconds, null,
                ImmutableArray<SecretMetadata>.Empty, ImmutableArray<FieldDescriptor>.Empty, latestApplyState);
        internal override void AppendAudit(SqliteConnection connection, ConfigurationReceipt receipt,
            ImmutableArray<ConfigurationFieldId> fields) { }
    }
}
