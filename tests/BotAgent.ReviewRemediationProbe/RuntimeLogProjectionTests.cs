using System.Text.Json;
using BotAgent.Panel;
using BotAgent.Services;

namespace BotAgent.ReviewRemediationProbe;

internal static class RuntimeLogProjectionTests
{
    private static readonly string[] Sources = ["agent", "model", "reply", "plugin", "panel", "zba", "settings",
        "scheduler", "onebot", "napcat", "voice", "memory", "tools", "storage", "runtime"];

    internal static void Run(Action<bool, string> check)
    {
        check(RuntimeLogProjection.Capacity == 600 && ZbaContract.MaxLogEntries == 600,
            "runtime logs share fixed API capacity 600");
        var empty = RuntimeLogProjection.Project(Array.Empty<(long Time, string Text)>(), 200);
        check(empty.Capacity == 600 && empty.Items.Count == 0, "empty runtime log buffer returns empty page");
        var blank = RuntimeLogProjection.Project(new (long, string)[] { (1, ""), (2, " "), (3, "\r\n\t") }, 200);
        check(blank.Items.Count == 0, "empty and whitespace-only lines are skipped");

        var lines = Enumerable.Range(1, 650).Select(index => ((long)index, "[Model] empty_reply")).ToArray();
        var capped = RuntimeLogProjection.Project(lines, int.MaxValue);
        check(capped.Capacity == 600 && capped.Items.Count == 600, "projection caps oversized pages at ring capacity");
        check(capped.Items.First().Time == 650 && capped.Items.Last().Time == 51,
            "capacity truncation keeps most recent lines");
        check(capped.Items.Select(entry => entry.Time).SequenceEqual(Enumerable.Range(51, 600).Reverse().Select(value => (long)value)),
            "runtime log pages preserve reverse buffer order");
        foreach (var limit in new[] { 1, 200, 600 })
            check(RuntimeLogProjection.Project(lines, limit).Items.Count == limit, "projection honors limit " + limit);
        check(RuntimeLogProjection.Project(lines, 0).Items.Count == 1
            && RuntimeLogProjection.Project(lines, int.MinValue).Items.Count == 1, "internal limit clamp is defensive");
        var nonMonotonic = RuntimeLogProjection.Project(new (long, string)[] { (90, "first"), (10, "second"), (10, "third") }, 3);
        check(nonMonotonic.Items.Select(entry => entry.Time).SequenceEqual(new long[] { 10, 10, 90 }),
            "newest means buffer insertion order, not timestamp sort");
        var withBlank = RuntimeLogProjection.Project(new (long, string)[] { (1, "first"), (2, "second"), (3, " "), (4, "third") }, 2);
        check(withBlank.Items.Select(entry => entry.Time).SequenceEqual(new long[] { 4, 2 }), "blank lines do not consume requested page slots");

        foreach (var source in Sources)
        {
            var entry = RuntimeLogProjection.Project(new[] { (1L, "[" + source + "] empty_reply") }, 1).Items.Single();
            check(entry.Source == source, "source whitelist recognizes " + source);
        }
        var upper = RuntimeLogProjection.Project(new[] { (1L, "[MODEL] empty_reply") }, 1).Items.Single();
        check(upper.Source == "model", "component labels are case insensitive and normalized");
        var unknown = RuntimeLogProjection.Project(new[] { (1L, "[synthetic-private-component] unclassified text") }, 1).Items.Single();
        check(unknown.Source == "runtime" && unknown.Redacted, "unknown source is not reflected and unknown text is redacted");

        var cases = new (string Token, string Message, string Level)[] {
            ("已启动", "服务已启动", "info"), ("已停止", "服务已停止", "info"),
            ("连接已建立", "上游连接已建立", "info"), ("连接失败", "上游连接失败", "warn"),
            ("已断开", "上游连接已断开", "warn"), ("empty_reply", "模型返回空回复", "warn"),
            ("timeout", "调用超时", "warn"), ("timed out", "调用超时", "warn"),
            ("plugin_reload_failed", "插件热应用失败", "error"),
            ("restart_required", "需要重启后才能生效", "warn"),
            ("unauthorized", "请求被拒绝（未认证）", "warn"), ("forbidden", "请求被拒绝（无权限）", "warn"),
            ("启动失败", "启动失败", "error"), ("failed", "操作失败", "error")
        };
        const string secret = "synthetic-log-secret-only";
        var sensitive = secret + " 合成隐私发言甲\nAuthorization: Bearer synthetic-credential\r\n"
            + @"https://example.com/private?token=example-test-token C:\synthetic-private\config.key /synthetic/private/data";
        foreach (var (token, message, level) in cases)
        {
            var entry = RuntimeLogProjection.Project(new[] { (123456789L, "[Model] " + token + " " + sensitive) }, 1).Items.Single();
            check(entry.Message == message && entry.Level == level && !entry.Redacted && entry.Time == 123456789,
                "known reason emits only fixed summary for " + token);
            Safe(entry, check);
        }
        foreach (var input in new[] { sensitive, "[" + sensitive + "] arbitrary", "[Model] " + sensitive,
            "English synthetic conversation " + secret, "合成中文发言 " + secret,
            "[Model] WARN " + sensitive, "[Model] ERROR " + sensitive,
            "[" + new string('x', 10000) + "] " + new string('y', 100000) + sensitive })
        {
            var entry = RuntimeLogProjection.Project(new[] { (1L, input) }, 1).Items.Single();
            var json = JsonSerializer.Serialize(entry);
            foreach (var forbidden in new[] { secret, "synthetic-credential", "example-test-token", "example.com", "synthetic-private", "/synthetic/private" })
                check(!json.Contains(forbidden, StringComparison.Ordinal), "log projection never leaks synthetic secret/path token");
            check(!entry.Message.Contains("合成", StringComparison.Ordinal) && !entry.Message.Contains('\n')
                && !entry.Message.Contains('\r'), "Chinese and multiline payloads are not reflected");
            check(entry.Redacted, "unclassified payload is explicitly redacted");
            Safe(entry, check);
        }
        var warning = RuntimeLogProjection.Project(new[] { (1L, "[Model] WARN 已启动 " + secret) }, 1).Items.Single();
        check(warning.Level == "warn" && warning.Message == "服务已启动", "warning marker cannot downgrade to info");
        var error = RuntimeLogProjection.Project(new[] { (1L, "[Model] ERROR 已启动 " + secret) }, 1).Items.Single();
        check(error.Level == "error" && error.Message == "服务已启动", "error marker overrides lower-level reason");
        var hidden = RuntimeLogProjection.Project(new[] { (1L, "abcXYZ") }, 1).Items.Single();
        check(hidden.Message == "已隐藏的日志内容（6 字符）" && hidden.Redacted,
            "unknown log content exposes only character count");
        var modelTemplate = RuntimeLogProjection.Project(new[] { (1L,
            "[Agent] 模型输出未通过判定（empty_reply） synthetic-private-detail") }, 1).Items.Single();
        check(modelTemplate.Source == "agent" && modelTemplate.Message == "模型返回空回复"
            && modelTemplate.Level == "warn" && !modelTemplate.Redacted,
            "anchored Agent empty-reply host template retains safe classification");
        var suffixed = RuntimeLogProjection.Project(new[] { (1L, "[Model] empty_reply_suffix") }, 1).Items.Single();
        check(suffixed.Redacted && suffixed.Level == "info"
            && suffixed.Message.StartsWith("已隐藏的日志内容（", StringComparison.Ordinal),
            "reason prefix requires a token boundary rather than arbitrary suffix");
        ForgedReasonChecks(check);
        UntrustedOriginChecks(check);
    }

