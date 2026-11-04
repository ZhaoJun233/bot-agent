using System.Text;
using BotAgent.Adapters.Persistence;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "botagent-snapshot-probe-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    checks++;
}
async Task Reject<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); }
    catch (T) { checks++; return; }
    throw new InvalidOperationException(label);
}
try
{
    var path = Path.Combine(root, "state.json");
    Check(TextSnapshotFile.Read(path) is null, "missing_snapshot");
    Check(await TextSnapshotFile.ReadAsync(Path.Combine(root, "missing", "state")) is null, "missing_parent");
    await Reject<FileNotFoundException>(() => Task.FromResult(TextSnapshotFile.ReadRequired(path)), "required_missing");
    TextSnapshotFile.Write(path, "synthetic-initial");
    Check(TextSnapshotFile.Read(path) == "synthetic-initial", "sync_roundtrip");
    await TextSnapshotFile.WriteAsync(path, "合成配置");
    Check(await TextSnapshotFile.ReadAsync(path) == "合成配置", "unicode_roundtrip");
    await Reject<InvalidDataException>(() => TextSnapshotFile.ReadAsync(path, 2), "bounded_async_read");
    await Reject<InvalidDataException>(() => Task.FromResult(TextSnapshotFile.Read(path, 2)), "bounded_sync_read");
    await Reject<ArgumentOutOfRangeException>(() => Task.FromResult(TextSnapshotFile.Read(path, -1)), "invalid_limit");
    await File.WriteAllTextAsync(path, "synthetic-bom", new UTF8Encoding(true));
    Check(TextSnapshotFile.Read(path) == "synthetic-bom", "legacy_bom");
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        await Reject<OperationCanceledException>(() => TextSnapshotFile.WriteAsync(path, "must-not-publish", cancellation.Token), "canceled_write");
        await Reject<OperationCanceledException>(() => TextSnapshotFile.ReadAsync(path, ct: cancellation.Token), "canceled_read");
    }
    Check(TextSnapshotFile.Read(path) == "synthetic-bom", "cancel_preserves_prior");
    var blocked = Path.Combine(root, "blocked-target");
    Directory.CreateDirectory(blocked);
    try
    {
        await TextSnapshotFile.WriteAsync(blocked, "must-not-publish");
        throw new InvalidOperationException("failed_move");
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { checks++; }
    Check(Directory.GetFiles(root, "*.tmp").Length == 0, "failed_write_cleanup");
    await Reject<DirectoryNotFoundException>(() => TextSnapshotFile.WriteAsync(Path.Combine(root, "missing", "state"), "ignored"), "no_implicit_parent");
    const int rounds = 20;
    var payloads = Enumerable.Range(0, rounds).Select(i => new string((char)('A' + i), 65536)).ToArray();
    await Task.WhenAll(payloads.Select(value => TextSnapshotFile.WriteAsync(path, value)));
    Check(payloads.Contains(TextSnapshotFile.Read(path)), "concurrent_atomic_publish");
    Check(Directory.GetFiles(root, "*.tmp").Length == 0, "concurrent_cleanup");
    await TextSnapshotFile.WriteAsync(path, string.Empty);
    Check(TextSnapshotFile.Read(path, 0) == string.Empty, "empty_snapshot");
    Check(await TextSnapshotFile.ReadAsync(path, 0) == string.Empty, "empty_async_snapshot");
    Console.WriteLine($"SnapshotProbe PASS {checks} synthetic checks");
}
finally
{
    if (!Path.GetFileName(root).StartsWith("botagent-snapshot-probe-", StringComparison.Ordinal))
        throw new InvalidOperationException("invalid_probe_cleanup_root");
    Directory.Delete(root, recursive: true);
}
