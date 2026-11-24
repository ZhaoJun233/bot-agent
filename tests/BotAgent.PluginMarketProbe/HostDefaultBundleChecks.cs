extern alias StorageModule;

using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Management;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services.Plugins;

internal partial class Program
{
    internal static async Task<int> RunHostDefaultBundleChecks(string root)
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        const string id = "preset.feature.token-quota";
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var runtimeType = typeof(PluginPackageService).Assembly.GetType("BotAgent.Host.PluginRuntime")!;
        var directory = Path.Combine(root, "host-default-bundle");
        var flags = new StorageModule::BotAgent.Storage.PluginStore();
        var enabled = flags.LoadPluginStates().GetValueOrDefault(id, true);
        flags.SetPluginEnabled(id, false);
        IAsyncDisposable NewRuntime(string? path = null) => (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
            .Invoke([path ?? directory, (Action<string>)(_ => { })]);
        PluginManager Registry(object runtime) => (PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
        PluginPackageService Packages(object runtime) => (PluginPackageService)runtimeType.GetProperty("Packages", hidden)!.GetValue(runtime)!;
        Task Start(object runtime) => (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;
        try
        {
            string fingerprint;
            await using (var runtime = NewRuntime())
            {
                var registry = Registry(runtime);
                Check(registry.GetAll().All(p => p.Id != id), "quota has no legacy preset registration before package adoption");
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == id);
                Check(state.Active is { Control: true } && state.Pending is null
                    && state.Active.DownloadUrl == "host-distribution://" + id + "/1.0.0", "host adopts actual default distribution ZIP");
                Check(!registry.IsReady(id) && registry.IsControlReady(id) && !flags.LoadPluginStates()[id], "explicit disabled intent survives first adoption");
                var control = registry.GetControlContribution<ITenantQuotaManagement>(id);
                Check(control.ReadManagement("synthetic:host-bundle").Quota.DailyTokenLimit == 50000, "disabled package supplies management");
                var market = (PluginMarketService)runtimeType.GetProperty("Market", hidden)!.GetValue(runtime)!;
                var installed = await market.ReadInstalledAsync(default);
                Check(installed.Plugins.Single(p => p.Manifest.Id == id).Metadata is
                    { Name: "Token 配额", Runtime: "dotnet", SourceKind: "official", Review.Status: "unreviewed" }
                    && installed.BuiltinPlugins.All(p => p.PluginId != id),
                    "the actual disabled default package retains offline presentation as an unreviewed official package");
                using var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, id, "installed.json")));
                Check(metadata.RootElement.GetProperty("minimumReaderVersion").GetInt32() == 12, "offline distribution source with packaged UI requires reader twelve");
                fingerprint = state.Active!.Sha256;
                try
                {
                    new PluginSettingsStore().ReadManifest(state.Active);
                    throw new InvalidOperationException("unapproved_settings_accepted");
                }
                catch (BotAgent.Domain.Plugins.PluginSettingsException) { checks++; }
                var settings = (PluginSettingsStore)runtimeType.GetProperty("Settings", hidden)!.GetValue(runtime)!;
                try
                {
                    settings.ReadManifest(state.Active with { Control = false });
                    throw new InvalidOperationException("changed_settings_manifest_accepted");
                }
                catch (BotAgent.Domain.Plugins.PluginSettingsException) { checks++; }
                Check(AssemblyLoadContext.Default.Assemblies.All(a => a.GetName().Name != "BotAgent.Package.TokenQuota"), "business implementation is not loaded into Default");
                await Packages(runtime).StageUninstallAsync(id);
            }
            await using (var runtime = NewRuntime())
            {
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == id);
                Check(!Registry(runtime).IsControlReady(id) && state.Active is null && state.Previous?.Sha256 == fingerprint,
                    "restart adopts explicit uninstall and preserves previous release");
            }
            var packageDirectory = Path.GetFullPath(Path.Combine(directory, id));
            var preserved = Path.GetFullPath(Path.Combine(root, "host-default-preserved"));
            if (!packageDirectory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !preserved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("synthetic_path_escape");
            Directory.Move(packageDirectory, preserved);
            await using (var runtime = NewRuntime())
            {
                await Start(runtime);
                Check(!Directory.Exists(packageDirectory) && !Registry(runtime).IsControlReady(id), "default bundle does not resurrect physically removed adopted package");
            }
            if (OperatingSystem.IsWindows())
            {
                await using var runtime = NewRuntime(directory.ToUpperInvariant());
                await Start(runtime);
                Check(!Directory.Exists(packageDirectory), "path casing cannot resurrect a removed default package");
            }
            var alias = Path.Combine(root, "host-default-bundle-alias");
            if (OperatingSystem.IsWindows())
            {
                var command = new System.Diagnostics.ProcessStartInfo("pwsh")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
                foreach (var arg in new[] { "-NoProfile", "-Command", "New-Item -ItemType Junction -Path '"
                    + alias.Replace("'", "''") + "' -Target '" + directory.Replace("'", "''") + "' -ErrorAction Stop | Out-Null" })
                    command.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(command)!;
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(process.ExitCode == 0, "synthetic directory junction created");
            }
            else Directory.CreateSymbolicLink(alias, directory);
            try
            {
                await using var runtime = NewRuntime(alias);
                await Start(runtime);
                Check(!Directory.Exists(packageDirectory), "physical directory alias cannot resurrect a removed default package");
            }
            finally
            {
                var link = new DirectoryInfo(alias);
                var resolve = typeof(StorageModule::BotAgent.Adapters.Persistence.AppDatabase)
                    .GetMethod("ResolvePhysicalPath", BindingFlags.NonPublic | BindingFlags.Static)!;
                var expected = (string)resolve.Invoke(null, [directory])!;
                var actual = link.LinkTarget is null ? null : (string)resolve.Invoke(null, [link.ResolveLinkTarget(true)!.FullName])!;
                if (!string.Equals(actual, expected,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("synthetic_alias_changed");
                link.Delete();
            }
            var dependencies = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.ChangeExtension(typeof(PluginPackageService).Assembly.Location, ".deps.json")));
            Check(dependencies.RootElement.GetProperty("libraries").EnumerateObject().All(p =>
                !p.Name.StartsWith("BotAgent.Package.TokenQuota/", StringComparison.Ordinal)
                && !p.Name.StartsWith("BotAgent.PluginBundle/", StringComparison.Ordinal)), "host runtime dependencies exclude package and bundler");
            checks += await RunHostDistributionUpgradeChecks(root);
        }
        finally { flags.SetPluginEnabled(id, enabled); }
        return checks;
    }

    private static async Task<int> RunHostDistributionUpgradeChecks(string root)
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("host_upgrade: " + label); checks++; }
        const string quotaId = "preset.feature.token-quota";
        const string stickersId = "preset.feature.stickers";
        const string zeroSha = "0000000000000000000000000000000000000000000000000000000000000000";
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var asm = typeof(PluginPackageService).Assembly;
        var runtimeType = asm.GetType("BotAgent.Host.PluginRuntime")!;
        var envelopeType = asm.GetType("BotAgent.Adapters.Plugins.PluginStateEnvelope")!;
        var writeEnvelope = envelopeType.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!;
        var flags = new StorageModule::BotAgent.Storage.PluginStore();
        var prevQuotaEnabled = flags.LoadPluginStates().GetValueOrDefault(quotaId, true);
        var prevStickersEnabled = flags.LoadPluginStates().GetValueOrDefault(stickersId, true);

        byte[] EncodeEnvelope(InstalledPluginPackage state) => (byte[])writeEnvelope.Invoke(null, [state])!;
        IAsyncDisposable NewRuntime(string path, List<string> logs) => (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
            .Invoke([path, (Action<string>)(line => logs.Add(line))]);
        PluginManager Registry(object runtime) => (PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
        PluginPackageService Packages(object runtime) => (PluginPackageService)runtimeType.GetProperty("Packages", hidden)!.GetValue(runtime)!;
        Task Start(object runtime) => (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;
        ImmutableArray<ManagedDescriptor> Describe(object runtime)
            => (ImmutableArray<ManagedDescriptor>)runtimeType.GetMethod("Describe", hidden)!.Invoke(runtime, null)!;

        string OldSha(string tag) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-old-host-build:" + tag))).ToLowerInvariant();

        void ReplaceWithOldRelease(string dir, PluginPackageManifest current, InstalledPluginPackage oldState, string oldSha)
        {
            var pkgDir = Path.Combine(dir, current.Id);
            var releasesDir = Path.Combine(pkgDir, "releases");
            Directory.CreateDirectory(releasesDir);
            var currentZip = Path.Combine(releasesDir, $"{current.Version}-{current.Sha256.ToLowerInvariant()}.zip");
            if (File.Exists(currentZip)) File.Delete(currentZip);
            var oldZip = Path.Combine(releasesDir, $"{current.Version}-{oldSha}.zip");
            File.WriteAllBytes(oldZip, "synthetic-old-archive-bytes"u8.ToArray());
            File.WriteAllBytes(Path.Combine(pkgDir, "installed.json"), EncodeEnvelope(oldState));
        }

        try
        {
            // Case A: old build hash -> automatic upgrade preserves desiredEnabled, config, prunes old zip, verifies non-zero UI asset hashes.
            var dirA = Path.Combine(root, "host-upgrade-case-a");
            flags.SetPluginEnabled(quotaId, true);
            flags.SetPluginEnabled(stickersId, false);
            PluginPackageManifest currentQuota;
            PluginPackageManifest currentStickers;
            await using (var runtime = NewRuntime(dirA, []))
            {
                await Start(runtime);
                var all = await Packages(runtime).GetInstalledAsync();
                currentQuota = all.Single(p => p.Id == quotaId).Active!;
                currentStickers = all.Single(p => p.Id == stickersId).Active!;
                Check(currentQuota.Ui is { Assets.Count: > 0 } && currentQuota.Ui.Assets.All(a => a.Sha256 != zeroSha),
                    "compiled token-quota bundle has real non-zero UI asset hashes");
                Check(currentStickers.Ui is { Assets.Count: > 0 } && currentStickers.Ui.Assets.All(a => a.Sha256 != zeroSha),
                    "compiled stickers bundle has real non-zero UI asset hashes");
                Check(all.All(p => p.Active is null || p.Active.Sha256 != zeroSha),
                    "all compiled host releases have non-zero archive hashes");
            }
            var customConfigPath = Path.Combine(dirA, quotaId, "config", "synthetic-user.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(customConfigPath)!);
            await File.WriteAllTextAsync(customConfigPath, "preserved-user-config");
            var oldQuotaSha = OldSha("case-a-quota");
            var oldStickersSha = OldSha("case-a-stickers");
            ReplaceWithOldRelease(dirA, currentQuota,
                new InstalledPluginPackage(quotaId) { Active = currentQuota with { Sha256 = oldQuotaSha } }, oldQuotaSha);
            ReplaceWithOldRelease(dirA, currentStickers,
                new InstalledPluginPackage(stickersId) { Active = currentStickers with { Sha256 = oldStickersSha } }, oldStickersSha);
            var logsA = new List<string>();
            await using (var runtime = NewRuntime(dirA, logsA))
            {
                await Start(runtime);
                var all = await Packages(runtime).GetInstalledAsync();
                var quota = all.Single(p => p.Id == quotaId);
                var stickers = all.Single(p => p.Id == stickersId);
                Check(quota.MetadataFailure is null && quota.Active?.Sha256 == currentQuota.Sha256 && quota.Pending is null && quota.Previous is null,
                    "case A upgrades enabled host package cleanly without plugin_metadata_unreadable");
                Check(Registry(runtime).IsReady(quotaId) && Registry(runtime).IsControlReady(quotaId) && flags.LoadPluginStates()[quotaId],
                    "case A preserves enabled state and activates control+business plugin");
                Check(await File.ReadAllTextAsync(customConfigPath) == "preserved-user-config",
                    "case A preserves user config directory across upgrade");
                Check(!File.Exists(Path.Combine(dirA, quotaId, "releases", $"{currentQuota.Version}-{oldQuotaSha}.zip"))
                    && File.Exists(Path.Combine(dirA, quotaId, "releases", $"{currentQuota.Version}-{currentQuota.Sha256.ToLowerInvariant()}.zip")),
                    "case A prunes obsolete old archive after successful activation");
                Check(stickers.MetadataFailure is null && stickers.Active?.Sha256 == currentStickers.Sha256
                    && !Registry(runtime).IsReady(stickersId) && !flags.LoadPluginStates()[stickersId],
                    "case A upgrades disabled host package while preserving disabled intent");
                Check(logsA.Any(line => line.Contains($"pluginId={quotaId}", StringComparison.Ordinal)
                    && line.Contains($"oldShaPrefix={oldQuotaSha[..8]}", StringComparison.Ordinal)
                    && line.Contains("action=upgraded", StringComparison.Ordinal)),
                    "case A emits sanitized upgrade log");
            }

            // Case B (supplemental): physical directory deletion emits action=skipped-absent and never resurrects.
            var dirB = Path.Combine(root, "host-upgrade-case-b");
            await using (var runtime = NewRuntime(dirB, [])) { await Start(runtime); }
            Directory.Delete(Path.Combine(dirB, quotaId), recursive: true);
            var logsB = new List<string>();
            await using (var runtime = NewRuntime(dirB, logsB))
            {
                await Start(runtime);
                Check(!Directory.Exists(Path.Combine(dirB, quotaId)) && !Registry(runtime).IsControlReady(quotaId),
                    "case B physically removed package never resurrects");
                Check(logsB.Any(line => line.Contains($"pluginId={quotaId}", StringComparison.Ordinal)
                    && line.Contains("action=skipped-absent", StringComparison.Ordinal)),
                    "case B emits sanitized skipped-absent log");
            }

            // Case C1 & C2: explicitly uninstalled (old Previous only, or PendingUninstall=true) stays uninstalled.
            var dirC1 = Path.Combine(root, "host-upgrade-case-c1");
            await using (var runtime = NewRuntime(dirC1, [])) { await Start(runtime); }
            var oldC1Sha = OldSha("case-c1");
            ReplaceWithOldRelease(dirC1, currentQuota,
                new InstalledPluginPackage(quotaId) { Active = null, Pending = null, Previous = currentQuota with { Sha256 = oldC1Sha }, PendingUninstall = false },
                oldC1Sha);
            var logsC1 = new List<string>();
            await using (var runtime = NewRuntime(dirC1, logsC1))
            {
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == quotaId);
                Check(state.MetadataFailure is null && state.Active is null && state.Pending is null && state.Previous is null,
                    "case C1 clears stale unapproved Previous without plugin_metadata_unreadable");
                Check(!Registry(runtime).IsControlReady(quotaId) && !Registry(runtime).IsReady(quotaId),
                    "case C1 keeps explicitly uninstalled package uninstalled");
                Check(!File.Exists(Path.Combine(dirC1, quotaId, "releases", $"{currentQuota.Version}-{oldC1Sha}.zip")),
                    "case C1 prunes stale archive of uninstalled package");
                Check(logsC1.Any(line => line.Contains($"pluginId={quotaId}", StringComparison.Ordinal)
                    && line.Contains("action=kept-uninstalled", StringComparison.Ordinal)),
                    "case C1 emits kept-uninstalled log");
            }
            await using (var runtime = NewRuntime(dirC1, []))
            {
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == quotaId);
                Check(state.MetadataFailure is null && state.Active is null && state.Pending is null && !Registry(runtime).IsControlReady(quotaId),
                    "case C1 remains uninstalled on subsequent restart");
            }

            var dirC2 = Path.Combine(root, "host-upgrade-case-c2");
            await using (var runtime = NewRuntime(dirC2, [])) { await Start(runtime); }
            var oldC2Sha = OldSha("case-c2");
            ReplaceWithOldRelease(dirC2, currentQuota,
                new InstalledPluginPackage(quotaId) { Active = currentQuota with { Sha256 = oldC2Sha }, Pending = null, Previous = null, PendingUninstall = true },
                oldC2Sha);
            await using (var runtime = NewRuntime(dirC2, []))
            {
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == quotaId);
                Check(state.MetadataFailure is null && state.Active is null && state.Pending is null && !state.PendingUninstall
                    && !Registry(runtime).IsControlReady(quotaId),
                    "case C2 honors PendingUninstall across host rebuild without resurrecting");
            }

            // Case D1 & D2: tampered executionSha256 and non-host DownloadUrl are refused and never overwritten.
            var dirD1 = Path.Combine(root, "host-upgrade-case-d1");
            await using (var runtime = NewRuntime(dirD1, [])) { await Start(runtime); }
            var oldD1Sha = OldSha("case-d1");
            var validOldBytes = EncodeEnvelope(new InstalledPluginPackage(quotaId) { Active = currentQuota with { Sha256 = oldD1Sha } });
            var tamperedText = Encoding.UTF8.GetString(validOldBytes).Replace("\"pendingUninstall\": false", "\"pendingUninstall\":  false", StringComparison.Ordinal);
            var tamperedBytes = Encoding.UTF8.GetBytes(tamperedText);
            Check(!tamperedBytes.AsSpan().SequenceEqual(validOldBytes), "case D1 prepared tampered envelope bytes");
            var installedPathD1 = Path.Combine(dirD1, quotaId, "installed.json");
            await File.WriteAllBytesAsync(installedPathD1, tamperedBytes);
            await using (var runtime = NewRuntime(dirD1, []))
            {
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == quotaId);
                Check(state.MetadataFailure?.Code == "plugin_metadata_unreadable" && !Registry(runtime).IsControlReady(quotaId),
                    "case D1 rejects tampered executionSha256 as plugin_metadata_unreadable");
                Check((await File.ReadAllBytesAsync(installedPathD1)).AsSpan().SequenceEqual(tamperedBytes),
                    "case D1 never overwrites tampered installed.json");
            }

            var dirD2 = Path.Combine(root, "host-upgrade-case-d2");
            await using (var runtime = NewRuntime(dirD2, [])) { await Start(runtime); }
            var oldD2Sha = OldSha("case-d2");
            var thirdPartyBytes = EncodeEnvelope(new InstalledPluginPackage(quotaId)
            {
                Active = currentQuota with { Sha256 = oldD2Sha, DownloadUrl = "https://example.com/non-host.zip" }
            });
            var installedPathD2 = Path.Combine(dirD2, quotaId, "installed.json");
            await File.WriteAllBytesAsync(installedPathD2, thirdPartyBytes);
            await using (var runtime = NewRuntime(dirD2, []))
            {
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == quotaId);
                Check(state.MetadataFailure?.Code == "plugin_metadata_unreadable" && !Registry(runtime).IsControlReady(quotaId),
                    "case D2 refuses non-host-distribution DownloadUrl");
                Check((await File.ReadAllBytesAsync(installedPathD2)).AsSpan().SequenceEqual(thirdPartyBytes),
                    "case D2 leaves non-host installed.json untouched");
            }

            // Case E: .governance.json revoking target new host release blocks upgrade and leaves original state intact.
            var dirE = Path.Combine(root, "host-upgrade-case-e");
            await using (var runtime = NewRuntime(dirE, [])) { await Start(runtime); }
            var oldESha = OldSha("case-e");
            var oldEState = new InstalledPluginPackage(quotaId) { Active = currentQuota with { Sha256 = oldESha } };
            ReplaceWithOldRelease(dirE, currentQuota, oldEState, oldESha);
            var installedPathE = Path.Combine(dirE, quotaId, "installed.json");
            var originalBytesE = await File.ReadAllBytesAsync(installedPathE);
            var governanceJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                generatedAt = "2026-11-21T00:00:00Z",
                entries = new[]
                {
                    new
                    {
                        id = quotaId,
                        version = currentQuota.Version,
                        sha256 = currentQuota.Sha256.ToLowerInvariant(),
                        status = "revoked",
                        reason = "synthetic host release revocation check",
                        updatedAt = "2026-11-21T00:00:00Z"
                    }
                }
            });
            await File.WriteAllTextAsync(Path.Combine(dirE, ".governance.json"), governanceJson);
            var logsE = new List<string>();
            await using (var runtime = NewRuntime(dirE, logsE))
            {
                await Start(runtime);
                Check((await File.ReadAllBytesAsync(installedPathE)).AsSpan().SequenceEqual(originalBytesE),
                    "case E governance revocation leaves original installed.json untouched");
                Check(File.Exists(Path.Combine(dirE, quotaId, "releases", $"{currentQuota.Version}-{oldESha}.zip"))
                    && !File.Exists(Path.Combine(dirE, quotaId, "releases", $"{currentQuota.Version}-{currentQuota.Sha256.ToLowerInvariant()}.zip")),
                    "case E does not commit revoked archive or prune original archive");
                Check(!Registry(runtime).IsControlReady(quotaId) && !Registry(runtime).IsReady(quotaId),
                    "case E does not load revoked host package");
                var desc = Describe(runtime).Single(d => d.Key == new ManagedObjectKey.Plugin(new(quotaId)));
                Check(desc.ErrorCode == "plugin_governance_revoked",
                    "case E surfaces plugin_governance_revoked in descriptor");
                Check(logsE.Any(line => line.Contains($"pluginId={quotaId}", StringComparison.Ordinal)
                    && line.Contains("action=blocked-governance", StringComparison.Ordinal)),
                    "case E emits sanitized blocked-governance log");
            }
        }
        finally
        {
            flags.SetPluginEnabled(quotaId, prevQuotaEnabled);
            flags.SetPluginEnabled(stickersId, prevStickersEnabled);
        }
        return checks;
    }
}
