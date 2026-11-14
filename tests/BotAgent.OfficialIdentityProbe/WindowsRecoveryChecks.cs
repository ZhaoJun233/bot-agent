using BotAgent.Adapters.Persistence;

internal static class WindowsRecoveryChecks
{
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("windows_recovery_checks_require_windows");
        await test("windows transient writer contention waits then acquires without duplicate ownership", async () =>
        {
            using var directory = new SyntheticDirectory();
            using var original = new OfficialDeliverySnapshotFile(directory.Map);
            original.Commit("synthetic-unchanged-snapshot");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var acquiring = Task.Run(() =>
            {
                entered.SetResult();
                return new OfficialDeliverySnapshotFile(directory.Map);
            });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Task.Delay(50);
                Require(!acquiring.IsCompleted, "writer_acquisition_did_not_wait");
                original.Dispose();
                using var acquired = await acquiring.WaitAsync(TimeSpan.FromSeconds(2));
                Require(acquired.Load() == "synthetic-unchanged-snapshot", "acquisition_changed_snapshot");
            }
            finally
            {
                original.Dispose();
                try { (await acquiring.WaitAsync(TimeSpan.FromSeconds(2))).Dispose(); }
                catch (InvalidOperationException) { /* Preserve the original test failure. */ }
            }
        });
        await test("windows persistent writer contention remains bounded and preserves original ownership", async () =>
        {
            using var directory = new SyntheticDirectory();
            using var original = new OfficialDeliverySnapshotFile(directory.Map);
            original.Commit("synthetic-live-writer");
            var rejected = await Task.Run(() =>
            {
                try { using var unexpected = new OfficialDeliverySnapshotFile(directory.Map); return false; }
                catch (InvalidOperationException error) when (error.Message == "official_delivery_store_unavailable") { return true; }
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Require(rejected && original.Load() == "synthetic-live-writer", "live_writer_was_displaced");
            original.Commit("synthetic-live-writer-still-owns");
            Require(original.Load() == "synthetic-live-writer-still-owns", "original_writer_lost_ownership");
        });
        await test("windows nonsharing lock errors remain unavailable without replacing files", async () =>
        {
            using var directory = new SyntheticDirectory();
            File.WriteAllText(directory.Map, "synthetic-original-bytes");
            Directory.CreateDirectory(directory.Map + ".lock");
            var rejected = await Task.Run(() =>
            {
                try { using var unexpected = new OfficialDeliverySnapshotFile(directory.Map); return false; }
                catch (InvalidOperationException error) when (error.Message == "official_delivery_store_unavailable") { return true; }
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Require(rejected && File.ReadAllText(directory.Map) == "synthetic-original-bytes"
                && Directory.Exists(directory.Map + ".lock"), "nonsharing_failure_changed_files");
        });
    }
}
