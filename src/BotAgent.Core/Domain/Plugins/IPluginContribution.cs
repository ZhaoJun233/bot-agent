using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(BotAgent.Domain.Plugins.IPluginContribution))]
[assembly: TypeForwardedTo(typeof(BotAgent.Domain.Plugins.IPluginControlContribution))]

namespace BotAgent.Domain.Plugins;

/// <summary>Loader-to-registry bridge. Consumers use generation-bound registry proxies, never this raw bridge.</summary>
public interface IPluginContributionSource
{
    object? GetContribution(Type contract);
}
