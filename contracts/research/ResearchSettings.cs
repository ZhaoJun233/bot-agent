using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Net;

/// <summary>Research field semantics shared by the independent owner and legacy compatibility adapters.</summary>
public static class ResearchSettings
{
    public static ResearchOptions Defaults { get; } = new(
        EnableWebSearch: true,
        WebSearchUseModelSearch: true,
        WebSearchSources: "wiki|https://zh.wikipedia.org/w/api.php?action=query&list=search&srsearch={q}&format=json&srlimit=5&utf8=1",
        WebSearchMaxResults: 5,
        WebSearchCooldownSeconds: 30,
        WebSearchTimeoutSeconds: 20,
        WebSearchReadMaxChars: 1800,
        EnableLinkPreview: true,
        LinkPreviewTimeoutSeconds: 5,
        LinkPreviewMax: 3);

    /// <summary>Frozen numeric identities used only by the old configuration bridge.</summary>
    public static class LegacyIds
    {
        public const int EnableLinkPreview = 39;
        public const int EnableWebSearch = 49;
        public const int LinkPreviewMax = 65;
        public const int LinkPreviewTimeoutSeconds = 66;
        public const int WebSearchCooldownSeconds = 136;
        public const int WebSearchMaxResults = 137;
        public const int WebSearchSources = 138;
        public const int WebSearchTimeoutSeconds = 139;
        public const int WebSearchUseModelSearch = 140;
    }

    private sealed record Field(string Name, string Label, string Type, string EnvironmentName,
        object DefaultValue, int? LegacyId = null, (int Min, int Max)? Range = null)
    {
        internal string Key => JsonNamingPolicy.CamelCase.ConvertName(Name);
    }

    private static readonly Field[] Fields =
    [
        new(nameof(ResearchOptions.EnableLinkPreview), "链接预览", "boolean", "QQCHAT_LINK_PREVIEW",
            Defaults.EnableLinkPreview, LegacyIds.EnableLinkPreview),
        new(nameof(ResearchOptions.EnableWebSearch), "联网搜索", "boolean", "QQCHAT_WEB_SEARCH",
            Defaults.EnableWebSearch, LegacyIds.EnableWebSearch),
        new(nameof(ResearchOptions.WebSearchUseModelSearch), "优先使用模型搜索", "boolean", "QQCHAT_SEARCH_USE_MODEL",
            Defaults.WebSearchUseModelSearch, LegacyIds.WebSearchUseModelSearch),
        new(nameof(ResearchOptions.WebSearchSources), "搜索源模板", "string", "QQCHAT_SEARCH_SOURCES",
            Defaults.WebSearchSources, LegacyIds.WebSearchSources),
        new(nameof(ResearchOptions.WebSearchMaxResults), "搜索结果数量", "number", "QQCHAT_SEARCH_MAX_RESULTS",
            Defaults.WebSearchMaxResults, LegacyIds.WebSearchMaxResults, (1, 10)),
        new(nameof(ResearchOptions.WebSearchCooldownSeconds), "搜索冷却（秒）", "number", "QQCHAT_SEARCH_COOLDOWN",
            Defaults.WebSearchCooldownSeconds, LegacyIds.WebSearchCooldownSeconds, (0, 86400)),
        new(nameof(ResearchOptions.WebSearchTimeoutSeconds), "搜索超时（秒）", "number", "QQCHAT_SEARCH_TIMEOUT",
            Defaults.WebSearchTimeoutSeconds, LegacyIds.WebSearchTimeoutSeconds, (5, 60)),
        new(nameof(ResearchOptions.WebSearchReadMaxChars), "读页截断长度", "number", "QQCHAT_SEARCH_READ_CHARS",
            Defaults.WebSearchReadMaxChars),
        new(nameof(ResearchOptions.LinkPreviewTimeoutSeconds), "链接预览超时（秒）", "number", "QQCHAT_LINK_PREVIEW_TIMEOUT",
            Defaults.LinkPreviewTimeoutSeconds, LegacyIds.LinkPreviewTimeoutSeconds, (2, 30)),
        new(nameof(ResearchOptions.LinkPreviewMax), "链接预览数量", "number", "QQCHAT_LINK_PREVIEW_MAX",
            Defaults.LinkPreviewMax, LegacyIds.LinkPreviewMax, (0, 5))
    ];

    private static readonly IReadOnlyDictionary<string, Field> ByName = new ReadOnlyDictionary<string, Field>(
        Fields.ToDictionary(field => field.Name, StringComparer.Ordinal));
    private static readonly IReadOnlyDictionary<string, Field> ByKey = new ReadOnlyDictionary<string, Field>(
        Fields.ToDictionary(field => field.Key, StringComparer.Ordinal));

    /// <summary>The nine existing editable fields; the env-only read limit remains outside the old panel.</summary>
    public static IReadOnlyList<PluginSettingDefinition> Schema { get; } = Array.AsReadOnly(Fields
        .Where(field => field.LegacyId is not null)
        .Select(field => new PluginSettingDefinition(field.Key, field.Label, field.Type,
            field.Type == "string" ? (string)field.DefaultValue : JsonSerializer.Serialize(field.DefaultValue))
        {
            NumberConstraints = field.Range is { } limits ? new(limits.Min, limits.Max, Integer: true) : null
        })
        .ToArray());

