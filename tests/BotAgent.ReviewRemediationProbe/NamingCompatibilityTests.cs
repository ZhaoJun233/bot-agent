extern alias StorageModule;

using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Rendering;
using StoragePaths = StorageModule::BotAgent.Storage.StoragePaths;
using StorageOptions = StorageModule::BotAgent.Storage.StorageOptions;

namespace BotAgent.ReviewRemediationProbe;

/// <summary>N1 naming compatibility: canonical botagent.db first, qqchat.db fallback; env alias precedence.</summary>
internal static class NamingCompatibilityTests
{
    internal static void RunAsync(Action<bool, string> check)
    {
        check(AppDatabase.FilePath.EndsWith(StoragePaths.CanonicalDatabaseFileName, StringComparison.OrdinalIgnoreCase),
            $"isolated root uses {StoragePaths.CanonicalDatabaseFileName}");
        check(new StorageOptions().DbPath == StoragePaths.CanonicalDatabaseFileName,
            "StorageOptions default is canonical");

        var root = Path.Combine(Path.GetTempPath(), "botagent-naming-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            check(AppDatabase.ResolveDatabasePath(root).EndsWith(StoragePaths.CanonicalDatabaseFileName, StringComparison.OrdinalIgnoreCase),
                "empty root resolves canonical");
            File.WriteAllText(Path.Combine(data, StoragePaths.LegacyDatabaseFileName), string.Empty);
            check(AppDatabase.ResolveDatabasePath(root).EndsWith(StoragePaths.LegacyDatabaseFileName, StringComparison.OrdinalIgnoreCase),
                "legacy-only root resolves legacy");
            File.WriteAllText(Path.Combine(data, StoragePaths.CanonicalDatabaseFileName), string.Empty);
            check(AppDatabase.ResolveDatabasePath(root).EndsWith(StoragePaths.CanonicalDatabaseFileName, StringComparison.OrdinalIgnoreCase),
                "both files prefer canonical");
            File.Delete(Path.Combine(data, StoragePaths.LegacyDatabaseFileName));
            check(AppDatabase.ResolveDatabasePath(root).EndsWith(StoragePaths.CanonicalDatabaseFileName, StringComparison.OrdinalIgnoreCase),
                "canonical-only root resolves canonical");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        var canonical = "BOTAGENT_SYNTHETIC_ALIAS_" + Guid.NewGuid().ToString("N");
        var legacy = "QQCHAT_SYNTHETIC_ALIAS_" + Guid.NewGuid().ToString("N");
        var oldCanonical = Environment.GetEnvironmentVariable(canonical);
        var oldLegacy = Environment.GetEnvironmentVariable(legacy);
        try
        {
            Environment.SetEnvironmentVariable(canonical, null);
            Environment.SetEnvironmentVariable(legacy, null);
            check(EnvironmentAliases.Get(canonical, legacy) is null, "empty alias resolves null");
            Environment.SetEnvironmentVariable(legacy, "legacy-value");
            check(EnvironmentAliases.Get(canonical, legacy) == "legacy-value", "legacy aliases remain supported");
            Environment.SetEnvironmentVariable(canonical, "canonical-value");
            check(EnvironmentAliases.Get(canonical, legacy) == "canonical-value", "canonical alias wins");
        }
        finally
        {
            Environment.SetEnvironmentVariable(canonical, oldCanonical);
            Environment.SetEnvironmentVariable(legacy, oldLegacy);
        }

        var botTimeout = "BOTAGENT_MODEL_TIMEOUT_SECONDS";
        var qqTimeout = "QQCHAT_MODEL_TIMEOUT_SECONDS";
        var oldBotTimeout = Environment.GetEnvironmentVariable(botTimeout);
        var oldQqTimeout = Environment.GetEnvironmentVariable(qqTimeout);
        try
        {
            Environment.SetEnvironmentVariable(botTimeout, "10");
            Environment.SetEnvironmentVariable(qqTimeout, "20");
            check(BotAgent.Model.OpenAiClient.ModelTimeout() == TimeSpan.FromSeconds(10),
                "canonical model timeout wins");
            Environment.SetEnvironmentVariable(botTimeout, null);
            check(BotAgent.Model.OpenAiClient.ModelTimeout() == TimeSpan.FromSeconds(20),
                "legacy model timeout remains supported");
        }
        finally
        {
            Environment.SetEnvironmentVariable(botTimeout, oldBotTimeout);
            Environment.SetEnvironmentVariable(qqTimeout, oldQqTimeout);
        }

        check(ReplyAuditRules.Judge("../../botagent.db", allowLocalPaths: false) == ReplyAuditVerdict.BlockLocalPath,
            "canonical database path is blocked by outbound audit");
        check(ReplyAuditRules.Judge("../../qqchat.db", allowLocalPaths: false) == ReplyAuditVerdict.BlockLocalPath,
            "legacy database path remains blocked by outbound audit");
    }
}
