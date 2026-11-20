namespace BotAgent.Adapters.Plugins;

/// <summary>One bounded binary reader for package archives, metadata and offline candidate inspection.</summary>
internal static class PluginPackageFiles
{
    // A single extra byte distinguishes a complete file from an explicitly bounded diagnostic prefix.
    internal static async Task<byte[]?> ReadPrefixAsync(string path, int maximumBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (maximumBytes < 0 || maximumBytes == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        await using (input)
        {
            var bytes = new byte[Math.Min(input.Length, maximumBytes + 1L)];
            var count = await input.ReadAtLeastAsync(bytes, bytes.Length, false, ct).ConfigureAwait(false);
            return bytes[..count];
        }
    }

    internal static async Task<byte[]> ReadRequiredAsync(string path, int maximumBytes, CancellationToken ct)
    {
        var bytes = await ReadPrefixAsync(path, maximumBytes, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException("package_file_missing");
        if (bytes.Length > maximumBytes) throw new InvalidDataException("package_file_too_large");
        return bytes;
    }
}
