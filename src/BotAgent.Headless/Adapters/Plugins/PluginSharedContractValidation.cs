using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

internal static class PluginSharedContractValidation
{
    internal static void Validate(PluginPackageManifest manifest)
    {
        if (manifest.SharedContracts is null) return;
        if (manifest.ApiVersion != 1 || manifest.SharedContracts.Count > 32)
            throw new InvalidDataException("plugin_contract_declaration_invalid");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contract in manifest.SharedContracts)
        {
            if (contract is null || contract.Name is null || !Regex.IsMatch(contract.Name, @"^[A-Za-z][A-Za-z0-9_.-]{0,119}$")
                || !names.Add(contract.Name) || contract.Name.Equals(typeof(IBotPlugin).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)
                || contract.Name.Equals(typeof(IPluginContribution).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(contract.Version ?? "", @"^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)$")
                || !Version.TryParse(contract.Version, out var version)
                || version.Major > 65534 || version.Minor > 65534 || version.Build > 65534 || version.Revision > 65534
                || !Regex.IsMatch(contract.Sha256 ?? "", "^[a-fA-F0-9]{64}$")
                || contract.Owner != manifest.Id && !(manifest.Dependencies ?? []).Any(d => d?.Id == contract.Owner)
                    && !PluginSharedContractRegistry.IsHostContract(contract))
                throw new InvalidDataException("plugin_contract_declaration_invalid");
            PluginPackageValidation.ValidateRelativePath(contract.Name + ".dll");
            if (manifest.EntryAssembly!.Equals(contract.Name + ".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("plugin_contract_entry_forbidden");
        }
    }

    internal static bool HasIndependentHostReference(PluginPackageManifest? manifest)
        => manifest?.SharedContracts?.Any(contract => contract is not null && contract.Owner != manifest.Id
            && !(manifest.Dependencies ?? []).Any(dependency => dependency?.Id == contract.Owner)) == true;

    internal static byte[] VerifyFile(string directory, PluginSharedContract contract)
    {
        var file = Path.Combine(directory, contract.Name + ".dll");
        // Exact file case is part of the archive contract on Windows as well as Linux.
        if (!Directory.EnumerateFiles(directory).Any(p => Path.GetFileName(p) == contract.Name + ".dll"))
            throw new InvalidDataException("plugin_contract_hash_mismatch");
        var bytes = File.ReadAllBytes(file);
        VerifyBytes(bytes, contract);
        return bytes;
    }

    internal static void VerifyBytes(byte[] bytes, PluginSharedContract contract)
    {
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(contract.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("plugin_contract_hash_mismatch");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new InvalidDataException("plugin_contract_identity_mismatch");
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) throw new InvalidDataException("plugin_contract_identity_mismatch");
            var definition = metadata.GetAssemblyDefinition();
            if (metadata.GetString(definition.Name) != contract.Name || definition.Version.ToString() != contract.Version
                || !string.IsNullOrEmpty(metadata.GetString(definition.Culture)))
                throw new InvalidDataException("plugin_contract_identity_mismatch");
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException("plugin_contract_identity_mismatch", exception);
        }
    }
}
