using BotAgent.Domain.Plugins;
using BotAgent.Services.Music;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>
/// 预设插件：群聊音乐解析与分享插件。
/// </summary>
public sealed class MusicPresetPlugin : IBotPlugin
{
    private MusicUseCase? _useCase;

    public MusicPresetPlugin() { }

    public MusicPresetPlugin(MusicUseCase useCase)
    {
        _useCase = useCase;
    }

    public string Id => "preset.feature.music";
    public string Name => "群聊音乐卡片与波形解析";
    public string Version => "1.0.0";
    public string Description => "支持群聊音乐卡片解析、低码率音频波形特征提取与网易云音乐卡片分享。";
    public PluginCategory Category => PluginCategory.Media;
    public bool IsPreset => true;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _useCase ??= context.GetService<MusicUseCase>();
        context.TryBindToggle(PresetToggleBindings.Music(), out _);
        context.TryContributeSettings(new SettingsSection(
            "sec-music", "媒体模块", Id,
            new[]
            {
                new SettingsField("EnableMusic", "启用群聊音乐卡片解析与分享", "boolean",
                    "支持卡片歌词提取与波形分析"),
                new SettingsField("NeteaseBaseUrl", "网易云接口根地址", "text",
                    "默认 https://music.163.com，可指向自建代理")
            }), out _);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
