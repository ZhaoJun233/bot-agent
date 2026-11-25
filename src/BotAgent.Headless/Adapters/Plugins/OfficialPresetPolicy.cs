using System.Collections.Frozen;
using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Plugins.Execution;
using BotAgent.Plugins.Net;

namespace BotAgent.Adapters.Plugins;

/// <summary>
/// Closed zero-trust policy for the 13 official marketplace feature packages migrated out of the host bundle.
/// Keeps system.* in HostPluginBundle while locking preset.feature.* origin, least-privilege capabilities,
/// and cross-repository shared contract identities.
/// </summary>
internal static class OfficialPresetPolicy
{
    internal const string DefaultReleasePrefix = "https://github.com/ZhaoJun233/bot-agent-plugins/releases/download";
    internal const string OfficialRepositoryOwner = "ZhaoJun233";
    internal const string OfficialRepositoryName = "bot-agent-plugins";
    internal const string ReleasePrefixEnvironmentVariable = "BOTAGENT_OFFICIAL_PLUGIN_RELEASE_PREFIX";

    internal sealed record OfficialFeatureProfile(
        string ContractName,
        bool AllowControl,
        bool AllowConfiguration,
        bool RequireControlWhenApproved,
        bool AllowData,
        bool RequireDataWhenApproved,
        int? RequiredToolApiVersion,
        IReadOnlySet<string> AllowedPermissions,
        IReadOnlySet<string> AllowedUiActions);

    internal static readonly string ContractAssemblyVersion = new Version(1, 0, 0, 0).ToString();

