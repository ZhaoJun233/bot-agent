using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Tools;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Tools;

namespace BotAgent.Services.Reply;


/// <summary>
/// 聊天路径的**兼容壳**（#115）：真正的判定在 <see cref="ToolVisibility" />，这里只保留原有的
/// 构造签名与 <see cref="Allows" /> / <see cref="Render" /> 两个入口，于是调用点一行都不用改。
/// 新代码请直接依赖 <see cref="ToolVisibility" />；本类存在的意义是“改判定时只有一处要改”。
/// </summary>
public sealed class ReplyToolAvailability(
    PluginGate? plugins = null,
    IPlatformPolicyResolver? policies = null,
    Func<bool>? isDisposed = null,
    Func<bool>? webReady = null,
    Func<ChatToolCatalog>? catalog = null)
{
    private readonly ToolVisibility _visibility = new(plugins, policies, isDisposed, webReady, catalog);

    public bool IsCurrent => _visibility.IsCurrent;

    /// <summary>这一轮这个工具是否可见（判定逻辑见 <see cref="ToolVisibility.Allows" />）。</summary>
    public bool Allows(AppSettings snapshot, EffectivePlatformPolicy? pinned, string toolId,
        ChatToolCatalog? pinnedTools = null, EffectiveConversationSettings? conversation = null)
        => _visibility.Allows(snapshot, pinned, toolId, pinnedTools, conversation);

    public string Render(AppSettings snapshot, EffectivePlatformPolicy pinned, ChatCapabilitySet caps,
        EffectiveConversationSettings? conversation = null)
        => ToolPromptText.Render(caps.Catalog.Specs.Where(spec => Allows(snapshot, pinned, spec.Id, caps.Catalog, conversation)).ToArray(), caps.Policy);
}
