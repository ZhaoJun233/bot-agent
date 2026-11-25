using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;

internal static class MetadataIsolationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "plugin-metadata-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var good = new PluginPackageManifest("test.valid", "1.0.0", "probe.dll", "SyntheticPlugin",
                new string('a', 64), "https://example.com/plugin.zip");
            async Task<string> Write(string id, string text)
            {
                var directory = Path.Combine(root, id);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "installed.json");
                await File.WriteAllTextAsync(path, text);
                return path;
            }
            var validPath = await Write(good.Id, JsonSerializer.Serialize(new InstalledPluginPackage(good.Id, Active: good)));
            var corruptPath = await Write("test.corrupt", "{truncated synthetic metadata");
            var futurePath = await Write("test.future", """{"id":"test.future","futurePolicy":{"mode":"unknown"}}""");
            var nestedPath = await Write("test.nested", JsonSerializer.Serialize(new InstalledPluginPackage("test.nested",
                Previous: good with { Id = "test.nested", ApiVersion = 999 })));
            var duplicatePath = await Write("test.duplicate", """{"id":"test.duplicate","ID":"test.duplicate"}""");
            var oversizedPath = await Write("test.large", new string(' ', 131074));
            var invalidPath = await Write("bad name", "{}");
            var nullPath = await Write("test.null", "null");
            var bomPath = await Write("test.bom", "\ufeff" + JsonSerializer.Serialize(new InstalledPluginPackage("test.bom")));
            var before = new[] { validPath, corruptPath, futurePath, nestedPath, duplicatePath, oversizedPath, invalidPath, nullPath, bomPath }
                .ToDictionary(path => path, File.ReadAllBytes);
            var service = new PluginPackageService(root, new FakeHttp());
            var listed = await service.GetInstalledAsync();
            check(listed.Single(p => p.Id == good.Id).Active == good, "a corrupt neighbor must not block valid metadata");
            check(listed.Count == 9, "unreadable entries must remain observable in the directory");
            check(listed.Count(p => p.MetadataFailure is not null) == 7, "every invalid entry has one fault observation");
            check(listed.Single(p => p.Id == "test.bom").MetadataFailure is null, "UTF-8 BOM metadata remains readable");
            var corruption = listed.Single(p => p.Id == "test.corrupt").MetadataFailure!;
            check(corruption.Code == "plugin_metadata_unreadable" && corruption.Fingerprint == "sha256:" +
                Convert.ToHexString(SHA256.HashData(before[corruptPath])).ToLowerInvariant(), "fault binds exact raw bytes without echoing them");
            check(listed.Single(p => p.Id == "test.large").MetadataFailure?.Fingerprint == "prefix:" +
                Convert.ToHexString(SHA256.HashData(before[oversizedPath][..131073])).ToLowerInvariant(), "oversized observation is bounded and explicitly prefix-only");
            check(!JsonSerializer.Serialize(listed).Contains("MetadataFailure", StringComparison.OrdinalIgnoreCase), "observations never enter executable state JSON");
            check(listed.All(p => p.Id != "bad name") && listed.Any(p => p.MetadataFailure?.Code == "plugin_metadata_invalid_id"), "invalid directory names are opaque references");
            var rejected = false;
            try { await service.StageUninstallAsync("test.corrupt"); }
            catch (JsonException) { rejected = true; }
            check(rejected, "mutations use the strict reader and cannot overwrite unreadable state");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            rejected = false;
            try { await service.GetInstalledAsync(cancellation.Token); }
            catch (OperationCanceledException) { rejected = true; }
            check(rejected, "directory isolation does not swallow cancellation");
            foreach (var pair in before)
                check(File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value), "directory reads preserve exact metadata bytes");
            await File.WriteAllTextAsync(futurePath, JsonSerializer.Serialize(new InstalledPluginPackage("test.future")), Encoding.UTF8);
            check((await service.GetInstalledAsync()).Single(p => p.Id == "test.future").MetadataFailure is null,
                "restoring a compatible record clears its observation without restarting the directory");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
