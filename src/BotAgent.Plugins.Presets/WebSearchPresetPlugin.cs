using BotAgent.Domain.Plugins;
using BotAgent.Services.Links;
using BotAgent.Services.Net;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>
/// 预设插件：联网深度研究与网页链接摘要插件。
/// </summary>
public sealed class WebSearchPresetPlugin : IBotPlugin
{
    private ResearchUseCase? _research;
    private LinkPreviewer? _links;

    public WebSearchPresetPlugin() { }

    public WebSearchPresetPlugin(ResearchUseCase research, LinkPreviewer? links)
    {
        _research = research;
        _links = links;
    }

    public string Id => "preset.feature.websearch";
    public string Name => "联网研究与链接摘要";
    public string Version => "1.0.0";
    public string Description => "支持受控联网搜索抓取、群聊外部网页链接实时抓取与内容智能提要。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _research ??= context.GetService<ResearchUseCase>();
        _links ??= context.GetService<LinkPreviewer>();
        context.TryBindToggle(PresetToggleBindings.WebSearch(), out _);
        context.TryContributeSettings(new SettingsSection(
            "sec-research", "联网模块", Id,
            new[]
            {
                new SettingsField("EnableWebSearch", "启用联网深度研究与搜索", "boolean",
                    "允许模型通过搜索引擎获取时效知识"),
                new SettingsField("WebSearchSources", "搜索源模板 (每行一条 name|url，{q} 为查询词)", "textarea",
                    "SearXNG / MediaWiki / 通用 HTML 兜底搜索源"),
                new SettingsField("EnableLinkPreview", "启用外部网页链接智能提要", "boolean",
                    "自动抓取群友分享链接并提取正文摘要")
            }), out _);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
