using System.IO;
using System.Text;

namespace BotAgent.Services;

/// <summary>
/// 应用日志（headless）：同时写 stdout（容器友好，docker logs 可读）与 logs/qqchat.log。
/// 设 QQCHAT_LOG_FILE=0 可只输出到 stdout。
///
/// 除了“写出去”，这里还是**面板日志页唯一的数据源**：
///   • 每行都进一个内存环形缓冲（<see cref="RecentCapacity" /> 行），面板首屏用 /api/logs 拉它；
///   • 启动时用 <see cref="PreloadRecent" /> 从日志文件尾部回填一段 —— 这样**刷新页面 / 重启进程之后
///     日志页也不是空的**（以前日志只活在浏览器内存里：一刷新就“被清空”，号主反馈过）。
///   • 通过 <see cref="LineWritten" /> 把同一份文本推给面板（SSE 实时流）。
/// </summary>
public static class FileLog
{
    private static readonly object Gate = new();

    /// <summary>环形缓冲里最多的行数（面板首屏最多也就拉这么多）。</summary>
    public const int RecentCapacity = 600;

    private static readonly Queue<(long Time, string Text)> Recent = new();

    /// <summary>一条新日志（文本含级别标签、不含时间戳；时间为本地时间毫秒）。面板用它做实时推送。</summary>
    public static event Action<long, string>? LineWritten;

    private static string LogPath => Path.Combine(AppPaths.LogsDir, "qqchat.log");

    /// <summary>当前日志文件路径（健康日报要报它的大小；文件可能不存在）。</summary>
    public static string LogFilePath => LogPath;

    /// <summary>是否同时写文件（托管侧可关闭）。</summary>
    public static bool WriteToFile { get; set; } = true;

    /// <summary>控制台冗余度：Quiet 只输出 Warning/Error 及以上。</summary>
    public static bool Verbose { get; set; } = true;

    public static void Write(string message) => WriteCore(null, message, warn: false);

    public static void Write(string tag, string message) => WriteCore(tag, message, warn: false);

    /// <summary>警告：无论 Verbose 与否都输出到 stderr。</summary>
    public static void Warn(string tag, string message) => WriteCore(tag, message, warn: true);

    /// <summary>最近若干行（新的在后）。面板 /api/logs 用。</summary>
    public static IReadOnlyList<(long Time, string Text)> RecentLines(int limit)
    {
        lock (Gate)
        {
            var take = Math.Clamp(limit, 1, RecentCapacity);
            return Recent.Count <= take ? Recent.ToArray() : Recent.Skip(Recent.Count - take).ToArray();
        }
    }

    /// <summary>
    /// 启动时从日志文件尾部回填环形缓冲（跨重启的历史）。
    /// 读文件最后一段就够（64KB ≈ 几百行），不整份读 —— 日志文件可能很大。
    /// 解析不出来的行（例如被截断的尾行）原样当文本，时间取“现在”。
    /// </summary>
    public static void PreloadRecent(int limit = 300)
    {
        try
        {
            var path = LogPath;
            if (!File.Exists(path))
            {
                return;
            }

            const int tailBytes = 64 * 1024;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - tailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (start > 0)
            {
                // 从中间截断：丢掉第一段（可能是半个字符/半行）
                var firstBreak = text.IndexOf('\n');
                text = firstBreak >= 0 ? text[(firstBreak + 1)..] : string.Empty;
            }

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lock (Gate)
            {
                foreach (var raw in lines.TakeLast(Math.Clamp(limit, 1, RecentCapacity)))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    Recent.Enqueue(ParseLine(line));
                }

                TrimLocked();
            }
        }
        catch
        {
            // 回填失败不影响任何功能（只是首屏少一段历史）
        }
    }

    /// <summary>把文件里的一行拆成（时间毫秒，文本）。解析不了就整行当文本。</summary>
    private static (long Time, string Text) ParseLine(string line)
    {
        // 形如：2026-09-14 23:06:15.683 [Agent] 已启动
        if (line.Length > 24 &&
            DateTime.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff", null,
                System.Globalization.DateTimeStyles.None, out var at))
        {
            return (new DateTimeOffset(at).ToUnixTimeMilliseconds(), line[24..]);
        }

        return (Clock.Now.ToUnixTimeMilliseconds(), line);
    }

    private static void WriteCore(string? tag, string message, bool warn)
    {
        var text = tag is null
            ? message
            : warn ? $"[{tag}] WARN {message}" : $"[{tag}] {message}";
        var now = Clock.Now;
        var line = $"{Clock.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} {text}";

        // 1) stdout/stderr：容器日志（第三方 dalvik 等以 stdout 为准）
        if (warn)
        {
            Console.Error.WriteLine(line);
        }
        else if (Verbose)
        {
            Console.WriteLine(line);
        }

        // 2) 文件：便于事后排查
        if (WriteToFile)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.LogsDir);
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }

        // 3) 内存环形缓冲 + 面板实时推送（都放在文件写入之后：面板挂掉也拖不慢主流程）
        lock (Gate)
        {
            Recent.Enqueue((now.ToUnixTimeMilliseconds(), text));
            TrimLocked();
        }

        try
        {
            LineWritten?.Invoke(now.ToUnixTimeMilliseconds(), text);
        }
        catch
        {
            // UI 推送失败不影响主流程
        }
    }

    private static void TrimLocked()
    {
        while (Recent.Count > RecentCapacity)
        {
            Recent.Dequeue();
        }
    }
}
