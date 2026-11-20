using System.Collections.Immutable;
using System.Text.RegularExpressions;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Tools;
using BotAgent.Services.Tools;

namespace BotAgent.Services.Plugins;

public sealed partial class PluginManager
{
    /// <summary>A fixed declaration and executor generation. Reading a stale description grants no execution rights.</summary>
    public sealed class ToolBinding : IToolBinding
    {
        private readonly PluginManager _owner;
        private readonly Entry _entry;
        internal ToolBinding(PluginManager owner, Entry entry, ToolDefinition definition)
        { _owner = owner; _entry = entry; Definition = definition; }
        public string ProviderId => _entry.Info.Id;
        public string Generation => _entry.ToolGeneration;
        public ToolDefinition Definition { get; }
        public bool IsReady => new InvocationGate(_owner, _entry).IsReady;
        /// <summary>Checks arguments and generation lifetime. Policy, approval and budget admission remain with the caller.</summary>
        public Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct = default)
            => _owner.ExecuteToolAsync(_entry, Definition, sourceKey, call, ct);
        public T GetContribution<T>() where T : class, IPluginContribution
            => _owner.Contribution<T>(_entry.Info.Id, null, fixedConsumer: _entry);
    }

    public ChatToolCatalog GetToolCatalog() => new(GetTools());

    /// <summary>One locked snapshot; every returned entry keeps its original generation.</summary>
    public ToolBinding[] GetTools()
    {
        lock (_sync)
            return _plugins.Values.Where(entry => Ready(entry, new()))
                .SelectMany(entry => entry.Tools.Select(definition => new ToolBinding(this, entry, definition))).ToArray();
    }

    private void PrepareTools(Entry entry)
    {
        _callbackDepth.Value++;
        try
        {
            var target = entry.Plugin is IPluginContributionSource source
                ? source.GetContribution(typeof(IToolContribution)) as IToolContribution : entry.Plugin as IToolContribution;
            if (target is null) return;
            var definitions = target.DescribeTools() ?? throw new InvalidOperationException("plugin_tools_invalid");
            if (definitions.Length > 256) throw new InvalidOperationException("plugin_tools_invalid");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var captured = new List<ToolDefinition>();
            foreach (var definition in definitions)
            {
                ValidateToolDefinition(definition);
                if (!ids.Add(definition.Spec.Id)) throw new InvalidOperationException("plugin_tool_conflict");
                // Copy the array storage too: ImmutableArray may wrap storage supplied by package code.
                captured.Add(definition with { Spec = definition.Spec with { Origin = ToolOrigin.Plugin },
                    Fields = definition.Fields.ToArray().ToImmutableArray() });
            }
            entry.Tools = captured.ToArray();
            entry.ToolGeneration = Guid.NewGuid().ToString("N");
            entry.ToolTarget = target;
        }
        finally { _callbackDepth.Value--; }
    }

    private static void ValidateToolDefinition(ToolDefinition definition)
    {
        static bool Identifier(string? value) => value is { Length: > 0 and <= 128 }
            && Regex.IsMatch(value, @"\A[a-zA-Z0-9_][a-zA-Z0-9_.-]*\z", RegexOptions.CultureInvariant);
        if (definition?.Spec is not { } spec || !Identifier(spec.Id) || !Identifier(spec.Executor)
            || !Enum.IsDefined(spec.Category) || !Enum.IsDefined(spec.Default) || !Enum.IsDefined(spec.Origin)
            || string.IsNullOrWhiteSpace(spec.Summary) || spec.Summary.Length > 4096
            || spec.Parameters is null || spec.Parameters.Length > 8192
            || spec.FeatureKey is null || spec.FeatureKey.Length > 128
            || definition.Fields.IsDefault || definition.Fields.Length > 64)
            throw new InvalidOperationException("plugin_tools_invalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in definition.Fields)
            if (field is null || !Identifier(field.Name) || !names.Add(field.Name) || field.MinLength < 0
                || field.MaxLength < 0 || field.MaxLength > 0 && field.MinLength > field.MaxLength)
                throw new InvalidOperationException("plugin_tools_invalid");
        if (definition.HttpUrlField is { } urlField && !definition.Fields.Any(field => field.Name == urlField && field.Required))
            throw new InvalidOperationException("plugin_tools_invalid");
    }

    // Caller owns _sync. Disabled owners also reserve their names, so re-enable cannot create duplicates.
    private void CheckToolConflicts(Entry candidate)
    {
        var claimed = _plugins.Values.Where(entry => entry.Info.Id != candidate.Info.Id)
            .SelectMany(entry => entry.Tools).Select(definition => definition.Spec.Id).ToHashSet(StringComparer.Ordinal);
        claimed.UnionWith(ToolDirectory.Builtin.Specs.Select(spec => spec.Id));
        var executors = _plugins.Values.Where(entry => entry.Info.Id != candidate.Info.Id)
            .SelectMany(entry => entry.Tools).Select(definition => definition.Spec.Executor).ToHashSet(StringComparer.Ordinal);
        executors.UnionWith(ToolDirectory.Builtin.Executors.All.Select(executor => executor.Id));
        if (candidate.Tools.Any(definition => claimed.Contains(definition.Spec.Id) || executors.Contains(definition.Spec.Executor)))
            throw new InvalidOperationException("plugin_tool_conflict");
    }

    private async Task<ToolOutcome> ExecuteToolAsync(Entry entry, ToolDefinition definition, string sourceKey,
        ToolCall call, CancellationToken ct)
    {
        using var lease = new InvocationGate(this, entry).TryAcquire()
            ?? throw new InvalidOperationException("plugin_contribution_unavailable");
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(call);
        if (call.ToolId != definition.Spec.Id) return ToolOutcome.Failure("unknown_tool", "");
        var captured = call with { Arguments = (System.Text.Json.Nodes.JsonObject?)call.Arguments?.DeepClone() ?? new() };
        var verdict = ToolArguments.Validate(definition, captured.Arguments);
        if (!verdict.Ok) return ToolOutcome.Failure(verdict.ReasonCode, "");
        _callbackDepth.Value++;
        try { return await entry.ToolTarget!.ExecuteAsync(sourceKey, captured, ct).ConfigureAwait(false); }
        finally { _callbackDepth.Value--; }
    }
}
