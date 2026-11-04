extern alias StorageModule;

using BotAgent.Adapters.Persistence;
using BotAgent.Services;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using StoragePaths = StorageModule::BotAgent.Storage.StoragePaths;
using StorageLogging = StorageModule::BotAgent.Storage.StorageLogging;
using StorageOwnMessages = StorageModule::BotAgent.Adapters.Persistence.OwnMessageStore;

namespace BotAgent.ReviewRemediationProbe;

internal static class StorageMigrationTests
{
    public static int? TryRunChild(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("--storage-context-uninitialized" or "--storage-typed-import")) return null;
        var passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); passed++; }
        string? root = null;
        try
        {
            if (args[0] == "--storage-context-uninitialized")
            {
                ExpectCode(() => _ = StoragePaths.RuntimeRoot, "storage_paths_not_initialized", Check, "root requires host binding");
                ExpectCode(() => _ = StoragePaths.DataDir, "storage_paths_not_initialized", Check, "data path requires host binding");
                ExpectCode(() => _ = StoragePaths.SettingsFile, "storage_paths_not_initialized", Check, "settings path requires host binding");
                var rejected = false;
                try { StoragePaths.Initialize(null!); } catch (ArgumentException) { rejected = true; }
                Check(rejected && !AppDatabase.IsReady, "null root cannot initialize a database");
            }
            else
            {
                root = Path.Combine(Path.GetTempPath(), "botagent-c2-import-" + Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
                Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
                AppDatabase.Initialize();
                var path = AppPaths.SettingsFile;
                File.WriteAllText(path, "{\"verboseLog\":\"not-a-bool\"}");
                LegacyJsonImporter.ImportIfNeeded();
                var settings = new SettingsStore();
                Check(!settings.HasStoredSettings(), "wrong settings type never commits");
                Check(File.Exists(path) && !AppDatabase.HasMeta("legacy_import_done"), "wrong type remains retryable without done marker");
                File.WriteAllText(path, "{\"VerboseLog\":true}");
                LegacyJsonImporter.ImportIfNeeded();
                Check(settings.HasStoredSettings() && settings.Load().VerboseLog, "original typed settings import remains usable");
                Check(!File.Exists(path) && File.Exists(Path.Combine(root, "legacy-json", "data", "settings.json")), "successful settings source is archived");
                settings.Save(new AppSettings { VerboseLog = false });
                LegacyJsonImporter.ImportIfNeeded();
                Check(!settings.Load().VerboseLog, "repeated import cannot overwrite a later save");
            }
            Console.WriteLine($"C2B_CHILD {args[0]} passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"C2B_CHILD {args[0]} passed={passed} failed=1 type={ex.GetType().Name}"); return 1; }
        finally
        {
            if (root is not null)
            {
                SqliteConnection.ClearAllPools();
                var full = Path.GetFullPath(root);
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("botagent-c2-import-", StringComparison.Ordinal))
                    throw new InvalidOperationException("unsafe_storage_import_cleanup");
                Directory.Delete(full, recursive: true);
            }
        }
    }

    public static async Task BindingAndImportAsync(Action<bool, string> check)
    {
        check(StoragePaths.RuntimeRoot == AppPaths.RuntimeRoot && StoragePaths.DataDir == AppPaths.DataDir
            && StoragePaths.SettingsFile == AppPaths.SettingsFile, "Storage paths match the actual Host path policy");
        StoragePaths.Initialize(Path.Combine(AppPaths.RuntimeRoot, "."));
        check(StoragePaths.RuntimeRoot == AppPaths.RuntimeRoot, "same canonical runtime root is idempotent");
        ExpectCode(() => StoragePaths.Initialize(Path.Combine(AppPaths.RuntimeRoot, "different-root")),
            "storage_paths_already_initialized", check, "another runtime root is rejected");
        check(Delegate.Equals(StorageLogging.WriteHandler, (Action<string, string>)FileLog.Write)
            && Delegate.Equals(StorageLogging.WarnHandler, (Action<string, string>)FileLog.Warn), "Storage uses the actual Host log sink");
        await ChildAsync("--storage-context-uninitialized", "passed=4 failed=0", check);
        await ChildAsync("--storage-typed-import", "passed=5 failed=0", check);
    }

    private static async Task ChildAsync(string mode, string counts, Action<bool, string> check)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(StorageMigrationTests).Assembly.Location);
        start.ArgumentList.Add(mode);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("storage_child_start");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        var text = await output;
        // Never forward import logs; only the fixed child result line is printed.
        foreach (var line in text.Split('\n').Where(l => l.StartsWith("C2B_CHILD", StringComparison.Ordinal))) Console.WriteLine(line.Trim());
        check(child.ExitCode == 0 && text.Contains(counts), "isolated child " + mode + " passes");
        _ = await errors;
    }

    private static void ExpectCode(Action action, string code, Action<bool, string> check, string label)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message == code) { check(true, label); return; }
        check(false, label);
    }

    public static Task EpisodeAsync(Action<bool, string> check)
    {
        foreach (var type in new[] { typeof(AgentImageStore), typeof(AgentSessionStore), typeof(AudioCache),
            typeof(ConversationStore), typeof(EpisodeStore), typeof(HostMetrics), typeof(JargonStore),
            typeof(MemberProfileStore), typeof(MemberRoleStore), typeof(MoodStore), typeof(MusicStore),
            typeof(OwnMessageStore), typeof(PromptTemplateStore), typeof(SecretsStore), typeof(StickerStore), typeof(TenantQuotaStore) })
        {
            var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(m => m.DeclaringType != typeof(object)).ToArray();
            check(methods.Length > 0 && methods.All(m => m.DeclaringType!.Assembly.GetName().Name == "BotAgent.Storage"),
                "host " + type.Name + " executes the actual Storage implementation");
        }
        return Task.CompletedTask;
    }

    public static Task BoundRootAsync(Action<bool, string> check)
    {
        var path = Path.Combine(AppPaths.DataDir, "own-messages.json");
        File.WriteAllText(path, "[{\"id\":10004,\"text\":\"synthetic bound-root import\",\"at\":\"1970-01-01T00:00:00Z\"}]");
        var oldBot = Environment.GetEnvironmentVariable("BOTAGENT_DATA_DIR");
        var oldQq = Environment.GetEnvironmentVariable("QQCHAT_DATA_DIR");
        try
        {
            var alternate = Path.Combine(AppPaths.RuntimeRoot, "c2-b-alternate");
            Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", alternate);
            Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", alternate);
            var rows = new StorageOwnMessages().LoadRecent();
            check(rows.Count == 1 && rows[0].Text == "synthetic bound-root import",
                "Storage legacy import reads the bound Host root despite changed environment aliases");
            check(!File.Exists(path) && File.Exists(Path.Combine(AppPaths.RuntimeRoot, "legacy-json", "data", "own-messages.json")),
                "Storage archives only under the bound Host root");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", oldBot);
            Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", oldQq);
        }
        return Task.CompletedTask;
    }
}
