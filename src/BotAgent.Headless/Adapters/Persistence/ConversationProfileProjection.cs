extern alias StorageModule;

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Services.Agent;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 会话级可组合档案的独立协作器（非 partial SettingsStore）：
/// 承接事务内逐行编辑规划、严格 Overview/Editable 脱敏投影与运行时热生效态采集。
/// </summary>
internal static class ConversationProfileProjection
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal readonly record struct EditPlan(
        bool Changed,
        ImmutableArray<ConversationProfile> Entries,
        ImmutableArray<FieldError> Errors,
        ConfigurationResultCode Code);

    internal static ImmutableArray<ConversationProfileView> ProjectViews(
        ImmutableArray<ConversationProfile> profiles,
        ConfigurationReadKind kind,
        bool maskSensitive)
    {
        if (profiles.IsDefaultOrEmpty) return ImmutableArray<ConversationProfileView>.Empty;
        var builder = ImmutableArray.CreateBuilder<ConversationProfileView>(profiles.Length);
        foreach (var profile in profiles)
        {
            if (kind == ConfigurationReadKind.Overview)
            {
                var maskedKey = MaskConversationKey(profile.Key.ConversationKey, maskSensitive: true);
                var maskedLabel = string.IsNullOrEmpty(profile.DisplayLabel)
                    ? string.Empty : AgentMask.Shorten(profile.DisplayLabel);
                builder.Add(new ConversationProfileView(
                    new ConversationProfileKey(profile.Key.Driver, maskedKey),
                    maskedLabel,
                    maskedKey,
                    FieldVisibility.Hidden));
            }
            else
            {
                var displayLabel = maskSensitive && !string.IsNullOrEmpty(profile.DisplayLabel)
                    ? AgentMask.Shorten(profile.DisplayLabel) : profile.DisplayLabel;
                var displayKey = MaskConversationKey(profile.Key.ConversationKey, maskSensitive);
                builder.Add(new ConversationProfileView(
                    profile.Key,
                    displayLabel,
                    displayKey,
                    FieldVisibility.Editable,
                    profile.DisplayLabel,
                    profile.Style,
                    profile.Pacing,
                    profile.Capabilities));
            }
        }
        return builder.ToImmutable();
    }

    internal static EditPlan PlanEdits(
        SqliteConnection connection,
        RegisteredConfigurationChanges? registered,
        ImmutableArray<DriverKey> declaredDrivers,
        StorageConfiguration.ConfigurationStorageProfile profile)
    {
        var edits = registered?.ConversationProfiles ?? ImmutableArray<ConversationProfileEdit>.Empty;
        if (edits.IsDefaultOrEmpty)
            return new(false, ImmutableArray<ConversationProfile>.Empty, ImmutableArray<FieldError>.Empty, ConfigurationResultCode.Ok);

        if (profile == StorageConfiguration.ConfigurationStorageProfile.Legacy)
        {
            return new(false, ImmutableArray<ConversationProfile>.Empty,
                ImmutableArray.Create(new FieldError(new(ConfigurationFieldId.ConversationProfiles, null), "legacy_conversation_profile_unsupported")),
                ConfigurationResultCode.ValidationFailed);
        }

        try
        {
            var original = StorageConfiguration.ConversationProfileStorageBridge.Load(connection);
            var map = original.ToDictionary(item => item.Key);
            foreach (var edit in edits)
            {
                if (edit.Operation == ConversationProfileEditOperation.Delete)
                {
                    map.Remove(edit.Key);
                }
                else if (edit.Profile is { } next)
                {
                    map[edit.Key] = next;
                }
            }
            var final = map.Values
                .OrderBy(item => ConversationProfileValidator.KeyOrder(item.Key), StringComparer.Ordinal)
                .ToImmutableArray();
            var errors = ConversationProfileValidator.ValidateCollection(final, declaredDrivers);
            if (!errors.IsEmpty)
                return new(false, final, errors.Take(32).ToImmutableArray(), ConfigurationResultCode.ValidationFailed);

            var nextEnvelope = StorageConfiguration.ConversationProfileStorageBridge.SerializeEnvelope(final);
            if (StrictUtf8.GetByteCount(nextEnvelope) > ConversationProfileValidator.MaxJsonBytes)
            {
                return new(false, final,
                    ImmutableArray.Create(new FieldError(new(ConfigurationFieldId.ConversationProfiles, null), "conversation_profile_payload_too_large")),
                    ConfigurationResultCode.ValidationFailed);
            }
            var oldEnvelope = StorageConfiguration.ConversationProfileStorageBridge.SerializeEnvelope(original);
            return new(oldEnvelope != nextEnvelope, final, ImmutableArray<FieldError>.Empty, ConfigurationResultCode.Ok);
        }
        catch (JsonException)
        {
            return new(false, ImmutableArray<ConversationProfile>.Empty,
                ImmutableArray.Create(new FieldError(new(ConfigurationFieldId.ConversationProfiles, null), "conversation_profile_needs_review")),
                ConfigurationResultCode.LegacyNeedsReview);
        }
    }

    internal static void ObserveRuntimeEffects(
        ImmutableArray<ConversationProfile> desired,
        ImmutableArray<ConversationProfile> active,
        ConfigurationReadKind kind,
        ConfigurationRuntimeEffectCollector collector)
    {
        var desiredMap = desired.IsDefaultOrEmpty
            ? new Dictionary<ConversationProfileKey, ConversationProfile>()
            : desired.ToDictionary(item => item.Key);
        var activeMap = active.IsDefaultOrEmpty
            ? new Dictionary<ConversationProfileKey, ConversationProfile>()
            : active.ToDictionary(item => item.Key);
        if (desiredMap.Count == 0 && activeMap.Count == 0) return;

        var keys = desiredMap.Keys.Concat(activeMap.Keys).Distinct()
            .OrderBy(ConversationProfileValidator.KeyOrder, StringComparer.Ordinal);
        if (kind == ConfigurationReadKind.Overview)
        {
            // Overview 严禁在 runtimeEffects 中暴露未脱敏的原始会话键：仅输出聚合生效态。
            collector.ObserveConversationProfilesOverview(keys.Select(key =>
            {
                desiredMap.TryGetValue(key, out var d);
                activeMap.TryGetValue(key, out var a);
                return (d, a);
            }));
            return;
        }
        foreach (var key in keys)
        {
            desiredMap.TryGetValue(key, out var d);
            activeMap.TryGetValue(key, out var a);
            collector.ObserveConversationProfile(key, d, a);
        }
        collector.CompleteConversationProfiles();
    }

    internal static string MaskConversationKey(string conversationKey, bool maskSensitive)
    {
        if (!maskSensitive || string.IsNullOrEmpty(conversationKey)) return conversationKey;
        var colon = conversationKey.LastIndexOf(':');
        if (colon < 0 || colon == conversationKey.Length - 1) return AgentMask.Shorten(conversationKey);
        return string.Concat(conversationKey[..(colon + 1)], AgentMask.Shorten(conversationKey[(colon + 1)..]));
    }
}
