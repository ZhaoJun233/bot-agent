using BotAgent.Domain.Plugins;
using BotAgent.Services.Voice;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>
/// 预设插件：TTS 语音合成与下发插件。
/// </summary>
public sealed class VoicePresetPlugin : IBotPlugin
{
    private VoiceUseCase? _useCase;

    public VoicePresetPlugin() { }

    public VoicePresetPlugin(VoiceUseCase useCase)
    {
        _useCase = useCase;
    }

    public string Id => "preset.feature.voice";
    public string Name => "TTS 拟真语音合成";
    public string Version => "1.0.0";
    public string Description => "支持对接外部 TTS 服务，模型自主决策语音表达，并具有会话级防连发频控。";
    public PluginCategory Category => PluginCategory.Media;
    public bool IsPreset => true;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _useCase ??= context.GetService<VoiceUseCase>();
        context.TryBindToggle(PresetToggleBindings.Voice(), out _);
        context.TryContributeSettings(new SettingsSection(
            "sec-voice", "语音模块", Id,
            new[]
            {
                new SettingsField("EnableVoice", "启用 TTS 语音合成下发", "boolean",
                    "模型可自主决策输出语音或文字"),
                new SettingsField("TtsServiceUrl", "TTS 服务 HTTP 端点", "text",
                    "外部语音合成引擎接口地址，如 http://tts:5000"),
                new SettingsField("VoiceName", "默认发音人 / 音色代号", "text",
                    "例如 zh_CN-huayan-medium，云端音色随厂商而定")
            }), out _);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
