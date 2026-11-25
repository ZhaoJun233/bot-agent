extern alias StorageModule;

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.TestFixtures;

internal static class OfficialMarketplaceTestHelper
{
    private static readonly Lazy<string> MarketplaceRootValue = new(() =>
        OfficialPluginsTestLocator.ResolveMarketplaceRoot(OfficialPluginsTestLocator.FindRepoRoot()));

    internal static string MarketplaceRoot => MarketplaceRootValue.Value;

    internal static readonly IReadOnlyDictionary<string, string> FeatureFolders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["preset.feature.digest"] = "digest",
            ["preset.feature.documents"] = "documents",
            ["preset.feature.knowledge"] = "knowledge",
            ["preset.feature.music"] = "music",
            ["preset.feature.personal-todo"] = "personal-todo",
            ["preset.feature.poke"] = "poke",
            ["preset.feature.profiles"] = "profiles",
            ["preset.feature.scheduler"] = "scheduler",
            ["preset.feature.stickers"] = "stickers",
            ["preset.feature.token-quota"] = "token-quota",
            ["preset.feature.vibes"] = "vibes",
            ["preset.feature.voice"] = "voice",
            ["preset.feature.websearch"] = "websearch",
        };

    private static readonly MethodInfo EncodeEnvelopeMethod = typeof(PluginPackageService).Assembly
        .GetType("BotAgent.Adapters.Plugins.PluginStateEnvelope", throwOnError: true)!
        .GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static string ExpectedDownloadUrl(string pluginId, string version = "1.1.0")
        => $"https://github.com/ZhaoJun233/bot-agent-plugins/releases/download/{pluginId}-{version}/{pluginId}-{version}.zip";

    internal static (PluginPackageManifest Manifest, byte[] ArchiveBytes, string Directory) ReadRelease(
        string pluginId, string version = "1.1.0")
    {
        if (!FeatureFolders.TryGetValue(pluginId, out var folder))
            throw new ArgumentException("Unknown official feature plugin ID: " + pluginId, nameof(pluginId));
        return OfficialPluginsTestLocator.ReadOfficialRelease(MarketplaceRoot, folder, pluginId, version);
    }

    internal static string PluginBackendSourceDirectory(string pluginId)
    {
        if (!FeatureFolders.TryGetValue(pluginId, out var folder))
            throw new ArgumentException("Unknown official feature plugin ID: " + pluginId, nameof(pluginId));
        return OfficialPluginsTestLocator.PluginBackendSourceDirectory(MarketplaceRoot, folder);
    }

    internal static byte[] EncodeEnvelope(InstalledPluginPackage state)
        => (byte[])EncodeEnvelopeMethod.Invoke(null, [state])!;

    internal static void StageInstalled(string pluginRoot, PluginPackageManifest manifest, byte[] archiveBytes)
    {
        var pkgDir = Path.Combine(pluginRoot, manifest.Id);
        var releasesDir = Path.Combine(pkgDir, "releases");
        Directory.CreateDirectory(releasesDir);
        File.WriteAllBytes(Path.Combine(releasesDir, $"{manifest.Version}-{manifest.Sha256.ToLowerInvariant()}.zip"), archiveBytes);
        File.WriteAllBytes(Path.Combine(pkgDir, "installed.json"), EncodeEnvelope(new InstalledPluginPackage(manifest.Id, Active: manifest)));
    }

    internal static (PluginPackageManifest Manifest, byte[] ArchiveBytes) BuildLegacyHostDistributionMusicRelease(
        string tempRoot, PluginPackageManifest currentMusicManifest)
    {
        var sourceDir = PluginBackendSourceDirectory("preset.feature.music");
        var copyDir = Path.Combine(tempRoot, "legacy-music-src");
        var outDir = Path.Combine(tempRoot, "legacy-music-out");
        Directory.CreateDirectory(copyDir);
        Directory.CreateDirectory(outDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.csproj")
            .Concat(Directory.EnumerateFiles(sourceDir, "*.cs")))
        {
            File.Copy(file, Path.Combine(copyDir, Path.GetFileName(file)), overwrite: true);
        }

        var csproj = Directory.EnumerateFiles(copyDir, "*.csproj").Single();
        var hostBinDir = Path.GetDirectoryName(typeof(PluginPackageService).Assembly.Location)!;
        var psi = new ProcessStartInfo("dotnet",
            $"build \"{csproj}\" -c Release -p:Version=1.0.0 -p:PluginSdkPath=\"{hostBinDir}\" -o \"{outDir}\" --nologo -v quiet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet build for legacy music.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Legacy 1.0.0 music build failed ({proc.ExitCode}):\n{stdout}\n{stderr}");

        var contractBytes = File.ReadAllBytes(Path.Combine(hostBinDir, "BotAgent.Music.Contracts.dll"));
        var contractSha = Convert.ToHexString(SHA256.HashData(contractBytes)).ToLowerInvariant();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entryDll = Path.Combine(outDir, "BotAgent.Package.Music.dll");
            WriteZipEntry(zip, "BotAgent.Package.Music.dll", File.ReadAllBytes(entryDll));
            WriteZipEntry(zip, "BotAgent.Music.Contracts.dll", contractBytes);
        }
        var archiveBytes = ms.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var sharedContracts = (currentMusicManifest.SharedContracts ?? [])
            .Select(c => c.Name == "BotAgent.Music.Contracts" ? c with { Sha256 = contractSha } : c)
            .ToArray();
        var legacyManifest = currentMusicManifest with
        {
            Version = "1.0.0",
            Sha256 = sha,
            DownloadUrl = "host-distribution://preset.feature.music/1.0.0",
            SharedContracts = sharedContracts
        };
        return (legacyManifest, archiveBytes);
    }

    private static void WriteZipEntry(ZipArchive zip, string entryName, byte[] content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    internal static void StageOfficialPackages(string pluginRoot, IEnumerable<string> pluginIds, bool enableInStore = false)
    {
        var store = enableInStore ? new StorageModule::BotAgent.Storage.PluginStore() : null;
        foreach (var id in pluginIds)
        {
            var (manifest, bytes, _) = ReadRelease(id);
            StageInstalled(pluginRoot, manifest, bytes);
            store?.SetPluginEnabled(id, true);
        }
    }

    internal static void StageAllOfficialFeatures(string pluginRoot, bool enableInStore = true)
        => StageOfficialPackages(pluginRoot, FeatureFolders.Keys, enableInStore);

    internal static MarketplaceStubHttp CreateStubHttp(params (PluginPackageManifest Manifest, byte[] Bytes)[] packages)
        => new(packages);

    internal sealed class MarketplaceStubHttp : IHttpFetcher
    {
        private readonly Dictionary<string, byte[]> _archives = new(StringComparer.Ordinal);
        public MarketplaceStubHttp(IEnumerable<(PluginPackageManifest Manifest, byte[] Bytes)> packages)
        {
            foreach (var (manifest, bytes) in packages)
                _archives[manifest.DownloadUrl] = bytes;
        }
        public int Calls { get; private set; }
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (url.EndsWith("/governance.json", StringComparison.Ordinal))
            {
                var doc = $"{{\"schemaVersion\":1,\"generatedAt\":\"{DateTimeOffset.UtcNow:O}\",\"entries\":[]}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(doc, Encoding.UTF8, "application/json") });
            }
            Calls++;
            if (_archives.TryGetValue(url, out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
            => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.AbsoluteUri, ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.AbsoluteUri, ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => GetAsync(url, ct);
    }
}
