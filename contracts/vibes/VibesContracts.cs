using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Conversations;

/// <summary>
/// Generation-bound conversation atmosphere tracking and sober-state dampening owned by <c>preset.feature.vibes</c>.
/// </summary>
public interface IVibesContribution : IPluginContribution
{
    void Remember(string sourceKey, string? vibe, string? note, DateTimeOffset now);

    string Current(string sourceKey, DateTimeOffset now);

    string? Hint(string sourceKey, DateTimeOffset now);

    bool IsSober(string sourceKey, DateTimeOffset now);

    void ClearConversation(string sourceKey);
}
