using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Configuration;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compares existing authorized read projections without retaining or returning their values.</summary>
internal sealed class ConfigurationRuntimeEffectCollector
{
    private readonly ImmutableDictionary<ApplyStep, ApplyState> _steps;
    private readonly Dictionary<FieldAddress, ConfigurationRuntimeEffect> _effects = new();

    internal ConfigurationRuntimeEffectCollector(ImmutableDictionary<ApplyStep, ApplyState> steps) => _steps = steps;

    internal void Observe<T>(ConfigurationFieldId field, FieldEffect effect, T desired, T active)
    {
        // Only ordinary read fields enter this collector; secret slots are projected separately.
        var state = ConfigurationRuntimeTracker.Resolve(field, effect, Equal(desired, active), _steps);
        Add(new(field, null), state);
    }

    internal void ObservePolicy<T>(PolicyTarget target, T desired, T active)
    {
        // Publication precedes whitelist rebuilding. Equal snapshots are not proof that the gate applied them.
        var state = Equal(desired, active)
            ? ConfigurationRuntimeTracker.Resolve(ConfigurationFieldId.PlatformPolicies, FieldEffect.Hot, true, _steps)
            : ConfigurationRuntimeState.RestartRequired;
        Add(new(ConfigurationFieldId.PlatformPolicies, new ConfigurationObjectAddress.Policy(target)), state);
    }

    internal void CompletePolicies()
    {
        var states = _effects.Where(pair => pair.Key.Object is ConfigurationObjectAddress.Policy)
            .Select(pair => pair.Value.State).ToArray();
        var state = states.Contains(ConfigurationRuntimeState.ApplyFailed) ? ConfigurationRuntimeState.ApplyFailed
            : states.Contains(ConfigurationRuntimeState.Unknown) ? ConfigurationRuntimeState.Unknown
            : states.Contains(ConfigurationRuntimeState.RestartRequired) ? ConfigurationRuntimeState.RestartRequired
            : ConfigurationRuntimeTracker.Resolve(ConfigurationFieldId.PlatformPolicies, FieldEffect.Hot, true, _steps);
        Add(new(ConfigurationFieldId.PlatformPolicies, null), state);
    }

    internal ImmutableArray<ConfigurationRuntimeEffect> Build() => _effects.Values.ToImmutableArray();

    private void Add(FieldAddress address, ConfigurationRuntimeState state)
        => _effects[address] = new(address, state, state == ConfigurationRuntimeState.RestartRequired
            ? ConfigurationRestartTarget.BotAgent : null);

    private static bool Equal<T>(T desired, T active)
    {
        if (EqualityComparer<T>.Default.Equals(desired, active)) return true;
        // Collection-bearing rule/view records need structural equality (ImmutableArray equality is referential).
        return JsonNode.DeepEquals(JsonSerializer.SerializeToNode(desired), JsonSerializer.SerializeToNode(active));
    }
}
