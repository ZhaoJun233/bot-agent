namespace BotAgent.Services.Plugins;

/// <summary>The desired package configuration is retained, but adopting it requires a host restart.</summary>
public sealed class PluginRestartRequiredException()
    : InvalidOperationException("plugin_contract_cold_start_required");
