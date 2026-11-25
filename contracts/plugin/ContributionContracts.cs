namespace BotAgent.Domain.Plugins;

/// <summary>Marks a shared, typed contribution contract. Implementations stay private to a package.</summary>
public interface IPluginContribution;

/// <summary>Marks an explicitly approved contribution independent of business enablement.</summary>
public interface IPluginControlContribution : IPluginContribution { }
