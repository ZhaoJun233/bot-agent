using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BotAgent.Domain.Mcp;
using BotAgent.Mcp;

namespace BotAgent.Adapters.Plugins;

/// <summary>Only enabled package generations publish here; lifecycle owns their disposal.</summary>
public sealed class PluginPythonSources : IMcpToolSource
{
    private sealed record Entry(PythonWorkerToolSource Source, string Owner);
    private readonly ConcurrentDictionary<string, Entry> _sources = new(StringComparer.Ordinal);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // lifecycle owns workers
    public bool Enabled => !_sources.IsEmpty;
    public McpConnectionState State => Enabled ? McpConnectionState.Ready : McpConnectionState.Disabled;
    internal void Attach(string id, PythonWorkerToolSource source)
    {
        if (!_sources.TryAdd(id, new(source, Guid.NewGuid().ToString("N")))) throw new InvalidOperationException("Python generation already attached.");
    }
    internal void Detach(string id, PythonWorkerToolSource source)
    {
        if (_sources.TryGetValue(id, out var entry) && ReferenceEquals(entry.Source, source))
            ((ICollection<KeyValuePair<string, Entry>>)_sources).Remove(new(id, entry));
    }
    public async Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct)
    {
        var tools = new List<McpToolInfo>();
        foreach (var entry in _sources.Values)
        {
            var found = await entry.Source.DiscoverAsync(ct).ConfigureAwait(false);
            tools.AddRange(found.Select(tool => tool with { IsLocalProcess = true, SourceGeneration = entry.Owner + ":" + tool.SourceGeneration }));
        }
        return tools;
    }
    private static string Generation(Entry entry) => entry.Owner + ":" + entry.Source.Generation;
    internal static string SourceId(string id) => "python_" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id))).ToLowerInvariant()[..24];
    private Entry? Find(string name) => _sources.FirstOrDefault(pair => name.StartsWith("mcp." + SourceId(pair.Key) + ".", StringComparison.Ordinal)).Value;
    public bool IsToolCurrent(McpToolInfo tool) => Find(tool.QualifiedName) is { } entry &&
        entry.Source.State == McpConnectionState.Ready && tool.SourceGeneration == Generation(entry);
    public Task<McpCallResult> CallWithLeaseAsync(McpToolInfo tool, JsonObject arguments, CancellationToken ct)
    {
        var entry = Find(tool.QualifiedName);
        // Validate against the captured source, never a second lookup that could return a replacement.
        if (entry is null || entry.Source.State != McpConnectionState.Ready || tool.SourceGeneration != Generation(entry))
            return Task.FromResult(new McpCallResult(false, "mcp_source_changed", null));
        if (!long.TryParse(tool.SourceGeneration?.Split(':').Last(), out var generation))
            return Task.FromResult(new McpCallResult(false, "mcp_source_changed", null));
        return entry.Source.CallWithGenerationAsync(tool.QualifiedName, arguments, generation, ct);
    }
    public Task<McpCallResult> CallAsync(string name, JsonObject arguments, CancellationToken ct)
        => Task.FromResult(new McpCallResult(false, "mcp_source_lease_required", null));
}

internal sealed class CompositePackageToolSource(IMcpToolSource remote, PluginPythonSources local) : IMcpToolSource
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // composition owns sources
    public bool Enabled => remote.Enabled || local.Enabled;
    public McpConnectionState State => local.Enabled ? local.State : remote.State;
    public async Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct)
    {
        var remoteTools = await remote.DiscoverAsync(ct).ConfigureAwait(false);
        var localTools = await local.DiscoverAsync(ct).ConfigureAwait(false);
        return remoteTools.Where(tool => !tool.QualifiedName.StartsWith("mcp.python_", StringComparison.Ordinal)).Concat(localTools).ToArray();
    }
    public bool IsToolCurrent(McpToolInfo tool) => tool.IsLocalProcess ? local.IsToolCurrent(tool) : remote.IsToolCurrent(tool);
    public Task<McpCallResult> CallWithLeaseAsync(McpToolInfo tool, JsonObject arguments, CancellationToken ct)
        => tool.IsLocalProcess ? local.CallWithLeaseAsync(tool, arguments, ct) : remote.CallWithLeaseAsync(tool, arguments, ct);
    public Task<McpCallResult> CallAsync(string name, JsonObject arguments, CancellationToken ct)
        => name.StartsWith("mcp.python_", StringComparison.Ordinal) ? local.CallAsync(name, arguments, ct) : remote.CallAsync(name, arguments, ct);
}
