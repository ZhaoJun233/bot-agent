using BotAgent.Domain.Configuration;

namespace BotAgent.Adapters.Panel.Zba;

internal static class ZbaConfigurationEvents
{
    internal static void Append(ZbaEventBuffer events, ConfigurationSaveResult result, DateTimeOffset now)
    {
        if (result.Code is ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges)
        {
            events.Append("configuration.saved", null, State(result),
                result.CurrentVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result.Receipt?.ApplyState.ToString().ToLowerInvariant(), now);
        }
        else events.AppendError("configuration", result.Code.ToString(), now);
    }

    private static string State(ConfigurationSaveResult result)
    {
        if (result.Code == ConfigurationResultCode.NoChanges) return "no_changes";
        return result.Receipt?.ApplyState switch
        {
            ApplyState.PartiallyApplied => "partially_applied",
            ApplyState.RestartRequired => "restart_required",
            ApplyState.Failed => "failed",
            ApplyState.Unknown => "unknown",
            ApplyState.Applied => "applied",
            _ => "not_started"
        };
    }
}
