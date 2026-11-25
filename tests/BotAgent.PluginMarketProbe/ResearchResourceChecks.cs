using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Plugins.Execution;
using BotAgent.Services.Plugins;
using BotAgent.Services.Resilience;

internal partial class Program
{
    internal static async Task<int> RunResearchResourceChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); } catch (Exception e) when (e.Message == code) { checks++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        var bytes = ReviewZip("research.dll", await File.ReadAllBytesAsync(typeof(ResearchResourcePlugin).Assembly.Location));
        var plain = ReviewManifest(bytes, "test.research-resources", "1.0.0", "research.dll", nameof(ResearchResourcePlugin));
        var modelOnly = plain with { Permissions = [IGroundedTextModel.Permission] };
        var executionOnly = plain with { Permissions = [IToolExecution.Permission] };
        var manifest = plain with { Permissions = [IGroundedTextModel.Permission, IToolExecution.Permission] };
        var controlManifest = manifest with { Version = "1.0.1", Control = true, EntryType = nameof(ResearchControlResourcePlugin) };
        var emptyIndex = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = Array.Empty<PluginPackageManifest>() });
        var distribution = TrustedPluginDistribution.Read(emptyIndex, Convert.ToHexString(SHA256.HashData(emptyIndex)));
        var http = new ReviewPackageHttp { Bytes = bytes };
        var denied = new PluginPackageService(Path.Combine(root, "research-denied"), http, distribution);
        await Reject(() => denied.StageInstallAsync(modelOnly), "plugin_model_not_authorized");
        await Reject(() => denied.StageInstallAsync(executionOnly), "plugin_tool_execution_not_authorized");
        foreach (var managed in new[] { modelOnly, executionOnly })
            await Reject(() =>
            {
                PluginPackageValidation.Validate(managed with { ApiVersion = 2, EntryAssembly = null, EntryType = null,
                    TargetFramework = null, Runtime = new("python", 1, "main.py", "3.11", "stdlib-only", ["echo"]) });
                return Task.CompletedTask;
            }, "plugin_execution_invalid_declaration");
        Check((await denied.GetInstalledAsync()).Count == 0, "resource declarations cannot authorize themselves");
        var packageRoot = Path.Combine(root, "research-approved");
        var packages = new PluginPackageService(packageRoot, http, distribution,
            new PluginResourceAuthorizations([manifest, modelOnly, executionOnly, controlManifest]));
        await Reject(() => packages.StageInstallAsync(manifest with { Sha256 = new string('b', 64) }), "plugin_model_not_authorized");
        await Reject(() => packages.StageInstallAsync(manifest with { Permissions = [IGroundedTextModel.Permission, IToolExecution.Permission, "message.send"] }), "plugin_model_not_authorized");
        var model = new BlockingGroundedModel();
        var circuit = new ToolCircuitBreaker("synthetic-research", timeoutThreshold: 1, hardTimeout: TimeSpan.FromMilliseconds(100));
        var captures = new Dictionary<string, (IGroundedTextModel? Model, IToolExecution? Execution)>();
        var events = new List<string>();
        var fail = false;
        PluginContext Context(IGroundedTextModel? suppliedModel, IToolExecution? execution) => new(new ResearchResourceServices(
            suppliedModel, execution, (name, m, e) => captures[name] = (m, e), events.Add, () => fail));
        var context = Context(model, circuit);
        var registry = new PluginManager(_ => { });
        var request = new GroundedTextRequest("synthetic", 0.1, 32);
        try
        {
            await denied.StageInstallAsync(plain);
            var legacyLoaded = await denied.LoadAtStartupAsync(context, isEnabled: _ => true);
            Check(legacyLoaded.Loaded.Count == 1 && legacyLoaded.Failures.Count == 0 && captures["business"] == (null, null),
                "legacy startup also masks undeclared ambient resources");
            foreach (var plugin in legacyLoaded.Loaded) await plugin.ShutdownAsync(default);
            await denied.ApplyAtStartupAsync(plain.Id, context, registry, true);
            Check(captures["business"] == (null, null), "undeclared contexts cannot inherit ambient model or execution");
            await registry.RemoveAsync(plain.Id);
            foreach (var partial in new[] { modelOnly, executionOnly })
            {
                var partialPackages = new PluginPackageService(Path.Combine(root, partial == modelOnly ? "research-model-only" : "research-execution-only"),
                    http, distribution, new PluginResourceAuthorizations([partial]));
                await partialPackages.StageInstallAsync(partial);
                await partialPackages.ApplyAtStartupAsync(partial.Id, context, registry, true);
                var captured = captures["business"];
                Check((captured.Model is not null) == (partial == modelOnly)
                    && (captured.Execution is not null) == (partial == executionOnly), "permissions independently bind only their declared resource");
                await registry.RemoveAsync(partial.Id);
            }
            await packages.StageInstallAsync(manifest);
            var unapprovedReopen = new PluginPackageService(packageRoot, http, distribution);
            await Reject(() => unapprovedReopen.ApplyAtStartupAsync(manifest.Id, context, registry, true), "plugin_model_not_authorized");
            var metadataPath = Path.Combine(packageRoot, manifest.Id, "installed.json");
            var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
            var metadata = JsonNode.Parse(metadataBytes)!;
            Check(metadata["minimumReaderVersion"]!.GetValue<int>() == 7, "model and execution authority requires reader seven");
            var current = await PluginStateCompatibility.InspectAsync(packageRoot, typeof(PluginPackageService).Assembly.Location);
            Check(current.Compatible && current.CandidateReaderVersion == 12, "actual current PE understands resource state");
            if (Environment.GetEnvironmentVariable("BOTAGENT_IT_PREVIOUS_RESOURCE_HOST") is { Length: > 0 } previousHost)
            {
                var previous = await PluginStateCompatibility.InspectAsync(packageRoot, previousHost);
                Check(!previous.Compatible && previous.CandidateReaderVersion == 6
                    && previous.Entries.Single().Code == "plugin_state_reader_incompatible", "actual reader six cannot adopt model/execution state");
                Check((await File.ReadAllBytesAsync(metadataPath)).SequenceEqual(metadataBytes), "downgrade inspection preserves durable bytes");
            }
            metadata["minimumReaderVersion"] = 6;
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
            Check((await packages.GetInstalledAsync()).Single().MetadataFailure is not null, "lowered header cannot hide new authority semantics");
            await File.WriteAllBytesAsync(metadataPath, metadataBytes);
            await Reject(() => packages.ApplyAsync(manifest.Id, context, registry, true), "plugin_contract_cold_start_required");
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, Context(null, circuit), registry, true), "plugin_model_missing");
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, Context(model, null), registry, true), "plugin_tool_execution_missing");
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, Context(model, new UntrackedExecution()), registry, true), "plugin_tool_execution_missing");
            fail = true;
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, context, registry, true), "synthetic_research_prepare_failed");
            var failed = captures["business"];
            await Reject(() => failed.Model!.GenerateAsync(request, default), "plugin_model_not_ready");
            await Reject(() => failed.Execution!.RunAsync(_ => Task.FromResult(1)), "plugin_tool_execution_not_ready");
            Check(model.Calls == 0 && circuit.Snapshot().ConsecutiveTimeouts == 0, "failed preparation touches no model or circuit");
            fail = false; events.Clear();
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var first = captures["business"];
            Check(events.Contains("business-model-denied") && events.Contains("business-execution-denied"), "loaded DLL cannot execute resources during preparation");
            Check((await first.Model!.GenerateAsync(request, default)).Text == "synthetic"
                && (await first.Execution!.RunAsync(_ => Task.FromResult(7))).Value == 7, "published model and circuit handles dispatch");
            registry.SetEnabled(manifest.Id, false);
            await Reject(() => first.Model.GenerateAsync(request, default), "plugin_model_not_ready");
            await Reject(() => first.Execution!.RunAsync(_ => Task.FromResult(1)), "plugin_tool_execution_not_ready");
            registry.SetEnabled(manifest.Id, true);
            model.Block = true;
            var pending = first.Model.GenerateAsync(request, default);
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var removal = registry.RemoveAsync(manifest.Id);
            Check(!removal.IsCompleted, "model response retains the generation during removal");
            await Reject(() => first.Model.GenerateAsync(request, default), "plugin_model_not_ready");
            model.Release.TrySetResult();
            Check((await pending).Text == "synthetic", "admitted model response drains normally");
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
            model.Block = false;
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var second = captures["business"];
            Check(!ReferenceEquals(first.Model, second.Model) && !ReferenceEquals(first.Execution, second.Execution), "same release receives fresh resource handles");
            await Reject(() => first.Model.GenerateAsync(request, default), "plugin_model_not_ready");
            await Reject(() => first.Execution!.RunAsync(_ => Task.FromResult(1)), "plugin_tool_execution_not_ready");
            model.Fail = true;
            await Reject(() => second.Model!.GenerateAsync(request, default), "synthetic_model_failure");
            model.Fail = false;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await second.Model!.GenerateAsync(request, cancelled.Token); throw new Exception("model_cancellation_ignored"); }
                catch (OperationCanceledException) { checks++; }
                try { await second.Execution!.RunAsync(_ => Task.FromResult(1), cancelled.Token); throw new Exception("execution_cancellation_ignored"); }
                catch (OperationCanceledException) { checks++; }
            }
            try { await second.Execution!.RunAsync<int>(null!); throw new Exception("null_operation_accepted"); }
            catch (ArgumentNullException) { checks++; }
            Check((await second.Execution!.RunAsync<int>(_ => throw new IOException("synthetic"))).ReasonCode == "tool_exception", "synchronous delegate failure retains circuit result semantics");
            await registry.RemoveAsync(manifest.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Check(true, "model faults, caller cancellation and invalid delegates release leases");

            foreach (var cancelCaller in new[] { false, true })
            {
                // Both the operation and its cancellation callback may ignore cancellation. Neither may
                // hold up the caller, and neither may outlive the runtime's completed removal.
                circuit.RecordSuccess();
                await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
                var execution = captures["business"].Execution!;
                using var callbackRelease = new ManualResetEventSlim();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var caller = new CancellationTokenSource();
                Task? draining = null;
                try
                {
                    var run = execution.RunAsync(token =>
                    {
                        token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(); throw new IOException("synthetic_callback_fault"); });
                        entered.TrySetResult();
                        return completed.Task;
                    }, caller.Token);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (cancelCaller) caller.Cancel();
                    if (cancelCaller)
                    {
                        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("caller_cancellation_ignored"); }
                        catch (OperationCanceledException) { checks++; }
                    }
                    else Check((await run.WaitAsync(TimeSpan.FromSeconds(2))).TimedOut, "hard deadline returns while plugin delegate and callbacks remain active");
                    await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var circuitAfterResult = circuit.Snapshot();
                    Check(cancelCaller ? circuitAfterResult.ConsecutiveTimeouts == 0 : circuitAfterResult.State == ToolCircuitState.Open,
                        "timeout and caller cancellation retain their original circuit accounting");
                    if (!cancelCaller)
                        Check((await execution.RunAsync<int>(_ => throw new Exception("open_circuit_invoked"))).ReasonCode == "tool_circuit_open", "open circuit rejects without invoking or leaking a lease");
                    draining = registry.RemoveAsync(manifest.Id);
                    await Task.Delay(50);
                    Check(!draining.IsCompleted, "early result does not release the live plugin generation");
                    await Reject(() => execution.RunAsync(_ => Task.FromResult(1)), "plugin_tool_execution_not_ready");
                    if (cancelCaller)
                    {
                        callbackRelease.Set();
                        await Task.Delay(50);
                        Check(!draining.IsCompleted, "delegate lifetime survives completed cancellation callbacks");
                        completed.TrySetException(new IOException("synthetic_late_fault"));
                    }
                    else
                    {
                        completed.TrySetResult(9);
                        await Task.Delay(50);
                        Check(!draining.IsCompleted, "callback lifetime survives completed delegate work");
                        callbackRelease.Set();
                    }
                    await draining.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(circuit.Snapshot() == circuitAfterResult, "late result and callback faults cannot mutate the circuit result");
                }
                finally
                {
                    callbackRelease.Set(); completed.TrySetResult(0);
                    if (draining is not null) await draining.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            circuit.RecordSuccess();
            await packages.StageInstallAsync(controlManifest);
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var business = captures["business"]; var control = captures["control"];
            registry.SetEnabled(manifest.Id, false);
            await Reject(() => business.Model!.GenerateAsync(request, default), "plugin_model_not_ready");
            await Reject(() => business.Execution!.RunAsync(_ => Task.FromResult(1)), "plugin_tool_execution_not_ready");
            Check(!ReferenceEquals(business.Model, control.Model) && (await control.Model!.GenerateAsync(request, default)).Text == "synthetic"
                && !ReferenceEquals(business.Execution, control.Execution) && (await control.Execution!.RunAsync(_ => Task.FromResult(2))).Value == 2,
                "approved control resources stay usable while business is disabled");
            await registry.RemoveAsync(manifest.Id);
            await Reject(() => control.Model!.GenerateAsync(request, default), "plugin_model_not_ready");
            await Reject(() => control.Execution!.RunAsync(_ => Task.FromResult(1)), "plugin_tool_execution_not_ready");
            events.Clear();
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, false);
            Check(!events.Any(e => e.StartsWith("business-", StringComparison.Ordinal)), "disabled cold startup does not initialize business resources");
            control = captures["control"];
            await registry.StopAllAsync(default);
            await Reject(() => control.Model!.GenerateAsync(request, default), "plugin_model_not_ready");
            await Reject(() => control.Execution!.RunAsync(_ => Task.FromResult(1)), "plugin_tool_execution_not_ready");
        }
        finally { model.Release.TrySetResult(); await registry.StopAllAsync(default); }
        return checks;
    }

    private sealed class ResearchResourceServices(IGroundedTextModel? model, IToolExecution? execution,
        Action<string, IGroundedTextModel?, IToolExecution?> capture, Action<string> record, Func<bool> fail) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(IGroundedTextModel) ? model : type == typeof(IToolExecution) ? execution
            : type == typeof(Action<string, IGroundedTextModel?, IToolExecution?>) ? capture
            : type == typeof(Action<string>) ? record : type == typeof(Func<bool>) ? fail : null;
    }
    private sealed class UntrackedExecution : IToolExecution
    {
        public Task<ToolRunResult<T>> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
            => throw new Exception("untracked_execution_must_not_be_used");
    }
    private sealed class BlockingGroundedModel : IGroundedTextModel
    {
        public int Calls;
        public bool Block, Fail;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GroundedTextResult> GenerateAsync(GroundedTextRequest request, CancellationToken ct)
        {
            Calls++; ct.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("synthetic_model_failure");
            if (Block) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return new("synthetic", [], []);
        }
    }
}

