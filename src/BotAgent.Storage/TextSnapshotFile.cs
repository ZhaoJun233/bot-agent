using System.Text;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// UTF-8 state snapshots with BOM-compatible reads and same-directory atomic replacement.
/// Missing files return null; permission, format and size failures remain distinguishable.
/// The caller owns directory creation and schema validation. Cancellation is checked before publication.
/// </summary>
public static class TextSnapshotFile
{
    private const int BufferSize = 16384;
    private static readonly Encoding OutputEncoding = new UTF8Encoding(false);
    private static readonly object PublicationGate = new();

    public static string? Read(string path, long maxBytes = long.MaxValue)
    {
        using var input = OpenSnapshot(path, maxBytes);
        if (input is null) return null;
        using var bytes = new MemoryStream();
        var buffer = new byte[BufferSize];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            AppendBounded(bytes, buffer, count, maxBytes);
        return Decode(bytes);
    }

    public static string ReadRequired(string path, long maxBytes = long.MaxValue)
        => Read(path, maxBytes) ?? throw new FileNotFoundException("snapshot_missing", path);

    public static async Task<string?> ReadAsync(string path, long maxBytes = long.MaxValue,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var input = OpenSnapshot(path, maxBytes);
        if (input is null) return null;
        using var bytes = new MemoryStream();
        var buffer = new byte[BufferSize];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            AppendBounded(bytes, buffer, count, maxBytes);
        return Decode(bytes);
    }

    public static void Write(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var target = Path.GetFullPath(path);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = CreateSnapshot(temporary))
            {
                using var writer = new StreamWriter(output, OutputEncoding, BufferSize, leaveOpen: true);
                writer.Write(content);
                writer.Flush();
                output.Flush(flushToDisk: true);
            }
            Publish(temporary, target);
        }
        finally { RemoveTemporary(temporary); }
    }

    public static async Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ct.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(path);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = CreateSnapshot(temporary))
            {
                await using var writer = new StreamWriter(output, OutputEncoding, BufferSize, leaveOpen: true);
                await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            Publish(temporary, target);
        }
        finally { RemoveTemporary(temporary); }
    }

    private static FileStream? OpenSnapshot(string path, long maxBytes)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        FileStream input;
        try
        {
            input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            if (input.Length > maxBytes) throw new InvalidDataException("snapshot_too_large");
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    private static FileStream CreateSnapshot(string temporary)
        => new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void AppendBounded(MemoryStream bytes, byte[] buffer, int count, long maxBytes)
    {
        if (count > maxBytes - bytes.Length) throw new InvalidDataException("snapshot_too_large");
        bytes.Write(buffer, 0, count);
    }

    private static string Decode(MemoryStream bytes)
    {
        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            BufferSize, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static void Publish(string temporary, string target)
    {
        // Concurrent overwrite renames can race inside Win32 even with unique temporary files.
        // Serialize only the short publication step, never the file writes or readers.
        lock (PublicationGate) File.Move(temporary, target, overwrite: true);
    }

    private static void RemoveTemporary(string temporary)
    {
        try { File.Delete(temporary); }
        catch (IOException) { /* A cleanup failure must not hide the publication outcome. */ }
        catch (UnauthorizedAccessException) { }
    }
}