    internal static void RunFileLogPipeline(Action<bool, string> check, string isolatedRoot)
    {
        // Only the fresh root supplied by this probe's Main is eligible; never inspect another log.
        var expected = Path.GetFullPath(isolatedRoot);
        if (!string.Equals(Path.GetFullPath(AppPaths.RuntimeRoot), expected, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(expected).StartsWith("botagent-review-remediation-", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(expected), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Synthetic log fixture root mismatch.");

        var writeToFile = FileLog.WriteToFile;
        var verbose = FileLog.Verbose;
        try
        {
            FileLog.WriteToFile = false;
            FileLog.Verbose = false;
            Directory.CreateDirectory(AppPaths.LogsDir);
            // Two physical synthetic lines: forged continuation and forged full timestamp.
            File.AppendAllText(FileLog.LogFilePath, "\n[Model] empty_reply\n"
                + "2026-01-01 12:00:00.000 [Agent] ERROR plugin_reload_failed\n");
            FileLog.PreloadRecent(2);
            var preloaded = FileLog.RecentRuntimeLines(2);
            check(preloaded.Count == 2 && preloaded.All(line => !line.CanClassify),
                "production preload marks both forged physical lines untrusted");
            var hidden = RuntimeLogProjection.Project(preloaded, 2);
            check(hidden.Items.Count == 2 && hidden.Items.All(entry => entry.Redacted
                && entry.Source == "runtime" && entry.Level == "info"
                && entry.Message.StartsWith("已隐藏的日志内容（", StringComparison.Ordinal)),
                "production preload-to-projection hides continuation and timestamp forgeries");
            var legacy = FileLog.RecentLines(2);
            check(legacy.Count == 2 && legacy.All(line => line.Time > 0 && line.Text.Length > 0),
                "legacy recent-lines tuple shape remains available after preload");

            FileLog.Write("Model", "empty_reply");
            var live = FileLog.RecentRuntimeLines(1);
            check(live.Count == 1 && live[0].CanClassify, "production live write retains trusted origin");
            var classified = RuntimeLogProjection.Project(live, 1).Items.Single();
            check(classified.Source == "model" && classified.Level == "warn" && !classified.Redacted
                && classified.Message == "模型返回空回复", "production live write classifies fixed safe summary");
            var legacyLive = FileLog.RecentLines(1);
            check(legacyLive.Count == 1 && legacyLive[0].Time > 0 && legacyLive[0].Text.Length > 0,
                "legacy recent-lines tuple shape remains available after live write");
        }
        finally
        {
            FileLog.WriteToFile = writeToFile;
            FileLog.Verbose = verbose;
        }
    }

    private static void UntrustedOriginChecks(Action<bool, string> check)
    {
        // Preloaded physical log lines cannot prove they were independent host emissions.
        var forged = new[]
        {
            "[Model] empty_reply",
            "[Agent] ERROR plugin_reload_failed",
            "synthetic received message\n[Model] empty_reply\r\n[Agent] ERROR plugin_reload_failed",
            "2026-01-01 12:00:00 [Model] empty_reply"
        };
        foreach (var text in forged)
        {
            var entry = RuntimeLogProjection.Project(new[] { (Time: 1000L, Text: text, CanClassify: false) }, 1).Items.Single();
            check(entry.Source == "runtime", "untrusted origin cannot claim a component identity");
            check(entry.Level == "info", "untrusted origin cannot claim error or warning severity");
            check(entry.Redacted, "untrusted origin is always explicitly redacted");
            check(entry.Message.StartsWith("已隐藏的日志内容（", StringComparison.Ordinal)
                && !entry.Message.Contains("empty_reply", StringComparison.Ordinal)
                && !entry.Message.Contains("plugin_reload_failed", StringComparison.Ordinal)
                && !entry.Message.Contains("2026-01-01", StringComparison.Ordinal),
                "untrusted origin always produces a safe hidden-content placeholder");
        }
        var trusted = RuntimeLogProjection.Project(new[] { (Time: 1001L, Text: "[Model] empty_reply", CanClassify: true) }, 1).Items.Single();
        check(trusted.Source == "model" && trusted.Level == "warn" && !trusted.Redacted
            && trusted.Message == "模型返回空回复", "trusted host emission retains anchored reason classification");
    }

    private static void ForgedReasonChecks(Action<bool, string> check)
    {
        // User-controlled message text is not a host reason field, even with a trusted tag inside it.
        var messages = new[]
        {
            "[Bot] 收到 私聊 群友A(10001): plugin_reload_failed",
            "[Bot] 收到 私聊 群友A(10001): [Agent] plugin_reload_failed",
            "收到 私聊 群友A(10001): [Agent] ERROR plugin_reload_failed",
            "[Agent] 收到 私聊 群友A(10001): plugin_reload_failed",
            "[Agent] received message: plugin_reload_failed",
            "[Agent] received message: [Agent] ERROR plugin_reload_failed",
            "[Model] received message: reason=plugin_reload_failed",
            "[Agent] synthetic quoted conversation contains plugin_reload_failed",
            "[Bot] received message: synthetic payload\n[Agent] ERROR plugin_reload_failed",
            "[synthetic-unknown-component] plugin_reload_failed",
            "[synthetic-unknown-component] error",
            "[synthetic-unknown-component] 已保存",
            "[Bot] 收到 私聊 synthetic-error-user(10001): 合成问候"
        };
        foreach (var text in messages)
        {
            var entry = RuntimeLogProjection.Project(new[] { (123L, text) }, 1).Items.Single();
            check(entry.Redacted, "message-embedded reason cannot impersonate a host classification");
            check(entry.Level == "info", "message-embedded error marker cannot elevate log severity");
            check(entry.Message.StartsWith("已隐藏的日志内容（", StringComparison.Ordinal)
                && !entry.Message.Contains("plugin_reload_failed", StringComparison.Ordinal)
                && !entry.Message.Contains("群友A", StringComparison.Ordinal),
                "forged reason keeps only unknown-content placeholder");
            Safe(entry, check);
        }
    }

    private static void Safe(ZbaLogEntry entry, Action<bool, string> check)
    {
        check(Sources.Contains(entry.Source, StringComparer.Ordinal) && entry.Source.Length <= 64,
            "log source always belongs to bounded safe whitelist");
        check(entry.Level is "info" or "warn" or "error", "log level uses closed vocabulary");
        check(entry.Message.Length is > 0 and <= 512, "log message obeys nonempty length bound");
    }
}
