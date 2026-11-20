using System.Text.RegularExpressions;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

internal static class PluginDataValidation
{
    internal static void Validate(PluginPackageManifest manifest)
    {
        if (manifest.Control && manifest.ApiVersion != 1) throw new InvalidDataException("plugin_control_invalid_declaration");
        if (manifest.Data is not { } data) return;
        if (manifest.ApiVersion != 1 || data.Tables is null || data.StatePrefixes is null
            || data.Tables.Count > 32 || data.StatePrefixes.Count > 32
            || data.Tables.Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Tables.Count
            || data.StatePrefixes.Distinct(StringComparer.Ordinal).Count() != data.StatePrefixes.Count
            || data.Tables.Any(t => t is null || !Regex.IsMatch(t, "^[a-z][a-z0-9_]{0,79}$")
                || t.StartsWith("sqlite_", StringComparison.Ordinal) || t is "meta" or "token_usage")
            || data.StatePrefixes.Any(p => p is null || !Regex.IsMatch(p, "^[a-z][a-z0-9:.-]{0,118}:$")
                || p.StartsWith("operation-recovery:", StringComparison.Ordinal)
                || "operation-recovery:v1:".StartsWith(p, StringComparison.Ordinal))
            || data.Receipt is { } receipt && (!Token(receipt.Contract) || !Token(receipt.Version) || receipt.Version.Length > 64))
            throw new InvalidDataException("plugin_data_invalid_declaration");
    }

    internal static PluginPackageData Normalize(PluginPackageData data) => data with
    { Tables = data.Tables.Order(StringComparer.Ordinal).ToArray(), StatePrefixes = data.StatePrefixes.Order(StringComparer.Ordinal).ToArray() };

    internal static bool RequiresColdStart(PluginPackageManifest? manifest)
        => PluginUiValidation.RequiresColdStart(manifest);

    private static bool Token(string value) => Regex.IsMatch(value ?? "", "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$");
}
