using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Net;

internal partial class Program
{
    internal static Task<int> RunResearchSettingsContractChecks()
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        void Expect<T>(Action action, string label) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException(label);
        }

        Check(typeof(PluginSettingDefinition).Assembly.GetName().Name == "BotAgent.Plugin.Contracts",
            "independent owners use the existing declarative setting type from the generic SDK");
        Check(typeof(ResearchSettings).Assembly == typeof(ResearchOptions).Assembly,
            "research field semantics have the same independent owner as research options");
        Check(ResearchSettings.Defaults is { EnableWebSearch: true, WebSearchUseModelSearch: true,
            WebSearchMaxResults: 5, WebSearchCooldownSeconds: 30, WebSearchTimeoutSeconds: 20,
            WebSearchReadMaxChars: 1800, EnableLinkPreview: true, LinkPreviewTimeoutSeconds: 5, LinkPreviewMax: 3 },
            "ten research defaults preserve the original enabled features and limits");
        Check(ResearchSettings.Defaults.WebSearchSources.StartsWith("wiki|https://zh.wikipedia.org/", StringComparison.Ordinal)
            && ResearchSettings.Defaults.WebSearchSources.Contains("srsearch={q}", StringComparison.Ordinal),
            "the public wiki fallback keeps its query placeholder");
        var schema = ResearchSettings.Schema;
        Check(schema.Count == 9 && schema.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() == 9,
            "the same nine fields are editable without adding the env-only read limit");
        Check(schema.All(field => !string.IsNullOrWhiteSpace(field.Label) && field.DefaultValue is not null
            && field.Type is "boolean" or "string" or "number" && !field.Required && field.Choices is null),
            "declarations use existing generic primitive semantics only");
        Check(schema.Single(field => field.Key == "webSearchSources").DefaultValue == ResearchSettings.Defaults.WebSearchSources
            && schema.Single(field => field.Key == "webSearchTimeoutSeconds").DefaultValue == "20"
            && schema.Single(field => field.Key == "enableWebSearch").DefaultValue == "true",
            "string defaults are plain strings while numeric and boolean defaults are JSON literals");
        Check(!schema.Any(field => field.Key == "webSearchReadMaxChars" || field.Type == "secret"),
            "metadata exposes neither new legacy edits nor credentials");
        Expect<NotSupportedException>(() => ((IList<PluginSettingDefinition>)schema)[0] = schema[0] with { Key = "changed" },
            "a consumer cannot mutate the shared field list");
        var aliases = ResearchSettings.EnvironmentAliases;
        Check(aliases.Count == 10 && aliases.Values.SelectMany(values => values).Distinct(StringComparer.Ordinal).Count() == 10,
            "the complete env seed surface is owned once");
        Check(aliases[nameof(ResearchOptions.WebSearchReadMaxChars)].SequenceEqual(["QQCHAT_SEARCH_READ_CHARS"])
            && aliases[nameof(ResearchOptions.EnableLinkPreview)].SequenceEqual(["QQCHAT_LINK_PREVIEW"]),
            "existing deployment aliases and the env-only limit retain their names");
        Expect<NotSupportedException>(() => ((IDictionary<string, IReadOnlyList<string>>)aliases).Remove("WebSearchMaxResults"),
            "a consumer cannot remove another caller's env binding");
        Expect<NotSupportedException>(() => ((IList<string>)aliases[nameof(ResearchOptions.EnableWebSearch)])[0] = "CHANGED",
            "nested env aliases are read-only as well");

        var options = ResearchSettings.Defaults with { EnableWebSearch = false, WebSearchSources = "synthetic",
            WebSearchMaxResults = 8, LinkPreviewMax = 2 };
        var requested = new List<string>();
        var untouched = ResearchSettings.SeedEnvironment(options, name => { requested.Add(name); return null; });
        Check(untouched == options && requested.Count == 10 && requested.Distinct(StringComparer.Ordinal).Count() == 10
            && requested.All(name => aliases.Values.Any(values => values.Contains(name, StringComparer.Ordinal))),
            "seeding reads only the ten approved aliases and preserves the supplied current values");
        foreach (var token in new[] { "1", "true", " YES ", "Y", "ON" })
            Check(ResearchSettings.SeedEnvironment(options, name => name == "QQCHAT_WEB_SEARCH" ? token : null).EnableWebSearch,
                "existing true env token " + token);
        foreach (var token in new[] { "0", "false", " No ", "n", "off" })
            Check(!ResearchSettings.SeedEnvironment(ResearchSettings.Defaults,
                name => name == "QQCHAT_WEB_SEARCH" ? token : null).EnableWebSearch, "existing false env token " + token);
        var warnings = new List<string>();
        var invalid = ResearchSettings.SeedEnvironment(options, name => name switch
        {
            "QQCHAT_WEB_SEARCH" => "invalid",
            "QQCHAT_SEARCH_MAX_RESULTS" => "2147483648",
            _ => null
        }, warnings.Add);
        Check(invalid == options && warnings.Count == 2 && warnings[0].Contains("不是布尔值", StringComparison.Ordinal)
            && warnings[1].Contains("不是整数", StringComparison.Ordinal), "invalid env values retain current settings and old warning kinds");
        var seeded = ResearchSettings.SeedEnvironment(options, name => name switch
        {
            "QQCHAT_SEARCH_MAX_RESULTS" => " -1 ",
            "QQCHAT_SEARCH_READ_CHARS" => " +42 ",
            "QQCHAT_SEARCH_SOURCES" => "  source|https://example.com/?q={q}  ",
            _ => null
        });
        Check(seeded.WebSearchMaxResults == -1 && seeded.WebSearchReadMaxChars == 42
            && seeded.WebSearchSources == "source|https://example.com/?q={q}" && options.WebSearchMaxResults == 8,
            "first-deployment seeding keeps original integer parsing and trimming without introducing panel clamps");
        Expect<ArgumentNullException>(() => ResearchSettings.SeedEnvironment(options, null!), "missing env reader is rejected");

        Check(!ResearchSettings.TryReadLegacyField<bool>(new JsonObject(), "enableWebSearch", out _)
            && !ResearchSettings.TryReadLegacyField<bool>(new JsonObject { ["enableWebSearch"] = null }, "enableWebSearch", out _),
            "absent and explicit null retain the prior setting");
        Check(ResearchSettings.TryReadLegacyField<bool>(new JsonObject { ["enableWebSearch"] = false }, "enableWebSearch", out var disabled)
            && !disabled, "an explicit false remains a real edit");
        Check(!ResearchSettings.TryReadLegacyField<int>(new JsonObject { ["webSearchReadMaxChars"] = 42 }, "webSearchReadMaxChars", out _)
            && !ResearchSettings.TryReadLegacyField<string>(new JsonObject { ["unrelated"] = "synthetic" }, "unrelated", out _),
            "env-only and unrelated fields cannot be edited through the old field reader");
        Check(ResearchSettings.TryReadLegacyField<string>(new JsonObject { ["webSearchSources"] = "  synthetic  " },
            "webSearchSources", out var text) && text == "synthetic", "legacy source edits preserve trimming");
        Check(ResearchSettings.TryReadLegacyField<string>(new JsonObject { ["webSearchSources"] = "   " },
            "webSearchSources", out var empty) && empty == "", "an explicit empty source remains an edit");
        foreach (var raw in new[] { "\"false\"", "0", "[]", "{}" })
            Expect<InvalidOperationException>(() => ResearchSettings.TryReadLegacyField<bool>(
                JsonNode.Parse("{\"enableWebSearch\":" + raw + "}")!.AsObject(), "enableWebSearch", out _),
                "wrong JSON bool types remain errors");
        Expect<InvalidOperationException>(() => ResearchSettings.TryReadLegacyField<int>(
            new JsonObject { ["webSearchMaxResults"] = "5" }, "webSearchMaxResults", out _),
            "numeric strings are not coerced by the panel parser");

        (string Name, string Key, int Min, int Max)[] ranges =
        [
            ("LinkPreviewMax", "linkPreviewMax", 0, 5),
            ("LinkPreviewTimeoutSeconds", "linkPreviewTimeoutSeconds", 2, 30),
            ("WebSearchMaxResults", "webSearchMaxResults", 1, 10),
            ("WebSearchCooldownSeconds", "webSearchCooldownSeconds", 0, 86400),
            ("WebSearchTimeoutSeconds", "webSearchTimeoutSeconds", 5, 60)
        ];
        foreach (var field in ranges)
        {
            Check(ResearchSettings.TryGetNumericRange(field.Name, out var range) && range == (field.Min, field.Max),
                "versioned configuration preserves the original rejecting range for " + field.Name);
            Check(ResearchSettings.TryReadLegacyField<int>(new JsonObject { [field.Key] = int.MinValue }, field.Key, out var minimum)
                && minimum == field.Min, "legacy lower-bound clamping for " + field.Name);
            Check(ResearchSettings.TryReadLegacyField<int>(new JsonObject { [field.Key] = int.MaxValue }, field.Key, out var maximum)
                && maximum == field.Max, "legacy upper-bound clamping for " + field.Name);
        }
        Check(!ResearchSettings.TryGetNumericRange("MaxTokens", out _) && !ResearchSettings.TryGetNumericRange("WebSearchReadMaxChars", out _),
            "range ownership does not absorb other features or add old edits");
        Check(ResearchSettings.TryNormalizeText("WebSearchSources", "  synthetic  ", out var normalized) && normalized == "synthetic"
            && !ResearchSettings.TryNormalizeText("Model", "  synthetic  ", out var unrelated) && unrelated == "  synthetic  ",
            "text normalization is restricted to owned editable fields");
        Check(new[] { ResearchSettings.LegacyIds.EnableLinkPreview, ResearchSettings.LegacyIds.EnableWebSearch,
            ResearchSettings.LegacyIds.LinkPreviewMax, ResearchSettings.LegacyIds.LinkPreviewTimeoutSeconds,
            ResearchSettings.LegacyIds.WebSearchCooldownSeconds, ResearchSettings.LegacyIds.WebSearchMaxResults,
            ResearchSettings.LegacyIds.WebSearchSources, ResearchSettings.LegacyIds.WebSearchTimeoutSeconds,
            ResearchSettings.LegacyIds.WebSearchUseModelSearch }.SequenceEqual([39, 49, 65, 66, 136, 137, 138, 139, 140]),
            "numeric compatibility identities preserve every original value");
        return Task.FromResult(checks);
    }
}
