using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Model;

/// <summary>Selected route parameters for a model call (fast-reply vs primary, bounded timeout).</summary>
public sealed record ModelRouteSelection(string ProviderId, string ModelName, bool IsFastReply, int TimeoutSeconds);

/// <summary>
/// Generation-bound model routing, endpoint validation, and failover budget contribution
/// owned by <c>system.model-services</c>. Business prompt assembly stays outside model transport.
/// </summary>
public interface IModelServicesContribution : IPluginContribution
{
    ModelRouteSelection SelectRoute(string primaryProviderId, string primaryModel, string? fastModel, bool fastReplyEnabled, int timeoutSeconds);

    bool IsEndpointAuthorized(string? baseUrl);

    int ClampTimeoutSeconds(int requestedSeconds);
}
