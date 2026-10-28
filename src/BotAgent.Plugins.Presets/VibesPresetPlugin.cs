using BotAgent.Domain.Plugins;
using BotAgent.Services.Participation;
using BotAgent.Services.Conversations;

namespace BotAgent.Services.Plugins.Presets;
/// <summary>
/// 预设插件：群聊氛围感知与疲劳阻尼插件。
/// </summary>
public sealed class VibesPresetPlugin : IBotPlugin
{
    private VibeTracker? _vibes;
    private ParticipationUseCase? _participation;

    public VibesPresetPlugin() { }

    public VibesPresetPlugin(VibeTracker vibes, ParticipationUseCase participation)
    {
        _vibes = vibes;
        _participation = participation;
    }

    public string Id => "preset.feature.vibes";
    public string Name => "群聊氛围感知与发言欲望阻尼";
    public string Version => "1.0.0";
    public string Description => "动态监测群消息密度与发言热度，连续发言后自适应非线性衰减回复欲望并进入强制降温冷却。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _vibes ??= context.GetService<VibeTracker>();
        _participation ??= context.GetService<ParticipationUseCase>();
        context.TryBindToggle(PresetToggleBindings.Vibes(), out _);
        context.TryContributeSettings(new SettingsSection(
            "sec-vibes", "氛围阻尼", Id,
            new[]
            {
                new SettingsField("EnableAtmosphereDamping", "启用群聊氛围与疲劳阻尼", "boolean",
                    "连续发言后自适应非线性降温防刷屏"),
                new SettingsField("IdleFallbackSeconds", "静默兜底判定时长 (秒)", "number",
                    "群聊安静达到此时长后触发主动开口"),
                new SettingsField("ProactiveQuietSeconds", "主动开口安静时长基准 (秒)", "number",
                    "群聊安静达到此时长后方可触发主动开口")
            }), out _);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
