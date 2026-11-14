using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;

internal sealed class LifecycleOfficialIds : IOfficialIdentityMap
{
    private readonly Dictionary<OfficialIdentityKey, long> _aliases = new();
    public int Count => _aliases.Count;
    public long AliasFor(OfficialIdentityKey key)
    {
        if (!_aliases.TryGetValue(key, out var alias)) _aliases.Add(key, alias = Channels.AliasBase + Count + 1);
        return alias;
    }
    public string? OriginalOf(long alias, OfficialIdentityScope scope)
        => _aliases.FirstOrDefault(pair => pair.Value == alias && pair.Key.Scope == scope).Key?.NativeId;
}
