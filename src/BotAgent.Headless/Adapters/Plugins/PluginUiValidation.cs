using System.Text.RegularExpressions;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

internal static class PluginUiValidation
{
    internal const long MaxTotalUiBytes = 4 * 1024 * 1024;
    private const string ZeroSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly Dictionary<string, string> AllowedExtensionContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".json"] = "application/json; charset=utf-8",
    };

    internal static void Validate(PluginPackageManifest manifest)
    {
        if (manifest.Adoption is not null && manifest.Adoption is not (PluginAdoptionMode.Cold or PluginAdoptionMode.Online))
            throw new InvalidDataException("plugin_adoption_invalid");
        if (manifest.ExclusiveResources is { } exclusive)
        {
            if (exclusive.Count > 32
                || exclusive.Distinct(StringComparer.Ordinal).Count() != exclusive.Count
                || exclusive.Any(item => !Token(item)))
                throw new InvalidDataException("plugin_exclusive_resources_invalid");
            if (exclusive.Count > 0 && manifest.Adoption == PluginAdoptionMode.Online)
                throw new InvalidDataException("plugin_exclusive_resource_requires_cold_adoption");
        }
        if (manifest.Adoption == PluginAdoptionMode.Online && RequiresColdStartIntrinsic(manifest))
            throw new InvalidDataException("plugin_online_adoption_not_supported");

        if (manifest.Ui is not { } ui) return;
        if (manifest.ApiVersion != 1 || ui.ContractVersion != PluginUiDeclaration.CurrentContractVersion)
            throw new InvalidDataException("plugin_ui_invalid_declaration");
        if (ui.Kind is not null && ui.Kind is not ("dialog" or "panel" or "page"))
            throw new InvalidDataException("plugin_ui_invalid_declaration");
        if (ui.Title is not null && (string.IsNullOrWhiteSpace(ui.Title) || ui.Title.Length > 120 || ui.Title.Any(char.IsControl)))
            throw new InvalidDataException("plugin_ui_invalid_declaration");
        if (ui.Assets is null || ui.Assets.Count is 0 or > 64)
            throw new InvalidDataException("plugin_ui_invalid_declaration");
        ValidateAssetPath(ui.Entry);
        if (!ui.Entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("plugin_ui_entry_must_be_html");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryFound = false;
        foreach (var asset in ui.Assets)
        {
            if (asset is null) throw new InvalidDataException("plugin_ui_invalid_declaration");
            ValidateAssetPath(asset.Path);
            if (!seen.Add(asset.Path)) throw new InvalidDataException("plugin_ui_duplicate_asset");
            if (!Regex.IsMatch(asset.Sha256 ?? "", "^[0-9a-fA-F]{64}$")
                || string.Equals(asset.Sha256, ZeroSha256, StringComparison.Ordinal))
                throw new InvalidDataException("plugin_ui_asset_hash_invalid");
            var ext = Path.GetExtension(asset.Path);
            if (!AllowedExtensionContentTypes.TryGetValue(ext, out var expectedType)
                || !string.Equals(asset.ContentType, expectedType, StringComparison.Ordinal))
                throw new InvalidDataException("plugin_ui_asset_content_type_invalid");
            if (string.Equals(asset.Path, ui.Entry, StringComparison.Ordinal)) entryFound = true;
        }
        if (!entryFound) throw new InvalidDataException("plugin_ui_entry_missing_from_assets");
        if (ui.AllowedActions is { } actions)
        {
            if (actions.Count > 32
                || actions.Distinct(StringComparer.Ordinal).Count() != actions.Count
                || actions.Any(action => !Token(action) || !IsActionAllowedForPlugin(manifest.Id, action)))
                throw new InvalidDataException("plugin_ui_allowed_actions_invalid");
        }
    }

    internal static bool IsActionAllowedForPlugin(string pluginId, string action) => action switch
    {
        "dialog.close" or "operations.list" or "settings.read" or "settings.save" => true,
        "quotas.list" or "quotas.save" or "quotas.find" => string.Equals(pluginId, "preset.feature.token-quota", StringComparison.Ordinal),
        "scheduler.list" or "scheduler.save" or "scheduler.delete" or "scheduler.runs" => string.Equals(pluginId, "preset.feature.scheduler", StringComparison.Ordinal),
        "todo.list" or "todo.save" or "todo.delete" => string.Equals(pluginId, "preset.feature.personal-todo", StringComparison.Ordinal),
        _ => false,
    };

    internal static PluginUiDeclaration Normalize(PluginUiDeclaration ui) => ui with
    {
        Assets = ui.Assets
            .OrderBy(asset => asset.Path, StringComparer.Ordinal)
            .Select(asset => asset with { Sha256 = asset.Sha256.ToLowerInvariant() })
            .ToArray(),
        AllowedActions = ui.AllowedActions is { Count: > 0 }
            ? ui.AllowedActions.OrderBy(action => action, StringComparer.Ordinal).ToArray()
            : null,
    };

    internal static string ResolveAdoptionMode(PluginPackageManifest? manifest)
        => manifest is null ? PluginAdoptionMode.Online
            : PluginDataValidation.RequiresColdStart(manifest) ? PluginAdoptionMode.Cold : PluginAdoptionMode.Online;

    internal static bool RequiresColdStartIntrinsic(PluginPackageManifest? manifest)
        => manifest?.Control == true || manifest?.ToolApiVersion is not null || manifest?.Configuration is not null
            || manifest?.Data is not null || manifest?.SharedContracts is { Count: > 0 }
            || PluginActivationWeb.Requested(manifest) || PluginActivationModel.Requested(manifest)
            || PluginActivationExecution.Requested(manifest)
            || manifest?.ExclusiveResources is { Count: > 0 };

    internal static bool RequiresColdStart(PluginPackageManifest? manifest)
        => manifest?.Adoption == PluginAdoptionMode.Cold || RequiresColdStartIntrinsic(manifest);

    internal static bool RequiresReaderTwelve(PluginPackageManifest? manifest)
        => manifest?.Ui is not null || manifest?.Adoption is not null || manifest?.ExclusiveResources is { Count: > 0 };

    private static void ValidateAssetPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("web/", StringComparison.Ordinal))
            throw new InvalidDataException("plugin_ui_asset_path_invalid");
        PluginPackageValidation.ValidateRelativePath(path);
    }

    private static bool Token(string? value)
        => value is { Length: > 0 } && Regex.IsMatch(value, "^[a-z][a-z0-9._-]{0,79}$");
}
