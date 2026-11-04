using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace BotAgent.Services;

/// <summary>
/// 日志轮转与归档清理的纯逻辑。抽出来是为了能被探针直接验证 —— 否则「写满 10 MiB 那天会发生什么」
/// 只能靠线上运气。这里只碰 <see cref="FileInfo" /> / <see cref="DirectoryInfo" /> 的实例 API，
/// 不新增 <c>File.*</c> 静态调用（R3 的直接文件 IO 计数保持原样）。
/// </summary>
public static class LogRotation
{
    /// <summary>单个活跃文件的上限：写满就滚（10 MiB）。</summary>
    public const long DefaultMaxBytes = 10L * 1024 * 1024;

    /// <summary>归档保留天数。日志里有群聊正文，上限必须能用一句话说清（见 AGENTS.md 第 1 节）。</summary>
    public const int DefaultRetentionDays = 7;

    /// <summary>活跃文件名。</summary>
    public const string ActiveFileName = "qqchat.log";

    /// <summary>该不该滚：跨天或写满都滚。</summary>
    public static bool ShouldRoll(DateTime lastWriteLocal, long activeBytes, DateOnly today, long maxBytes) =>
        DateOnly.FromDateTime(lastWriteLocal) != today || activeBytes >= maxBytes;

    /// <summary>归档基本名（压缩前）：qqchat-YYYY-MM-DD.log。</summary>
    public static string ArchiveName(DateOnly day) =>
        "qqchat-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";

    /// <summary>
    /// 把活跃文件挪成归档并压缩：先改名（同目录内 rename，不复制内容），再 gzip，最后删明文。
    /// 返回归档路径（正常是 .gz；压缩失败时保留 .log）；没有可滚的内容返回 null。
    /// </summary>
    public static string? Roll(string logsDirectory, DateOnly day, string activeFileName = ActiveFileName)
    {
        var info = new FileInfo(Path.Combine(logsDirectory, activeFileName));
        if (!info.Exists || info.Length == 0)
        {
            return null;
        }

        var plain = UniqueArchivePath(logsDirectory, ArchiveName(day));
        info.MoveTo(plain);
        var gzipPath = plain + ".gz";
        try
        {
            Compress(plain, gzipPath);
            return gzipPath;
        }
        catch
        {
            // 压缩失败：删掉半截 .gz，保留明文归档 —— 宁可多占盘，也不丢日志。
            try
            {
                var partial = new FileInfo(gzipPath);
                if (partial.Exists)
                {
                    partial.Delete();
                }
            }
            catch
            {
                // 连清理都失败就算了，返回明文路径
            }

            return plain;
        }
    }

    /// <summary>删掉最后写入时间早于 now - retentionDays 的归档；返回删掉几个。活跃文件不匹配这个模式，不会被误删。</summary>
    public static int Sweep(string logsDirectory, DateTimeOffset now, int retentionDays)
    {
        var directory = new DirectoryInfo(logsDirectory);
        if (!directory.Exists)
        {
            return 0;
        }

        var cutoff = now.UtcDateTime.AddDays(-retentionDays);
        var removed = 0;
        foreach (var file in directory.GetFiles("qqchat-*.log*", SearchOption.TopDirectoryOnly))
        {
            if (file.LastWriteTimeUtc >= cutoff)
            {
                continue;
            }

            try
            {
                file.Delete();
                removed++;
            }
            catch
            {
                // 单个文件删不掉不影响其它归档
            }
        }

        return removed;
    }

    private static void Compress(string plainPath, string gzipPath)
    {
        using (var source = new FileInfo(plainPath).OpenRead())
        using (var target = new FileInfo(gzipPath).Create())
        using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize))
        {
            source.CopyTo(gzip);
        }

        new FileInfo(plainPath).Delete();
    }

    /// <summary>同一天滚多次时避免覆盖：先看 .log，再看 .log.gz（滚过的只剩 gz）。</summary>
    private static string UniqueArchivePath(string logsDirectory, string baseName)
    {
        var stem = baseName[..^".log".Length];
        for (var index = 1; index < 1000; index++)
        {
            var name = index == 1 ? baseName : stem + "-" + index.ToString(CultureInfo.InvariantCulture) + ".log";
            var candidate = Path.Combine(logsDirectory, name);
            if (!new FileInfo(candidate).Exists && !new FileInfo(candidate + ".gz").Exists)
            {
                return candidate;
            }
        }

        return Path.Combine(logsDirectory, stem + "-" + Guid.NewGuid().ToString("N") + ".log");
    }
}
