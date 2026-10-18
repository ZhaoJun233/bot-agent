using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>预设插件：长效人物画像与记忆沉淀插件。</summary>
public sealed class ProfilesPresetPlugin : IBotPlugin
{
    public string Id => "preset.feature.profiles";
    public string Name => "长效人物画像与记忆";
    public string Version => "1.0.0";
    public string Description => "基于发送者标识构建短期会话栈与长期用户画像（支持事实证据链溯源与人工设定覆盖）。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
