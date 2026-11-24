using BotAgent.Domain.Permissions;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Tools;
using BotAgent.Services.Net;
using BotAgent.Services.Tools;
using System.Text.Json.Nodes;

/// <summary>Isolated engine fixtures use deterministic research tool definitions with controlled doubles.
/// PluginMarketProbe separately verifies the actual collectible executor and host adoption.</summary>
internal static class ResearchToolFixture
{
    private static readonly ToolDefinition[] Definitions =
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
    public static ChatToolCatalog Catalog { get; } = Create();
    public static ChatToolCatalog Create(IResearchUseCase? research = null)
        => new(Definitions.Select(definition => new Binding(definition, research)));

    public static ChatCapabilitySet Capabilities(bool enableWebSearch, bool enableMusic, bool enableVoice,
        bool enableStickers, bool enablePoke, string? scenario, int policyVersion = 1,
        bool approvalsEnabled = false, bool questionsEnabled = false, ChatToolCatalog? catalog = null)
        => ChatCapabilitySet.FromSwitches(enableWebSearch, enableMusic, enableVoice, enableStickers, enablePoke,
            scenario, policyVersion, approvalsEnabled, questionsEnabled, catalog ?? Catalog);

    public static bool HasContract(string? id) => id is not null && Catalog.FindBinding(id) is not null;
    public static ArgsVerdict Validate(string? id, JsonObject? args)
        => id is not null && Catalog.FindBinding(id) is { } binding
            ? ToolArguments.Validate(binding.Definition, args) : ArgsVerdict.Pass;

    private sealed class Binding(ToolDefinition definition, IResearchUseCase? research) : IToolBinding
    {
        public string ProviderId => "synthetic.research";
        public string Generation { get; } = Guid.NewGuid().ToString("N");
        public ToolDefinition Definition => definition;
        public bool IsReady => research?.IsReady ?? true;
        public T GetContribution<T>() where T : class, IPluginContribution => research as T
            ?? throw new InvalidOperationException("synthetic_contribution_missing");
        public async Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct = default)
        {
            if (research is null) throw new InvalidOperationException("synthetic_executor_missing");
            if (call.ToolId == "web.search")
            {
                var result = await research.SearchAsync(sourceKey, call.Arguments["query"]!.GetValue<string>(), ct);
                return result is { HasContent: true } ? ToolOutcome.Success(result.Describe())
                    : ToolOutcome.Failure(result is null ? "research_unavailable" : "search_no_content", "");
            }
            var text = await research.ReadPageAsync(sourceKey, call.Arguments["url"]!.GetValue<string>(), ct);
            return text is not null ? ToolOutcome.Success(text) : ToolOutcome.Failure("page_no_content", "");
        }
    }
}
