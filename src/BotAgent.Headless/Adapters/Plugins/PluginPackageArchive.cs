using System.IO.Compression;
using System.Security.Cryptography;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

internal static class PluginPackageArchive
{
    internal const int MaxArchiveBytes = 32 * 1024 * 1024;
    private const long MaxExpandedBytes = 128 * 1024 * 1024;

    // Staging checks every payload without creating files that it would immediately discard.
    // Activation still extracts and verifies a fresh private runtime directory.
    internal static Task ValidateAsync(byte[] bytes, PluginPackageManifest manifest, CancellationToken ct)
        => ReadAsync(bytes, manifest, null, ct);

    internal static Task ExtractAsync(byte[] bytes, PluginPackageManifest manifest, string target, CancellationToken ct)
        => ReadAsync(bytes, manifest, target, ct);

    private static async Task ReadAsync(byte[] bytes, PluginPackageManifest manifest, string? target, CancellationToken ct)
    {
        if (bytes.Length > MaxArchiveBytes || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package hash mismatch or size exceeded.");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 2048) throw new InvalidDataException("Too many package entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileSystemComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = new HashSet<string>(fileSystemComparer);
        var directories = new HashSet<string>(fileSystemComparer);
        var exactFiles = new HashSet<string>(StringComparer.Ordinal);
        long declared = 0, actual = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var name = directory ? entry.FullName[..^1] : entry.FullName;
            PluginPackageValidation.ValidateRelativePath(name);
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((unixType != 0 && unixType != 0x8000 && unixType != 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || !paths.Add(name))
                throw new InvalidDataException("Links, special files or duplicate entries forbidden.");
            ValidateParents(name, directory, files, directories);
            declared = checked(declared + entry.Length);
            if (declared > MaxExpandedBytes || entry.Length > MaxArchiveBytes)
                throw new InvalidDataException("Expanded package limit exceeded.");
            if (manifest.ApiVersion == 2)
            {
                PluginPythonArchive.ValidatePath(name, unixType, directory);
                if (declared > 32L * 1024 * 1024 || entry.Length > Math.Max(1L, entry.CompressedLength) * 200)
                    throw new InvalidDataException("Python archive expansion or compression ratio exceeds limit.");
            }
            var destination = target is null ? null : Path.Combine(target, name);
            if (directory)
            {
                if (destination is not null) Directory.CreateDirectory(destination);
                continue;
            }
            exactFiles.Add(name);
            if (destination is not null) Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using Stream output = destination is null ? Stream.Null
                : new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            long entryActual = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) != 0)
            {
                actual += count; entryActual += count;
                if (actual > MaxExpandedBytes || entryActual > entry.Length)
                    throw new InvalidDataException("Expanded package limit exceeded.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (entryActual != entry.Length) throw new InvalidDataException("Truncated package entry.");
            await output.DisposeAsync().ConfigureAwait(false);
            if (manifest.ApiVersion == 2 && name.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                await using var source = entry.Open();
                await PluginPythonArchive.ValidateSourceAsync(source, ct).ConfigureAwait(false);
            }
        }
        if (manifest.ApiVersion == 2 && (!exactFiles.Contains(manifest.Runtime!.EntryPoint) || !exactFiles.Contains("botagent_worker.py")))
            throw new InvalidDataException("Python entry and SDK names must match exact archive paths.");
        var entryPoint = manifest.Runtime?.EntryPoint ?? manifest.EntryAssembly!;
        var entryPresent = target is null
            ? exactFiles.Contains(entryPoint) || OperatingSystem.IsWindows() && files.Contains(entryPoint)
            : File.Exists(Path.Combine(target, entryPoint));
        if (!entryPresent)
            throw new InvalidDataException("Package entry missing.");
        foreach (var contract in manifest.SharedContracts ?? [])
        {
            if (target is not null) PluginSharedContractValidation.VerifyFile(target, contract);
            else
            {
                var name = contract.Name + ".dll";
                if (!exactFiles.Contains(name)) throw new InvalidDataException("plugin_contract_hash_mismatch");
                var entry = zip.GetEntry(name)!;
                await using var input = entry.Open();
                using var output = new MemoryStream((int)entry.Length);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                PluginSharedContractValidation.VerifyBytes(output.ToArray(), contract);
            }
        }
        if (manifest.Ui is { } ui)
        {
            long uiTotal = 0;
            foreach (var asset in ui.Assets)
            {
                if (!exactFiles.Contains(asset.Path)) throw new InvalidDataException("plugin_ui_asset_missing");
                var entry = zip.GetEntry(asset.Path) ?? throw new InvalidDataException("plugin_ui_asset_missing");
                uiTotal = checked(uiTotal + entry.Length);
                if (uiTotal > PluginUiValidation.MaxTotalUiBytes) throw new InvalidDataException("plugin_ui_asset_limit_exceeded");
                await using var input = entry.Open();
                using var output = new MemoryStream((int)Math.Min(entry.Length, PluginUiValidation.MaxTotalUiBytes));
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                var digest = Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant();
                if (!digest.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("plugin_ui_asset_hash_mismatch");
            }
        }
    }

    private static void ValidateParents(string name, bool directory, HashSet<string> files, HashSet<string> directories)
    {
        // The preflight must reject the same file/directory collisions as CreateNew during extraction,
        // including implicit parents and either archive entry order.
        for (var separator = name.LastIndexOf('/'); separator >= 0; separator = name.LastIndexOf('/', separator - 1))
        {
            var parent = name[..separator];
            if (files.Contains(parent)) throw new InvalidDataException("Package file conflicts with directory.");
            directories.Add(parent);
        }
        if (directory) directories.Add(name);
        else
        {
            if (directories.Contains(name)) throw new InvalidDataException("Package file conflicts with directory.");
            files.Add(name);
        }
    }
}
