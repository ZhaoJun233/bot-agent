namespace BotAgent.Services.Links;

/// <summary>Materialized link descriptions; fetching and caching belong to the research package.</summary>
public interface ILinkPreviewer : BotAgent.Domain.Plugins.IPluginContribution
{
    Task<string?> DescribeAsync(IReadOnlyList<string> urls, CancellationToken ct);
}
