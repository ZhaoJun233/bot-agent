using System.Text;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunHotReloadRegressionAsync(string syntheticRoot)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }
        async Task Expect(Func<Task> action, int status, string code)
        {
            try { await action(); }
            catch (ZbaBackendException ex) when (ex.Status == status && ex.Code == code)
            { checks++; return; }
            throw new InvalidOperationException("hot_expected_" + code);
        }

        // Exercise market orchestration and persistence; the callback is a synthetic runtime seam.
        var bytes = ReviewZip("probe.dll", Encoding.UTF8.GetBytes("synthetic-hot-callback-only"));
        var http = new ReviewPackageHttp { Bytes = bytes };
        var root = Path.Combine(syntheticRoot, "hot-callback");
        var packageRoot = Path.Combine(root, "packages");
        var receiptPath = Path.Combine(root, "operations.json");
        var packages = new PluginPackageService(packageRoot, http);
        var settings = new PluginSettingsStore();
        Func<string?, CancellationToken, Task> callback = (_, _) => Task.CompletedTask;
        PluginMarketService Market(bool freshPackages = false) => new(
            freshPackages ? new PluginPackageService(packageRoot, http) : packages, settings,
            new PluginCatalogSource(http, _ => { }), () => DateTimeOffset.UnixEpoch, receiptPath,
            reload: (id, ct) => callback(id, ct));
        var market = Market();
        var manifest = ReviewManifest(bytes, "probe.hot.success", "1.0.0", "probe.dll", "SyntheticEntry");
        var callbackCalls = 0;

        foreach (var verb in new[] { "install", "update", "uninstall" })
        {
            if (verb == "update") manifest = manifest with { Version = "2.0.0" };
            var revision = (await market.ReadInstalledAsync(default)).Revision;
            var key = "hot-success-" + verb;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            callback = async (id, ct) =>
            {
                callbackCalls++;
                Check(id == manifest.Id && !ct.CanBeCanceled, "hot_callback_target_and_detached_token_" + verb);
                var staged = (await packages.GetInstalledAsync(default)).Single(item => item.Id == id);
                Check(verb == "uninstall" ? staged.PendingUninstall : staged.Pending?.Version == manifest.Version,
                    "hot_stage_visible_before_callback_" + verb);
                entered.TrySetResult();
                await release.Task;
            };
            Task<ZbaPluginOperationResult> Submit(PluginMarketService service, string expected, CancellationToken ct)
                => verb == "uninstall"
                    ? service.UninstallAsync(manifest.Id, new(expected, key), "synthetic-admin", ct)
                    : verb == "update"
                        ? service.UpdateAsync(new(manifest, expected, key, true), "synthetic-admin", ct)
                        : service.InstallAsync(new(manifest, expected, key, true), "synthetic-admin", ct);
            using var callerCancellation = new CancellationTokenSource();
            var beforeCalls = callbackCalls;
            var beforeDownloads = http.Calls;
            var first = Submit(market, revision, callerCancellation.Token);
            Task<ZbaPluginOperationResult>? replay = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!first.IsCompleted, "hot_receipt_waits_for_callback_" + verb);
                await Expect(() => Task.FromResult(Market().FindOperation(key)), 503, "plugin_operation_unknown");
                replay = Submit(Market(), revision, default);
                callerCancellation.Cancel();
            }
            finally { release.TrySetResult(); }
            var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Operation.Status == (verb == "uninstall" ? "not_installed" : "installed") &&
                !result.Operation.RestartRequired && result.Operation.ErrorCode is null, "hot_success_receipt_" + verb);
            Check(replay is not null && await replay.WaitAsync(TimeSpan.FromSeconds(5)) == result,
                "hot_concurrent_replay_exact_" + verb);
            var downloads = http.Calls;
            var reloaded = Market(freshPackages: true);
            Check(reloaded.FindOperation(key) == result &&
                await Submit(reloaded, "stale", default) == result, "hot_durable_replay_before_CAS_" + verb);
            Check(callbackCalls == beforeCalls + 1 && http.Calls == downloads &&
                downloads == beforeDownloads + (verb == "uninstall" ? 0 : 1),
                "hot_replay_no_callback_or_download_" + verb);
        }

        // A reload failure is durable for every mutation, not a successful staged receipt.
        foreach (var verb in new[] { "install", "update", "uninstall" })
        {
            var failedManifest = ReviewManifest(bytes, "probe.hot.failure." + verb, "1.0.0", "probe.dll", "SyntheticEntry");
            if (verb != "install") await packages.StageInstallAsync(failedManifest);
            if (verb == "update") failedManifest = failedManifest with { Version = "2.0.0" };
            var revision = (await market.ReadInstalledAsync(default)).Revision;
            var key = "hot-failure-" + verb;
            var calls = 0;
            callback = (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("synthetic callback failure must not escape");
            };
            Task<ZbaPluginOperationResult> Submit(PluginMarketService service, string expected, string operationKey)
                => verb == "uninstall"
                    ? service.UninstallAsync(failedManifest.Id, new(expected, operationKey), "synthetic-admin", default)
                    : verb == "update"
                        ? service.UpdateAsync(new(failedManifest, expected, operationKey, true), "synthetic-admin", default)
                        : service.InstallAsync(new(failedManifest, expected, operationKey, true), "synthetic-admin", default);
            await Expect(() => Submit(market, revision, key), 503, "plugin_reload_failed");
            var failed = Market(freshPackages: true).FindOperation(key);
            Check(failed.Operation.Status == "failed" && failed.Operation.ErrorCode == "plugin_reload_failed" &&
                !failed.Operation.RestartRequired && failed.Operation.Kind == verb, "hot_failure_receipt_" + verb);
            var staged = (await packages.GetInstalledAsync(default)).Single(item => item.Id == failedManifest.Id);
            Check(verb == "uninstall" ? staged.PendingUninstall : staged.Pending?.Version == failedManifest.Version,
                "hot_failure_keeps_stage_for_retry_" + verb);
            var downloads = http.Calls;
            Check(await Submit(Market(freshPackages: true), "stale", key) == failed,
                "hot_failed_replay_returns_exact_receipt_" + verb);
            Check(calls == 1 && http.Calls == downloads, "hot_failed_replay_no_side_effects_" + verb);
            callback = (_, _) => { calls++; return Task.CompletedTask; };
            var currentRevision = (await market.ReadInstalledAsync(default)).Revision;
            var recovered = await Submit(market, currentRevision, key + "-retry");
            Check(!recovered.Operation.RestartRequired && recovered.Operation.ErrorCode is null && calls == 2,
                "hot_failure_retry_new_key_succeeds_" + verb);
            Check(Market(freshPackages: true).FindOperation(key) == failed, "hot_failure_original_receipt_unchanged_" + verb);
        }

        var settingsManifest = ReviewManifest(bytes, "probe.hot.settings", "1.0.0", "probe.dll", "SyntheticEntry") with
        { Settings = new[] { new PluginSettingDefinition("greeting", "Greeting", "string", "hello") } };
        await packages.StageInstallAsync(settingsManifest);
        var previous = await market.ReadSettingsAsync(settingsManifest.Id, default);
        callback = (id, ct) =>
        {
            var saved = settings.ReadManifest(settingsManifest);
            Check(id == settingsManifest.Id && !ct.CanBeCanceled && saved.Values["greeting"].GetString() == "applied" &&
                saved.Revision > 0 && saved.RestartRequired, "hot_settings_saved_before_callback");
            settings.ResolveRuntimeValues(settingsManifest);
            settings.MarkApplied(settingsManifest, saved.Revision);
            return Task.CompletedTask;
        };
        var applied = await market.SaveSettingsAsync(settingsManifest.Id, new(previous.Revision, "hot-settings-applied",
            new[] { new ZbaPluginSettingEdit("greeting", 1, "applied") }), "synthetic-admin", default);
        Check(applied.Revision != previous.Revision && applied.Values["greeting"] == "applied" && !applied.RestartRequired,
            "hot_settings_response_reread_after_callback");

        var failedSettingsCalls = 0;
        callback = (_, _) =>
        {
            failedSettingsCalls++;
            throw new InvalidOperationException("synthetic settings callback failure");
        };
        var failedSave = new ZbaPluginSettingsSaveRequest(applied.Revision, "hot-settings-failed",
            new[] { new ZbaPluginSettingEdit("greeting", 1, "saved-not-applied") });
        await Expect(() => market.SaveSettingsAsync(settingsManifest.Id, failedSave, "synthetic-admin", default),
            503, "plugin_reload_failed");
        var savedAfterFailure = await Market(freshPackages: true).ReadSettingsAsync(settingsManifest.Id, default);
        Check(failedSettingsCalls == 1 && savedAfterFailure.Values["greeting"] == "saved-not-applied" &&
            savedAfterFailure.Revision != applied.Revision && savedAfterFailure.RestartRequired,
            "hot_settings_failure_saved_but_not_reported_applied");
        callback = (_, _) =>
        {
            var saved = settings.ReadManifest(settingsManifest);
            settings.ResolveRuntimeValues(settingsManifest);
            settings.MarkApplied(settingsManifest, saved.Revision);
            return Task.CompletedTask;
        };
        var retried = await market.SaveSettingsAsync(settingsManifest.Id, failedSave, "synthetic-admin", default);
        Check(retried.Revision == savedAfterFailure.Revision && !retried.RestartRequired,
            "hot_settings_retry_reloads_without_revision_churn");
        return checks;
    }
}
