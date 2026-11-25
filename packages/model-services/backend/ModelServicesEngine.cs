using BotAgent.Services.Model;

namespace BotAgent.Packages.ModelServices;

/// <summary>Implements model provider routing selection, fast-reply tier switching, and endpoint/timeout budgets.</summary>
public sealed class ModelServicesEngine : IModelServicesContribution
{
    public ModelRouteSelection SelectRoute(string primaryProviderId, string primaryModel, string? fastModel, bool fastReplyEnabled, int timeoutSeconds)
    {
        var useFast = fastReplyEnabled && !string.IsNullOrWhiteSpace(fastModel);
        var chosenModel = useFast ? fastModel!.Trim() : (primaryModel ?? string.Empty).Trim();
        return new ModelRouteSelection(
            string.IsNullOrWhiteSpace(primaryProviderId) ? "primary" : primaryProviderId.Trim(),
            chosenModel,
            useFast,
            ClampTimeoutSeconds(timeoutSeconds));
    }

    public bool IsEndpointAuthorized(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > 2048) return false;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.UserInfo.Length == 0;
    }

    public int ClampTimeoutSeconds(int requestedSeconds)
        => Math.Clamp(requestedSeconds <= 0 ? 120 : requestedSeconds, 1, 300);
}
