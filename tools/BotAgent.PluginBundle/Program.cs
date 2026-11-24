using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BotAgent.Domain.Plugins;
using BotAgent.Adapters.Plugins;

internal static class Program
{
    private const string ZeroSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static async Task Main(string[] args)
    {
        // Input pairs are trusted build inputs, never package-managed paths or downloaded declarations.
        if (args.Length < 4 || args[0] != "--output" || args.Length % 2 != 0)
            throw new ArgumentException("--output <directory> <manifest-template> <managed-assembly> [...]");
        var output = Path.GetFullPath(args[1]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true, NewLine = "\n", UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var releases = new List<PluginPackageManifest>();
        var artifacts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var allowedExtensionTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".mjs"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".json"] = "application/json; charset=utf-8",
        };
        for (var i = 2; i < args.Length; i += 2)
        {
            var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(await PluginPackageFiles.ReadRequiredAsync(args[i], 32768, default), options)
                ?? throw new InvalidDataException("bundle_manifest_missing");
            if (!Regex.IsMatch(manifest.Id ?? "", @"^system[.][a-z][a-z0-9.-]{0,70}$")
                || manifest.Id!.Contains("..", StringComparison.Ordinal) || manifest.Id.EndsWith('.')
                || manifest.ApiVersion != 1 || manifest.EntryAssembly != Path.GetFileName(args[i + 1])
                || manifest.Version is not null || manifest.Sha256 is not null || manifest.DownloadUrl is not null)
                throw new InvalidDataException("bundle_template_invalid");
            if (manifest.Adoption is not null && manifest.Adoption is not (PluginAdoptionMode.Cold or PluginAdoptionMode.Online))
                throw new InvalidDataException("bundle_adoption_invalid");
            if (manifest.ExclusiveResources is { Count: > 0 } exclusive)
            {
                if (exclusive.Count > 32 || exclusive.Distinct(StringComparer.Ordinal).Count() != exclusive.Count
                    || exclusive.Any(item => string.IsNullOrEmpty(item) || !Regex.IsMatch(item, "^[a-z][a-z0-9._-]{0,79}$")))
                    throw new InvalidDataException("bundle_exclusive_resources_invalid");
                if (manifest.Adoption == PluginAdoptionMode.Online)
                    throw new InvalidDataException("bundle_exclusive_resource_requires_cold");
            }
            var assembly = await PluginPackageFiles.ReadRequiredAsync(args[i + 1], 64 * 1024 * 1024, default);
            var version = ReadVersion(assembly, manifest.EntryAssembly!);
            manifest = ConfigurationDefinitionReader.Read(Path.GetFullPath(args[i + 1]), manifest);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [manifest.EntryAssembly!] = assembly };
            var contracts = new List<PluginSharedContract>();
            foreach (var contract in manifest.SharedContracts ?? [])
            {
                if (!Regex.IsMatch(contract.Name ?? "", @"^[A-Za-z][A-Za-z0-9_.-]{0,119}$")
                    || contract.Name!.Contains("..", StringComparison.Ordinal)
                    || string.IsNullOrEmpty(contract.Owner)
                    || !Regex.IsMatch(contract.Owner, @"^system[.][a-z][a-z0-9.-]{0,70}$")
                    || contract.Version is not null || contract.Sha256 is not null
                    || contract.Name.Equals(typeof(IBotPlugin).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)
                    || contract.Name.Equals(typeof(IPluginContribution).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)
                    || files.ContainsKey(contract.Name + ".dll"))
                    throw new InvalidDataException("bundle_contract_template_invalid");
                var contractBytes = await PluginPackageFiles.ReadRequiredAsync(
                    Path.Combine(Path.GetDirectoryName(args[i + 1])!, contract.Name + ".dll"), 64 * 1024 * 1024, default);
                using var pe = new PEReader(new MemoryStream(contractBytes, writable: false));
                var metadata = pe.GetMetadataReader();
                var definition = metadata.GetAssemblyDefinition();
                if (metadata.GetString(definition.Name) != contract.Name || !definition.Culture.IsNil)
                    throw new InvalidDataException("bundle_contract_identity");
                files.Add(contract.Name + ".dll", contractBytes);
                contracts.Add(contract with { Version = definition.Version.ToString(), Sha256 = Hash(contractBytes) });
            }
            PluginUiDeclaration? packagedUi = null;
            if (manifest.Ui is { } uiTemplate)
            {
                // Source distribution.json templates intentionally leave ui.assets[].sha256 blank or set to
                // the all-zero placeholder (0000...0000) so template files stay deterministic across UI edits;
                // PluginBundle always computes and enforces the authoritative SHA-256 from packaged bytes.
                if (uiTemplate.ContractVersion != PluginUiDeclaration.CurrentContractVersion
                    || uiTemplate.Assets is null || uiTemplate.Assets.Count is 0 or > 64
                    || string.IsNullOrEmpty(uiTemplate.Entry) || !uiTemplate.Entry.StartsWith("web/", StringComparison.Ordinal)
                    || !uiTemplate.Entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    || uiTemplate.Entry.Contains("..", StringComparison.Ordinal) || uiTemplate.Entry.Contains('\\'))
                    throw new InvalidDataException("bundle_ui_declaration_invalid");
                var templateDir = Path.GetDirectoryName(Path.GetFullPath(args[i]))!;
                var uiAssets = new List<PluginUiAsset>(uiTemplate.Assets.Count);
                long uiTotal = 0;
                var entryFound = false;
                foreach (var asset in uiTemplate.Assets)
                {
                    if (string.IsNullOrEmpty(asset.Path) || !asset.Path.StartsWith("web/", StringComparison.Ordinal)
                        || asset.Path.Contains("..", StringComparison.Ordinal) || asset.Path.Contains('\\')
                        || files.ContainsKey(asset.Path))
                        throw new InvalidDataException("bundle_ui_asset_invalid");
                    if (!string.IsNullOrEmpty(asset.Sha256)
                        && !string.Equals(asset.Sha256, ZeroSha256, StringComparison.Ordinal))
                        throw new InvalidDataException("bundle_ui_asset_template_hash_must_be_placeholder");
                    var ext = Path.GetExtension(asset.Path);
                    if (!allowedExtensionTypes.TryGetValue(ext, out var expectedType)
                        || !string.Equals(asset.ContentType, expectedType, StringComparison.Ordinal))
                        throw new InvalidDataException("bundle_ui_asset_content_type_invalid");
                    var assetBytes = await PluginPackageFiles.ReadRequiredAsync(
                        Path.Combine(templateDir, asset.Path), 4 * 1024 * 1024, default);
                    uiTotal = checked(uiTotal + assetBytes.Length);
                    if (uiTotal > 4L * 1024 * 1024) throw new InvalidDataException("bundle_ui_asset_limit_exceeded");
                    if (string.Equals(asset.Path, uiTemplate.Entry, StringComparison.Ordinal)) entryFound = true;
                    files.Add(asset.Path, assetBytes);
                    var assetHash = Hash(assetBytes);
                    if (!Regex.IsMatch(assetHash, "^[0-9a-f]{64}$")
                        || string.Equals(assetHash, ZeroSha256, StringComparison.Ordinal))
                        throw new InvalidDataException("bundle_ui_asset_hash_invalid");
                    uiAssets.Add(asset with { Sha256 = assetHash });
                }
                if (!entryFound) throw new InvalidDataException("bundle_ui_entry_missing");
                packagedUi = uiTemplate with { Assets = uiAssets.OrderBy(a => a.Path, StringComparer.Ordinal).ToArray() };
            }
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    entry.ExternalAttributes = 0;
                    using var stream = entry.Open();
                    stream.Write(content);
                }
            }
            var bytes = buffer.ToArray();
            manifest = manifest with { Version = version, Sha256 = Hash(bytes),
                DownloadUrl = $"host-distribution://{manifest.Id}/{version}",
                SharedContracts = contracts.Count == 0 ? null : contracts,
                Ui = packagedUi };
            if (releases.Any(item => item.Id == manifest.Id)) throw new InvalidDataException("bundle_duplicate_owner");
            releases.Add(manifest);
            artifacts.Add(manifest.Sha256 + ".zip", bytes);
        }
        // Verify all shared-contract owners exist in the compiled bundle and agree on exact identity,
        // and forbid any all-zero placeholder SHA-256 across packaged releases, contracts, and UI assets.
        var releaseIds = releases.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var bundleContracts = new Dictionary<string, PluginSharedContract>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in releases)
        {
            if (string.Equals(release.Sha256, ZeroSha256, StringComparison.Ordinal))
                throw new InvalidDataException("bundle_placeholder_hash_forbidden");
            foreach (var asset in release.Ui?.Assets ?? [])
                if (string.Equals(asset.Sha256, ZeroSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("bundle_placeholder_hash_forbidden");
            foreach (var contract in release.SharedContracts ?? [])
            {
                if (string.Equals(contract.Sha256, ZeroSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("bundle_placeholder_hash_forbidden");
                if (!releaseIds.Contains(contract.Owner))
                    throw new InvalidDataException("bundle_contract_owner_missing");
                if (bundleContracts.TryGetValue(contract.Name, out var existing)
                    && (existing.Owner != contract.Owner || existing.Version != contract.Version
                        || !existing.Sha256.Equals(contract.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("bundle_contract_identity_conflict");
                bundleContracts[contract.Name] = contract;
            }
        }
        var index = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = releases.OrderBy(r => r.Id, StringComparer.Ordinal) }, options);
        artifacts.Add("index.json", index);
        artifacts.Add("bundle-files.txt", Encoding.UTF8.GetBytes(string.Join("\n", artifacts.Keys.OrderBy(n => n, StringComparer.Ordinal)) + "\n"));
        artifacts.Add("HostPluginBundle.g.cs", Encoding.UTF8.GetBytes(
            $"[assembly: System.Reflection.AssemblyMetadata(\"BotAgent.PluginDistribution\", \"{Hash(index)}\")]\n" +
            $"[assembly: System.Reflection.AssemblyMetadata(\"BotAgent.PluginDistributionIndex\", \"{Convert.ToBase64String(index)}\")]\n"));
        // Validate all inputs before writing. The host pins index bytes in its own assembly, never in this directory.
        Directory.CreateDirectory(output);
        foreach (var staleZip in Directory.EnumerateFiles(output, "*.zip"))
        {
            if (!artifacts.ContainsKey(Path.GetFileName(staleZip)))
                try { File.Delete(staleZip); } catch { }
        }
        foreach (var (name, bytes) in artifacts)
        {
            var path = Path.Combine(output, name);
            var existing = await PluginPackageFiles.ReadPrefixAsync(path, bytes.Length, default);
            if (existing is null || !existing.AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
        }
        Console.WriteLine($"bundle_releases={releases.Count} index_sha256={Hash(index)}");

    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static string ReadVersion(byte[] bytes, string fileName)
    {
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        var metadata = pe.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        if (metadata.GetString(definition.Name) + ".dll" != fileName) throw new InvalidDataException("bundle_assembly_identity");
        foreach (var handle in definition.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection"
                || metadata.GetString(type.Name) != "AssemblyInformationalVersionAttribute") continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) break;
            var version = blob.ReadSerializedString()?.Split('+', 2)[0];
            if (Regex.IsMatch(version ?? "", @"^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$"))
                return version!;
            break;
        }
        throw new InvalidDataException("bundle_version_missing");
    }
}
