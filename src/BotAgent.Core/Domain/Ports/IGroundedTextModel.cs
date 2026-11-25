using System.Collections.Immutable;
using BotAgent.Domain.Plugins;

namespace BotAgent.Domain.Ports;

/// <summary>One grounded text generation against the host-selected model. Callers cannot
/// select a destination or credential. Cancellation propagates; other failures contain safe codes.</summary>
public interface IGroundedTextModel : IPluginContribution
{
    const string Permission = "model.grounded-text";
    Task<GroundedTextResult> GenerateAsync(GroundedTextRequest request, CancellationToken ct);
}

public sealed record GroundedTextRequest(string Prompt, double Temperature, int MaxOutputTokens);
public sealed record GroundedTextSource(string? Title, string? Url);

/// <summary>Materialized provider output, not instructions or fetched source pages. Source filtering
/// and presentation belong to the consumer. ErrorCode never carries a provider body or exception.</summary>
public sealed record GroundedTextResult(string? Text, ImmutableArray<GroundedTextSource> Sources,
    ImmutableArray<string> SearchQueries, string? ErrorCode = null, int? StatusCode = null);
