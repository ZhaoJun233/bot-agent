using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Reply;

namespace BotAgent.Packages.Conversation;

/// <summary>Independent system capability package for <c>system.conversation</c>.</summary>
public sealed class ConversationPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private ConversationOrchestratorEngine? _engine;

    public string Id => "system.conversation";
    public string Name => "对话编排与上下文预算";
    public string Version => typeof(ConversationPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "统一汇聚无预设耦合的轮次上下文贡献、节能上下文预算与会话删除广播。";
    public PluginCategory Category => PluginCategory.Management;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _engine = new ConversationOrchestratorEngine();
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IConversationContribution) ? _engine : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _engine = null;
        return Task.CompletedTask;
    }
}
