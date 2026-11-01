using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using BotAgent.Domain.Ports;
using BotAgent.Panel;
using BotAgent.Services;
using BotAgent.Services.Ops;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// 运维水位（#121）：数据库/日志目录多大、最近一次热备是什么时候、进程 RSS 与内存上限。
///
/// 为什么放在面板适配层：读目录大小、读进程 RSS 都是**文件系统 / 进程 API**，
/// 属于 IO 实现细节；用例层只该看到“备份是否过期”这个结论。
/// 这里同时给两处用：旧面板 <c>/api/status</c> 的 <c>facts</c> 段，以及 ZBA status 的
/// <c>backup</c> 组件（面板顶部的红条就看那个组件）。
/// </summary>
internal static class PanelStatusFacts
{
    /// <summary>超过这么多天没成功备份，就算过期（与健康日报的告警口径一致）。</summary>
    private const double StaleDays = 2;

    /// <summary>旧面板 <c>/api/status</c> 的 <c>facts</c> 段（拿不到的部分为 null，前端按“未知”显示）。</summary>
    public static JsonObject Build(
        DatabaseBackupService? backups,
        IHostFacts? host,
        Func<string> databasePath,
        DateTimeOffset now)
    {
        var facts = new JsonObject();
        try
        {
            var path = databasePath();
            var info = new FileInfo(path);
            facts["database"] = new JsonObject
            {
                ["path"] = path,
                ["bytes"] = info.Exists ? info.Length : null
            };
            facts["logs"] = new JsonObject { ["bytes"] = DirectoryBytes(AppPaths.LogsDir) };
            facts["backups"] = new JsonObject
            {
                ["lastSuccessAt"] = backups?.LastSuccessAt?.ToString("O"),
                ["lastError"] = backups?.LastError,
                ["stale"] = IsStale(backups, now),
                ["running"] = backups?.IsRunning ?? false
            };
            if (host is not null)
            {
                var memory = new JsonObject();
                if (host.MemoryLimitBytes() is { } limit)
                {
                    var rss = Process.GetCurrentProcess().WorkingSet64;
                    memory["rssBytes"] = rss;
                    memory["limitBytes"] = limit;
                    memory["usedPercent"] = limit > 0 ? (int)Math.Round(rss * 100.0 / limit) : null;
                }

                if (host.SwapBytes() is { } swap)
                {
                    memory["swapUsedBytes"] = swap.Used;
                    memory["swapTotalBytes"] = swap.Total;
                }

                if (memory.Count > 0)
                {
                    facts["memory"] = memory;
                }
            }

            var startups = ReadStartups();
            facts["process"] = new JsonObject
            {
                ["startedAt"] = host?.ProcessStartTime()?.ToString("O"),
                ["uptimeSeconds"] = host?.ProcessStartTime() is { } started
                    ? (long)(now - started).TotalSeconds
                    : null,
                // 容器重启次数 = 启动次数 - 1：健康日报里“为什么今天特别慢”常常就是这个数在涨。
                ["startups"] = startups,
                ["restarts"] = startups > 0 ? startups - 1 : 0
            };
        }
        catch (Exception exception)
        {
            facts["error"] = exception.Message;
        }

        return facts;
    }

    /// <summary>ZBA status 里的 <c>backup</c> 组件：面板顶部红条靠它判断。</summary>
    public static ZbaComponent? BuildBackupComponent(DatabaseBackupService? backups, DateTimeOffset now)
    {
        if (backups is null)
        {
            return null;
        }

        var last = backups.LastSuccessAt;
        var failure = backups.LastError;
        var stale = IsStale(backups, now);
        var state = failure is not null ? "faulted" : stale ? "stale" : "healthy";
        var reason = failure
            ?? (last is null
                ? "还没成功备份过"
                : stale ? $"备份已 {(int)(now - last.Value).TotalDays} 天没跑" : null);
        return new ZbaComponent("backup", "backup", "database-backup", state, state == "healthy", reason, last);
    }

    /// <summary>宿主进程累计启动次数（meta 表；读不到 = 0）。</summary>
    private static long ReadStartups()
    {
        var raw = BotAgent.Adapters.Persistence.AppDatabase.GetMeta(
            BotAgent.Adapters.Persistence.AppDatabase.StartupCountMetaKey);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
    }

    private static bool IsStale(DatabaseBackupService? backups, DateTimeOffset now)
    {
        if (backups is null)
        {
            return false;
        }

        var last = backups.LastSuccessAt;
        return last is null || (now - last.Value).TotalDays >= StaleDays;
    }

    /// <summary>目录占用（递归；目录不存在 = 0）。</summary>
    private static long DirectoryBytes(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
            {
                return 0;
            }

            var total = 0L;
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                total += file.Length;
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

}