    private static readonly IReadOnlySet<string> EmptySet = FrozenSet<string>.Empty;
    private static readonly IReadOnlySet<string> BasicUiActions = new[]
    {
        "dialog.close", "operations.list", "settings.read", "settings.save"
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly IReadOnlyDictionary<string, OfficialFeatureProfile> Profiles =
        new Dictionary<string, OfficialFeatureProfile>(StringComparer.Ordinal)
        {
            ["preset.feature.digest"] = new(
                "BotAgent.Digest.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: 1,
                AllowedPermissions: new[] { IGroundedTextModel.Permission, IToolExecution.Permission }.ToFrozenSet(StringComparer.Ordinal),
                AllowedUiActions: EmptySet),
            ["preset.feature.documents"] = new(
                "BotAgent.Documents.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: 1,
                AllowedPermissions: new[] { IWebPageTransport.Permission, IToolExecution.Permission }.ToFrozenSet(StringComparer.Ordinal),
                AllowedUiActions: EmptySet),
            ["preset.feature.knowledge"] = new(
                "BotAgent.Knowledge.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: 1,
                AllowedPermissions: new[] { IToolExecution.Permission }.ToFrozenSet(StringComparer.Ordinal),
                AllowedUiActions: EmptySet),
            ["preset.feature.music"] = new(
                "BotAgent.Music.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: null,
                AllowedPermissions: EmptySet,
                AllowedUiActions: EmptySet),
            ["preset.feature.personal-todo"] = new(
                "BotAgent.PersonalTodo.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: 1,
                AllowedPermissions: new[] { IToolExecution.Permission }.ToFrozenSet(StringComparer.Ordinal),
                AllowedUiActions: BasicUiActions.Concat(["todo.list", "todo.save", "todo.delete"]).ToFrozenSet(StringComparer.Ordinal)),
            ["preset.feature.poke"] = new(
                "BotAgent.Poke.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: null,
                AllowedPermissions: EmptySet,
                AllowedUiActions: EmptySet),
            ["preset.feature.profiles"] = new(
                "BotAgent.Profiles.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: null,
                AllowedPermissions: EmptySet,
                AllowedUiActions: EmptySet),
            ["preset.feature.scheduler"] = new(
                "BotAgent.Scheduler.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: 1,
                AllowedPermissions: new[] { IToolExecution.Permission }.ToFrozenSet(StringComparer.Ordinal),
                AllowedUiActions: BasicUiActions.Concat(["scheduler.list", "scheduler.save", "scheduler.delete", "scheduler.runs"]).ToFrozenSet(StringComparer.Ordinal)),
            ["preset.feature.stickers"] = new(
                "BotAgent.Stickers.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: null,
                AllowedPermissions: EmptySet,
                AllowedUiActions: BasicUiActions),
            ["preset.feature.token-quota"] = new(
                "BotAgent.Quota.Contracts",
                AllowControl: true,
                AllowConfiguration: false,
                RequireControlWhenApproved: true,
                AllowData: true,
                RequireDataWhenApproved: true,
                RequiredToolApiVersion: null,
                AllowedPermissions: EmptySet,
                AllowedUiActions: BasicUiActions.Concat(["quotas.list", "quotas.save", "quotas.find"]).ToFrozenSet(StringComparer.Ordinal)),
            ["preset.feature.vibes"] = new(
                "BotAgent.Vibes.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: null,
                AllowedPermissions: EmptySet,
                AllowedUiActions: EmptySet),
            ["preset.feature.voice"] = new(
                "BotAgent.Voice.Contracts",
                AllowControl: false,
                AllowConfiguration: false,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: null,
                AllowedPermissions: EmptySet,
                AllowedUiActions: EmptySet),
            ["preset.feature.websearch"] = new(
                "BotAgent.Research.Contracts",
                AllowControl: true,
                AllowConfiguration: true,
                RequireControlWhenApproved: false,
                AllowData: false,
                RequireDataWhenApproved: false,
                RequiredToolApiVersion: 1,
                AllowedPermissions: new[]
                {
                    IWebPageTransport.Permission,
                    IGroundedTextModel.Permission,
                    IToolExecution.Permission
                }.ToFrozenSet(StringComparer.Ordinal),
                AllowedUiActions: EmptySet),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static readonly IReadOnlyDictionary<string, string> OfficialContractOwners =
        Profiles.ToFrozenDictionary(pair => pair.Value.ContractName, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    internal static bool IsOfficialPresetId(string? id)
        => id is not null && Profiles.ContainsKey(id);

    internal static bool IsOfficialRepository(string owner, string repository)
        => string.Equals(owner, OfficialRepositoryOwner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(repository, OfficialRepositoryName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsOfficialContractName(string? contractName)
        => contractName is not null && OfficialContractOwners.ContainsKey(contractName);

    internal static bool IsOfficialContractIdentity(PluginSharedContract contract)
        => contract is not null
            && OfficialContractOwners.TryGetValue(contract.Name, out var owner)
            && string.Equals(contract.Owner, owner, StringComparison.Ordinal)
            && string.Equals(contract.Version, ContractAssemblyVersion, StringComparison.Ordinal);

    internal static string EffectiveReleasePrefix()
    {
        var configured = Environment.GetEnvironmentVariable(ReleasePrefixEnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(configured)) return DefaultReleasePrefix;
        var trimmed = configured.TrimEnd('/');
        _ = PluginPackageValidation.ValidateUrl(trimmed + "/probe-1.0.0/probe-1.0.0.zip");
        return trimmed;
    }

    internal static string ExpectedDownloadUrl(string id, string version)
        => $"{EffectiveReleasePrefix()}/{id}-{version}/{id}-{version}.zip";

    internal static string LegacyHostDistributionUrl(string id, string version)
        => $"host-distribution://{id}/{version}";

    internal static bool HasPinnedOrigin(PluginPackageManifest manifest, bool allowLegacyHostDistribution)
    {
        if (!IsOfficialPresetId(manifest.Id) || manifest.ApiVersion != 1 || manifest.Runtime is not null)
            return false;
        if (string.Equals(manifest.DownloadUrl, ExpectedDownloadUrl(manifest.Id, manifest.Version), StringComparison.Ordinal))
            return true;
        return allowLegacyHostDistribution
            && string.Equals(manifest.DownloadUrl, LegacyHostDistributionUrl(manifest.Id, manifest.Version), StringComparison.Ordinal);
    }

    internal static void ValidateOrigin(PluginPackageManifest manifest, bool allowLegacyHostDistribution)
    {
        if (!HasPinnedOrigin(manifest, allowLegacyHostDistribution))
            throw new InvalidDataException("Official preset package requires pinned marketplace release URL.");
    }

    internal static bool IsApprovedOfficialOrLegacyManifest(PluginPackageManifest? manifest)
    {
        if (manifest is null || !Profiles.TryGetValue(manifest.Id, out var profile))
            return false;
        if (!HasPinnedOrigin(manifest, allowLegacyHostDistribution: true))
            return false;
        if (!IsCapabilitySetAuthorized(manifest, profile, enforceRequiredCapabilities: true))
            return false;
        return AreSharedContractsAuthorized(manifest);
    }

    internal static void EnsureResourcesAuthorized(PluginPackageManifest manifest, bool allowLegacyHostDistribution)
    {
        if (!Profiles.TryGetValue(manifest.Id, out var profile)
            || !HasPinnedOrigin(manifest, allowLegacyHostDistribution))
        {
            ThrowFirstRequestedCapabilityFailure(manifest);
            return;
        }

        if (manifest.Configuration is not null && !profile.AllowConfiguration)
            throw new InvalidOperationException("plugin_configuration_not_authorized");
        if (PluginActivationModel.Requested(manifest) && !profile.AllowedPermissions.Contains(IGroundedTextModel.Permission))
            throw new InvalidOperationException("plugin_model_not_authorized");
        if (PluginActivationExecution.Requested(manifest) && !profile.AllowedPermissions.Contains(IToolExecution.Permission))
            throw new InvalidOperationException("plugin_tool_execution_not_authorized");
        if (PluginActivationWeb.Requested(manifest) && !profile.AllowedPermissions.Contains(IWebPageTransport.Permission))
            throw new InvalidOperationException("plugin_web_not_authorized");
        if (manifest.Control && !profile.AllowControl)
            throw new InvalidOperationException("plugin_data_not_authorized");
        if (manifest.Data is { } data && (!profile.AllowData || !IsQuotaDataShape(data)))
            throw new InvalidOperationException("plugin_data_not_authorized");
        foreach (var permission in manifest.Permissions ?? [])
        {
            if (!profile.AllowedPermissions.Contains(permission))
                throw new InvalidOperationException(PermissionDenialCode(permission));
        }
    }

    private static void ThrowFirstRequestedCapabilityFailure(PluginPackageManifest manifest)
    {
        if (manifest.Configuration is not null)
            throw new InvalidOperationException("plugin_configuration_not_authorized");
        if (PluginActivationModel.Requested(manifest))
            throw new InvalidOperationException("plugin_model_not_authorized");
        if (PluginActivationExecution.Requested(manifest))
            throw new InvalidOperationException("plugin_tool_execution_not_authorized");
        if (PluginActivationWeb.Requested(manifest))
            throw new InvalidOperationException("plugin_web_not_authorized");
        if (manifest.Data is not null || manifest.Control)
            throw new InvalidOperationException("plugin_data_not_authorized");
        if (manifest.Permissions is { Count: > 0 })
            throw new InvalidOperationException(PermissionDenialCode(manifest.Permissions[0]));
    }

    private static bool IsCapabilitySetAuthorized(
        PluginPackageManifest manifest, OfficialFeatureProfile profile, bool enforceRequiredCapabilities)
    {
        if (manifest.Configuration is not null && !profile.AllowConfiguration) return false;
        if (manifest.Control && !profile.AllowControl) return false;
        if (enforceRequiredCapabilities && profile.RequireControlWhenApproved && !manifest.Control) return false;
        if (manifest.Data is not null && (!profile.AllowData || !IsQuotaDataShape(manifest.Data))) return false;
        if (enforceRequiredCapabilities && profile.RequireDataWhenApproved && manifest.Data is null) return false;
        if (manifest.ToolApiVersion is not null && manifest.ToolApiVersion != profile.RequiredToolApiVersion) return false;
        foreach (var permission in manifest.Permissions ?? [])
        {
            if (!profile.AllowedPermissions.Contains(permission)) return false;
        }
        if (manifest.Ui?.AllowedActions is { } actions)
        {
            foreach (var action in actions)
            {
                if (!profile.AllowedUiActions.Contains(action)) return false;
            }
        }
        return true;
    }

    private static bool AreSharedContractsAuthorized(PluginPackageManifest manifest)
    {
        foreach (var contract in manifest.SharedContracts ?? [])
        {
            if (contract is null) return false;
            if (contract.Owner == manifest.Id && IsOfficialContractIdentity(contract)) continue;
            if (!PluginSharedContractRegistry.IsHostContract(contract) && !IsOfficialContractIdentity(contract))
                return false;
        }
        return true;
    }

    private static bool IsQuotaDataShape(PluginPackageData data)
    {
        var tables = data.Tables.Order(StringComparer.Ordinal).ToArray();
        var prefixes = data.StatePrefixes.Order(StringComparer.Ordinal).ToArray();
        return tables.Length == 1
            && string.Equals(tables[0], "tenant_quotas", StringComparison.OrdinalIgnoreCase)
            && prefixes.SequenceEqual(["quota:receipts:", "quota:revision:"])
            && data.Usage
            && data.Audit
            && data.Receipt is { Contract: "quota.management", Version: "1" };
    }

    private static string PermissionDenialCode(string permission) => permission switch
    {
        IGroundedTextModel.Permission => "plugin_model_not_authorized",
        IToolExecution.Permission => "plugin_tool_execution_not_authorized",
        IWebPageTransport.Permission => "plugin_web_not_authorized",
        _ => "plugin_data_not_authorized",
    };
}
