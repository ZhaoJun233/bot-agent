using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Plugins.Execution;
using BotAgent.Plugins.Net;
using BotAgent.Services.Links;
using BotAgent.Services.Net;

namespace BotAgent.Packages.WebSearch;

public class ResearchPlugin : IBotPlugin, IPluginContributionSource
{
    private ResearchCoordinator? _research;
    private ResearchTools? _tools;
    private ILinkPreviewer? _links;
    public string Id => "preset.feature.websearch";
    public string Name => "联网搜索";
    public string Version => typeof(ResearchPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "联网搜索、读页、链接预览与一次性结果笔记。";
    public PluginCategory Category => PluginCategory.Management;
    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var configuration = context.Services.GetService(typeof(IPluginConfiguration)) as IPluginConfiguration;
        var settings = configuration is null
            ? JsonSerializer.Deserialize<Settings>(context.EnvironmentInfo.GetValueOrDefault("pluginSettings") ?? "{}") : null;
        var clock = context.Services.GetService(typeof(IClock)) as IClock
            ?? throw new InvalidOperationException("research_clock_missing");
        var log = context.Services.GetService(typeof(Action<string>)) as Action<string> ?? (_ => { });
        // Only acquire capabilities here. Provider calls and clock reads occur after publication.
        IWebSearch search;
        if (!string.IsNullOrWhiteSpace(settings?.ProviderId))
        {
            var dependencies = Required<IPluginDependencies>(context, "research_dependencies_missing");
            search = new DependencySearch(dependencies, settings.ProviderId);
        }
        else
        {
            var model = Required<IGroundedTextModel>(context, "research_model_missing");
            var pages = Required<IWebPageTransport>(context, "research_pages_missing");
            var execution = Required<IToolExecution>(context, "research_execution_missing");
            // Retain the explicit legacy accessor for old embeddings. The production host
            // supplies the declared reader and interprets values in this owner instead.
            var options = context.Services.GetService(typeof(Func<ResearchOptions>)) as Func<ResearchOptions>
                ?? (configuration is null ? throw new InvalidOperationException("research_options_missing")
                    : () => ResearchSettings.Interpret(configuration.Read()));
            search = new WebSearchService(model, pages, options, log, execution);
            _links = new LinkPreviewer(pages, options, log);
        }
        _research = new ResearchCoordinator(search, log, () => clock.Now);
        _tools = new ResearchTools(_research);
        return Task.CompletedTask;
    }
    public object? GetContribution(Type contract) => contract == typeof(IResearchUseCase) || contract == typeof(IResearchDiagnostics)
        ? _research : contract == typeof(ILinkPreviewer) ? _links
            : contract == typeof(BotAgent.Services.Tools.IToolContribution) ? _tools : null;
    public Task ShutdownAsync(CancellationToken ct) { _research = null; _links = null; _tools = null; return Task.CompletedTask; }

    private static T Required<T>(PluginContext context, string code) where T : class
        => context.Services.GetService(typeof(T)) as T ?? throw new InvalidOperationException(code);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Settings([property: JsonPropertyName("providerId")] string? ProviderId = null);

    private sealed class DependencySearch(IPluginDependencies dependencies, string providerId) : IWebSearch
    {
        public Task<WebSearchResult?> SearchAsync(string query, CancellationToken ct)
            => dependencies.Get<IWebSearch>(providerId).SearchAsync(query, ct);
        public Task<(string? Text, string? Error)> ReadPageAsync(string url, CancellationToken ct)
            => dependencies.Get<IWebSearch>(providerId).ReadPageAsync(url, ct);
    }
}

/// <summary>Default distribution adds approved diagnostics with a separate capability lifetime.</summary>
public sealed class ResearchControlPlugin : ResearchPlugin, IPluginControlModule, IPluginConfigurationDefinition
{
    public PluginConfigurationDefinition ConfigurationDefinition => ResearchSettings.Definition;
    private ResearchPlugin? _control;
    public Task PrepareControlAsync(PluginContext context, CancellationToken ct)
    {
        _control = new ResearchPlugin();
        return _control.InitializeAsync(context, ct);
    }
    public object? GetControlContribution(Type contract)
        => contract == typeof(IResearchDiagnostics) ? _control?.GetContribution(contract) : null;
    public async Task ShutdownControlAsync(CancellationToken ct)
    {
        var control = _control; _control = null;
        if (control is not null) await control.ShutdownAsync(ct);
    }
}
