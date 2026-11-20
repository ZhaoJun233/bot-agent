using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>One exact contract identity per process. Business assemblies never enter this registry.
/// Contracts remain in Default until exit; an identity change requires a cold process restart.</summary>
internal static class PluginSharedContractRegistry
{
    private sealed record Binding(PluginSharedContract Contract, Assembly Assembly);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Binding> Bindings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<IReadOnlyDictionary<string, PluginSharedContract>> HostContracts = new(ReadHostContracts);
    private static readonly HashSet<string> Framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(path => Path.GetDirectoryName(path) == Path.GetDirectoryName(typeof(object).Assembly.Location))
        .Select(path => Path.GetFileNameWithoutExtension(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The compiled bundle declares each contract on its owner. Reading that authority does
    // not require this exception; imported manifests cannot supply their own host approval.
    internal static bool IsHostContract(PluginSharedContract contract)
        => HostContracts.Value.TryGetValue(contract.Name, out var host) && Same(host, contract);

    internal static IReadOnlyDictionary<string, Assembly> Bind(string directory, PluginPackageManifest manifest)
    {
        var contracts = manifest.SharedContracts ?? [];
        var verified = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Validate the entire request before adding any permanent Default binding.
        foreach (var contract in contracts)
        {
            if (HostContracts.Value.TryGetValue(contract.Name, out var host) && !Same(host, contract))
                throw new InvalidOperationException("plugin_contract_host_mismatch");
            var bytes = PluginSharedContractValidation.VerifyFile(directory, contract);
            VerifyReferences(bytes);
            verified.Add(contract.Name, bytes);
        }
        lock (Gate)
        {
            var preloaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (var contract in contracts)
            {
                if (Bindings.TryGetValue(contract.Name, out var existing))
                {
                    if (!Same(existing.Contract, contract))
                        throw new InvalidOperationException("plugin_contract_restart_required");
                }
                else
                {
                    var hostOwned = HostContracts.Value.ContainsKey(contract.Name);
                    if (contract.Owner != manifest.Id && !hostOwned)
                        throw new InvalidOperationException("plugin_contract_owner_unavailable");
                    var loaded = AssemblyLoadContext.Default.Assemblies.SingleOrDefault(a =>
                        string.Equals(a.GetName().Name, contract.Name, StringComparison.OrdinalIgnoreCase));
                    if (loaded is not null)
                    {
                        if (!hostOwned || !MatchesHostAssembly(loaded, contract))
                            throw new InvalidOperationException("plugin_contract_identity_conflict");
                        preloaded.Add(contract.Name, loaded);
                    }
                }
            }
            var result = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (var contract in contracts)
            {
                if (!Bindings.TryGetValue(contract.Name, out var existing))
                {
                    using var stream = new MemoryStream(verified[contract.Name], writable: false);
                    var assembly = preloaded.GetValueOrDefault(contract.Name) ?? AssemblyLoadContext.Default.LoadFromStream(stream);
                    existing = new(contract, assembly);
                    Bindings.Add(contract.Name, existing);
                }
                result.Add(contract.Name, existing.Assembly);
            }
            return result;
        }
    }

    internal static Assembly? Resolve(AssemblyName requested, IReadOnlyDictionary<string, Assembly> declared)
    {
        lock (Gate)
        {
            if (requested.Name is null) return null;
            if (declared.TryGetValue(requested.Name, out var assembly))
            {
                var identity = assembly.GetName();
                if (requested.FullName != identity.FullName)
                    throw new FileLoadException("plugin_contract_reference_mismatch");
                return assembly;
            }
            if (Bindings.ContainsKey(requested.Name) || HostContracts.Value.ContainsKey(requested.Name))
                throw new FileLoadException("plugin_contract_undeclared");
            return null;
        }
    }

    private static bool Same(PluginSharedContract left, PluginSharedContract right)
        => left.Name == right.Name && left.Version == right.Version && left.Owner == right.Owner
            && left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, PluginSharedContract> ReadHostContracts()
    {
        // Read the compiled distribution index directly (with SHA-256 verification) so bundle
        // validation of cross-referenced host contracts (e.g. BotAgent.Platforms.Contracts) never
        // re-enters HostContracts.Value through TrustedPluginDistribution.Read.
        var host = typeof(HostPluginBundle).Assembly;
        var metadata = host.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        var indexBytes = Convert.FromBase64String(metadata["BotAgent.PluginDistributionIndex"]!);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(indexBytes)).ToLowerInvariant();
        if (!string.Equals(digest, metadata["BotAgent.PluginDistribution"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("plugin_contract_host_digest_mismatch");
        var index = System.Text.Json.JsonSerializer.Deserialize<HostDistributionContractsIndex>(
            indexBytes, PluginPackageValidation.JsonOptions);
        var result = new Dictionary<string, PluginSharedContract>(StringComparer.OrdinalIgnoreCase);
        foreach (var contract in (index?.Releases ?? []).SelectMany(r => r.SharedContracts ?? []))
        {
            if (result.TryGetValue(contract.Name, out var previous) && !Same(previous, contract))
                throw new InvalidDataException("plugin_contract_host_conflict");
            result[contract.Name] = contract;
        }
        return result;
    }

    private sealed record HostDistributionContractsIndex(int FormatVersion, IReadOnlyList<PluginPackageManifest> Releases);

    private static bool MatchesHostAssembly(Assembly assembly, PluginSharedContract contract)
    {
        var directory = Path.GetDirectoryName(typeof(HostPluginBundle).Assembly.Location)!;
        var expected = Path.Combine(directory, contract.Name + ".dll");
        if (!string.Equals(assembly.Location, expected,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        var bytes = PluginSharedContractValidation.VerifyFile(directory, contract);
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        var metadata = pe.GetMetadataReader();
        return assembly.ManifestModule.ModuleVersionId == metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }

    private static void VerifyReferences(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(input);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var name = metadata.GetString(reference.Name);
            var sdk = typeof(IPluginContribution).Assembly.GetName();
            if (string.Equals(name, sdk.Name, StringComparison.OrdinalIgnoreCase))
            {
                var identity = new AssemblyName { Name = name, Version = reference.Version, CultureName = metadata.GetString(reference.Culture) };
                var key = metadata.GetBlobBytes(reference.PublicKeyOrToken);
                if ((reference.Flags & AssemblyFlags.PublicKey) != 0) identity.SetPublicKey(key);
                else identity.SetPublicKeyToken(key);
                if (identity.FullName != sdk.FullName)
                    throw new InvalidDataException("plugin_sdk_contract_reference_mismatch");
            }
            else if (!string.Equals(name, typeof(IBotPlugin).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)
                && !Framework.Contains(name))
                throw new InvalidDataException("plugin_contract_private_dependency");
        }
    }
}
