extern alias StorageModule;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services.Plugins;
using Usage = StorageModule::BotAgent.Adapters.Persistence.TokenUsageStore;
using Recovery = StorageModule::BotAgent.Adapters.Persistence.OperationRecoveryStore;

internal partial class Program
{
    internal static async Task<int> RunQuotaPackageChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        void Reject(Action action, string code)
        {
            try { action(); }
            catch (Exception e) when (e.Message == code) { checks++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        var isolated = Path.Combine(root, "quota-independent");
        var payload = await BuildQuotaPackage(isolated);
        checks += await RunQuotaLegacyDtoChecks(root);
        var contracts = typeof(ITenantQuotaManagement).Assembly;
        var contractBytes = await File.ReadAllBytesAsync(contracts.Location);
        var bytes = QuotaArchive(payload, contractBytes);
        var manifest = ReviewManifest(bytes, "preset.feature.token-quota", "1.0.0",
            "BotAgent.Package.TokenQuota.dll", "BotAgent.Packages.TokenQuota.QuotaPlugin") with
        {
            Control = true, Data = new(["tenant_quotas"], ["quota:revision:", "quota:receipts:"], true, true, new("quota.management", "1")),
            SharedContracts = [new(contracts.GetName().Name!, contracts.GetName().Version!.ToString(),
                Convert.ToHexString(SHA256.HashData(contractBytes)), "preset.feature.token-quota")]
        };
        const string nextVersion = "1.0.1-preview.1";
        var updatedBytes = QuotaArchive(await BuildQuotaPackage(Path.Combine(root, "quota-preview"), nextVersion), contractBytes);
        var updatedManifest = manifest with { Version = nextVersion, Sha256 = Convert.ToHexString(SHA256.HashData(updatedBytes)) };
        var index = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = new[] { manifest, updatedManifest } });
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_QUOTA_EVIDENCE") is { Length: > 0 } evidence)
        {
            await File.WriteAllBytesAsync(Path.Combine(evidence, "quota.zip"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(evidence, "quota-preview.zip"), updatedBytes);
            await File.WriteAllBytesAsync(Path.Combine(evidence, "distribution.json"), index);
        }
        var distribution = TrustedPluginDistribution.Read(index, Convert.ToHexString(SHA256.HashData(index)));
        var packages = new PluginPackageService(Path.Combine(root, "quota-packages"), new ReviewPackageHttp { Bytes = bytes }, distribution);
        await packages.StageDistributionInstallAsync(manifest, bytes);
        var context = new PluginContext(new EmptyQuotaServices());
        var registry = new PluginManager(_ => { });
        try
        {
            Reject(() => registry.GetContribution<ITenantQuotaManagement>(manifest.Id), "plugin_contribution_unavailable");
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var quota = registry.GetContribution<ITenantQuotaManagement>(manifest.Id);
            var control = registry.GetControlContribution<ITenantQuotaManagement>(manifest.Id);
            checks += RunQuotaOperationChecks(registry.GetControlContribution<IQuotaOperations>(manifest.Id));
            var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "quota-packages", manifest.Id, "installed.json")));
            Check(metadata.RootElement.GetProperty("minimumReaderVersion").GetInt32() == 4, "control declaration requires reader four");
            Check(registry.IsControlReady(manifest.Id), "approved control is ready after publication");
            var meter = new Usage();
            const string tenant = "synthetic:quota-package";
            const string caller = "synthetic:quota-admin";
            meter.RecordUsage(tenant, 30, 20);
            var before = quota.ReadManagement(tenant);
            Check(before.Revision == 0 && before.Quota.DailyTokenLimit == 50000 && before.Quota.UsedTotalTokens == 50, "independent DLL reads real host metering");
            var saved = quota.SaveManagementWithAudit(tenant, 40, 0, "synthetic-quota-save", caller,
                (_, _) => new("config_change", "synthetic", "probe", "quota", "1"));
            Check(saved.Revision == 1 && saved.Quota.EnergySaving && saved.Quota.UsedTotalTokens == 50, "real package commits limit revision and audit");
            var receipt = new Recovery().Find(manifest.Id, caller, "synthetic-quota-save");
            Check(receipt?.Generation?.Length == 97 && receipt.State == "committed", "quota receipt binds actual approved package activation");
            Check(quota.SaveManagementWithAudit(tenant, 40, 0, "synthetic-quota-save", caller, (_, _) => throw new Exception("replay_audit")) == saved, "original key replay precedes stale CAS and audit");
            Check(quota.FindManagement("synthetic-quota-save", caller) == saved, "typed contribution returns original business receipt");
            Reject(() => quota.SaveManagementWithAudit(tenant, 90, 0, "synthetic-quota-stale", caller, (_, _) => throw new Exception()), "quota_revision_conflict");
            Reject(() => quota.SaveManagementWithAudit(tenant, 90, 1, "synthetic-quota-fault", caller, (_, _) => throw new InvalidOperationException("synthetic_audit_fault")), "synthetic_audit_fault");
            Check(quota.ReadManagement(tenant) == saved && new Recovery().Find(manifest.Id, caller, "synthetic-quota-fault") is null, "audit failure rolls back limit revision and both receipts");
            var policy = (IReplyPolicyPlugin)registry.Find(manifest.Id)!;
            Check(policy.BlockReason(new(tenant, false, false)) == "quota_energy_saving"
                && policy.BlockReason(new(tenant, Proactive: false, DirectMention: true)) is null
                && policy.BlockReason(new(tenant, true, true)) == "quota_energy_saving", "independent admission preserves direct mention and proactive semantics");
            registry.SetEnabled(manifest.Id, false);
            meter.RecordUsage(tenant, 4, 6);
            Reject(() => quota.ReadManagement(tenant), "plugin_contribution_unavailable");
            Check(registry.GetControlContribution<IQuotaOperations>(manifest.Id).List([tenant], _ => "Synthetic").Items.Count == 1, "disabled business keeps HTTP control operations");
            Check(meter.GetUsage(tenant).UsedTotalTokens == 60 && policy.BlockReason(new(tenant, false, false)) is null, "disabled quota still records generic usage");
            Check(registry.IsControlReady(manifest.Id) && !registry.IsReady(manifest.Id)
                && control.ReadManagement(tenant).Quota.UsedTotalTokens == 60, "disabled policy retains approved control query");
            Check(control.FindManagement("synthetic-quota-save", caller) == saved, "disabled control resolves original business receipt");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var save = Task.Run(() => control.SaveManagementWithAudit(tenant, 80, 1, "synthetic-quota-drain", caller, (_, _) =>
            { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return new("config_change", "synthetic", "probe", "drain", "1"); }));
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "actual quota commit entered");
            var removal = registry.RemoveAsync(manifest.Id);
            try
            {
                Check(!removal.IsCompleted, "unload waits for actual quota transaction and contribution leases");
                Reject(() => control.ReadManagement(tenant), "plugin_contribution_unavailable");
                Reject(() => quota.ReadManagement(tenant), "plugin_contribution_unavailable");
            }
            finally { release.Set(); }
            Check((await save).Revision == 2, "in-flight quota commit finishes once");
            await removal;
            Check(new Recovery().Find(manifest.Id, caller, "synthetic-quota-drain")?.State == "committed", "safe receipt remains after package removal");
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, false);
            var disabledControl = registry.GetControlContribution<ITenantQuotaManagement>(manifest.Id);
            Check(!registry.IsReady(manifest.Id) && registry.IsControlReady(manifest.Id)
                && disabledControl.ReadManagement(tenant).Revision == 2, "cold disabled package prepares only management");
            Reject(() => registry.GetContribution<ITenantQuotaManagement>(manifest.Id), "plugin_contribution_unavailable");
            Check(disabledControl.FindManagement("synthetic-quota-save", caller) == saved, "cold disabled control preserves receipt semantics");
            await registry.RemoveAsync(manifest.Id);
            Reject(() => disabledControl.ReadManagement(tenant), "plugin_contribution_unavailable");
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var current = registry.GetContribution<ITenantQuotaManagement>(manifest.Id);
            Reject(() => quota.ReadManagement(tenant), "plugin_contribution_unavailable");
            Check(current.ReadManagement(tenant).Revision == 2 && current.FindManagement("synthetic-quota-save", caller) == saved, "same-version reactivation preserves old receipts and rejects old reference");
            await registry.RemoveAsync(manifest.Id);
            await packages.StageDistributionInstallAsync(updatedManifest, updatedBytes);
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            Check(registry.GetAll().Single().Version == nextVersion, "actual independent package preserves prerelease version");
            Reject(() => current.ReadManagement(tenant), "plugin_contribution_unavailable");
            Check(registry.GetContribution<ITenantQuotaManagement>(manifest.Id).FindManagement("synthetic-quota-save", caller) == saved, "package version adoption retains original business receipt");
        }
        finally { await registry.StopAllAsync(CancellationToken.None); }
        return checks;
    }

    private static async Task<byte[]> BuildQuotaPackage(string directory, string? version = null)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BotAgent.slnx"))) repo = repo.Parent;
        if (repo is null) throw new InvalidOperationException("quota_source_missing");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(repo.FullName, "packages/token-quota/backend");
        foreach (var file in Directory.EnumerateFiles(source, "*.cs")) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        Directory.CreateDirectory(Path.Combine(directory, "Schema"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "Schema"), "*.sql"))
            File.Copy(file, Path.Combine(directory, "Schema", Path.GetFileName(file)));
        File.Copy(Path.Combine(source, "BotAgent.Package.TokenQuota.csproj"), Path.Combine(directory, "Quota.csproj"));
        if (version is not null)
        {
            var project = XDocument.Load(Path.Combine(directory, "Quota.csproj"));
            project.Descendants("Version").Single().Value = version;
            project.Save(Path.Combine(directory, "Quota.csproj"));
        }
        File.Copy(Path.Combine(repo.FullName, "Directory.Build.props"), Path.Combine(directory, "Directory.Build.props"));
        await File.WriteAllTextAsync(Path.Combine(directory, "NuGet.Config"), "<configuration><packageSources><clear/></packageSources></configuration>");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "build", "Quota.csproj", "-c", "Release", "--nologo", "-o", "out", "-p:PluginSdkPath=" + Path.GetDirectoryName(typeof(IBotPlugin).Assembly.Location) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var diagnostics = await output + await error;
        await File.WriteAllTextAsync(Path.Combine(directory, "build.log"), diagnostics);
        if (process.ExitCode != 0) throw new InvalidOperationException("quota_independent_build_failed: " + diagnostics);
        var deps = await File.ReadAllTextAsync(Path.Combine(directory, "out/BotAgent.Package.TokenQuota.deps.json"));
        if (deps.Contains("BotAgent.Storage", StringComparison.Ordinal) || deps.Contains("BotAgent.Headless", StringComparison.Ordinal) || deps.Contains("BotAgent.Engine", StringComparison.Ordinal))
            throw new InvalidOperationException("quota_private_host_dependency");
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_QUOTA_EVIDENCE") is { Length: > 0 } evidence)
        {
            if (version is not null) evidence = Path.Combine(evidence, "preview-build");
            if (Directory.Exists(evidence)) throw new InvalidOperationException("quota_evidence_exists");
            Directory.CreateDirectory(evidence);
            foreach (var file in Directory.EnumerateFiles(directory)) File.Copy(file, Path.Combine(evidence, Path.GetFileName(file)));
            Directory.CreateDirectory(Path.Combine(evidence, "Schema"));
            foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "Schema")))
                File.Copy(file, Path.Combine(evidence, "Schema", Path.GetFileName(file)));
            Directory.CreateDirectory(Path.Combine(evidence, "out"));
            foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "out"))) File.Copy(file, Path.Combine(evidence, "out", Path.GetFileName(file)));
            Directory.CreateDirectory(Path.Combine(evidence, "sdk"));
            File.Copy(typeof(IBotPlugin).Assembly.Location, Path.Combine(evidence, "sdk/BotAgent.Core.dll"));
            File.Copy(typeof(IPluginContribution).Assembly.Location, Path.Combine(evidence, "sdk/BotAgent.Plugin.Contracts.dll"));
            File.Copy(typeof(ITenantQuotaManagement).Assembly.Location, Path.Combine(evidence, "sdk/BotAgent.Quota.Contracts.dll"));
            await File.WriteAllTextAsync(Path.Combine(evidence, "build-command.json"), JsonSerializer.Serialize(new
            { arguments = start.ArgumentList.ToArray(), exitCode = process.ExitCode, sdkSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(IBotPlugin).Assembly.Location))) }));
        }
        return await File.ReadAllBytesAsync(Path.Combine(directory, "out/BotAgent.Package.TokenQuota.dll"));
    }
    private sealed class EmptyQuotaServices : IServiceProvider { public object? GetService(Type type) => null; }

    private static byte[] QuotaArchive(byte[] assembly, byte[] contracts)
    {
        using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, true))
        {
            using (var entry = zip.CreateEntry("BotAgent.Package.TokenQuota.dll").Open()) entry.Write(assembly);
            using (var entry = zip.CreateEntry("BotAgent.Quota.Contracts.dll").Open()) entry.Write(contracts);
        }
        return result.ToArray();
    }
}
