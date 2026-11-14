extern alias StorageModule;
using System.Text;
using System.Diagnostics;
using BotAgent.Services;
using SnapshotFile = StorageModule::BotAgent.Adapters.Persistence.TextSnapshotFile;

namespace BotAgent.Adapters.Persistence;

/// <summary>Exclusive ownership plus the existing flushed, atomic snapshot primitive.</summary>
public sealed class OfficialDeliverySnapshotFile : IOfficialDeliverySnapshot
{
    private readonly string _path;
    private readonly IDisposable _writerLock;
    private bool _disposed;

    public OfficialDeliverySnapshotFile(string path)
    {
        _path = Path.GetFullPath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _writerLock = AcquireWriter();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("official_delivery_store_unavailable");
        }
    }

    private IDisposable AcquireWriter()
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return SnapshotFile.AcquireExclusiveWriter(_path); }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 32)
            {
                // After process termination Windows may briefly retain the file's sharing lock.
                // Retry ownership only, before loading or committing anything. A live writer wins.
                if (elapsed.ElapsedMilliseconds >= 250) throw;
                Thread.Sleep(10);
            }
        }
    }

    public string? Load()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SnapshotFile.Read(_path, IOfficialDeliverySnapshot.MaxBytes);
    }

    public void Commit(string snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Encoding.UTF8.GetByteCount(snapshot) > IOfficialDeliverySnapshot.MaxBytes)
            throw new InvalidDataException("official_delivery_snapshot_too_large");
        SnapshotFile.Write(_path, snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writerLock.Dispose();
    }
}
