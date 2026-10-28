using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>
/// 预设插件：长效人物画像与记忆沉淀插件。
/// </summary>
public sealed class ProfilesPresetPlugin : IBotPlugin
{
    private IProfileRepository? _profiles;

    public ProfilesPresetPlugin() { }

    public ProfilesPresetPlugin(IProfileRepository profiles)
    {
        _profiles = profiles;
    }

    public string Id => "preset.feature.profiles";
    public string Name => "长效人物画像与记忆";
    public string Version => "1.0.0";
    public string Description => "基于发送者标识构建短期会话栈与长期用户画像（支持事实证据链溯源与人工设定覆盖）。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _profiles ??= context.GetService<IProfileRepository>();
        context.TryBindToggle(PresetToggleBindings.Profiles(), out _);
        context.TryContributeSettings(new SettingsSection(
            "sec-profiles", "人物画像", Id,
            new[]
            {
                new SettingsField("EnableProfileSummary", "启用长效人物画像抽取", "boolean",
                    "根据历史发言沉淀用户性格与关键事实"),
                new SettingsField("ProfileSummaryThreshold", "触发画像抽取的新发言条数", "number",
                    "会话累计多少条新发言后触发抽取")
            }), out _);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
