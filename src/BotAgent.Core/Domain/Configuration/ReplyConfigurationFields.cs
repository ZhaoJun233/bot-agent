namespace BotAgent.Domain.Configuration;

/// <summary>Historical IDs stay decodable but cannot become active configuration again.</summary>
public static class ReplyConfigurationFields
{
    public static bool IsRetired(ConfigurationFieldId field) => field is
        ConfigurationFieldId.AiDesire or ConfigurationFieldId.ReplyPacingEnabled
        or ConfigurationFieldId.EnableAtmosphereDamping or ConfigurationFieldId.EnableParticipationGating
        or ConfigurationFieldId.GroupCooldownSeconds or ConfigurationFieldId.PrivateCooldownSeconds
        or ConfigurationFieldId.IdleFallbackSeconds or ConfigurationFieldId.ProactiveCooldownSeconds
        or ConfigurationFieldId.ParticipationCooldownSeconds or ConfigurationFieldId.ParticipationMaxActiveLifetimeSeconds
        or ConfigurationFieldId.ParticipationMaxConsecutiveReplies or ConfigurationFieldId.ParticipationMaxExitingLifetimeSeconds
        or ConfigurationFieldId.ParticipationProbingMaxReplies;

    public static bool IsRetired(string property)
        => Enum.TryParse<ConfigurationFieldId>(property, ignoreCase: true, out var field) && IsRetired(field);
}
