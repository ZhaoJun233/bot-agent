using System.IO.Compression;
using System.Security.Cryptography;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

internal static class PluginPackageArchive
{
    internal const int MaxArchiveBytes = 32 * 1024 * 1024;
    private const long MaxExpandedBytes = 128 * 1024 * 1024;

    internal static async Task ExtractAsync(byte[] bytes, PluginPackageManifest manifest, string target, CancellationToken ct)
    {
        if (bytes.Length > MaxArchiveBytes || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package hash mismatch or size exceeded.");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 2048) throw new InvalidDataException("Too many package entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exactFiles = new HashSet<string>(StringComparer.Ordinal);
        long declared = 0, actual = 0;
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
            declared = checked(declared + entry.Length);
            if (declared > MaxExpandedBytes || entry.Length > MaxArchiveBytes)
                throw new InvalidDataException("Expanded package limit exceeded.");
            if (manifest.ApiVersion == 2)
            {
                PluginPythonArchive.ValidatePath(name, unixType, directory);
                if (declared > 32L * 1024 * 1024 || entry.Length > Math.Max(1L, entry.CompressedLength) * 200)
                    throw new InvalidDataException("Python archive expansion or compression ratio exceeds limit.");
            }
            var destination = Path.Combine(target, name);
            if (directory) { Directory.CreateDirectory(destination); continue; }
            exactFiles.Add(name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[81920];
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
        if (!File.Exists(Path.Combine(target, manifest.Runtime?.EntryPoint ?? manifest.EntryAssembly!)))
            throw new InvalidDataException("Package entry missing.");

    }
}
