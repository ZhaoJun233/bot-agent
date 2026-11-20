using BotAgent.Domain.Permissions;
using BotAgent.Domain.Tools;
using BotAgent.Services.Tools;

namespace BotAgent.Packages.WebSearch;

/// <summary>The package owns tool language, argument rules and execution together.</summary>
internal sealed class ResearchTools(ResearchCoordinator research) : IToolContribution
{
    public ToolDefinition[] DescribeTools() =>
    [
        new(new("web.search", ToolCategory.WebRead, "联网搜索", true, "research.actions",
            "search（要联网查的问题，2~120 字）", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.WebSearch, Origin: ToolOrigin.Plugin),
            [new("query", Required: true, MinLength: 2, MaxLength: 120)]),
        new(new("web.read", ToolCategory.WebRead, "读网页正文", true, "research.actions",
            "read（要读的网页 URL）", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.WebSearch, Origin: ToolOrigin.Plugin),
            [new("url", Required: true, MinLength: 8, MaxLength: 2000)], HttpUrlField: "url")
    ];

    public async Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var definition = DescribeTools().SingleOrDefault(tool => tool.Spec.Id == call.ToolId);
        if (definition is null) return ToolOutcome.Failure("unknown_tool", "");
        var verdict = ToolArguments.Validate(definition, call.Arguments);
        if (!verdict.Ok) return ToolOutcome.Failure(verdict.ReasonCode, "");
        if (string.IsNullOrWhiteSpace(sourceKey)) return ToolOutcome.Failure("source_required", "");
        if (call.ToolId == "web.search")
        {
            var result = await research.SearchAsync(sourceKey, call.Arguments["query"]!.GetValue<string>(), ct);
            return result is { HasContent: true } ? ToolOutcome.Success(result.Describe(), $"hits={result.Hits.Count}")
                : ToolOutcome.Failure(result is null ? "research_unavailable" : "search_no_content", "");
        }
        var text = await research.ReadPageAsync(sourceKey, call.Arguments["url"]!.GetValue<string>(), ct);
        return !string.IsNullOrWhiteSpace(text) ? ToolOutcome.Success("页面正文：\n" + text, $"chars={text.Length}")
            : ToolOutcome.Failure("page_no_content", "");
    }
}
