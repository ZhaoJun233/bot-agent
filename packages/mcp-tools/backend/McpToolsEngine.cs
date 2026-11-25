using System.Text.RegularExpressions;
using BotAgent.Services.Agent;

namespace BotAgent.Packages.McpTools;

/// <summary>Validates MCP server identities and merges configured MCP servers with Python plugin tool sources.</summary>
public sealed class McpToolsEngine : IMcpToolsContribution
{
    public bool ValidateToolSourceName(string serverName)
        => !string.IsNullOrWhiteSpace(serverName)
           && Regex.IsMatch(serverName.Trim(), @"^[A-Za-z0-9_-]{1,32}$");

    public string[] MergeToolSources(IEnumerable<string> configuredServers, IEnumerable<string> pluginPythonSources)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in (configuredServers ?? Array.Empty<string>()).Concat(pluginPythonSources ?? Array.Empty<string>()))
        {
            var name = (source ?? string.Empty).Trim();
            if (ValidateToolSourceName(name) && seen.Add(name))
            {
                merged.Add(name);
            }
        }
        return merged.ToArray();
    }
}
