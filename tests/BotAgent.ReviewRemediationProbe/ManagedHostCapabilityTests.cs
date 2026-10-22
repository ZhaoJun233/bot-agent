extern alias StorageModule;

using System.Collections.Immutable;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Platforms;
using BotAgent.Services;
using BotAgent.Services.Platforms;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;

namespace BotAgent.ReviewRemediationProbe;

/// <summary>
/// C4 host consumer coverage: the real CompositionRoot.Build creates AppGraph.Management over the
/// same configuration store, exposes it as IManagedCapabilities, and the trusted lifecycle bootstrap
/// can start the OneBot driver even when the optional OneBot token is empty (S36 regression).
/// Everything runs in an isolated synthetic data root; no real protocol endpoint is configured.
/// </summary>
internal static class ManagedHostCapabilityTests
{
    private const string SuccessMarker = "MANAGED_HOST failed=0 passed=";
    private const string TempPrefix = "botagent-managed-host-";

    internal static async Task<int?> TryRunChildAsync(string[] args)
        => args is ["--managed-host-capabilities"] ? await ChildAsync() : null;

    internal static Task RunAsync(Action<bool, string> report)
        => ManagedCapabilityContractTests.RunIsolatedAsync("--managed-host-capabilities", SuccessMarker, report,
            "C4 host composition exposes same-store managed drivers and starts a tokenless OneBot reverse WS");

    private static async Task<int> ChildAsync()
    {
        ClearInheritedEnvironment();
        var root = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("BOTAGENT_FILE_LOG", "0");
        Environment.SetEnvironmentVariable("QQCHAT_FILE_LOG", "0");
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("managed_host_assertion:" + label);
            passed++;
            Console.WriteLine("PASS " + label);
        }

