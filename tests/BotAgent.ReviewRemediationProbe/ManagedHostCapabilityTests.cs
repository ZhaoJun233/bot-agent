using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Platforms;
using BotAgent.Services;
using BotAgent.Services.Platforms;
using Microsoft.Data.Sqlite;

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

            var port = FreeTcpPort();
            var settings = new AppSettings
            {
                ApiKey = "synthetic-model-key",
                Model = "synthetic-model",
                ModelBaseUrl = "https://model.example.com/v1",
                OneBotProtocol = "ReverseWebSocket",
                OneBotAddress = "http://127.0.0.1:" + port,
                OneBotToken = string.Empty,
                HealthPort = 0,
                HealthReportEnabled = false,
                EnableVoice = false,
                VerboseLog = false,
            };
            new SettingsStore().Save(settings);

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

    private static object Build(AppSettings settings, IManagedAuthorization authorization)
    {
        var root = typeof(AppSettings).Assembly.GetType("BotAgent.Host.CompositionRoot", throwOnError: true)!;
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
                && request.Target is ManagedObjectKey.Driver);
        }
    }
}
