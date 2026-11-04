namespace BotAgent.Panel;

internal sealed class ZbaQuery
{
    private readonly Dictionary<string, string> _values;

    private ZbaQuery(Dictionary<string, string> values) => _values = values;

    internal static ZbaQuery Parse(string raw)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = raw.StartsWith('?') ? raw[1..] : raw;
        if (text.Length == 0) return new ZbaQuery(values);
        if (text.Length > 2048) throw new ZbaBadRequestException("invalid_query");

        foreach (var pair in text.Split('&'))
        {
            if (pair.Length == 0) continue;
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            key = Decode(key);
            value = Decode(value);
            // cursor 是后端签发的不透明长令牌（能力目录 = 签名游标，会话/任务 = 序号），
            // 它的长度上限写在 GetCursor()；其余参数仍然只允许 256 个字符。
            var limit = key is "cursor" ? CursorLimit : 256;
            if (key.Length is 0 or > 32 || value.Length > limit)
                throw new ZbaBadRequestException("invalid_query");
            if (!values.TryAdd(key, value))
                throw new ZbaBadRequestException("duplicate_query_parameter");
        }

        return new ZbaQuery(values);
    }

    internal void AllowOnly(params string[] names)
    {
        foreach (var key in _values.Keys)
            if (!names.Contains(key, StringComparer.Ordinal))
                throw new ZbaBadRequestException("unknown_query_parameter");
    }

    internal string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    internal int GetInt(string name, int defaultValue, int min, int max)
    {
        var raw = Get(name);
        if (raw is null) return defaultValue;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ZbaBadRequestException("invalid_" + name);
        return value;
    }

    internal long GetLong(string name, long defaultValue, long min, long max)
    {
        var raw = Get(name);
        if (raw is null) return defaultValue;
        if (!long.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ZbaBadRequestException("invalid_" + name);
        return value;
    }

    /// <summary>
    /// 游标长度上限（字符）。与签发侧 ManagedCapabilityService.CursorTokenLimit 一致：
    /// 320 字节载荷 → Base64URL 427 字符 + '.' + 43 字符签名 = 471。
    /// </summary>
    internal const int CursorLimit = 512;

    internal string? GetCursor()
    {
        var value = Get("cursor");
        if (value is null) return null;
        // 能力目录的游标是 Base64URL + '.' 的签名令牌，分隔点必须放行；
        // 其余字符仍然只允许 URL 安全集合（'+/=' 与 '+' 解码成空格的问题一起挡掉）。
        if (value.Length is 0 or > CursorLimit || !value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            throw new ZbaBadRequestException("invalid_cursor");
        return value;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            throw new ZbaBadRequestException("invalid_query");
        }
    }
}
