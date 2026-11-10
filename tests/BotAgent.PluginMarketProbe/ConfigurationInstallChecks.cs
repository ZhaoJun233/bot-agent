using System.Text;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunConfigurationInstallChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var assembly = typeof(ConfigurationStalePlugin).Assembly;
        var bytes = ReviewZip("probe.dll", await File.ReadAllBytesAsync(assembly.Location));
        var http = new ReviewPackageHttp { Bytes = bytes };
        await using var fixture = new ConfigurationInstallFixture(Path.Combine(root, "configuration-install"), http);
        var market = new PluginMarketService(fixture.Packages, new PluginSettingsStore(), new PluginCatalogSource(http),
            () => DateTimeOffset.UnixEpoch, Path.Combine(root, "configuration-install-operations.json"));
        async Task<ZbaPluginMutationRequest> Request(string id)
        {
            var manifest = ReviewManifest(bytes, id, "1.0.0", "probe.dll", typeof(ConfigurationStalePlugin).FullName!);
            var revision = (await market.ReadInstalledAsync(default)).Revision;
            var preview = await market.PreviewAsync(new("json", revision, Manifest: manifest), "configuration-probe", default);
            return new(manifest, revision, id, true) { PreviewId = preview.PreviewId };
        }
        var request = await Request("probe.install.stale");
        Check(!fixture.Declared(request.Manifest.Id) && fixture.Enabled(request.Manifest.Id), "stale_id_not_globally_declared");
        Check((await fixture.Disable(request.Manifest.Id, default)).Code != ConfigurationResultCode.Ok,
            "unregistered_id_rejected_by_real_settings_store");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var install = market.InstallAsync(request, "configuration-probe", default, async ct =>
        {
            try
            {
                Check(fixture.Declared(request.Manifest.Id), "validated_install_id_visible_inside_callback");
                Check((await fixture.Packages.GetInstalledAsync(ct)).Count == 0, "no_pending_or_id_directory_before_configuration");
                entered.TrySetResult();
            }
            catch (Exception exception)
            {
                entered.TrySetException(exception);
                throw;
            }
            await release.Task.WaitAsync(ct);
            var disabled = await fixture.Disable(request.Manifest.Id, ct);
            Check(disabled.Code == ConfigurationResultCode.Ok && disabled.Receipt?.CommitState == CommitState.Committed,
                "real_store_stale_enabled_true_commits_false");
            await fixture.Reload(); // Acquires runtime then package gates while install is paused outside its gate.
            Check(!fixture.Enabled(request.Manifest.Id), "reload_reentry_observes_disabled_state");
        });
        var first = await Task.WhenAny(entered.Task, install).WaitAsync(TimeSpan.FromSeconds(5));
        if (first == install && !entered.Task.IsCompleted)
        {
            var completed = await install;
            throw new InvalidOperationException($"configuration_callback_not_entered: {completed.Operation.Status}/{completed.Operation.ErrorCode}");
        }
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!fixture.Declared(request.Manifest.Id), "unrelated_async_flow_cannot_see_prepared_id");
        Check((await fixture.Disable(request.Manifest.Id, default)).Code != ConfigurationResultCode.Ok,
            "unrelated_flow_cannot_save_prepared_id");
        release.TrySetResult();
        var installedResult = await install.WaitAsync(TimeSpan.FromSeconds(10));
        Check(installedResult.Operation.Status != "failed", "configuration_callback_completed_successfully");
        await fixture.Reload();
        Check(!fixture.Enabled(request.Manifest.Id) && fixture.Packages.InstalledSnapshot.Single().Active is not null,
            "hot_apply_commits_disabled_package_without_initializing_synthetic_plugin");
        var restart = new PluginPackageService(Path.Combine(root, "configuration-install"), http);
        var loaded = await restart.LoadAtStartupAsync(new PluginContext(new ReviewNoServices()), isEnabled: fixture.Enabled);
        Check(loaded.Failures.Count == 0 && loaded.Loaded.Count == 0 && !fixture.Enabled(request.Manifest.Id),
            "restart_still_disabled_without_assembly_execution");

        var fresh = await Request("probe.install.fresh");
        await market.InstallAsync(fresh, "configuration-probe", default, async ct =>
        {
            var noChange = await fixture.Disable(fresh.Manifest.Id, ct);
            Check(noChange.Code == ConfigurationResultCode.NoChanges && noChange.Receipt?.CommitState == CommitState.NotCommitted,
                "real_store_absent_false_returns_authoritative_nochange");
        });
        foreach (var mode in new[] { "failure", "cancel", "cas" })
        {
            var rejected = await Request("probe.install." + mode);
            using var cancellation = new CancellationTokenSource();
            try
            {
                await market.InstallAsync(rejected, "configuration-probe", cancellation.Token, async ct =>
                {
                    Check(fixture.Declared(rejected.Manifest.Id), "scope_visible_" + mode);
                    if (mode == "failure") throw new ZbaBackendException(409, "synthetic_configuration_rejected", "synthetic");
                    if (mode == "cancel") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                    await fixture.Packages.StageInstallAsync(rejected.Manifest with { Id = "probe.install.concurrent" });
                });
                throw new InvalidOperationException("expected_install_refusal_" + mode);
            }
            catch (OperationCanceledException) when (mode == "cancel") { checks++; }
            catch (ZbaBackendException ex) when (mode == "failure" && ex.Code == "synthetic_configuration_rejected"
                || mode == "cas" && ex.Code == "plugin_revision_conflict") { checks++; }
            Check(!fixture.Declared(rejected.Manifest.Id) && fixture.Packages.InstalledSnapshot.All(p => p.Id != rejected.Manifest.Id),
                "scope_restored_no_pending_" + mode);
        }
        return checks;
    }
}