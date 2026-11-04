using BotAgent.Panel;

namespace BotAgent.Services;

/// <summary>
/// 把 <see cref="FileLog"/> 的自由文本行投影成 ZBA 日志页的安全摘要。
/// 只输出固定组件名与固定摘要文案；原文（发言、昵称、路径、URL、凭据）一律不进入输出。
/// 无法安全分类的行只给「已隐藏」占位与原字符数。<see cref="Project"/> 是纯函数，
/// 无 IO、无时钟、不读配置，宿主与回归探针共用同一份实现。
/// </summary>
public static class RuntimeLogProjection
{
    /// <summary>环形缓冲上限，也是单页条数上限；与 <see cref="ZbaContract.MaxLogEntries"/> 一致。</summary>
    public const int Capacity = ZbaContract.MaxLogEntries;

    /// <summary>来源名上限，与前端校验的 64 字符一致。</summary>
    public const int MaxSourceLength = 64;

    /// <summary>摘要文案上限，与前端校验的 512 字符一致。</summary>
    public const int MaxMessageLength = 512;

    /// <summary>日志标签到固定安全组件名的白名单；不在表内一律折叠成 runtime。</summary>
    private static readonly Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Agent"] = "agent",
        ["Model"] = "model",
        ["Reply"] = "reply",
        ["Plugin"] = "plugin",
        ["Panel"] = "panel",
        ["Zba"] = "zba",
        ["Settings"] = "settings",
        ["Scheduler"] = "scheduler",
        ["OneBot"] = "onebot",
        ["NapCat"] = "napcat",
        ["Voice"] = "voice",
        ["Memory"] = "memory",
        ["Tools"] = "tools",
        ["Tool"] = "tools",
        ["Storage"] = "storage",
        ["DB"] = "storage",
        ["Database"] = "storage",
        ["Runtime"] = "runtime",
        ["Startup"] = "runtime",
        ["Web"] = "web",
        ["Net"] = "network",
        ["Channel"] = "channel",
        ["Sticker"] = "sticker",
        ["Vision"] = "vision",
        ["Search"] = "search",
        ["WebSearch"] = "search",
        ["Health"] = "health",
        ["Music"] = "music",
        ["Profiles"] = "profiles",
        ["Mood"] = "mood",
        ["Task"] = "task",
        ["Host"] = "host",
        ["Store"] = "storage",
        ["Secrets"] = "settings",
        ["Login"] = "login",
        ["Official"] = "official",
        ["Local"] = "local",
        ["Feishu"] = "feishu",
        ["Links"] = "links",
        ["Mcp"] = "mcp",
        ["ServerAgent"] = "agent",
        ["Platform"] = "platform",
        ["TTS"] = "voice",
        ["部署"] = "deploy"
    };

    /// <summary>
    /// 固定原因码表：命中只输出固定摘要文案，不回显原文；顺序即优先级（错误类在前）。
    /// </summary>
    private static readonly (string Token, string Message, string Level)[] Reasons =
    {
        ("plugin_reload_failed", "插件热应用失败", "error"),
        ("plugin_registry_stopped", "插件注册表已停止", "error"),
        ("plugin_lifecycle_reentry", "插件生命周期重入被拒绝", "error"),
        ("restart_required", "需要重启后才能生效", "warn"),
        ("plugin_reload_pending", "插件热应用待确认", "warn"),
        ("empty_reply", "模型返回空回复", "warn"),
        ("timed out", "调用超时", "warn"),
        ("timeout", "调用超时", "warn"),
        ("unauthorized", "请求被拒绝（未认证）", "warn"),
        ("forbidden", "请求被拒绝（无权限）", "warn"),
        ("connection refused", "上游拒绝连接", "warn"),
        ("启动失败", "启动失败", "error"),
        ("崩溃", "组件异常退出", "error"),
        ("连接失败", "上游连接失败", "warn"),
        ("已断开", "上游连接已断开", "warn"),
        ("重连", "上游连接重连中", "warn"),
        ("已启动", "服务已启动", "info"),
        ("已停止", "服务已停止", "info"),
        ("连接已建立", "上游连接已建立", "info"),
        ("已保存", "配置已保存", "info"),
        ("已应用", "设置已应用", "info"),
        ("已取消", "操作已取消", "info"),
        ("failed", "操作失败", "error"),
        ("error", "运行错误", "error")
    };

    /// <summary>
    /// 输入约定与 <see cref="FileLog.RecentLines"/> 一致：元组为（epoch 毫秒, 已去时间戳的文本），
    /// 新的在数组末尾。输出最新在前，最多 <paramref name="limit"/> 条（再按 <see cref="Capacity"/> 收口）。
    /// </summary>
    public static ZbaLogPage Project(IReadOnlyList<(long Time, string Text)> lines, int limit)
        => Project(lines.Select(line => (line.Time, line.Text, CanClassify: true)).ToArray(), limit);

    public static ZbaLogPage Project(IReadOnlyList<(long Time, string Text, bool CanClassify)> lines, int limit)
    {
        var bounded = Math.Clamp(limit, 1, Capacity);
        var items = new List<ZbaLogEntry>();
        for (var index = lines.Count - 1; index >= 0 && items.Count < bounded; index--)
        {
            var (time, text, canClassify) = lines[index];
            if (string.IsNullOrWhiteSpace(text)) continue;

            var (tag, rest) = canClassify ? SplitTag(text.Trim()) : (Tag: string.Empty, Remainder: text);
            var knownSource = Sources.ContainsKey(tag);
            var marker = knownSource ? LevelMarker(rest) : (Level: (string?)null, Length: 0);
            if (marker.Level is not null) rest = rest[marker.Length..].TrimStart();
            var source = Bound(ResolveSource(tag), MaxSourceLength);

            if (knownSource && TryClassify(rest, out var message, out var level))
            {
                items.Add(new ZbaLogEntry(time, Prefer(marker.Level, level), source,
                    Bound(message, MaxMessageLength), false));
            }
            else
            {
                var placeholder = "已隐藏的日志内容（" +
                    rest.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 字符）";
                items.Add(new ZbaLogEntry(time, marker.Level ?? "info", source,
                    Bound(placeholder, MaxMessageLength), true));
            }
        }

        return new ZbaLogPage(items, Capacity);
    }

    private static (string Tag, string Remainder) SplitTag(string text)
    {
        if (text.Length > 2 && text[0] == '[')
        {
            var close = text.IndexOf(']', 1);
            if (close > 1 && close <= 33)
                return (text[1..close], text[(close + 1)..].TrimStart());
        }

        return (string.Empty, text);
    }

    private static (string? Level, int Length) LevelMarker(string rest)
    {
        if (rest.StartsWith("ERROR ", StringComparison.Ordinal) || rest.StartsWith("FATAL ", StringComparison.Ordinal))
            return ("error", 6);
        if (rest.StartsWith("WARN ", StringComparison.Ordinal) || rest.StartsWith("WARN:", StringComparison.Ordinal))
            return ("warn", 5);
        return (null, 0);
    }

    private static string ResolveSource(string tag)
        => tag.Length > 0 && Sources.TryGetValue(tag, out var source) ? source : "runtime";

    private static bool TryClassify(string rest, out string message, out string level)
    {
        // 自由正文可能包含任意原因码，只识别行首模板或独立原因码，
        // 不把“收到 … plugin_reload_failed”误报成插件故障。
        if (rest.StartsWith("模型输出未通过判定（empty_reply）", StringComparison.Ordinal))
        {
            message = "模型返回空回复";
            level = "warn";
            return true;
        }
        foreach (var (token, text, tokenLevel) in Reasons)
        {
            if (rest.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                && (rest.Length == token.Length || char.IsWhiteSpace(rest[token.Length])
                    || rest[token.Length] is ':' or '：' or '（' or '('))
            {
                message = text;
                level = tokenLevel;
                return true;
            }
        }

        message = string.Empty;
        level = "info";
        return false;
    }

    private static string Prefer(string? marker, string level)
        => marker == "error" ? "error" : marker == "warn" && level == "info" ? "warn" : level;

    private static string Bound(string value, int max)
        => value.Length <= max ? value : value[..max];
}
