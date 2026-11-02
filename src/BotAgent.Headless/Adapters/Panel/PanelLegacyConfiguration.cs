using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Settings;
using BotAgent.Adapters.Persistence;
using System.Collections.Immutable;
using BotAgent.Domain.Configuration;

namespace BotAgent.Adapters.Panel;

internal static class PanelLegacyConfiguration
{
    /// <summary>Pure complete intent. Composite validation finishes before the one secret owner is allocated.</summary>
    internal static LegacySecretPlan ParseLegacyConfiguration(JsonObject body, BotAgent.Platforms.PlatformOptions trustedOptions)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(trustedOptions);
        if (PanelLegacySecrets.HasOfficialSecretInput(body))
            return PanelLegacySecrets.RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "official_secret_environment_only");
        try
        {
            var policies = FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Keep();
            var scalarBody = body;
            if (body["platformPolicies"] is { } policyNode)
            {
                if (!PanelLegacyPolicies.TryParsePlatformPolicies(policyNode, WebUiServer.Json, out var parsed))
                    return PanelLegacySecrets.RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "invalid_platform_policies");
                var legacy = new BotAgent.Platforms.PlatformOptions
                {
                    PlatformPolicies = parsed!, PlatformSwitchSchemaVersion = 1,
                    DeclaredPlatformDrivers = trustedOptions.DeclaredPlatformDrivers,
                    DeclaredPlatformActions = trustedOptions.DeclaredPlatformActions,
                };
                var imported = Services.Platforms.PlatformSwitchSettings.ImportLegacyPolicies(legacy,
                    legacy.DeclaredPlatformDrivers, ImmutableArray<ActionId>.Empty);
                if (imported.Code != ConfigurationResultCode.Ok)
                    return PanelLegacySecrets.RejectedLegacySecrets(imported.Code, "legacy_policy_needs_review");
                policies = FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Set(imported.Entries);
                scalarBody = (JsonObject)body.DeepClone();
                foreach (var policy in parsed!)
                {
                    var platform = Domain.Platforms.PlatformId.Normalize(policy.PlatformId);
                    if (platform == Domain.Platforms.PlatformId.QqOfficial)
                    {
                        if (policy.Enabled is bool officialEnabled) scalarBody["officialEnabled"] = officialEnabled;
                        if (policy.ChatEnabled is bool officialChat) scalarBody["officialChatEnabled"] = officialChat;
                        if (policy.GroupWhitelist is not null) scalarBody["officialWhitelistGroups"] = policy.GroupWhitelist;
                        if (policy.PrivateWhitelist is not null) scalarBody["officialWhitelistPrivates"] = policy.PrivateWhitelist;
                    }
                    else if (platform == Domain.Platforms.PlatformId.QqPrivate)
                    {
                        if (policy.ChatEnabled is bool privateChat) scalarBody["privateChatEnabled"] = privateChat;
                        if (policy.GroupWhitelist is not null) scalarBody["whitelistGroups"] = policy.GroupWhitelist;
                        if (policy.PrivateWhitelist is not null) scalarBody["whitelistPrivates"] = policy.PrivateWhitelist;
                    }
                    else if (platform == Domain.Platforms.PlatformId.Feishu && policy.Enabled is bool feishuEnabled)
                        scalarBody["feishuEnabled"] = feishuEnabled;
                    if (platform is Domain.Platforms.PlatformId.Feishu or Domain.Platforms.PlatformId.Local
                        && (policy.GroupWhitelist is not null || policy.PrivateWhitelist is not null))
                        scalarBody[platform == Domain.Platforms.PlatformId.Local ? "localChannelIds" : "feishuWhitelist"] =
                            !string.IsNullOrWhiteSpace(policy.GroupWhitelist) ? policy.GroupWhitelist : policy.PrivateWhitelist ?? string.Empty;
                }
            }
            var changes = PanelLegacyScalarFields.ParseLegacyFields(scalarBody, policies);
            var plan = PanelLegacySecrets.ParseLegacySecrets(body);
            if (plan.Code == ConfigurationResultCode.Ok) plan.AttachChanges(changes);
            return plan;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return PanelLegacySecrets.RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "invalid_legacy_configuration");
        }
    }
}
