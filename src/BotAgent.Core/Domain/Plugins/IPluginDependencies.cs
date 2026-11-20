namespace BotAgent.Domain.Plugins;

/// <summary>A host-issued capability for this activation's exact direct dependencies.
/// Retaining it never permits following a replacement consumer or provider.</summary>
public interface IPluginDependencies
{
    T Get<T>(string providerId) where T : class, IPluginContribution;
}
