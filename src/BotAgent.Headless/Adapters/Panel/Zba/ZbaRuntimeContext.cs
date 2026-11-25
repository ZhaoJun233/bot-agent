using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Panel;
using BotAgent.Services;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>Process identity and recovery stores have no dependency on task or conversation construction.</summary>
internal sealed record ZbaRuntimeContext(
    ZbaOptions Options, string Version, string InstanceId, DateTimeOffset StartedAt,
    ZbaEventBuffer Events, ZbaTaskReceiptStore TaskReceipts)
{
    internal static ZbaRuntimeContext Create(Func<DateTimeOffset> now, bool hadStoredConfigurationBeforeStartup)
    {
        var version = typeof(ZbaRuntimeContext).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
        var instanceId = "zi-" + now().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                         + "-" + Guid.NewGuid().ToString("N")[..8];
        Action<string> log = message => FileLog.Write("Zba", message);
        var events = new ZbaEventBuffer(new ZbaEventSequenceStore(
            Path.Combine(AppPaths.DataDir, "zba-event-sequence"), now, log), hadStoredConfigurationBeforeStartup);
        var receipts = new ZbaTaskReceiptStore(Path.Combine(AppPaths.DataDir, "zba-task-receipts.json"), now, log);
        return new(FromEnvironment(), version, instanceId, now(), events, receipts);
    }

    private static ZbaOptions FromEnvironment()
    {
        var admin = Clean(EnvironmentAliases.Get("BOTAGENT_ZBA_TOKEN", "QQCHAT_ZBA_TOKEN"));
        var read = Clean(EnvironmentAliases.Get("BOTAGENT_ZBA_READ_TOKEN", "QQCHAT_ZBA_READ_TOKEN"));
        var allowLoopback = EnvironmentAliases.Get("BOTAGENT_ZBA_ALLOW_LOOPBACK", "QQCHAT_ZBA_ALLOW_LOOPBACK")
            ?.Trim() is "1" or "true" or "TRUE" or "True";
        FileLog.Write("Zba", admin is null && read is null && !allowLoopback
            ? "[Zba] 未配置 bearer；管理端点 fail-closed（503），仅 /zba/v1/health 可读"
            : "[Zba] v1 已挂载（bearer；写路径需要 admin）");
        return new ZbaOptions { AdminToken = admin, ReadToken = read, AllowLoopbackWithoutToken = allowLoopback };
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
