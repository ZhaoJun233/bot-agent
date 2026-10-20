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
            if (key.Length is 0 or > 32 || value.Length > 256)
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

    internal string? GetCursor()
    {
        var value = Get("cursor");
        if (value is null) return null;
        if (value.Length is 0 or > 512 || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
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
