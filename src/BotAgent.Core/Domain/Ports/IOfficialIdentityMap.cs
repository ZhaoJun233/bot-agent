using BotAgent.Domain.Qq;

namespace BotAgent.Domain.Ports;

/// <summary>Runtime official identity operations. Legacy attribution belongs to the offline migration tool.</summary>
public interface IOfficialIdentityMap
{
    int Count { get; }
    long AliasFor(OfficialIdentityKey identity);
    string? OriginalOf(long alias, OfficialIdentityScope expectedScope);
    /// <summary>Resolve the complete identity only in the exact expected scope; old providers prove no index.</summary>
    OfficialIdentityKey? IdentityOf(long alias, OfficialIdentityScope expectedScope)
        => OriginalOf(alias, expectedScope) is { } native ? new(expectedScope, native) : null;
}
