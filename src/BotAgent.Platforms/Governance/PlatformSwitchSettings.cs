using BotAgent.Domain.Platforms;

namespace BotAgent.Services.Platforms;

/// <summary>Versioned compatibility bridge for platform and chat switches.</summary>
public static class PlatformSwitchSettings
{
    public static (bool Enabled, bool ChatEnabled) Read(PlatformOptions settings, string platform, string account)
    {
        platform = PlatformId.Normalize(platform);
        if (platform is not (PlatformId.QqPrivate or PlatformId.QqOfficial or PlatformId.Feishu or PlatformId.Local))
            return (false, false);

        var policy = Find(settings, platform, account);
        var legacy = Legacy(settings, platform);
        return settings.PlatformSwitchSchemaVersion < 1
            ? ((policy?.Enabled ?? true) && legacy.Enabled, (policy?.ChatEnabled ?? true) && legacy.ChatEnabled)
            : (policy?.Enabled ?? legacy.Enabled, policy?.ChatEnabled ?? legacy.ChatEnabled);
    }

    public static void Normalize(PlatformOptions settings)
    {
        settings.PlatformPolicies ??= new();
        if (settings.PlatformSwitchSchemaVersion < 1)
        {
            // Calculate all intersections before mirroring any legacy fields.
            foreach (var policy in settings.PlatformPolicies.Where(p => p is not null))
            {
                var switches = Read(settings, policy.PlatformId, policy.AccountScope);
                policy.Enabled = switches.Enabled;
                policy.ChatEnabled = switches.ChatEnabled;
            }
            settings.PlatformSwitchSchemaVersion = 1;
        }
        SynchronizeLegacy(settings);
    }

    public static void SynchronizeLegacy(PlatformOptions settings)
    {
        var official = Find(settings, PlatformId.QqOfficial, AccountScope.Legacy);
        if (official?.Enabled is bool officialEnabled) settings.OfficialEnabled = officialEnabled;
        if (official?.ChatEnabled is bool officialChat) settings.OfficialChatEnabled = officialChat;
        var qq = Find(settings, PlatformId.QqPrivate, AccountScope.Legacy);
        if (qq?.ChatEnabled is bool privateChat) settings.PrivateChatEnabled = privateChat;
        var feishu = Find(settings, PlatformId.Feishu, AccountScope.Default);
        if (feishu?.Enabled is bool feishuEnabled) settings.FeishuEnabled = feishuEnabled;
    }

    public static void ApplyLegacy(PlatformOptions settings, string platform, string account, bool? enabled, bool? chatEnabled)
    {
        var policy = Find(settings, platform, account);
        if (policy is null) return;
        if (enabled is bool e) policy.Enabled = e;
        if (chatEnabled is bool c) policy.ChatEnabled = c;
    }

    public static List<PlatformPolicySettings> EditablePolicies(PlatformOptions settings)
    {
        var policies = (settings.PlatformPolicies ?? new()).Where(p => p is not null).Select(p => p.Clone()).ToList();
        foreach (var policy in policies)
        {
            var switches = Read(settings, policy.PlatformId, policy.AccountScope);
            policy.Enabled = switches.Enabled;
            policy.ChatEnabled = switches.ChatEnabled;
        }
        foreach (var (platform, account) in new[]
        {
            (PlatformId.QqPrivate, AccountScope.Legacy), (PlatformId.QqOfficial, AccountScope.Legacy),
            (PlatformId.Feishu, AccountScope.Default), (PlatformId.Local, AccountScope.Legacy),
        })
        {
            var policy = policies.FirstOrDefault(p => PlatformId.Normalize(p.PlatformId) == platform
                && string.Equals(string.IsNullOrWhiteSpace(p.AccountScope) ? AccountScope.Default : p.AccountScope,
                    account, StringComparison.OrdinalIgnoreCase));
            if (policy is null)
            {
                policy = new PlatformPolicySettings
                {
                    PlatformId = platform,
                    AccountScope = account,
                    InheritActionAllowlist = true,
                    GroupWhitelist = platform switch
                    {
                        PlatformId.QqPrivate => !string.IsNullOrWhiteSpace(settings.WhitelistGroups) ? settings.WhitelistGroups : settings.MessageWhitelist,
                        PlatformId.QqOfficial => settings.OfficialWhitelistGroups,
                        PlatformId.Feishu => settings.FeishuWhitelist,
                        PlatformId.Local => settings.LocalChannelIds,
                        _ => string.Empty,
                    },
                    PrivateWhitelist = platform switch
                    {
                        PlatformId.QqPrivate => !string.IsNullOrWhiteSpace(settings.WhitelistPrivates) ? settings.WhitelistPrivates : settings.MessageWhitelist,
                        PlatformId.QqOfficial => settings.OfficialWhitelistPrivates,
                        _ => string.Empty,
                    },
                };
                policies.Add(policy);
            }
            policy.PlatformId = platform;
            policy.AccountScope = account;
            var switches = Read(settings, platform, account);
            policy.Enabled = switches.Enabled;
            policy.ChatEnabled = switches.ChatEnabled;
        }
        return policies;
    }

    private static PlatformPolicySettings? Find(PlatformOptions settings, string platform, string account)
        => (settings.PlatformPolicies ?? new()).FirstOrDefault(p => p is not null
            && string.Equals(PlatformId.Normalize(p.PlatformId), platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(string.IsNullOrWhiteSpace(p.AccountScope) ? AccountScope.Default : p.AccountScope,
                string.IsNullOrWhiteSpace(account) ? AccountScope.Default : account, StringComparison.OrdinalIgnoreCase));

    private static (bool Enabled, bool ChatEnabled) Legacy(PlatformOptions settings, string platform)
        => platform switch
        {
            PlatformId.QqPrivate => (true, settings.PrivateChatEnabled),
            PlatformId.QqOfficial => (settings.OfficialEnabled, settings.OfficialChatEnabled),
            PlatformId.Feishu => (settings.FeishuEnabled, settings.FeishuEnabled),
            PlatformId.Local => (!string.IsNullOrWhiteSpace(settings.LocalChannelIds), !string.IsNullOrWhiteSpace(settings.LocalChannelIds)),
            _ => (false, false),
        };
}
