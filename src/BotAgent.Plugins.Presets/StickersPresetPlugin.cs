using BotAgent.Domain.Plugins;
using BotAgent.Services.Stickers;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>
/// 预设插件：表情包感知、审核与协同插件。
/// </summary>
public sealed class StickersPresetPlugin : IBotPlugin
{
    private StickerService? _service;

    public StickersPresetPlugin() { }

    public StickersPresetPlugin(StickerService service)
    {
        _service = service;
    }

    public string Id => "preset.feature.stickers";
    public string Name => "表情包协同与安全库";
    public string Version => "1.0.0";
    public string Description => "自动学习收录群聊表情包，提取标签并根据对话语境协同发送，内置安全魔数校验。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _service ??= context.GetService<StickerService>();
        context.TryBindToggle(PresetToggleBindings.Stickers(), out _);
        // 面板字段由插件自述：本插件被禁用或不存在时，这一整组不会出现在面板上
        context.TryContributeSettings(new SettingsSection(
            "sec-stickers", "表情池", Id,
            new[]
            {
                new SettingsField("EnableStickers", "启用表情包感知与协同库", "boolean",
                    "自动收集审核并根据语境协同回复表情包"),
                new SettingsField("StickerLibraryMax", "表情包库存储容量上限 (张)", "number",
                    "超出后按使用频次与活跃度淘汰"),
                new SettingsField("StickerCandidates", "每轮对话候选表情检索数", "number",
                    "语境检索候选供给模型参考的数量")
            }), out _);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