    public static PluginConfigurationDefinition Definition { get; } = new(Schema,
        new(1, Array.AsReadOnly(Fields.Select(field => new PluginConfigurationBinding(field.Key,
            field.LegacyId, ReadOnly: field.LegacyId is null, Trim: field.Type == "string")).ToArray())));

    /// <summary>Interpret only this owner's complete runtime snapshot; no host settings object is exposed.</summary>
    public static ResearchOptions Interpret(PluginConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Values.Count != Fields.Length || Fields.Any(field => !snapshot.Values.ContainsKey(field.Key)))
            throw new InvalidOperationException("research_configuration_incomplete");
        var values = snapshot.Values;
        return new(values["enableWebSearch"].GetBoolean(), values["webSearchUseModelSearch"].GetBoolean(),
            values["webSearchSources"].GetString()!, values["webSearchMaxResults"].GetInt32(),
            values["webSearchCooldownSeconds"].GetInt32(), values["webSearchTimeoutSeconds"].GetInt32(),
            values["webSearchReadMaxChars"].GetInt32(), values["enableLinkPreview"].GetBoolean(),
            values["linkPreviewTimeoutSeconds"].GetInt32(), values["linkPreviewMax"].GetInt32());
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> EnvironmentAliases { get; } =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(Fields.ToDictionary(field => field.Name,
            field => (IReadOnlyList<string>)Array.AsReadOnly(new[] { field.EnvironmentName }), StringComparer.Ordinal));

    /// <summary>Seed a first configuration; existing persisted configurations never call this operation.</summary>
    public static ResearchOptions SeedEnvironment(ResearchOptions options, Func<string, string?> readEnvironment,
        Action<string>? warning = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        return options with
        {
            EnableLinkPreview = Seed(nameof(ResearchOptions.EnableLinkPreview), options.EnableLinkPreview),
            EnableWebSearch = Seed(nameof(ResearchOptions.EnableWebSearch), options.EnableWebSearch),
            WebSearchUseModelSearch = Seed(nameof(ResearchOptions.WebSearchUseModelSearch), options.WebSearchUseModelSearch),
            WebSearchSources = Seed(nameof(ResearchOptions.WebSearchSources), options.WebSearchSources),
            WebSearchMaxResults = Seed(nameof(ResearchOptions.WebSearchMaxResults), options.WebSearchMaxResults),
            WebSearchCooldownSeconds = Seed(nameof(ResearchOptions.WebSearchCooldownSeconds), options.WebSearchCooldownSeconds),
            WebSearchTimeoutSeconds = Seed(nameof(ResearchOptions.WebSearchTimeoutSeconds), options.WebSearchTimeoutSeconds),
            WebSearchReadMaxChars = Seed(nameof(ResearchOptions.WebSearchReadMaxChars), options.WebSearchReadMaxChars),
            LinkPreviewTimeoutSeconds = Seed(nameof(ResearchOptions.LinkPreviewTimeoutSeconds), options.LinkPreviewTimeoutSeconds),
            LinkPreviewMax = Seed(nameof(ResearchOptions.LinkPreviewMax), options.LinkPreviewMax)
        };

        T Seed<T>(string name, T fallback)
        {
            var field = ByName[name];
            var raw = readEnvironment(field.EnvironmentName);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (field.Type == "string") return (T)(object)raw.Trim();
            if (field.Type == "number")
            {
                if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    return (T)(object)number;
                warning?.Invoke($"[Config] {field.EnvironmentName}='{raw}' 不是整数，已忽略");
                return fallback;
            }
            var flag = raw.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "y" or "on" => (bool?)true,
                "0" or "false" or "no" or "n" or "off" => false,
                _ => null
            };
            if (flag is { } value) return (T)(object)value;
            warning?.Invoke($"[Config] {field.EnvironmentName}='{raw}' 不是布尔值，已忽略");
            return fallback;
        }
    }

    /// <summary>Preserve old JSON null/absence, strict value types, numeric clamping and source trimming.</summary>
    public static bool TryReadLegacyField<T>(JsonObject body, string key, out T value)
    {
        ArgumentNullException.ThrowIfNull(body);
        value = default!;
        if (!ByKey.TryGetValue(key, out var field) || field.LegacyId is null || body[key] is not { } node)
            return false;
        object parsed = field.Type switch
        {
            "boolean" => node.GetValue<bool>(),
            "string" => node.GetValue<string>().Trim(),
            "number" => Clamp(node.GetValue<int>(), field.Range!.Value),
            _ => throw new InvalidOperationException("unknown_research_field_type")
        };
        value = (T)parsed;
        return true;

        static int Clamp(int number, (int Min, int Max) range) => Math.Clamp(number, range.Min, range.Max);
    }

    public static bool TryGetNumericRange(string legacyName, out (double Min, double Max) range)
    {
        range = default;
        if (!ByName.TryGetValue(legacyName, out var field) || field.LegacyId is null || field.Range is not { } limits)
            return false;
        range = (limits.Min, limits.Max);
        return true;
    }

    public static bool TryNormalizeText(string legacyName, string text, out string normalized)
    {
        normalized = text;
        if (!ByName.TryGetValue(legacyName, out var field) || field.LegacyId is null || field.Type != "string")
            return false;
        normalized = text.Trim();
        return true;
    }
}