public class ResearchResourcePlugin : IBotPlugin
{
    public string Id => "test.research-resources";
    public virtual string Version => "1.0.0";
    public string Name => "Synthetic research resources";
    public string Description => "Synthetic model and execution lifecycle";
    public PluginCategory Category => PluginCategory.Feature;
    protected async Task Prepare(PluginContext context, string name)
    {
        var model = context.Services.GetService(typeof(IGroundedTextModel)) as IGroundedTextModel;
        var execution = context.Services.GetService(typeof(IToolExecution)) as IToolExecution;
        var record = (Action<string>)context.Services.GetService(typeof(Action<string>))!;
        ((Action<string, IGroundedTextModel?, IToolExecution?>)context.Services.GetService(typeof(Action<string, IGroundedTextModel?, IToolExecution?>))!)(name, model, execution);
        if (model is not null)
        {
            try { await model.GenerateAsync(new("synthetic", 0.1, 32), default); throw new Exception("prepare_model_allowed"); }
            catch (InvalidOperationException e) when (e.Message == "plugin_model_not_ready") { record(name + "-model-denied"); }
        }
        if (execution is not null)
        {
            try { await execution.RunAsync(_ => Task.FromResult(1)); throw new Exception("prepare_execution_allowed"); }
            catch (InvalidOperationException e) when (e.Message == "plugin_tool_execution_not_ready") { record(name + "-execution-denied"); }
        }
        if (((Func<bool>)context.Services.GetService(typeof(Func<bool>))!)()) throw new InvalidOperationException("synthetic_research_prepare_failed");
    }
    public Task InitializeAsync(PluginContext context, CancellationToken ct) => Prepare(context, "business");
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class ResearchControlResourcePlugin : ResearchResourcePlugin, IPluginControlModule
{
    public override string Version => "1.0.1";
    public Task PrepareControlAsync(PluginContext context, CancellationToken ct) => Prepare(context, "control");
    public object? GetControlContribution(Type contract) => null;
    public Task ShutdownControlAsync(CancellationToken ct) => Task.CompletedTask;
}