        object? graph = null;
        try
        {
            ClockBindings.InitializePlatforms();
            ClockBindings.InitializeModel();
            ClockBindings.InitializeStorage();
            AppDatabase.Initialize();

            using var providerEndpoint = new SyntheticProviderEndpoint();
            var port = FreeTcpPort();
            var settings = new AppSettings
            {
                ApiKey = "synthetic-model-key",
                ApiKeyOverride = "synthetic-model-key",
                Model = "synthetic-model",
                ModelBaseUrl = providerEndpoint.BaseUrl,
                OneBotProtocol = "ReverseWebSocket",
                OneBotAddress = "http://127.0.0.1:" + port,
                OneBotToken = string.Empty,
                HealthPort = 0,
                HealthReportEnabled = false,
                EnableVoice = false,
                VerboseLog = false,
            };
            new SettingsStore().Save(settings);
            Check(new SecretsStore().SaveApiKey("synthetic-model-key"), "synthetic provider credential is persisted before activation");
            new ModelProviderStore().EnsurePrimary(settings);

            var allowed = new ConfigurationCaller("synthetic-authority", "synthetic-manager", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var denied = new ConfigurationCaller("synthetic-authority", "synthetic-intruder", "bot",
                ConfigurationAccess.ReadEditable);
            var authorization = new TestAuthorization(allowed);

            graph = Build(settings, authorization);
            Check(graph is not null, "CompositionRoot.Build returns a host graph");

            var management = RequiredProperty(graph!, "Management", BindingFlags.Public | BindingFlags.Instance);
            Check(management.GetType().Name == "ManagedCapabilityBinding", "AppGraph.Management is the host managed binding");
            Check(ReferenceEquals(BindingField(management, "_callerAuthorization"), authorization),
                "CompositionRoot injects the trusted caller authorization into the managed binding");

            var capabilities = (IManagedCapabilities)RequiredProperty(management, "Capabilities",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var store = BindingField(management, "_store");
            var storeOwnedService = store is null ? null
                : store.GetType().GetField("_managedService", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(store);
            Check(store is not null && ReferenceEquals(storeOwnedService, capabilities),
                "AppGraph.Management.Capabilities is the service owned by the same configuration store");

            var operations = (PlatformDriverOperations)RequiredProperty(graph!, "DriverOperations",
                BindingFlags.Public | BindingFlags.Instance);
            var driverKey = new DriverKey(PlatformId.QqPrivate, AccountScope.Legacy, string.Empty);
            var driver = new ManagedObjectKey.Driver(driverKey);
            var page = await capabilities.ListAsync(allowed, new ManagedPageRequest(ManagedKind.Driver, 100, null));
            Check(page.Code == ManagedResultCode.Ok, "authorized caller can list managed drivers");
            var initial = page.Items.SingleOrDefault(item => item.Key == driver);
            Check(initial is not null, "managed catalog exposes the qq.private driver");
            Check(initial!.CredentialConfigured == false,
                "onebot driver reports the optional credential as absent when OneBotToken is empty");
            Check(initial.RuntimeState is ManagedRuntimeState.NotLoaded or ManagedRuntimeState.Disconnected
                or ManagedRuntimeState.Disabled, "qq.private driver starts in a non-running state (" + initial.RuntimeState + ")");
            var declaredOfficial = new ManagedObjectKey.Driver(
                new DriverKey(PlatformId.QqOfficial, AccountScope.Legacy, string.Empty));
            var official = page.Items.SingleOrDefault(item => item.Key == declaredOfficial);
            Check(official?.RuntimeState == ManagedRuntimeState.NotLoaded,
                "declared but unconstructed qq.official driver is visible as NotLoaded");

            var inspected = await capabilities.InspectAsync(allowed, driver);
            Check(inspected.Code == ManagedResultCode.Ok && inspected.Descriptor is not null
                && inspected.Descriptor.Key == driver, "authorized caller can inspect the qq.private driver");

            var deniedInspect = await capabilities.InspectAsync(denied, driver);
            Check(deniedInspect.Code == ManagedResultCode.Forbidden, "unauthorized caller receives Forbidden for the same driver inspect");
            var deniedPage = await capabilities.ListAsync(denied, new ManagedPageRequest(ManagedKind.Driver, 100, null));
            Check(deniedPage.Code == ManagedResultCode.Ok && deniedPage.Items.Length == 0,
                "unauthorized listing is fail-closed and exposes no managed drivers");

            await VerifyProviderAsync(management, capabilities, allowed, providerEndpoint, Check);

            await InvokeLifecycleAsync(management, "StartAsync");
            Check(await CanConnectAsync(port), "tokenless OneBot driver StartAsync opens the reverse WebSocket TCP port");
            Check(AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations WHERE namespace='mg1'") > 0,
                "managed lifecycle receipts use the real configuration ledger");

            var started = await capabilities.InspectAsync(allowed, driver);
            Check(started.Descriptor?.RuntimeState == ManagedRuntimeState.Disconnected,
                "started qq.private driver waits for a handshake as Disconnected");
            Check(started.Descriptor?.CredentialConfigured == false,
                "starting without a OneBot token keeps the optional credential absent");

            await InvokeLifecycleAsync(management, "StopAsync");
            Check(!await CanConnectAsync(port), "ManagedCapabilityBinding.StopAsync closes the reverse WebSocket port");
            var stopped = operations.TryGet(driverKey);
            Check(stopped is not null && !stopped.IsActive, "ManagedCapabilityBinding.StopAsync retires the driver lease");
            await InvokeLifecycleAsync(management, "StartAsync");
            Check(await CanConnectAsync(port), "managed lifecycle restarts the tokenless OneBot reverse WebSocket port");
            var restarted = await capabilities.InspectAsync(allowed, driver);
            Check(restarted.Descriptor?.RuntimeState == ManagedRuntimeState.Disconnected,
                "restarted qq.private driver returns to Disconnected");

            await ((IAsyncDisposable)graph!).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            Check(!await CanConnectAsync(port), "AppGraph.DisposeAsync closes the reverse WebSocket port");

            var registration = operations.TryGet(driverKey);
            Check(registration is not null && !registration.IsActive, "AppGraph.DisposeAsync stops the qq.private driver lease");
            Check(registration?.RuntimeFact.State == ManagedRuntimeState.Disabled,
                "disposed driver reports a stopped runtime state");

            Console.WriteLine($"MANAGED_HOST failed=0 passed={passed}");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"MANAGED_HOST failed=1 passed={passed} type={error.GetType().Name} detail={error.Message}");
            return 1;
        }
        finally
        {
            if (graph is not null)
            {
                try { await ((IAsyncDisposable)graph).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (Exception) { /* The explicit dispose above already asserted the success path. */ }
            }
            SqliteConnection.ClearAllPools();
            DeleteSyntheticRoot(root);
        }
    }

    private static async Task VerifyProviderAsync(object management, IManagedCapabilities capabilities,
        ConfigurationCaller allowed, SyntheticProviderEndpoint endpoint, Action<bool, string> check)
    {
        var store = (StorageConfiguration.ConfigurationStore)BindingField(management, "_store")!;
        var authorization = BindingField(management, "_configurationAuthorization")!;
        var enter = authorization.GetType().GetMethod("Enter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var configurationCaller = allowed with
        {
            Access = ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets
        };
        IDisposable Enter() => (IDisposable)enter.Invoke(authorization,
            new object[] { configurationCaller, (Func<bool>)(() => true) })!;
        async Task<ConfigurationReadResult> Header()
        {
            using var scope = Enter();
            var header = await store.ReadAsync(configurationCaller, ConfigurationReadKind.Editable);
            check(header.Code == ConfigurationResultCode.Ok, "actual Host participant authorizes the synthetic configuration header");
            return header;
        }
        var provider = new ManagedObjectKey.Provider(new("primary"));
        async Task<ManagedExecuteRequest> Request()
        {
            var header = await Header();
            var inspected = await capabilities.InspectAsync(allowed, provider);
            check(inspected.Code == ManagedResultCode.Ok && inspected.Descriptor is not null,
                "actual Host catalog exposes the registered primary Provider");
            return new("cfg-0.3.3", inspected.Descriptor!.DescriptorRevision,
                new(header.Generation, header.ServerUnixSeconds, Guid.NewGuid()),
                new ManagedCommand.Provider(provider.Key, ProviderCommand.TestConnection));
        }
        var request = await Request();
        var rejected = await capabilities.ExecuteAsync(allowed with { }, request);
        check(rejected.Code == ManagedResultCode.Forbidden && rejected.Receipt is null && endpoint.Requests.Count == 0,
            "equal caller fields and configuration flags cannot impersonate the trusted management caller");
        endpoint.EnqueueReply();
        var result = await capabilities.ExecuteAsync(allowed, request);
        check(result.Code == ManagedResultCode.Ok && result.Receipt?.State == ManagedOperationState.Completed
            && endpoint.Requests.Count == 1, "actual Host managed Provider dispatch completes exactly one local HTTP chat request");
        var payload = endpoint.Requests.Single();
        check(payload["model"]?.GetValue<string>() == "synthetic-model" && payload["max_tokens"]?.GetValue<int>() == 64
            && payload.Count == 3 && payload["messages"]?.AsArray().Count == 1,
            "real Provider tester uses only the fixed minimal chat payload and 64-token budget");
        check(endpoint.LastAuthorization == "Bearer synthetic-model-key", "real Provider request resolves the committed database credential");
        check(AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations WHERE namespace='mg1'") > 0,
            "actual Provider dispatch has a durable receipt in the shared configuration ledger");
        var queried = await capabilities.QueryAsync(allowed, request.Key);
        var replay = await capabilities.ExecuteAsync(allowed, request);
        check(queried.Receipt?.State == ManagedOperationState.Completed && replay.Replayed
            && replay.Receipt?.State == ManagedOperationState.Completed && endpoint.Requests.Count == 1,
            "query and replay retain the actual Provider outcome without a second HTTP request");

        async Task SaveSecret(SecretEdit edit)
        {
            var header = await Header();
            using var scope = Enter();
            var saved = await store.SaveAsync(configurationCaller, new("cfg-0.3.3", header.Version,
                new(header.Generation, header.ServerUnixSeconds, Guid.NewGuid()),
                new(null, null, null, null, null, null, null, null), ImmutableArray.Create(edit)));
            if (saved.Code != ConfigurationResultCode.Ok || saved.Receipt?.CommitState != CommitState.Committed
                || saved.Receipt.ApplyState != ApplyState.NotStarted)
                Console.WriteLine($"MANAGED_HOST_C3 code={saved.Code} commit={saved.Receipt?.CommitState} apply={saved.Receipt?.ApplyState} "
                    + "errors=" + string.Join(",", saved.Errors.Select(error => error.Code)));
            check(saved.Code == ConfigurationResultCode.Ok && saved.Receipt?.CommitState == CommitState.Committed
                && saved.Receipt.ApplyState == ApplyState.NotStarted,
                "actual C3 commits the credential change without falsely claiming runtime application");
        }
        using (var input = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-model-key-replaced")))
            await SaveSecret(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, input));
        var replacement = await Request();
        endpoint.EnqueueReply();
        var replaced = await capabilities.ExecuteAsync(allowed, replacement);
        check(replaced.Receipt?.State == ManagedOperationState.Completed && endpoint.Requests.Count == 2
            && endpoint.LastAuthorization == "Bearer synthetic-model-key-replaced",
            "Host Provider tester reads the newly committed key without resurrecting the startup cache");
        var beforeDelete = await Header();
        using (var scope = Enter())
        {
            var invalidDelete = await store.SaveAsync(configurationCaller, new("cfg-0.3.3", beforeDelete.Version,
                new(beforeDelete.Generation, beforeDelete.ServerUnixSeconds, Guid.NewGuid()),
                new(null, null, null, null, null, null, null, null),
                ImmutableArray.Create(SecretEdit.Delete(WritableSecretSlot.ModelApiKey))));
            check(invalidDelete.Code == ConfigurationResultCode.ValidationFailed && invalidDelete.CurrentVersion == beforeDelete.Version
                && invalidDelete.Errors.Any(error => error.Code == "credential_required") && endpoint.Requests.Count == 2,
                "deleting a still-enabled Provider credential rejects the whole batch without version advance or HTTP");
            var disablePrimary = new RegisteredConfigurationChanges(ImmutableArray<PluginEnabledEdit>.Empty,
                ImmutableArray.Create(new ProviderEdit(provider.Key, new(default, default, default, default,
                    FieldEdit<bool>.Set(false), CredentialBindingEdit.KeepExisting()))), ImmutableArray<PolicyEdit>.Empty);
            var invalidDisable = await store.SaveAsync(configurationCaller, new("cfg-0.3.3", beforeDelete.Version,
                new(beforeDelete.Generation, beforeDelete.ServerUnixSeconds, Guid.NewGuid()),
                new(null, null, null, null, null, null, null, disablePrimary),
                ImmutableArray.Create(SecretEdit.Delete(WritableSecretSlot.ModelApiKey))));
            check(invalidDisable.Code == ConfigurationResultCode.ValidationFailed && invalidDisable.CurrentVersion == beforeDelete.Version
                && invalidDisable.Errors.Any(error => error.Code == "field_read_only") && endpoint.Requests.Count == 2,
                "read-only primary enabled metadata cannot bypass the credential deletion invariant");
        }
        var retained = await capabilities.InspectAsync(allowed, provider);
        check(retained.Descriptor?.CredentialConfigured == true, "rejected credential deletion retains the configured metadata");
        var afterRejectedDelete = await Request();
        endpoint.EnqueueReply();
        var preserved = await capabilities.ExecuteAsync(allowed, afterRejectedDelete);
        check(preserved.Receipt?.State == ManagedOperationState.Completed && endpoint.Requests.Count == 3
            && endpoint.LastAuthorization == "Bearer synthetic-model-key-replaced",
            "whole-batch deletion rejection retains the committed credential and real Provider connectivity");
    }

    private sealed class SyntheticProviderEndpoint : IDisposable
    {
        private readonly object _mock;
        private readonly Type _type;
        internal SyntheticProviderEndpoint()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BotAgent.slnx"))) directory = directory.Parent;
            var repository = directory?.FullName ?? throw new InvalidOperationException("synthetic_repository_missing");
            var harness = Assembly.LoadFrom(Path.Combine(repository,
                "tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll"));
            _type = harness.GetType("BotAgent.IntegrationHarness.MockOpenAi", throwOnError: true)!;
            _mock = Activator.CreateInstance(_type, new object[] { FreeTcpPort(), "127.0.0.1" })!;
            try { _type.GetMethod("Start")!.Invoke(_mock, null); }
            catch { ((IDisposable)_mock).Dispose(); throw; }
        }
        internal string BaseUrl => (string)_type.GetProperty("BaseUrl")!.GetValue(_mock)!;
        internal IReadOnlyList<JsonObject> Requests => (IReadOnlyList<JsonObject>)_type.GetProperty("Requests")!.GetValue(_mock)!;
        internal string? LastAuthorization => (string?)_type.GetProperty("LastAuthorization")!.GetValue(_mock);
        internal void EnqueueReply() => _type.GetMethod("EnqueueReply")!.Invoke(_mock, new object[] { "OK" });
        public void Dispose() => ((IDisposable)_mock).Dispose();
    }

    private static object Build(AppSettings settings, IManagedAuthorization authorization)
    {
        var root = typeof(BotAgent.Services.SettingsBox).Assembly.GetType("BotAgent.Host.CompositionRoot", throwOnError: true)!;
        var build = root.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("managed_host_build_missing");
        try
        {
            return build.Invoke(null, new object?[] { settings, authorization })
                ?? throw new InvalidOperationException("managed_host_graph_missing");
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static async Task InvokeLifecycleAsync(object binding, string method)
    {
        var info = binding.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("managed_host_lifecycle_missing:" + method);
        object? result;
        try
        {
            result = info.Invoke(binding, new object?[] { CancellationToken.None });
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        await ((Task)result!).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static object RequiredProperty(object target, string name, BindingFlags flags)
        => target.GetType().GetProperty(name, flags)?.GetValue(target)
            ?? throw new InvalidOperationException("managed_host_property_missing:" + name);

    private static object? BindingField(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        using var client = new TcpClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync("127.0.0.1", port, cancellation.Token);
            return client.Connected;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static void ClearInheritedEnvironment()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            new[] { "BOTAGENT_", "QQCHAT_", "OPENAI_", "FEISHU_", "MINIMAX_", "TTS_" }
                .Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray())
            Environment.SetEnvironmentVariable(name, null);
    }

    private static void DeleteSyntheticRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith(TempPrefix, StringComparison.Ordinal)
            && Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
            Directory.Delete(full, recursive: true);
    }

    private sealed class TestAuthorization : IManagedAuthorization
    {
        private readonly ConfigurationCaller _allowed;
        internal TestAuthorization(ConfigurationCaller allowed) => _allowed = allowed;

        public ValueTask<bool> IsAllowedAsync(ManagedAuthorizationRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReferenceEquals(request.Caller, _allowed)
                && (request.Target is ManagedObjectKey.Driver
                    || request.Target is ManagedObjectKey.Provider provider && provider.Key.Value == "primary"
                    && request.Action is null or ManagedAction.Inspect or ManagedAction.TestConnection));
        }
    }
}
