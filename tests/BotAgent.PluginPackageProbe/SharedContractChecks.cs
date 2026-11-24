using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

internal static class SharedContractChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "shared-contract-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new PluginManager(_ => { });
        try
        {
            var built = await SharedContractFixture.BuildAsync(root);
            var http = new FakeHttp();
            var packages = new PluginPackageService(Path.Combine(root, "installed"), http);
            var services = new FixtureServices();
            var context = new PluginContext(services);
            var contract = new PluginSharedContract("Synthetic.Usage.Contracts", "1.0.0.0",
                Digest(File.ReadAllBytes(built.Contract)), "test.contract.provider");
            PluginPackageManifest Manifest(string id, byte[] zip, bool consumer = false)
                => new(id, "1.0.0", consumer ? "Consumer.dll" : "Provider.dll", consumer ? "Consumer" : "Provider",
                    Digest(zip), "https://example.com/synthetic.zip", Dependencies: consumer ? [new(contract.Owner, "1.0.0")] : null)
                { SharedContracts = [contract] };
            var providerZip = Zip(built.Provider, built.Contract, built.ProviderPrivate);
            var consumerZip = Zip(built.Consumer, built.Contract, built.ConsumerPrivate);
            var provider = Manifest(contract.Owner, providerZip);
            var consumer = Manifest("test.contract.consumer", consumerZip, consumer: true);
            async Task Stage(PluginPackageManifest manifest, byte[] bytes)
            { http.Bytes = bytes; await packages.StageInstallAsync(manifest); }
            async Task Reject(Func<Task> action, string code, string label)
            {
                var failed = false;
                try { await action(); } catch (Exception ex) when (HasCode(ex, code)) { failed = true; }
                check(failed, label);
            }
            await Stage(provider, providerZip);
            await Stage(consumer, consumerZip);
            var metadata = Path.Combine(root, "installed", provider.Id, "installed.json");
            using (var document = JsonDocument.Parse(File.ReadAllBytes(metadata)))
                check(document.RootElement.GetProperty("minimumReaderVersion").GetInt32() == 2, "shared contracts require reader 2");
            var before = await File.ReadAllBytesAsync(metadata);
            var oldReader = await PluginStateCompatibility.InspectAsync(Path.Combine(root, "installed"), built.ReaderOne);
            check(oldReader.CandidateReaderVersion == 1 && !oldReader.Compatible
                && oldReader.Entries.All(entry => entry.Code == "plugin_state_reader_incompatible"), "reader 1 refuses contract state without loading candidate");
            var forged = JsonNode.Parse(before)!;
            forged["minimumReaderVersion"] = 1;
            await File.WriteAllTextAsync(metadata, forged.ToJsonString());
            check((await packages.GetInstalledAsync()).Single(p => p.Id == provider.Id).MetadataFailure is not null,
                "lowered envelope reader cannot conceal contract requirement");
            await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(new InstalledPluginPackage(provider.Id, Active: provider)));
            oldReader = await PluginStateCompatibility.InspectAsync(Path.Combine(root, "installed"), built.ReaderOne);
            check(!oldReader.Entries.Single(entry => entry.Id == provider.Id).Compatible, "legacy-shaped contract state still requires reader 2");
            await File.WriteAllBytesAsync(metadata, before);
            await Reject(() => packages.ApplyAsync(provider.Id, context, registry, true), "plugin_contract_cold_start_required", "shared contracts refuse online adoption");
            check(services.Exported is null, "online refusal precedes plugin initialization");
            await Reject(() => packages.ApplyAtStartupAsync(consumer.Id, context, registry, true), "plugin_dependency_unavailable", "consumer cannot initialize before ready owner");
            await packages.ApplyAtStartupAsync(provider.Id, context, registry, true);
            await packages.ApplyAtStartupAsync(consumer.Id, context, registry, true);
            var policy = (IReplyPolicyPlugin)registry.Find(consumer.Id)!;
            check(policy.BlockReason(new("synthetic", false, false)) == "usage:17:provider-1:consumer-2",
                "independent binaries call shared interface while private versions remain isolated");
            var shared = services.Exported!.GetType().GetInterfaces().Single(i => i.Name == "IUsageSnapshot").Assembly;
            check(ReferenceEquals(AssemblyLoadContext.GetLoadContext(shared), AssemblyLoadContext.Default), "contract has unique Default owner");
            check(AssemblyLoadContext.GetLoadContext(services.Exported.GetType().Assembly)!.IsCollectible, "business implementation remains collectible");
            check(AssemblyLoadContext.Default.Assemblies.Count(a => a.GetName().Name == contract.Name) == 1, "duplicate archive DLL does not create duplicate contract type");

            foreach (var (changed, code) in new[] { (contract with { Sha256 = new string('0', 64) }, "plugin_contract_hash_mismatch"),
                (contract with { Version = "2.0.0.0" }, "plugin_contract_identity_mismatch"), (contract with { Name = "Missing.Contracts" }, "plugin_contract_hash_mismatch") })
                await Reject(() => Stage(provider with { Id = "test.contract.invalid", SharedContracts = [changed with { Owner = "test.contract.invalid" }] }, providerZip), code,
                    "archive refuses contract hash, identity or missing file mismatch");
            var malformedDirectory = Directory.CreateDirectory(Path.Combine(root, "malformed")).FullName;
            var malformedFile = Path.Combine(malformedDirectory, contract.Name + ".dll");
            await File.WriteAllTextAsync(malformedFile, "synthetic non-assembly bytes");
            var malformedZip = Zip(built.Provider, malformedFile, built.ProviderPrivate);
            await Reject(() => Stage(Manifest("test.contract.malformed", malformedZip) with
                { SharedContracts = [contract with { Owner = "test.contract.malformed", Sha256 = Digest(File.ReadAllBytes(malformedFile)) }] }, malformedZip),
                "plugin_contract_identity_mismatch", "non-assembly contract is a structured validation failure");
            var wrongZip = Zip(built.Consumer, built.ContractV2, built.ConsumerPrivate);
            var wrong = Manifest("test.contract.changed", wrongZip, consumer: true) with
                { EntryType = "ChangedConsumer", SharedContracts = [contract with { Version = "2.0.0.0", Sha256 = Digest(File.ReadAllBytes(built.ContractV2)) }] };
            await Stage(wrong, wrongZip);
            await Reject(() => packages.ApplyAtStartupAsync(wrong.Id, context, registry, true), "plugin_contract_restart_required", "incompatible contract version cannot replace loaded identity");
            var hidden = Manifest("test.contract.undeclared", consumerZip, consumer: true) with { EntryType = "UndeclaredConsumer", SharedContracts = null };
            await Stage(hidden, consumerZip);
            await Reject(() => packages.ApplyAtStartupAsync(hidden.Id, context, registry, true), "plugin_contract_undeclared", "undeclared contract cannot fall back to Default or private duplicate");
            var wrongReferenceZip = Zip(built.ConsumerV2, built.Contract, built.ConsumerPrivate);
            var wrongReference = Manifest("test.contract.reference", wrongReferenceZip, consumer: true) with { EntryType = "ReferenceConsumer" };
            await Stage(wrongReference, wrongReferenceZip);
            await Reject(() => packages.ApplyAtStartupAsync(wrongReference.Id, context, registry, true), "plugin_contract_reference_mismatch", "compiled contract reference must match declared version");
            check(policy.BlockReason(new("synthetic", false, false)) == "usage:17:provider-1:consumer-2", "rejected candidates preserve original callable pair");

            using var entered = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            services.Read = () => { entered.Set(); if (!finish.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return 23; };
            var call = Task.Run(() => policy.BlockReason(new("synthetic", false, false)));
            check(entered.Wait(TimeSpan.FromSeconds(10)), "actual cross-package call entered provider");
            var remove = registry.RemoveAsync(provider.Id);
            try
            {
                check(!remove.IsCompleted && services.Shutdowns == 0, "provider shutdown drains real consumer call");
                check(policy.BlockReason(new("synthetic", false, false)) == "plugin_dependency_unavailable", "new consumer call refused while provider retires");
            }
            finally { finish.Set(); }
            check(await call == "usage:23:provider-1:consumer-2", "old typed call completes before release");
            await remove;
            check(services.Shutdowns == 1 && !registry.IsReady(consumer.Id), "provider exit invalidates dependent generation");
            check(shared.GetType("IUsageSnapshot") is not null && !shared.IsCollectible, "contract remains valid after provider exit");
            services.Read = () => 31;
            await packages.ApplyAtStartupAsync(provider.Id, context, registry, true);
            await packages.ApplyAtStartupAsync(consumer.Id, context, registry, true);
            check(((IReplyPolicyPlugin)registry.Find(consumer.Id)!).BlockReason(new("synthetic", false, false)) == "usage:31:provider-1:consumer-2",
                "same exact contract supports fresh bound generations");
            VerifyHistoricalHostContractUpgradeValidation(check);
        }
        finally
        {
            await registry.StopAllAsync(CancellationToken.None);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void VerifyHistoricalHostContractUpgradeValidation(Action<bool, string> check)
    {
        var stalePlatformsContract = new PluginSharedContract(
            "BotAgent.Platforms.Contracts", "1.0.0.0", new string('a', 64), "system.platform-local");
        var publicConsumer = new PluginPackageManifest(
            "test.platform.consumer", "1.0.0", "Consumer.dll", "Consumer",
            new string('b', 64), "https://example.com/consumer.zip")
        {
            SharedContracts = [stalePlatformsContract]
        };
        var publicRejected = false;
        try { PluginPackageValidation.Validate(publicConsumer); }
        catch (InvalidDataException ex) when (ex.Message == "plugin_contract_declaration_invalid") { publicRejected = true; }
        check(publicRejected, "public validation always rejects unowned stale host contract digest");

        var validateInternal = typeof(PluginPackageValidation).GetMethod(
            "Validate",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(PluginPackageManifest), typeof(bool), typeof(bool), typeof(bool)])
            ?? throw new InvalidOperationException("Missing internal PluginPackageValidation.Validate overload.");

        var platformConsumer = new PluginPackageManifest(
            "system.platform-qq-private", "1.0.0", "BotAgent.Package.PlatformQqPrivate.dll",
            "BotAgent.Packages.PlatformQqPrivate.PlatformQqPrivatePlugin",
            new string('b', 64), "host-distribution://system.platform-qq-private/1.0.0")
        {
            SharedContracts = [stalePlatformsContract]
        };

        var strictRejected = false;
        try { validateInternal.Invoke(null, [platformConsumer, false, true, false]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException ide && ide.Message == "plugin_contract_declaration_invalid") { strictRejected = true; }
        check(strictRejected, "strict internal validation rejects stale host contract digest");

        validateInternal.Invoke(null, [platformConsumer, false, true, true]);
        check(true, "historical host upgrade validation accepts matching host contract identity with stale digest");

        var wrongOwner = platformConsumer with
        {
            SharedContracts = [stalePlatformsContract with { Owner = "system.other" }]
        };
        var wrongOwnerRejected = false;
        try { validateInternal.Invoke(null, [wrongOwner, false, true, true]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException ide && ide.Message == "plugin_contract_declaration_invalid") { wrongOwnerRejected = true; }
        check(wrongOwnerRejected, "historical host upgrade validation rejects mismatched host contract owner");
    }

    private static byte[] Zip(params string[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            { using var entry = zip.CreateEntry(Path.GetFileName(file)).Open(); entry.Write(File.ReadAllBytes(file)); }
        return output.ToArray();
    }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool HasCode(Exception error, string code)
        => error.Message == code || error.InnerException is { } inner && HasCode(inner, code);

    // Fixture transport only. Production PluginServices still exposes no arbitrary business lookup.
    private sealed class FixtureServices : IServiceProvider
    {
        public object? Exported;
        public Func<int> Read = () => 17;
        public int Shutdowns;
        public object? GetService(Type type) => type == typeof(Action<object>) ? (Action<object>)(value => Exported = value)
            : type == typeof(Func<object>) ? (Func<object>)(() => Exported!)
            : type == typeof(Func<int>) ? (Func<int>)(() => Read())
            : type == typeof(Action) ? (Action)(() => Interlocked.Increment(ref Shutdowns)) : null;
    }
}
