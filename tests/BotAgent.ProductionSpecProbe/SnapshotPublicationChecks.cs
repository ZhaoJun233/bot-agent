extern alias StorageModule;

using TextSnapshotFile = StorageModule::BotAgent.Adapters.Persistence.TextSnapshotFile;

namespace BotAgent.ProductionSpecProbe;

internal static class SnapshotPublicationChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "snapshot-publication");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        TextSnapshotFile.Write(path, "synthetic-original");
        check(TextSnapshotFile.ReadRequired(path) == "synthetic-original", "snapshot first publication creates complete bytes");
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            await TextSnapshotFile.WriteAsync(path, "synthetic-replacement");
            using var reader = new StreamReader(input);
            check(reader.ReadToEnd() == "synthetic-original" && TextSnapshotFile.ReadRequired(path) == "synthetic-replacement",
                "replacement preserves an open delete-sharing reader and publishes complete new bytes");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var refused = false;
            try { await TextSnapshotFile.WriteAsync(path, "synthetic-cancelled", cancelled.Token); }
            catch (OperationCanceledException) { refused = true; }
            check(refused && TextSnapshotFile.ReadRequired(path) == "synthetic-replacement", "cancelled write cannot replace committed snapshot");
        }
        if (OperatingSystem.IsWindows())
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var refused = false;
                try { TextSnapshotFile.Write(path, "synthetic-blocked"); }
                catch (IOException) { refused = true; }
                catch (UnauthorizedAccessException) { refused = true; }
                check(refused && TextSnapshotFile.ReadRequired(path) == "synthetic-replacement", "reader without delete sharing refuses publication and retains old bytes");
            }
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            try
            {
                var refused = false;
                try { TextSnapshotFile.Write(path, "synthetic-readonly"); }
                catch (IOException) { refused = true; }
                catch (UnauthorizedAccessException) { refused = true; }
                check(refused && TextSnapshotFile.ReadRequired(path) == "synthetic-replacement", "read-only target refuses replacement without swallowing the failure");
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
        }
        await TextSnapshotFile.WriteAsync(path, "synthetic-recovered");
        check(TextSnapshotFile.ReadRequired(path) == "synthetic-recovered" && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "successful publication after refusal leaves complete state and no partial file");
    }
}
